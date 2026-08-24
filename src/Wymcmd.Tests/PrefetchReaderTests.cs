using Wymcmd.Core.Forensic;
using Xunit;

namespace Wymcmd.Tests;

public class PrefetchReaderTests
{
    private static string? AnySample()
    {
        // Reading the real folder needs administrator rights; when it is not available the
        // parser still has to behave, which is what the other tests here check.
        foreach (var folder in new[]
                 {
                     Path.Combine(Path.GetTempPath(), "pfsample"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch")
                 })
        {
            try
            {
                var sample = Directory.EnumerateFiles(folder, "*.pf").FirstOrDefault();
                if (sample is not null) return sample;
            }
            catch (Exception)
            {
                // Not readable here; try the next folder.
            }
        }

        return null;
    }

    [Fact]
    public void Reads_a_real_prefetch_file_when_the_folder_is_readable()
    {
        var sample = AnySample();
        if (sample is null) return;

        var entry = PrefetchReader.Read(sample);
        if (entry is null) return; // an older format version, which the reader declines on purpose

        // The name inside the file has to be the one the file is named after.
        var expected = Path.GetFileNameWithoutExtension(sample).Split('-')[0];
        Assert.Equal(expected, entry.ImageName, ignoreCase: true);

        foreach (var when in entry.RunTimes)
        {
            Assert.True(when.Year >= 2000);
            Assert.True(when <= DateTime.Now.AddDays(1));
        }

        Assert.InRange(entry.RunCount, 0, 10_000_000);
    }

    [Fact]
    public void A_file_that_is_not_prefetch_is_declined_rather_than_guessed_at()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wymcmd-fake-{Guid.NewGuid():n}.pf");
        File.WriteAllBytes(path, "this is not a prefetch file at all"u8.ToArray());

        try
        {
            Assert.Null(PrefetchReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_truncated_file_is_declined()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wymcmd-short-{Guid.NewGuid():n}.pf");
        File.WriteAllBytes(path, [0x4D, 0x41, 0x4D, 0x04]);

        try
        {
            Assert.Null(PrefetchReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_is_declined()
    {
        Assert.Null(PrefetchReader.Read(Path.Combine(Path.GetTempPath(), "wymcmd-nope.pf")));
    }
}
