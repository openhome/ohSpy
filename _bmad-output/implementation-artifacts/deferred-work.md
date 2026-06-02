# Deferred Work

## Deferred from: code review of 2-1-ssdp-transport-multicast-search-sockets-with-bounded-channel (2026-06-02)

- **`StartAsync`/`DisposeAsync` concurrent-call race** — `SsdpTransport` has no per-instance lock; if `StartAsync` and `DisposeAsync` are called concurrently, partially-initialised socket/task state can be observed. By design: `AdapterScope` (Story 2.2) owns lifecycle sequencing and must ensure StartAsync completes before DisposeAsync is called. Revisit if the transport ever needs to be used outside an AdapterScope-managed context.

## Deferred from: code review of 1-6-fakeupnpdevice-minimal-modes-first-chaos-test-netarchtest-rules (2026-06-02)

- ~~**FluentAssertions 8.0.0 commercial license**~~ — **✅ RESOLVED 2026-06-02 at Epic 1 retrospective.** Downgraded to FluentAssertions 7.2.0 (last MIT-licensed) in `Directory.Packages.props`. All 126 tests still pass. See `epic-1-retro-2026-06-02.md`.
