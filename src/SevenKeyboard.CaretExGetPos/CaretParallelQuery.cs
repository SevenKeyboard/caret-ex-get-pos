using System.Diagnostics;

namespace SevenKeyboard;

// Result delivery and native/COM completion deliberately have different lifetimes.
internal sealed record CaretQueryOperation(Task<CaretPosition?> Result, Task Drain);

internal static class CaretParallelQuery
{
    // Validate again after a synchronous provider call: a delayed timer/continuation
    // must not allow a result acquired outside its original observation budget.
    internal static CaretPosition? ReadIfCurrent(Func<bool> eligible, Func<CaretPosition?> read)
    {
        if (!eligible()) return null;
        CaretPosition? result = read();
        return eligible() ? result : null;
    }

    internal static CaretQueryOperation Start(
        Task<CaretPosition?> java, Task<CaretPosition?> uia2, Task<CaretPosition?> msaa,
        Task<CaretPosition?> gui, Task<CaretPosition?> uia1, Task? cleanup = null, Task? deadline = null)
    {
        Task<CaretPosition?>[] candidates = [java, uia2, msaa, gui, uia1];
        return new(SelectAsync(candidates, deadline), DrainAsync(candidates, cleanup));
    }

    private static async Task<CaretPosition?> SelectAsync(Task<CaretPosition?>[] candidates, Task? deadline)
    {
        // With no budget, retain the original deterministic priority contract.
        if (deadline is null)
        {
            foreach (Task<CaretPosition?> candidate in candidates)
                if (await candidate.ConfigureAwait(false) is { } position) return position;
            return null;
        }

        while (true)
        {
            bool expired = deadline.IsCompleted;
            bool higherPending = false;
            List<Task> pending = [];
            foreach (Task<CaretPosition?> candidate in candidates)
            {
                if (!candidate.IsCompleted)
                {
                    higherPending = true;
                    pending.Add(candidate);
                    continue;
                }
                // Required-path programmer/lifetime failures retain their identity.
                // Lower speculative failures remain observed by DrainAsync.
                if (!higherPending || expired)
                {
                    if (await candidate.ConfigureAwait(false) is { } position) return position;
                }
            }
            if (expired || pending.Count == 0) return null;
            pending.Add(deadline);
            await Task.WhenAny(pending).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Task<CaretPosition?>[] candidates, Task? cleanup)
    {
        foreach (Task<CaretPosition?> candidate in candidates)
        {
            try { await candidate.ConfigureAwait(false); }
            catch (Exception error)
            {
                // Selection forwards errors on paths it needs. Other speculative
                // results must be observed too, without replacing an earlier winner.
                Trace.TraceInformation("Caret provider completed with an error: {0}", error.GetType().Name);
            }
        }
        // Ownership cleanup is different from an unused provider's query failure.
        // The orchestrator retains cleanup faults for DisposeAsync.
        if (cleanup is not null) await cleanup.ConfigureAwait(false);
    }
}
