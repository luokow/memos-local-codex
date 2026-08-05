using System.ComponentModel;

namespace QwenLocalChat;

internal sealed class RoundedPanel : Panel
{
    private int _cornerRadius = 16;
    private Color _fillColor = ThemePalette.Surface;
    private Color _borderColor = ThemePalette.Border;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.Transparent;
        Padding = new Padding(1);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = Math.Max(2, value); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor
    {
        get => _fillColor;
        set { _fillColor = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor
    {
        get => _borderColor;
        set { _borderColor = value; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var scale = UiDrawing.ScaleForDpi(DeviceDpi);
        UiDrawing.Prepare(e.Graphics);
        UiDrawing.FillRounded(e.Graphics, UiDrawing.ClientBounds(ClientSize, scale), CornerRadius * scale, FillColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = UiDrawing.ScaleForDpi(DeviceDpi);
        UiDrawing.Prepare(e.Graphics);
        UiDrawing.DrawRoundedBorder(e.Graphics, UiDrawing.ClientBounds(ClientSize, scale), CornerRadius * scale, BorderColor, scale);
    }
}
