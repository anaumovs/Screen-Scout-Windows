using System.Diagnostics;
using System.Runtime.InteropServices;
using ScoutCore;

namespace ScreenScout;

internal sealed class WindowsMouse : IMouseOutput
{
    public bool ExclusiveOwner { get; set; }
    public bool MonitoringAvailable { get; set; }
    public bool SessionAvailable { get; set; } = true;
    public uint? VerificationProcess { get; set; }
    public Action? OnDelivery { get; set; }
    public Func<long?>? ReadPhysicalMovement { get; set; }
    public long? PhysicalMovementVersion => ReadPhysicalMovement?.Invoke();
    private (ClickButton Button, PointD Point)? held;
    private ulong serial;
    private IReadOnlyList<MovementDisplay>? cachedDisplays;
    public void InvalidateDisplays() => cachedDisplays = null;
    private readonly uint tagPrefix = (uint)Random.Shared.Next(1, 0x7FFF) << 16;
    private readonly Dictionary<nuint, double> awaiting = [];
    public int ConfirmedPairs { get; private set; }
    public int PendingAcknowledgements => awaiting.Count;
    public string LastSentTag { get; private set; } = "";
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public bool Available => ExclusiveOwner && MonitoringAvailable && SessionAvailable && Native.DefaultDesktop && (VerificationProcess is not { } pid || Native.ProcessAt(Native.GetForegroundWindow()) == pid);
    public string FailureReason => !ExclusiveOwner ? "Another Screen Scout instance owns the clicker. Close it first." :
        !SessionAvailable || !Native.DefaultDesktop ? "Windows is locked or showing a secure desktop. Resume explicitly after returning to your desktop." :
        VerificationProcess is { } pid && Native.ProcessAt(Native.GetForegroundWindow()) != pid ? "External verification stopped because the test pad lost focus." :
        "The global shortcut or event monitor is unavailable. Close conflicting shortcut software or choose another emergency key, then restart explicitly.";
    public PointD? CurrentPosition() => Native.GetCursorPos(out var p) ? new(p.X, p.Y) : null;
    public PointD Normalize(PointD p) => new(Math.Round(p.X, MidpointRounding.AwayFromZero), Math.Round(p.Y, MidpointRounding.AwayFromZero));
    public static RectD Bounds(Screen s) => new(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
    public static string Identity(Screen screen)
    {
        var device = new Native.DisplayDevice { Size = Marshal.SizeOf<Native.DisplayDevice>() };
        // Monitor device-interface identity persists across display rearrangements.
        if (Native.EnumDisplayDevices(screen.DeviceName, 0, ref device, 1) && !string.IsNullOrEmpty(device.DeviceId)) return device.DeviceId;
        return screen.DeviceName;
    }
    public MovementDisplay? MovementDisplay(string id)
    {
        var screen = string.IsNullOrEmpty(id) ? Screen.PrimaryScreen : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == id);
        return screen is null ? null : new(Identity(screen), Bounds(screen));
    }
    public IReadOnlyList<MovementDisplay> Displays() => cachedDisplays ??= Screen.AllScreens.Select(s => new MovementDisplay(Identity(s), Bounds(s))).ToArray();
    public RectD? DisplayBounds(PointD p)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = Bounds(screen);
            if (bounds.ContainsPixel(p)) return bounds;
        }
        return null;
    }
    public bool Allows(PointD point)
    {
        if (!point.IsFinite || DisplayBounds(point) is null) return false;
        var pid = Native.ProcessAt(Native.WindowFromPoint(new Native.Point { X = (int)point.X, Y = (int)point.Y }));
        return VerificationProcess is { } testPid ? pid == testPid : pid != Environment.ProcessId;
    }
    private void Check(PointD point)
    {
        if (!Available) throw new InvalidOperationException(FailureReason);
        if (!Allows(point)) throw new InvalidOperationException("The target is off-display or over Screen Scout. Reposition and restart.");
    }
    public void Move(PointD point) { Check(point); Send(point, 0, false); }
    internal void DisplaceCursorForVerification(PointD point)
    {
        if (VerificationProcess is null) throw new InvalidOperationException("Cursor fault injection requires the test pad.");
        Check(point); point = Normalize(point);
        if (!Native.SetCursorPos((int)point.X, (int)point.Y)) throw new InvalidOperationException("Cursor fault injection failed.");
    }
    public void Down(ClickButton button, PointD point)
    {
        Check(point);
        Send(point, button == ClickButton.Left ? 0x0002u : 0x0008u, false);
        held = (button, point);
    }
    public void Up(ClickButton button, PointD point)
    {
        if (!Available) { Release(); throw new InvalidOperationException(FailureReason); }
        // Release at the same coordinate even if a protected window appeared after down.
        Send(point, button == ClickButton.Left ? 0x0004u : 0x0010u, true); held = null;
    }
    public void Release()
    {
        awaiting.Clear();
        if (held is { } h)
        {
            try { Send(h.Point, h.Button == ClickButton.Left ? 0x0004u : 0x0010u, false, false); }
            catch (InvalidOperationException) { /* Always attempt button-up; secure desktops can reject it. */ }
        }
        held = null;
    }
    private void Send(PointD p, uint flags, bool acknowledge, bool move = true)
    {
        p = Normalize(p);
        var desktop = SystemInformation.VirtualScreen;
        if (desktop.Width <= 0 || desktop.Height <= 0) throw new InvalidOperationException("No active Windows display.");
        // Some Windows input paths retain only 32 bits of dwExtraInfo. Keep the
        // per-instance marker and sequence in that range so our hook can match it.
        var tag = (nuint)(tagPrefix | (uint)(++serial & 0xFFFF));
        LastSentTag = tag.ToString("X");
        // Target the centre of the pixel in the normalized virtual desktop, including negative origins.
        var input = new Native.Input
        {
            Type = 0,
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput
                {
                    Dx = (int)Math.Clamp(Math.Floor((p.X - desktop.Left + .5) * 65536.0 / desktop.Width), 0, 65535),
                    Dy = (int)Math.Clamp(Math.Floor((p.Y - desktop.Top + .5) * 65536.0 / desktop.Height), 0, 65535),
                    Flags = (move ? 0x0001u | 0x8000u | 0x4000u | 0x2000u : 0u) | flags,
                    ExtraInfo = tag
                }
            }
        };
        if (acknowledge) awaiting[tag] = Now;
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
        {
            awaiting.Remove(tag);
            throw new InvalidOperationException("Windows rejected mouse input. Check that the target app runs at the same privilege level and the desktop is unlocked. No automatic retry was made.");
        }
    }
    public bool OwnsTag(nuint tag) => ((uint)tag & 0xFFFF0000u) == tagPrefix;
    internal void SetSequenceForVerification(ulong value)
    {
        if (VerificationProcess is null) throw new InvalidOperationException("Sequence test requires the test pad.");
        serial = value;
    }
    public void Acknowledge(nuint tag, int message)
    {
        if (message is 0x0202 or 0x0205 && awaiting.Remove(tag)) { ConfirmedPairs++; OnDelivery?.Invoke(); }
    }
    // Allow short UI stalls before reconnecting the monitor. Never resend an uncertain pair.
    public bool DeliveryTimedOut => awaiting.Values.Any(t => Now - t > 2);
}
