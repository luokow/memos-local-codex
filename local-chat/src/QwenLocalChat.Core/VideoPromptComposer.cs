using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QwenLocalChat.Core;

public sealed record VideoPromptExtras(
    string Action = "",
    string Sound = "",
    string Music = "",
    string Identity = "");

/// <summary>
/// H3 only attends to official IR fields. User slots, duration, and trailing
/// notes are compiled into those fields on submit.
/// </summary>
public static class VideoPromptComposer
{
    public const string DirectorNotesHeading = "Director notes (must follow):";
    public const string NotesOverrideSummary =
        "Director notes override clothing, body state, action details, and sound. Keep only facial identity locked to the appearance picture.";
    public const string NotesOverrideSubject =
        "Director notes may change clothing, body exposure, motion, and sound; keep the same person's face and identity.";
    public const string NotesOverrideRetention =
        "Clothing, body state, action, and sound follow director notes; only facial identity stays fully_preserved.";
    public const string OfficialStyleLine =
        "The target video uses a cinematic live-action style.";
    public const string ActionFollowsShotSummary =
        "Action and camera follow the [Shot 1] description.";
    public const string ActionFollowsShotRetention =
        "body action and camera follow the [Shot 1] description; face, hair, and outfit stay locked to the appearance picture";
    public const string LegacyStyleLine = "Cinematic live-action, medium shot.";
    public const string LegacyKeyframeStylePrefix = "Live-action, cinematic,";
    public const string FirstFrameGenericMotion =
        "The camera then begins a small, slow movement as the same person continues naturally from that first frame.";
    public const string FirstLastMetaInstruction =
        "Do not jump-cut between the two stills; write the in-between action.";

    private static readonly Regex FieldHeader = new(
        @"^(subject_definitions|summary|retention_analysis|detailed_description|integrated_multimodal_description|overall_soundscape|non_diegetic_music)\s*:\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LooksExactlyLike = new(
        @"<Subject\s+(\d+)>\s+looks exactly like\s+(<Picture\s+\d+>)\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PoseContinue = new(
        @"Then\s+<Subject\s+\d+>\s+continues that action naturally for the rest of the clip\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex KeepPictureCamera = new(
        @"Keep\s+<(?:Picture|Video)\s+\d+>'s camera(?: movement)? and relative positions\.\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CameraFramingFollow = new(
        @"The camera framing, character placement, and pose follow\s+(<Picture\s+\d+>|<Video\s+\d+>)\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VideoCameraFollow = new(
        @"The camera movement, blocking, and action follow\s+(<Video\s+\d+>)\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Fl2vaEndMark = new(
        @"aligns with the (?:end of the target video|\d+\.\d+-second mark of the target video)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuotedSpeech = new(
        @"(?:说|喊|叫|问|答)[：:]?\s*[「『“""](.+?)[」』”""]",
        RegexOptions.Compiled);

    public static string MergeDirectorNotes(string? prompt)
        => Compose(prompt, template: null);

    /// <summary>
    /// Keep only the unstructured extras from a composer box that still contains
    /// a previously pasted official skeleton.
    /// </summary>
    public static string ExtractExtras(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var parsed = Parse(text);
        if (parsed is null) return text.Trim();
        return parsed.Trailer.Trim();
    }

    public static string FormatDurationMark(int durationSeconds)
        => Math.Clamp(durationSeconds, 1, 300).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Merge free-form extras into an official template. Untagged lines go to the
    /// picture description; prefixed lines (<c>声音:</c> <c>配乐:</c> <c>身份:</c>)
    /// replace or append the matching field. An empty extras box still sends the
    /// template after duration and legacy-style sanitizing.
    /// </summary>
    public static string Compose(
        string? extras,
        string? template = null,
        VideoPromptTemplatePhrases? phrases = null,
        string? assembledOverride = null,
        int? durationSeconds = null)
    {
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        if (!string.IsNullOrWhiteSpace(assembledOverride))
            return Finalize(assembledOverride, copy, durationSeconds);

        extras ??= "";
        template = template?.Trim() ?? "";
        if (template.Length == 0)
            return Finalize(extras, copy, durationSeconds, wrapUnstructured: true);

        var parsed = Parse(template);
        if (parsed is null)
        {
            if (string.IsNullOrWhiteSpace(extras))
                return Finalize(template, copy, durationSeconds, wrapUnstructured: false);
            return Finalize(
                template.TrimEnd() + Newline(template) + Newline(template) + extras,
                copy,
                durationSeconds,
                wrapUnstructured: true);
        }

        var nl = Newline(template);
        if (!string.IsNullOrWhiteSpace(extras))
            ApplyExtras(parsed, extras, nl, copy);
        parsed.Trailer = "";
        return FinalizeParsed(parsed, nl, copy, durationSeconds);
    }

