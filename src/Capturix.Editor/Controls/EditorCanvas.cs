using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Capturix.Editor.Model;

namespace Capturix.Editor.Controls;

public enum EditorTool
{
    Select,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Text,
    Highlight,
    Pixelate,
    Step,
    Pen,
    Crop,
    /// <summary>Drag a zone to read its text (OCR); the image is not changed.</summary>
    Ocr,
    /// <summary>Click to place an emoji stamp; resize it with its handles.</summary>
    Stamp,
}

public sealed class TextEditRequestEventArgs(TextAnnotation annotation, bool isNew) : EventArgs
{
    public TextAnnotation Annotation { get; } = annotation;
    public bool IsNew { get; } = isNew;
}

/// <summary>
/// Draws the document at one canvas unit per image pixel (zoom is applied by the parent's
/// LayoutTransform) and turns mouse input into annotation edits.
/// </summary>
public sealed class EditorCanvas : FrameworkElement
{
    private enum DragMode { None, Create, Move, Handle, Crop }

    private EditorDocument? _document;
    private EditorTool _tool = EditorTool.Arrow;
    private DragMode _dragMode;
    private Point _lastPoint;
    private Annotation? _creating;
    private int _handleIndex;
    private bool _checkpointTaken;
    private Point _cropStart;
    private Rect _cropRect = Rect.Empty;

    public Color CurrentColor { get; set; } = Colors.Red;
    public double CurrentThickness { get; set; } = 4;

    /// <summary>The emoji the Stamp tool places next.</summary>
    public string CurrentEmoji { get; set; } = EmojiRenderer.DefaultStamp;

    /// <summary>New stamps start at this size in image pixels, readable on a typical screenshot.</summary>
    public const double DefaultStampSize = 72;

    /// <summary>Current view zoom; keeps hit tolerances and handles a constant size on screen.</summary>
    public double Zoom { get; set; } = 1;

    public Annotation? Selected { get; private set; }

    public event EventHandler? SelectionChanged;
    public event EventHandler? DocumentEdited;
    public event EventHandler<TextEditRequestEventArgs>? TextEditRequested;
    public event EventHandler<Point>? PointerMoved;
    public event EventHandler<Int32Rect>? OcrZoneSelected;

