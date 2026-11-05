using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using WindowsLosslessSwitcher.Models;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Reads Apple Music's quality panel, with bounded automatic attempts and cleanup of its own popup.</summary>
internal sealed class AppleMusicQualityReader
{
    internal sealed record Result(Format? Format, bool RetrySuggested = false);
    internal sealed record Format(int Rate, int? Bits)
    {
        internal RateObservation Observe(PlatformAudioSource source, long revision) =>
            new(source.Identity, source.SessionToken, revision, Rate, RateProvenance.PlayerMetadata,
                "Apple Music Lossless details report this track's ALAC format.", Bits);
    }

    private int _reading;
    private (TrackSnapshot Track, int ProcessId, int Count)? _automaticAttempt;
    private bool _automaticDismissalFailed;

    internal async Task<Result> ReadAsync(PlatformAudioSource source, TrackSnapshot track, bool automatic = false)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) return new(null);
        var pending = Task.Run(() =>
        {
            try { return Read(source, track, automatic); }
            finally { Volatile.Write(ref _reading, 0); }
        });
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false); }
        catch (TimeoutException) { return new(null); }
    }

    private Result Read(PlatformAudioSource source, TrackSnapshot track, bool automatic)
    {
        var window = PlayerWindowLocator.Find("AppleMusic", path => AppleMusicIdentity.Equivalent(path, source.ExecutablePath));
        return window is { } found ? ReadWindow(found, track, automatic) : new(null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Result ReadWindow((nint Handle, int Pid) window, TrackSnapshot track, bool automatic)
    {
        AutomationElement? root = null;
        int[]? ownedPanelId = null;
        try
        {
            root = AutomationElement.FromHandle(window.Handle);
            if (root.Current.ProcessId != window.Pid || root.Current.IsOffscreen) return new(null);
            var canRetry = automatic && GetForegroundWindow() == window.Handle;
            var captionMatches = CaptionMatches(Find(root, "LCD")?.Current.Name, track);
            // WinUI's caption can arrive just after the media-session notification.
            for (var retry = 0; !captionMatches && automatic && retry < 3
                && GetForegroundWindow() == window.Handle; retry++)
            {
                Thread.Sleep(25);
                captionMatches = CaptionMatches(Find(root, "LCD")?.Current.Name, track);
            }
            if (!captionMatches) return new(null, canRetry);
            var panel = Find(root, "AudioQualityFlyout");
            if (panel is null && automatic && !_automaticDismissalFailed
                && (_automaticAttempt is not { } attempted || !ReferenceEquals(attempted.Track, track)
                    || attempted.ProcessId != window.Pid || attempted.Count < 2)
                && GetForegroundWindow() == window.Handle)
            {
                var badge = Find(root, "AudioBadgeButton");
                if (badge is not null && !badge.Current.IsOffscreen && badge.Current.IsEnabled
                    && badge.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)
                    && CaptionMatches(Find(root, "LCD")?.Current.Name, track))
                {
                    // Recheck for a user-opened panel immediately before invocation.
                    panel = Find(root, "AudioQualityFlyout");
                    if (panel is null && GetForegroundWindow() == window.Handle)
                    {
                        var count = _automaticAttempt is { } previousAttempt
                            && ReferenceEquals(previousAttempt.Track, track) && previousAttempt.ProcessId == window.Pid
                            ? previousAttempt.Count + 1 : 1;
                        _automaticAttempt = (track, window.Pid, count);
                        ((InvokePattern)invoke).Invoke();
                        for (var attempt = 0; attempt < 8; attempt++)
                        {
                            panel = Find(root, "AudioQualityFlyout");
                            if (panel is not null)
                            {
                                ownedPanelId = panel.GetRuntimeId();
                                break;
                            }
                            Thread.Sleep(30);
                        }
                    }
                }
            }
            if (panel is null || panel.Current.IsOffscreen) return new(null, canRetry && !_automaticDismissalFailed);
            Format? previous = null;
            var sampleCount = ownedPanelId is null ? 1 : 5;
            for (var i = 0; i < sampleCount; i++)
            {
                var labels = panel.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Text));
                var formats = labels.Cast<AutomationElement>().Where(label => !label.Current.IsOffscreen)
                    .Select(label => Parse(label.Current.Name)).OfType<Format>().Distinct().Take(2).ToArray();
                var format = formats.Length == 1 ? formats[0] : null;
                if (format is not null && (sampleCount == 1 || format == previous))
                    return !panel.Current.IsOffscreen && CaptionMatches(Find(root, "LCD")?.Current.Name, track)
                        ? new(format) : new(null, canRetry);
                previous = format;
                if (i + 1 < sampleCount) Thread.Sleep(30);
            }
            return new(null, canRetry);
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or
            InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(null);
        }
        finally
        {
            if (root is not null && ownedPanelId is not null && !DismissOwnedPanel(root, ownedPanelId))
                _automaticDismissalFailed = true;
        }
    }

    private static bool DismissOwnedPanel(AutomationElement root, int[] ownedPanelId)
    {
        try
        {
            var panel = Find(root, "AudioQualityFlyout");
            if (panel is null || !panel.GetRuntimeId().SequenceEqual(ownedPanelId)) return true;
            var popup = TreeWalker.ControlViewWalker.GetParent(panel);
            if (popup?.Current.ClassName != "Popup" || popup.Current.ProcessId != root.Current.ProcessId
                || !popup.TryGetCurrentPattern(WindowPattern.Pattern, out var window)) return false;
            ((WindowPattern)window).Close();
            return true;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private static AutomationElement? Find(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

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
