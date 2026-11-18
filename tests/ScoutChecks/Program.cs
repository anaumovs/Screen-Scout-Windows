using System.Text.Json;
using ScoutCore;
using ScoutChecks;

int checks = 0, failures = 0;
void Test(string name, Action action)
{
    checks++;
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Expect(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
void Near(double a, double b, double tolerance = 1e-7) => Expect(Math.Abs(a - b) < tolerance, $"Expected {b}, got {a}");
string Save(ClickSettings s) => JsonSerializer.Serialize(s, ClickSettings.JsonOptions);
var bounds = new RectD(0, 0, 800, 600); var from = new PointD(100, 300); var to = new PointD(700, 300);
double Mid(double a, double b) => (a + b) / 2;
// Warm the JIT before tests whose production planner deliberately has a wall-clock budget.
for (int i = 0; i < 4; i++)
{
    _ = CurvedMovement.Plan(from, to, new(bounds), Mid);
    _ = CurvedMovement.Plan(from, to, new(bounds, blocked: [new(399.9, 100, .2, 350)]), Mid);
}

Test("Default values match the Mac 2.3 behavior; Windows emergency key is F8", () =>
{
    var s = new ClickSettings();
    Expect(s.Button == ClickButton.Right && s.MinInterval == 1.4 && s.MaxInterval == 2.8 && s.MovementProbability == .15 && s.MovementRadius == 4 && s.BreakProbability == .08);
    Expect(s.SmallMovementEnabled && !s.LargeMovementEnabled && !s.SmallClickWhileMoving && !s.LargeClickWhileMoving && s.LargeMovementSpeed == 1 && s.EmergencyCode == 0x77 && s.ValidationError is null);
});
Test("31:26 is 1886 seconds", () => Near(new ClickSettings { TimerMinutes = 31, TimerSeconds = 26 }.TimerDuration, 1886));
Test("Disabled values and region profiles survive settings serialization", () =>
{
    var s = new ClickSettings { SmallMovementEnabled = false, LargeMovementEnabled = false, MovementProbability = .11, LargeMovementProbability = .88, MovementRadius = 17, LargeClickWhileMoving = true, LargeMovementSpeed = 9.5, FinalClickEnabled = true };
    s.MovementRegions.Add(new("test-display", bounds.Size, [new(new(2, 3, 100, 200), true)]));
    Expect(Save(ClickSettings.Load(Save(s))) == Save(s));
});
foreach (var invalid in new[] { "", "null", "{", "{\"minInterval\":-1}", "{\"timerHours\":-1}", "{\"movementRegions\":null}", "{\"movementRegions\":[null]}", "{\"button\":\"nonsense\"}" })
    Test("Malformed settings recover: " + invalid, () => Expect(ClickSettings.Load(invalid).ValidationError is null));
foreach (double speed in new[] { 0, .9, 10.1, double.NaN, double.PositiveInfinity })
    Test("Reject invalid movement speed " + speed, () => Expect(new ClickSettings { LargeMovementSpeed = speed }.ValidationError is not null));
foreach (var invalid in new Action<ClickSettings>[] { s => s.MinInterval = .079, s => s.MaxInterval = 3601, s => s.MaxInterval = .2, s => s.MovementProbability = -1, s => s.LargeMovementProbability = 2, s => s.MovementRadius = 51, s => s.ClickVariationRadius = 11, s => s.TimerMinutes = 60, s => s.TimerHours = 1000, s => s.TimerSeconds = -1, s => { s.TimerEnabled = true; s.TimerMinutes = 0; }, s => s.MinBreak = .1, s => s.MaxBreak = 4, s => s.EmergencyCode = 0x41, s => s.Button = (ClickButton)9 })
    Test("Invalid settings prevent starting #" + checks, () => { var r = new Rig(); invalid(r.Settings); r.Start(); Expect(!r.Engine.Active && r.Mouse.Downs.Length == 0); });
Test("Basic interval preserves spread and clamps safely", () =>
{
    var s = new ClickSettings(); s.SetBasicInterval(3); Near(s.BasicInterval, 3); Near(s.MaxInterval - s.MinInterval, 1.4);
    s.SetBasicInterval(.08); Expect(s.MinInterval == .08 && s.MaxInterval == .08); s.SetBasicInterval(9999); Expect(s.MaxInterval == 3600);
});
foreach (bool large in new[] { false, true }) foreach (bool enabled in new[] { false, true })
    Test($"Previous movement mode migrates large={large} enabled={enabled}", () =>
    {
        var s = ClickSettings.Load(JsonSerializer.Serialize(new { movementEnabled = enabled, movementMode = large ? "Full-screen random movement" : "Existing local movement", movementProbability = .73 }));
        Expect(s.SmallMovementEnabled == (enabled && !large) && s.LargeMovementEnabled == (enabled && large) && s.LargeMovementProbability == .73);
    });
Test("Final button inherits previous regular preference", () => Expect(ClickSettings.Load("{\"button\":\"Left\"}").FinalButton == ClickButton.Left));
Test("Session settings are an independent snapshot", () => { var r = new Rig(); r.Start(); r.Settings.Button = ClickButton.Left; r.Clock.Advance(.2); Expect(r.Mouse.Downs.Single().Button == ClickButton.Right); });
foreach (int[] keys in new[] { new[] { 70, 71, 72 }, new[] { 73, 79, 80 } })
{
    Test("Chord waits for all keys: " + keys[0], () => { var l = new ChordLatch(keys); Expect(!l.Update(keys[0], true) && !l.Update(keys[1], true) && l.Update(keys[2], true)); });
    Test("Chord requires every key release: " + keys[0], () =>
    {
        var l = new ChordLatch(keys); foreach (int k in keys) l.Update(k, true);
        l.Update(keys[0], false); Expect(!l.Update(keys[0], true)); foreach (int k in keys) l.Update(k, false);
        Expect(!l.Update(keys[0], true) && !l.Update(keys[1], true) && l.Update(keys[2], true));
    });
    Test("Held/repeated keys and irrelevant keys cannot toggle: " + keys[0], () => { var l = new ChordLatch(keys); foreach (var k in keys) l.Update(k, true); Expect(!l.Update(keys[2], true, true) && !l.Update(99, true)); });
    Test("RequireRelease inhibits held chord: " + keys[0], () => { var l = new ChordLatch(keys); l.RequireRelease(keys); Expect(!l.Update(keys[0], true)); foreach (var k in keys) l.Update(k, false); foreach (var k in keys[..2]) l.Update(k, true); Expect(l.Update(keys[2], true)); });
}
foreach (var button in new[] { ClickButton.Left, ClickButton.Right })
    Test("Regular " + button + " down/up coordinates match", () => { var r = new Rig(); r.Settings.Button = button; r.Start(); r.Clock.Advance(.2); Expect(r.Mouse.Downs.Single().Button == button && r.Paired()); });
Test("Starts stopped and awaits delivery acknowledgement", () => { var r = new Rig(); Expect(r.Engine.Status == ClickStatus.Stopped); r.Start(); r.Clock.Advance(.2); Expect(r.Engine.Status == ClickStatus.Starting); r.Engine.ConfirmDelivery(); Expect(r.Engine.Status == ClickStatus.Running); });
Test("Off-display target cannot start", () => { var r = new Rig(); r.Engine.Start(r.Settings, new(-1, 0)); Expect(!r.Engine.Active); });
Test("Protected controls cannot be a target", () => { var r = new Rig(); r.Mouse.Forbidden.Add(new(190, 190, 50, 50)); r.Start(); Expect(!r.Engine.Active && r.Mouse.Downs.Length == 0); });
Test("Unavailable input cannot start", () => { var r = new Rig(); r.Mouse.Available = false; r.Start(); Expect(!r.Engine.Active); });
Test("Disabled clicking, movement and timer cannot start", () => { var r = new Rig(); r.Settings.RegularClickingEnabled = false; r.Start(false); Expect(!r.Engine.Active); });
Test("Unavailable large movement display is reported", () => { var r = new Rig(); r.Settings.LargeMovementEnabled = true; r.Mouse.MissingDisplay = true; r.Start(); Expect(!r.Engine.Active); });
Test("Missing pointer skips movement without inventing a target", () => { var r = new Rig(); r.Travel(); r.Mouse.MissingPointer = true; r.Start(); r.Clock.Advance(.2); Expect(r.Engine.MovementNote is not null); });
foreach (bool stop in new[] { false, true })
    Test("Cancel pending mouse down on " + (stop ? "Stop" : "Pause"), () => { var r = new Rig(); r.Start(); r.Clock.Advance(.13); if (stop) r.Engine.Stop(); else r.Engine.Pause(); var count = r.Mouse.Events.Count; r.Clock.Advance(100, true); Expect(r.Mouse.Events.Count == count && !r.Engine.Active); });
foreach (int action in new[] { 0, 1, 2 })
    Test("Held button released on pause/stop/permission loss #" + action, () =>
    {
        var r = new Rig(); r.Start(); r.Clock.Advance(.14); Expect(r.Mouse.Held is not null);
        if (action == 0) r.Engine.Pause(); else if (action == 1) r.Engine.Stop(); else { r.Mouse.Available = false; r.Engine.CheckEnvironment(); }
        var count = r.Mouse.Events.Count; r.Clock.Advance(100, true); Expect(r.Mouse.Held is null && r.Mouse.Events.Count == count && r.Paired());
    });
Test("Posting failure pauses without a retry loop", () => { var r = new Rig(); r.Mouse.FailDown = true; r.Start(); r.Clock.Advance(100); Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Downs.Length == 0); });
Test("Button-up failure releases the held pair", () => { var r = new Rig(); r.Mouse.FailUp = true; r.Start(); r.Clock.Advance(1); Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Held is null); });
Test("Manual mouse movement neither pauses nor retargets", () => { var r = new Rig(); r.Start(); r.Clock.Advance(.2); r.Mouse.Position = new(500, 500); r.Clock.Advance(1); Expect(r.Engine.Active && r.Engine.Target == new PointD(200, 200) && r.Mouse.Downs.All(e => e.Point == new PointD(200, 200))); });
Test("Fixed intervals use midpoint without interval random draws", () =>
{
    int draws = 0; var r = new Rig((a, b) => { if (a == .3) draws++; return Mid(a, b); }); r.Settings.RandomIntervalsEnabled = false; r.Start(); r.Clock.Advance(2);
    Expect(draws == 0); var downs = r.Mouse.Downs; for (int i = 1; i < downs.Length; i++) Near(downs[i].Time - downs[i - 1].Time, .465);
});
Test("Fresh random interval drawn after each ordinary pair", () => { int draws = 0; var r = new Rig((a, b) => { if (a == .3) draws++; return b; }); r.Start(); r.Clock.Advance(3); Expect(draws >= 4 && r.Mouse.Downs.Length >= 4); });
Test("Breaks are opt-out and cancelable", () => { var r = new Rig(); r.Settings.BreaksEnabled = true; r.Settings.BreakProbability = 1; r.Start(); r.Clock.Advance(.7); Expect(r.Engine.Status == ClickStatus.TakingBreak && r.Mouse.Downs.Length == 1); r.Engine.Stop(); r.Clock.Advance(20, true); Expect(r.Mouse.Downs.Length == 1); });
Test("Disabled breaks retain probability but do not run", () => { var r = new Rig(); r.Settings.BreakProbability = 1; r.Start(); r.Clock.Advance(2); Expect(r.Mouse.Downs.Length > 3 && r.Engine.Status != ClickStatus.TakingBreak); });
foreach (bool rounded in new[] { false, true })
    Test("Variation remains within original radius without drift; pixel rounding=" + rounded, () =>
    {
        var r = new Rig(); r.Mouse.RoundPositions = rounded; r.Settings.ClickVariationEnabled = true; r.Settings.ClickVariationRadius = 2; r.Start(); r.Clock.Advance(8); r.Engine.Stop();
        Expect(r.Mouse.Downs.Length > 10 && r.Mouse.Downs.All(e => PointD.Distance(e.Point, new(200, 200)) <= 2) && r.Paired() && r.Engine.Target == new PointD(200, 200));
    });
