namespace QwenLocalChat.Core;

public sealed record VideoPromptMention(string Tag, string Kind, int Index, string FileName = "")
{
    public string Label => Kind switch
    {
        "Picture" => $"图{Index}",
        "Video" => $"视频{Index}",
        "Audio" => $"音频{Index}",
        _ => $"{Kind} {Index}",
    };

    public string Display => string.IsNullOrWhiteSpace(FileName)
        ? $"{Label}  {Tag}"
        : $"{Label}  {FileName}  {Tag}";
}

/// <summary>
/// `@` completion for MiniMax H3 prompt tags (`&lt;Picture N&gt;`, `&lt;Video N&gt;`, `&lt;Audio N&gt;`).
/// The menu lists chip order plus file name; inserting still writes the official tag.
/// </summary>
public static class VideoPromptMentions
{
    public static IReadOnlyList<VideoPromptMention> Candidates(
        VideoConditioningMode mode,
        int imageCount,
        int videoCount,
        int audioCount)
        => Candidates(mode, DummyPaths(imageCount), DummyPaths(videoCount), DummyPaths(audioCount));

    public static IReadOnlyList<VideoPromptMention> Candidates(
        VideoConditioningMode mode,
        IReadOnlyList<string>? imagePaths,
        IReadOnlyList<string>? videoPaths,
        IReadOnlyList<string>? audioPaths)
    {
        if (mode is VideoConditioningMode.Text) return [];
        if (!VideoMediaCapabilities.IsReferenceFamily(mode)
            && !VideoMediaCapabilities.IsKeyframeFamily(mode))
            return [];
        var items = new List<VideoPromptMention>();
        items.AddRange(Build("Picture", imagePaths, includeDummyWhenEmpty: true));
        items.AddRange(Build("Video", videoPaths, includeDummyWhenEmpty: false));
        items.AddRange(Build("Audio", audioPaths, includeDummyWhenEmpty: false));
        if (items.Count == 0)
            items.AddRange(Build("Picture", imagePaths, includeDummyWhenEmpty: true));
        return items;
    }

    public static bool TryGetActiveQuery(string? text, int caret, out int atIndex, out string query)
    {
        atIndex = -1;
        query = "";
        if (string.IsNullOrEmpty(text) || caret < 1 || caret > text.Length) return false;

        for (var i = caret - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '@')
            {
                if (i > 0 && !char.IsWhiteSpace(text[i - 1])) return false;
                query = text[(i + 1)..caret];
                if (query.Length > 0 && query.Any(char.IsWhiteSpace)) return false;
                atIndex = i;
                return true;
            }
            if (char.IsWhiteSpace(ch)) return false;
        }

        return false;
    }

    public static IReadOnlyList<VideoPromptMention> Filter(IReadOnlyList<VideoPromptMention> candidates, string? query)
    {
        var items = candidates ?? [];
        if (string.IsNullOrWhiteSpace(query)) return items.ToArray();
        var needle = query.Trim();
        return items
            .Where(item => Matches(item, needle))
            .ToArray();
    }

    public static string Insert(string text, int atIndex, int caret, string tag, out int newCaret)
    {
        text ??= "";
        if (atIndex < 0 || atIndex > text.Length) throw new ArgumentOutOfRangeException(nameof(atIndex));
        if (caret < atIndex || caret > text.Length) throw new ArgumentOutOfRangeException(nameof(caret));

        var prefix = text[..atIndex];
        var suffix = caret < text.Length ? text[caret..] : "";
        var insertion = tag ?? "";
        if (suffix.Length == 0 || !char.IsWhiteSpace(suffix[0]))
            insertion += " ";
        var next = prefix + insertion + suffix;
        newCaret = prefix.Length + insertion.Length;
        return next;
    }

    private static bool Matches(VideoPromptMention item, string needle)
    {
        if (item.Tag.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Kind.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Index.ToString().Contains(needle, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(item.FileName)
            && item.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        return !string.IsNullOrWhiteSpace(stem)
               && stem.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<VideoPromptMention> Build(
        string kind,
        IReadOnlyList<string>? paths,
        bool includeDummyWhenEmpty = true)
    {
        var files = (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (files.Length == 0)
            return includeDummyWhenEmpty ? [new VideoPromptMention($"<{kind} 1>", kind, 1, "")] : [];
        return files
            .Select((path, offset) => new VideoPromptMention(
                $"<{kind} {offset + 1}>",
                kind,
                offset + 1,
                string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path)))
            .ToArray();
    }

    private static IReadOnlyList<string> DummyPaths(int count)
        => Enumerable.Range(1, Math.Max(0, count)).Select(i => $"slot-{i}").ToArray();
}
