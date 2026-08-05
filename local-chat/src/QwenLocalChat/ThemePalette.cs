namespace QwenLocalChat;

internal static class ThemePalette
{
    private static readonly string UiFamily = ResolveFont("Segoe UI Variable Text", "Microsoft YaHei UI");
    private static readonly string MonoFamily = ResolveFont("Cascadia Mono", "Consolas");

    public static readonly Color Canvas = Color.FromArgb(8, 8, 9);
    public static readonly Color Header = Color.FromArgb(17, 17, 18);
    public static readonly Color Surface = Color.FromArgb(14, 14, 15);
    public static readonly Color Raised = Color.FromArgb(24, 24, 26);
    public static readonly Color Ink = Color.FromArgb(241, 241, 238);
    public static readonly Color Secondary = Color.FromArgb(163, 163, 160);
    public static readonly Color Muted = Color.FromArgb(111, 111, 108);
    public static readonly Color Border = Color.FromArgb(53, 53, 55);
    public static readonly Color StrongBorder = Color.FromArgb(92, 92, 94);
    public static readonly Color PrimaryButton = Color.FromArgb(240, 240, 237);
    public static readonly Color PrimaryButtonText = Color.FromArgb(17, 17, 18);

    public static Font Ui(float size, FontStyle style = FontStyle.Regular)
        => new(UiFamily, size, style, GraphicsUnit.Point);

    public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        => new(MonoFamily, size, style, GraphicsUnit.Point);

    private static string ResolveFont(string preferred, string fallback)
        => FontFamily.Families.Any(item => item.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ? preferred : fallback;
}
