using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capturix.Common;
using Capturix.Common.Format;
using Capturix.Common.Storage;
using Capturix.Editor.Controls;
using Capturix.Editor.Library;
using Capturix.Editor.Ocr;
using Capturix.Editor.Theming;
using Capturix.Editor.Model;
using Microsoft.Win32;

namespace Capturix.Editor;

public partial class MainWindow : Window
{
    private static readonly Color[] PaletteColors =
    {
        Color.FromRgb(0xE8, 0x1E, 0x1E), // red
        Color.FromRgb(0xFF, 0x8C, 0x00), // orange
        Color.FromRgb(0xFF, 0xD8, 0x00), // yellow
        Color.FromRgb(0x2E, 0xCC, 0x40), // green
        Color.FromRgb(0x00, 0x99, 0xFF), // blue
        Color.FromRgb(0xB1, 0x0D, 0xC9), // purple
        Colors.Black,
        Colors.White,
    };

    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;
    private const double ZoomStep = 1.15;

    private EditorDocument? _document;
    private double _zoom = 1;
    private Color _currentColor = PaletteColors[0];

    private TextBox? _textBox;
    private TextAnnotation? _editingText;
    private bool _editingIsNew;
    private string _textBeforeEdit = "";

    private bool _ocrRunning;

    private LibraryTray _library;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly FileSystemWatcher? _settingsWatcher;
    private readonly DispatcherTimer _settingsDebounce;
    private bool _syncingLibrarySelection;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Track(this);
        foreach (var toolBar in LogicalTreeHelper.GetChildren((DependencyObject)Content).OfType<DependencyObject>()
                     .SelectMany(Descendants).OfType<ToolBar>())
            HideIdleOverflowButton(toolBar);
        BuildPalette();
        BuildStampPicker();
        EditorView.CurrentColor = _currentColor;
        EditorView.DocumentEdited += (_, _) => RefreshUi();
        EditorView.SelectionChanged += (_, _) => SyncToolbarWithSelection();
        EditorView.TextEditRequested += (_, e) => BeginTextEdit(e.Annotation, e.IsNew);
        EditorView.OcrZoneSelected += async (_, zone) => await ExtractTextAsync(zone);
        EditorView.PointerMoved += (_, p) =>
            StatusPointer.Text = _document is null ? "" : $"{(int)p.X}, {(int)p.Y} px";

