using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class ProcessIdentityCacheTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RepeatedReadsReuseTheHandleAndImmutableIdentity()
    {
        var opens = 0;
        var lease = Lease(42);
        using var cache = new ProcessIdentityCache(_ => { opens++; return lease; });
        Assert.Same(lease.Snapshot, cache.Read(42, @"C:\Apps\player.exe"));
        Assert.Same(lease.Snapshot, cache.Read(42, @"c:\apps\PLAYER.exe"));
        Assert.Equal(1, opens);
        Assert.False(lease.Disposed);
    }

    [Fact]
    public void RecycledPidCannotUseTheOldCreationTimeOrImage()
    {
        var original = Lease(42);
        var replacement = Lease(42, @"C:\Apps\different.exe", Started.AddMinutes(1));
        var leases = new Queue<FakeLease>([original, replacement]);
        using var cache = new ProcessIdentityCache(_ => leases.Dequeue());
        Assert.Equal(Started, cache.Read(42)!.Identity.StartedAtUtc);
        original.IsRunning = false;

        Assert.Null(cache.Read(42, @"C:\Apps\player.exe"));
        Assert.True(original.Disposed);
        Assert.Same(replacement.Snapshot, cache.Read(42, @"C:\Apps\different.exe"));
        Assert.Equal(Started.AddMinutes(1), cache.Read(42)!.Identity.StartedAtUtc);
    }

    [Fact]
    public void UnavailableProcessIsRetriedRatherThanCachedNegatively()
    {
        var opens = 0;
        using var cache = new ProcessIdentityCache(pid => ++opens == 1 ? null : Lease(pid));
        Assert.Null(cache.Read(42));
        Assert.NotNull(cache.Read(42));
        Assert.Equal(2, opens);
    }

    [Fact]
    public void ExpectedPathMismatchCannotBorrowAnotherApplicationsEvidence()
    {
        var lease = Lease(42);
        using var cache = new ProcessIdentityCache(_ => lease);
        Assert.Null(cache.Read(42, @"C:\Other\player.exe"));
        Assert.Same(lease.Snapshot, cache.Read(42, @"C:\Apps\player.exe"));
    }

    [Fact]
    public void CacheIsBoundedAndClosesTheLeastRecentlyUsedHandle()
    {
        var leases = new Dictionary<int, FakeLease>();
        using var cache = new ProcessIdentityCache(pid => leases[pid] = Lease(pid));
        for (var pid = 1; pid <= ProcessIdentityCache.MaximumEntries; pid++) Assert.NotNull(cache.Read(pid));
        Assert.NotNull(cache.Read(1)); // PID 2 becomes the least recently used.
        Assert.NotNull(cache.Read(ProcessIdentityCache.MaximumEntries + 1));
        Assert.Equal(ProcessIdentityCache.MaximumEntries, cache.Count);
        Assert.True(leases[2].Disposed);
        Assert.False(leases[1].Disposed);
    }

    [Fact]
    public void EndedProcessIsRemovedBeforeEvictingAnyLiveProcess()
    {
        var leases = new Dictionary<int, FakeLease>();
        using var cache = new ProcessIdentityCache(pid => leases[pid] = Lease(pid));
        for (var pid = 1; pid <= ProcessIdentityCache.MaximumEntries; pid++) cache.Read(pid);
        leases[20].IsRunning = false;
        cache.Read(ProcessIdentityCache.MaximumEntries + 1);
        Assert.True(leases[20].Disposed);
        Assert.False(leases[1].Disposed);
        Assert.Equal(ProcessIdentityCache.MaximumEntries, cache.Count);
    }

    [Fact]
    public void ProcessWhichExitsDuringOpenIsNotRetained()
    {
        var lease = Lease(42);
        lease.IsRunning = false;
        using var cache = new ProcessIdentityCache(_ => lease);
        Assert.Null(cache.Read(42));
        Assert.True(lease.Disposed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DisposeClosesEveryHandleAndPreventsFurtherOpens()
    {
        var opens = 0;
        var lease = Lease(42);
        var cache = new ProcessIdentityCache(_ => { opens++; return lease; });
        cache.Read(42);
        cache.Dispose();
        cache.Dispose();
        Assert.True(lease.Disposed);
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Read(42));
        Assert.Equal(1, opens);
    }

    [Fact]
    public void NativeHandleReadsCurrentProcessWithLimitedQueryPermission()
    {
        using var cache = new ProcessIdentityCache();
        var process = cache.Read(Environment.ProcessId, Environment.ProcessPath);
        Assert.NotNull(process);
        Assert.Equal(Environment.ProcessId, process.Identity.ProcessId);
        Assert.True(process.Identity.StartedAtUtc <= DateTimeOffset.UtcNow);
        Assert.Equal(Environment.ProcessPath, process.ExecutablePath, ignoreCase: true);
    }

    private static FakeLease Lease(int pid, string path = @"C:\Apps\player.exe", DateTimeOffset? started = null) =>
        new(new(new StreamProcessIdentity(pid, started ?? Started), path));

    private sealed class FakeLease(ProcessIdentitySnapshot snapshot) : IProcessIdentityLease
    {
        public ProcessIdentitySnapshot Snapshot { get; } = snapshot;
        public bool IsRunning { get; set; } = true;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
