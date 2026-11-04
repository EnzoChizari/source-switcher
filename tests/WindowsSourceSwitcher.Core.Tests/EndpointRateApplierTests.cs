using System.Runtime.InteropServices;
using WindowsSourceSwitcher.Core;
using Xunit;

namespace WindowsSourceSwitcher.Core.Tests;

public sealed class EndpointRateApplierTests
{
    private const string EndpointId = "topping-dx5-ii-test-endpoint";
    private static readonly EndpointFormat Initial = new(48000, 24, 2);
    private static readonly EndpointFormat Target = new(44100, 24, 2);

    [Fact]
    public void ExactSupportedRateIsWrittenOnceAndVerifiedAgainstDeviceAndMix()
    {
        var adapter = new FakeAdapter();
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal([Target], adapter.Writes);
        Assert.Equal(2, adapter.DeviceReads);
        Assert.Equal(2, adapter.MixReads);
        Assert.All(adapter.EndpointIds, id => Assert.Equal(EndpointId, id));
        Assert.Contains("Confirmed", result.Detail);
    }

    [Fact]
    public void MatchingDeviceAndMixAreSuccessfulWithoutProbingOrWriting()
    {
        var adapter = new FakeAdapter { Device = Target, Mix = Target };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Empty(adapter.Writes);
        Assert.Equal(0, adapter.SupportReads);
    }

    [Fact]
    public void MatchingDeviceWithWrongMixStillAttemptsChange()
    {
        var adapter = new FakeAdapter { Device = Target, Mix = Initial };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Single(adapter.Writes);
    }

    [Fact]
    public void UnsupportedExactRateNeverWritesOrChoosesNearestRate()
    {
        var adapter = new FakeAdapter { Supported = [new(88200, 24, 2), new(96000, 24, 2)] };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 86000);

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Empty(adapter.Writes);
        Assert.Contains("86000 Hz", result.Detail);
    }

    [Theory]
    [InlineData(16, 2)]
    [InlineData(32, 2)]
    [InlineData(24, 6)]
    public void RateAtDifferentDepthOrChannelCountNeverWrites(int bits, int channels)
    {
        var adapter = new FakeAdapter { Supported = [new(44100, bits, channels)] };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.Empty(adapter.Writes);
    }

    [Fact]
    public void NativeFailureDetailIncludingHResultIsPreserved()
    {
        var adapter = new FakeAdapter { ApplySucceeds = false, Failure = "SetDeviceFormat failed: 0x8889000A" };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Contains(adapter.Failure, result.Detail);
        Assert.Single(adapter.Writes);
        Assert.Equal(1, adapter.DeviceReads);
    }

    [Theory]
    [InlineData(48000, 24, 2)]
    [InlineData(44100, 16, 2)]
    [InlineData(44100, 24, 6)]
    public void IncorrectDeviceReadbackFailsEvenWhenMixMatches(int rate, int bits, int channels)
    {
        var adapter = new FakeAdapter { DeviceAfter = new(rate, bits, channels) };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.True(result.Changed);
        Assert.Contains("readback", result.Detail);
        Assert.Single(adapter.Writes);
    }

    [Fact]
    public void MixReadbackMismatchIsVisibleFailure()
    {
        var adapter = new FakeAdapter { MixAfter = Initial };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.True(result.Changed);
        Assert.Contains("mix 48000 Hz", result.Detail);
    }

    [Fact]
    public void MissingDeviceReadbackIsVisibleFailure()
    {
        var adapter = new FakeAdapter { DeviceMissingAfter = true };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.True(result.Changed);
        Assert.Contains("device unavailable", result.Detail);
    }

    [Fact]
    public void MissingMixReadbackIsVisibleFailure()
    {
        var adapter = new FakeAdapter { MixMissingAfter = true };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.True(result.Changed);
        Assert.Contains("mix unavailable", result.Detail);
    }

    [Fact]
    public void MissingCurrentDeviceFormatPreventsWrite()
    {
        var adapter = new FakeAdapter { Device = null };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Empty(adapter.Writes);
        Assert.Equal(0, adapter.SupportReads);
    }

    [Theory]
    [InlineData(7999)]
    [InlineData(768001)]
    public void InvalidRateNeverTouchesEndpoint(int rate)
    {
        var adapter = new FakeAdapter();
        var result = EndpointRateApplier.Apply(adapter, EndpointId, rate);

        Assert.False(result.Success);
        Assert.Empty(adapter.EndpointIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionsAreReturnedWithMessageAndHResult(bool afterWrite)
    {
        var adapter = new FakeAdapter { ThrowOnRead = !afterWrite, ThrowOnReadback = afterWrite };
        var result = EndpointRateApplier.Apply(adapter, EndpointId, 44100);

        Assert.False(result.Success);
        Assert.Equal(afterWrite, result.Changed);
        Assert.Contains("test endpoint disconnected", result.Detail);
        Assert.Contains("0x88890004", result.Detail);
    }

    private sealed class FakeAdapter : IEndpointRateAdapter
    {
        public EndpointFormat? Device { get; init; } = Initial;
        public EndpointFormat? Mix { get; init; } = Initial;
        public EndpointFormat DeviceAfter { get; init; } = Target;
        public EndpointFormat MixAfter { get; init; } = Target;
        public bool DeviceMissingAfter { get; init; }
        public bool MixMissingAfter { get; init; }
        public bool ApplySucceeds { get; init; } = true;
        public string Failure { get; init; } = "";
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnReadback { get; init; }
        public IReadOnlyList<EndpointFormat> Supported { get; init; } = [Target];
        public List<EndpointFormat> Writes { get; } = [];
        public List<string> EndpointIds { get; } = [];
        public int DeviceReads { get; private set; }
        public int MixReads { get; private set; }
        public int SupportReads { get; private set; }

        public EndpointFormat? ReadDeviceFormat(string endpointId)
        {
            EndpointIds.Add(endpointId);
            DeviceReads++;
            if (ThrowOnRead || ThrowOnReadback && DeviceReads > 1)
                throw new COMException("test endpoint disconnected", unchecked((int)0x88890004));
            return Writes.Count == 0 ? Device : DeviceMissingAfter ? null : DeviceAfter;
        }

        public EndpointFormat? ReadMixFormat(string endpointId)
        {
            EndpointIds.Add(endpointId);
            MixReads++;
            return Writes.Count == 0 ? Mix : MixMissingAfter ? null : MixAfter;
        }

        public IReadOnlyList<EndpointFormat> ReadSupportedFormats(string endpointId)
        {
            EndpointIds.Add(endpointId);
            SupportReads++;
            return Supported;
        }

        public bool TryApply(string endpointId, EndpointFormat format, out string failure)
        {
            EndpointIds.Add(endpointId);
            Writes.Add(format);
            failure = Failure;
            return ApplySucceeds;
        }
    }
}
