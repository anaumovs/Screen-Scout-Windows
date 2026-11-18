namespace ScoutCore;

/// Recovers ordinary input monitoring without replaying an uncertain click.
/// Called on the engine dispatcher; retries are polled, never queued independently.
public sealed class InputRecovery(ClickEngine engine, IActionScheduler clock, Func<bool> canRecover, Func<bool> reconnect)
{
    public const int MaximumAttempts = 3;
    public int Attempts { get; private set; }
    public int TotalAttempts { get; private set; }
    public bool Pending => retryAt is not null;
    private double? retryAt, healthySince;
    private ulong pausedVersion;

    public void Cancel()
    {
        retryAt = healthySince = null;
        Attempts = 0;
    }

    public void ConfirmDelivery()
    {
        if (engine.Active && !Pending) healthySince ??= clock.Now;
    }

    public void Check(bool deliveryTimedOut)
    {
        if (Pending && (engine.Status != ClickStatus.Paused || engine.SessionVersion != pausedVersion)) Cancel();
        if (engine.Active && healthySince is { } since && clock.Now - since >= 30) Attempts = 0;

        if (deliveryTimedOut && engine.Active)
        {
            healthySince = null;
            // The one-shot final action must never be replayed, even if its acknowledgement was lost.
            if (engine.Status == ClickStatus.Finishing || engine.RemainingDuration == 0)
            {
                Cancel(); engine.Stop("Final click confirmation was interrupted; it will not be retried."); return;
            }
            QueueRetry();
        }

        if (retryAt is not { } due) return;
        if (!canRecover())
        {
            Cancel(); engine.Pause("Input recovery canceled because the desktop or session changed. Resume explicitly when ready."); return;
        }
        if (clock.Now < due) return;
        retryAt = null; Attempts++; TotalAttempts++;
        if (!reconnect()) { QueueRetry(); return; }
        // No position argument: keep both saved targets and the frozen remaining timer.
        engine.Start(engine.Settings);
    }

    private void QueueRetry()
    {
        if (Attempts >= MaximumAttempts)
        {
            retryAt = null;
            engine.Pause("Input recovery failed after 3 attempts. Target and remaining timer are saved. Choose Retry same position to try again.");
            return;
        }
        var delay = 1 << Attempts;
        engine.Pause($"Windows did not confirm mouse input. Retrying at the saved position in {delay}s (attempt {Attempts + 1}/3). Timer paused.");
        pausedVersion = engine.SessionVersion;
        retryAt = clock.Now + delay;
    }

    public bool RetrySamePosition(ClickSettings settings)
    {
        if (engine.Status != ClickStatus.Paused || engine.Target is null || engine.RemainingDuration == 0) return false;
        Cancel();
        if (!canRecover() || !reconnect())
        {
            engine.Pause("Input monitor could not reconnect. Target and remaining timer are saved; try again when the desktop is ready.");
            return false;
        }
        engine.Start(settings);
        return engine.Active;
    }
}
