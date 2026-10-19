using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Capturix.Common;

/// <summary>The user's colour-scheme choice, persisted in settings.json as "Theme".</summary>
public static class ThemeMode
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static readonly string[] All = { System, Light, Dark };

    public static string DisplayName(string mode) => mode switch
    {
        Light => "Claro",
        Dark => "Oscuro",
        _ => "Sistema",
    };

    public static string Normalize(string? mode) => mode is Light or Dark ? mode : System;

    /// <summary>Resolves "system" with the Windows app colour setting (Settings → Personalization → Colors).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsDark(string? mode) => Normalize(mode) switch
    {
        Dark => true,
        Light => false,
        _ => WindowsAppsUseDarkTheme(),
    };

    [SupportedOSPlatform("windows")]
    public static bool WindowsAppsUseDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
