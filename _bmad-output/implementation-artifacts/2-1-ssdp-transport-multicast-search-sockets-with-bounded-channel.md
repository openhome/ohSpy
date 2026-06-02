# Story 2.1: SSDP Transport — Multicast + Search Sockets with Bounded Channel

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want ohSpy to bind two adapter-specific UDP sockets (a multicast listener on `(adapter_ipv4, 1900)` plus an ephemeral search socket) and feed every received datagram into a bounded channel,
so that subsequent stories have a stable, source-tagged datagram stream to parse — independent of how many devices are announcing and resistant to back-pressure from a slow consumer.

## Acceptance Criteria

**Verbatim ACs from epics.md §Story 2.1 (lines 750–803). AC trait IDs follow Amendment A2 (`AC-2.1.<n>`).**

**AC-2.1.1 — Datagram + Source models (D2)**

**Given** `ohSpy.Core/Models/SsdpDatagram.cs` and `ohSpy.Core/Models/SsdpSource.cs`
**When** I inspect them
**Then** `SsdpDatagram` is a `public sealed record` with `IPEndPoint Remote`, `byte[] Payload`, `DateTime ArrivalUtc`, `SsdpSource Source` (D2)
**And** `SsdpSource` is an `enum { Multicast, SearchResponse }` (D2)

**AC-2.1.2 — Transport interface surface (D2)**

**Given** `ohSpy.Core/Discovery/ISsdpTransport.cs`
**When** I inspect the interface
**Then** it declares `Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)`, `Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)`, `ChannelReader<SsdpDatagram> IncomingDatagrams { get; }`, and is `IAsyncDisposable` (D2)

**AC-2.1.3 — Multicast listener socket setup (D2 / FR-006 / NFR-R5)**

**Given** `ohSpy.Core/Discovery/SsdpTransport.cs` impl
**When** `StartAsync(adapterIPv4, ct)` runs
**Then** the multicast listener socket is created with `AddressFamily.InterNetwork`, `SocketType.Dgram`, `ProtocolType.Udp`
**And** `SocketOptionName.ReuseAddress` is set BEFORE binding (mandatory — coexists with Windows `SSDPSRV`) (D2)
**And** the socket binds to `IPEndPoint(adapterIPv4, 1900)`
**And** it joins the multicast group `239.255.255.250` on `adapterIPv4` via `SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(...))` (D2)

**AC-2.1.4 — Ephemeral search socket setup (D2 / FR-004)**

**Given** the same `StartAsync`
**When** I inspect the second socket
**Then** the ephemeral search socket is created similarly (`AddressFamily.InterNetwork`, `SocketType.Dgram`, `ProtocolType.Udp`)
**And** it is bound to `IPEndPoint(adapterIPv4, 0)` with `MulticastInterface` set to `adapterIPv4`
**And** receive loops on both sockets post datagrams to the channel with the correct `SsdpSource` tag

**AC-2.1.5 — Bounded channel configuration (D2)**

