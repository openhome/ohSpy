# Story 2.12: Rebind on host network change (NetworkAddressChanged auto-rebind)

Status: review

<!-- Corrective story (correct-course). Requirements source: sprint-change-proposal-2026-06-11.md §4.2 (FR-057) + §4.4 (Story 2-12) + §4.5 (Amendment pointer), NOT epics.md. Epic 2 is CLOSED/done; this is corrective work appended to it (the 2-10 / 2-11 precedent) — do NOT flip Epic 2 status. The story also AUTHORS the PRD FR-057 + an Architecture Amendment A34 as tasks (the 2-10 / 2-11 precedent). Sibling story 2-11 (SSDP max-age expiry, committed d36316f) is the safety-net backstop this story leans on. -->

## Story

As a **ohSpy operator who moves the host PC between networks (office → home, lab → desk) while the app is running**,
I want **ohSpy to detect when the host's network changes — the bound adapter's IPv4 changes, or the adapter is removed/disabled — and automatically rebind to the live network (or tear down to the zero-adapter state)**,
so that **the now-unreachable network's stale devices clear immediately and the new network is discovered, without me having to manually re-pick the adapter from `View → Network adapter`**.

## Problem Statement

Moving the PC between networks (office→home) leaves **all** the unreachable office-subnet devices visible — for a day, in the real-world report. There is **no** `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` listener anywhere in the app (verified 2026-06-11: the only `NetworkChange` reference is a doc-comment mention in `AdapterScope.cs`; nothing subscribes). The app **only** rebinds on a **manual** `View → Network adapter` pick (`ShellViewModel.SwitchAdapterAsync`, Story 5.2). So on a network move:

- the old devices never `byebye` (the PC left them, not vice-versa),
- the app never notices the adapter/IP changed → it keeps the dead scope bound,
- the stale devices linger.

**Relationship to Story 2-11 (the safety net).** With 2-11 shipped (FR-056 SSDP max-age expiry), the stale devices DO eventually age out after their `CACHE-CONTROL` lease (≤ ~1800 s + jitter + one sweep). That is the *eventual* cleanup. **This story (FR-057) is the *responsive* fix:** detect the change immediately and rebind to the live network, clearing the stale one at once. Expiry remains the backstop for the corner where a rebind target isn't found and the bound scope keeps a frozen registry.

**The fix (FR-057):** introduce a **Core seam `INetworkChangeNotifier`** (a test-fakeable abstraction over the BCL `NetworkChange.NetworkAddressChanged` static event — which is itself NOT directly fakeable). `ShellViewModel` subscribes to it, **debounces** the burst of OS notifications a transition produces, then on quiescence re-enumerates eligible adapters via the existing `INetworkAdapterEnumerator.Enumerate()`. If the currently-bound adapter is **gone or its IPv4 changed**, it drives the **existing** `ShellViewModel.SwitchAdapterAsync(bestEligible)` (Story 5.2 atomic rebind — which already clears the registry + log + re-discovers) — or, if no adapter is eligible, tears down to the **zero-adapter** state. The off-thread event is marshalled via `IUiDispatcher.Post`; the existing `_switching` re-entrancy guard serialises an auto-rebind against any manual switch.

**Scope: Core seam + ShellViewModel wiring + thin App-side production registration.** The detection logic, debounce, re-enumerate, and rebind decision are all **Core** (unit-testable over a fake notifier + a fake enumerator). The ONLY App touch is registering the real `NetworkChangeNotifier` (the BCL-backed implementation) in the DI graph and confirming `ShellViewModel.StartAsync` wires the subscription — no UI, no XAML. The rebind itself reuses the shipped Story 5.2 machinery unchanged.

## Acceptance Criteria

