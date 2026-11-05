using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class OwnedAudioTraceSessionsTests
{
    [Theory]
    [InlineData("DX5SourceSwitcher.StreamRates.123", 123)]
    [InlineData("DX5SourceSwitcher.StreamRates.123.0123456789abcdef0123456789abcdef", 123)]
    [InlineData("DX5SourceSwitcher.StreamSnapshot.42.0123456789abcdef0123456789abcdef", 42)]
    public void Exact_product_names_identify_owner(string name, int pid)
    {
        Assert.True(OwnedAudioTraceSessions.TryGetOwner(name, out var actual));
        Assert.Equal(pid, actual);
    }

    [Theory]
    [InlineData("Other.StreamRates.123")]
    [InlineData("DX5SourceSwitcher.StreamRates.0")]
    [InlineData("DX5SourceSwitcher.StreamRates.0123")]
    [InlineData("DX5SourceSwitcher.StreamRates.+123")]
    [InlineData("DX5SourceSwitcher.StreamRates.123.extra")]
    [InlineData("DX5SourceSwitcher.StreamRates.123.0123456789abcdef0123456789abcdef.extra")]
    [InlineData("DX5SourceSwitcher.StreamRates.999999999999")]
    [InlineData("DX5SourceSwitcher.StreamSnapshot.123.bad-guid")]
    [InlineData("DX5SourceSwitcher.StreamSnapshot.123.0123456789abcdef0123456789abcdef.extra")]
    public void Malformed_and_unrelated_names_are_never_eligible(string name) =>
        Assert.False(OwnedAudioTraceSessions.TryGetOwner(name, out _));

    [Fact]
    public void Cleanup_preserves_live_reused_and_unknown_processes_and_continues_after_failure()
    {
        var stopped = new List<string>();
        var names = Enumerable.Range(1, 5).Select(pid => $"DX5SourceSwitcher.StreamRates.{pid}")
            .Append("unrelated");
        var count = OwnedAudioTraceSessions.RecoverAbandoned(names, pid => pid switch
        { 1 => true, 2 => null, _ => false }, name =>
        {
            if (name.EndsWith(".3")) throw new UnauthorizedAccessException();
            stopped.Add(name);
        });
        Assert.Equal(2, count);
        Assert.Equal(new[] { "DX5SourceSwitcher.StreamRates.4", "DX5SourceSwitcher.StreamRates.5" }, stopped);
    }

    [Fact]
    public void A_process_reappearing_during_validation_prevents_cleanup()
    {
        var calls = 0;
        var stopped = false;
        Assert.Equal(0, OwnedAudioTraceSessions.RecoverAbandoned(["DX5SourceSwitcher.StreamRates.123"],
            _ => ++calls != 1, _ => stopped = true));
        Assert.Equal(2, calls);
        Assert.False(stopped);
    }
}
