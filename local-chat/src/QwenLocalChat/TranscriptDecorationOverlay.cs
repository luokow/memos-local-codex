using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QwenLocalChat;

// An owned, non-activating layered window keeps the approved decoration out of
// RichTextBox's scroll buffer while remaining visually anchored to its shell.
internal sealed class TranscriptDecorationOverlay : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private readonly Control _anchor;
    private Bitmap? _layer;
    private Form? _owner;
    private bool _syncing;
    private bool _ownerEventsBound;
    private bool _syncSuspended;

    public TranscriptDecorationOverlay(Control anchor)
    {
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _anchor.HandleCreated += AnchorChanged;
        _anchor.SizeChanged += AnchorChanged;
        _anchor.LocationChanged += AnchorChanged;
        _anchor.VisibleChanged += AnchorChanged;
        _anchor.Disposed += AnchorDisposed;
    }

    protected override bool ShowWithoutActivation => true;

    internal int OwnerEventBindingsForTesting => _ownerEventsBound ? 1 : 0;

    internal void SuspendSynchronization(bool suspended)
    {
        if (IsDisposed) return;
        _syncSuspended = suspended;
        if (suspended)
        {
            if (Visible) Hide();
            return;
        }

        Synchronize();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public void ShowFor(Form owner)
    {
        if (IsDisposed) return;

        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (ReferenceEquals(_owner, owner) && _ownerEventsBound)
        {
            // Re-showing the form must refresh the existing layer, not attach a
            // second set of owner callbacks that can repaint the same decoration.
            Synchronize();
            return;
        }

        UnbindOwnerEvents();
        _owner = owner;
        Owner = owner;
        owner.Move += OwnerChanged;
        owner.Resize += OwnerChanged;
        owner.VisibleChanged += OwnerChanged;
        owner.FormClosed += OwnerClosed;
        _ownerEventsBound = true;
        if (!IsHandleCreated) CreateControl();
        Synchronize();
    }

    public void Synchronize()
    {
        if (IsDisposed || _syncing || _owner is null || !_anchor.IsHandleCreated) return;
        _syncing = true;
        try
        {
            if (_syncSuspended || _owner.WindowState == FormWindowState.Minimized || !_owner.Visible || !_anchor.Visible || _anchor.ClientSize.Width <= 0 || _anchor.ClientSize.Height <= 0)
            {
                if (Visible) Hide();
                return;
            }

            var screenLocation = _anchor.PointToScreen(Point.Empty);
            var targetSize = _anchor.ClientSize;
            SetBounds(screenLocation.X, screenLocation.Y, targetSize.Width, targetSize.Height, BoundsSpecified.All);
            RebuildLayer(targetSize);
            if (!Visible) Show(_owner);
            ApplyLayeredBitmap();
        }
        finally
        {
            _syncing = false;
        }
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
            UnbindOwnerEvents();

            _anchor.HandleCreated -= AnchorChanged;
            _anchor.SizeChanged -= AnchorChanged;
            _anchor.LocationChanged -= AnchorChanged;
            _anchor.VisibleChanged -= AnchorChanged;
            _anchor.Disposed -= AnchorDisposed;
            _layer?.Dispose();
            _layer = null;
        }

        base.Dispose(disposing);
    }

    private void AnchorChanged(object? sender, EventArgs e) => Synchronize();

    private void OwnerChanged(object? sender, EventArgs e)
    {
        if (_owner?.WindowState == FormWindowState.Minimized) Hide();
        else Synchronize();
    }

    private void OwnerClosed(object? sender, FormClosedEventArgs e) => Dispose();

    private void AnchorDisposed(object? sender, EventArgs e) => Dispose();

    private void UnbindOwnerEvents()
    {
        if (!_ownerEventsBound || _owner is null) return;
        _owner.Move -= OwnerChanged;
        _owner.Resize -= OwnerChanged;
        _owner.VisibleChanged -= OwnerChanged;
        _owner.FormClosed -= OwnerClosed;
        _ownerEventsBound = false;
    }

    private void RebuildLayer(Size size)
    {
        if (_layer is not null && _layer.Size == size) return;
        _layer?.Dispose();
        using var source = UiDrawing.CreateTranscriptDecoration(size);
        _layer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(_layer);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(Color.Transparent);
        graphics.DrawImageUnscaled(source, Point.Empty);
    }

    private void ApplyLayeredBitmap()
    {
        if (!IsHandleCreated || _layer is null || _layer.Width <= 0 || _layer.Height <= 0) return;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return;
        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        var bitmapHandle = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            bitmapHandle = _layer.GetHbitmap(Color.FromArgb(0));
            previousObject = SelectObject(memoryDc, bitmapHandle);
            var destination = new Point(Left, Top);
            var size = new Size(_layer.Width, _layer.Height);
            var source = Point.Empty;
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };
            _ = UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previousObject != IntPtr.Zero) _ = SelectObject(memoryDc, previousObject);
            if (bitmapHandle != IntPtr.Zero) _ = DeleteObject(bitmapHandle);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr windowHandle,
        IntPtr destinationDevice,
        ref Point destinationPoint,
        ref Size size,
        IntPtr sourceDevice,
        ref Point sourcePoint,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicObject);
}