        // Edits to library documents are saved shortly after the user stops changing them.
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            AutosaveNow();
        };

        _library = BindLibrary(AppSettings.Load().ResolvedLibraryFolder);

        // The tray app changes the theme and the save location in settings.json; follow it live.
        _settingsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _settingsDebounce.Tick += (_, _) =>
        {
            _settingsDebounce.Stop();
            ApplySettings();
        };
        _settingsWatcher = WatchSettings();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ThemeManager.Changed += (_, _) => UpdateThemeButton();
        UpdateThemeButton();

        SetZoom(1);
        RefreshUi();
    }

    // ---- document lifecycle -----------------------------------------------------------------------

    public void OpenFile(string path)
    {
        try
        {
            SetDocument(EditorDocument.Load(path));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException
                                       or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log.Error($"No se pudo abrir {path}", ex);
            MessageBox.Show(this, $"No se pudo abrir el archivo:\n{ex.Message}", "Capturix", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Handles a message from another process (capturer or a second editor launch):
    /// a path to open, or an empty string to just come to the front.
    /// </summary>
    public void HandleExternalRequest(string message)
    {
        BringToFront();
        if (string.IsNullOrWhiteSpace(message) || !File.Exists(message))
            return;
        if (LibraryItem.IsMediaPath(message))
        {
            OpenInDefaultApp(message);
            return;
        }
        if (_document?.SourcePath is { } current && string.Equals(current, message, StringComparison.OrdinalIgnoreCase))
            return;
        if (ConfirmDiscardChanges())
            OpenFile(message);
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        // Windows refuses focus stealing; a brief Topmost toggle reliably raises the window.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void SetDocument(EditorDocument document)
    {
        CommitTextEdit();
        if (_document is not null)
        {
            AutosaveNow();
            _document.Changed -= OnDocumentChanged;
        }
        _document = document;
        _document.Changed += OnDocumentChanged;
        EditorView.Document = document;
        EmptyHint.Visibility = Visibility.Collapsed;
        StatusSave.Text = document.DocumentPath is null ? "" : "Autoguardado activo";
        SyncLibrarySelection();
        RefreshUi();
        // Fit once layout knows the viewport size.
        Dispatcher.BeginInvoke(ZoomToFit, DispatcherPriority.Loaded);
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => RefreshUi();

    /// <summary>
    /// Library (.cpx) documents are autosaved, so leaving them never loses work. Plain images
    /// opened from elsewhere ask before their annotations are thrown away.
    /// </summary>
    private bool ConfirmDiscardChanges()
    {
        if (_document is null)
            return true;
        if (_document.DocumentPath is not null)
            return AutosaveNow();
        if (!_document.IsDirty)
            return true;
        var answer = MessageBox.Show(this, "La imagen tiene cambios sin guardar. ¿Quieres guardarlos?", "Capturix",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveAs(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Writes the current library document if it has unsaved changes. Returns false on failure.</summary>
    private bool AutosaveNow()
    {
        _autosaveTimer.Stop();
        if (_document is not { DocumentPath: { } path, IsDirty: true })
            return true;
        CommitTextEdit();
        try
        {
            CpxMapper.Save(_document, path);
            _document.IsDirty = false;
            StatusSave.Text = $"Guardado {DateTime.Now:HH:mm:ss}";
            RefreshUi();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Autoguardado fallido: {path}", ex);
            StatusSave.Text = "¡Error al guardar!";
            return false;
        }
    }

    private void RefreshUi()
    {
        UndoButton.IsEnabled = _document?.CanUndo == true;
        ExtractTextButton.IsEnabled = _document is not null && !_ocrRunning;
        RedoButton.IsEnabled = _document?.CanRedo == true;
        string name = _document?.SourcePath is { } p ? Path.GetFileName(p) : _document is null ? "" : "Imagen pegada";
        Title = _document is null ? "Capturix — Editor" : $"Capturix — {name}{(_document.IsDirty ? " *" : "")}";
        StatusSize.Text = _document is null ? "" : $"{_document.PixelWidth} × {_document.PixelHeight} px";

        if (_document is { DocumentPath: not null, IsDirty: true })
        {
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        }
    }

    // ---- settings shared with the tray ----------------------------------------------------------

    private FileSystemWatcher? WatchSettings()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsFolder);
            var watcher = new FileSystemWatcher(AppSettings.SettingsFolder, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _settingsDebounce.Stop();
                _settingsDebounce.Start();
            });
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, e) => changed(watcher, e);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Error("No se pueden vigilar los ajustes", ex);
            return null;
        }
    }

    private void ApplySettings()
    {
        var settings = AppSettings.Load();
        if (settings.Theme != ThemeManager.Mode)
            ThemeManager.Apply(settings.Theme);

        string folder = settings.ResolvedLibraryFolder;
        if (!string.Equals(folder, _library.Folder, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Biblioteca cambiada a {folder}");
            _library.Dispose();
            _library = BindLibrary(folder);
        }
    }

    /// <summary>"Sistema" follows Windows, so a change in Windows' colour setting re-themes the editor.</summary>
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && ThemeManager.Mode == ThemeMode.System)
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(ThemeMode.System));
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        int next = (Array.IndexOf(ThemeMode.All, ThemeManager.Mode) + 1) % ThemeMode.All.Length;
        string mode = ThemeMode.All[next];
        try
        {
            AppSettings.Update(s => s.Theme = mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("No se pudo guardar el tema", ex);
        }
        ThemeManager.Apply(mode);
    }

    private void UpdateThemeButton()
    {
        string icon = ThemeManager.Mode switch { ThemeMode.Light => "☀", ThemeMode.Dark => "☾", _ => "◐" };
        ThemeButton.Content = $"{icon} Tema: {ThemeMode.DisplayName(ThemeManager.Mode)}";
    }

    // ---- library tray -------------------------------------------------------------------------

    private LibraryTray BindLibrary(string folder)
    {
        var library = new LibraryTray(folder, Dispatcher);
        LibraryList.ItemsSource = library.Items;
        library.Items.CollectionChanged += (_, _) => LibraryCount.Text = $"{library.Items.Count} capturas";
        LibraryCount.Text = $"{library.Items.Count} capturas";
        string provider = StorageLocations.SyncProviderOf(folder) ?? "Local";
        string? warning = StorageLocations.SyncWarningFor(folder);
        LibraryLocation.Text = provider == "Local" ? "Local" : warning is null ? $"☁ {provider}" : $"⚠ {provider}";
        LibraryLocation.ToolTip = provider == "Local"
            ? $"{folder}\nSolo en este equipo"
            : $"{folder}\n{warning ?? $"La app de {provider} sincroniza esta carpeta"}";
        return library;
    }


    /// <summary>Highlights the open document in the tray without re-triggering an open.</summary>
    private void SyncLibrarySelection()
    {
        _syncingLibrarySelection = true;
        LibraryList.SelectedItem = _library.Items.FirstOrDefault(i =>
            string.Equals(i.Path, _document?.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (LibraryList.SelectedItem is not null)
            LibraryList.ScrollIntoView(LibraryList.SelectedItem);
        _syncingLibrarySelection = false;
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLibrarySelection || LibraryList.SelectedItem is not LibraryItem item)
            return;
        if (string.Equals(item.Path, _document?.SourcePath, StringComparison.OrdinalIgnoreCase))
            return;
        if (item.IsMedia)
        {
            OpenInDefaultApp(item.Path);
            SyncLibrarySelection(); // keep the open image highlighted
            return;
        }
        if (ConfirmDiscardChanges())
            OpenFile(item.Path);
        else
            SyncLibrarySelection();
    }

    private static void OpenInDefaultApp(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_library.Folder) { UseShellExecute = true });

    // ---- toolbar ------------------------------------------------------------------------------

    private void BuildPalette()
    {
        foreach (var color in PaletteColors)
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                Tag = color,
                ToolTip = "Color (se aplica también a la anotación seleccionada)",
            };
            swatch.MouseLeftButtonUp += (_, _) => SetColor(color, applyToSelection: true);
            Palette.Children.Add(swatch);
        }
        HighlightSwatch();
    }

    private void SetColor(Color color, bool applyToSelection)
    {
        _currentColor = color;
        EditorView.CurrentColor = color;
        HighlightSwatch();
        if (applyToSelection && EditorView.Selected is not null)
            EditorView.ApplyStyleToSelected(color: color);
    }

    private void HighlightSwatch()
    {
        foreach (Border swatch in Palette.Children)
        {
            bool selected = swatch.Tag is Color c && c == _currentColor;
            swatch.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0, 153, 255)) : Brushes.Gray;
            swatch.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }

    private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EditorView is null || ThicknessText is null)
            return; // fires once during InitializeComponent
        ThicknessText.Text = ((int)e.NewValue).ToString();
        EditorView.CurrentThickness = e.NewValue;
        if (EditorView.Selected is not null && Math.Abs(EditorView.Selected.Thickness - e.NewValue) > 0.01)
            EditorView.ApplyStyleToSelected(thickness: e.NewValue);
    }

    /// <summary>Selecting an annotation shows its colour and thickness in the toolbar.</summary>
    private void SyncToolbarWithSelection()
    {
        if (EditorView.Selected is not { } selected)
            return;
        if (selected is StampAnnotation stamp)
        {
            ShowCurrentStamp(stamp.Emoji);
            return; // stamps carry their own colours
        }
        SetColor(selected.Color, applyToSelection: false);
        ThicknessSlider.Value = selected.Thickness;
    }

    // ---- emoji stamps ---------------------------------------------------------------------

    private System.Windows.Controls.Primitives.Popup? _stampPopup;

    /// <summary>A popup grid with every stamp, drawn in colour by <see cref="EmojiRenderer"/>.</summary>
    private void BuildStampPicker()
    {
        var grid = new WrapPanel { Width = 6 * 44, Margin = new Thickness(6) };
        foreach (var emoji in EmojiRenderer.Stamps)
        {
            var button = new Button
            {
                Width = 40, Height = 40, Margin = new Thickness(2), Padding = new Thickness(4),
                Content = new Image { Source = EmojiRenderer.ToImage(emoji), Width = 28, Height = 28 },
                ToolTip = emoji, Tag = emoji,
                Style = (Style)FindResource(ToolBar.ButtonStyleKey),
            };
            AutomationProperties.SetName(button, $"Sello {emoji}");
            button.Click += (_, _) => ChooseStamp(emoji);
            grid.Children.Add(button);
        }
        var border = new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        border.SetResourceReference(Border.BackgroundProperty, "Theme.Panel");
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        _stampPopup = new System.Windows.Controls.Primitives.Popup
        {
            Child = border, PlacementTarget = StampPickerButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        ShowCurrentStamp(EditorView.CurrentEmoji);
    }

    private void StampPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_stampPopup is not null)
            _stampPopup.IsOpen = !_stampPopup.IsOpen;
    }

    /// <summary>Picks the emoji for new stamps; a selected stamp changes to it too.</summary>
    private void ChooseStamp(string emoji)
    {
        if (_stampPopup is not null)
            _stampPopup.IsOpen = false;
        EditorView.CurrentEmoji = emoji;
        ShowCurrentStamp(emoji);
        if (EditorView.Selected is StampAnnotation)
            EditorView.SetSelectedEmoji(emoji);
        else
            SelectTool(EditorTool.Stamp);
    }

    private void ShowCurrentStamp(string emoji) => StampPickerImage.Source = EmojiRenderer.ToImage(emoji);

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (EditorView is null || sender is not RadioButton { Tag: string tag })
            return; // the default tool is checked during InitializeComponent
        CommitTextEdit();
        EditorView.Tool = Enum.Parse<EditorTool>(tag);
    }

    private void SelectTool(EditorTool tool)
    {
        foreach (var button in FindToolButtons())
        {
            if (button.Tag is string tag && tag == tool.ToString())
                button.IsChecked = true;
        }
    }

    /// <summary>
    /// The stock overflow chevron is a light box even in dark mode; show it only while the
    /// toolbar actually has hidden items (narrow window).
    /// </summary>
    private static void HideIdleOverflowButton(ToolBar toolBar)
    {
        void Update()
        {
            if (toolBar.Template?.FindName("OverflowGrid", toolBar) is FrameworkElement grid)
                grid.Visibility = toolBar.HasOverflowItems ? Visibility.Visible : Visibility.Collapsed;
            if (toolBar.Template?.FindName("MainPanelBorder", toolBar) is FrameworkElement border)
                border.Margin = new Thickness(0);
        }
        toolBar.Loaded += (_, _) => Update();
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(ToolBar.HasOverflowItemsProperty, typeof(ToolBar))
            .AddValueChanged(toolBar, (_, _) => Update());
    }

    private IEnumerable<RadioButton> FindToolButtons() =>
        LogicalTreeHelper.GetChildren((DependencyObject)Content).OfType<DependencyObject>()
            .SelectMany(Descendants).OfType<RadioButton>();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var d in Descendants(child))
                yield return d;
        }
    }

    // ---- commands -----------------------------------------------------------------------------

    private void Open_Click(object sender, RoutedEventArgs e) => OpenWithDialog();

    private void OpenWithDialog()
    {
        if (!ConfirmDiscardChanges())
            return;
        var dialog = new OpenFileDialog
        {
            Filter = "Capturas e imágenes|*.cpx;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Documento Capturix|*.cpx|Todos los archivos|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            OpenFile(dialog.FileName);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private bool SaveAs()
    {
        if (_document is null)
            return false;
        CommitTextEdit();

        string folder = _document.SourcePath is { } src ? Path.GetDirectoryName(src)! : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string baseName = _document.SourcePath is { } s ? Path.GetFileNameWithoutExtension(s) + "-editado" : "captura";
        var dialog = new SaveFileDialog
        {
            InitialDirectory = folder,
            FileName = baseName,
            Filter = "PNG (imagen con anotaciones)|*.png|JPEG (imagen con anotaciones)|*.jpg|Documento Capturix (editable)|*.cpx",
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true)
            return false;

        string target = dialog.FileName;
        if (CpxFile.IsCpx(target))
        {
            // Saving as .cpx keeps annotations editable and makes this file the autosave target.
            CpxMapper.Save(_document, target);
            _document.DocumentPath = target;
            _document.SourcePath = target;
            _document.IsDirty = false;
            StatusSave.Text = "Autoguardado activo";
            RefreshUi();
            return true;
        }

        var flattened = DocumentRenderer.Flatten(_document);
        if (Path.GetExtension(target).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            DocumentRenderer.SaveJpeg(flattened, target);
        else
            DocumentRenderer.SavePng(flattened, target);

        // An export does not save a library document's editable state; autosave does that.
        if (_document.DocumentPath is null)
            _document.IsDirty = false;
        StatusSave.Text = $"Exportado {Path.GetFileName(target)}";
        RefreshUi();
        return true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyFlattened();

    private void CopyFlattened()
    {
        if (_document is null)
            return;
        CommitTextEdit();
        Clipboard.SetImage(DocumentRenderer.Flatten(_document));
    }

    private async void ExtractText_Click(object sender, RoutedEventArgs e) => await ExtractTextAsync(null);

    /// <summary>
    /// Reads the text of the whole image or of a zone. Works on the flattened image, so text the
    /// user hid with the pixelate tool stays hidden from OCR as well.
    /// </summary>
    private async Task ExtractTextAsync(Int32Rect? zone)
    {
        if (_document is null || _ocrRunning)
            return;
        CommitTextEdit();
        _ocrRunning = true;
        string previousStatus = StatusSave.Text;
        StatusSave.Text = "Reconociendo texto…";
        RefreshUi();
        try
        {
            var outcome = await OcrRunner.RecognizeAsync(DocumentRenderer.Flatten(_document), zone);
            string scope = zone is { } z ? $"zona {z.Width}×{z.Height} px" : "imagen completa";
            Log.Info($"OCR ({scope}): {outcome.Lines.Count} líneas, idioma {outcome.Language}");
            new OcrResultWindow(outcome, scope) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error("OCR fallido", ex);
            MessageBox.Show(this, $"No se pudo reconocer el texto:\n{ex.Message}", "Capturix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _ocrRunning = false;
            StatusSave.Text = previousStatus;
            RefreshUi();
        }
    }

    private void PasteImage()
    {
        if (!Clipboard.ContainsImage() || !ConfirmDiscardChanges())
            return;
        if (Clipboard.GetImage() is BitmapSource image)
        {
            var doc = new EditorDocument(image) { IsDirty = true };
            SetDocument(doc);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitTextEdit();
        _document?.Undo();
        RefreshUi();
    }

    private void Redo()
    {
        CommitTextEdit();
        _document?.Redo();
        RefreshUi();
    }

    // ---- zoom -------------------------------------------------------------------------------

    /// <summary>Device pixels per WPF unit on the current monitor (1.5 at 150 % scaling).</summary>
    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        // The canvas works in image pixels; tell it how big one is on screen.
        EditorView.Zoom = _zoom * DpiScale;
        ZoomText.Text = $"{Math.Round(_zoom * DpiScale * 100)} %";
    }

    /// <summary>Shows the whole image, never enlarging beyond 1 image pixel per screen pixel.</summary>
    private void ZoomToFit()
    {
        if (_document is null)
            return;
        double availableWidth = Math.Max(50, Scroller.ViewportWidth - 48);
        double availableHeight = Math.Max(50, Scroller.ViewportHeight - 48);
        double fit = Math.Min(availableWidth / _document.PixelWidth, availableHeight / _document.PixelHeight);
        SetZoom(Math.Min(fit, 1 / DpiScale));
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / ZoomStep);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomToFit();
    private void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoom(1 / DpiScale);

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        SetZoom(e.Delta > 0 ? _zoom * ZoomStep : _zoom / ZoomStep);
        e.Handled = true;
    }

    // ---- in-place text editing ------------------------------------------------------------------

    private void BeginTextEdit(TextAnnotation annotation, bool isNew)
    {
        CommitTextEdit();
        if (_document is null)
            return;

        _editingText = annotation;
        _editingIsNew = isNew;
        _textBeforeEdit = annotation.Text;
        if (!isNew)
            _document.Checkpoint();

        bool lightText = annotation.Color.R + annotation.Color.G + annotation.Color.B > 600;
        _textBox = new TextBox
        {
            Text = annotation.Text,
            FontFamily = TextAnnotation.Typeface.FontFamily,
            FontWeight = TextAnnotation.Typeface.Weight,
            FontSize = annotation.FontSize,
            Foreground = new SolidColorBrush(annotation.Color),
            Background = new SolidColorBrush(lightText ? Color.FromArgb(0xB0, 0x20, 0x20, 0x20) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 153, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = annotation.FontSize * 2,
        };
        Canvas.SetLeft(_textBox, annotation.Position.X - 3);
        Canvas.SetTop(_textBox, annotation.Position.Y - 1);
        _textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
        _textBox.LostKeyboardFocus += (_, _) => CommitTextEdit();

        // Hide the rendered text while the box shows it.
        annotation.Text = "";
        EditorView.InvalidateVisual();

        TextOverlay.Children.Add(_textBox);
        _textBox.Focus();
        _textBox.CaretIndex = _textBox.Text.Length;
    }

    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool commit = e.Key == Key.Escape || (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (commit)
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    private void CommitTextEdit()
    {
        if (_textBox is null || _editingText is null || _document is null)
            return;

        // Detach first: removing the box triggers LostKeyboardFocus, which calls back in here.
        var box = _textBox;
        var annotation = _editingText;
        _textBox = null;
        _editingText = null;
        TextOverlay.Children.Remove(box);

        string text = box.Text.TrimEnd();
        if (text.Length == 0)
        {
            _document.Remove(annotation);
            if (_editingIsNew)
                _document.DiscardCheckpoint(); // nothing was ever added
            EditorView.SelectAnnotation(null);
        }
        else
        {
            annotation.Text = text;
            if (!_editingIsNew && text == _textBeforeEdit)
                _document.DiscardCheckpoint(); // unchanged edit leaves no undo step
        }

        EditorView.InvalidateVisual();
        EditorView.Focus();
        RefreshUi();
    }

    // ---- keyboard, drag & drop, closing ------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textBox is not null && _textBox.IsKeyboardFocusWithin)
            return; // typing into a text annotation

        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift: Redo(); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.O: OpenWithDialog(); break;
                case Key.S: SaveAs(); break;
                case Key.C: CopyFlattened(); break;
                case Key.V: PasteImage(); break;
                case Key.T: _ = ExtractTextAsync(null); break;
                case Key.D0: case Key.NumPad0: ZoomToFit(); break;
                case Key.D1: case Key.NumPad1: SetZoom(1 / DpiScale); break;
                case Key.OemPlus: case Key.Add: SetZoom(_zoom * ZoomStep); break;
                case Key.OemMinus: case Key.Subtract: SetZoom(_zoom / ZoomStep); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && EditorView.Selected is not null)
        {
            double step = shift ? 10 : 1;
            var delta = e.Key switch
            {
                Key.Left => new Vector(-step, 0),
                Key.Right => new Vector(step, 0),
                Key.Up => new Vector(0, -step),
                _ => new Vector(0, step),
            };
            EditorView.NudgeSelected(delta);
            e.Handled = true;
            return;
        }

        EditorTool? tool = e.Key switch
        {
            Key.V => EditorTool.Select,
            Key.A => EditorTool.Arrow,
            Key.L => EditorTool.Line,
            Key.R => EditorTool.Rectangle,
            Key.E => EditorTool.Ellipse,
            Key.T => EditorTool.Text,
            Key.H => EditorTool.Highlight,
            Key.B => EditorTool.Pixelate,
            Key.S => EditorTool.Step,
            Key.P => EditorTool.Pen,
            Key.C => EditorTool.Crop,
            Key.O => EditorTool.Ocr,
            Key.M => EditorTool.Stamp,
            _ => null,
        };
        if (tool is EditorTool t && mods == ModifierKeys.None)
        {
            SelectTool(t);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                EditorView.DeleteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                EditorView.SelectAnnotation(null);
                e.Handled = true;
                break;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && ConfirmDiscardChanges())
            OpenFile(files[0]);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitTextEdit();
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }
        _library.Dispose();
        _settingsWatcher?.Dispose();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
