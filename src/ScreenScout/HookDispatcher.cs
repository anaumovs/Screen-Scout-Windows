namespace ScreenScout;

/// Dedicated message pump for input observation only. Mouse output stays on the engine dispatcher.
internal sealed class HookDispatcher : IDisposable
{
    private readonly Thread thread;
    private readonly RawMouseWindow control;
    public long? PhysicalMovementVersion => control.MovementVersion;
    public void IgnoreOwnInput(Func<uint, bool> predicate) => Invoke(() => control.IsOwnInput = predicate);
    private bool disposed;
    public int ThreadId { get; private set; }

    public HookDispatcher()
    {
        using var ready = new ManualResetEventSlim();
        RawMouseWindow? created = null; Exception? failure = null;
        thread = new Thread(() =>
        {
            RawMouseWindow? window = null;
            try
            {
                ThreadId = Environment.CurrentManagedThreadId;
                window = new RawMouseWindow();
                _ = window.Handle; created = window;
            }
            catch (Exception e) { failure = e; window?.Dispose(); }
            finally { ready.Set(); }
            if (created is not null) { using (created) Application.Run(); }
        })
        { IsBackground = true, Name = "Screen Scout input monitor" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Wait();
        control = created ?? throw new InvalidOperationException("Could not start the input monitor thread.", failure);
    }

    public void Invoke(Action action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId == ThreadId) action();
        else control.Invoke(action);
    }

    public void Dispose()
    {
        if (disposed) return;
        Invoke(Application.ExitThread); disposed = true;
        if (Environment.CurrentManagedThreadId != ThreadId) thread.Join();
    }
}