foreach (bool enabled in new[] { false, true })
    Test("Zero/disabled variation stays exact; enabled=" + enabled, () => { var r = new Rig(); r.Settings.ClickVariationEnabled = enabled; r.Settings.ClickVariationRadius = enabled ? 0 : 3; r.Start(); r.Clock.Advance(2); Expect(r.Mouse.Downs.All(e => e.Point == new PointD(200, 200))); });
Test("Display change clears both targets and all pending work", () => { var r = new Rig(); r.Timer(final: true); r.Start(); r.Clock.Advance(.14); r.Engine.ClearTargets(); r.Clock.Advance(10, true); Expect(r.Engine.Target is null && r.Engine.FinalTarget is null && !r.Engine.Active && r.Mouse.Held is null); });
Test("Final target capture alone emits no input", () => { var r = new Rig(); Expect(r.Engine.CaptureFinalTarget(new(650, 450))); Expect(r.Mouse.Events.Count == 0 && !r.Engine.Active && r.Engine.Target is null); });
Test("Final target capture during a session pauses", () => { var r = new Rig(); r.Start(); r.Clock.Advance(.2); r.Engine.CaptureFinalTarget(new(650, 450)); Expect(r.Engine.Status == ClickStatus.Paused && r.Engine.Target == new PointD(200, 200)); });
Test("Invalid final target is rejected", () => { var r = new Rig(); Expect(!r.Engine.CaptureFinalTarget(new(-1, -1)) && r.Engine.FinalTarget is null); });
Test("Final click requires a target before start", () => { var r = new Rig(); r.Timer(); r.Settings.FinalClickEnabled = true; r.Start(); Expect(!r.Engine.Active); });
Test("Disabled timer retains final preference but never fires it", () => { var r = new Rig(); r.Settings.FinalClickEnabled = true; r.Start(); r.Clock.Advance(2); Expect(r.Engine.Active); });
Test("Timer-only session needs no regular target", () => { var r = new Rig(); r.Timer(); r.Settings.RegularClickingEnabled = false; r.Start(false); r.Clock.Advance(1.1); Expect(r.Engine.Status == ClickStatus.Stopped && r.Mouse.Events.Count == 0); });
Test("Pause freezes remaining timer; resume preserves it", () => { var r = new Rig(); r.Timer(10); r.Start(); r.Clock.Advance(3); r.Engine.Pause(); Near(r.Engine.RemainingDuration!.Value, 7); r.Clock.Advance(20); r.Engine.Start(r.Settings); Near(r.Engine.RemainingDuration!.Value, 7); });
Test("Stop resets timer on next fresh start", () => { var r = new Rig(); r.Timer(10); r.Start(); r.Clock.Advance(3); r.Engine.Stop(); r.Start(); Near(r.Engine.RemainingDuration!.Value, 10); });
Test("Changing timer duration resets next Start", () => { var r = new Rig(); r.Timer(10); r.Start(); r.Clock.Advance(3); r.Engine.Pause(); r.Settings.TimerSeconds = 20; r.Engine.Start(r.Settings); Near(r.Engine.RemainingDuration!.Value, 20); });
foreach (string interruption in new[] { "pause", "stop", "permission", "target", "late" })
    Test("No final click after " + interruption, () =>
    {
        var r = new Rig(); r.Timer(final: true); r.Start(); r.Clock.Advance(.2);
        switch (interruption) { case "pause": r.Engine.Pause(); break; case "stop": r.Engine.Stop(); break; case "permission": r.Mouse.Available = false; r.Engine.CheckEnvironment(); break; case "target": r.Mouse.Forbidden.Add(new(640, 440, 20, 20)); r.Engine.CheckEnvironment(); break; case "late": r.Clock.DeliverLate(3); break; }
        r.Clock.Advance(20, true); Expect(!r.Mouse.Downs.Any(e => e.Button == ClickButton.Left));
    });
