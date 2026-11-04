namespace WindowsSourceSwitcher.Tray;

using WindowsSourceSwitcher.Core;

internal record FullscreenCandidate(nint Window, string SourceKey, string DisplayName);

internal record FullscreenMasterSelection(string? SourceKey, string? DisplayName, string? HeldSourceKey, int? HeldRateHz);

internal sealed class FullscreenMasterPolicy
{
    private enum PendingAction
    {
        None,
        Acquire,
        Restore
    }

    private PendingAction _pendingAction = PendingAction.None;
    private FullscreenCandidate? _pendingCandidate;

    private bool _isOwning;
    private FullscreenCandidate? _ownedCandidate;
    private string? _confirmedSourceKey;
    private long _confirmedRevision;
    private string? _confirmedEndpoint;

    private string? _backupSourceKey;
    private string? _backupDisplayName;
    private string? _backupHeldSourceKey;
    private int? _backupHeldRateHz;

    private nint _suppressedWindow;
    private string? _suppressedSourceKey;

    public bool HasPriority { get; private set; }

    public FullscreenMasterSelection? Update(bool enabled, SourceSettings settings, FullscreenCandidate? candidate)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_pendingAction != PendingAction.None && !_isOwning)
        {
            _backupSourceKey = null;
            _backupDisplayName = null;
            _backupHeldSourceKey = null;
            _backupHeldRateHz = null;
        }

        _pendingAction = PendingAction.None;
        _pendingCandidate = null;

        bool effectiveEnabled = enabled && settings.SwitchingEnabled;

        if (!effectiveEnabled)
        {
            EndSuppression();

            if (_isOwning)
            {
                if (HasManualChange(settings))
                {
                    DropOwnershipAndBackup();
                    return null;
                }

                return PrepareRestore();
            }

            return null;
        }

        if (candidate is null)
        {
            EndSuppression();

            if (_isOwning)
            {
                if (HasManualChange(settings))
                {
                    DropOwnershipAndBackup();
                    return null;
                }

                return PrepareRestore();
            }

            return null;
        }

        if (_isOwning)
        {
            if (HasManualChange(settings))
            {
                _suppressedWindow = _ownedCandidate!.Window;
                _suppressedSourceKey = _ownedCandidate.SourceKey;

                DropOwnershipAndBackup();

                if (IsSameEpisode(candidate, _suppressedWindow, _suppressedSourceKey))
                {
                    return null;
                }

                EndSuppression();
            }
            else
            {
                if (candidate.Window == _ownedCandidate!.Window &&
                    StringComparer.OrdinalIgnoreCase.Equals(candidate.SourceKey, _ownedCandidate.SourceKey))
                {
                    return null;
                }

                return PrepareAcquire(candidate);
            }
        }

        if (IsSuppressed(candidate))
        {
            return null;
        }

        EndSuppression();

        if (StringComparer.OrdinalIgnoreCase.Equals(candidate.SourceKey, settings.SelectedSourceKey))
        {
            return null;
        }

        _backupSourceKey = settings.SelectedSourceKey;
        _backupDisplayName = settings.SelectedSourceDisplayName;
        _backupHeldSourceKey = settings.LastKnownSourceKey;
        _backupHeldRateHz = settings.LastKnownSampleRateHz;

        return PrepareAcquire(candidate);
    }

    public void ConfirmSelection(SourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_pendingAction == PendingAction.None)
        {
            throw new InvalidOperationException("ConfirmSelection must only be used following a returned action.");
        }

        if (_pendingAction == PendingAction.Acquire)
        {
            _ownedCandidate = _pendingCandidate;
            _confirmedSourceKey = settings.SelectedSourceKey;
            _confirmedRevision = settings.SelectionRevision;
            _confirmedEndpoint = settings.TargetEndpointId;
            _isOwning = true;
            HasPriority = true;
        }
        else if (_pendingAction == PendingAction.Restore)
        {
            _ownedCandidate = null;
            _confirmedSourceKey = settings.SelectedSourceKey;
            _confirmedRevision = settings.SelectionRevision;
            _confirmedEndpoint = settings.TargetEndpointId;
            _isOwning = false;
            _backupSourceKey = null;
            _backupDisplayName = null;
            _backupHeldSourceKey = null;
            _backupHeldRateHz = null;
            HasPriority = false;
        }

        _pendingAction = PendingAction.None;
        _pendingCandidate = null;
    }

    private FullscreenMasterSelection PrepareAcquire(FullscreenCandidate candidate)
    {
        _pendingAction = PendingAction.Acquire;
        _pendingCandidate = candidate;
        return new FullscreenMasterSelection(candidate.SourceKey, candidate.DisplayName, null, null);
    }

    private FullscreenMasterSelection PrepareRestore()
    {
        _pendingAction = PendingAction.Restore;
        _pendingCandidate = null;
        HasPriority = false;
        return new FullscreenMasterSelection(_backupSourceKey, _backupDisplayName, _backupHeldSourceKey, _backupHeldRateHz);
    }

    private bool HasManualChange(SourceSettings settings)
    {
        return !StringComparer.OrdinalIgnoreCase.Equals(settings.SelectedSourceKey, _confirmedSourceKey)
            || settings.SelectionRevision != _confirmedRevision
            || !StringComparer.OrdinalIgnoreCase.Equals(settings.TargetEndpointId, _confirmedEndpoint);
    }

    private void DropOwnershipAndBackup()
    {
        _isOwning = false;
        _ownedCandidate = null;
        _backupSourceKey = null;
        _backupDisplayName = null;
        _backupHeldSourceKey = null;
        _backupHeldRateHz = null;
        HasPriority = false;
        _pendingAction = PendingAction.None;
        _pendingCandidate = null;
    }

    private bool IsSuppressed(FullscreenCandidate candidate)
    {
        return _suppressedWindow != 0 &&
               _suppressedSourceKey is not null &&
               IsSameEpisode(candidate, _suppressedWindow, _suppressedSourceKey);
    }

    private static bool IsSameEpisode(FullscreenCandidate candidate, nint window, string? sourceKey)
    {
        return candidate.Window == window &&
               StringComparer.OrdinalIgnoreCase.Equals(candidate.SourceKey, sourceKey);
    }

    private void EndSuppression()
    {
        _suppressedWindow = 0;
        _suppressedSourceKey = null;
    }
}
