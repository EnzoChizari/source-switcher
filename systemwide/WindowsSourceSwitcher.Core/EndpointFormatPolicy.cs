namespace WindowsSourceSwitcher.Core;

/// <summary>Only PCM dimensions needed for pure endpoint format selection; the adapter retains the native blob.</summary>
public sealed record EndpointFormat(int SampleRateHz, int BitsPerSample, int Channels);

public static class EndpointFormatPolicy
{
    /// <summary>
    /// Require exact sample rate, depth and channel count. An unsupported rate must produce a visible
    /// adapter error, never a nearest-rate or different-depth/channel fallback.
    /// </summary>
    public static EndpointFormat? SelectExact(
        EndpointFormat current,
        IEnumerable<EndpointFormat> supportedFormats,
        int desiredSampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(supportedFormats);
        if (!SampleRates.IsValid(desiredSampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(desiredSampleRateHz));

        return supportedFormats.FirstOrDefault(format => format.SampleRateHz == desiredSampleRateHz
            && format.BitsPerSample == current.BitsPerSample
            && format.Channels == current.Channels);
    }
}
