using System.Diagnostics;
using QwenLocalChat.Core;

namespace QwenLocalChat;

internal sealed class MainForm : Form
{
    private static readonly Color Surface = ThemePalette.Surface;
    private static readonly Color Ink = ThemePalette.Ink;
    private static readonly Color Muted = ThemePalette.Muted;
    private static readonly Color Good = ThemePalette.Ink;
    private static readonly Color Warning = Color.FromArgb(201, 201, 197);
    private static readonly Color Error = Color.White;

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly QwenServiceManager _modelManager;
    private readonly QwenChatClient _chatClient;
    private readonly MemoryWriteQueue _memoryQueue = new();
    private readonly InputHistoryNavigator _inputHistory = new();
    private readonly SemaphoreSlim _memosGate = new(1, 1);
    private readonly List<ChatMessage> _history = [];
    private readonly ObsidianTranscriptBox _transcript = new();
    private readonly TextBox _input = new();
    private readonly TechButton _sendButton = new() { Primary = true };
    private readonly ToggleSwitch _useMemos = new();
    private readonly ToggleSwitch _saveLogs = new();
    private readonly StatusPill _qwenStatus = new();
    private readonly StatusPill _memosStatus = new();
    private readonly StatusPill _logStatus = new();
    private readonly Label _notice = new();
    private MemosStdioClient? _memos;
    private MarkdownChatLog _chatLog;
    private string _sessionId = NewSessionId();
    private bool _loadingSettings;
    private bool _busy;
    private bool _closing;
    private bool _allowClose;
    private bool _wasMinimized;
    private bool _restorePaintQueued;
    private bool _restoreRedrawHeld;

    public MainForm(AppPaths paths) : this(paths, startModel: true)
    {
    }

    internal MainForm(AppPaths paths, bool startModel)
    {
        _paths = paths;
        _settingsStore = new SettingsStore(paths.SettingsFile);
        var modelOptions = new LocalModelOptions(
            new Uri("http://127.0.0.1:18135/health"),
            new Uri("http://127.0.0.1:18135/v1/chat/completions"),
            paths.ServerExecutable,
            paths.ModelFile,
            paths.ModelLogFile,
            18135);
        _modelManager = new QwenServiceManager(modelOptions, new WindowsModelProcessLauncher());
        _chatClient = new QwenChatClient(modelOptions.ChatCompletionsUri);
        _chatLog = NewLog();

        BuildInterface();
        LoadSettings();
        _memoryQueue.StateChanged += (_, _) => SafeUi(UpdateMemosStatus);
        if (startModel) Shown += OnShown;
        FormClosing += OnFormClosing;
    }

