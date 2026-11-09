using System.Diagnostics;

namespace SevenKeyboard;

// Result delivery and native/COM completion deliberately have different lifetimes.
internal sealed record CaretQueryOperation(Task<CaretPosition?> Result, Task Drain);

internal static class CaretParallelQuery
{
    internal static CaretQueryOperation Start(
        Task<CaretPosition?> java, Task<CaretPosition?> uia2, Task<CaretPosition?> msaa,
        Task<CaretPosition?> gui, Task<CaretPosition?> uia1, Task? cleanup = null)
    {
        Task<CaretPosition?>[] candidates = [java, uia2, msaa, gui, uia1];
        return new(SelectAsync(candidates), DrainAsync(candidates, cleanup));
    }

    private static async Task<CaretPosition?> SelectAsync(Task<CaretPosition?>[] candidates)
    {
        // All work has already been started/queued. Await order decides preference,
        // not execution order. A faster, lower-ranked result cannot win the race.
        foreach (Task<CaretPosition?> candidate in candidates)
            if (await candidate.ConfigureAwait(false) is { } position)
                return position;
        return null;
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
