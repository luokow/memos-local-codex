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
    private HanhuaJobStore? _store;
    private Func<LocalChatSettings>? _settings;
    private Func<bool>? _chatBusy;
    private Func<bool>? _videoBusy;
    private Func<HanhuaGpuNeed, CancellationToken, Task>? _prepareGpu;
    private Func<string, Task<string?>>? _pickFolder;
    private Action<HanhuaEngine>? _persistEngine;
    private CancellationTokenSource? _runCts;
    private HanhuaJob? _job;
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

    public event EventHandler? SettingsRequested;
    public event EventHandler<HanhuaGpuNeedChangedEventArgs>? GpuNeedChanged;

    public void Configure(
        Func<LocalChatSettings> settings,
        Func<bool> chatBusy,
        Func<bool> videoBusy,
        Func<HanhuaGpuNeed, CancellationToken, Task> prepareGpu,
        Func<string, Task<string?>> pickFolder,
        Action<HanhuaEngine> persistEngine,
        string localChatRoot)
    {
        _settings = settings;
        _chatBusy = chatBusy;
        _videoBusy = videoBusy;
        _prepareGpu = prepareGpu;
        _pickFolder = pickFolder;
        _persistEngine = persistEngine;
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

    private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickFolder is null || IsBusy) return;
        var path = await _pickFolder(SelectedKind == HanhuaKind.Image ? "选择图片目录" : "选择游戏目录");
        if (!string.IsNullOrWhiteSpace(path)) SourcePathBox.Text = path;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => HanhuaEmptyState.Text = SelectedKind == HanhuaKind.Image
            ? "选择含 png 的目录后开始"
            : "选择游戏或图片目录后开始";

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEngine || _persistEngine is null) return;
        _persistEngine(SelectedEngine);
    }

    private async void StartOrCancel_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) { await CancelAsync(); return; }
        await StartAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_job?.OutputPath is not { Length: > 0 } output) return;
        try
        {
            var bat = Path.Combine(output, "点我打开中文版.bat");
            var exe = Path.Combine(output, "Game.exe");
            var lastPng = Directory.Exists(output)
                ? Directory.GetFiles(output, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            var reveal = File.Exists(bat) ? bat
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

    private async Task StartAsync()
    {
        if (_settings is null || _store is null || _prepareGpu is null) return;
        var settings = _settings();
        var kind = SelectedKind;
        var engine = SelectedEngine;
        var source = SourcePathBox.Text.Trim();
        var blocked = HanhuaArbitration.RefuseHanhua(IsBusy, _chatBusy?.Invoke() == true, _videoBusy?.Invoke() == true);
        if (blocked is not null) { SetIdleStatus(blocked); return; }
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            SetIdleStatus("请先选择有效的源目录。");
            return;
        }
        var missing = HanhuaCommand.Validate(settings, kind);
        if (missing is not null) { SetIdleStatus(missing); return; }

        var resume = _job is { CanResume: true }
            && string.Equals(_job.SourcePath, source, StringComparison.OrdinalIgnoreCase)
            && _job.Kind == kind;
        var job = resume
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
        _job = job;
        _store.Upsert(job);
        _runCts = new CancellationTokenSource();
        _detail.Clear();
        HanhuaEmptyState.Visibility = Visibility.Collapsed;
        ResultPreview.Visibility = Visibility.Collapsed;
        UpdateButtons();
        try
        {
            if (kind == HanhuaKind.Game)
                await RunLaunchAsync(HanhuaCommand.Game(settings, job.Engine, source), job.Engine, HanhuaPhase.Translate, _runCts.Token);
            else
                await RunImageAsync(settings, job, _runCts.Token);
            if (_job.Status == HanhuaJobStatus.Cancelling)
            {
                Finish(HanhuaJobStatus.Interrupted, _job.Phase, "已取消。已完成的句子还在。");
                return;
            }
            Finish(HanhuaJobStatus.Succeeded, _job.Phase, "汉化完成。");
            await ShowPreviewAsync(_job.OutputPath);
        }
        catch (OperationCanceledException)
        {
            Finish(HanhuaJobStatus.Interrupted, _job.Phase, "已取消。已完成的句子还在。");
        }
        catch (Exception error)
        {
            Finish(HanhuaJobStatus.Failed, _job.Phase, error.Message);
        }
        finally
        {
            GpuNeedChanged?.Invoke(this, new(HanhuaGpuNeed.None, null));
            _runCts?.Dispose();
            _runCts = null;
            UpdateButtons();
        }
    }

    private async Task RunImageAsync(LocalChatSettings settings, HanhuaJob job, CancellationToken cancellationToken)
    {
        var work = job.WorkPath ?? HanhuaCommand.ImageWorkPath(settings.HanhuaPackRoot, job.Id);
        Directory.CreateDirectory(work);
        _job = job with { WorkPath = work, UpdatedUtc = DateTimeOffset.UtcNow };
        var phases = new (HanhuaPhase Phase, HanhuaLaunch Launch)[]
        {
            (HanhuaPhase.Ocr, HanhuaCommand.ImageOcr(settings, job.SourcePath, work)),
            (HanhuaPhase.Fill, HanhuaCommand.ImageFill(settings, job.Engine, work)),
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

    private async Task RunLaunchAsync(HanhuaLaunch launch, HanhuaEngine engine, HanhuaPhase phase, CancellationToken cancellationToken)
    {
        if (_job is null || _prepareGpu is null) return;
        var need = HanhuaCommand.GpuNeed(_job.Kind, engine, phase);
        GpuNeedChanged?.Invoke(this, new(need, need == HanhuaGpuNeed.Mit ? "汉化占用 GPU" : null));
        await _prepareGpu(need, cancellationToken);
        _job = _job with { Phase = phase, Status = HanhuaJobStatus.Running, UpdatedUtc = DateTimeOffset.UtcNow };
        _store?.Upsert(_job);
        SetBusyStatus(phase.ToString(), _job.Done, _job.Total);
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
        _job = _job with
        {
            Phase = phase,
            Done = progress.Done,
            Total = progress.Total,
            Message = progress.Message ?? _job.Message,
            OutputPath = string.IsNullOrWhiteSpace(progress.Output) ? _job.OutputPath : progress.Output,
            Error = progress.Type == "error" ? progress.Message : _job.Error,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        _store?.Upsert(_job);
        if (!string.IsNullOrWhiteSpace(progress.Message)) AppendLog(progress.Message);
        DispatcherQueue.TryEnqueue(() => SetBusyStatus(progress.Message ?? phase.ToString(), _job.Done, _job.Total));
    }

    private async Task CancelAsync()
    {
        if (_job is null) return;
        _job = _job with { Status = HanhuaJobStatus.Cancelling, UpdatedUtc = DateTimeOffset.UtcNow };
        _store?.Upsert(_job);
        try { _runCts?.Cancel(); } catch { }
        await Task.Delay(50);
    }

    private void Finish(HanhuaJobStatus status, HanhuaPhase phase, string message)
    {
        if (_job is null) return;
        _job = _job with { Status = status, Phase = phase, Message = message, Error = status == HanhuaJobStatus.Failed ? message : null, UpdatedUtc = DateTimeOffset.UtcNow };
        _store?.Upsert(_job);
        AppendLog(message);
        SetIdleStatus(message);
        HasInterruptedWork = status is HanhuaJobStatus.Interrupted;
        InterruptedWorkSummary = HasInterruptedWork ? message : null;
        OpenHanhuaOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(_job.OutputPath);
        HanhuaErrorDetailsButton.Visibility = status == HanhuaJobStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string line)
    {
        _detail.AppendLine(line);
        DispatcherQueue.TryEnqueue(() =>
        {
            _log.Add(line);
            if (_log.Count > 400) _log.RemoveAt(0);
            if (JobLogList.Items.Count > 0)
                JobLogList.ScrollIntoView(_log[^1]);
            HanhuaEmptyState.Visibility = _log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void SetBusyStatus(string text, int done, int total)
    {
        HanhuaStatusBox.Text = OneLine(text);
        var generating = true;
        HanhuaJobProgress.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        HanhuaJobPercentText.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        if (total > 0)
        {
            HanhuaJobProgress.IsIndeterminate = false;
            HanhuaJobProgress.Value = Math.Clamp(100.0 * done / total, 0, 100);
            HanhuaJobPercentText.Text = $"{Math.Clamp((int)Math.Round(100.0 * done / total), 0, 100)}%";
        }
        else
        {
            HanhuaJobProgress.IsIndeterminate = true;
            HanhuaJobPercentText.Text = "";
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
        StartHanhuaButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelHanhuaButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ComposerStartButton.Content = busy ? "取消" : "开始汉化";
        KindBox.IsEnabled = !busy;
        EngineBox.IsEnabled = !busy;
        PickFolderButton.IsEnabled = !busy;
    }

    private async Task ShowPreviewAsync(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || !Directory.Exists(output)) return;
        var png = Directory.GetFiles(output, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
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