foreach (bool regular in new[] { false, true })
    Test("Natural timer expiry sends exactly one final pair; regular=" + regular, () =>
    {
        var r = new Rig(); r.Timer(final: true); r.Settings.RegularClickingEnabled = regular; r.Start(regular); r.Clock.Advance(1.1); r.Engine.ConfirmDelivery(); var count = r.Mouse.Events.Count; r.Clock.Advance(20, true);
        Expect(r.Mouse.Downs.Count(e => e.Button == ClickButton.Left && e.Point == new PointD(650, 450)) == 1 && r.Engine.Status == ClickStatus.Stopped && r.Mouse.Events.Count == count && r.Paired());
    });
Test("Final delivery acknowledgement can be synchronous", () => { var r = new Rig(); r.Mouse.OnUp = r.Engine.ConfirmDelivery; r.Timer(final: true); r.Start(); r.Clock.Advance(1.1); Expect(r.Engine.Status == ClickStatus.Stopped && r.Clock.Pending == 0); });
Test("Unconfirmed final click is not retried", () => { var r = new Rig(); r.Timer(final: true); r.Start(); r.Clock.Advance(20); Expect(r.Mouse.Downs.Count(e => e.Button == ClickButton.Left) == 1 && !r.Engine.Active && r.Engine.Detail.Contains("unconfirmed")); });
Test("Timer interrupts a long interval", () => { var r = new Rig(); r.Timer(); r.Settings.MinInterval = r.Settings.MaxInterval = 100; r.Start(); r.Clock.Advance(1.01); Expect(!r.Engine.Active && r.Mouse.Downs.Length == 1); });
Test("Timer includes automatic breaks", () => { var r = new Rig(); r.Timer(final: true); r.Settings.BreaksEnabled = true; r.Settings.BreakProbability = 1; r.Start(); r.Clock.Advance(1.1); r.Engine.ConfirmDelivery(); Expect(!r.Engine.Active && r.Mouse.Downs.Length == 2); });

