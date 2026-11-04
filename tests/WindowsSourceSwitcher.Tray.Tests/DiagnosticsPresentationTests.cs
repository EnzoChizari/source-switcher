using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class DiagnosticsPresentationTests
{
    [Fact]
    public void Export_preserves_distinct_source_stream_engine_and_output_evidence()
    {
        var snapshot = new AudioDiagnosticsSnapshot([
            new("Content/native rate", "Unknown"),
            new("Submitted PCM", "48 kHz — observed WASAPI stream"),
            new("Engine mix", "44.1 kHz"),
            new("Endpoint output", "44.1 kHz · 32-bit · 2 channels"),
            new("Resampling", "Expected: submitted PCM differs from output.")], []);

        var text = DiagnosticsPresentation.FormatSnapshot(snapshot);

        Assert.Contains("Content/native rate: Unknown", text);
        Assert.Contains("Submitted PCM: 48 kHz — observed WASAPI stream", text);
        Assert.Contains("Engine mix: 44.1 kHz", text);
        Assert.Contains("Endpoint output: 44.1 kHz · 32-bit · 2 channels", text);
        Assert.DoesNotContain("Verified", text);
    }

    [Fact]
    public void Export_keeps_utc_sequence_and_fact_detail_without_claiming_audible_interruption()
    {
        var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(-4));
        var snapshot = new AudioDiagnosticsSnapshot([], [
            new(3, time, "Format result", "Endpoint readback matched 96 kHz."),
            new(2, time.AddSeconds(-1), "Format attempt", "Requested 96 kHz.")]);

        var text = DiagnosticsPresentation.FormatSnapshot(snapshot);

        Assert.Contains("2026-10-20T16:00:00.0000000+00:00 #3 Format result: Endpoint readback matched 96 kHz.", text);
        Assert.True(text.IndexOf("#3", StringComparison.Ordinal) < text.IndexOf("#2", StringComparison.Ordinal));
        Assert.Contains("do not establish an audible interruption", text);
        Assert.DoesNotContain("interruption detected", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_snapshot_has_an_honest_export_without_inventing_readings()
    {
        var text = DiagnosticsPresentation.FormatSnapshot(new([], []));

        Assert.Contains("Source Switcher diagnostics", text);
        Assert.DoesNotContain("kHz", text);
        Assert.DoesNotContain("bit-perfect", text);
    }
}
