using SevenKeyboard;
using static CorePolicyTests;

internal static class CoreLifecycleTests
{
    internal static async Task RunAsync()
    {
        await FifoAndDrainAsync();
        await QueryErrorsDoNotAbandonQueueAsync();
        await IndependentInstancesAsync();
        await CleanupFailureIsRepeatableAsync();
    }

    private static async Task FifoAndDrainAsync()
    {
        TaskCompletionSource[] entered = [Signal(), Signal(), Signal()];
        TaskCompletionSource[] resume = [Signal(), Signal(), Signal()];
        TaskCompletionSource releasing = Signal(), finishRelease = Signal();
        int[] observedForeground = new int[3];
        int foreground = 10, next = 0, active = 0, releaseCount = 0;
        CaretExGetPos caret = new(async () =>
        {
            Require(Interlocked.Increment(ref active) == 1, "Queries must not overlap.");
            int index = next++;
            observedForeground[index] = Volatile.Read(ref foreground);
            entered[index].SetResult();
            await resume[index].Task;
            Interlocked.Decrement(ref active);
            return new CaretPosition(observedForeground[index], 0, 1, 2, "MSAA");
        }, async () =>
        {
            Require(Volatile.Read(ref active) == 0, "Release must run after the last query.");
            Interlocked.Increment(ref releaseCount);
            releasing.SetResult();
            await finishRelease.Task;
        });
        Task<CaretPosition?> first = caret.GetPositionAsync();
        await entered[0].Task;
        Task<CaretPosition?> second = caret.GetPositionAsync();
        Task<CaretPosition?> third = caret.GetPositionAsync();
        Task dispose = caret.DisposeAsync().AsTask();
        Require(ReferenceEquals(dispose, caret.DisposeAsync().AsTask()), "Dispose must return the same cleanup task.");
        ExpectRejected(caret);
        Require(!second.IsCompleted && !third.IsCompleted && !dispose.IsCompleted,
            "Disposal must drain accepted queued work.");
        Volatile.Write(ref foreground, 20);
        resume[0].SetResult();
        await entered[1].Task;
        Require((await first)?.X == 10 && !third.IsCompleted, "FIFO first result was lost.");
        Volatile.Write(ref foreground, 30);
        resume[1].SetResult();
        await entered[2].Task;
        Require((await second)?.X == 20, "A queued query must sample state at execution, not admission.");
        resume[2].SetResult();
        CaretPosition? detached = await third;
        await releasing.Task;
        Require(detached?.X == 30 && !dispose.IsCompleted && releaseCount == 1,
            "Cleanup must wait for all queries and its own resource release.");
        finishRelease.SetResult();
        await dispose;
        Require(detached?.X == 30 && releaseCount == 1, "Plain results survive cleanup without retaining it.");
        ExpectRejected(caret);
    }

    private static async Task QueryErrorsDoNotAbandonQueueAsync()
    {
        int queryCount = 0, released = 0;
        ObjectDisposedException disposedError = new("borrowed UIA");
        ArgumentException programmerError = new("provider misuse");
        CaretExGetPos caret = new(() => ++queryCount switch
        {
            1 => Task.FromException<CaretPosition?>(disposedError),
            2 => throw programmerError,
            _ => Task.FromResult<CaretPosition?>(new(7, 8, 1, 2, "MSAA")),
        }, () => { released++; return ValueTask.CompletedTask; });
        Task<CaretPosition?> first = caret.GetPositionAsync();
        Task<CaretPosition?> second = caret.GetPositionAsync();
        Task<CaretPosition?> third = caret.GetPositionAsync();
        Task dispose = caret.DisposeAsync().AsTask();
        await ExpectErrorAsync(first, disposedError);
        await ExpectErrorAsync(second, programmerError);
        Require((await third)?.X == 7, "A failed query must not abandon later accepted work.");
        await dispose;
        Require(released == 1 && queryCount == 3, "Failures must not skip drain or cleanup.");
    }

    private static async Task IndependentInstancesAsync()
    {
        TaskCompletionSource entered = Signal(), resume = Signal();
        await using CaretExGetPos first = new(async () =>
        {
            entered.SetResult();
            await resume.Task;
            return null;
        }, () => ValueTask.CompletedTask);
        await using CaretExGetPos second = new(() => Task.FromResult<CaretPosition?>(new(9, 0, 1, 2, "MSAA")),
            () => ValueTask.CompletedTask);
        Task<CaretPosition?> pending = first.GetPositionAsync();
        await entered.Task;
        Require((await second.GetPositionAsync())?.X == 9 && !pending.IsCompleted,
            "Separate caret instances must not share an orchestration queue.");
        resume.SetResult();
        await pending;
        for (int i = 0; i < 10; i++)
            Require((await second.GetPositionAsync())?.X == 9, "An idle queue must restart.");
    }

    private static async Task CleanupFailureIsRepeatableAsync()
    {
        InvalidOperationException error = new("cleanup failure");
        int releaseCount = 0;
        CaretExGetPos caret = new(() => Task.FromResult<CaretPosition?>(null), () =>
        {
            releaseCount++;
            return ValueTask.FromException(error);
        });
        Task first = caret.DisposeAsync().AsTask();
        await ExpectErrorAsync(first, error);
        Task second = caret.DisposeAsync().AsTask();
        Require(ReferenceEquals(first, second) && releaseCount == 1, "Failed cleanup must not be repeated.");
        await ExpectErrorAsync(second, error);
        ExpectRejected(caret);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ExpectRejected(CaretExGetPos caret)
    {
        try { _ = caret.GetPositionAsync(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("A closing instance accepted a new query.");
    }

    private static async Task ExpectErrorAsync(Task task, Exception expected)
    {
        try { await task; }
        catch (Exception actual) when (ReferenceEquals(actual, expected)) { return; }
        throw new InvalidOperationException("The caller did not receive its original exception.");
    }
}
