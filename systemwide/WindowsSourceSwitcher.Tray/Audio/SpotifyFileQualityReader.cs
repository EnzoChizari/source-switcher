using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray.Audio;

/// <summary>Reads the open Spotify desktop File quality card without changing the player's UI.</summary>
internal sealed class SpotifyFileQualityReader
{
    private int _reading;

    internal sealed record Reading(int Bits, string TrackLabel)
    {
        internal RateObservation Observe(SourceSnapshot source, long revision) =>
            new(source.Key, source.SessionToken, revision, 44100, RateProvenance.PlayerMetadata,
                "Spotify File quality reports lossless FLAC.", Bits);
    }

    internal async Task<Reading?> ReadAsync(string sourceKey)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) return null;
        var pending = Task.Run(() =>
        {
            try { return Read(sourceKey); }
            finally { Volatile.Write(ref _reading, 0); }
        });
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private static Reading? Read(string sourceKey)
    {
        var processes = Process.GetProcessesByName("Spotify");
        try
        {
            var window = nint.Zero;
            foreach (var process in processes)
            {
                var candidate = process.MainWindowHandle;
                if (candidate == nint.Zero || process.MainModule?.FileName is not { } path
                    || !StringComparer.OrdinalIgnoreCase.Equals(SourceIdentity.FromExecutablePath(path), sourceKey)) continue;
                if (window != nint.Zero) return null;
                window = candidate;
            }
            if (window == nint.Zero) return null;

            var root = AutomationElement.FromHandle(window);
            var panel = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "Desktop_PanelContainer_Id"));
            if (panel is null || panel.Current.IsOffscreen || !HasText(panel, "This computer")) return null;
            var nowPlaying = root.FindFirst(TreeScope.Descendants, Named("Now playing bar"));
            if (nowPlaying is null || TrackLabel(nowPlaying) is not { } track) return null;

            var cards = panel.FindAll(TreeScope.Descendants,
                new OrCondition(Named("Lossless 16-bit"), Named("Lossless 24-bit")));
            if (cards.Count != 1 || cards[0].Current.IsOffscreen) return null;
            var card = cards[0];
            var bits = ReadCardDepth(card.Current.Name, text => HasText(card, text));
            return bits is { } depth && TrackLabel(nowPlaying) == track ? new(depth, track) : null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or
            InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static PropertyCondition Named(string name) => new(AutomationElement.NameProperty, name);

    internal static int? ReadCardDepth(string name, Func<string, bool> hasText)
    {
        int? bits = name switch { "Lossless 16-bit" => 16, "Lossless 24-bit" => 24, _ => null };
        return bits is { } depth && hasText("File quality") && hasText($"Up to {depth}-bit/44.1 kHz • FLAC") ? depth : null;
    }

    private static bool HasText(AutomationElement parent, string name) =>
        parent.FindFirst(TreeScope.Descendants, new AndCondition(
            Named(name), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))) is not null;

    private static string? TrackLabel(AutomationElement bar) =>
        bar.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
            .Select(element => element.Current.Name).FirstOrDefault(name => name.StartsWith("Now playing: ", StringComparison.Ordinal));
}
