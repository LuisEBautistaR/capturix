using System.Runtime.InteropServices;
using Capturix.Common;

namespace Capturix.Core.Recording;

/// <summary>
/// Captures one WASAPI endpoint in shared mode — the default speakers in loopback mode, or the
/// default microphone — on its own thread, converting whatever the device delivers (usually
/// 32-bit float, any rate and channel count) into 48 kHz interleaved stereo float in a buffer
/// the mixer drains.
/// </summary>
internal sealed class AudioCaptureSource : IDisposable
{
    public const int OutputRate = 48000;
    private const long BufferDuration = 2_000_000; // 200 ms device buffer
    private const int PollMs = 10;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AudioFormat _format;
    private readonly object _gate = new();
    private readonly List<float> _buffer = new(OutputRate * 2);
    private readonly Thread _thread;
    private volatile bool _running;

    // Linear resampler state (source rate -> 48 kHz), kept across packets.
    private readonly double _step;
    private double _position;
    private float _lastL, _lastR;

    public string Name { get; }
    public float Peak { get; private set; }
    public long FramesCaptured { get; private set; }

    private AudioCaptureSource(string name, IAudioClient client, IAudioCaptureClient capture, AudioFormat format)
    {
        Name = name;
        _client = client;
        _capture = capture;
        _format = format;
        _step = (double)format.SampleRate / OutputRate;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Capturix audio ({name})" };
    }

    /// <summary>Opens the default speakers (loopback) or microphone; returns null if there is none.</summary>
    public static AudioCaptureSource? TryOpen(bool loopback)
    {
        string name = loopback ? "sistema" : "micrófono";
        try
        {
            var enumerator = Wasapi.CreateEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(loopback ? Wasapi.eRender : Wasapi.eCapture, Wasapi.eConsole, out var device) < 0)
            {
                Log.Info($"Audio {name}: no hay dispositivo predeterminado");
                return null;
            }
            MF.Check(device.Activate(Wasapi.IID_IAudioClient, Wasapi.CLSCTX_ALL, IntPtr.Zero, out object clientObject), "IMMDevice.Activate");
            var client = (IAudioClient)clientObject;
            MF.Check(client.GetMixFormat(out IntPtr mix), "GetMixFormat");
            AudioFormat format;
            try
            {
                format = AudioFormat.FromPointer(mix);
                MF.Check(client.Initialize(Wasapi.AUDCLNT_SHAREMODE_SHARED, loopback ? Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK : 0,
                    BufferDuration, 0, mix, IntPtr.Zero), "IAudioClient.Initialize");
            }
            finally
            {
                Wasapi.CoTaskMemFree(mix);
            }
            MF.Check(client.GetService(Wasapi.IID_IAudioCaptureClient, out object captureObject), "GetService");
            Log.Info($"Audio {name}: {format.SampleRate} Hz, {format.Channels} canales, {format.BitsPerSample} bits{(format.IsFloat ? " float" : "")}");
            return new AudioCaptureSource(name, client, (IAudioCaptureClient)captureObject, format);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Error($"Audio {name}: no se pudo abrir", ex);
            return null;
        }
    }

    public void Start()
    {
        _running = true;
        MF.Check(_client.Start(), "IAudioClient.Start");
        _thread.Start();
    }

    /// <summary>Converted 48 kHz stereo frames waiting to be mixed.</summary>
    public int Available
    {
        get
        {
            lock (_gate) return _buffer.Count / 2;
        }
    }

    /// <summary>Adds up to <paramref name="frames"/> frames into <paramref name="mix"/>; returns how many were mixed.</summary>
    public int MixInto(float[] mix, int frames)
    {
        lock (_gate)
        {
            int n = Math.Min(frames, _buffer.Count / 2);
            for (int i = 0; i < n * 2; i++)
                mix[i] += _buffer[i];
            _buffer.RemoveRange(0, n * 2);
            return n;
        }
    }

    /// <summary>Drops the oldest frames (pause, or catching up after clock drift).</summary>
    public void Discard(int frames)
    {
        lock (_gate)
            _buffer.RemoveRange(0, Math.Min(frames, _buffer.Count / 2) * 2);
    }

    private void Run()
    {
        while (_running)
        {
            Thread.Sleep(PollMs);
            try
            {
                while (_running && _capture.GetNextPacketSize(out int packet) >= 0 && packet > 0)
                {
                    if (_capture.GetBuffer(out IntPtr data, out int frames, out int flags, out _, out _) < 0)
                        break;
                    try
                    {
                        Append(data, frames, (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                    }
                    finally
                    {
                        _capture.ReleaseBuffer(frames);
                    }
                }
            }
            catch (COMException ex)
            {
                Log.Error($"Audio {Name}: captura interrumpida", ex);
                return;
            }
        }
    }

    private void Append(IntPtr data, int frames, bool silent)
    {
        FramesCaptured += frames;
        var stereo = new float[frames * 2];
        if (!silent)
        {
            var raw = new byte[frames * _format.BlockAlign];
            Marshal.Copy(data, raw, 0, raw.Length);
            for (int f = 0; f < frames; f++)
            {
                float l = Sample(raw, f, 0);
                float r = _format.Channels > 1 ? Sample(raw, f, 1) : l; // mono mics go to both sides
                stereo[f * 2] = l;
                stereo[f * 2 + 1] = r;
                float peak = Math.Max(Math.Abs(l), Math.Abs(r));
                if (peak > Peak) Peak = peak;
            }
        }

        var resampled = Resample(stereo, frames);
        lock (_gate)
            _buffer.AddRange(resampled);
    }

    private float Sample(byte[] raw, int frame, int channel)
    {
        int offset = frame * _format.BlockAlign + channel * (_format.BitsPerSample / 8);
        return (_format.IsFloat, _format.BitsPerSample) switch
        {
            (true, 32) => BitConverter.ToSingle(raw, offset),
            (false, 16) => BitConverter.ToInt16(raw, offset) / 32768f,
            (false, 24) => ((raw[offset] << 8 | raw[offset + 1] << 16 | raw[offset + 2] << 24) >> 8) / 8388608f,
            (false, 32) => BitConverter.ToInt32(raw, offset) / 2147483648f,
            _ => 0f,
        };
    }

    /// <summary>Linear interpolation to 48 kHz; a no-op copy when the device already runs at 48 kHz.</summary>
    private float[] Resample(float[] stereo, int frames)
    {
        if (_format.SampleRate == OutputRate)
            return stereo;

        // Output sample at source position p interpolates between frames i-1 and i (i = floor p);
        // frame -1 is the previous packet's last frame. The one-frame delay keeps packets seamless.
        var output = new List<float>((int)(frames / _step) * 2 + 4);
        while (_position < frames)
        {
            int i = (int)_position;
            double t = _position - i;
            float l0 = i == 0 ? _lastL : stereo[(i - 1) * 2];
            float r0 = i == 0 ? _lastR : stereo[(i - 1) * 2 + 1];
            float l1 = stereo[i * 2], r1 = stereo[i * 2 + 1];
            output.Add((float)(l0 + (l1 - l0) * t));
            output.Add((float)(r0 + (r1 - r0) * t));
            _position += _step;
        }
        _position -= frames;
        if (frames > 0)
        {
            _lastL = stereo[(frames - 1) * 2];
            _lastR = stereo[(frames - 1) * 2 + 1];
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive)
            _thread.Join(500);
        try
        {
            _client.Stop();
        }
        catch (COMException)
        {
            // Device already gone (unplugged); nothing to stop.
        }
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
    }
}
