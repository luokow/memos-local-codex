namespace QwenLocalChat;

internal sealed class StatusPill : Label
{
    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        BackColor = Color.Transparent;
        Padding = new Padding(10, 6, 10, 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = UiDrawing.ScaleForDpi(DeviceDpi);
        UiDrawing.DrawStatusPill(e.Graphics, ClientRectangle, Text, Font, ForeColor, scale);
    }

}
