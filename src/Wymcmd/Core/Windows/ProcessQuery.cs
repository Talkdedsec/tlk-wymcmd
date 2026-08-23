using System.Runtime.InteropServices;
using System.Text;
using static Wymcmd.Core.Windows.NativeMethods;

namespace Wymcmd.Core.Windows;

/// <summary>
/// Reads what the OS will tell us about a live process. Every call is best-effort:
/// a process can die between two lines, and protected processes refuse most handles.
/// </summary>
internal static class ProcessQuery
{
    // 64-bit PEB layout
    private const int PebProcessParametersOffset = 0x20;
    private const int ParamsCurrentDirectoryOffset = 0x38;
    private const int ParamsImagePathOffset = 0x60;
    private const int ParamsCommandLineOffset = 0x70;

    public static string? ImagePath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            uint size = 1024;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    public static DateTime? StartTime(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(handle, out var created, out _, out _, out _)
                ? DateTime.FromFileTime(created)
                : null;
        }
        finally { CloseHandle(handle); }
    }

    public static int? SessionId(int pid)
        => ProcessIdToSessionId((uint)pid, out var session) ? (int)session : null;

    public static bool IsAlive(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return GetExitCodeProcess(handle, out var code) && code == 259; // STILL_ACTIVE
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Full command line straight out of the target PEB - no WMI round trip.</summary>
    public static string? CommandLine(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return ReadPebString(handle, ParamsCommandLineOffset);
        }
        catch
        {
            return null;
        }
        finally { CloseHandle(handle); }
    }

    public static string? WorkingDirectory(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return ReadPebString(handle, ParamsCurrentDirectoryOffset);
        }
        catch
        {
            return null;
        }
        finally { CloseHandle(handle); }
    }

    public static (string? Sid, string? Name) User(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return (null, null);
        try
        {
            if (!OpenProcessToken(handle, TOKEN_QUERY, out var token)) return (null, null);
            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
                if (needed <= 0) return (null, null);

                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return (null, null);

                    var sidPtr = Marshal.PtrToStructure<SID_AND_ATTRIBUTES>(buffer).Sid;
                    string? sidText = null;
                    if (ConvertSidToStringSidW(sidPtr, out var sidString))
                    {
                        sidText = Marshal.PtrToStringUni(sidString);
                        LocalFree(sidString);
                    }

                    return (sidText, AccountName(sidPtr));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        catch
        {
            return (null, null);
        }
        finally { CloseHandle(handle); }
    }

    public static bool? IsElevated(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(handle, TOKEN_QUERY, out var token)) return null;
            try
            {
                var size = Marshal.SizeOf<TOKEN_ELEVATION>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenElevation, buffer, size, out _)) return null;
                    return Marshal.PtrToStructure<TOKEN_ELEVATION>(buffer).TokenIsElevated != 0;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        catch
        {
            return null;
        }
        finally { CloseHandle(handle); }
    }

    public static int? ParentPid(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
        }
        finally { CloseHandle(handle); }
    }

    private static string? ReadPebString(IntPtr handle, int fieldOffset)
    {
        var info = new PROCESS_BASIC_INFORMATION();
        if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out _) != 0) return null;
        if (info.PebBaseAddress == IntPtr.Zero) return null;

        var parameters = ReadPointer(handle, info.PebBaseAddress + PebProcessParametersOffset);
        if (parameters == IntPtr.Zero) return null;

        var unicode = ReadStruct<UNICODE_STRING>(handle, parameters + fieldOffset);
        if (unicode is not { } value || value.Length == 0 || value.Buffer == IntPtr.Zero) return null;

        var bytes = new byte[value.Length];
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            if (!ReadProcessMemory(handle, value.Buffer, pinned.AddrOfPinnedObject(), value.Length, out var read) || read == IntPtr.Zero)
                return null;
            return Encoding.Unicode.GetString(bytes, 0, (int)read).TrimEnd('\0');
        }
        finally { pinned.Free(); }
    }

    private static IntPtr ReadPointer(IntPtr handle, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (!ReadProcessMemory(handle, address, pinned.AddrOfPinnedObject(), IntPtr.Size, out _)) return IntPtr.Zero;
            return (IntPtr)BitConverter.ToInt64(buffer, 0);
        }
        finally { pinned.Free(); }
    }

    private static T? ReadStruct<T>(IntPtr handle, IntPtr address) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!ReadProcessMemory(handle, address, buffer, size, out _)) return null;
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? AccountName(IntPtr sid)
    {
        uint nameLength = 256, domainLength = 256;
        var name = new StringBuilder(256);
        var domain = new StringBuilder(256);
        if (!LookupAccountSidW(null, sid, name, ref nameLength, domain, ref domainLength, out _)) return null;
        return domain.Length > 0 ? domain + "\\" + name : name.ToString();
    }
}
