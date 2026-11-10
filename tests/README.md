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


Conservative geometry and bounded parallel lookup (4.0.0, 2026-11-10):

- Signed managed x64, managed x86, and NativeAOT x64 passed core policy/lifecycle,
  fixed-priority selection, and new bounded-selection/slot/drain tests.
- Tests explicitly cover expiry with no result, choosing the best completed result,
  no late replacement or reuse, skipping busy paths, resuming available paths,
  parallel progress despite unfinished work, sequential barriers, disposal draining,
  and cleanup errors surviving later successful queries. A synchronous provider
  result is rejected if its budget/context expires during the call.
- Signed NativeSmoke --workers passed on x64, x86, and NativeAOT x64 (25 base
  native/DPI checks plus worker and shared-Java admission suites). Shared Java tests
  race 16 admissions across two leases, verify only one queued/active query,
  expired queued work, sequential-call exclusion, failure recovery and disposal.
  They substitute the Java query body; they do not establish real JAB accuracy.
- UiAutomationClient 1.1.0 passed 266 assertions on each of the same three targets.
  Native COM stubs exercise direct inactive TP2 geometry, real selection endpoints,
  LTR/RTL neighbors, unsupported/mixed direction, wrap/bidi ambiguity, larger
  fallback text units, verified document boundaries, and failure-path ownership.
  Raw UIA APIs retain their previous contracts.
- Temporary enhanced verifier startup passed: F1-F4, passive overlays, initialization
  and cleanup without injected input. Separate unaware/system/PMv2 matrix hosts
  verified their DPI settings, UIA/JAB readiness and clean exit.
- Initial checks did not cover real-application alignment or live IsActive
  comparison. Later VS Code timing and geometry observations are recorded below.
  WPF/browser LTR, RTL, mixed text, selection and wrap fixtures are ready for
  manual trials. Ambiguous/missing results are not passes.
- Initial timing used a uniform 50 ms budget. Historical 3.1.0 matrix reports
  had 30 first Auto samples (median 78.28 ms, max 164.88 ms) and 30 repeated samples
  (median 4.85 ms, max 14.78 ms). They do not record each parallel completion time
  and cannot establish this revision's deadline miss rate or an optimal budget.
- Current foreground/focus HWND checks reject obvious target changes, not all
  movement within one field. Provider queries are not an atomic desktop snapshot.
  IsActive is deliberately still not filtered. Live RTL/wrap accuracy and
  broader cold/warm timing remain unverified. Windows 10 and live x86/AOT
  mixed-DPI coverage remain unverified.
- Local evidence: D:/Dev/_scratch/caret-enhancement-20261110/{caret,uia,native}-*
  logs; enhanced startup logs beside the temporary verifier projects.


First-query allowance and fallback review (2026-11-10):

- The first executed parallel query per CaretExGetPos instance now gets 200 ms;
  subsequent parallel queries get 100 ms, including after null or failure.
  Sequential queries stay unbounded and neither consume nor reset the allowance.
  No per-window tracking or public option was added. The same captured duration
  governs both provider eligibility and the result-selection timer.
- Signed managed x64, managed x86, and NativeAOT x64 smoke executions passed
  for the earlier 100/50 setting. Signal-driven tests cover 100 -> 50 -> 50 admission,
  independent instances, null/failing first attempts consuming the allowance,
  and sequential interleaving preserving it, alongside existing selection and
  cleanup checks. Logs: D:/Dev/_scratch/caret-budget-final-20261110/{x64,x86,aot}.txt.
- UIA TextPattern remains last. No VS Code blocklist, zero-element-bounds filter,
  stale-coordinate heuristic, or IsActive filter was added.
- Prior uniform-50-ms Notepad trials: managed x64 and NativeAOT x64 each missed
  the first result; all 39 repeats succeeded. A separate NativeAOT UIA observation
  reached 45.85 ms on a repeat. These motivated the initial 100/50 choice,
  later widened to 200/100 for more allowance. Neither is proven optimal.
