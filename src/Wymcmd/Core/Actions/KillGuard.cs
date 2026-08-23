using System.Diagnostics;

namespace Wymcmd.Core.Actions;

/// <summary>
/// One gate that every terminate path goes through - CLI, rules and the UI alike.
/// Killing any of these takes the machine down with it, so no flag opens this door.
/// </summary>
public static class KillGuard
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "registry", "memory compression",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "sihost.exe", "dwm.exe",
        "svchost.exe", "trustedinstaller.exe", "msmpeng.exe"
    };

    public static readonly int SelfPid = Environment.ProcessId;

    public static bool IsProtected(int pid, string imageName)
        => pid <= 10 || pid == SelfPid || Protected.Contains(imageName.Trim());

    public static bool IsProtected(int pid)
    {
        if (pid <= 10 || pid == SelfPid) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return Protected.Contains(process.ProcessName + ".exe");
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
