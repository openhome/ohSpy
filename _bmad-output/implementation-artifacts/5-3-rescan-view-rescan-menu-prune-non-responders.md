---
baseline_commit: 884ab845c4beb37adccc56ce76d6a30fb268cd04
---

# Story 5.3: Rescan — `View → Rescan` Menu + Prune Non-Responders

Status: done
<!-- 2026-06-05: Code review (Sonnet) APPROVED-WITH-MINOR-FIXES → 1 P2 fixed (ODE→OCE translation in
     AdapterScope.SendMSearchAsync for the switch-wins race; regression test added; W1 TOCTOU deferred as a safe
     pre-existing pattern). AC-5.3.14 manual UI smoke PASSED on the live Linn network: powered-off device pruned
     after MX while live devices + open subscriptions/NOTIFY survive; Rescan item disables mid-run; Diagnostics
     shows Adapter.Rescan entries; switch-wins shows "abandoned". review → done. Epic 5 CLOSED. -->;

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want `View → Rescan` to re-issue the M-SEARCH on the current adapter, wait MX seconds, and prune any device that did not respond to the rescan — without suspending the live unsolicited-NOTIFY listener,
so that I can clean up stale devices that left the network ungracefully (no `byebye`) without restarting the tool, and so I can confirm "this device really is gone" vs "we just haven't seen it advertise recently."

## ⭐ CRITICAL RECONCILIATIONS — READ FIRST (epic + architecture prose are STALE in five places)

The Epic 5 prose (epics.md §Story 5.3, lines 1904-1944) and the architecture source-tree map (architecture.md:2194) were written BEFORE Epics 4 + 5.1/5.2 shipped. Five of their statements are now wrong against shipped code. **Follow this story, not the prose, where they conflict.** Each was verified against shipped code on 2026-06-05 (HEAD `884ab84`).

1. **⭐ The `View` menu ALREADY EXISTS with Diagnostics + Network adapter — do NOT create it; ADD a `Rescan` item.** The epic AC ("**Given** the `View → Rescan` menu item *added to the shell in Story 5.1*") is STALE: **Story 5.2 built the View menu** and **Story 5.1 added only the `Diagnostics` item.** Neither added Rescan. The shipped shape (verified `src/ohSpy.App/MainWindow.xaml:36-45`): a title-bar `<Button Content="View">` whose `Button.Flyout` holds `<MenuFlyout Placement="BottomEdgeAlignedLeft" Opening="OnViewMenuOpening">` containing, in order: `<MenuFlyoutItem Text="Diagnostics" Command="{x:Bind ViewModel.OpenDiagnosticsCommand}" />`, a `<MenuFlyoutSeparator />`, and `<MenuFlyoutSubItem x:Name="NetworkAdapterMenu" Text="Network adapter" />`. **Story 5.3 ADDS one `<MenuFlyoutItem Text="Rescan" Command="{x:Bind ViewModel.RescanCommand}" />` to THIS existing flyout** (the natural place is right after `Diagnostics`, before the separator, or just after it — group the two action items together; keep the Network-adapter submenu last). Do NOT add a second menu / Button. The `OnViewMenuOpening` handler (MainWindow.xaml.cs:204-236) rebuilds only the adapter submenu — it does NOT touch the static Diagnostics/Rescan items; leave it untouched.

2. **⭐⭐ Rescan is an ACTION, not a window — NO launcher seam.** Unlike 5.1's Diagnostics (which opens a `Window`, so it needed an `IDiagnosticsLauncher` Core→App seam), Rescan mutates Core state only (M-SEARCH + registry prune). It is a plain `[RelayCommand]` on `ShellViewModel` calling Core methods. **No `I*Launcher`, no new Window, no Core→App boundary concern from the command itself** (the transient "Rescanning…" indicator binds an existing-style `[ObservableProperty]`, mirroring `IsSwitching`).

3. **⭐⭐⭐ `DiscoveryService.RescanAsync` CANNOT call `ISsdpTransport.SendMSearchAsync` directly — the transport is no longer reachable from `DiscoveryService`.** The architecture map (architecture.md:2194) and the epic AC (`DiscoveryService.RescanAsync(mx)` → `ISsdpTransport.SendMSearchAsync(mx, _adapterToken)`) predate **Amendment A23 (Story 5.2)**. Post-A23, `AdapterScope` OWNS the transport; `DiscoveryService` only receives a `ChannelReader<SsdpDatagram>` via `StartAsync`/`RebindAsync` — it holds **no `ISsdpTransport` reference and no adapter token**. So the rescan orchestration belongs in **`ShellViewModel.RescanCommand`** (which owns `_adapterScope`), exactly like `SwitchAdapterAsync`. **Decision (Q1, default): `ShellViewModel.RescanAsync` orchestrates; it sends the M-SEARCH through the scope-owned transport.** Because `AdapterScope` today exposes only `IncomingDatagrams` (not `SendMSearchAsync`), **add a thin pass-through `Task SendMSearchAsync(TimeSpan mx)` to `AdapterScope`** that forwards to its `_transport.SendMSearchAsync(mx, _adapterCts.Token)` (the token the scope already owns; mirrors how `AdapterScope.StartAsync` already calls `_transport.SendMSearchAsync(InitialMx, _adapterCts.Token)` at line 118). This keeps the transport encapsulated and the adapter token correct (FR-024: same token the switch cancels — AC for "switch wins"). Do NOT re-thread the transport back into `DiscoveryService`.

4. **⭐⭐⭐⭐ `DeviceRegistry.PruneNonResponders` does NOT exist — CREATE it (mirror the shipped `Clear()` cascade).** The architecture (architecture.md:2194) names it but only `OnAlive` / `OnByebye` / `Remove` / `Clear` / `RemoveCore` ship (`DeviceRegistry.cs`). **Add a public `void PruneNotSeenSince(DateTime epochUtc)`** (name it for what it does; `PruneNonResponders` is acceptable too — pick one and use it consistently) on `IDeviceRegistry` + `DeviceRegistry`: UI-thread (`ui.AssertOnUiThread()`); snapshot keys first; for each entry whose `LastSeenUtc < epochUtc`, call the existing private `RemoveCore(udn)` (cancel+dispose `DeviceCts` + raise `DeviceRemoved(udn)` — byebye-identical, the FR-037 popup path). Return the count pruned (so the command can emit "pruned N"). This is the load-bearing prune primitive.

5. **Epic 5 is 5.1 + 5.3 only; 5.3 is the LAST story.** 5.2 (adapter switch) was re-sequenced into Epic 4 and is **done**. `epic-5` is already `in-progress` (5.1) — **this story is NOT the first in the epic, so no epic-status change.** After 5.3 reaches `done`, Epic 5 closes (retrospective is `optional`).

## The prune rule (the load-bearing design — derived from the shipped liveness model)

