using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capturix.Common.Storage;

namespace Capturix.Common;

/// <summary>User preferences, stored as JSON in %APPDATA%\Capturix\settings.json.</summary>
public sealed class AppSettings
{
    public string HotkeyRegion { get; set; } = "Ctrl+Shift+A";
    public string HotkeyFullScreen { get; set; } = "Ctrl+Shift+F";
    public string HotkeyActiveWindow { get; set; } = "Ctrl+Shift+W";
    public string HotkeyScrolling { get; set; } = "Ctrl+Shift+D";

    /// <summary>Select an area and copy its text (OCR) to the clipboard; no image is saved.</summary>
    public string HotkeyCaptureText { get; set; } = "Ctrl+Shift+T";

    /// <summary>Starts an MP4 recording; pressing it again stops it.</summary>
    public string HotkeyRecordVideo { get; set; } = "Ctrl+Shift+R";

    /// <summary>Starts an animated-GIF recording; pressing it again stops it.</summary>
    public string HotkeyRecordGif { get; set; } = "Ctrl+Shift+G";

    public int VideoFps { get; set; } = 30;
    public int GifFps { get; set; } = 12;
    public bool RecordCursor { get; set; } = true;

    /// <summary>MP4 recordings include what the speakers play.</summary>
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>MP4 recordings include the default microphone (off by default for privacy).</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>"system", "light" or "dark" (see <see cref="ThemeMode"/>); shared by the tray and the editor.</summary>
    public string Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// "local", "onedrive", "googledrive" or "custom" (see <see cref="Storage.SaveLocation"/>).
    /// Null only in settings written before this option existed; <see cref="Load"/> migrates it.
    /// </summary>
    public string? SaveLocation { get; set; }

    /// <summary>The folder used when <see cref="SaveLocation"/> is "custom".</summary>
    public string LibraryFolder { get; set; } = "";

    public bool CopyToClipboard { get; set; } = true;
    public bool ShowNotification { get; set; } = true;

    /// <summary>Open each new capture in Capturix.Editor.exe right after it is taken.</summary>
    public bool OpenEditorAfterCapture { get; set; } = true;

    /// <summary>Also write a plain PNG next to each editable .cpx capture.</summary>
    public bool SavePngCopy { get; set; }

    /// <summary>"all" = every monitor, "cursor" = only the monitor under the mouse.</summary>
    public string FullScreenMode { get; set; } = "all";

    [JsonIgnore]
    [SupportedOSPlatform("windows")]
    public string ResolvedLibraryFolder => StorageLocations.Resolve(SaveLocation ?? Storage.SaveLocation.Local, LibraryFolder);

    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capturix");

    public static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(ReadShared(SettingsPath), JsonOptions) ?? new AppSettings { SaveLocation = Storage.SaveLocation.Local }
                : new AppSettings { SaveLocation = Storage.SaveLocation.Local };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt or busy settings file falls back to defaults instead of blocking startup.
            settings = new AppSettings { SaveLocation = Storage.SaveLocation.Local };
        }

        settings.Theme = ThemeMode.Normalize(settings.Theme);
        if (settings.SaveLocation is null)
        {
            // Settings from before the save-location option: keep the library where it was
            // (an empty folder meant Documents\Capturix) so existing captures stay visible.
            settings.SaveLocation = Storage.SaveLocation.Custom;
            if (string.IsNullOrWhiteSpace(settings.LibraryFolder))
                settings.LibraryFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Capturix");
        }
        return settings;
    }

    /// <summary>Written to a temporary file and swapped in, so the other process never reads half a file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    /// <summary>
    /// Changes settings on top of the latest file contents. The tray and the editor both write
    /// settings.json; re-reading first keeps one from undoing the other's changes.
    /// </summary>
    public static AppSettings Update(Action<AppSettings> change)
    {
        var fresh = Load();
        change(fresh);
        fresh.Save();
        return fresh;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
