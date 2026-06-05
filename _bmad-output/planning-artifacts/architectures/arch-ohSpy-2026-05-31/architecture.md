---
stepsCompleted: [1, 2, 3, 4, 5, 6, 7, 8]
lastStep: 8
status: 'complete'
completedAt: '2026-06-01'
inputDocuments:
  - "_bmad-output/planning-artifacts/briefs/brief-ohSpy-2026-05-29/brief.md"
  - "_bmad-output/planning-artifacts/briefs/brief-ohSpy-2026-05-29/addendum.md"
  - "_bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md"
workflowType: 'architecture'
project_name: 'ohSpy'
user_name: 'Simonc'
date: '2026-05-31'
---

# Architecture Decision Document — ohSpy

_This document builds collaboratively through step-by-step discovery. Sections are appended as we work through each architectural decision together._

## Project Context Analysis

### Requirements Overview

**Functional Requirements (≈ 60 across 13 feature areas):**

- **4.1 Discovery & Device Registry** (FR-004..FR-008, FR-053, FR-054) — SSDP M-SEARCH + continuous NOTIFY listening on one IPv4 adapter; UDN-keyed registry (string identity, `OrdinalIgnoreCase` — see Amendment A30); root-only with three-layer enforcement; case-insensitive sort with stable identity.
- **4.2 Eager description fetch** (FR-043, FR-047) — async fetch on registry entry; tree visibility gated on `Loaded`; bounded parallelism (target 8); mismatched-root backstop.
- **4.3 Device tree** (FR-001, FR-002, FR-009..FR-013, FR-044, FR-045, FR-051) — two-pane layout; device → service → action; persistent expand chevron via "Loading…" placeholder; kind glyphs from a Windows-shipped font; muted secondary detail line (deviceType tail + host:port).
- **4.4 Lazy SCPD enumeration** (FR-012, FR-100) — fetch on service expand; incremental parse so a 100-action SCPD never freezes the UI.
- **4.5 SSDP log** (FR-003, FR-014..FR-016, FR-055, FR-101) — newest-first virtualised list capped at 10,000 with FIFO eviction; smart auto-follow.
- **4.6 XML viewing** (FR-017..FR-020) — right-click → open description/SCPD XML in default browser.
- **4.7 Device Properties window** (FR-052) — read-only, owned by main window, sections: Identity / Manufacturer / Network / Discovery history / Embedded.
- **4.8 Rescan** (FR-021..FR-024) — repeats startup probe; prunes non-responders; live listening continues unaffected.
- **4.9 Action invocation** (FR-025..FR-031, FR-102, FR-103) — SOAP POST popup; success / UPnP fault / transport-error displays; `<allowedValueList>` → constrained selector; `<allowedValueRange>` → constrained numeric input.
- **4.10 GENA subscription** (FR-032..FR-038, FR-104) — SUBSCRIBE on open; UNSUBSCRIBE on close; renew before timeout; multiple concurrent popups; non-serial NOTIFY processing; "Latest property values" summary anchored above newest-first event list.
- **4.11 Adapter selection** (FR-048..FR-050) — single adapter at a time; default = first eligible enumerated; atomic rebind sequence.
- **4.12 Diagnostics** (FR-039..FR-042) — structured entries; two sinks (rolling file under `%LOCALAPPDATA%` + in-memory ring); live viewer; logging never blocks UI and never blocks startup.
- **4.13 Secondary windows** (FR-037, FR-046) — main-window-owned z-order / lifetime contract for all popups; mid-interaction device disappearance handled cleanly.

**Non-Functional Requirements:**

- **Reliability** (NFR-R1..R5): no crashes over a 30-min session; slow devices don't hang the UI; popups recover from device disappearance; diagnostic logging failure does not block startup; zero-adapter host still runs.
- **Performance** (NFR-P1..P6): item-virtualised high-cardinality rendering; per-request HTTP timeout discipline; **no UI-thread blocking ever** (binding invariant — `.Result` / `.Wait()` forbidden); incremental large-XML parse; keyed identity-tracked collection updates; bounded discovery fan-out.
- **UI Polish** (NFR-UI1..UI4): WinUI 3 design conventions; considered tree-row hierarchy; no flicker on incremental updates; smooth steady-state interaction on contemporary Windows hardware.

**Performance Budgets (§6) — anchored to verifiable scenarios:**

- Startup → tree populated ≤ ~7 s; warm SCPD expand ≤ 100 ms; cold 100-action SCPD expand ≤ 2 s with no UI freeze.
- Eager-fetch concurrency ≤ 8; SSDP log cap 10,000; diagnostic ring ~5,000; on-disk log ≤ 16 MB total (≤ 8 × ≤ 2 MB).
- 8-hour scale ceiling: 20 devices + 5 subscription popups + saturated SSDP log < 200 MB resident.
- Sustained chatty-SSDP target ≥ 20 adv/s for ≥ 30 s without dropped frames or main-thread stalls > 16 ms.

**Scale & Complexity:**

- Primary domain: native Windows desktop with embedded UPnP / SOAP / GENA protocol stack.
- Complexity level: **medium** — small footprint (single user, single process, no persistence, no auth, no compliance regime), but architecturally non-trivial because of in-process HTTP server, four-window lifecycle, async cancellation plumbing across adapter switch, identity-tracked tree updates, and incremental XML parsing.
- Estimated architectural components: ~15 (UI shell + tree + log VMs, popups × 4, discovery, registry, eager-fetch dispatcher, SCPD parser, control client, subscription client + callback host, diagnostics sinks, dispatcher abstraction).

### Technical Constraints & Dependencies

- **Platform:** Windows-only, IPv4-only, single adapter at a time, single host process. No cross-platform support, no IPv6, no multi-NIC merging (explicit Non-Goals).
- **PRD-locked technical shapes:** raw-BCL UPnP stack (no third-party library); `TcpListener` callback host (not `HttpListener`); no `netsh http` URL ACL; no Administrator privileges required; in-memory operation only (no settings persistence, no cross-session state); unsigned MSIX installer for internal Linn distribution.
- **Performance discipline as constraint:** every outbound HTTP request bounded by per-request timeout; every async path async end-to-end (no sync-over-async); every high-cardinality list virtualised; every collection update identity-tracked.
- **Out of scope:** consumer concerns (no localisation, no theming, no a11y, no error-message polish work), pathological/adversarial UPnP fuzz traffic, persistence, type-specific input pickers beyond `<allowedValueList>` / `<allowedValueRange>`, per-service rich event interpretation.

### Cross-Cutting Concerns Identified

- **Threading model & UI dispatcher contract.** All registry / VM mutations must marshal to the UI thread via a dispatcher abstraction; all I/O off the UI thread. NFR-P3 is the binding invariant.
- **Cancellation plumbing.** Adapter switch (FR-050) and byebye (FR-008) / rescan-prune (FR-023) must cancel every in-flight description fetch, SCPD fetch, SOAP invocation, and SUBSCRIBE / UNSUBSCRIBE — and inform every dependent open popup (FR-037). Cancellation tokens must be threaded coherently from the SSDP / registry layer down into HTTP clients and popup view-models.
- **Bounded collections everywhere.** SSDP log (10K FIFO), per-subscription event list (~5K FIFO), diagnostic ring (~5K FIFO), on-disk diagnostic file (≤ 16 MB rolling), eager-fetch semaphore (8). All identity-tracked where the UI consumes them (NFR-P5).
- **Diagnostic emission on every error path.** SSDP parse, description fetch, SCPD fetch, SOAP transport, SUBSCRIBE / UNSUBSCRIBE establish / renew / cancel — every failure path lands a structured `DiagnosticEntry` with a stable context schema (`device.uuid`, `url`, status code, error text), consumed identically by the rolling file sink and the live viewer (FR-041 columns).
- **Per-request HTTP timeout discipline.** A single `HttpClient` instance (or pool) cannot rely on its default 100 s timeout; every request site applies a per-call `CancellationTokenSource` with the appropriate budget. Architecture pins exact defaults (NFR-P2 Open Question).
- **Identity-tracked observable collections.** Reused at the device tree, the SSDP log, every subscription popup's event list, and the diagnostic viewer. A reusable bounded-newest-first collection primitive belongs in the Core project rather than four near-duplicates.
- **Embedded HTTP request parsing.** Hand-rolled HTTP/1.1 surface (FR-049) is its own component with its own hardening contract (header / body bounds, framing validation, per-request read timeout, 400-on-malformed).
- **Multi-window lifecycle.** Z-order ownership + lifetime contract (FR-046) and graceful device-disappearance behaviour (FR-037) implemented once and applied uniformly across the four secondary window types.
- **Distribution & packaging shape.** Unsigned MSIX with Windows App Runtime bundled (per addendum prior art); x64 + ARM64 publish profiles; no Admin / install steps; runs from `%LOCALAPPDATA%`.

## Starter Template Evaluation

### Primary Technology Domain

Native Windows desktop with embedded UPnP / SOAP / GENA protocol stack. WinUI 3 (Windows App SDK) + .NET. Single platform by deliberate scope.

### Starter Options Considered

| Option | What it is | Why considered | Verdict |
|---|---|---|---|
| **`dotnet new winui`** | Minimal official MS template — blank `App` project, `MainWindow.xaml`, `package.appxmanifest`, MSIX-ready, `net10.0-windows10.0.xxxxx.0` TFM. | Smallest viable WinUI 3 skeleton; reference shape every other approach builds on. | ✅ Base scaffold |
| **Template Studio for WinUI** | VSIX wizard scaffolding NavigationView shell + Settings + About + theming + DI. | Considered for the DI / structure it pre-wires. | ❌ Too opinionated: settings page, navigation shell, and theme switcher are explicit Non-Goals. Net work to *remove* them on day one. |
| **Hand-rolled `App` + `Core` split** | `dotnet new winui` + add a `Core` class library, move all non-XAML code into it (services, view-models, models, protocol code). | Matches the prior-art shape that worked at `C:\work\UpnpSpy`; isolates testable code from WinUI dependency. | ✅ Selected (built on top of `dotnet new winui`). |

### Selected Starter: `dotnet new winui` + two-project split

**Rationale for selection:**

- **Smallest scaffold compatible with the PRD.** Template Studio's pre-wired navigation shell, settings page, and theme switcher are explicit Non-Goals; pulling them out is work we don't need to do.
- **Matches the prior-art shape that demonstrably works.** UpnpSpy used App + Core; the addendum's "carry forward" list (registry/VM separation, dispatcher abstraction, bounded eager-fetch) all sit naturally on this split.
- **Testability.** `Core` as a `net10.0` class library (no `-windows` TFM) keeps protocol/registry/VM code unit-testable from xUnit without WinUI runtime requirements; only the App project carries the `-windows` TFM.
- **L&L narrative friendliness.** "Microsoft's template gave us this; we added one structural choice — App/Core split — for testability; now here's the architecture" is a clean two-step opener.

### Initialization Command

```powershell
dotnet new winui -n ohSpy.App -o src\ohSpy.App
dotnet new classlib -n ohSpy.Core -o src\ohSpy.Core --framework net10.0
dotnet new xunit -n ohSpy.Core.Tests -o tests\ohSpy.Core.Tests --framework net10.0
dotnet new sln -n ohSpy
dotnet sln add src\ohSpy.App\ohSpy.App.csproj src\ohSpy.Core\ohSpy.Core.csproj tests\ohSpy.Core.Tests\ohSpy.Core.Tests.csproj
dotnet add src\ohSpy.App\ohSpy.App.csproj reference src\ohSpy.Core\ohSpy.Core.csproj
dotnet add tests\ohSpy.Core.Tests\ohSpy.Core.Tests.csproj reference src\ohSpy.Core\ohSpy.Core.csproj
```

### Architectural Decisions Provided by Starter

**Language & Runtime:**

- C# 13 (the language version that ships with .NET 10).
- **.NET 10 LTS** (released 2025-11-11; supported until 2028-11-14). LTS, not STS — three-year support window comfortably covers ohSpy's expected lifetime as an internal tool.
- App project TFM: `net10.0-windows10.0.19041.0` (or whatever the template emits — pin to a specific Windows 10 SDK version for reproducibility).
- Core / Tests project TFM: `net10.0` (no `-windows`; keeps Core testable without WinUI runtime).

**Windows App SDK:**

- **Windows App SDK 2.1.3 (Stable)** — current Stable as of 2026-05-21; lifecycle support runs to 2027-04-29 under the 2.0 release line.
- WinUI 3 ships with the SDK; no separate WinUI package reference.

**MVVM:**

- `CommunityToolkit.Mvvm` (latest stable) — source-gen MVVM (`[ObservableProperty]`, `[RelayCommand]`). Lifted from the prior art's working stack.

**DI / Logging / Options:**

- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Logging` (with our custom diagnostic sinks plugged in — see §Diagnostics)
- `Microsoft.Extensions.Options` (configuration is in-memory / from code; no `appsettings.json` since FR explicitly forbids persistence — but `Options` is still useful for shared timeout / cap constants).

**Testing:**

- xUnit + Moq + FluentAssertions (Core project). Carried forward from prior art.

**Packaging:**

- MSIX (the `Package.appxmanifest` the template emits is the basis).
- Self-contained publish with Windows App Runtime bundled (per addendum carry-forward).
- Publish profiles: `win-x64` and `win-arm64` — both targets the audience uses.

**Code Organization:**

- `src/ohSpy.App/` — WinUI 3 App project: XAML (`MainWindow`, popup windows), code-behind, App startup / DI composition root, `Package.appxmanifest`.
- `src/ohSpy.Core/` — class library: models, view-models, services (Discovery, Registry, EagerDescriptionDispatcher, ControlClient, SubscriptionClient, EventCallbackHost), dispatcher abstraction, bounded-collection primitives, diagnostic sinks.
- `tests/ohSpy.Core.Tests/` — xUnit unit + integration tests on Core. (UI-level tests, if any, are a later decision; PRD does not mandate them.)

**Development Experience:**

- Hot reload via Visual Studio 2022/2026 for XAML — useful during UI-polish iteration.
- `dotnet build` / `dotnet test` from CLI for headless iteration with Claude.
- No CI configured by starter; CI is a later decision (see §Open Items).

**Note:** Project initialization using the command block above should be the first implementation story.

## Core Architectural Decisions

Working through 12 architectural decisions in sequence. Each is captured here with the chosen option, rationale, and concrete shape that downstream implementation agents need.

### Decision 1 — Threading Model & UI Dispatcher Contract

**Chosen:** Custom `IUiDispatcher` abstraction over WinUI 3's `Microsoft.UI.Dispatching.DispatcherQueue`.

**Shape (canonical interface, lives in `ohSpy.Core`):**

```csharp
public interface IUiDispatcher
{
    void Post(Action action);                    // Fire-and-forget marshal to UI thread.
    Task<T> PostAsync<T>(Func<T> readback);      // Round-trip: read a UI-owned value off-thread.
    bool IsOnUiThread { get; }                   // Cheap query; safe to call from any thread.
    void AssertOnUiThread();                     // Throws if called off-thread — Debug AND Release.
}
```

**Concrete impl (in `ohSpy.App`, registered as singleton):**

- `WinUiDispatcher` wraps `DispatcherQueue.GetForCurrentThread()`, captured once during App startup on the UI thread.
- `Post` → `_queue.TryEnqueue(...)`.
- `PostAsync` → `TaskCompletionSource` posted via `TryEnqueue`.
- `IsOnUiThread` → `_queue.HasThreadAccess`.
- `AssertOnUiThread()` → `throw new InvalidOperationException(...)` if `!IsOnUiThread`. **Throws in Release as well as Debug** — this is a coding-error invariant, not a debug aid.

**Test impl (in `ohSpy.Core.Tests`):**

- `InlineUiDispatcher` executes `Post(Action a)` synchronously as `a()`; `PostAsync` runs the readback inline. `IsOnUiThread` returns `true`. `AssertOnUiThread()` no-ops. Sufficient for all `Core` unit tests.

**Where it's injected:**

- Every `Core` service that mutates a VM (`DiscoveryService` → registry events, `EagerDescriptionDispatcher` results, `SsdpLog` inserts, `EventCallbackHost` → `SubscriptionPopupVM` appends) takes `IUiDispatcher` in its constructor.
- The shared `IUiDispatcher` singleton is app-wide. WinUI 3 default: all `Window`s launched on the same UI thread share one `DispatcherQueue`. No per-window dispatchers in v1.

**Rationale:**

- NFR-P3 ("no UI-thread blocking, ever") is the binding invariant; the dispatcher abstraction is the seam through which every cross-thread mutation passes.
- `DispatcherQueue` is the recommended on-platform answer for WinUI 3 (vs legacy `SynchronizationContext`).
- `AssertOnUiThread()` throwing in Release is deliberate: a thread-discipline violation is a bug, and bugs should surface as exceptions in production, not silent UI-repaint glitches downstream.
- Interface abstraction (not direct `DispatcherQueue` reference) keeps `Core` WinUI-free and unit-testable with a synchronous fake.

---

### Decision 2 — SSDP Socket Topology

**Chosen:** Two sockets per active adapter — multicast listener bound to `(adapter_ipv4, 1900)` with `ReuseAddress`, joined to `239.255.255.250` on that adapter; ephemeral search socket bound to `(adapter_ipv4, 0)`. Both feed a single bounded `Channel<SsdpDatagram>` (capacity 4096) consumed by `DiscoveryService`.

**Shape:**

```csharp
public sealed record SsdpDatagram(
    IPEndPoint Remote,
    byte[] Payload,
    DateTime ArrivalUtc,
    SsdpSource Source);

public enum SsdpSource { Multicast, SearchResponse }

public interface ISsdpTransport : IAsyncDisposable
{
    Task StartAsync(IPAddress adapterIPv4, CancellationToken ct);
    Task SendMSearchAsync(TimeSpan mx, CancellationToken ct);
    ChannelReader<SsdpDatagram> IncomingDatagrams { get; }
}
```

**Implementation contract (lives in `ohSpy.Core`):**

- **Multicast listener socket:**
  - `new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)`
  - `SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)` — mandatory; coexists with Windows `SSDPSRV`.
  - Bind to `IPEndPoint(adapterIPv4, 1900)`.
  - Join group: `SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse("239.255.255.250"), adapterIPv4))`.
  - Receive loop posts datagrams with `Source = Multicast`.
- **Ephemeral search socket:**
  - Same address family / Dgram / UDP.
  - Bind to `IPEndPoint(adapterIPv4, 0)` — OS chooses port.
  - Set `MulticastInterface` to `adapterIPv4` so M-SEARCH egress is on the chosen adapter.
  - Sends M-SEARCH (UDA 1.0 §1.2.2) with `ST: upnp:rootdevice` (FR-004, FR-022, FR-053).
  - Same socket receives unicast M-SEARCH responses; posts datagrams with `Source = SearchResponse`.
- **Channel:** `Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false })`.
  - Two writers (the two sockets), one reader (`DiscoveryService`).
  - `DropOldest` so a stalled consumer cannot back-pressure the receive loops.
  - Channel near-full (≥ 90%) and overflow (drop-oldest) both emit a `Warning` `DiagnosticEntry` (`Ssdp.Channel.NearFull`, `Ssdp.Channel.Overflow`).

**Adapter switch (FR-050):** `ISsdpTransport.DisposeAsync()` is part of the atomic rebind — it closes both sockets, leaves the multicast group cleanly, and disposes the channel. A fresh transport is then constructed on the newly-selected adapter.

**Test contract (patched 2026-06-02 by Amendment A22 — Windows multicast-only delivery):**

SSDP transport integration tests MUST deliver test datagrams via the multicast group (`239.255.255.250:1900`), NOT by unicast to `(adapter, 1900)`. On Windows, the built-in `SSDPSRV` service co-binds `*:1900` with `ReuseAddress`; the OS delivers a unicast datagram to only ONE of the reuse-bound sockets and may pick `SSDPSRV` instead of the transport-under-test. Multicast is fanned out to all group members, so the transport's joined listener reliably receives it. Receive-side assertions MUST include a unique `USN` marker per test + a read-until-match loop so live-network NOTIFYs from real devices on a real adapter do not pollute assertions. This rule is load-bearing for Story 2.4 (SSDP parser + chaos tests) and every subsequent SSDP receive test.

**Rationale:**

- Two sockets sidestep port-1900 contention with Windows `SSDPSRV`. `ReuseAddress` works most of the time on a single socket, but "most of the time" is exactly what NFR-R1 precludes.
- Adapter-specific multicast bind (rather than `0.0.0.0`) means the OS does interface filtering for us. FR-048 already constrains us to one adapter at a time; binding adapter-specifically aligns the socket layer with that constraint and simplifies atomic rebind.
- Single source-tagged channel keeps the parser and `DiscoveryService` ignorant of the dual-socket topology.
- Channel capacity 4096 carried from prior art; at ≥ 20 adv/s sustained (NFR target), the channel is a comfortable burst buffer — not a session backlog.

---

### Decision 3 — HTTP Client Strategy

**Chosen:** Typed `IUpnpHttpClient` facade over a single app-lifetime `HttpClient` singleton. Each method bakes its per-request timeout into a linked CTS internally. Caller passes only an external `CancellationToken`. Typed exception hierarchy. Plus the amendments surfaced in party-mode review (headers-and-body-cancellation gap closed; size caps; `SocketsHttpHandler` keep-alive ping for truly-hung sockets).

**Underlying `HttpClient` configuration (constructed once at app startup):**

```csharp
var handler = new SocketsHttpHandler
{
    UseProxy = false,                                              // LAN-only; no corporate proxies in the loop.
    AllowAutoRedirect = false,                                     // UPnP doesn't legitimately redirect; treat as malformed.
    ConnectTimeout = TimeSpan.FromSeconds(5),                      // Dead-IP fast-fail, separate from per-op budget.
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),            // Recycle on adapter switch.
    KeepAlivePingDelay = TimeSpan.FromSeconds(15),                 // Active OS-level liveness probe.
    KeepAlivePingTimeout = TimeSpan.FromSeconds(5),                // — answer to "network truly hung, no RST".
    MaxResponseHeadersLength = 16,                                 // 16 KB headers ceiling.
};
var http = new HttpClient(handler, disposeHandler: true)
{
    Timeout = Timeout.InfiniteTimeSpan,                            // Per-op CTS is the SOLE timeout source.
    DefaultRequestVersion = HttpVersion.Version11,
};
```

**Facade contract (`ohSpy.Core/Http/IUpnpHttpClient.cs`):**

> *Patched 2026-06-02 by [Amendment A10 — Story 1.3 implementation reality](#amendment-a10--fetchdevicedescriptionasync--fetchscpdasync-return-type-symmetry-decision-3-refinement): both Fetch methods now return `Task<byte[]>` (raw bytes; parsing is Story 1.4's concern). The D5 revision that moved `FetchScpdAsync` from `Task<ScpdDocument>` to `Task<byte[]>` should have been mirrored here for `FetchDeviceDescriptionAsync`. Consumers compose `IDeviceDescriptionParser` / `IScpdParser` over the raw bytes.*

```csharp
public interface IUpnpHttpClient
{
    Task<byte[]>            FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct);
    Task<byte[]>            FetchScpdAsync(Uri scpdUrl, CancellationToken ct);
    Task<SoapResponse>      InvokeActionAsync(SoapRequest request, CancellationToken ct);
    Task<SubscribeResponse> SubscribeAsync(Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct);
    Task<SubscribeResponse> RenewSubscriptionAsync(Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct);
    Task                    UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct);
}
```

**Invariant inside every facade method (the architectural contract):**

```csharp
public async Task<T> SomethingAsync(..., CancellationToken external)
{
    using var timeoutCts = new CancellationTokenSource(_opts.SomeTimeout);
    using var linked     = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);

    using var req  = BuildRequest(...);
    using var resp = await _http.SendAsync(req,
        HttpCompletionOption.ResponseHeadersRead,   // mandatory: headers AND body covered by linked CTS
        linked.Token).ConfigureAwait(false);

    if (resp.Content.Headers.ContentLength > _opts.MaxResponseBytes)
        throw new UpnpProtocolException(...);       // size cap before reading body

    var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false); // token threaded through

    return Parse<T>(body);
}
catch (OperationCanceledException) when (external.IsCancellationRequested)
{
    throw;                                          // external cancel: silent, caller's problem
}
catch (OperationCanceledException)
{
    _diag.Warning("Http.Timeout", new { url, budget = _opts.SomeTimeout, elapsed });
    throw new UpnpTimeoutException(...);
}
catch (HttpRequestException ex)
{
    _diag.Warning("Http.Transport", new { url, error = ex.Message });
    throw new UpnpTransportException(...);
}
```

**Typed exception hierarchy:**

- `UpnpException` (never thrown directly)
  - `UpnpTimeoutException` — per-op CTS fired (not external cancellation)
  - `UpnpTransportException` — socket / DNS / TLS / connection reset (HttpRequestException family)
  - `UpnpProtocolException` — malformed XML, oversized body, bad status code on description/SCPD
  - `UpnpFaultException` — SOAP 500 + `<s:Fault>`; carries faultcode, errorCode, errorDescription

**Per-method timeout placeholders (pinned definitively in Decision 11):**

- `FetchDeviceDescriptionAsync` — 5 s
- `FetchScpdAsync` — 5 s default, configurable up to ~15 s for known-large-SCPD vendors (decided in Decision 11 after empirical capture against Linn DS + IGD)
- `InvokeActionAsync` — 10 s
- `SubscribeAsync` / `RenewSubscriptionAsync` / `UnsubscribeAsync` — 5 s

**Per-method body size cap (`MaxResponseBytes`):**

- Description: 1 MB.
- SCPD: 2 MB (some IGD vendors legitimately ship 500 KB+).
- SOAP response: 1 MB.
- GENA SUBSCRIBE/UNSUBSCRIBE response: 64 KB (header-only response in practice).

Bodies exceeding the cap → `UpnpProtocolException`, response disposed, `Warning` diagnostic (`Http.OversizeBody`).

**Cancellation semantics:**

- External token cancelled (adapter switch, device byebye, popup close) → `OperationCanceledException` propagates; no diagnostic emitted (caller-initiated, expected).
- Per-op timeout fired → `UpnpTimeoutException` thrown; `Warning` diagnostic emitted with URL, budget, elapsed.

**Test contract:**

- **Above the facade:** consumer code (`DiscoveryService`, `EagerDescriptionDispatcher`, `SubscriptionClient`, popup VMs) takes `IUpnpHttpClient` via DI. Tests mock the interface with Moq.
- **Below the facade:** `UpnpHttpClient` implementation tested against a hand-rolled `TestHttpMessageHandler : HttpMessageHandler` (Moq's `Protected()` is brittle for this; ~40 lines reusable handler is cleaner).
- **End-to-end fixture:** in-process Kestrel `FakeUpnpDevice` on `127.0.0.1:0` with failure-injection modes — `HangBeforeHeaders`, `HangAfter200Ok`, `SlowDripBody`, `GiantScpd`, `ChunkedThenAbort`, `FaultResponse`, `WrongContentLength`. The `HangAfter200Ok` fixture is the regression test for the prior tool's eager-fetch-queue stall.

**Acceptance criteria (citable in stories):**

- **AC-3.1** `UpnpHttpClient` never reads `HttpClient.Timeout`; all timeouts via linked CTS. Test: handler delays 200 s, op times out at configured budget ± 100 ms.
- **AC-3.2** `SUBSCRIBE` / `UNSUBSCRIBE` reach handler with exact verb string. Test: `TestHttpMessageHandler` asserts `request.Method.Method == "SUBSCRIBE"`.
- **AC-3.3** SOAP 500 with `<s:Fault>` → `UpnpFaultException` carrying `faultcode` / `<UPnPError><errorCode/>`. Test: canned XML body.
- **AC-3.4** Response body exceeding per-method cap → `UpnpProtocolException`, connection disposed. Test: handler returns infinite stream.
- **AC-3.5** Headers received then body hang → `UpnpTimeoutException` at op budget. Test: handler returns 200 + stream that never completes.
- **AC-3.6** Caller's `CancellationToken` cancellation → `OperationCanceledException`, not `UpnpTimeoutException`. Test: caller cancels before op budget.

**Rationale:**

- The "slow responses" failure of the prior tool was *forgetting* per-request timeouts. The typed-facade design makes that mistake structurally impossible: every UPnP call goes through a method whose implementation bakes the timeout in.
- One shared `HttpClient` underneath is correct — LAN-local, no DNS rotation, no handler recycling needed. `IHttpClientFactory` would add `Microsoft.Extensions.Http` without commensurate benefit.
- `HttpCompletionOption.ResponseHeadersRead` + token-threaded `ReadAsStringAsync(ct)` is the critical mechanic that closes the headers-vs-body-read gap. **This is the architectural difference between a typed-but-leaky facade and one that actually delivers NFR-P2.** Surfaced in party-mode review as the likely actual prior-tool bug, independent of dispatcher discipline.
- `SocketsHttpHandler.KeepAlivePingDelay` / `KeepAlivePingTimeout` close the "truly-hung TCP with no RST" gap that even per-call CTS can't cover (the OS keeps the socket alive for ~2h by default).
- Typed exceptions over `Result<T>`: `OperationCanceledException` is already exception-flavoured; SOAP fault is genuinely exceptional from the caller's perspective; xUnit `Assert.ThrowsAsync<UpnpTimeoutException>` is more legible than `Assert.True(result.IsTimeout)`.

**Open follow-ups (carried forward, not blocking):**

- **IPv6 / link-local zone IDs:** out of v1 scope (Non-Goal: no IPv6). If IPv6 surfaces later, the facade is the right seam to add zone-aware URL handling.
- **mDNS / `.local` hostname resolution outside CTS budget:** UPnP descriptions sometimes embed `.local` URLs. If we see this in practice, add an IP-only enforcement layer or a pre-resolution step with its own budget. Logged as Open Item.
- **HTTP/1.0 devices:** allow per-call override of `RequestVersion` if a known-vintage device requires it. Not implementing speculatively.

---

### Decision 4 — GENA Callback Host Hardening Contract

**Chosen:** Pragmatic parser — strict on framing (size limits, line endings, Content-Length matching), lenient on header quirks (case-insensitive, ignore extras). Implemented over `TcpListener` per FR-049 (no `HttpListener`, no URL ACL, no Admin).

**Threat model in scope:** broken devices, slowloris starvation, body-bombs, oversized headers, connection floods. Out of scope: TLS attacks (UPnP is plaintext), authenticated attackers (no auth surface), pathological adversarial fuzz (NFR scope explicitly excludes).

**Connection acceptance:**

- `TcpListener.Start(backlog: 16)`.
- Per-instance max concurrent connections: **8**. Excess accepted-then-immediately-closed with `Warning` diagnostic (`Gena.Callback.ConnectionFlood`).
- Per-connection lifetime: single request, no keep-alive. `Connection: close` in every response.

**Per-request budgets (enforced locally, separate from NFR-P2 outbound budgets):**

- Connect → headers-complete: **5 s** (slowloris defense).
- Headers-complete → body-complete: **5 s** (separate; total per-request worst case 10 s).
- Max header block size: **16 KB**.
- Max body size: **1 MB**.
- Max number of headers: **64**.

**Framing rules (strict — violation → 400 + close + Warning):**

- Request line ends with `CRLF`; bare `LF` accepted, bare `CR` rejected.
- Request line: `METHOD SP request-target SP HTTP-version CRLF`; exactly two SP; method is uppercase ASCII tokens.
- Header lines per RFC 7230 §3.2.6; case-insensitive on read, canonical lowercase internally.
- Empty `CRLF` terminates header block.
- `Content-Length` MUST be present and parseable as non-negative integer ≤ 1 MB. Absence → `411 Length Required`. Body shorter than declared → body-complete timeout → close. Body longer → read exactly `Content-Length`, ignore rest.
- `Transfer-Encoding: chunked` rejected with `400` (defer chunked support until a real vendor needs it).
- Whitespace-folded headers (obsolete RFC 7230 §3.2.4): rejected with `400`.

**Header tolerance (lenient):**

- Duplicate headers: last-wins for known headers (`NT`, `NTS`, `SID`, `SEQ`); `CONTENT-LENGTH` duplicates → `400`.
- Unknown headers ignored, counted against 64-header cap.

**Response shape:**

| Outcome | Response | Diagnostic |
|---|---|---|
| Valid NOTIFY parsed & dispatched | `200 OK` + `Content-Length: 0` + `Connection: close` | `Information` `Gena.Notify.Received` (verbose only) |
| Malformed framing | `400 Bad Request` + `Connection: close` | `Warning` `Gena.Callback.MalformedRequest` |
| Missing `Content-Length` | `411 Length Required` + `Connection: close` | `Warning` `Gena.Callback.NoContentLength` |
| Oversized headers or body | `413 Content Too Large` + `Connection: close` | `Warning` `Gena.Callback.Oversize` |
| Internal dispatch error | `500 Internal Server Error` + `Connection: close` | `Warning` with stack |
| Subscription unknown / cancelled | `200 OK` (idempotent ack) | — |

**Parser surface (the seam to upstream consumers):**

```csharp
public interface IEventCallbackHost : IAsyncDisposable
{
    Task StartAsync(IPAddress adapterIPv4, CancellationToken ct);
    Uri CallbackBaseUrl { get; }     // e.g. http://192.168.1.42:54321/ — announced in SUBSCRIBE CALLBACK header
    event Func<NotifyRequest, Task> NotifyReceived;
}

