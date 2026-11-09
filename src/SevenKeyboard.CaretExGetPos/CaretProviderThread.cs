using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

// A reusable owner for one native provider. Unlike JAB, these MTA/Win32 calls
// need no application message window. No native/COM object leaves its command.
internal sealed class CaretProviderThread : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly nint _desktop;
    private readonly bool _initializeCom;
    private bool _closing, _stopped;
    private Exception? _failure;
    private Task? _disposal;

    private CaretProviderThread(string name, nint desktop, bool initializeCom)
    {
        _desktop = desktop;
        _initializeCom = initializeCom;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        // Bind the desktop before COM can create any hidden windows.
        try { _thread.Start(); }
        catch { _wake.Dispose(); throw; }
    }

    internal static async Task<CaretProviderThread> CreateAsync(string name, nint desktop, bool initializeCom)
    {
        if (desktop == 0) throw new ArgumentException("A desktop handle is required.", nameof(desktop));
        var worker = new CaretProviderThread(name, desktop, initializeCom);
        try
        {
            await worker._ready.Task.ConfigureAwait(false);
            return worker;
        }
        catch
        {
            // Preserve the startup exception, after its thread has fully exited.
            try { await worker.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
            throw;
        }
    }

    internal Task<T> RunAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closing || _stopped)
                return Task.FromException<T>(_failure ?? new ObjectDisposedException(nameof(CaretProviderThread)));
            _commands.Enqueue(new(() => completion.TrySetResult(action()), error => completion.TrySetException(error)));
            _wake.Set();
        }
        return completion.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _closing = true;
            if (!_stopped) _wake.Set();
            _disposal = Task.Run(() =>
            {
                _thread.Join();
                if (_failure is not null) throw new InvalidOperationException("Caret provider worker failed.", _failure);
            });
            return new(_disposal);
        }
    }

    private void Run()
    {
        bool com = false;
        try
        {
            if (CaretNative.GetThreadDesktop(CaretNative.GetCurrentThreadId()) != _desktop &&
                CaretNative.SetThreadDesktop(_desktop) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot bind the caret provider's desktop.");
            if (_initializeCom)
            {
                Marshal.ThrowExceptionForHR(CaretNative.CoInitializeEx(0, 0)); // MTA
                com = true; // S_FALSE also needs balancing.
            }
            _ready.TrySetResult();
            while (true)
            {
                Command? command;
                lock (_gate)
                {
                    if (!_commands.TryDequeue(out command) && _closing) break;
                }
                if (command is null) _wake.WaitOne();
                else command.Execute();
            }
        }
        catch (Exception error) { _failure = error; }
        finally
        {
            lock (_gate)
            {
                _stopped = true;
                Exception error = _failure ?? new ObjectDisposedException(nameof(CaretProviderThread));
                while (_commands.TryDequeue(out Command? command)) command.Fail(error);
                _wake.Dispose();
            }
            if (com) CaretNative.CoUninitialize();
            _ready.TrySetException(_failure ?? new InvalidOperationException("Caret provider startup stopped."));
        }
    }

    private sealed record Command(Action Run, Action<Exception> Fail)
    {
        internal void Execute() { try { Run(); } catch (Exception error) { Fail(error); } }
    }
}
