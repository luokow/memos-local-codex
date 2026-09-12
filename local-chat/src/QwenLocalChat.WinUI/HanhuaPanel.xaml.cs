using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QwenLocalChat.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QwenLocalChat_WinUI;

public sealed class HanhuaGpuNeedChangedEventArgs(HanhuaGpuNeed need, string? statusText) : EventArgs
{
    public HanhuaGpuNeed Need { get; } = need;
    public string? StatusText { get; } = statusText;
}

public sealed partial class HanhuaPanel : UserControl
{
    private readonly ObservableCollection<string> _log = [];
    private readonly HanhuaProcessHost _host = new();
    private readonly StringBuilder _detail = new();
    private readonly Stopwatch _elapsed = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _elapsedTicker;
    private HanhuaJobStore? _store;
    private Func<LocalChatSettings>? _settings;
    private Func<bool>? _chatBusy;
    private Func<bool>? _videoBusy;
    private Func<HanhuaGpuNeed, CancellationToken, Task>? _prepareGpu;
    private Func<string, Task<string?>>? _pickFolder;
    private Action<HanhuaEngine>? _persistEngine;
    private Func<string?>? _fillModel;
    private CancellationTokenSource? _runCts;
    private HanhuaJob? _job;
    private IReadOnlyList<HanhuaPhase>? _runPhases;
    private bool _suppressEngine;

    public HanhuaPanel()
    {
        InitializeComponent();
        JobLogList.ItemsSource = _log;
        KindBox.SelectedIndex = 0;
        EngineBox.SelectedIndex = 0;
    }

    public bool IsBusy => _job?.Status is HanhuaJobStatus.Running or HanhuaJobStatus.Cancelling;
    public bool HasInterruptedWork { get; private set; }
    public string? InterruptedWorkSummary { get; private set; }
    private TimeSpan DisplayElapsed => _elapsed.Elapsed;

    public event EventHandler? SettingsRequested;
    public event EventHandler<HanhuaGpuNeedChangedEventArgs>? GpuNeedChanged;

    public void Configure(
        Func<LocalChatSettings> settings,
        Func<bool> chatBusy,
        Func<bool> videoBusy,
        Func<HanhuaGpuNeed, CancellationToken, Task> prepareGpu,
        Func<string, Task<string?>> pickFolder,
        Action<HanhuaEngine> persistEngine,
        string localChatRoot,
        Func<string?>? fillModel = null)
    {
        _settings = settings;
        _chatBusy = chatBusy;
        _videoBusy = videoBusy;
        _prepareGpu = prepareGpu;
        _pickFolder = pickFolder;
        _persistEngine = persistEngine;
        _fillModel = fillModel;
        _store = new HanhuaJobStore(HanhuaJobStore.DefaultPath(localChatRoot));
        _suppressEngine = true;
        SelectTagged(EngineBox, HanhuaEngineCodec.ToJson(HanhuaEngineCodec.Parse(settings().HanhuaEngine)));
        _suppressEngine = false;
        var interrupted = _store.MarkInterruptedIfRunning();
        HasInterruptedWork = interrupted?.CanResume == true;
        InterruptedWorkSummary = HasInterruptedWork
            ? "有未完成的汉化任务。打开汉化页后可继续，不会自动开始。"
            : null;
        if (interrupted is not null)
        {
            _job = interrupted;
            SourcePathBox.Text = interrupted.SourcePath;
            SelectTagged(KindBox, interrupted.Kind == HanhuaKind.Image ? "image" : "game");
            AppendLog(interrupted.Message ?? "上次汉化未完成。");
            SetIdleStatus(interrupted.Message ?? "上次汉化未完成。");
        }
        else
        {
            var catchUp = HanhuaCatchUp.Latest(_store.Load().Jobs);
            if (catchUp is not null)
            {
                _job = catchUp;
                SourcePathBox.Text = catchUp.SourcePath;
                SelectTagged(KindBox, "image");
                OpenHanhuaOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(catchUp.OutputPath);
                SetIdleStatus("上次图片汉化可点补翻译，或点开始汉化选择接着上次/全新。");
            }
        }
        UpdateButtons();
    }

