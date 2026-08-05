namespace QwenLocalChat;

internal enum ThreeWayChoice { First, Second, Cancel }

internal sealed class ChoiceDialog : Form
{
    private ThreeWayChoice _choice = ThreeWayChoice.Cancel;

    private ChoiceDialog(string title, string message, string first, string second)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 170);
        Font = ThemePalette.Ui(9F);
        BackColor = ThemePalette.Surface;
        ForeColor = ThemePalette.Ink;

        var label = new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 20, 22, 8),
            AutoSize = false,
            ForeColor = ThemePalette.Ink,
            BackColor = Color.Transparent,
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            BackColor = ThemePalette.Canvas,
        };
        buttons.Controls.Add(MakeButton("取消", ThreeWayChoice.Cancel));
        buttons.Controls.Add(MakeButton(second, ThreeWayChoice.Second));
        buttons.Controls.Add(MakeButton(first, ThreeWayChoice.First));
        Controls.Add(label);
        Controls.Add(buttons);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.ApplyDarkMode(Handle);
    }

    private TechButton MakeButton(string text, ThreeWayChoice choice)
    {
        var button = new TechButton { Text = text, AutoSize = true, MinimumSize = new Size(105, 38), Margin = new Padding(6, 0, 0, 0) };
        button.Click += (_, _) => { _choice = choice; DialogResult = DialogResult.OK; Close(); };
        return button;
    }

    public static ThreeWayChoice ShowDialog(IWin32Window owner, string title, string message, string first, string second)
    {
        using var dialog = new ChoiceDialog(title, message, first, second);
        dialog.ShowDialog(owner);
        return dialog._choice;
    }
}
