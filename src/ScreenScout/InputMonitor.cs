using System.Runtime.InteropServices;
using ScoutCore;

namespace ScreenScout;

internal sealed class InputMonitor : IDisposable
{
    private readonly HookDispatcher dispatcher = new();
    private bool disposed;
    public int HookThreadId => dispatcher.ThreadId;
    public long? PhysicalMovementVersion => dispatcher.PhysicalMovementVersion;
    public void IgnoreOwnInput(Func<uint, bool> predicate) => dispatcher.IgnoreOwnInput(predicate);
    public int LastInstallError { get; private set; }
    private readonly Native.HookProc keyCallback, mouseCallback;
    private nint keyHook, mouseHook, layout;
    private ChordLatch toggle = new([0x46, 0x47, 0x48]), final = new([0x49, 0x4F, 0x50]);
    private readonly HashSet<int> pressed = [];
    public bool LayoutMapped { get; private set; }
    public bool Listening => keyHook != 0 && mouseHook != 0 && LayoutMapped;
    private volatile int emergencyKey = 0x77;
    private volatile bool emergencyControlAlt;
    public int EmergencyKey { get => emergencyKey; set => emergencyKey = value; }
    public bool EmergencyControlAlt { get => emergencyControlAlt; set => emergencyControlAlt = value; }
    public Action? OnToggle, OnFinal, OnEmergency;
    public Action<string>? OnFailure;
    public Action<nuint, int>? OnMouse;
    public int MouseEvents { get; private set; }
    private int keyboardEvents;
    public int KeyboardEvents => Volatile.Read(ref keyboardEvents);
    public string LastMouseTag { get; private set; } = "";
    public InputMonitor() { keyCallback = HandleKey; mouseCallback = HandleMouse; }
    public void Start(bool notifyFailure = true) => dispatcher.Invoke(() =>
    {
        Uninstall(); UpdateLayout(false);
        keyHook = Native.SetWindowsHookEx(Native.WhKeyboardLl, keyCallback, Native.GetModuleHandle(null), 0);
        int keyError = keyHook == 0 ? Marshal.GetLastWin32Error() : 0;
        mouseHook = Native.SetWindowsHookEx(Native.WhMouseLl, mouseCallback, Native.GetModuleHandle(null), 0);
        LastInstallError = mouseHook == 0 ? Marshal.GetLastWin32Error() : keyError;
        RequireRelease();
        if (keyHook == 0 || mouseHook == 0)
        {
            Uninstall();
            if (notifyFailure) OnFailure?.Invoke("Windows could not install the global input monitor. Restart Screen Scout.");
        }
    });
    internal void RemoveMouseHookForVerification() => dispatcher.Invoke(() =>
    {
        // Model Windows silently removing a hook while its old handle still looks valid.
        if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
    });
    public void UpdateLayout(bool notify = true) => dispatcher.Invoke(() =>
    {
        var thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var current = Native.GetKeyboardLayout(thread);
        if (current == layout) return;
        layout = current;
        int[] Map(string letters) => letters.Select(c => (int)Native.VkKeyScanEx(c, layout)).ToArray();
        var fg = Map("fgh"); var io = Map("iop");
        LayoutMapped = fg.Concat(io).All(c => c >= 0 && (c >> 8) == 0) && fg.Distinct().Count() == 3 && io.Distinct().Count() == 3;
        if (LayoutMapped) { toggle = new(fg); final = new(io); }
        RequireRelease();
        if (notify) OnFailure?.Invoke(LayoutMapped ? "Keyboard layout changed. Release F/G/H and I/O/P, then resume explicitly." : "F/G/H or I/O/P is unavailable on this keyboard layout. Choose a layout containing these letters.");
    });
    public void RequireRelease() => dispatcher.Invoke(() =>
    {
        toggle.RequireRelease(toggle.Keys.Where(Native.KeyDown)); final.RequireRelease(final.Keys.Where(Native.KeyDown)); pressed.Clear();
    });
    private nint HandleKey(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            Interlocked.Increment(ref keyboardEvents);
            try
            {
                var key = Marshal.PtrToStructure<Native.KeyHook>(data); int vk = (int)key.VkCode;
                bool down = (int)message is 0x0100 or 0x0104, up = (int)message is 0x0101 or 0x0105;
                if (down || up)
                {
                    bool repeated = down && !pressed.Add(vk); if (up) pressed.Remove(vk);
                    bool control = Native.KeyDown(0x11), alt = Native.KeyDown(0x12), shift = Native.KeyDown(0x10), win = Native.KeyDown(0x5B) || Native.KeyDown(0x5C);
                    if (down && vk == EmergencyKey && !win && !shift && control == EmergencyControlAlt && alt == EmergencyControlAlt)
                    { toggle.RequireRelease(toggle.Pressed); final.RequireRelease(final.Pressed); OnEmergency?.Invoke(); }
                    else if (down && (control || alt || win))
                    { toggle.RequireRelease(toggle.Pressed.Append(vk)); final.RequireRelease(final.Pressed.Append(vk)); }
                    else if (LayoutMapped)
                    { if (toggle.Update(vk, down, repeated)) OnToggle?.Invoke(); if (final.Update(vk, down, repeated)) OnFinal?.Invoke(); }
                }
            }
            catch (Exception e) { OnFailure?.Invoke("Keyboard monitor interrupted: " + e.Message); }
        }
        // Chords are observers: all original key events still reach the focused app.
        return Native.CallNextHookEx(keyHook, code, message, data);
    }
    private nint HandleMouse(int code, nint message, nint data)
    {
        if (code >= 0 && (int)message is 0x0201 or 0x0202 or 0x0204 or 0x0205)
        {
            try { var m = Marshal.PtrToStructure<Native.MouseHook>(data); MouseEvents++; LastMouseTag = m.ExtraInfo.ToString("X"); OnMouse?.Invoke(m.ExtraInfo, (int)message); }
            catch (Exception e) { OnFailure?.Invoke("Mouse monitor interrupted: " + e.Message); }
        }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }
    private void Uninstall()
    {
        if (keyHook != 0) Native.UnhookWindowsHookEx(keyHook);
        if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
        keyHook = mouseHook = 0; pressed.Clear();
    }
    public void Dispose()
    {
        if (disposed) return;
        dispatcher.Invoke(Uninstall); dispatcher.Dispose(); disposed = true;
    }
}
