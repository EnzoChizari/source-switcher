namespace WindowsSourceSwitcher.Tray;

internal sealed class UpdateChangelogDialog : Form
{
    public UpdateChangelogDialog(PortableRelease release)
    {
        Text = $"What's new in v{release.Version}";
        ClientSize = new Size(430, 254);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = MaximizeBox = MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var notes = new TextBox
        {
            AccessibleName = "Release changelog",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "No changelog was provided for this version."
                : release.Notes.ReplaceLineEndings(Environment.NewLine)
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            WrapContents = false, Margin = new Padding(0, 10, 0, 0)
        };
        var update = new Button { AutoSize = true, Text = "Update", DialogResult = DialogResult.OK };
        var close = new Button { AutoSize = true, Text = "Close", DialogResult = DialogResult.Cancel };
        actions.Controls.Add(update);
        actions.Controls.Add(close);
        layout.Controls.Add(notes, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        Controls.Add(layout);
        CancelButton = close;
        ActiveControl = close;
        MinimalDarkTheme.Apply(this);
    }
}
