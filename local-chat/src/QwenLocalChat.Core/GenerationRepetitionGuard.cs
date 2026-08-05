using System.Text;

namespace QwenLocalChat.Core;

/// <summary>
/// Detects and trims paragraph/block loops that local models often enter near the end of
/// long free-form generations (consecutive A-A, interleaved A-B-C-A-B-C, exact block re-runs).
/// </summary>
public static class GenerationRepetitionGuard
{
    private const int MinParagraphChars = 48;
    private const int MinBlockChars = 80;
    private const int MaxBlockChars = 2_400;

    public readonly record struct TrimResult(string Text, bool Trimmed, int RemovedUnits);

    public static TrimResult TrimTrailingLoops(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinParagraphChars * 2)
            return new TrimResult(text, false, 0);

        var work = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        var original = work;
        var removed = 0;

        // 1) Paragraph cycle periods: [A B C][A B C] → keep one period (also covers A A A).
        var parts = SplitParagraphs(work);
        while (parts.Count >= 2)
        {
            var period = FindTrailingCyclePeriod(parts);
            if (period <= 0) break;
            for (var i = 0; i < period; i++)
                parts.RemoveAt(parts.Count - 1);
            removed += period;
        }
        work = JoinParagraphs(parts);

        // 2) Exact trailing block re-runs (multi-paragraph cycles without clean boundaries).
        //    Repeat until stable — long answers often re-run the same 1–2k block several times.
        while (true)
        {
            var next = TrimExactTrailingBlockRepeat(work);
            if (string.Equals(next, work, StringComparison.Ordinal)) break;
            work = next;
            removed++;
        }

        // 3) Trailing idiom/slogan salad (和谐共生互利互惠…) — not a paragraph loop.
        var slogan = SloganChainDetector.TrimTrailingSloganChain(work);
        if (slogan.Trimmed)
        {
            work = slogan.Text;
            removed += slogan.RemovedUnits;
        }

        if (removed == 0 && string.Equals(work, original, StringComparison.Ordinal))
            return new TrimResult(text.TrimEnd(), false, 0);

        return new TrimResult(work.TrimEnd(), !string.Equals(work, original, StringComparison.Ordinal), removed);
    }

    public static bool ShouldStopStreaming(string accumulated)
    {
        if (string.IsNullOrEmpty(accumulated) || accumulated.Length < MinParagraphChars * 3)
            return false;

        var text = accumulated.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = SplitParagraphs(text);
        if (parts.Count >= 4 && FindTrailingCyclePeriod(parts) > 0)
            return true;

        // Exact block re-run of >= 120 chars is enough to stop mid-stream.
        if (HasExactTrailingBlockRepeat(text, minLen: 120))
            return true;

        return SloganChainDetector.ShouldStopStreaming(text);
    }

    internal static int FindTrailingCyclePeriod(IReadOnlyList<string> parts)
    {
        if (parts.Count < 2) return 0;
        var maxPeriod = Math.Min(parts.Count / 2, 8);
        for (var k = maxPeriod; k >= 1; k--)
        {
            if (!PeriodIsSubstantial(parts, k)) continue;
            var match = true;
            for (var i = 0; i < k; i++)
            {
                if (!ExactNormalizedEqual(parts[parts.Count - 2 * k + i], parts[parts.Count - k + i]))
                {
                    match = false;
                    break;
                }
            }
            if (match) return k;
        }
        return 0;
    }

    private static bool PeriodIsSubstantial(IReadOnlyList<string> parts, int k)
    {
        var total = 0;
        for (var i = 0; i < k; i++)
            total += parts[parts.Count - k + i].Length;
        return total >= MinParagraphChars * k;
    }

    private static bool ExactNormalizedEqual(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static List<string> SplitParagraphs(string text)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var newlines = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                newlines++;
                if (newlines >= 2)
                {
                    var piece = sb.ToString().Trim();
                    if (piece.Length > 0) parts.Add(piece);
                    sb.Clear();
                    newlines = 0;
                }
                continue;
            }
            if (newlines == 1) sb.Append('\n');
            newlines = 0;
            sb.Append(ch);
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts;
    }

    private static string JoinParagraphs(IReadOnlyList<string> parts)
        => string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    private static bool HasExactTrailingBlockRepeat(string text, int minLen)
    {
        var maxBlock = Math.Min(MaxBlockChars, text.Length / 2);
        for (var len = maxBlock; len >= minLen; len--)
        {
            if (text.Length < len * 2) continue;
            var suffix = text.AsSpan(text.Length - len);
            if (suffix.IsWhiteSpace()) continue;
            if (text.AsSpan(0, text.Length - len).LastIndexOf(suffix) >= 0)
                return true;
        }
        return false;
    }

    private static string TrimExactTrailingBlockRepeat(string text)
    {
        var maxBlock = Math.Min(MaxBlockChars, text.Length / 2);
        for (var len = maxBlock; len >= MinBlockChars; len--)
        {
            if (text.Length < len * 2) continue;
            var suffix = text[^len..];
            if (string.IsNullOrWhiteSpace(suffix)) continue;
            if (text.AsSpan(0, text.Length - len).LastIndexOf(suffix, StringComparison.Ordinal) < 0)
                continue;

            var cut = text.Length - len;
            while (cut > 0 && (text[cut - 1] == '\n' || char.IsWhiteSpace(text[cut - 1])))
                cut--;
            if (cut >= MinParagraphChars * 2)
                return text[..cut].TrimEnd();
        }
        return text;
    }
}
