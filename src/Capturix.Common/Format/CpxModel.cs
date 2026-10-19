using System.Text.Json.Serialization;

namespace Capturix.Common.Format;

// JSON schema of a .cpx document. A .cpx is a ZIP archive:
//
//   index.json       CpxIndex     – format version and page list
//   metadata.json    CpxMetadata  – capture date, source, tags
//   thumbnail.png                 – small flattened preview for the library
//   <pageId>.json    CpxPage      – annotations of the page
//   <pageId>.png                  – untouched base image of the page

public sealed class CpxIndex
{
    /// <summary>2 added emoji stamps. Files without stamps are still written as 1 so Capturix 1.0 opens them.</summary>
    public const int CurrentVersion = 2;
    public const int FirstVersionWithStamps = 2;

    public int Version { get; set; } = CurrentVersion;
    public List<string> Pages { get; set; } = new();
}

public sealed class CpxMetadata
{
    public DateTime CaptureDate { get; set; } = DateTime.Now;

    /// <summary>How the image was obtained, e.g. "Región", "Ventana: Notepad", "Imagen abierta".</summary>
    public string Source { get; set; } = "";

    public List<string> Tags { get; set; } = new();
}

public sealed class CpxPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("B").ToUpperInvariant();
    public int Width { get; set; }
    public int Height { get; set; }
    public int NextStepNumber { get; set; } = 1;
    public List<AnnotationDto> Annotations { get; set; } = new();

    [JsonIgnore]
    public string JsonEntryName => $"{Id}.json";

    [JsonIgnore]
    public string ImageEntryName => $"{Id}.png";
}

/// <summary>Colours are stored as "#AARRGGBB"; coordinates are image pixels.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LineDto), "line")]
[JsonDerivedType(typeof(ArrowDto), "arrow")]
[JsonDerivedType(typeof(RectangleDto), "rectangle")]
[JsonDerivedType(typeof(EllipseDto), "ellipse")]
[JsonDerivedType(typeof(HighlightDto), "highlight")]
[JsonDerivedType(typeof(PixelateDto), "pixelate")]
[JsonDerivedType(typeof(StepDto), "step")]
[JsonDerivedType(typeof(TextDto), "text")]
[JsonDerivedType(typeof(PenDto), "pen")]
[JsonDerivedType(typeof(StampDto), "stamp")]
public abstract class AnnotationDto
{
    public string Color { get; set; } = "#FFE81E1E";
    public double Thickness { get; set; } = 4;
}

public abstract class TwoPointDto : AnnotationDto
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
}

public sealed class LineDto : TwoPointDto;
public sealed class ArrowDto : TwoPointDto;
public sealed class RectangleDto : TwoPointDto;
public sealed class EllipseDto : TwoPointDto;
public sealed class HighlightDto : TwoPointDto;
public sealed class PixelateDto : TwoPointDto;

/// <summary>An emoji stamp fitted into the box (X1,Y1)–(X2,Y2).</summary>
public sealed class StampDto : TwoPointDto
{
    public string Emoji { get; set; } = "";
}

public sealed class StepDto : AnnotationDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public int Number { get; set; }
}

public sealed class TextDto : AnnotationDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
}

public sealed class PenDto : AnnotationDto
{
    /// <summary>Flattened x,y pairs: [x0, y0, x1, y1, …] keeps the JSON compact.</summary>
    public List<double> Points { get; set; } = new();
}