- VS Code timing initially included 90-160 ms total-return outliers. A later
  NativeAOT trace had 60 successes, first 51.47 ms, repeated median 4.88 ms and
  maximum 10.95 ms. Its first UIA focused-element lookup took about 95 ms, but
  the MSAA result was delivered earlier after timer expiry. The older large
  return outliers were not reproduced or explained; no timing fix is claimed.
- User visual checks found Auto/MSAA following the VS Code insertion caret;
  the UIA TextPattern marker stayed at the left. Three stable passive samples
  found MSAA at (1155, 744), 1x26 and raw UIA selection at (906, 745), 1x24.
  Current resolved/final coordinates and AHK-style conversion of that same raw
  selection all matched UIA's (906, 745). AHK itself was not executed.
  Thus the discrepancy predates C# geometry conversion; the underlying provider
  cause is still unknown. TextPattern2 supplied no rectangle in these samples.
- Local timing/geometry evidence is under D:/Dev/_scratch/
  caret-latency-20261110, caret-latency-trace-20261110, and
  caret-vscode-geometry-20261110. Test-only instrumentation is not in the library.

Final pre-commit validation (2026-11-10):

- Adopted the agreed 200 ms first-query / 100 ms subsequent-query policy.
  Signed managed x64, managed x86, and NativeAOT x64 passed the core policy,
  lifecycle, fixed-priority, and bounded-selection suites with the new values.
- On the same three targets, NativeSmoke --workers passed 25 native/DPI checks
  plus worker/admission suites and the new shared-Java shutdown/replacement test.
  The new signal-driven test holds a fake Java query and queued work, closes both
  leases, then starts replacement creation. It checks preserved accepted results,
  draining before old-thread exit, a fresh runtime, and fresh Java admission.
  This does not exercise a real Java application's Access Bridge.
- Updated comments to match eager worker preparation and null-result behavior.
  No additional UIA heuristics or COM-call refactoring was introduced.
- Evidence: D:/Dev/_scratch/caret-release-20261110/
  core-{x64,x86,aot}.txt and native-{x64,x86,aot}.txt.
  No new live-app timing or visual alignment test was performed.

Shared DPI context reuse (4.0.1, 2026-11-10):

- Signed managed x64, managed x86, and NativeAOT x64 each passed
  NativeSmoke --pump: 85 native/DPI checks. The added regression checks zero
  rejection, native error propagation, and exact raw-context restoration while
  unwinding a failed nested scope.
- Signed managed x64 DpiSmoke --require-mixed passed 7,168 assertions,
  512 GuiToPhysical calls, and 32 hidden HWND fixtures across four monitors.
  Mixed-scale coverage was present, with 10 nonidentity-size fixtures.
- All four builds/publishes completed without warnings or errors. All output
  EXEs and top-level DLLs had Valid Authenticode signatures before execution.
- Source review confirmed TransformJava is unchanged; JavaToPhysical changes
  only the raw DPI getter qualification. These runs used owned hidden windows
  without input or foreground application queries.
- Logs: D:/Dev/_scratch/dpi-context-reuse-20261110/
  {caret-x64,caret-x86,caret-aot,caret-dpi-x64}/ contains build.log, sign.log,
  test.stdout.log, test.stderr.log, and test.exitcode.txt. All test exits were 0.
  Other suites and live-application checks were not repeated in this matrix.

Remaining live validation:

- Fresh-client Notepad/VS Code trials with the new 200/100 policy; current live
  timing evidence above used the previous uniform-50-ms policy.
- Selection endpoints, RTL/mixed-direction text, and wrap boundaries in real
  WPF/browser controls for the new conservative geometry behavior.
- Reproduction of the occasional 90-160 ms return delay if observed again.
- Windows 10, live x86/NativeAOT mixed-DPI alignment, and additional real Java
  applications remain outside the completed coverage.
- Known fallback limitation: plausible incorrect UIA TextPattern coordinates can
  still be returned when all higher-priority paths fail. This is accepted policy,
  not a defect claimed to have been corrected. Callers may use CaretType.
