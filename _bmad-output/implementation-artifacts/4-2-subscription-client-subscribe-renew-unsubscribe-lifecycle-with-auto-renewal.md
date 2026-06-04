---
baseline_commit: 5a5ad9452f8273332d079d36231c519d46dd9978
---

# Story 4.2: Subscription Client — SUBSCRIBE / RENEW / UNSUBSCRIBE Lifecycle with Auto-Renewal

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- 2026-06-04: both Sonnet-review [Patch] items applied (Opus, main session); Core 0/0 -warnaserror; 462 passed / 2 skipped; SubscriptionClient suite green 6× consecutively. Status review → done. -->;

## Story

As an ohSpy developer,
I want a `SubscriptionClient` that orchestrates the full GENA subscription lifecycle (SUBSCRIBE → auto-renew before timeout → UNSUBSCRIBE on close), routes incoming `NOTIFY` messages to subscribers by SID, parses each `<e:propertyset>` body into an `EventNotification`, and applies the "cleanup uses the level-above token" invariant for UNSUBSCRIBE,
so that the popup in Story 4.3 can take one `SubscribeAsync(service, parentEntry, popupToken)` call and trust that all the lifecycle plumbing runs correctly — renewal that keeps events flowing across multi-minute sessions, UNSUBSCRIBE that still fires on close even though the popup-level CTS has just been cancelled, lapsed subscriptions handled cleanly, and a failed SUBSCRIBE reported **without** an UNSUBSCRIBE attempt.

## Epic-4 scope context

Epic 4 (GENA Subscription): right-click → Subscribe opens a subscription popup; SUBSCRIBE goes out with a `CALLBACK` URL pointing at the in-process callback host (Story 4.1) on the selected adapter; NOTIFY events stream into the popup. **This story (4.2) is the lifecycle orchestrator** between the 4.1 inbound callback host and the 4.3 popup VM. It is **headless Core** (`ohSpy.Core.Events`) — no UI, no bound VM state, no `IUiDispatcher` mutation of bound collections (that is 4.3). It is the **first consumer** of both the Story 1.3 GENA verbs (`SubscribeAsync`/`RenewSubscriptionAsync`/`UnsubscribeAsync`) and the Story 4.1 callback-host seam (`CallbackBaseUrl` + `NotifyReceived`). The **subscription popup VM** (`SubscriptionPopupViewModel`, `BoundedObservableCollection`, "Latest property values" map, multiple concurrent popups, the Action-H `DeferredUiDispatcher` marshalling guard) is **Story 4.3** and consumes the `SubscriptionHandle` this story returns.

## Acceptance Criteria

> Tag every test with `[Trait("ac", "AC-4.2.x")]`. The six **outbound** `DiagCategories.Gena*` constants are **pre-added and verified present** (see Dev Notes "Pre-added scaffolding") → **no new `DiagCategories` constant is introduced** → `DiagCategoriesUsageTests` (reflection-based) stays unchanged.

### Surface: client, handle, models

