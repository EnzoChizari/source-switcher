using WindowsLosslessSwitcher.Services;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

internal sealed class EndpointRateAdapter(CoreAudioEndpointController controller) : IEndpointRateAdapter
{
    public EndpointFormat? ReadDeviceFormat(string endpointId) => Convert(controller.GetCurrentDeviceFormat(endpointId));
    public EndpointFormat? ReadMixFormat(string endpointId) => Convert(controller.GetCurrentMixFormat(endpointId));
    public IReadOnlyList<EndpointFormat> ReadSupportedFormats(string endpointId) =>
        controller.GetSupportedFormats(endpointId).Select(format => Convert(format)!).ToArray();
    public bool TryApply(string endpointId, EndpointFormat format, out string failure) =>
        controller.TryApplyFormat(endpointId, new(format.SampleRateHz, format.BitsPerSample, format.Channels), out _, out failure);

    private static EndpointFormat? Convert(WindowsLosslessSwitcher.Models.AudioFormatCandidate? format) =>
        format is null ? null : new(format.SampleRateHz, format.BitDepth, format.Channels);
}
