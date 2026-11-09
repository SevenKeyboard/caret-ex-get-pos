using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SevenKeyboard;

internal static class Program
{
    private static int _checks, _mode, _releases;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            RunPure();
            if (args.SequenceEqual(["--pump"])) await RunLifetimeAsync(loadJava: false);
            else if (args.SequenceEqual(["--jab-lifetime"])) await RunLifetimeAsync(loadJava: true);
            else if (args.SequenceEqual(["--workers"])) await ProviderThreadTests.RunAsync();
            else if (args.Length != 0) throw new ArgumentException("Usage: CaretExGetPos.NativeSmoke [--pump|--jab-lifetime|--workers]");
            Console.WriteLine($"PASS: {_checks} native/DPI checks ({RuntimeInformation.ProcessArchitecture}).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static unsafe void RunPure()
    {
        int screenCalls = 0, physicalCalls = 0;
        CaretPosition? gui = CaretDpi.MapGuiCorners(new(10, 20, 12, 30),
            (ref CaretNative.Point point) => { screenCalls++; point.X -= 300; point.Y += 40; return true; },
            (ref CaretNative.Point point) => { physicalCalls++; point.X *= 2; point.Y *= 3; return true; });
        Check(gui == new CaretPosition(-580, 180, 4, 30, "GUIThreadInfo"), "GUI conversion must transform width and height via both corners.");
        Check(screenCalls == 2 && physicalCalls == 2, "Both logical corners must pass through both transforms.");
        int calls = 0;
        gui = CaretDpi.MapGuiCorners(new(0, 0, 1, 1),
            (ref CaretNative.Point _) => true,
            (ref CaretNative.Point _) => ++calls != 2);
        Check(gui is null && calls == 2, "Second-corner conversion failure must reject the whole rectangle.");
        Check(CaretDpi.FromRectangle(.5, -.5, 1.5, 2.5, "test") == new CaretPosition(1, -1, 2, 3, "test"),
            "AHK ties must round away from zero independently.");
        Check(CaretDpi.FromRectangle(double.NaN, 0, 1, 1, "test") is null, "NaN must not become an integer caret.");
        Check(CaretDpi.FromRectangle(int.MaxValue + 1.0, 0, 1, 1, "test") is null, "Overflow must not wrap.");

        var monitor = new CaretNative.Rect(1920, -1080, 3840, 0);
        var rect = new JavaAccessBridge.TextRect(2000, -1000, 2, 10);
        Check(CaretDpi.TransformJava(rect, 1, monitor, 1.5, 2) == new CaretPosition(1440, -560, 4, 20, "JavaAccessBridge"),
            "System Java must retain monitor-local pretransform and the legacy origin/s term.");
        Check(CaretDpi.TransformJava(rect, 2, monitor, 1.5, 2) == new CaretPosition(2720, -1280, 4, 20, "JavaAccessBridge"),
            "Per-monitor Java must use the original GLOBAL pretransform.");
        Check(CaretDpi.TransformJava(rect, 0, monitor, 1.5, 2) == new CaretPosition(1387, -613, 3, 13, "JavaAccessBridge"),
            "Unaware Java must retain the original no-prescaling assumption.");
        Check(CaretDpi.TransformJava(new(-2000, 0, 2, 10), 1, new(-1920, 0, 0, 1080), 1.5, 2)
            == new CaretPosition(-1440, 0, 4, 20, "JavaAccessBridge"), "Negative monitor origins must remain signed.");
        Check(CaretDpi.TransformJava(new(1, 1, 1, 1), 1, new(0, 0, 100, 100), 1.5, 1.5)
            == new CaretPosition(2, 2, 2, 2, "JavaAccessBridge"), "Round the width, not the difference of rounded edges.");
        Check(CaretDpi.TransformJava(rect, -1, monitor, 1.5, 2) is null, "Unknown Java DPI awareness must fail.");
        Check(CaretDpi.TransformJava(rect, 1, monitor, 0, 2) is null, "Invalid scale must fail.");

        Check(sizeof(CaretNative.Variant) == (IntPtr.Size == 8 ? 24 : 16), "Native VARIANT ABI size.");
        Check(sizeof(CaretNative.GuiThreadInfo) == (IntPtr.Size == 8 ? 72 : 48), "GUITHREADINFO ABI size.");
        Check(sizeof(CaretNative.Message) == (IntPtr.Size == 8 ? 48 : 32), "MSG ABI size.");
        Check(sizeof(CaretNative.WindowClass) == (IntPtr.Size == 8 ? 80 : 48), "WNDCLASSEX ABI size.");
        Check(sizeof(JavaAccessBridge.TextRect) == 16, "JAB AccessibleTextRectInfo ABI size.");
        CaretNative.Variant value = default;
        Check((byte*)&value.Value - (byte*)&value == 8, "VARIANT union offset.");

        // Invoke the real raw MSAA vtable path against an in-process fake; no COM,
        // foreground queries, accessibility providers, HWNDs, or desktop access.
        nint* table = stackalloc nint[23];
        new Span<nint>(table, 23).Clear();
        table[22] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int*, int*, int*, CaretNative.Variant, int>)&Location;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        nint instance = (nint)table;
        _mode = 0;
        Check(CaretNative.ReadAccessibleLocation((nint)(&instance)) == new CaretPosition(-100, 200, 2, 15, "MSAA"),
            "MSAA accLocation slot and by-value CHILDID_SELF VARIANT.");
        _mode = 1;
        Check(CaretNative.ReadAccessibleLocation((nint)(&instance)) is null, "Original MSAA (0,0) rejection.");
        _mode = 2;
        Check(CaretNative.ReadAccessibleLocation((nint)(&instance)) is null, "MSAA HRESULT failure.");
        CaretNative.Release((nint)(&instance));
        Check(_releases == 1, "IUnknown Release slot.");
        bool relativeRejected = false;
        try { JavaAccessBridge.ResolvePath("relative.dll"); }
        catch (ArgumentException) { relativeRejected = true; }
        Check(relativeRejected, "An explicit relative override must not trigger DLL searching.");
        Check(JavaAccessBridge.ResolvePath(null) == Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "dll", "x64", "windowsaccessbridge-64.dll")),
            "Default JAB path must be application-relative.");
    }

    private static async Task RunLifetimeAsync(bool loadJava)
    {
        // Opt-in: hidden nonactivating native HWNDs only, never foreground/caret queries.
        // --pump uses a nonexistent exact override; --jab-lifetime also exercises the
        // signed bundled JAB's Windows_run -> FreeLibrary lifecycle without Java queries.
        string? path = loadJava ? null : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");
        string resolved = JavaAccessBridge.ResolvePath(path);
        long previousRuntime = 0;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            await using CaretNativeThread first = await CaretNativeThread.CreateAsync(path);
            Check(first.RuntimeId != previousRuntime, "Each zero-lease cycle must create a NEW runtime.");
            previousRuntime = first.RuntimeId;
            nint window = await first.RunAsync(() => first.NotificationWindow);
            uint worker = await first.RunAsync(CaretNative.GetCurrentThreadId);
            Check(window != 0 && CaretNative.IsWindow(window) != 0, "Hidden native notification window exists.");
            Check(await first.RunAsync(() => first.JavaAvailable) == loadJava,
                "Java load availability matches the selected fixture (bundled test requires x64 and its runtime dependencies).");
            if (loadJava) Check(CaretNative.GetModuleHandle(resolved) != 0, "JAB module is loaded while its runtime is leased.");

            await using CaretNativeThread second = await CaretNativeThread.CreateAsync(path);
            Check(second.RuntimeId == first.RuntimeId && await second.RunAsync(CaretNative.GetCurrentThreadId) == worker,
                "Overlapping leases share one native runtime/thread.");
            Check(await second.RunAsync(() => second.NotificationWindow) == window, "Overlapping leases share the shell HWND.");
            var order = new List<int>();
            Task<int>[] work = Enumerable.Range(0, 32).Select(i => first.RunAsync(() => { order.Add(i); return i; })).ToArray();
            await Task.WhenAll(work);
            Check(order.SequenceEqual(Enumerable.Range(0, 32)), "Worker executes admitted calls in FIFO order.");
            bool restored = await first.RunAsync(() =>
            {
                nint before = CaretNative.GetThreadDpiAwarenessContext();
                using (new CaretDpi.Scope(CaretDpi.SystemAware))
                    if (CaretNative.GetAwarenessFromDpiAwarenessContext(CaretNative.GetThreadDpiAwarenessContext()) != 1) return false;
                return CaretNative.GetThreadDpiAwarenessContext() == before;
            });
            Check(restored, "Worker DPI scope restores its previous thread context.");
            foreach (nint context in new nint[] { -1, -2, -3, -4 })
            {
                bool msaaRestored = await first.RunAsync(() =>
                {
                    using var outer = new CaretDpi.Scope(context);
                    nint before = CaretNative.GetThreadDpiAwarenessContext();
                    // Our invisible notification HWND owns no caret. Exercise the
                    // real MSAA no-result path without querying the user's foreground.
                    CaretPosition? result = CaretNative.TryGetMsaa(window);
                    return result is null && CaretNative.GetThreadDpiAwarenessContext() == before;
                });
                Check(msaaRestored, $"MSAA no-result lookup restores caller DPI context {context}.");
            }
            await first.DisposeAsync();
            Check(await second.RunAsync(() => 42) == 42, "Disposing one lease leaves another usable.");
            Check(CaretNative.IsWindow(window) != 0, "Nonfinal disposal retains the shared HWND.");
            bool rejected = false;
            try { await first.RunAsync(() => 0); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "Disposed lease rejects new submissions.");
            await first.DisposeAsync();
            await second.DisposeAsync();
            Check(CaretNative.IsWindow(window) == 0, "Final disposal destroys the notification HWND before returning.");
            if (loadJava) Check(CaretNative.GetModuleHandle(resolved) == 0, "Final disposal unloads the JAB DLL.");
        }

        // Hold a previously admitted native stage, then race last-close against new
        // creation. The replacement cannot initialize until the old stage drains.
        await using CaretNativeThread old = await CaretNativeThread.CreateAsync(path);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<int> accepted = old.RunAsync(() => { entered.Set(); release.Wait(); return 17; });
        Task closing;
        Task<CaretNativeThread> creating;
        try
        {
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), "Controlled native stage entered.");
            closing = old.DisposeAsync().AsTask();
            creating = CaretNativeThread.CreateAsync(path);
            Check(!closing.IsCompleted && !creating.IsCompleted, "Overlapping creation waits while final disposal drains accepted work.");
        }
        finally { release.Set(); }
        Check(await accepted == 17, "Final disposal preserves an already admitted stage.");
        await closing;
        await using CaretNativeThread replacement = await creating;
        Check(replacement.RuntimeId != old.RuntimeId, "Creation after close uses a new runtime.");
        Check(await replacement.RunAsync(() => replacement.JavaAvailable) == loadJava, "Replacement JAB initialization remains usable.");
        nint finalWindow = await replacement.RunAsync(() => replacement.NotificationWindow);
        await replacement.DisposeAsync();
        Check(CaretNative.IsWindow(finalWindow) == 0, "Replacement final disposal also destroys its HWND.");
        if (loadJava) Check(CaretNative.GetModuleHandle(resolved) == 0, "Replacement final disposal also unloads JAB.");

        // A stopped runtime must not poison new instances even while its failed
        // lease is still held. WM_QUIT targets only the worker created by this test.
        CaretNativeThread failed = await CaretNativeThread.CreateAsync(path);
        uint failedThread = await failed.RunAsync(CaretNative.GetCurrentThreadId);
        nint failedWindow = await failed.RunAsync(() => failed.NotificationWindow);
        Check(CaretNative.PostThreadMessage(failedThread, 0x0012, 0, 0) != 0, "Stop only the owned test worker.");
        await failed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(CaretNative.IsWindow(failedWindow) == 0, "Unexpected worker exit still destroys its HWND.");
        if (loadJava) Check(CaretNative.GetModuleHandle(resolved) == 0, "Unexpected worker exit still unloads JAB.");
        await using CaretNativeThread recovered = await CaretNativeThread.CreateAsync(path);
        Check(recovered.RuntimeId != failed.RuntimeId, "New creation recovers while the old failed lease is still alive.");
        bool oldFailed = false;
        try { await failed.DisposeAsync(); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { oldFailed = true; }
        Check(oldFailed, "The old owner observes its own worker failure.");
        await using CaretNativeThread recoveredPeer = await CaretNativeThread.CreateAsync(path);
        Check(recoveredPeer.RuntimeId == recovered.RuntimeId, "Late old disposal cannot clear the new shared runtime.");
        Check(await recovered.RunAsync(() => recovered.JavaAvailable) == loadJava, "Recovered provider availability.");
        await recovered.DisposeAsync();
        await recoveredPeer.DisposeAsync();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int Location(nint self, int* x, int* y, int* width, int* height, CaretNative.Variant child)
    {
        if (child.Type != 3 || child.Value.Integer != 0) return unchecked((int)0x80070057);
        if (_mode == 2) return unchecked((int)0x80004005);
        *x = _mode == 1 ? 0 : -100; *y = _mode == 1 ? 0 : 200; *width = 2; *height = 15;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self) { _releases++; return 0; }
}
