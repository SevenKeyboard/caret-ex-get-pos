using System.ComponentModel;
using System.Runtime.InteropServices;
using SevenKeyboard.UiAutomation;

namespace SevenKeyboard;

public sealed partial class CaretExGetPos
{
    // Accessed only by the instance's FIFO query/disposal runner.
    private CaretParallelNativeThreads? _parallelNative;

    private async Task<CaretQueryOperation> StartParallelAsync(CaretNativeThread native, UiAutomationClient? uia)
    {
        _parallelNative ??= await CaretParallelNativeThreads.CreateAsync(native.Desktop).ConfigureAwait(false);

        // Do not Begin/GetOrder/Complete a cache entry in this mode.
        nint window = await native.RunAsync(native.SampleForeground).ConfigureAwait(false);
        if (window == 0)
            return new(Task.FromResult<CaretPosition?>(null), Task.CompletedTask);

        Task<CaretPosition?> java = native.RunAsync(() => native.TryGetJava(window));
        var uia2 = new TaskCompletionSource<CaretPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uia1 = new TaskCompletionSource<CaretPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Four independent owners: JAB/message pump, MSAA, Win32, and borrowed UIA.
        // Both UIA stages retain one focused element and their existing owner.
        Task uiaCleanup = RunUiaAsync(uia, uia2, uia1);
        Task<CaretPosition?> msaa = _parallelNative.Msaa.RunAsync(() => CaretNative.TryGetMsaa(window));
        Task<CaretPosition?> gui = _parallelNative.Gui.RunAsync(CaretNative.TryGetGuiThreadInfo);
        return CaretParallelQuery.Start(java, uia2.Task, msaa, gui, uia1.Task, uiaCleanup);
    }

    private static async Task RunUiaAsync(
        UiAutomationClient? uia, TaskCompletionSource<CaretPosition?> uia2, TaskCompletionSource<CaretPosition?> uia1)
    {
        UiaElement? element = null;
        try
        {
            if (uia is not null) element = await uia.GetFocusedElementAsync().ConfigureAwait(false);
            if (element is null)
            {
                uia2.TrySetResult(null); uia1.TrySetResult(null);
                return;
            }
            await CompleteUiaAsync(uia2, async () =>
                CaretQueryState.FromCaretRange(await element.GetCaretRangeAsync().ConfigureAwait(false))).ConfigureAwait(false);
            await CompleteUiaAsync(uia1, async () =>
                CaretQueryState.FromFirstTextSelection(await element.GetFirstTextSelectionAsync().ConfigureAwait(false))).ConfigureAwait(false);
        }
        catch (Exception error) when (error is COMException or Win32Exception)
        {
            uia2.TrySetResult(null); uia1.TrySetResult(null);
        }
        catch (Exception error)
        {
            uia2.TrySetException(error); uia1.TrySetException(error);
        }
        finally
        {
            if (element is not null) await element.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CompleteUiaAsync(TaskCompletionSource<CaretPosition?> completion, Func<Task<CaretPosition?>> query)
    {
        try { completion.TrySetResult(await query().ConfigureAwait(false)); }
        catch (Exception error) when (error is COMException or Win32Exception) { completion.TrySetResult(null); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