**How the registry tracks liveness (verified `RegistryEntry.cs` + `DeviceRegistry.OnAlive`):** every `RegistryEntry` carries `LastSeenUtc` (UTC of the most recent alive) and `AliveCount`. `DeviceRegistry.OnAlive(...)` calls `entry.RefreshSsdpMetadata(nowUtc, ...)` which sets `LastSeenUtc = nowUtc` and `AliveCount++` on **every** alive announcement — **and M-SEARCH responses route through the exact same `OnAlive` path** (verified `DiscoveryService.RouteOnUiThread`: an M-SEARCH response has `NTS == null`, so it falls into the `else` alive branch; `effectiveNt = ann.NT ?? ann.ST` = `ST: upnp:rootdevice`; → `registry.OnAlive(...)`). There is no separate "responded" flag and we do NOT need one.

**The prune rule (FR-023 + FR-024 satisfied together):**
1. Capture `epochUtc = DateTime.UtcNow` **immediately before** sending the M-SEARCH. (`OnAlive` stamps `arrivalUtc` from the datagram's `ArrivalUtc`, which is `DateTime.UtcNow` at receive — both UTC, monotone-enough for this purpose; see Q2.)
2. Send M-SEARCH; live NOTIFY handling never pauses (no socket teardown, no consumer suspension — FR-024).
3. Wait MX (plus a small grace; see Q3).
4. Prune every entry with `LastSeenUtc < epochUtc`. An entry that responded to the rescan **or** received any unsolicited alive during the window has `LastSeenUtc ≥ epochUtc` → survives (FR-024). An entry that got a `byebye` during the window was already removed by `OnByebye` → the prune simply doesn't find it (idempotent; no double-remove — the epic's third integration AC). Devices that did not respond and did not announce → `LastSeenUtc` is stale (< epoch) → pruned (FR-023).

This rule is strictly a function of the SHIPPED `LastSeenUtc` semantics — no new per-entry "seen-this-scan" bookkeeping, no change to `OnAlive`/`RefreshSsdpMetadata`.

## Acceptance Criteria

Reconciled against shipped code. AC numbers map to the epic's `Given/When/Then` blocks (epics.md:1910-1944) plus the standing project gates. Test names reference FR-021..FR-024 per Pattern 15.

1. **AC-5.3.1 — Rescan menu item (FR-021).** `MainWindow.xaml`'s EXISTING View `MenuFlyout` gains a `<MenuFlyoutItem Text="Rescan" Command="{x:Bind ViewModel.RescanCommand}" />` (grouped with the `Diagnostics` action item; Network-adapter submenu stays last). No new menu/Button. `OnViewMenuOpening` untouched.

2. **AC-5.3.2 — `ShellViewModel.RescanCommand` (FR-021).** A `[RelayCommand]` (async) named `Rescan` on `ShellViewModel`. Choosing the menu item invokes it. It is fire-and-forget from the menu/command infra (the body handles its own exceptions — Amendment A26, the `SwitchAdapterAsync` precedent). Default MX = `TimeSpan.FromSeconds(5)` (FR-022 parity with the startup `AdapterScope.InitialMx`).

3. **AC-5.3.3 — Re-entrancy guard (menu disabled during a rescan).** The command cannot be triggered while a rescan is already in flight (an `Interlocked` guard, OR `IRelayCommand.CanExecute` driven by a `[ObservableProperty] bool _isRescanning`). A second invocation while one is running is a silent no-op (no overlapping rescans). The Rescan menu item is disabled while `IsRescanning` is true (CanExecute → MenuFlyoutItem auto-disables via the bound `RescanCommand`).

4. **AC-5.3.4 — "Rescanning…" transient indicator (NFR-UI3).** A `[ObservableProperty] bool _isRescanning` (mirrors `IsSwitching`) is set `true` at start (synchronously, on the UI thread — the command begins on the UI thread) and cleared `false` at end via `_ui.Post` (the post-await continuation runs off-thread — WinUI has no SynchronizationContext; memory `winui-no-synccontext-marshal-vm`). The App binds it to a status-bar message / inline spinner. No flicker.

5. **AC-5.3.5 — M-SEARCH via the scope-owned transport (FR-022).** `RescanAsync` sends an M-SEARCH with `mx` through the current `AdapterScope` (new pass-through `AdapterScope.SendMSearchAsync(TimeSpan)` → `_transport.SendMSearchAsync(mx, _adapterCts.Token)`). Identical wire semantics to startup discovery — same `ST: upnp:rootdevice`, same `BuildMSearchPayload` (verified `SsdpTransport.BuildMSearchPayload` is the only M-SEARCH builder; reused unchanged). On the zero-adapter host (`_adapterScope is null` / `CurrentAdapterIPv4 is null`) Rescan is a safe no-op (NFR-R5) — nothing to scan.

6. **AC-5.3.6 — Live listening continues (FR-024).** No socket teardown, no transport dispose, no `DiscoveryService` suspension, no callback-host or subscription teardown during a rescan. The multicast + search receive loops keep delivering; `OnAlive`/`OnByebye` keep firing throughout the MX window. (Contrast: this is NOT the 5.2 adapter switch — verify nothing in the rescan path calls `AdapterScope.DisposeAsync`, `_callbackHost.DisposeAsync`, `_registry.Clear()`, `DiscoveryService.RebindAsync`, or `SetCallbackHost`/`SetAdapterContext`.)

7. **AC-5.3.7 — Epoch stamp + MX wait.** `RescanAsync` captures `epochUtc = DateTime.UtcNow` BEFORE the M-SEARCH send, then waits the MX window (plus an optional small grace — Q3) before pruning. The wait is testable WITHOUT a real 5 s delay via an injectable delay seam (`Func<TimeSpan, CancellationToken, Task>`, the shipped `SubscriptionClient._delay` precedent — see Dev Notes). The wait honours the adapter token (cancelled by a concurrent switch — AC-5.3.10).

