using System.Runtime.InteropServices;

namespace SevenKeyboard;

// Blittable ABI only: no RCWs, runtime COM marshalling, or dynamic invocation.
internal static unsafe partial class CaretNative
{
    internal const uint ObjidCaret = 0xFFFFFFF8;
    internal static readonly Guid AccessibleId = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; internal Rect(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
    }
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

    // VARIANT is 16 bytes on x86 and 24 on x64, including its BRECORD union arm.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Variant
    {
        internal ushort Type, Reserved1, Reserved2, Reserved3;
        internal VariantValue Value;
    }
    [StructLayout(LayoutKind.Explicit)]
    internal struct VariantValue
    {
        [FieldOffset(0)] internal long Integer;
        [FieldOffset(0)] internal RecordPair Record;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RecordPair { internal nint Data, Info; }

    internal static CaretPosition? TryGetMsaa(nint window)
    {
        if (window == 0) return null;
        try
        {
            // Keep object acquisition, accLocation and release in one physical
            // coordinate context; restore this worker's previous context afterward.
            using var dpi = new CaretDpi.Scope(CaretDpi.PerMonitorAware);
            nint accessible = 0;
            try
            {
                Guid id = AccessibleId;
                int hr = AccessibleObjectFromWindow(window, ObjidCaret, &id, &accessible);
                if (hr != 0 || accessible == 0) return null;
                return ReadAccessibleLocation(accessible);
            }
            finally { if (accessible != 0) Release(accessible); }
        }
        catch (Exception) { return null; }
    }

    internal static CaretPosition? ReadAccessibleLocation(nint accessible)
    {
        int x = 0, y = 0, width = 0, height = 0;
        Variant child = new() { Type = 3 }; // VT_I4, CHILDID_SELF = 0
        nint* table = *(nint**)accessible;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int*, int*, int*, int*, Variant, int>)table[22])
            (accessible, &x, &y, &width, &height, child);
        // accLocation returns physical pixels. Keep the original empirical (0,0) rejection.
        return hr == 0 && (x != 0 || y != 0) ? new(x, y, width, height, "MSAA") : null;
    }

    internal static void Release(nint value) =>
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)value)[2])(value);

    internal static CaretPosition? TryGetGuiThreadInfo()
    {
        try
        {
            GuiThreadInfo info = new() { Size = (uint)sizeof(GuiThreadInfo) };
            // Preserve the original foreground-thread semantics, not the cached HWND's thread.
            if (GetGUIThreadInfo(0, ref info) == 0 || info.Caret == 0) return null;
            return CaretDpi.GuiToPhysical(info.Caret, info.CaretRect);
        }
        catch (Exception) { return null; }
    }

    [LibraryImport("oleacc.dll")]
    private static partial int AccessibleObjectFromWindow(nint window, uint objectId, Guid* iid, nint* result);
    [LibraryImport("ole32.dll")] internal static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] internal static partial void CoUninitialize();
    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandle(string? name);
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint LoadLibraryEx(string path, nint file, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial int FreeLibrary(nint module);
    [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] internal static partial int IsWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    internal static partial int PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint GetThreadDesktop(uint thread);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int SetThreadDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll")] internal static partial int LogicalToPhysicalPointForPerMonitorDPI(nint window, ref Point point);
    [LibraryImport("user32.dll")] internal static partial int GetAwarenessFromDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll")] internal static partial nint MonitorFromPoint(Point point, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] internal static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("shcore.dll")] internal static partial int GetScaleFactorForMonitor(nint monitor, out int scale);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WindowClass windowClass);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int UnregisterClass(string name, nint instance);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    internal static partial int PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in Message message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Message message);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
}
