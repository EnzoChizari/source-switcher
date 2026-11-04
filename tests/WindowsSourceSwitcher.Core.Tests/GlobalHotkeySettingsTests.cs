using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class GlobalHotkeySettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "GlobalHotkeySettings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "settings.json");

    [Fact]
    public void GlobalHotkeysAreOptInByDefault() => Assert.False(new SourceSettings().GlobalHotkeysEnabled);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalHotkeyChoicePersistsWithoutChangingMasterRevisionOrRate(bool enabled)
    {
        var settings = new SourceSettings { GlobalHotkeysEnabled = enabled };
        settings.SelectMaster(@"C:\Apps\Player.exe", "Player");
        settings.RememberAppliedRate(settings.SelectedSourceKey!, 96000);
        var store = new SettingsStore(FilePath);
        await store.SaveAsync(settings);
        var result = await store.LoadAsync();
        Assert.Null(result.Warning);
        Assert.Equal(enabled, result.Settings.GlobalHotkeysEnabled);
        Assert.Equal(settings.SelectedSourceKey, result.Settings.SelectedSourceKey);
        Assert.Equal(settings.SelectionRevision, result.Settings.SelectionRevision);
        Assert.Equal(96000, result.Settings.LastKnownSampleRateHz);
    }

    [Fact]
    public async Task OlderSchemaOneSettingsNeverRegisterShortcutsImplicitly()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(FilePath, "{\"SchemaVersion\":1,\"SwitchingEnabled\":true}");
        var result = await new SettingsStore(FilePath).LoadAsync();
        Assert.Null(result.Warning);
        Assert.False(result.Settings.GlobalHotkeysEnabled);
        Assert.True(result.Settings.SwitchingEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
