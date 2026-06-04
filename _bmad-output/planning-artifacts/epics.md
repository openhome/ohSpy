---
stepsCompleted: [1, 2, 3, 4]
lastStep: 4
inputDocuments:
  - "_bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md"
  - "_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md"
project_name: "ohSpy"
user_name: "Simonc"
date: "2026-06-01"
status: "final"
completedAt: "2026-06-01"
---

# ohSpy - Epic Breakdown

## Overview

This document provides the complete epic and story breakdown for ohSpy, decomposing the requirements from the PRD and Architecture into implementable stories.

ohSpy is a native Windows desktop UPnP inspector for Linn software engineers — the supported successor to Intel's discontinued Device Spy. v1 scope is parity with the prior `UpnpSpy` implementation plus named fixes (virtualised SSDP log, per-request HTTP timeouts, incremental SCPD parse) and two parity-plus additions (`<allowedValueList>` / `<allowedValueRange>` constrained inputs).

## Requirements Inventory

### Functional Requirements

**4.1 Discovery & Device Registry**

- **FR-004:** Active SSDP discovery on startup — issue M-SEARCH with `ST: upnp:rootdevice` on the user-selected eligible adapter.
- **FR-005:** Tree entry per responding root device — create a registry entry; tree row admitted once eager description fetch succeeds.
- **FR-006:** Continuous unsolicited-advertisement listening — listen for unsolicited SSDP NOTIFY for the entire app runtime.
- **FR-007:** UUID-keyed device identity — devices uniquely identified by UUID from `USN` / `<UDN>`; further advertisements for known UUID MUST NOT create duplicates.
- **FR-008:** Removal on graceful leave — `NTS: ssdp:byebye` removes the device from registry and tree.
- **FR-053:** Root-only registration with three-layer enforcement — registry contains only root UPnP devices; embedded children flatten into root's `<serviceList>`.
- **FR-054:** Case-insensitive alphabetical tree ordering with stable secondary key — device rows ordered case-insensitive by friendly name with ordinal UUID tiebreak; node identity (selection/expansion) preserved across sort-induced row migration.

**4.2 Eager Device-Description Fetch & Tree Visibility**

- **FR-043:** Asynchronous, bounded eager description fetch — fetch description XML eagerly on registry entry; bounded parallelism (target 8); mismatched-root backstop; subsequent advertisements MUST NOT re-fetch.
- **FR-047:** Hide-until-loaded tree visibility — a device appears in the tree if and only if `DescriptionFetchState == Loaded`.

**4.3 Device Tree Display**

- **FR-001:** Two-pane layout — device tree left, SSDP message log right.
- **FR-002:** Tree shape — device → service → action.
- **FR-009:** Friendly-name labels — each device row labelled with description's `<friendlyName>`.
- **FR-010:** Friendly-name fallback — `uuid:<uuid>` when description has no `<friendlyName>`; failed-fetch devices are hidden, not relabelled.
- **FR-011:** Service enumeration on device expansion — display every service in `<serviceList>` (with embedded children flattened) on expand; MUST NOT trigger a second HTTP fetch.
- **FR-013:** Inline error placeholder on enumeration failure — surface enumeration failure inline without crashing or affecting siblings.
- **FR-044:** Persistent expand chevron via "Loading…" placeholder — every async-children node carries a placeholder child from the moment the node is added so the chevron renders immediately.
- **FR-045:** Kind glyphs in front of node labels — small glyph per kind (device / service / action), drawn from a Windows-shipped font; visually distinct enough to identify kind without reading the label.
- **FR-051:** Device row secondary detail line — muted secondary line below friendly name with (a) `deviceType` URN tail and (b) IPv4 host:port from `LOCATION`, separated by middle-dot.

**4.4 Service & Action Enumeration (Lazy SCPD)**

- **FR-012:** Action enumeration on service expansion — fetch service's SCPD on expand; display every action from `<actionList>` as child nodes; "Loading…" placeholder during fetch.
- **FR-100:** Incremental SCPD parse — UI never blocked — actions MAY appear as parsed; no UI-thread freeze beyond no-blocking budget for 100-action SCPDs.

**4.5 SSDP Message Log**

- **FR-003:** Right pane is a scrolling SSDP log — newer entries at the top.
- **FR-014:** Alive log entries — for every `NTS: ssdp:alive`, insert row at top showing timestamp + `ALIVE` + UUID.
- **FR-015:** Byebye log entries — for every `NTS: ssdp:byebye`, insert row at top showing timestamp + `BYEBYE` + UUID.
- **FR-016:** SSDP log cap with FIFO eviction — capped at 10,000 entries; oldest discarded on overflow.
- **FR-055:** Newest-first ordering with smart auto-follow — auto-follow only while operator is at (or near) the top; do not yank to top when scrolled away.
- **FR-101:** Virtualised log rendering — item-virtualised scrolling; sustained high advertisement rates produce no visible stutter and no full-pane repaints.

**4.6 XML Viewing**

- **FR-017:** Right-click device → Fetch description XML — present context menu.
- **FR-018:** Right-click service → Fetch service XML / Subscribe — present context menu with both options.
- **FR-019:** Open device XML in default browser — choosing "Fetch XML" opens device description in default browser.
- **FR-020:** Open service XML (SCPD) in default browser — choosing "Fetch service XML" opens SCPD in default browser.

**4.7 Device Properties Window**

- **FR-052:** Read-only Properties window — right-click → Properties… on device opens a read-only window with Identity / Manufacturer / Network / Discovery history / Embedded devices sections; remains closeable if device removed mid-view (FR-037).

**4.8 Rescan**

- **FR-021:** Rescan menu command — "Rescan" under "View" menu.
- **FR-022:** Rescan uses identical M-SEARCH semantics — same `ST: upnp:rootdevice` as startup (FR-004).
- **FR-023:** Rescan-prune of non-responders — after MX, remove devices that did not respond.
- **FR-024:** Rescan does not suspend live listening — unsolicited alive/byebye continues to be handled.

**4.9 Action Invocation**

- **FR-025:** Open invocation popup on action double-click.
- **FR-026:** Editable input arguments — list every input argument with editable field.
- **FR-027:** Invoke control sends SOAP request — POST SOAP action to `<controlURL>`.
- **FR-028:** Success result display — every output argument with value.
- **FR-029:** UPnP fault display — HTTP status code, UPnP error code, UPnP fault description.
- **FR-030:** Transport-error display — diagnostic information without crashing.
- **FR-031:** Argument-less actions — handle actions with no inputs and/or no outputs.
- **FR-102:** Enumerated input arguments via SCPD `<allowedValueList>` — constrained selector populated in declared order; `<defaultValue>` honoured when member of list; malformed list → free-form fallback + Warning diagnostic.
- **FR-103:** Numeric input arguments via SCPD `<allowedValueRange>` — constrained numeric input bounded by `<minimum>`/`<maximum>`, advancing by `<step>`; `<defaultValue>` honoured; malformed range → free-form fallback + Warning diagnostic.

**4.10 Service Subscription (GENA)**

- **FR-032:** Open subscription popup and SUBSCRIBE — `CALLBACK` URL points at currently-selected adapter's IPv4 + local callback host port.
- **FR-033:** Event list and "Latest property values" summary — newest-first event list capped at ~5,000 with FIFO tail eviction; anchored property-value summary above the list with overwrite-in-place semantics per property name.
- **FR-034:** UNSUBSCRIBE on popup close.
- **FR-035:** Failed-subscription handling — inform operator; MUST NOT send UNSUBSCRIBE for a subscription that never existed.
- **FR-036:** Multiple concurrent subscription popups across different services.
- **FR-038:** Subscription auto-renewal — renew with `SID` before each device-granted `TIMEOUT`; lapse on refused renewal; MUST NOT attempt UNSUBSCRIBE for expired subscription.
- **FR-104:** Non-serial NOTIFY processing per subscription — one slow / malformed NOTIFY MUST NOT block subsequent events; per-subscription queues are bounded with FIFO tail eviction.

**4.11 Network Adapter Selection**

- **FR-048:** Single adapter at a time, radio-list switch — default = first eligible adapter at startup; `View → Network adapter` radio list lists every eligible adapter; zero-adapter host runs with empty tree + Warning diagnostic.
- **FR-049:** TcpListener callback host — no URL ACL, no Admin — bind via `System.Net.Sockets.TcpListener` to selected adapter's IPv4; hand-parsed HTTP/1.1; size-bounded headers/body; per-request read timeout; 400 on malformed framing.
- **FR-050:** Atomic adapter-switch rebind — stop transport + callback host → clear registry → cancel in-flight fetches → notify open popups → rebind on new adapter → re-run startup discovery; MUST NOT block UI thread; within Performance Budget.

**4.12 Diagnostics**

- **FR-039:** Record structured diagnostic entries — timestamp + severity + context (device UUID, service id, action name, URL, status code, error text) for every internal error category.
- **FR-040:** Bounded rolling log file — per-user location (e.g. under `%LOCALAPPDATA%`); size-based rollover with small fixed number of rotated files.
- **FR-041:** In-memory diagnostic buffer and live viewer — bounded ring buffer exposed via `View → Diagnostics`; viewer remains responsive; live update; surfaces Identity and Endpoint columns resolved at arrival (snapshot semantics).
- **FR-042:** Diagnostic logging discipline — MUST NOT block UI thread; MUST NOT prevent startup on log-file failure (ring + viewer continue, single warning shown); MUST NOT include sensitive data.

**4.13 Secondary Window Lifecycle**

- **FR-037:** Open popups survive device disappearance — popups inform operator that device is unreachable and remain closeable without errors.
- **FR-046:** Main-window-owned popups — every secondary window visually owned by main window (z-order, no-push-behind on focus, minimise/restore together, close-with-parent); ownership is z-order + lifetime, not modality.

### NonFunctional Requirements

**Reliability**

- **NFR-R1:** No crashes during a typical 30-minute debugging session on a developer's network with normal real-world device misbehaviour (slow responders, mid-interaction byebye, partial NOTIFY, larger-than-typical SCPDs).
- **NFR-R2:** Slow-responding or misbehaving devices MUST NOT hang the UI. Bounded eager-fetch parallelism (FR-043) + per-request HTTP timeout (NFR-P2) + incremental SCPD parse (FR-100) are the enforcement mechanisms.
- **NFR-R3:** Open popups MUST recover cleanly when their device disappears mid-interaction (FR-037 restated as a cross-cutting expectation).
- **NFR-R4:** Diagnostic logging failure (e.g. log file path unwritable) MUST NOT prevent the app from running (FR-042).
- **NFR-R5:** Hosts with zero eligible network adapters MUST keep running (FR-048) — empty tree, Warning diagnostic, app remains interactive.

**Performance**

- **NFR-P1:** Virtualised rendering on all high-cardinality lists — SSDP log (FR-101), subscription event list, diagnostic viewer. Visible memory and per-frame cost MUST scale with visible-row count, not buffered-entry count.
- **NFR-P2:** Per-request HTTP timeout discipline — every outbound HTTP request (description fetch, SCPD fetch, SOAP invocation, SUBSCRIBE, UNSUBSCRIBE) bounded by per-request timeout; hung device MUST NOT stall fetch queue or freeze popup.
- **NFR-P3:** No UI-thread blocking — all network I/O async end-to-end; `.Result` / `.Wait()` forbidden; binding invariant verified by static analysis (`Microsoft.VisualStudio.Threading.Analyzers`).
- **NFR-P4:** Incremental large-SCPD parse — restatement of FR-100; same discipline anywhere unbounded XML is parsed.
- **NFR-P5:** Keyed, identity-tracked collection updates — no rebuild-on-change; single child fetch MUST NOT cause subtree redraw; single SSDP arrival MUST NOT cause full-pane repaint.
- **NFR-P6:** Bounded fan-out on discovery bursts — eager description fetch concurrency capped at 8; 50-device burst does NOT produce 50 concurrent HTTP requests.

**UI Polish**

- **NFR-UI1:** Modern WinUI 3 design conventions throughout — typographic hierarchy, spacing, colour. WinUI 3 design guidelines win conflicts.
- **NFR-UI2:** Considered visual hierarchy on tree rows — friendly name primary, secondary detail muted, glyph leading. No placeholder visuals.
- **NFR-UI3:** No flicker on incremental updates — no transient empty states (FR-044), no chevron disappear/reappear, no subtree redraw on label refresh.
- **NFR-UI4:** Smooth interaction in steady state on contemporary Windows hardware — no dropped frames during SSDP burst, large-SCPD expand, or rapid event arrival.

**Performance Budgets (verifiable scenarios — anchor for AC writing)**

- **SC-001:** Startup → every responsive device visible ≤ ~7 s (5 s MX + ≤ 2 s eager fetch).
- **SC-002:** 30-minute session — exactly one tree entry per UUID; zero duplicates.
- **SC-003:** `ssdp:byebye` → tree row removed typically < 2 s.
- **SC-004:** Service/action expansion → children visible ≤ 2 s typical.
- **SC-005:** "View XML" → default browser opens ≤ 2 s.
- **SC-009:** SSDP advertisement received → row visible ≤ 1 s.
- **SC-010:** Double-click action → invocation popup interactive ≤ 1 s.
- **SC-011:** Action invocation submitted → result visible ≤ 2 s (device < 1 s LAN latency).
- **SC-013:** 1-hour continuous operation — no memory exhaustion; SSDP log + diagnostic buffer remain bounded; on-disk log rolls.
- **SC-R-30min:** 30-min debugging session — 0 crashes, 0 UI hangs > 1 s, 0 unclosable popups after device disappearance.
- **Scale ceiling:** 8-hour session, 20 devices, 5 subscription popups, saturated SSDP log < 200 MB resident.
- **Warm SCPD expand:** ≤ 100 ms when description eager-fetched.
- **Cold large-SCPD expand:** ≤ 2 s for 100+-action SCPD, no UI freeze.
- **Sustained chatty-SSDP target:** ≥ 20 adv/s for ≥ 30 s without visible dropped frames or main-thread stalls > 16 ms.

### Additional Requirements

(Extracted from Architecture — implementation-time requirements that impact epic and story creation.)

**Starter scaffold (Architecture: Starter Template Evaluation + Decision 12):**

- `dotnet new winui` base + hand-rolled App/Core split: `src/ohSpy.App` (WinUI 3, `net10.0-windows10.0.19041.0`), `src/ohSpy.Core` (class library, `net10.0`, no `-windows` TFM), `tests/ohSpy.Core.Tests` (xUnit, `net10.0`).
- Solution file + project references; one-time `dotnet new` + `dotnet add reference` commands captured verbatim in Architecture §"Initialization Command".
- `<WindowsPackageType>None</WindowsPackageType>` + `<SelfContained>true</SelfContained>` + `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` (unpackaged WinUI 3 with bundled runtime).
- Bootstrap initialiser in `src/ohSpy.App/Program.cs` calling `Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize` before any WinUI type touched.

**Runtime stack pins (Architecture: Decision 12 + A3):**

- .NET 10 LTS pinned via `global.json`.
- Windows App SDK 2.1.3 (Stable).
- `CommunityToolkit.Mvvm` (8.4.x) for source-gen MVVM.
- `Microsoft.Extensions.{DependencyInjection, Logging, Options}` (10.0.x).
- xUnit + Moq + FluentAssertions + NetArchTest.Rules (Core.Tests).

**Build / quality infrastructure (Architecture: A3 + A4):**

