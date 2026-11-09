namespace SevenKeyboard;

// Admission is serialized by CaretExGetPos. A busy owner never receives another
// speculative request; its old result is owned exclusively by its original query.
internal sealed class CaretProviderSlot<T>
{
    private Task _pending = Task.CompletedTask;
    internal Task Pending => _pending;

    internal bool TryStart(Func<Task<T>> start, out Task<T> task)
    {
        if (!_pending.IsCompleted)
        {
            task = Task.FromResult(default(T)!);
            return false;
        }
        try { task = start(); }
        catch (Exception error) { task = Task.FromException<T>(error); }
        _pending = task;
        return true;
    }
}
