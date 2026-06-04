---
baseline_commit: 5a5ad9452f8273332d079d36231c519d46dd9978
---

# Story 4.1: Event Callback Host — `TcpListener` + Hand-Rolled HTTP/1.1 Parser

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want an in-process callback HTTP server bound to the selected adapter's IPv4 via `TcpListener` with strict framing, lenient header tolerance, size caps, per-phase timeouts, and a connection cap,
so that subscribed devices can deliver `NOTIFY` events back to ohSpy without requiring Administrator privileges, URL-ACL registration, or `HttpListener` — and so that slowloris / body-bomb / connection-flood attacks cannot stall or crash the host.

**Note (Architecture risk carry-forward — epics.md L1499):** This story is sized at **1.5× the implied story size** ("hand-rolled HTTP is where confident architectures meet humbling reality"). Expect the parser, the `TimeoutStream` wrapper, and the malformed-input AC matrix to take meaningfully more time than a typical story. The `D4 ↔ D7` open follow-up (architecture L3031) calls for a ~30-min design pass on cancellation propagation between header-read and body-read at the start of this story — do it first.

## Epic-4 scope context

Epic 4 (GENA Subscription): right-click → Subscribe opens a subscription popup; SUBSCRIBE goes out with a `CALLBACK` URL pointing at **this** in-process callback host; NOTIFY events stream into the popup. **This story (4.1) is the inbound NOTIFY receiver only** — the first inbound TCP listener in the product. It is headless Core/infra (no UI, no bound VM state), closest in shape to Story 3.1 and Story 2.1 (`SsdpTransport`). The downstream consumers are **Story 4.2** (`SubscriptionClient` filters/routes `NotifyReceived` by SID, parses the `<e:propertyset>` body) and **Story 4.3** (subscription popup VM). 4.1 does **not** parse the eventing XML and holds **no** VM state.

## Acceptance Criteria

> All hardening ACs below map to the **pre-added** `DiagCategories.Gena.Callback.*` constants (verified present — see Dev Notes "Pre-added scaffolding"). **No new `DiagCategories` constant is introduced** → `DiagCategoriesUsageTests` stays unchanged. Tag every AC with `[Trait("ac", "AC-4.x")]`.

### Interface & record shape

**AC-4.1.1 — `IEventCallbackHost` surface (D4).** `src/ohSpy.Core/Events/IEventCallbackHost.cs` declares exactly:
```csharp
public interface IEventCallbackHost : IAsyncDisposable
{
    Task StartAsync(IPAddress adapterIPv4, CancellationToken ct);
    Uri CallbackBaseUrl { get; }     // http://<adapterIPv4>:<port>/ — announced in SUBSCRIBE CALLBACK header
    event Func<NotifyRequest, Task> NotifyReceived;
}
```

**AC-4.1.2 — `NotifyRequest` record (D4).** `src/ohSpy.Core/Events/NotifyRequest.cs` is exactly:
```csharp
public sealed record NotifyRequest(string Sid, long Seq, string PathAndQuery, byte[] Body, DateTime ReceivedUtc);
```
Namespace `ohSpy.Core.Events`. `Seq` is parsed from the `SEQ` header; absent/unparseable `SEQ` → `0` (lenient — GENA seq may be absent on the initial event of some stacks; do NOT 400 on it).

### Listener bind & lifecycle

**AC-4.1.3 — bind to the adapter IP, ephemeral port (NOT `0.0.0.0`).** On `StartAsync`, construct `new TcpListener(new IPEndPoint(adapterIPv4, 0))`, call `Start(backlog: 16)`. `CallbackBaseUrl` is `http://<adapterIPv4>:<actual-port>/` read back from `((IPEndPoint)listener.LocalEndpoint).Port` after `Start()`. `StartAsync` returns once the listener is bound and accepting (the accept loop runs on a background `Task.Run`, mirroring `SsdpTransport`). Calling `StartAsync` twice throws `InvalidOperationException` (mirror `SsdpTransport`).

**AC-4.1.4 — `CallbackBaseUrl` consumable by Story 4.2.** `CallbackBaseUrl` is a `Uri` in the exact shape `IUpnpHttpClient.SubscribeAsync(Uri eventSubUrl, Uri callbackUrl, …)` accepts (verified signature — Dev Notes). Before `StartAsync`, accessing `CallbackBaseUrl` throws `InvalidOperationException` ("StartAsync has not been called") — same guard idiom as `SsdpTransport.IncomingDatagrams`.

**AC-4.1.5 — single-request connections, no keep-alive.** Every accepted connection is bounded to a single request; every response carries `Connection: close`; the connection is closed after the response is written.

### Connection cap (flood defence)

**AC-4.1.6 — max 8 concurrent connections (AC-4.7 / D4).** A `SemaphoreSlim(8,8)` (or equivalent bounded gate) caps concurrent in-flight connection handlers at **8**. When a 9th connection is accepted while all 8 slots are busy, it is **accepted-then-immediately-closed** with a `Warning` `DiagCategories.GenaCallbackFlood` carrying `RemoteEndpoint` context; no request is read, no other behavioural effect. (Connection cap 8 is independent of — and coincidentally equal to — the eager-fetch cap; keep them distinct.)

### Per-connection budgets (slowloris / body-bomb defence)

**AC-4.1.7 — header read budget 5 s (AC-4.3 / D11).** The connect → headers-complete budget is `HttpTimeoutOptions.CallbackHeaders` (5 s, pre-added). Headers stalled beyond it → connection closed + `Warning` `DiagCategories.GenaCallbackHeadersTo`.

**AC-4.1.8 — body read budget 5 s (AC-4.4 / D11).** The headers-complete → body-complete budget is `HttpTimeoutOptions.CallbackBody` (5 s, pre-added, **separate** from headers — total worst case 10 s/connection). Body stalled beyond it → connection closed + `Warning` `DiagCategories.GenaCallbackBodyTo`. (Body shorter than declared `Content-Length` manifests as a body-read stall → this path.)

**AC-4.1.9 — `TimeoutStream` enforces the active budget (D4).** `src/ohSpy.Core/Events/TimeoutStream.cs` wraps the raw `NetworkStream` and throws (a distinguishable `TimeoutException` / sentinel) on any read whose idle time exceeds the **active** budget. The parser sets the active budget as it transitions phase (headers → body) — "one place to enforce timeout discipline" (D4 L469). Idle-time enforcement via `CancellationTokenSource.CancelAfter` reset per read, or a `ReadAsync(...).WaitAsync(budget)` per read — engineering judgment; document the choice. Honour the linked CTS (AC-4.1.18) so adapter/app cancellation also unblocks a pending read.

### Size caps

