# Deferred Work

## ✅ RESOLVED 2026-06-04 — deferred UI smokes run on the live Linn network (Story 5.2 keystone smoke)

The Story 5.2 keystone smoke (live Linn/OpenHome network, multi-adapter) **cleared the whole deferred-smoke debt**: the **Story 4.3 event-stream smoke** (subscribe → live NOTIFY rows + latest-values + concurrent popups + lapse banner), the **Story 3.2 steps 5/6/7** (transport-error styling, device-gone banner, close-mid-invoke), and the **Story 3.3 numeric / off-step / loading** steps all PASSED. (Two App-side bugs were found + fixed en route: device-tree "Loading…" stuck `a55ed74`; subscription-popup NOTIFY-property render crash `63e2378`.) The `5.2` adapter switch superseded the retro Action-I `OHSPY_ADAPTER` override (not built). The individual entries below for those smokes are now historical.



## Deferred from: code review of 5-2-adapter-switch-view-network-adapter-menu-atomic-rebind (2026-06-04)

- **W1 — Narrow use-after-clear race: `ui.Post(registry.OnAlive)` from old read loop can land after `registry.Clear()`** [`src/ohSpy.Core/ViewModels/ShellViewModel.cs:262-273`] — `DrainInFlightFetchesAsync` yields 3 times before `registry.Clear()`. If a `ReadLoopAsync` continuation from the old transport's channel posts an `OnAlive` that lands on the UI queue AFTER `Clear()` runs, a stale device re-appears with a cancelled `DeviceCts`. Fix requires an aggregate fetch-task join handle (open-Q #3, architecture); window is extremely narrow because the old channel writer completes before the settle yields run.
- **W2 — `SwitchAdapterAsync_DuringStartup_IsRejected` does not exercise the live guarded-startup path** [`tests/ohSpy.Core.Tests/ViewModels/ShellViewModelTests.cs:233-249`] — test calls `WaitForStartupAsync()` before attempting the switch; only tests post-startup steady state. Guard is structurally sound (confirmed by code review). Fixing the test requires a seam into `RunStartAsync` to park startup mid-flight.
- **W3 — `AdapterScope.IncomingDatagrams` XML doc overpromises "Throws if accessed before StartAsync"** [`src/ohSpy.Core/Discovery/AdapterScope.cs:49-54`] — no runtime guard exists; real transport does not throw on the property access. Doc-only fix.
- **W4 — `Clear_RaisesDeviceRemovedPerUuid_DisposesEachCts_EmptiesRegistry` test name overclaims CTS disposal** [`tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs`] — the test asserts `IsCancellationRequested == true` but not disposal (CancellationTokenSource.Dispose is not observable from the token). Same `RemoveCore` path as `OnByebye` (already tested); minor naming gap.

## Deferred from: code review of 4-3-subscription-popup-event-list-latest-property-values-multiple-concurrent-popups (2026-06-04)

- **`SubscriptionPopupViewModel.StatusMessage` shows the SID, not the device-granted TIMEOUT** [`src/ohSpy.Core/ViewModels/SubscriptionPopupViewModel.cs`] — AC-4.3.1's example wanted the granted lease (e.g. "device-granted TIMEOUT: 300 s"), but the Story 4.2 `SubscriptionHandle` seam exposes only `Sid`, not the granted `Timeout`. SID is useful (Wireshark/log correlation) and the dev correctly did not break the 4.2 freeze for this. Future micro-story: add `TimeSpan GrantedTimeout` to `SubscriptionHandle` (plumbed from `SubscribeResponse.Timeout`, which `SubscriptionClient` already has) and surface it in the popup status line. Low priority, non-blocking.
- **Manual UI smoke (Story 4.3 Task 12) FULLY deferred** — requires an event-emitting Linn DS, reachable only via the retro Action-I `OHSPY_ADAPTER` dev override (the Sky IGD emits no useful events). Steps: subscribe to a DS service → newest-first stream + live latest-values; 2nd concurrent popup → independent; trigger lapse → reason banner; close mid-stream → clean UNSUBSCRIBE. Unlike 3.2/3.3 (where some steps passed), ZERO of 4.3's event-stream smoke can run on the current network → Epic 4's live-eventing payload is unverified on a real device until Action I lands. Core VM logic is fully unit-tested (incl. 6 DeferredUiDispatcher marshalling guards) as the compensating control.


## Deferred from: code review of 3-3-constrained-inputs-allowedvaluelist-dropdown-allowedvaluerange-numeric (2026-06-04)

