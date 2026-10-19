using Capturix.Common.Format;

namespace Capturix.Common.Storage;

/// <summary>Moves the captures of a library folder to a new location (used when the save location changes).</summary>
public static class LibraryMover
{
    public static readonly string[] Extensions = { CpxFile.Extension, ".png", ".jpg", ".jpeg", ".gif", ".mp4" };

    public static IReadOnlyList<string> CapturesIn(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Moves every capture from <paramref name="from"/> to <paramref name="to"/>. Name clashes get a
    /// numeric suffix instead of overwriting. Returns how many files moved and which failed.
    /// </summary>
    public static (int Moved, IReadOnlyList<string> Failed) MoveAll(string from, string to)
    {
        Directory.CreateDirectory(to);
        int moved = 0;
        var failed = new List<string>();
        foreach (var source in CapturesIn(from))
        {
            try
            {
                File.Move(source, FreeName(to, Path.GetFileName(source)));
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"No se pudo mover {source}", ex);
                failed.Add(Path.GetFileName(source));
            }
        }
        return (moved, failed);
    }

    private static string FreeName(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem}_{i}{ext}");
        return path;
    }
}
