using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal static class AppleMusicFormatPolicy
{
    internal static RateDecision? ManualDepthAtCurrentRate(SourceSettings settings, RateDecision decision, int currentRate)
    {
        if (!settings.SwitchingEnabled || !settings.FollowSourceBitDepth
            || settings.SelectedSourceKey is not { } source || !AppleMusicIdentity.IsOfficialExecutable(source)
            || !settings.ManualBitDepths.TryGetValue(source, out var bits) || bits is not (16 or 24 or 32)
            || decision.Reason is not (RateDecisionReason.SourcePaused
                or RateDecisionReason.NativeRateUnknown or RateDecisionReason.ConflictingObservations)) return null;
        return new(currentRate, RateDecisionReason.ManualOverride,
            "Applying Apple Music's manual bit depth at the current output rate.", bits, ManualBitDepth: true);
    }
}
