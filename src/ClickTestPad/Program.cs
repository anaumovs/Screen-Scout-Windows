using System.Diagnostics;
using System.Text.Json;

namespace ClickTestPad;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Pad(args));
    }
}
internal sealed record PadEvent(string Kind, int X, int Y, string Button, string Key, double Time);
internal sealed class Pad : Form
{
    private readonly List<PadEvent> events = [];
    private readonly string? log;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer flush = new() { Interval = 200 };
    public Pad(string[] args)
    {
        Text = "Click Test Pad"; BackColor = Color.FromArgb(28, 40, 52); ForeColor = Color.WhiteSmoke;
        DoubleBuffered = true; KeyPreview = true; Size = new Size(1000, 700); StartPosition = FormStartPosition.CenterScreen;
        var index = Array.IndexOf(args, "--log"); if (index >= 0 && index + 1 < args.Length) log = Path.GetFullPath(args[index + 1]);
        if (args.Contains("--fullscreen")) { FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = SystemInformation.VirtualScreen; TopMost = true; }
        MouseDown += (_, e) => Record("down", e.Button.ToString(), location: e.Location);
        MouseUp += (_, e) => Record("up", e.Button.ToString(), location: e.Location);
        MouseMove += (_, e) => Record("move", location: e.Location);
        KeyDown += (_, e) => { Record("keyDown", key: e.KeyCode.ToString()); if (e.KeyCode == Keys.Escape && !args.Contains("--fullscreen")) Close(); };
        KeyUp += (_, e) => Record("keyUp", key: e.KeyCode.ToString());
        flush.Tick += (_, _) => Flush(); flush.Start(); FormClosing += (_, _) => Flush(); Shown += (_, _) => { Activate(); Focus(); Flush(); };
    }
    private void Record(string kind, string button = "", string key = "", Point? location = null)
    {
        // Use the coordinate carried by the received message, not a later global cursor read.
        var p = location is { } client ? PointToScreen(client) : Cursor.Position;
        events.Add(new(kind, p.X, p.Y, button, key, clock.Elapsed.TotalSeconds));
        if (events.Count > 100000) events.RemoveRange(0, 10000); Invalidate();
    }
    private void Flush()
    {
        if (log is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var data = new { ProcessId = Environment.ProcessId, Width = Width, Height = Height, Events = events };
        try { File.WriteAllText(log + ".tmp", JsonSerializer.Serialize(data)); File.Move(log + ".tmp", log, true); }
        catch (IOException) { /* A verifier may have the prior report open; retry the next flush. */ }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var heading = new Font("Segoe UI", 24, FontStyle.Bold); using var body = new Font("Segoe UI", 13);
        var origin = PointToClient(new Point(Screen.PrimaryScreen!.Bounds.X + 35, Screen.PrimaryScreen.Bounds.Y + 35));
        if (!ClientRectangle.Contains(origin)) origin = new Point(30, 30);
        e.Graphics.DrawString("Click Test Pad", heading, Brushes.WhiteSmoke, origin);
        e.Graphics.DrawString($"Harmless receiver — no game actions\nLeft clicks: {events.Count(x => x.Kind == "down" && x.Button == "Left")}    Right clicks: {events.Count(x => x.Kind == "down" && x.Button == "Right")}\nF + G + H: toggle Screen Scout    I + O + P: save final target\nUse Screen Scout’s emergency key to stop.\nAlt + F4 closes this pad.", body, Brushes.LightGray, origin.X, origin.Y + 55);
        using var pen = new Pen(Color.FromArgb(82, 222, 194), 2);
        var trace = events.Where(x => x.Kind == "move").TakeLast(800).Select(x => PointToClient(new Point(x.X, x.Y))).ToArray();
        if (trace.Length > 1) e.Graphics.DrawLines(pen, trace);
        foreach (var click in events.Where(x => x.Kind == "down").TakeLast(200))
        {
            var p = PointToClient(new Point(click.X, click.Y)); e.Graphics.DrawEllipse(Pens.Orange, p.X - 4, p.Y - 4, 8, 8);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) flush.Dispose(); base.Dispose(disposing); }
}
