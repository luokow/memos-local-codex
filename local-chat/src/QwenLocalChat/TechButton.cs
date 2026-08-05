using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace QwenLocalChat;

internal sealed class TechButton : Control, IButtonControl
{
    private bool _hovered;
    private bool _pressed;
    private bool _isDefault;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; init; }

    [DefaultValue(DialogResult.None)]
    public DialogResult DialogResult { get; set; }

    public TechButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.StandardClick |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        ForeColor = ThemePalette.Ink;
        Cursor = Cursors.Hand;
        Font = ThemePalette.Ui(9F, FontStyle.Bold);
        MinimumSize = new Size(96, 40);
        Padding = new Padding(10, 0, 10, 0);
        Margin = new Padding(8, 2, 0, 2);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new TechButtonAccessibleObject(this);

    public void NotifyDefault(bool value)
    {
        if (_isDefault == value) return;
        _isDefault = value;
        Invalidate();
    }

    public void PerformClick()
    {
        if (!Enabled || !Visible) return;
        OnClick(EventArgs.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var measured = TextRenderer.MeasureText(Text ?? string.Empty, Font, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(
            Math.Max(MinimumSize.Width, measured.Width + Padding.Horizontal + 20),
            Math.Max(MinimumSize.Height, measured.Height + Padding.Vertical + 16));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (string.IsNullOrWhiteSpace(AccessibleName)) AccessibleName = Text;
        PerformLayout();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = true;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Invalidate();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            var shouldClick = _pressed;
            _pressed = false;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Invalidate();
            if (shouldClick) PerformClick();
            return;
        }
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = UiDrawing.ScaleForDpi(DeviceDpi);
        UiDrawing.DrawTechButton(e.Graphics, ClientRectangle, Text, Font, Primary, Enabled, _hovered, _pressed, _isDefault, Focused && ShowFocusCues, scale);
    }

    private sealed class TechButtonAccessibleObject(TechButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override string? Name { get => owner.AccessibleName ?? owner.Text; set => owner.AccessibleName = value; }
        public override string? DefaultAction => "按下";
        public override AccessibleStates State => base.State | (owner._isDefault ? AccessibleStates.Default : AccessibleStates.None);
        public override void DoDefaultAction() => owner.PerformClick();
    }
}
