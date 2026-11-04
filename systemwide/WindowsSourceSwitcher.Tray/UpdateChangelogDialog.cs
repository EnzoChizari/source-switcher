namespace WindowsSourceSwitcher.Tray;

internal sealed class UpdateChangelogDialog : Form
{
    public UpdateChangelogDialog(PortableRelease release)
    {
        Text = $"Update available · v{release.Version}";
        ClientSize = new Size(510, 426);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = MaximizeBox = MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(1) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            AutoSize = true, Margin = Padding.Empty, Padding = new Padding(20, 14, 20, 8), Tag = "Card" };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = Text, Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            AutoSize = true }, 0, 0);
        header.Controls.Add(new Label { Text = $"You currently have v{Program.DisplayVersion} installed.",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
        var notes = new RichTextBox
        {
            AccessibleName = "Release changelog",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            Margin = Padding.Empty,
            Tag = "Card"
        };
        var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(20, 0, 14, 14), Tag = "Card" };
        content.Controls.Add(notes);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            AutoSize = true, Margin = Padding.Empty, Padding = new Padding(20, 12, 20, 14) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var update = new Button { Dock = DockStyle.Fill, Height = 34, Text = "Update", Tag = "PrimaryAction", DialogResult = DialogResult.OK };
        var close = new Button { Dock = DockStyle.Fill, Height = 34, Text = "Close", DialogResult = DialogResult.Cancel };
        actions.Controls.Add(update, 0, 0);
        actions.Controls.Add(close, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        ActiveControl = close;
        MinimalDarkTheme.Apply(this);
        notes.BorderStyle = BorderStyle.None;
        using var headingFont = new Font(notes.Font.FontFamily, 12, FontStyle.Bold);
        var body = string.IsNullOrWhiteSpace(release.Notes) ? "No changelog was provided for this version." : release.Notes;
        foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            var contentLine = line.Trim();
            var heading = contentLine.StartsWith('#');
            notes.SelectionStart = notes.TextLength;
            notes.SelectionFont = heading ? headingFont : notes.Font;
            if (heading) contentLine = contentLine.TrimStart('#', ' ');
            else if (contentLine.StartsWith("- ") || contentLine.StartsWith("* ")) contentLine = "• " + contentLine[2..];
            notes.AppendText(contentLine + Environment.NewLine);
        }
        notes.Select(0, 0);
        Paint += (_, args) =>
        {
            using var border = new Pen(MinimalDarkTheme.Border);
            args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        base.OnShown(e);
    }
}
