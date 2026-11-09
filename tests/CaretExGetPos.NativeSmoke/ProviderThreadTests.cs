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