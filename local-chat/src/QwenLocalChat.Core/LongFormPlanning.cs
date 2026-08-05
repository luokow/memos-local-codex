using System.Globalization;
using System.Text.RegularExpressions;

namespace QwenLocalChat.Core;

/// <summary>
/// Active multi-segment long-form job for one conversation thread.
/// </summary>
public sealed class LongFormPlan
{
    public required string OriginalUserPrompt { get; init; }
    public required int TargetChars { get; init; }
    public required int SegmentChars { get; init; }
    public required int TotalSegments { get; init; }
    public int CompletedSegments { get; set; }
    public bool Active => CompletedSegments < TotalSegments;

    public int NextSegmentIndex => CompletedSegments; // 0-based
    public int NextSegmentNumber => CompletedSegments + 1;
}

/// <summary>
/// Detects soft length goals like「约4000字」, tightens max_tokens, and plans segment turns.
/// </summary>
public static partial class LongFormPlanner
{
    public const int SegmentMinChars = 800;
    public const int SegmentMaxChars = 1_500;
    public const int SegmentDefaultChars = 1_000;
    /// <summary>Targets at or above this use multi-turn segments instead of one long burst.</summary>
    public const int SegmentModeThresholdChars = 2_000;

    public const string NextSegmentUserLabel = "（续写下一段）";
    public const string NextSegmentButtonLabel = "下一段";
    public const string PrefillContinueButtonLabel = "继续";

    /// <summary>
    /// Parse a soft character target from free-form Chinese/English prompts.
    /// Examples: 4000字, 约4000字, 写一篇4000字, 4k字, 三千字.
    /// </summary>
    public static int? TryParseTargetChars(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return null;
        var text = userMessage.Trim();

        // 4000字 / 约 4,000 字 / 写4000字小说
        foreach (Match match in TargetDigitsRegex().Matches(text))
        {
            var raw = match.Groups["n"].Value.Replace(",", "").Replace("，", "");
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                continue;
            if (n is >= 200 and <= 50_000)
                return n;
        }

        // 4k字 / 4K 字
        foreach (Match match in TargetKRegex().Matches(text))
        {
            if (!int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k))
                continue;
            var n = k * 1_000;
            if (n is >= 200 and <= 50_000)
                return n;
        }

        // 两千字 / 三千字 / 四千字 …
        var cn = TryParseChineseThousand(text);
        if (cn is >= 200 and <= 50_000)
            return cn;

