using System.Text;

namespace QwenLocalChat.Core;

/// <summary>
/// Detects trailing "idiom / slogan salad": long CJK runs without punctuation where
/// four-character packs chain together (和谐共生互利互惠协同发展…).
/// Distinct from paragraph loops handled by <see cref="GenerationRepetitionGuard"/>.
///
/// Important: plain Chinese prose also has long unpunctuated CJK runs. A naive
/// "length/4 packs" rule false-fires and aborts normal long-form mid-stream.
/// Slogan salad is recognized by low function-word density + 4-char packing.
/// </summary>
public static class SloganChainDetector
{
    public const int DefaultTailChars = 400;
    public const int MinRunChars = 64;
    public const int MinPacks = 8;
    /// <summary>
    /// Prose is usually well above this; idiom walls sit near zero.
    /// Keep mild room for idiom characters like 上/中/与 that also appear in slogans.
    /// </summary>
    public const double MaxFunctionCharRatio = 0.10;
    /// <summary>Early-stop needs a longer, clearer salad than post-hoc trim.</summary>
    public const int StreamMinRunChars = 120;
    public const int StreamMinPacks = 12;
    public const double StreamMaxFunctionCharRatio = 0.05;

    // High-frequency narrative glue (particles / pronouns). Avoid directional 上中下 that appear in idioms.
    private const string FunctionChars =
        "的了是在有和就不人我他这那着过说你她它也还又都把被让吗呢吧啊呀嘛给";

    public readonly record struct Analysis(
        bool HasSloganChain,
        int TrailingCjkRunChars,
        int FourCharPacks,
        double PackDensity,
        double FunctionCharRatio,
        string TailSample);

    public static Analysis Analyze(string? text, int tailChars = DefaultTailChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinRunChars)
            return new Analysis(false, 0, 0, 0, 0, string.Empty);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var tail = normalized.Length <= tailChars
            ? normalized
            : normalized[^tailChars..];

        var run = MeasureTrailingCjkRun(normalized);
        if (run < MinRunChars)
            return new Analysis(false, run, 0, 0, 0, TailPreview(tail));

        var runText = ExtractTrailingCjk(normalized, run);
        var packs = runText.Length / 4;
        var density = runText.Length > 0 ? packs * 4.0 / runText.Length : 0;
        var funcRatio = FunctionRatio(runText);
        // Slogan salad: long pure-CJK, edge-to-edge 4-char packs, almost no narrative glue.
        var has = packs >= MinPacks
                  && density >= 0.90
                  && run >= MinRunChars
                  && funcRatio <= MaxFunctionCharRatio;
        return new Analysis(
            has,
            run,
            packs,
            Math.Round(density, 3),
            Math.Round(funcRatio, 3),
            TailPreview(runText.Length <= 120 ? runText : runText[^120..]));
    }

    public static bool HasSloganChain(string? text)
        => Analyze(text).HasSloganChain;

    /// <summary>
    /// Streaming early-stop: only after a long, low-function-word slogan wall is clear.
    /// Must stay conservative — false positives cancel the server mid-novel.
    /// </summary>
    public static bool ShouldStopStreaming(string accumulated)
    {
        if (string.IsNullOrEmpty(accumulated) || accumulated.Length < StreamMinRunChars * 2)
            return false;
        var a = Analyze(accumulated, tailChars: 320);
        return a.HasSloganChain
               && a.TrailingCjkRunChars >= StreamMinRunChars
               && a.FourCharPacks >= StreamMinPacks
               && a.FunctionCharRatio <= StreamMaxFunctionCharRatio;
    }

    /// <summary>
    /// Soft trim: cut the trailing CJK slogan run if it dominates the ending.
    /// Does not invent new text — only removes the salad tail.
    /// </summary>
    public static GenerationRepetitionGuard.TrimResult TrimTrailingSloganChain(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new GenerationRepetitionGuard.TrimResult(text, false, 0);

        var work = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        var a = Analyze(work);
        if (!a.HasSloganChain || a.TrailingCjkRunChars < MinRunChars)
            return new GenerationRepetitionGuard.TrimResult(work, false, 0);

        var cut = work.Length - a.TrailingCjkRunChars;
        var windowStart = Math.Max(0, cut - 80);
        var region = work[windowStart..cut];
        var lastBreak = Math.Max(
            region.LastIndexOf("\n\n", StringComparison.Ordinal),
            Math.Max(region.LastIndexOf('。'), region.LastIndexOf('！')));
        if (lastBreak >= 0)
            cut = windowStart + lastBreak + (region[lastBreak] == '\n' ? 2 : 1);

        if (cut < 8)
            return new GenerationRepetitionGuard.TrimResult(work, false, 0);

        var trimmed = work[..cut].TrimEnd();
        if (work.Length - trimmed.Length < MinRunChars / 2)
            return new GenerationRepetitionGuard.TrimResult(work, false, 0);

        return new GenerationRepetitionGuard.TrimResult(trimmed, true, 1);
    }

    private static int MeasureTrailingCjkRun(string text)
    {
        var i = text.Length - 1;
        while (i >= 0 && IsIgnorableTail(text[i])) i--;
        var end = i;
        while (i >= 0 && IsCjkNoPunct(text[i])) i--;
        return end >= 0 ? end - i : 0;
    }

    private static string ExtractTrailingCjk(string text, int run)
    {
        if (run <= 0 || text.Length == 0) return string.Empty;
        var slice = text[^Math.Min(run + 8, text.Length)..];
        var sb = new StringBuilder(run);
        for (var i = slice.Length - 1; i >= 0; i--)
        {
            if (IsIgnorableTail(slice[i])) continue;
            if (!IsCjkNoPunct(slice[i])) break;
            sb.Insert(0, slice[i]);
        }
        return sb.ToString();
    }

    private static double FunctionRatio(string cjkRun)
    {
        if (cjkRun.Length == 0) return 0;
        var hits = 0;
        foreach (var ch in cjkRun)
        {
            if (FunctionChars.Contains(ch))
                hits++;
        }
        return (double)hits / cjkRun.Length;
    }

    private static bool IsCjkNoPunct(char ch)
        => ch is >= '\u4e00' and <= '\u9fff';

    private static bool IsIgnorableTail(char ch)
        => char.IsWhiteSpace(ch) || ch is '…' or '。' or '！' or '!' or '」' or '"' or '”' or '’' or '？' or '?';

    private static string TailPreview(string s)
    {
        var t = s.Replace('\n', ' ').Trim();
        return t.Length <= 100 ? t : t[^100..];
    }
}
