namespace QwenLocalChat;

// Keep the named type as a stable seam for the form and tests, but leave text
// painting, selection, IME, keyboard input, and scrolling to RichTextBox.
internal sealed class ObsidianTranscriptBox : RichTextBox
{
    private const int EmLineScroll = 0x00B6;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (IsHandleCreated && e.Delta != 0)
        {
            var detents = e.Delta / SystemInformation.MouseWheelScrollDelta;
            var configuredLines = SystemInformation.MouseWheelScrollLines;
            var linesPerDetent = configuredLines < 0
                ? Math.Max(1, ClientSize.Height / Math.Max(1, Font.Height) - 1)
                : Math.Max(1, configuredLines);
            _ = SendMessage(Handle, EmLineScroll, IntPtr.Zero, (IntPtr)(-detents * linesPerDetent));
        }

        base.OnMouseWheel(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);
}
