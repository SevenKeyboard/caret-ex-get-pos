# Validation

Observed on Windows 11, 2026-11-09:

| Check | Result |
| --- | --- |
| Core preference/cache rules and asynchronous admission/drain | Signed x64 and x86 passed |
| Native ABI and message-worker lifetime (`--pump`) | Signed x64, x86 and NativeAOT x64: 70 checks each |
| Bundled Java DLL lifetime (`--jab-lifetime`) | Signed x64 and NativeAOT x64: 78 checks each |
| Controlled HWND DPI mapping | x64 passed: 7,168 assertions, 512 conversions, 32 hidden fixtures |
| DPI fixture x86 build | Passed without warnings/errors; execution not performed |
| Shared UiAutomationClient | Signed x64, x86 and NativeAOT x64 passed 155 assertions each |
| IMC using the extracted client | x64 smoke and extended smoke passed; extended NativeAOT x64 passed |

Native lifetime runs include repeated shared ownership, last-owner cleanup,
accepted-work draining, actual Java DLL load/free/recreate, and recovery after
WM_QUIT to the owned worker while an old lease remains alive. No actual JVM caret
was queried. All counts above include the final stopped-worker recovery fix.

The DPI fixture used four monitors at 100%, 125%, and 175%, including negative
coordinates. It checked unaware, system-aware, PMv1 and PMv2 targets/callers,
position and size, and restoration of the caller's DPI context. It uses invisible
owned windows and does not send input or inspect user applications.

Projects:
- `CaretExGetPos.Smoke`: provider preference rules, cache invalidation, rectangle
  policy, concurrent admission, failures, and disposal draining.
- `CaretExGetPos.NativeSmoke`: native ABI, coordinate math, internal message
  window and Java DLL lifetime. `--pump` excludes Java; `--jab-lifetime` loads it.
- `CaretExGetPos.DpiSmoke`: real controlled HWND DPI conversion. See its README
  for the independent geometry oracle and build commands.

Build a test project with `dotnet build <project> -c Release`. Use separate
`--artifacts-path` directories for x86 and NativeAOT and the host's normal signing
workflow. `-r win-x64 -p:PublishAot=true` produces a NativeAOT executable.

Not established by these checks: actual Java application caret coordinates,
application-specific UIA text/caret behavior, Windows 10 execution, and live x86
mixed-DPI placement. The original Java correction formulas remain unchanged.
DpiSmoke NativeAOT execution was not completed. Existing IMC real-IME trials were
not repeated because its recognition policies are preserved.
