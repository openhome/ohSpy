# Deferred Work

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
