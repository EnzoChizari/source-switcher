using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using WindowsSourceSwitcher.BrowserBridge;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Opt-in metadata receiver. The bridge never changes audio, launches apps or trusts JSON process identities.</summary>
internal sealed class BrowserRateBridge : IAsyncDisposable
{
    private readonly string _expectedHostPath;
    private readonly Func<NamedPipeServerStream> _createServer;
    private readonly Func<SafePipeHandle, string, BrowserPeerIdentity> _validateClient;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private CancellationTokenSource? _stop;
    private NamedPipeServerStream? _activePipe;
    private Task _worker = Task.CompletedTask;
    private BrowserDecoderSnapshot _snapshot = BrowserDecoderSnapshot.Empty;
    private bool _enabled, _disposed;
    private long _generation, _version;

    public BrowserRateBridge(string expectedHostPath) : this(expectedHostPath,
        () => WindowsBrowserPeerIdentity.CreateServer(WindowsBrowserPeerIdentity.CurrentPipeName),
        WindowsBrowserPeerIdentity.ValidateClient) { }

    internal BrowserRateBridge(string expectedHostPath, Func<NamedPipeServerStream> createServer,
        Func<SafePipeHandle, string, BrowserPeerIdentity> validateClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHostPath);
        _expectedHostPath = Path.GetFullPath(expectedHostPath);
        _createServer = createServer ?? throw new ArgumentNullException(nameof(createServer));
        _validateClient = validateClient ?? throw new ArgumentNullException(nameof(validateClient));
    }

    /// <summary>Raised on a receiver worker; handlers should enqueue work and return promptly.</summary>
    public event Action? Changed;

    public BrowserDecoderSnapshot Snapshot
    {
        get
        {
            BrowserDecoderSnapshot snapshot;
            var changed = false;
            lock (_sync)
            {
                if (_snapshot.State == BrowserDecoderState.Available
                    && System.Diagnostics.Stopwatch.GetElapsedTime(_snapshot.LastReceivedTimestamp) >= BrowserEvidenceGuard.MaximumEvidenceAge)
                {
                    _snapshot = _snapshot with { Version = ++_version, DecoderRateHz = null, OutputRateHz = null,
                        State = BrowserDecoderState.TimedOut, Detail = "Tab decoder evidence expired; holding the confirmed target." };
                    changed = true;
                }
                snapshot = _snapshot;
            }
            if (changed) NotifyChanged();
            return snapshot;
        }
    }

    public bool IsEvidenceCurrent(long version)
    {
        var snapshot = Snapshot;
        lock (_sync) return _enabled && !_disposed && snapshot.Version == version
            && _snapshot.Version == version && snapshot.State == BrowserDecoderState.Available;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_sync)
            {
                if (_enabled) return;
                _enabled = true;
                _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiverToken = _stop.Token;
                var generation = ++_generation;
                _snapshot = BrowserDecoderSnapshot.Empty with { Version = ++_version, State = BrowserDecoderState.WaitingForHost,
                    Detail = "Waiting for the installed Firefox/Zen or Helium extension. Choose Follow this tab in its popup." };
                _worker = Task.Run(() => RunAsync(generation, receiverToken));
            }
        }
        finally { _lifetime.Release(); }
        NotifyChanged();
    }

    public async Task StopAsync()
    {
        await _lifetime.WaitAsync().ConfigureAwait(false);
        try
        {
            Task worker;
            lock (_sync)
            {
                _enabled = false;
                ++_generation;
                _stop?.Cancel();
                _activePipe?.Dispose();
                _activePipe = null;
                worker = _worker;
                _snapshot = BrowserDecoderSnapshot.Empty with { Version = ++_version };
            }
            try { await worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
            _stop?.Dispose();
            _stop = null;
        }
        finally { _lifetime.Release(); }
        NotifyChanged();
    }

    private async Task RunAsync(long generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var authenticated = false;
            try
            {
                pipe = _createServer();
                lock (_sync)
                {
                    if (!_enabled || generation != _generation) return;
                    _activePipe = pipe;
                }
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var peer = _validateClient(pipe.SafePipeHandle, _expectedHostPath);
                authenticated = true;
                var connectionNonce = Guid.NewGuid().ToString("N");
                var guard = new BrowserEvidenceGuard(connectionNonce, peer.BrowserExecutablePath,
                    peer.BrowserProcessId, peer.BrowserStartedAtUtc);
                Publish(generation, guard.Snapshot, preservePinnedContext: true);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var request = new BrowserProbeRequest(connectionNonce, Guid.NewGuid().ToString("N"));
                    guard.BeginRequest(request.RequestId);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(BrowserEvidenceGuard.MaximumEvidenceAge);
                    await BrowserWireProtocol.WriteFrameAsync(pipe, BrowserWireProtocol.CreateRequest(request), deadline.Token).ConfigureAwait(false);
                    var frame = await BrowserWireProtocol.ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Browser host disconnected.");
                    var message = BrowserWireProtocol.ParseResponse(frame);
                    var observation = guard.Accept(message);
                    Publish(generation, observation);
                    await Task.Delay(observation.IsTabPinned ? 250 : 1000, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                PublishFailure(generation, error is OperationCanceledException ? BrowserDecoderState.TimedOut
                    : authenticated && error is IOException
                        ? BrowserDecoderState.Disconnected : BrowserDecoderState.Unavailable,
                    "Browser decoder bridge unavailable: " + error.Message + " Holding the confirmed tab target.");
                try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                lock (_sync) { if (ReferenceEquals(_activePipe, pipe)) _activePipe = null; }
                pipe?.Dispose();
            }
        }
    }

    private void Publish(long generation, BrowserDecoderSnapshot snapshot, bool preservePinnedContext = false)
    {
        bool changed;
        lock (_sync)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            if (preservePinnedContext && _snapshot.IsTabPinned)
                snapshot = _snapshot with { DecoderRateHz = null, OutputRateHz = null,
                    State = snapshot.State, Detail = snapshot.Detail };
            changed = !_snapshot.HasSameEvidenceAs(snapshot);
            _snapshot = snapshot with { Version = changed ? ++_version : _version };
        }
        if (changed) NotifyChanged();
    }

    private void PublishFailure(long generation, BrowserDecoderState state, string detail)
    {
        bool changed;
        lock (_sync)
        {
            if (!_enabled || _disposed || generation != _generation) return;
            var next = _snapshot with { State = state, Detail = detail,
                DecoderRateHz = null, OutputRateHz = null };
            changed = !_snapshot.HasSameEvidenceAs(next);
            _snapshot = next with { Version = changed ? ++_version : _version };
        }
        if (changed) NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { /* UI shutdown or a diagnostic consumer cannot stop the receiver. */ }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
    }
}
