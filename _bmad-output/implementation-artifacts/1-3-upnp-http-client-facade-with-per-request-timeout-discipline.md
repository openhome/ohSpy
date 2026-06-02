---
baseline_commit: b9ea15d1c10c41094d06f91fb36c299bd27483e3
---

# Story 1.3: UPnP HTTP Client Facade with Per-Request Timeout Discipline

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an **ohSpy developer**,
I want **a typed `IUpnpHttpClient` facade whose every method bakes a per-request timeout and a size cap into a linked CTS internally**,
so that **downstream stories cannot accidentally inherit `HttpClient`'s 100 s default timeout or leak hung sockets — closing the structural defect that traced to the prior tool's "slow devices hang the app" complaint**.

## Acceptance Criteria

> Each AC is restated verbatim from epics.md §Story 1.3 (lines 521–580). The architecture-level AC IDs (AC-3.x, AC-11.x, A5) cited inline trace back to architecture.md §Decision-3, §Decision-11, and §Amendment-A5.

### AC-1 — `UpnpException` hierarchy shape (A5)

**Given** `ohSpy.Core/Http/UpnpExceptions.cs`
**When** I inspect the type hierarchy
**Then** `UpnpException` is `abstract` and never thrown directly (A5)
**And** four sealed derivatives exist: `UpnpTimeoutException`, `UpnpTransportException`, `UpnpProtocolException`, `UpnpFaultException` (A5)
**And** each carries the type-specific structured context (Url + Budget + Elapsed on Timeout; Url + StatusCode on Transport; Url on Protocol; Url + ActionName + ErrorCode + ErrorDescription on Fault) (A5)
**And** none of the types is `[Serializable]` (A5)

### AC-2 — `HttpTimeoutOptions` defaults (D11)

**Given** `ohSpy.Core/Http/HttpTimeoutOptions.cs`
**When** I read the defaults
**Then** they match Decision 11 exactly: `DescriptionFetch` 5 s, `ScpdFetch` 10 s, `SoapInvoke` 10 s, `GenaSubscribe` 5 s, `GenaUnsubscribe` 5 s, `ConnectTimeout` 5 s, `KeepAlivePingDelay` 15 s, `KeepAlivePingTimeout` 5 s, `CallbackHeaders` 5 s, `CallbackBody` 5 s (AC-11.1)
**And** the type is registered via `services.Configure<HttpTimeoutOptions>` in `ServiceRegistration` (AC-11.3)

### AC-3 — `IUpnpHttpClient` interface surface

**Given** `ohSpy.Core/Http/IUpnpHttpClient.cs`
**When** I inspect the interface
**Then** it declares `FetchDeviceDescriptionAsync`, `FetchScpdAsync`, `InvokeActionAsync`, `SubscribeAsync`, `RenewSubscriptionAsync`, `UnsubscribeAsync` — each taking `CancellationToken ct` as the last parameter
**And** `FetchScpdAsync` returns `Task<byte[]>` (raw SCPD body — parsing is a separate concern per Story 1.4 / D5 revision)

### AC-4 — Timeout discipline + headers/body coverage

**Given** the `UpnpHttpClient` impl
**When** any method runs
**Then** the underlying `HttpClient` has `Timeout = Timeout.InfiniteTimeSpan` — the per-op linked CTS is the SOLE timeout source (AC-3.1 + AC-11.2)
**And** every call site composes `CancellationTokenSource.CreateLinkedTokenSource(externalToken, new CTS(_opts.<budget>))`
**And** every `SendAsync` uses `HttpCompletionOption.ResponseHeadersRead` AND threads the linked token through the body-read (`ReadAsStringAsync(linked.Token)` or `ReadAsByteArrayAsync(linked.Token)`) so both header and body phases are timeout-covered (AC-3.5 closes the gap the prior tool had)
**And** the response body size is checked against the per-method cap from `HttpTimeoutOptions`/code constants before reading the body (description 1 MB, SCPD 2 MB, SOAP 1 MB, GENA 64 KB)
**And** `SocketsHttpHandler` is configured with `UseProxy=false`, `AllowAutoRedirect=false`, `ConnectTimeout = _opts.ConnectTimeout`, `KeepAlivePingDelay = _opts.KeepAlivePingDelay`, `KeepAlivePingTimeout = _opts.KeepAlivePingTimeout`, `MaxResponseHeadersLength = 16` (KB) (AC-11.4 covers KeepAlive surfaces hung TCP within 20 s ± 5 s)

### AC-5 — Per-op timeout fires (`UpnpTimeoutException`)

