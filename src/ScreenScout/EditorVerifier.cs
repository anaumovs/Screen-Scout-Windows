using System.Reflection;
using System.Text.Json;
using ScoutCore;

namespace ScreenScout;

// Opt-in in-process control tests. Never starts hooks or sends OS mouse input.
internal static class EditorVerifier
{
    public static int Run(string report)
    {
        List<string> passed = [];
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed.Add(label); }
        try
        {
            var display = new MovementDisplay("editor-test", new(0, 0, 800, 600));
            var green = new MovementBox(new(20, 20, 80, 80), false);
            var red = new MovementBox(new(150, 20, 80, 80), true);
            using var editor = new RegionEditor(display, new(display.Identity, display.Bounds.Size, [green, red]), true);
            var tool = (ComboBox)typeof(RegionEditor).GetField("tool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
            Check(tool.SelectedItem?.ToString() == "Draw secondary", "Choose purple opens Draw secondary tool");
            void Mouse(string name, int x, int y) => typeof(RegionEditor).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);
            void Drag(int x, int y, int endX, int endY) { Mouse("OnMouseDown", x, y); Mouse("OnMouseMove", endX, endY); Mouse("OnMouseUp", endX, endY); }
            Drag(300, 100, 400, 200);
            Check(editor.Boxes.Single(b => b.Secondary).Rect == new RectD(300, 100, 100, 100), "Purple rectangle draws");
            Drag(400, 200, 500, 300);
            Check(editor.Boxes.Count(b => b.Secondary) == 1 && editor.Boxes.Single(b => b.Secondary).Rect.X == 400, "New purple replaces previous rectangle");
            tool.SelectedIndex = 2;
            Drag(450, 250, 470, 270);
            Check(editor.Boxes.Single(b => b.Secondary).Rect == new RectD(420, 220, 100, 100), "Purple rectangle moves");
            Drag(520, 320, 570, 350);
            Check(editor.Boxes.Single(b => b.Secondary).Rect == new RectD(420, 220, 150, 130), "Purple corner resizes");
            using (var bitmap = new Bitmap(800, 600))
            {
                editor.DrawToBitmap(bitmap, new Rectangle(0, 0, 800, 600));
                var purple = bitmap.GetPixel(450, 260); var allowed = bitmap.GetPixel(50, 60); var blocked = bitmap.GetPixel(180, 60);
                Check(purple.B > purple.G && purple.R > purple.G, "Purple region paints purple");
                Check(allowed.G > allowed.R && blocked.R > blocked.G, "Existing green and red colors preserved");
                bitmap.Save(Path.ChangeExtension(report, ".png"));
            }
            typeof(RegionEditor).GetMethod("Delete", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
            Check(!editor.Boxes.Any(b => b.Secondary), "Selected purple deletes");
            Check(editor.Boxes.SequenceEqual(new[] { green, red }), "Green and red boxes unchanged by purple editing");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, Failure = (string?)null }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, Failure = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
