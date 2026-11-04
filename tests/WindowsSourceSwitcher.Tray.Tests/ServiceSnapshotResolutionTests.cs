using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class ServiceSnapshotResolutionTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 20, 12, 55, 51, TimeSpan.Zero);
    private const string Endpoint = "{0.0.0.00000000}.{TOPPING}";
    private const string Session = Endpoint + "|C:\\Player.exe|1%b9872";
    private const int ServicePid = 2428;
    private const int SourcePid = 9872;

    [Fact]
    public void ConcurrentConflictingRatesFromSameProcessWithholdAutomaticRate()
    {
        var result = Resolve(Snapshot(Stream(48_000), Stream(96_000) with { StreamId = 9 }));
        Assert.Null(result.SampleRateHz);
        Assert.Contains("conflicting", result.Detail);
    }

    [Fact]
    public void PausedStreamDoesNotConflictWithRunningStream()
    {
        var result = Resolve(Snapshot(Stream(48_000), Stream(96_000) with { StreamId = 9, IsRunning = false }));
        Assert.Equal(48_000, result.SampleRateHz);
    }

    [Fact]
    public void OnlyPausedStreamsCannotSelectARate()
    {
        var result = Resolve(Snapshot(Stream(96_000) with { IsRunning = false }));
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void SameExecutableProcessesAlreadyGroupedByCallerAreConsideredTogether()
    {
        // Executable-path grouping belongs to the session census. Its complete PID
        // list must be considered here rather than selecting the first process.
        var otherSession = Endpoint + "|C:\\Player.exe|1%b9873";
        var streams = Snapshot(Stream(48_000), Stream(96_000) with
        {
            StreamId = 9, SourceProcessId = SourcePid + 1, SessionInstanceId = otherSession
        });
        var processes = new[] { Process(SourcePid), Process(SourcePid + 1) };
        var result = WasapiStreamRateMonitor.ResolveServiceSnapshot(streams, Endpoint, processes,
            new[] { Session, otherSession });
        Assert.Null(result.SampleRateHz);
        Assert.Contains("conflicting", result.Detail);

        streams = streams with { Streams = streams.Streams.Select(s => s with { SampleRateHz = 96_000 }).ToArray() };
        result = WasapiStreamRateMonitor.ResolveServiceSnapshot(streams, Endpoint, processes,
            new[] { Session, otherSession });
        Assert.Equal(96_000, result.SampleRateHz);
    }

    [Fact]
    public void ProcessCreatedAfterObservationCannotInheritReusedPidEvidence()
    {
        var result = WasapiStreamRateMonitor.ResolveServiceSnapshot(Snapshot(Stream(96_000)), Endpoint,
            new[] { new StreamProcessIdentity(SourcePid, Observed.AddTicks(1)) }, new[] { Session });
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void DifferentEndpointCannotSupplySelectedEndpointRate()
    {
        var result = Resolve(Snapshot(Stream(96_000) with { EndpointId = "{0.0.0.00000000}.{OTHER}" }));
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void EndpointComparisonIsCaseInsensitiveButSessionInstanceMustMatchExactly()
    {
        var snapshot = Snapshot(Stream(96_000));
        var result = WasapiStreamRateMonitor.ResolveServiceSnapshot(snapshot, Endpoint.ToLowerInvariant(),
            new[] { Process(SourcePid) }, new[] { Session });
        Assert.Equal(96_000, result.SampleRateHz);

        result = WasapiStreamRateMonitor.ResolveServiceSnapshot(snapshot, Endpoint,
            new[] { Process(SourcePid) }, new[] { Session.ToLowerInvariant() });
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void SameProcessWithReplacedSessionCannotUseOldSessionEvidence()
    {
        var result = WasapiStreamRateMonitor.ResolveServiceSnapshot(Snapshot(Stream(96_000)), Endpoint,
            new[] { Process(SourcePid) }, new[] { Session + "-new-instance" });
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void InvalidProcessBlocksWholeGroupedSourceEvenWhenAnotherStreamIsValid()
    {
        var snapshot = Snapshot(Stream(96_000)) with { InvalidProcessIds = new[] { SourcePid + 1 } };
        var result = WasapiStreamRateMonitor.ResolveServiceSnapshot(snapshot, Endpoint,
            new[] { Process(SourcePid), Process(SourcePid + 1) }, new[] { Session });
        Assert.Null(result.SampleRateHz);
        Assert.Contains("unsupported or incomplete", result.Detail);
    }

    [Fact]
    public void InvalidUnrelatedProcessDoesNotBlockValidSource()
    {
        var snapshot = Snapshot(Stream(96_000)) with { InvalidProcessIds = new[] { SourcePid + 1 } };
        Assert.Equal(96_000, Resolve(snapshot).SampleRateHz);
    }

    [Fact]
    public void ServiceHeaderPidCannotBeMistakenForStreamOwner()
    {
        var snapshot = Snapshot(Stream(96_000));
        Assert.Equal(96_000, Resolve(snapshot).SampleRateHz);
        var result = WasapiStreamRateMonitor.ResolveServiceSnapshot(snapshot, Endpoint,
            new[] { Process(ServicePid) }, new[] { Session });
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void SnapshotErrorCannotReturnRateEvenIfItContainsOtherwiseValidStream()
    {
        var snapshot = Snapshot(Stream(96_000)) with { Error = "Audio trace events were lost." };
        var result = Resolve(snapshot);
        Assert.Null(result.SampleRateHz);
        Assert.Equal(snapshot.Error, result.Detail);
    }

    [Fact]
    public void EmptySnapshotCannotSelectARate()
    {
        Assert.Null(Resolve(Snapshot()).SampleRateHz);
    }

    private static StreamRateResult Resolve(AudioServiceStreamSnapshotResult snapshot) =>
        WasapiStreamRateMonitor.ResolveServiceSnapshot(snapshot, Endpoint,
            new[] { Process(SourcePid) }, new[] { Session });

    private static StreamProcessIdentity Process(int pid) => new(pid, Observed.AddMinutes(-1));

    private static AudioServiceStreamSnapshotResult Snapshot(params AudioServiceStreamState[] streams) =>
        new(streams, Array.Empty<int>(), null, streams.Length > 0, Observed.AddMilliseconds(-1), Observed.AddMilliseconds(1));

    private static AudioServiceStreamState Stream(int rate) => new(ServicePid, SourcePid, 8, Session,
        Endpoint, rate, 2, 32, Guid.Parse("00000003-0000-0010-8000-00aa00389b71"), 0,
        true, Observed);
}
