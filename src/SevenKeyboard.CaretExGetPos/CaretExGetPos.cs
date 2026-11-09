using System.ComponentModel;
using System.Runtime.InteropServices;
using SevenKeyboard.UiAutomation;

namespace SevenKeyboard;

/// <summary>Queries the current caret, trying the original providers in their learned order.</summary>
/// <remarks>
/// Queries are accepted synchronously and executed in FIFO order. A supplied UIA client
/// is borrowed and must remain usable until this instance finishes disposing.
/// </remarks>
public sealed class CaretExGetPos : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource<CaretPosition?>> _queries = new();
    private readonly CaretQueryState _state = new();
    private readonly Func<Task<CaretPosition?>> _query;
    private readonly Func<ValueTask> _release;
    private bool _processing;
    private TaskCompletionSource? _disposal;

    private CaretExGetPos(CaretNativeThread native, UiAutomationClient? uia)
    {
        _query = () => QueryAsync(native, uia);
        _release = () => ReleaseAsync(native);
        native.WindowDestroyed += _state.Forget;
    }

    // A desktop-free seam for checking the real admission/drain lifecycle.
    internal CaretExGetPos(Func<Task<CaretPosition?>> query, Func<ValueTask> release)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(release);
        _query = query;
        _release = release;
    }

    /// <summary>Initializes native providers without sampling the foreground window.</summary>
    /// <param name="uia">Optional externally owned UIA client. Null skips UIA providers.</param>
    /// <param name="javaAccessBridgePath">Optional absolute JAB DLL path; null uses the bundled default.</param>
    public static async Task<CaretExGetPos> CreateAsync(
        UiAutomationClient? uia = null, string? javaAccessBridgePath = null)
    {
        CaretNativeThread native = await CaretNativeThread.CreateAsync(javaAccessBridgePath).ConfigureAwait(false);
        try
        {
            return new CaretExGetPos(native, uia);
        }
        catch
        {
            await native.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Queues a query. The foreground window is sampled when that query starts.</summary>
    /// <exception cref="ObjectDisposedException">Disposal has begun.</exception>
    public Task<CaretPosition?> GetPositionAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            TaskCompletionSource<CaretPosition?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queries.Enqueue(completion);
            StartProcessing();
            return completion.Task;
        }
    }

    /// <summary>Rejects new queries, drains accepted queries, and releases native ownership.</summary>
    /// <remarks>Repeated calls await the same cleanup. The borrowed UIA client is never disposed here.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is null)
            {
                _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                StartProcessing();
            }
            return new ValueTask(_disposal.Task);
        }
    }

    // _gate is held by the caller; the runner never holds it while awaiting work.
    private void StartProcessing()
    {
        if (_processing)
            return;
        _processing = true;
        _ = Task.Run(ProcessAsync);
    }

    private async Task ProcessAsync()
    {
        TaskCompletionSource? disposal;
        while (true)
        {
            TaskCompletionSource<CaretPosition?>? completion;
            lock (_gate)
            {
                if (!_queries.TryDequeue(out completion))
                {
                    _processing = false;
                    disposal = _disposal;
                    break;
                }
            }

            try
            {
                completion.SetResult(await _query().ConfigureAwait(false));
            }
            catch (Exception error)
            {
                // Provider/programmer failures are delivered to this caller without
                // abandoning later, already accepted queries or shutdown cleanup.
                completion.SetException(error);
            }
        }

        if (disposal is null)
            return;
        try
        {
            await _release().ConfigureAwait(false);
            disposal.SetResult();
        }
        catch (Exception error)
        {
            disposal.SetException(error);
        }
    }

    private async Task<CaretPosition?> QueryAsync(CaretNativeThread native, UiAutomationClient? uia)
    {
        var start = await native.RunAsync(() =>
        {
            nint window = native.SampleForeground();
            return (Window: window, Entry: window == 0 ? null : _state.Begin(window));
        }).ConfigureAwait(false);
        if (start.Entry is null)
            return null;

        UiaElement? focusedElement = null;
        bool focusAttempted = false;
        try
        {
            foreach (CaretProvider provider in _state.GetOrder(start.Entry))
            {
                CaretPosition? position;
                switch (provider)
                {
                    case CaretProvider.JavaAccessBridge:
                        position = await native.RunAsync(() => native.TryGetJava(start.Window)).ConfigureAwait(false);
                        break;
                    case CaretProvider.MSAA:
                        position = await native.RunAsync(() => native.TryGetMsaa(start.Window)).ConfigureAwait(false);
                        break;
                    case CaretProvider.GUIThreadInfo:
                        position = await native.RunAsync(native.TryGetGuiThreadInfo).ConfigureAwait(false);
                        break;
                    case CaretProvider.UIATextPattern2:
                    case CaretProvider.UIATextPattern:
                        if (uia is null)
                            continue;
                        try
                        {
                            if (!focusAttempted)
                            {
                                focusAttempted = true;
                                focusedElement = await uia.GetFocusedElementAsync().ConfigureAwait(false);
                            }
                            if (focusedElement is null)
                                continue;
                            position = provider == CaretProvider.UIATextPattern2
                                ? CaretQueryState.FromCaretRange(await focusedElement.GetCaretRangeAsync().ConfigureAwait(false))
                                : CaretQueryState.FromFirstTextSelection(await focusedElement.GetFirstTextSelectionAsync().ConfigureAwait(false));
                        }
                        catch (Exception error) when (error is COMException or Win32Exception)
                        {
                            // Unsupported/missing data is null. Only expected provider
                            // failures fall through; lifetime/programmer errors propagate.
                            continue;
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unknown caret provider.");
                }

                if (position.HasValue)
                {
                    _state.Complete(start.Entry, provider);
                    return position;
                }
            }
            _state.Complete(start.Entry, null);
            return null;
        }
        finally
        {
            if (focusedElement is not null)
                await focusedElement.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync(CaretNativeThread native)
    {
        native.WindowDestroyed -= _state.Forget;
        _state.Clear();
        await native.DisposeAsync().ConfigureAwait(false);
    }
}
