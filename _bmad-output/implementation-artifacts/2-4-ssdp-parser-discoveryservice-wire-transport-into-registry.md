---
baseline_commit: 39f3f06aa4a593924c06d072f8e2abca8ea15dae
---

# Story 2.4: SSDP Parser + DiscoveryService â€” Wire Transport Into Registry

Status: done

## Story

As a Linn engineer,
I want the SSDP parser to translate raw datagrams into structured announcements and the `DiscoveryService` to route them into the registry (root-only, dedup-by-UUID, alive vs byebye),
so that the transport's datagram stream actually drives the device list â€” turning "we're receiving UDP packets" into "the tree fills with devices."

## Acceptance Criteria

**Verbatim ACs derived from epics.md Â§Story 2.4 (lines 928â€“989). AC trait IDs follow Amendment A2.**

**AC-2.4.1 â€” `SsdpAnnouncement` record shape**

**Given** `ohSpy.Core/Discovery/SsdpAnnouncement.cs`
**When** I inspect the type
**Then** it is a `public sealed record` exposing parsed fields: `NT?`, `NTS?`, `ST?`, `USN?`, `Uuid?` (Guid? â€” UUID extracted from USN), `Location?` (Uri?), `CacheControlMaxAge?` (TimeSpan?), `Server?`, `BootId?`, `ConfigId?`, plus `bool IsRootDevice` (computed: `NT == "upnp:rootdevice"` case-insensitive â€” FR-053 layer b)

**AC-2.4.2 â€” `SsdpParser` â€” happy-path parsing**

**Given** `ohSpy.Core/Discovery/SsdpParser.cs`
**When** I parse a valid SSDP NOTIFY datagram payload (HTTPMU text) or a valid M-SEARCH response (HTTPU text)
**Then** the parser returns a non-null `SsdpAnnouncement` with all recognized headers populated
**And** unrecognised headers are silently ignored (lenient â€” vendor-noise philosophy from D4 applied to SSDP)
**And** both `NOTIFY * HTTP/1.1` (multicast NOTIFY) and `HTTP/1.1 200 OK` (M-SEARCH response) first-lines are accepted

**AC-2.4.3 â€” `SsdpParser` â€” malformed datagrams**

**When** a datagram payload is truly malformed (empty, no recognisable first-line, or body-only with missing required headers)
**Then** `Parse` returns `null`
**And** the `DiscoveryService` emits a `Warning` `DiagCategories.SsdpParse` diagnostic with `RemoteEndpoint` context (Pattern 11: `SsdpParse` mandatory field = `RemoteEndpoint`)

**AC-2.4.4 â€” `DiscoveryService` starts consuming**

**Given** `ohSpy.Core/Discovery/DiscoveryService.cs`
**When** `StartAsync(adapterToken, ct)` is called
**Then** the service begins consuming from `ISsdpTransport.IncomingDatagrams` as the single reader
**And** the background read loop exits cleanly when either `adapterToken` or `ct` is cancelled

**AC-2.4.5 â€” Alive for NEW root UUID â†’ registry add + eager fetch**

**When** the announcement is `ssdp:alive` (or M-SEARCH response, NTS absent) with `NT == upnp:rootdevice` AND the UUID is NOT yet in the registry
**Then** `DiscoveryService` calls `registry.OnAlive(uuid, location, nowUtc, server, maxAge, bootId, configId, adapterToken)` on the UI thread (via `IUiDispatcher.Post`) (FR-005)
**And** `DeviceRegistry.EntryNeedsFetch` fires, `EagerDescriptionDispatcher` schedules the fetch â€” **DiscoveryService does NOT call the dispatcher directly**

**AC-2.4.6 â€” Alive for KNOWN UUID â†’ metadata refresh, no re-fetch**

**When** the announcement is `ssdp:alive` for a UUID ALREADY in the registry
**Then** `registry.OnAlive(...)` updates the entry's `LastSeenUtc`, `AliveCount`, SSDP metadata â€” no new entry, no new fetch (FR-007 + FR-043 cache invariant)

**AC-2.4.7 â€” Byebye for root UUID â†’ registry remove**

**When** the announcement is `ssdp:byebye` with `NT == upnp:rootdevice` for a KNOWN UUID
**Then** `registry.OnByebye(uuid)` runs on the UI thread â€” cancels `DeviceCts`, disposes it, removes the entry, raises `DeviceRemoved` (FR-008)
**And** the in-flight description fetch (if any) observes `OperationCanceledException` and exits silently (AC-9.7)

**AC-2.4.8 â€” Non-root announcements â†’ registry muted, log visible**

**When** `NT != upnp:rootdevice` (e.g. `urn:schemas-upnp-org:device:MediaRenderer:1`, service-only type)
**Then** the registry is NOT mutated (FR-053 layer b â€” embedded children flatten via description parse, never via separate registry entries)
**And** `AnnouncementReceived` IS still raised (the SSDP log in Story 2.7 observes ALL announcements â€” FR-014/FR-015)

**AC-2.4.9 â€” `AnnouncementReceived` event**

**Given** the discovery service's event surface
**When** any datagram is successfully parsed (alive or byebye, root or non-root)
**Then** `AnnouncementReceived(SsdpAnnouncement announcement)` is raised
**And** every emit marshals through `IUiDispatcher.Post` (NFR-P3)
**And** the event is NOT raised for datagrams that fail to parse (those produce only a `SsdpParse` Warning)

**AC-2.4.10 â€” M-SEARCH responses treated as alive**

**When** `datagram.Source == SsdpSource.SearchResponse` (ephemeral search socket response)
**Then** the datagram is parsed and routed identically to an unsolicited `ssdp:alive` (FR-005 + FR-006 + architecture Â§"SSDP datagram flow")

**AC-2.4.11 â€” Rescan stub (forward-compatible shape)**

**Given** the rescan contract (E5)
**When** I inspect `DiscoveryService`
**Then** it exposes a method shape for `RescanAsync(CancellationToken ct)` that calls `_transport.SendMSearchAsync(...)` â€” the body is a stub (`throw new NotImplementedException()`) or minimal implementation, present so E5's `DiscoveryService` wiring doesn't require a new method signature

**AC-2.4.12 â€” DI registration + App startup pin**

**Given** the DI composition
**When** the App starts
**Then** `IDiscoveryService` â†’ `DiscoveryService` is registered as a singleton
**And** `DiscoveryService.StartAsync(adapterToken, ct)` is called inside `StartAdapterScopeAsync` AFTER `scope.StartAsync()` succeeds â€” so the transport is bound and `IncomingDatagrams` is live before the consumer starts reading
**And** on zero-adapter paths (`CurrentAdapterIPv4 == null`) the discovery service is NOT started (no channel to read)