GeometryTests();
TravelTests();
RecoveryTests();
Test("Two-hour session survives independent hook losses without losing targets or timer", () =>
{
    var r = new Rig(); r.Timer(final: true); r.Settings.TimerHours = 3;
    var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => true);
    r.Mouse.OnUp = () => { r.Engine.ConfirmDelivery(); recovery.ConfirmDelivery(); };
    r.Start(); r.Clock.Advance(7200);
    for (int i = 0; i < 8; i++)
    {
        var remaining = r.Engine.RemainingDuration; recovery.Check(true);
        r.Clock.Advance(1); recovery.Check(false); r.Clock.Advance(.2);
        Expect(r.Engine.Active && r.Engine.Target == new PointD(200, 200) && r.Engine.FinalTarget == new PointD(650, 450));
        Expect(r.Engine.RemainingDuration <= remaining && r.Engine.RemainingDuration > remaining - 1);
        r.Clock.Advance(31); recovery.Check(false); Expect(recovery.Attempts == 0);
    }
    r.Engine.Stop(); Expect(r.Paired());
});
SecondaryChecks.Run(Test);
Console.WriteLine($"{checks} checks, {failures} failures.");
return failures == 0 ? 0 : 1;

void RecoveryTests()
{
    Test("Recovery after six minutes preserves both targets and remaining timer", () =>
    {
        var r = new Rig(); r.Timer(final: true); r.Settings.TimerHours = 1;
        int repairs = 0; var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => { repairs++; return true; });
        r.Mouse.OnUp = () => { r.Engine.ConfirmDelivery(); recovery.ConfirmDelivery(); };
        r.Start(); r.Clock.Advance(360); var left = r.Engine.RemainingDuration!.Value;
        recovery.Check(true); Expect(recovery.Pending && !r.Engine.Active && r.Mouse.Held is null);
        var count = r.Mouse.Downs.Length; r.Mouse.Position = new(500, 500);
        r.Clock.Advance(.9, true); recovery.Check(false); Expect(repairs == 0 && r.Mouse.Downs.Length == count);
        Near(r.Engine.RemainingDuration!.Value, left);
        r.Clock.Advance(.1); recovery.Check(false); Near(r.Engine.RemainingDuration!.Value, left);
        r.Clock.Advance(.2); Expect(repairs == 1 && r.Engine.Status == ClickStatus.Running && r.Mouse.Downs.Length == count + 1);
        Expect(r.Engine.Target == new PointD(200, 200) && r.Engine.FinalTarget == new PointD(650, 450));
        Expect(r.Mouse.Downs.Last().Point == new PointD(200, 200) && r.Paired());
    });
    foreach (string action in new[] { "pause", "stop", "clear targets", "change final", "new session" })
        Test("Pending recovery cannot restart after " + action, () =>
        {
            var r = new Rig(); int repairs = 0; var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => { repairs++; return true; });
            r.Start(); r.Clock.Advance(.2); recovery.Check(true);
            switch (action)
            {
                case "pause": r.Engine.Pause(); break;
                case "stop": r.Engine.Stop(); break;
                case "clear targets": r.Engine.ClearTargets(); break;
                case "change final": recovery.Cancel(); r.Engine.CaptureFinalTarget(new(650, 450)); break;
                case "new session": r.Engine.Start(r.Settings, new(300, 300)); break;
            }
            r.Clock.Advance(10, true); recovery.Check(false);
            Expect(!recovery.Pending && repairs == 0);
            Expect(action == "new session" ? r.Engine.Target == new PointD(300, 300) : !r.Engine.Active);
        });
    foreach (bool reconnects in new[] { false, true })
        Test("Repeated recovery failure stops after three attempts; reconnects=" + reconnects, () =>
        {
            var r = new Rig(); int repairs = 0; var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => { repairs++; return reconnects; });
            r.Start(); r.Clock.Advance(.2); recovery.Check(true);
            foreach (var delay in new[] { 1, 2, 4 })
            {
                r.Clock.Advance(delay); recovery.Check(false);
                if (reconnects) { r.Clock.Advance(.2); recovery.Check(true); }
            }
            r.Clock.Advance(100, true); recovery.Check(false);
            Expect(repairs == 3 && !recovery.Pending && r.Engine.Status == ClickStatus.Paused && r.Mouse.Held is null);
            Expect(r.Engine.Detail.Contains("3 attempts"));
        });
    Test("Healthy confirmations restore retry allowance after 30 seconds", () =>
    {
        var r = new Rig(); var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => true);
        r.Mouse.OnUp = () => { r.Engine.ConfirmDelivery(); recovery.ConfirmDelivery(); };
        r.Start(); r.Clock.Advance(.2); recovery.Check(true); r.Clock.Advance(1); recovery.Check(false);
        r.Clock.Advance(.2); recovery.Check(false); Expect(recovery.Attempts == 1);
        r.Clock.Advance(31); recovery.Check(false); Expect(recovery.Attempts == 0 && recovery.TotalAttempts == 1);
        recovery.Check(true); r.Clock.Advance(1); recovery.Check(false); Expect(recovery.TotalAttempts == 2 && r.Engine.Active);
    });
    Test("Desktop loss cancels recovery permanently until explicit resume", () =>
    {
        var r = new Rig(); bool ready = true; int repairs = 0;
        var recovery = new InputRecovery(r.Engine, r.Clock, () => ready, () => { repairs++; return true; });
        r.Start(); r.Clock.Advance(.2); recovery.Check(true); ready = false; recovery.Check(false);
        ready = true; r.Clock.Advance(100, true); recovery.Check(false);
        Expect(repairs == 0 && !recovery.Pending && !r.Engine.Active);
    });
    Test("Invalid saved target cannot resume through recovery", () =>
    {
        var r = new Rig(); var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => true);
        r.Start(); r.Clock.Advance(.2); recovery.Check(true); var count = r.Mouse.Downs.Length;
        r.Mouse.Forbidden.Add(new(190, 190, 20, 20)); r.Clock.Advance(1); recovery.Check(false); r.Clock.Advance(10);
        Expect(!r.Engine.Active && r.Mouse.Downs.Length == count);
    });
    Test("Retry same position repairs monitor and resumes frozen timer without retargeting", () =>
    {
        var r = new Rig(); r.Timer(50, true); int repairs = 0;
        var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => { repairs++; return true; });
        r.Start(); r.Clock.Advance(3); r.Engine.Pause(); r.Mouse.Position = new(500, 500); r.Clock.Advance(10);
        Expect(recovery.RetrySamePosition(r.Settings)); Near(r.Engine.RemainingDuration!.Value, 47);
        r.Clock.Advance(.2); Expect(repairs == 1 && r.Mouse.Downs.Last().Point == new PointD(200, 200));
        Expect(r.Engine.FinalTarget == new PointD(650, 450));
    });
    Test("Manual retry failure preserves saved timer and stays paused", () =>
    {
        var r = new Rig(); r.Timer(50); var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => false);
        r.Start(); r.Clock.Advance(3); r.Engine.Pause(); Expect(!recovery.RetrySamePosition(r.Settings));
        Near(r.Engine.RemainingDuration!.Value, 47); Expect(!r.Engine.Active && !recovery.Pending && r.Engine.Target is not null);
    });
    Test("Recovery never retries uncertain final clicks", () =>
    {
        var r = new Rig(); r.Timer(final: true); int repairs = 0;
        var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => { repairs++; return true; });
        r.Start(); r.Clock.Advance(1.05); Expect(r.Engine.Status == ClickStatus.Finishing);
        recovery.Check(true); r.Clock.Advance(10, true); recovery.Check(false);
        Expect(repairs == 0 && !recovery.Pending && !r.Engine.Active && !recovery.RetrySamePosition(r.Settings));
        Expect(r.Mouse.Downs.Count(e => e.Point == new PointD(650, 450)) == 1);
    });
    Test("Retry button cannot start a stopped session", () =>
    {
        var r = new Rig(); var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => true);
        r.Start(); r.Clock.Advance(.2); r.Engine.Stop(); Expect(!recovery.RetrySamePosition(r.Settings));
        r.Clock.Advance(10, true); Expect(r.Mouse.Downs.Length == 1);
    });
}

