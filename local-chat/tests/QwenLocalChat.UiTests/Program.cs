using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QwenLocalChat;
using QwenLocalChat.Core;

namespace QwenLocalChat.UiTests;

internal static class Program
{
    private static readonly (int Dpi, float Scale)[] DpiCases = [(96, 1f), (120, 1.25f), (144, 1.5f)];

    [STAThread]
    private static int Main(string[] args)
    {
        var approve = args.Contains("--approve", StringComparer.OrdinalIgnoreCase);
        var root = FindProjectRoot();
        var candidateDirectory = Path.Combine(root, "diagnostics", "ui-regression");
        var baselineDirectory = Path.Combine(root, "tests", "QwenLocalChat.UiTests", "Baselines");
        Directory.CreateDirectory(candidateDirectory);
        if (approve) Directory.CreateDirectory(baselineDirectory);

        var failed = 0;
        try
        {
            ChoiceDialogUsesCustomControls();
            Console.WriteLine("PASS choice-dialog-uses-custom-controls");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL choice-dialog-uses-custom-controls: {error.Message}");
        }

        try
        {
            KeyboardActivationWorks();
            Console.WriteLine("PASS keyboard-activation");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL keyboard-activation: {error.Message}");
        }

        try
        {
            SelfTestAcceptanceRejectsExistingDuplicates();
            Console.WriteLine("PASS self-test-rejects-existing-duplicates");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL self-test-rejects-existing-duplicates: {error.Message}");
        }

        try
        {
            RecallAcceptanceWaitsForVisibleMarker();
            Console.WriteLine("PASS recall-acceptance-polls-condition");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL recall-acceptance-polls-condition: {error.Message}");
        }

        try
        {
            SemanticMemoryProbeIsDeterministicAndDistinct();
            Console.WriteLine("PASS semantic-memory-probe");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL semantic-memory-probe: {error.Message}");
        }

        try
        {
            RecentTraceAcceptanceRequiresSessionAndMarker();
            Console.WriteLine("PASS recent-trace-acceptance");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL recent-trace-acceptance: {error.Message}");
        }

        try
        {
            TranscriptWheelUsesNativeScrollSemantics();
            Console.WriteLine("PASS transcript-wheel-native-scroll");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL transcript-wheel-stable-paint: {error.Message}");
        }

        try
        {
            TranscriptWheelMovesOneNativeDetent();
            Console.WriteLine("PASS transcript-wheel-single-detent");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL transcript-wheel-single-detent: {error.Message}");
        }

        try
        {
            RestoreRedrawGateBridgesRestoreFrame();
            Console.WriteLine("PASS restore-redraw-gate");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL restore-redraw-gate: {error.Message}");
        }

        try
        {
            NativeTranscriptKeepsPaintOwnership();
            Console.WriteLine("PASS transcript-native-paint-ownership");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL transcript-native-paint-ownership: {error.Message}");
        }

        try
        {
            TranscriptDecorationOverlayIsAnchoredSeparately();
            Console.WriteLine("PASS transcript-decoration-independent-layer");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL transcript-decoration-independent-layer: {error.Message}");
        }

        try
        {
            TranscriptDecorationBindingIsIdempotent();
            Console.WriteLine("PASS transcript-decoration-idempotent-binding");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL transcript-decoration-idempotent-binding: {error.Message}");
        }

        try
        {
            TranscriptDecorationSuspensionSurvivesOwnerResize();
            Console.WriteLine("PASS transcript-decoration-restore-suspension");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL transcript-decoration-restore-suspension: {error.Message}");
        }

        try
        {
            BackgroundGeometryAvoidsRadialRingCompositing();
            Console.WriteLine("PASS background-geometry-single-layer");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL background-geometry-single-layer: {error.Message}");
        }

        foreach (var (dpi, scale) in DpiCases)
        {
            using var candidate = RenderComponentMatrix(dpi, scale);
            ValidatePixelContract(candidate, dpi, scale);
            var candidatePath = Path.Combine(candidateDirectory, $"components-{dpi}.png");
            candidate.Save(candidatePath, ImageFormat.Png);

            var baselinePath = Path.Combine(baselineDirectory, $"components-{dpi}.png");
            if (approve)
            {
                candidate.Save(baselinePath, ImageFormat.Png);
                Console.WriteLine($"APPROVED ui-snapshot-{dpi}: {baselinePath}");
                continue;
            }

            if (!File.Exists(baselinePath))
            {
                failed++;
                Console.Error.WriteLine($"FAIL ui-snapshot-{dpi}: baseline is missing; inspect {candidatePath} before approval.");
                continue;
            }

            using var baseline = new Bitmap(baselinePath);
            var mismatch = CountMismatchedPixels(baseline, candidate);
            if (mismatch == 0) Console.WriteLine($"PASS ui-snapshot-{dpi}");
            else
            {
                failed++;
                Console.Error.WriteLine($"FAIL ui-snapshot-{dpi}: {mismatch} pixels differ; candidate={candidatePath}");
            }
        }

        foreach (var (dpi, scale) in DpiCases)
        {
            using var candidate = RenderTranscriptBackground(dpi, scale);
            var candidatePath = Path.Combine(candidateDirectory, $"transcript-background-{dpi}.png");
            candidate.Save(candidatePath, ImageFormat.Png);
            ValidateTranscriptBackground(candidate, dpi, scale);

            var baselinePath = Path.Combine(baselineDirectory, $"transcript-background-{dpi}.png");
            if (approve)
            {
                candidate.Save(baselinePath, ImageFormat.Png);
                Console.WriteLine($"APPROVED transcript-background-{dpi}: {baselinePath}");
                continue;
            }

            if (!File.Exists(baselinePath))
            {
                failed++;
                Console.Error.WriteLine($"FAIL transcript-background-{dpi}: baseline is missing; inspect {candidatePath} before approval.");
                continue;
            }

            using var baseline = new Bitmap(baselinePath);
            var mismatch = CountMismatchedPixels(baseline, candidate);
            if (mismatch == 0) Console.WriteLine($"PASS transcript-background-{dpi}");
            else
            {
                failed++;
                Console.Error.WriteLine($"FAIL transcript-background-{dpi}: {mismatch} pixels differ; candidate={candidatePath}");
            }
        }

        foreach (var (dpi, scale) in DpiCases)
        {
            using var candidate = RenderAppBackdrop(dpi, scale);
            ValidateAppBackdrop(candidate, dpi, scale);
            var candidatePath = Path.Combine(candidateDirectory, $"app-backdrop-{dpi}.png");
            candidate.Save(candidatePath, ImageFormat.Png);

            var baselinePath = Path.Combine(baselineDirectory, $"app-backdrop-{dpi}.png");
            if (approve)
            {
                candidate.Save(baselinePath, ImageFormat.Png);
                Console.WriteLine($"APPROVED app-backdrop-{dpi}: {baselinePath}");
                continue;
            }

            if (!File.Exists(baselinePath))
            {
                failed++;
                Console.Error.WriteLine($"FAIL app-backdrop-{dpi}: baseline is missing; inspect {candidatePath} before approval.");
                continue;
            }

            using var baseline = new Bitmap(baselinePath);
            var mismatch = CountMismatchedPixels(baseline, candidate);
            if (mismatch == 0) Console.WriteLine($"PASS app-backdrop-{dpi}");
            else
            {
                failed++;
                Console.Error.WriteLine($"FAIL app-backdrop-{dpi}: {mismatch} pixels differ; candidate={candidatePath}");
            }
        }

        using (var current = RenderRealControls())
        {
            ValidateRealControlCorners(current);
            var currentPath = Path.Combine(candidateDirectory, "real-controls-current-dpi.png");
            current.Save(currentPath, ImageFormat.Png);
            Console.WriteLine($"PASS real-control-corners: {currentPath}");
        }

        return failed == 0 ? 0 : 1;
    }