- `Directory.Packages.props` at repo root — single source of truth for package versions (Central Package Management; `ManagePackageVersionsCentrally=true`).
- `Directory.Build.props` at repo root — `LangVersion=13`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest`, `AnalysisMode=recommended`.
- `Microsoft.VisualStudio.Threading.Analyzers` referenced from `Directory.Build.props` (VSTHRD002 / 003 / 100 enforce Pattern 6 async discipline).
- Project-local `src/ohSpy.Core/Directory.Build.props` — Core ↔ App boundary (no WinUI / WindowsAppSDK package reference in Core).
- `.editorconfig` — `dotnet new editorconfig` defaults (4-space indent, CRLF, UTF-8 no BOM); test-tree exemption for VSTHRD100.
- `global.json` — pin .NET SDK 10.0.x.

**No CI (Decision 12 + 13):**

- No `.github/workflows/`. Local-only `dotnet build` / `dotnet test`.
- `.githooks/pre-commit` runs the chaos test suite (~5 s budget) on every commit; `git config core.hooksPath .githooks` set during Story 1 init.
- Architecture leaves CI as a drop-in (future); nothing precludes it.

**Packaging (Decision 12):**

- `installer/ohSpy.iss` — InnoSetup 6 script (hand-authored, ~50 lines).
- Per-user install under `%LOCALAPPDATA%\Programs\ohSpy\`; no Administrator required; SmartScreen warning bypassed on first run (unsigned).
- MSBuild target `BuildInstaller` (in `ohSpy.App.csproj` or `Directory.Build.targets`) that depends on `Publish` and shells out to `ISCC.exe` to produce `installer/out/ohSpy-setup-<yyyy.MM.dd.HHmm>-x64.exe`.
- Self-contained publish bundles .NET runtime + Windows App Runtime; clean Windows 11 machine requires no pre-installed runtimes.
- Uninstaller registered under Apps & Features; removes install dir + Start Menu shortcut; preserves `%LOCALAPPDATA%\ohSpy\diagnostics\`.
- x64 publish profile primary; ARM64 publish profile exists in the project (`Properties/PublishProfiles/`); ARM64 installer is a deferred follow-up.

**Test infrastructure (Architecture: Project Structure + Decisions 3 / 4 / 6):**

- Mirror-tree `tests/ohSpy.Core.Tests/` matching `src/ohSpy.Core/` folder layout.
- `Fakes/`:
  - `TestHttpMessageHandler` — hand-rolled `HttpMessageHandler` for `IUpnpHttpClient` unit tests.
  - `FakeUpnpDevice` — in-process Kestrel server on `127.0.0.1:0` with failure-injection modes: `Happy`, `HangBeforeHeaders`, `HangAfter200Ok`, `SlowDripBody`, `GiantScpd`, `ChunkedThenAbort`, `FaultResponse`, `WrongContentLength`. (Murat's split: minimal modes in Story 2a; extended modes in Story 2b deferred until needed.)
  - `FakeGenaClient` — raw `TcpClient` driver for `EventCallbackHost` malformed-input tests.
  - `FakeSsdpTransport`.
  - `InlineUiDispatcher` — synchronous `IUiDispatcher` fake for `Core` unit tests.
- `Fixtures/`:
  - `Scpds/` — small (5-action Linn DS), medium (30-action third-party), large (200-action synthetic IGD), pathological (malformed mid-document, empty `<actionList>`, missing required fields, XXE attempt, deeply-nested noise).
  - `DeviceDescriptions/` — Linn DS, DLNA renderer, IGD router.
  - `NotifyPayloads/` — Linn volume update, DLNA transport state.
  - `SsdpDatagrams/` — alive, byebye, malformed.
- `Architecture/` — NetArchTest rules: `CoreAppBoundaryTests` (Pattern 2), `AsyncDisciplineTests`, `DiagCategoriesUsageTests` (Pattern 11).
- Test trait shape: `[Trait("ac", "AC-N.M")]` for AC-anchored tests; `[Trait("category", "integration|chaos|soak")]` for categorisation.

**Cross-cutting implementation patterns (Architecture: Implementation Patterns & Consistency Rules):**

- `IUiDispatcher` (Decision 1) — interface in `Core`, `WinUiDispatcher` impl in `App`, `InlineUiDispatcher` in tests; `AssertOnUiThread()` throws in Release.
- `BoundedObservableCollection<T>` + `IdentityKeyedSortedCollection<TIdentity, TItem>` (Decision 6) — UI-thread-owned identity-tracked collection primitives; `Move(old, new)` for sort-key change (not `Remove`+`Add`).
- Cancellation hierarchy (Decision 7) — app → adapter → device → popup CTS; cleanup operations use level-above token.
- Diagnostic emission discipline (Decision 8 + Pattern 11) — typed `IDiagnosticEmitter` over MEL; categories from `DiagCategories.*` constants; structured `DiagnosticContext`; mandatory fields per category family.
- `LoadingPlaceholderViewModel` + `InlineErrorViewModel` (Amendment A1) — atomic `ReplaceWith(realChildren)` on every async-children VM; no remove-then-add.
- `IWindowOwnershipManager` (Decision 10) — Win32 `SetWindowLongPtr(GWLP_HWNDPARENT)` after `child.Activate()`; canonical pattern across all four popup creation sites.
- `IUpnpHttpClient` typed facade + `HttpTimeoutOptions` (Decisions 3 + 11) — every operation uses linked CTS internally; `HttpClient.Timeout = Infinite`; `HttpCompletionOption.ResponseHeadersRead` + token-threaded `ReadAsStringAsync(ct)`; per-method body-size cap.
- `UpnpException` hierarchy (Amendment A5) — abstract `UpnpException` + `UpnpTimeoutException` / `UpnpTransportException` / `UpnpProtocolException` / `UpnpFaultException`; not `[Serializable]`.

**~70 architecture-level ACs (AC-1.x..AC-13.x) — referenced by story-level AC sets:**

- AC-3.x — `IUpnpHttpClient` behaviour (timeout, body cap, fault parsing, headers-vs-body cancellation, caller cancellation).
- AC-4.x — `EventCallbackHost` framing / size / timeout / connection-cap behaviour (9 ACs).
- AC-5.x — `IScpdParser` incremental streaming + XXE defense (5 ACs).
- AC-6.x — `BoundedObservableCollection` + `IdentityKeyedSortedCollection` semantics (6 ACs).
- AC-7.x — Cancellation hierarchy behaviour (5 ACs).
- AC-8.x — Diagnostic emit / sink / column-resolution behaviour (8 ACs).
- AC-9.x — `DescriptionFetchState` machine + registry-event surface (7 ACs).
- AC-10.x — Window ownership across the 4 popup types (5 ACs).
- AC-11.x — `HttpTimeoutOptions` defaults / override / keep-alive (4 ACs).
- AC-12.x — Installer / publish / uninstaller / clean-machine launch (6 ACs).
- AC-13.x — Pre-commit chaos hook behaviour (4 ACs).
- AC-A1.x — `LoadingPlaceholderViewModel` / `InlineErrorViewModel` / atomic `ReplaceWith` (5 ACs).
- AC-A2.x — `[Trait("ac", ...)]` shape (1 AC).

These are inherited verbatim into the relevant stories.

### UX Design Requirements

_Not applicable._ Per PRD §2.2 / §7, persona work, visual design system, and branding are explicit Non-Goals for v1. WinUI 3 design guidelines (NFR-UI1) are the only "design" anchor. No separate UX Design specification was authored.

### FR Coverage Map

Every FR (FR-001..FR-055, FR-100..FR-104) is allocated to one or more epics. Joint-delivered FRs (FR-037, FR-039, FR-046, FR-048) are noted explicitly.

| FR | Epic(s) | Brief |
|---|---|---|
| FR-001 | E2 | Two-pane layout |
| FR-002 | E2 | Tree shape (device → service → action) |
| FR-003 | E2 | Right pane scrolling SSDP log |
| FR-004 | E2 | Active SSDP discovery on startup |
| FR-005 | E2 | Tree entry per responding root device |
| FR-006 | E2 | Continuous unsolicited-NOTIFY listening |
| FR-007 | E2 | UUID-keyed device identity |
| FR-008 | E2 | Removal on byebye |
| FR-009 | E2 | Friendly-name labels |
| FR-010 | E2 | `uuid:<uuid>` fallback |
| FR-011 | E2 | Service enumeration on device expansion |
| FR-012 | E2 | Action enumeration on service expansion |
| FR-013 | E2 | Inline error placeholder |
| FR-014 | E2 | Alive log entries |
| FR-015 | E2 | Byebye log entries |
| FR-016 | E2 | SSDP log 10 K FIFO cap |
| FR-017 | E2 | Right-click device → Fetch description XML |
| FR-018 | E2 | Right-click service → Fetch service XML / Subscribe (menu surface in E2; Subscribe handler hooked in E4) |
| FR-019 | E2 | Open device XML in default browser |
| FR-020 | E2 | Open SCPD in default browser |
| FR-021 | E5 | View → Rescan command |
| FR-022 | E5 | Rescan uses identical M-SEARCH |
| FR-023 | E5 | Rescan-prune of non-responders |
| FR-024 | E5 | Rescan does not suspend live listening |
| FR-025 | E3 | Open invocation popup on action double-click |
| FR-026 | E3 | Editable input arguments |
| FR-027 | E3 | Invoke sends SOAP request |
| FR-028 | E3 | Success result display |
| FR-029 | E3 | UPnP fault display |
| FR-030 | E3 | Transport-error display |
| FR-031 | E3 | Argument-less actions |
| FR-032 | E4 | Open subscription popup + SUBSCRIBE |
| FR-033 | E4 | Event list + "Latest property values" summary |
| FR-034 | E4 | UNSUBSCRIBE on popup close |
| FR-035 | E4 | Failed-subscription handling |
| FR-036 | E4 | Multiple concurrent subscription popups |
| FR-037 | **E2 + E3 + E4** | Open popups survive device disappearance — joint delivery: Properties (E2), Invocation (E3), Subscription (E4) |
| FR-038 | E4 | Subscription auto-renewal |
| FR-039 | **E1 + E2 + E3 + E4 + E5** | Record structured diagnostic entries — emitter infrastructure in E1; emission added at every error path through E2-E4; viewer in E5 |
| FR-040 | E1 | Bounded rolling log file (file sink infrastructure) |
| FR-041 | E5 | In-memory buffer + live viewer (ring sink is E1; viewer UI is E5) |
| FR-042 | E1 | Logging discipline (non-blocking + startup-tolerant contract) |
| FR-043 | E2 | Async bounded eager description fetch |
| FR-044 | E2 | "Loading…" placeholder + chevron |
| FR-045 | E2 | Kind glyphs |
| FR-046 | E2 | Main-window-owned popups (introduced w/ Properties; pattern reused in E3/E4/E5) |
| FR-047 | E2 | Hide-until-loaded tree visibility |
| FR-048 | **E2 + E5** | Single-adapter operation — startup-default + zero-adapter tolerance in E2; radio-list switch UI in E5 |
| FR-049 | E4 | TcpListener callback host (needed by SUBSCRIBE CALLBACK URL) |
| FR-050 | E5 | Atomic adapter-switch rebind |
| FR-051 | E2 | Device row secondary detail line |
| FR-052 | E2 | Read-only Properties window |
| FR-053 | E2 | Root-only registration |
| FR-054 | E2 | Case-insensitive sort with stable identity |
| FR-055 | E2 | Newest-first + smart auto-follow |
| FR-100 | E2 | Incremental SCPD parse |
| FR-101 | E2 | Virtualised log rendering |
| FR-102 | E3 | `<allowedValueList>` selector |
| FR-103 | E3 | `<allowedValueRange>` numeric input |
| FR-104 | E4 | Non-serial NOTIFY processing |

**NFR allocation summary:**

- **NFR-R1** (30-min no-crash) — E6 (verification)
- **NFR-R2** (slow devices don't hang UI) — E1 (infrastructure: timeouts + analyzer + chaos test); applied throughout E2-E5
- **NFR-R3** (popups recover) — E2 + E3 + E4 (per-popup FR-037 work)
- **NFR-R4** (diagnostic logging failure doesn't block startup) — E1 (file sink contract)
- **NFR-R5** (zero-adapter host runs) — E2 (startup); E5 (switch path)
- **NFR-P1** (virtualised lists) — E2 (SSDP log + tree); E4 (subscription event list); E5 (diagnostic viewer)
- **NFR-P2** (per-request HTTP timeout) — E1 (UpnpHttpClient + HttpTimeoutOptions); applied throughout
- **NFR-P3** (no UI-thread blocking) — E1 (analyzer + chaos hook)
- **NFR-P4** (incremental large-XML parse) — E2 (SCPD parsing)
- **NFR-P5** (identity-tracked collections) — E1 (primitives); applied throughout
- **NFR-P6** (bounded discovery fan-out) — E2 (eager-fetch semaphore)
- **NFR-UI1..4** — E2-E5 application; E6 manual verification
- **Performance Budgets (SC-*)** — verified across E2-E5; soak-bar items (SC-013, SC-R-30min, Scale ceiling) in E6

## Epic List

### Epic 1: Project Foundation & Test Infrastructure

A working build pipeline that a developer can clone-and-build on Windows. `dotnet build` is green, `dotnet test` passes a first chaos test, `dotnet build -t:BuildInstaller` produces an installer that runs on a clean Windows 11 box. No UPnP behaviour yet — the foundations every subsequent epic builds on.

**FRs covered:** FR-040, FR-042 (foundational); FR-039 (emitter + sink infrastructure — emission added throughout subsequent epics).

**NFRs covered:** NFR-P3 (analyzer pinned), NFR-R4 (file-sink failure tolerance), NFR-P5 (collection primitives), NFR-P2 (HTTP timeout infrastructure), NFR-R2 (chaos test discipline).

**Architecture decisions delivered or scaffolded:** Project structure + tree (Architecture §Project Structure); D1 (`IUiDispatcher`); D3 (`IUpnpHttpClient` + linked-CTS pattern); D5 (`IScpdParser` + `IDeviceDescriptionParser` with XXE defence); D6 (`BoundedObservableCollection`, `IdentityKeyedSortedCollection`); D8 (`IDiagnosticEmitter`, `DiagCategories` constants, ring sink, file sink); D11 (`HttpTimeoutOptions`); D12 (no-CI, InnoSetup installer, MSBuild `BuildInstaller` target, unpackaged WinUI 3 + bootstrap); D13 (`.githooks/pre-commit` chaos hook); A3 (`Directory.Packages.props`); A4 (`Directory.Build.props` + analyzer); A5 (`UpnpException` hierarchy).

### Epic 2: Device Discovery & Tree Browsing

Launch ohSpy → see every UPnP root device on the network populate within ~7 s, with friendly name + deviceType-tail + host:port. Expand a device to see its services with kind-glyphs and persistent "Loading…" chevrons. Expand a service to see its actions (lazy, incremental — large IGD SCPDs never freeze the UI). Watch the SSDP log fill in real time (10 K virtualised entries, smart auto-follow). Right-click → open description / SCPD XML in default browser. Right-click → Properties opens a read-only Properties window. Embedded children flatten into root; sort is stable across re-announces; failed-fetch devices don't appear.

**FRs covered:** FR-001, 002, 003, 004, 005, 006, 007, 008, 009, 010, 011, 012, 013, 014, 015, 016, 017, 018, 019, 020, 043, 044, 045, 047, 051, 052, 053, 054, 055, 100, 101 + FR-046 (introduced w/ Properties; reused thereafter) + FR-048 half-A (default-to-first-eligible at startup + zero-adapter tolerance) + FR-037 half (Properties window survival) + FR-039 emission at SSDP / Description / SCPD error paths.

**NFRs covered:** NFR-P1 (virtualised SSDP log + tree), NFR-P2 applied to description / SCPD fetches, NFR-P4 (incremental SCPD), NFR-P5 (identity-tracked tree + log), NFR-P6 (eager-fetch semaphore = 8), NFR-UI2/UI3 (tree row hierarchy + atomic placeholder replacement), NFR-R5 (zero-adapter startup).

**Architecture decisions delivered:** D2 (SSDP socket topology + channel); D7 (cancellation hierarchy: app/adapter/device scopes); D9 (`DescriptionFetchState` machine + `RegistryEntry` + `IDeviceRegistry` + `EagerDescriptionDispatcher`); D10 (`IWindowOwnershipManager` first use); A1 (`LoadingPlaceholderViewModel` + `InlineErrorViewModel` + atomic `ReplaceWith`).

### Epic 3: Action Invocation

Double-click an action → invocation popup opens with editable input fields. Inputs with `<allowedValueList>` render as constrained dropdowns; numeric inputs with `<allowedValueRange>` render as constrained spinners honouring `<step>`. Invoke → success shows output args, UPnP fault shows error code + description, transport error shows diagnostic info — without crashing. Argument-less actions invoke cleanly. Popup remains closeable if the device disappears mid-invocation; popup is z-order-owned by the main window.

**FRs covered:** FR-025, 026, 027, 028, 029, 030, 031, 102, 103 + FR-037 half (invocation popup survives) + FR-046 reuse (invocation popup adopted by main window) + FR-039 emission at SOAP transport / fault paths.

**NFRs covered:** NFR-R3 (popup recovery), NFR-P2 applied to SOAP invocation.

**Architecture decisions applied:** D7 (popup-level cancellation token derived from device token); D10 (popup window-ownership pattern reused).

### Epic 4: GENA Subscription

Right-click → Subscribe opens a subscription popup. SUBSCRIBE goes out with a `CALLBACK` URL pointing at the in-process callback host on the selected adapter. NOTIFY events stream into the popup's newest-first list (~5 K cap, FIFO eviction) with a "Latest property values" summary anchored above. Multiple subscriptions across services run concurrently and independently. One slow / malformed NOTIFY does not block others. Auto-renew before timeout; UNSUBSCRIBE on close; lapsed subscriptions handled cleanly; failed subscribe is reported without an UNSUBSCRIBE attempt. Callback host is hardened (size caps, slowloris defence, connection cap = 8, no Admin / URL ACL).

**FRs covered:** FR-032, 033, 034, 035, 036, 038, 049, 104 + FR-037 half (subscription popup survives) + FR-046 reuse + FR-039 emission at GENA SUBSCRIBE / RENEW / UNSUBSCRIBE / callback-parse paths.

**NFRs covered:** NFR-R3 (popup recovery), NFR-P1 (subscription event list virtualisation), NFR-P2 applied to SUBSCRIBE / UNSUBSCRIBE.

**Architecture decisions delivered:** D4 (`IEventCallbackHost` + `HttpRequestParser` + `TimeoutStream` — pragmatic strict-framing + lenient-headers); D7 (UNSUBSCRIBE-on-close uses adapter-level token, not cancelled popup token — the cleanup-uses-level-above invariant).

### Epic 5: Operator Tooling — Diagnostics, Adapter Switch, Rescan

`View → Diagnostics` opens a live diagnostic viewer with Identity / Endpoint columns resolved at arrival (snapshot semantics). `View → Network adapter` lists every eligible IPv4 adapter as radio items; selecting a different adapter atomically rebinds (tear down SSDP + callback host, clear registry, cancel in-flight fetches, notify open popups, rebind, re-discover). `View → Rescan` re-runs the M-SEARCH and prunes non-responders without suspending live NOTIFY handling.

**FRs covered:** FR-021, 022, 023, 024, 041 (viewer UI), 050 + FR-048 half-B (radio-list switch UI) + FR-039 final delivery (viewer surfaces structured entries emitted throughout build).

**NFRs covered:** NFR-R5 (zero-adapter handling through switch path), NFR-R3 (adapter switch's notify-open-popups cascade), NFR-P1 (diagnostic viewer virtualisation).

**Architecture decisions delivered:** D7 atomic adapter-switch sequence (cancel cascade → drain → rebind → re-discover); FR-041 viewer column resolution rules (Decision 8 final integration).

### Epic 6: Polish, Soak & Release Readiness

A built installer that lands cleanly on a fresh Windows 11 machine, runs the 30-min no-crash debugging session, holds the 8-hour 200 MB scale ceiling under load, and demonstrates the FR-044 / FR-046 / FR-054 manual UI behaviours that red-green-refactor TDD can't enforce. Performance Budgets (SC-*) verified end-to-end. Ready for L&L.

**FRs covered:** _(none new — verification-only across all features delivered in E1-E5)_.

**NFRs covered:** NFR-R1 (30-min no-crash soak), NFR-UI1/UI2/UI3/UI4 (manual UI verification), NFR-P3/P5 long-run verification, all Performance Budget SC-* targets verified end-to-end, AC-12.4 clean-machine install + AC-13.x chaos-hook regression discipline.

**Architecture decisions delivered:** Soak / manual UI / clean-machine install verification per Architecture §"Implementation Handoff → Polish & Soak story (before release) — Murat's recommendation".

---

## Epic 1: Project Foundation & Test Infrastructure

A working build pipeline that a developer can clone-and-build on Windows. `dotnet build` is green, `dotnet test` passes a first chaos test, `dotnet build -t:BuildInstaller` produces an installer that runs on a clean Windows 11 box. No UPnP behaviour yet — the foundations every subsequent epic builds on.

### Story 1.1: Project Scaffold & Build/Test/Installer Pipeline

As an ohSpy developer,
I want a clone-and-build .NET 10 / WinUI 3 solution wired to an InnoSetup installer pipeline and a pre-commit chaos hook,
So that I can write subsequent stories against a stable foundation with one-step `build`, `test`, and `package` commands.

**Acceptance Criteria:**

**Given** a fresh clone of the repository on Windows 11 with .NET 10 SDK + Visual Studio 2026 + InnoSetup 6 installed
**When** I run `dotnet build` from the repo root
**Then** the solution containing `ohSpy.App`, `ohSpy.Core`, and `ohSpy.Core.Tests` builds without warnings
**And** `TreatWarningsAsErrors=true` is enforced via `Directory.Build.props` (A4)
**And** `LangVersion=13`, `Nullable=enable`, `ImplicitUsings=enable` are configured solution-wide
**And** `Microsoft.VisualStudio.Threading.Analyzers` is referenced via `Directory.Build.props` (A4) and active in every project

**Given** the solution is built
**When** I run `dotnet test`
**Then** the test runner discovers `ohSpy.Core.Tests` and reports 0 failures (zero or more tests, all green)

**Given** I want to package the app
**When** I run `dotnet build src/ohSpy.App -t:BuildInstaller -p:Configuration=Release`
**Then** the target depends on `Publish` and produces `installer/out/ohSpy-setup-<yyyy.MM.dd.HHmm>-x64.exe` (AC-12.2)
**And** the installer script `installer/ohSpy.iss` is present and committed
**And** the publish profile bundles the .NET 10 runtime AND the Windows App Runtime via `<SelfContained>true</SelfContained>` + `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` (D12)
**And** `<WindowsPackageType>None</WindowsPackageType>` is set in `ohSpy.App.csproj` (AC-12.6)
**And** the build succeeds for the `win-x64` RID with the `win-arm64` publish profile also present in `Properties/PublishProfiles/` but not built by default

**Given** the installer artifact exists
**When** I run it on a clean Windows 11 machine with no .NET 10 or WindowsAppRuntime pre-installed (AC-12.4)
**Then** the installer installs to `%LOCALAPPDATA%\Programs\ohSpy\` per-user with no Administrator prompt (AC-12.3)
**And** the app launches and shows an empty WinUI 3 window after the user clicks past the SmartScreen warning
**And** `Bootstrap.TryInitialize` runs in `Program.cs` before any WinUI type is touched (AC-12.6)
**And** the bootstrap failure path is wired (native message box + exit) for the case where runtime binding fails

**Given** I uninstall via Apps & Features
**When** the uninstaller runs
**Then** the install dir and Start Menu shortcut are removed (AC-12.5)
**And** `%LOCALAPPDATA%\ohSpy\diagnostics\` is preserved (no diagnostic content yet, but the directory survives if present)

**Given** the repository is cloned fresh
**When** I run the Story 1 init steps that configure the chaos hook
**Then** `git config core.hooksPath .githooks` has been set as part of the documented init flow (AC-13.2)
**And** `.githooks/pre-commit` exists, is executable, and contains the chaos-test shell command (AC-13.1)
**And** committing a change runs the pre-commit hook (currently passing trivially because no chaos tests exist yet — full chaos-test integration lands in Story 1.6)

**Given** I look at root-level configuration
**When** I inspect the repo
**Then** `Directory.Packages.props` (A3) is present at the repo root with `ManagePackageVersionsCentrally=true` and pins for every dependency the architecture names
**And** `global.json` pins the .NET SDK to 10.0.x
**And** `.editorconfig` carries the `dotnet new editorconfig` defaults
**And** `.gitignore` covers `bin/`, `obj/`, `installer/out/`, and any other standard .NET ignores

---

### Story 1.2: UI Dispatcher Contract & Collection Primitives

As an ohSpy developer,
I want the `IUiDispatcher` thread-marshalling contract plus the two identity-tracked observable collection primitives that virtualised lists will bind to,
So that subsequent stories can write thread-safe, identity-stable, redraw-free collection updates with one consistent pattern instead of re-deriving the rules each time.

**Acceptance Criteria:**

**Given** the `IUiDispatcher` interface in `ohSpy.Core/Threading/IUiDispatcher.cs`
**When** I look at its surface
**Then** it exposes `Post(Action)`, `PostAsync<T>(Func<T> readback)`, `IsOnUiThread`, and `AssertOnUiThread()` (D1)
**And** `AssertOnUiThread()` throws `InvalidOperationException` in Release as well as Debug — this is a coding-error invariant, not a debug aid (D1)

**Given** `ohSpy.App/Windowing/WinUiDispatcher.cs`
**When** I read the impl
**Then** it wraps `Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()` captured during App startup on the UI thread
**And** `Post` forwards to `_queue.TryEnqueue`
**And** `PostAsync` returns a `TaskCompletionSource`-backed `Task<T>` posted via `TryEnqueue`
**And** `IsOnUiThread` reads `_queue.HasThreadAccess`

**Given** `tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs`
**When** unit tests use it
**Then** `Post(Action a)` executes `a()` synchronously
**And** `PostAsync` runs the readback inline
**And** `IsOnUiThread` returns `true`
**And** `AssertOnUiThread()` no-ops

**Given** `ohSpy.Core/Collections/BoundedObservableCollection<T>.cs`
**When** I call `PrependNewest(item)` at capacity
**Then** the collection emits exactly two `INotifyCollectionChanged` notifications — `Add(index=0)` and `Remove(index=Count)` — and NEVER `Reset` (AC-6.1)
**And** 100,000 sequential `PrependNewest` calls on a 10,000-capacity collection complete in O(N) total wall time with zero `Reset` notifications (AC-6.2)
**And** the backing store is a ring buffer (`T[]` of capacity) so `PrependNewest` is O(1) — no list shift, no array copy
**And** `Clear()` emits a single `Reset` notification (AC-6.6)
**And** indexed access `this[0]` returns the newest item; `this[Count-1]` returns the oldest

**Given** `ohSpy.Core/Collections/IdentityKeyedSortedCollection<TIdentity, TItem>.cs`
**When** I call `Update(item)` with the sort key unchanged
**Then** no `INotifyCollectionChanged` notification is emitted (AC-6.3)

**Given** the same collection
**When** I call `Update(item)` with the sort key changed
**Then** exactly one `Move(old, new)` notification is emitted (AC-6.4) — never `Remove`+`Add`
**And** the underlying item instance is preserved across the migration so any UI selection/expansion state bound to that node survives (AC-6.5 verified via integration test if WinUI test infrastructure exists, otherwise via collection-level identity assertion)
**And** the backing store is `List<TItem>` + `Dictionary<TIdentity, int>` for O(1) identity-lookup

**Given** both primitives are used cross-thread
**When** any mutation is attempted off the UI thread
**Then** the call surfaces the dispatcher-violation contract appropriately (these collections are UI-thread-owned; cross-thread mutations are expected to marshal through `IUiDispatcher`)

**Given** the DI composition root
**When** the App starts
**Then** `IUiDispatcher` is registered as a singleton via `ServiceRegistration.RegisterServices` (Pattern 7) with `WinUiDispatcher` as the implementation

---

### Story 1.3: UPnP HTTP Client Facade with Per-Request Timeout Discipline

As an ohSpy developer,
I want a typed `IUpnpHttpClient` facade whose every method bakes a per-request timeout and a size cap into a linked CTS internally,
So that downstream stories cannot accidentally inherit `HttpClient`'s 100 s default timeout or leak hung sockets — closing the structural defect that traced to the prior tool's "slow devices hang the app" complaint.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Http/UpnpExceptions.cs`
**When** I inspect the type hierarchy
**Then** `UpnpException` is `abstract` and never thrown directly (A5)
**And** four sealed derivatives exist: `UpnpTimeoutException`, `UpnpTransportException`, `UpnpProtocolException`, `UpnpFaultException` (A5)
**And** each carries the type-specific structured context (Url + Budget + Elapsed on Timeout; Url + StatusCode on Transport; Url on Protocol; Url + ActionName + ErrorCode + ErrorDescription on Fault) (A5)
**And** none of the types is `[Serializable]` (A5)

