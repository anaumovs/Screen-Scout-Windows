using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using ScoutCore;

namespace ScreenScout;

internal sealed partial class MainForm : Form
{
    internal static readonly Color BackgroundColor = Color.FromArgb(19, 24, 31), CardColor = Color.FromArgb(30, 36, 45), Accent = Color.FromArgb(82, 222, 194), Muted = Color.FromArgb(174, 186, 201);
    private readonly SettingsStore store = new();
    internal ClickSettings Settings { get; private set; }
    internal ClickEngine Engine { get; private set; } = null!;
    internal InputRecovery Recovery { get; private set; } = null!;
    internal WindowsMouse MouseOutput { get; } = new() { ExclusiveOwner = true };
    private readonly InputMonitor monitor = new();
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 150 };
    private readonly Label status = new(), detail = new(), target = new(), finalTarget = new(), remaining = new(), movement = new(), readiness = new(), conflict = new(), errors = new();
    private readonly Button start = new ThemeButton(), retry = new ThemeButton(), advancedToggle = new ThemeButton();
    private readonly FlowLayoutPanel root = new(), advanced = new();
    private readonly FlowLayoutPanel header = new(), footer = new();
    private readonly DiagnosticLog diagnostics;
    private readonly List<(Control Control, Func<bool> Enabled)> dependencies = [];
    private NumericUpDown baseInterval = null!, minimum = null!, maximum = null!;
    private ComboBox displays = null!;
    private double? captureDeadline;
    private bool emergencyReady, editing, initialized, filling, externalVerification;
    private volatile bool closing;
    private RegionEditor? regionEditor;
    private readonly string[] arguments;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool VerificationRunning { get; set; }
    internal bool VerificationAborted { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ExpectedVerificationEmergency { get; set; }
    private string? persistenceError;
    internal object InputDiagnostics => new { monitor.MouseEvents, monitor.KeyboardEvents, monitor.LastMouseTag, monitor.HookThreadId, monitor.LastInstallError, MouseOutput.LastSentTag, MouseOutput.ConfirmedPairs, MouseOutput.PendingAcknowledgements, Recovery.TotalAttempts, monitor.PhysicalMovementVersion, Engine.SecondarySettlingWaits, Engine.SecondaryReturnsReplanned };
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    internal bool Active => initialized && (Engine.Active || Recovery.Pending) || captureDeadline is not null;
    internal int ObservedKeyboardEvents => monitor.KeyboardEvents;
    internal int MonitorThreadId => monitor.HookThreadId;
    private bool Ready => initialized && monitor.Listening && emergencyReady && MouseOutput.Available;
    public MainForm(string[] args)
    {
        arguments = args; externalVerification = args.Contains("--verify-external") || args.Contains("--verify-ui");
        Settings = store.Load();
        diagnostics = new(store.DirectoryPath);
        Text = "Screen Scout Test"; Name = "ScreenScoutTest"; AccessibleName = "Screen Scout Test";
        BackColor = BackgroundColor; ForeColor = Color.WhiteSmoke; Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(680, 880); MinimumSize = new Size(620, 600); StartPosition = FormStartPosition.CenterScreen;
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenScout.Icon.png");
        if (iconStream is not null)
        {
            using var bitmap = new Bitmap(iconStream); using var small = new Bitmap(bitmap, 64, 64);
            var handle = small.GetHicon(); using var borrowed = Icon.FromHandle(handle); Icon = (Icon)borrowed.Clone(); DestroyIcon(handle);
        }
        root.FlowDirection = FlowDirection.TopDown; root.WrapContents = false; root.AutoScroll = true; root.Dock = DockStyle.Fill; root.Padding = new Padding(22);
        Controls.Add(root); BuildControls();
        Resize += (_, _) => FitLayout(); FitLayout();
        Load += OnLoaded; FormClosing += OnClosing;
        heartbeat.Tick += (_, _) => Tick();
    }
    private void OnLoaded(object? sender, EventArgs e)
    {
        var scheduler = new WindowsScheduler(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        Engine = new(MouseOutput, scheduler);
        MouseOutput.ReadPhysicalMovement = () => monitor.PhysicalMovementVersion;
        monitor.IgnoreOwnInput(tag => MouseOutput.OwnsTag(tag));
        Recovery = new(Engine, scheduler, CanRecover, ReconnectMonitor);
        initialized = true; Engine.Changed = RefreshState;
        MouseOutput.OnDelivery = () => { Engine.ConfirmDelivery(); Recovery.ConfirmDelivery(); };
        // Keep low-level hooks short: UI work and session planning run after the callback returns.
        monitor.OnToggle = () => OnUi(ToggleFromChord); monitor.OnFinal = () => OnUi(CaptureFinal); monitor.OnEmergency = () => OnUi(EmergencyStop);
        // Queue acknowledgement so SendInput/Up finishes clearing the held pair
        // before a final-delivery callback can stop and release the engine.
        monitor.OnMouse = (tag, message) => { if (!closing && MouseOutput.OwnsTag(tag) && message is 0x0202 or 0x0205) OnUi(() => MouseOutput.Acknowledge(tag, message)); };
        monitor.OnFailure = reason => OnUi(() => Pause(reason));
        monitor.Start(); ConfigureEmergency();
        Settings.MovementDisplayIdentity ??= MouseOutput.MovementDisplay(Settings.MovementDisplayId)?.Identity;
        SystemEvents.DisplaySettingsChanged += DisplayChanged; SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.SessionSwitch += SessionChanged;
        heartbeat.Start(); RefreshState();
        if (arguments.Contains("--smoke-test")) BeginInvoke(async () => { await Task.Delay(250); Environment.ExitCode = Ready ? 0 : 1; Close(); });
        if (arguments.Contains("--verify-external")) BeginInvoke(async () => await ExternalVerifier.Run(this, arguments));
        if (arguments.Contains("--verify-ui")) BeginInvoke(async () => await UiVerifier.Run(this, arguments));
    }
    private void ConfigureEmergency()
    {
        if (!initialized) return;
        Native.UnregisterHotKey(Handle, 1);
        emergencyReady = Native.RegisterHotKey(Handle, 1, 0x4000u | (Settings.EmergencyControlAlt ? 3u : 0u), (uint)Settings.EmergencyCode);
        monitor.EmergencyKey = Settings.EmergencyCode; monitor.EmergencyControlAlt = Settings.EmergencyControlAlt;
        MouseOutput.MonitoringAvailable = monitor.Listening && emergencyReady;
    }
    private bool CanRecover() => !closing && !editing && captureDeadline is null &&
        MouseOutput.ExclusiveOwner && MouseOutput.SessionAvailable && Native.DefaultDesktop &&
        (MouseOutput.VerificationProcess is not { } pid || Native.ProcessAt(Native.GetForegroundWindow()) == pid);
    private bool ReconnectMonitor()
    {
        // A nonzero hook handle does not prove Windows still has the hook installed.
        monitor.Start(notifyFailure: false); ConfigureEmergency();
        diagnostics.Record("Reconnect", Ready ? "Input hooks installed" : MouseOutput.FailureReason, InputDiagnostics);
        return Ready;
    }
    internal void RemoveMouseHookForVerification()
    {
        if (!VerificationRunning || MouseOutput.VerificationProcess is null) throw new InvalidOperationException("Fault injection requires the test pad.");
        monitor.RemoveMouseHookForVerification();
    }
    internal void RetrySamePosition()
    {
        if (!initialized || editing || closing) return;
        Recovery.RetrySamePosition(Settings); RefreshState();
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WmHotkey && m.WParam == 1 && initialized) EmergencyStop();
        if (m.Msg == 0x0011 && initialized) EmergencyStop(); // WM_QUERYENDSESSION
        base.WndProc(ref m);
    }
    private void SettingsChanged()
    {
        if (!initialized) return;
        if (Active) Pause("Settings changed. Start again to use the new values.");
        filling = true; baseInterval.Value = (decimal)Settings.BasicInterval; minimum.Value = (decimal)Settings.MinInterval; maximum.Value = (decimal)Settings.MaxInterval; filling = false;
        if (!externalVerification)
        {
            try { store.Save(Settings); persistenceError = null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { persistenceError = "Could not save settings: " + e.Message; }
        }
        ConfigureEmergency(); RefreshState();
    }
    internal void UseVerificationSettings(ClickSettings settings) { Settings = settings; ConfigureEmergency(); }
    private void RefreshState()
    {
        if (!initialized || closing) return;
        status.Text = captureDeadline is { } end ? $"Set target in {Math.Max(0, Math.Ceiling(end - Now))}…" : Recovery.Pending ? "Retrying…" : Engine.Status switch { ClickStatus.Starting => "Starting…", ClickStatus.TakingBreak => "Taking a break", ClickStatus.Finishing => "Final click…", _ => Engine.Status.ToString() };
        detail.Text = captureDeadline is not null ? "Set target: position your mouse outside Screen Scout." : Engine.Detail;
        target.Text = Engine.Target is { } p ? $"Saved target       x {p.X:0}   y {p.Y:0}" : "Saved target       Not set";
        finalTarget.Text = Engine.FinalTarget is { } f ? $"Final target        x {f.X:0}   y {f.Y:0}" : "Final target        Set with I + O + P";
        finalTarget.Visible = Engine.FinalTarget is not null || Settings.FinalClickEnabled;
        int seconds = (int)Math.Ceiling(Engine.RemainingDuration ?? Settings.TimerDuration);
        remaining.Text = $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"; remaining.Visible = Settings.TimerEnabled;
        movement.Text = Engine.MovementNote ?? ""; movement.Visible = movement.Text.Length > 0;
        start.Text = Active ? "Pause" : "Start"; start.Enabled = Active || (Ready && !editing && Settings.ValidationError is null);
        retry.Enabled = !Active && !editing && Engine.Status == ClickStatus.Paused && Engine.Target is not null && Engine.RemainingDuration != 0 && Settings.ValidationError is null;
        readiness.Text = !Ready ? MouseOutput.FailureReason : ""; readiness.Visible = readiness.Text.Length > 0;
        errors.Text = Settings.ValidationError ?? persistenceError ?? ""; errors.Visible = errors.Text.Length > 0;
        if (!externalVerification) diagnostics.Record(Engine.Status.ToString(), Engine.Detail + " | " + Engine.MovementNote, InputDiagnostics);
        foreach (var dependency in dependencies) ThemeControls.SetAvailable(dependency.Control, dependency.Enabled());
    }
    private void StartButton()
    {
        if (Active) { Pause(); return; }
        if (editing || !Ready) return;
        Recovery.Cancel();
        if (Settings.RegularClickingEnabled && Engine.Target is null) SetTargetCountdown();
        else Engine.Start(Settings);
    }
    private void SetTargetCountdown()
    {
        if (!Ready || editing) return;
        Recovery.Cancel();
        if (Engine.Active) Engine.Pause();
        captureDeadline = Now + 5; RefreshState();
    }
    private void ToggleFromChord()
    {
        if (!initialized || editing || closing) return;
        if (Active) { Pause(); return; }
        if (!Ready) { RefreshState(); return; }
        Recovery.Cancel();
        if (MouseOutput.CurrentPosition() is { } p) Engine.Start(Settings, p);
    }
    private void CaptureFinal()
    {
        if (!initialized || editing || closing) return;
        Recovery.Cancel(); captureDeadline = null;
        if (MouseOutput.CurrentPosition() is { } p) Engine.CaptureFinalTarget(p);
    }
    internal void Pause(string reason = "Paused. Reposition, then press F + G + H to resume at the new position.")
    { captureDeadline = null; if (initialized) { Recovery.Cancel(); Engine.Pause(reason); } }
    internal void EmergencyStop()
    {
        if (VerificationRunning && !ExpectedVerificationEmergency) VerificationAborted = true;
        VerificationStop();
    }
    internal void VerificationStop() { captureDeadline = null; if (initialized) { Recovery.Cancel(); Engine.Stop(); } }
    private void Tick()
    {
        if (!initialized || closing) return;
        monitor.UpdateLayout();
        MouseOutput.MonitoringAvailable = monitor.Listening && emergencyReady;
        Engine.CheckEnvironment();
        Recovery.Check(MouseOutput.DeliveryTimedOut);
        if (captureDeadline is { } end)
        {
            if (!Ready || editing) captureDeadline = null;
            else if (Now >= end)
            {
                captureDeadline = null;
                if (MouseOutput.CurrentPosition() is { } p) Engine.Start(Settings, p);
            }
        }
        RefreshState();
    }
    private sealed record DisplayChoice(string Id, string Caption) { public override string ToString() => Caption; }
    private void FillDisplays()
    {
        filling = true;
        try
        {
            displays.Items.Clear();
            foreach (var screen in Screen.AllScreens)
                displays.Items.Add(new DisplayChoice(screen.DeviceName, $"{screen.DeviceName} — {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " (primary)" : "")}"));
            var selected = displays.Items.Cast<DisplayChoice>().FirstOrDefault(d => d.Id == Settings.MovementDisplayId);
            if (selected is null && string.IsNullOrEmpty(Settings.MovementDisplayId))
                selected = displays.Items.Cast<DisplayChoice>().FirstOrDefault(d => d.Id == Screen.PrimaryScreen?.DeviceName);
            displays.SelectedItem = selected;
        }
        finally { filling = false; }
    }
    private void EditRegions(bool secondary = false)
    {
        if (editing || !initialized) return;
        Pause("Editing movement regions. Resume explicitly when finished.");
        if (MouseOutput.MovementDisplay(Settings.MovementDisplayId) is not { } display) { errors.Text = "Choose an active display first."; errors.Visible = true; return; }
        var profile = Settings.MovementRegions.FirstOrDefault(p => p.DisplayIdentity == display.Identity);
        editing = true;
        try
        {
            using var editor = new RegionEditor(display, profile, secondary); regionEditor = editor;
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                Settings.MovementRegions.RemoveAll(p => p.DisplayIdentity == display.Identity);
                Settings.MovementRegions.Add(new(display.Identity, display.Bounds.Size, editor.Boxes) { ReferenceBounds = display.Bounds });
                Settings.MovementDisplayIdentity = display.Identity; SettingsChanged();
            }
        }
        finally { regionEditor = null; editing = false; monitor.RequireRelease(); RefreshState(); }
    }
    private void ChooseMinecraft()
    {
        using var picker = new OpenFileDialog { Title = "Choose Minecraft options.txt", Filter = "Minecraft settings|options.txt|Text files|*.txt", CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) CheckMinecraft(picker.FileName);
    }
    private void CheckMinecraft(string file)
    {
        if (!File.Exists(file)) { conflict.Text = "Minecraft binding check: choose options.txt to inspect F/G/H and I/O/P bindings."; return; }
        try
        {
            var matches = File.ReadLines(file).Where(line => line.StartsWith("key_", StringComparison.Ordinal) &&
                new[] { "f", "g", "h", "i", "o", "p" }.Any(key => line.EndsWith(":key.keyboard." + key, StringComparison.Ordinal))).ToArray();
            conflict.Text = matches.Length == 0 ? "No F/G/H or I/O/P Minecraft bindings found." : "Minecraft shortcut conflicts: " + string.Join("; ", matches);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { conflict.Text = "Could not read Minecraft settings: " + e.Message; }
    }
    private void OpenPad()
    {
        Pause("Test pad opened. Set a target in the pad when ready.");
        var pad = Path.Combine(AppContext.BaseDirectory, "ClickTestPad.exe");
        if (File.Exists(pad)) Process.Start(new ProcessStartInfo(pad) { UseShellExecute = true });
        else MessageBox.Show(this, "Keep ClickTestPad.exe beside ScreenScout.exe.", "Screen Scout");
    }
    private void OnUi(Action action)
    {
        if (closing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!closing) action(); }); } catch (InvalidOperationException) { }
    }
    private void DisplayChanged(object? sender, EventArgs e) => OnUi(() =>
    { Recovery.Cancel(); captureDeadline = null; regionEditor?.Close(); Engine.ClearTargets(); MouseOutput.InvalidateDisplays(); FillDisplays(); monitor.RequireRelease(); RefreshState(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => OnUi(() =>
    { MouseOutput.SessionAvailable = e.Mode != PowerModes.Suspend; Pause("Power state changed. Resume explicitly when ready."); monitor.RequireRelease(); });
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => OnUi(() =>
    { MouseOutput.SessionAvailable = e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect; Pause("Windows session changed. Resume explicitly when ready."); monitor.RequireRelease(); });
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (VerificationRunning) { EmergencyStop(); e.Cancel = true; return; }
        closing = true; heartbeat.Stop(); EmergencyStop(); MouseOutput.Release(); monitor.Dispose();
        Native.UnregisterHotKey(Handle, 1);
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
        regionEditor?.Close(); heartbeat.Dispose();
    }
}
