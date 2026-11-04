using System.Globalization;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

/// <summary>Read-only presentation. The sole external write is an explicit Copy diagnostics action.</summary>
internal sealed class DiagnosticsView : UserControl
{
    private readonly ListView _fields = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        ShowItemToolTips = true, AccessibleName = "Current audio diagnostics"
    };
    private readonly ListView _history = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        ShowItemToolTips = true, AccessibleName = "Controller event history"
    };
    private readonly Button _copy = new()
    {
        AutoSize = true, Text = "Copy diagnostics", AccessibleName = "Copy diagnostics",
        AccessibleDescription = "Copy the displayed audio fields and controller event history to the clipboard.", Enabled = false
    };
    private readonly Label _copyStatus = new()
    {
        AutoSize = true, MaximumSize = new Size(414, 0), AccessibleName = "Diagnostics copy status"
    };
    private readonly Action<string> _copyText;
    private AudioDiagnosticsSnapshot _snapshot = new([], []);
    private readonly Dictionary<ListView, (int Width, int Columns, int Used)> _columnLayouts = [];

    public DiagnosticsView(Action<string>? copyText = null)
    {
        Dock = DockStyle.Fill;
        _copyText = copyText ?? Clipboard.SetText;
        _fields.Columns.Add("Field", 138);
        _fields.Columns.Add("Value", 267);
        _history.Columns.Add("Time", 86);
        _history.Columns.Add("Event", 85);
        _history.Columns.Add("Details", 234);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Current audio state", Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        var currentCard = new SectionCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
        currentCard.Controls.Add(_fields);
        layout.Controls.Add(currentCard, 0, 1);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Recent controller events (newest first)", Margin = new Padding(0, 10, 0, 6) }, 0, 2);
        var historyCard = new SectionCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
        historyCard.Controls.Add(_history);
        layout.Controls.Add(historyCard, 0, 3);
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(414, 0), Margin = new Padding(0, 8, 0, 8),
            Text = "Session and format events are software observations. They do not prove an audible interruption. Hover a value or event for full details."
        }, 0, 4);
        layout.Controls.Add(_copy, 0, 5);
        layout.Controls.Add(_copyStatus, 0, 6);
        Controls.Add(layout);
        _fields.ClientSizeChanged += (_, _) => FitLastColumn(_fields);
        _history.ClientSizeChanged += (_, _) => FitLastColumn(_history);
        _fields.HandleCreated += (_, _) => FitLastColumn(_fields, force: true);
        _history.HandleCreated += (_, _) => FitLastColumn(_history, force: true);
        _copy.Click += (_, _) =>
        {
            if (!_copy.Enabled) return;
            try
            {
                _copyText(DiagnosticsPresentation.FormatSnapshot(_snapshot));
                _copyStatus.Text = "Diagnostics copied.";
            }
            catch (Exception error) when (error is ExternalException or InvalidOperationException)
            {
                _copyStatus.Text = "Clipboard unavailable. Try Copy diagnostics again.";
            }
        };
    }

    public void Render(AudioDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var fieldsChanged = !_snapshot.Fields.SequenceEqual(snapshot.Fields);
        var historyChanged = !_snapshot.History.SequenceEqual(snapshot.History);
        if (!fieldsChanged && !historyChanged) return;
        // A later change to a caller-owned list must not change what Copy diagnostics exports.
        _snapshot = new(snapshot.Fields.ToArray(), snapshot.History.ToArray());
        if (fieldsChanged) RenderFields(_snapshot.Fields);
        if (historyChanged) RenderHistory(_snapshot.History);
        _copy.Enabled = _snapshot.Fields.Count > 0 || _snapshot.History.Count > 0;
        _copyStatus.Text = "";
    }

    private void FitLastColumn(ListView list, bool force = false)
    {
        if (list.IsDisposed || list.Disposing || list.Columns.Count == 0 || list.ClientSize.Width <= 0) return;
        var used = list.Columns.Cast<ColumnHeader>().SkipLast(1).Sum(column => column.Width);
        var layout = (list.Width, list.Columns.Count, used);
        if (!force && _columnLayouts.TryGetValue(list, out var previous) && previous == layout) return;
        _columnLayouts[list] = layout;
        // Keep a scrollbar gutter: common-controls can show the vertical bar after
        // EndUpdate without another managed size event. Avoid a second scrollbar.
        var width = Math.Max(100, list.ClientSize.Width - used - SystemInformation.VerticalScrollBarWidth - 1);
        if (list.Columns[^1].Width != width) list.Columns[^1].Width = width;
    }

    private void RenderFields(IReadOnlyList<DiagnosticField> fields)
    {
        _fields.BeginUpdate();
        try
        {
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                var row = index < _fields.Items.Count ? _fields.Items[index] : _fields.Items.Add(new ListViewItem(["", ""]));
                if (row.Text != field.Name) row.Text = field.Name;
                if (row.SubItems[1].Text != field.Value) row.SubItems[1].Text = field.Value;
                row.ToolTipText = field.Name + ": " + field.Value;
            }
            while (_fields.Items.Count > fields.Count) _fields.Items.RemoveAt(_fields.Items.Count - 1);
        }
        finally { _fields.EndUpdate(); }
        FitLastColumn(_fields, force: true);
    }

    private void RenderHistory(IReadOnlyList<ControlHistoryEntry> history)
    {
        var existing = _history.Items.Cast<ListViewItem>().ToDictionary(row => (long)row.Tag!);
        _history.BeginUpdate();
        try
        {
            var rows = new List<ListViewItem>(history.Count);
            foreach (var entry in history)
            {
                if (!existing.TryGetValue(entry.Sequence, out var row))
                    row = new ListViewItem(["", "", ""]) { Tag = entry.Sequence };
                row.Text = entry.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                row.SubItems[1].Text = entry.Kind;
                row.SubItems[2].Text = entry.Detail;
                row.ToolTipText = entry.Utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    + " · " + entry.Kind + ": " + entry.Detail;
                rows.Add(row);
            }
            _history.Items.Clear();
            _history.Items.AddRange(rows.ToArray());
        }
        finally { _history.EndUpdate(); }
        FitLastColumn(_history, force: true);
    }
}