**Given** the bounded channel
**When** I look at its configuration
**Then** it is `Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false })` (D2)
**And** when channel writes reach ≥ 90% capacity a single `Warning` (`DiagCategories.SsdpChannelNearFull`) is emitted (rate-limited so we don't spam — see Task 5.5)
**And** when `DropOldest` actually drops an item a `Warning` (`DiagCategories.SsdpChannelOverflow`) is emitted (rate-limited)

**AC-2.1.6 — `SendMSearchAsync` semantics (FR-004 / FR-022 / FR-053 layer (a))**

**Given** `SendMSearchAsync(mx, ct)`
**When** it runs
**Then** an M-SEARCH datagram is sent via the ephemeral search socket using `ST: upnp:rootdevice`
**And** the MX header carries the supplied TimeSpan (typically 5 s) — value is `(int)mx.TotalSeconds`, clamped ≥ 1
**And** the request egresses on the chosen adapter (because `MulticastInterface` is set)
**And** the datagram is sent to `239.255.255.250:1900` (UDA 1.0 §1.2.2)

**AC-2.1.7 — `DisposeAsync` teardown (D2 / FR-050)**

**Given** `DisposeAsync()`
**When** the transport is torn down
**Then** the multicast group is left cleanly (`DropMembership`)
**And** both sockets are closed
**And** the channel writer completes so the reader observes the close
**And** dispose is idempotent (a second call is a no-op, does not throw)

**AC-2.1.8 — Receive-loop resilience under bad datagrams (FR-039 / NFR-R1)**

**Given** any unhandled `SocketException` during receive
**When** it surfaces
**Then** a `Warning` diagnostic (`DiagCategories.SsdpParse` with `RemoteEndpoint` when applicable) is emitted (FR-039 emission point)
**And** the receive loop continues rather than tearing down the whole transport (NFR-R1 — one bad packet does not kill the session)

**AC-2.1.9 — Cancellation discipline (D7)**

**Given** the transport is running
**When** the `adapterToken` passed to `StartAsync` is cancelled
**Then** both receive loops observe the cancellation and exit cleanly via `OperationCanceledException`
**And** `DisposeAsync` from the caller completes the teardown sequence per AC-2.1.7

**AC-2.1.10 — Integration tests against loopback (Pattern 15 / Amendment A2)**

**Given** the test suite
**When** I run the transport tests
**Then** integration tests against loopback / 127.0.0.1 verify both sockets receive what the test fixture sends
**And** every AC-traceable test carries `[Trait("ac", "AC-2.1.<n>")]` (Amendment A2)
**And** loopback integration tests carry `[Trait("category", "integration")]` so the chaos-hook filter `category=chaos` does NOT pick them up (they run on every `dotnet test`, not just pre-commit — Pattern 15 + Story 1.6 anti-pattern)

## Tasks / Subtasks

### Task 1 — Models: `SsdpDatagram` + `SsdpSource` (AC: #1)

- [ ] **1.1** Create `src/ohSpy.Core/Models/SsdpSource.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  public enum SsdpSource
  {
      Multicast,
      SearchResponse,
  }
  ```
- [ ] **1.2** Create `src/ohSpy.Core/Models/SsdpDatagram.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  using System.Net;

  public sealed record SsdpDatagram(
      IPEndPoint Remote,
      byte[] Payload,
      DateTime ArrivalUtc,
      SsdpSource Source);
  ```
- [ ] **1.3** Both files use file-scoped namespace, one type per file (Pattern 1). `byte[]` is the raw datagram bytes — no parsing in this story (parsing is Story 2.4).
- [ ] **1.4** `DateTime.UtcNow` populated at receive-loop wakeup, NOT here (record is a pure data carrier — Pattern 9).

### Task 2 — Folder + interface: `ISsdpTransport` (AC: #2)

- [ ] **2.1** Create folder `src/ohSpy.Core/Discovery/` (new — does not exist yet).
- [ ] **2.2** Create `src/ohSpy.Core/Discovery/ISsdpTransport.cs`:
  ```csharp
  namespace ohSpy.Core.Discovery;

  using System.Net;
  using System.Threading.Channels;
  using ohSpy.Core.Models;

  /// <summary>
  /// Per-adapter UDP transport for SSDP datagrams. One instance per active
  /// adapter; <see cref="DisposeAsync"/> is part of the FR-050 atomic
  /// adapter-switch sequence (Decision 7).
  /// </summary>
  public interface ISsdpTransport : IAsyncDisposable
  {
      Task StartAsync(IPAddress adapterIPv4, CancellationToken ct);
      Task SendMSearchAsync(TimeSpan mx, CancellationToken ct);
      ChannelReader<SsdpDatagram> IncomingDatagrams { get; }
  }
  ```
- [ ] **2.3** XML doc comments on each member describing pre/post-conditions; reference D2 / D7 inline so future readers find the rationale.

### Task 3 — Impl: `SsdpTransport` socket setup (AC: #3, #4)

- [ ] **3.1** Create `src/ohSpy.Core/Discovery/SsdpTransport.cs`. Class is `internal sealed` (per Pattern 7 the DI registration in App composition uses the interface; the impl is internal). Add `InternalsVisibleTo` is already in place — `ohSpy.Core.csproj` grants visibility to both `ohSpy.Core.Tests` and `ohSpy.App` (no csproj edit needed).
- [ ] **3.2** Ctor takes `IDiagnosticEmitter` only — no `IUiDispatcher` (this is a non-UI transport service; receive loops live on background tasks). Primary constructor is fine (Pattern 8):
  ```csharp
  internal sealed class SsdpTransport(IDiagnosticEmitter diag) : ISsdpTransport
  {
      private const string SsdpMulticastAddressLiteral = "239.255.255.250";
      private const int SsdpPort = 1900;
      private static readonly IPAddress SsdpMulticastAddress =
          IPAddress.Parse(SsdpMulticastAddressLiteral);

      private Socket? _multicastSocket;
      private Socket? _searchSocket;
      private IPAddress? _adapterIPv4;
      private Channel<SsdpDatagram>? _channel;
      private CancellationTokenSource? _runCts;
      private Task? _multicastLoop;
      private Task? _searchLoop;
      private int _disposed;
      // … rate-limit counters per Task 5.5
  }
  ```
- [ ] **3.3** `StartAsync(IPAddress adapterIPv4, CancellationToken ct)` — guard against double-start (`if (_multicastSocket is not null) throw new InvalidOperationException("StartAsync already called")`).
- [ ] **3.4** Build the channel BEFORE the sockets so receive loops can post immediately:
  ```csharp
  _channel = Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(4096)
  {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false,
  });
  ```
- [ ] **3.5** Multicast listener socket — exact sequence (Order matters: `ReuseAddress` must precede `Bind`; `AddMembership` after `Bind`):
  ```csharp
  var mcast = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
  mcast.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
  mcast.Bind(new IPEndPoint(adapterIPv4, SsdpPort));
  mcast.SetSocketOption(
      SocketOptionLevel.IP,
      SocketOptionName.AddMembership,
      new MulticastOption(SsdpMulticastAddress, adapterIPv4));
  _multicastSocket = mcast;
  ```
- [ ] **3.6** Ephemeral search socket — adapter-scoped multicast egress (`MulticastInterface` is set as an `int`-shaped IPv4 address; use `adapterIPv4.GetAddressBytes()` reversed-to-network-order trick OR the simpler `IPAddress.HostToNetworkOrder` form — the canonical pattern is the address-bytes form):
  ```csharp
  var search = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
  search.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
  search.Bind(new IPEndPoint(adapterIPv4, 0));
  // MulticastInterface takes a 4-byte big-endian IPv4 address.
  search.SetSocketOption(
      SocketOptionLevel.IP,
      SocketOptionName.MulticastInterface,
      adapterIPv4.GetAddressBytes());
  _searchSocket = search;
  ```
  **Note:** the `MulticastInterface` option accepts either a `byte[]` (the address-bytes form, which is what we use here) or an `int` (network-order). Both work; address-bytes is more obviously correct.
- [ ] **3.7** Store `_adapterIPv4` for later (`SendMSearchAsync` does not take it as a parameter — it uses the bound adapter).
- [ ] **3.8** Build a run-CTS that links the caller's `ct` to a private CTS so `DisposeAsync` can cancel even if the caller didn't:
  ```csharp
  _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
  ```
- [ ] **3.9** Spawn receive loops via `Task.Run` for each socket; capture them in `_multicastLoop` / `_searchLoop` for `DisposeAsync` to await:
  ```csharp
  _multicastLoop = Task.Run(() => ReceiveLoopAsync(mcast, SsdpSource.Multicast, _runCts.Token));
  _searchLoop    = Task.Run(() => ReceiveLoopAsync(search, SsdpSource.SearchResponse, _runCts.Token));
  ```
  Pattern 6 caveat: `Task.Run` over an async lambda is acceptable HERE because we're starting a long-running background loop — NOT to fake async over sync. The lambdas themselves use proper async I/O.

### Task 4 — Receive loop (AC: #4, #8, #9)

- [ ] **4.1** Implement `ReceiveLoopAsync(Socket s, SsdpSource source, CancellationToken token)` as a `private static async Task` (static — the loop closes over no mutable state beyond the channel writer captured via a parameter or wrapper). Actual shape:
  ```csharp
  private async Task ReceiveLoopAsync(Socket s, SsdpSource source, CancellationToken token)
  {
      var writer = _channel!.Writer;
      // 64 KB buffer per loop — typical SSDP datagrams are < 1500 bytes; we
      // allocate generously to absorb anything reasonable. Per-iteration ToArray()
      // copies only the actually-received bytes so consumers don't see slack.
      var buffer = new byte[64 * 1024];
      var endpoint = new IPEndPoint(IPAddress.Any, 0);

      while (!token.IsCancellationRequested)
      {
          SocketReceiveFromResult result;
          try
          {
              result = await s.ReceiveFromAsync(buffer, SocketFlags.None, endpoint, token)
                              .ConfigureAwait(false);
          }
          catch (OperationCanceledException)
          {
              break; // normal shutdown path
          }
          catch (SocketException sx)
          {
              // FR-039 / NFR-R1: one bad packet does not kill the session.
              diag.Warning(
                  DiagCategories.SsdpParse,
                  "ssdp receive failed",
                  new DiagnosticContext { ErrorText = sx.SocketErrorCode.ToString() });
              // Tiny back-off so a hot-loop bad-state cannot pin the CPU.
              try { await Task.Delay(50, token).ConfigureAwait(false); }
              catch (OperationCanceledException) { break; }
              continue;
          }
          catch (ObjectDisposedException)
          {
              break; // teardown raced
          }

          var remote = (IPEndPoint)result.RemoteEndPoint;
          var payload = new byte[result.ReceivedBytes];
          Buffer.BlockCopy(buffer, 0, payload, 0, result.ReceivedBytes);

          var datagram = new SsdpDatagram(remote, payload, DateTime.UtcNow, source);

          // Bounded channel with DropOldest never blocks the writer.
          var wrote = writer.TryWrite(datagram);
          if (!wrote)
          {
              // TryWrite returns false only if the channel is closed.
              break;
          }

          // Channel near-full / overflow telemetry — see Task 5.5.
          MaybeEmitChannelTelemetry();
      }
  }
  ```
- [ ] **4.2** **Why not pin the buffer per-loop and reuse?** The cost of one 64 KB allocation per datagram is negligible vs. typical SSDP rates (≤ 20 adv/s sustained per NFR target). Premature optimisation. Revisit only if profiling shows GC pressure under chatty-SSDP soak.
- [ ] **4.3** **`ReceiveFromAsync` overload pick:** the modern `(Memory<byte>, SocketFlags, EndPoint, CancellationToken)` overload accepts the cancellation token directly — no manual `RegisterCallback` shenanigans (Pattern 6 — token threaded through every async).
- [ ] **4.4** Use `ConfigureAwait(false)` on every `await` (Pattern 6 — Core library convention).
- [ ] **4.5** Do not log every received datagram — SSDP traffic is noisy and `DiagSeverity.Verbose` is appropriate only at the parser layer (Story 2.4). The transport layer is silent on the happy path.

### Task 5 — `SendMSearchAsync` + channel-fill telemetry (AC: #5, #6)

- [ ] **5.1** Implement `SendMSearchAsync(TimeSpan mx, CancellationToken ct)`:
  ```csharp
  public async Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)
  {
      if (_searchSocket is null || _adapterIPv4 is null)
          throw new InvalidOperationException("SendMSearchAsync called before StartAsync");

      var mxSeconds = Math.Max(1, (int)mx.TotalSeconds);
      var bytes = BuildMSearchPayload(mxSeconds);
      var dest  = new IPEndPoint(SsdpMulticastAddress, SsdpPort);

      await _searchSocket.SendToAsync(bytes, SocketFlags.None, dest, ct).ConfigureAwait(false);
  }
  ```
- [ ] **5.2** `BuildMSearchPayload(int mx)` returns ASCII bytes for the wire payload exactly per UDA 1.0 §1.2.2. CRLF line endings, blank line terminator:
  ```text
  M-SEARCH * HTTP/1.1\r\n
  HOST: 239.255.255.250:1900\r\n
  MAN: "ssdp:discover"\r\n
  MX: <mxSeconds>\r\n
  ST: upnp:rootdevice\r\n
  \r\n
  ```
  Implementation:
  ```csharp
  private static byte[] BuildMSearchPayload(int mxSeconds)
  {
      var text =
          "M-SEARCH * HTTP/1.1\r\n" +
          "HOST: 239.255.255.250:1900\r\n" +
          "MAN: \"ssdp:discover\"\r\n" +
          $"MX: {mxSeconds}\r\n" +
          "ST: upnp:rootdevice\r\n" +
          "\r\n";
      return System.Text.Encoding.ASCII.GetBytes(text);
  }
  ```
  **Why ASCII not UTF-8:** SSDP / HTTP framing is strict ASCII. UTF-8 incidentally matches for these bytes, but ASCII is the documentation-correct encoding and makes intent explicit.
- [ ] **5.3** `MAN` header value MUST include the surrounding double quotes per RFC and UDA — `"ssdp:discover"` not `ssdp:discover`. Devices reject unquoted MAN.
- [ ] **5.4** **Why no `ST: ssdp:all`?** D2 + FR-004 + FR-053 layer (a) explicitly: we only ever ask for `upnp:rootdevice`. The architecture's root-only-registration enforcement starts here at the wire — sending `ssdp:all` would invite embedded-device responses we'd have to filter out downstream.
- [ ] **5.5** Channel telemetry — rate-limited emission. Naive "emit every time" is a diagnostic flood; emit at most once per second per category:
  ```csharp
  private long _lastNearFullTicks;
  private long _lastOverflowTicks;
  private const long TelemetryIntervalTicks = TimeSpan.TicksPerSecond;

  private void MaybeEmitChannelTelemetry()
  {
      var reader = _channel!.Reader;
      var capacity = 4096;
      // Channel.Reader.Count is available on bounded channels.
      var count = reader.Count;
      if (count >= capacity * 9 / 10)
      {
          MaybeEmitOnce(ref _lastNearFullTicks, DiagCategories.SsdpChannelNearFull,
              "ssdp channel near full");
      }
  }

  private void MaybeEmitOnce(ref long lastEmitTicks, string category, string message)
  {
      var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
      var last = Interlocked.Read(ref lastEmitTicks);
      if (now - last < TelemetryIntervalTicks) return;
      if (Interlocked.CompareExchange(ref lastEmitTicks, now, last) != last) return;
      diag.Warning(category, message);
  }
  ```
  **Overflow signal — where does it come from?** `BoundedChannelOptions.FullMode = DropOldest` does NOT raise an event when it drops. The only way to observe a drop is to detect the condition: a `TryWrite` will succeed (DropOldest semantics) but `Reader.Count` was at capacity *before* the write. Implementation pattern:
  ```csharp
  // Just before TryWrite:
  if (writer.TryWrite(datagram))
  {
      if (_channel!.Reader.Count >= capacity)
      {
          MaybeEmitOnce(ref _lastOverflowTicks, DiagCategories.SsdpChannelOverflow,
              "ssdp channel overflow — oldest dropped");
      }
  }
  ```
  **Pragmatic adjustment to the spec:** the architecture says "when `DropOldest` actually drops an item". The above approximation is the best we can do without a custom channel implementation; document this limitation in the dev-agent record. Race: a write can happen between the count-check and the next iteration; we'd see overflow signal a write or two later than reality. That's fine for telemetry.

### Task 6 — `DisposeAsync` teardown (AC: #7)

- [ ] **6.1** Make dispose idempotent + cancellation-safe:
  ```csharp
  public async ValueTask DisposeAsync()
  {
      if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

      // 1. Signal the receive loops to exit.
      try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }

      // 2. Leave the multicast group cleanly (before closing the socket).
      if (_multicastSocket is not null && _adapterIPv4 is not null)
      {
          try
          {
              _multicastSocket.SetSocketOption(
                  SocketOptionLevel.IP,
                  SocketOptionName.DropMembership,
                  new MulticastOption(SsdpMulticastAddress, _adapterIPv4));
          }
          catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
          {
              // Tolerate teardown races; we're shutting down anyway.
          }
      }

      // 3. Close both sockets — also causes any in-flight ReceiveFromAsync to throw.
      try { _multicastSocket?.Dispose(); } catch { /* tolerated */ }
      try { _searchSocket?.Dispose(); }    catch { /* tolerated */ }

      // 4. Await loop completion so we don't leave background tasks dangling.
      try { if (_multicastLoop is not null) await _multicastLoop.ConfigureAwait(false); }
      catch { /* loops swallow their own non-cancellation errors per AC-2.1.8 */ }
      try { if (_searchLoop is not null) await _searchLoop.ConfigureAwait(false); }
      catch { /* same */ }

      // 5. Complete the writer so the reader observes the close.
      _channel?.Writer.TryComplete();

      // 6. Dispose the run-CTS.
      _runCts?.Dispose();
  }
  ```
- [ ] **6.2** **Why `DropMembership` before `Close`?** Closing the socket implicitly leaves any joined groups, but doing it explicitly is the documented, well-behaved sequence (recommended by Microsoft socket docs). Some routers also notice the explicit IGMP-LEAVE and stop forwarding multicast to that adapter immediately.
- [ ] **6.3** Bare `catch` is rare in Core (Pattern 10 — three places only). Teardown is one of those places by precedent (`DiagnosticFileSink` drain loop is another); the `Exception` filter narrows to socket-disposal races. Re-evaluate if the dev-agent finds a more typed catch fits.
- [ ] **6.4** Tests should be able to call `DisposeAsync` twice without exception (AC-2.1.7 idempotence).

### Task 7 — DI registration (Pattern 7)

- [ ] **7.1** Add to `src/ohSpy.App/Composition/ServiceRegistration.cs` — register as singleton:
  ```csharp
  s.AddSingleton<ISsdpTransport, SsdpTransport>();
  ```
- [ ] **7.2** **Verify** the line is added inside the `RegisterServices` extension method, ordered alphabetically near the existing `IUpnpHttpClient` registration. Build-and-run nothing changes at the App level yet (no consumer wires the transport until Story 2.4 — `DiscoveryService`).
- [ ] **7.3** **Don't** add the transport to `App.xaml.cs` startup — the lifecycle is owned by `AdapterScope` (Story 2.2). Registration only.

### Task 8 — Tests: loopback integration tests (AC: #10, plus AC-2.1.1..9)

**Location:** `tests/ohSpy.Core.Tests/Discovery/SsdpTransportTests.cs` (mirror-tree, Pattern 5).
Carry `[Trait("category", "integration")]` (Pattern 14) AND per-test `[Trait("ac", "AC-2.1.<n>")]` (Amendment A2).
**Do NOT carry `[Trait("category", "chaos")]`** — these are not chaos tests; chaos is for misbehaving-network scenarios with non-trivial wall-clock cost.

Recommended test surface:

- [ ] **8.1** `Datagram_Record_HasD2Shape_AC211` — reflection over `SsdpDatagram` confirms `IPEndPoint Remote`, `byte[] Payload`, `DateTime ArrivalUtc`, `SsdpSource Source`; confirm `sealed record`.
- [ ] **8.2** `Source_Enum_HasMulticastAndSearchResponseOnly_AC211` — assert exactly two enum values.
- [ ] **8.3** `Interface_DeclaresStartSendIncomingDispose_AC212` — reflection over `ISsdpTransport` confirms surface + `IAsyncDisposable`.
- [ ] **8.4** **Loopback receive test (multicast leg):** spin up a `SsdpTransport`, call `StartAsync(IPAddress.Loopback, ct)`, send a canned NOTIFY payload from a second loopback UDP socket to `239.255.255.250:1900`, read one `SsdpDatagram` from `IncomingDatagrams`. Assert `Source == Multicast`, `Remote.Address` matches the sender, `Payload` matches the canned bytes.

  **Loopback caveat for the dev agent:** joining the SSDP multicast group on `127.0.0.1` requires the loopback interface to support multicast. **This is OS-dependent and CAN flake on Windows hosts that haven't been configured for loopback multicast.** Recommended alternatives if loopback multicast doesn't deliver:
    1. Use `IPAddress.Loopback` for the bind but send to the multicast group via a sender bound to `IPAddress.Any` with the `MulticastInterface` set to loopback.
    2. **Fallback:** bind both transport AND sender to a real adapter address from `NetworkInterface.GetAllNetworkInterfaces()` filtered to up + IPv4 + non-loopback. This is more realistic but introduces machine-dependence (Linn dev machines all have a real adapter; CI doesn't exist per Decision 12, so this is OK).
    3. **If both fail on the dev agent's machine:** document the flake in Completion Notes + tag the test `[Trait("category", "integration")]` (already done) + add a `[Fact(Skip = "loopback multicast not available — see Completion Notes")]` placeholder. **Don't ship a broken test — surface the limitation honestly.**
- [ ] **8.5** **Loopback receive test (search-response leg):** call `SendMSearchAsync(TimeSpan.FromSeconds(1), ct)`, have a second loopback UDP socket bound to `IPAddress.Loopback:1900` listen for the M-SEARCH (the second socket is acting as a fake device). Read the payload; assert it contains `ST: upnp:rootdevice`, `MAN: "ssdp:discover"` (quoted), `MX: 1`, `HOST: 239.255.255.250:1900`. **This test does not require multicast delivery — the second socket can be bound to receive from `239.255.255.250` OR (simpler) the test can read the search-socket's send by other means.** The simplest assertion is "we constructed and sent a payload with the right shape" — capture the payload via `BuildMSearchPayload` exposed as `internal static` and unit-tested directly.
- [ ] **8.6** **M-SEARCH payload unit test (no sockets):** make `BuildMSearchPayload` `internal static` (Core grants `InternalsVisibleTo("ohSpy.Core.Tests")` already from Story 1.3), test the byte exactness for a known MX value. This is the strongest AC-2.1.6 assertion — much more reliable than a socket-level test, and the bytes are exactly what we put on the wire.
- [ ] **8.7** **Diagnostic-emission test for `SocketException` path (AC-2.1.8):** induce a `SocketException` by closing the socket mid-receive (the canonical pattern: start transport, dispose `_multicastSocket` via reflection or test hook, observe a Warning with `DiagCategories.SsdpParse`). **Pragmatic substitute** if internal-socket disposal proves brittle: hand the receive loop a deliberately broken socket (impl note: leave room for a test-hook seam if the dev-agent finds this is the cleanest path — but don't over-engineer; if the integration test above naturally triggers the path during teardown, that's sufficient).
- [ ] **8.8** **`DisposeAsync` idempotence test (AC-2.1.7):** call `DisposeAsync` twice on a started transport; second call returns without throwing; `IncomingDatagrams.Completion` is completed.
- [ ] **8.9** **Cancellation-from-caller test (AC-2.1.9):** start transport with a CTS, cancel the CTS without calling `DisposeAsync`, await ≤ 500 ms — assert both `_multicastLoop` and `_searchLoop` are completed (Faulted or Canceled both acceptable; Faulted via swallowed `OperationCanceledException` is what the spec's loop body produces).
- [ ] **8.10** Use `CapturingDiagnosticEmitter` (Story 1.3 fake, `tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs`) for emission assertions.

### Task 9 — Final verification (AC: all)

- [ ] **9.1** `dotnet build` succeeds with `0 Warning(s), 0 Error(s)` under `TreatWarningsAsErrors=true`. Expect to need explicit `using` directives for `System.Net`, `System.Net.Sockets`, `System.Threading.Channels`, `System.Threading.Tasks`, `ohSpy.Core.Models`, `ohSpy.Core.Diagnostics`.
- [ ] **9.2** `dotnet test` reports green. Story 1.6 left 126 tests (124 passing + 2 documented skipped). Story 2.1 adds ~10 tests; target ~136.
- [ ] **9.3** `dotnet test --filter "category=chaos"` still runs the existing 1 Story 1.6 chaos test (and passes). **Story 2.1 adds NO chaos tests** — the chaos suite stays at 1 for now. Adding chaos for SSDP malformed frames is the natural Story 2.4 (`SsdpParser`) follow-up; Murat called it out in Story 1.6 dev notes line 704–706.
- [ ] **9.4** NetArchTest boundary tests (Story 1.6 `CoreAppBoundaryTests`) still pass — `SsdpTransport` lives in Core and references only BCL + `ohSpy.Core.Diagnostics`/`ohSpy.Core.Models` (no WinUI / WindowsAppSDK / WinRT.Interop / `ohSpy.App`).
- [ ] **9.5** **Smoke against a real device on Simon's LAN — OPTIONAL, NOT REQUIRED for AC.** If the dev agent's machine is on Simon's UPnP-equipped network and the dev agent wants a final confidence check: write a one-off `Program.Main` (NOT committed) that wires up `SsdpTransport` to `NetworkInterface.GetAllNetworkInterfaces()` first eligible IPv4 adapter, calls `StartAsync` + `SendMSearchAsync(5s)`, reads from `IncomingDatagrams` for 10 s, prints what came back. Should see Linn DS / DLNA renderer / IGD router / etc. announcements. Document any surprises in Completion Notes. **Don't commit the smoke runner** — it's not test infrastructure.

## Dev Notes

### Architectural pillars this story implements

| Architecture decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **Decision 2 — SSDP Socket Topology** | Two-socket model (multicast listener + ephemeral search), bounded channel `DropOldest(4096)`, near-full + overflow telemetry, `MulticastInterface` for adapter-scoped egress | AC-2.1.1–7, 10 |
| **Decision 7 — Cancellation hierarchy** | Run-CTS linked to adapter-level token passed in via `StartAsync(ct)`; loops observe cancellation; teardown is idempotent | AC-2.1.9 |
| **Pattern 6 — async discipline** | All I/O async; `ConfigureAwait(false)` in Core; cancellation token threaded; no `.Result` / `.Wait()` | AC-2.1.4, 5, 6, 9 |
| **Pattern 11 / D8 — DiagCategories usage** | Pre-existing `Ssdp.Parse`, `Ssdp.Channel.NearFull`, `Ssdp.Channel.Overflow` constants used exactly (no inline strings) | AC-2.1.5, 8 |
| **Amendment A2 — AC trait shape** | Every test carries `[Trait("ac", "AC-2.1.<n>")]` | AC-2.1.10 |
| **FR-004 / FR-022 / FR-053 (a)** | `M-SEARCH` payload with `ST: upnp:rootdevice` only; quoted `MAN: "ssdp:discover"` | AC-2.1.6 |
| **NFR-R1 / NFR-R5** | One bad packet does not kill the session; receive-loop swallow + diagnostic emit + back-off | AC-2.1.8 |

### What this story does NOT do (scope discipline)

- **Does NOT parse SSDP datagrams.** That's Story 2.4 (`SsdpParser`). This story produces raw `byte[]` payloads tagged with `IPEndPoint` + arrival time. The consumer is the channel reader — `DiscoveryService` in Story 2.4.
- **Does NOT enumerate network adapters.** That's Story 2.2 (`NetworkAdapterEnumerator`). Story 2.1 takes an `IPAddress` parameter from the caller and binds to it.
- **Does NOT register with `AdapterScope`.** That's also Story 2.2. The DI registration in Task 7 just makes the type resolvable; the lifecycle owner is Story 2.2's `AdapterScope`.
- **Does NOT add new `DiagCategories` constants.** `Ssdp.Parse`, `Ssdp.Channel.NearFull`, `Ssdp.Channel.Overflow` already exist in `src/ohSpy.Core/Diagnostics/DiagCategories.cs` (pre-added in Story 1.5 line 21–29 of that file; commit `155601b`).
- **Does NOT add new packages.** All needed types are in BCL: `System.Net.Sockets.Socket`, `System.Net.IPAddress`, `System.Threading.Channels.Channel`, `System.Threading.Tasks.Task`. No `Directory.Packages.props` changes.
- **Does NOT touch `App` code beyond one DI registration line.** Pattern 2 boundary holds — all SSDP transport code is in Core.
- **Does NOT extend `FakeUpnpDeviceBehavior`.** That fixture is for HTTP testing (Kestrel-based, Story 1.6); SSDP testing uses raw UDP sockets in tests.
- **Does NOT add chaos tests.** Murat's note in Story 1.6 dev-notes line 704–706: SSDP chaos is the natural follow-up at Story 2.4 (when there's a parser to misbehave). Story 2.1's loopback-integration tests are sufficient infrastructure coverage.

### Previous-story intelligence — what to reuse and what to copy from

**Story 1.5 (`Diagnostics`):**
- `IDiagnosticEmitter` already injected in many places; ctor pattern is established (`internal sealed class X(IDiagnosticEmitter diag) : IX`). Use it.
- `DiagCategories.SsdpParse / SsdpChannelNearFull / SsdpChannelOverflow` constants are LIVE in `src/ohSpy.Core/Diagnostics/DiagCategories.cs:22–29`. Don't reinvent.
- `DiagnosticContext` shape (`readonly record struct` with nullable fields) — use `RemoteEndpoint` for `Ssdp.Parse` per Pattern 11 table (architecture line 1919); leave channel categories with `(none beyond message)` per the same table.

**Story 1.6 (`FakeUpnpDevice`, NetArchTest):**
- **Per-test fixture pattern, not `IClassFixture`** — Story 1.6 dev notes line 669–676 explicitly preferred this. Carry forward: each transport test does `await using var transport = new SsdpTransport(emitter); await transport.StartAsync(...);`.
- **`CapturingDiagnosticEmitter`** at `tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs` is the canonical test emitter — use it; don't create a new one.
- **Chaos-hook filter syntax (A18 lesson):** if you accidentally add `[Trait("category", "chaos")]` to a fast loopback test, the pre-commit hook will block on it. Don't. Loopback integration tests are `[Trait("category", "integration")]`, NOT `chaos`. Story 1.6 dev notes line 790 calls this out as an anti-pattern.
- **NetArchTest enforcement is LIVE** — `CoreAppBoundaryTests` will fail the build if `SsdpTransport.cs` accidentally imports `Microsoft.UI.*`, `Microsoft.Windows.*`, `WinRT.Interop.*`, or `ohSpy.App.*`. Keep imports to BCL + `ohSpy.Core.*`.

**Story 1.3 (`UpnpHttpClient`):**
- The "options bundle for timeouts" pattern is well-established (`HttpTimeoutOptions`). Story 2.1 does **not** introduce an `SsdpTransportOptions` — the only "knob" worth exposing (channel capacity 4096) is baked into D2's constants. If a future story wants to tune it, that becomes an A-amendment.
- `InternalsVisibleTo("ohSpy.Core.Tests")` and `InternalsVisibleTo("ohSpy.App")` are already on `ohSpy.Core.csproj`. Story 2.1 needs NO new InternalsVisibleTo edits.

### Epic 1 retro carry-forwards (`epic-1-retro-2026-06-02.md`)

- **"Spec-skeleton C# longer than ~50 lines is fragile."** The skeletons in Tasks 3 + 4 + 6 above stay within or just over that target deliberately; the dev agent should still compile each as written and flag any defects before mass adoption. Five real bugs were caught across Stories 1.4/1.5/1.6 from spec-skeleton copy-paste errors.
- **"Trivially passing is a red flag."** If the loopback multicast tests yield "0 received in 5 s" without obvious cause, that is NOT acceptance — diagnose (firewall, IPv4-only assumption, loopback multicast policy) before checking in.
- **`PackageReference + FrameworkReference + transitive-pin` checklist.** Story 2.1 adds NO packages — but if the dev agent finds they want `System.Threading.Channels` as an explicit reference, note: it's part of `net10.0` BCL since .NET Core 3.0; no PackageReference needed.
- **FluentAssertions is at 7.2.0 (MIT)** as of the epic-1 retrospective. Tests use it freely.

### Code-style + pattern compliance (citable rulebook)

- **Pattern 1 (naming):** file-scoped namespace, one type per file, PascalCase types, `_camelCase` private fields, `Async` suffix.
- **Pattern 2 (Core ↔ App):** `Discovery/SsdpTransport.cs` lives in Core. Backstopped by Story 1.6's NetArchTest `CoreAppBoundaryTests`.
- **Pattern 6 (async):** `ConfigureAwait(false)` on every `await`. No `.Result` / `.Wait()`. `CancellationToken ct` as last parameter on public async methods. `Microsoft.VisualStudio.Threading.Analyzers` is build-time backstop.
- **Pattern 7 (DI):** singleton lifetime, registered via `ServiceRegistration.cs`; per-entity types (none in this story) constructed by parent.
- **Pattern 8 (constructors):** primary constructor preferred for straight DI.
- **Pattern 9 (records vs classes):** `SsdpDatagram` is `public sealed record`; `SsdpSource` is `public enum`; `SsdpTransport` is `internal sealed class`.
- **Pattern 10 (exceptions):** narrowest catch wins; bare `catch` only in teardown + the three documented places (this story's `DisposeAsync` is one of them).
- **Pattern 11 (`DiagnosticContext` discipline):** `Ssdp.Parse` requires `RemoteEndpoint` (per architecture line 1919) — comply when emitting. `Ssdp.Channel.*` requires nothing beyond the message (architecture line 1920).
- **Pattern 12 (message grammar):** sentence case, terse, ASCII only, no trailing punctuation. `"ssdp channel near full"`, `"ssdp channel overflow — oldest dropped"`, `"ssdp receive failed"`.
- **Pattern 14 + 15 (test naming + AC traceability):** `MethodUnderTest_Scenario_Expected_ACxxx`. `[Trait("category", "integration")]` for loopback tests; `[Trait("ac", "AC-2.1.<n>")]` always (Amendment A2).

### Anti-patterns to avoid

- **Don't bind the multicast socket to `IPAddress.Any`.** D2 explicitly requires adapter-specific bind. Binding to `Any` defeats FR-048's single-adapter constraint and breaks the FR-050 atomic-rebind sequence.
- **Don't skip `ReuseAddress` — and don't set it AFTER `Bind`.** The order is documented: option-set first, bind second. Windows `SSDPSRV` already holds `*:1900`; without `ReuseAddress` set before `Bind`, you get `WSAEADDRINUSE` (10048). With it set after Bind, the option doesn't take effect.
- **Don't use `Socket.MulticastLoopback`.** Default is enabled; explicit set to true is a no-op (and explicit set to false would break loopback integration tests).
- **Don't allocate a new buffer per `ReceiveFromAsync` — but DO copy the received bytes out before re-using.** The skeleton above uses a per-loop 64 KB buffer + per-datagram `Buffer.BlockCopy` of `result.ReceivedBytes` into a fresh `byte[]`. Avoid the GC pressure of a 64 KB allocation per datagram AND avoid handing consumers a slack-padded buffer.
- **Don't use `Encoding.UTF8` for the M-SEARCH payload.** SSDP / HTTP framing is ASCII. UTF-8 happens to produce the same bytes for these characters, but ASCII is intent-correct.
- **Don't add `Socket.IOControl(IOControlCode.NewIfaceAddrChange, ...)`.** Adapter-change detection is FR-050's job, surfaced via `NetworkChange.NetworkAddressChanged` in Story 2.2. Story 2.1 stays adapter-agnostic — caller passes the IP.
- **Don't expose `BuildMSearchPayload` as `public`.** `internal static` is correct — tests need it (InternalsVisibleTo grants access), production callers don't.
- **Don't use a `BlockingCollection<T>` instead of `Channel<T>`.** D2 picks `Channel` deliberately — `BlockingCollection` is sync-blocking under the hood; `Channel` is the canonical async-channel primitive in modern .NET.
- **Don't catch `Exception` in the receive loop without a guard clause.** Pattern 10 + Story 1.6's NetArchTest scrutinise this. Catch `SocketException` and `ObjectDisposedException` specifically; let everything else propagate (it shouldn't happen in steady state, and if it does we want to know).
- **Don't call `_channel.Reader.ReadAsync` anywhere in `SsdpTransport`.** The transport is a producer only. The consumer (`DiscoveryService` in Story 2.4) owns the reader.
- **Don't put `[Trait("category", "chaos")]` on Story 2.1's loopback tests.** Chaos is for misbehaving-device scenarios with non-trivial wall-clock cost — Story 1.6's HTTP body hang, future SSDP malformed-frame tests at Story 2.4. Story 2.1's tests are fast and run on every `dotnet test`. Carrying `chaos` would block the pre-commit hook on every commit.

### Forward-looking dependencies — what Stories 2.2–2.4 need from us

| Story | What it consumes from 2.1 |
|---|---|
| 2.2 (`NetworkAdapterEnumerator` + `AdapterScope`) | `ISsdpTransport.StartAsync(adapterIPv4, adapterToken)` called from `AdapterScope`; `DisposeAsync` participates in atomic adapter-switch sequence (D7 step 2) |
| 2.3 (`DescriptionFetchState` + `EagerDescriptionDispatcher`) | Indirect — not 2.1-direct; consumes the parsed SSDP announcement from 2.4 |
| 2.4 (`SsdpParser` + `DiscoveryService` wire-into-registry) | `IncomingDatagrams` ChannelReader — consumes raw `SsdpDatagram` records, parses into `SsdpAnnouncement`, routes |
| 5.2 (Atomic adapter switch UI) | `DisposeAsync` is the step-2 teardown call in D7's adapter-switch sequence; expect to be invoked from `ShellViewModel.SwitchAdapterAsync` via the `AdapterScope`-owned reference |

### Architecture amendments to anticipate

Stories with amendments so far: 1.1 → A6/A7/A8, 1.3 → A9/A10/A11, 1.5 → A14, 1.6 → A16/A18. Stories without: 1.2, 1.4. Story 2.1 is the first "real protocol" story (sockets on the network — though loopback-tested). **Candidates to flag in Completion Notes if encountered:**

- **A19 (speculative)** — If `Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addressBytes)` doesn't behave on Windows as the architecture assumes (i.e. the egress doesn't follow the adapter), document the alternative API form (`int`-shaped network-order address) and amend D2 with the correct invocation.
- **A20 (speculative)** — If loopback multicast doesn't deliver on a clean Windows 11 dev box, document the bind-to-real-adapter test pattern as the recommended approach for the SSDP test family. Amend D2's "test contract" prose to match.
- **A21 (speculative — Murat-flagged)** — The "near-full / overflow" rate-limit is invented in this story (D2 says "emit a Warning" without specifying cadence). If 1 Hz proves too chatty or too sparse during real device traffic, document the tune in Completion Notes; amend D2 with the chosen cadence.

These are *candidates*, not promises. If implementation goes clean, no amendment is needed.

### Project Structure Notes

**Files this story creates (4):**

```
src/ohSpy.Core/
├── Models/
│   ├── SsdpDatagram.cs                          ← Task 1.2 NEW
│   └── SsdpSource.cs                            ← Task 1.1 NEW
└── Discovery/                                   ← NEW folder
    ├── ISsdpTransport.cs                        ← Task 2.2 NEW
    └── SsdpTransport.cs                         ← Tasks 3–6 NEW

tests/ohSpy.Core.Tests/
└── Discovery/                                   ← NEW folder
    └── SsdpTransportTests.cs                    ← Task 8 NEW
```

**Files this story modifies (1):**

- `src/ohSpy.App/Composition/ServiceRegistration.cs` — one new `AddSingleton<ISsdpTransport, SsdpTransport>()` line (Task 7).

**Files this story does NOT modify:**

- `Directory.Build.props`, `Directory.Packages.props` — no new pins.
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` — `SsdpParse`, `SsdpChannelNearFull`, `SsdpChannelOverflow` already exist.
- `src/ohSpy.Core/ohSpy.Core.csproj` — no new PackageReference (Channels + Sockets are in BCL); `InternalsVisibleTo` already grants test + App access.
- `.githooks/pre-commit` — Story 1.6 fixed it (A18); Story 2.1 inherits the working hook.
- Any `MainWindow.xaml`, ViewModels, `App.xaml.cs` — not in scope; consumer wiring lands in Stories 2.2 / 2.4.

### Testing standards summary

- xUnit + FluentAssertions 7.2.0 (MIT) — both pinned via `Directory.Packages.props`.
- **Each AC-traceable test carries `[Trait("ac", "AC-2.1.<n>")]`** (Amendment A2).
- **Loopback integration tests carry `[Trait("category", "integration")]`** (Pattern 14) — NOT `chaos`. Chaos suite remains size-1 from Story 1.6.
- **Per-test transport instance** via `await using` — no `IClassFixture` (Story 1.6 dev-notes line 669–676 set this precedent).
- Use `CapturingDiagnosticEmitter` (`tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs`) for emission assertions.
- **Test names follow Pattern 14:** `MethodUnderTest_Scenario_Expected_ACxxx`. AC ID embedded in name per Pattern 15.
- **`dotnet test` total target: ~136** (Story 1.6 left 124 passing + 2 skipped = 126; Story 2.1 adds ~10).
- **`dotnet test --filter "category=chaos"` target: 1 test (unchanged from Story 1.6).** No chaos additions in this story.

### References

> Authoritative paths (for grep / cross-reference):
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (~3000 lines, post amendments A6–A18)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (lines 750–803 for Story 2.1)
> - Epic 1 retrospective: `_bmad-output/implementation-artifacts/epic-1-retro-2026-06-02.md`
> - Story 1.6 completion: `_bmad-output/implementation-artifacts/1-6-fakeupnpdevice-minimal-modes-first-chaos-test-netarchtest-rules.md`
> - Existing diagnostic categories: `src/ohSpy.Core/Diagnostics/DiagCategories.cs:22–29`

- [Source: epics.md#Story-2.1] — verbatim ACs (lines 750–803).
- [Source: epics.md#Epic-2] — epic-level FR/NFR coverage map.
- [Source: architecture.md#Decision-2] — SSDP socket topology (lines 207–258; verbatim ChannelOptions on line 244).
- [Source: architecture.md#Decision-7] — Cancellation hierarchy (lines 730–875; "cleanup uses level-above token" invariant on lines 786–812; adapter-switch sequence on lines 814–828).
- [Source: architecture.md#Pattern-2] — Core ↔ App boundary (lines 1710–1726; backstopped by Story 1.6 NetArchTest).
- [Source: architecture.md#Pattern-6] — async discipline + `ConfigureAwait(false)` (lines 1802–1811).
- [Source: architecture.md#Pattern-7] — DI lifetime defaults (lines 1813–1839).
- [Source: architecture.md#Pattern-10] — exception conventions (lines 1883–1902).
- [Source: architecture.md#Pattern-11] — `DiagnosticContext` mandatory fields per category (lines 1906–1926; `Ssdp.Parse` requires `RemoteEndpoint` line 1919).
- [Source: architecture.md#Pattern-12] — message grammar (lines 1928–1940).
- [Source: architecture.md#Pattern-14] — xUnit test naming (lines 1966–1984).
- [Source: architecture.md#Pattern-15] — AC traceability (lines 1986–2010).
- [Source: architecture.md#Project-Structure-and-Boundaries] — file inventory + Core layer rules (lines 2031–2230; SSDP transport listed line 2092).
- [Source: architecture.md#Amendment-A2] — AC trait shape (lines 2425–2448).
- [Source: architecture.md#Amendment-A18] — chaos-hook filter syntax `category=chaos` (lines 2792–2820).
- [Source: 1-6-…md#Anti-Patterns] — "don't tag fast tests with `category=chaos`" (line 790).
- [Source: 1-6-…md#Why-per-test-FakeUpnpDevice-instantiation] — per-test fixture precedent (lines 669–676).
- [Source: 1-6-…md#Cross-story-dependencies] — Story 2.x will add SSDP chaos tests at the parser layer, not the transport layer (lines 704–706).
- [Source: epic-1-retro-2026-06-02.md#Process-improvements] — action items A (compile-the-skeleton check) and B (trivially-passing red flag) (lines 141–142).
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs:22–29] — `SsdpParse`, `SsdpChannelNearFull`, `SsdpChannelOverflow` already pre-added.
- [Source: tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs] — canonical test diagnostic emitter.
- [Source: project_ohspy memory] — native Windows desktop UPnP inspector; raw-BCL UPnP (no third-party UPnP libs); no CI (pre-commit chaos hook is the regression net).

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
