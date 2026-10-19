namespace Capturix.Core.Recording;

/// <summary>
/// Streaming animated-GIF encoder for screen recordings. Each frame stores only the
/// rectangle that changed since the previous one; unchanged pixels inside it are
/// transparent, and identical frames just extend the previous frame's delay. That keeps
/// screen GIFs (mostly static UI) small. Colours: per-frame popularity palette (≤255 colours
/// plus one transparent index), which suits flat UI colours far better than a fixed palette.
/// </summary>
public sealed class GifSink : IFrameSink
{
    private const int TransparentIndex = 255;
    private const int MaxColors = 255;

    private readonly Stream _out;
    private readonly int _width, _height;
    private readonly int _fps;

    private byte[]? _canvas;          // previous source frame (BGRA), for change detection
    private PendingFrame? _pending;   // encoded frame waiting for the next timestamp (its delay)
    private bool _completed;

    public int FramesWritten { get; private set; }

    private sealed record PendingFrame(int X, int Y, int W, int H, byte[] Indices, byte[] Palette, bool UsesTransparency, long Timestamp);

    public GifSink(string path, int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _out = new BufferedStream(File.Create(path), 1 << 16);
        WriteHeader();
    }

    private void WriteHeader()
    {
        WriteAscii("GIF89a");
        WriteUInt16(_width);
        WriteUInt16(_height);
        _out.WriteByte(0x70); // no global colour table, 8-bit colour resolution
        _out.WriteByte(0);    // background colour index
        _out.WriteByte(0);    // pixel aspect ratio
        // NETSCAPE2.0 application extension: loop forever.
        _out.Write(new byte[] { 0x21, 0xFF, 0x0B });
        WriteAscii("NETSCAPE2.0");
        _out.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
    }

    public void WriteFrame(byte[] bgra, int stride, long timestamp)
    {
        // Bounding box of pixels that changed since the previous frame.
        int minX = _width, minY = _height, maxX = -1, maxY = -1;
        if (_canvas is null)
        {
            minX = 0; minY = 0; maxX = _width - 1; maxY = _height - 1;
        }
        else
        {
            for (int y = 0; y < _height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < _width; x++)
                {
                    int i = row + x * 4;
                    if (bgra[i] != _canvas[i] || bgra[i + 1] != _canvas[i + 1] || bgra[i + 2] != _canvas[i + 2])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0)
                return; // identical frame: the pending frame simply stays on screen longer
        }

        int w = maxX - minX + 1, h = maxY - minY + 1;
        bool transparency = _canvas is not null;
        var (indices, palette) = Quantize(bgra, stride, minX, minY, w, h, transparency);

        FlushPending(timestamp);
        _pending = new PendingFrame(minX, minY, w, h, indices, palette, transparency, timestamp);

        _canvas ??= new byte[stride * _height];
        Buffer.BlockCopy(bgra, 0, _canvas, 0, Math.Min(bgra.Length, _canvas.Length));
    }

    /// <summary>Writes the pending frame now that the next frame's time (its delay) is known.</summary>
    private void FlushPending(long nextTimestamp)
    {
        if (_pending is null)
            return;
        var f = _pending;
        int delayCs = (int)Math.Clamp((nextTimestamp - f.Timestamp) / 100_000, 2, 65535); // 1/100 s; browsers clamp <2

        // Graphic control extension: delay, disposal "leave in place", optional transparency.
        _out.Write(new byte[] { 0x21, 0xF9, 0x04, (byte)(0x04 | (f.UsesTransparency ? 1 : 0)) });
        WriteUInt16(delayCs);
        _out.WriteByte(TransparentIndex);
        _out.WriteByte(0);

        // Image descriptor with a 256-entry local colour table.
        _out.WriteByte(0x2C);
        WriteUInt16(f.X);
        WriteUInt16(f.Y);
        WriteUInt16(f.W);
        WriteUInt16(f.H);
        _out.WriteByte(0x87);
        _out.Write(f.Palette);

        LzwEncoder.Encode(_out, f.Indices, minCodeSize: 8);
        _pending = null;
        FramesWritten++;
    }

    /// <summary>
    /// Popularity quantization over 15-bit colour bins. With transparency enabled, pixels equal
    /// to the previous frame map to <see cref="TransparentIndex"/> so the old pixels show through.
    /// </summary>
    private (byte[] Indices, byte[] Palette) Quantize(byte[] bgra, int stride, int x0, int y0, int w, int h, bool transparency)
    {
        var counts = new int[32768];
        var sumR = new long[32768];
        var sumG = new long[32768];
        var sumB = new long[32768];

        for (int y = y0; y < y0 + h; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x0 + w; x++)
            {
                int i = row + x * 4;
                if (transparency && Unchanged(bgra, i))
                    continue;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                counts[key]++;
                sumR[key] += r;
                sumG[key] += g;
                sumB[key] += b;
            }
        }

