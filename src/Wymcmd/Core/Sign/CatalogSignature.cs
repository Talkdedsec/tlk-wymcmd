using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wymcmd.Core.Sign;

/// <summary>
/// Most Windows binaries - cmd.exe included - carry no embedded signature; they are vouched
/// for by a security catalog. Without this check every system tool looks unsigned, which
/// would poison the risk score of practically every event.
/// </summary>
internal static class CatalogSignature
{
    public static string? FindCatalogFor(string filePath)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
        {
            if (!CryptCATAdminAcquireContext(out admin, IntPtr.Zero, 0)) return null;
        }

        try
        {
            using var file = File.OpenRead(filePath);
            var handle = file.SafeFileHandle;

            uint hashLength = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref hashLength, null, 0) && hashLength == 0)
                return null;

            var hash = new byte[hashLength];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref hashLength, hash, 0))
                return null;

            var catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, hashLength, 0, IntPtr.Zero);
            if (catalog == IntPtr.Zero) return null;

            try
            {
                var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                return CryptCATCatalogInfoFromContext(catalog, ref info, 0) ? info.wszCatalogFile : null;
            }
            finally
            {
                CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext(out IntPtr hCatAdmin, IntPtr pgSubsystem, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr hCatAdmin, IntPtr pgSubsystem,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile,
        ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash,
        uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }
}
