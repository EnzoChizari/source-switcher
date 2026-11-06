using System.Buffers.Binary;
using NAudio.Wave;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class RevolutionDeviceControllerTests
{
    [Fact]
    public void RebindingRequiresTheSamePhysicalAndLogicalOutput()
    {
        var physical = Guid.NewGuid();
        var candidates = new[] { ("other-unit", "Analog Out 1/2", Guid.NewGuid()),
            ("virtual", "Virtual Out 1/2", physical), ("analog", "Analog Out 1/2", physical) };
        Assert.Equal("analog", RevolutionDeviceController.FindReplacement("Analog Out 1/2", physical, candidates));
        Assert.Null(RevolutionDeviceController.FindReplacement("Analog Out 1/2", Guid.Empty, candidates));
        Assert.Null(RevolutionDeviceController.FindReplacement("Analog Out 3/4", physical, candidates));
        Assert.Null(RevolutionDeviceController.FindReplacement("Analog Out 1/2", physical,
            candidates.Append(("ambiguous", "Analog Out 1/2", physical))));
    }

    [Fact]
    public void DriverRatesDoNotInventBitDepthsOrModifyTheCurrentFormat()
    {
        var current = EndpointFormatSupport.CreateDescriptor(new WaveFormatExtensible(48000, 24, 2), EndpointFormatOrigin.PropertyStore);
        var before = EndpointFormatSupport.SerializeWaveFormat(current.NativeFormat);
        var result = RevolutionDeviceController.BuildFormats(current, [44100, 48000, 88200, 96000, 176400, 192000]);
        Assert.Equal(6, result.Count);
        Assert.All(result, item =>
        {
            Assert.Equal(24, item.Format.BitDepth);
            Assert.Equal(2, item.Format.Channels);
            Assert.Equal(EndpointFormatOrigin.DriverApi, item.Origin);
            Assert.Equal(item.NativeFormat.SampleRate * item.NativeFormat.BlockAlign, item.NativeFormat.AverageBytesPerSecond);
        });
        Assert.Equal(before, EndpointFormatSupport.SerializeWaveFormat(current.NativeFormat));
    }

    [Fact]
    public void PaddedPrecisionAndSpeakerMaskSurviveRateChanges()
    {
        var blob = EndpointFormatSupport.SerializeWaveFormat(new WaveFormatExtensible(48000, 32, 2));
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(18), 24);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(20), 0);
        var current = EndpointFormatSupport.TryParseDeviceFormat(blob)!;
        var target = Assert.Single(RevolutionDeviceController.BuildFormats(current, [192000]));
        var after = EndpointFormatSupport.SerializeWaveFormat(target.NativeFormat);
        Assert.Equal(24, target.Format.BitDepth);
        Assert.Equal(32, target.NativeFormat.BitsPerSample);
        Assert.Equal(blob.AsSpan(12).ToArray(), after.AsSpan(12).ToArray());
    }

    [Fact]
    public void InvalidOrDuplicateDriverRatesAreNotOffered()
    {
        var current = EndpointFormatSupport.CreateDescriptor(new WaveFormat(48000, 24, 2), EndpointFormatOrigin.PropertyStore);
        var result = RevolutionDeviceController.BuildFormats(current, [0, -1, int.MaxValue, 48000, 44100, 48000]);
        Assert.Equal(new[] { 44100, 48000 }, result.Select(item => item.Format.SampleRateHz));
        Assert.Empty(RevolutionDeviceController.BuildFormats(current, []));
    }
}
