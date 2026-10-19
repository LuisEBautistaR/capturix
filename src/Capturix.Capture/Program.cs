namespace Capturix.Capture;

internal static class Program
{
    // Named mutex that marks the capturer as running; the GUID keeps the name unique.
    private const string SingleInstanceMutex = "Capturix.Capture.{6B1E3C52-7F0A-4C1D-9B8E-2A4F5D6C7E81}";

    [STAThread]
    private static int Main(string[] args)
    {
        // Headless check of the scrolling-capture stitcher: Capturix.exe --selftest-scroll <outdir>
        if (args.Length == 2 && args[0] == "--selftest-scroll")
            return ScrollSelfTest.Run(args[1]);
        // Headless check of the MP4/GIF encoders: Capturix.exe --selftest-record <outdir>
        if (args.Length == 2 && args[0] == "--selftest-record")
            return RecordSelfTest.Run(args[1]);
        // Audio device diagnostics (nothing is saved): Capturix.exe --selftest-audio-devices <outdir>
        if (args.Length == 2 && args[0] == "--selftest-audio-devices")
            return RecordSelfTest.RunDeviceCheck(args[1]);
        // Headless OCR check on synthetic text images: Capturix.exe --selftest-ocr <outdir>
        if (args.Length == 2 && args[0] == "--selftest-ocr")
            return OcrSelfTest.Run(args[1]);
        // Save-location detection and library moves on scratch files: Capturix.exe --selftest-storage <outdir>
        if (args.Length == 2 && args[0] == "--selftest-storage")
            return StorageSelfTest.Run(args[1]);

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Capturix ya se está ejecutando en la bandeja del sistema.", "Capturix",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}