**Given** the facade's exception-mapping discipline
**When** a per-op CTS fires (timeout)
**Then** a `UpnpTimeoutException` is thrown carrying Url + Budget + Elapsed
**And** a `Warning` diagnostic (`DiagCategories.HttpTimeout`) is emitted with Url + Elapsed + Budget context (test stub allowed if Story 1.5 hasn't shipped yet — production wiring comes after Story 1.5)

### AC-6 — Caller cancellation (`OperationCanceledException` propagates as-is)

**When** the external (caller) token fires
**Then** `OperationCanceledException` propagates as-is — NOT wrapped in `UpnpTimeoutException` (AC-3.6)
**And** no diagnostic is emitted on caller-initiated cancellation

### AC-7 — Transport error (`UpnpTransportException`)

**When** `HttpRequestException` is raised by the underlying transport
**Then** `UpnpTransportException` is thrown carrying Url and (when present) StatusCode

### AC-8 — Oversize body (`UpnpProtocolException`)

**When** the body exceeds the per-method size cap
**Then** `UpnpProtocolException` is thrown and the response is disposed (AC-3.4)

### AC-9 — SOAP fault (`UpnpFaultException`)

**When** SOAP returns 500 with a `<s:Fault><detail><UPnPError><errorCode/>` body
**Then** `UpnpFaultException` is thrown carrying ActionName + ErrorCode + ErrorDescription (AC-3.3)

### AC-10 — Custom HTTP methods (SUBSCRIBE / UNSUBSCRIBE)

**Given** `SUBSCRIBE` / `UNSUBSCRIBE` semantics
**When** the facade calls those methods
**Then** the underlying `HttpRequestMessage.Method` is the exact string `"SUBSCRIBE"` or `"UNSUBSCRIBE"` (AC-3.2)

### AC-11 — Test infrastructure (hand-rolled `TestHttpMessageHandler`)

**Given** test infrastructure
**When** I look at `tests/ohSpy.Core.Tests/Fakes/TestHttpMessageHandler.cs`
**Then** it is a hand-rolled `HttpMessageHandler` (not Moq `Protected()`) reusable across `UpnpHttpClient` unit tests
**And** AC-3.1..AC-3.6 + AC-11.1..AC-11.3 are exercised by tests carrying `[Trait("ac", "AC-3.x")]` and `[Trait("ac", "AC-11.x")]`

## Tasks / Subtasks

> Tasks are ordered to land Core types first (zero WinUI deps, fully unit-testable), then `UpnpHttpClient` impl, then test infrastructure and tests, then DI wiring. AC mappings explicit. Architecture's pinned versions / paths / patterns are the contract — do not deviate.

### Task 1 — Author minimal diagnostic surface (Core/Diagnostics, prereq for Story 1.5) (AC: #5)

> Story 1.5 implements the full diagnostic pipeline (sinks, emitter, ring/file output). Story 1.3 introduces only the surface needed to make `UpnpHttpClient` compile + emit. Story 1.5 will ADD to this; nothing here gets replaced (just extended).

- [x] **1.1** Create folder `src/ohSpy.Core/Diagnostics/`.
- [x] **1.2** Create `src/ohSpy.Core/Diagnostics/DiagnosticContext.cs` — readonly record struct with the Story 1.3-relevant fields [Source: architecture.md §Decision-8, lines ~881–911]. Match D8's spec exactly so Story 1.5 doesn't need to mutate it:
  ```csharp
  namespace ohSpy.Core.Diagnostics;

  /// <summary>
  /// Structured context attached to a <see cref="IDiagnosticEmitter"/> call. Zero-allocation
  /// when default; all fields nullable so a caller can populate only the relevant ones.
  /// </summary>
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
  ```
- [x] **1.3** Create `src/ohSpy.Core/Diagnostics/DiagCategories.cs` — static class with only the categories Story 1.3 emits. Story 1.5 will add more (SSDP, SCPD, GENA, etc.):
  ```csharp
  namespace ohSpy.Core.Diagnostics;

  /// <summary>
  /// Single source of truth for diagnostic category strings. Each constant carries the
  /// mandatory <see cref="DiagnosticContext"/> fields per Pattern 11.
  /// </summary>
  public static class DiagCategories
  {
      /// <summary>Mandatory context: Url, Elapsed, Budget.</summary>
      public const string HttpTimeout = "Http.Timeout";

      /// <summary>Mandatory context: Url; StatusCode if present.</summary>
      public const string HttpTransport = "Http.Transport";

      /// <summary>Mandatory context: Url.</summary>
      public const string HttpOversizeBody = "Http.OversizeBody";
  }
  ```
- [x] **1.4** Create `src/ohSpy.Core/Diagnostics/IDiagnosticEmitter.cs` — the full D8 surface (4 severity methods). Story 1.3 only USES `Warning`, but introducing the full surface here means Story 1.5 doesn't need to widen the interface later:
  ```csharp
  namespace ohSpy.Core.Diagnostics;

  /// <summary>
  /// Fan-out emitter for structured diagnostic entries. Story 1.5 implements the
  /// production sinks (ring + rolling file); Story 1.3 ships only an interface +
  /// no-op impl so <c>UpnpHttpClient</c> can take this dependency.
  /// </summary>
  public interface IDiagnosticEmitter
  {
      void Verbose(string category, string message, DiagnosticContext context = default);
      void Information(string category, string message, DiagnosticContext context = default);
      void Warning(string category, string message, DiagnosticContext context = default);
      void Error(string category, string message, DiagnosticContext context = default);
  }
  ```
- [x] **1.5** Create `src/ohSpy.Core/Diagnostics/NoOpDiagnosticEmitter.cs` — the placeholder impl wired in DI until Story 1.5 ships:
  ```csharp
  namespace ohSpy.Core.Diagnostics;

  /// <summary>
  /// No-op <see cref="IDiagnosticEmitter"/> placeholder. Story 1.5 replaces the DI
  /// registration with the real <c>DiagnosticEmitter</c> + ring/file sinks. Marked
  /// <c>internal</c> because nothing outside DI should ever reference it.
  /// </summary>
  internal sealed class NoOpDiagnosticEmitter : IDiagnosticEmitter
  {
      public void Verbose(string category, string message, DiagnosticContext context = default) { }
      public void Information(string category, string message, DiagnosticContext context = default) { }
      public void Warning(string category, string message, DiagnosticContext context = default) { }
      public void Error(string category, string message, DiagnosticContext context = default) { }
  }
  ```
- [x] **1.6** Do NOT create `DiagSeverity.cs`, `DiagnosticEntry.cs`, `IDiagnosticRingSink.cs`, `IDiagnosticFileSink.cs`, or the real `DiagnosticEmitter.cs`. Those are Story 1.5's deliverables.

### Task 2 — Author `UpnpExceptions.cs` (Core/Http, A5) (AC: #1)

- [x] **2.1** Create folder `src/ohSpy.Core/Http/`.
- [x] **2.2** Create `src/ohSpy.Core/Http/UpnpExceptions.cs` [Source: architecture.md §Amendment-A5, lines ~2520–2590], **with one corrected divergence on `UpnpTransportException`** — see warning below:
  ```csharp
  namespace ohSpy.Core.Http;

  /// <summary>
  /// Abstract base for UPnP-domain exceptions. Never thrown directly; consumers catch
  /// either <see cref="UpnpException"/> for "any UPnP problem" or one of the four
  /// sealed derivatives for type-specific handling.
  /// </summary>
  public abstract class UpnpException : Exception
  {
      protected UpnpException(string message) : base(message) { }
      protected UpnpException(string message, Exception inner) : base(message, inner) { }
  }

  /// <summary>
  /// Thrown when a per-operation timeout budget elapses before the request completes.
  /// Carries the originating URL plus the budget and actual elapsed time for diagnostics.
  /// </summary>
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

  /// <summary>
  /// Thrown on transport-layer failure (HttpRequestException, socket error, DNS, etc.).
  /// Carries the originating URL and the HTTP status code if one was received.
  /// </summary>
  public sealed class UpnpTransportException : UpnpException
  {
      public Uri Url { get; }
      public int? StatusCode { get; }

      public UpnpTransportException(Uri url, string message, int? statusCode = null, Exception? inner = null)
          : base(message, inner ?? new InvalidOperationException(message))
      {
          Url = url; StatusCode = statusCode;
      }
  }

  /// <summary>
  /// Thrown when the response violates UPnP protocol expectations: oversize body,
  /// malformed framing, missing required header, etc.
  /// </summary>
  public sealed class UpnpProtocolException : UpnpException
  {
      public Uri Url { get; }
      public UpnpProtocolException(Uri url, string message) : base(message) { Url = url; }
  }

  /// <summary>
  /// Thrown when a SOAP action invocation returns a structured UPnP fault (HTTP 500 +
  /// <c>&lt;s:Fault&gt;</c> body). Carries the action name plus the UPnP error code
  /// and description from the fault detail.
  /// </summary>
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
- [x] **2.3** **A5 smell flagged for amendment** — the `UpnpTransportException` ctor's `inner ?? new InvalidOperationException(message)` synthesises a fake inner exception when none is supplied. The above code MATCHES THE ARCHITECTURE VERBATIM (don't fight it inline). Instead, **after Story 1.3 lands**, recommend an architecture amendment that changes the base call to `: base(message, inner)` (accepting null `inner` — the `Exception(string, Exception?)` ctor handles null cleanly). Add this to the Dev Agent Record's "Architecture amendments uncovered" section the same way Story 1.1 surfaced A6/A7/A8.
- [x] **2.4** No `[Serializable]` attribute on any of the five types. Confirmed by A5; deprecated guidance in modern .NET.

### Task 3 — Author `HttpTimeoutOptions.cs` (Core/Http, D11) (AC: #2, #4)

- [x] **3.1** Create `src/ohSpy.Core/Http/HttpTimeoutOptions.cs` [Source: architecture.md §Decision-11, lines ~1393–1411 + size-cap inference from §Decision-3 lines 349–356]:
  ```csharp
  namespace ohSpy.Core.Http;

  /// <summary>
  /// Per-request timeout budgets and response-body size caps for <see cref="IUpnpHttpClient"/>
  /// and friends. Bound via <c>services.Configure&lt;HttpTimeoutOptions&gt;(...)</c> (Pattern 7);
  /// resolved via <see cref="Microsoft.Extensions.Options.IOptions{T}"/> at consumer ctors.
  /// </summary>
  public sealed class HttpTimeoutOptions
  {
      // ─── IUpnpHttpClient per-request budgets (Decision 3) ───
      public TimeSpan DescriptionFetch     { get; init; } = TimeSpan.FromSeconds(5);
      public TimeSpan ScpdFetch            { get; init; } = TimeSpan.FromSeconds(10);
      public TimeSpan SoapInvoke           { get; init; } = TimeSpan.FromSeconds(10);
      public TimeSpan GenaSubscribe        { get; init; } = TimeSpan.FromSeconds(5);
      public TimeSpan GenaUnsubscribe      { get; init; } = TimeSpan.FromSeconds(5);

      // ─── SocketsHttpHandler (shared HttpClient) ───
      public TimeSpan ConnectTimeout       { get; init; } = TimeSpan.FromSeconds(5);
      public TimeSpan KeepAlivePingDelay   { get; init; } = TimeSpan.FromSeconds(15);
      public TimeSpan KeepAlivePingTimeout { get; init; } = TimeSpan.FromSeconds(5);

      // ─── Inbound GENA callback host (Decision 4 — consumed by Story 4.1) ───
      public TimeSpan CallbackHeaders      { get; init; } = TimeSpan.FromSeconds(5);
      public TimeSpan CallbackBody         { get; init; } = TimeSpan.FromSeconds(5);

      // ─── Per-method response-body size caps (bytes) ───
      // From D3 lines 349–356. The architecture text says these "should live in HttpTimeoutOptions";
      // this story places them here so a single Configure<> call tunes timeouts AND caps.
      public int MaxDescriptionBytes       { get; init; } = 1_048_576;   // 1 MB
      public int MaxScpdBytes              { get; init; } = 2_097_152;   // 2 MB
      public int MaxSoapResponseBytes      { get; init; } = 1_048_576;   // 1 MB
      public int MaxGenaResponseBytes      { get; init; } = 65_536;      // 64 KB
  }
  ```
- [x] **3.2** All fields have `init` setters (records-style immutability) so test-side `services.Configure<HttpTimeoutOptions>(o => o.ScpdFetch = TimeSpan.FromMilliseconds(100))` works without mutating a shared instance.
- [x] **3.3** No validation in the type itself (caller bears responsibility). The defaults are valid by construction.

### Task 4 — Author thin model records (Core/Http or Core/Soap) (AC: #3)

> Story 1.3's `InvokeActionAsync` / `SubscribeAsync` / `RenewSubscriptionAsync` need data-carrier types. Story 3.1 (SOAP envelope builder) and Story 4.2 (subscription client) will extend these.

- [x] **4.1** Create `src/ohSpy.Core/Http/SoapRequest.cs` (minimal record, just enough for Story 1.3's `InvokeActionAsync` impl):
  ```csharp
  namespace ohSpy.Core.Http;

  /// <summary>
  /// Pre-built SOAP request envelope ready for POST. Story 3.1 will introduce a builder
  /// that constructs this from <c>ScpdAction</c> + argument values; for now it's
  /// constructed manually by test code.
  /// </summary>
  /// <param name="ControlUrl">Absolute URL of the service's controlURL endpoint.</param>
  /// <param name="ServiceType">UPnP serviceType URN, e.g. <c>urn:schemas-upnp-org:service:AVTransport:1</c>.</param>
  /// <param name="ActionName">Action name as declared in SCPD.</param>
  /// <param name="EnvelopeXml">Complete SOAP envelope XML, UTF-8 encoded.</param>
  public sealed record SoapRequest(
      Uri ControlUrl,
      string ServiceType,
      string ActionName,
      string EnvelopeXml);
  ```
- [x] **4.2** Create `src/ohSpy.Core/Http/SoapResponse.cs`:
  ```csharp
  namespace ohSpy.Core.Http;

  using System.Net;

  /// <summary>
  /// Raw SOAP response. Story 3.1 will introduce a parser that lifts output args out of
  /// <see cref="ResponseXml"/>.
  /// </summary>
  /// <param name="StatusCode">HTTP status of the response (typically 200 OK; 500 only when a SOAP fault was raised and converted to <see cref="UpnpFaultException"/>).</param>
  /// <param name="ResponseXml">Complete response envelope as a UTF-8 string.</param>
  public sealed record SoapResponse(HttpStatusCode StatusCode, string ResponseXml);
  ```
- [x] **4.3** Create `src/ohSpy.Core/Http/SubscribeResponse.cs`:
  ```csharp
  namespace ohSpy.Core.Http;

  /// <summary>
  /// Result of a successful SUBSCRIBE or RENEW. <see cref="Sid"/> is the subscription
  /// identifier from the response's <c>SID:</c> header; <see cref="Timeout"/> is parsed
  /// from the <c>TIMEOUT: Second-N</c> header.
  /// </summary>
  /// <param name="Sid">Subscription identifier (e.g. <c>uuid:abcd-1234-...</c>).</param>
  /// <param name="Timeout">
  /// Granted lease duration from the device — consumers must RENEW before this expires.
  /// <b>NOT</b> the request timeout budget. See <see cref="HttpTimeoutOptions.GenaSubscribe"/>
  /// for the per-request budget that bounds the SUBSCRIBE call itself.
  /// </param>
  public sealed record SubscribeResponse(string Sid, TimeSpan Timeout);
  ```

### Task 5 — Author `IUpnpHttpClient` interface (Core/Http) (AC: #3, #10)

- [x] **5.1** Create `src/ohSpy.Core/Http/IUpnpHttpClient.cs`:
  ```csharp
  namespace ohSpy.Core.Http;

  /// <summary>
  /// Typed facade over a single shared <see cref="HttpClient"/> for all UPnP outbound HTTP.
  /// Every method bakes a per-request timeout (via linked CTS) and a per-response size cap
  /// into the call — there is no way for a consumer to accidentally inherit
  /// <see cref="HttpClient.Timeout"/>'s 100 s default or to skip the size guard. This is
  /// the structural antidote to the prior tool's "slow devices hang the app" defect.
  /// </summary>
  /// <remarks>
  /// All Fetch methods return <c>byte[]</c> — parsing is a separate concern (Story 1.4 / D5
  /// revision). The architecture's original D3 text shows <c>FetchDeviceDescriptionAsync</c>
  /// returning <c>Task&lt;DeviceDescription&gt;</c>; that is corrected here to mirror
  /// <c>FetchScpdAsync</c>'s raw-bytes return for symmetry. See Dev Notes for the
  /// architecture-amendment recommendation.
  /// </remarks>
  public interface IUpnpHttpClient
  {
      /// <summary>
      /// GET the device description XML from <paramref name="locationUrl"/> (the SSDP
      /// <c>LOCATION</c> header). Returns raw bytes; parsing is the caller's concern
      /// (typically <c>IDeviceDescriptionParser</c> from Story 1.4).
      /// </summary>
      Task<byte[]> FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct);

      /// <summary>
      /// GET the service control protocol description (SCPD) XML from <paramref name="scpdUrl"/>.
      /// Returns raw bytes; incremental parsing is the caller's concern
      /// (<c>IScpdParser.StreamActionsAsync</c> from Story 1.4 + FR-100).
      /// </summary>
      Task<byte[]> FetchScpdAsync(Uri scpdUrl, CancellationToken ct);

      /// <summary>
      /// POST a SOAP action envelope to a service's control URL. Returns the response
      /// envelope on 200 OK; throws <see cref="UpnpFaultException"/> on 500 + structured
      /// <c>&lt;s:Fault&gt;</c> body.
      /// </summary>
      Task<SoapResponse> InvokeActionAsync(SoapRequest request, CancellationToken ct);

      /// <summary>
      /// Send a SUBSCRIBE request to a service's eventSubURL with the given callback URL.
      /// Returns the granted subscription on success.
      /// </summary>
      Task<SubscribeResponse> SubscribeAsync(
          Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct);

      /// <summary>
      /// Renew an existing subscription identified by <paramref name="sid"/>. Returns
      /// the updated lease on success.
      /// </summary>
      Task<SubscribeResponse> RenewSubscriptionAsync(
          Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct);

      /// <summary>
      /// Tear down a subscription identified by <paramref name="sid"/>. Best-effort —
      /// fire-and-forget on popup close. Throws on transport/timeout failure so callers
      /// can decide whether to retry.
      /// </summary>
      Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct);
  }
  ```
- [x] **5.2** Every method takes `CancellationToken ct` as the LAST parameter — Pattern 6 convention.
- [x] **5.3** Every async method ends in `Async` — Pattern 6 convention.

### Task 6 — Author `UpnpHttpClient` impl (Core/Http) (AC: #4, #5, #6, #7, #8, #9, #10)

- [x] **6.1** Create `src/ohSpy.Core/Http/UpnpHttpClient.cs` implementing `IUpnpHttpClient`. Recommended skeleton:
  ```csharp
  namespace ohSpy.Core.Http;

  using System.Diagnostics;
  using System.Net;
  using System.Net.Http.Headers;
  using System.Text;
  using System.Xml;
  using Microsoft.Extensions.Options;
  using ohSpy.Core.Diagnostics;

  /// <summary>
  /// Production implementation of <see cref="IUpnpHttpClient"/>. Owns a single shared
  /// <see cref="HttpClient"/> over a configured <see cref="SocketsHttpHandler"/>.
  /// All per-op timeouts are enforced via linked <see cref="CancellationTokenSource"/>
  /// (NOT <see cref="HttpClient.Timeout"/>, which is set to infinite).
  /// </summary>
  internal sealed class UpnpHttpClient : IUpnpHttpClient, IDisposable
  {
      private readonly HttpClient _http;
      private readonly HttpTimeoutOptions _opts;
      private readonly IDiagnosticEmitter _diag;

      public UpnpHttpClient(IOptions<HttpTimeoutOptions> options, IDiagnosticEmitter diag)
      {
          ArgumentNullException.ThrowIfNull(options);
          ArgumentNullException.ThrowIfNull(diag);
          _opts = options.Value;
          _diag = diag;

          var handler = new SocketsHttpHandler
          {
              UseProxy = false,
              AllowAutoRedirect = false,
              ConnectTimeout = _opts.ConnectTimeout,
              KeepAlivePingDelay = _opts.KeepAlivePingDelay,
              KeepAlivePingTimeout = _opts.KeepAlivePingTimeout,
              MaxResponseHeadersLength = 16,                    // 16 KB
              PooledConnectionLifetime = TimeSpan.FromMinutes(2),
          };
          _http = new HttpClient(handler, disposeHandler: true)
          {
              Timeout = Timeout.InfiniteTimeSpan,               // SOLE timeout = per-op linked CTS
              DefaultRequestVersion = HttpVersion.Version11,
          };
      }

      // Test-only ctor — accepts a pre-built HttpClient (typically over TestHttpMessageHandler).
      internal UpnpHttpClient(HttpClient httpForTests, IOptions<HttpTimeoutOptions> options, IDiagnosticEmitter diag)
      {
          ArgumentNullException.ThrowIfNull(httpForTests);
          ArgumentNullException.ThrowIfNull(options);
          ArgumentNullException.ThrowIfNull(diag);
          _http = httpForTests;
          _opts = options.Value;
          _diag = diag;
      }

      public Task<byte[]> FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct) =>
          GetBytesWithSizeCapAsync(locationUrl, _opts.DescriptionFetch, _opts.MaxDescriptionBytes, ct);

      public Task<byte[]> FetchScpdAsync(Uri scpdUrl, CancellationToken ct) =>
          GetBytesWithSizeCapAsync(scpdUrl, _opts.ScpdFetch, _opts.MaxScpdBytes, ct);

      // ─── shared GET implementation ───
      private async Task<byte[]> GetBytesWithSizeCapAsync(
          Uri url, TimeSpan budget, int maxBytes, CancellationToken external)
      {
          using var timeoutCts = new CancellationTokenSource(budget);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);

          var sw = Stopwatch.StartNew();
          try
          {
              using var req = new HttpRequestMessage(HttpMethod.Get, url);
              using var resp = await _http.SendAsync(
                  req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

              EnforceSizeCapOnHeaders(resp, url, maxBytes);
              var bytes = await ReadWithSizeCapAsync(resp, maxBytes, linked.Token).ConfigureAwait(false);
              return bytes;
          }
          catch (OperationCanceledException) when (external.IsCancellationRequested)
          {
              throw;                                            // caller cancelled: silent re-throw
          }
          catch (OperationCanceledException)
          {
              sw.Stop();
              _diag.Warning(DiagCategories.HttpTimeout, "request timed out",
                  new DiagnosticContext { Url = url.ToString(), Elapsed = sw.Elapsed, Budget = budget });
              throw new UpnpTimeoutException(url, budget, sw.Elapsed);
          }
          catch (HttpRequestException ex)
          {
              _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                  new DiagnosticContext { Url = url.ToString(), StatusCode = (int?)ex.StatusCode });
              throw new UpnpTransportException(url, ex.Message, (int?)ex.StatusCode, ex);
          }
      }

      // Throws UpnpProtocolException + disposes resp if Content-Length already exceeds cap.
      private static void EnforceSizeCapOnHeaders(HttpResponseMessage resp, Uri url, int maxBytes)
      {
          var len = resp.Content.Headers.ContentLength;
          if (len.HasValue && len.Value > maxBytes)
          {
              resp.Dispose();
              throw new UpnpProtocolException(url,
                  $"response body declared {len.Value} bytes; per-method cap is {maxBytes}");
          }
      }

      // Streaming size guard: throws UpnpProtocolException if cumulative bytes exceed cap.
      // Handles chunked transfer (null Content-Length) — the only safe way to enforce caps
      // when the server doesn't declare length up front.
      private async Task<byte[]> ReadWithSizeCapAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
      {
          await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
          using var buffer = new MemoryStream();
          var chunk = new byte[8192];
          int read;
          long total = 0;
          while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
          {
              total += read;
              if (total > maxBytes)
              {
                  _diag.Warning(DiagCategories.HttpOversizeBody, "body exceeded per-method cap",
                      new DiagnosticContext { Url = resp.RequestMessage?.RequestUri?.ToString() });
                  throw new UpnpProtocolException(
                      resp.RequestMessage?.RequestUri ?? new Uri("about:blank"),
                      $"response body exceeded {maxBytes} bytes mid-read");
              }
              await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
          }
          return buffer.ToArray();
      }

      public async Task<SoapResponse> InvokeActionAsync(SoapRequest request, CancellationToken external)
      {
          ArgumentNullException.ThrowIfNull(request);
          using var timeoutCts = new CancellationTokenSource(_opts.SoapInvoke);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);
          var sw = Stopwatch.StartNew();
          try
          {
              using var req = new HttpRequestMessage(HttpMethod.Post, request.ControlUrl)
              {
                  Content = new StringContent(request.EnvelopeXml, Encoding.UTF8, "text/xml"),
              };
              // SOAPAction MUST be quoted: "urn:..#ActionName"
              req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{request.ServiceType}#{request.ActionName}\"");

              using var resp = await _http.SendAsync(
                  req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

              EnforceSizeCapOnHeaders(resp, request.ControlUrl, _opts.MaxSoapResponseBytes);
              var bytes = await ReadWithSizeCapAsync(resp, _opts.MaxSoapResponseBytes, linked.Token).ConfigureAwait(false);
              var responseXml = Encoding.UTF8.GetString(bytes);

              if (resp.StatusCode == HttpStatusCode.InternalServerError)
              {
                  // SOAP fault path — try to parse <s:Fault><detail><UPnPError><errorCode/></UPnPError></detail>
                  if (TryParseUPnPError(responseXml, out var errorCode, out var errorDescription))
                  {
                      throw new UpnpFaultException(request.ControlUrl, request.ActionName, errorCode, errorDescription);
                  }
                  // Malformed fault → transport error. Emit diagnostic before throw
                  // (this path bypasses the catch-block diagnostic since the throw originates inside try).
                  _diag.Warning(DiagCategories.HttpTransport, "HTTP 500 without parseable UPnPError",
                      new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName, StatusCode = 500 });
                  throw new UpnpTransportException(request.ControlUrl,
                      "HTTP 500 without parseable UPnPError", 500);
              }
              if (!resp.IsSuccessStatusCode)
              {
                  _diag.Warning(DiagCategories.HttpTransport, $"unexpected status {(int)resp.StatusCode}",
                      new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName, StatusCode = (int)resp.StatusCode });
                  throw new UpnpTransportException(request.ControlUrl,
                      $"unexpected status {(int)resp.StatusCode}", (int)resp.StatusCode);
              }
              return new SoapResponse(resp.StatusCode, responseXml);
          }
          catch (OperationCanceledException) when (external.IsCancellationRequested) { throw; }
          catch (OperationCanceledException)
          {
              sw.Stop();
              _diag.Warning(DiagCategories.HttpTimeout, "SOAP invoke timed out",
                  new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName,
                                           Elapsed = sw.Elapsed, Budget = _opts.SoapInvoke });
              throw new UpnpTimeoutException(request.ControlUrl, _opts.SoapInvoke, sw.Elapsed);
          }
          catch (HttpRequestException ex)
          {
              _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                  new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName,
                                           StatusCode = (int?)ex.StatusCode });
              throw new UpnpTransportException(request.ControlUrl, ex.Message, (int?)ex.StatusCode, ex);
          }
      }

      // Minimal inline UPnPError parser. Story 3.1 (SOAP envelope builder + fault parser)
      // will replace this with a fuller XML parser; for now we extract just errorCode +
      // errorDescription from the SOAP fault envelope.
      private static bool TryParseUPnPError(string xml, out int errorCode, out string errorDescription)
      {
          errorCode = 0;
          errorDescription = string.Empty;
          try
          {
              var settings = new XmlReaderSettings
              {
                  DtdProcessing = DtdProcessing.Prohibit,
                  XmlResolver = null,
                  IgnoreWhitespace = true,
                  IgnoreComments = true,
              };
              using var reader = XmlReader.Create(new StringReader(xml), settings);
              while (reader.Read())
              {
                  if (reader.NodeType == XmlNodeType.Element)
                  {
                      if (reader.LocalName == "errorCode")
                      {
                          var v = reader.ReadElementContentAsString();
                          int.TryParse(v, out errorCode);
                      }
                      else if (reader.LocalName == "errorDescription")
                      {
                          errorDescription = reader.ReadElementContentAsString();
                      }
                  }
              }
              return errorCode != 0;
          }
          catch
          {
              return false;
          }
      }

      public Task<SubscribeResponse> SubscribeAsync(
          Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct)
      {
          ArgumentNullException.ThrowIfNull(eventSubUrl);
          ArgumentNullException.ThrowIfNull(callbackUrl);
          var headers = new[]
          {
              ("CALLBACK", $"<{callbackUrl}>"),
              ("NT", "upnp:event"),
              ("TIMEOUT", $"Second-{(int)requestedTimeout.TotalSeconds}"),
          };
          return SendSubscribeOrRenewAsync(eventSubUrl, headers, ct);
      }

      public Task<SubscribeResponse> RenewSubscriptionAsync(
          Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct)
      {
          ArgumentNullException.ThrowIfNull(eventSubUrl);
          ArgumentException.ThrowIfNullOrEmpty(sid);
          var headers = new[]
          {
              ("SID", sid),
              ("TIMEOUT", $"Second-{(int)requestedTimeout.TotalSeconds}"),
          };
          return SendSubscribeOrRenewAsync(eventSubUrl, headers, ct);
      }

      private async Task<SubscribeResponse> SendSubscribeOrRenewAsync(
          Uri eventSubUrl, (string Name, string Value)[] headers, CancellationToken external)
      {
          using var timeoutCts = new CancellationTokenSource(_opts.GenaSubscribe);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);
          var sw = Stopwatch.StartNew();
          try
          {
              using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), eventSubUrl);
              foreach (var (name, value) in headers)
                  req.Headers.TryAddWithoutValidation(name, value);

              using var resp = await _http.SendAsync(
                  req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
              EnforceSizeCapOnHeaders(resp, eventSubUrl, _opts.MaxGenaResponseBytes);

              if (!resp.IsSuccessStatusCode)
              {
                  throw new UpnpTransportException(eventSubUrl,
                      $"SUBSCRIBE returned {(int)resp.StatusCode}", (int)resp.StatusCode);
              }
              if (!resp.Headers.TryGetValues("SID", out var sidValues))
                  throw new UpnpProtocolException(eventSubUrl, "SUBSCRIBE response missing SID header");
              if (!resp.Headers.TryGetValues("TIMEOUT", out var timeoutValues))
                  throw new UpnpProtocolException(eventSubUrl, "SUBSCRIBE response missing TIMEOUT header");

              var sid = sidValues.First();
              var granted = ParseSecondHeader(timeoutValues.First())
                  ?? throw new UpnpProtocolException(eventSubUrl,
                      $"SUBSCRIBE response TIMEOUT header malformed: '{timeoutValues.First()}'");

              return new SubscribeResponse(sid, granted);
          }
          catch (OperationCanceledException) when (external.IsCancellationRequested) { throw; }
          catch (OperationCanceledException)
          {
              sw.Stop();
              _diag.Warning(DiagCategories.HttpTimeout, "SUBSCRIBE/RENEW timed out",
                  new DiagnosticContext { Url = eventSubUrl.ToString(), Elapsed = sw.Elapsed, Budget = _opts.GenaSubscribe });
              throw new UpnpTimeoutException(eventSubUrl, _opts.GenaSubscribe, sw.Elapsed);
          }
          catch (HttpRequestException ex)
          {
              _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                  new DiagnosticContext { Url = eventSubUrl.ToString(), StatusCode = (int?)ex.StatusCode });
              throw new UpnpTransportException(eventSubUrl, ex.Message, (int?)ex.StatusCode, ex);
          }
      }

      // Parses "Second-N" (the only legitimate UPnP TIMEOUT shape v1 supports).
      // Returns null on malformed input; "Second-infinite" is also returned as null
      // (caller decides whether to treat as "never expires" — for ohSpy we treat it
      // as an unsupported edge case and surface UpnpProtocolException above).
      private static TimeSpan? ParseSecondHeader(string value)
      {
          const string prefix = "Second-";
          if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
              return null;
          var rest = value[prefix.Length..];
          return int.TryParse(rest, out var seconds) && seconds > 0
              ? TimeSpan.FromSeconds(seconds)
              : null;
      }

      public async Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken external)
      {
          ArgumentNullException.ThrowIfNull(eventSubUrl);
          ArgumentException.ThrowIfNullOrEmpty(sid);
          using var timeoutCts = new CancellationTokenSource(_opts.GenaUnsubscribe);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);
          var sw = Stopwatch.StartNew();
          try
          {
              using var req = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), eventSubUrl);
              req.Headers.TryAddWithoutValidation("SID", sid);
              using var resp = await _http.SendAsync(
                  req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
              if (!resp.IsSuccessStatusCode)
              {
                  throw new UpnpTransportException(eventSubUrl,
                      $"UNSUBSCRIBE returned {(int)resp.StatusCode}", (int)resp.StatusCode);
              }
          }
          catch (OperationCanceledException) when (external.IsCancellationRequested) { throw; }
          catch (OperationCanceledException)
          {
              sw.Stop();
              _diag.Warning(DiagCategories.HttpTimeout, "UNSUBSCRIBE timed out",
                  new DiagnosticContext { Url = eventSubUrl.ToString(), Sid = sid, Elapsed = sw.Elapsed, Budget = _opts.GenaUnsubscribe });
              throw new UpnpTimeoutException(eventSubUrl, _opts.GenaUnsubscribe, sw.Elapsed);
          }
          catch (HttpRequestException ex)
          {
              _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                  new DiagnosticContext { Url = eventSubUrl.ToString(), Sid = sid, StatusCode = (int?)ex.StatusCode });
              throw new UpnpTransportException(eventSubUrl, ex.Message, (int?)ex.StatusCode, ex);
          }
      }

      public void Dispose() => _http.Dispose();
  }
  ```
- [x] **6.2** **`internal sealed`** — outside the App project nothing references `UpnpHttpClient` directly; consumers depend on `IUpnpHttpClient`.
- [x] **6.3** **Two constructors:** the production ctor (takes `IOptions<HttpTimeoutOptions>` + `IDiagnosticEmitter`, constructs its own HttpClient/handler) AND a test-only `internal` ctor that accepts a pre-built `HttpClient` (so tests can inject `TestHttpMessageHandler`). DI uses the production ctor; tests use the test-only ctor.
- [x] **6.4** Every `await` in this file uses `.ConfigureAwait(false)` — Pattern 6 Core convention.
- [x] **6.5** **The streaming size guard is non-negotiable.** The architecture's text says "the response body size is checked against the per-method cap BEFORE reading the body" — that works only when `Content-Length` is present. For chunked-transfer-encoded responses (no `Content-Length`), the impl above streams with a cumulative-byte guard. Both code paths land in `UpnpProtocolException` on overflow.
- [x] **6.6** **`HttpCompletionOption.ResponseHeadersRead` is non-negotiable.** Combined with `ReadAsStreamAsync(linked.Token)` (token-threaded body read), this closes the headers-vs-body-read gap AC-3.5 calls out — the prior tool's actual defect.

- [x] **6.7** **Expose `UpnpHttpClient`'s internal members to the test project via `InternalsVisibleTo`.** Without this, `tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs` cannot reach the `internal` test-only ctor that accepts a pre-built `HttpClient` (Task 6.3) — CS0122 at compile time. Add to `src/ohSpy.Core/ohSpy.Core.csproj` inside a new `<ItemGroup>`:
  ```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ohSpy.Core.Tests" />
  </ItemGroup>
  ```
  SDK-style csprojs support `<InternalsVisibleTo>` directly as an MSBuild item — no `Properties/AssemblyInfo.cs` needed. Verify with a build after adding.

### Task 7 — DI wiring (App/Composition) (AC: #2)

- [x] **7.1** **Read** `src/ohSpy.App/Composition/ServiceRegistration.cs` first (Story 1.2 created it with just `IUiDispatcher` registered). Modify in place — add to the existing `RegisterServices` method.
- [x] **7.2** Append to the existing method body:
  ```csharp
  // Story 1.3 — HTTP client facade (Decision 3) + timeout options (Decision 11).
  // Singleton lifetime: UpnpHttpClient owns a single shared HttpClient over a
  // SocketsHttpHandler with PooledConnectionLifetime=2min for DNS-refresh resilience.
  // Do NOT change to AddTransient — that would create a new handler+client per resolve
  // and exhaust sockets under SSDP burst.
  services.Configure<HttpTimeoutOptions>(_ => { /* defaults from HttpTimeoutOptions ctor */ });
  services.AddSingleton<IUpnpHttpClient, UpnpHttpClient>();

  // Story 1.3 — minimal diagnostic surface; Story 1.5 will REPLACE this with the
  // production DiagnosticEmitter + ring/file sinks.
  services.AddSingleton<IDiagnosticEmitter, NoOpDiagnosticEmitter>();
  ```
- [x] **7.3** Add the required usings to the top of `ServiceRegistration.cs`:
  ```csharp
  using ohSpy.Core.Diagnostics;
  using ohSpy.Core.Http;
  ```
- [x] **7.4** **Add `<PackageReference Include="Microsoft.Extensions.Options" />` to `src/ohSpy.Core/ohSpy.Core.csproj`** (no `Version=` attribute). The `PackageVersion` is **already pinned** in `Directory.Packages.props` (line 10, `10.0.0`) — do NOT duplicate it. `Core.csproj` currently has no explicit `<PackageReference>` items; this is the first. Without this reference, `IUpnpHttpClient`'s ctor (`IOptions<HttpTimeoutOptions>`) won't compile because Core's transitive graph doesn't currently pull `Microsoft.Extensions.Options` in (Core doesn't reference `Microsoft.Extensions.DependencyInjection` either). Do NOT add the reference to the App csproj — `UpnpHttpClient` lives in Core, and Core needs the using.

### Task 8 — Author `TestHttpMessageHandler` fake (Tests/Fakes) (AC: #11)

- [x] **8.1** Create `tests/ohSpy.Core.Tests/Fakes/TestHttpMessageHandler.cs`:
  ```csharp
  namespace ohSpy.Core.Tests.Fakes;

  /// <summary>
  /// Hand-rolled <see cref="HttpMessageHandler"/> test fake. Use over Moq's
  /// <c>Protected()</c> because (a) compile-time type safety, (b) clearer test
  /// assertions, (c) supports both pre-built response and per-request lambda response.
  /// </summary>
  internal sealed class TestHttpMessageHandler : HttpMessageHandler
  {
      private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

      /// <summary>Captured requests in arrival order. Tests assert against this.</summary>
      public List<HttpRequestMessage> Requests { get; } = new();

      /// <summary>Construct with a per-request responder.</summary>
      public TestHttpMessageHandler(
          Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
      {
          ArgumentNullException.ThrowIfNull(responder);
          _responder = responder;
      }

      /// <summary>Construct returning a fixed response.</summary>
      public TestHttpMessageHandler(HttpResponseMessage fixedResponse)
      {
          ArgumentNullException.ThrowIfNull(fixedResponse);
          _responder = (_, _) => Task.FromResult(fixedResponse);
      }

      /// <summary>Construct returning a fixed status + body.</summary>
      public static TestHttpMessageHandler WithBody(System.Net.HttpStatusCode status, string body, string contentType = "text/xml") =>
          new(new HttpResponseMessage(status)
          {
              Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
          });

      protected override async Task<HttpResponseMessage> SendAsync(
          HttpRequestMessage request, CancellationToken cancellationToken)
      {
          Requests.Add(request);
          return await _responder(request, cancellationToken).ConfigureAwait(false);
      }
  }
  ```
- [x] **8.2** **Helper for tests that need a stream that never completes** (for AC-3.5 HangAfter200Ok simulation):
  ```csharp
  // tests/ohSpy.Core.Tests/Fakes/HangingStream.cs
  namespace ohSpy.Core.Tests.Fakes;

  /// <summary>
  /// A <see cref="Stream"/> whose <c>ReadAsync</c> blocks indefinitely (until cancelled).
  /// Use to simulate the "HTTP headers arrived, body hangs" failure mode that
  /// motivated AC-3.5 (the prior tool's actual defect).
  /// </summary>
  internal sealed class HangingStream : Stream
  {
      public override bool CanRead => true;
      public override bool CanSeek => false;
      public override bool CanWrite => false;
      public override long Length => throw new NotSupportedException();
      public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
      public override void Flush() { }
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
      public override void SetLength(long value) => throw new NotSupportedException();
      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
      {
          await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
          return 0; // unreachable; ct will cancel
      }

      public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
          ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
  }
  ```
- [x] **8.3** **NoOpDiagnosticEmitter-equivalent for tests** — the test project should use the production `NoOpDiagnosticEmitter` from Core (it's `internal`, so add `InternalsVisibleTo` to `ohSpy.Core.csproj` for `ohSpy.Core.Tests`, OR have tests use a captures-mock implementing `IDiagnosticEmitter`). **Preferred:** use a captures-mock so tests can assert diagnostic emission (AC-5 requires the `Warning` diagnostic to be emitted):
  ```csharp
  // tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs
  namespace ohSpy.Core.Tests.Fakes;

  using ohSpy.Core.Diagnostics;

  /// <summary>
  /// Captures every emitter call into <see cref="Entries"/> so tests can assert the
  /// HTTP-error diagnostic stream. Substitutes for <c>NoOpDiagnosticEmitter</c> in
  /// tests that need to verify AC-5's "Warning diagnostic emitted on timeout" clause.
  /// </summary>
  internal sealed class CapturingDiagnosticEmitter : IDiagnosticEmitter
  {
      public record Entry(string Severity, string Category, string Message, DiagnosticContext Context);
      public List<Entry> Entries { get; } = new();
      public void Verbose(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Verbose", c, m, ctx));
      public void Information(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Information", c, m, ctx));
      public void Warning(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Warning", c, m, ctx));
      public void Error(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Error", c, m, ctx));
  }
  ```

### Task 9 — Unit tests (AC: all)

- [x] **9.1** Create folder `tests/ohSpy.Core.Tests/Http/` and author `UpnpHttpClientTests.cs`. Use xUnit + FluentAssertions. Trait every AC-mapped test with `[Trait("ac", "AC-N.M")]` (Amendment A2 pattern).

- [x] **9.2** **Test suite for `FetchDeviceDescriptionAsync` / `FetchScpdAsync` (the GET path):**
  1. **AC-3.1: per-op timeout fires** `[Trait("ac", "AC-3.1")]`. Handler delays 200 s via `await Task.Delay(200_000, ct);`. Override `ScpdFetch = TimeSpan.FromMilliseconds(200)`. Assert `UpnpTimeoutException` thrown; `ex.Budget == 200ms`; `ex.Elapsed` within 200ms ± 100ms.
  2. **AC-5: timeout emits Warning diagnostic** `[Trait("ac", "AC-5")]`. Use `CapturingDiagnosticEmitter`. After the timeout from test 1, assert `emitter.Entries.Single(e => e.Category == DiagCategories.HttpTimeout)` exists with non-null `Url`, `Elapsed`, `Budget`.
  3. **AC-3.6 / AC-6: caller cancellation propagates as OperationCanceledException** `[Trait("ac", "AC-3.6")]`. Caller passes a CTS, cancels it before the handler returns. Assert `OperationCanceledException` thrown (NOT `UpnpTimeoutException`). Assert NO Warning diagnostic in the captured emitter.
  4. **AC-3.4 / AC-8: oversize body via Content-Length** `[Trait("ac", "AC-3.4")]`. Handler returns 200 OK + ContentLength=5_000_000 in headers. Override `MaxDescriptionBytes = 1_000_000`. Assert `UpnpProtocolException` thrown. Disposal is handled by the impl's `resp.Dispose()` + the outer `using var resp` (double-dispose is safe per `HttpResponseMessage` contract); no need for an explicit disposal-evidence assertion. Assert diagnostic `HttpOversizeBody` was NOT emitted — the Content-Length path throws BEFORE the streaming read, so it bypasses `ReadWithSizeCapAsync`'s `Warning` call. (Test 5 below covers the streaming path that DOES emit.)
  5. **AC-3.4 / AC-8: oversize body via chunked transfer (no Content-Length)** `[Trait("ac", "AC-3.4")]`. Handler returns 200 OK + `StreamContent` over a `MemoryStream` of 2 MB. Override `MaxDescriptionBytes = 1_000_000`. Assert `UpnpProtocolException` thrown.
  6. **AC-3.5: hang-after-200-OK** `[Trait("ac", "AC-3.5")]`. Handler returns 200 OK + `StreamContent` over a `HangingStream`. Override `ScpdFetch = TimeSpan.FromMilliseconds(200)`. Assert `UpnpTimeoutException` thrown within 200ms ± 100ms. **This is the prior tool's actual defect — the canonical regression test.**
  7. **AC-7: transport error → UpnpTransportException** `[Trait("ac", "AC-3")]`. Handler throws `new HttpRequestException("conn refused")`. Assert `UpnpTransportException` thrown; `ex.Url`, `ex.StatusCode == null`. Diagnostic `HttpTransport` emitted.
  8. **Happy path** `[Trait("ac", "AC-3")]`. Handler returns 200 OK + small body. Assert bytes returned match the body; no exception, no diagnostic.
  9. **AC-3.1 / AC-11.2: HttpClient.Timeout is infinite** `[Trait("ac", "AC-11.2")]`. Build `UpnpHttpClient` via the test-only ctor with a `TestHttpMessageHandler`-backed `HttpClient`. Reflect on the underlying `_http.Timeout` field via `BindingFlags.Instance | BindingFlags.NonPublic` (or expose an internal `Timeout` property guarded by `InternalsVisibleTo`). Assert `_http.Timeout == Timeout.InfiniteTimeSpan`. (One-liner; deterministic; preferred over behavioural assertion which would be timing-sensitive.)

- [x] **9.3** **Test suite for `InvokeActionAsync` (the POST path):**
  1. **AC-3.3 / AC-9: SOAP 500 + fault → UpnpFaultException** `[Trait("ac", "AC-3.3")]`. Handler returns 500 with the canonical fault envelope:
     ```xml
     <?xml version="1.0"?>
     <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
       <s:Body>
         <s:Fault>
           <faultcode>s:Client</faultcode>
           <faultstring>UPnPError</faultstring>
           <detail>
             <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
               <errorCode>701</errorCode>
               <errorDescription>Invalid Action</errorDescription>
             </UPnPError>
           </detail>
         </s:Fault>
       </s:Body>
     </s:Envelope>
     ```
     Assert `UpnpFaultException` thrown; `ex.ActionName == "Browse"` (or whatever was in the request); `ex.ErrorCode == 701`; `ex.ErrorDescription == "Invalid Action"`.
  2. **Malformed 500 → UpnpTransportException** `[Trait("ac", "AC-3.3")]`. Handler returns 500 with arbitrary HTML (no `<UPnPError>`). Assert `UpnpTransportException` (NOT `UpnpFaultException`).
  3. **Happy path** `[Trait("ac", "AC-3")]`. Handler returns 200 + canned response envelope. Assert `SoapResponse.ResponseXml == body`; `StatusCode == OK`. Assert request's `SOAPAction` header was set with the quoted serviceType#actionName form.
  4. **Timeout, caller-cancellation, transport, oversize** — same shape as test suite 9.2 but routed through `InvokeActionAsync`.

- [x] **9.4** **Test suite for `SubscribeAsync` / `RenewSubscriptionAsync` / `UnsubscribeAsync`:**
  1. **AC-3.2: SUBSCRIBE custom HTTP method** `[Trait("ac", "AC-3.2")]`. Handler returns 200 with `SID: uuid:abc` + `TIMEOUT: Second-1800` headers. Call `SubscribeAsync(...)`. Assert `handler.Requests.Single().Method.Method == "SUBSCRIBE"`. Assert returned `SubscribeResponse.Sid == "uuid:abc"`; `Timeout == TimeSpan.FromSeconds(1800)`.
  2. **AC-3.2: UNSUBSCRIBE custom HTTP method** `[Trait("ac", "AC-3.2")]`. Handler returns 200 OK. Call `UnsubscribeAsync(...)`. Assert `handler.Requests.Single().Method.Method == "UNSUBSCRIBE"`. Assert no exception.
  3. **SUBSCRIBE response missing SID → UpnpProtocolException**. Handler returns 200 with no SID header. Assert `UpnpProtocolException`.
  4. **SUBSCRIBE response with malformed TIMEOUT → UpnpProtocolException**. Handler returns 200 with `TIMEOUT: NotASecondHeader`. Assert `UpnpProtocolException`.
  5. **RENEW**: same shape as SUBSCRIBE but with SID header instead of CALLBACK. Assert `handler.Requests.Single().Headers.Contains("SID")` and NOT `Contains("CALLBACK")`.
  6. **Timeout / caller-cancel / transport** — same as 9.2 tests but routed through SUBSCRIBE.

- [x] **9.5** **Test suite for `HttpTimeoutOptions` defaults (AC-2, AC-11.1):**
  1. **AC-11.1: defaults match D11** `[Trait("ac", "AC-11.1")]`. New `HttpTimeoutOptions()`, assert each field equals the exact spec value. Single test with 14 FluentAssertions assertions.
  2. **AC-11.3: services.Configure overrides work** `[Trait("ac", "AC-11.3")]`. Build a `ServiceCollection`, `Configure<HttpTimeoutOptions>(o => o.ScpdFetch = TimeSpan.FromMilliseconds(50))`, build provider, resolve `IOptions<HttpTimeoutOptions>`, assert `.Value.ScpdFetch == 50ms`.

- [x] **9.6** **Test suite for `UpnpExceptions` hierarchy (AC-1, A5):**
  1. **`UpnpException` is abstract**. Reflect on `typeof(UpnpException).IsAbstract == true`.
  2. **Four sealed derivatives**. `IsSealed == true` for each.
  3. **Carries structured context**. Construct each, assert properties echo constructor args.
  4. **Not Serializable**. Reflect on each type's attributes, assert no `[SerializableAttribute]`.

- [x] **9.7** **Total target: ~25–35 tests across these suites.** Story 1.2 landed 25 tests; Story 1.3's larger surface justifies ~30.

### Task 10 — Verification + smoke (AC: all)

- [x] **10.1** Run `dotnet build` from the repo root. Must succeed with ZERO warnings.
- [x] **10.2** Run `dotnet test`. All Story 1.3 tests must pass alongside Story 1.2's 25 tests. Final summary: `Passed: ~55, Failed: 0`.
- [x] **10.3** Run `dotnet test --filter "category=chaos"`. Still matches 0 tests (chaos lands in Story 1.6). Exit code 0.
- [x] **10.4** Manual smoke: run the App (`dotnet run --project src/ohSpy.App --launch-profile "ohSpy.App (Unpackaged)"` per Story 1.2's launch-profile gotcha). The empty WinUI window must still appear. If the DI graph is broken (e.g. `IUpnpHttpClient` resolution fails), the App will crash at `OnLaunched` time — check the exception, fix the registration.
- [x] **10.5** Make a trivial commit. Pre-commit hook still passes (chaos filter matches 0).

## Dev Notes

### Architectural pillars this story implements

| Architecture decision | What this story delivers | AC tag |
|---|---|---|
| **Decision 3** (`IUpnpHttpClient` facade + linked-CTS pattern) | Interface + production impl + token-threaded body read; closes the prior tool's "hung devices freeze app" defect | AC-3, AC-4, AC-5, AC-6, AC-7, AC-8, AC-9, AC-10 |
| **Decision 11** (`HttpTimeoutOptions` defaults) | Options class + defaults table + DI Configure registration + per-method size caps | AC-2, AC-4 |
| **Amendment A5** (`UpnpException` hierarchy) | Abstract base + 4 sealed derivatives with structured context | AC-1 |
| **Decision 8** (minimal surface) | `IDiagnosticEmitter` interface + `DiagnosticContext` + `DiagCategories.Http*` constants + `NoOpDiagnosticEmitter` (full impl in Story 1.5) | AC-5 |
| **Pattern 6** (async discipline) | `ConfigureAwait(false)` on every Core await; `Async` suffix; `CancellationToken ct` last param | (referenced) |
| **Pattern 7** (DI composition root) | `services.Configure<HttpTimeoutOptions>(...)` + `services.AddSingleton<IUpnpHttpClient, UpnpHttpClient>()` + `services.AddSingleton<IDiagnosticEmitter, NoOpDiagnosticEmitter>()` | AC-2, AC-5 |
| **Pattern 11** (mandatory DiagnosticContext fields per category) | `Http.Timeout` → Url/Elapsed/Budget; `Http.Transport` → Url/StatusCode; `Http.OversizeBody` → Url | AC-5 |

### Architecture amendments to expect

Story 1.1's dev-story surfaced 3 amendments (A6/A7/A8) which were applied to architecture.md before Story 1.2. Story 1.3 surfaces TWO more amendment candidates the dev agent should flag in the Dev Agent Record's "Architecture amendments uncovered" section (same pattern):

1. **A9 candidate — `UpnpTransportException` ctor smell.** Architecture A5 has `: base(message, inner ?? new InvalidOperationException(message))` which synthesises a fake inner when none is supplied. Change to `: base(message, inner)` (the `Exception(string, Exception?)` base ctor accepts null). Story 1.3 ships the architecture's verbatim form (Task 2.2) to preserve the spec match; A9 is the cleanup.
2. **A10 candidate — `FetchDeviceDescriptionAsync` return type symmetry.** Architecture D3 declares `Task<DeviceDescription>` but D5 revision changed `FetchScpdAsync` to `Task<byte[]>` (parsing → Story 1.4). Symmetry says both Fetch methods should return raw bytes; consumers compose the parser. Story 1.3 implements `Task<byte[]>` for both (Task 5.1); A10 records the architecture-text fix.

These are NOT defects to fix in code (Story 1.3's `IUpnpHttpClient` already implements the right thing). They're notes for the dev agent to add to architecture.md so Stories 1.4 / 2.3 inherit the corrected guidance.

### What this story explicitly does NOT do

- **Does NOT implement the full diagnostic pipeline** (`DiagnosticEntry`, `DiagSeverity`, `DiagnosticEmitter` real impl, `IDiagnosticRingSink`, `IDiagnosticFileSink`, ring/file sinks) — that's Story 1.5. Story 1.3 ships only the surface `UpnpHttpClient` consumes.
- **Does NOT implement SOAP envelope construction or full fault parsing** — that's Story 3.1. Story 1.3's `InvokeActionAsync` takes a pre-built `SoapRequest.EnvelopeXml`. The inline `TryParseUPnPError` in Task 6.1 extracts just errorCode + errorDescription for AC-9 (`UpnpFaultException`); Story 3.1 will introduce a richer SOAP fault parser if needed.
- **Does NOT implement device description / SCPD XML parsing** — that's Story 1.4. Story 1.3 returns raw bytes; consumers parse.
- **Does NOT implement the GENA event-callback host** — that's Story 4.1. Story 1.3 only handles the outbound HTTP for SUBSCRIBE/RENEW/UNSUBSCRIBE; the inbound NOTIFY listener is separate. Note that `HttpTimeoutOptions.CallbackHeaders` and `CallbackBody` are introduced here for Story 4.1's later consumption.
- **Does NOT add chaos tests.** That's Story 1.6. Story 1.6 will use `FakeUpnpDevice` (Kestrel-in-process) against `IUpnpHttpClient` to repeat AC-3.5 against `HangAfter200Ok`, this time as a `[Trait("category", "chaos")]` test that the pre-commit hook gates on. Story 1.3's `HangingStream`-based test (Task 9.2 test 6) demonstrates the discipline against a unit-test fake; Story 1.6 hardens it against a real socket.
- **Does NOT pre-create downstream consumer-side code** (`EagerDescriptionDispatcher`, SOAP invocation popup, GENA subscription popup, etc.) — those are Epic 2/3/4 stories that depend on `IUpnpHttpClient`.

### Cross-story dependencies (forward-looking)

| Story | Why it depends on 1.3 |
|---|---|
| 1.4 | XML parsers consume the raw bytes returned by `FetchDeviceDescriptionAsync` / `FetchScpdAsync`. |
| 1.5 | Replaces the `NoOpDiagnosticEmitter` registration with the real `DiagnosticEmitter` + ring/file sinks. Story 1.3's `Warning` calls start landing in the diagnostics viewer. |
| 1.6 | Chaos test fixture uses `FakeUpnpDevice` against `IUpnpHttpClient` for AC-3.5 regression coverage (`HangAfter200Ok`). |
| 2.3 | `EagerDescriptionDispatcher` calls `FetchDeviceDescriptionAsync` with bounded parallelism (semaphore = 8 per FR-043 + NFR-P6). |
| 2.6 | Service-node expansion calls `FetchScpdAsync` lazily on demand. |
| 3.1 | SOAP envelope builder produces `SoapRequest.EnvelopeXml`; invocation popup calls `InvokeActionAsync` and handles `UpnpFaultException` / `UpnpTransportException` / `UpnpTimeoutException`. |
| 4.2 | Subscription client orchestrates `SubscribeAsync` / `RenewSubscriptionAsync` / `UnsubscribeAsync` with timer-based renewal. |
| 5.3 | Rescan re-runs description fetches. |

**The per-request timeout discipline is load-bearing for NFR-P2 / NFR-R2.** Any story that consumes `IUpnpHttpClient` inherits the discipline automatically — that's the structural point.

### Story 1.2 learnings worth carrying forward

[Source: `1-2-ui-dispatcher-contract-collection-primitives.md` §Completion Notes + Code Review, commits `b15e0b7` / `b9ea15d`]

- **All Story 1.2 tests passed (25/25); the analyzer-coverage smoke test from Story 1.1 still holds.** Any `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` in new code will fail the build.
- **VSTHRD100 (async void) is exempted in `tests/**`.** Test fixtures may use `async void` patterns if needed.
- **`Microsoft.Extensions.DependencyInjection 10.0.0`** AND **`Microsoft.Extensions.Options 10.0.0`** are already pinned in `Directory.Packages.props` (part of the original A3 baseline — NOT added by any amendment). Story 1.3 adds explicit `<PackageReference Include="Microsoft.Extensions.Options" />` to `src/ohSpy.Core/ohSpy.Core.csproj` so Core can use `IOptions<HttpTimeoutOptions>` (Task 7.4). **Do NOT add a duplicate `<PackageVersion>` entry to `Directory.Packages.props`.**
- **CA1707 + CA1806 are suppressed in `tests/**`** (Story 1.2 added these to `.editorconfig`). New tests can use the xUnit `Method_Scenario_ExpectedResult` naming idiom and FluentAssertions' constructor-throws pattern freely.
- **Namespace convention:** `ohSpy.Core.*` and `ohSpy.App.*` (dots, not underscores). New folders inherit this.
- **The launchSettings.json profile gotcha** remains — for `dotnet run` use `--launch-profile "ohSpy.App (Unpackaged)"`. F5 in VS picks it automatically. Story 1.3's smoke test (Task 10.4) needs the flag.
- **Architecture amendments A6/A7/A8 + A9/A10-candidates inherited.** Story 1.3 ships clean code from day one.

### Project Structure Notes

**Minimum directories this story must create:**

```
src/ohSpy.Core/
├── Diagnostics/                          ← NEW in 1.3
│   ├── DiagnosticContext.cs              ← Task 1.2
│   ├── DiagCategories.cs                 ← Task 1.3
│   ├── IDiagnosticEmitter.cs             ← Task 1.4
│   └── NoOpDiagnosticEmitter.cs          ← Task 1.5
└── Http/                                 ← NEW in 1.3
    ├── UpnpExceptions.cs                 ← Task 2
    ├── HttpTimeoutOptions.cs             ← Task 3
    ├── SoapRequest.cs                    ← Task 4.1
    ├── SoapResponse.cs                   ← Task 4.2
    ├── SubscribeResponse.cs              ← Task 4.3
    ├── IUpnpHttpClient.cs                ← Task 5
    └── UpnpHttpClient.cs                 ← Task 6

tests/ohSpy.Core.Tests/
├── Fakes/                                (already exists from 1.2)
│   ├── TestHttpMessageHandler.cs         ← Task 8.1
│   ├── HangingStream.cs                  ← Task 8.2
│   └── CapturingDiagnosticEmitter.cs     ← Task 8.3
└── Http/                                 ← NEW in 1.3
    └── UpnpHttpClientTests.cs            ← Task 9
```

**Files modified:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — append HttpTimeoutOptions Configure + IUpnpHttpClient registration + NoOpDiagnosticEmitter registration (Task 7).
- Possibly `src/ohSpy.Core/ohSpy.Core.csproj` and `Directory.Packages.props` — only if `Microsoft.Extensions.Options` is not transitively available (Task 7.4).

### Anti-patterns to avoid

- **Don't use `HttpClient.Timeout` to enforce per-op budgets.** Set it to `Timeout.InfiniteTimeSpan`; the linked CTS is the SOLE source. Mixing the two creates two timeouts racing each other and surfaces `TaskCanceledException` (the wrong type) instead of `UpnpTimeoutException`.
- **Don't `await using var resp = …; await resp.Content.ReadAsStringAsync(); // no token`.** The body-read must be token-threaded — that's AC-3.5, the prior tool's actual defect. Untokened body reads can hang indefinitely after `ResponseHeadersRead` succeeds.
- **Don't omit `HttpCompletionOption.ResponseHeadersRead`.** Default is `ResponseContentRead`, which buffers the entire body before returning. The linked CTS still works on the read, but you lose the ability to stream-bound the size cap (you've already buffered before you check).
- **Don't wrap `OperationCanceledException` when the caller cancelled.** AC-3.6 / AC-6 demands the original `OperationCanceledException` propagate when `external.IsCancellationRequested == true`. Wrap only when the per-op CTS fired. The `when (external.IsCancellationRequested)` filter clause is the canonical idiom.
- **Don't "fix" the cancellation race.** When external and timeout-CTS fire essentially simultaneously, the `when (external.IsCancellationRequested)` filter checks the external token's state AT CATCH TIME — if it shows requested, we classify as caller-cancel (silent re-throw, no diagnostic). That's the correct semantic: if the caller cancelled at all, treat it as caller-driven. A naive "more precise" attempt (e.g. checking which CTS fired first via timestamps) introduces races without value. Leave the filter as written.
- **Don't emit a `Warning` diagnostic on caller-cancelled `OperationCanceledException`.** It's expected behaviour, not an error. The architecture explicitly says no diagnostic on caller-initiated cancel.
- **Don't use `Moq.Protected()` for `HttpMessageHandler` tests.** Hand-rolled `TestHttpMessageHandler` is cleaner, compile-time-typed, and reusable. ~40 lines.
- **Don't reference `Microsoft.UI.*` from anywhere in Core (`UpnpHttpClient`, `UpnpExceptions`, anything in `Diagnostics/` or `Http/`).** Pattern 2 boundary. Build-time enforcement (`ohSpy.Core.csproj` doesn't reference `Microsoft.WindowsAppSDK`) catches gross violations; NetArchTest in Story 1.6 will catch transitive leakage.
- **Don't add `ConfigureAwait(false)` in the App project.** Pattern 6: Core only. The App is the UI consumer; context capture is desired.
- **Don't import `System.Net.Http` and `System.Threading.Tasks` explicitly** — both are in `ImplicitUsings` (Story 1.1's `Directory.Build.props`). Adding explicit usings is harmless but noisy.
- **Don't add a `SerializableAttribute` to any of the exception types.** A5 explicitly: deprecated in modern .NET, no cross-AppDomain use case.
- **Don't conflate `SoapRequest` with the SOAP envelope builder Story 3.1 will deliver.** Story 1.3's `SoapRequest` is a thin data carrier (controlURL + serviceType + actionName + already-built envelope XML). Story 3.1 will introduce a builder that takes ScpdAction + input values and produces this record.
- **Don't add a real `DiagnosticEmitter` implementation.** Story 1.5 ships it. Story 1.3's NoOp emitter is correct for now — exactly because Story 1.5 will replace the DI registration without changing any other code.

### Testing standards summary

- xUnit + FluentAssertions are already pinned (Story 1.1); no new packages.
- Every test with an architecture-level AC ID carries `[Trait("ac", "AC-N.M")]` (Amendment A2).
- **Use `CapturingDiagnosticEmitter` for diagnostic assertions** (not `Mock<IDiagnosticEmitter>` — direct equality assertions on captured entries read cleaner).
- **The `HangingStream` fake is the AC-3.5 acid test** at unit level. Story 1.6 hardens it against a real socket via `FakeUpnpDevice`.
- **Use the test-only ctor on `UpnpHttpClient`** that accepts a pre-built `HttpClient`. The production ctor constructs its own handler (over real sockets) — not what you want in unit tests.
- **For SOAP-fault test (AC-9):** the canonical envelope from Task 9.3 test 1 is enough. Don't over-cover edge cases (wrong root, wrong namespace, etc.) — Story 3.1's parser will handle the full spectrum.
- **For SUBSCRIBE/UNSUBSCRIBE custom-method tests (AC-10):** the HttpMessageHandler captures the request method as a string via `request.Method.Method`. Assert directly: `request.Method.Method.Should().Be("SUBSCRIBE")`. The .NET BCL doesn't have `HttpMethod.Subscribe`; constructing via `new HttpMethod("SUBSCRIBE")` is the canonical idiom.
- **No mocking of the HttpClient itself.** Mock at the `HttpMessageHandler` layer (the seam designed for testing).

### References

> Authoritative paths (for grep / cross-reference):
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (~2800 lines, post amendments A6/A7/A8)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (lines 521–580 for Story 1.3, 350–354 + 408–410 for Epic 1)
> - Story 1.1 completion: `_bmad-output/implementation-artifacts/1-1-project-scaffold-build-test-installer-pipeline.md`
> - Story 1.2 completion: `_bmad-output/implementation-artifacts/1-2-ui-dispatcher-contract-collection-primitives.md`

- [Source: epics.md#Story-1.3] — verbatim ACs (lines 521–580).
- [Source: epics.md#Epic-1] — epic-level FR/NFR coverage map (lines 350–354, 408–410).
- [Source: architecture.md#Decision-3] — `IUpnpHttpClient` facade + linked-CTS pattern + exception mapping discipline (lines ~260–391).
- [Source: architecture.md#Decision-11] — `HttpTimeoutOptions` defaults + DI Configure pattern (lines ~1387–1483).
- [Source: architecture.md#Amendment-A5] — `UpnpException` hierarchy (lines ~2520–2590).
- [Source: architecture.md#Decision-8] — full diagnostic pipeline (Story 1.3 implements only the surface; Story 1.5 implements the sinks) (lines ~875–1073).
- [Source: architecture.md#Pattern-6] — async discipline (lines ~1800–1809).
- [Source: architecture.md#Pattern-7] — DI composition root + lifetime (lines ~1811–1837).
- [Source: architecture.md#Pattern-11] — mandatory DiagnosticContext fields per category (lines ~1908–1920).
- [Source: architecture.md#Pattern-2] — Core ↔ App boundary (lines ~1708–1723).
- [Source: project_ohspy memory] — `IUpnpHttpClient` facade was the architecture's primary structural answer to the prior tool's "slow devices hang the app" complaint.

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m] (Claude Opus 4.7, 1M-context build) via bmad-dev-story workflow, 2026-06-02.

### Debug Log References

- 2026-06-02 — initial Core-only build (Diagnostics + Http types, before suppressions) surfaced two analyzer errors promoted to errors by `TreatWarningsAsErrors=true`:
  - `CA1716` on `IDiagnosticEmitter.Error` (member name conflicts with VB.NET keyword). Suppressed inline at the member level with a justification comment — the diagnostic severity vocabulary (Verbose/Information/Warning/Error) mirrors `Microsoft.Extensions.Logging.LogLevel` and is the idiomatic .NET shape; ohSpy is C#-only so the trade-off is one-sided.
  - `CA1806` on `int.TryParse(v, out errorCode)` inside `TryParseUPnPError` (return value not used). Wrapped with `_ =` discard and a comment — the success gate is the `errorCode != 0` check at the bottom of the parser; a parse-failed `errorCode == 0` is correctly treated as "not a UPnPError".
- 2026-06-02 — full solution build then surfaced `CS0122` in `src/ohSpy.App/Composition/ServiceRegistration.cs` — `UpnpHttpClient` and `NoOpDiagnosticEmitter` are `internal sealed` (architecture pattern) but `ohSpy.App` is a separate assembly. Resolved by extending `InternalsVisibleTo` in `src/ohSpy.Core/ohSpy.Core.csproj` to grant the App project the same visibility the test project gets (Task 6.7 only mentioned the test project — Task 7.2's `services.AddSingleton<IUpnpHttpClient, UpnpHttpClient>()` triggers the App-side need).
- 2026-06-02 — `dotnet test --no-build` first run: 65 passed / 1 failed (`InvokeAction_Soap500WithFault_ThrowsUpnpFaultException` — `ErrorDescription` came back empty). Root-caused to the `TryParseUPnPError` reader-advance bug: combining `while (reader.Read())` with `reader.ReadElementContentAsString()` skips a node — `ReadElementContentAsString` already advances past EndElement, then the `Read()` in the while header advances again, hopping over `<errorDescription>`. Fixed by replacing the loop with a manual `MoveToContent()` + `while (!reader.EOF)` driver that uses `continue` after `ReadElementContentAsString` (skipping the redundant `Read()`) and explicit `reader.Read()` otherwise.
- 2026-06-02 — final state: 0 build warnings/errors, 66/66 tests passing, app smoke OK.

### Completion Notes List

**Build, test, smoke evidence**

- `dotnet build` (full solution: ohSpy.Core + ohSpy.Core.Tests + ohSpy.App) — `0 Warning(s) 0 Error(s)`. `TreatWarningsAsErrors=true` is live, so this is a strict pass.
- `dotnet test tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` — `Passed!  - Failed: 0, Passed: 66, Skipped: 0, Total: 66, Duration: 1 s` (Story 1.2 baseline was 25; Story 1.3 added 41 tests — over the projected ~30. Theory iterations and a couple of extra subscribe-state edge cases padded the count).
- `dotnet test --filter "category=chaos"` — `No test matches the given testcase filter` (expected; chaos lands in Story 1.6).
- Manual smoke: `dotnet run --project src/ohSpy.App --launch-profile "ohSpy.App (Unpackaged)" --no-build` launched cleanly, ran for 8 s while held open, exited on TERM with no exception output. The new DI registrations (`IUpnpHttpClient`, `IDiagnosticEmitter`, `Configure<HttpTimeoutOptions>`) all resolved during `OnLaunched`; no `InvalidOperationException: Unable to resolve service` traceback. Empty WinUI shell still appeared (no XAML regressions from Story 1.2).

**`Microsoft.Extensions.Options` PackageReference (Task 7.4 contingency check)**

`<PackageReference Include="Microsoft.Extensions.Options" />` was added to `src/ohSpy.Core/ohSpy.Core.csproj` per the story spec. The version was already pinned in `Directory.Packages.props` (line 10, `10.0.0`) from the original A3 baseline — no duplicate `PackageVersion` entry needed. Central package management did the resolution without complaint. The same package was also added to `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` along with `Microsoft.Extensions.DependencyInjection` so the AC-11.3 tests can call `Configure<HttpTimeoutOptions>` + `BuildServiceProvider()` against a real `ServiceCollection`.

**Architecture amendments uncovered during implementation**

Surfacing the two amendment candidates that Dev Notes told me to expect, plus a third minor one that came up during analyzer triage:

- **A9 candidate — `UpnpTransportException` ctor synthetic-inner smell (architecture.md §Amendment-A5 lines ~2520-2590).** As flagged in story Task 2.3, the verbatim A5 form is `: base(message, inner ?? new InvalidOperationException(message))`. That fabricates a fake inner exception when none is supplied, which (a) misleads any debugger inspecting `InnerException`, (b) buries the real (null) state behind a constructed object that didn't originate at the failure site, and (c) costs an allocation per construction even when no inner is meaningful. The `Exception(string, Exception?)` base ctor accepts null cleanly — change to `: base(message, inner)`. Story 1.3 ships the verbatim form (preserves the architecture-doc match) so this is a doc/spec amendment for a later refactor, not a code defect to fix now.
- **A10 candidate — `FetchDeviceDescriptionAsync` return-type symmetry (architecture.md §Decision-3 lines ~260-391).** D3's original text declares `Task<DeviceDescription>` for the description fetch, but D5's later revision changed `FetchScpdAsync` to `Task<byte[]>` (parsing → Story 1.4). Symmetry says both Fetch methods should return raw bytes; consumers compose the parser separately. Story 1.3's `IUpnpHttpClient.FetchDeviceDescriptionAsync` already returns `Task<byte[]>`. The architecture text should be updated so Stories 1.4 / 2.3 inherit the corrected guidance (consumer-side: build a `DeviceDescription` from the bytes via `IDeviceDescriptionParser` rather than expecting the facade to return a typed structure).
- **A11 candidate (minor, optional) — analyzer-exemption documentation in `.editorconfig`.** Story 1.3 extended `[tests/**/*.cs]` exemptions with `VSTHRD003` (await-pending-task pattern fundamental to cancellation tests) and `CA2263` (the runtime-`Type` overload of `Be(...)` is unavoidable in `[Theory]+[InlineData(typeof(T))]` patterns). Architecture amendment A8 already documents the test-side exemption pattern; if the team standardises this list further, A11 would lift the convention from `.editorconfig` comments into the architecture's testing-conventions section.

**Deviations from the spec — with rationale**

- **`InternalsVisibleTo` for `ohSpy.App`, not just `ohSpy.Core.Tests`.** Task 6.7 only specified the test-project grant; the App project's need surfaced at build-time because `UpnpHttpClient` and `NoOpDiagnosticEmitter` are `internal sealed` (architecture-correct) and `services.AddSingleton<IUpnpHttpClient, UpnpHttpClient>()` in `ServiceRegistration.cs` lives in the App assembly. Adding the App grant alongside the test grant was the minimum-friction fix that preserves the `internal sealed` posture (architecture Pattern 2 — Core types not part of the public surface). Recommend Task 6.7 mention this in future stories that add Core-side internal types consumed by App composition.
- **Test-only `internal TimeSpan HttpClientTimeoutForTests => _http.Timeout` accessor on `UpnpHttpClient`.** Task 9.2 test 9 offered "reflection OR expose an internal property guarded by `InternalsVisibleTo`". I picked the accessor — reflection is slower, requires `BindingFlags` plumbing, and gives weaker compile-time guarantees. Three lines on the SUT in exchange for a one-line assertion in the test.
- **Helper test class (`UpnpHttpClientTests.NoOpEmitter`).** Story Task 9.2 test 9 needs an emitter, but the test doesn't capture diagnostics, so I added a tiny private no-op rather than spinning up `CapturingDiagnosticEmitter` for nothing. `NoOpDiagnosticEmitter` from Core is `internal` — `InternalsVisibleTo` would make it visible, but a private nested class keeps the dependency direction explicit (the test owns its no-op; Core's `NoOpDiagnosticEmitter` is purely a DI-time placeholder for App registration).
- **AC-11.3 test split into two: one verifying `Options.Create` round-trips overrides, one verifying `ServiceCollection.Configure` + `BuildServiceProvider` round-trips defaults.** `HttpTimeoutOptions` uses `init` setters, which makes the typical `services.Configure<HttpTimeoutOptions>(o => o.ScpdFetch = ...)` lambda syntax illegal (init-only outside object initialiser). The `Configure` pattern still works for binding from configuration sources (Story 1.5 will use it for the logging-level lookup), and the registration is exercised end-to-end via the second test's `BuildServiceProvider` round-trip. If a future story needs to mutate options at registration time, switching `init` → `set` on the relevant property is the unblock — flag for that story's spec author.

### File List

**Created:**
- `src/ohSpy.Core/Diagnostics/DiagnosticContext.cs`
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs`
- `src/ohSpy.Core/Diagnostics/IDiagnosticEmitter.cs`
- `src/ohSpy.Core/Diagnostics/NoOpDiagnosticEmitter.cs`
- `src/ohSpy.Core/Http/UpnpExceptions.cs`
- `src/ohSpy.Core/Http/HttpTimeoutOptions.cs`
- `src/ohSpy.Core/Http/SoapRequest.cs`
- `src/ohSpy.Core/Http/SoapResponse.cs`
- `src/ohSpy.Core/Http/SubscribeResponse.cs`
- `src/ohSpy.Core/Http/IUpnpHttpClient.cs`
- `src/ohSpy.Core/Http/UpnpHttpClient.cs`
- `tests/ohSpy.Core.Tests/Fakes/TestHttpMessageHandler.cs`
- `tests/ohSpy.Core.Tests/Fakes/HangingStream.cs`
- `tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs`
- `tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs`

**Modified:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — appended `services.Configure<HttpTimeoutOptions>(_ => {})` + `services.AddSingleton<IUpnpHttpClient, UpnpHttpClient>()` + `services.AddSingleton<IDiagnosticEmitter, NoOpDiagnosticEmitter>()` registrations plus the two new usings (Task 7).
- `src/ohSpy.Core/ohSpy.Core.csproj` — added `<PackageReference Include="Microsoft.Extensions.Options" />` (Task 7.4) and `<InternalsVisibleTo Include="ohSpy.Core.Tests" />` + `<InternalsVisibleTo Include="ohSpy.App" />` (Task 6.7 + App-side need surfaced during build).
- `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` — added `<PackageReference Include="Microsoft.Extensions.DependencyInjection" />` and `<PackageReference Include="Microsoft.Extensions.Options" />` so the AC-11.3 round-trip tests can call `Configure<>` + `BuildServiceProvider()` (transitive references through Core were not enough to satisfy the compiler for the test source).
- `.editorconfig` — extended the `[tests/**/*.cs]` analyzer-exemption block with `VSTHRD003` (await-pending-task pattern intentional in cancellation tests) and `CA2263` (runtime-`Type` overload unavoidable in `[Theory] [InlineData(typeof(T))]` patterns), each with a justification comment. Pattern follows Story 1.2's CA1707/CA1806/VSTHRD100 exemption block.

**NOT modified:**
- `Directory.Packages.props` — `Microsoft.Extensions.Options 10.0.0` and `Microsoft.Extensions.DependencyInjection 10.0.0` are already pinned (lines 8 + 10, original A3 baseline). No new `PackageVersion` entries.

## Change Log

- **2026-06-02 — Story 1.3 implementation (claude-opus-4-7[1m] via bmad-dev-story).** Shipped `IUpnpHttpClient` facade (`UpnpHttpClient` + 4-derivative `UpnpException` hierarchy + `HttpTimeoutOptions` + thin Soap/Subscribe records) and the minimum `IDiagnosticEmitter` surface (`NoOpDiagnosticEmitter` placeholder ahead of Story 1.5). Closes the prior tool's "slow devices hang the app" defect via per-op linked CTS + `HttpCompletionOption.ResponseHeadersRead` + token-threaded body reads + per-method size caps (AC-3.5). All 11 ACs covered by 41 new tests. Build 0/0; test 66/0; chaos filter 0; App smoke OK. Surfaced A9/A10 architecture-amendment candidates (verbatim from Dev Notes) plus an optional A11 (analyzer-exemption documentation). Status: ready-for-dev → in-progress → review.
