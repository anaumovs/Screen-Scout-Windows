using ScoutCore;

namespace ScoutChecks;

internal sealed class Clock : IActionScheduler
{
    internal sealed class Token : IPendingAction { public bool Cancelled; public void Cancel() => Cancelled = true; }
    private readonly List<(double At, Token Token, Action Action)> queue = [];
    public double Now { get; private set; }
    public int Pending => queue.Count(x => !x.Token.Cancelled);
    public IPendingAction Schedule(double delay, Action action) { var token = new Token(); queue.Add((Now + delay, token, action)); return token; }
    public void Advance(double time, bool forceCancelled = false)
    {
        var until = Now + time; int limit = 100000;
        while (queue.Any(t => t.At <= until))
        {
            var item = queue.Where(t => t.At <= until).MinBy(t => t.At); queue.Remove(item); Now = item.At;
            if (forceCancelled || !item.Token.Cancelled) item.Action();
            if (--limit == 0) throw new InvalidOperationException("Unbounded scheduler loop");
        }
        Now = until;
    }
    public void DeliverLate(double delay)
    {
        Now += delay; var ready = queue.Where(t => t.At <= Now).ToArray();
        foreach (var item in ready) queue.Remove(item);
        foreach (var item in ready) if (!item.Token.Cancelled) item.Action();
    }
}
internal sealed record Event(string Kind, ClickButton? Button, PointD Point, double Time, string? Phase);
internal sealed class FakeMouse(Clock clock) : IMouseOutput
{
    public bool Available { get; set; } = true;
    public string FailureReason => "Test input unavailable";
    public PointD Position = new(200, 200);
    public List<Event> Events { get; } = [];
    public List<RectD> Forbidden { get; } = [];
    public ClickButton? Held;
    public PointD HeldPoint;
    public bool FailDown, FailUp, RoundPositions, MissingDisplay, MissingPointer;
    public long? PhysicalMovementVersion { get; set; }
    public double MoveDelay;
    public string Identity = "test-display";
    public RectD Bounds = new(0, 0, 800, 600);
    public IReadOnlyList<MovementDisplay>? TestDisplays;
    public IReadOnlyList<MovementDisplay> Displays() => TestDisplays ?? (MovementDisplay("") is { } d ? [d] : []);
    public Func<string?> Phase = () => null;
    public Action? OnUp;
    public Action<Event>? Observe { get; set; }
    public PointD? CurrentPosition() => MissingPointer ? null : Position;
    public PointD Normalize(PointD p) => RoundPositions ? new(Math.Round(p.X, MidpointRounding.AwayFromZero), Math.Round(p.Y, MidpointRounding.AwayFromZero)) : p;
    public MovementDisplay? MovementDisplay(string id) => MissingDisplay ? null : new(Identity, Bounds);
    public RectD? DisplayBounds(PointD point) => Displays().Where(d => d.Bounds.ContainsPixel(point)).Select(d => (RectD?)d.Bounds).FirstOrDefault();
    public bool Allows(PointD point) => DisplayBounds(point) is not null && !Forbidden.Any(r => r.Contains(point));
    private void Record(string kind, PointD p, ClickButton? button = null) { var e = new Event(kind, button, p, clock.Now, Phase()); Events.Add(e); Observe?.Invoke(e); }
    public void Move(PointD p)
    {
        if (Held is not null) throw new InvalidOperationException("Engine dragged a held button");
        var delivered = Normalize(p);
        if (MoveDelay > 0) clock.Schedule(MoveDelay, () => Position = delivered); else Position = delivered;
        Record("move", delivered);
    }
    public void Down(ClickButton button, PointD p) { if (FailDown || Held is not null) throw new InvalidOperationException("Posting failure"); Held = button; HeldPoint = p; Record("down", p, button); }
    public void Up(ClickButton button, PointD p) { if (FailUp) throw new InvalidOperationException("Up failure"); Held = null; Record("up", p, button); OnUp?.Invoke(); }
    public void Release() { if (Held is { } button) Record("release", HeldPoint, button); Held = null; }
    public Event[] Downs => Events.Where(e => e.Kind == "down").ToArray();
}
internal sealed class Rig
{
    public Clock Clock { get; } = new();
    public FakeMouse Mouse { get; }
    public ClickEngine Engine { get; }
    public ClickSettings Settings { get; } = new() { MinInterval = .3, MaxInterval = .6, SmallMovementEnabled = false, BreaksEnabled = false };
    public List<string> Phases { get; } = [];
    public Rig(Func<double, double, double>? random = null)
    {
        Mouse = new(Clock); Engine = new(Mouse, Clock, random ?? ((a, b) => (a + b) / 2));
        Mouse.Phase = () => Engine.MovementPhase;
        Engine.Changed = () => { if (Engine.MovementPhase is { } p && Phases.LastOrDefault() != p) Phases.Add(p); };
    }
    public void Start(bool target = true) => Engine.Start(Settings, target ? Mouse.Position : null);
    public void Timer(int seconds = 1, bool final = false)
    {
        Settings.TimerEnabled = true; Settings.TimerMinutes = 0; Settings.TimerSeconds = seconds; Settings.FinalClickEnabled = final; Settings.FinalButton = ClickButton.Left;
        if (final) Engine.CaptureFinalTarget(new(650, 450));
    }
    public void Travel(bool small = false, bool large = true, bool smallClicks = false, bool largeClicks = true, double speed = 1)
    {
        Settings.SmallMovementEnabled = small; Settings.LargeMovementEnabled = large;
        Settings.SmallClickWhileMoving = smallClicks; Settings.LargeClickWhileMoving = largeClicks;
        Settings.MovementProbability = Settings.LargeMovementProbability = 1; Settings.MovementRadius = 50;
        Settings.MinInterval = Settings.MaxInterval = .12; Settings.RandomIntervalsEnabled = false; Settings.LargeMovementSpeed = speed;
    }
    public void FirstArrival()
    {
        for (int i = 0; i < 1500; i++) { Clock.Advance(.01); if (Phases.Count > 0 && Engine.MovementPhase is null && Mouse.Held is null) return; }
        throw new InvalidOperationException("Movement never arrived");
    }
    public bool Paired()
    {
        Event? held = null;
        foreach (var e in Mouse.Events)
        {
            if (e.Kind == "move" && held is not null) return false;
            if (e.Kind == "down") { if (held is not null) return false; held = e; }
            if (e.Kind is "up" or "release") { if (held is null || e.Point != held.Point || e.Button != held.Button) return false; held = null; }
        }
        return held is null;
    }
}
