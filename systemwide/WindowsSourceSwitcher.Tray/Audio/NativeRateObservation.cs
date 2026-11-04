namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>
/// A result bound to both a selected executable and its current audio sessions. A non-null
/// value from this first provider is an Apple Music catalog estimate, never VerifiedNative.
/// </summary>
public sealed record NativeRateObservation(
    string SourceIdentity,
    string SessionToken,
    long SelectionRevision,
    long Generation,
    string? TrackKey,
    int? SampleRateHz,
    string Evidence,
    int? BitDepth = null);
