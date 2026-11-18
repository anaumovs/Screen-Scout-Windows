using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScoutCore;

namespace ScreenScout;

/// Opt-in interactive verification; every mouse event is restricted to the pad process.
internal static class ExternalVerifier
{
    private sealed record Received(string Kind, int X, int Y, string Button, string Key, double Time);
    private sealed record PadReport(int ProcessId, int Width, int Height, List<Received> Events);
    private sealed record Check(string Name, bool Passed, string Detail = "");
    public static async Task Run(MainForm form, string[] args)
    {
        var originalMotionReader = form.MouseOutput.ReadPhysicalMovement;
        var original = form.Settings.Snapshot(); List<Check> checks = []; List<object> scenarios = [];
        var reportIndex = Array.IndexOf(args, "--report");
        var reportPath = reportIndex >= 0 && reportIndex + 1 < args.Length ? Path.GetFullPath(args[reportIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenScoutTest", "verification", $"external-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var padLog = Path.Combine(Path.GetDirectoryName(reportPath)!, "pad-" + Guid.NewGuid().ToString("N") + ".json");
        Process? pad = null; var runId = Guid.NewGuid(); bool completed = false;
        async Task<PadReport> ReadPad()
        {
            await Task.Delay(250);
            var result = JsonSerializer.Deserialize<PadReport>(await File.ReadAllTextAsync(padLog)) ?? throw new InvalidOperationException("Empty pad report.");
            if (pad is null || result.ProcessId != pad.Id) throw new InvalidOperationException("Pad report is from a different process.");
            return result;
        }
        void RequirePad()
        {
            if (form.VerificationAborted) throw new OperationCanceledException("Verification was stopped by the user. No scenario will restart.");
            if (pad is null || pad.HasExited || Native.ProcessAt(Native.GetForegroundWindow()) != pad.Id) throw new InvalidOperationException("The test pad lost focus. Verification aborted without sending input elsewhere.");
            if (!form.MouseOutput.Available) throw new InvalidOperationException(form.MouseOutput.FailureReason);
        }
        form.VerificationRunning = true;
        try
        {
            form.VerificationStop();
            var padPath = Path.Combine(AppContext.BaseDirectory, "ClickTestPad.exe");
            if (!File.Exists(padPath)) throw new FileNotFoundException("Keep ClickTestPad.exe beside ScreenScout.exe.", padPath);
            var start = new ProcessStartInfo(padPath) { UseShellExecute = false };
            start.ArgumentList.Add("--fullscreen"); start.ArgumentList.Add("--log"); start.ArgumentList.Add(padLog);
            pad = Process.Start(start) ?? throw new InvalidOperationException("Could not launch Click Test Pad.");
            for (int i = 0; i < 100 && !pad.HasExited; i++) { pad.Refresh(); if (pad.MainWindowHandle != 0 && File.Exists(padLog)) break; await Task.Delay(100); }
            if (pad.HasExited || pad.MainWindowHandle == 0) throw new InvalidOperationException("Click Test Pad did not start.");
            Native.SetForegroundWindow(pad.MainWindowHandle);
            for (int i = 0; i < 200 && Native.ProcessAt(Native.GetForegroundWindow()) != pad.Id; i++) await Task.Delay(100);
            await Task.Delay(500); RequirePad();
            form.MouseOutput.VerificationProcess = (uint)pad.Id;
            var bounds = WindowsMouse.Bounds(Screen.PrimaryScreen!);
            var target = new PointD(bounds.X + Math.Round(bounds.Width * .28), bounds.Y + Math.Round(bounds.Height * .4));
            var final = new PointD(bounds.X + Math.Round(bounds.Width * .75), bounds.Y + Math.Round(bounds.Height * .65));
            ClickSettings Defaults() => new() { SmallMovementEnabled = false, LargeMovementEnabled = false, BreaksEnabled = false, RandomIntervalsEnabled = false, MinInterval = .16, MaxInterval = .16 };

            async Task Scenario(string name, ClickSettings settings, double seconds)
            {
                List<object> states = [];
                var pairsBefore = form.MouseOutput.ConfirmedPairs;
                var deviceMotionBefore = form.MouseOutput.PhysicalMovementVersion;
                var priorChanged = form.Engine.Changed;
                form.Engine.Changed = () => { priorChanged?.Invoke(); states.Add(new { At = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, Status = form.Engine.Status.ToString(), form.Engine.Detail, form.Engine.FinalTarget }); };
                RequirePad(); form.Engine.ClearTargets(); form.UseVerificationSettings(settings);
                var before = (await ReadPad()).Events.Count;
                form.MouseOutput.Move(target);
                if (settings.FinalClickEffective) form.Engine.CaptureFinalTarget(final);
                form.Engine.Start(settings, settings.RegularClickingEnabled ? target : null);
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (settings.SecondaryClickingEnabled)
                {
                    var beganWaiting = Stopwatch.GetTimestamp();
                    while (form.Engine.Active && form.Engine.MovementNote != "Secondary visit complete.")
                    {
                        RequirePad();
                        if (Stopwatch.GetElapsedTime(beganWaiting).TotalSeconds > 20) throw new InvalidOperationException("Secondary visit did not finish: " + form.Engine.MovementNote);
                        await Task.Delay(30);
                    }
                }
                var endingStatus = form.Engine.Status.ToString(); var endingDetail = form.Engine.Detail; var inputDiagnostics = form.InputDiagnostics;
                if (settings.SecondaryClickingEnabled)
                    checks.Add(new(name + ": injected travel is not reported as device movement", deviceMotionBefore is not null && form.MouseOutput.PhysicalMovementVersion == deviceMotionBefore));
                if (settings.RegularClickingEnabled)
                    checks.Add(new(name + ": Windows acknowledged click delivery", form.MouseOutput.ConfirmedPairs > pairsBefore));
                if (!settings.TimerEnabled)
                    checks.Add(new(name + ": session remained running", form.Engine.Active));
                if (settings.FinalClickEffective)
                    checks.Add(new(name + ": timer completed and stopped", form.Engine.Status == ClickStatus.Stopped && form.Engine.Detail.Contains("delivered")));
                form.VerificationStop(); form.Engine.Changed = priorChanged; await Task.Delay(120); RequirePad();
                var all = (await ReadPad()).Events; var trace = all.Skip(before).ToArray();
                var downs = trace.Where(e => e.Kind == "down").ToArray();
                checks.Add(new(name + ": input received", settings.RegularClickingEnabled ? downs.Length > 0 : trace.Any(e => e.Kind == "move")));
                Received? held = null; bool pairs = true;
                foreach (var e in trace.Where(e => e.Kind is "down" or "up"))
                {
                    if (e.Kind == "down") { if (held is not null) pairs = false; held = e; }
                    else { if (held is null || held.Button != e.Button || held.X != e.X || held.Y != e.Y) pairs = false; held = null; }
                }
                checks.Add(new(name + ": matching down/up pairs", pairs && held is null));
                if (!settings.RegularClickingEnabled && !settings.SecondaryClickingEnabled) checks.Add(new(name + ": no ordinary clicks", downs.All(e => settings.FinalClickEffective && e.X == final.X && e.Y == final.Y)));
                if (settings.FinalClickEffective) checks.Add(new(name + ": one exact final click", downs.Count(e => e.X == final.X && e.Y == final.Y && e.Button == settings.FinalButton.ToString()) == 1));
                var stoppedCount = all.Count; await Task.Delay(350);
                var later = (await ReadPad()).Events.Skip(stoppedCount).Where(e => e.Kind is "down" or "up").ToArray();
                checks.Add(new(name + ": no clicks after Stop", later.Length == 0));
                scenarios.Add(new { Name = name, Settings = settings.Snapshot(), Events = trace, States = states, EndingStatus = endingStatus, EndingDetail = endingDetail, InputDiagnostics = inputDiagnostics });
            }
            if (args.Contains("--reliability"))
            {
                checks.Add(new("Hooks have a dedicated thread", form.MonitorThreadId != Environment.CurrentManagedThreadId));
                var settings = Defaults(); settings.SecondaryClickingEnabled = true;
                settings.TimerEnabled = true; settings.TimerHours = 3; settings.TimerMinutes = 0;
                form.UseVerificationSettings(settings); form.MouseOutput.Move(target);
                form.Engine.Start(settings, target); await WaitFor(() => form.Engine.Status == ClickStatus.Running, 3);
                var keyboardBefore = form.ObservedKeyboardEvents;
                // Keyboard-only probe in the harmless pad, while the UI dispatcher is deliberately stalled.
                var probe = Task.Run(() => { Thread.Sleep(100); SendKeys([0x87], false); SendKeys([0x87], true); });
                Thread.Sleep(1600);
                checks.Add(new("Input observation survives a 1.6-second UI stall", form.ObservedKeyboardEvents >= keyboardBefore + 2));
                await probe; await Task.Delay(400); RequirePad();
                var afterStall = form.MouseOutput.ConfirmedPairs; await Task.Delay(500);
                checks.Add(new("Clicks remain confirmed after UI stall", form.MouseOutput.ConfirmedPairs > afterStall && form.Engine.Active));
                foreach (ulong sequence in new ulong[] { 65530, 131066 })
                {
                    form.MouseOutput.SetSequenceForVerification(sequence);
                    var pairs = form.MouseOutput.ConfirmedPairs; await Task.Delay(1400); RequirePad();
                    checks.Add(new("Event tags survive sequence wrap " + sequence, form.MouseOutput.ConfirmedPairs >= pairs + 3));
                }
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    form.RemoveMouseHookForVerification();
                    await WaitFor(() => form.Recovery.Pending, 5);
                    var remaining = form.Engine.RemainingDuration;
                    await WaitFor(() => form.Engine.Status == ClickStatus.Running && !form.Recovery.Pending, 7);
                    checks.Add(new("Repeated native recovery " + (cycle + 1), form.Engine.Target == target && form.Engine.RemainingDuration <= remaining && form.Engine.RemainingDuration > remaining - 2));
                    // Confirm healthy traffic resets the retry allowance, then inject the next independent loss.
                    await Task.Delay(31000); RequirePad();
                    checks.Add(new("Healthy input resets retries " + (cycle + 1), form.Recovery.Attempts == 0));
                }
                form.VerificationStop(); var stopped = (await ReadPad()).Events.Count;
                await Task.Delay(300);
                checks.Add(new("No output after reliability Stop", !(await ReadPad()).Events.Skip(stopped).Any(e => e.Kind is "down" or "up")));
                completed = true; return;
            }
            if (args.Contains("--secondary-only"))
            {
                checks.Add(new("Raw device movement monitor registered", form.MouseOutput.PhysicalMovementVersion is not null));
                foreach (bool regular in new[] { false, true }) foreach (var button in new[] { ClickButton.Left, ClickButton.Right })
                {
                    var s = Defaults(); s.RegularClickingEnabled = regular; s.Button = button;
                    s.MinInterval = s.MaxInterval = 100; s.SecondaryClickingEnabled = true;
                    s.SecondaryMinWait = s.SecondaryMaxWait = 5;
                    var area = new RectD(Math.Round(bounds.Width * .65), Math.Round(bounds.Height * .55), 80, 80);
                    var display = form.MouseOutput.MovementDisplay("")!;
                    s.MovementRegions.Add(new(display.Identity, bounds.Size, [new(area, false) { Secondary = true }]) { ReferenceBounds = bounds });
                    var before = (await ReadPad()).Events.Count;
                    await Scenario($"Secondary {button}, regular={regular}, regular interval=100s", s, 9);
                    var trace = (await ReadPad()).Events.Skip(before).ToArray();
                    var purple = area.Offset(bounds.X, bounds.Y);
                    var clicks = trace.Where(e => e.Kind == "down").ToArray();
                    checks.Add(new("Exactly one secondary click received in purple", clicks.Count(e => purple.Contains(new PointD(e.X, e.Y))) == 1));
                    checks.Add(new("No clicks during secondary travel", clicks.All(e => purple.Contains(new PointD(e.X, e.Y)) || regular && e.X == target.X && e.Y == target.Y)));
                    checks.Add(new("Selected secondary button delivered", clicks.Where(e => purple.Contains(new PointD(e.X, e.Y))).All(e => e.Button == button.ToString())));
                    checks.Add(new("Saved regular target preserved", form.Engine.Target == (regular ? target : null)));
                    checks.Add(new("Pointer returned smoothly to start/target", form.MouseOutput.CurrentPosition() == target));
                    var moves = trace.Where(e => e.Kind == "move").Skip(1).ToArray();
                    checks.Add(new("Many movement samples delivered", moves.Length > 40));
                }
                foreach (var interruption in new[] { "manual displacement", "external displacement", "Pause", "Stop", "F8", "timer expiry" })
                {
                    RequirePad(); form.Engine.ClearTargets();
                    var s = Defaults(); s.RegularClickingEnabled = false; s.SecondaryClickingEnabled = true;
                    s.SecondaryMinWait = s.SecondaryMaxWait = .2;
                    var display = form.MouseOutput.MovementDisplay("")!;
                    s.MovementRegions.Add(new(display.Identity, bounds.Size, [new(new(bounds.Width * .65, bounds.Height * .55, 80, 80), false) { Secondary = true }]) { ReferenceBounds = bounds });
                    if (interruption == "timer expiry") { s.TimerEnabled = true; s.TimerMinutes = 0; s.TimerSeconds = 1; }
                    var interruptionStart = (await ReadPad()).Events.Count;
                    form.UseVerificationSettings(s); form.MouseOutput.Move(target); form.Engine.Start(s);
                    await WaitFor(() => form.Engine.MovementPhase == "Secondary outward", 3);
                    if (interruption is "manual displacement" or "external displacement")
                    {
                        var previousClicks = form.Engine.ClicksPosted;
                        if (interruption == "external displacement") form.MouseOutput.DisplaceCursorForVerification(new(target.X + 100, target.Y - 100));
                        else form.MouseOutput.Move(new(target.X + 100, target.Y - 100));
                        if (interruption == "manual displacement")
                        {
                            // Device signal is fault-injected; the cursor displacement is real Windows delivery.
                            var changed = (form.MouseOutput.PhysicalMovementVersion ?? 0) + 1;
                            form.MouseOutput.ReadPhysicalMovement = () => changed;
                        }
                        else
                        {
                            await WaitFor(() => form.Engine.MovementNote == "Secondary visit complete.", 8);
                            checks.Add(new("External displacement recovers without manual pause or duplicate click", form.Engine.Active && form.Engine.ClicksPosted == previousClicks && form.MouseOutput.CurrentPosition() == target));
                            form.VerificationStop();
                            var recoveryMoves = (await ReadPad()).Events.Skip(interruptionStart).Where(e => e.Kind == "move")
                                .SkipWhile(e => e.X != target.X + 100 || e.Y != target.Y - 100).ToArray();
                            checks.Add(new("External recovery uses intermediate movements without a return jump", recoveryMoves.Length > 20 && recoveryMoves.Zip(recoveryMoves.Skip(1)).All(pair => PointD.Distance(new(pair.First.X, pair.First.Y), new(pair.Second.X, pair.Second.Y)) < 40)));
                        }
                    }
                    else if (interruption == "Pause") form.Pause();
                    else if (interruption == "Stop") form.VerificationStop();
                    else if (interruption == "F8")
                    {
                        form.ExpectedVerificationEmergency = true;
                        try { SendKeys([0x77], false); SendKeys([0x77], true); await Task.Delay(100); }
                        finally { form.ExpectedVerificationEmergency = false; }
                    }
                    await Task.Delay(interruption == "timer expiry" ? 1200 : 150); RequirePad();
                    checks.Add(new("Native secondary cancellation: " + interruption, !form.Engine.Active));
                    var stopped = form.MouseOutput.CurrentPosition(); var before = (await ReadPad()).Events.Count;
                    await Task.Delay(300); var after = (await ReadPad()).Events.Skip(before).ToArray();
                    checks.Add(new("No native mouse output after " + interruption, form.MouseOutput.CurrentPosition() == stopped && !after.Any(e => e.Kind is "down" or "up" or "move")));
                    if (interruption == "manual displacement") checks.Add(new("Native displacement never snaps pointer back", stopped == new PointD(target.X + 100, target.Y - 100)));
                    form.VerificationStop();
                    form.MouseOutput.ReadPhysicalMovement = originalMotionReader;
                }
                completed = true; return;
            }
            if (!args.Contains("--retry-only"))
            {
            if (!args.Contains("--timer-only"))
            {
            foreach (var button in new[] { ClickButton.Left, ClickButton.Right })
            {
                var s = Defaults(); s.Button = button; await Scenario(button + " regular", s, .8);
            }
            var varied = Defaults(); varied.ClickVariationEnabled = true; varied.ClickVariationRadius = 3; await Scenario("Variation", varied, 1.2);
            foreach (bool smallClick in new[] { false, true }) foreach (bool largeClick in new[] { false, true })
            {
                var s = Defaults(); s.SmallMovementEnabled = s.LargeMovementEnabled = true; s.MovementProbability = s.LargeMovementProbability = 1;
                s.MovementRadius = 40; s.SmallClickWhileMoving = smallClick; s.LargeClickWhileMoving = largeClick; s.LargeMovementSpeed = 5;
                await Scenario($"Travel small={smallClick} large={largeClick}", s, 5);
            }
            var movement = Defaults(); movement.RegularClickingEnabled = false; movement.SmallMovementEnabled = true; movement.MovementProbability = 1; movement.SmallClickWhileMoving = true;
            await Scenario("Movement only", movement, 1.4);
            }
            var timer = Defaults(); timer.TimerEnabled = true; timer.TimerMinutes = 0; timer.TimerSeconds = 1; timer.FinalClickEnabled = true; timer.FinalButton = ClickButton.Left;
            await Scenario("Timer and exact final click", timer, 1.4);
            timer.RegularClickingEnabled = false; await Scenario("Timer only and final click", timer, 1.4);

            form.Engine.ClearTargets(); form.UseVerificationSettings(Defaults()); form.MouseOutput.Move(target);
            RequirePad(); var keyStart = (await ReadPad()).Events.Count;
            await Chord("fgh"); await Task.Delay(400);
            checks.Add(new("FGH starts session", form.Engine.Active && form.Engine.Target == target));
            await Chord("fgh"); await Task.Delay(100);
            checks.Add(new("FGH pauses session", form.Engine.Status == ClickStatus.Paused));
            form.MouseOutput.Move(final); await Chord("iop"); await Task.Delay(100);
            checks.Add(new("IOP captures exact final target without starting", form.Engine.FinalTarget == final && !form.Engine.Active));
            await Chord("fgh"); await Task.Delay(200); RequirePad();
            form.ExpectedVerificationEmergency = true;
            try { SendKeys([0x77], false); SendKeys([0x77], true); await Task.Delay(100); }
            finally { form.ExpectedVerificationEmergency = false; }
            checks.Add(new("F8 emergency stops", form.Engine.Status == ClickStatus.Stopped));
            var keys = (await ReadPad()).Events.Skip(keyStart).Where(e => e.Kind == "keyDown").Select(e => e.Key).ToHashSet();
            checks.Add(new("Both letter chords pass through to test pad", new[] { "F", "G", "H", "I", "O", "P" }.All(keys.Contains)));
            }

            // Exercise the real heartbeat and reconnect path, including a stale nonzero hook handle.
            var recoverySettings = Defaults(); recoverySettings.TimerEnabled = true;
            recoverySettings.TimerMinutes = 15; recoverySettings.TimerSeconds = 0;
            recoverySettings.FinalClickEnabled = true;
            recoverySettings.SecondaryClickingEnabled = args.Contains("--secondary-recovery");
            form.Engine.ClearTargets(); form.UseVerificationSettings(recoverySettings);
            form.Engine.CaptureFinalTarget(final); form.MouseOutput.Move(target);
            var recoveryBefore = (await ReadPad()).Events.Count;
            form.Engine.Start(recoverySettings, target); await Task.Delay(400); RequirePad();
            if (args.Contains("--soak"))
            {
                var soakStart = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(soakStart).TotalSeconds < 360)
                {
                    await Task.Delay(1000); RequirePad();
                    if (!form.Engine.Active && !form.Recovery.Pending) throw new InvalidOperationException("Six-minute session stopped: " + form.Engine.Detail);
                }
                checks.Add(new("Six-minute native session remained active", form.Engine.Active));
            }
            var attemptsBefore = form.Recovery.TotalAttempts;
            var confirmationsBefore = form.MouseOutput.ConfirmedPairs;
            form.RemoveMouseHookForVerification();
            await WaitFor(() => form.Recovery.Pending, 5);
            var frozen = form.Engine.RemainingDuration;
            await Task.Delay(300); RequirePad();
            checks.Add(new("Recovery freezes remaining timer", frozen == form.Engine.RemainingDuration));
            form.MouseOutput.Move(new(target.X + 80, target.Y + 80));
            await WaitFor(() => form.Engine.Status == ClickStatus.Running && !form.Recovery.Pending, 5);
            checks.Add(new("Removed Windows hook is reinstalled automatically", form.Recovery.TotalAttempts == attemptsBefore + 1 && form.MouseOutput.ConfirmedPairs > confirmationsBefore));
            checks.Add(new("Automatic recovery retains both saved targets", form.Engine.Target == target && form.Engine.FinalTarget == final));
            checks.Add(new("Recovered timer continues without resetting", form.Engine.RemainingDuration <= frozen && form.Engine.RemainingDuration > frozen - 2));

            form.Pause(); var manualRemaining = form.Engine.RemainingDuration;
            form.MouseOutput.Move(new(target.X + 90, target.Y + 90)); form.RetrySamePosition();
            await WaitFor(() => form.Engine.Status == ClickStatus.Running, 3);
            checks.Add(new("Retry same position resumes saved target and timer", form.Engine.Target == target && form.Engine.RemainingDuration <= manualRemaining && form.Engine.RemainingDuration > manualRemaining - 2));

            form.RemoveMouseHookForVerification(); await WaitFor(() => form.Recovery.Pending, 5);
            var canceledAttempts = form.Recovery.TotalAttempts;
            form.Pause(); await Task.Delay(1300); RequirePad();
            checks.Add(new("Manual Pause cancels native pending retry", !form.Recovery.Pending && form.Engine.Status == ClickStatus.Paused && form.Recovery.TotalAttempts == canceledAttempts));
            form.RetrySamePosition(); await WaitFor(() => form.Engine.Status == ClickStatus.Running, 3);
            form.RemoveMouseHookForVerification(); await WaitFor(() => form.Recovery.Pending, 5);
            form.ExpectedVerificationEmergency = true;
            try { SendKeys([0x77], false); SendKeys([0x77], true); await Task.Delay(1300); }
            finally { form.ExpectedVerificationEmergency = false; }
            RequirePad();
            checks.Add(new("F8 cancels native pending retry", !form.Recovery.Pending && form.Engine.Status == ClickStatus.Stopped && form.Recovery.TotalAttempts == canceledAttempts));
            var recoveryTrace = (await ReadPad()).Events.Skip(recoveryBefore).Where(e => e.Kind is "down" or "up").ToArray();
            checks.Add(new("Independent receiver confirms retry clicks stay at original position", recoveryTrace.Length > 0 && recoveryTrace.All(e => e.X == target.X && e.Y == target.Y)));
            checks.Add(new("Retry clicks retain complete down/up pairs", recoveryTrace.Length % 2 == 0 && recoveryTrace.Chunk(2).All(p => p.Length == 2 && p[0].Kind == "down" && p[1].Kind == "up" && p[0].Button == p[1].Button)));
            var stoppedEvents = (await ReadPad()).Events.Count; await Task.Delay(400);
            checks.Add(new("No late clicks after recovery is stopped", !(await ReadPad()).Events.Skip(stoppedEvents).Any(e => e.Kind is "down" or "up")));
            completed = true;

            async Task WaitFor(Func<bool> condition, double seconds)
            {
                var deadline = Stopwatch.GetTimestamp();
                while (!condition())
                {
                    RequirePad();
                    if (Stopwatch.GetElapsedTime(deadline).TotalSeconds > seconds) throw new InvalidOperationException("Recovery test timed out: " + form.Engine.Detail);
                    await Task.Delay(50);
                }
                RequirePad();
            }
        }
        catch (Exception e) { checks.Add(new("External verification completed", false, e.Message)); }
        finally
        {
            form.VerificationStop(); form.Engine.ClearTargets(); form.MouseOutput.ReadPhysicalMovement = originalMotionReader; form.MouseOutput.VerificationProcess = null; form.UseVerificationSettings(original);
            if (pad is { HasExited: false }) { pad.CloseMainWindow(); await Task.Delay(300); if (!pad.HasExited) pad.Kill(); }
            pad?.Dispose();
            var report = new { RunId = runId, CreatedUtc = DateTime.UtcNow, Completed = completed, Checks = checks, Scenarios = scenarios, PadLog = padLog, SettingsRestored = true, Stopped = !form.Engine.Active };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = completed && checks.All(c => c.Passed) ? 0 : 1; form.VerificationRunning = false; form.Close();
        }

        async Task Chord(string letters)
        {
            RequirePad();
            var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _));
            var keys = letters.Select(c => (int)Native.VkKeyScanEx(c, layout)).ToArray();
            if (keys.Any(k => k < 0 || k > 255)) throw new InvalidOperationException("Test letters cannot be mapped without modifiers.");
            try { SendKeys(keys, false); await Task.Delay(80); }
            finally { SendKeys(keys.Reverse().ToArray(), true); }
            await Task.Delay(100);
        }
    }
    private static void SendKeys(int[] keys, bool up)
    {
        var input = keys.Select(k => new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Vk = (ushort)k, Flags = up ? 2u : 0u } } }).ToArray();
        if (Native.SendInput((uint)input.Length, input, Marshal.SizeOf<Native.Input>()) != input.Length) throw new InvalidOperationException("Windows rejected verification keyboard input.");
    }
}
