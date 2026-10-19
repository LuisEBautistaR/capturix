using System.Drawing.Drawing2D;
using Capturix.Core.Interop;

namespace Capturix.Capture;

/// <summary>Red dashed outline around the recorded area. Click-through and invisible to capture.</summary>
internal sealed class RecordingFrameForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int OutlineGap = 4; // the outline sits just outside the recorded pixels

    public RecordingFrameForm(Rectangle region)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta; // everything but the outline is see-through
        var bounds = region;
        bounds.Inflate(OutlineGap, OutlineGap);
        Bounds = bounds;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(230, 30, 30), 2f) { DashStyle = DashStyle.Dash };
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }
}

/// <summary>Countdown, elapsed time, pause/resume and stop. Invisible to capture.</summary>
internal sealed class RecordingControlBar : Form
{
    private readonly Label _status;
    private readonly Button _pause;
    private readonly Button _stop;

    public event EventHandler? StopClicked;
    public event EventHandler? PauseClicked;

    public RecordingControlBar(Rectangle region, string formatName)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Padding = new Padding(10, 6, 6, 6);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = BackColor, Margin = Padding.Empty };
        _status = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0, 6, 12, 0),
            Text = $"{formatName} · preparando…",
        };
        _pause = MakeButton("Pausa");
        _stop = MakeButton("■ Detener");
        _pause.Click += (_, _) => PauseClicked?.Invoke(this, EventArgs.Empty);
        _stop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        layout.Controls.AddRange(new Control[] { _status, _pause, _stop });
        Controls.Add(layout);

        Load += (_, _) => Location = PlaceNear(region, Size);
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.White,
        BackColor = Color.FromArgb(60, 60, 60),
        Font = new Font("Segoe UI", 9.5f),
        Margin = new Padding(4, 2, 4, 2),
        Padding = new Padding(6, 2, 6, 2),
    };

    /// <summary>Below the area if it fits on that screen, otherwise above, otherwise inside at the bottom.</summary>
    private static Point PlaceNear(Rectangle region, Size size)
    {
        var screen = Screen.FromRectangle(region).WorkingArea;
        int x = Math.Clamp(region.Left + (region.Width - size.Width) / 2, screen.Left, screen.Right - size.Width);
        if (region.Bottom + 10 + size.Height <= screen.Bottom)
            return new Point(x, region.Bottom + 10);
        if (region.Top - 10 - size.Height >= screen.Top)
            return new Point(x, region.Top - 10 - size.Height);
        return new Point(x, region.Bottom - size.Height - 16);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public void ShowCountdown(int seconds) => _status.Text = $"Empieza en {seconds}…";

    public void ShowRecording(TimeSpan elapsed, bool paused, string formatName)
    {
        _status.Text = paused ? $"⏸ {formatName} {elapsed:mm\\:ss} (en pausa)" : $"● {formatName} {elapsed:mm\\:ss}";
        _status.ForeColor = paused ? Color.Gold : Color.FromArgb(255, 90, 90);
        _pause.Text = paused ? "Reanudar" : "Pausa";
    }

    public void ShowSaving()
    {
        _status.Text = "Guardando…";
        _pause.Enabled = false;
        _stop.Enabled = false;
    }
}
