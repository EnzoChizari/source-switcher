using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class SourceExclusionsTests
{
    [Theory]
    [InlineData(@"C:\Program Files\ShareX\ShareX.exe", null, true)]
    [InlineData(@"C:\Apps\sHaReX.ExE", null, true)]
    [InlineData("/apps/ShareX.exe", null, true)]
    [InlineData("ShareX.exe", null, true)]
    [InlineData(@"C:\Apps\ShareXHelper.exe", null, false)]
    [InlineData(@"C:\ShareX.exe\spotify.exe", null, false)]
    [InlineData(@"C:\Apps\MyShareX.exe", null, false)]
    [InlineData(@"C:\Apps\ShareX.exe.backup", null, false)]
    [InlineData(@"C:\Apps\spotify.exe", "ShareX", false)]
    [InlineData(null, "ShareX", true)]
    [InlineData("", "sharex", true)]
    [InlineData(" ", "SHAREX", true)]
    [InlineData(null, "ShareXHelper", false)]
    [InlineData(null, "ShareX.exe", false)]
    [InlineData(null, "ShareX ", false)]
    [InlineData(null, null, false)]
    public void Matches_only_exact_executable_or_unavailable_path_fallback(
        string? path, string? processName, bool expected)
    {
        Assert.Equal(expected, SourceExclusions.IsExcluded(path, processName));
    }

    [Fact]
    public void Excluded_saved_master_is_released_without_replacement_or_endpoint_change()
    {
        var settings = new SourceSettings { TargetEndpointId = "dx5" };
        settings.SelectMaster(@"C:\Apps\ShareX.exe", "ShareX");
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 96000);
        settings.ManualSampleRatesHz[settings.SelectedSourceKey!] = 96000;
        settings.ManualSampleRatesHz[@"C:\Apps\Music.exe"] = 44100;
        var revision = settings.SelectionRevision;

        Assert.True(SourceExclusions.ApplyToSettings(settings));
        Assert.Null(settings.SelectedSourceKey);
        Assert.Null(settings.SelectedSourceDisplayName);
        Assert.Null(settings.LastKnownSourceKey);
        Assert.Null(settings.LastKnownSampleRateHz);
        Assert.Equal(revision + 1, settings.SelectionRevision);
        Assert.Equal("dx5", settings.TargetEndpointId);
        Assert.True(settings.SwitchingEnabled);
        Assert.Single(settings.ManualSampleRatesHz);
        Assert.Equal(44100, settings.ManualSampleRatesHz[@"C:\Apps\Music.exe"]);
        settings.Validate();
        Assert.False(SourceExclusions.ApplyToSettings(settings));
    }

    [Fact]
    public void Removes_excluded_history_but_preserves_an_unrelated_master_and_held_rate()
    {
        var settings = new SourceSettings { TargetEndpointId = "dx5" };
        settings.SelectMaster(@"C:\Apps\Music.exe", "Music");
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 48000);
        settings.ManualSampleRatesHz[@"C:\Apps\SHAREX.EXE"] = 96000;
        var revision = settings.SelectionRevision;

        Assert.True(SourceExclusions.ApplyToSettings(settings));
        Assert.Equal(@"C:\Apps\Music.exe", settings.SelectedSourceKey);
        Assert.Equal(48000, settings.LastKnownSampleRateHz);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Empty(settings.ManualSampleRatesHz);
        settings.Validate();
    }

    [Fact]
    public void Similar_names_and_an_unselected_app_leave_settings_unchanged()
    {
        var settings = new SourceSettings();
        settings.ManualSampleRatesHz[@"C:\Apps\ShareXHelper.exe"] = 48000;
        Assert.False(SourceExclusions.ApplyToSettings(settings));
        Assert.Single(settings.ManualSampleRatesHz);
        Assert.Null(settings.SelectedSourceKey);
        Assert.Equal(0, settings.SelectionRevision);
    }
}
