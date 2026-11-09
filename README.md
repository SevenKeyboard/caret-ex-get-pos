# caret-ex-get-pos

Asynchronous caret lookup in physical desktop pixels. Version 4.0.0; .NET 10,
Windows 10 1607+, x64/x86, NativeAOT. Java Access Bridge is x64 only.

    using SevenKeyboard;
    using SevenKeyboard.UiAutomation;

    // Keep a dedicated client when IMC or other consumers need a separate queue.
    await using var caretUia = await UiAutomationClient.CreateAsync();
    await using var caret = await CaretExGetPos.CreateAsync(caretUia);

    if (await caret.GetPositionAsync() is { } position)
        Console.WriteLine($"{position.X}, {position.Y}, {position.Width}, {position.Height}: {position.CaretType}");

Create one caret instance and share it. UIA is optional: CreateAsync() uses the
other providers. Dispose the caret before its borrowed UIA client.

The default runs four groups in parallel: Java, MSAA, Win32, and UIA. UIA
TextPattern2 and TextPattern share one owner. Preference is Java, UIA TextPattern2,
MSAA, GUIThreadInfo, then UIA TextPattern. It does not use the learned cache.
The first parallel query per instance has a 200 ms preference budget; later
queries have 100 ms. These are not hard real-time deadlines. The first allowance
is consumed when that query starts, even if it returns null or fails; changing
windows does not reset it. Explicit sequential queries do not consume it.
An available winner returns sooner when no higher pending path can win.
At the budget, the best completed result is returned once; no usable result gives
null. Late results never replace it or become a later query's answer.

Each provider group accepts only one outstanding query. A busy group is skipped
by later requests while the other groups can sample again. Slow native calls are
not forcibly cancelled. Dedicated UIA clients separate client queues, but cannot
make an unresponsive target application respond.

Coordinates and dimensions are physical screen pixels, including negative desktop
coordinates. UIA caret geometry uses a direct rectangle or supported text-range
evidence. Ambiguous selection endpoints or character edges return no result.
IsActive is not used to reject a caret. No input-field-edge guesses are made.
UIA TextPattern remains the last fallback. A provider can return a plausible but
incorrect rectangle; no app-specific blocking or confidence heuristic is applied.
Callers can inspect CaretType when deciding whether to use this fallback.
Returned values own no resources. Missing data and native lookup failures fall
through; caller misuse and disposed-object errors remain exceptions.

Results are selected in acceptance order. Obvious foreground/focus HWND changes
discard the parallel result. This is not an atomic snapshot: movement inside one
field may still race the query. Do not enqueue an unbounded stream of requests.

Explicit sequential lookup remains available:

    var position = await caret.GetPositionAsync(CaretQueryMode.Sequential);

It retains the original per-window learned preference and waits for preceding
speculative work. Both modes use the conservative UIA geometry improvements.
DisposeAsync() rejects new requests, finishes accepted requests, waits for all
native work and cleanup, then releases the workers. A stalled provider may delay
shutdown. Cleanup errors are reported by disposal.

MSAA temporarily uses per-monitor thread DPI and restores it. Java retains its
original conversion formulas. The application's process DPI is unchanged.
Java uses dll/x64/windowsaccessbridge-64.dll below AppContext.BaseDirectory;
an optional absolute javaAccessBridgePath overrides it. Missing/unloadable Java
support disables that path. The DLL and notices are copied to build/publish output.
The target Java application must already have Access Bridge enabled.

Dependencies are separate source projects. Override UiAutomationClientProjectPath,
ShellHookWindowProjectPath, and LibraryBuildTargetsPath for another layout.
The build checks minimum versions and matching majors. Product snapshots include
dependency sources, the Java DLL, and notices; source repositories do not vendor
each other.

Version 4 changes the default scheduling and may return null where older versions
waited longer or used an ambiguous selection rectangle. AHK remains unchanged.
See [tests](tests/README.md) for completed validation and live-test limits.