        // The most frequent bins become the palette; each entry is the mean colour of its bin.
        var top = Enumerable.Range(0, 32768).Where(k => counts[k] > 0)
            .OrderByDescending(k => counts[k]).Take(MaxColors).ToArray();
        var palette = new byte[256 * 3];
        var lookup = new int[32768];
        Array.Fill(lookup, -1);
        var pr = new int[top.Length];
        var pg = new int[top.Length];
        var pb = new int[top.Length];
        for (int p = 0; p < top.Length; p++)
        {
            int k = top[p];
            pr[p] = (int)(sumR[k] / counts[k]);
            pg[p] = (int)(sumG[k] / counts[k]);
            pb[p] = (int)(sumB[k] / counts[k]);
            palette[p * 3] = (byte)pr[p];
            palette[p * 3 + 1] = (byte)pg[p];
            palette[p * 3 + 2] = (byte)pb[p];
            lookup[k] = p;
        }

        var indices = new byte[w * h];
        int n = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x0 + w; x++, n++)
            {
                int i = row + x * 4;
                if (transparency && Unchanged(bgra, i))
                {
                    indices[n] = TransparentIndex;
                    continue;
                }
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                int index = lookup[key];
                if (index < 0)
                    index = lookup[key] = Nearest(r, g, b, pr, pg, pb);
                indices[n] = (byte)index;
            }
        }
        return (indices, palette);
    }

    private bool Unchanged(byte[] bgra, int i) =>
        _canvas is not null && bgra[i] == _canvas[i] && bgra[i + 1] == _canvas[i + 1] && bgra[i + 2] == _canvas[i + 2];

    private static int Nearest(int r, int g, int b, int[] pr, int[] pg, int[] pb)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int p = 0; p < pr.Length; p++)
        {
            int dr = r - pr[p], dg = g - pg[p], db = b - pb[p];
            int dist = dr * dr * 3 + dg * dg * 4 + db * db * 2; // rough perceptual weighting
            if (dist < bestDist)
            {
                bestDist = dist;
                best = p;
            }
        }
        return best;
    }

    public void Complete()
    {
        if (_completed)
            return;
        _completed = true;
        long last = _pending?.Timestamp ?? 0;
        FlushPending(last + 10_000_000L / _fps);
        _out.WriteByte(0x3B); // trailer
        _out.Flush();
    }

    public void Dispose()
    {
        try
        {
            Complete();
        }
        finally
        {
            _out.Dispose();
        }
    }

    private void WriteUInt16(int value)
    {
        _out.WriteByte((byte)(value & 0xFF));
        _out.WriteByte((byte)(value >> 8));
    }

    private void WriteAscii(string text)
    {
        foreach (char c in text)
            _out.WriteByte((byte)c);
    }
}

/// <summary>GIF-flavoured LZW (variable code width 9–12 bits, LSB-first, 255-byte sub-blocks).</summary>
internal static class LzwEncoder
{
    public static void Encode(Stream output, byte[] indices, int minCodeSize)
    {
        output.WriteByte((byte)minCodeSize);
        var bits = new BitPacker(output);

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int maxCode = endCode;
        var dictionary = new Dictionary<int, int>(4096);

        bits.Write(clearCode, codeSize);
        if (indices.Length == 0)
        {
            bits.Write(endCode, codeSize);
            bits.Flush();
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            int key = (prefix << 8) | k;
            if (dictionary.TryGetValue(key, out int code))
            {
                prefix = code;
                continue;
            }

            bits.Write(prefix, codeSize);
            dictionary[key] = ++maxCode;
            if (maxCode >= (1 << codeSize))
                codeSize++;
            if (maxCode == 4095)
            {
                // Table full: emit a clear code and start over.
                bits.Write(clearCode, codeSize);
                dictionary.Clear();
                codeSize = minCodeSize + 1;
                maxCode = endCode;
            }
            prefix = k;
        }

        bits.Write(prefix, codeSize);
        bits.Write(clearCode, codeSize);
        bits.Write(endCode, minCodeSize + 1);
        bits.Flush();
    }

    private sealed class BitPacker(Stream output)
    {
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private int _accumulator;
        private int _bitCount;

        public void Write(int code, int size)
        {
            _accumulator |= code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                Emit((byte)(_accumulator & 0xFF));
                _accumulator >>= 8;
                _bitCount -= 8;
            }
        }

        private void Emit(byte b)
        {
            _block[_blockLength++] = b;
            if (_blockLength == 255)
                FlushBlock();
        }

        private void FlushBlock()
        {
            if (_blockLength == 0)
                return;
            output.WriteByte((byte)_blockLength);
            output.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }

        public void Flush()
        {
            if (_bitCount > 0)
                Emit((byte)(_accumulator & 0xFF));
            _accumulator = 0;
            _bitCount = 0;
            FlushBlock();
            output.WriteByte(0); // block terminator
        }
    }
}
