using System.Drawing.Drawing2D;
using Capturix.Core.Capture;

namespace Capturix.Capture;

/// <summary>
/// Full-desktop overlay over a frozen screenshot. Hovering highlights the window under the
/// cursor (click to take it); dragging selects a free region. Esc or right-click cancels.
/// All coordinates are client pixels, which equal bitmap pixels because the form covers
/// the virtual screen exactly and the process is Per-Monitor-V2 DPI aware.
/// </summary>
internal sealed class RegionSelectorForm : Form
{
    private const int DragThreshold = 4;
    private const int MagnifierSource = 15;   // source pixels per side (odd, so the cursor pixel is centred)
    private const int MagnifierZoom = 8;
    private const int MagnifierSize = MagnifierSource * MagnifierZoom;
    private const int InfoHeight = 40;

    private static readonly Color Accent = Color.FromArgb(255, 0, 153, 255);

    private readonly Bitmap _screenshot;
    private readonly Bitmap _dimmed;
    private readonly Rectangle _virtualBounds;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly Font _infoFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Pen _accentPen = new(Accent, 2f);

    private Point _cursor;
    private Point _dragStart;
    private bool _mouseDown;
    private bool _dragging;
    private Rectangle _hoverWindow;       // client coordinates
    private Rectangle _lastDirty;

    /// <summary>The selected rectangle in screen coordinates, valid when DialogResult is OK.</summary>
    public Rectangle SelectedScreenRect { get; private set; }

