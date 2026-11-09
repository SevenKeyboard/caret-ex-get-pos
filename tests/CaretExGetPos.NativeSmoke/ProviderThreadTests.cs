using System.ComponentModel;
using System.Runtime.InteropServices;
using SevenKeyboard;
using SevenKeyboard.UiAutomation;

internal static partial class ProviderThreadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync()
    {
        // Borrow this thread's desktop; never open/switch the input desktop.
        nint desktop = CaretNative.GetThreadDesktop(CaretNative.GetCurrentThreadId());
        Require(desktop != 0, "The test thread must have a desktop.");

        await IndependentWorkersAsync(desktop);
        foreach (bool initializeCom in new[] { false, true })
        {
            await FifoAndCommandFailureAsync(desktop, initializeCom);
            await DisposeDrainsAndJoinsAsync(desktop, initializeCom);
            await InvalidDesktopAsync(desktop, initializeCom);
        }
        await FourOwnersAsync(desktop);
        await SharedJavaAdmissionAsync();
        await SharedJavaShutdownAndReplacementAsync();
        Console.WriteLine("Provider worker concurrency/FIFO/failure/disposal checks passed (no input or foreground queries).");
    }

    private static async Task IndependentWorkersAsync(nint desktop)
    {
        CaretProviderThread? msaa = null, gui = null;
        Owner[] owners = [];
        try
        {
            msaa = await CaretProviderThread.CreateAsync("NativeSmoke MSAA", desktop, true).WaitAsync(Timeout);
            gui = await CaretProviderThread.CreateAsync("NativeSmoke Win32", desktop, false).WaitAsync(Timeout);
            owners = await ConcurrentOwnersAsync(desktop, action => msaa.RunAsync(action), action => gui.RunAsync(action));
            var apartment = await msaa.RunAsync(() =>
            {
                int hr = CoGetApartmentType(out int type, out int qualifier);
                return (hr, type, qualifier);
            }).WaitAsync(Timeout);
            // MTA/NONE distinguishes explicit COM initialization from an implicit MTA.
            // https://learn.microsoft.com/windows/win32/api/objidlbase/ne-objidlbase-apttypequalifier
            Require(apartment.hr == 0 && apartment.type == 1 && apartment.qualifier == 0,
                $"MSAA must initialize an MTA (hr=0x{apartment.hr:X8}, type={apartment.type}, qualifier={apartment.qualifier}).");
        }
        finally { await Task.WhenAll(DisposeAsync(msaa), DisposeAsync(gui)); }
        Require(owners.All(owner => !owner.Thread.IsAlive), "Both independently owned provider threads must be joined.");
    }

    private static async Task<Owner[]> ConcurrentOwnersAsync(
        nint desktop, params Func<Func<Owner>, Task<Owner>>[] queues)
    {
        TaskCompletionSource release = Signal();
        TaskCompletionSource<Owner>[] entered = queues.Select(_ =>
            new TaskCompletionSource<Owner>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var commands = new List<Task<Owner>>();
        try
        {
            for (int index = 0; index < queues.Length; index++)
            {
                int slot = index;
                commands.Add(queues[slot](() =>
                {
                    Owner owner = CaptureOwner();
                    entered[slot].SetResult(owner);
                    WaitForRelease(release, $"owner {slot}");
                    return owner;
                }));
            }

            // No owner is released until every callback has entered. A shared owner
            // or a global serialization lock cannot satisfy this barrier.
            Owner[] owners = await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(Timeout);
            Require(commands.All(command => !command.IsCompleted), "Every owner must still be held at the common barrier.");
            Require(owners.Select(owner => owner.NativeId).Distinct().Count() == queues.Length,
                "Concurrent providers must have distinct native thread IDs.");
            Require(owners.Select(owner => owner.Thread.ManagedThreadId).Distinct().Count() == queues.Length,
                "Concurrent providers must have distinct managed owner threads.");
            foreach (Owner owner in owners)
            {
                Require(owner.NativeId != 0 && owner.Desktop == desktop, "Each owner must use the requested desktop.");
                Require(owner.Thread.IsBackground && !owner.Thread.IsThreadPoolThread,
                    "Each provider must own a dedicated background thread.");
            }

            release.TrySetResult();
            await Task.WhenAll(commands).WaitAsync(Timeout);
            for (int repeat = 0; repeat < 4; repeat++)
            {
                Owner[] reused = await Task.WhenAll(queues.Select(queue => queue(CaptureOwner))).WaitAsync(Timeout);
                for (int index = 0; index < owners.Length; index++)
                    Require(ReferenceEquals(reused[index].Thread, owners[index].Thread) &&
                        reused[index].NativeId == owners[index].NativeId && reused[index].Desktop == desktop,
                        $"Provider {index} must reuse its fixed owner and desktop.");
            }
            return owners;
        }
        finally { release.TrySetResult(); }
    }

    private static async Task FifoAndCommandFailureAsync(nint desktop, bool initializeCom)
    {
        CaretProviderThread worker = await CaretProviderThread.CreateAsync(
            $"NativeSmoke FIFO COM={initializeCom}", desktop, initializeCom).WaitAsync(Timeout);
        TaskCompletionSource entered = Signal(), release = Signal();
        var order = new List<int>();
        var expected = new InvalidOperationException("Intentional provider command failure.");
        try
        {
            Task<int> first = worker.RunAsync(() =>
            {
                entered.SetResult();
                WaitForRelease(release, "FIFO first command");
                order.Add(0);
                return 0;
            });
            await entered.Task.WaitAsync(Timeout);

            Task<int>[] queued = Enumerable.Range(1, 8).Select(index => worker.RunAsync(() =>
            {
                order.Add(index);
                if (index == 3) throw expected;
                return index;
            })).ToArray();
            Require(queued.All(command => !command.IsCompleted), "Queued work must wait behind the gated first command.");
            release.TrySetResult();

            Require(await first.WaitAsync(Timeout) == 0, "The first command must keep its result.");
            for (int index = 1; index <= queued.Length; index++)
            {
                if (index == 3)
                {
                    await ExpectErrorAsync(queued[index - 1], expected);
                    Require(queued[index - 1].IsFaulted, "An action exception must fault its own command.");
                }
                else Require(await queued[index - 1].WaitAsync(Timeout) == index, "FIFO results must reach their own callers.");
            }
            Require(order.SequenceEqual(Enumerable.Range(0, 9)), "Accepted commands, including the failure, must execute in FIFO order.");
            Require(await worker.RunAsync(() => 42).WaitAsync(Timeout) == 42,
                "A failed command must not poison later submissions.");
        }
        finally
        {
            release.TrySetResult();
            await DisposeAsync(worker);
        }
    }

    private static async Task DisposeDrainsAndJoinsAsync(nint desktop, bool initializeCom)
    {
        CaretProviderThread worker = await CaretProviderThread.CreateAsync(
            $"NativeSmoke dispose COM={initializeCom}", desktop, initializeCom).WaitAsync(Timeout);
        TaskCompletionSource<Thread> firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource queuedEntered = Signal(), releaseFirst = Signal(), releaseQueued = Signal();
        int rejectedExecutions = 0;
        try
        {
            Task<int> first = worker.RunAsync(() =>
            {
                firstEntered.SetResult(Thread.CurrentThread);
                WaitForRelease(releaseFirst, "in-flight command");
                return 17;
            });
            Thread owner = await firstEntered.Task.WaitAsync(Timeout);
            Task<int> queued = worker.RunAsync(() =>
            {
                queuedEntered.SetResult();
                WaitForRelease(releaseQueued, "queued command");
                return 23;
            });
            Task<int> tail = worker.RunAsync(() => 42);

            Task closing = worker.DisposeAsync().AsTask();
            Task closingAgain = worker.DisposeAsync().AsTask();
            Require(ReferenceEquals(closing, closingAgain), "Repeated disposal must share the same completion.");
            Require(!closing.IsCompleted && !first.IsCompleted && !queued.IsCompleted && !tail.IsCompleted,
                "Disposal must wait for the in-flight command and all accepted queued work.");
            await ExpectDisposedAsync(() => worker.RunAsync(() => Interlocked.Increment(ref rejectedExecutions)));

            releaseFirst.TrySetResult();
            await queuedEntered.Task.WaitAsync(Timeout);
            Require(await first.WaitAsync(Timeout) == 17, "Closing admission must preserve the in-flight result.");
            Require(!closing.IsCompleted && !closingAgain.IsCompleted && !queued.IsCompleted && !tail.IsCompleted,
                "Disposal must also wait while a previously queued command is in flight.");
            Require(ReferenceEquals(closing, worker.DisposeAsync().AsTask()), "Disposal completion must stay stable during drain.");
            await ExpectDisposedAsync(() => worker.RunAsync(() => Interlocked.Increment(ref rejectedExecutions)));

            releaseQueued.TrySetResult();
            await Task.WhenAll(closing, closingAgain).WaitAsync(Timeout);
            Require(queued.IsCompletedSuccessfully && tail.IsCompletedSuccessfully,
                "Disposal must complete every accepted command before returning.");
            Require(await queued == 23 && await tail == 42, "Drain must preserve queued command results.");
            Require(!owner.IsAlive, "Disposal must join the owner thread before completing.");
            Require(ReferenceEquals(closing, worker.DisposeAsync().AsTask()), "Completed disposal must remain idempotent.");
            await worker.DisposeAsync().AsTask().WaitAsync(Timeout);
            await ExpectDisposedAsync(() => worker.RunAsync(() => Interlocked.Increment(ref rejectedExecutions)));
            Require(rejectedExecutions == 0, "Rejected submissions must never execute.");
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseQueued.TrySetResult();
            await DisposeAsync(worker);
        }
    }

    private static async Task InvalidDesktopAsync(nint desktop, bool initializeCom)
    {
        CaretProviderThread? unexpected = null;
        bool failed = false;
        try
        {
            // A nonzero invalid HDESK exercises worker startup failure, not just
            // factory argument validation. Do not create or close any desktop.
            unexpected = await CaretProviderThread.CreateAsync(
                "NativeSmoke invalid desktop", (nint)(-1), initializeCom).WaitAsync(Timeout);
        }
        catch (Win32Exception) { failed = true; }
        finally { await DisposeAsync(unexpected); }
        Require(failed, "Invalid desktop startup must finish with its native binding error.");

        CaretProviderThread recovered = await CaretProviderThread.CreateAsync(
            "NativeSmoke after startup failure", desktop, initializeCom).WaitAsync(Timeout);
        try
        {
            Require(await recovered.RunAsync(() => CaretNative.GetThreadDesktop(CaretNative.GetCurrentThreadId()))
                .WaitAsync(Timeout) == desktop, "A failed startup must not poison the next valid worker.");
        }
        finally { await DisposeAsync(recovered); }
    }

    private static async Task FourOwnersAsync(nint desktop)
    {
        CaretParallelNativeThreads? pair = null;
        CaretNativeThread? native = null;
        UiAutomationClient? uia = null;
        Owner[] owners = [];
        try
        {
            pair = await CaretParallelNativeThreads.CreateAsync(desktop).WaitAsync(Timeout);
            string missingJava = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");
            native = await CaretNativeThread.CreateAsync(missingJava).WaitAsync(Timeout);
            uia = await UiAutomationClient.CreateAsync(activateScreenReader: false).WaitAsync(Timeout);
            Require(!await native.RunAsync(() => native.JavaAvailable).WaitAsync(Timeout),
                "The four-owner fixture must not load Java.");

            // The existing native runtime owns hidden nonactivating lifecycle HWNDs.
            // UIA callbacks intentionally ignore the query: no elements, foreground,
            // accessibility provider queries, screen-reader activation, or input.
            owners = await ConcurrentOwnersAsync(desktop,
                action => native.RunAsync(action),
                action => uia.RunAsync(_ => action()),
                action => pair.Msaa.RunAsync(action),
                action => pair.Gui.RunAsync(action));
        }
        finally { await Task.WhenAll(DisposeAsync(pair), DisposeAsync(native), DisposeAsync(uia)); }
        Require(owners.All(owner => !owner.Thread.IsAlive), "Disposing the pair and existing clients must join all four owners.");
    }

    private static async Task SharedJavaAdmissionAsync()
    {
        string missingJava = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");
        CaretNativeThread? first = null, second = null;
        var releases = new List<TaskCompletionSource>();
        TaskCompletionSource ReleaseSignal()
        {
            TaskCompletionSource signal = Signal();
            releases.Add(signal);
            return signal;
        }
        try
        {
            first = await CaretNativeThread.CreateAsync(missingJava).WaitAsync(Timeout);
            second = await CaretNativeThread.CreateAsync(missingJava).WaitAsync(Timeout);
            CaretNativeThread a = first, b = second;
            Require(a.RuntimeId == b.RuntimeId, "Java admission must be tested across leases of the same owner.");
            uint owner = await a.RunAsync(CaretNative.GetCurrentThreadId).WaitAsync(Timeout);
            CaretPosition expected = new(11, 22, 1, 18, "JavaAccessBridge");
            int nativeCalls = 0, guards = 0;
            bool allowStart = true;
            CaretPosition? Query(nint window)
            {
                Require(CaretNative.GetCurrentThreadId() == owner && window == 123,
                    "The Java query body must run on its owner with the requested HWND.");
                Interlocked.Increment(ref nativeCalls);
                return expected;
            }
            await a.RunAsync(() =>
            {
                Require(!a.JavaAvailable && !b.JavaAvailable, "This fixture must not load or invoke JAB.");
                a.SetJavaQueryForTests(Query);
                b.SetJavaQueryForTests(Query);
                return 0;
            }).WaitAsync(Timeout);

            // Hold an unrelated command so all admissions race while Java is queued.
            TaskCompletionSource held = Signal(), releaseOwner = ReleaseSignal();
            Task<int> blocker = a.RunAsync(() =>
            {
                held.SetResult();
                WaitForRelease(releaseOwner, "shared Java admission queue");
                return 0;
            });
            await held.Task.WaitAsync(Timeout);
            var submissions = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            {
                CaretNativeThread lease = index % 2 == 0 ? a : b;
                return (Index: index, Query: lease.TryRunJavaAsync(123, () =>
                {
                    Require(CaretNative.GetCurrentThreadId() == owner,
                        "The start guard must be evaluated on the owner, not on the submitting thread.");
                    Interlocked.Increment(ref guards);
                    return Volatile.Read(ref allowStart);
                }));
            }))).WaitAsync(Timeout);
            Require(submissions.Count(item => !item.Query.IsCompleted) == 1,
                "Exactly one shared Java admission may wait in the queue across all leases.");
            Task<CaretPosition?> admitted = submissions.Single(item => !item.Query.IsCompleted).Query;
            foreach (var item in submissions.Where(item => !ReferenceEquals(item.Query, admitted)))
                Require(item.Query.IsCompletedSuccessfully && await item.Query is null,
                    "Busy shared Java admission must return completed null without adding work.");
            Require(guards == 0 && nativeCalls == 0, "Queued and skipped requests must not evaluate guards early.");
            Volatile.Write(ref allowStart, false);
            releaseOwner.TrySetResult();
            await blocker.WaitAsync(Timeout);
            Require(await admitted.WaitAsync(Timeout) is null && guards == 1 && nativeCalls == 0,
                "A request that expired in the queue must check once on the owner and skip the native call.");
            Require(await b.TryRunJavaAsync(123, () => true).WaitAsync(Timeout) == expected && nativeCalls == 1,
                "Skipping an expired request must release shared admission for the next query.");

            // An admitted native call cannot be cancelled by expiry or disposal.
            TaskCompletionSource running = Signal(), finishJava = ReleaseSignal();
            await a.RunAsync(() =>
            {
                a.SetJavaQueryForTests(window =>
                {
                    running.SetResult();
                    WaitForRelease(finishJava, "shared Java native call");
                    return Query(window);
                });
                return 0;
            }).WaitAsync(Timeout);
            Task<CaretPosition?> inFlight = a.TryRunJavaAsync(123, () => true);
            await running.Task.WaitAsync(Timeout);
            Task<CaretPosition?> busy = b.TryRunJavaAsync(123, () => throw new InvalidOperationException("Busy guard ran."));
            Require(busy.IsCompletedSuccessfully && await busy is null, "A running Java call must block other leases' admission.");
            Task<CaretPosition?> sequential = b.RunAsync(() => b.TryGetJava(123));
            Require(!sequential.IsCompleted, "Explicit sequential Java queries must retain their normal queue semantics.");
            finishJava.TrySetResult();
            Require(await inFlight.WaitAsync(Timeout) == expected && await sequential.WaitAsync(Timeout) == expected,
                "Both accepted parallel and explicit sequential calls must finish normally.");

            // Sequential Java calls also advertise busy while their body executes.
            TaskCompletionSource sequentialRunning = Signal(), finishSequential = ReleaseSignal();
            await a.RunAsync(() =>
            {
                a.SetJavaQueryForTests(window =>
                {
                    sequentialRunning.SetResult();
                    WaitForRelease(finishSequential, "sequential Java native call");
                    return Query(window);
                });
                return 0;
            }).WaitAsync(Timeout);
            sequential = a.RunAsync(() => a.TryGetJava(123));
            await sequentialRunning.Task.WaitAsync(Timeout);
            busy = b.TryRunJavaAsync(123, () => throw new InvalidOperationException("Sequential busy guard ran."));
            Require(busy.IsCompletedSuccessfully && await busy is null,
                "A running explicit sequential Java call must make speculative admission skip across leases.");
            finishSequential.TrySetResult();
            Require(await sequential.WaitAsync(Timeout) == expected, "Skipping speculative work must not interrupt sequential Java.");

            var guardError = new InvalidOperationException("Intentional Java guard failure.");
            await ExpectErrorAsync(b.TryRunJavaAsync(123, () => throw guardError), guardError);
            Require(await b.TryRunJavaAsync(123, () => true).WaitAsync(Timeout) == expected,
                "A throwing guard must not leave Java admission busy.");
            var queryError = new InvalidOperationException("Intentional Java query failure.");
            await a.RunAsync(() => { a.SetJavaQueryForTests(_ => throw queryError); return 0; }).WaitAsync(Timeout);
            await ExpectErrorAsync(a.TryRunJavaAsync(123, () => true), queryError);
            Require(await b.TryRunJavaAsync(123, () => true).WaitAsync(Timeout) == expected,
                "A failed native query must release both admission and the running flag.");
            await ExpectErrorAsync(a.RunAsync(() => a.TryGetJava(123)), queryError);
            Require(await b.TryRunJavaAsync(123, () => true).WaitAsync(Timeout) == expected,
                "An explicit sequential failure must also release the running flag.");

            TaskCompletionSource closingEntered = Signal(), finishClosing = ReleaseSignal();
            await a.RunAsync(() =>
            {
                a.SetJavaQueryForTests(window =>
                {
                    closingEntered.SetResult();
                    WaitForRelease(finishClosing, "closing Java native call");
                    return Query(window);
                });
                return 0;
            }).WaitAsync(Timeout);
            Task<CaretPosition?> closingQuery = a.TryRunJavaAsync(123, () => true);
            await closingEntered.Task.WaitAsync(Timeout);
            Task closing = a.DisposeAsync().AsTask();
            Require(!closing.IsCompleted && !closingQuery.IsCompleted, "Disposal must wait for the accepted Java call.");
            await ExpectDisposedAsync(() => a.TryRunJavaAsync(123, () => true));
            busy = b.TryRunJavaAsync(123, () => throw new InvalidOperationException("Closing busy guard ran."));
            Require(busy.IsCompletedSuccessfully && await busy is null, "Disposal must not release Java admission prematurely.");
            finishClosing.TrySetResult();
            Require(await closingQuery.WaitAsync(Timeout) == expected, "Disposal must preserve the accepted result.");
            await closing.WaitAsync(Timeout);
            Require(await b.TryRunJavaAsync(123, () => true).WaitAsync(Timeout) == expected,
                "The surviving lease must be able to query after another lease disposes.");
            Console.WriteLine("Shared Java admission/expiry/sequential/disposal checks passed (fake query; no JAB).");
        }
        finally
        {
            foreach (var signal in releases) signal.TrySetResult();
            await Task.WhenAll(DisposeAsync(first), DisposeAsync(second));
        }
    }

    private static async Task SharedJavaShutdownAndReplacementAsync()
    {
        string missingJava = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");
        TaskCompletionSource javaEntered = Signal(), releaseJava = Signal();
        TaskCompletionSource queuedEntered = Signal(), releaseQueued = Signal();
        var creations = new List<Task<CaretNativeThread>>();
        Task<CaretNativeThread> CreateLease()
        {
            Task<CaretNativeThread> creating = CaretNativeThread.CreateAsync(missingJava);
            creations.Add(creating);
            return creating;
        }
        static async Task DisposeCreatedAsync(Task<CaretNativeThread> creating)
        {
            CaretNativeThread lease;
            try { lease = await creating.ConfigureAwait(false); }
            catch { return; } // Failed creation owns its cleanup.
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        try
        {
            CaretNativeThread first = await CreateLease().WaitAsync(Timeout);
            CaretNativeThread second = await CreateLease().WaitAsync(Timeout);
            Require(first.RuntimeId == second.RuntimeId, "The closing Java fixture must have two leases of one runtime.");
            CaretPosition expected = new(11, 22, 1, 18, "JavaAccessBridge");
            Thread oldOwner = await first.RunAsync(() =>
            {
                Thread owner = Thread.CurrentThread;
                Require(!first.JavaAvailable && !second.JavaAvailable, "The closing fixture must not load JAB.");
                first.SetJavaQueryForTests(window =>
                {
                    Require(ReferenceEquals(Thread.CurrentThread, owner) && window == 123,
                        "The blocked Java call must run on the old owner with the requested HWND.");
                    javaEntered.SetResult();
                    WaitForRelease(releaseJava, "Java shutdown call");
                    return expected;
                });
                return owner;
            }).WaitAsync(Timeout);

            Task<CaretPosition?> accepted = first.TryRunJavaAsync(123, () => true);
            await javaEntered.Task.WaitAsync(Timeout);
            Task<CaretPosition?> busy = second.TryRunJavaAsync(123,
                () => throw new InvalidOperationException("Closing peer's busy guard ran."));
            Require(busy.IsCompletedSuccessfully && await busy is null,
                "The blocked Java call must hold admission across both leases.");
            Task<int> queued = second.RunAsync(() =>
            {
                Require(ReferenceEquals(Thread.CurrentThread, oldOwner), "Accepted queued work must stay on the old owner.");
                queuedEntered.SetResult();
                WaitForRelease(releaseQueued, "Java shutdown drain");
                return 17;
            });
            Task<int> tail = first.RunAsync(() => 23);

            // DisposeAsync queues detachment and synchronously marks the final
            // reference Closing. CreateLease calls CreateAsync directly, reaching
            // its old-thread exit await before returning this pending task.
            Task firstClosing = first.DisposeAsync().AsTask();
            Task lastClosing = second.DisposeAsync().AsTask();
            Task oldExit = first.WaitForExitAsync();
            Task<CaretNativeThread> creating = CreateLease();
            Require(!accepted.IsCompleted && !queued.IsCompleted && !tail.IsCompleted,
                "All accepted work must remain held behind the Java barrier.");
            Require(!firstClosing.IsCompleted && !lastClosing.IsCompleted && !oldExit.IsCompleted && !creating.IsCompleted,
                "Neither lease cleanup, old thread exit, nor replacement may finish during the blocked Java call.");
            Require(oldOwner.IsAlive, "The old owner must remain alive while Java is in flight.");
            await ExpectDisposedAsync(() => first.TryRunJavaAsync(123,
                () => throw new InvalidOperationException("Disposed first guard ran.")));
            await ExpectDisposedAsync(() => second.TryRunJavaAsync(123,
                () => throw new InvalidOperationException("Disposed second guard ran.")));

            releaseJava.TrySetResult();
            await queuedEntered.Task.WaitAsync(Timeout);
            Require(accepted.IsCompletedSuccessfully && await accepted == expected,
                "Final disposal must preserve the accepted Java result before draining queued work.");
            Require(!queued.IsCompleted && !tail.IsCompleted && !firstClosing.IsCompleted && !lastClosing.IsCompleted &&
                !oldExit.IsCompleted && !creating.IsCompleted && oldOwner.IsAlive,
                "Releasing Java must still leave cleanup and replacement waiting for accepted queued work.");

            releaseQueued.TrySetResult();
            CaretNativeThread replacement = await creating.WaitAsync(Timeout);
            // Creation and final disposal both await the join, but their async
            // continuations have no ordering. Check exit/work here, then await disposal.
            Require(oldExit.IsCompletedSuccessfully && !oldOwner.IsAlive,
                "Replacement creation must wait until the old owner has terminated.");
            Require(queued.IsCompletedSuccessfully && tail.IsCompletedSuccessfully && await queued == 17 && await tail == 23,
                "Replacement creation must follow successful draining of all accepted work.");
            await Task.WhenAll(firstClosing, lastClosing).WaitAsync(Timeout);
            Require(replacement.RuntimeId != first.RuntimeId, "Replacement must use a fresh native runtime.");

            CaretPosition replacementExpected = new(33, 44, 1, 18, "JavaAccessBridge");
            int replacementCalls = 0;
            Thread newOwner = await replacement.RunAsync(() =>
            {
                Thread owner = Thread.CurrentThread;
                Require(!ReferenceEquals(owner, oldOwner) && !replacement.JavaAvailable,
                    "Replacement must have a fresh owner without loading JAB.");
                replacement.SetJavaQueryForTests(window =>
                {
                    Require(ReferenceEquals(Thread.CurrentThread, owner) && window == 456,
                        "The replacement Java query must run on its new owner with the requested HWND.");
                    Interlocked.Increment(ref replacementCalls);
                    return replacementExpected;
                });
                return owner;
            }).WaitAsync(Timeout);
            Require(await replacement.TryRunJavaAsync(456, () => true).WaitAsync(Timeout) == replacementExpected &&
                replacementCalls == 1, "The fresh runtime must admit a Java query without stale pending or running state.");
            await DisposeAsync(replacement);
            Require(!newOwner.IsAlive, "Replacement disposal must also join its owner.");
            Console.WriteLine("Shared Java shutdown/drain/replacement checks passed (fake query; no JAB).");
        }
        finally
        {
            releaseJava.TrySetResult();
            releaseQueued.TrySetResult();
            // Track every creation, including one whose bounded wait failed, so a
            // late successful factory result still has a disposer awaiting it.
            await Task.WhenAll(creations.Select(DisposeCreatedAsync)).WaitAsync(Timeout);
        }
    }

    private sealed record Owner(Thread Thread, uint NativeId, nint Desktop);

    private static Owner CaptureOwner()
    {
        uint id = CaretNative.GetCurrentThreadId();
        return new(Thread.CurrentThread, id, CaretNative.GetThreadDesktop(id));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void WaitForRelease(TaskCompletionSource release, string stage)
    {
        if (!release.Task.Wait(Timeout)) throw new TimeoutException($"Timed out releasing {stage}.");
    }

    private static async Task DisposeAsync(IAsyncDisposable? owner)
    {
        if (owner is not null) await owner.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private static async Task ExpectErrorAsync(Task task, Exception expected)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception actual) when (ReferenceEquals(actual, expected)) { return; }
        throw new InvalidOperationException("The failing command must preserve its original exception.");
    }

    private static async Task ExpectDisposedAsync(Func<Task> submit)
    {
        // Both synchronous ObjectDisposedException and a returned faulted Task
        // satisfy rejection; accepted-but-never-executed work must time out/fail.
        try { await submit().WaitAsync(Timeout); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("A closing worker must reject new submissions.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoGetApartmentType(out int apartmentType, out int qualifier);
}