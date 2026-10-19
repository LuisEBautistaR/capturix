using Capturix.Common;

namespace Capturix.Core.Recording;

/// <summary>One block of mixed 48 kHz stereo 16-bit PCM, timed on the recording clock.</summary>
public sealed record AudioChunk(byte[] Pcm, long Timestamp, long Duration);

/// <summary>
/// Mixes the audio sources onto the recording's clock. Every few milliseconds it emits exactly
/// as many samples as the clock says have passed (minus a small safety lag), taking what each
/// source has and filling the rest with silence. That keeps audio aligned with the video even
/// though loopback capture delivers nothing at all while the system is silent.
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    private const int Rate = AudioCaptureSource.OutputRate;
    /// <summary>Headroom for device latency: samples are mixed this long after they were captured.</summary>
    private static readonly long LagTicks = TimeSpan.FromMilliseconds(150).Ticks;
    /// <summary>More buffered than this beyond what is needed means clocks drifted; drop the excess.</summary>
    private const int MaxBacklogFrames = Rate / 2;

    private readonly List<AudioCaptureSource> _sources;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<bool> _paused;
    private readonly Action<AudioChunk> _output;
    private readonly Thread _thread;
    private volatile bool _running;
    private long _produced; // frames emitted so far

    public IReadOnlyList<AudioCaptureSource> Sources => _sources;

    public AudioMixer(List<AudioCaptureSource> sources, Func<TimeSpan> clock, Func<bool> paused, Action<AudioChunk> output)
    {
        _sources = sources;
        _clock = clock;
        _paused = paused;
        _output = output;
        _thread = new Thread(Run) { IsBackground = true, Name = "Capturix audio mixer" };
    }

    public void Start()
    {
        foreach (var s in _sources)
            s.Start();
        _running = true;
        _thread.Start();
    }

    /// <summary>Stops mixing and emits everything up to the final clock time (no lag at the end).</summary>
    public void Stop()
    {
        _running = false;
        _thread.Join();
        Thread.Sleep(60); // let the sources deliver their last packets
        MixUntil(_clock().Ticks);
        foreach (var s in _sources)
            Log.Info($"Audio {s.Name}: {s.FramesCaptured} muestras capturadas, pico {s.Peak:0.000}");
    }

    private void Run()
    {
        while (_running)
        {
            Thread.Sleep(20);
            if (_paused())
            {
                // Sound during a pause is not recorded: keep the sources empty.
                foreach (var s in _sources)
                    s.Discard(int.MaxValue);
                continue;
            }
            MixUntil(_clock().Ticks - LagTicks);
        }
    }

    private void MixUntil(long clockTicks)
    {
        long target = clockTicks * Rate / TimeSpan.TicksPerSecond;
        int need = (int)Math.Min(target - _produced, Rate); // at most 1 s per pass
        if (need <= 0)
            return;

        var mix = new float[need * 2];
        foreach (var source in _sources)
        {
            int backlog = source.Available - need;
            if (backlog > MaxBacklogFrames)
                source.Discard(backlog);
            source.MixInto(mix, need); // fewer than needed => the rest stays silent
        }

        var pcm = new byte[need * 4];
        for (int i = 0; i < mix.Length; i++)
        {
            int v = (int)Math.Round(Math.Clamp(mix[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }

        long timestamp = _produced * TimeSpan.TicksPerSecond / Rate;
        long duration = need * TimeSpan.TicksPerSecond / Rate;
        _produced += need;
        _output(new AudioChunk(pcm, timestamp, duration));
    }

    public void Dispose()
    {
        _running = false;
        foreach (var s in _sources)
            s.Dispose();
    }
}
