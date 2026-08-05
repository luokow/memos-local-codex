using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace QwenLocalChat;

internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hovered;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = true;
        Height = 36;
        BackColor = Color.Transparent;
        ForeColor = ThemePalette.Secondary;
        Font = ThemePalette.Ui(8.75F);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        UpdatePreferredWidth();
    }

    [DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        UpdatePreferredWidth();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdatePreferredWidth();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdatePreferredWidth();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnClick(EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = UiDrawing.ScaleForDpi(DeviceDpi);
        UiDrawing.DrawToggle(e.Graphics, ClientRectangle, Text, Font, ForeColor, Checked, Enabled, _hovered, Focused && ShowFocusCues, scale);
    }

    private void UpdatePreferredWidth()
    {
        var textWidth = TextRenderer.MeasureText(Text ?? string.Empty, Font).Width;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        Width = Math.Max((int)Math.Ceiling(104f * scale), (int)Math.Ceiling(58f * scale) + textWidth);
    }

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override AccessibleStates State => base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
        public override string? Name { get => owner.AccessibleName ?? owner.Text; set => owner.AccessibleName = value; }
        public override string? DefaultAction => "切换";
        public override void DoDefaultAction() => owner.OnClick(EventArgs.Empty);
    }
}