**Given** `ohSpy.Core/Http/HttpTimeoutOptions.cs`
**When** I read the defaults
**Then** they match Decision 11 exactly: `DescriptionFetch` 5 s, `ScpdFetch` 10 s, `SoapInvoke` 10 s, `GenaSubscribe` 5 s, `GenaUnsubscribe` 5 s, `ConnectTimeout` 5 s, `KeepAlivePingDelay` 15 s, `KeepAlivePingTimeout` 5 s, `CallbackHeaders` 5 s, `CallbackBody` 5 s (AC-11.1)
**And** the type is registered via `services.Configure<HttpTimeoutOptions>` in `ServiceRegistration` (AC-11.3)

**Given** `ohSpy.Core/Http/IUpnpHttpClient.cs`
**When** I inspect the interface
**Then** it declares `FetchDeviceDescriptionAsync`, `FetchScpdAsync`, `InvokeActionAsync`, `SubscribeAsync`, `RenewSubscriptionAsync`, `UnsubscribeAsync` — each taking `CancellationToken ct` as the last parameter
**And** `FetchScpdAsync` returns `Task<byte[]>` (raw SCPD body — parsing is a separate concern per Story 1.4 / D5 revision)

**Given** the `UpnpHttpClient` impl
**When** any method runs
**Then** the underlying `HttpClient` has `Timeout = Timeout.InfiniteTimeSpan` — the per-op linked CTS is the SOLE timeout source (AC-3.1 + AC-11.2)
**And** every call site composes `CancellationTokenSource.CreateLinkedTokenSource(externalToken, new CTS(_opts.<budget>))`
**And** every `SendAsync` uses `HttpCompletionOption.ResponseHeadersRead` AND threads the linked token through the body-read (`ReadAsStringAsync(linked.Token)`) so both header and body phases are timeout-covered (AC-3.5 closes the gap the prior tool had)
**And** the response body size is checked against the per-method cap from `HttpTimeoutOptions`/code constants before reading the body (description 1 MB, SCPD 2 MB, SOAP 1 MB, GENA 64 KB)
**And** `SocketsHttpHandler` is configured with `UseProxy=false`, `AllowAutoRedirect=false`, `ConnectTimeout = _opts.ConnectTimeout`, `KeepAlivePingDelay = _opts.KeepAlivePingDelay`, `KeepAlivePingTimeout = _opts.KeepAlivePingTimeout`, `MaxResponseHeadersLength = 16` (KB) (AC-11.4 covers KeepAlive surfaces hung TCP within 20 s ± 5 s)

**Given** the facade's exception-mapping discipline
**When** a per-op CTS fires (timeout)
**Then** a `UpnpTimeoutException` is thrown carrying Url + Budget + Elapsed
**And** a `Warning` diagnostic (`DiagCategories.HttpTimeout`) is emitted with Url + Elapsed + Budget context (test stub allowed if Story 1.5 hasn't shipped yet — production wiring comes after Story 1.5)

**When** the external (caller) token fires
**Then** `OperationCanceledException` propagates as-is — NOT wrapped in `UpnpTimeoutException` (AC-3.6)
**And** no diagnostic is emitted on caller-initiated cancellation

**When** `HttpRequestException` is raised by the underlying transport
**Then** `UpnpTransportException` is thrown carrying Url and (when present) StatusCode

**When** the body exceeds the per-method size cap
**Then** `UpnpProtocolException` is thrown and the response is disposed (AC-3.4)

**When** SOAP returns 500 with a `<s:Fault><detail><UPnPError><errorCode/>` body
**Then** `UpnpFaultException` is thrown carrying ActionName + ErrorCode + ErrorDescription (AC-3.3)

**Given** `SUBSCRIBE` / `UNSUBSCRIBE` semantics
**When** the facade calls those methods
**Then** the underlying `HttpRequestMessage.Method` is the exact string `"SUBSCRIBE"` or `"UNSUBSCRIBE"` (AC-3.2)

**Given** test infrastructure
**When** I look at `tests/ohSpy.Core.Tests/Fakes/TestHttpMessageHandler.cs`
**Then** it is a hand-rolled `HttpMessageHandler` (not Moq `Protected()`) reusable across `UpnpHttpClient` unit tests
**And** AC-3.1..AC-3.6 + AC-11.1..AC-11.3 are exercised by tests carrying `[Trait("ac", "AC-3.x")]` and `[Trait("ac", "AC-11.x")]`

---

### Story 1.4: XML Parsers — SCPD Streaming + Device Description with XXE Defence

As an ohSpy developer,
I want incremental SCPD streaming via `IAsyncEnumerable<ScpdAction>` and a device-description parser, both with XXE-locked `XmlReaderSettings`,
So that subsequent stories can parse arbitrary LAN device XML without freezing the UI on 200-action SCPDs and without exposing the host filesystem to malicious DTD entity attacks.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Scpd/IScpdParser.cs`
**When** I inspect the interface
**Then** it declares `IAsyncEnumerable<ScpdAction> StreamActionsAsync(Stream xml, CancellationToken ct)` and `Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct)` (D5)

**Given** `ohSpy.Core/Models/` ScpdAction / ScpdArgument / ScpdDirection / ScpdStateTable / ScpdStateVariable / ScpdAllowedValueRange
**When** I inspect them
**Then** they are `public sealed record` types with the shape defined in D5 (Pattern 9)

**Given** the parser's `XmlReaderSettings`
**When** any parse starts
**Then** the settings have `Async=true`, `DtdProcessing=DtdProcessing.Prohibit`, `XmlResolver=null`, `IgnoreComments=true`, `IgnoreWhitespace=true`, `MaxCharactersInDocument=4_000_000` (D5)
**And** the same settings are used by the device-description parser

**Given** a 200-action SCPD fixture in `tests/Fixtures/Scpds/igd-router-200action.xml`
**When** I `await foreach` over `StreamActionsAsync`
**Then** actions emit one-by-one as they parse (not as a single batch at the end) (AC-5.1)
**And** there is an `await Task.Yield()` between each emitted action (verifiable via consumer-side iteration timing — no individual iteration > 16 ms)
**And** total parse completes within ~2 s on the test baseline (AC-5.1 cold-large-SCPD budget)

**Given** a malformed SCPD fixture (`tests/Fixtures/Scpds/malformed-mid-document.xml`) that breaks at action N
**When** I `await foreach`
**Then** actions 0..N-1 are yielded successfully
**And** the next iteration throws `UpnpProtocolException` (AC-5.2)

**Given** an XXE-attempt fixture (`tests/Fixtures/Scpds/xxe-attempt.xml`) with a `<!DOCTYPE ... [<!ENTITY ...>]>` declaration
**When** I attempt to parse it
**Then** `UpnpProtocolException` is thrown (AC-5.3)
**And** no filesystem read happens (no entity is resolved; `XmlResolver = null`)

**Given** any in-progress streaming parse
**When** I cancel the `CancellationToken` mid-document
**Then** `OperationCanceledException` propagates at the next yield (AC-5.4)
**And** the `XmlReader` is disposed (via `using` in the parser impl)

**Given** the state-table parser
**When** I call `ReadStateTableAsync` over an SCPD that declares `<stateVariable>` entries with `<allowedValueList>`, `<allowedValueRange>`, and `<defaultValue>`
**Then** every state variable is parsed correctly and `ScpdStateTable.ByName` returns the right `ScpdStateVariable` for each name (AC-5.5)
**And** `ScpdAllowedValueRange.Step` is null when the SCPD omits `<step>`

**Given** `ohSpy.Core/Scpd/IDeviceDescriptionParser.cs` + `DeviceDescriptionParser.cs`
**When** I parse a typical device-description XML
**Then** the parser extracts `<friendlyName>`, `<deviceType>`, `<UDN>`, `<presentationURL>`, `<manufacturer>`, `<manufacturerURL>`, `<modelName>`, `<modelNumber>`, `<modelDescription>`, `<modelURL>`, `<serialNumber>`, `<UPC>`, `<serviceList>` (with `<service>` entries carrying `<serviceType>`, `<serviceId>`, `<SCPDURL>`, `<controlURL>`, `<eventSubURL>`), and `<deviceList>` (recursive — embedded children flattened per FR-053)
**And** the same XmlReaderSettings discipline applies

---

### Story 1.5: Diagnostic Emitter, Ring Sink, File Sink

As an ohSpy developer,
I want the typed `IDiagnosticEmitter` plus the in-memory ring sink and on-disk rolling file sink, with mandatory structured `DiagnosticContext` and a single source-of-truth `DiagCategories` constants file,
So that every error path emitted from subsequent stories lands in the live diagnostic stream + the rolling log file uniformly, with no per-call format drift and no UI-thread blocking.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Diagnostics/`
**When** I inspect the types
**Then** `DiagSeverity` is an enum of `Verbose | Information | Warning | Error`
**And** `DiagnosticEntry` is a `public sealed record` with `TimestampUtc`, `Severity`, `Category`, `Message`, `Context` (D8)
**And** `DiagnosticContext` is a `readonly record struct` with nullable `DeviceUuid`, `Url`, `RemoteEndpoint`, `ServiceId`, `ActionName`, `StatusCode`, `Elapsed`, `Budget`, `ErrorText`, `Sid` (D8)
**And** `DiagCategories` is a `static class` carrying every category as a `public const string` (D8 — exhaustive list across architecture decisions D2/D3/D4/D8/D9/D11/D12 plus Adapter.Switch.*)
**And** each category constant carries an XML doc comment naming the mandatory `DiagnosticContext` fields per Pattern 11

**Given** `IDiagnosticEmitter`
**When** I look at the interface
**Then** it declares `Verbose`, `Information`, `Warning`, `Error` — each `(string category, string message, DiagnosticContext context = default)` — D8

**Given** `DiagnosticEmitter` impl
**When** I emit any severity
**Then** the entry fans out simultaneously to (a) the MEL `ILogger` pipeline, (b) the ring sink via `IUiDispatcher.Post`, and (c) the file sink via channel-write (D8)
**And** the emit call returns within 100 µs (file write is deferred to background pump) (AC-8.8)
**And** `Verbose` calls below `MinSeverity` allocate zero `DiagnosticEntry` instances (AC-8.7 — verified via BenchmarkDotNet allocation tracking or similar)

**Given** `DiagnosticRingSink`
**When** entries arrive
**Then** the sink owns a `BoundedObservableCollection<DiagnosticRow>(5000)` (FR-041 cap)
**And** every `Push` marshals through `IUiDispatcher.Post` so the prepend happens on the UI thread
**And** `DiagnosticRow.IdentityLabel` resolves at arrival via the FR-041 rules: `null DeviceUuid` → `"—"`; registry lookup hit with friendly name → friendly name; registry hit without friendly name OR registry miss → `"uuid:<uuid>"` (AC-8.3)
**And** `DiagnosticRow.EndpointLabel` resolves at arrival via the FR-041 rules: parsed URL → `host` (default port) or `host:port` (non-default); fallback to `RemoteEndpoint`; final fallback `"—"` (AC-8.4)
**And** identity / endpoint resolution is snapshot-at-arrival — later registry changes do NOT update existing rows (FR-041)
**And** the ring sink's `Entries` is the SAME `BoundedObservableCollection<DiagnosticRow>` instance later bound by `DiagnosticsViewModel.Entries` in Epic 5 (AC-8.2 — no copy, no view layer)

**Given** `IDiagnosticFileSink` (interface in `Core`) + `DiagnosticFileSink` impl (in `App` — needs `%LOCALAPPDATA%`)
**When** the sink is started
**Then** it opens `%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-<yyyyMMdd>.log` for append-write
**And** writes are JSON-lines (keys `ts`, `sev`, `cat`, `msg`, `ctx`) via `System.Text.Json`
**And** the sink uses a `Channel<DiagnosticEntry>(capacity=1000, FullMode=DropOldest)` + background pump task

**Given** a full session of emits
**When** the on-disk log file reaches 2 MB
**Then** the sink rotates to a new file (oldest of 8 retained files is deleted on roll) — total on-disk footprint ≤ 16 MB (AC-8.5)

**Given** the diagnostic dir or file cannot be created at startup
**When** the file sink initialises
**Then** it emits ONE `Warning` via the ring sink (`DiagCategories.DiagnosticsFileSinkUnavailable`)
**And** subsequent `Push` calls silently no-op
**And** the app continues to run (AC-8.6 + FR-042)

**Given** the emitter is registered in DI
**When** `ServiceRegistration` runs
**Then** `IDiagnosticEmitter`, `IDiagnosticRingSink`, `IDiagnosticFileSink` are all registered as singletons (Pattern 7)

---

### Story 1.6: FakeUpnpDevice (Minimal Modes), First Chaos Test, NetArchTest Rules

As an ohSpy developer,
I want minimal test infrastructure — a 3-mode `FakeUpnpDevice` Kestrel fixture, the first chaos test that exercises `IUpnpHttpClient`'s timeout discipline against `HangAfter200Ok`, the chaos category trait, NetArchTest rules pinning the Core ↔ App boundary and async / `DiagCategories` discipline, and the pre-commit hook running the chaos suite,
So that the regression net is closed before Epic 2's protocol code lands and a future change that breaks `ResponseHeadersRead` or smuggles `.Result` into `Core` is caught before it merges.

**Acceptance Criteria:**

**Given** `tests/ohSpy.Core.Tests/Fakes/FakeUpnpDevice.cs`
**When** I inspect it
**Then** it is an in-process Kestrel server bound to `127.0.0.1:0` (ephemeral port)
**And** it exposes three failure modes for v1: `Happy` (normal 200 OK with canned body), `HangBeforeHeaders` (accept connection then never reply), `HangAfter200Ok` (write 200 OK headers then dangle the body — the regression test for the prior tool's eager-fetch-queue stall — D3)
**And** the fixture exposes `Uri DescriptionUrl` and `Uri ScpdUrl` for tests to point `IUpnpHttpClient` at

**Given** the first chaos test
**When** I run `dotnet test --filter "Trait=category&Value=chaos"`
**Then** at least one `[Fact]` with `[Trait("category", "chaos")]` and `[Trait("ac", "AC-3.5")]` runs against `HangAfter200Ok` and asserts `UpnpHttpClient.FetchScpdAsync` throws `UpnpTimeoutException` within the configured `ScpdFetch` budget ± 100 ms (AC-13.4 simulated NFR-P2 regression coverage)
**And** the test completes in well under the ~5 s pre-commit budget (D13)

**Given** the pre-commit chaos hook
**When** I run `git commit -m 'test'` after a change
**Then** `.githooks/pre-commit` runs `dotnet test --filter "Trait=category&Value=chaos"` and aborts the commit on any failure (AC-13.1)
**And** the chaos suite now actually has tests in it (vs. the trivially-passing state after Story 1.1)

**Given** a deliberately-broken `UpnpHttpClient` change (e.g. removing `HttpCompletionOption.ResponseHeadersRead`)
**When** I attempt to commit
**Then** the chaos hook fails the commit (AC-13.4)

**Given** a deliberately-broken Core-async change (`.Result` introduced)
**When** I build
**Then** the `Microsoft.VisualStudio.Threading.Analyzers` (VSTHRD002 / 003 / 100) emits a build error and the commit fails at the chaos-hook's `dotnet test` step (AC-13.3 — analyzer + chaos hook combine for the regression net)

**Given** `tests/ohSpy.Core.Tests/Architecture/CoreAppBoundaryTests.cs`
**When** the test runs
**Then** it uses NetArchTest to assert that `ohSpy.Core` types reference NO type in `Microsoft.UI.*`, `Microsoft.Windows.*` (WindowsAppSDK-specific), or `WinRT.Interop.*` (Pattern 2)
**And** it asserts that `ohSpy.Core` does NOT reference `ohSpy.App.*`

**Given** `tests/ohSpy.Core.Tests/Architecture/AsyncDisciplineTests.cs`
**When** the test runs
**Then** it asserts that `ohSpy.Core` declares no `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` usage that the analyzer doesn't already catch (defence in depth — Pattern 6)

**Given** `tests/ohSpy.Core.Tests/Architecture/DiagCategoriesUsageTests.cs`
**When** the test runs
**Then** it asserts every emit call site references `DiagCategories.<Name>` rather than an inline string literal (Pattern 11, D8 open-follow-up closed in this story)
**And** the test passes initially because there are zero emit call sites yet — but the rule is in place to catch future violations

**Given** the test class
**When** I inspect the trait pattern
**Then** every test satisfying an architecture AC carries `[Trait("ac", "AC-N.M")]` per Amendment A2 (AC-A2.1)

---

## Epic 2: Device Discovery & Tree Browsing

Launch ohSpy → see every UPnP root device on the network populate within ~7 s, with friendly name + deviceType-tail + host:port. Expand a device to see its services with kind-glyphs and persistent "Loading…" chevrons. Expand a service to see its actions (lazy, incremental — large IGD SCPDs never freeze the UI). Watch the SSDP log fill in real time (10 K virtualised entries, smart auto-follow). Right-click → open description / SCPD XML in default browser. Right-click → Properties opens a read-only Properties window. Embedded children flatten into root; sort is stable across re-announces; failed-fetch devices don't appear.

### Story 2.1: SSDP Transport — Multicast + Search Sockets with Bounded Channel

As a Linn engineer,
I want ohSpy to bind two adapter-specific UDP sockets (a multicast listener on `(adapter_ipv4, 1900)` plus an ephemeral search socket) and feed every received datagram into a bounded channel,
So that subsequent stories have a stable, source-tagged datagram stream to parse — independent of how many devices are announcing and resistant to back-pressure from a slow consumer.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Models/SsdpDatagram.cs` and `ohSpy.Core/Models/SsdpSource.cs`
**When** I inspect them
**Then** `SsdpDatagram` is a `public sealed record` with `IPEndPoint Remote`, `byte[] Payload`, `DateTime ArrivalUtc`, `SsdpSource Source` (D2)
**And** `SsdpSource` is an `enum { Multicast, SearchResponse }` (D2)

**Given** `ohSpy.Core/Discovery/ISsdpTransport.cs`
**When** I inspect the interface
**Then** it declares `Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)`, `Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)`, `ChannelReader<SsdpDatagram> IncomingDatagrams { get; }`, and is `IAsyncDisposable` (D2)

**Given** `ohSpy.Core/Discovery/SsdpTransport.cs` impl
**When** `StartAsync(adapterIPv4, ct)` runs
**Then** the multicast listener socket is created with `AddressFamily.InterNetwork`, `SocketType.Dgram`, `ProtocolType.Udp`
**And** `SocketOptionName.ReuseAddress` is set BEFORE binding (mandatory — coexists with Windows `SSDPSRV`) (D2)
**And** the socket binds to `IPEndPoint(adapterIPv4, 1900)`
**And** it joins the multicast group `239.255.255.250` on `adapterIPv4` via `SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(...))` (D2)
**And** the ephemeral search socket is created similarly and bound to `IPEndPoint(adapterIPv4, 0)` with `MulticastInterface` set to `adapterIPv4`
**And** receive loops on both sockets post datagrams to the channel with the correct `SsdpSource` tag

**Given** the bounded channel
**When** I look at its configuration
**Then** it is `Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false })` (D2)
**And** when channel writes reach ≥ 90% capacity a single `Warning` (`DiagCategories.SsdpChannelNearFull`) is emitted
**And** when `DropOldest` actually drops an item a `Warning` (`DiagCategories.SsdpChannelOverflow`) is emitted

**Given** `SendMSearchAsync(mx, ct)`
**When** it runs
**Then** an M-SEARCH datagram is sent via the ephemeral search socket using `ST: upnp:rootdevice` (FR-004 + FR-053 layer (a))
**And** the MX header carries the supplied TimeSpan (typically 5 s)
**And** the request egresses on the chosen adapter (because `MulticastInterface` is set)

**Given** `DisposeAsync()`
**When** the transport is torn down
**Then** the multicast group is left cleanly (`DropMembership`)
**And** both sockets are closed
**And** the channel writer completes so the reader observes the close

**Given** any unhandled `SocketException` during receive
**When** it surfaces
**Then** a `Warning` diagnostic is emitted with `RemoteEndpoint` context where applicable (FR-039 emission point)
**And** the receive loop continues rather than tearing down the whole transport (NFR-R1 — one bad packet does not kill the session)

**Given** the test suite
**When** I run the transport tests
**Then** integration tests against loopback / 127.0.0.1 verify both sockets receive what the test fixture sends
**And** the `[Trait("category", "integration")]` carries them so they run via the chaos-hook + main `dotnet test`

---

### Story 2.2: Network Adapter Enumerator + Adapter Scope + Startup Bind

As a Linn engineer,
I want ohSpy to enumerate every eligible IPv4 adapter at startup, default to the first one, bind the SSDP transport to it inside a cancellation scope, and degrade gracefully when there are no eligible adapters,
So that the tool runs deterministically on the developer's primary network without configuration — and never crashes on a host that happens to be offline.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Discovery/NetworkAdapterEnumerator.cs`
**When** I call it on a typical Windows host
**Then** it returns the list of eligible IPv4 adapters — operational, non-loopback, multicast-capable — with stable enumeration ordering (FR-048)
**And** each entry exposes the friendly name, description, and IPv4 address suitable for display in the future `View → Network adapter` radio list (E5 consumer)