- **`NoInputsVisibility` binding missing `Mode=OneWay`** [`src/ohSpy.App/Views/InvocationPopupWindow.xaml:136`] — pre-existing 3.2 pattern, not introduced by 3.3. `NoInputsVisibility` is a static one-time computation (arg count does not change) so the binding correctness is unaffected. Add `Mode=OneWay` in a future XAML clean-up pass alongside any similar cases in the window.
- **Manual smoke steps 2–5 deferred** (Story 3.3 Task 9) — requires a Linn DS network / Story 5.2 adapter switch. Deferred steps: (2) NumberBox `<allowedValueRange>` numeric + invariant wire value on `SetVolume 0..100 step 1`; (3) off-step inline error + Invoke refused; (4) fallback-to-text for a plain string arg; (5) "Loading…" state visible then resolves. FR-102 dropdown (step 1) was smoke-PASSED on live Sky network. Core VM logic for all deferred steps is unit-tested (`AllowedValueRangeArgumentViewModel`, off-step gate, `ResolveInput` fallback, `IsLoadingInputs` marshalling). Only the App-side template/projection is unverified for steps 2–5. Revisit when a Linn DS is reachable via the Story 5.2 adapter switch.

## Deferred from: code review of 3-1-soap-envelope-builder-fault-parser-and-invokeactionasync-wire-up (2026-06-03)

