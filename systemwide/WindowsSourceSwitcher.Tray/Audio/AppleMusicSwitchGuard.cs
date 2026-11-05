using System.Diagnostics;
using Windows.Media.Control;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal sealed record AppleSwitchState(string TrackKey, MediaTransportPlaybackStatus Status, TimeSpan? Position = null);
internal interface IAppleSwitchSession
{
    Task<AppleSwitchState?> ReadAsync(CancellationToken cancellationToken);
    Task<bool> TryReseekAsync(AppleSwitchState state, Func<bool> contextCurrent, CancellationToken cancellationToken) => Task.FromResult(false);
}
internal sealed record AppleSwitchResult(ApplyResult? Applied, string Detail, bool PlaybackObserved, bool RetryOnActivity,
    bool WaitingForSignal = false, ApplyResult? Restoration = null);

/// <summary>Write while Apple is rendering, or recover a proven silent stream when
/// a higher output depth is available. Never write to an unobservable starting stream.</summary>
internal sealed class AppleMusicSwitchGuard(
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<TimeSpan>? elapsed = null)
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan HealthyConfirmation = TimeSpan.FromMilliseconds(120);
    internal static readonly TimeSpan TimelineConfirmation = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan RebuildTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReseekDelay = TimeSpan.FromMilliseconds(900);
    internal const string WaitingForSignalStatus = "Waiting for Apple Music to send audio. Reopen the player if it stays silent.";
    public async Task<AppleSwitchResult> RunAsync(IAppleSwitchSession? session, Func<bool> contextCurrent,
        Func<bool?> renderActive, Func<float?> signalPeak, Func<ApplyResult?> apply, CancellationToken cancellationToken,
        Action? formatConfirmed = null, bool allowSilentDepthIncrease = false, Func<ApplyResult?>? restorePrevious = null,
        Func<ApplyResult?>? recoverSilent = null)
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
            var silentThroughout = true;
            var recoveringSilence = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!contextCurrent() || state?.TrackKey != original.TrackKey || state.Status != MediaTransportPlaybackStatus.Playing)
                    return new(null, "Apple Music track/session, playback or master changed; output held.", false, true);
                var peak = signalPeak();
                var active = renderActive();
                silentThroughout &= active == true && peak == 0;
                if (HasLiveSignal(active, peak)) healthySince ??= Now();
                else healthySince = null;
                if (healthySince is { } healthy && Now() - healthy >= HealthyConfirmation) break;
                if (Now() - since >= StartupTimeout)
                {
                    if ((!allowSilentDepthIncrease && recoverSilent is null) || !silentThroughout || !TimelineAdvanced(original.Position, state.Position))
                        return new(null, WaitingForSignalStatus, false, true, WaitingForSignal: true);
                    recoveringSilence = true;
                    break;
                }
                await Wait(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (contextCurrent()) applied = recoveringSilence && recoverSilent is not null ? recoverSilent() : apply();
            if (applied is null) return new(null, "Apple Music evidence changed before the write; output held.", false, true);
            if (!applied.Success) return new(applied, "Apple Music output format change failed.", false, false);
            if (!applied.Changed && !recoveringSilence)
                return new(applied, "Apple Music is already rendering at the current output format.", true, false);
            // Endpoint readback and player recovery are different milestones. The
            // host can publish the actual output now without claiming audio resumed.
            var afterWrite = Now();
            formatConfirmed?.Invoke();
            var position = (await session.ReadAsync(cancellationToken).ConfigureAwait(false))?.Position;
            bool? lastActive = null;
            float? lastPeak = null;
            TimeSpan? lastPosition = null;
            TimeSpan? firstSignal = null;
            var reseeked = false;
            var reseekAccepted = false;
            TimeSpan? silentSince = null;
            while (Now() - afterWrite <= RebuildTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!contextCurrent() || state?.TrackKey != original.TrackKey || state.Status != MediaTransportPlaybackStatus.Playing)
                    return new(applied, "Apple Music confirmation stopped after a track, pause or master change.", false, false);
                var peak = signalPeak();
                lastActive = renderActive(); lastPeak = peak; lastPosition = state.Position;
                silentSince = lastActive == true && peak == 0 ? silentSince ?? Now() : null;
                if (HasLiveSignal(lastActive, peak))
                {
                    firstSignal ??= Now() - afterWrite;
                    if (TimelineAdvanced(position, state.Position))
                        return new(applied, $"Apple-associated signal returned after {firstSignal.Value.TotalMilliseconds:0} ms; signal and timeline confirmed {(Now() - afterWrite).TotalMilliseconds:0} ms after endpoint readback."
                            + (reseekAccepted ? " Apple Music reloaded the current playback position." : "")
                            + " Physical audibility is unverified.", true, false);
                }
                if (applied.Changed && recoveringSilence && !reseeked && firstSignal is null
                    && silentSince is { } silent && Now() - silent >= ReseekDelay
                    && TimelineAdvanced(position, state.Position) && contextCurrent())
                {
                    reseeked = true;
                    reseekAccepted = await session.TryReseekAsync(state, contextCurrent, cancellationToken).ConfigureAwait(false);
                    if (reseekAccepted) position = state.Position;
                }
                await Wait(cancellationToken).ConfigureAwait(false);
            }
            if (applied.Changed && firstSignal is null && (lastActive == false || lastActive == true && lastPeak == 0)
                && restorePrevious is not null && contextCurrent())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (restorePrevious() is { } restored)
                {
                    if (restored.Success) formatConfirmed?.Invoke();
                    var detail = restored.Success
                        ? "Apple Music stayed silent at the lower depth. Restored the previous output format."
                        : "Apple Music stayed silent and Windows could not restore the previous output format: " + restored.Detail;
                    return new(applied, detail, false, false, Restoration: restored);
                }
            }
            return new(applied, $"Output format confirmed, but Apple Music audio/timeline recovery is unconfirmed (active={lastActive}, peak={lastPeak:G4}, position={position}->{lastPosition})."
                + (reseeked ? " The player was asked once to reload its playback position." : "")
                + " Try restarting the current track.", false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(applied, "Apple Music switch/confirmation cancelled.", false, applied is null); }
        catch (Exception error)
        { return new(applied, "Apple Music switch/confirmation unavailable: " + error.Message, false, applied is null); }
    }

    internal static bool HasLiveSignal(bool? active, float? peak) =>
        active == true && peak is > 0.001f && float.IsFinite(peak.Value);

    private static bool TimelineAdvanced(TimeSpan? start, TimeSpan? current) =>
        start is { } from && current is { } to && to - from >= TimelineConfirmation;
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

    public async Task<bool> TryReseekAsync(AppleSwitchState state, Func<bool> contextCurrent, CancellationToken cancellationToken)
    {
        var latest = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.TrackKey != state.TrackKey || latest.Status != MediaTransportPlaybackStatus.Playing
            || latest.Position is not { } position || position <= TimeSpan.Zero
            || !session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled) return false;
        cancellationToken.ThrowIfCancellationRequested();
        if (!contextCurrent() || !IsCurrent() || readRevision != Volatile.Read(ref revision)) return false;
        return await session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(cancellationToken).ConfigureAwait(false);
    }
}