1. **A network-change notification triggers a re-enumerate.** Given ohSpy is bound to an adapter, when the host raises a network-address change (delivered through the new `INetworkChangeNotifier` seam), then — after the debounce window elapses (AC #4) — ohSpy re-enumerates eligible adapters via `INetworkAdapterEnumerator.Enumerate()` and evaluates whether the bound adapter is still eligible (present with the same IPv4). The notifier's raw event handler MUST NOT do the enumerate/rebind work inline — it schedules the debounced evaluation only.

2. **Bound adapter gone / IPv4 changed → auto-rebind to the best eligible adapter.** Given the re-enumeration finds the currently-bound adapter is **no longer eligible** — its IPv4 is absent from the fresh `Enumerate()` result (the adapter was removed/disabled, or its IPv4 changed) — and at least one eligible adapter remains, when the evaluation runs, then ohSpy drives `ShellViewModel.SwitchAdapterAsync(best)` where `best` is the first eligible adapter from `Enumerate()` (the launch-default policy, FR-048 — see Dev Notes §"Auto-target policy"). The FR-050 atomic rebind clears the stale registry + SSDP log, cancels in-flight fetches, flips open popups to FR-037, rebinds on the new adapter, and re-runs the M-SEARCH sweep — i.e. the stale network's devices are cleared and the new network is discovered. NO operator action is required (no manual re-pick).

3. **Bound adapter still present and unchanged → no-op.** Given the re-enumeration finds the currently-bound adapter **still eligible** with the **same IPv4** (a network event fired but our adapter is unaffected — e.g. a *different* NIC changed, or a transient blip that resolved), when the evaluation runs, then ohSpy does NOTHING: no rebind, no registry clear, no `SwitchAdapterAsync` call (the same-adapter no-op short-circuit in `SwitchAdapterAsync` would catch it anyway, but the evaluation MUST NOT even call it — to avoid a spurious `Adapter.NetworkChanged` diagnostic and a needless guard acquisition).

4. **Debounce coalesces a burst.** Given a network transition produces a **rapid burst** of `NetworkAddressChanged` events (the OS fires several within tens-to-hundreds of ms as interfaces flap up/down and addresses settle), when the events arrive, then ohSpy debounces them: only **one** re-enumerate/rebind evaluation runs, after the burst goes quiet for the debounce window. A new event arriving during the window **resets** the timer (trailing-edge debounce — we want the settled state, not the first transient). Default window = **2 s** (see Dev Notes §"Debounce window"); the window is a test-settable seam (no real multi-second waits in tests).

5. **No eligible adapter → tear down to the zero-adapter state.** Given the re-enumeration finds **zero** eligible adapters (the PC has no live network — e.g. cable pulled, Wi-Fi off, between networks mid-transition), when the evaluation runs, then ohSpy tears down to the **zero-adapter** state: the stale devices are cleared and inbound discovery is stopped (no active scope). This MUST reuse `SwitchAdapterAsync`'s existing zero-adapter handling — `SwitchAdapterAsync` builds a scope whose `Enumerate()` yields nothing, `AdapterScope.StartAsync` sets `CurrentAdapterIPv4 = null`, and `StartBoundServicesAsync` starts nothing inbound (NFR-R5). The registry + SSDP log are cleared as part of the FR-050 sequence; the app remains running and interactive (NFR-R5). A later network-change event that yields an eligible adapter rebinds via AC #2. **Decision (see Dev Notes §"Zero-adapter teardown"): drive this through `SwitchAdapterAsync` with a synthetic zero-IPv4 target, NOT a separate teardown path** — reuse, do not fork the rebind.

6. **Off-thread event is marshalled (Action H).** Given `NetworkChange.NetworkAddressChanged` fires on a **non-UI thread** (a thread-pool / OS-callback thread), when the notifier raises it through the seam, then every mutation of observable VM state (the debounce-driven evaluation, the `SwitchAdapterAsync` invocation, any transient flag, the diagnostic emit context that touches the registry) is marshalled onto the UI thread via `IUiDispatcher.Post`. Proven by a `DeferredUiDispatcher`-based test (NOT `InlineUiDispatcher`, which masks a missing `Post`): under a deferred dispatcher the rebind is NOT applied until the UI thread drains. (Memory `winui-no-synccontext-marshal-vm`: WinUI 3 has no `SynchronizationContext`; an off-thread continuation that mutates bound state without marshalling crashes `RPC_E_WRONGTHREAD`.)

7. **Re-entrancy guard prevents a manual + auto race.** Given a **manual** `SwitchAdapterAsync` is already in flight (the operator picked an adapter), when an auto-rebind evaluation fires concurrently, then the existing `_switching` `Interlocked` guard serialises them: the auto-rebind's `SwitchAdapterAsync` call is rejected (the guard is held) and does not orphan a scope or run a second concurrent rebind. Symmetrically, an auto-rebind in flight blocks a manual switch the same way (the guard is shared, not a new one — do NOT add a second guard). After the in-flight switch completes, a subsequent network-change event (or a still-pending debounced evaluation) re-evaluates against the now-current adapter (AC #1/#2/#3) — so a change that landed mid-switch is not permanently lost (see Dev Notes §"Re-entrancy + the mid-switch change").

8. **Diagnostic on network-change rebind.** When an auto-rebind (or zero-adapter teardown) is triggered by a network change, ohSpy emits a diagnostic: a NEW `DiagCategories.AdapterNetworkChanged = "Adapter.NetworkChanged"` (Information severity, carrying the old → new adapter IPv4 in `ErrorText`, e.g. `"network change: 10.0.0.5 → 192.168.1.20"` or `"network change: 10.0.0.5 → (no eligible adapter)"`) so the operator sees in the FR-041 Diagnostics viewer that the rebind was network-triggered (distinct from a manual `Adapter.Switch`, an `Adapter.Rescan`, and an `Ssdp.Expired`). This is a **pinned-set change** — see AC #9. The no-op path (AC #3) emits NOTHING (or at most a Verbose "network change ignored — bound adapter unchanged", optional — pick one and document).

9. **DiagCategories pinned-set synced (atomic triple-change).** The new `DiagCategories.AdapterNetworkChanged` constant is added to ALL THREE pinned locations together (the 5.1 `SsdpSearchObserved` / 5.3 `Rescan` / 2.11 `SsdpExpired` precedent): `src/ohSpy.Core/Diagnostics/DiagCategories.cs`, the exact-set array in `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` (`expectedNames`, in the Adapter group), AND the architecture Decision 8 / Pattern-11 constants list (`architecture.md`). The `DiagCategoriesTests` exact-set + `DiagCategoriesUsageTests` usage guards stay green. Flag this to the reviewer as INTENTIONAL, not drift.

10. **`INetworkChangeNotifier` is a clean Core seam.** A new `INetworkChangeNotifier` interface lives in `ohSpy.Core` (it CAN — `System.Net.NetworkInformation.NetworkChange` is BCL, available to Core; verify nothing WinUI leaks in, `CoreAppBoundaryTests` stays green). It exposes an event the VM subscribes to (e.g. `event EventHandler NetworkAddressChanged;`) and is `IDisposable` (so the BCL subscription is detached on app teardown). The real `NetworkChangeNotifier` implementation wraps the BCL static event (subscribe in ctor / a `Start()` method, unsubscribe in `Dispose`). A test fake `FakeNetworkChangeNotifier` raises the event on demand. The VM ctor-injects the seam (or `StartAsync` wires it — see Dev Notes §"Where the subscription lives"). This is the load-bearing testability decision (the BCL static event cannot be raised by a test).

11. **Subscription lifecycle is clean.** The VM subscribes to `INetworkChangeNotifier.NetworkAddressChanged` at startup (`StartAsync`) and unsubscribes + disposes the notifier (or detaches its handler) in `ShellViewModel.DisposeAsync`, so no dangling BCL handler survives app teardown (a leaked static-event handler is a classic memory leak — the BCL `NetworkChange` event roots its subscribers for process life). Any pending debounce timer is also cancelled on dispose.

12. **PRD FR-057 authored.** FR-057 (text in Dev Notes §"PRD FR-057") is inserted into the PRD after FR-056 / near FR-050 (after the FR-050 block, before §4.12 Diagnostics).

13. **Architecture Amendment authored.** A new Amendment A34 (network-change auto-rebind — text in Dev Notes §"Architecture Amendment A34") is appended after A33, before `### Decision 13`; the `AdapterNetworkChanged` row is added to the Decision 8 / Pattern-11 constants list.

14. **Suite green.** The full Core suite passes (`-warnaserror` 0/0), including the new tests below; `CoreAppBoundaryTests` still forbids `Core → App` (the new seam is Core, the BCL impl is Core, only DI registration is App); `DiagCategoriesTests` exact-set + `DiagCategoriesUsageTests` + `AsyncDisciplineTests` green; the chaos hook stays green. The new `INetworkChangeNotifier` ctor param ripples into every `ShellViewModel` test rig + the soak harness — fix every consumer the compiler flags.

## Tasks / Subtasks

- [x] **Task 0 — Settle the four design decisions + the seam shape (do FIRST).** (AC: #2, #4, #5, #10)
  - [x] Confirm (and record in the Dev Agent Record before coding): **(a) auto-target policy** = auto-pick the **first eligible adapter** from `Enumerate()` (the FR-048 launch-default), NOT clear-and-prompt. **(b) debounce window** = **2 s** trailing-edge, test-settable. **(c) Core seam** = a Core `INetworkChangeNotifier` (event + `IDisposable`) with a BCL-backed `NetworkChangeNotifier` impl + a `FakeNetworkChangeNotifier` test double. **(d) zero-adapter teardown** = drive through `SwitchAdapterAsync`'s shared body (a private `SwitchCoreAsync(NetworkAdapter? target)`; `target == null` ⇒ build scope launch-default → empty enumerate → null IPv4), reusing the existing zero-adapter handling — NOT a forked teardown path. All four confirmed; decisions recorded in Completion Notes.
  - [x] Confirm the subscription home = `ShellViewModel`. Confirmed.

- [x] **Task 1 — The `INetworkChangeNotifier` Core seam + BCL impl + fake.** (AC: #10, #11, #14)
  - [x] Added `src/ohSpy.Core/Discovery/INetworkChangeNotifier.cs` (`public interface INetworkChangeNotifier : IDisposable { event EventHandler NetworkAddressChanged; }`, full XML-doc).
  - [x] Added `src/ohSpy.Core/Discovery/NetworkChangeNotifier.cs` (`internal sealed`, pure forwarder over the BCL static event, `Dispose()` detaches, no diagnostics).
  - [x] Added `tests/ohSpy.Core.Tests/Fakes/FakeNetworkChangeNotifier.cs` (`Raise()`, `RaiseOffThreadAsync()` via `Task.Run`, idempotent `Dispose()` counting `DisposeCount`).

- [x] **Task 2 — The diagnostic constant (pinned-set, all three sites together).** (AC: #8, #9)
  - [x] `DiagCategories.cs`: added `AdapterNetworkChanged = "Adapter.NetworkChanged"` after `AdapterSwitchTimeout`, before `Rescan`, with XML-doc.
  - [x] `DiagCategoriesTests.cs`: added `"AdapterNetworkChanged"` to `expectedNames` (Adapter group).
  - [x] Architecture Decision 8 constants list: added the `AdapterNetworkChanged` row (lands with Task 7's amendment, consistent).

- [x] **Task 3 — The debounce + evaluate logic in ShellViewModel.** (AC: #1, #2, #3, #4, #5, #6, #7, #8)
  - [x] Ctor-injected `INetworkChangeNotifier networkChangeNotifier`; stored; NOT subscribed in ctor. Added `_networkChangeDebounce` (`Func<TimeSpan,CancellationToken,Task>`, default `Task.Delay`) + `SetNetworkChangeDebounceForTest(...)` + `_debounceWindow` (default `TimeSpan.FromSeconds(2)`).
  - [x] `StartAsync` subscribes `_networkChangeNotifier.NetworkAddressChanged += OnNetworkAddressChanged;` after the scope is constructed.
  - [x] `OnNetworkAddressChanged` (raw off-thread handler) does only the trailing-edge debounce reset (Interlocked.Exchange the CTS, cancel+dispose prior, kick fire-and-forget `DebouncedEvaluateAsync`). `DebouncedEvaluateAsync` awaits the delay seam then `_ui.Post(() => EvaluateNetworkChangeAsync())`; OCE = coalesced; other faults → Warning (A26).
  - [x] `EvaluateNetworkChangeAsync` (UI-thread post-marshal): re-enumerate; still-bound → no-op (no diagnostic); else `best = adapters[0]` → Information `AdapterNetworkChanged` (`old → new`) then `SwitchAdapterAsync(best)`; `best == null` → `SwitchToZeroAdapterAsync()`.
  - [x] Re-entrancy: NO new guard. Reuses `SwitchAdapterAsync`'s `_switching` guard. Evaluation reads `CurrentAdapterIPv4` AFTER the marshal.

- [x] **Task 4 — Subscription lifecycle + dispose.** (AC: #11, #14)
  - [x] `DisposeAsync` unsubscribes, cancels (await `CancelAsync`) + disposes any pending debounce CTS, and `_networkChangeNotifier.Dispose()` — BEFORE tearing the scope down.
  - [x] App DI: `services.AddSingleton<INetworkChangeNotifier, NetworkChangeNotifier>();` added before the `ShellViewModel` registration.

- [x] **Task 5 — Tests (Core only).** (AC: #1–#11, #14)
  - [x] `ShellViewModelTests` extended (mutable `StubAdapterEnumerator.SetAdapters`, `FakeNetworkChangeNotifier` in `NewHarness`/`NewHarnessEmptyAdapters`, `WaitForNetworkChangeEvaluationForTestAsync` seam for determinism): 8 new tests — AC #2 rebind-to-best, AC #3 no-op, AC #4 burst-coalesce (gated infinite delay proves trailing-edge), AC #5 zero-adapter teardown + the return-rebind follow-on, AC #6 Action H (DeferredUiDispatcher + off-thread raise), AC #7 re-entrancy (GatedUiDispatcher manual switch parked → auto rejected), AC #11 lifecycle.
  - [x] All new tests `[Trait("fr", "FR-057")]`.
  - [x] `DiagCategoriesTests` exact-set green.

- [x] **Task 6 — Build clean + fix the ctor blast radius.** (AC: #14)
  - [x] Core `-warnaserror` 0/0; App build 0/0 (the single pre-existing benign WMC1506 on MainWindow.xaml:162 is the only `-warnaserror` casualty — pre-existing, no XAML touched). Ctor sites fixed: both `ShellViewModelTests` harnesses + `SoakHarness` (inert `InertNetworkChangeNotifier`). `AdapterSwitchPopupCascadeTests` does NOT construct `ShellViewModel` (no change needed). Soak project builds 0/0.

- [x] **Task 7 — PRD FR-057 + Architecture Amendment A34.** (AC: #12, #13) FR-057 inserted into the PRD after the FR-050 block, before §4.12 Diagnostics. Amendment A34 appended after A33, before `### Decision 13`; `AdapterNetworkChanged` row added to the Decision 8 constants list.

- [x] **Task 8 — Run the full suite + record results.** (AC: #14) Core suite **574 passed / 2 skipped / 0 failed** (baseline 566/2 → +8 FR-057). `DiagCategoriesTests` exact-set + `DiagCategoriesUsageTests` + `CoreAppBoundaryTests` + `AsyncDisciplineTests` + chaos (`category=chaos` 1/1) all green.

- [ ] **Task 9 — Manual smoke (live, FIRST-CLASS gate).** (AC: #2, #5) DEFERRED to the Project Lead — cannot run in the headless dev environment (there is no second real network). Story ends at `review`. See Dev Notes §"Manual smoke" for the script.

## Dev Notes

### The four design decisions (settled — confirm in the Dev Agent Record before coding)

1. **Auto-target policy = auto-pick the first eligible adapter (the FR-048 launch-default), NOT clear-and-prompt.** When the bound adapter is gone, rebind to `Enumerate().FirstOrDefault()` — the exact same "first eligible" rule the app uses at launch (FR-048: "At startup the system MUST default to the first eligible adapter") and that the manual switch leaves to the operator. Rationale: the whole POINT of FR-057 is that the operator should NOT have to manually re-pick ("MUST NOT require an operator to manually re-pick the adapter" — proposal §4.2). A clear-and-prompt would re-introduce the manual step we are removing. The operator can still override via `View → Network adapter` afterward (5.2 is untouched). This mirrors how Story 2-11's expiry "just clears" without asking. **Open question flagged to the Project Lead** (Q1): on a multi-NIC host, "first eligible" may not be the *intended* network — but it is the deterministic, no-prompt default, and matches launch behaviour; revisit only if the smoke shows it picking the wrong NIC.

2. **Debounce window = 2 s, trailing-edge, test-settable.** A network transition fires a *burst* of `NetworkAddressChanged` (interfaces flap up/down; DHCP settles; IPv6 + IPv4 each notify). We want the *settled* state, so we debounce on the **trailing edge**: each event resets a 2 s timer; we evaluate only after 2 s of quiet. 2 s is comfortably longer than a typical DHCP-settle burst yet well inside "responsive" for a human moving a laptop. The window is NOT load-bearing for correctness (a too-short window just risks evaluating against a still-settling state — the next event re-triggers; a too-long window delays the rebind). Make it a `Func<TimeSpan,CT,Task>` delay seam (the `SubscriptionClient._delay` / `ShellViewModel._rescanDelay` precedent) so tests drive it to (effectively) zero — NO real 2 s sleeps anywhere. **Open question flagged** (Q2): 2 s vs 1 s vs 3 s — default 2 s; tune from the smoke.

3. **Core seam = `INetworkChangeNotifier` (event + `IDisposable`), BCL-backed impl + a fake.** The BCL `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` is a **static event** — a unit test cannot raise it, and subscribing in `ShellViewModel` directly would make the rebind logic untestable + couple Core to a process-global. The seam is the standard testability move (the `IClock` / `IUiDispatcher` / `INetworkAdapterEnumerator` pattern — Core already abstracts the BCL `NetworkInterface` enumeration behind `INetworkInterfaceSource`/`INetworkAdapterEnumerator` for exactly this reason). `NetworkChange` lives in `System.Net.NetworkInformation` (BCL, referenced by Core today — `NetworkAdapterEnumerator` uses `System.Net.NetworkInformation.NetworkInterfaceType`), so the seam + impl are both **Core**; `CoreAppBoundaryTests` stays green. The only App concern is DI registration.

4. **Zero-adapter teardown = drive through `SwitchAdapterAsync`, do NOT fork.** `SwitchAdapterAsync` already handles the zero-adapter case correctly: it builds a fresh `AdapterScope`, calls `scope.StartAsync(target)` (where a target with no matching eligible adapter, or the launch-default path, yields `CurrentAdapterIPv4 == null`), and `StartBoundServicesAsync` starts nothing inbound when `CurrentAdapterIPv4 is null` (NFR-R5; ShellViewModel.cs:271-304). The registry + SSDP log are cleared by the FR-050 sequence regardless. So the zero-adapter case is just "rebind to nothing". **Implementation note (verify against shipped `AdapterScope.StartAsync`):** `SwitchAdapterAsync(NetworkAdapter newAdapter)` is non-null-typed and short-circuits on `newAdapter.IPv4.Equals(CurrentAdapterIPv4)`. For the zero-adapter case you need a target that (a) is not equal to the current IPv4 (so it doesn't no-op) and (b) drives `AdapterScope.StartAsync` to the null-CurrentAdapterIPv4 outcome. Read `AdapterScope.StartAsync(NetworkAdapter? preferred)` — it binds the *chosen* adapter if supplied, else `Enumerate().FirstOrDefault()`. Since `Enumerate()` is now empty, passing `null` (launch-default) yields the zero-adapter scope. **Cleanest seam: add an overload / internal path `SwitchToZeroAdapterAsync()`** that runs the SAME teardown+rebuild body as `SwitchAdapterAsync` but builds the scope with `preferred: null` (→ empty enumerate → null CurrentAdapterIPv4), OR generalise `SwitchAdapterAsync` to take a `NetworkAdapter?` (null = zero-adapter). **Pick one and document; prefer the minimal change that does not fork the FR-050 sequence.** Confirm the chosen shape preserves the `_switching` guard + the marshalled clear + the diagnostic. (This is the one place the story may need a small, surgical change to the shipped `SwitchAdapterAsync` signature — keep it backward-compatible with the 5.2 menu call.)

### The `INetworkChangeNotifier` seam (the load-bearing testability decision)

```csharp
// src/ohSpy.Core/Discovery/INetworkChangeNotifier.cs
namespace ohSpy.Core.Discovery;

/// <summary>
/// Test-fakeable abstraction over <c>System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged</c>
/// (FR-057). The BCL static event cannot be raised by a unit test and roots its subscribers for process
/// life, so Core consumes this seam instead. The event is raised on a NON-UI thread — consumers MUST
/// marshal any observable-state mutation via <c>IUiDispatcher.Post</c> (Action H).
/// </summary>
public interface INetworkChangeNotifier : IDisposable
{
    event EventHandler NetworkAddressChanged;
}

// src/ohSpy.Core/Discovery/NetworkChangeNotifier.cs — internal sealed
internal sealed class NetworkChangeNotifier : INetworkChangeNotifier
{
    public event EventHandler? NetworkAddressChanged;
    public NetworkChangeNotifier() =>
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnBcl;
    private void OnBcl(object? s, EventArgs e) => NetworkAddressChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose() =>
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnBcl;
}
```

- Keep it `internal sealed` (the `NetworkAdapterEnumerator` precedent); register via the public interface.
- NO diagnostics / no logic inside it — pure forwarder. The VM owns the debounce + the decision + the diagnostic.
- The fake: `FakeNetworkChangeNotifier { public void Raise() => NetworkAddressChanged?.Invoke(this, EventArgs.Empty); public int DisposeCount; public void Dispose() => DisposeCount++; }`. For the Action H test, raise off-thread: `Task.Run(() => notifier.Raise())`.

### Where the subscription lives (ShellViewModel, NOT DiscoveryService)

`ShellViewModel` is the right home and matches the 5.2 / 5.3 precedents:
- It **owns the adapter-selection decision**: `_adapterScope`, `SwitchAdapterAsync`, the `_switching` re-entrancy guard, `_adapterEnum` (`EnumerateAdapters()`), `CurrentAdapterIPv4`, `IsCurrentAdapter`, and `_ui` for marshalling — everything the evaluation needs is already here.
- It already owns the **app-lifecycle wiring** point (`StartAsync(appToken)` / `DisposeAsync`) where the BCL subscription must be armed + torn down.
- **Why NOT `DiscoveryService`:** DiscoveryService owns the per-adapter read loop + (post-2.11) the expiry sweep — it is *torn down and rebuilt* by an auto-rebind (`SwitchAdapterAsync` → `RebindAsync`). It is the wrong layer to host a process-lifetime network-change subscription that *triggers* that teardown. The 2.11 sweep lives in DiscoveryService because it is *intrinsic to the adapter scope*; the network-change listener is *above* the scope (it decides when to replace the scope) — so it belongs in the VM, exactly like the manual `SwitchAdapterAsync` does.

### Debounce shape (trailing-edge, fire-and-forget, marshalled)

```csharp
// fields
private readonly INetworkChangeNotifier _networkChangeNotifier;
private TimeSpan _debounceWindow = TimeSpan.FromSeconds(2);                 // FR-057 default (test-settable)
private Func<TimeSpan, CancellationToken, Task> _networkChangeDebounce
    = (d, ct) => Task.Delay(d, ct);                                          // SubscriptionClient._delay precedent
private CancellationTokenSource? _debounceCts;
internal void SetNetworkChangeDebounceForTest(Func<TimeSpan,CancellationToken,Task> d, TimeSpan window)
    { _networkChangeDebounce = d; _debounceWindow = window; }

// raw off-thread handler — schedules only (AC #1: no inline work)
private void OnNetworkAddressChanged(object? sender, EventArgs e)
{
    // Trailing-edge debounce: cancel the prior pending evaluation, start a fresh window (AC #4).
    var prior = Interlocked.Exchange(ref _debounceCts, new CancellationTokenSource());
    prior?.Cancel(); prior?.Dispose();
    var token = _debounceCts!.Token;
    _ = DebouncedEvaluateAsync(token); // A26 fire-and-forget; body swallows OCE + faults
}

private async Task DebouncedEvaluateAsync(CancellationToken token)
{
    try
    {
        await _networkChangeDebounce(_debounceWindow, token).ConfigureAwait(false);
        // Marshal the evaluation onto the UI thread (Action H — the event + this continuation are off-thread).
        _ui.Post(() => { _ = EvaluateNetworkChangeAsync(); });
    }
    catch (OperationCanceledException) { /* superseded by a newer event — coalesced (AC #4) */ }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        _diag.Warning(DiagCategories.AdapterNetworkChanged, "network-change evaluation failed",
            new DiagnosticContext { ErrorText = ex.Message });
    }
}

private async Task EvaluateNetworkChangeAsync()   // runs on the UI thread (post-_ui.Post)
{
    var adapters = _adapterEnum.Enumerate();
    var current = CurrentAdapterIPv4;
    var stillBound = current is not null && adapters.Any(a => a.IPv4.Equals(current));
    if (stillBound) return;                                              // AC #3 no-op (no diagnostic)

    var best = adapters.FirstOrDefault();                               // AC #2 / #5
    var oldIp = current?.ToString() ?? "(none)";
    _diag.Information(DiagCategories.AdapterNetworkChanged, "network change → auto-rebind",
        new DiagnosticContext { ErrorText = $"{oldIp} → {(best?.IPv4.ToString() ?? "(no eligible adapter)")}" });
    if (best is not null) await SwitchAdapterAsync(best).ConfigureAwait(false);
    else                  await SwitchToZeroAdapterAsync().ConfigureAwait(false);  // AC #5 (see §"Zero-adapter teardown")
}
```

- The exact `_ui.Post` / `PostAsync` shape should match the shipped marshalling idiom in `SwitchAdapterAsync` (the `_ui.PostAsync(() => { ...; return true; })` pattern) — reconcile against shipped code and keep one style.
- `EvaluateNetworkChangeAsync` reading `CurrentAdapterIPv4` *after* the marshal is deliberate (AC #7): if a manual switch landed during the debounce, the evaluation sees the *new* current adapter and correctly no-ops (or rebinds against the new state).

### Re-entrancy + the mid-switch change (AC #7)

- `SwitchAdapterAsync` already takes `_switching` (`Interlocked.Exchange(ref _switching, 1) == 1` → reject). The auto path calls the SAME method, so a manual switch in flight makes the auto call return early (rejected, with the existing "adapter switch rejected — a switch or startup is already in progress" Information diagnostic). **Do NOT add a second guard** (the 5.3 Rescan added its own guard because Rescan and Switch must coexist; here the auto-rebind IS a switch, so it shares the switch guard — that is the correct serialisation).
- **The mid-switch-lost-event corner:** if a network change fires *while* a manual switch holds the guard, the auto `SwitchAdapterAsync` is rejected. That is acceptable because: (a) the manual switch is itself a rebind to a (presumably live) adapter; (b) the trailing-edge debounce means the *last* event in the burst usually lands after the manual switch settles; (c) the 2-11 expiry backstop clears any residue. If you want belt-and-braces, the evaluation can re-check `stillBound` after a rejected switch and re-arm one debounce cycle — OPTIONAL, document if added; do not over-engineer.

### Reuse vs new (reconciliation against SHIPPED code)

| Need | Shipped seam to REUSE | New code |
|---|---|---|
| Re-enumerate eligible adapters | `INetworkAdapterEnumerator.Enumerate()` (`EnumerateAdapters()` on the VM) | — |
| Current adapter IPv4 / still-bound check | `CurrentAdapterIPv4`, `IsCurrentAdapter(adapter)` | — |
| Atomic rebind (clear registry+log, cancel fetches, FR-037 popups, rebind, re-discover) | `ShellViewModel.SwitchAdapterAsync(NetworkAdapter)` (Story 5.2 / FR-050) | maybe a `SwitchToZeroAdapterAsync()` overload (§"Zero-adapter teardown") |
| Re-entrancy guard | the existing `_switching` `Interlocked` flag | — |
| Marshalling | `IUiDispatcher.Post` / `PostAsync` (the shipped `SwitchAdapterAsync` idiom) | — |
| Debounce delay seam | the `SubscriptionClient._delay` / `ShellViewModel._rescanDelay` pattern | `_networkChangeDebounce` + `SetNetworkChangeDebounceForTest` |
| Network-change event | — | `INetworkChangeNotifier` + `NetworkChangeNotifier` + `FakeNetworkChangeNotifier` |
| Diagnostic | the `DiagCategories` pinned-set pattern | `AdapterNetworkChanged` constant (triple-synced) |

**Do NOT** re-implement the FR-050 teardown sequence, the popup FR-037 cascade, the registry clear, or the M-SEARCH re-sweep — `SwitchAdapterAsync` does all of it. This story is a *trigger* in front of the shipped switch.

### Standing gates / boundaries

- **Core seam + VM wiring + thin App DI** — production changes: `Discovery/INetworkChangeNotifier.cs` (NEW), `Discovery/NetworkChangeNotifier.cs` (NEW), `Diagnostics/DiagCategories.cs`, `ViewModels/ShellViewModel.cs` (subscribe + debounce + evaluate + dispose + maybe a zero-adapter overload), and `App/Composition/ServiceRegistration.cs` (ONE `AddSingleton` line). `CoreAppBoundaryTests` MUST stay green — the seam is Core, the BCL impl is Core, only the DI registration is App.
- **`-warnaserror` 0/0** Core; App build only the pre-existing benign WMC1506.
- **Chaos hook** stays green (`dotnet test --filter "category=chaos"`).
- **Action H** (`winui-no-synccontext-marshal-vm`): `NetworkAddressChanged` fires off-thread → the evaluation + rebind MUST be marshalled via `IUiDispatcher.Post`, proven by a `DeferredUiDispatcher` guard test. This is the load-bearing threading risk (mirrors the off-thread `Lapsed(AdapterSwitch)` marshalling in 4.3 and the 2.11 sweep).
- **Smoke-per-UI-story / live gate**: a live smoke (physically move the PC between two networks) is a FIRST-CLASS gate; story ends at `review`, not done (the Epic 2 retro lesson + the project's `smoke-per-ui-story` rule; the 2-11 + 5.2 precedent of ending at `review`).
- **No struct data-binding** is involved here (no new bound collections) — but keep the diagnostic context a `DiagnosticContext` (readonly record struct, never bound directly — memory `winui-no-struct-databinding`).

### Test plan (Core only)

- Extend the shipped `ShellViewModelTests` switch rig (`NewHarness` + `StubAdapterEnumerator` + `SwitchRecorder` + `DeferredUiDispatcher` + `GatedUiDispatcher` — all from Story 5.2). The new ctor param takes a `FakeNetworkChangeNotifier`.
- Drive the debounce via `SetNetworkChangeDebounceForTest` (return immediately, or a `TaskCompletionSource` the test controls to prove coalescing). NO real 2 s sleeps.
- `StubAdapterEnumerator` must support *changing* its `Enumerate()` result between calls (so a test can flip A→[B] / A→[] to simulate the network move) — extend it if it is currently fixed-list.
- The seven assertions map 1:1 to AC #1–#7 + #11 (see Task 5). Trait `[Trait("fr", "FR-057")]`.
- The Action H test is MANDATORY and MUST use `DeferredUiDispatcher` + an off-thread `Raise()` (`InlineUiDispatcher` masks a missing `Post`).

### PRD FR-057 (Task 7 — the story authors this; insert after the FR-050 block, before §4.12 Diagnostics)

```
#### FR-057: Rebind on host network change

When the host's network changes while ohSpy is running — the bound adapter's IPv4 address changes, or the adapter is removed/disabled (e.g. moving the PC between networks) — ohSpy MUST detect the change and rebind: re-enumerate eligible adapters (FR-048) and, if the currently-bound adapter is no longer eligible, atomically rebind to the best available adapter (the FR-050 sequence) or, if none is eligible, tear down to the zero-adapter state (NFR-R5). Devices from the now-unreachable network are cleared as part of the rebind. The detection MUST debounce the burst of OS notifications a transition produces, and MUST NOT require an operator to manually re-pick the adapter.

- **Detection:** subscribe to `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` (via a test-fakeable Core seam). The event fires on a non-UI thread; all observable-state mutation is marshalled onto the UI thread.
- **Debounce:** a network transition produces a rapid burst of notifications; ohSpy debounces (trailing-edge, ~2 s) so a single rebind evaluation runs once the burst settles.
- **Auto-target:** rebind to the first eligible adapter (the FR-048 launch-default) — no operator prompt. The operator may still override via `View → Network adapter` (FR-048).
- **No-op:** if the bound adapter is still eligible with an unchanged IPv4 (a different NIC changed, or a transient blip), ohSpy does nothing.
- **Re-entrancy:** the auto-rebind shares the FR-050 switch's re-entrancy guard with the manual switch — the two never run concurrently.
- **Diagnostic:** a network-triggered rebind emits a distinct `Adapter.NetworkChanged` diagnostic (Information) carrying old → new adapter IPv4, so the FR-041 Diagnostics viewer shows the rebind was network-triggered.
- **Backstop:** with FR-056 (expiry) in place, stale devices also age out if a rebind target is not found; FR-057 is the responsive fix, FR-056 the safety net.
```

### Architecture Amendment A34 (Task 7 — append after A33, before `### Decision 13`; add the `AdapterNetworkChanged` row to the Decision 8 / Pattern-11 constants list)

```
### Amendment A34 — Host network-change auto-rebind (NetworkAddressChanged → SwitchAdapterAsync; FR-050 / Decision 9 complement)

**Source:** Sprint Change Proposal 2026-06-11 (correct-course); surfaced in real-world use after the Epic 6 install — moving the PC between networks (office→home) left the unreachable subnet's devices visible for a day. There was NO `NetworkChange.NetworkAddressChanged` listener anywhere; the app only rebound on a MANUAL `View → Network adapter` pick (Story 5.2 / FR-050). Authored in Story 2.12. Implements PRD FR-057.

**The gap:** the atomic adapter rebind (`ShellViewModel.SwitchAdapterAsync`, Story 5.2 / FR-050) — which clears the registry + SSDP log, cancels in-flight fetches, flips open popups to FR-037, rebinds the transport + callback host on the new adapter, and re-runs the M-SEARCH sweep — was only ever triggered by the operator. On a host network change (the bound adapter's IPv4 changes, or the adapter disappears), the app kept the dead scope bound; the stale devices never byebye'd, and (pre-2.11) never expired. Story 2.11's expiry (FR-056) is the eventual safety net; FR-057 is the responsive trigger.

**The amendment:** ohSpy subscribes to `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` through a new test-fakeable Core seam **`INetworkChangeNotifier`** (the BCL static event is not directly raisable in a test and roots its subscribers for process life — the `INetworkAdapterEnumerator` / `IClock` testability pattern). On the event, `ShellViewModel` **debounces** the OS notification burst (trailing-edge, ~2 s, test-settable delay seam — the `SubscriptionClient._delay` precedent), then re-enumerates eligible adapters (`INetworkAdapterEnumerator.Enumerate()`). If the bound adapter is no longer eligible, it drives the EXISTING `SwitchAdapterAsync(best-eligible)` (auto-target = the FR-048 launch-default) or, if none is eligible, tears down to the zero-adapter state via the same FR-050 sequence (NFR-R5). The stale network's devices are cleared as part of the rebind.
- **Owner:** `ShellViewModel` (it owns the adapter-selection decision: `_adapterScope`, `SwitchAdapterAsync`, the `_switching` re-entrancy guard, `_adapterEnum`, `_ui`). NOT `DiscoveryService` (that owns the per-adapter read/sweep loop — the layer that gets torn down + rebuilt by a rebind, the wrong place for the process-lifetime listener that triggers it). The subscription is armed in `StartAsync` and torn down (detach + dispose) in `DisposeAsync` — a leaked BCL static-event handler is a process-life leak.
- **Re-entrancy:** the auto-rebind calls the SAME `SwitchAdapterAsync`, sharing its existing `_switching` `Interlocked` guard — a manual switch and an auto-rebind cannot run concurrently (no second guard). A manual pick in flight wins; a network event landing mid-switch re-evaluates against the new current adapter on the next debounce cycle (the 2.11 expiry backstops any residue).
- **Marshalling (Action H / `winui-no-synccontext-marshal-vm`):** `NetworkAddressChanged` fires on a non-UI thread; the debounce continuation + the re-enumerate/rebind evaluation are marshalled onto the UI thread via `IUiDispatcher.Post`. Proven by a `DeferredUiDispatcher` guard test.
- **Diagnostic:** a NEW `DiagCategories.AdapterNetworkChanged = "Adapter.NetworkChanged"` (Information; context: ErrorText = old → new adapter IPv4). Pinned-set change — added to `DiagCategories.cs`, `DiagCategoriesTests.expectedNames`, and this Decision-8 / Pattern-11 list together (the 5.1 `SsdpSearchObserved` / 5.3 `Rescan` / 2.11 `SsdpExpired` precedent). Distinct from `Adapter.Switch` (manual), `Adapter.Rescan`, and `Ssdp.Expired`.

**Applied to:** `INetworkChangeNotifier` + `NetworkChangeNotifier` (NEW Core seam over the BCL static event), `ShellViewModel` (subscribe + debounce + re-enumerate/rebind evaluation + dispose + the debounce delay seam; possibly a `SwitchToZeroAdapterAsync` overload reusing the FR-050 body for the zero-adapter case), `DiagCategories` (+ the test exact-set), `ServiceRegistration` (ONE `AddSingleton<INetworkChangeNotifier, NetworkChangeNotifier>()`). Core seam + VM wiring + thin App DI — the rebind machinery (FR-050) is reused unchanged. `CoreAppBoundaryTests` stays green (the seam + impl are Core; only the registration is App).
```

### Manual smoke (Task 9 — first-class gate, live two-network move)

1. Start ohSpy on network A (e.g. the office/lab network); confirm A's devices appear in the tree and `View → Network adapter` shows A checked.
2. **Move the PC to network B** — physically: unplug the wired adapter and join Wi-Fi B, or carry the laptop to the home network, or disable adapter A and enable adapter B. (The real-world repro is office→home.)
3. Wait the debounce window (~2 s after the network settles). **Expected:** A's stale devices CLEAR from the tree (the registry was cleared by the auto-rebind); the SSDP log clears + refills with B's traffic; B's devices discover + appear; the `View → Network adapter` check mark moves to B; an `Adapter.NetworkChanged` Information diagnostic (`A-IP → B-IP`) appears in the FR-041 Diagnostics viewer; any open popups for A's devices flip to the FR-037 device-unreachable banner; NO manual re-pick was needed.
4. **Zero-adapter case:** disconnect ALL networks (pull cable + Wi-Fi off). **Expected:** after the debounce, the tree clears, discovery stops, the app stays running + interactive (NFR-R5), and an `Adapter.NetworkChanged` (`A-IP → (no eligible adapter)`) diagnostic appears. Reconnect a network → it rebinds + re-discovers.
5. **No-op sanity:** on a multi-NIC host, change a *different* (non-bound) adapter (e.g. toggle a secondary NIC) → confirm the bound adapter's tree is UNDISTURBED (no clear, no `Adapter.NetworkChanged` diagnostic).
6. **Manual-switch coexistence:** while devices are present, do a manual `View → Network adapter` switch — confirm it still works exactly as in 5.2 (the auto-rebind wiring did not regress the manual path).

Story ends at `review`, NOT done — the live two-network move is the Project-Lead gate (it cannot run in the headless dev environment — there is no second real network).

### Project Structure Notes

- Core/App split holds: the seam (`INetworkChangeNotifier`) + the BCL impl (`NetworkChangeNotifier`) + all the decision logic (`ShellViewModel`) are Core; the ONLY App change is one `AddSingleton` registration line. `CoreAppBoundaryTests` must stay green.
- The new `ShellViewModel` ctor param (`INetworkChangeNotifier`) is the test/soak blast radius — every `ShellViewModel` construction site needs a `FakeNetworkChangeNotifier` (inert by default). This is the same kind of ripple Story 2.11 had with the `DiscoveryService` ctor arg; expect to touch `ShellViewModelTests`, `AdapterSwitchPopupCascadeTests`, and `SoakHarness.cs`.
- Naming: the diagnostic is `Adapter.NetworkChanged` (dotted, in the `Adapter.*` family with `Adapter.Switch` / `Adapter.Switch.Timeout` / `Adapter.Rescan`). The constant is `AdapterNetworkChanged`.
- No new DI cycle: `ShellViewModel` already ctor-injects `IDiagnosticEmitter` + everything else; `INetworkChangeNotifier` is a leaf dependency (no back-references). Register it BEFORE `ShellViewModel` in `ServiceRegistration` so it auto-resolves.

### References

- [Source: _bmad-output/planning-artifacts/sprint-change-proposal-2026-06-11.md §1 (Defect 2), §2, §4.2 (FR-057), §4.4 (Story 2-12), §4.5 (Amendment pointer)] — the requirements source (NOT epics.md).
- [Source: _bmad-output/implementation-artifacts/2-11-ssdp-maxage-expiry-inferred-byebye.md] — the SIBLING corrective story (committed d36316f); the safety-net backstop, the pinned-set/diagnostic precedent, the clock/delay seam precedent, the Action H DeferredUiDispatcher guard, and the "ends at review NOT done (manual smoke gate)" framing this story mirrors.
- [Source: _bmad-output/implementation-artifacts/5-2-adapter-switch-view-network-adapter-menu-atomic-rebind.md] — `ShellViewModel.SwitchAdapterAsync` (the reused FR-050 atomic rebind), the `_switching` re-entrancy guard, the zero-adapter handling, the marshalling idiom, and the `SwitchRecorder`/`GatedUiDispatcher`/`StubAdapterEnumerator` test fakes.
- Verified shipped code (read 2026-06-11): `ViewModels/ShellViewModel.cs` (`SwitchAdapterAsync` body L322-428, `StartAsync` L218-232, `StartBoundServicesAsync` L271-305 zero-adapter handling, `EnumerateAdapters`/`IsCurrentAdapter`/`CurrentAdapterIPv4` L203-213, `_switching` guard, ctor L85-108, `DisposeAsync` L448+, the `_rescanDelay`/`SetRescanDelayForTest` seam precedent); `Discovery/NetworkAdapterEnumerator.cs` + `INetworkAdapterEnumerator.cs` (`Enumerate()` → `IReadOnlyList<NetworkAdapter>`; the BCL `System.Net.NetworkInformation` is referenced by Core); `Discovery/AdapterScope.cs` (`StartAsync(NetworkAdapter? preferred)` binds chosen-or-launch-default; the only existing `NetworkChange` mention is a doc-comment — nothing subscribes); `Diagnostics/DiagCategories.cs` + `tests/.../Diagnostics/DiagCategoriesTests.cs` (the pinned set; `Adapter.Switch`/`Adapter.Switch.Timeout`/`Adapter.Rescan`/`Ssdp.Expired` already present); `App/Composition/ServiceRegistration.cs` (DI graph; `ShellViewModel` singleton at L202, `IDiagnosticsLauncher`-before-VM registration precedent).
- [Source: architecture.md#Decision 8 (constants list ~L1031-1033)] — the pinned DiagCategories set to amend. [Source: architecture.md#Amendment A33 (L3056)] — the append point (A34 goes after A33, before `### Decision 13` at L3074). [Source: architecture.md#Decision 9] — the registry/discovery lifecycle this complements.
- [Source: prd.md#FR-050 (L547-558)] — the atomic adapter-switch rebind reused here; FR-057 inserts after this block. [Source: prd.md#FR-048 (L528-533)] — the eligible-adapter / launch-default rule the auto-target reuses. [Source: prd.md#NFR-R5 (L642)] — the zero-adapter-host keep-running contract.
- Project memories: `winui-no-synccontext-marshal-vm` (the load-bearing off-thread-event marshalling), `smoke-per-ui-story` (the live gate), `winui-no-struct-databinding` (keep `DiagnosticContext` unbound).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (dev-story workflow, fully automated sub-agent).

### Debug Log References

- Core `-warnaserror`: caught VSTHRD103 (synchronous `Cancel()`) in the off-thread `OnNetworkAddressChanged` void handler — resolved with a scoped `#pragma warning disable VSTHRD103` (the handler cannot await; the cancel only trips a CTS, no blocking teardown). DisposeAsync uses `await CancelAsync()` (it is async).
- VSTHRD200 (Async suffix) + VSTHRD003 (await foreign task) on the test seam `WaitForNetworkChangeEvaluationForTestAsync` and the fake's `RaiseOffThreadAsync` — resolved by renaming + a scoped VSTHRD003 suppress (the joined tasks are deliberately fire-and-forget; the test rig captures no UI affinity → no deadlock). Mirrors the shipped `WaitForStartupAsync` VSTHRD003 precedent.
- AC #4 burst test initially failed (4 evaluations) under an instant debounce — an instant `Task.CompletedTask` delay completes each window before the next raise can cancel it, so coalescing was not exercised. Fixed with a gated `Task.Delay(Timeout.Infinite, ct)` per window (a newer raise's CTS cancels the parked window; a `settled` flag lets the final window complete) — this genuinely proves the trailing-edge reset.
- Soak `InertNetworkChangeNotifier` tripped CS0414 (`event ... = null` in Dispose, never invoked) — the Soak project treats it as error; resolved by dropping the assignment + a scoped `#pragma warning disable CS0067`.

### Completion Notes List

**Settled design decisions (Task 0, confirmed before coding):**
- **(a) Auto-target = first eligible adapter** (`adapters[0]`, the FR-048 launch default) — no clear-and-prompt (the whole point of FR-057 is to remove the manual re-pick). Open-Q1 (multi-NIC "first eligible" may not be the intended NIC) flagged to the Project Lead — deterministic + matches launch behaviour; revisit only if the smoke picks the wrong NIC.
- **(b) Debounce = 2 s trailing-edge**, test-settable via `_networkChangeDebounce` (`Func<TimeSpan,CancellationToken,Task>`) + `SetNetworkChangeDebounceForTest`. No real multi-second sleeps in tests. Open-Q2 (2 vs 1 vs 3 s) flagged — default 2 s; tune from the smoke.
- **(c) Core seam** `INetworkChangeNotifier` (event + `IDisposable`) + `internal sealed NetworkChangeNotifier` BCL forwarder + `FakeNetworkChangeNotifier`. `System.Net.NetworkInformation` is BCL → seam + impl are Core; `CoreAppBoundaryTests` stays green; only the `AddSingleton` is App.
- **(d) Zero-adapter teardown via `SwitchAdapterAsync`, not a fork.** Implemented by refactoring the public `SwitchAdapterAsync(NetworkAdapter)` to delegate (after its same-adapter no-op short-circuit) to a private `SwitchCoreAsync(NetworkAdapter? target)`. `target == null` ⇒ the new scope is built with `preferred: null` → `AdapterScope.StartAsync(null)` re-enumerates → finds the now-empty list → `CurrentAdapterIPv4 == null` → `StartBoundServicesAsync` starts nothing inbound (NFR-R5). `internal SwitchToZeroAdapterAsync() => SwitchCoreAsync(null)`. The 5.2 public signature is preserved (the View menu call is unchanged); the `_switching` guard, the marshalled registry/log clear, and the diagnostics are all reused. This was the one small, surgical change to shipped `SwitchAdapterAsync` the story anticipated.

**Implementation notes:**
- The completion diagnostic in `SwitchCoreAsync` now reads the real `newScope.CurrentAdapterIPv4` ("now on X" / "now on (no adapter)") so the zero-adapter teardown logs coherently — previously it echoed the requested IP, which would be wrong for a null target.
- Subscription home = `ShellViewModel`; armed in `StartAsync` (after scope construction, so a never-started test VM doesn't arm the BCL handler), torn down in `DisposeAsync` BEFORE the scope teardown (detach handler → cancel+dispose pending debounce CTS → dispose notifier) so a late event can't kick a rebind mid-dispose.
- Test determinism: the off-thread handler is fire-and-forget, so a `WaitForNetworkChangeEvaluationForTestAsync` seam retains the last debounce + evaluate task handles (production never awaits them) — lets tests await the debounce → marshal → evaluate chain instead of racing it. Under `DeferredUiDispatcher` the evaluate task is captured only after `Drain()`, which the Action H test exploits.
- `StubAdapterEnumerator` made mutable (`SetAdapters(params NetworkAdapter[])`, `volatile` backing field) so a test can flip the eligible set (A → [B], A → []) mid-run to simulate a host network move.

**PRD placement decision (documented):** FR-057 is topically an adapter-switch concern, so it was inserted after the FR-050 block (before §4.12 Diagnostics) per AC #12's explicit anchor — NOT physically next to FR-056, which Story 2.11 had placed in the §4.x device-removal narrative (a different topical section). This matches the AC's literal instruction and keeps the adapter-rebind FRs together.

**Flag for review / smoke:**
- The `DiagCategories.AdapterNetworkChanged` add is an INTENTIONAL pinned-set change (triple-synced: `DiagCategories.cs` + `DiagCategoriesTests.expectedNames` + architecture Decision 8 list), NOT drift.
- Noted: `Ssdp.Expired` (Story 2.11) is absent from the architecture Decision-8 constants block at ~L1031 (it appears 2.11 synced only its own narrative/Pattern-11 mention); I did NOT add it (out of scope) but flag it so a future PR can reconcile that block. `AdapterNetworkChanged` was added there correctly.
- AC #9 / #14 guards (`DiagCategoriesTests` exact-set, `DiagCategoriesUsageTests`, `CoreAppBoundaryTests`, `AsyncDisciplineTests`, chaos) all green.
- Manual smoke (Task 9 / AC #2,#5) is the Project-Lead live gate — deferred (no second real network headless).

### File List

**Production (Core):**
- `src/ohSpy.Core/Discovery/INetworkChangeNotifier.cs` (NEW)
- `src/ohSpy.Core/Discovery/NetworkChangeNotifier.cs` (NEW)
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` (added `AdapterNetworkChanged`)
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` (ctor param + debounce/evaluate/dispose + `SwitchCoreAsync`/`SwitchToZeroAdapterAsync` refactor + test seams)

**Production (App):**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` (ONE `AddSingleton<INetworkChangeNotifier, NetworkChangeNotifier>()`)

**Tests:**
- `tests/ohSpy.Core.Tests/Fakes/FakeNetworkChangeNotifier.cs` (NEW)
- `tests/ohSpy.Core.Tests/Fakes/SwitchRecorder.cs` (`StubAdapterEnumerator` made mutable: `SetAdapters`)
- `tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs` (Harness ctor sites + 8 FR-057 tests)
- `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs` (exact-set add)
- `tests/ohSpy.Soak.Tests/Harness/SoakHarness.cs` (ctor site + `InertNetworkChangeNotifier`)

**Docs:**
- `_bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md` (FR-057)
- `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (Amendment A34 + Decision 8 constant row)

### Change Log

- 2026-06-11 — Story 2.12 context created via bmad-create-story (claude-opus-4-8[1m]). CORRECTIVE (correct-course; source sprint-change-proposal-2026-06-11.md §4.2/§4.4). FR-057 network-change auto-rebind. Status → ready-for-dev.
- 2026-06-11 — Story 2.12 IMPLEMENTED via dev-story (claude-opus-4-8[1m]). NEW Core `INetworkChangeNotifier` seam + BCL `NetworkChangeNotifier` forwarder + `FakeNetworkChangeNotifier`; `ShellViewModel` subscribes in `StartAsync`, debounces (2 s trailing-edge, test seam), re-enumerates, and drives the EXISTING FR-050 `SwitchAdapterAsync(first-eligible)` or a zero-adapter teardown via a new `SwitchCoreAsync(NetworkAdapter?)`/`SwitchToZeroAdapterAsync` (reuse, no fork); off-thread event marshalled via `IUiDispatcher.Post` (Action H); shares the `_switching` guard (no second guard); detach+dispose in `DisposeAsync`. NEW `DiagCategories.AdapterNetworkChanged` (pinned-set triple-sync). PRD FR-057 + Architecture A34 authored. App: one `AddSingleton`. 8 new FR-057 tests. Core 574 passed / 2 skipped / 0 failed (from 566/2); App `-warnaserror` clean bar the pre-existing WMC1506; chaos + boundary + exact-set + async-discipline green. Status → review. Manual live smoke (Task 9) is the Project-Lead gate. NOT committed.
