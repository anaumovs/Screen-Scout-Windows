using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ScoutCore;

namespace ScreenScout;

internal sealed class WindowsScheduler(SynchronizationContext context) : IActionScheduler
{
    public double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public IPendingAction Schedule(double delay, Action action) => new Token(context, delay, action);
    private sealed class Token : IPendingAction
    {
        private readonly WaitHandle timer;
        private readonly RegisteredWaitHandle registration;
        private int cancelled;
        public Token(SynchronizationContext context, double delay, Action action)
        {
            var nativeTimer = CreateWaitableTimerExW(0, null, 2, 0x1F0003);
            if (nativeTimer.IsInvalid)
            {
                nativeTimer.Dispose(); nativeTimer = CreateWaitableTimerExW(0, null, 0, 0x1F0003);
            }
            if (nativeTimer.IsInvalid) { nativeTimer.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            timer = new TimerHandle(nativeTimer);
            registration = ThreadPool.RegisterWaitForSingleObject(timer, (_, _) =>
            {
                if (Volatile.Read(ref cancelled) != 0) return;
                try { context.Post(_ => { if (Volatile.Read(ref cancelled) != 0) return; Cancel(); action(); }, null); }
                catch (InvalidAsynchronousStateException) { Cancel(); }
            }, null, Timeout.Infinite, true);
            long due = -Math.Max(1, (long)Math.Ceiling(Math.Max(0, delay) * TimeSpan.TicksPerSecond));
            if (!SetWaitableTimer(nativeTimer, ref due, 0, 0, 0, false))
            { int error = Marshal.GetLastWin32Error(); Cancel(); throw new Win32Exception(error); }
        }
        public void Cancel()
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
            CancelWaitableTimer(timer.SafeWaitHandle); registration.Unregister(null); timer.Dispose();
        }
        private sealed class TimerHandle : WaitHandle { public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint completion, nint state, [MarshalAs(UnmanagedType.Bool)] bool resume);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    }
}
