using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Capturix.Editor.Model;

/// <summary>
/// Draws emoji in colour. WPF renders emoji as black outlines, so this reads the colour layers
/// straight from Windows' own emoji font (Segoe UI Emoji): the COLR table lists, for each emoji
/// glyph, the stack of plain glyphs that make it up and the CPAL palette entry each one is
/// filled with. Every layer becomes a vector shape, so stamps stay sharp at any size and look
/// exactly like the emoji in Windows itself. No image assets are bundled.
/// </summary>
public static class EmojiRenderer
{
    /// <summary>The stamps offered in the editor, in picker order.</summary>
    public static readonly string[] Stamps =
    {
        "😀", "😂", "😍", "😎", "🤔", "😮", "😢", "😭", "😡", "😴",
        "👍", "👎", "👏", "👉", "👀", "🙏",
        "✅", "❌", "⚠️", "❓", "❗", "💯",
        "❤️", "⭐", "🔥", "💡", "🐞", "🔒", "📌", "🎉",
    };

    public const string DefaultStamp = "👍";

    private const ushort ForegroundPalette = 0xFFFF;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (Drawing Drawing, Rect Bounds)> Cache = new();
    private static FontData? _font;
    private static bool _loadFailed;

    /// <summary>True when the colour tables could be read (Windows 10 1709 or later).</summary>
    public static bool HasColor => Font is not null;

    /// <summary>Number of colour layers for <paramref name="emoji"/>; 0 means it would draw in one colour.</summary>
    public static int LayerCount(string emoji) =>
        Font is { } f && f.GlyphFor(emoji) is { } gid && f.Layers.TryGetValue(gid, out var layers) ? layers.Length : 0;

    /// <summary>Draws <paramref name="emoji"/> centred in <paramref name="box"/>, as large as fits.</summary>
    public static void Draw(DrawingContext dc, string emoji, Rect box)
    {
        if (box.Width < 1 || box.Height < 1)
            return;
        var (drawing, bounds) = Get(emoji);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return;
        double scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
        double x = box.X + (box.Width - bounds.Width * scale) / 2 - bounds.X * scale;
        double y = box.Y + (box.Height - bounds.Height * scale) / 2 - bounds.Y * scale;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        dc.DrawDrawing(drawing);
        dc.Pop();
    }

    /// <summary>A frozen image of the emoji, for buttons in the picker.</summary>
    public static ImageSource ToImage(string emoji)
    {
        var image = new DrawingImage(Get(emoji).Drawing);
        image.Freeze();
        return image;
    }

    private static (Drawing Drawing, Rect Bounds) Get(string emoji)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(emoji, out var hit))
                return hit;
            var built = Build(emoji);
            Cache[emoji] = built;
            return built;
        }
    }

    private static (Drawing, Rect) Build(string emoji)
    {
        const double em = 100;
        var group = new DrawingGroup();
        var font = Font;
        if (font is not null && font.GlyphFor(emoji) is { } gid && font.Layers.TryGetValue(gid, out var layers))
        {
            foreach (var (layerGlyph, paletteIndex) in layers)
            {
                var geometry = font.Typeface.GetGlyphOutline(layerGlyph, em, em);
                var color = paletteIndex == ForegroundPalette || paletteIndex >= font.Palette.Length
                    ? Colors.Black
                    : font.Palette[paletteIndex];
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                group.Children.Add(new GeometryDrawing(brush, null, geometry));
            }
        }
        else
        {
            // No colour data: fall back to the plain glyph so the stamp is still visible.
            var text = new FormattedText(emoji, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"), em, Brushes.Black, 1.0);
            group.Children.Add(new GeometryDrawing(Brushes.Black, null, text.BuildGeometry(new Point(0, 0))));
        }
        group.Freeze();
        return (group, group.Bounds);
    }

    private static FontData? Font
    {
        get
        {
            lock (Gate)
            {
                if (_font is null && !_loadFailed)
                {
                    try
                    {
                        _font = FontData.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                                   or ArgumentException or FileFormatException)
                    {
                        Capturix.Common.Log.Error("No se pudieron leer los colores de la fuente de emoji", ex);
                    }
                    _loadFailed = _font is null;
                }
                return _font;
            }
        }
    }

    /// <summary>The parts of the font file this renderer needs: glyph map, colour layers, palette.</summary>
    private sealed class FontData
    {
        public required GlyphTypeface Typeface { get; init; }
        public required Dictionary<ushort, (ushort Glyph, ushort Palette)[]> Layers { get; init; }
        public required Color[] Palette { get; init; }

        public ushort? GlyphFor(string emoji)
        {
            if (string.IsNullOrEmpty(emoji))
                return null;
            int codepoint = char.ConvertToUtf32(emoji, 0); // the base character; a trailing VS16 only asks for colour
            return Typeface.CharacterToGlyphMap.TryGetValue(codepoint, out var gid) ? gid : null;
        }

        public static FontData Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            var tables = new Dictionary<string, int>();
            int numTables = U16(b, 4);
            for (int i = 0; i < numTables; i++)
            {
                int r = 12 + 16 * i;
                tables[System.Text.Encoding.ASCII.GetString(b, r, 4)] = (int)U32(b, r + 8);
            }
            if (!tables.TryGetValue("COLR", out int colr) || !tables.TryGetValue("CPAL", out int cpal))
                throw new InvalidDataException("La fuente de emoji no tiene tablas de color.");

            // COLR, version 0 part (also present in version 1 fonts): flat-colour layer stacks.
            int baseCount = U16(b, colr + 2);
            int baseOffset = (int)U32(b, colr + 4);
            int layerOffset = (int)U32(b, colr + 8);
            var layers = new Dictionary<ushort, (ushort, ushort)[]>(baseCount);
            for (int i = 0; i < baseCount; i++)
            {
                int rec = colr + baseOffset + 6 * i;
                ushort glyph = U16(b, rec);
                int first = U16(b, rec + 2), count = U16(b, rec + 4);
                var stack = new (ushort, ushort)[count];
                for (int j = 0; j < count; j++)
                {
                    int lr = colr + layerOffset + 4 * (first + j);
                    stack[j] = (U16(b, lr), U16(b, lr + 2));
                }
                layers[glyph] = stack;
            }

            // CPAL: palette 0. Colour records are stored as B, G, R, A.
            int entries = U16(b, cpal + 2);
            int recordsOffset = (int)U32(b, cpal + 8);
            int firstIndex = U16(b, cpal + 12);
            var palette = new Color[entries];
            for (int i = 0; i < entries; i++)
            {
                int c = cpal + recordsOffset + 4 * (firstIndex + i);
                palette[i] = Color.FromArgb(b[c + 3], b[c + 2], b[c + 1], b[c]);
            }

            return new FontData { Typeface = new GlyphTypeface(new Uri(path)), Layers = layers, Palette = palette };
        }

        private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2));
        private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    }
}
