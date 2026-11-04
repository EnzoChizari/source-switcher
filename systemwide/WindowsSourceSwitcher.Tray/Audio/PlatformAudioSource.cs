using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>
/// A group of non-expired sessions on the selected endpoint. The executable path is the
/// restart-stable identity. A protected or exited process has a temporary, unpersistable identity.
/// </summary>
public sealed record PlatformAudioSource(
    string Identity,
    string ProcessName,
    string? ExecutablePath,
    IReadOnlyList<int> ProcessIds,
    bool IsActive,
    bool CanPersist,
    string SessionToken)
{
    /// <summary>Exact live session instances, used to bind service rundown evidence.</summary>
    public IReadOnlyList<string> SessionInstanceIds { get; init; } = [];
    public IReadOnlyDictionary<int, string> ProcessExecutablePaths { get; init; } = new Dictionary<int, string>();

    /// <summary>
    /// Catalog lookup is an optional estimate, not a measurement of the source stream.
    /// Require the official package path as well as a known executable name, since the
    /// GSMTC Apple Music session cannot safely be associated with an arbitrary renamed app.
    /// </summary>
    public bool SupportsAutomaticRate => IsOfficialAppleMusicExecutable(ExecutablePath);

    public SourceSnapshot ToSnapshot() => new(
        Identity, ProcessName, ExecutablePath ?? string.Empty, ProcessIds, IsActive, SessionToken);

    internal static bool IsOfficialAppleMusicExecutable(string? executablePath) => AppleMusicIdentity.IsOfficialExecutable(executablePath);
}
