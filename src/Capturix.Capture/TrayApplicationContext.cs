using System.Diagnostics;
using Capturix.Common;
using Capturix.Common.Ipc;
using Capturix.Common.Storage;
using Capturix.Core.Capture;
using Capturix.Core.Hotkeys;
using Capturix.Core.Interop;
using Capturix.Core.Recording;
using Capturix.Core.Storage;

namespace Capturix.Capture;

/// <summary>
/// Lives in the notification area: owns the hotkeys, runs captures and hands results
/// to the store / clipboard. It is the always-running half of Capturix; the editor is the other.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    // Time for a just-closed tray menu to disappear before the screen is grabbed.
    private const int MenuCloseDelayMs = 250;

    private AppSettings _settings;
    private CaptureStore _store;
    private readonly List<(ToolStripMenuItem Item, Func<AppSettings, bool> Get)> _toggles = new();
    private ToolStripMenuItem _themeMenu = null!;
    private ToolStripMenuItem _storageMenu = null!;
    private bool _refreshingMenu;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys = new();
    private readonly Icon _icon = AppIcon.Create();
    private bool _capturing;
    private string? _lastSavedPath;
    private bool _lastBalloonWasText;
    private RecordingSession? _recording;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();
        _settings.Save(); // writes defaults on first run so the user can edit them
        _store = new CaptureStore(_settings.ResolvedLibraryFolder);

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Capturix",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _ = CaptureRegionAsync(MenuCloseDelayMs);
        };
        // Text balloons have no file behind them; clicking one must not reveal an older capture.
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (!_lastBalloonWasText)
                OpenOrRevealLastCapture();
        };

        RegisterHotkeys();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Command("Capturar región", _settings.HotkeyRegion, (_, _) => _ = CaptureRegionAsync(MenuCloseDelayMs)));
        menu.Items.Add(Command("Pantalla completa", _settings.HotkeyFullScreen, (_, _) => _ = CaptureFullScreenAsync(MenuCloseDelayMs)));
        menu.Items.Add(Command("Ventana activa", _settings.HotkeyActiveWindow, (_, _) => _ = CaptureActiveWindowAsync(MenuCloseDelayMs)));
        menu.Items.Add(Command("Captura con desplazamiento", _settings.HotkeyScrolling, (_, _) => _ = CaptureScrollingAsync(MenuCloseDelayMs)));
        menu.Items.Add(Command("Capturar texto (OCR)", _settings.HotkeyCaptureText, (_, _) => _ = CaptureTextAsync(MenuCloseDelayMs)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Command("Grabar vídeo (MP4)", _settings.HotkeyRecordVideo, (_, _) => _ = ToggleRecordingAsync(RecordingFormat.Mp4, MenuCloseDelayMs)));
        menu.Items.Add(Command("Grabar GIF animado", _settings.HotkeyRecordGif, (_, _) => _ = ToggleRecordingAsync(RecordingFormat.Gif, MenuCloseDelayMs)));
        menu.Items.Add(MakeToggle("Grabar sonido del sistema", s => s.RecordSystemAudio, (s, v) => s.RecordSystemAudio = v));
        menu.Items.Add(MakeToggle("Grabar micrófono", s => s.RecordMicrophone, (s, v) => s.RecordMicrophone = v));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir editor", null, (_, _) => TryOpenEditor());
        menu.Items.Add("Abrir carpeta de capturas", null, (_, _) => OpenLibraryFolder());
        _storageMenu = new ToolStripMenuItem("Guardar capturas en");
        _storageMenu.DropDownItems.Add("(cargando)"); // filled on every opening
        _storageMenu.DropDown.ShowItemToolTips = true;
        menu.Items.Add(_storageMenu);
        _themeMenu = new ToolStripMenuItem("Tema");
        foreach (var mode in ThemeMode.All)
        {
            var item = new ToolStripMenuItem(mode == ThemeMode.System ? "Sistema (sigue a Windows)" : ThemeMode.DisplayName(mode)) { Tag = mode };
            item.Click += (_, _) => _settings = AppSettings.Update(s => s.Theme = mode);
            _themeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_themeMenu);
        menu.Items.Add("Editar ajustes (settings.json)", null, (_, _) => OpenSettingsFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RefreshMenu(menu);
        MenuTheme.Apply(menu, ThemeMode.IsDark(_settings.Theme));
        return menu;
    }

    /// <summary>Menu entry with its hotkey right-aligned (WinForms ignores "\t" in item text).</summary>
    private static ToolStripMenuItem Command(string text, string hotkey, EventHandler onClick) =>
        new(text, null, onClick) { ShortcutKeyDisplayString = hotkey };

    /// <summary>
    /// Re-reads settings.json each time the menu opens: the editor may have changed the theme,
    /// and the cloud apps may have been installed or removed since the last time.
    /// </summary>
    private void RefreshMenu(ContextMenuStrip menu)
    {
        _settings = AppSettings.Load();
        _refreshingMenu = true;
        foreach (var (item, get) in _toggles)
            item.Checked = get(_settings);
        _refreshingMenu = false;

        foreach (ToolStripMenuItem item in _themeMenu.DropDownItems)
            item.Checked = (string)item.Tag! == _settings.Theme;

        _storageMenu.DropDownItems.Clear();
        foreach (var target in StorageLocations.GetTargets(_settings.LibraryFolder))
            _storageMenu.DropDownItems.Add(MakeStorageItem(target));
        _storageMenu.DropDownItems.Add(new ToolStripSeparator());
        string sync = StorageLocations.SyncProviderOf(_store.Folder) is { } provider
            ? StorageLocations.SyncWarningFor(_store.Folder) is null ? $"se sincroniza con {provider}" : $"{provider} sin sesión iniciada"
            : "solo en este equipo";
        _storageMenu.DropDownItems.Add(new ToolStripMenuItem($"Ahora: {_store.Folder} ({sync})") { Enabled = false });

        MenuTheme.Apply(menu, ThemeMode.IsDark(_settings.Theme));
    }

    private ToolStripMenuItem MakeStorageItem(StorageTarget target)
    {
        string text = target.Kind switch
        {
            SaveLocation.OneDrive when !target.IsAvailable => "OneDrive (no está instalado)",
            SaveLocation.GoogleDrive when !target.IsAvailable => "Google Drive (instala Google Drive para escritorio)",
            SaveLocation.Custom => target.IsAvailable ? $"Otra carpeta: {target.Folder}…" : "Otra carpeta…",
            _ => $"{target.DisplayName} — {target.Folder}",
        };
        if (target.Warning is not null)
            text += "  ⚠ sin sesión iniciada";
        var item = new ToolStripMenuItem(text)
        {
            ToolTipText = target.Warning,
            Checked = target.Kind == _settings.SaveLocation,
            Enabled = target.IsAvailable || target.Kind == SaveLocation.Custom,
        };
        item.Click += (_, _) =>
        {
            if (target.Kind == SaveLocation.Custom)
                _ = ChooseCustomFolderAsync();
            else
                _ = ChangeSaveLocationAsync(target.Kind, null);
        };
        return item;
    }

    private async Task ChooseCustomFolderAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Carpeta donde guardar las capturas",
            UseDescriptionForTitle = true,
            InitialDirectory = _store.Folder,
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            await ChangeSaveLocationAsync(SaveLocation.Custom, dialog.SelectedPath);
    }

    /// <summary>
    /// Switches the library folder, offering to bring the existing captures along. Captures in a
    /// synced folder are uploaded by the OneDrive / Google Drive app, not by Capturix.
    /// </summary>
    private async Task ChangeSaveLocationAsync(string kind, string? customFolder)
    {
        string oldFolder = _store.Folder;
        string newFolder = StorageLocations.Resolve(kind, customFolder ?? _settings.LibraryFolder);
        bool sameFolder = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldFolder)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(newFolder)), StringComparison.OrdinalIgnoreCase);

        bool move = false;
        var existing = sameFolder ? Array.Empty<string>() : LibraryMover.CapturesIn(oldFolder);
        if (existing.Count > 0)
        {
            var answer = MessageBox.Show(
                $"Hay {existing.Count} capturas en:\n{oldFolder}\n\n¿Quieres moverlas a la nueva ubicación?\n{newFolder}\n\n" +
                "Sí: moverlas.   No: dejarlas donde están.   Cancelar: no cambiar nada.",
                "Capturix — ubicación de las capturas", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
                return;
            move = answer == DialogResult.Yes;
        }

        try
        {
            _settings = AppSettings.Update(s =>
            {
                s.SaveLocation = kind;
                if (customFolder is not null)
                    s.LibraryFolder = customFolder;
            });
            _store = new CaptureStore(newFolder);
            Log.Info($"Ubicación de capturas: {kind} → {newFolder}");

            string where = StorageLocations.SyncWarningFor(newFolder)
                           ?? (StorageLocations.SyncProviderOf(newFolder) is { } provider
                               ? $"La app de {provider} las subirá a la nube."
                               : "Se quedan solo en este equipo.");
            if (move)
            {
                var (moved, failed) = await Task.Run(() => LibraryMover.MoveAll(oldFolder, newFolder));
                ShowBalloon(5000, "Capturas movidas",
                    $"{moved} movidas a {newFolder}" + (failed.Count > 0 ? $" ({failed.Count} no se pudieron mover)" : "") + $"\n{where}",
                    failed.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            else
            {
                ShowBalloon(4000, "Ubicación cambiada", $"Las capturas nuevas se guardarán en {newFolder}\n{where}", ToolTipIcon.Info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("No se pudo cambiar la ubicación de las capturas", ex);
            ShowBalloon(5000, "Capturix — error", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>Checkable menu entry bound to a setting; changes are saved immediately.</summary>
    private ToolStripMenuItem MakeToggle(string text, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get(_settings), CheckOnClick = true };
        item.CheckedChanged += (_, _) =>
        {
            if (!_refreshingMenu)
                _settings = AppSettings.Update(s => set(s, item.Checked));
        };
        _toggles.Add((item, get));
        return item;
    }

    private void RegisterHotkeys()
    {
        var failed = new List<string>();
        TryRegister(_settings.HotkeyRegion, () => _ = CaptureRegionAsync(0), failed);
        TryRegister(_settings.HotkeyFullScreen, () => _ = CaptureFullScreenAsync(0), failed);
        TryRegister(_settings.HotkeyActiveWindow, () => _ = CaptureActiveWindowAsync(0), failed);
        TryRegister(_settings.HotkeyScrolling, () => _ = CaptureScrollingAsync(0), failed);
        TryRegister(_settings.HotkeyCaptureText, () => _ = CaptureTextAsync(0), failed);
        TryRegister(_settings.HotkeyRecordVideo, () => _ = ToggleRecordingAsync(RecordingFormat.Mp4, 0), failed);
        TryRegister(_settings.HotkeyRecordGif, () => _ = ToggleRecordingAsync(RecordingFormat.Gif, 0), failed);

        if (failed.Count > 0)
        {
            ShowBalloon(5000, "Capturix",
                "No se pudieron registrar estos atajos (otra aplicación los usa o son inválidos): " +
                string.Join(", ", failed), ToolTipIcon.Warning);
        }
    }

    private void TryRegister(string text, Action handler, List<string> failed)
    {
        bool ok = HotkeyBinding.TryParse(text, out var binding) && _hotkeys.Register(binding, handler);
        Log.Info($"Atajo {text}: {(ok ? "registrado" : "NO registrado")}");
        if (!ok)
            failed.Add(text);
    }

    // ---- capture modes ------------------------------------------------------------------

    /// <summary>A captured image plus a human-readable description of where it came from.</summary>
    private sealed record CaptureResult(Bitmap Image, string Source);

    private Task CaptureRegionAsync(int delayMs) => RunCaptureAsync(delayMs, () =>
    {
        if (SelectArea() is not { } selection)
            return null;
        var (frozen, bounds, rect, window) = selection;
        using (frozen)
        {
            rect.Offset(-bounds.X, -bounds.Y);
            // Crop from the frozen frame, so the result is exactly what the user saw.
            return new CaptureResult(ScreenCapture.Crop(frozen, rect),
                window is null ? "Región" : $"Ventana: {window.Title}");
        }
    });

    /// <summary>Shows the region selector; the caller owns the returned frozen screenshot.</summary>
    private static (Bitmap Frozen, Rectangle Bounds, Rectangle ScreenRect, WindowInfo? Window)? SelectArea()
    {
        var bounds = ScreenCapture.VirtualScreen;
        var windows = WindowEnumerator.GetVisibleWindows();
        var frozen = ScreenCapture.CaptureVirtualScreen();
        using var selector = new RegionSelectorForm(frozen, bounds, windows);
        if (selector.ShowDialog() != DialogResult.OK)
        {
            frozen.Dispose();
            return null;
        }
        var screenRect = selector.SelectedScreenRect;
        var window = windows.FirstOrDefault(w => Rectangle.Intersect(w.Bounds, bounds) == screenRect);
        return (frozen, bounds, screenRect, window);
    }

    /// <summary>
    /// Pick a window or region, then scroll it to the end and stitch the frames.
    /// Picking a whole window works too: its fixed toolbars and status bars are detected
    /// as static bands and appear only once.
    /// </summary>
    private Task CaptureScrollingAsync(int delayMs) => RunCaptureAsync(delayMs, async () =>
    {
        if (SelectArea() is not { } selection)
            return null;
        selection.Frozen.Dispose();

        using var status = new ScrollStatusForm(selection.ScreenRect);
        status.Show();
        var progress = new Progress<string>(status.SetStatus);
        var image = await new ScrollingCapture().RunAsync(selection.ScreenRect, progress, CancellationToken.None);
        string source = selection.Window is null ? "Desplazamiento" : $"Desplazamiento: {selection.Window.Title}";
        return new CaptureResult(image, source);
    });

    /// <summary>
    /// Select an area and copy its text to the clipboard. Unlike the other modes nothing is
    /// saved to the library: only the recognized text leaves the screen.
    /// </summary>
    private async Task CaptureTextAsync(int delayMs)
    {
        if (_capturing)
            return;
        _capturing = true;
        Bitmap? image = null;
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);
            if (SelectArea() is not { } selection)
            {
                Log.Info("Captura de texto cancelada");
                return;
            }
            var (frozen, bounds, rect, _) = selection;
            using (frozen)
            {
                rect.Offset(-bounds.X, -bounds.Y);
                image = ScreenCapture.Crop(frozen, rect);
            }
            _capturing = false; // the pixels are in hand; hotkeys work again while OCR runs

            var sw = Stopwatch.StartNew();
            var outcome = await BitmapOcr.RecognizeAsync(image);
            Log.Info($"OCR de {image.Width}x{image.Height}: {outcome.Lines.Count} líneas, idioma {outcome.Language}, {sw.ElapsedMilliseconds} ms");
            if (outcome.Lines.Count == 0)
            {
                ShowTextBalloon("Texto: nada encontrado", "No se reconoció texto en la zona seleccionada.", ToolTipIcon.Warning);
                return;
            }
            Clipboard.SetText(outcome.Text);
            string preview = outcome.Text.Length > 180 ? outcome.Text[..180] + "…" : outcome.Text;
            ShowTextBalloon($"Texto copiado ({outcome.Lines.Count} líneas)", preview, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("La captura de texto falló", ex);
            ShowTextBalloon("Capturix — error de OCR", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _capturing = false;
            image?.Dispose();
        }
    }

    private void ShowTextBalloon(string title, string text, ToolTipIcon icon)
    {
        _tray.ShowBalloonTip(4000, title, text, icon);
        _lastBalloonWasText = true;
    }

    private void ShowBalloon(int timeoutMs, string title, string text, ToolTipIcon icon)
    {
        _tray.ShowBalloonTip(timeoutMs, title, text, icon);
        _lastBalloonWasText = false;
    }

    private Task CaptureFullScreenAsync(int delayMs) => RunCaptureAsync(delayMs, () =>
        _settings.FullScreenMode == "cursor"
            ? new CaptureResult(ScreenCapture.CaptureMonitorUnderCursor(), "Monitor")
            : new CaptureResult(ScreenCapture.CaptureVirtualScreen(), "Pantalla completa"));

    private Task CaptureActiveWindowAsync(int delayMs) => RunCaptureAsync(delayMs, () =>
    {
        string title = Native.GetWindowTitle(Native.GetForegroundWindow());
        return new CaptureResult(ScreenCapture.CaptureForegroundWindow(), $"Ventana: {title}");
    });

    private Task RunCaptureAsync(int delayMs, Func<CaptureResult?> capture) =>
        RunCaptureAsync(delayMs, () => Task.FromResult(capture()));

    private async Task RunCaptureAsync(int delayMs, Func<Task<CaptureResult?>> capture)
    {
        if (_capturing)
            return; // ignore a second hotkey press while the selector is open
        _capturing = true;
        CaptureResult? result = null;
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);

            var sw = Stopwatch.StartNew();
            result = await capture();
            if (result is null)
            {
                Log.Info("Captura cancelada");
                return;
            }
            Log.Info($"Captura de {result.Image.Width}x{result.Image.Height} ({result.Source}) en {sw.ElapsedMilliseconds} ms");

            // The clipboard needs the UI (STA) thread; it is fast, so do it right away.
            if (_settings.CopyToClipboard)
                Clipboard.SetImage(result.Image);
        }
        catch (Exception ex)
        {
            result?.Image.Dispose();
            Log.Error("La captura falló", ex);
            ShowBalloon(5000, "Capturix — error", ex.Message, ToolTipIcon.Error);
            return;
        }
        finally
        {
            // Free the hotkeys as soon as the pixels are in hand; saving continues below.
            _capturing = false;
        }

        await SaveAndOpenAsync(result);
    }

    /// <summary>
    /// Encodes and writes the .cpx on a worker thread (OneDrive-synced folders can take seconds),
    /// then hands the file to the editor.
    /// </summary>
    private async Task SaveAndOpenAsync(CaptureResult result)
    {
        var image = result.Image;
        try
        {
            var sw = Stopwatch.StartNew();
            string path = await Task.Run(() => _store.SaveCapture(image, result.Source, _settings.SavePngCopy));
            _lastSavedPath = path;
            Log.Info($"Guardada {path} en {sw.ElapsedMilliseconds} ms");

            if (_settings.OpenEditorAfterCapture && TryOpenEditor(path))
                return; // the editor window is the feedback; no balloon needed

            if (_settings.ShowNotification)
            {
                ShowBalloon(3000, "Captura guardada",
                    $"{image.Width} × {image.Height} — {Path.GetFileName(path)}\nHaz clic para verla.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar la captura", ex);
            ShowBalloon(5000, "Capturix — error al guardar", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            image.Dispose();
        }
    }

    // ---- shell helpers -------------------------------------------------------------------

    private static string EditorPath => Path.Combine(AppContext.BaseDirectory, "Capturix.Editor.exe");

    /// <summary>
    /// Hands <paramref name="path"/> to the running editor over its pipe, or starts the editor
    /// if none is running. Returns false if the editor is not installed.
    /// </summary>
    private static bool TryOpenEditor(string? path = null)
    {
        // Without this, Windows' focus-stealing guard keeps the editor behind other windows.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        if (EditorPipe.TrySend(path ?? ""))
            return true;

        if (!File.Exists(EditorPath))
        {
            Log.Error($"No se encontró el editor en {EditorPath}");
            return false;
        }
        var start = new ProcessStartInfo(EditorPath) { UseShellExecute = false };
        if (path is not null)
            start.ArgumentList.Add(path);
        Process.Start(start);
        return true;
    }

    /// <summary>Balloon click: recordings open in the default player, captures are shown in Explorer.</summary>
    private void OpenOrRevealLastCapture()
    {
        if (_lastSavedPath is null || !File.Exists(_lastSavedPath))
            return;
        string ext = Path.GetExtension(_lastSavedPath).ToLowerInvariant();
        if (ext is ".mp4" or ".gif")
            Process.Start(new ProcessStartInfo(_lastSavedPath) { UseShellExecute = true });
        else
            Process.Start("explorer.exe", $"/select,\"{_lastSavedPath}\"");
    }

    // ---- recording ---------------------------------------------------------------------

    /// <summary>
    /// First press: pick an area and record it after a 3-second countdown. Second press (or the
    /// Stop button): stop and save into the library.
    /// </summary>
    private async Task ToggleRecordingAsync(RecordingFormat format, int delayMs)
    {
        if (_recording is not null)
        {
            await _recording.StopAsync();
            return;
        }
        if (_capturing)
            return;

        _capturing = true;
        RecordingSession? session = null;
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);
            if (SelectArea() is not { } selection)
                return;
            selection.Frozen.Dispose();

            string path = _store.NextRecordingPath(format == RecordingFormat.Mp4 ? ".mp4" : ".gif");
            int fps = format == RecordingFormat.Mp4 ? _settings.VideoFps : _settings.GifFps;
            session = new RecordingSession(selection.ScreenRect, format, path, fps, _settings.RecordCursor,
                _settings.RecordSystemAudio, _settings.RecordMicrophone);
            _recording = session;
            _capturing = false; // other screenshots stay available while recording
            session.Begin();

            string? saved = await session.Completion;
            if (saved is null)
            {
                Log.Info("Grabación cancelada durante la cuenta atrás");
                return;
            }
            _lastSavedPath = saved;
            var size = new FileInfo(saved).Length / 1024.0 / 1024.0;
            ShowBalloon(4000, format == RecordingFormat.Mp4 ? "Vídeo guardado" : "GIF guardado",
                $"{Path.GetFileName(saved)} — {size:0.0} MB\nHaz clic para abrirlo.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("La grabación falló", ex);
            ShowBalloon(5000, "Capturix — error al grabar", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _capturing = false;
            _recording = null;
            session?.Dispose();
        }
    }

    private void OpenLibraryFolder() =>
        Process.Start(new ProcessStartInfo(_store.Folder) { UseShellExecute = true });

    private static void OpenSettingsFile() =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.SettingsPath}\""));

    protected override void ExitThreadCore()
    {
        // Quitting mid-recording still finalizes the file instead of leaving a broken MP4.
        _recording?.StopForExit();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