    public RegionSelectorForm(Bitmap screenshot, Rectangle virtualBounds, IReadOnlyList<WindowInfo> windows)
    {
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;
        _windows = windows;
        _dimmed = CreateDimmed(screenshot);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = virtualBounds;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private static Bitmap CreateDimmed(Bitmap source)
    {
        var dimmed = new Bitmap(source);
        using var g = Graphics.FromImage(dimmed);
        using var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        g.FillRectangle(shade, 0, 0, dimmed.Width, dimmed.Height);
        return dimmed;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Re-assert bounds: moving between monitors with different DPI can rescale the form.
        Bounds = _virtualBounds;
        Activate();
        _cursor = PointToClient(Cursor.Position);
        UpdateHover();
        Invalidate();
    }

    private Rectangle CurrentSelection =>
        _dragging ? NormalizedRect(_dragStart, _cursor) : _hoverWindow;

    private static Rectangle NormalizedRect(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);

    private void UpdateHover()
    {
        var screenPoint = new Point(_cursor.X + _virtualBounds.X, _cursor.Y + _virtualBounds.Y);
        var hit = WindowEnumerator.HitTest(_windows, screenPoint);
        var rect = hit is null ? Rectangle.Empty : hit.Bounds;
        rect.Offset(-_virtualBounds.X, -_virtualBounds.Y);
        rect.Intersect(ClientRectangle);
        _hoverWindow = rect;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            _mouseDown = true;
            _dragStart = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursor = e.Location;
        if (_mouseDown && !_dragging &&
            (Math.Abs(e.X - _dragStart.X) > DragThreshold || Math.Abs(e.Y - _dragStart.Y) > DragThreshold))
        {
            _dragging = true;
        }
        if (!_dragging)
            UpdateHover();
        InvalidateChanged();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_mouseDown)
            return;
        _mouseDown = false;

        var selection = CurrentSelection;
        if (selection.Width >= 2 && selection.Height >= 2)
            Accept(selection);
        else
            _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Cancel();
                break;
            case Keys.Enter:
            case Keys.Space:
                // Keyboard accept: take the window under the cursor, or the whole desktop.
                Accept(_hoverWindow.IsEmpty ? ClientRectangle : _hoverWindow);
                break;
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
                NudgeCursor(e.KeyCode, e.Shift ? 10 : 1);
                break;
        }
    }

    /// <summary>Arrow keys move the mouse pixel by pixel for precise selection edges.</summary>
    private void NudgeCursor(Keys key, int step)
    {
        var p = Cursor.Position;
        p.X += key == Keys.Left ? -step : key == Keys.Right ? step : 0;
        p.Y += key == Keys.Up ? -step : key == Keys.Down ? step : 0;
        Cursor.Position = p;
    }

    private void Accept(Rectangle clientRect)
    {
        clientRect.Intersect(ClientRectangle);
        clientRect.Offset(_virtualBounds.X, _virtualBounds.Y);
        SelectedScreenRect = clientRect;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // ---- painting -------------------------------------------------------------------------

    /// <summary>Repaints only the area touched by the old and new selection/magnifier.</summary>
    private void InvalidateChanged()
    {
        var dirty = Rectangle.Union(Inflate(CurrentSelection, 4 + InfoHeight), MagnifierBounds());
        dirty = Rectangle.Union(dirty, SizeLabelBounds(CurrentSelection));
        Invalidate(Rectangle.Union(dirty, _lastDirty));
        _lastDirty = dirty;
    }

    private static Rectangle Inflate(Rectangle r, int by)
    {
        if (r.IsEmpty) return r;
        r.Inflate(by, by);
        return r;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is painted in OnPaint; skipping the background avoids flicker.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);

        var selection = CurrentSelection;
        if (!selection.IsEmpty)
        {
            var visible = Rectangle.Intersect(selection, clip);
            if (!visible.IsEmpty)
                g.DrawImage(_screenshot, visible, visible, GraphicsUnit.Pixel);
            g.DrawRectangle(_accentPen, selection.X, selection.Y, selection.Width - 1, selection.Height - 1);
            DrawSizeLabel(g, selection);
        }

        if (_dragging || selection.IsEmpty)
            DrawCrosshair(g);

        DrawMagnifier(g);
    }

    private void DrawCrosshair(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(160, Accent), 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, _cursor.X, 0, _cursor.X, ClientSize.Height);
        g.DrawLine(pen, 0, _cursor.Y, ClientSize.Width, _cursor.Y);
    }

    private Rectangle SizeLabelBounds(Rectangle selection)
    {
        if (selection.IsEmpty) return Rectangle.Empty;
        int y = selection.Y - 26 >= 0 ? selection.Y - 26 : selection.Y + 4;
        return new Rectangle(selection.X, y, 140, 24);
    }

    private void DrawSizeLabel(Graphics g, Rectangle selection)
    {
        var box = SizeLabelBounds(selection);
        string text = $"{selection.Width} × {selection.Height}";
        using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        var size = g.MeasureString(text, _infoFont);
        g.FillRectangle(bg, box.X, box.Y, size.Width + 12, box.Height);
        g.DrawString(text, _infoFont, Brushes.White, box.X + 6, box.Y + 4);
    }

    private Rectangle MagnifierBounds()
    {
        const int offset = 24;
        int w = MagnifierSize, h = MagnifierSize + InfoHeight;
        int x = _cursor.X + offset, y = _cursor.Y + offset;
        if (x + w > ClientSize.Width) x = _cursor.X - offset - w;
        if (y + h > ClientSize.Height) y = _cursor.Y - offset - h;
        return new Rectangle(x, y, w + 2, h + 2);
    }

    private void DrawMagnifier(Graphics g)
    {
        var box = MagnifierBounds();
        var zoomRect = new Rectangle(box.X, box.Y, MagnifierSize, MagnifierSize);
        int half = MagnifierSource / 2;
        var source = new Rectangle(_cursor.X - half, _cursor.Y - half, MagnifierSource, MagnifierSource);

        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.FillRectangle(Brushes.Black, zoomRect);
        g.DrawImage(_screenshot, zoomRect, source, GraphicsUnit.Pixel);
        g.Restore(state);

        // Outline of the pixel under the cursor.
        using (var cellPen = new Pen(Color.White, 1f))
            g.DrawRectangle(cellPen, zoomRect.X + half * MagnifierZoom, zoomRect.Y + half * MagnifierZoom, MagnifierZoom, MagnifierZoom);
        g.DrawRectangle(_accentPen, zoomRect);

        var color = SamplePixel(_cursor);
        var screenX = _cursor.X + _virtualBounds.X;
        var screenY = _cursor.Y + _virtualBounds.Y;
        var infoRect = new Rectangle(box.X, zoomRect.Bottom, MagnifierSize, InfoHeight);
        using var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
        g.FillRectangle(bg, infoRect);
        using (var swatch = new SolidBrush(color))
            g.FillRectangle(swatch, infoRect.X + 6, infoRect.Y + 8, 14, 14);
        g.DrawString($"({screenX}, {screenY})\n#{color.R:X2}{color.G:X2}{color.B:X2}",
            _infoFont, Brushes.White, infoRect.X + 26, infoRect.Y + 2);
    }

    private Color SamplePixel(Point p) =>
        p.X >= 0 && p.Y >= 0 && p.X < _screenshot.Width && p.Y < _screenshot.Height
            ? _screenshot.GetPixel(p.X, p.Y)
            : Color.Black;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimmed.Dispose();
            _infoFont.Dispose();
            _accentPen.Dispose();
        }
        base.Dispose(disposing);
    }
}
