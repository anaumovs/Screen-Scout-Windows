using System.Text.Json;
using ScoutCore;

namespace ScoutChecks;
internal static class SecondaryChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Expect(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
        Rig Setup(bool regular = false, double wait = 5)
        {
            var r = new Rig(); r.Settings.RegularClickingEnabled = regular;
            r.Settings.MinInterval = r.Settings.MaxInterval = 100;
            r.Settings.SecondaryClickingEnabled = true;
            r.Settings.SecondaryMinWait = r.Settings.SecondaryMaxWait = wait;
            r.Settings.MovementRegions.Add(new("test-display", r.Mouse.Bounds.Size, [new(new(550, 350, 100, 100), false) { Secondary = true }]) { ReferenceBounds = r.Mouse.Bounds });
            r.Mouse.RoundPositions = true;
            return r;
        }
        void Until(Rig r, Func<bool> predicate)
        {
            for (int i = 0; i < 3000 && !predicate(); i++) r.Clock.Advance(.01);
            Expect(predicate(), "Condition not reached: " + r.Engine.Detail + " / " + r.Engine.MovementNote);
        }
        test("Secondary defaults OFF and old settings retain values", () =>
        {
            var s = ClickSettings.Load("{\"minInterval\":100,\"maxInterval\":100,\"button\":\"Left\"}");
            Expect(!s.SecondaryClickingEnabled && s.SecondaryMinWait == 5 && s.SecondaryMaxWait == 10 && s.MinInterval == 100 && s.Button == ClickButton.Left);
        });
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, .19, -1 }) test("Secondary rejects minimum " + value, () =>
            Expect(new ClickSettings { SecondaryMinWait = value }.ValidationError is not null));
        foreach (var value in new[] { double.NaN, double.NegativeInfinity, 4, 3601 }) test("Secondary rejects maximum " + value, () =>
            Expect(new ClickSettings { SecondaryMaxWait = value }.ValidationError is not null));
        test("Purple persists and never becomes a green allowed box", () =>
        {
            var r = Setup(); var s = ClickSettings.Load(JsonSerializer.Serialize(r.Settings, ClickSettings.JsonOptions));
            var profile = s.MovementRegions.Single();
            Expect(profile.Boxes.Single().Secondary && profile.DomainFor(r.Mouse.MovementDisplay("")!)!.Allowed.Count == 0);
            Expect(s.Snapshot().MovementRegions[0].Boxes[0] == profile.Boxes[0]);
        });
        foreach (bool regular in new[] { false, true }) foreach (var button in new[] { ClickButton.Left, ClickButton.Right })
        test($"Secondary independent timer, paired {button}, smooth return regular={regular}", () =>
        {
            var r = Setup(regular); r.Settings.Button = button; var start = r.Mouse.Position; r.Start(regular);
            r.Clock.Advance(4.99); Expect(!r.Mouse.Events.Any(e => e.Phase?.StartsWith("Secondary") == true));
            Until(r, () => r.Engine.MovementNote == "Secondary visit complete.");
            var visits = r.Mouse.Events.Where(e => e.Phase?.StartsWith("Secondary") == true).ToArray();
            Expect(visits.First().Time >= 5 && visits.First().Time < 5.02);
            Expect(visits.Count(e => e.Kind == "down") == 1 && visits.Single(e => e.Kind == "down").Button == button && r.Paired());
            Expect(visits.Where(e => e.Kind == "down").All(e => e.Phase == "Secondary click"));
            Expect(r.Mouse.Position == start && r.Engine.Target == (regular ? start : null));
            var moves = visits.Where(e => e.Kind == "move").ToArray(); Expect(moves.Length > 40);
            var previous = start;
            foreach (var move in moves) { Expect(PointD.Distance(previous, move.Point) < 20, "Position jump"); previous = move.Point; }
            var outbound = moves.Where(e => e.Phase == "Secondary outward").ToArray();
            Expect(outbound.Any(e => Math.Abs((e.Point.X - start.X) * 200 - (e.Point.Y - start.Y) * 400) > 1000), "Path should bend");
            var finished = r.Clock.Now; r.Clock.Advance(4.98); Expect(r.Mouse.Events.Count(e => e.Phase == "Secondary click" && e.Kind == "down") == 1);
            Until(r, () => r.Mouse.Events.Count(e => e.Phase == "Secondary click" && e.Kind == "down") == 2);
            Expect(r.Clock.Now >= finished + 5); r.Engine.Stop(); Expect(r.Clock.Pending == 0);
        });
        test("Secondary pause freezes wait and Stop resets it", () =>
        {
            var r = Setup(); r.Start(false); r.Clock.Advance(2); r.Engine.Pause(); r.Clock.Advance(100, true);
            r.Engine.Start(r.Settings); r.Clock.Advance(2.99); Expect(r.Mouse.Events.Count == 0);
            r.Clock.Advance(.02); Expect(r.Engine.MovementPhase == "Secondary outward");
            r.Engine.Stop(); r.Engine.Start(r.Settings); r.Clock.Advance(4.99); Expect(r.Engine.MovementPhase is null);
        });
        foreach (var phase in new[] { "Secondary outward", "Secondary click", "Secondary return" })
        foreach (var cancel in new[] { "pause", "stop/emergency", "permission" }) test($"Secondary {cancel} cancels {phase}", () =>
        {
            var r = Setup(); r.Start(false); Until(r, () => r.Engine.MovementPhase == phase);
            if (cancel == "pause") r.Engine.Pause(); else if (cancel == "stop/emergency") r.Engine.Stop(); else { r.Mouse.Available = false; r.Engine.CheckEnvironment(); }
            var count = r.Mouse.Events.Count; r.Clock.Advance(100, true);
            Expect(r.Mouse.Held is null && r.Mouse.Events.Count == count && !r.Engine.Active);
        });
        foreach (var phase in new[] { "Secondary outward", "Secondary click", "Secondary return" }) test("Manual displacement pauses " + phase, () =>
        {
            var r = Setup(); r.Start(false); Until(r, () => r.Engine.MovementPhase == phase);
            r.Mouse.Position = new(100, 100); var count = r.Mouse.Events.Count; r.Clock.Advance(.04);
            Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Position == new PointD(100, 100) && r.Mouse.Held is null);
            Expect(!r.Mouse.Events.Skip(count).Any(e => e.Kind is "move" or "down"));
        });
        test("Windows one-pixel rounding does not pause secondary", () =>
        {
            var r = Setup(); r.Start(false); Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            r.Mouse.Position = r.Mouse.Position with { X = r.Mouse.Position.X + 1 }; r.Clock.Advance(.03); Expect(r.Engine.Active);
        });
        foreach (bool wall in new[] { false, true }) test("Red blocked route wall=" + wall, () =>
        {
            var r = Setup(); var obstacle = wall ? new RectD(390, 0, 20, 600) : new RectD(390, 150, 20, 200);
            r.Settings.MovementRegions[0].Boxes.Add(new(obstacle, true)); r.Start(false); r.Clock.Advance(9);
            if (wall) Expect(r.Mouse.Events.Count == 0 && r.Engine.MovementNote!.Contains("no safe"));
            else { Expect(r.Mouse.Downs.Length == 1); var previous = new PointD(200, 200); var d = new MovementDomain(r.Mouse.Bounds, blocked: [obstacle]); foreach (var e in r.Mouse.Events.Where(e => e.Kind == "move")) { Expect(d.PermitsSegment(previous, e.Point)); previous = e.Point; } }
        });
        test("Green areas do not restrict secondary", () =>
        {
            var r = Setup(); r.Settings.MovementRegions[0].Boxes.Add(new(new(0, 0, 40, 40), false)); r.Start(false); r.Clock.Advance(9); Expect(r.Mouse.Downs.Length == 1);
        });
        test("Changed geometry invalidates purple", () =>
        {
            var r = Setup(); r.Mouse.Bounds = new(-100, 0, 800, 600); r.Start(false); r.Clock.Advance(9); Expect(r.Mouse.Events.Count == 0 && r.Engine.MovementNote!.Contains("geometry"));
        });
        test("Timer expiry cancels secondary and retains exact final click", () =>
        {
            var r = Setup(false, .2); r.Timer(1, true); r.Mouse.OnUp = r.Engine.ConfirmDelivery; r.Start(false); r.Clock.Advance(2);
            Expect(r.Engine.Status == ClickStatus.Stopped && r.Mouse.Downs.Count(e => e.Point == new PointD(650, 450)) == 1 && r.Paired());
        });
        test("Late wake has one output continuation and no catch-up bursts", () =>
        {
            var r = Setup(); r.Start(false); r.Clock.Advance(1); r.Clock.DeliverLate(1000);
            for (int i = 0; i < 400; i++) { Expect(r.Clock.Pending <= 1); r.Clock.Advance(.01); }
            Expect(r.Mouse.Downs.Length == 1 && r.Paired());
        });
        test("Secondary disabled produces identical legacy event traces", () =>
        {
            var a = new Rig(); var b = Setup(true); b.Settings.SecondaryClickingEnabled = false;
            b.Settings.MinInterval = a.Settings.MinInterval; b.Settings.MaxInterval = a.Settings.MaxInterval; b.Mouse.RoundPositions = false;
            a.Start(); b.Start(); a.Clock.Advance(20); b.Clock.Advance(20); Expect(a.Mouse.Events.SequenceEqual(b.Mouse.Events));
        });
        test("Secondary waits until an automatic break finishes", () =>
        {
            var r = Setup(true, 1); r.Settings.MinInterval = r.Settings.MaxInterval = .3;
            r.Settings.BreaksEnabled = true; r.Settings.BreakProbability = 1; r.Settings.MinBreak = r.Settings.MaxBreak = 3;
            r.Start(); r.Clock.Advance(2); Expect(r.Engine.Status == ClickStatus.TakingBreak && !r.Mouse.Events.Any(e => e.Phase?.StartsWith("Secondary") == true));
            Until(r, () => r.Engine.MovementPhase == "Secondary outward"); Expect(r.Clock.Now > 3);
        });
        test("Secondary waits for existing movement and suppresses travel clicks", () =>
        {
            var r = Setup(true, .2); r.Travel(false, true, false, true);
            r.Start(); Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            var started = r.Clock.Now; Expect(started > .2 && r.Phases.Contains("Large return"));
            Until(r, () => r.Engine.MovementNote == "Secondary visit complete.");
            Expect(r.Mouse.Downs.Where(e => e.Time >= started).All(e => e.Phase == "Secondary click"));
        });
        foreach (bool up in new[] { false, true }) test("Secondary output failure releases button up=" + up, () =>
        {
            var r = Setup(); r.Mouse.FailDown = !up; r.Mouse.FailUp = up; r.Start(false); r.Clock.Advance(9);
            Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Held is null && r.Clock.Pending == 0);
        });
        test("Unconfirmed secondary delivery automatically reconnects and resumes", () =>
        {
            var r = Setup(); int reconnects = 0; var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => { reconnects++; return true; });
            r.Start(false); Until(r, () => r.Engine.MovementPhase == "Secondary return"); r.Clock.Advance(.3); recovery.Check(true);
            Expect(recovery.Pending && r.Engine.Status == ClickStatus.Paused); var count = r.Mouse.Downs.Length;
            r.Clock.Advance(1); recovery.Check(false);
            Expect(reconnects == 1 && r.Engine.Active && !recovery.Pending && r.Mouse.Downs.Length == count);
            Until(r, () => r.Mouse.Downs.Length > count); r.Engine.Stop(); Expect(r.Paired());
        });
        foreach (bool stop in new[] { false, true }) test("Secondary automatic retry is canceled by " + (stop ? "Stop/F8" : "Pause"), () =>
        {
            var r = Setup(); var recovery = new InputRecovery(r.Engine, r.Clock, () => true, () => throw new Exception("Unexpected restart"));
            r.Start(false); recovery.Check(true);
            if (stop) r.Engine.Stop(); else r.Engine.Pause();
            r.Clock.Advance(2); recovery.Check(false); Expect(!r.Engine.Active && !recovery.Pending);
        });
        foreach (bool gap in new[] { false, true }) test("Multi-monitor negative coordinates and disconnected desktop gap=" + gap, () =>
        {
            var r = Setup(); r.Mouse.TestDisplays = [new("left", new(-800, 0, 800, 600)), new("test-display", new(gap ? 50 : 0, 0, 800, 600))];
            r.Mouse.Position = new(-200, 200);
            if (gap) r.Settings.MovementRegions[0] = r.Settings.MovementRegions[0] with { ReferenceBounds = new(50, 0, 800, 600) };
            r.Start(false); r.Clock.Advance(11);
            if (gap) Expect(r.Mouse.Events.Count == 0 && r.Engine.MovementNote!.Contains("no safe"));
            else Expect(r.Mouse.Downs.Length == 1 && r.Mouse.Position == new PointD(-200, 200));
        });
        test("Live display change cancels secondary trip", () =>
        {
            var r = Setup(); r.Start(false); Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            r.Mouse.Bounds = new(0, 0, 801, 600); r.Clock.Advance(.03);
            Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Downs.Length == 0);
        });
        test("Both independent waits are freshly randomized after return", () =>
        {
            int draws = 0; var r = new Rig((a, b) => { if (a == 5 && b == 10) return ++draws == 1 ? 5 : 10; return (a + b) / 2; });
            var s = Setup().Settings; s.SecondaryMaxWait = 10;
            r.Engine.Start(s); Until(r, () => r.Engine.MovementNote == "Secondary visit complete."); Expect(draws == 2);
            r.Clock.Advance(9.99); Expect(r.Mouse.Downs.Length == 1);
        });
        test("Delayed Windows cursor delivery settles without false manual pause", () =>
        {
            var r = Setup(); r.Mouse.PhysicalMovementVersion = 0; r.Mouse.MoveDelay = .06;
            r.Start(false); Until(r, () => r.Engine.MovementNote == "Secondary visit complete.");
            Expect(r.Engine.Active && r.Engine.SecondarySettlingWaits > 0 && r.Engine.SecondaryReturnsReplanned == 0);
            Expect(r.Mouse.Downs.Length == 1 && r.Mouse.Position == new PointD(200, 200) && r.Paired());
        });
        foreach (string phase in new[] { "Secondary outward", "Secondary click", "Secondary return" })
        test("Non-device displacement cancels click and returns safely: " + phase, () =>
        {
            var r = Setup(); r.Mouse.PhysicalMovementVersion = 0; r.Start(false);
            Until(r, () => r.Engine.MovementPhase == phase);
            var clicks = r.Mouse.Downs.Length;
            r.Mouse.Position = new(300, 200);
            Until(r, () => r.Engine.MovementNote == "Secondary visit complete.");
            Expect(r.Engine.Active && r.Mouse.Position == new PointD(200, 200) && r.Mouse.Downs.Length == clicks && r.Paired());
            Expect(r.Engine.SecondaryReturnsReplanned == 1);
        });
        test("Genuine device movement still pauses without snapping back", () =>
        {
            var r = Setup(); r.Mouse.PhysicalMovementVersion = 0; r.Start(false);
            Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            r.Mouse.Position = new(300, 200); r.Mouse.PhysicalMovementVersion = 1; r.Clock.Advance(.03);
            Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Position == new PointD(300, 200) && r.Mouse.Downs.Length == 0);
        });
        test("Device movement is not hidden by a coincident cursor position", () =>
        {
            var r = Setup(); r.Mouse.PhysicalMovementVersion = 0; r.Start(false);
            Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            var position = r.Mouse.Position; r.Mouse.PhysicalMovementVersion = 1; r.Clock.Advance(.03);
            Expect(r.Engine.Status == ClickStatus.Paused && r.Mouse.Position == position && r.Mouse.Downs.Length == 0);
        });
        test("Raw device filter handles touchpads and ignores stationary relative reports", () =>
        {
            var filter = new ScreenScout.DeviceMotionFilter();
            Expect(filter.Observe(0, 0, 1, 0)); Expect(!filter.Observe(0, 0, 0, 0)); Expect(!filter.Observe(1, 0, 0, 0));
            Expect(filter.Observe(1, 0, -1, 0)); Expect(filter.Observe(2, 0, 0, 1));
        });
        test("Own tagged Raw Input is excluded without hiding physical touchpad reports", () =>
        {
            var filter = new ScreenScout.DeviceMotionFilter(tag => tag == 0x12345678);
            Expect(!filter.Observe(0, 3, 1000, 1000, 0x12345678));
            Expect(!filter.Observe(1, 0, 5, 5, 0x12345678));
            Expect(filter.Observe(0, 0, 5, 5));
            Expect(filter.Observe(1, 1, 1000, 1000));
        });
        test("Absolute device reports compare positions separately per device", () =>
        {
            var filter = new ScreenScout.DeviceMotionFilter();
            Expect(filter.Observe(1, 1, 1000, 1000)); Expect(!filter.Observe(1, 1, 1000, 1000));
            Expect(filter.Observe(2, 3, 3000, 4000)); Expect(!filter.Observe(2, 3, 3000, 4000));
            Expect(filter.Observe(1, 1, 1001, 1000)); Expect(!filter.Observe(2, 3, 3000, 4000));
            filter.Remove(1); Expect(filter.Observe(1, 1, 1001, 1000));
        });
        foreach (bool stop in new[] { false, true }) test("Cancel during cursor settling stop=" + stop, () =>
        {
            var r = Setup(); r.Mouse.PhysicalMovementVersion = 0; r.Start(false);
            Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            r.Mouse.Position = new(300, 200); r.Clock.Advance(.05);
            if (stop) r.Engine.Stop(); else r.Engine.Pause(); var count = r.Mouse.Events.Count;
            r.Clock.Advance(100, true); Expect(!r.Engine.Active && r.Mouse.Events.Count == count && r.Mouse.Held is null);
        });
        test("Unreachable recovery waits without clicking and remains stoppable", () =>
        {
            var r = Setup(); r.Mouse.PhysicalMovementVersion = 0;
            r.Settings.MovementRegions[0].Boxes.Add(new(new(0, 0, 100, 600), true)); r.Start(false);
            Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            r.Mouse.Position = new(50, 200); var count = r.Mouse.Events.Count;
            r.Clock.Advance(12); Expect(r.Engine.Active && r.Mouse.Events.Count == count && r.Clock.Pending == 1);
            r.Engine.Stop(); r.Clock.Advance(100, true); Expect(r.Mouse.Events.Count == count);
        });
        test("Session expiry interrupts blocked secondary recovery and releases input", () =>
        {
            var r = Setup(false, .2); r.Mouse.PhysicalMovementVersion = 0; r.Timer(2);
            r.Start(false); Until(r, () => r.Engine.MovementPhase == "Secondary outward");
            r.Mouse.MissingPointer = true; r.Clock.Advance(3);
            Expect(r.Engine.Status == ClickStatus.Stopped && r.Mouse.Held is null && r.Clock.Pending == 0);
        });
        test("25-hour accelerated session: secondary trips, delays, displacement and hourly reconnects", () =>
        {
            var r = Setup(true); r.Settings.MinInterval = .8; r.Settings.MaxInterval = 1.2;
            r.Settings.SecondaryMaxWait = 10; r.Settings.BreaksEnabled = true;
            r.Settings.BreakProbability = .7; r.Settings.MinBreak = 2; r.Settings.MaxBreak = 6;
            var random = new Random(241116);
            var engine = new ClickEngine(r.Mouse, r.Clock, (a, b) => a + random.NextDouble() * (b - a));
            var recovery = new InputRecovery(engine, r.Clock, () => true, () => true);
            r.Mouse.PhysicalMovementVersion = 0; r.Mouse.Phase = () => engine.MovementPhase;
            r.Mouse.OnUp = () => { engine.ConfirmDelivery(); recovery.ConfirmDelivery(); };
            Event? held = null; int clicks = 0, injected = 0; double nextFault = 1200;
            r.Mouse.Observe = e =>
            {
                if (e.Kind == "down") { Expect(held is null); held = e; clicks++; }
                if (e.Kind is "up" or "release") { Expect(held is not null && held.Button == e.Button && held.Point == e.Point); held = null; }
                if (e.Kind == "move")
                {
                    Expect(held is null);
                    if (e.Phase == "Secondary outward" && r.Clock.Now >= nextFault)
                    { r.Mouse.Position = new(250, 250); nextFault += 1200; injected++; }
                }
            };
            engine.Start(r.Settings, new(200, 200));
            for (int seconds = 0; seconds < 25 * 3600; seconds++)
            {
                // Inject delay in selected hours without an unbounded event history.
                r.Mouse.MoveDelay = seconds / 3600 % 3 == 1 ? .06 : 0;
                r.Clock.Advance(1); recovery.Check(false);
                if (seconds > 0 && seconds % 3600 == 0) recovery.Check(true);
                Expect(engine.Active || recovery.Pending, "Unexpected pause at simulated second " + seconds + ": " + engine.Detail);
                Expect(engine.Target == new PointD(200, 200));
                r.Mouse.Events.Clear();
            }
            engine.Stop(); Expect(held is null && clicks > 10000 && injected >= 70);
            Expect(engine.SecondarySettlingWaits > 0 && engine.SecondaryReturnsReplanned > 0 && recovery.TotalAttempts == 24);
            Console.WriteLine($"ENDURANCE 25 simulated hours; {clicks} clicks, {injected} displacement injections, {recovery.TotalAttempts} recoveries, {engine.SecondarySettlingWaits} settling waits.");
        });
    }
}
