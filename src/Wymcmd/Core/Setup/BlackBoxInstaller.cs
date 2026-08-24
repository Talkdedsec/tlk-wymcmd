using System.Security;
using Microsoft.Win32;
using Wymcmd.Core.Diagnostics;
using Wymcmd.Core.Store;

namespace Wymcmd.Core.Setup;

/// <summary>
/// The black box: an ETW AutoLogger that Windows itself starts at boot and writes into a
/// circular file. Nothing of ours stays resident - there is no wymcmd process, no service and
/// no CPU cost - yet when you open the tool later the full history is sitting in the trace.
/// </summary>
public static class BlackBoxInstaller
{
    private const string AutologgerRoot = @"SYSTEM\CurrentControlSet\Control\WMI\Autologger";
    private const string KernelProcessProvider = "{22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716}";
    private const long KeywordProcess = 0x10;

    private static string SessionKey => $@"{AutologgerRoot}\{AppPaths.BlackBoxSessionName}";

    /// <summary>
    /// The autologger key is admin-readable only, so a normal user can see that it exists but
    /// not what is in it. Both answers are reported honestly instead of guessing.
    /// </summary>
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionKey);
            return key is not null;
        }
        catch (SecurityException)
        {
            return true;
        }
    }

    public static bool? IsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionKey);
            if (key is null) return false;
            return key.GetValue("Start") is int start && start == 1;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    public static long TraceSizeBytes()
        => File.Exists(AppPaths.BlackBoxTrace) ? new FileInfo(AppPaths.BlackBoxTrace).Length : 0;

    /// <summary>Writes the autologger definition. Takes effect on the next boot.</summary>
    public static void Install(int maxFileSizeMb = 64)
    {
        SharedRoot.Ensure();
        Directory.CreateDirectory(AppPaths.Root);

        using var key = Registry.LocalMachine.CreateSubKey(SessionKey, true)
            ?? throw new UnauthorizedAccessException("cannot write the autologger key");

        key.SetValue("Start", 1, RegistryValueKind.DWord);
        key.SetValue("Guid", "{" + Guid.NewGuid().ToString("d") + "}", RegistryValueKind.String);
        key.SetValue("FileName", AppPaths.BlackBoxTrace, RegistryValueKind.String);
        key.SetValue("LogFileMode", 0x00000002, RegistryValueKind.DWord);  // circular file
        key.SetValue("MaxFileSize", maxFileSizeMb, RegistryValueKind.DWord);
        key.SetValue("BufferSize", 64, RegistryValueKind.DWord);           // KB per buffer
        key.SetValue("MinimumBuffers", 4, RegistryValueKind.DWord);
        key.SetValue("MaximumBuffers", 16, RegistryValueKind.DWord);
        key.SetValue("FlushTimer", 5, RegistryValueKind.DWord);
        key.SetValue("ClockType", 1, RegistryValueKind.DWord);             // QPC

        using var provider = key.CreateSubKey(KernelProcessProvider, true)
            ?? throw new UnauthorizedAccessException("cannot write the provider key");

        provider.SetValue("Enabled", 1, RegistryValueKind.DWord);
        provider.SetValue("EnableLevel", 5, RegistryValueKind.DWord);      // verbose
        provider.SetValue("MatchAnyKeyword", KeywordProcess, RegistryValueKind.QWord);

        Log.Info($"black box installed, trace file {AppPaths.BlackBoxTrace} capped at {maxFileSizeMb} MB");
    }

    public static void Uninstall(bool deleteTrace = true)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(SessionKey, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }

        if (deleteTrace && File.Exists(AppPaths.BlackBoxTrace))
        {
            try
            {
                File.Delete(AppPaths.BlackBoxTrace);
            }
            catch (IOException ex)
            {
                // The session still holds the file until the next boot.
                Log.Warn("trace file still in use: " + ex.Message);
            }
        }

        Log.Info("black box removed");
    }
}
