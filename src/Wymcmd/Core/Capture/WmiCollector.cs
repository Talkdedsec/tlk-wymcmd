using System.Management;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Capture;

/// <summary>
/// The no-admin fallback. WMI polls, so anything that lives shorter than the poll window is
/// simply never seen - the UI says so instead of pretending the list is complete.
/// </summary>
public sealed class WmiCollector : ICollector
{
    private const double PollSeconds = 0.3;

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _running;

    public EvidenceSource Source => EvidenceSource.Wmi;
    public bool Lossless => false;
    public bool Available => true;

    public event Action<RawStart>? Started;
    public event Action<RawStop>? Stopped;

    public void Start()
    {
        if (_running) return;

        var scope = new ManagementScope(@"\\.\root\cimv2");
        scope.Connect();

        _startWatcher = new ManagementEventWatcher(scope, new WqlEventQuery(
            $"SELECT * FROM __InstanceCreationEvent WITHIN {PollSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            "WHERE TargetInstance ISA 'Win32_Process'"));
        _startWatcher.EventArrived += OnStartArrived;
        _startWatcher.Start();

        _stopWatcher = new ManagementEventWatcher(scope, new WqlEventQuery(
            $"SELECT * FROM __InstanceDeletionEvent WITHIN {PollSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            "WHERE TargetInstance ISA 'Win32_Process'"));
        _stopWatcher.EventArrived += OnStopArrived;
        _stopWatcher.Start();

        _running = true;
        Log.Info("wmi collector started (degraded mode: short-lived processes can be missed)");
    }

    private void OnStartArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid = Convert.ToInt32(target["ProcessId"]);
            var parent = Convert.ToInt32(target["ParentProcessId"]);
            var path = target["ExecutablePath"] as string ?? "";
            var name = target["Name"] as string ?? "";
            var commandLine = target["CommandLine"] as string ?? "";
            var sessionId = target["SessionId"] is null ? 0 : Convert.ToInt32(target["SessionId"]);
            var created = ParseCimDate(target["CreationDate"] as string) ?? DateTime.Now;

            Started?.Invoke(new RawStart(pid, parent, 0, name, path, commandLine, sessionId, created, EvidenceSource.Wmi));
        }
        catch (Exception ex)
        {
            Log.Warn("wmi start event unreadable: " + ex.Message);
        }
    }

    private void OnStopArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid = Convert.ToInt32(target["ProcessId"]);
            Stopped?.Invoke(new RawStop(pid, DateTime.Now, null));
        }
        catch (Exception ex)
        {
            Log.Warn("wmi stop event unreadable: " + ex.Message);
        }
    }

    private static DateTime? ParseCimDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        try { _startWatcher?.Stop(); } catch { /* nothing to do */ }
        try { _stopWatcher?.Stop(); } catch { /* nothing to do */ }
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        _startWatcher = null;
        _stopWatcher = null;
        Log.Info("wmi collector stopped");
    }

    public void Dispose() => Stop();
}
