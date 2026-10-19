using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capturix.Common;
using Capturix.Common.Format;
using Capturix.Common.Shell;

namespace Capturix.Editor.Library;

/// <summary>One capture in the library tray.</summary>
public sealed class LibraryItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public LibraryItem(string path, DateTime modified)
    {
        Path = path;
        Modified = modified;
    }

    public string Path { get; }
    public DateTime Modified { get; set; }
    public string Label => Modified.ToString("dd/MM HH:mm");
    public string ToolTip => $"{System.IO.Path.GetFileName(Path)}\n{Modified:G}" + (IsMedia ? "\nClic: abrir en el reproductor" : "");
    public bool IsEditable => CpxFile.IsCpx(Path);

    /// <summary>Recordings are listed too, but open in the default player rather than the editor.</summary>
    public bool IsMedia => IsMediaPath(Path);

    public string Badge => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
    {
        ".mp4" => "▶ Vídeo",
        ".gif" => "GIF",
        _ => "",
    };

    public static bool IsMediaPath(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".gif";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Keeps an observable, newest-first list of the captures in the library folder in sync
/// with the disk and loads thumbnails off the UI thread.
/// </summary>
public sealed class LibraryTray : IDisposable
{
    private const int MaxItems = 300;
    private const int ThumbnailHeight = 144; // decoded at 2× the displayed height for sharp HiDPI
    private static readonly string[] Extensions = { CpxFile.Extension, ".png", ".jpg", ".jpeg", ".gif", ".mp4" };

    private readonly Dispatcher _dispatcher;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;

    public string Folder { get; }
    public ObservableCollection<LibraryItem> Items { get; } = new();

    public LibraryTray(string folder, Dispatcher dispatcher)
    {
        Folder = folder;
        _dispatcher = dispatcher;
        Directory.CreateDirectory(folder);

        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Refresh();
        }, dispatcher);
        _debounce.Stop();

        try
        {
            _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            _watcher.Created += OnDiskChange;
            _watcher.Changed += OnDiskChange;
            _watcher.Deleted += OnDiskChange;
            _watcher.Renamed += OnDiskChange;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Log.Error("No se pudo vigilar la carpeta de la biblioteca", ex);
        }

        Refresh();
    }

    private void OnDiskChange(object sender, FileSystemEventArgs e)
    {
        // Autosave writes through hidden .tmp files; only react to real captures.
        if (!IsCapture(e.FullPath) && !(e is RenamedEventArgs r && IsCapture(r.FullPath)))
            return;
        _dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private static bool IsCapture(string path) =>
        !System.IO.Path.GetFileName(path).StartsWith('.') &&
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Syncs the list with the folder in place, so selection and scroll position survive.</summary>
    public void Refresh()
    {
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(Folder).EnumerateFiles()
                .Where(f => IsCapture(f.FullName))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(MaxItems)
                .ToList();
        }
        catch (IOException ex)
        {
            Log.Error("No se pudo leer la biblioteca", ex);
            return;
        }

        var existing = Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var wanted = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Items[i].Path))
                Items.RemoveAt(i);
        }

        for (int index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (existing.TryGetValue(file.FullName, out var item))
            {
                if (item.Modified != file.LastWriteTime)
                {
                    item.Modified = file.LastWriteTime;
                    LoadThumbnailAsync(item);
                }
                int current = Items.IndexOf(item);
                if (current != index)
                    Items.Move(current, index);
            }
            else
            {
                item = new LibraryItem(file.FullName, file.LastWriteTime);
                Items.Insert(index, item);
                LoadThumbnailAsync(item);
            }
        }
    }

    private static void LoadThumbnailAsync(LibraryItem item)
    {
        string path = item.Path;
        var load = System.IO.Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? RunOnStaThread(() => DecodeShellThumbnail(path)) // shell thumbnail providers expect STA
            : Task.Run(() => CpxFile.IsCpx(path) ? DecodeCpxThumbnail(path) : DecodeImageThumbnail(path));

        load.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                item.Thumbnail = t.Result;
            else if (t.Exception?.InnerException is { } ex and not (IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException))
                Log.Error($"Miniatura de {path}", ex); // the usual case is a file still being written
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static Task<BitmapSource?> RunOnStaThread(Func<BitmapSource?> work)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>A frame of the video, as Explorer would show it.</summary>
    private static BitmapSource? DecodeShellThumbnail(string path)
    {
        IntPtr hbitmap = ShellThumbnail.GetHBitmap(path, ThumbnailHeight * 2);
        if (hbitmap == IntPtr.Zero)
            return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            ShellThumbnail.DeleteBitmap(hbitmap);
        }
    }

    private static BitmapSource? DecodeCpxThumbnail(string path)
    {
        var bytes = CpxFile.ReadThumbnail(path);
        if (bytes is null)
            return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelHeight = ThumbnailHeight;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource DecodeImageThumbnail(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelHeight = ThumbnailHeight;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _watcher?.Dispose();
    }
}