    public static string EnsureExtrasForm(string? extras, VideoPromptTemplatePhrases? phrases = null)
    {
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var slots = SplitExtras(extras ?? "", copy);
        var lines = new List<string>();
        foreach (var (slot, prefix) in copy.ExtraPrefixSlots())
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            var body = (slots.GetValueOrDefault(slot) ?? "").Trim();
            lines.Add(body.Length == 0
                ? prefix + ": "
                : body.Contains('\n', StringComparison.Ordinal)
                    ? prefix + ":" + "\n" + body
                    : prefix + ": " + body);
        }
        return string.Join("\n", lines);
    }

    public static VideoPromptExtras ReadExtras(string? extras, VideoPromptTemplatePhrases? phrases = null)
    {
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var slots = SplitExtras(extras ?? "", copy);
        return new VideoPromptExtras(
            Action: (slots.GetValueOrDefault("description") ?? "").Trim(),
            Sound: (slots.GetValueOrDefault("overall_soundscape") ?? "").Trim(),
            Music: (slots.GetValueOrDefault("non_diegetic_music") ?? "").Trim(),
            Identity: (slots.GetValueOrDefault("subject_definitions") ?? "").Trim());
    }

    public static string WriteExtras(VideoPromptExtras extras, VideoPromptTemplatePhrases? phrases = null)
    {
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = extras.Action ?? "",
            ["overall_soundscape"] = extras.Sound ?? "",
            ["non_diegetic_music"] = extras.Music ?? "",
            ["subject_definitions"] = extras.Identity ?? "",
        };
        var lines = new List<string>();
        foreach (var (slot, prefix) in copy.ExtraPrefixSlots())
        {
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            var body = values.GetValueOrDefault(slot)?.Trim() ?? "";
            lines.Add(body.Length == 0
                ? prefix + ": "
                : body.Contains('\n', StringComparison.Ordinal)
                    ? prefix + ":\n" + body
                    : prefix + ": " + body);
        }
        return string.Join("\n", lines);
    }

    private static string Finalize(
        string? prompt,
        VideoPromptTemplatePhrases phrases,
        int? durationSeconds,
        bool wrapUnstructured = true)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return prompt ?? "";
        var parsed = Parse(prompt);
        if (parsed is null)
            return wrapUnstructured
                ? WrapUnstructured(prompt, phrases, durationSeconds)
                : prompt;

        var notes = parsed.Trailer.Trim();
        var nl = Newline(prompt);
        if (notes.Length > 0)
        {
            IntegrateUserShot(parsed, notes, nl, wrapDirectorNotes: NeedsDirectorOverride(notes), phrases);
            if (LooksLikeAudioOnlyNotes(notes))
                OverlaySoundscape(parsed, notes, phrases);
            if (NeedsDirectorOverride(notes))
                ReconcileOfficialFields(parsed, nl);
            else
                ReconcileActionFields(parsed, nl);
            parsed.Trailer = "";
        }
        return FinalizeParsed(parsed, nl, phrases, durationSeconds);
    }

    private static string FinalizeParsed(
        ParsedPrompt parsed,
        string nl,
        VideoPromptTemplatePhrases phrases,
        int? durationSeconds)
    {
        ApplyDurationAlignment(parsed, durationSeconds);
        RewriteLegacyStyle(parsed, phrases, nl);
        parsed.Trailer = "";
        EnsureDescriptionOrder(parsed);
        return Render(parsed, nl);
    }

    private static string WrapUnstructured(string extras, VideoPromptTemplatePhrases phrases, int? durationSeconds)
    {
        extras = extras.Trim();
        if (extras.Length == 0) return extras;

        var slots = SplitExtras(extras, phrases);
        var description = (slots.GetValueOrDefault("description") ?? "").Trim();
        if (description.Length == 0)
            description = extras;
        var sound = (slots.GetValueOrDefault("overall_soundscape") ?? "").Trim();
        var music = (slots.GetValueOrDefault("non_diegetic_music") ?? "").Trim();
        var identity = (slots.GetValueOrDefault("subject_definitions") ?? "").Trim();
        var nl = "\n";
        var camera = TryEnglishCameraSentence(description);
        var payload = BuildShotPayload(
            WrapDialogue(description),
            camera,
            NeedsDirectorOverride(description) || NeedsDirectorOverride(identity),
            nl);
        if (identity.Length > 0)
            payload += nl + "Keep identity: " + identity;

        var body = phrases.StyleLine + nl + "[Shot 1] " + payload;
        var skeleton =
            "integrated_multimodal_description:" + nl +
            body + nl + nl +
            "overall_soundscape: " + (sound.Length > 0 ? sound : phrases.Soundscape) + nl +
            "non_diegetic_music: " + (music.Length > 0 ? music : phrases.Music) + nl;
        return Finalize(skeleton, phrases, durationSeconds, wrapUnstructured: false);
    }

    private static void ApplyExtras(ParsedPrompt parsed, string extras, string nl, VideoPromptTemplatePhrases phrases)
    {
        var slots = SplitExtras(extras, phrases);
        var description = (slots.GetValueOrDefault("description") ?? "").Trim();
        if (description.Length > 0)
            IntegrateUserShot(parsed, description, nl, wrapDirectorNotes: NeedsDirectorOverride(description), phrases);
        if (slots.TryGetValue("overall_soundscape", out var sound) && !string.IsNullOrWhiteSpace(sound))
            parsed.Blocks["overall_soundscape"] = sound.Trim();
        if (slots.TryGetValue("non_diegetic_music", out var music) && !string.IsNullOrWhiteSpace(music))
            parsed.Blocks["non_diegetic_music"] = music.Trim();
        if (slots.TryGetValue("subject_definitions", out var subjects) && !string.IsNullOrWhiteSpace(subjects))
            IntegrateIdentity(parsed, subjects.Trim(), nl, phrases);
        if (slots.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary))
            AppendBlock(parsed, "summary", summary.Trim(), nl);
        if (slots.TryGetValue("retention_analysis", out var retention) && !string.IsNullOrWhiteSpace(retention))
            AppendBlock(parsed, "retention_analysis", retention.Trim(), nl);

        var allExtras = extras.Trim();
        if (NeedsDirectorOverride(allExtras))
            ReconcileOfficialFields(parsed, nl);
        else if (description.Length > 0)
            ReconcileActionFields(parsed, nl);
        if (LooksLikeAudioOnlyNotes(allExtras)
            && string.IsNullOrWhiteSpace(slots.GetValueOrDefault("overall_soundscape")))
            OverlaySoundscape(parsed, allExtras, phrases);
        EnsureDescriptionOrder(parsed);
    }

    private static Dictionary<string, string> SplitExtras(string extras, VideoPromptTemplatePhrases phrases)
    {
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var asOfficial = Parse(extras);
        if (asOfficial is not null
            && asOfficial.Blocks.Count > 0
            && string.IsNullOrWhiteSpace(asOfficial.Preamble))
        {
            foreach (var pair in asOfficial.Blocks)
                AppendSlot(slots, CanonicalSlot(pair.Key, phrases) ?? "description", pair.Value);
            if (!string.IsNullOrWhiteSpace(asOfficial.Trailer))
                AppendSlot(slots, "description", asOfficial.Trailer);
            return slots;
        }

        var current = "description";
        foreach (var raw in extras.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (TryMatchSlotHeader(raw, phrases, out var slot, out var rest))
            {
                current = slot;
                AppendSlot(slots, current, rest);
                continue;
            }
            AppendSlot(slots, current, raw);
        }
        return slots;
    }

    private static bool TryMatchSlotHeader(string line, VideoPromptTemplatePhrases phrases, out string slot, out string rest)
    {
        slot = "description";
        rest = line;
        var trimmed = line.Trim();
        var split = trimmed.IndexOfAny([':', '：']);
        if (split <= 0) return false;
        var name = trimmed[..split].Trim();
        rest = trimmed[(split + 1)..].Trim();
        var canonical = CanonicalSlot(name, phrases);
        if (canonical is null) return false;
        slot = canonical;
        return true;
    }

    private static string? CanonicalSlot(string name, VideoPromptTemplatePhrases phrases)
    {
        var builtIn = name.Trim().ToLowerInvariant() switch
        {
            "detailed_description" or "integrated_multimodal_description"
                or "description" or "画面" or "镜头" or "动作" or "补充" or "画面描述" => "description",
            "overall_soundscape" or "soundscape" or "声音" or "音效" or "环境声" => "overall_soundscape",
            "non_diegetic_music" or "music" or "配乐" or "音乐" => "non_diegetic_music",
            "subject_definitions" or "subject" or "身份" or "外观" => "subject_definitions",
            "summary" or "摘要" => "summary",
            "retention_analysis" or "retention" or "保留" => "retention_analysis",
            _ => null,
        };
        if (builtIn is not null) return builtIn;

        var needle = VideoPromptTemplatePhrases.NormalizePrefix(name);
        foreach (var (slot, prefix) in phrases.ExtraPrefixSlots())
        {
            if (prefix.Length > 0
                && string.Equals(needle, prefix, StringComparison.OrdinalIgnoreCase))
                return slot;
        }
        return null;
    }

    private static void AppendSlot(Dictionary<string, string> slots, string slot, string value)
    {
        if (string.IsNullOrWhiteSpace(slot)) return;
        if (slots.TryGetValue(slot, out var existing) && existing.Length > 0)
            slots[slot] = existing.TrimEnd() + "\n" + value;
        else
            slots[slot] = value;
    }

    private static void IntegrateUserShot(
        ParsedPrompt parsed,
        string notes,
        string nl,
        bool wrapDirectorNotes,
        VideoPromptTemplatePhrases phrases)
    {
        notes = WrapDialogue(notes.Trim());
        if (notes.Length == 0) return;

        var key = DescriptionKey(parsed);
        var existing = parsed.Blocks.GetValueOrDefault(key) ?? "";
        var camera = TryEnglishCameraSentence(notes);
        var rewritten = RewriteOfficialDescription(existing, rewriteCameraFollow: true, phrases);
        var replacedGenericMotion = !string.IsNullOrWhiteSpace(existing)
            && existing.Contains(FirstFrameGenericMotion, StringComparison.Ordinal)
            && !rewritten.Contains(FirstFrameGenericMotion, StringComparison.Ordinal);

        var payload = BuildShotPayload(notes, camera, wrapDirectorNotes, "\n");
        if (!DescriptionBodyContains(rewritten, notes))
        {
            if (replacedGenericMotion)
            {
                rewritten = rewritten.Replace(
                    "USER_SHOT_PAYLOAD",
                    payload.Replace("\n", " "),
                    StringComparison.Ordinal);
            }
            else
            {
                rewritten = rewritten.Replace("USER_SHOT_PAYLOAD", "", StringComparison.Ordinal);
                if (rewritten.Length > 0) rewritten = rewritten.TrimEnd() + "\n";
                rewritten += payload;
            }
        }
        else
        {
            rewritten = rewritten.Replace("USER_SHOT_PAYLOAD", "", StringComparison.Ordinal);
        }

        rewritten = CollapseBlankLines(rewritten, "\n");
        parsed.Blocks[key] = rewritten;
        if (!parsed.Order.Contains(key, StringComparer.OrdinalIgnoreCase))
            InsertBefore(parsed.Order, key, "overall_soundscape");
    }

    private static void IntegrateIdentity(
        ParsedPrompt parsed,
        string notes,
        string nl,
        VideoPromptTemplatePhrases phrases)
    {
        if (parsed.Blocks.ContainsKey("subject_definitions"))
        {
            AppendBlock(parsed, "subject_definitions", notes, nl);
            return;
        }

        IntegrateUserShot(parsed, "Keep identity: " + notes, nl, wrapDirectorNotes: NeedsDirectorOverride(notes), phrases);
    }

    private static string BuildShotPayload(string notes, string? camera, bool wrapDirectorNotes, string nl)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(camera))
            parts.Add(camera.Trim());
        if (wrapDirectorNotes)
            parts.Add(DirectorNotesHeading + nl + notes);
        else
            parts.Add(notes);
        return string.Join(nl, parts);
    }

    private static string RewriteOfficialDescription(
        string existing,
        bool rewriteCameraFollow,
        VideoPromptTemplatePhrases phrases)
    {
        var body = (existing ?? "").TrimEnd();
        if (body.Length == 0)
            return phrases.StyleLine + "\n" + "[Shot 1]";

        body = body.Replace(LegacyStyleLine, phrases.StyleLine, StringComparison.Ordinal);
        body = StripLegacyKeyframeStyle(body, phrases, "\n");
        body = LooksExactlyLike.Replace(
            body,
            m => $"<Subject {m.Groups[1].Value}> keeps the facial identity of {m.Groups[2].Value} and performs the action described in this shot.");
        body = PoseContinue.Replace(body, "");
        body = body.Replace(FirstLastMetaInstruction, "", StringComparison.Ordinal);
        if (body.Contains(FirstFrameGenericMotion, StringComparison.Ordinal))
            body = body.Replace(FirstFrameGenericMotion, "USER_SHOT_PAYLOAD", StringComparison.Ordinal);
        if (rewriteCameraFollow)
        {
            body = KeepPictureCamera.Replace(body, "");
            body = CameraFramingFollow.Replace(
                body,
                m => $"Character placement and pose follow {m.Groups[1].Value}.");
            body = VideoCameraFollow.Replace(
                body,
                m => $"Blocking and action follow {m.Groups[1].Value}.");
        }

        body = EnsureShotMarker(body, "\n");
        return CollapseBlankLines(body, "\n");
    }

    private static void RewriteLegacyStyle(ParsedPrompt parsed, VideoPromptTemplatePhrases phrases, string nl)
    {
        foreach (var key in new[] { "detailed_description", "integrated_multimodal_description" })
        {
            if (!parsed.Blocks.TryGetValue(key, out var body) || body.Length == 0) continue;
            var next = body.Replace(LegacyStyleLine, phrases.StyleLine, StringComparison.Ordinal);
            next = StripLegacyKeyframeStyle(next, phrases, nl);
            parsed.Blocks[key] = next;
        }
    }

    private static string StripLegacyKeyframeStyle(string body, VideoPromptTemplatePhrases phrases, string nl)
    {
        if (!body.Contains(LegacyKeyframeStylePrefix, StringComparison.OrdinalIgnoreCase))
            return body;
        var next = Regex.Replace(
            body,
            @"Live-action,\s*cinematic,\s*",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        next = Regex.Replace(
            next,
            @"Live-action,\s*cinematic",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!next.Contains(phrases.StyleLine, StringComparison.Ordinal)
            && !next.Contains(OfficialStyleLine, StringComparison.Ordinal))
            next = phrases.StyleLine + nl + next.TrimStart();
        return next;
    }

    private static void ApplyDurationAlignment(ParsedPrompt parsed, int? durationSeconds)
    {
        if (durationSeconds is not int seconds || seconds < 1) return;
        var mark = "aligns with the " + FormatDurationMark(seconds) + "-second mark of the target video";
        parsed.Preamble = Fl2vaEndMark.Replace(parsed.Preamble ?? "", mark);
        foreach (var key in parsed.Blocks.Keys.ToArray())
            parsed.Blocks[key] = Fl2vaEndMark.Replace(parsed.Blocks[key], mark);
    }

    private static string EnsureShotMarker(string body, string nl)
    {
        if (body.Contains("[Shot 1]", StringComparison.OrdinalIgnoreCase))
            return body;
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count == 1)
            return lines[0] + "\n" + "[Shot 1]";
        lines[1] = "[Shot 1] " + lines[1].TrimStart();
        return string.Join("\n", lines);
    }

    private static string CollapseBlankLines(string text, string nl)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();
        var blank = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (blank || kept.Count == 0) continue;
                blank = true;
                continue;
            }
            blank = false;
            kept.Add(line.TrimEnd());
        }
        return string.Join("\n", kept);
    }

    private static bool DescriptionBodyContains(string body, string notes)
        => body.Contains(notes, StringComparison.Ordinal);

    private static string WrapDialogue(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes) || notes.Contains("<d>", StringComparison.Ordinal))
            return notes;
        return QuotedSpeech.Replace(
            notes,
            m => $"says: <d>[中文] {m.Groups[1].Value}</d>");
    }

    private static void OverlaySoundscape(ParsedPrompt parsed, string notes, VideoPromptTemplatePhrases phrases)
    {
        var sound = (parsed.Blocks.GetValueOrDefault("overall_soundscape") ?? "").Trim();
        var isDefault = sound.Length == 0
            || sound.Contains("Quiet room tone", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sound, phrases.Soundscape.Trim(), StringComparison.Ordinal);
        if (isDefault)
            parsed.Blocks["overall_soundscape"] = notes;
        else if (!sound.Contains(notes, StringComparison.Ordinal))
            parsed.Blocks["overall_soundscape"] = sound + " " + notes;
    }

    private static void AppendBlock(ParsedPrompt parsed, string key, string value, string nl)
    {
        var existing = parsed.Blocks.GetValueOrDefault(key) ?? "";
        parsed.Blocks[key] = string.IsNullOrWhiteSpace(existing)
            ? value
            : existing.TrimEnd() + "\n" + value;
        if (!parsed.Order.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(key, "subject_definitions", StringComparison.OrdinalIgnoreCase))
                parsed.Order.Insert(0, key);
            else
                parsed.Order.Add(key);
        }
    }

    private static string DescriptionKey(ParsedPrompt parsed)
        => parsed.Blocks.ContainsKey("detailed_description")
            ? "detailed_description"
            : parsed.Blocks.ContainsKey("integrated_multimodal_description")
                ? "integrated_multimodal_description"
                : "detailed_description";

    private static void EnsureDescriptionOrder(ParsedPrompt parsed)
    {
        var key = DescriptionKey(parsed);
        if (parsed.Blocks.ContainsKey(key) && !parsed.Order.Contains(key, StringComparer.OrdinalIgnoreCase))
            InsertBefore(parsed.Order, key, "overall_soundscape");
    }

    private static bool NeedsDirectorOverride(string extras)
        => extras.Contains("衣服", StringComparison.Ordinal)
           || extras.Contains("服装", StringComparison.Ordinal)
           || extras.Contains("露出", StringComparison.Ordinal)
           || extras.Contains("裸", StringComparison.Ordinal)
           || extras.Contains("胸", StringComparison.Ordinal)
           || extras.Contains("乳", StringComparison.Ordinal)
           || extras.Contains("身体", StringComparison.Ordinal)
           || extras.Contains("换装", StringComparison.Ordinal)
           || extras.Contains("脱掉", StringComparison.Ordinal)
           || extras.Contains("外套", StringComparison.Ordinal)
           || extras.Contains("衬衫", StringComparison.Ordinal)
           || extras.Contains("裙子", StringComparison.Ordinal)
           || extras.Contains("内衣", StringComparison.Ordinal);

    private static void ReconcileActionFields(ParsedPrompt parsed, string nl)
    {
        if (parsed.Blocks.TryGetValue("retention_analysis", out var retention)
            && retention.Contains("face, body, hair, and outfit stay locked to", StringComparison.Ordinal))
        {
            parsed.Blocks["retention_analysis"] = retention.Replace(
                "face, body, hair, and outfit stay locked to",
                "face, hair, and outfit stay locked to",
                StringComparison.Ordinal);
            retention = parsed.Blocks["retention_analysis"];
            if (!retention.Contains("action and camera follow", StringComparison.OrdinalIgnoreCase))
                parsed.Blocks["retention_analysis"] = retention.TrimEnd() + "\n" + ActionFollowsShotRetention + ".";
        }

        if (parsed.Blocks.TryGetValue("summary", out var summary)
            && !summary.Contains("Action and camera follow", StringComparison.Ordinal)
            && !summary.Contains("Director notes override", StringComparison.Ordinal))
        {
            parsed.Blocks["summary"] = summary.TrimEnd() + " " + ActionFollowsShotSummary;
        }
    }

    private static void ReconcileOfficialFields(ParsedPrompt parsed, string nl)
    {
        if (parsed.Blocks.TryGetValue("subject_definitions", out var subjects))
        {
            subjects = RelaxExclusiveLocks(subjects);
            if (!subjects.Contains(NotesOverrideSubject, StringComparison.Ordinal))
                subjects = subjects.TrimEnd() + "\n" + NotesOverrideSubject;
            parsed.Blocks["subject_definitions"] = subjects;
        }

        if (parsed.Blocks.TryGetValue("retention_analysis", out var retention))
        {
            retention = RelaxExclusiveLocks(retention);
            if (!retention.Contains("follow director notes", StringComparison.Ordinal))
                retention = retention.TrimEnd() + "\n" + NotesOverrideRetention;
            parsed.Blocks["retention_analysis"] = retention;
        }

        if (parsed.Blocks.TryGetValue("summary", out var summary)
            && !summary.Contains("Director notes override", StringComparison.Ordinal))
        {
            parsed.Blocks["summary"] = summary.TrimEnd() + " " + NotesOverrideSummary;
        }
    }

    private static string RelaxExclusiveLocks(string text)
        => text
            .Replace("face, body, hair, and clothing come only from", "face and identity come from", StringComparison.Ordinal)
            .Replace("face, body, hair, and outfit stay locked to", "face and identity stay locked to", StringComparison.Ordinal)
            .Replace("outfit stay locked to", "face stays locked to", StringComparison.Ordinal);

    private static bool LooksLikeAudioOnlyNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return false;
        var hasAudio = notes.Contains('声', StringComparison.Ordinal)
            || notes.Contains('音', StringComparison.Ordinal)
            || notes.Contains("呻吟", StringComparison.Ordinal)
            || notes.Contains("嗯啊", StringComparison.Ordinal);
        if (!hasAudio) return false;
        return !SuggestsVisualAction(notes);
    }

    internal static bool SuggestsCamera(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return false;
        ReadOnlySpan<string> needles =
        [
            "镜头", "运镜", "机位", "特写", "中景", "全景", "近景", "远景", "全身",
            "推进", "推近", "拉远", "拉近", "固定", "俯视", "俯拍", "仰视", "仰拍",
            "环绕", "跟随", "摇镜", "升镜", "横移", "平移", "晃", "滚转", "变焦",
            "dolly", "zoom", "pan", "tilt", "truck", "shake", "roll", "pedestal",
            "tracking", "push in", "pull out", "static",
        ];
        foreach (var needle in needles)
        {
            if (notes.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool SuggestsVisualAction(string notes)
        => SuggestsCamera(notes)
           || notes.Contains("拉开", StringComparison.Ordinal)
           || notes.Contains("衣服", StringComparison.Ordinal)
           || notes.Contains("服装", StringComparison.Ordinal)
           || notes.Contains("躺", StringComparison.Ordinal)
           || notes.Contains("坐", StringComparison.Ordinal)
           || notes.Contains("走", StringComparison.Ordinal)
           || notes.Contains("动作", StringComparison.Ordinal)
           || notes.Contains("双腿", StringComparison.Ordinal)
           || notes.Contains("表情", StringComparison.Ordinal)
           || notes.Contains("露出", StringComparison.Ordinal)
           || notes.Contains("插入", StringComparison.Ordinal)
           || notes.Contains("转头", StringComparison.Ordinal)
           || notes.Contains("微笑", StringComparison.Ordinal);

    internal static string? TryEnglishCameraSentence(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;

        string? motion = null;
        if (ContainsAny(notes, "环绕")) motion = "Arc Shot";
        else if (ContainsAny(notes, "跟随", "tracking")) motion = "Tracking Shot";
        else if (ContainsAny(notes, "变焦拉远", "zoom out")) motion = "Zoom Out";
        else if (ContainsAny(notes, "变焦拉近", "变焦推进", "zoom in")) motion = "Zoom In";
        else if (ContainsAny(notes, "向后拉远", "往后拉", "拉远", "dolly out", "pull out")) motion = "Pull Out";
        else if (ContainsAny(notes, "推进", "推近", "push in", "dolly in")) motion = "Push In";
        else if (ContainsAny(notes, "拉近")) motion = "Zoom In";
        else if (ContainsAny(notes, "横移左", "左横移", "truck left")) motion = "Truck Left";
        else if (ContainsAny(notes, "横移右", "右横移", "truck right", "横移")) motion = "Truck Right";
        else if (ContainsAny(notes, "剧烈晃", "大晃", "shake strongly")) motion = "Shake Strongly";
        else if (ContainsAny(notes, "轻晃", "晃动", "shake")) motion = "Shake Slightly";
        else if (ContainsAny(notes, "逆时针滚", "roll counter")) motion = "Roll Counterclockwise";
        else if (ContainsAny(notes, "滚转", "顺时针滚", "roll clockwise")) motion = "Roll Clockwise";
        else if (ContainsAny(notes, "降下", "降镜头", "pedestal down")) motion = "Pedestal Down";
        else if (ContainsAny(notes, "左摇", "摇左", "pan left")) motion = "Pan Left";
        else if (ContainsAny(notes, "右摇", "摇右", "pan right")) motion = "Pan Right";
        else if (ContainsAny(notes, "俯视", "俯拍", "tilt down")) motion = "Tilt Down";
        else if (ContainsAny(notes, "仰视", "仰拍", "tilt up")) motion = "Tilt Up";
        else if (ContainsAny(notes, "升起", "升镜头", "pedestal up")) motion = "Pedestal Up";
        else if (ContainsAny(notes, "固定机位", "固定镜头", "静止", "static")) motion = "Static Shot";

        var slow = ContainsAny(notes, "缓慢", "缓缓", "慢慢", "slow");
        var fast = ContainsAny(notes, "快速", "迅速", "fast");
        var speed = slow ? " at slow speed" : fast ? " at fast speed" : "";

        string? framing = null;
        if (ContainsAny(notes, "面部特写", "脸部特写", "脸部", "面部") && ContainsAny(notes, "特写"))
            framing = "a close-up of the face";
        else if (ContainsAny(notes, "特写", "close-up", "close up"))
            framing = "a close-up";
        else if (ContainsAny(notes, "中景", "medium shot"))
            framing = "a medium shot";
        else if (ContainsAny(notes, "全景", "全身", "wide shot"))
            framing = "a wide shot";

        if (motion is null && framing is null) return null;

        var amplitude = "";
        if (motion is not null and not "Static Shot")
        {
            if (ContainsAny(notes, "小幅度", "小范围", "轻微幅度", "with small amplitude"))
                amplitude = " with small amplitude";
            else if (ContainsAny(notes, "大幅度", "大范围", "大幅", "with large amplitude"))
                amplitude = " with large amplitude";
        }

        if (motion == "Static Shot")
            return framing is null
                ? $"The camera holds a static shot{speed}."
                : $"The camera holds a static shot{speed} on {framing}.";
        if (motion is null)
            return $"The shot is {framing}.";

        var verb = motion switch
        {
            "Pull Out" => "pulls out",
            "Push In" => "pushes in",
            "Zoom In" => "zooms in",
            "Zoom Out" => "zooms out",
            "Pan Left" => "pans left",
            "Pan Right" => "pans right",
            "Tilt Down" => "tilts down",
            "Tilt Up" => "tilts up",
            "Pedestal Up" => "pedestals up",
            "Pedestal Down" => "pedestals down",
            "Truck Left" => "trucks left",
            "Truck Right" => "trucks right",
            "Shake Slightly" => "shakes slightly",
            "Shake Strongly" => "shakes strongly",
            "Roll Clockwise" => "rolls clockwise",
            "Roll Counterclockwise" => "rolls counterclockwise",
            "Arc Shot" => "arcs around the subject",
            "Tracking Shot" => "tracks the subject",
            _ => motion.ToLowerInvariant(),
        };
        if (framing is null)
            return $"The camera {verb}{amplitude}{speed}.";
        var relation = motion is "Pull Out" or "Zoom Out" ? "from" : "toward";
        if (motion.StartsWith("Shake", StringComparison.Ordinal) || motion.StartsWith("Roll", StringComparison.Ordinal))
            return $"The camera {verb}{amplitude}{speed} on {framing}.";
        return $"The camera {verb}{amplitude}{speed} {relation} {framing}.";
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeNewlines(string? text)
        => (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Newline(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\r') ? "\r"
            : "\n";

    private static ParsedPrompt? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var preamble = new StringBuilder();
        string? current = null;
        var body = new StringBuilder();
        var sawField = false;
        var trailer = new StringBuilder();
        var inTrailer = false;

        void Flush()
        {
            if (current is null) return;
            blocks[current] = body.ToString().TrimEnd();
            body.Clear();
        }

        foreach (var raw in lines)
        {
            var match = FieldHeader.Match(raw);
            if (match.Success)
            {
                Flush();
                current = match.Groups[1].Value.ToLowerInvariant();
                if (!order.Contains(current, StringComparer.OrdinalIgnoreCase))
                    order.Add(current);
                body.Append(match.Groups[2].Value);
                sawField = true;
                inTrailer = false;
                continue;
            }

            if (!sawField)
            {
                if (preamble.Length > 0) preamble.Append('\n');
                preamble.Append(raw);
                continue;
            }

            if (current is "non_diegetic_music" or "overall_soundscape"
                && raw.Length > 0
                && !FieldHeader.IsMatch(raw)
                && current is "non_diegetic_music")
            {
                Flush();
                current = null;
                inTrailer = true;
            }

            if (inTrailer || current is null)
            {
                if (trailer.Length > 0) trailer.Append('\n');
                trailer.Append(raw);
                continue;
            }

            if (body.Length > 0) body.Append('\n');
            body.Append(raw);
        }

        Flush();
        if (!sawField) return null;
        return new ParsedPrompt(preamble.ToString(), blocks, order, trailer.ToString());
    }

    private static string Render(ParsedPrompt parsed, string nl)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(parsed.Preamble))
            parts.Add(NormalizeNewlines(parsed.Preamble).Replace("\n", nl).TrimEnd());
        foreach (var key in parsed.Order)
        {
            if (!parsed.Blocks.TryGetValue(key, out var value)) continue;
            var header = key + ":";
            var normalized = NormalizeNewlines(value);
            parts.Add(normalized.Length == 0
                ? header
                : header + (normalized.Contains('\n') ? nl + normalized.Replace("\n", nl) : " " + normalized));
        }
        if (!string.IsNullOrWhiteSpace(parsed.Trailer))
            parts.Add(NormalizeNewlines(parsed.Trailer).Replace("\n", nl).TrimEnd());
        return string.Join(nl + nl, parts).TrimEnd() + nl;
    }

    private static void InsertBefore(List<string> order, string key, string before)
    {
        var index = order.FindIndex(item => string.Equals(item, before, StringComparison.OrdinalIgnoreCase));
        if (index < 0) order.Add(key);
        else order.Insert(index, key);
    }

    private sealed class ParsedPrompt
    {
        public ParsedPrompt(
            string preamble,
            Dictionary<string, string> blocks,
            List<string> order,
            string trailer)
        {
            Preamble = preamble;
            Blocks = blocks;
            Order = order;
            Trailer = trailer;
        }

        public string Preamble { get; set; }
        public Dictionary<string, string> Blocks { get; }
        public List<string> Order { get; }
        public string Trailer { get; set; }
    }
}
