namespace SevenKeyboard;

// Prepared with the caret instance for parallel queries; sequential lookup uses the shared native runtime.
internal sealed class CaretParallelNativeThreads : IAsyncDisposable
{
    internal CaretProviderThread Msaa { get; }
    internal CaretProviderThread Gui { get; }

    private CaretParallelNativeThreads(CaretProviderThread msaa, CaretProviderThread gui)
    {
        Msaa = msaa;
        Gui = gui;
    }

    internal static async Task<CaretParallelNativeThreads> CreateAsync(nint desktop)
    {
        CaretProviderThread msaa = await CaretProviderThread.CreateAsync("CaretExGetPos MSAA", desktop, true).ConfigureAwait(false);
        try
        {
            CaretProviderThread gui = await CaretProviderThread.CreateAsync("CaretExGetPos Win32", desktop, false).ConfigureAwait(false);
            return new(msaa, gui);
        }
        catch
        {
            await msaa.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(Task.WhenAll(Msaa.DisposeAsync().AsTask(), Gui.DisposeAsync().AsTask()));
}
