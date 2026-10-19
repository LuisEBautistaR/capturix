using System.Runtime.InteropServices;

namespace Capturix.Core.Recording;

/// <summary>
/// Encodes frames to H.264 in an MP4 container with the Media Foundation Sink Writer.
/// The writer converts our RGB32 input to the encoder's YUV format and uses a hardware
/// encoder when the GPU offers one. Must be used from one MTA thread (the recorder's encoder thread).
/// </summary>
public sealed class Mp4Sink : IFrameSink, IAudioSink
{
    private const int AudioRate = 48000;
    private const int AudioChannels = 2;
    private const int AacBytesPerSecond = 24000; // 192 kbit/s

    private readonly IMFSinkWriter _writer;
    private readonly int _stream;
    private readonly int _audioStream = -1;
    private readonly int _width, _height, _fps;
    private readonly int _frameBytes;
    private bool _completed;

    /// <summary>Width and height must be even (H.264 4:2:0 requirement); the recorder rounds them down.</summary>
    public Mp4Sink(string path, int width, int height, int fps, bool withAudio = false)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("El tamaño del vídeo debe ser par.");
        _width = width;
        _height = height;
        _fps = fps;
        _frameBytes = width * height * 4;

        MF.Check(MF.MFStartup(MF.Version, MF.StartupFull), "MFStartup");

        MF.Check(MF.MFCreateAttributes(out var attributes, 2), "MFCreateAttributes");
        MF.Check(attributes.SetUINT32(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1), "hardware transforms");
        MF.Check(attributes.SetUINT32(MF.SINK_WRITER_DISABLE_THROTTLING, 1), "disable throttling");
        MF.Check(MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer), "MFCreateSinkWriterFromURL");

        // Output: H.264. Screen content compresses well; ~0.1 bit per pixel per frame looks clean.
        MF.Check(MF.MFCreateMediaType(out var output), "MFCreateMediaType (salida)");
        output.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
        output.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_H264);
        output.SetUINT32(MF.MT_AVG_BITRATE, (int)Math.Clamp((long)width * height * fps / 10, 1_000_000, 25_000_000));
        output.SetUINT32(MF.MT_INTERLACE_MODE, MF.VideoInterlace_Progressive);
        output.SetUINT64(MF.MT_FRAME_SIZE, MF.Pack(width, height));
        output.SetUINT64(MF.MT_FRAME_RATE, MF.Pack(fps, 1));
        output.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
        MF.Check(_writer.AddStream(output, out _stream), "AddStream");

        // Input: top-down RGB32 (a positive default stride means the first row is the top).
        MF.Check(MF.MFCreateMediaType(out var input), "MFCreateMediaType (entrada)");
        input.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
        input.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_RGB32);
        input.SetUINT32(MF.MT_INTERLACE_MODE, MF.VideoInterlace_Progressive);
        input.SetUINT64(MF.MT_FRAME_SIZE, MF.Pack(width, height));
        input.SetUINT64(MF.MT_FRAME_RATE, MF.Pack(fps, 1));
        input.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
        input.SetUINT32(MF.MT_DEFAULT_STRIDE, width * 4);
        MF.Check(_writer.SetInputMediaType(_stream, input, null), "SetInputMediaType");

        if (withAudio)
            _audioStream = AddAudioStream();

        MF.Check(_writer.BeginWriting(), "BeginWriting");

        Marshal.ReleaseComObject(input);
        Marshal.ReleaseComObject(output);
        Marshal.ReleaseComObject(attributes);
    }

    /// <summary>AAC track fed with 48 kHz stereo 16-bit PCM (the Windows AAC encoder's native input).</summary>
    private int AddAudioStream()
    {
        MF.Check(MF.MFCreateMediaType(out var output), "MFCreateMediaType (audio salida)");
        output.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
        output.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_AAC);
        output.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
        output.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
        output.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, AudioChannels);
        output.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, AacBytesPerSecond);
        MF.Check(_writer.AddStream(output, out int stream), "AddStream (audio)");

        MF.Check(MF.MFCreateMediaType(out var input), "MFCreateMediaType (audio entrada)");
        input.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
        input.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_PCM);
        input.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
        input.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
        input.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, AudioChannels);
        input.SetUINT32(MF.MT_AUDIO_BLOCK_ALIGNMENT, AudioChannels * 2);
        input.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, AudioRate * AudioChannels * 2);
        MF.Check(_writer.SetInputMediaType(stream, input, null), "SetInputMediaType (audio)");

        Marshal.ReleaseComObject(input);
        Marshal.ReleaseComObject(output);
        return stream;
    }

    public void WriteAudio(AudioChunk chunk)
    {
        if (_audioStream < 0 || chunk.Pcm.Length == 0)
            return;
        WriteSample(_audioStream, chunk.Pcm, chunk.Pcm.Length, chunk.Timestamp, chunk.Duration);
    }

    private void WriteSample(int stream, byte[] data, int length, long timestamp, long duration)
    {
        MF.Check(MF.MFCreateMemoryBuffer(length, out var buffer), "MFCreateMemoryBuffer");
        try
        {
            MF.Check(buffer.Lock(out IntPtr dest, out _, out _), "Lock");
            try
            {
                Marshal.Copy(data, 0, dest, length);
            }
            finally
            {
                buffer.Unlock();
            }
            MF.Check(buffer.SetCurrentLength(length), "SetCurrentLength");
            MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
            try
            {
                MF.Check(sample.AddBuffer(buffer), "AddBuffer");
                MF.Check(sample.SetSampleTime(timestamp), "SetSampleTime");
                MF.Check(sample.SetSampleDuration(duration), "SetSampleDuration");
                MF.Check(_writer.WriteSample(stream, sample), "WriteSample");
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    public void WriteFrame(byte[] bgra, int stride, long timestamp)
    {
        int rowBytes = _width * 4;
        byte[] packed = bgra;
        if (stride != rowBytes)
        {
            packed = new byte[_frameBytes];
            for (int y = 0; y < _height; y++)
                Buffer.BlockCopy(bgra, y * stride, packed, y * rowBytes, rowBytes);
        }
        WriteSample(_stream, packed, _frameBytes, timestamp, 10_000_000L / _fps);
    }

    public void Complete()
    {
        if (_completed)
            return;
        _completed = true;
        MF.Check(_writer.FinalizeWriting(), "Finalize");
    }

    public void Dispose()
    {
        try
        {
            Complete();
        }
        finally
        {
            Marshal.ReleaseComObject(_writer);
            MF.MFShutdown();
        }
    }
}
