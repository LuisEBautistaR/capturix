using Capturix.Common;
using Capturix.Core.Recording;

namespace Capturix.Capture;

/// <summary>
/// One recording from countdown to saved file: shows the outline and control bar, runs the
/// <see cref="ScreenRecorder"/> and completes <see cref="Completion"/> with the output path
/// (or null if the user stopped during the countdown).
/// </summary>
internal sealed class RecordingSession : IDisposable
{
    private const int CountdownSeconds = 3;

    private readonly RecordingFormat _format;
    private string _formatName;
    private readonly ScreenRecorder _recorder;
    private readonly RecordingFrameForm _frame;
    private readonly RecordingControlBar _bar;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly TaskCompletionSource<string?> _completion = new();
    private int _countdown = CountdownSeconds;
    private bool _started;
    private bool _stopping;

    public Task<string?> Completion => _completion.Task;
    public RecordingFormat Format => _format;

    public RecordingSession(Rectangle region, RecordingFormat format, string path, int fps, bool drawCursor,
        bool systemAudio, bool microphone)
    {
        _format = format;
        _formatName = format == RecordingFormat.Mp4 ? "Vídeo" : "GIF";
        _recorder = new ScreenRecorder(region, format, path, fps, drawCursor, systemAudio, microphone);
        _frame = new RecordingFrameForm(_recorder.Region);
        _bar = new RecordingControlBar(_recorder.Region, _formatName);
        _bar.StopClicked += (_, _) => _ = StopAsync();
        _bar.PauseClicked += (_, _) => TogglePause();
        _timer.Tick += (_, _) => Tick();
    }

    public void Begin()
    {
        _frame.Show();
        _bar.Show();
        _bar.ShowCountdown(_countdown);
        _timer.Interval = 1000;
        _timer.Start();
    }

    private void Tick()
    {
        if (!_started)
        {
            if (--_countdown > 0)
            {
                _bar.ShowCountdown(_countdown);
                return;
            }
            _started = true;
            _recorder.Start();
            if (_recorder.AudioSources.Count > 0)
                _formatName += " + " + string.Join(" + ", _recorder.AudioSources.Select(s => s == "sistema" ? "🔊" : "🎤"));
            Log.Info($"Grabación {_formatName} iniciada: {_recorder.Region} → {_recorder.OutputPath}");
            _timer.Interval = 250;
        }
        _bar.ShowRecording(_recorder.Elapsed, _recorder.IsPaused, _formatName);
    }

    private void TogglePause()
    {
        if (!_started) return;
        if (_recorder.IsPaused) _recorder.Resume(); else _recorder.Pause();
        _bar.ShowRecording(_recorder.Elapsed, _recorder.IsPaused, _formatName);
    }

    /// <summary>Stops (or cancels during the countdown) and finishes writing the file.</summary>
    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _timer.Stop();
        try
        {
            if (!_started)
            {
                _completion.TrySetResult(null);
                return;
            }
            _bar.ShowSaving();
            await _recorder.StopAsync();
            _completion.TrySetResult(_recorder.OutputPath);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
        finally
        {
            _frame.Close();
            _bar.Close();
        }
    }

    /// <summary>Synchronous stop for app shutdown, safe to call on the UI thread.</summary>
    public void StopForExit()
    {
        if (_stopping) return;
        _stopping = true;
        _timer.Stop();
        if (_started)
            _recorder.StopAsync().GetAwaiter().GetResult();
        _completion.TrySetResult(_started ? _recorder.OutputPath : null);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _recorder.Dispose();
        _frame.Dispose();
        _bar.Dispose();
    }
}
