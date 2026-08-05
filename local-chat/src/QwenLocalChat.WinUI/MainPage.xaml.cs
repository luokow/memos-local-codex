using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QwenLocalChat.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;

namespace QwenLocalChat_WinUI;

public sealed partial class MainPage : Page
{
    private static readonly Color PrimaryText = ColorHelper.FromArgb(255, 244, 246, 248);
    private static readonly Color SecondaryText = ColorHelper.FromArgb(255, 166, 175, 188);
    private static readonly Color MutedText = ColorHelper.FromArgb(255, 112, 122, 136);
    private static readonly Color WarningText = ColorHelper.FromArgb(255, 231, 204, 136);
    private static readonly Color ErrorText = ColorHelper.FromArgb(255, 255, 107, 107);

    private readonly List<ChatMessage> _history = [];
    private readonly InputHistoryNavigator _inputHistory = new();
    private readonly MemoryWriteQueue _memoryQueue = new();
    private readonly SemaphoreSlim _memosGate = new(1, 1);
    private readonly ConversationSessionWorkspace _sessions = new();
    private bool _suppressSessionPicker;
    private AppPaths? _paths;
    private SettingsStore? _settingsStore;
    private ModelServiceConfigStore? _modelConfigStore;
    private ModelServiceConfig? _modelConfig;
    private ConversationSessionStore? _sessionStore;
    private QwenServiceManager? _modelManager;
    private QwenChatClient? _chatClient;
    private MemosStdioClient? _memos;
    private MarkdownChatLog? _chatLog;
    private LocalChatSettings _settings = LocalChatSettings.SafeDefaults;
    private LocalChatSettings _activeModelSettings = LocalChatSettings.SafeDefaults;
    private bool _loadingSettings;
    private bool _busy;
    private bool _initialized;
    private bool _disposed;
    private string? _lastFinishReason;
    private string _initializationStage = "页面加载";
    /// <summary>
    /// In-flight generations keyed by session id. Count is capped by the model
    /// <c>parallel_slots</c> setting so multi-session concurrent replies stay isolated.
    /// </summary>
    private readonly Dictionary<string, SessionGeneration> _generations = new(StringComparer.Ordinal);

    public ObservableCollection<TranscriptEntry> TranscriptItems { get; } = [];

    private sealed class SessionGeneration
    {
        public required string SessionId { get; init; }
        public required ConversationSession Session { get; init; }
        public required string UserMessage { get; init; }
        public required string VisibleUserText { get; init; }
        public required bool IsContinuation { get; init; }
        /// <summary>True when this turn is a planned long-form segment (new user turn, not prefill).</summary>
        public bool IsSegmentTurn { get; init; }
        public required bool UseMemos { get; init; }
        public required bool SaveLog { get; init; }
        public required string MemosSessionId { get; init; }
        public StringBuilder Accumulated { get; } = new();
        public List<ChatMessage> RequestHistory { get; init; } = [];
        public CancellationTokenSource Cts { get; } = new();
        /// <summary>UI bubbles for this job only — never look up "last streaming" globally.</summary>
        public TranscriptEntry? SubmittedEntry { get; set; }
        public TranscriptEntry? ReplyEntry { get; set; }
        public int RequestedMaxOutputTokens { get; set; }
        /// <summary>Actual max_tokens after context budget clamp.</summary>
        public int AppliedMaxOutputTokens { get; set; }
        public long StartedTimestamp { get; set; }
        public string? ProgressBaseNote { get; set; }
    }

    private SessionGeneration? ActiveGeneration
        => _generations.TryGetValue(_sessions.Active.Id, out var gen) ? gen : null;

    private SessionGeneration? FindGeneration(string sessionId)
        => _generations.TryGetValue(sessionId, out var gen) ? gen : null;

    private int MaxParallelGenerations
        => Math.Clamp(_activeModelSettings.ParallelSlots, 1, 8);

    private IEnumerable<SessionGeneration> OtherRunningGenerations(string sessionId)
        => _generations.Values.Where(g => g.SessionId != sessionId);

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
        _memoryQueue.StateChanged += MemoryQueue_StateChanged;
        SettingsPanel.SaveRequested += SettingsPanel_SaveRequested;
        SettingsPanel.CloseRequested += SettingsPanel_CloseRequested;
        // Focus only after close animation finishes — focusing mid-slide causes UI thrash/jank.
        SettingsPanel.Closed += (_, _) => SettingsButton.Focus(FocusState.Programmatic);
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _initializationStage = "解析本地路径";
            _paths = AppPaths.Discover();
            _initializationStage = "加载设置";
            _settingsStore = new SettingsStore(_paths.SettingsFile);
            _modelConfigStore = new ModelServiceConfigStore(_paths.ProjectRoot, _paths.ModelServiceConfigFile);
            var modelConfigLoad = _modelConfigStore.LoadOrMigrate(_paths.SettingsFile);
            _modelConfig = modelConfigLoad.Config;
            LoadSettings();
            if (modelConfigLoad.Migrated) _settingsStore.Save(_settings);
            _initializationStage = "创建模型客户端";
            CreateModelClients(_settings);
            _initializationStage = "创建聊天日志";
            _chatLog = NewLog();
            _initializationStage = "加载会话";
            _sessionStore = new ConversationSessionStore(_paths.SessionsFile);
            _sessions.SortMode = _settings.ResolveSessionSortMode();
            var sessionLoad = _sessionStore.LoadInto(_sessions);
            if (sessionLoad.Restored)
            {
                ApplySessionToUi(_sessions.Active, announce: false);
                SetNotice($"已恢复 {_sessions.Sessions.Count} 个会话（含草稿）。", MutedText);
                if (sessionLoad.Warning is not null)
                    SetNotice(sessionLoad.Warning, WarningText);
            }
            else
            {
                if (sessionLoad.Warning is not null)
                    SetNotice(sessionLoad.Warning, WarningText);
                AppendSystem("正在检查本地模型服务；长期记忆和磁盘日志默认关闭。");
                MaybeShowOnboardingTips();
                CaptureActiveSession();
                PersistSessions();
            }

