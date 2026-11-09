using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

// One shared native runtime while leases overlap. The final lease drains accepted
// work, unloads JAB on its owner, destroys the HWND, and joins before a replacement.
internal sealed class CaretNativeThread : IAsyncDisposable
{
    private static readonly object SharedGate = new();
    private static Backend? _shared;
    private readonly object _gate = new();
    private readonly Backend _backend;
    private bool _disposed, _javaAvailable;
    private Func<nint, CaretPosition?>? _javaQueryForTests;
    private Task? _disposeTask;
    private Action<nint>? _destroyed;

    private CaretNativeThread(Backend backend) => _backend = backend;

    internal static async Task<CaretNativeThread> CreateAsync(string? javaAccessBridgePath = null)
    {
        string path = JavaAccessBridge.ResolvePath(javaAccessBridgePath);
        // Capture before any await; a replacement may otherwise start from a pool
        // continuation attached to a different desktop than the original caller.
        nint desktop = CaretNative.GetThreadDesktop(CaretNative.GetCurrentThreadId());
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Backend backend;
        while (true)
        {
            Backend? closing;
            lock (SharedGate)
            {
                backend = _shared ??= new Backend(desktop);
                closing = backend.Closing || backend.IsStoppingOrStopped ? backend : null;
                if (closing is null) { backend.LeaseCount++; break; }
            }
            // No new Windows_run may overlap the old runtime's unload/thread exit.
            // The previous owner's failure belongs to its existing leases. Once
            // the old thread has ended, a new creation can retry independently.
            await closing.WaitForExitAsync().ConfigureAwait(false);
            lock (SharedGate) if (ReferenceEquals(_shared, closing)) _shared = null;
        }
        try
        {
            await backend.Ready.ConfigureAwait(false);
            var lease = new CaretNativeThread(backend);
            await backend.Enqueue(() =>
            {
                lease._javaAvailable = backend.Java.TryInitialize(path);
                backend.Add(lease);
                return 0;
            }).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            try { await ReleaseReference(backend).ConfigureAwait(false); }
            catch (Exception cleanup) { System.Diagnostics.Trace.TraceError("Caret startup cleanup failed: {0}", cleanup); }
            throw;
        }
    }

    // Called after a lease's detach command was enqueued. Reference reservations
    // include CreateAsync calls still awaiting startup, so startup cannot race unload.
    private static Task ReleaseReference(Backend backend)
    {
        lock (SharedGate)
        {
            if (--backend.LeaseCount != 0) return Task.CompletedTask;
            backend.Closing = true; // New CreateAsync calls wait immediately.
            // Each releasing lease has already queued its own work/detachment.
            // A separate final sentinel covers all those queues, irrespective of
            // the order in which concurrent disposers reached this reference gate.
            Task drained = backend.Enqueue(() => 0);
            return CloseBackendAsync(backend, drained);
        }
    }

    private static async Task CloseBackendAsync(Backend backend, Task drained)
    {
        try { await drained.ConfigureAwait(false); }
        finally
        {
            backend.RequestStop();
            try { await backend.JoinAsync().ConfigureAwait(false); }
            finally { lock (SharedGate) if (ReferenceEquals(_shared, backend)) _shared = null; }
        }
    }

    // Narrow lifecycle diagnostics for the input-free native smoke test.
    internal long RuntimeId => _backend.RuntimeId;
    internal nint Desktop => _backend.Desktop;
    internal Task WaitForExitAsync() => _backend.WaitForExitAsync();
    internal nint NotificationWindow { get { _backend.RequireCurrent(); return _backend.Window; } }
    internal bool JavaAvailable { get { _backend.RequireCurrent(); return _javaAvailable; } }