    public async Task ShutdownAsync()
    {
        if (IsBusy) await CancelAsync();
    }

    private HanhuaKind SelectedKind
        => KindBox.SelectedItem is ComboBoxItem { Tag: "image" } ? HanhuaKind.Image : HanhuaKind.Game;

    private HanhuaEngine SelectedEngine
        => HanhuaEngineCodec.Parse(EngineBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : HanhuaEngineCodec.Local);

    private string? FillModel(LocalChatSettings settings)
    {
        var fromCatalog = _fillModel?.Invoke();
        return HanhuaCommand.ResolveFillModel(settings, fromCatalog);
    }

    private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickFolder is null || IsBusy) return;
        var path = await _pickFolder(SelectedKind == HanhuaKind.Image ? "选择图片目录" : "选择游戏目录");
        if (string.IsNullOrWhiteSpace(path)) return;
        HideDuplicateConfirm();
        SourcePathBox.Text = path;
        UpdateButtons();
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HanhuaEmptyStateHint is null || CatchUpHanhuaButton is null) return;
        HideDuplicateConfirm();
        HanhuaEmptyStateHint.Text = HanhuaProgressStatus.EmptyHint(SelectedKind);
        if (HanhuaComposerHint is not null)
            HanhuaComposerHint.Text = HanhuaProgressStatus.ComposerHint(SelectedKind);
        UpdateButtons();
    }

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEngine || _persistEngine is null) return;
        _persistEngine(SelectedEngine);
    }

    private async void StartOrCancel_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) { await CancelAsync(); return; }
        if (ShouldPromptDuplicateStart())
        {
            ShowDuplicateConfirm();
            return;
        }
        await StartAsync();
    }

    private async void CatchUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        HideDuplicateConfirm();
        await StartAsync(catchUp: true);
    }

    private async void DuplicateContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        HideDuplicateConfirm();
        await StartAsync(catchUp: true);
    }

    private async void DuplicateFreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        HideDuplicateConfirm();
        await StartAsync();
    }

    private void DuplicateCancelButton_Click(object sender, RoutedEventArgs e)
        => HideDuplicateConfirm();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_job?.OutputPath is not { Length: > 0 } output) return;
        try
        {
            var unityBat = Path.Combine(output, "点我启动汉化版.bat");
            var bat = Path.Combine(output, "点我打开中文版.bat");
            var exe = Path.Combine(output, "Game.exe");
            var lastPng = Directory.Exists(output)
                ? Directory.GetFiles(output)
                    .Where(path => HanhuaCatchUp.ImageSuffixes.Contains(Path.GetExtension(path)))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            var reveal = File.Exists(unityBat) ? unityBat
                : File.Exists(bat) ? bat
                : File.Exists(exe) ? exe
                : lastPng ?? output;
            Process.Start(File.Exists(reveal)
                ? WindowsFileReveal.CreateExplorerSelectStartInfo(reveal)
                : WindowsFileReveal.CreateExplorerOpenDirectoryStartInfo(output));
        }
        catch (Exception error)
        {
            SetIdleStatus($"无法打开结果：{error.Message}");
        }
    }

    private void ErrorDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(_detail.ToString());
        Clipboard.SetContent(data);
        SetIdleStatus("详情已复制，完整日志也在上方。");
    }

    private HanhuaJob? CatchUpTarget()
    {
        var source = SourcePathBox.Text.Trim();
        if (HanhuaCatchUp.CanCatchUp(_job)
            && (string.IsNullOrWhiteSpace(source)
                || HanhuaCatchUp.SameSource(_job!.SourcePath, source)))
            return _job;
        if (_store is null || string.IsNullOrWhiteSpace(source))
            return null;
        return HanhuaCatchUp.Latest(
            _store.Load().Jobs.Where(job => HanhuaCatchUp.SameSource(job.SourcePath, source)));
    }

    private bool ShouldPromptDuplicateStart()
        => _store is not null
            && HanhuaCatchUp.ShouldPromptInsteadOfFreshStart(
                SelectedKind,
                SourcePathBox.Text.Trim(),
                _job,
                _store.Load().Jobs);

    private void ShowDuplicateConfirm()
    {
        if (DuplicateConfirmBar is null) return;
        DuplicateConfirmText.Text = HanhuaProgressStatus.DuplicateStartPrompt;
        DuplicateConfirmBar.Visibility = Visibility.Visible;
        SetIdleStatus(HanhuaProgressStatus.DuplicateStartPrompt);
    }

    private void HideDuplicateConfirm()
    {
        if (DuplicateConfirmBar is null || DuplicateConfirmBar.Visibility == Visibility.Collapsed)
            return;
        DuplicateConfirmBar.Visibility = Visibility.Collapsed;
    }

    private async Task StartAsync(bool catchUp = false)
    {
        HideDuplicateConfirm();
        if (_settings is null || _store is null || _prepareGpu is null) return;
        var settings = _settings();
        var kind = SelectedKind;
        var engine = SelectedEngine;
        var source = SourcePathBox.Text.Trim();
        var blocked = HanhuaArbitration.RefuseHanhua(IsBusy, _chatBusy?.Invoke() == true, _videoBusy?.Invoke() == true);
        if (blocked is not null) { SetIdleStatus(blocked); return; }
        HanhuaJob job;
        if (catchUp)
        {
            var target = CatchUpTarget();
            if (target is null)
            {
                SetIdleStatus("没有可补的图片任务。请先完成一次图片汉化。");
                return;
            }
            source = target.SourcePath;
            kind = target.Kind;
            engine = target.Engine;
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                SetIdleStatus("源目录不在了，无法补翻译。");
                return;
            }
            var missingCatchUp = HanhuaCommand.Validate(settings, kind);
            if (missingCatchUp is not null) { SetIdleStatus(missingCatchUp); return; }
            SourcePathBox.Text = source;
            SelectTagged(KindBox, "image");
            _runPhases = HanhuaCatchUp.Phases(source, target.WorkPath ?? "");
            job = HanhuaCatchUp.Begin(target);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                SetIdleStatus("请先选择有效的源目录。");
                return;
            }
            var missing = HanhuaCommand.Validate(settings, kind);
            if (missing is not null) { SetIdleStatus(missing); return; }
            var unity = kind == HanhuaKind.Game && HanhuaCommand.LooksLikeUnity(source);
            if (unity)
                source = HanhuaCommand.ResolveUnityRoot(source) ?? source;
            _runPhases = unity ? HanhuaCommand.UnityPhases : HanhuaCommand.Phases(kind);

            var resume = _job is { CanResume: true }
                && HanhuaCatchUp.SameSource(_job.SourcePath, source)
                && _job.Kind == kind;
            job = resume
                ? _job! with { Status = HanhuaJobStatus.Running, Engine = _job.Engine, Error = null, UpdatedUtc = DateTimeOffset.UtcNow }
                : new HanhuaJob(
                    Guid.NewGuid().ToString("N")[..12],
                    kind,
                    engine,
                    kind == HanhuaKind.Game ? HanhuaPhase.Copy : HanhuaPhase.Ocr,
                    HanhuaJobStatus.Running,
                    source,
                    kind == HanhuaKind.Image ? HanhuaCommand.ImageWorkPath(settings.HanhuaPackRoot, Guid.NewGuid().ToString("N")[..12]) : null,
                    null, 0, 0, "开始汉化", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
        _job = job;
        _store.Upsert(job);
        _runCts = new CancellationTokenSource();
        _detail.Clear();
        DispatcherQueue.TryEnqueue(() => _log.Clear());
        HanhuaEmptyState.Visibility = Visibility.Collapsed;
        ResultPreview.Visibility = Visibility.Collapsed;
        var unityJob = kind == HanhuaKind.Game && HanhuaCommand.LooksLikeUnity(source);
        AppendLog(catchUp
            ? "补翻译：只填未译句子，缺页才抽字，改动页才嵌字。"
            : HanhuaProgressStatus.StartBanner(kind, unityJob));
        StartElapsedTicker();
        ApplyProgressUi();
        UpdateButtons();
        try
        {
            if (kind == HanhuaKind.Game && HanhuaCommand.LooksLikeUnity(source))
                await RunUnityAsync(settings, job, source, _runCts.Token);
            else if (kind == HanhuaKind.Game)
                await RunLaunchAsync(HanhuaCommand.Game(settings, job.Engine, source, FillModel(settings)), job.Engine, HanhuaPhase.Translate, _runCts.Token);
            else if (catchUp)
                await RunImageCatchUpAsync(settings, job, _runCts.Token);
            else
                await RunImageAsync(settings, job, _runCts.Token);
            if (_job.Status == HanhuaJobStatus.Cancelling)
            {
                Finish(HanhuaJobStatus.Interrupted, _job.Phase, "已取消。已完成的部分还在，点开始汉化可从当前步继续。");
                return;
            }
            Finish(HanhuaJobStatus.Succeeded, _job.Phase, catchUp ? "补翻译完成。" : "汉化完成。");
            await ShowPreviewAsync(_job.OutputPath);
        }
        catch (OperationCanceledException)
        {
            Finish(HanhuaJobStatus.Interrupted, _job.Phase, "已取消。已完成的部分还在，点开始汉化可从当前步继续。");
        }
        catch (Exception error)
        {
            Finish(HanhuaJobStatus.Failed, _job.Phase, error.Message);
        }
        finally
        {
            StopElapsedTicker();
            GpuNeedChanged?.Invoke(this, new(HanhuaGpuNeed.None, null));
            _runCts?.Dispose();
            _runCts = null;
            UpdateButtons();
        }
    }

    private async Task RunUnityAsync(LocalChatSettings settings, HanhuaJob job, string source, CancellationToken cancellationToken)
    {
        var root = HanhuaCommand.ResolveUnityRoot(source) ?? source;
        await RunLaunchAsync(
            HanhuaCommand.Unity(settings, job.Engine, root, fill: false, FillModel(settings)),
            job.Engine,
            HanhuaPhase.Copy,
            cancellationToken);
        if (_job is null || _job.Status == HanhuaJobStatus.Cancelling) return;
        _job = _job with { OutputPath = root, UpdatedUtc = DateTimeOffset.UtcNow };
        if (job.Engine == HanhuaEngine.LocalQwen && HanhuaCommand.UnityHasDump(root))
        {
            await RunLaunchAsync(
                HanhuaCommand.Unity(settings, job.Engine, root, fill: true, FillModel(settings)),
                job.Engine,
                HanhuaPhase.Translate,
                cancellationToken);
            if (_job.Status == HanhuaJobStatus.Cancelling) return;
        }
        _job = _job with { OutputPath = root, UpdatedUtc = DateTimeOffset.UtcNow };
    }

    private async Task RunImageAsync(LocalChatSettings settings, HanhuaJob job, CancellationToken cancellationToken)
    {
        var work = job.WorkPath ?? HanhuaCommand.ImageWorkPath(settings.HanhuaPackRoot, job.Id);
        Directory.CreateDirectory(work);
        _job = job with { WorkPath = work, UpdatedUtc = DateTimeOffset.UtcNow };
        var phases = new (HanhuaPhase Phase, HanhuaLaunch Launch)[]
        {
            (HanhuaPhase.Ocr, HanhuaCommand.ImageOcr(settings, job.SourcePath, work)),
            (HanhuaPhase.Fill, HanhuaCommand.ImageFill(settings, job.Engine, work, FillModel(settings))),
            (HanhuaPhase.Typeset, HanhuaCommand.ImageTypeset(settings, work)),
        };
        var startIndex = Math.Max(0, Array.FindIndex(phases, item => item.Phase == job.Phase));
        for (var i = startIndex; i < phases.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunLaunchAsync(phases[i].Launch, job.Engine, phases[i].Phase, cancellationToken);
            if (_job.Status == HanhuaJobStatus.Cancelling) return;
        }
        var output = Path.Combine(work, "out");
        _job = _job with { OutputPath = Directory.Exists(output) ? output : work };
    }

    private async Task RunImageCatchUpAsync(LocalChatSettings settings, HanhuaJob job, CancellationToken cancellationToken)
    {
        var work = job.WorkPath ?? HanhuaCommand.ImageWorkPath(settings.HanhuaPackRoot, job.Id);
        Directory.CreateDirectory(work);
        _job = job with { WorkPath = work, UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var phase in HanhuaCatchUp.Phases(job.SourcePath, work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launch = phase switch
            {
                HanhuaPhase.Ocr => HanhuaCommand.ImageOcrCatchUp(settings, job.SourcePath, work),
                HanhuaPhase.Fill => HanhuaCommand.ImageFill(settings, job.Engine, work, FillModel(settings)),
                HanhuaPhase.Typeset => HanhuaCommand.ImageTypesetCatchUp(settings, work),
                _ => throw new InvalidOperationException(phase.ToString()),
            };
            await RunLaunchAsync(launch, job.Engine, phase, cancellationToken);
            if (_job.Status == HanhuaJobStatus.Cancelling) return;
        }
        var output = Path.Combine(work, "out");
        _job = _job with { OutputPath = Directory.Exists(output) ? output : work };
    }

    private async Task RunLaunchAsync(HanhuaLaunch launch, HanhuaEngine engine, HanhuaPhase phase, CancellationToken cancellationToken)
    {
        if (_job is null || _prepareGpu is null) return;
        var need = HanhuaCommand.GpuNeed(_job.Kind, engine, phase);
        GpuNeedChanged?.Invoke(this, new(need, need == HanhuaGpuNeed.Mit ? "汉化占用 GPU" : null));
        _job = HanhuaProgressStatus.BeginPhase(_job, phase);
        _store?.Upsert(_job);
        DispatcherQueue.TryEnqueue(ApplyProgressUi);
        await _prepareGpu(need, cancellationToken);
        var code = await _host.RunAsync(launch, OnProgress, AppendLog, cancellationToken);
        if (cancellationToken.IsCancellationRequested || _job.Status == HanhuaJobStatus.Cancelling)
            throw new OperationCanceledException();
        if (code != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(_job.Error) ? $"脚本退出码 {code}。" : _job.Error);
    }

    private void OnProgress(HanhuaProgressEvent progress)
    {
        if (_job is null) return;
        if (!progress.IsJson)
        {
            if (!string.IsNullOrWhiteSpace(progress.RawLine)) AppendLog(progress.RawLine);
            return;
        }
        var phase = HanhuaProgress.TryPhase(progress.Phase) ?? _job.Phase;
        var (done, total) = phase == _job.Phase
            ? HanhuaProgressStatus.MergePhaseCounters(_job.Done, _job.Total, progress.Done, progress.Total)
            : (progress.Done, progress.Total);
        _job = _job with
        {
            Phase = phase,
            Done = done,
            Total = total,
            Message = progress.Message ?? _job.Message,
            OutputPath = string.IsNullOrWhiteSpace(progress.Output) ? _job.OutputPath : progress.Output,
            Error = progress.Type == "error" ? progress.Message : _job.Error,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        _store?.Upsert(_job);
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            AppendLog(progress.Type == "error"
                ? HanhuaErrorPresentation.Summarize(progress.Message, phase)
                : progress.Message);
        }
        DispatcherQueue.TryEnqueue(ApplyProgressUi);
    }

    private async Task CancelAsync()
    {
        if (_job is null) return;
        _job = _job with { Status = HanhuaJobStatus.Cancelling, UpdatedUtc = DateTimeOffset.UtcNow };
        _store?.Upsert(_job);
        ApplyProgressUi();
        try { _runCts?.Cancel(); } catch { }
        await Task.Delay(50);
    }

    private void Finish(HanhuaJobStatus status, HanhuaPhase phase, string message)
    {
        if (_job is null) return;
        StopElapsedTicker();
        var display = HanhuaErrorPresentation.DisplayMessage(
            _job.Kind, phase, status, message, _job.Done, _job.Total);
        _job = _job with { Status = status, Phase = phase, Message = display, Error = status == HanhuaJobStatus.Failed ? message : null, UpdatedUtc = DateTimeOffset.UtcNow };
        _store?.Upsert(_job);
        AppendLog(display);
        SetIdleStatus(HanhuaProgressStatus.FormatFinished(display, DisplayElapsed));
        HasInterruptedWork = status is HanhuaJobStatus.Interrupted;
        InterruptedWorkSummary = HasInterruptedWork ? display : null;
        OpenHanhuaOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(_job.OutputPath);
        HanhuaErrorDetailsButton.Visibility = status == HanhuaJobStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string line)
    {
        _detail.AppendLine(line);
        if (IsNoiseLog(line)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _log.Add(line);
            if (_log.Count > 400) _log.RemoveAt(0);
            if (JobLogList.Items.Count > 0)
                JobLogList.ScrollIntoView(_log[^1]);
            HanhuaEmptyState.Visibility = _log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private static bool IsNoiseLog(string line)
    {
        var text = line.Trim();
        return text.StartsWith("Don't continue if --save-text", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("mit_exit=", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Failed to open image:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TypeError: 'str' object does not support item assignment", StringComparison.Ordinal);
    }

    private void StartElapsedTicker()
    {
        _elapsed.Restart();
        _elapsedTicker ??= DispatcherQueue.CreateTimer();
        _elapsedTicker.Interval = TimeSpan.FromSeconds(1);
        _elapsedTicker.IsRepeating = true;
        _elapsedTicker.Tick -= ElapsedTicker_Tick;
        _elapsedTicker.Tick += ElapsedTicker_Tick;
        _elapsedTicker.Start();
    }

    private void StopElapsedTicker()
    {
        _elapsed.Stop();
        if (_elapsedTicker is null) return;
        _elapsedTicker.Stop();
        _elapsedTicker.Tick -= ElapsedTicker_Tick;
    }

    private void ElapsedTicker_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!IsBusy) return;
        ApplyProgressUi();
    }

    private void ApplyProgressUi()
    {
        if (_job is null) return;
        var live = HanhuaProgressStatus.FromJob(_job, DisplayElapsed, _runPhases);
        HanhuaStatusBox.Text = OneLine(live.StatusText);
        HanhuaJobProgress.Visibility = Visibility.Visible;
        if (live.Determinate)
        {
            HanhuaJobProgress.IsIndeterminate = false;
            HanhuaJobProgress.Maximum = 100;
            HanhuaJobProgress.Value = Math.Clamp((live.Fraction ?? 0) * 100, 0, 100);
            HanhuaJobPercentText.Text = live.PercentText;
            HanhuaJobPercentText.Visibility = Visibility.Visible;
        }
        else
        {
            HanhuaJobProgress.IsIndeterminate = true;
            HanhuaJobPercentText.Text = live.PercentText;
            HanhuaJobPercentText.Visibility = string.IsNullOrWhiteSpace(live.PercentText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        UpdateButtons();
    }

    private void SetIdleStatus(string text)
    {
        HanhuaStatusBox.Text = OneLine(text);
        HanhuaJobProgress.Visibility = Visibility.Collapsed;
        HanhuaJobPercentText.Visibility = Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var busy = IsBusy;
        CancelHanhuaButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ComposerStartButton.Content = busy ? "取消" : "开始汉化";
        KindBox.IsEnabled = !busy;
        EngineBox.IsEnabled = !busy;
        PickFolderButton.IsEnabled = !busy;
        if (CatchUpHanhuaButton is not null)
            CatchUpHanhuaButton.Visibility = !busy && CatchUpTarget() is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async Task ShowPreviewAsync(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || !Directory.Exists(output)) return;
        var png = Directory.GetFiles(output)
            .Where(path => HanhuaCatchUp.ImageSuffixes.Contains(Path.GetExtension(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (png is null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(png);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            ResultPreview.Source = image;
            ResultPreview.Visibility = Visibility.Visible;
        }
        catch { /* preview is optional */ }
    }

    private static void SelectTagged(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string OneLine(string text)
        => string.IsNullOrWhiteSpace(text) ? "等待开始" : text.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
