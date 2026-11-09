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

The checks above did not establish live-application caret accuracy. Subsequent
manual trials are recorded below. Windows 10 execution and live x86 mixed-DPI
placement remain unverified. The original Java correction formulas are unchanged.
DpiSmoke NativeAOT execution was not completed. Existing IMC real-IME trials were
not repeated because its recognition policies are preserved.

Experimental parallel scheduling (3.1.0, 2026-11-09):
- Signed x64 and x86: existing sequential policy/lifecycle tests and new parallel tests passed.
- Signed NativeAOT x64: the same policy/lifecycle tests passed via a temporary host.
- Priority tests cover all 120 completion orders and all five first-available winners
  (600 cases), early delivery, required-provider errors, unused-provider faults,
  cleanup failure, FIFO mode capture, invalid modes, and disposal draining.
- NativeAOT also called the actual production sequential/parallel modes twice each
  against the current foreground without input. All calls and cleanup completed;
  those samples had no caret result, so they establish neither coordinate accuracy
  nor comparative performance. The manual verifier provides the live comparison.
- The parallel code does not touch the learned cache; this was checked in code review.
  Java/UIA ownership and existing coordinate transformations are unchanged.

Four-group revision (2026-11-09):
- Separate reusable MSAA and Win32 workers now overlap the existing Java and UIA
  owners. UIA TextPattern2 and TextPattern still share one focused element/owner.
- Signed x64, x86, and NativeAOT x64 `NativeSmoke --workers` passed: barriers prove
  four distinct owners can enter concurrently; desktop/MTA binding, thread reuse,
  FIFO, command failure isolation, startup failure, draining, and joins are checked.
  These fixtures do not query foreground applications or inject input.
- Existing priority/lifecycle tests passed again on signed x64, x86, and NativeAOT
  x64. NativeAOT's production sequential/parallel calls each ran twice; all four
  returned MSAA and disposed successfully. These samples are not an accuracy or
  performance comparison; live alignment and throughput still need manual review.
- The temporary verifier now uses only the fixed four-group Auto policy. The F5
  scheduler comparison was removed. F1-F4, stale-result rejection, passive-overlay,
  startup, and cleanup checks passed. Real Swing, WPF, WinForms and browser input
  fixtures are prepared outside the repository; live caret alignment remains manual.

Live application and caller-DPI checks (2026-11-10, Windows 11 x64):

The temporary comparison app queried each target twice from three separate
processes: DPI-unaware, system-aware, and per-monitor-aware. The user also checked
the physical caret overlay against the visible caret. Agreement between hosts
alone does not prove screen alignment. Input-interrupted captures were rejected;
missing results were not counted as passes.

| Target | Matching paths across the three caller environments |
| --- | --- |
| WinForms with unaware, system-aware, and per-monitor-aware target windows | Auto / GUIThreadInfo |
| Java Swing input (175% monitor) | Auto / JavaAccessBridge |
| WPF input (175% monitor, after MSAA fix) | Auto / MSAA / GUIThreadInfo |
| Chrome input (175% monitor) | Auto / MSAA / UIA TextPattern |
| Notepad (175% monitor) | Auto / UIA TextPattern2 / UIA TextPattern / GUIThreadInfo |

WinForms checks included 125% and 175% monitors. The Notepad UIA rectangle and
GUIThreadInfo rectangle had different heights but the same lower edge; each path
was consistent across caller DPI settings. This does not establish that those two
providers describe identical text extents. Auto selected UIA TextPattern2 and the
user confirmed alignment.

WPF initially reproduced caller-dependent MSAA coordinates. Object acquisition,
accLocation, and release now run inside a temporary per-monitor thread-DPI scope;
the previous context is restored afterward. The shared MSAA method is used by both
query modes. No AHK code, Java conversion, UIA policy, or process DPI was changed.
The repeated WPF trial returned (1392, 2436), 1 x 49 in all three caller environments
for Auto, MSAA, and GUIThreadInfo, and the user confirmed visible alignment.

Local comparison evidence includes matrix-20261110-015929-1775e756.json (WPF before),
matrix-20261110-023247-d0d9a3f8.json (WPF after), and the 020207, 020540, and 020742
reports for Java, Chrome, and Notepad. Temporary apps and reports are not shipped
with the library. These trials are not a throughput benchmark or coverage of all
Java runtimes, editors, and UI frameworks. Live x86, Windows 10, and NativeAOT
mixed-DPI application alignment still require separate coverage.

Final MSAA-scope regression checks (2026-11-10):

- Signed managed x64, signed managed x86, and signed NativeAOT x64 each passed
  `NativeSmoke --pump`: 82 native/DPI checks.
- These include twelve actual MSAA no-result calls on the owned hidden window:
  unaware, system-aware, PMv1, and PMv2 caller contexts across three lifecycles.
  Every call restored the worker's previous DPI context. Successful live WPF
  queries are covered by the separate manual trial above.
- Final x86 and NativeAOT x64 publish completed without build warnings or errors.
- The comparison app verified all three host DPI modes, UIA/JAB readiness, and
  clean shutdown. The displayed overlay and comparison helpers were rebuilt
  from the revised source and signed before the final WPF trial.
