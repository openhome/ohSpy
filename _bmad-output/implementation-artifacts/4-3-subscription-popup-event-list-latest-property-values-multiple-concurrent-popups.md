---
baseline_commit: 1063891c488aea368889768a44e34591933f0465
---

# Story 4.3: Subscription Popup — Event List, Latest Property Values, Multiple Concurrent Popups

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want right-click → Subscribe on a service to open a popup that shows incoming NOTIFY events newest-first in a virtualised list, with a "Latest property values" summary anchored above showing each evented property's most-recent value, supporting multiple concurrent popups across different services, and surviving device disappearance,
so that I can watch a streamer's transport state, queue, volume, etc. update live as I drive the device — and have multiple services under observation simultaneously without one slow service blocking another.

## Context & boundaries (read first)

This is the **last GENA story** (Story 4.1 callback host + Story 4.2 subscription client are `done`/committed). It is the **first and only CONSUMER** of the Story 4.2 seam: it holds a `SubscriptionHandle`, renders its parsed `EventNotification`s, and closes it on popup close. It is **UI-touching** (a real WinUI popup with a live event stream) → **manual UI smoke is a first-class gate** (Task 12), not deferred to epic close.

It is **the single most marshalling-sensitive ViewModel in the project so far** — see Dev Note **§0 (the #1 hazard)**. Read that before writing any handler.

**Out of scope (already shipped — do NOT rebuild):**
- SUBSCRIBE / RENEW / UNSUBSCRIBE / SID-routing / `<e:propertyset>` parse / auto-renew / lapse detection → **Story 4.2 `SubscriptionClient`** owns all of it. 4.3 just consumes the handle.
- The callback HTTP host → **Story 4.1**. 4.3 never touches it.
- Window ownership (FR-046 z-order / minimise-with-parent / close-with-parent) → **Story 2.9 `WindowOwnershipManager`** (`Activate()` → `Adopt()`); reuse verbatim.
- The newest-first bounded FIFO list → **Story 2.7 `BoundedObservableCollection<T>`**; reuse verbatim.
- The launcher seam pattern + DI factory → **Story 3.2 `IInvocationPopupLauncher` / `InvocationPopupLauncher`**; mirror verbatim into a new `ISubscriptionPopupLauncher`.
- The device-gone banner → **Story 2.9 `PropertiesViewModel` / 3.2 `InvocationPopupViewModel`** pattern.

## Acceptance Criteria

**AC-4.3.1 (Core VM shape — FR-032/FR-033 + D6).**
`src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs` exists (Core, sealed, `ObservableObject`, `IDisposable`). Its constructor takes `ServiceDescription service`, `RegistryEntry parentEntry`, `ISubscriptionClient subscriptionClient`, `IUiDispatcher ui`, `IDiagnosticEmitter diag`, `IDeviceRegistry registry`. It exposes:
- `BoundedObservableCollection<EventNotification> Events` constructed with capacity **5,000** (FR-033 + D6).
- An overwrite-in-place, bound, observable "latest values" map (`LatestPropertyValues`) — newest value per evented property name, anchored summary (FR-033). Engineering judgment on the concrete type is documented in Dev Notes §4 (the chosen shape is an `ObservableCollection<LatestPropertyValue>` of `(Name, Value)` rows with last-write-wins update-in-place, because no `ObservableDictionary` exists in the project and `BoundedObservableCollection` is single-value-stream-only).
- `[ObservableProperty] SubscriptionStatus Status` — enum `Subscribing | Subscribed | Lapsed | DeviceGone | FailedToSubscribe`.
- `[ObservableProperty] string? StatusMessage` — human-readable detail (e.g. "device-granted TIMEOUT: 300 s", "renewal refused", subscribe-failure text).
- A read-only `Title` (the service identifier header — reuse the `:service:` tail logic).
- **No `Visibility`/`Brush`/WinUI types in Core** (Pattern 2; `CoreAppBoundaryTests` enforces). The App window code-behind projects `Status` → banner/indicator visibility.

**AC-4.3.2 (subscribe-async flow — the launch + the off-thread continuation).**
The VM exposes a public `async Task InitializeAsync()` (mirror `InvocationPopupViewModel.InitializeAsync`), kicked off fire-and-forget by the App launcher **after** `window.Activate()` + `Adopt(...)`. It:
1. Sets `Status = Subscribing` synchronously in the constructor (ctor runs on the UI thread).
2. Calls `await _subscriptionClient.SubscribeAsync(_service, _parentEntry, _popupCts.Token).ConfigureAwait(false)`.
3. On the **post-await continuation (off-thread)**, marshals all observable mutations via `_ui.Post`: attach the handle's `NotificationReceived` + `Lapsed` handlers, set `Status = Subscribed`, set `StatusMessage` to the granted-timeout detail if available.
4. The handler attach happens on the continuation; the **4.2 replay buffer flush** then fires the handlers synchronously for any pre-attach events/lapse — and **that flush runs on whatever thread calls `add`** (here the off-thread continuation), so the handlers MUST marshal too (see §0).

**AC-4.3.3 (NOTIFY → newest-first list + latest-values, both marshalled).**
On each `handle.NotificationReceived(EventNotification n)` (raised on the **4.2 NOTIFY-worker thread — NOT the UI thread**): the handler marshals via `_ui.Post` to (a) `Events.PrependNewest(n)` (FR-033 newest-first) and (b) update `LatestPropertyValues` last-write-wins for each `kvp in n.Properties` (overwrite existing name, append new). No XML parse here — `n.Properties` is already the parsed dictionary (Story 4.2 owns the `<e:propertyset>` parse).

**AC-4.3.4 (lapse → banner, marshalled, reason-specific text).**
On `handle.Lapsed(SubscriptionLapseReason reason)` (raised off-thread): marshal via `_ui.Post` → set `Status` and `StatusMessage` per reason:
- `DeviceGone` → `Status = DeviceGone`, message "device no longer reachable" (FR-037 shape — same as Properties window).
- `AdapterSwitch` → `Status = Lapsed`, message "device unreachable after adapter switch" (Story 5.2 fires this).
- `RenewRefused` / `RenewTransportError` → `Status = Lapsed`, message "subscription lapsed (renewal refused / failed)".
The popup stays open and closeable; already-shown events/values remain.

**AC-4.3.5 (failed subscribe — FR-035).**
If `SubscribeAsync` throws (`UpnpTransportException` / `UpnpTimeoutException` / `UpnpProtocolException`), the continuation marshals `Status = FailedToSubscribe` + `StatusMessage` = human-readable error. No handle is attached (none was returned). `OperationCanceledException` (popup closed during subscribe) is swallowed: no status flip, no diagnostic (mirror `InvokeAsync`/`InitializeAsync` convention). The popup is closeable, and **close performs NO UNSUBSCRIBE** (no handle, no SID). A broad NFR-R3 defensive catch handles any other exception as a failed-subscribe (no diagnostic).

**AC-4.3.6 (FIFO eviction — FR-033 cap + AC-6.1).**
At capacity (5,000), the 5,001st event evicts the oldest tail event. `BoundedObservableCollection.PrependNewest` emits `Add(0)` then `Remove(5000)` — never `Reset`. (This is already the collection's contract — covered by a VM-level test that drives 5,001 notifications and asserts `Events.Count == 5000` and the newest is at index 0.)

**AC-4.3.7 (multiple concurrent independent popups — FR-036 + FR-104).**
Each popup owns its own `SubscriptionHandle`, its own `_popupCts` (linked to `parentEntry.DeviceToken`), its own `Events` list, its own `LatestPropertyValues`. Opening N popups across different services runs them independently; closing one does not affect the others. The `WindowOwnershipManager` already tracks multiple owned children per parent (`Dictionary<parentHwnd, List<childHwnd>>` + `GetChildrenOf`) — no change needed. FR-104 non-serial NOTIFY processing is already isolated by 4.2's per-subscription worker; the VM just renders.

**AC-4.3.8 (Story 2.8 Subscribe-stub removal).**
`ServiceNodeViewModel.SubscribeCommand`'s stub body (the `Feature.NotImplemented` Warning) is replaced with the real launch: `_services.SubscriptionPopupLauncher.Open(_service, _parentEntry)`. The "Subscribe (coming in Epic 4)" menu label is replaced with "Subscribe" (epic L1722; `MainWindow.xaml`). The `ServiceNodeViewModelTests.SubscribeCommand_Stub_WarnsNotImplemented_AC285` test is retargeted to assert the launcher receives the `(service, entry)` (via a new `FakeSubscriptionPopupLauncher`), not a warning. **`DiagCategories.FeatureNotImplemented` decision: LEAVE THE CONSTANT IN PLACE** (do NOT delete) — see Dev Notes §6; the pinned-set guard test stays unchanged.

**AC-4.3.9 (popup-close path — FR-034 + D7).**
The window's `Closed` handler calls `vm.Dispose()`, which (idempotent, `Interlocked` guard): `_popupCts.Cancel()`; unsubscribes the handle's events + `_registry.DeviceRemoved`; calls `handle?.CloseAsync()` (fire-and-forget — Story 4.2 runs the UNSUBSCRIBE-with-adapter-token discipline; on a lapsed/device-gone/failed-subscribe handle it sends no UNSUBSCRIBE); `_popupCts.Dispose()` in a `finally`. The window closes immediately — the async UNSUBSCRIBE (≤5 s budget, Story 4.2) does not block the close visually.

**AC-4.3.10 (App window — FR-032 + Pattern 13).**
`src/ohSpy.App/Views/SubscriptionPopupWindow.xaml` + `.xaml.cs` exist. Layout: service identifier header; "Latest property values" summary panel anchored at top (always visible regardless of event-list scroll); the scrolling, **item-virtualised** (`ItemsRepeater` + `ScrollViewer`, NFR-P1) event list below; a status indicator driven by `Status`. Code-behind is constructor-only (Pattern 13) + the `Closed` → `Dispose()` handler + the `Status`/bool → `Visibility` projections (App-side, mirror `InvocationPopupWindow.xaml.cs`).

**AC-4.3.11 (App launcher seam + DI).**
A new Core `ISubscriptionPopupLauncher` seam (mirror `IInvocationPopupLauncher`) with `void Open(ServiceDescription service, RegistryEntry parentEntry)`; App-side `SubscriptionPopupLauncher` (mirror `InvocationPopupLauncher`): Pattern-7 `SubscriptionPopupViewModelFactory` delegate, `new SubscriptionPopupWindow(vm)`, `window.Activate()` THEN `Adopt(window, ShellWindow)`, THEN `_ = vm.InitializeAsync()`. Registered in `ServiceRegistration` (dual-reg concrete + interface, `ShellWindow` set in `App.OnLaunched`); added to `NodeServices` as the **8th member** so `ServiceNodeViewModel` reaches it.

**AC-4.3.12 (marshalling regression guard — retro Action H).**
A `DeferredUiDispatcher`-driven test proves each handle-event handler marshals through `_ui.Post`: raise `NotificationReceived` / `Lapsed` while the dispatcher is in deferred mode and assert the observable state (`Events`, `LatestPropertyValues`, `Status`) is **NOT** mutated until `Drain()` is called. Also a test for `InitializeAsync`'s post-await `Status = Subscribed` flip going through `Post`. (`InlineUiDispatcher` masks the missing marshalling — the exact 3.2 crash class.)

**AC-4.3.13 (integration drill — FR-036 / FR-104 through the VM).**
A Core integration test using a fake `ISubscriptionClient` / a controllable `SubscriptionHandle`-emitting source drives: events flow newest-first; summary updates overwrite-in-place; 5 concurrent VMs each render independently; a slow/delayed notification on VM A does not block VM B's notification delivery (observed through the VMs — the 4.2 discipline drill, now at the VM layer); the close-cascade (`Dispose` → `CloseAsync`) is exercised.

## Tasks / Subtasks

- [x] **Task 0 — Threading/marshalling design pass (do this FIRST; AC-4.3.2/.3/.4/.12).** Before writing any handler, internalise Dev Note §0: `SubscriptionHandle.NotificationReceived` and `Lapsed` fire on the **4.2 NOTIFY-worker thread (a thread-pool thread)**, and the **replay-buffer flush fires on the thread that calls `add`** (the off-thread post-await continuation). So EVERY observable mutation in EVERY handler — list append, latest-values update, `Status`/`StatusMessage` flip — goes through `_ui.Post`. Confirm `_diag` is thread-safe (it is — emit may stay off-thread). Map each handler to its marshalled body now.
- [x] **Task 1 — `SubscriptionStatus` enum + `LatestPropertyValue` row type (AC-4.3.1).** Add `SubscriptionStatus { Subscribing, Subscribed, Lapsed, DeviceGone, FailedToSubscribe }` (Core/ViewModels or Core/Events — co-locate with the VM). Add a small `LatestPropertyValue(string Name, string Value)` observable row (or `partial` `ObservableObject` with a settable `Value`) for the summary panel.
- [x] **Task 2 — `SubscriptionPopupViewModel` skeleton + ctor (AC-4.3.1/.2).** Sealed `partial ObservableObject, IDisposable`. Fields: `_service`, `_parentEntry`, `_subscriptionClient`, `_ui`, `_diag`, `_registry`, `_uuid`, `_popupCts` (linked to `parentEntry.DeviceToken`), `_handle` (nullable), `_disposed` (Interlocked). Ctor: compute `Title`; build `Events = new BoundedObservableCollection<EventNotification>(5000)`; `LatestPropertyValues = []`; `Status = Subscribing`; subscribe `_registry.DeviceRemoved += OnDeviceRemoved`. (Mirror `InvocationPopupViewModel` ctor.)
- [x] **Task 3 — `InitializeAsync` subscribe flow (AC-4.3.2/.5).** `await SubscribeAsync(...).ConfigureAwait(false)`; on success, marshal: store `_handle`, attach `OnNotification` + `OnLapsed`, `Status = Subscribed`, set granted-timeout `StatusMessage`. Catch `OperationCanceledException` (swallow); catch the typed `UpnpException`s + a broad NFR-R3 catch → marshal `Status = FailedToSubscribe` + message. **Attach handlers BEFORE returning from the marshalled block** so the 4.2 replay flush (fired during `add`) is delivered — and remember that flush is itself off-thread → the handlers' own `_ui.Post` covers it.
- [x] **Task 4 — NOTIFY handler (AC-4.3.3/.6).** `OnNotification(EventNotification n)` → `_ui.Post(() => { Events.PrependNewest(n); MergeLatest(n.Properties); })`. `MergeLatest` = last-write-wins over the `LatestPropertyValues` rows.
- [x] **Task 5 — Lapse handler (AC-4.3.4).** `OnLapsed(SubscriptionLapseReason r)` → `_ui.Post(() => { Status = Map(r); StatusMessage = Text(r); })`. Reason→status/text per AC-4.3.4.
- [x] **Task 6 — DeviceRemoved banner + Dispose/close cascade (AC-4.3.9).** `OnDeviceRemoved(Guid uuid)` UUID-match (FR-037, the 2.9 pattern) → idempotent `Status = DeviceGone` (note: the handle also raises `Lapsed(DeviceGone)` via 4.2; both paths converge on `Status = DeviceGone` — idempotent). `Dispose()`: Interlocked once-guard → `_popupCts.Cancel()` → detach handle events + `_registry.DeviceRemoved -= OnDeviceRemoved` → `_ = _handle?.CloseAsync()` (fire-and-forget) → `_popupCts.Dispose()` in `finally`.
- [x] **Task 7 — `ISubscriptionPopupLauncher` Core seam (AC-4.3.11).** `void Open(ServiceDescription service, RegistryEntry parentEntry)` (mirror `IInvocationPopupLauncher`). Add to `NodeServices` record as the 8th member.
- [x] **Task 8 — Remove the 2.8 Subscribe stub (AC-4.3.8).** Replace `ServiceNodeViewModel.Subscribe()` body with `_services.SubscriptionPopupLauncher.Open(_service, _parentEntry)`. Update the XML-doc comment (drop "coming in Epic 4 / Story 4.1 removes this stub"). Replace the `MainWindow.xaml` "Subscribe (coming in Epic 4)" menu label with "Subscribe". **Do NOT delete `DiagCategories.FeatureNotImplemented`** (§6).
- [x] **Task 9 — App `SubscriptionPopupWindow.xaml` + `.xaml.cs` (AC-4.3.10).** Header + anchored summary panel (top, outside the event ScrollViewer) + virtualised `ItemsRepeater` event list + status indicator. Code-behind: constructor-only + `Closed` → `Dispose()` + `Status`/bool → `Visibility` projections (mirror `InvocationPopupWindow.xaml.cs`).
- [x] **Task 10 — App `SubscriptionPopupLauncher` + DI wiring (AC-4.3.11).** `SubscriptionPopupViewModelFactory` delegate + launcher (Activate→Adopt→`InitializeAsync`); `ServiceRegistration` block (factory news up the VM with `ISubscriptionClient`+`IUiDispatcher`+`IDiagnosticEmitter`+`IDeviceRegistry`; dual-reg; **before the `NodeServices` line** so it auto-resolves into the bundle); `App.OnLaunched` sets `ShellWindow`.
- [x] **Task 11 — Core tests (AC-4.3.2..4.3.7, .12, .13).** Add `FakeSubscriptionPopupLauncher` (mirror `FakeInvocationPopupLauncher`) + a `FakeSubscriptionClient` returning a controllable handle source (a real `SubscriptionHandle` newed with a no-op close delegate + `internal` `RaiseNotification`/`RaiseLapsed` — these are `internal`, and `ohSpy.Core.Tests` has `InternalsVisibleTo`, so the test can drive them directly; confirm). Tests: subscribe→Subscribed; NOTIFY→newest-first+latest-merge (DeferredUiDispatcher guard); lapse→banner (each reason); failed-subscribe→FailedToSubscribe (no UNSUBSCRIBE); 5,001-event eviction (`Add(0)`+`Remove(5000)`, count 5000); Dispose→CloseAsync; 5-concurrent-independent + non-serial drill. Retarget the `ServiceNodeViewModelTests` Subscribe test + update the `MakeNodeServices` helper for the 8th `NodeServices` member; update the OTHER `NodeServices` construction sites (`DeviceNodeViewModelTests`, `DeviceTreeViewModelTests`, `ActionNodeViewModelTests`) — same blast-radius pattern as the 3.2 7th-member add.
- [ ] **Task 12 — MANUAL UI SMOKE (first-class gate; AC-4.3.3/.4/.7/.9; retro Action E + memory `smoke-per-ui-story`).** ⚠️ Depends on **retro Action I** (the `OHSPY_ADAPTER` dev override) being in place to reach the **Linn-DS network** (the Sky IGD likely emits no useful events). Steps: (1) right-click a DS event-emitting service (e.g. `Ds/Product`, a `Volume`/`Playlist` service) → Subscribe → popup opens, `Status = Subscribed`; (2) drive the device → events stream newest-first, latest-values panel updates overwrite-in-place, panel stays anchored while scrolling the list; (3) open a SECOND popup on another service → both update independently; (4) trigger a lapse (power-off / leave the network) → banner with the right text; (5) close mid-stream → clean UNSUBSCRIBE, no crash. **If Action I is not yet in place, record an honest partial** (like the 3.2/3.3 deferrals): the Core VM logic is unit-tested as the compensating control; the App-side stream/projection/anchored-panel is the only unverified surface. State the dependency explicitly in the smoke note.
  - **SMOKE GATE: OPEN / DEFERRED (cannot run in this environment).** This dev session is headless (no display) and has no event-emitting Linn DS reachable — the Action-I `OHSPY_ADAPTER` dev override is NOT yet built, so the app cannot be pointed at the Linn-DS network (the Sky IGD likely emits no useful events). Per the 3.2/3.3 precedent, the story moves to `review` with the smoke gate explicitly OPEN; the Core `SubscriptionPopupViewModel` logic is the compensating control (23 new unit tests, incl. the `DeferredUiDispatcher` marshalling guards). The only UNVERIFIED surface is App-side: the live NOTIFY stream rendering, the `Status`→Visibility/banner projections, the anchored "Latest property values" panel, and `ItemsRepeater` virtualisation. **Concrete steps to run once Action I lands:** (1) right-click a DS event-emitting service (`Ds/Product`, a `Volume`/`Playlist`/`Playlist`-type service) → Subscribe → popup opens, status line shows `Subscribed · SID …`, no banner; (2) drive the device (volume/transport/playlist changes) → events stream newest-first in the bottom list, the top "Latest property values" panel updates overwrite-in-place (no reshuffle), and the panel stays anchored while you scroll the event list; (3) open a SECOND popup on another service → both update independently, closing one leaves the other live; (4) trigger a lapse (power the device off / leave the network) → caution banner appears with reason-specific text ("device no longer reachable" / "device unreachable after adapter switch" / "subscription lapsed (renewal refused / failed)"), already-shown events stay; (5) close a popup mid-stream → window closes immediately, clean best-effort UNSUBSCRIBE fires (4.2 owns it), no crash.
- [x] **Task 13 — Gates.** Core build `-warnaserror` 0/0; full suite green (baseline 462 passed / 2 skipped); chaos 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` + `DiagCategoriesTests` green; App build (1 pre-existing benign `WMC1506` on `MainWindow.xaml:141` only — no new warnings). No new `DiagCategories` constant.

## Dev Notes

### §0 — ⚠️ THE #1 HAZARD: off-thread `SubscriptionHandle` events → marshal EVERYTHING via `_ui.Post`

This is the 3.2 `RPC_E_WRONGTHREAD` crash class **at its sharpest**, and the headline risk of this story. Read `src/ohSpy.Core/Events/SubscriptionHandle.cs` (verified):

- `NotificationReceived` and `Lapsed` are **raw `Action<…>` events** — the handle's XML-doc says verbatim: *"raised (raw, off the host's/worker's thread)… 4.3 marshals onto bound state."* They fire from `SubscriptionClient`'s **per-subscription NOTIFY-worker thread** (a thread-pool thread, Story 4.2), **never** the UI thread.
- **The replay buffer (AC-4.2.7) flushes pre-attach events INSIDE the `add` accessor** (`SubscriptionHandle.cs` L43-73 for notifications, L85-112 for lapse): when the VM attaches its handler, any buffered notification/lapse is delivered **synchronously on the thread that called `add`** — which, for 4.3, is the **off-thread post-await continuation** of `InitializeAsync` (because the VM attaches handlers AFTER the `SubscribeAsync` await, off the UI thread). So even the replay flush is off-thread.

**Consequence — the load-bearing rule:** every observable-state mutation a handler performs — `Events.PrependNewest`, the `LatestPropertyValues` merge, `Status` / `StatusMessage` flips — **MUST be wrapped in `_ui.Post(...)`** (Decision 1 / `IUiDispatcher`). WinUI 3 installs **no `SynchronizationContext`**, so a direct off-thread mutation raises `PropertyChanged` / mutates a bound `ObservableCollection` off-thread → the window pokes `UIElement.Visibility`/items off-thread → `COMException 0x8001010E` (`RPC_E_WRONGTHREAD`) → unhandled → **process crash**. This is exactly memory `winui-no-synccontext-marshal-vm` and the Epic 3 retro headline.

`BoundedObservableCollection<T>` documents this itself: *"UI-thread-owned. Not thread-safe. Callers must marshal mutations via `IUiDispatcher`."*

**`_diag` is thread-safe** (the `DiagnosticEmitter` pushes through its own ring/file pipeline) → diagnostic emits may stay off-thread; only the **VM-state apply** is marshalled. Pre-await ctor mutations (e.g. `Status = Subscribing`) are safe direct because the ctor runs on the UI thread.

**4.2's post-close patch (2026-06-04):** `SubscriptionClient.CloseAsync` was patched to await the renew loop + NOTIFY worker so **no `RaiseNotification`/`RaiseLapsed` fires AFTER `CloseAsync` returns** — this protects 4.3's marshalled handler from a post-teardown event. But this does NOT relax §0: during **normal operation** (before close) every event is still off-thread and must be marshalled. Do not treat the patch as a reason to skip marshalling.

**Guard (retro Action H — AC-4.3.12):** drive the handlers with `DeferredUiDispatcher` (queues `Post`, runs on `Drain()`) and assert state is unchanged until `Drain()`. `InlineUiDispatcher` runs `Post` inline and **masks** missing marshalling — it is the exact reason the 3.2 crash slipped 356 green tests. Keep `InlineUiDispatcher` for the convenience paths, but the marshalling proof MUST use `DeferredUiDispatcher`.

**Canonical shape to copy:** `InvocationPopupViewModel.InvokeAsync` / `.InitializeAsync` (`src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs` L159-258, L277-325) — `ConfigureAwait(false)` on every await, pure off-thread projection, then a single `_ui.Post(() => { ...apply... })`.

### §1 — The Story 4.2 consumption seam (exact shapes, verified)

- `ISubscriptionClient` (`src/ohSpy.Core/Events/ISubscriptionClient.cs`):
  - `Task<SubscriptionHandle> SubscribeAsync(ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken)` — `popupToken` is the D7 popup-level token; closing it cancels the renew loop. On a failed SUBSCRIBE the thrown `UpnpException` propagates and **no** subscription is created (no SID → no UNSUBSCRIBE).
  - `void SetAdapterContext(CancellationToken)` — already called by `ShellViewModel.RunStartAsync`; **4.3 does NOT call this** (the singleton client already has the adapter context).
- `SubscriptionHandle` (`src/ohSpy.Core/Events/SubscriptionHandle.cs`):
  - `string Sid { get; }`
  - `event Action<EventNotification> NotificationReceived` (off-thread; replay-flush on attach)
  - `event Action<SubscriptionLapseReason> Lapsed` (off-thread; replay-flush on attach)
  - `Task CloseAsync()` — **idempotent** (Interlocked-guarded); active → UNSUBSCRIBE over a fresh adapter-token-linked 5 s CTS (D7); lapsed/device-gone → no UNSUBSCRIBE. 4.3 just calls it + disposes.
- `EventNotification(string Sid, long Seq, DateTime ReceivedUtc, IReadOnlyDictionary<string,string> Properties)` (`src/ohSpy.Core/Models/EventNotification.cs`) — `Properties` is **already parsed** (4.2 owns the `<e:propertyset>` parse; **4.3 does NOT re-parse XML** — keeps the VM pure-UI per retro Action H).
- `SubscriptionLapseReason { RenewRefused, RenewTransportError, AdapterSwitch, DeviceGone }` (`src/ohSpy.Core/Events/SubscriptionLapseReason.cs`).

The subscribe-async flow mirrors **3.3's `InitializeAsync`** (kicked off after the window activates) — on the post-await continuation, marshal. The "Latest property values" is a **last-write-wins merge** of each `EventNotification.Properties`; the event list is the **raw newest-first stream**.

### §2 — Reuse, don't reinvent

- **Newest-first ~5 K FIFO list = `BoundedObservableCollection<EventNotification>(5000)`** (`src/ohSpy.Core/Collections/BoundedObservableCollection.cs`). API confirmed: `PrependNewest(item)` inserts at logical index 0; at capacity emits `Add(0)` then `Remove(Capacity)` — **never `Reset`** (only `Clear()` emits `Reset`). `this[0]` is newest. Story 2.7's SSDP log used `10_000`; the epic pins **`5_000`** for events (FR-033 + D6) — use 5,000.
- **Popup window + ownership = the 2.9 / 3.2 launcher seam.** Mirror `IInvocationPopupLauncher` (`src/ohSpy.Core/ViewModels/IInvocationPopupLauncher.cs`) + `InvocationPopupLauncher` (`src/ohSpy.App/Windowing/InvocationPopupLauncher.cs`): Pattern-7 named factory delegate → `new XxxWindow(vm)` → `window.Activate()` → `Adopt(window, ShellWindow)` → `_ = vm.InitializeAsync()`. `WindowOwnershipManager` (`src/ohSpy.App/Windowing/WindowOwnershipManager.cs`) already supports **multiple owned children per parent** (`Dictionary<IntPtr, List<IntPtr>>` + `GetChildrenOf`) → the concurrent-popups AC needs no ownership change.
- **Lapsed / device-gone banner = the 2.9 `PropertiesViewModel` / 3.2 pattern (FR-037).** `IDeviceRegistry.DeviceRemoved` fires on the UI thread; a UUID match flips the banner. `IDisposable` unsubscribes (without it the singleton registry pins every popup VM ever opened — 2.9's hard lesson). Note the **dual path** to DeviceGone: `DeviceRemoved` (registry, UI-thread) AND `handle.Lapsed(DeviceGone)` (4.2, off-thread) — both converge on `Status = DeviceGone`; make the setter idempotent.
- **The `NodeServices` 8th member** (`src/ohSpy.Core/ViewModels/NodeServices.cs`) — currently 7 members (the 7th is `IInvocationPopupLauncher`, Story 3.2). Add `ISubscriptionPopupLauncher` as the 8th, register it in `ServiceRegistration` BEFORE the `services.AddSingleton<NodeServices>()` line. This breaks the same construction sites as the 3.2 add: `ServiceNodeViewModelTests.MakeNodeServices`, `DeviceNodeViewModelTests`, `DeviceTreeViewModelTests`, `ActionNodeViewModelTests` — add a `FakeSubscriptionPopupLauncher` default.

### §3 — Launch routing: `ServiceNodeViewModel` is the right seam (cleaner than the epic sketch)

The epic (L1697) sketches `ShellViewModel.OpenSubscriptionPopupCommand(service)`. **That is the wrong seam here** — and unlike 3.2, no enrichment is needed: `ServiceNodeViewModel` (`src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`) **already holds `_service` (`ServiceDescription`), `_parentEntry` (`RegistryEntry`), and `_services` (`NodeServices`)** — everything the launcher needs. So the real `SubscribeCommand` body is simply:

```csharp
[RelayCommand]
private void Subscribe() => _services.SubscriptionPopupLauncher.Open(_service, _parentEntry);
```

This mirrors how `ActionNodeViewModel.OpenInvocationPopup()` calls `_services.InvocationPopupLauncher.Open(_action, _parentService, _parentEntry)` (verified). No `ShellViewModel` command, no context threading — the context is already on the node. (3.2's ActionNode needed enrichment only because the action leaf had no service/entry back-reference; the ServiceNode already does.)

### §4 — "Latest property values" map: chosen shape (engineering judgment, documented per epic L1691)

No `ObservableDictionary` exists in the codebase, and `BoundedObservableCollection` is a single newest-first stream (wrong shape for an overwrite-in-place keyed map). **Decision:** expose `ObservableCollection<LatestPropertyValue> LatestPropertyValues`, where `LatestPropertyValue` is a tiny `ObservableObject` with `string Name` (immutable) + `[ObservableProperty] string Value`. Merge logic (`MergeLatest`): for each `kvp` in the notification, find the existing row by `Name` → set `.Value` (last-write-wins, in-place, raises `PropertyChanged` → the bound row text updates); else append a new row. This keeps the summary panel **anchored** (it is a separate panel above the event `ScrollViewer`, not inside it — AC-4.3.10) and overwrite-in-place (FR-033). All mutations are marshalled (§0). Keep `Name` ordering stable (append-on-first-seen) so the panel doesn't reshuffle on every event.

### §5 — Files to create / modify

**CREATE (Core):**
- `src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs` — the heart (sealed, `ObservableObject`, `IDisposable`). Model on `InvocationPopupViewModel`.
- `src/ohSpy.Core/ViewModels/ISubscriptionPopupLauncher.cs` — Core seam (mirror `IInvocationPopupLauncher`).
- `SubscriptionStatus` enum + `LatestPropertyValue` row (co-locate with the VM, or a small `Subscription*.cs`).

**CREATE (App):**
- `src/ohSpy.App/Views/SubscriptionPopupWindow.xaml` + `.xaml.cs` — mirror `InvocationPopupWindow.*`.
- `src/ohSpy.App/Windowing/SubscriptionPopupLauncher.cs` — mirror `InvocationPopupLauncher.cs` (+ the `SubscriptionPopupViewModelFactory` delegate).

**CREATE (tests):**
- `tests/ohSpy.Core.Tests/Fakes/FakeSubscriptionPopupLauncher.cs` — mirror `FakeInvocationPopupLauncher`.
- `tests/ohSpy.Core.Tests/Fakes/FakeSubscriptionClient.cs` — returns a controllable handle source (drive `internal RaiseNotification`/`RaiseLapsed` directly via `InternalsVisibleTo` — confirm the test project has it; the 4.2 `SubscriptionClientTests` already drive internals, so it does).
- `tests/ohSpy.Core.Tests/ViewModels/SubscriptionPopupViewModelTests.cs`.

**MODIFY:**
- `src/ohSpy.Core/ViewModels/NodeServices.cs` — add `ISubscriptionPopupLauncher` as the 8th member (current state: 7-member record ending `IInvocationPopupLauncher InvocationPopupLauncher`). Preserve member order + the per-member comments.
- `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs` — replace the `Subscribe()` stub body (L161-165) with the real launch; update its comment (currently "STUB — AC-2.8.5… Story 4.1 removes this stub"). **Preserve** `FetchServiceXmlCommand` and everything else.
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — add the launcher block (factory + dual-reg) BEFORE the `NodeServices` line (L150-153); set `ShellWindow` in `App.OnLaunched` next to the existing `InvocationPopupLauncher`/`PropertiesLauncher` sets. **Preserve** all existing registrations.
- `src/ohSpy.App/.../MainWindow.xaml` — replace the "Subscribe (coming in Epic 4)" `MenuFlyoutItem` label with "Subscribe" (the `Subscribe` command binding already exists from 2.8). **Preserve** the existing `ContextFlyout` structure (do not disturb the pre-existing benign `WMC1506` site).
- `src/ohSpy.App/App.xaml.cs` (`OnLaunched`) — set `SubscriptionPopupLauncher.ShellWindow = _window`.
- Test ctor sites for the `NodeServices` 8th member (4 files, as in §2).
- `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs` — retarget `SubscribeCommand_Stub_WarnsNotImplemented_AC285` to assert the launcher fired (not a warning); update `MakeNodeServices`.

**DO NOT MODIFY:** `SubscriptionHandle.cs`, `SubscriptionClient.cs`, `ISubscriptionClient.cs`, `EventNotification.cs`, `SubscriptionLapseReason.cs` (Story 4.2 — frozen seam), `BoundedObservableCollection.cs`, `WindowOwnershipManager.cs`, `DiagCategories.cs` (§6).

### §6 — `DiagCategories.FeatureNotImplemented` — LEAVE IT (do not delete)

After Task 8 removes the Subscribe stub, `FeatureNotImplemented` (`DiagCategories.cs` L112) becomes **unused in production** (the 2.9 Properties stub already stopped using it; the Subscribe stub was its last consumer). **Decision: leave the constant in place.**
- It is **architecturally pinned** by `DiagCategoriesTests.DiagCategories_ExactSetMatchesArchitecturePinnedList` (verified — the name `"FeatureNotImplemented"` is in `expectedNames` L64). Deleting it would force editing that pinned-set guard.
- It is harmless (a `const string`), self-documents the "placeholder for menu items whose real handler lands later" intent, and is a deliberate part of the pinned diagnostic surface.
- `DiagCategoriesUsageTests` is **reflection-based on the constant surface** (it doesn't require every constant to have a live call site), so an unused constant does NOT break it — confirm by running it, but no change expected.
- **Action for the implementer:** do nothing to `DiagCategories.cs` or `DiagCategoriesTests.cs`. If a future cleanup wants to drop it, that is a separate deliberate PR touching both the constant and the pinned list together. (Flag this to the reviewer as an intentional "known-unused but pinned" constant, not an oversight.)

### §7 — Lifecycle + close (D7) and the Story 5.2 forward dependency

- Popup close → `Dispose()` → `_handle?.CloseAsync()` (4.2 does the best-effort UNSUBSCRIBE over the adapter-linked token; the VM just calls it + disposes). Fire-and-forget — the window closes immediately; the ≤5 s UNSUBSCRIBE runs async (AC-4.3.9 / epic L1740).
- A `Lapsed` event → banner with reason-specific text (AC-4.3.4). On a lapsed/device-gone handle, `CloseAsync` sends no UNSUBSCRIBE (4.2 behaviour) — the VM still calls it (idempotent, safe).
- **Story 5.2 (adapter switch, runs AFTER 4.3) relies on this:** 5.2 cancels the adapter token → every open subscription popup's renew loop lapses → `handle.Lapsed(AdapterSwitch)` → 4.3's banner. So **build the lapsed handling correctly now** — it is the contract 5.2 consumes. (5.2 is re-sequenced into Epic 4 as the last story; sprint-status L153-167.)

### §8 — Test boundary (Core vs App) + the DeferredUiDispatcher guard

- **Automated surface = the Core `SubscriptionPopupViewModel`**: subscribe→handle wiring, marshalled event append, latest-values merge, lapsed banner (each reason), failed-subscribe, bounded-list eviction, dispose→CloseAsync, 5-concurrent-independent + non-serial drill — **with the `DeferredUiDispatcher` marshalling guard** (AC-4.3.12, retro Action H). Mirror `InvocationPopupViewModelTests` structure.
- **App-only (untestable; `CoreAppBoundaryTests` forbids `Core.Tests → App`, no App test project):** `SubscriptionPopupWindow` (XAML + `Status`→Visibility projections + the anchored-panel layout + `ItemsRepeater` virtualisation), `SubscriptionPopupLauncher` (`Activate→Adopt`), the right-click → `Subscribe` routing, FR-046 ownership of concurrent popups → **manual smoke (Task 12)**.
- Baseline: **462 passed / 2 skipped** (post-4.2). Expect ~20-28 new Core tests.

### Project Structure Notes

- VM lands at `src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs` (arch source tree L2137 — `# FR-032`). Launcher seam in `ViewModels/` next to `IInvocationPopupLauncher.cs`. Window in `src/ohSpy.App/Views/`; launcher in `src/ohSpy.App/Windowing/`. All consistent with the 2.9 / 3.2 layout — no structure variance.
- `CoreAppBoundaryTests` forbids `Core → App`: the popup-open crosses the boundary via the `ISubscriptionPopupLauncher` seam (Core interface, App impl) exactly like 3.2.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story-4.3] (epic L1674-1762: ACs, multiple-popups FR-036/FR-104, stub removal L1719-1722).
- [Source: _bmad-output/planning-artifacts/architectures/.../architecture.md#Decision-4] (GENA; the host does NOT parse `<e:propertyset>` — the popup VM does; non-serial NOTIFY above the host). Source-tree `SubscriptionPopupViewModel.cs # FR-032` at L2137.
- [Source: src/ohSpy.Core/Events/SubscriptionHandle.cs] — **off-thread `NotificationReceived`/`Lapsed` + replay-flush-on-attach** (§0).
- [Source: src/ohSpy.Core/Events/ISubscriptionClient.cs] — `SubscribeAsync` shape; `SetAdapterContext` is ShellViewModel's job, not 4.3's.
- [Source: src/ohSpy.Core/Models/EventNotification.cs] — pre-parsed `Properties` (no re-parse).
- [Source: src/ohSpy.Core/Collections/BoundedObservableCollection.cs] — `PrependNewest`, `Add(0)`/`Remove(cap)`, never `Reset`; "callers must marshal via `IUiDispatcher`".
- [Source: src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs] — canonical marshalling shape (`ConfigureAwait(false)` + `_ui.Post`), DeviceRemoved banner, Interlocked Dispose — **the template for this story**.
- [Source: src/ohSpy.App/Windowing/InvocationPopupLauncher.cs + WindowOwnershipManager.cs] — Activate→Adopt; multi-child ownership.
- [Source: src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs] — the `Subscribe` stub (L158-165) to replace; already holds `_service`/`_parentEntry`/`_services`.
- [Source: src/ohSpy.Core/ViewModels/NodeServices.cs] — 7-member record; add the 8th.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs L112 + tests/.../DiagCategoriesTests.cs L64] — `FeatureNotImplemented` pinned (§6).
- [Source: tests/ohSpy.Core.Tests/Fakes/DeferredUiDispatcher.cs] — the marshalling guard fake (retro Action H).
- [Source: _bmad-output/implementation-artifacts/epic-3-retro-2026-06-04.md] — Action E (smoke per UI story), H (DeferredUiDispatcher guard), I (dev adapter override), J (event-smoke plan); the 3.2 `RPC_E_WRONGTHREAD` crash narrative.
- [Source: _bmad-output/implementation-artifacts/4-2-…md + sprint-status.yaml L40,165] — 4.2 seam + the post-close-event patch (CloseAsync awaits the worker so no event fires after close).
- [Memory: winui-no-synccontext-marshal-vm] — WinUI 3 has no `SynchronizationContext`; marshal every post-await / off-thread observable mutation via `_ui.Post`.
- [Memory: smoke-per-ui-story] — manual UI smoke per UI-touching story before review/done; never batch to epic close.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (story-context creation); claude-opus-4-8[1m] (implementation)

### Debug Log References

- `dotnet build src/ohSpy.Core -warnaserror` → 0 warnings / 0 errors.
- `dotnet test tests/ohSpy.Core.Tests` → 485 passed / 2 skipped / 0 failed (baseline 462/2 → +23 new Core tests). The 2 skips are the pre-existing intentional `[Fact(Skip=…)]` on AsyncDisciplineTests + DiagCategoriesUsageTests (analyzer/review-enforced).
- CoreAppBoundaryTests + DiagCategoriesTests (exact pinned-set incl. `FeatureNotImplemented`) → 11 passed / 0 skipped. Chaos = 1 (`UpnpHttpClientChaosTests`).
- `dotnet build src/ohSpy.App` → Build succeeded, 1 warning (the pre-existing benign `WMC1506` on `MainWindow.xaml:141`), 0 errors. No new warnings from the new XAML window.
- Two CA1305 locale errors in the new test file (long/int `.ToString()`) fixed with `CultureInfo.InvariantCulture`.

### Completion Notes List

- Story-context analysis (Opus, fresh context). Reconciled epic/architecture prose against SHIPPED 4.1/4.2/2.7/2.9/3.2 source.
- Key reconciliations: (1) `SubscriptionHandle` events fire OFF the UI thread (4.2 worker) AND the replay-buffer flush fires on the `add`-calling thread (the off-thread post-await continuation) → §0 marshalling is the headline risk. (2) Launch seam is `ServiceNodeViewModel.SubscribeCommand` (already holds service+entry+services) — NOT the epic's `ShellViewModel.OpenSubscriptionPopupCommand`; no context threading needed (§3). (3) `LatestPropertyValues` shape chosen as `ObservableCollection<LatestPropertyValue>` (no ObservableDictionary in repo; BoundedObservableCollection is stream-only) — §4. (4) `DiagCategories.FeatureNotImplemented` becomes production-unused after the stub removal but is architecturally pinned → LEAVE IT (§6). (5) `WindowOwnershipManager` already supports multi-child ownership → concurrent-popups AC needs no change.

**Implementation notes (dev session):**
- ⚠️ §0 MARSHALLING — CONFIRMED: EVERY observable mutation in EVERY `SubscriptionHandle` event/replay path goes through `_ui.Post`. `InitializeAsync` awaits `SubscribeAsync` with `ConfigureAwait(false)` and applies the success state (attach handlers + `Status=Subscribed`) inside a single `_ui.Post`; `OnNotification` posts `Events.PrependNewest` + `MergeLatest`; `OnLapsed` posts the `Status`/`StatusMessage` flip; `FailSubscribe` posts the failed-subscribe state. The replay-buffer flush (which fires synchronously inside `add` on the off-thread continuation) lands in `OnNotification`/`OnLapsed`, which themselves `_ui.Post` — so it is covered. `_diag` left unused in the VM (no off-thread emit needed; failed-subscribe shows text, no diagnostic per AC-4.3.5 convention). Pre-await ctor mutations (`Status=Subscribing`) are direct (UI thread). `OnDeviceRemoved` is direct (FR-037: DeviceRemoved fires on the UI thread).
- GUARDED BY `DeferredUiDispatcher` (NOT inline) — 4 dedicated regression tests assert state is NOT mutated until `Drain()`: `InitializeAsync_SubscribedFlip_GoesThroughPost_DeferredGuard`, `Notification_IsMarshalled_DeferredGuard`, `Lapse_IsMarshalled_DeferredGuard`, plus the replay-buffer drill `ReplayBuffer_PreAttachEvent_IsDeliveredAndMarshalled` (two `Drain()`s: attach-flush then the queued apply). No real-thread reliance.
- Multiple concurrent popups: `FiveConcurrentPopups_RenderIndependently` (5 VMs, different event counts, close one mid-stream → others unaffected, per-client CloseCount isolation) + the non-serial drill `SlowNotificationOnVmA_DoesNotBlockVmB` (per-VM deferred dispatchers; draining B delivers B while A stays pending). Each VM owns its own handle + `_popupCts` (linked to `DeviceToken`) + `Events` + `LatestPropertyValues`.
- Latest-values: `ObservableCollection<LatestPropertyValue>` last-write-wins in-place (`MergeLatest` overwrites an existing row's `[ObservableProperty] Value`, appends on first-seen → stable order). Anchored in a separate panel above the event `ScrollViewer` in XAML (AC-4.3.10) so it never scrolls.
- Lapsed banner: reason-specific text per AC-4.3.4 (DeviceGone / AdapterSwitch / RenewRefused+RenewTransportError); covered by a `[Theory]`. Dual DeviceGone path (registry `DeviceRemoved` + handle `Lapsed(DeviceGone)`) is idempotent (`DeviceGone_DualPath_IsIdempotent`). This is the contract Story 5.2 (`Lapsed(AdapterSwitch)`) consumes.
- Close cascade (AC-4.3.9): `Dispose()` is Interlocked once-guarded → cancel CTS → detach handle events + registry → fire-and-forget `handle.CloseAsync()` → `_popupCts.Dispose()` in `finally`. Failed-subscribe path has no handle → no UNSUBSCRIBE (`FailedSubscribe…NoUnsubscribe` asserts CloseCount==0). Added a disposed-during-await guard in the `InitializeAsync` Post (if `_disposed`, close the freshly-returned handle instead of attaching).
- AC-4.3.8 stub removal: `ServiceNodeViewModel.Subscribe()` now calls `_services.SubscriptionPopupLauncher.Open(_service, _parentEntry)` (no diagnostic); `MainWindow.xaml` label "Subscribe (coming in Epic 4)" → "Subscribe"; the old `SubscribeCommand_Stub_WarnsNotImplemented_AC285` test retargeted to `SubscribeCommand_OpensSubscriptionPopup_WithServiceAndEntry_AC438`.
- §6 DECISION HONOURED: `DiagCategories.cs` and `DiagCategoriesTests.cs` UNTOUCHED. `DiagCategories.FeatureNotImplemented` is now production-UNUSED (the Subscribe stub was its last consumer) but remains pinned by `DiagCategoriesTests` exact-set guard (green). **FLAG TO REVIEWER:** this is an intentional "known-unused but architecturally-pinned" constant, not an oversight — dropping it is a separate deliberate PR touching the constant + the pinned list together.
- Manual UI smoke (Task 12): GATE OPEN / DEFERRED — cannot run headless and depends on the not-yet-built Action-I `OHSPY_ADAPTER` adapter override to reach an event-emitting Linn DS. Core VM unit tests are the compensating control; App-side stream/projection/anchored-panel/virtualisation is the only unverified surface. Concrete steps recorded inline under Task 12.

### File List

**Created (Core):**
- `src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs`
- `src/ohSpy.Core/ViewModels/SubscriptionStatus.cs`
- `src/ohSpy.Core/ViewModels/LatestPropertyValue.cs`
- `src/ohSpy.Core/ViewModels/ISubscriptionPopupLauncher.cs`

**Created (App):**
- `src/ohSpy.App/Views/SubscriptionPopupWindow.xaml`
- `src/ohSpy.App/Views/SubscriptionPopupWindow.xaml.cs`
- `src/ohSpy.App/Windowing/SubscriptionPopupLauncher.cs`

**Created (tests):**
- `tests/ohSpy.Core.Tests/Fakes/FakeSubscriptionPopupLauncher.cs`
- `tests/ohSpy.Core.Tests/Fakes/FakeSubscriptionClient.cs`
- `tests/ohSpy.Core.Tests/ViewModels/SubscriptionPopupViewModelTests.cs`

**Modified (Core):**
- `src/ohSpy.Core/ViewModels/NodeServices.cs` — added `ISubscriptionPopupLauncher` as the 8th member.
- `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs` — replaced the 2.8 Subscribe stub body with the real launch.

**Modified (App):**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — subscription-popup launcher block (factory + dual-reg) before the NodeServices line.
- `src/ohSpy.App/App.xaml.cs` — `SubscriptionPopupLauncher.ShellWindow = _window` in `OnLaunched`.
- `src/ohSpy.App/MainWindow.xaml` — menu label "Subscribe (coming in Epic 4)" → "Subscribe".

**Modified (tests):**
- `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs` — retargeted the Subscribe test; `MakeNodeServices` 8th member.
- `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs` — 3 `NodeServices` ctor sites updated.
- `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs` — `NodeServices` ctor site updated.
- `tests/ohSpy.Core.Tests/ViewModels/ActionNodeViewModelTests.cs` — `Services` helper `NodeServices` ctor updated.

**Untouched by decision (§6):** `DiagCategories.cs`, `DiagCategoriesTests.cs`. **Frozen seam (§5):** `SubscriptionHandle.cs`, `SubscriptionClient.cs`, `ISubscriptionClient.cs`, `EventNotification.cs`, `SubscriptionLapseReason.cs`, `BoundedObservableCollection.cs`, `WindowOwnershipManager.cs`.

### Review Findings

_Code review by bmad-code-review, 2026-06-04 (Sonnet 4.6, fresh context). Verdict: **APPROVED-WITH-MINOR-FIXES** — no blockers, no patches required. Independently verified the marshalling is complete (all handle-event/replay mutations via `_ui.Post`; the 4 `DeferredUiDispatcher` guards genuinely fail against un-marshalled code), the disposed-during-await guard is race-free (UI-thread Post serialisation), and concurrent-popup independence holds._

- [x] [Review][Low] ✅ ADDRESSED 2026-06-04 — no `DisposedDuringAwait` test for the guard at the success-`Post` (correct-by-inspection but unproven). **Added `DisposedDuringAwait_ClosesFreshHandle_NoAttach_NoLeak`** (DeferredUiDispatcher: dispose between `InitializeAsync` and `Drain` → guard closes the fresh handle, `CloseCount==1`, no attach, no `Subscribed` flip).
- [x] [Review][Low] ✅ ADDRESSED 2026-06-04 — no lapse-replay test (lapse buffered pre-attach). **Added `ReplayBuffer_PreAttachLapse_IsDeliveredAndMarshalled`** (lapse sibling of the notification replay drill; two `Drain()`s).
- [x] [Review][Trivial] ✅ FIXED 2026-06-04 — stale `DiagCategories.FeatureNotImplemented` XML-doc ("removed in Story 4.1" → Story 4.3); also documents it as now production-unused-but-pinned. Constant value + the exact-set guard untouched.
- [x] [Review][Info] ✅ LOGGED 2026-06-04 — `StatusMessage` shows the SID not the granted TIMEOUT (the 4.2 `SubscriptionHandle` seam exposes only `Sid`). Not worth breaking the 4.2 freeze; logged in `deferred-work.md` as a future micro-story (add `TimeSpan GrantedTimeout` to the handle).
- _Dev judgment calls #1 (pin `FeatureNotImplemented`), #2 (disposed-during-await guard), #4 (`_diag` reserve), #5 (InvariantCulture) all reviewer-APPROVED; #3 (SID-vs-TIMEOUT) APPROVED-WITH-RESERVATION → logged above._

**Post-review verification (Opus main session):** Core `-warnaserror` 0/0; full suite **487 passed / 2 skipped / 0 failed** (+2 coverage tests); the 25-test `SubscriptionPopupViewModel` suite green on 3 consecutive runs. **Automated side is clean-APPROVED.** **Manual UI smoke (Task 12): ACCEPTED-AS-DEFERRED by Project Lead (Simonc), 2026-06-04 → Status `done`.** None of the live event-stream steps can run without an event-emitting Linn DS (retro Action-I `OHSPY_ADAPTER` override). Unlike 3.2/3.3 (partial smoke), ZERO of Epic 4's live-eventing payload is real-device-verified — the Core `SubscriptionPopupViewModel` (25 tests incl. 6 `DeferredUiDispatcher` marshalling guards) is the sole compensating control. The full event-stream smoke is logged in `deferred-work.md`; **run it when Action-I lands or Story 5.2's adapter switch puts a Linn DS on-net** (5.2's `Lapsed(AdapterSwitch)` path also exercises this popup's banner). This is a known, accepted verification gap for Epic 4's marquee feature.
