using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SevenKeyboard;

namespace CaretDpiSmoke;

internal static unsafe class Program
{
    private const int EdgeTolerance = 1; // USER32 may quantize virtualized edges by one physical pixel.
    [ThreadStatic] private static bool _activationOrShow;
    private sealed record Monitor(nint Handle, FixtureNative.Rect Bounds, int Scale, bool Primary);
    private sealed record Mode(string Name, nint Context);
    private sealed class Results
    {
        internal int Checks, Fixtures, Queries, ScaledFixtures;
        internal readonly List<string> Lines = new();
        internal void Check(bool condition, string reason)
        {
            Checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--require-mixed"))
        {
            Console.Error.WriteLine("Usage: CaretExGetPos.DpiSmoke [--require-mixed]");
            return 2;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            Console.Error.WriteLine("UNSUPPORTED: Windows 10 1607 or newer is required for per-thread DPI contexts.");
            return 2;
        }
        try
        {
            // Borrow only this process's desktop. No foreground/input-desktop query or switch.
            nint desktop = FixtureNative.GetThreadDesktop(FixtureNative.GetCurrentThreadId());
            if (desktop == 0) throw LastError("GetThreadDesktop");
            Monitor[] monitors = OnThread(desktop, "DpiSmoke monitor enumeration", EnumerateMonitors);
            if (monitors.Length == 0) throw new InvalidOperationException("No physical monitor was available for HWND fixtures.");
            var modes = new List<Mode>
            {
                new("unaware", FixtureNative.Unaware), new("system", FixtureNative.SystemAware),
                new("per-monitor", FixtureNative.PerMonitor)
            };
            if (FixtureNative.IsValidDpiAwarenessContext(FixtureNative.PerMonitorV2) != 0)
                modes.Add(new("per-monitor-v2", FixtureNative.PerMonitorV2));
            else Console.WriteLine("SKIP: per-monitor-v2 is unavailable; per-monitor-v1 remains covered.");

            int checks = 0, fixtures = 0, queries = 0, scaled = 0;
            foreach (Mode mode in modes)
            {
                Results result = OnThread(desktop, $"DpiSmoke {mode.Name}", () => RunMode(mode, modes, monitors));
                foreach (string line in result.Lines) Console.WriteLine(line);
                checks += result.Checks; fixtures += result.Fixtures; queries += result.Queries; scaled += result.ScaledFixtures;
            }
            int scales = monitors.Select(m => m.Scale).Distinct().Count();
            Console.WriteLine($"PASS: {checks} assertions, {queries} GuiToPhysical calls, {fixtures} hidden HWND fixtures, {monitors.Length} monitors ({RuntimeInformation.ProcessArchitecture}).");
            Console.WriteLine($"Coverage: {scales} distinct monitor scale(s); {scaled} fixtures with observed logical/physical size differences.");
            Console.WriteLine("Safety: own invisible disabled HWNDs only; no foreground query, activation, input, clipboard, JAB, UIA, screenshots, or display-setting changes.");
            if (scales < 2)
            {
                Console.WriteLine("SKIP: mixed-DPI topology was unavailable; this run does not validate mixed-scale monitor placement.");
                if (args.Length != 0) return 2;
            }
            if (scaled == 0)
            {
                Console.WriteLine("SKIP: no bitmap-scaled fixture was observed; nonidentity DPI mapping remains unverified.");
                if (args.Length != 0) return 2;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static T OnThread<T>(nint desktop, string name, Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (FixtureNative.GetThreadDesktop(FixtureNative.GetCurrentThreadId()) != desktop &&
                    FixtureNative.SetThreadDesktop(desktop) == 0) throw LastError("SetThreadDesktop");
                _activationOrShow = false;
                if (FixtureNative.GetActiveWindow() != 0 || FixtureNative.GetFocus() != 0)
                    throw new InvalidOperationException("A fresh fixture thread unexpectedly owns active/focused UI.");
                completion.TrySetResult(action());
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = name };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException($"{name} exceeded 30 seconds; the harness will exit without further tests.");
        return completion.Task.GetAwaiter().GetResult();
    }

    private sealed class Enumeration
    {
        internal readonly List<Monitor> Monitors = new();
        internal Exception? Error;
    }
    private static Monitor[] EnumerateMonitors()
    {
        using var scope = new DpiScope(FixtureNative.PerMonitor);
        var state = new Enumeration();
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            int ok = FixtureNative.EnumDisplayMonitors(0, 0, &MonitorCallback, GCHandle.ToIntPtr(handle));
            if (state.Error is not null) throw state.Error;
            if (ok == 0) throw LastError("EnumDisplayMonitors");
            return state.Monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToArray();
        }
        finally { handle.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MonitorCallback(nint monitor, nint dc, FixtureNative.Rect* rect, nint data)
    {
        var state = (Enumeration)GCHandle.FromIntPtr(data).Target!;
        try
        {
            FixtureNative.MonitorInfo info = new() { Size = (uint)sizeof(FixtureNative.MonitorInfo) };
            if (FixtureNative.GetMonitorInfo(monitor, ref info) == 0) throw LastError("GetMonitorInfo");
            int hr = FixtureNative.GetScaleFactorForMonitor(monitor, out int scale);
            if (hr != 0 || scale <= 0) throw new InvalidOperationException($"Cannot read monitor scale (HRESULT 0x{hr:X8}).");
            state.Monitors.Add(new(monitor, info.Monitor, scale, (info.Flags & 1) != 0));
            return 1;
        }
        catch (Exception error) { state.Error = error; return 0; }
    }

    private static Results RunMode(Mode target, IReadOnlyList<Mode> callers, Monitor[] monitors)
    {
        var result = new Results();
        using var targetScope = new DpiScope(target.Context);
        using var cls = new WindowClass();
        for (int index = 0; index < monitors.Length; index++)
        {
            Monitor monitor = monitors[index];
            if (monitor.Bounds.Width < 160 || monitor.Bounds.Height < 160)
                throw new InvalidOperationException($"Monitor #{index + 1} is too small for meaningful fixtures.");
            int width = Math.Min(483, monitor.Bounds.Width / 2), height = Math.Min(317, monitor.Bounds.Height / 2);
            for (int placement = 0; placement < 2; placement++)
            {
                string id = $"target={target.Name} monitor=#{index + 1} scale={monitor.Scale}% placement={placement}";
                int x = placement == 0 ? monitor.Bounds.Left + 23 : monitor.Bounds.Right - width - 29;
                int y = placement == 0 ? monitor.Bounds.Top + 31 : monitor.Bounds.Bottom - height - 37;
                using Window parent = cls.Create(0, 0, 0, 32, 32);
                using (new DpiScope(FixtureNative.PerMonitor))
                {
                    FixtureNative.MonitorInfo now = new() { Size = (uint)sizeof(FixtureNative.MonitorInfo) };
                    if (FixtureNative.GetMonitorInfo(monitor.Handle, ref now) == 0) throw LastError("GetMonitorInfo");
                    result.Check(Same(now.Monitor, monitor.Bounds), id + ": monitor topology changed during the run.");
                    if (FixtureNative.SetWindowPos(parent.Handle, 0, x, y, width, height, FixtureNative.PositionFlags) == 0)
                        throw LastError("SetWindowPos");
                    result.Check(FixtureNative.MonitorFromWindow(parent.Handle, 0) == monitor.Handle, id + ": fixture was not placed on its intended monitor.");
                }
                Pump();
                CheckSafe(parent.Handle, target.Context, result, id);
                FixtureNative.Rect logical = ReadClient(parent.Handle);
                FixtureNative.Rect physical = ReadPhysical(parent.Handle);
                result.Check(logical.Left == 0 && logical.Top == 0 && logical.Width >= 12 && logical.Height >= 12,
                    id + ": invalid target-logical client geometry.");
                // This custom WS_POPUP has no frame/menu/scrollbars. Its outer rect is
                // independently measurable physical client geometry; no transform API is used.
                using (new DpiScope(FixtureNative.PerMonitor))
                {
                    var origin = new FixtureNative.Point(0, 0);
                    if (FixtureNative.ClientToScreen(parent.Handle, ref origin) == 0) throw LastError("ClientToScreen (fixture sanity)");
                    result.Check(origin.X == physical.Left && origin.Y == physical.Top, id + ": borderless client origin must equal the physical window origin.");
                }
                bool scaled = logical.Width != physical.Width || logical.Height != physical.Height;
                if (scaled) result.ScaledFixtures++;
                int shapeIndex = 0;
                foreach (FixtureNative.Rect shape in Shapes(logical.Width, logical.Height))
                {
                    string region = $"{id} shape={shapeIndex++} logical={shape}";
                    // The oracle is an actual invisible custom child HWND positioned in
                    // the target's logical client space. USER32 computes its physical
                    // screen rectangle independently of GuiToPhysical's conversion path.
                    using Window marker = cls.Create(parent.Handle, shape.Left, shape.Top, shape.Width, shape.Height);
                    CheckSafe(marker.Handle, target.Context, result, region);
                    FixtureNative.Rect expected = ReadPhysical(marker.Handle);
                    result.Check(expected.Width >= 0 && expected.Height > 0, region + ": invalid physical marker bounds.");
                    foreach (Mode caller in callers)
                    {
                        using var callerScope = new DpiScope(caller.Context);
                        nint before = FixtureNative.GetThreadDpiAwarenessContext();
                        CaretPosition? converted = CaretDpi.GuiToPhysical(parent.Handle,
                            new CaretNative.Rect(shape.Left, shape.Top, shape.Right, shape.Bottom));
                        string label = $"{region} caller={caller.Name}";
                        result.Check(FixtureNative.AreDpiAwarenessContextsEqual(before, FixtureNative.GetThreadDpiAwarenessContext()) != 0,
                            label + ": production conversion did not restore the caller DPI context.");
                        result.Check(converted.HasValue, label + ": GuiToPhysical returned null for an interior controlled HWND rectangle.");
                        CaretPosition actual = converted!.Value;
                        result.Check(actual.CaretType == "GUIThreadInfo", label + ": wrong provider label.");
                        AssertEdge(result, actual.X, expected.Left, "left", label);
                        AssertEdge(result, actual.Y, expected.Top, "top", label);
                        AssertEdge(result, (long)actual.X + actual.Width, expected.Right, "right", label);
                        AssertEdge(result, (long)actual.Y + actual.Height, expected.Bottom, "bottom", label);
                        result.Check(actual.Width >= 0 && actual.Height > 0, label + ": invalid converted dimensions.");
                        CheckSafe(parent.Handle, target.Context, result, label);
                        result.Queries++;
                    }
                    result.Check(Same(expected, ReadPhysical(marker.Handle)), region + ": marker geometry changed during conversion.");
                }
                result.Fixtures++;
                result.Lines.Add($"PASS {id} logicalClient={logical.Width}x{logical.Height} physicalClient={physical} targetDpi={FixtureNative.GetDpiForWindow(parent.Handle)} virtualized={scaled}");
            }
        }
        return result;
    }

    private static FixtureNative.Rect[] Shapes(int width, int height)
    {
        int narrowHeight = Math.Min(31, height / 3);
        int blockWidth = Math.Min(37, width / 3), blockHeight = Math.Min(53, height / 3);
        return
        [
            new(0, 0, width - 1, height - 1),
            new(width / 7, height / 6, width * 3 / 4, height * 4 / 5),
            new(width - blockWidth - 3, height - blockHeight - 3, width - 3, height - 3),
            new(width / 3, height / 4, width / 3 + 1, height / 4 + narrowHeight)
        ];
    }
    private static void AssertEdge(Results results, long actual, int expected, string edge, string label) =>
        results.Check(Math.Abs(actual - expected) <= EdgeTolerance,
            $"{label}: {edge} physical edge was {actual}, HWND oracle {expected} (tolerance {EdgeTolerance}px).");

    private static FixtureNative.Rect ReadClient(nint window)
    {
        if (FixtureNative.GetClientRect(window, out var rect) == 0) throw LastError("GetClientRect");
        return rect;
    }
    private static FixtureNative.Rect ReadPhysical(nint window)
    {
        using var scope = new DpiScope(FixtureNative.PerMonitor);
        if (FixtureNative.GetWindowRect(window, out var rect) == 0) throw LastError("GetWindowRect (physical oracle)");
        return rect;
    }
    private static bool Same(FixtureNative.Rect a, FixtureNative.Rect b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static void CheckSafe(nint window, nint targetContext, Results results, string label)
    {
        results.Check(FixtureNative.GetWindowThreadProcessId(window, out uint process) == FixtureNative.GetCurrentThreadId() && process == Environment.ProcessId,
            label + ": window is not owned by this fixture thread/process.");
        uint style = unchecked((uint)FixtureNative.GetWindowLong(window, -16));
        uint extended = unchecked((uint)FixtureNative.GetWindowLong(window, -20));
        results.Check(FixtureNative.IsWindowVisible(window) == 0 && (style & FixtureNative.Visible) == 0 &&
            (style & FixtureNative.Disabled) != 0 && (extended & FixtureNative.NoActivate) != 0,
            label + ": fixture lost its hidden/disabled/no-activation invariant.");
        results.Check(!_activationOrShow && FixtureNative.GetActiveWindow() == 0 && FixtureNative.GetFocus() == 0,
            label + ": a fixture received activation, focus, or show notification.");
        results.Check(FixtureNative.AreDpiAwarenessContextsEqual(FixtureNative.GetWindowDpiAwarenessContext(window), targetContext) != 0,
            label + ": HWND did not retain its intended target DPI context.");
    }
    private static void Pump()
    {
        for (int count = 0; count < 256 && FixtureNative.PeekMessage(out var message, 0, 0, 0, 1) != 0; count++)
        {
            if (message.Id == 0x0012) throw new InvalidOperationException("Unexpected WM_QUIT on a fixture thread.");
            _ = FixtureNative.TranslateMessage(in message);
            _ = FixtureNative.DispatchMessage(in message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0021) return 4; // MA_NOACTIVATEANDEAT; this is not an interactive window.
        if (message == 0x0007 || (message == 0x0006 && (wParam & 0xFFFF) != 0) || (message == 0x0018 && wParam != 0))
            _activationOrShow = true;
        return FixtureNative.DefWindowProc(window, message, wParam, lParam);
    }

    private readonly struct DpiScope : IDisposable
    {
        private readonly nint _previous;
        internal DpiScope(nint context)
        {
            _previous = FixtureNative.SetThreadDpiAwarenessContext(context);
            if (_previous == 0) throw LastError("SetThreadDpiAwarenessContext");
        }
        public void Dispose()
        {
            if (_previous != 0 && FixtureNative.SetThreadDpiAwarenessContext(_previous) == 0)
                throw LastError("Restore fixture DPI context");
        }
    }
    private sealed class WindowClass : IDisposable
    {
        private readonly nint _instance;
        private readonly string _name = "SevenKeyboard.CaretExGetPos.DpiSmoke." + Guid.NewGuid().ToString("N");
        internal WindowClass()
        {
            _instance = FixtureNative.GetModuleHandle(null);
            if (_instance == 0) throw LastError("GetModuleHandle");
            fixed (char* name = _name)
            {
                FixtureNative.WindowClass definition = new()
                {
                    Size = (uint)sizeof(FixtureNative.WindowClass), Instance = _instance, ClassName = (nint)name,
                    Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure
                };
                if (FixtureNative.RegisterClassEx(in definition) == 0) throw LastError("RegisterClassEx");
            }
        }
        internal Window Create(nint parent, int x, int y, int width, int height)
        {
            uint style = (parent == 0 ? FixtureNative.Popup : FixtureNative.Child) | FixtureNative.Disabled;
            nint handle = FixtureNative.CreateWindowEx(FixtureNative.NoActivate | FixtureNative.ToolWindow,
                _name, "", style, x, y, width, height, parent, 0, _instance, 0);
            if (handle == 0) throw LastError("CreateWindowEx (hidden fixture)");
            return new(handle);
        }
        public void Dispose()
        {
            if (FixtureNative.UnregisterClass(_name, _instance) == 0) throw LastError("UnregisterClass");
        }
    }
    private sealed class Window(nint handle) : IDisposable
    {
        internal nint Handle { get; private set; } = handle;
        public void Dispose()
        {
            if (Handle == 0) return;
            if (FixtureNative.DestroyWindow(Handle) == 0) throw LastError("DestroyWindow (fixture cleanup)");
            Handle = 0;
        }
    }
    private static Win32Exception LastError(string operation) => new(Marshal.GetLastPInvokeError(), operation + " failed.");
}
