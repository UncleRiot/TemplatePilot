using System.Runtime.InteropServices;
using System.Text;

namespace TemplatePilot.Interop;

public static class User32ClipboardInterop
{
    public const int WmClipboardUpdate = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetOpenClipboardWindow();
}

public static class User32DesktopInterop
{
    private const int UoiName = 2;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    public static string? GetInputDesktopName()
    {
        var handle = OpenInputDesktop(0, false, DesktopReadObjects | DesktopSwitchDesktop);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(256);
            return GetUserObjectInformation(handle, UoiName, buffer, buffer.Capacity, out _) ? buffer.ToString() : null;
        }
        finally
        {
            CloseDesktop(handle);
        }
    }
}

public static class User32HotkeyInterop
{
    public const int WmHotkey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public static class User32KeyboardInterop
{
    public const uint InputKeyboard = 1;
    public const uint KeyEventFKeyUp = 0x0002;

    public const ushort VkControl = 0x11;
    public const ushort VkShift = 0x10;
    public const ushort VkMenu = 0x12;
    public const ushort VkLeft = 0x25;
    public const ushort VkRight = 0x27;
    public const ushort VkC = 0x43;
    public const ushort VkV = 0x56;
    public const ushort VkLWin = 0x5B;
    public const ushort VkRWin = 0x5C;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, Win32Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    public static Win32Input KeyDown(ushort virtualKey)
    {
        return new Win32Input
        {
            Type = InputKeyboard,
            Union = new Win32InputUnion
            {
                KeyboardInput = new Win32KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static Win32Input KeyUp(ushort virtualKey)
    {
        return new Win32Input
        {
            Type = InputKeyboard,
            Union = new Win32InputUnion
            {
                KeyboardInput = new Win32KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = KeyEventFKeyUp,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Win32Input
{
    public uint Type;
    public Win32InputUnion Union;
}

[StructLayout(LayoutKind.Explicit)]
public struct Win32InputUnion
{
    [FieldOffset(0)]
    public Win32MouseInput MouseInput;

    [FieldOffset(0)]
    public Win32KeyboardInput KeyboardInput;

    [FieldOffset(0)]
    public Win32HardwareInput HardwareInput;
}

[StructLayout(LayoutKind.Sequential)]
public struct Win32MouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct Win32KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct Win32HardwareInput
{
    public uint Message;
    public ushort ParamL;
    public ushort ParamH;
}

public static class User32WindowInterop
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}

[StructLayout(LayoutKind.Sequential)]
public struct Win32Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct Win32Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
public struct GuiThreadInfo
{
    public int Size;
    public int Flags;
    public IntPtr Active;
    public IntPtr Focus;
    public IntPtr Capture;
    public IntPtr MenuOwner;
    public IntPtr MoveSize;
    public IntPtr Caret;
    public Win32Rect CaretRect;
}
