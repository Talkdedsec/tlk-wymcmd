using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Forensic;

public sealed record AmCacheEntry(string ImageName, string Path, DateTime? FirstSeen, string? Sha1, string? Publisher);

/// <summary>
/// AmCache answers the question no run ledger can: when did this binary first appear on this
/// machine at all. A file that showed up twenty minutes ago and is already opening consoles is
/// a different story from one that has been here since the install.
///
/// The hive is held open by the system, so it is copied first and mounted privately with
/// RegLoadAppKey. Needs administrator rights; without them this simply returns nothing.
/// </summary>
public static class AmCacheReader
{
    private const string HivePath = @"appcompat\Programs\Amcache.hve";
    private const string InventoryKey = @"Root\InventoryApplicationFile";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly Lock Sync = new();
    private static IReadOnlyList<AmCacheEntry> _cache = [];
    private static DateTime _readAt = DateTime.MinValue;

    public static IReadOnlyList<AmCacheEntry> Entries()
    {
        lock (Sync)
        {
            if (DateTime.Now - _readAt < CacheFor) return _cache;
            _readAt = DateTime.Now;
        }

        var entries = Read();

        lock (Sync)
        {
            _cache = entries;
            return _cache;
        }
    }

    public static AmCacheEntry? Find(string imagePath, string imageName)
    {
        var entries = Entries();
        if (entries.Count == 0) return null;

        return entries.FirstOrDefault(entry =>
                   imagePath.Length > 0 && entry.Path.Equals(imagePath, StringComparison.OrdinalIgnoreCase))
               ?? entries.FirstOrDefault(entry => entry.ImageName.Equals(imageName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<AmCacheEntry> Read()
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), HivePath);
        if (!File.Exists(source)) return [];

        var copy = Path.Combine(Path.GetTempPath(), $"wymcmd-amcache-{Environment.ProcessId}.hve");

        try
        {
            File.Copy(source, copy, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("amcache hive needs administrator rights: " + ex.Message);
            return [];
        }

        try
        {
            var status = RegLoadAppKey(copy, out var handle, KeyRead, 0, 0);
            if (status != 0 || handle.IsInvalid)
            {
                Log.Debug($"amcache hive could not be mounted (status {status})");
                return [];
            }

            using (handle)
            using (var root = RegistryKey.FromHandle(handle))
            using (var inventory = root.OpenSubKey(InventoryKey))
            {
                if (inventory is null) return [];

                var entries = new List<AmCacheEntry>(2048);
                foreach (var name in inventory.GetSubKeyNames())
                {
                    using var item = inventory.OpenSubKey(name);
                    if (item is null) continue;

                    var path = item.GetValue("LowerCaseLongPath") as string;
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    entries.Add(new AmCacheEntry(
                        Path.GetFileName(path),
                        path,
                        LastWriteTime(item),
                        CleanHash(item.GetValue("FileId") as string),
                        item.GetValue("Publisher") as string));
                }

                return entries;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("amcache unreadable: " + ex.Message);
            return [];
        }
        finally
        {
            try { File.Delete(copy); } catch (IOException) { /* left for the temp folder */ }
        }
    }

    /// <summary>The key's own write time is when this machine first catalogued the binary.</summary>
    private static DateTime? LastWriteTime(RegistryKey key)
    {
        var status = RegQueryInfoKey(key.Handle, null, IntPtr.Zero, IntPtr.Zero,
            out _, out _, out _, out _, out _, out _, out _, out var fileTime);

        if (status != 0) return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>AmCache stores the SHA-1 with four leading zeroes in front of it.</summary>
    private static string? CleanHash(string? fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId)) return null;
        var trimmed = fileId.Trim();
        return trimmed.Length == 44 && trimmed.StartsWith("0000", StringComparison.Ordinal)
            ? trimmed[4..]
            : trimmed;
    }

    private const int KeyRead = 0x20019;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadAppKey(string file, out SafeRegistryHandle result,
        int samDesired, int options, int reserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, System.Text.StringBuilder? className,
        IntPtr classLength, IntPtr reserved, out int subKeys, out int maxSubKeyLength, out int maxClassLength,
        out int values, out int maxValueNameLength, out int maxValueLength, out int securityDescriptorSize,
        out long lastWriteTime);
}
