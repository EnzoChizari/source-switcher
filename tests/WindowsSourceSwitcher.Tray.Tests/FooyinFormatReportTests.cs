using System.Diagnostics;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class FooyinFormatReportTests
{
    [Theory]
    [InlineData("SSFOOYIN1|1|96000|24|2026-11-04 14:00:00", 96000, 24)]
    [InlineData("SSFOOYIN1|1|44100||2026-11-04 14:00:00", 44100, null)]
    [InlineData("SSFOOYIN1|1|192000|20|2026-11-04 14:00:00", 192000, null)]
    [InlineData("SSFOOYIN1||96000|24|2026-11-04 14:00:00", null, null)]
    [InlineData("SSFOOYIN1|1|999999|24|2026-11-04 14:00:00", null, null)]
    public void OnlyAnActiveBoundedFormatCanBeUsed(string line, int? rate, int? depth)
    {
        var result = FooyinFormatReport.Parse(line);
        Assert.Equal(rate, result?.Rate);
        Assert.Equal(depth, result?.Bits);
    }

    [Fact]
    public void RejectsStaleOrInactiveReports()
    {
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule!.FileName;
        var source = new SourceSnapshot(SourceIdentity.FromExecutablePath(executable), "Fooyin test", executable,
            [current.Id], true, "session");
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "SSFOOYIN1|1|96000|24|2026-11-04 14:00:00");
            var observation = Assert.IsType<RateObservation>(FooyinFormatReport.Read(path, source, 1));
            Assert.Equal(96000, observation.SampleRateHz);
            Assert.Equal(24, observation.BitsPerSample);
            Assert.Null(FooyinFormatReport.Read(path, source with { IsActive = false }, 1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
            Assert.Null(FooyinFormatReport.Read(path, source, 1));
        }
        finally { File.Delete(path); }
    }
}
