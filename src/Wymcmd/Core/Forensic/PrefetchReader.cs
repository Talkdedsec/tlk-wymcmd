using System.Runtime.InteropServices;
using System.Text;
using Wymcmd.Core.Diagnostics;

namespace Wymcmd.Core.Forensic;

public sealed record PrefetchEntry(string ImageName, int RunCount, IReadOnlyList<DateTime> RunTimes)
{
    public DateTime? LastRun => RunTimes.Count > 0 ? RunTimes[0] : null;
}

/// <summary>
/// Prefetch says how often a program has run here and when the last eight of those were, which
/// is what separates "runs every morning" from "first time an hour ago".
///
/// Windows 10 and 11 compress the file (MAM, Xpress Huffman) and use format version 30 or 31.
/// The offsets below were read off real files on this platform and every field is range-checked
/// before it is trusted: a misparsed timestamp would be worse than no timestamp.
/// </summary>
public static class PrefetchReader
{
    private const ushort CompressionXpressHuffman = 4;
    private const int MaxDecompressedBytes = 32 * 1024 * 1024;

    private const int NameOffset = 0x10;
    private const int NameChars = 60;
    private const int RunTimesOffset = 0x80;
    private const int RunTimeCount = 8;
    private const int RunCountOffset = 0x12C;

    public static PrefetchEntry? Read(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            var body = Decompress(raw);
            if (body is null || body.Length < RunCountOffset + 4) return null;

            var version = BitConverter.ToUInt32(body, 0);
            if (Encoding.ASCII.GetString(body, 4, 4) != "SCCA") return null;
            if (version is not (30 or 31)) return null;

            var name = Encoding.Unicode.GetString(body, NameOffset, NameChars * 2).Split('\0')[0];
            if (name.Length == 0) return null;

            var times = new List<DateTime>();
            for (var i = 0; i < RunTimeCount; i++)
            {
                var raw64 = BitConverter.ToInt64(body, RunTimesOffset + i * 8);
                if (raw64 <= 0) continue;

                DateTime when;
                try
                {
                    when = DateTime.FromFileTimeUtc(raw64).ToLocalTime();
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (when.Year is < 2000 or > 2100) continue;
                if (when > DateTime.Now.AddDays(1)) continue;
                times.Add(when);
            }

            var count = BitConverter.ToInt32(body, RunCountOffset);
            if (count is < 0 or > 10_000_000) count = 0;

            return times.Count == 0 && count == 0 ? null : new PrefetchEntry(name, count, times);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"prefetch file unreadable ({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    private static byte[]? Decompress(byte[] raw)
    {
        if (raw.Length < 8) return null;

        // Older builds wrote the format uncompressed; newer ones wrap it in a MAM container.
        if (Encoding.ASCII.GetString(raw, 4, 4) == "SCCA") return raw;
        if (Encoding.ASCII.GetString(raw, 0, 3) != "MAM") return null;

        var size = BitConverter.ToInt32(raw, 4);
        if (size is <= 0 or > MaxDecompressedBytes) return null;

        var status = RtlGetCompressionWorkSpaceSize(CompressionXpressHuffman, out var workSpaceSize, out _);
        if (status != 0) return null;

        var workSpace = Marshal.AllocHGlobal((int)workSpaceSize);
        var input = Marshal.AllocHGlobal(raw.Length - 8);
        var output = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.Copy(raw, 8, input, raw.Length - 8);

            status = RtlDecompressBufferEx(CompressionXpressHuffman, output, (uint)size,
                input, (uint)(raw.Length - 8), out var written, workSpace);

            if (status != 0 || written == 0) return null;

            var result = new byte[written];
            Marshal.Copy(output, result, 0, (int)written);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(workSpace);
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(ushort formatAndEngine,
        out uint bufferWorkSpaceSize, out uint fragmentWorkSpaceSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlDecompressBufferEx(ushort formatAndEngine, IntPtr uncompressedBuffer,
        uint uncompressedBufferSize, IntPtr compressedBuffer, uint compressedBufferSize,
        out uint finalUncompressedSize, IntPtr workSpace);
}
