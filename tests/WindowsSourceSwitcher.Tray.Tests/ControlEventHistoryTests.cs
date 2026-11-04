using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class ControlEventHistoryTests
{
    [Fact]
    public void History_keeps_latest_bounded_events_newest_first_with_monotonic_sequences()
    {
        var history = new ControlEventHistory(3, () => DateTimeOffset.UnixEpoch);
        for (var index = 1; index <= 5; index++) history.Append("Format attempt", index.ToString());

        Assert.Equal([5L, 4L, 3L], history.Entries.Select(entry => entry.Sequence));
        Assert.Equal(["5", "4", "3"], history.Entries.Select(entry => entry.Detail));
    }

    [Fact]
    public void Default_history_is_bounded_at_two_hundred_entries()
    {
        var history = new ControlEventHistory();
        for (var index = 0; index < 250; index++) history.Append("Observed session", "Session changed.");

        Assert.Equal(200, history.Entries.Count);
        Assert.Equal(250, history.Entries[0].Sequence);
        Assert.Equal(51, history.Entries[^1].Sequence);
    }

    [Fact]
    public void Retrieved_history_is_a_snapshot_and_cannot_mutate_internal_storage()
    {
        var history = new ControlEventHistory(2);
        var first = history.Append("Selected source", "Music");
        var captured = history.Entries;
        history.Append("Format attempt", "Requested 96 kHz.");
        history.Append("Format result", "Endpoint readback: 96 kHz.");

        Assert.Same(first, Assert.Single(captured));
        var mutableCopy = Assert.IsType<ControlHistoryEntry[]>(captured);
        mutableCopy[0] = new(999, DateTimeOffset.UnixEpoch, "Changed", "Caller copy only.");
        Assert.DoesNotContain(history.Entries, entry => entry.Sequence == 999);
    }

    [Fact]
    public void Stored_event_time_is_utc_and_preserves_actual_instant()
    {
        var local = new DateTimeOffset(2026, 10, 20, 12, 30, 0, TimeSpan.FromHours(-4));
        var history = new ControlEventHistory(2, () => local);

        var entry = history.Append("Observed session", "Fresh instance.");

        Assert.Equal(TimeSpan.Zero, entry.Utc.Offset);
        Assert.Equal(local, entry.Utc);
        Assert.Equal("Fresh instance.", entry.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_capacity_is_rejected(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlEventHistory(capacity));

    [Theory]
    [InlineData("", "Detail")]
    [InlineData("Kind", " ")]
    public void Empty_event_metadata_is_rejected_without_adding_a_record(string kind, string detail)
    {
        var history = new ControlEventHistory();

        Assert.Throws<ArgumentException>(() => history.Append(kind, detail));

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Concurrent_appenders_cannot_exceed_capacity_or_duplicate_sequence_numbers()
    {
        var history = new ControlEventHistory(23);
        Parallel.For(0, 400, index => history.Append("Observation", index.ToString()));

        var entries = history.Entries;

        Assert.Equal(23, entries.Count);
        Assert.Equal(400, entries[0].Sequence);
        Assert.Equal(23, entries.Select(entry => entry.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(378, 23).Select(value => (long)value).Reverse(), entries.Select(entry => entry.Sequence));
    }
}
