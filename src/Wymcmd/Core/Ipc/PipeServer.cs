using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Ipc;

/// <summary>
/// The watchdog service runs as LocalSystem; this is how the interface sees its events the
/// moment they happen instead of waiting for the next database read. One line of JSON per
/// event, no request/response protocol - clients subscribe and listen.
/// </summary>
public sealed class PipeServer : IDisposable
{
    /// <summary>
    /// How long a broadcast may take, however many clients there are. A reader that has fallen
    /// 64 KB behind is not going to catch up in the next quarter second.
    /// </summary>
    private static readonly TimeSpan WriteBudget = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private Task? _accepting;

    /// <param name="pipeName">Another name than the service's own - what the tests listen on.</param>
    public PipeServer(string? pipeName = null) => _pipeName = pipeName ?? AppPaths.PipeName;

    public int ClientCount => _clients.Count;

    public void Start()
    {
        _accepting ??= Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        Log.Info($"pipe server listening on \\\\.\\pipe\\{_pipeName}");
    }

    public void Broadcast(ProcEvent evt)
    {
        if (_clients.IsEmpty) return;

        var line = JsonSerializer.Serialize(new
        {
            pid = evt.Pid,
            parentPid = evt.ParentPid,
            startTime = evt.StartTime,
            image = evt.ImageName,
            imagePath = evt.ImagePath,
            commandLine = evt.CommandLine,
            window = evt.Window.ToString(),
            risk = evt.Risk,
            sourceKind = (evt.Source?.Kind ?? LaunchSourceKind.Unknown).ToString(),
            sourceName = evt.Source?.Name
        });

        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        // This runs on the capture worker. A client that stopped reading fills its buffer and
        // the next write waits for it - which used to mean capture and rule enforcement waited
        // too, for as long as any logged-in user cared to hold them. Every write is started
        // first and they share one budget, so two hundred silent clients cost what one does.
        var writes = new List<(NamedPipeServerStream Stream, Task Write)>();
        foreach (var stream in _clients.Keys)
        {
            try
            {
                writes.Add((stream, stream.WriteAsync(bytes).AsTask()));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Drop(stream);
            }
        }

        try { Task.WaitAll(writes.Select(w => w.Write).ToArray(), WriteBudget); }
        catch (AggregateException) { /* the ones that failed are dropped below */ }

        foreach (var (stream, write) in writes)
            if (!write.IsCompletedSuccessfully) Drop(stream);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = Create();
                await stream.WaitForConnectionAsync(token);

                _clients[stream] = 0;
                Log.Debug("pipe client connected");
            }
            catch (OperationCanceledException)
            {
                stream?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("pipe accept failed: " + ex.Message);
                stream?.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
        }
    }

    private NamedPipeServerStream Create()
    {
        // Administrators and the interactive user may read; nobody else can see the stream.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.Out,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 64 * 1024,
            security);
    }

    private void Drop(NamedPipeServerStream stream)
    {
        if (_clients.TryRemove(stream, out _))
        {
            try { stream.Dispose(); } catch { /* already gone */ }
            Log.Debug("pipe client disconnected");
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var stream in _clients.Keys) Drop(stream);
        _shutdown.Dispose();
    }
}
