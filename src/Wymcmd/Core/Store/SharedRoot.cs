using System.Security.AccessControl;
using System.Security.Principal;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Store;

/// <summary>
/// The service runs as LocalSystem and the UI runs as you; both have to reach the same
/// database. An elevated setup step creates the shared folder once and opens it to Users,
/// otherwise everything falls back to per-user data and the two never meet.
/// </summary>
public static class SharedRoot
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "wymcmd");

    public static bool Exists => Directory.Exists(Path);

    /// <summary>Creates ProgramData\wymcmd and grants Users modify rights. Needs elevation.</summary>
    public static bool Ensure()
    {
        try
        {
            var directory = Directory.CreateDirectory(Path);

            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);

            Log.Info($"shared data folder ready at {Path}");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn("cannot create the shared data folder without administrator rights");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("shared data folder setup failed", ex);
            return false;
        }
    }
}