        return null;
    }

    /// <summary>
    /// Start a plan when the user asks for a long piece (≥ segment threshold).
    /// Shorter targets only tighten max_tokens (no multi-turn plan).
    /// </summary>
    public static LongFormPlan? TryBeginSegmentedPlan(string userMessage)
    {
        var target = TryParseTargetChars(userMessage);
        if (target is null || target.Value < SegmentModeThresholdChars)
            return null;

        var segmentChars = ChooseSegmentChars(target.Value);
        var total = Math.Max(2, (int)Math.Ceiling(target.Value / (double)segmentChars));
        // Prefer 3–6 segments for typical 3k–6k asks.
        if (total > 8)
        {
            segmentChars = Math.Clamp(
                (int)Math.Ceiling(target.Value / 6.0),
                SegmentMinChars,
                SegmentMaxChars);
            total = Math.Max(2, (int)Math.Ceiling(target.Value / (double)segmentChars));
        }

        return new LongFormPlan
        {
            OriginalUserPrompt = userMessage.Trim(),
            TargetChars = target.Value,
            SegmentChars = segmentChars,
            TotalSegments = total,
            CompletedSegments = 0,
        };
    }

    public static int ChooseSegmentChars(int targetChars)
    {
        if (targetChars <= 2_500) return 1_000;
        if (targetChars <= 4_500) return 1_000;
        if (targetChars <= 8_000) return 1_200;
        return SegmentMaxChars;
    }

    /// <summary>
    /// Map a soft character goal to a token budget. CJK ≈ 1 token/char in our estimator;
    /// add headroom then clamp to settings.
    /// </summary>
    public static int SuggestMaxOutputTokens(int? targetChars, int settingsMax, bool segmentTurn)
    {
        settingsMax = Math.Max(128, settingsMax);
        if (targetChars is null or <= 0)
            return settingsMax;

        var chars = targetChars.Value;
        // Segment turns use a short burst; single-shot uses the full soft goal.
        var budgetChars = segmentTurn
            ? Math.Clamp(chars, SegmentMinChars, SegmentMaxChars + 200)
            : chars;

        // ~1.15 token per CJK char + small fixed headroom for titles/markdown.
        var tokens = (int)Math.Ceiling(budgetChars * 1.15) + 64;
        tokens = Math.Clamp(tokens, 256, settingsMax);

        // Soft goals well below the settings max should not still fire the full max.
        if (!segmentTurn && chars < settingsMax)
            tokens = Math.Min(tokens, Math.Max(256, (int)Math.Ceiling(chars * 1.20) + 80));

        return tokens;
    }

    public static int ResolveRequestMaxOutputTokens(
        string userMessage,
        int settingsMax,
        LongFormPlan? activePlan,
        bool isSegmentTurn)
    {
        if (isSegmentTurn && activePlan is not null)
            return SuggestMaxOutputTokens(activePlan.SegmentChars, settingsMax, segmentTurn: true);

        var target = TryParseTargetChars(userMessage);
        if (target is null)
            return settingsMax;

        // Long asks that open a plan are first segments.
        if (target.Value >= SegmentModeThresholdChars)
            return SuggestMaxOutputTokens(
                ChooseSegmentChars(target.Value),
                settingsMax,
                segmentTurn: true);

        return SuggestMaxOutputTokens(target.Value, settingsMax, segmentTurn: false);
    }

    public static string BuildSegmentUserMessage(LongFormPlan plan, int segmentIndex)
    {
        var n = segmentIndex + 1;
        var total = plan.TotalSegments;
        var seg = plan.SegmentChars;
        var original = plan.OriginalUserPrompt.Trim();

        if (segmentIndex == 0)
        {
            return
                $"{original}\n\n" +
                $"【分段写作·第{n}/{total}段】全文目标约{plan.TargetChars}字。" +
                $"现在只写开头第{n}段，约{seg}字：推进情节，不要写结局，本段结束即停，禁止重复句子。";
        }

        if (n >= total)
        {
            return
                $"【分段写作·第{n}/{total}段·收束】接着上文继续写约{seg}字，" +
                $"完成高潮与结局并自然收束。禁止重复已写段落与套话堆砌，写完即停。";
        }

        return
            $"【分段写作·第{n}/{total}段】接着上文继续写约{seg}字，推进情节与冲突，" +
            $"不要提前写结局，本段结束即停，禁止重复已写内容。";
    }

    public static string SegmentVisibleLabel(LongFormPlan plan, int segmentIndex)
    {
        var n = segmentIndex + 1;
        if (segmentIndex == 0)
            return $"{Truncate(plan.OriginalUserPrompt, 48)} · 第{n}/{plan.TotalSegments}段";
        return $"{NextSegmentUserLabel} 第{n}/{plan.TotalSegments}段（约{plan.SegmentChars}字）";
    }

    public static void MarkSegmentCompleted(LongFormPlan plan)
    {
        if (plan.CompletedSegments < plan.TotalSegments)
            plan.CompletedSegments++;
    }

    public static bool CanContinueNextSegment(LongFormPlan? plan, IReadOnlyList<ChatMessage> history)
    {
        if (plan is null || !plan.Active) return false;
        if (history.Count == 0) return false;
        return history[^1].Role is "assistant" && !string.IsNullOrWhiteSpace(history[^1].Content);
    }

    public static string? BudgetNotice(int settingsMax, int effectiveMax, int? targetChars, bool segmented)
        => GoalFeedback(
            userMessage: null,
            settingsMax,
            effectiveMax,
            autoTighten: effectiveMax < settingsMax || targetChars is not null,
            segmentedEnabled: segmented,
            plan: null,
            isSegmentTurn: segmented,
            targetCharsOverride: targetChars);

    /// <summary>
    /// One-line UX notice for soft length goals: segmented / tightened / detected-but-disabled.
    /// </summary>
    public static string? GoalFeedback(
        string? userMessage,
        int settingsMax,
        int effectiveMax,
        bool autoTighten,
        bool segmentedEnabled,
        LongFormPlan? plan,
        bool isSegmentTurn,
        int? targetCharsOverride = null)
    {
        var target = targetCharsOverride
            ?? plan?.TargetChars
            ?? TryParseTargetChars(userMessage);

        if (plan is not null && isSegmentTurn)
        {
            return $"长文分段 {plan.NextSegmentNumber}/{plan.TotalSegments} · 全文约{plan.TargetChars}字 · 本段上限 {effectiveMax} token（设置 {settingsMax}）。";
        }

        if (plan is not null && segmentedEnabled)
        {
            return $"长文已按约{plan.TargetChars}字启用分段（本段上限 {effectiveMax} token，设置 {settingsMax}）。";
        }

        if (target is not null && effectiveMax < settingsMax && (autoTighten || isSegmentTurn))
            return $"已按约{target}字收紧本轮上限为 {effectiveMax} token（设置 {settingsMax}）。";

        if (target is not null && !autoTighten && !segmentedEnabled)
        {
            return $"检测到约{target}字目标；「按目标字数收紧输出」「长文分段写作」均为关闭，本轮使用上限 {settingsMax} token。可在设置中开启。";
        }

        if (target is not null && autoTighten && effectiveMax >= settingsMax)
            return $"约{target}字目标不低于设置上限，本轮仍用 {settingsMax} token。";

        return null;
    }

    /// <summary>Live status while a reply is streaming.</summary>
    public static string FormatGenerationProgress(
        int maxOutputTokens,
        int elapsedSeconds,
        int outputChars,
        LongFormPlan? plan,
        bool isSegmentTurn,
        bool isContinuation)
    {
        var mode = isContinuation
            ? "继续生成"
            : isSegmentTurn && plan is not null
                ? $"分段 {plan.NextSegmentNumber}/{plan.TotalSegments}"
                : "生成中";
        var secs = Math.Max(0, elapsedSeconds);
        var body = $"{mode} · 上限 {maxOutputTokens} token · 已 {secs}s";
        if (outputChars > 0)
            body += $" · 约 {outputChars} 字";
        if (plan is not null && isSegmentTurn)
            body += $" · 全文约{plan.TargetChars}字";
        return body;
    }

    public static string PrefillContinueTooltip
        => "从上次中断处接着写（未写完被截断时）";

    public static string NextSegmentTooltip(LongFormPlan plan)
        => $"续写第 {plan.NextSegmentNumber}/{plan.TotalSegments} 段（约 {plan.SegmentChars} 字/段，全文约 {plan.TargetChars} 字）";


    private static int? TryParseChineseThousand(string text)
    {
        // 两千/三千/四千/五千/六千/八千/一万 字
        var map = new Dictionary<char, int>
        {
            ['两'] = 2, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5,
            ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10,
        };
        var m = ChineseThousandRegex().Match(text);
        if (!m.Success) return null;
        var head = m.Groups["h"].Value;
        if (head == "一" || head == "壹") return 10_000;
        if (head.Length == 1 && map.TryGetValue(head[0], out var n))
            return n * 1_000;
        return null;
    }

    private static string Truncate(string text, int max)
    {
        var one = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (one.Contains("  ", StringComparison.Ordinal))
            one = one.Replace("  ", " ", StringComparison.Ordinal);
        if (one.Length <= max) return one;
        return one[..max].TrimEnd() + "…";
    }

    [GeneratedRegex(
        @"(?<n>\d{3,5})\s*[字詞词]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetDigitsRegex();

    [GeneratedRegex(
        @"(?<n>\d{1,2})\s*[kK]\s*[字詞词]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetKRegex();

    [GeneratedRegex(
        @"(?<h>[两二三四五六七八九十壹一])\s*千\s*字|(?<h>一)\s*万\s*字",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChineseThousandRegex();
}
