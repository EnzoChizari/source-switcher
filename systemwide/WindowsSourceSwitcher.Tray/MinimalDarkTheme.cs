using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

/// <summary>
/// A small, local WinForms palette. Existing controls retain their input, native accessibility
/// and automation contracts. No animation, timer, browser runtime or background worker is added.
/// </summary>
internal static class MinimalDarkTheme
{
    public static Color Background => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(36, 36, 36);
    public static Color NavigationSurface => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(29, 29, 29);
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(47, 47, 47);
    public static Color RaisedSurface => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(53, 53, 53);
    public static Color HoverSurface => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(63, 63, 63);
    public static Color Text => SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(239, 239, 242);
    public static Color MutedText => SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(166, 166, 177);
    public static Color Border => SystemInformation.HighContrast ? SystemColors.WindowFrame : Color.FromArgb(63, 63, 70);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(255, 185, 73);
    public static Color SelectedSurface => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(69, 59, 42);
    public static Color SelectedText => SystemInformation.HighContrast ? SystemColors.HighlightText : Text;
    private static readonly ConditionalWeakTable<Control, Attachment> Attachments = new();

    /// <summary>
    /// Apply once after the view is constructed. Later child controls are styled as they are added.
    /// Reapplying refreshes the palette without installing duplicate painting/event handlers.
    /// Font, bounds, tab order, selection, values and event subscriptions are left to the view.
    /// </summary>
    public static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.IsDisposed) return;
        var attachment = Attachments.GetValue(root, static _ => new Attachment());
        if (!attachment.Wired)
        {
            attachment.Wired = true;
            root.ControlAdded += ChildAdded;
            root.HandleCreated += HandleCreated;
            root.HandleDestroyed += HandleDestroyed;
            root.Disposed += ControlDisposed;
            if (root is ComboBox combo) combo.DrawItem += DrawComboItem;
            if (root is ListView list)
            {
                list.DrawColumnHeader += DrawListHeader;
                list.DrawItem += DrawListItem;
                list.DrawSubItem += DrawListSubItem;
            }
        }

        var wasMuted = root.ForeColor == Color.DimGray || root.ForeColor == Color.Gray || root.ForeColor == MutedText;
        root.ForeColor = wasMuted ? MutedText : Text;
        var card = false;
        for (Control? ancestor = root; ancestor is not null; ancestor = ancestor.Parent)
            if (Equals(ancestor.Tag, "Card")) { card = true; break; }
        root.BackColor = Equals(root.Tag, "Navigation") || root is SidebarButton ? NavigationSurface
            : card || root is ListView or ComboBox or TextBoxBase ? Surface : Background;
        switch (root)
        {
            case Button button:
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Equals(button.Tag, "WindowChrome") ? Background : RaisedSurface;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.BorderSize = Equals(button.Tag, "WindowChrome") ? 0 : 1;
                button.FlatAppearance.MouseOverBackColor = HoverSurface;
                button.FlatAppearance.MouseDownBackColor = SelectedSurface;
                break;
            case CheckBox checkbox:
                checkbox.FlatStyle = FlatStyle.Flat;
                checkbox.FlatAppearance.BorderColor = Border;
                checkbox.FlatAppearance.MouseOverBackColor = HoverSurface;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                break;
            case ListView list:
                list.BorderStyle = BorderStyle.None;
                list.OwnerDraw = !SystemInformation.HighContrast;
                break;
            case TextBoxBase text:
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ToolStrip strip:
                strip.Renderer = new DarkToolStripRenderer();
                strip.BackColor = Surface;
                break;
        }
        if (root is Form form && form.IsHandleCreated) ApplyWindowChrome(form);
        AttachListHeader(root, attachment);
        AttachScrollBars(root, attachment);
        foreach (Control child in root.Controls) Apply(child);
        root.Invalidate();
    }

    /// <summary>Use documented Windows 11 DWM attributes while retaining the normal system window.</summary>
    private static void ApplyWindowChrome(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var dark = SystemInformation.HighContrast ? 0 : 1;
        var corners = 2; // DWMWCP_ROUND; DWM can decline for unsupported window types.
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 33, ref corners, sizeof(int));
    }

    private static void ChildAdded(object? sender, ControlEventArgs args)
    {
        if (args.Control is not null) Apply(args.Control);
    }

    private static void HandleCreated(object? sender, EventArgs args)
    {
        if (sender is Form form) ApplyWindowChrome(form);
        if (sender is Control control && Attachments.TryGetValue(control, out var attachment))
        {
            AttachListHeader(control, attachment);
            AttachScrollBars(control, attachment);
        }
    }

    private static void AttachListHeader(Control control, Attachment attachment)
    {
        if (control is ListView { IsHandleCreated: true, View: View.Details } list
            && attachment.Header is null && !SystemInformation.HighContrast)
            attachment.Header = DarkListHeader.Attach(list);
    }

    private static void AttachScrollBars(Control control, Attachment attachment)
    {
        if (!control.IsHandleCreated || control.IsDisposed
            || control is not (ListView or ScrollableControl { AutoScroll: true }
                or TextBox { Multiline: true, ScrollBars: not ScrollBars.None })) return;
        if (attachment.ScrollBars?.IsAttachedTo(control.Handle) != true)
        {
            attachment.ScrollBars?.Dispose();
            attachment.ScrollBars = new DarkScrollBarPainter(control.Handle);
        }
        attachment.ScrollBars.Redraw();
    }

    private static void HandleDestroyed(object? sender, EventArgs args)
    {
        if (sender is not Control control || !Attachments.TryGetValue(control, out var attachment)) return;
        attachment.ScrollBars?.Dispose();
        attachment.ScrollBars = null;
        attachment.Header?.Dispose();
        attachment.Header = null;
    }

    private static void ControlDisposed(object? sender, EventArgs args)
    {
        HandleDestroyed(sender, args);
        if (sender is Control control) Attachments.Remove(control);
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs args)
    {
        if (sender is not ComboBox combo) return;
        var selected = (args.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? SelectedSurface : Surface);
        args.Graphics.FillRectangle(background, args.Bounds);
        var text = args.Index >= 0 && args.Index < combo.Items.Count ? combo.GetItemText(combo.Items[args.Index]) : combo.Text;
        var bounds = Rectangle.Inflate(args.Bounds, -5, 0);
        TextRenderer.DrawText(args.Graphics, text, args.Font ?? combo.Font, bounds,
            !combo.Enabled ? MutedText : selected ? SelectedText : Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
    }

    private static void DrawListHeader(object? sender, DrawListViewColumnHeaderEventArgs args)
    {
        if (sender is not ListView list) return;
        using var background = new SolidBrush(Background);
        args.Graphics.FillRectangle(background, args.Bounds);
        using var border = new Pen(Border);
        args.Graphics.DrawLine(border, args.Bounds.Left, args.Bounds.Bottom - 1, args.Bounds.Right, args.Bounds.Bottom - 1);
        var bounds = args.Bounds;
        bounds.Inflate(-8, 0);
        TextRenderer.DrawText(args.Graphics, args.Header?.Text ?? "", list.Font, bounds, MutedText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static void DrawListItem(object? sender, DrawListViewItemEventArgs args)
    {
        // Details rows are painted one subitem at a time. Other view modes retain native painting.
        if (sender is ListView { View: not View.Details }) args.DrawDefault = true;
    }

    private static void DrawListSubItem(object? sender, DrawListViewSubItemEventArgs args)
    {
        if (sender is not ListView list) return;
        var selected = args.Item?.Selected == true;
        using var background = new SolidBrush(selected ? SelectedSurface : Surface);
        args.Graphics.FillRectangle(background, args.Bounds);
        var bounds = args.Bounds;
        bounds.Inflate(-8, 0);
        var align = args.Header?.TextAlign switch
        {
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            HorizontalAlignment.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        TextRenderer.DrawText(args.Graphics, args.SubItem?.Text ?? "", list.Font, bounds,
            selected ? SelectedText : list.ForeColor,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (args.ColumnIndex == 0 && list.Focused && args.Item?.Focused == true)
            ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(args.Bounds, -1, -1), Text, selected ? SelectedSurface : Surface);
    }

    private sealed class Attachment
    {
        public bool Wired;
        public DarkScrollBarPainter? ScrollBars;
        public DarkListHeader? Header;
    }

    private sealed class DarkToolStripRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs args)
        {
            args.TextColor = args.Item.Enabled ? Text : MutedText;
            base.OnRenderItemText(args);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => SelectedSurface;
        public override Color MenuItemSelectedGradientBegin => SelectedSurface;
        public override Color MenuItemSelectedGradientEnd => SelectedSurface;
        public override Color MenuItemPressedGradientBegin => SelectedSurface;
        public override Color MenuItemPressedGradientMiddle => SelectedSurface;
        public override Color MenuItemPressedGradientEnd => SelectedSurface;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
