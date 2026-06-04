---
baseline_commit: 5e9d537a7daa0e9856aefd5de1bbabfc5df8fa85
---

# Story 5.2: Adapter Switch — `View → Network adapter` Menu + Atomic Rebind

Status: review

<!-- Re-sequenced (Epic 3 retro 2026-06-04): runs LAST in Epic 4 (after 4.3), not Epic 5. Key stays 5-2-… -->

## Story

As a Linn engineer,
I want `View → Network adapter` to show a radio list of every eligible IPv4 adapter (with the current one indicated) and let me select a different adapter to trigger an atomic rebind — tearing down the SSDP transport + callback host, clearing the registry, cancelling in-flight fetches, notifying every open popup, rebinding on the new adapter, and re-running the startup discovery — all within the FR-050 2 s budget,
so that I can move between development networks (lab Wi-Fi, wired test rig, dev laptop) without restarting the tool.

## Story Context — re-sequencing & prerequisite (READ FIRST)

This story is the **single most interconnected story in the project**: it tears down and rebuilds nearly every subsystem built in Epics 2–4. It was **re-sequenced from Epic 5 to be the final story of Epic 4** (Epic 3 retrospective) because its atomic-rebind sequence has a one-way **forward dependency** on Story 4.1 (`EventCallbackHost.DisposeAsync`/`StartAsync`) and Story 4.3 (the subscription popup teardown). Epic 4 itself has no reverse need for adapter switching, so 5.2 runs after 4.1/4.3. The key stays `5-2-…` to preserve architecture A23 / FR-050 / cross-references.

**Hard prerequisite — the A23 transport-factory refactor (Task 0).** The SSDP transport is still a **Story 2.1 DI singleton** (`AddSingleton<ISsdpTransport, SsdpTransport>()`). A singleton, once `DisposeAsync`'d, cannot rebind to a new adapter (`StartAsync` double-start guard + sockets/fields not reset). The atomic rebind **must dispose the old transport and construct a fresh one bound to the new adapter**, which means migrating the transport from a singleton to a **per-`AdapterScope` factory** ([Source: architecture.md#Amendment A23]). This is the load-bearing change and the first task.

## Acceptance Criteria

> ACs are the epic's L1842-1900 spec **reconciled against shipped code**. Where the epic prose names a type/seam that does not match reality, the reconciled name is used and flagged in Dev Notes. Author the `[Trait("ac", "AC-7.x")]` / `[Trait("ac", "AC-4.9")]` test traits per the epic's final AC.

**AC-5.2.1 — `View → Network adapter` menu populated dynamically.**
Given a new `View` menu in `MainWindow.xaml` containing a `Network adapter` submenu, when I open it, then it is populated via `INetworkAdapterEnumerator.Enumerate()` (Story 2.2) — every eligible adapter is listed as a `RadioMenuFlyoutItem` showing friendly name + IPv4 address; the currently-active adapter's item is checked; and if there is **zero or one** eligible adapter the menu still opens but contains a single **disabled** item ("No other adapters available"). (FR-048 half-A)
**NOTE (reconciliation):** `Enumerate()` returns `IReadOnlyList<NetworkAdapter>` (record: `Name`, `Description`, `IPv4`) — **not** `AdapterCandidate` (no such type exists). The menu is rebuilt each time it opens (adapters change at runtime).

**AC-5.2.2 — choosing a different adapter triggers the switch.**
Given I choose a non-active adapter, when the `RadioMenuFlyoutItem`'s command fires, then `ShellViewModel.SwitchAdapterAsync(NetworkAdapter newAdapter)` runs (FR-048 half-B + FR-050 trigger), the menu closes, and the UI shows a brief "Switching adapter…" transient state (NFR-UI3). Choosing the **already-active** adapter is a no-op (no rebind).

**AC-5.2.3 — the FR-050 atomic-rebind sequence (D7, 10 steps, 2 s budget).**
Given `SwitchAdapterAsync` executes, when the sequence runs, then the order is (epic L1856-1866, authoritative; mapped to real components in Dev Notes §3):
1. `_adapterCts.Cancel()` — cascades to every linked CTS (transport, callback host, every `RegistryEntry.DeviceCts`, every popup `_popupCts`, every 4.2 renew loop).
2. `await SsdpTransport.DisposeAsync()` — sockets + channel torn down.
3. `await EventCallbackHost.DisposeAsync()` — `TcpListener` stopped; in-flight callback connections drained (budgeted, idempotent — Story 4.1).
4. Dispose every `RegistryEntry.DeviceCts` (already cancelled via linkage in step 1; **dispose-only**) — performed by `DeviceRegistry.Clear()` (step 6).
5. Drain in-flight fetch tasks (await, 2 s budget).
6. `DeviceRegistry.Clear()` — raises `DeviceRemoved` per UUID (tree drops rows; popups flip to device-gone) and disposes each `DeviceCts`; **plus** `SsdpLogViewModel.Clear()` is called (Story 2.7 — the SSDP log clear is a separate call).
7. Dispose `_adapterCts` (owned by the old `AdapterScope`; performed by old-scope `DisposeAsync`).
8. Construct a new `AdapterScope` bound to the new adapter's IPv4.
9. New `SsdpTransport.StartAsync` + `EventCallbackHost.StartAsync` (re-enter the `RunStartAsync` start sequence; re-call `_subscriptionClient.SetAdapterContext(newScope.AdapterToken)`).
10. `SsdpTransport.SendMSearchAsync(5 s MX)` — re-runs the startup discovery sweep (FR-050 step (f) + FR-004 reuse).

**AC-5.2.4 — budget + timeout path.**
Given the sequence runs, then it completes within **2 s** (FR-050 + AC-7.1); and if step 5's drain exceeds the budget, **force-tear-down proceeds** and emits a `Warning DiagCategories.AdapterSwitchTimeout` ("we don't block UX on hung tasks", D7). An `Information DiagCategories.AdapterSwitch` is emitted at **start and end** of the switch with old + new adapter IPs in `DiagnosticContext`. (Both constants are pre-added — `DiagCategories.cs` L93-98 — **no new constant**.)

**AC-5.2.5 — open popups transition to FR-037 device-unreachable on switch.**
Given open popups during the switch, when `_adapterCts` cancels (+ `DeviceRegistry.Clear()` raises `DeviceRemoved`), then **every** open Properties window (2.9), invocation popup (3.2), and subscription popup (4.3) transitions to its FR-037 device-unreachable state (NFR-R3); no popup crashes; no popup blocks the switch (popup transitions are dispatched; the switch awaits its **own** work, not popups). (Per-popup mechanism pinned in Dev Notes §4.)

**AC-5.2.6 — tree + log clear and refill; diagnostics persist; check mark moves.**
Given the switch completes successfully, then the device tree is empty (cleared) and refills as M-SEARCH responses + unsolicited NOTIFYs arrive; the SSDP log is empty (cleared) and refills as new datagrams arrive; the diagnostic viewer (if open — Story 5.1, not yet built) would continue to show historical entries (the ring sink is **app-lifetime**, not adapter-scoped — no action needed here beyond NOT clearing it); and the `View → Network adapter` check mark moves to the new adapter.

**AC-5.2.7 — empty-network switch is graceful.**
Given a switch to an adapter with zero responding devices, when the MX elapses with no responses, then the app remains running with an empty tree (NFR-R5 across the switch path); unsolicited NOTIFYs can still populate the tree later.

**AC-5.2.8 — switch aborted mid-flight (app shutdown).**
Given the switch is aborted (e.g. `_appCts` cancels during the sequence), then in-progress steps abort cleanly, any partially-constructed new transport/callback host is disposed, and the app shuts down without errors.

**AC-5.2.9 — re-entrancy guard.**
Given a switch is in progress, when a second switch (or a switch fired during startup) is requested, then it is rejected/serialised (no two concurrent rebinds; no orphaned scope). The active `Network adapter` menu items are disabled during the switch.

**AC-5.2.10 (AC-7.1) — cancellation drill (automated).**
Given the test suite, an integration test simulates 10 devices on the old adapter with in-flight fetches; the switch is triggered; **every fetch throws `OperationCanceledException` within 100 ms; no fetch posts to a disposed VM**.

**AC-5.2.11 — popup-cascade test (automated).**
A popup-cascade test asserts that an open Properties + Invocation + Subscription popup VM all transition to their device-unreachable state on switch (Properties/Invocation via `DeviceRemoved`; Subscription via both `DeviceRemoved` **and** `handle.Lapsed(AdapterSwitch)`).