public sealed record NotifyRequest(
    string Sid,
    long Seq,
    string PathAndQuery,
    byte[] Body,
    DateTime ReceivedUtc);
```

- The host does NOT parse `<e:propertyset>` XML — that's the subscription popup VM's job (FR-104 non-serial NOTIFY processing above the host).
- `NotifyReceived` handlers are awaited; the host tracks in-flight tasks to drain on shutdown.

**Per-connection stream wrapper:**

- `TimeoutStream` adapter wraps the raw `NetworkStream`; throws on any read whose idle time exceeds the active budget (headers or body, depending on parser phase). Cleaner than raw CTS-around-each-read; one place to enforce timeout discipline.

**Cascading implications:**

- Host runs on a dedicated background task spawned during App startup; `StartAsync` returns once the listener is bound and accepting.
- Adapter switch (FR-050) tears the host down and reconstructs on the new adapter; open connections are drained or force-closed within a 2 s budget that matches the FR-050 atomic-rebind expectations.
- Connection cap (8) is independent of the eager-fetch concurrency cap (also 8 — coincidental). Both are bounded but distinct.

**Test contract:**

- Hand-rolled `FakeGenaClient` that opens raw `TcpClient` connections and sends/withholds bytes — drives every malformed-input AC.
- `SlowlorisTest`: opens 8 connections, trickles 1 byte every 4 s; asserts all 8 hit the 5 s headers timeout, all close cleanly, 9th connection opens immediately after slots free.
- `FloodTest`: opens 50 connections in a tight loop; asserts 8 are served, 42 are accepted-then-immediately-closed with `ConnectionFlood` diagnostic, no thread/socket leak.

**Acceptance criteria (citable in stories):**

- **AC-4.1** Headers exceeding 16 KB → `413` + connection closed + `Warning` diagnostic.
- **AC-4.2** Body exceeding 1 MB → `413` + connection closed + `Warning`.
- **AC-4.3** Headers stalled for > 5 s → connection closed + `Warning` (`Gena.Callback.HeadersTimeout`).
- **AC-4.4** Body stalled for > 5 s after headers complete → connection closed + `Warning` (`Gena.Callback.BodyTimeout`).
- **AC-4.5** Missing `Content-Length` → `411` + `Warning`.
- **AC-4.6** `Transfer-Encoding: chunked` → `400` + `Warning`.
- **AC-4.7** 9th concurrent connection accepted then immediately closed with `Warning` (`Gena.Callback.ConnectionFlood`); no other behavioural effect.
- **AC-4.8** Valid `NOTIFY` → `200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n` returned to device; `NotifyReceived` event raised with parsed SID / SEQ / body.
- **AC-4.9** Adapter switch tears down listener + drains in-flight connections within 2 s.

**Rationale:**

- Strict framing closes the actual threat surface (slowloris, body-bombs, oversized headers). Lenient headers absorb the actual vendor noise (case quirks, ordering, extras).
- Connection cap 8 + per-request 5+5 s budget bounds worst-case host occupancy at 80 connection-seconds per 10 s window. Comfortable for 5 simultaneous subscription popups (FR-036) at normal NOTIFY rates; resistant to bursty floods.
- 1 MB body cap is well above legitimate GENA NOTIFY payload sizes (typically a few KB; tens of KB for verbose evented services) and well below memory-pressure thresholds.
- No keep-alive simplifies state — GENA NOTIFY is not pipelined; a new TCP handshake per NOTIFY costs sub-millisecond on the LAN.
- The host's contract ends at framing+routing. XML parsing happens above, in the subscription popup VM, where the FR-104 non-serial processing discipline lives.

**Open follow-ups (deferred, not blocking):**

- **Chunked-encoding NOTIFY:** add if a real vendor ships it.
- **HTTP/1.0 NOTIFY:** strict rejection in v1; tolerate later if discovered.
- **TLS callback endpoints (HTTPS NOTIFY):** out of scope (UPnP is plaintext by spec).

---

### Decision 5 — SCPD Parsing Strategy

**Chosen:** `IAsyncEnumerable<ScpdAction>`-based incremental streaming via `XmlReader.ReadAsync`, with cooperative `Task.Yield()` between actions. State variable table parsed separately, lazily, in a second pass over the same buffer.

**Note — Decision 3 revision:** `IUpnpHttpClient.FetchScpdAsync` returns `Task<byte[]>` (raw SCPD body, 2 MB cap), not a parsed `ScpdDocument`. This decouples network fetch (I/O-bound, timeout-disciplined) from XML parse (CPU-bound, yield-disciplined). Decision 3's signature is hereby updated.

**Parser contract (`ohSpy.Core/Scpd/IScpdParser.cs`):**

```csharp
public interface IScpdParser
{
    IAsyncEnumerable<ScpdAction> StreamActionsAsync(Stream xml, CancellationToken ct);
    Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct);
}

public sealed record ScpdAction(
    string Name,
    IReadOnlyList<ScpdArgument> Inputs,
    IReadOnlyList<ScpdArgument> Outputs);

public sealed record ScpdArgument(
    string Name,
    string RelatedStateVariable,
    ScpdDirection Direction);   // In | Out

public sealed record ScpdStateTable(
    IReadOnlyDictionary<string, ScpdStateVariable> ByName);

public sealed record ScpdStateVariable(
    string Name,
    string DataType,
    string? DefaultValue,
    IReadOnlyList<string>? AllowedValueList,
    ScpdAllowedValueRange? AllowedValueRange);

public sealed record ScpdAllowedValueRange(double Minimum, double Maximum, double? Step);
```

**Two methods, two timelines:**

- `StreamActionsAsync` — consumed during service-node expansion (FR-012). Actions appear in the tree one by one as the document is parsed. Yields `Task.Yield()` between each emitted action so the UI thread services other work.
- `ReadStateTableAsync` — consumed lazily on invocation-popup open (FR-102, FR-103). Returns the whole table; not streamed (consumer pattern is "look up one state variable by name").

**Consumer pattern in the service-expansion VM:**

```csharp
var bytes = await _http.FetchScpdAsync(scpdUrl, ct);
await foreach (var action in _parser.StreamActionsAsync(new MemoryStream(bytes), ct))
{
    _dispatcher.Post(() => _serviceNode.Children.Add(new ActionNodeVm(action)));
}
// State table fetched on demand the first time an invocation popup opens for this service:
var stateTable = await _parser.ReadStateTableAsync(new MemoryStream(bytes), ct);
_serviceNode.StateTable = stateTable;
```

**XmlReader settings (mandatory on every UPnP XML parse — applies equally to description-XML parser):**

```csharp
var settings = new XmlReaderSettings
{
    Async = true,
    DtdProcessing = DtdProcessing.Prohibit,   // XXE defense
    XmlResolver = null,                       // no external entity resolution
    IgnoreComments = true,
    IgnoreWhitespace = true,
    MaxCharactersInDocument = 4_000_000,      // 4M chars ≈ 2 MB body cap from Decision 3
};
```

**Yielding discipline:**

- `await Task.Yield()` after each completed `<action>` element gives the dispatcher a chance to process input/render between rows.
- Empirically: yielding every action keeps frame timing under 16 ms on a 200-action document. If profiling reveals over-yielding hurts throughput, batch to "yield every 4 actions" — tuning knob, defer.

**Error handling:**

- Malformed XML at action N: `UpnpProtocolException` thrown from the `IAsyncEnumerable` at the next iteration. Actions 0..N-1 already appended to the tree remain (they were valid up to the failure point). Service node shows FR-013 inline-error placeholder.
- XXE attempt: `XmlException` from `XmlReader` due to `DtdProcessing.Prohibit`; wrapped to `UpnpProtocolException`; `Warning` diagnostic.
- Cancellation mid-stream: `OperationCanceledException` propagates from `XmlReader.ReadAsync` via the linked CT; `XmlReader` disposed by `using` in parser implementation.

**Cascading implications:**

- `IScpdParser` is a singleton (stateless across documents). Registered once in DI.
- `IDeviceDescriptionParser` is a parallel type with the same `XmlReaderSettings` discipline but a different schema (parses device description XML, populates `DeviceDescription`). No shared internals — different schema, different output shape.
- The action-stream path is independent of the state-table path; tree expansion (FR-012) latency is decoupled from state-table availability.
- Re-parsing on each expansion (vs caching) is acceptable at SCPD sizes (~50 ms even for large documents). If expand-collapse-expand performance suffers, cache `ScpdStateTable` per service; the `byte[]` is already cheap to retain.

**Test contract:**

- Canned SCPD fixtures: small (5-action Linn DS), medium (30-action third-party), large (200-action synthetic IGD shape), pathological (malformed mid-document, empty `<actionList>`, missing required fields, XXE attempt, deeply-nested noise).
- Streaming assertion: `await foreach` over the 100-action fixture yields between actions; total parse time ≤ 2 s; no individual yield > 16 ms.
- Cancellation test: cancel CT mid-document → enumeration throws `OperationCanceledException` at the next yield; no leaked `XmlReader` (verify via `using` discipline).
- XXE test: doctype-with-entity-ref fixture → `UpnpProtocolException`; no file system access (assert via filesystem-mock or platform-level check).

**Acceptance criteria:**

- **AC-5.1** SCPD with 200 actions streams actions one-by-one; each `await foreach` iteration yields control before the next; total parse ≤ 2 s; no UI-thread stall > 16 ms (FR-100, Perf Budget §6 cold-large-SCPD).
- **AC-5.2** Malformed XML at action N → `UpnpProtocolException` after yielding actions 0..N-1; service node shows inline error placeholder (FR-013).
- **AC-5.3** XXE attempt → `UpnpProtocolException`, no file read, no DTD processing.
- **AC-5.4** Cancellation during stream → `OperationCanceledException`; `XmlReader` disposed; no resource leak.
- **AC-5.5** State variable table reflects every `<stateVariable>` with `<allowedValueList>` / `<allowedValueRange>` / `<defaultValue>` parsed correctly; constraints surface unchanged to FR-102 / FR-103 invocation popup.

**Rationale:**

- `IAsyncEnumerable<ScpdAction>` is the .NET idiom for "values produced over time" and matches FR-100 exactly. The consumer pattern (`await foreach` + `_dispatcher.Post`) is small and obvious — downstream agents implementing service-node expansion don't need to re-derive the streaming pattern.
- Two-method split reflects the two consumer timelines (action list during expansion, state table on invocation-popup open). Forcing both into one return value defeats the streaming UX of FR-100.
- Decoupling `FetchScpdAsync` (returns bytes) from parsing matches their actual nature: fetch is I/O with NFR-P2 timeout discipline; parse is CPU with FR-100 yield discipline. Mixing them gives neither a clean home.
- XXE defaults non-negotiable: devices on a LAN aren't trusted to ship safe XML, even from "well-known" vendors.

**Open follow-ups:**

- **Yield batching (1 vs 4 vs N actions per yield):** empirical, post-profiling.
- **Cached `ScpdStateTable` per service:** if expand-collapse-expand reveals re-parse overhead.

---

### Decision 6 — Identity-Tracked Observable Collection Primitives

**Chosen:** Two custom collection primitives in `ohSpy.Core/Collections/`:

1. `BoundedObservableCollection<T>` — newest-first, FIFO tail eviction. Used by SSDP log, subscription event list, diagnostic ring.
2. `IdentityKeyedSortedCollection<TIdentity, TItem>` — sorted by computed key, identity-stable across re-sort via `Move(old, new)` notifications. Used by the device tree's top-level rows.

Both consumed by VMs in `Core`, bound to virtualised WinUI controls in `App`. Neither is thread-safe — they are UI-thread-owned; cross-thread mutations marshal through `IUiDispatcher`.

**`BoundedObservableCollection<T>` contract:**

```csharp
public sealed class BoundedObservableCollection<T> : IReadOnlyList<T>, INotifyCollectionChanged
{
    public BoundedObservableCollection(int capacity);
    public int Capacity { get; }
    public int Count { get; }
    public T this[int index] { get; }   // index 0 = newest, Count-1 = oldest

    public void PrependNewest(T item);  // O(1). Adds at 0; if at capacity, evicts tail.
                                        // Emits Add(0). If eviction occurred, ALSO emits Remove(Count).
                                        // No Reset.

    public void Clear();                // Single Reset notification. Used on adapter switch.

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
}
```

Implementation: ring buffer `T[]` of size `Capacity`. `head` index advances on each `PrependNewest`. Indexed access `this[index]` translates index → ring offset. `PrependNewest` is O(1) — no array shift, no list copy, no `Reset` notification.

**`IdentityKeyedSortedCollection<TIdentity, TItem>` contract:**

```csharp
public sealed class IdentityKeyedSortedCollection<TIdentity, TItem>
    : IReadOnlyList<TItem>, INotifyCollectionChanged
    where TIdentity : notnull
{
    public IdentityKeyedSortedCollection(
        Func<TItem, TIdentity> identitySelector,
        IComparer<TItem> sortComparer);

    public int Count { get; }
    public TItem this[int index] { get; }
    public bool TryGetItem(TIdentity id, out TItem item);

    public void Add(TItem item);                // Insert in sorted position; emits Add(index).
    public bool Remove(TIdentity id);           // Remove by identity; emits Remove(oldIndex).

    public void Update(TItem updatedItem);      // If sort key unchanged: emits nothing.
                                                // If sort key changed: emits Move(old, new).
                                                // Identity preserved across move — selection/expansion
                                                // state in bound TreeView MUST survive.

    public void Clear();                        // Single Reset notification.

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
}
```

Implementation: backing store is `List<TItem>` + `Dictionary<TIdentity, int>` for O(1) lookup-by-identity. Insertions use binary search on the `List` to find sort position; identity-index updated for displaced items. `Update` finds current index via identity, recomputes sort position; if unchanged → no notification; if changed → `List.RemoveAt(old)` + `List.Insert(new, item)` + `Move(old, new)` notification.

**Critical: `Move(old, new)` for sort-key-change rather than `Remove`+`Add`.**

WinUI `TreeView` reacts to `Move` by preserving node identity in the visual tree — selection state, expansion state, scroll position — exactly as FR-054 requires. `Remove`+`Add` is two operations the framework cannot unify; the expanded children collapse, scroll resets, selection is lost. The architecture explicitly dictates `Move`; downstream agents will not derive this from FR-054 alone.

**Where bound:**

| VM property | Type | Used by |
|---|---|---|
| `SsdpLogViewModel.Entries` | `BoundedObservableCollection<SsdpLogEntry>(10_000)` | SSDP log pane (FR-016, FR-101) |
| `SubscriptionPopupViewModel.Events` | `BoundedObservableCollection<EventNotification>(5_000)` | Subscription popup event list (FR-033) |
| `DiagnosticsViewModel.Entries` | `BoundedObservableCollection<DiagnosticEntry>(5_000)` | Diagnostic viewer (FR-041) |
| `DeviceTreeViewModel.Devices` | `IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>` | Device tree top-level rows (FR-005, FR-008, FR-054) |

The device-tree comparator is case-insensitive on `FriendlyName` (or `uuid:<uuid>` fallback per FR-010) with ordinal UUID tiebreak per FR-054.

**Test contract:**

- `BoundedObservableCollection`: capacity enforcement; FIFO eviction order; exact `Add`/`Remove` indices in notifications; no `Reset` on incremental change; index correctness across ring wrap; thread-confinement (any mutation off-thread throws when used with `InlineUiDispatcher` in the off-thread direction — tested via deliberately off-thread call).
- `IdentityKeyedSortedCollection`: sort order maintained across add/update/remove; `Move` emitted (not `Remove`+`Add`) on sort-key change; identity preserved through migration; `TryGetItem` returns correct item across migrations; `Clear` emits single `Reset`.
- Integration test: `IdentityKeyedSortedCollection` bound to a `TreeView` survives a sort-key-induced migration with expansion + selection state intact (the FR-054 regression test).

**Acceptance criteria:**

- **AC-6.1** `BoundedObservableCollection.PrependNewest` at capacity emits exactly two notifications — `Add(index=0)` and `Remove(index=Count)` — and never `Reset` (NFR-P5).
- **AC-6.2** 100,000 sequential `PrependNewest` calls on a 10,000-capacity collection complete in O(N) total wall time (constant per-call); zero `Reset` notifications.
- **AC-6.3** `IdentityKeyedSortedCollection.Update` with unchanged sort key emits no notification.
- **AC-6.4** `IdentityKeyedSortedCollection.Update` with changed sort key emits exactly one `Move(old, new)` notification (FR-054 stable-identity invariant).
- **AC-6.5** Bound to a `TreeView`: row migration via `Move` preserves selection, expansion state, and scroll position (integration test).
- **AC-6.6** Both primitives' `Clear()` emits a single `Reset`; used by adapter switch (FR-050).

**Rationale:**

- The prior tool's "unnecessary full-screen repaints" traced to `ObservableCollection.Insert(0)` (O(N) per insert) + non-virtualised list + occasional `Reset` notifications. The primitive replaces all three vectors: O(1) prepend via ring buffer; exact-index notifications; no `Reset` on incremental change. Virtualisation in the bound control (Implementation Patterns step) finishes the fix.
- Two primitives rather than one is right specialisation: newest-first-bounded and sorted-identity-stable have structurally different APIs and unifying them produces a sloppy union surface.
- `Move(old, new)` for sort-key change is the *only* mechanism that preserves `TreeView` visual state. Architecture has to dictate it because it's a non-obvious framework-coupling decision that downstream FR-054 work depends on.
- Both types in `Core` (not `App`): unit-testable without any WinUI runtime; `INotifyCollectionChanged` lives in `System.Collections.Specialized` which is available in any `net10.0` project.

**Open follow-ups:**

- **Subscription "Latest property values" summary (FR-033 anchored top section):** a separate observable map (`Dictionary<string, string>` with property-changed notifications). Different semantics — overwrite-in-place, not append. Special-cased in the subscription popup VM; no general primitive needed.
- **Burst-load profile:** under the chatty-network ≥ 20 adv/s burst, verify `BoundedObservableCollection` + virtualised host stay under the 16 ms frame budget. If degraded, add a coalescing layer in the VM that batches N prepends into one dispatcher tick.

---

### Decision 7 — Cancellation Token Flow

**Chosen:** Three-level CTS hierarchy that mirrors the lifetime graph: **app → adapter → device → popup**, with per-operation linked CTS at the leaf. Cancellation propagates downward via `CancellationTokenSource.CreateLinkedTokenSource`; cleanup operations that must run during cancellation derive their token from the level *above* the cancelled scope.

**Ownership map:**

| Level | Owner | Lifetime ends on | Cancels |
|---|---|---|---|
| App | `App` startup composition | App shutdown | Everything |
| Adapter | `AdapterScope` (one active at a time) | Adapter switch (FR-050) or app shutdown | SSDP transport, callback host, all registry entries, all popups |
| Device | `RegistryEntry` (per UUID) | Byebye (FR-008), rescan prune (FR-023), adapter switch | In-flight description + SCPD fetches for this device; informs popups for this device's services via the registry's `DeviceRemoved` event (FR-037 trigger) |
| Popup | Each invocation popup, subscription popup, properties window | Popup close (user), device gone (cascaded), adapter switch | In-flight invocation request; subscription work; per-popup state |
| Per-operation | Each `IUpnpHttpClient.*Async` call site | Operation completes or per-call timeout fires | The single operation |

```
App
 ├── _appCts                                   // disposed at app shutdown
 │     └── token: appToken
 │
 └── AdapterScope (one active)
       ├── _adapterCts = linked(appToken)      // disposed on adapter switch
       │     └── token: adapterToken
       │
       ├── SsdpTransport(adapterToken)
       ├── EventCallbackHost(adapterToken)
       │
       └── DeviceRegistry
             └── RegistryEntry (per UUID)
                   ├── _deviceCts = linked(adapterToken)   // disposed on byebye/prune
                   │     └── token: deviceToken
                   │
                   ├── EagerDescriptionFetch uses deviceToken
                   │
                   └── Open popups for this UUID (back-refs):
                         └── Popup
                               ├── _popupCts = linked(deviceToken)   // disposed on close
                               │     └── token: popupToken
                               │
                               └── Invocation / Subscription tasks use popupToken