**AC-4.2.1 — `SubscriptionClient` surface (D4 / epic L1591-1594).** `src/ohSpy.Core/Events/SubscriptionClient.cs` exposes a public method:
```csharp
Task<SubscriptionHandle> SubscribeAsync(
    ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken);
```
Abstract behind `ISubscriptionClient` (Story 4.3's popup VM injects it; mirrors every other Core seam — `IUpnpHttpClient`, `IEventCallbackHost`). Both `ISubscriptionClient` + impl live in `ohSpy.Core.Events`. Register the impl as a **singleton** (epic L1668-1670).

**AC-4.2.2 — `SubscriptionHandle` surface (epic L1594).** `src/ohSpy.Core/Events/SubscriptionHandle.cs` (public) exposes:
```csharp
string Sid { get; }
event Action<EventNotification> NotificationReceived;
event Action<SubscriptionLapseReason> Lapsed;
Task CloseAsync();   // idempotent — multiple calls are safe
```
`NotificationReceived` and `Lapsed` are **raw Core events** (`Action<…>`, NOT marshalled) — 4.3's popup VM is responsible for `_ui.Post` marshalling onto bound state (retro Action H; 4.2 stays non-UI). The handle is the only object 4.3 holds onto.

**AC-4.2.3 — `EventNotification` record (epic L1596-1598).** `src/ohSpy.Core/Models/EventNotification.cs` is exactly:
```csharp
public sealed record EventNotification(
    string Sid, long Seq, DateTime ReceivedUtc, IReadOnlyDictionary<string, string> Properties);
```
Namespace `ohSpy.Core.Models`. `Properties` is the parsed `<e:propertyset>` (property name → string value). **Reconciliation:** the epic AC pins `ohSpy.Core/Models/EventNotification.cs`; the architecture source tree (L2112) listed it under `Events/`. **Place it in `Models/`** (epic AC wins; matches `SoapArgument`/`ServiceDescription` data-record placement) and note the divergence in Dev Notes. `BoundedObservableCollection<EventNotification>` is what 4.3 binds (arch L699).

**AC-4.2.4 — `SubscriptionLapseReason` enum (epic L1599).** `src/ohSpy.Core/Events/SubscriptionLapseReason.cs` — `enum` with at least `RenewRefused`, `RenewTransportError`, `AdapterSwitch`, `DeviceGone`. (Add `Closed` only if the impl needs an internal terminal marker — engineering judgment; the four above are the contract.)

### Happy-path subscribe

**AC-4.2.5 — happy SUBSCRIBE (epic L1601-1606, FR-032).** `SubscribeAsync` resolves the absolute eventSubURL via `new Uri(parentEntry.LocationUrl, service.EventSubUrl)` (EventSubUrl is a possibly-**relative** string — see the `InvocationPopupViewModel` control-URL precedent), then calls `_http.SubscribeAsync(eventSubUrl, _callbackHost.CallbackBaseUrl, TimeSpan.FromSeconds(<initial>), popupToken)` with an initial requested `TIMEOUT` (e.g. **300 s** — document the constant). On the 200 OK it receives `SubscribeResponse(Sid, Timeout)` (SID + granted lease already parsed by the 1.3 verb — do NOT re-parse `Second-N`). It registers the SID→handle mapping, starts the auto-renew loop, emits a `Verbose`/`Information` `DiagCategories.GenaSubscribe` (carrying `DeviceUuid`, `Url`, `Sid`), and returns the handle.

**AC-4.2.6 — NOTIFY↔subscription correlation by SID (the central design decision; epic L1605, L1653-1659).** The client subscribes ONCE to `_callbackHost.NotifyReceived`. On each `NotifyRequest`, it routes by **`NotifyRequest.Sid`** to the matching handle (a concurrent SID→handle map). A NOTIFY whose SID matches no live subscription is **dropped silently** (the host already returned its idempotent `200`; no diagnostic, no throw). See Dev Notes "NOTIFY↔subscription correlation" for the **NOTIFY-before-SID race** handling (AC-4.2.7) and why callback-path tokens are **not** used in v1.

**AC-4.2.7 — NOTIFY-before-SID race (GENA classic; Dev Notes).** A device MAY deliver the first NOTIFY **before** the SUBSCRIBE HTTP response carrying the SID has returned. The client MUST NOT lose that event: register a **short-lived pending-buffer** keyed so the very first NOTIFY(s) arriving in the gap are held and **replayed to the handle once the SID is known**, OR establish the SID→handle registration is *visible to the NotifyReceived handler before the buffered event is dispatched* (engineering judgment — document the chosen mechanism; a small per-subscribe `ConcurrentQueue` drained on registration is the recommended shape). A test drives a NOTIFY landing during a delayed SUBSCRIBE responder and asserts the event reaches the handle.

### Propertyset parse boundary

**AC-4.2.8 — 4.2 parses `<e:propertyset>` (FR-104; epic L1656-1659; D4 L464).** On a routed NOTIFY, the client parses `NotifyRequest.Body` (bytes) into the property dictionary using the **shared `UpnpXmlReaderSettings.Create()`** XXE-locked settings (the Story 1.4 discipline — `DtdProcessing.Prohibit`, `XmlResolver=null`, char cap), constructs `new EventNotification(sid, req.Seq, req.ReceivedUtc, properties)`, and raises `handle.NotificationReceived(notification)`. **This is the parse boundary:** 4.1 ships raw `byte[] Body` and never parses; **4.2 owns the propertyset parse**; 4.3 only renders the parsed `EventNotification` (it does NOT re-parse XML). A malformed/unparseable propertyset body is **swallowed** (drop that one NOTIFY, optional Verbose diagnostic — do NOT lapse the subscription, do NOT crash the host's awaited handler).

**AC-4.2.9 — non-serial NOTIFY processing across subscriptions (FR-104; epic L1659-1665).** The parse + dispatch runs so a slow parse on subscription A does NOT block subscription B's NOTIFY (and does not block the host's accept loop — `NotifyReceived` is `await`ed by the host, so the client's handler must return promptly: hand the body to a per-subscription bounded worker/queue rather than parsing inline on the host's awaiting task). An integration test: subscription A simulated parse delay 200 ms; subscription B's NOTIFY observed end-to-end under 50 ms.

### Failure & lifecycle semantics

**AC-4.2.10 — failed SUBSCRIBE reported, NO UNSUBSCRIBE (FR-035; epic L1608-1613).** When `_http.SubscribeAsync` throws (`UpnpTransportException` / `UpnpTimeoutException` / `UpnpProtocolException`): the handle is **NOT returned** — the caller observes the thrown exception; **no SID is registered** (there is none); a `Warning` `DiagCategories.GenaSubscribeFailed` (Pattern 11: `DeviceUuid`, `Url`, `ErrorText`) is emitted; and **no UNSUBSCRIBE is ever attempted** for this never-created subscription (no SID = no unsubscribe). A test asserts zero `UnsubscribeAsync` calls on the failed path.

**AC-4.2.11 — auto-renew before expiry (FR-038; epic L1615-1619).** While active, a per-subscription background loop renews at **~80 % of the device-granted `Timeout`** (document the exact margin constant; ≥ a small floor so a tiny granted lease still renews sanely) via `_http.RenewSubscriptionAsync(eventSubUrl, sid, TimeSpan.FromSeconds(<requested>), <renew token>)`. On success the new granted `Timeout` replaces the prior and the loop reschedules; event delivery continues uninterrupted. The renew loop's delay must be **unit-testable without real waits** — see AC-4.2.16 + Dev Notes "Auto-renew timing + testability".

**AC-4.2.12 — renew failure → lapsed, NO retry, NO unsubscribe (FR-038/035; epic L1621-1626).** When a renew is **refused** (HTTP 412 → surfaces as `UpnpTransportException` with `StatusCode == 412`) OR fails transport-level (`UpnpTransportException` other status / `UpnpTimeoutException` / `UpnpProtocolException`): the renew loop **stops** (no further attempts — drop, not retry); the handle raises `Lapsed(RenewRefused)` (for 412) or `Lapsed(RenewTransportError)` (otherwise); the SID is marked **lapsed** internally so a later `CloseAsync` does **NOT** send UNSUBSCRIBE (UNSUBSCRIBE on an expired subscription is forbidden); a `Warning` `DiagCategories.GenaRenewFailed` (Pattern 11: `DeviceUuid`, `Url`, `Sid`) is emitted.

**AC-4.2.13 — `CloseAsync` on an ACTIVE subscription → cleanup-uses-level-above-token UNSUBSCRIBE (D7; arch L790-816; epic L1628-1634).** When `CloseAsync` runs and the subscription is still active (not lapsed): (1) stop the renew loop / cancel the client's internal popup-derived state; (2) construct a **NEW `CancellationTokenSource` with a 5 s budget**; (3) link it to the **`_adapterToken`** — **NOT** the now-cancelled popup token (linking to the cancelled popup token would cancel UNSUBSCRIBE immediately — the non-obvious bug D7 pins explicitly); (4) call `_http.UnsubscribeAsync(eventSubUrl, sid, linked.Token)`; (5) on success emit `DiagCategories.GenaUnsubscribe`; a **failed** UNSUBSCRIBE is **swallowed** (popup close MUST NOT block on a hung device — FR-034 is "send UNSUBSCRIBE", not "guarantee delivery") + `Warning` `DiagCategories.GenaUnsubscribeFailed`. Always de-register the SID from the routing map. `CloseAsync` is **idempotent** (a second call is a safe no-op — AC-4.2.2).

**AC-4.2.14 — `CloseAsync` on a LAPSED subscription → NO UNSUBSCRIBE (epic L1636-1639).** No UNSUBSCRIBE is sent; the SID is de-registered from the routing map; internal state is disposed cleanly. Idempotent.

**AC-4.2.15 — adapter switch / device-gone cancellation cascades (D7; epic L1641-1651).** The renew loop and the client's per-subscription work observe cancellation of the **adapter token** (adapter switch → `Lapsed(AdapterSwitch)`, NO UNSUBSCRIBE — device unreachable on this adapter) and the **device token** (`parentEntry.DeviceToken` cascade on byebye/prune → `Lapsed(DeviceGone)`, NO UNSUBSCRIBE — device is gone). In both cases the renew loop exits and no UNSUBSCRIBE is attempted. (The popup token, the device token, and the adapter token form the D7 chain `popup ⊂ device ⊂ adapter`; the renew loop should observe a token linked across `popupToken` + `_adapterToken` so all three abort it.)

### Concurrency & testability

**AC-4.2.16 — auto-renew timing is testable without real waits (Dev Notes).** The renew-loop delay is driven by a seam that tests can fast-forward (recommended: a small internal `Func<TimeSpan, CancellationToken, Task>` delay seam defaulting to `Task.Delay`, OR a configurable tiny granted-timeout via a controllable `IUpnpHttpClient` responder so 80 % of, say, 200 ms fires in ~160 ms). A test proves renewal fires before expiry and reschedules on the new lease — running in ms, not minutes. Document the chosen seam.

**AC-4.2.17 — multiple concurrent + independent subscriptions (FR-036; epic L1714-1717).** Several subscriptions (across services / devices) run concurrently with **independent** handles, SID routing, renew loops, and lapse state. One slow/failed renew or one slow/malformed NOTIFY on subscription A does NOT block, lapse, or mis-route subscription B. All shared state (SID→handle map, pending buffers) is thread-safe (`ConcurrentDictionary` / locks) — the host raises `NotifyReceived` from its accept/handler tasks, and renew loops run on background tasks, so the client is inherently multi-threaded.

**AC-4.2.18 — async discipline (Decision 3 / Pattern 6).** No `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` anywhere (VSTHRD002/003/100 + `AsyncDisciplineTests` are build gates); `ConfigureAwait(false)` on every Core await; renew loops on `Task.Run` with the same VSTHRD justification as `SsdpTransport`/`EventCallbackHost`; cancellation (`OperationCanceledException`) on shutdown is swallowed as the normal path.

## Tasks / Subtasks

- [x] **Task 0 — Cancellation + correlation design pass (do FIRST; AC-4.2.6, AC-4.2.7, AC-4.2.13, AC-4.2.15).** Pin on paper before coding:
  - [x] **Correlation:** route NOTIFY by **`NotifyRequest.Sid`** (`ConcurrentDictionary<string, Subscription>`). **No** per-subscription callback-path token in v1 (single shared `CallbackBaseUrl`; SID is the discriminator). Forward option recorded in Dev Notes (path token via `PathAndQuery`).
  - [x] **NOTIFY-before-SID race:** per-subscribe pending buffer (`_pending` keyed by a correlation `Guid`) filled by the `NotifyReceived` handler while a SUBSCRIBE is in flight, drained at SID registration; PLUS a handle-level replay buffer so a notification delivered before the consumer attaches its handler is flushed to the first subscriber (belt-and-braces — closed a test-load race). Sequence documented in Completion Notes.
  - [x] **Token graph (D7):** renew-loop CTS = `CreateLinkedTokenSource(popupToken, deviceToken, _adapterToken)`; UNSUBSCRIBE-on-active-close uses a FRESH `CTS(5s)` linked to `_adapterToken` only (level-above). Implemented + asserted.
  - [x] **Adapter-token acquisition for a DI singleton:** `SetAdapterContext(CancellationToken)` called from `ShellViewModel.RunStartAsync` right after `_callbackHost.StartAsync`. Wired in Task 7.
- [x] **Task 1 — Models + enum + handle (AC-4.2.2, AC-4.2.3, AC-4.2.4).**
  - [x] `src/ohSpy.Core/Models/EventNotification.cs` — the public sealed record.
  - [x] `src/ohSpy.Core/Events/SubscriptionLapseReason.cs` — the enum (4 contract members; no internal `Closed` needed).
  - [x] `src/ohSpy.Core/Events/SubscriptionHandle.cs` — public; `Sid`, `NotificationReceived` (with replay), `Lapsed`, idempotent `CloseAsync()` via a close-delegate back to the client.
- [x] **Task 2 — `ISubscriptionClient` + `SubscriptionClient` skeleton (AC-4.2.1).** `internal sealed class SubscriptionClient : ISubscriptionClient` (ctor: `IUpnpHttpClient`, `IEventCallbackHost`, `IDiagnosticEmitter` + internal test-ctor delay seam). Subscribes to `_callbackHost.NotifyReceived` once; SID→subscription `ConcurrentDictionary`.
- [x] **Task 3 — `SubscribeAsync` happy + failed paths (AC-4.2.5, AC-4.2.6, AC-4.2.7, AC-4.2.10).**
  - [x] Resolve absolute eventSubURL (`Uri.TryCreate(parentEntry.LocationUrl, service.EventSubUrl)`, guarded — malformed → `UpnpProtocolException`, no SID).
  - [x] Calls the 1.3 `SubscribeAsync` verb with `_callbackHost.CallbackBaseUrl` + 300 s; on success registers SID, drains the race buffer, starts renew loop + NOTIFY worker, emits `GenaSubscribe`, returns the handle.
  - [x] On throw: emit `GenaSubscribeFailed`, register nothing, rethrow; never UNSUBSCRIBE.
- [x] **Task 4 — NOTIFY routing + propertyset parse + non-serial dispatch (AC-4.2.6, AC-4.2.8, AC-4.2.9).**
  - [x] `OnNotifyReceivedAsync` returns promptly: SID lookup → `TryWrite` onto the subscription's bounded channel.
  - [x] Per-subscription drain worker parses `<e:propertyset>` with `UpnpXmlReaderSettings.Create()` → dict; builds `EventNotification`; raises `handle.NotificationReceived`. Malformed → swallow one NOTIFY (Verbose), no lapse.
- [x] **Task 5 — auto-renew loop + lapse (AC-4.2.11, AC-4.2.12, AC-4.2.15, AC-4.2.16).** Per-subscription `Task.Run` loop via the testable `_delay` seam, linked CTS; renew at `RenewDelayFor(granted)`; success reschedules on the new lease; 412 → `Lapsed(RenewRefused)`, other `UpnpException` → `Lapsed(RenewTransportError)` (no retry) + `GenaRenewFailed`; adapter/device/popup cancel → exit + correct lapse reason.
- [x] **Task 6 — `CloseAsync` (active vs lapsed) (AC-4.2.13, AC-4.2.14).** Idempotent (Interlocked guard on the handle). Active → fresh 5 s CTS linked to `_adapterToken` (NOT popup) → `UnsubscribeAsync` → `GenaUnsubscribe` / swallow + `GenaUnsubscribeFailed`. Lapsed → no UNSUBSCRIBE. Always de-registers SID + disposes state.
- [x] **Task 7 — DI registration + adapter-context wiring (AC-4.2.1; Task 0 seam).**
  - [x] Registered `ISubscriptionClient`→`SubscriptionClient` singleton in `ServiceRegistration.cs` next to `IEventCallbackHost`.
  - [x] Wired `_subscriptionClient.SetAdapterContext(scope.AdapterToken)` in `ShellViewModel.RunStartAsync` right after `_callbackHost.StartAsync`. `ShellViewModel` gains an `ISubscriptionClient` ctor arg (DI-resolved only — confirmed no `new ShellViewModel` test sites). 5.2 blast radius noted at the wiring site.
- [x] **Task 8 — Tests (every AC; AC-4.2.16/17 concurrency).** Extended `StubUpnpHttpClient` (controllable Subscribe/Renew/Unsubscribe responders + `GenaCalls`); added `FakeEventCallbackHost` (settable `CallbackBaseUrl` + awaited `RaiseNotifyAsync`). 19 tests covering the full AC matrix.
- [x] **Task 9 — Gate the build.** Core `0/0`; full suite **462 passed / 2 skipped** (baseline 443 + 19 new; 5× clean runs, no flakiness); chaos 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green (unchanged — no new constant). App build = 1 pre-existing benign `WMC1506` on `MainWindow.xaml:141`, no new warnings.
- [x] **Task 10 — NO manual UI smoke (headless Core orchestration).** No UI surface, no bound VM state → no manual-UI-smoke gate (Story 4.1 / 3.1 posture). Real-device SUBSCRIBE + NOTIFY smoke is a Story 4.3 item. No smoke task added here.

## Dev Notes

### EXHAUSTIVE-ANALYSIS reconciliation (architecture/epic prose vs SHIPPED code)

Every Epic 2/3/4.1 story found the prose diverged from reality. Findings for 4.2, verified against source:

1. **The 1.3 GENA verbs already exist — ORCHESTRATE, do not rebuild.** `src/ohSpy.Core/Http/IUpnpHttpClient.cs` L42-61 + `UpnpHttpClient.cs` L230-354 already implement, fully wired:
   - `SubscribeAsync(Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken)` → builds `CALLBACK: <url>`, `NT: upnp:event`, `TIMEOUT: Second-N`; on 200 parses `SID` + `TIMEOUT: Second-N` → returns `SubscribeResponse(Sid, Timeout)`; non-2xx → `UpnpTransportException(statusCode)`; missing SID/TIMEOUT or malformed `Second-N` → `UpnpProtocolException`; timeout → `UpnpTimeoutException`. Per-request budget `HttpTimeoutOptions.GenaSubscribe` (5 s).
   - `RenewSubscriptionAsync(eventSubUrl, sid, requestedTimeout, ct)` → `SID` + `TIMEOUT` headers; same `SubscribeResponse` shape + same exception mapping. **A 412 refusal surfaces as `UpnpTransportException` with `StatusCode == 412`** (there is no dedicated refusal type — branch on `StatusCode`).
   - `UnsubscribeAsync(eventSubUrl, sid, ct)` → `SID` header; non-2xx → `UpnpTransportException`; budget `HttpTimeoutOptions.GenaUnsubscribe` (5 s). It **throws** on failure (XML doc-comment: "Throws on transport/timeout failure so callers can decide whether to retry") — so **4.2's `CloseAsync` is the layer that swallows** the UNSUBSCRIBE failure (AC-4.2.13).
   - `src/ohSpy.Core/Http/SubscribeResponse.cs`: `public sealed record SubscribeResponse(string Sid, TimeSpan Timeout)` — `Sid` from the `SID:` header, `Timeout` the **granted lease** (NOT the request budget). 4.2 schedules renewal off `Timeout`.
   - ⚠️ **Diagnostics already emitted by the verb layer carry `DeviceUuid = null`** (the http layer has no UUID): `HttpTimeout` on timeout, `HttpTransport` on transport error. 4.2 emits the **UUID-bearing** `Gena*` diagnostics at the orchestrator level (it has `parentEntry.Uuid`) — exactly the intentional-duplicate pattern Story 3.2 established for `SoapFault` (`InvocationPopupViewModel.cs` L200-214). Keep both; the 4.2 emit is the FR-041-useful one.
2. **The 4.1 callback-host seam is shipped — wire it (AC-4.2.5/4.2.6).** `src/ohSpy.Core/Events/IEventCallbackHost.cs`: `Uri CallbackBaseUrl { get; }` (pass straight into the 1.3 `SubscribeAsync`'s `callbackUrl` — verified Uri-typed match) + `event Func<NotifyRequest, Task> NotifyReceived` (the host **awaits** handlers and drains them on shutdown → 4.2's handler must return promptly, AC-4.2.9). `src/ohSpy.Core/Events/NotifyRequest.cs`: `(string Sid, long Seq, string PathAndQuery, byte[] Body, DateTime ReceivedUtc)` — **raw bytes, never parsed by the host** (4.1 completion notes; D4 L464). 4.1's open-Q#4 explicitly punted the callback-path-token decision to 4.2.
3. **NOTIFY↔subscription correlation (the central design decision 4.2 OWNS).** Route by **`NotifyRequest.Sid`** (a `ConcurrentDictionary<string, SubscriptionHandle>`), NOT by a callback-path token. Rationale: (a) the SID is the canonical GENA discriminator and is present on every NOTIFY (UDA 1.0 §4.2); (b) a single shared `CallbackBaseUrl` is simpler and the host surfaces only one base URL; (c) v1 needs no path token. **The NOTIFY-before-SID race** (AC-4.2.7) is the one gap SID-only routing has: a device can fire NOTIFY #0 before our SUBSCRIBE response returns the SID. Close it with a short pending buffer/replay (recommended: a per-subscribe `ConcurrentQueue<NotifyRequest>` filled by the `NotifyReceived` handler when no handle yet matches *and* a SUBSCRIBE for that eventSubURL is in flight, drained when the SID registers) — do NOT rely on a path token. **Forward option (record, do not build):** if a future device reuses/omits SIDs ambiguously, embed a per-subscription token in the CALLBACK path (`CallbackBaseUrl` + `/sub/<token>`, read back via `NotifyRequest.PathAndQuery` which 4.1 surfaces verbatim) — 4.1 already proved `/sub/abc?token=xyz` round-trips. Out of v1 scope.
4. **Propertyset parse boundary (FR-104).** 4.1 → raw `byte[]`; **4.2 parses** the `<e:propertyset>` into `EventNotification.Properties` with the **shared `UpnpXmlReaderSettings.Create()`** (`src/ohSpy.Core/Scpd/UpnpXmlReaderSettings.cs`, `internal` — same assembly, reuse it; `DtdProcessing.Prohibit` + `XmlResolver=null` + 4 M char cap, exactly as `SoapFaultParser`/`DeviceDescriptionParser` do); 4.3 renders the parsed record and **does not** re-parse XML. The `<e:propertyset>` shape is `<e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0"><e:property><VarName>value</VarName></e:property>…</e:propertyset>` — extract each inner element name→text. (Architecture data-flow L2295 originally sketched the parse in the popup VM; the **epic AC L1656 explicitly assigns it to the client** — epic wins, and it keeps 4.3 pure-UI per retro Action H. Note this divergence.)
5. **`EventNotification` placement.** Epic AC L1596 says `ohSpy.Core/Models/EventNotification.cs`; arch source tree L2112 said `Events/`. **Use `Models/`** (epic AC wins; consistent with `SoapArgument`/`ServiceDescription` data records). `SubscriptionClient.cs`, `SubscriptionHandle.cs`, `SubscriptionLapseReason.cs`, `ISubscriptionClient.cs` live in `Events/` (`ohSpy.Core.Events`).
6. **`SubscriptionClient` does NOT exist yet — this story creates it.** `Glob src/ohSpy.Core/Events/*` → the 4.1 host files only (`NotifyRequest`, `IEventCallbackHost`, `EventCallbackHost`, `HttpRequestParser*`, `TimeoutStream`, `CallbackTimeoutException`). No subscription types exist.

### Pre-added scaffolding (cite + use; NO new constant, NO new option)

- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` L55-69 — all six **outbound** GENA constants pre-added (comment "Story 4.2 — pre-added"): `GenaSubscribe` (ctx `DeviceUuid, Url, Sid`), `GenaSubscribeFailed` (`DeviceUuid, Url, ErrorText`), `GenaUnsubscribe` (`DeviceUuid, Url, Sid`), `GenaUnsubscribeFailed` (`DeviceUuid, Url, Sid`), `GenaRenewFailed` (`DeviceUuid, Url, Sid`). **No new constant → `DiagCategoriesUsageTests` (reflection) stays unchanged.** Lifecycle/failure → constant map:

  | Lifecycle event / failure | Severity | Diagnostic constant |
  |---|---|---|
  | SUBSCRIBE success (SID + lease) | Verbose/Info | `GenaSubscribe` |
  | SUBSCRIBE failed (transport/timeout/protocol) — **no UNSUBSCRIBE** | Warning | `GenaSubscribeFailed` |
  | RENEW refused (412) → `Lapsed(RenewRefused)` | Warning | `GenaRenewFailed` |
  | RENEW transport/timeout fail → `Lapsed(RenewTransportError)` | Warning | `GenaRenewFailed` |
  | UNSUBSCRIBE on active close — success | Verbose/Info | `GenaUnsubscribe` |
  | UNSUBSCRIBE on active close — failed (swallowed) | Warning | `GenaUnsubscribeFailed` |
  | NOTIFY received + routed | (no new emit needed; 4.1 already emits `GenaNotifyReceived` Verbose) | — |
  | Adapter switch / device gone → `Lapsed(AdapterSwitch\|DeviceGone)` | (no diag required; lapse event is the signal) | — |

- `src/ohSpy.Core/Http/HttpTimeoutOptions.cs` L14-15 — `GenaSubscribe` (5 s) + `GenaUnsubscribe` (5 s) **per-request budgets pre-added** and already consumed by the 1.3 verbs. The initial requested **lease** (300 s) and the **80 %-renew margin** are 4.2 constants (NOT in `HttpTimeoutOptions`) — define them as named consts in the client with a one-line rationale. (`MaxGenaResponseBytes` = 64 KB L32 is the outbound SUBSCRIBE/RENEW response cap the 1.3 verb already enforces; 4.2 needs nothing there.)

### NOTIFY↔subscription correlation (front-and-centre design)

```
ONE NotifyReceived handler (registered once in the ctor):
  on NotifyRequest req:
     if map.TryGetValue(req.Sid, handle):  enqueue req → handle's bounded worker → (parse propertyset → raise NotificationReceived)
     else if a SUBSCRIBE is in-flight whose response may carry req.Sid: buffer req in the pending queue (race, AC-4.2.7)
     else: drop silently (host already 200'd; unknown/cancelled SID is an idempotent ack — D4 L444)
  handler RETURNS PROMPTLY (host awaits it; do not parse inline) — AC-4.2.9
```

- **Why SID not path-token:** see reconciliation #3. Single shared `CallbackBaseUrl`; SID is per-subscription and authoritative.
- **Race close-out:** the SUBSCRIBE response and the first NOTIFY can interleave. Register the pending buffer **before** awaiting `SubscribeAsync`; on the response, register SID→handle, then drain the buffer into the handle. (A device that NOTIFYs before we know the SID can't be matched by SID yet — the buffer holds it. Document the exact ordering you implement.)

### Auto-renew timing + testability seam

- **Margin:** renew at **~80 % of granted `Timeout`** (e.g. 240 s for a 300 s lease). Document the const; clamp with a small floor (e.g. `max(0.8 × granted, granted − 30 s)` or similar — engineering judgment, but write it down) so a short lease still renews ahead of expiry.
- **No injectable clock exists in Core.** The SSDP log + diagnostic emitter use `DateTime.UtcNow` directly; there is **no `IClock`/timer abstraction**. So make the renew delay **testable without a real clock**: either (a) a tiny internal delay seam `Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;` overridable via an internal test ctor (recommended — minimal surface, no new public type, mirrors the `EventCallbackHost` internal-test-ctor precedent for the drain budget), or (b) drive a controllable `IUpnpHttpClient` SUBSCRIBE responder that grants a tiny lease (e.g. 200 ms) so 80 % fires in ~160 ms real time. Pick (a) for deterministic renewal-timing tests + use (b) for the integration-style lifecycle test. Document the choice in the impl + AC-4.2.16.
- **Renew-loop token (D7):** the loop's CTS is `CreateLinkedTokenSource(popupToken, _adapterToken)` (popup close, device-gone-via-popup-link, adapter switch all abort it). On cancel: exit cleanly, raise the right `Lapsed` reason (distinguish adapter-token-cancelled vs device/popup by inspecting which token fired, like `EventCallbackHost`/`UpnpHttpClient` disambiguate `external.IsCancellationRequested`).

### Cleanup-uses-level-above-token (D7 — the non-obvious invariant)

Verbatim from architecture L794-813 — the dev WILL reach for `_popupCts.Token` by default and break UNSUBSCRIBE on close. Pin it:
```csharp
// CloseAsync on an ACTIVE subscription:
//   popupToken is (about to be) cancelled — do NOT use it for the UNSUBSCRIBE.
using var unsubCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using var linked   = CancellationTokenSource.CreateLinkedTokenSource(_adapterToken, unsubCts.Token); // level-above
try    { await _http.UnsubscribeAsync(eventSubUrl, sid, linked.Token).ConfigureAwait(false);
         _diag.Information/Verbose(DiagCategories.GenaUnsubscribe, …); }
catch (Exception ex) when (ex is not OutOfMemoryException)
       { _diag.Warning(DiagCategories.GenaUnsubscribeFailed, …); } // swallow — close must not block on a hung device
```
The `InvocationPopupViewModel.Dispose()` (L394-406) deliberately notes "No GENA unsubscribe here (that is Epic 4) — nothing needs the level-above token." **4.2 is that Epic-4 layer** — it is the first real user of the level-above-token invariant.

### Ownership / lifetime + hand-off to 4.3

- **Singleton `ISubscriptionClient`** (epic L1668), registered in `ServiceRegistration.cs` near the `IEventCallbackHost` line. Subscribes to `NotifyReceived` **once** for its lifetime (one host, one client).
- **Adapter-token acquisition (the DI-singleton problem):** the adapter token is created per-`AdapterScope` at startup, not at DI-construction. The host already receives it via `StartAsync(ip, scope.AdapterToken)` in `ShellViewModel.RunStartAsync`. Give `SubscriptionClient` the same point of entry: a `SetAdapterContext(scope.AdapterToken)` call right after `_callbackHost.StartAsync(...)` (Task 0/7). `ShellViewModel` gains an `ISubscriptionClient` ctor arg (DI-resolved only — no `new ShellViewModel(...)` test sites exist, same as 4.1; confirm in Task 7).
- **5.2 blast radius (record, do not implement):** Story 5.2's atomic rebind disposes/recreates the `EventCallbackHost`; it must also (a) lapse all live subscriptions with `AdapterSwitch` (the adapter-token cancel already cascades into every renew loop) and (b) re-`SetAdapterContext` on the new adapter. Keep all per-subscription tokens linked to `_adapterToken` so step (a) is automatic. Note this at the wiring site.
- **4.3 consumes only `SubscriptionHandle`:** `Sid`, `NotificationReceived` (raw `Action<EventNotification>` — 4.3 marshals via `_ui.Post` per retro Action H + memory `winui-no-synccontext-marshal-vm`), `Lapsed`, idempotent `CloseAsync()`. 4.2 surfaces the **parsed** `EventNotification` — 4.3 does NOT re-parse the propertyset. 4.2 stays Core/non-UI (no `IUiDispatcher`).

### Files to create / modify

| File | Action | Notes |
|---|---|---|
| `src/ohSpy.Core/Models/EventNotification.cs` | **NEW** | public sealed record (AC-4.2.3). |
| `src/ohSpy.Core/Events/SubscriptionLapseReason.cs` | **NEW** | enum (AC-4.2.4). |
| `src/ohSpy.Core/Events/SubscriptionHandle.cs` | **NEW** | public; idempotent `CloseAsync` delegates to client (AC-4.2.2). |
| `src/ohSpy.Core/Events/ISubscriptionClient.cs` | **NEW** | public seam (AC-4.2.1). |
| `src/ohSpy.Core/Events/SubscriptionClient.cs` | **NEW** | `internal sealed : ISubscriptionClient`; SID map, renew loops, parse, close (AC-4.2.5..4.2.18). |
| `src/ohSpy.App/Composition/ServiceRegistration.cs` | UPDATE | register `ISubscriptionClient`→`SubscriptionClient` singleton (near `IEventCallbackHost` L81). |
| `src/ohSpy.Core/ViewModels/ShellViewModel.cs` | UPDATE | new `ISubscriptionClient` ctor arg; `SetAdapterContext(scope.AdapterToken)` after `_callbackHost.StartAsync` in `RunStartAsync` (L75). |
| `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs` | UPDATE | make `SubscribeAsync`/`RenewSubscriptionAsync`/`UnsubscribeAsync` controllable responders + record calls (mirror `InvokeResponder`/`InvokedRequests`, L88-97). |
| `tests/ohSpy.Core.Tests/Fakes/FakeEventCallbackHost.cs` | **NEW** | exposes a settable `CallbackBaseUrl` + a method to raise `NotifyReceived(req)` and await its handlers (drive routing/race/non-serial tests). |
| `tests/ohSpy.Core.Tests/Events/SubscriptionClientTests.cs` | **NEW** | the AC matrix (Task 8). |

`InternalsVisibleTo` already grants `ohSpy.Core.Tests` + `ohSpy.App` (`ohSpy.Core.csproj`) — `internal` impl is testable + App-resolvable.

### Canonical precedents to mirror

- **`UpnpHttpClient`** (`src/ohSpy.Core/Http/UpnpHttpClient.cs`) — ctor shape (`IDiagnosticEmitter`), linked-CTS-per-op, `catch (OCE) when (external.IsCancellationRequested)` re-throw vs. timeout-branch, `ConfigureAwait(false)` throughout.
- **`EventCallbackHost`** (`src/ohSpy.Core/Events/EventCallbackHost.cs`) — internal-test-ctor for an injectable budget (precedent for the renew-delay seam), `Task.Run` long-loop + VSTHRD pragma, linked-CTS disambiguation (shutdown-cancel vs. budget-fire), idempotent `Interlocked`-guarded teardown, swallow-OCE-on-shutdown.
- **`InvocationPopupViewModel`** (`src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs`) — relative-URL resolve `new Uri(parentEntry.LocationUrl, …)` (L98 control-url; 4.2 does the same for `EventSubUrl`), the intentional UUID-bearing duplicate diagnostic (L200-214), Interlocked dispose, the explicit "GENA unsubscribe is Epic 4" note (L397-398).
- **`AdapterScope.DisposeAsync`** (`src/ohSpy.Core/Discovery/AdapterScope.cs` L97-132) — the 5 s/2 s budgeted-WaitAsync teardown shape.

### Verification posture (retro Action J / H)

- **Headless Core orchestration → NO manual-UI-smoke gate.** No UI, no bound VM, no `IUiDispatcher` collection mutation (Story 4.1 / 3.1 posture). State this so the dev adds no smoke task. Retro **Action H** (`DeferredUiDispatcher` per async VM path) folds into **4.3**, NOT 4.2 — 4.2 has no bound VM state; its `NotificationReceived`/`Lapsed` are raw `Action<…>` events and 4.3 owns the marshalling.
- **Strong automated surface (the lifecycle contract IS the test contract).** Mocked `IUpnpHttpClient` (controllable SUBSCRIBE/RENEW/UNSUBSCRIBE responders + recorded calls) + a faked `IEventCallbackHost` driving `NotifyReceived` → exercises happy lifecycle, auto-renew firing (testable seam, AC-4.2.16), renew-failure→lapsed, failed-subscribe-no-unsubscribe, UNSUBSCRIBE-on-active-close (level-above token asserted), lapsed-close-no-unsubscribe, adapter/device cancellation, the NOTIFY-before-SID race, propertyset parse (+ malformed-swallow), the FR-104 non-serial drill, and concurrent-independent subscriptions. No real device required.
- **Real-device SUBSCRIBE + NOTIFY smoke is a 4.3 forward item** (retro Action J): end-to-end eventing needs the popup (4.3) + a device that **emits** events (Linn DS — Sky IGD eventing for `WANIPConnection` unconfirmed), reachable via the **Action-I dev-adapter override** (env var `OHSPY_ADAPTER=<name|index>`). Record; do not block 4.2 on it.

### Previous-story / epic intelligence

- **Story 4.1 (done, reviewed)** shipped the callback host (the `NotifyReceived`/`CallbackBaseUrl` seam 4.2 consumes); baseline **443 passed / 2 skipped**; chaos 1; CoreAppBoundary + AsyncDiscipline + DiagCategoriesUsage green. 4.1 explicitly left the callback-path-token decision (open-Q#4), the `NotifyRequest` raw-bytes hand-off, and the real-device NOTIFY smoke to 4.2/4.3.
- **Epic 3** established: reconcile-against-shipped-code (every story's prose diverged); the intentional UUID-bearing duplicate diagnostic; the WinUI marshalling lesson (4.3's concern, not 4.2's).
- **Story 5.2 (adapter switch)** is re-sequenced to the END of Epic 4 (after 4.3); its atomic rebind cancels `_adapterCts` (cascading into 4.2's renew loops → `AdapterSwitch` lapse) and disposes/recreates the host → 4.2 must re-`SetAdapterContext`. Keep tokens linked to `_adapterToken`.

### Open questions for the implementer

1. **Initial requested lease (300 s) + renew margin (80 %).** Both are 4.2 constants (not `HttpTimeoutOptions`). Confirm 300 s is within UPnP norms for the target devices; pick & document the exact margin formula. Verify against a real Linn DS during the 4.3 smoke.
2. **Renew-timing test seam:** internal `Func<TimeSpan,CancellationToken,Task>` delay seam (recommended) vs. tiny-lease responder. Pick one (or both for different tests); document.
3. **`SetAdapterContext` seam vs. reading the token off the host.** Recommended: an explicit `SetAdapterContext(adapterToken)` called in `RunStartAsync` after `_callbackHost.StartAsync`. Confirm with the reviewer; note the 5.2 re-context requirement.
4. **Per-subscription bounded worker shape (AC-4.2.9).** A dedicated `Channel<NotifyRequest>` per subscription vs. a `Task.Run` per NOTIFY with a per-subscription gate. Either satisfies non-serial-across-subscriptions; document the chosen back-pressure (epic L1664: bounded, FIFO tail-eviction, no device back-pressure).
5. **Malformed-propertyset diagnostic.** AC-4.2.8 swallows the bad NOTIFY. Decide whether to emit a Verbose diagnostic (no new constant — could reuse none / log nothing). Recommend: drop silently or Verbose only; do NOT lapse.

### References

- [Source: epics.md#Story 4.2] (L1583-1670) — story statement, full AC list (surface, happy-path, failed-subscribe FR-035, auto-renew FR-038, level-above-token UNSUBSCRIBE, lapse reasons, NOTIFY routing + propertyset parse FR-104, non-serial drill, DI singleton).
- [Source: epics.md#Epic 4 scope] (L1489-1491) — concurrent/independent subscriptions, auto-renew, UNSUBSCRIBE on close, lapsed handling, failed-subscribe-no-unsubscribe.
- [Source: architecture.md#Decision 4 — GENA Callback Host Hardening Contract] (L400-507) — host seam (`CallbackBaseUrl`, `NotifyReceived`, raw `NotifyRequest`), "host does NOT parse `<e:propertyset>`" (L464), idempotent-ack-on-unknown-SID (L444).
- [Source: architecture.md#Cancellation hierarchy + cleanup-uses-level-above-token] (L740-849) — D7 token graph `popup ⊂ device ⊂ adapter`; the UNSUBSCRIBE-on-close level-above invariant verbatim (L790-816); adapter-switch atomic sequence (L818-831).
- [Source: architecture.md#GENA callback flow] (L2286-2299) — device → host → `SubscriptionClient` routes by SID → parse propertyset → `EventNotification`.
- [Source: architecture.md#Source tree] (L1739, L2111-2112, L2191) — `Events/SubscriptionClient.cs`; `EventNotification.cs` (placement reconciled to `Models/` per epic AC); FR 4.10 mapping.
- [Source: src/ohSpy.Core/Http/IUpnpHttpClient.cs#L42-61] + [UpnpHttpClient.cs#L230-354] — the shipped `SubscribeAsync`/`RenewSubscriptionAsync`/`UnsubscribeAsync` + `SendSubscribeOrRenewAsync` + `ParseSecondHeader` (412→`UpnpTransportException(StatusCode)`; UNSUBSCRIBE throws-on-failure).
- [Source: src/ohSpy.Core/Http/SubscribeResponse.cs] — `(string Sid, TimeSpan Timeout)`; `Timeout` is the granted lease.
- [Source: src/ohSpy.Core/Http/HttpTimeoutOptions.cs#L14-15, L32] — `GenaSubscribe`/`GenaUnsubscribe` (5 s) per-request budgets (consumed by the verbs); `MaxGenaResponseBytes` (outbound cap, already enforced).
- [Source: src/ohSpy.Core/Events/IEventCallbackHost.cs + NotifyRequest.cs] — `CallbackBaseUrl` (Uri) + `event Func<NotifyRequest, Task> NotifyReceived` + raw `(Sid, Seq, PathAndQuery, byte[] Body, ReceivedUtc)`.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs#L55-69] — the six pre-added outbound `Gena*` constants (no new constant).
- [Source: src/ohSpy.Core/Scpd/UpnpXmlReaderSettings.cs] — the shared XXE-locked `XmlReaderSettings.Create()` for the propertyset parse (Story 1.4 discipline).
- [Source: src/ohSpy.Core/Devices/RegistryEntry.cs#L20-79] — `Uuid`, `LocationUrl`, public `DeviceToken` (snapshot, safe after dispose).
- [Source: src/ohSpy.Core/Models/ServiceDescription.cs] — `EventSubUrl` (possibly-relative string).
- [Source: src/ohSpy.Core/Discovery/AdapterScope.cs#L35-38, L97-132] — `AdapterToken` source + budgeted teardown shape.
- [Source: src/ohSpy.Core/ViewModels/ShellViewModel.cs#L64-88] — `RunStartAsync` host-start point (where `SetAdapterContext` wires in).
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs#L75-81] — `IEventCallbackHost` singleton registration precedent.
- [Source: src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs#L98, L200-214, L394-406] — relative-URL resolve, UUID-bearing duplicate diagnostic, "GENA unsubscribe is Epic 4" note.
- [Source: tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs#L88-97] — the GENA-verb stubs to make controllable (currently `throw new NotSupportedException()`).
- [Source: epic-3-retro-2026-06-04.md#Action items] — H (`DeferredUiDispatcher` → 4.3, not 4.2), I (dev adapter override), J (GENA event-smoke → 4.3), 5.2 re-sequencing.
- [Source: 4-1-event-callback-host-…md] — the shipped 4.1 seam + open-Q#4 (callback-path token punted to 4.2) + raw-bytes hand-off + no-manual-smoke posture.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context) — bmad-dev-story workflow, fresh context.

### Debug Log References

- Core `dotnet build`: 0 warnings / 0 errors (after fixing one CA1859 — propertyset parser return type narrowed `IReadOnlyDictionary` → `Dictionary`).
- Test build: fixed VSTHRD200 ("Async" suffix) on the `NeverDelayAsync`/`WaitUntilAsync` helpers; converted two local `Task Delay(...)` functions to `Func<…>` lambda variables (analyzer flags awaitable-returning local *methods*, not lambdas).
- Flakiness hunt: the NOTIFY-before-SID race test failed ~1/5 under full-suite load — a worker-drain-vs-handler-attach race. Closed it in the IMPL (not the test) with a handle-level replay buffer (see Completion Notes). 5× full-suite reruns then clean.

### Completion Notes List

**5 open questions resolved:**
1. **Initial lease + renew margin.** `InitialLease = 300 s` (UPnP norm; device may grant less, we renew off the GRANTED value). `RenewDelayFor(granted) = min(0.8×granted, granted−30s)`, floored at `MinRenewDelay = 1 s`. For 300 s → 240 s (80% dominates; asserted in `RenewDelay_UsesEightyPercentOfGrantedLease`). The `granted−30s` arm gives a short lease a sane head start; the floor guards a pathological tiny lease.
2. **Renew-timing seam.** Chose the internal `Func<TimeSpan,CancellationToken,Task> _delay` seam (defaults to `Task.Delay`), injected via an internal test ctor — mirrors the `EventCallbackHost` internal-test-ctor precedent. Auto-renew is unit-tested with NO real waits (immediate-fire / gated-release delays). The tiny-lease responder option was not needed.
3. **`SetAdapterContext` vs reading off the host.** Chose the explicit `SetAdapterContext(adapterToken)` seam called from `ShellViewModel.RunStartAsync` right after `_callbackHost.StartAsync`. Keeps the level-above token explicit and gives Story 5.2 a single re-context point.
4. **Per-subscription worker shape.** Chose a bounded `Channel<NotifyRequest>` (capacity 256, `FullMode = DropOldest` → FIFO tail-eviction, no device back-pressure, `SingleReader`) with one drain-loop `Task.Run` worker per subscription. The `NotifyReceived` host handler only does a SID lookup + `TryWrite` and returns promptly; all parse/dispatch happens off the host's awaited task and per-subscription (proves the FR-104 non-serial drill: A's 200 ms parse does not delay B).
5. **Malformed-propertyset diagnostic.** Swallow the one bad NOTIFY at **Verbose** (reusing the existing `GenaNotifyReceived` constant — NO new constant). Never lapse, never crash the host's awaited handler. Asserted by `Propertyset_Malformed_Swallowed_NoLapse_NoCrash`.

**Prompt lapse on device-gone / adapter-switch (D7).** Beyond the renew-loop's OCE-on-cancel path, the subscription registers cancellation callbacks on the device + adapter tokens (`token.Register(...)`) so a device-gone/adapter-switch lapses the subscription PROMPTLY — independent of whether the renew-loop `Task.Run` has even been scheduled. `Lapse` is idempotent (Interlocked guard), so whichever path fires first wins. This also hardened the tests against threadpool-starvation flakiness under xUnit's parallel test execution (the renew-loop task could otherwise be starved for >30 s under CPU contention; the callback fires on the canceller's thread). The `SubscriptionHandle` also REPLAYS a pre-attach lapse/notification to the first subscriber (belt-and-braces for the consumer that attaches synchronously post-await). 30× full-suite reruns clean after this.

**D7 level-above-token UNSUBSCRIBE — confirmed.** `CloseAsync` on an ACTIVE subscription cancels the renew-loop CTS (popup-derived) but builds a FRESH `CancellationTokenSource(5s)` linked to `_adapterToken` ONLY for the UNSUBSCRIBE — never the just-cancelled popup token. Test `ActiveClose_Unsubscribes_WithAdapterLinkedToken_NotCancelledPopupToken` cancels the popup token, then closes, and asserts (a) UNSUBSCRIBE fired, (b) the token it ran under was NOT cancelled, (c) the adapter token was NOT cancelled by the popup close. Failed UNSUBSCRIBE is swallowed + `GenaUnsubscribeFailed` (close must not block on a hung device).

**NOTIFY-before-SID race — covered (two mechanisms).** (a) The `NotifyReceived` handler buffers an unmatched NOTIFY into every in-flight subscribe's pending queue while the SUBSCRIBE is in flight; at SID registration the matching-SID events are drained into the channel. (b) `SubscriptionHandle` additionally buffers any notification raised before the first subscriber attaches and flushes it on subscription — this closed a worker-drain-vs-handler-attach race that surfaced only under full-suite load. Test `NotifyBeforeSid_Race_IsBufferedAndReplayed` fires a NOTIFY while SUBSCRIBE is gated in-flight and asserts the event reaches the handle.

**Auto-renew unit-tested via the delay seam (no real waits) — confirmed.** `AutoRenew_FiresBeforeExpiry_AndReschedules_ViaDelaySeam` drives a fast-forward delay and proves ≥3 renews reschedule across leases; `RenewDelay_UsesEightyPercentOfGrantedLease` asserts the exact 240 s delay. Renew-failure tests gate the delay on a release TCS so the `Lapsed` handler is attached before the lapse fires (deterministic).

**Reconciliations honoured:** orchestrates the shipped 1.3 verbs (no SUBSCRIBE rebuild); 412 branched via `UpnpTransportException.StatusCode == 412`; `UnsubscribeAsync` throws-on-failure → `CloseAsync` is the swallow layer; `EventNotification` placed in `Models/` (epic AC) not `Events/` (arch tree); propertyset parsed with the shared XXE-locked `UpnpXmlReaderSettings.Create()`; UUID-bearing `Gena*` emits atop the verb-layer's uuid-less `HttpTransport`/`HttpTimeout` (intentional-duplicate pattern); NO new `DiagCategories` constant (DiagCategories.cs untouched, `DiagCategoriesUsageTests` unchanged + green).

**Verification:** Core `dotnet build` 0/0; full suite **462 passed / 2 skipped / 0 failed** (5× clean reruns); chaos 1; App build 1 pre-existing benign `WMC1506` (MainWindow.xaml:141), no new warnings. No `new ShellViewModel` test sites broke. NOT committed.

**Follow-ups for the reviewer:**
- The handle-level replay buffer flushes to the FIRST subscriber only (matches the single-consumer 4.3 popup VM). If a future consumer attaches multiple handlers before any event, only the first attach drains the buffer — acceptable for v1 (one popup, one handler); confirm.
- `RenewRequestedLease` is a separate 300 s const from `InitialLease` (same value today) — kept distinct in case the renew-requested lease ever diverges from the initial. Confirm acceptable or collapse.
- Verify the 300 s initial lease + 80% margin against a real Linn DS during the Story 4.3 smoke (Open Q1).

### File List

**New (Core):**
- `src/ohSpy.Core/Models/EventNotification.cs`
- `src/ohSpy.Core/Events/SubscriptionLapseReason.cs`
- `src/ohSpy.Core/Events/SubscriptionHandle.cs`
- `src/ohSpy.Core/Events/ISubscriptionClient.cs`
- `src/ohSpy.Core/Events/SubscriptionClient.cs`

**New (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/FakeEventCallbackHost.cs`
- `tests/ohSpy.Core.Tests/Events/SubscriptionClientTests.cs`

**Modified:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — `ISubscriptionClient`→`SubscriptionClient` singleton.
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — `ISubscriptionClient` ctor arg + `SetAdapterContext` call.
- `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs` — controllable GENA responders + `GenaCalls` recording.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `4-2 → review`.

**Untouched (verified):** `src/ohSpy.Core/Diagnostics/DiagCategories.cs` (no new constant).

### Review Findings

_Code review by bmad-code-review, 2026-06-04. Reviewer: claude-sonnet-4-6 (Sonnet 4.6). Layers: Blind Hunter + Edge Case Hunter + Acceptance Auditor (all internal). Verdict: **APPROVED-WITH-MINOR-FIXES**._

- [x] [Review][Patch] ✅ APPLIED 2026-06-04 — Lapse+CloseAsync TOCTOU — potential UNSUBSCRIBE sent for a lapsed subscription [`src/ohSpy.Core/Events/SubscriptionClient.cs`:484] — `CloseAsync` reads `_lapsed` via `Volatile.Read` before it cancels `_loopCts`. If `Lapse()` fires concurrently between the `Volatile.Read(_lapsed)` and the Interlocked exchange inside `Lapse` (i.e. both see `_lapsed == 0`), `wasLapsed` is captured as `false` and `CloseAsync` will attempt to UNSUBSCRIBE a subscription that `Lapse()` is simultaneously marking as lapsed. Fix: re-read `_lapsed` with `Volatile.Read` immediately before the UNSUBSCRIBE block (after the `_loopCts.CancelAsync` await), replacing the stale `wasLapsed` capture. This closes the window from ~10 lines to near-zero and does not require a new primitive.
- [x] [Review][Patch] ✅ APPLIED 2026-06-04 — `CloseAsync` does not await `_renewLoop` or `_notifyWorker` — post-close notifications possible [`src/ohSpy.Core/Events/SubscriptionClient.cs`:479] — After completing the channel writer and calling `DisposeResources`, `CloseAsync` returns without awaiting the notifyWorker task. The worker may still be processing an already-queued `NotifyRequest` and will fire `Handle.RaiseNotification` after `CloseAsync` has returned. For 4.3's popup VM this may raise a marshalled event after popup teardown. For this story's scope (headless Core) it is safe, but the `CloseAsync` contract says teardown — callers may assume clean stop. Recommended fix: await `_notifyWorker` (with a bounded timeout, e.g. 2 s) after completing the channel writer, mirroring `AdapterScope.DisposeAsync`'s budgeted-WaitAsync pattern. The renew loop is already signalled via `_loopCts.CancelAsync` (which unblocks the delay). Awaiting it here is also advisable for clean unit-test teardown. Note: this may require `_notifyWorker` / `_renewLoop` to be stored as non-null before awaiting.
- [x] [Review][Defer] `_adapterToken` plain (non-volatile) struct field accessed cross-thread [`src/ohSpy.Core/Events/SubscriptionClient.cs`:79,111] — `SetAdapterContext` writes a `CancellationToken` struct field; `StartRenewLoop` reads it later on a Task.Run thread. Formally requires a memory barrier for C# memory model correctness, though on .NET/x64 TSO makes this safe in practice. `CancellationToken` cannot be `volatile` (it is a struct). A formal fix would store `_adapterToken`'s underlying `CancellationTokenSource` as a reference type (which can be `volatile`). Deferred: `SetAdapterContext` is called from `RunStartAsync` which completes before any `SubscribeAsync` call is possible (single-threaded startup sequence in `ShellViewModel`), so the happens-before is established in practice. Tag for Story 5.2 (adapter-switch rebind adds a second `SetAdapterContext` call under potential concurrency). — deferred, pre-existing architecture constraint; safe in current single-threaded startup flow; revisit in Story 5.2.
- [x] [Review][Defer] `SubscriptionClient` has no `IAsyncDisposable` / `DisposeAsync` — live subscriptions are silently abandoned at app shutdown [`src/ohSpy.Core/Events/SubscriptionClient.cs`:30] — If the app exits while subscriptions are live, their renew loops exit via `OperationCanceledException` (adapter token cancelled) → `AdapterSwitch` lapse (no UNSUBSCRIBE, correct per D7). But `_notifyWorker` tasks are not awaited by anyone at shutdown. `ShellViewModel.DisposeAsync` awaits `_callbackHost.DisposeAsync` but not `_subscriptionClient` (there is no such method). This is acceptable for v1 because the adapter-token cancel cascades the lapse and the device will time out the subscription. Tag for Story 5.2 or an Epic 4 cleanup pass. — deferred, acceptable v1 posture; adapter-token cancel cascades correctly; 5.2 re-context note already in the code.

**Patches applied (2026-06-04, Opus main session):** `CloseAsync` restructured — after `_loopCts.CancelAsync()` + `_channel.Writer.TryComplete()` it now awaits the renew loop then the NOTIFY worker via a bounded `AwaitBoundedAsync(Task?)` helper (`DrainBudget = 2 s`, swallows timeout/fault — `EventCallbackHost.DisposeAsync` `WaitAsync` precedent, `#pragma warning disable VSTHRD003` for the own-task await), so no `RaiseNotification`/`RaiseLapsed` can fire after `CloseAsync` returns (Patch 2 — protects 4.3's `_ui.Post`-marshalled handler from a post-teardown event). The `wasLapsed` read moved to the latest point — after the loop drains, immediately before the UNSUBSCRIBE decision — so a concurrent device-gone/adapter-switch `Lapse` is observed and no UNSUBSCRIBE is sent for an already-lapsed sub (Patch 1, window now near-zero; the renew-loop await also means any renew-failure `Lapse` has completed first). Verification: Core `-warnaserror` 0/0; full suite 462 passed / 2 skipped / 0 failed; the 19-test SubscriptionClient suite green on 6 consecutive runs (no introduced flake). Both reviewer DEFER items remain deferred (logged in `deferred-work.md`, tagged Story 5.2). A deterministic regression test for Patch 2 was NOT added — proving "no event fires after CloseAsync returns" requires a parse-path delay seam that doesn't exist; the existing active-close / lapsed-close / FR-104 close-path tests + the 6× stability runs cover it.

**Dismissed (5):** `_subscribedToHost` Interlocked guard is redundant but harmless (correct per-instance guard); over-buffering of pending NOTIFYs to all in-flight subscribes (correct behavior, SID filter applied at drain); `WaitUntilAsync` polling loop in tests (guarded by timeout, test-only); `InitialLease`/`RenewRequestedLease` separate constants with same value (explicitly justified by dev as forward-compatibility); `GenaNotifyReceived` reuse for malformed propertyset (explicitly permitted by AC-4.2.8 "optional Verbose diagnostic — no new constant").
