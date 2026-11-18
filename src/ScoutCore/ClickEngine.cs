using System.Diagnostics;

namespace ScoutCore;

public enum ClickStatus { Stopped, Paused, Starting, Running, TakingBreak, Finishing }
public interface IMouseOutput
{
    bool Available { get; }
    string FailureReason { get; }
    PointD? CurrentPosition();
    long? PhysicalMovementVersion => null;
    MovementDisplay? MovementDisplay(string id);
    IReadOnlyList<MovementDisplay> Displays() => MovementDisplay("") is { } d ? [d] : [];
    RectD? DisplayBounds(PointD point);
    bool Allows(PointD point);
    PointD Normalize(PointD point) => point;
    void Move(PointD point);
    void Down(ClickButton button, PointD point);
    void Up(ClickButton button, PointD point);
    void Release();
}
public interface IPendingAction { void Cancel(); }
public interface IActionScheduler
{
    double Now { get; }
    IPendingAction Schedule(double delay, Action action);
}

/// All state belongs to one dispatcher. Timer expiry and animation share one continuation.
public sealed partial class ClickEngine(IMouseOutput output, IActionScheduler scheduler, Func<double, double, double>? randomSource = null)
{
    private readonly Func<double, double, double> random = randomSource ?? ((a, b) => a + Random.Shared.NextDouble() * (b - a));
    public ClickStatus Status { get; private set; }
    public PointD? Target { get; private set; }
    public PointD? FinalTarget { get; private set; }
    public string Detail { get; private set; } = "Position your mouse over the target, then press F + G + H.";
    public ClickSettings Settings { get; private set; } = new();
    public int ClicksPosted { get; private set; }
    public double? BreakEndsAt { get; private set; }
    public string? MovementNote { get; private set; }
    public string? MovementPhase { get; private set; }
    public Action? Changed { get; set; }
    public bool Active => Status is ClickStatus.Starting or ClickStatus.Running or ClickStatus.TakingBreak or ClickStatus.Finishing;
    public double? RemainingDuration => deadline is { } d ? Math.Max(0, d - scheduler.Now) : frozenRemaining;
    private IPendingAction? pending;
    private ulong generation;
    // Changes whenever a session is started, paused, stopped, or completes.
    public ulong SessionVersion => generation;
    private bool delivered, finalAttempted, awaitingFinalDelivery;
    private double clickBegan, cycleBegan;
    private PointD? clickPoint;
    private double? nextRegularClickAt, deadline, frozenRemaining;
    private bool UsesTravelCadence => Settings.RegularClickingEnabled && ((Settings.SmallMovementEnabled && Settings.SmallClickWhileMoving) || (Settings.LargeMovementEnabled && Settings.LargeClickWhileMoving));

