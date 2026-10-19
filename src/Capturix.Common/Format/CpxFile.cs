using System.IO.Compression;
using System.Text.Json;

namespace Capturix.Common.Format;

/// <summary>Reads and writes .cpx documents (single page for now; the format allows several).</summary>
public sealed class CpxFile
{
    public const string Extension = ".cpx";
    private const string IndexEntry = "index.json";
    private const string MetadataEntry = "metadata.json";
    private const string ThumbnailEntry = "thumbnail.png";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public CpxMetadata Metadata { get; set; } = new();
    public CpxPage Page { get; set; } = new();

    /// <summary>PNG bytes of the untouched base image.</summary>
    public byte[] ImagePng { get; set; } = Array.Empty<byte>();

    /// <summary>PNG bytes of a small flattened preview (optional).</summary>
    public byte[]? ThumbnailPng { get; set; }

    public static bool IsCpx(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public static CpxFile Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var index = ReadJson<CpxIndex>(zip, IndexEntry);
        if (index.Version > CpxIndex.CurrentVersion)
            throw new InvalidDataException($"Este documento se creó con una versión más nueva de Capturix (formato {index.Version}). Actualiza Capturix para abrirlo.");
        if (index.Pages.Count == 0)
            throw new InvalidDataException("El documento no tiene páginas.");

        var page = ReadJson<CpxPage>(zip, index.Pages[0]);
        return new CpxFile
        {
            Metadata = zip.GetEntry(MetadataEntry) is null ? new CpxMetadata() : ReadJson<CpxMetadata>(zip, MetadataEntry),
            Page = page,
            ImagePng = ReadBytes(zip, page.ImageEntryName)
                       ?? throw new InvalidDataException($"Falta la imagen {page.ImageEntryName}."),
            ThumbnailPng = ReadBytes(zip, ThumbnailEntry),
        };
    }

    /// <summary>Reads only the preview image, for fast library listings.</summary>
    public static byte[]? ReadThumbnail(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadBytes(zip, ThumbnailEntry);
    }

    /// <summary>
    /// Writes to a temporary file and then replaces the target, so an interrupted autosave
    /// can never leave a half-written document behind.
    /// </summary>
    public void Write(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temp))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                int version = Page.Annotations.Any(a => a is StampDto) ? CpxIndex.FirstVersionWithStamps : 1;
                WriteJson(zip, IndexEntry, new CpxIndex { Version = version, Pages = { Page.JsonEntryName } });
                WriteJson(zip, MetadataEntry, Metadata);
                WriteJson(zip, Page.JsonEntryName, Page);
                // PNG data is already compressed; storing avoids wasting CPU on autosave.
                WriteBytes(zip, Page.ImageEntryName, ImagePng, CompressionLevel.NoCompression);
                if (ThumbnailPng is not null)
                    WriteBytes(zip, ThumbnailEntry, ThumbnailPng, CompressionLevel.NoCompression);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static T ReadJson<T>(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Falta {name} en el documento.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException($"{name} está vacío.");
    }

    private static byte[]? ReadBytes(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
            return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, Json);
    }

    private static void WriteBytes(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data);
    }
}