**AC-4.1.10 — header block ≤ 16 KB (AC-4.1 / D4).** Accumulated header bytes (request line + all header lines, before the terminating empty CRLF) exceeding **16 KB** → `413 Content Too Large` + `Connection: close` + `Warning` `DiagCategories.GenaCallbackOversize`.

**AC-4.1.11 — body ≤ 1 MB (AC-4.2 / D4).** A `Content-Length` > **1 MB** (`1_048_576`) → `413` + close + `Warning` `GenaCallbackOversize` (reject **before** reading the body — do not buffer a body-bomb). Read exactly `Content-Length` bytes for a valid body; any extra bytes on the wire are ignored (no keep-alive, connection closes).

**AC-4.1.12 — max 64 headers.** More than **64** header lines → treat as malformed framing → `400` + close + `Warning` `GenaCallbackMalformed`. Unknown headers count against the cap.

### Framing (strict — violation → `400` + close + `GenaCallbackMalformed`)

**AC-4.1.13 — request line (D4).** `METHOD SP request-target SP HTTP-version CRLF`; **exactly two** SP; method is uppercase ASCII token chars; line ends with `CRLF` — **bare `LF` accepted, bare `CR` rejected**. Empty `CRLF` terminates the header block. Violations → `400`.

**AC-4.1.14 — header lines (D4, RFC 7230 §3.2.6).** Header names read case-insensitively, canonicalised to lowercase internally. **Whitespace-folded (obsolete-fold) headers (RFC 7230 §3.2.4) → `400`.** Duplicate **known** headers (`nt`/`nts`/`sid`/`seq`) → **last-wins**. Unknown headers ignored (counted against the 64 cap).

**AC-4.1.15 — `Content-Length` required (AC-4.5 / D4).** `Content-Length` MUST be present and parse as a non-negative integer. **Absent → `411 Length Required` + close + `Warning` `DiagCategories.GenaCallbackNoLength`.** **Duplicate `Content-Length` → `400`** + `GenaCallbackMalformed` (NOT last-wins — this one is strict).

**AC-4.1.16 — `Transfer-Encoding: chunked` rejected (AC-4.6 / D4).** → `400` + close + `Warning` `GenaCallbackMalformed`. (Chunked support deferred until a real vendor needs it — out of v1; recorded as a forward follow-up.)

### Valid NOTIFY happy path

**AC-4.1.17 — valid NOTIFY dispatch (AC-4.8 / D4).** On a well-framed request with a valid `Content-Length`-delimited body, the host extracts `SID`, `SEQ`, path-and-query (request-target), and the body bytes, constructs `new NotifyRequest(sid, seq, pathAndQuery, body, DateTime.UtcNow)`, and raises `NotifyReceived`. Handlers are **awaited** (the event is `Func<NotifyRequest, Task>`); the host **tracks in-flight handler tasks** to drain on shutdown. It then writes exactly:
```
200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n
```
back to the device and closes the connection. A `Verbose` `DiagCategories.GenaNotifyReceived` (carrying `Sid`) is emitted (verbose-only per D4 table). The host does **NOT** parse the body as `<e:propertyset>` XML — that is Story 4.2/4.3's job (FR-104).

