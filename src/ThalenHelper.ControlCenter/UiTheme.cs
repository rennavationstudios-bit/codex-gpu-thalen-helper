using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ThalenHelper.ControlCenter;

internal enum AppButtonStyle
{
    Primary,
    Secondary,
    Quiet,
    Danger
}

internal static class UiTheme
{
    public static readonly Color Canvas = Color.FromArgb(10, 13, 19);
    public static readonly Color Surface = Color.FromArgb(17, 22, 31);
    public static readonly Color SurfaceRaised = Color.FromArgb(23, 29, 42);
    public static readonly Color SurfaceHover = Color.FromArgb(31, 39, 55);
    public static readonly Color Border = Color.FromArgb(42, 53, 73);
    public static readonly Color Text = Color.FromArgb(241, 245, 249);
    public static readonly Color Muted = Color.FromArgb(151, 164, 184);
    public static readonly Color Accent = Color.FromArgb(124, 92, 255);
    public static readonly Color AccentHover = Color.FromArgb(146, 120, 255);
    public static readonly Color Cyan = Color.FromArgb(42, 218, 199);
    public static readonly Color Success = Color.FromArgb(67, 209, 158);
    public static readonly Color Warning = Color.FromArgb(245, 185, 66);
    public static readonly Color Danger = Color.FromArgb(255, 92, 122);

    public static Font DisplayFont(float size, FontStyle style = FontStyle.Regular)
        => new("Segoe UI Semibold", size, style, GraphicsUnit.Point);

    public static Font BodyFont(float size = 9.5F, FontStyle style = FontStyle.Regular)
        => new("Segoe UI", size, style, GraphicsUnit.Point);

    public static void Apply(Form form, Size minimumSize)
    {
        form.BackColor = Canvas;
        form.ForeColor = Text;
        form.Font = BodyFont();
        form.MinimumSize = minimumSize;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.ShowIcon = true;
        form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    public static Label Label(
        string text,
        float size = 9.5F,
        Color? color = null,
        FontStyle style = FontStyle.Regular)
        => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = color ?? Text,
            Font = BodyFont(size, style),
            BackColor = Color.Transparent
        };

    public static Label SectionLabel(string text)
        => new()
        {
            Text = text.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Muted,
            Font = BodyFont(8.25F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent
        };

    public static Button Button(string text, AppButtonStyle style = AppButtonStyle.Secondary)
    {
        var button = new RoundedButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(112, 40),
            Padding = new Padding(14, 7, 14, 7),
            Margin = new Padding(0, 0, 10, 10),
            FlatStyle = FlatStyle.Flat,
            Font = BodyFont(9.25F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            AccessibleRole = AccessibleRole.PushButton
        };
        button.FlatAppearance.BorderSize = 0;
        ApplyButtonColors(button, style, hovered: false);
        button.MouseEnter += (_, _) => ApplyButtonColors(button, style, hovered: true);
        button.MouseLeave += (_, _) => ApplyButtonColors(button, style, hovered: false);
        button.EnabledChanged += (_, _) => ApplyButtonColors(button, style, hovered: false);
        return button;
    }

    public static ToggleSwitch Toggle(string accessibleName)
        => new()
        {
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.CheckButton
        };

    public static TextBox TextBox(int width = 520)
        => new()
        {
            Width = width,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SurfaceRaised,
            ForeColor = Text,
            Font = BodyFont(10F),
            Margin = new Padding(0, 0, 10, 10)
        };

    public static ComboBox ComboBox(int width = 480)
        => new()
        {
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = SurfaceRaised,
            ForeColor = Text,
            FlatStyle = FlatStyle.Flat,
            Font = BodyFont(10F),
            Margin = new Padding(0, 0, 0, 10)
        };

    public static CheckBox CheckBox(string text, bool isChecked = false)
        => new()
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            ForeColor = Text,
            Font = BodyFont(9.5F),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 3, 0, 8),
            Cursor = Cursors.Hand
        };

    public static RadioButton RadioButton(string text, bool isChecked = false)
        => new()
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            ForeColor = Text,
            Font = BodyFont(9.5F, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 4, 0, 7),
            Cursor = Cursors.Hand
        };

    public static DarkToolTip ToolTip() => new();

    private static void ApplyButtonColors(Button button, AppButtonStyle style, bool hovered)
    {
        if (!button.Enabled)
        {
            button.BackColor = Surface;
            button.ForeColor = Color.FromArgb(91, 103, 122);
            button.FlatAppearance.BorderColor = Border;
            return;
        }

        (button.BackColor, button.ForeColor, button.FlatAppearance.BorderColor) = style switch
        {
            AppButtonStyle.Primary => (hovered ? AccentHover : Accent, Color.White, hovered ? AccentHover : Accent),
            AppButtonStyle.Danger => (hovered ? Color.FromArgb(77, 31, 45) : SurfaceRaised, Danger, hovered ? Danger : Color.FromArgb(90, 46, 60)),
            AppButtonStyle.Quiet => (hovered ? SurfaceHover : Canvas, Muted, hovered ? Border : Canvas),
            _ => (hovered ? SurfaceHover : SurfaceRaised, Text, hovered ? Color.FromArgb(71, 84, 111) : Border)
        };
    }
}

