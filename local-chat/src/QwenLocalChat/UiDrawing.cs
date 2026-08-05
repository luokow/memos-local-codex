using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QwenLocalChat;

internal static class UiDrawing
{
    private static readonly (float X, float Y, int Alpha)[] TranscriptNodes =
    [
        (0.73f, 0.20f, 72),
        (0.88f, 0.12f, 96),
        (0.93f, 0.38f, 82),
        (0.78f, 0.66f, 62),
        (0.91f, 0.84f, 86),
    ];
    public static float ScaleForDpi(int dpi) => Math.Max(1f, dpi / 96f);

    public static void Prepare(Graphics graphics)
    {
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
    }

    public static RectangleF ClientBounds(Size clientSize, float scale, float inset = 0.75f)
        => ClientBounds(new Rectangle(Point.Empty, clientSize), scale, inset);

    public static RectangleF ClientBounds(Rectangle clientRectangle, float scale, float inset = 0.75f)
    {
        var physicalInset = inset * scale;
        return new RectangleF(
            clientRectangle.Left + physicalInset,
            clientRectangle.Top + physicalInset,
            Math.Max(1f, clientRectangle.Width - physicalInset * 2f),
            Math.Max(1f, clientRectangle.Height - physicalInset * 2f));
    }

    public static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(Math.Max(0f, radius) * 2f, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0.01f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics graphics, RectangleF bounds, float radius, Color color)
    {
        using var path = CreateRoundedPath(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedBorder(Graphics graphics, RectangleF bounds, float radius, Color color, float scale)
    {
        using var path = CreateRoundedPath(bounds, radius);
        using var pen = new Pen(color, Math.Max(1f, scale));
        graphics.DrawPath(pen, path);
    }

    public static void DrawRoundedSurface(Graphics graphics, RectangleF bounds, float radius, Color fill, Color border, float scale)
    {
        FillRounded(graphics, bounds, radius, fill);
        DrawRoundedBorder(graphics, bounds, radius, border, scale);
    }

    public static void DrawTechButton(
        Graphics graphics,
        Rectangle clientRectangle,
        string text,
        Font font,
        bool primary,
        bool enabled,
        bool hovered,
        bool pressed,
        bool isDefault,
        bool focused,
        float scale)
    {
        Prepare(graphics);
        var bounds = ClientBounds(clientRectangle, scale);
        var radius = 10f * scale;
        var fill = ResolveButtonFill(primary, enabled, hovered, pressed);
        var borderColor = hovered || isDefault ? ThemePalette.StrongBorder : ThemePalette.Border;
        if (primary) FillRounded(graphics, bounds, radius, fill);
        else DrawRoundedSurface(graphics, bounds, radius, fill, borderColor, scale);

        var textColor = enabled
            ? primary ? ThemePalette.PrimaryButtonText : ThemePalette.Ink
            : ThemePalette.Muted;
        TextRenderer.DrawText(graphics, text, font, clientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (!focused) return;
        using var focusPath = CreateRoundedPath(RectangleF.Inflate(bounds, -3.5f * scale, -3.5f * scale), 7f * scale);
        using var focus = new Pen(primary ? Color.FromArgb(120, 17, 17, 18) : Color.FromArgb(150, 241, 241, 238), Math.Max(1f, scale))
        {
            DashStyle = DashStyle.Dot,
        };
        graphics.DrawPath(focus, focusPath);
    }

    public static void DrawToggle(
        Graphics graphics,
        Rectangle clientRectangle,
        string text,
        Font font,
        Color foreColor,
        bool isChecked,
        bool enabled,
        bool hovered,
        bool focused,
        float scale)
    {
        Prepare(graphics);
        var trackWidth = 34f * scale;
        var trackHeight = 18f * scale;
        var track = new RectangleF(clientRectangle.Left + 2f * scale, clientRectangle.Top + (clientRectangle.Height - trackHeight) / 2f, trackWidth, trackHeight);
        using var trackPath = CreateRoundedPath(track, trackHeight / 2f);
        using var trackBrush = new SolidBrush(isChecked ? ThemePalette.PrimaryButton : Color.FromArgb(11, 11, 12));
        using var trackBorder = new Pen(isChecked ? ThemePalette.PrimaryButton : hovered ? ThemePalette.StrongBorder : ThemePalette.Border, Math.Max(1f, scale));
        graphics.FillPath(trackBrush, trackPath);
        graphics.DrawPath(trackBorder, trackPath);

        var knobSize = 12f * scale;
        var knobInset = 3f * scale;
        var knobX = isChecked ? track.Right - knobSize - knobInset : track.Left + knobInset;
        using var knob = new SolidBrush(isChecked ? ThemePalette.PrimaryButtonText : ThemePalette.Secondary);
        graphics.FillEllipse(knob, knobX, track.Top + knobInset, knobSize, knobSize);

        var textX = (int)Math.Ceiling(46f * scale);
        var textBounds = new Rectangle(clientRectangle.Left + textX, clientRectangle.Top, Math.Max(1, clientRectangle.Width - textX - (int)(8f * scale)), clientRectangle.Height);
        var textColor = enabled ? hovered || isChecked ? ThemePalette.Ink : foreColor : ThemePalette.Muted;
        TextRenderer.DrawText(graphics, text, font, textBounds, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (!focused) return;
        using var focusPath = CreateRoundedPath(RectangleF.Inflate(track, 2f * scale, 2f * scale), trackHeight / 2f + 2f * scale);
        using var focus = new Pen(Color.FromArgb(145, ThemePalette.Ink), Math.Max(1f, scale)) { DashStyle = DashStyle.Dot };
        graphics.DrawPath(focus, focusPath);
    }

    public static void DrawStatusPill(Graphics graphics, Rectangle clientRectangle, string text, Font font, Color foreColor, float scale)
    {
        Prepare(graphics);
        var bounds = ClientBounds(clientRectangle, scale, 0.5f);
        DrawRoundedSurface(graphics, bounds, 10f * scale, Color.FromArgb(132, ThemePalette.Raised), Color.FromArgb(150, ThemePalette.Border), scale);
        TextRenderer.DrawText(graphics, text, font, clientRectangle, foreColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    public static void DrawTranscriptDecoration(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var decoration = CreateTranscriptDecoration(bounds.Size);
        graphics.DrawImage(
            decoration,
            bounds,
            new Rectangle(Point.Empty, decoration.Size),
            GraphicsUnit.Pixel);
    }

    public static Bitmap CreateTranscriptDecoration(Size size)
    {
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var pixels = new byte[bitmapData.Stride * height];
            for (var y = 0; y < height; y++)
            {
                var normalizedY = (y + 0.5f) / height;
                for (var x = 0; x < width; x++)
                {
                    var normalizedX = (x + 0.5f) / width;
                    var bandCenter = 0.24f + 0.58f * (1f - normalizedY);
                    var bandDistance = MathF.Abs(normalizedX - bandCenter) / 0.21f;
                    var bandEnvelope = 0.45f + 0.55f * SmoothFalloff(MathF.Abs(normalizedY - 0.50f) / 0.60f);
                    var band = 25f * SmoothFalloff(bandDistance) * bandEnvelope;

                    var haloDistance = MathF.Sqrt(
                        MathF.Pow((normalizedX - 0.82f) / 0.72f, 2f) +
                        MathF.Pow((normalizedY - 0.08f) / 0.58f, 2f));
                    var halo = 16f * SmoothFalloff(haloDistance);
                    var requestedAlpha = Math.Min(30f, band + halo);
                    if (requestedAlpha < 0.35f) continue;
                    var ditheredAlpha = requestedAlpha + (Noise01(x, y) - 0.5f) * 1.6f;
                    var alpha = (byte)Math.Clamp((int)MathF.Round(ditheredAlpha), 0, 255);
                    if (alpha == 0) continue;

                    var offset = y * bitmapData.Stride + x * 4;
                    pixels[offset] = 237;
                    pixels[offset + 1] = 240;
                    pixels[offset + 2] = 240;
                    pixels[offset + 3] = alpha;
                }
            }

            Marshal.Copy(pixels, 0, bitmapData.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var graphics = Graphics.FromImage(bitmap);
        Prepare(graphics);

        using (var connector = new Pen(Color.FromArgb(22, 241, 241, 238), 1f))
        {
            var points = TranscriptNodes
                .Take(3)
                .Select(node => new PointF(width * node.X, height * node.Y))
                .ToArray();
            graphics.DrawLines(connector, points);
        }

        for (var index = 0; index < TranscriptNodes.Length; index++)
        {
            var node = TranscriptNodes[index];
            var point = new PointF(width * node.X, height * node.Y);
            using var glow = new SolidBrush(Color.FromArgb(Math.Max(7, node.Alpha / 8), 241, 241, 238));
            graphics.FillEllipse(glow, point.X - 2.2f, point.Y - 2.2f, 4.4f, 4.4f);
            using var core = new SolidBrush(Color.FromArgb(node.Alpha, 241, 241, 238));
            graphics.FillEllipse(core, point.X - 0.75f, point.Y - 0.75f, 1.5f, 1.5f);
        }

        return bitmap;
    }

    private static float SmoothFalloff(float distance)
    {
        var value = Math.Clamp(1f - distance, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    private static float Noise01(int x, int y)
    {
        var value = unchecked((uint)(x * 374761393 + y * 668265263));
        value = (value ^ (value >> 13)) * 1274126177u;
        return (value & 0x00FFFFFFu) / 16777216f;
    }

    public static void DrawAppBackdrop(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        Prepare(graphics);
        graphics.ResetClip();
        graphics.SetClip(bounds, CombineMode.Replace);
        using var backdrop = CreateAppBackdrop(bounds.Size);
        graphics.DrawImage(backdrop, bounds, new Rectangle(Point.Empty, backdrop.Size), GraphicsUnit.Pixel);
    }

    // This is the approved 01 双轨信号 background layer. The decoration is
    // fixed to the application viewport; the transcript and composer remain
    // separate native controls above it. The two rails are deliberately kept
    // at the top and bottom boundaries so no ornament enters the reading area.
    public static Bitmap CreateAppBackdrop(Size size)
    {
        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var pixels = new byte[Math.Abs(stride) * height];
            var rowStride = Math.Abs(stride);
            for (var y = 0; y < height; y++)
            {
                var yRatio = (y + 0.5f) / height;
                var edge = MathF.Max(
                    SmoothFalloff((0.25f - yRatio) / 0.25f),
                    SmoothFalloff((yRatio - 0.75f) / 0.25f));
                var offsetRow = stride >= 0 ? y * stride : (height - 1 - y) * rowStride;
                for (var x = 0; x < width; x++)
                {
                    var xRatio = (x + 0.5f) / width;
                    var topRight = MathF.Max(0f, 1f - MathF.Sqrt(
                        MathF.Pow((xRatio - 0.82f) / 0.72f, 2f) +
                        MathF.Pow((yRatio - 0.08f) / 0.58f, 2f)));
                    var gradient = 0.5f + 0.5f * (1f - xRatio) + 0.08f * (1f - yRatio);
                    var noise = (Noise01(x, y) - 0.5f) * 1.8f;
                    var blue = 14f + 4f * gradient + 3f * edge + 3f * topRight;
                    var green = 10f + 4f * gradient + 4f * edge + 4f * topRight;
                    var red = 7f + 2f * gradient + 1f * edge + 1f * topRight;
                    var offset = offsetRow + x * 4;
                    pixels[offset] = ToByte(blue + noise);
                    pixels[offset + 1] = ToByte(green + noise);
                    pixels[offset + 2] = ToByte(red + noise);
                    pixels[offset + 3] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var decoration = Graphics.FromImage(bitmap);
        Prepare(decoration);
        decoration.ResetClip();
        decoration.SetClip(new RectangleF(0, 0, width, height), CombineMode.Replace);
        var scale = Math.Max(0.75f, Math.Min(width / 960f, height / 740f));
        var topRail = new[]
        {
            PrototypePoint(width, height, 36, 118),
            PrototypePoint(width, height, 660, 118),
            PrototypePoint(width, height, 724, 72),
            PrototypePoint(width, height, 1120, 72),
        };
        var bottomRail = new[]
        {
            PrototypePoint(width, height, 36, 642),
            PrototypePoint(width, height, 348, 642),
            PrototypePoint(width, height, 414, 692),
            PrototypePoint(width, height, 824, 692),
            PrototypePoint(width, height, 880, 650),
            PrototypePoint(width, height, 1238, 650),
        };
        DrawRail(decoration, topRail, scale);
        DrawRail(decoration, bottomRail, scale);

        using (var shortLine = new Pen(Color.FromArgb(28, 167, 213, 240), Math.Max(1f, 3f * scale)))
        {
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 92, 146), PrototypePoint(width, height, 414, 146));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 458, 146), PrototypePoint(width, height, 612, 146));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 754, 100), PrototypePoint(width, height, 860, 100));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 900, 100), PrototypePoint(width, height, 1088, 100));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 74, 670), PrototypePoint(width, height, 226, 670));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 466, 718), PrototypePoint(width, height, 608, 718));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 654, 718), PrototypePoint(width, height, 814, 718));
            decoration.DrawLine(shortLine, PrototypePoint(width, height, 944, 676), PrototypePoint(width, height, 1100, 676));
        }

        using (var connector = new Pen(Color.FromArgb(44, 182, 224, 247), Math.Max(1f, scale)))
        {
            decoration.DrawLine(connector, PrototypePoint(width, height, 724, 72), PrototypePoint(width, height, 724, 34));
            decoration.DrawLine(connector, PrototypePoint(width, height, 724, 34), PrototypePoint(width, height, 782, 34));
            decoration.DrawLine(connector, PrototypePoint(width, height, 414, 692), PrototypePoint(width, height, 414, 728));
            decoration.DrawLine(connector, PrototypePoint(width, height, 414, 728), PrototypePoint(width, height, 454, 728));
            decoration.DrawLine(connector, PrototypePoint(width, height, 1088, 100), PrototypePoint(width, height, 1088, 54));
            decoration.DrawLine(connector, PrototypePoint(width, height, 1088, 54), PrototypePoint(width, height, 1162, 54));
            decoration.DrawLine(connector, PrototypePoint(width, height, 1100, 676), PrototypePoint(width, height, 1100, 720));
            decoration.DrawLine(connector, PrototypePoint(width, height, 1100, 720), PrototypePoint(width, height, 1170, 720));
        }

        using (var node = new SolidBrush(Color.FromArgb(196, 217, 243, 255)))
        {
            foreach (var point in new[]
            {
                PrototypePoint(width, height, 658, 116),
                PrototypePoint(width, height, 724, 70),
                PrototypePoint(width, height, 414, 690),
                PrototypePoint(width, height, 880, 648),
            })
            {
                var nodeSize = Math.Max(3f, 5f * scale);
                decoration.FillRectangle(node, point.X - nodeSize / 2f, point.Y - nodeSize / 2f, nodeSize, nodeSize);
            }
        }

        return bitmap;
    }

    private static PointF PrototypePoint(int width, int height, float x, float y)
        => new(width * x / 1280f, height * y / 760f);

    private static void DrawRail(Graphics graphics, PointF[] points, float scale)
    {
        using var broadGlow = new Pen(Color.FromArgb(9, 126, 181, 216), Math.Max(4f, 18f * scale)) { LineJoin = LineJoin.Round };
        using var softGlow = new Pen(Color.FromArgb(18, 146, 198, 229), Math.Max(2f, 7f * scale)) { LineJoin = LineJoin.Round };
        using var rail = new Pen(Color.FromArgb(56, 167, 213, 240), Math.Max(1f, scale)) { LineJoin = LineJoin.Round };
        graphics.DrawLines(broadGlow, points);
        graphics.DrawLines(softGlow, points);
        graphics.DrawLines(rail, points);
    }

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static Color ResolveButtonFill(bool primary, bool enabled, bool hovered, bool pressed)
    {
        if (!enabled) return primary ? Color.FromArgb(92, 92, 90) : Color.FromArgb(20, 20, 22);
        if (primary)
        {
            if (pressed) return Color.FromArgb(206, 206, 202);
            if (hovered) return Color.White;
            return ThemePalette.PrimaryButton;
        }
        if (pressed) return Color.FromArgb(39, 39, 41);
        if (hovered) return Color.FromArgb(31, 31, 33);
        return ThemePalette.Raised;
    }
}