**AC-2.4.13 â€” Integration test: datagram drill**

**Given** the integration test suite
**When** I run a full datagram drill
**Then** a `ChannelSsdpTransport` test double (new fake with writable channel and larger capacity) feeds canned `SsdpDatagram` fixtures
**And** an alive for a root UUID â†’ `DeviceRegistry.Count == 1`, entry is `Pending`, `EntryNeedsFetch` fired once
**And** a byebye for that UUID â†’ `DeviceRegistry.Count == 0`, `DeviceRemoved` raised, `DeviceCts` cancelled
**And** an embedded-device alive (`NT != upnp:rootdevice`) â†’ registry unchanged, `AnnouncementReceived` raised once
**And** a malformed datagram â†’ `Warning(DiagCategories.SsdpParse)` emitted, registry unchanged

## Tasks / Subtasks

### Task 1 â€” `SsdpAnnouncement` record (AC: #1)

- [x] **1.1** Create `src/ohSpy.Core/Discovery/SsdpAnnouncement.cs`:
  ```csharp
  namespace ohSpy.Core.Discovery;

  /// <summary>
  /// Parsed SSDP announcement. All header fields are nullable â€” a lenient parser omits
  /// fields missing from the datagram. <see cref="IsRootDevice"/> is the FR-053 layer (b)
  /// gate: only root-device announcements mutate the registry.
  /// </summary>
  public sealed record SsdpAnnouncement(
      string? NT,
      string? NTS,
      string? ST,
      string? USN,
      Guid? Uuid,            // extracted from USN (or null if USN absent or unparseable)
      Uri? Location,         // parsed from LOCATION header
      TimeSpan? CacheControlMaxAge,
      string? Server,
      string? BootId,
      string? ConfigId)
  {
      /// <summary>True iff NT == "upnp:rootdevice" (case-insensitive) â€” FR-053 layer (b).</summary>
      public bool IsRootDevice =>
          NT?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true;
  }
  ```
- [x] **1.2** File-scoped namespace, Pattern 9 (`public sealed record`). `IsRootDevice` is a computed property â€” no storage, no constructor parameter.
- [x] **1.3** `Uuid` is extracted from USN (e.g. `uuid:f7dc20e5-1234-...:upnp:rootdevice` or bare `uuid:f7dc20e5-...`). Extraction is the PARSER's job (Task 2), not the record's.

### Task 2 â€” `SsdpParser` (AC: #2, #3)

- [x] **2.1** Create `src/ohSpy.Core/Discovery/SsdpParser.cs` â€” `internal sealed`. Takes `IDiagnosticEmitter` in ctor (for the malformed-datagram Warning). The caller (`DiscoveryService`) passes the `RemoteEndpoint` string from the datagram:
  ```csharp
  internal sealed class SsdpParser(IDiagnosticEmitter diag)
  {
      /// <summary>
      /// Parse a raw SSDP datagram payload. Returns null + emits Warning on malformed.
      /// Both NOTIFY (request-form) and M-SEARCH response (response-form) are accepted.
      /// </summary>
      internal SsdpAnnouncement? Parse(byte[] payload, string remoteEndpoint) { ... }
  }
  ```
- [x] **2.2** Decode with `Encoding.UTF8.GetString(payload)` (SSDP is ASCII-on-the-wire; UTF-8 is a safe superset). Split on `\r\n` to get lines.
- [x] **2.3** Accept valid first-line shapes (case-insensitive prefix match is fine):
  - `NOTIFY * HTTP/1.1` â†’ NOTIFY (multicast announce)
  - `HTTP/1.1 200 OK` â†’ M-SEARCH response (treat as `ssdp:alive`)
  - Anything else â†’ malformed â†’ return `null` + emit `Warning(DiagCategories.SsdpParse, "ssdp parse failed", new DiagnosticContext { RemoteEndpoint = remoteEndpoint })`
- [x] **2.4** Parse headers: for each subsequent line of the form `Key: Value`, extract the key-value pair (case-insensitive key comparison). Blank line = end of message. **Ignore unknown headers** (lenient, D4 vendor-noise philosophy).
  Headers to extract:
  | Header | Field | Parsing notes |
  |---|---|---|
  | `NT` | `NT` | raw string |
  | `NTS` | `NTS` | raw string |
  | `ST` | `ST` | raw string (M-SEARCH responses use ST, not NT) |
  | `USN` | `USN` + `Uuid` | Extract Guid from `uuid:<guid>[::<nt>]` prefix; use `UdnMatches`-style strip of `uuid:` prefix |
  | `LOCATION` | `Location` | `Uri.TryCreate(..., UriKind.Absolute, out var uri)` â†’ null on parse fail |
  | `CACHE-CONTROL` | `CacheControlMaxAge` | `max-age=N` â†’ `TimeSpan.FromSeconds(N)` |
  | `SERVER` | `Server` | raw string |
  | `BOOTID.UPNP.ORG` | `BootId` | raw string |
  | `CONFIGID.UPNP.ORG` | `ConfigId` | raw string |
- [x] **2.5** For M-SEARCH responses (first line = `HTTP/1.1 200 OK`): `ST` plays the role of `NT`, `NTS` is absent (treat as `ssdp:alive`). The `IsRootDevice` check is `ST == "upnp:rootdevice"` for responses â€” see Task 4.5.
- [x] **2.6** `ExtractUuid(string? usn) â†’ Guid?` â€” internal static helper:
  ```csharp
  internal static Guid? ExtractUuid(string? usn)
  {
      if (usn is null) return null;
      var s = usn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? usn[5..] : usn;
      var colonPos = s.IndexOf(':', StringComparison.Ordinal);
      if (colonPos >= 0) s = s[..colonPos];
      return Guid.TryParse(s, out var g) ? g : null;
  }
  ```
  (Re-uses the prefix-strip pattern from `EagerDescriptionDispatcher.UdnMatches`.)
- [x] **2.7** Emit the `Warning` diagnostic with exact Pattern 11 context for `Ssdp.Parse` â€” `RemoteEndpoint` is the mandatory field, no other fields needed beyond `message`. Pattern 12 message: `"ssdp parse failed"`.
- [x] **2.8** `ConfigureAwait(false)` is not relevant here (synchronous parser), but keep Pattern 6 discipline for any future async paths. Mark `SsdpParser` as `internal sealed` (Pattern 7/9).

### Task 3 â€” `IDiscoveryService` + `DiscoveryService` (AC: #4â€“#12)

