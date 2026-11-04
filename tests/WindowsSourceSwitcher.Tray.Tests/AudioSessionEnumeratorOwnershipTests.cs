using System.Runtime.InteropServices;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioSessionEnumeratorOwnershipTests
{
    private const string Endpoint = "dac";
    private const string Player = @"C:\Apps\Player.exe";

    [Fact]
    public void SourceCensusUsesIdentityOnlyAndClosesEveryEndpointLease()
    {
        var session = Session(Player, 10, "one");
        var store = new Store(session);
        using var enumerator = new AudioSessionEnumerator(store);
        var row = Assert.Single(enumerator.Enumerate(Endpoint));
        Assert.Equal(SourceIdentity.FromExecutablePath(Player), row.Identity);
        Assert.True(row.IsActive);
        Assert.True(row.CanPersist);
        Assert.Equal(Player, row.ProcessExecutablePaths[10]);
        Assert.Equal(new[] { "one" }, row.SessionInstanceIds);
        Assert.Equal(1, session.IdentityReads);
        Assert.Equal(0, session.VolumeReads);
        Assert.Equal(0, session.Writes);
        Assert.Equal(store.Opens, store.Closed);
        Assert.Null(enumerator.LastError);
    }

    [Fact]
    public void NativeMetadataStillGroupsIdleAppleUiAndActiveHelperWithActualPaths()
    {
        const string package = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa";
        var ui = Session(package + @"\AppleMusic.exe", 10, "ui");
        ui.State = ui.State! with { IsActive = false };
        var helper = Session(package + @"\AMPLibraryAgent.exe", 20, "helper");
        using var enumerator = new AudioSessionEnumerator(new Store(ui, helper));
        var row = Assert.Single(enumerator.Enumerate(Endpoint));
        Assert.Equal("Apple Music", row.ProcessName);
        Assert.True(row.IsActive);
        Assert.Equal(new[] { 10, 20 }, row.ProcessIds);
        Assert.Equal(package + @"\AMPLibraryAgent.exe", row.ProcessExecutablePaths[20]);
        Assert.Equal(new[] { "ui", "helper" }, row.SessionInstanceIds);
    }

    [Fact]
    public void FailedAndExpiredSessionsDoNotHideHealthyRowsOrLeakTheEndpoint()
    {
        var gone = Session(Player, 10, "gone");
        gone.ThrowOnIdentity = true;
        var expired = Session(Player, 11, "expired");
        expired.State = null;
        var current = Session(Player, 12, "current");
        var store = new Store(gone, expired, current);
        using var enumerator = new AudioSessionEnumerator(store);
        Assert.Equal(new[] { 12 }, Assert.Single(enumerator.Enumerate(Endpoint)).ProcessIds);
        Assert.NotNull(enumerator.LastError);
        Assert.Equal(store.Opens, store.Closed);
        gone.ThrowOnIdentity = false;
        gone.State = null;
        Assert.Single(enumerator.Enumerate(Endpoint));
        Assert.Null(enumerator.LastError);
        Assert.Equal(store.Opens, store.Closed);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("empty-instance")]
    public void InconsistentSessionIdentityCannotBePersistedOrUsedForDetection(string mismatch)
    {
        var session = Session(Player, 10, "one");
        session.State = session.State! with { Identity = mismatch == "endpoint"
            ? session.State.Identity with { EndpointId = "other" }
            : session.State.Identity with { SessionInstanceId = " " } };
        var store = new Store(session);
        using var enumerator = new AudioSessionEnumerator(store);
        Assert.Empty(enumerator.Enumerate(Endpoint));
        Assert.NotNull(enumerator.LastError);
        Assert.Equal(store.Opens, store.Closed);
    }

    [Fact]
    public void UnexpectedEndpointIsDisposedWithoutReadingItsSessions()
    {
        var session = Session(Player, 10, "one");
        var store = new Store(session) { ReturnedEndpoint = "other" };
        using var enumerator = new AudioSessionEnumerator(store);
        Assert.Empty(enumerator.Enumerate(Endpoint));
        Assert.Equal(0, session.IdentityReads);
        Assert.NotNull(enumerator.LastError);
        Assert.Equal(store.Opens, store.Closed);
    }

    [Fact]
    public void EndpointFailuresReturnUnknownAndRetainNoPreviousSources()
    {
        var store = new Store(Session(Player, 10, "one"));
        using var enumerator = new AudioSessionEnumerator(store);
        Assert.Single(enumerator.Enumerate(Endpoint));
        store.ThrowOnOpen = true;
        Assert.Empty(enumerator.Enumerate(Endpoint));
        Assert.Contains("unavailable", enumerator.LastError);
    }

    [Fact]
    public void PathUnavailableOwnersRemainVisibleWithoutInventingPersistentIdentity()
    {
        var session = Session(Player, 10, "one");
        session.State = session.State! with { Identity = session.State.Identity with { ExecutablePath = null } };
        using var enumerator = new AudioSessionEnumerator(new Store(session));
        var source = Assert.Single(enumerator.Enumerate(Endpoint));
        Assert.False(source.CanPersist);
        Assert.Null(source.ExecutablePath);
        Assert.Empty(source.ProcessExecutablePaths);
        Assert.StartsWith("unavailable:", source.Identity);
    }

    [Fact]
    public void ShareXExclusionAppliesToIdentityOnlyCensusAsWell()
    {
        using var enumerator = new AudioSessionEnumerator(new Store(Session(@"C:\Apps\ShareX.exe", 10, "excluded")));
        Assert.Empty(enumerator.Enumerate(Endpoint));
    }

    [Fact]
    public void DisposeIsIdempotentAndPreventsNewNativeOpens()
    {
        var store = new Store(Session(Player, 10, "one"));
        var enumerator = new AudioSessionEnumerator(store);
        Assert.Single(enumerator.Enumerate(Endpoint));
        enumerator.Dispose();
        enumerator.Dispose();
        Assert.Equal(1, store.DisposeCount);
        var opened = store.Opens;
        Assert.Empty(enumerator.Enumerate(Endpoint));
        Assert.Equal(opened, store.Opens);
        Assert.Contains("closed", enumerator.LastError);
    }

    private static SessionLease Session(string path, int pid, string instance) => new(new(
        new(Endpoint, SourceIdentity.FromExecutablePath(path), pid, DateTimeOffset.Parse("2026-10-21T00:00:00Z"), path, instance, false),
        Path.GetFileNameWithoutExtension(path), true));
    private sealed class SessionLease(AudioSessionIdentityState state) : IAudioSessionVolumeSession
    {
        public AudioSessionIdentityState? State { get; set; } = state;
        public bool ThrowOnIdentity { get; set; }
        public int IdentityReads { get; private set; }
        public int VolumeReads { get; private set; }
        public int Writes { get; private set; }
        public AudioSessionIdentityState? ReadIdentity()
        {
            IdentityReads++;
            if (ThrowOnIdentity) throw new COMException("Session closed");
            return State;
        }
        public AudioVolumeSessionState? Read() { VolumeReads++; throw new InvalidOperationException("A source census must not read gain/mute."); }
        public bool TrySet(VolumeSessionIdentity expected, float? volume, bool? muted, out string? error)
        { Writes++; error = "A source census must not adjust sessions."; return false; }
    }
    private sealed class Store(params SessionLease[] sessions) : IAudioSessionVolumeStore
    {
        public int Opens { get; private set; }
        public int Closed { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ThrowOnOpen { get; set; }
        public string ReturnedEndpoint { get; set; } = Endpoint;
        public IAudioSessionVolumeEndpoint Open(string endpointId)
        {
            if (ThrowOnOpen) throw new COMException("Disconnected");
            Opens++;
            return new EndpointLease(this, sessions);
        }
        public void Dispose() => DisposeCount++;
        private sealed class EndpointLease(Store owner, SessionLease[] sessions) : IAudioSessionVolumeEndpoint
        {
            public string EndpointId => owner.ReturnedEndpoint;
            public IReadOnlyList<IAudioSessionVolumeSession> Sessions => sessions;
            public string? Error => null;
            public void Dispose() => owner.Closed++;
        }
    }
}
