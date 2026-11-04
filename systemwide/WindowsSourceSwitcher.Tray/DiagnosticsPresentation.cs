using System.Globalization;
using System.Text;

namespace WindowsSourceSwitcher.Tray;

internal sealed record DiagnosticField(string Name, string Value);
internal sealed record AudioDiagnosticsSnapshot(IReadOnlyList<DiagnosticField> Fields,
    IReadOnlyList<ControlHistoryEntry> History);

internal static class DiagnosticsPresentation
{
    public static string FormatSnapshot(AudioDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder(Program.ProductName).AppendLine(" diagnostics");
        foreach (var field in snapshot.Fields)
            text.Append(field.Name).Append(": ").AppendLine(field.Value);
        text.AppendLine().AppendLine("Control event history (newest first; software observations)");
        text.AppendLine("These events do not establish an audible interruption or physical DAC output.");
        foreach (var entry in snapshot.History)
            text.Append(entry.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append(" #").Append(entry.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(entry.Kind).Append(": ").AppendLine(entry.Detail);
        return text.ToString();
    }
}
