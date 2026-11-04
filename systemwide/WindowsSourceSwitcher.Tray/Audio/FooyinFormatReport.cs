using System.Diagnostics;
using System.Globalization;
using System.Text;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Reads a user-configured, local fooyin Now Playing report. No process hooks or network calls.</summary>
internal static class FooyinFormatReport
{
    internal const string FileName = "fooyin-format.txt";
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);

    internal static RateObservation? Read(string path, SourceSnapshot source, long selectionRevision)
    {
        if (!source.IsActive || source.ProcessIds.Count != 1 || !File.Exists(path)) return null;
        try
        {
            using var process = Process.GetProcessById(source.ProcessIds[0]);
            if (process.MainModule?.FileName is not { } pathOfProcess
                || !StringComparer.OrdinalIgnoreCase.Equals(SourceIdentity.FromExecutablePath(pathOfProcess), source.Key)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> bytes = stackalloc byte[129];
            var length = file.ReadAtLeast(bytes, bytes.Length, false);
            if (length == bytes.Length) return null;
            var modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
            var now = DateTime.UtcNow;
            if (modified < process.StartTime.ToUniversalTime() || modified > now || now - modified > MaximumAge
                || process.HasExited) return null;
            var format = Parse(Encoding.UTF8.GetString(bytes[..length]));
            return format is { } valid ? new(source.Key, source.SessionToken, selectionRevision, valid.Rate,
                RateProvenance.PlayerMetadata, "Fooyin Now Playing track metadata.", valid.Bits) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static (int Rate, int? Bits)? Parse(string text)
    {
        var fields = text.Trim().TrimStart('\uFEFF').Split('|');
        if (fields is not ["SSFOOYIN1", "1", var rate, var bits, var heartbeat] || heartbeat.Length == 0
            || !int.TryParse(rate, NumberStyles.None, CultureInfo.InvariantCulture, out var hz) || !SampleRates.IsValid(hz))
            return null;
        int? depth = int.TryParse(bits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is 16 or 24 or 32 ? value : null;
        return (hz, depth);
    }
}
