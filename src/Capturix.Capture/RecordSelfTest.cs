using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Capturix.Core.Recording;

namespace Capturix.Capture;

/// <summary>
/// Headless check of the encoders: Capturix.exe --selftest-record outdir.
/// Feeds synthetic frames (gradient + moving square + counter) to the MP4 and GIF sinks and
/// checks the GIF by decoding it. The MP4 is checked afterwards from the shell's properties.
/// </summary>
internal static class RecordSelfTest
{
    private const int W = 640, H = 360, Fps = 30, Frames = 60;

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        bool ok = true;
        void Check(bool condition, string description)
        {
            report.AppendLine($"{(condition ? "OK  " : "FAIL")} {description}");
            ok &= condition;
        }

        try
        {
            string mp4 = Path.Combine(outDir, "selftest.mp4");
            string gif = Path.Combine(outDir, "selftest.gif");
            Encode(new Mp4Sink(mp4, W, H, Fps), Frames);
            Check(File.Exists(mp4) && new FileInfo(mp4).Length > 10_000, $"MP4 escrito ({new FileInfo(mp4).Length:N0} bytes)");

            // Every other frame is identical to the previous one: the GIF must merge those.
            Encode(new GifSink(gif, W, H, 15), 30, repeatEachFrame: true);
            Check(File.Exists(gif), $"GIF escrito ({new FileInfo(gif).Length:N0} bytes)");

            using var image = Image.FromFile(gif);
            int gifFrames = image.GetFrameCount(FrameDimension.Time);
            Check(gifFrames == 15, $"GIF con 15 fotogramas distintos (tiene {gifFrames}; los repetidos se fusionan)");

            // Frame 10 must show the moving square where it was drawn.
            image.SelectActiveFrame(FrameDimension.Time, 10);
            using var frame10 = new Bitmap(image);
            var expectedSquare = SquarePosition(20); // repeated frames: frame 10 of the GIF is source frame 20
            var inside = frame10.GetPixel(expectedSquare.X + 20, expectedSquare.Y + 20);
            Check(inside.R > 200 && inside.G > 200 && inside.B < 80, $"El cuadrado amarillo está en su sitio en el fotograma 10 ({inside.R},{inside.G},{inside.B})");
            var corner = frame10.GetPixel(5, 5);
            Check(corner.R > 150 && corner.B < 120, $"El fondo superior conserva el rojo del degradado ({corner.R},{corner.G},{corner.B})");

            byte[] bytes = File.ReadAllBytes(gif);
            Check(Encoding.ASCII.GetString(bytes, 0, 6) == "GIF89a" && Encoding.ASCII.GetString(bytes).Contains("NETSCAPE2.0"),
                "Cabecera GIF89a con repetición infinita");

            // Video plus a 440 Hz tone: the MP4 must gain an AAC track of the same length.
            string withAudio = Path.Combine(outDir, "selftest_audio.mp4");
            EncodeWithTone(withAudio);
            Check(File.Exists(withAudio) && new FileInfo(withAudio).Length > new FileInfo(mp4).Length,
                $"MP4 con audio escrito ({new FileInfo(withAudio).Length:N0} bytes, más que sin audio)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"FAIL excepción: {ex}");
            ok = false;
        }

        report.AppendLine(ok ? "RESULTADO: OK" : "RESULTADO: FALLÓ");
        File.WriteAllText(Path.Combine(outDir, "record_selftest.txt"), report.ToString());
        return ok ? 0 : 1;
    }

    private static void Encode(IFrameSink sink, int frames, bool repeatEachFrame = false)
    {
        using (sink)
        {
            using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var pixels = new byte[W * H * 4];
            for (int i = 0; i < frames; i++)
            {
                int source = repeatEachFrame ? i / 2 * 2 : i;
                DrawFrame(bmp, source);
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                bmp.UnlockBits(data);
                long timestamp = i * 10_000_000L / Fps;
                sink.WriteFrame(pixels, W * 4, timestamp);
            }
            sink.Complete();
        }
    }

    /// <summary>Same synthetic video with a 440 Hz sine on the audio track, in 20 ms chunks.</summary>
    private static void EncodeWithTone(string path)
    {
        using var sink = new Mp4Sink(path, W, H, Fps, withAudio: true);
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var pixels = new byte[W * H * 4];
        const int rate = 48000, chunk = 960; // 20 ms
        long audioFrames = 0;
        for (int i = 0; i < Frames; i++)
        {
            DrawFrame(bmp, i);
            var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bmp.UnlockBits(data);
            long timestamp = i * 10_000_000L / Fps;
            sink.WriteFrame(pixels, W * 4, timestamp);

            // Audio up to the end of this video frame.
            long audioTarget = (i + 1) * rate / Fps;
            while (audioFrames < audioTarget)
            {
                var pcm = new byte[chunk * 4];
                for (int s = 0; s < chunk; s++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * 440 * (audioFrames + s) / rate) * 12000);
                    pcm[s * 4] = pcm[s * 4 + 2] = (byte)v;
                    pcm[s * 4 + 1] = pcm[s * 4 + 3] = (byte)(v >> 8);
                }
                sink.WriteAudio(new AudioChunk(pcm, audioFrames * 10_000_000L / rate, chunk * 10_000_000L / rate));
                audioFrames += chunk;
            }
        }
        sink.Complete();
    }

    /// <summary>
    /// Capturix.exe --selftest-audio-devices outdir: opens the default speakers (loopback) and
    /// microphone for 1.5 s and reports format, captured samples and peak level. Nothing is saved.
    /// </summary>
    public static int RunDeviceCheck(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        foreach (bool loopback in new[] { true, false })
            report.AppendLine(AudioDeviceProbe.Probe(loopback, TimeSpan.FromSeconds(1.5)));
        File.WriteAllText(Path.Combine(outDir, "audio_devices.txt"), report.ToString());
        return 0;
    }

    private static Point SquarePosition(int frame) => new(20 + frame * 8, 120 + (int)(Math.Sin(frame / 6.0) * 60));

    private static void DrawFrame(Bitmap bmp, int frame)
    {
        using var g = Graphics.FromImage(bmp);
        using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(220, 40, 40), Color.FromArgb(30, 60, 200), 90f))
            g.FillRectangle(gradient, 0, 0, W, H);
        var p = SquarePosition(frame);
        g.FillRectangle(Brushes.Yellow, p.X, p.Y, 60, 60);
        using var font = new Font("Segoe UI", 20f, FontStyle.Bold);
        g.DrawString($"Fotograma {frame}", font, Brushes.White, 380, 20);
    }
}
