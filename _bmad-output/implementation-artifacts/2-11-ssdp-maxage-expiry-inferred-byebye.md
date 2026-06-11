---
baseline_commit: d0ad292534255f2a4b57a223fbe950bd0027d5ec
---

# Story 2.11: SSDP max-age expiry (inferred byebye)

Status: review

<!-- Corrective story (correct-course). Requirements source: sprint-change-proposal-2026-06-11.md §4.1 (FR-056) + §4.3 (Story 2-11), NOT epics.md. The story also authors the PRD FR-056 + an Architecture Amendment as tasks (the 2.10 precedent). -->

## Story

As a **ohSpy operator on a real UPnP network**,
I want **a device that is pulled off the network WITHOUT sending `ssdp:byebye` to disappear from the tree once its advertised `CACHE-CONTROL: max-age` lease lapses**,
so that **stale devices age out automatically (inferred byebye, UDA 1.0 §1.2.2) instead of lingering in the tree indefinitely**.

## Problem Statement

A device pulled off the network (power yanked, cable unplugged) WITHOUT a graceful `ssdp:byebye` is **never removed**. The `CACHE-CONTROL: max-age` lease **IS** parsed and stored — `SsdpParser.ParseMaxAge` → `SsdpAnnouncement.CacheControlMaxAge` → `DiscoveryService.OnAlive` → `RegistryEntry.CacheControlMaxAge` + `LastSeenUtc` (refreshed by `RefreshSsdpMetadata` on every alive) — but **nothing evicts on it**.

The only removal paths shipped today (all verified in code 2026-06-11):
- `DeviceRegistry.OnByebye(udn)` — graceful leave (FR-008), byebye only.
- `DeviceRegistry.PruneNotSeenSince(epochUtc)` — the **manual** Story 5.3 Rescan prune (single epoch).
- `DeviceRegistry.Clear()` — adapter-switch reset (Story 5.2, FR-050 step 6).

FR-008 is **byebye-only**. Standard UDA behaviour — a device promises to re-advertise within its `max-age`; a control point evicts when that lease lapses with no refresh — was never required. **FR-056 (new, authored by this story)** closes the gap: evict when `now > LastSeenUtc + lease` with no refreshing `alive`. The fix is a **periodic expiry sweep** that reuses the EXISTING `RemoveCore` cascade (cancel `DeviceCts` + raise `DeviceRemoved(udn)` — the same path as byebye/prune, so the FR-037 popup banners + in-flight-fetch cancellation just work). **It is the automatic cousin of `PruneNotSeenSince`.**

**Scope: PURE CORE** (no UI redesign). One App touch only if the sweep needs to be started at app launch (it does not — it hangs off the existing `DiscoveryService.StartAsync`/`RebindAsync` already called by `ShellViewModel.StartBoundServicesAsync`).

## Acceptance Criteria

1. **Per-entry lease expiry.** A registered device is evicted from the registry when `now > LastSeenUtc + lease`, where `lease = CacheControlMaxAge` for that entry. The eviction uses the **same `RemoveCore` cascade** as byebye: the entry leaves the registry, its `DeviceCts` is cancelled + disposed (cancels any in-flight description/SCPD fetch, AC-7.2), and `DeviceRemoved(udn)` is raised (so the tree drops the row and open popups flip to their FR-037 device-gone banners).
2. **A refreshing alive resets the lease.** A subsequent `ssdp:alive` (or M-SEARCH response) refreshes `LastSeenUtc` via the existing `OnAlive` → `RefreshSsdpMetadata` path; the device survives every sweep so long as it re-advertises within its lease. No new entry, no re-fetch (FR-043 unchanged).
3. **byebye still wins immediately.** A `byebye` removes the device at once via `OnByebye`, independent of any lease; the sweep does not delay or override graceful removal. The expiry sweep is idempotent with byebye/Rescan/Clear (all share `RemoveCore.TryRemove` — an already-removed UDN raises no second `DeviceRemoved`).
4. **Default lease for missing CACHE-CONTROL.** When an entry's latest alive omitted `max-age` (`CacheControlMaxAge` is `null` — non-conformant but seen in the wild), a sensible **default lease of 1800 s** (the UDA 1.0 §1.2.2 example) applies so the device still expires rather than living forever. (Grace + default constants — see Dev Notes §"The three design decisions".)
5. **Grace = 1× max-age + a small jitter tolerance.** Eviction occurs at `LastSeenUtc + lease + jitter`, where `jitter` is a small fixed tolerance (see Dev Notes) so a device re-advertising right at its lease edge is not evicted by routing-latency / clock-skew jitter. The device promised to re-advertise within `max-age` (UDA recommends `< ½ max-age`), so `1× max-age` is a conservative, non-aggressive eviction point.
6. **The sweep is periodic and non-blocking.** The check runs on a periodic timer/loop owned by `DiscoveryService` (which already holds the registry + the read-loop lifecycle). It MUST NOT block the SSDP read loop, the GENA listener, or the UI thread. The registry mutation (eviction) is **marshalled onto the UI thread via `IUiDispatcher.Post`** (the registry is UI-thread-owned — `DeviceRegistry` mutators `AssertOnUiThread`).
7. **The sweep starts + stops with the adapter scope.** The sweep is started by `DiscoveryService.StartAsync` and re-started by `RebindAsync` (bound to the adapter token), and STOPS on adapter switch (the old adapter token cancels) / teardown (`DisposeAsync`). After an adapter switch the sweep runs against the fresh scope only — it never evicts against a torn-down or replaced registry from a stale timer thread.
8. **Test seam: virtual clock + settable interval.** "Now" and the sweep interval are injectable (the `SubscriptionClient._delay` / `ShellViewModel._rescanDelay` precedent + a `Func<DateTime>` clock) so the expiry logic is unit-testable instantly — NO real multi-minute waits in any test.
9. **Diagnostic on expiry.** When the sweep evicts a device it emits a diagnostic (new `DiagCategories.SsdpExpired` = `"Ssdp.Expired"`, Information severity, carrying the device UDN + the elapsed-since-LastSeen / lease in context) so the operator can see in the FR-041 Diagnostics viewer *why* a device left (distinct from byebye / Rescan / adapter-switch removals). This is a **pinned-set change** — see AC #10.
10. **DiagCategories pinned-set synced.** The new `DiagCategories.SsdpExpired` constant is added to ALL THREE pinned locations together (the 5.1 `SsdpSearchObserved` / 5.3 `Rescan` precedent): `DiagCategories.cs`, the exact-set array in `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` (`expectedNames`), AND the architecture Decision 8 / Pattern-11 list. The `DiagCategoriesTests` exact-set + usage guards stay green. Flagged to the reviewer as INTENTIONAL, not drift.
11. **Action H marshalling guard.** A `DeferredUiDispatcher`-based test proves the eviction is applied THROUGH `IUiDispatcher.Post` (NOT inline): under a deferred dispatcher the registry is NOT mutated and no `DeviceRemoved` fires until the UI thread drains. (Memory `winui-no-synccontext-marshal-vm`: WinUI 3 has no SynchronizationContext; a timer-thread continuation mutating bound state without marshalling crashes RPC_E_WRONGTHREAD. `InlineUiDispatcher` masks it — the guard MUST use the deferred dispatcher.)
12. **PRD FR-056 authored.** FR-056 (text in Dev Notes §"PRD FR-056") is inserted into the PRD after FR-008.
13. **Architecture Amendment authored.** A new Amendment (Decision 9 / DiscoveryService-lifecycle expiry sweep — text in Dev Notes §"Architecture Amendment") is appended after A32.
14. **Suite green.** The full Core suite passes (`-warnaserror` 0/0), including the new tests below; `CoreAppBoundaryTests` still forbids `Core → App`; the chaos hook stays green.