- [x] **3.1** Create `src/ohSpy.Core/Discovery/IDiscoveryService.cs`:
  ```csharp
  public interface IDiscoveryService : IAsyncDisposable
  {
      /// <summary>Raised on the UI thread for every successfully parsed announcement (FR-014/FR-015).</summary>
      event Action<SsdpAnnouncement> AnnouncementReceived;

      /// <summary>Starts consuming <see cref="ISsdpTransport.IncomingDatagrams"/>.</summary>
      Task StartAsync(CancellationToken adapterToken, CancellationToken ct);
  }
  ```
- [x] **3.2** Create `src/ohSpy.Core/Discovery/DiscoveryService.cs` â€” `internal sealed`, primary constructor over its dependencies:
  ```csharp
  internal sealed class DiscoveryService(
      ISsdpTransport transport,
      DeviceRegistry registry,
      SsdpParser parser,
      IUiDispatcher ui,
      IDiagnosticEmitter diag) : IDiscoveryService
  {
      public event Action<SsdpAnnouncement>? AnnouncementReceived;
      private Task? _readLoop;
      private int _started;

      public Task StartAsync(CancellationToken adapterToken, CancellationToken ct)
      {
          if (Interlocked.Exchange(ref _started, 1) == 1)
              throw new InvalidOperationException("StartAsync already called");
          _readLoop = Task.Run(() => ReadLoopAsync(adapterToken, ct));
          return Task.CompletedTask;
      }

      public async ValueTask DisposeAsync()
      {
          if (_readLoop is not null)
          {
              try { await _readLoop.ConfigureAwait(false); }
              catch { /* loop exits via cancellation or channel completion */ }
          }
      }
  }
  ```
- [x] **3.3** Implement `ReadLoopAsync(CancellationToken adapterToken, CancellationToken ct)`:
  ```csharp
  private async Task ReadLoopAsync(CancellationToken adapterToken, CancellationToken ct)
  {
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(adapterToken, ct);
      try
      {
          await foreach (var datagram in transport.IncomingDatagrams.ReadAllAsync(linked.Token)
                                                                     .ConfigureAwait(false))
          {
              var remoteStr = datagram.Remote.ToString();
              var announcement = parser.Parse(datagram.Payload, remoteStr);
              if (announcement is null) continue; // Warning already emitted by parser

              var capturedAdapterToken = adapterToken; // AC-2.4.5: passed into OnAlive
              ui.Post(() => RouteOnUiThread(announcement, datagram.ArrivalUtc, capturedAdapterToken));
          }
      }
      catch (OperationCanceledException)
      {
          // Normal shutdown â€” adapterToken or ct cancelled.
      }
  }
  ```
- [x] **3.4** Implement `RouteOnUiThread(SsdpAnnouncement, DateTime, CancellationToken adapterToken)` â€” runs on the UI thread (called via `Post`):
  ```csharp
  private void RouteOnUiThread(SsdpAnnouncement ann, DateTime arrivalUtc, CancellationToken adapterToken)
  {
      // Effective NT for M-SEARCH responses is ST (NTS absent).
      var effectiveNt = ann.NT ?? ann.ST;

      if (ann.NTS?.Equals("ssdp:byebye", StringComparison.OrdinalIgnoreCase) == true)
      {
          if (ann.Uuid.HasValue &&
              (effectiveNt?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true))
          {
              registry.OnByebye(ann.Uuid.Value); // FR-008 + AC-7.2
          }
      }
      else // ssdp:alive or M-SEARCH response (NTS absent)
      {
          if (ann.Uuid.HasValue && ann.Location is not null &&
              (effectiveNt?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true))
          {
              registry.OnAlive(ann.Uuid.Value, ann.Location, arrivalUtc,
                  ann.Server, ann.CacheControlMaxAge, ann.BootId, ann.ConfigId,
                  adapterToken); // FR-005 / FR-007 / FR-043
          }
          // Non-root alives: registry untouched (FR-053 layer b)
      }

      // Raise for ALL successfully-parsed announcements (FR-014/FR-015 â€” log gets everything).
      AnnouncementReceived?.Invoke(ann);
  }
  ```
