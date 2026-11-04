using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SourceListPresentationTests
{
    [Fact]
    public void Master_stays_first_then_favorites_then_other_apps_even_when_master_is_paused()
    {
        var settings = Settings();
        var master = Source("Music", active: false);
        settings.SelectMaster(master.Identity, master.ProcessName);
        settings.SetFavorite(Source("Zulu").Identity, "Zulu", true);

        var entries = SourceListPresentation.Build([Source("Alpha"), Source("Zulu"), master], settings);

        Assert.Equal(["Music", "Zulu", "Alpha"], entries.Select(entry => entry.Name));
        Assert.True(entries[0].IsMaster);
        Assert.False(entries[0].IsFavorite);
        Assert.Equal("★ Zulu", entries[1].DisplayName);
    }

    [Fact]
    public void Active_favorites_precede_paused_and_offline_favorites_and_sort_by_name()
    {
        var settings = Settings();
        foreach (var name in new[] { "Paused", "Offline", "zulu", "Alpha" })
            settings.SetFavorite(Source(name).Identity, name, true);

        var entries = SourceListPresentation.Build([Source("Paused", false), Source("zulu"), Source("Alpha")], settings);

        Assert.Equal(["Alpha", "zulu", "Offline", "Paused"], entries.Select(entry => entry.Name));
        Assert.Equal("No session", entries[2].SessionText);
        Assert.Equal("Idle / paused", entries[3].SessionText);
    }

    [Fact]
    public void Offline_favorite_is_a_saved_choice_without_a_fabricated_live_session()
    {
        var settings = Settings();
        var favorite = Source("Music");
        settings.SetFavorite(favorite.Identity, "Saved music player", true);

        var entry = Assert.Single(SourceListPresentation.Build([], settings));

        Assert.Equal("Saved music player", entry.Name);
        Assert.Equal(favorite.Identity, entry.Identity);
        Assert.Null(entry.LiveSource);
        Assert.True(entry.CanSelectSaved);
        Assert.Equal("No session", entry.SessionText);
        var rate = SourceListPresentation.DescribeSavedRate(entry, settings);
        Assert.Equal("Not observed", rate.Text);
        Assert.Null(rate.Evidence);
        Assert.Contains("does not launch", rate.Detail);
    }

    [Fact]
    public void Selected_offline_favorite_is_deduplicated_case_insensitively()
    {
        var source = Source("Music");
        var settings = Settings();
        settings.SetFavorite(source.Identity, "Music", true);
        settings.SelectMaster(source.Identity.ToLowerInvariant(), "Music");

        var entry = Assert.Single(SourceListPresentation.Build([], settings));

        Assert.True(entry.IsMaster);
        Assert.True(entry.IsFavorite);
        Assert.Equal("Rate held", SourceListPresentation.DescribeSavedRate(entry, settings).Text);
    }

    [Fact]
    public void Restarted_favorite_uses_current_live_session_and_no_offline_duplicate()
    {
        var source = Source("Music");
        var settings = Settings();
        settings.SetFavorite(source.Identity.ToLowerInvariant(), "Previous name", true);
        var restarted = source with { ProcessIds = [987], SessionToken = "new-session" };

        var entry = Assert.Single(SourceListPresentation.Build([restarted], settings));

        Assert.Same(restarted, entry.LiveSource);
        Assert.Equal("Music", entry.Name);
        Assert.True(entry.IsFavorite);
        Assert.False(entry.CanSelectSaved);
        Assert.Equal("Active", entry.SessionText);
    }

    [Fact]
    public void Removing_favorite_removes_offline_row_but_preserves_selected_master_placeholder()
    {
        var source = Source("Music");
        var settings = Settings();
        settings.SetFavorite(source.Identity, source.ProcessName, true);
        settings.SetFavorite(source.Identity, source.ProcessName, false);
        Assert.Empty(SourceListPresentation.Build([], settings));
        settings.SelectMaster(source.Identity, source.ProcessName);

        var entry = Assert.Single(SourceListPresentation.Build([], settings));

        Assert.True(entry.IsMaster);
        Assert.False(entry.IsFavorite);
        Assert.True(entry.CanSelectSaved);
    }

    [Fact]
    public void Offline_manual_target_takes_priority_over_held_rate_and_never_claims_observed_stream()
    {
        var source = Source("Music");
        var settings = Settings();
        settings.SelectMaster(source.Identity, source.ProcessName);
        settings.RememberAppliedRate(source.Identity, 96000);
        settings.ManualSampleRatesHz[source.Identity.ToLowerInvariant()] = 48000;
        var entry = Assert.Single(SourceListPresentation.Build([], settings));

        var rate = SourceListPresentation.DescribeSavedRate(entry, settings);

        Assert.Equal("48 kHz manual", rate.Text);
        Assert.Contains("no current audio session", rate.Detail);
        Assert.Null(rate.Evidence);
    }

    [Fact]
    public void Offline_master_holds_its_confirmed_rate_but_other_favorite_cannot_borrow_it()
    {
        var source = Source("Music");
        var other = Source("Other");
        var settings = Settings();
        settings.SelectMaster(source.Identity, source.ProcessName);
        settings.RememberAppliedRate(source.Identity, 192000);
        settings.SetFavorite(other.Identity, other.ProcessName, true);
        var entries = SourceListPresentation.Build([], settings);

        Assert.Equal("192 kHz held", SourceListPresentation.DescribeSavedRate(entries[0], settings).Text);
        Assert.Equal("Not observed", SourceListPresentation.DescribeSavedRate(entries[1], settings).Text);
    }

    [Fact]
    public void Invalid_held_or_manual_rate_never_becomes_a_numeric_offline_target()
    {
        var source = Source("Music");
        var settings = Settings();
        settings.SelectMaster(source.Identity, source.ProcessName);
        settings.LastKnownSourceKey = source.Identity;
        settings.LastKnownSampleRateHz = 7999;
        settings.ManualSampleRatesHz[source.Identity] = 768001;

        var entry = Assert.Single(SourceListPresentation.Build([], settings));

        Assert.Equal("Rate held", SourceListPresentation.DescribeSavedRate(entry, settings).Text);
    }

    [Fact]
    public void Temporary_and_system_rows_cannot_be_selected_as_saved_apps_and_excluded_apps_are_omitted()
    {
        var settings = Settings();
        var system = new PlatformAudioSource("system-sounds", "System sounds", null, [], false, false, "system");
        var unavailable = system with { Identity = "unavailable:endpoint:123:session", ProcessName = "Unavailable process" };
        var entries = SourceListPresentation.Build([system, unavailable, Source("ShareX")], settings);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.False(entry.CanSelectSaved));
        Assert.All(entries, entry => Assert.False(entry.IsFavorite));
    }

    [Fact]
    public void Case_variant_duplicate_live_rows_choose_active_current_evidence_once()
    {
        var source = Source("Music");
        var idle = source with { Identity = source.Identity.ToLowerInvariant(), IsActive = false, SessionToken = "old" };

        var entry = Assert.Single(SourceListPresentation.Build([idle, source], Settings()));

        Assert.Same(source, entry.LiveSource);
    }

    private static SourceSettings Settings() => new();
    private static PlatformAudioSource Source(string name, bool active = true)
    {
        var path = @"C:\Apps\" + name + ".exe";
        return new(SourceIdentity.FromExecutablePath(path), name, path, [123], active, true, name + "-session");
    }
}