```

**Per-operation linked CTS at the leaf** — already established in Decision 3; each `IUpnpHttpClient.*Async` method internally does:

```csharp
using var timeoutCts = new CancellationTokenSource(_opts.SomeTimeout);
using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, timeoutCts.Token);
```

where `externalToken` is whichever scope token applies (`popupToken` for invocation, `deviceToken` for description/SCPD fetches, etc.).

**Cancellation propagation rules:**

- **Adapter switch fires `_adapterCts.Cancel()`** → SSDP transport and callback host tear down; every `RegistryEntry.deviceToken` cancels (linked); every fetch observes; every `popupToken` cancels (linked); popups transition to FR-037 "device gone" state.
- **Device byebye fires `RegistryEntry._deviceCts.Cancel()`** → in-flight description/SCPD fetches for this device throw; popups for this device transition to FR-037 state (notified via registry `DeviceRemoved` event). Other devices unaffected.
- **Popup close fires `_popupCts.Cancel()`** → in-flight invocation throws; subscription popup runs UNSUBSCRIBE with a **separate token derived from adapter-level** (see invariant below).

**Architectural invariant — cleanup uses the level-above token:**

When a scope cancels but still has a cleanup operation that must run (UNSUBSCRIBE on popup close, transport teardown on adapter switch), the cleanup must NOT use the cancelled scope's token — it would cancel immediately. The cleanup derives its token from the **level above** the cancelled scope.

```csharp
// Inside SubscriptionPopupViewModel.OnClosing():
_popupCts.Cancel();   // cancels in-flight subscribe/renew
try
{
    using var unsubCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        _adapterToken,                  // NOT _popupCts.Token — that's already cancelled
        unsubCts.Token);
    await _http.UnsubscribeAsync(_eventSubUrl, _sid, linked.Token);
}
catch (Exception ex)
{
    _diag.Warning("Gena.Unsubscribe.Failed", new { sid = _sid, error = ex.Message });
    // FR-035 / FR-038: unsubscribe failure does not block popup close
}
finally
{
    _popupCts.Dispose();
}
```

This invariant is non-obvious; downstream agents will reach for `_popupCts.Token` by default and break UNSUBSCRIBE on close. The architecture pins it explicitly.

**Adapter-switch atomic sequence (FR-050), 2 s total budget:**

```
1. _adapterCts.Cancel()                                  // signal cascades
2. await SsdpTransport.DisposeAsync()                    // sockets + channel torn down
3. await EventCallbackHost.DisposeAsync()                // TcpListener stopped; in-flight drained
4. Cancel + dispose every RegistryEntry._deviceCts       // (already cancelled via linkage; this dispose-only)
5. Drain in-flight fetch tasks (await with budget 2 s)
6. Clear DeviceRegistry (raises DeviceRemoved per UUID — VM drops rows)
7. Dispose _adapterCts
8. Construct new AdapterScope on new adapter IPv4
```

If drain exceeds 2 s, force-tear-down and emit `Warning` diagnostic — adapter switch must complete within budget; we don't block UX on hung tasks.

**Registry-events vs cancellation tokens (two mechanisms, one outcome):**

- `DeviceRegistry` raises `DeviceAdded` / `DeviceUpdated` / `DeviceRemoved` events. These are *notifications*, dispatched via `IUiDispatcher`.
- Cancellation tokens are the *propagation mechanism* for in-flight work cancellation.
- The two compose to deliver FR-037: a popup learns "your device is gone" via the `DeviceRemoved(uuid)` event (notification → UI state change) *and* its `_popupCts` is cancelled via the linked `_deviceCts` (in-flight work aborts).

**Naming convention for `CancellationToken` parameters:**

When ambiguity is possible, parameters are named to indicate which scope they belong to:

```csharp
Task FetchDescriptionAsync(Uri url, CancellationToken deviceToken);
Task InvokeActionAsync(SoapRequest req, CancellationToken popupToken);
Task SubscribeAsync(Uri url, ..., CancellationToken popupToken);
```

This is a documentation convention, not a type-system thing — but pinning it here saves rediscovery in every story.

**Test contract:**

- **Adapter-switch cancellation drill:** 10 devices, 10 in-flight description fetches, fire adapter switch — all 10 throw `OperationCanceledException` within 100 ms; no fetch posts to a disposed VM.
- **Per-device byebye drill:** 5 devices, 5 in-flight fetches, fire byebye on device 3 — device 3's fetch throws; the other 4 are unaffected and complete normally.
- **Popup-close-with-pending-invocation:** open invocation popup, send invocation, close before response — invocation throws `OperationCanceledException`; `_popupCts` is disposed; no leaked task.
- **UNSUBSCRIBE-on-close drill:** open subscription, close popup — UNSUBSCRIBE sent via adapter-token-derived CT with 5 s budget; completes (or fails per FR-035) without the cancelled popup token blocking it.
- **CTS leak test:** open and close 100 popups; assert no remaining `CancellationTokenSource` instances reachable from GC roots (`WeakReference` + `GC.Collect`).

**Acceptance criteria:**

- **AC-7.1** Adapter switch cancels all in-flight fetches within the FR-050 2 s budget; no fetch task posts to its VM after switch completes.
- **AC-7.2** Device byebye cancels in-flight fetches for that device only; other devices' fetches are unaffected.
- **AC-7.3** Popup close cancels in-flight invocation; subscription popup's UNSUBSCRIBE runs via adapter-token-derived CT with a separate 5 s budget (FR-034) and completes or fails per FR-035 without being blocked by the cancelled popup token.
- **AC-7.4** No `CancellationTokenSource` survives its owning entity (no leaks). Verified via `WeakReference` + GC.
- **AC-7.5** Cleanup operations during cancellation derive their token from the level above the cancelled scope (architectural invariant; verified by code review and by AC-7.3's UNSUBSCRIBE test).

**Rationale:**

- Three-level hierarchy is the lifetime graph 1:1 — derivable, not inventable.
- Standard `CancellationTokenSource.CreateLinkedTokenSource` does the heavy lifting; no custom scope-token abstraction needed.
- The "cleanup uses level-above token" invariant is the one rule downstream agents will most likely violate; pinning it explicitly is the architectural value-add.
- 2 s atomic-switch budget matches FR-050 verbatim and gives stories a concrete pass/fail target.
- Registry events + cancellation tokens compose: notification handles the UI state change, cancellation handles the in-flight work — FR-037 needs both.

**Open follow-ups:**

- **Subscription renewal under partial cancellation:** if `_adapterCts` cancels mid-renew, the HTTP request throws and the popup transitions to lapsed state (FR-038 consequence). No special handling beyond standard propagation.

---

### Decision 8 — Diagnostic Logging Pipeline

**Chosen:** Typed `IDiagnosticEmitter` facade over `Microsoft.Extensions.Logging` (`ILogger`) with a structured `DiagnosticContext` value type. Two custom `ILoggerProvider`-equivalent sinks (in-memory ring + rolling file) plug into the emitter directly. Categories live as `public const string` constants in `ohSpy.Core/Diagnostics/Categories.cs`.

**Core types:**

```csharp
public enum DiagSeverity { Verbose, Information, Warning, Error }

public sealed record DiagnosticEntry(
    DateTime TimestampUtc,
    DiagSeverity Severity,
    string Category,
    string Message,
    DiagnosticContext Context);

public readonly record struct DiagnosticContext
{
    public Guid? DeviceUuid { get; init; }       // FR-041 Identity column
    public string? Url { get; init; }            // FR-041 Endpoint column
    public string? RemoteEndpoint { get; init; } // FR-041 Endpoint fallback
    public string? ServiceId { get; init; }
    public string? ActionName { get; init; }
    public int? StatusCode { get; init; }
    public TimeSpan? Elapsed { get; init; }
    public TimeSpan? Budget { get; init; }
    public string? ErrorText { get; init; }
    public string? Sid { get; init; }
}

public interface IDiagnosticEmitter
{
    void Verbose(string category, string message, DiagnosticContext context = default);
    void Information(string category, string message, DiagnosticContext context = default);
    void Warning(string category, string message, DiagnosticContext context = default);
    void Error(string category, string message, DiagnosticContext context = default);
}
```

`DiagnosticContext` is a `readonly record struct` — zero allocation when unused (`default`), single-value-copy when populated.

**Emitter implementation:**

```csharp
public sealed class DiagnosticEmitter(
    ILogger<DiagnosticEmitter> logger,
    IDiagnosticRingSink ring,
    IDiagnosticFileSink file,
    IOptions<DiagnosticOptions> options) : IDiagnosticEmitter
{
    public void Warning(string category, string message, DiagnosticContext context = default)
    {
        if (DiagSeverity.Warning < options.Value.MinSeverity) return;

        var entry = new DiagnosticEntry(DateTime.UtcNow, DiagSeverity.Warning, category, message, context);

        logger.LogWarning(new EventId(StableHash(category), category),
            "[{Category}] {Message}", category, message);   // MEL pipeline (dotnet-trace etc.)
        ring.Push(entry);   // non-blocking; dispatcher-posted prepend
        file.Push(entry);   // non-blocking; channel + background pump
    }
    // Verbose / Information / Error similar; Verbose checks IsEnabled early for zero-alloc skip.
}
```

Severity → MEL `LogLevel` mapping: `Verbose` → `Trace`, `Information` → `Information`, `Warning` → `Warning`, `Error` → `Error`. Default `MinSeverity` at startup: `Information`. User can flip to `Verbose` at runtime via the Diagnostics viewer (no persistence — Non-Goal).

**Sinks:**

```csharp
public interface IDiagnosticRingSink
{
    void Push(DiagnosticEntry entry);
    BoundedObservableCollection<DiagnosticRow> Entries { get; }   // bound to FR-041 viewer
}

public interface IDiagnosticFileSink : IAsyncDisposable
{
    void Push(DiagnosticEntry entry);                              // non-blocking
    Task FlushAsync(CancellationToken ct);                         // app shutdown
}

public sealed record DiagnosticRow(
    DiagnosticEntry Entry,
    string IdentityLabel,    // resolved at arrival — see FR-041 rules below
    string EndpointLabel);
```

**`DiagnosticRingSink`:**

- Holds the `BoundedObservableCollection<DiagnosticRow>(5_000)` instance bound to `DiagnosticsViewModel.Entries`.
- `Push` marshals through `IUiDispatcher.Post`; the prepend happens on the UI thread tick.
- Identity / Endpoint resolution happens **at arrival** (snapshot per FR-041) before the prepend:

```
Identity:
  context.DeviceUuid is null                                                   -> "—"
  else registry.TryGetEntry(uuid, e) && e.FriendlyName is not null             -> e.FriendlyName
  else                                                                         -> "uuid:<uuid>"

Endpoint:
  context.Url is non-null URI    -> uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port)
  else context.RemoteEndpoint    -> context.RemoteEndpoint
  else                            -> "—"
```

Resolved labels are stored on `DiagnosticRow`; they do NOT update when the device's `FriendlyName` later changes or the device leaves the registry — historical entries remain stable (FR-041 "snapshot at arrival" invariant).

**`DiagnosticFileSink`:**

- Holds a `Channel<DiagnosticEntry>` bounded to capacity 1,000 with `FullMode = DropOldest`. `Push` enqueues; a background `Task` drains to disk.
- File path: `%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-<yyyyMMdd>.log` — JSON-lines (one entry per line, `System.Text.Json` serialised).
- Keys (short for grep / `jq`): `ts`, `sev`, `cat`, `msg`, `ctx`.
- Rolling: size-based, ≤ 2 MB per file; ≤ 8 rotated files (matches PRD §6 budget "≤ 16 MB total").
- On startup, if the directory or file cannot be created, the sink emits one `Warning` via the ring sink (`Diagnostics.FileSink.Unavailable`) and silently no-ops on subsequent `Push` calls — FR-042 prevents startup failure.
- On shutdown, `FlushAsync` drains the channel synchronously (5 s budget) before disposing the file handle.

**Categories as constants (single source of truth):**

`ohSpy.Core/Diagnostics/Categories.cs`:

```csharp
public static class DiagCategories
{
    public const string HttpTimeout              = "Http.Timeout";
    public const string HttpTransport            = "Http.Transport";
    public const string HttpOversizeBody         = "Http.OversizeBody";
    public const string SsdpParse                = "Ssdp.Parse";               // + ErrorText reason (Story 5.1 smoke)
    public const string SsdpSearchObserved       = "Ssdp.SearchObserved";       // Verbose: received M-SEARCH request (Story 5.1 smoke)
    public const string SsdpChannelNearFull      = "Ssdp.Channel.NearFull";
    public const string SsdpChannelOverflow      = "Ssdp.Channel.Overflow";
    public const string DescriptionFetch         = "Description.Fetch";
    public const string DescriptionFetchMismatch = "Description.Fetch.MismatchedRoot";
    public const string ScpdFetch                = "Scpd.Fetch";
    public const string ScpdParse                = "Scpd.Parse";
    public const string SoapInvoke               = "Soap.Invoke";
    public const string SoapFault                = "Soap.Fault";
    public const string GenaSubscribe            = "Gena.Subscribe";
    public const string GenaSubscribeFailed      = "Gena.Subscribe.Failed";
    public const string GenaUnsubscribe          = "Gena.Unsubscribe";
    public const string GenaUnsubscribeFailed    = "Gena.Unsubscribe.Failed";
    public const string GenaRenewFailed          = "Gena.Renew.Failed";
    public const string GenaCallbackMalformed    = "Gena.Callback.MalformedRequest";
    public const string GenaCallbackOversize     = "Gena.Callback.Oversize";
    public const string GenaCallbackNoLength     = "Gena.Callback.NoContentLength";
    public const string GenaCallbackHeadersTo    = "Gena.Callback.HeadersTimeout";
    public const string GenaCallbackBodyTo       = "Gena.Callback.BodyTimeout";
    public const string GenaCallbackFlood        = "Gena.Callback.ConnectionFlood";
    public const string GenaNotifyReceived       = "Gena.Notify.Received";       // Verbose
    public const string AdapterSwitch            = "Adapter.Switch";
    public const string AdapterSwitchTimeout    = "Adapter.Switch.Timeout";
    public const string DiagnosticsFileSinkUnavailable = "Diagnostics.FileSink.Unavailable";
    // Extended as new error paths are added; one PR adds the constant + the call sites.
}
```

This list is *exhaustive across the architectural decisions made so far*. New stories add new constants alongside their new error paths.

**Logging discipline (the architectural contract):**

1. **Every error path emits.** Every `catch` block in `Core` and every transport-error site emits at least one diagnostic with appropriate severity and context.
2. **Categories from the constants file.** No inline string literals at call sites; all categories reference `DiagCategories.XxxYyy`.
3. **Context is structured, not formatted.** Always pass via `DiagnosticContext`; message field is human-readable summary only.
4. **No PII.** UPnP protocol contents only (UUIDs, friendly names, URLs, SOAP args). No file paths beyond `%LOCALAPPDATA%`; no env/config dumps; no full XML bodies (parse-failure excerpts capped to 256 bytes).
5. **Severity discipline:** `Verbose` = high-volume normal (e.g. `Gena.Notify.Received`); `Information` = notable normal (e.g. `Adapter.Switch`); `Warning` = recoverable abnormal (the common case); `Error` = unrecoverable abnormal the app worked around.

**Test contract:**

- Categories-from-constants rule enforced via an architecture test (Open follow-up — defer mechanism to Implementation Patterns step).
- Ring sink: cross-thread `Push` marshals via dispatcher; entry arrives at index 0; identity/endpoint resolved per rules; no `Reset` notifications.
- File sink: 1,000 `Push`es over 1 second → file contains 1,000 JSON-lines; rotation triggered at 2 MB; ≤ 8 files retained.
- Startup with unwritable file path: app starts; ring sink works; single Warning emitted (`DiagnosticsFileSinkUnavailable`).
- Verbose-filtered no-allocation: `MinSeverity = Information`; 100,000 `Verbose` calls allocate zero `DiagnosticEntry` instances (verified via `BenchmarkDotNet` allocation tracking).
- FR-041 column tests: device with friendly name → Identity = friendly name; device without → Identity = `"uuid:<uuid>"`; no UUID context → Identity = `"—"`; URL with default port → Endpoint = host only; URL with non-default port → Endpoint = `host:port`; only `RemoteEndpoint` → Endpoint = `RemoteEndpoint`.

**Acceptance criteria:**

- **AC-8.1** Every `Core` `catch` block emits a `Warning` or `Error` with non-default `DiagnosticContext` carrying the relevant URL / UUID / status code / elapsed.
- **AC-8.2** Ring sink's `Entries` collection is the same instance bound to `DiagnosticsViewModel.Entries` (no copy, no view layer).
- **AC-8.3** Identity column resolution conforms to the FR-041 rules above; resolution is snapshot-at-arrival.
- **AC-8.4** Endpoint column resolution conforms to the FR-041 rules above.
- **AC-8.5** File sink writes JSON-lines under `%LOCALAPPDATA%\ohSpy\diagnostics\`; rotates at 2 MB; retains ≤ 8 files; total ≤ 16 MB.
- **AC-8.6** App start succeeds with file sink failure; single `Warning` emitted via ring sink (FR-042).
- **AC-8.7** Verbose diagnostics filtered out below `MinSeverity` allocate zero `DiagnosticEntry` instances.
- **AC-8.8** `IDiagnosticEmitter.Warning` returns within 100 µs (non-blocking; file write deferred to background pump).

**Rationale:**

- Layering on `Microsoft.Extensions.Logging` keeps us inside the broader .NET observability ecosystem (dotnet-trace, dotnet-monitor) for free.
- Typed `DiagnosticContext` struct closes the FR-041 column-resolution gap that stringly-typed message templates would leave open.
- Two-sink fan-out from the emitter keeps sinks decoupled and individually testable.
- Categories-as-constants makes diagnostic categories grep-able and refactor-safe; downstream agents cannot accidentally fork a category via typo.
- Non-blocking emit (dispatcher-posted prepend for ring; channel + background pump for file) honours NFR-P3.

**Open follow-ups:**

- **Architecture test enforcing `DiagCategories.*` usage:** Roslyn analyzer or NetArchTest rule — design in Implementation Patterns step.
- **Diagnostics viewer filter UI:** severity and category filter chips — design in UI patterns.
- **Telemetry export (OpenTelemetry / App Insights):** out of scope for v1 — internal-only tool.

---

### Decision 9 — `DescriptionFetchState` Machine

> **UDN-keyed (string identity, `OrdinalIgnoreCase`) — see Amendment A30.** UPnP UDNs are opaque strings; the registry never parses them to `Guid`. The `RegistryEntry` identity is `string Udn` (`uuid:<body>`), NOT `Guid Uuid`.

**Chosen:** Enum + method-gated transitions on `RegistryEntry`. Four states, ~5 legal transitions, `internal`-visibility `MarkXxx` methods called exclusively by `EagerDescriptionDispatcher` on the UI thread.

**Type contract:**

```csharp
public enum DescriptionFetchState
{
    Pending,    // entry added to registry; fetch not yet started
    InFlight,   // HTTP fetch issued; response not yet parsed
    Loaded,     // description fetched + parsed successfully — ONLY state visible in tree (FR-047)
    Failed      // fetch or parse failed terminally
}

public sealed class RegistryEntry
{
    public string Udn { get; }                                        // A30: opaque "uuid:<body>" — NOT a parsed Guid
    public Uri LocationUrl { get; private set; }
    public DescriptionFetchState State { get; private set; } = DescriptionFetchState.Pending;

    public DeviceDescription? Description { get; private set; }       // non-null iff State == Loaded
    public string? FailureReason { get; private set; }                // populated iff State == Failed

    public DateTime FirstSeenUtc { get; }
    public DateTime LastSeenUtc { get; private set; }
    public int AliveCount { get; private set; }
    public string? Server { get; private set; }                       // SSDP SERVER header
    public TimeSpan? CacheControlMaxAge { get; private set; }
    public string? BootId { get; private set; }                       // BOOTID.UPNP.ORG (UDA 1.1)
    public string? ConfigId { get; private set; }                     // CONFIGID.UPNP.ORG (UDA 1.1)

    internal CancellationTokenSource DeviceCts { get; } = new();      // Decision 7 device-level CTS
    public CancellationToken DeviceToken => DeviceCts.Token;

    public RegistryEntry(Guid uuid, Uri locationUrl, DateTime firstSeenUtc) { ... }

    // ── Transitions (UI thread only; internal so only Core can call) ──
    internal void MarkInFlight();                                     // throws if not Pending
    internal void MarkLoaded(DeviceDescription description);          // throws if not InFlight
    internal void MarkFailed(string reason);                          // throws if Loaded or already Failed

    // ── Metadata refresh (UI thread; no state transition) ──
    internal void RefreshSsdpMetadata(DateTime nowUtc, string? server, TimeSpan? maxAge,
                                       string? bootId, string? configId);
}
```

**Legal transitions:**

```
Pending  ──MarkInFlight──▶  InFlight  ──MarkLoaded──▶  Loaded
   │                            │
   │                            └────MarkFailed────▶  Failed
   │
   └─────MarkFailed────────────▶  Failed   (e.g. cancellation between Pending and InFlight,
                                            or semaphore wait throws)
```

All other transitions throw `InvalidOperationException`. `Loaded` and `Failed` are terminal for the lifetime of the entry.

**Who can transition:**

- `MarkInFlight` / `MarkLoaded` / `MarkFailed` are `internal` to `ohSpy.Core` — called only by `EagerDescriptionDispatcher` (and tests).
- `RefreshSsdpMetadata` is also `internal` — called by `DiscoveryService` on every subsequent alive for an already-known UUID. Does NOT trigger re-fetch (FR-043).
- All calls happen on the UI thread via `IUiDispatcher.Post`. No locks; no `volatile`; UI-thread-only mutation is the synchronisation invariant.

**Canonical flow (`EagerDescriptionDispatcher`, bounded semaphore = 8):**

```csharp
async Task FetchAsync(RegistryEntry entry)
{
    try
    {
        await _semaphore.WaitAsync(entry.DeviceToken);    // may throw OperationCanceledException
        _dispatcher.Post(() => entry.MarkInFlight());

        var bytes = await _http.FetchDeviceDescriptionAsync(entry.LocationUrl, entry.DeviceToken);
        var description = _descParser.Parse(bytes);

        if (description.RootUdn != entry.Uuid)
        {
            // FR-043 mismatched-root backstop
            _diag.Information(DiagCategories.DescriptionFetchMismatch,
                "Root UDN mismatch — entry removed",
                new DiagnosticContext { DeviceUuid = entry.Uuid,
                                        Url = entry.LocationUrl.ToString(),
                                        ErrorText = $"declared root: {description.RootUdn}" });
            _dispatcher.Post(() => _registry.Remove(entry.Uuid));
            return;
        }

        _dispatcher.Post(() =>
        {
            entry.MarkLoaded(description);
            _registry.RaiseDeviceLoaded(entry);          // ← admits row to tree (FR-047)
        });
    }
    catch (OperationCanceledException) when (entry.DeviceToken.IsCancellationRequested)
    {
        // External cancellation (byebye / adapter switch) — entry already being removed; no transition.
    }
    catch (Exception ex)
    {
        _diag.Warning(DiagCategories.DescriptionFetch, "fetch/parse failed",
            new DiagnosticContext { DeviceUuid = entry.Uuid,
                                    Url = entry.LocationUrl.ToString(),
                                    ErrorText = ex.Message });
        _dispatcher.Post(() => entry.MarkFailed(ex.Message));
        // Failed entry remains in registry; not in tree (FR-047); visible in diagnostic viewer.
    }
    finally
    {
        _semaphore.Release();
    }
}
```

**Registry event surface (this decision's primary externally-visible artifact):**

```csharp
public interface IDeviceRegistry
{
    bool TryGetEntry(Guid uuid, out RegistryEntry entry);
    IReadOnlyCollection<RegistryEntry> Loaded { get; }   // snapshot of state==Loaded entries
    int Count { get; }                                   // total registry count, all states