internal sealed class RoundedButton : Button
{
    private const int LogicalCornerRadius = 12;

    public RoundedButton()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var path = RoundedPanel.RoundedRectangle(ClientRectangle, ScaledCornerRadius(), 1F);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(FlatAppearance.BorderColor, 1F);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, path);

        var flags = TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(eventArgs.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(ClientRectangle, -4, -4);
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, focus, ForeColor, Color.Transparent);
        }
    }

    private int ScaledCornerRadius()
        => Math.Max(6, LogicalCornerRadius * DeviceDpi / 96);
}

internal sealed class DarkToolTip : ToolTip
{
    private const int MaximumWidth = 320;
    private readonly Font _font = UiTheme.BodyFont(9F);

    public DarkToolTip()
    {
        AutoPopDelay = 6_000;
        InitialDelay = 550;
        ReshowDelay = 120;
        ShowAlways = true;
        OwnerDraw = true;
        Popup += (_, args) =>
        {
            var text = GetToolTip(args.AssociatedControl);
            var measured = TextRenderer.MeasureText(
                text,
                _font,
                new Size(MaximumWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            args.ToolTipSize = new Size(Math.Min(MaximumWidth, measured.Width) + 26, measured.Height + 22);
        };
        Draw += (_, args) =>
        {
            using var background = new SolidBrush(UiTheme.SurfaceRaised);
            using var border = new Pen(UiTheme.Border);
            args.Graphics.FillRectangle(background, args.Bounds);
            args.Graphics.DrawRectangle(border, 0, 0, args.Bounds.Width - 1, args.Bounds.Height - 1);
            TextRenderer.DrawText(
                args.Graphics,
                args.ToolTipText,
                _font,
                Rectangle.Inflate(args.Bounds, -12, -9),
                UiTheme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 18;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color OutlineColor { get; set; } = UiTheme.Border;

    public RoundedPanel()
    {
        BackColor = UiTheme.Surface;
        Padding = new Padding(20);
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        eventArgs.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas);
        using var path = RoundedRectangle(ClientRectangle, ScaledCornerRadius(), 1F);
        using var fill = new SolidBrush(BackColor);
        eventArgs.Graphics.FillPath(fill, path);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var path = RoundedRectangle(ClientRectangle, ScaledCornerRadius(), 1F);
        using var pen = new Pen(OutlineColor);
        eventArgs.Graphics.DrawPath(pen, path);
    }

    protected int ScaledCornerRadius()
        => Math.Max(8, CornerRadius * DeviceDpi / 96);

    internal static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius, float inset = 0F)
    {
        var path = new GraphicsPath();
        var bounds = RectangleF.Inflate(rectangle, -inset, -inset);
        if (bounds.Width <= 1F || bounds.Height <= 1F)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var clampedRadius = Math.Max(1F, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2F));
        var diameter = clampedRadius * 2F;
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class GradientPanel : RoundedPanel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GradientStart { get; set; } = Color.FromArgb(32, 25, 63);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GradientEnd { get; set; } = UiTheme.Surface;

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        eventArgs.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas);
        using var path = RoundedRectangle(ClientRectangle, ScaledCornerRadius(), 1F);
        using var brush = new LinearGradientBrush(ClientRectangle, GradientStart, GradientEnd, 18F);
        eventArgs.Graphics.FillPath(brush, path);

        using var glow = new Pen(Color.FromArgb(34, UiTheme.Cyan), 1F);
        for (var index = 0; index < 4; index++)
        {
            var y = 30 + index * 22;
            eventArgs.Graphics.DrawLine(glow, Width - 260, y, Width - 40, y + 35);
        }
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    private const int LogicalWidth = 52;
    private const int LogicalHeight = 28;

    public ToggleSwitch()
    {
        Appearance = Appearance.Button;
        AutoSize = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
        Text = string.Empty;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        UpdateLogicalSize();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        UpdateLogicalSize();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var bounds = RectangleF.Inflate(ClientRectangle, -1F, -1F);
        var radius = bounds.Height / 2F;
        using var track = RoundedPanel.RoundedRectangle(Rectangle.Round(bounds), (int)radius, 0.5F);
        using var trackBrush = new SolidBrush(Enabled
            ? Checked ? UiTheme.Accent : UiTheme.SurfaceHover
            : UiTheme.Surface);
        using var trackPen = new Pen(Enabled && Checked ? UiTheme.AccentHover : UiTheme.Border, 1F);
        eventArgs.Graphics.FillPath(trackBrush, track);
        eventArgs.Graphics.DrawPath(trackPen, track);

        var thumbSize = bounds.Height - Scale(6);
        var thumbX = Checked ? bounds.Right - thumbSize - Scale(3) : bounds.Left + Scale(3);
        var thumbY = bounds.Top + (bounds.Height - thumbSize) / 2F;
        using var thumbBrush = new SolidBrush(Enabled ? Color.White : UiTheme.Muted);
        eventArgs.Graphics.FillEllipse(thumbBrush, thumbX, thumbY, thumbSize, thumbSize);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
    }

    private int Scale(int value) => Math.Max(1, value * DeviceDpi / 96);

    private void UpdateLogicalSize()
    {
        Size = new Size(Scale(LogicalWidth), Scale(LogicalHeight));
        MinimumSize = Size;
        MaximumSize = Size;
    }
}
