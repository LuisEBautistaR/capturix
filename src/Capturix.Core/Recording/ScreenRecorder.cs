using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Capturix.Common;
using Capturix.Core.Interop;

namespace Capturix.Core.Recording;

public enum RecordingFormat { Mp4, Gif }

/// <summary>
/// Records a screen rectangle to MP4 or GIF. A capture thread grabs frames on a fixed
/// schedule; an encoder thread (MTA, as Media Foundation prefers) writes them. A bounded
/// queue between them drops frames instead of drifting if encoding falls behind, and every
/// frame carries its real capture time, so the video stays in sync either way.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private const int QueueCapacity = 45;

    private readonly Rectangle _region;
    private readonly RecordingFormat _format;
    private readonly string _path;
    private readonly int _fps;
    private readonly bool _drawCursor;

    private readonly bool _recordSystemAudio;
    private readonly bool _recordMicrophone;
    private AudioMixer? _mixer;
    private readonly ConcurrentQueue<AudioChunk> _audioQueue = new();

    private readonly BlockingCollection<(byte[] Pixels, long Timestamp)> _queue = new(QueueCapacity);
    private readonly TaskCompletionSource _encoderDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private Thread? _captureThread;
    private Thread? _encoderThread;
    private volatile bool _stopping;
    private volatile bool _paused;
    private long _pausedTicks;
    private long _pauseStartedTicks;
    private int _dropped;
    private int _captured;

    /// <summary>The region actually recorded (MP4 needs even sizes, so it may be 1 px smaller).</summary>
    public Rectangle Region => _region;
    public string OutputPath => _path;
    public bool IsPaused => _paused;
    public int FramesCaptured => _captured;
    public int FramesDropped => _dropped;

    /// <summary>Audio sources actually being recorded ("sistema", "micrófono"); empty for silent recordings.</summary>
    public IReadOnlyList<string> AudioSources { get; private set; } = Array.Empty<string>();

    /// <summary>Recorded time, excluding pauses.</summary>
    public TimeSpan Elapsed =>
        TimeSpan.FromTicks(Math.Max(0, _clock.Elapsed.Ticks - _pausedTicks - (_paused ? _clock.Elapsed.Ticks - _pauseStartedTicks : 0)));

    /// <param name="systemAudio">MP4 only: record what the speakers play (WASAPI loopback).</param>
    /// <param name="microphone">MP4 only: record the default microphone.</param>
    public ScreenRecorder(Rectangle region, RecordingFormat format, string path, int fps, bool drawCursor,
        bool systemAudio = false, bool microphone = false)
    {
        _recordSystemAudio = systemAudio && format == RecordingFormat.Mp4;
        _recordMicrophone = microphone && format == RecordingFormat.Mp4;
        if (format == RecordingFormat.Mp4)
            region = new Rectangle(region.X, region.Y, region.Width & ~1, region.Height & ~1);
        if (region.Width < 16 || region.Height < 16)
            throw new ArgumentException("La región es demasiado pequeña para grabar.");
        _region = region;
        _format = format;
        _path = path;
        _fps = Math.Clamp(fps, 1, 60);
        _drawCursor = drawCursor;
    }

    public void Start()
    {
        // Open audio first: the MP4 needs to know whether it has an audio track before it starts.
        var sources = new List<AudioCaptureSource>();
        if (_recordSystemAudio && AudioCaptureSource.TryOpen(loopback: true) is { } system)
            sources.Add(system);
        if (_recordMicrophone && AudioCaptureSource.TryOpen(loopback: false) is { } mic)
            sources.Add(mic);
        if (sources.Count > 0)
        {
            _mixer = new AudioMixer(sources, () => Elapsed, () => _paused, chunk => _audioQueue.Enqueue(chunk));
            AudioSources = sources.Select(s => s.Name).ToArray();
        }

        _encoderThread = new Thread(EncoderLoop) { IsBackground = true, Name = "Capturix encoder" };
        _encoderThread.SetApartmentState(ApartmentState.MTA);
        _encoderThread.Start();

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Capturix capture", Priority = ThreadPriority.AboveNormal };
        _clock.Start();
        _mixer?.Start();
        _captureThread.Start();
    }

    public void Pause()
    {
        if (_paused) return;
        _pauseStartedTicks = _clock.Elapsed.Ticks;
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused) return;
        _pausedTicks += _clock.Elapsed.Ticks - _pauseStartedTicks;
        _paused = false;
    }

    /// <summary>Stops capturing and waits until the file is completely written.</summary>
    public async Task StopAsync()
    {
        if (_paused) Resume();
        _stopping = true;
        // No UI context needed: callers may block on this while the app shuts down.
        await Task.Run(() =>
        {
            _captureThread?.Join();
            _mixer?.Stop(); // flushes audio up to the final clock time
        }).ConfigureAwait(false);
        _queue.CompleteAdding();
        await _encoderDone.Task.ConfigureAwait(false);
        Log.Info($"Grabación terminada: {_path} — {_captured} fotogramas, {_dropped} descartados, {Elapsed:mm\\:ss}");
    }

    private void CaptureLoop()
    {
        Native.timeBeginPeriod(1); // 1 ms sleep granularity for even frame pacing
        try
        {
            using var grabber = new FrameGrabber(_region, _drawCursor);
            long frameTicks = TimeSpan.TicksPerSecond / _fps;
            long next = 0;
            while (!_stopping)
            {
                long now = _clock.Elapsed.Ticks;
                if (now < next)
                {
                    Thread.Sleep(TimeSpan.FromTicks(Math.Min(next - now, TimeSpan.TicksPerMillisecond * 20)));
                    continue;
                }
                next = Math.Max(next + frameTicks, now); // never try to "catch up" with a burst

                if (_paused)
                    continue;

                long timestamp = now - _pausedTicks;
                var pixels = ArrayPool<byte>.Shared.Rent(grabber.FrameBytes);
                grabber.Grab(pixels);
                if (_queue.TryAdd((pixels, timestamp)))
                {
                    _captured++;
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    _dropped++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fallo capturando fotogramas", ex);
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    private void EncoderLoop()
    {
        try
        {
            int stride = _region.Width * 4;
            using IFrameSink sink = _format == RecordingFormat.Mp4
                ? new Mp4Sink(_path, _region.Width, _region.Height, _fps, withAudio: _mixer is not null)
                : new GifSink(_path, _region.Width, _region.Height, _fps);
            var audioSink = sink as IAudioSink;

            // The sink writer is not thread-safe, so video and audio are both written from here.
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var frame, millisecondsTimeout: 15))
                {
                    try
                    {
                        sink.WriteFrame(frame.Pixels, stride, frame.Timestamp);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame.Pixels);
                    }
                }
                DrainAudio(audioSink);
            }
            DrainAudio(audioSink);
            sink.Complete();
            _encoderDone.TrySetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Fallo codificando la grabación", ex);
            _stopping = true;
            // Drain so the capture thread is never blocked on a full queue.
            while (_queue.TryTake(out var item))
                ArrayPool<byte>.Shared.Return(item.Pixels);
            _encoderDone.TrySetException(ex);
        }
    }

    private void DrainAudio(IAudioSink? sink)
    {
        while (_audioQueue.TryDequeue(out var chunk))
            sink?.WriteAudio(chunk);
    }

    public void Dispose()
    {
        _stopping = true;
        _mixer?.Dispose();
        _queue.Dispose();
    }
}