**Given** the application starts
**When** `App.OnLaunched` (or equivalent) runs
**Then** an `AdapterScope` is constructed wrapping `_adapterCts = CancellationTokenSource.CreateLinkedTokenSource(appToken)` (D7 — adapter level of the cancellation hierarchy)
**And** the `AdapterScope` selects the FIRST eligible adapter from the enumerator (FR-048: default at launch is the first eligible adapter)
**And** the `AdapterScope` constructs an `ISsdpTransport` bound to that adapter and awaits `StartAsync(adapterIPv4, _adapterCts.Token)`
**And** the `AdapterScope` issues the initial M-SEARCH via `SendMSearchAsync(TimeSpan.FromSeconds(5), _adapterCts.Token)` (FR-004 startup discovery)

**Given** a host with ZERO eligible adapters
**When** the app launches
**Then** the app does NOT crash and does NOT show an error dialog (NFR-R5 + FR-048)
**And** the main window opens with an empty device tree
**And** a single `Warning` diagnostic is emitted (e.g. `DiagCategories.AdapterSwitch` with context "no eligible adapters at startup")
**And** the app remains interactive — the user can still open menus, view diagnostics later, etc.

**Given** the DI composition root
**When** the App starts
**Then** `INetworkAdapterEnumerator` is registered as singleton
**And** the `AdapterScope` is constructed inside the `ShellViewModel` (or equivalent app-startup orchestrator), not registered as a long-lived DI singleton (its lifetime is bounded by adapter selection, not the process)

**Given** the future adapter-switch use case
**When** I look at the `AdapterScope` shape
**Then** it exposes an `IPAddress CurrentAdapterIPv4`, a `CancellationToken AdapterToken`, and an `IAsyncDisposable.DisposeAsync()` that cancels `_adapterCts`, tears down the transport, and completes within the FR-050 2 s budget (the FR-050 atomic-switch sequence itself lands in E5; this story scaffolds the shape so E5 can plug in)

**Given** the test suite
**When** I run the adapter tests
**Then** the enumerator is testable via a stubbed `INetworkInterfaceSource` (or equivalent injection) so unit tests can simulate zero/one/many adapters
**And** an integration test asserts that on the dev machine, at least one eligible adapter is enumerated

---

### Story 2.3: Device Registry + DescriptionFetchState Machine + Eager Description Dispatcher

As a Linn engineer,
I want a UUID-keyed device registry whose entries progress through a strict `Pending → InFlight → Loaded/Failed` state machine driven by a bounded-parallelism eager-fetch dispatcher,
So that devices appear in the visible tree only after their description is parsed (no transient placeholders), slow devices can't choke the fetch pipeline, and the registry's event surface gives the tree exactly the rows it needs — no filtering, no race conditions.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Devices/DescriptionFetchState.cs`
**When** I inspect the enum
**Then** it has the four values `Pending, InFlight, Loaded, Failed` (D9)

**Given** `ohSpy.Core/Devices/RegistryEntry.cs`
**When** I inspect the type
**Then** it carries the shape defined in D9: `Uuid` (Guid), `LocationUrl`, `State` (default `Pending`), `Description?` (non-null iff `Loaded` — AC-9.2), `FailureReason?` (non-null iff `Failed`), `FirstSeenUtc`, `LastSeenUtc`, `AliveCount`, `Server?`, `CacheControlMaxAge?`, `BootId?`, `ConfigId?`, internal `DeviceCts`, public `DeviceToken => DeviceCts.Token`
**And** `MarkInFlight()`, `MarkLoaded(DeviceDescription)`, `MarkFailed(string)`, `RefreshSsdpMetadata(...)` are all `internal` so only `Core` (and tests) can call them (D9)
**And** all four are UI-thread-only (callers must marshal through `IUiDispatcher.Post` — Pattern 7 + D1 + D9 contract)

**Given** the state machine
**When** I attempt the legal transitions
**Then** `Pending → InFlight` (`MarkInFlight`), `Pending → Failed` (`MarkFailed`), `InFlight → Loaded` (`MarkLoaded`), `InFlight → Failed` (`MarkFailed`) all succeed (AC-9.1)
**And** every other transition (`Loaded → anything`, `Failed → anything`, `Pending → Loaded` directly, etc.) throws `InvalidOperationException` (AC-9.1)
**And** `Loaded` and `Failed` are terminal for the entry's lifetime

**Given** `ohSpy.Core/Devices/IDeviceRegistry.cs` + `DeviceRegistry.cs`
**When** I inspect the interface
**Then** it exposes `TryGetEntry(Guid, out RegistryEntry)`, `IReadOnlyCollection<RegistryEntry> Loaded`, `int Count`, and three events: `DeviceLoaded(RegistryEntry)`, `DeviceUpdated(RegistryEntry)`, `DeviceRemoved(Guid)` (D9)
**And** there is NO `DeviceAdded` event — the registry's external surface emits `DeviceLoaded` exactly when `MarkLoaded` runs (AC-9.3 — VMs don't see entries before `Loaded`)
**And** `DeviceUpdated` is raised when an already-`Loaded` entry's friendly name (or other display-affecting field) changes (FR-054 trigger)

**Given** `ohSpy.Core/Devices/EagerDescriptionDispatcher.cs`
**When** I inspect the impl
**Then** it holds a `SemaphoreSlim(8, 8)` cap (NFR-P6 + FR-043 — target 8 concurrent fetches)
**And** it injects `IUpnpHttpClient`, `IDeviceDescriptionParser`, `IUiDispatcher`, `IDeviceRegistry`, `IDiagnosticEmitter`
**And** `FetchAsync(RegistryEntry entry)` implements the canonical flow from D9 verbatim

**Given** the canonical fetch flow
**When** `FetchAsync(entry)` runs against a happy device
**Then** the sequence is: `await _semaphore.WaitAsync(entry.DeviceToken)` → `_dispatcher.Post(() => entry.MarkInFlight())` → `await _http.FetchDeviceDescriptionAsync(entry.LocationUrl, entry.DeviceToken)` → `_descParser.Parse(bytes)` → `_dispatcher.Post(() => { entry.MarkLoaded(description); _registry.RaiseDeviceLoaded(entry); })`
**And** the semaphore is released in the `finally` block

**When** the fetched description's `RootUdn` does NOT match `entry.Uuid`
**Then** an `Information` `DiagCategories.DescriptionFetchMismatch` diagnostic is emitted with `DeviceUuid = entry.Uuid`, `Url = entry.LocationUrl.ToString()`, `ErrorText = $"declared root: {description.RootUdn}"` (FR-043 mismatched-root backstop)
**And** the requesting entry is REMOVED from the registry via `_dispatcher.Post(() => _registry.Remove(entry.Uuid))`
**And** no `MarkLoaded` is called on either entry (AC-9.6)

**When** the fetch is cancelled via `entry.DeviceToken` (byebye, adapter switch)
**Then** `OperationCanceledException` is caught silently — no state transition (AC-9.7)
**And** no diagnostic is emitted (the cancellation is caller-initiated, registry remove handles the rest)

**When** any other exception is raised
**Then** a `Warning` `DiagCategories.DescriptionFetch` diagnostic is emitted with `DeviceUuid`, `Url`, `ErrorText`
**And** `_dispatcher.Post(() => entry.MarkFailed(ex.Message))` runs (FR-047: failed entries stay in registry but DO NOT appear in tree)

**Given** the device-level CTS hierarchy (D7)
**When** the registry adds an entry
**Then** `entry.DeviceCts = CancellationTokenSource.CreateLinkedTokenSource(adapterToken)` (D7 device-level)
**And** removing an entry cancels its `DeviceCts` before the entry is dropped (AC-7.2: byebye cancels in-flight fetches for that device only)

**Given** subsequent alive for an already-known UUID
**When** the registry observes it (the call surface for this is the discovery service in Story 2.4, but the registry method is shaped now)
**Then** the registry routes it through `entry.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId)` (FR-007 + AC-9.4)
**And** `RefreshSsdpMetadata` does NOT call any `Mark*` (AC-9.4)
**And** no re-fetch is issued (FR-043 cache invariant)
**And** `LastSeenUtc` is updated and `AliveCount` is incremented

**Given** the re-discovery scenario
**When** a known UUID receives byebye then alive
**Then** the registry creates a NEW `RegistryEntry` instance (different reference) for the second alive (AC-9.5)
**And** the new entry starts at `Pending` with a fresh `DeviceCts`
**And** a fresh fetch is scheduled

**Given** the test suite
**When** I run state-machine tests
**Then** the full AC-9.1..AC-9.7 transition matrix is exercised with the `[Trait("ac", "AC-9.x")]` discipline
**And** AC-7.2 (per-device byebye drill) is exercised with a 5-device scenario where one byebye cancels only the targeted device's fetch

---

### Story 2.4: SSDP Parser + DiscoveryService — Wire Transport Into Registry

As a Linn engineer,
I want the SSDP parser to translate raw datagrams into structured announcements and the `DiscoveryService` to route them into the registry (root-only, dedup-by-UUID, alive vs byebye),
So that the transport's datagram stream actually drives the device list — turning "we're receiving UDP packets" into "the tree fills with devices."

**Acceptance Criteria:**

**Given** `ohSpy.Core/Discovery/SsdpAnnouncement.cs`
**When** I inspect the type
**Then** it is a `public sealed record` exposing parsed fields: `NT?`, `NTS?`, `ST?`, `USN?` (UUID extracted), `LOCATION?`, `CacheControlMaxAge?`, `SERVER?`, `BootId?`, `ConfigId?`, plus `IsRootDevice` (computed from NT == `upnp:rootdevice` per FR-053 layer (b))

**Given** `ohSpy.Core/Discovery/SsdpParser.cs`
**When** I parse a raw SSDP datagram payload (HTTPMU / HTTPU text)
**Then** the parser extracts every required header above
**And** unrecognised headers are ignored (lenient on extras — D4 vendor-noise philosophy applied to SSDP too)
**And** truly malformed datagrams (no request/response line, missing required headers) produce a parse failure surfaced via a `Warning` `DiagCategories.SsdpParse` diagnostic with `RemoteEndpoint` context

**Given** `ohSpy.Core/Discovery/DiscoveryService.cs`
**When** the service starts
**Then** it begins consuming from `ISsdpTransport.IncomingDatagrams` as a single reader
**And** for each datagram it invokes `SsdpParser.Parse(datagram.Payload)` and routes the announcement

**Given** an announcement is parsed
**When** the announcement is an `alive` for a UUID NOT in the registry AND `NT == upnp:rootdevice` (FR-053 layer (b))
**Then** `DiscoveryService` creates a new `RegistryEntry(uuid, locationUrl, nowUtc)` via `IDeviceRegistry.Add` (FR-005)
**And** the registry schedules `EagerDescriptionDispatcher.FetchAsync(entry)` (FR-043)

**When** the announcement is `alive` for a UUID ALREADY in the registry
**Then** `DeviceRegistry` routes via `entry.RefreshSsdpMetadata` — no re-fetch (FR-007 + FR-043 cache)
**And** no new registry entry is created

**When** the announcement is `byebye` for a known UUID AND `NT == upnp:rootdevice` (FR-053 layer (b))
**Then** `IDeviceRegistry.Remove(uuid)` runs (FR-008)
**And** the entry's `DeviceCts.Cancel()` fires
**And** `DeviceRemoved(uuid)` is raised

**When** the announcement's `NT != upnp:rootdevice` (e.g. embedded device, service-only)
**Then** the registry is NOT mutated (FR-053 layer (b) — embedded children flatten via description parse, NOT via separate registry entries)
**And** the announcement is STILL routed to the SSDP log VM via the discovery-service event surface defined below (FR-014 + FR-015 — log captures everything; registry filters to roots)

**Given** the discovery service's event surface
**When** I look at its public events
**Then** it raises `AnnouncementReceived(SsdpAnnouncement)` for every successfully-parsed announcement (alive AND byebye, regardless of root vs embedded — the SSDP log subscribes to this in Story 2.7)
**And** every emit marshals through `IUiDispatcher.Post` (NFR-P3)

**Given** the initial M-SEARCH response burst
**When** the search socket receives M-SEARCH responses (datagrams with `Source = SearchResponse`)
**Then** each response is parsed and routed identically to unsolicited `alive` (FR-005 + FR-006 + Architecture §"SSDP datagram flow")

**Given** the rescan cancellation contract (forward-compatible)
**When** rescan is later wired in E5
**Then** the discovery service exposes a method that re-issues `SendMSearchAsync` AND tracks which UUIDs responded so non-responders can be pruned — but the rescan flow itself lives in E5; THIS story only ensures the shape is set up for it

**Given** the integration test
**When** I run a full datagram drill
**Then** an in-memory `ISsdpTransport` test double feeds canned `SsdpDatagram` fixtures into the channel
**And** a happy alive → registry add → mocked `EagerDescriptionDispatcher.FetchAsync` invocation is verified
**And** a byebye → registry remove → `DeviceCts.Cancel()` chain is verified
**And** an embedded-device alive is silently ignored at the registry level but is observed via `AnnouncementReceived`

---

### Story 2.5: Main Window Shell + Device Tree (Top-Level Rows)

As a Linn engineer,
I want a two-pane main window with the discovered root devices populating the left-pane tree (sorted, with friendly name + secondary detail line + kind glyph + persistent expand chevron),
So that I can launch ohSpy and see every UPnP root device on my network within the SC-001 budget without manually triggering anything.

**Acceptance Criteria:**

**Given** `src/ohSpy.App/MainWindow.xaml` + `MainWindow.xaml.cs`
**When** the window opens
**Then** the layout is a `Grid` with two columns — left tree pane and right SSDP log pane (FR-001 — log pane content fills in Story 2.7; this story renders an empty placeholder for the right pane)
**And** the code-behind is constructor-only (Pattern 13): `InitializeComponent()` + DI-injected `ShellViewModel` assignment to `DataContext`

**Given** `src/ohSpy.Core/ViewModels/ShellViewModel.cs`
**When** I inspect it
**Then** it composes `DeviceTreeViewModel` and exposes it via an `[ObservableProperty]` (FR-002)
**And** the `AdapterScope` from Story 2.2 lives inside `ShellViewModel` (or is constructed by it during initialization)

**Given** `src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs`
**When** I inspect it
**Then** it exposes `IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel> Devices` (D6 + FR-054)
**And** the sort comparator is case-insensitive on `FriendlyName` (with `uuid:<uuid>` fallback) with ordinal UUID tiebreak (FR-054)
**And** the VM subscribes to `IDeviceRegistry.DeviceLoaded` → `Devices.Add(new DeviceNodeViewModel(entry))` (FR-005 + FR-047)
**And** the VM subscribes to `IDeviceRegistry.DeviceUpdated` → `Devices.Update(existingNode)` (label/sort-key change → `Move(old, new)` per AC-6.4 — selection/expansion preserved per FR-054)
**And** the VM subscribes to `IDeviceRegistry.DeviceRemoved` → `Devices.Remove(uuid)` (FR-008)
**And** all subscriptions marshal via `IUiDispatcher.Post`

**Given** `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`
**When** I inspect the VM
**Then** it wraps a `RegistryEntry` and exposes `[ObservableProperty]` `FriendlyName` (FR-009 — bound from `entry.Description.FriendlyName` or `uuid:<uuid>` fallback per FR-010)
**And** it exposes `NodeKind Kind => NodeKind.Device` (FR-045)
**And** it exposes a `SecondaryDetail` string formatted per FR-051: `<deviceTypeTail> · <host>:<port>` (middle-dot separator; tail of `<deviceType>` after `:device:`; host:port extracted from `entry.LocationUrl`)
**And** `Children` is initialised in the constructor to `[ new LoadingPlaceholderViewModel() ]` so the WinUI `TreeView` renders the expand chevron immediately (FR-044 + AC-A1.1)
**And** `[ObservableProperty]` `IsExpanded` triggers service enumeration in Story 2.6 — wiring stub is present but does nothing in this story

**Given** `src/ohSpy.Core/ViewModels/LoadingPlaceholderViewModel.cs` + `InlineErrorViewModel.cs` (A1)
**When** I inspect them
**Then** they implement an `INodeViewModel` marker interface with `string Label`, `NodeKind Kind` (FR-045)
**And** `LoadingPlaceholderViewModel.Label == "Loading…"` and `Kind == NodeKind.Placeholder`
**And** `InlineErrorViewModel.Label` carries the FR-013 error text and `Kind == NodeKind.Error`
**And** neither renders a kind glyph (FR-045 — only device/service/action nodes carry glyphs)

**Given** the XAML `DataTemplate` for `DeviceNodeViewModel`
**When** it renders
**Then** the layout is a horizontal `StackPanel` (or `Grid`) with a leading kind glyph (`FontIcon`), the friendly name as primary text, and the secondary detail line beneath in a muted brush (FR-045 + FR-051 + NFR-UI2)
**And** the glyph is drawn from a font already shipped by Windows (e.g. Segoe Fluent Icons or Segoe MDL2 Assets) — no external icon assets (FR-045)
**And** the muted brush is a resource key `MutedForegroundBrush` in the App-level resources (Pattern 13)
**And** binding uses `x:Bind` with `x:DataType="vm:DeviceNodeViewModel"` (Pattern 13)

**Given** the SC-001 performance budget
**When** I launch ohSpy on a LAN with 10–20 announcing UPnP devices
**Then** every responsive device with a fetchable description is visible in the tree within ≤ ~7 s (5 s MX + ≤ 2 s eager fetch)
**And** zero duplicate tree entries appear for any UUID (SC-002 + FR-007)
**And** devices whose description fetch failed do NOT appear in the tree (FR-047)

**Given** a re-announce that changes a device's friendly name
**When** the change triggers `DeviceUpdated`
**Then** the row migrates to its new sorted position via `Move(old, new)` (AC-6.4 + FR-054)
**And** the row's identity (and any future expansion state) is preserved across the migration
**And** sibling subtrees are NOT redrawn (NFR-P5 + FR-054 consequence)

**Given** a `byebye` arrives during steady state
**When** the registry removes the entry
**Then** the row vanishes within ~ 2 s on a quiet LAN (SC-003)

**Given** the diagnostic emission discipline
**When** any of the above paths fails internally (e.g. description fetch error already covered in Story 2.3)
**Then** the relevant `Warning` is already in the diagnostic stream (no new emit sites added by THIS story beyond what 2.3 covers)

---

### Story 2.6: Service & Action Expansion (Lazy SCPD, Incremental)

As a Linn engineer,
I want to expand a device row to see its services (immediate from the eager-fetched description) and expand a service to see its actions (lazily fetched on first expand, streamed incrementally),
So that I can navigate to any action on any device without the UI freezing on a 200-action IGD router SCPD.

**Acceptance Criteria:**

**Given** `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`
**When** I inspect it
**Then** it wraps a `ServiceDescription` (extracted by `DeviceDescriptionParser` in Story 1.4)
**And** exposes `[ObservableProperty]` `Label` (typically `<serviceId>` tail or `<serviceType>` tail — pick the more readable; consistency with the prior tool's UpnpSpy preferred)
**And** `Kind => NodeKind.Service` for FR-045
**And** `Children` is initialised in the constructor to `[ new LoadingPlaceholderViewModel() ]` (AC-A1.2 + FR-044)
**And** an `[ObservableProperty]` `IsExpanded` triggers `LoadActionsAsync` on first transition to `true`

**Given** `DeviceNodeViewModel.IsExpanded` transitions to `true`
**When** the expansion happens (Story 2.5 wired the stub; this story implements the real handler)
**Then** the device's children are replaced atomically via `ReplaceWith([...new ServiceNodeViewModel(s) for s in entry.Description.AllServices])` (AC-A1.4 — single `INotifyCollectionChanged` notification; chevron does NOT collapse mid-expand; NFR-UI3)
**And** the service list is built from the device description's `<serviceList>` recursively flattened across embedded children (FR-011 + FR-053 — embedded children's services appear as the root's services in the tree)
**And** no HTTP fetch is triggered by the expand (FR-011 — description was eager-fetched in Story 2.3)

**Given** `ServiceNodeViewModel.LoadActionsAsync` runs on first expand
**When** the SCPD URL is fetched
**Then** the call uses `IUpnpHttpClient.FetchScpdAsync(scpdUrl, popupOrNodeToken)` (NFR-P2 timeout applies)
**And** on success, the returned `byte[]` is wrapped in a `MemoryStream` and passed to `IScpdParser.StreamActionsAsync`
**And** the consumer loop is `await foreach (var action in parser.StreamActionsAsync(stream, ct)) { _dispatcher.Post(() => actionsList.Add(new ActionNodeViewModel(action))); }` (FR-100 incremental — actions appear as they parse)
**And** `actionsList` is an `ObservableCollection<INodeViewModel>` (Pattern 9 fits — small bounded collection per service; full `BoundedObservableCollection` overkill here)
**And** when streaming completes, the service node's `Children` is replaced atomically via `ReplaceWith(actionsList)` so the placeholder is removed in a single notification — OR, the placeholder is removed before the first `Add` and subsequent actions append (pick the cleaner semantic; document the choice in the impl)

**Given** the SCPD fetch fails (timeout, transport, protocol)
**When** the failure is observed
**Then** the service node's `Children` is replaced via `ReplaceWith([ new InlineErrorViewModel(message) ])` (FR-013 + AC-A1.5)
**And** a `Warning` `DiagCategories.ScpdFetch` or `DiagCategories.ScpdParse` diagnostic is emitted with `DeviceUuid`, `Url` per Pattern 11

**Given** a 100-action SCPD (`tests/Fixtures/Scpds/igd-router-200action.xml` subset)
**When** the operator expands the service
**Then** the service node enters "Loading…" state immediately (AC-5.1 streaming behaviour)
**And** the first action appears in the tree promptly (sub-second on a LAN)
**And** the full action list is visible within ≤ 2 s (Performance Budget "Cold large-SCPD expand")
**And** no UI-thread stall > 16 ms occurs during the parse (NFR-UI4 + AC-5.1)

**Given** a service that has already been expanded
**When** the operator collapses then re-expands it
**Then** no re-fetch is issued (the action list is retained); the chevron toggles state cleanly (NFR-UI3)

**Given** `src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs`
**When** I inspect it
**Then** it wraps an `ScpdAction` and exposes `[ObservableProperty]` `Label` (the action name)
**And** `Kind => NodeKind.Action` (FR-045)
**And** `Children` is EMPTY (FR-044 second consequence + AC-A1.3)
**And** the XAML template does NOT render an expand chevron for `ActionNodeViewModel` instances (verified via manual UI inspection)

**Given** any service node's cancellation
**When** the device's `DeviceCts` cancels mid-parse (byebye, adapter switch)
**Then** the `await foreach` throws `OperationCanceledException` (AC-5.4)
**And** the partial action list previously emitted is discarded along with the node itself (the device is being removed)
**And** no diagnostic is emitted for the cancellation

---

### Story 2.7: SSDP Message Log (Right Pane, Virtualised, Smart Auto-Follow)

As a Linn engineer,
I want the right pane to be a live scrolling list of every SSDP `alive` and `byebye` advertisement, newest at the top, virtualised so a chatty network doesn't stutter, with smart auto-follow that respects manual scroll position,
So that I can monitor what's happening on the wire without the prior tool's full-pane repaints and without losing my place when I scroll back to read history.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Models/SsdpLogEntry.cs`
**When** I inspect it
**Then** it is a `public sealed record` with `DateTime TimestampUtc`, `SsdpLogKind Kind` (enum `Alive | Byebye`), `Guid Uuid` (extracted from `USN`)

