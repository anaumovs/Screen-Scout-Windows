using System.Runtime.InteropServices;
using System.Text;

namespace ScreenScout;

internal static class Native
{
    internal const int WhKeyboardLl = 13, WhMouseLl = 14, WmHotkey = 0x0312;
    internal delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHook { public Point Point; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyHook { public uint VkCode, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern short VkKeyScanEx(char key, nint layout);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);

    internal static bool KeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static uint ProcessAt(nint window) { GetWindowThreadProcessId(window, out var pid); return pid; }
    internal static bool DefaultDesktop
    {
        get
        {
            var desktop = OpenInputDesktop(0, false, 1);
            if (desktop == 0) return false;
            try { var name = new StringBuilder(256); return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase); }
            finally { CloseDesktop(desktop); }
        }
    }
}