    public bool CaptureFinalTarget(PointD point)
    {
        point = output.Normalize(point);
        if (!output.Allows(point))
        {
            if (Active) Pause("Final target is invalid. Session paused; set it outside Screen Scout with I + O + P.");
            else { Detail = "Final target is invalid. Set it outside Screen Scout with I + O + P."; Changed?.Invoke(); }
            return false;
        }
        if (Active) Pause("Final target changed. Resume explicitly to continue.");
        FinalTarget = point; Changed?.Invoke(); return true;
    }
    public void Start(ClickSettings settings, PointD? position = null)
    {
        var secondaryRemaining = Status == ClickStatus.Paused ? frozenSecondary : null;
        bool resume = Status == ClickStatus.Paused && settings.TimerEnabled && Settings.TimerEnabled && settings.TimerDuration == Settings.TimerDuration;
        var remaining = resume ? frozenRemaining : null;
        CancelPending(); output.Release(); deadline = null; delivered = false;
        if (settings.ValidationError is { } error) { Stop(error); return; }
        if (!output.Available) { Pause(output.FailureReason); return; }
        if (!settings.RegularClickingEnabled && !settings.MovementEnabled && !settings.TimerEnabled && !settings.SecondaryClickingEnabled) { Stop("Enable clicking, movement, or the session timer before starting."); return; }
        var candidate = position is { } p ? output.Normalize(p) : Target;
        if ((settings.RegularClickingEnabled || position is not null) && (candidate is null || !output.Allows(candidate.Value))) { Pause("Set a target on an active display, outside Screen Scout's visible controls."); return; }
        if (settings.FinalClickEffective && (FinalTarget is null || !output.Allows(FinalTarget.Value))) { Stop("Set a valid final target with I + O + P before starting."); return; }
        if (settings.LargeMovementEnabled && output.MovementDisplay(settings.MovementDisplayId) is null) { Stop("The selected movement display is unavailable. Choose an active display."); return; }
        Settings = settings.Snapshot(); secondaryDue = settings.SecondaryClickingEnabled ? scheduler.Now + (secondaryRemaining ?? random(settings.SecondaryMinWait, settings.SecondaryMaxWait)) : null; frozenSecondary = null; Target = candidate; BreakEndsAt = null;
        finalAttempted = false; awaitingFinalDelivery = false; clickPoint = null;
        nextRegularClickAt = UsesTravelCadence ? scheduler.Now + .135 : null;
        frozenRemaining = settings.TimerEnabled ? remaining ?? settings.TimerDuration : null;
        if (frozenRemaining is { } duration) deadline = scheduler.Now + duration;
        Status = settings.RegularClickingEnabled ? ClickStatus.Starting : ClickStatus.Running;
        Detail = settings.RegularClickingEnabled ? "Waiting for Windows to confirm the first click in its event stream." : "Session running. Regular clicking is off.";
        Changed?.Invoke(); Schedule(.12, () => PrepareCycle(false));
    }
    public void Pause(string reason = "Paused. F + G + H captures a new regular target and resumes.")
    {
        if (Status == ClickStatus.Finishing) { Stop("Final click interrupted; it will not be retried."); return; }
        frozenSecondary = secondaryDue is { } sd ? Math.Max(0, sd - scheduler.Now) : frozenSecondary; secondaryDue = null;
        frozenRemaining = RemainingDuration; deadline = null;
        CancelPending(); output.Release(); BreakEndsAt = null; clickPoint = null;
        Status = ClickStatus.Paused; Detail = reason; Changed?.Invoke();
    }
    public void Stop(string reason = "Stopped. No clicks, movement, timer, or breaks are queued.")
    {
        secondaryDue = frozenSecondary = null;
        CancelPending(); output.Release(); deadline = null; frozenRemaining = null;
        BreakEndsAt = null; clickPoint = null; awaitingFinalDelivery = false;
        Status = ClickStatus.Stopped; Detail = reason; Changed?.Invoke();
    }
    public void ConfirmDelivery()
    {
        if (!Active) return;
        if (Status == ClickStatus.Finishing) { if (awaitingFinalDelivery) Complete("Timer complete. One exact final click delivered; session stopped."); return; }
        delivered = true;
        if (Status == ClickStatus.Starting)
        {
            Status = ClickStatus.Running;
            Detail = UsesTravelCadence ? "Clicking along enabled movement paths and at the saved target. Manual mouse movement does not pause." : "Clicking near the saved target. Manual mouse movement does not pause.";
            Changed?.Invoke();
        }
    }
    public void ClearTargets() { Stop(); Target = null; FinalTarget = null; Changed?.Invoke(); }
    public void CheckEnvironment()
    {
        if (!Active) return;
        if (!output.Available) { Pause(output.FailureReason); return; }
        if (Settings.FinalClickEffective && (FinalTarget is null || !output.Allows(FinalTarget.Value))) { Stop("The final target is no longer valid. No final click will be sent; set it again with I + O + P."); return; }
        if (Settings.RegularClickingEnabled && (Target is null || !output.Allows(Target.Value))) Pause("The regular target is unavailable or covered by Screen Scout. Reposition and restart.");
    }
    private void CancelPending() { nextRegularClickAt = null; MovementPhase = null; generation++; pending?.Cancel(); pending = null; }
    private void Schedule(double delay, Action action)
    {
        var token = generation;
        var wait = deadline is { } d ? Math.Min(Math.Max(0, delay), Math.Max(0, d - scheduler.Now)) : Math.Max(0, delay);
        pending?.Cancel();
        pending = scheduler.Schedule(wait, () =>
        {
            if (generation != token || !Active) return;
            pending = null; CheckEnvironment();
            if (!Active) return;
            if (deadline is { } end && scheduler.Now >= end)
            {
                if (scheduler.Now - end >= .75) { Stop("Timer completion was interrupted. Final click skipped; restart explicitly."); return; }
                Expire(); return;
            }
            action();
        });
    }
    private void PrepareCycle(bool allowBreak)
    {
        if (allowBreak && Settings.BreaksEnabled && random(0, 1) < Settings.BreakProbability)
        {
            var duration = random(Settings.MinBreak, Settings.MaxBreak);
            Status = ClickStatus.TakingBreak; BreakEndsAt = scheduler.Now + duration;
            Detail = $"Resuming automatically after a {duration:F1}-second break."; Changed?.Invoke();
            Schedule(duration, () => { BreakEndsAt = null; Status = !Settings.RegularClickingEnabled || delivered ? ClickStatus.Running : ClickStatus.Starting; Changed?.Invoke(); PrepareCycle(false); });
            return;
        }
        cycleBegan = scheduler.Now; MovementNote = Settings.SecondaryClickingEnabled && MovementNote?.StartsWith("Secondary") == true ? MovementNote : null; MovementPhase = null;
        clickPoint = SampleClickPoint();
        bool small = Settings.SmallMovementEnabled && random(0, 1) < Settings.MovementProbability;
        bool large = Settings.LargeMovementEnabled && random(0, 1) < Settings.LargeMovementProbability;
        if (small && Settings.MovementRadius > 0) SmallExcursion(() => { if (large) LargeExcursion(); else AfterMovement(); });
        else if (large) LargeExcursion(); else AfterMovement();
    }
    private PointD? SampleClickPoint()
    {
        if (!Settings.RegularClickingEnabled || Target is not { } target) return null;
        if (Settings.ClickVariationEnabled && Settings.ClickVariationRadius > 0 && output.DisplayBounds(target) is { } bounds)
        {
            for (int i = 0; i < 24; i++)
            {
                var angle = random(0, 2 * Math.PI); var radius = Math.Sqrt(random(0, 1)) * Settings.ClickVariationRadius;
                var candidate = output.Normalize(new(target.X + Math.Cos(angle) * radius, target.Y + Math.Sin(angle) * radius));
                if (bounds.ContainsPixel(candidate) && PointD.Distance(candidate, target) <= Settings.ClickVariationRadius && output.Allows(candidate)) return candidate;
            }
        }
        return target;
    }
    private void SkipMovement(string reason, Action completion) { MovementNote = reason; MovementPhase = null; Changed?.Invoke(); completion(); }
    private void SmallExcursion(Action completion)
    {
        if (output.CurrentPosition() is not { } start || output.DisplayBounds(start) is not { } bounds) { SkipMovement("Small movement skipped: pointer is off-display.", completion); return; }
        var anchor = Target ?? start; var back = clickPoint ?? Target ?? start;
        var domain = new MovementDomain(bounds.Inset(.001));
        for (int i = 0; i < 12; i++)
        {
            var angle = random(0, 2 * Math.PI); var radius = random(0, Settings.MovementRadius);
            var destination = output.Normalize(new(anchor.X + Math.Cos(angle) * radius, anchor.Y + Math.Sin(angle) * radius));
            if (!output.Allows(destination)) continue;
            var outward = CurvedMovement.Plan(start, destination, domain, random); var inward = CurvedMovement.Plan(destination, back, domain, random);
            if (outward is null || inward is null) continue;
            MovementPhase = "Small outward"; Changed?.Invoke();
            Schedule(0, () => MovePath(outward.Points, 0, null, start, .018, Settings.SmallClickWhileMoving, null, () =>
            {
                MovementPhase = "Small return"; Changed?.Invoke();
                MovePath(inward.Points, 0, null, destination, .018, Settings.SmallClickWhileMoving, null, completion);
            })); return;
        }
        SkipMovement("Small movement skipped: no valid curved path on the current display.", completion);
    }
    private void LargeExcursion()
    {
        if (output.MovementDisplay(Settings.MovementDisplayId) is not { } display) { SkipMovement("Large movement skipped: selected display is unavailable.", AfterMovement); return; }
        if (Settings.MovementDisplayIdentity is { } identity && identity != display.Identity) { SkipMovement("Large movement skipped: display identity changed. Select the intended display and edit its regions.", AfterMovement); return; }
        var profile = Settings.MovementRegions.FirstOrDefault(p => p.DisplayIdentity == display.Identity);
        var domain = profile is null ? new MovementDomain(display.Bounds.Inset(.001)) : profile.DomainFor(display);
        if (domain is null) { SkipMovement("Large movement skipped: display size or region data changed. Correct and save its regions.", AfterMovement); return; }
        if (output.CurrentPosition() is not { } start || !domain.Contains(start)) { SkipMovement("Large movement skipped: current pointer is outside the permitted regions.", AfterMovement); return; }
        var back = clickPoint ?? Target ?? start;
        if (!domain.Contains(back)) { SkipMovement("Large movement skipped: required return position is outside the permitted regions.", AfterMovement); return; }
        var clock = Stopwatch.StartNew();
        var areas = domain.Allowed.Count == 0 ? new[] { domain.Bounds } : domain.Allowed;
        for (int i = 0; i < 12; i++)
        {
            if (clock.Elapsed.TotalSeconds > .06) break;
            var area = areas[Math.Min(areas.Count - 1, (int)random(0, areas.Count))];
            var destination = output.Normalize(new(random(area.X, area.Right), random(area.Y, area.Bottom)));
            if (!domain.Contains(destination) || !output.Allows(destination)) continue;
            var outward = CurvedMovement.Plan(start, destination, domain, random); var inward = CurvedMovement.Plan(destination, back, domain, random);
            if (outward is null || inward is null) continue;
            MovementPhase = "Large outward"; Changed?.Invoke();
            Schedule(0, () => MovePath(outward.Points, 0, domain, start, .018 / Settings.LargeMovementSpeed, Settings.LargeClickWhileMoving, null, () =>
            {
                MovementPhase = "Large return"; Changed?.Invoke();
                MovePath(inward.Points, 0, domain, destination, .018 / Settings.LargeMovementSpeed, Settings.LargeClickWhileMoving, null, AfterMovement);
            })); return;
        }
        SkipMovement("Large movement skipped: no connected, certifiable curved route after bounded planning attempts.", AfterMovement);
    }
    private void MovePath(IReadOnlyList<PointD> path, int index, MovementDomain? domain, PointD expected, double stepInterval, bool permitClicks, double? frameDue, Action completion)
    {
        var due = frameDue ?? scheduler.Now;
        if (index >= path.Count && scheduler.Now >= due) { completion(); return; }
        if (output.CurrentPosition() is not { } current) { SkipMovement("Movement skipped: the current pointer position is unavailable.", AfterMovement); return; }
        // Windows positions are integral physical pixels. Recheck the actual rounded segment.
        var nextPoint = index < path.Count ? output.Normalize(path[index]) : expected;
        if (domain is not null && !domain.PermitsSegment(current, nextPoint)) { SkipMovement("Large movement skipped: pointer moved across a region boundary. Saved targets are unchanged.", AfterMovement); return; }
        bool clicksAllowed = permitClicks && Settings.RegularClickingEnabled;
        if (clicksAllowed && nextRegularClickAt <= scheduler.Now && PointD.Distance(current, expected) > .01)
        {
            if (domain is not null && !domain.PermitsSegment(current, expected)) { SkipMovement("Large movement skipped: the actual pointer cannot safely rejoin its path.", AfterMovement); return; }
            try { output.Move(expected); } catch (Exception e) { Pause(e.Message); return; }
            Schedule(.004, () => MovePath(path, index, domain, expected, stepInterval, permitClicks, due, completion)); return;
        }
        if (clicksAllowed && nextRegularClickAt <= scheduler.Now && PointD.Distance(current, expected) <= .01 && output.Allows(current) && (domain is null || domain.Contains(current)))
        {
            var heldSince = scheduler.Now;
            RegularClick(current, () => MovePath(path, index, domain, expected, stepInterval, permitClicks, due + scheduler.Now - heldSince, completion)); return;
        }
        if (scheduler.Now < due)
        {
            var wake = clicksAllowed && nextRegularClickAt > scheduler.Now ? Math.Min(due, nextRegularClickAt.Value) : due;
            Schedule(wake - scheduler.Now, () => MovePath(path, index, domain, expected, stepInterval, permitClicks, due, completion)); return;
        }
        if (index >= path.Count) { completion(); return; }
        if (!output.Allows(nextPoint)) { Pause("Movement path crossed an unavailable or protected area. No click was sent."); return; }
        try { output.Move(nextPoint); } catch (Exception e) { Pause(e.Message); return; }
        var nextFrame = scheduler.Now + stepInterval;
        var nextWake = clicksAllowed && nextRegularClickAt > scheduler.Now ? Math.Min(nextFrame, nextRegularClickAt.Value) : nextFrame;
        Schedule(nextWake - scheduler.Now, () => MovePath(path, index + 1, domain, nextPoint, stepInterval, permitClicks, nextFrame, completion));
    }
    private void AfterMovement()
    {
        MovementPhase = null; Changed?.Invoke();
        if (!Settings.RegularClickingEnabled) { NextCycle(cycleBegan); return; }
        if (clickPoint is not { } point) { Stop("No regular target is saved."); return; }
        try { output.Move(point); } catch (Exception e) { Pause(e.Message); return; }
        var wait = UsesTravelCadence ? Math.Max(0, (nextRegularClickAt ?? scheduler.Now + .015) - scheduler.Now) : .015;
        if (Settings.SecondaryClickingEnabled)
            WaitWithSecondary(scheduler.Now + wait, () => RegularClick(point, () => { clickPoint = null; NextCycle(clickBegan); }));
        else Schedule(wait, () => RegularClick(point, () => { clickPoint = null; NextCycle(clickBegan); }));
    }
    private double ClickInterval() => Settings.RandomIntervalsEnabled ? random(Settings.MinInterval, Settings.MaxInterval) : Settings.BasicInterval;
    private void RegularClick(PointD point, Action completion)
    {
        if (deadline is { } end && scheduler.Now >= end) { Expire(); return; }
        try
        {
            clickBegan = scheduler.Now; output.Down(Settings.Button, point);
            if (UsesTravelCadence) nextRegularClickAt = clickBegan + ClickInterval();
            Schedule(.028, () =>
            {
                try { output.Up(Settings.Button, point); } catch (Exception e) { Pause(e.Message); return; }
                ClicksPosted++; Changed?.Invoke(); if (Active) completion();
            });
        }
        catch (Exception e) { Pause(e.Message); }
    }
    private void NextCycle(double began)
    {
        var wait = UsesTravelCadence && nextRegularClickAt is { } next ? next - scheduler.Now : ClickInterval() - (scheduler.Now - began);
        WaitForCycle(scheduler.Now + Math.Max(.02, wait));
    }
    private void Expire()
    {
        CancelPending(); output.Release(); deadline = null; frozenRemaining = 0; BreakEndsAt = null; clickPoint = null;
        if (!Settings.FinalClickEffective) { Complete("Timer complete. Session stopped."); return; }
        if (finalAttempted || FinalTarget is not { } final || !output.Available || !output.Allows(final)) { Stop("Final target or permission is unavailable. Final click skipped."); return; }
        finalAttempted = true; Status = ClickStatus.Finishing; Detail = "Sending one exact final click."; Changed?.Invoke();
        try { output.Move(final); } catch (Exception e) { Stop("Final click failed and will not be retried: " + e.Message); return; }
        Schedule(.015, () =>
        {
            try
            {
                output.Down(Settings.FinalButton, final);
                Schedule(.028, () =>
                {
                    awaitingFinalDelivery = true;
                    try { output.Up(Settings.FinalButton, final); } catch { Stop("Final click delivery is uncertain; it will not be retried."); return; }
                    if (Status == ClickStatus.Finishing) Schedule(.8, () => Stop("Final click was sent once, but delivery is unconfirmed. It will not be retried."));
                });
            }
            catch (Exception e) { Stop("Final click failed and will not be retried: " + e.Message); }
        });
    }
    private void Complete(string message) { Stop(message); frozenRemaining = 0; Changed?.Invoke(); }
}