**AC-5.2.12 — manual UI smoke (UI-touching gate + the verification keystone).**
Given the live app on a multi-adapter machine, the operator runs the AC-5.2.1/.2/.5/.6 behaviours on real hardware (per `smoke-per-ui-story`), AND — because 5.2 is what finally makes the Linn-DS network reachable in-app — bundles the **deferred Story 4.3 event-stream smoke** and the **deferred 3.2 (5/6/7) + 3.3 (2/3/5) steps** (Dev Notes §7). This supersedes the retro Action-I `OHSPY_ADAPTER` dev override (the real adapter menu IS Action-I's proper form).

## Tasks / Subtasks

- [x] **Task 0 — A23 transport-factory refactor (the prerequisite; do this FIRST)** (AC: #3 steps 2/8/9)
  - [x] Replace `services.AddSingleton<ISsdpTransport, SsdpTransport>()` with a factory registration: `services.AddSingleton<Func<ISsdpTransport>>(sp => () => new SsdpTransport(sp.GetRequiredService<IDiagnosticEmitter>()))` (or an `ISsdpTransportFactory` wrapper — pick `Func<>` per Dev Notes §1; document the choice). `SsdpTransport` stays `internal sealed`.
  - [x] Make `AdapterScope` **own and construct** its transport via the factory (ctor takes `Func<ISsdpTransport>` instead of a shared `ISsdpTransport`), and **expose** the live transport (e.g. `public ISsdpTransport Transport { get; }` or `ChannelReader<SsdpDatagram> IncomingDatagrams`) so `DiscoveryService` reads the **scope-owned** instance, not a second DI-resolved one ([Source: architecture.md#Amendment A23 — Reconciliation with Story 2.4]).
  - [x] Reconcile `DiscoveryService` (currently `internal sealed`, ctor-injects the singleton `ISsdpTransport`, has an `Interlocked _started` **throw-on-second-start** guard, and captures `transport.IncomingDatagrams` in `ReadLoopAsync`). Decide + implement the per-adapter wiring so a second adapter gets a fresh read loop against the fresh transport. **Two viable shapes — pick one, document why** (Dev Notes §1): (a) make `DiscoveryService` per-adapter too (constructed by/with `AdapterScope`, owning its read loop), or (b) keep `DiscoveryService` a singleton but give it a `(Re)BindAsync(ISsdpTransport)` that resets `_started` + starts a fresh read loop on the new transport's reader. **Do NOT leave `DiscoveryService` reading a disposed transport's `IncomingDatagrams`.**
  - [x] Update `ShellViewModel` ctor/wiring: it currently injects `ISsdpTransport _transport` and passes it to `new AdapterScope(_adapterEnum, _transport, _diag, appToken)`. Re-thread to the factory/scope-owned shape.
  - [x] Fix the blast radius in tests: every test that `new`s an `AdapterScope` with a transport, every `FakeSsdpTransport`/`ChannelSsdpTransport` wiring, the `ShellViewModel` test ctor sites, and any `DiscoveryService` integration test. Keep the existing 2.1/2.2/2.4 ACs green (Core `-warnaserror` 0/0, full suite green).
  - [x] Guard: confirm `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` stay green; **no new `DiagCategories` constant** (the two adapter-switch constants are pre-added).

- [x] **Task 1 — `DeviceRegistry.Clear()` (does NOT exist yet — create it)** (AC: #3 step 6, #5, #10)
  - [x] Add `void Clear();` to `IDeviceRegistry` (public interface) and implement on `DeviceRegistry`. It must run on the UI thread (`ui.AssertOnUiThread()`), iterate every entry, **cancel + dispose each `RegistryEntry.DeviceCts`** and raise `DeviceRemoved(uuid)` per UUID, then empty the backing `ConcurrentDictionary`. Reuse the `RemoveCore` cascade so removal semantics match byebye exactly (the popups' FR-037 path).
  - [x] Decide raise-order vs clear-order so a `DeviceRemoved` handler that re-reads the registry sees a consistent state (snapshot UUIDs first, then remove + raise). Idempotent; safe on an empty registry.
  - [x] Unit tests: `Clear()` raises one `DeviceRemoved` per UUID; disposes each `DeviceCts` (assert tokens cancelled); empties `Count`; no-op on empty.

- [x] **Task 2 — `ShellViewModel.SwitchAdapterAsync(NetworkAdapter newAdapter)` + the 10-step sequence** (AC: #2, #3, #4, #8, #9)
  - [x] Add `public async Task SwitchAdapterAsync(NetworkAdapter newAdapter)`. **Re-entrancy guard** (`Interlocked.Exchange(ref _switching, 1)` — reject/serialise concurrent switches; also reject if startup is still running; re-enable in a `finally`). No-op if `newAdapter.IPv4` equals the current adapter IP.
  - [x] Emit `Information AdapterSwitch` at start (old IP + new IP in context). Set the "Switching adapter…" transient VM state (marshalled — Dev Notes §6).
  - [x] Tear down the OLD scope: `await _adapterScope.DisposeAsync()` already performs step 1 (`_adapterCts.Cancel()`), step 2 (`SsdpTransport.DisposeAsync()` within the 2 s budget), and step 7 (`_adapterCts.Dispose()`). **Extend `AdapterScope.DisposeAsync` (or orchestrate around it)** to also cover steps 3–6: callback-host dispose, in-flight fetch drain (2 s budget), `DeviceRegistry.Clear()`, `SsdpLogViewModel.Clear()`. Decide whether these live in `AdapterScope.DisposeAsync` (its XML-doc says the "full switch sequence … is Story 5.2 and references types that do not exist yet") or in `SwitchAdapterAsync` around the scope dispose. **Document the chosen split** (Dev Notes §5). The callback host + registry are owned by `ShellViewModel`, so the natural split is: `AdapterScope.DisposeAsync` = steps 1/2/7; `SwitchAdapterAsync` = steps 3/4/5/6 + the rebuild.
  - [x] On budget-exceeded during the drain (step 5): force-tear-down + `Warning AdapterSwitchTimeout` (reuse the `AdapterScope` `WaitAsync(budget)` + `catch (TimeoutException)` precedent).
  - [x] Rebuild (steps 8–10): construct `new AdapterScope(...)` on `newAdapter.IPv4` and re-enter the **same** start path `RunStartAsync` uses (reuse, do **not** duplicate): `scope.StartAsync()` → if `CurrentAdapterIPv4 is not null`: `_callbackHost.StartAsync` → `_subscriptionClient.SetAdapterContext(scope.AdapterToken)` → `_discovery` (re)bind → `SendMSearchAsync(5 s MX)`. **`AdapterScope.StartAsync` currently always binds adapter[0]** — it must accept the chosen `NetworkAdapter` (or its IPv4) so the switch binds the selected adapter, not the launch default. Update `StartAsync` (and the launch-default call in `RunStartAsync`) accordingly.
  - [x] Emit `Information AdapterSwitch` at end (new IP). Clear the transient VM state (marshalled).
  - [x] AC-5.2.8: ensure `_appCts` cancellation mid-switch aborts cleanly and disposes any partially-built scope/host (broad `catch (Exception) when (ex is not OutOfMemoryException)` → diagnostic, dispose partial, A26 fire-and-forget discipline).

- [x] **Task 3 — `View → Network adapter` menu (5.2 CREATES the View menu)** (AC: #1, #2, #6, #9)
  - [x] `MainWindow.xaml` has **no menu bar today** (only `TitleBar` + tree + log). Add a `View` menu (a `MenuBar` with a `MenuBarItem Title="View"`, or a `MenuFlyout` off a title-bar button — pick the WinUI-native shape; Dev Notes §3). Inside it add a `Network adapter` submenu (`MenuFlyoutSubItem`). **5.2 owns the View-menu creation** — Story 5.1 (Diagnostics) and 5.3 (Rescan) were assumed to build it first (epic L1788-1790) but they run **later** (still Epic 5); leave a comment that 5.1/5.3 will hang `Diagnostics` / `Rescan` items off this menu.
  - [x] Populate the submenu **on open** with one `RadioMenuFlyoutItem` per `NetworkAdapterEnumerator.Enumerate()` result (text = `Name` + `IPv4`; `GroupName` shared so they're mutually exclusive; `IsChecked` on the current adapter). Zero/one eligible → a single **disabled** `MenuFlyoutItem` "No other adapters available". Use the dynamic-populate-on-`Opening` pattern (the static x:Bind context-menu pattern from 2.8 won't work for a runtime-variable list).
  - [x] Wire each item to invoke `ShellViewModel.SwitchAdapterAsync(adapter)` (fire-and-forget per the launcher/command precedent; exceptions handled inside `SwitchAdapterAsync`). Disable the items while a switch is in progress (AC-5.2.9).
  - [x] Keep code-behind to view mechanics only (Pattern 13 — the menu build/populate is the documented exception, like the auto-follow + tree handlers). Expose any needed state (e.g. `IsSwitching`, current adapter IP) on `ShellViewModel`.

- [x] **Task 4 — Popup teardown verification (FR-037 across all three popup types)** (AC: #5, #11)
  - [x] No new popup code is expected — the three popups already react to `DeviceRemoved` (Properties 2.9, Invocation 3.2, Subscription 4.3) and the subscription popup additionally to `handle.Lapsed(AdapterSwitch)` (the 4.2 `_adapterToken.Register(... Lapse(AdapterSwitch))` cascade). **Verify** each path fires on switch and none blocks the 2 s sequence (the switch awaits its own work; popup transitions are dispatched). If any gap is found, fix minimally and document.
  - [x] Confirm the subscription re-context ordering: the OLD `_adapterCts.Cancel()` (step 1) fires `Lapsed(AdapterSwitch)` on every live sub via the already-registered adapter callback **before** the rebuild; the NEW `SetAdapterContext(newScope.AdapterToken)` (step 9) re-arms the client for future subs. Old popups stay lapsed (their handles are dead); new subscriptions use the new token. Document this in Dev Notes §4.

- [x] **Task 5 — Automated test suite (Core)** (AC: #3, #4, #10, #11)
  - [x] 10-step order + 2 s budget + timeout path: drive `SwitchAdapterAsync` with a fake transport factory + fake callback host + fake subscription client + the registry; assert the step order, that the drain budget caps at 2 s and emits `AdapterSwitchTimeout` on overrun, and that `DeviceRegistry.Clear()` raises removals.
  - [x] AC-7.1 cancellation drill: 10 devices, 10 in-flight fetches → switch → all 10 `OperationCanceledException` within 100 ms; no fetch posts to a disposed VM.
  - [x] Popup-cascade: open Properties + Invocation + Subscription VMs → switch → all reach device-unreachable (subscription via both `DeviceRemoved` and `Lapsed(AdapterSwitch)`).
  - [x] Re-entrancy: a second `SwitchAdapterAsync` while one is running is rejected/serialised; no orphaned scope.
  - [x] `SetAdapterContext` re-call asserted on rebuild (new token); `SsdpLogViewModel.Clear()` called.
  - [x] Marshalling guard: any VM-state mutation after an `await` (the transient, menu state) goes through `IUiDispatcher.Post` — use `DeferredUiDispatcher` (NOT `InlineUiDispatcher`, which masks missing marshalling — retro Action H).
  - [x] Trait every AC test `[Trait("ac", "AC-7.x")]` (or `[Trait("ac", "AC-4.9")]` for the callback-host drain-budget reuse from Story 4.1), per the epic's final AC.

- [ ] **Task 6 — Manual UI smoke (UI-touching gate + the deferred-verification keystone)** (AC: #12) — ⚠️ **PENDING / GATE OPEN** — CANNOT run in this headless dev environment (needs two real eligible adapters + the Linn-DS network). Story moves to `review` with this smoke gate explicitly OPEN (mirrors 3.2/3.3/4.3). This smoke is the VERIFICATION KEYSTONE bundling the deferred 4.3 event-stream + 3.2 (5/6/7) + 3.3 (2/3/5) steps; it SUPERSEDES retro Action-I (`OHSPY_ADAPTER`) — the real adapter menu IS Action-I's proper form. Concrete steps below.
  - [ ] On a multi-adapter machine: open `View → Network adapter`, confirm the radio list (friendly name + IP), current adapter checked, zero/one-adapter disabled-item behaviour; switch to the Linn-DS adapter; confirm < 2 s, "Switching adapter…" transient (no flicker), tree clears + refills, log clears + refills, check mark moves, open popups all flip to device-unreachable, no crash.
  - [ ] **Bundle the deferred Story 4.3 event-stream smoke** (now reachable): subscribe to an event-emitting Linn DS service (e.g. `Ds/Product`, `Volume`, `Playlist`) → live NOTIFY stream newest-first + latest-values overwrite-in-place; 2nd concurrent popup on another service → independent; trigger a lapse (power-off / leave) → reason banner; close mid-stream → clean UNSUBSCRIBE.
  - [ ] **Bundle the deferred 3.2 (5/6/7) + 3.3 (2/3/5) steps**: transport-error styling, device-gone banner, close-mid-invoke; numeric `NumberBox` (FR-103), off-step rejection, loading state.
  - [ ] Record results honestly in this story (pass / partial-defer with reason), per `smoke-per-ui-story` and the 3.2/3.3 honest-partial precedent. Note explicitly that 5.2 **supersedes** the retro Action-I `OHSPY_ADAPTER` override (no need to build the throwaway override separately).

## Dev Notes

### §0 — Reconciliation headlines (epic/D7 prose vs SHIPPED code)

| Epic/D7 prose | Shipped reality | Resolution |
|---|---|---|
| `SwitchAdapterAsync(AdapterCandidate)` | No `AdapterCandidate` type. `INetworkAdapterEnumerator.Enumerate()` → `IReadOnlyList<NetworkAdapter>` (record `Name`/`Description`/`IPv4`). | `SwitchAdapterAsync(NetworkAdapter newAdapter)`. |
| "`DeviceRegistry.Clear()` … (Story 2.7 already covers the log clear)" | **`DeviceRegistry.Clear()` does NOT exist** — only `OnAlive`/`OnByebye`/`Remove`/`RemoveCore`. `SsdpLogViewModel.Clear()` **does** exist (2.7, forward-compat). | **Task 1 creates `DeviceRegistry.Clear()`** (interface + impl). Log clear = call the existing `SsdpLogViewModel.Clear()`. |
| "`_adapterCts.Cancel()` … dispose `_adapterCts`" as if owned by ShellViewModel | `_adapterCts` lives **inside `AdapterScope`**; `AdapterScope.DisposeAsync()` already does Cancel (step 1) + transport dispose within the 2 s budget (step 2) + `_adapterCts.Dispose()` (step 7). | `SwitchAdapterAsync` orchestrates via `_adapterScope.DisposeAsync()` for steps 1/2/7 and adds steps 3/4/5/6 around it; builds a new scope for 8/9/10. |
| `SsdpTransport` per-adapter dispose+reconstruct | Registered as a **DI singleton** (2.1); `DiscoveryService` ctor-injects it + has a throw-on-second-start guard. | **Task 0 (A23):** `Func<ISsdpTransport>` factory; `AdapterScope` owns+exposes the transport; reconcile `DiscoveryService` to read the scope-owned instance. |
| `View` menu built by Story 5.1 first | `MainWindow.xaml` has **no menu bar** at all; 5.1 runs later (Epic 5). | **5.2 creates the `View` menu + `Network adapter` submenu**; 5.1/5.3 later hang their items off it. |
| `EventCallbackHost.DisposeAsync` budgeted | Confirmed: `IEventCallbackHost : IAsyncDisposable`, idempotent 2 s-drain `DisposeAsync` (Story 4.1). | Call it as step 3. |
| `DiagCategories.AdapterSwitch` / `AdapterSwitchTimeout` | Confirmed pre-added (`DiagCategories.cs` L93-98). | Use them; **no new constant** (keeps `DiagCategoriesUsageTests` exact-set green). |
| `SubscriptionClient.SetAdapterContext` | Confirmed (`ISubscriptionClient.SetAdapterContext(CancellationToken)`; default `_adapterToken = None`, `CanBeCanceled`-guarded registration). | Re-call with the new scope's token at step 9. |

### §1 — The A23 transport-factory design (the load-bearing refactor)

**Today** ([Source: ServiceRegistration.cs:73], [Source: SsdpTransport.cs], [Source: AdapterScope.cs], [Source: DiscoveryService.cs]):
- `services.AddSingleton<ISsdpTransport, SsdpTransport>()` — one instance for the process.
- `AdapterScope(enumerator, ISsdpTransport transport, diag, appToken)` consumes the singleton; `StartAsync` binds it and issues the initial M-SEARCH.
- `DiscoveryService(ISsdpTransport transport, DeviceRegistry, SsdpParser, IUiDispatcher)` is a **singleton**; `StartAsync` has `Interlocked _started` → **throws on a second call**; `ReadLoopAsync` reads `transport.IncomingDatagrams`.
- `ShellViewModel` injects `ISsdpTransport _transport` and hands it to `new AdapterScope(...)`.

**Target (A23):** register `Func<ISsdpTransport>` (transient construction) instead of the singleton; each `AdapterScope` constructs and **owns** its transport, disposing it on scope teardown; the switch builds a fresh scope → fresh transport ([Source: architecture.md#Amendment A23]). **Pick `Func<ISsdpTransport>` over a bespoke `ISsdpTransportFactory`** — it matches the project's existing Pattern-7 `Func<…>` factories (the 2.9/3.2/4.3 popup-VM factories) and needs no new interface. Document this choice in code.

**The non-obvious blast radius (bigger than just the transport):** `DiscoveryService` and `EventCallbackHost` and `SsdpLogViewModel` are all singletons that touch per-adapter state.
- `EventCallbackHost` is already lifecycle-owned by `ShellViewModel` (Start in `RunStartAsync`, Dispose in `DisposeAsync`), is `internal sealed` singleton, and its `StartAsync` throws on second call — but the host has a budgeted `DisposeAsync` and is re-`StartAsync`'d on the new IP. **Confirm a disposed `EventCallbackHost` can be re-started, or it too needs the factory treatment.** If `EventCallbackHost.StartAsync` cannot run twice after `DisposeAsync` (sockets/fields not reset, like the transport), apply the **same factory pattern** to `IEventCallbackHost` (and re-thread `ISubscriptionClient`, which injects the singleton host to read `CallbackBaseUrl` + subscribe to `NotifyReceived`). **This is an open question the implementer must resolve by reading `EventCallbackHost.cs` — flagged below.**
- `DiscoveryService`: it must read the **same** transport the active scope started, and must be able to start a **fresh** read loop per adapter. The throw-on-second-start guard forbids a naive re-`StartAsync`. **Two shapes (pick one, document):**
  - (a) **`DiscoveryService` per-adapter:** constructed by/with `AdapterScope` against the scope's transport reader; old one drained on scope dispose. Cleanest for the cancellation model but moves `DiscoveryService` out of the DI singleton (and `SsdpLogViewModel` subscribes to `IDiscoveryService.AnnouncementReceived` — see below).
  - (b) **`DiscoveryService` stays singleton + `RebindAsync(ISsdpTransport newTransport, adapterToken)`** that drains the old read loop, resets `_started`, and starts a fresh loop on the new reader. Keeps `SsdpLogViewModel`'s subscription stable.
- **`SsdpLogViewModel`** ([Source: SsdpLogViewModel.cs]) subscribes to `IDiscoveryService.AnnouncementReceived` in its ctor and is created **once** by `ShellViewModel` (app-lifetime). **Shape (b) keeps this subscription valid across switches** (the singleton `IDiscoveryService` persists; only its read loop rebinds) — a strong argument for (b). If you pick (a), `SsdpLogViewModel` must re-subscribe to the new `DiscoveryService` each switch (more churn). **Recommendation: shape (b)** unless reading the code reveals a blocker. Either way, `SsdpLogViewModel.Clear()` is called on switch to empty the log.

**Test blast radius (Task 0):** `ChannelSsdpTransport` (cap=256, the 2.4 integration fake) and `FakeSsdpTransport` (cap=1) wiring; `AdapterScope` test ctor sites; `ShellViewModel` test ctor sites; any `DiscoveryService` integration test. Keep 2.1/2.2/2.4 ACs green.

### §2 — The `_adapterCts` cancellation tree (D7)

`app → adapter → device → popup`, all linked CTS ([Source: architecture.md#Decision 7 L748-773]):
```
_appCts (App)                               // disposed at app shutdown
  └─ _adapterCts = linked(appToken)         // AdapterScope; disposed on switch (step 7)
       ├─ SsdpTransport(adapterToken)        // _runCts = linked(adapterToken)
       ├─ EventCallbackHost(adapterToken)
       └─ RegistryEntry.DeviceCts = linked(adapterToken)   // per UUID; disposed on byebye/clear
            └─ popup _popupCts = linked(DeviceToken)        // per popup; disposed on close
                 └─ 4.2 renew loop = linked(popupToken, deviceToken, adapterToken)
```
Step 1's `_adapterCts.Cancel()` therefore cascades through **everything** in one signal: transport + host run-CTS, every device fetch, every popup's in-flight work, every renew loop (→ `Lapsed(AdapterSwitch)` via the registered adapter callback). Steps 2–7 are **orderly teardown** of what the cancel signalled; step 4's per-device dispose is dispose-only because the linkage already cancelled them. **Cleanup-uses-level-above invariant** ([Source: architecture.md L790-816]): the 4.2 UNSUBSCRIBE-on-close uses the **adapter** token (not the cancelled popup token) — already built in `SubscriptionClient.CloseAsync`; on adapter switch the handles are lapsed so they send **no** UNSUBSCRIBE (correct — the device is unreachable).

### §3 — The 10-step sequence mapped to real components

| Step | Owner / call | Source |
|---|---|---|
| 1 `_adapterCts.Cancel()` | `AdapterScope.DisposeAsync` (already does `await _adapterCts.CancelAsync()`) | AdapterScope.cs:104 |
| 2 `SsdpTransport.DisposeAsync()` | `AdapterScope.DisposeAsync` (`WaitAsync(2 s budget)` + `TimeoutException` → `AdapterSwitchTimeout`) | AdapterScope.cs:118-123 |
| 3 `EventCallbackHost.DisposeAsync()` | `SwitchAdapterAsync` (host owned by ShellViewModel) — budgeted idempotent 2 s drain | IEventCallbackHost (IAsyncDisposable), Story 4.1 |
| 4 dispose every `RegistryEntry.DeviceCts` | folded into `DeviceRegistry.Clear()` (Task 1) — dispose-only (linkage already cancelled them) | RegistryEntry.cs:101-104 pattern |
| 5 drain in-flight fetches (2 s budget) | `SwitchAdapterAsync` — await the fetch tasks with `WaitAsync(budget)`; overrun → force-tear + `AdapterSwitchTimeout` | D7 L825/831 |
| 6 `DeviceRegistry.Clear()` (+ `SsdpLogViewModel.Clear()`) | `SwitchAdapterAsync` (UI thread — `Clear()` raises `DeviceRemoved` per UUID, tree + popups react) | Task 1; SsdpLogViewModel.cs:77 |
| 7 dispose `_adapterCts` | `AdapterScope.DisposeAsync` (`_adapterCts.Dispose()`) | AdapterScope.cs:131 |
| 8 new `AdapterScope` on new IPv4 | `SwitchAdapterAsync` — `new AdapterScope(...)`; `StartAsync` must bind the **chosen** adapter (today it always binds `adapters[0]`) | AdapterScope.cs:83 |
| 9 new transport+host StartAsync + `SetAdapterContext` | reuse `RunStartAsync`'s body (host.Start → SetAdapterContext → discovery (re)bind) | ShellViewModel.cs:67-89 |
| 10 `SendMSearchAsync(5 s MX)` | `AdapterScope.StartAsync` already issues `InitialMx = 5 s` | AdapterScope.cs:89 |

**Note on the arch's 8-step list (L818-829) vs the epic's 10-step (L1856-1866):** they are the **same** sequence; the epic splits out callback-host dispose (3) and the explicit restart (9) + M-SEARCH (10). **The epic's 10-step is authoritative for this story.**

**Reuse, don't duplicate (step 9):** `RunStartAsync(AdapterScope scope)` already encodes the exact start order (scope.StartAsync → host.StartAsync(IP, token) → `SetAdapterContext(token)` → discovery.StartAsync → [M-SEARCH inside scope.StartAsync]). Factor the post-`scope.StartAsync` block so both `RunStartAsync` (startup) and `SwitchAdapterAsync` (switch) call it. The only switch-specific deltas are: bind the **chosen** adapter (not `[0]`), and `DiscoveryService` (re)bind per the §1 shape.

**The `View` menu (Task 3):** WinUI 3 exposes `MenuBar`/`MenuBarItem` and `MenuFlyout`/`MenuFlyoutSubItem`/`RadioMenuFlyoutItem`. The shipped precedent is `MenuFlyout` via `ContextFlyout` on tree items with **static x:Bind** commands (2.8). For 5.2 the adapter list is **runtime-variable**, so build/populate the submenu in code on `Opening` (a documented Pattern-13 view-mechanics exception, like the auto-follow + tree handlers in `MainWindow.xaml.cs`). Keep business logic in `ShellViewModel` (expose `IsSwitching`, current adapter IP / a `CurrentAdapterChecked(adapter)` helper).

### §4 — Per-popup-type FR-037 teardown mechanism (verified)

- **Properties (2.9):** `_registry.DeviceRemoved += OnDeviceRemoved` → UUID match → `IsDeviceGone = true` banner. **No CTS link** — purely registry-event driven. `DeviceRegistry.Clear()` raising `DeviceRemoved` per UUID is its sole trigger. ([Source: PropertiesViewModel.cs:134-151])
- **Invocation (3.2):** `_popupCts = linked(DeviceToken)` (cancels in-flight invoke → OCE swallowed) **and** `DeviceRemoved` → `IsDeviceGone`. Step 1's cascade aborts an in-flight invoke; `Clear()`'s `DeviceRemoved` flips the banner. ([Source: InvocationPopupViewModel.cs:110-118])
- **Subscription (4.3):** `_popupCts = linked(DeviceToken)` **and** `DeviceRemoved → DeviceGone` **and** `handle.Lapsed(AdapterSwitch)`. The 4.2 renew loop registers `_adapterToken.Register(() => Lapse(AdapterSwitch))`, so step 1's `_adapterCts.Cancel()` raises `Lapsed(AdapterSwitch)` on every live sub **off-thread**, marshalled to `Status = Lapsed` / "device unreachable after adapter switch". Convergent + idempotent with the `DeviceRemoved → DeviceGone` path. ([Source: SubscriptionPopupViewModel.cs:215-245], [Source: SubscriptionClient.cs:373-382])
- **Re-context ordering (the 4.2 "5.2 re-context" note):** the OLD adapter token's cancel (step 1) lapses all existing subs **before** rebuild; `SetAdapterContext(newScope.AdapterToken)` (step 9) re-arms the singleton client for **future** subs. The `_adapterToken = None` default + `CanBeCanceled` guard means a not-yet-set context never registers. Old popups stay lapsed (dead handles); new subscriptions bind the new token. ([Source: ISubscriptionClient.cs:20-28], [Source: SubscriptionClient.cs:116])
- **Non-blocking:** every popup transition is dispatched (`_ui.Post`) or registry-event-driven on the UI thread; the switch awaits **its own** work (transport/host dispose, fetch drain), never a popup. No popup blocks the 2 s sequence.

### §5 — `SwitchAdapterAsync` shape + re-entrancy + ownership

`ShellViewModel` owns `_adapterScope` (since 2.5 / A26), `_callbackHost`, `_subscriptionClient`, `_discovery`, `_deviceTree`, `_ssdpLog` ([Source: ShellViewModel.cs]). `_appCts` stays in `App` (A26); its token reaches the scope. `SwitchAdapterAsync` lives in `ShellViewModel`.

- **Re-entrancy guard:** `Interlocked.Exchange(ref _switching, 1)`; also reject while `_runTask` startup is incomplete (or await it first). Re-enable in `finally`. The menu disables its items while `IsSwitching` (AC-5.2.9). No two scopes ever live at once.
- **Split decision (document the chosen one):** the natural split given ownership is `AdapterScope.DisposeAsync` = steps **1/2/7** (it owns `_adapterCts` + the transport); `SwitchAdapterAsync` = steps **3** (host, ShellViewModel-owned) / **4+6** (`DeviceRegistry.Clear()`, ShellViewModel-reachable via the registry) / **5** (fetch drain) / **8/9/10** (rebuild). `AdapterScope`'s own XML-doc says the full sequence "plugs in" at 5.2 — extending it vs orchestrating around it is the implementer's call; **keep the per-adapter, disposable-once bits in `AdapterScope` and the app-lifetime singletons' rebind in `ShellViewModel`.**
- **A26 fire-and-forget discipline:** the menu invokes `SwitchAdapterAsync` fire-and-forget; wrap the body in `try/catch (Exception ex) when (ex is not OutOfMemoryException)` → `Warning AdapterSwitch` + dispose any partial scope/host. Don't leak an unobserved task fault.

### §6 — Marshalling (retro Action H / `winui-no-synccontext-marshal-vm`)

`SwitchAdapterAsync` is invoked from the UI (menu) so its pre-`await` body runs on the UI thread, but **continuations after the `await`s resume off-thread** (WinUI 3 has no `SynchronizationContext`). Anything the switch mutates **directly** on VM state post-await — the "Switching adapter…" transient flag, the menu check-mark/`IsSwitching` flip, any direct tree/log poke — **must** go through `IUiDispatcher.Post`. Most clears are safe automatically: `DeviceRegistry.Clear()` raises `DeviceRemoved` on the UI thread (the registry asserts UI-thread + callers marshal), and `DeviceTreeViewModel`/popups react on the UI thread; `SsdpLogViewModel.Clear()` must be called on the UI thread. **Call out and marshal any direct post-await VM mutation.** Guard with `DeferredUiDispatcher` tests (Action H) — `InlineUiDispatcher` would mask a missing `Post`. Less acute than 4.3 (no off-thread event handlers here) but the transient + menu-state flips are the live hazard.

### §7 — Verification posture (5.2 is the verification keystone)

- **Automated (Core):** the rebind sequence is largely testable headless — fake transport-factory + fake callback host + fake subscription client + the real registry → assert the 10-step order, the 2 s budget + `AdapterSwitchTimeout` path, `DeviceRegistry.Clear()` raising removals, re-`SetAdapterContext`, the AC-7.1 cancellation drill, the popup-cascade (VM-level), re-entrancy, and the marshalling guard.
- **App-only (manual smoke):** the `View` menu, `RadioMenuFlyoutItem` population, and the **live** rebind (real sockets, real devices) are App-only (`CoreAppBoundaryTests` forbids `Core.Tests → App`; there's no App test project).
- **Smoke bundles the deferred debt (Task 6):** after switching to the Linn adapter, run (a) 5.2's own ACs, (b) the **deferred 4.3 event-stream smoke** (live NOTIFY + latest-values + lapsed banner — was un-runnable without an event-emitting device; the Sky IGD emits nothing useful), and (c) the **deferred 3.2 (5/6/7) + 3.3 (2/3/5) steps**. **5.2 supersedes retro Action-I** — the real adapter menu IS Action-I's proper form, so the throwaway `OHSPY_ADAPTER` override need not be built separately. ([Source: epic-3-retro-2026-06-04.md Action I/H])

### Project Structure Notes

- Core/App split holds: `ISsdpTransport` factory, `AdapterScope`, `DeviceRegistry.Clear()`, `DiscoveryService` rebind, `SwitchAdapterAsync`, popup VMs, `SubscriptionClient` are all **Core**. The `View` menu XAML + populate-on-open code-behind + the menu→`SwitchAdapterAsync` wiring are **App**. `CoreAppBoundaryTests` must stay green (the menu cannot leak into Core; the rebind orchestration stays headless-testable).
- No new `DiagCategories` constant (the two adapter-switch constants are pre-added → `DiagCategoriesUsageTests` exact-set guard unchanged). Verify `AsyncDisciplineTests`/VSTHRD (no `.Result`/`.Wait`; fire-and-forget wrapped) and the chaos guard stay green.
- Baseline before this story: full suite **487 passed / 2 skipped** (post-4.3). Expect a meaningful Core test delta from Task 0's refactor churn + the new switch tests.

### Open questions for the implementer

1. **Does `EventCallbackHost.StartAsync` run twice after `DisposeAsync`?** Read `src/ohSpy.Core/Events/EventCallbackHost.cs`. If its sockets/fields are not reset on dispose (like `SsdpTransport`), apply the **same A23 factory pattern** to `IEventCallbackHost` and re-thread `ISubscriptionClient` (which injects the singleton host for `CallbackBaseUrl` + `NotifyReceived`). If it can re-start cleanly, keep it a singleton lifecycle-owned by `ShellViewModel`. **This decision sizes Task 0.**
2. **`DiscoveryService` shape (a) per-adapter vs (b) singleton-with-`RebindAsync`** — §1 recommends (b) to keep `SsdpLogViewModel`'s `AnnouncementReceived` subscription stable; confirm against the code and pick one.
3. **In-flight fetch drain (step 5):** which task handle does the switch await? The eager-description fetches are launched by `EagerDescriptionDispatcher` and bounded by each `DeviceToken` (cancelled in step 1). Confirm whether a drainable handle exists or whether the 2 s `WaitAsync` is a best-effort settle window (the cancellation drill AC-7.1 only requires OCE within 100 ms, not a hard join).
4. **Menu shape:** `MenuBar` row under the `TitleBar` vs a title-bar command button with a `MenuFlyout` — pick the cleaner WinUI-native fit for the two-pane shell and leave the 5.1/5.3 extension comment.

### References

- [Source: epics.md#Story 5.2 (L1832-1900)] — the 10-step sequence + ACs (authoritative).
- [Source: architecture.md#Decision 7 (L734-877)] — cancellation tree + atomic-switch sequence + AC-7.1..7.5 + cleanup-level-above invariant.
- [Source: architecture.md#Amendment A23 (L2858-2874)] — singleton→`Func<ISsdpTransport>` factory; `AdapterScope` owns+exposes transport; `DiscoveryService` reconciliation.
- [Source: architecture.md#Amendment A26 (L2878-2895)] — App-lifetime disposable + fire-and-forget discipline; `_appCts` stays in App.
- [Source: src/ohSpy.Core/ViewModels/ShellViewModel.cs] — `RunStartAsync` start sequence to reuse; owns scope/host/client/discovery/tree/log.
- [Source: src/ohSpy.Core/Discovery/AdapterScope.cs] — owns `_adapterCts` + transport; `DisposeAsync` steps 1/2/7; `StartAsync` binds `adapters[0]` (must take the chosen adapter).
- [Source: src/ohSpy.Core/Discovery/SsdpTransport.cs] — `internal sealed`, idempotent `DisposeAsync`; A23 factory target.
- [Source: src/ohSpy.Core/Discovery/DiscoveryService.cs] — singleton, ctor-injects transport, `Interlocked _started` throw-on-second-start, reads `IncomingDatagrams`.
- [Source: src/ohSpy.Core/Devices/DeviceRegistry.cs] / [IDeviceRegistry.cs] — **no `Clear()` yet**; `RemoveCore` cancel+dispose+raise pattern to reuse.
- [Source: src/ohSpy.Core/Devices/RegistryEntry.cs] — `DeviceCts = linked(adapterToken)`; public `DeviceToken` snapshot.
- [Source: src/ohSpy.Core/ViewModels/SsdpLogViewModel.cs] — `Clear()` exists (2.7); subscribes to `IDiscoveryService.AnnouncementReceived`.
- [Source: src/ohSpy.Core/Events/IEventCallbackHost.cs] — `IAsyncDisposable`, budgeted `DisposeAsync`; `StartAsync(IP, ct)` throws on second call.
- [Source: src/ohSpy.Core/Events/ISubscriptionClient.cs] / [SubscriptionClient.cs] — `SetAdapterContext`; `_adapterToken.Register(() => Lapse(AdapterSwitch))`; UNSUBSCRIBE over adapter token.
- [Source: src/ohSpy.Core/ViewModels/{PropertiesViewModel,InvocationPopupViewModel,SubscriptionPopupViewModel}.cs] — per-popup FR-037 mechanisms.
- [Source: src/ohSpy.Core/Models/NetworkAdapter.cs] / [Discovery/INetworkAdapterEnumerator.cs] — `Enumerate()` → `IReadOnlyList<NetworkAdapter>`.
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs] — DI graph (A23 edit point: L73).
- [Source: src/ohSpy.App/MainWindow.xaml(.cs)] — no menu bar today; Pattern-13 view-mechanics precedent.
- [Source: src/ohSpy.App/App.xaml.cs] — `_appCts` ownership; `OnLaunched` launcher `ShellWindow` injection; `ShutdownAsync`.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs (L93-98)] — `AdapterSwitch` / `AdapterSwitchTimeout` pre-added.
- [Source: epic-3-retro-2026-06-04.md] — Action H (DeferredUiDispatcher), Action I (`OHSPY_ADAPTER`, superseded by this story), deferred 3.2/3.3/4.3 verification.
- Project memories: `winui-no-synccontext-marshal-vm`, `smoke-per-ui-story`, `winui-treeview-datacontext-null`.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Amelia, BMAD dev agent) — dev-story workflow, 2026-06-04.

### Debug Log References

- Baseline (pre-story): Core `-warnaserror` 0/0; full suite **487 passed / 2 skipped**.
- After A23 refactor + `DeviceRegistry.Clear()` (Tasks 0+1), before new tests: 487 passed / 2 skipped (no regression).
- Final: Core `-warnaserror` 0/0; full suite **503 passed / 2 skipped** (+16 new tests). Switch + cascade suites green on 3 consecutive runs (no flakiness). App + full solution build: 1 warning (the pre-existing WMC1506, see note), 0 errors. Chaos 1; `CoreAppBoundaryTests` / `AsyncDisciplineTests` / `DiagCategoriesUsageTests` green; **no new `DiagCategories` constant**.

### Completion Notes List

**Task 0 design decisions (pinned — the load-bearing A23 refactor):**
1. **Transport factory shape = `Func<ISsdpTransport>`** (chosen over a bespoke `ISsdpTransportFactory`) — matches the project's existing Pattern-7 `Func<>` popup factories; no new interface. DI: `AddSingleton<Func<ISsdpTransport>>(sp => () => new SsdpTransport(diag))`. `AdapterScope` now ctor-takes the factory, **constructs + owns** its transport, and **exposes** the live reader via `public ChannelReader<SsdpDatagram> IncomingDatagrams`. `SsdpTransport` stays `internal sealed`.
2. **`EventCallbackHost` CANNOT re-start after dispose** (confirmed by reading `EventCallbackHost.cs`: `Interlocked _started` throw-on-second-start guard + `_listener`/`_slots`/`_runCts`/`_callbackBaseUrl`/`_started`/`_disposed` are never reset). → **Same factory treatment: `Func<IEventCallbackHost>`.** `ShellViewModel` now OWNS the live host instance (a mutable field), constructs a fresh one on startup and on each switch, starts it, and drains it. Re-threaded `ISubscriptionClient`: it no longer ctor-injects the host — added `ISubscriptionClient.SetCallbackHost(IEventCallbackHost)`; `ShellViewModel` hands the live host to it on startup AND each switch; the client detaches `NotifyReceived` from the old (disposed) host and re-attaches to the new one + re-points `CallbackBaseUrl`. (This also fixes a latent leak: the old client subscribed once-ever via an `_subscribedToHost` Interlocked guard that would never re-fire on a new host.)
3. **`DiscoveryService` shape = (b) keep singleton + rebindable read loop** (recommended in §1) so `SsdpLogViewModel`'s app-lifetime `AnnouncementReceived` subscription stays valid across switches. Concretely: `DiscoveryService` **no longer ctor-injects `ISsdpTransport`** (it can't — the transport is per-adapter now); its ctor is `(DeviceRegistry, SsdpParser, IUiDispatcher)`. `StartAsync(ChannelReader<SsdpDatagram> reader, adapterToken, ct)` takes the **scope-owned** reader; new `RebindAsync(reader, adapterToken, ct)` drains the old loop (the old transport's `DisposeAsync` already completed its channel, so the old `ReadAllAsync` has ended), resets the `_started` guard, and starts a fresh loop on the new reader. The dead Story-5.3 `RescanAsync` stub (which referenced the injected transport) was removed.

**Other engineering-judgment decisions / deviations:**
- **Ownership split (Dev Notes §5, chosen):** `AdapterScope.DisposeAsync` = steps **1/2/7** (it owns `_adapterCts` + the transport); `ShellViewModel.SwitchAdapterAsync` orchestrates steps **3** (host dispose) / **4+6** (`DeviceRegistry.Clear()` + `SsdpLogViewModel.Clear()`) / **5** (fetch settle) / **8/9/10** (rebuild). I did NOT extend `AdapterScope.DisposeAsync` to cover 3–6 (those touch ShellViewModel-owned singletons), matching the natural ownership boundary.
- **`AdapterScope.StartAsync(NetworkAdapter? preferred = null)`** — `null` ⇒ launch default (first eligible, FR-048); a supplied adapter ⇒ bind the **chosen** one (the switch). Previously always bound `adapters[0]`. The switch passes the operator-chosen record directly (no re-enumeration).
- **Reuse, not duplicate (step 9):** factored the post-`scope.StartAsync` start block into `ShellViewModel.StartBoundServicesAsync(scope)`, called by BOTH `RunStartAsync` (startup) and `SwitchAdapterAsync` (switch). On startup it `_discovery.StartAsync`; on switch it `_discovery.RebindAsync` (a `_discoveryStarted` flag selects).
- **`AdapterScope` now disposes its owned transport even when unstarted** (the factory always constructs one; leaving it undisposed would leak it). This is a genuine A23 seam change — updated `AdapterScopeTests.DisposeAsync_TransportNeverStarted_*` to assert dispose-count 1 (was 0). `SsdpTransport.DisposeAsync` on an unbound instance is idempotent + leak-free (null sockets).
- **Step 5 in-flight fetch drain = best-effort settle window, NOT a hard join (open-Q #3 resolved).** There is no aggregate join handle — eager fetches are fire-and-forget off `EntryNeedsFetch`, each bounded only by its `DeviceCts` (linked to the adapter token, cancelled by step 1). So `DrainInFlightFetchesAsync` is a brief multi-`Task.Yield()` settle (well under the 2 s budget — a fixed 2 s block on every switch would itself blow FR-050). AC-7.1 only requires OCE within 100 ms, which the token linkage already guarantees (asserted in the cancellation-drill test).
- **`AdapterSwitchTimeout` is owned by step 2** (transport teardown via `AdapterScope.DisposeAsync`'s `WaitAsync(budget)` + `catch (TimeoutException)`), the genuine hung-teardown path. The step-5 settle never emits it (it never blocks on a hung task). A new test drives a slow transport `DisposeAsync` → `AdapterSwitchTimeout` warning + the switch still rebinds (force-tear-down). Two test seams added to `ShellViewModel` for fast tests: `SetFetchDrainBudgetForTest`, `SetAdapterTeardownBudgetForTest`.
- **Marshalling (retro Action H):** `IsSwitching = true` is set pre-await (UI thread, safe direct). The `IsSwitching = false` clear is post-await → off-thread → marshalled via `_ui.Post`. `DeviceRegistry.Clear()` + `SsdpLogViewModel.Clear()` run inside a `_ui.PostAsync(...)` (UI thread). Guarded by a `DeferredUiDispatcher` test (`SwitchAdapterAsync_TransientClear_IsMarshalled`) that proves the clear does NOT apply without a `Drain()` — `InlineUiDispatcher` would mask it.
- **Re-entrancy guard:** a single `_switching` Interlocked flag rejects a concurrent switch AND a switch fired during startup (held across startup, released in `RunStartAsync.finally`). The same-adapter no-op short-circuits before taking the guard. The menu disables its items while `IsSwitching` (AC-5.2.9).
- **Menu shape (open-Q #4):** `MenuFlyoutSubItem` has **no `Opening` event** (confirmed: `WMC0011`), so the `MenuBar`/`MenuBarItem` shape can't populate-on-open. Pivoted to a title-bar-adjacent **`Button` + `MenuFlyout`** (the §3 / open-Q #4 alternative): `MenuFlyout.Opening` rebuilds the `Network adapter` `MenuFlyoutSubItem` each open with one `RadioMenuFlyoutItem` per `EnumerateAdapters()` (shared `GroupName`, current `IsChecked`, disabled while `IsSwitching`); 0/1 eligible ⇒ a single disabled "No other adapters available". Each item fires `SwitchAdapterAsync(adapter)` fire-and-forget. Code-behind is view-mechanics-only (Pattern 13 documented exception); business logic (enumeration, current-adapter check, the rebind) is on `ShellViewModel` (`EnumerateAdapters()`, `IsCurrentAdapter(adapter)`, `IsSwitching`, `CurrentAdapterIPv4`). Left a comment that 5.1/5.3 hang Diagnostics/Rescan off this same View flyout.

**Confirmations (per the task brief):**
- **10-step order + 2 s budget + re-entrancy + re-`SetAdapterContext`/`SetCallbackHost` + `DeviceRegistry.Clear`** are all asserted by `ShellViewModelTests` (10-step order via the `SwitchRecorder` cross-instance ordering; budget/timeout; reject concurrent + during-startup; new adapter token + new host on rebuild; registry+log emptied). Popup cascade (Properties + Invocation via `DeviceRemoved`; Subscription via both `DeviceRemoved` AND `Lapsed(AdapterSwitch)`) asserted by `AdapterSwitchPopupCascadeTests`. `DeviceRegistry.Clear()` semantics by `DeviceRegistryTests` (one `DeviceRemoved`/UUID, each `DeviceCts` cancelled, empty + idempotent).
- **Subscription re-context ordering (Dev Notes §4):** the OLD adapter-token cancel (step 1, in `AdapterScope.DisposeAsync`) fires `Lapsed(AdapterSwitch)` on every live sub via the already-registered adapter callback BEFORE the rebuild; the NEW `SetAdapterContext(newScope.AdapterToken)` (step 9, in `StartBoundServicesAsync`) re-arms the client for FUTURE subs. Old popups stay lapsed (dead handles). Verified at the VM level in the cascade test.

**Existing tests updated for the A23 seam (and why):**
- `AdapterScopeTests` — `Scope(...)` + the budget-test ctor now pass `() => transport` (the factory); `DisposeAsync_TransportNeverStarted_*` renamed + asserts dispose-count 1 (new scope-owns-transport semantics). The named/positional `switchBudget`/`appToken` ctor args were re-ordered to match the new signature.
- `DiscoveryServiceTests` — `new DiscoveryService(transport, ...)` → `(registry, parser, ui)`; every `StartAsync(...)` now passes `transport.IncomingDatagrams` as the reader. (The AC-2.4.6 second-pass already built a fresh service per transport — that pattern is exactly the rebind model, so it stayed.)
- `SubscriptionClientTests` — `NewHarness` drops the host ctor arg and calls `client.SetCallbackHost(host)` (the ShellViewModel precedent).
- Fakes: `FakeSubscriptionClient` + `HandReturningClient` (in SubscriptionPopupViewModelTests) + `StubDiscoveryService` gained the new interface members; `FakeDeviceRegistry` + the real `DeviceRegistry` gained `Clear()`.
- New test fakes: `SwitchRecorder` + `RecordingSsdpTransport` + `RecordingCallbackHost` + `StubAdapterEnumerator` (ordered lifecycle log + tagged factories), `GatedUiDispatcher` (parks a switch mid-flight for the re-entrancy test).

**No new `DiagCategories` constant** — only the pre-added `AdapterSwitch` / `AdapterSwitchTimeout` are used. `DiagCategoriesUsageTests` unchanged + green.

**Pre-existing WMC1506 note:** the single benign `WMC1506` warning on the fallback-template `Label` OneWay binding moved from `MainWindow.xaml:141` → `:156` because the View menu added 15 lines above it. It is the SAME warning on the SAME element — no new warning introduced (verified line 156 is the fallback-template `Text="{x:Bind Label, Mode=OneWay}"`).

**⚠️ Manual UI smoke (Task 6) — NOT run (headless; needs two real adapters + the Linn-DS network). GATE OPEN — story → `review`.** It is the VERIFICATION KEYSTONE bundling the deferred 4.3 event-stream smoke + 3.2 (5/6/7) + 3.3 (2/3/5), and it SUPERSEDES retro Action-I (the real adapter menu is Action-I's proper form). Concrete steps to run on real hardware:
1. **5.2 own ACs:** open `View → Network adapter` → confirm the radio list (friendly name + IPv4), current adapter checked, mutually-exclusive radios; verify the 0/1-eligible disabled "No other adapters available" item (e.g. disable all but one adapter). Switch to the Linn-DS adapter → confirm < 2 s, the "Switching adapter…" transient with no flicker, tree clears + refills as M-SEARCH/NOTIFYs arrive, log clears + refills, the check mark moves, menu items disabled mid-switch, no crash.
2. **Deferred 4.3 event-stream (now reachable):** subscribe to an event-emitting Linn DS service (`Ds/Product`, `Volume`, `Playlist`) → live NOTIFY stream newest-first + latest-values overwrite-in-place; open a 2nd concurrent popup on another service → independent; trigger a lapse (power-off / leave the network) → reason banner; close mid-stream → clean UNSUBSCRIBE.
3. **Deferred 3.2 (5/6/7) + 3.3 (2/3/5):** transport-error styling, device-gone banner, close-mid-invoke; numeric `NumberBox` (FR-103), off-step rejection, loading state.
4. **Cross-switch popup FR-037:** with Properties + Invocation + Subscription popups open on the old adapter, switch → confirm all three flip to device-unreachable, none crashes, none blocks the < 2 s switch.
Record results honestly (pass / partial-defer with reason) per `smoke-per-ui-story`.

### File List

**Core (modified):**
- `src/ohSpy.Core/Discovery/AdapterScope.cs` — A23: ctor takes `Func<ISsdpTransport>`, constructs+owns the transport, exposes `IncomingDatagrams`; `StartAsync(NetworkAdapter? preferred)` binds the chosen adapter; disposes the owned transport even when unstarted.
- `src/ohSpy.Core/Discovery/DiscoveryService.cs` — ctor drops the transport; `StartAsync(reader, …)` + new `RebindAsync(reader, …)` (shape (b)); removed the dead `RescanAsync` stub.
- `src/ohSpy.Core/Discovery/IDiscoveryService.cs` — `StartAsync(ChannelReader<SsdpDatagram>, …)` + `RebindAsync(…)`.
- `src/ohSpy.Core/Devices/DeviceRegistry.cs` — added `Clear()` (snapshot UUIDs, RemoveCore cascade per UUID, empty).
- `src/ohSpy.Core/Devices/IDeviceRegistry.cs` — added `void Clear();`.
- `src/ohSpy.Core/Events/SubscriptionClient.cs` — drop host from ctor; added `SetCallbackHost(host)` (re-subscribe NotifyReceived + re-point CallbackBaseUrl on each host).
- `src/ohSpy.Core/Events/ISubscriptionClient.cs` — added `SetCallbackHost(IEventCallbackHost)`.
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — owns the live callback host; `Func<>` factories; `SwitchAdapterAsync` 10-step sequence; `StartBoundServicesAsync` reuse; `IsSwitching`/`EnumerateAdapters`/`IsCurrentAdapter`/`CurrentAdapterIPv4`; re-entrancy guard + marshalling; test seams.

**App (modified):**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — `Func<ISsdpTransport>` + `Func<IEventCallbackHost>` factory registrations; comments for DiscoveryService/SubscriptionClient re-thread.
- `src/ohSpy.App/MainWindow.xaml` — added the View `Button`+`MenuFlyout` with the `Network adapter` `MenuFlyoutSubItem` (new grid row); shifted the two-pane grid to row 2.
- `src/ohSpy.App/MainWindow.xaml.cs` — `OnViewMenuOpening` populate-on-open handler (RadioMenuFlyoutItem per adapter / disabled single item; fire-and-forget `SwitchAdapterAsync`).

**Tests (added):**
- `tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` — 11 tests: 10-step order, diagnostics start/end, registry+log clear, re-SetAdapterContext/SetCallbackHost, same-adapter no-op, re-entrancy (concurrent + during-startup), AC-7.1 cancellation drill, empty-network graceful, transport-teardown timeout, marshalling guard.
- `tests/ohSpy.Core.Tests/ViewModels/AdapterSwitchPopupCascadeTests.cs` — 2 tests: DeviceRemoved → Properties+Invocation device-gone; AdapterSwitch lapse + DeviceRemoved → Subscription unreachable.
- `tests/ohSpy.Core.Tests/Fakes/SwitchRecorder.cs` — `SwitchRecorder` + `RecordingSsdpTransport` + `RecordingCallbackHost` + `StubAdapterEnumerator`.
- `tests/ohSpy.Core.Tests/Fakes/GatedUiDispatcher.cs` — parks a switch at the registry/log clear for the re-entrancy test.

**Tests (modified):**
- `tests/ohSpy.Core.Tests/Discovery/AdapterScopeTests.cs` — factory ctor; unstarted-transport-dispose semantics.
- `tests/ohSpy.Core.Tests/Discovery/DiscoveryServiceTests.cs` — ctor + `StartAsync(reader, …)`.
- `tests/ohSpy.Core.Tests/Events/SubscriptionClientTests.cs` — `SetCallbackHost` wiring.
- `tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs` — 3 `Clear()` tests.
- `tests/ohSpy.Core.Tests/Fakes/FakeSubscriptionClient.cs` — `SetCallbackHost`.
- `tests/ohSpy.Core.Tests/Fakes/FakeDeviceRegistry.cs` — `Clear()`.
- `tests/ohSpy.Core.Tests/Fakes/StubDiscoveryService.cs` — `StartAsync(reader,…)` + `RebindAsync`.
- `tests/ohSpy.Core.Tests/ViewModels/SubscriptionPopupViewModelTests.cs` — `HandReturningClient.SetCallbackHost`.

**Story tracking (modified):**
- `_bmad-output/implementation-artifacts/5-2-adapter-switch-view-network-adapter-menu-atomic-rebind.md` — frontmatter `baseline_commit`, Status, Tasks 0-5 checked, Dev Agent Record.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `5-2 → review`.

## Review Findings

Code review conducted 2026-06-04 by claude-sonnet-4-6 (BMAD code-review skill). Build/test: Core `-warnaserror` 0/0; App 0 errors; 503 passed / 2 skipped across 3 consecutive runs; no flakes. Verdict: **APPROVED-WITH-MINOR-FIXES** (1 decision-needed, 2 patches, 4 deferred; manual smoke gate remains open).

### Decision-Needed

- [x] [Review][Decision] ✅ RESOLVED 2026-06-04 — **Project Lead chose "harden the failed state" (no auto-rebind).** `SwitchAdapterAsync`'s catch now disposes the partial new scope **unconditionally** (dropped the `!ReferenceEquals` guard) and **nulls `_adapterScope`**, so a failed rebuild leaves an unambiguous "no active adapter — select one to retry" state (the menu + re-entrancy guard release in `finally`; the next switch rebuilds cleanly from the null scope). The Warning now reads "adapter switch failed — no active adapter; select an adapter to retry". Regression test added: `SwitchAdapterAsync_NewScopeStartFails_NoActiveAdapter_PartialDisposed_Retryable` (arms the switch's new transport #1 to throw on bind → asserts `CurrentAdapterIPv4` null + partial transport disposed + Warning emitted, then a retry to a good adapter recovers; switch suite green 3×). — original: Partial-scope + no-transport limbo after a mid-rebuild failure.

### Patches

- [x] [Review][Patch] ✅ APPLIED 2026-06-04 — P1: removed the dead `budget` parameter from `DrainInFlightFetchesAsync`, the `_fetchDrainBudget` field, the `DefaultFetchDrainBudget` const, and the `SetFetchDrainBudgetForTest` seam (+ its test call site). The settle is now plainly "a fixed handful of yields"; the doc/comments no longer imply an enforced bound (the genuine timeout is the step-2 transport-dispose `AdapterSwitchTimeout`).
- [x] [Review][Patch] ✅ APPLIED 2026-06-04 — P2: marked `_callbackHost` `volatile` with a single-writer threading comment (SetCallbackHost only called from startup + the `_switching`-serialised switch; the off-thread `CallbackHost` read can race a switch write → volatile, stale-but-valid worst case, and the popup's adapter-linked token cancels the doomed subscribe anyway).

### Deferred

- [x] [Review][Defer] W1 — Narrow use-after-clear race: a `ui.Post(registry.OnAlive)` from the old read loop landing after `registry.Clear()` re-adds a stale device with cancelled CTS [`src/ohSpy.Core/ViewModels/ShellViewModel.cs:262-273`] — deferred, requires the aggregate fetch-task join handle from open-Q #3 to fix properly; window is extremely narrow (old channel writer completes before settle yields run).
- [x] [Review][Defer] W2 — `SwitchAdapterAsync_DuringStartup_IsRejected` does not exercise the guarded-startup live path — only tests post-startup steady state [`tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs:233-249`] — deferred, guard is structurally sound; fixing test requires a seam into `RunStartAsync`.
- [x] [Review][Defer] W3 — `AdapterScope.IncomingDatagrams` XML doc overpromises "Throws if accessed before StartAsync" but no guard exists [`src/ohSpy.Core/Discovery/AdapterScope.cs:49-54`] — deferred, doc-only fix, no runtime risk.
- [x] [Review][Defer] W4 — `Clear_RaisesDeviceRemovedPerUuid_DisposesEachCts_EmptiesRegistry` asserts cancellation but not CTS disposal (test name slightly overclaims) [`tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs`] — deferred, minor assertion gap; disposal follows the same `RemoveCore` path as `OnByebye` which is already tested.

### Post-review verification (Opus main session, 2026-06-04)

D1 hardened + P1/P2 applied; W1–W4 left deferred (logged in `deferred-work.md`). Verified: Core `-warnaserror` 0/0; full suite **504 passed / 2 skipped / 0 failed** (+1 D1 regression test over the reviewed 503); the 12-test `ShellViewModel` switch suite green on 3 consecutive runs (no introduced flake). **Automated side is clean-APPROVED.**

⚠️ **Story holds at `review` — manual UI smoke (Task 6) is OPEN and is the VERIFICATION KEYSTONE.** Per Project Lead decision (2026-06-04), Story 5.2 is **committed at `review`**; the Project Lead will run the smoke on real hardware (a multi-adapter machine + the Linn-DS network), which bundles the deferred **4.3 event-stream** smoke + the **3.2 (5/6/7)** + **3.3 (2/3/5)** steps (run on the Linn adapter after a switch) — and supersedes the retro Action-I override. **Mark 5.2 (and the bundled deferred items) `done` only after that smoke passes.** This is the last gate before Epic 4 closes.
