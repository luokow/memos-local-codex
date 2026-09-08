using System.Diagnostics;

namespace QwenLocalChat.Core;

public static class WindowsFileReveal
{
    public static ProcessStartInfo CreateExplorerSelectStartInfo(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("视频输出路径必须是绝对路径。", nameof(filePath));
        var fullPath = Path.GetFullPath(filePath);
        return new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{fullPath}\"",
            UseShellExecute = true,
        };
    }

    /// <summary>
    /// Open a folder in Explorer. Prefer this while a video job is still writing files.
    /// </summary>
    public static ProcessStartInfo CreateExplorerOpenDirectoryStartInfo(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Path.IsPathFullyQualified(directoryPath))
            throw new ArgumentException("目录路径必须是绝对路径。", nameof(directoryPath));
        var fullPath = Path.GetFullPath(directoryPath);
        return new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{fullPath}\"",
            UseShellExecute = true,
        };
    }

    /// <summary>
    /// Prefer selecting an existing file; otherwise open its parent or a known output directory.
    /// </summary>
    public static ProcessStartInfo CreateExplorerRevealStartInfo(string? preferredFilePath, string? fallbackDirectory)
    {
        if (!string.IsNullOrWhiteSpace(preferredFilePath)
            && Path.IsPathFullyQualified(preferredFilePath)
            && File.Exists(preferredFilePath))
            return CreateExplorerSelectStartInfo(preferredFilePath);

        if (!string.IsNullOrWhiteSpace(preferredFilePath) && Path.IsPathFullyQualified(preferredFilePath))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(preferredFilePath));
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                return CreateExplorerOpenDirectoryStartInfo(parent);
        }

        if (!string.IsNullOrWhiteSpace(fallbackDirectory)
            && Path.IsPathFullyQualified(fallbackDirectory)
            && Directory.Exists(fallbackDirectory))
            return CreateExplorerOpenDirectoryStartInfo(fallbackDirectory);

        throw new InvalidOperationException("没有可打开的视频文件或输出目录。");
    }
}
