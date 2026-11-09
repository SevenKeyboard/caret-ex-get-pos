using System.Runtime.InteropServices;

namespace CaretDpiSmoke;

// Fixture-only ABI. No foreground, input, capture, accessibility, COM, or JAB entry points.
internal static unsafe partial class FixtureNative
{
    internal const uint Popup = 0x80000000, Child = 0x40000000, Disabled = 0x08000000, Visible = 0x10000000;
    internal const uint NoActivate = 0x08000000, ToolWindow = 0x80;
    internal const uint PositionFlags = 0x0010 | 0x0004 | 0x0200 | 0x0008; // NOACTIVATE | NOZORDER | NOOWNERZORDER | NOREDRAW
    internal const nint Unaware = -1, SystemAware = -2, PerMonitor = -3, PerMonitorV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly int Width => checked(Right - Left);
        internal readonly int Height => checked(Bottom - Top);
        internal Rect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo { internal uint Size; internal Rect Monitor, Work; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }

    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandle(string? name);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint GetThreadDesktop(uint thread);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int SetThreadDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged[Stdcall]<nint, nint, Rect*, nint, int> callback, nint data);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    internal static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("shcore.dll")] internal static partial int GetScaleFactorForMonitor(nint monitor, out int scale);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll")] internal static partial int IsValidDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] internal static partial nint GetThreadDpiAwarenessContext();
    [LibraryImport("user32.dll")] internal static partial nint GetWindowDpiAwarenessContext(nint window);
    [LibraryImport("user32.dll")] internal static partial int AreDpiAwarenessContextsEqual(nint first, nint second);
    [LibraryImport("user32.dll")] internal static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WindowClass definition);
    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int UnregisterClass(string name, nint instance);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int GetWindowRect(nint window, out Rect rect);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int GetClientRect(nint window, out Rect rect);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll")] internal static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint window, int index);
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("user32.dll")] internal static partial nint GetActiveWindow();
    [LibraryImport("user32.dll")] internal static partial nint GetFocus();
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    internal static partial int PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in Message message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Message message);
}
