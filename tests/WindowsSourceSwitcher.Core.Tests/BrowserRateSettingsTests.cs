using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class BrowserRateSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DX5BrowserSettings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task MissingAndLegacySettingsDoNotOptIntoBrowserIntegration()
    {
        Assert.False(new SourceSettings().BrowserRateDetectionEnabled);
        var store = new SettingsStore(FilePath);
        var missing = await store.LoadAsync();
        Assert.Null(missing.Warning);
        Assert.False(missing.Settings.BrowserRateDetectionEnabled);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{\"SchemaVersion\":1,\"StreamRateDetectionEnabled\":true}");
        var legacy = await store.LoadAsync();
        Assert.Null(legacy.Warning);
        Assert.False(legacy.Settings.BrowserRateDetectionEnabled);
        Assert.True(legacy.Settings.StreamRateDetectionEnabled);
    }

    [Fact]
    public async Task BrowserOptInRoundTripsWithoutChangingMasterEndpointOverrideFavoritesOrHeldRate()
    {
        const string browser = @"C:\Program Files\Zen Browser\zen.exe";
        var settings = new SourceSettings { TargetEndpointId = "topping-endpoint" };
        settings.SelectMaster(browser, "Zen");
        settings.RememberAppliedRate(browser, 96000);
        settings.ManualSampleRatesHz[browser] = 88200;
        settings.SetFavorite(browser, "Zen", true);
        var revision = settings.SelectionRevision;
        var store = new SettingsStore(FilePath);
        foreach (var enabled in new[] { true, false })
        {
            settings.BrowserRateDetectionEnabled = enabled;
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.Null(loaded.Warning);
            Assert.Equal(enabled, loaded.Settings.BrowserRateDetectionEnabled);
            Assert.Equal(browser, loaded.Settings.SelectedSourceKey);
            Assert.Equal(revision, loaded.Settings.SelectionRevision);
            Assert.Equal("topping-endpoint", loaded.Settings.TargetEndpointId);
            Assert.Equal(96000, loaded.Settings.LastKnownSampleRateHz);
            Assert.Equal(88200, loaded.Settings.ManualSampleRatesHz[browser]);
            Assert.True(loaded.Settings.IsFavorite(browser));
        }
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("null")]
    [InlineData("1")]
    public async Task MalformedBrowserOptInDoesNotEnableIntegrationAndOriginalBytesArePreserved(string value)
    {
        Directory.CreateDirectory(_directory);
        var original = "{\"BrowserRateDetectionEnabled\":" + value + "}";
        await File.WriteAllTextAsync(FilePath, original);
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.False(result.Settings.BrowserRateDetectionEnabled);
        Assert.NotNull(result.RecoveryBackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(result.RecoveryBackupPath!));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