    public EditorCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        UpdateCursor();
    }

    public EditorDocument? Document
    {
        get => _document;
        set
        {
            if (_document is not null)
                _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document is not null)
                _document.Changed += OnDocumentChanged;
            SetSelected(null);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            _cropRect = Rect.Empty;
            if (value != EditorTool.Select)
                SetSelected(null);
            UpdateCursor();
            InvalidateVisual();
        }
    }

    private double Tolerance => 4 / Zoom;
    private double HandleRadius => 6 / Zoom;

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // Undo/redo replaces annotation objects, so a held selection would be stale.
        SetSelected(null);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void SetSelected(Annotation? annotation)
    {
        if (ReferenceEquals(Selected, annotation))
            return;
        Selected = annotation;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void RaiseEdited()
    {
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void UpdateCursor() =>
        Cursor = _tool switch
        {
            EditorTool.Select => Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };

    protected override Size MeasureOverride(Size availableSize) =>
        _document is null ? new Size(0, 0) : new Size(_document.PixelWidth, _document.PixelHeight);

    // ---- commands used by the window ------------------------------------------------------------

    public void SelectAnnotation(Annotation? annotation) => SetSelected(annotation);

    public void DeleteSelected()
    {
        if (_document is null || Selected is null)
            return;
        _document.Checkpoint();
        _document.Remove(Selected);
        SetSelected(null);
        RaiseEdited();
    }

    public void NudgeSelected(Vector delta)
    {
        if (_document is null || Selected is null)
            return;
        _document.Checkpoint();
        Selected.Move(delta);
        RaiseEdited();
    }

    /// <summary>Applies the toolbar colour/thickness to the selected annotation.</summary>
    /// <summary>Changes the emoji of the selected stamp (one undo step).</summary>
    public void SetSelectedEmoji(string emoji)
    {
        if (_document is null || Selected is not StampAnnotation stamp || stamp.Emoji == emoji)
            return;
        _document.Checkpoint();
        stamp.Emoji = emoji;
        RaiseEdited();
    }

    public void ApplyStyleToSelected(Color? color = null, double? thickness = null)
    {
        if (_document is null || Selected is null)
            return;
        _document.Checkpoint();
        if (color is Color c) Selected.Color = c;
        if (thickness is double t) Selected.Thickness = t;
        RaiseEdited();
    }

    // ---- mouse ---------------------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_document is null)
            return;
        Focus();
        var p = e.GetPosition(this);

        if (e.ClickCount == 2 && _tool == EditorTool.Select && Selected is TextAnnotation text)
        {
            TextEditRequested?.Invoke(this, new TextEditRequestEventArgs(text, isNew: false));
            return;
        }

        switch (_tool)
        {
            case EditorTool.Select:
                if (Selected is TwoPointAnnotation tp && HitHandle(tp, p) is int handle)
                {
                    _dragMode = DragMode.Handle;
                    _handleIndex = handle;
                }
                else
                {
                    var hit = HitTestTop(p);
                    SetSelected(hit);
                    _dragMode = hit is null ? DragMode.None : DragMode.Move;
                }
                _checkpointTaken = false;
                break;

            case EditorTool.Crop:
            case EditorTool.Ocr:
                _dragMode = DragMode.Crop;
                _cropStart = p;
                _cropRect = Rect.Empty;
                break;

            case EditorTool.Step:
                _document.Checkpoint();
                var step = new StepAnnotation
                {
                    Center = p,
                    Number = _document.NextStepNumber++,
                    Color = CurrentColor,
                    Thickness = CurrentThickness,
                };
                _document.Add(step);
                SetSelected(step);
                RaiseEdited();
                return;

            case EditorTool.Stamp:
                _document.Checkpoint();
                var half = new Vector(DefaultStampSize / 2, DefaultStampSize / 2);
                var stamp = new StampAnnotation { Emoji = CurrentEmoji, Start = p - half, End = p + half };
                _document.Add(stamp);
                SetSelected(stamp);
                RaiseEdited();
                return;

            case EditorTool.Text:
                _document.Checkpoint();
                var newText = new TextAnnotation { Position = p, Color = CurrentColor, Thickness = CurrentThickness };
                _document.Add(newText);
                SetSelected(newText);
                TextEditRequested?.Invoke(this, new TextEditRequestEventArgs(newText, isNew: true));
                return;

            case EditorTool.Pen:
                _document.Checkpoint();
                var pen = new PenAnnotation { Color = CurrentColor, Thickness = CurrentThickness };
                pen.Points.Add(p);
                _document.Add(pen);
                _creating = pen;
                _dragMode = DragMode.Create;
                break;

            default:
                _document.Checkpoint();
                var shape = CreateTwoPoint(_tool);
                shape.Start = p;
                shape.End = p;
                shape.Thickness = CurrentThickness;
                shape.Color = CurrentColor;
                _document.Add(shape);
                _creating = shape;
                _dragMode = DragMode.Create;
                break;
        }

        _lastPoint = p;
        if (_dragMode != DragMode.None)
            CaptureMouse();
        InvalidateVisual();
    }

    private static TwoPointAnnotation CreateTwoPoint(EditorTool tool) => tool switch
    {
        EditorTool.Arrow => new ArrowAnnotation(),
        EditorTool.Line => new LineAnnotation(),
        EditorTool.Rectangle => new RectangleAnnotation(),
        EditorTool.Ellipse => new EllipseAnnotation(),
        EditorTool.Highlight => new HighlightAnnotation(),
        EditorTool.Pixelate => new PixelateAnnotation(),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        PointerMoved?.Invoke(this, p);
        if (_document is null || _dragMode == DragMode.None)
            return;

        bool constrain = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_dragMode)
        {
            case DragMode.Create when _creating is TwoPointAnnotation tp:
                tp.End = constrain ? Constrain(tp, tp.Start, p) : p;
                break;

            case DragMode.Create when _creating is PenAnnotation pen:
                if ((p - pen.Points[^1]).Length >= 1)
                    pen.Points.Add(p);
                break;

            case DragMode.Move when Selected is not null:
                EnsureCheckpoint();
                Selected.Move(p - _lastPoint);
                break;

            case DragMode.Handle when Selected is TwoPointAnnotation tp:
                EnsureCheckpoint();
                if (_handleIndex == 0)
                    tp.Start = constrain ? Constrain(tp, tp.End, p) : p;
                else
                    tp.End = constrain ? Constrain(tp, tp.Start, p) : p;
                break;

            case DragMode.Crop:
                _cropRect = Rect.Intersect(new Rect(_cropStart, p), new Rect(0, 0, _document.PixelWidth, _document.PixelHeight));
                break;
        }

        _lastPoint = p;
        InvalidateVisual();
    }

    /// <summary>Moves and handle drags only record an undo point once something actually changes.</summary>
    private void EnsureCheckpoint()
    {
        if (_checkpointTaken || _document is null)
            return;
        _document.Checkpoint();
        _checkpointTaken = true;
    }

    /// <summary>Shift-drag: lines snap to 45° steps, boxes become squares.</summary>
    private static Point Constrain(TwoPointAnnotation shape, Point anchor, Point p)
    {
        Vector v = p - anchor;
        if (shape is LineAnnotation or ArrowAnnotation)
        {
            double step = Math.PI / 4;
            double angle = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
            return anchor + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
        }
        double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
        return anchor + new Vector(Math.Sign(v.X) * side, Math.Sign(v.Y) * side);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_document is null)
            return;

        switch (_dragMode)
        {
            case DragMode.Create:
                bool tooSmall = _creating is TwoPointAnnotation { IsTooSmall: true } || _creating is PenAnnotation { Points.Count: < 2 };
                if (tooSmall && _creating is not null)
                {
                    _document.Remove(_creating);
                    _document.DiscardCheckpoint();
                }
                else
                {
                    SetSelected(_creating);
                    RaiseEdited();
                }
                _creating = null;
                break;

            case DragMode.Move:
            case DragMode.Handle:
                if (_checkpointTaken)
                    RaiseEdited();
                break;

            case DragMode.Crop:
                if (_cropRect.Width >= 4 && _cropRect.Height >= 4)
                {
                    var rect = _document.Pixels.ClampToImage(_cropRect);
                    _cropRect = Rect.Empty;
                    if (_tool == EditorTool.Ocr)
                    {
                        OcrZoneSelected?.Invoke(this, rect);
                    }
                    else
                    {
                        _document.Crop(rect);
                        RaiseEdited();
                    }
                }
                _cropRect = Rect.Empty;
                break;
        }

        _dragMode = DragMode.None;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    private Annotation? HitTestTop(Point p)
    {
        if (_document is null)
            return null;
        for (int i = _document.Annotations.Count - 1; i >= 0; i--)
        {
            if (_document.Annotations[i].HitTest(p, Tolerance))
                return _document.Annotations[i];
        }
        return null;
    }

    private int? HitHandle(TwoPointAnnotation shape, Point p)
    {
        if ((p - shape.Start).Length <= HandleRadius * 1.5) return 0;
        if ((p - shape.End).Length <= HandleRadius * 1.5) return 1;
        return null;
    }

    // ---- rendering -----------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        if (_document is null)
            return;

        DocumentRenderer.Render(dc, _document);

        if (Selected is not null && _document.Annotations.Contains(Selected))
            DrawSelection(dc, Selected);

        if (_tool is EditorTool.Crop or EditorTool.Ocr && !_cropRect.IsEmpty)
            DrawCropOverlay(dc);
    }

    private void DrawSelection(DrawingContext dc, Annotation annotation)
    {
        double px = 1 / Zoom;
        var bounds = annotation.Bounds;
        bounds.Inflate(annotation.Thickness / 2 + 3 * px, annotation.Thickness / 2 + 3 * px);

        var dash = new Pen(new SolidColorBrush(Color.FromRgb(0, 153, 255)), px) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        dc.DrawRectangle(null, dash, bounds);

        if (annotation is TwoPointAnnotation tp)
        {
            var fill = Brushes.White;
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0, 153, 255)), 1.5 * px);
            dc.DrawEllipse(fill, outline, tp.Start, HandleRadius, HandleRadius);
            dc.DrawEllipse(fill, outline, tp.End, HandleRadius, HandleRadius);
        }
    }

    private void DrawCropOverlay(DrawingContext dc)
    {
        var full = new Rect(0, 0, _document!.PixelWidth, _document.PixelHeight);
        var shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
        var geometry = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(_cropRect));
        dc.DrawGeometry(shade, null, geometry);
        dc.DrawRectangle(null, new Pen(Brushes.White, 1 / Zoom) { DashStyle = DashStyles.Dash }, _cropRect);
    }
}
