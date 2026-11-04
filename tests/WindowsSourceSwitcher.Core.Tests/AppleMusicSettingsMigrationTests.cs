using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class AppleMusicSettingsMigrationTests
{
    private const string Package = @"C:\Program Files\WindowsApps\AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa";
    private const string Main = Package + @"\AppleMusic.exe";
    private const string Helper = Package + @"\AMPLibraryAgent.exe";

    [Fact]
    public void HelperMasterPreferencesMigrateTogetherWithoutDiscardingConfirmedRate()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(Helper, "AMPLibraryAgent");
        settings.RememberAppliedRate(Helper, 96000);
        settings.ManualSampleRatesHz[Helper] = 192000;
        settings.SetFavorite(Helper, "AMPLibraryAgent", true);
        var revision = settings.SelectionRevision;

        Assert.True(AppleMusicIdentity.MigrateSettings(settings));
        Assert.Equal(Main.ToUpperInvariant(), settings.SelectedSourceKey);
        Assert.Equal("AppleMusic", settings.SelectedSourceDisplayName);
        Assert.Equal(revision + 1, settings.SelectionRevision);
        Assert.Equal(Main.ToUpperInvariant(), settings.LastKnownSourceKey);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
        Assert.Equal(192000, Assert.Single(settings.ManualSampleRatesHz).Value);
        Assert.Equal("AppleMusic", Assert.Single(settings.FavoriteSources).Value);
        Assert.False(AppleMusicIdentity.MigrateSettings(settings));
        settings.Validate();
    }

    [Fact]
    public void ExplicitMainOverridesAndFavoriteNamesWinOverConflictingHelperValues()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(Helper, "My music source");
        settings.ManualSampleRatesHz[Helper] = 44100;
        settings.ManualSampleRatesHz[Main] = 192000;
        settings.SetFavorite(Helper, "Helper custom favorite", true);
        settings.SetFavorite(Main, "My explicit Apple favorite", true);
        Assert.True(AppleMusicIdentity.MigrateSettings(settings));
        Assert.Equal("My music source", settings.SelectedSourceDisplayName);
        Assert.Equal(192000, Assert.Single(settings.ManualSampleRatesHz).Value);
        Assert.Equal("My explicit Apple favorite", Assert.Single(settings.FavoriteSources).Value);
    }

    [Fact]
    public void MainMasterDoesNotGetReselectedWhenOnlyLegacyHelperFavoriteChanges()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(Main.ToUpperInvariant(), "AppleMusic");
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 192000);
        settings.SetFavorite(Helper, "Custom favorite", true);
        var revision = settings.SelectionRevision;
        Assert.True(AppleMusicIdentity.MigrateSettings(settings));
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(192000, settings.LastKnownSampleRateHz);
        Assert.Equal("Custom favorite", Assert.Single(settings.FavoriteSources).Value);
    }

    [Fact]
    public void NonAppleSettingsAndUnrelatedPackageVersionsArePreserved()
    {
        const string spotify = @"C:\Apps\Spotify.exe";
        var settings = new SourceSettings();
        settings.SelectMaster(spotify, "Spotify");
        settings.ManualSampleRatesHz[spotify] = 48000;
        settings.SetFavorite(spotify, "Spotify", true);
        var revision = settings.SelectionRevision;
        Assert.False(AppleMusicIdentity.MigrateSettings(settings));
        Assert.Equal(spotify, settings.SelectedSourceKey);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(48000, settings.ManualSampleRatesHz[spotify]);
        settings.SetFavorite(Helper, "AMPLibraryAgent", true);
        var otherMain = Main.Replace("1.1540.23042.0", "1.1540.23043.0", StringComparison.Ordinal);
        settings.SetFavorite(otherMain, "New package", true);
        Assert.True(AppleMusicIdentity.MigrateSettings(settings));
        Assert.Equal(3, settings.FavoriteSources.Count);
        Assert.True(settings.FavoriteSources.ContainsKey(Main));
        Assert.True(settings.FavoriteSources.ContainsKey(otherMain));
        Assert.Equal(spotify, settings.SelectedSourceKey);
        Assert.Equal(revision, settings.SelectionRevision);
    }

    [Fact]
    public void RevisionOverflowCannotPartiallyRewriteSettings()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(Helper, "AMPLibraryAgent");
        settings.SelectionRevision = long.MaxValue;
        settings.ManualSampleRatesHz[Helper] = 96000;
        Assert.Throws<OverflowException>(() => AppleMusicIdentity.MigrateSettings(settings));
        Assert.Equal(Helper, settings.SelectedSourceKey);
        Assert.Equal("AMPLibraryAgent", settings.SelectedSourceDisplayName);
        Assert.True(settings.ManualSampleRatesHz.ContainsKey(Helper));
    }
}
