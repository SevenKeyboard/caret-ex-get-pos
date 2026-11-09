using SevenKeyboard;
using static CorePolicyTests;

internal static class ParallelQueryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly string[] Providers =
        ["JavaAccessBridge", "UIATextPattern2", "MSAA", "GUIThreadInfo", "UIATextPattern"];

    internal static async Task RunAsync()
    {
        await FixedPriorityAsync();
        await EarlyResultAndDrainAsync();
        await RequiredFailuresAsync();
        await AllUnavailableAsync();
        await AlternatingModesAndDrainAsync();
        await LosingFaultAndCleanupAsync(cleanupFails: false);
        await LosingFaultAndCleanupAsync(cleanupFails: true);
        await InvalidModeAndDefaultAsync();
        Console.WriteLine("Parallel caret priority/queue/drain checks passed (no desktop providers).");
    }

    private static async Task FixedPriorityAsync()
    {
        // Every possible completion order, with each provider as the first available one.
        foreach (int[] order in Permutations([0, 1, 2, 3, 4], 0))
        {
            for (int winner = 0; winner < Providers.Length; winner++)
            {
                var candidates = Candidates();
                CaretQueryOperation operation = Start(candidates);
                Require(!operation.Result.IsCompleted && !operation.Drain.IsCompleted,
                    "Starting pending providers must return an operation immediately.");
                foreach (int index in order)
                {
                    candidates[index].SetResult(index < winner ? null : Position(index));
                    if (!candidates[winner].Task.IsCompleted ||
                        candidates.Take(winner).Any(candidate => !candidate.Task.IsCompleted))
                        Require(!operation.Result.IsCompleted,
                            "A lower success must wait for every higher-priority provider.");
                }
                Require(await operation.Result.WaitAsync(Timeout) == Position(winner),
                    $"Completion order {string.Join(",", order)} changed priority for {Providers[winner]}.");
                await operation.Drain.WaitAsync(Timeout);
            }
        }
    }

    private static async Task EarlyResultAndDrainAsync()
    {
        var candidates = Candidates();
        TaskCompletionSource cleanup = Signal();
        CaretQueryOperation operation = Start(candidates, cleanup.Task);
        candidates[0].SetResult(Position(0));
        Require(await operation.Result.WaitAsync(Timeout) == Position(0),
            "The highest-priority success must be delivered before speculative work ends.");
        Require(!operation.Drain.IsCompleted, "Drain must include unfinished losing providers.");
        for (int index = 1; index < candidates.Length - 1; index++)
            candidates[index].SetResult(null);
        Require(!operation.Drain.IsCompleted, "Drain must include the last provider.");
        candidates[^1].SetResult(Position(4));
        Require(!operation.Drain.IsCompleted, "Drain must include ownership cleanup.");
        cleanup.SetResult();
        await operation.Drain.WaitAsync(Timeout);
        Require(await operation.Result.WaitAsync(Timeout) == Position(0),
            "Later successes must not replace an already delivered result.");

        // Already-finished cleanup must not let Drain skip unfinished providers.
        candidates = Candidates();
        operation = Start(candidates, Task.CompletedTask);
        for (int index = 0; index < candidates.Length - 1; index++)
            candidates[index].SetResult(index == 0 ? Position(0) : null);
        await operation.Result.WaitAsync(Timeout);
        Require(!operation.Drain.IsCompleted, "Cleanup completion cannot stand in for provider drain.");
        candidates[^1].SetResult(null);
        await operation.Drain.WaitAsync(Timeout);
    }

    private static async Task RequiredFailuresAsync()
    {
        for (int failing = 0; failing < Providers.Length; failing++)
        {
            var candidates = Candidates();
            InvalidOperationException error = new($"required provider {failing}");
            CaretQueryOperation operation = Start(candidates);
            // Lower providers succeed first; they must not mask a required-path error.
            for (int index = candidates.Length - 1; index > failing; index--)
                candidates[index].SetResult(Position(index));
            for (int index = 0; index < failing; index++)
                candidates[index].SetResult(null);
            candidates[failing].SetException(error);
            await ExpectErrorAsync(operation.Result, error);
            await operation.Drain.WaitAsync(Timeout);
        }
    }

    private static async Task AllUnavailableAsync()
    {
        var candidates = Candidates();
        CaretQueryOperation operation = Start(candidates);
        for (int index = candidates.Length - 1; index > 0; index--)
            candidates[index].SetResult(null);
        Require(!operation.Result.IsCompleted, "All-null selection must still await the first provider.");
        candidates[0].SetResult(null);
        Require(await operation.Result.WaitAsync(Timeout) is null, "All unavailable providers must return null.");
        await operation.Drain.WaitAsync(Timeout);
    }

    private static async Task AlternatingModesAndDrainAsync()
    {
        CaretQueryMode[] modes = [CaretQueryMode.Sequential, CaretQueryMode.ParallelPriority,
            CaretQueryMode.Sequential, CaretQueryMode.ParallelPriority,
            CaretQueryMode.Sequential, CaretQueryMode.ParallelPriority];
        var entered = modes.Select(_ => Signal()).ToArray();
        var results = modes.Select(_ => Candidate()).ToArray();
        var drains = modes.Select(_ => Signal()).ToArray();
        for (int index = 0; index < modes.Length; index++)
            if (modes[index] == CaretQueryMode.Sequential) drains[index].SetResult();
        TaskCompletionSource releasing = Signal(), finishRelease = Signal();
        int next = 0, releaseCount = 0;

        int Enter(CaretQueryMode actual)
        {
            int index = Interlocked.Increment(ref next) - 1;
            Require(index < modes.Length && actual == modes[index],
                "Each queued request must retain its own admitted mode.");
            if (index > 0)
                Require(results[index - 1].Task.IsCompleted && drains[index - 1].Task.IsCompleted,
                    "The next query ran before the previous result and drain completed.");
            entered[index].SetResult();
            return index;
        }

        CaretExGetPos caret = new(
            () => results[Enter(CaretQueryMode.Sequential)].Task,
            async () =>
            {
                Require(Volatile.Read(ref next) == modes.Length && drains[^1].Task.IsCompleted,
                    "Disposal released resources before all accepted requests drained.");
                Interlocked.Increment(ref releaseCount);
                releasing.SetResult();
                await finishRelease.Task.WaitAsync(Timeout);
            },
            budget =>
            {
                int index = Enter(CaretQueryMode.ParallelPriority);
                Require(budget == TimeSpan.FromMilliseconds(index == 1 ? 200 : 100),
                    "Sequential requests before and between queued parallel queries must neither consume nor reset the 200ms budget.");
                return Task.FromResult(new CaretQueryOperation(results[index].Task, drains[index].Task));
            });

        Task<CaretPosition?>[] queries = new Task<CaretPosition?>[modes.Length];
        // Hold the first result so every following mode is captured while queued.
        queries[0] = caret.GetPositionAsync(CaretQueryMode.Sequential);
        await entered[0].Task.WaitAsync(Timeout);
        for (int index = 1; index < modes.Length; index++)
            queries[index] = caret.GetPositionAsync(modes[index]);
        Task disposal = caret.DisposeAsync().AsTask();
        Require(ReferenceEquals(disposal, caret.DisposeAsync().AsTask()),
            "Concurrent disposal calls must share completion.");
        foreach (CaretQueryMode mode in new[] { CaretQueryMode.Sequential, CaretQueryMode.ParallelPriority })
            ExpectDisposed(caret, mode);

        for (int index = 0; index < modes.Length; index++)
        {
            await entered[index].Task.WaitAsync(Timeout);
            CaretPosition expected = new(index, 10, 1, 2, modes[index].ToString());
            results[index].SetResult(expected);
            Require(await queries[index].WaitAsync(Timeout) == expected, "FIFO result was routed to the wrong request.");
            if (modes[index] == CaretQueryMode.ParallelPriority)
            {
                Require(Volatile.Read(ref next) == index + 1 && !releasing.Task.IsCompleted && !disposal.IsCompleted,
                    "A following sequential query and disposal must wait for the preceding parallel drain.");
                drains[index].SetResult();
            }
        }
        await releasing.Task.WaitAsync(Timeout);
        Require(!disposal.IsCompleted && releaseCount == 1, "Disposal must await resource release exactly once.");
        finishRelease.SetResult();
        await disposal.WaitAsync(Timeout);
        Require(releaseCount == 1, "Repeated disposal must not repeat release.");
    }

    private static async Task LosingFaultAndCleanupAsync(bool cleanupFails)
    {
        var candidates = Candidates();
        TaskCompletionSource cleanup = Signal(), nextEntered = Signal();
        InvalidOperationException loserError = new("unused provider");
        InvalidOperationException cleanupError = new("parallel cleanup");
        CaretQueryOperation operation = Start(candidates, cleanup.Task);
        int released = 0;
        CaretExGetPos caret = new(() =>
        {
            Require(operation.Drain.IsCompleted, "A queued request started before the faulty batch drained.");
            nextEntered.SetResult();
            return Task.FromResult<CaretPosition?>(Position(3));
        }, () =>
        {
            Require(operation.Drain.IsCompleted && nextEntered.Task.IsCompleted,
                "Release skipped the failed drain or abandoned the queued request.");
            Interlocked.Increment(ref released);
            return ValueTask.CompletedTask;
        }, _ => Task.FromResult(operation));

        Task<CaretPosition?> first = caret.GetPositionAsync(CaretQueryMode.ParallelPriority);
        Task<CaretPosition?> next = caret.GetPositionAsync(CaretQueryMode.Sequential);
        Task disposal = caret.DisposeAsync().AsTask();
        // Fault a lower provider before the higher success, then hold the last loser.
        candidates[1].SetException(loserError);
        candidates[0].SetResult(Position(0));
        candidates[2].SetResult(null);
        candidates[3].SetResult(null);
        Require(await first.WaitAsync(Timeout) == Position(0), "A losing fault poisoned the successful result.");
        Require(!nextEntered.Task.IsCompleted && !disposal.IsCompleted,
            "A losing fault must not bypass remaining provider work.");
        candidates[4].SetException(new ArgumentException("another unused provider"));
        Require(!operation.Drain.IsCompleted, "Provider faults must not bypass cleanup.");
        if (cleanupFails) cleanup.SetException(cleanupError);
        else cleanup.SetResult();

        // Awaiting Drain verifies that all losing faults are consumed without propagating.
        if (cleanupFails) await ExpectErrorAsync(operation.Drain, cleanupError);
        else await operation.Drain.WaitAsync(Timeout);
        Require(await next.WaitAsync(Timeout) == Position(3), "A drain failure abandoned accepted work.");
        if (cleanupFails)
        {
            await ExpectErrorAsync(disposal, cleanupError);
            Task repeated = caret.DisposeAsync().AsTask();
            Require(ReferenceEquals(disposal, repeated), "Failed disposal must retain the original task.");
            await ExpectErrorAsync(repeated, cleanupError);
        }
        else await disposal.WaitAsync(Timeout);
        Require(released == 1 && await first.WaitAsync(Timeout) == Position(0),
            "Cleanup must release once and preserve the already delivered result.");
    }

    private static async Task InvalidModeAndDefaultAsync()
    {
        int sequentialCalls = 0, parallelCalls = 0;
        CaretExGetPos caret = new(() =>
        {
            sequentialCalls++;
            return Task.FromResult<CaretPosition?>(Position(0));
        }, () => ValueTask.CompletedTask, _ =>
        {
            parallelCalls++;
            return Task.FromResult(new CaretQueryOperation(
                Task.FromResult<CaretPosition?>(Position(1)), Task.CompletedTask));
        });
        foreach (int invalid in new[] { -1, 2, int.MinValue, int.MaxValue })
        {
            try { _ = caret.GetPositionAsync((CaretQueryMode)invalid); }
            catch (ArgumentOutOfRangeException error) when (error.ParamName == "mode") { continue; }
            throw new InvalidOperationException("Invalid enum admitted work.");
        }
        Require(await caret.GetPositionAsync() == Position(1), "The default must select bounded parallel lookup.");
        Require(await caret.GetPositionAsync(CaretQueryMode.Sequential) == Position(0), "Explicit sequential lookup remains available.");
        await caret.DisposeAsync();
        Require(sequentialCalls == 1 && parallelCalls == 1, "Invalid requests must not execute providers.");
    }

    private static CaretQueryOperation Start(TaskCompletionSource<CaretPosition?>[] candidates, Task? cleanup = null) =>
        CaretParallelQuery.Start(candidates[0].Task, candidates[1].Task, candidates[2].Task,
            candidates[3].Task, candidates[4].Task, cleanup);

    private static TaskCompletionSource<CaretPosition?>[] Candidates() =>
        Providers.Select(_ => Candidate()).ToArray();

    private static TaskCompletionSource<CaretPosition?> Candidate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CaretPosition Position(int index) => new(index, 20 + index, 1, 12, Providers[index]);

    private static IEnumerable<int[]> Permutations(int[] values, int start)
    {
        if (start == values.Length)
        {
            yield return (int[])values.Clone();
            yield break;
        }
        for (int index = start; index < values.Length; index++)
        {
            (values[start], values[index]) = (values[index], values[start]);
            foreach (int[] permutation in Permutations(values, start + 1))
                yield return permutation;
            (values[start], values[index]) = (values[index], values[start]);
        }
    }

    private static async Task ExpectErrorAsync(Task task, Exception expected)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception actual) when (ReferenceEquals(actual, expected)) { return; }
        throw new InvalidOperationException("The original exception was not preserved.");
    }

    private static void ExpectDisposed(CaretExGetPos caret, CaretQueryMode mode)
    {
        try { _ = caret.GetPositionAsync(mode); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("A disposing instance admitted another request.");
    }
}