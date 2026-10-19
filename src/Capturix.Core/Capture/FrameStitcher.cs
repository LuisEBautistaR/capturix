using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Capturix.Core.Capture;

public enum StitchOutcome
{
    /// <summary>First frame stored.</summary>
    First,
    /// <summary>The content moved and the new rows were appended.</summary>
    Appended,
    /// <summary>Nothing moved: the end of the scrollable content was reached.</summary>
    NoMovement,
    /// <summary>No reliable overlap was found (scrolled too far, or the content changed).</summary>
    NoMatch,
}

public readonly record struct StitchResult(StitchOutcome Outcome, int ScrolledPixels = 0, int MovingHeight = 0);

/// <summary>
/// Builds one tall image from successive frames of a scrolling area.
///
/// Each frame is reduced to one 64-bit hash per pixel row (ignoring the rightmost columns,
/// where a scrollbar thumb moves). Between two frames, rows that are identical at the same
/// position at the top and bottom are fixed bands (headers, toolbars, footers); the vertical
/// offset of the band in between is the shift that makes the most content rows line up.
/// Uniform rows (blank space) match anything, so only rows with detail are counted.
/// </summary>
public sealed class FrameStitcher : IDisposable
{
    /// <summary>Columns at the right edge excluded from matching: vertical scrollbars live there.</summary>
    public const int IgnoredRightColumns = 24;

    /// <summary>Window borders at the left edge are never part of the scrolling content.</summary>
    public const int IgnoredLeftColumns = 2;

    private const double MinMatchRatio = 0.97;
    private const int MinMatchedRows = 8;

    private readonly List<Bitmap> _strips = new();
    private Bitmap? _previous;
    private RowInfo? _previousRows;
    private Bitmap? _firstFrame;
    private int _bottomBand = -1;   // fixed footer height, taken from the first successful pair
    private int _lastBottomBand;

    public int FrameCount { get; private set; }

    /// <summary>Height the composed image would have right now.</summary>
    public int Height => _strips.Count == 0
        ? _firstFrame?.Height ?? 0
        : _strips.Sum(s => s.Height) + _lastBottomBand;

    public StitchResult Add(Bitmap frame)
    {
        var rows = RowInfo.From(frame);
        if (_previous is null || _previousRows is null)
        {
            _firstFrame = (Bitmap)frame.Clone();
            Remember(frame, rows);
            FrameCount = 1;
            return new StitchResult(StitchOutcome.First);
        }

        if (frame.Width != _previous.Width || frame.Height != _previous.Height)
            return new StitchResult(StitchOutcome.NoMatch);

        var prev = _previousRows;
        int h = frame.Height;

        int top = 0;
        while (top < h && prev.Hashes[top] == rows.Hashes[top])
            top++;
        if (top >= h - 2)
            return new StitchResult(StitchOutcome.NoMovement);

        int bottom = 0;
        while (bottom < h - top && prev.Hashes[h - 1 - bottom] == rows.Hashes[h - 1 - bottom])
            bottom++;

        int moving = h - top - bottom;

        // A real scroll changes almost every detailed row; a blinking caret or a hover effect
        // changes only a few. Treat the latter as "did not move".
        int detailedRows = 0, unchangedRows = 0;
        for (int y = top; y < h - bottom; y++)
        {
            if (!rows.Detailed[y]) continue;
            detailedRows++;
            if (rows.Hashes[y] == prev.Hashes[y]) unchangedRows++;
        }
        if (detailedRows > 0 && unchangedRows >= detailedRows * 0.85)
            return new StitchResult(StitchOutcome.NoMovement);

        // Columns that look the same at the same position in both frames do not scroll:
        // window borders (translucent on Windows 11, so they show whatever is behind them),
        // fixed sidebars, blank margins. Matching only the columns that changed keeps those
        // from vetoing every candidate shift.
        var columns = ChangedColumns(prev, rows, top, h - bottom);
        if (columns.Length == 0)
            return new StitchResult(StitchOutcome.NoMovement);

        int dy = FindShift(prev.Rehash(columns, top, h - bottom), rows.Rehash(columns, top, h - bottom), top, h - bottom);
        if (dy <= 0)
            return new StitchResult(StitchOutcome.NoMatch, 0, moving);

        if (_bottomBand < 0)
        {
            // First successful pair: the first strip is frame 0 down to the fixed footer.
            _bottomBand = bottom;
            _strips.Add(Crop(_firstFrame!, 0, h - _bottomBand));
        }

        // The rows that scrolled into view sit just above the footer of the new frame.
        int footer = Math.Min(_bottomBand, h - dy);
        _strips.Add(Crop(frame, h - footer - dy, dy));
        _lastBottomBand = footer;
        Remember(frame, rows);
        FrameCount++;
        return new StitchResult(StitchOutcome.Appended, dy, moving);
    }

