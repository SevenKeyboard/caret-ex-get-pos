# caret-ex-get-pos

Asynchronous caret lookup in physical desktop pixels. Version 3.0.0; .NET 10,
Windows 10 1607+, x64/x86, NativeAOT. Java Access Bridge is x64 only.

```csharp
using SevenKeyboard;
using SevenKeyboard.UiAutomation;

await using var uia = await UiAutomationClient.CreateAsync();
await using var caret = await CaretExGetPos.CreateAsync(uia);

if (await caret.GetPositionAsync() is { } position)
    Console.WriteLine($"{position.X}, {position.Y}, {position.Width}, {position.Height}: {position.CaretType}");
```

Create one caret instance and share it. UIA is optional: `CaretExGetPos.CreateAsync()`
uses the other providers. The caller owns UIA and disposes the caret first.
If UIA creation fails, the caller may catch the exception and create the caret
without it; retrying UIA creation is also the caller's responsibility.

The original provider order is Java Access Bridge, UIA TextPattern2, MSAA,
GUIThreadInfo, UIA TextPattern, with the original per-window preferred-provider
cache. Unavailable providers and native lookup failures fall through; no result
returns null. Caller misuse and disposed-object errors remain exceptions.
Coordinates and dimensions use physical screen pixels, including negative desktop
coordinates. Results contain values only and need no disposal.

Queries run in acceptance order. Foreground lookup begins when a request actually
starts, rather than when it is queued. Focus may still change while Windows is
being queried. Window destruction clears its cache without repopulating it from
an older query. `DisposeAsync()` rejects new requests and drains accepted requests
before releasing internal resources. A stalled native provider can delay shutdown.

Java uses `dll/x64/windowsaccessbridge-64.dll` below `AppContext.BaseDirectory`.
An optional absolute `javaAccessBridgePath` overrides it. Missing/unloadable Java
support disables only that provider; no other installation is searched. The DLL
and its notices are included automatically in build and publish output. The Java
application must have Access Bridge enabled; this library does not change its
settings. The bundled binary and original Java DPI corrections are retained.
Overlapping instances share one native runtime. If another instance already loaded
a different DLL path, the new instance skips Java rather than substituting that
DLL. The last instance releases the runtime; a later instance may select a new path.

Dependencies are separate source projects. Override `UiAutomationClientProjectPath`,
`ShellHookWindowProjectPath`, and `LibraryBuildTargetsPath` for another layout.
The build checks minimum versions and matching majors. Product snapshots should
include these sources, the Java DLL, and its notices; original repositories do
not copy dependency sources into themselves.

Compared with AHK 2.1.0, C# 3.0.0 uses an owned instance and asynchronous value
results, borrows optional UIA, and always returns physical pixels. Provider order,
cache learning, first-selection rules, and provider labels retain original behavior.
See [tests](tests/README.md) for validation and remaining live-DPI coverage.
