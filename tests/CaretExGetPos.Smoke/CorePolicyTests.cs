using SevenKeyboard;
using SevenKeyboard.UiAutomation;

internal static class CorePolicyTests
{
    internal static async Task RunAsync()
    {
        OriginalLearning();
        DestroyedWindowIdentity();
        TextRectanglePolicy();
        await CoreLifecycleTests.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("Caret core policy/lifecycle checks passed (no desktop providers).");
    }

    private static void OriginalLearning()
    {
        CaretQueryState state = new();
        var entry = state.Begin(123);
        Equal(new[] { CaretProvider.JavaAccessBridge, CaretProvider.UIATextPattern2,
            CaretProvider.MSAA, CaretProvider.GUIThreadInfo, CaretProvider.UIATextPattern }, state.GetOrder(entry));
        state.Complete(entry, null);
        Require(entry.Method is null && entry.Failures == 0, "An unlearned miss must not accumulate failures.");
        state.Complete(entry, CaretProvider.GUIThreadInfo);
        Equal(new[] { CaretProvider.GUIThreadInfo, CaretProvider.JavaAccessBridge,
            CaretProvider.UIATextPattern2, CaretProvider.MSAA, CaretProvider.UIATextPattern }, state.GetOrder(entry));
        for (int i = 1; i <= 2; i++)
        {
            state.Complete(entry, CaretProvider.UIATextPattern);
            Require(entry.Method == CaretProvider.GUIThreadInfo && entry.Failures == i,
                "The first two worse-provider successes must retain the cached method.");
        }
        state.Complete(entry, CaretProvider.UIATextPattern);
        Require(entry.Method == CaretProvider.UIATextPattern && entry.Failures == 0,
            "The third worse-provider success must replace the cached method.");
        state.Complete(entry, null);
        state.Complete(entry, CaretProvider.UIATextPattern2);
        Require(entry.Method == CaretProvider.UIATextPattern2 && entry.Failures == 0,
            "A better default-priority provider must be learned immediately.");
        state.Complete(entry, null);
        state.Complete(entry, CaretProvider.UIATextPattern2);
        Require(entry.Failures == 0, "Success from the cached provider resets failures.");
        state.Complete(entry, null);
        state.Complete(entry, null);
        state.Complete(entry, CaretProvider.GUIThreadInfo);
        Require(entry.Method == CaretProvider.GUIThreadInfo && entry.Failures == 0,
            "Total misses count toward the same three-failure threshold.");
        state.Complete(entry, CaretProvider.JavaAccessBridge);
        state.Complete(entry, CaretProvider.MSAA);
        state.Complete(entry, CaretProvider.UIATextPattern);
        state.Complete(entry, CaretProvider.GUIThreadInfo);
        Require(entry.Method == CaretProvider.GUIThreadInfo && entry.Failures == 0,
            "Fallback successes need not come from the same provider to reach the threshold.");
        for (int i = 0; i < 150; i++)
            state.Complete(entry, null);
        Require(entry.Failures == 99 && entry.Method == CaretProvider.GUIThreadInfo,
            "Total misses cap at 99 without discarding the cached provider.");
        state.Complete(entry, CaretProvider.UIATextPattern);
        Require(entry.Method == CaretProvider.UIATextPattern && entry.Failures == 0,
            "A worse-provider success after capped misses must switch immediately.");
        Require(state.Begin(456).Method is null, "Learning must be per HWND.");
    }

    private static void DestroyedWindowIdentity()
    {
        CaretQueryState state = new();
        var oldEntry = state.Begin(123);
        state.Complete(oldEntry, CaretProvider.JavaAccessBridge);
        state.Forget(123);
        state.Complete(oldEntry, CaretProvider.UIATextPattern2);
        var replacement = state.Begin(123);
        Require(!ReferenceEquals(oldEntry, replacement) && replacement.Method is null,
            "Late success must not recreate a destroyed HWND's entry.");
        state.Complete(replacement, CaretProvider.MSAA);
        state.Complete(oldEntry, CaretProvider.JavaAccessBridge);
        state.Complete(oldEntry, null);
        Require(replacement.Method == CaretProvider.MSAA && replacement.Failures == 0,
            "Late completion must not train a new entry for a reused HWND.");
        state.Forget(999);
        Require(ReferenceEquals(replacement, state.Begin(123)), "An unrelated destruction must not clear the cache.");
    }

    private static void TextRectanglePolicy()
    {
        UiaTextRectangle rectangle = new(-1.5, 2.5, 3.5, 4.5);
        Require(CaretQueryState.FromCaretRange(new(false, [rectangle])) ==
            new CaretPosition(-2, 3, 4, 5, "UIATextPattern2"),
            "TP2 ignores IsActive, preserves width, and rounds away from zero.");
        Require(CaretQueryState.FromFirstTextSelection([rectangle]) ==
            new CaretPosition(-2, 3, 1, 5, "UIATextPattern"), "TP1 must force width 1.");
        Require(CaretQueryState.FromCaretRange(null) is null &&
            CaretQueryState.FromCaretRange(new(true, [])) is null &&
            CaretQueryState.FromCaretRange(new(true, [rectangle, rectangle])) is null,
            "TP2 accepts exactly one rectangle.");
        Require(CaretQueryState.FromFirstTextSelection(null) is null &&
            CaretQueryState.FromFirstTextSelection([]) is null &&
            CaretQueryState.FromFirstTextSelection([rectangle, rectangle]) is null,
            "TP1 accepts exactly one rectangle in the first selection range.");
        Require(CaretQueryState.FromFirstTextSelection([rectangle with { Width = double.NaN }])?.Width == 1,
            "TP1 ignores the reported width entirely.");
        Require(CaretQueryState.FromCaretRange(new(true, [rectangle with { Left = double.NaN }])) is null &&
            CaretQueryState.FromCaretRange(new(true, [rectangle with { Height = double.PositiveInfinity }])) is null &&
            CaretQueryState.FromCaretRange(new(true, [rectangle with { Left = (double)int.MaxValue + 1 }])) is null,
            "Malformed native coordinates must not become wrapped integer results.");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal(CaretProvider[] expected, CaretProvider[] actual) =>
        Require(expected.SequenceEqual(actual), "Provider order differs from the original policy.");
}
