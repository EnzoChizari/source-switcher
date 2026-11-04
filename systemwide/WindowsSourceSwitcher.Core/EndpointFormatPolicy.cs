namespace WindowsSourceSwitcher.Core;

/// <summary>Only PCM dimensions needed for pure endpoint format selection; the adapter retains the native blob.</summary>
public sealed record EndpointFormat(int SampleRateHz, int BitsPerSample, int Channels);

public static class EndpointFormatPolicy
{
    /// <summary>
    /// Require the exact sample rate and channel count. With source precision available, prefer
    /// the smallest depth that can carry it, or the highest available depth below it.
    /// </summary>
    public static EndpointFormat? SelectExact(
        EndpointFormat current,
        IEnumerable<EndpointFormat> supportedFormats,
        int desiredSampleRateHz,
        int? sourceBitsPerSample = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(supportedFormats);
        if (!SampleRates.IsValid(desiredSampleRateHz))
            throw new ArgumentOutOfRangeException(nameof(desiredSampleRateHz));

        var matchingRate = supportedFormats.Where(format => format.SampleRateHz == desiredSampleRateHz
            && format.Channels == current.Channels);
        if (sourceBitsPerSample is not (16 or 24 or 32))
            return matchingRate.FirstOrDefault(format => format.BitsPerSample == current.BitsPerSample);

        return matchingRate.Where(format => format.BitsPerSample is 16 or 24 or 32)
            .OrderBy(format => format.BitsPerSample < sourceBitsPerSample.Value)
            .ThenBy(format => Math.Abs(format.BitsPerSample - sourceBitsPerSample.Value))
            .FirstOrDefault();
    }
}
