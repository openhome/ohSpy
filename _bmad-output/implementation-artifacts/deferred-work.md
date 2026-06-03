# Deferred Work

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
