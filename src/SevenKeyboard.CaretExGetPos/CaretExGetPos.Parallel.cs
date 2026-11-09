using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SevenKeyboard.UiAutomation;

namespace SevenKeyboard;

public sealed partial class CaretExGetPos
{
    // A preference budget, not a native-call cancellation or a real-time deadline.
    internal static readonly TimeSpan FirstPreferenceBudget = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan PreferenceBudget = TimeSpan.FromMilliseconds(100);
    private bool _parallelQueryStarted;

    // Only the serial coordinator consumes this once, including null/failing queries.
    // Sequential queries do not consume it, and changing windows does not reset it.
    private TimeSpan TakeParallelPreferenceBudget()
    {
        TimeSpan budget = _parallelQueryStarted ? PreferenceBudget : FirstPreferenceBudget;
        _parallelQueryStarted = true;
        return budget;
    }
    private CaretParallelNativeThreads? _parallelNative;
    private readonly CaretProviderSlot<CaretPosition?> _javaSlot = new(), _msaaSlot = new(), _guiSlot = new();
    private readonly CaretProviderSlot<int> _uiaSlot = new();

    private async Task<CaretQueryOperation> StartParallelAsync(CaretNativeThread native, UiAutomationClient? uia, TimeSpan budget)
    {
        _parallelNative ??= await CaretParallelNativeThreads.CreateAsync(native.Desktop).ConfigureAwait(false);

        // Do not enqueue this sample behind a slow JAB call.
        CaretFocusContext context = CaretFocusContext.Capture();
        if (context.Window == 0)
            return new(Task.FromResult<CaretPosition?>(null), Task.CompletedTask);

        long started = Stopwatch.GetTimestamp();
        bool Eligible() => Stopwatch.GetElapsedTime(started) < budget && context.IsCurrent;
        Task deadline = Task.Delay(budget);
        _javaSlot.TryStart(() => native.TryRunJavaAsync(context.Window, Eligible), out var java);
        _msaaSlot.TryStart(() => _parallelNative.Msaa.RunAsync(() =>
            CaretParallelQuery.ReadIfCurrent(Eligible, () => CaretNative.TryGetMsaa(context.Window))), out var msaa);
        _guiSlot.TryStart(() => _parallelNative.Gui.RunAsync(() =>
            CaretParallelQuery.ReadIfCurrent(Eligible, CaretNative.TryGetGuiThreadInfo)), out var gui);

        var uia2 = new TaskCompletionSource<CaretPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uia1 = new TaskCompletionSource<CaretPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uiaSlot.TryStart(() => RunUiaAsync(uia, Eligible, uia2, uia1), out var uiaCleanup))
        {
            uia2.SetResult(null);
            uia1.SetResult(null);
        }
        CaretQueryOperation operation = CaretParallelQuery.Start(java, uia2.Task, msaa, gui, uia1.Task, uiaCleanup, deadline);
        return new(ValidateContextAsync(operation.Result, context), operation.Drain);
    }

    private static async Task<CaretPosition?> ValidateContextAsync(Task<CaretPosition?> result, CaretFocusContext context)
    {
        CaretPosition? position = await result.ConfigureAwait(false);
        return context.IsCurrent ? position : null;
    }

    private static async Task<int> RunUiaAsync(UiAutomationClient? uia, Func<bool> eligible,
        TaskCompletionSource<CaretPosition?> uia2, TaskCompletionSource<CaretPosition?> uia1)
    {
        try
        {
            if (uia is null) return 0;
            // Keep the focused element and both short text operations in one owner
            // batch. Temporary nodes/ranges are released even after the caller's budget.
            return await uia.RunAsync(query =>
            {
                if (!eligible()) return 0;
                using UiAutomationNode? element = query.GetFocusedElement();
                if (element is null) return 0;
                if (eligible())
                    CompleteUia(uia2, () => CaretParallelQuery.ReadIfCurrent(eligible, () => CaretQueryState.FromResolvedRectangle(element.GetCaretRectangle(), true)));
                // TP1 cannot win after TP2 succeeds; avoid occupying the UIA owner
                // with unnecessary lower-priority work.
                if (eligible() &&
                    !(uia2.Task.IsCompletedSuccessfully && uia2.Task.Result.HasValue))
                    CompleteUia(uia1, () => CaretParallelQuery.ReadIfCurrent(eligible, () => CaretQueryState.FromResolvedRectangle(element.GetSelectionCaretRectangle(), false)));
                return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or Win32Exception) { return 0; }
        catch (Exception error)
        {
            uia2.TrySetException(error);
            uia1.TrySetException(error);
            throw; // Ownership/batch failures are also observed by the drain.
        }
        finally
        {
            uia2.TrySetResult(null);
            uia1.TrySetResult(null);
        }
    }

    private static void CompleteUia(TaskCompletionSource<CaretPosition?> completion, Func<CaretPosition?> query)
    {
        try { completion.TrySetResult(query()); }
        catch (Exception error) when (error is COMException or Win32Exception) { completion.TrySetResult(null); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
