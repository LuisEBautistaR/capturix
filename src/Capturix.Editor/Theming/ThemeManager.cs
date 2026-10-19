using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Capturix.Common;

namespace Capturix.Editor.Theming;

/// <summary>
/// Light/dark theme for the editor. Colours are exposed as "Theme.*" brushes that the XAML
/// references with DynamicResource, and the stock WPF controls (toolbars, text boxes, lists)
/// follow by overriding the SystemColors brushes their templates look up dynamically.
/// </summary>
public static class ThemeManager
{
    private const string MarkerKey = "Theme.Marker";
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static string Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    private sealed record Palette(
        Color Background, Color Panel, Color PanelAlt, Color Border, Color Text, Color SubtleText,
        Color Canvas, Color Control, Color Thumbnail, Color GrayText);

    private static readonly Palette Light = new(
        Background: C(0xF9F9F9), Panel: C(0xF0F0F0), PanelAlt: C(0xE9E9E9), Border: C(0xD4D4D4),
        Text: C(0x1B1B1B), SubtleText: C(0x6B6B6B), Canvas: C(0xD9D9D9), Control: C(0xFFFFFF),
        Thumbnail: C(0xFFFFFF), GrayText: C(0x8A8A8A));

    private static readonly Palette Dark = new(
        Background: C(0x1F1F1F), Panel: C(0x2B2B2B), PanelAlt: C(0x202020), Border: C(0x3A3A3A),
        Text: C(0xE8E8E8), SubtleText: C(0x9A9A9A), Canvas: C(0x181818), Control: C(0x262626),
        Thumbnail: C(0x333333), GrayText: C(0x7A7A7A));

    public static readonly Color Accent = C(0x0099FF);

    public static void Apply(string? mode)
    {
        Mode = ThemeMode.Normalize(mode);
        IsDark = ThemeMode.IsDark(Mode);

        var resources = Application.Current.Resources.MergedDictionaries;
        var old = resources.FirstOrDefault(d => d.Contains(MarkerKey));
        if (old is not null)
            resources.Remove(old);
        resources.Add(Build(IsDark ? Dark : Light, IsDark));

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Gives a window the themed background, text colour and title bar, now and after theme changes.</summary>
    public static void Track(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        window.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    /// <summary>Windows 10 2004+ / 11 draw a dark caption when asked through DWM.</summary>
    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // The caption only repaints on the next activation change; nudge it now.
        if (window.IsActive)
        {
            SendMessage(hwnd, WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(hwnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
        }
    }

    private static ResourceDictionary Build(Palette p, bool dark)
    {
        var d = new ResourceDictionary { [MarkerKey] = dark ? "dark" : "light" };
        void Brush(object key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            d[key] = b;
        }

        Brush("Theme.Background", p.Background);
        Brush("Theme.Panel", p.Panel);
        Brush("Theme.PanelAlt", p.PanelAlt);
        Brush("Theme.Border", p.Border);
        Brush("Theme.Text", p.Text);
        Brush("Theme.SubtleText", p.SubtleText);
        Brush("Theme.Canvas", p.Canvas);
        Brush("Theme.Control", p.Control);
        Brush("Theme.Thumbnail", p.Thumbnail);
        Brush("Theme.Accent", Accent);

        if (dark)
        {
            // Stock templates read these dynamically; overriding them darkens the built-in controls.
            Brush(SystemColors.ControlTextBrushKey, p.Text);
            Brush(SystemColors.WindowTextBrushKey, p.Text);
            Brush(SystemColors.WindowBrushKey, p.Control);
            Brush(SystemColors.ControlBrushKey, p.Panel);
            Brush(SystemColors.ControlLightBrushKey, p.Border);
            Brush(SystemColors.ControlLightLightBrushKey, p.Panel);
            Brush(SystemColors.ControlDarkBrushKey, p.Border);
            Brush(SystemColors.GrayTextBrushKey, p.GrayText);
            Brush(SystemColors.MenuBrushKey, p.Panel);
            Brush(SystemColors.MenuTextBrushKey, p.Text);
            Brush(SystemColors.InfoBrushKey, p.Panel);
            Brush(SystemColors.InfoTextBrushKey, p.Text);
        }

        var panel = (Brush)d["Theme.Panel"];
        var text = (Brush)d["Theme.Text"];
        d[typeof(ToolBarTray)] = Styled<ToolBarTray>(s => s.Setters.Add(new Setter(ToolBarTray.BackgroundProperty, panel)));
        d[typeof(ToolBar)] = Styled<ToolBar>(s =>
        {
            s.Setters.Add(new Setter(Control.BackgroundProperty, panel));
            s.Setters.Add(new Setter(Control.ForegroundProperty, text));
        });
        d[typeof(StatusBar)] = Styled<StatusBar>(s =>
        {
            s.Setters.Add(new Setter(Control.BackgroundProperty, panel));
            s.Setters.Add(new Setter(Control.ForegroundProperty, text));
        });
        if (dark)
            d[typeof(Button)] = DarkButtonStyle(p);
        d[typeof(ToolTip)] = Styled<ToolTip>(s =>
        {
            s.Setters.Add(new Setter(Control.BackgroundProperty, panel));
            s.Setters.Add(new Setter(Control.ForegroundProperty, text));
            s.Setters.Add(new Setter(Control.BorderBrushProperty, d["Theme.Border"]));
        });
        return d;
    }

    /// <summary>
    /// Dialog buttons (outside toolbars). The stock template paints hard-coded light hover and
    /// pressed colours, which leave light text unreadable on a dark theme, so it is replaced.
    /// </summary>
    private static Style DarkButtonStyle(Palette p)
    {
        string Hex(Color c) => $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
        var xaml = $$"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Foreground" Value="{{Hex(p.Text)}}" />
              <Setter Property="Background" Value="#FF333333" />
              <Setter Property="BorderBrush" Value="{{Hex(p.Border)}}" />
              <Setter Property="BorderThickness" Value="1" />
              <Setter Property="Padding" Value="12,4" />
              <Setter Property="FocusVisualStyle" Value="{x:Null}" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" SnapsToDevicePixels="True">
                      <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#FF3D3D3D" /></Trigger>
                      <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#FF2A2A2A" /></Trigger>
                      <Trigger Property="IsDefault" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{{Hex(Accent)}}" /></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="{{Hex(Accent)}}" /><Setter TargetName="Chrome" Property="BorderThickness" Value="2" /></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{{Hex(p.GrayText)}}" /></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>An implicit style that keeps the control's stock look and only changes colours.</summary>
    private static Style Styled<T>(Action<Style> configure)
    {
        var baseStyle = Application.Current.TryFindResource(typeof(T)) as Style;
        var style = new Style(typeof(T), baseStyle);
        configure(style);
        return style;
    }

    private static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private const int WM_NCACTIVATE = 0x0086;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
