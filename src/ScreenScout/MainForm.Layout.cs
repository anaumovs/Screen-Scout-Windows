using System.Diagnostics;
using ScoutCore;

namespace ScreenScout;

internal sealed partial class MainForm
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    internal static Label TextLabel(string text, int width = 520, float size = 9, Color? color = null) => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color ?? Muted, Font = new Font("Segoe UI", size), Margin = new Padding(0, 4, 0, 7) };
    internal static Button ActionButton(string text, Action action)
    {
        var b = new ThemeButton { Text = text, AutoSize = true, MinimumSize = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = CardColor, ForeColor = Color.WhiteSmoke, Margin = new Padding(0, 3, 8, 3), AccessibleName = text };
        b.FlatAppearance.BorderColor = Color.FromArgb(66, 78, 93); b.Click += (_, _) => action(); return b;
    }
    private FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(535, 0), WrapContents = true, Margin = new Padding(0, 2, 0, 4) }; row.Controls.AddRange(controls); return row;
    }
    private CheckBox Toggle(string text, Func<bool> get, Action<bool> set, FlowLayoutPanel parent, Func<bool>? enabled = null)
    {
        var toggle = new ThemeCheckBox { Text = text, Checked = get(), AutoSize = true, ForeColor = Color.WhiteSmoke, Margin = new Padding(0, 6, 0, 6), AccessibleName = text };
        toggle.CheckedChanged += (_, _) => { if (!filling) { set(toggle.Checked); SettingsChanged(); } };
        parent.Controls.Add(toggle); if (enabled is not null) dependencies.Add((toggle, enabled)); return toggle;
    }
    private NumericUpDown Number(string title, double value, double min, double max, Action<double> set, int decimals = 2, Func<bool>? enabled = null, FlowLayoutPanel? parent = null)
    {
        var field = new ThemeNumber { DecimalPlaces = decimals, Minimum = (decimal)min, Maximum = (decimal)max, Value = (decimal)Math.Clamp(value, min, max), Increment = decimals == 0 ? 1 : .1m, Width = 118, BackColor = CardColor, ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.FixedSingle, AccessibleName = title };
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 15, 5) };
        box.Controls.Add(TextLabel(title, 240)); box.Controls.Add(field);
        field.ValueChanged += (_, _) => { if (!filling) { set((double)field.Value); SettingsChanged(); } };
        (parent ?? advanced).Controls.Add(box); if (enabled is not null) dependencies.Add((box, enabled)); return field;
    }
    private void Probability(string title, double value, Action<double> set, Func<bool> enabled)
    {
        var caption = TextLabel($"{title}: {value:P0}", 220);
        var slider = new TrackBar { Minimum = 0, Maximum = 100, Value = (int)Math.Round(value * 100), Width = 280, Height = 35, TickStyle = TickStyle.None, AccessibleName = title };
        slider.ValueChanged += (_, _) => { caption.Text = $"{title}: {slider.Value}%"; set(slider.Value / 100.0); SettingsChanged(); };
        var row = Row(caption, slider); advanced.Controls.Add(row); dependencies.Add((row, enabled));
    }
    private ComboBox Choice(string label, string[] options, int selected, Action<int> set, FlowLayoutPanel parent, Func<bool>? enabled = null)
    {
        var combo = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, BackColor = CardColor, ForeColor = Color.WhiteSmoke, AccessibleName = label };
        combo.Items.AddRange(options); combo.SelectedIndex = selected;
        combo.SelectedIndexChanged += (_, _) => { if (!filling && combo.SelectedIndex >= 0) { set(combo.SelectedIndex); SettingsChanged(); } };
        var row = Row(TextLabel(label, 180), combo); parent.Controls.Add(row); if (enabled is not null) dependencies.Add((row, enabled)); return combo;
    }
    private void AddLabel(Label label, FlowLayoutPanel panel, float size = 9, Color? color = null)
    {
        label.AutoSize = true; label.MaximumSize = new Size(515, 0); label.ForeColor = color ?? Muted; label.Font = new Font("Segoe UI", size); label.Margin = new Padding(0, 4, 0, 6); panel.Controls.Add(label);
    }
    private void BuildControls()
    {
        header.FlowDirection = FlowDirection.TopDown; header.WrapContents = false; header.Dock = DockStyle.Top; header.AutoSize = true; header.Padding = new Padding(22, 14, 22, 10); header.BackColor = CardColor;
        header.Controls.Add(TextLabel("Screen Scout Test", 600, 22, Color.WhiteSmoke));
        header.Controls.Add(TextLabel("PRECISION AUTOMATION    ·    F + G + H", 600, 9, Accent));
        root.Controls.Add(TextLabel("Position your mouse over the target, then press F + G + H. Press it again to pause.", 530, 11, Color.WhiteSmoke));
        var card = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = 530, Padding = new Padding(12), BackColor = CardColor, Margin = new Padding(0, 8, 0, 12) };
        AddLabel(status, card, 12, Accent); status.AccessibleName = "Status";
        AddLabel(detail, card); AddLabel(movement, card, 9, Color.Orange); AddLabel(target, card); AddLabel(finalTarget, card);
        card.Controls.Add(ActionButton("Set target…", SetTargetCountdown)); AddLabel(remaining, card, 21, Color.WhiteSmoke); remaining.AccessibleName = "Time remaining";
        root.Controls.Add(card);
        Toggle("Regular auto-clicking", () => Settings.RegularClickingEnabled, x => Settings.RegularClickingEnabled = x, root);
        Choice("Mouse button", ["Left click", "Right click"], (int)Settings.Button, x => Settings.Button = (ClickButton)x, root, () => Settings.RegularClickingEnabled || Settings.SecondaryClickingEnabled);
        baseInterval = Number("Interval (seconds)", Settings.BasicInterval, .08, 3600, Settings.SetBasicInterval, parent: root);
        Toggle("Session timer", () => Settings.TimerEnabled, x => Settings.TimerEnabled = x, root);
        var duration = Row(); root.Controls.Add(duration);
        Number("Hours", Settings.TimerHours, 0, 999, x => Settings.TimerHours = (int)x, 0, () => Settings.TimerEnabled, duration);
        Number("Minutes", Settings.TimerMinutes, 0, 59, x => Settings.TimerMinutes = (int)x, 0, () => Settings.TimerEnabled, duration);
        Number("Seconds", Settings.TimerSeconds, 0, 59, x => Settings.TimerSeconds = (int)x, 0, () => Settings.TimerEnabled, duration);
        AddLabel(readiness, root, 9, Color.Orange);
        start.Text = "Start"; start.AutoSize = true; start.MinimumSize = new Size(110, 38); start.BackColor = Accent; start.ForeColor = BackgroundColor; start.FlatStyle = FlatStyle.Flat; start.Click += (_, _) => StartButton();
        retry.Text = "Retry same position"; retry.AccessibleName = retry.Text; retry.AutoSize = true;
        retry.MinimumSize = new Size(155, 38); retry.FlatStyle = FlatStyle.Flat;
        retry.Click += (_, _) => RetrySamePosition();
        footer.FlowDirection = FlowDirection.TopDown; footer.WrapContents = false; footer.Dock = DockStyle.Bottom; footer.AutoSize = true; footer.Padding = new Padding(22, 10, 22, 10); footer.BackColor = CardColor;
        retry.BackColor = BackgroundColor; retry.ForeColor = Color.WhiteSmoke;
        footer.Controls.Add(Row(start, retry, ActionButton("Stop", EmergencyStop)));
        footer.Controls.Add(TextLabel("Manual movement pauses secondary trips. Emergency stop: your configured key (default F8).", 600));
        footer.Controls.Add(TextLabel("Automatic input recovery · Pause or Stop always cancels retries.", 600));
        advancedToggle.Text = "＋  Advanced settings"; advancedToggle.MinimumSize = new Size(220, 38); advancedToggle.BackColor = BackgroundColor; advancedToggle.FlatAppearance.BorderColor = Accent; advancedToggle.AutoSize = true; advancedToggle.FlatStyle = FlatStyle.Flat; advancedToggle.ForeColor = Color.WhiteSmoke;
        advancedToggle.Click += (_, _) => { advanced.Visible = !advanced.Visible; advancedToggle.Text = advanced.Visible ? "−  Advanced settings" : "＋  Advanced settings"; FitLayout(); if (advanced.Visible) root.ScrollControlIntoView(advanced); };
        header.Controls.Add(advancedToggle);
        Controls.Add(footer); Controls.Add(header);
        advanced.FlowDirection = FlowDirection.TopDown; advanced.WrapContents = false; advanced.AutoSize = true; advanced.MaximumSize = new Size(535, 0); advanced.Visible = false;
        root.Controls.Add(advanced);
        Section("Click timing");
        Toggle("Randomized click intervals", () => Settings.RandomIntervalsEnabled, x => Settings.RandomIntervalsEnabled = x, advanced);
        minimum = Number("Minimum interval", Settings.MinInterval, .08, 3600, x => Settings.MinInterval = x, enabled: () => Settings.RandomIntervalsEnabled);
        maximum = Number("Maximum interval", Settings.MaxInterval, .08, 3600, x => Settings.MaxInterval = x, enabled: () => Settings.RandomIntervalsEnabled);
        advanced.Controls.Add(TextLabel("When off, the basic interval is fixed. Movement can lengthen a cycle; missed actions never accumulate."));
        Section("Small movements");
        Toggle("Small movements", () => Settings.SmallMovementEnabled, x => Settings.SmallMovementEnabled = x, advanced);
        Probability("Small frequency", Settings.MovementProbability, x => Settings.MovementProbability = x, () => Settings.SmallMovementEnabled);
        Number("Maximum distance (screen pixels)", Settings.MovementRadius, 0, 50, x => Settings.MovementRadius = x, enabled: () => Settings.SmallMovementEnabled);
        Toggle("Click while moving — small", () => Settings.SmallClickWhileMoving, x => Settings.SmallClickWhileMoving = x, advanced, () => Settings.SmallMovementEnabled);
        Section("Large movements");
        Toggle("Large movements", () => Settings.LargeMovementEnabled, x => Settings.LargeMovementEnabled = x, advanced);
        Probability("Large frequency", Settings.LargeMovementProbability, x => Settings.LargeMovementProbability = x, () => Settings.LargeMovementEnabled);
        Number("Movement speed (1×–10×)", Settings.LargeMovementSpeed, 1, 10, x => Settings.LargeMovementSpeed = x, 1, () => Settings.LargeMovementEnabled);
        Toggle("Click while moving — large", () => Settings.LargeClickWhileMoving, x => Settings.LargeClickWhileMoving = x, advanced, () => Settings.LargeMovementEnabled);
        advanced.Controls.Add(TextLabel("Each frequency is an independent chance per cycle. Both movements can run in sequence. Travel clicks use the regular button and intervals, and require Regular auto-clicking. 1× preserves the original travel timing."));
        displays = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460, AccessibleName = "Active display" };
        advanced.Controls.Add(TextLabel("Active display")); advanced.Controls.Add(displays); FillDisplays();
        displays.SelectedIndexChanged += (_, _) =>
        {
            if (filling || displays.SelectedItem is not DisplayChoice choice) return;
            Settings.MovementDisplayId = choice.Id; Settings.MovementDisplayIdentity = MouseOutput.MovementDisplay(choice.Id)?.Identity; SettingsChanged();
        };
        advanced.Controls.Add(ActionButton("Edit large-movement regions…", () => EditRegions()));
        advanced.Controls.Add(TextLabel("Green boxes restrict large movement only; red boxes also block secondary trips. Small movement, regular clicks and exact final clicks are unaffected. Targets and regions use physical desktop pixels on Windows, including displays with negative coordinates."));
        Section("Secondary area");
        Toggle("Secondary-area clicking", () => Settings.SecondaryClickingEnabled, x => Settings.SecondaryClickingEnabled = x, advanced);
        Number("Minimum wait (seconds)", Settings.SecondaryMinWait, .2, 3600, x => Settings.SecondaryMinWait = x, enabled: () => Settings.SecondaryClickingEnabled);
        Number("Maximum wait (seconds)", Settings.SecondaryMaxWait, .2, 3600, x => Settings.SecondaryMaxWait = x, enabled: () => Settings.SecondaryClickingEnabled);
        advanced.Controls.Add(ActionButton("Choose purple area…", () => EditRegions(true)));
        advanced.Controls.Add(TextLabel("Purple: one secondary click area per display. Red blocks secondary travel; green restricts large random movement only. Manual pointer movement pauses a secondary visit. Start resumes without recapturing the target."));
        Section("Click variation");
        Toggle("Slight click-position variation", () => Settings.ClickVariationEnabled, x => Settings.ClickVariationEnabled = x, advanced);
        Number("Variation radius (screen pixels, 0–10)", Settings.ClickVariationRadius, 0, 10, x => Settings.ClickVariationRadius = x, enabled: () => Settings.ClickVariationEnabled);
        advanced.Controls.Add(TextLabel("Fresh offset around the original target, never cumulative. Final clicks are always exact."));
        Section("Automatic breaks");
        Toggle("Automatic breaks", () => Settings.BreaksEnabled, x => Settings.BreaksEnabled = x, advanced);
        Probability("Break frequency", Settings.BreakProbability, x => Settings.BreakProbability = x, () => Settings.BreaksEnabled);
        Number("Minimum break (seconds)", Settings.MinBreak, .2, 3600, x => Settings.MinBreak = x, enabled: () => Settings.BreaksEnabled);
        Number("Maximum break (seconds)", Settings.MaxBreak, .2, 3600, x => Settings.MaxBreak = x, enabled: () => Settings.BreaksEnabled);
        advanced.Controls.Add(TextLabel("Timer time includes breaks and movement. Pause freezes it; Stop resets it. Changing duration resets the next Start."));
        Section("Session completion");
        Toggle("Final click at timer completion", () => Settings.FinalClickEnabled, x => Settings.FinalClickEnabled = x, advanced, () => Settings.TimerEnabled);
        Choice("Final click button", ["Left click", "Right click"], (int)Settings.FinalButton, x => Settings.FinalButton = (ClickButton)x, advanced, () => Settings.FinalClickEffective);
        advanced.Controls.Add(TextLabel("I + O + P saves the exact final coordinate without clicking or starting. Capturing during a run pauses it. Final click works with regular clicking off, only at natural timer completion."));
        Section("Shortcuts & tools");
        int[] keys = [0x77, 0x78, 0x79, 0x1B];
        Choice("Emergency key", ["F8", "F9", "F10", "Escape"], Array.IndexOf(keys, Settings.EmergencyCode), x => Settings.EmergencyCode = keys[x], advanced);
        Choice("Modifier", ["None", "Control + Alt"], Settings.EmergencyControlAlt ? 1 : 0, x => Settings.EmergencyControlAlt = x != 0, advanced);
        advanced.Controls.Add(TextLabel("Release all three keys before using a chord again. Both letter chords pass through to the focused app. Windows prevents sending input to apps running at a higher privilege level."));
        AddLabel(conflict, advanced);
        advanced.Controls.Add(Row(ActionButton("Check Minecraft options…", ChooseMinecraft), ActionButton("Open click test pad", OpenPad)));
        advanced.Controls.Add(ActionButton("Open settings folder", () => { Directory.CreateDirectory(store.DirectoryPath); Process.Start(new ProcessStartInfo(store.DirectoryPath) { UseShellExecute = true }); }));
        AddLabel(errors, root, 9, Color.Orange);
        CheckMinecraft(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "options.txt"));
    }
    private void Section(string title)
    {
        var label = TextLabel(title.ToUpperInvariant(), 580, 10, Accent);
        label.Margin = new Padding(0, 22, 0, 10); advanced.Controls.Add(label);
    }
    private void FitLayout()
    {
        if (root.IsDisposed) return;
        var width = Math.Max(400, root.ClientSize.Width - root.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
        root.SuspendLayout();
        advanced.MaximumSize = new Size(width, 0); advanced.Width = width;
        foreach (Control control in root.Controls)
        {
            if (control is FlowLayoutPanel panel && panel != advanced) { panel.MaximumSize = new Size(width, 0); if (panel.BackColor == CardColor) { panel.MinimumSize = new Size(width, 0); panel.AutoSizeMode = AutoSizeMode.GrowAndShrink; panel.AutoSize = true; } }
            else if (control is Label label) label.MaximumSize = new Size(width, 0);
        }
        root.ResumeLayout();
    }
}
