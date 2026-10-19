namespace Capturix.Core.Recording;

/// <summary>Diagnostics: briefly opens an audio endpoint and describes what it delivered (no audio is kept).</summary>
public static class AudioDeviceProbe
{
    public static string Probe(bool loopback, TimeSpan duration)
    {
        string name = loopback ? "sistema (loopback)" : "micrófono";
        using var source = AudioCaptureSource.TryOpen(loopback);
        if (source is null)
            return $"{name}: no disponible";
        source.Start();
        Thread.Sleep(duration);
        int buffered = source.Available;
        source.Discard(int.MaxValue);
        return $"{name}: {source.FramesCaptured} muestras del dispositivo, {buffered} a 48 kHz, pico {source.Peak:0.000}";
    }
}
