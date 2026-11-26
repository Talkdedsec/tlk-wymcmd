using System.Security.AccessControl;
using System.Security.Principal;

namespace Wymcmd.Core.Store;

/// <summary>
/// Answers one question: can anybody who is not an administrator change this file? The service
/// runs as LocalSystem and acts on what it reads, so it only reads files that pass.
/// </summary>
public static class FileTrust
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") // TrustedInstaller
    ];

    private const FileSystemRights Changes =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // GENERIC_WRITE and GENERIC_ALL, which an ACE may carry instead of the specific bits.
    private const int GenericChanges = 0x40000000 | 0x10000000;

    /// <summary>The file and the folder holding it are both owned and writable by administrators only.</summary>
    public static bool IsAdminOnly(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            var folder = info.Directory;
            if (folder is null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            return IsAdminOnly(info.GetAccessControl()) && IsAdminOnly(folder.GetAccessControl());
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsAdminOnly(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner))
            return false;

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        // No entries at all is a null DACL, which lets everyone in.
        if (rules.Count == 0) return false;

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;

            var changes = (rule.FileSystemRights & Changes) != 0 || ((int)rule.FileSystemRights & GenericChanges) != 0;
            if (changes && !Trusted.Contains((SecurityIdentifier)rule.IdentityReference)) return false;
        }

        return true;
    }
}
