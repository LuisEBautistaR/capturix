using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Capturix.Common.Storage;

/// <summary>Where the library lives; persisted in settings.json as "SaveLocation".</summary>
public static class SaveLocation
{
    public const string Local = "local";
    public const string OneDrive = "onedrive";
    public const string GoogleDrive = "googledrive";
    public const string Custom = "custom";
}

/// <summary>A place the library can be stored, with whether it is usable on this PC.</summary>
public sealed record StorageTarget(string Kind, string DisplayName, string? Folder, string? Warning = null)
{
    public bool IsAvailable => Folder is not null;
}

/// <summary>
/// Finds the folders that the OneDrive and Google Drive desktop apps keep in sync. Capturix
/// only writes files there; uploading is done by the services' own apps, so Capturix never
/// handles accounts, passwords or tokens.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StorageLocations
{
    public const string LibraryFolderName = "Capturix";

    /// <summary>Never synced: the user profile root is not part of OneDrive's folder backup.</summary>
    public static string LocalFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), LibraryFolderName);

    public static IReadOnlyList<StorageTarget> GetTargets(string customFolder)
    {
        var oneDrive = OneDriveRoots().FirstOrDefault();
        var google = GoogleDriveRoot();
        return new[]
        {
            new StorageTarget(SaveLocation.Local, "Local (solo este equipo)", LocalFolder),
            new StorageTarget(SaveLocation.OneDrive, "OneDrive", oneDrive is null ? null : Path.Combine(oneDrive, LibraryFolderName),
                oneDrive is not null && !OneDriveSignedIn() ? OneDriveNotSignedIn : null),
            new StorageTarget(SaveLocation.GoogleDrive, "Google Drive", google is null ? null : Path.Combine(google, LibraryFolderName)),
            new StorageTarget(SaveLocation.Custom, "Otra carpeta", string.IsNullOrWhiteSpace(customFolder) ? null : customFolder),
        };
    }

    /// <summary>
    /// The folder for <paramref name="kind"/>; falls back to the local folder when the cloud app
    /// is missing (for example after uninstalling it), so captures are never lost.
    /// </summary>
    public static string Resolve(string kind, string customFolder) =>
        GetTargets(customFolder).FirstOrDefault(t => t.Kind == kind && t.IsAvailable)?.Folder ?? LocalFolder;

    /// <summary>"OneDrive", "Google Drive" or null when <paramref name="path"/> is not inside a synced folder.</summary>
    public const string OneDriveNotSignedIn = "OneDrive no tiene una sesión iniciada: los archivos se quedan en este equipo hasta que inicies sesión";

    /// <summary>
    /// The OneDrive folder can exist while no account is signed in (then nothing is uploaded).
    /// A signed-in account records its e-mail under its Accounts key.
    /// </summary>
    public static bool OneDriveSignedIn()
    {
        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        foreach (var name in accounts?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var account = accounts!.OpenSubKey(name);
            if (account?.GetValue("UserEmail") is string { Length: > 0 })
                return true;
        }
        return false;
    }

    /// <summary>A caveat about syncing <paramref name="path"/>, or null when there is none.</summary>
    public static string? SyncWarningFor(string path) =>
        SyncProviderOf(path) == "OneDrive" && !OneDriveSignedIn() ? OneDriveNotSignedIn : null;

    public static string? SyncProviderOf(string path)
    {
        string full = Normalize(path);
        if (OneDriveRoots().Any(r => IsUnder(full, r)))
            return "OneDrive";
        if (GoogleDriveRoot() is { } g && IsUnder(full, g))
            return "Google Drive";
        return null;
    }

    /// <summary>OneDrive roots from the environment and the client's account registry (personal and work).</summary>
    public static IReadOnlyList<string> OneDriveRoots()
    {
        var roots = new List<string>();
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            Add(roots, Environment.GetEnvironmentVariable(name));

        using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        foreach (var accountName in accounts?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var account = accounts!.OpenSubKey(accountName);
            Add(roots, account?.GetValue("UserFolder") as string);
        }
        return roots;
    }

    /// <summary>
    /// The "My Drive" folder of Google Drive for desktop. Its mount letter is stored per account in
    /// the DriveFS registry key (default G:); the folder name follows the Windows language.
    /// </summary>
    public static string? GoogleDriveRoot()
    {
        var letters = new List<string>();
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Google\DriveFS"))
        {
            if (key?.GetValue("PerAccountPreferences") is string json)
                letters.AddRange(MountLettersFrom(json));
            if (key?.GetValue("DefaultMountPoint") is string mount)
                letters.Add(mount.TrimEnd(':', '\\'));
        }
        letters.Add("G");

        var candidates = letters.Distinct(StringComparer.OrdinalIgnoreCase).Select(l => $"{l}:\\")
            .Concat(DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Network).Select(d => d.RootDirectory.FullName));
        foreach (var root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var myDrive in new[] { "My Drive", "Mi unidad" })
            {
                try
                {
                    string path = Path.Combine(root, myDrive);
                    if (Directory.Exists(path))
                        return path;
                }
                catch (IOException)
                {
                    // Disconnected network drive.
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> MountLettersFrom(string json)
    {
        var letters = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("per_account_preferences", out var accounts))
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    if (account.TryGetProperty("value", out var value)
                        && value.TryGetProperty("mount_point_path", out var mount)
                        && mount.GetString() is { Length: > 0 } letter)
                        letters.Add(letter.TrimEnd(':', '\\'));
                }
            }
        }
        catch (JsonException)
        {
            // Unknown format in a future Drive version: fall back to scanning drives.
        }
        return letters;
    }

    private static void Add(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        string full = Normalize(path);
        if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            roots.Add(full);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