**AC-4.1.18 — unknown / no-subscriber SID is an idempotent ack (D4).** If `NotifyReceived` has no subscriber (or no handler matches the SID — the host itself does not know SIDs; routing is Story 4.2's concern), the host still returns `200 OK`. The host never 404s a NOTIFY (the device may already be unsubscribed from our side). No special-case logic in 4.1 beyond "raise the event, return 200".

### Error response matrix

**AC-4.1.19 — internal dispatch error → `500`.** If a `NotifyReceived` handler throws (or any unexpected error occurs after framing), the host returns `500 Internal Server Error` + `Connection: close` and emits a `Warning` (with the exception stack in `ErrorText`). The handler-fault must NOT take down the accept loop or other connections (catch per-connection; `OutOfMemoryException` not swallowed — house style).

**AC-4.1.20 — every Warning carries `RemoteEndpoint` (Pattern 11).** Each hardening/error `Warning` populates `DiagnosticContext.RemoteEndpoint` (the client's `IPEndPoint.ToString()`). `DeviceUuid` is **not** known at the callback-host layer (the host sees only an IP:port, not which subscription) — leave it null. This matches the documented context for every `Gena.Callback.*` constant ("Mandatory context: RemoteEndpoint").

### Async cancellation & teardown

**AC-4.1.21 — accept loop + reads honour adapter & app CTS (D3/D7).** The accept loop and every per-connection read are bounded by a private CTS linked to the `ct` passed to `StartAsync` (the adapter token in production — see wiring). `OperationCanceledException` / `ObjectDisposedException` on accept or read is the normal shutdown path (swallowed, loop exits) — mirror `SsdpTransport.ReceiveLoopAsync`. No `.Result` / `.Wait()` (AsyncDisciplineTests / VSTHRD002/003/100 are build-time gates); `ConfigureAwait(false)` on every await in Core.

**AC-4.1.22 — graceful, budgeted `DisposeAsync` (AC-4.9 / D7).** `DisposeAsync` is idempotent (`Interlocked.Exchange` guard), cancels the private CTS, stops the listener (`listener.Stop()` — unblocks the pending `AcceptTcpClientAsync`), then **drains in-flight connection handlers + in-flight `NotifyReceived` handler tasks within a 2 s budget** (`WaitAsync(2s)`; on timeout, force-close and log — mirror `AdapterScope.DisposeAsync`'s `WaitAsync(_switchBudget)` shape). A fresh `StartAsync` on a new adapter constructs a fresh listener — host instances are scope-bound, not long-lived across adapter switches. **Story 5.2's atomic rebind calls this `DisposeAsync`** (forward dependency — keep the 2 s budget aligned with FR-050).

### Test contract (D4 "Test contract" + AC L1574-1579)

**AC-4.1.23 — `FakeGenaClient` raw `TcpClient` driver.** `tests/ohSpy.Core.Tests/Fakes/FakeGenaClient.cs` is a hand-rolled raw `TcpClient` driver that connects to `CallbackBaseUrl`'s host:port and sends / withholds bytes for each AC. It drives every malformed-input + happy-path AC in-process (no real device).

**AC-4.1.24 — `SlowlorisTest` (AC-4.3 + AC-4.7 combined).** Opens 8 connections each trickling 1 byte every 4 s; all 8 hit the 5 s headers timeout and close cleanly; the 9th connection opens immediately after a slot frees. (Use a shrunk `CallbackHeaders` budget via `Configure<HttpTimeoutOptions>` so the test runs in ~ms, not seconds — see Dev Notes test plan.)

**AC-4.1.25 — `FloodTest` (AC-4.7).** Opens 50 connections in a tight loop; 8 are served; 42 are accepted-then-immediately-closed with `GenaCallbackFlood` Warnings; no thread/socket leak (assert listener disposes cleanly, no dangling handler tasks).

## Tasks / Subtasks

- [x] **Task 0 — D4↔D7 cancellation design pass (AC-4.1.9, AC-4.1.21).** Done first (see Completion Notes "Task 0"). Per-read CTS is `CreateLinkedTokenSource(callerToken)` + `CancelAfter(budget)`; on unblock, `TimeoutStream` disambiguates: caller-token-cancelled → rethrow OCE (shutdown, swallowed); else budget fired → `CallbackTimeoutException` sentinel. Shutdown wins over timeout; the two never confuse. `listener.Stop()` unblocks the pending accept; CTS cancel unblocks in-flight reads.
- [x] **Task 1 — Create the `Events/` folder + record + interface (AC-4.1.1, AC-4.1.2).**
  - [x] `src/ohSpy.Core/Events/NotifyRequest.cs` — the sealed record (namespace `ohSpy.Core.Events`).
  - [x] `src/ohSpy.Core/Events/IEventCallbackHost.cs` — the interface (`IAsyncDisposable`, `StartAsync`, `CallbackBaseUrl`, `event Func<NotifyRequest, Task> NotifyReceived`).
- [x] **Task 2 — `TimeoutStream` (AC-4.1.9).** `src/ohSpy.Core/Events/TimeoutStream.cs` wrapping `NetworkStream`; settable `ActiveBudget`; throws `CallbackTimeoutException` on idle-read overrun; honours the linked CTS. `internal sealed`. Unit-tested directly against `HangingStream` (+ a prompt-read `MemoryStream`).
- [x] **Task 3 — `HttpRequestParser` (AC-4.1.10..AC-4.1.16).** `src/ohSpy.Core/Events/HttpRequestParser.cs`, `internal sealed` hand-rolled parser:
  - [x] Request-line parse: exactly-two-SP, uppercase method, CRLF (bare LF ok, bare CR → 400).
  - [x] Header-block parse: case-insensitive → lowercase canonical; empty CRLF terminates; 16 KB cap; 64-header cap; obsolete-fold → 400; duplicate known headers last-wins; unknown headers ignored.
  - [x] `Content-Length`: required (absent → 411), non-negative, ≤ 1 MB (else 413), duplicate → 400.
  - [x] `Transfer-Encoding: chunked` → 400.
  - [x] Emits `HttpRequestParseResult` (discriminated `Success`/`Failure`) so the host maps it to a response + diagnostic in one place.
- [x] **Task 4 — `EventCallbackHost` impl (AC-4.1.3..AC-4.1.8, AC-4.1.17..AC-4.1.22).** `src/ohSpy.Core/Events/EventCallbackHost.cs`, `internal sealed : IEventCallbackHost`:
  - [x] ctor takes `IOptions<HttpTimeoutOptions>` + `IDiagnosticEmitter` (Pattern 7; mirror `UpnpHttpClient`). Internal test ctor adds an injectable drain budget.
  - [x] `StartAsync(adapterIPv4, ct)`: bind `TcpListener(new IPEndPoint(adapterIPv4, 0))`, `Start(16)`, read back port, set `CallbackBaseUrl`, link a private CTS to `ct`, spin the accept loop on `Task.Run` (VSTHRD pragma). Idempotent-start guard (Interlocked → throws on 2nd call).
  - [x] Accept loop: `SemaphoreSlim(8,8)` gate (non-blocking `Wait(0)`); on no free slot → accept + immediate close + `GenaCallbackFlood`; else hand the client to a tracked per-connection handler task.
  - [x] Per-connection handler: `TimeoutStream` wrap (headers budget) → `HttpRequestParser` → success switches to body budget, reads exactly `Content-Length` (leftover-aware) → raise + await `NotifyReceived` → 200; parser failure → mapped status + diagnostic; handler throw → 500; headers/body timeout → close + HeadersTo/BodyTo. Always `Connection: close`. Per-connection catch (not OOM). Slot released in `finally`.
  - [x] `DisposeAsync`: idempotent (Interlocked); cancel CTS; `listener.Stop()`; await accept-loop exit; drain handler tasks within 2 s (`WaitAsync`), else force-close + log.
- [x] **Task 5 — DI registration + adapter-scope wiring (AC-4.1.3, AC-4.1.22).**
  - [x] Registered `IEventCallbackHost`→`EventCallbackHost` as a singleton in `ServiceRegistration.cs` near the `ISsdpTransport` line, with the "lifecycle owned by ShellViewModel/AdapterScope, NOT DI-autostarted" comment.
  - [x] Threaded `IEventCallbackHost` into `ShellViewModel` (new ctor arg, **open Q#2 → ShellViewModel chosen**); started in `RunStartAsync` at the `scope.CurrentAdapterIPv4 is not null` gate (passing `scope.AdapterToken`), before discovery; disposed in `ShellViewModel.DisposeAsync` after the scope. Blast radius / 5.2 note in Completion Notes.
- [x] **Task 6 — `FakeGenaClient` + test suite (AC-4.1.23..AC-4.1.25 + every framing/size/timeout AC).**
  - [x] `tests/ohSpy.Core.Tests/Fakes/FakeGenaClient.cs` raw `TcpClient` driver (connect, send-bytes, send-drip, read-response, wait-for-close) + `ConcurrentCapturingDiagnosticEmitter` (thread-safe for the flood/slowloris races).
  - [x] Happy-path NOTIFY → 200 + `NotifyReceived` raised with parsed SID/SEQ/path/body (AC-4.1.17); unknown-SID idempotent 200 (AC-4.1.18); empty-body 200.
  - [x] Malformed framing (bare CR, three-SP request line, lowercase method, obsolete-fold) → 400 + `GenaCallbackMalformed` (AC-4.1.13/14).
  - [x] Missing CL → 411 + `GenaCallbackNoLength`; duplicate CL → 400; chunked → 400 (AC-4.1.15/16).
  - [x] Oversize headers (>16 KB) → 413; oversize body (CL >1 MB, rejected before buffering) → 413; >64 headers → 400 (AC-4.1.10/11/12).
  - [x] Header-stall + body-underflow timeouts → HeadersTo/BodyTo; `SlowlorisTest` (AC-4.1.24, 8 drip conns + 9th served) + `FloodTest` (AC-4.1.25, 50 conns → ≤8 in-flight + Flood + no leak) using a shrunk-budget `HttpTimeoutOptions`.
  - [x] `DisposeAsync` idempotent + no-op-without-start + drains an in-flight connection within budget + force-closes a slow handler at the (shrunk) budget (AC-4.1.22).
  - [x] Every test carries `[Trait("ac", "AC-4.x")]`.
- [x] **Task 7 — Gate the build.** Core `0/0` (verified); full suite **443 passed / 2 skipped / 0 failed** (baseline 396/2 → **+47** new Events tests, no regressions); chaos hook unchanged (1); `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green (the last UNCHANGED — **no new `DiagCategories` constant**). App build unchanged (1 pre-existing benign `WMC1506` on `MainWindow.xaml:141`).
- [x] **Task 8 — NO manual UI smoke (headless Core infra).** No UI surface, no bound VM state, no `IUiDispatcher` → **no manual-UI-smoke gate** (Story 3.1 posture). The real-device event smoke (a device that actually emits NOTIFY) is a **forward smoke item for Story 4.2/4.3** — it needs a SUBSCRIBE first and an event-emitting device (Linn DS), reachable via the retro Action-I dev-adapter override. Recorded; no smoke task added here.

## Dev Notes

### EXHAUSTIVE-ANALYSIS reconciliation (architecture/epic prose vs SHIPPED code)

Every Epic 2/3 story found the prose diverged from reality. Findings for 4.1, verified against source:

1. **`Events/` does NOT exist yet — this story creates the folder.** `Glob src/ohSpy.Core/Events/**` → no files. The architecture source tree (L2107-2110) lists `IEventCallbackHost.cs`/`EventCallbackHost.cs`, `HttpRequestParser.cs`, `TimeoutStream.cs`, `NotifyRequest.cs` under `Events/` — all to be created here. Namespace: **`ohSpy.Core.Events`**.
2. **Pre-added scaffolding is REAL — cite and use it (no new options, no new diagnostics):**
   - `src/ohSpy.Core/Http/HttpTimeoutOptions.cs` L22-24: `CallbackHeaders` = 5 s, `CallbackBody` = 5 s (comment: "Inbound GENA callback host (Decision 4 — consumed by Story 4.1)"). Also L32 `MaxGenaResponseBytes` = 64 KB (that is the OUTBOUND GENA *response* cap, Story 4.2 — NOT the inbound body cap; the inbound 1 MB body cap is a D4 constant, hard-code `1_048_576` or add a named const in the host. Do NOT reuse `MaxGenaResponseBytes` for the inbound body). Bind via the existing `services.Configure<HttpTimeoutOptions>(...)` line and resolve via `IOptions<HttpTimeoutOptions>`.
   - `src/ohSpy.Core/Diagnostics/DiagCategories.cs` L71-91: **all** the inbound constants already exist — `GenaCallbackMalformed`, `GenaCallbackOversize`, `GenaCallbackNoLength`, `GenaCallbackHeadersTo`, `GenaCallbackBodyTo`, `GenaCallbackFlood`, `GenaNotifyReceived` (comment: "Story 4.1 — pre-added"). Each documents "Mandatory context: RemoteEndpoint". **→ no new `DiagCategories` constant → `DiagCategoriesUsageTests` is reflection-based and stays unchanged.** Hardening-path → constant map:

     | Failure path | HTTP status | Diagnostic constant |
     |---|---|---|
     | Malformed framing (bad request-line, obsolete-fold, dup CL, chunked, >64 hdrs) | `400` | `GenaCallbackMalformed` |
     | Missing `Content-Length` | `411` | `GenaCallbackNoLength` |
     | Oversize headers (>16 KB) or body (CL >1 MB) | `413` | `GenaCallbackOversize` |
     | Headers stalled >5 s | (close) | `GenaCallbackHeadersTo` |
     | Body stalled >5 s | (close) | `GenaCallbackBodyTo` |
     | 9th concurrent connection | (accept+close) | `GenaCallbackFlood` |
     | Internal dispatch error | `500` | *(no dedicated constant — D4 says "Warning with stack"; use the closest existing or `GenaCallbackMalformed` with the stack in `ErrorText`; flag as Open Question)* |
     | Valid NOTIFY | `200` | `GenaNotifyReceived` (Verbose) |

3. **Adapter-IP seam (where the bound IPv4 comes from).** `AdapterScope` (`src/ohSpy.Core/Discovery/AdapterScope.cs`) exposes `public IPAddress? CurrentAdapterIPv4 { get; private set; }` (set after the transport binds, L87) and `public CancellationToken AdapterToken`. `AdapterScope` is `internal sealed`, constructed by `ShellViewModel.StartAsync` (`new AdapterScope(...)`, L52) and started in `RunStartAsync` (L60-77) where `scope.CurrentAdapterIPv4 is not null` is the gate that also starts `DiscoveryService`. **That is the exact point to `await _callbackHost.StartAsync(scope.CurrentAdapterIPv4, scope.AdapterToken)`.** The selected IPv4 originates from `NetworkAdapterEnumerator.Enumerate()` → `NetworkAdapter(c.Name, c.Description, ipv4)` (first `AddressFamily.InterNetwork` unicast address; `AdapterScope.StartAsync` picks `adapters[0]` = FR-048 launch default). Bind the host on this exact `IPAddress` (NOT `IPAddress.Any`/`0.0.0.0` — AC-4.1.3, FR-049 "no Admin"; binding a specific NIC IP needs no URL ACL).
4. **`CallbackBaseUrl` → Story 4.2 hand-off (verified signature).** `IUpnpHttpClient.SubscribeAsync(Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct)` (`src/ohSpy.Core/Http/IUpnpHttpClient.cs` L46-47) takes `callbackUrl` as a `Uri`. So `CallbackBaseUrl` MUST be a `Uri` (it is, per D4). 4.2's `SubscribeAsync` passes `_callbackHost.CallbackBaseUrl` straight through. The path-and-query the device echoes on NOTIFY (`NotifyRequest.PathAndQuery`) lets 4.2 disambiguate which subscription a NOTIFY belongs to **if** it embeds a per-subscription path in the CALLBACK URL — but **4.1 only surfaces the raw path**; routing-by-SID-or-path is 4.2's design, not 4.1's. (Note: `SubscribeResponse` type referenced by `IUpnpHttpClient` is a Story 4.2 concern — out of scope here.)
5. **`NotifyReceived` hand-off seam (4.1 → 4.2/4.3 boundary).** The host raises `event Func<NotifyRequest, Task> NotifyReceived` with the **raw** `NotifyRequest` (SID, SEQ, path, body bytes, ReceivedUtc). It awaits handlers and tracks in-flight tasks to drain on shutdown (D4 L465). **The host does NOT parse `<e:propertyset>`** (D4 L464; epic L1549) — Story 4.2's `SubscriptionClient` subscribes to `NotifyReceived`, filters by SID, parses the body with the Story 1.4 `XmlReaderSettings` discipline, and raises `EventNotification` to the 4.3 popup VM. Confirm this boundary in code: 4.1 ships `byte[] Body`, never an XML model. (Action H / `DeferredUiDispatcher` marshalling is NOT central to 4.1 — the host has no bound VM state and no `IUiDispatcher` — but the eventual 4.3 hand-off will need it; out of scope here.)

### Canonical implementation model: `SsdpTransport`

`src/ohSpy.Core/Discovery/SsdpTransport.cs` is the closest shipped precedent and should be mirrored closely:
- `internal sealed class … : I…` behind the interface; DI registers the interface (Pattern 7).
- Ephemeral-port bind on the adapter IP; `StartAsync` returns `Task.CompletedTask` after binding + spinning background loops via `Task.Run(() => LoopAsync(token), token)`.
- Private `CancellationTokenSource.CreateLinkedTokenSource(ct)` so `DisposeAsync` can tear down even if the caller never cancels.
- Receive loop swallows `OperationCanceledException` / `ObjectDisposedException` as the normal shutdown path; one bad packet/connection does NOT kill the loop (per-iteration try/catch, back-off on hot error).
- Idempotent `DisposeAsync` (`Interlocked.Exchange(ref _disposed, 1)`); cancel CTS → close socket/listener (unblocks pending accept) → `await` loop completion under `#pragma warning disable VSTHRD003` → dispose CTS.
- `internal` test seams exposing the background tasks (e.g. `internal Task? AcceptLoop => _acceptLoop;`) for deterministic test joins.
- `AdapterScope.DisposeAsync` (L97-132) is the budget model: `await x.DisposeAsync().AsTask().WaitAsync(_switchBudget)` with a `TimeoutException` → `AdapterSwitchTimeout` Warning. Mirror the 2 s drain.

### Files to create (all `ohSpy.Core.Events`)

| File | Shape | Notes |
|---|---|---|
| `Events/NotifyRequest.cs` | `public sealed record NotifyRequest(string Sid, long Seq, string PathAndQuery, byte[] Body, DateTime ReceivedUtc)` | Public (consumed by 4.2). D4 L456-461. |
| `Events/IEventCallbackHost.cs` | `public interface IEventCallbackHost : IAsyncDisposable { … }` | Public (4.2 injects it). D4 L449-454. |
| `Events/TimeoutStream.cs` | `internal sealed class TimeoutStream : Stream` (or wrapper) | Active-budget idle-read enforcer. D4 L467-469. |
| `Events/HttpRequestParser.cs` | `internal static`/`internal sealed` | Strict-framing / lenient-headers; emits a parse outcome (success fields OR failure status+category). D4 L420-433. |
| `Events/EventCallbackHost.cs` | `internal sealed class EventCallbackHost : IEventCallbackHost` | `TcpListener`, semaphore gate, per-connection handler, drain. D4 L406-475. |

`InternalsVisibleTo` already grants `ohSpy.Core.Tests` + `ohSpy.App` (`ohSpy.Core.csproj` L17, L19) — `internal` impls are testable and App-resolvable without extra plumbing.

### Security-hardening rationale (D4 L495-501) — this is the FIRST inbound listener

- **Bind specific NIC IP, not `0.0.0.0`** → no URL ACL, no Admin (FR-049); narrows the listen surface to the operator's chosen adapter.
- **Strict framing** closes the real threat surface (slowloris, body-bombs, oversized headers); **lenient headers** absorb real vendor noise (case quirks, ordering, extras). Don't over-reject legitimate-but-quirky NOTIFYs.
- **Connection cap 8 + 5+5 s budgets** bound worst-case occupancy at 80 connection-seconds / 10 s window — comfortable for 5 concurrent subscription popups (FR-036), resistant to floods.
- **1 MB body cap** is well above legitimate GENA payloads (KB to tens of KB) and well below memory-pressure thresholds; reject by `Content-Length` **before** buffering.
- **No keep-alive** simplifies state — GENA NOTIFY is not pipelined; a fresh TCP handshake per NOTIFY is sub-ms on a LAN.
- **Threat model in scope:** broken devices, slowloris, body-bombs, oversized headers, floods. **Out of scope:** TLS (UPnP is plaintext), authenticated attackers (no auth surface), adversarial fuzz (NFR-excluded). Don't gold-plate beyond this.

### Verification posture (Epic 3 retro Action J + H)

- **Headless Core infra → NO manual-UI-smoke gate.** No UI, no bound VM, no `IUiDispatcher`. Same posture as Story 3.1 ("Pure Core, NO UI surface → no manual smoke"). State this explicitly so the dev does not add a smoke task.
- **Strong automated surface (the security contract is the test contract).** In-process raw `TcpClient` (`FakeGenaClient`) feeds canned NOTIFYs + every hardening case: malformed framing, missing/duplicate `Content-Length`, chunked, oversize headers, oversize body, >64 headers, slowloris drip on headers AND body, >8 concurrent connections (`FloodTest`), graceful drain on `DisposeAsync`. No real device required.
- **Real-device event smoke is a FORWARD item for 4.2/4.3** (Action J). End-to-end NOTIFY needs a SUBSCRIBE (Story 4.2) and a device that *emits* events. **Linn DS emits** (confirm whether the Sky IGD emits for `WANIPConnection`). The **Epic 4 kickoff dev-adapter override** (retro Action I — env var `OHSPY_ADAPTER=<name|index>`) is what makes the Linn-DS network reachable for that smoke. Record this; do not block 4.1 on it.
- **Test-budget trick:** the slowloris/timeout tests must run in ms, not 5 s. Inject a shrunk `HttpTimeoutOptions { CallbackHeaders = 100ms, CallbackBody = 100ms }` via `Configure<>` (or a test ctor) so the timeout fires fast. The `FakeGenaClient` drip interval then exceeds the shrunk budget. `HangingStream` (`tests/.../Fakes/HangingStream.cs`) already models a never-completing read for `TimeoutStream` unit tests.

### Async / cancellation discipline (Decision 3 / Pattern 6)

- Fully async: accept loop + every read awaited; **no** `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` (VSTHRD002/003/100 break the build; `AsyncDisciplineTests` documents the rule). `ConfigureAwait(false)` on every Core await.
- The private CTS is linked to the `StartAsync` `ct` (adapter token in prod). Story 5.2's `_adapterCts.Cancel()` + the host's `DisposeAsync` must tear it down cleanly — the accept loop's pending `AcceptTcpClientAsync` is unblocked by `listener.Stop()`; pending reads are unblocked by the linked-CTS cancel (and/or the per-read budget).
- `Task.Run` for the long-running accept loop is legitimate (not sync-over-async) — same justification + `CA2016`/VSTHRD pragma as `SsdpTransport` (L100-105).

### Project structure notes

- New folder `src/ohSpy.Core/Events/` — matches architecture canonical tree L2107-2110 and L1739 (`Events/ # IEventCallbackHost, SubscriptionClient, NotifyRequest (D4)`). `SubscriptionClient` lands later (4.2) in the same folder.
- DI: add the host registration in `ServiceRegistration.cs` near the `ISsdpTransport` line (L69-72) with the "lifecycle owned by AdapterScope/ShellViewModel" comment. **Blast radius:** `ShellViewModel` gains a ctor arg (`IEventCallbackHost`) → update `ShellViewModel` construction in DI (L140) and any `ShellViewModel` test construction sites. (Alternatively thread through `AdapterScope` — heavier, but pre-positions for 5.2; document whichever you choose.)
- The Story 2.8 `Subscribe` context-menu stub label "Subscribe (coming in Epic 4)" + the `FeatureNotImplemented` warning are **Story 4.3's** to remove (epic L1719-1722), NOT 4.1's. Leave them.

### Previous-story / epic intelligence

- **Epic 3 closed** (3/3 done, committed `dfa5b81`/`0c11c8b`/`2a29e84`); baseline **396 passed / 2 skipped**; chaos 1; CoreAppBoundary + AsyncDiscipline + DiagCategoriesUsage green.
- **Retro Action H** (`DeferredUiDispatcher` per async VM path) — folded into **4.3**, not 4.1 (no VM here).
- **Retro Action I** (dev adapter override) — Epic 4 **kickoff prep**, unblocks Linn-DS event smoke for 4.2/4.3; reversible env var.
- **Retro Action J** (front-load GENA event-smoke plan) — captured above: Linn DS emits; the smoke is a 4.2/4.3 forward item.
- **Story 5.2 (adapter switch) is re-sequenced to the END of Epic 4** (after 4.3) and calls **this host's** `DisposeAsync`/`StartAsync` in its atomic rebind — keep the 2 s drain budget aligned with FR-050; prereq is the A23 transport-factory refactor.
- **`SsdpTransport` (Story 2.1)** is the canonical shape; **`AdapterScope` (Story 2.2)** is the budgeted-dispose + adapter-IP source; **`UpnpHttpClient` (1.3)** is the `IOptions<HttpTimeoutOptions>` + `IDiagnosticEmitter` ctor + linked-CTS-per-request shape.

### Open questions for the implementer

1. **Internal-dispatch-error (`500`) diagnostic category.** D4's response table says "`Warning` with stack" but lists **no** `Gena.Callback.*` constant for the 500 path (the pre-added set has Malformed/Oversize/NoLength/HeadersTo/BodyTo/Flood + Notify.Received only). Options: reuse `GenaCallbackMalformed` with the stack in `ErrorText`, or accept that the 500 path is rare (a faulting `NotifyReceived` handler) and log under the nearest fit. **Adding a new constant would break the "no new `DiagCategories`" reconciliation + touch the pinned-set guard** — flagged; recommend NOT adding one. Confirm with the reviewer.
2. **Host wiring seam — `ShellViewModel` vs `AdapterScope`.** Recommended: `ShellViewModel.RunStartAsync` (the bound IP is known there; minimal blast radius). Cleaner for 5.2's atomic rebind would be inside `AdapterScope` (it already owns the transport's Start/Dispose) — but `AdapterScope` is `internal` and has no host field today. Pick one; if `ShellViewModel`, note that 5.2 will need to reach the host through it (or relocate then).
3. **`SEQ` parse leniency.** Confirmed design: absent/unparseable `SEQ` → `0`, no 400 (some stacks omit it on the initial event). Verify against a real Linn DS NOTIFY during the 4.2/4.3 smoke.
4. **`PathAndQuery` content.** Surface the raw request-target verbatim. If 4.2 chooses to embed a per-subscription token in the CALLBACK path, this is where it reads it back — but that's 4.2's call; 4.1 just passes it through.

### References

- [Source: architecture.md#Decision 4 — GENA Callback Host Hardening Contract] (L400-507) — the canonical contract: bind, caps, budgets, framing, header tolerance, response matrix, interface + record, `TimeoutStream`, cascading implications, test contract, AC-4.1..4.9.
- [Source: architecture.md#Cancellation hierarchy] (L743, L758, L786, L823) — adapter scope owns the callback host; adapter switch fires `_adapterCts.Cancel()` → host teardown; D7 step "await EventCallbackHost.DisposeAsync()".
- [Source: architecture.md#Source tree] (L1739, L2107-2110, L2191) — `Events/` folder + the four files; FR mapping 4.10.
- [Source: architecture.md#D4↔D7 open follow-up] (L3031) — 30-min cancellation design pass at story start.
- [Source: epics.md#Story 4.1] (L1493-1579) — story statement, 1.5× sizing note, full AC list (AC-4.1..4.9, framing rules, response shapes, test contract).
- [Source: epics.md#Epic 4 scope] (L1489-1491) — Epic 4 prose.
- [Source: src/ohSpy.Core/Http/HttpTimeoutOptions.cs#L22-24, L32] — pre-added `CallbackHeaders`/`CallbackBody` (5 s each); `MaxGenaResponseBytes` (outbound, do not reuse).
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs#L71-91] — pre-added `Gena.Callback.*` + `GenaNotifyReceived` (no new constant).
- [Source: src/ohSpy.Core/Discovery/AdapterScope.cs#L34-38, L73-90, L97-132] — `CurrentAdapterIPv4`/`AdapterToken` seam + budgeted `DisposeAsync` model.
- [Source: src/ohSpy.Core/Discovery/SsdpTransport.cs] — canonical `internal sealed` listener: ephemeral-port bind, background loops, linked CTS, idempotent budgeted `DisposeAsync`, swallow-OCE-on-shutdown.
- [Source: src/ohSpy.Core/ViewModels/ShellViewModel.cs#L47-101] — `RunStartAsync` (host start point) + `DisposeAsync` (host dispose point).
- [Source: src/ohSpy.Core/Http/IUpnpHttpClient.cs#L46-47] — `SubscribeAsync(Uri eventSubUrl, Uri callbackUrl, …)` — the `CallbackBaseUrl` consumer (Story 4.2).
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs#L69-72] — `ISsdpTransport` registration precedent for the host.
- [Source: epic-3-retro-2026-06-04.md#Action items] — H (DeferredUiDispatcher → 4.3), I (dev adapter override kickoff), J (GENA event-smoke plan), 5.2 re-sequencing.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (dev-story implementation)

### Debug Log References

- Build: `dotnet build src/ohSpy.Core` → 0 warnings / 0 errors. `dotnet build src/ohSpy.App` → 0 errors, 1 pre-existing `WMC1506` (MainWindow.xaml:141). Full solution → same.
- Tests: `dotnet test tests/ohSpy.Core.Tests` → **443 passed / 2 skipped / 0 failed** (baseline 396/2; +47 new Events tests). Guards: `CoreAppBoundaryTests`+`AsyncDisciplineTests`+`DiagCategoriesUsageTests` green (7 passed / 2 skipped in that filtered run). Chaos (`category=chaos`) = 1, unchanged.
- Three analyzer fixes during GREEN: CA2249 (`IndexOf`→`Contains`), and VSTHRD103/CA2016 on the non-blocking `_slots.Wait(0)` (suppressed with justification — it is a zero-timeout try-acquire, never blocks).

### Completion Notes List

- **Task 0 — D4↔D7 cancellation design pass (done first).** Per-connection sequence: accept → `_slots.Wait(0)` gate (no-slot → accept+close+Flood) → `TimeoutStream` wrap (headers budget) → `HttpRequestParser.ParseHeadersAsync` → switch to body budget → read exactly `Content-Length` → raise+await `NotifyReceived` → 200 → close; slot released in `finally`. **Composition:** each `TimeoutStream.ReadAsync` arms `CreateLinkedTokenSource(callerToken).CancelAfter(ActiveBudget)`. On unblock it disambiguates — if `callerToken.IsCancellationRequested` it's a genuine adapter/app shutdown → rethrow `OperationCanceledException` (host swallows, no diagnostic); otherwise the budget timer fired → throw the distinguishable `CallbackTimeoutException` sentinel → host maps to HeadersTo/BodyTo. **Shutdown wins over timeout; the two never confuse.** `listener.Stop()` unblocks the pending `AcceptTcpClientAsync`; CTS cancel unblocks in-flight reads. (Resolves architecture L3031.)
- **Reconciliation confirmed in code:** `Events/` created (5 source files + 2 result/exception types); `HttpTimeoutOptions.CallbackHeaders/CallbackBody` (5 s) consumed via `IOptions<>`; **no new options, no new `DiagCategories` constant** (`DiagCategoriesUsageTests` unchanged). Inbound 1 MB body cap is a host const (`HttpRequestParser.MaxBodyBytes = 1_048_576`) — **NOT** `MaxGenaResponseBytes` (the 64 KB outbound cap). The host binds `new TcpListener(new IPEndPoint(adapterIPv4, 0))` — the **specific NIC IP, ephemeral port, never `0.0.0.0`/`IPAddress.Any`** (FR-049, verified by a test asserting `CallbackBaseUrl.Host == adapterIp` and `!= "0.0.0.0"`).
- **`NotifyReceived` is the RAW hand-off** — `NotifyRequest` carries `byte[] Body`; the host never parses `<e:propertyset>` (4.2/4.3's boundary). `CallbackBaseUrl` is a `Uri` in the exact shape `IUpnpHttpClient.SubscribeAsync(eventSubUrl, callbackUrl, …)` consumes.
- **Hardening cases the tests actually exercise:** flood (50 conns → `≤8` in-flight, `GenaCallbackFlood` warnings, no leak); slowloris (8 drip conns each gap>budget → 8× `GenaCallbackHeadersTo`, 9th served 200 after slots free); header-stall + body-underflow timeouts (HeadersTo/BodyTo, shrunk ms budgets); **oversize body rejected by `Content-Length` BEFORE buffering** (declares 2 MB, sends zero body → 413, proving no buffer); oversize headers >16 KB → 413; >64 headers → 400; bare-CR/three-SP/lowercase-method/obsolete-fold → 400; missing CL → 411; duplicate CL → 400 (strict, not last-wins); chunked → 400; handler-throws → 500 with the accept loop surviving; every `Gena.Callback.*` Warning asserted to carry `RemoteEndpoint` and null `DeviceUuid`.
- **`DisposeAsync` is budgeted + idempotent (verified):** `Interlocked` guard (second call = no-op test); safe no-op when never started (zero-adapter path test); drains an in-flight connection cleanly within budget (no force-close warning); a 30 s slow handler is force-closed at the shrunk 200 ms budget (returns in <5 s, logs the drain-exceeded warning). Mirrors `AdapterScope.DisposeAsync`'s `WaitAsync(budget)` shape. **Story 5.2's atomic rebind calls this** — 2 s default budget aligned with FR-050.
- **Async discipline:** accept loop + every read fully async, `ConfigureAwait(false)` throughout, private CTS linked to the adapter token; no `.Result`/`.Wait()`/blocking (`AsyncDisciplineTests` + VSTHRD gates green). `Task.Run` for the accept loop is the `SsdpTransport` precedent (long-running async I/O, not sync-over-async).
- **Open questions resolved:**
  1. **500 internal-dispatch-error diagnostic.** No new constant added (per the no-new-`DiagCategories` reconciliation). The 500 path emits a `Warning` under **`GenaCallbackMalformed`** with the full exception stack in `ErrorText` (closest existing fit; D4 only said "Warning with stack"). The host's own **drain-exceeded** warning (rare) likewise reuses `GenaCallbackFlood` (both signal callback-host resource pressure). **Reviewer: confirm these two reuses are acceptable vs. adding a constant later.**
  2. **Host wiring seam → `ShellViewModel`** (recommended option). New `IEventCallbackHost` ctor arg; started in `RunStartAsync` at the `CurrentAdapterIPv4 is not null` gate (before discovery, passing `scope.AdapterToken`); disposed in `ShellViewModel.DisposeAsync` after the scope. **No test ctor sites broke** (`ShellViewModel` is DI-resolved only; nothing `new`s it). **5.2 blast radius:** when 5.2's atomic rebind needs per-adapter host teardown/reconstruct, it must reach the host through `ShellViewModel` (or relocate the host into `AdapterScope` then — `AdapterScope` is `internal` with no host field today, so I kept it in `ShellViewModel` for minimal blast radius now).
  3. **`SEQ` leniency** — absent/unparseable → `0`, never a 400 (two parser tests). Verify against a real Linn DS NOTIFY during the 4.2/4.3 smoke.
  4. **`PathAndQuery`** — the request-target surfaced verbatim (test asserts `/sub/abc?token=xyz` round-trips). 4.2 reads back any embedded per-subscription token.
- **NO manual-UI-smoke gate** (headless Core infra, no VM state — Story 3.1 posture). Real-device NOTIFY smoke deferred to Story 4.2/4.3 (needs SUBSCRIBE + an event-emitting device; Linn DS via the Action-I dev adapter override).
- **Follow-ups for the reviewer:** (a) confirm the 500/drain-warning constant reuse (open Q#1); (b) chunked `Transfer-Encoding` is rejected, not supported — forward item if a real vendor needs it (AC-4.1.16 records this); (c) `TimeoutStream` uses a per-read budget timer (idle-time model) not a wall-clock phase cap — documented in its summary; (d) the Story 2.8 "Subscribe (coming in Epic 4)" stub + `FeatureNotImplemented` warning are intentionally **left** (Story 4.3 removes them, not 4.1).

### File List

**Created (Core — `src/ohSpy.Core/Events/`, namespace `ohSpy.Core.Events`):**
- `src/ohSpy.Core/Events/NotifyRequest.cs` — public sealed record (raw hand-off; `byte[] Body`).
- `src/ohSpy.Core/Events/IEventCallbackHost.cs` — public interface (`IAsyncDisposable`, `StartAsync`, `CallbackBaseUrl`, `NotifyReceived`).
- `src/ohSpy.Core/Events/EventCallbackHost.cs` — internal sealed impl (TcpListener, semaphore gate, per-connection handler, budgeted drain).
- `src/ohSpy.Core/Events/HttpRequestParser.cs` — internal sealed hand-rolled HTTP/1.1 header parser (strict framing, lenient headers).
- `src/ohSpy.Core/Events/HttpRequestParseResult.cs` — internal discriminated `Success`/`Failure` parse outcome.
- `src/ohSpy.Core/Events/TimeoutStream.cs` — internal sealed active-budget idle-read enforcer.
- `src/ohSpy.Core/Events/CallbackTimeoutException.cs` — internal sentinel distinguishing budget-overrun from shutdown-cancel.

**Created (Tests — `tests/ohSpy.Core.Tests/`):**
- `tests/ohSpy.Core.Tests/Fakes/FakeGenaClient.cs` — raw `TcpClient` driver (connect/send/drip/read/wait-for-close).
- `tests/ohSpy.Core.Tests/Fakes/ConcurrentCapturingDiagnosticEmitter.cs` — thread-safe capturing emitter (flood/slowloris races).
- `tests/ohSpy.Core.Tests/Events/TimeoutStreamTests.cs` — 4 tests (AC-4.1.9).
- `tests/ohSpy.Core.Tests/Events/HttpRequestParserTests.cs` — 21 tests (AC-4.1.10..AC-4.1.17, AC-4.1.2).
- `tests/ohSpy.Core.Tests/Events/EventCallbackHostTests.cs` — 22 tests (lifecycle, framing, size caps, 500, timeouts, slowloris, flood, drain).

**Modified:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — registered `IEventCallbackHost`→`EventCallbackHost` singleton (+`using ohSpy.Core.Events`).
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — new `IEventCallbackHost` ctor arg; start in `RunStartAsync` at the bound-IP gate; dispose in `DisposeAsync` (+`using ohSpy.Core.Events`).

### Review Findings

Reviewed 2026-06-04 by claude-sonnet-4-6 (bmad-code-review). Verdict: **APPROVED-WITH-MINOR-FIXES** — 3 patches applied, 3 items deferred. Build: Core 0/0, App 1 pre-existing WMC1506. Tests: 443 passed / 2 skipped / 0 failed (post-patch, same count). All five hardening controls verified. Open Q#1 resolved: GenaCallbackMalformed reuse APPROVED.

- [x] [Review][Patch] `_slots.Release()` throws ObjectDisposedException after drain-timeout force-close [EventCallbackHost.cs:155] — **Applied**: wrapped in `try/catch (ObjectDisposedException)` in `TrackConnection` finally block.
- [x] [Review][Patch] Drain-exceeded Warning emitted without DiagnosticContext (AC-4.1.20 Pattern 11) [EventCallbackHost.cs:393] — **Applied**: now passes `new DiagnosticContext()` (empty — host-level event, no specific remote). Note: RemoteEndpoint is null by design for this host-level event; this is the only Gena.Callback.* Warning where null RemoteEndpoint is correct.
- [x] [Review][Patch] Drain-overrun test missing assertion to document null-RemoteEndpoint exception [EventCallbackHostTests.cs:500] — **Applied**: added explicit assertion on drain-warning DiagnosticContext to document and lock the host-level-exception from AC-4.1.20.
- [x] [Review][Defer] Lying Content-Length (declares small, sends more) — no test [EventCallbackHost.cs:302] — deferred, no vulnerability (no keep-alive, host reads exactly CL bytes and closes; extra wire bytes ignored). Safe by design.
- [x] [Review][Defer] Premature EOF on body emits GenaCallbackBodyTo with misleading message "budget exceeded" [EventCallbackHost.cs:305-308] — deferred, documented intentional design (EOF-before-CL treated identically to body stall per story completion notes).
- [x] [Review][Defer] Semaphore slot theoretical leak when token pre-cancelled in TrackConnection [EventCallbackHost.cs:147] — deferred, harmless (slot count inaccuracy only on shutdown when semaphore is disposed anyway; TOCTOU window is sub-millisecond and benign).

**Open Q#1 verdict (GenaCallbackMalformed for 500 path + GenaCallbackFlood for drain-overrun):** APPROVED. Both reuses are semantically defensible and the `message`/`ErrorText` fields distinguish them clearly in log analysis. Adding a new constant would change `DiagCategoriesUsageTests` (pinned-set guard) — not justified for two rare paths. Record for future: a dedicated `GenaCallbackDispatchError` constant when the diagnostics layer is revisited in a later epic.

### Change Log

- 2026-06-04 — Story 4.1 implemented (claude-opus-4-8[1m], dev-story). Created the `Events/` callback host (the first inbound network listener): hardened hand-rolled HTTP/1.1 `NOTIFY` receiver bound to the adapter IP, with connection cap 8, 5+5 s per-phase idle budgets, 16 KB header / 1 MB body caps, strict framing + lenient headers, budgeted idempotent `DisposeAsync`. Wired into `ShellViewModel`/DI. +47 Events tests (396→443 passed / 2 skipped). No new options/constants. Status ready-for-dev → in-progress → review.
- 2026-06-04 — Story 4.1 reviewed (claude-sonnet-4-6, bmad-code-review). 3 patches applied: ODE guard on `_slots.Release()` post-dispose; DiagnosticContext added to drain-overrun Warning; drain-overrun test assertion added. 3 items deferred (safe-by-design lying-CL, EOF mislabelling, semaphore slot TOCTOU). Open Q#1 resolved: constant reuse approved. Status: review → done.
