using System.Diagnostics;
using Windows.Media.Control;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal sealed record AppleSwitchState(string TrackKey, MediaTransportPlaybackStatus Status, TimeSpan? Position = null);
internal interface IAppleSwitchSession
{
    Task<AppleSwitchState?> ReadAsync(CancellationToken cancellationToken);
}
internal sealed record AppleSwitchResult(ApplyResult? Applied, string Detail, bool PlaybackObserved, bool RetryOnActivity);

/// <summary>Only invalidate a genuinely rendering Apple stream. Dormant/starting streams
/// can be permanently wedged by a format write. No transport commands are issued.</summary>
internal sealed class AppleMusicSwitchGuard(
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<TimeSpan>? elapsed = null)
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan HealthyConfirmation = TimeSpan.FromMilliseconds(120);
    internal static readonly TimeSpan TimelineConfirmation = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan RebuildTimeout = TimeSpan.FromSeconds(6);
    public async Task<AppleSwitchResult> RunAsync(IAppleSwitchSession? session, Func<bool> contextCurrent,
        Func<bool?> renderActive, Func<float?> signalPeak, Func<ApplyResult?> apply, CancellationToken cancellationToken,
        Action? formatConfirmed = null)
    {
        if (session is null) return new(null, "Apple Music session unavailable; output held.", false, true);
        var watch = Stopwatch.StartNew();
        TimeSpan Now() => elapsed?.Invoke() ?? watch.Elapsed;
        Task Wait(CancellationToken token) => delay?.Invoke(PollInterval, token) ?? Task.Delay(PollInterval, token);
        ApplyResult? applied = null;
        try
        {
            var original = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!contextCurrent() || original is null || string.IsNullOrWhiteSpace(original.TrackKey)
                || original.Status != MediaTransportPlaybackStatus.Playing)
                return new(null, "Apple Music is paused or its track is unconfirmed; output held until playback.", false, true);
            var since = Now();
            TimeSpan? healthySince = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!contextCurrent() || state?.TrackKey != original.TrackKey || state.Status != MediaTransportPlaybackStatus.Playing)
                    return new(null, "Apple Music track/session, playback or master changed; output held.", false, true);
                var peak = signalPeak();
                if (renderActive() == true && peak is > 0.001f && float.IsFinite(peak.Value)) healthySince ??= Now();
                else healthySince = null;
                if (healthySince is { } healthy && Now() - healthy >= HealthyConfirmation) break;
                if (Now() - since >= StartupTimeout)
                    return new(null, "Apple Music is not yet rendering confirmed audio; holding output to avoid a stalled player.", false, true);
                await Wait(cancellationToken).ConfigureAwait(false);
            }
            // A live stream is invalidated while actively pumping, so Apple can detect it
            // and rebuild. Pausing before this write can instead leave a dormant zombie.
            cancellationToken.ThrowIfCancellationRequested();
            if (contextCurrent()) applied = apply();
            if (applied is null) return new(null, "Apple Music evidence changed before the write; output held.", false, true);
            if (!applied.Success) return new(applied, "Apple Music output format change failed.", false, false);
            // Endpoint readback and player recovery are different milestones. The
            // host can publish the actual output now without claiming audio resumed.
            var afterWrite = Now();
            formatConfirmed?.Invoke();
            var position = (await session.ReadAsync(cancellationToken).ConfigureAwait(false))?.Position;
            bool? lastActive = null;
            float? lastPeak = null;
            TimeSpan? lastPosition = null;
            TimeSpan? firstSignal = null;
            while (Now() - afterWrite <= RebuildTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!contextCurrent() || state?.TrackKey != original.TrackKey || state.Status != MediaTransportPlaybackStatus.Playing)
                    return new(applied, "Apple Music confirmation stopped after a track, pause or master change; no transport command sent.", false, false);
                var peak = signalPeak();
                lastActive = renderActive(); lastPeak = peak; lastPosition = state.Position;
                if (lastActive == true && peak is > 0.001f && float.IsFinite(peak.Value))
                {
                    firstSignal ??= Now() - afterWrite;
                    if (position is { } start && state.Position is { } current && current - start >= TimelineConfirmation)
                        return new(applied, $"Apple-associated signal returned after {firstSignal.Value.TotalMilliseconds:0} ms; signal and timeline confirmed {(Now() - afterWrite).TotalMilliseconds:0} ms after endpoint readback. Physical audibility is unverified.", true, false);
                }
                await Wait(cancellationToken).ConfigureAwait(false);
            }
            return new(applied, $"Output format confirmed, but Apple Music audio/timeline recovery is unconfirmed (active={lastActive}, peak={lastPeak:G4}, position={position}->{lastPosition}). Check the player; no restart or transport command was sent.", false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(applied, "Apple Music switch/confirmation cancelled; no transport command sent.", false, applied is null); }
        catch (Exception error)
        { return new(applied, "Apple Music switch/confirmation unavailable: " + error.Message, false, applied is null); }
    }
}

/// <summary>Each transaction pins one GSMTC object. It cannot send commands to another
/// player or to a replacement Apple session. No permanent polling/subscription is added.</summary>
internal sealed class WindowsAppleSwitchSession : IAppleSwitchSession, IDisposable
{
    private readonly GlobalSystemMediaTransportControlsSessionManager manager;
    private readonly GlobalSystemMediaTransportControlsSession session;
    private int revision, readRevision = -1, disposed;
    private string? trackKey;
    private WindowsAppleSwitchSession(GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session)
    {
        this.manager = manager; this.session = session;
        session.MediaPropertiesChanged += PropertiesChanged;
    }
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Interlocked.Increment(ref revision);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) session.MediaPropertiesChanged -= PropertiesChanged;
    }
    public static async Task<IAppleSwitchSession?> OpenAsync(CancellationToken cancellationToken)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        var matches = manager.GetSessions().Where(IsApple).Take(2).ToArray();
        return matches.Length == 1 ? new WindowsAppleSwitchSession(manager, matches[0]) : null;
    }

    private static bool IsApple(GlobalSystemMediaTransportControlsSession value) =>
        value.SourceAppUserModelId.Contains("AppleInc.AppleMusicWin", StringComparison.OrdinalIgnoreCase)
        || value.SourceAppUserModelId.Equals("AppleMusic.exe", StringComparison.OrdinalIgnoreCase);

    private bool IsCurrent() => manager.GetSessions().Where(IsApple).Take(2).ToArray() is { Length: 1 } found
        && ReferenceEquals(found[0], session);

    public async Task<AppleSwitchState?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0 || !IsCurrent()) return null;
        var currentRevision = Volatile.Read(ref revision);
        if (readRevision != currentRevision)
        {
            // Fetch metadata once, then only after a media-properties notification.
            // Repeated async property requests are unnecessary player IPC during rebuild.
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            if (!IsCurrent() || currentRevision != Volatile.Read(ref revision) || properties is null
                || string.IsNullOrWhiteSpace(properties.Title) || string.IsNullOrWhiteSpace(properties.Artist)) return null;
            trackKey = string.Join('\u001f', session.SourceAppUserModelId, properties.Title, properties.Artist, properties.AlbumTitle);
            readRevision = currentRevision;
        }
        var status = session.GetPlaybackInfo().PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaTransportPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaTransportPlaybackStatus.Paused,
            _ => MediaTransportPlaybackStatus.Unknown
        };
        if (currentRevision != Volatile.Read(ref revision)) return null;
        return new(trackKey!, status, session.GetTimelineProperties().Position);
    }

}
