namespace QwenLocalChat;

internal sealed class ObsidianBackdropPanel : Panel
{
    private Bitmap? _backdrop;
    private Size _backdropSize;

    public ObsidianBackdropPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = ThemePalette.Canvas;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_backdrop is null || _backdropSize != ClientSize)
        {
            _backdrop?.Dispose();
            _backdrop = UiDrawing.CreateAppBackdrop(ClientSize);
            _backdropSize = ClientSize;
        }

        e.Graphics.DrawImageUnscaled(_backdrop, ClientRectangle.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backdrop?.Dispose();
            _backdrop = null;
            _backdropSize = Size.Empty;
        }

        base.Dispose(disposing);
    }
}