- **`$"unexpected status {(int)resp.StatusCode}"` — string interpolation in diagnostic message** [`src/ohSpy.Core/Http/UpnpHttpClient.cs:192`] — pre-existing from Story 1.3 baseline; Pattern 11 purists would keep the status-code out of the message (it's already in `DiagnosticContext.StatusCode`). Low cosmetic impact; revisit when a diagnostic-message cleanup pass is warranted.
- **`UpnpFault` declared `public` but only consumed Core-internally** [`src/ohSpy.Core/Soap/UpnpFault.cs:12`] — intentional dev choice; Story 3.2 may use it from App layer. Not a problem now; if 3.2 doesn't use it from App, consider narrowing to `internal` at that point.
- **A9: `UpnpTransportException` synthetic-inner form** [`src/ohSpy.Core/Http/UpnpExceptions.cs:40-44`] — Amendment A9 flagged `inner ?? new InvalidOperationException(message)` for replacement with `: base(message, inner)`. Dev deliberately out-of-scoped to keep 3.1 tight. Apply when any PR next touches `UpnpExceptions.cs`.

## Deferred from: code review of 2-7-ssdp-message-log-right-pane-virtualised-smart-auto-follow (2026-06-03)

- **`OnLogEntriesChanged` does not assert `NewStartingIndex == 0`** — The scroll handler in `MainWindow.xaml.cs` guards on `NotifyCollectionChangedAction.Add` but does not verify `e.NewStartingIndex == 0`. `BoundedObservableCollection` is sealed and `PrependNewest` is its only Add operation (always index 0), so this is safe today. If the collection gains an append or mid-insert operation in a future story, any `Add` at index > 0 would incorrectly trigger the at-top anchor or offset compensation. Low risk while `BoundedObservableCollection` remains prepend-only; revisit if new insertion variants are added. `src/ohSpy.App/MainWindow.xaml.cs:47`.

## Discovered during: first manual run / deployment check (2026-06-03)

- **`Program.cs` bootstrap call contradicts `WindowsAppSDKSelfContained=true` (runtime-availability bug).** `Program.Main` unconditionally calls `Bootstrap.TryInitialize(0x00020001, "", minVersion 2.1.3.0, …)` — the **framework-dependent** Windows App SDK bootstrapper, which requires an *installed* Windows App Runtime ≥ 2.1.3. But `ohSpy.App.csproj` sets `WindowsAppSDKSelfContained=true` + `SelfContained=true`, whose contract is that the runtime ships **next to the exe** and the bootstrapper is **not** used. The two are mutually exclusive, so the self-contained config is currently a no-op and every output (build *and* publish) behaves framework-dependent.
  - **Symptom:** on a machine without a 2.1.x runtime, the app dies at startup with a native MessageBox `"Windows App Runtime initialisation failed (0x80670016)"` (that dialog is `Program.Main`'s own `MessageBoxW`). Observed 2026-06-03: dev machine had only WinAppRuntime `2.0.1.0`; app targets WinAppSDK `2.1.3`. Worked around by registering the `2.1.3` framework/Main/Singleton/DDLM MSIX packages (from the `microsoft.windowsappsdk.runtime` NuGet package's `tools\MSIX\win10-x64`) via `Add-AppxPackage` — but that's a per-developer-machine fix, not a product fix.
  - **Impact — this WILL bite the installer (Epic 1 / Story 1.1).** A clean machine running the InnoSetup installer hits the identical `0x80670016` unless the runtime is present. Two coherent options, pick one:
    1. **Truly self-contained:** remove the `Bootstrap.TryInitialize`/`Bootstrap.Shutdown` calls from `Program.cs` (self-contained apps load the bundled runtime directly; no bootstrap), keep the csproj flags, and confirm `publish` lays down a runnable bundle. No runtime install required on target machines.
    2. **Framework-dependent:** drop the `WindowsAppSDKSelfContained`/`SelfContained` flags (they're misleading as-is) and make the InnoSetup installer carry + run `WindowsAppRuntimeInstall-x64.exe` (≥ 2.1.3) as a prerequisite.
  - Either way, add a clean-machine install/run smoke to the Epic 6 release-readiness checks (Story 6.3) so this can't regress silently. Note: the `Program.cs` comment ("Bind to the Windows App Runtime self-contained-published alongside this exe") states the self-contained *intent* but the chosen API does the opposite.

## Deferred from: code review of 2-6-service-action-expansion-lazy-scpd-incremental (2026-06-03)

- **AC-2.6.8 cancellation test only validates parser-path OCE** — `Expand_DeviceTokenCancelled_NoError_NoDiagnostic_AC268` pre-cancels the token but `StubUpnpHttpClient.ScpdResponder` does not check `ct`, so `FetchScpdAsync` succeeds and OCE is only observed at `StubScpdParser.ct.ThrowIfCancellationRequested()`. The HTTP-layer cancellation path is untested. Behaviour is correct in both cases; a targeted test would set `ScpdResponder = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(...); }`. Low risk — deferred to avoid complexity in the test-strategy pattern already established.

## Deferred from: code review of 2-5-main-window-shell-device-tree-top-level-rows (2026-06-03)

- **`DeviceNodeViewModel.ReplaceWith` emits Reset** — `Children.Clear()` + `Add` raises `NotifyCollectionChangedAction.Reset`, which collapses any expanded service subtrees. Harmless for the placeholder→real-children first swap, but a second `ReplaceWith` (service-list re-fetch) would collapse expansion — the exact failure mode FR-054 guards against at the top level. Surfaces when Story 2.6 wires real expansion; consider incremental child reconciliation there instead of Clear+Add.

## Deferred from: code review of 2-4-ssdp-parser-discoveryservice-wire-transport-into-registry (2026-06-02)

- **DiscoveryService not disposed on shutdown** — `App.xaml.cs` does not call `DiscoveryService.DisposeAsync`; adapterToken cancellation is the effective cleanup. Revisit when Story 5.2 implements proper adapter-switch lifecycle.
- **UTF-8 BOM in SSDP datagrams** — `Encoding.UTF8.GetString(byte[])` does not strip BOM preamble; conformant SSDP devices do not emit BOM in UDP, but non-conformant ones could cause the first-line check to fail silently.
- **`IsRootDevice` only checks NT** — returns `false` for M-SEARCH responses (`ST == "upnp:rootdevice"`, `NT == null`). By spec design; routing code uses `effectiveNt = ann.NT ?? ann.ST`. Document clearly so future consumers of `SsdpAnnouncement` don't use `IsRootDevice` for response routing.
- **HTTP/1.0 M-SEARCH responses rejected** — parser accepts `HTTP/1.1 200 OK` only. UPnP 1.0 devices may respond with `HTTP/1.0 200 OK`. Needs spec amendment if encountered.
- **Folded HTTP headers not handled** — obs-fold continuation lines treated as unknown headers. Deprecated by RFC 7230; not seen in practice.
- **`DisposeAsync` hangs if tokens not cancelled** — no internal timeout; production teardown via adapterToken cancellation is the expected path. Add `WaitAsync(TimeSpan)` guard if timeouts become a concern in Story 5.2.
- **adapterToken cancelled before `RouteOnUiThread` executes** — entry may enter registry with pre-cancelled `DeviceCts`; fetch immediately observes cancellation; self-healing on next alive message.
- **`discovery.StartAsync` exceptions swallowed** — `StartAdapterScopeAsync` catch block treats all non-OOM exceptions as "adapter startup failed", including programming errors like double-start.
- **`_started` guard does not prevent `StartAsync` after `DisposeAsync`** — second `StartAsync` post-dispose creates an orphaned task. Unreachable in singleton DI context.
- **AC-2.4.4 cancellation test uses arbitrary 200 ms timeout** — could flake on very slow CI; acceptable for current test environment.
- **`AnnouncementReceived` ordering** — event raised after `OnAlive`/`OnByebye` mutation; post-mutation state visible to subscribers; not documented on the event.

## Deferred from: code review of 2-1-ssdp-transport-multicast-search-sockets-with-bounded-channel (2026-06-02)

- **`StartAsync`/`DisposeAsync` concurrent-call race** — `SsdpTransport` has no per-instance lock; if `StartAsync` and `DisposeAsync` are called concurrently, partially-initialised socket/task state can be observed. By design: `AdapterScope` (Story 2.2) owns lifecycle sequencing and must ensure StartAsync completes before DisposeAsync is called. Revisit if the transport ever needs to be used outside an AdapterScope-managed context.

## Deferred from: code review of 1-6-fakeupnpdevice-minimal-modes-first-chaos-test-netarchtest-rules (2026-06-02)

- ~~**FluentAssertions 8.0.0 commercial license**~~ — **✅ RESOLVED 2026-06-02 at Epic 1 retrospective.** Downgraded to FluentAssertions 7.2.0 (last MIT-licensed) in `Directory.Packages.props`. All 126 tests still pass. See `epic-1-retro-2026-06-02.md`.
