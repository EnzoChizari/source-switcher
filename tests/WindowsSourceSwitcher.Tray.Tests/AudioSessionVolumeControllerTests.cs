using System.Runtime.InteropServices;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioSessionVolumeControllerTests
{
    private const string Endpoint = "output-one";
    private const string AppPath = @"C:\Apps\player.exe";
    private static readonly DateTimeOffset Started = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VolumeChangesOnlyTheSelectedExecutableOnThePinnedEndpoint()
    {
        var player = Session(1, "player-session");
        var other = Session(2, "other-session", path: @"C:\Other\player.exe");
        using var store = new FakeStore(player, other);
        using var controller = new AudioSessionVolumeController(store);
        var target = controller.Read(Endpoint).Sources.Single(source => source.Identity == player.State.Identity.SourceIdentity);

        var result = controller.SetVolume(Endpoint, target, .35f);

        Assert.True(result.Success);
        Assert.Equal(1, result.AppliedSessions);
        Assert.Equal(.35f, player.State.Volume);
        Assert.Equal(.8f, other.State.Volume);
        Assert.Equal(.35f, result.Confirmed!.Volume);
        Assert.Equal(0, other.Writes);
        Assert.Equal(Endpoint, store.LastOpenedEndpoint);
    }

    [Fact]
    public void GroupAdjustmentSetsEveryPreviouslyObservedSessionUniformly()
    {
        var first = Session(1, "one", .2f);
        var second = Session(2, "two", .9f);
        using var controller = Controller(out var store, first, second);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.True(target.HasMixedVolumes);
        Assert.Null(target.Volume);

        var result = controller.SetVolume(Endpoint, target, .4f);

        Assert.True(result.Success);
        Assert.Equal(2, result.AppliedSessions);
        Assert.Equal(.4f, first.State.Volume);
        Assert.Equal(.4f, second.State.Volume);
        Assert.False(result.Confirmed!.HasMixedVolumes);
        Assert.Equal(.4f, result.Confirmed.Volume);
        Assert.Equal(store.Opens, store.ClosedEndpoints);
    }

    [Fact]
    public void MuteAndVolumeRemainIndependentControls()
    {
        var player = Session(1, "one", .4f);
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);

        Assert.True(controller.SetMute(Endpoint, target, true).Success);
        Assert.Equal(.4f, player.State.Volume);
        Assert.True(player.State.Muted);
        Assert.True(controller.SetVolume(Endpoint, target, .7f).Success);
        Assert.True(player.State.Muted);
        Assert.True(controller.SetMute(Endpoint, target, false).Success);
        Assert.Equal(.7f, player.State.Volume);
        Assert.False(player.State.Muted);
    }

    [Fact]
    public void MixedMuteIsReportedInsteadOfTreatingTheWholeGroupAsUnmuted()
    {
        var first = Session(1, "one");
        var second = Session(2, "two");
        second.State = second.State with { Muted = true };
        using var controller = Controller(out _, first, second);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.True(target.HasMixedMute);
        Assert.Null(target.Muted);
        Assert.True(controller.SetMute(Endpoint, target, false).Success);
        Assert.False(first.State.Muted);
        Assert.False(second.State.Muted);
    }

    [Fact]
    public void ReadAlwaysReflectsExternalWindowsMixerChanges()
    {
        var player = Session(1, "one", .2f);
        using var controller = Controller(out _, player);
        Assert.Equal(.2f, Assert.Single(controller.Read(Endpoint).Sources).Volume);
        player.State = player.State with { Volume = .64f, Muted = true };
        var updated = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.Equal(.64f, updated.Volume);
        Assert.True(updated.Muted);
        Assert.Equal(0, player.Writes);
    }

    [Fact]
    public void ANewSessionOfTheSameAppDoesNotInheritAQueuedAdjustment()
    {
        var original = Session(1, "one");
        using var controller = Controller(out var store, original);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        var replacement = Session(2, "two");
        store.Sessions.Add(replacement);

        var result = controller.SetVolume(Endpoint, target, .2f);

        Assert.True(result.Success);
        Assert.Equal(.2f, original.State.Volume);
        Assert.Equal(.8f, replacement.State.Volume);
        Assert.True(result.Confirmed!.HasMixedVolumes);
    }

    [Theory]
    [InlineData("creation")]
    [InlineData("path")]
    [InlineData("instance")]
    [InlineData("pid")]
    [InlineData("endpoint")]
    [InlineData("shared")]
    public void StaleSessionIdentityCannotAuthorizeAWrite(string changed)
    {
        var player = Session(1, "one");
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        var identity = player.State.Identity;
        identity = changed switch
        {
            "creation" => identity with { ProcessStartedAtUtc = Started.AddSeconds(1) },
            "path" => identity with { ExecutablePath = @"C:\Other\player.exe", SourceIdentity = SourceIdentity.FromExecutablePath(@"C:\Other\player.exe") },
            "instance" => identity with { SessionInstanceId = "new-session" },
            "pid" => identity with { ProcessId = 999 },
            "endpoint" => identity with { EndpointId = "other-output" },
            "shared" => identity with { IsSharedAcrossProcesses = true },
            _ => throw new InvalidOperationException()
        };
        player.State = player.State with { Identity = identity };

        var result = controller.SetVolume(Endpoint, target, .3f);

        Assert.False(result.Success);
        Assert.Equal(0, player.Writes);
        Assert.Equal(1, result.MissingSessions);
        Assert.Equal(.8f, player.State.Volume);
    }

    [Fact]
    public void RevalidationAtTheWriteBoundaryCatchesAnExitAfterTheCensus()
    {
        var player = Session(1, "one");
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        player.BeforeWrite = () => player.State = player.State with
        { Identity = player.State.Identity with { ProcessStartedAtUtc = Started.AddMinutes(1) } };

        var result = controller.SetVolume(Endpoint, target, .3f);

        Assert.False(result.Success);
        Assert.Equal(0, result.AppliedSessions);
        Assert.Equal(0, player.Writes);
    }

    [Fact]
    public void LostSessionsDoNotPreventSafeRemainingSessionsFromUpdating()
    {
        var first = Session(1, "one");
        var second = Session(2, "two");
        using var controller = Controller(out var store, first, second);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        store.Sessions.Remove(second);

        var result = controller.SetVolume(Endpoint, target, .3f);

        Assert.False(result.Success);
        Assert.Equal(1, result.AppliedSessions);
        Assert.Equal(1, result.MissingSessions);
        Assert.Equal(.3f, first.State.Volume);
        Assert.Equal(.8f, second.State.Volume);
        Assert.Contains("closed or restarted", result.Error);
    }

    [Fact]
    public void FailureOfOneSessionDoesNotSkipAnotherValidSession()
    {
        var first = Session(1, "one");
        var second = Session(2, "two");
        using var controller = Controller(out _, first, second);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        first.FailWrites = true;

        var result = controller.SetMute(Endpoint, target, true);

        Assert.False(result.Success);
        Assert.Equal(1, result.AppliedSessions);
        Assert.False(first.State.Muted);
        Assert.True(second.State.Muted);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void FailedWindowsReadbackIsNeverReportedAsConfirmedSuccess()
    {
        var player = Session(1, "one");
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        player.IgnoreWrites = true;

        var result = controller.SetVolume(Endpoint, target, .1f);

        Assert.False(result.Success);
        Assert.Equal(.8f, result.Confirmed!.Volume);
        Assert.Contains("did not confirm", result.Error);
    }

    [Fact]
    public void SwitchingTheSelectedEndpointRejectsTheOldControlBeforeOpeningIt()
    {
        var player = Session(1, "one");
        using var controller = Controller(out var store, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        var opens = store.Opens;

        var result = controller.SetMute("new-output", target, true);

        Assert.False(result.Success);
        Assert.Equal(opens, store.Opens);
        Assert.Equal(0, player.Writes);
    }

    [Fact]
    public void AnAdapterReturningAnotherEndpointCannotRedirectAWrite()
    {
        var player = Session(1, "one");
        using var controller = Controller(out var store, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        store.ReturnedEndpoint = "other-output";
        Assert.False(controller.SetMute(Endpoint, target, true).Success);
        Assert.Empty(controller.Read(Endpoint).Sources);
        Assert.Equal(0, player.Writes);
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidVolumeIsRejectedBeforeAnyNativeCall(float volume)
    {
        var player = Session(1, "one");
        using var controller = Controller(out var store, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        var opens = store.Opens;
        Assert.False(controller.SetVolume(Endpoint, target, volume).Success);
        Assert.Equal(opens, store.Opens);
        Assert.Equal(0, player.Writes);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void VolumeEndpointsAreAcceptedPrecisely(float volume)
    {
        var player = Session(1, "one");
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.True(controller.SetVolume(Endpoint, target, volume).Success);
        Assert.Equal(volume, player.State.Volume);
    }

    [Fact]
    public void ShareXIsOmittedAndCannotBeWrittenThroughAForgedSnapshot()
    {
        var shareX = Session(1, "one", path: @"C:\Tools\ShareX.exe");
        using var controller = Controller(out _, shareX);
        Assert.Empty(controller.Read(Endpoint).Sources);
        var target = new AudioSessionVolumeSnapshot(Endpoint, shareX.State.Identity.SourceIdentity, "ShareX",
            .8f, false, false, false, [shareX.State.Identity], true, true, null);
        Assert.False(controller.SetVolume(Endpoint, target, .2f).Success);
        Assert.Equal(0, shareX.Writes);
    }

    [Fact]
    public void UnverifiedProcessIsVisibleButReadOnly()
    {
        var player = Session(1, "one");
        player.State = player.State with { Identity = player.State.Identity with { ExecutablePath = null, ProcessStartedAtUtc = null } };
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.False(target.CanControl);
        Assert.NotNull(target.Error);
        Assert.False(controller.SetMute(Endpoint, target, true).Success);
        Assert.Equal(0, player.Writes);
    }

    [Fact]
    public void CrossProcessSessionDoesNotPretendToBeAnIsolatedAppControl()
    {
        var player = Session(1, "one");
        player.State = player.State with { Identity = player.State.Identity with { IsSharedAcrossProcesses = true } };
        using var controller = Controller(out _, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.False(target.CanControl);
        Assert.Contains("shared by multiple processes", target.Error);
        Assert.False(controller.SetVolume(Endpoint, target with { CanControl = true }, .3f).Success);
        Assert.Equal(0, player.Writes);
    }

    [Fact]
    public void SystemSoundsCanBeAdjustedWithoutInventingAnExecutableIdentity()
    {
        var system = Session(0, "system");
        system.State = system.State with { Name = "System sounds", Identity = system.State.Identity with
        { SourceIdentity = "system-sounds", ExecutablePath = null, ProcessStartedAtUtc = null, IsSystemSounds = true } };
        using var controller = Controller(out _, system);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        Assert.True(target.CanControl);
        Assert.True(controller.SetVolume(Endpoint, target, .25f).Success);
        Assert.Equal(.25f, system.State.Volume);
    }

    [Fact]
    public void ASessionReadFailureIsReportedWithoutHidingOtherApps()
    {
        var player = Session(1, "one");
        var other = Session(2, "two", path: @"C:\Other\other.exe");
        other.FailReads = true;
        using var controller = Controller(out _, player, other);
        var result = controller.Read(Endpoint);
        Assert.Single(result.Sources);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void UnavailableDeviceReturnsAnErrorInsteadOfFallingBackToAnotherOutput()
    {
        var player = Session(1, "one");
        using var controller = Controller(out var store, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        store.FailOpen = true;

        Assert.NotNull(controller.Read(Endpoint).Error);
        var result = controller.SetVolume(Endpoint, target, .3f);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(0, player.Writes);
        Assert.Equal(Endpoint, store.LastOpenedEndpoint);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    [InlineData(1.01f)]
    public void InvalidNativeReadbackIsOmittedAndReported(float volume)
    {
        var player = Session(1, "one", volume);
        using var controller = Controller(out _, player);
        var result = controller.Read(Endpoint);
        Assert.Empty(result.Sources);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void AnUnavailableShareXPathStillHonorsTheExactProcessNameExclusion()
    {
        var shareX = Session(1, "one");
        shareX.State = shareX.State with { Name = "ShareX", Identity = shareX.State.Identity with
        { ExecutablePath = null, ProcessStartedAtUtc = null } };
        using var controller = Controller(out _, shareX);
        Assert.Empty(controller.Read(Endpoint).Sources);
    }

    [Fact]
    public void SourceIdentityCannotBeChangedWithoutChangingItsSessionAuthorization()
    {
        var player = Session(1, "one");
        using var controller = Controller(out var store, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        var opens = store.Opens;
        var result = controller.SetVolume(Endpoint, target with { Identity = @"C:\Other\other.exe" }, .3f);
        Assert.False(result.Success);
        Assert.Equal(opens, store.Opens);
        Assert.Equal(0, player.Writes);
    }

    [Fact]
    public void DisposeClosesResourcesAndPreventsReadsOrWrites()
    {
        var player = Session(1, "one");
        var controller = Controller(out var store, player);
        var target = Assert.Single(controller.Read(Endpoint).Sources);
        controller.Dispose();
        controller.Dispose();
        Assert.True(store.Disposed);
        Assert.Equal(store.Opens, store.ClosedEndpoints);
        Assert.Empty(controller.Read(Endpoint).Sources);
        Assert.False(controller.SetMute(Endpoint, target, true).Success);
        Assert.Equal(0, player.Writes);
    }

    private static AudioSessionVolumeController Controller(out FakeStore store, params FakeSession[] sessions)
    {
        store = new(sessions);
        return new(store);
    }

    private static FakeSession Session(int pid, string instance, float volume = .8f, string path = AppPath) => new(new(
        new(Endpoint, SourceIdentity.FromExecutablePath(path), pid, Started, path, instance, false),
        Path.GetFileNameWithoutExtension(path), volume, false, true));

    private sealed class FakeSession(AudioVolumeSessionState state) : IAudioSessionVolumeSession
    {
        public AudioVolumeSessionState State { get; set; } = state;
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public bool IgnoreWrites { get; set; }
        public Action? BeforeWrite { get; set; }
        public int Writes { get; private set; }
        public AudioVolumeSessionState Read() => FailReads ? throw new COMException("gone") : State;
        public bool TrySet(VolumeSessionIdentity expected, float? volume, bool? muted, out string? error)
        {
            BeforeWrite?.Invoke();
            if (FailWrites || !AudioSessionVolumeController.Matches(State.Identity, expected))
            { error = "changed"; return false; }
            Writes++;
            if (!IgnoreWrites) State = State with { Volume = volume ?? State.Volume, Muted = muted ?? State.Muted };
            error = null;
            return true;
        }
    }

    private sealed class FakeStore(params FakeSession[] sessions) : IAudioSessionVolumeStore
    {
        public List<IAudioSessionVolumeSession> Sessions { get; } = [.. sessions];
        public int Opens { get; private set; }
        public int ClosedEndpoints { get; private set; }
        public string? LastOpenedEndpoint { get; private set; }
        public string ReturnedEndpoint { get; set; } = Endpoint;
        public bool FailOpen { get; set; }
        public bool Disposed { get; private set; }
        public IAudioSessionVolumeEndpoint Open(string endpointId)
        {
            Opens++;
            LastOpenedEndpoint = endpointId;
            if (FailOpen) throw new COMException("device removed");
            return new FakeEndpoint(this);
        }
        public void Dispose() => Disposed = true;
        private sealed class FakeEndpoint(FakeStore owner) : IAudioSessionVolumeEndpoint
        {
            public string EndpointId => owner.ReturnedEndpoint;
            public IReadOnlyList<IAudioSessionVolumeSession> Sessions => owner.Sessions.ToArray();
            public string? Error => null;
            public void Dispose() => owner.ClosedEndpoints++;
        }
    }
}
