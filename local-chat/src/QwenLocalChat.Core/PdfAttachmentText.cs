using UglyToad.PdfPig;

namespace QwenLocalChat.Core;

/// <summary>Turns a PDF attachment into plain text for the text-only chat model.</summary>
public static class PdfAttachmentText
{
    public static bool IsPdf(string? pathOrExtension)
        => string.Equals(ChatAttachmentPolicy.NormalizeExtension(pathOrExtension), ".pdf", StringComparison.Ordinal);

    public static string Extract(string path, int maxChars, out bool truncated)
    {
        truncated = false;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxChars < 1)
            throw new ArgumentOutOfRangeException(nameof(maxChars));

        using var document = PdfDocument.Open(path);
        var builder = new System.Text.StringBuilder();
        foreach (var page in document.GetPages())
        {
            var pageText = page.Text;
            if (string.IsNullOrWhiteSpace(pageText)) continue;
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append("第 ").Append(page.Number).AppendLine(" 页");
            builder.Append(pageText.Trim());
            if (builder.Length >= maxChars) break;
        }

        var text = builder.ToString().Trim();
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
            truncated = true;
        }
        return text;
    }
}
