using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using QwenLocalChat.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace QwenLocalChat_WinUI;

internal enum ModelLifecycleState
{
    NotStarted,
    Starting,
    Ready,
    Reused,
    Failed,
    Released,
}

internal sealed record ModelLifecycleUpdate(ModelLifecycleState State, string? Detail = null);

public sealed partial class VideoGenerationPanel : UserControl
{
    private Func<CancellationToken, Task>? _prepareForVideoAsync;
    private Func<VideoGenerationSettings>? _settingsProvider;
    private Func<VideoPromptTemplatePhrases>? _templatePhrasesProvider;
    private Func<VideoModelProfile>? _profileProvider;
    private string? _projectRoot;
    private CancellationTokenSource? _jobCts;
    private ComfyUiServiceManager? _serviceManager;
    private ComfyUiWorkflowVideoClient? _client;
    private string? _activeProfileId;
    private VideoJob? _job;
    private readonly Stopwatch _elapsed = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _elapsedTicker;
    private VideoProgressObservation? _lastProgressObservation;
    private double _mediaWidth;
    private double _mediaHeight;
    private bool _isPointerOverPreview;
    private bool _isPreviewKeyboardFocused;
    private string? _previewOutputPath;
    private readonly InputHistoryNavigator _promptHistory = new();
    private string? _fullError;
    private bool _populatingJobParameters;
    private VideoGenerationCheckpointStore? _checkpointStore;
    private VideoJobRuntimeStore? _runtimeStore;
    private VideoQueuedJob? _runningRequest;
    private TimeSpan _elapsedOffset;
    private bool _detaching;
    private bool _applyingSession;
    private DateTimeOffset? _runningStartedAtUtc;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _draftPersistTimer;
    private string? _lastPrompt;
    private string? _appliedTemplate;
    private string? _appliedTemplateTitle;
    private string? _assembledOverride;
    private bool _suppressSlotOverrideClear;
    private VideoGenerationSettings? _lastSettings;
    private bool _resumeRequested;
    private VideoConditioningMode _conditioningMode = VideoConditioningMode.Text;
    internal VideoConditioningMode CurrentConditioningMode => _conditioningMode;
    private string? _firstFramePath;
    private string? _lastFramePath;
    private string? _referenceImagePath;
    private readonly List<string> _referenceImagePaths = [];
    private readonly List<string> _referenceVideoPaths = [];
    private readonly List<string> _referenceAudioPaths = [];
    private bool _suppressModeUi;
    private readonly VideoSessionWorkspace _sessions = new();
    private readonly VideoJobQueue _queue = new();
    private VideoSessionStore? _sessionStore;
    private string? _runningSessionId;
    private bool _drainQueue = true;

    public bool IsGenerating { get; private set; }
    public bool HasRunningJob => _runningSessionId is not null;
    public bool IsActiveSessionGenerating => _runningSessionId is not null && _runningSessionId == _sessions.Active.Id;
    public bool IsActiveSessionQueued => _queue.Position(_sessions.Active.Id) is not null;
    public IReadOnlyList<VideoSession> Sessions => _sessions.Sessions;
    public VideoSession ActiveSession => _sessions.Sessions.Count == 0 ? _sessions.EnsureBootstrap() : _sessions.Active;
    public event EventHandler? SessionsChanged;
    public bool OwnsModel => _serviceManager?.OwnsModel == true;
    public bool HasInitializedRuntime => _serviceManager is not null || _client is not null;
    public bool HasInterruptedWork { get; private set; }
    public string? InterruptedWorkSummary { get; private set; }
    private TimeSpan DisplayElapsed => _elapsedOffset + _elapsed.Elapsed;