    event Action<RegistryEntry> DeviceLoaded;            // raised when MarkLoaded runs
    event Action<RegistryEntry> DeviceUpdated;           // raised on already-Loaded entry's label/data change (FR-054 trigger)
    event Action<Guid> DeviceRemoved;                    // raised on byebye, prune, mismatched-root removal
}
```

**No `DeviceAdded` event.** The VM never sees entries before `Loaded`. This is the simplification that lets the VM avoid filtering on state — it subscribes to `DeviceLoaded` and gets exactly the right rows. The internal "entry created in `Pending`" event is just `EagerDescriptionDispatcher.Schedule(entry)`, an `internal` Core call.

**Re-discovery semantics:**

- `byebye` → `_registry.Remove(uuid)` → `entry.DeviceCts.Cancel()` → entry GC'd once popups drop their back-refs.
- Subsequent alive for same UUID → **new** `RegistryEntry` instance, fresh `Pending`, fresh `DeviceCts`, fresh fetch scheduled.
- `RegistryEntry` instances have no persistent identity beyond their lifetime in the registry. No reset path, no carry-over, no stale-CTS reuse.

**Thread discipline:**

- All `Mark*` and `RefreshSsdpMetadata` calls happen on UI thread via `IUiDispatcher.Post`. Enforced by the dispatcher contract (Decision 1) and by code review.
- Reads of `State` and `Description` from VM happen on UI thread (VM lives there).
- Reads of `DeviceToken` from background dispatcher are safe (token is thread-safe).
- No fields require `volatile` or locks.

**Test contract:**

- State-transition test matrix: every legal transition succeeds; every illegal transition throws `InvalidOperationException`.
- `Loaded → re-MarkLoaded` throws; `Failed → MarkLoaded` throws; etc.
- Re-discovery test: register, fetch, byebye, re-alive same UUID → asserts new `RegistryEntry` instance (different reference), fresh `Pending` state, new fetch scheduled.
- Mismatched-root test: dispatcher receives description whose `RootUdn` ≠ requesting UUID → entry removed, `Information` diagnostic with `declared.root.uuid` in context, no `MarkLoaded` called on either entry.
- Subsequent alive: known-UUID alive triggers `RefreshSsdpMetadata` (LastSeenUtc updated, AliveCount incremented, headers refreshed); does not call any `Mark*`; does not re-issue HTTP.
- Failed-entry visibility: failed entry remains in `_registry.Count` total; not in `_registry.Loaded`; `DeviceLoaded` never raised; one Warning diagnostic emitted with full context.

**Acceptance criteria:**

- **AC-9.1** Legal transitions allowed: `Pending → InFlight`; `Pending → Failed`; `InFlight → Loaded`; `InFlight → Failed`. All others throw `InvalidOperationException`.
- **AC-9.2** `RegistryEntry.Description` is non-null iff `State == Loaded`.
- **AC-9.3** Registry raises `DeviceLoaded(entry)` exactly when `MarkLoaded` executes; never for `Failed`; never for `Pending` / `InFlight`.
- **AC-9.4** Subsequent alive for known UUID calls `RefreshSsdpMetadata` only; does not call any `Mark*`; does not re-issue HTTP fetch (FR-043 cache invariant).
- **AC-9.5** Byebye-then-rediscovery of same UUID creates a new `RegistryEntry` instance; fresh `Pending` state; fetch re-issued.
- **AC-9.6** Mismatched-root response causes entry removal + `Information` diagnostic with `declared.root.uuid`; no `MarkLoaded` called on either entry.
- **AC-9.7** Cancellation during fetch (byebye / adapter switch) results in no state transition; entry already being removed via the registry's remove path.

**Rationale:**

- Enum + method-gated transitions: smallest model that prevents flag-based "invalid combinations are representable" failure mode. Pulling into a separate state-machine type adds indirection without buying invariant strength at 4-state granularity.
- `internal` `Mark*` visibility makes `EagerDescriptionDispatcher` the sole authorised mutator. Any future agent debugging "why is a row missing?" finds one chokepoint.
- UI-thread-only mutation removes synchronisation overhead and reasoning load.
- Eliminating `DeviceAdded` from the registry's event surface keeps the state machine an implementation detail of `Core`; VMs subscribe to a stable contract (`DeviceLoaded`).
- Re-discovery creating a new instance: no reset code path, no state carryover bugs, no stale-CTS reuse — instance-equals-lifetime is a stronger invariant than a reset-able entity.

**Open follow-ups:**

- **Post-load operation failures** (SCPD fetch fail, SOAP invoke fail, GENA subscribe fail): out of scope here — those are local to their operations and do NOT affect `DescriptionFetchState`. The state machine governs description-fetch only.
- **Manual description re-fetch** (e.g. Properties window refresh button): not in v1 (Non-Goal: no manual refresh action). If added, the cleanest shape is `entry.Reset()` requiring `Loaded` or `Failed` and returning to `Pending`. Defer.

---

### Decision 10 — Window Ownership Mechanism

> *Revised 2026-06-04 by [Amendment A31 — Popups float in free z-order; no Win32 owner link](#amendment-a31--popups-float-in-free-z-order-no-win32-owner-link-fr-046--decision-10-revision). The Win32 owner link (`GWLP_HWNDPARENT`) pinned popups **always above** the shell, which proved confusing — clicking the shell could never bring it forward over an open popup. `Adopt` no longer sets the owner link: a popup opens on top via `Activate()` then floats in normal z-order, and **close-with-parent** is re-implemented as an explicit `parent.Closed` handler. The always-above, no-push-behind, and minimise/restore-with-parent behaviours below (and AC-10.1/10.3/10.4) are superseded; the `Activate()`-then-`Adopt` pattern, the `GetChildrenOf` introspection seam, and close-with-parent (now AC-10.2, handler-based) stand. The interop snippet below is historical.*

**Chosen:** Custom `IWindowOwnershipManager` service in `ohSpy.App` that encapsulates the Win32 owner relationship (`SetWindowLongPtr(GWLP_HWNDPARENT)`) for every popup. Order of operations is `child.Activate()` then `_windowOwnership.Adopt(child, _shellWindow)` — pattern applied uniformly across all four popup types.

**Why interop is required:** WinUI 3's `Window` doesn't expose an `Owner` property (unlike WPF). The four FR-046 behaviours (z-order above parent, no-push-behind on focus, minimise/restore together, close-with-parent) are OS-delivered by the Win32 owner relationship — but the relationship is only accessible via `SetWindowLongPtr`. Centralising the interop in one service makes the FR-046 contract a pattern, not boilerplate.

**Service contract:**

```csharp
public interface IWindowOwnershipManager
{
    // Establish FR-046 ownership. MUST be called AFTER child.Activate().
    void Adopt(Window child, Window parent);

    IReadOnlyList<Window> GetChildrenOf(Window parent);   // testability
}
```

**Implementation (in `ohSpy.App/Windowing/WindowOwnershipManager.cs`):**

```csharp
internal sealed partial class WindowOwnershipManager : IWindowOwnershipManager
{
    private const int GWLP_HWNDPARENT = -8;
    private readonly Dictionary<IntPtr, List<IntPtr>> _ownership = new();

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public void Adopt(Window child, Window parent)
    {
        var childHwnd  = WinRT.Interop.WindowNative.GetWindowHandle(child);
        var parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(parent);

        SetWindowLongPtr(childHwnd, GWLP_HWNDPARENT, parentHwnd);

        if (!_ownership.TryGetValue(parentHwnd, out var children))
            _ownership[parentHwnd] = children = new();
        children.Add(childHwnd);

        child.Closed += (_, _) => _ownership[parentHwnd].Remove(childHwnd);
    }

