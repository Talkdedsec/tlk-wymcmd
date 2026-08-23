using System.Runtime.InteropServices;
using Wymcmd.Core.Windows;
using static Wymcmd.Core.Windows.NativeMethods;

namespace Wymcmd.Core.Tree;

/// <summary>One toolhelp pass over everything currently running, enriched in parallel.</summary>
internal static class ProcessSnapshot
{
    public static IReadOnlyList<ProcRecord> Capture()
    {
        var skeleton = Walk();
        var records = new ProcRecord[skeleton.Count];

        Parallel.For(0, skeleton.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var (pid, parentPid, imageName) = skeleton[i];
            records[i] = new ProcRecord
            {
                Pid = pid,
                ParentPid = parentPid,
                ImageName = imageName,
                ImagePath = ProcessQuery.ImagePath(pid) ?? "",
                CommandLine = ProcessQuery.CommandLine(pid) ?? "",
                StartTime = ProcessQuery.StartTime(pid) ?? DateTime.Now,
                SessionId = ProcessQuery.SessionId(pid) ?? 0,
                UserName = ProcessQuery.User(pid).Name
            };
        });

        return records;
    }

    private static List<(int Pid, int ParentPid, string ImageName)> Walk()
    {
        var result = new List<(int, int, string)>(320);
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;

            do
            {
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
