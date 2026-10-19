using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Capturix.Common.Ipc;

/// <summary>
/// Single-editor hand-off: the running editor listens on a named pipe and other processes
/// send it file paths to open. A pipe gives framing, back-pressure and an owner check for
/// free, which a shared-memory block with named events would have to build by hand.
/// Messages are one UTF-8 line each: a file path, or an empty line meaning "come to front".
/// </summary>
public static class EditorPipe
{
    /// <summary>Per user and per logon session, so other users or sessions never collide.</summary>
    public static string PipeName =>
        $"Capturix.Editor.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>Sends <paramref name="message"/> to a running editor. Returns false if none is listening.</summary>
    public static bool TrySend(string message, int timeoutMs = 500)
    {
        try
        {
            // CurrentUserOnly: refuse to talk to a pipe owned by another account.
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(message.Replace('\n', ' ') + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts listening. Returns null if another editor already owns the pipe.
    /// <paramref name="onMessage"/> runs on a background thread.
    /// </summary>
    public static Listener? TryListen(Action<string> onMessage)
    {
        try
        {
            var first = CreateServer();
            return new Listener(first, onMessage);
        }
        catch (IOException)
        {
            return null; // pipe instance limit reached: another editor is the server
        }
    }

    private static NamedPipeServerStream CreateServer() =>
        new(PipeName, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public sealed class Listener : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        internal Listener(NamedPipeServerStream first, Action<string> onMessage)
        {
            _loop = Task.Run(() => RunAsync(first, onMessage, _cts.Token));
        }

        private static async Task RunAsync(NamedPipeServerStream server, Action<string> onMessage, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using (var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true))
                    {
                        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                            onMessage(line.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    Log.Error("Error en el pipe del editor", ex);
                }
                finally
                {
                    server.Dispose();
                }

                if (ct.IsCancellationRequested)
                    break;
                try
                {
                    server = CreateServer(); // one instance at a time: recreate for the next client
                }
                catch (IOException ex)
                {
                    // Another editor grabbed the name in the gap; it is the server now.
                    Log.Error("El pipe del editor pasó a otra instancia", ex);
                    break;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // Shutting down; nothing useful to report.
            }
            _cts.Dispose();
        }
    }
}
