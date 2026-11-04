using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class FullscreenAutoSwitchSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FavoriteAutoSwitchSettings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");

    [Fact]
    public void FullscreenPriorityIsOptInByDefault() => Assert.False(new SourceSettings().FullscreenAutoSwitchEnabled);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoicePersistsInSchemaOneWithoutChangingMasterRevisionOrRate(bool enabled)
    {
        var settings = new SourceSettings { FullscreenAutoSwitchEnabled = enabled };
        settings.SelectMaster(@"C:\Apps\Player.exe", "Player");
        settings.SetFavorite(settings.SelectedSourceKey!, "Player", true);
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 96000);
        var store = new SettingsStore(FilePath);
        await store.SaveAsync(settings);
        var result = await store.LoadAsync();
        Assert.Null(result.Warning);
        Assert.Equal(1, result.Settings.SchemaVersion);
        Assert.Equal(enabled, result.Settings.FullscreenAutoSwitchEnabled);
        Assert.Equal(settings.SelectedSourceKey, result.Settings.SelectedSourceKey);
        Assert.Equal(settings.SelectionRevision, result.Settings.SelectionRevision);
        Assert.Equal(96000, result.Settings.LastKnownSampleRateHz);
        Assert.True(result.Settings.IsFavorite(settings.SelectedSourceKey!));
    }

    [Fact]
    public async Task ExistingSchemaOneSettingsDoNotEnableAutomaticSelection()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, "{\"SchemaVersion\":1,\"SwitchingEnabled\":true}");
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.Null(result.Warning);
        Assert.False(result.Settings.FullscreenAutoSwitchEnabled);
        Assert.True(result.Settings.SwitchingEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
