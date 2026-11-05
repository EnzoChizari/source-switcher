using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Reads the open Apple Music quality panel without opening, closing or controlling the player.</summary>
internal sealed class AppleMusicQualityReader
{
    internal sealed record Format(int Rate, int? Bits)
    {
        internal RateObservation Observe(PlatformAudioSource source, long revision) =>
            new(source.Identity, source.SessionToken, revision, Rate, RateProvenance.PlayerMetadata,
                "Apple Music Lossless details report this track's ALAC format.", Bits);
    }

    private int _reading;

    internal async Task<Format?> ReadAsync(PlatformAudioSource source, TrackSnapshot track)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) return null;
        var pending = Task.Run(() =>
        {
            try { return Read(source, track); }
            finally { Volatile.Write(ref _reading, 0); }
        });
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private static Format? Read(PlatformAudioSource source, TrackSnapshot track)
    {
        var processes = Process.GetProcessesByName("AppleMusic");
        try
        {
            var window = (Handle: nint.Zero, Pid: 0);
            foreach (var process in processes)
            {
                var handle = process.MainWindowHandle;
                if (handle == nint.Zero || !AppleMusicIdentity.Equivalent(process.MainModule?.FileName, source.ExecutablePath)) continue;
                if (window.Handle != nint.Zero) return null;
                window = (handle, process.Id);
            }
            if (window.Handle == nint.Zero) return null;
            var root = AutomationElement.FromHandle(window.Handle);
            if (root.Current.ProcessId != window.Pid || root.Current.IsOffscreen
                || !CaptionMatches(Find(root, "LCD")?.Current.Name, track)) return null;
            var panel = Find(root, "AudioQualityFlyout");
            if (panel is null || panel.Current.IsOffscreen) return null;
            var labels = panel.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Text));
            var formats = labels.Cast<AutomationElement>().Where(label => !label.Current.IsOffscreen)
                .Select(label => Parse(label.Current.Name)).OfType<Format>().Distinct().Take(2).ToArray();
            return formats.Length == 1 && !panel.Current.IsOffscreen && CaptionMatches(Find(root, "LCD")?.Current.Name, track)
                ? formats[0] : null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static AutomationElement? Find(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    internal static bool CaptionMatches(string? caption, TrackSnapshot track)
    {
        if (caption is null || string.IsNullOrWhiteSpace(track.Title)
            || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Album)) return false;
        static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
        var expected = Normalize($"{track.Title} {track.Artist} — {track.Album}");
        var actual = Normalize(caption);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || actual.Equals(expected + " Explicit", StringComparison.OrdinalIgnoreCase);
    }

    internal static Format? Parse(string? label)
    {
        if (label is null || label.Length > 120) return null;
        var match = Regex.Match(label, @"^\s*(?:(?<bits>16|24|32)[-\u2010-\u2013\s]?bit\s+)?(?<rate>[0-9]+(?:[.,][0-9]+)?)\s*kHz\s+ALAC\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !decimal.TryParse(match.Groups["rate"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var khz) || khz < 8 || khz > 768) return null;
        var hz = khz * 1000;
        if (hz != decimal.Truncate(hz) || !SampleRates.IsValid((int)hz)) return null;
        var bits = match.Groups["bits"];
        return new((int)hz, bits.Success ? int.Parse(bits.Value, CultureInfo.InvariantCulture) : null);
    }
}
