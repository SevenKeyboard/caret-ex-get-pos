using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

internal static class CaretDpi
{
    internal const nint SystemAware = -2;
    internal delegate bool PointTransform(ref CaretNative.Point point);

    internal readonly struct Scope : IDisposable
    {
        private readonly nint _previous;
        internal Scope(nint context)
        {
            if (context == 0) throw new ArgumentException("A valid DPI context is required.", nameof(context));
            _previous = CaretNative.SetThreadDpiAwarenessContext(context);
            if (_previous == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        public void Dispose()
        {
            if (_previous != 0 && CaretNative.SetThreadDpiAwarenessContext(_previous) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot restore the worker's DPI context.");
        }
    }

    internal static CaretPosition? GuiToPhysical(nint window, CaretNative.Rect rect)
    {
        // rcCaret is in the target window's logical CLIENT space, irrespective of caller DPI.
        using var scope = new Scope(CaretNative.GetWindowDpiAwarenessContext(window));
        return MapGuiCorners(rect,
            (ref CaretNative.Point p) => CaretNative.ClientToScreen(window, ref p) != 0,
            (ref CaretNative.Point p) => CaretNative.LogicalToPhysicalPointForPerMonitorDPI(window, ref p) != 0);
    }

    // Both corners follow the same client -> target logical screen -> physical mapping.
    // Kept separate from P/Invoke for controlled, mixed-DPI tests without querying the desktop.
    internal static CaretPosition? MapGuiCorners(CaretNative.Rect rect, PointTransform toScreen, PointTransform toPhysical)
    {
        CaretNative.Point first = new(rect.Left, rect.Top), second = new(rect.Right, rect.Bottom);
        if (!toScreen(ref first) || !toScreen(ref second) ||
            !toPhysical(ref first) || !toPhysical(ref second)) return null;
        return FromRectangle(first.X, first.Y, (double)second.X - first.X, (double)second.Y - first.Y, "GUIThreadInfo");
    }

    internal static unsafe CaretPosition? JavaToPhysical(nint window, JavaAccessBridge.TextRect raw)
    {
        int awareness = CaretNative.GetAwarenessFromDpiAwarenessContext(CaretNative.GetWindowDpiAwarenessContext(window));
        if (awareness is < 0 or > 2) return null;
        using var systemScope = new Scope(SystemAware);
        nint monitor = CaretNative.MonitorFromWindow(window, 2);
        if (monitor == 0) return null;
        CaretNative.MonitorInfo systemInfo = new() { Size = (uint)sizeof(CaretNative.MonitorInfo) };
        if (CaretNative.GetMonitorInfo(monitor, ref systemInfo) == 0) return null;
        nint primary = CaretNative.MonitorFromPoint(new(0, 0), 1);
        double systemScale = ReadScale(primary) / 100.0;
        double monitorScale = ReadScale(monitor) / 100.0;
        return TransformJava(raw, awareness, systemInfo.Monitor, systemScale, monitorScale);
    }

    private static int ReadScale(nint monitor) =>
        monitor != 0 && CaretNative.GetScaleFactorForMonitor(monitor, out int scale) == 0 && scale > 0 ? scale : 100;

    internal static CaretPosition? TransformJava(JavaAccessBridge.TextRect raw, int awareness,
        CaretNative.Rect systemMonitor, double systemScale, double monitorScale)
    {
        if (awareness is < 0 or > 2 || !double.IsFinite(systemScale) || !double.IsFinite(monitorScale) ||
            systemScale <= 0 || monitorScale <= 0) return null;
        double x = raw.X, y = raw.Y, width = raw.Width, height = raw.Height;
        // Preserve the AHK provider's empirically verified intermediate SYSTEM-space output:
        // Swing/system-aware: scale monitor-local offsets. IntelliJ/per-monitor: scale globally.
        // Unaware/GDI-scaled: retain the original untested assumption (no preliminary scaling).
        if (awareness == 1)
        {
            x = systemMonitor.Left + (x - systemMonitor.Left) * systemScale;
            y = systemMonitor.Top + (y - systemMonitor.Top) * systemScale;
            width *= systemScale;
            height *= systemScale;
        }
        else if (awareness == 2)
        {
            x *= systemScale;
            y *= systemScale;
            width *= systemScale;
            height *= systemScale;
        }
        double ratio = monitorScale / systemScale;
        // Preserve DpiAwareCoord.convertSysToMon exactly, including origin / primaryScale.
        // Substituting a physical monitor origin changes the original empirical behavior.
        double left = systemMonitor.Left / systemScale + (x - systemMonitor.Left) * ratio;
        double top = systemMonitor.Top / systemScale + (y - systemMonitor.Top) * ratio;
        double right = systemMonitor.Left / systemScale + (x + width - systemMonitor.Left) * ratio;
        double bottom = systemMonitor.Top / systemScale + (y + height - systemMonitor.Top) * ratio;
        return FromRectangle(left, top, right - left, bottom - top, "JavaAccessBridge");
    }

    internal static CaretPosition? FromRectangle(double x, double y, double width, double height, string type)
    {
        if (!TryRound(x, out int ix) || !TryRound(y, out int iy) ||
            !TryRound(width, out int iw) || !TryRound(height, out int ih)) return null;
        return new(ix, iy, iw, ih, type);
    }
    private static bool TryRound(double value, out int result)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(rounded) || rounded < int.MinValue || rounded > int.MaxValue) { result = 0; return false; }
        result = (int)rounded;
        return true;
    }
}
