using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class AudioPeakMeterViewTests
{
    [Theory]
    [InlineData(1f, 0, 1)]
    [InlineData(.1f, -20, 2d / 3)]
    [InlineData(.01f, -40, 1d / 3)]
    [InlineData(.001f, -60, 0)]
    [InlineData(0f, -60, 0)]
    public void Normalized_session_peak_uses_a_logarithmic_sixty_decibel_scale(float peak, double db, double fraction)
    {
        var meter = new AudioMeterEnvelope();
        meter.Update(peak, TimeSpan.Zero);
        Assert.True(meter.IsAvailable);
        Assert.Equal(db, meter.Decibels, 4);
        Assert.Equal(fraction, meter.DisplayFraction, 4);
        Assert.Equal(peak <= .001f, meter.IsQuiet);
    }

    [Fact]
    public void Attack_is_immediate_release_is_bounded_and_reaches_silence_in_two_hundred_ms()
    {
        var meter = new AudioMeterEnvelope();
        meter.Update(.01f, TimeSpan.Zero);
        meter.Update(1, TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, meter.DisplayFraction);
        meter.Update(0, TimeSpan.FromMilliseconds(100));
        Assert.Equal(.75, meter.DisplayFraction, 5);
        Assert.True(meter.IsQuiet); // The numeric value describes the current observation, not the release envelope.
        meter.Update(0, TimeSpan.FromMilliseconds(200));
        Assert.Equal(.25, meter.DisplayFraction, 5);
        meter.Update(0, TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, meter.DisplayFraction);
    }

    [Fact]
    public void Out_of_order_timestamp_cannot_raise_or_age_the_meter()
    {
        var meter = new AudioMeterEnvelope();
        meter.Update(.1f, TimeSpan.FromSeconds(2));
        var fraction = meter.DisplayFraction;
        meter.Update(1, TimeSpan.FromSeconds(1));
        Assert.Equal(fraction, meter.DisplayFraction);
        Assert.Equal(-20, meter.Decibels, 4);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-.1f)]
    [InlineData(1.1f)]
    public void Invalid_measurement_clears_immediately_instead_of_displaying_certainty(float invalid)
    {
        var meter = new AudioMeterEnvelope();
        meter.Update(1, TimeSpan.Zero);
        meter.Update(invalid, TimeSpan.FromMilliseconds(50));
        Assert.False(meter.IsAvailable);
        Assert.Equal(0, meter.DisplayFraction);
    }

    [Fact]
    public void Missing_data_clears_release_and_the_next_valid_sample_attacks_from_fresh_state()
    {
        var meter = new AudioMeterEnvelope();
        meter.Update(1, TimeSpan.Zero);
        meter.Update(null, TimeSpan.FromMilliseconds(50));
        Assert.False(meter.IsAvailable);
        meter.Update(.01f, TimeSpan.FromMilliseconds(100));
        Assert.Equal(1d / 3, meter.DisplayFraction, 4);
        meter.Clear();
        Assert.False(meter.IsAvailable);
        Assert.Equal(0, meter.DisplayFraction);
    }
}