    internal bool RestoreRedrawHeldForTesting => _restoreRedrawHeld;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.ApplyDarkMode(Handle);
    }

    protected override void OnResize(EventArgs e)
    {
        var wasMinimized = _wasMinimized;
        var nowMinimized = WindowState == FormWindowState.Minimized;
        var restoredFromMinimized = wasMinimized && !nowMinimized;
        if (restoredFromMinimized && !_restoreRedrawHeld && IsHandleCreated)
        {
            _restoreRedrawHeld = true;
            WindowChrome.SetRedrawTree(this, false);
        }
        _wasMinimized = nowMinimized;
        base.OnResize(e);
        if (_wasMinimized)
        {
            return;
        }

        if (restoredFromMinimized)
        {
            QueueRestorePaint();
            return;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        QueueRestorePaint();
    }

    private void BuildInterface()
    {
        Text = "Qwen Local";
        MinimumSize = new Size(760, 560);
        ClientSize = new Size(960, 740);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemePalette.Canvas;
        ForeColor = Ink;
        Font = ThemePalette.Ui(9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        DoubleBuffered = true;

        var associatedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (associatedIcon is not null) Icon = associatedIcon;

        var backdrop = new ObsidianBackdropPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18) };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(4, 0, 4, 0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = 2,
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        if (Icon is not null)
        {
            var brandIcon = new PictureBox
            {
                Image = Icon.ToBitmap(),
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 6, 12, 5),
                AccessibleName = "本地聊天应用图标",
            };
            brand.Controls.Add(brandIcon, 0, 0);
            brand.SetRowSpan(brandIcon, 2);
        }
        brand.Controls.Add(new Label
        {
            Text = "Qwen Local",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Ink,
            Font = ThemePalette.Ui(12F, FontStyle.Bold),
            Margin = Padding.Empty,
        }, 1, 0);
        brand.Controls.Add(new Label
        {
            Text = "LOCAL CORE  /  127.0.0.1:18135",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = ThemePalette.Secondary,
            Font = ThemePalette.Mono(7.25F, FontStyle.Bold),
            Margin = Padding.Empty,
        }, 1, 1);

        var statuses = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0),
        };
        ConfigureStatus(_qwenStatus, "— 9B 启动中", Warning);
        ConfigureStatus(_memosStatus, "○ 记忆关闭", Muted);
        ConfigureStatus(_logStatus, "○ 日志关闭", Muted);
        statuses.Controls.AddRange([_logStatus, _memosStatus, _qwenStatus]);
        _notice.Dock = DockStyle.Fill;
        _notice.ForeColor = ThemePalette.Secondary;
        _notice.AutoEllipsis = true;
        _notice.Text = "所有对话均在本机处理。";
        _notice.Font = ThemePalette.Ui(8.5F);
        _notice.Margin = new Padding(0, 7, 0, 0);
        header.Controls.Add(brand, 0, 0);
        header.Controls.Add(statuses, 1, 0);
        header.Controls.Add(_notice, 0, 1);
        header.SetColumnSpan(_notice, 2);

        _transcript.Dock = DockStyle.Fill;
        _transcript.ReadOnly = true;
        _transcript.BackColor = Surface;
        _transcript.ForeColor = Ink;
        _transcript.BorderStyle = BorderStyle.None;
        _transcript.Font = ThemePalette.Ui(10.25F);
        _transcript.DetectUrls = false;
        _transcript.ScrollBars = RichTextBoxScrollBars.None;
        _transcript.Margin = Padding.Empty;
        _transcript.AccessibleName = "聊天记录";
        var transcriptShell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Surface,
            BorderColor = ThemePalette.Border,
            CornerRadius = 18,
            Padding = new Padding(20, 18, 20, 16),
            Margin = new Padding(0, 5, 0, 10),
        };
        transcriptShell.Controls.Add(_transcript);
        AppendSystem("正在检查本地 Qwen3.5-9B 服务；长期记忆和磁盘日志默认关闭。");

        _input.Dock = DockStyle.Fill;
        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.ScrollBars = ScrollBars.None;
        _input.BorderStyle = BorderStyle.None;
        _input.BackColor = ThemePalette.Raised;
        _input.ForeColor = Ink;
        _input.Font = ThemePalette.Ui(10.5F);
        _input.PlaceholderText = "输入消息";
        _input.Margin = new Padding(4, 5, 15, 0);
        _input.AccessibleName = "消息输入框";
        _input.KeyDown += InputKeyDown;

        var composerShell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = ThemePalette.Raised,
            BorderColor = ThemePalette.StrongBorder,
            CornerRadius = 18,
            Padding = new Padding(16, 13, 13, 10),
            Margin = new Padding(0, 8, 0, 8),
        };
        var composer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 2, RowCount = 2 };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        composer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));

        _sendButton.Text = "发送  ↗";
        _sendButton.MinimumSize = new Size(104, 44);
        _sendButton.Dock = DockStyle.Fill;
        _sendButton.Margin = new Padding(10, 1, 0, 5);
        _sendButton.Click += async (_, _) => await SendAsync();
        _sendButton.AccessibleName = "发送消息";
        var shortcut = new Label
        {
            Text = "ENTER 发送   ·   SHIFT+ENTER 换行   ·   ↑↓ 历史",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ThemePalette.Muted,
            Font = ThemePalette.Mono(7.25F),
            Margin = new Padding(4, 4, 0, 0),
        };
        composer.Controls.Add(_input, 0, 0);
        composer.Controls.Add(_sendButton, 1, 0);
        composer.Controls.Add(shortcut, 0, 1);
        composer.SetColumnSpan(shortcut, 2);
        composerShell.Controls.Add(composer);

        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        var settingsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0),
        };
        var actionsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 5, 0, 0),
        };
        _useMemos.Text = "MemOS 长期记忆";
        ConfigureToggle(_useMemos);
        _useMemos.CheckedChanged += SettingsChanged;
        _saveLogs.Text = "保存聊天日志";
        ConfigureToggle(_saveLogs);
        _saveLogs.CheckedChanged += SettingsChanged;
        new ToolTip().SetToolTip(_saveLogs, "开启后，完整可见问答将保存到 D 盘 Markdown 文件。隐藏记忆不会写入日志。");
        var clear = MakeButton("清空会话", async () => await ClearConversationAsync());
        var openLogs = MakeButton("打开日志目录", OpenLogsDirectory);
        settingsFlow.Controls.AddRange([_useMemos, _saveLogs]);
        actionsFlow.Controls.AddRange([openLogs, clear]);
        controls.Controls.Add(settingsFlow, 0, 0);
        controls.Controls.Add(actionsFlow, 1, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(transcriptShell, 0, 1);
        root.Controls.Add(composerShell, 0, 2);
        root.Controls.Add(controls, 0, 3);
        backdrop.Controls.Add(root);
        Controls.Add(backdrop);
        AcceptButton = _sendButton;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        await InitializeModelAsync();
    }

    private void QueueRestorePaint()
    {
        if (_restorePaintQueued || !IsHandleCreated || IsDisposed || WindowState == FormWindowState.Minimized)
        {
            if (IsDisposed || WindowState == FormWindowState.Minimized) ReleaseRestoreRedraw();
            return;
        }
        _restorePaintQueued = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!IsDisposed && WindowState != FormWindowState.Minimized) PerformLayout();
                }
                finally
                {
                    _restorePaintQueued = false;
                    ReleaseRestoreRedraw();
                }

                if (IsDisposed || WindowState == FormWindowState.Minimized) return;
                WindowChrome.RedrawTree(Handle);
            }));
        }
        catch
        {
            _restorePaintQueued = false;
            ReleaseRestoreRedraw();
            throw;
        }
    }

    private void ReleaseRestoreRedraw()
    {
        if (!_restoreRedrawHeld) return;
        _restoreRedrawHeld = false;
        if (IsHandleCreated && !IsDisposed) WindowChrome.SetRedrawTree(this, true);
    }

    private static void ConfigureStatus(StatusPill label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
        label.AutoSize = true;
        label.Margin = new Padding(8, 0, 0, 0);
        label.Font = ThemePalette.Mono(7.75F, FontStyle.Bold);
    }

    private static void ConfigureToggle(ToggleSwitch toggle)
    {
        toggle.ForeColor = ThemePalette.Secondary;
        toggle.Font = ThemePalette.Ui(8.75F);
        toggle.Margin = new Padding(0, 0, 14, 0);
        toggle.Cursor = Cursors.Hand;
    }

    private static TechButton MakeButton(string text, Action action)
    {
        var button = new TechButton { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        return button;
    }

    private static TechButton MakeButton(string text, Func<Task> action)
    {
        var button = new TechButton { Text = text, AutoSize = true };
        button.Click += async (_, _) => await action();
        return button;
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        var loaded = _settingsStore.Load();
        _useMemos.Checked = loaded.Settings.UseMemos;
        _saveLogs.Checked = loaded.Settings.SaveChatLogs;
        _chatLog.Enabled = loaded.Settings.SaveChatLogs;
        _loadingSettings = false;
        UpdateMemosStatus();
        UpdateLogStatus();
        if (loaded.Warning is not null) SetNotice(loaded.Warning, Warning);
    }

    private async void SettingsChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings) return;
        try
        {
            _settingsStore.Save(new LocalChatSettings(_useMemos.Checked, _saveLogs.Checked));
            _chatLog.Enabled = _saveLogs.Checked;
            UpdateMemosStatus();
            UpdateLogStatus();
            if (!_useMemos.Checked)
            {
                await _memoryQueue.DrainAsync();
                if (!_useMemos.Checked) await DisposeMemosAsync();
            }
        }
        catch (Exception error)
        {
            SetNotice($"设置保存失败：{error.Message}", Error);
        }
    }

    private async Task InitializeModelAsync()
    {
        try
        {
            var availability = await _modelManager.EnsureAvailableAsync();
            _qwenStatus.Text = availability.Reused ? "● 9B 已复用" : "● 9B 已启动";
            _qwenStatus.ForeColor = Good;
            SetNotice("模型只监听 127.0.0.1:18135。", Muted);
            AppendSystem(availability.Reused ? "已复用当前 Qwen3.5-9B 服务，可以开始对话。" : "Qwen3.5-9B 已由本窗口启动，可以开始对话。");
            _input.Focus();
        }
        catch (Exception error)
        {
            _qwenStatus.Text = "× 9B 不可用";
            _qwenStatus.ForeColor = Error;
            SetNotice($"模型启动失败：{error.Message}", Error);
        }
    }

    private async void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Modifiers == Keys.None && e.KeyCode is Keys.Up or Keys.Down && !_input.Text.Contains('\n'))
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            var recalled = e.KeyCode == Keys.Up
                ? _inputHistory.Previous(_input.Text)
                : _inputHistory.Next(_input.Text);
            _input.Text = recalled;
            _input.SelectionStart = _input.TextLength;
            return;
        }
        if (e.KeyCode != Keys.Enter || e.Shift) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        if (_busy) return;
        var userMessage = _input.Text.Trim();
        if (userMessage.Length == 0) return;
        var useMemosThisRound = _useMemos.Checked;
        var saveLogThisRound = _saveLogs.Checked;
        SetBusy(true);
        try
        {
            _qwenStatus.Text = "— 9B 检查中";
            _qwenStatus.ForeColor = Warning;
            _ = await _modelManager.EnsureAvailableAsync();
            _qwenStatus.Text = _modelManager.OwnsModel ? "● 9B 已启动" : "● 9B 已复用";
            _qwenStatus.ForeColor = Good;

            string? memoryContext = null;
            MemosStdioClient? memoryClient = null;
            if (useMemosThisRound)
            {
                try
                {
                    _memosStatus.Text = "— 记忆召回中";
                    _memosStatus.ForeColor = Warning;
                    memoryClient = await EnsureMemosAsync();
                    memoryContext = await memoryClient.RecallAsync(userMessage, 5);
                    _memosStatus.Text = "● 记忆已开启";
                    _memosStatus.ForeColor = Good;
                }
                catch (Exception error)
                {
                    SetNotice($"MemOS 召回失败，本轮按无记忆模式继续：{error.Message}", Warning);
                    _memosStatus.Text = "△ 记忆降级";
                    _memosStatus.ForeColor = Warning;
                }
            }

            var requestMessages = ConversationContext.Build(_history, userMessage, memoryContext);
            var assistantMessage = await _chatClient.CompleteAsync(requestMessages);
            _history.Add(new ChatMessage("user", userMessage));
            _history.Add(new ChatMessage("assistant", assistantMessage));
            _inputHistory.Record(userMessage);
            AppendTurn("你", userMessage, ThemePalette.Ink);
            AppendTurn("Qwen", assistantMessage, Ink);
            _input.Clear();

            _chatLog.Enabled = saveLogThisRound;
            try
            {
                _chatLog.RecordSuccessfulRound(userMessage, assistantMessage);
                UpdateLogStatus();
            }
            catch (Exception error)
            {
                SetNotice($"日志写入失败：{error.Message}", Warning);
                _logStatus.Text = "× 日志失败";
                _logStatus.ForeColor = Warning;
            }

            if (useMemosThisRound && memoryClient is not null)
            {
                var capturedClient = memoryClient;
                var capturedSession = _sessionId;
                _memoryQueue.Enqueue(async cancellationToken =>
                {
                    await capturedClient.RememberAsync(userMessage, assistantMessage, capturedSession, cancellationToken);
                });
            }
        }
        catch (Exception error)
        {
            SetNotice($"发送失败，输入内容已保留：{error.Message}", Error);
        }
        finally
        {
            SetBusy(false);
            UpdateMemosStatus();
        }
    }

    private async Task<MemosStdioClient> EnsureMemosAsync()
    {
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

    private Task ClearConversationAsync()
    {
        _history.Clear();
        _inputHistory.Clear();
        _input.Clear();
        _sessionId = NewSessionId();
        _chatLog = NewLog();
        _transcript.Clear();
        AppendSystem("已清空当前窗口上下文并开始新会话。MemOS 和记忆日志文件未被删除。");
        SetNotice("新会话已开始。", Muted);
        return Task.CompletedTask;
    }

    private MarkdownChatLog NewLog()
        => new(_paths.LogsRoot, _sessionId["qwen-local-chat-".Length..]) { Enabled = _saveLogs.Checked };

    private void OpenLogsDirectory()
    {
        Directory.CreateDirectory(_paths.LogsRoot);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { _paths.LogsRoot } });
    }

    private void AppendSystem(string text)
    {
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.SelectionColor = Muted;
        using (var labelFont = ThemePalette.Mono(7.25F, FontStyle.Bold))
        {
            _transcript.SelectionFont = labelFont;
            _transcript.AppendText("SYSTEM\n");
        }
        _transcript.SelectionColor = ThemePalette.Secondary;
        _transcript.SelectionFont = _transcript.Font;
        _transcript.AppendText($"{text}\n\n");
    }

    private void AppendTurn(string speaker, string text, Color speakerColor)
    {
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.SelectionColor = speakerColor;
        using (var labelFont = ThemePalette.Mono(7.5F, FontStyle.Bold))
        {
            _transcript.SelectionFont = labelFont;
            _transcript.AppendText($"{speaker}\n");
        }
        _transcript.SelectionColor = Ink;
        _transcript.SelectionFont = _transcript.Font;
        _transcript.AppendText($"{text}\n\n");
        _transcript.ScrollToCaret();
    }

    private void UpdateMemosStatus()
    {
        if (!_useMemos.Checked)
        {
            _memosStatus.Text = "○ 记忆关闭";
            _memosStatus.ForeColor = Muted;
        }
        else if (_memoryQueue.PendingCount > 0)
        {
            _memosStatus.Text = $"— 记忆保存中 ({_memoryQueue.PendingCount})";
            _memosStatus.ForeColor = Warning;
        }
        else if (_memoryQueue.LastError is not null)
        {
            _memosStatus.Text = "× 记忆保存失败";
            _memosStatus.ForeColor = Warning;
            SetNotice($"MemOS 写入失败：{_memoryQueue.LastError.Message}", Warning);
        }
        else
        {
            _memosStatus.Text = "● 记忆已开启";
            _memosStatus.ForeColor = Good;
        }
    }

    private void UpdateLogStatus()
    {
        _logStatus.Text = _saveLogs.Checked ? "● 日志已开启" : "○ 日志关闭";
        _logStatus.ForeColor = _saveLogs.Checked ? Good : Muted;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _sendButton.Enabled = !busy;
        _input.Enabled = !busy;
        _sendButton.Text = busy ? "生成中…" : "发送  ↗";
        if (!busy) _input.Focus();
    }

    private void SetNotice(string text, Color color)
    {
        _notice.Text = text;
        _notice.ForeColor = color;
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            if (_memoryQueue.PendingCount > 0)
            {
                var choice = ChoiceDialog.ShowDialog(this, "仍在保存记忆", "MemOS 还有未完成的后台写入。请选择退出方式。", "等待完成", "放弃写入并退出");
                if (choice == ThreeWayChoice.Cancel) return;
                if (choice == ThreeWayChoice.First) await _memoryQueue.DrainAsync();
                else
                {
                    _memoryQueue.CancelPending();
                    await DisposeMemosAsync();
                    await _memoryQueue.DrainAsync();
                }
            }

            if (_modelManager.OwnsModel)
            {
                var choice = ChoiceDialog.ShowDialog(this, "模型由本窗口启动", "关闭聊天窗口后，是否停止本窗口启动的 Qwen 模型？", "停止模型并退出", "保持模型后台运行");
                if (choice == ThreeWayChoice.Cancel) return;
                if (choice == ThreeWayChoice.First) await _modelManager.StopOwnedAsync();
            }

            await DisposeMemosAsync();
            _chatClient.Dispose();
            await _modelManager.DisposeAsync();
            _memoryQueue.Dispose();
            _memosGate.Dispose();
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "退出时发生错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _closing = false;
        }
    }

    private static string NewSessionId() => $"qwen-local-chat-{Guid.NewGuid():N}";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmSize = 0x0005;
        const int SizeMinimized = 1;
        var restoringFromMinimized = message.Msg == WmSize
            && _wasMinimized
            && message.WParam.ToInt32() != SizeMinimized;
        if (restoringFromMinimized && !_restoreRedrawHeld && IsHandleCreated)
        {
            _restoreRedrawHeld = true;
            WindowChrome.SetRedrawTree(this, false);
        }

        base.WndProc(ref message);

        if (restoringFromMinimized) QueueRestorePaint();
    }
}
