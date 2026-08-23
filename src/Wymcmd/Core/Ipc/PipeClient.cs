using System.IO.Pipes;
using System.Text.Json;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Ipc;

/// <summary>
/// Reads the watchdog service's live feed. Used by the interface when the service is the one
/// doing the capturing, so two collectors never fight over the same ETW session.
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _reader;

    public event Action<ProcEvent>? Received;
    public event Action<bool>? ConnectionChanged;

    public bool Connected { get; private set; }

    public void Start() => _reader ??= Task.Run(() => ReadLoopAsync(_shutdown.Token));

    public static bool ServiceIsListening()
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.In);
            probe.Connect(200);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var stream = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await stream.ConnectAsync(token);

                Connected = true;
                ConnectionChanged?.Invoke(true);

                using var reader = new StreamReader(stream);
                while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
                {
                    var evt = Parse(line);
                    if (evt is not null) Received?.Invoke(evt);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("pipe read ended: " + ex.Message);
            }
            finally
            {
                if (Connected)
                {
                    Connected = false;
                    ConnectionChanged?.Invoke(false);
                }
            }

            // The service may be restarting; keep trying, quietly.
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static ProcEvent? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var evt = new ProcEvent
            {
                Pid = root.GetProperty("pid").GetInt32(),
                ParentPid = root.GetProperty("parentPid").GetInt32(),
                StartTime = root.GetProperty("startTime").GetDateTime(),
                ImageName = root.GetProperty("image").GetString() ?? "",
                ImagePath = root.GetProperty("imagePath").GetString() ?? "",
                CommandLine = root.GetProperty("commandLine").GetString() ?? "",
                Risk = root.GetProperty("risk").GetInt32(),
                Sources = EvidenceSource.Etw,
                Confidence = Confidence.Certain
            };

            if (Enum.TryParse<WindowVisibility>(root.GetProperty("window").GetString(), out var window))
                evt.Window = window;

            if (Enum.TryParse<LaunchSourceKind>(root.GetProperty("sourceKind").GetString(), out var kind))
            {
                evt.Source = new LaunchSource
                {
                    Kind = kind,
                    Name = root.TryGetProperty("sourceName", out var name) ? name.GetString() : null,
                    Confidence = Confidence.High
                };
            }

            return evt;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
