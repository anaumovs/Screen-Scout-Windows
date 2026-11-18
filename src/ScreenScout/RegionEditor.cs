using ScoutCore;

namespace ScreenScout;

internal sealed class RegionEditor : Form
{
    private readonly MovementDisplay display;
    public List<MovementBox> Boxes { get; }
    private Guid? selected;
    private PointD? origin;
    private RectD? original;
    private int? corner;
    private readonly ComboBox tool = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Label info = MainForm.TextLabel("", 700);
    private readonly Button save;
    private readonly FlowLayoutPanel toolbar = new();
    public RegionEditor(MovementDisplay display, RegionProfile? profile, bool secondary = false)
    {
        this.display = display; Boxes = profile?.Boxes.Select(b => b with { }).ToList() ?? [];
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
        Bounds = new Rectangle((int)display.Bounds.X, (int)display.Bounds.Y, (int)display.Bounds.Width, (int)display.Bounds.Height);
        TopMost = true; ShowInTaskbar = false; KeyPreview = true; DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 26, 34); ForeColor = Color.WhiteSmoke; Opacity = .86;
        toolbar.Dock = DockStyle.Bottom; toolbar.Height = Math.Min(210, Height / 2); toolbar.BackColor = MainForm.BackgroundColor;
        toolbar.FlowDirection = FlowDirection.TopDown; toolbar.WrapContents = false; toolbar.AutoScroll = true; toolbar.Padding = new Padding(14);
        toolbar.Controls.Add(MainForm.TextLabel("Green: large movement allowed · Red: blocked · Purple: secondary click", 800, 12, Color.WhiteSmoke));
        toolbar.Controls.Add(MainForm.TextLabel("Drag to draw. In Move / resize, drag a box or its corner handles. No allowed boxes means the whole display minus blocked boxes. Maximum 32 movement boxes plus one purple area.", 900));
        tool.Items.AddRange(["Draw allowed", "Draw blocked", "Move / resize", "Draw secondary"]); tool.SelectedIndex = secondary ? 3 : 0;
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true }; row.Controls.Add(tool);
        row.Controls.Add(MainForm.ActionButton("Delete selected", Delete));
        row.Controls.Add(MainForm.ActionButton("Clear", () => { Boxes.Clear(); selected = null; RefreshDraft(); }));
        row.Controls.Add(MainForm.ActionButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }));
        save = MainForm.ActionButton("Save", () => { if (!Valid) return; DialogResult = DialogResult.OK; Close(); }); row.Controls.Add(save);
        toolbar.Controls.Add(row); toolbar.Controls.Add(info);
        toolbar.Controls.Add(MainForm.TextLabel("Automation stays paused after Save or Cancel. Move this toolbar with F2 (top/bottom) to edit the area behind it.", 900));
        Controls.Add(toolbar);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) Delete(); if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.F2) toolbar.Dock = toolbar.Dock == DockStyle.Bottom ? DockStyle.Top : DockStyle.Bottom; };
        RefreshDraft();
    }
    private bool Valid => new RegionProfile(display.Identity, display.Bounds.Size, Boxes).DomainFor(display) is not null;
    private void RefreshDraft()
    {
        save.Enabled = Valid; info.Text = $"{Boxes.Count(b => !b.Secondary)} / 32 movement boxes · {Boxes.Count(b => b.Secondary)} / 1 secondary" + (Valid ? "" : " — Correct out-of-bounds or invalid boxes before saving.");
        Invalidate();
    }
    private static PointD[] Corners(RectD r) => [new(r.X, r.Y), new(r.Right, r.Y), new(r.Right, r.Bottom), new(r.X, r.Bottom)];
    private PointD Local(MouseEventArgs e) => new(Math.Clamp(e.X, 0, display.Bounds.Width), Math.Clamp(e.Y, 0, display.Bounds.Height));
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        foreach (var b in Boxes)
        {
            var r = new RectangleF((float)b.Rect.X, (float)b.Rect.Y, (float)b.Rect.Width, (float)b.Rect.Height);
            var color = b.Secondary ? Color.MediumPurple : b.Blocked ? Color.Tomato : Color.MediumSpringGreen;
            using var fill = new SolidBrush(Color.FromArgb(65, color)); using var pen = new Pen(color, b.Id == selected ? 3 : 2);
            e.Graphics.FillRectangle(fill, r); e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            using var text = new SolidBrush(color); e.Graphics.DrawString(b.Secondary ? "SECONDARY" : b.Blocked ? "BLOCKED" : "ALLOWED", Font, text, r.X + 5, r.Y + 5);
            if (b.Id == selected) foreach (var p in Corners(b.Rect)) e.Graphics.FillRectangle(Brushes.White, (float)p.X - 5, (float)p.Y - 5, 10, 10);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        var p = Local(e); origin = p; original = null; corner = null;
        if (tool.SelectedIndex != 2)
        {
            if (tool.SelectedIndex == 3) Boxes.RemoveAll(b => b.Secondary);
            if (tool.SelectedIndex != 3 && Boxes.Count(b => !b.Secondary) >= 32) { origin = null; return; }
            var box = new MovementBox(new(p.X, p.Y, 0, 0), tool.SelectedIndex == 1) { Secondary = tool.SelectedIndex == 3 }; Boxes.Add(box); selected = box.Id;
        }
        else
        {
            var previous = Boxes.FirstOrDefault(b => b.Id == selected);
            var k = previous is null ? -1 : Array.FindIndex(Corners(previous.Rect), c => PointD.Distance(c, p) < 10);
            if (k >= 0) { corner = k; original = previous!.Rect; }
            else if (Boxes.LastOrDefault(b => b.Rect.Contains(p)) is { } box) { selected = box.Id; original = box.Rect; }
            else { selected = null; origin = null; }
        }
        Capture = origin is not null; RefreshDraft();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (origin is not { } start) return;
        int index = Boxes.FindIndex(b => b.Id == selected); if (index < 0) return;
        var p = Local(e); RectD rect;
        if (original is { } old)
        {
            if (corner is { } k) { var opposite = Corners(old)[(k + 2) % 4]; rect = FromPoints(p, opposite); }
            else rect = old with { X = Math.Max(0, Math.Min(display.Bounds.Width - old.Width, old.X + p.X - start.X)), Y = Math.Max(0, Math.Min(display.Bounds.Height - old.Height, old.Y + p.Y - start.Y)) };
        }
        else rect = FromPoints(start, p);
        Boxes[index] = Boxes[index] with { Rect = rect }; RefreshDraft();
    }
    private static RectD FromPoints(PointD a, PointD b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
        OnMouseMove(e); origin = null; original = null; corner = null; Capture = false;
        Boxes.RemoveAll(b => b.Rect.Width < .1 || b.Rect.Height < .1);
        if (!Boxes.Any(b => b.Id == selected)) selected = null; RefreshDraft();
    }
    private void Delete() { Boxes.RemoveAll(b => b.Id == selected); selected = null; RefreshDraft(); }
}
