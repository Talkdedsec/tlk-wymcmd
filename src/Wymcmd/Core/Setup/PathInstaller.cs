using System.Runtime.InteropServices;
using Microsoft.Win32;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Setup;

public sealed record InstallResult(string Directory, bool CopiedFiles, bool PathChanged, bool ShortcutCreated);

/// <summary>
/// Puts the two executables somewhere permanent and adds that folder to the user's PATH, so
/// "wymcmd" works from any prompt. Per-user by design: no administrator, no installer, and
/// uninstall is the same two steps in reverse.
/// </summary>
public static class PathInstaller
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "wymcmd");

    public static bool IsOnPath(string directory)
        => CurrentUserPath()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => entry.TrimEnd('\\').Equals(directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    public static InstallResult Install(string? targetDirectory = null, bool createShortcut = true)
    {
        var directory = targetDirectory ?? DefaultDirectory;
        Directory.CreateDirectory(directory);

        var copied = CopyPayload(directory);
        var pathChanged = AddToPath(directory);
        var shortcut = createShortcut && CreateStartMenuShortcut(Path.Combine(directory, "wymcmd.exe"));

        return new InstallResult(directory, copied, pathChanged, shortcut);
    }

    public static bool Uninstall(string? targetDirectory = null)
    {
        var directory = targetDirectory ?? DefaultDirectory;
        var removed = RemoveFromPath(directory);
        RemoveStartMenuShortcut();

        try
        {
            if (Directory.Exists(directory))
            {
                // The copy we are running from cannot delete itself; leave it for the shell.
                var running = Environment.ProcessPath;
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (string.Equals(file, running, StringComparison.OrdinalIgnoreCase)) continue;
                    File.Delete(file);
                }

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
                else
                    removed = true;
            }
        }
        catch (IOException ex)
        {
            Log.Warn("could not remove the install folder: " + ex.Message);
        }

        return removed;
    }

    private static bool CopyPayload(string directory)
    {
        var source = Environment.ProcessPath;
        if (source is null) return false;

        var sourceDirectory = Path.GetDirectoryName(source)!;
        if (string.Equals(sourceDirectory.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return false;

        var copied = false;
        foreach (var name in new[] { "wymcmd.exe", "wymcmd.com" })
        {
            var from = Path.Combine(sourceDirectory, name);
            if (!File.Exists(from)) continue;

            File.Copy(from, Path.Combine(directory, name), overwrite: true);
            copied = true;
        }

        // The published build keeps its translations next to the executable.
        var assets = Path.Combine(sourceDirectory, "Assets", "i18n");
        if (Directory.Exists(assets))
        {
            var target = Path.Combine(directory, "Assets", "i18n");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(assets, "*.json"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return copied;
    }

    private static string CurrentUserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    private static bool AddToPath(string directory)
    {
        if (IsOnPath(directory)) return false;

        var current = CurrentUserPath();
        var updated = current.Length == 0 ? directory : current.TrimEnd(';') + ";" + directory;

        using var key = Registry.CurrentUser.CreateSubKey("Environment", true)
            ?? throw new UnauthorizedAccessException("cannot write the user environment");

        key.SetValue("Path", updated, RegistryValueKind.ExpandString);
        Announce();
        Log.Info($"added {directory} to the user PATH");
        return true;
    }

    private static bool RemoveFromPath(string directory)
    {
        var current = CurrentUserPath();
        if (current.Length == 0) return false;

        var entries = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !entry.TrimEnd('\\').Equals(directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (entries.Length == current.Split(';', StringSplitOptions.RemoveEmptyEntries).Length) return false;

        using var key = Registry.CurrentUser.CreateSubKey("Environment", true);
        key?.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
        Announce();
        return true;
    }

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs", "Why My CMD Opened.lnk");

    private static bool CreateStartMenuShortcut(string target)
    {
        if (!File.Exists(target)) return false;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.Description = "Why My CMD Opened";
            shortcut.Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("could not create the start menu shortcut: " + ex.Message);
            return false;
        }
    }

    private static void RemoveStartMenuShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
        catch (IOException)
        {
            // Someone has the shortcut open; not worth failing an uninstall over.
        }
    }

    /// <summary>Tells already-running shells and Explorer that the environment changed.</summary>
    private static void Announce()
    {
        SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 2000, out _);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam,
        int flags, int timeout, out IntPtr result);
}