    private static Bitmap RenderComponentMatrix(int dpi, float scale)
    {
        var bitmap = new Bitmap(Px(640, scale), Px(252, scale), PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(ThemePalette.Canvas);
        UiDrawing.Prepare(graphics);

        var panel = Rect(10, 10, 620, 232, scale);
        UiDrawing.DrawRoundedSurface(graphics, panel, 18f * scale, ThemePalette.Surface, ThemePalette.Border, scale);

        using var uiFont = ThemePalette.Ui(9f);
        using var monoFont = ThemePalette.Mono(8f, FontStyle.Bold);
        UiDrawing.DrawStatusPill(graphics, RectI(28, 28, 150, 38, scale), "● 9B 已复用", monoFont, ThemePalette.Ink, scale);
        UiDrawing.DrawTechButton(graphics, RectI(28, 88, 104, 48, scale), "普通", uiFont, false, true, false, false, false, false, scale);
        UiDrawing.DrawTechButton(graphics, RectI(144, 88, 104, 48, scale), "悬停", uiFont, false, true, true, false, false, false, scale);
        UiDrawing.DrawTechButton(graphics, RectI(260, 88, 104, 48, scale), "按下", uiFont, false, true, false, true, false, false, scale);
        UiDrawing.DrawTechButton(graphics, RectI(376, 88, 104, 48, scale), "不可用", uiFont, false, false, false, false, false, false, scale);
        UiDrawing.DrawTechButton(graphics, RectI(492, 88, 112, 48, scale), "发送  ↗", uiFont, true, true, false, false, true, true, scale);
        UiDrawing.DrawToggle(graphics, RectI(28, 166, 235, 42, scale), "MemOS 长期记忆", uiFont, ThemePalette.Secondary, false, true, false, false, scale);
        UiDrawing.DrawToggle(graphics, RectI(286, 166, 235, 42, scale), "保存聊天日志", uiFont, ThemePalette.Secondary, true, true, false, false, scale);

        return bitmap;
    }

    private static Bitmap RenderRealControls()
    {
        using var host = new Panel { Size = new Size(640, 170), BackColor = ThemePalette.Canvas };
        var secondary = new TechButton { Text = "清空会话", Location = new Point(18, 20), Size = new Size(136, 48) };
        var primary = new TechButton { Text = "发送  ↗", Primary = true, Location = new Point(170, 20), Size = new Size(126, 48) };
        var off = new ToggleSwitch { Text = "MemOS 长期记忆", Location = new Point(18, 90), Checked = false };
        var on = new ToggleSwitch { Text = "保存聊天日志", Location = new Point(280, 90), Checked = true };
        host.Controls.AddRange([secondary, primary, off, on]);
        host.CreateControl();
        foreach (Control control in host.Controls) control.CreateControl();

        var bitmap = new Bitmap(host.Width, host.Height, PixelFormat.Format32bppArgb);
        host.DrawToBitmap(bitmap, host.ClientRectangle);
        return bitmap;
    }

    private static Bitmap RenderTranscriptBackground(int dpi, float scale)
    {
        var bitmap = new Bitmap(Px(640, scale), Px(320, scale), PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(ThemePalette.Surface);
        UiDrawing.DrawTranscriptDecoration(graphics, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    private static Bitmap RenderAppBackdrop(int dpi, float scale)
    {
        var bitmap = new Bitmap(Px(960, scale), Px(740, scale), PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        UiDrawing.DrawAppBackdrop(graphics, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    private static void ValidatePixelContract(Bitmap bitmap, int dpi, float scale)
    {
        Equal(ThemePalette.Canvas.ToArgb(), bitmap.GetPixel(0, 0).ToArgb(), $"{dpi} DPI canvas corner must remain untouched");
        Equal(ThemePalette.Canvas.ToArgb(), bitmap.GetPixel(bitmap.Width - 1, 0).ToArgb(), $"{dpi} DPI opposite canvas corner must remain untouched");

        var secondaryCorner = bitmap.GetPixel(Px(28, scale), Px(88, scale));
        NotEqual(Color.White.ToArgb(), secondaryCorner.ToArgb(), $"{dpi} DPI secondary button corner must not expose a white native rectangle");
        NotEqual(SystemColors.Control.ToArgb(), secondaryCorner.ToArgb(), $"{dpi} DPI secondary button corner must not expose the Windows control color");

        var primaryFillProbe = bitmap.GetPixel(Px(548, scale), Px(127, scale));
        Equal(ThemePalette.PrimaryButton.ToArgb(), primaryFillProbe.ToArgb(), $"{dpi} DPI primary button fill must use the approved off-white token");

        var logicalRadius = 18f * scale;
        using var path = UiDrawing.CreateRoundedPath(Rect(10, 10, 620, 232, scale), logicalRadius);
        Equal(false, path.IsVisible(Px(10, scale), Px(10, scale)), $"{dpi} DPI exact rounded corner must remain outside the surface");
        Equal(true, path.IsVisible(Px(28, scale), Px(10, scale) + 1), $"{dpi} DPI top edge must begin after the scaled radius");
    }

    private static void ValidateRealControlCorners(Bitmap bitmap)
    {
        var secondaryCorner = bitmap.GetPixel(18, 20);
        NotEqual(Color.White.ToArgb(), secondaryCorner.ToArgb(), "real secondary button corner must not expose a white native rectangle");
        NotEqual(SystemColors.Control.ToArgb(), secondaryCorner.ToArgb(), "real secondary button corner must not expose Windows control background");
        Equal(ThemePalette.Canvas.ToArgb(), secondaryCorner.ToArgb(), "transparent custom-control corner must compose with its parent canvas");
    }

    private static void ValidateTranscriptBackground(Bitmap bitmap, int dpi, float scale)
    {
        Equal(ThemePalette.Surface.ToArgb(), bitmap.GetPixel(Px(24, scale), Px(160, scale)).ToArgb(), $"{dpi} DPI transcript left side must retain the approved solid reading surface");
        NotEqual(ThemePalette.Surface.ToArgb(), bitmap.GetPixel(Px(563, scale), Px(38, scale)).ToArgb(), $"{dpi} DPI transcript signature node must remain visible");
        NotEqual(ThemePalette.Surface.ToArgb(), bitmap.GetPixel(Px(550, scale), Px(52, scale)).ToArgb(), $"{dpi} DPI transcript upper-right glow must remain visible");

        var scanY = Px(190, scale);
        var maxFlatRun = MaximumIdenticalColorRun(bitmap, scanY, Px(360, scale), Px(502, scale));
        if (maxFlatRun > Px(24, scale))
            throw new InvalidOperationException($"{dpi} DPI transcript decoration contains a {maxFlatRun}px flat color band; the approved background must remain softly graded.");
    }

    private static int MaximumIdenticalColorRun(Bitmap bitmap, int y, int startX, int endX)
    {
        var maximum = 1;
        var current = 1;
        var previous = bitmap.GetPixel(startX, y).ToArgb();
        for (var x = startX + 1; x <= endX; x++)
        {
            var value = bitmap.GetPixel(x, y).ToArgb();
            current = value == previous ? current + 1 : 1;
            maximum = Math.Max(maximum, current);
            previous = value;
        }

        return maximum;
    }

    private static void ValidateAppBackdrop(Bitmap bitmap, int dpi, float scale)
    {
        var topRail = bitmap.GetPixel(Px(75, scale), Px(115, scale));
        var topQuiet = bitmap.GetPixel(Px(75, scale), Px(230, scale));
        var bottomRail = bitmap.GetPixel(Px(75, scale), Px(625, scale));
        var center = bitmap.GetPixel(Px(480, scale), Px(380, scale));
        NotEqual(topQuiet.ToArgb(), topRail.ToArgb(), $"{dpi} DPI app backdrop top rail must remain visible");
        NotEqual(center.ToArgb(), bottomRail.ToArgb(), $"{dpi} DPI app backdrop bottom rail must remain visible");
        NotEqual(ThemePalette.Canvas.ToArgb(), center.ToArgb(), $"{dpi} DPI app backdrop center must retain its dark material");
    }

    private static int CountMismatchedPixels(Bitmap expected, Bitmap actual)
    {
        if (expected.Size != actual.Size) return int.MaxValue;
        var mismatch = 0;
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
            if (expected.GetPixel(x, y).ToArgb() != actual.GetPixel(x, y).ToArgb()) mismatch++;
        return mismatch;
    }

    private static void ChoiceDialogUsesCustomControls()
    {
        using var dialog = (ChoiceDialog?)Activator.CreateInstance(
            typeof(ChoiceDialog),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: ["确认", "确认消息", "第一项", "第二项"],
            culture: null) ?? throw new InvalidOperationException("choice dialog could not be created");
        var nativeButtons = Descendants(dialog).Where(control => control is Button).Select(control => control.GetType().Name).ToArray();
        Equal(0, nativeButtons.Length, $"choice dialog must not contain native Button controls ({string.Join(", ", nativeButtons)})");
    }

    private static void KeyboardActivationWorks()
    {
        using var host = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Location = new Point(-32000, -32000), Size = new Size(320, 120) };
        var button = new TechButton { Text = "发送", Location = new Point(8, 8), Size = new Size(100, 40) };
        var toggle = new ToggleSwitch { Text = "记忆", Location = new Point(120, 8), Checked = false };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        host.Controls.AddRange([button, toggle]);
        host.Show();
        _ = button.Handle;
        _ = toggle.Handle;

        SendKey(button.Handle, Keys.Space);
        SendKey(toggle.Handle, Keys.Space);

        Equal(1, clicks, "Space must activate the custom button exactly once");
        Equal(true, toggle.Checked, "Space must toggle the custom switch");
        host.Close();
    }

    private static void SelfTestAcceptanceRejectsExistingDuplicates()
    {
        Equal(true, SelfTest.SingleModelWasReused([26576], [26576]), "one stable model process must pass reuse acceptance");
        Equal(false, SelfTest.SingleModelWasReused([13828, 26576], [13828, 26576]), "a pre-existing duplicate must fail reuse acceptance");
        Equal(false, SelfTest.SingleModelWasReused([26576], [13828, 26576]), "a newly added model process must fail reuse acceptance");
    }

    private static void RecallAcceptanceWaitsForVisibleMarker()
    {
        var attempts = 0;
        var result = SelfTest.RecallUntilVisibleAsync(
            () => Task.FromResult(++attempts < 3 ? "尚未建立索引" : "聊天验收-marker-123"),
            "聊天验收-marker-123",
            maxAttempts: 4,
            pollInterval: TimeSpan.Zero).GetAwaiter().GetResult();
        Equal(true, result.Contains("聊天验收-marker-123", StringComparison.Ordinal), "recall acceptance must return the first visible marker result");
        Equal(3, attempts, "recall acceptance must poll fresh state until the marker is visible");
    }

    private static void SemanticMemoryProbeIsDeterministicAndDistinct()
    {
        var first = SelfTest.CreateSemanticMemoryProbe(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var second = SelfTest.CreateSemanticMemoryProbe(Guid.Parse("00112234-4455-6677-8899-aabbccddeeff"));

        Equal("赤铜水獭藏起玻璃种子在雾松塔顶，校验尾码00112233。", first.Statement, "the fixed probe statement changed unexpectedly");
        Equal("回忆赤铜水獭的独特事实，完整回答它的动作、物品、地点和校验尾码。", first.Query, "the fixed probe query changed unexpectedly");
        Equal("00112233", first.Marker, "the fixed probe marker changed unexpectedly");
        Equal(false, string.Equals(first.Statement, second.Statement, StringComparison.Ordinal), "different probe ids must produce different semantic statements");
    }

    private static void RecentTraceAcceptanceRequiresSessionAndMarker()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"traces":[{"sessionId":"session-target","userText":"独特事实，校验尾码abc12345。"}]}
            """);
        Equal(true, SelfTest.RecentTraceContains(document.RootElement, "session-target", "abc12345"), "the exact session and marker must pass trace acceptance");
        Equal(false, SelfTest.RecentTraceContains(document.RootElement, "session-other", "abc12345"), "a different session must fail trace acceptance");
        Equal(false, SelfTest.RecentTraceContains(document.RootElement, "session-target", "deadbeef"), "a different marker must fail trace acceptance");
    }

    private static void TranscriptWheelUsesNativeScrollSemantics()
    {
        using var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Location = new Point(-32000, -32000),
            Size = new Size(420, 240),
        };
        var transcript = new ObsidianTranscriptBox
        {
            Dock = DockStyle.Fill,
            BackColor = ThemePalette.Surface,
            ForeColor = ThemePalette.Ink,
            Text = string.Join(Environment.NewLine, Enumerable.Range(1, 500).Select(index => $"第 {index} 行滚动回归内容")),
        };
        host.Controls.Add(transcript);
        host.Show();
        _ = transcript.Handle;
        Application.DoEvents();

        transcript.Focus();
        transcript.SelectionStart = 0;
        transcript.ScrollToCaret();
        SendKey(transcript.Handle, Keys.PageDown);
        Application.DoEvents();
        Equal(true, GetFirstVisibleLine(transcript.Handle) > 0, "Page Down must move a long transcript");
        SendKey(transcript.Handle, Keys.PageUp);
        Application.DoEvents();

        transcript.SelectionStart = transcript.TextLength;
        transcript.AppendText($"{Environment.NewLine}追加消息滚动验收");
        transcript.ScrollToCaret();
        Application.DoEvents();
        Equal(true, GetFirstVisibleLine(transcript.Handle) > 0, "ScrollToCaret must reveal an appended message");
        transcript.SelectionStart = 0;
        transcript.ScrollToCaret();
        Application.DoEvents();

        var fullViewportInvalidations = 0;
        transcript.Invalidated += (_, eventArgs) =>
        {
            if (eventArgs.InvalidRect == transcript.ClientRectangle) fullViewportInvalidations++;
        };

        for (var index = 1; index <= 120; index++)
        {
            SendMouseWheel(transcript.Handle, -SystemInformation.MouseWheelScrollDelta);
            if (index % 20 != 0) continue;
            _ = index;
        }
        Application.DoEvents();

        Equal(true, GetFirstVisibleLine(transcript.Handle) > 0, "continuous wheel scrolling must move a long transcript down");
        for (var index = 0; index < 140; index++) SendMouseWheel(transcript.Handle, SystemInformation.MouseWheelScrollDelta);
        Application.DoEvents();

        Equal(0, GetFirstVisibleLine(transcript.Handle), "continuous reverse scrolling must return to the first line");
        Equal(true, fullViewportInvalidations < 260, "native scrolling must not force a full custom repaint for every wheel detent");
        host.Close();
    }

    private static void NativeTranscriptKeepsPaintOwnership()
    {
        var sourcePath = Path.Combine(FindProjectRoot(), "src", "QwenLocalChat", "ObsidianTranscriptBox.cs");
        var source = File.ReadAllText(sourcePath);
        Equal(false, source.Contains("WndProc", StringComparison.Ordinal), "native transcript must not intercept WndProc painting");
        Equal(false, source.Contains("BeginPaint", StringComparison.Ordinal), "native transcript must not call BeginPaint");
        Equal(false, source.Contains("WM_PRINT", StringComparison.Ordinal), "native transcript must not compose WM_PRINT frames");
        Equal(false, source.Contains("BufferedGraphics", StringComparison.Ordinal), "native transcript must not own a second paint buffer");
    }

    private static void TranscriptWheelMovesOneNativeDetent()
    {
        using var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Location = new Point(-32000, -32000),
            Size = new Size(420, 240),
        };
        var transcript = new ObsidianTranscriptBox
        {
            Dock = DockStyle.Fill,
            BackColor = ThemePalette.Surface,
            ForeColor = ThemePalette.Ink,
            Text = string.Join(Environment.NewLine, Enumerable.Range(1, 500).Select(index => $"第 {index} 行单刻度滚轮回归")),
        };
        host.Controls.Add(transcript);
        host.Show();
        _ = transcript.Handle;
        Application.DoEvents();

        transcript.Focus();
        transcript.SelectionStart = 0;
        transcript.ScrollToCaret();
        Application.DoEvents();

        SendMouseWheel(transcript.Handle, -SystemInformation.MouseWheelScrollDelta);
        Application.DoEvents();

        var expectedLines = SystemInformation.MouseWheelScrollLines < 0
            ? Math.Max(1, transcript.ClientSize.Height / Math.Max(1, transcript.Font.Height) - 1)
            : Math.Max(1, SystemInformation.MouseWheelScrollLines);
        Equal(expectedLines, GetFirstVisibleLine(transcript.Handle), "one wheel detent must move exactly one native line-scroll amount");
        host.Close();
    }

    private static void RestoreRedrawGateBridgesRestoreFrame()
    {
        using var form = new MainForm(AppPaths.Discover(), startModel: false)
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(760, 560),
        };
        form.Show();
        Application.DoEvents();

        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        form.WindowState = FormWindowState.Normal;

        Equal(true, form.RestoreRedrawHeldForTesting, "restore must hold redraw before the first restored frame can paint");
        Application.DoEvents();
        Equal(false, form.RestoreRedrawHeldForTesting, "restore must release redraw after the queued layout pass");
        form.Dispose();
    }

    private static void TranscriptDecorationOverlayIsAnchoredSeparately()
    {
        using var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(640, 320),
        };
        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ThemePalette.Surface,
            FillColor = ThemePalette.Surface,
            CornerRadius = 18,
        };
        var transcript = new ObsidianTranscriptBox { Dock = DockStyle.Fill, BackColor = ThemePalette.Surface, BorderStyle = BorderStyle.None };
        shell.Controls.Add(transcript);
        host.Controls.Add(shell);
        host.Show();
        Application.DoEvents();

        using var overlay = new TranscriptDecorationOverlay(shell);
        overlay.ShowFor(host);
        Application.DoEvents();

        Equal(true, overlay.Visible, "the decoration layer must be visible while the owner is visible");
        Equal(host.Handle, overlay.Owner?.Handle ?? IntPtr.Zero, "the decoration layer must be owned by the main window");
        Equal(shell.PointToScreen(Point.Empty), overlay.Location, "the decoration layer must track the transcript shell location");
        Equal(shell.ClientSize, overlay.ClientSize, "the decoration layer must track the transcript shell size");
        host.Close();
    }

    private static void TranscriptDecorationBindingIsIdempotent()
    {
        using var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(640, 320),
        };
        using var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = ThemePalette.Surface,
            BorderColor = ThemePalette.Border,
            CornerRadius = 18,
        };
        host.Controls.Add(shell);
        host.Show();
        Application.DoEvents();

        using var overlay = new TranscriptDecorationOverlay(shell);
        overlay.ShowFor(host);
        overlay.ShowFor(host);
        Application.DoEvents();

        Equal(1, overlay.OwnerEventBindingsForTesting, "repeated ShowFor must keep one owner event binding");
        Equal(true, overlay.Visible, "idempotent ShowFor must keep one visible decoration window");
        host.Close();
    }

    private static void BackgroundGeometryAvoidsRadialRingCompositing()
    {
        var sourcePath = Path.Combine(FindProjectRoot(), "src", "QwenLocalChat", "UiDrawing.cs");
        var source = File.ReadAllText(sourcePath);
        var mainFormPath = Path.Combine(FindProjectRoot(), "src", "QwenLocalChat", "MainForm.cs");
        var mainForm = File.ReadAllText(mainFormPath);
        Equal(false, source.Contains("PathGradientBrush", StringComparison.Ordinal), "fixed backdrop must not use radial path gradients that quantize into rings");
        Equal(false, source.Contains("glowPath.AddEllipse", StringComparison.Ordinal), "fixed backdrop must not add a second radial ellipse layer");
        Equal(false, source.Contains("bandPath.AddPolygon", StringComparison.Ordinal), "fixed backdrop must not use a hard polygon in place of the soft prototype beam");
        Equal(true, source.Contains("CreateAppBackdrop", StringComparison.Ordinal), "fixed backdrop must keep one cached pixel-generated layer");
        Equal(true, source.Contains("topRail", StringComparison.Ordinal), "fixed backdrop must keep the approved top rail");
        Equal(true, source.Contains("bottomRail", StringComparison.Ordinal), "fixed backdrop must keep the approved bottom rail");
        Equal(true, source.Contains("DrawRail", StringComparison.Ordinal), "fixed backdrop must draw rails through one owner");
        Equal(false, mainForm.Contains("TranscriptDecorationOverlay", StringComparison.Ordinal), "production chat must not add a second decoration window over the transcript");
    }

    private static void TranscriptDecorationSuspensionSurvivesOwnerResize()
    {
        using var host = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(640, 320),
        };
        using var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = ThemePalette.Surface,
            BorderColor = ThemePalette.Border,
            CornerRadius = 18,
        };
        host.Controls.Add(shell);
        host.Show();
        Application.DoEvents();

        using var overlay = new TranscriptDecorationOverlay(shell);
        overlay.ShowFor(host);
        Application.DoEvents();
        Equal(true, overlay.Visible, "decoration must start visible before restore suspension");

        overlay.SuspendSynchronization(true);
        host.ClientSize = new Size(660, 340);
        Application.DoEvents();
        Equal(false, overlay.Visible, "owner resize during restore suspension must not reshow decoration");

        overlay.SuspendSynchronization(false);
        Application.DoEvents();
        Equal(true, overlay.Visible, "decoration must resynchronize after restore suspension is released");
        host.Close();
    }

    private static void SendKey(IntPtr handle, Keys key)
    {
        const int wmKeyDown = 0x0100;
        const int wmKeyUp = 0x0101;
        _ = SendMessage(handle, wmKeyDown, (IntPtr)key, IntPtr.Zero);
        _ = SendMessage(handle, wmKeyUp, (IntPtr)key, IntPtr.Zero);
    }

    private static void SendMouseWheel(IntPtr handle, int delta)
    {
        const int wmMouseWheel = 0x020A;
        var wheelData = unchecked((uint)(ushort)(short)delta) << 16;
        _ = SendMessage(handle, wmMouseWheel, (IntPtr)wheelData, IntPtr.Zero);
    }

    private static int GetFirstVisibleLine(IntPtr handle)
    {
        const int emGetFirstVisibleLine = 0x00CE;
        return SendMessage(handle, emGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "QwenLocalChat", "QwenLocalChat.csproj"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate the local-chat project root.");
    }

    private static int Px(float logical, float scale) => (int)Math.Round(logical * scale, MidpointRounding.AwayFromZero);
    private static RectangleF Rect(float x, float y, float width, float height, float scale) => new(Px(x, scale), Px(y, scale), Px(width, scale), Px(height, scale));
    private static Rectangle RectI(float x, float y, float width, float height, float scale) => Rectangle.Round(Rect(x, y, width, height, scale));

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}; expected={expected}, actual={actual}");
    }

    private static void NotEqual<T>(T forbidden, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(forbidden, actual)) throw new InvalidOperationException($"{message}; forbidden={forbidden}");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

}