    /// <summary>
    /// Shift dy such that new rows [y] equal previous rows [y + dy] inside [start, end).
    /// Picks the candidate with the most matching detailed rows among those that are
    /// almost never contradicted, so repeated or blank content cannot fool it.
    /// </summary>
    private static int FindShift(RowInfo prev, RowInfo cur, int start, int end)
    {
        int bestShift = 0, bestMatches = 0;
        for (int dy = 1; dy < end - start; dy++)
        {
            int matches = 0, mismatches = 0;
            for (int y = start; y + dy < end; y++)
            {
                if (!cur.Detailed[y])
                    continue;
                if (cur.Hashes[y] == prev.Hashes[y + dy])
                    matches++;
                else
                    mismatches++;
            }
            int compared = matches + mismatches;
            if (compared == 0 || matches < MinMatchedRows || (double)matches / compared < MinMatchRatio)
                continue;
            if (matches > bestMatches)
            {
                bestMatches = matches;
                bestShift = dy;
            }
        }
        return bestShift;
    }

    /// <summary>Indexes of the columns (outside the ignored edges) that differ somewhere in [start, end).</summary>
    private static int[] ChangedColumns(RowInfo prev, RowInfo cur, int start, int end)
    {
        int w = cur.Width;
        var changed = new List<int>();
        for (int x = IgnoredLeftColumns; x < w - IgnoredRightColumns; x++)
        {
            for (int y = start; y < end; y++)
            {
                int i = y * w + x;
                if ((prev.Pixels[i] & 0x00FFFFFF) != (cur.Pixels[i] & 0x00FFFFFF))
                {
                    changed.Add(x);
                    break;
                }
            }
        }
        return changed.ToArray();
    }

    private void Remember(Bitmap frame, RowInfo rows)
    {
        _previous?.Dispose();
        _previous = (Bitmap)frame.Clone();
        _previousRows = rows;
    }

    private static Bitmap Crop(Bitmap source, int y, int height) =>
        source.Clone(new Rectangle(0, y, source.Width, height), PixelFormat.Format32bppArgb);

    /// <summary>Draws all strips, plus the footer of the last frame, into one image.</summary>
    public Bitmap Compose()
    {
        if (_firstFrame is null)
            throw new InvalidOperationException("No hay fotogramas.");
        if (_strips.Count == 0)
            return (Bitmap)_firstFrame.Clone(); // the area never scrolled

        int width = _firstFrame.Width;
        int footer = _lastBottomBand;
        int total = _strips.Sum(s => s.Height) + footer;
        var result = new Bitmap(width, total, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        int y = 0;
        foreach (var strip in _strips)
        {
            g.DrawImageUnscaled(strip, 0, y);
            y += strip.Height;
        }
        if (footer > 0 && _previous is not null)
        {
            var src = new Rectangle(0, _previous.Height - footer, width, footer);
            g.DrawImage(_previous, new Rectangle(0, y, width, footer), src, GraphicsUnit.Pixel);
        }
        return result;
    }

    /// <summary>Cheap whole-frame fingerprint, used to wait until a scroll animation settles.</summary>
    public static ulong Signature(Bitmap frame)
    {
        ulong h = 1469598103934665603UL;
        foreach (ulong row in RowInfo.From(frame).Hashes)
            h = (h ^ row) * 1099511628211UL;
        return h;
    }

    public void Dispose()
    {
        foreach (var s in _strips) s.Dispose();
        _strips.Clear();
        _previous?.Dispose();
        _firstFrame?.Dispose();
    }

    /// <summary>Frame pixels plus a per-row hash and whether the row has any detail (is not one flat colour).</summary>
    private sealed class RowInfo
    {
        public required int Width { get; init; }
        public required int[] Pixels { get; init; }
        public required ulong[] Hashes { get; init; }
        public required bool[] Detailed { get; init; }

        public static RowInfo From(Bitmap frame)
        {
            int w = frame.Width, h = frame.Height;
            var data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new int[w * h];
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w, w);
            }
            finally
            {
                frame.UnlockBits(data);
            }

            var columns = Enumerable.Range(IgnoredLeftColumns, Math.Max(1, w - IgnoredLeftColumns - IgnoredRightColumns)).ToArray();
            var (hashes, detailed) = HashRows(pixels, w, columns, 0, h);
            return new RowInfo { Width = w, Pixels = pixels, Hashes = hashes, Detailed = detailed };
        }

        /// <summary>Same frame, rows [start, end) hashed over only <paramref name="columns"/>.</summary>
        public RowInfo Rehash(int[] columns, int start, int end)
        {
            var (hashes, detailed) = HashRows(Pixels, Width, columns, start, end);
            return new RowInfo { Width = Width, Pixels = Pixels, Hashes = hashes, Detailed = detailed };
        }

        private static (ulong[] Hashes, bool[] Detailed) HashRows(int[] pixels, int w, int[] columns, int start, int end)
        {
            int h = pixels.Length / w;
            var hashes = new ulong[h];
            var detailed = new bool[h];
            for (int y = start; y < end; y++)
            {
                int row = y * w;
                ulong hash = 1469598103934665603UL;
                int first = pixels[row + columns[0]] & 0x00FFFFFF;
                bool flat = true;
                foreach (int x in columns)
                {
                    int px = pixels[row + x] & 0x00FFFFFF;
                    hash = (hash ^ (uint)px) * 1099511628211UL;
                    if (flat && px != first)
                        flat = false;
                }
                hashes[y] = hash;
                detailed[y] = !flat;
            }
            return (hashes, detailed);
        }
    }
}
