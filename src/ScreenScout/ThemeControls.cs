using System.Drawing.Drawing2D;

namespace ScreenScout;

internal static class ThemeControls
{
    public static readonly Color Border = Color.FromArgb(61, 76, 91);
    public static readonly Color Inactive = Color.FromArgb(155, 171, 190);

    // Do not disable a whole container: WinForms then paints every child label black.
    public static void SetAvailable(Control control, bool available)
    {
        if (control is Panel)
        {
            foreach (Control child in control.Controls) SetAvailable(child, available);
        }
        else if (control is Label) { /* Labels always remain readable. */ }
        else if (control is ThemeNumber number) number.SetAvailable(available);
        else if (control.Enabled != available) control.Enabled = available;
    }
}

internal sealed class ThemeNumber : NumericUpDown
{
    private bool available = true;
    public void SetAvailable(bool value)
    {
        if (available == value) return;
        available = value; ReadOnly = !value; InterceptArrowKeys = value; TabStop = value;
        ForeColor = value ? Color.WhiteSmoke : ThemeControls.Inactive;
        foreach (Control child in Controls) if (child is not TextBox) child.Enabled = value;
    }
    public override void UpButton() { if (available) base.UpButton(); }
    public override void DownButton() { if (available) base.DownButton(); }
}

internal sealed class ThemeCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var size = Math.Max(14, (int)(16 * DeviceDpi / 96f));
        var box = new Rectangle(0, (Height - size) / 2, size, size);
        using var fill = new SolidBrush(Checked && Enabled ? MainForm.Accent : MainForm.CardColor);
        using var border = new Pen(Enabled ? MainForm.Accent : ThemeControls.Inactive);
        e.Graphics.FillRectangle(fill, box); e.Graphics.DrawRectangle(border, box);
        if (Checked)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var check = new Pen(Enabled ? MainForm.BackgroundColor : ThemeControls.Inactive, 2);
            e.Graphics.DrawLines(check, [new PointF(box.X + size * .2f, box.Y + size * .5f), new PointF(box.X + size * .43f, box.Y + size * .73f), new PointF(box.X + size * .82f, box.Y + size * .27f)]);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(size + 9, 0, Width - size - 9, Height),
            Enabled ? ForeColor : ThemeControls.Inactive, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new(text.Width + (int)(32 * DeviceDpi / 96f), Math.Max(text.Height + 8, 28));
    }
}

internal sealed class ThemeComboBox : ComboBox
{
    public ThemeComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed; FlatStyle = FlatStyle.Flat;
        BackColor = MainForm.CardColor; ForeColor = Color.WhiteSmoke;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        using var background = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(44, 64, 75) : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -5, 0),
            Enabled ? ForeColor : ThemeControls.Inactive, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }
}

internal sealed class ThemeButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(BackColor);
        using var border = new Pen(ThemeControls.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ThemeControls.Inactive,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
