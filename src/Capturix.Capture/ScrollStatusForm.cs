using Capturix.Core.Interop;

namespace Capturix.Capture;

/// <summary>
/// Small "scrolling capture in progress" banner. It never takes focus (the wheel must keep
/// going to the target window) and is excluded from screen capture, so it can sit anywhere.
/// </summary>
internal sealed class ScrollStatusForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private readonly Label _label;

    public ScrollStatusForm(Rectangle target)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(32, 32, 32);
        Padding = new Padding(14, 10, 14, 10);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _label = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            Text = "Capturando con desplazamiento…   Esc para terminar",
        };
        Controls.Add(_label);

        // Bottom centre of the monitor that holds the target area.
        var screen = Screen.FromRectangle(target).WorkingArea;
        Load += (_, _) => Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Bottom - Height - 24);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public void SetStatus(string text) => _label.Text = $"Capturando con desplazamiento…   {text}   ·   Esc para terminar";
}
