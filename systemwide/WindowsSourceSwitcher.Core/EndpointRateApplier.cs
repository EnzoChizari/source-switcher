namespace WindowsSourceSwitcher.Core;

/// <summary>
/// Endpoint access supplied by the Windows host. Reads must be fresh, including the mix format
/// from a newly activated audio client after a change. Implementations retain the native format blob.
/// </summary>
public interface IEndpointRateAdapter
{
    EndpointFormat? ReadDeviceFormat(string endpointId);
    EndpointFormat? ReadMixFormat(string endpointId);
    IReadOnlyList<EndpointFormat> ReadSupportedFormats(string endpointId);
    bool TryApply(string endpointId, EndpointFormat format, out string failure);
}

/// <summary>Changed means a write was accepted, even if subsequent verification failed.</summary>
public sealed record ApplyResult(bool Success, bool Changed, string Detail);

/// <summary>Applies the requested rate with the selected endpoint precision and original channel count.</summary>
public static class EndpointRateApplier
{
    public static ApplyResult Apply(IEndpointRateAdapter adapter, string endpointId, int desiredHz,
        int? sourceBitsPerSample = null, bool requireExactDepth = false)
    {
        var changed = false;
        try
        {
            ArgumentNullException.ThrowIfNull(adapter);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
            if (!SampleRates.IsValid(desiredHz))
                return new(false, false, "Requested sample rate must be between 8000 and 768000 Hz.");

            var current = adapter.ReadDeviceFormat(endpointId);
            if (current is null)
                return new(false, false, "Cannot read the endpoint device format; no change was attempted.");

            var mix = adapter.ReadMixFormat(endpointId);
            if (current.SampleRateHz == desiredHz && mix?.SampleRateHz == desiredHz
                && (sourceBitsPerSample is null || current.BitsPerSample == sourceBitsPerSample))
                return new(true, false, $"Endpoint device and shared mix already use {desiredHz} Hz.");

            var selected = EndpointFormatPolicy.SelectExact(current, adapter.ReadSupportedFormats(endpointId), desiredHz,
                sourceBitsPerSample, requireExactDepth);
            if (selected is null)
                return new(false, false, sourceBitsPerSample is null
                    ? $"Endpoint does not advertise {desiredHz} Hz at its current {current.BitsPerSample}-bit, {current.Channels}-channel format; no change was attempted."
                    : requireExactDepth
                        ? $"Endpoint does not advertise {desiredHz} Hz at {sourceBitsPerSample}-bit with {current.Channels} channels."
                        : $"Endpoint does not advertise {desiredHz} Hz in 16-, 24-, or 32-bit PCM with {current.Channels} channels.");

            if (selected == current && mix?.SampleRateHz == desiredHz)
                return new(true, false, $"The endpoint already uses the supported format {Describe(current)}.");

            if (!adapter.TryApply(endpointId, selected, out var failure))
                return new(false, false, string.IsNullOrWhiteSpace(failure)
                    ? "Windows rejected the endpoint format change."
                    : $"Windows rejected the endpoint format change: {failure}");

            changed = true;
            var deviceAfter = adapter.ReadDeviceFormat(endpointId);
            var mixAfter = adapter.ReadMixFormat(endpointId);
            if (deviceAfter != selected || mixAfter?.SampleRateHz != desiredHz)
                return new(false, true,
                    $"Endpoint format change was accepted but readback did not confirm it. Expected device {Describe(selected)} and shared mix {desiredHz} Hz; read device {Describe(deviceAfter)}, mix {Describe(mixAfter)}. Playback may need to restart.");

            return new(true, true, $"Confirmed endpoint device and shared mix at {desiredHz} Hz ({selected.BitsPerSample}-bit, {selected.Channels} channels).");
        }
        catch (Exception exception)
        {
            return new(false, changed, $"Endpoint sample-rate change failed: {exception.Message} (0x{exception.HResult:X8}).");
        }
    }

    private static string Describe(EndpointFormat? format) => format is null
        ? "unavailable"
        : $"{format.SampleRateHz} Hz/{format.BitsPerSample}-bit/{format.Channels} channels";
}
