using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Wymcmd.Core.Model;

namespace Wymcmd.Core.Sign;

/// <summary>
/// Authenticode check with a cache keyed on path + size + write time, because the same
/// dozen binaries show up over and over and WinVerifyTrust is the expensive part of enrichment.
/// </summary>
public static class SignatureVerifier
{
    private static readonly ConcurrentDictionary<string, SignatureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureInfo Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return SignatureInfo.Unknown;

        string key;
        try
        {
            var info = new FileInfo(path);
            key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return SignatureInfo.Unknown;
        }

        return Cache.GetOrAdd(key, _ => Verify(path));
    }

    /// <summary>Cached answer only - used on hot paths that must not block.</summary>
    public static SignatureInfo? Peek(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return Cache.TryGetValue($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", out var hit) ? hit : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SignatureInfo Verify(string path)
    {
        var status = TrustStatus(path);
        string? publisher = null;
        string? thumbprint = null;
        string? signedFile = path;

        if (status == SignatureStatus.Unsigned)
        {
            // No embedded signature: the file may still be vouched for by a catalog.
            var catalog = CatalogSignature.FindCatalogFor(path);
            if (catalog is not null && File.Exists(catalog))
            {
                status = TrustStatus(catalog) == SignatureStatus.Valid ? SignatureStatus.Valid : status;
                signedFile = catalog;
            }
        }

        if (status != SignatureStatus.Unsigned)
        {
            try
            {
                // CreateFromSignedFile is the only in-box way to pull the signer certificate out of a PE.
#pragma warning disable SYSLIB0057
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
#pragma warning restore SYSLIB0057
                publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
                thumbprint = certificate.Thumbprint;
            }
            catch (Exception)
            {
                // Signed but the certificate is unreadable - keep the trust verdict, drop the name.
            }
        }

        return new SignatureInfo { Status = status, Publisher = publisher, Thumbprint = thumbprint };
    }

    private static SignatureStatus TrustStatus(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE - offline friendly
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePointer,
                dwStateAction = 0,
                dwProvFlags = 0x00000010 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };

            var action = WintrustActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result switch
            {
                0 => SignatureStatus.Valid,
                unchecked((int)0x800B0100) => SignatureStatus.Unsigned,  // TRUST_E_NOSIGNATURE
                unchecked((int)0x800B0101) => SignatureStatus.Expired,   // CERT_E_EXPIRED
                unchecked((int)0x800B010A) => SignatureStatus.Invalid,   // CERT_E_CHAINING
                unchecked((int)0x80092003) => SignatureStatus.Unknown,   // CRYPT_E_FILE_ERROR
                _ => SignatureStatus.Invalid
            };
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
