using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using WindowsSourceSwitcher.Core;
using WindowsSourceSwitcher.Tray.Audio;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class SourcePopupFavoritesTests
{
    [Fact]
    public void Pin_button_only_requests_a_pin_and_offline_pins_keep_their_own_marker() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Pinned");
        var settings = Settings(source);
        var events = new List<string>();
        popup.PinChanged += (_, _, pinned) => events.Add(pinned ? "pin" : "unpin");
        popup.FavoriteChanged += (_, _, _) => events.Add("favorite");
        popup.RateSelected += _ => events.Add("rate");
        popup.SourceSelected += _ => events.Add("source");
        Render(popup, settings, [source]);
        Click(Find<Button>(popup).Single(button => button.AccessibleName == "Pin master"));
        Assert.Equal(new[] { "pin" }, events);
        settings.SetPinned(source.Identity, source.ProcessName, true);
        Render(popup, settings, []);
        Assert.Equal("◆ Pinned", Assert.Single(Rows(popup)).Text);
        Assert.Equal("No session", Assert.Single(Rows(popup)).SubItems[1].Text);
        Assert.True(Find<Button>(popup).Single(button => button.AccessibleName == "Unpin master").Enabled);
        Assert.Equal(new[] { "pin" }, events);
        Render(popup, settings, [], applying: true);
        Assert.False(Find<Button>(popup).Single(button => button.AccessibleName == "Unpin master").Enabled);
    });

    [Fact]
    public void Favorites_and_pins_are_distinct_and_favorite_marker_wins_when_both_are_set() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        settings.SetPinned(source.Identity, source.ProcessName, true);
        settings.SetFavorite(source.Identity, source.ProcessName, true);
        Render(popup, settings, [source]);
        Assert.Equal("★ Music", Assert.Single(Rows(popup)).Text);
        Assert.Equal("Unfavorite master", FavoriteButton(popup).AccessibleName);
        Assert.Contains(Find<Button>(popup), button => button.AccessibleName == "Unpin master");
        settings.SetFavorite(source.Identity, source.ProcessName, false);
        Render(popup, settings, [source]);
        Assert.Equal("◆ Music", Assert.Single(Rows(popup)).Text);
    });

    [Fact]
    public void Rendering_favorites_does_not_select_a_master_or_request_rate_volume_or_favorite_writes() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        settings.SetFavorite(source.Identity, source.ProcessName, true);
        var events = new List<string>();
        popup.SourceSelected += _ => events.Add("source");
        popup.SavedSourceSelected += (_, _) => events.Add("saved");
        popup.FavoriteChanged += (_, _, _) => events.Add("favorite");
        popup.RateSelected += _ => events.Add("rate");
        popup.VolumeChangeRequested += _ => events.Add("volume");

        Render(popup, settings, [source]);
        Render(popup, settings, []);
        _ = popup.Handle;
        _ = Find<ListView>(popup).Single(list => list.AccessibleName == "Audio sources; star marks favorites").Handle;

        Assert.Empty(events);
        Assert.Equal(source.Identity, settings.SelectedSourceKey);
        Assert.Equal("★ Music", Assert.Single(Rows(popup)).Text);
        Assert.Equal("No session", Assert.Single(Rows(popup)).SubItems[1].Text);
        Assert.Equal("Unfavorite master", FavoriteButton(popup).AccessibleName);
        Assert.Equal(680, popup.ClientSize.Width);
    });

    [Fact]
    public void Favorite_button_requests_only_bookmark_toggle_for_current_master() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        settings.RememberAppliedRate(source.Identity, 96000);
        settings.ManualSampleRatesHz[source.Identity] = 48000;
        var revision = settings.SelectionRevision;
        var requests = new List<(string Key, string Name, bool Add)>();
        var selections = 0;
        var rateChanges = 0;
        popup.FavoriteChanged += (key, name, add) => requests.Add((key, name, add));
        popup.SourceSelected += _ => selections++;
        popup.SavedSourceSelected += (_, _) => selections++;
        popup.RateSelected += _ => rateChanges++;
        Render(popup, settings, [source]);

        Click(FavoriteButton(popup));

        var request = Assert.Single(requests);
        Assert.Equal((source.Identity, "Music", true), request);
        Assert.Equal(0, selections);
        Assert.Equal(0, rateChanges);
        Assert.Equal(source.Identity, settings.SelectedSourceKey);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
        Assert.Equal(48000, settings.ManualSampleRatesHz[source.Identity]);
        Assert.False(settings.IsFavorite(source.Identity)); // Only the owner persists a requested change.
    });

    [Fact]
    public void Favorite_changes_update_star_and_toggle_caption_without_session_restart() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        var requests = new List<bool>();
        popup.FavoriteChanged += (_, _, add) => requests.Add(add);
        Render(popup, settings, [source]);
        Assert.Equal("Music", Assert.Single(Rows(popup)).Text);
        Assert.Equal("Favorite master", FavoriteButton(popup).AccessibleName);
        settings.SetFavorite(source.Identity, source.ProcessName, true);

        Render(popup, settings, [source]);
        Click(FavoriteButton(popup));

        Assert.Equal("★ Music", Assert.Single(Rows(popup)).Text);
        Assert.Equal("Unfavorite master", FavoriteButton(popup).AccessibleName);
        Assert.False(Assert.Single(requests));
        settings.SetFavorite(source.Identity, source.ProcessName, false);
        Render(popup, settings, [source]);
        Assert.Equal("Music", Assert.Single(Rows(popup)).Text);
        Assert.Equal("Favorite master", FavoriteButton(popup).AccessibleName);
    });

    [Fact]
    public void Absent_saved_master_can_be_favorited_without_creating_a_live_source() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        (string Key, string Name, bool Add)? request = null;
        popup.FavoriteChanged += (key, name, add) => request = (key, name, add);
        var liveSelections = 0;
        popup.SourceSelected += _ => liveSelections++;
        Render(popup, settings, []);

        Assert.True(FavoriteButton(popup).Enabled);
        Click(FavoriteButton(popup));

        Assert.Equal((source.Identity, source.ProcessName, true), request);
        Assert.Equal(0, liveSelections);
        Assert.Null(Assert.IsType<SourceListEntry>(Assert.Single(Rows(popup)).Tag).LiveSource);
    });

    [Fact]
    public void Choosing_offline_favorite_emits_saved_identity_only_and_never_fabricates_live_evidence() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var master = Source("Music");
        var favorite = Source("Video");
        var settings = Settings(master);
        settings.SetFavorite(favorite.Identity, favorite.ProcessName, true);
        var live = new List<PlatformAudioSource>();
        var saved = new List<(string Key, string Name)>();
        popup.SourceSelected += live.Add;
        popup.SavedSourceSelected += (key, name) => saved.Add((key, name));
        Render(popup, settings, [master]);

        SelectRow(popup, "★ Video");

        Assert.Empty(live);
        Assert.Equal((favorite.Identity, favorite.ProcessName), Assert.Single(saved));
        Assert.Equal(master.Identity, settings.SelectedSourceKey); // UI requests selection; owner decides/persists.
    });

    [Fact]
    public void Choosing_live_favorite_emits_exact_current_source_instead_of_saved_entry() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var master = Source("Music");
        var favorite = Source("Video");
        var settings = Settings(master);
        settings.SetFavorite(favorite.Identity, favorite.ProcessName, true);
        var live = new List<PlatformAudioSource>();
        var saved = 0;
        popup.SourceSelected += live.Add;
        popup.SavedSourceSelected += (_, _) => saved++;
        Render(popup, settings, [master, favorite]);

        SelectRow(popup, "★ Video");

        Assert.Same(favorite, Assert.Single(live));
        Assert.Equal(0, saved);
    });

    [Theory]
    [InlineData("system-sounds", "System sounds")]
    [InlineData("unavailable:endpoint:123:session", "Unavailable process")]
    [InlineData(@"C:\Apps\ShareX.exe", "ShareX")]
    public void Unsafe_master_favorite_action_is_disabled(string key, string name) => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var settings = new SourceSettings { SelectedSourceKey = key, SelectedSourceDisplayName = name };
        var requests = 0;
        popup.FavoriteChanged += (_, _, _) => requests++;

        Render(popup, settings, []);
        Click(FavoriteButton(popup));

        Assert.False(FavoriteButton(popup).Enabled);
        Assert.Equal(0, requests);
    });

    [Fact]
    public void Applying_format_disables_favorite_action_until_complete() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        var requests = 0;
        popup.FavoriteChanged += (_, _, _) => requests++;
        Render(popup, settings, [source], applying: true);

        Click(FavoriteButton(popup));

        Assert.False(FavoriteButton(popup).Enabled);
        Assert.Equal(0, requests);
        Render(popup, settings, [source]);
        Assert.True(FavoriteButton(popup).Enabled);
    });

    [Fact]
    public void Favorite_capacity_disables_adding_but_keeps_remove_available() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        for (var index = 0; index < SourceFavorites.MaxFavorites; index++)
        {
            var favorite = Source("Favorite" + index);
            settings.SetFavorite(favorite.Identity, favorite.ProcessName, true);
        }
        Render(popup, settings, [source]);
        Assert.False(FavoriteButton(popup).Enabled);
        var saved = settings.FavoriteSources.First();
        settings.SelectMaster(saved.Key, saved.Value);

        Render(popup, settings, []);

        Assert.True(FavoriteButton(popup).Enabled);
        Assert.Equal("Unfavorite master", FavoriteButton(popup).AccessibleName);
    });

    [Fact]
    public void Offline_held_rate_updates_even_when_session_and_favorite_set_are_unchanged() => OnSta(() =>
    {
        using var popup = new SourcePopup();
        var source = Source("Music");
        var settings = Settings(source);
        settings.SetFavorite(source.Identity, source.ProcessName, true);
        Render(popup, settings, []);
        Assert.Equal("Rate held", Assert.Single(Rows(popup)).SubItems[2].Text);
        settings.RememberAppliedRate(source.Identity, 96000);

        Render(popup, settings, []);

        Assert.Equal("96 kHz held", Assert.Single(Rows(popup)).SubItems[2].Text);
        Assert.Equal("No session", Assert.Single(Rows(popup)).SubItems[1].Text);
    });

    private static SourceSettings Settings(PlatformAudioSource source)
    {
        var settings = new SourceSettings();
        settings.SelectMaster(source.Identity, source.ProcessName);
        return settings;
    }

    private static PlatformAudioSource Source(string name)
    {
        var path = @"C:\Apps\" + name + ".exe";
        return new(SourceIdentity.FromExecutablePath(path), name, path, [123], true, true, name + "-session");
    }

    private static void Render(SourcePopup popup, SourceSettings settings, IReadOnlyList<PlatformAudioSource> sources,
        bool applying = false) => popup.Render([], sources, [], null, settings, "Status", "Evidence", applying);
    private static Button FavoriteButton(SourcePopup popup) => Find<Button>(popup)
        .Single(button => button.AccessibleName is "Favorite master" or "Unfavorite master");
    private static ListViewItem[] Rows(SourcePopup popup) => Find<ListView>(popup).Single(list => list.AccessibleName == "Audio sources; star marks favorites").Items.Cast<ListViewItem>().ToArray();
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, [EventArgs.Empty]);

    private static void SelectRow(SourcePopup popup, string name)
    {
        // Native ListView selection notifications require a handle, but no visible window or audio APIs.
        var list = Find<ListView>(popup).Single(list => list.AccessibleName == "Audio sources; star marks favorites");
        _ = popup.Handle;
        _ = list.Handle;
        list.SelectedIndices.Clear();
        list.Items.Cast<ListViewItem>().Single(item => item.Text == name).Selected = true;
    }

    private static IEnumerable<T> Find<T>(Control control) where T : Control =>
        control.Controls.Cast<Control>().SelectMany(child => (child is T match ? new[] { match } : []).Concat(Find<T>(child)));

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Source popup UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
