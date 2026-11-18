using System.Runtime.InteropServices;

namespace ScreenScout;

/// Device motion observation, separate from injected cursor output. No key or position history.
internal sealed class RawMouseWindow : Control
{
    private long movementVersion;
    private readonly DeviceMotionFilter motion;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<uint, bool>? IsOwnInput { get; set; }
    public RawMouseWindow() => motion = new(tag => IsOwnInput?.Invoke(tag) == true);
    private volatile bool available;
    public long? MovementVersion => available ? Interlocked.Read(ref movementVersion) : null;
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        available = RegisterRawInputDevices([new() { UsagePage = 1, Usage = 2, Flags = 0x2100, Target = Handle }], 1, (uint)Marshal.SizeOf<Device>());
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        available = false;
        RegisterRawInputDevices([new() { UsagePage = 1, Usage = 2, Flags = 1 }], 1, (uint)Marshal.SizeOf<Device>());
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0xFF)
        {
            uint bytes = (uint)Marshal.SizeOf<Packet>();
            if (GetRawInputData(m.LParam, 0x10000003, out var packet, ref bytes, (uint)Marshal.SizeOf<Header>()) == bytes && packet.Header.Type == 0)
            {
                if (motion.Observe(packet.Header.Device, packet.Mouse.Flags, packet.Mouse.X, packet.Mouse.Y, packet.Mouse.ExtraInfo)) Interlocked.Increment(ref movementVersion);
            }
        }
        else if (m.Msg == 0xFE && m.WParam == 2) motion.Remove(m.LParam);
        base.WndProc(ref m); // DefWindowProc performs WM_INPUT cleanup.
    }
    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Mouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
        [FieldOffset(20)] public uint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Packet { public Header Header; public Mouse Mouse; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint input, uint command, out Packet data, ref uint size, uint headerSize);
}
