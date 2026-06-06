---
baseline_commit: 2557f1e5f4941b6ffafeae5d5c36077edeeaa1a3
---
# Story 6.2: Soak Tests — 30-min No-Crash + 8-Hour Scale Ceiling

Status: done
<!-- 2026-06-06: Code review (Sonnet) CHANGES-REQUESTED → all 4 patches applied + verified (P1 8-hr
     popup-closable/diagnostics-responsive now asserted not hardcoded; P2 IsBounded guard + Samples>=3
     cross-check; P3 named EventListCapacity const replaces the retyped 5000 literal; P3 rollover FileCount>=2
     in real gate runs). 3 P3s deferred (benign farm fidelity). Core 553/2 unchanged; soak quick-mode 6/6 (~10s);
     exclusion re-verified (not in .sln, soak-trait). review → done. NOTE: the harness is the deliverable +
     structurally validated; the real 30-min soak run (and the OPTIONAL 8-hr) are pre-release gate activities the
     Project Lead runs at release time per docs/DEVELOPMENT.md. -->;

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer / ohSpy maintainer,
I want two automated soak tests under `[Trait("category", "soak")]` — a 30-minute representative-debugging-session run that asserts zero crashes / zero UI-thread stalls > 1 s / zero unclosable popups (NFR-R1 / SC-R-30min), and an 8-hour scale-ceiling run that asserts process memory stays bounded (< 200 MB) with 20 devices + 5 subscription popups + a saturated SSDP log while the bounded collections and on-disk log rollover behave (Scale ceiling / SC-013),
so that NFR-R1 and the Scale-Ceiling Performance Budget are verified before the L&L rather than discovered during the demo or in real-world use.

## ⚠️ Read this first — what this story is (and is NOT)

**This is a TEST-INFRASTRUCTURE story. It writes test/harness code only — essentially NO new production code.**

- The deliverables are: (1) a soak harness + two soak tests under `[Trait("category", "soak")]`; (2) the `FakeUpnpDevice`-farm capabilities the soak needs (some must be BUILT — see §"FakeUpnpDevice farm: reuse vs build"); (3) a committed Markdown soak report under `docs/soak-reports/`; (4) the gate documented in `docs/DEVELOPMENT.md`.
- **Epic 6 delivers no new FRs.** Source: epics.md §"Epic 6" + epic-5-retro "Epic-6 preparation". If a soak run surfaces a real defect, that defect is a **separate fix** (with its own regression test); do not gold-plate the harness, do not refactor production code, do not add features. **A soak flake is a real defect — investigate it, never retry-until-green** (NFR-R1 and the Scale Ceiling are not statistical claims; a single failure is a defect to fix). Source: epics.md 6.2 AC, "flakes are investigated".
- **The soak is HEADLESS — it drives the Core VM + service stack, NOT the WinUI windows.** See ⭐ #1 below. This is the load-bearing design decision.

## ⭐ #1 — The headless-drive boundary (THE design decision; read before coding)

`CoreAppBoundaryTests` (NetArchTest) forbids any WinUI dependency in `ohSpy.Core` and in `ohSpy.Core.Tests`. The real WinUI windows live in `ohSpy.App` and **cannot** be driven from a test project. **So the soak does NOT open real windows.** It exercises the **real Core VM + service stack** against a `FakeUpnpDevice` farm:

- Real types under test (all shipped, all in `ohSpy.Core`): `ShellViewModel`, `AdapterScope`, `DiscoveryService`, `SsdpParser`, `DeviceRegistry`, `DeviceTreeViewModel`, `DeviceNodeViewModel`/`ServiceNodeViewModel`/`ActionNodeViewModel`, `SubscriptionClient`, `EventCallbackHost`, `SubscriptionPopupViewModel`, `InvocationPopupViewModel`, `PropertiesViewModel`, `DiagnosticsViewModel`, `SsdpLogViewModel`, `DiagnosticRingSink`, `DiagnosticFileSink`, and the real `IDiagnosticEmitter` fan-out.
- The harness wires them **exactly like `ShellViewModelTests.NewHarness` does today** (`tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` L62-97) — that is the canonical full-stack assembly. The soak is a longer-running, larger-farm version of that rig.

**The session script (epics.md 6.2) maps to these Core operations — NOT to UI gestures:**