**Given** `ohSpy.Core/ViewModels/SsdpLogViewModel.cs`
**When** I inspect it
**Then** it exposes `BoundedObservableCollection<SsdpLogEntry> Entries` constructed with capacity 10,000 (FR-016 + D6)
**And** it exposes `[ObservableProperty] bool IsAtTop` reflecting whether the bound list is parked at (or near) the top
**And** it subscribes to `DiscoveryService.AnnouncementReceived` and routes alive / byebye announcements via `IUiDispatcher.Post(() => Entries.PrependNewest(new SsdpLogEntry(...)))` (FR-014 + FR-015)
**And** announcements with NTS other than `ssdp:alive` / `ssdp:byebye` are ignored at the log VM level (per FR-014 / FR-015 grammar)

**Given** the FIFO eviction
**When** the 10,001st entry arrives at capacity
**Then** the oldest (tail) entry is discarded (FR-016)
**And** the underlying `BoundedObservableCollection.PrependNewest` emits exactly `Add(0)` + `Remove(10000)` — never `Reset` (AC-6.1 invariant carried into the log VM)
**And** eviction never removes the top row (FR-055)

**Given** `MainWindow.xaml`'s right pane
**When** the log is rendered
**Then** the visual is an `ItemsRepeater` (or equivalent virtualised control) inside a `ScrollViewer` — NOT a `ListView` with non-virtualised wrapping (FR-101 + NFR-P1)
**And** each row displays the timestamp, the literal `ALIVE` / `BYEBYE` token, and the UUID — with `x:Bind` and `x:DataType="m:SsdpLogEntry"` (Pattern 13)

**Given** the smart auto-follow rule (FR-055)
**When** the operator is parked at (or near) the top of the list
**Then** new arrivals scroll into view automatically (the visual stays anchored at the top)