            RefreshSessionPicker();
            _initializationStage = "检查模型服务";
            await InitializeModelAsync();
            UpdateContinueButtonState();
        }
        catch (Exception error)
        {
            SendButton.IsEnabled = false;
            ContinueButton.IsEnabled = false;
            SetNotice($"初始化失败（{_initializationStage}）：{error.Message}", ErrorText);
        }
    }

    private void MaybeShowOnboardingTips()
    {
        if (_settings.OnboardingSeen || _settingsStore is null) return;
        AppendSystem("欢迎使用 Qwen Local：模型仅监听本机 127.0.0.1，不调用云端 API。");
        AppendSystem("快速上手：顶栏可切换/新建会话；底部开关控制 MemOS 与日志；「设置」调参数；停止或达上限后可用「继续生成」；「更多」里可导出对话。");
        try
        {
            _settings = _settings with { OnboardingSeen = true };
            _settingsStore.Save(_settings);
        }
        catch (Exception error)
        {
            SetNotice($"引导状态保存失败：{error.Message}", WarningText);
        }
    }

    private TaskCompletionSource<AppCloseDecision>? _closeDecisionTcs;

    /// <summary>
    /// Ask whether to stop the local model when leaving. Shows for both owned and reused services
    /// (reused = previous session left the process running). Avoids ContentDialog (crash-prone here).
    /// </summary>
    public async Task<AppCloseDecision> RequestCloseDecisionAsync()
    {
        if (_disposed) return AppCloseDecision.ExitKeepModel;
        if (_modelManager is null) return AppCloseDecision.ExitKeepModel;

        var ownsModel = _modelManager.OwnsModel;
        var serviceRunning = ownsModel || await _modelManager.IsHealthyAsync();
        if (!serviceRunning) return AppCloseDecision.ExitKeepModel;

        _closeDecisionTcs?.TrySetResult(AppCloseDecision.Cancel);
        _closeDecisionTcs = new TaskCompletionSource<AppCloseDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClosePromptTitle.Text = "退出 Qwen Local";
        ClosePromptMessage.Text = ownsModel
            ? "本窗口启动了本地模型服务。关闭时是否一并停止？"
            : "本机模型服务仍在运行（当前为「已复用」）。关闭时是否一并停止？";
        CloseStopModelHint.Text = ownsModel
            ? "停止模型可释放 GPU/内存；改过的启动参数下次打开才生效。"
            : "停止模型会结束端口上的 llama 服务；改过的启动参数下次打开才生效。";
        ClosePromptOverlay.Visibility = Visibility.Visible;
        ClosePromptOverlay.IsHitTestVisible = true;
        CloseStopModelButton.Focus(FocusState.Programmatic);
        return await _closeDecisionTcs.Task;
    }

    private void CloseStopModelButton_Click(object sender, RoutedEventArgs e)
        => CompleteClosePrompt(AppCloseDecision.ExitStopModel);

    private void CloseKeepModelButton_Click(object sender, RoutedEventArgs e)
        => CompleteClosePrompt(AppCloseDecision.ExitKeepModel);

    private void CloseCancelButton_Click(object sender, RoutedEventArgs e)
        => CompleteClosePrompt(AppCloseDecision.Cancel);

    private void CompleteClosePrompt(AppCloseDecision decision)
    {
        ClosePromptOverlay.Visibility = Visibility.Collapsed;
        ClosePromptOverlay.IsHitTestVisible = false;
        var tcs = _closeDecisionTcs;
        _closeDecisionTcs = null;
        tcs?.TrySetResult(decision);
    }

    public async Task ShutdownAsync(bool stopOwnedModel = false)
    {
        if (_disposed) return;
        _disposed = true;
        CompleteClosePrompt(AppCloseDecision.Cancel);
        _memoryQueue.StateChanged -= MemoryQueue_StateChanged;
        SettingsPanel.SaveRequested -= SettingsPanel_SaveRequested;
        SettingsPanel.CloseRequested -= SettingsPanel_CloseRequested;
        foreach (var gen in _generations.Values.ToList())
        {
            try { gen.Cts.Cancel(); } catch { }
        }
        // Flush active UI (incl. unsent draft) before tearing down generation.
        try
        {
            if (_sessions.Sessions.Count > 0)
            {
                CaptureActiveSession();
                PersistSessions();
            }
        }
        catch { /* never block close on disk issues */ }

        foreach (var gen in _generations.Values.ToList())
        {
            try { gen.Cts.Dispose(); } catch { }
        }
        _generations.Clear();
        _memoryQueue.CancelPending();
        try { await _memoryQueue.DrainAsync(); } catch { }
        await DisposeMemosAsync();
        _chatClient?.Dispose();
        if (_modelManager is not null)
        {
            try
            {
                // Stop owned process, or the reused listener on our port when user chose "停止模型".
                if (stopOwnedModel)
                    await _modelManager.StopServiceAsync();
            }
            catch { /* best-effort stop; still release handles */ }
            await _modelManager.DisposeAsync();
        }
        _memoryQueue.Dispose();
        _memosGate.Dispose();
    }

    private void MemoryQueue_StateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(UpdateMemosStatus);
    }

    private void LoadSettings()
    {
        if (_settingsStore is null) return;
        _loadingSettings = true;
        var loaded = _settingsStore.Load();
        _settings = _modelConfig is null ? loaded.Settings : loaded.Settings.WithModelService(_modelConfig);
        UseMemosToggle.IsOn = loaded.Settings.UseMemos;
        SaveLogsToggle.IsOn = loaded.Settings.SaveChatLogs;
        if (_chatLog is not null) _chatLog.Enabled = loaded.Settings.SaveChatLogs;
        _loadingSettings = false;
        UpdateMemosStatus();
        UpdateLogStatus();
        if (loaded.Warning is not null) SetNotice(loaded.Warning, WarningText);
    }

    private async void UseMemosToggle_Toggled(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async void SaveLogsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        if (_loadingSettings || _settingsStore is null) return;
        try
        {
            _settings = _settings with
            {
                UseMemos = UseMemosToggle.IsOn,
                SaveChatLogs = SaveLogsToggle.IsOn,
            };
            _settingsStore.Save(_settings);
            if (_chatLog is not null) _chatLog.Enabled = SaveLogsToggle.IsOn;
            UpdateMemosStatus();
            UpdateLogStatus();
            if (!UseMemosToggle.IsOn)
            {
                await _memoryQueue.DrainAsync();
                if (!UseMemosToggle.IsOn) await DisposeMemosAsync();
            }
        }
        catch (Exception error)
        {
            SetNotice($"设置保存失败：{error.Message}", ErrorText);
        }
    }

    private void CreateModelClients(LocalChatSettings settings)
    {
        if (_paths is null || _modelConfig is null) throw new InvalidOperationException("模型服务配置尚未初始化");
        var options = _modelConfig.ToLocalModelOptions(
            _paths.ProjectRoot,
            _paths.ModelLogFile,
            _paths.ModelStartLockFile,
            _paths.ModelLifecycleLogFile);
        _modelManager = new QwenServiceManager(options, new WindowsModelProcessLauncher());
        _chatClient = new QwenChatClient(options.ChatCompletionsUri);
        _activeModelSettings = settings;
        EndpointText.Text = $"LOCAL CORE  /  {_modelConfig.BindHost}:{_modelConfig.Port}";
        if (App.Window is MainWindow window) window.SetEndpointSubtitle(_modelConfig.BindHost, _modelConfig.Port);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Open(_settings);
    }

    private async void SettingsPanel_SaveRequested(object? sender, SettingsSaveRequestedEventArgs e)
    {
        DismissSettingsPanel();
        await ApplyRuntimeSettingsAsync(e.Settings);
    }

    private void SettingsPanel_CloseRequested(object? sender, EventArgs e)
        => DismissSettingsPanel();

    private void DismissSettingsPanel()
    {
        // Keep focus on the panel content until Closed; transferring focus early freezes the slide.
        SettingsPanel.Close();
    }

    private async Task ApplyRuntimeSettingsAsync(LocalChatSettings candidate)
    {
        if (_settingsStore is null) return;

        var previous = _settings;
        var previousModelConfig = _modelConfig;
        try
        {
            if (_modelConfigStore is null || previousModelConfig is null)
                throw new InvalidOperationException("模型服务配置尚未初始化");
            var candidateModelConfig = candidate.ApplyToModelService(previousModelConfig);
            _modelConfigStore.Save(candidateModelConfig);
            _settingsStore.Save(candidate);
            _settings = candidate;
            _modelConfig = candidateModelConfig;
            ApplySessionSortMode(candidate.ResolveSessionSortMode());
            // Long-form toggles affect the continue / next-segment button immediately.
            if (!candidate.SegmentedLongForm)
            {
                foreach (var session in _sessions.Sessions)
                    session.LongFormPlan = null;
            }
            UpdateContinueButtonState();
            var requiresRestart = candidate.RequiresModelRestartComparedWith(previous);
            if (!requiresRestart)
            {
                var notice = candidate.DescribeSaveResult(previous, modelOwnedByWindow: false);
                if (previous.ResolveSessionSortMode() != candidate.ResolveSessionSortMode())
                    notice = $"{notice} 会话列表：{SessionSortModes.DisplayName(candidate.ResolveSessionSortMode())}。";
                SetNotice(notice, MutedText);
                return;
            }

            if (_modelManager is { OwnsModel: true })
            {
                SetNotice("正在按新启动参数重启本窗口拥有的模型…", WarningText);
                await ModelRestartTransaction.RunAsync(
                    stopPrevious: async () =>
                    {
                        await _modelManager.StopOwnedAsync("settings_restart");
                        await _modelManager.DisposeAsync();
                        _chatClient?.Dispose();
                        _modelManager = null;
                        _chatClient = null;
                    },
                    startCandidate: async () =>
                    {
                        CreateModelClients(candidate);
                        await InitializeModelAsync(throwOnFailure: true);
                    },
                    restorePersistentState: () =>
                    {
                        _settings = previous;
                        _modelConfig = previousModelConfig;
                        _modelConfigStore.Save(previousModelConfig);
                        _settingsStore.Save(previous);
                        return Task.CompletedTask;
                    },
                    stopCandidate: async () =>
                    {
                        var candidateManager = _modelManager;
                        var candidateChatClient = _chatClient;
                        _modelManager = null;
                        _chatClient = null;
                        try
                        {
                            if (candidateManager is { OwnsModel: true })
                                await candidateManager.StopOwnedAsync("settings_restart_rollback");
                        }
                        finally
                        {
                            if (candidateManager is not null)
                                await candidateManager.DisposeAsync();
                            candidateChatClient?.Dispose();
                        }
                    },
                    startPrevious: async () =>
                    {
                        _settings = previous;
                        _modelConfig = previousModelConfig;
                        CreateModelClients(previous);
                        await InitializeModelAsync(throwOnFailure: true);
                    });
                SetNotice(candidate.DescribeSaveResult(previous, modelOwnedByWindow: true), MutedText);
            }
            else
            {
                SetNotice(candidate.DescribeSaveResult(previous, modelOwnedByWindow: false), WarningText);
            }
        }
        catch (Exception error)
        {
            _settings = previous;
            _modelConfig = previousModelConfig;
            if (previousModelConfig is not null && _modelConfigStore is not null)
            {
                try { _modelConfigStore.Save(previousModelConfig); } catch { }
            }
            try { _settingsStore.Save(previous); } catch { }
            if (error is ModelRestartException restartError)
            {
                var rollbackStatus = restartError.PreviousRuntimeRestored
                    ? "已恢复原设置和原模型运行状态"
                    : "已恢复磁盘设置，模型运行状态仍需处理";
                SetNotice($"设置应用失败；{rollbackStatus}：{restartError.ApplyError.Message}", ErrorText);
                return;
            }
            SetNotice($"设置应用失败，已恢复原参数：{error.Message}", ErrorText);
        }
    }

    private async Task InitializeModelAsync(bool throwOnFailure = false)
    {
        if (_modelManager is null) return;
        try
        {
            SetStatus(QwenStatusText, "• 模型检查中", WarningText);
            var availability = await _modelManager.EnsureAvailableAsync();
            SetStatus(QwenStatusText, availability.Reused ? "• 模型已复用" : "• 模型已启动", PrimaryText);
            SetNotice($"模型只监听 127.0.0.1:{_activeModelSettings.Port}。", MutedText);
            // Lifecycle notice: dedupe + not persisted (avoids duplicate rows after session restore).
            AppendSystem(availability.Reused
                ? "已复用当前本地模型服务，可以开始对话。"
                : "本地模型服务已由本窗口启动，可以开始对话。",
                dedupeIdentical: true);
            InputBox.Focus(FocusState.Programmatic);
        }
        catch (Exception error)
        {
            if (throwOnFailure) throw;
            SetStatus(QwenStatusText, "× 模型不可用", ErrorText);
            SetNotice($"模型启动失败：{error.Message}", ErrorText);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsActiveSessionGenerating())
        {
            try { ActiveGeneration?.Cts.Cancel(); } catch { }
            SetNotice("正在停止当前会话的生成…", WarningText);
            return;
        }

        await SendAsync();
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsActiveSessionGenerating()) return;

        // Prefer planned long-form next segment over prefill continue when both apply.
        var plan = _sessions.Active.LongFormPlan;
        if (_settings.SegmentedLongForm
            && LongFormPlanner.CanContinueNextSegment(plan, _history))
        {
            await SendAsync(isSegmentContinue: true);
            return;
        }

        if (!ConversationExport.CanContinue(_history, _lastFinishReason))
        {
            SetNotice("当前没有可继续的回复。", WarningText);
            UpdateContinueButtonState();
            return;
        }

        // True assistant prefill: no synthetic "请继续写" user turn.
        await SendAsync(isContinuation: true);
    }

    private async void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (!shift && (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down) && !InputBox.Text.Contains('\n'))
        {
            e.Handled = true;
            var recalled = e.Key == VirtualKey.Up
                ? _inputHistory.Previous(InputBox.Text)
                : _inputHistory.Next(InputBox.Text);
            InputBox.Text = recalled;
            InputBox.SelectionStart = InputBox.Text.Length;
            return;
        }

        var enterAction = ChatInteractionPolicy.ResolveEnter(e.Key == VirtualKey.Enter, shift);
        if (enterAction == ComposerKeyAction.Send)
        {
            e.Handled = true;
            if (IsActiveSessionGenerating()) return;
            await SendAsync();
        }
    }

    private bool IsActiveSessionGenerating()
        => ActiveGeneration is not null;

    private bool IsViewingSession(string sessionId)
        => _sessions.Active.Id == sessionId;

    private async Task SendAsync(
        string? forcedUserMessage = null,
        string? displayUserLabel = null,
        bool isContinuation = false,
        bool isSegmentContinue = false)
    {
        if (_modelManager is null || _chatClient is null) return;

        var owner = _sessions.Active;
        if (FindGeneration(owner.Id) is not null)
        {
            SetNotice("当前会话仍在生成中，请先点「停止」或等完成后再发。", WarningText);
            return;
        }

        if (_generations.Count >= MaxParallelGenerations)
        {
            var titles = string.Join("、", _generations.Values.Select(g => g.Session.Title));
            SetNotice(
                $"已有 {_generations.Count} 路生成在跑（并发槽位 {MaxParallelGenerations}）：{titles}。请等其一完成，或到对应会话点「停止」。",
                WarningText);
            return;
        }

        var submittedInput = InputBox.Text;
        string userMessage;
        string visibleUserText;
        List<ChatMessage> requestHistory;
        var isSegmentTurn = false;
        var requestedMax = _settings.MaxOutputTokens;

        if (isContinuation && !isSegmentContinue)
        {
            if (!ConversationExport.CanContinue(_history, _lastFinishReason))
            {
                SetNotice("当前没有可继续的回复。", WarningText);
                return;
            }
            // Prefill uses history as-is (ends on incomplete assistant). UserMessage is only
            // a log label — it is not sent as a new chat turn.
            userMessage = ConversationExport.ContinuePrompt;
            visibleUserText = displayUserLabel ?? ConversationExport.ContinueUserLabel;
            requestHistory = _history.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
            // Prefill continue: keep settings max (already length-capped last turn).
            requestedMax = _settings.MaxOutputTokens;
        }
        else if (isSegmentContinue)
        {
            if (!_settings.SegmentedLongForm)
            {
                SetNotice("长文分段写作已关闭，可在设置中开启。", WarningText);
                owner.LongFormPlan = null;
                UpdateContinueButtonState();
                return;
            }

            var plan = owner.LongFormPlan;
            if (!LongFormPlanner.CanContinueNextSegment(plan, _history) || plan is null)
            {
                SetNotice("当前没有待续写的长文分段。", WarningText);
                UpdateContinueButtonState();
                return;
            }

            var segIndex = plan.NextSegmentIndex;
            userMessage = LongFormPlanner.BuildSegmentUserMessage(plan, segIndex);
            visibleUserText = displayUserLabel ?? LongFormPlanner.SegmentVisibleLabel(plan, segIndex);
            requestHistory = _history.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
            isSegmentTurn = true;
            requestedMax = LongFormPlanner.SuggestMaxOutputTokens(
                plan.SegmentChars, _settings.MaxOutputTokens, segmentTurn: true);
        }
        else
        {
            userMessage = forcedUserMessage ?? submittedInput.Trim();
            if (userMessage.Length == 0) return;
            visibleUserText = displayUserLabel ?? userMessage;
            requestHistory = _history.Select(m => new ChatMessage(m.Role, m.Content)).ToList();

            // New user turn replaces any unfinished long-form plan.
            owner.LongFormPlan = null;
            if (_settings.SegmentedLongForm
                && LongFormPlanner.TryBeginSegmentedPlan(userMessage) is { } newPlan)
            {
                owner.LongFormPlan = newPlan;
                userMessage = LongFormPlanner.BuildSegmentUserMessage(newPlan, 0);
                visibleUserText = displayUserLabel
                    ?? LongFormPlanner.SegmentVisibleLabel(newPlan, 0);
                isSegmentTurn = true;
                requestedMax = LongFormPlanner.SuggestMaxOutputTokens(
                    newPlan.SegmentChars, _settings.MaxOutputTokens, segmentTurn: true);
            }
            else if (_settings.AutoTightenOutputTokens)
            {
                requestedMax = LongFormPlanner.ResolveRequestMaxOutputTokens(
                    userMessage, _settings.MaxOutputTokens, activePlan: null, isSegmentTurn: false);
            }
            else
            {
                requestedMax = _settings.MaxOutputTokens;
            }
        }

        // Snapshot history/session before any await so a later session switch cannot re-route this job.
        var gen = new SessionGeneration
        {
            SessionId = owner.Id,
            Session = owner,
            UserMessage = userMessage,
            VisibleUserText = visibleUserText,
            IsContinuation = isContinuation && !isSegmentContinue,
            IsSegmentTurn = isSegmentTurn,
            UseMemos = !(isContinuation && !isSegmentContinue) && UseMemosToggle.IsOn,
            SaveLog = SaveLogsToggle.IsOn,
            MemosSessionId = owner.MemosSessionId,
            RequestHistory = requestHistory,
            RequestedMaxOutputTokens = requestedMax,
        };

        if (!gen.IsContinuation && !isSegmentContinue && forcedUserMessage is null)
            InputBox.Text = string.Empty;
        var sendToken = gen.Cts.Token;
        _generations[gen.SessionId] = gen;
        RefreshComposerBusyState();
        if (_generations.Count > 1)
        {
            SetNotice($"并发生成 {_generations.Count}/{MaxParallelGenerations}：当前会话已发送，其它会话后台继续。", MutedText);
        }
        else
        {
            var goalNote = LongFormPlanner.GoalFeedback(
                userMessage: gen.Session.LongFormPlan?.OriginalUserPrompt ?? gen.VisibleUserText,
                settingsMax: _settings.MaxOutputTokens,
                effectiveMax: gen.RequestedMaxOutputTokens,
                autoTighten: _settings.AutoTightenOutputTokens,
                segmentedEnabled: _settings.SegmentedLongForm,
                plan: gen.Session.LongFormPlan,
                isSegmentTurn: gen.IsSegmentTurn);
            gen.ProgressBaseNote = goalNote;
            if (goalNote is not null)
                SetNotice(goalNote, MutedText);
        }

        TranscriptEntry? submittedEntry = null;
        TranscriptEntry? replyEntry = null;
        try
        {
            if (IsViewingSession(gen.SessionId))
                SetStatus(QwenStatusText, "• 模型检查中", WarningText);
            await _modelManager.EnsureAvailableAsync(sendToken);
            if (IsViewingSession(gen.SessionId))
                SetStatus(QwenStatusText, _modelManager.OwnsModel ? "• 模型已启动" : "• 模型已复用", PrimaryText);

            string? memoryContext = null;
            MemosStdioClient? memoryClient = null;
            if (gen.UseMemos)
            {
                try
                {
                    if (IsViewingSession(gen.SessionId))
                        SetStatus(MemosStatusText, "• 记忆召回中", WarningText);
                    memoryClient = await EnsureMemosAsync();
                    memoryContext = await memoryClient.RecallAsync(userMessage, _settings.MemosTopK, sendToken);
                    if (IsViewingSession(gen.SessionId))
                        SetStatus(MemosStatusText, "• 记忆已开启", PrimaryText);
                }
                catch (OperationCanceledException) when (sendToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    if (IsViewingSession(gen.SessionId))
                    {
                        SetNotice($"MemOS 召回失败，本轮按无记忆模式继续：{error.Message}", WarningText);
                        SetStatus(MemosStatusText, "△ 记忆降级", WarningText);
                    }
                }
            }

            // Prefer live server n_ctx (after kv-unified / slot split) over the settings label.
            var effectiveContext = _modelManager.EffectiveContextSize > 0
                ? _modelManager.EffectiveContextSize
                : _activeModelSettings.ContextSize;
            var contextPolicy = new ContextWindowPolicy(
                effectiveContext,
                gen.RequestedMaxOutputTokens,
                _settings.MaxHistoryRounds);
            IReadOnlyList<ChatMessage> requestMessages;
            if (gen.IsContinuation)
            {
                requestMessages = AssistantContinuation.BuildPrefillMessages(gen.RequestHistory, contextPolicy);
            }
            else
            {
                requestMessages = ConversationContext.Build(
                    gen.RequestHistory, gen.UserMessage, memoryContext, contextPolicy);
            }
            var maxOutputTokens = ContextBudget.CalculateMaxOutputTokens(
                requestMessages,
                effectiveContext,
                gen.RequestedMaxOutputTokens);
            gen.AppliedMaxOutputTokens = maxOutputTokens;
            gen.StartedTimestamp = Stopwatch.GetTimestamp();
            if (IsViewingSession(gen.SessionId) && _generations.Count == 1)
                SetNotice(FormatLiveGenerationProgress(gen, outputChars: 0), MutedText);

            // Prefill continuation: disable thinking so the budget is not spent on reasoning_content.
            // Normal turns: keep server thinking when enabled, with an auto thinking-token budget.
            var generation = ChatGenerationOptions.FromSettings(
                _settings,
                _activeModelSettings.ModelAlias,
                maxOutputTokens,
                enableThinking: gen.IsContinuation ? false : null,
                reasoningEnabled: _activeModelSettings.ReasoningEnabled && !gen.IsContinuation);

            var previousAssistant = gen.IsContinuation ? gen.RequestHistory[^1].Content : string.Empty;

            if (IsViewingSession(gen.SessionId))
            {
                if (gen.IsContinuation)
                {
                    // Extend the existing Qwen bubble — do not invent a fake user turn.
                    replyEntry = TranscriptItems.LastOrDefault(item => item.Label == "Qwen");
                    if (replyEntry is not null)
                    {
                        replyEntry.ResumeStreaming(previousAssistant);
                        gen.ReplyEntry = replyEntry;
                        ScrollTranscriptToQuestion(replyEntry);
                    }
                    else
                    {
                        replyEntry = AppendTurn("Qwen", previousAssistant, PrimaryText, isStreaming: true);
                        gen.ReplyEntry = replyEntry;
                    }
                }
                else
                {
                    submittedEntry = AppendTurn("你", gen.VisibleUserText, PrimaryText);
                    gen.SubmittedEntry = submittedEntry;
                }
            }

            ChatCompletionResult completion;
            if (_settings.StreamResponses)
            {
                if (IsViewingSession(gen.SessionId) && !gen.IsContinuation)
                {
                    replyEntry = AppendTurn("Qwen", string.Empty, PrimaryText, isStreaming: true);
                    gen.ReplyEntry = replyEntry;
                    if (submittedEntry is not null)
                        ScrollTranscriptToQuestion(submittedEntry);
                }

                var lastUpdate = Stopwatch.GetTimestamp();
                var lastProgressUpdate = Stopwatch.GetTimestamp();
                completion = await _chatClient.CompleteStreamingAsync(requestMessages, generation, delta =>
                {
                    gen.Accumulated.Append(delta);
                    // Slightly longer throttle: live Markdown re-parse is heavier than plain text.
                    if (Stopwatch.GetElapsedTime(lastUpdate) < TimeSpan.FromMilliseconds(120)) return;
                    lastUpdate = Stopwatch.GetTimestamp();
                    // Prefill streams re-emit prior text then new tokens; snapshot is the full body.
                    var snapshot = gen.IsContinuation
                        ? AssistantContinuation.MergeResponse(previousAssistant, gen.Accumulated.ToString())
                        : gen.Accumulated.ToString();
                    // Only paint tokens onto this job's bubble while its session is on screen.
                    if (!IsViewingSession(gen.SessionId)) return;
                    var refreshProgress = Stopwatch.GetElapsedTime(lastProgressUpdate) >= TimeSpan.FromSeconds(1);
                    if (refreshProgress)
                        lastProgressUpdate = Stopwatch.GetTimestamp();
                    var charsForProgress = CountCjkRough(snapshot);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!IsViewingSession(gen.SessionId)) return;
                        // Prefer the bubble created for this gen — never "last streaming in the list".
                        var live = gen.ReplyEntry;
                        if (live is null || !TranscriptItems.Contains(live))
                        {
                            live = FindStreamingReplyForGeneration(gen);
                            gen.ReplyEntry = live;
                        }
                        live?.UpdateStreamingText(snapshot);
                        if (refreshProgress && _generations.Count == 1)
                            SetNotice(FormatLiveGenerationProgress(gen, charsForProgress), MutedText);
                    });
                }, sendToken);

                var finalBody = gen.IsContinuation
                    ? AssistantContinuation.MergeResponse(previousAssistant, completion.Content)
                    : completion.Content;
                completion = completion with { Content = finalBody };

                if (IsViewingSession(gen.SessionId))
                {
                    var live = gen.ReplyEntry;
                    if (live is null || !TranscriptItems.Contains(live))
                        live = FindStreamingReplyForGeneration(gen) ?? replyEntry;
                    live?.Complete(finalBody);
                    gen.ReplyEntry = live;
                    var question = gen.SubmittedEntry ?? submittedEntry ?? live;
                    if (question is not null && live is not null)
                        ScrollTranscriptToQuestion(TranscriptPresentationPolicy.AnchorAfterReply(question, live));
                }
            }
            else
            {
                completion = await _chatClient.CompleteWithDetailsAsync(requestMessages, generation, sendToken);
                var finalBody = gen.IsContinuation
                    ? AssistantContinuation.MergeResponse(previousAssistant, completion.Content)
                    : completion.Content;
                completion = completion with { Content = finalBody };
                gen.Accumulated.Clear();
                gen.Accumulated.Append(finalBody);
                if (IsViewingSession(gen.SessionId))
                {
                    if (gen.IsContinuation)
                    {
                        var live = gen.ReplyEntry ?? TranscriptItems.LastOrDefault(item => item.Label == "Qwen");
                        live?.Complete(finalBody);
                        gen.ReplyEntry = live;
                        if (live is not null)
                            ScrollTranscriptToQuestion(live);
                    }
                    else
                    {
                        replyEntry = AppendTurn("Qwen", finalBody, PrimaryText);
                        gen.ReplyEntry = replyEntry;
                        var question = gen.SubmittedEntry ?? submittedEntry;
                        if (question is not null)
                            ScrollTranscriptToQuestion(TranscriptPresentationPolicy.AnchorAfterReply(question, replyEntry));
                    }
                }
            }

            CommitGenerationSuccess(gen, completion, maxOutputTokens, memoryClient);
        }
        catch (OperationCanceledException) when (sendToken.IsCancellationRequested)
        {
            CommitGenerationCancelled(
                gen,
                submittedInput,
                restoreInput: !gen.IsContinuation && forcedUserMessage is null);
        }
        catch (Exception error)
        {
            CommitGenerationFailed(
                gen,
                error,
                submittedInput,
                restoreInput: !gen.IsContinuation && forcedUserMessage is null);
        }
        finally
        {
            if (_generations.TryGetValue(gen.SessionId, out var current) && ReferenceEquals(current, gen))
                _generations.Remove(gen.SessionId);
            try { gen.Cts.Dispose(); } catch { }
            RefreshComposerBusyState();
            UpdateMemosStatus();
            UpdateContinueButtonState();
            RefreshSessionPicker();
        }
    }

    private TranscriptEntry? FindStreamingReplyEntry()
        => TranscriptItems.LastOrDefault(item => item.Label == "Qwen" && item.IsStreaming);

    /// <summary>Locate this job's streaming bubble without stealing another generation's row.</summary>
    private TranscriptEntry? FindStreamingReplyForGeneration(SessionGeneration gen)
    {
        if (gen.ReplyEntry is not null && TranscriptItems.Contains(gen.ReplyEntry))
            return gen.ReplyEntry;

        // Prefer Qwen after this gen's user bubble.
        if (gen.SubmittedEntry is not null && TranscriptItems.Contains(gen.SubmittedEntry))
        {
            var idx = TranscriptItems.IndexOf(gen.SubmittedEntry);
            for (var i = idx + 1; i < TranscriptItems.Count; i++)
            {
                if (TranscriptItems[i].Label == "Qwen" && TranscriptItems[i].IsStreaming)
                    return TranscriptItems[i];
            }
        }

        // Fallback: last streaming Qwen only if its preceding user matches this gen.
        for (var i = TranscriptItems.Count - 1; i >= 0; i--)
        {
            var item = TranscriptItems[i];
            if (item.Label != "Qwen" || !item.IsStreaming) continue;
            for (var j = i - 1; j >= 0; j--)
            {
                if (TranscriptItems[j].Label != "你") continue;
                return string.Equals(TranscriptItems[j].Text, gen.VisibleUserText, StringComparison.Ordinal)
                    ? item
                    : null;
            }
            return null;
        }

        return null;
    }

    /// <summary>
    /// Upsert this round's 你/Qwen pair into a session transcript (idempotent — fixes double replies).
    /// </summary>
    private static void UpsertGenerationTranscript(ConversationSession session, SessionGeneration gen, string assistantMessage)
    {
        var turns = session.Transcript
            .Where(t => SystemNoticePolicy.ShouldPersist(t.Label, t.Text))
            .ToList();

        // Remove any existing pair for this user turn (partial stream capture, retries, etc.).
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Label != "你"
                || !string.Equals(turns[i].Text, gen.VisibleUserText, StringComparison.Ordinal))
            {
                continue;
            }

            var removeCount = 1;
            if (i + 1 < turns.Count && turns[i + 1].Label == "Qwen")
                removeCount = 2;
            turns.RemoveRange(i, removeCount);
            // Keep scanning in case older partial duplicates exist.
        }

        turns.Add(new ConversationTurn("你", gen.VisibleUserText));
        turns.Add(new ConversationTurn("Qwen", assistantMessage));
        session.Transcript = turns;
        session.UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>Replace the last Qwen body after assistant-prefill continuation.</summary>
    private static void UpsertContinuedAssistantTranscript(ConversationSession session, string assistantMessage)
    {
        var turns = session.Transcript
            .Where(t => SystemNoticePolicy.ShouldPersist(t.Label, t.Text))
            .ToList();
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Label != "Qwen") continue;
            turns[i] = new ConversationTurn("Qwen", assistantMessage);
            session.Transcript = turns;
            session.UpdatedAt = DateTimeOffset.Now;
            return;
        }

        turns.Add(new ConversationTurn("Qwen", assistantMessage));
        session.Transcript = turns;
        session.UpdatedAt = DateTimeOffset.Now;
    }

    private void CommitGenerationSuccess(
        SessionGeneration gen,
        ChatCompletionResult completion,
        int maxOutputTokens,
        MemosStdioClient? memoryClient)
    {
        var assistantMessage = completion.Content;
        var viewing = IsViewingSession(gen.SessionId);

        // Always persist into the owning session object (never the currently viewed other session).
        if (gen.IsContinuation)
        {
            // Extend the same assistant turn — do not append a synthetic user/assistant pair.
            gen.Session.History = AssistantContinuation.ReplaceLastAssistant(
                gen.RequestHistory, assistantMessage).ToList();
            UpsertContinuedAssistantTranscript(gen.Session, assistantMessage);
        }
        else
        {
            gen.Session.History = gen.RequestHistory
                .Concat([new ChatMessage("user", gen.UserMessage), new ChatMessage("assistant", assistantMessage)])
                .ToList();
            // Idempotent transcript write for this round — avoids double Qwen when mid-stream was captured.
            UpsertGenerationTranscript(gen.Session, gen, assistantMessage);
        }
        gen.Session.LastFinishReason = completion.FinishReason;
        gen.Session.UpdatedAt = DateTimeOffset.Now;
        if (gen.Session.Title is "新会话" or "")
            gen.Session.Title = ConversationSession.SuggestTitle(gen.Session.History);

        // Advance segmented long-form progress after a real (non-prefill) segment turn.
        if (gen.IsSegmentTurn && gen.Session.LongFormPlan is { } plan)
        {
            LongFormPlanner.MarkSegmentCompleted(plan);
            if (!plan.Active)
                gen.Session.LongFormPlan = null;
        }

        _sessions.ApplySort();

        if (viewing)
        {
            _history.Clear();
            _history.AddRange(gen.Session.History.Select(m => new ChatMessage(m.Role, m.Content)));
            if (!gen.IsContinuation) _inputHistory.Record(gen.UserMessage);
            _lastFinishReason = completion.FinishReason;

            // Rebuild UI from the canonical session transcript so any stale stream rows are gone.
            RebuildVisibleTranscriptFromSession(gen.Session, liveGen: null);
            PersistSessions();
        }
        else
        {
            PersistSessions();
        }

        if (gen.SaveLog && _chatLog is not null)
        {
            _chatLog.Enabled = true;
            try
            {
                _chatLog.RecordSuccessfulRound(
                    gen.IsContinuation ? ConversationExport.ContinueUserLabel : gen.UserMessage,
                    assistantMessage);
                if (viewing) UpdateLogStatus();
            }
            catch (Exception error)
            {
                if (viewing)
                {
                    SetNotice($"日志写入失败：{error.Message}", WarningText);
                    SetStatus(LogStatusText, "× 日志失败", WarningText);
                }
            }
        }

        if (gen.UseMemos && memoryClient is not null
            && !string.Equals(completion.FinishReason, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            var capturedClient = memoryClient;
            var memosSessionId = gen.MemosSessionId;
            var userMessage = gen.UserMessage;
            _memoryQueue.Enqueue(async cancellationToken =>
                await capturedClient.RememberAsync(userMessage, assistantMessage, memosSessionId, cancellationToken));
        }

        if (viewing)
        {
            var remaining = gen.Session.LongFormPlan;
            var cjk = CountCjkRough(assistantMessage);
            var elapsed = gen.StartedTimestamp == 0
                ? 0
                : (int)Stopwatch.GetElapsedTime(gen.StartedTimestamp).TotalSeconds;
            if (string.Equals(completion.FinishReason, "cancelled", StringComparison.OrdinalIgnoreCase))
                SetNotice(
                    $"已停止（约 {cjk} 字 · {elapsed}s）。可点「{LongFormPlanner.PrefillContinueButtonLabel}」从中断处接着写。",
                    WarningText);
            else if (remaining is not null && remaining.Active)
                SetNotice(
                    $"第 {remaining.CompletedSegments}/{remaining.TotalSegments} 段完成（约 {cjk} 字 · {elapsed}s，全文约 {remaining.TargetChars} 字）。点「{LongFormPlanner.NextSegmentButtonLabel}」续写。",
                    MutedText);
            else if (gen.IsSegmentTurn && remaining is null)
                SetNotice($"长文全部分段已完成（末段约 {cjk} 字 · {elapsed}s）。", MutedText);
            else if (string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                SetNotice(
                    $"已达本轮上限 {maxOutputTokens} token（约 {cjk} 字 · {elapsed}s）。可点「{LongFormPlanner.PrefillContinueButtonLabel}」接着写，或在设置中提高最大输出。",
                    WarningText);
            else if (gen.IsContinuation)
                SetNotice($"已从中断处继续（本段约 {cjk} 字 · {elapsed}s）。", MutedText);
            else if (elapsed >= 3 || cjk >= 200)
                SetNotice($"本轮完成（约 {cjk} 字 · {elapsed}s · 上限 {maxOutputTokens} token）。", MutedText);
        }
        else
        {
            SetNotice($"会话「{gen.Session.Title}」已在后台生成完成。", MutedText);
        }
    }

    private void CommitGenerationCancelled(SessionGeneration gen, string submittedInput, bool restoreInput)
    {
        var partial = gen.Accumulated.ToString().Trim();
        var viewing = IsViewingSession(gen.SessionId);

        if (gen.IsContinuation)
        {
            var previous = gen.RequestHistory[^1].Content;
            if (partial.Length > 0)
            {
                var merged = AssistantContinuation.MergeResponse(previous, partial);
                CommitGenerationSuccess(
                    gen,
                    new ChatCompletionResult(merged, "cancelled", null, null),
                    _settings.MaxOutputTokens,
                    memoryClient: null);
                return;
            }

            // No new tokens: restore the incomplete answer; never delete the original bubble.
            if (viewing && gen.ReplyEntry is not null)
                gen.ReplyEntry.Complete(previous);
            if (viewing)
                SetNotice("已停止继续生成，已保留中断前的内容。", WarningText);
            else
                SetNotice($"已停止会话「{gen.Session.Title}」的继续生成。", WarningText);
            return;
        }

        if (partial.Length > 0)
        {
            // Keep partial like a cancelled completion.
            CommitGenerationSuccess(
                gen,
                new ChatCompletionResult(partial, "cancelled", null, null),
                _settings.MaxOutputTokens,
                memoryClient: null);
            return;
        }

        if (viewing)
        {
            RemoveGenerationBubblesFromUi(gen);
            if (restoreInput && InputBox.Text.Length == 0) InputBox.Text = submittedInput;
            SetNotice("已停止生成。", WarningText);
        }
        else
        {
            // Drop a partial mid-stream capture for this round if nothing useful was produced.
            StripIncompleteGenerationFromTranscript(gen.Session, gen);
            SetNotice($"已停止会话「{gen.Session.Title}」的后台生成。", WarningText);
        }
    }

    private void CommitGenerationFailed(
        SessionGeneration gen,
        Exception error,
        string submittedInput,
        bool restoreInput)
    {
        var viewing = IsViewingSession(gen.SessionId);
        if (gen.IsContinuation)
        {
            var previous = gen.RequestHistory[^1].Content;
            if (viewing && gen.ReplyEntry is not null)
                gen.ReplyEntry.Complete(previous);
            if (viewing)
                SetNotice($"继续生成失败：{error.Message}", ErrorText);
            else
                SetNotice($"会话「{gen.Session.Title}」继续生成失败：{error.Message}", ErrorText);
            return;
        }

        if (viewing)
        {
            RemoveGenerationBubblesFromUi(gen);
            if (restoreInput)
            {
                if (InputBox.Text.Length == 0)
                {
                    InputBox.Text = submittedInput;
                    SetNotice($"发送失败，原输入已恢复：{error.Message}", ErrorText);
                }
                else
                {
                    SetNotice($"发送失败，当前新草稿已保留：{error.Message}", ErrorText);
                }
            }
            else
            {
                SetNotice($"发送失败：{error.Message}", ErrorText);
            }
        }
        else
        {
            StripIncompleteGenerationFromTranscript(gen.Session, gen);
            SetNotice($"会话「{gen.Session.Title}」后台生成失败：{error.Message}", ErrorText);
        }
    }

    private async Task<MemosStdioClient> EnsureMemosAsync()
    {
        if (_paths is null) throw new InvalidOperationException("项目路径尚未初始化");
        await _memosGate.WaitAsync();
        try
        {
            if (_memos is { IsStarted: true }) return _memos;
            if (_memos is not null) await _memos.DisposeAsync();
            _memos = new MemosStdioClient("node.exe", _paths.McpServerScript, _paths.ProjectRoot);
            await _memos.StartAsync();
            _ = await _memos.HealthAsync();
            return _memos;
        }
        finally
        {
            _memosGate.Release();
        }
    }

    private async Task DisposeMemosAsync()
    {
        await _memosGate.WaitAsync();
        try
        {
            if (_memos is null) return;
            await _memos.DisposeAsync();
            _memos = null;
        }
        finally
        {
            _memosGate.Release();
        }
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        // Clearing the session that is generating must stop that job first.
        if (ActiveGeneration is { } activeGen)
        {
            try { activeGen.Cts.Cancel(); } catch { }
            for (var i = 0; i < 80 && FindGeneration(activeGen.SessionId) is not null; i++)
                await Task.Delay(50);
        }

        _history.Clear();
        _inputHistory.Clear();
        _lastFinishReason = null;
        // Keep composer draft — clearing chat should not throw away unsent text.
        TranscriptItems.Clear();
        AppendSystem("已清空当前会话内容。其它会话与 MemOS/日志文件未被删除。");
        CaptureActiveSession();
        _sessions.Active.Title = "新会话";
        PersistSessions();
        RefreshSessionPicker();
        SetNotice("当前会话已清空。", MutedText);
        UpdateContinueButtonState();
    }

    private void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        // Free switch: current generation keeps running on the previous session.
        _sessions.CreateAndActivate(CaptureIntoSession);
        ApplySessionToUi(_sessions.Active, announce: true);
        PersistSessions();
        RefreshSessionPicker();
        RefreshComposerBusyState();
        if (_generations.Count > 0)
        {
            var titles = string.Join("、", _generations.Values.Select(g => g.Session.Title));
            SetNotice($"已新建会话。后台仍在生成：{titles}。", MutedText);
        }
        else
            SetNotice("已新建会话。", MutedText);
    }

    private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveGeneration is { } activeGen)
        {
            try { activeGen.Cts.Cancel(); } catch { }
            for (var i = 0; i < 80 && FindGeneration(activeGen.SessionId) is not null; i++)
                await Task.Delay(50);
        }

        if (!_sessions.TryDeleteActive(CaptureIntoSession, out var next))
        {
            SetNotice("至少保留一个会话。", WarningText);
            return;
        }

        ApplySessionToUi(next, announce: false);
        PersistSessions();
        RefreshSessionPicker();
        RefreshComposerBusyState();
        SetNotice("已删除会话。", MutedText);
    }

    private void SessionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSessionPicker) return;
        if (SessionPicker.SelectedItem is not ConversationSession selected) return;
        if (selected.Id == _sessions.Active.Id) return;

        // Capture current UI (including in-progress stream bubbles), then show the other thread.
        // Generation for the previous session continues in the background.
        if (!_sessions.TryActivate(selected.Id, CaptureIntoSession, out var session))
        {
            RefreshSessionPicker();
            return;
        }

        ApplySessionToUi(session, announce: false);
        PersistSessions();
        RefreshSessionPicker();
        RefreshComposerBusyState();
        var others = OtherRunningGenerations(session.Id).Select(g => g.Session.Title).ToList();
        if (FindGeneration(session.Id) is not null)
            SetNotice(others.Count > 0
                ? $"已回到生成中的会话：{session.Title}（另有后台：{string.Join("、", others)}）"
                : $"已回到生成中的会话：{session.Title}", WarningText);
        else if (others.Count > 0)
            SetNotice($"当前会话：{session.Title}（后台生成中：{string.Join("、", others)}）", MutedText);
        else
            SetNotice($"当前会话：{session.Title}", MutedText);
    }

    private void CaptureActiveSession()
        => CaptureIntoSession(_sessions.Active);

    private void CaptureIntoSession(ConversationSession session)
    {
        // Never overwrite a background session with the currently viewed transcript/history.
        // Background sessions are updated only by CommitGeneration* using their own snapshot.
        if (_sessions.Sessions.Count > 0 && session.Id != _sessions.Active.Id)
            return;

        // Flush latest stream tokens into this job's bubble before snapshotting.
        if (FindGeneration(session.Id) is { } gen && IsViewingSession(session.Id))
            EnsureLiveGenerationBubbles(gen);

        // While a generation is in flight, do not persist its partial 你/Qwen into sessions.json
        // via Capture — CommitGenerationSuccess upserts the final pair once (prevents double replies).
        IEnumerable<(string Label, string Text)> turns;
        if (FindGeneration(session.Id) is { } liveGen)
        {
            turns = TranscriptItems
                .Where(item => SystemNoticePolicy.ShouldPersist(item.Label, item.Text))
                .Where(item =>
                    !(item.Label == "你" && string.Equals(item.Text, liveGen.VisibleUserText, StringComparison.Ordinal))
                    && !(item.Label == "Qwen" && (item.IsStreaming || ReferenceEquals(item, liveGen.ReplyEntry))))
                .Select(item => (item.Label, item.Text));
            session.Capture(_history, turns, _lastFinishReason, draftInput: InputBox.Text);
        }
        else
        {
            session.Capture(
                _history,
                TranscriptItems
                    .Where(item => SystemNoticePolicy.ShouldPersist(item.Label, item.Text))
                    .Select(item => (item.Label, item.Text)),
                _lastFinishReason,
                draftInput: InputBox.Text);
        }

        _sessions.ApplySort();
    }

    private void RemoveGenerationBubblesFromUi(SessionGeneration gen)
    {
        if (gen.ReplyEntry is not null)
            TranscriptItems.Remove(gen.ReplyEntry);
        else
        {
            var streaming = FindStreamingReplyForGeneration(gen);
            if (streaming is not null) TranscriptItems.Remove(streaming);
        }

        if (gen.SubmittedEntry is not null)
            TranscriptItems.Remove(gen.SubmittedEntry);
        else
        {
            var lastUser = TranscriptItems.LastOrDefault(t =>
                t.Label == "你" && string.Equals(t.Text, gen.VisibleUserText, StringComparison.Ordinal));
            if (lastUser is not null) TranscriptItems.Remove(lastUser);
        }

        gen.ReplyEntry = null;
        gen.SubmittedEntry = null;
    }

    private static void StripIncompleteGenerationFromTranscript(ConversationSession session, SessionGeneration gen)
    {
        var turns = session.Transcript.ToList();
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Label != "你"
                || !string.Equals(turns[i].Text, gen.VisibleUserText, StringComparison.Ordinal))
            {
                continue;
            }

            var removeCount = 1;
            if (i + 1 < turns.Count && turns[i + 1].Label == "Qwen")
                removeCount = 2;
            turns.RemoveRange(i, removeCount);
        }

        session.Transcript = turns;
    }

    /// <summary>
    /// Ensure this job has 你 + streaming Qwen bubbles on the current UI (bound to gen.*Entry).
    /// </summary>
    private void EnsureLiveGenerationBubbles(SessionGeneration gen)
    {
        if (!IsViewingSession(gen.SessionId)) return;

        if (gen.SubmittedEntry is null || !TranscriptItems.Contains(gen.SubmittedEntry))
        {
            var existingUser = TranscriptItems.LastOrDefault(t =>
                t.Label == "你" && string.Equals(t.Text, gen.VisibleUserText, StringComparison.Ordinal));
            gen.SubmittedEntry = existingUser ?? AppendTurn("你", gen.VisibleUserText, PrimaryText);
        }

        var partial = gen.Accumulated.ToString();
        if (gen.ReplyEntry is null || !TranscriptItems.Contains(gen.ReplyEntry))
        {
            var existing = FindStreamingReplyForGeneration(gen);
            if (existing is not null)
            {
                gen.ReplyEntry = existing;
                existing.UpdateStreamingText(partial);
            }
            else
            {
                gen.ReplyEntry = AppendTurn("Qwen", partial, PrimaryText, isStreaming: true);
            }
        }
        else
        {
            gen.ReplyEntry.UpdateStreamingText(partial);
        }
    }

    /// <summary>
    /// Rebuild the visible transcript from session data; attach live generation bubbles without
    /// reinterpreting an older completed Qwen row as the current stream.
    /// </summary>
    private void RebuildVisibleTranscriptFromSession(ConversationSession session, SessionGeneration? liveGen)
    {
        TranscriptItems.Clear();

        // Completed turns only — if live gen's user line was partially captured earlier, skip it here
        // and re-add from liveGen so we never get two 你/Qwen pairs for the same round.
        var turns = session.Transcript;
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (string.Equals(turn.Label, "SYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                if (SystemNoticePolicy.IsModelLifecycleNotice(turn.Text))
                    continue;
                AppendSystem(turn.Text);
                continue;
            }

            if (liveGen is not null
                && turn.Label == "你"
                && string.Equals(turn.Text, liveGen.VisibleUserText, StringComparison.Ordinal))
            {
                // Skip this user and an immediate following Qwen (mid-stream capture of the live job).
                if (i + 1 < turns.Count && turns[i + 1].Label == "Qwen")
                    i++;
                continue;
            }

            AppendTurn(turn.Label, turn.Text, PrimaryText);
        }

        if (liveGen is not null && IsViewingSession(liveGen.SessionId))
        {
            liveGen.SubmittedEntry = null;
            liveGen.ReplyEntry = null;
            EnsureLiveGenerationBubbles(liveGen);
        }
    }

    private void ApplySessionSortMode(SessionSortMode mode)
    {
        _sessions.SortMode = mode;
        RefreshSessionPicker();
    }

    private void PersistSessions()
    {
        if (_sessionStore is null || _sessions.Sessions.Count == 0) return;
        try
        {
            _sessionStore.Save(_sessions);
        }
        catch (Exception error)
        {
            SetNotice($"会话保存失败：{error.Message}", WarningText);
        }
    }

    private void ApplySessionToUi(ConversationSession session, bool announce)
    {
        _history.Clear();
        _history.AddRange(session.History.Select(m => new ChatMessage(m.Role, m.Content)));
        _lastFinishReason = session.LastFinishReason;
        _inputHistory.Clear();
        InputBox.Text = session.DraftInput ?? string.Empty;
        if (InputBox.Text.Length > 0)
            InputBox.SelectionStart = InputBox.Text.Length;

        var liveGen = FindGeneration(session.Id);
        if (session.Transcript.Count == 0 && liveGen is null)
        {
            TranscriptItems.Clear();
            AppendSystem(announce
                ? "新会话已开始。可在顶栏切换或新建会话；切换不会中断其它会话的生成。"
                : "当前会话为空。");
        }
        else if (session.Transcript.Count == 0 && liveGen is not null)
        {
            TranscriptItems.Clear();
            if (announce)
                AppendSystem("新会话已开始。可在顶栏切换或新建会话；切换不会中断其它会话的生成。");
            EnsureLiveGenerationBubbles(liveGen);
        }
        else
        {
            RebuildVisibleTranscriptFromSession(session, liveGen);
            if (announce && TranscriptItems.All(t => t.Label != "SYSTEM"))
            {
                // Rare: restored session with content; no extra system line needed.
            }
        }

        UpdateContinueButtonState();
    }

    private void RefreshSessionPicker()
    {
        _suppressSessionPicker = true;
        try
        {
            SessionPicker.ItemsSource = null;
            SessionPicker.ItemsSource = _sessions.Sessions.ToList();
            SessionPicker.DisplayMemberPath = nameof(ConversationSession.Title);
            SessionPicker.SelectedItem = _sessions.Sessions.FirstOrDefault(s => s.Id == _sessions.Active.Id);
            DeleteSessionButton.IsEnabled = _sessions.Sessions.Count > 1;
        }
        finally
        {
            _suppressSessionPicker = false;
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsActiveSessionGenerating())
        {
            SetNotice("当前会话仍在生成，请停止或等完成后再导出。", WarningText);
            return;
        }

        var turns = ConversationExport.FromTranscript(
            TranscriptItems.Select(item => (item.Label, item.Text)));
        if (turns.Count == 0)
        {
            SetNotice("当前窗口没有可导出的对话内容。", WarningText);
            return;
        }

        try
        {
            var markdown = ConversationExport.ToMarkdown(turns);
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = ConversationExport.SuggestFileName(DateTimeOffset.Now),
            };
            picker.FileTypeChoices.Add("Markdown", [".md"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                SetNotice("已取消导出。", MutedText);
                return;
            }

            await FileIO.WriteTextAsync(file, markdown);
            // Reveal the file in Explorer so the user can open it or its folder.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{file.Path}\"",
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Non-fatal: export already succeeded.
            }

            SetNotice($"已导出并在资源管理器中定位：{file.Path}", MutedText);
        }
        catch (Exception error)
        {
            SetNotice($"导出失败：{error.Message}", ErrorText);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_paths is null) return;
        Directory.CreateDirectory(_paths.LogsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_paths.LogsRoot}\"",
            UseShellExecute = true,
        });
    }

    private async void CopyMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string markdown }) return;
        var text = MarkdownPresentation.ToPlainText(markdown);
        if (text.Length == 0) return;

        try
        {
            var copied = await ClipboardWritePolicy.TryWriteAsync(() =>
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            });
            SetNotice(copied ? "已复制整条消息。" : "剪贴板正被占用，请再试一次。", copied ? MutedText : WarningText);
        }
        catch (Exception error)
        {
            SetNotice($"复制失败：{error.Message}", WarningText);
        }
    }

    private void AppendSystem(string text, bool dedupeIdentical = false)
    {
        if (dedupeIdentical
            && TranscriptItems.Any(item =>
                string.Equals(item.Label, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Text, text, StringComparison.Ordinal)))
        {
            return;
        }

        // Strip stale lifecycle rows restored from older sessions.json (pre-filter versions).
        if (SystemNoticePolicy.IsModelLifecycleNotice(text))
        {
            for (var i = TranscriptItems.Count - 1; i >= 0; i--)
            {
                var item = TranscriptItems[i];
                if (string.Equals(item.Label, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                    && SystemNoticePolicy.IsModelLifecycleNotice(item.Text))
                {
                    TranscriptItems.RemoveAt(i);
                }
            }
        }

        var entry = AppendFormatted("SYSTEM", text, MutedText, 12);
        ScrollTranscriptToEnd(entry);
    }

    private TranscriptEntry AppendTurn(string speaker, string text, Color speakerColor, bool isStreaming = false)
    {
        // Keep the model label as "Qwen" (not "QWEN"); role labels are display text, not enum keys.
        return AppendFormatted(speaker, text, speakerColor, 13, isStreaming);
    }

    private TranscriptEntry AppendFormatted(string label, string text, Color labelColor, double labelSize, bool isStreaming = false)
    {
        var entry = new TranscriptEntry(
            label,
            text,
            new SolidColorBrush(labelColor),
            labelSize,
            TranscriptPresentationPolicy.CanCopy(label),
            isStreaming);
        TranscriptItems.Add(entry);
        return entry;
    }

    private void ScrollTranscriptToEnd(TranscriptEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TranscriptList.ScrollIntoView(entry);
        });
    }

    private void ScrollTranscriptToQuestion(TranscriptEntry questionEntry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TranscriptList.ScrollIntoView(questionEntry, ScrollIntoViewAlignment.Leading);
        });
    }

    private void RefreshComposerBusyState()
    {
        // Stop mode only when the *currently viewed* session owns an in-flight generation.
        var busyHere = IsActiveSessionGenerating();
        _busy = busyHere;
        var atParallelLimit = !busyHere && _generations.Count >= MaxParallelGenerations;
        var composerState = ChatInteractionPolicy.ComposerState(busyHere);
        SendButton.IsEnabled = composerState.ActionEnabled && !atParallelLimit;
        InputBox.IsEnabled = true;
        InputBox.IsReadOnly = false;
        if (busyHere)
        {
            SendButton.Content = ChatInteractionPolicy.ActionLabel(busy: true);
            SendButton.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
            AutomationProperties.SetName(SendButton, "停止生成");
            SendButton.IsEnabled = true;
        }
        else
        {
            SendButton.Content = ChatInteractionPolicy.ActionLabel(busy: false);
            SendButton.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
            AutomationProperties.SetName(SendButton, "发送消息");
            // Other sessions may still generate in the background up to parallel_slots.
            if (atParallelLimit)
                SendButton.IsEnabled = false;
        }

        MoreButton.IsEnabled = true;
        SessionPicker.IsEnabled = true;
        NewSessionButton.IsEnabled = true;
        DeleteSessionButton.IsEnabled = _sessions.Sessions.Count > 1
                                        && ActiveGeneration is null;
        UpdateContinueButtonState();
        if (!busyHere) InputBox.Focus(FocusState.Programmatic);
    }

    private void SetBusy(bool busy)
    {
        // Kept for call sites that still pass a local busy flag; prefer RefreshComposerBusyState.
        _busy = busy;
        RefreshComposerBusyState();
    }

    private void UpdateContinueButtonState()
    {
        if (_busy)
        {
            ContinueButton.IsEnabled = false;
            ContinueButton.Visibility = Visibility.Collapsed;
            return;
        }

        var plan = _sessions.Active.LongFormPlan;
        var canSegment = _settings.SegmentedLongForm
            && LongFormPlanner.CanContinueNextSegment(plan, _history);
        // Drop stale segment plans when the feature is off so the button does not linger.
        if (!_settings.SegmentedLongForm && plan is not null)
            _sessions.Active.LongFormPlan = null;
        var canPrefill = ConversationExport.CanContinue(_history, _lastFinishReason);
        var canContinue = canSegment || canPrefill;
        ContinueButton.IsEnabled = canContinue;
        ContinueButton.Visibility = canContinue ? Visibility.Visible : Visibility.Collapsed;
        if (canSegment)
        {
            ContinueButton.Content = LongFormPlanner.NextSegmentButtonLabel;
            ToolTipService.SetToolTip(ContinueButton, LongFormPlanner.NextSegmentTooltip(plan!));
            AutomationProperties.SetName(ContinueButton, "续写下一段");
        }
        else
        {
            ContinueButton.Content = LongFormPlanner.PrefillContinueButtonLabel;
            ToolTipService.SetToolTip(ContinueButton, LongFormPlanner.PrefillContinueTooltip);
            AutomationProperties.SetName(ContinueButton, "继续生成");
        }
    }

    private string FormatLiveGenerationProgress(SessionGeneration gen, int outputChars)
    {
        var elapsed = gen.StartedTimestamp == 0
            ? 0
            : (int)Stopwatch.GetElapsedTime(gen.StartedTimestamp).TotalSeconds;
        var maxTok = gen.AppliedMaxOutputTokens > 0
            ? gen.AppliedMaxOutputTokens
            : gen.RequestedMaxOutputTokens;
        return LongFormPlanner.FormatGenerationProgress(
            maxTok,
            elapsed,
            outputChars,
            gen.Session.LongFormPlan,
            gen.IsSegmentTurn,
            gen.IsContinuation);
    }

    private static int CountCjkRough(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var n = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff')
                n++;
        }
        return n;
    }

    private void UpdateMemosStatus()
    {
        if (!UseMemosToggle.IsOn)
        {
            SetStatus(MemosStatusText, "○ 记忆关闭", MutedText);
        }
        else if (_memoryQueue.PendingCount > 0)
        {
            SetStatus(MemosStatusText, $"• 记忆保存中 ({_memoryQueue.PendingCount})", WarningText);
        }
        else if (_memoryQueue.LastError is not null)
        {
            SetStatus(MemosStatusText, "× 记忆保存失败", WarningText);
            SetNotice($"MemOS 写入失败：{_memoryQueue.LastError.Message}", WarningText);
        }
        else
        {
            SetStatus(MemosStatusText, "• 记忆已开启", PrimaryText);
        }
    }

    private void UpdateLogStatus()
    {
        SetStatus(LogStatusText, SaveLogsToggle.IsOn ? "• 日志已开启" : "○ 日志关闭", SaveLogsToggle.IsOn ? PrimaryText : MutedText);
    }

    private MarkdownChatLog NewLog()
    {
        if (_paths is null) throw new InvalidOperationException("项目路径尚未初始化");
        var active = _sessions.EnsureBootstrap();
        var stamp = active.MemosSessionId.StartsWith("qwen-local-chat-", StringComparison.Ordinal)
            ? active.MemosSessionId["qwen-local-chat-".Length..]
            : active.Id;
        return new MarkdownChatLog(_paths.LogsRoot, stamp)
        {
            Enabled = SaveLogsToggle.IsOn,
        };
    }

    private void SetNotice(string text, Color color)
    {
        NoticeText.Text = text;
        NoticeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    private static void SetStatus(TextBlock target, string text, Color color)
    {
        target.Text = text;
        target.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }
}
