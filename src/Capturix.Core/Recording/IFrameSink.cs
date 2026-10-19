namespace Capturix.Core.Recording;

/// <summary>Receives captured frames (top-down BGRA32) in order and produces a file.</summary>
public interface IFrameSink : IDisposable
{
    /// <param name="bgra">Pixels, <paramref name="stride"/> bytes per row, top row first.</param>
    /// <param name="timestamp">Presentation time in 100-ns units since the recording started.</param>
    void WriteFrame(byte[] bgra, int stride, long timestamp);

    /// <summary>Flushes and closes the output file.</summary>
    void Complete();
}

/// <summary>A sink that also stores an audio track (48 kHz stereo 16-bit PCM in).</summary>
public interface IAudioSink
{
    void WriteAudio(AudioChunk chunk);
}
