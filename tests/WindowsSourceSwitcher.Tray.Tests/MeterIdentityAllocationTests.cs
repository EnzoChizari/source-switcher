using System.Diagnostics;
using System.Reflection;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class MeterIdentityAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void RepeatedFrameSessionValidationReportsAllocationAndRejectsReplacedOrDuplicateSessions()
    {
        var compare = typeof(VolumeMixerView).GetNestedType("MixerRow", BindingFlags.NonPublic)!
            .GetMethod("SameSessions", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<VolumeSessionIdentity>, IReadOnlyList<VolumeSessionIdentity>, bool>>();
        var start = DateTimeOffset.UtcNow;
        var session = new VolumeSessionIdentity("output", @"C:\PLAYER.EXE", 42, start, @"C:\PLAYER.EXE", "instance", false);
        IReadOnlyList<VolumeSessionIdentity> left = [session];
        IReadOnlyList<VolumeSessionIdentity> right = [session with { }];
        for (var index = 0; index < 2000; index++) compare(left, right);
        var clock = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var allMatched = true;
        const int iterations = 100_000;
        for (var index = 0; index < iterations; index++) allMatched &= compare(left, right);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        clock.Stop();
        Assert.True(allMatched);
        Assert.False(compare(left, [session with { ProcessId = 43 }]));
        Assert.False(compare(left, [session with { ProcessStartedAtUtc = start.AddSeconds(1) }]));
        Assert.False(compare([session, session], [session, session with { SessionInstanceId = "other" }]));
        Assert.False(compare([], []));
        output.WriteLine($"SESSION_COMPARISON iterations={iterations}; allocatedBytes={allocated}; elapsedMs={clock.Elapsed.TotalMilliseconds:F3}");
    }
}
