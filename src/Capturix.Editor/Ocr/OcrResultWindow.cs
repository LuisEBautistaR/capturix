using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Capturix.Ocr;

namespace Capturix.Editor.Ocr;

/// <summary>Shows recognized text so it can be corrected and copied.</summary>
public sealed class OcrResultWindow : Window
{
    private readonly TextBox _text;
    private readonly TextBlock _status;

    public OcrResultWindow(OcrOutcome outcome, string scope)
    {
        Title = "Capturix — Texto reconocido";
        Width = 560;
        Height = 420;
        MinWidth = 320;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theming.ThemeManager.Track(this);

        _text = new TextBox
        {
            Text = outcome.Text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Padding = new Thickness(6),
        };

        string summary = outcome.Lines.Count == 0
            ? "No se encontró texto."
            : $"{outcome.Lines.Count} líneas · {scope} · idioma {outcome.Language}";
        _status = new TextBlock { Text = summary, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

        var copy = new Button { Content = "Copiar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        copy.Click += (_, _) => CopyText();
        var close = new Button { Content = "Cerrar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var footer = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_text);
        Content = root;

        Loaded += (_, _) =>
        {
            _text.Focus();
            _text.SelectAll();
        };
    }

    /// <summary>Copies the selection if there is one, otherwise all the (possibly corrected) text.</summary>
    private void CopyText()
    {
        string text = _text.SelectionLength > 0 ? _text.SelectedText : _text.Text;
        if (text.Length == 0)
            return;
        Clipboard.SetText(text);
        _status.Text = $"Copiado al portapapeles ({text.Length} caracteres)";
    }
}
