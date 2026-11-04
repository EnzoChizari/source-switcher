using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

internal sealed class FirstRunTutorial : IDisposable, IMessageFilter
{
    internal sealed record Step(int Tab, Control Target, string Title, string Body);

    private readonly Form _owner;
    private readonly TabControl _tabs;
    private readonly Step[] _steps;
    private readonly Action _finished;
    private readonly SectionCard _bubble = new() { AccessibleName = "Quick tour hint", Cursor = Cursors.Hand, Margin = Padding.Empty };
    private readonly Panel _outline = new() { Enabled = false, TabStop = false };
    private readonly Label _progress = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _title = new() { AutoSize = true };
    private readonly Label _body = new() { AutoSize = true };
    private readonly Button _back = new() { Text = "Back", AutoSize = true };
    private readonly Button _skip = new() { Text = "Skip", AutoSize = true };
    private readonly Button _next = new() { Text = "Next", AutoSize = true };
    private readonly TableLayoutPanel _text = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
    private readonly Font _titleFont;
    private readonly ScrollableControl[] _scrollParents;
    private Rectangle _highlight;
    private Rectangle _connector;
    private int _index;
    private bool _changingStep, _placing, _pressedTarget, _disposed;

    public FirstRunTutorial(Form owner, TabControl tabs, Step[] steps, Action finished)
    {
        _owner = owner;
        _tabs = tabs;
        _steps = steps;
        _finished = finished;
        _titleFont = new Font(owner.Font, FontStyle.Bold);
        _title.Font = _titleFont;
        _bubble.AccessibleDescription = "Click the hint or highlighted control to advance. Use Back to review or Skip to close.";
        _bubble.Padding = new Padding(Pixels(12));
        _bubble.AutoScroll = true;

        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Margin = Padding.Empty };
        footer.Controls.AddRange([_back, _skip, _next]);
        _text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var control in new Control[] { _progress, _title, _body, footer }) _text.Controls.Add(control);
        _bubble.Controls.Add(_text);
        foreach (var control in new Control[] { _bubble, _text, _progress, _title, _body, footer })
            control.MouseClick += BubbleClicked;
        _back.Click += (_, _) => ShowStep(_index - 1);
        _next.Click += (_, _) => Next();
        _skip.Click += (_, _) => _finished();

        owner.Controls.Add(_outline);
        owner.Controls.Add(_bubble);
        ApplyBubblePalette(footer);
        _outline.BackColor = MinimalDarkTheme.Accent;
        _outline.BringToFront();
        _bubble.BringToFront();
        _scrollParents = steps.SelectMany(step => Ancestors(step.Target)).OfType<ScrollableControl>()
            .Where(control => control.AutoScroll).Distinct().ToArray();
        foreach (var parent in _scrollParents) parent.Scroll += Scrolled;
        owner.Layout += OwnerLayout;
        tabs.SelectedIndexChanged += TabChanged;
        Application.AddMessageFilter(this);
    }

    private void ApplyBubblePalette(FlowLayoutPanel footer)
    {
        var highContrast = SystemInformation.HighContrast;
        var fill = highContrast ? SystemColors.Highlight : Color.FromArgb(255, 194, 91);
        var ink = highContrast ? SystemColors.HighlightText : Color.FromArgb(38, 25, 13);
        _bubble.FillColor = fill;
        foreach (var control in new Control[] { _bubble, _text, _progress, _title, _body, footer })
        {
            control.BackColor = fill;
            control.ForeColor = ink;
        }
        foreach (var button in new[] { _back, _skip, _next })
        {
            button.BackColor = MinimalDarkTheme.NavigationSurface;
            button.ForeColor = MinimalDarkTheme.Text;
            button.FlatAppearance.BorderColor = MinimalDarkTheme.Border;
            button.FlatAppearance.MouseOverBackColor = highContrast ? SystemColors.ControlLight : MinimalDarkTheme.HoverSurface;
            button.FlatAppearance.MouseDownBackColor = highContrast ? SystemColors.ControlLight : MinimalDarkTheme.Background;
        }
    }

    public void Start() => ShowStep(0);

    public void Focus() => _next.Focus();

    private void ShowStep(int index)
    {
        if (_disposed || index < 0 || index >= _steps.Length) return;
        _changingStep = true;
        try
        {
            _index = index;
            _pressedTarget = false;
            var step = _steps[index];
            _tabs.SelectedIndex = step.Tab;
            _progress.Text = $"{index + 1} of {_steps.Length}";
            _title.Text = step.Title;
            _body.Text = step.Body;
            _back.Enabled = index > 0;
            _next.Text = index == _steps.Length - 1 ? "Done" : "Next";
            foreach (var scroll in Ancestors(step.Target).OfType<ScrollableControl>().Where(parent => parent.AutoScroll))
                scroll.ScrollControlIntoView(step.Target);
        }
        finally { _changingStep = false; }
        PlaceHint();
        Focus();
    }

    private void Next()
    {
        if (_disposed) return;
        if (_index == _steps.Length - 1) _finished();
        else ShowStep(_index + 1);
    }

    private void TabChanged(object? sender, EventArgs args)
    {
        if (_changingStep || _disposed || _steps[_index].Tab == _tabs.SelectedIndex) return;
        ShowStep(Array.FindIndex(_steps, step => step.Tab == _tabs.SelectedIndex));
    }

    private void BubbleClicked(object? sender, MouseEventArgs args)
    {
        if (args.Button == MouseButtons.Left) Next();
    }

    private void OwnerLayout(object? sender, LayoutEventArgs args) => PlaceHint();
    private void Scrolled(object? sender, ScrollEventArgs args) => PlaceHint();

    private static IEnumerable<Control> Ancestors(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent) yield return parent;
    }

    private int Pixels(int value) => (int)Math.Round(value * _owner.DeviceDpi / 96d);

    private void PlaceHint()
    {
        if (_disposed || _placing || _changingStep || !_owner.Visible) return;
        _placing = true;
        try
        {
            var area = Rectangle.Inflate(_owner.ClientRectangle, -Pixels(10), -Pixels(10));
            if (area.Width <= 0 || area.Height <= 0) return;
            var target = _steps[_index].Target;
            var anchor = target.Visible ? _owner.RectangleToClient(target.RectangleToScreen(target.ClientRectangle)) : Rectangle.Empty;
            foreach (var parent in Ancestors(target))
                anchor.Intersect(_owner.RectangleToClient(parent.RectangleToScreen(parent.ClientRectangle)));

            var width = Math.Min(Pixels(300), area.Width);
            var textWidth = Math.Max(1, width - _bubble.Padding.Horizontal - Pixels(8));
            _title.MaximumSize = _body.MaximumSize = new Size(textWidth, 0);
            _text.Width = textWidth;
            var height = Math.Min(area.Height, _text.GetPreferredSize(new Size(textWidth, 0)).Height + _bubble.Padding.Vertical);
            var gap = Pixels(16);
            var left = Math.Clamp(anchor.Left, area.Left, area.Right - width);
            var top = Math.Clamp(anchor.Top, area.Top, area.Bottom - height);
            Rectangle[] candidates = [new(anchor.Right + gap, top, width, height), new(left, anchor.Bottom + gap, width, height),
                new(left, anchor.Top - gap - height, width, height), new(anchor.Left - gap - width, top, width, height)];
            var bounds = anchor.Width > 0 && anchor.Height > 0 ? candidates.FirstOrDefault(area.Contains) : Rectangle.Empty;
            if (bounds.IsEmpty) bounds = new Rectangle(area.Right - width, area.Bottom - height, width, height);
            if (_bubble.Bounds != bounds) _bubble.Bounds = bounds;
            DrawOutline(anchor, bounds);
        }
        finally { _placing = false; }
    }

    private void DrawOutline(Rectangle anchor, Rectangle bubble)
    {
        var stroke = Pixels(2);
        var hasAnchor = anchor.Width > 0 && anchor.Height > 0;
        var highlight = hasAnchor ? Rectangle.Inflate(anchor, stroke, stroke) : Rectangle.Empty;
        var connector = Rectangle.Empty;
        if (hasAnchor && (bubble.Left >= highlight.Right || bubble.Right <= highlight.Left))
        {
            var left = Math.Min(highlight.Right, bubble.Right);
            var right = Math.Max(highlight.Left, bubble.Left);
            var y = Math.Clamp(anchor.Top + anchor.Height / 2, bubble.Top + stroke, bubble.Bottom - stroke);
            connector = new Rectangle(left, y, right - left, stroke);
        }
        else if (hasAnchor && (bubble.Top >= highlight.Bottom || bubble.Bottom <= highlight.Top))
        {
            var top = Math.Min(highlight.Bottom, bubble.Bottom);
            var bottom = Math.Max(highlight.Top, bubble.Top);
            var x = Math.Clamp(anchor.Left + anchor.Width / 2, bubble.Left + stroke, bubble.Right - stroke);
            connector = new Rectangle(x, top, stroke, bottom - top);
        }
        if (_highlight == highlight && _connector == connector && _outline.Bounds == _owner.ClientRectangle) return;
        _highlight = highlight;
        _connector = connector;
        var region = new Region(highlight);
        region.Exclude(anchor);
        region.Union(connector);
        var previous = _outline.Region;
        _outline.Bounds = _owner.ClientRectangle;
        _outline.Region = region;
        previous?.Dispose();
    }

    public bool PreFilterMessage(ref Message message)
    {
        const int leftDown = 0x0201, leftUp = 0x0202, leftDoubleClick = 0x0203;
        if (_disposed || !_owner.Visible || message.Msg is not (leftDown or leftUp or leftDoubleClick)) return false;
        var control = Control.FromChildHandle(message.HWnd);
        if (control is null || (control != _owner && !_owner.Contains(control))) return false;
        if (control == _bubble || _bubble.Contains(control)) return false;
        var packed = message.LParam.ToInt64();
        var point = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
        if (!ClientToScreen(message.HWnd, ref point)) return false;
        var onTarget = _highlight.Contains(_owner.PointToClient(point));
        if (message.Msg != leftUp)
        {
            _pressedTarget = onTarget;
            return onTarget;
        }
        if (!_pressedTarget) return false;
        _pressedTarget = false;
        if (onTarget) Next();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Application.RemoveMessageFilter(this);
        _owner.Layout -= OwnerLayout;
        _tabs.SelectedIndexChanged -= TabChanged;
        foreach (var parent in _scrollParents) parent.Scroll -= Scrolled;
        _bubble.Dispose();
        _outline.Dispose();
        _titleFont.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
}
