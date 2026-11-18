namespace ScoutCore;

public sealed partial class ClickEngine
{
    private double? secondaryDue, frozenSecondary;
    public int SecondarySettlingWaits { get; private set; }
    public int SecondaryReturnsReplanned { get; private set; }
    private bool SecondaryReady => Settings.SecondaryClickingEnabled && secondaryDue <= scheduler.Now;
    private void ResetSecondary() => secondaryDue = scheduler.Now + random(Settings.SecondaryMinWait, Settings.SecondaryMaxWait);

    // Only this existing continuation wakes early. No second output timer or worker.
    private void WaitForCycle(double regularDue)
        => WaitWithSecondary(regularDue, () => PrepareCycle(true));

    private void WaitWithSecondary(double regularDue, Action continuation)
    {
        var wake = secondaryDue is { } due ? Math.Min(due, regularDue) : regularDue;
        Schedule(Math.Max(0, wake - scheduler.Now), () =>
        {
            if (SecondaryReady) SecondaryVisit(() => WaitWithSecondary(Math.Max(regularDue, scheduler.Now + .02), continuation));
            else continuation();
        });
    }

    private void SecondaryVisit(Action completion)
    {
        void Skip(string reason)
        {
            ResetSecondary(); MovementNote = "Secondary visit skipped: " + reason;
            MovementPhase = null; Changed?.Invoke(); completion();
        }
        var displays = output.Displays();
        if (displays.Count == 0 || output.CurrentPosition() is not { } start) { Skip("pointer or displays unavailable."); return; }
        var physicalVersion = output.PhysicalMovementVersion;
        var back = Settings.RegularClickingEnabled ? Target ?? start : start;
        List<RectD> blocked = [], areas = [];
        foreach (var profile in Settings.MovementRegions)
        {
            var display = displays.FirstOrDefault(d => d.Identity == profile.DisplayIdentity);
            if (display is null) continue;
            if (profile.DomainFor(display) is not { } existing || (profile.ReferenceBounds is { } saved && saved != display.Bounds))
            { Skip("display geometry changed; edit and save its regions again."); return; }
            blocked.AddRange(existing.Blocked.Select(r => r.Inset(-2)));
            if (profile.Boxes.FirstOrDefault(b => b.Secondary) is { } purple)
                areas.Add(purple.Rect.Offset(display.Bounds.X, display.Bounds.Y).Inset(2));
        }
        if (areas.Count == 0) { Skip("choose a purple area on an active display."); return; }
        var x = displays.Min(d => d.Bounds.X); var y = displays.Min(d => d.Bounds.Y);
        var domain = new MovementDomain(new(x, y, displays.Max(d => d.Bounds.Right) - x, displays.Max(d => d.Bounds.Bottom) - y),
            displays.Select(d => d.Bounds).ToArray(), blocked);
        bool Safe(MovementPath path, PointD origin)
        {
            foreach (var raw in path.Points)
            {
                var p = output.Normalize(raw);
                if (!domain.PermitsSegment(origin, p) || !output.Allows(p)) return false;
                origin = p;
            }
            return true;
        }
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var area = areas[Math.Min(areas.Count - 1, (int)random(0, areas.Count))];
            if (area.Width <= 0 || area.Height <= 0) continue;
            var destination = output.Normalize(new(random(area.X, area.Right), random(area.Y, area.Bottom)));
            if (!area.Contains(destination) || !output.Allows(destination)) continue;
            var outward = CurvedMovement.Plan(start, destination, domain, random);
            var inward = CurvedMovement.Plan(destination, back, domain, random);
            if (outward is null || inward is null || !Safe(outward, start) || !Safe(inward, destination)) continue;
            var geometry = displays.ToArray();
            void Finish()
            {
                ResetSecondary(); MovementPhase = null; MovementNote = "Secondary visit complete.";
                if (UsesTravelCadence) nextRegularClickAt = scheduler.Now + ClickInterval();
                Changed?.Invoke(); completion();
            }
            bool GeometryValid()
            {
                if (geometry.SequenceEqual(output.Displays())) return true;
                Pause("Displays changed during secondary travel. Edit regions before resuming."); return false;
            }
            bool IsNear(PointD actual, PointD expected) => Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1;
            void RecoverReturn()
            {
                if (!GeometryValid()) return;
                var version = output.PhysicalMovementVersion;
                if (version is null || physicalVersion is null || version != physicalVersion)
                { Pause("Pointer movement interrupted secondary recovery. Resume explicitly when ready."); return; }
                if (output.CurrentPosition() is { } actual && domain.Contains(actual) && output.Allows(actual))
                {
                    if (IsNear(actual, back)) { Finish(); return; }
                    var returnOnly = CurvedMovement.Plan(actual, back, domain, random);
                    if (returnOnly is not null && Safe(returnOnly, actual))
                    {
                        MovementPhase = "Secondary recovery return"; Changed?.Invoke();
                        Travel(returnOnly, 0, actual, Finish); return;
                    }
                }
                MovementNote = "Secondary visit canceled: waiting for a safe return route. No click will be retried.";
                Changed?.Invoke(); Schedule(1, RecoverReturn);
            }
            void AtPosition(PointD expected, Action<PointD> arrived, double? unsettledSince = null, bool buttonHeld = false)
            {
                if (!GeometryValid()) return;
                var actual = output.CurrentPosition(); var version = output.PhysicalMovementVersion;
                if (version is not null && physicalVersion is not null && version != physicalVersion)
                {
                    Pause("Physical mouse movement paused the secondary visit. Start resumes; the regular target is unchanged.");
                    return;
                }
                if (actual is { } position && IsNear(position, expected) && domain.Contains(position) && output.Allows(position))
                { physicalVersion = version; arrived(position); return; }
                if (version is null || physicalVersion is null || version != physicalVersion)
                {
                    Pause(version is null ? "Unverified pointer displacement paused the secondary visit. Resume when ready." : "Physical mouse movement paused the secondary visit. Start resumes; the regular target is unchanged.");
                    return;
                }
                if (buttonHeld)
                {
                    output.Release(); SecondaryReturnsReplanned++;
                    MovementNote = "Secondary click interrupted by external cursor displacement; released without retrying.";
                    Changed?.Invoke(); Schedule(1, RecoverReturn); return;
                }
                // SendInput acceptance is not a guarantee that GetCursorPos has already caught up.
                // Wait without sending another movement or click. Stop/expiry still owns this continuation.
                var began = unsettledSince ?? scheduler.Now;
                if (scheduler.Now - began < .25)
                {
                    if (unsettledSince is null) SecondarySettlingWaits++;
                    Schedule(.01, () => AtPosition(expected, arrived, began)); return;
                }
                output.Release(); SecondaryReturnsReplanned++;
                MovementNote = "Secondary visit canceled after external cursor displacement; returning safely without retrying its click.";
                Changed?.Invoke(); Schedule(1, RecoverReturn);
            }
            void Travel(MovementPath path, int index, PointD expected, Action arrived)
            {
                AtPosition(expected, current =>
                {
                    if (index == path.Points.Count) { arrived(); return; }
                    var point = output.Normalize(path.Points[index]);
                    if (!domain.PermitsSegment(current, point)) { Pause("Secondary route is no longer safe."); return; }
                    try { output.Move(point); } catch (Exception e) { Pause(e.Message); return; }
                    Schedule(.018, () => Travel(path, index + 1, point, arrived));
                });
            }
            MovementPhase = "Secondary outward"; MovementNote = "Secondary visit: physical mouse movement pauses the session."; Changed?.Invoke();
            Schedule(0, () => Travel(outward, 0, start, () => AtPosition(destination, clickedAt =>
            {
                if (!area.Contains(clickedAt)) { output.Release(); Schedule(1, RecoverReturn); return; }
                MovementPhase = "Secondary click"; Changed?.Invoke();
                try { output.Down(Settings.Button, clickedAt); }
                catch (Exception e) { Pause(e.Message); return; }
                Schedule(.028, () => AtPosition(clickedAt, _ =>
                {
                    try { output.Up(Settings.Button, clickedAt); } catch (Exception e) { Pause(e.Message); return; }
                    ClicksPosted++; MovementPhase = "Secondary return"; Changed?.Invoke();
                    Travel(inward, 0, clickedAt, Finish);
                }, buttonHeld: true));
            })));
            return;
        }
        Skip("no safe outward and return curved route; red areas or disconnected displays block travel.");
    }
}
