using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class FullscreenPriorityIntegrationPolicyTests
{
    private static readonly FullscreenCandidate Browser = new(123, @"C:\Apps\helium.exe", "Helium");
    private static readonly FullscreenCandidate Video = new(456, @"C:\Apps\video.exe", "Video");
    private static SourceSettings Settings()
    {
        var settings = new SourceSettings { TargetEndpointId = "dac", FullscreenAutoSwitchEnabled = true };
        settings.SelectMaster(@"C:\Apps\music.exe", "Music");
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 96000);
        return settings;
    }
    private static void Apply(FullscreenMasterPolicy policy, SourceSettings settings, FullscreenMasterSelection choice)
    {
        if (choice.SourceKey is { } key) settings.SelectMaster(key, choice.DisplayName!);
        else { settings.SelectedSourceKey = settings.SelectedSourceDisplayName = null; settings.SelectionRevision++; }
        settings.LastKnownSourceKey = choice.HeldSourceKey;
        settings.LastKnownSampleRateHz = choice.HeldRateHz;
        settings.Validate();
        policy.ConfirmSelection(settings);
    }
    [Fact]
    public void Any_fullscreen_app_takes_over_without_being_a_favorite_and_restores_held_rate()
    {
        var settings = Settings(); var policy = new FullscreenMasterPolicy();
        Apply(policy, settings, Assert.IsType<FullscreenMasterSelection>(policy.Update(true, settings, Browser)));
        Assert.True(policy.HasPriority); Assert.Equal(Browser.SourceKey, settings.SelectedSourceKey);
        Assert.Null(settings.LastKnownSampleRateHz);
        Assert.Null(policy.Update(true, settings, Browser));
        Apply(policy, settings, Assert.IsType<FullscreenMasterSelection>(policy.Update(true, settings, null)));
        Assert.False(policy.HasPriority); Assert.Equal("Music", settings.SelectedSourceDisplayName);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
    }
    [Fact]
    public void Switching_between_fullscreen_apps_retains_original_backup()
    {
        var settings = Settings(); var policy = new FullscreenMasterPolicy();
        Apply(policy, settings, policy.Update(true, settings, Browser)!);
        Apply(policy, settings, policy.Update(true, settings, Video)!);
        Apply(policy, settings, policy.Update(false, settings, Video)!);
        Assert.Equal("Music", settings.SelectedSourceDisplayName); Assert.Equal(96000, settings.LastKnownSampleRateHz);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_release_or_selection_wins_until_episode_changes(bool release)
    {
        var settings = Settings(); var policy = new FullscreenMasterPolicy();
        Apply(policy, settings, policy.Update(true, settings, Browser)!);
        if (release) { settings.SelectedSourceKey = settings.SelectedSourceDisplayName = null; settings.SelectionRevision++; }
        else settings.SelectMaster(@"C:\Apps\other.exe", "Other");
        var manualKey = settings.SelectedSourceKey;
        Assert.Null(policy.Update(true, settings, Browser)); Assert.False(policy.HasPriority);
        Assert.Null(policy.Update(true, settings, Browser)); Assert.Equal(manualKey, settings.SelectedSourceKey);
        Assert.Null(policy.Update(true, settings, null));
        Assert.NotNull(policy.Update(true, settings, Browser));
    }
    [Fact]
    public void Endpoint_or_revision_change_does_not_restore_stale_backup()
    {
        foreach (var endpointChange in new[] { false, true })
        {
            var settings = Settings(); var policy = new FullscreenMasterPolicy();
            Apply(policy, settings, policy.Update(true, settings, Browser)!);
            if (endpointChange) settings.TargetEndpointId = "another-dac";
            else settings.SelectionRevision++;
            Assert.Null(policy.Update(false, settings, Browser)); Assert.False(policy.HasPriority);
            Assert.Equal(Browser.SourceKey, settings.SelectedSourceKey);
        }
    }
    [Fact]
    public void Disabled_main_switching_and_option_do_not_acquire()
    {
        var settings = Settings(); var policy = new FullscreenMasterPolicy();
        Assert.Null(policy.Update(false, settings, Browser));
        settings.SwitchingEnabled = false;
        Assert.Null(policy.Update(true, settings, Browser)); Assert.False(policy.HasPriority);
    }
    [Fact]
    public void Existing_master_is_not_claimed_for_restoration()
    {
        var settings = Settings(); var policy = new FullscreenMasterPolicy();
        settings.SelectMaster(Browser.SourceKey, Browser.DisplayName);
        Assert.Null(policy.Update(true, settings, Browser)); Assert.False(policy.HasPriority);
        Assert.Null(policy.Update(true, settings, null)); Assert.Equal(Browser.SourceKey, settings.SelectedSourceKey);
    }
    [Fact]
    public void An_originally_released_master_is_restored_as_released()
    {
        var settings = new SourceSettings(); var policy = new FullscreenMasterPolicy();
        Apply(policy, settings, policy.Update(true, settings, Browser)!);
        Apply(policy, settings, policy.Update(false, settings, null)!);
        Assert.Null(settings.SelectedSourceKey); Assert.Null(settings.LastKnownSourceKey);
        Assert.False(policy.HasPriority);
    }
}