    public IReadOnlyList<Window> GetChildrenOf(Window parent) { ... }
}
```

`SetWindowLongPtrW` works identically on x64 and ARM64 — `IntPtr` is the correct pointer-sized type for both.

**Usage pattern (the canonical popup-open sequence — applies to all four popup types):**

```csharp
public void OpenInvocationPopup(ActionNodeViewModel action)
{
    var window = new InvocationPopupWindow(action);
    window.Activate();                                   // (1) WinUI 3 lifecycle: window creation completes
    _windowOwnership.Adopt(window, _shellWindow);        // (2) FR-046 ownership applied
}
```

The order — Activate then Adopt — is non-obvious but empirically required in WinUI 3; calling `SetWindowLongPtr` before `Activate` leaves the relationship undefined. **Architecture pins it.** Downstream agents follow the pattern verbatim across all four popup creation sites.

**Coverage across popup types:**

| Popup type | FR | Created by | Parent |
|---|---|---|---|
| Action invocation popup | FR-025 | `ActionNodeViewModel.OnDoubleClick` | `_shellWindow` |
| Subscription popup | FR-032 | `ServiceNodeViewModel.OnSubscribe` | `_shellWindow` |
| Diagnostics viewer | FR-041 | `ShellViewModel.OpenDiagnosticsCommand` | `_shellWindow` |
| Properties window | FR-052 | `DeviceNodeViewModel.OnProperties` | `_shellWindow` |

All four routes through `_windowOwnership.Adopt(window, _shellWindow)` after `window.Activate()`.

**What `Adopt` delivers (FR-046 behaviours, all OS-delivered):**

| FR-046 behaviour | Mechanism |
|---|---|
| Appears above parent on show | Win32 owner z-order |
| Stays above parent on focus shift | Win32 owner z-order (not focus-dependent) |
| Minimises/restores with parent | OS-managed: owned windows track owner's `WS_MINIMIZE` |
| Closes when parent closes | OS-managed: closing owner destroys owned windows |

No event handlers needed for minimise/restore or close-cascade — the OS does it.

**What `Adopt` does NOT deliver (intentional):**

- **Modality:** FR-046 explicitly says ownership is z-order + lifetime, not modality. Popups are independently activatable; user can interact with the main window while popups are open.
- **Child focusing:** caller is responsible for `Activate` before `Adopt`; `Adopt` doesn't shift focus.

**Adapter-switch interaction (Decision 7 cross-reference):**

Adapter switch (FR-050) does NOT close popups via window ownership — it cancels their CTS, which causes them to transition to FR-037 "device unreachable" state. The popup window remains open; only its content changes. Window-ownership close-cascade is only relevant on main-window close (app shutdown).

**Cascading implications:**

- `IWindowOwnershipManager` registered as singleton in `App` DI. Injected into the four popup-creation services.
- Popup creation is the only place in the codebase that does Win32 interop for owner-setting.
- The `Closed` event hook in `Adopt` cleans up the tracking dictionary, but the OS handles actual window destruction — tracking is for testability and lifecycle queries only.

**Test contract:**

- `Adopt` unit test: create two windows; call `Adopt(child, parent)`; verify `GetChildrenOf(parent)` returns `[child]`.
- Close-cascade integration test (manual or gated `[UiTest]` if WinAppDriver added): open all four popup types, close main window, verify all popups close. Gated behind UI-test infrastructure that's out of v1 scope.
- Minimise/restore manual test: open popup, minimise main, verify popup minimises; restore, verify popup restores.
- z-order manual test: open popup, click main window for focus, verify popup stays above main visually.

**Acceptance criteria:**

- **AC-10.1** `WindowOwnershipManager.Adopt(child, parent)` sets Win32 owner via `SetWindowLongPtr(GWLP_HWNDPARENT)` after `child.Activate()`. Verified via reflection over `_ownership` and visual confirmation during dev.
- **AC-10.2** Closing the main window closes every popup currently owned by it (manual / UI-test).
- **AC-10.3** Minimising the main window minimises every popup; restoring restores them (manual / UI-test).
- **AC-10.4** Focus shift to the main window does NOT push popups behind it (manual / UI-test).
- **AC-10.5** All four popup creation sites (FR-025, FR-032, FR-041, FR-052) call `Activate` then `Adopt(window, _shellWindow)` — pattern verified by code review and architecture test for the popup-creation services.

**Rationale:**

- WinUI 3 doesn't expose `Window.Owner`; Win32 interop is the only mechanism to deliver FR-046 behaviours.
- Centralising in `IWindowOwnershipManager` makes FR-046 a property of "ohSpy popup-creation pattern", not a thing each popup type re-derives.
- Win32 owner relationship delivers all four FR-046 behaviours OS-natively — no event-handler scaffolding.
- Pinning the `Activate` → `Adopt` order in the architecture saves rediscovery in every popup story.

**Open follow-ups:**

- **UI automation (WinAppDriver / Appium):** when added, AC-10.2..10.4 become automated. Defer.
- **Popups owned by other popups:** out of scope; no such interaction in the FR set. `Adopt` accepts any `Window` as parent, so generalisation is free if needed later.
- **Per-monitor / per-DPI positioning relative to parent:** WinUI default (centred on parent's monitor) is fine for v1.

---

### Decision 11 — Per-Request HTTP Timeout Defaults

**Chosen:** `IOptions<HttpTimeoutOptions>` bag with empirically-anchored defaults. Resolves the NFR-P2 Open Question. Pins values for the eight outbound and two inbound HTTP budgets called out by Decisions 3 and 4.

**Options type:**

```csharp
public sealed class HttpTimeoutOptions
{
    // IUpnpHttpClient per-request budgets (Decision 3)
    public TimeSpan DescriptionFetch     { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ScpdFetch            { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SoapInvoke           { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan GenaSubscribe        { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan GenaUnsubscribe      { get; init; } = TimeSpan.FromSeconds(5);

    // SocketsHttpHandler (shared HttpClient)
    public TimeSpan ConnectTimeout       { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan KeepAlivePingDelay   { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan KeepAlivePingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // Inbound GENA callback host (Decision 4)
    public TimeSpan CallbackHeaders      { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CallbackBody         { get; init; } = TimeSpan.FromSeconds(5);
}
```

**Per-row justification:**

| Field | Value | Why |
|---|---|---|
| `DescriptionFetch` | 5 s | Description XML 1–20 KB typical; 5 s is 25× a healthy device's response. Anchors SC-001 (startup ≤ 7 s = 5 s MX + 2 s fetch). |
| `ScpdFetch` | 10 s | SCPDs range from KB to 2 MB (IGD routers); 10 s covers 2 MB at slow-LAN throughput. Pulled up from D3 placeholder 5 s — IGD vendors empirically take 3–8 s on contended networks. Operation timeout ≠ perceived budget: budget is "operator sees first action" (FR-100 incremental parse delivers that); timeout is "give up entirely". |
| `SoapInvoke` | 10 s | Carried from UpnpSpy. Cheap DLNA renderers take 2–5 s on slow actions (`Browse`). 10 s = generous headroom. |
| `GenaSubscribe` | 5 s | SUBSCRIBE is cheap (single round-trip, no body); usual completion < 500 ms. |
| `GenaUnsubscribe` | 5 s | Same shape; called on popup close — short enough that close feels instant, low enough that hung device doesn't block close (FR-035 makes failure non-blocking anyway). |
| `ConnectTimeout` | 5 s | TCP SYN-SYNACK on LAN < 5 ms typical. 5 s catches dead IPs (device left mid-discovery) fast. |
| `KeepAlivePingDelay` | 15 s | OS-level keep-alive probe interval. Boundary between active/idle. |
| `KeepAlivePingTimeout` | 5 s | Probe unanswered for 5 s → connection dead. Total "truly hung TCP" detection: 20 s — well inside 8-hour session tolerance, well outside any legitimate device delay. |
| `CallbackHeaders` | 5 s | Slowloris defense (Decision 4). Symmetric with outbound HTTP budgets — bidirectional uniformity. |
| `CallbackBody` | 5 s | Body-stall defense. NOTIFY bodies typically < 50 KB; 5 s at 1 KB/s pessimistic drip covers 5 KB worst case. Generous in practice. |

**DI registration:**

```csharp
// In App startup:
services.Configure<HttpTimeoutOptions>(_ => { /* defaults from initialiser */ });

// In tests:
services.Configure<HttpTimeoutOptions>(o =>
{
    o.ScpdFetch = TimeSpan.FromMilliseconds(100);
});
```

Injected into:
- `UpnpHttpClient` (uses 8 outbound timeouts).
- `EventCallbackHost` (uses `CallbackHeaders`, `CallbackBody`).
- `SocketsHttpHandler` factory (uses `ConnectTimeout`, `KeepAlivePingDelay`, `KeepAlivePingTimeout`).

**Reference points used to calibrate:**

- UpnpSpy `plan.md` intended defaults (per addendum): 5 s description, 10 s SOAP. The `HttpClient.Timeout` 100 s default leaking through was the actual prior bug, not these values.
- PRD §6 Performance Budgets: SC-001 startup ≤ 7 s; SC-004 service-node expansion ≤ 2 s typical; SC-010 invocation popup interactive ≤ 1 s; SC-011 action result ≤ 2 s.
- Typical UPnP device response times on LAN: description 20–200 ms; SCPD 20–500 ms; SOAP 50–500 ms. Per-op timeouts target *misbehaving* devices, not typical.

**Persistence:**

None. Per Non-Goal "no settings persistence", values do NOT round-trip to disk between sessions. `IOptions<HttpTimeoutOptions>` lives in-memory only. App restart resets to defaults.

**Test contract:**

- **Defaults respected:** with default options, every facade method honours its budget within ± 100 ms (uses `HangAfter200Ok` fixture from Decision 3 test plan).
- **Override works:** configure `ScpdFetch = 100 ms` in test DI; assert `FetchScpdAsync` against a 200 ms-delay handler throws `UpnpTimeoutException`.
- **KeepAlive ping surfaces hung TCP:** simulate truly-hung TCP (fake socket accepts SYN, ACKs data, then stops responding); verify OS-level keep-alive surfaces connection failure within 20 s ± 5 s.

**Acceptance criteria:**

- **AC-11.1** `HttpTimeoutOptions` defaults match the table above.
- **AC-11.2** Every `IUpnpHttpClient` method's per-op timeout reads from `IOptions<HttpTimeoutOptions>`; no inline `TimeSpan.FromSeconds(N)` literals at call sites.
- **AC-11.3** Tests override individual fields via `services.Configure<HttpTimeoutOptions>(...)`; new values take effect.
- **AC-11.4** Truly-hung TCP (keep-alive ping unanswered) surfaces connection failure within 20 s ± 5 s (`KeepAlivePingDelay + KeepAlivePingTimeout`).

**Rationale:**

- Empirically anchored, not theoretical. Calibrated against addendum prior-art numbers, PRD §6 budgets, and known device behaviour.
- `IOptions<>` over hard constants because we will tune empirically post-fixture-build (Murat's chaos test from the party-mode review). Can't tune what we can't override.
- Single options bag, not per-vendor, defers complexity until vendor-specific need is observed.
- Symmetric inbound/outbound HTTP budgets (5 s headers, 5 s body) make the system's tolerance for "slow HTTP" uniform bidirectionally.

**Open follow-ups:**

- **Empirical re-tuning after fixture build-out:** scheduled into the test-infrastructure story; defaults may shift ± 50% based on real device behaviour observed in CI chaos tests.
- **Diagnostics viewer timeout overrides:** dev-only debug convenience; defer until viewer ships.
- **Per-vendor overrides:** speculative; defer until a real device doesn't fit single set.

---

### Decision 12 — Build / Packaging Pipeline Shape

**Chosen:** No CI in v1. Local-only build and test (`dotnet build` / `dotnet test`). MSBuild target produces an unsigned InnoSetup installer (`ohSpy-setup-<version>-x64.exe`) on demand. Installer is the v1 distribution artifact.

**This decision back-applies upstream:**

- **Starter Template Evaluation (Step 3):** the `dotnet new winui` template defaults to **Packaged** (MSIX) mode. ohSpy switches to **Unpackaged** mode via `<WindowsPackageType>None</WindowsPackageType>` in `ohSpy.App.csproj`. The Windows App Runtime is bundled via self-contained publish and bound at app startup via the bootstrap initialiser.
- **PRD §8.1:** "unsigned MSIX installer" line is revised in the PRD to "unsigned InnoSetup installer (single `setup.exe`)" — see the PRD addendum/revision note.

**No CI rationale:**

- Solo greenfield. The first user of every build is the author. Per-commit CI buys nothing beyond what local `dotnet test` already provides.
- L&L narrative doesn't depend on a green-badge — the artifact trail (brief / PRD / architecture / stories) carries the methodology story; a CI dashboard would be a distraction from the spec-driven-development point.
- If we later need CI (second contributor, public OSS release, distribution to non-Linn audience), `.github/workflows/ci.yml` is a 50-line drop-in. Architecturally, nothing about the codebase prevents it; nothing about no-CI requires it.

**Local build / test workflow (the canonical dev loop):**

```powershell
dotnet build          # whole solution
dotnet test           # all xUnit suites (unit + integration + chaos)
dotnet publish src/ohSpy.App/ohSpy.App.csproj -c Release -r win-x64 --self-contained
dotnet build -t:BuildInstaller -p:Configuration=Release  # produces installer/ohSpy-setup-<ver>-x64.exe
```

Total wall-clock budget for the inner loop (build + test): < 30 s. The installer build is opt-in; not part of the default loop.

**Installer mechanism — InnoSetup:**

InnoSetup 6 (Jordan Russell) — free, ubiquitous, Pascal-scripted, produces a single self-contained `setup.exe`. Equivalents considered:
- **NSIS** — older, Lua-style scripting, viable but less ergonomic; no advantage.
- **WiX** without MSI (Burn / Bundle) — overkill; we don't need MSI-style transactional behaviour.
- **MSIX** — already rejected (sandbox interferes with diagnostic-log path; unsigned MSIX requires the user to enable "developer mode" or "sideload apps" — bad audience UX).

InnoSetup wins on: smallest install footprint, no platform-imposed sandbox, "ignore SmartScreen → run anyway" is the only friction (signing optional), familiar to Windows engineers.

**Installer behaviour (the InnoSetup script's contract):**

- **Install location:** `%LOCALAPPDATA%\Programs\ohSpy\` — per-user, no Administrator required (consistent with the "no Admin" theme established by FR-049 for the callback host).
- **Start Menu shortcut:** `Programs\ohSpy\ohSpy.lnk`.
- **Desktop shortcut:** opt-in checkbox in the installer; unchecked by default.
- **Uninstaller:** registered with Windows Apps & Features under "ohSpy"; removes install dir and Start Menu shortcut. Does NOT remove `%LOCALAPPDATA%\ohSpy\diagnostics\` — diagnostic logs persist across uninstall (operator value).
- **Upgrade behaviour:** detect prior install via the same `AppId` GUID; if present, replace silently (no "please uninstall first" prompt). Standard InnoSetup `usesetupclassic` / `SetupAppMutex` pattern.
- **Architecture:** v1 ships x64 only via the installer; ARM64 publish profile exists in the project (Step 3) and can be built manually via `dotnet publish ... -r win-arm64` from a dev box if needed. Adding ARM64 to the installer is a 10-line `.iss` change when wanted; not in v1.
- **Signing:** none. SmartScreen will show "Windows protected your PC" on first run; user clicks "More info" → "Run anyway". Acceptable for internal-Linn audience. Path to signing (EV cert, internal Linn cert, sigstore-style) is an upgrade decision tied to wider distribution.

**`.iss` script location:** `installer/ohSpy.iss`. Versioned in the repo. Hand-authored; ~50 lines.

**MSBuild target — `BuildInstaller`:**

Lives in `src/ohSpy.App/ohSpy.App.csproj` (or extracted to `Directory.Build.targets` if it grows):

```xml
<Target Name="BuildInstaller"
        DependsOnTargets="Publish"
        Condition="'$(RuntimeIdentifier)' == 'win-x64' Or '$(BuildInstaller)' == 'true'">

  <PropertyGroup>
    <InnoSetupCompiler Condition="'$(InnoSetupCompiler)' == ''">$(ProgramFiles)\Inno Setup 6\ISCC.exe</InnoSetupCompiler>
    <InstallerOutputDir>$(MSBuildThisFileDirectory)..\..\installer\out</InstallerOutputDir>
    <InstallerVersion>$([System.DateTime]::UtcNow.ToString("yyyy.MM.dd.HHmm"))</InstallerVersion>
  </PropertyGroup>

  <Error Condition="!Exists('$(InnoSetupCompiler)')"
         Text="Inno Setup compiler not found at '$(InnoSetupCompiler)'. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or override InnoSetupCompiler." />

  <Exec Command="&quot;$(InnoSetupCompiler)&quot; /Q /DPublishDir=&quot;$(PublishDir)&quot; /DOutputDir=&quot;$(InstallerOutputDir)&quot; /DVersion=$(InstallerVersion) &quot;$(MSBuildThisFileDirectory)..\..\installer\ohSpy.iss&quot;" />

  <Message Text="Installer built: $(InstallerOutputDir)\ohSpy-setup-$(InstallerVersion)-x64.exe" Importance="high" />
</Target>
```

Invocation: `dotnet build src/ohSpy.App -t:BuildInstaller -p:Configuration=Release`. The target depends on `Publish`, so it runs `dotnet publish` first if the publish output doesn't exist.

**Version stamping:** `yyyy.MM.dd.HHmm` UTC at build time. No semver in v1 — this is an internal tool with no API contract; the build timestamp suffices as a unique identifier. Open follow-up if the team adopts semver later.

**Bootstrap initialiser change (consequence of going Unpackaged):**

Unpackaged WinUI 3 apps must explicitly initialise the Windows App Runtime at startup before any WinUI types are touched. In `src/ohSpy.App/Program.cs` (or the equivalent `Main` entry point — replacing the generated `Main`):

> *Patched 2026-06-01 by [Amendment A7 — Story 1.1 implementation reality](#amendment-a7--bootstraptryinitialize-real-api-signature-decision-12-refinement): the snippet originally showed a 4-arg int-returning form of `Bootstrap.TryInitialize` that does not exist in WindowsAppSDK 2.x. The real API returns `bool` and takes a 5th `InitializeOptions options` parameter. The block below shows the corrected signature as actually shipped in Story 1.1.*

```csharp
using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace ohSpy.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Bind to the Windows App Runtime self-contained-published alongside this exe.
        // MUST run before any Microsoft.UI.Xaml type is touched.
        // API surface (WindowsAppSDK 2.x):
        //   bool Bootstrap.TryInitialize(uint majorMinorVersion, string versionTag,
        //                                PackageVersion minVersion, InitializeOptions options,
        //                                out int hr)
        // Returns true on success; false + non-zero hr on failure.
        var minVersion = new PackageVersion(major: 2, minor: 1, build: 3, revision: 0);
        var ok = Bootstrap.TryInitialize(
            majorMinorVersion: 0x00020001,            // WindowsAppSDK 2.1.x
            versionTag: "",
            minVersion: minVersion,
            options: Bootstrap.InitializeOptions.None,
            out var hr);

        if (!ok)
        {
            // Bootstrap failed — runtime missing or mismatched.
            // No WinUI available yet; no diagnostic sink yet. Native message box + exit is terminal.
            _ = MessageBoxW(
                IntPtr.Zero,
                $"Windows App Runtime initialisation failed (0x{hr:X8}).\n\n" +
                "Reinstall ohSpy. If the problem persists, contact the ohSpy maintainers.",
                "ohSpy",
                MB_OK | MB_ICONERROR);
            return hr;
        }

        try
        {
            // CA1806 suppressed: WinUI 3's Application.Start consumes the App instance via internal
            // machinery; the lambda is the canonical Microsoft-documented startup pattern.
#pragma warning disable CA1806
            Microsoft.UI.Xaml.Application.Start(_ => new App());
#pragma warning restore CA1806
        }
        finally
        {
            Bootstrap.Shutdown();
        }
        return 0;
    }

    private const uint MB_OK = 0x0u;
    private const uint MB_ICONERROR = 0x10u;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
```

A self-contained publish bundles the Windows App Runtime alongside the EXE; `Bootstrap.TryInitialize` finds the bundled runtime and binds to it. The installer carries everything; the user's machine needs nothing pre-installed.

**csproj changes (consequence):**

> *Patched 2026-06-01 by [Amendment A8 — Story 1.1 implementation reality](#amendment-a8--csproj-snippet-completeness-decision-12-refinement): the original snippet omitted `<PlatformTarget>AnyCPU</PlatformTarget>` (Debug builds without a RID hit NETSDK1032), `<UseWinUI>true</UseWinUI>` (required for WinUI 3 build targets even when set by the template), and the `<StartupObject>` + `DISABLE_XAML_GENERATED_MAIN` pair (without which the XAML compiler emits a competing `Main` and the build fails CS0017 "multiple entry points"). The block below shows the corrected `<PropertyGroup>` as actually shipped in Story 1.1.*

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
  <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
  <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
  <!-- Allow MSBuild to build without a RID (Debug builds don't need one); only Publish/BuildInstaller require an explicit RID. -->
  <PlatformTarget>AnyCPU</PlatformTarget>
  <RootNamespace>ohSpy.App</RootNamespace>
  <AssemblyName>ohSpy.App</AssemblyName>
  <ApplicationManifest>app.manifest</ApplicationManifest>
  <UseWinUI>true</UseWinUI>
  <WindowsPackageType>None</WindowsPackageType>                     <!-- Unpackaged -->
  <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>     <!-- bundle WAS -->
  <SelfContained>true</SelfContained>                               <!-- bundle .NET -->
  <PublishSingleFile>false</PublishSingleFile>                      <!-- installer wraps -->
  <!-- Pin our Program.Main as the entry point; disable the XAML-compiler-generated Main from
       App.xaml so we don't get CS0017 "multiple entry points". -->
  <StartupObject>ohSpy.App.Program</StartupObject>
  <DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>
</PropertyGroup>
```

**Test contract:**

- `dotnet build -t:BuildInstaller -p:Configuration=Release` produces `installer/out/ohSpy-setup-<version>-x64.exe`.
- The installer, run on a clean Windows 11 machine without WindowsAppRuntime or .NET 10 installed, installs and launches the app successfully.
- Diagnostic logs land in `%LOCALAPPDATA%\ohSpy\diagnostics\` (not in any MSIX-virtualised path).
- Uninstall via Apps & Features removes the install dir and shortcuts; diagnostic dir survives.

**Acceptance criteria:**

- **AC-12.1** `installer/ohSpy.iss` exists and produces a self-contained `setup.exe` when compiled with InnoSetup 6.
- **AC-12.2** `dotnet build -t:BuildInstaller -p:Configuration=Release` runs `dotnet publish` (if needed) and then `iscc.exe`, producing the installer artifact.
- **AC-12.3** Installer installs the app to `%LOCALAPPDATA%\Programs\ohSpy\` per-user with no Administrator required.
- **AC-12.4** App launches successfully on a clean Windows 11 machine without any pre-installed runtimes (verifies self-contained bundling).
- **AC-12.5** Uninstaller removes install dir + Start Menu shortcut; preserves `%LOCALAPPDATA%\ohSpy\diagnostics\`.
- **AC-12.6** `<WindowsPackageType>None</WindowsPackageType>` is set in `ohSpy.App.csproj`; bootstrap initialiser runs before any WinUI type is touched.

**Rationale:**

- No CI: solo project, no PR gating need, no public-distribution gate. Local `dotnet test` discipline is enough. Architecture leaves CI as a future drop-in.
- InnoSetup over MSIX: unsigned MSIX requires user-side "developer mode" toggles; InnoSetup unsigned only triggers SmartScreen, which the audience knows to bypass. MSIX sandbox would also obscure `%LOCALAPPDATA%\ohSpy\diagnostics\` (the FR-040 log file location) — wrong UX for a diagnostic tool.
- Per-user install (`%LOCALAPPDATA%\Programs\`): no Administrator required, aligned with the "no Admin" architectural theme (FR-049 for callback host; consistent across distribution).
- Self-contained publish: ohSpy controls its runtime stack. Linn engineers don't need to install .NET 10 or Windows App Runtime separately.
- Version stamp = UTC build timestamp: avoids premature semver commitment for an internal tool with no API contract.

**Open follow-ups:**

- **Adding CI later:** drop-in `.github/workflows/ci.yml` if needed; nothing in the codebase precludes it.
- **Signing the installer:** internal Linn certificate or EV cert if/when wider distribution is decided.
- **ARM64 in the installer:** 10-line `.iss` change; defer until concrete need.
- **Semver scheme:** if/when the tool gains a stable API or external consumers.

## Implementation Patterns & Consistency Rules

Conventions that prevent multiple AI agents (or future-Simon) from writing inconsistent code across stories. The 12 architectural decisions settle *what* the system is; this section settles *how* to write it. 15 patterns, grouped.

### Naming & Structure

#### 1. C# / file / namespace conventions

- PascalCase for types, methods, properties, public fields, constants.
- camelCase for parameters, locals.
- `_camelCase` for private fields. Primary-constructor parameters keep plain camelCase (no underscore — they double as parameter names).
- Async methods suffix `Async`. Interfaces prefix `I`.
- One type per file (rare nested-private-type exceptions OK). File name = type name + `.cs`.
- File-scoped namespaces (`namespace ohSpy.Core.Http;`).
- Namespace structure mirrors folder structure.
- Root namespaces: `ohSpy.App`, `ohSpy.Core`, `ohSpy.Core.Tests`.

**Anti-pattern:** `private string m_url`; multiple `internal class` declarations in one file.

#### 2. `Core` ↔ `App` boundary rules

| Allowed in `Core` | Forbidden in `Core` |
|---|---|
| `System.*` | `Microsoft.UI.*` |
| `Microsoft.Extensions.{DependencyInjection, Logging, Options}` | `Microsoft.Windows.*` (WindowsAppSDK-specific) |
| `CommunityToolkit.Mvvm` | `WinRT.Interop.*` |
| `System.Net.Sockets`, `System.Net.Http`, `System.Xml`, `System.Text.Json` | Any P/Invoke |
| Other `Core` namespaces | `ohSpy.App.*` |

- All UI marshalling goes through `IUiDispatcher` (Decision 1). Core never touches `DispatcherQueue` directly.
- All P/Invoke in `App` only.
- All `Window` types in `App`. VMs in `Core` (UI-agnostic, testable). Views + XAML in `App`.
- The dispatcher singleton, file-sink, window-ownership service, and `Bootstrap.TryInitialize` calls live in `App`.

**Anti-pattern:** a `ViewModel` in `Core` referencing `Microsoft.UI.Xaml.Visibility`. Use a `bool IsVisible` + a converter in `App`.

#### 3. Folder layout

```
src/ohSpy.Core/
  Collections/         # BoundedObservableCollection, IdentityKeyedSortedCollection (D6)
  Diagnostics/         # IDiagnosticEmitter, Categories, DiagnosticEntry, DiagnosticContext (D8)
  Discovery/           # ISsdpTransport, SsdpParser, DiscoveryService (D2)
  Devices/             # RegistryEntry, DeviceRegistry, EagerDescriptionDispatcher (D9)
  Events/              # IEventCallbackHost, SubscriptionClient, NotifyRequest (D4)
  Http/                # IUpnpHttpClient, UpnpHttpClient, UpnpExceptions, HttpTimeoutOptions (D3, D11)
  Models/              # DeviceDescription, ScpdAction, ScpdStateTable, SsdpDatagram, etc.
  Scpd/                # IScpdParser, XmlReaderScpdParser, IDeviceDescriptionParser (D5)
  Soap/                # SoapRequest, SoapResponse, SoapEnvelopeBuilder
  Threading/           # IUiDispatcher (D1)
  ViewModels/          # ShellViewModel, DeviceTreeViewModel, DeviceNodeViewModel, …

src/ohSpy.App/
  App.xaml + App.xaml.cs
  MainWindow.xaml + .cs
  Program.cs                       # Bootstrap.TryInitialize → Application.Start (D12)
  Composition/ServiceRegistration.cs
  Windowing/WinUiDispatcher.cs
  Windowing/WindowOwnershipManager.cs
  Views/InvocationPopupWindow.xaml + .cs
  Views/SubscriptionPopupWindow.xaml + .cs
  Views/DiagnosticsWindow.xaml + .cs
  Views/PropertiesWindow.xaml + .cs
  Controls/                         # custom controls if needed
  Converters/                       # IValueConverter implementations

tests/ohSpy.Core.Tests/
  # Mirror-tree of src/ohSpy.Core/
  Collections/, Diagnostics/, Devices/, Discovery/, Events/, Http/, Scpd/, ViewModels/
  Fakes/                            # TestHttpMessageHandler, FakeUpnpDevice, InlineUiDispatcher
  Fixtures/                         # canned XML: SCPDs, descriptions, NOTIFY payloads, SSDP datagrams

installer/
  ohSpy.iss                         # InnoSetup script (D12)
  out/                              # .gitignored
```

#### 4. MVVM file-naming convention

- `XxxViewModel` — full word, not `Vm` abbreviation. PascalCase. One VM per file. File name matches type name.
- `partial class` only when source-gen requires (`[ObservableObject]`, `[ObservableProperty]`).
- Source-gen attributes from `CommunityToolkit.Mvvm`: `[ObservableProperty]`, `[RelayCommand]`, `[NotifyCanExecuteChangedFor]`.
- Base class: `ObservableObject` from CommunityToolkit unless a specific VM needs something else.
- VMs wrapping a domain entity name themselves after the entity: `DeviceNodeViewModel` wraps `RegistryEntry`; `ActionNodeViewModel` wraps `ScpdAction`.

```csharp
public partial class DeviceNodeViewModel : ObservableObject
{
    private readonly RegistryEntry _entry;

    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private async Task FetchXmlAsync() { ... }
}
```

**Anti-pattern:** `DeviceVM.cs`, multiple VMs per file, logic in `MainWindow.xaml.cs` instead of `ShellViewModel`.

#### 5. Test layout

- Mirror-tree: `src/ohSpy.Core/Http/UpnpHttpClient.cs` → `tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs`.
- One test class per production class. Split into multiple only when one production class exceeds ~30 tests.
- `Fakes/` (test doubles) and `Fixtures/` (canned data) are shared across the project.
- Integration tests needing port binding or filesystem use `[Collection("Integration")]` to serialise.

**Anti-pattern:** `tests/HttpTests.cs` flat file mixing tests for 5 different production types; co-located `*.test.cs` files inside `src/`.

### Code Patterns

#### 6. Async / await discipline

- All I/O is async. No `Task.Run` to fake async over sync.
- `ConfigureAwait(false)` on every `await` in **`Core`** (library convention — context capture unnecessary). Omit in **`App`** (UI consumer; context capture is desired).
- **Banned:** `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`. Enforced by `Microsoft.VisualStudio.Threading.Analyzers` build-time lint.
- Every async method ends in `Async`. Every public async method takes `CancellationToken ct` as the **last** parameter — named per Decision 7 convention when ambiguity (`deviceToken`, `popupToken`).
- `await foreach` for `IAsyncEnumerable` consumption.
- `async void` only on event handlers (UI eventing requires it). Async-void bodies MUST wrap in `try/catch` that emits a `Warning` diagnostic — uncaught async-void exceptions terminate the process.

**Anti-pattern:** `var result = SomeAsync().Result;`, `Task.Run(() => SyncMethod())`.

#### 7. DI composition root + lifetime

- Composition in `src/ohSpy.App/Composition/ServiceRegistration.cs` as `internal static class` exposing one `IServiceCollection RegisterServices(this IServiceCollection)` extension.
- Built once at app startup. One `IServiceProvider` for app lifetime.
- **Default lifetime: singleton.** Single-process, single-user, no per-request scopes. Anything in DI is singleton unless explicitly documented otherwise.
- Per-entity types (per-device VM, per-popup VM) are **not** registered in DI. They're constructed by their parent via a factory — typically `Func<TArgs, TViewModel>` registered in DI, or a typed factory interface.

```csharp
internal static class ServiceRegistration
{
    public static IServiceCollection RegisterServices(this IServiceCollection s)
    {
        s.Configure<HttpTimeoutOptions>(_ => { /* defaults */ });
        s.AddSingleton<IUiDispatcher, WinUiDispatcher>();
        s.AddSingleton<IUpnpHttpClient, UpnpHttpClient>();
        s.AddSingleton<ISsdpTransport, SsdpTransport>();
        s.AddSingleton<IDeviceRegistry, DeviceRegistry>();
        s.AddSingleton<EagerDescriptionDispatcher>();
        s.AddSingleton<IDiagnosticEmitter, DiagnosticEmitter>();
        s.AddSingleton<IWindowOwnershipManager, WindowOwnershipManager>();
        // ...
        return s;
    }
}
```

**Anti-pattern:** `AddTransient<T>` for service-layer types (no per-request lifecycle); injecting `IServiceProvider` into services (service locator).

#### 8. Constructor patterns

- **Primary constructors** (C# 12+) for types whose constructor is straightforward DI:

  ```csharp
  internal sealed class DiagnosticEmitter(
      ILogger<DiagnosticEmitter> logger,
      IDiagnosticRingSink ring,
      IDiagnosticFileSink file) : IDiagnosticEmitter
  { ... }
  ```

- **Traditional constructors** when non-trivial init (assigning derived fields, hooking events, computing initial state).
- Underscore prefix for private fields in traditional constructors:

  ```csharp
  internal sealed class EagerDescriptionDispatcher
  {
      private readonly IUpnpHttpClient _http;
      private readonly IUiDispatcher _dispatcher;
      private readonly SemaphoreSlim _semaphore = new(8, 8);

      public EagerDescriptionDispatcher(IUpnpHttpClient http, IUiDispatcher dispatcher)
      {
          _http = http;
          _dispatcher = dispatcher;
      }
  }
  ```

- Primary-constructor parameters: plain camelCase (no underscore — they ARE the parameter names).

#### 9. Records vs classes

- **`public sealed record`** — immutable data carriers (`ScpdAction`, `DeviceDescription`, `DiagnosticEntry`, `SsdpDatagram`, `NotifyRequest`, `SoapRequest`, `SoapResponse`).
- **`public class`** or **`internal sealed class`** — mutable entities with lifecycle (`RegistryEntry`, every VM, every service).
- **`sealed` by default** on records and classes. Explicit opt-in for inheritance — `UpnpException` family is the rare exception.
- **`readonly record struct`** — small value types travelling by value (`DiagnosticContext`, `ScpdAllowedValueRange`).
- **`enum`** — closed sets of named constants (`DescriptionFetchState`, `SsdpSource`, `DiagSeverity`, `ScpdDirection`).

**Anti-pattern:** manually overriding `Equals` on a data class instead of using a record.

#### 10. Exception conventions

- Typed exception hierarchy from Decision 3.
- `catch` blocks catch the **narrowest** type they can act on:

  ```csharp
  try { await _http.InvokeActionAsync(req, ct); }
  catch (UpnpFaultException fault)     { ShowFault(fault); }
  catch (UpnpTimeoutException)         { ShowTimeoutMessage(); }
  catch (UpnpTransportException)       { ShowConnectionError(); }
  ```

- Bare `catch (Exception)` allowed ONLY in three places:
  - async-void event handler outer body (diagnostic-emit + swallow).
  - `EagerDescriptionDispatcher.FetchAsync` outermost `catch` (exception → `MarkFailed` mapping; Decision 9).
  - `DiagnosticFileSink` background drain loop (don't let one bad write kill the pump).
- Re-throw with `throw;` (preserves stack), not `throw ex;`.
- Never swallow silently — always emit a diagnostic at minimum.

**Anti-pattern:** `try { … } catch { }`; `catch (Exception ex) { /* nothing */ }`.

### Diagnostics & Logging

#### 11. `DiagnosticContext` discipline

Mandatory fields per category family (downstream agents follow this table; deviations require an architecture amendment):

| Category prefix | Mandatory `DiagnosticContext` fields |
|---|---|
| `Http.*` | `Url`; `Elapsed` if budget was relevant; `StatusCode` for HTTP error status |
| `Description.Fetch.*` | `DeviceUuid`, `Url` |
| `Scpd.*` | `DeviceUuid`, `Url` |
| `Soap.*` | `DeviceUuid`, `Url`, `ActionName` |
| `Gena.Subscribe.*` / `Gena.Unsubscribe.*` / `Gena.Renew.*` | `DeviceUuid`, `Sid` (when known), `Url` |
| `Gena.Callback.*` | `RemoteEndpoint` (`DeviceUuid` not yet known) |
| `Gena.Notify.Received` | `Sid` |
| `Ssdp.Parse` | `RemoteEndpoint` |
| `Ssdp.Channel.*` | (none beyond message) |
| `Adapter.Switch.*` | (none beyond message) |
| `Diagnostics.FileSink.*` | (none beyond message) |

These are documented as XML doc comments on each `DiagCategories` constant.

**Anti-pattern:** `_diag.Warning(DiagCategories.HttpTimeout, $"timeout after {elapsed} for {url}")` — pass `Elapsed` and `Url` via `DiagnosticContext`, not message interpolation.

#### 12. Message-field grammar

- Sentence case.
- Terse / telegram-style. Fragment OK if it suffices.
- ASCII only. (Friendly names in `Context` may contain unicode — that's fine; the message itself stays ASCII.)
- No trailing punctuation.

| Good | Bad |
|---|---|
| `"timeout exceeded"` | `"Description fetch timeout exceeded! The device at <URL> did not respond. 🚨"` |
| `"declared root UDN mismatch"` | `"The fetched device description's <UDN> element does not match the UUID that ohSpy requested it for."` |
| `"chunked transfer-encoding not supported"` | `"We don't support chunked encoding!!"` |

### XAML / UI

#### 13. XAML conventions

- **`x:Bind` preferred over `Binding`.** `x:Bind` is compile-time-checked and faster. Use `Binding` only when reflection / DataContext-late-resolution is unavoidable.
- Code-behind `*.xaml.cs` is constructor-only — `InitializeComponent()` plus DI-injected `DataContext` assignment. All logic in the VM.
- Resource keys: PascalCase (`MutedForegroundBrush`, `KindGlyphFontFamily`).
- DataTemplates use `x:DataType` for compile-time binding:

  ```xml
  <DataTemplate x:DataType="vm:DeviceNodeViewModel">
      <StackPanel Orientation="Horizontal">
          <FontIcon Glyph="{x:Bind KindGlyph}"/>
          <TextBlock Text="{x:Bind FriendlyName}"/>
      </StackPanel>
  </DataTemplate>
  ```

- Layout: prefer `Grid` over nested `StackPanel`. Use named `RowDefinitions` / `ColumnDefinitions` when referenced from code.
- One `Window` / `Page` per XAML file.

**Anti-pattern:** `Click="OnButtonClicked"` event handlers in code-behind — use `[RelayCommand]` in the VM + `Command="{x:Bind ClickCommand}"`.

### Test Patterns

#### 14. xUnit test naming

- Pattern: `MethodUnderTest_Scenario_ExpectedOutcome`.
- Test class: `{TypeUnderTest}Tests`.
- `[Fact]` for parameterless; `[Theory]` + `[InlineData]` for parameterised.
- Traits for categorisation:
  - `[Trait("category", "integration")]` — needs port binding, real filesystem, or shared singleton state.
  - `[Trait("category", "chaos")]` — Murat's 20-device mixed-behaviour test and similar.
  - `[Trait("category", "soak")]` — extended wall-clock (nightly).

Examples:

```csharp
[Fact] public Task FetchScpdAsync_HangsAfter200Ok_ThrowsUpnpTimeoutException() { ... }
[Fact] public void PrependNewest_AtCapacity_EmitsAddAndRemoveNotifications() { ... }
[Fact] public void MarkLoaded_FromPendingState_ThrowsInvalidOperationException() { ... }
```

**Anti-pattern:** `Test1`, `It_should_work`, behaviour-style names that obscure which method is being tested.

#### 15. AC traceability

- Each architectural / story AC gets one focused test (or one explicit cluster).
- Test name embeds the AC ID where it reads cleanly:

  ```csharp
  [Fact] public Task FetchScpdAsync_BodyExceeds2Mb_ThrowsUpnpProtocolException_AC34() { ... }   // AC-3.4
  [Fact] public void PrependNewest_HundredKOps_NoResetNotifications_AC62() { ... }              // AC-6.2
  ```

- When an AC needs multiple tests, prefix all with the AC ID:

  ```csharp
  [Fact] public Task AC74_PopupCloseCancelsInFlightInvocation() { ... }
  [Fact] public Task AC74_PopupCloseDisposesPopupCts() { ... }
  ```

- Story implementers verify their AC group via:

  ```powershell
  dotnet test --filter "FullyQualifiedName~AC34"
  ```

**Anti-pattern:** AC numbers buried in a comment in the test body instead of embedded in the test name.

### Baseline Tooling

#### `.editorconfig`

Use the standard .NET defaults from `dotnet new editorconfig` as a baseline. Spaces (4-space indent), CRLF on Windows, UTF-8 without BOM, modern C# defaults enabled. No project-specific overrides expected in v1.

### Enforcement Summary

| Rule | Mechanism |
|---|---|
| Async discipline (`.Result`/`.Wait()` banned, rule 6) | `Microsoft.VisualStudio.Threading.Analyzers` build-time lint |
| `Core` / `App` boundary (rule 2) | `Directory.Build.props` package-reference restrictions, plus NetArchTest architecture test |
| Category constants used (rule 11, Decision 8) | NetArchTest rule (open follow-up — design in implementation phase) |
| All other rules | Code review against this section as the citable rulebook |

### Open Follow-ups

- **NetArchTest project** in `tests/ohSpy.Core.Tests` enforcing rules 2 and 11; gets stood up as part of the test-infrastructure story.
- **Roslyn analyzer or `.editorconfig` rules** for any pattern that turns out to be commonly violated during implementation — added reactively, not speculatively.

## Project Structure & Boundaries

The 12 decisions name components; the 15 patterns name conventions. This section is the file-level inventory + the FR → component mapping + the integration-point wiring diagram. Downstream agents start a story here when they need to know "where does this code live".

### Complete Project Tree

```
ohSpy/
├── README.md                              # what it is, how to build, where to find the spec trail
├── LICENSE
├── .gitignore                             # standard .NET + installer/out/ + _bmad-output/*.tmp
├── .editorconfig                          # `dotnet new editorconfig` baseline
├── Directory.Build.props                  # solution-wide MSBuild: TF, LangVersion, package-ref boundary rules
├── global.json                            # pin .NET SDK 10.0.x
├── ohSpy.sln
│
├── src/
│   ├── ohSpy.App/
│   │   ├── ohSpy.App.csproj               # WindowsPackageType=None, SelfContained=true, RuntimeIdentifiers=win-x64;win-arm64
│   │   ├── App.xaml + App.xaml.cs         # App-level resources; DI provider creation; MainWindow construction
│   │   ├── MainWindow.xaml + .cs          # FR-001 two-pane shell; ctor-only code-behind
│   │   ├── Program.cs                     # Bootstrap.TryInitialize → Application.Start (Decision 12)
│   │   ├── app.manifest                   # DPI awareness, minimum Windows version
│   │   ├── Assets/
│   │   │   ├── ohSpy.ico
│   │   │   └── ohSpy-44x44.png
│   │   ├── Composition/
│   │   │   └── ServiceRegistration.cs     # DI composition root (Pattern 7)
│   │   ├── Windowing/
│   │   │   ├── WinUiDispatcher.cs         # IUiDispatcher impl (Decision 1)
│   │   │   └── WindowOwnershipManager.cs  # IWindowOwnershipManager impl (Decision 10)
│   │   ├── Views/
│   │   │   ├── InvocationPopupWindow.xaml + .cs        # FR-025
│   │   │   ├── SubscriptionPopupWindow.xaml + .cs      # FR-032
│   │   │   ├── DiagnosticsWindow.xaml + .cs            # FR-041
│   │   │   └── PropertiesWindow.xaml + .cs             # FR-052
│   │   ├── Converters/
│   │   │   ├── BoolToVisibilityConverter.cs
│   │   │   ├── SeverityToBrushConverter.cs
│   │   │   └── NodeKindToGlyphConverter.cs             # FR-045
│   │   ├── Styles/                                     # XAML resource dictionaries
│   │   └── Properties/
│   │       ├── PublishProfiles/win-x64.pubxml, win-arm64.pubxml
│   │       └── launchSettings.json
│   │
│   └── ohSpy.Core/
│       ├── ohSpy.Core.csproj              # TargetFramework=net10.0; no -windows TFM
│       ├── Collections/
│       │   ├── BoundedObservableCollection.cs          # Decision 6
│       │   └── IdentityKeyedSortedCollection.cs        # Decision 6
│       ├── Diagnostics/
│       │   ├── DiagSeverity.cs
│       │   ├── DiagCategories.cs                       # Decision 8 — single source of truth
│       │   ├── DiagnosticEntry.cs
│       │   ├── DiagnosticContext.cs
│       │   ├── DiagnosticOptions.cs
│       │   ├── IDiagnosticEmitter.cs / DiagnosticEmitter.cs
│       │   ├── IDiagnosticRingSink.cs / DiagnosticRingSink.cs
│       │   ├── IDiagnosticFileSink.cs / DiagnosticFileSink.cs   # Patched A14: impl lives in Core
│       │   └── DiagnosticRow.cs                        # resolved Identity / Endpoint (FR-041)
│       ├── Discovery/
│       │   ├── ISsdpTransport.cs / SsdpTransport.cs    # Decision 2
│       │   ├── SsdpParser.cs                           # SsdpDatagram → SsdpAnnouncement
│       │   ├── SsdpAnnouncement.cs                     # NT, NTS, USN, LOCATION, CACHE-CONTROL, SERVER, BOOTID, CONFIGID
│       │   ├── DiscoveryService.cs                     # channel consumer; routes to registry
│       │   └── NetworkAdapterEnumerator.cs             # FR-048 eligible-adapter list
│       ├── Devices/
│       │   ├── DescriptionFetchState.cs                # Decision 9 enum
│       │   ├── RegistryEntry.cs                        # Decision 9
│       │   ├── IDeviceRegistry.cs / DeviceRegistry.cs  # Decision 9
│       │   └── EagerDescriptionDispatcher.cs           # Decisions 3 + 9 canonical FetchAsync
│       ├── Events/
│       │   ├── IEventCallbackHost.cs / EventCallbackHost.cs   # Decision 4
│       │   ├── HttpRequestParser.cs                    # Decision 4 hand-rolled HTTP/1.1
│       │   ├── TimeoutStream.cs                        # Decision 4 per-connection stream wrapper
│       │   ├── NotifyRequest.cs                        # Decision 4 record
│       │   ├── SubscriptionClient.cs                   # SUBSCRIBE / RENEW / UNSUBSCRIBE orchestration
│       │   └── EventNotification.cs                    # for subscription-popup event list
│       ├── Http/
│       │   ├── IUpnpHttpClient.cs / UpnpHttpClient.cs  # Decision 3
│       │   ├── UpnpExceptions.cs                       # UpnpException + 4 derivatives
│       │   └── HttpTimeoutOptions.cs                   # Decision 11
│       ├── Models/
│       │   ├── DeviceDescription.cs / ServiceDescription.cs
│       │   ├── ScpdAction.cs / ScpdArgument.cs / ScpdDirection.cs
│       │   ├── ScpdStateTable.cs / ScpdStateVariable.cs / ScpdAllowedValueRange.cs
│       │   ├── SsdpDatagram.cs / SsdpSource.cs / SsdpLogEntry.cs
│       │   └── SoapRequest.cs / SoapResponse.cs
│       ├── Scpd/
│       │   ├── IScpdParser.cs / XmlReaderScpdParser.cs # Decision 5
│       │   └── IDeviceDescriptionParser.cs / DeviceDescriptionParser.cs
│       ├── Soap/
│       │   ├── SoapEnvelopeBuilder.cs                  # outbound SOAP for InvokeActionAsync
│       │   └── SoapFaultParser.cs                      # UPnPError extraction
│       ├── Threading/
│       │   └── IUiDispatcher.cs                        # Decision 1 interface — impl in App
│       └── ViewModels/
│           ├── ShellViewModel.cs                       # main-window orchestration; rescan; adapter switch
│           ├── DeviceTreeViewModel.cs                  # FR-002 top-level tree
│           ├── DeviceNodeViewModel.cs / ServiceNodeViewModel.cs / ActionNodeViewModel.cs
│           ├── SsdpLogViewModel.cs                     # FR-003 right pane
│           ├── InvocationPopupViewModel.cs / ArgumentInputViewModel.cs  # FR-025, FR-026, FR-102, FR-103
│           ├── SubscriptionPopupViewModel.cs           # FR-032
│           ├── DiagnosticsViewModel.cs                 # FR-041
│           └── PropertiesViewModel.cs                  # FR-052
│
├── tests/
│   └── ohSpy.Core.Tests/
│       ├── ohSpy.Core.Tests.csproj
│       │   # Mirror-tree of src/ohSpy.Core/ (Pattern 5):
│       ├── Collections/, Diagnostics/, Discovery/, Devices/, Events/, Http/, Scpd/, ViewModels/
│       ├── Architecture/                               # NetArchTest rules — Patterns 2, 11
│       │   ├── CoreAppBoundaryTests.cs
│       │   ├── AsyncDisciplineTests.cs
│       │   └── DiagCategoriesUsageTests.cs
│       ├── Fakes/
│       │   ├── TestHttpMessageHandler.cs               # for UpnpHttpClient unit tests
│       │   ├── FakeUpnpDevice.cs                       # in-process Kestrel test server (party-mode deliverable)
│       │   ├── FakeUpnpDeviceBehavior.cs               # HangBeforeHeaders, HangAfter200Ok, SlowDripBody, GiantScpd, ChunkedThenAbort, FaultResponse, WrongContentLength
│       │   ├── FakeSsdpTransport.cs
│       │   └── InlineUiDispatcher.cs                   # IUiDispatcher synchronous fake (Decision 1)
│       └── Fixtures/
│           ├── Scpds/                                  # linn-ds-5action.xml, dlna-renderer-30action.xml,
│           │                                           #  igd-router-200action.xml, malformed-mid-document.xml, xxe-attempt.xml
│           ├── DeviceDescriptions/                     # linn-ds.xml, dlna-renderer.xml, igd-router.xml
│           ├── NotifyPayloads/                         # linn-volume-update.xml, dlna-transport-state.xml
│           └── SsdpDatagrams/                          # alive-linn-ds.txt, byebye.txt, malformed.txt
│
├── installer/
│   ├── ohSpy.iss                          # Decision 12 InnoSetup script
│   └── out/                               # .gitignored
│
├── docs/
│   ├── ARCHITECTURE.md                    # short — points to planning-artifacts/architectures/.../architecture.md
│   └── DEVELOPMENT.md                     # short — build / test / package commands
│
└── _bmad-output/                          # planning-artifacts trail (already present)
    └── planning-artifacts/
        ├── briefs/brief-ohSpy-2026-05-29/
        ├── prds/prd-ohSpy-2026-05-30/
        └── architectures/arch-ohSpy-2026-05-31/
```

### FR-Category → Component Mapping

| FR group | Primary files |
|---|---|
| **4.1 Discovery & Registry** (FR-004..008, 053, 054) | `Discovery/SsdpTransport.cs`, `Discovery/SsdpParser.cs`, `Discovery/DiscoveryService.cs`, `Devices/DeviceRegistry.cs`, `ViewModels/DeviceTreeViewModel.cs` |
| **4.2 Eager description fetch** (FR-043, 047) | `Devices/EagerDescriptionDispatcher.cs`, `Devices/RegistryEntry.cs`, `Http/UpnpHttpClient.FetchDeviceDescriptionAsync`, `Scpd/DeviceDescriptionParser.cs` |
| **4.3 Device tree** (FR-001, 002, 009..013, 044, 045, 051) | `ViewModels/DeviceTreeViewModel.cs`, `ViewModels/DeviceNodeViewModel.cs`, `MainWindow.xaml`, `Converters/NodeKindToGlyphConverter.cs` |
| **4.4 Lazy SCPD** (FR-012, 100) | `Http/UpnpHttpClient.FetchScpdAsync`, `Scpd/XmlReaderScpdParser.cs`, `ViewModels/ServiceNodeViewModel.cs` |
| **4.5 SSDP log** (FR-003, 014..016, 055, 101) | `ViewModels/SsdpLogViewModel.cs`, `Collections/BoundedObservableCollection<SsdpLogEntry>`, `MainWindow.xaml` (right pane) |
| **4.6 XML viewing** (FR-017..020) | `ViewModels/DeviceNodeViewModel.FetchXmlCommand`, `ViewModels/ServiceNodeViewModel.FetchXmlCommand`, OS shell-open via `System.Diagnostics.Process.Start` |
| **4.7 Device Properties** (FR-052) | `Views/PropertiesWindow.xaml`, `ViewModels/PropertiesViewModel.cs` |
| **4.8 Rescan** (FR-021..024) | `ViewModels/ShellViewModel.RescanCommand`, `Discovery/DiscoveryService.RescanAsync`, `Devices/DeviceRegistry.PruneNonResponders` |
| **4.9 Action invocation** (FR-025..031, 102, 103) | `Views/InvocationPopupWindow.xaml`, `ViewModels/InvocationPopupViewModel.cs`, `ViewModels/ArgumentInputViewModel.cs`, `Soap/SoapEnvelopeBuilder.cs`, `Http/UpnpHttpClient.InvokeActionAsync` |
| **4.10 GENA subscription** (FR-032..038, 104) | `Views/SubscriptionPopupWindow.xaml`, `ViewModels/SubscriptionPopupViewModel.cs`, `Events/SubscriptionClient.cs`, `Events/EventCallbackHost.cs`, `Events/HttpRequestParser.cs` |
| **4.11 Adapter selection** (FR-048..050) | `ViewModels/ShellViewModel.AdapterSelectionCommand`, `Discovery/NetworkAdapterEnumerator.cs`, atomic-rebind sequence in `ShellViewModel.SwitchAdapterAsync` |
| **4.12 Diagnostics** (FR-039..042) | `Diagnostics/IDiagnosticEmitter.cs`, `Diagnostics/DiagnosticRingSink.cs`, `Diagnostics/DiagnosticFileSink.cs` (Core; Patched A14), `Views/DiagnosticsWindow.xaml`, `ViewModels/DiagnosticsViewModel.cs` |
| **4.13 Secondary window lifecycle** (FR-037, 046) | `Windowing/WindowOwnershipManager.cs`, popup VM disposal patterns, registry `DeviceRemoved` handling per popup VM |

### Architectural Boundaries

**Project boundary:**

- `ohSpy.Core` (class library, `net10.0`): protocol code, VMs, services, models, primitives. UI-agnostic. Unit-testable without WinUI runtime.
- `ohSpy.App` (WinExe, `net10.0-windows10.0.19041.0`): App composition, Views, XAML, WinUI dispatcher impl, Win32 interop. References `ohSpy.Core`.
- `ohSpy.Core.Tests`: references `ohSpy.Core` only. Never references `ohSpy.App`.
- Enforcement: `Directory.Build.props` package-reference restrictions + NetArchTest rules in `tests/.../Architecture/CoreAppBoundaryTests.cs`.

**Layer boundary (within `Core`):**

```
ViewModels/              ← depend on Services, Models, Threading, Diagnostics
  ↓
Services (Discovery, Devices, Events, Http, Scpd, Soap)
  ↓                      ← depend on Models, Diagnostics, Threading, Collections, lower-level services
Models/, Collections/, Threading/, Diagnostics/    ← leaves; no inter-dependencies
```

Rules:
- Higher layers depend on lower layers. Lower layers never depend on higher.
- Services depend on services through interfaces (`IXxx`), never concrete types.
- Models are pure data; no behaviour beyond constructors and value-equality.
- `IUiDispatcher` is the only mechanism for cross-thread mutation; injected via DI.

**Process boundary:**

- Single OS process. No IPC. `EventCallbackHost` accepts TCP but the host itself is in-process; callers happen to be on the network.

### Integration Points (the wiring diagram)

**1. SSDP datagram flow:**

```
network → SsdpTransport.{Multicast, SearchResponse} sockets
        → Channel<SsdpDatagram>(4096, DropOldest)
        → DiscoveryService (single reader)
        → SsdpParser → SsdpAnnouncement
        → (a) new UUID → DeviceRegistry.Add(new RegistryEntry) → EagerDescriptionDispatcher.Schedule
          (b) known UUID alive → entry.RefreshSsdpMetadata (no re-fetch — FR-043)
          (c) known UUID byebye → DeviceRegistry.Remove(uuid) → entry._deviceCts.Cancel()
        → SsdpLogViewModel.Entries.PrependNewest(SsdpLogEntry) via IUiDispatcher (FR-014, FR-015)
```

**2. Eager description fetch flow (Decision 9 canonical):**

```
EagerDescriptionDispatcher.FetchAsync(entry)
  └─ semaphore.WaitAsync(entry.DeviceToken)
  └─ _dispatcher.Post(() => entry.MarkInFlight())
  └─ IUpnpHttpClient.FetchDeviceDescriptionAsync(entry.LocationUrl, entry.DeviceToken)
      └─ on success → DeviceDescriptionParser.Parse(bytes)
          └─ mismatched root → _dispatcher.Post(() => DeviceRegistry.Remove(uuid)) + Information diagnostic
          └─ matched root → _dispatcher.Post(() => { entry.MarkLoaded(desc); DeviceRegistry.RaiseDeviceLoaded(entry) })
      └─ on cancellation (deviceToken) → no transition; entry being removed by registry path
      └─ on exception → Warning diagnostic + _dispatcher.Post(() => entry.MarkFailed(...))
  └─ semaphore.Release()
```

**3. Registry-event → VM flow (via `IUiDispatcher`):**

```
DeviceRegistry.DeviceLoaded(RegistryEntry)
  → DeviceTreeViewModel.OnDeviceLoaded(entry)
  → IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>.Add(new DeviceNodeViewModel(entry))
  → INotifyCollectionChanged.Add(index)
  → WinUI TreeView shows new row (FR-005 + FR-047)

DeviceRegistry.DeviceUpdated(RegistryEntry)          (e.g. re-announce with different friendly name)
  → DeviceTreeViewModel.OnDeviceUpdated(entry)
  → IdentityKeyedSortedCollection.Update(existingDeviceNodeViewModel)
  → if sort key changed → Move(old, new) → row migrates in-place; expansion/selection preserved (FR-054)

DeviceRegistry.DeviceRemoved(Guid uuid)
  → DeviceTreeViewModel.OnDeviceRemoved(uuid) → IdentityKeyedSortedCollection.Remove(uuid)
  → every open popup with matching UUID → FR-037 transition to "device gone" UI state
```

**4. HTTP fetch flow (single shared `HttpClient`, Decision 3):**

```
IUpnpHttpClient.SomethingAsync(externalToken)
  └─ linked CTS = CTS.Link(externalToken, new CTS(_opts.SomeTimeout))
  └─ HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
  └─ size cap check (Content-Length vs _opts.MaxResponseBytes)
  └─ ReadAsStringAsync(linked.Token)              ← token threaded through body read
  └─ Parse<T>(body) → return
  └─ catch arms → typed exception + Warning diagnostic with Url + Elapsed + Budget
```

**5. GENA callback flow:**

```
device → TCP (adapter_ipv4, ephemeral_port)
       → EventCallbackHost.Listener accepts connection
       → TimeoutStream wrap (Decision 4 headers/body budgets)
       → HttpRequestParser (Decision 4 strict-framing / lenient-headers)
       → on success → NotifyRequest record raised on IEventCallbackHost.NotifyReceived
                    → SubscriptionClient routes by SID to matching SubscriptionPopupViewModel
                    → VM parses <e:propertyset> off the UI thread (FR-104 non-serial)
                    → VM.Events.PrependNewest(EventNotification) via IUiDispatcher
                    → VM.LatestPropertyValues[name] = newValue (overwrite-in-place observable map)
       → on malformed → HTTP 400/411/413 to device + Warning diagnostic
```

**6. Diagnostic emit fan-out (Decision 8):**

```
service.Catch (Exception ex)
  → _diag.Warning(DiagCategories.SomeCategory, "msg", new DiagnosticContext { ... })
  → DiagnosticEmitter routes simultaneously to:
      ├─ ILogger.LogWarning (MEL pipeline — dotnet-trace etc.)
      ├─ DiagnosticRingSink.Push → _dispatcher.Post → Entries.PrependNewest
      │                          → DiagnosticsViewModel sees live update (FR-041)
      └─ DiagnosticFileSink.Push → Channel<DiagnosticEntry>
                                 → background pump → JSON-lines file (%LOCALAPPDATA%)
```

**7. Cancellation hierarchy (Decision 7):**

```
_appCts (App)
  └─ _adapterCts = linked(_appCts.Token)               (per AdapterScope)
      ├─ SsdpTransport(adapterToken)
      ├─ EventCallbackHost(adapterToken)
      └─ per RegistryEntry:
          └─ _deviceCts = linked(adapterToken)         (per RegistryEntry)
              ├─ EagerDescriptionDispatcher fetch tasks use deviceToken
              └─ per open popup:
                  └─ _popupCts = linked(deviceToken)   (per popup)
                      └─ invocation / subscription tasks use popupToken

Per-operation: linked(externalToken, timeoutCts.Token) inside every IUpnpHttpClient method.
Cleanup ops (UNSUBSCRIBE on close): use the level-above token, NOT the cancelled scope's token.
```

**8. Window ownership (Decision 10):**

```
ShellViewModel.OpenXxxPopupCommand
  → new XxxPopupWindow(injectedVm)
  → window.Activate()
  → _windowOwnership.Adopt(window, _shellWindow)       (SetWindowLongPtr(GWLP_HWNDPARENT))
  → FR-046 z-order + minimise/restore + close-with-parent now OS-delivered
```

### File Organization Patterns

**Configuration files (repo root):**

- `Directory.Build.props` — solution-wide MSBuild defaults (TargetFramework lookups, LangVersion 13, Nullable enable, ImplicitUsings enable, package-reference boundary rules for `ohSpy.Core`).
- `global.json` — pin .NET SDK channel to 10.0.x.
- `ohSpy.sln` — three projects: `ohSpy.App`, `ohSpy.Core`, `ohSpy.Core.Tests`.
- `.editorconfig` — `dotnet new editorconfig` defaults.
- `.gitignore` — standard .NET (`bin/`, `obj/`) + `installer/out/` + `_bmad-output/*.tmp`.

**Source organisation:** one concept per file; co-locate related types in the same folder (e.g. `RegistryEntry.cs`, `IDeviceRegistry.cs`, `DeviceRegistry.cs`, `EagerDescriptionDispatcher.cs`, `DescriptionFetchState.cs` all in `Devices/`).

**Test organisation:** mirror-tree (Pattern 5). One test class per production class. `Fakes/` and `Fixtures/` shared. Architecture tests in `Architecture/` subfolder for NetArchTest rules (Patterns 2, 11).

**Asset organisation:** `Assets/` in `ohSpy.App` for icons/images shipped with the app. `installer/` at repo root for InnoSetup script + build output.

**Diagnostic logs:** `%LOCALAPPDATA%\ohSpy\diagnostics\` (NOT in the install dir). Survive uninstall (Decision 12). Out of the repo.

### Development Workflow

**Inner dev loop:**

```powershell
dotnet build                                # whole solution
dotnet test                                 # all xUnit suites
dotnet run --project src/ohSpy.App          # launch app for manual testing
```

**Publish + package:**

```powershell
dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained
dotnet build -t:BuildInstaller -p:Configuration=Release   # invokes InnoSetup; output to installer/out/
```

**Test filtering:**

```powershell
dotnet test --filter "category!=chaos&category!=soak"     # quick: unit + integration only
dotnet test --filter "category=chaos"                     # Murat-style mixed-behaviour drill
dotnet test --filter "FullyQualifiedName~AC34"            # all AC-3.4 tests (Pattern 15)
```

## Amendments from Validation Review (Party-Mode Sign-Off)

The party-mode sign-off (Winston / Amelia / Murat) surfaced six concrete preconditions to lift confidence from MEDIUM-HIGH to HIGH. All six are applied inline below — five amendments plus one new Decision (D13).

### Amendment A1 — "Loading…" Placeholder VM Contract (FR-044)

WinUI's `TreeView` only renders the expand chevron when a node has at least one child. Every `DeviceNodeViewModel` and `ServiceNodeViewModel` carries a placeholder child from construction; it is *replaced atomically* when real children load. This is a D6 + D9 + D5 integration point — Winston flagged that the placeholder is a *collection-mutation pattern*, not a cosmetic VM concern.

**Contract:**

```csharp
public sealed class LoadingPlaceholderViewModel : INodeViewModel
{
    public string Label => "Loading…";
    public NodeKind Kind => NodeKind.Placeholder;        // FR-045 — no glyph (the row is a placeholder)
    // Implements INodeViewModel just enough for the WinUI template to render it as a child.
}

public sealed class InlineErrorViewModel : INodeViewModel
{
    public string Label { get; }                          // FR-013 error text
    public NodeKind Kind => NodeKind.Error;
    public InlineErrorViewModel(string message) { Label = message; }
}
```

**Rules:**

- `DeviceNodeViewModel.Children` is initialised in its constructor to `[ new LoadingPlaceholderViewModel() ]`.
- `ServiceNodeViewModel.Children` is initialised the same way.
- `ActionNodeViewModel` has NO children and NO placeholder — actions are leaves; the WinUI template MUST NOT render an expand chevron for `ActionNodeViewModel` instances (FR-044 second consequence).
- **Atomic replacement on real-children load:** the VM exposes a single `ReplaceWith(IReadOnlyList<INodeViewModel> realChildren)` method that swaps the entire collection in one operation under the dispatcher. Implementation: clear + add-range emits a single `NotifyCollectionChangedAction.Reset` — acceptable here because the placeholder is the *only* item being replaced and the tree node is currently expanding (the framework's "redraw all" reaction operates on an empty visible set).
- **NEVER** remove-then-add as two operations — that emits two notifications and collapses the chevron mid-expand, violating NFR-UI3 (no flicker on incremental updates).
- **On fetch failure** (description fetch or SCPD fetch): `ReplaceWith([ new InlineErrorViewModel(message) ])`. Same atomic-replacement rule.
- **On fetch cancellation** (adapter switch / byebye / device-CTS cancel): VM is being torn down via parent `IdentityKeyedSortedCollection.Remove`; no replacement is issued; the entire node is dropped from the tree.

**Acceptance criteria:**

- **AC-A1.1** `DeviceNodeViewModel` constructor produces `Children` containing exactly one `LoadingPlaceholderViewModel`.
- **AC-A1.2** `ServiceNodeViewModel` constructor produces `Children` containing exactly one `LoadingPlaceholderViewModel`.
- **AC-A1.3** `ActionNodeViewModel.Children` is empty; XAML template does not render an expand chevron for an `ActionNodeViewModel` (manual UI verification).
- **AC-A1.4** `ReplaceWith(realChildren)` emits a single `INotifyCollectionChanged` notification; the bound `TreeView` does NOT collapse the chevron during the swap (manual UI test).
- **AC-A1.5** Description / SCPD fetch failure → `ReplaceWith([new InlineErrorViewModel(message)])`; FR-013 error placeholder visible inline.

### Amendment A2 — AC Trait Shape (Pattern 15 refinement)

Every test satisfying a numbered AC carries `[Trait("ac", "AC-3.4")]`. Trait name lowercase (xUnit convention); value uppercase with the `AC-` prefix matching architecture numbering.

```csharp
[Fact]
[Trait("ac", "AC-3.4")]
public Task FetchScpdAsync_BodyExceeds2Mb_ThrowsUpnpProtocolException_AC34() { ... }
```

Filter invocations:

```powershell
dotnet test --filter "Trait=ac&Value~AC-3"            # all AC-3.x tests
dotnet test --filter "ac=AC-3.4"                      # exact AC
dotnet test --filter "FullyQualifiedName~AC34"        # name-embedded match (Pattern 15)
```

Test names still embed the AC ID per Pattern 15. The trait is *additional*, enabling filter-based execution.

**AC:**

- **AC-A2.1** Every test satisfying a numbered AC carries `[Trait("ac", "AC-N.M")]`.

### Amendment A3 — Central Package Management

`Directory.Packages.props` at the repo root is the single source of truth for package versions. Per-project `<PackageReference Include="X" />` carries no `Version` attribute. One place to bump versions; no per-project drift.

> *Patched 2026-06-01 by [Amendment A6 — Story 1.1 implementation reality](#amendment-a6--a3-package-pin-corrections-story-11-implementation-reality): the `xunit.runner.visualstudio` pin was originally `3.0.x` (wrong — that's the xunit v3 runner) and `Microsoft.NET.Test.Sdk` was missing from the table (required under `CentralPackageTransitivePinningEnabled=true`). The block below shows the corrected pins as actually shipped in Story 1.1.*

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm"                          Version="8.4.x" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection"       Version="10.0.x" />
    <PackageVersion Include="Microsoft.Extensions.Logging"                   Version="10.0.x" />
    <PackageVersion Include="Microsoft.Extensions.Options"                   Version="10.0.x" />
    <PackageVersion Include="Microsoft.VisualStudio.Threading.Analyzers"     Version="17.x" />
    <PackageVersion Include="Microsoft.WindowsAppSDK"                        Version="2.1.3" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk"                         Version="17.12.x" />
    <PackageVersion Include="xunit"                                          Version="2.9.x" />
    <PackageVersion Include="xunit.runner.visualstudio"                      Version="2.8.x" />
    <PackageVersion Include="Moq"                                            Version="4.20.x" />
    <PackageVersion Include="FluentAssertions"                               Version="8.x" />
    <PackageVersion Include="NetArchTest.Rules"                              Version="1.x" />
  </ItemGroup>
</Project>
```

Wildcard `.x` patches resolve at Story-1-init time via `dotnet add package`. Concrete versions baked at that point.

### Amendment A4 — `Directory.Build.props` Contents + Named Analyzer

```xml
<!-- Directory.Build.props (repo root) -->
<Project>
  <PropertyGroup>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest</AnalysisLevel>
    <AnalysisMode>recommended</AnalysisMode>
  </PropertyGroup>

  <!-- Pattern 6 async discipline: .Result / .Wait() / .GetAwaiter().GetResult() lint -->
  <ItemGroup>
    <PackageReference Include="Microsoft.VisualStudio.Threading.Analyzers" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

`Microsoft.VisualStudio.Threading.Analyzers` is the named analyzer for Pattern 6 enforcement. Relevant rules: VSTHRD002 (sync wait on async), VSTHRD003 (`.GetAwaiter().GetResult()`), VSTHRD100 (`async void` without `try/catch`).

`Core` and `App` csprojs inherit the analyzer. **`Core.Tests` is exempt** from VSTHRD100 (test fixtures may use `async void` semantics) — exemption configured via `.editorconfig` rule overrides scoped to `tests/**`.

**Project-local override for `ohSpy.Core` (Core ↔ App boundary enforcement, Pattern 2):**

```xml
<!-- src/ohSpy.Core/Directory.Build.props -->
<Project>
  <Import Project="..\..\Directory.Build.props" />
  <!-- Boundary: Microsoft.WindowsAppSDK and Microsoft.UI.* must not be referenced from Core.
       Static enforcement: NetArchTest in tests/.../Architecture/CoreAppBoundaryTests.cs.
       Build-time enforcement: this csproj does NOT add Microsoft.WindowsAppSDK in its PackageReferences.
       NetArchTest catches transitive leakage if any. -->
</Project>
```

### Amendment A5 — `UpnpException` Hierarchy Concrete Shape (Decision 3 refinement)

```csharp
namespace ohSpy.Core.Http;

public abstract class UpnpException : Exception
{
    protected UpnpException(string message) : base(message) { }
    protected UpnpException(string message, Exception inner) : base(message, inner) { }
}

public sealed class UpnpTimeoutException : UpnpException
{
    public Uri Url { get; }
    public TimeSpan Budget { get; }
    public TimeSpan Elapsed { get; }

    public UpnpTimeoutException(Uri url, TimeSpan budget, TimeSpan elapsed)
        : base($"UPnP request to {url} timed out after {elapsed.TotalMilliseconds:F0}ms (budget {budget.TotalMilliseconds:F0}ms)")
    {
        Url = url; Budget = budget; Elapsed = elapsed;
    }
}

public sealed class UpnpTransportException : UpnpException
{
    public Uri Url { get; }
    public int? StatusCode { get; }

    // Patched 2026-06-02 by Amendment A9 — Story 1.3 implementation reality.
    // Original form: `: base(message, inner ?? new InvalidOperationException(message))`
    // synthesised a fake inner exception when none was supplied, misleading
    // debuggers and masking the null-inner state. Exception(string, Exception?)
    // accepts null cleanly; pass it through.
    public UpnpTransportException(Uri url, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Url = url; StatusCode = statusCode;
    }
}

public sealed class UpnpProtocolException : UpnpException
{
    public Uri Url { get; }
    public UpnpProtocolException(Uri url, string message) : base(message) { Url = url; }
}

public sealed class UpnpFaultException : UpnpException
{
    public Uri Url { get; }
    public string ActionName { get; }
    public int ErrorCode { get; }
    public string ErrorDescription { get; }

    public UpnpFaultException(Uri url, string actionName, int errorCode, string errorDescription)
        : base($"UPnP fault from {url} action '{actionName}': {errorCode} {errorDescription}")
    {
        Url = url; ActionName = actionName; ErrorCode = errorCode; ErrorDescription = errorDescription;
    }
}
```

- `UpnpException` is `abstract` — never thrown directly.
- **Not `[Serializable]`.** No cross-AppDomain or remoting use case; serializable exceptions are deprecated guidance in modern .NET.
- Each derived type holds the URL plus type-specific structured context.
- Exception field values double as the `DiagnosticContext` payload at the `catch` site:

```csharp
catch (UpnpTimeoutException ex)
{
    _diag.Warning(DiagCategories.HttpTimeout, "timeout exceeded",
        new DiagnosticContext { Url = ex.Url.ToString(), Elapsed = ex.Elapsed, Budget = ex.Budget });
    // ... handle
}
```

### Amendment A6 — A3 package pin corrections (Story 1.1 implementation reality)

**Source:** Story 1.1 (`1-1-project-scaffold-build-test-installer-pipeline`) implementation, surfaced by both the implementing Opus dev agent and the Sonnet code-review agent (2026-06-01).

**Issue 1 — `xunit.runner.visualstudio` version mismatch.** A3 originally pinned `xunit.runner.visualstudio` to `3.0.x`, but the `3.0.x` line targets **xUnit v3**. `xunit` itself is pinned to `2.9.x` (xUnit v2). The xUnit v3 runner does not pair with v2 sources — test discovery silently fails or behaves oddly under `dotnet test`. **Corrected to `2.8.x`** (the latest stable v2-compatible runner).

**Issue 2 — `Microsoft.NET.Test.Sdk` was missing from A3.** Under `CentralPackageTransitivePinningEnabled=true`, every transitive `PackageReference` (including those `dotnet new xunit` adds implicitly) must resolve to a `PackageVersion` entry — otherwise `dotnet restore` fails NU1010. The omission would have blocked Story 1.1's `dotnet test` step on a fresh clone. **Added `<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.x" />`.**

**Correction applied to:** Amendment A3's package table (above), with a forward marker linking back to this amendment.

**Implementation evidence:** `Directory.Packages.props` at the repo root.

---

### Amendment A7 — `Bootstrap.TryInitialize` real API signature (Decision 12 refinement)

**Source:** Story 1.1 implementation. The dev agent hit `CS0117 'Bootstrap' does not contain a definition for 'TryInitialize'` (with the 4-arg overload) and confirmed the actual WindowsAppSDK 2.1.3 API surface via the IDE.

**Issue.** Decision 12's `Program.cs` snippet showed:

```csharp
var bootstrapResult = Bootstrap.TryInitialize(
    majorMinorVersion: 0x00020001,
    versionTag: "",
    minVersion: new PackageVersion(2, 1, 3, 0),
    out _);          // expected int return
```

That 4-arg int-returning form does not exist in `Microsoft.WindowsAppSDK 2.x`. The real API is:

```
bool TryInitialize(uint majorMinorVersion,
                   string versionTag,
                   PackageVersion minVersion,
                   Bootstrap.InitializeOptions options,
                   out int hr)
```

Returns `bool` (true on success), takes a 5th `InitializeOptions` parameter (pass `Bootstrap.InitializeOptions.None` for the unpackaged case), and surfaces the failure HRESULT via the `out int hr`.

**Correction applied to:** Decision 12's "Bootstrap initialiser change" snippet (above), replaced with the canonical Story 1.1 implementation. The error-path check changes from `if (bootstrapResult < 0)` to `if (!ok)`, and the message-box format uses the out-`hr` value.

**Implementation evidence:** `src/ohSpy.App/Program.cs`. The structural intent (bootstrap before any `Microsoft.UI.*` type touch, native `MessageBoxW` for failure path, `Bootstrap.Shutdown()` in `finally`) is unchanged from the original D12.

---

### Amendment A8 — csproj snippet completeness (Decision 12 refinement)

**Source:** Story 1.1 implementation. Three issues surfaced in sequence:

**Issue 1 — `NETSDK1032: RuntimeIdentifier without PlatformTarget=AnyCPU`.** Debug builds (no RID specified) hit this error because MSBuild needs a `<PlatformTarget>` to pick when no RID resolves one. **Add `<PlatformTarget>AnyCPU</PlatformTarget>`.** Publish/BuildInstaller invocations still pass `RuntimeIdentifier=win-x64` on the command line; the AnyCPU default just unblocks the default Debug build path.

**Issue 2 — `<UseWinUI>true</UseWinUI>` was missing from the architecture snippet.** Template-default value, but omitting it from the canonical snippet means anyone copy-pasting the architecture's csproj into a hand-rolled project will get a non-WinUI build. **Add explicitly.**

**Issue 3 — `CS0017: Program has more than one entry point.`** `dotnet new winui` produces an `App.xaml` whose XAML compiler emits its own `Main` method (via `ApplicationDefinition` build action). When `Program.cs` also defines `[STAThread] Main`, both compile as entry points. The fix is the pair:
- `<StartupObject>ohSpy.App.Program</StartupObject>` — pin the entry point.
- `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` — suppress the XAML compiler's generator.

This was foreseen in the Story 1.1 spec (added as a conditional in the original D12 csproj task, then promoted to unconditional by the adversarial review). **Promoted to canonical in the architecture's csproj snippet.**

**Correction applied to:** Decision 12's "csproj changes (consequence)" snippet (above), replaced with the canonical Story 1.1 implementation.

**Implementation evidence:** `src/ohSpy.App/ohSpy.App.csproj`.

---

### Amendment A9 — `UpnpTransportException` ctor synthetic-inner smell (Decision 3 / Amendment A5 refinement)

**Source:** Story 1.3 (`1-3-upnp-http-client-facade-with-per-request-timeout-discipline`) implementation, confirmed by Sonnet code-review of commit `8a6fb44` (2026-06-02).

**Issue.** Amendment A5's `UpnpTransportException` ctor originally read:

```csharp
public UpnpTransportException(Uri url, string message, int? statusCode = null, Exception? inner = null)
    : base(message, inner ?? new InvalidOperationException(message))
```

The `inner ?? new InvalidOperationException(message)` fabricates a fake inner exception when none is supplied. This:

1. Misleads debugger / stack-trace tooling — a synthetic `InvalidOperationException` appears in the `InnerException` chain that didn't really occur.
2. Costs an unnecessary allocation on every non-inner-bearing throw.
3. Hides the genuine null-inner state — diagnostic consumers that check `ex.InnerException == null` to detect "this is a fresh top-level error" never see null.

**Correction.** Accept the null inner cleanly. `System.Exception(string?, Exception?)` (the BCL base ctor) handles null `inner` correctly:

```csharp
public UpnpTransportException(Uri url, string message, int? statusCode = null, Exception? inner = null)
    : base(message, inner)
```

The `UpnpException` abstract base may need its `Exception inner` ctor overload widened to `Exception? inner` for the call site to compile cleanly under `<Nullable>enable</Nullable>`; the consuming dev story (this one, retroactively) decides whether to apply the abstract-base fix too or to use a null-forgiving operator (`!`) at the single call site.

**Correction applied to:** Amendment A5's `UpnpTransportException` ctor (above), with an inline comment linking to this amendment.

**Implementation evidence:** Story 1.3 shipped the original verbatim form (`src/ohSpy.Core/Http/UpnpExceptions.cs`) to preserve architecture-match; A9 is a doc-first amendment. A small follow-up commit to that file will apply the fix in code — Story 1.4 or any author who touches `UpnpExceptions.cs` next can pick it up.

---

### Amendment A10 — `FetchDeviceDescriptionAsync` / `FetchScpdAsync` return-type symmetry (Decision 3 refinement)

**Source:** Story 1.3 implementation. The dev agent observed that D5's later revision moved `FetchScpdAsync` from `Task<ScpdDocument>` to `Task<byte[]>` (parsing is the caller's concern — `IScpdParser` from Story 1.4 + FR-100 incremental parse), but the equivalent revision of `FetchDeviceDescriptionAsync` from `Task<DeviceDescription>` to `Task<byte[]>` never landed in the architecture text. The two Fetch methods should be symmetric: both return raw bytes; consumers compose their respective parsers over them.

**Issue.** D3's interface signature showed:

```csharp
Task<DeviceDescription> FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct);
Task<ScpdDocument>      FetchScpdAsync(Uri scpdUrl, CancellationToken ct);  // pre-D5 revision
```

After D5 revised `FetchScpdAsync` to `Task<byte[]>`, the device-description return type was left unchanged — a likely oversight, not a deliberate design choice.

**Correction.** Both Fetch methods return `Task<byte[]>`:

```csharp
Task<byte[]>            FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct);
Task<byte[]>            FetchScpdAsync(Uri scpdUrl, CancellationToken ct);
```

The architectural reasoning is identical to D5's rationale: separate network fetch (I/O-bound, timeout-disciplined) from XML parse (CPU-bound, yield-disciplined). Consumers (Story 2.3's `EagerDescriptionDispatcher`, Story 2.6's lazy SCPD expansion) compose the parser.

**Correction applied to:** Decision 3's `IUpnpHttpClient` facade contract (above).

**Implementation evidence:** Story 1.3 already shipped `Task<byte[]>` for both methods (`src/ohSpy.Core/Http/IUpnpHttpClient.cs`), so this amendment is purely a doc-text fix to close the gap between code and architecture. Story 1.4 (XML parsers) will inherit the corrected guidance and consume raw bytes.

---

### Amendment A11 — Test-tree analyzer exemption conventions (Pattern 6 refinement)

**Source:** Stories 1.1–1.3 incremental additions to `.editorconfig`; consolidated 2026-06-02 by Story 1.3's code review.

**Context.** The `[tests/**/*.cs]` exemption block in `.editorconfig` has accumulated several analyzer suppressions across stories, each with a `# justification` comment but no central architectural reference. The canonical list as of Story 1.3:

| Analyzer | Suppressed in tests | Justification |
|---|---|---|
| `VSTHRD100` | `async void` without `try/catch` | xUnit + Moq fixture patterns require `async void` for event-handler test doubles. Added Story 1.1. |
| `CA1707` | underscores in identifiers | xUnit `Method_Scenario_ExpectedResult` naming idiom. Added Story 1.2. |
| `CA1806` | constructor side-effect (`new` not assigned) | FluentAssertions `act.Should().Throw<T>()` pattern accepts `Action act = () => new MyType(...)`. Added Story 1.2. |
| `VSTHRD003` | await on task started elsewhere | Cancellation-testing pattern: caller's CTS is observed by the under-test code, which awaits a TCS-backed task started by the test handler. Added Story 1.3. |
| `CA2263` | prefer generic type-parameter overload | `[InlineData(typeof(T))]` mandates the runtime-`Type` overload — xUnit's `[Theory]` attribute machinery doesn't support generic type parameters in inline-data. Added Story 1.3. |

**Position.** These exemptions are scoped to `tests/**` only — production code (`src/ohSpy.Core/**` + `src/ohSpy.App/**`) enforces every analyzer. The pattern works; the only architectural improvement is **discoverability**: future story authors should be able to find the canonical list without grepping `.editorconfig`.

**Correction.** No change to Pattern 6's existing async-discipline text. The exemption list above is the canonical reference; future additions to the test-tree exemption block should:

1. Add the analyzer ID + justification to the `.editorconfig` block (existing pattern).
2. Update the table in this amendment.
3. Cite the originating story.

Mechanical only — no code or pattern changes required.

**Implementation evidence:** `.editorconfig` test-tree block as of commit `8a6fb44`.

---

### Amendment A14 — `DiagnosticFileSink` location (Decision 8 refinement)

**Source:** Story 1.5 (`1-5-diagnostic-emitter-ring-sink-file-sink`) implementation; confirmed by the Sonnet code-review of commit `155601b` (2026-06-02).

**Issue.** Decision 8's project-structure tree placed `DiagnosticFileSink` in `src/ohSpy.App/Diagnostics/` with the rationale "App-side because `%LOCALAPPDATA%`". That rationale is incorrect: `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` is plain BCL — it has no Windows-only dependency, no WinUI dependency, nothing that would force the type into the `App` assembly. The same is true for every other type the file sink uses: `System.IO.FileStream`, `System.Threading.Channels.Channel<T>`, `System.Text.Json.JsonSerializer`, `Microsoft.Extensions.Logging.ILogger<T>` — all BCL or Microsoft.Extensions.* multi-platform packages.

The mis-placement caused real friction during Story 1.5 implementation:

1. **TFM mismatch.** `ohSpy.App` targets `net10.0-windows10.0.19041.0`; `ohSpy.Core.Tests` targets `net10.0`. NU1201 blocks the test project from referencing the App. Testing `DiagnosticFileSink` from the test project required cross-assembly InternalsVisibleTo gymnastics that wouldn't compile.
2. **Spurious `InternalsVisibleTo` grant.** The spec's Task 14.6 required adding `<InternalsVisibleTo Include="ohSpy.Core.Tests" />` to `ohSpy.App.csproj` so tests could reach the file sink's internal test-only ctor. With the type in Core, the existing Core-side InternalsVisibleTo grant (from Story 1.3) is enough — no new App-side grant needed.
3. **Cleaner Core ↔ App boundary.** Pattern 2 says App holds WinUI / Windows-App-SDK / P/Invoke surface; Core holds everything else. The file sink uses none of those, so Core is the right home. App is now reduced to its proper role for diagnostics: just the DI registration (`services.AddSingleton<IDiagnosticFileSink, DiagnosticFileSink>()`) plus the `SetRingSink` late-bind in `App.xaml.cs`.

**Correction.**

`DiagnosticFileSink` lives in `src/ohSpy.Core/Diagnostics/DiagnosticFileSink.cs`. The project structure tree (above), the §FR mapping table (above), and any other references in Decision 8 that placed it App-side are corrected to Core-side.

**Applied to:**
- §Project Structure: `src/ohSpy.App/Diagnostics/` folder removed (it never existed in shipped code post-Story-1.5); Core's `Diagnostics/` tree updated to show `DiagnosticFileSink.cs` as an impl, not just an interface.
- §FR mapping table (4.12 Diagnostics): `App/Diagnostics/DiagnosticFileSink.cs` → `Diagnostics/DiagnosticFileSink.cs (Core)`.

**Implementation evidence:** `src/ohSpy.Core/Diagnostics/DiagnosticFileSink.cs` in commit `155601b`. Test project (`ohSpy.Core.Tests`) reaches the internal test-only ctor via Story 1.3's existing `InternalsVisibleTo("ohSpy.Core.Tests")` on `ohSpy.Core.csproj` — no new csproj edit was required, and the App-side grant the spec mandated was never added (Story 1.5 commit message documents this as deviation #1).

---

### Amendment A16 — Test project: `FrameworkReference Microsoft.AspNetCore.App` + NU1510 prune

**Source:** Story 1.6 (`1-6-fakeupnpdevice-minimal-modes-first-chaos-test-netarchtest-rules`) implementation, confirmed by the Sonnet code-review of commit `50345c9` (2026-06-02).

**Issue.** Story 1.6's `FakeUpnpDevice` Kestrel fixture needs ASP.NET Core types (`WebApplication`, `Kestrel`, `HttpContext`). The architecture's D3 line (~370) left the Kestrel-package vs framework-reference choice open. The dev agent chose `<FrameworkReference Include="Microsoft.AspNetCore.App" />` — cleaner than 5+ transitive PackageReferences. **But adding the FrameworkReference to `ohSpy.Core.Tests.csproj` triggered NU1510 against the three existing `Microsoft.Extensions.{DependencyInjection,Options,Logging}` PackageReferences** — the shared framework provides those transitively; explicit refs are redundant under NU1510.

**Correction applied.** The dev agent removed the three redundant `Microsoft.Extensions.*` PackageReferences from `ohSpy.Core.Tests.csproj`. The test project still has access to those types (transitively via the framework); build is clean (0 warnings under `TreatWarningsAsErrors=true`).

**Guidance for future test projects.** Any test project that needs ASP.NET Core types (Kestrel fixture, integration test for an HTTP endpoint, etc.) should:

1. Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to the csproj.
2. Then remove any existing `Microsoft.Extensions.{DependencyInjection,Logging,Options,Hosting,Configuration}` PackageReferences from that csproj — the framework provides them transitively, NU1510 flags duplicates as warnings.
3. The framework reference works under `Microsoft.NET.Sdk` (no need to switch to `Microsoft.NET.Sdk.Web`); confirmed in .NET 8+.

**Implementation evidence:** `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` in commit `50345c9` — three PackageReferences removed alongside the FrameworkReference addition; build green.

---

### Amendment A18 — Chaos-hook filter syntax bug (Decision 13 refinement) — **CRITICAL**

**Source:** Story 1.6 implementation; **confirmed by live test run during Sonnet code-review (2026-06-02).**

**Issue.** The pre-commit chaos hook's `dotnet test --filter` argument was quoted verbatim in D13's bash script + prose description as:

```
dotnet test --filter "Trait=category&Value=chaos"
```

This is **MSTest TestProperty syntax**. Under xUnit's VSTest adapter, this filter form **silently matches zero tests** — `dotnet test` exits 0 with no test output. The correct xUnit syntax for `[Trait("category", "chaos")]` is:

```
dotnet test --filter "category=chaos"
```

Verified by the code-review's live test run:
- Old form: `dotnet test --filter "Trait=category&Value=chaos"` → **0 tests matched** (silent exit 0)
- New form: `dotnet test --filter "category=chaos"` → `Passed: 1, Duration: 470 ms`

**Impact: Stories 1.1-1.5's chaos hook was a SILENT NO-OP for ~5 days of work.** Every "Running chaos tests... 0 matched, exit 0 trivially" we read in dev-story reports was reported as "trivially-passing because no chaos tests exist yet" — but the root cause was actually the broken filter string. Once Story 1.6 added a chaos test, the hook would STILL have matched zero unless the filter was fixed.

**Correction applied.** Story 1.6 patched `.githooks/pre-commit` to use `category=chaos`. This amendment patches the architecture document's D13 bash script + prose description to match — see the patched verbatim block above.

**Future-proofing.** xUnit's VSTest filter syntax is documented at <https://github.com/Microsoft/vstest-docs/blob/main/docs/filter.md>: trait filters use `<TraitName>=<Value>` form (case-sensitive trait name). MSTest's `Trait=X&Value=Y` form does not apply. Any future hook additions to D13 must use xUnit syntax.

**Implementation evidence:** `.githooks/pre-commit` in commit `50345c9` (Story 1.6) carries the fix. Architecture D13 patched in the next docs commit.

---

### Amendment A22 — SSDP integration tests must deliver via multicast on Windows (Decision 2 refinement)

**Source:** Story 2.1 (`2-1-ssdp-transport-multicast-search-sockets-with-bounded-channel`) implementation; **confirmed by Sonnet code-review of commit `bafe206`-descendant (2026-06-02).**

**Issue.** D2's prose did not pin the *delivery method* SSDP integration tests must use. The dev agent's first instinct on the receive test was to deliver the canned NOTIFY by **unicast** to `(adapter_ipv4, 1900)`. The test timed out with the transport receiving nothing — despite the same code path working perfectly for the M-SEARCH self-receive test minutes earlier.

**Root cause.** Windows `SSDPSRV` (the built-in OS-level SSDP service) co-binds `*:1900` with `ReuseAddress`. Our transport's multicast listener ALSO binds `(adapter_ipv4, 1900)` with `ReuseAddress` — required by D2 to coexist. With two reuse-bound sockets on the same port:

- A **unicast** datagram is delivered to only ONE of them. Windows picks; in practice it picks `SSDPSRV` (which then silently drops it because the M-SEARCH-only filter doesn't match a NOTIFY). The transport-under-test never sees the datagram.
- A **multicast** datagram (sent to `239.255.255.250:1900`) is fanned out to ALL group members. Both `SSDPSRV` and our transport receive it. The transport-under-test's `ReceiveFromAsync` returns.

The M-SEARCH self-receive test worked because M-SEARCH is multicast; the failing receive test used unicast. Same sockets, same code — only the delivery method differed. Clean falsifiable diagnosis.

**Correction.** D2's narrative now carries an explicit **"Test contract"** subsection (patched in this amendment) pinning the rule:

> SSDP transport integration tests MUST deliver test datagrams via the multicast group (`239.255.255.250:1900`), NOT by unicast to `(adapter, 1900)`. Receive-side assertions MUST include a unique `USN` marker per test + a read-until-match loop so live-network NOTIFYs from real devices on a real adapter do not pollute assertions.

**Why the read-until-match loop matters.** Even on loopback, a developer machine sitting on a real LAN sometimes sees unsolicited NOTIFYs from real devices leak through (depending on adapter selection and OS multicast routing). Without a USN-marker filter, those NOTIFYs land in the channel and the first-datagram assertion fails non-deterministically. The pattern Story 2.1's tests established: generate a per-test GUID, embed it in the canned `USN: uuid:<guid>` header, then read from the channel until you see that exact marker (with a bounded timeout).

**Applied to:**

- D2 §"Test contract" — new subsection authored above (after the "Adapter switch (FR-050)" paragraph; before the "Rationale" block).
- Subsequent SSDP-story specs (Story 2.4 SSDP parser + chaos tests, any Epic 5 rescan tests that exercise the transport) inherit the rule via the architecture; create-story for those stories should surface it in their Dev Notes.

**Why this surfaced at Story 2.1, not earlier:** Story 2.1 is the first story that wires sockets onto port 1900. No earlier story did. The interaction with `SSDPSRV` was not foreseeable from the architecture-level decision alone — it's a Windows-specific socket-delivery quirk that only shows up under the exact conditions D2 mandates (reuse-bound multicast listener on the standard port).

**Implementation evidence:** `tests/ohSpy.Core.Tests/Discovery/SsdpTransportTests.cs` in Story 2.1 commits — the receive tests deliver via multicast with USN markers + read-until-match. The first version of the test that delivered by unicast was discarded in the dev-story workflow before review; the lesson is preserved here so it isn't re-discovered by Story 2.4.

**Carry-forward to future implementers.** If you find yourself writing an SSDP receive test that uses unicast because "it's simpler," stop. The simpler path is broken on Windows by design. The multicast + USN-marker pattern Story 2.1 codified is the canonical SSDP test delivery method.

---

### Amendment A23 — `ISsdpTransport` must become a per-`AdapterScope` factory for the FR-050 switch (Decision 2 + Decision 7 refinement)

**Source:** Story 2.2 (`2-2-network-adapter-enumerator-adapter-scope-startup-bind`) implementation + Sonnet code-review (2026-06-02). Raised as a candidate in Story 2.1's Dev Notes; **confirmed as a hard prerequisite for Story 5.2** during Story 2.2.

**Issue.** D2 says a fresh transport is constructed per adapter on switch (line 249); Decision 7's atomic-switch sequence step 8 says "construct new `AdapterScope` on new adapter IPv4". But Story 2.1 registered `ISsdpTransport` as a **DI singleton**, and Story 2.2's `AdapterScope` consumes that singleton. A singleton, once `DisposeAsync`'d, cannot be rebound to a new adapter — `SsdpTransport.StartAsync` guards against double-start and its sockets/fields are not reset on dispose. So the current wiring supports exactly ONE adapter bind for the process lifetime.

**Why this is fine for v1 through Story 2.4.** There is exactly one `AdapterScope` (constructed at startup), and no adapter switch exists until Story 5.2. The singleton is started once and disposed once at app exit. Stories 2.2–2.4 never switch adapters, so the singleton is correct and minimal for them.

**Correction (deferred to Story 5.2, pinned here so it isn't rediscovered).** When Story 5.2 implements the FR-050 atomic switch, `ISsdpTransport` MUST migrate from a DI singleton to a per-scope factory:

> Register `Func<ISsdpTransport>` (transient construction) instead of `AddSingleton<ISsdpTransport, SsdpTransport>()`. Each `AdapterScope` constructs and OWNS its own transport via the factory, disposing it on scope teardown. The switch sequence then constructs a fresh `AdapterScope` (Decision 7 step 8) which gets a fresh transport.

**Reconciliation with Story 2.4 (`DiscoveryService`).** `DiscoveryService` consumes `ISsdpTransport.IncomingDatagrams` and MUST read the **same** instance the active `AdapterScope` started — not a second DI-resolved instance. The factory migration therefore requires `AdapterScope` to **own and expose** its transport (or the transport's `ChannelReader`) so `DiscoveryService` is wired to the scope-owned instance, not to DI directly. Story 2.4's create-story must surface this ownership question; whichever shape 2.4 picks for the transport↔DiscoveryService wiring constrains the 5.2 factory design.

**Applied to:** No code change in Story 2.2 (singleton retained). Story 5.2's create-story + Story 2.4's create-story must both carry this amendment in their Dev Notes. D2's "adapter switch (FR-050)" prose and Decision 7 step 8 are the authoritative source; this amendment records the singleton→factory migration as the concrete mechanism.

**Why this surfaced at Story 2.2, not 2.1:** Story 2.1 only registered the transport type; it had no consumer and no scope. Story 2.2 is the first story to own the transport lifecycle via `AdapterScope`, which made the singleton-vs-per-adapter tension concrete.

---

### Amendment A26 — App-level disposable-ownership pattern (Pattern 7 + Pattern 6 refinement)

**Source:** Story 2.2 (`2-2-network-adapter-enumerator-adapter-scope-startup-bind`) implementation + Sonnet code-review (2026-06-02).

**Issue.** Decision 7 places the app-level `_appCts` (`CancellationTokenSource`, `IDisposable`) in `App`, and Story 2.2 adds the app-owned `_adapterScope` (`IAsyncDisposable`). A type owning `IDisposable`/`IAsyncDisposable` fields trips analyzer **CA1001** ("types that own disposable fields should be disposable") under `TreatWarningsAsErrors=true`. The naive fix — `App : IDisposable` — does not work: WinUI's `Application` base exposes no `IDisposable` contract the framework invokes, so `Dispose()` would never be called; and `_adapterScope` is `IAsyncDisposable`, so a synchronous `Dispose()` would have to block on async teardown, violating Pattern 6 (no `.Wait()`/`.GetAwaiter().GetResult()`).

**Decision (the canonical App-lifetime-disposable pattern).** App-lifetime disposables owned by the WinUI `App` are torn down deterministically in the `Window.Closed` handler, NOT via `IDisposable`:

1. Hold them as `private readonly`/nullable fields on `App`.
2. Apply a justified type-level `[SuppressMessage("Microsoft.Design", "CA1001", Justification = "...")]` explaining the WinUI-no-IDisposable + async-disposable reasons.
3. Subscribe a **synchronous** `void OnWindowClosed(object, WindowEventArgs)` handler (the `Window.Closed` delegate returns void) that fire-and-forgets an `async Task ShutdownAsync()` via `_ = ShutdownAsync()` — this avoids `async void` (VSTHRD100, which is App-tree-fatal; exempt only in `tests/**` per A11).
4. In `ShutdownAsync`, **cancel the app token first** (`await _appCts.CancelAsync()`), THEN `await scope.DisposeAsync()`, THEN `_appCts.Dispose()` — Decision 7 ordering: the parent cancellation propagates through all linked child scopes before teardown begins, so components holding `_appCts.Token` directly (future `DiscoveryService`, GENA) observe cancellation promptly rather than after the child's teardown budget elapses.

**Fire-and-forget exception discipline.** Any `App`-level fire-and-forget (`_ = StartAdapterScopeAsync(...)`, `_ = ShutdownAsync()`) MUST wrap its body in `try/catch (Exception ex) when (ex is not OutOfMemoryException)` and emit a diagnostic — an unobserved exception on a discarded `Task` is silently swallowed by the .NET unobserved-task path and would mask a real startup/teardown failure (e.g. `SocketException` from transport bind). Story 2.2's `StartAdapterScopeAsync` is the reference implementation.

**Applied to:** `src/ohSpy.App/App.xaml.cs` (Story 2.2). Stories 2.5 (relocates `AdapterScope` construction into `ShellViewModel`) and 5.2 (adds switch-time app-lifetime state) inherit this pattern; their create-story Dev Notes should reference A26. When `AdapterScope` moves into `ShellViewModel` (a Core type) in 2.5, the `_appCts` ownership stays in `App` (Decision 7) and the token is passed down — only the scope *construction site* moves.

**Why this surfaced at Story 2.2:** Story 2.2 is the first story to give `App` ownership of long-lived disposables. Every prior disposable (HTTP client, diagnostic sinks, transport) lived in DI, where the container owns disposal — so CA1001 never fired on `App` before.

---

### Amendment A27 — `RegistryEntry.DeviceCts` must use `CreateLinkedTokenSource(adapterToken)` and be disposed on removal (Decision 9 refinement)

**Source:** Story 2.3 (`2-3-device-registry-descriptionfetchstate-machine-eager-description-dispatcher`) implementation + Sonnet code-review (2026-06-02).

**Issue (two related defects in D9's code sketch):**

1. **Wrong initialiser.** D9's `RegistryEntry` code sketch shows `internal CancellationTokenSource DeviceCts { get; } = new()` — a standalone CTS. The architecture's own Decision 7 table (line 744) says the device level is "linked to adapter token", and the epics AC explicitly requires `CreateLinkedTokenSource(adapterToken)`. The sketch was never reconciled with D7.

2. **Missing `Dispose()`.** `CancellationTokenSource.CreateLinkedTokenSource(adapterToken)` registers a callback node in the parent token's internal `CallbackPartition` linked list. That node is only released when `Dispose()` is called on the linked CTS — `Cancel()` alone does not release it. D9's sketch and the architecture narrative mention `Cancel()` at byebye but are silent on `Dispose()`. On a busy network with frequent device arrivals and departures, each removal without `Dispose()` permanently holds a slot on the adapter's `_adapterCts` until the adapter itself is torn down, accumulating O(N) leaked callback registrations.

**Correction.** Two changes to `RegistryEntry` and `DeviceRegistry.RemoveCore`:

```csharp
// RegistryEntry ctor (correct form — not the sketch's = new()):
DeviceCts = CancellationTokenSource.CreateLinkedTokenSource(adapterToken);
DeviceToken = DeviceCts.Token; // snapshot before Dispose() could invalidate .Token

// DeviceRegistry.RemoveCore (correct form):
entry.DeviceCts.Cancel();   // AC-7.2: cancel the device's in-flight fetch
entry.DeviceCts.Dispose();  // release the linked-token callback on the adapter CTS
```

**Why `DeviceToken` must be snapshotted.** `CancellationTokenSource.Token` throws `ObjectDisposedException` after `Dispose()`. Any caller checking `entry.DeviceToken.IsCancellationRequested` after byebye (e.g. the test harness, or a future VM holding a stale reference) would crash. Storing the token as a field at construction time makes it permanently readable — the snapshotted `CancellationToken` value type already reflects the cancelled state via its internal `_source` reference without re-entering the CTS.

**Applied to:** `src/ohSpy.Core/Devices/RegistryEntry.cs` (ctor + `DeviceToken` property), `src/ohSpy.Core/Devices/DeviceRegistry.cs` (`RemoveCore`). Story 2.3 implements the correct form; this amendment patches D9's sketch so downstream stories don't copy the wrong version.

---

### Amendment A28 — Decision 9 FetchAsync sketch has two inaccuracies: `RootUdn:Guid` and "no locks" (Decision 9 refinement)

> **PARTIALLY SUPERSEDED by Amendment A30 (Story 2.10).** Inaccuracy 1's `UdnMatches(string udn, Guid uuid)` signature no longer holds: device identity is now the UDN **string**, so the helper is `UdnMatches(string descUdn, string registeredUdn)` (strip `uuid:` from both, `OrdinalIgnoreCase` — NO `Guid.TryParse`). The threading note (Inaccuracy 2) still stands.

**Source:** Story 2.3 implementation + Sonnet code-review (2026-06-02). Two independent inaccuracies in D9's `FetchAsync` pseudo-code and threading narrative.

**Inaccuracy 1 — `description.RootUdn != entry.Uuid` (wrong field name, wrong type).**

D9's sketch writes `if (description.RootUdn != entry.Uuid)` — implying `RootUdn` is a `Guid`. The real `DeviceDescription` model (Amendment A10, `src/ohSpy.Core/Models/DeviceDescription.cs`) exposes `string Udn`, carrying the raw UPnP `<UDN>` text `"uuid:<guid>"`. A naive `description.Udn != entry.Uuid.ToString()` compare false-mismatches every real device (prefix mismatch + hex casing). The correct check requires normalisation:

```csharp
internal static bool UdnMatches(string udn, Guid uuid)
{
    var s = udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? udn[5..] : udn;
    return Guid.TryParse(s, out var parsed) && parsed == uuid;
}
```

**Inaccuracy 2 — "no locks" prose omits the cross-thread read via identity lookup.**

D9 states "no fields require `volatile` or locks" — correct for `RegistryEntry` field *writes* (all UI-thread). However, `DeviceRegistry`'s backing collection is read off the UI thread by `RegistryIdentityLookup → DiagnosticRingSink.Push` (which resolves identity on the emitting thread — confirmed at `DiagnosticRingSink.cs:27`). A plain `Dictionary<Guid, RegistryEntry>` read concurrent with a UI-thread `Add`/`Remove` is a data race (torn-read / corruption). The registry's backing store must be `ConcurrentDictionary<Guid, RegistryEntry>`:

> The "no locks" guarantee applies to `RegistryEntry` *field* mutations (all UI-thread). `DeviceRegistry`'s *collection* requires `ConcurrentDictionary` because `TryGetEntry` is read on the emitting thread (identity lookup path). `RegistryEntry.Description` reference reads are safe off-thread (atomic reference read in .NET); a slightly-stale `null` yields the `uuid:<uuid>` fallback.

**Applied to:** `src/ohSpy.Core/Devices/DeviceRegistry.cs` (ConcurrentDictionary backing), `src/ohSpy.Core/Devices/EagerDescriptionDispatcher.cs` (`UdnMatches` static helper). Update D9's `FetchAsync` sketch to replace `description.RootUdn != entry.Uuid` with a call to `UdnMatches(description.Udn, entry.Uuid)`. Update D9's threading narrative to note the ConcurrentDictionary requirement and its rationale.

---

### Amendment A29 — Allocation-sensitive tests must use thread-local GC measurement (Pattern 15 / testing-standards refinement)

**Source:** Story 2.3 implementation (2026-06-02). Surfaced when new test classes increased parallel xUnit load.

**Issue.** `DiagnosticEmitterTests.Verbose_BelowMinSeverity_AllocatesZeroDiagnosticEntries` (AC-8.7) measured **process-wide** allocations via `GC.GetTotalAllocatedBytes(precise: true)`. xUnit runs test classes in parallel by default; a process-wide counter folds in allocations from concurrently-running tests on other threads. Story 2.3's ~36 new test classes increased that background allocation pressure, causing the assertion to fail once in the full run while passing immediately in isolation.

**Correction.** Allocation-sensitive tests must use **`GC.GetAllocatedBytesForCurrentThread()`** (thread-local), which isolates the measured loop from other threads:

```csharp
// WRONG — polluted by concurrent xUnit threads:
var before = GC.GetTotalAllocatedBytes(precise: true);

// CORRECT — thread-local, immune to parallelism:
var before = GC.GetAllocatedBytesForCurrentThread();
```

**Testing-standards rule (append to Pattern 14/15 in the architecture):** Any test asserting zero or near-zero allocations per operation MUST use `GC.GetAllocatedBytesForCurrentThread()`. `GC.GetTotalAllocatedBytes` is process-wide and non-deterministic under xUnit parallelism regardless of test isolation annotations.

**Applied to:** `tests/ohSpy.Core.Tests/Diagnostics/DiagnosticEmitterTests.cs` (Story 2.3). Future zero-allocation tests (e.g. SSDP channel path, Description parsing hot-path) must follow this pattern.

---

### Amendment A30 — Device identity is the UDN string, not a parsed `Guid` (Decision 9 correction)

**Source:** Sprint Change Proposal 2026-06-04 (correct-course); surfaced by the Story 5.2 manual smoke on a live Linn network. Fixed in Story 2.10.

UPnP UDNs are opaque strings (`uuid:` + an identifier; UDA recommends but does not *require* RFC 4122). Devices in the wild — including Linn — use non-RFC-4122 UDNs. The original Decision 9 keyed the registry on a `Guid` parsed via `Guid.TryParse`, which silently drops every non-RFC-4122 device: `SsdpParser.ExtractUuid` parses → null → `DiscoveryService`'s `Uuid.HasValue` gate skips the announcement → no registry entry → no tree row; the SSDP log renders the all-zero `Guid.Empty`; `EagerDescriptionDispatcher.UdnMatches` re-parses the same way.

**Correction:** identity is the full normalised UDN **string** (`uuid:<body>`, the `::<nt>` suffix stripped, the `uuid:` prefix retained), compared `OrdinalIgnoreCase`. The registry is **UDN-keyed** (`ConcurrentDictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)`). `DiagnosticContext.DeviceUuid` (now `string?`), `RegistryEntry.Udn`, `IDeviceRegistry.DeviceRemoved` (`Action<string>`), `IDiagnosticIdentityLookup.TryGetFriendlyName(string)`, the device-tree node identity, and the popup FR-037 banners all carry the string. The FR-041 Identity-column fallback is the UDN string itself (already prefixed `uuid:`). `Guid.TryParse` on a UDN is forbidden. `SubscriptionClient._pending`/`PendingId` stay `Guid` — they key a per-subscribe correlation id, not device identity. The `OrdinalIgnoreCase` comparer preserves the prior `Guid`-equality semantics for RFC-4122 (hex) UDNs, so existing devices are unaffected.

**Supersedes** Amendment A28's `UdnMatches(string udn, Guid uuid)` signature: the helper is now `UdnMatches(string descUdn, string registeredUdn)` (strip `uuid:` from both, `OrdinalIgnoreCase`) — no Guid parse.

**Applied to:** `SsdpParser`, `SsdpAnnouncement`, `DiscoveryService`, `DeviceRegistry`/`IDeviceRegistry`/`RegistryEntry`, `EagerDescriptionDispatcher`, `DiagnosticContext`/`DiagnosticRingSink`/`IDiagnosticIdentityLookup`/`RegistryIdentityLookup`/`NullIdentityLookup`, `DeviceNodeViewModel`/`DeviceTreeViewModel`/`ServiceNodeViewModel`/`BrowserLaunch`, `PropertiesViewModel`/`InvocationPopupViewModel`/`SubscriptionPopupViewModel`, `SsdpLogEntry`/`SsdpLogViewModel`, `SubscriptionClient` (identity emits only). Decision 9 + §4.1 component bullet reworded "UUID-keyed" → "UDN-keyed (string identity, OrdinalIgnoreCase)".

---

### Amendment A31 — Popups float in free z-order; no Win32 owner link (FR-046 / Decision 10 revision)

**Source:** Story 5.2 keystone-smoke follow-up (2026-06-04, live Linn network) — Project Lead found the pinned z-order confusing during use.

The original Decision 10 established the Win32 owner relationship via `SetWindowLongPtr(GWLP_HWNDPARENT)`. The OS honours an owner link by keeping the owned window **always above** its owner, so clicking the shell could never bring it in front of an open popup. FR-046's "z-order above parent / no-push-behind on focus" behaviour was therefore *too strong*: operators expect a popup to open on top but then float freely (e.g. open an action-invocation popup, then click the device tree → the shell should come forward over the popup).

**Revision:** `WindowOwnershipManager.Adopt` no longer sets the owner link. A popup opens on top via its existing `child.Activate()` (the canonical `Activate()`-then-`Adopt` order is unchanged), then participates in **normal z-order** — the shell can be clicked back in front of it and vice-versa. The one ownership behaviour worth keeping, **close-with-parent**, is re-implemented explicitly: a per-child `parent.Closed` handler closes the child (unhooked if the child closes first), so closing the shell still tears down its popups with no orphaned windows. The `GetChildrenOf` / `_ownership` introspection seam is retained for tracking.

**Dropped (consequences of removing the owner link, all accepted by the Project Lead):** always-above, no-push-behind on focus, and minimise/restore-with-parent. Modality was never in scope (popups remain independently activatable). The `GWLP_HWNDPARENT` const and the `SetWindowLongPtr` P/Invoke are removed; `WindowOwnershipManager` is no longer `partial`.

**FR-046 behaviours after A31:**

| FR-046 behaviour | After A31 |
|---|---|
| Appears above parent on show | ✅ Retained — via `child.Activate()` (foreground on open), not an owner link |
| Stays above parent on focus shift (no-push-behind) | ❌ **Removed** — popups float in normal z-order |
| Minimises/restores with parent | ❌ **Removed** — independent windows |
| Closes when parent closes | ✅ Retained — now an explicit `parent.Closed` handler, not OS-delivered |

**Supersedes:** Decision 10's "four OS-delivered behaviours" framing, its FR-046 behaviours table, and AC-10.1 / AC-10.3 / AC-10.4. **AC-10.2** (close-with-parent) and **AC-10.5** (`Activate()`-then-`Adopt` at all popup sites) stand. The z-order manual-test now reads: open a popup, click the shell → shell comes forward over the popup; re-click the popup → it comes forward.

**Applied to:** `WindowOwnershipManager` (App). No Core change; no unit-test change (App windowing layer — smoke-verified 2026-06-04).

---

### Decision 13 — Pre-Commit Chaos Hook (the regression net replacing CI)

**Chosen:** Git pre-commit hook at `.githooks/pre-commit` running the chaos test suite before every commit. Without CI (Decision 12), this is the regression net that catches `.Result` regressions and broken NFR-P2 invariants before they're merged.

**One-time setup (executed in Story 1 init):**

```powershell
git config core.hooksPath .githooks
```

**Hook contents (`.githooks/pre-commit`, committed to the repo):**

```bash
#!/usr/bin/env bash
# Runs the chaos test category to catch NFR-P2 regressions.
# Wall-clock budget: ~5s. Fail the commit if any chaos test fails.
# Patched 2026-06-02 by Amendment A18: filter syntax must be xUnit-form
# `category=chaos`, NOT MSTest-form `Trait=category&Value=chaos` (which
# silently matches zero tests under xUnit's VSTest adapter — the actual
# root cause of Stories 1.1-1.5's "trivially-passing" hook state).
set -e
echo "Running chaos tests..."
dotnet test --filter "category=chaos" --nologo --verbosity quiet
```

**On Windows without bash:** the hook works via the bash shipped with Git for Windows. If Simon's machine doesn't have Git Bash for some reason, a PowerShell equivalent (`.githooks/pre-commit.ps1` shimmed via `.githooks/pre-commit`) is the fallback.

**Behaviour:**

- Pre-commit runs `dotnet test --filter "category=chaos"` (patched A18 from the broken MSTest-form `Trait=category&Value=chaos`). Wall-clock ~5 s with the FakeUpnpDevice fixture and chaos test (both Story 1.6).
- Hook fails (non-zero exit) → commit aborted with the test output visible.
- Hook can be skipped via `git commit --no-verify` for emergency commits (e.g. WIP). Architecturally allowed but linted via PR-review discipline.

**Acceptance criteria:**

- **AC-13.1** `.githooks/pre-commit` exists, is executable, and runs the chaos test suite.
- **AC-13.2** Story 1 sets `core.hooksPath` to `.githooks` as part of repo init steps.
- **AC-13.3** Adding a `.Result` to any `Core` async-call site causes the pre-commit hook to fail (verified by Pattern 6 analyzer producing a build error, which causes `dotnet test` to fail at compile time).
- **AC-13.4** A simulated NFR-P2 regression (e.g. removing the `ResponseHeadersRead` flag from `UpnpHttpClient.FetchScpdAsync`) is caught by the chaos test and fails the pre-commit hook.

**Rationale:**

- No-CI (Decision 12) leaves a real regression-net hole. The `Microsoft.VisualStudio.Threading.Analyzers` build-time lint catches some violations at compile time (`.Result` / `.Wait()`), but architectural regressions like "we accidentally dropped `ResponseHeadersRead`" only surface at runtime against a misbehaving device. The chaos test fixture is exactly that.
- Cost is one Git hook file + ~5 s of pre-commit wall-clock — single-digit-line cost for the bulk of CI's regression value.
- Murat's confidence-lift estimate (62% → 78% on the three quality bars) is largely realised by this single decision, the Story-2-split sprint-planning move, and the polish/soak story.

**Open follow-ups:**

- **Soak test in pre-commit:** No — soak is 8 hours, never going in pre-commit. Soak runs manually before release (open Sprint-Plan item).
- **CI as later drop-in:** If a second contributor joins or the project goes public, the pre-commit hook converts to a GitHub Actions step verbatim. Decision 12's "drop-in CI" pathway remains the upgrade path.

### Carry-Forward Risk Items (acknowledged, not amended)

These came up in the party-mode review but don't require architectural changes — they're risks to *manage* during implementation, captured here so they're not lost.

- **GENA hand-rolled HTTP parser (D4) — budget 1.5× the implied story size.** Winston: "hand-rolled HTTP is where confident architectures meet humbling reality." Sprint plan should size the FR-049 / FR-104 stories accordingly.
- **D4 ↔ D7 cancellation between header-read and body-read in the callback host.** The `TimeoutStream` wrapper enforces budgets, but cancellation propagation from the adapter / device CTS into the per-connection parse loop deserves an explicit story-level sequence diagram. Worth a 30-minute design pass at the start of the FR-049 story.
- **D2 ↔ D5 backpressure under concurrent SSDP storm + slow SCPD parse.** Channel is `DropOldest(4096)`. Plausible scenario: 50+ devices announcing simultaneously while a 200-action IGD SCPD is being parsed. Diagnostic-driven monitoring (`Ssdp.Channel.NearFull` / `Overflow` Warnings) is the v1 mechanism; if the diagnostics show consistent overflow, raise the channel capacity or rebalance the parse-to-channel cost ratio.
- **Reliability / UI-polish ACs are partially aspirational.** 30-minute no-crash, 8-hour 200 MB ceiling, "popup recovers gracefully" cannot be enforced by red-green-refactor TDD. They require manual UI testing + soak testing + the pre-release polish story. Expect 8-10 of the ~70 ACs to be soak-test or manual-only.

---

## Architecture Validation & Readiness

### Coherence

All 12 architectural decisions + 5 amendments + Decision 13 cross-reference consistently:

- D1 (`IUiDispatcher`) referenced by D3, D4, D6, D7, D8, D9, A1. Consistent.
- D2 → D9 via Registry. Consistent.
- D3 → D5 (revised to return `byte[]`) → A5 (exception hierarchy). Consistent.
- D4 → D7 (cancellation into callback parse). Risk item flagged; mechanism design pinned to story-level.
- D6 → A1 (placeholder atomic replacement). Pinned.
- D7 → D9, D10. Cleanup-uses-level-above-token invariant explicitly applied to UNSUBSCRIBE.
- D8 → every error path; categories in `DiagCategories.*` constants (A4 enforcement via NetArchTest).
- D9 → D6 (`IdentityKeyedSortedCollection` for top-level rows, `BoundedObservableCollection` for children via A1). Consistent.
- D11 → D3, D4 via `IOptions<HttpTimeoutOptions>`. Consistent.
- D12 → A3, A4 (build configuration). D13 fills the no-CI regression-net gap. Consistent.

### Requirements Coverage

Every FR (FR-001..055, FR-100..104) has an architectural home; cross-referenced in the Step 6 FR-mapping table. Every NFR (NFR-R1..R5, NFR-P1..P6, NFR-UI1..UI4) is addressed by a specific decision or pattern. PRD §6 Performance Budgets are referenced in the chaos test plan; soak tests for 8-hour and 30-minute bars are sprint-plan items.

**Three previously-flagged gaps:**

- ~~Gap-1 (FR-044 placeholder pattern):~~ pinned via Amendment A1.
- Gap-2 (FR-055 smart auto-follow): remains story-level; concrete mechanism (`IsAtTop` flag observed from `ItemsRepeater` scroll position; `PrependNewest` auto-scrolls only when `IsAtTop == true`) is the FR-055 story's first AC.
- Gap-3 (FR-052 URL safety): remains story-level; `http://` / `https://` whitelist + Warning diagnostic for other schemes is the FR-052 story's safety AC.

### Implementation Readiness

- Every Decision has explicit ACs (citable in stories) — ~70 ACs across the 12 Decisions + 5 Amendments + D13.
- Pattern 11 + DiagCategories.cs makes diagnostic emission uniform and grep-able.
- Pattern 15 + Amendment A2 fixes the AC trait shape (`[Trait("ac", "AC-N.M")]`).
- Amendment A3 + A4 unblock Story 1 (project init): `Directory.Packages.props` + `Directory.Build.props` are concrete.
- Amendment A5 unblocks Story-4-ish work (HTTP facade): exception hierarchy is concrete.
- Decision 13 + the chaos test fixture (Story 2a deliverable) form the regression net.

### Architecture Completeness Checklist

**Requirements Analysis** [x] [x] [x] [x]

**Architectural Decisions** [x] [x] [x] [x]

**Implementation Patterns** [x] [x] [x] [x]

**Project Structure** [x] [x] [x] [x]

16/16 checked.

### Readiness Assessment

**Overall Status:** ✅ **READY FOR IMPLEMENTATION**

**Confidence:** **HIGH** (lifted from MEDIUM-HIGH after the validation amendments).

Quality-bar confidence per Murat's framework, post-amendments:

| Bar | Pre-amendment | Post-amendment |
|---|---|---|
| Reliability | 70% | 80% (D13 closes the regression net; Story-2-split call-out helps fixture realism) |
| Performance | 75% | 85% (Pattern 6 analyzer pinned by name in A4; D13 catches `ResponseHeadersRead`-style regressions) |
| UI polish | 55% | 65% (A1 closes the FR-044 chevron-collapse trap; remains the bar most exposed to schedule pressure) |
| **Combined** | **62%** | **~78%** |

The 78% combined number remains a sober estimate — the remaining ~22% risk is implementation-execution risk that no amount of architectural specification can pre-pay. Sprint discipline (Story 2 split, polish/soak ringfenced) carries the rest.

### Implementation Handoff

**Read-this-first order for an agent picking up Story 1:**

1. The brief (`briefs/brief-ohSpy-2026-05-29/brief.md`) — what + why.
2. The PRD (`prds/prd-ohSpy-2026-05-30/prd.md`) — every FR + NFR.
3. This architecture document, top to bottom — every decision + every amendment.
4. The Step 6 file tree — where every component lives.
5. The Patterns section — coding conventions.

**First implementation priorities (informative — sprint plan in the next workflow phase pins ordering):**

1. **Story 1 — Project init.** `dotnet new`, two-project split, `Directory.Packages.props` (A3), `Directory.Build.props` (A4), `.editorconfig`, `.githooks/pre-commit` + `core.hooksPath` setup (D13), `installer/ohSpy.iss` skeleton, `BuildInstaller` MSBuild target (D12). Green `dotnet build`.
2. **Story 2a — Minimal test infrastructure.** xUnit + Moq + FluentAssertions packages; `Fakes/FakeUpnpDevice.cs` skeleton with 3 modes (Happy / Hang / Fault); `TestHttpMessageHandler`; `InlineUiDispatcher`; first BoundedObservableCollection tests; first chaos test exercising the hook (D13 AC-13.4).
3. **Story 2b — Extended fake-device modes.** SlowDripBody, ChunkedThenAbort, WrongContentLength, GiantScpd. Defer until Story 7 (SCPD parsing) needs them — Murat's split.
4. **Story 3+ — Core primitives → SSDP transport → DiscoveryService → DeviceRegistry → EagerDescriptionDispatcher → tree VM.** Bottom-up to "devices appear in the tree".
5. **Subsequent stories — SCPD lazy load, action invocation, GENA subscription, diagnostics viewer, properties window, adapter switch, rescan.**
6. **Polish & Soak story (before release) — manual UI verification of FR-044/046/054 behaviours, the 30-minute no-crash soak, the 8-hour 200 MB-ceiling soak. Murat's recommendation.**

**Agent guidelines (recap):**

- Treat this document as the contract; deviate only via explicit amendment.
- For diagnostics: always use a `DiagCategories.*` constant; always populate the mandatory `DiagnosticContext` fields per Pattern 11.
- For async in `Core`: `ConfigureAwait(false)` on every await; CT parameter last; never `.Result`/`.Wait()`.
- For UI-thread mutations: via `IUiDispatcher.Post`/`PostAsync`; call `AssertOnUiThread()` at entry of UI-thread-only methods (throws in Release, D1).
- For cancellation: derive the CT from the right scope (D7); cleanup operations use level-above token.
- For collections: `BoundedObservableCollection` for newest-first-bounded; `IdentityKeyedSortedCollection` for sorted with stable identity (D6).
- For VMs that own child nodes (Device/Service): construct with a `LoadingPlaceholderViewModel` child; replace atomically via `ReplaceWith` (A1).

---

This document is the architecture for ohSpy. **The workflow may now proceed to the next phase (epic + story breakdown via `bmad-create-epics-and-stories`).**
