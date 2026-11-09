# Controlled HWND DPI smoke test

This standalone executable tests the actual `CaretDpi.GuiToPhysical` function through
`InternalsVisibleTo("CaretExGetPos.DpiSmoke")`. It requires Windows 10 1607+ and
supports x64/x86, including NativeAOT. PMv2 cases are added when the OS supports them.

Each target DPI awareness mode (unaware, system, per-monitor, optional PMv2) has a
fresh owner thread. On every available physical monitor it creates two borderless,
invisible, disabled, nonactivating custom parent HWND fixtures at different positions.
It never shows a window, adds a standard UI control, or changes the display settings.

For each parent it places four invisible custom child HWND markers in known target-
logical client rectangles: an almost full-client rectangle, an interior rectangle,
a rectangle near the far corner, and a one-pixel-wide caret shape. All corners stay
inside the parent. Windows positions/sizes these HWNDs; `GetWindowRect` from a
per-monitor-aware context supplies their independent physical screen bounds. The
oracle does **not** call `LogicalToPhysicalPointForPerMonitorDPI`, `GuiToPhysical`,
or copy the production conversion sequence. A separate physical client-origin
check confirms the frame-free parent geometry.

The production conversion runs for every available caller-awareness context, and
all four returned physical edges are compared against the marker HWND bounds.
One physical pixel of edge tolerance accounts for USER32's coordinate quantization.
Wide/tall rectangles expose missing width/height scaling beyond this tolerance.
A one-pixel logical caret is allowed to collapse to zero physical width on a lower-
DPI monitor. The checks also verify caller DPI restoration, target HWND awareness,
fixture ownership, and absence of visibility, activation, and focus.

Only handles created by this executable are queried. Monitor geometry/scales are
read to select fixture placements. The executable never calls global GetPosition,
GetGUIThreadInfo(0), GetForegroundWindow, JAB, UIA, clipboard APIs, input APIs,
screenshot/capture APIs, ShowWindow, SetFocus, or SetForegroundWindow. No process-
wide DPI setting is changed. HWNDs and window classes are destroyed on their owner
threads, including failure paths. Every owner thread has a 30-second test timeout.

## Build and run

Run these commands from the repository root. Keeping the artifacts path under this
project isolates outputs/intermediates for this project and its project references.

```powershell
dotnet build tests/CaretExGetPos.DpiSmoke/CaretExGetPos.DpiSmoke.csproj -c Release --artifacts-path tests/CaretExGetPos.DpiSmoke/artifacts/managed
dotnet publish tests/CaretExGetPos.DpiSmoke/CaretExGetPos.DpiSmoke.csproj -c Release -r win-x64 -p:PublishAot=true --artifacts-path tests/CaretExGetPos.DpiSmoke/artifacts/x64 -o tests/CaretExGetPos.DpiSmoke/artifacts/x64/publish
dotnet publish tests/CaretExGetPos.DpiSmoke/CaretExGetPos.DpiSmoke.csproj -c Release -r win-x86 -p:PublishAot=true --artifacts-path tests/CaretExGetPos.DpiSmoke/artifacts/x86 -o tests/CaretExGetPos.DpiSmoke/artifacts/x86/publish
& tests/CaretExGetPos.DpiSmoke/artifacts/x64/publish/CaretExGetPos.DpiSmoke.exe
& tests/CaretExGetPos.DpiSmoke/artifacts/x86/publish/CaretExGetPos.DpiSmoke.exe
```

Use the repository's normal signing workflow if Windows Application Control
requires trusted binaries. This harness does not modify trust/security settings.

The report includes every target/monitor/placement, measured logical and physical
client bounds, assertion/query counts, and actual scaling coverage. Default exit
codes: `0` assertions passed; `1` failure; `2` unsupported OS or invalid arguments.
`--require-mixed` additionally returns `2` when fewer than two monitor scales or no
nonidentity logical/physical fixture are available. It never changes scaling to
manufacture coverage. A default run on a single-scale setup explicitly reports the
missing mixed-DPI coverage rather than claiming that scenario passed.

This verifies Windows client-to-physical mapping for controlled hidden HWNDs, not
foreground caret discovery, Java's empirical coordinate assumptions, actual text
rendering, or any third-party accessibility provider. No Java transforms are edited.

References:
- [Mixed-mode DPI contexts](https://learn.microsoft.com/windows/win32/hidpi/high-dpi-improvements-for-desktop-applications)
- [GetWindowRect and DPI virtualization](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowrect)
- [LogicalToPhysicalPointForPerMonitorDPI](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-logicaltophysicalpointforpermonitordpi)

## Observed validation (2026-11-09)

The x64 JIT run with `--require-mixed` exited 0: 7,168 assertions, 512 direct
`GuiToPhysical` calls, and 32 hidden parent HWND fixtures on four monitors with
100%, 125%, and 175% scaling. Unaware, system-aware, PMv1, and PMv2 targets and
callers were covered. Ten fixtures had measured nonidentity logical/physical
sizing; placements included a negative-X monitor and a monitor below the primary.
All four physical edges and hidden/disabled/noactivation/context-restoration
checks passed. No input, foreground application inspection, or screenshots were used.

NativeAOT runs were not completed. Initial publishes exposed cross-artifact SDK
source globbing; the project now excludes all `artifacts*/**` and `reports/**`.
MSBuild evaluation confirms only `FixtureNative.cs` and `Program.cs` are source
items. The test code and its assertions were unchanged by that build fix.

The subsequent managed win-x86 build (PlatformTarget=x86, PublishAot=false)
passed with zero warnings and zero errors. No x86 fixture execution is claimed.