8. **AC-5.3.8 — Prune via `DeviceRegistry.PruneNotSeenSince(epochUtc)` (FR-023).** After the MX window, the prune runs on the UI thread (marshalled via `_ui.Post`/`PostAsync`). It removes every entry with `LastSeenUtc < epochUtc` through the byebye-identical `RemoveCore` cascade: `DeviceCts.Cancel()` (cancels that device's in-flight fetch), `DeviceRemoved(udn)` raised (tree drops the row; open Properties/Invocation/Subscription popups flip to their FR-037 device-gone state). Returns the pruned count. Devices that responded or got an unsolicited alive during the window (`LastSeenUtc ≥ epochUtc`) remain untouched — their `LastSeenUtc`/`AliveCount` already refreshed by `OnAlive`.

9. **AC-5.3.9 — Rescan drill + FR-024 integration tests (the epic's test block).**
   - **Drill (FR-023):** a fixture populates the registry with 5 devices A..E (each via `OnAlive`); a rescan runs where only A/B/C "respond" (feed A/B/C alives during the window, after the epoch); after the window D and E are pruned, A/B/C remain. Test name references FR-023.
   - **FR-024 (alive during window):** during the rescan, an unsolicited `alive` for E arrives (after the epoch) → E is NOT pruned (it announced itself; `LastSeenUtc ≥ epoch`). Test name references FR-024.
   - **FR-024 (byebye during window, no double-remove):** during the rescan, an unsolicited `byebye` for A arrives → A is removed via the byebye path; the prune does NOT re-remove an already-gone entry (idempotent — `RemoveCore`'s `TryRemove` is a no-op for the missing key; `DeviceRemoved` is NOT raised twice for A). Test name references FR-024.

10. **AC-5.3.10 — Adapter switch wins over an in-flight rescan.** A rescan triggered concurrently with an adapter switch must lose: the switch's `_adapterCts.Cancel()` (inside `AdapterScope.DisposeAsync`, step 1) aborts the in-flight rescan's MX wait (the wait is linked to the adapter token) → the rescan throws `OperationCanceledException` internally, which the command swallows. No exception surfaces to the user. A `Warning` diagnostic notes the rescan was abandoned. The prune does NOT run against the new (fresh, post-switch) registry. (Mind the re-entrancy interplay: the switch and rescan are distinct guards; do NOT let the rescan guard block the switch. See Dev Notes §"Two guards, switch wins".)

11. **AC-5.3.11 — Diagnostics emission (FR-039/D8).** An `Information` diagnostic is emitted at rescan start ("rescan started") and at completion with the pruned count in context ("rescan pruned N non-responders"), so the operator sees it in the Story 5.1 viewer. The abandoned-by-switch path emits a `Warning`. **DECISION (Q4): add a NEW `DiagCategories.Rescan` constant** — it is semantically distinct from `AdapterSwitch` (a different operator action; the epic offers reuse-or-new and a dedicated category reads correctly in the viewer/filters; the just-shipped 5.1 smoke set the precedent of adding `SsdpSearchObserved` cleanly). **⚠️ This changes the architecturally-pinned set: you MUST update `DiagCategories.cs` (the constant + its XML-doc) AND `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` `DiagCategories_ExactSetMatchesArchitecturePinnedList` `expectedNames` array AND the architecture D8 list (`architecture.md` §Decision-8 category table) — all three together, in one PR.** The structural `DiagCategoriesUsageTests` (dot-separated/unique/non-empty) pass automatically for `"Rescan"` (use a dotted value, e.g. `Rescan` → `"Adapter.Rescan"` or `"Discovery.Rescan"`; a single-token `"Rescan"` FAILS `EveryCategoryConstant_IsDotSeparated` — pick a dotted namespace). If you conclude reuse-`AdapterSwitch` is better after all, STOP and flag it — but the default for this story is the new dotted constant.

12. **AC-5.3.12 — Marshalling guard (retro Action H — MANDATORY, since `RescanAsync` is async).** At least one test using `DeferredUiDispatcher` (NOT `InlineUiDispatcher`) proves the post-MX-wait prune + the `IsRescanning=false` clear are applied THROUGH `IUiDispatcher.Post`: after the (faked) wait completes, the registry is unchanged + `IsRescanning` still true until `Drain()` is called; after `Drain()`, the prune has run and `IsRescanning` is false. (The async path mutates observable state from an off-thread continuation — project standing rule.)

13. **AC-5.3.13 — Build + suite gates.** Core builds `-warnaserror` 0/0 (mind VSTHRD / async-discipline on the new async command + the delay seam). App builds with only the pre-existing benign `WMC1506` (no NEW warnings; the single `MenuFlyoutItem` line may shift the WMC1506 line number — that is not a new warning). Full suite green (current baseline 537 passed / 2 skipped — see Dev Notes). `DiagCategoriesUsageTests`, `DiagCategoriesTests` (with the updated exact set), `CoreAppBoundaryTests`, `AsyncDisciplineTests` all green.

14. **AC-5.3.14 — Manual UI smoke (FIRST-CLASS GATE — `smoke-per-ui-story` + retro Action L).** Build + run the real app on the live Linn/OpenHome network. Discover devices (tree fills). Then exercise: (a) **prune** — power off (or unplug) one device that left no `byebye`, choose `View → Rescan`, confirm the dead device's row disappears after ~MX seconds while live devices remain; (b) **liveness preserved (FR-024)** — confirm unsolicited NOTIFYs still arrive during the rescan (SSDP log keeps scrolling) and an open Subscription popup keeps receiving NOTIFY rows across the rescan (NOT lapsed); (c) **open subscriptions survive** — a subscription to a device that DOES respond is NOT torn down (no UNSUBSCRIBE, no lapse banner) while a subscription to a PRUNED device flips to its FR-037 device-gone banner; (d) **re-entrancy / indicator** — the Rescan item is disabled and a "Rescanning…" indicator shows during the window, then clears; (e) **large network responsiveness (Action L)** — on a busy network the UI stays responsive throughout. Do NOT smoke only a trivial case. Story stays at `review` until this passes (Project Lead, real hardware).

## Tasks / Subtasks

- [x] **Task 1 — `DeviceRegistry.PruneNotSeenSince` (Core)** (AC: 8, 9)
  - [x] Add `int PruneNotSeenSince(DateTime epochUtc)` to `IDeviceRegistry` (`src/ohSpy.Core/Devices/IDeviceRegistry.cs`) with XML-doc mirroring `Clear()`'s (UI-thread, byebye-identical cascade, idempotent, FR-023). Reference FR-023.
  - [x] Implement in `DeviceRegistry.cs`: `ui.AssertOnUiThread()`; snapshot `_entries.Keys.ToArray()`; for each, if `_entries.TryGetValue(udn, out var e)` && `e.LastSeenUtc < epochUtc` → `RemoveCore(udn)` (reuse the existing private cascade — do NOT duplicate the cancel/dispose/raise). Count + return the number pruned. Snapshot-before-mutate (same rationale as `Clear()`: a `DeviceRemoved` handler may re-read the registry).
  - [x] Do NOT touch `OnAlive`/`RefreshSsdpMetadata`/`RemoveCore` — the prune rides the existing `LastSeenUtc` liveness model.

- [x] **Task 2 — `AdapterScope.SendMSearchAsync` pass-through (Core)** (AC: 5)
  - [x] Add `public Task SendMSearchAsync(TimeSpan mx)` to `AdapterScope` (`src/ohSpy.Core/Discovery/AdapterScope.cs`) that forwards to `_transport.SendMSearchAsync(mx, _adapterCts.Token)` (the scope-owned token — so a switch cancel aborts it). Guard: only valid after a successful `StartAsync` (when `_transportStarted` / `CurrentAdapterIPv4` is non-null); a defensive no-op or guard on the zero-adapter scope is fine. XML-doc it as the Story 5.3 rescan re-trigger (FR-022).
  - [x] Do NOT re-thread the transport into `DiscoveryService` (A23 keeps it scope-owned).

- [x] **Task 3 — `ShellViewModel.RescanCommand` + `IsRescanning` (Core)** (AC: 2, 3, 4, 5, 6, 7, 10, 11, 12)
  - [x] Add `[ObservableProperty] private bool _isRescanning;` (mirror `_isSwitching`). Add the delay seam: an instance field `Func<TimeSpan, CancellationToken, Task> _rescanDelay = (d, ct) => Task.Delay(d, ct);` plus an `internal void SetRescanDelayForTest(Func<…> delay)` seam (the `SubscriptionClient._delay` + `SetAdapterTeardownBudgetForTest` precedents).
  - [x] Add `[RelayCommand(CanExecute = nameof(CanRescan))] private async Task RescanAsync()`. Used `[NotifyCanExecuteChangedFor(nameof(RescanCommand))]` on `_isRescanning` so the generated setter auto-raises CanExecute (no manual `NotifyCanExecuteChanged()` needed). `bool CanRescan() => !IsRescanning;`.
  - [x] Body: (1) re-entrancy guard `if (IsRescanning) return;`. (2) zero-adapter no-op: `if (scope is null || scope.CurrentAdapterIPv4 is null) return;`. (3) `IsRescanning = true` (UI thread, synchronous; the [NotifyCanExecuteChangedFor] setter disables the menu item) + `_diag.Information(DiagCategories.Rescan, "rescan started")`. (4) capture `var epochUtc = DateTime.UtcNow;`. (5) `var token = scope.AdapterToken;` then `await scope.SendMSearchAsync(RescanMx).ConfigureAwait(false);`. (6) `await _rescanDelay(RescanMx + RescanGrace, token).ConfigureAwait(false);` (grace = 500 ms, Q3). (7) prune + completion-diag + transient clear marshalled TOGETHER in a single `_ui.Post(() => { var pruned = _registry.PruneNotSeenSince(epochUtc); _diag.Information(...); IsRescanning = false; })` — see Dev Note "AC-5.3.12 marshalling choice".
  - [x] `catch (OperationCanceledException)` (switch won the token — AC-5.3.10): `_diag.Warning(DiagCategories.Rescan, "rescan abandoned — adapter switch in progress")`; do NOT prune; `_ui.Post(() => IsRescanning = false)`. `catch (Exception ex) when (ex is not OOM)`: `_diag.Warning(DiagCategories.Rescan, "rescan failed", new DiagnosticContext { ErrorText = ex.Message })`; `_ui.Post(() => IsRescanning = false)`.
  - [x] Transient clear is marshalled via `_ui.Post` on every exit path (success path clears inside the same posted block as the prune; cancel/error paths clear via their own `_ui.Post`). The `[NotifyCanExecuteChangedFor]` setter raises CanExecute when `IsRescanning` flips.
  - [x] **Two guards, switch wins (AC-5.3.10):** Rescan uses its OWN guard (`IsRescanning` / CanExecute), SEPARATE from the `_switching` startup/switch guard. `SwitchAdapterAsync` is untouched — it does NOT wait on `IsRescanning`. A rescan fired mid-switch no-ops on the null-scope check; an in-flight rescan's MX wait is linked to the scope token the switch cancels. Documented in Dev Note.
  - [x] Marshalling discipline (Action H): every post-await observable mutation (`IsRescanning` clear, the prune) goes via `_ui.Post` — the body resumes off-thread after the first `await`.

- [x] **Task 4 — `DiagCategories.Rescan` constant + pinned-set sync (Core + tests + arch)** (AC: 11, 13)
  - [x] Add `public const string Rescan = "Adapter.Rescan";` to `src/ohSpy.Core/Diagnostics/DiagCategories.cs` with an XML-doc.
  - [x] **Update `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs`** — added `"Rescan"` to the `expectedNames` array in `DiagCategories_ExactSetMatchesArchitecturePinnedList`.
  - [x] **Update the architecture D8 category list** — added `Adapter.Rescan` to BOTH the `DiagCategories` constants block AND the Pattern-11 mandatory-context table in `architecture.md` §Decision 8.
  - [x] Confirm `DiagCategoriesUsageTests` (structural) still green (the dotted value `Adapter.Rescan` passes).

- [x] **Task 5 — App menu wiring** (AC: 1, 4)
  - [x] `MainWindow.xaml`: added `<MenuFlyoutItem Text="Rescan" Command="{x:Bind ViewModel.RescanCommand}" />` to the EXISTING View `MenuFlyout`, grouped right after `Diagnostics`, before the separator. PRESERVED `Opening="OnViewMenuOpening"`, the Diagnostics item, the separator, the `NetworkAdapterMenu` subitem (still last). No new menu/Button.
  - [x] v1 affordance: the disabled (CanExecute-driven) menu item is the "Rescanning…" indicator; no extra status-bar chrome added (Do NOT over-build). `IsRescanning` is the bound seam available for a future spinner.
  - [x] No DI change — `RescanCommand` is a member of the already-registered `ShellViewModel` singleton; the delay seam defaults inline (no new ctor param).

- [x] **Task 6 — Tests (Core)** (AC: 8, 9, 10, 11, 12)
  - [x] `DeviceRegistryTests` (extended): `PruneNotSeenSince` removes only `LastSeenUtc < epoch`; raises `DeviceRemoved` per pruned UDN; cancels each pruned `DeviceCts`; returns the count; refreshed-entry survives; empty→0; idempotent (no double `DeviceRemoved`). [Trait fr FR-023].
  - [x] `ShellViewModelTests` (extended the existing `NewHarness`/`RecordingSsdpTransport` rig): the rescan drill (5 devices, A/B/C respond via the delay-seam callback, D/E pruned); FR-024 alive-during-window (E survives); FR-024 byebye-during-window (A removed once); switch-wins (delay seam throws OCE → no prune, Warning); M-SEARCH through the scope transport (`MSearchCallCount` 1→2, no transport dispose, no new transport); start+completion diagnostics with the count; zero-adapter no-op; CanExecute false mid-flight. `SetRescanDelayForTest` controls the MX wait. [Trait fr FR-021..024].
  - [x] **AC-5.3.12 marshalling guard:** `RescanAsync_PruneAndClear_AreMarshalled` drives a `DeferredUiDispatcher`; before `Drain()` the registry is unchanged + `IsRescanning` still true; after `Drain()` the prune ran + `IsRescanning` false.
  - [x] Diagnostics assertions via `CapturingDiagnosticEmitter`: start `Information`, completion `Information` with the count, abandoned `Warning` — all `DiagCategories.Rescan`.

- [x] **Task 7 — Build, suite, smoke** (AC: 13, 14)
  - [x] Core `dotnet build -warnaserror` 0/0; App build succeeds with ONLY the pre-existing benign `WMC1506` (shifted :159→:162 by the 1-line menu insert — not a new warning). Full suite 552 passed / 2 skipped (from 539/2; +13). `DiagCategoriesTests` (updated exact set), `DiagCategoriesUsageTests`, `CoreAppBoundaryTests`, `AsyncDisciplineTests` all green.
  - [ ] **AC-5.3.14 manual UI smoke on the live Linn/OpenHome network — PENDING (first-class gate, Project Lead performs on real hardware).** Story stays at `review` until smoke passes (dev-story → code-review → smoke → done pattern).

## Dev Notes

### The rescan home: ShellViewModel, not DiscoveryService (A23 reconciliation — READ)
The architecture map (architecture.md:2194) lists `Discovery/DiscoveryService.RescanAsync` and `Devices/DeviceRegistry.PruneNonResponders`. **`PruneNonResponders` is correct** (build it as `PruneNotSeenSince` on the registry — Task 1). **`DiscoveryService.RescanAsync` is NOT, post-A23:** `DiscoveryService` no longer holds the transport or the adapter token (Story 5.2 / Amendment A23 decoupled it to a `ChannelReader` + per-call tokens — verified `DiscoveryService.cs:17-34`). The only types that can send an M-SEARCH on the live adapter are `AdapterScope` (owns the transport) and, above it, `ShellViewModel` (owns the scope). So the rescan ORCHESTRATION lives in `ShellViewModel.RescanAsync`, exactly parallel to `SwitchAdapterAsync`, and the M-SEARCH re-trigger is a thin `AdapterScope.SendMSearchAsync(mx)` pass-through. This is the smallest, most encapsulated change and keeps the A23 invariant (transport is scope-owned) intact. If a future refactor wants a `DiscoveryService.RescanAsync`, it would have to re-acquire a transport handle — out of scope and against A23.

### Does an M-SEARCH re-trigger already exist? YES on the transport, NO on the reachable surface
- `ISsdpTransport.SendMSearchAsync(TimeSpan mx, CancellationToken ct)` EXISTS and is fully implemented (`SsdpTransport.cs:110-122`; builds the UDA `M-SEARCH * … ST: upnp:rootdevice` payload via `BuildMSearchPayload`, clamps MX ≥ 1 s, egresses on the bound adapter's search socket). It is already called once at startup (`AdapterScope.StartAsync:118`). **Re-use it verbatim — do NOT write a second M-SEARCH builder.**
- But it is NOT reachable from `ShellViewModel` today: `ShellViewModel` holds `_adapterScope` (an `AdapterScope`), and `AdapterScope` exposes only `IncomingDatagrams`, `AdapterToken`, `CurrentAdapterIPv4`, `StartAsync`, `DisposeAsync` — NOT a public M-SEARCH method. **Task 2 adds the one-line pass-through.** That is the entirety of the "re-trigger" plumbing.

### Liveness model + the prune rule (verified — see top section "The prune rule")
- `RegistryEntry.LastSeenUtc` is updated by `RefreshSsdpMetadata` on EVERY alive (unsolicited OR M-SEARCH response — both flow through `DeviceRegistry.OnAlive`). `AliveCount` bumps too. No "responded-this-scan" flag exists or is needed.
- M-SEARCH responses are alive-equivalent at the routing layer (`DiscoveryService.RouteOnUiThread`: `NTS == null` → alive branch; `effectiveNt = NT ?? ST`).
- Prune = "stamp `epochUtc` before M-SEARCH; after MX, remove entries with `LastSeenUtc < epochUtc`." Survivors: responders + in-window unsolicited alives. Already-byebye'd entries: gone before the prune; `RemoveCore`'s `TryRemove` makes the prune idempotent for them (no double `DeviceRemoved`).
- `DeviceRegistry.Clear()` is the EXACT cascade template for `PruneNotSeenSince` — copy its snapshot-then-`RemoveCore` shape; the only difference is the per-entry `LastSeenUtc < epoch` predicate instead of "all".

### Shipped pieces — VERIFIED present, do NOT rebuild
- `ISsdpTransport.SendMSearchAsync` / `SsdpTransport.BuildMSearchPayload` / `ClampMxSeconds` — the M-SEARCH machinery (Story 2.1). Reuse.
- `DeviceRegistry.RemoveCore` (private) — the byebye/clear cascade (`DeviceCts.Cancel()` + `Dispose()` + `DeviceRemoved(udn)`). The prune calls it per stale entry.
- `RegistryEntry.LastSeenUtc` / `AliveCount` / `DeviceCts` / `DeviceToken` — liveness + per-device cancellation (Story 2.3/2.4 + A30 string identity).
- `ShellViewModel._switching` re-entrancy guard + `IsSwitching` transient + `_ui.Post`/`PostAsync` marshalling + the `SetAdapterTeardownBudgetForTest` test-seam pattern — copy the shapes for the rescan guard/indicator/delay-seam.
- `SubscriptionClient._delay` (`Func<TimeSpan, CancellationToken, Task>`) — the delay-seam precedent for unit-testing the MX wait without a real 5 s sleep (`SubscriptionClient.cs:84,107,425`).
- `DeviceRemoved` is `Action<string>` (UDN string — Amendment A30). The popup FR-037 device-gone path subscribes to it (Properties 2.9 / Invocation 3.2 / Subscription 4.3) — the prune triggers it identically to byebye, so popups already react correctly; NO popup-side change.
- The View menu + `OnViewMenuOpening` (Story 5.2) + the Diagnostics item (Story 5.1) — ADD the Rescan item alongside; do not rebuild.

### DiagCategories — a NEW dotted constant is needed (Q4) — this changes the PINNED set
Unlike 5.1 (which needed NO new constant), 5.3's epic explicitly offers "reuse `AdapterSwitch` OR add a new `Rescan`." A dedicated category reads correctly in the viewer and filters. The pinned set is guarded by `DiagCategoriesTests.DiagCategories_ExactSetMatchesArchitecturePinnedList` (a literal `expectedNames` array, ~30 names) which fails on ANY add/delete by design, plus the architecture D8 list. **Add the constant in `DiagCategories.cs`, the name to the test's `expectedNames`, and the entry to architecture D8 — all in one PR (Task 4).** Use a DOTTED value (`"Adapter.Rescan"` recommended — keeps it near `Adapter.Switch`; or `"Discovery.Rescan"`) — a bare `"Rescan"` fails `EveryCategoryConstant_IsDotSeparated`. This is the exact precedent Story 5.1's smoke used to add `SsdpSearchObserved`. ⚠️ Flag this constant addition explicitly to the reviewer as intentional (not drift).

### Threading / WinUI (memory `winui-no-synccontext-marshal-vm` — MANDATORY guard)
`RescanAsync` is async: after the first `await` (the M-SEARCH send, then the MX wait) the continuation resumes on a thread-pool thread (WinUI has no SynchronizationContext). EVERY observable mutation in the continuation — the registry prune (mutates the bound tree via `DeviceRemoved`) and the `IsRescanning = false` clear — MUST be marshalled via `_ui.Post`/`PostAsync`. The `IsRescanning = true` set is synchronous at the top (the command starts on the UI thread) and is safe direct. The `DeviceRegistry.PruneNotSeenSince` itself asserts UI-thread, so it MUST be invoked inside a `_ui.PostAsync(...)` (the `SwitchAdapterAsync` registry-clear precedent: `await _ui.PostAsync(() => { _registry.Clear(); … });`). **AC-5.3.12 guards this with `DeferredUiDispatcher`** — `InlineUiDispatcher` would mask a missing marshal (the 3.2 crash class). This is non-negotiable per retro Action H.

No new UI binding hazards beyond the simple `bool IsRescanning` (a CLR bool property — no struct-binding risk; `winui-no-struct-databinding` N/A). The menu item binds an `ICommand` — standard. (Story 5.1 learned `x:Bind` + a StaticResource converter does NOT compile under a Window root — N/A here; no converter on the Rescan item.)

### Standing gates
- **Core/App boundary (`CoreAppBoundaryTests`, Pattern 2):** `ShellViewModel` is Core and must not reference WinUI. The rescan command touches only Core types (registry, scope, diag, ui) — no Window, no launcher seam (Reconciliation #2). Stays green.
- **`-warnaserror` (VSTHRD / async-discipline):** the new async command + delay seam must be `ConfigureAwait(false)` throughout, no `async void` (use `async Task` `[RelayCommand]`), no `.Result`/`.Wait`. The fire-and-forget from the menu mirrors `SwitchAdapterAsync` (A26).
- **chaos hook:** pre-commit, unchanged.
- **smoke-per-ui-story (FIRST-CLASS):** AC-5.3.14 — the dev implements + automated tests; the manual smoke on the live Linn network is the Project Lead's gate. Story ends at `review`, not `done`.

### Source tree — files this story touches
NEW: (none — no new types; all additions are members on existing files)

UPDATE (read current state before editing — listed with what to preserve):
- `src/ohSpy.Core/Devices/IDeviceRegistry.cs` — ADD `int PruneNotSeenSince(DateTime epochUtc)`. PRESERVE the existing surface + the `Clear()` doc style.
- `src/ohSpy.Core/Devices/DeviceRegistry.cs` — ADD the `PruneNotSeenSince` impl (reuse `RemoveCore`). PRESERVE `OnAlive`/`OnByebye`/`Remove`/`Clear`/`RemoveCore` exactly.
- `src/ohSpy.Core/Discovery/AdapterScope.cs` — ADD `SendMSearchAsync(TimeSpan mx)` pass-through. PRESERVE the A23 transport ownership, `StartAsync`/`DisposeAsync`, the `_adapterCts` token.
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — ADD `_isRescanning` + `RescanCommand` + the delay seam + `SetRescanDelayForTest`. PRESERVE the ENTIRE adapter-scope/switch machinery (`StartAsync`/`SwitchAdapterAsync`/`DisposeAsync`/`_switching`) — the rescan guard is SEPARATE (Task 3 "two guards"). Do not touch `OpenDiagnostics`/`SwitchAdapterAsync`.
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` — ADD the `Rescan` dotted constant (Task 4).
- `src/ohSpy.App/MainWindow.xaml` — ADD one `MenuFlyoutItem` to the EXISTING View `MenuFlyout` (lines 36-45). PRESERVE the `Opening` hook, Diagnostics item, separator, `NetworkAdapterMenu`. Do NOT add a new menu/Button.
- `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` — ADD `"Rescan"` to `expectedNames`.
- `architecture.md` §Decision-8 — ADD `Adapter.Rescan` to the pinned category list.
- `tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs` (or wherever registry tests live) — ADD prune tests.
- `tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` — ADD rescan drill + FR-024 + switch-wins + marshalling-guard tests (extend the existing harness/recording transport).

### Testing standards
- xUnit + FluentAssertions (`Should()`); `[Trait("ac","AC-5.3.x")]` / `[Trait("fr","FR-02x")]`; Pattern 15 — FR-021..FR-024 in test names.
- `InlineUiDispatcher` for ordering/behaviour; `DeferredUiDispatcher` for the MANDATORY marshalling guard (AC-5.3.12) — both in `tests/ohSpy.Core.Tests/Fakes/`.
- The MX wait is unit-tested via `SetRescanDelayForTest` (delay seam) — NO real 5 s sleeps in tests.
- The existing `ShellViewModelTests` harness (`NewHarness` + `RecordingSsdpTransport` with `MSearchCallCount` + `DeviceRegistry` real + `CapturingDiagnosticEmitter`) covers the rescan flow end-to-end in Core. App-only bits (the menu item, live sockets) are the manual smoke (AC-5.3.14).
- Baseline before this story: 537 passed / 2 skipped (the 2 skips are the source-scanning `DiagCategoriesUsageTests`/`AsyncDisciplineTests` when run from the compiled assembly — pre-existing). Updating `DiagCategoriesTests.expectedNames` keeps it green; forgetting to fails the exact-set test (intended).

### Project Structure Notes
- `ShellViewModel` is Core (`src/ohSpy.Core/ViewModels/`). `AdapterScope`/`DeviceRegistry`/`DiagCategories` are Core. `MainWindow.xaml` is at `src/ohSpy.App/MainWindow.xaml` (NOT under `Views/`).
- No DI registration change: `RescanCommand` is a member of the already-singleton `ShellViewModel`; the delay seam defaults inline (no ctor param), so no DI/test-ctor blast radius (contrast 5.1/5.2 which added ctor params).

### References
- [Source: epics.md#Story 5.3] — epic AC (lines 1904-1944; STALE on "added in 5.1", on `DiscoveryService.RescanAsync` calling the transport directly post-A23). Reconciled above.
- [Source: epics.md:978-980] — the Story 2.4 "rescan cancellation contract (forward-compatible)" — the discovery layer was to expose a re-issue-M-SEARCH + track-responders method; superseded by A23 (the responder-tracking is the registry's `LastSeenUtc`, the re-issue is the scope's transport).
- [Source: prd.md#4.8 Rescan / FR-021..FR-024] (lines 353-373) — menu command, identical M-SEARCH, prune non-responders, live listening not suspended.
- [Source: prd.md:143] — "a rescan (FR-021) MUST NOT suspend unsolicited-advertisement handling (FR-024)". [Source: prd.md:163] — a fresh entry after prune MUST re-fetch (the prune removes; a later alive re-creates Pending → fetch).
- [Source: architecture.md:33,69,744,2194] — Rescan summary; cancellation plumbing (prune cancels in-flight fetches + informs popups FR-037); the (partly stale) source-tree map naming `DiscoveryService.RescanAsync` + `DeviceRegistry.PruneNonResponders`.
- [Source: architecture.md §Decision 8] — DiagCategories pinned list (update for `Adapter.Rescan`, Task 4).
- [Code: src/ohSpy.App/MainWindow.xaml:36-45 + MainWindow.xaml.cs:204-236] — the EXISTING View menu (5.1 Diagnostics + 5.2 Network-adapter) to extend; `OnViewMenuOpening` rebuilds only the adapter submenu.
- [Code: src/ohSpy.Core/Discovery/ISsdpTransport.cs:27-34 + SsdpTransport.cs:110-122,317-327] — `SendMSearchAsync` + `BuildMSearchPayload` (reuse).
- [Code: src/ohSpy.Core/Discovery/AdapterScope.cs:114-118] — startup already calls `_transport.SendMSearchAsync(InitialMx, _adapterCts.Token)`; the rescan pass-through mirrors it.
- [Code: src/ohSpy.Core/Discovery/DiscoveryService.cs:100-127] — `RouteOnUiThread` proves M-SEARCH responses route through `registry.OnAlive` (alive-equivalent) → `LastSeenUtc` refresh.
- [Code: src/ohSpy.Core/Devices/DeviceRegistry.cs:105-126] — `Clear()` + `RemoveCore` cascade (the prune template). [Code: RegistryEntry.cs:37-41,109-118] — `LastSeenUtc`/`AliveCount`/`RefreshSsdpMetadata` (the liveness model).
- [Code: src/ohSpy.Core/ViewModels/ShellViewModel.cs:55-56,212-318] — `IsSwitching` transient + `_switching` guard + `SwitchAdapterAsync` marshalling/`PostAsync(_registry.Clear)` precedent (copy the shapes; rescan is a SEPARATE guard).
- [Code: src/ohSpy.Core/Events/SubscriptionClient.cs:84,107,425] — the `Func<TimeSpan,CancellationToken,Task>` delay-seam precedent for the MX wait.
- [Code: tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs + Fakes/SwitchRecorder.cs (RecordingSsdpTransport, MSearchCallCount)] — the harness to extend.
- [Code: tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs:39-65] — the pinned `expectedNames` array to update. [Code: tests/.../Fakes/DeferredUiDispatcher.cs] — the marshalling-guard fake (AC-5.3.12).
- [Memory: winui-no-synccontext-marshal-vm] [Memory: smoke-per-ui-story] — standing rules baked into ACs above.

### Open Questions (flagged for dev/reviewer — do NOT block; default given)
- **Q1 (rescan home) — RESOLVED (default): `ShellViewModel.RescanAsync` orchestrates** (not `DiscoveryService` — A23 made the transport unreachable from there). Adds `AdapterScope.SendMSearchAsync` pass-through + `DeviceRegistry.PruneNotSeenSince`. If the reviewer prefers re-coupling the transport into `DiscoveryService`, flag it — but that fights A23.
- **Q2 (epoch clock skew):** `epochUtc = DateTime.UtcNow` captured pre-send vs `OnAlive`'s `arrivalUtc` (datagram `ArrivalUtc`, also `DateTime.UtcNow` at receive). Since responses arrive AFTER the send, their `arrivalUtc > epochUtc` → `LastSeenUtc ≥ epoch` holds. No monotonic clock needed; `DateTime.UtcNow` is adequate. (Theoretical NTP step-back mid-window is out of scope for v1.) Confirm acceptable.
- **Q3 (grace after MX):** prune at `epoch + mx` or `epoch + mx + small grace` (e.g. +500 ms) to let the last in-flight responses land + route through `_ui.Post` before the prune snapshot? Default: add a small grace (e.g. 500 ms–1 s) so a device that responded right at the MX edge isn't pruned by a routing-latency race. Tunable; documented as a constant.
- **Q4 (DiagCategories.Rescan):** add a NEW dotted constant (`"Adapter.Rescan"`) — default YES (Task 4; updates the pinned set + arch D8). Reuse-`AdapterSwitch` is the alternative; flagged intentional to reviewer.
- **Q5 (re-entrancy mechanism):** `[RelayCommand(CanExecute=...)]` driven by `IsRescanning` (auto-disables the menu item — cleanest) vs an `Interlocked` guard + manual `IsEnabled`. Default: CanExecute. Confirm.
- **Q6 (rescan during a switch / switch during a rescan):** the two guards are separate; the switch wins via the shared `_adapterCts` cancel (AC-5.3.10). A rescan fired while a switch holds `_switching` should still no-op safely (null/disposed scope guard). Confirm the null-scope guard covers the mid-teardown window.

## Dev Agent Record

### Agent Model Used
claude-opus-4-8[1m] (dev-story workflow)

### Debug Log References
- Core build `-warnaserror`: 0 Warning(s) / 0 Error(s).
- App build: 1 Warning (pre-existing `WMC1506` at MainWindow.xaml:162, shifted from :159 by the 1-line Rescan insert — NOT a new warning) / 0 Error(s).
- Full Core suite: 552 passed / 2 skipped / 0 failed (baseline 539/2; +13 new tests). Gate subset (DiagCategories*, CoreAppBoundary, AsyncDiscipline): 14 passed / 2 skipped.

### Completion Notes List
- **M-SEARCH re-trigger (Task 2):** added a thin `AdapterScope.SendMSearchAsync(TimeSpan mx)` pass-through to the scope-owned `_transport.SendMSearchAsync(mx, _adapterCts.Token)` — same token the switch cancels (AC-5.3.10). Defensive no-op before `StartAsync` (`!_transportStarted`). Transport NOT re-threaded into `DiscoveryService` (A23 preserved). Reuses the shipped `BuildMSearchPayload` unchanged.
- **`PruneNotSeenSince` (Task 1):** mirrors the shipped `Clear()` cascade — `ui.AssertOnUiThread()`, snapshot `_entries.Keys.ToArray()`, then for each entry with `LastSeenUtc < epochUtc` call the existing private `RemoveCore` (cancel+dispose `DeviceCts` + raise `DeviceRemoved`). Returns the count. `OnAlive`/`RefreshSsdpMetadata`/`RemoveCore` untouched — the prune rides the existing `LastSeenUtc` liveness model.
- **Epoch/grace timing + delay seam (Task 3):** `epochUtc = DateTime.UtcNow` stamped BEFORE the send; MX = `RescanMx` (5 s, startup parity); wait = `RescanMx + RescanGrace` (grace = 500 ms, Q3 default) so an edge-of-MX responder isn't pruned by routing latency. The wait is a `Func<TimeSpan,CancellationToken,Task> _rescanDelay` seam (real `Task.Delay` in prod; `SetRescanDelayForTest` swaps it so tests are instant — no real 5 s sleep). The wait is passed `scope.AdapterToken`, so a concurrent switch's `_adapterCts.Cancel()` aborts it.
- **Re-entrancy + switch-vs-rescan (AC-5.3.3 / AC-5.3.10):** rescan uses `[RelayCommand(CanExecute = nameof(CanRescan))]` with `CanRescan() => !IsRescanning`, and `_isRescanning` carries `[NotifyCanExecuteChangedFor(nameof(RescanCommand))]` so the generated setter raises CanExecute and the bound `MenuFlyoutItem` auto-disables (Q5 default). This guard is SEPARATE from the `_switching` guard — `SwitchAdapterAsync` is untouched and never waits on `IsRescanning`. The switch wins via the shared `_adapterCts` cancel aborting the linked MX wait → the rescan catches `OperationCanceledException`, emits a `Warning`, and does NOT prune the fresh post-switch registry. A rescan fired mid-switch no-ops on the null/`CurrentAdapterIPv4`-null scope check (Q6 confirmed covered).
- **AC-5.3.12 marshalling choice (deviation from the story's literal step (7)):** the story's task text suggested `var pruned = await _ui.PostAsync(() => _registry.PruneNotSeenSince(epochUtc))`. But the `DeferredUiDispatcher` test fake runs `PostAsync` INLINE (it cannot truly defer a value-returning round-trip), which would defeat the AC-5.3.12 guard ("registry unchanged until Drain"). So the prune + completion diagnostic + `IsRescanning=false` clear are all marshalled together inside a SINGLE `_ui.Post(() => { … })` (which `DeferredUiDispatcher` genuinely queues). The pruned count is read inside that posted block for the completion diagnostic. This satisfies AC-5.3.8 (UI-thread prune) AND AC-5.3.12 (nothing applies until `Drain()`), and is cleaner than the `PostAsync` form. Flagged for the reviewer as an intentional, AC-faithful adjustment.
- **`DiagCategories.Rescan = "Adapter.Rescan"` (Task 4) — INTENTIONAL pinned-set change (NOT drift):** added the dotted constant + its XML-doc, added `"Rescan"` to the `DiagCategoriesTests` `expectedNames` exact-set array, and added `Adapter.Rescan` to BOTH the architecture §Decision-8 constants block AND its Pattern-11 context table — all three together (the Story 5.1 `SsdpSearchObserved` precedent). Q4 default (new constant over reusing `AdapterSwitch`). Emits: Information "rescan started", Information "rescan pruned N non-responders", Warning "rescan abandoned — adapter switch in progress", Warning "rescan failed" (with ErrorText).
- **App menu (Task 5):** one `<MenuFlyoutItem Text="Rescan" Command="{x:Bind ViewModel.RescanCommand}" />` added to the EXISTING View `MenuFlyout`, immediately after Diagnostics (before the separator); `OnViewMenuOpening`, the separator, and `NetworkAdapterMenu` (still last) untouched. No new menu/Button, no launcher seam, no DI change, no new binding hazard (an `ICommand` x:Bind — no struct binding, no converter).
- **AC-5.3.14 manual UI smoke** is PENDING — the Project Lead's first-class gate on real Linn/OpenHome hardware. Story left at `review`.

### File List
UPDATED (production):
- `src/ohSpy.Core/Devices/IDeviceRegistry.cs` — added `int PruneNotSeenSince(DateTime epochUtc)` to the interface (+ XML-doc).
- `src/ohSpy.Core/Devices/DeviceRegistry.cs` — implemented `PruneNotSeenSince` (reuses `RemoveCore`).
- `src/ohSpy.Core/Discovery/AdapterScope.cs` — added the `SendMSearchAsync(TimeSpan mx)` pass-through.
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — added `_isRescanning` ([NotifyCanExecuteChangedFor]), `RescanMx`/`RescanGrace` constants, the `_rescanDelay` seam + `SetRescanDelayForTest`, the `RescanAsync` `[RelayCommand]` + `CanRescan`.
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` — added the `Rescan = "Adapter.Rescan"` constant.
- `src/ohSpy.App/MainWindow.xaml` — added the Rescan `MenuFlyoutItem` to the existing View flyout.

UPDATED (tests):
- `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` — added `"Rescan"` to the pinned `expectedNames`.
- `tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs` — 4 new `PruneNotSeenSince` tests.
- `tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` — 9 new rescan tests + a `NewHarnessEmptyAdapters` helper.
- `tests/ohSpy.Core.Tests/Fakes/FakeDeviceRegistry.cs` — inert `PruneNotSeenSince` (interface impl).

UPDATED (docs):
- `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` — §Decision-8: added `Adapter.Rescan` to the constants block + the context table.

### Review Findings

- [x] [Review][Patch] P2 — **FIXED 2026-06-05** (reviewer's preferred option). `AdapterScope.SendMSearchAsync` now wraps `_transport.SendMSearchAsync(mx, _adapterCts.Token)` in `try/catch (ObjectDisposedException) → throw new OperationCanceledException(...)`, so the disposed-CTS token read in the switch-wins race hits `RescanAsync`'s OCE "abandoned — switch in progress" path instead of logging "rescan failed". Regression test `AdapterScopeTests.SendMSearchAsync_AfterDispose_ThrowsOperationCanceled_NotObjectDisposed`; Core suite 553/2. Original finding: `AdapterScope.SendMSearchAsync` may surface `ObjectDisposedException` instead of `OperationCanceledException` in the narrow switch-wins race, causing "rescan failed" diagnostic instead of "rescan abandoned". The underlying issue: `_adapterCts.Dispose()` is called at line 186 of `AdapterScope.DisposeAsync` after the transport teardown budget expires; in the narrow window between `CancelAsync()` completing and `Dispose()` executing, a concurrent call to `return _transport.SendMSearchAsync(mx, _adapterCts.Token)` accesses `_adapterCts.Token` on a (concurrently-being-disposed) CTS. With .NET, `CancellationTokenSource.Token` throws `ObjectDisposedException` after `Dispose()` is called. The actual prune does not run in either case (ODE thrown before the prune block), so correctness is preserved — the only impact is the wrong diagnostic message.
- [x] [Review][Defer] W1 — TOCTOU "MX wait completes just as switch fires": posted prune action can interleave with the switch's posted `registry.Clear()` [`src/ohSpy.Core/ViewModels/ShellViewModel.cs:177-182`] — deferred, pre-existing architectural pattern (same as the W1 deferred from 5.2 switch review; both `Post` calls land on the same UI dispatcher queue; ordering is UI-framework-defined; all orderings are safe: the prune is idempotent on an already-cleared registry and does not re-add entries).

### Change Log
- 2026-06-05: Story 5.3 implemented (dev-story). Rescan = View→Rescan menu item → `ShellViewModel.RescanCommand`: stamps epoch, re-issues M-SEARCH via the new `AdapterScope.SendMSearchAsync` pass-through (scope-owned transport + token), waits MX+grace through a test-seamed delay, then prunes via the new `DeviceRegistry.PruneNotSeenSince` (byebye-identical cascade, returns count) — all marshalled via `_ui.Post` (Action H). Separate CanExecute re-entrancy guard; switch wins via the shared adapter-token cancel. New `DiagCategories.Rescan` (Adapter.Rescan) synced across the constant, the pinned-set test, and architecture D8. +13 Core tests; suite 552/2; Core -warnaserror 0/0; App only pre-existing WMC1506. Status ready-for-dev → review. AC-5.3.14 manual smoke pending (Project Lead, real hardware).
- 2026-06-05: Story 5.3 code review (Sonnet, fresh context). APPROVED-WITH-MINOR-FIXES. 1 patch (P2: ODE vs OCE mis-classification in switch-wins race), 1 defer (TOCTOU post ordering — safe), 0 blockers. All focus-area checks passed: prune correctness confirmed, no-teardown/NOTIFY survival confirmed, re-entrancy + switch-wins confirmed, marshalling guard confirmed correct deviation, pinned-set sync confirmed across all 3 locations. AC-5.3.14 manual smoke still PENDING.
