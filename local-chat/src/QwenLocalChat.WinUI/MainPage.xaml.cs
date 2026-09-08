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
using FolderPicker = Microsoft.Windows.Storage.Pickers.FolderPicker;
using Windows.Storage.Pickers;
using QwenLocalChat.Core;
using Windows.Storage;
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
    private readonly List<string> _attachmentPaths = [];
    private readonly InputHistoryNavigator _inputHistory = new();
    private readonly MemoryWriteQueue _memoryQueue = new();
    private readonly SemaphoreSlim _memosGate = new(1, 1);
    private readonly ConversationSessionWorkspace _sessions = new();
    private bool _suppressSessionPicker;
    private bool _suppressVideoSessionPicker;
    private AppPaths? _paths;
    private SettingsStore? _settingsStore;
    private ModelServiceConfigStore? _modelConfigStore;
    private ModelServiceConfig? _modelConfig;
    private ModelProfileCatalogStore? _modelProfileStore;
    private ModelProfileCatalog? _modelProfiles;
    private VideoModelProfileCatalogStore? _videoProfileStore;
    private VideoModelProfileCatalog? _videoProfiles;
    private ConversationSessionStore? _sessionStore;
    private QwenServiceManager? _modelManager;
    private QwenChatClient? _chatClient;
    private MemosStdioClient? _memos;
    private MarkdownChatLog? _chatLog;
    private LocalChatSettings _settings = LocalChatSettings.SafeDefaults;
    private LocalChatSettings _activeModelSettings = LocalChatSettings.SafeDefaults;
    private bool _loadingSettings;
    private bool _settingsOpenedForVideo;
    private bool _busy;
    private bool _initialized;
    private bool _disposed;
    private ExpandedEditorMode _expandedEditorMode;
    private string? _lastFinishReason;
    private string _initializationStage = "页面加载";
    /// <summary>
    /// In-flight generations keyed by session id. Count is capped by the model
    /// <c>parallel_slots</c> setting so multi-session concurrent replies stay isolated.
    /// </summary>
    private readonly Dictionary<string, SessionGeneration> _generations = new(StringComparer.Ordinal);
    private readonly SessionJobQueue<ChatQueuedTurn> _chatQueue = new(turn => turn.SessionId);
    private bool _drainChatQueue = true;
    /// <summary>Last conversation that used llama-server. Switching threads must drop that slot KV.</summary>
    private string? _lastModelSessionId;

    private enum ExpandedEditorMode { Chat, Video, ReadOnly }

    public ObservableCollection<TranscriptEntry> TranscriptItems { get; } = [];

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // View navigation deliberately does not cancel a running chat, video, or hanhua job.
        var chat = sender.SelectedItem == ChatModeItem;
        var video = sender.SelectedItem == VideoModeItem;
        var hanhua = sender.SelectedItem == HanhuaModeItem;
        VideoModeHost.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        HanhuaModeHost.Visibility = hanhua ? Visibility.Visible : Visibility.Collapsed;
        ChatTranscriptHost.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ChatComposerHost.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ChatFooterHost.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ChatToolsHost.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        VideoSessionToolsHost.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        TextModelToolsHost.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        VideoToolsHost.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        QwenStatusBorder.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        VideoStatusBorder.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(
            StartTextModelPageButton,
            hanhua ? "填字阶段会自动启动本机 Qwen，一般不用点这里。" : null);
        if (video) VideoPanel.RefreshPresetSummary();
        else if (chat) UpdateChatComposerLayout();
    }

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
        ModeSelector.SelectedItem = ChatModeItem;
        Loaded += MainPage_Loaded;
        SizeChanged += (_, _) => UpdateChatComposerLayout();
        InputBox.TextChanged += (_, _) => UpdateChatComposerLayout();
        ExpandedEditorText.TextChanged += (_, _) => UpdateExpandedMentions();
        ExpandedEditorText.SelectionChanged += (_, _) => UpdateExpandedMentions();
        _memoryQueue.StateChanged += MemoryQueue_StateChanged;
        SettingsPanel.SaveRequested += SettingsPanel_SaveRequested;
        SettingsPanel.CloseRequested += SettingsPanel_CloseRequested;
        SettingsPanel.TextImportRequested += async (_, _) => await ImportTextModelAsync();
        SettingsPanel.VideoImportRequested += async (_, _) => await ImportVideoModelAsync();
        SettingsPanel.ReleaseTextModelRequested += async (_, _) => await ReleaseTextModelAsync();
        SettingsPanel.ReleaseVideoModelRequested += async (_, _) => await ReleaseVideoModelAsync();
        // Focus only after close animation finishes — focusing mid-slide causes UI thrash/jank.
        VideoPanel.SettingsRequested += (_, _) => OpenSettings(videoSection: true);
        VideoPanel.ModelStatusChanged += UpdateVideoModelStatus;
        HanhuaPanel.SettingsRequested += (_, _) => OpenSettings(videoSection: false);
        HanhuaPanel.GpuNeedChanged += HanhuaPanel_GpuNeedChanged;
        VideoPanel.ExpandedTextRequested += ShowVideoExpandedText;
        VideoPanel.SessionsChanged += (_, _) => RefreshVideoSessionPicker();
        SettingsPanel.Closed += (_, _) =>
        {
            if (_settingsOpenedForVideo) VideoPanel.FocusSettingsButton();
            else SettingsButton.Focus(FocusState.Programmatic);
        };
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
            _modelProfileStore = new ModelProfileCatalogStore(_paths.ProjectRoot, _paths.ModelProfilesFile);
            _modelProfiles = _modelProfileStore.Load();
            _videoProfileStore = new VideoModelProfileCatalogStore(_paths.ProjectRoot, _paths.VideoModelProfilesFile);
            _videoProfiles = _videoProfileStore.Load();
            LoadSettings();
            VideoPanel.Configure(
                PrepareForVideoAsync,
                () => _settings.VideoGeneration,
                () => _videoProfiles!.Resolve(_settings.SelectedVideoProfileId),
                _paths.ProjectRoot,
                () => _settings.VideoPromptPhrases);
            HanhuaPanel.Configure(
                () => _settings,
                () => _generations.Count > 0,
                () => VideoPanel.IsGenerating,
                PrepareForHanhuaGpuAsync,
                PickHanhuaFolderAsync,
                PersistHanhuaEngine,
                _paths.LocalChatRoot);
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
            RefreshVideoSessionPicker();
            _initializationStage = "准备模型服务";
            UpdateTextModelStatus(ModelLifecycleState.NotStarted);
            UpdateVideoModelStatus(new(ModelLifecycleState.NotStarted));
            if (VideoPanel.HasInterruptedWork)
            {
                ModeSelector.SelectedItem = VideoModeItem;
                if (VideoPanel.InterruptedWorkSummary is { } summary)
                    SetNotice(summary, WarningText);
                try { await StartVideoModelAsync(); }
                catch (Exception error) { SetNotice($"恢复视频任务前启动服务失败：{error.Message}", ErrorText); }
                _ = RestoreInterruptedVideoWorkAsync();
            }
            else if (HanhuaPanel.HasInterruptedWork)
            {
                ModeSelector.SelectedItem = HanhuaModeItem;
                if (HanhuaPanel.InterruptedWorkSummary is { } summary)
                    SetNotice(summary, WarningText);
                var plan = StartupModelPlan.Resolve(_settings.StartupModel);
                if (plan.StartTextModel) await StartTextModelAsync();
            }
            else
            {
                await ApplyStartupModelSelectionAsync();
            }
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
        AppendSystem("欢迎使用 Local AI：聊天和视频模型仅监听本机回环地址，不调用云端 API。");
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
    public Task<AppCloseDecision> RequestCloseDecisionAsync()
    {
        if (_disposed) return Task.FromResult(AppCloseDecision.ExitKeepModel);
        var presentation = AppClosePromptPresentation.Create();

        _closeDecisionTcs?.TrySetResult(AppCloseDecision.Cancel);
        _closeDecisionTcs = new TaskCompletionSource<AppCloseDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClosePromptTitle.Text = "退出 Local AI";
        ClosePromptMessage.Text = presentation.Message;
        CloseStopModelHint.Text = presentation.Hint;
        ClosePromptOverlay.Visibility = Visibility.Visible;
        ClosePromptOverlay.IsHitTestVisible = true;
        CloseStopModelButton.Focus(FocusState.Programmatic);
        return _closeDecisionTcs.Task;
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
        _drainChatQueue = false;
        while (_chatQueue.Dequeue() is not null) { }
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
        await HanhuaPanel.ShutdownAsync();
        await VideoPanel.ShutdownAsync(stopOwnedModel, detachRunningWork: true);
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
        if (_settingsStore is null || _modelProfiles is null || _videoProfiles is null || _modelConfigStore is null) return;
        _loadingSettings = true;
        var loaded = _settingsStore.Load();
        var selectedText = _modelProfiles.Resolve(loaded.Settings.SelectedTextProfileId);
        var selectedVideo = _videoProfiles.Resolve(loaded.Settings.SelectedVideoProfileId);
        _modelConfig = selectedText.Service;
        _modelConfigStore.Save(_modelConfig);
        _settings = loaded.Settings with
        {
            SelectedTextProfileId = selectedText.Id,
            SelectedVideoProfileId = selectedVideo.Id,
        };
        _settings = _settings.WithModelService(_modelConfig);
        if (!string.Equals(loaded.Settings.SelectedTextProfileId, selectedText.Id, StringComparison.Ordinal)
            || !string.Equals(loaded.Settings.SelectedVideoProfileId, selectedVideo.Id, StringComparison.Ordinal))
            _settingsStore.Save(_settings);
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
        if (_paths is null || _modelConfig is null || _modelProfiles is null || _videoProfiles is null)
            throw new InvalidOperationException("模型档案尚未初始化");
        var options = _modelConfig.ToLocalModelOptions(
            _paths.ProjectRoot,
            _paths.ModelLogFile,
            _paths.ModelStartLockFile,
            _paths.ModelLifecycleLogFile);
        _modelManager = new QwenServiceManager(options, new WindowsModelProcessLauncher());
        _chatClient = new QwenChatClient(options.ChatCompletionsUri);
        _activeModelSettings = settings;
        var textProfile = _modelProfiles.Resolve(settings.SelectedTextProfileId);
        var videoProfile = _videoProfiles.Resolve(settings.SelectedVideoProfileId);
        EndpointText.Text = $"LOCAL CORE  /  {_modelConfig.BindHost}:{_modelConfig.Port}";
        TextModelSummaryText.Text = $"{textProfile.DisplayName}  {_modelConfig.BindHost}:{_modelConfig.Port}";
        ChatComposerMetaText.Text = FormatTextComposerMeta(settings);
        VideoModelSummaryText.Text = $"{videoProfile.DisplayName}  {videoProfile.Service.BindHost}:{videoProfile.Service.Port}";
        if (App.Window is MainWindow window) window.SetEndpointSubtitle(_modelConfig.BindHost, _modelConfig.Port);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings(ModeSelector.SelectedItem == VideoModeItem);
    }

    private void PersistHanhuaEngine(HanhuaEngine engine)
    {
        if (_settingsStore is null) return;
        var json = HanhuaEngineCodec.ToJson(engine);
        if (string.Equals(_settings.HanhuaEngine, json, StringComparison.Ordinal)) return;
        _settings = _settings with { HanhuaEngine = json };
        _settingsStore.Save(_settings);
    }

    private async Task<string?> PickHanhuaFolderAsync(string commitButtonText)
    {
        var picker = CreateModelFolderPicker(commitButtonText);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void HanhuaPanel_GpuNeedChanged(object? sender, HanhuaGpuNeedChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.StatusText))
            SetStatus(QwenStatusText, "• " + e.StatusText, WarningText);
        var label = StartTextModelPageButton.Content as string;
        var occupied = label is "启动中" or "已启动";
        StartTextModelPageButton.IsEnabled = !occupied && !HanhuaPanel.IsBusy;
    }

    private async Task PrepareForHanhuaGpuAsync(HanhuaGpuNeed need, CancellationToken cancellationToken)
    {
        if (need == HanhuaGpuNeed.None) return;
        if (need == HanhuaGpuNeed.Qwen)
        {
            if (_settings.EnforceTextVideoModelExclusivity)
                await VideoPanel.ReleaseModelAsync(cancellationToken);
            if (_modelManager is null) throw new InvalidOperationException("文本模型尚未初始化。");
            await _modelManager.EnsureAvailableAsync(cancellationToken);
            if (!await _modelManager.IsHealthyAsync(cancellationToken))
                throw new InvalidOperationException("本机 Qwen 没能自动启动，填字无法继续。");
            UpdateTextModelStatus(_modelManager.OwnsModel ? ModelLifecycleState.Ready : ModelLifecycleState.Reused);
            return;
        }

        if (_modelManager is not null)
            await _modelManager.StopServiceAsync(cancellationToken);
        await VideoPanel.ReleaseModelAsync(cancellationToken);
        if (_modelManager is not null && await _modelManager.IsHealthyAsync(cancellationToken))
            throw new InvalidOperationException("聊天服务仍在占用本地端口；OCR 和嵌字需要先停掉本机 Qwen。");
        UpdateTextModelStatus(ModelLifecycleState.Released);
    }

    private void OpenSettings(bool videoSection)
    {
        if (_modelProfiles is null || _videoProfiles is null) return;
        _settingsOpenedForVideo = videoSection;
        SettingsPanel.Open(_settings, _modelProfiles, _videoProfiles, videoSection);
    }

    private async void SettingsPanel_SaveRequested(object? sender, SettingsSaveRequestedEventArgs e)
    {
        DismissSettingsPanel();
        await ApplyRuntimeSettingsAsync(e.Settings, e.EditedVideoProfile);
    }

    private void SettingsPanel_CloseRequested(object? sender, EventArgs e)
        => DismissSettingsPanel();

    private void DismissSettingsPanel()
    {
        // Keep focus on the panel content until Closed; transferring focus early freezes the slide.
        SettingsPanel.Close();
    }

    private async Task ApplyRuntimeSettingsAsync(LocalChatSettings candidate, VideoModelProfile editedVideoProfile)
    {
        if (_settingsStore is null) return;
        if (VideoPanel.IsGenerating)
        {
            SetNotice("视频仍在生成，暂不能重启聊天模型；请等待完成或先取消视频任务。", WarningText);
            return;
        }

        var previous = _settings;
        var previousModelConfig = _modelConfig;
        var previousProfiles = _modelProfiles;
        var previousVideoProfiles = _videoProfiles;
        try
        {
            if (_modelConfigStore is null || previousModelConfig is null || _modelProfileStore is null
                || _modelProfiles is null || _videoProfiles is null)
                throw new InvalidOperationException("模型档案尚未初始化");
            var selectedText = _modelProfiles.Resolve(candidate.SelectedTextProfileId);
            if (!string.Equals(editedVideoProfile.Id, candidate.SelectedVideoProfileId, StringComparison.Ordinal))
                throw new InvalidOperationException("编辑的视频档案与当前选择不一致。");
            var candidateVideoProfiles = _videoProfiles.AddOrReplace(editedVideoProfile);
            var selectedVideo = candidateVideoProfiles.Resolve(candidate.SelectedVideoProfileId);
            var videoErrors = selectedVideo.Capabilities.Validate(candidate.VideoGeneration);
            if (videoErrors.Count > 0) throw new ArgumentException(string.Join("；", videoErrors), nameof(candidate));
            var candidateModelConfig = candidate.ApplyToModelService(selectedText.Service);
            var candidateProfiles = _modelProfiles.Replace(selectedText with { Service = candidateModelConfig });
            _modelProfileStore.Save(candidateProfiles);
            _modelConfigStore.Save(candidateModelConfig);
            _videoProfileStore!.Save(candidateVideoProfiles);
            _settingsStore.Save(candidate);
            _settings = candidate;
            _modelConfig = candidateModelConfig;
            _modelProfiles = candidateProfiles;
            _videoProfiles = candidateVideoProfiles;
            var videoProfileChanged = !string.Equals(previous.SelectedVideoProfileId, candidate.SelectedVideoProfileId, StringComparison.Ordinal);
            var previousVideoProfile = previousVideoProfiles!.Resolve(previous.SelectedVideoProfileId);
            var videoProfileEdited = previousVideoProfile != editedVideoProfile;
            if (videoProfileChanged || videoProfileEdited)
                await VideoPanel.ResetProfileAsync(stopModel: VideoPanel.HasInitializedRuntime);
            VideoPanel.RefreshPresetSummary();
            TextModelSummaryText.Text = $"{selectedText.DisplayName}  {candidateModelConfig.BindHost}:{candidateModelConfig.Port}";
            ChatComposerMetaText.Text = FormatTextComposerMeta(candidate);
            VideoModelSummaryText.Text = $"{selectedVideo.DisplayName}  {selectedVideo.Service.BindHost}:{selectedVideo.Service.Port}";
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
                        _modelProfiles = previousProfiles;
                        if (previousProfiles is not null) _modelProfileStore.Save(previousProfiles);
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
            _modelProfiles = previousProfiles;
            _videoProfiles = previousVideoProfiles;
            if (previousProfiles is not null && _modelProfileStore is not null)
            {
                try { _modelProfileStore.Save(previousProfiles); } catch { }
            }
            if (previousModelConfig is not null && _modelConfigStore is not null)
            {
                try { _modelConfigStore.Save(previousModelConfig); } catch { }
            }
            if (previousVideoProfiles is not null && _videoProfileStore is not null)
            {
                try { _videoProfileStore.Save(previousVideoProfiles); } catch { }
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
            UpdateTextModelStatus(ModelLifecycleState.Starting);
            var availability = await _modelManager.EnsureAvailableAsync();
            UpdateTextModelStatus(availability.Reused ? ModelLifecycleState.Reused : ModelLifecycleState.Ready);
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
            UpdateTextModelStatus(ModelLifecycleState.Failed, error.Message);
            SetNotice($"模型启动失败：{error.Message}", ErrorText);
            if (throwOnFailure) throw;
        }
    }

    private async Task RestoreInterruptedVideoWorkAsync()
    {
        try
        {
            await VideoPanel.RestoreInterruptedWorkAsync();
        }
        catch (Exception error)
        {
            SetNotice($"恢复视频任务失败：{error.Message}", ErrorText);
        }
    }

    private async Task ApplyStartupModelSelectionAsync()
    {
        var plan = StartupModelPlan.Resolve(_settings.StartupModel);
        switch (plan.Mode)
        {
            case StartupModelSelection.Video:
                ModeSelector.SelectedItem = VideoModeItem;
                if (plan.StartVideoModel) await StartVideoModelAsync();
                break;
            case StartupModelSelection.None:
                if (plan.ChatNotice is not null) SetNotice(plan.ChatNotice, MutedText);
                if (_modelManager is not null && await _modelManager.IsHealthyAsync())
                    UpdateTextModelStatus(ModelLifecycleState.Reused);
                await VideoPanel.RefreshModelStatusAsync();
                break;
            default:
                ModeSelector.SelectedItem = ChatModeItem;
                if (plan.ChatNotice is not null) SetNotice(plan.ChatNotice, WarningText);
                if (plan.StartTextModel) await StartTextModelAsync();
                break;
        }
    }

    private async Task PrepareForVideoAsync(CancellationToken cancellationToken)
    {
        var hanhuaBusy = HanhuaArbitration.RefuseVideo(HanhuaPanel.IsBusy);
        if (hanhuaBusy is not null)
            throw new InvalidOperationException(hanhuaBusy);
        if (_generations.Count > 0)
            throw new InvalidOperationException("仍有聊天回复正在生成，请先在对应会话停止后再生成视频。");
        if (_modelManager is null || !_settings.EnforceTextVideoModelExclusivity) return;

        SetStatus(QwenStatusText, "• 正在释放聊天模型", WarningText);
        await _modelManager.StopServiceAsync(cancellationToken);
        if (await _modelManager.IsHealthyAsync(cancellationToken))
            throw new InvalidOperationException("聊天服务仍在占用本地端口；为避免显存冲突，已拒绝启动视频模型。");
        UpdateTextModelStatus(ModelLifecycleState.Released);
    }

    private FolderPicker CreateModelFolderPicker(string commitButtonText)
    {
        if (App.Window is not MainWindow window)
            throw new InvalidOperationException("无法取得 Local AI 主窗口，目录选择器未打开。");
        return new FolderPicker(window.AppWindowId) { CommitButtonText = commitButtonText };
    }

    private async Task ImportTextModelAsync()
    {
        if (_paths is null || _modelProfileStore is null || _modelProfiles is null || SettingsPanel.CurrentTextProfile is not { } template)
            return;
        SettingsPanel.SetModelOperationStatus(video: false, "正在选择本地目录…", busy: true);
        SetNotice("正在打开文本模型目录选择器…", WarningText);
        try
        {
            var picker = CreateModelFolderPicker("导入唯一 GGUF");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                SettingsPanel.SetModelOperationStatus(false, "已取消目录选择。", false);
                SetNotice("已取消文本模型目录选择。", MutedText);
                return;
            }
            var imported = TextModelProfileImport.CreateProfile(_modelProfiles, template, TextModelProfileImport.FindSingleGguf(folder.Path));
            _modelProfiles = _modelProfiles.AddOrReplace(imported);
            _modelProfileStore.Save(_modelProfiles);
            if (_modelManager is not null) await _modelManager.StopServiceAsync();
            _modelConfig = imported.Service;
            _modelConfigStore?.Save(_modelConfig);
            _settings = _settings with { SelectedTextProfileId = imported.Id };
            _settingsStore?.Save(_settings);
            CreateModelClients(_settings);
            SettingsPanel.RefreshTextProfiles(_modelProfiles, imported.Id);
            SettingsPanel.SetModelOperationStatus(false, $"已导入 {imported.DisplayName}。", false);
            SetNotice($"已导入文本模型：{imported.DisplayName}。", MutedText);
        }
        catch (Exception error)
        {
            SettingsPanel.SetModelOperationStatus(false, $"未导入：{error.Message}", false);
            SetNotice($"文本模型目录选择失败：{error.Message}", ErrorText);
        }
    }

    private async Task ImportVideoModelAsync()
    {
        if (_videoProfileStore is null || _videoProfiles is null || _settingsStore is null || SettingsPanel.CurrentVideoProfile is not { } template)
            return;
        SettingsPanel.SetModelOperationStatus(video: true, "正在选择 ComfyUI 根目录…", busy: true);
        SetNotice("正在打开视频模型目录选择器…", WarningText);
        try
        {
            var picker = CreateModelFolderPicker("导入兼容视频模型");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) { SettingsPanel.SetModelOperationStatus(true, "已取消目录选择。", false); SetNotice("已取消视频模型目录选择。", MutedText); return; }
            var imported = VideoModelProfileImport.CreateCompatibleProfile(_videoProfiles, template, folder.Path);
            await VideoPanel.ResetProfileAsync(stopModel: true);
            _videoProfiles = _videoProfiles.AddOrReplace(imported);
            _videoProfileStore.Save(_videoProfiles);
            _settings = _settings with { SelectedVideoProfileId = imported.Id };
            _settingsStore.Save(_settings);
            SettingsPanel.RefreshVideoProfiles(_videoProfiles, imported.Id);
            VideoPanel.RefreshPresetSummary();
            VideoModelSummaryText.Text = $"{imported.DisplayName}  {imported.Service.BindHost}:{imported.Service.Port}";
            SettingsPanel.SetModelOperationStatus(true, $"已导入 {imported.DisplayName}；沿用所选档案的兼容工作流，首次生成前会校验节点。", false);
            SetNotice($"已导入视频模型：{imported.DisplayName}。", MutedText);
        }
        catch (Exception error) { SettingsPanel.SetModelOperationStatus(true, $"未导入：{error.Message}", false); SetNotice($"视频模型目录选择失败：{error.Message}", ErrorText); }
    }

    private async Task StartTextModelAsync()
    {
        if (HanhuaPanel.IsBusy)
        {
            SetNotice("汉化正在占用 GPU，请等它结束或取消后再启动文本模型。", WarningText);
            return;
        }
        UpdateTextModelStatus(ModelLifecycleState.Starting);
        try { if (_settings.EnforceTextVideoModelExclusivity) await VideoPanel.ReleaseModelAsync(); await InitializeModelAsync(throwOnFailure: true); }
        catch { }
    }

    private async Task ReleaseTextModelAsync()
    {
        UpdateTextModelStatus(ModelLifecycleState.Starting, "正在释放");
        try { if (_modelManager is not null) await _modelManager.StopServiceAsync(); _lastModelSessionId = null; UpdateTextModelStatus(ModelLifecycleState.Released); }
        catch (Exception error) { UpdateTextModelStatus(ModelLifecycleState.Failed, $"释放失败：{error.Message}"); }
    }

    private async Task StartVideoModelAsync()
    {
        try { await VideoPanel.StartModelAsync(); }
        catch (Exception error) { SetNotice($"视频模型启动失败：{error.Message}", ErrorText); }
    }

    private async Task ReleaseVideoModelAsync()
    {
        try { await VideoPanel.ReleaseModelAsync(); }
        catch (Exception error) { UpdateVideoModelStatus(new(ModelLifecycleState.Failed, $"释放失败：{error.Message}")); }
    }

    private async void StartTextModelPageButton_Click(object sender, RoutedEventArgs e) => await StartTextModelAsync();

    private async void StartVideoModelPageButton_Click(object sender, RoutedEventArgs e) => await StartVideoModelAsync();

    private void UpdateTextModelStatus(ModelLifecycleState state, string? detail = null)
    {
        var (text, color, busy, ready) = state switch
        {
            ModelLifecycleState.Starting when detail == "正在释放" => ("• 聊天模型正在释放", WarningText, true, false),
            ModelLifecycleState.Starting => ("• 聊天模型正在启动", WarningText, true, false),
            ModelLifecycleState.Ready => ("• 聊天模型已启动", PrimaryText, false, true),
            ModelLifecycleState.Reused => ("• 聊天模型已复用", PrimaryText, false, true),
            ModelLifecycleState.Failed => ("× 聊天模型启动失败", ErrorText, false, false),
            ModelLifecycleState.Released => ("○ 聊天模型已释放", MutedText, false, false),
            _ => ("○ 聊天模型未启动", MutedText, false, false),
        };
        SetStatus(QwenStatusText, text, color);
        StartTextModelPageButton.Content = busy ? "启动中" : ready ? "已启动" : state == ModelLifecycleState.Failed ? "重试" : "启动";
        StartTextModelPageButton.IsEnabled = !busy && !ready && !HanhuaPanel.IsBusy;
        SettingsPanel.SetModelOperationStatus(false, detail is null ? text.TrimStart('•', '×', '○', ' ') : $"{text.TrimStart('•', '×', '○', ' ')}：{detail}", busy);
    }

    private void UpdateVideoModelStatus(ModelLifecycleUpdate update)
    {
        var (text, color, busy, ready) = update.State switch
        {
            ModelLifecycleState.Starting => ("• 视频模型正在启动", WarningText, true, false),
            ModelLifecycleState.Ready => ("• 视频模型已启动", PrimaryText, false, true),
            ModelLifecycleState.Reused => ("• 视频模型已复用", PrimaryText, false, true),
            ModelLifecycleState.Failed => ("× 视频模型启动失败", ErrorText, false, false),
            ModelLifecycleState.Released => ("○ 视频模型已释放", MutedText, false, false),
            _ => ("○ 视频模型未启动", MutedText, false, false),
        };
        SetStatus(VideoModelStatusText, text, color);
        StartVideoModelPageButton.Content = busy ? "启动中" : ready ? "已启动" : update.State == ModelLifecycleState.Failed ? "重试" : "启动";
        StartVideoModelPageButton.IsEnabled = !busy && !ready;
        SettingsPanel.SetModelOperationStatus(true, update.Detail is null ? text.TrimStart('•', '×', '○', ' ') : $"{text.TrimStart('•', '×', '○', ' ')}：{update.Detail}", busy);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsActiveSessionGenerating())
        {
            try { ActiveGeneration?.Cts.Cancel(); } catch { }
            SetNotice("正在停止当前会话的生成…", WarningText);
            return;
        }

        if (CancelActiveChatQueue())
            return;

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
        if (sender is not TextBox input) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (!shift && (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down) && !input.Text.Contains('\n'))
        {
            e.Handled = true;
            var recalled = e.Key == VirtualKey.Up
                ? _inputHistory.Previous(input.Text)
                : _inputHistory.Next(input.Text);
            input.Text = recalled;
            input.SelectionStart = input.Text.Length;
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

    private void UpdateChatComposerLayout()
        => ComposerTextBoxLayout.Apply(InputBox, ActualHeight);

    private void ExpandChatInputButton_Click(object sender, RoutedEventArgs e)
        => ShowExpandedEditor(ExpandedEditorMode.Chat, "展开编辑聊天消息", InputBox.Text, "发送");

    private void ShowVideoExpandedText(string title, string text, bool readOnly)
        => ShowExpandedEditor(readOnly ? ExpandedEditorMode.ReadOnly : ExpandedEditorMode.Video, title, text, readOnly ? "关闭" : "生成视频");

    private void ShowExpandedEditor(ExpandedEditorMode mode, string title, string text, string actionLabel)
    {
        _expandedEditorMode = mode;
        ExpandedEditorTitle.Text = title;
        ExpandedEditorText.Text = text;
        ExpandedEditorText.IsReadOnly = mode == ExpandedEditorMode.ReadOnly;
        ExpandedEditorHint.Text = mode == ExpandedEditorMode.ReadOnly
            ? "完整内容可选择，也可以复制到剪贴板"
            : mode == ExpandedEditorMode.Chat
                ? "Enter 发送    Shift+Enter 换行"
                : "Enter 生成    Shift+Enter 换行    输入 @ 插入参考标签";
        ExpandedEditorCopyButton.Visibility = mode == ExpandedEditorMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ExpandedEditorCloseButton.Content = mode == ExpandedEditorMode.ReadOnly ? "关闭" : "收起";
        ExpandedEditorActionButton.Content = actionLabel;
        ExpandedEditorActionButton.Visibility = mode == ExpandedEditorMode.ReadOnly ? Visibility.Collapsed : Visibility.Visible;
        if (ExpandedInsertTemplateButton is not null)
            ExpandedInsertTemplateButton.Visibility =
                mode == ExpandedEditorMode.Video && VideoPanel.CanInsertPromptTemplate
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (ExpandedTemplatePicker is not null)
            ExpandedTemplatePicker.Visibility = Visibility.Collapsed;
        HideExpandedMentions();
        ExpandedEditorOverlay.Visibility = Visibility.Visible;
        ExpandedEditorOverlay.IsHitTestVisible = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            ExpandedEditorText.SelectionStart = ExpandedEditorText.Text.Length;
            ExpandedEditorText.Focus(FocusState.Programmatic);
        });
    }

    private void CloseExpandedEditorButton_Click(object sender, RoutedEventArgs e)
        => CloseExpandedEditor(commitText: true);

    private void CloseExpandedEditor(bool commitText)
    {
        if (commitText)
        {
            if (_expandedEditorMode == ExpandedEditorMode.Chat) InputBox.Text = ExpandedEditorText.Text;
            else if (_expandedEditorMode == ExpandedEditorMode.Video) VideoPanel.ApplyExpandedPrompt(ExpandedEditorText.Text);
        }
        HideExpandedMentions();
        ExpandedEditorOverlay.Visibility = Visibility.Collapsed;
        ExpandedEditorOverlay.IsHitTestVisible = false;
        if (_expandedEditorMode == ExpandedEditorMode.Chat) InputBox.Focus(FocusState.Programmatic);
    }

    private async void ExpandedEditorActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_expandedEditorMode == ExpandedEditorMode.ReadOnly) { CloseExpandedEditor(commitText: false); return; }
        var mode = _expandedEditorMode;
        var text = ExpandedEditorText.Text;
        CloseExpandedEditor(commitText: true);
        if (mode == ExpandedEditorMode.Chat) await SendAsync();
        else await VideoPanel.GenerateFromExpandedPromptAsync(text);
    }

    private async void ExpandedEditorText_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_expandedEditorMode == ExpandedEditorMode.ReadOnly) return;
        if (_expandedEditorMode == ExpandedEditorMode.Video && IsExpandedMentionListOpen())
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideExpandedMentions();
                return;
            }
            if (e.Key is VirtualKey.Down or VirtualKey.Up)
            {
                e.Handled = true;
                MoveExpandedMentionSelection(e.Key == VirtualKey.Down ? 1 : -1);
                return;
            }
            if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
            {
                e.Handled = true;
                InsertSelectedExpandedMention();
                return;
            }
        }
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (ChatInteractionPolicy.ResolveEnter(e.Key == VirtualKey.Enter, shift) != ComposerKeyAction.Send) return;
        e.Handled = true;
        var mode = _expandedEditorMode;
        var text = ExpandedEditorText.Text;
        CloseExpandedEditor(commitText: true);
        if (mode == ExpandedEditorMode.Chat) await SendAsync();
        else await VideoPanel.GenerateFromExpandedPromptAsync(text);
    }

    private bool IsExpandedMentionListOpen()
        => ExpandedMentionList is not null && ExpandedMentionList.Visibility == Visibility.Visible;

    private void UpdateExpandedMentions()
    {
        if (ExpandedMentionList is null || ExpandedEditorText is null) return;
        if (_expandedEditorMode != ExpandedEditorMode.Video)
        {
            HideExpandedMentions();
            return;
        }

        var candidates = VideoPanel.MentionCandidates();
        var text = ExpandedEditorText.Text ?? "";
        var caret = ExpandedEditorText.SelectionStart;
        if (candidates.Count == 0
            || !VideoPromptMentions.TryGetActiveQuery(text, caret, out _, out var query))
        {
            HideExpandedMentions();
            return;
        }

        var filtered = VideoPromptMentions.Filter(candidates, query);
        if (filtered.Count == 0)
        {
            HideExpandedMentions();
            return;
        }

        ExpandedMentionList.ItemsSource = filtered.ToArray();
        if (ExpandedMentionList.SelectedIndex < 0)
            ExpandedMentionList.SelectedIndex = 0;
        ExpandedMentionList.Visibility = Visibility.Visible;
    }

    private void HideExpandedMentions()
    {
        if (ExpandedMentionList is null) return;
        ExpandedMentionList.Visibility = Visibility.Collapsed;
    }

    private void MoveExpandedMentionSelection(int delta)
    {
        if (ExpandedMentionList.Items.Count == 0) return;
        var next = ExpandedMentionList.SelectedIndex + delta;
        if (next < 0) next = ExpandedMentionList.Items.Count - 1;
        if (next >= ExpandedMentionList.Items.Count) next = 0;
        ExpandedMentionList.SelectedIndex = next;
        if (ExpandedMentionList.SelectedItem is not null)
            ExpandedMentionList.ScrollIntoView(ExpandedMentionList.SelectedItem);
    }

    private void ExpandedMentionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoPromptMention item)
            InsertExpandedMention(item.Tag);
    }

    private void InsertSelectedExpandedMention()
    {
        var item = ExpandedMentionList.SelectedItem as VideoPromptMention
                   ?? ExpandedMentionList.Items.OfType<VideoPromptMention>().FirstOrDefault();
        if (item is not null)
            InsertExpandedMention(item.Tag);
    }

    private void InsertExpandedMention(string tag)
    {
        var text = ExpandedEditorText.Text ?? "";
        var caret = ExpandedEditorText.SelectionStart;
        if (!VideoPromptMentions.TryGetActiveQuery(text, caret, out var atIndex, out _))
        {
            HideExpandedMentions();
            return;
        }

        var next = VideoPromptMentions.Insert(text, atIndex, caret, tag, out var newCaret);
        ExpandedEditorText.Text = next;
        ExpandedEditorText.SelectionStart = newCaret;
        HideExpandedMentions();
        ExpandedEditorText.Focus(FocusState.Programmatic);
    }

    private void ExpandedInsertTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExpandedTemplatePicker is null) return;
        if (ExpandedTemplatePicker.Visibility == Visibility.Visible)
        {
            ExpandedTemplatePicker.Visibility = Visibility.Collapsed;
            return;
        }
        ExpandedTemplatePicker.Refresh(
            VideoPanel.CurrentConditioningMode,
            VideoPanel.CurrentTemplateMedia());
        ExpandedTemplatePicker.Visibility = Visibility.Visible;
    }

    private void ExpandedTemplatePicker_InsertRequested(object sender, VideoPromptTemplateInsert insert)
    {
        VideoPanel.ApplyPromptTemplate(insert.Title, insert.Text, ExpandedEditorText.Text);
        ExpandedEditorText.Text = VideoPanel.ComposerText;
        if (ExpandedTemplatePicker is not null)
            ExpandedTemplatePicker.Visibility = Visibility.Collapsed;
    }

    private void ExpandedTemplatePicker_Cancelled(object sender, EventArgs e)
    {
        if (ExpandedTemplatePicker is not null)
            ExpandedTemplatePicker.Visibility = Visibility.Collapsed;
    }

    private void ExpandedEditorCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ExpandedEditorText.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private bool IsActiveSessionGenerating()
        => ActiveGeneration is not null;

    private bool IsViewingSession(string sessionId)
        => _sessions.Active.Id == sessionId;

    private async Task SendAsync(
        string? forcedUserMessage = null,
        string? displayUserLabel = null,
        bool isContinuation = false,
        bool isSegmentContinue = false,
        bool skipAttachments = false)
    {
        if (_modelManager is null || _chatClient is null) return;

        var hanhuaBusy = HanhuaArbitration.RefuseChat(HanhuaPanel.IsBusy);
        if (hanhuaBusy is not null)
        {
            SetNotice(hanhuaBusy, WarningText);
            return;
        }

        if (VideoPanel.IsGenerating)
        {
            SetNotice("视频仍在生成；请先等待完成或取消视频任务。", WarningText);
            return;
        }

        var owner = _sessions.Active;
        if (FindGeneration(owner.Id) is not null)
        {
            SetNotice("当前会话仍在生成中，请先点「停止」或等完成后再发。", WarningText);
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
            var prompt = forcedUserMessage ?? submittedInput.Trim();
            if (skipAttachments)
            {
                userMessage = prompt;
                visibleUserText = displayUserLabel ?? prompt;
            }
            else
            {
                var loaded = ChatAttachmentComposer.Load(_attachmentPaths, CurrentAttachmentPolicy());
                if (loaded.Errors.Count > 0)
                    SetNotice(string.Join(" ", loaded.Errors), WarningText);
                userMessage = ChatAttachmentComposer.BuildModelMessage(prompt, loaded.Attachments);
                if (userMessage.Length == 0)
                {
                    if (loaded.Errors.Count == 0)
                        SetNotice("请输入消息，或添加可读的文本附件。", WarningText);
                    return;
                }
                visibleUserText = displayUserLabel ?? ChatAttachmentComposer.BuildVisibleMessage(prompt, loaded.Attachments);
                _attachmentPaths.Clear();
                RebuildChatAttachmentChips();
            }
            if (userMessage.Length == 0)
            {
                SetNotice("请输入消息，或添加可读的文本附件。", WarningText);
                return;
            }
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

        var turn = new ChatQueuedTurn(
            owner.Id,
            userMessage,
            visibleUserText,
            submittedInput,
            isContinuation && !isSegmentContinue,
            isSegmentTurn,
            !(isContinuation && !isSegmentContinue) && UseMemosToggle.IsOn,
            SaveLogsToggle.IsOn,
            owner.MemosSessionId,
            requestHistory,
            requestedMax,
            RestoreInputOnCancel: !isContinuation && !isSegmentContinue && forcedUserMessage is null);

        if (_generations.Count >= MaxParallelGenerations)
        {
            EnqueueActiveChatTurn(turn);
            if (turn.RestoreInputOnCancel)
                InputBox.Text = string.Empty;
            return;
        }

        if (turn.RestoreInputOnCancel)
            InputBox.Text = string.Empty;
        await RunChatTurnAsync(turn, owner);
    }

    private async Task RunChatTurnAsync(ChatQueuedTurn turn, ConversationSession owner)
    {
        // Snapshot history/session before any await so a later session switch cannot re-route this job.
        var gen = new SessionGeneration
        {
            SessionId = turn.SessionId,
            Session = owner,
            UserMessage = turn.UserMessage,
            VisibleUserText = turn.VisibleUserText,
            IsContinuation = turn.IsContinuation,
            IsSegmentTurn = turn.IsSegmentTurn,
            UseMemos = turn.UseMemos,
            SaveLog = turn.SaveLog,
            MemosSessionId = turn.MemosSessionId,
            RequestHistory = turn.RequestHistory.ToList(),
            RequestedMaxOutputTokens = turn.RequestedMaxOutputTokens,
        };

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
                UpdateTextModelStatus(ModelLifecycleState.Starting);
            if (_settings.EnforceTextVideoModelExclusivity)
                await VideoPanel.ReleaseModelAsync(sendToken);
            try
            {
                await _modelManager.EnsureAvailableAsync(sendToken);
            }
            catch (Exception error)
            {
                if (IsViewingSession(gen.SessionId)) UpdateTextModelStatus(ModelLifecycleState.Failed, error.Message);
                throw;
            }
            if (IsViewingSession(gen.SessionId))
                UpdateTextModelStatus(_modelManager.OwnsModel ? ModelLifecycleState.Ready : ModelLifecycleState.Reused);

            var resetServerCache = ModelSessionCache.ShouldReset(
                _lastModelSessionId, gen.SessionId, _generations.Count);
            if (resetServerCache)
            {
                try { await _modelManager.EraseIdleSlotsAsync(sendToken); }
                catch (Exception) { /* older llama-server builds may lack /slots */ }
            }

            string? memoryContext = null;
            MemosStdioClient? memoryClient = null;
            if (gen.UseMemos)
            {
                try
                {
                    if (IsViewingSession(gen.SessionId))
                        SetStatus(MemosStatusText, "• 记忆召回中", WarningText);
                    memoryClient = await EnsureMemosAsync();
                    memoryContext = await memoryClient.RecallAsync(gen.UserMessage, _settings.MemosTopK, sendToken);
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
                reasoningEnabled: _activeModelSettings.ReasoningEnabled && !gen.IsContinuation)
                with
                {
                    SlotId = 0,
                    CachePrompt = !resetServerCache,
                };
            _lastModelSessionId = gen.SessionId;

            var previousAssistant = gen.IsContinuation ? gen.RequestHistory[^1].Content : string.Empty;

            if (IsViewingSession(gen.SessionId))
            {
                if (gen.IsContinuation)
                {
                    // Extend the existing assistant bubble — do not invent a fake user turn.
                    replyEntry = TranscriptItems.LastOrDefault(item => TranscriptPresentationPolicy.IsAssistant(item.Label));
                    if (replyEntry is not null)
                    {
                        replyEntry.ResumeStreaming(previousAssistant);
                        gen.ReplyEntry = replyEntry;
                        ScrollTranscriptToQuestion(replyEntry);
                    }
                    else
                    {
                        replyEntry = AppendTurn(TranscriptPresentationPolicy.AssistantLabel, previousAssistant, PrimaryText, isStreaming: true);
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
                    replyEntry = AppendTurn(TranscriptPresentationPolicy.AssistantLabel, string.Empty, PrimaryText, isStreaming: true);
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
                        var live = gen.ReplyEntry ?? TranscriptItems.LastOrDefault(item => TranscriptPresentationPolicy.IsAssistant(item.Label));
                        live?.Complete(finalBody);
                        gen.ReplyEntry = live;
                        if (live is not null)
                            ScrollTranscriptToQuestion(live);
                    }
                    else
                    {
                        replyEntry = AppendTurn(TranscriptPresentationPolicy.AssistantLabel, finalBody, PrimaryText);
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
                turn.SubmittedInput,
                restoreInput: turn.RestoreInputOnCancel);
        }
        catch (Exception error)
        {
            CommitGenerationFailed(
                gen,
                error,
                turn.SubmittedInput,
                restoreInput: turn.RestoreInputOnCancel);
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
            await StartNextChatQueuedAsync();
        }
    }

    private void EnqueueActiveChatTurn(ChatQueuedTurn turn)
    {
        var position = _chatQueue.Enqueue(turn);
        var status = SessionJobQueue<ChatQueuedTurn>.FormatStatus(position);
        SetNotice(status, MutedText);
        if (IsViewingSession(turn.SessionId))
            AppendSystem(status, dedupeIdentical: true);
        CaptureActiveSession();
        PersistSessions();
        RefreshComposerBusyState();
        RefreshSessionPicker();
    }

    private bool CancelActiveChatQueue()
    {
        if (!_chatQueue.Remove(_sessions.Active.Id)) return false;
        SetNotice("已取消排队。", MutedText);
        RefreshQueuedChatNotices();
        RefreshComposerBusyState();
        return true;
    }

    private void RefreshQueuedChatNotices()
    {
        var position = _chatQueue.Position(_sessions.Active.Id);
        if (position is int place)
            SetNotice(SessionJobQueue<ChatQueuedTurn>.FormatStatus(place), MutedText);
    }

    private async Task StartNextChatQueuedAsync()
    {
        if (!_drainChatQueue || _generations.Count >= MaxParallelGenerations) return;
        if (VideoPanel.IsGenerating) return;
        var next = _chatQueue.Dequeue();
        if (next is null) return;
        var session = _sessions.Find(next.SessionId);
        if (session is null)
        {
            await StartNextChatQueuedAsync();
            return;
        }
        RefreshQueuedChatNotices();
        await RunChatTurnAsync(next, session);
    }

    private TranscriptEntry? FindStreamingReplyEntry()
        => TranscriptItems.LastOrDefault(item => TranscriptPresentationPolicy.IsAssistant(item.Label) && item.IsStreaming);

    /// <summary>Locate this job's streaming bubble without stealing another generation's row.</summary>
    private TranscriptEntry? FindStreamingReplyForGeneration(SessionGeneration gen)
    {
        if (gen.ReplyEntry is not null && TranscriptItems.Contains(gen.ReplyEntry))
            return gen.ReplyEntry;

        // Prefer the assistant row after this generation's user bubble.
        if (gen.SubmittedEntry is not null && TranscriptItems.Contains(gen.SubmittedEntry))
        {
            var idx = TranscriptItems.IndexOf(gen.SubmittedEntry);
            for (var i = idx + 1; i < TranscriptItems.Count; i++)
            {
                if (TranscriptPresentationPolicy.IsAssistant(TranscriptItems[i].Label) && TranscriptItems[i].IsStreaming)
                    return TranscriptItems[i];
            }
        }

        // Fallback: use the last streaming assistant only when its preceding user matches this generation.
        for (var i = TranscriptItems.Count - 1; i >= 0; i--)
        {
            var item = TranscriptItems[i];
            if (!TranscriptPresentationPolicy.IsAssistant(item.Label) || !item.IsStreaming) continue;
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
    /// Upsert this round's user/assistant pair into a session transcript (idempotent — fixes double replies).
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
            if (i + 1 < turns.Count && TranscriptPresentationPolicy.IsAssistant(turns[i + 1].Label))
                removeCount = 2;
            turns.RemoveRange(i, removeCount);
            // Keep scanning in case older partial duplicates exist.
        }

        turns.Add(new ConversationTurn("你", gen.VisibleUserText));
        turns.Add(new ConversationTurn(TranscriptPresentationPolicy.AssistantLabel, assistantMessage));
        session.Transcript = turns;
        session.UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>Replace the last assistant body after assistant-prefill continuation.</summary>
    private static void UpsertContinuedAssistantTranscript(ConversationSession session, string assistantMessage)
    {
        var turns = session.Transcript
            .Where(t => SystemNoticePolicy.ShouldPersist(t.Label, t.Text))
            .ToList();
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (!TranscriptPresentationPolicy.IsAssistant(turns[i].Label)) continue;
            turns[i] = new ConversationTurn(TranscriptPresentationPolicy.AssistantLabel, assistantMessage);
            session.Transcript = turns;
            session.UpdatedAt = DateTimeOffset.Now;
            return;
        }

        turns.Add(new ConversationTurn(TranscriptPresentationPolicy.AssistantLabel, assistantMessage));
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
            // Idempotent transcript write for this round avoids duplicate assistant rows after mid-stream capture.
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
            RestoreQuestionAnchorAfterRebuild(gen.Session, gen.VisibleUserText);
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
                    $"已停止（约 {cjk} 字，用时 {elapsed}s）。可点「{LongFormPlanner.PrefillContinueButtonLabel}」从中断处接着写。",
                    WarningText);
            else if (remaining is not null && remaining.Active)
                SetNotice(
                    $"第 {remaining.CompletedSegments}/{remaining.TotalSegments} 段完成（约 {cjk} 字，用时 {elapsed}s，全文约 {remaining.TargetChars} 字）。点「{LongFormPlanner.NextSegmentButtonLabel}」续写。",
                    MutedText);
            else if (gen.IsSegmentTurn && remaining is null)
                SetNotice($"长文全部分段已完成（末段约 {cjk} 字，用时 {elapsed}s）。", MutedText);
            else if (string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                SetNotice(
                    $"已达本轮上限 {maxOutputTokens} token（约 {cjk} 字，用时 {elapsed}s）。可点「{LongFormPlanner.PrefillContinueButtonLabel}」接着写，或在设置中提高最大输出。",
                    WarningText);
            else if (gen.IsContinuation)
                SetNotice($"已从中断处继续（本段约 {cjk} 字，用时 {elapsed}s）。", MutedText);
            else if (elapsed >= 3 || cjk >= 200)
                SetNotice($"本轮完成（约 {cjk} 字，用时 {elapsed}s，上限 {maxOutputTokens} token）。", MutedText);
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
        CancelActiveChatQueue();
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
        CancelActiveChatQueue();
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
        RefreshQueuedChatNotices();
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

        // While a generation is in flight, do not persist its partial user/assistant pair into sessions.json
        // via Capture — CommitGenerationSuccess upserts the final pair once (prevents double replies).
        IEnumerable<(string Label, string Text)> turns;
        if (FindGeneration(session.Id) is { } liveGen)
        {
            turns = TranscriptItems
                .Where(item => SystemNoticePolicy.ShouldPersist(item.Label, item.Text))
                .Where(item =>
                    !(item.Label == "你" && string.Equals(item.Text, liveGen.VisibleUserText, StringComparison.Ordinal))
                    && !(TranscriptPresentationPolicy.IsAssistant(item.Label) && (item.IsStreaming || ReferenceEquals(item, liveGen.ReplyEntry))))
                .Select(item => (item.Label, item.Text));
            session.Capture(_history, turns, _lastFinishReason, draftInput: InputBox.Text, draftAttachmentPaths: _attachmentPaths);
        }
        else
        {
            session.Capture(
                _history,
                TranscriptItems
                    .Where(item => SystemNoticePolicy.ShouldPersist(item.Label, item.Text))
                    .Select(item => (item.Label, item.Text)),
                _lastFinishReason,
                draftInput: InputBox.Text,
                draftAttachmentPaths: _attachmentPaths);
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
            if (i + 1 < turns.Count && TranscriptPresentationPolicy.IsAssistant(turns[i + 1].Label))
                removeCount = 2;
            turns.RemoveRange(i, removeCount);
        }

        session.Transcript = turns;
    }

    /// <summary>
    /// Ensure this job has user and streaming assistant bubbles on the current UI (bound to gen.*Entry).
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
                gen.ReplyEntry = AppendTurn(TranscriptPresentationPolicy.AssistantLabel, partial, PrimaryText, isStreaming: true);
            }
        }
        else
        {
            gen.ReplyEntry.UpdateStreamingText(partial);
        }
    }

    /// <summary>
    /// Rebuild the visible transcript from session data; attach live generation bubbles without
    /// reinterpreting an older completed assistant row as the current stream.
    /// </summary>
    private void RebuildVisibleTranscriptFromSession(ConversationSession session, SessionGeneration? liveGen)
    {
        TranscriptItems.Clear();

        // Completed turns only — if live gen's user line was partially captured earlier, skip it here
        // and re-add from liveGen so one round never gets two user/assistant pairs.
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
                // Skip this user and an immediate following assistant row captured from the live job.
                if (i + 1 < turns.Count && TranscriptPresentationPolicy.IsAssistant(turns[i + 1].Label))
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
        _attachmentPaths.Clear();
        _attachmentPaths.AddRange(session.DraftAttachmentPaths);
        RebuildChatAttachmentChips();

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

    private void RefreshVideoSessionPicker()
    {
        _suppressVideoSessionPicker = true;
        try
        {
            VideoSessionPicker.ItemsSource = null;
            VideoSessionPicker.ItemsSource = VideoPanel.Sessions.ToList();
            VideoSessionPicker.DisplayMemberPath = nameof(VideoSession.Title);
            VideoSessionPicker.SelectedItem = VideoPanel.Sessions.FirstOrDefault(s => s.Id == VideoPanel.ActiveSession.Id);
            DeleteVideoSessionButton.IsEnabled = VideoPanel.Sessions.Count > 1;
        }
        finally
        {
            _suppressVideoSessionPicker = false;
        }
    }

    private void NewVideoSessionButton_Click(object sender, RoutedEventArgs e)
    {
        VideoPanel.NewSession();
        RefreshVideoSessionPicker();
        SetNotice(VideoPanel.HasRunningJob
            ? "已新建视频窗口。上一窗口的任务继续在后台跑。"
            : "已新建视频窗口。", MutedText);
    }

    private async void DeleteVideoSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await VideoPanel.DeleteActiveSessionAsync())
        {
            SetNotice("至少保留一个视频窗口。", WarningText);
            return;
        }

        RefreshVideoSessionPicker();
        SetNotice("已删除视频窗口。", MutedText);
    }

    private void VideoSessionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVideoSessionPicker) return;
        if (VideoSessionPicker.SelectedItem is not VideoSession selected) return;
        if (selected.Id == VideoPanel.ActiveSession.Id) return;
        if (!VideoPanel.TryActivateSession(selected.Id))
        {
            RefreshVideoSessionPicker();
            return;
        }

        RefreshVideoSessionPicker();
        SetNotice(VideoPanel.IsActiveSessionGenerating
            ? $"已回到生成中的视频窗口：{selected.Title}"
            : VideoPanel.HasRunningJob
                ? $"当前视频窗口：{selected.Title}（另一窗口仍在生成）"
                : $"当前视频窗口：{selected.Title}", MutedText);
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

    private void EditUserMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranscriptEntry entry }) return;
        if (!entry.CanEdit) return;
        if (IsActiveSessionGenerating())
        {
            SetNotice("当前会话仍在生成中，请先停止或等完成后再改提问。", WarningText);
            return;
        }

        foreach (var item in TranscriptItems)
        {
            if (!ReferenceEquals(item, entry))
                item.CancelEdit();
        }
        entry.BeginEdit();
        ScrollTranscriptToQuestion(entry);
    }

    private void CancelEditUserMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptEntry entry })
            entry.CancelEdit();
    }

    private async void RegenerateUserMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranscriptEntry entry }) return;
        if (IsActiveSessionGenerating())
        {
            SetNotice("当前会话仍在生成中，请先停止或等完成后再重新生成。", WarningText);
            return;
        }

        var occurrenceFromEnd = 0;
        var start = TranscriptItems.IndexOf(entry);
        if (start < 0)
        {
            SetNotice("找不到要编辑的提问。", WarningText);
            return;
        }
        for (var i = start + 1; i < TranscriptItems.Count; i++)
        {
            if (TranscriptItems[i].Label == "你"
                && string.Equals(TranscriptItems[i].Text, entry.Text, StringComparison.Ordinal))
            {
                occurrenceFromEnd++;
            }
        }

        var owner = _sessions.Active;
        if (!ConversationEdit.TryPrepareRegenerate(
                _history,
                owner.Transcript,
                entry.Text,
                occurrenceFromEnd,
                entry.EditText,
                out var prepared,
                out var error)
            || prepared is null)
        {
            SetNotice(error ?? "这条提问无法重新生成。", WarningText);
            return;
        }

        CancelActiveChatQueue();
        owner.History = prepared.History.ToList();
        owner.Transcript = prepared.Transcript.ToList();
        owner.LongFormPlan = null;
        owner.LastFinishReason = null;
        owner.UpdatedAt = DateTimeOffset.Now;
        _history.Clear();
        _history.AddRange(prepared.History.Select(m => new ChatMessage(m.Role, m.Content)));
        _lastFinishReason = null;
        RebuildVisibleTranscriptFromSession(owner, liveGen: null);
        PersistSessions();
        UpdateContinueButtonState();
        await SendAsync(
            forcedUserMessage: prepared.ModelUserText,
            displayUserLabel: prepared.VisibleUserText,
            skipAttachments: true);
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
        // Role labels are display text, not enum keys; new assistant rows use the generic AI label.
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
            TranscriptList.UpdateLayout();
            TranscriptList.ScrollIntoView(questionEntry, ScrollIntoViewAlignment.Leading);
        });
    }

    private void RestoreQuestionAnchorAfterRebuild(ConversationSession session, string submittedQuestion)
    {
        var turnIndex = TranscriptPresentationPolicy.FindLatestQuestionIndex(session.Transcript, submittedQuestion);
        if (turnIndex < 0) return;
        var question = TranscriptItems.LastOrDefault(entry =>
            string.Equals(entry.Label, "你", StringComparison.Ordinal)
            && string.Equals(entry.Text, session.Transcript[turnIndex].Text, StringComparison.Ordinal));
        if (question is not null) ScrollTranscriptToQuestion(question);
    }

    private ChatAttachmentPolicy CurrentAttachmentPolicy()
        => (_settings.ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults).Normalized();

    private async void AttachChatFileButton_Click(object sender, RoutedEventArgs e)
    {
        var policy = CurrentAttachmentPolicy();
        var extensions = policy.AllowedExtensions();
        if (extensions.Count == 0)
        {
            SetNotice("设置里至少打开一种聊天附件类型。", WarningText);
            return;
        }

        var remaining = policy.MaxFiles - _attachmentPaths.Count;
        if (remaining <= 0)
        {
            SetNotice($"附件最多 {policy.MaxFiles} 个。", WarningText);
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            foreach (var extension in extensions)
                picker.FileTypeFilter.Add(extension);
            if (App.WindowHandle != 0)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            var incoming = files.Select(file => file.Path).Take(remaining);
            var merged = VideoMediaInputs.MergeReferencePaths(_attachmentPaths, incoming, policy.MaxFiles);
            _attachmentPaths.Clear();
            _attachmentPaths.AddRange(merged);
            RebuildChatAttachmentChips();
            CaptureIntoSession(_sessions.Active);
            PersistSessions();
        }
        catch (Exception error)
        {
            SetNotice($"选择附件失败：{error.Message}", ErrorText);
        }
    }

    private void RebuildChatAttachmentChips()
    {
        if (ChatAttachmentsHost is null) return;
        ChatAttachmentsHost.Items.Clear();
        foreach (var path in _attachmentPaths.ToArray())
        {
            var current = path;
            var name = Path.GetFileName(current);
            var clear = new Button
            {
                Content = "清除",
                Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
            };
            clear.Click += (_, _) =>
            {
                _attachmentPaths.Remove(current);
                RebuildChatAttachmentChips();
                CaptureIntoSession(_sessions.Active);
                PersistSessions();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(name) ? "未命名" : name,
                Style = (Style)Application.Current.Resources["ComposerMetaTextStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(clear);
            ChatAttachmentsHost.Items.Add(row);
        }

        ChatAttachmentsHost.Visibility = _attachmentPaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateChatComposerLayout();
    }

    private static string FormatTextComposerMeta(LocalChatSettings settings)
        => $"上下文 {settings.ContextSize}      输出上限 {settings.MaxOutputTokens}";

    private void RefreshComposerBusyState()
    {
        // Stop mode only when the *currently viewed* session owns an in-flight generation.
        var busyHere = IsActiveSessionGenerating();
        var queuedHere = _chatQueue.Position(_sessions.Active.Id) is not null;
        _busy = busyHere;
        var composerState = ChatInteractionPolicy.ComposerState(busyHere || queuedHere);
        SendButton.IsEnabled = composerState.ActionEnabled;
        InputBox.IsEnabled = true;
        InputBox.IsReadOnly = false;
        if (busyHere || queuedHere)
        {
            SendButton.Content = ChatInteractionPolicy.ActionLabel(busy: true);
            SendButton.Style = (Style)Application.Current.Resources["SecondaryButtonStyle"];
            AutomationProperties.SetName(SendButton, queuedHere ? "取消排队" : "停止生成");
            SendButton.IsEnabled = true;
        }
        else
        {
            SendButton.Content = ChatInteractionPolicy.ActionLabel(busy: false);
            SendButton.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
            AutomationProperties.SetName(SendButton, "发送消息");
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
