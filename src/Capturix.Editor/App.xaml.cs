using System.IO;
using System.Windows;
using Capturix.Common.Ipc;

namespace Capturix.Editor;

public partial class App : Application
{
    private EditorPipe.Listener? _pipe;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless check of the model and renderer: Capturix.Editor.exe --selftest in.png out.png
        if (e.Args.Length == 3 && e.Args[0] == "--selftest")
        {
            Shutdown(SelfTest.Run(e.Args[1], e.Args[2]));
            return;
        }

        string? fileArg = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        // Single editor: if one is already running, hand it the file (or just raise it) and exit.
        if (EditorPipe.TrySend(fileArg ?? "", timeoutMs: 300))
        {
            Shutdown(0);
            return;
        }

        Theming.ThemeManager.Apply(Capturix.Common.AppSettings.Load().Theme);
        var window = new MainWindow();
        MainWindow = window;

        _pipe = EditorPipe.TryListen(message => Dispatcher.BeginInvoke(() => window.HandleExternalRequest(message)));
        if (_pipe is null && EditorPipe.TrySend(fileArg ?? "", timeoutMs: 1000))
        {
            // Another editor started in the same instant and won the pipe; let it handle the file.
            Shutdown(0);
            return;
        }

        window.Show();
        if (fileArg is not null && File.Exists(fileArg))
            window.OpenFile(fileArg);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipe?.Dispose();
        base.OnExit(e);
    }
}