- [x] **3.5** **`adapterToken` capture:** The token passed into `StartAsync` must be captured and forwarded to `registry.OnAlive`. The `ReadLoopAsync` method captures it in a local for the lambda (Task 3.3). Avoid capturing the CancellationToken directly in a closure that could outlive the scope.
- [x] **3.6** `RescanAsync` stub (AC-2.4.11):
  ```csharp
  /// <summary>Re-issues M-SEARCH and prunes non-responders (E5). Stub in Story 2.4.</summary>
  public Task RescanAsync(CancellationToken ct) =>
      _transport.SendMSearchAsync(TimeSpan.FromSeconds(5), ct);
  ```
  (Not a full stub â€” the M-SEARCH part is real and harmless; the prune part is E5's work. This is cleaner than `throw new NotImplementedException()` and exercises the same transport path.)
- [x] **3.7** `DiscoveryService` takes the **concrete** `DeviceRegistry` (needs internal `OnAlive`/`OnByebye`) and `SsdpParser` (internal type). Both are registered as singletons (see Task 4). Pattern 7: internal types registered behind their interfaces where applicable.
- [x] **3.8** Required usings: `System.Threading`, `System.Threading.Channels` (for `ReadAllAsync`), `ohSpy.Core.Diagnostics`, `ohSpy.Core.Devices`.

### Task 4 â€” DI registration + App startup (AC: #12)

- [x] **4.1** In `ServiceRegistration.cs`, after the `EagerDescriptionDispatcher` registration, add:
  ```csharp
  // Story 2.4 â€” SSDP parser + discovery service.
  // SsdpParser is internal; registered as concrete so DiscoveryService can receive it.
  services.AddSingleton<SsdpParser>();
  services.AddSingleton<DiscoveryService>();
  services.AddSingleton<IDiscoveryService>(sp => sp.GetRequiredService<DiscoveryService>());
  ```
  Add `using ohSpy.Core.Discovery;` if not already present (it is, from Story 2.2).
- [x] **4.2** In `App.xaml.cs`, add a `DiscoveryService` field and start it after scope start. The key constraint: **DiscoveryService must start AFTER `scope.StartAsync()` so `IncomingDatagrams` is live.** Update `StartAdapterScopeAsync` to accept and start the discovery service:
  ```csharp
  // In OnLaunched, resolve discovery service and pass to startup helper:
  var discoveryService = Services.GetRequiredService<DiscoveryService>();
  _ = StartAdapterScopeAsync(_adapterScope, discoveryService, diag);
  ```
  Update the helper:
  ```csharp
  private static async Task StartAdapterScopeAsync(
      AdapterScope scope, DiscoveryService discovery, IDiagnosticEmitter diag)
  {
      try
      {
          await scope.StartAsync().ConfigureAwait(false);
          // Transport is now live. Start consuming the datagram channel.
          // Zero-adapter path: CurrentAdapterIPv4 is null â†’ don't start (no channel to read).
          if (scope.CurrentAdapterIPv4 is not null)
          {
              await discovery.StartAsync(scope.AdapterToken, scope.AdapterToken)
                             .ConfigureAwait(false);
          }
      }
      catch (Exception ex) when (ex is not OutOfMemoryException)
      {
          diag.Warning(DiagCategories.AdapterSwitch,
              "adapter startup failed â€” no SSDP traffic",
              new DiagnosticContext { ErrorText = ex.Message });
      }
  }
  ```
  **Note:** Both `adapterToken` and `ct` use `scope.AdapterToken` for now â€” a single token cancels both the scope and the read loop. When Story 5.2 implements atomic switch, a dedicated `ct` separate from `adapterToken` may be needed; this matches the architecture's "cleanup uses level-above token" invariant.
- [x] **4.3** Add `using ohSpy.Core.Discovery;` to `App.xaml.cs` (already present from Story 2.2).

### Task 5 â€” `ChannelSsdpTransport` test fake (AC: #13)

- [x] **5.1** Create `tests/ohSpy.Core.Tests/Fakes/ChannelSsdpTransport.cs` â€” a writable-channel transport fake for integration tests (distinct from `FakeSsdpTransport` which has capacity 1 and is for AdapterScope lifecycle tests):
  ```csharp
  internal sealed class ChannelSsdpTransport : ISsdpTransport
  {
      private readonly Channel<SsdpDatagram> _channel =
          Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(256)
          {
              FullMode = BoundedChannelFullMode.DropOldest,
              SingleReader = true,
              SingleWriter = false,
          });

      public ChannelReader<SsdpDatagram> IncomingDatagrams => _channel.Reader;

      /// <summary>Feed a datagram into the channel for the DiscoveryService to process.</summary>
      public ValueTask WriteAsync(SsdpDatagram datagram, CancellationToken ct = default) =>
          _channel.Writer.WriteAsync(datagram, ct);

      /// <summary>Completes the channel so the read loop exits cleanly.</summary>
      public void Complete() => _channel.Writer.Complete();

      public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct) => Task.CompletedTask;
      public Task SendMSearchAsync(TimeSpan mx, CancellationToken ct) => Task.CompletedTask;
      public ValueTask DisposeAsync() { Complete(); return ValueTask.CompletedTask; }
  }
  ```
- [x] **5.2** Also create a small `SsdpDatagramBuilder` test helper (static class or extension) to build canned `SsdpDatagram` values without string templating at every call site:
  ```csharp
  internal static class SsdpDatagramBuilder
  {
      private static readonly IPEndPoint TestRemote =
          new(IPAddress.Parse("192.0.2.42"), 50000);

      public static SsdpDatagram Notify(string nt, string nts, Guid uuid,
          string location = "http://192.0.2.42:49152/desc.xml") =>
          Build($$"""
              NOTIFY * HTTP/1.1\r\n
              HOST: 239.255.255.250:1900\r\n
              NT: {{nt}}\r\n
              NTS: {{nts}}\r\n
              USN: uuid:{{uuid}}::{{nt}}\r\n
              LOCATION: {{location}}\r\n
              CACHE-CONTROL: max-age=1800\r\n
              \r\n
              """);

      public static SsdpDatagram SearchResponse(Guid uuid,
          string location = "http://192.0.2.42:49152/desc.xml") =>
          Build($$"""
              HTTP/1.1 200 OK\r\n
              ST: upnp:rootdevice\r\n
              USN: uuid:{{uuid}}::upnp:rootdevice\r\n
              LOCATION: {{location}}\r\n
              CACHE-CONTROL: max-age=1800\r\n
              \r\n
              """);

      public static SsdpDatagram Malformed() =>
          Build("NOT_SSDP garbage\r\n");

      private static SsdpDatagram Build(string text) =>
          new(TestRemote, Encoding.UTF8.GetBytes(
              text.Replace("\\r\\n", "\r\n")), DateTime.UtcNow, SsdpSource.Multicast);
  }
  ```
  **Note:** Use `\r\n` (actual CR+LF) in the byte payload, not the escape sequences. Adjust the builder if needed.

### Task 6 â€” Tests: `SsdpParser` (AC: #2, #3)

**Location:** `tests/ohSpy.Core.Tests/Discovery/SsdpParserTests.cs`. Use `CapturingDiagnosticEmitter` for Warning assertions. All unit tests â€” no real sockets, no channel.

- [x] **6.1** `Parse_Notify_Alive_RootDevice_ExtractsAllFields_AC242` â€” canned NOTIFY `ssdp:alive` bytes with all headers present â†’ all fields parsed, `IsRootDevice == true`, no diagnostic.
- [x] **6.2** `Parse_Notify_Alive_EmbeddedDevice_IsRootDeviceFalse_AC241` â€” NT = `urn:schemas-upnp-org:device:...` â†’ `IsRootDevice == false`.
- [x] **6.3** `Parse_Notify_Byebye_AC242` â€” NTS = `ssdp:byebye`, NT = `upnp:rootdevice` â†’ parsed, `NTS` field set.
- [x] **6.4** `Parse_MSearchResponse_200OK_AC242` â€” first line `HTTP/1.1 200 OK`, ST header â†’ parsed, `ST` field set, `NT` null.
- [x] **6.5** `Parse_UnknownHeaders_Ignored_AC242` â€” extra headers (`X-VENDOR-EXTENSION: foo`) â†’ no throw, ignored.
- [x] **6.6** `Parse_Malformed_EmptyPayload_ReturnsNull_EmitsWarning_AC243` â€” empty bytes â†’ null + Warning(SsdpParse) with `RemoteEndpoint`.
- [x] **6.7** `Parse_Malformed_NoFirstLine_ReturnsNull_AC243` â€” garbage text â†’ null + Warning.
- [x] **6.8** `ExtractUuid_HandlesAllForms_AC241` â€” unit test the static helper: `uuid:<g>`, `uuid:<g>::nt`, bare `<g>`, `UUID:<G>` (uppercase), junk â†’ expected Guid? results. (Use `[Theory][InlineData]`.)
- [x] **6.9** `Parse_CacheControl_ParsesMaxAge_AC242` â€” `CACHE-CONTROL: max-age=1800` â†’ `CacheControlMaxAge == TimeSpan.FromSeconds(1800)`. Missing `max-age` â†’ null.
- [x] **6.10** `Parse_Location_ParsesUri_AC242` â€” valid `LOCATION` header â†’ `Location` is the parsed Uri. Invalid URL â†’ `Location == null`.

### Task 7 â€” Tests: `DiscoveryService` integration drill (AC: #4â€“#11, #13)

**Location:** `tests/ohSpy.Core.Tests/Discovery/DiscoveryServiceTests.cs`. Uses `ChannelSsdpTransport` + `InlineUiDispatcher` + `CapturingDiagnosticEmitter`. The `InlineUiDispatcher` runs `Post` synchronously so registry mutations and `AnnouncementReceived` are observable immediately after the read-loop iteration.

**Important:** `DiscoveryService.ReadLoopAsync` is `async` and the read happens on a background thread (`Task.Run`). After writing a datagram, the test must await the service processing it. Use a `TaskCompletionSource` or `SemaphoreSlim` gate that releases when the announcement is raised, OR write the datagram, then `Complete()` the channel so the loop exits, then await `DisposeAsync()` to ensure all processing finished.

- [x] **7.1** `StartAsync_Alive_RootUuid_AddsEntryToRegistry_AC245` â€” write one root alive â†’ complete channel â†’ `await service.DisposeAsync()` â†’ `registry.Count == 1`, `EntryNeedsFetch` fired once.
- [x] **7.2** `StartAsync_Alive_KnownUuid_RefreshesNoNewEntry_AC246` â€” write alive, complete+drain â†’ write another alive, complete+drain â†’ `registry.Count == 1`, `AliveCount == 2`.
- [x] **7.3** `StartAsync_Byebye_KnownUuid_RemovesEntry_AC247` â€” alive (to add) â†’ byebye â†’ `registry.Count == 0`, `DeviceRemoved` raised.
- [x] **7.4** `StartAsync_Byebye_UnknownUuid_RegistryUnchanged_AC247` â€” byebye for unknown UUID â†’ no crash, `registry.Count == 0`.
- [x] **7.5** `StartAsync_EmbeddedDevice_RegistryMuted_AnnouncementRaised_AC248` â€” alive with non-root NT â†’ `registry.Count == 0`, `AnnouncementReceived` raised once.
- [x] **7.6** `StartAsync_MSearchResponse_TreatedAsAlive_AC2410` â€” `SearchResponse` source, `HTTP/1.1 200 OK` payload â†’ registry add.
- [x] **7.7** `StartAsync_Malformed_EmitsWarning_RegistryUnchanged_AC243` â€” malformed bytes â†’ `Warning(SsdpParse)`, `registry.Count == 0`, `AnnouncementReceived` NOT raised.
- [x] **7.8** `StartAsync_CancelToken_LoopExitsCleanly_AC244` â€” start service, write one datagram, cancel the token â†’ `await DisposeAsync()` completes within 200 ms.
- [x] **7.9** `AnnouncementReceived_FiredForAllParsedAnnouncements_AC249` â€” three datagrams (root alive, embedded alive, root byebye) â†’ `AnnouncementReceived` fires 3 times; malformed datagram does NOT fire.

### Task 8 â€” Tests: `SsdpAnnouncement` properties (AC: #1)

**Location:** `tests/ohSpy.Core.Tests/Discovery/SsdpAnnouncementTests.cs` (fast, pure unit tests).

- [x] **8.1** `IsRootDevice_WhenNtIsRootdevice_ReturnsTrue_AC241` â€” case-insensitive check.
- [x] **8.2** `IsRootDevice_WhenNtIsEmbedded_ReturnsFalse_AC241`.
- [x] **8.3** `IsRootDevice_WhenNtIsNull_ReturnsFalse_AC241`.

### Task 9 â€” Final verification (AC: all)

- [x] **9.1** **Compile skeletons first (epic-1 retro action A).** Expect: `ConfigureAwait` on `ReadAllAsync`; possible `VSTHRD110`/`CA2012` on `_ = Task.Run(...)` in `StartAsync`; `CA1068` if token params are in wrong order. Fix at source.
- [x] **9.2** `dotnet build` 0 warnings / 0 errors under `TreatWarningsAsErrors`. NetArchTest `CoreAppBoundaryTests` still green â€” all new `Discovery/` types are BCL + `ohSpy.Core.*` only.
- [x] **9.3** `dotnet test` green. Story 2.3 left **199 passing + 2 skipped (201 total)**. Story 2.4 adds ~25 tests; target ~226.
- [x] **9.4** `dotnet test --filter "category=chaos"` still exactly **1** (no chaos tests added here â€” chaos at the SSDP layer means malformed-frame storms, future work after the parser exists).
- [x] **9.5** Manual smoke (optional, not AC-gating): launch `ohSpy.App` on Simon's LAN. After startup, the `DiscoveryService` begins reading from `IncomingDatagrams`. Within ~7 s (5 s MX + up to 2 s description fetch), the `EagerDescriptionDispatcher` should be fetching descriptions â€” observable via the diagnostic file sink. **The tree will not yet populate** (that's Story 2.5's `ShellViewModel` subscribing to `IDeviceRegistry.DeviceLoaded`), but `registry.Count` should be > 0 if everything wired correctly.

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **Decision 2 / "SSDP datagram flow"** | `SsdpParser` + `DiscoveryService` as the single `IncomingDatagrams` reader; both datagram sources (Multicast/SearchResponse) routed identically | AC-2.4.2, 2.4.10 |
| **FR-053 layer (b)** | Root-only registry mutations (NT/ST == `upnp:rootdevice` check) | AC-2.4.5, 2.4.7, 2.4.8 |
| **FR-005 / FR-006 / FR-007 / FR-008** | New UUID â†’ `OnAlive`; re-announce â†’ `RefreshSsdpMetadata`; byebye â†’ `OnByebye` | AC-2.4.5â€“2.4.7 |
| **FR-014 / FR-015 / NFR-P3** | `AnnouncementReceived` raised for every parsed announcement via `IUiDispatcher.Post` (SSDP log Story 2.7 subscribes here) | AC-2.4.9 |
| **Decision 7 â€” adapter token threading** | `adapterToken` captured in `StartAsync`, forwarded into `OnAlive` so each `RegistryEntry.DeviceCts` is linked to the right adapter level | AC-2.4.5 |
| **Pattern 6 â€” async discipline** | `ReadAllAsync` + `ConfigureAwait(false)`; no `.Wait()`; cancellation via linked CTS in read loop | AC-2.4.4 |
| **Pattern 11 â€” DiagnosticContext** | `SsdpParse` requires `RemoteEndpoint` â€” provided from `datagram.Remote.ToString()` | AC-2.4.3 |
| **Amendment A22** | Story 2.4's tests use `ChannelSsdpTransport` (write-to-channel), bypassing real sockets â€” A22's multicast-delivery rule applies only to real-socket tests | AC-2.4.13 |
| **Amendment A23** | DiscoveryService resolves `ISsdpTransport` from DI (same singleton AdapterScope started). Single-scope shape deferred to 5.2 | AC-2.4.12 |

### THE THREE CRITICAL DESIGN DECISIONS (read before coding)

1. **`adapterToken` threading.** `registry.OnAlive(...)` takes a `CancellationToken adapterToken` (D7 device level). `DiscoveryService.StartAsync(adapterToken, ct)` receives it from `App.xaml.cs` via `scope.AdapterToken`. Inside the async read loop, the token must be captured in a local variable before the `ui.Post` lambda captures it â€” do NOT capture `adapterToken` directly from the method parameter into a lambda that executes asynchronously (the parameter is valid for the loop's lifetime, so capturing it is fine, but be explicit).

2. **M-SEARCH responses use `ST` not `NT`.** A `SearchResponse` datagram (first line = `HTTP/1.1 200 OK`) has `ST` where NOTIFY has `NT`. The root-device check and UUID routing must check **both** `NT` and `ST`. The `SsdpAnnouncement` record preserves both; `RouteOnUiThread` uses `effectiveNt = ann.NT ?? ann.ST` to normalize. `IsRootDevice` on the record only checks `NT` (for NOTIFY alignment) â€” so for responses, `IsRootDevice` is false even for root-device responses. **The routing code must check `effectiveNt`, not `ann.IsRootDevice`.** This is the subtle trap.

3. **`DiscoveryService.StartAsync` must be called AFTER `scope.StartAsync()`.** The transport binds to the adapter in `scope.StartAsync()`. Before that call, `transport.IncomingDatagrams` exists (it's a bounded channel created in the transport constructor) but no receive loops are running. Starting the discovery service before the transport is bound wastes the read loop. `StartAdapterScopeAsync` in `App.xaml.cs` must start the discovery service AFTER the scope starts successfully.

### What this story does NOT do (scope discipline)

- **Does NOT subscribe to `IDeviceRegistry.DeviceLoaded` / build any ViewModel.** The tree is Story 2.5.
- **Does NOT implement the SSDP log display.** `AnnouncementReceived` is shaped for Story 2.7 to subscribe to; no consumer exists yet.
- **Does NOT implement adapter switch.** `DiscoveryService` is started once per adapter scope. Full lifecycle management (stop on switch, restart on new adapter) is Story 5.2.
- **Does NOT parse SCPD or action XML.** That's Story 1.4 (already done) + Story 2.6.
- **Does NOT add `ssdp:update` handling.** UDA 1.1 `ssdp:update` messages exist but are not mentioned in the epics' ACs; ignore them (unknown NTS â†’ falls through as non-byebye, non-alive, registry untouched, announcement still raised).
- **Does NOT add new `DiagCategories`.** `SsdpParse` pre-exists.
- **Does NOT add new packages.** `System.Threading.Channels.ChannelReader.ReadAllAsync` is in BCL (.NET Core 3.0+).

### Previous-story intelligence

**Story 2.1 (`SsdpTransport`) â€” the datagram producer:**
- `IncomingDatagrams` is a `ChannelReader<SsdpDatagram>` with `SingleReader = true`, `DropOldest(4096)`. `DiscoveryService` is the sole consumer.
- `SsdpDatagram.Payload` is raw bytes (no parsing). `SsdpDatagram.Remote` is the sender's `IPEndPoint`. `SsdpDatagram.ArrivalUtc` is UTC. `SsdpDatagram.Source` is `Multicast` or `SearchResponse`.

**Story 2.2 (`AdapterScope`) â€” the token source:**
- `scope.AdapterToken` is the adapter-level CTS token (linked to `_appCts.Token`). Pass it as `adapterToken` to `DiscoveryService.StartAsync` and subsequently to `registry.OnAlive`.
- `scope.CurrentAdapterIPv4` is non-null iff the transport is bound. Start discovery only when non-null (AC-2.4.12).

**Story 2.3 (`DeviceRegistry` + `EagerDescriptionDispatcher`) â€” the consumers:**
- `DeviceRegistry.OnAlive(Guid, Uri, DateTime, string?, TimeSpan?, string?, string?, CancellationToken)` â€” `internal`, `AssertOnUiThread`. ALWAYS call via `ui.Post(...)`.
- `DeviceRegistry.OnByebye(Guid)` â€” `internal`, `AssertOnUiThread`. ALWAYS call via `ui.Post(...)`.
- `EagerDescriptionDispatcher` subscribes to `EntryNeedsFetch` in its ctor â€” `DiscoveryService` never calls it directly.
- **`InlineUiDispatcher` in tests runs `Post` synchronously** â€” registry mutations and `AnnouncementReceived` are observable immediately after the round-trip in integration tests.

**Story 2.3 review-learnings:**
- Dispose linked `CancellationTokenSource` instances after cancel.
- Guard `Post` lambdas against stale state with token-cancelled checks where relevant.
- `GC.GetAllocatedBytesForCurrentThread()` for allocation tests (A29).

### Code-style + pattern compliance

- **Pattern 1:** file-scoped namespaces; `_camelCase` fields; `Async` suffix.
- **Pattern 2:** all new Core types only reference BCL + `ohSpy.Core.*`. NetArchTest-backstopped.
- **Pattern 6:** `ConfigureAwait(false)` on every `await`; `ReadAllAsync(token)` passes the cancellation token; no `.Wait()`.
- **Pattern 7:** `SsdpParser` registered as concrete singleton (internal type); `DiscoveryService` double-registered (concrete + interface forward) like `DeviceRegistry`.
- **Pattern 9:** `SsdpAnnouncement` is `public sealed record`; `SsdpParser`/`DiscoveryService` are `internal sealed class`.
- **Pattern 11:** `SsdpParse` requires `RemoteEndpoint`. Pass `datagram.Remote.ToString()` as the context.
- **Pattern 12:** `"ssdp parse failed"` â€” sentence case, ASCII, no trailing punctuation.
- **Pattern 14/15 + A2:** test names `Method_Scenario_Expected_AC24n`; `[Trait("ac", "AC-2.4.<n>")]`.

### Anti-patterns to avoid

- **Don't check `ann.IsRootDevice` in routing code.** Use `effectiveNt = ann.NT ?? ann.ST` for root-device check (M-SEARCH responses use ST, not NT; `IsRootDevice` only checks NT).
- **Don't start DiscoveryService before `scope.StartAsync()`.** The channel exists but receive loops aren't running.
- **Don't call `registry.OnAlive`/`OnByebye` directly from the read loop thread.** ALWAYS via `ui.Post(...)`.
- **Don't call `EagerDescriptionDispatcher.FetchAsync` from DiscoveryService.** The registry's `EntryNeedsFetch` event handles that.
- **Don't use `FakeSsdpTransport` for DiscoveryService integration tests** â€” it has channel capacity 1 and no `WriteAsync` method. Use `ChannelSsdpTransport` (Task 5).
- **Don't apply A22 (multicast test delivery) to Story 2.4 tests.** A22 is for real-socket SSDP transport tests. Story 2.4 tests write datagrams directly to the channel â€” A22 doesn't apply.
- **Don't emit `AnnouncementReceived` for parse-failed datagrams.** Only successfully parsed announcements raise the event.
- **Don't decode with `Encoding.ASCII`.** Use `Encoding.UTF8` (superset of ASCII; robust against any accidental UTF-8 content in vendor SERVER strings).

### Forward-looking dependencies

| Story | What it consumes from 2.4 |
|---|---|
| 2.5 (Shell + Device Tree) | `IDeviceRegistry.DeviceLoaded` / `DeviceUpdated` / `DeviceRemoved` events (already shaped in 2.3). Story 2.4 makes them fire. |
| 2.7 (SSDP Message Log) | `IDiscoveryService.AnnouncementReceived` event â†’ prepends entries to `SsdpLogViewModel`. The event surface is established here. |
| 5.2 (Adapter Switch) | `DiscoveryService` teardown (stop reading channel, restart on new adapter). Currently started once; lifecycle management is 5.2's work. |

### Architecture amendments to anticipate

- **A30 (likely):** The architecture's "SSDP datagram flow" integration diagram says `DeviceRegistry.Add(new RegistryEntry)` (lines 2234) but the correct call is `DeviceRegistry.OnAlive(...)` (as implemented in Story 2.3). Recommend patching the diagram to use `OnAlive`. Also, the diagram implies `DiscoveryService` directly calls `EagerDescriptionDispatcher.Schedule` â€” incorrect since Story 2.3 wires that via `EntryNeedsFetch`. Recommend clarifying the integration diagram.
- **A31 (speculative):** The `ssdp:update` NTS (UDA 1.1) is not in scope but real devices emit it. If a `ssdp:update` arrives, the current routing falls through to the non-byebye non-alive path (registry untouched, event raised). If future requirements need to handle CONFIGID changes, an explicit `ssdp:update` branch would be added here.

### Project Structure Notes

**New (4 source + 4 test):**
```
src/ohSpy.Core/
â””â”€â”€ Discovery/
    â”œâ”€â”€ SsdpAnnouncement.cs          â† Task 1 NEW (public sealed record)
    â”œâ”€â”€ SsdpParser.cs                â† Task 2 NEW (internal sealed)
    â”œâ”€â”€ IDiscoveryService.cs         â† Task 3.1 NEW (public interface)
    â””â”€â”€ DiscoveryService.cs          â† Task 3.2â€“3.6 NEW (internal sealed)

tests/ohSpy.Core.Tests/
â”œâ”€â”€ Discovery/
â”‚   â”œâ”€â”€ SsdpParserTests.cs           â† Task 6 NEW
â”‚   â”œâ”€â”€ SsdpAnnouncementTests.cs     â† Task 8 NEW
â”‚   â””â”€â”€ DiscoveryServiceTests.cs     â† Task 7 NEW
â””â”€â”€ Fakes/
    â”œâ”€â”€ ChannelSsdpTransport.cs      â† Task 5.1 NEW
    â””â”€â”€ SsdpDatagramBuilder.cs       â† Task 5.2 NEW
```

**Modified (2):**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` â€” `SsdpParser` + `DiscoveryService` (concrete + interface forward).
- `src/ohSpy.App/App.xaml.cs` â€” `DiscoveryService` field + `StartAdapterScopeAsync` signature extension + `using`.

### Testing standards summary

- xUnit + FluentAssertions 7.2.0. `[Trait("ac", "AC-2.4.<n>")]` per AC-traceable test.
- **No chaos tests** (chaos suite stays at 1). Parser-chaos (malformed datagram storms) would be natural here if a chaos test is desired â€” flag as future work.
- **`InlineUiDispatcher`** for synchronous `Post` in integration tests.
- **`ChannelSsdpTransport`** for datagram-feeding integration tests. **NOT** `FakeSsdpTransport` (capacity 1).
- **No `[Trait("category","integration")]`** â€” all tests are fast pure-logic; no real sockets.
- **Target: ~226 tests** (201 baseline + ~25).

### References

> Authoritative paths:
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (SSDP datagram flow diagram ~2227â€“2238; Decision 2 ~207â€“261; A22 ~2826â€“2855; A23 ~2858â€“2875; A28 ~2927â€“2950)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (Story 2.4 lines 928â€“989)
> - Previous story: `_bmad-output/implementation-artifacts/2-3-device-registry-descriptionfetchstate-machine-eager-description-dispatcher.md`

- [Source: epics.md#Story-2.4] â€” verbatim ACs (lines 928â€“989).
- [Source: architecture.md Â§SSDP datagram flow] â€” integration diagram (DiscoveryService routing paths).
- [Source: architecture.md#Decision-2] â€” socket topology, channel config, `SsdpDatagram`/`SsdpSource` shape.
- [Source: architecture.md#Amendment-A22] â€” multicast-only test delivery (does NOT apply to Story 2.4's channel-based tests).
- [Source: architecture.md#Amendment-A23] â€” DiscoveryService owns the same transport instance as AdapterScope; factory deferred to 5.2.
- [Source: architecture.md#Pattern-11] â€” `SsdpParse` mandatory context = `RemoteEndpoint`.
- [Source: src/ohSpy.Core/Models/SsdpDatagram.cs] â€” `(IPEndPoint Remote, byte[] Payload, DateTime ArrivalUtc, SsdpSource Source)`.
- [Source: src/ohSpy.Core/Discovery/ISsdpTransport.cs:41] â€” `IncomingDatagrams` ChannelReader; "consumer (DiscoveryService, Story 2.4) owns the read side."
- [Source: src/ohSpy.Core/Devices/DeviceRegistry.cs:52â€“74] â€” `OnAlive(...)` and `OnByebye(Guid)` internal signatures.
- [Source: src/ohSpy.Core/Devices/EagerDescriptionDispatcher.cs:37] â€” `EntryNeedsFetch` subscription; DiscoveryService does NOT call FetchAsync.
- [Source: src/ohSpy.Core/Discovery/AdapterScope.cs:35,38] â€” `CurrentAdapterIPv4` + `AdapterToken`.
- [Source: src/ohSpy.App/App.xaml.cs:107,124-136] â€” `StartAdapterScopeAsync` pattern (to be extended in Task 4.2).
- [Source: tests/ohSpy.Core.Tests/Fakes/FakeSsdpTransport.cs:15] â€” capacity 1; don't use for DiscoveryService integration tests.
- [Source: tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs] â€” synchronous Post for test isolation.
- [Source: 2-3-â€¦md#Dev-Agent-Record] â€” A29 allocation test rule; A27 CTS dispose; A28 `UdnMatches`.
- [Source: project_ohspy memory] â€” native Windows desktop UPnP inspector; raw-BCL UPnP; no CI (pre-commit chaos hook).

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- VSTHRD003 on `DiscoveryService.DisposeAsync` awaiting `_readLoop` field — same pattern as `SsdpTransport`; suppressed with `#pragma warning disable/restore VSTHRD003`.

### Completion Notes List

- Implemented `SsdpAnnouncement` public sealed record with `IsRootDevice` computed property (NT-only check, as designed — routing code uses `effectiveNt = ann.NT ?? ann.ST`).
- Implemented `SsdpParser` (internal sealed) with lenient header parsing, `ExtractUuid` static helper, and `Warning(SsdpParse)` on malformed datagrams.
- Implemented `IDiscoveryService` / `DiscoveryService` with `ReadLoopAsync` on `Task.Run`, `RouteOnUiThread` via `IUiDispatcher.Post`, `adapterToken` capture, and `RescanAsync` stub.
- DI: `SsdpParser` + `DiscoveryService` (concrete) + `IDiscoveryService` (forward) registered as singletons in `ServiceRegistration.cs`.
- `App.xaml.cs`: `StartAdapterScopeAsync` extended to accept `DiscoveryService`; discovery started after scope start, zero-adapter path skipped.
- Created `ChannelSsdpTransport` (cap=256) and `SsdpDatagramBuilder` test fakes.
- 28 new tests (14 parser + 9 integration + 4 announcement + 1 cache + 1 location extras). Baseline 201 → 229 passing, 2 skips unchanged.
- Chaos suite still exactly 1. Build 0 warnings / 0 errors.

### File List

- src/ohSpy.Core/Discovery/SsdpAnnouncement.cs (new)
- src/ohSpy.Core/Discovery/SsdpParser.cs (new)
- src/ohSpy.Core/Discovery/IDiscoveryService.cs (new)
- src/ohSpy.Core/Discovery/DiscoveryService.cs (new)
- src/ohSpy.App/Composition/ServiceRegistration.cs (modified)
- src/ohSpy.App/App.xaml.cs (modified)
- tests/ohSpy.Core.Tests/Fakes/ChannelSsdpTransport.cs (new)
- tests/ohSpy.Core.Tests/Fakes/SsdpDatagramBuilder.cs (new)
- tests/ohSpy.Core.Tests/Discovery/SsdpParserTests.cs (new)
- tests/ohSpy.Core.Tests/Discovery/SsdpAnnouncementTests.cs (new)
- tests/ohSpy.Core.Tests/Discovery/DiscoveryServiceTests.cs (new)

## Review Findings

### Patch (1)

- [x] [Review][Patch] Bare LF line endings not handled — `text.Split("\r\n")` silently produces a single-element array for `\n`-only datagrams; headers are not parsed, all fields null, no diagnostic emitted [SsdpParser.cs:Parse] — FIXED: normalise to `\n` before split

### Deferred (11)

- [x] [Review][Defer] DiscoveryService not disposed on shutdown — App.xaml.cs does not call `DiscoveryService.DisposeAsync`; adapterToken cancellation is the effective cleanup [App.xaml.cs] — deferred, pre-existing App-lifecycle pattern
- [x] [Review][Defer] UTF-8 BOM bytes not stripped before first-line check — `Encoding.UTF8.GetString(byte[])` does not strip BOM; conformant SSDP devices do not emit BOM in UDP datagrams [SsdpParser.cs:Parse] — deferred, non-conformant device edge case
- [x] [Review][Defer] AnnouncementReceived ordering after registry mutation undocumented — event raised after `OnAlive`/`OnByebye`; subscribers see post-mutation state; ordering not in XML doc [DiscoveryService.cs:RouteOnUiThread] — deferred, documentation gap
- [x] [Review][Defer] `_started` guard does not prevent `StartAsync` after `DisposeAsync` — second `StartAsync` after dispose creates an orphaned background task [DiscoveryService.cs:StartAsync] — deferred, unrealistic call sequence in singleton DI context
- [x] [Review][Defer] AC-2.4.4 cancellation test uses arbitrary 200 ms timeout — racy on heavily-loaded CI [DiscoveryServiceTests.cs:StartAsync_CancelToken_LoopExitsCleanly_AC244] — deferred, acceptable for current test environment
- [x] [Review][Defer] `IsRootDevice` only checks NT — returns false for M-SEARCH responses where `ST == "upnp:rootdevice"` and `NT == null`; misleading for future consumers not using `effectiveNt` pattern [SsdpAnnouncement.cs:IsRootDevice] — deferred, by spec design; anti-pattern documented in spec
- [x] [Review][Defer] Folded HTTP headers (obs-fold) not handled — continuation lines beginning with whitespace are treated as unknown headers [SsdpParser.cs:Parse] — deferred, deprecated by RFC 7230; not observed in practice
- [x] [Review][Defer] HTTP/1.0 M-SEARCH responses not accepted — `StartsWith("HTTP/1.1 200")` rejects UPnP 1.0 devices that respond with `HTTP/1.0 200 OK` [SsdpParser.cs:Parse] — deferred, spec specifies HTTP/1.1; would need spec amendment
- [x] [Review][Defer] `DisposeAsync` can hang if read loop does not exit — no internal timeout; relies on caller cancelling tokens first [DiscoveryService.cs:DisposeAsync] — deferred, production teardown always cancels adapterToken before dispose
- [x] [Review][Defer] `adapterToken` may be cancelled before `RouteOnUiThread` closure executes — entry added to registry with pre-cancelled `DeviceCts`; fetch immediately fails; self-healing on next alive [DiscoveryService.cs:RouteOnUiThread] — deferred, transient state during shutdown; acceptable
- [x] [Review][Defer] `discovery.StartAsync` exceptions swallowed by `StartAdapterScopeAsync` catch block — programming errors (double-start) logged as "adapter startup failed" [App.xaml.cs:StartAdapterScopeAsync] — deferred, double-start is unreachable in normal usage

## Change Log

- 2026-06-02: Story 2.4 implemented — SsdpAnnouncement record, SsdpParser, IDiscoveryService/DiscoveryService, DI wiring, App startup integration. 28 new tests (201→229 passing). Build 0 warnings. (claude-sonnet-4-6)