    public VideoGenerationPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePromptLayout();
        SizeChanged += (_, _) => UpdatePromptLayout();
        VideoPromptInput.TextChanged += VideoPromptInput_TextChanged;
        VideoPromptInput.SelectionChanged += VideoPromptInput_SelectionChanged;
        Loaded += (_, _) =>
        {
            NumberBoxEditDisplay.Attach(VideoJobWidthBox);
            NumberBoxEditDisplay.Attach(VideoJobHeightBox);
            NumberBoxEditDisplay.Attach(VideoJobDurationBox);
            NumberBoxEditDisplay.Attach(VideoJobStepsBox);
            NumberBoxEditDisplay.Attach(VideoJobSeedBox);
        };
    }

    public void Configure(
        Func<CancellationToken, Task> prepareForVideoAsync,
        Func<VideoGenerationSettings> settingsProvider,
        Func<VideoModelProfile> profileProvider,
        string projectRoot,
        Func<VideoPromptTemplatePhrases>? templatePhrasesProvider = null)
    {
        _prepareForVideoAsync = prepareForVideoAsync;
        _settingsProvider = settingsProvider;
        _templatePhrasesProvider = templatePhrasesProvider;
        _profileProvider = profileProvider;
        _projectRoot = projectRoot;
        try
        {
            var localChatRoot = AppPaths.Discover().LocalChatRoot;
            _checkpointStore = new VideoGenerationCheckpointStore(VideoGenerationCheckpointStore.DefaultPath(localChatRoot));
            _runtimeStore = new VideoJobRuntimeStore(VideoJobRuntimeStore.DefaultPath(localChatRoot));
        }
        catch
        {
            _checkpointStore = new VideoGenerationCheckpointStore(
                Path.Combine(projectRoot, "local-chat", "data", "video-generation-checkpoint.json"));
            _runtimeStore = new VideoJobRuntimeStore(
                Path.Combine(projectRoot, "local-chat", "data", "video-job-runtime.json"));
        }
        RefreshPresetSummary();
        RefreshMediaModeUi();
        LoadSessions();
        LoadRuntimeIntoMemory();
        RefreshResumeUi();
        UpdateOpenOutputButtonState();
    }

    public void NewSession()
    {
        _sessions.CreateAndActivate(CaptureIntoSession);
        _ = ApplySessionToUiAsync(_sessions.Active);
        PersistSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> DeleteActiveSessionAsync()
    {
        DropActiveFromQueue();
        if (IsActiveSessionGenerating)
            await CancelActiveJobAsync();
        if (!_sessions.TryDeleteActive(CaptureIntoSession, out var next))
            return false;
        await ApplySessionToUiAsync(next);
        PersistSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryActivateSession(string id)
    {
        if (!_sessions.TryActivate(id, CaptureIntoSession, out var session))
            return false;
        _ = ApplySessionToUiAsync(session);
        PersistSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async void ClearVideoSessionButton_Click(object sender, RoutedEventArgs e)
        => await ClearActiveSessionAsync();

    public async Task ClearActiveSessionAsync()
    {
        DropActiveFromQueue();
        if (IsActiveSessionGenerating)
            await CancelActiveJobAsync();
        CaptureIntoSession(_sessions.Active);
        _sessions.Active.ClearWindow();
        await ApplySessionToUiAsync(_sessions.Active);
        PersistSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        VideoJobStatus.Text = "当前窗口已清空。成片文件仍在输出目录。";
    }

    private void LoadSessions()
    {
        try
        {
            _sessionStore = new VideoSessionStore(AppPaths.Discover().VideoSessionsFile);
        }
        catch
        {
            var root = _projectRoot ?? AppContext.BaseDirectory;
            _sessionStore = new VideoSessionStore(Path.Combine(root, "local-chat", "data", "video-sessions.json"));
        }

        _sessionStore.LoadInto(_sessions);
        ApplySessionToUi(_sessions.Active);
    }

    private void LoadRuntimeIntoMemory()
    {
        var snapshot = _runtimeStore?.Load() ?? VideoJobRuntimeSnapshot.Empty;
        _queue.ReplaceAll(snapshot.Queue);
        HasInterruptedWork = snapshot.HasWork;
        InterruptedWorkSummary = DescribeInterruptedWork(snapshot);
        if (snapshot.Queue.Count > 0)
            ApplyQueueStatuses();
    }

    private static string? DescribeInterruptedWork(VideoJobRuntimeSnapshot snapshot)
    {
        if (!snapshot.HasWork) return null;
        if (snapshot.Running is not null && snapshot.QueuedCount > 0)
            return $"正在恢复未完成的视频任务，另外还有 {snapshot.QueuedCount} 个已排队。";
        if (snapshot.Running is not null)
            return "正在恢复未完成的视频任务。";
        return snapshot.QueuedCount == 1
            ? "正在恢复 1 个已排队的视频任务。"
            : $"正在恢复 {snapshot.QueuedCount} 个已排队的视频任务。";
    }

    private void PersistRuntime(bool clearPromptId = false)
    {
        if (_runtimeStore is null) return;
        try
        {
            VideoRuntimeRunningJob? running = null;
            if (_runningRequest is not null)
            {
                var elapsed = DisplayElapsed.TotalSeconds;
                running = new VideoRuntimeRunningJob(
                    _runningRequest,
                    clearPromptId ? null : _job?.PromptId,
                    CurrentProfileOrNull()?.Id,
                    _runningStartedAtUtc ?? DateTimeOffset.UtcNow,
                    elapsed);
            }
            _runtimeStore.Save(new VideoJobRuntimeSnapshot(
                VideoJobRuntimeSnapshot.CurrentSchemaVersion,
                running,
                _queue.Items.ToArray()));
            HasInterruptedWork = running is not null || _queue.Count > 0;
        }
        catch
        {
            /* best effort; a failed write must not stop generate or exit */
        }
    }

    private VideoModelProfile? CurrentProfileOrNull()
    {
        try { return CurrentProfile; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task RestoreInterruptedWorkAsync()
    {
        var snapshot = _runtimeStore?.Load() ?? VideoJobRuntimeSnapshot.Empty;
        if (!snapshot.HasWork) return;
        _drainQueue = true;
        _queue.ReplaceAll(snapshot.Queue);
        foreach (var queued in snapshot.Queue)
            RestorePromptOntoSession(queued);
        ApplyQueueStatuses();
        if (snapshot.Running is { } running)
        {
            RestorePromptOntoSession(running.Job);
            var elapsed = running.StartedAtUtc == default
                ? TimeSpan.FromSeconds(Math.Max(0, running.ElapsedSeconds))
                : DateTimeOffset.UtcNow - running.StartedAtUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            await ExecuteJobAsync(running.Job, running.PromptId, elapsed);
            return;
        }
        await StartNextQueuedAsync();
    }

    private void PersistSessions()
    {
        try { _sessionStore?.Save(_sessions); }
        catch { /* best effort */ }
    }

    private void VideoPromptInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePromptLayout();
        UpdateMentionPopup();
        if (_applyingSession) return;
        ScheduleDraftPersist();
    }

    private void VideoPromptInput_SelectionChanged(object sender, RoutedEventArgs e)
        => UpdateMentionPopup();

    private void ScheduleDraftPersist()
    {
        _draftPersistTimer ??= DispatcherQueue.CreateTimer();
        _draftPersistTimer.Interval = TimeSpan.FromMilliseconds(400);
        _draftPersistTimer.IsRepeating = false;
        _draftPersistTimer.Tick -= DraftPersistTimer_Tick;
        _draftPersistTimer.Tick += DraftPersistTimer_Tick;
        _draftPersistTimer.Start();
    }

    private void DraftPersistTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        => FlushActiveDraft();

    private void FlushActiveDraft()
    {
        if (_sessions.Sessions.Count == 0) return;
        CaptureIntoSession(_sessions.Active);
        PersistSessions();
    }

    private void RestorePromptOntoSession(VideoQueuedJob job)
    {
        var session = _sessions.Find(job.SessionId);
        if (session is null) return;
        if (string.IsNullOrWhiteSpace(session.LastPrompt))
            session.LastPrompt = job.Prompt;
        if (string.IsNullOrWhiteSpace(session.DraftPrompt)
            && string.IsNullOrWhiteSpace(session.DraftTemplate)
            && session.Id == _sessions.Active.Id
            && string.IsNullOrWhiteSpace(VideoPromptInput.Text))
            VideoPromptInput.Text = job.Prompt;
        PersistSessions();
    }

    private void CaptureIntoSession(VideoSession session)
    {
        if (_sessions.Sessions.Count > 0 && session.Id != _sessions.Active.Id)
            return;

        var viewingRunning = _runningSessionId == session.Id;
        session.Capture(new VideoSessionSnapshot(
            ComposerText,
            VideoMediaCapabilities.ToId(_conditioningMode),
            _firstFramePath,
            _lastFramePath,
            _referenceImagePaths.FirstOrDefault(),
            ReadJobOverrides(),
            viewingRunning ? session.LastOutputPath : _previewOutputPath ?? session.LastOutputPath,
            viewingRunning ? session.LastStatus : VideoJobStatus.Text,
            viewingRunning ? session.LastError : _fullError,
            _lastPrompt ?? session.LastPrompt,
            _referenceImagePaths.ToArray(),
            _referenceVideoPaths.ToArray(),
            _referenceAudioPaths.ToArray(),
            _appliedTemplate,
            _appliedTemplateTitle,
            ExtraActionBox?.Text,
            ExtraSoundBox?.Text,
            ExtraMusicBox?.Text,
            ExtraIdentityBox?.Text,
            _assembledOverride));
        _sessions.ApplySort();
    }

    private void ApplySessionToUi(VideoSession session)
        => _ = ApplySessionToUiAsync(session);

    private async Task ApplySessionToUiAsync(VideoSession session)
    {
        _applyingSession = true;
        try
        {
        VideoPromptInput.Text = session.DraftPrompt ?? string.Empty;
        _appliedTemplate = session.DraftTemplate;
        _appliedTemplateTitle = session.DraftTemplateTitle;
        _assembledOverride = session.AssembledOverride;
        ApplySessionExtras(session);
        _conditioningMode = VideoMediaCapabilities.ParseMode(session.ConditioningMode) ?? VideoConditioningMode.Text;
        _firstFramePath = session.FirstFramePath;
        _lastFramePath = session.LastFramePath;
        _referenceImagePath = session.ReferenceImagePath;
        _referenceImagePaths.Clear();
        _referenceImagePaths.AddRange(session.ReferenceImagePaths.Count > 0
            ? session.ReferenceImagePaths
            : string.IsNullOrWhiteSpace(session.ReferenceImagePath) ? [] : [session.ReferenceImagePath]);
        _referenceVideoPaths.Clear();
        _referenceVideoPaths.AddRange(session.ReferenceVideoPaths);
        _referenceAudioPaths.Clear();
        _referenceAudioPaths.AddRange(session.ReferenceAudioPaths);
        _lastPrompt = session.LastPrompt;
        _fullError = session.LastError;
        RefreshPresetSummary();
        if (session.JobOverrides is { } overrides)
        {
            _populatingJobParameters = true;
            try
            {
                if (overrides.Width is int width) VideoJobWidthBox.Value = width;
                if (overrides.Height is int height) VideoJobHeightBox.Value = height;
                if (overrides.DurationSeconds is int duration) VideoJobDurationBox.Value = duration;
                if (overrides.Steps is int steps) VideoJobStepsBox.Value = steps;
                if (overrides.Seed is long seed) VideoJobSeedBox.Value = seed;
                if (overrides.RandomSeed is bool random) VideoJobRandomSeedToggle.IsOn = random;
            }
            finally { _populatingJobParameters = false; }
            UpdateJobParameterSummary();
        }
        RefreshMediaModeUi();
        UpdateAppliedTemplateUi();
        SetVideoError(session.LastError);
        VideoJobStatus.Text = string.IsNullOrWhiteSpace(session.LastStatus) ? "等待开始" : session.LastStatus;
        ClearVideoPreview();
        RefreshGeneratingUi();
        var preview = VideoPreviewHistory.ResolveSessionPreview(session.LastOutputPath, null);
        if (preview is not null && !IsActiveSessionGenerating)
        {
            try { await LoadVideoPreviewAsync(preview); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        }
        finally
        {
            _applyingSession = false;
        }
    }

    private void BindJobResult(string sessionId, string? outputPath, string status, string? error)
    {
        var session = _sessions.Find(sessionId);
        if (session is null) return;
        session.LastOutputPath = string.IsNullOrWhiteSpace(outputPath) ? session.LastOutputPath : outputPath;
        session.LastStatus = status;
        session.LastError = error;
        session.LastPrompt = _lastPrompt ?? session.LastPrompt;
        if (session.Title is VideoSession.EmptyTitle or "")
            session.Title = VideoSession.SuggestTitle(session.LastPrompt ?? session.DraftPrompt);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        _sessions.ApplySort();
        PersistSessions();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task CancelActiveJobAsync()
    {
        try
        {
            if (_job is not null && _client is not null && !_job.IsTerminal)
                await _client.CancelAsync(_job.PromptId);
        }
        catch { }
        finally
        {
            _jobCts?.Cancel();
            for (var i = 0; i < 40 && IsGenerating; i++)
                await Task.Delay(50);
        }
    }

    public event EventHandler? SettingsRequested;
    internal event Action<string, string, bool>? ExpandedTextRequested;
    internal event Action<ModelLifecycleUpdate>? ModelStatusChanged;

    public void FocusSettingsButton() => VideoSettingsButton.Focus(FocusState.Programmatic);

    private void VideoSettingsButton_Click(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private async void StartVideoButton_Click(object sender, RoutedEventArgs e)
    {
        _resumeRequested = false;
        await StartVideoAsync();
    }

    private async void RetryVideoButton_Click(object sender, RoutedEventArgs e)
    {
        _resumeRequested = false;
        if (!string.IsNullOrWhiteSpace(_lastPrompt) && string.IsNullOrWhiteSpace(_appliedTemplate))
            VideoPromptInput.Text = _lastPrompt;
        await StartVideoAsync();
    }

    private async void ResumeVideoButton_Click(object sender, RoutedEventArgs e)
    {
        _resumeRequested = true;
        var checkpoint = _checkpointStore?.Load();
        if (checkpoint is null || !checkpoint.CanResume)
        {
            SetVideoError("没有可恢复的视频检查点。");
            RefreshResumeUi();
            return;
        }
        await StartVideoAsync();
    }

    private async Task StartVideoAsync()
    {
        var resumeCheckpoint = _resumeRequested ? _checkpointStore?.Load() : null;
        var prompt = resumeCheckpoint is { CanResume: true }
            ? resumeCheckpoint.Prompt
            : CurrentAssembledPrompt();
        if (prompt.Length == 0)
        {
            VideoJobStatus.Text = string.IsNullOrWhiteSpace(_appliedTemplate)
                ? "请输入视频提示词。"
                : "请先插入模板，或填写补充内容。";
            VideoPromptInput.Focus(FocusState.Programmatic);
            return;
        }

        var settings = ReadJobOverrides().ApplyTo(CurrentSettings);
        if (resumeCheckpoint is { CanResume: true })
        {
            settings = settings with
            {
                Width = resumeCheckpoint.Width,
                Height = resumeCheckpoint.Height,
                DurationSeconds = resumeCheckpoint.DurationSeconds,
                Steps = resumeCheckpoint.Steps,
                Seed = Math.Clamp(resumeCheckpoint.Seed, 0, int.MaxValue),
                RandomSeed = false,
            };
            RefreshPresetSummary();
            // Re-apply resume values after RefreshPresetSummary resets boxes from defaults.
            _populatingJobParameters = true;
            try
            {
                VideoJobWidthBox.Value = settings.Width;
                VideoJobHeightBox.Value = settings.Height;
                VideoJobDurationBox.Value = settings.DurationSeconds;
                VideoJobStepsBox.Value = settings.Steps;
                VideoJobSeedBox.Value = settings.Seed;
                VideoJobRandomSeedToggle.IsOn = false;
            }
            finally { _populatingJobParameters = false; }
            UpdateJobParameterSummary();
        }

        var media = ReadMediaInputs();
        var validationErrors = settings.Validate()
            .Concat(CurrentProfile.Capabilities.Validate(settings))
            .Concat(media.Validate(CurrentProfile.EffectiveMedia))
            .Distinct()
            .ToArray();
        if (validationErrors.Length > 0)
        {
            VideoJobStatus.Text = string.Join("；", validationErrors);
            SetJobParametersExpanded(true);
            return;
        }

        var request = new VideoQueuedJob(
            _sessions.EnsureBootstrap().Id,
            prompt,
            settings,
            media,
            _resumeRequested);
        _promptHistory.Record(prompt);
        FlushActiveDraft();

        if (HasRunningJob)
        {
            var position = _queue.Enqueue(request);
            ApplyQueueStatuses();
            PersistRuntime();
            VideoJobStatus.Text = VideoJobQueue.FormatStatus(position);
            return;
        }

        await ExecuteJobAsync(request);
    }

    private async Task ExecuteJobAsync(VideoQueuedJob request, string? attachPromptId = null, TimeSpan? elapsedOffset = null)
    {
        _runningSessionId = request.SessionId;
        _runningRequest = request;
        _lastPrompt = request.Prompt;
        _lastSettings = request.Settings;
        _resumeRequested = false;
        _elapsedOffset = elapsedOffset ?? TimeSpan.Zero;
        _runningStartedAtUtc = DateTimeOffset.UtcNow - _elapsedOffset;
        SetVideoError(null);
        SetGenerating(true);
        _job = null;
        UpdateOpenOutputButtonState();
        PersistRuntime();
        var viewing = request.SessionId == _sessions.Active.Id;
        try
        {
            ModelStatusChanged?.Invoke(new(ModelLifecycleState.Starting));
            if (viewing)
                VideoJobStatus.Text = $"正在检查 {CurrentProfile.DisplayName}…";
            if (_prepareForVideoAsync is not null) await _prepareForVideoAsync(_jobCts!.Token);

            EnsureClients();
            bool started;
            try
            {
                started = await _serviceManager!.EnsureAvailableAsync(_jobCts!.Token);
                ModelStatusChanged?.Invoke(new(started ? ModelLifecycleState.Ready : ModelLifecycleState.Reused));
            }
            catch (Exception error)
            {
                ModelStatusChanged?.Invoke(new(ModelLifecycleState.Failed, error.Message));
                throw;
            }
            if (viewing)
                VideoJobStatus.Text = string.IsNullOrWhiteSpace(attachPromptId)
                    ? $"{CurrentProfile.DisplayName} 已就绪，正在提交任务…"
                    : $"{CurrentProfile.DisplayName} 已就绪，正在接回未完成任务…";
            var resumeCheckpoint = request.ResumeRequested ? _checkpointStore?.Load() : null;
            _elapsed.Restart();
            if (!string.IsNullOrWhiteSpace(attachPromptId)
                && await TryAttachRunningJobAsync(attachPromptId, request, _jobCts.Token))
            {
                // Poll finished inside TryAttach; _job is terminal or we fell through to resubmit.
            }
            if (_job is null || !_job.IsTerminal)
                await RunSegmentedGenerationAsync(request.Prompt, request.Settings, request.Media, resumeCheckpoint, _jobCts.Token);

            _elapsed.Stop();
            var jobSessionId = request.SessionId;
            viewing = jobSessionId == _sessions.Active.Id;
            if (_job?.Status == "completed" && _job.OutputPath is { } output && File.Exists(output))
            {
                var status = $"生成完成  {DisplayElapsed:hh\\:mm\\:ss}  {Path.GetFileName(output)}";
                BindJobResult(jobSessionId, output, status, null);
                if (viewing)
                {
                    await LoadVideoPreviewAsync(output);
                    UpdateOpenOutputButtonState();
                    VideoJobStatus.Text = status;
                }
                _checkpointStore?.Clear();
            }
            else if (_job?.Status == "completed")
            {
                var status = "任务完成，但 ComfyUI 未返回可用的本地视频路径。";
                BindJobResult(jobSessionId, null, status, null);
                if (viewing)
                    VideoJobStatus.Text = status;
            }
            else if (!string.IsNullOrWhiteSpace(_job?.Error))
            {
                BindJobResult(jobSessionId, null, _job.Error, _job.Error);
                if (viewing)
                    SetVideoError(_job.Error);
            }
            else if (_job is not null)
            {
                var status = StatusLabel(_job.Status);
                BindJobResult(jobSessionId, null, status, null);
                if (viewing)
                    VideoJobStatus.Text = status;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_detaching)
            {
                BindJobResult(request.SessionId, null, "已取消", null);
                if (request.SessionId == _sessions.Active.Id)
                    VideoJobStatus.Text = "已取消";
            }
        }
        catch (Exception error)
        {
            BindJobResult(request.SessionId, null, $"生成失败：{error.Message}", error.Message);
            if (request.SessionId == _sessions.Active.Id)
                SetVideoError($"生成失败：{error.Message}");
        }
        finally
        {
            _elapsed.Stop();
            if (_detaching)
            {
                PersistRuntime();
            }
            else
            {
                _runningSessionId = null;
                _runningRequest = null;
                _runningStartedAtUtc = null;
                _elapsedOffset = TimeSpan.Zero;
                PersistRuntime();
                SetGenerating(false);
                RefreshResumeUi();
                await StartNextQueuedAsync();
            }
        }
    }

    private async Task<bool> TryAttachRunningJobAsync(string promptId, VideoQueuedJob request, CancellationToken cancellationToken)
    {
        try
        {
            var job = await _client!.GetJobAsync(promptId, cancellationToken);
            if (job.Status is "unknown" or "failed" or "cancelled")
                return false;
            _job = job;
            PersistRuntime();
            if (_job.IsTerminal)
                return true;
            while (!_job.IsTerminal)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                _job = await _client.GetJobAsync(_job.PromptId, cancellationToken);
                PersistRuntime();
                var observation = VideoProgressObservation.FromJob(_job, DisplayElapsed, totalSteps: request.Settings.Steps);
                if (!string.IsNullOrWhiteSpace(_job.Error))
                {
                    SetVideoError(_job.Error);
                    ApplyProgressUi(observation, updateStatusText: false);
                }
                else
                {
                    ApplyProgressUi(observation);
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartNextQueuedAsync()
    {
        if (!_drainQueue || HasRunningJob) return;
        var next = _queue.Dequeue();
        ApplyQueueStatuses();
        PersistRuntime();
        if (next is null) return;
        await ExecuteJobAsync(next);
    }

    private void ApplyQueueStatuses()
    {
        foreach (var item in _queue.Items)
        {
            var position = _queue.Position(item.SessionId) ?? 0;
            var status = VideoJobQueue.FormatStatus(position);
            var session = _sessions.Find(item.SessionId);
            if (session is not null)
            {
                session.LastStatus = status;
                session.LastError = null;
                session.UpdatedAt = DateTimeOffset.Now;
            }
            if (item.SessionId == _sessions.Active.Id && !IsActiveSessionGenerating)
                VideoJobStatus.Text = status;
        }
        PersistSessions();
        PersistRuntime();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        RefreshGeneratingUi();
    }

    private bool DropActiveFromQueue()
    {
        if (!_queue.Remove(_sessions.Active.Id)) return false;
        ApplyQueueStatuses();
        PersistRuntime();
        return true;
    }

    private async Task RunSegmentedGenerationAsync(
        string prompt,
        VideoGenerationSettings settings,
        VideoMediaInputs media,
        VideoGenerationCheckpoint? resume,
        CancellationToken cancellationToken)
    {
        // Force a fixed seed for checkpoint continuity across segments / resume.
        if (settings.RandomSeed && resume is null)
        {
            var rolled = Random.Shared.NextInt64(0, (long)int.MaxValue + 1);
            settings = settings with { RandomSeed = false, Seed = rolled };
        }
        else if (resume is not null)
        {
            settings = settings with
            {
                RandomSeed = false,
                Seed = Math.Clamp(resume.Seed, 0, int.MaxValue),
            };
        }
        else
        {
            settings = settings with { RandomSeed = false };
        }

        // Resume keeps text/seed continuity; media for the current UI selection still applies on resubmit.
        var request = new VideoGenerationRequest(prompt, settings, media);
        var totalSteps = settings.Steps;
        var segmentSize = resume?.SegmentSize > 0
            ? resume.SegmentSize
            : VideoSegmentedWorkflowBuilder.SegmentSizeFor(totalSteps);
        var seed = (long)settings.Seed;
        var resumeFromStep = resume is { CanResume: true } ? resume.CompletedSteps : 0;
        var resumeLatent = resume is { CanResume: true } ? resume.LatentFileName : null;
        if (resumeFromStep > 0 && !string.IsNullOrWhiteSpace(resumeLatent))
        {
            // Ensure LoadLatent can see the file under ComfyUI/input.
            var inputPath = Path.Combine(CurrentProfile.Service.ComfyUiRoot, "input", resumeLatent);
            if (!File.Exists(inputPath))
            {
                var fromOutput = FindLatestLatent(
                    CurrentProfile.Service.OutputDirectory,
                    VideoGenerationCheckpointStore.BuildLatentPrefix(CurrentProfile.Id, seed),
                    resumeFromStep);
                if (fromOutput is not null)
                    resumeLatent = StageLatentForLoad(CurrentProfile.Service.ComfyUiRoot, fromOutput);
            }
            VideoJobStatus.Text = $"从第 {resumeFromStep}/{totalSteps} 步继续…";
        }

        var workflowSha = VideoGenerationCheckpointStore.ComputeWorkflowSha256(_client!.WorkflowPath);
        var latentPrefix = VideoGenerationCheckpointStore.BuildLatentPrefix(CurrentProfile.Id, seed);

        _checkpointStore?.Save(new VideoGenerationCheckpoint(
            VideoGenerationCheckpoint.CurrentSchemaVersion,
            CurrentProfile.Id,
            prompt,
            settings.Width,
            settings.Height,
            settings.DurationSeconds,
            totalSteps,
            seed,
            resumeFromStep,
            segmentSize,
            resumeLatent,
            "latents",
            workflowSha,
            "running",
            DateTimeOffset.UtcNow));

        var useStepCheckpoints = false;
        var graph = await _client.BuildGraphObjectAsync(request, g =>
        {
            // NestedTensor AV latents cannot use stock SaveLatent (.contiguous).
            useStepCheckpoints = VideoSegmentedWorkflowBuilder.SupportsStepLatentCheckpoints(g);
            if (useStepCheckpoints)
            {
                VideoSegmentedWorkflowBuilder.ApplySegmentedSampler(
                    g,
                    totalSteps,
                    segmentSize,
                    seed,
                    latentPrefix,
                    resumeFromStep,
                    resumeLatent);
            }
        }, cancellationToken);
        seed = _client.TakeResolvedSeed(graph);
        if (seed == 0) seed = settings.Seed;
        latentPrefix = VideoGenerationCheckpointStore.BuildLatentPrefix(CurrentProfile.Id, seed);

        // NestedTensor AV models: ignore stale step latents; full-graph KSampler only.
        if (!useStepCheckpoints)
        {
            resumeFromStep = 0;
            resumeLatent = null;
        }

        VideoJobStatus.Text = useStepCheckpoints
            ? (resumeFromStep > 0
                ? $"已提交续跑（从步 {resumeFromStep}）…"
                : "已提交分段检查点任务…")
            : "已提交视频任务…";
        ApplyProgressUi(new VideoProgressObservation(
            DisplayElapsed,
            "pending",
            CompletedSteps: useStepCheckpoints ? resumeFromStep : null,
            TotalSteps: totalSteps));
        _job = await _client.SubmitGraphAsync(graph, cancellationToken);
        PersistRuntime();

        while (!_job.IsTerminal)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            _job = await _client.GetJobAsync(_job.PromptId, cancellationToken);

            int? completedSteps = null;
            if (useStepCheckpoints)
            {
                // Opportunistically record the newest on-disk latent while still running.
                var latestStep = FindNewestCheckpointStep(CurrentProfile.Service.OutputDirectory, latentPrefix);
                if (latestStep is int step && step > resumeFromStep)
                {
                    var path = FindLatestLatent(CurrentProfile.Service.OutputDirectory, latentPrefix, step);
                    if (path is not null)
                    {
                        var staged = StageLatentForLoad(CurrentProfile.Service.ComfyUiRoot, path);
                        _checkpointStore?.Save(new VideoGenerationCheckpoint(
                            VideoGenerationCheckpoint.CurrentSchemaVersion,
                            CurrentProfile.Id,
                            prompt,
                            settings.Width,
                            settings.Height,
                            settings.DurationSeconds,
                            totalSteps,
                            seed,
                            step,
                            segmentSize,
                            staged,
                            "latents",
                            workflowSha,
                            "running",
                            DateTimeOffset.UtcNow));
                    }
                }

                completedSteps = latestStep ?? resumeFromStep;
            }

            if (completedSteps is null
                && totalSteps > 0
                && _job.Progress is double backend
                && backend > 0)
            {
                var fraction = VideoProgressEstimator.NormalizeBackendProgress(backend);
                completedSteps = (int)Math.Round(fraction * totalSteps);
            }

            var observation = VideoProgressObservation.FromJob(
                _job,
                DisplayElapsed,
                completedSteps: completedSteps,
                totalSteps: totalSteps);
            if (!string.IsNullOrWhiteSpace(_job.Error))
            {
                SetVideoError(_job.Error);
                ApplyProgressUi(observation, updateStatusText: false);
            }
            else
            {
                ApplyProgressUi(observation);
            }
        }

        if (_job.Status == "completed")
        {
            _checkpointStore?.Clear();
            return;
        }

        var completed = 0;
        string? latentName = null;
        if (useStepCheckpoints)
        {
            completed = FindNewestCheckpointStep(CurrentProfile.Service.OutputDirectory, latentPrefix) ?? resumeFromStep;
            if (completed > 0)
            {
                var path = FindLatestLatent(CurrentProfile.Service.OutputDirectory, latentPrefix, completed);
                if (path is not null)
                    latentName = StageLatentForLoad(CurrentProfile.Service.ComfyUiRoot, path);
            }
        }

        // When step checkpoints are unavailable, keep seed/params for identical re-run only.
        _checkpointStore?.Save(new VideoGenerationCheckpoint(
            VideoGenerationCheckpoint.CurrentSchemaVersion,
            CurrentProfile.Id,
            prompt,
            settings.Width,
            settings.Height,
            settings.DurationSeconds,
            totalSteps,
            seed,
            completed,
            segmentSize,
            latentName,
            "latents",
            workflowSha,
            useStepCheckpoints && completed > 0 && latentName is not null ? "ready_to_resume" : "failed",
            DateTimeOffset.UtcNow,
            _job.Error));

        try { await _client.CancelAsync(_job.PromptId, CancellationToken.None); }
        catch { /* best-effort release of a stuck backend job */ }
    }

    private static int? FindNewestCheckpointStep(string outputDirectory, string latentPrefix)
    {
        var relative = latentPrefix.Replace('/', Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.Combine(outputDirectory, relative));
        if (directory is null || !Directory.Exists(directory)) return null;
        var baseName = Path.GetFileName(relative);
        var best = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.latent"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.Contains(baseName, StringComparison.OrdinalIgnoreCase)) continue;
            var marker = "_step";
            var idx = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var numberPart = name[(idx + marker.Length)..];
            var digits = new string(numberPart.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var step) && step > best)
                best = step;
        }
        return best > 0 ? best : null;
    }

    private static string? FindLatestLatent(string outputDirectory, string latentPrefix, int endStep)
    {
        var relative = latentPrefix.Replace('/', Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(Path.Combine(outputDirectory, relative));
        if (directory is null || !Directory.Exists(directory)) return null;
        var marker = $"_step{endStep}";
        return Directory.EnumerateFiles(directory, "*.latent")
            .Where(path => Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(path).Contains(Path.GetFileName(relative) + marker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string StageLatentForLoad(string comfyRoot, string latentPath)
    {
        var inputDir = Path.Combine(comfyRoot, "input");
        Directory.CreateDirectory(inputDir);
        var fileName = Path.GetFileName(latentPath);
        var destination = Path.Combine(inputDir, fileName);
        File.Copy(latentPath, destination, overwrite: true);
        return fileName;
    }

    private async void CancelVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (DropActiveFromQueue())
        {
            BindJobResult(_sessions.Active.Id, null, "已取消排队", null);
            VideoJobStatus.Text = "已取消排队";
            RefreshGeneratingUi();
            return;
        }

        try
        {
            CancelVideoButton.IsEnabled = false;
            VideoJobStatus.Text = "正在取消视频任务…";
            if (_job is not null && _client is not null && !_job.IsTerminal)
                await _client.CancelAsync(_job.PromptId);
        }
        catch (Exception error)
        {
            SetVideoError($"取消请求失败：{error.Message}");
        }
        finally
        {
            _jobCts?.Cancel();
        }
    }

    private void OpenVideoOutputButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = ResolveRevealTarget();
            if (target is null)
            {
                VideoJobStatus.Text = IsGenerating
                    ? "输出目录尚未就绪，请稍后再试「文件位置」。"
                    : "还没有可定位的视频文件或输出目录。";
                return;
            }

            Process.Start(WindowsFileReveal.CreateExplorerRevealStartInfo(target.FilePath, target.DirectoryPath));
        }
        catch (Exception error)
        {
            SetVideoError($"无法定位视频：{error.Message}");
        }
    }

    private sealed record RevealTarget(string? FilePath, string? DirectoryPath);

    private RevealTarget? ResolveRevealTarget()
    {
        if (_previewOutputPath is { } preview && File.Exists(preview))
            return new(preview, Path.GetDirectoryName(preview));

        try
        {
            var outputDirectory = CurrentProfile.Service.OutputDirectory;
            if (Directory.Exists(outputDirectory))
            {
                // Prefer local-ai subfolder when present (active job / latest clips).
                var localAi = Path.Combine(outputDirectory, "local-ai");
                if (Directory.Exists(localAi))
                    return new(null, localAi);
                return new(null, outputDirectory);
            }
        }
        catch (InvalidOperationException)
        {
            // Profile not ready yet.
        }

        return null;
    }

    private void UpdateOpenOutputButtonState()
    {
        OpenVideoOutputButton.IsEnabled = ResolveRevealTarget() is not null;
    }

    public async Task<bool> IsServiceRunningAsync(CancellationToken cancellationToken = default)
        => _serviceManager is not null && (OwnsModel || await _serviceManager.IsHealthyAsync(cancellationToken));

    public async Task ReleaseModelAsync(CancellationToken cancellationToken = default)
    {
        if (IsGenerating) throw new InvalidOperationException("视频仍在生成，不能启动聊天模型。");
        if (_serviceManager is null)
        {
            ModelStatusChanged?.Invoke(new(ModelLifecycleState.Released));
            return;
        }
        if (_serviceManager.OwnsModel) await _serviceManager.StopOwnedAsync(cancellationToken);
        else await _serviceManager.UnloadReusedAsync(cancellationToken);
        ModelStatusChanged?.Invoke(new(ModelLifecycleState.Released));
    }

    public async Task StartModelAsync(CancellationToken cancellationToken = default)
    {
        if (IsGenerating) throw new InvalidOperationException("视频仍在生成，不能重复启动模型。");
        ModelStatusChanged?.Invoke(new(ModelLifecycleState.Starting));
        try
        {
            if (_prepareForVideoAsync is not null) await _prepareForVideoAsync(cancellationToken);
            EnsureClients();
            var started = await _serviceManager!.EnsureAvailableAsync(cancellationToken);
            ModelStatusChanged?.Invoke(new(started ? ModelLifecycleState.Ready : ModelLifecycleState.Reused));
        }
        catch (Exception error)
        {
            ModelStatusChanged?.Invoke(new(ModelLifecycleState.Failed, error.Message));
            throw;
        }
    }

    public async Task RefreshModelStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureClients();
            ModelStatusChanged?.Invoke(new(
                await _serviceManager!.IsHealthyAsync(cancellationToken)
                    ? ModelLifecycleState.Reused
                    : ModelLifecycleState.NotStarted));
        }
        catch (Exception error)
        {
            ModelStatusChanged?.Invoke(new(ModelLifecycleState.Failed, error.Message));
        }
    }

    public async Task ShutdownAsync(bool stopModel, bool detachRunningWork = false)
    {
        FlushActiveDraft();
        if (detachRunningWork)
        {
            _drainQueue = false;
            _detaching = true;
            PersistRuntime(clearPromptId: stopModel);
        }
        if (stopModel)
        {
            try
            {
                if (_job is not null && _client is not null && !_job.IsTerminal)
                    await _client.CancelAsync(_job.PromptId);
            }
            catch { }
        }
        _jobCts?.Cancel();
        if (stopModel && _serviceManager is null)
        {
            try { EnsureClients(); }
            catch { /* Invalid profile configuration must not block app exit. */ }
        }
        if (_serviceManager is not null)
        {
            if (stopModel)
                await _serviceManager.StopServiceAsync();
            await _serviceManager.DisposeAsync();
            _serviceManager = null;
        }
        _client?.Dispose();
        _client = null;
        _activeProfileId = null;
        _jobCts?.Dispose();
        _jobCts = null;
        if (stopModel) ModelStatusChanged?.Invoke(new(ModelLifecycleState.Released));
    }

    private void EnsureClients()
    {
        var profile = CurrentProfile;
        if (_serviceManager is not null && _client is not null && string.Equals(_activeProfileId, profile.Id, StringComparison.Ordinal))
            return;
        if (_serviceManager is not null || _client is not null)
            throw new InvalidOperationException("视频模型档案已切换，请先释放旧模型后重试。");
        if (string.IsNullOrWhiteSpace(_projectRoot)) throw new InvalidOperationException("视频模型目录尚未初始化。");
        _serviceManager = new ComfyUiServiceManager(profile.Service, requiredNodeClasses: profile.RequiredNodeClasses);
        _client = new ComfyUiWorkflowVideoClient(_projectRoot, profile);
        _activeProfileId = profile.Id;
    }

    private VideoGenerationSettings CurrentSettings
        => _settingsProvider?.Invoke() ?? VideoGenerationSettings.SafeDefaults;

    private VideoModelProfile CurrentProfile
        => _profileProvider?.Invoke() ?? throw new InvalidOperationException("视频模型档案尚未初始化。");

    public async Task ResetProfileAsync(bool stopModel)
    {
        if (IsGenerating) throw new InvalidOperationException("视频仍在生成，不能切换视频模型。");
        await ShutdownAsync(stopModel);
        ClearVideoPreview();
        RefreshPresetSummary();
        RefreshMediaModeUi();
        await RestoreActiveSessionPreviewAsync();
    }

    public void RefreshPresetSummary()
    {
        var settings = CurrentSettings;
        var capabilities = CurrentProfile.Capabilities;
        _populatingJobParameters = true;
        try
        {
            ConfigureJobNumberBox(VideoJobWidthBox, capabilities.MinimumDimension, capabilities.MaximumDimension, capabilities.DimensionStep, settings.Width);
            ConfigureJobNumberBox(VideoJobHeightBox, capabilities.MinimumDimension, capabilities.MaximumDimension, capabilities.DimensionStep, settings.Height);
            ConfigureJobNumberBox(VideoJobDurationBox, capabilities.MinimumDurationSeconds, capabilities.MaximumDurationSeconds, 1, settings.DurationSeconds);
            ConfigureJobNumberBox(VideoJobStepsBox, capabilities.MinimumSteps, capabilities.MaximumSteps, 1, settings.Steps);
            VideoJobSeedBox.Value = settings.Seed;
            VideoJobRandomSeedToggle.IsOn = settings.RandomSeed;
        }
        finally
        {
            _populatingJobParameters = false;
        }
        UpdateJobParameterSummary();
    }

    private static void ConfigureJobNumberBox(NumberBox box, int minimum, int maximum, int step, int value)
    {
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.SmallChange = step;
        box.LargeChange = step;
        box.Value = value;
    }

    private VideoGenerationOverrides ReadJobOverrides()
        => new()
        {
            Width = SettingsNumberInput.Whole(VideoJobWidthBox.Text, VideoJobWidthBox.Value),
            Height = SettingsNumberInput.Whole(VideoJobHeightBox.Text, VideoJobHeightBox.Value),
            DurationSeconds = SettingsNumberInput.Whole(VideoJobDurationBox.Text, VideoJobDurationBox.Value),
            Steps = SettingsNumberInput.Whole(VideoJobStepsBox.Text, VideoJobStepsBox.Value),
            Seed = SettingsNumberInput.Whole(VideoJobSeedBox.Text, VideoJobSeedBox.Value),
            RandomSeed = VideoJobRandomSeedToggle.IsOn,
        };

    private void UpdateJobParameterSummary()
    {
        if (_settingsProvider is null || _profileProvider is null) return;
        var settings = ReadJobOverrides().ApplyTo(CurrentSettings);
        // Same meta style as chat "上下文 / 输出上限": compact values only, no extra labels.
        VideoPresetText.Text = $"{settings.Width} × {settings.Height}";
        VideoDurationSummaryText.Text = $"{settings.DurationSeconds} 秒";
    }

    private void VideoJobParameter_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_populatingJobParameters) UpdateJobParameterSummary();
    }

    private void VideoJobRandomSeedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_populatingJobParameters) UpdateJobParameterSummary();
    }

    private void ResetVideoJobParametersButton_Click(object sender, RoutedEventArgs e)
        => RefreshPresetSummary();

    private void VideoJobParametersToggle_Click(object sender, RoutedEventArgs e)
        => SetJobParametersExpanded(VideoJobParametersDetails.Visibility != Visibility.Visible);

    private void SetJobParametersExpanded(bool expanded)
    {
        VideoJobParametersDetails.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        VideoJobParametersChevron.Glyph = expanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(VideoJobParametersToggle, expanded ? "收起本次生成参数" : "展开本次生成参数");
        UpdatePromptLayout();
    }

    private void UpdatePromptLayout()
    {
        if (HasAppliedTemplate) return;
        ComposerTextBoxLayout.Apply(
            VideoPromptInput,
            ActualHeight,
            VideoJobParametersDetails.Visibility == Visibility.Visible);
    }

    private void SetGenerating(bool generating)
    {
        IsGenerating = generating;
        if (generating)
        {
            _jobCts?.Dispose();
            _jobCts = new CancellationTokenSource();
            StartElapsedTicker();
        }
        else
        {
            StopElapsedTicker();
        }
        RefreshGeneratingUi();
    }

    private void StartElapsedTicker()
    {
        _elapsedTicker ??= DispatcherQueue.CreateTimer();
        _elapsedTicker.Interval = TimeSpan.FromSeconds(1);
        _elapsedTicker.IsRepeating = true;
        _elapsedTicker.Tick -= ElapsedTicker_Tick;
        _elapsedTicker.Tick += ElapsedTicker_Tick;
        _elapsedTicker.Start();
    }

    private void StopElapsedTicker()
    {
        if (_elapsedTicker is null) return;
        _elapsedTicker.Stop();
        _elapsedTicker.Tick -= ElapsedTicker_Tick;
    }

    private void ElapsedTicker_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!IsActiveSessionGenerating || !_elapsed.IsRunning) return;
        if (_lastProgressObservation is { } observation)
            ApplyProgressUi(observation with { Elapsed = DisplayElapsed });
    }

    private void RefreshGeneratingUi()
    {
        var generating = IsActiveSessionGenerating;
        var queued = IsActiveSessionQueued;
        var occupied = HasRunningJob && !generating;
        StartVideoButton.IsEnabled = true;
        CancelVideoButton.IsEnabled = generating || queued;
        VideoPromptInput.IsEnabled = true;
        ExpandVideoInputButton.IsEnabled = true;
        VideoConditioningModeBox.IsEnabled = true;
        PickFirstFrameButton.IsEnabled = true;
        ClearFirstFrameButton.IsEnabled = true;
        PickLastFrameButton.IsEnabled = true;
        ClearLastFrameButton.IsEnabled = true;
        ReferenceMediaHost.IsHitTestVisible = true;
        if (generating)
        {
            VideoJobProgress.Maximum = 100;
            if (_lastProgressObservation is null)
            {
                VideoJobProgress.IsIndeterminate = true;
                VideoJobProgress.Value = 0;
                VideoJobPercentText.Text = "0%";
                VideoJobPercentText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            VideoJobProgress.IsIndeterminate = false;
            VideoJobPercentText.Visibility = Visibility.Collapsed;
        }
        VideoJobProgress.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        CancelVideoButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        if (queued)
            CancelVideoButton.Visibility = Visibility.Visible;
        if (generating || occupied || queued)
        {
            RetryVideoButton.Visibility = Visibility.Collapsed;
            ResumeVideoButton.Visibility = Visibility.Collapsed;
        }
        UpdateOpenOutputButtonState();
    }

    /// <summary>
    /// Merge multi-source progress into status text and the determinate/indeterminate bar.
    /// Adapter-agnostic: new models only need to fill <see cref="VideoProgressObservation"/>.
    /// </summary>
    private void ApplyProgressUi(VideoProgressObservation observation, bool updateStatusText = true)
    {
        if (!IsActiveSessionGenerating) return;
        _lastProgressObservation = observation;
        var estimate = VideoProgressEstimator.Estimate(observation);
        if (updateStatusText)
        {
            var text = VideoProgressEstimator.FormatLiveStatus(
                StatusLabel(observation.Status),
                estimate,
                observation.Elapsed);
            if (IsActiveSessionQueued)
                text += "  下个视频已排队";
            VideoJobStatus.Text = text;
        }

        if (!IsGenerating) return;

        var percent = VideoProgressEstimator.FormatPercent(estimate);
        VideoJobPercentText.Text = percent;
        VideoJobPercentText.Visibility = string.IsNullOrWhiteSpace(percent) ? Visibility.Collapsed : Visibility.Visible;
        var fraction = estimate.Fraction ?? 0;
        if (estimate.HasDeterminateProgress
            && estimate.Source is not VideoProgressSource.Phase
            && fraction > 0)
        {
            VideoJobProgress.IsIndeterminate = false;
            VideoJobProgress.Maximum = 100;
            VideoJobProgress.Value = Math.Clamp(fraction * 100, 0, 100);
        }
        else
        {
            // Phase-only / unknown: pulse the bar instead of pinning a fake 5%.
            VideoJobProgress.IsIndeterminate = true;
        }
    }

    private void RefreshResumeUi()
    {
        var checkpoint = _checkpointStore?.Load();
        var canResume = !IsGenerating && checkpoint is { CanResume: true }
            && string.Equals(checkpoint.ProfileId, CurrentProfile.Id, StringComparison.Ordinal);
        ResumeVideoButton.Visibility = canResume ? Visibility.Visible : Visibility.Collapsed;
        if (canResume && string.IsNullOrWhiteSpace(_fullError))
            VideoJobStatus.Text = $"可继续上次任务：已完成 {checkpoint!.CompletedSteps}/{checkpoint.Steps} 步";
    }

    private async Task LoadVideoPreviewAsync(string output)
    {
        var file = await StorageFile.GetFileFromPathAsync(output);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        _mediaWidth = properties.Width;
        _mediaHeight = properties.Height;
        UpdateVideoPreviewLayout();
        VideoPreview.Source = MediaSource.CreateFromStorageFile(file);
        VideoPreview.Visibility = Visibility.Visible;
        VideoEmptyState.Visibility = Visibility.Collapsed;
        _previewOutputPath = output;
        UpdateOpenOutputButtonState();
    }

    private async Task RestoreActiveSessionPreviewAsync()
    {
        if (_sessions.Sessions.Count == 0) return;
        var preview = VideoPreviewHistory.ResolveSessionPreview(_sessions.Active.LastOutputPath, null);
        if (preview is null || IsActiveSessionGenerating) return;
        try
        {
            await LoadVideoPreviewAsync(preview);
            if (string.IsNullOrWhiteSpace(VideoJobStatus.Text) || VideoJobStatus.Text == "等待开始")
                VideoJobStatus.Text = $"窗口成片  {Path.GetFileName(preview)}";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ClearVideoPreview()
    {
        VideoPreview.Source = null;
        VideoPreview.Visibility = Visibility.Collapsed;
        VideoEmptyState.Visibility = Visibility.Visible;
        _previewOutputPath = null;
        _mediaWidth = 0;
        _mediaHeight = 0;
        UpdateOpenOutputButtonState();
    }

    private void VideoPreviewRegion_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVideoPreviewLayout();

    private void UpdateVideoPreviewLayout()
    {
        var size = VideoPreviewLayout.Fit(VideoPreviewRegion.ActualWidth, VideoPreviewRegion.ActualHeight, _mediaWidth, _mediaHeight);
        VideoPreviewFrame.Width = size.Width;
        VideoPreviewFrame.Height = size.Height;
    }

    private void VideoPreview_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOverPreview = true;
        UpdateTransportControlsVisibility();
    }

    private void VideoPreview_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPointerOverPreview = false;
        UpdateTransportControlsVisibility();
    }

    private void VideoPreview_GotFocus(object sender, RoutedEventArgs e)
    {
        _isPreviewKeyboardFocused = true;
        UpdateTransportControlsVisibility();
    }

    private void VideoPreview_LostFocus(object sender, RoutedEventArgs e)
    {
        _isPreviewKeyboardFocused = false;
        UpdateTransportControlsVisibility();
    }

    private void UpdateTransportControlsVisibility()
        => VideoPreview.AreTransportControlsEnabled = VideoPreviewInteraction.ShouldShowControls(
            _isPointerOverPreview,
            _isPreviewKeyboardFocused);

    private async void VideoPromptInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox input) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (IsMentionListOpen())
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideMentionPopup();
                return;
            }
            if (e.Key is VirtualKey.Down or VirtualKey.Up)
            {
                e.Handled = true;
                MoveMentionSelection(e.Key == VirtualKey.Down ? 1 : -1);
                return;
            }
            if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
            {
                e.Handled = true;
                InsertSelectedMention();
                return;
            }
        }

        if (!shift && (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down) && !input.Text.Contains('\n'))
        {
            e.Handled = true;
            var recalled = e.Key == VirtualKey.Up
                ? _promptHistory.Previous(input.Text)
                : _promptHistory.Next(input.Text);
            input.Text = recalled;
            input.SelectionStart = input.Text.Length;
            return;
        }

        if (ChatInteractionPolicy.ResolveEnter(e.Key == VirtualKey.Enter, shift) != ComposerKeyAction.Send)
            return;
        e.Handled = true;
        await StartVideoAsync();
    }

    private void ExpandVideoInputButton_Click(object sender, RoutedEventArgs e)
        => ExpandedTextRequested?.Invoke(
            HasAppliedTemplate ? "查看并修改完整提示词" : "展开编辑视频提示词",
            HasAppliedTemplate ? CurrentAssembledPrompt() : (VideoPromptInput.Text ?? ""),
            false);

    internal void ApplyExpandedPrompt(string text)
    {
        if (HasAppliedTemplate)
        {
            _assembledOverride = text;
            UpdateAssembledOverrideHint();
            FlushActiveDraft();
            return;
        }
        VideoPromptInput.Text = text;
    }

    internal async Task GenerateFromExpandedPromptAsync(string text)
    {
        ApplyExpandedPrompt(text);
        await StartVideoAsync();
    }

    private void SetVideoError(string? error)
    {
        _fullError = string.IsNullOrWhiteSpace(error) ? null : error;
        VideoErrorDetailsButton.Visibility = _fullError is null ? Visibility.Collapsed : Visibility.Visible;
        if (_fullError is not null) VideoJobStatus.Text = VideoErrorPresentation.Summarize(_fullError);
        RetryVideoButton.Visibility = !IsGenerating && _fullError is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VideoErrorDetailsButton_Click(object sender, RoutedEventArgs e)
        => ExpandedTextRequested?.Invoke("视频生成错误", _fullError ?? VideoJobStatus.Text, true);

    private static string StatusLabel(string status) => status switch
    {
        "pending" => "等待执行",
        "in_progress" => "正在生成",
        "completed" => "生成完成",
        "failed" => "生成失败",
        "cancelled" => "已取消",
        _ => status,
    };

    private VideoMediaInputs ReadMediaInputs()
        => new(
            _conditioningMode,
            _firstFramePath,
            _lastFramePath,
            _referenceImagePaths.FirstOrDefault(),
            CurrentProfile.EffectiveMedia.DefaultRefImageSize,
            _referenceImagePaths.ToArray(),
            _referenceVideoPaths.ToArray(),
            _referenceAudioPaths.ToArray());

    private void RefreshMediaModeUi()
    {
        if (_profileProvider is null) return;
        var media = CurrentProfile.EffectiveMedia;
        var modes = media.EnabledModes();
        _suppressModeUi = true;
        try
        {
            VideoConditioningModeBox.Items.Clear();
            foreach (var mode in modes)
            {
                VideoConditioningModeBox.Items.Add(new ComboBoxItem
                {
                    Content = VideoMediaCapabilities.DisplayName(mode),
                    Tag = mode,
                });
            }

            if (!modes.Contains(_conditioningMode))
            {
                if (VideoMediaCapabilities.IsReferenceFamily(_conditioningMode)
                    && modes.Contains(VideoConditioningMode.SingleReferenceImage))
                    _conditioningMode = VideoConditioningMode.SingleReferenceImage;
                else
                    _conditioningMode = media.ResolveDefaultMode();
            }

            for (var i = 0; i < VideoConditioningModeBox.Items.Count; i++)
            {
                if (VideoConditioningModeBox.Items[i] is ComboBoxItem item && item.Tag is VideoConditioningMode mode
                    && mode == _conditioningMode)
                {
                    VideoConditioningModeBox.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _suppressModeUi = false;
        }

        UpdateMediaSlotVisibility();
        RebuildReferenceMediaSlots();
        UpdateMediaPathLabels();
    }

    private void VideoConditioningModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModeUi) return;
        if (VideoConditioningModeBox.SelectedItem is ComboBoxItem { Tag: VideoConditioningMode mode })
            _conditioningMode = mode;
        UpdateMediaSlotVisibility();
        if (!_applyingSession) FlushActiveDraft();
    }

    private void UpdateMediaSlotVisibility()
    {
        var media = _profileProvider is null ? VideoMediaCapabilities.MiniMaxH3Local8Gb : CurrentProfile.EffectiveMedia;
        var showFirst = _conditioningMode is VideoConditioningMode.FirstFrame or VideoConditioningMode.FirstLastFrame;
        var showLast = _conditioningMode is VideoConditioningMode.LastFrame or VideoConditioningMode.FirstLastFrame;
        var showRefFamily = VideoMediaCapabilities.IsReferenceFamily(_conditioningMode);
        var showRef = showRefFamily && (
            media.MaxReferenceImages > 0 || media.MaxReferenceVideos > 0 || media.MaxReferenceAudios > 0);
        var showStrip = showFirst || showLast || showRef;
        FirstFrameSlot.Visibility = showFirst ? Visibility.Visible : Visibility.Collapsed;
        LastFrameSlot.Visibility = showLast ? Visibility.Visible : Visibility.Collapsed;
        ReferenceMediaHost.Visibility = showRef ? Visibility.Visible : Visibility.Collapsed;
        VideoMediaSlots.Visibility = showFirst || showLast ? Visibility.Visible : Visibility.Collapsed;
        // Keep text-mode height aligned with chat: hide the whole media strip when unused.
        VideoMediaCard.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        RebuildReferenceMediaSlots();
        UpdatePromptModeHints();
    }

    private void UpdatePromptModeHints()
    {
        VideoPromptInput.PlaceholderText = VideoMediaCapabilities.PromptPlaceholder(_conditioningMode);
        var tagHint = VideoMediaCapabilities.IsReferenceFamily(_conditioningMode)
            ? VideoMediaCapabilities.ReferenceFamilyPromptTagHint(
                _referenceImagePaths.Count(path => !string.IsNullOrWhiteSpace(path)),
                _referenceVideoPaths.Count(path => !string.IsNullOrWhiteSpace(path)),
                _referenceAudioPaths.Count(path => !string.IsNullOrWhiteSpace(path)))
            : VideoMediaCapabilities.PromptTagHint(_conditioningMode);
        if (string.IsNullOrWhiteSpace(tagHint))
        {
            VideoPromptTagHintText.Text = string.Empty;
            VideoPromptTagHintText.Visibility = Visibility.Collapsed;
        }
        else
        {
            VideoPromptTagHintText.Text = tagHint;
            // Hint lives inside the media strip (only visible for image modes).
            VideoPromptTagHintText.Visibility = Visibility.Visible;
        }

        var mentionable = VideoMediaCapabilities.IsReferenceFamily(_conditioningMode)
            || VideoMediaCapabilities.IsKeyframeFamily(_conditioningMode);
        if (VideoMentionHintText is not null)
            VideoMentionHintText.Visibility = mentionable ? Visibility.Visible : Visibility.Collapsed;
        if (InsertVideoPromptTemplateButton is not null)
        {
            var canInsert = CanInsertPromptTemplate;
            InsertVideoPromptTemplateButton.Visibility = canInsert ? Visibility.Visible : Visibility.Collapsed;
            if (!canInsert && VideoTemplatePicker is not null)
                VideoTemplatePicker.Visibility = Visibility.Collapsed;
        }
        if (!mentionable) HideMentionPopup();
        UpdateAppliedTemplateUi();
    }

    internal IReadOnlyList<VideoPromptMention> MentionCandidates()
        => VideoPromptMentions.Candidates(
            _conditioningMode,
            _referenceImagePaths,
            _referenceVideoPaths,
            _referenceAudioPaths);

    internal bool CanInsertPromptTemplate
        => VideoPromptTemplates.CanInsertAny(
            _conditioningMode,
            CurrentTemplateMedia().Count(item => item.Kind == "Picture"),
            CurrentTemplateMedia().Count(item => item.Kind == "Video"),
            CurrentTemplateMedia().Count(item => item.Kind == "Audio"));

    internal IReadOnlyList<VideoPromptMediaChoice> CurrentTemplateMedia()
    {
        if (_conditioningMode is VideoConditioningMode.FirstFrame)
            return PictureChoices([_firstFramePath]);
        if (_conditioningMode is VideoConditioningMode.LastFrame)
            return PictureChoices([_lastFramePath]);
        if (_conditioningMode is VideoConditioningMode.FirstLastFrame)
            return PictureChoices([_firstFramePath, _lastFramePath]);
        if (VideoMediaCapabilities.IsReferenceFamily(_conditioningMode))
        {
            return PictureChoices(_referenceImagePaths)
                .Concat(IndexedChoices("Video", _referenceVideoPaths))
                .Concat(IndexedChoices("Audio", _referenceAudioPaths))
                .ToArray();
        }
        return [];
    }

    private static IReadOnlyList<VideoPromptMediaChoice> PictureChoices(IEnumerable<string?> paths)
        => IndexedChoices("Picture", paths);

    private static IReadOnlyList<VideoPromptMediaChoice> IndexedChoices(string kind, IEnumerable<string?> paths)
        => (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select((path, offset) => new VideoPromptMediaChoice(kind, offset + 1, Path.GetFileName(path)!))
            .ToArray();

    private void InsertVideoPromptTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (VideoTemplatePicker is null) return;
        if (VideoTemplatePicker.Visibility == Visibility.Visible)
        {
            VideoTemplatePicker.Visibility = Visibility.Collapsed;
            return;
        }
        VideoTemplatePicker.Refresh(
            _conditioningMode,
            CurrentTemplateMedia(),
            _templatePhrasesProvider?.Invoke());
        VideoTemplatePicker.Visibility = Visibility.Visible;
    }

    private void VideoTemplatePicker_InsertRequested(object sender, VideoPromptTemplateInsert insert)
    {
        ApplyPromptTemplate(insert.Title, insert.Text);
        if (VideoTemplatePicker is not null)
            VideoTemplatePicker.Visibility = Visibility.Collapsed;
    }

    private void VideoTemplatePicker_Cancelled(object sender, EventArgs e)
    {
        if (VideoTemplatePicker is not null)
            VideoTemplatePicker.Visibility = Visibility.Collapsed;
    }

    internal bool HasAppliedTemplate => !string.IsNullOrWhiteSpace(_appliedTemplate);

    internal string ComposerText => HasAppliedTemplate
        ? VideoPromptComposer.WriteExtras(ReadSlotExtras(), CurrentPhrases())
        : VideoPromptInput.Text ?? "";

    private VideoPromptTemplatePhrases CurrentPhrases()
        => (_templatePhrasesProvider?.Invoke() ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();

    private VideoPromptExtras ReadSlotExtras()
        => new(
            Action: ExtraActionBox?.Text ?? "",
            Sound: ExtraSoundBox?.Text ?? "",
            Music: ExtraMusicBox?.Text ?? "",
            Identity: ExtraIdentityBox?.Text ?? "");

    internal string CurrentAssembledPrompt()
        => VideoPromptComposer.Compose(
            ComposerText,
            _appliedTemplate,
            CurrentPhrases(),
            _assembledOverride,
            CurrentDurationSeconds());

    private int CurrentDurationSeconds()
    {
        if (VideoJobDurationBox is null) return CurrentSettings.DurationSeconds;
        return SettingsNumberInput.Whole(VideoJobDurationBox.Text, VideoJobDurationBox.Value);
    }

    private void ApplySessionExtras(VideoSession session)
    {
        var phrases = CurrentPhrases();
        var extras = !string.IsNullOrWhiteSpace(session.ExtraAction)
            || !string.IsNullOrWhiteSpace(session.ExtraSound)
            || !string.IsNullOrWhiteSpace(session.ExtraMusic)
            || !string.IsNullOrWhiteSpace(session.ExtraIdentity)
            ? new VideoPromptExtras(session.ExtraAction, session.ExtraSound, session.ExtraMusic, session.ExtraIdentity)
            : VideoPromptComposer.ReadExtras(session.DraftPrompt, phrases);
        WriteSlotBoxes(extras);
    }

    private void WriteSlotBoxes(VideoPromptExtras extras)
    {
        _suppressSlotOverrideClear = true;
        try
        {
            if (ExtraActionBox is not null) ExtraActionBox.Text = extras.Action;
            if (ExtraSoundBox is not null) ExtraSoundBox.Text = extras.Sound;
            if (ExtraMusicBox is not null) ExtraMusicBox.Text = extras.Music;
            if (ExtraIdentityBox is not null) ExtraIdentityBox.Text = extras.Identity;
        }
        finally
        {
            _suppressSlotOverrideClear = false;
        }
    }

    internal void ApplyPromptTemplate(string title, string text, string? extrasSource = null)
    {
        var phrases = CurrentPhrases();
        var current = extrasSource ?? ComposerText;
        var extras = VideoPromptComposer.ReadExtras(
            VideoPromptComposer.ExtractExtras(current),
            phrases);
        WriteSlotBoxes(extras);
        _appliedTemplate = text;
        _appliedTemplateTitle = string.IsNullOrWhiteSpace(title) ? "已套模板" : title.Trim();
        _assembledOverride = null;
        UpdateAppliedTemplateUi();
        FlushActiveDraft();
        ExtraActionBox?.Focus(FocusState.Programmatic);
    }

    private void ClearAppliedTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var action = ExtraActionBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(action) && string.IsNullOrWhiteSpace(VideoPromptInput.Text))
            VideoPromptInput.Text = action;
        _appliedTemplate = null;
        _appliedTemplateTitle = null;
        _assembledOverride = null;
        UpdateAppliedTemplateUi();
        FlushActiveDraft();
    }

    private void PromptSlot_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingSession) return;
        if (!_suppressSlotOverrideClear && !string.IsNullOrWhiteSpace(_assembledOverride))
        {
            _assembledOverride = null;
            UpdateAssembledOverrideHint();
        }
        ScheduleDraftPersist();
        if (ReferenceEquals(sender, ExtraActionBox) || ReferenceEquals(sender, ExtraIdentityBox))
            UpdateMentionPopup();
    }

    private void UpdateAppliedTemplateUi()
    {
        var applied = HasAppliedTemplate;
        if (AppliedTemplateText is not null)
        {
            AppliedTemplateText.Text = applied ? $"已套：{_appliedTemplateTitle}" : string.Empty;
            AppliedTemplateText.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ClearAppliedTemplateButton is not null)
            ClearAppliedTemplateButton.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        if (InsertVideoPromptTemplateButton is not null && InsertVideoPromptTemplateButton.Visibility == Visibility.Visible)
            InsertVideoPromptTemplateButton.Content = applied ? "更换模板" : "插入模板";
        if (VideoPromptInput is not null)
            VideoPromptInput.Visibility = applied ? Visibility.Collapsed : Visibility.Visible;
        if (PromptSlotsHost is not null)
            PromptSlotsHost.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
        if (ExpandVideoInputButton is not null)
        {
            ExpandVideoInputButton.Content = applied ? "查看正文" : "展开";
            AutomationProperties.SetName(ExpandVideoInputButton, applied ? "查看并修改完整提示词" : "展开视频提示词");
        }
        ApplySlotHeaders(CurrentPhrases());
        UpdateAssembledOverrideHint();
        UpdatePromptLayout();
    }

    private void ApplySlotHeaders(VideoPromptTemplatePhrases phrases)
    {
        foreach (var (slot, prefix) in phrases.ExtraPrefixSlots())
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            var label = slot switch
            {
                "description" => ExtraActionLabel,
                "overall_soundscape" => ExtraSoundLabel,
                "non_diegetic_music" => ExtraMusicLabel,
                "subject_definitions" => ExtraIdentityLabel,
                _ => null,
            };
            if (label is not null)
                label.Text = prefix;
            var box = slot switch
            {
                "description" => ExtraActionBox,
                "overall_soundscape" => ExtraSoundBox,
                "non_diegetic_music" => ExtraMusicBox,
                "subject_definitions" => ExtraIdentityBox,
                _ => null,
            };
            if (box is not null)
                AutomationProperties.SetName(box, prefix);
        }
    }

    private void UpdateAssembledOverrideHint()
    {
        if (AssembledOverrideHint is null) return;
        AssembledOverrideHint.Visibility = HasAppliedTemplate && !string.IsNullOrWhiteSpace(_assembledOverride)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool IsMentionListOpen()
        => VideoMentionList is not null && VideoMentionList.Visibility == Visibility.Visible;

    private TextBox? ActivePromptBox()
    {
        if (!HasAppliedTemplate) return VideoPromptInput;
        foreach (var box in new[] { ExtraActionBox, ExtraIdentityBox, ExtraSoundBox, ExtraMusicBox })
        {
            if (box is not null && box.FocusState != FocusState.Unfocused)
                return box;
        }
        return ExtraActionBox;
    }

    private void UpdateMentionPopup()
    {
        var box = ActivePromptBox();
        if (box is null || VideoMentionList is null) return;
        if (_applyingSession)
        {
            HideMentionPopup();
            return;
        }

        var text = box.Text ?? "";
        var caret = box.SelectionStart;
        var candidates = VideoPromptMentions.Candidates(
            _conditioningMode,
            _referenceImagePaths,
            _referenceVideoPaths,
            _referenceAudioPaths);
        if (candidates.Count == 0
            || !VideoPromptMentions.TryGetActiveQuery(text, caret, out _, out var query))
        {
            HideMentionPopup();
            return;
        }

        var filtered = VideoPromptMentions.Filter(candidates, query);
        if (filtered.Count == 0)
        {
            HideMentionPopup();
            return;
        }

        VideoMentionList.ItemsSource = filtered.ToArray();
        if (VideoMentionList.SelectedIndex < 0)
            VideoMentionList.SelectedIndex = 0;
        VideoMentionList.Visibility = Visibility.Visible;
    }

    private void HideMentionPopup()
    {
        if (VideoMentionList is null) return;
        VideoMentionList.Visibility = Visibility.Collapsed;
    }

    private void MoveMentionSelection(int delta)
    {
        if (VideoMentionList.Items.Count == 0) return;
        var next = VideoMentionList.SelectedIndex + delta;
        if (next < 0) next = VideoMentionList.Items.Count - 1;
        if (next >= VideoMentionList.Items.Count) next = 0;
        VideoMentionList.SelectedIndex = next;
        if (VideoMentionList.SelectedItem is not null)
            VideoMentionList.ScrollIntoView(VideoMentionList.SelectedItem);
    }

    private void VideoMentionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoPromptMention item)
            InsertMention(item.Tag);
    }

    private void InsertSelectedMention()
    {
        var item = VideoMentionList.SelectedItem as VideoPromptMention
                   ?? VideoMentionList.Items.OfType<VideoPromptMention>().FirstOrDefault();
        if (item is not null)
            InsertMention(item.Tag);
    }

    private void InsertMention(string tag)
    {
        var box = ActivePromptBox();
        if (box is null) return;
        var text = box.Text ?? "";
        var caret = box.SelectionStart;
        if (!VideoPromptMentions.TryGetActiveQuery(text, caret, out var atIndex, out _))
        {
            HideMentionPopup();
            return;
        }

        var next = VideoPromptMentions.Insert(text, atIndex, caret, tag, out var newCaret);
        box.Text = next;
        box.SelectionStart = newCaret;
        HideMentionPopup();
        box.Focus(FocusState.Programmatic);
    }

    private void UpdateMediaPathLabels()
    {
        FirstFramePathText.Text = string.IsNullOrWhiteSpace(_firstFramePath) ? "未选择" : Path.GetFileName(_firstFramePath);
        LastFramePathText.Text = string.IsNullOrWhiteSpace(_lastFramePath) ? "未选择" : Path.GetFileName(_lastFramePath);
        SetStillThumbnail(FirstFrameThumbHost, FirstFrameThumb, _firstFramePath);
        SetStillThumbnail(LastFrameThumbHost, LastFrameThumb, _lastFramePath);
        RebuildReferenceMediaSlots();
        if (!_applyingSession) FlushActiveDraft();
    }

    private void RebuildReferenceMediaSlots()
    {
        if (ReferenceMediaHost is null) return;
        var media = CurrentProfileOrNull()?.EffectiveMedia ?? VideoMediaCapabilities.MiniMaxH3Local8Gb;
        var imageMax = Math.Max(0, media.MaxReferenceImages);
        var videoMax = Math.Max(0, media.MaxReferenceVideos);
        var audioMax = Math.Max(0, media.MaxReferenceAudios);

        CompactReferencePaths(_referenceImagePaths, imageMax);
        CompactReferencePaths(_referenceVideoPaths, videoMax);
        CompactReferencePaths(_referenceAudioPaths, audioMax);

        var items = new List<UIElement>();
        items.AddRange(CreateReferenceChips(_referenceImagePaths, "图", "image", PickImagePathAsync));
        items.AddRange(CreateReferenceChips(_referenceVideoPaths, "视频", "video", PickVideoPathAsync));
        items.AddRange(CreateReferenceChips(_referenceAudioPaths, "音频", "audio", PickAudioPathAsync));
        foreach (var action in VideoMediaCapabilities.ReferenceAddActions(
            _referenceImagePaths.Count, imageMax,
            _referenceVideoPaths.Count, videoMax,
            _referenceAudioPaths.Count, audioMax))
        {
            items.Add(CreateReferenceAddButton(action, imageMax, videoMax, audioMax));
        }

        ReferenceMediaHost.Items.Clear();
        foreach (var item in items)
            ReferenceMediaHost.Items.Add(item);
        UpdatePromptModeHints();
    }

    private static void CompactReferencePaths(List<string> paths, int max)
    {
        var compact = VideoMediaInputs.MergeReferencePaths(paths, null, max).ToList();
        paths.Clear();
        paths.AddRange(compact);
    }

    private IEnumerable<UIElement> CreateReferenceChips(
        List<string> paths,
        string labelPrefix,
        string mediaKind,
        Func<Task<string?>> pickOneAsync)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            var index = i;
            yield return CreateReferenceChip(
                $"{labelPrefix}{i + 1}",
                paths[i],
                mediaKind,
                replaceAsync: async () =>
                {
                    var path = await pickOneAsync();
                    if (path is null) return;
                    if (index < paths.Count) paths[index] = path;
                    RebuildReferenceMediaSlots();
                },
                clear: () =>
                {
                    if (index < paths.Count) paths.RemoveAt(index);
                    RebuildReferenceMediaSlots();
                });
        }
    }

    private Button CreateReferenceAddButton(ReferenceAddAction action, int imageMax, int videoMax, int audioMax)
    {
        var add = new Button
        {
            Content = action.Label,
            Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(add, action.Name);
        ToolTipService.SetToolTip(add, action.Tooltip);
        add.Click += async (_, _) =>
        {
            switch (action.Kind)
            {
                case "image":
                    await AppendPickedReferences(_referenceImagePaths, imageMax, PickImagePathsAsync);
                    break;
                case "video":
                    await AppendPickedReferences(_referenceVideoPaths, videoMax, PickVideoPathsAsync);
                    break;
                case "audio":
                    await AppendPickedReferences(_referenceAudioPaths, audioMax, PickAudioPathsAsync);
                    break;
            }
        };
        return add;
    }

    private async Task AppendPickedReferences(
        List<string> paths,
        int max,
        Func<Task<IReadOnlyList<string>>> pickManyAsync)
    {
        var remaining = max - paths.Count;
        if (remaining <= 0) return;
        var picked = await pickManyAsync();
        if (picked.Count == 0) return;
        var merged = VideoMediaInputs.MergeReferencePaths(paths, picked.Take(remaining), max);
        paths.Clear();
        paths.AddRange(merged);
        RebuildReferenceMediaSlots();
    }

    private Border CreateReferenceChip(string label, string fullPath, string mediaKind, Func<Task> replaceAsync, Action clear)
    {
        var displayName = string.IsNullOrWhiteSpace(fullPath) ? "未选择" : Path.GetFileName(fullPath);
        var nameText = new TextBlock
        {
            Text = displayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 112,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameButton = new Button
        {
            Content = nameText,
            Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
            Padding = new Thickness(8, 0, 8, 0),
            MinWidth = 0,
            MinHeight = 0,
        };
        nameButton.Click += async (_, _) => await replaceAsync();
        ToolTipService.SetToolTip(nameButton, displayName + "（点击更换）");

        var clearButton = new Button
        {
            Content = "清除",
            Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
        };
        AutomationProperties.SetName(clearButton, "清除" + label);
        clearButton.Click += (_, _) => clear();

        var row = new Grid
        {
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelText = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["ComposerHintTextStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumb = CreateThumbnailShell(mediaKind);
        _ = FillThumbnailAsync(thumb, fullPath, mediaKind);
        Grid.SetColumn(thumb, 0);
        Grid.SetColumn(labelText, 1);
        Grid.SetColumn(nameButton, 2);
        Grid.SetColumn(clearButton, 3);
        row.Children.Add(thumb);
        row.Children.Add(labelText);
        row.Children.Add(nameButton);
        row.Children.Add(clearButton);

        return new Border
        {
            Child = row,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
        };
    }

    private static void SetStillThumbnail(Border host, Image image, string? path)
    {
        if (host is null || image is null) return;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            image.Source = null;
            host.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            image.Source = new BitmapImage
            {
                DecodePixelWidth = 72,
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(path),
            };
            host.Visibility = Visibility.Visible;
        }
        catch
        {
            image.Source = null;
            host.Visibility = Visibility.Collapsed;
        }
    }

    private static Border CreateThumbnailShell(string mediaKind)
    {
        return new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateMediaGlyph(mediaKind),
        };
    }

    private static FontIcon CreateMediaGlyph(string mediaKind)
        => new()
        {
            Glyph = mediaKind switch
            {
                "video" => "\uE714",
                "audio" => "\uE8D6",
                _ => "\uE91B",
            },
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        };

    private static async Task FillThumbnailAsync(Border host, string path, string mediaKind)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            if (mediaKind == "image" || IsImagePath(path))
            {
                host.Child = new Image
                {
                    Width = 36,
                    Height = 36,
                    Stretch = Stretch.UniformToFill,
                    Source = new BitmapImage
                    {
                        DecodePixelWidth = 72,
                        CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                        UriSource = new Uri(path),
                    },
                };
                return;
            }

            var file = await StorageFile.GetFileFromPathAsync(path);
            var mode = mediaKind == "audio" || IsAudioPath(path)
                ? ThumbnailMode.MusicView
                : ThumbnailMode.VideosView;
            using var thumb = await file.GetThumbnailAsync(mode, 72, ThumbnailOptions.UseCurrentScale);
            if (thumb is null || thumb.Size == 0) return;

            var memory = new InMemoryRandomAccessStream();
            await RandomAccessStream.CopyAsync(thumb, memory);
            memory.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(memory);
            host.Child = new Image
            {
                Width = 36,
                Height = 36,
                Stretch = Stretch.UniformToFill,
                Source = bitmap,
            };
        }
        catch
        {
            // Keep the kind glyph when a still cannot be decoded.
        }
    }

    private static bool IsImagePath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudioPath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureReferenceSlot(int index)
    {
        while (_referenceImagePaths.Count <= index)
            _referenceImagePaths.Add(string.Empty);
    }

    private async void PickFirstFrameButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickImagePathAsync();
        if (path is null) return;
        _firstFramePath = path;
        UpdateMediaPathLabels();
    }

    private async void PickLastFrameButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickImagePathAsync();
        if (path is null) return;
        _lastFramePath = path;
        UpdateMediaPathLabels();
    }

    private async void PickReferenceImageButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickImagePathAsync();
        if (path is null) return;
        EnsureReferenceSlot(0);
        _referenceImagePaths[0] = path;
        UpdateMediaPathLabels();
    }

    private void ClearFirstFrameButton_Click(object sender, RoutedEventArgs e)
    {
        _firstFramePath = null;
        UpdateMediaPathLabels();
    }

    private void ClearLastFrameButton_Click(object sender, RoutedEventArgs e)
    {
        _lastFramePath = null;
        UpdateMediaPathLabels();
    }

    private void ClearReferenceImageButton_Click(object sender, RoutedEventArgs e)
    {
        _referenceImagePaths.Clear();
        _referenceImagePaths.Add(string.Empty);
        UpdateMediaPathLabels();
    }

    private Task<string?> PickImagePathAsync()
        => PickMediaPathAsync(
            PickerLocationId.PicturesLibrary,
            PickerViewMode.Thumbnail,
            [".png", ".jpg", ".jpeg", ".webp", ".bmp"],
            "选择图片失败");

    private Task<IReadOnlyList<string>> PickImagePathsAsync()
        => PickMediaPathsAsync(
            PickerLocationId.PicturesLibrary,
            PickerViewMode.Thumbnail,
            [".png", ".jpg", ".jpeg", ".webp", ".bmp"],
            "选择图片失败");

    private Task<string?> PickVideoPathAsync()
        => PickMediaPathAsync(
            PickerLocationId.VideosLibrary,
            PickerViewMode.Thumbnail,
            [".mp4", ".webm", ".mov", ".mkv", ".avi"],
            "选择视频失败");

    private Task<IReadOnlyList<string>> PickVideoPathsAsync()
        => PickMediaPathsAsync(
            PickerLocationId.VideosLibrary,
            PickerViewMode.Thumbnail,
            [".mp4", ".webm", ".mov", ".mkv", ".avi"],
            "选择视频失败");

    private Task<string?> PickAudioPathAsync()
        => PickMediaPathAsync(
            PickerLocationId.MusicLibrary,
            PickerViewMode.List,
            [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac"],
            "选择音频失败");

    private Task<IReadOnlyList<string>> PickAudioPathsAsync()
        => PickMediaPathsAsync(
            PickerLocationId.MusicLibrary,
            PickerViewMode.List,
            [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac"],
            "选择音频失败");

    private FileOpenPicker CreateMediaPicker(
        PickerLocationId startLocation,
        PickerViewMode viewMode,
        IReadOnlyList<string> extensions)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = startLocation,
            ViewMode = viewMode,
        };
        foreach (var extension in extensions)
            picker.FileTypeFilter.Add(extension);
        var hwnd = App.WindowHandle;
        if (hwnd != 0)
            InitializeWithWindow.Initialize(picker, hwnd);
        return picker;
    }

    private async Task<string?> PickMediaPathAsync(
        PickerLocationId startLocation,
        PickerViewMode viewMode,
        IReadOnlyList<string> extensions,
        string errorPrefix)
    {
        try
        {
            var file = await CreateMediaPicker(startLocation, viewMode, extensions).PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception error)
        {
            SetVideoError($"{errorPrefix}：{error.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> PickMediaPathsAsync(
        PickerLocationId startLocation,
        PickerViewMode viewMode,
        IReadOnlyList<string> extensions,
        string errorPrefix)
    {
        try
        {
            var files = await CreateMediaPicker(startLocation, viewMode, extensions).PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return [];
            return files.Select(file => file.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        }
        catch (Exception error)
        {
            SetVideoError($"{errorPrefix}：{error.Message}");
            return [];
        }
    }
}
