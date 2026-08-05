namespace QwenLocalChat;

// A short-lived opaque snapshot covers the restored client area while the
// real form performs one complete layout/paint pass. It is only used for the
// minimize/restore transition; normal scrolling never touches this window.
internal sealed class RestoreSnapshotOverlay : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private Form? _owner;

    public RestoreSnapshotOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = ThemePalette.Canvas;
        BackgroundImageLayout = ImageLayout.Stretch;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public void ShowSnapshot(Form owner, Bitmap snapshot)
    {
        if (IsDisposed || owner.IsDisposed || snapshot.Width <= 0 || snapshot.Height <= 0) return;
        _owner = owner;
        Owner = owner;
        BackgroundImage?.Dispose();
        BackgroundImage = new Bitmap(snapshot);
        var screenLocation = owner.PointToScreen(Point.Empty);
        SetBounds(screenLocation.X, screenLocation.Y, owner.ClientSize.Width, owner.ClientSize.Height, BoundsSpecified.All);
        if (!Visible) Show(owner);
        BringToFront();
    }

    public void HideSnapshot()
    {
        if (Visible) Hide();
        BackgroundImage?.Dispose();
        BackgroundImage = null;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = (IntPtr)HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HideSnapshot();
            _owner = null;
        }

        base.Dispose(disposing);
    }
}