**When** the operator scrolls away from the top to read history
**Then** new arrivals do NOT yank the view back to the top (FR-055 — the operator's scroll context is preserved)
**And** the `IsAtTop` flag transitions to `false` when the operator's scroll offset exceeds a small threshold (e.g. one row from the top)
**And** the `IsAtTop` flag transitions to `true` when the operator scrolls back to the top

**Given** the sustained chatty-SSDP test target
**When** the test fixture injects ≥ 20 advertisements/sec for ≥ 30 seconds (test baseline §6)
**Then** the log renders every entry without dropped frames visible to the eye (NFR-UI4)
**And** main-thread stalls remain < 16 ms (NFR-P5 + NFR-UI4)
**And** memory used by the rendered view scales with VISIBLE row count, not with the 10,000 buffered entries (FR-101 consequence)

**Given** an adapter switch (forward-compatible — full FR-050 lands in E5)
**When** the AdapterScope is replaced
**Then** the log VM's `Entries.Clear()` is called (single `Reset` notification — AC-6.6)
**And** the log starts fresh on the new adapter — no carry-over (PRD §7 Non-Goal: no settings persistence; same principle applies to runtime state)

---

### Story 2.8: Right-Click Context Menus — XML Viewing in Default Browser

As a Linn engineer,
I want to right-click a device row to fetch its description XML in my default browser, and right-click a service row to fetch its SCPD XML (or open a Subscribe menu item — handler lands in E4),
So that I can read the raw protocol payloads directly without leaving my Windows workflow.

**Acceptance Criteria:**

**Given** `DeviceNodeViewModel`
**When** I right-click the device row
**Then** a context menu opens with a "Fetch description XML" item AND a "Properties…" item (FR-017 + FR-052 wiring — Properties window itself is delivered in Story 2.9)
**And** the menu uses XAML `MenuFlyout` bound via `x:Bind` to `[RelayCommand]` methods on the VM

**Given** the "Fetch description XML" item is chosen
**When** `DeviceNodeViewModel.FetchXmlCommand` runs
**Then** the device's `LocationUrl` is opened in the user's default web browser via `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })` (FR-019)
**And** the operation completes within ≤ 2 s typical (SC-005)
**And** if the launch fails (e.g. no default browser), a `Warning` diagnostic is emitted with `Url` context — no app crash

**Given** the URL safety check (Architecture validation Gap-3)
**When** `FetchXmlCommand` is invoked
**Then** only `http://` and `https://` schemes are accepted (whitelist)
**And** any other scheme causes a `Warning` diagnostic to be emitted and the launch is skipped (defensive; UPnP `LOCATION` URLs are HTTP per UDA 1.0)

**Given** `ServiceNodeViewModel`
**When** I right-click the service row
**Then** a context menu opens with two items: "Fetch service XML" AND "Subscribe" (FR-018)
**And** "Fetch service XML" opens the service's `SCPDURL` in the default browser via the same shell-execute path as the device case (FR-020)
**And** the URL whitelist applies the same way
**And** the "Subscribe" item is wired to a `SubscribeCommand` on `ServiceNodeViewModel` — but the command's implementation is a stub that emits a `Warning` `"subscribe not yet implemented"` diagnostic; full implementation lands in Epic 4 (Story 4.1)
**And** the stub clearly indicates to the operator that subscription is forthcoming (e.g. a transient flyout "Subscribe — coming in Epic 4") — OR the menu item is hidden behind a feature flag — engineering judgment, document the choice in the impl

**Given** any context-menu-driven shell-execute call
**When** it runs
**Then** it executes on the UI thread and returns within the SC-005 budget; the brief shell-execute kick-off is non-blocking enough not to require `IUiDispatcher.PostAsync`-style readback (it's a fire-and-forget)

---

### Story 2.9: Window Ownership Manager + Properties Window (First Popup)

As a Linn engineer,
I want right-click → Properties… on a device row to open a read-only Properties window showing the full UPnP description and SSDP metadata, owned by the main window so its z-order and lifetime behave correctly, and surviving cleanly if the device leaves the network while open,
So that I can see every captured field for a device without committing to keep it on the network — and the popup behaves like a proper Windows child window.

**Acceptance Criteria:**

**Given** `src/ohSpy.App/Windowing/WindowOwnershipManager.cs`
**When** I inspect it
**Then** it implements `IWindowOwnershipManager` declared in `Core` (or `App` if the interface is App-local — D10 default)
**And** it uses `[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]` for the Win32 `SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, parentHwnd)` call with `GWLP_HWNDPARENT = -8` (D10)
**And** it tracks ownership in a `Dictionary<IntPtr, List<IntPtr>>` for testability via `GetChildrenOf(parent)`
**And** the `Closed` event on the child window prunes the tracking dictionary

**Given** the canonical popup-open pattern (D10)
**When** any popup is constructed
**Then** the sequence is `window.Activate()` THEN `_windowOwnership.Adopt(window, _shellWindow)` (AC-10.1 — order is non-obvious but empirically required in WinUI 3)
**And** the pattern is documented in code-comment on the `Adopt` method so future popup-creation sites (Epics 3-5) follow it verbatim

**Given** the four FR-046 behaviours
**When** the Properties popup is open
**Then** it appears above the main window when shown (AC-10.1)
**And** clicking the main window for focus does NOT push the Properties popup behind it (AC-10.4 — manual UI test)
**And** minimising the main window minimises the Properties popup; restoring restores it (AC-10.3 — manual UI test)
**And** closing the main window closes the Properties popup (AC-10.2 — manual UI test)
**And** the popup is independently activatable — z-order ownership is NOT modality (D10)

**Given** `src/ohSpy.Core/ViewModels/PropertiesViewModel.cs`
**When** I inspect it
**Then** it takes a `RegistryEntry` in its constructor and exposes read-only properties grouped per FR-052: `Identity` (FriendlyName, DeviceTypeUrn, Udn / Uuid, PresentationUrl), `Manufacturer` (Manufacturer, ManufacturerUrl, ModelName, ModelNumber, ModelDescription, ModelUrl, SerialNumber, Upc), `Network` (LocationUrl, Ip, Port, SsdpServer, CacheControlMaxAgeSeconds), `DiscoveryHistory` (FirstSeenUtc, LastSeenUtc, AliveCount, BootId, ConfigId), `EmbeddedDevices` (recursive list)
**And** fields the device did not declare render as a muted placeholder (e.g. `"—"`) so the operator can distinguish "absent" from "empty" (FR-052 consequence)

**Given** the Properties window XAML (`src/ohSpy.App/Views/PropertiesWindow.xaml`)
**When** the window renders
**Then** it is read-only (no editable controls)
**And** `PresentationUrl`, `ManufacturerUrl`, `ModelUrl`, `LocationUrl` render as clickable hyperlinks (when present and matching the http/https whitelist from Story 2.8); clicking opens in the default browser via the same shell-execute path
**And** the layout uses sections with section headers matching the FR-052 grouping (Identity / Manufacturer / Network / Discovery history / Embedded devices)

**Given** the Properties window is open
**When** the device leaves the network (`byebye` arrives, registry removes the entry — FR-008)
**Then** the popup transitions to a "device is no longer reachable" UI state (e.g. a banner at the top reading "Device left the network at <time>"; data remains visible from the snapshot at popup-open time)
**And** the popup remains closeable without producing errors (FR-037 + NFR-R3 + AC-10.5)
**And** the registry's `DeviceRemoved(uuid)` event is the trigger — the VM subscribes to the registry and matches by UUID

**Given** the right-click handler from Story 2.8's "Properties…" menu item
**When** the user chooses it
**Then** `DeviceNodeViewModel.OpenPropertiesCommand` (or `ShellViewModel.OpenPropertiesCommand` — engineering judgment, document the seam) creates a new `PropertiesWindow(propertiesVm)`, calls `Activate()`, calls `_windowOwnership.Adopt(propertiesWindow, _shellWindow)` (AC-10.5)

**Given** the DI composition root
**When** the App starts
**Then** `IWindowOwnershipManager` is registered as a singleton with `WindowOwnershipManager` as the implementation
**And** a `Func<RegistryEntry, PropertiesViewModel>` factory is registered so popups can be constructed without leaking the `IServiceProvider` to call sites (Pattern 7)

---

## Epic 3: Action Invocation

Double-click an action → invocation popup opens with editable input fields. Inputs with `<allowedValueList>` render as constrained dropdowns; numeric inputs with `<allowedValueRange>` render as constrained spinners honouring `<step>`. Invoke → success shows output args, UPnP fault shows error code + description, transport error shows diagnostic info — without crashing. Argument-less actions invoke cleanly. Popup remains closeable if the device disappears mid-invocation; popup is z-order-owned by the main window.

### Story 3.1: SOAP Envelope Builder, Fault Parser, and `InvokeActionAsync` Wire-Up

As an ohSpy developer,
I want SOAP envelope construction, UPnP fault parsing, and the full body of `IUpnpHttpClient.InvokeActionAsync` wired up,
So that the invocation popup in Story 3.2 can call one method and trust that the request is well-formed, the success path returns structured output, and the SOAP 500 / `<UPnPError>` fault path raises the correct typed exception.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Models/SoapRequest.cs` and `SoapResponse.cs`
**When** I inspect the records
**Then** `SoapRequest` is a `public sealed record` with `Uri ControlUrl`, `string ServiceType` (the `<serviceType>` URN string), `string ActionName`, `IReadOnlyList<SoapArgument> InputArguments` (Pattern 9)
**And** `SoapResponse` is a `public sealed record` with `string ActionName`, `IReadOnlyList<SoapArgument> OutputArguments` (Pattern 9)
**And** `SoapArgument` is a `public sealed record` with `string Name`, `string Value` (free-form text per PRD §7 Non-Goal — no `<dataType>`-driven typed inputs in v1)

**Given** `ohSpy.Core/Soap/SoapEnvelopeBuilder.cs`
**When** I call `Build(SoapRequest req)`
**Then** the output is a valid SOAP 1.1 envelope conforming to UDA 1.0 §3.2.1 with the standard `s:Envelope` + `s:Body` structure
**And** the action element uses `<u:ActionName xmlns:u="<serviceType>">` and the namespace prefix `u:` consistently
**And** each input argument is rendered as `<argName>value</argName>` inside the action element in the order declared by `req.InputArguments`
**And** input values are XML-escaped properly (`<`, `>`, `&`, `"`, `'` → entities) — verified by fuzzy tests with adversarial input strings
**And** the envelope is UTF-8 encoded; the request later carries `Content-Type: text/xml; charset="utf-8"` and `SOAPACTION: "<serviceType>#<actionName>"` headers (per UDA 1.0 §3.2.1)
**And** argument-less actions produce an empty action element (`<u:ActionName xmlns:u="..." />`) (FR-031)

**Given** `ohSpy.Core/Soap/SoapFaultParser.cs`
**When** I call `TryParse(byte[] body, out UpnpFault fault)` on a SOAP 500 response body containing `<s:Fault><detail><UPnPError><errorCode>402</errorCode><errorDescription>Invalid Args</errorDescription></UPnPError></detail></s:Fault>`
**Then** parsing succeeds and `fault.ErrorCode == 402`, `fault.ErrorDescription == "Invalid Args"` (FR-029 — UDA 1.0 §3.2.2)
**And** the parser uses the same XmlReaderSettings discipline as Story 1.4 (DtdProcessing.Prohibit, XmlResolver = null) — defence in depth against XXE in fault responses
**And** if the body is a SOAP 500 WITHOUT a parsable `<UPnPError>` (e.g. raw fault string only), `TryParse` returns `false` and the caller treats it as a generic transport error

**Given** `UpnpHttpClient.InvokeActionAsync` body
**When** the method runs against a happy device
**Then** the request is built via `SoapEnvelopeBuilder.Build(request)`
**And** the HTTP request carries `POST <controlUrl>`, `Content-Type: text/xml; charset="utf-8"`, `SOAPACTION: "<serviceType>#<actionName>"`, and the envelope body
**And** the per-op timeout is `_opts.SoapInvoke` (10 s default — Decision 11)
**And** the body-size cap is 1 MB (Decision 3)
**And** on a 2xx response, the response body is parsed (via a small SOAP response reader) into a `SoapResponse` carrying each `<argName>value</argName>` from the response's `<u:ActionNameResponse>` element — XML-unescaped on value extraction

**When** the device responds with HTTP 500 + a parsable `<s:Fault>` body
**Then** `UpnpFaultException` is thrown carrying `Url = req.ControlUrl`, `ActionName = req.ActionName`, `ErrorCode`, `ErrorDescription` (A5 + AC-3.3)
**And** a `Warning` `DiagCategories.SoapFault` diagnostic is emitted with `DeviceUuid` (when known by the caller), `Url`, `ActionName`, `StatusCode = 500`, `ErrorText = $"{ErrorCode}: {ErrorDescription}"` per Pattern 11

**When** the device responds with HTTP 500 + an UN-parsable fault body
**Then** `UpnpTransportException` is thrown carrying `Url`, `StatusCode = 500`
**And** a `Warning` `DiagCategories.SoapInvoke` diagnostic is emitted

**When** the device responds with a non-2xx / non-500 status (404, 405, etc.)
**Then** `UpnpTransportException` is thrown carrying `Url`, `StatusCode`
**And** a `Warning` `DiagCategories.SoapInvoke` diagnostic is emitted

**Given** the test suite
**When** I run SOAP tests
**Then** `SoapEnvelopeBuilder` is exercised against canned action shapes (zero args, one string arg, multiple args with adversarial chars) with golden-file assertions on the envelope output
**And** `SoapFaultParser` is exercised against canned `<s:Fault>` fixtures including missing `<errorCode>`, missing `<errorDescription>`, malformed XML, and XXE-attempt
**And** `UpnpHttpClient.InvokeActionAsync` is exercised via `TestHttpMessageHandler` for the happy path (returns canned SoapResponse), SOAP fault path (asserts `UpnpFaultException` with correct error code), transport-error path (asserts `UpnpTransportException`), and timeout path (asserts `UpnpTimeoutException`)
**And** AC-3.3 carries `[Trait("ac", "AC-3.3")]`

---

### Story 3.2: Invocation Popup with Free-Form Text Inputs

As a Linn engineer,
I want double-clicking an action node to open an invocation popup that lists every input argument as a free-form text input, lets me press Invoke to POST the SOAP request, and displays success outputs / UPnP fault details / transport errors,
So that I can drive any device action with arbitrary arguments and see exactly what the device returned — without leaving ohSpy.

**Acceptance Criteria:**

**Given** `src/ohSpy.App/Views/InvocationPopupWindow.xaml` + `.xaml.cs`
**When** I inspect the window
**Then** the layout shows the action name as a header, a panel of input-argument controls, an Invoke button, a result area that toggles between "no result yet" / output args / fault detail / transport error, and a status indicator
**And** the code-behind is constructor-only (Pattern 13)

**Given** `src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs`
**When** I inspect it
**Then** the constructor takes `ScpdAction action`, `ServiceDescription parentService`, `RegistryEntry parentEntry`, plus injected `IUpnpHttpClient`, `IUiDispatcher`, `IDiagnosticEmitter`, and parent CTS tokens
**And** it exposes `string Title => $"{parentService.ServiceId} · {action.Name}"` (or similar — engineering judgment; document the choice)
**And** it exposes `ObservableCollection<ArgumentInputViewModel> Inputs` populated from `action.Inputs` (FR-026 — one input control per declared input arg)
**And** it exposes `[ObservableProperty] InvocationResultViewModel? Result` set when Invoke completes
**And** it exposes `[RelayCommand(CanExecute = nameof(CanInvoke))] InvokeAsync` (FR-027)

**Given** `src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs`
**When** I inspect it
**Then** it wraps an `ScpdArgument` and exposes `string Name`, `[ObservableProperty] string Value` (default `""` — FR-026 free-form text input)
**And** the value type is the polymorphic seam where Story 3.3 will layer in the constrained-input variants (`AllowedValueList` dropdown + `AllowedValueRange` numeric); the base `ArgumentInputViewModel` in this story is text-only

**Given** the popup-open trigger
**When** the operator double-clicks an `ActionNodeViewModel` row in the device tree
**Then** the TreeView's `ItemInvoked` / `DoubleTapped` event routes to a `ShellViewModel.OpenInvocationPopupCommand(action)` (or equivalent — document the seam)
**And** the command constructs `new InvocationPopupWindow(invocationVm)`, calls `Activate()`, then calls `_windowOwnership.Adopt(invocationPopup, _shellWindow)` — the canonical D10 pattern from Story 2.9 (FR-046 reuse)
**And** the popup is interactive (input fields editable) within ≤ 1 s of the double-click (SC-010)

**Given** an argument-less action (FR-031)
**When** the popup opens
**Then** `Inputs` is empty and the input panel shows a neutral "No input arguments" hint
**And** the Invoke button is enabled (no inputs required)

**Given** the operator presses Invoke
**When** `InvokeAsync` runs
**Then** the VM constructs a `SoapRequest` with `ControlUrl = parentService.ControlUrl`, `ServiceType = parentService.ServiceType`, `ActionName = action.Name`, `InputArguments = [Inputs.Select(i => new SoapArgument(i.Name, i.Value))]`
**And** the call passes a `CancellationToken` derived from the popup's `_popupCts` which is linked to `parentEntry.DeviceToken` (D7 popup level)
**And** during the in-flight call the popup shows a "Invoking…" status; controls are disabled to prevent re-invocation while in flight (NFR-UI3 — visual feedback without flicker)

**Given** a successful response
**When** `InvokeAsync` returns a `SoapResponse`
**Then** `Result` is set to a "Success" view-model variant carrying the output arguments as `(name, value)` pairs (FR-028)
**And** the result area renders one row per output argument; argument-less responses show a neutral "Success (no output)" message (FR-031 second consequence)
**And** the action result is visible within ≤ 2 s of pressing Invoke when the device responds within < 1 s LAN latency (SC-011)

**Given** a `UpnpFaultException`
**When** it is caught
**Then** `Result` is set to a "Fault" view-model variant carrying `StatusCode` (always 500 for SOAP fault), `ErrorCode`, `ErrorDescription` (FR-029 — UDA 1.0 §3.2.2)
**And** the result area visually distinguishes the fault from success (e.g. a warning brush, an icon)

**Given** a `UpnpTransportException`, `UpnpTimeoutException`, or any other transport-layer failure
**When** caught
**Then** `Result` is set to a "TransportError" view-model variant carrying a human-readable diagnostic message (Url + StatusCode if known + exception message) (FR-030)
**And** the result area visually distinguishes the transport error from a UPnP fault
**And** the popup does NOT crash (NFR-R3)

**Given** the operator closes the popup mid-invocation
**When** `OnClosing` runs
**Then** `_popupCts.Cancel()` is called → the in-flight SOAP request observes cancellation and throws `OperationCanceledException` (D7 popup-close-cancels-invocation contract)
**And** `_popupCts` is disposed in a `finally` block (D7 — no leaked CTS; AC-7.4)
**And** the popup closes cleanly without any exception surfacing to the user

**Given** the device disappears mid-invocation (`byebye`, rescan-prune)
**When** `parentEntry.DeviceCts` cancels (cascading from device removal)
**Then** the popup's `_popupCts` cancels (it's linked to the device token)
**And** the in-flight invocation throws `OperationCanceledException`
**And** the popup VM transitions to a "device is no longer reachable" UI state (a banner similar to Story 2.9's Properties window) (FR-037 + NFR-R3)
**And** the popup remains closeable without errors

**Given** the diagnostic emission discipline
**When** `InvokeAsync` catches `UpnpTimeoutException`
**Then** a `Warning` `DiagCategories.HttpTimeout` diagnostic is emitted with `DeviceUuid = parentEntry.Uuid`, `Url = parentService.ControlUrl`, `ActionName = action.Name`, `Elapsed`, `Budget` per Pattern 11

**When** `InvokeAsync` catches `UpnpFaultException`
**Then** a `Warning` `DiagCategories.SoapFault` diagnostic is emitted with `DeviceUuid`, `Url`, `ActionName`, `ErrorText = $"{ErrorCode}: {ErrorDescription}"` (this may be a duplicate of the emit Story 3.1 added inside `UpnpHttpClient` — engineering judgment whether to suppress the popup-level emit when the http-layer already emitted; document the choice)

---

### Story 3.3: Constrained Inputs — `<allowedValueList>` Dropdown + `<allowedValueRange>` Numeric

As a Linn engineer,
I want input arguments whose related state variable declares `<allowedValueList>` to render as a dropdown of exactly those values, and arguments declaring `<allowedValueRange>` on a numeric `<dataType>` to render as a bounded numeric input honouring `<step>`,
So that I can drive constrained actions (e.g. `SetMute true/false`, `SetVolume 0..100 step 1`) without typing the literal value and without submitting an invalid value the device will reject.

**Acceptance Criteria:**

**Given** the invocation popup is opening for an action whose service has not yet loaded its state-variable table
**When** the popup VM constructs `Inputs`
**Then** the VM calls `IScpdParser.ReadStateTableAsync(scpdStreamFromCachedBytes, popupToken)` to obtain the `ScpdStateTable` (Story 1.4 + D5)
**And** while the state table is loading, the popup shows a brief "Loading…" placeholder on the input panel (FR-044 family — UI feedback)
**And** the state table is cached on the parent `ServiceDescription` / `ServiceNodeViewModel` so subsequent invocations for the same service do not re-parse (Story 1.4 open follow-up — implemented here at the consumer level)
**And** if state-table parsing fails entirely, every input falls back to free-form text (defensive) and a `Warning` `DiagCategories.ScpdParse` diagnostic is emitted

**Given** an input argument whose related state variable declares `<allowedValueList>` with values `["true","false"]` and `<dataType>boolean</dataType>`
**When** the `ArgumentInputViewModel` is constructed for it
**Then** the VM resolves to an `AllowedValueListArgumentViewModel` variant (sealed subclass of `ArgumentInputViewModel` OR a polymorphic property — pick the cleaner shape; document the choice)
**And** it exposes `IReadOnlyList<string> AllowedValues` populated in declared order
**And** it exposes `[ObservableProperty] string SelectedValue` (FR-102 consequence — Value is one of AllowedValues)
**And** the XAML DataTemplate for this variant renders a `ComboBox` (or `RadioButtons`) bound to `AllowedValues` / `SelectedValue`

**Given** the state variable declares a `<defaultValue>`
**When** the input is constructed
**Then** if `<defaultValue>` is a MEMBER of `<allowedValueList>`, the selector is pre-populated with the default
**And** otherwise the FIRST listed value is pre-populated (FR-102 second consequence)

**Given** the malformed-list edge case (`<allowedValueList>` present but EMPTY, or values cannot be parsed)
**When** the VM is constructed
**Then** the input falls back to free-form text (the base `ArgumentInputViewModel` from Story 3.2)
**And** a `Warning` `DiagCategories.ScpdParse` diagnostic is emitted with `DeviceUuid`, `Url`, `ServiceId`, and an `ErrorText` describing the malformed list (FR-102 fallback)

**Given** an input argument whose related state variable declares `<allowedValueRange>` AND `<dataType>` is numeric (e.g. `ui1`, `ui2`, `ui4`, `i1`, `i2`, `i4`, `int`)
**When** the `ArgumentInputViewModel` is constructed
**Then** the VM resolves to an `AllowedValueRangeArgumentViewModel` variant
**And** it exposes `double Minimum`, `double Maximum`, `double? Step` (parsed from `<minimum>`, `<maximum>`, `<step>`)
**And** it exposes `[ObservableProperty] double NumericValue` (the bound value)
**And** the XAML DataTemplate renders a `NumberBox` (WinUI 3 native) with `SmallChange = Step` (or `1` when `Step` is null), `Minimum`, `Maximum` bound from the VM
**And** the bound value is serialised to `Value` via `NumericValue.ToString(CultureInfo.InvariantCulture)` on Invoke (FR-103 — culture-invariant per UPnP spec)

**Given** `<allowedValueRange>` with a `<defaultValue>`
**When** the input is constructed
**Then** if `<defaultValue>` satisfies the range (AND step where declared), it is pre-populated
**And** otherwise `Minimum` is pre-populated (FR-103 third consequence)

**Given** `<step>` is declared and non-zero
**When** the operator submits a value off-step
**Then** client-side validation fails BEFORE the SOAP request fires (`InvokeAsync.CanExecute` returns false OR the popup shows an inline error and refuses to send)
**And** an inline message indicates the constraint (e.g. "Value must be a multiple of <step> from <minimum>")

**Given** the malformed-range edge case (`<allowedValueRange>` on a non-numeric `<dataType>`, OR `<minimum>` > `<maximum>`, OR `<step>` ≤ 0)
**When** the VM is constructed
**Then** the input falls back to free-form text
**And** a `Warning` `DiagCategories.ScpdParse` diagnostic is emitted (FR-103 fallback)

**Given** an SCPD that declares BOTH `<allowedValueList>` and `<allowedValueRange>` on the same state variable (malformed per UDA 1.0 §2.3)
**When** the VM is constructed
**Then** FR-102 wins (the list constraint is honoured; range is ignored)
**And** a `Warning` `DiagCategories.ScpdParse` diagnostic is emitted (FR-102 last consequence)

**Given** an input argument with neither `<allowedValueList>` nor `<allowedValueRange>`
**When** the VM is constructed
**Then** the input remains free-form text per Story 3.2 (PRD §7 Non-Goal — no `<dataType>`-driven typed inputs in v1)

**Given** the operator presses Invoke with a mix of constrained + free-form inputs
**When** the SOAP request is built
**Then** the resolved string value from each `ArgumentInputViewModel` variant (selected from list / numeric formatted invariant / free text) flows into `SoapArgument.Value` uniformly
**And** the rest of the invocation flow (Story 3.2's success / fault / transport-error handling) operates identically

**Given** the test suite
**When** I run constrained-input tests
**Then** fixture SCPDs exercise: list with default-in-list, list with default-not-in-list, list empty, list with one value; range with default-in-range, default-out-of-range, default-off-step, step zero, step negative, min > max, range on non-numeric dataType
**And** ACs carry `[Trait("ac", "AC-3.x")]` linking to AC-3.3 (fault parse) and to FR-102 / FR-103 (via test name embedding)

---

## Epic 4: GENA Subscription

Right-click → Subscribe opens a subscription popup. SUBSCRIBE goes out with a `CALLBACK` URL pointing at the in-process callback host on the selected adapter. NOTIFY events stream into the popup's newest-first list (~5 K cap, FIFO eviction) with a "Latest property values" summary anchored above. Multiple subscriptions across services run concurrently and independently. One slow / malformed NOTIFY does not block others. Auto-renew before timeout; UNSUBSCRIBE on close; lapsed subscriptions handled cleanly; failed subscribe is reported without an UNSUBSCRIBE attempt. Callback host is hardened (size caps, slowloris defence, connection cap = 8, no Admin / URL ACL).

### Story 4.1: Event Callback Host — `TcpListener` + Hand-Rolled HTTP/1.1 Parser

As a Linn engineer,
I want an in-process callback HTTP server bound to the selected adapter's IPv4 via `TcpListener` with strict framing, lenient header tolerance, size caps, per-phase timeouts, and a connection cap,
So that subscribed devices can deliver NOTIFY events back to ohSpy without requiring Administrator privileges, URL ACL registration, or `HttpListener` — and so that slowloris / body-bomb / connection-flood attacks cannot stall or crash the host.

**Note (Architecture risk carry-forward):** Per Architecture validation, this story is sized at **1.5× the implied story size** ("hand-rolled HTTP is where confident architectures meet humbling reality"). The implementing dev agent should expect the parser implementation, the `TimeoutStream` wrapper, and the malformed-input AC matrix to take meaningfully more time than a typical Story.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Events/IEventCallbackHost.cs`
**When** I inspect the interface
**Then** it declares `Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)`, `Uri CallbackBaseUrl { get; }`, `event Func<NotifyRequest, Task> NotifyReceived`, and is `IAsyncDisposable` (D4)

**Given** `ohSpy.Core/Events/NotifyRequest.cs`
**When** I inspect the record
**Then** it is `public sealed record NotifyRequest(string Sid, long Seq, string PathAndQuery, byte[] Body, DateTime ReceivedUtc)` (D4)

**Given** `ohSpy.Core/Events/EventCallbackHost.cs` impl
**When** `StartAsync` runs
**Then** it constructs a `TcpListener` bound to `(adapterIPv4, 0)` (ephemeral port)
**And** calls `Start(backlog: 16)`
**And** exposes `CallbackBaseUrl` as `http://<adapterIPv4>:<port>/` for the SUBSCRIBE `CALLBACK` header (FR-032 consequence)
**And** accepts up to **8 concurrent connections** — the 9th is accepted-then-immediately-closed with a `Warning` `DiagCategories.GenaCallbackFlood` diagnostic (AC-4.7)
**And** every accepted connection has its lifetime bounded to a single request (`Connection: close` in every response — no keep-alive)

**Given** the per-connection budgets
**When** a connection is accepted
**Then** the connect → headers-complete budget is 5 s (`HttpTimeoutOptions.CallbackHeaders` — Decision 11)
**And** the headers-complete → body-complete budget is a separate 5 s (`HttpTimeoutOptions.CallbackBody`) — total worst case 10 s per connection (AC-4.3 + AC-4.4)
**And** the max header block size is 16 KB (AC-4.1)
**And** the max body size is 1 MB (AC-4.2)
**And** the max number of headers is 64

**Given** `ohSpy.Core/Events/TimeoutStream.cs`
**When** I inspect the wrapper
**Then** it wraps a raw `NetworkStream` and throws on any read whose idle time exceeds the active budget (headers or body, depending on parser phase)
**And** the active budget is set by the parser as it transitions phases (D4 — "one place to enforce timeout discipline")

**Given** `ohSpy.Core/Events/HttpRequestParser.cs`
**When** I parse an incoming request
**Then** the request line is parsed as `METHOD SP request-target SP HTTP-version CRLF` (strict — exactly two SP; method is uppercase ASCII tokens; bare CR rejected, bare LF accepted) (D4)
**And** header lines are parsed per RFC 7230 §3.2.6 with case-insensitive read and canonical lowercase internally
**And** an empty CRLF terminates the header block
**And** `Content-Length` MUST be present and parseable as non-negative integer ≤ 1 MB; absence returns `411 Length Required` (AC-4.5)
**And** `Transfer-Encoding: chunked` is REJECTED with `400` (AC-4.6 — defer chunked support until a real vendor needs it; out of v1)
**And** whitespace-folded headers (obsolete RFC 7230 §3.2.4) are rejected with `400`
**And** duplicate `Content-Length` returns `400`
**And** duplicate known headers (NT/NTS/SID/SEQ) use last-wins
**And** unknown headers are ignored (counted against the 64-header cap)

**Given** a valid NOTIFY arrives
**When** the parser succeeds
**Then** the host extracts SID, SEQ, path-and-query, and body bytes
**And** raises `NotifyReceived(new NotifyRequest(...))` — handlers are awaited; the host tracks in-flight tasks to drain on shutdown
**And** returns `200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n` to the device (AC-4.8)
**And** the body is NOT parsed as `<e:propertyset>` XML by the host — that's the subscription popup VM's job (FR-104 non-serial processing above the host; Story 4.3)

**Given** malformed inputs
**When** the parser detects each failure mode
**Then** the response shape is per Decision 4:
- Malformed framing → `400 Bad Request` + `Connection: close` + `Warning` `GenaCallbackMalformed` (AC-4 row 2)
- Missing `Content-Length` → `411 Length Required` + close + `Warning` `GenaCallbackNoLength` (AC-4.5)
- Oversized headers (> 16 KB) → `413 Content Too Large` + close + `Warning` `GenaCallbackOversize` (AC-4.1)
- Oversized body (> 1 MB) → `413` + close + `Warning` `GenaCallbackOversize` (AC-4.2)
- Headers stalled (> 5 s) → close + `Warning` `GenaCallbackHeadersTo` (AC-4.3)
- Body stalled (> 5 s after headers) → close + `Warning` `GenaCallbackBodyTo` (AC-4.4)
- Internal dispatch error → `500 Internal Server Error` + close + `Warning` with stack trace
**And** each Warning carries `RemoteEndpoint` context (per Pattern 11 — `DeviceUuid` is not yet known at the callback host layer)

**Given** subscription unknown / cancelled
**When** a NOTIFY arrives for a SID the dispatcher does not recognise
**Then** the host returns `200 OK` (idempotent ack — device may already be unsubscribed from our side)
**And** no `NotifyReceived` event fires for that SID (or fires harmlessly with no subscriber)

**Given** adapter switch
**When** the host's `DisposeAsync` runs
**Then** the listener stops accepting new connections
**And** in-flight connections are drained or force-closed within a 2 s budget that matches FR-050 (AC-4.9 + D7 atomic-rebind)
**And** the next `StartAsync` on a new adapter constructs a fresh listener (host instances are scope-bound, not long-lived singletons across adapter switches)

**Given** the test suite
**When** I run callback-host tests
**Then** `tests/ohSpy.Core.Tests/Fakes/FakeGenaClient.cs` is the hand-rolled raw `TcpClient` driver that opens connections and sends / withholds bytes for each AC
**And** `SlowlorisTest` opens 8 connections trickling 1 byte every 4 s; all 8 hit the 5 s headers timeout; all close cleanly; the 9th connection opens immediately after a slot frees (AC-4.3 + AC-4.7 combined)
**And** `FloodTest` opens 50 connections in a tight loop; 8 are served; 42 are accepted-then-immediately-closed with `GenaCallbackFlood` Warnings; no thread/socket leak (AC-4.7)
**And** every AC carries `[Trait("ac", "AC-4.x")]`

---

### Story 4.2: Subscription Client — SUBSCRIBE / RENEW / UNSUBSCRIBE Lifecycle with Auto-Renewal

As an ohSpy developer,
I want a `SubscriptionClient` that orchestrates the full GENA subscription lifecycle (SUBSCRIBE → auto-renew before timeout → UNSUBSCRIBE on close), routes incoming NOTIFY messages to subscribers by SID, and applies the "cleanup uses level-above token" invariant for UNSUBSCRIBE,
So that the popup in Story 4.3 can take one `Subscribe(serviceUrl)` call and trust that all the lifecycle plumbing runs correctly — including renewal that keeps events flowing across multi-minute sessions, and UNSUBSCRIBE that fires on close even though the popup-level CTS has just been cancelled.

**Acceptance Criteria:**

**Given** `ohSpy.Core/Events/SubscriptionClient.cs` (or `ISubscriptionClient` + impl — engineering judgment whether to abstract; document the choice)
**When** I inspect its surface
**Then** it exposes `Task<SubscriptionHandle> SubscribeAsync(ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken)` returning a handle the popup uses to receive events and to close the subscription
**And** the `SubscriptionHandle` exposes `string Sid { get; }`, `event Action<EventNotification> NotificationReceived`, `event Action<SubscriptionLapseReason> Lapsed`, and `Task CloseAsync()` (idempotent — multiple calls are safe)

**Given** `ohSpy.Core/Models/EventNotification.cs`
**When** I inspect the record
**Then** it is `public sealed record EventNotification(string Sid, long Seq, DateTime ReceivedUtc, IReadOnlyDictionary<string, string> Properties)` (per-NOTIFY parsed `<e:propertyset>` body)
**And** `SubscriptionLapseReason` is an enum with at least `RenewRefused`, `RenewTransportError`, `AdapterSwitch`, `DeviceGone`

**Given** `SubscribeAsync` runs against a happy device
**When** the call executes
**Then** it calls `IUpnpHttpClient.SubscribeAsync(service.EventSubUrl, _callbackHost.CallbackBaseUrl, TimeSpan.FromSeconds(<initial>), popupToken)` (initial requested TIMEOUT: e.g. 300 s — engineering judgment within UPnP norms)
**And** on the 200 OK response it extracts `SID` and `TIMEOUT` (parsed as `Second-<n>` per UDA 1.0 §4.1.2)
**And** the client registers the SID with the callback host (`IEventCallbackHost.NotifyReceived` is the source; the client filters by SID and routes to the right handle)
**And** returns the handle

**Given** the failed-subscribe path (FR-035)
**When** `SubscribeAsync` throws `UpnpTransportException` / `UpnpTimeoutException` / `UpnpProtocolException` (the response is parsed-malformed)
**Then** the handle is NOT returned — the caller observes the thrown exception
**And** the client does NOT register any SID (there is no SID to register)
**And** a `Warning` `DiagCategories.GenaSubscribeFailed` diagnostic is emitted per Pattern 11
**And** subsequent close attempts on a never-created subscription do NOT attempt UNSUBSCRIBE (FR-035 — no SID = no unsubscribe)

**Given** the auto-renewal background task (FR-038)
**When** a subscription is active and the device-granted TIMEOUT is approaching expiry
**Then** the client triggers `IUpnpHttpClient.RenewSubscriptionAsync(eventSubUrl, sid, TimeSpan.FromSeconds(<requested>), adapterTokenOrPopupToken)` BEFORE the timeout expires (target: at 80% of the device-granted TIMEOUT — concrete budget documented in impl)
**And** on success the new TIMEOUT replaces the prior; the renewal task reschedules
**And** event delivery continues uninterrupted (FR-038)

**Given** renewal is refused (HTTP 412 Precondition Failed — UDA 1.0 §4.1.2) OR the renewal fails transport-level
**When** the failure is observed
**Then** the renewal task stops attempting further renewals (FR-038 first consequence)
**And** the handle raises `Lapsed(SubscriptionLapseReason.RenewRefused or RenewTransportError)` (FR-038 — popup informs the operator that the subscription has lapsed)
**And** the client marks the SID as lapsed internally; closing the handle does NOT attempt UNSUBSCRIBE (FR-038 + FR-035 — UNSUBSCRIBE on an expired subscription is forbidden)
**And** a `Warning` `DiagCategories.GenaRenewFailed` diagnostic is emitted per Pattern 11

**Given** `SubscriptionHandle.CloseAsync` runs (popup close)
**When** the subscription is still ACTIVE (not lapsed)
**Then** the client cancels its internal popup-derived state and runs UNSUBSCRIBE via the **cleanup-uses-level-above-token** invariant (D7 — AC-7.3 + AC-7.5):
- A NEW `CancellationTokenSource` with a 5 s budget is constructed for the UNSUBSCRIBE
- It is linked with the `_adapterToken` — NOT the now-cancelled popup token
- `IUpnpHttpClient.UnsubscribeAsync(eventSubUrl, sid, linked.Token)` is called
- A failed UNSUBSCRIBE is swallowed (popup close MUST NOT block on a hung device — FR-034 contract is "send UNSUBSCRIBE", not "guarantee delivery") and a `Warning` `DiagCategories.GenaUnsubscribeFailed` diagnostic is emitted

**When** `CloseAsync` runs on a LAPSED subscription
**Then** no UNSUBSCRIBE is sent (FR-038 + FR-035)
**And** the SID is de-registered from the callback host's routing table
**And** internal state is disposed cleanly

**Given** an adapter switch happens while the subscription is active
**When** `_adapterCts` cancels
**Then** the renewal task observes cancellation and exits (no further renewals attempted)
**And** the handle raises `Lapsed(SubscriptionLapseReason.AdapterSwitch)`
**And** the subscription is NOT closed via UNSUBSCRIBE — the device is no longer reachable on this adapter (D7 cascade behaviour matches FR-037)

**Given** the device disappears (`byebye`, registry remove)
**When** `parentEntry.DeviceCts` cancels (cascades to the popup CTS the client received)
**Then** the renewal task exits
**And** the handle raises `Lapsed(SubscriptionLapseReason.DeviceGone)`
**And** no UNSUBSCRIBE is attempted (the device is gone)

**Given** the NOTIFY routing path
**When** the callback host raises `NotifyReceived(NotifyRequest)` with a known SID
**Then** the client looks up the matching `SubscriptionHandle` by SID
**And** parses the `NotifyRequest.Body` as `<e:propertyset>` XML — using the same XmlReaderSettings discipline from Story 1.4
**And** constructs an `EventNotification` with the per-property dictionary
**And** raises the handle's `NotificationReceived(notification)` event
**And** the parse runs on a worker (not the host's accept task) so a slow parse does NOT block subsequent NOTIFY ingest (FR-104 — non-serial NOTIFY processing across subscriptions)

**Given** the FR-104 non-serial discipline drill
**When** subscription A receives a slow-parsing NOTIFY
**Then** subscription B's incoming NOTIFY is processed without waiting on A's parse to finish
**And** subscription A's subsequent NOTIFY is processed without waiting on its earlier NOTIFY's parse (per-subscription queues are bounded — overflow is by FIFO tail-eviction; back-pressure to the device is NOT used)
**And** an integration test exercises this: subscription A simulated parse delay 200 ms; subscription B's NOTIFY observed end-to-end under 50 ms (FR-104 consequence)

**Given** the DI composition root
**When** the App starts
**Then** `SubscriptionClient` (or `ISubscriptionClient`) is registered as a singleton
**And** it injects `IUpnpHttpClient`, `IEventCallbackHost`, `IUiDispatcher`, `IDiagnosticEmitter`, and the adapter-level `CancellationToken`

---

### Story 4.3: Subscription Popup — Event List, Latest Property Values, Multiple Concurrent Popups

As a Linn engineer,
I want right-click → Subscribe on a service to open a popup that shows incoming NOTIFY events newest-first in a virtualised list, with a "Latest property values" summary anchored above showing each evented property's most-recent value, supporting multiple concurrent popups across different services, and surviving device disappearance,
So that I can watch a streamer's transport state, queue, volume, etc. update live as I drive the device — and have multiple services under observation simultaneously without one slow service blocking another.

**Acceptance Criteria:**

**Given** `src/ohSpy.App/Views/SubscriptionPopupWindow.xaml` + `.xaml.cs`
**When** I inspect the window
**Then** the layout shows the service identifier as a header, the "Latest property values" summary panel anchored at top (always visible regardless of event-list scroll), the scrolling event list below, and a status indicator (subscribed / lapsed / device-gone)
**And** the code-behind is constructor-only (Pattern 13)

**Given** `src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs`
**When** I inspect it
**Then** the constructor takes `ServiceDescription service`, `RegistryEntry parentEntry`, plus injected `ISubscriptionClient` (or equivalent), `IUiDispatcher`, `IDiagnosticEmitter`
**And** exposes `BoundedObservableCollection<EventNotification> Events` constructed with capacity 5,000 (FR-033 + D6)
**And** exposes `ObservableDictionary<string, string> LatestPropertyValues` (or equivalent overwrite-in-place observable map — engineering judgment; document the choice) bound to the summary panel (FR-033)
**And** exposes `[ObservableProperty] SubscriptionStatus Status` (enum: `Subscribing | Subscribed | Lapsed | DeviceGone`)
**And** exposes `[ObservableProperty] string? StatusMessage` carrying any human-readable detail (e.g. "device-granted TIMEOUT: 300 s", or "renewal refused")

**Given** the popup-open trigger
**When** the operator chooses Subscribe from `ServiceNodeViewModel`'s right-click menu (Story 2.8 wired the stub; this story implements the real handler)
**Then** the command routes to `ShellViewModel.OpenSubscriptionPopupCommand(service)` (or equivalent), which constructs `new SubscriptionPopupWindow(subscriptionVm)`, calls `Activate()`, then `_windowOwnership.Adopt(subscriptionPopup, _shellWindow)` (D10 reuse — FR-046)
**And** the popup VM's initialization calls `_subscriptionClient.SubscribeAsync(service, parentEntry, _popupCts.Token)` on a worker
**And** during the subscribe attempt `Status = Subscribing`

**Given** the subscribe succeeds
**When** the handle is returned
**Then** `Status = Subscribed`
**And** the VM subscribes to `handle.NotificationReceived` → marshals via `IUiDispatcher.Post` → `Events.PrependNewest(notification)` (FR-033 newest-first)
**And** the VM also updates `LatestPropertyValues[name] = value` for each property in the notification (overwrite-in-place — FR-033 anchored summary)
**And** the VM subscribes to `handle.Lapsed` → marshals to UI thread → `Status = Lapsed` with `StatusMessage` carrying the reason (FR-038)

**Given** the subscribe fails (FR-035)
**When** `SubscribeAsync` throws
**Then** `Status` transitions to a failed variant (e.g. add `FailedToSubscribe` to the enum) with the human-readable error
**And** the popup informs the operator (FR-035 — "inform the operator")
**And** the popup is closeable (the close button still works) — and closing does NOT attempt UNSUBSCRIBE since there is no SID (FR-035)

**Given** the multiple-popups-concurrent invariant (FR-036)
**When** the operator opens 5 subscription popups across different services
**Then** each popup runs independently — each has its own `SubscriptionHandle`, its own event list, its own latest-property-values map
**And** a slow / malformed NOTIFY on one subscription does NOT block any other subscription's NOTIFY delivery (FR-104 — verified end-to-end via integration test from Story 4.2's discipline drill, now observed through the popup VMs)

**Given** the SubscribeUrlMenuItem from Story 2.8 — the "Subscribe" item was stubbed
**When** this story replaces the stub
**Then** `ServiceNodeViewModel.SubscribeCommand` invokes the real `OpenSubscriptionPopupCommand` (FR-018 second item wired)
**And** the "coming in Epic 4" flyout / hidden state from Story 2.8 is removed

**Given** the FIFO eviction
**When** the 5,001st event arrives at capacity
**Then** the oldest tail event is discarded (FR-033 cap)
**And** `BoundedObservableCollection.PrependNewest` emits `Add(0)` + `Remove(5000)` — never `Reset` (AC-6.1)

**Given** the event-list rendering
**When** the popup is open and events stream in
**Then** the bound visual is item-virtualised (`ItemsRepeater` or equivalent — NFR-P1) so memory + per-frame cost scales with visible rows, not buffered events
**And** the latest-property-values summary panel remains anchored at the top of the popup INDEPENDENT of the event list's scroll position (FR-033)
**And** a high-frequency event burst on a busy service does NOT produce visible stutter (NFR-UI4)

**Given** the popup-close path
**When** the operator closes the popup
**Then** `OnClosing` calls `handle.CloseAsync()` which executes the UNSUBSCRIBE-with-adapter-token discipline from Story 4.2 (FR-034 + D7 cleanup-uses-level-above)
**And** `_popupCts.Cancel()` is called for any other in-flight popup-scoped work
**And** `_popupCts.Dispose()` runs in a `finally` block
**And** the popup closes cleanly within a budget that does not block the user's close action visually (the UNSUBSCRIBE runs to completion or its 5 s budget asynchronously; the popup window closes immediately)

**Given** the device disappears mid-subscription
**When** `parentEntry.DeviceCts` cancels
**Then** `handle.Lapsed(DeviceGone)` is raised by the subscription client
**And** the VM transitions to `Status = DeviceGone` with a banner — same shape as the Properties window's "device no longer reachable" state (FR-037)
**And** the popup remains closeable without errors (NFR-R3)
**And** closing the popup in the DeviceGone state does NOT attempt UNSUBSCRIBE (Story 4.2 client behaviour)

**Given** adapter switch
**When** `_adapterCts` cancels (E5 will fire this; this story handles the cascade)
**Then** `handle.Lapsed(AdapterSwitch)` is raised
**And** the VM transitions to `Status = Lapsed` with a "device unreachable after adapter switch" message
**And** the popup remains closeable — closing performs no UNSUBSCRIBE (the device is unreachable)

**Given** the integration test
**When** I run a full subscription drill
**Then** a `FakeUpnpDevice` mode "EventEmittingService" emits canned NOTIFY messages at a steady rate
**And** the popup VM observes events flowing in newest-first
**And** the summary panel updates overwrite-in-place
**And** opening 5 concurrent popups against 5 services verifies FR-036 and FR-104 (one slow-parser does not block others)
**And** the close-cascade drill (Story 4.2 AC) is exercised through the popup VM's `OnClosing` path

---

## Epic 5: Operator Tooling — Diagnostics, Adapter Switch, Rescan

`View → Diagnostics` opens a live diagnostic viewer with Identity / Endpoint columns resolved at arrival (snapshot semantics). `View → Network adapter` lists every eligible IPv4 adapter as radio items; selecting a different adapter atomically rebinds (tear down SSDP + callback host, clear registry, cancel in-flight fetches, notify open popups, rebind, re-discover). `View → Rescan` re-runs the M-SEARCH and prunes non-responders without suspending live NOTIFY handling.

### Story 5.1: Diagnostics Viewer Window

As a Linn engineer,
I want `View → Diagnostics` to open a live, virtualised diagnostic viewer showing every entry the emitter has recorded — with timestamp, severity, category, message, Identity column, and Endpoint column — so I can investigate failures (SSDP parse errors, description fetch failures, SOAP faults, subscription lapses, etc.) without restarting the tool or grepping the on-disk log file.

**Acceptance Criteria:**

**Given** `src/ohSpy.App/Views/DiagnosticsWindow.xaml` + `.xaml.cs`
**When** I inspect the window
**Then** the layout is a single virtualised list of diagnostic rows with columns: Timestamp (UTC, formatted `HH:mm:ss.fff`), Severity, Category, Message, Identity, Endpoint (FR-041)
**And** each row carries a severity-colour brush (Warning amber, Error red, Information neutral, Verbose muted) via `SeverityToBrushConverter` (App-side converter)
**And** the code-behind is constructor-only (Pattern 13)

**Given** `src/ohSpy.Core/ViewModels/DiagnosticsViewModel.cs`
**When** I inspect it
**Then** it exposes `BoundedObservableCollection<DiagnosticRow> Entries` bound to the same instance the `DiagnosticRingSink` populates (AC-8.2 — no copy, no view layer)
**And** it exposes `[ObservableProperty] DiagSeverity MinSeverity` with a default of `Information` (D8 — runtime-flippable, not persisted)
**And** it exposes filter UI affordances (severity chip selector at minimum; category filter chips are an open follow-up per D8)

**Given** the right-pane shell menu
**When** I look at `MainWindow.xaml`'s menu bar
**Then** there is a `View` menu containing `Diagnostics` (this story), `Network adapter` (Story 5.2), and `Rescan` (Story 5.3) — added in this story as a complete menu shape; Story 5.2 / 5.3 wire their respective handlers
**And** choosing `View → Diagnostics` invokes `ShellViewModel.OpenDiagnosticsCommand`
**And** the command constructs `new DiagnosticsWindow(diagnosticsVm)`, calls `Activate()`, then `_windowOwnership.Adopt(diagnosticsWindow, _shellWindow)` (D10 reuse — FR-046)

**Given** the viewer is open
**When** new diagnostic entries arrive
**Then** they appear at the top of the list within the next dispatcher tick (D8 — ring sink dispatches via `IUiDispatcher.Post`)
**And** the viewer remains responsive while entries arrive at high rates (FR-041 first consequence)
**And** opening the viewer for the first time mid-session displays all entries that have arrived since app start (up to the 5,000-entry ring cap)

**Given** the Identity column
**When** I look at an entry whose `DiagnosticContext.DeviceUuid` is set
**Then** the Identity label is the device's `FriendlyName` if the device is in the registry and has one (AC-8.3 + FR-041 first rule)
**And** otherwise the label is `"uuid:<uuid>"` (FR-041 second rule)
**And** entries with no `DeviceUuid` context render as a muted `"—"` placeholder (FR-041 third rule)
**And** resolution is snapshot-at-arrival — the label does NOT change if the device's friendly name changes later or the device leaves the registry (FR-041 invariant — preserved by `DiagnosticRingSink.Push` doing the resolution before prepend)

**Given** the Endpoint column
**When** I look at an entry whose `DiagnosticContext.Url` is set
**Then** the Endpoint label is `host` (when port is the URI's default for the scheme) or `host:port` (when non-default) (AC-8.4)
**And** entries with `RemoteEndpoint` instead (e.g. `Ssdp.Parse` failures, `Gena.Callback.*`) display `RemoteEndpoint` directly
**And** entries with neither render as `"—"`

**Given** the diagnostic file sink fails to initialise at app startup
**When** the app launches anyway (FR-042)
**Then** the viewer is still functional (the ring sink continues to work)
**And** the single `Warning` `DiagCategories.DiagnosticsFileSinkUnavailable` entry is visible in the viewer (NFR-R4 — the user sees the diagnostic about diagnostics)

**Given** filtering UI
**When** I change `MinSeverity` to `Warning`
**Then** the viewer hides `Verbose` and `Information` rows (visual filter; the underlying ring buffer is NOT mutated — filtering is a view concern)
**And** subsequent `Verbose` / `Information` arrivals do NOT increment visible rows
**And** the filter state does NOT persist across app restart (PRD §7 Non-Goal — no settings persistence)

**Given** the integration test
**When** I emit 100 diagnostics at various severities through `IDiagnosticEmitter`
**Then** they appear in the viewer's `Entries` in newest-first order
**And** identity/endpoint resolution matches AC-8.3 / AC-8.4 across all entries
**And** the viewer's bound control is item-virtualised (NFR-P1) — confirmed by inspecting memory usage as the ring fills to capacity

---

### Story 5.2: Adapter Switch — `View → Network adapter` Menu + Atomic Rebind

> **Re-sequenced 2026-06-04 (Epic 3 retrospective):** this story now executes as the **last story of Epic 4**, not Epic 5. Its atomic-rebind sequence depends on Story 4.1's `EventCallbackHost` (steps 3 + 9) and Story 4.3's subscription popup (the popup-teardown AC) — a one-way forward dependency, so it must run after 4.1/4.3. Prerequisite: the **A23 transport-factory** refactor (the SSDP transport is still a Story 2.1 singleton; rebind needs per-adapter dispose + reconstruct). The story key stays `5-2-…` to preserve architecture A23 / FR-050 / cross-references; only the execution order moves. See `epic-3-retro-2026-06-04.md`.

As a Linn engineer,
I want `View → Network adapter` to show a radio list of every eligible IPv4 adapter (with the current one indicated) and let me select a different adapter to trigger an atomic rebind — tearing down the SSDP transport + callback host, clearing the registry, cancelling in-flight fetches, notifying every open popup, rebinding on the new adapter, and re-running the startup discovery — all within the FR-050 2 s budget,
So that I can move between development networks (lab Wi-Fi, wired test rig, dev laptop) without restarting the tool.

**Acceptance Criteria:**

**Given** the `View → Network adapter` menu
**When** I open it
**Then** the menu is populated dynamically via `NetworkAdapterEnumerator.Enumerate()` (Story 2.2) — every eligible IPv4 adapter is listed as a `RadioMenuFlyoutItem` showing friendly name + IPv4 address
**And** the currently-active adapter is checked
**And** if there is only zero or one eligible adapter, the menu still opens but contains a single disabled item ("no other adapters available") — the operator can verify there is no alternative

**Given** I choose a different adapter
**When** the `RadioMenuFlyoutItem`'s command fires
**Then** `ShellViewModel.SwitchAdapterAsync(newAdapter)` runs (FR-048 half-B + FR-050 trigger)
**And** the menu closes
**And** the UI shows a brief "Switching adapter…" transient state (NFR-UI3 — feedback without flicker)

**Given** `SwitchAdapterAsync` executes the FR-050 atomic-rebind sequence
**When** the sequence runs
**Then** the order is (D7 atomic-switch sequence verbatim):
1. `_adapterCts.Cancel()` — signal cascades to every linked CTS (transports, callback host, registry entries, popups)
2. `await SsdpTransport.DisposeAsync()` — sockets + channel torn down
3. `await EventCallbackHost.DisposeAsync()` — TcpListener stopped; in-flight callback connections drained
4. Cancel + dispose every `RegistryEntry.DeviceCts` (already cancelled via linkage; this is dispose-only)
5. Drain in-flight fetch tasks (await with budget 2 s)
6. `DeviceRegistry.Clear()` — raises `DeviceRemoved` per UUID; tree drops rows; SSDP log clears via separate `Clear()` call (Story 2.7 already covers the log clear)
7. Dispose `_adapterCts`
8. Construct new `AdapterScope` on the new adapter IPv4
9. New `SsdpTransport.StartAsync` + `EventCallbackHost.StartAsync`
10. `SsdpTransport.SendMSearchAsync(5 s MX)` — re-runs the startup discovery sweep (FR-050 step (f) + FR-004 reuse)

**And** the entire sequence completes within 2 s (FR-050 budget + AC-7.1)
**And** if step 5's drain exceeds 2 s, force-tear-down proceeds and emits a `Warning` `DiagCategories.AdapterSwitchTimeout` diagnostic (D7 — "we don't block UX on hung tasks")
**And** an `Information` `DiagCategories.AdapterSwitch` diagnostic is emitted at start and end of the switch with old + new adapter IPs in context

**Given** open popups during the switch
**When** `_adapterCts` cancels
**Then** every open Properties window (Story 2.9), invocation popup (Story 3.2), and subscription popup (Story 4.3) transitions to its FR-037 device-unreachable state (NFR-R3)
**And** no popup crashes
**And** no popup blocks the switch sequence (popup transitions are dispatched; the switch awaits its own work, not popups)

**Given** the switch completes successfully
**When** the new adapter is active
**Then** the device tree is empty (cleared) and refills as M-SEARCH responses + unsolicited NOTIFYs arrive
**And** the SSDP log is empty (cleared) and refills as new datagrams arrive
**And** the diagnostic viewer (if open) continues to show historical entries — diagnostics persist across the switch (the ring sink is app-lifetime, not adapter-scoped)
**And** the `View → Network adapter` menu's check mark moves to the new adapter

**Given** a switch to an adapter that has zero responding devices
**When** the M-SEARCH MX elapses with no responses
**Then** the app remains running with an empty tree (NFR-R5 carried across switch path)
**And** unsolicited NOTIFYs can still populate the tree later

**Given** the switch is aborted mid-flight (e.g. app shutdown)
**When** `_appCts` cancels during the switch sequence
**Then** the in-progress steps abort cleanly
**And** the new transport / callback host (if partially constructed) is disposed
**And** the app shuts down without errors

**Given** the test suite
**When** I run the adapter-switch tests
**Then** an integration test simulates 10 devices on the old adapter with in-flight fetches; the switch is triggered; AC-7.1 asserts every fetch throws `OperationCanceledException` within 100 ms; no fetch posts to a disposed VM
**And** a popup-cascade test asserts that an open Properties + Invocation + Subscription popup all transition to their device-unreachable state on switch
**And** ACs carry `[Trait("ac", "AC-7.x")]` / `[Trait("ac", "AC-4.9")]` (callback-host drain budget reuse from Story 4.1)

---

### Story 5.3: Rescan — `View → Rescan` Menu + Prune Non-Responders

As a Linn engineer,
I want `View → Rescan` to re-issue the M-SEARCH on the current adapter, wait MX seconds, and prune any device that did not respond to the rescan — without suspending the live unsolicited-NOTIFY listener,
So that I can clean up stale devices that left the network ungracefully (no `byebye`) without restarting the tool, and so I can confirm "this device really is gone" vs "we just haven't seen it advertise recently."

**Acceptance Criteria:**

**Given** the `View → Rescan` menu item (added to the shell in Story 5.1)
**When** I choose it
**Then** `ShellViewModel.RescanCommand` invokes `DiscoveryService.RescanAsync(mx: TimeSpan.FromSeconds(5))` (FR-021)
**And** the menu item is disabled during a rescan (re-entrancy guard; the user cannot trigger overlapping rescans)
**And** a brief "Rescanning…" indicator is visible (e.g. a status bar message or an inline spinner) (NFR-UI3 — feedback without flicker)

**Given** `DiscoveryService.RescanAsync(mx)`
**When** it runs
**Then** it issues an M-SEARCH via `ISsdpTransport.SendMSearchAsync(mx, _adapterToken)` (FR-022 — identical semantics to startup discovery, same `ST: upnp:rootdevice`)
**And** it tracks the set of UUIDs that respond during the MX window
**And** the live unsolicited-NOTIFY listening continues unaffected (FR-024 — no socket teardown, no consumer-side suspension)

**Given** the MX window elapses
**When** the prune phase runs
**Then** every UUID in the registry that did NOT respond during the rescan AND did NOT receive an unsolicited NOTIFY during the rescan window is removed (FR-023)
**And** the removal cascades exactly like a `byebye` — `_deviceCts.Cancel()`, `DeviceRemoved(uuid)`, open popups transition to FR-037 state
**And** devices that DID respond (or that received an unsolicited NOTIFY during the window) remain in the registry untouched — their `LastSeenUtc` / `AliveCount` already refreshed

**Given** the test suite
**When** I run a rescan drill
**Then** a test fixture populates the registry with 5 devices (A..E), then issues a rescan where only A/B/C respond; D and E are pruned; A/B/C remain
**And** an integration test verifies FR-024: during the rescan, an unsolicited `alive` for E arrives; E is NOT pruned (it announced itself during the window)
**And** another integration test verifies FR-024: during the rescan, an unsolicited `byebye` for A arrives; A is removed via the byebye path; the rescan-prune does NOT re-remove an already-gone entry
**And** ACs reference FR-021..FR-024 in the test names per Pattern 15

**Given** a rescan triggered concurrently with an adapter switch
**When** the user fires both rapidly
**Then** the adapter switch wins — `_adapterCts.Cancel()` aborts the in-flight rescan; the new adapter starts fresh
**And** no exception surfaces to the user; a `Warning` diagnostic notes the rescan was abandoned

**Given** the diagnostic emission discipline
**When** the rescan completes
**Then** an `Information` diagnostic (e.g. `DiagCategories.AdapterSwitch` reused or a new `DiagCategories.Rescan` constant added) is emitted with the count of pruned devices in context — operator can see "rescan pruned 2 devices" in the viewer

---

## Epic 6: Polish, Soak & Release Readiness

A built installer that lands cleanly on a fresh Windows 11 machine, runs the 30-min no-crash debugging session, holds the 8-hour 200 MB scale ceiling under load, and demonstrates the FR-044 / FR-046 / FR-054 manual UI behaviours that red-green-refactor TDD can't enforce. Performance Budgets (SC-*) verified end-to-end. Ready for L&L.

**Note:** This epic delivers no new FRs. It is verification-only, anchored on the Architecture's "Polish & Soak story (before release) — Murat's recommendation." The deliverables are a documented verification pass for each story — observed behaviour matching the architecture's spec.

### Story 6.1: Manual UI Verification — FR-044 / FR-046 / FR-054 Behaviours

As a Linn engineer,
I want a human walkthrough confirming every FR that automated tests can't fully cover — the chevron-no-collapse-during-load behaviour, popup z-order / minimise / restore / close-with-parent behaviours, row-migration identity preservation, kind-glyph rendering, secondary-detail-line muted styling — observed visually against a real LAN,
So that the L&L demo doesn't get derailed by a polish defect that integration tests passed but the eye catches.

**Acceptance Criteria:**

**Given** a build of ohSpy run against the dev LAN with 10–20 announcing UPnP devices
**When** I observe device-tree row rendering
**Then** every device row shows: kind glyph (leading) + friendly name (primary, default weight) + secondary detail line (muted brush; deviceType-tail + middle-dot + host:port) (FR-045 + FR-051 + NFR-UI2)
**And** there is no flicker on incremental updates (NFR-UI3) — labels do not disappear / reappear when re-announces refresh metadata

**Given** I expand a device whose services have not yet loaded
**When** the chevron appears
**Then** the chevron is rendered IMMEDIATELY when the device row first appears (FR-044 — persistent expand chevron via "Loading…" placeholder)
**And** clicking the chevron expands to show the "Loading…" placeholder for ~0 to ~2 s while service entries populate
**And** the chevron does NOT disappear and re-appear during the load (NFR-UI3 + AC-A1.4 — atomic ReplaceWith)

**Given** I expand a service whose SCPD has not yet been fetched (FR-012 + FR-100)
**When** the action list streams in
**Then** I observe actions appearing one-by-one (or in small batches) for large SCPDs — the UI does NOT freeze
**And** the chevron does NOT collapse during the stream
**And** the cold large-SCPD expand completes within ≤ 2 s on the test baseline (Performance Budget "Cold large-SCPD expand" — verified by stopwatch or video capture against a 100+-action SCPD)

**Given** I expand an action node
**When** I observe its row
**Then** NO expand chevron is rendered (FR-044 second consequence + AC-A1.3 — actions are leaves)

**Given** a re-announce changes a device's friendly name (e.g. firmware update during the demo)
**When** the row migrates to its new sorted position (FR-054)
**Then** the migration is in-place — selection state, expansion state, scroll position are preserved (FR-054 + AC-6.4)
**And** sibling subtrees are NOT redrawn (NFR-P5 visible to the eye — no flash)

**Given** I open all four popup types in sequence — Properties (right-click device), Invocation (double-click action), Subscription (right-click service → Subscribe), Diagnostics (View → Diagnostics)
**When** each is open
**Then** the popup appears ABOVE the main window when shown (AC-10.1)
**And** clicking the main window for focus does NOT push the popup behind it (AC-10.4)

**Given** all four popups are open and I minimise the main window
**When** I observe the taskbar / desktop
**Then** every popup minimises together with the main window (AC-10.3)

**Given** all four popups are minimised
**When** I restore the main window from the taskbar
**Then** every popup restores together (AC-10.3)

**Given** all four popups are open
**When** I close the main window
**Then** every popup closes automatically (AC-10.2)
**And** there is no exception, no error dialog, no leftover window in the taskbar

**Given** the SSDP log under a chatty network burst (≥ 20 adv/s for ≥ 30 s per test baseline)
**When** I watch the log render
**Then** there are no visible dropped frames (NFR-UI4)
**And** scrolling away from the top while bursting continues does NOT yank back to top (FR-055 — smart auto-follow respected)
**And** scrolling back to the top re-engages auto-follow (FR-055)

**Given** WinUI 3 design conformance (NFR-UI1)
**When** I review the app side-by-side with the WinUI 3 design guidelines
**Then** typographic hierarchy, spacing, and colour broadly conform (no claim of pixel-perfect conformance — "considered" per NFR-UI1)
**And** any deviations are documented as conscious choices (e.g. dense layout for developer audience) rather than oversights

**Given** the verification pass
**When** the story is closed
**Then** a verification report exists (Markdown file in `docs/` or as the story's completion note) capturing: which devices were used, which behaviours were observed, screenshots / video for any non-obvious behaviour, any defects found and their resolutions
**And** the report explicitly cites each AC above with PASS / FAIL / N/A

---

### Story 6.2: Soak Tests — 30-min No-Crash + 8-Hour Scale Ceiling

As a Linn engineer / ohSpy maintainer,
I want two automated soak tests under `[Trait("category", "soak")]` — a 30-minute representative-debugging-session run that asserts zero crashes / zero UI hangs > 1 s / zero unclosable popups, and an 8-hour load run that asserts memory stays under 200 MB with 20 devices + 5 subscription popups + saturated SSDP log,
So that NFR-R1 and the Scale Ceiling Performance Budget are verified before release rather than discovered during the L&L or in real-world use.

**Acceptance Criteria:**

**Given** a soak-test harness under `tests/ohSpy.Core.Tests/` (or a separate `tests/ohSpy.Soak.Tests` project — engineering judgment; document the choice)
**When** I run `dotnet test --filter "category=soak"`
**Then** the soak suite is excluded from the default `dotnet test` run AND from the pre-commit chaos hook (D13 — soak is 8 hours; never in pre-commit)
**And** the soak suite is documented in `docs/DEVELOPMENT.md` (or README) as a pre-release gate

**Given** the 30-min no-crash soak (SC-R-30min — NFR-R1)
**When** the test runs
**Then** the test harness sets up a `FakeUpnpDevice` farm simulating a representative developer LAN: 15 devices announcing at typical rates, 3 devices intermittently slow / partial / mis-behaving (slow responders, mid-interaction byebye, partial NOTIFY, larger-than-typical SCPDs)
**And** the harness drives the app through a representative session script: open device tree, expand services, invoke a few actions (some succeed, some fault, some timeout), open 2 subscription popups, leave them running, open and close diagnostic viewer, switch adapter once, rescan twice
**And** the script runs continuously for 30 minutes
**And** the assertions are: 0 unhandled exceptions / app crashes; 0 UI-thread stalls > 1 s observed (via dispatcher-tick timing); 0 popups that cannot be closed; the diagnostic viewer remains responsive at session end

**Given** the 8-hour scale ceiling soak (Performance Budget "Scale ceiling")
**When** the test runs
**Then** the harness sets up 20 announcing devices and opens 5 subscription popups (one per service) that receive moderate event traffic
**And** the SSDP log is held at saturation (≥ 1 adv/s sustained background so the log stays near its 10,000-cap)
**And** the test runs for 8 hours
**And** the assertion is: process resident memory remains < 200 MB at all sampling points (sample every 10 minutes; record samples for the verification report)
**And** the bounded collections behave per spec: SSDP log capped at 10,000; subscription event lists capped at 5,000 each; diagnostic ring capped at 5,000; on-disk log rolls within ≤ 16 MB total (≤ 8 files × ≤ 2 MB) (SC-013 + Performance Budget caps)
**And** zero unhandled exceptions over 8 hours

**Given** the soak result reports
**When** a soak run completes
**Then** the run produces a Markdown report under `docs/soak-reports/<yyyy-MM-dd-HHmm>-<duration>.md` summarising: test environment (adapter, dev machine specs), device farm composition, memory samples, exception count (should be 0), any anomalies observed
**And** the report is committed to the repo as a release-gate artefact

**Given** the soak test is conceptually under-deterministic (real timing, real OS scheduling)
**When** flakes are observed
**Then** flakes are investigated — not retried-until-green. NFR-R1 and the Scale Ceiling are not statistical claims; a single failure indicates a real defect to fix.

**Given** the diagnostic emission during soak
**When** the soak run completes
**Then** the rolling on-disk log file (`%LOCALAPPDATA%\ohSpy\diagnostics\`) contains entries spanning the soak duration with size-based rollover applied (verifies AC-8.5 end-to-end over real wall-clock)

---

### Story 6.3: Performance Budget Verification + Clean-Machine Install Dry-Run

As a Linn engineer / ohSpy maintainer,
I want a single verification pass that walks every Performance Budget SC-* row and asserts it against the dev LAN, plus a clean-Windows-11 install dry-run that asserts the installer lands, the app launches, diagnostics are written to `%LOCALAPPDATA%\ohSpy\diagnostics\`, and the uninstaller behaves per spec,
So that the L&L can confidently claim "every budget in §6 of the PRD is met" and "drop the installer on a fresh machine, double-click setup.exe, run ohSpy."

**Acceptance Criteria:**

**Given** the dev LAN with 10–20 announcing UPnP devices
**When** I walk every SC-* budget
**Then** each is verified per the test baseline §6 of the PRD and the result captured in the verification report:
- **SC-001:** Launch → every responsive device visible ≤ ~7 s (stopwatch from process start to last device row populated)
- **SC-002:** 30-min session — exactly one tree entry per UUID; zero duplicates (verified by tree-snapshot comparison against the SSDP log)
- **SC-003:** `ssdp:byebye` → tree row removed typically < 2 s (manually trigger byebye via fake-device fixture; stopwatch)
- **SC-004:** Service node expansion → children visible ≤ 2 s typical (cold cache; against real device)
- **SC-005:** "View XML" → default browser opens ≤ 2 s
- **SC-009:** SSDP advertisement received → row visible ≤ 1 s
- **SC-010:** Double-click action → invocation popup interactive ≤ 1 s
- **SC-011:** Action invocation submitted → result visible ≤ 2 s (against device with < 1 s LAN latency)
- **SC-013:** 1-hour continuous operation — no memory exhaustion; bounded collections behave (subset of Story 6.2's 8-hour soak; smaller window verifies the same invariant in interactive mode)
- **Warm SCPD expand:** ≤ 100 ms when description eager-fetched (re-expand the same service node after first cold expand)
- **Cold large-SCPD expand:** ≤ 2 s for 100+-action SCPD with no UI freeze (verified against `FakeUpnpDevice` GiantScpd mode)
- **Sustained chatty-SSDP target:** ≥ 20 adv/s for ≥ 30 s — no visible dropped frames; main-thread stalls < 16 ms (verified against fake-device burst fixture)

**Given** a fresh Windows 11 machine with NO .NET 10, NO WindowsAppRuntime, NO Visual Studio
**When** I copy `ohSpy-setup-<version>-x64.exe` to that machine and run it
**Then** the SmartScreen "Windows protected your PC" dialog appears; clicking "More info" → "Run anyway" proceeds
**And** the installer runs to completion without an Administrator prompt (AC-12.3)
**And** the install lands in `%LOCALAPPDATA%\Programs\ohSpy\` (AC-12.3)
**And** a Start Menu shortcut at `Programs\ohSpy\ohSpy.lnk` exists
**And** the desktop-shortcut checkbox is unchecked by default (AC-12.5 derived)

**Given** the install completes
**When** I launch ohSpy from the Start Menu
**Then** the app opens and the main window renders (AC-12.4 — `Bootstrap.TryInitialize` succeeds; the bundled WindowsAppRuntime + .NET 10 are picked up)
**And** the SSDP discovery proceeds (assuming an eligible adapter exists on the test machine)
**And** within ~7 s the device tree populates (SC-001 verified end-to-end on a clean machine)
**And** the diagnostic file sink creates `%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-<yyyyMMdd>.log` and writes the first session's entries (AC-8.5 verified end-to-end)

**Given** I uninstall via Apps & Features
**When** the uninstaller runs
**Then** the install dir (`%LOCALAPPDATA%\Programs\ohSpy\`) and the Start Menu shortcut are removed (AC-12.5)
**And** `%LOCALAPPDATA%\ohSpy\diagnostics\` is PRESERVED (operator value — D12 + AC-12.5)

**Given** I rerun the installer on the same machine while a prior install exists
**When** the installer detects the prior install via the `AppId` GUID (D12 upgrade behaviour)
**Then** it replaces the prior install silently (no "please uninstall first" prompt)
**And** the install completes cleanly

**Given** the chaos-hook regression discipline (AC-13.x) over the entire build period
**When** I review the commit history
**Then** every merged commit was pre-commit-hooked (the chaos suite ran and passed); no `--no-verify` bypasses appear without justified-in-message rationale (D13 carries this as a discipline; verified by spot-check)

**Given** the final release-readiness checkpoint
**When** Stories 6.1 + 6.2 + 6.3 are all green
**Then** the verification reports are committed to `docs/` (or equivalent)
**And** the latest installer artefact is tagged with a build timestamp (`yyyy.MM.dd.HHmm` per D12 versioning) and identified as the L&L-ready build
**And** the L&L narrative arc (brief → PRD → architecture → epics + stories → working app) is walkable end-to-end against the committed artefacts (SM-5 + SM-6 from PRD §9 verified)

---

