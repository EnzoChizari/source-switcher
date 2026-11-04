using System.Text.Json;
using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class SourceFavoritesTests : IDisposable
{
    private const string PlayerKey = @"C:\Apps\Player.exe";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SourceFavoritesTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");

    [Fact]
    public void DefaultsAreEmptyAndUseWindowsCaseSemantics()
    {
        var settings = new SourceSettings();
        Assert.Empty(settings.FavoriteSources);
        Assert.Same(StringComparer.OrdinalIgnoreCase, settings.FavoriteSources.Comparer);
        Assert.False(settings.IsFavorite(PlayerKey));
    }

    [Theory]
    [InlineData(@"C:\Apps\Player.exe")]
    [InlineData(@"d:\Program Files\Music Player\player.EXE")]
    [InlineData(@"\\server\share\Apps\Player.exe")]
    [InlineData("C:/Apps/Player.exe")]
    [InlineData(@"C:\应用\Player.exe")]
    public void StableDriveAndUncExecutableIdentitiesCanBePinned(string path)
    {
        var settings = new SourceSettings();
        Assert.True(SourceFavorites.CanFavorite(path, "Music Player"));
        Assert.True(settings.SetFavorite(path, "Music Player", true));
        Assert.True(settings.IsFavorite(path.ToLowerInvariant()));
        settings.Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("system-sounds")]
    [InlineData("pid:1234")]
    [InlineData("unavailable:1234")]
    [InlineData("Player.exe")]
    [InlineData(@"C:Player.exe")]
    [InlineData(@"\Apps\Player.exe")]
    [InlineData(@"C:\Apps\..\Player.exe")]
    [InlineData(@"C:\.\Player.exe")]
    [InlineData(@"C:\Apps\\Player.exe")]
    [InlineData(@"C:\Apps\Player.dll")]
    [InlineData(@"C:\Apps\Player.exe ")]
    [InlineData(@"C:\Apps \Player.exe")]
    [InlineData(@"C:\Apps\Player.exe:stream.exe")]
    [InlineData(@"C:\Apps\Player?.exe")]
    [InlineData(@"\\server\Player.exe")]
    [InlineData(@"\\?\C:\Apps\Player.exe")]
    [InlineData(@"\\.\Apps\Player.exe")]
    [InlineData(@"C:\Apps\ShareX.exe")]
    public void TransientInvalidAndExcludedIdentitiesCannotBePinned(string? path)
    {
        var settings = new SourceSettings();
        Assert.False(SourceFavorites.CanFavorite(path, "Player"));
        Assert.Throws<ArgumentException>(() => settings.SetFavorite(path!, "Player", true));
        Assert.Empty(settings.FavoriteSources);
        Assert.False(settings.IsFavorite(path!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Player\nOther")]
    [InlineData("Player\tOther")]
    public void EmptyAndControlCharacterNamesCannotBePinned(string? name)
    {
        Assert.False(SourceFavorites.CanFavorite(PlayerKey, name));
        Assert.Throws<ArgumentException>(() => new SourceSettings().SetFavorite(PlayerKey, name!, true));
    }

    [Fact]
    public void DisplayNameAndPathLengthsAreBounded()
    {
        Assert.True(SourceFavorites.CanFavorite(PlayerKey, new string('x', SourceFavorites.MaxDisplayNameLength)));
        Assert.False(SourceFavorites.CanFavorite(PlayerKey, new string('x', SourceFavorites.MaxDisplayNameLength + 1)));
        Assert.False(SourceFavorites.CanFavorite(@"C:\" + new string('x', SourceFavorites.MaxSourceKeyLength) + ".exe", "Player"));
    }

    [Fact]
    public void RepeatedPinIsIdempotentCaseInsensitiveAndCanUpdateDisplayName()
    {
        var settings = new SourceSettings();
        Assert.True(settings.SetFavorite(PlayerKey, "Player", true));
        Assert.False(settings.SetFavorite(PlayerKey.ToUpperInvariant(), "Player", true));
        Assert.False(settings.SetFavorite("c:/apps/player.exe", "Player", true));
        Assert.True(settings.SetFavorite(PlayerKey.ToLowerInvariant(), "Music Player", true));
        var favorite = Assert.Single(settings.FavoriteSources);
        Assert.Equal("Music Player", favorite.Value);
        Assert.True(settings.SetFavorite(PlayerKey.ToLowerInvariant(), "Music Player", false));
        Assert.False(settings.SetFavorite(PlayerKey, "Player", false));
        Assert.False(settings.IsFavorite(PlayerKey));
    }

    [Fact]
    public void FavoriteLimitRejectsOnlyAnAdditionalIdentityAndStillAllowsUnpinning()
    {
        var settings = new SourceSettings();
        for (var index = 0; index < SourceFavorites.MaxFavorites; index++)
            settings.SetFavorite($@"C:\Apps\Player{index}.exe", $"Player {index}", true);
        Assert.Throws<InvalidOperationException>(() => settings.SetFavorite(PlayerKey, "One too many", true));
        Assert.Equal(SourceFavorites.MaxFavorites, settings.FavoriteSources.Count);
        Assert.True(settings.SetFavorite(@"C:\Apps\Player0.exe", "Renamed", true));
        Assert.True(settings.SetFavorite(@"C:\Apps\Player0.exe", "Renamed", false));
        Assert.True(settings.SetFavorite(PlayerKey, "Player", true));
        settings.Validate();
    }

    [Fact]
    public void PinningAndUnpinningNeverChangesTheMasterRateOrControllerPolicy()
    {
        var settings = new SourceSettings
        {
            TargetEndpointId = "dx5",
            StreamRateDetectionEnabled = false,
            CatalogRateDetectionEnabled = true
        };
        settings.SelectMaster(PlayerKey, "Player");
        settings.ManualSampleRatesHz[PlayerKey] = 96000;
        settings.RememberAppliedRate(PlayerKey, 96000);
        var source = new SourceSnapshot(PlayerKey, "Player", PlayerKey, [42], true, "session-before");
        var before = SampleRatePolicy.Evaluate(settings, [source]);
        var revision = settings.SelectionRevision;

        Assert.True(settings.SetFavorite(@"C:\Apps\Different.exe", "Different", true));
        Assert.True(settings.SetFavorite(PlayerKey, "Pinned Player", true));
        Assert.True(settings.SetFavorite(PlayerKey, "Pinned Player", false));

        Assert.Equal(PlayerKey, settings.SelectedSourceKey);
        Assert.Equal("Player", settings.SelectedSourceDisplayName);
        Assert.Equal(revision, settings.SelectionRevision);
        Assert.Equal(PlayerKey, settings.LastKnownSourceKey);
        Assert.Equal(96000, settings.LastKnownSampleRateHz);
        Assert.Equal(96000, Assert.Single(settings.ManualSampleRatesHz).Value);
        Assert.Equal("dx5", settings.TargetEndpointId);
        Assert.True(settings.SwitchingEnabled);
        Assert.False(settings.StreamRateDetectionEnabled);
        Assert.True(settings.CatalogRateDetectionEnabled);
        Assert.Equal(before, SampleRatePolicy.Evaluate(settings, [source]));
    }

    [Fact]
    public void PinningDoesNotSelectAMasterWhenThereIsNone()
    {
        var settings = new SourceSettings();
        settings.SetFavorite(PlayerKey, "Player", true);
        Assert.Null(settings.SelectedSourceKey);
        Assert.Null(settings.SelectedSourceDisplayName);
        Assert.Equal(0, settings.SelectionRevision);
        Assert.Null(settings.LastKnownSampleRateHz);
        Assert.Equal(RateDecisionReason.NoMasterSelected, SampleRatePolicy.Evaluate(settings, []).Reason);
    }

    [Fact]
    public async Task SchemaOneWithoutFavoritesLoadsBackwardsCompatibleDefaults()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, "{\"SchemaVersion\":1,\"SwitchingEnabled\":false}");
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.Null(result.Warning);
        Assert.Empty(result.Settings.FavoriteSources);
        Assert.False(result.Settings.SwitchingEnabled);
        Assert.Equal(1, result.Settings.SchemaVersion);
    }

    [Fact]
    public async Task FavoriteSurvivesAppAndControllerRestartsWithCaseInsensitiveLookup()
    {
        var before = new SourceSnapshot(PlayerKey, "Player", PlayerKey, [123], true, "old-session");
        var settings = new SourceSettings();
        settings.SetFavorite(before.Key, before.ProcessName, true);
        await new SettingsStore(FilePath).SaveAsync(settings);

        var restartedSource = new SourceSnapshot(PlayerKey.ToUpperInvariant(), "Player", PlayerKey, [456, 789], false, "new-session");
        var restored = await new SettingsStore(FilePath).LoadAsync();
        Assert.Null(restored.Warning);
        Assert.True(restored.Settings.IsFavorite(restartedSource.Key));
        Assert.Equal("Player", restored.Settings.FavoriteSources[PlayerKey.ToLowerInvariant()]);
        Assert.Same(StringComparer.OrdinalIgnoreCase, restored.Settings.FavoriteSources.Comparer);
        Assert.Null(restored.Settings.SelectedSourceKey);
    }

    [Theory]
    [InlineData("{\"FavoriteSources\":null}")]
    [InlineData("{\"FavoriteSources\":{\"pid:1234\":\"Player\"}}")]
    [InlineData("{\"FavoriteSources\":{\"C:\\\\Apps\\\\Player.exe\":\"\"}}")]
    [InlineData("{\"FavoriteSources\":{\"C:\\\\Apps\\\\Player.exe\":null}}")]
    [InlineData("{\"FavoriteSources\":{\"C:\\\\Apps\\\\Player.exe\":\"Player\",\"c:\\\\apps\\\\player.exe\":\"Duplicate\"}}")]
    [InlineData("{\"FavoriteSources\":{\"C:/Apps/Player.exe\":\"Player\",\"c:/apps/player.exe\":\"Duplicate\"}}")]
    public async Task MalformedFavoritesLoadDefaultsAndPreserveExactOriginal(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, json);
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.Empty(result.Settings.FavoriteSources);
        Assert.NotNull(result.RecoveryBackupPath);
        Assert.Equal(json, await File.ReadAllTextAsync(result.RecoveryBackupPath!));
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task OversizedFavoritesAreRejectedWithCorruptRecovery()
    {
        var settings = new SourceSettings();
        for (var index = 0; index <= SourceFavorites.MaxFavorites; index++)
            settings.FavoriteSources[$@"C:\Apps\Player{index}.exe"] = $"Player {index}";
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings);
        await File.WriteAllTextAsync(FilePath, json);
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.Empty(result.Settings.FavoriteSources);
        Assert.Equal(json, await File.ReadAllTextAsync(result.RecoveryBackupPath!));
    }

    [Fact]
    public async Task InvalidFavoriteSaveCannotReplaceExistingValidSettings()
    {
        var settings = new SourceSettings();
        settings.SetFavorite(PlayerKey, "Player", true);
        var store = new SettingsStore(FilePath);
        await store.SaveAsync(settings);
        var original = await File.ReadAllTextAsync(FilePath);
        settings.FavoriteSources["pid:999"] = "Transient";
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task SavedShareXFavoriteCanBeLoadedAndRemovedWithoutTouchingMaster()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(PlayerKey, "Player");
        settings.RememberAppliedRate(PlayerKey, 48000);
        settings.FavoriteSources[@"C:\Apps\ShareX.exe"] = "ShareX";
        settings.SetFavorite(PlayerKey, "Player", true);
        await new SettingsStore(FilePath).SaveAsync(settings);

        var loaded = (await new SettingsStore(FilePath).LoadAsync()).Settings;
        var revision = loaded.SelectionRevision;
        Assert.True(SourceExclusions.ApplyToSettings(loaded));
        Assert.Single(loaded.FavoriteSources);
        Assert.True(loaded.IsFavorite(PlayerKey));
        Assert.Equal(PlayerKey, loaded.SelectedSourceKey);
        Assert.Equal(revision, loaded.SelectionRevision);
        Assert.Equal(48000, loaded.LastKnownSampleRateHz);
        Assert.False(SourceExclusions.ApplyToSettings(loaded));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
