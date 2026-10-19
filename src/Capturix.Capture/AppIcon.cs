namespace Capturix.Capture;

/// <summary>The Capturix logo (assets/logo/capturix.ico, embedded) at the tray's icon size.</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Capturix.ico")
                           ?? throw new InvalidOperationException("Falta el recurso Capturix.ico");
        // SmallIconSize follows the display scaling (16 px at 100 %, 24 px at 150 %...), and the
        // .ico carries a hand-tuned image for each of those sizes.
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