void GeometryTests()
{
    Test("Allowed union with blocked priority", () =>
    {
        var d = new MovementDomain(bounds, [new(50, 50, 400, 400), new(300, 50, 400, 400)], [new(350, 200, 20, 50)]);
        Expect(d.Contains(new(600, 100)) && d.PermitsSegment(new(100, 100), new(600, 100)) && !d.Contains(new(355, 220)) && !d.PermitsSegment(new(100, 220), new(600, 220)) && !d.Contains(new(0, 0)));
    });
    Test("Subpixel obstacles and tangent contact cannot be skipped", () => { var d = new MovementDomain(bounds, blocked: [new(333.333, 200, .0001, 100)]); Expect(!d.PermitsSegment(from, to) && !d.PermitsSegment(new(100, 200), new(700, 200))); });
    Test("Whole cubic rejected when bow crosses a blocked rectangle", () => { var d = new MovementDomain(bounds, blocked: [new(300, 330, 200, 120)]); Expect(d.PermitsSegment(from, to) && !d.PermitsCurve(new(from, new(250, 450), new(550, 450), to))); });
    Test("Disconnected allowed rectangles cannot be bridged", () => { var d = new MovementDomain(bounds, [new(0, 0, 300, 600), new(500, 0, 300, 600)]); Expect(d.Route(from, to) is null && CurvedMovement.Plan(from, to, d, Mid) is null); });
    Test("A solid barrier has no route", () => Expect(new MovementDomain(bounds, blocked: [new(399, 0, 2, 600)]).Route(from, to) is null));
    Test("Narrow barrier is routed around with an independently sampled trace", () =>
    {
        var obstacle = new RectD(399.9, 100, .2, 350); var d = new MovementDomain(bounds, blocked: [obstacle]);
        var path = CurvedMovement.Plan(from, to, d, Mid) ?? throw new Exception("No route");
        Expect(path.Curves.Count > 1 && path.Points[^1] == to);
        foreach (var c in path.Curves) for (int i = 0; i <= 1000; i++) Expect(!obstacle.Contains(c.Point(i / 1000.0)));
        var previous = from; foreach (var p in path.Points) { Expect(d.PermitsSegment(previous, p)); previous = p; }
    });
    Test("Outward and return paths are independently curved", () =>
    {
        var a = CurvedMovement.Plan(from, to, new(bounds), Mid)!; var b = CurvedMovement.Plan(to, from, new(bounds), Mid)!;
        Expect(a.Points.Any(p => Math.Abs(p.Y - 300) > 10) && PointD.Distance(a.Curves[0].Point(.5), b.Curves[0].Point(.5)) > 20);
    });
    Test("Profile follows display rearrangement including negative origin", () => { var p = new RegionProfile("a", bounds.Size, [new(new(10, 10, 50, 50), false)]); Expect(p.DomainFor(new("a", bounds.Offset(-800, 120)))!.Contains(new(-780, 140))); });
    Test("Profile rejects different display identity or size", () => { var p = new RegionProfile("a", bounds.Size, []); Expect(p.DomainFor(new("b", bounds)) is null && p.DomainFor(new("a", new(0, 0, 400, 300))) is null); });
    Test("Profile rejects 33 boxes and out-of-bounds boxes", () =>
    {
        var a = new RegionProfile("a", bounds.Size, Enumerable.Range(0, 33).Select(_ => new MovementBox(new(10, 10, 5, 5), true)).ToList());
        var b = new RegionProfile("a", bounds.Size, [new(new(790, 0, 50, 20), false)]);
        Expect(a.DomainFor(new("a", bounds)) is null && b.DomainFor(new("a", bounds)) is null);
    });
    Test("Nonfinite points cannot authorize movement", () => { var d = new MovementDomain(bounds); Expect(!d.Contains(new(double.NaN, 0)) && !d.PermitsSegment(from, new(double.PositiveInfinity, 0))); });
}
void TravelTests()
{
    foreach (bool small in new[] { false, true }) foreach (bool large in new[] { false, true })
        Test($"Independent movement switches small={small} large={large}", () =>
        {
            var r = new Rig(); r.Travel(small, large, false, false); r.Settings.MinInterval = r.Settings.MaxInterval = 100;
            r.Start(); for (int i = 0; i < 800; i++) { r.Clock.Advance(.01); Expect(r.Clock.Pending <= 1); }
            var expected = (small ? new[] { "Small outward", "Small return" } : []).Concat(large ? new[] { "Large outward", "Large return" } : []);
            Expect(r.Phases.SequenceEqual(expected) && r.Mouse.Downs.Length == 1 && r.Paired());
        });
    foreach (bool large in new[] { false, true })
    {
        Test("Movement only returns to its start; large=" + large, () =>
        {
            var r = new Rig(); r.Travel(!large, large, false, false); r.Settings.RegularClickingEnabled = false; r.Settings.MinInterval = r.Settings.MaxInterval = 100;
            var original = r.Mouse.Position; r.Start(false); r.FirstArrival();
            Expect(r.Mouse.Position == original && r.Mouse.Downs.Length == 0 && r.Engine.Target is null);
        });
        foreach (bool clicks in new[] { false, true }) foreach (double speed in new[] { 1.0, 10.0 })
            Test($"Travel clicks are independent, paired, and stop cleanly; large={large}, clicks={clicks}, speed={speed}", () =>
            {
                var r = new Rig(); r.Travel(!large, large, !large && clicks, large && clicks, speed);
                r.Start(); r.FirstArrival();
                var travel = r.Mouse.Downs.Where(e => e.Phase is not null).ToArray();
                Expect(clicks ? travel.Length > 0 : travel.Length == 0);
                Expect(r.Paired()); r.Engine.Stop(); var count = r.Mouse.Events.Count; r.Clock.Advance(100, true);
                Expect(count == r.Mouse.Events.Count && r.Mouse.Held is null && r.Clock.Pending == 0);
            });
        Test("Pause during travel cancels all input; large=" + large, () =>
        {
            var r = new Rig(); r.Travel(!large, large, true, true); r.Start(); r.Clock.Advance(.3);
            r.Engine.Pause(); var count = r.Mouse.Events.Count; r.Clock.Advance(30, true);
            Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Events.Count == count && r.Paired());
        });
    }
    Test("Missing pointer skips movement without losing target", () =>
    {
        var r = new Rig(); r.Travel(); r.Mouse.MissingPointer = true; r.Start(); r.Clock.Advance(.3);
        Expect(r.Engine.Target == new PointD(200, 200) && r.Engine.MovementNote is not null);
    });
    Test("Large movement rejects missing display before sending input", () =>
    { var r = new Rig(); r.Travel(); r.Mouse.MissingDisplay = true; r.Start(); r.Clock.Advance(3); Expect(!r.Engine.Active && r.Mouse.Events.Count == 0); });
    Test("Travel cannot drag while holding a button", () =>
    {
        var r = new Rig(); r.Travel(true, true, true, true); r.Start(); r.Clock.Advance(8); r.Engine.Stop(); Expect(r.Paired());
    });
}
