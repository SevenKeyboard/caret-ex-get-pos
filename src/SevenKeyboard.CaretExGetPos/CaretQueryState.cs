using SevenKeyboard.UiAutomation;

namespace SevenKeyboard;

// Numeric values are the original provider priorities, not the current query order.
internal enum CaretProvider
{
    JavaAccessBridge = 1,
    UIATextPattern2,
    MSAA,
    GUIThreadInfo,
    UIATextPattern,
}

internal sealed class CaretQueryState
{
    private static readonly CaretProvider[] DefaultOrder =
    [
        CaretProvider.JavaAccessBridge, CaretProvider.UIATextPattern2,
        CaretProvider.MSAA, CaretProvider.GUIThreadInfo, CaretProvider.UIATextPattern,
    ];
    private readonly object _gate = new();
    private readonly Dictionary<nint, Entry> _windows = new();

    internal sealed class Entry(nint window)
    {
        internal nint Window { get; } = window;
        internal CaretProvider? Method { get; set; }
        internal int Priority { get; set; } = 999;
        internal int Failures { get; set; }
    }

    // Called in the same native work item as the foreground sample, so a shell
    // notification cannot run between sampling that HWND and creating its entry.
    internal Entry Begin(nint window)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(window, out Entry? entry))
                _windows.Add(window, entry = new Entry(window));
            return entry;
        }
    }

    internal CaretProvider[] GetOrder(Entry entry)
    {
        lock (_gate)
        {
            if (entry.Method is not { } first)
                return [.. DefaultOrder];
            CaretProvider[] order = new CaretProvider[DefaultOrder.Length];
            order[0] = first;
            int index = 1;
            foreach (CaretProvider provider in DefaultOrder)
                if (provider != first)
                    order[index++] = provider;
            return order;
        }
    }

    internal void Complete(Entry entry, CaretProvider? successfulProvider)
    {
        lock (_gate)
        {
            // An in-flight query may still return a useful result after destruction.
            // It must not restore the old entry or train a reused HWND's new entry.
            if (!_windows.TryGetValue(entry.Window, out Entry? current) ||
                !ReferenceEquals(current, entry))
                return;

            if (successfulProvider is not { } provider)
            {
                if (entry.Method.HasValue)
                    entry.Failures = Math.Min(99, entry.Failures + 1);
                return;
            }

            int priority = (int)provider;
            if (priority <= entry.Priority || ++entry.Failures >= 3)
            {
                entry.Method = provider;
                entry.Priority = priority;
                entry.Failures = 0;
            }
        }
    }

    internal void Forget(nint window)
    {
        lock (_gate)
            _windows.Remove(window);
    }

    internal void Clear()
    {
        lock (_gate)
            _windows.Clear();
    }

    internal static CaretPosition? FromCaretRange(UiaCaretRange? range) =>
        FromRectangles(range?.Rectangles, useTextPattern2: true);

    internal static CaretPosition? FromFirstTextSelection(UiaTextRectangle[]? rectangles) =>
        FromRectangles(rectangles, useTextPattern2: false);

    private static CaretPosition? FromRectangles(UiaTextRectangle[]? rectangles, bool useTextPattern2)
    {
        if (rectangles is not [var rectangle] ||
            !TryRound(rectangle.Left, out int x) ||
            !TryRound(rectangle.Top, out int y) ||
            !TryRound(rectangle.Height, out int height))
            return null;

        int width = 1;
        if (useTextPattern2 && !TryRound(rectangle.Width, out width))
            return null;
        return new CaretPosition(x, y, width, height,
            useTextPattern2 ? "UIATextPattern2" : "UIATextPattern");
    }

    private static bool TryRound(double value, out int result)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(rounded) || rounded < int.MinValue || rounded > int.MaxValue)
        {
            result = 0;
            return false;
        }
        result = (int)rounded;
        return true;
    }
}
