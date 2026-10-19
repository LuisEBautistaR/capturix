using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturix.Common.Format;

namespace Capturix.Editor.Model;

/// <summary>
/// The capture being edited: base image plus annotations. Undo/redo stores whole-state
/// snapshots; annotations are small and the image is immutable, so snapshots are cheap.
/// </summary>
public sealed class EditorDocument
{
    private sealed record Snapshot(BitmapSource Image, PixelBuffer Pixels, List<Annotation> Annotations, int NextStepNumber);

    private const int MaxUndo = 100;

    private readonly LinkedList<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    public BitmapSource Image { get; private set; }
    public PixelBuffer Pixels { get; private set; }
    public List<Annotation> Annotations { get; private set; } = new();
    public int NextStepNumber { get; set; } = 1;

    /// <summary>File the image was opened from, if any.</summary>
    public string? SourcePath { get; set; }

    /// <summary>The .cpx this document autosaves to; null for plain images not yet saved as .cpx.</summary>
    public string? DocumentPath { get; set; }

    public CpxMetadata Metadata { get; set; } = new();
    public string PageId { get; set; } = Guid.NewGuid().ToString("B").ToUpperInvariant();

    private byte[]? _imagePng;
    private BitmapSource? _imagePngSource;

    public bool IsDirty { get; set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int PixelWidth => Image.PixelWidth;
    public int PixelHeight => Image.PixelHeight;

    /// <summary>Raised after undo, redo, crop or any structural change the view must re-read.</summary>
    public event EventHandler? Changed;

    public EditorDocument(BitmapSource image, string? sourcePath = null)
    {
        Image = Normalize(image);
        Pixels = new PixelBuffer(Image);
        SourcePath = sourcePath;
    }

    /// <summary>Opens a .cpx document or any image format WPF can decode.</summary>
    public static EditorDocument Load(string path)
    {
        if (CpxFile.IsCpx(path))
            return CpxMapper.Load(path);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // release the file handle immediately
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        return new EditorDocument(bmp, path)
        {
            Metadata = new CpxMetadata { CaptureDate = File.GetLastWriteTime(path), Source = "Imagen abierta" },
        };
    }

    /// <summary>PNG of the base image, cached until the image itself changes (crop, undo of a crop).</summary>
    public byte[] GetImagePng()
    {
        if (_imagePng is null || !ReferenceEquals(_imagePngSource, Image))
        {
            _imagePng = CpxMapper.EncodePng(Image);
            _imagePngSource = Image;
        }
        return _imagePng;
    }

    /// <summary>Converts to BGRA32 at 96 DPI so one image pixel is one canvas unit.</summary>
    private static BitmapSource Normalize(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        normalized.Freeze();
        return normalized;
    }

    public RenderContext RenderContext => new(Pixels);

    // ---- undo / redo -----------------------------------------------------------------------

    private Snapshot Capture() =>
        new(Image, Pixels, Annotations.Select(a => a.Clone()).ToList(), NextStepNumber);

    private void Restore(Snapshot s)
    {
        Image = s.Image;
        Pixels = s.Pixels;
        Annotations = s.Annotations.Select(a => a.Clone()).ToList();
        NextStepNumber = s.NextStepNumber;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Call before every modification: records the current state as an undo point.</summary>
    public void Checkpoint()
    {
        _undo.AddLast(Capture());
        if (_undo.Count > MaxUndo)
            _undo.RemoveFirst();
        _redo.Clear();
        IsDirty = true;
    }

    /// <summary>Drops the last checkpoint when the change it guarded was abandoned.</summary>
    public void DiscardCheckpoint()
    {
        if (_undo.Count > 0)
            _undo.RemoveLast();
    }

    public bool Undo()
    {
        if (_undo.Last is not { } last)
            return false;
        _redo.Push(Capture());
        _undo.RemoveLast();
        Restore(last.Value);
        IsDirty = true;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.AddLast(Capture());
        Restore(_redo.Pop());
        IsDirty = true;
        return true;
    }

    // ---- edits that touch the base image ------------------------------------------------------

    /// <summary>Crops the image to <paramref name="rect"/> and shifts annotations to match.</summary>
    public void Crop(Int32Rect rect)
    {
        Checkpoint();
        var cropped = new CroppedBitmap(Image, rect);
        Image = Normalize(cropped);
        Pixels = new PixelBuffer(Image);
        var offset = new Vector(-rect.X, -rect.Y);
        foreach (var a in Annotations)
            a.Move(offset);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(Annotation annotation) => Annotations.Add(annotation);

    public void Remove(Annotation annotation) => Annotations.Remove(annotation);

    /// <summary>Raises <see cref="Changed"/> after in-place edits made by the view.</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
