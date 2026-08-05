using System.Runtime.InteropServices;

namespace QwenLocalChat.Core;

public static class ClipboardWritePolicy
{
    public const int CannotOpenClipboardHResult = unchecked((int)0x800401D0);

    public static async Task<bool> TryWriteAsync(
        Action write,
        Func<TimeSpan, Task>? delay = null,
        int maxAttempts = 5)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        delay ??= Task.Delay;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                write();
                return true;
            }
            catch (COMException error) when (error.HResult == CannotOpenClipboardHResult)
            {
                if (attempt == maxAttempts) return false;
                await delay(TimeSpan.FromMilliseconds(40 * attempt));
            }
        }

        return false;
    }
}
