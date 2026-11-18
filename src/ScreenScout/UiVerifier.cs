using System.Reflection;
using System.Text.Json;

namespace ScreenScout;

internal static class UiVerifier
{
    public static async Task Run(MainForm form, string[] args)
    {
        var folder = Path.GetFullPath(args[Array.IndexOf(args, "--verify-ui") + 1]);
        Directory.CreateDirectory(folder); List<string> passed = []; string? failure = null;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); passed.Add(name); }
        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        void Render(string file) { using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height }); bitmap.Save(Path.Combine(folder, file)); }
        try
        {
            form.Show(); await Task.Delay(300);
            var header = Field<FlowLayoutPanel>("header"); var footer = Field<FlowLayoutPanel>("footer");
            var root = Field<FlowLayoutPanel>("root"); var advanced = Field<FlowLayoutPanel>("advanced"); var button = Field<Button>("advancedToggle");
            Check(header.Visible && header.Bounds.Contains(button.Bounds), "Advanced navigation visible without scrolling");
            Render("basic.png");
            button.PerformClick(); await Task.Delay(150);
            Check(advanced.Visible, "Advanced section opens");
            Check(header.Bottom <= root.Top && root.Bottom <= footer.Top, "Fixed navigation and actions do not overlap scrolling content");
            Render("advanced.png");
            var target = advanced.Controls.Cast<Control>().First(c => c.Text == "Large movements");
            root.AutoScrollPosition = new Point(0, advanced.Top - root.AutoScrollPosition.Y + target.Top - 30); await Task.Delay(150); Render("movement.png");
            using var label = new Label { Text = "Disabled setting", ForeColor = MainForm.Muted };
            using var number = new ThemeNumber { Value = 7, ForeColor = Color.WhiteSmoke };
            using var panel = new FlowLayoutPanel(); panel.Controls.Add(label); panel.Controls.Add(number);
            ThemeControls.SetAvailable(panel, false); number.UpButton(); number.DownButton();
            Check(label.Enabled && label.ForeColor == MainForm.Muted, "Disabled section labels retain readable color");
            Check(number.ReadOnly && number.ForeColor == ThemeControls.Inactive && number.Value == 7, "Inactive numeric values stay readable and cannot be stepped");
            ThemeControls.SetAvailable(panel, true); number.UpButton(); Check(number.Value == 8 && !number.ReadOnly, "Re-enabled numeric controls work normally");
            form.ClientSize = new Size(620, 600); await Task.Delay(150);
            Check(header.Visible && footer.Visible && root.Height > 100, "Navigation and actions fit at minimum window size"); Render("compact.png");
            Check(!form.Engine.Active, "UI verification never starts mouse automation");
        }
        catch (Exception e) { failure = e.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "checks.json"), JsonSerializer.Serialize(new { Passed = passed, Failure = failure }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = failure is null ? 0 : 1; form.Close();
        }
    }
}
