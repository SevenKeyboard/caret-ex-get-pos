using SevenKeyboard;
using static CorePolicyTests;

// All scheduling decisions are driven by completion signals. Five-second waits
// only bound a broken test; they never act as the query's preference deadline.
internal static class BoundedQueryTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] Providers =
        ["JavaAccessBridge", "UIATextPattern2", "MSAA", "GUIThreadInfo", "UIATextPattern"];

    internal static async Task RunAsync()
    {
        SynchronousBudgetExpiry();
        await DeadlineSelectsBestCompletedAsync();
        await DeadlineWithoutResultAsync();
        await EarlyResultAsync();
        await UnboundedAndAllUnavailableAsync();
        await BusySlotAndExpiredResultsAsync();
        await SlotRecoversAfterFailureAsync();
        await LateFaultsAfterExpiryAsync();
        await LosingErrorsAndCleanupAsync(cleanupFails: false);
        await LosingErrorsAndCleanupAsync(cleanupFails: true);
        await ParallelRequestsAndDisposalAsync();
        await IndependentInstanceBudgetsAsync();
        await FirstAttemptConsumesBudgetAsync();
        await SequentialWaitsForAllDrainsAsync();
        await CleanupFaultSurvivesLaterQueriesAsync(faultAlreadyCompleted: true);
        await CleanupFaultSurvivesLaterQueriesAsync(faultAlreadyCompleted: false);
        await DefaultWithoutParallelBackendAsync();
        Console.WriteLine("Bounded caret selection/slot/drain checks passed (signals only; no desktop providers).");
    }

    private static void SynchronousBudgetExpiry()
    {
        bool eligible = false;
        int reads = 0;
        CaretPosition? Read() { reads++; eligible = false; return Position(0); }
        Require(CaretParallelQuery.ReadIfCurrent(() => eligible, Read) is null && reads == 0,
            "Expired queued work must never enter the provider.");
        eligible = true;
        Require(CaretParallelQuery.ReadIfCurrent(() => eligible, Read) is null && reads == 1,
            "A call completing after its budget/context expires must not provide a candidate.");
        Require(CaretParallelQuery.ReadIfCurrent(() => true, () => Position(0)) == Position(0),
            "A result acquired within a valid observation window must survive.");
    }

    private static async Task DeadlineSelectsBestCompletedAsync()
    {
        foreach (bool alreadyExpired in new[] { false, true })
        {
            for (int winner = 1; winner < Providers.Length; winner++)
            {
                var candidates = Candidates();
                TaskCompletionSource deadline = Signal(), cleanup = Signal();
                // Lower-priority successes arrive first. Higher providers stay busy.
                for (int index = Providers.Length - 1; index >= winner; index--)
                    candidates[index].SetResult(Position(index));
                if (alreadyExpired) deadline.SetResult();
                CaretQueryOperation operation = Start(candidates, deadline.Task, cleanup.Task);
                try
                {
                    if (!alreadyExpired)
                    {
                        Require(!operation.Result.IsCompleted,
                            "A completed lower result must wait for unresolved higher providers before the deadline.");
                        deadline.SetResult();
                    }
                    Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(winner),
                        $"Deadline selection must choose the best completed result, not the first completion ({winner}).");
                    Require(!operation.Drain.IsCompleted,
                        "The preference deadline must not complete pending provider work or ownership cleanup.");

                    for (int index = 0; index < winner; index++)
                        candidates[index].SetResult(Position(index, sample: 2));
                    Require(!operation.Drain.IsCompleted, "Provider completion must still wait for cleanup.");
                    cleanup.SetResult();
                    await operation.Drain.WaitAsync(SafetyTimeout);
                    Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(winner),
                        "A late higher-priority result must not replace the delivered position.");
                }
                finally
                {
                    Finish(candidates);
                    deadline.TrySetResult();
                    cleanup.TrySetResult();
                    await ObserveForCleanupAsync(operation.Drain);
                }
            }
        }
    }

    private static async Task DeadlineWithoutResultAsync()
    {
        foreach (bool someUnavailable in new[] { false, true })
        {
            var candidates = Candidates();
            TaskCompletionSource deadline = Signal();
            if (someUnavailable)
            {
                candidates[1].SetResult(null);
                candidates[3].SetResult(null);
            }
            CaretQueryOperation operation = Start(candidates, deadline.Task);
            try
            {
                Require(!operation.Result.IsCompleted, "Pending providers must be given the preference budget.");
                deadline.SetResult();
                Require(await operation.Result.WaitAsync(SafetyTimeout) is null,
                    "Expiry without a completed position must return null, even if native calls remain pending.");
                Require(!operation.Drain.IsCompleted, "A null result must not abandon pending providers.");
                for (int index = 0; index < candidates.Length; index++)
                    candidates[index].TrySetResult(Position(index));
                await operation.Drain.WaitAsync(SafetyTimeout);
                Require(await operation.Result.WaitAsync(SafetyTimeout) is null,
                    "An expired null result must not acquire a late position.");
            }
            finally
            {
                Finish(candidates);
                deadline.TrySetResult();
                await ObserveForCleanupAsync(operation.Drain);
            }
        }
    }

    private static async Task EarlyResultAsync()
    {
        for (int winner = 0; winner < Providers.Length; winner++)
        {
            var candidates = Candidates();
            TaskCompletionSource deadline = Signal();
            for (int index = 0; index < winner; index++)
                candidates[index].SetResult(null);
            CaretQueryOperation operation = Start(candidates, deadline.Task);
            try
            {
                candidates[winner].SetResult(Position(winner));
                Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(winner),
                    "Once higher providers are unavailable, a success must not wait for the deadline or lower providers.");
                Require(!deadline.Task.IsCompleted, "Early selection must not require expiry.");
                if (winner < Providers.Length - 1)
                    Require(!operation.Drain.IsCompleted, "Early selection must retain unfinished lower work.");
            }
            finally
            {
                Finish(candidates);
                deadline.TrySetResult();
                await ObserveForCleanupAsync(operation.Drain);
            }
        }
    }

    private static async Task UnboundedAndAllUnavailableAsync()
    {
        var candidates = Candidates();
        for (int index = 1; index < candidates.Length; index++)
            candidates[index].SetResult(Position(index));
        CaretQueryOperation unbounded = Start(candidates, deadline: null);
        try
        {
            Require(!unbounded.Result.IsCompleted, "A null deadline must preserve unbounded priority waiting.");
            candidates[0].SetResult(null);
            Require(await unbounded.Result.WaitAsync(SafetyTimeout) == Position(1),
                "Unbounded selection must still use provider priority.");
            await unbounded.Drain.WaitAsync(SafetyTimeout);
        }
        finally
        {
            Finish(candidates);
            await ObserveForCleanupAsync(unbounded.Drain);
        }

        TaskCompletionSource deadline = Signal();
        candidates = Candidates();
        Finish(candidates);
        CaretQueryOperation unavailable = Start(candidates, deadline.Task);
        Require(await unavailable.Result.WaitAsync(SafetyTimeout) is null && !deadline.Task.IsCompleted,
            "If every provider is already unavailable, null must be returned without waiting for expiry.");
        await unavailable.Drain.WaitAsync(SafetyTimeout);
    }

    private static async Task BusySlotAndExpiredResultsAsync()
    {
        CaretProviderSlot<CaretPosition?> slot = new();
        var oldProvider = Candidate();
        var freshProvider = Candidate();
        TaskCompletionSource firstDeadline = Signal(), nextDeadline = Signal();
        int factoryCalls = 0;
        Require(slot.Pending.IsCompleted, "A new provider slot must be idle.");
        Require(slot.TryStart(() => { factoryCalls++; return oldProvider.Task; }, out var firstTask),
            "An idle slot must admit a provider.");
        Require(ReferenceEquals(slot.Pending, firstTask) && ReferenceEquals(firstTask, oldProvider.Task),
            "Pending must represent the admitted provider, not result delivery.");
        CaretQueryOperation first = CaretParallelQuery.Start(
            None(), firstTask, None(), None(), None(), deadline: firstDeadline.Task);
        CaretQueryOperation? second = null, third = null;
        try
        {
            firstDeadline.SetResult();
            Require(await first.Result.WaitAsync(SafetyTimeout) is null && !first.Drain.IsCompleted,
                "The first request must expire while its UIA provider remains occupied.");

            Task<CaretPosition?> skipped = None();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Require(!slot.TryStart(() => { factoryCalls++; return freshProvider.Task; }, out skipped),
                    "A busy slot must reject duplicate provider work.");
                Require(skipped.IsCompletedSuccessfully && await skipped.WaitAsync(SafetyTimeout) is null,
                    "A busy slot must supply a completed default result instead of the old pending task.");
                Require(ReferenceEquals(slot.Pending, firstTask) && factoryCalls == 1,
                    "Skipped attempts must neither invoke factories nor replace the active provider.");
            }

            second = CaretParallelQuery.Start(None(), skipped,
                Task.FromResult<CaretPosition?>(Position(2, sample: 2)), None(), None(), deadline: nextDeadline.Task);
            Require(await second.Result.WaitAsync(SafetyTimeout) == Position(2, sample: 2),
                "A busy UIA slot must not block a fresh result from another provider.");
            oldProvider.SetResult(Position(1, sample: 1));
            await first.Drain.WaitAsync(SafetyTimeout);
            await second.Drain.WaitAsync(SafetyTimeout);
            Require(slot.Pending.IsCompleted && factoryCalls == 1,
                "Finishing a provider must not launch any skipped factory as backlog.");

            Require(slot.TryStart(() => { factoryCalls++; return freshProvider.Task; }, out var freshTask),
                "The next query must be able to reuse an idle slot.");
            Require(factoryCalls == 2 && ReferenceEquals(freshTask, freshProvider.Task) &&
                ReferenceEquals(slot.Pending, freshTask) && !freshTask.IsCompleted,
                "Resuming a slot must start fresh work, never return its prior completed result.");
            third = CaretParallelQuery.Start(None(), freshTask, None(), None(), None(), deadline: nextDeadline.Task);
            Require(!third.Result.IsCompleted, "A previous query's position must not satisfy the new query.");
            freshProvider.SetResult(Position(1, sample: 3));
            Require(await third.Result.WaitAsync(SafetyTimeout) == Position(1, sample: 3),
                "The resumed provider must report only the new sample.");
            await third.Drain.WaitAsync(SafetyTimeout);
            Require(await first.Result.WaitAsync(SafetyTimeout) is null &&
                await second.Result.WaitAsync(SafetyTimeout) == Position(2, sample: 2),
                "Later UIA completions must not rewrite either earlier query.");
        }
        finally
        {
            oldProvider.TrySetResult(null);
            freshProvider.TrySetResult(null);
            firstDeadline.TrySetResult();
            nextDeadline.TrySetResult();
            await ObserveForCleanupAsync(first.Drain);
            if (second is not null) await ObserveForCleanupAsync(second.Drain);
            if (third is not null) await ObserveForCleanupAsync(third.Drain);
        }
    }

    private static async Task SlotRecoversAfterFailureAsync()
    {
        CaretProviderSlot<int> slot = new();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException providerError = new("slot provider failed");
        int calls = 0;
        Require(slot.TryStart(() => pending.Task, out var first), "An empty value-type slot must admit work.");
        try
        {
            Require(!slot.TryStart(() => { calls++; return Task.FromResult(9); }, out var skipped) &&
                skipped.IsCompletedSuccessfully && await skipped.WaitAsync(SafetyTimeout) == 0 && calls == 0,
                "Busy generic slots must return default(T) without executing the supplied factory.");
            pending.SetException(providerError);
            await ExpectErrorAsync(first, providerError);
            Require(slot.Pending.IsCompleted, "A faulted provider must not leave a slot permanently busy.");
            Require(slot.TryStart(() => { calls++; return Task.FromResult(27); }, out var resumed) &&
                await resumed.WaitAsync(SafetyTimeout) == 27 && calls == 1,
                "A failed provider must permit a new query without replaying skipped work.");

            InvalidOperationException factoryError = new("synchronous factory failure");
            Require(slot.TryStart(() => throw factoryError, out var failed),
                "A synchronous factory failure must belong to the admitted task.");
            await ExpectErrorAsync(failed, factoryError);
            Require(slot.TryStart(() => Task.FromResult(31), out var afterFailure) &&
                await afterFailure.WaitAsync(SafetyTimeout) == 31,
                "A synchronous factory failure must also leave the slot reusable.");
        }
        finally
        {
            pending.TrySetResult(0);
            await ObserveForCleanupAsync(first);
        }
    }

    private static async Task LateFaultsAfterExpiryAsync()
    {
        var candidates = Candidates();
        TaskCompletionSource deadline = Signal(), cleanup = Signal();
        candidates[2].SetResult(Position(2));
        CaretQueryOperation operation = Start(candidates, deadline.Task, cleanup.Task);
        try
        {
            deadline.SetResult();
            Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(2),
                "Expiry must deliver the available position without waiting for higher providers.");
            candidates[0].SetException(new InvalidOperationException("late Java failure"));
            candidates[1].SetException(new ObjectDisposedException("late UIA failure"));
            candidates[3].SetCanceled();
            Require(!operation.Drain.IsCompleted,
                "Late higher-provider errors must not abandon the last native task.");
            candidates[4].SetException(new ArgumentException("late fallback failure"));
            Require(!operation.Drain.IsCompleted,
                "Late failures must still wait for ownership cleanup.");
            cleanup.SetResult();
            await operation.Drain.WaitAsync(SafetyTimeout);
            Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(2),
                "Even a higher-provider error after expiry must not fault a delivered result.");
        }
        finally
        {
            Finish(candidates);
            deadline.TrySetResult();
            cleanup.TrySetResult();
            await ObserveForCleanupAsync(operation.Drain);
        }
    }

    private static async Task LosingErrorsAndCleanupAsync(bool cleanupFails)
    {
        var candidates = Candidates();
        TaskCompletionSource deadline = Signal(), cleanup = Signal();
        InvalidOperationException cleanupError = new("bounded operation cleanup");
        CaretQueryOperation operation = Start(candidates, deadline.Task, cleanup.Task);
        try
        {
            candidates[1].SetException(new ArgumentException("losing UIA provider"));
            candidates[0].SetResult(Position(0));
            Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(0),
                "A losing error must not poison an early higher-priority success.");
            candidates[2].SetCanceled();
            candidates[3].SetException(new InvalidOperationException("losing native provider"));
            Require(!operation.Drain.IsCompleted,
                "Observing a provider fault or cancellation must not skip remaining native work.");
            candidates[4].SetException(new ObjectDisposedException("losing text provider"));
            Require(!operation.Drain.IsCompleted, "Losing errors must not bypass ownership cleanup.");
            if (cleanupFails) cleanup.SetException(cleanupError);
            else cleanup.SetResult();

            // Drain must consume speculative faults/cancellation, but preserve a
            // cleanup fault. No GC or UnobservedTaskException timing is involved.
            if (cleanupFails) await ExpectErrorAsync(operation.Drain, cleanupError);
            else await operation.Drain.WaitAsync(SafetyTimeout);
            Require(await operation.Result.WaitAsync(SafetyTimeout) == Position(0),
                "Faults discovered while draining must not change the delivered result.");
        }
        finally
        {
            Finish(candidates);
            deadline.TrySetResult();
            cleanup.TrySetResult();
            await ObserveForCleanupAsync(operation.Drain);
        }
    }

    private static async Task ParallelRequestsAndDisposalAsync()
    {
        TaskCompletionSource[] entered = [Signal(), Signal(), Signal()];
        var results = new[] { Candidate(), Candidate(), Candidate() };
        TaskCompletionSource[] drains = [Signal(), Signal(), Signal()];
        TaskCompletionSource releasing = Signal(), finishRelease = Signal();
        int admitted = 0, releases = 0;
        CaretExGetPos caret = new(
            () => throw new InvalidOperationException("Default query unexpectedly selected the sequential backend."),
            async () =>
            {
                Require(drains.All(drain => drain.Task.IsCompleted) && admitted == 3,
                    "Disposal must drain every accepted operation before releasing ownership.");
                Interlocked.Increment(ref releases);
                releasing.SetResult();
                await finishRelease.Task.WaitAsync(SafetyTimeout);
            },
            budget =>
            {
                int index = Interlocked.Increment(ref admitted) - 1;
                Require(index < 3, "An accepted request must start exactly one operation.");
                Require(budget == TimeSpan.FromMilliseconds(index == 0 ? 200 : 100),
                    "Queued parallel requests must receive 200ms once, then 100ms even while older drains remain pending.");
                if (index > 0)
                    Require(results[index - 1].Task.IsCompleted && !drains[index - 1].Task.IsCompleted,
                        "A new parallel request must run after the prior result while its drain is still pending.");
                entered[index].SetResult();
                return Task.FromResult(new CaretQueryOperation(results[index].Task, drains[index].Task));
            });
        Task? disposal = null;
        try
        {
            Task<CaretPosition?> first = caret.GetPositionAsync();
            await entered[0].Task.WaitAsync(SafetyTimeout);
            Task<CaretPosition?> second = caret.GetPositionAsync(CaretQueryMode.ParallelPriority);
            Task<CaretPosition?> third = caret.GetPositionAsync();
            disposal = caret.DisposeAsync().AsTask();
            Require(ReferenceEquals(disposal, caret.DisposeAsync().AsTask()),
                "Disposal calls must share a single completion.");
            ExpectRejected(caret);

            results[0].SetResult(Position(2, sample: 1));
            await entered[1].Task.WaitAsync(SafetyTimeout);
            Require(await first.WaitAsync(SafetyTimeout) == Position(2, sample: 1),
                "The first result must be returned without waiting for its drain.");
            results[1].SetResult(Position(3, sample: 2));
            await entered[2].Task.WaitAsync(SafetyTimeout);
            Require(await second.WaitAsync(SafetyTimeout) == Position(3, sample: 2),
                "The second parallel request must complete independently of the older drain.");
            results[2].SetResult(Position(1, sample: 3));
            drains[2].SetResult();
            Require(await third.WaitAsync(SafetyTimeout) == Position(1, sample: 3),
                "Later results must remain associated with their own requests.");
            Require(!disposal.IsCompleted && !releasing.Task.IsCompleted,
                "A completed newest drain must not hide older active drains.");
            drains[0].SetResult();
            Require(!disposal.IsCompleted, "Disposal must also wait for the middle operation's drain.");
            drains[1].SetResult();
            await releasing.Task.WaitAsync(SafetyTimeout);
            Require(!disposal.IsCompleted && Volatile.Read(ref releases) == 1,
                "Disposal must await ownership release exactly once.");
            finishRelease.SetResult();
            await disposal.WaitAsync(SafetyTimeout);
            await caret.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
            Require(releases == 1, "Repeated disposal must not release ownership again.");
        }
        finally
        {
            Finish(results);
            foreach (var drain in drains) drain.TrySetResult();
            finishRelease.TrySetResult();
            await ObserveForCleanupAsync(disposal ?? caret.DisposeAsync().AsTask());
        }
    }

    private static async Task IndependentInstanceBudgetsAsync()
    {
        List<TimeSpan> firstBudgets = [], secondBudgets = [];
        CaretExGetPos Create(List<TimeSpan> budgets) => new(
            () => throw new InvalidOperationException("Expected the parallel backend."),
            () => ValueTask.CompletedTask,
            budget =>
            {
                budgets.Add(budget);
                return Task.FromResult(new CaretQueryOperation(None(), Task.CompletedTask));
            });
        CaretExGetPos first = Create(firstBudgets);
        CaretExGetPos? second = null;
        try
        {
            await first.GetPositionAsync().WaitAsync(SafetyTimeout);
            // Creating another instance must neither inherit nor reset the first one's budget.
            second = Create(secondBudgets);
            await second.GetPositionAsync(CaretQueryMode.ParallelPriority).WaitAsync(SafetyTimeout);
            await first.GetPositionAsync(CaretQueryMode.ParallelPriority).WaitAsync(SafetyTimeout);
            await second.GetPositionAsync().WaitAsync(SafetyTimeout);
            TimeSpan[] expected = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100)];
            Require(firstBudgets.SequenceEqual(expected) && secondBudgets.SequenceEqual(expected),
                "Each live instance must independently receive 200ms on its first parallel query and 100ms thereafter.");
        }
        finally
        {
            await ObserveForCleanupAsync(first.DisposeAsync().AsTask());
            if (second is not null) await ObserveForCleanupAsync(second.DisposeAsync().AsTask());
        }
    }

    private static async Task FirstAttemptConsumesBudgetAsync()
    {
        foreach (string outcome in new[] { "null", "synchronous", "asynchronous" })
        {
            List<TimeSpan> budgets = [];
            TaskCompletionSource entered = Signal();
            var pending = new TaskCompletionSource<CaretQueryOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
            InvalidOperationException error = new($"first parallel {outcome} failure");
            CaretExGetPos caret = new(
                () => throw new InvalidOperationException("Expected the parallel backend."),
                () => ValueTask.CompletedTask,
                budget =>
                {
                    budgets.Add(budget);
                    if (budgets.Count > 1)
                        return Task.FromResult(new CaretQueryOperation(
                            Task.FromResult<CaretPosition?>(Position(2, sample: budgets.Count)), Task.CompletedTask));
                    entered.SetResult();
                    return outcome switch
                    {
                        "null" => Task.FromResult(new CaretQueryOperation(None(), Task.CompletedTask)),
                        "synchronous" => throw error,
                        _ => pending.Task
                    };
                });
            try
            {
                Task<CaretPosition?> first = caret.GetPositionAsync();
                await entered.Task.WaitAsync(SafetyTimeout);
                Task<CaretPosition?> second = caret.GetPositionAsync(CaretQueryMode.ParallelPriority);
                Task<CaretPosition?> third = caret.GetPositionAsync();
                if (outcome == "asynchronous")
                {
                    Require(!first.IsCompleted && !second.IsCompleted && !third.IsCompleted && budgets.Count == 1,
                        "Queued requests must wait while the first parallel backend is still starting.");
                    pending.SetException(error);
                }
                if (outcome == "null")
                    Require(await first.WaitAsync(SafetyTimeout) is null, "An unavailable first query must remain null.");
                else
                    await ExpectErrorAsync(first, error);
                Require(await second.WaitAsync(SafetyTimeout) == Position(2, sample: 2) &&
                    await third.WaitAsync(SafetyTimeout) == Position(2, sample: 3),
                    "A null or failed first attempt must not abandon subsequent accepted queries.");
                Require(budgets.SequenceEqual([TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)]),
                    $"A {outcome} first parallel attempt must consume the 200ms budget before the backend completes.");
            }
            finally
            {
                pending.TrySetResult(new CaretQueryOperation(None(), Task.CompletedTask));
                await ObserveForCleanupAsync(caret.DisposeAsync().AsTask());
            }
        }
    }

    private static async Task SequentialWaitsForAllDrainsAsync()
    {
        TaskCompletionSource firstDrain = Signal(), secondDrain = Signal();
        TaskCompletionSource sequentialEntered = Signal();
        var sequentialResult = Candidate();
        int parallelCalls = 0, sequentialCalls = 0, releases = 0;
        CaretExGetPos caret = new(
            () =>
            {
                Require(firstDrain.Task.IsCompleted && secondDrain.Task.IsCompleted,
                    "An explicit sequential query must wait for every older parallel drain.");
                Interlocked.Increment(ref sequentialCalls);
                sequentialEntered.SetResult();
                return sequentialResult.Task;
            },
            () => { Interlocked.Increment(ref releases); return ValueTask.CompletedTask; },
            _ =>
            {
                int index = Interlocked.Increment(ref parallelCalls);
                Require(index <= 2, "The explicit sequential mode must not use the parallel backend.");
                return Task.FromResult(new CaretQueryOperation(
                    Task.FromResult<CaretPosition?>(Position(index)),
                    index == 1 ? firstDrain.Task : secondDrain.Task));
            });
        try
        {
            await caret.GetPositionAsync().WaitAsync(SafetyTimeout);
            await caret.GetPositionAsync().WaitAsync(SafetyTimeout);
            Task<CaretPosition?> sequential = caret.GetPositionAsync(CaretQueryMode.Sequential);
            Task disposal = caret.DisposeAsync().AsTask();
            Require(!sequential.IsCompleted && !disposal.IsCompleted,
                "Sequential work and disposal must stay pending while previous providers are occupied.");
            secondDrain.SetResult();
            Require(!disposal.IsCompleted, "Finishing the latest batch cannot bypass an older drain.");
            firstDrain.SetResult();
            await sequentialEntered.Task.WaitAsync(SafetyTimeout);
            Require(!disposal.IsCompleted && releases == 0,
                "Disposal must still wait for the admitted sequential result.");
            sequentialResult.SetResult(Position(4, sample: 4));
            Require(await sequential.WaitAsync(SafetyTimeout) == Position(4, sample: 4),
                "A sequential request must preserve its own result after draining.");
            await disposal.WaitAsync(SafetyTimeout);
            Require(sequentialCalls == 1 && parallelCalls == 2 && releases == 1,
                "Switching modes must neither duplicate work nor skip release.");
        }
        finally
        {
            firstDrain.TrySetResult();
            secondDrain.TrySetResult();
            sequentialResult.TrySetResult(null);
            await ObserveForCleanupAsync(caret.DisposeAsync().AsTask());
        }
    }

    private static async Task CleanupFaultSurvivesLaterQueriesAsync(bool faultAlreadyCompleted)
    {
        TaskCompletionSource oldCleanup = Signal();
        InvalidOperationException cleanupError = new("older operation cleanup failed");
        int queries = 0, releases = 0;
        if (faultAlreadyCompleted) oldCleanup.SetException(cleanupError);
        CaretExGetPos caret = new(
            () => throw new InvalidOperationException("Expected the default parallel backend."),
            () => { Interlocked.Increment(ref releases); return ValueTask.CompletedTask; },
            _ =>
            {
                int index = Interlocked.Increment(ref queries);
                return Task.FromResult(new CaretQueryOperation(
                    Task.FromResult<CaretPosition?>(Position(2, sample: index)),
                    index == 1 ? oldCleanup.Task : Task.CompletedTask));
            });
        try
        {
            for (int index = 1; index <= 4; index++)
                Require(await caret.GetPositionAsync().WaitAsync(SafetyTimeout) == Position(2, sample: index),
                    "Older pending or failed cleanup must not abandon later parallel requests.");

            Task disposal = caret.DisposeAsync().AsTask();
            if (!faultAlreadyCompleted)
            {
                Require(!disposal.IsCompleted, "Disposal must retain the first drain after later queries finish.");
                oldCleanup.SetException(cleanupError);
            }
            await ExpectErrorAsync(disposal, cleanupError);
            Require(queries == 4 && releases == 1, "A drain fault must not prevent once-only ownership release.");
            Task repeated = caret.DisposeAsync().AsTask();
            Require(ReferenceEquals(disposal, repeated), "Failed disposal must retain its completion task.");
            await ExpectErrorAsync(repeated, cleanupError);
            Require(releases == 1, "A remembered cleanup fault must not trigger repeated release.");
            ExpectRejected(caret);
        }
        finally
        {
            oldCleanup.TrySetResult();
            await ObserveForCleanupAsync(caret.DisposeAsync().AsTask());
        }
    }

    private static async Task DefaultWithoutParallelBackendAsync()
    {
        int queries = 0, releases = 0;
        CaretExGetPos caret = new(
            () => Task.FromResult<CaretPosition?>(++queries == 2 ? null : Position(2, sample: queries)),
            () => { releases++; return ValueTask.CompletedTask; });
        try
        {
            Require(await caret.GetPositionAsync().WaitAsync(SafetyTimeout) == Position(2, sample: 1),
                "The internal seam without a parallel delegate must still support the new default mode.");
            Require(await caret.GetPositionAsync().WaitAsync(SafetyTimeout) is null,
                "The fallback seam must not reuse a preceding position for an unavailable query.");
            Require(await caret.GetPositionAsync(CaretQueryMode.ParallelPriority).WaitAsync(SafetyTimeout) ==
                Position(2, sample: 3), "Explicit parallel mode must also forward to the supplied fake query.");
            await caret.DisposeAsync().AsTask().WaitAsync(SafetyTimeout);
            Require(queries == 3 && releases == 1, "Fallback query and cleanup delegates must run exactly as admitted.");
        }
        finally
        {
            await ObserveForCleanupAsync(caret.DisposeAsync().AsTask());
        }
    }

    private static CaretQueryOperation Start(
        TaskCompletionSource<CaretPosition?>[] candidates, Task? deadline, Task? cleanup = null) =>
        CaretParallelQuery.Start(candidates[0].Task, candidates[1].Task, candidates[2].Task,
            candidates[3].Task, candidates[4].Task, cleanup: cleanup, deadline: deadline);

    private static TaskCompletionSource<CaretPosition?>[] Candidates() =>
        Providers.Select(_ => Candidate()).ToArray();

    private static TaskCompletionSource<CaretPosition?> Candidate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<CaretPosition?> None() => Task.FromResult<CaretPosition?>(null);

    private static CaretPosition Position(int provider, int sample = 1) =>
        new(100 * sample + provider, 200 * sample + provider, 1, 12, Providers[provider]);

    private static void Finish(TaskCompletionSource<CaretPosition?>[] candidates)
    {
        foreach (var candidate in candidates) candidate.TrySetResult(null);
    }

    private static async Task ExpectErrorAsync(Task task, Exception expected)
    {
        try { await task.WaitAsync(SafetyTimeout); }
        catch (Exception actual) when (ReferenceEquals(actual, expected)) { return; }
        throw new InvalidOperationException("The original cleanup/provider exception was not preserved.");
    }

    private static async Task ObserveForCleanupAsync(Task task)
    {
        try { await task.WaitAsync(SafetyTimeout); }
        catch (Exception) when (task.IsFaulted || task.IsCanceled) { }
    }

    private static void ExpectRejected(CaretExGetPos caret)
    {
        foreach (CaretQueryMode mode in new[] { CaretQueryMode.Sequential, CaretQueryMode.ParallelPriority })
        {
            try { _ = caret.GetPositionAsync(mode); }
            catch (ObjectDisposedException) { continue; }
            throw new InvalidOperationException("A disposing instance admitted another query.");
        }
    }
}
