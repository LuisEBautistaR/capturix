using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturix.Editor.Model;

/// <summary>Everything an annotation may need while rendering.</summary>
public sealed record RenderContext(PixelBuffer Pixels);

/// <summary>
/// An editable object drawn over the capture. Annotations are plain objects so the
/// document can snapshot them (by cloning) for undo/redo.
/// </summary>
public abstract class Annotation
{
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4;

    public abstract Rect Bounds { get; }
    public abstract void Render(DrawingContext dc, RenderContext ctx);
    public abstract bool HitTest(Point p, double tolerance);
    public abstract void Move(Vector delta);

    public virtual Annotation Clone() => (Annotation)MemberwiseClone();

    protected SolidColorBrush MakeBrush(byte? alpha = null)
    {
        var c = Color;
        if (alpha is byte a) c.A = a;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    protected Pen MakePen(byte? alpha = null)
    {
        var pen = new Pen(MakeBrush(alpha), Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9)
            return (p - a).Length;
        double t = Math.Clamp((p - a) * ab / len2, 0, 1);
        return (p - (a + t * ab)).Length;
    }
}

/// <summary>An emoji in colour, scaled to fit the box between its two points.</summary>
public sealed class StampAnnotation : TwoPointAnnotation
{
    public string Emoji { get; set; } = EmojiRenderer.DefaultStamp;

    public override void Render(DrawingContext dc, RenderContext ctx) => EmojiRenderer.Draw(dc, Emoji, Bounds);

    public override bool HitTest(Point p, double tolerance)
    {
        var area = Bounds;
        area.Inflate(tolerance, tolerance);
        return area.Contains(p);
    }
}

/// <summary>Shapes defined by two points; the selection handles sit on those points.</summary>
public abstract class TwoPointAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public override Rect Bounds => new(Start, End);

    public override void Move(Vector delta)
    {
        Start += delta;
        End += delta;
    }

    public bool IsTooSmall => (End - Start).Length < 3;
}

public sealed class LineAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawLine(MakePen(), Start, End);

    public override bool HitTest(Point p, double tolerance) =>
        DistanceToSegment(p, Start, End) <= Thickness / 2 + tolerance;
}

public sealed class ArrowAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        Vector dir = End - Start;
        double length = dir.Length;
        if (length < 1)
            return;
        dir /= length;

        double headLength = Math.Min(length, 3 * Thickness + 10);
        double headHalfWidth = headLength * 0.55;
        Point basePoint = End - dir * headLength;
        var normal = new Vector(-dir.Y, dir.X);

        // The shaft stops at the head's base so a thick line never pokes past the tip.
        dc.DrawLine(MakePen(), Start, basePoint);

        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(End, isFilled: true, isClosed: true);
            g.LineTo(basePoint + normal * headHalfWidth, true, true);
            g.LineTo(basePoint - normal * headHalfWidth, true, true);
        }
        head.Freeze();
        dc.DrawGeometry(MakeBrush(), null, head);
    }

    public override bool HitTest(Point p, double tolerance) =>
        DistanceToSegment(p, Start, End) <= Thickness / 2 + tolerance + 4;
}

public sealed class RectangleAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawRectangle(null, MakePen(), Bounds);

    public override bool HitTest(Point p, double tolerance)
    {
        var r = Bounds;
        r.Inflate(Thickness / 2 + tolerance, Thickness / 2 + tolerance);
        return r.Contains(p);
    }
}

public sealed class EllipseAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = Bounds;
        dc.DrawEllipse(null, MakePen(), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var r = Bounds;
        double rx = r.Width / 2 + Thickness / 2 + tolerance, ry = r.Height / 2 + Thickness / 2 + tolerance;
        if (rx <= 0 || ry <= 0) return false;
        double dx = (p.X - (r.X + r.Width / 2)) / rx, dy = (p.Y - (r.Y + r.Height / 2)) / ry;
        return dx * dx + dy * dy <= 1;
    }
}

/// <summary>Translucent marker box, like a highlighter pen over text.</summary>
public sealed class HighlightAnnotation : TwoPointAnnotation
{
    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawRectangle(MakeBrush(0x66), null, Bounds);

    public override bool HitTest(Point p, double tolerance) => Bounds.Contains(p);
}

/// <summary>Hides sensitive content by replacing the area with large averaged blocks.</summary>
public sealed class PixelateAnnotation : TwoPointAnnotation
{
    private BitmapSource? _cache;
    private Int32Rect _cacheRect;
    private PixelBuffer? _cacheSource;
    private int _cacheBlock;

    /// <summary>Block size in pixels; grows with the stroke thickness setting.</summary>
    public int BlockSize => Math.Max(6, (int)(Thickness * 2.5));

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var region = ctx.Pixels.ClampToImage(Bounds);
        if (region.Width <= 0 || region.Height <= 0)
            return;

        if (_cache is null || !_cacheRect.Equals(region) || !ReferenceEquals(_cacheSource, ctx.Pixels) || _cacheBlock != BlockSize)
        {
            _cache = ctx.Pixels.Pixelate(region, BlockSize);
            _cacheRect = region;
            _cacheSource = ctx.Pixels;
            _cacheBlock = BlockSize;
        }
        dc.DrawImage(_cache, new Rect(region.X, region.Y, region.Width, region.Height));
    }

    public override bool HitTest(Point p, double tolerance) => Bounds.Contains(p);
}

/// <summary>Numbered circle for step-by-step instructions; numbers auto-increment.</summary>
public sealed class StepAnnotation : Annotation
{
    public Point Center { get; set; }
    public int Number { get; set; } = 1;

    public double Radius => 12 + Thickness * 1.5;

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var outline = new Pen(Brushes.White, 2);
        outline.Freeze();
        dc.DrawEllipse(MakeBrush(), outline, Center, Radius, Radius);

        var text = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.1, Brushes.White, 1.0);
        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }

    public override bool HitTest(Point p, double tolerance) => (p - Center).Length <= Radius + tolerance;

    public override void Move(Vector delta) => Center += delta;
}

public sealed class TextAnnotation : Annotation
{
    public Point Position { get; set; }
    public string Text { get; set; } = "";

    public double FontSize => 14 + Thickness * 2;

    public static readonly Typeface Typeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private FormattedText Format() =>
        new(Text.Length == 0 ? " " : Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface, FontSize, MakeBrush(), 1.0);

    public override Rect Bounds
    {
        get
        {
            var ft = Format();
            return new Rect(Position, new Size(Math.Max(ft.WidthIncludingTrailingWhitespace, FontSize), ft.Height));
        }
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (Text.Length > 0)
            dc.DrawText(Format(), Position);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var r = Bounds;
        r.Inflate(tolerance, tolerance);
        return r.Contains(p);
    }

    public override void Move(Vector delta) => Position += delta;
}

/// <summary>Freehand stroke.</summary>
public sealed class PenAnnotation : Annotation
{
    public List<Point> Points { get; private set; } = new();

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            var r = new Rect(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            return r;
        }
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count < 2)
            return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Points[0], isFilled: false, isClosed: false);
            g.PolyLineTo(Points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, MakePen(), geometry);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        for (int i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= Thickness / 2 + tolerance)
                return true;
        }
        return false;
    }

    public override void Move(Vector delta)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] += delta;
    }

    public override Annotation Clone()
    {
        var copy = (PenAnnotation)base.Clone();
        copy.Points = new List<Point>(Points);
        return copy;
    }
}
