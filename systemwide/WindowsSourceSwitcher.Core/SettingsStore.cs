using System.Text.Json;

namespace WindowsSourceSwitcher.Core;

public sealed record SettingsLoadResult(SourceSettings Settings, string? Warning, string? RecoveryBackupPath = null)
{
    public bool IsNew { get; init; }
}

/// <summary>Validated JSON settings with same-directory atomic replacement and corrupt-file recovery.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
                return new(new SourceSettings { TutorialPending = true }, null) { IsNew = true };

            try
            {
                var settings = Parse(await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false));
                return new(settings, null);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            {
                // Preserve the exact malformed bytes before a later save can replace the original.
                var backup = FilePath + ".corrupt-" + Guid.NewGuid().ToString("N") + ".json";
                File.Copy(FilePath, backup, overwrite: false);
                return new(new SourceSettings(), $"Settings could not be read ({error.Message}). Defaults loaded; original preserved at {backup}.", backup);
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(SourceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        // Serialize before the first await so callers may resume editing their in-memory settings.
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(FilePath))
            {
                // Preserve malformed existing files even when Save is called before Load.
                string? backup = null;
                try { Parse(await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false)); }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
                { backup = FilePath + ".corrupt-" + Guid.NewGuid().ToString("N") + ".json"; }
                File.Replace(temporary, FilePath, backup);
            }
            else
            {
                File.Move(temporary, FilePath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            gate.Release();
        }
    }

    private static SourceSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<SourceSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("Settings file contains null.");
        settings.Validate();
        // JsonSerializer constructs dictionaries with its own comparer; restore Windows identity semantics.
        settings.ManualSampleRatesHz = new Dictionary<string, int>(settings.ManualSampleRatesHz, StringComparer.OrdinalIgnoreCase);
        settings.ManualBitDepths = new Dictionary<string, int>(settings.ManualBitDepths, StringComparer.OrdinalIgnoreCase);
        settings.FavoriteSources = settings.FavoriteSources.ToDictionary(
            entry => SourceIdentity.FromExecutablePath(entry.Key), entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        settings.PinnedSources = settings.PinnedSources.ToDictionary(
            entry => SourceIdentity.FromExecutablePath(entry.Key), entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        return settings;
    }
}
