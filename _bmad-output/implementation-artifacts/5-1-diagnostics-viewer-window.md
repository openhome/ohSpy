---
baseline_commit: 56687598bff0ae65951cd8f50a5e39a6301b6b7a
---

# Story 5.1: Diagnostics Viewer Window

Status: done
<!-- 2026-06-05: Code review (Sonnet) CHANGES-REQUESTED → 2 P1 patches applied (severity-colour {Binding} +
     view-filter DataContext, both fixed via per-row DataContext set in OnRowPrepared since x:Bind+StaticResource
     converter can't compile under a Window root). AC-5.1.14 manual UI smoke PASSED on the live Linn network:
     colours/identities/live-update/z-order good; Q1 gate↔view coupling verified (Verbose firehose toggles on/off).
     The smoke also surfaced a separate Epic-2 SSDP defect (M-SEARCH requests mis-logged as Warning "parse failed"
     with no reason) — fixed separately (SsdpParser → Verbose Ssdp.SearchObserved + ErrorText reasons). review → done. -->;

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want `View → Diagnostics` to open a live, virtualised diagnostic viewer showing every entry the emitter has recorded — with timestamp, severity, category, message, Identity column, and Endpoint column,
so that I can investigate failures (SSDP parse errors, description fetch failures, SOAP faults, subscription lapses, etc.) without restarting the tool or grepping the on-disk log file.

## ⭐ CRITICAL RECONCILIATIONS — READ FIRST (epic prose is STALE in three places)

The Epic 5 prose (epics.md §Story 5.1, lines 1769-1828) was written BEFORE Epic 4 shipped. Three of its statements are now wrong against shipped code. **Follow this story, not the epic prose, where they conflict.**

1. **The `View` menu ALREADY EXISTS — do NOT create it.** Epic AC ("**Then** there is a `View` menu containing Diagnostics, Network adapter, and Rescan… added in this story as a complete menu shape") is STALE. **Story 5.2 (done, committed) built the View menu** because `MainWindow.xaml` had no menu bar. The shipped shape (verified): `src/ohSpy.App/MainWindow.xaml` lines 36-42 — a title-bar `Button Content="View"` (Grid.Row=1) whose `Button.Flyout` holds a `<MenuFlyout Placement="BottomEdgeAlignedLeft" Opening="OnViewMenuOpening">` containing one `<MenuFlyoutSubItem x:Name="NetworkAdapterMenu" Text="Network adapter" />`. **Story 5.1 ADDS a `Diagnostics` `MenuFlyoutItem` to THIS existing flyout** (alongside the Network-adapter subitem), wired to `ShellViewModel.OpenDiagnosticsCommand`. Story 5.3 (Rescan) later hangs its item off the SAME flyout. Do NOT add a second menu/Button.

2. **Do NOT have the VM `new` a `DiagnosticsWindow`.** Epic AC ("the command constructs `new DiagnosticsWindow(diagnosticsVm)`, calls Activate()…") describes the wrong layer. **Pattern 2 / `CoreAppBoundaryTests` forbids Core → App**, and `ShellViewModel` lives in `ohSpy.Core` — it cannot reference a WinUI `Window`. The shipped precedent for EVERY popup (Properties 2.9, Invocation 3.2, Subscription 4.3) is a **Core launcher seam interface** (`I*Launcher`) implemented App-side. Story 5.1 introduces **`IDiagnosticsLauncher` (Core)** + **`DiagnosticsLauncher` (App)**; `OpenDiagnosticsCommand` calls `_diagnosticsLauncher.Open()`. The App launcher does the `new DiagnosticsWindow(vm)` + `window.Activate()` + `_ownership.Adopt(window, ShellWindow)` (the exact 4.3 `SubscriptionPopupLauncher` shape — see References).

3. **Epic 5 is now just 5.1 + 5.3.** 5.2 (adapter switch) was re-sequenced into Epic 4 and is **done**. Do not treat 5.2 as upcoming or reference its handlers as un-wired.

## Acceptance Criteria

Reconciled against shipped code. AC numbers map to the epic's `Given/When/Then` blocks plus the standing project gates.

1. **AC-5.1.1 — DiagnosticsWindow (App).** `src/ohSpy.App/Views/DiagnosticsWindow.xaml` + `.xaml.cs` exist. Layout is a single **item-virtualised** list of diagnostic rows (newest-first) with columns: **Timestamp** (UTC, `HH:mm:ss.fff`), **Severity**, **Category**, **Message**, **Identity**, **Endpoint** (FR-041). Code-behind is **constructor-only** (Pattern 13) — its only logic is the `Window.Closed` handler (dispose/unsubscribe the VM) plus any App-side `Visibility`/brush projections the VM cannot carry (Pattern 2 forbids `Visibility`/`Brush` in Core).

2. **AC-5.1.2 — Severity colour.** Each row carries a severity-colour foreground/accent brush via an **App-side `SeverityToBrushConverter`** (`IValueConverter`, in `src/ohSpy.App/Converters/`): `Warning` amber, `Error` red, `Information` neutral, `Verbose` muted. (NEW converter — there is no existing one; precedent is `NodeDataTemplateSelector`/`ArgumentInputTemplateSelector` in that folder.)

3. **AC-5.1.3 — DiagnosticsViewModel (Core).** `src/ohSpy.Core/ViewModels/DiagnosticsViewModel.cs` exposes `BoundedObservableCollection<DiagnosticRow> Entries` bound to the **SAME instance** the `DiagnosticRingSink` populates (`IDiagnosticRingSink.Entries`) — **AC-8.2: no copy, no view layer**. The VM takes `IDiagnosticRingSink` by ctor and exposes `Entries => _ringSink.Entries`.

4. **AC-5.1.4 — MinSeverity control (Q1 RESOLVED — couples emitter gate + view filter).** The VM exposes `[ObservableProperty] DiagSeverity MinSeverity` defaulting to `DiagSeverity.Information` (D8 — runtime-flippable, NOT persisted). Changing it (a) **drives the runtime emitter gate** so flipping to `Verbose` turns the firehose ON and raising it turns it OFF (via the new `IDiagnosticLevelGate` seam — see Dev Notes §"MinSeverity: two meanings"), AND (b) **filters the view** (hides already-captured rows below the threshold; MUST NOT mutate or copy the bounded ring — AC-8.2). The VM exposes a severity-chip selector affordance (at minimum). Category filter chips are an explicit open follow-up (D8) — out of scope.

5. **AC-5.1.5 — View menu wiring.** `MainWindow.xaml`'s EXISTING View `MenuFlyout` gains a `Diagnostics` `MenuFlyoutItem` (alongside the `Network adapter` subitem). Choosing it invokes `ShellViewModel.OpenDiagnosticsCommand` (a `[RelayCommand]`). The command calls `IDiagnosticsLauncher.Open()` (NOT `new DiagnosticsWindow` — see Reconciliation #2). The App `DiagnosticsLauncher.Open()` constructs `new DiagnosticsWindow(vm)`, calls `window.Activate()`, THEN `_ownership.Adopt(window, ShellWindow)` (D10/FR-046, the canonical 4.3 sequence — AC-10.5).

6. **AC-5.1.6 — Live update + first-open backfill.** With the viewer open, new entries appear at the **top** within the next dispatcher tick (D8 — `DiagnosticRingSink.Push` dispatches the prepend via `IUiDispatcher.Post`). The viewer stays responsive at high arrival rates (FR-041). Opening the viewer mid-session shows **all** entries accumulated since app start (up to the 5,000 ring cap) — because `Entries` IS the live ring, already-populated.

7. **AC-5.1.7 — Identity column (snapshot-at-arrival).** Already resolved on the `DiagnosticRow` by the sink (`DiagnosticRow.IdentityLabel`). The viewer **binds the pre-resolved label** — it does NOT re-resolve. Verify the shipped semantics hold (they are tested in `DiagnosticRingSinkTests`): friendly name when registry hit with a name (AC-8.3); else the UDN string (already carries `uuid:` — Amendment A30, FR-041 2nd rule); `"—"` when `DeviceUuid` is null/empty (3rd rule); and the label does NOT change on later registry mutation (snapshot invariant). **No new Core logic — the viewer must NOT duplicate resolution.**

8. **AC-5.1.8 — Endpoint column.** Bind the pre-resolved `DiagnosticRow.EndpointLabel`: `host` (default port) or `host:port` (non-default) from `Url` (AC-8.4); else `RemoteEndpoint` directly; else `"—"`. Pre-resolved by the sink — bind, don't recompute.

9. **AC-5.1.9 — File-sink-unavailable visible.** When the diagnostic file sink fails to initialise at startup (FR-042/NFR-R4), the app still launches and the single `Warning` `DiagCategories.DiagnosticsFileSinkUnavailable` entry is visible in the viewer (the ring keeps working; the entry was pushed through it via the App.OnLaunched `SetRingSink` wiring). Covered by integration test — no new production code.

10. **AC-5.1.10 — Filtering + gating behaviour (Q1 RESOLVED).** Setting `MinSeverity = Warning` (a) raises the emitter gate so NEW `Verbose`/`Information` entries are no longer created (AC-8.7 zero-alloc — they never enter the ring), AND (b) hides any already-captured `Verbose`/`Information` rows from the view (the ring is NOT mutated or copied — AC-8.2). Setting `MinSeverity = Verbose` lowers the gate so Verbose entries start entering the ring and become visible. Neither the gate nor the view-filter state persists across restart (PRD §7 Non-Goal).

11. **AC-5.1.11 — Integration test (newest-first + resolution + virtualisation).** Emitting 100 diagnostics at various severities through `IDiagnosticEmitter` yields them in `Entries` newest-first; identity/endpoint resolution matches AC-8.3/AC-8.4 across all; the bound control is item-virtualised (NFR-P1).

12. **AC-5.1.12 — Marshalling guard (retro Action H — MANDATORY).** At least one test using `DeferredUiDispatcher` (NOT `InlineUiDispatcher`) proves the ring-sink prepend is applied through `IUiDispatcher.Post`: after `Push`, `Entries` is unchanged until `Drain()` is called; after `Drain()`, the row appears. (The async/threaded path mutates bound state — project standing rule, memory `winui-no-synccontext-marshal-vm`.)

13. **AC-5.1.13 — Build + suite gates.** Core builds `-warnaserror` 0/0. App builds with only the pre-existing benign `WMC1506` (no NEW warnings). Full suite green (current baseline ~517 passed / 2 skipped). `DiagCategoriesUsageTests`, `CoreAppBoundaryTests`, `AsyncDisciplineTests` all green. **No new `DiagCategories` constant is required** (see Dev Notes) — if you believe one is, STOP and flag it (it changes the pinned set).

14. **AC-5.1.14 — Manual UI smoke (FIRST-CLASS GATE — Action L).** Per `smoke-per-ui-story`: build + run the real app, open `View → Diagnostics`, and exercise it against a **LARGE real diagnostic stream** (ring near/at the 5,000 cap, many real entries from a live Linn/OpenHome network — e.g. trigger description fetches, an adapter switch, subscriptions). Confirm: live prepend at the top; responsiveness with the ring full (virtualisation holds — scrolling stays smooth, memory bounded); Identity/Endpoint columns render real names/endpoints AND `—` placeholders; severity colours correct; the severity filter hides/shows rows live; z-order/ownership per A31 (popup floats free, shell can come forward). Do NOT smoke only a trivial case.

## Tasks / Subtasks

- [x] **Task 1 — `DiagnosticsViewModel` (Core)** (AC: 3, 4, 6, 7, 8)
  - [x] Create `src/ohSpy.Core/ViewModels/DiagnosticsViewModel.cs` as `public sealed partial class : ObservableObject`.
  - [x] Ctor takes `IDiagnosticRingSink ringSink` (store it). Expose `public BoundedObservableCollection<DiagnosticRow> Entries => _ringSink.Entries;` (SAME instance — AC-8.2; no copy).
  - [x] `[ObservableProperty] private DiagSeverity _minSeverity;` seeded from the gate (D8 default). Ctor also takes `IDiagnosticLevelGate gate`; in `partial void OnMinSeverityChanged(value)` write `gate.MinSeverity = value` (couples the runtime emitter gate — Q1). Seed `_minSeverity` from `gate.MinSeverity` in the ctor so the VM reflects the configured default.
  - [x] Expose the severity-chip selector affordance shape the App binds (`SelectableSeverities` list + the bound `MinSeverity`). Core-pure (no `Visibility`/`Brush`).
  - [x] Do NOT add identity/endpoint resolution here — it lives on `DiagnosticRow` already (snapshot-at-arrival, done by the sink).

- [x] **Task 1b — Runtime emitter gate seam (Core)** (AC: 4, 10 — Q1 RESOLVED, see Dev Notes §"MinSeverity: two meanings")
  - [x] Create `IDiagnosticLevelGate` + impl `DiagnosticLevelGate` in `src/ohSpy.Core/Diagnostics/`: a singleton holding the current `DiagSeverity` via `Volatile.Read/Write` over an `int` backing field (enum cast). Cheap read — on the emit hot path.
  - [x] Rewire `DiagnosticEmitter` to gate on `_gate.MinSeverity` (Volatile read) INSTEAD of `IOptions<DiagnosticOptions>.Value.MinSeverity`. AC-8.7 zero-alloc fast-path preserved (return before any `DiagnosticEntry` allocation when `severity < gate`).
  - [x] Seed the gate's initial value FROM `DiagnosticOptions.MinSeverity` at construction so the configured startup default is preserved.
  - [x] DI: register `IDiagnosticLevelGate` as a singleton (App `ServiceRegistration`, before the emitter), injected into both `DiagnosticEmitter` and `DiagnosticsViewModel`.

- [x] **Task 2 — `IDiagnosticsLauncher` seam (Core) + `OpenDiagnosticsCommand`** (AC: 5)
  - [x] Create `src/ohSpy.Core/ViewModels/IDiagnosticsLauncher.cs` (mirrors `ISubscriptionPopupLauncher` XML-doc rationale). Method: `void Open();` (no args — single app-lifetime viewer).
  - [x] In `ShellViewModel`: inject `IDiagnosticsLauncher` (ctor param + field) and add `[RelayCommand] private void OpenDiagnostics() => _diagnosticsLauncher.Open();` (`using CommunityToolkit.Mvvm.Input;` added).
  - [x] DECISION: **single** app-lifetime viewer — the launcher tracks the open window and re-activates it if already open, else creates it (matches "the Diagnostics viewer" singular in FR-041/FR-046). Q3 default taken.

- [x] **Task 3 — `DiagnosticsWindow` (App XAML + code-behind)** (AC: 1, 2, 7, 8, 10)
  - [x] Create `src/ohSpy.App/Views/DiagnosticsWindow.xaml` + `.xaml.cs` (namespace `ohSpy.App.Views`). Constructor `DiagnosticsWindow(DiagnosticsViewModel vm)`; typed `public DiagnosticsViewModel ViewModel { get; }` for compile-time `x:Bind`.
  - [x] `ItemsRepeater` inside a `ScrollViewer` (NFR-P1 item-virtualised) bound `ItemsSource="{x:Bind ViewModel.Entries, Mode=OneWay}"`. `DataTemplate x:DataType="diag:DiagnosticRow"`.
  - [x] Columns via a `Grid` per row. Timestamp/severity bound via Core projections (`TimestampDisplay`/`SeverityLabel`); Category/Message via `Entry.Category`/`Entry.Message` (record reference path); `IdentityLabel`/`EndpointLabel` bound directly.
  - [x] **WinUI struct-binding trap honored:** no binding touches a `DiagnosticContext` member. Severity colour binds `Entry.Severity` (an enum member of the `Entry` record reference) — safe.
  - [x] Timestamp formatting `HH:mm:ss.fff` from `Entry.TimestampUtc` (UTC): `DiagnosticRow.TimestampDisplay` Core projection (`InvariantCulture`).
  - [x] Severity colour: `SeverityToBrushConverter` (Task 4) applied to `Entry.Severity` as a `{StaticResource}` in the row template.
  - [x] MinSeverity view-filter (DISPLAY half of Q1): per-row `Visibility` (mechanism #1) set in code-behind on `ElementPrepared` + re-applied on `MinSeverity` change — `Entries` stays the same ring instance (AC-8.2).
  - [x] Code-behind constructor-only (Pattern 13): `InitializeComponent()` + set `ViewModel` + `Title = "Diagnostics"` + `Closed += OnClosed` (sync void — VSTHRD100) unsubscribes the VM `PropertyChanged` hook.

- [x] **Task 4 — `SeverityToBrushConverter` (App)** (AC: 2)
  - [x] Create `src/ohSpy.App/Converters/SeverityToBrushConverter.cs` implementing `IValueConverter`. `DiagSeverity` → `Brush`: Warning→amber, Error→red, Information→neutral (theme `TextFillColorPrimaryBrush`), Verbose→muted (`MutedForegroundBrush`). Fixed palette for amber/red (v1).
  - [x] Registered as a `{StaticResource}` in the window's `Grid.Resources` so the row `DataTemplate` references it.

- [x] **Task 5 — DI + App wiring** (AC: 5, 9)
  - [x] In `ServiceRegistration.cs` (BEFORE the `ShellViewModel` line): register `DiagnosticsViewModel` (singleton), `DiagnosticsLauncher` + `IDiagnosticsLauncher` (dual reg). Gate registered before the emitter.
  - [x] `src/ohSpy.App/Windowing/DiagnosticsLauncher.cs`: implements `IDiagnosticsLauncher` (mirror `SubscriptionPopupLauncher`): `ShellWindow` settable; `Open()` → `new DiagnosticsWindow(vm)` → `Activate()` → `Adopt(window, ShellWindow)`; single-viewer re-activate (tracks the live window; on re-Open `Activate()` the existing one; clears the handle on `Closed`).
  - [x] `App.xaml.cs` `OnLaunched`: added `Services.GetRequiredService<DiagnosticsLauncher>().ShellWindow = _window;` alongside the other launcher injections.
  - [x] `MainWindow.xaml`: added `<MenuFlyoutItem Text="Diagnostics" Command="{x:Bind ViewModel.OpenDiagnosticsCommand}" />` + a `<MenuFlyoutSeparator />` to the EXISTING `MenuFlyout` (Diagnostics first, separator, then Network adapter). `OnViewMenuOpening` untouched.

- [x] **Task 6 — Tests (Core)** (AC: 3, 6, 7, 8, 11, 12)
  - [x] `DiagnosticsViewModelTests.cs`: `Entries` same instance (ReferenceEquals); `MinSeverity` default `Information`; observable (raises `PropertyChanged`); setter writes through to the gate (Q1); ctor seeds `MinSeverity` from the gate.
  - [x] Gate tests: `DiagnosticLevelGateTests` (default seeded from `DiagnosticOptions.MinSeverity`; runtime-mutable); emitter respects a runtime gate change up (raise → stop) AND down (lower → start); AC-8.7 zero-alloc preserved under the runtime gate.
  - [x] Integration (AC-5.1.11): 100 entries through the real `DiagnosticRingSink` (InlineUiDispatcher) at various severities → newest-first in the VM's `Entries` + Identity (AC-8.3) / Endpoint (AC-8.4) labels.
  - [x] **AC-5.1.12 marshalling guard:** `DiagnosticRingSink` with a `DeferredUiDispatcher`; `Push` → `Entries.Count == 0` (not applied); `Drain()` → row present at index 0 + `PostCount == 1`.
  - [x] `TimestampDisplay` formatting test (UTC `HH:mm:ss.fff`, invariant culture) + `SeverityLabel`.
  - [x] `OpenDiagnosticsCommand` invokes the launcher (ShellViewModelTests, FakeDiagnosticsLauncher).

- [x] **Task 7 — Build, suite, smoke** (AC: 13, 14)
  - [x] Core `dotnet build -warnaserror` 0/0; App build (only pre-existing `WMC1506`, shifted :156→:159 by the menu insert — no NEW warning). Full suite green (537 passed / 2 skipped); `DiagCategoriesUsageTests` + `CoreAppBoundaryTests` + `AsyncDisciplineTests` green.
  - [ ] **AC-5.1.14 manual UI smoke against a LARGE real stream — PENDING (first-class gate, Project Lead performs on real Linn/OpenHome hardware).** Story stays at `review` until smoke passes (prior dev-story → code-review → smoke → done pattern).

## Dev Notes

### MinSeverity: two meanings — keep them separate (epic vs architecture tension)
- **Architecture D8** (architecture.md:933,946) says the operator can flip `MinSeverity` at runtime via the viewer, and the EMITTER gates on `DiagnosticOptions.MinSeverity` (entries below it are never created — AC-8.7 zero-alloc). That is the EMITTER level.
- **Epic AC for 5.1** (epics.md:1819-1822) is explicit that the viewer's `MinSeverity` is a **VIEW FILTER**: "the underlying ring buffer is NOT mutated — filtering is a view concern", and the filter does not persist.
- **Resolution for THIS story (Q1 — Project Lead, 2026-06-04): COUPLE them.** `DiagnosticsViewModel.MinSeverity` is the single operator control for BOTH: (a) the **emitter gate** — flipping to `Verbose` turns the firehose ON (lower-severity entries start entering the ring), raising it turns it OFF; AND (b) the **view filter** — already-captured rows below the threshold are hidden so the display matches the chosen level. Default `Information`; runtime-only, not persisted.
- **⚠️ IMPLEMENTATION GAP TO CLOSE (load-bearing):** the shipped `DiagnosticEmitter` gates on `IOptions<DiagnosticOptions>.Value.MinSeverity`, and `DiagnosticOptions.MinSeverity` is `{ get; init; }` — **NOT runtime-mutable**. Introduce a runtime-mutable, thread-safe Core gate seam (suggested: `IDiagnosticLevelGate` singleton holding the current level via `Volatile.Read/Write` over an `int` backing field — enum isn't directly `volatile`; keep the read cheap to preserve the AC-8.7 zero-alloc fast-path). `DiagnosticEmitter` reads the gate INSTEAD of the init-only option; **seed the gate's initial value FROM `DiagnosticOptions.MinSeverity`** so the configured default is preserved. `DiagnosticsViewModel` takes the gate by ctor; its `MinSeverity` setter writes the gate AND drives the view filter. The gate read is on the emitter hot path (every emit, many threads) — `Volatile.Read` of an `int` is sufficient (no torn reads); do not lock. Add Core tests: gate default seeded from options; emitter respects a runtime gate change (below-gate severity not emitted after raise, emitted after lower); AC-8.7 zero-alloc preserved; VM setter writes through to the gate.

### MinSeverity view-filter — how (must keep `Entries` the SAME instance, AC-8.2)
You CANNOT introduce a second/filtered `ObservableCollection` in Core (that is the "view layer" AC-8.2 forbids, and breaks the `EntriesProperty_IsSameInstanceAcrossPushes` guard intent). Two viable mechanisms, App-side:
1. **Per-row `Visibility`** in the row `DataTemplate`: bind row visibility through a converter that takes `Entry.Severity` + the VM's `MinSeverity` (a `MultiBinding`-equivalent; WinUI lacks classic MultiBinding, so use a converter with `ConverterParameter` or a small per-row helper). Collapsed rows take zero height. Simple; virtualisation still realises them but they collapse. Acceptable for v1.
2. **A `CollectionViewSource`/`ICollectionView` filter** over `Entries` — but WinUI 3's `ItemsRepeater` does not consume `ICollectionView` filtering the way `ListView` does, and re-filtering on `MinSeverity` change needs a refresh. More complex; avoid unless #1 proves inadequate.
Prefer mechanism #1. Whichever you pick, `DiagnosticsViewModel.Entries` MUST remain `_ringSink.Entries` (same instance).

### Shipped diagnostics layer — VERIFIED present, do NOT rebuild (AC assumptions hold)
All of these exist and are correct as the epic assumes — bind/consume them, do not reimplement:
- `BoundedObservableCollection<T>` (`src/ohSpy.Core/Collections/`): newest-first ring, `PrependNewest` emits `Add(0)` [+`Remove(Capacity)` at cap], `Clear` is the only `Reset`. UI-thread-owned; not thread-safe — callers marshal. `IReadOnlyList<T> + INotifyCollectionChanged`. Capacity 5000.
- `DiagnosticRingSink` (`src/ohSpy.Core/Diagnostics/`, `internal`): holds the `Entries` instance; `Push` resolves Identity+Endpoint on the CALLING thread (snapshot — FR-041), builds the immutable `DiagnosticRow`, then `_dispatcher.Post(() => Entries.PrependNewest(row))`. **This `Post` is the Action-H async path your AC-5.1.12 test guards.**
- `DiagnosticRow` (record, reference type): `(DiagnosticEntry Entry, string IdentityLabel, string EndpointLabel)`. Safe for `x:Bind`. (If you add `TimestampDisplay`/severity-display projections, add them HERE.)
- `DiagnosticEntry` (record): `(DateTime TimestampUtc, DiagSeverity Severity, string Category, string Message, DiagnosticContext Context)`.
- `DiagnosticContext` (**`readonly record struct`** — STRUCT-BINDING TRAP; never bind its members directly): `DeviceUuid` is now `string?` (the UDN string, Amendment A30), `Url`, `RemoteEndpoint`, etc.
- `DiagSeverity` enum: `Verbose, Information, Warning, Error`.
- `IDiagnosticRingSink` (public interface): `void Push(DiagnosticEntry)`, `BoundedObservableCollection<DiagnosticRow> Entries { get; }`. Registered singleton (ServiceRegistration:49).
- `RegistryIdentityLookup` / `IDiagnosticIdentityLookup`: resolves UDN→friendly name (registry-backed). Already wired INTO the sink — viewer does NOT touch it.
- `DiagCategories`: pinned by `DiagCategoriesUsageTests` (structural: non-empty, dot-separated, unique). **Story 5.1 needs NO new category constant** — the only diagnostics it surfaces already exist (`DiagnosticsFileSinkUnavailable` for AC-5.1.9; all others arrive from existing emit sites). If you find yourself adding one, STOP — it changes the pinned set and means the design drifted.

### Window-launch + ownership pattern (the SHIPPED precedent to copy verbatim)
- Core seam: `ISubscriptionPopupLauncher` (`src/ohSpy.Core/ViewModels/`) — copy its XML-doc rationale for `IDiagnosticsLauncher`.
- App impl: `SubscriptionPopupLauncher` (`src/ohSpy.App/Windowing/`): `public Window? ShellWindow { get; set; }`; `Open(...)` → build VM (factory) → `new XxxWindow(vm)` → `window.Activate()` → `if (ShellWindow is not null) _ownership.Adopt(window, ShellWindow)`. Diagnostics has no per-open args and a single VM, so simpler (no factory needed; inject the singleton VM).
- `IWindowOwnershipManager.Adopt(child, parent)` (`src/ohSpy.App/Windowing/WindowOwnershipManager.cs`): MUST be called AFTER `Activate()`. Per **Amendment A31** (commit 5668759), popups float in FREE z-order (no Win32 owner link) — only AC-10.2 (close-with-parent) + AC-10.5 (Activate-then-Adopt) stand. Do NOT add a Win32 owner link.
- DI dual-registration precedent (ServiceRegistration:142-143, 159-160, 175-176): register concrete + interface forwarding so `App.OnLaunched` can set `ShellWindow` on the concrete.
- `App.OnLaunched` injects `ShellWindow` post-construction (lines 93-97) because `MainWindow` is created there, not in DI. Add the Diagnostics line alongside.

### Window code-behind / XAML conventions (Pattern 13)
- Constructor-only code-behind. Set `ViewModel` BEFORE/around `InitializeComponent()` (PropertiesWindow sets it before; SubscriptionPopupWindow sets it before `InitializeComponent()` too — follow that order so `x:Bind` has the VM). Typed `public XxxViewModel ViewModel { get; }` for compile-time `x:Bind`.
- `Closed += OnClosed` MUST be a **synchronous `void`** handler (VSTHRD100; async void is App-tree-fatal). Diagnostics VM likely holds no disposable subscriptions (it just exposes the ring) — if so, `OnClosed` may be minimal or unnecessary, but if you wire any `PropertyChanged`/`CollectionChanged` in code-behind, unsubscribe there.
- Virtualisation: use `ItemsRepeater` in a `ScrollViewer` (SSDP log + subscription event list precedent — MainWindow.xaml:187, SubscriptionPopupWindow.xaml:120). This is item-virtualised (NFR-P1).
- `MicaBackdrop` + standard window chrome to match the other windows.

### WinUI render traps to AVOID (all three project memories apply to this NEW window)
1. **No struct data-binding** (`winui-no-struct-databinding`, fix 63e2378): never `x:Bind`/`{Binding}` a `DiagnosticContext` member or any value-type/`KeyValuePair`. Bind only the `DiagnosticRow` record's reference-typed/string/enum members. This is why Identity/Endpoint are pre-resolved to `string` on the row.
2. **TreeView DataContext null** (`winui-treeview-datacontext-null`): N/A here (no TreeView) — but the general lesson (declarative container binding can silently no-op) is why you must SMOKE this window, not trust the build.
3. **No SynchronizationContext — marshal via IUiDispatcher** (`winui-no-synccontext-marshal-vm`): the sink already marshals its prepend via `Post`; your AC-5.1.12 test proves it with `DeferredUiDispatcher`. If `DiagnosticsViewModel` ever mutates observable state from an `await` continuation, marshal via `IUiDispatcher.Post` — but in this story the VM is passive (binds the ring), so the risk is the sink's path, already covered.

### Source tree — files this story touches
NEW:
- `src/ohSpy.Core/ViewModels/DiagnosticsViewModel.cs`
- `src/ohSpy.Core/ViewModels/IDiagnosticsLauncher.cs`
- `src/ohSpy.App/Views/DiagnosticsWindow.xaml` + `.xaml.cs`
- `src/ohSpy.App/Windowing/DiagnosticsLauncher.cs`
- `src/ohSpy.App/Converters/SeverityToBrushConverter.cs`
- `tests/ohSpy.Core.Tests/ViewModels/DiagnosticsViewModelTests.cs`

UPDATE (read current state before editing — listed with what to preserve):
- `src/ohSpy.App/MainWindow.xaml` — ADD one `MenuFlyoutItem` to the EXISTING View `MenuFlyout` (lines 36-42). PRESERVE the `Opening="OnViewMenuOpening"` hook, the `NetworkAdapterMenu` subitem, the two-pane shell, the TreeView/ItemsRepeater. Do NOT add a new menu/Button.
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — ADD `IDiagnosticsLauncher` ctor param + field + `[RelayCommand] OpenDiagnostics`. PRESERVE the entire adapter-scope/switch machinery (StartAsync/SwitchAdapterAsync/DisposeAsync) — do NOT touch it. Adding a ctor param means updating the DI registration (it is `AddSingleton<ShellViewModel>()` — DI auto-resolves the new param once `IDiagnosticsLauncher` is registered BEFORE it).
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — ADD the Diagnostics VM + launcher registrations BEFORE the `ShellViewModel` line (so `IDiagnosticsLauncher` is resolvable when `ShellViewModel` is built). Mirror the 4.3 block.
- `src/ohSpy.App/App.xaml.cs` — ADD `DiagnosticsLauncher.ShellWindow = _window;` in `OnLaunched` alongside the other launcher injections (lines 93-97).
- (Maybe) `src/ohSpy.Core/Diagnostics/DiagnosticRow.cs` — ADD `TimestampDisplay` (UTC `HH:mm:ss.fff`) and/or a severity-display string projection, IF you choose to keep XAML dumb. Keep them pure (no `Brush`/`Visibility`).

### Testing standards
- xUnit + FluentAssertions (`Should()`), `[Trait("ac", "AC-...")]` / `[Trait("fr", "FR-041")]` traits (see `DiagnosticRingSinkTests`).
- `InlineUiDispatcher` runs `Post` immediately (use for ordering/resolution tests); `DeferredUiDispatcher` queues until `Drain()` (use for the MANDATORY marshalling guard, AC-5.1.12). Both in `tests/ohSpy.Core.Tests/Fakes/`.
- The App project is not unit-tested headlessly (WinUI) — `DiagnosticsWindow`/`SeverityToBrushConverter`/launcher are covered by the manual smoke (AC-5.1.14) + Core VM unit tests as the compensating control. This is the established project pattern (4.3 precedent).
- Baseline before this story: ~517 passed / 2 skipped.

### Project Structure Notes
- `MainWindow.xaml` is at `src/ohSpy.App/MainWindow.xaml` (NOT under `Views/`); the popup windows ARE under `src/ohSpy.App/Views/`. Put `DiagnosticsWindow` under `Views/` with the other popups.
- Converters live in `src/ohSpy.App/Converters/`. Launchers live in `src/ohSpy.App/Windowing/`. Core launcher interfaces live in `src/ohSpy.Core/ViewModels/` (next to `ISubscriptionPopupLauncher`).
- `CoreAppBoundaryTests` enforces no Core→App reference — this is WHY the launcher seam exists. Adding `new DiagnosticsWindow` anywhere in Core will fail that test (and won't compile).

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 5.1] — epic AC (reconciled above; lines 1769-1828; STALE on View-menu creation + `new DiagnosticsWindow`).
- [Source: _bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md#FR-041] — viewer behaviour, Identity/Endpoint resolution, snapshot-at-arrival (lines 577-587).
- [Source: …/prd.md#FR-042] — diagnostic logging discipline / NFR-R4 (lines 589-594, 632).
- [Source: …/prd.md#FR-046] — main-window-owned popups (lines 608-616), incl. Diagnostics viewer.
- [Source: …/architecture.md#Decision 8] — diagnostic pipeline, `MinSeverity` semantics (lines 881-946). NOTE: D8's `DiagnosticContext.DeviceUuid` was `Guid?` — superseded by **Amendment A30** (now `string?` UDN).
- [Source: …/architecture.md#Decision 10] + **Amendment A31** (lines 1266, 2995-3014) — window ownership; popups float free; Activate-then-Adopt (AC-10.5) + close-with-parent (AC-10.2) stand.
- [Code: src/ohSpy.App/MainWindow.xaml:36-42 + MainWindow.xaml.cs:204-236] — the EXISTING View menu (Story 5.2) to extend.
- [Code: src/ohSpy.Core/Diagnostics/{DiagnosticRingSink,DiagnosticRow,DiagnosticEntry,DiagnosticContext,DiagSeverity,IDiagnosticRingSink,RegistryIdentityLookup}.cs] — the shipped diagnostics layer to consume.
- [Code: src/ohSpy.Core/Collections/BoundedObservableCollection.cs] — the bound ring.
- [Code: src/ohSpy.App/Windowing/SubscriptionPopupLauncher.cs + src/ohSpy.Core/ViewModels/ISubscriptionPopupLauncher.cs] — the launcher pattern to copy.
- [Code: src/ohSpy.App/Windowing/WindowOwnershipManager.cs] — Adopt contract + A31 free-z-order note.
- [Code: src/ohSpy.App/Composition/ServiceRegistration.cs:131-186] — DI launcher-block precedent + ShellViewModel registration.
- [Code: src/ohSpy.App/App.xaml.cs:90-99] — ShellWindow post-construction injection.
- [Code: src/ohSpy.App/Views/SubscriptionPopupWindow.xaml(.cs)] — ItemsRepeater virtualisation + constructor-only code-behind + typed projections precedent (also the 63e2378 struct-binding fix example).
- [Code: tests/ohSpy.Core.Tests/Diagnostics/DiagnosticRingSinkTests.cs] — existing AC-8.2/8.3/8.4 + snapshot coverage to extend, and the identity-lookup test doubles.
- [Code: tests/ohSpy.Core.Tests/Fakes/DeferredUiDispatcher.cs] — the marshalling-guard fake (AC-5.1.12).
- [Memory: winui-no-struct-databinding] [Memory: winui-no-synccontext-marshal-vm] [Memory: smoke-per-ui-story] — standing project rules baked into ACs above.

### Open Questions (flagged for dev/reviewer — do NOT block; default given)
- **Q1 (MinSeverity coupling) — RESOLVED 2026-06-04 (Project Lead): COUPLE them.** The viewer's `MinSeverity` ALSO drives the emitter's runtime gate (turn the Verbose firehose on/off), in addition to the view filter — see AC-5.1.4 / AC-5.1.10 and Dev Notes §"MinSeverity: two meanings". Requires the new runtime-mutable `IDiagnosticLevelGate` seam (the shipped `DiagnosticOptions.MinSeverity` is `init`-only): gate seeded from `DiagnosticOptions.MinSeverity`; emitter reads the gate; `Volatile.Read` fast-path preserves AC-8.7 zero-alloc; not persisted. This is now in scope for the story, not deferred.
- **Q2 (Timestamp tz):** Epic/FR-041 say UTC `HH:mm:ss.fff`; the SSDP log uses LOCAL time. Confirm UTC is intended for the diagnostics viewer (default: yes, per FR-041 wording "UTC").
- **Q3 (single vs multiple viewer):** FR-041/FR-046 say "the Diagnostics viewer" (singular). Default: single app-lifetime viewer; re-`Open` re-activates the existing window. Confirm acceptable.
- **Q4 (category filter chips):** D8 lists category filter chips as an open follow-up — out of scope for 5.1 (severity chip only). Confirmed deferred.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (BMAD dev-story workflow)

### Debug Log References

- Core `dotnet build -warnaserror`: **0 Warning(s) / 0 Error(s)**.
- App `dotnet build` (full solution): **1 Warning (WMC1506) / 0 Error(s)** — the single pre-existing benign WMC1506 at `MainWindow.xaml:159` (the device-tree FallbackTemplate `{x:Bind Label, Mode=OneWay}` against `INodeViewModel`). Verified against the clean baseline (git stash): same warning was at `MainWindow.xaml:156`; it shifted down exactly 3 lines because the Diagnostics `MenuFlyoutItem` + `MenuFlyoutSeparator` (3 XAML lines) were inserted above it. NO new warning from the Diagnostics window/converter XAML.
- Core suite: **537 passed / 2 skipped / 0 failed** (baseline ~517 + 20 new). The 2 skipped are the source-scanning `DiagCategoriesUsageTests` + `AsyncDisciplineTests` that skip when run from the compiled assembly (pre-existing baseline). `CoreAppBoundaryTests` green (no Core→App leak from the launcher seam).
- One transient parallel-load flake observed once in `SubscriptionClientTests.Renew412_Lapses_…` (a known handler-attach race on a `CapturingDiagnosticEmitter` fake — unrelated to the emitter rewire, which never touches that fake); passed in isolation and on the immediate re-run.

### Completion Notes List

- **Q1 (RESOLVED) implemented as specified — MinSeverity couples BOTH the runtime emitter gate AND the view filter.**
  - New Core seam **`IDiagnosticLevelGate`** + `DiagnosticLevelGate` (`src/ohSpy.Core/Diagnostics/`): holds the `DiagSeverity` ordinal as an `int`, accessed via `Volatile.Read`/`Volatile.Write` (enum can't be a `volatile` field; cast). Lock-free single-value gate, cheap read for the emit hot path. Seeded from `DiagnosticOptions.MinSeverity` at construction.
  - **`DiagnosticEmitter` rewired:** ctor now takes `IDiagnosticLevelGate` instead of `IOptions<DiagnosticOptions>`; the AC-8.7 fast-path now reads `severity < _gate.MinSeverity` (a single `Volatile.Read`) and returns BEFORE any `DiagnosticEntry` allocation / `DateTime.UtcNow` / `EventId`. **AC-8.7 zero-alloc preserved** — verified by the existing elision test AND a new test that raises the gate at runtime then asserts <4 bytes/call over 100k below-gate emits.
  - **`DiagnosticsViewModel`** takes the gate by ctor; `OnMinSeverityChanged` writes through to `gate.MinSeverity`; `_minSeverity` is seeded from `gate.MinSeverity` in the ctor. The view-filter (display half) hides already-captured below-threshold rows App-side (per-row `Visibility`) WITHOUT mutating/copying the ring (AC-8.2).
- **DeferredUiDispatcher guard (AC-5.1.12, retro Action H):** `DiagnosticsViewModelTests.RingPrepend_IsAppliedThroughUiDispatcherPost` constructs the real `DiagnosticRingSink` with a `DeferredUiDispatcher`, `Push`es an entry, asserts `vm.Entries.Count == 0` + `PostCount == 1` (prepend queued, NOT applied), then `Drain()` → `Count == 1` at index 0. Proves the prepend goes THROUGH `IUiDispatcher.Post`.
- **View menu:** the `Diagnostics` `MenuFlyoutItem` was added to the **EXISTING** View `MenuFlyout` (Story 5.2's title-bar `Button Content="View"` flyout) — Diagnostics item, then a `MenuFlyoutSeparator`, then the untouched `NetworkAdapterMenu` subitem. `OnViewMenuOpening` was NOT altered. No new menu/Button created.
- **Core/App boundary:** `ShellViewModel.OpenDiagnosticsCommand` → `IDiagnosticsLauncher.Open()` (Core seam) → App `DiagnosticsLauncher` does `new DiagnosticsWindow(vm)` + `Activate()` + `Adopt(window, ShellWindow)` (the verbatim 4.3 sequence; A31 free z-order). No `new DiagnosticsWindow` in Core.
- **No new `DiagCategories` constant** added (confirmed — `DiagCategoriesUsageTests` pinned set unchanged; AC-5.1.9's `DiagnosticsFileSinkUnavailable` already exists).
- **Single viewer (Q3 default):** the launcher tracks the live window and re-activates it on re-Open; clears its handle on `Closed`.
- **AC-5.1.14 manual UI smoke is PENDING** — a headless-impossible first-class gate the Project Lead runs on real Linn/OpenHome hardware against a LARGE diagnostic stream. Story left at `review` (not `done`).
- **Deviations / notes:** (a) `Gate` test helpers return the concrete `DiagnosticLevelGate` (not the interface) to satisfy CA1859 under `-warnaserror`. (b) The existing `DiagnosticEmitterTests.Opts(...)` helper was replaced with `Gate(...)` since the emitter ctor changed. (c) `ShellViewModelTests` harness threads a `FakeDiagnosticsLauncher` (new fake) through the ctor + a new `OpenDiagnosticsCommand_InvokesLauncher` test. No open questions remain (Q1/Q2/Q3/Q4 all resolved per the story defaults).

### File List

**NEW (production):**
- `src/ohSpy.Core/Diagnostics/IDiagnosticLevelGate.cs`
- `src/ohSpy.Core/Diagnostics/DiagnosticLevelGate.cs`
- `src/ohSpy.Core/ViewModels/DiagnosticsViewModel.cs`
- `src/ohSpy.Core/ViewModels/IDiagnosticsLauncher.cs`
- `src/ohSpy.App/Views/DiagnosticsWindow.xaml`
- `src/ohSpy.App/Views/DiagnosticsWindow.xaml.cs`
- `src/ohSpy.App/Windowing/DiagnosticsLauncher.cs`
- `src/ohSpy.App/Converters/SeverityToBrushConverter.cs`

**NEW (tests):**
- `tests/ohSpy.Core.Tests/ViewModels/DiagnosticsViewModelTests.cs`
- `tests/ohSpy.Core.Tests/Diagnostics/DiagnosticLevelGateTests.cs`
- `tests/ohSpy.Core.Tests/Fakes/FakeDiagnosticsLauncher.cs`

**MODIFIED (production):**
- `src/ohSpy.Core/Diagnostics/DiagnosticEmitter.cs` — gate on `IDiagnosticLevelGate` (Volatile read) instead of `IOptions<DiagnosticOptions>`; AC-8.7 fast-path preserved.
- `src/ohSpy.Core/Diagnostics/DiagnosticRow.cs` — added `TimestampDisplay` (UTC `HH:mm:ss.fff`, invariant) + `SeverityLabel` projections.
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — `IDiagnosticsLauncher` ctor param + field + `[RelayCommand] OpenDiagnostics`.
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — registered `IDiagnosticLevelGate` (before the emitter) + `DiagnosticsViewModel` + `DiagnosticsLauncher`/`IDiagnosticsLauncher` (before `ShellViewModel`).
- `src/ohSpy.App/App.xaml.cs` — `DiagnosticsLauncher.ShellWindow = _window;` in `OnLaunched`.
- `src/ohSpy.App/MainWindow.xaml` — added the Diagnostics `MenuFlyoutItem` + separator to the existing View `MenuFlyout`.

**MODIFIED (tests):**
- `tests/ohSpy.Core.Tests/Diagnostics/DiagnosticEmitterTests.cs` — `Opts`→`Gate` helper; added runtime gate raise/lower + zero-alloc-under-gate tests.
- `tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` — thread `FakeDiagnosticsLauncher`; `OpenDiagnosticsCommand_InvokesLauncher` test.

**MODIFIED (tracking):**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `5-1` → `in-progress` → `review`.

### Review Findings

Code review performed 2026-06-05 (claude-sonnet-4-6, bmad-code-review workflow). All three layers: Blind Hunter, Edge Case Hunter, Acceptance Auditor executed inline.

- [x] [Review][Patch] P1 — Severity colour broken: classic `{Binding Entry.Severity}` inside the ItemsRepeater/x:DataType DataTemplate silently fails (ItemsRepeater leaves the realized element's DataContext null → converter never fires → every severity cell renders the default foreground) — violated AC-5.1.2. **FIXED** — but NOT via the reviewer's suggested `x:Bind … Converter={StaticResource …}`: that does not compile when the XAML root is a `Window` (WinUI 3 `Window` is not a `FrameworkElement`, so the generated StaticResource lookup fails — CS1503 in `DiagnosticsWindow.g.cs`). Instead `OnRowPrepared` now sets each realized row's `DataContext = ViewModel.Entries[args.Index]`, so the classic `{Binding}` resolves and the converter runs. [src/ohSpy.App/Views/DiagnosticsWindow.xaml:~119, .xaml.cs OnRowPrepared]

- [x] [Review][Patch] P1 — View filter dead: `OnRowPrepared` and `ReapplyFilterToRealizedRows` used `fe.DataContext is DiagnosticRow row`, always null/false for ItemsRepeater realized elements (same root cause) — violated AC-5.1.10 display half (emitter-gate half was correct). **FIXED** — both now read the row by index (`ViewModel.Entries[args.Index]` / `ViewModel.Entries[i]`, with a bounds guard in OnRowPrepared). [src/ohSpy.App/Views/DiagnosticsWindow.xaml.cs OnRowPrepared + ReapplyFilterToRealizedRows]

  > Both P1 patches applied 2026-06-05; App builds 0 errors (only pre-existing WMC1506). App-XAML/code-behind only — no Core change, suite unaffected (537/2). Awaiting AC-5.1.14 manual smoke.

- [x] [Review][Defer] P3 — Redundant `Title = "Diagnostics"` in code-behind (XAML already sets `Title="Diagnostics"`); harmless double-set, no functional impact. [src/ohSpy.App/Views/DiagnosticsWindow.xaml.cs:32] — deferred, pre-existing nit

### Change Log

- 2026-06-05 — Code review (claude-sonnet-4-6, bmad-code-review workflow): CHANGES-REQUESTED. 2 P1 patches (classic {Binding} vs {x:Bind} in ItemsRepeater → severity colour broken AC-5.1.2; view filter dead AC-5.1.10 display-half); 1 P3 deferred nit. Core gates confirmed: emitter Volatile.Read fast-path correct, thread-safe, zero-alloc preserved; Q1 gate coupling correct; Core/App boundary clean; DeferredUiDispatcher guard genuine. Story remains at `review`; AC-5.1.14 smoke still pending. Fixes needed before smoke.
- 2026-06-04 — Story 5.1 implemented (dev-story, claude-opus-4-8[1m]). Diagnostics viewer window (FR-041): Core `DiagnosticsViewModel` binds the live ring (AC-8.2 same instance); new runtime-mutable `IDiagnosticLevelGate` seam couples the viewer's `MinSeverity` to the emitter gate (Q1) — emitter rewired to read the gate (Volatile), AC-8.7 zero-alloc preserved, seeded from `DiagnosticOptions.MinSeverity`; `IDiagnosticsLauncher` Core seam + App `DiagnosticsLauncher` (Activate-then-Adopt, single viewer); `DiagnosticsWindow` (ItemsRepeater/ScrollViewer virtualised, 6 columns, UTC timestamps, `SeverityToBrushConverter`, per-row view-filter); `Diagnostics` item added to the existing View flyout. 20 new Core tests incl. the mandatory `DeferredUiDispatcher` marshalling guard (AC-5.1.12). Core `-warnaserror` 0/0; App 0/0 bar the pre-existing WMC1506; suite 537/2. Manual UI smoke (AC-5.1.14) pending on real hardware. Status → review.