    internal event Action<nint>? WindowDestroyed
    {
        add { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _destroyed += value; } }
        remove { lock (_gate) _destroyed -= value; }
    }

    internal Task<T> RunAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(CaretNativeThread)));
            return _backend.Enqueue(action);
        }
    }

    // Reserve on the shared owner, including time spent in its queue. The guard
    // runs on that owner just before querying; expiry never cancels an active call.
    internal Task<CaretPosition?> TryRunJavaAsync(nint window, Func<bool> canStart)
    {
        ArgumentNullException.ThrowIfNull(canStart);
        lock (_gate)
        {
            if (_disposed) return Task.FromException<CaretPosition?>(new ObjectDisposedException(nameof(CaretNativeThread)));
            return _backend.TryEnqueueJava(() => CaretParallelQuery.ReadIfCurrent(canStart, () => TryGetJava(window)));
        }
    }

    // Replaces only the native query body for owner/admission smoke tests.
    internal void SetJavaQueryForTests(Func<nint, CaretPosition?>? query)
    {
        _backend.RequireCurrent();
        _javaQueryForTests = query;
    }

    // These methods are intentionally synchronous: core calls them only inside RunAsync.
    internal nint SampleForeground() { _backend.RequireCurrent(); return CaretNative.GetForegroundWindow(); }
    internal CaretPosition? TryGetMsaa(nint window) { _backend.RequireCurrent(); return CaretNative.TryGetMsaa(window); }
    internal CaretPosition? TryGetGuiThreadInfo() { _backend.RequireCurrent(); return CaretNative.TryGetGuiThreadInfo(); }
    internal CaretPosition? TryGetJava(nint window) => _backend.RunJava(() =>
        _javaQueryForTests is { } query ? query(window) :
        _javaAvailable ? _backend.Java.TryGet(window) : null);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _destroyed = null;
            // Admit detachment under the same gate as RunAsync, then close admission
            // to the shared runtime immediately if this was the final lease.
            Task detached = _backend.Enqueue(() => { _backend.Remove(this); return 0; });
            Task lifetime = ReleaseReference(_backend);
            _disposeTask = FinishDisposeAsync(detached, lifetime);
            return new(_disposeTask);
        }
    }

    private static async Task FinishDisposeAsync(Task detached, Task lifetime)
    {
        try { await detached.ConfigureAwait(false); }
        finally { await lifetime.ConfigureAwait(false); }
    }

    private void NotifyDestroyed(nint window)
    {
        Action<nint>? handlers;
        lock (_gate) handlers = _disposed ? null : _destroyed;
        if (handlers is null) return;
        foreach (Action<nint> handler in handlers.GetInvocationList())
        {
            try { handler(window); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Caret cache invalidation failed: {0}", error); }
        }
    }

    private sealed class Backend
    {
        [ThreadStatic] private static Backend? _current;
        private static long _nextRuntimeId;
        private readonly object _joinGate = new();
        private Task? _joinTask;
        internal readonly long RuntimeId = Interlocked.Increment(ref _nextRuntimeId);
        internal int LeaseCount; // guarded by SharedGate
        internal bool Closing; // guarded by SharedGate
        internal nint Window => _window;
        internal nint Desktop => _desktop;
        internal bool IsStoppingOrStopped { get { lock (_queueGate) return _stopping || _stopped; } }
        private readonly object _queueGate = new();
        private readonly Queue<Command> _commands = new();
        private readonly HashSet<CaretNativeThread> _leases = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private readonly nint _desktop;
        private bool _stopping, _stopped;
        private Task<CaretPosition?>? _javaPending; // guarded by _queueGate
        private int _javaCallDepth; // also covers explicit sequential calls
        private Exception? _failure;
        private nint _window;
        private uint _shellMessage;
        internal readonly JavaAccessBridge Java = new();
        internal Task Ready => _ready.Task;

        internal Backend(nint desktop)
        {
            _desktop = desktop;
            if (_desktop == 0) { _wake.Dispose(); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            _thread = new Thread(Run) { IsBackground = true, Name = "CaretExGetPos native" };
            // Raw IAccessible calls can run in MTA. In particular do not request STA
            // before binding the desktop: CLR STA startup can create COM windows.
            try { _thread.Start(); }
            catch { _wake.Dispose(); throw; }
        }

        internal void RequireCurrent()
        {
            if (_current != this) throw new InvalidOperationException("Native caret providers must run on their message thread.");
        }
        internal void Add(CaretNativeThread lease) => _leases.Add(lease);
        internal void Remove(CaretNativeThread lease) => _leases.Remove(lease);

        internal Task<T> Enqueue<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_queueGate)
            {
                if (_stopping || _stopped)
                {
                    completion.SetException(_failure ?? new ObjectDisposedException(nameof(CaretNativeThread)));
                    return completion.Task;
                }
                _commands.Enqueue(new(() => completion.TrySetResult(action()), error => completion.TrySetException(error)));
                _wake.Set();
            }
            return completion.Task;
        }

        internal Task<CaretPosition?> TryEnqueueJava(Func<CaretPosition?> action)
        {
            lock (_queueGate)
            {
                if (_stopping || _stopped)
                    return Task.FromException<CaretPosition?>(_failure ?? new ObjectDisposedException(nameof(CaretNativeThread)));
                if (_javaCallDepth != 0 || _javaPending is { IsCompleted: false })
                    return Task.FromResult<CaretPosition?>(null);
                // Enqueue uses this same reentrant gate: no other lease can reserve
                // between checking availability and publishing the pending task.
                return _javaPending = Enqueue(action);
            }
        }

        internal CaretPosition? RunJava(Func<CaretPosition?> action)
        {
            RequireCurrent();
            lock (_queueGate) _javaCallDepth++;
            try { return action(); }
            finally { lock (_queueGate) _javaCallDepth--; }
        }

        private bool Take(out Command? command)
        {
            lock (_queueGate)
            {
                command = null;
                return !_stopping && _commands.TryDequeue(out command);
            }
        }

        private unsafe void Run()
        {
            bool com = false, registered = false, shellRegistered = false;
            string className = "SevenKeyboard.CaretExGetPos." + Guid.NewGuid().ToString("N");
            nint instance = 0;
            try
            {
                if (CaretNative.GetThreadDesktop(CaretNative.GetCurrentThreadId()) != _desktop &&
                    CaretNative.SetThreadDesktop(_desktop) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot bind the native caret thread's desktop.");
                int hr = CaretNative.CoInitializeEx(0, 0); // COINIT_MULTITHREADED
                Marshal.ThrowExceptionForHR(hr);
                com = true; // S_FALSE also owns one balancing CoUninitialize.
                _current = this;
                using (new CaretDpi.Scope(CaretDpi.SystemAware))
                {
                    instance = CaretNative.GetModuleHandle(null);
                    if (instance == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    fixed (char* name = className)
                    {
                        CaretNative.WindowClass windowClass = new()
                        {
                            Size = (uint)sizeof(CaretNative.WindowClass),
                            Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                            Instance = instance, ClassName = (nint)name
                        };
                        if (CaretNative.RegisterClassEx(in windowClass) == 0)
                            throw new Win32Exception(Marshal.GetLastPInvokeError());
                        registered = true;
                    }
                    // Hidden top-level window: shell notifications don't target HWND_MESSAGE.
                    // No WS_VISIBLE, ShowWindow, focus/activation, or input APIs.
                    _window = CaretNative.CreateWindowEx(0x08000080, className, "", 0x80000000,
                        0, 0, 1, 1, 0, 0, instance, 0);
                    if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                _shellMessage = ShellHookWindow.MessageId;
                if (_shellMessage == 0 || !ShellHookWindow.Register(_window))
                    throw new InvalidOperationException("Cannot register the caret cache's shell notification window.");
                shellRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                _ready.TrySetResult();
                nint wakeHandle = _wake.SafeWaitHandle.DangerousGetHandle();
                while (true)
                {
                    lock (_queueGate) if (_stopping) break;
                    uint result = CaretNative.MsgWaitForMultipleObjectsEx(1, &wakeHandle, uint.MaxValue, 0x04FF, 4);
                    if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    for (int i = 0; i < 64 && CaretNative.PeekMessage(out var message, 0, 0, 0, 1) != 0; i++)
                    {
                        if (message.Id == 0x0012)
                        {
                            _failure = new InvalidOperationException("The native caret message loop received WM_QUIT.");
                            RequestStop(); break;
                        }
                        _ = CaretNative.TranslateMessage(in message);
                        _ = CaretNative.DispatchMessage(in message);
                    }
                    // Only short native stages are dispatched here. UIA awaits occur in
                    // the caller's async FIFO, leaving this pump free for JAB messages.
                    for (int i = 0; i < 32 && Take(out Command? command); i++) command!.Execute();
                    lock (_queueGate) if (_commands.Count != 0 && !_stopping) _wake.Set();
                }
            }
            catch (Exception error) { _failure = error; }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                // Retire even an unexpectedly failed runtime with surviving leases.
                // New factories wait for this thread's cleanup, then replace it;
                // late disposal of old leases is protected by shared identity checks.
                lock (SharedGate) Closing = true;
                lock (_queueGate)
                {
                    _stopped = true;
                    Exception error = _failure ?? new ObjectDisposedException(nameof(CaretNativeThread));
                    while (_commands.TryDequeue(out Command? command)) command.Fail(error);
                    _wake.Dispose();
                }
                _leases.Clear();
                // OpenJDK's DLL_PROCESS_DETACH deletes its global bridge and destroys
                // its own window. Free it here, on the initialization/query thread,
                // after all accepted queries and before COM/thread teardown.
                try { Java.Dispose(); } catch (Exception error) { _failure ??= error; }
                try
                {
                    if (shellRegistered && !ShellHookWindow.Deregister(_window))
                        throw new InvalidOperationException("Cannot deregister the caret shell window.");
                }
                catch (Exception error) { _failure ??= error; }
                try
                {
                    if (_window != 0 && CaretNative.DestroyWindow(_window) == 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    _window = 0;
                    if (registered) _ = CaretNative.UnregisterClass(className, instance);
                }
                catch (Exception error) { _failure ??= error; }
                _current = null;
                if (com) CaretNative.CoUninitialize();
                _ready.TrySetException(_failure ?? new InvalidOperationException("Native caret startup stopped."));
            }
        }

        internal Task WaitForExitAsync()
        {
            lock (_joinGate) return _joinTask ??= Task.Run(_thread.Join);
        }

        internal async Task JoinAsync()
        {
            await WaitForExitAsync().ConfigureAwait(false);
            if (_failure is not null) throw new InvalidOperationException("Native caret worker failed.", _failure);
        }

        internal void RequestStop()
        {
            lock (_queueGate)
            {
                if (_stopped) return;
                _stopping = true;
                _wake.Set();
            }
        }
        private void OnProcessExit(object? sender, EventArgs e)
        {
            RequestStop();
            if (_current != this) _thread.Join(TimeSpan.FromSeconds(1));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
        {
            try
            {
                Backend? current = _current;
                if (current is not null && message == current._shellMessage && wParam == 2 && lParam != 0)
                {
                    // Core callbacks only invalidate cache entries; they must not block
                    // or run provider work from this native callback.
                    foreach (CaretNativeThread lease in current._leases.ToArray()) lease.NotifyDestroyed(lParam);
                    return 0;
                }
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Caret window callback failed: {0}", error); }
            return CaretNative.DefWindowProc(window, message, wParam, lParam);
        }

        private sealed record Command(Action Run, Action<Exception> Fail)
        {
            internal void Execute() { try { Run(); } catch (Exception error) { Fail(error); } }
        }
    }
}
