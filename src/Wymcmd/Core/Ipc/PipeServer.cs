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
    private readonly ConcurrentDictionary<NamedPipeServerStream, StreamWriter> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _accepting;

    public void Start()
    {
        _accepting ??= Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        Log.Info($"pipe server listening on \\\\.\\pipe\\{AppPaths.PipeName}");
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

        foreach (var (stream, writer) in _clients)
        {
            try
            {
                writer.WriteLine(line);
            }
            catch (IOException)
            {
                Drop(stream);
            }
        }
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

                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _clients[stream] = writer;
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

    private static NamedPipeServerStream Create()
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
            AppPaths.PipeName,
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
