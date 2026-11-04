using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WindowsSourceSwitcherTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");

    [Fact]
    public async Task MissingSettingsLoadSafeDefaults()
    {
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.True(result.IsNew);
        Assert.True(result.Settings.TutorialPending);
        Assert.Null(result.Warning);
        Assert.Null(result.Settings.SelectedSourceKey);
        Assert.True(result.Settings.SwitchingEnabled);
        Assert.False(result.Settings.CatalogRateDetectionEnabled);
    }

    [Fact]
    public async Task SettingsRoundTripKeepsMasterOverridesEndpointAndHeldRate()
    {
        var settings = Selected();
        settings.TargetEndpointId = "topping-dx5-ii-endpoint";
        settings.ManualSampleRatesHz[settings.SelectedSourceKey!] = 88200;
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 88200);
        settings.SwitchingEnabled = false;
        settings.CatalogRateDetectionEnabled = true;
        var store = new SettingsStore(FilePath);
        await store.SaveAsync(settings);
        var result = await store.LoadAsync();

        Assert.False(result.IsNew);
        Assert.False(result.Settings.TutorialPending);
        Assert.Null(result.Warning);
        Assert.Equal(settings.SelectedSourceKey, result.Settings.SelectedSourceKey);
        Assert.Equal(settings.TargetEndpointId, result.Settings.TargetEndpointId);
        Assert.Equal(settings.SelectionRevision, result.Settings.SelectionRevision);
        Assert.Equal(88200, result.Settings.LastKnownSampleRateHz);
        Assert.Equal(88200, result.Settings.ManualSampleRatesHz[settings.SelectedSourceKey!.ToUpperInvariant()]);
        Assert.False(result.Settings.SwitchingEnabled);
        Assert.True(result.Settings.CatalogRateDetectionEnabled);
    }

    [Theory]
    [InlineData("{invalid json")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":42}")]
    [InlineData("{\"ManualSampleRatesHz\":null}")]
    [InlineData("{\"SelectedSourceKey\":\"app\",\"SelectedSourceDisplayName\":\"Player\",\"LastKnownSourceKey\":\"other\",\"LastKnownSampleRateHz\":44100}")]
    public async Task InvalidFileLoadsDefaultsAndPreservesExactOriginal(string invalidJson)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, invalidJson);
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.Null(result.Settings.SelectedSourceKey);
        Assert.NotNull(result.RecoveryBackupPath);
        Assert.False(result.IsNew);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(result.RecoveryBackupPath!));
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task ReplacingExistingSettingsLeavesCompleteNewJsonAndNoTemporaryFiles()
    {
        var store = new SettingsStore(FilePath);
        var settings = Selected();
        await store.SaveAsync(settings);
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 192000);
        await store.SaveAsync(settings);
        Assert.Equal(192000, (await store.LoadAsync()).Settings.LastKnownSampleRateHz);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveBeforeLoadPreservesAnExistingCorruptFile()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, "original broken bytes");
        var store = new SettingsStore(FilePath);
        await store.SaveAsync(Selected());
        var backup = Assert.Single(Directory.EnumerateFiles(directory, "*.corrupt-*.json"));
        Assert.Equal("original broken bytes", await File.ReadAllTextAsync(backup));
        Assert.Null((await store.LoadAsync()).Warning);
    }

    [Fact]
    public async Task InvalidWritesAreRejectedWithoutChangingTheValidFile()
    {
        var store = new SettingsStore(FilePath);
        var settings = Selected();
        await store.SaveAsync(settings);
        var original = await File.ReadAllTextAsync(FilePath);
        settings.ManualSampleRatesHz[settings.SelectedSourceKey!] = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task CancellationBeforeSaveLeavesExistingFileUntouched()
    {
        var store = new SettingsStore(FilePath);
        var settings = Selected();
        await store.SaveAsync(settings);
        var original = await File.ReadAllTextAsync(FilePath);
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 192000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(settings, cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task ConcurrentSavesAreSerializedAndLeaveOneCompleteValidatedSnapshot()
    {
        var store = new SettingsStore(FilePath);
        int[] rates = [44100, 48000, 88200, 96000, 176400, 192000];
        await Task.WhenAll(rates.Select(rate =>
        {
            var settings = Selected();
            settings.RememberAppliedRate(settings.SelectedSourceKey!, rate);
            return store.SaveAsync(settings);
        }));

        var result = await store.LoadAsync();
        Assert.Null(result.Warning);
        Assert.Contains(result.Settings.LastKnownSampleRateHz!.Value, rates);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        result.Settings.Validate();
    }

    [Fact]
    public async Task CaseDuplicateManualKeysAreRejectedInsteadOfWritingAmbiguousSettings()
    {
        var settings = Selected();
        settings.ManualSampleRatesHz = new(StringComparer.Ordinal)
        {
            [@"C:\Apps\Player.exe"] = 44100,
            [@"C:\APPS\PLAYER.EXE"] = 48000
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(FilePath).SaveAsync(settings));
        Assert.False(File.Exists(FilePath));
    }

    private static SourceSettings Selected()
    {
        var settings = new SourceSettings();
        settings.SelectMaster(@"C:\Apps\Player.exe", "Player");
        return settings;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
