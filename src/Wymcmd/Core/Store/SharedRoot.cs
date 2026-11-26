using System.Security.AccessControl;
using System.Security.Principal;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Store;

/// <summary>
/// The service runs as LocalSystem and the UI runs as you; both have to reach the same
/// database. An elevated setup step creates the shared folder once and opens it to Users,
/// otherwise everything falls back to per-user data and the two never meet.
///
/// What the service acts on - the rules, and the record of what setup changed - sits in a
/// subfolder only administrators can write. A folder every user can write must never decide
/// what a LocalSystem process kills.
/// </summary>
public static class SharedRoot
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private const InheritanceFlags Children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static string Path => global::System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "wymcmd");

    /// <summary>Readable by everyone, writable by administrators and LocalSystem only.</summary>
    public static string ProtectedPath => global::System.IO.Path.Combine(Path, "protected");

    public static bool Exists => Directory.Exists(Path);

    /// <summary>Creates ProgramData\wymcmd and its protected subfolder. Needs elevation.</summary>
    public static bool Ensure()
    {
        try
        {
            var root = new DirectoryInfo(Path);
            if (root.Exists && root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Log.Error($"{Path} is a link to somewhere else; refusing to use it");
                return false;
            }

            if (root.Exists) root.SetAccessControl(RootSecurity());
            else root.Create(RootSecurity());

            var guarded = new DirectoryInfo(ProtectedPath);

            // A folder somebody else made first may hold files of their choosing. Taking it over
            // would bless them, so it goes.
            if (guarded.Exists && (guarded.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                                   !FileTrust.IsAdminOnly(guarded.GetAccessControl())))
            {
                Log.Warn($"{ProtectedPath} was writable by non-administrators and has been reset");
                guarded.Delete(recursive: !guarded.Attributes.HasFlag(FileAttributes.ReparsePoint));
                guarded.Refresh();
            }

            if (!guarded.Exists) guarded.Create(ProtectedSecurity());

            // Creating a folder that is already there changes nothing, and anyone may create
            // folders here. If somebody put theirs in between the two lines above, it is theirs.
            guarded.Refresh();
            if (guarded.Attributes.HasFlag(FileAttributes.ReparsePoint) || !FileTrust.IsAdminOnly(guarded.GetAccessControl()))
            {
                Log.Error($"{ProtectedPath} is not ours: somebody else created it first");
                return false;
            }

            // The journal from before 0.4.2 sat where any user could write it. What it says
            // decides which audit settings uninstall switches off, so it is not carried over.
            // Named from the shared folder itself: with WYMCMD_HOME set the data root is somewhere
            // else, and there the journal in the root is the real one.
            var oldJournal = global::System.IO.Path.Combine(Path, "setup-journal.json");
            if (File.Exists(oldJournal)) File.Delete(oldJournal);

            // The root was resolved before this folder existed; look again.
            AppPaths.Reset();

            Rules.RuleSet.CarryOverLegacy();

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

    /// <summary>
    /// Users may fill the folder - the window writes the database as you - but may not delete or
    /// rename the folder itself, which would let them put a link in its place.
    /// </summary>
    internal static DirectorySecurity RootSecurity()
    {
        var security = Owned<DirectorySecurity>();
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users,
            FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Synchronize,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            Children, PropagationFlags.InheritOnly, AccessControlType.Allow));
        return security;
    }

    internal static DirectorySecurity ProtectedSecurity()
    {
        var security = Owned<DirectorySecurity>();
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, Children, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, Children, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Owned by Administrators and cut off from whatever the parent folder allows.</summary>
    private static T Owned<T>() where T : FileSystemSecurity, new()
    {
        var security = new T();
        security.SetOwner(Admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return security;
    }

    /// <summary>
    /// Makes a file in the protected folder what the service will accept, whatever the account
    /// that wrote it would have left as owner.
    /// </summary>
    internal static void Seal(string file)
    {
        var security = Owned<FileSecurity>();
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