## Tasks / Subtasks

- [x] **Task 0 — Settle the three design decisions + the sweep home (do FIRST).** (AC: #4, #5, #6, #7)
  - [x] Confirm (and record in the Dev Agent Record before coding): **grace** = `1× max-age + ExpiryJitter` (a small fixed tolerance, default ~`5 s`); **default lease** = `1800 s` when `CacheControlMaxAge` is null; **diagnostic** = a NEW `DiagCategories.SsdpExpired` constant. See Dev Notes §"The three design decisions".
  - [x] Confirm the sweep lives in `DiscoveryService` (it ctor-injects `DeviceRegistry registry` + `IUiDispatcher ui` already and owns the read-loop lifecycle via `StartAsync`/`RebindAsync`/`DisposeAsync`). Do NOT add a second timer to `ShellViewModel` (Rescan is operator-triggered; this is periodic). See Dev Notes §"Where the sweep lives".
- [x] **Task 1 — Add the per-entry expiry predicate + the registry method.** (AC: #1, #4, #5)
  - [x] Add `RegistryEntry.IsExpiredAt(DateTime nowUtc, TimeSpan defaultLease, TimeSpan jitter)` (or compute the lease inline in the registry method — pick one; the entry already exposes `LastSeenUtc` + `CacheControlMaxAge`, both shipped). Lease = `CacheControlMaxAge ?? defaultLease`; expired iff `nowUtc > LastSeenUtc + lease + jitter`.
  - [x] Add a NEW `IDeviceRegistry` method `int ExpireOlderThan(DateTime nowUtc, TimeSpan defaultLease, TimeSpan jitter)` (do NOT generalise `PruneNotSeenSince` — it uses a single epoch; expiry needs a PER-ENTRY lease, so a new method is cleaner — see Dev Notes §"New vs generalised registry method"). Mirror `PruneNotSeenSince` EXACTLY: `AssertOnUiThread`; snapshot `_entries.Keys.ToArray()` FIRST; for each, re-read the live entry and, if expired, `RemoveCore(udn)` + count; return the count. Idempotent; safe on an empty registry. Add the XML-doc in the `PruneNotSeenSince` style (note it is the automatic per-entry-lease cousin, byebye-identical cascade).
  - [x] Reuse `RemoveCore` unchanged (cancel + dispose `DeviceCts` + raise `DeviceRemoved`). Do NOT touch `OnAlive`/`RefreshSsdpMetadata`/`OnByebye`/`Remove`/`Clear`/`PruneNotSeenSince`.
- [x] **Task 2 — Add the diagnostic constant (pinned-set, all three sites together).** (AC: #9, #10)
  - [x] `DiagCategories.cs`: add `public const string SsdpExpired = "Ssdp.Expired";` in the SSDP block, with an XML-doc (Information severity; mandatory context: DeviceUuid; ErrorText carries the elapsed/lease reason). Distinct from byebye (FR-008, no diagnostic), Rescan (`Adapter.Rescan`), and adapter-switch (`Adapter.Switch`).
  - [x] `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs`: add `"SsdpExpired"` to the `expectedNames` array (in the SSDP group).
  - [x] Architecture Decision 8 list + Pattern-11 context table (whichever the 5.3 `Rescan` constant was added to): add the `SsdpExpired` row. (Same edit lands as part of Task 7's amendment — keep them consistent.)
- [x] **Task 3 — The expiry sweep in DiscoveryService (clock + interval seam).** (AC: #6, #7, #8, #9, #11)
  - [x] Inject TWO seams into `DiscoveryService` (defaulted so production is unchanged; `InternalsVisibleTo` test setters or ctor params — match the `SubscriptionClient`/`ShellViewModel` precedent): a `Func<DateTime>` clock (default `() => DateTime.UtcNow`) and a `Func<TimeSpan, CancellationToken, Task>` delay (default `(d, ct) => Task.Delay(d, ct)`). Also a settable sweep **interval** (default ~`30 s` — see Dev Notes §"Sweep interval") + the `DefaultLease` (1800 s) + `ExpiryJitter` (~5 s) as constants or test-settable fields.
  - [x] Start a sweep loop alongside the read loop. Recommended shape: in `StartAsync` (and via `RebindAsync` → `StartAsync`), after kicking the read loop, kick a second `Task.Run(() => SweepLoopAsync(adapterToken, ct))` bound to the SAME linked token. The loop: `while (!cancelled) { await _delay(_sweepInterval, linkedToken); var now = _clock(); _ui.Post(() => { var n = registry.ExpireOlderThan(now, DefaultLease, ExpiryJitter); /* emit SsdpExpired per eviction OR a single summary */ }); }`. Catch `OperationCanceledException` (normal shutdown — adapter switch / teardown). See Dev Notes §"Sweep loop shape" for the per-eviction-vs-summary diagnostic decision.
  - [x] **Lifecycle**: the sweep loop MUST be tracked (a `_sweepLoop` Task field) and drained in `RebindAsync` (alongside the read-loop drain) and `DisposeAsync` so a stale sweep never mutates a torn-down/replaced registry. The single-start guard (`_started`) already gates re-entrancy; reset it on rebind exactly as the read loop does.
  - [x] **Diagnostic**: `DiscoveryService` ctor-injects `DeviceRegistry`, NOT `IDiagnosticEmitter` (the registry deliberately avoids the emitter to dodge a DI cycle — but `DiscoveryService` is NOT in that cycle). Add `IDiagnosticEmitter` to the `DiscoveryService` ctor (it is a free-standing singleton; verify no cycle via `CoreAppBoundaryTests` / the DI graph) so the sweep can emit `SsdpExpired`. ALTERNATIVELY emit from the registry method if the emitter is already reachable there — but it is NOT (see the `DeviceRegistry` class-doc cycle note); emit from `DiscoveryService` after the marshalled `ExpireOlderThan` returns the evicted UDNs. **Decision**: have `ExpireOlderThan` return the evicted UDNs (or count) and emit from the `_ui.Post` lambda in `DiscoveryService`. Reconcile the exact emit shape in the Dev Agent Record.
- [x] **Task 4 — Wire the seams (no production behaviour change).** (AC: #6, #14)
  - [x] `DiscoveryService` stays a DI singleton (`ServiceRegistration.cs:133`). If a new ctor param (`IDiagnosticEmitter`) is added, it auto-resolves (already registered). The clock/delay/interval seams default inline (no DI change) — `InternalsVisibleTo` test setters, the `ShellViewModel.SetRescanDelayForTest` precedent. Confirm NO change to `ShellViewModel.StartBoundServicesAsync` is required (it already calls `_discovery.StartAsync`/`RebindAsync` which now also start the sweep).
- [x] **Task 5 — Tests (Core only — see Dev Notes §"Test plan").** (AC: #1–#11, #14)
  - [x] `DeviceRegistryTests`: device with lease L evicted after L (`ExpireOlderThan` with a virtual `now` past `LastSeenUtc + L + jitter`) raises `DeviceRemoved(udn)` + cancels its `DeviceCts`; a refreshed entry (LastSeenUtc bumped) survives; default-lease path for a null `CacheControlMaxAge` entry; jitter-edge case (just inside lease → survives, just past lease+jitter → evicted); idempotent (already-removed UDN → no second `DeviceRemoved`); empty registry → 0.
  - [x] `DiscoveryServiceTests`: the sweep loop, driven via the injected clock + delay seam (instant), evicts a stale entry via `ExpireOlderThan` and emits `SsdpExpired`; a byebye mid-window still removes immediately; the sweep stops when the adapter token cancels (no eviction after teardown / after `RebindAsync` drains the old loop). Use the existing `DiscoveryServiceTests` rig.
  - [x] **Marshalling guard (MANDATORY, Action H)**: under `DeferredUiDispatcher` the sweep's `ExpireOlderThan` is NOT applied (registry unchanged, no `DeviceRemoved`) until `Drain()` — proves the eviction goes through `IUiDispatcher.Post`. NOT `InlineUiDispatcher`.
  - [x] Trait the new tests `[Trait("fr", "FR-056")]` (the 5.3 `[Trait fr ...]` convention).
- [x] **Task 6 — Build clean.** (AC: #14) Build Core + App + Tests; `-warnaserror` 0/0 (App: pre-existing benign WMC1506 only). Fix every consumer the compiler flags (the new `IDeviceRegistry.ExpireOlderThan` method ripples into the fakes — `Fakes/FakeDeviceRegistry.cs`, `Fakes/StubDiscoveryService.cs` if it implements `IDiscoveryService`).
- [x] **Task 7 — PRD FR-056 + Architecture Amendment.** (AC: #12, #13) Insert FR-056 into the PRD after FR-008 (Dev Notes §"PRD FR-056"). Append the Amendment after A32 (Dev Notes §"Architecture Amendment"); add the `SsdpExpired` row to the Decision 8 / Pattern-11 list.
- [x] **Task 8 — Run the full suite + record results.** (AC: #14) Capture pass/skip counts + the new-test delta in the Dev Agent Record. `DiagCategoriesTests` exact-set + `DiagCategoriesUsageTests` + `CoreAppBoundaryTests` + chaos green.
- [ ] **Task 9 — Manual smoke (live, FIRST-CLASS gate).** (AC: #1, #5) See Dev Notes §"Manual smoke". Yank a device off the network (pull power / cable — NO byebye) → it disappears from the tree after its `max-age` lease; a live device that keeps advertising survives; a `byebye`'d device still leaves immediately. Story ends at `review` (NOT done) — the smoke is the Project-Lead gate on real Linn/OpenHome hardware.

## Dev Notes

### The three design decisions (settled — confirm in the Dev Agent Record before coding)

1. **Grace = `1× max-age + ExpiryJitter`.** Evict at `LastSeenUtc + lease + jitter`. `jitter` is a small fixed tolerance — default **`5 s`** — absorbing routing latency (the alive must arrive + route through `_ui.Post`) and minor clock skew so a device re-advertising right at its lease edge is not evicted spuriously. We deliberately do NOT evict early (UDA *recommends* a device re-advertise within `< ½ max-age`, but that is the DEVICE's obligation, not a requirement on the control point; evicting at `½ max-age` would aggressively drop slow-but-alive devices). `1× max-age` is the conservative, spec-faithful upper bound — "the device promised to be back within `max-age`; it wasn't, plus a jitter grace, so it's gone."
2. **Default lease = `1800 s`** when `CacheControlMaxAge` is null. UDA 1.0 §1.2.2 uses `max-age=1800` as its canonical example; it is the de-facto default for non-conformant devices that omit `CACHE-CONTROL`. Without a default, such a device would never expire (lease = ∞) — defeating the safety net. (`CacheControlMaxAge` is `TimeSpan?` on `RegistryEntry`; null ⇒ apply the default.)
3. **Diagnostic = a NEW `DiagCategories.SsdpExpired` (`"Ssdp.Expired"`), Information severity.** Distinct from byebye (FR-008 emits no diagnostic), Rescan (`Adapter.Rescan`), and adapter-switch (`Adapter.Switch`) so the operator sees *why* a device left in the FR-041 viewer. This is a PINNED-SET change → update all three sites together (AC #10). Mandatory context: `DeviceUuid` (the UDN); `ErrorText` carries the reason (e.g. `"no alive in 1800s lease (+5s grace)"`). This is intentional, not drift — flag it to the reviewer (the 5.1/5.3 precedent).

### Where the sweep lives (load-bearing reconciliation vs SHIPPED code)

`DiscoveryService` is the right home and the proposal is accurate:
- It is the **singleton that ctor-injects `DeviceRegistry registry`** (`DiscoveryService(DeviceRegistry registry, SsdpParser parser, IUiDispatcher ui)`) — it already holds both the registry reference AND `IUiDispatcher` for marshalling.
- It already owns a **background-loop lifecycle**: the SSDP read loop is `Task.Run(() => ReadLoopAsync(reader, adapterToken, ct))`, started in `StartAsync`, re-started by `RebindAsync` (drain old loop → reset `_started` → fresh loop), and drained in `DisposeAsync`. **The sweep loop hangs off the exact same lifecycle** — kicked next to the read loop, bound to the same `adapterToken`/`ct` linked token, drained in `RebindAsync`/`DisposeAsync`. This gives AC #7 (stop on adapter switch/teardown) for free: an adapter switch cancels the old adapter token (in `AdapterScope.DisposeAsync`), which cancels the sweep loop's linked token, and `RebindAsync` drains it before starting the fresh one.
- **Why NOT `ShellViewModel`**: Rescan (5.3) lives there because it is an OPERATOR action (a `[RelayCommand]` on a menu item). The expiry sweep is PERIODIC + automatic + tied to the adapter lifecycle — `DiscoveryService` is its natural owner, and putting it there avoids re-threading the registry/clock through the VM.
- **Important**: `DiscoveryService.ReadLoopAsync` already marshals `RouteOnUiThread` via `ui.Post(...)`. The sweep does the SAME: compute `now` off-thread, then `ui.Post(() => registry.ExpireOlderThan(...))`. The registry's `AssertOnUiThread` (in `ExpireOlderThan`, mirroring `PruneNotSeenSince`) enforces it.

### New vs generalised registry method (reconciliation)

Add a NEW `IDeviceRegistry.ExpireOlderThan(nowUtc, defaultLease, jitter)`; do NOT generalise `PruneNotSeenSince`:
- `PruneNotSeenSince(epochUtc)` evicts entries with `LastSeenUtc < epochUtc` — a **single global epoch** (correct for Rescan: stamp epoch, M-SEARCH, prune everything not refreshed since).
- Expiry needs a **PER-ENTRY lease**: `nowUtc > LastSeenUtc + (CacheControlMaxAge ?? defaultLease) + jitter`. Each entry has a different lease. This cannot be expressed as one epoch.
- Both methods share `RemoveCore` (the byebye-identical cascade) and the snapshot-keys-first discipline, so they coexist idempotently. Keep `PruneNotSeenSince` exactly as-is (Story 5.3 owns it). The new method sits beside it with the same structure.

### Sweep interval

Default **`30 s`** — a coarse periodic check (the registry is small; this is not hot). The exact value is not load-bearing (the lease is the precision-controlling quantity, not the poll rate; a device evicts within one interval of `LastSeenUtc + lease + jitter`). Make it a test-settable field so tests drive it to (effectively) zero via the delay seam. NFR: the sweep wakes ~every 30 s, snapshots the keys, and posts a single marshalled `ExpireOlderThan` — negligible cost, never blocks the read loop (separate Task).

### Sweep loop shape (DiscoveryService)

```
// fields (defaulted; InternalsVisibleTo test setters mirror SetRescanDelayForTest)
private Func<DateTime> _clock = () => DateTime.UtcNow;
private Func<TimeSpan, CancellationToken, Task> _delay = (d, ct) => Task.Delay(d, ct);
private TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);
private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(1800); // FR-056 default
private static readonly TimeSpan ExpiryJitter = TimeSpan.FromSeconds(5);     // FR-056 grace
private Task? _sweepLoop;

private async Task SweepLoopAsync(CancellationToken adapterToken, CancellationToken ct)
{
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(adapterToken, ct);
    try
    {
        while (!linked.IsCancellationRequested)
        {
            await _delay(_sweepInterval, linked.Token).ConfigureAwait(false);
            var now = _clock();
            ui.Post(() =>
            {
                var evicted = registry.ExpireOlderThan(now, DefaultLease, ExpiryJitter); // returns evicted UDNs or count
                // emit SsdpExpired (per-UDN, or one summary line with the count) via IDiagnosticEmitter
            });
        }
    }
    catch (OperationCanceledException) { /* normal shutdown: adapter switch / teardown */ }
}
```

- **Per-eviction vs summary diagnostic**: prefer `ExpireOlderThan` returning the **evicted UDNs** (`IReadOnlyList<string>`) so `DiscoveryService` can emit one `SsdpExpired` per device with its UDN in `DeviceUuid` (the FR-041 Identity column resolves it). If the count is enough, emit a single summary line. Pick per-UDN (richer for the operator); reconcile in the Dev Agent Record. Either way the registry method does the eviction; `DiscoveryService` does the emit (the registry must not depend on `IDiagnosticEmitter` — DI-cycle note in `DeviceRegistry`'s class doc).
- **Start**: in `StartAsync`, after `_readLoop = Task.Run(...)`, add `_sweepLoop = Task.Run(() => SweepLoopAsync(adapterToken, ct));`. **Drain**: in `RebindAsync` (alongside the `_readLoop` drain) and `DisposeAsync`, `await _sweepLoop` (same VSTHRD003-suppressed pattern). The `_started` guard already covers the pair.

### Interaction (confirm — no special-casing)

- A device being **eager-fetched** or with **open popups** expires via the SAME `RemoveCore` path as byebye: `DeviceCts.Cancel()` cancels the in-flight fetch (AC-7.2 linkage), `DeviceRemoved(udn)` flips the popups to FR-037. No special-casing.
- **byebye still wins immediately** (AC #3) — `OnByebye` is unchanged; if a byebye lands before the sweep, the entry is already gone and `RemoveCore.TryRemove` makes the sweep a no-op for that UDN.
- A **refreshing alive resets the lease** — `OnAlive` → `RefreshSsdpMetadata` bumps `LastSeenUtc` + re-reads `CacheControlMaxAge`. The next sweep sees a fresh lease.
- **Rescan (5.3) + this auto-expiry coexist** — both call `RemoveCore`; idempotent. A manual Rescan prune and an automatic expiry of the same dead device cannot double-remove (TryRemove).

### Test seam: clock + interval (vs SHIPPED precedents)

- There is **NO injectable clock in Core today** — `DateTime.UtcNow` is used directly in 6 files, and `RegistryEntry.LastSeenUtc` is stamped from the `nowUtc` callers pass. So this story threads a `Func<DateTime>` clock into `DiscoveryService` (for the sweep's "now") AND lets tests construct `RegistryEntry`/drive `OnAlive` with controlled `nowUtc` (already supported — `OnAlive(udn, location, nowUtc, ...)` takes `nowUtc`). The registry's `ExpireOlderThan` takes `nowUtc` as a PARAMETER (no clock inside the registry) — the caller (`DiscoveryService`) supplies it from its injected clock. This keeps the registry pure + instantly testable (pass any `nowUtc`).
- The **interval/delay seam** mirrors `SubscriptionClient._delay` (`Func<TimeSpan, CancellationToken, Task>`, default `Task.Delay`, swapped in tests) and `ShellViewModel._rescanDelay` (+ `SetRescanDelayForTest`). A test sets the delay to return immediately (or a `TaskCompletionSource` it controls) so the loop spins instantly with no real wait. NO real multi-minute sleeps anywhere (AC #8).

### Standing gates / boundaries

- **PURE CORE** — every production change is in `src/ohSpy.Core/` (`Devices/RegistryEntry.cs`, `Devices/DeviceRegistry.cs`, `Devices/IDeviceRegistry.cs`, `Discovery/DiscoveryService.cs`, `Diagnostics/DiagCategories.cs`). NO App change (the sweep starts via the already-wired `DiscoveryService.StartAsync`/`RebindAsync`). `CoreAppBoundaryTests` must stay green.
- **`-warnaserror` 0/0** Core; App build only the pre-existing benign WMC1506.
- **Chaos hook** stays green (`dotnet test --filter "category=chaos"`).
- **Action H** (`winui-no-synccontext-marshal-vm`): the sweep mutates bound state (registry → tree) off a TIMER thread → the eviction MUST be marshalled via `IUiDispatcher.Post`, proven by a `DeferredUiDispatcher` guard test. This is the load-bearing threading risk — DiscoveryService already marshals `RouteOnUiThread`; the sweep follows the same discipline.
- **Smoke-per-UI-story**: a live smoke (yank a no-byebye device) is a FIRST-CLASS gate; story ends at `review`, not done (the Epic 2 retro lesson + the project's `smoke-per-ui-story` rule).

### PRD FR-056 (Task 7 — the story authors this; insert after FR-008, before FR-053)

```
#### FR-056: Removal on expiry (inferred byebye)

A registered device whose latest `ssdp:alive` promised a `CACHE-CONTROL: max-age` lease MUST be removed from the registry (and tree) when that lease lapses without a refreshing `alive` — i.e. when `now > LastSeenUtc + max-age` — even though no `ssdp:byebye` was received (UDA 1.0 §1.2.2: a device re-advertises before its `max-age` expires; absence implies it has left). Removal uses the same path as FR-008 (byebye): the device leaves the registry + tree, open popups receive the FR-037 "device no longer reachable" treatment, and any in-flight description/SCPD fetch is cancelled.
- **Grace:** eviction occurs at `LastSeenUtc + max-age` plus a small fixed jitter tolerance (`~5 s`) for network/routing latency and clock skew (a device promises to re-advertise within that window; UDA recommends `< ½ max-age`, but eviction at `1× max-age` is the conservative, spec-faithful control-point bound).
- **Missing `CACHE-CONTROL`:** when an `alive` omits `max-age` (non-conformant but seen in the wild), a default lease of `1800 s` (the UDA 1.0 §1.2.2 example) applies so the device still expires rather than living forever.
- **Diagnostic:** an expiry emits a distinct `Ssdp.Expired` diagnostic (Information) carrying the device UDN, so the FR-041 Diagnostics viewer shows *why* a device left.
- The check is periodic and MUST NOT block the SSDP read loop, the GENA listener, or the UI thread; the eviction is marshalled onto the UI thread (the registry is UI-thread-owned).
```

### Architecture Amendment (Task 7 — append after A32, before `### Decision 13`; add the `SsdpExpired` row to the Decision 8 / Pattern-11 list)

```
### Amendment A33 — DiscoveryService periodic expiry sweep (inferred byebye; Decision 9 / DiscoveryService-lifecycle refinement)

**Source:** Sprint Change Proposal 2026-06-11 (correct-course); surfaced in real-world use — a device pulled off the network WITHOUT `ssdp:byebye` was never removed (FR-008 is byebye-only). Authored in Story 2.11. Implements PRD FR-056.

**The gap:** `CACHE-CONTROL: max-age` is parsed (`SsdpParser.ParseMaxAge`) and stored (`SsdpAnnouncement.CacheControlMaxAge` → `RegistryEntry.CacheControlMaxAge` + `LastSeenUtc`, refreshed by `RefreshSsdpMetadata` on every alive), but nothing evicts on it. The only removal paths were `DeviceRegistry.OnByebye` (FR-008), the manual `PruneNotSeenSince` (Story 5.3 Rescan), and `Clear()` (Story 5.2 adapter switch). Standard UDA §1.2.2 expiry (a device re-advertises before its `max-age`; absence implies it left) was never required.

**The amendment:** the registry gains an automatic **expiry sweep** — a periodic, UI-thread-marshalled eviction of entries past their `CACHE-CONTROL` lease, reusing the existing `RemoveCore` cascade (cancel + dispose `DeviceCts`, raise `DeviceRemoved` per UDN — byebye-identical, so the FR-037 popup banners + in-flight-fetch cancellation just work). It is the AUTOMATIC per-entry-lease cousin of Story 5.3's manual `PruneNotSeenSince`.
- **Owner:** the singleton `DiscoveryService` (it already ctor-injects `DeviceRegistry` + `IUiDispatcher` and owns the per-adapter read-loop lifecycle). The sweep loop is a second `Task.Run` started alongside the read loop in `StartAsync`, re-started by `RebindAsync`, and drained in `DisposeAsync` — bound to the same adapter token, so it STOPS on adapter switch / teardown (no eviction against a torn-down/replaced registry).
- **Lease / grace:** lease = `RegistryEntry.CacheControlMaxAge ?? 1800 s` (the UDA §1.2.2 default for non-conformant devices that omit `CACHE-CONTROL`); evict when `now > LastSeenUtc + lease + ~5 s` jitter. `1× max-age` (not `½`) is the conservative control-point bound.
- **Registry method:** a NEW `IDeviceRegistry.ExpireOlderThan(nowUtc, defaultLease, jitter)` (NOT a generalisation of `PruneNotSeenSince`, which keys on a single global epoch — expiry needs a per-entry lease). Same structure as `PruneNotSeenSince`: `AssertOnUiThread`, snapshot keys first, `RemoveCore` per expired entry, return the evicted UDNs / count. Idempotent with byebye / Rescan / Clear (shared `RemoveCore.TryRemove`).
- **Marshalling (Action H / `winui-no-synccontext-marshal-vm`):** the sweep runs on a timer thread; `now` is read off-thread, then `IUiDispatcher.Post` marshals `ExpireOlderThan` onto the UI thread (the registry is UI-thread-owned — `AssertOnUiThread`). Proven by a `DeferredUiDispatcher` guard test.
- **Test seam:** a `Func<DateTime>` clock + a `Func<TimeSpan,CancellationToken,Task>` delay + a settable interval are injected into `DiscoveryService` (defaulted to `DateTime.UtcNow` / `Task.Delay` / 30 s) — the `SubscriptionClient._delay` / `ShellViewModel._rescanDelay` precedent — so the sweep is unit-testable instantly with no real waits. `ExpireOlderThan` takes `nowUtc` as a parameter (no clock inside the registry — it stays pure).
- **Diagnostic:** a NEW `DiagCategories.SsdpExpired = "Ssdp.Expired"` (Information; context: DeviceUuid; ErrorText = the lease/grace reason). Pinned-set change — added to `DiagCategories.cs`, `DiagCategoriesTests.expectedNames`, and this Decision-8 / Pattern-11 list together (the 5.1 `SsdpSearchObserved` / 5.3 `Rescan` precedent). Emitted from `DiscoveryService` (not the registry — the registry deliberately has no `IDiagnosticEmitter` dependency, to avoid the Emitter→RingSink→IdentityLookup→Registry cycle).

**Applied to:** `DiscoveryService` (sweep loop + clock/delay/interval seams + the `SsdpExpired` emit + `IDiagnosticEmitter` ctor dep), `DeviceRegistry`/`IDeviceRegistry` (`ExpireOlderThan`), `RegistryEntry` (the per-entry expiry predicate; `CacheControlMaxAge` + `LastSeenUtc` already present), `DiagCategories` (+ the test exact-set). PURE CORE — no App change (the sweep starts via the already-wired `DiscoveryService.StartAsync`/`RebindAsync`).
```

### Manual smoke (Task 9 — first-class gate, live Linn/OpenHome hardware)

1. Start ohSpy on the live network; confirm a target device (with a known `max-age`, visible in the FR-041 Diagnostics viewer / SSDP log) appears in the tree.
2. **Yank it off the network** — pull power or unplug the cable so it sends NO `ssdp:byebye`.
3. Wait its `max-age` lease (+ the ~5 s jitter + up to one ~30 s sweep interval). **Expected:** the dead row disappears from the tree; an `Ssdp.Expired` Information diagnostic for that UDN appears in the Diagnostics viewer; any open popup for it flips to the FR-037 device-gone banner; live devices that keep advertising are UNAFFECTED.
4. Re-plug the device → it re-discovers + reappears (fresh entry / fresh lease).
5. Sanity: a *gracefully*-removed device (power-off that DOES byebye, or the 5.3 Rescan) still leaves immediately (byebye/Rescan wins, AC #3) — expiry is the safety net for the no-byebye case only.

Story ends at `review`, NOT done — the live smoke is the Project-Lead gate.

### Project Structure Notes

- Core/App split holds: all production changes in `src/ohSpy.Core/`. No DI-graph change beyond a possible `IDiagnosticEmitter` ctor param on `DiscoveryService` (already a registered singleton — auto-resolves). No new dependency, no new App wiring.
- The new `IDeviceRegistry.ExpireOlderThan` ripples into the test fakes (`Fakes/FakeDeviceRegistry.cs`) — implement it there (mirror the real method or a stub that the registry tests don't need). Confirm `StubDiscoveryService` still satisfies `IDiscoveryService` (the interface surface is unchanged — the sweep is an internal implementation detail of the concrete `DiscoveryService`, NOT a new interface member).
- Naming: the registry already uses `PruneNotSeenSince` for the manual prune; `ExpireOlderThan` is the parallel name for the automatic per-lease expiry. Keep `DiagCategories.SsdpExpired` dotted (`"Ssdp.Expired"`) like its siblings.

### References

- [Source: _bmad-output/planning-artifacts/sprint-change-proposal-2026-06-11.md §1, §4.1 (FR-056), §4.3 (Story 2-11), §4.5 (Amendment pointer)] — the requirements source (NOT epics.md).
- [Source: _bmad-output/implementation-artifacts/2-10-udn-string-identity.md] — the corrective-story precedent (PRD + architecture edits authored as story tasks).
- [Source: _bmad-output/implementation-artifacts/5-3-rescan-view-rescan-menu-prune-non-responders.md] — `PruneNotSeenSince` + the `_rescanDelay` seam + the `Rescan` pinned-set precedent.
- Verified shipped code (read 2026-06-11): `Discovery/DiscoveryService.cs` (read-loop lifecycle, `StartAsync`/`RebindAsync`/`DisposeAsync`, `RouteOnUiThread` marshalling), `Devices/DeviceRegistry.cs` (`PruneNotSeenSince`, `RemoveCore`, `Clear`, `AssertOnUiThread`), `Devices/RegistryEntry.cs` (`CacheControlMaxAge`, `LastSeenUtc`, `RefreshSsdpMetadata`, `DeviceCts`), `Devices/IDeviceRegistry.cs`, `ViewModels/ShellViewModel.cs` (`StartBoundServicesAsync`, `_rescanDelay`/`SetRescanDelayForTest` seam), `Discovery/AdapterScope.cs` (adapter-token lifecycle), `Diagnostics/DiagCategories.cs` + `tests/.../Diagnostics/DiagCategoriesTests.cs` (the pinned set), `App/Composition/ServiceRegistration.cs` (`DiscoveryService` singleton reg), `Events/SubscriptionClient.cs` (`_delay` seam precedent).
- [Source: architecture.md#Decision 9] (`:1084`) — the registry contract being amended. [Source: architecture.md#Amendment A32] (`:3025`) — append point for the new amendment.
- [Source: prd.md#FR-008] (`:117`) — the byebye-only removal FR; FR-056 inserts after it.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context) — bmad-dev-story workflow.

### Debug Log References

**Task 0 — design decisions confirmed before coding (settled in the story; recorded here per the workflow):**

1. **Grace = `1× max-age + ExpiryJitter`**, `ExpiryJitter = 5 s` (fixed). Evict iff `nowUtc > LastSeenUtc + lease + jitter`. `1× max-age` (not `½`) is the conservative control-point bound — `½` is the *device's* re-advertise obligation, not a requirement on us.
2. **Default lease = `1800 s`** when `RegistryEntry.CacheControlMaxAge` is `null` (UDA 1.0 §1.2.2 canonical example) — so non-conformant devices that omit `CACHE-CONTROL` still expire instead of living forever.
3. **Diagnostic = NEW `DiagCategories.SsdpExpired = "Ssdp.Expired"`** (Information). Pinned-set change → synced across `DiagCategories.cs` + `DiagCategoriesTests.expectedNames` + arch D8/Pattern-11. Context: `DeviceUuid` (the UDN); `ErrorText` carries the lease/grace reason. Emitted **per-UDN** from `DiscoveryService` (richer for the operator), NOT from the registry (registry has no `IDiagnosticEmitter` dep — DI-cycle).

**Sweep home = singleton `DiscoveryService`** (ctor-injects `DeviceRegistry` + `IUiDispatcher`; owns the read-loop lifecycle `StartAsync`/`RebindAsync`/`DisposeAsync`). A 2nd `Task.Run(SweepLoopAsync)` next to `ReadLoopAsync`, bound to the SAME `adapterToken`/`ct` linked token → stops on adapter switch + drains in `RebindAsync`/`DisposeAsync`. NOT `ShellViewModel` (Rescan is operator-triggered; this is periodic).

**Emit shape decision:** `ExpireOlderThan` returns `IReadOnlyList<string>` (the evicted UDNs). `DiscoveryService` emits one `SsdpExpired` per UDN inside the `_ui.Post` lambda (so it runs on the UI thread, where the registry mutation also happens). The registry method does the eviction only.

**Implementation refinement (NOT in the story's recommended shape — flag to reviewer):** the read loop stops on channel completion, but the sweep loop has **no equivalent stop signal** — it only loops on `_delay`. If `DisposeAsync` / `RebindAsync` simply `await _sweepLoop`, the loop is parked on `_delay(_sweepInterval, linkedToken)` where the linked token (adapterToken + ct) need not be cancelled, so the drain would hang up to one interval (in production: 30 s; in tests with `CancellationToken.None`: forever). To make teardown deterministic, `DiscoveryService` owns a per-start `_sweepCts` (the sweep loop's linked token now combines `adapterToken + ct + _sweepCts.Token`); `StopSweep()` (called at the top of the `_sweepLoop` drain in both `RebindAsync` and `DisposeAsync`) cancels + disposes it, so the parked `_delay` throws `OperationCanceledException` and the loop exits at once. This is the production-correct teardown signal (an adapter switch still ALSO cancels via `adapterToken`; `_sweepCts` just guarantees the drain even when the caller didn't cancel the adapter token, e.g. `DisposeAsync` at app shutdown). It does not change the eviction semantics.

**Test-rig note:** the sweep loop's drain is the reason the new `DiscoveryServiceTests` sweep cases use a cancellable `adapterCts` + a `DrainSweepAsync` helper (cancel the adapter token, complete the channel, await `DisposeAsync`). The Action H test seeds the entry by calling `registry.OnAlive` directly (the `DeferredUiDispatcher`'s `AssertOnUiThread` is a no-op), removing any read-loop/sweep race over the seeding post, then proves the sweep's eviction is QUEUED (PostCount rises, registry unchanged, no `DeviceRemoved`) until `ui.Drain()`.

### Completion Notes List

**What shipped (PURE CORE — no App source change):**
- `RegistryEntry.IsExpiredAt(nowUtc, defaultLease, jitter)` — pure per-entry predicate: `nowUtc > LastSeenUtc + (CacheControlMaxAge ?? defaultLease) + jitter`.
- `IDeviceRegistry.ExpireOlderThan(nowUtc, defaultLease, jitter) → IReadOnlyList<string>` + `DeviceRegistry` impl — mirrors `PruneNotSeenSince` exactly (`AssertOnUiThread`, snapshot keys first, re-read live entry, `RemoveCore` per expired, return evicted UDNs). `RemoveCore` reused unchanged. `PruneNotSeenSince`/`OnAlive`/`OnByebye`/`Clear`/`Remove`/`RefreshSsdpMetadata` untouched.
- `DiagCategories.SsdpExpired = "Ssdp.Expired"` (Information) — pinned-set synced across all three: the const (`DiagCategories.cs`), the exact-set array (`DiagCategoriesTests.expectedNames`, SSDP group), and the architecture (Decision-8 list at `architecture.md` + the Pattern-11 context table). INTENTIONAL pinned-set change — flag to reviewer.
- `DiscoveryService`: `IDiagnosticEmitter` ctor dep (auto-resolves — already a registered singleton, so `ServiceRegistration.cs:133` `AddSingleton<DiscoveryService>()` needs no edit); `Func<DateTime>` clock + `Func<TimeSpan,CT,Task>` delay + settable `_sweepInterval` seams (defaults `DateTime.UtcNow` / `Task.Delay` / 30 s; `InternalsVisibleTo` test setters — the `SetRescanDelayForTest` precedent); `DefaultLease`=1800 s + `ExpiryJitter`=5 s constants; `SweepLoopAsync` started next to the read loop in `StartAsync`, drained (via `StopSweep` + await) in `RebindAsync` + `DisposeAsync`, bound to `adapterToken + ct + _sweepCts`. Emits one `SsdpExpired` per evicted UDN from the marshalled `_ui.Post` lambda.

**Lease/grace math:** lease = `CacheControlMaxAge ?? 1800 s`; evict iff `now > LastSeenUtc + lease + 5 s`. `1×` max-age (conservative control-point bound), strict `>` so the exact edge survives.

**Action H (marshalling guard):** `Sweep_Eviction_IsMarshalledThroughUiDispatcher_ActionH_FR056` uses `DeferredUiDispatcher` (NOT inline) — registry unmutated + no `DeviceRemoved` until `Drain()`. Proves the eviction goes through `IUiDispatcher.Post`.

**Test results:** full Core suite **565 passed / 2 skipped / 0 failed** (was 553/2 — +12 FR-056 tests: 7 `DeviceRegistryTests` + 5 `DiscoveryServiceTests`). The 2 skips are pre-existing NetArchTest cases (`AsyncDisciplineTests.Core_AsyncDiscipline_NoBlockingWaits`, `DiagCategoriesUsageTests.EmitCallSites_UseConstants_NotInlineStrings`) — unchanged by this story. `DiagCategoriesTests` exact-set GREEN, `CoreAppBoundaryTests` GREEN (Core→App still forbidden), chaos hook GREEN (`category=chaos`). `-warnaserror` 0/0 on Core + Core.Tests + Soak.Tests; App builds with only the pre-existing benign WMC1506 (MainWindow.xaml:162, untouched — documented in Amendment A32).

**byebye/Rescan/Clear regression:** all three share `RemoveCore.TryRemove` with the new sweep → idempotent; `Sweep_ByebyeMidWindow_StillRemovesImmediately_FR056` + the unchanged byebye/prune/clear tests confirm.

**Deferred (NOT done here):** Task 9 manual live smoke on real Linn/OpenHome hardware is the Project-Lead gate; story left at `review`.

### File List

**Production (src/ohSpy.Core/):**
- `Devices/RegistryEntry.cs` — added `IsExpiredAt` predicate.
- `Devices/IDeviceRegistry.cs` — added `ExpireOlderThan` to the interface.
- `Devices/DeviceRegistry.cs` — added `ExpireOlderThan` impl.
- `Diagnostics/DiagCategories.cs` — added `SsdpExpired` constant.
- `Discovery/DiscoveryService.cs` — `IDiagnosticEmitter` ctor dep; clock/delay/interval seams; `SweepLoopAsync` + `_sweepCts`/`StopSweep` lifecycle; `RebindAsync`/`DisposeAsync` drain the sweep.

**Tests:**
- `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` — `"SsdpExpired"` added to `expectedNames`.
- `tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs` — 7 new `[Trait fr FR-056]` `ExpireOlderThan` tests.
- `tests/ohSpy.Core.Tests/Discovery/DiscoveryServiceTests.cs` — ctor arg + 5 new `[Trait fr FR-056]` sweep tests (incl. the Action H guard) + `OneShotDelay`/`DrainSweepAsync`/`WaitUntilAsync` helpers.
- `tests/ohSpy.Core.Tests/Fakes/FakeDeviceRegistry.cs` — inert `ExpireOlderThan`.
- `tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` — `DiscoveryService` ctor arg (×2 rigs).
- `tests/ohSpy.Soak.Tests/Harness/SoakHarness.cs` — `DiscoveryService` ctor arg.

**Planning artifacts (authored by this story, 2.10 precedent):**
- `_bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md` — FR-056 inserted after FR-008.
- `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` — Amendment A33 appended after A32; `SsdpExpired` row added to the Decision-8 list + the Pattern-11 context table.

### Review Findings

- [x] [Review][Patch] **FIXED 2026-06-11.** Diagnostic ErrorText is now per-device-accurate: `ExpireOlderThan` returns `(Udn, MaxAge?)` (the advertised CACHE-CONTROL captured before removal), and `DiscoveryService` emits the actual lease per device ("its Ns CACHE-CONTROL max-age lease" vs "the 1800s default lease (no CACHE-CONTROL advertised)"). [src/ohSpy.Core/Discovery/DiscoveryService.cs + Devices/IDeviceRegistry.cs/DeviceRegistry.cs + FakeDeviceRegistry + DeviceRegistryTests assertions updated to `.Select(e => e.Udn)`/`.MaxAge`]
- [x] [Review][Patch] **FIXED 2026-06-11.** Added `Sweep_RebindAsync_DrainsOldSweep_StartsFreshSweepOnNewScope_FR056` — the old sweep parks on a never-releasing delay (RebindAsync must drain it via StopSweep without hanging + reset `_started`), then a fresh OneShotDelay sweep runs on the new scope and evicts the stale entry + emits Ssdp.Expired. [tests/ohSpy.Core.Tests/Discovery/DiscoveryServiceTests.cs]

  > Both patches applied + verified: Core 565 → **566 passed / 2 skipped** (+1 RebindAsync test); App + soak build clean; `-warnaserror` 0/0. D1 (DisposeAsync idempotency) deferred — pre-existing.
- [x] [Review][Defer] DisposeAsync has no idempotency guard — double-dispose re-awaits a completed _readLoop/_sweepLoop (pre-existing; the read loop had the same gap before this story) [src/ohSpy.Core/Discovery/DiscoveryService.cs:124] — deferred, pre-existing

### Change Log

- 2026-06-11 — Story 2.11 implemented (FR-056 SSDP max-age expiry / inferred byebye). Added the `DiscoveryService` periodic expiry sweep + `IDeviceRegistry.ExpireOlderThan` + `RegistryEntry.IsExpiredAt` + `DiagCategories.SsdpExpired` (pinned-set triple-synced). Authored PRD FR-056 + Architecture Amendment A33. Core suite 565/2 green; `-warnaserror` 0/0 (Core/Core.Tests/Soak.Tests); App builds bar the pre-existing WMC1506; chaos green. Status → review (manual live smoke pending Project-Lead gate).
