using Capturix.Common;
using Capturix.Core.Interop;

namespace Capturix.Core.Capture;

/// <summary>
/// Captures content taller than the screen by scrolling an area with the mouse wheel and
/// stitching the frames. It runs in-process, so it scrolls ordinary windows; elevated windows
/// would need a separate uiAccess helper process.
/// </summary>
public sealed class ScrollingCapture
{
    public int MaxFrames { get; init; } = 120;
    public int MaxHeight { get; init; } = 30000;

    /// <summary>Fraction of the moving area to scroll per step; the rest is overlap for matching.</summary>
    private const double TargetStep = 0.55;
    private const int SettleTimeoutMs = 1500;
    private const int SettlePollMs = 70;
    private const int MaxRetries = 3;
    private const int StillStepsAtEnd = 3;
    private const int SlowAppGraceMs = 300;

    /// <summary>Set CAPTURIX_DEBUG_SCROLL to a folder to dump every frame and log each stitch result.</summary>
    private static readonly string? DebugFolder = Environment.GetEnvironmentVariable("CAPTURIX_DEBUG_SCROLL");
    private int _debugIndex;

    private void Debug(Bitmap frame, StitchResult result)
    {
        if (string.IsNullOrEmpty(DebugFolder))
            return;
        Directory.CreateDirectory(DebugFolder);
        int i = _debugIndex++;
        frame.Save(Path.Combine(DebugFolder, $"frame_{i:D3}.png"), System.Drawing.Imaging.ImageFormat.Png);
        Log.Info($"Desplazamiento fotograma {i}: {result.Outcome} dy={result.ScrolledPixels} móvil={result.MovingHeight}");
    }

    /// <summary>
    /// Scrolls <paramref name="screenRect"/> to the end (or until Esc / cancellation) and
    /// returns the stitched image. Must run on a UI thread with a message loop.
    /// </summary>
    public async Task<Bitmap> RunAsync(Rectangle screenRect, IProgress<string>? progress, CancellationToken ct)
    {
        var savedCursor = Cursor.Position;
        // Park the cursor over the scrollbar column: it is ignored by the matcher, so hover
        // effects there cannot break stitching, and the wheel still reaches the right window.
        var wheelPoint = new Point(
            Math.Max(screenRect.Left, screenRect.Right - FrameStitcher.IgnoredRightColumns / 2),
            screenRect.Top + screenRect.Height / 2);
        Native.SetCursorPos(wheelPoint.X, wheelPoint.Y);

        // Bring the target to the front first: some apps
        // ignore the wheel while inactive, and activation may itself scroll (e.g. to the caret),
        // so it must happen before the first frame is taken.
        var target = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = wheelPoint.X, Y = wheelPoint.Y }), Native.GA_ROOT);
        if (target != IntPtr.Zero && target != Native.GetForegroundWindow())
            Native.SetForegroundWindow(target);

        using var stitcher = new FrameStitcher();
        try
        {
            await Task.Delay(250, ct);
            using (var first = await CaptureSettledAsync(screenRect, ct))
                Debug(first, stitcher.Add(first));

            int notches = 3, stillFrames = 0, retries = 0;
            while (stitcher.FrameCount < MaxFrames && stitcher.Height < MaxHeight)
            {
                if (ct.IsCancellationRequested || EscapePressed())
                {
                    Log.Info("Captura con desplazamiento detenida por el usuario");
                    break;
                }

                // Re-park the cursor every step: the wheel goes to whatever window is under it,
                // and a nudge of the mouse would otherwise send it elsewhere.
                Native.SetCursorPos(wheelPoint.X, wheelPoint.Y);
                Wheel(-notches);
                var frame = await CaptureSettledAsync(screenRect, ct);
                var result = stitcher.Add(frame);
                if (result.Outcome == StitchOutcome.NoMovement)
                {
                    // The app may not have handled the wheel yet: give it a moment and look again.
                    await Task.Delay(SlowAppGraceMs, ct);
                    frame.Dispose();
                    frame = await CaptureSettledAsync(screenRect, ct);
                    result = stitcher.Add(frame);
                }
                Debug(frame, result);
                frame.Dispose();

                if (result.Outcome == StitchOutcome.Appended)
                {
                    stillFrames = 0;
                    retries = 0;
                    // Learn how many pixels one notch scrolls in this app and aim for ~55 % of the view.
                    double pixelsPerNotch = (double)result.ScrolledPixels / notches;
                    notches = Math.Clamp((int)Math.Round(result.MovingHeight * TargetStep / pixelsPerNotch), 1, 30);
                    progress?.Report($"{stitcher.FrameCount} tramos · {stitcher.Height} px");
                }
                else if (result.Outcome == StitchOutcome.NoMovement)
                {
                    // Several still steps in a row: the content cannot scroll any further.
                    if (++stillFrames >= StillStepsAtEnd)
                        break;
                }
                else
                {
                    // Lost the overlap (scrolled too far or the page changed): go back, use smaller steps.
                    if (++retries > MaxRetries)
                    {
                        Log.Info("Captura con desplazamiento: no se encontró solape, se termina");
                        break;
                    }
                    Wheel(notches);
                    (await CaptureSettledAsync(screenRect, ct)).Dispose();
                    notches = Math.Max(1, notches / 2);
                }
            }
        }
        finally
        {
            Native.SetCursorPos(savedCursor.X, savedCursor.Y);
        }

        Log.Info($"Captura con desplazamiento: {stitcher.FrameCount} tramos, {stitcher.Height} px");
        return stitcher.Compose();
    }

    private static void Wheel(int notches) =>
        Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, notches * Native.WHEEL_DELTA, UIntPtr.Zero);

    private static bool EscapePressed() => (Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0;

    /// <summary>Captures repeatedly until two consecutive frames are identical (smooth scrolling finished).</summary>
    private static async Task<Bitmap> CaptureSettledAsync(Rectangle rect, CancellationToken ct)
    {
        var frame = ScreenCapture.CaptureRegion(rect);
        ulong signature = FrameStitcher.Signature(frame);
        var deadline = Environment.TickCount64 + SettleTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(SettlePollMs, ct);
            var next = ScreenCapture.CaptureRegion(rect);
            ulong nextSignature = FrameStitcher.Signature(next);
            frame.Dispose();
            frame = next;
            if (nextSignature == signature)
                break;
            signature = nextSignature;
        }
        return frame;
    }
}