| Script step (epic prose) | Headless Core operation |
|---|---|
| start app / bind adapter | `ShellViewModel.StartAsync(appToken)` → `WaitForStartupAsync()`; the transport factory hands out a **writable** transport (see `ChannelSsdpTransport`) so the farm can inject datagrams |
| device announces / appears in tree | farm writes `SsdpDatagram` (NOTIFY ssdp:alive / search-response) into the transport channel → `DiscoveryService` → `DeviceRegistry.Upsert` → `DeviceTreeViewModel.Devices` (the real eager-description fetch hits the farm's HTTP endpoint) |
| open device tree / expand services | `DeviceNodeViewModel.EnsureChildrenLoadedAsync` / the node's expand path (lazy SCPD fetch against the farm) — the same command the TreeView fires on expand |
| expand a service → actions stream in | `ServiceNodeViewModel` lazy SCPD fetch (incremental) against the farm's `/scpd.xml` |
| invoke a few actions (succeed / fault / timeout) | construct `InvocationPopupViewModel` for an `ActionNodeViewModel`, call its initialise/submit path → real SOAP over loopback to the farm; farm returns 200 / SOAP-fault / hangs (timeout) |
| open 2 (8-hr: 5) subscription popups, leave running | construct `SubscriptionPopupViewModel(service, parentEntry, _subscriptionClient, _ui, _diag, _registry)` and call `InitializeAsync` — the real SUBSCRIBE through `SubscriptionClient` + the live `EventCallbackHost` on loopback; the farm emits NOTIFY to the callback URL → events land in `SubscriptionPopupViewModel.Events` (the 5,000-cap bounded list) |
| open + close diagnostic viewer | construct/dispose `DiagnosticsViewModel` (it wraps the live `DiagnosticRingSink.Entries`); "responsive at session end" = it still observes the ring + the gate setter still round-trips |
| switch adapter once | `ShellViewModel.SwitchAdapterAsync(otherAdapter)` (the farm's `StubAdapterEnumerator` exposes ≥ 2 adapters) |
| rescan twice | `ShellViewModel.RescanCommand` / `RescanAsync` (use `SetRescanDelayForTest` if you want the MX window compressed — see §time-parameterisation) |

**How "0 UI-thread stalls > 1 s" is measured headlessly — this needs a NEW test-only dispatcher.**
Neither shipped test dispatcher fits: `InlineUiDispatcher` runs `Post` inline on the calling thread (masks marshalling and has no UI thread to stall); `DeferredUiDispatcher` only queues until a manual `Drain`. The soak needs a **real single-threaded pumping dispatcher** — a dedicated "UI thread" with a serial work queue — so that:
1. the marshalling discipline is genuinely exercised (off-thread `await` continuations marshal back via `Post`, exactly as in the real app — see MEMORY `winui-no-synccontext-marshal-vm`), and
2. **UI-stall is observable**: enqueue a periodic "tick" action (e.g. every 100 ms) and measure the wall-clock gap between successive tick executions; any gap > 1 s means the UI thread was blocked > 1 s by some other queued work → **assert 0 such gaps**. (This is the "dispatcher-tick timing" the epic AC names.) Also record the **max observed dispatch latency** for the report.

Build this as `tests/ohSpy.Core.Tests/Fakes/PumpingUiDispatcher.cs` (a single dedicated thread running a `BlockingCollection<Action>` loop; `IsOnUiThread` true only on that thread; `AssertOnUiThread` enforces it; `PostAsync` round-trips through the queue). It is a **test fake, not a production seam** — it lives in the test/soak assembly. This is the only non-trivial new harness primitive.

## ⭐ #2 — Keep the 8-hour test OUT of the default suite AND the chaos hook; time-parameterise it

**The danger (the task's #2 reconciliation):** a bare `dotnet test` (run often during dev) runs **every** suite — architecture.md L1516 documents `dotnet test  # all xUnit suites (unit + integration + chaos)` and there is no filter by default. If the soak tests sit in `ohSpy.Core.Tests`, a plain `dotnet test` would block for 8 hours.

**Decision — separate `tests/ohSpy.Soak.Tests` project (engineering judgement the epic AC explicitly invites).** Rationale:
- A separate project means `dotnet test tests/ohSpy.Core.Tests` (the everyday command, and the command the dev runs constantly) **never** touches the soak. A trait on a test inside `ohSpy.Core.Tests` does NOT protect a project-targeted or bare `dotnet test` — only an explicit `--filter` does, and devs forget filters. Project isolation is the robust guard.
- **Do NOT add `ohSpy.Soak.Tests` to the solution's default build/test set.** Add the project file but leave it out of `ohSpy.sln`'s default test configuration (or add it to the solution but document that it is invoked **by path only**, never by a bare solution-wide `dotnet test`). Verify with `CoreAppBoundaryTests` still green and that `dotnet test ohSpy.sln` does not pick it up (if the sln includes it, set `<IsTestProject>` carefully or exclude via a solution filter `.slnf`; simplest robust option: keep it physically present but NOT referenced by `ohSpy.sln`, invoked only as `dotnet test tests/ohSpy.Soak.Tests`).
- **Still tag every soak test `[Trait("category", "soak")]`** (architecture.md L1985 reserves this trait; epics.md 6.2 mandates it). The trait is belt-and-braces: even if someone adds the project to the sln, `dotnet test --filter "category!=chaos&category!=soak"` (architecture.md L2387 — the documented "quick" command) excludes it.
- **Chaos hook is already safe:** `.githooks/pre-commit` runs `dotnet test --filter "category=chaos"` (architecture.md Decision 13 + Amendment A18, L3037-3045). `category=soak` is never matched. Do NOT add soak to the hook. (Note: this checkout has no installed `.git/hooks/pre-commit` — only samples — the committed hook lives at `.githooks/pre-commit`; do not regress it.)

**IMPORTANT consequence of a separate project — Core internals + test seams.** `ohSpy.Core` grants `InternalsVisibleTo` to **`ohSpy.Core.Tests` and `ohSpy.App` only** (`src/ohSpy.Core/ohSpy.Core.csproj` L17-19). The soak NEEDS Core internals and test seams: the `DiagnosticFileSink(ILogger, string diagnosticsDir)` **internal** test-only ctor (to point the rolling log at a temp dir, not the dev's real `%LOCALAPPDATA%`), and `ShellViewModel.SetRescanDelayForTest` / `WaitForStartupAsync` (internal). Two options — pick the lower-churn one and document it:
- **(a)** Add `<InternalsVisibleTo Include="ohSpy.Soak.Tests" />` to `ohSpy.Core.csproj` (one line; mirrors the existing pattern). Several test fakes the soak reuses (`ChannelSsdpTransport`, `SsdpDatagramBuilder`, `StubAdapterEnumerator`, the dispatchers) are `internal` to `ohSpy.Core.Tests` — to reuse them across projects, either move the shared fakes into a small `internal`-friendly shared test-support location or **reference the `ohSpy.Core.Tests` assembly** and add `InternalsVisibleTo` for the soak project on it too.
- **(b)** Keep the soak tests **inside `ohSpy.Core.Tests`** (so all internals/fakes are already visible), tag them `[Trait("category","soak")]`, and accept that protection depends on the documented filter. **Rejected as the default** because it leaves bare `dotnet test` able to trigger 8 hours — the exact failure the task forbids. If the dev finds the cross-project internals churn in (a) too costly, (b) is an acceptable documented fallback **only if** the everyday dev command is locked to a filter; state the choice explicitly in the story completion notes.

**Time-parameterisation (mandatory — the dev must prove the harness works without waiting hours).** Drive both durations from an env var / test parameter with a tiny default for structural validation:
- `OHSPY_SOAK_DURATION` (or two vars: `OHSPY_SOAK_30MIN_DURATION`, `OHSPY_SOAK_8HR_DURATION`) parsed as a `TimeSpan`; default to a **~10-second** smoke when unset so the same script proves it wires up, pumps, asserts, and writes a report in seconds.
- Compress the internal clocks too: use `SetRescanDelayForTest` so a rescan does not really sleep MX (5 s); keep subscription renew + NOTIFY cadence proportional to the run length; the memory-sample interval should be "every 10 min" for the real run but scale down for the smoke (e.g. sample N times across the run regardless of length).
- Document **both commands** in `docs/DEVELOPMENT.md`:
  - real gate: `OHSPY_SOAK_30MIN_DURATION=00:30:00 dotnet test tests/ohSpy.Soak.Tests --filter "category=soak&FullyQualifiedName~ThirtyMinute"` and the 8-hour equivalent.
  - quick structural validation: `dotnet test tests/ohSpy.Soak.Tests` (10-second default — proves the harness, not the gate).

## ⭐ #3 — Bounded-collection caps: reconciled against SHIPPED code (epic prose is ACCURATE)

The dev MUST assert the **shipped** constants (source of truth), not retype the epic numbers. Verified against `main`:

| Collection | Shipped cap | Source | Epic 6.2 AC claim | Match? |
|---|---|---|---|---|
| SSDP message log | **10,000** | `SsdpLogViewModel.cs` L22-23 `private const int Capacity = 10_000;` (FR-016 + D6) | 10,000 | ✅ |
| Subscription event list (per popup) | **5,000** | `SubscriptionPopupViewModel.cs` L58 `new(5000)` (FR-033 + D6) | 5,000 each | ✅ |
| Diagnostic ring | **5,000** | `DiagnosticRingSink.cs` L8-9 `private const int Capacity = 5000;` (FR-041) | 5,000 | ✅ |
| On-disk log | **≤ 2 MB/file, ≤ 8 files (≤ 16 MB)** | `DiagnosticFileSink.cs` L29-30 `MaxFileBytes = 2 MB`, `MaxRetainedFiles = 8` (AC-8.5) | ≤ 2 MB × ≤ 8 = ≤ 16 MB | ✅ |

**No discrepancy** — the earlier-story "5,000 SSDP log" worry the task flagged does NOT apply to shipped code: the SSDP log cap is firmly **10,000** (`SsdpLogViewModel`), distinct from the **5,000** event-list / ring caps. The soak asserts each via the public `BoundedObservableCollection.Count <= cap` after saturation, and asserts eviction is by single Add/Remove (never Reset) per `BoundedObservableCollection`'s `PrependNewest` contract — but the soak's job is the **steady-state cap**, not re-testing the eviction unit behaviour (already covered by `BoundedObservableCollectionTests`). Do NOT hard-code the literals in the soak — read them from the types where the public API allows, or reference the constant, so a future cap change can't silently desync the gate.

## ⭐ #4 — Memory-ceiling caveat (state it honestly in the report and the assert)

A **headless Core soak** process is `ohSpy.Core.Tests`/`ohSpy.Soak.Tests` + Kestrel (the farm) — it is **NOT** the full WinUI `ohSpy.App` process. The WinUI runtime (WindowsAppRuntime, XAML, composition) adds resident overhead the headless process never pays. Therefore:
- The 8-hour soak's `< 200 MB` assertion, measured headlessly, **verifies that the Core collections + pipeline do not leak and that growth is bounded** — it does **not** by itself prove the full app stays under 200 MB.
- The full-app 200 MB figure is verified more directly by **Story 6.3** (interactive SC-013 / a real-app run on the dev LAN). The soak report MUST cross-reference 6.3 and state this limitation explicitly so the assertion is honest.
- **What to assert:** (1) bounded growth / no-leak — resident memory stabilises (plateaus) rather than trending upward across the 10-min samples after warm-up; (2) record **absolute** `Process.WorkingSet64` and `Process.PrivateMemorySize64` samples in the report; (3) keep the `< 200 MB` headless assert as a generous ceiling (the headless process should be comfortably under it; a breach is a real signal), but frame it in the report as "headless Core process; full-app RSS verified in 6.3". Sample every 10 minutes (scaled for the smoke).

## ⭐ #5 — FakeUpnpDevice farm: reuse vs BUILD (significant gap — read before estimating)

The shipped `FakeUpnpDevice` (`tests/ohSpy.Core.Tests/Fakes/FakeUpnpDevice.cs`, Story 1.6) is an **HTTP-only** in-process Kestrel server: it serves `/description.xml` + `/scpd.xml` with **canned bodies** and exactly **three** behaviour modes — `Happy`, `HangBeforeHeaders`, `HangAfter200Ok` (`FakeUpnpDeviceBehavior.cs`). The extended modes (`SlowDripBody`, `GiantScpd`, `ChunkedThenAbort`, `FaultResponse`, `WrongContentLength`) are **explicitly documented as deferred** ("will land in a follow-up story when actually needed" — `FakeUpnpDeviceBehavior.cs` L4-7). **It does NOT advertise over SSDP, does NOT emit NOTIFY, and has a single hard-coded UDN/friendlyName.** SSDP is injected separately in tests via `ChannelSsdpTransport` + `SsdpDatagramBuilder`; NOTIFY is delivered via the real `EventCallbackHost` over loopback.

What the farm needs vs what exists:

| Farm capability (epics.md 6.2) | Status | Action |
|---|---|---|
| 15 normal devices announcing at typical rates | REUSE the HTTP `FakeUpnpDevice` (`Happy`) **per device** + per-device description/SCPD; **BUILD** an SSDP advertiser loop that writes per-device `SsdpDatagramBuilder.Notify(...)` into the `ChannelSsdpTransport` at the configured rate, with a **unique UDN + LOCATION per device** | partly build |
| 3 misbehaving: slow responders | `HangBeforeHeaders`/`HangAfter200Ok` exist (full hang) → a **partial/slow drip** is the deferred `SlowDripBody` — **BUILD** a minimal slow-drip handler if the script wants "intermittently slow" rather than "hangs forever" (or reuse the hang modes for the timeout-invocation step) | build (minimal) |
| 3 misbehaving: mid-interaction byebye | **BUILD** — emit `SsdpDatagramBuilder.Notify(nt, "ssdp:byebye", udn)` mid-run; this is a datagram, not an HTTP mode (the registry drops the row) | build (trivial — datagram only) |
| 3 misbehaving: partial NOTIFY | **BUILD** — `SsdpDatagramBuilder.Malformed()` exists for malformed; "partial NOTIFY" (GENA event) means an incomplete event body to the callback host — feed a truncated NOTIFY to `EventCallbackHost` (the farm's event-emitter, below) | build |
| 3 misbehaving: larger-than-typical / GiantScpd | the canned SCPD is trivial (empty actionList). **BUILD** a large SCPD body (100+ actions) — this is the deferred `GiantScpd` mode; needed to exercise FR-100 incremental stream + the cold-expand budget; **also serves 6.3's GiantScpd budget row** | build |
| event-emitter for 5 subscription popups | **BUILD** — after the soak's `SubscriptionPopupViewModel` SUBSCRIBEs, the farm must POST NOTIFY events to the callback URL the `EventCallbackHost` exposes. The host + `SubscriptionClient` are real; the emitter is a small loop POSTing GENA NOTIFY bodies (reuse `EventCallbackHostTests` patterns for the wire format) | build |
| sustained SSDP advertiser ≥ 1 adv/s background | **BUILD** — the advertiser loop above, configurable rate; ≥ 1/s keeps the 10,000-cap log saturated over 8 h | build |
| ≥ 20 adv/s burst capability | **BUILD** — same loop, burst rate; **also serves 6.1.14 / 6.3's chatty-SSDP target** | build |

**Net:** the HTTP description/SCPD serving is reused; the **SSDP advertiser**, the **GENA event-emitter**, the **GiantScpd body**, and **per-device identity** are new farm scaffolding the soak builds (test code, in the soak/test assembly). Keep it minimal and soak-scoped; do NOT promote it to production. Where a "misbehaving" mode maps to an already-shipped hang mode, reuse it rather than building a new one.

## ⭐ #6 — On-disk log rollover over real wall-clock (AC-8.5 end-to-end)

The soak must end with the rolling diagnostics log showing entries spanning the run **and** size-based rollover applied. Use the **internal test-only ctor** `new DiagnosticFileSink(logger, tempDiagnosticsDir)` (`DiagnosticFileSink.cs` L72) so the soak writes to a **temp dir**, never the dev's real `%LOCALAPPDATA%\ohSpy\diagnostics\`. The sink rotates at 2 MB → sequenced sibling (`ohSpy-yyyyMMdd-NNN.log`, L243-269) and prunes to ≤ 8 files (L271-307). Over 8 h at ≥ 1 adv/s the diagnostic stream will exceed 2 MB and roll. **Assert end-to-end:** after `FlushAsync`, the temp dir contains ≥ 2 files (rollover happened), ≤ 8 files (retention held), no file > ~2 MB (+ one entry's slop), and the earliest + latest entries span the run window. This verifies AC-8.5 over real wall-clock, which unit tests (synthetic byte injection) cannot.

## Acceptance Criteria

> Each AC is verified by the automated soak. ACs are renumbered `6.2.x` for traceability; the source epic/PRD AC is cited inline.

### Gate plumbing (epics.md 6.2 "Given a soak-test harness")

1. **(AC-6.2.1 — soak excluded from default + chaos)** Both soak tests carry `[Trait("category", "soak")]` and live in a project (`tests/ohSpy.Soak.Tests`, or `ohSpy.Core.Tests` per the documented fallback) such that: `dotnet test tests/ohSpy.Core.Tests` does NOT run them; `dotnet test --filter "category!=chaos&category!=soak"` excludes them; the `.githooks/pre-commit` chaos hook (`--filter "category=chaos"`) does NOT run them. Verify a bare everyday `dotnet test` against the Core test project never triggers a multi-hour run.
2. **(AC-6.2.2 — documented gate)** `docs/DEVELOPMENT.md` documents the soak as a pre-release gate, with the exact command for the real 30-min and 8-hour runs **and** the quick (~10 s default) structural-validation command, plus the "flakes are investigated, not retried" rule.

### 30-min no-crash soak (SC-R-30min — NFR-R1)

3. **(AC-6.2.3 — farm composition)** The harness stands up a farm of **15 normal** devices (typical announce rate) + **3 misbehaving** (slow/hang responder, mid-interaction byebye, partial NOTIFY, larger-than-typical/GiantScpd) — per §5 reuse-vs-build.
4. **(AC-6.2.4 — representative session script)** The harness drives the real Core stack through the script in ⭐#1 continuously for the configured duration (default ~10 s smoke; gate = 30 min): startup/bind, tree populate, expand services, invoke actions (succeed / SOAP-fault / timeout), open 2 subscription popups and leave them running, open+close diagnostics, switch adapter once, rescan twice.
5. **(AC-6.2.5 — assertions)** Over the run: **0** unhandled exceptions / faults escaping to the harness (an `UnobservedTaskException` / `AppDomain.UnhandledException` hook records any and FAILS the test); **0** UI-thread stalls > 1 s (measured via the `PumpingUiDispatcher` tick-gap, ⭐#1); **0** popups that cannot be closed (each opened popup VM disposes cleanly — its CTS cancels, handlers detach, no exception); the `DiagnosticsViewModel` remains responsive at session end (still observes the ring; the gate setter round-trips).

### 8-hour scale-ceiling soak (Scale ceiling / SC-013)

6. **(AC-6.2.6 — scale load)** 20 announcing devices + 5 subscription popups (one per service) receiving moderate NOTIFY traffic; the SSDP log held at saturation (≥ 1 adv/s sustained so it sits at/near the 10,000 cap).
7. **(AC-6.2.7 — memory)** `Process.WorkingSet64` + `PrivateMemorySize64` sampled every 10 min (scaled for the smoke); resident memory **bounded / no upward leak trend** after warm-up, and **< 200 MB** at every headless sample — **stated in the report as a HEADLESS Core figure (⭐#4); full-app RSS is 6.3's SC-013**. Samples recorded in the report.
8. **(AC-6.2.8 — bounded collections behave)** At/after saturation: SSDP log ≤ **10,000**; each subscription event list ≤ **5,000**; diagnostic ring ≤ **5,000**; on-disk log ≤ **16 MB** total (≤ 8 files × ≤ 2 MB) — asserted against the shipped constants (⭐#3), not retyped literals.
9. **(AC-6.2.9 — zero exceptions over 8 h)** 0 unhandled exceptions over the full run (same hook as AC-6.2.5).

### Reports + discipline

10. **(AC-6.2.10 — soak report)** A completed run writes `docs/soak-reports/<yyyy-MM-dd-HHmm>-<duration>.md` summarising: environment (adapter surrogate, dev-machine specs, build SHA, .NET version), farm composition, memory samples (table), exception count (0), max dispatch latency, on-disk-log rollover result, and any anomalies. The report is committed as a release-gate artefact. (For the L&L gate the dev commits at least the real 30-min report; the 8-hour report is committed when the gate run completes.)
11. **(AC-6.2.11 — log rollover end-to-end, AC-8.5)** The temp diagnostics dir after the run shows entries spanning the duration with size-based rollover applied (≥ 2 files, ≤ 8 files, no file materially over 2 MB) — ⭐#6.
12. **(AC-6.2.12 — flake discipline documented)** The story/test/README states explicitly that soak flakes are investigated as real defects, not retried-until-green.

## Tasks / Subtasks

- [x] **Task 0 — Project + gate plumbing** (AC: 1, 2)
  - [x] Decided: the **separate `tests/ohSpy.Soak.Tests` project** (the ⭐#2 default). Rationale in completion notes. The soak project is **self-contained** (it builds its OWN farm primitives) so it does NOT depend on `ohSpy.Core.Tests` internals — the only cross-project IVT is the single line on `ohSpy.Core`.
  - [x] Scaffolded `tests/ohSpy.Soak.Tests/ohSpy.Soak.Tests.csproj` (net10.0, xUnit, `FrameworkReference Microsoft.AspNetCore.App`, ref `ohSpy.Core`). Added `<InternalsVisibleTo Include="ohSpy.Soak.Tests" />` to `ohSpy.Core.csproj`. Project is NOT in `ohSpy.sln`. **Verified `dotnet test tests/ohSpy.Core.Tests` does not pick it up (553/2 unchanged) and the chaos hook (`dotnet test --filter category=chaos` solution-wide) only loads `ohSpy.Core.Tests`.**
  - [x] Every soak test tagged `[Trait("category", "soak")]`. **Verified `--filter "category=chaos"` AND `--filter "category!=chaos&category!=soak"` BOTH report "No test matches" against the soak project.**
  - [x] Created `docs/DEVELOPMENT.md` with the real 30-min + 8-hour commands (PowerShell + bash), the ~10 s structural-validation command, the env-var names, the 8-hour-optional note, the HEADLESS caveat + 6.3 cross-ref, and the flake-is-a-defect rule.

- [x] **Task 1 — PumpingUiDispatcher (the UI-stall instrument)** (AC: 5)
  - [x] Built `Fakes/PumpingUiDispatcher.cs` (dedicated thread + `BlockingCollection<Action>` serial queue; `IsOnUiThread` true only on that thread; `AssertOnUiThread` enforces; `Post` enqueues; `PostAsync` round-trips). Periodic tick + max-gap recorder; `MaxDispatchGap` + `StallsOverOneSecond`. Also re-surfaces a faulting UI-thread action via `UiThreadException` into the harness's exception capture.
  - [x] Sanity-tested (`PumpingUiDispatcherTests`): a 1.2 s blocking action → a gap > 1 s recorded; clean run → 0 stalls; `PostAsync` runs on the UI thread; `AssertOnUiThread` throws off-thread / passes on-thread.

- [x] **Task 2 — FakeUpnpDevice farm scaffolding** (AC: 3, 6) — per ⭐#5
  - [x] Per-device identity: `FarmUpnpDevice` (its own Kestrel server, the shipped fake's shape) with a unique UDN + LOCATION per device; serves `/description.xml` (one evented+controllable service) + `/scpd.xml`.
  - [x] `FarmSsdpTransport` (writable, the `ChannelSsdpTransport` pattern) + `DeviceFarm` SSDP advertiser loop (configurable adv/s, re-bursts on M-SEARCH) + byebye-on-demand for the mid-interaction-disappear device.
  - [x] GENA event-emitter: `FarmUpnpDevice.EmitNotifyAsync` POSTs GENA NOTIFY (NT/NTS/SID/SEQ + `<e:propertyset>`) to the live `EventCallbackHost` callback URL; includes a truncated "partial NOTIFY" for the misbehaving device.
  - [x] GiantScpd body (120 actions) for the larger-than-typical device. All test-scoped (in `ohSpy.Soak.Tests`), nothing promoted to production.

- [x] **Task 3 — Shared soak harness (full Core stack)** (AC: 4, 5)
  - [x] `Harness/SoakHarness.cs` assembles the REAL stack (ShellViewModel + DiscoveryService + DeviceRegistry + SubscriptionClient + real EventCallbackHost factory + real UpnpHttpClient + EagerDescriptionDispatcher + the REAL DiagnosticEmitter fan-out: ring + file + gate), with `PumpingUiDispatcher` as `IUiDispatcher` and the farm's writable transport via the factory.
  - [x] Wired the **real** `DiagnosticFileSink` via its internal temp-dir ctor into the emitter fan-out (on-disk rollover exercised; temp dir, not `%LOCALAPPDATA%`).
  - [x] `Harness/SoakRunner.cs` is the parameterised session-script loop driven by `OHSPY_SOAK_*` (default ~10 s); compresses the rescan MX wait via `SetRescanDelayForTest`; scales NOTIFY/memory cadence.
  - [x] `Harness/UnhandledExceptionCapture.cs` installs `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` (+ the UI-thread fault hook) → any hit fails the test.

- [x] **Task 4 — 30-min no-crash soak test** (AC: 3, 4, 5)
  - [x] `ThirtyMinuteNoCrashSoakTests` (`…~ThirtyMinute`, `[Trait("category","soak")]`); 15 normal + 4 misbehaving; runs for `OHSPY_SOAK_30MIN_DURATION` (default ~10 s).
  - [x] Asserts: 0 unhandled exceptions; 0 stalls > 1 s (PumpingUiDispatcher); all opened popups dispose cleanly (closable); DiagnosticsViewModel responsive at end (same live ring + gate setter round-trips).

- [x] **Task 5 — 8-hour scale-ceiling soak test** (AC: 6, 7, 8, 9, 11)
  - [x] `EightHourScaleCeilingSoakTests` (`…~EightHour`, `[Trait("category","soak")]`); 20 devices + 5 popups + 40 adv/s; runs for `OHSPY_SOAK_8HR_DURATION` (default ~10 s).
  - [x] `MemorySampler` samples WorkingSet64 + PrivateMemorySize64 on a run-relative cadence (10-min equivalent, scaled for the smoke); asserts bounded/no-leak (post-warm-up plateau) + < 200 MB HEADLESS; collects the samples.
  - [x] Asserts bounded caps against the SHIPPED constants (`ShippedCaps` reads them by reflection — no retyped literals): SSDP log ≤ 10,000; each event list ≤ 5,000; ring ≤ 5,000; on-disk ≤ 16 MB / ≤ 8 files.
  - [x] Asserts 0 unhandled exceptions over the run.

- [x] **Task 6 — Soak report writer** (AC: 10, 11)
  - [x] `Harness/SoakReport.cs` emits `docs/soak-reports/<yyyy-MM-dd-HHmm>-<duration>.md` (environment, farm composition, memory-sample table, exception count, max dispatch latency, on-disk-rollover result, bounded-cap snapshot, anomalies, the ⭐#4 HEADLESS caveat + 6.3 cross-ref).
  - [x] Committed the structural-validation report pair as evidence the writer works (`docs/soak-reports/2026-06-05-1617-{30min,8hr}.md`). Per the Project Lead, the real 30-min report is committed at the release-gate run (not run during dev).

- [x] **Task 7 — Gates + flake discipline** (AC: 1, 12)
  - [x] `Core -warnaserror` 0/0; App 0/0 bar the pre-existing benign WMC1506 (MainWindow.xaml:162 — untouched by this story); soak project 0/0. Full default suite green: **553 passed / 2 skipped** (the Epic-5/6.1 baseline — UNCHANGED, proving no soak leakage). Soak quick (~10 s) run green (6/6, stable across 3 consecutive runs). Chaos hook green + soak-free.
  - [x] Flake-is-a-defect rule stated in every soak test file header + `docs/DEVELOPMENT.md`.

### Review Findings

> Code review by claude-sonnet-4-6 (fresh context, bmad-code-review workflow). 2026-06-06.
> Verdict: **CHANGES-REQUESTED** — 1 blocking (P1) + 3 patch (P2/P3) + 3 deferred + 1 dismissed.
>
> **✅ ALL 4 PATCHES APPLIED + VERIFIED 2026-06-06** (Core 553/2 unchanged; soak quick-mode 6/6 ~10 s):
> - **P1** — the 8-hr test now calls `harness.CloseAllPopups()`, derives `popupsClosable`/`diagnosticsResponsive`
>   (ring-instance + gate-setter round-trip), passes them to `WriteReport` (no more hardcoded `true`), and
>   hard-asserts both; the exception assert runs after `CloseAllPopups`.
> - **P2** — `MemorySampler.IsBounded` guard lowered to `< 2`; both leak-judging tests now also assert
>   `Samples.Count >= 3` so the heuristic can never pass vacuously.
> - **P3 (literal)** — added a named shipped const `SubscriptionPopupViewModel.EventListCapacity = 5000`
>   (mirrors `SsdpLogViewModel.Capacity`); `ShippedCaps.SubscriptionEventListCapacityConst` reflects it, and
>   `SnapshotCaps`'s no-live-popup fallback reads that instead of the literal `5000`.
> - **P3 (rollover)** — both soak tests now assert `FileCount >= 2` when `!IsSmoke(duration)` (rollover must
>   actually apply in a real gate run, AC-6.2.11).

- [ ] [Review][Patch] **P1 — 8-hr test omits popup-closable + diagnostics-responsive assertions; hardcodes `true` to report** [`tests/ohSpy.Soak.Tests/EightHourScaleCeilingSoakTests.cs:66-68`] — `EightHourScaleCeilingSoakTests` never calls `harness.CloseAllPopups()` and passes `popupsClosable: true, diagnosticsResponsive: true` as hardcoded literals to `WriteReport`. Neither assertion appears in the hard gate asserts. The 30-min test does this correctly. Fix: before hard asserts, call `harness.CloseAllPopups()`, derive `popupsClosable = !harness.Exceptions.Any` + `diagnosticsResponsive = ReferenceEquals(harness.Diagnostics.Entries, harness.RingSink.Entries)`, pass those to `WriteReport`, and add `popupsClosable.Should().BeTrue(...)` and `diagnosticsResponsive.Should().BeTrue(...)` asserts. The exception-capture assert must come AFTER `CloseAllPopups` (as in the 30-min test — dispose exceptions are recorded there).
- [ ] [Review][Patch] **P2 — `IsBounded` returns `true` vacuously when samples < 3; guard practically unreachable but worth hardening** [`tests/ohSpy.Soak.Tests/Harness/MemorySampler.cs:41-43`] — The `if (_samples.Count < 3) return true` early exit means a run that somehow takes only 2 samples would pass the no-leak assertion. The caller ensures >= 4 samples in practice, but a belt-and-braces fix is to lower the guard to `< 2` or add an in-test cross-check that sample count >= 3 before calling `IsBounded`.
- [ ] [Review][Patch] **P3 — Literal `5000` fallback in `SnapshotCaps` when no live popups violates ⭐#3 no-retyped-literals rule** [`tests/ohSpy.Soak.Tests/ThirtyMinuteNoCrashSoakTests.cs:86`] — `eventListCap` falls back to the literal `5000` when `runner.LivePopups.Count == 0`. Fix: read `BoundedObservableCollection<EventNotification>(5000).Capacity` from a disposable temp instance, or expose the constant differently — any path that avoids hardcoding the production literal.
- [ ] [Review][Patch] **P3 — No assertion that rollover actually applied in the real gate run (AC-6.2.11)** [`tests/ohSpy.Soak.Tests/ThirtyMinuteNoCrashSoakTests.cs:119`, `EightHourScaleCeilingSoakTests.cs:56`] — `disk.FileCount` is asserted `<= 8` (cap held) but never `>= 2` (rollover happened). A real 30-min/8-hr run that fails to roll (no 2 MB threshold crossed) would pass silently. Fix: add `if (!SoakConfig.IsSmoke(duration)) { disk.FileCount.Should().BeGreaterThanOrEqualTo(2, "on-disk rollover must apply in the real gate run (AC-6.2.11)"); }` to both soak tests.
- [x] [Review][Defer] **P3 — `EmitNotifyBurstAsync` emits to devices without confirmed active subscriptions** [`tests/ohSpy.Soak.Tests/Harness/SoakRunner.cs:186-200`] — The burst iterates the first 8 farm devices regardless of which have live subscriptions; unmatched SIDs are dropped idempotently. Not a crash, reduces event-list fill rate in smoke. Deferred — pre-existing fidelity limit of quick smoke; cap assertions still hold. — deferred, benign fidelity gap
- [x] [Review][Defer] **P3 — `SoakHarness.DisposeAsync` dispose order pre-existing from `NewHarness` pattern** [`tests/ohSpy.Soak.Tests/Harness/SoakHarness.cs:235-258`] — `Shell.DisposeAsync()` before `_discovery.DisposeAsync()` is the same order as the shipped `NewHarness`. Not a new issue. — deferred, pre-existing
- [x] [Review][Defer] **P3 — `OnMSearch` fire-and-forget burst task not tracked through teardown** [`tests/ohSpy.Soak.Tests/Farm/DeviceFarm.cs:98`] — `_ = BurstAliveAsync(...)` could race channel teardown; burst swallows all exceptions. Tolerated / pre-existing pattern. — deferred, tolerated

## Dev Notes

### What this story is (framing)

A test-infrastructure story under a verification-only epic. The "implementation" is harness + farm scaffolding + two soak tests + a report writer + the documented gate. The only thing resembling production change is **one IVT line** in `ohSpy.Core.csproj` (if the separate-project route is taken). If a soak run finds a real defect, that is a **separate, minimal fix with its own regression test** — not part of this harness story.

### Shipped behaviour — verified against current `main` (reconcile, don't trust stale prose)

- **Caps are accurate** (⭐#3): SSDP log 10,000 (`SsdpLogViewModel.cs` L22-23); event list 5,000 (`SubscriptionPopupViewModel.cs` L58); ring 5,000 (`DiagnosticRingSink.cs` L8-9); on-disk 2 MB × 8 (`DiagnosticFileSink.cs` L29-30). The "5,000 SSDP log" worry does NOT apply to shipped code.
- **DiagnosticFileSink** has an internal test-only ctor accepting the diagnostics dir (`DiagnosticFileSink.cs` L72) — use it to point at a temp dir. Rotation is size (2 MB) + day; sequenced sibling on same-day cap; prune to ≤ 8 (L243-307). `FlushAsync` drains the channel with a 5 s budget (L330).
- **Full-stack assembly pattern** lives in `ShellViewModelTests.NewHarness` (`ShellViewModelTests.cs` L62-97) — copy this shape. The transport factory is the injection point for the farm's SSDP (a writable transport — see `ChannelSsdpTransport`, capacity 256, DropOldest).
- **FakeUpnpDevice is HTTP-only, 3 modes** (`FakeUpnpDevice.cs` + `FakeUpnpDeviceBehavior.cs`); extended modes are deferred and must be BUILT here (⭐#5). SSDP injection uses `SsdpDatagramBuilder` (`Notify(nt, nts, udnBody, location)`, `SearchResponse`, `Malformed`) + a writable transport.
- **Subscription popup is constructed directly** in the soak (the App launcher/window is not used): `new SubscriptionPopupViewModel(service, parentEntry, subscriptionClient, ui, diag, registry)` then `InitializeAsync()` (`SubscriptionPopupViewModel.cs` L73-105+). NOTIFY arrives via the live `EventCallbackHost` over loopback → `Events` bounded list (5,000). The popup links its CTS to the device token (L97); device byebye / adapter switch cancels it → tests the "popup recovers from device disappearance / is closable" path.
- **EventCallbackHost** is real, binds loopback, exposes test seams (`AcceptLoop`, `InFlightConnectionCount`, `EventCallbackHost.cs` L433-440). The farm's emitter POSTs to its callback URL.
- **ShellViewModel test seams** (internal): `SetRescanDelayForTest` (no real 5 s MX sleep), `WaitForStartupAsync`, `SetAdapterTeardownBudgetForTest`, `CurrentAdapterTokenForTest` (`ShellViewModel.cs` L20/83/237/241). The soak uses `SetRescanDelayForTest` to keep rescans fast.

### Marshalling discipline (load-bearing for the soak — MEMORY `winui-no-synccontext-marshal-vm`)

The soak's whole point includes catching the off-thread-VM-mutation crash class. Use `PumpingUiDispatcher` (a REAL second thread), NOT `InlineUiDispatcher` — inline masks marshalling (MEMORY `winui-no-synccontext-marshal-vm`: `InlineUiDispatcher` masks the very defect; the `DeferredUiDispatcher` exists precisely because inline hides it). Every Core VM that mutates observable state after an `await` does so via `IUiDispatcher.Post`; under the pumping dispatcher those continuations resume off-thread and must marshal back — exactly the production hazard. An unmarshalled mutation would either throw (if `AssertOnUiThread` is hit) or corrupt a collection — either way the soak surfaces it.

### Test taxonomy + filtering (architecture)

- `[Trait("category","soak")]` = extended wall-clock (architecture.md L1985). `[Trait("category","chaos")]` = mixed-behaviour drill (L1984); `[Trait("category","integration")]` = port/filesystem/singleton (L1983).
- Chaos hook: `.githooks/pre-commit` → `dotnet test --filter "category=chaos"` (Decision 13 + Amendment A18, architecture.md L3037-3045/L2803-2829). xUnit filter syntax is `category=soak` (NOT MSTest `Trait=category&Value=soak`, which silently matches zero — A18).
- Quick command (architecture.md L2387): `dotnet test --filter "category!=chaos&category!=soak"`. Bare `dotnet test` runs EVERYTHING (L1516) — the reason the soak must be project-isolated.
- AC-name embedding (Pattern 15): embed `ThirtyMinute` / `EightHour` (and AC IDs where clean) so `--filter "FullyQualifiedName~ThirtyMinute"` selects one soak.

### Soak report template (Task 6) — write to `docs/soak-reports/<yyyy-MM-dd-HHmm>-<duration>.md`

```markdown
# ohSpy Soak Report — <30min | 8hr>

- Date / start: <yyyy-MM-dd HH:mm>   Duration (configured): <…>   Duration (actual): <…>
- Build / commit: <sha>   .NET: <ver>   Machine: <CPU / RAM / OS build>
- Mode: HEADLESS Core soak (drives Core VM + service stack; NOT the WinUI app).
  Full-app resident memory is verified separately by Story 6.3 (interactive SC-013).

## Farm composition
| Devices | Normal | Misbehaving (modes) | Subscription popups | SSDP adv/s |
|---|---|---|---|---|

## Memory samples
| t (min) | WorkingSet64 (MB) | PrivateMemorySize64 (MB) |
|---|---|---|
(plateau after warm-up = bounded / no leak)

## Bounded-collection caps at end
| Collection | Cap | Observed |
| SSDP log | 10000 | … |  | event list (max) | 5000 | … | | ring | 5000 | … | | on-disk | ≤16MB/≤8 files | … |

## Assertions
- Unhandled exceptions: 0
- UI-thread stalls > 1 s: 0   (max dispatch gap: __ ms)
- Popups closable: yes   DiagnosticsViewModel responsive at end: yes
- On-disk rollover: <N files, max size, span>

## Anomalies / notes
<none, or list — each is investigated as a real defect, not retried>
```

### Project Structure Notes

- New (default route): `tests/ohSpy.Soak.Tests/` (csproj + soak tests + farm/harness fakes). One IVT line added to `src/ohSpy.Core/ohSpy.Core.csproj` (`ohSpy.Soak.Tests`). New folders `docs/soak-reports/` and (if absent) `docs/DEVELOPMENT.md`. `docs/` exists (only `docs/verification/` so far).
- Do NOT add `ohSpy.Soak.Tests` to `ohSpy.sln`'s default test set; invoke by path. Verify `dotnet test tests/ohSpy.Core.Tests` and `dotnet test ohSpy.sln` (if it ever includes it) cannot trigger a multi-hour run without an explicit duration env var (default is ~10 s).
- No new `DiagCategories` constant is expected — the soak OBSERVES existing diagnostics (`DiagCategoriesUsageTests` would flag an unused new one anyway). Verify none is needed.
- Gates: `Core -warnaserror` 0/0; fully async; the soak project compiles under the same analyzers; default suite unchanged (553/2 baseline); chaos hook untouched.

### Open questions (for the dev / Project Lead — resolve during dev-story, do not block)

1. **Separate project vs in-Core.Tests:** default is the separate `ohSpy.Soak.Tests` (robust against bare `dotnet test`). If the cross-project internals/fakes churn proves heavy, the documented fallback is in-`ohSpy.Core.Tests` with the filter — confirm the choice and lock the everyday dev command to a filter if so.
2. **8-hour report commit timing for the L&L:** the 30-min report is the committable gate artefact for the demo window; is an actual 8-hour run required before L&L, or is the structural-validation + the 30-min run + 6.3's interactive SC-013 sufficient evidence? (Epic AC implies the 8-hour report is committed when run; confirm whether the gate run is scheduled pre-L&L.)
3. **PRD §792 flags the 8-hour / 200 MB ceiling as extrapolated beyond the brief** ("revisit if architecture shows it's not deliverable"). Keep < 200 MB as the headless ceiling, but if the real-app 6.3 figure approaches it, that's a PRD-assumption conversation, not a soak failure.
4. **"Slow responder" vs "hang":** does the script want a genuine `SlowDripBody` (build minimal) or is reusing `HangAfter200Ok` for the timeout-invocation step enough? Lean minimal — reuse the hang mode unless the script specifically needs intermittent slowness.

### References

- [Source: epics.md#Story 6.2: Soak Tests — 30-min No-Crash + 8-Hour Scale Ceiling] (epic ACs; caps confirmed accurate vs shipped)
- [Source: prd.md#§6 Performance Budgets] SC-R-30min (L680), Scale ceiling (L681), SC-013 (L679); [#NFR-R1] (L626); §792 extrapolation note
- [Source: architecture.md#Test Patterns 14/15] trait taxonomy (L1982-1985), AC-name embedding (L1997-2018); [#Test filtering] (L2384-2390); [#Decision 13 — Pre-Commit Chaos Hook] (L3023-3052) + [#Amendment A18] xUnit filter syntax (L2803-2829); bare `dotnet test` runs all (L1516)
- [Source: tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs] (NewHarness L62-97 — the full-stack assembly to mirror)
- [Source: tests/ohSpy.Core.Tests/Fakes/FakeUpnpDevice.cs + FakeUpnpDeviceBehavior.cs] (HTTP-only, 3 modes; extended modes deferred — BUILD the farm)
- [Source: tests/ohSpy.Core.Tests/Fakes/ChannelSsdpTransport.cs + SsdpDatagramBuilder.cs] (writable SSDP injection)
- [Source: tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs + DeferredUiDispatcher.cs] (why a new PumpingUiDispatcher is needed)
- [Source: src/ohSpy.Core/ViewModels/SsdpLogViewModel.cs L22-23] 10,000 cap; [src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs L58, L73-105] 5,000 event list + ctor; [src/ohSpy.Core/Diagnostics/DiagnosticRingSink.cs L8-9] 5,000 ring; [src/ohSpy.Core/Diagnostics/DiagnosticFileSink.cs L29-30, L72, L243-307] rollover + temp-dir ctor
- [Source: src/ohSpy.Core/ViewModels/ShellViewModel.cs] StartAsync/SwitchAdapterAsync/RescanAsync + test seams (L20/83/237/241)
- [Source: src/ohSpy.Core/Threading/IUiDispatcher.cs] the marshalling contract the PumpingUiDispatcher implements
- [Source: src/ohSpy.Core/ohSpy.Core.csproj L17-19] InternalsVisibleTo (Core.Tests + App only — soak project needs adding)
- [Source: _bmad-output/implementation-artifacts/6-1-manual-ui-verification-…md] previous story (Epic 6 verification-only framing; the four WinUI render-hazard memories as defect classes)
- MEMORY: `winui-no-synccontext-marshal-vm` (PumpingUiDispatcher must be a real thread, not inline), `smoke-per-ui-story`

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (BMAD dev-story workflow).

### Debug Log References

- Quick-mode structural validation (~10 s default per test): `dotnet test tests/ohSpy.Soak.Tests` → 6 passed / 0 failed (4 `PumpingUiDispatcher` sanity + the 2 soak tests). Stable across 3 consecutive runs (no introduced flake).
- Exclusion proof: `dotnet test tests/ohSpy.Core.Tests` → 553 passed / 2 skipped in ~4 s (baseline UNCHANGED, no soak leakage). `dotnet test tests/ohSpy.Soak.Tests --filter "category=chaos"` and `--filter "category!=chaos&category!=soak"` both → "No test matches". Chaos hook command `dotnet test --filter "category=chaos"` (solution-wide) → loads only `ohSpy.Core.Tests` (1 chaos test passed); the soak project is not in `ohSpy.sln`.
- `-warnaserror`: Core 0/0, soak 0/0, App 0/0 bar the pre-existing benign WMC1506 (MainWindow.xaml:162, untouched).

### Completion Notes List

**Project-location decision (⭐#2 / Open-Q 1):** chose the **separate `tests/ohSpy.Soak.Tests` project** (the robust default — bare/solution-wide `dotnet test` can never trigger a multi-hour run because the project is not in `ohSpy.sln`). I made the soak project **self-contained**: it builds its OWN farm primitives (`FarmUpnpDevice`, `FarmSsdpTransport`, `SoakSsdpDatagram`, `PumpingUiDispatcher`, the no-op launchers) rather than referencing `ohSpy.Core.Tests` internals. This eliminates the cross-test-project IVT/fakes churn the story flagged as the main cost of the separate-project route — the ONLY production touch is the single `<InternalsVisibleTo Include="ohSpy.Soak.Tests" />` line on `ohSpy.Core.csproj` (for the `DiagnosticFileSink` temp-dir ctor + `ShellViewModel.SetRescanDelayForTest`/`WaitForStartupAsync` seams).

**Headless fidelity:** the soak drives the REAL Core stack with the REAL `UpnpHttpClient`, REAL `EventCallbackHost`, REAL `DiagnosticEmitter` fan-out (ring + temp-dir file sink + gate), and REAL `SubscriptionClient` — the farm devices are real loopback Kestrel servers answering description/SCPD/SOAP/SUBSCRIBE and POSTing real GENA NOTIFY to the live callback host. SSDP is injected via the scope-owned writable `FarmSsdpTransport` (the factory hands the farm's instance to the real `AdapterScope`). The session script maps to the shipped Core ops (StartAsync, node lazy-expand, Invocation/Subscription popup VMs, DiagnosticsViewModel, SwitchAdapterAsync, RescanCommand) — never UI gestures.

**UI-stall measurement (⭐#1):** `PumpingUiDispatcher` is a real dedicated thread + serial queue, so marshalling is genuinely exercised (off-thread `await` continuations re-queue via `Post`) and stalls are timed by inter-tick gap. An un-marshalled VM mutation would trip `AssertOnUiThread` (re-surfaced as a captured exception). The smoke recorded max dispatch gap ~112 ms, 0 stalls > 1 s.

**Bounded caps (⭐#3):** `ShippedCaps` reads the production cap CONSTANTS by reflection (and the live `BoundedObservableCollection.Capacity` for the event-list/ring) — no retyped literals, so a future cap change auto-desyncs and fails the gate loudly. Smoke confirmed SSDP log saturating (~1.2–1.8k toward 10k at the compressed rate), event lists ~290, ring ~120 — all ≤ their shipped caps.

**Memory (⭐#4):** `< 200 MB` is asserted as a HEADLESS WorkingSet64 ceiling with a bounded/no-leak heuristic (post-warm-up plateau within a 2× factor). The smoke plateaued at ~68 MB private / ~125 MB working set after warm-up. The report + DEVELOPMENT.md state this is the headless Core process, NOT the full WinUI app — full-app RSS is Story 6.3's SC-013.

**On-disk rollover (⭐#6):** the temp-dir `DiagnosticFileSink` is flushed before assertions; the inspector verifies ≤ 8 files / ≤ 16 MB + slop. In the ~10 s smoke the stream does not exceed 2 MB so only 1 file is produced (rollover "n/a (compressed smoke)") — the real 30-min/8-hour gate run exceeds 2 MB and rolls; the assertion (≤ 8 files, total ≤ 16 MB + slop) holds at both scales.

**8-hour OPTIONAL (Project Lead decision):** the full harness is built and 8-hour-capable/time-parameterised, but the 8-hour full run is NOT a required release gate. The only soak executed during dev is the ~10 s structural quick-mode (the same script + assertions, compressed). DEVELOPMENT.md documents the 8-hour run as optional; the required evidence is the 30-min soak + the structural quick-validation + Story 6.3's interactive 1-hour SC-013.

**No new production code / DiagCategories:** the soak OBSERVES existing diagnostics; no new `DiagCategories` constant (DiagCategoriesUsageTests/ExactSet unchanged). No defect surfaced by the quick-mode run (if a gate run finds one, it is a separate minimal fix with its own regression test — the harness is not gold-plated).

**Open questions resolved:** Q1 → separate project (above). Q2 → 8-hour optional (above; Project Lead). Q3 → < 200 MB kept as the headless ceiling, framed honestly. Q4 → reused the hang semantics for the slow/timeout device (no bespoke slow-drip) per the "lean minimal" guidance.

### File List

**Production (1 line):**
- `src/ohSpy.Core/ohSpy.Core.csproj` — added `<InternalsVisibleTo Include="ohSpy.Soak.Tests" />`.

**New soak project (`tests/ohSpy.Soak.Tests/`):**
- `ohSpy.Soak.Tests.csproj` — soak test project (NOT in `ohSpy.sln`; invoked by path).
- `Fakes/PumpingUiDispatcher.cs` — real-thread pumping dispatcher + UI-stall instrument.
- `Farm/FarmSsdpTransport.cs` — writable SSDP transport (the farm's injection point).
- `Farm/SoakSsdpDatagram.cs` — SSDP alive/byebye datagram builder.
- `Farm/FarmUpnpDevice.cs` — per-device Kestrel server (description/SCPD/SOAP/GENA verbs/NOTIFY emitter/GiantScpd).
- `Farm/DeviceFarm.cs` — farm orchestrator (advertiser loop, byebye/partial-NOTIFY, misbehaving set).
- `Harness/NoOpLaunchers.cs` — minimal launcher stubs + identity lookup for the NodeServices bundle.
- `Harness/UnhandledExceptionCapture.cs` — AppDomain/TaskScheduler/UI-thread exception capture.
- `Harness/SoakConfig.cs` — `OHSPY_SOAK_*` duration parsing (~10 s smoke default).
- `Harness/MemorySampler.cs` — WorkingSet64/PrivateMemorySize64 sampling + no-leak heuristic.
- `Harness/ShippedCaps.cs` — reflects the shipped bounded-collection cap constants (no retyped literals).
- `Harness/OnDiskLogInspector.cs` — on-disk rollover inspection.
- `Harness/SoakReport.cs` — Markdown report writer.
- `Harness/SoakHarness.cs` — the full real-Core-stack harness.
- `Harness/SoakRunner.cs` — the representative session-script driver.
- `PumpingUiDispatcherTests.cs` — dispatcher sanity tests (`[Trait soak]`).
- `ThirtyMinuteNoCrashSoakTests.cs` — 30-min no-crash soak (`[Trait soak]`, `…~ThirtyMinute`).
- `EightHourScaleCeilingSoakTests.cs` — 8-hour scale-ceiling soak (`[Trait soak]`, `…~EightHour`).

**Docs:**
- `docs/DEVELOPMENT.md` — NEW: everyday commands + the soak gate (real 30-min/8-hour + ~10 s quick commands, env vars, 8-hour-optional note, HEADLESS caveat, flake-is-a-defect rule).
- `docs/soak-reports/2026-06-05-1617-30min.md`, `docs/soak-reports/2026-06-05-1617-8hr.md` — NEW: structural-validation report artefacts.

### Change Log

- 2026-06-05: Story 6.2 implemented via dev-story (claude-opus-4-8[1m]). New headless `tests/ohSpy.Soak.Tests` project (separate, NOT in `ohSpy.sln`, invoked by path) with two `[Trait("category","soak")]` soak tests (30-min no-crash + 8-hour scale-ceiling), the `PumpingUiDispatcher` UI-stall instrument, a `FakeUpnpDevice`-style farm (SSDP advertiser + GENA emitter + GiantScpd + per-device identity), the full real-Core-stack harness + session-script runner, memory sampler, shipped-cap reflector, on-disk-rollover inspector, and the Markdown report writer. One production line: `InternalsVisibleTo` on `ohSpy.Core.csproj`. New `docs/DEVELOPMENT.md` + structural-validation soak reports. Core suite 553/2 UNCHANGED (no soak leakage); soak quick-mode 6/6 green (stable ×3); `-warnaserror` 0/0 across Core/soak (App bar pre-existing WMC1506); chaos hook + quick filter both exclude soak (verified "No test matches"). 8-hour full run is OPTIONAL per the Project Lead.
