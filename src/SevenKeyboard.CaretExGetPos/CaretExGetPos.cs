using System.ComponentModel;
using System.Runtime.InteropServices;
using SevenKeyboard.UiAutomation;

namespace SevenKeyboard;

/// <summary>Queries the current caret with bounded parallel lookup or explicit sequential lookup.</summary>
/// <remarks>
/// Results are selected in acceptance order; busy speculative providers are skipped. A supplied UIA client
/// is borrowed and must remain usable until this instance finishes disposing.
/// </remarks>
public sealed partial class CaretExGetPos : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<QueryRequest> _queries = new();
    private sealed record QueryRequest(CaretQueryMode Mode, TaskCompletionSource<CaretPosition?> Completion);
    private readonly CaretQueryState _state = new();
    private readonly Func<Task<CaretPosition?>> _query;
    private readonly Func<TimeSpan, Task<CaretQueryOperation>>? _parallelQuery;
    private Exception? _drainFailure;
    private readonly HashSet<Task> _drains = [];
    private readonly Func<ValueTask> _release;
    private bool _processing;
    private TaskCompletionSource? _disposal;

    private CaretExGetPos(CaretNativeThread native, UiAutomationClient? uia)
    {
        _query = () => QueryAsync(native, uia);
        _parallelQuery = budget => StartParallelAsync(native, uia, budget);
        _release = () => ReleaseAsync(native);
        native.WindowDestroyed += _state.Forget;
    }

    // A desktop-free seam for checking the real admission/drain lifecycle.
    internal CaretExGetPos(Func<Task<CaretPosition?>> query, Func<ValueTask> release, Func<TimeSpan, Task<CaretQueryOperation>>? parallelQuery = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(release);
        _query = query;
        _parallelQuery = parallelQuery ?? (_ => Task.FromResult(new CaretQueryOperation(_query(), Task.CompletedTask)));
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
            var workers = await CaretParallelNativeThreads.CreateAsync(native.Desktop).ConfigureAwait(false);
            try { return new CaretExGetPos(native, uia) { _parallelNative = workers }; }
            catch { await workers.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch
        {
            await native.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Queries in parallel: the first query per instance has a 200 ms preference budget, later queries 100 ms. Returns null when no usable result is available.</summary>
    /// <exception cref="ObjectDisposedException">Disposal has begun.</exception>
    public Task<CaretPosition?> GetPositionAsync() => GetPositionAsync(CaretQueryMode.ParallelPriority);

    /// <summary>Queues the selected mode. Mode is captured at admission; foreground at execution.</summary>
    public Task<CaretPosition?> GetPositionAsync(CaretQueryMode mode)
    {
        if (mode is not CaretQueryMode.Sequential and not CaretQueryMode.ParallelPriority)
            throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            TaskCompletionSource<CaretPosition?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queries.Enqueue(new QueryRequest(mode, completion));
            StartProcessing();
            return completion.Task;
        }
    }

    /// <summary>Rejects new queries, drains accepted queries, and releases native ownership.</summary>
    /// <remarks>Repeated calls await the same cleanup, including unfinished parallel work. The borrowed UIA client is never disposed here.</remarks>
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
            QueryRequest? request;
            lock (_gate)
            {
                if (!_queries.TryDequeue(out request))
                {
                    _processing = false;
                    disposal = _disposal;
                    break;
                }
            }

            CaretQueryOperation? operation = null;
            try
            {
                if (request.Mode == CaretQueryMode.Sequential) await DrainPendingAsync().ConfigureAwait(false);
                operation = request.Mode == CaretQueryMode.Sequential
                    ? new CaretQueryOperation(_query(), Task.CompletedTask)
                    : await (_parallelQuery ?? throw new InvalidOperationException("Parallel query backend is unavailable."))(TakeParallelPreferenceBudget()).ConfigureAwait(false);
                request.Completion.SetResult(await operation.Result.ConfigureAwait(false));
            }
            catch (Exception error)
            {
                // A query failure must not abandon later accepted work.
                request.Completion.SetException(error);
            }
            finally
            {
                if (operation is not null)
                {
                    // A completed result frees the coordinator, not the native owner.
                    // Per-provider slots prevent overlap; disposal still drains ownership.
                    TrackDrain(operation.Drain);
                }
            }
        }

        if (disposal is null)
            return;
        try
        {
            await DrainPendingAsync().ConfigureAwait(false);
            await _release().ConfigureAwait(false);
            if (_drainFailure is { } drainFailure) disposal.SetException(drainFailure);
            else disposal.SetResult();
        }
        catch (Exception error)
        {
            disposal.SetException(_drainFailure is { } drainFailure
                ? new AggregateException(drainFailure, error) : error);
        }
    }

    private void TrackDrain(Task drain)
    {
        Task observed = ObserveDrainAsync(drain);
        lock (_gate) if (!observed.IsCompleted) _drains.Add(observed);
        _ = RemoveDrainAsync(observed);
    }

    private async Task ObserveDrainAsync(Task drain)
    {
        try { await drain.ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) _drainFailure ??= error; }
    }

    private async Task RemoveDrainAsync(Task observed)
    {
        await observed.ConfigureAwait(false);
        lock (_gate) _drains.Remove(observed);
    }

    private Task DrainPendingAsync()
    {
        lock (_gate) return Task.WhenAll(_drains);
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
                                ? CaretQueryState.FromResolvedRectangle(await focusedElement.GetCaretRectangleAsync().ConfigureAwait(false), true)
                                : CaretQueryState.FromResolvedRectangle(await focusedElement.GetSelectionCaretRectangleAsync().ConfigureAwait(false), false);
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
        // All accepted batches have drained before ownership is released.
        Task nativeCleanup = native.DisposeAsync().AsTask();
        Task providersCleanup = _parallelNative?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        await Task.WhenAll(nativeCleanup, providersCleanup).ConfigureAwait(false);
    }
}
