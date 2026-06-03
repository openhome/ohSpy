---
baseline_commit: 8af34e8a734c45f8fb0a34e604b3e565b87aab5d
---

# Story 3.1: SOAP Envelope Builder, Fault Parser, and `InvokeActionAsync` Wire-Up

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an ohSpy developer,
I want SOAP envelope construction, UPnP fault parsing, and the body of `IUpnpHttpClient.InvokeActionAsync` reshaped onto structured argument records,
So that the invocation popup in Story 3.2 can build one `SoapRequest` from an `ScpdAction` + values, call one method, and trust that the request is well-formed, the success path returns structured output arguments, and the SOAP 500 / `<UPnPError>` fault path raises the correct typed exception.

---

## ⚠️ READ THIS FIRST — This is a RESHAPE, not greenfield

**Story 1.3 front-ran a large part of this story.** The epic text was written assuming a clean slate; it is not one. Before writing any code, internalise the current reality:

| Artifact | Epic/AC assumes | What 1.3 actually shipped | Your job |
|---|---|---|---|
| `SoapRequest` | record in `Models/` with `IReadOnlyList<SoapArgument> InputArguments` | record in **`Http/`** with `string EnvelopeXml` (pre-built) | **Reshape + relocate** |
| `SoapResponse` | record in `Models/` with `string ActionName` + `IReadOnlyList<SoapArgument> OutputArguments` | record in **`Http/`** with `HttpStatusCode StatusCode` + `string ResponseXml` | **Reshape + relocate** |
| `SoapArgument` | record `(string Name, string Value)` | **does not exist** | **Create** |
| `SoapEnvelopeBuilder` | `Soap/SoapEnvelopeBuilder.cs` | **does not exist** | **Create** |
| `SoapFaultParser` | `Soap/SoapFaultParser.cs` | **does not exist** — logic lives inline as `UpnpHttpClient.TryParseUPnPError` | **Create + delete the inline parser** |
| `InvokeActionAsync` | "wire up the full body" | **already fully wired** (POST, headers, size cap, 500/non-2xx handling, inline fault parse) | **Re-wire onto the new building blocks + fix diag categories** |

Do **not** treat the method as a stub. Do **not** duplicate functionality. The deliverable is a refactor that swaps the pre-built-string plumbing for structured records + dedicated SOAP classes, plus the new builder/parser and their tests.

`src/ohSpy.Core/Http/UpnpHttpClient.cs:143-254` is the code you are modifying. Read it in full before starting.

---

## Acceptance Criteria

> ACs below are the epic's, reconciled to current reality. Where the epic said "given the file" treat it as "given the reshaped file". File locations are pinned in Dev Notes (Models/ for records, Soap/ for builder+parser).

**AC-3.1.1 — Records (reshaped, relocated to `Models/`)**
1. `SoapRequest` is a `public sealed record` (namespace `ohSpy.Core.Models`) with `Uri ControlUrl`, `string ServiceType`, `string ActionName`, `IReadOnlyList<SoapArgument> InputArguments` (Pattern 9). The old `string EnvelopeXml` member is **removed**.
2. `SoapResponse` is a `public sealed record` (namespace `ohSpy.Core.Models`) with `string ActionName`, `IReadOnlyList<SoapArgument> OutputArguments` (Pattern 9). The old `HttpStatusCode StatusCode` + `string ResponseXml` members are **removed**.
3. `SoapArgument` is a `public sealed record` (namespace `ohSpy.Core.Models`) with `string Name`, `string Value` (free-form text per PRD §7 Non-Goal — no `<dataType>`-driven typed inputs in v1).

**AC-3.1.2 — `SoapEnvelopeBuilder.Build(SoapRequest req) : string`**
4. Output is a valid SOAP 1.1 envelope conforming to UDA 1.0 §3.2.1 — standard `s:Envelope` + `s:Body` structure with the SOAP encoding namespace.
5. The action element is `<u:ActionName xmlns:u="<serviceType>">`, with the prefix `u:` used consistently.
6. Each input argument renders as `<argName>value</argName>` inside the action element, **in `req.InputArguments` order**.
7. Input values are XML-escaped (`<`, `>`, `&`, `"`, `'` → entities) — verified by fuzzy tests with adversarial input strings.
8. The envelope is UTF-8 (the request later carries `Content-Type: text/xml; charset="utf-8"` and `SOAPACTION: "<serviceType>#<actionName>"`).
9. Argument-less actions produce an empty/self-closing action element (`<u:ActionName xmlns:u="..." />`) (FR-031).

**AC-3.1.3 — `SoapFaultParser.TryParse(byte[] body, out UpnpFault fault) : bool`**
10. On a SOAP 500 body containing `<s:Fault><detail><UPnPError><errorCode>402</errorCode><errorDescription>Invalid Args</errorDescription></UPnPError></detail></s:Fault>`, returns `true` with `fault.ErrorCode == 402`, `fault.ErrorDescription == "Invalid Args"` (FR-029 — UDA 1.0 §3.2.2).
11. Uses the **shared** `UpnpXmlReaderSettings.Create()` discipline (DtdProcessing.Prohibit, XmlResolver = null) — defence-in-depth XXE protection on fault responses. Do **not** hand-roll new `XmlReaderSettings`.
12. A SOAP 500 body **without** a parsable `<UPnPError>` (raw fault string, missing `<errorCode>`, malformed XML, XXE attempt) returns `false`; the caller treats it as a generic transport error.

**AC-3.1.4 — `InvokeActionAsync` happy path**
13. The request body is built via `SoapEnvelopeBuilder.Build(request)` (not a caller-supplied string).
14. The HTTP request carries `POST <ControlUrl>`, `Content-Type: text/xml; charset="utf-8"`, `SOAPACTION: "<serviceType>#<actionName>"`, and the envelope body.
15. Per-op timeout is `_opts.SoapInvoke` (10 s default — Decision 11); body-size cap is `_opts.MaxSoapResponseBytes` (1 MB — Decision 3). *(Both already in place — preserve.)*
16. On 2xx, the body is parsed into a `SoapResponse` carrying each `<argName>value</argName>` from the `<u:ActionNameResponse>` element, XML-unescaped on extraction.

**AC-3.1.5 — `InvokeActionAsync` fault & error paths (diagnostic categories change)**
17. HTTP 500 + **parsable** `<s:Fault>` → throw `UpnpFaultException(Url = ControlUrl, ActionName, ErrorCode, ErrorDescription)`; **and** emit a `Warning` `DiagCategories.SoapFault` with `Url`, `ActionName`, `StatusCode = 500`, `ErrorText = $"{ErrorCode}: {ErrorDescription}"` (DeviceUuid is absent at this layer — see Dev Notes). **This emit is new.**
18. HTTP 500 + **un-parsable** fault → throw `UpnpTransportException(Url, StatusCode = 500)`; emit `Warning` `DiagCategories.SoapInvoke` (was `HttpTransport`).
19. Non-2xx / non-500 status (404, 405, …) → throw `UpnpTransportException(Url, StatusCode)`; emit `Warning` `DiagCategories.SoapInvoke` (was `HttpTransport`).
20. Caller-token cancellation → `OperationCanceledException` propagates silently (no diagnostic). Per-op timeout → `UpnpTimeoutException` + `Warning` `DiagCategories.HttpTimeout` (**unchanged**). `HttpRequestException` → `UpnpTransportException` + `Warning` `DiagCategories.HttpTransport` (**unchanged**).

**AC-3.1.6 — Tests**
21. `SoapEnvelopeBuilder` is exercised against canned shapes (zero args; one string arg; multiple args with adversarial chars `< > & " '`) with golden-file (or pinned-string) assertions on the envelope output.
22. `SoapFaultParser` is exercised against fixtures: valid fault, missing `<errorCode>`, missing `<errorDescription>`, malformed XML, and an XXE-attempt — asserting the bool result and (on success) the parsed values.
23. `UpnpHttpClient.InvokeActionAsync` is exercised via `TestHttpMessageHandler` for: happy path (returns structured `SoapResponse` with output args), SOAP-fault path (`UpnpFaultException` w/ correct code + `SoapFault` diagnostic), 500-unparsable + non-2xx (`UpnpTransportException` + `SoapInvoke` diagnostic), timeout (`UpnpTimeoutException`), and caller-cancellation (`OperationCanceledException`, no diagnostic).
24. AC-3.3-tagged tests retain `[Trait("ac", "AC-3.3")]`; new SOAP-builder/parser tests carry an appropriate `[Trait("ac", "AC-3.1.x")]`.

---

## Tasks / Subtasks

- [x] **Task 1 — Reshape + relocate the records** (AC-3.1.1)
  - [x] Create `src/ohSpy.Core/Models/SoapArgument.cs` — `public sealed record SoapArgument(string Name, string Value);`
  - [x] Move + reshape `SoapRequest` → `src/ohSpy.Core/Models/SoapRequest.cs`, namespace `ohSpy.Core.Models`, members `(Uri ControlUrl, string ServiceType, string ActionName, IReadOnlyList<SoapArgument> InputArguments)`. Delete the old `Http/SoapRequest.cs`.
  - [x] Move + reshape `SoapResponse` → `src/ohSpy.Core/Models/SoapResponse.cs`, namespace `ohSpy.Core.Models`, members `(string ActionName, IReadOnlyList<SoapArgument> OutputArguments)`. Delete the old `Http/SoapResponse.cs` (and its `using System.Net;`).
  - [x] Add `using ohSpy.Core.Models;` to `IUpnpHttpClient.cs` and `UpnpHttpClient.cs` (the records' namespace changed; `InvokeActionAsync` signature still reads `Task<SoapResponse> InvokeActionAsync(SoapRequest request, …)`).
- [x] **Task 2 — `SoapEnvelopeBuilder`** (AC-3.1.2)
  - [x] Create `src/ohSpy.Core/Soap/SoapEnvelopeBuilder.cs`, namespace `ohSpy.Core.Soap`, `public static string Build(SoapRequest req)` (or a stateless instance class — engineering judgment; a `static` method is simplest and matches the call site).
  - [x] Use `XmlWriter` over a `StringWriter` (auto-escaping + correct framing). `OmitXmlDeclaration = true` (see Dev Notes — avoids the UTF-16 declaration trap). Write `s:Envelope` (+ `s:encodingStyle`), `s:Body`, then `u:<ActionName>` with `xmlns:u` = `req.ServiceType`, then one child element per input arg via `WriteElementString(arg.Name, arg.Value)`.
  - [x] Argument-less ⇒ no children ⇒ self-closing element (FR-031). Verify XmlWriter emits `<u:Foo xmlns:u="..." />`.
- [x] **Task 3 — `UpnpFault` + `SoapFaultParser`** (AC-3.1.3)
  - [x] Create `src/ohSpy.Core/Soap/UpnpFault.cs` — `public sealed record UpnpFault(int ErrorCode, string ErrorDescription);` (data carrier; distinct from the `UpnpFaultException` the client throws).
  - [x] Create `src/ohSpy.Core/Soap/SoapFaultParser.cs`, namespace `ohSpy.Core.Soap`, `public static bool TryParse(byte[] body, out UpnpFault fault)`. Reuse `UpnpXmlReaderSettings.Create()` (it's `internal` in `ohSpy.Core.Scpd` — same assembly, accessible). Read `errorCode` (gate: a parsed non-zero int) + `errorDescription` (default `""` if absent). Wrap the whole parse in try/catch → `false` on any `XmlException` (covers malformed + XXE-attempt). Set `fault = default!`/sentinel on the `false` path.
- [x] **Task 4 — SOAP response reader** (AC-3.1.4 #16)
  - [x] Create `src/ohSpy.Core/Soap/SoapResponseReader.cs` (namespace `ohSpy.Core.Soap`) `public static IReadOnlyList<SoapArgument> ReadOutputArguments(byte[] body)` — reuse `UpnpXmlReaderSettings.Create()`; navigate into `s:Body` → the single `*Response` element → collect each direct child element as `SoapArgument(localName, elementText)`, XML-unescaped (XmlReader unescapes automatically). Argument-less response ⇒ empty list. *(A private helper inside `UpnpHttpClient` is acceptable if you prefer, but a dedicated class is testable + symmetric with the builder/parser — recommended.)*
- [x] **Task 5 — Re-wire `InvokeActionAsync`** (AC-3.1.4, AC-3.1.5)
  - [x] Replace `request.EnvelopeXml` with `SoapEnvelopeBuilder.Build(request)` inside the `StringContent(...)`. Keep `Encoding.UTF8, "text/xml"` and the existing quoted `SOAPAction` header logic (preserve — already correct).
  - [x] Preserve the timeout/size-cap scaffolding (`SoapInvoke` budget, `MaxSoapResponseBytes`, `ResponseHeadersRead`, `ReadWithSizeCapAsync`) unchanged.
  - [x] 500 branch: call `SoapFaultParser.TryParse(bytes, out var fault)`. On `true` → emit `Warning SoapFault` (Url, ActionName, StatusCode=500, ErrorText) **then** throw `UpnpFaultException(...)`. On `false` → emit `Warning SoapInvoke` then throw `UpnpTransportException(ControlUrl, "HTTP 500 without parseable UPnPError", 500)`.
  - [x] Non-2xx/non-500 branch: emit `Warning SoapInvoke` (was `HttpTransport`) then throw `UpnpTransportException`.
  - [x] 2xx branch: `return new SoapResponse(request.ActionName, SoapResponseReader.ReadOutputArguments(bytes));`.
  - [x] **Delete** the now-unused `TryParseUPnPError` private method (its job moved to `SoapFaultParser`).
  - [x] Leave the `catch` blocks (timeout → `HttpTimeout`; `HttpRequestException` → `HttpTransport`; external-cancel silent rethrow) **unchanged**.
- [x] **Task 6 — Update existing tests + fakes** (AC-3.1.6)
  - [x] `tests/.../Fakes/StubUpnpHttpClient.cs` — add `using ohSpy.Core.Models;` (its `InvokeActionAsync` body still `throw new NotSupportedException();` — no logic change).
  - [x] `tests/.../Http/UpnpHttpClientTests.cs` — update `SampleSoap()` to build `new SoapRequest(SampleControlUrl, "urn:…:AVTransport:1", "Browse", [])` (or with `SoapArgument`s). Update `InvokeAction_HappyPath…` to return a `<u:BrowseResponse>` body with arg children and assert on `result.OutputArguments` (the `StatusCode`/`ResponseXml` assertions are gone). Add `SoapFault`/`SoapInvoke` diagnostic-category assertions to the 500-parsable / 500-unparsable / non-2xx tests. Add `using ohSpy.Core.Models;`.
- [x] **Task 7 — New tests + fixtures** (AC-3.1.6)
  - [x] `tests/.../Soap/SoapEnvelopeBuilderTests.cs` — zero-arg, one-arg, multi-arg-adversarial golden assertions (decided: pinned inline strings — captured verbatim from XmlWriter output, lower-friction and matches the repo's existing inline-XML test style).
  - [x] `tests/.../Soap/SoapFaultParserTests.cs` — valid / missing-code / missing-desc / malformed / XXE-attempt fixtures.
  - [x] `tests/.../Soap/SoapResponseReaderTests.cs` (split out) — output args + argument-less + unescaping.
  - [x] Run full suite; confirm `dotnet build` 0 warnings, chaos suite unchanged at 1, `CoreAppBoundaryTests` + `DiagCategoriesUsageTests` green.

---

## Dev Notes

### Files you will MODIFY (read each before editing)

- **`src/ohSpy.Core/Http/UpnpHttpClient.cs:143-254`** — the `InvokeActionAsync` method + the inline `TryParseUPnPError` helper.
  - *Current state:* fully functional. Builds `POST` with `StringContent(request.EnvelopeXml, UTF8, "text/xml")`, sets quoted `SOAPAction`, enforces header + streaming size cap, reads body to `responseXml` string. On 500: inline-parses `<UPnPError>` → `UpnpFaultException` (no diagnostic emitted on the parsable path today); on unparsable-500 → `Warning HttpTransport` + `UpnpTransportException(500)`; on non-2xx → `Warning HttpTransport` + `UpnpTransportException`; on 2xx → `new SoapResponse(resp.StatusCode, responseXml)`.
  - *What changes:* envelope source (builder), response shape (structured args via reader), fault parse (extracted to `SoapFaultParser`), and **diagnostic categories** (new `SoapFault` emit on parsable-500; `HttpTransport`→`SoapInvoke` on unparsable-500 + non-2xx).
  - *What you MUST preserve:* the linked-CTS timeout discipline, `HttpCompletionOption.ResponseHeadersRead`, `EnforceSizeCapOnHeaders` + `ReadWithSizeCapAsync`, the quoted `SOAPAction` format, the three `catch` blocks (external-cancel silent rethrow / timeout→`HttpTimeout` / `HttpRequestException`→`HttpTransport`), and `Encoding.UTF8, "text/xml"` content type. These are Story 1.3's hard-won NFR-P2 guarantees — don't regress them.
- **`src/ohSpy.Core/Http/IUpnpHttpClient.cs:38`** — signature unchanged; add `using ohSpy.Core.Models;` (records moved namespace).
- **`tests/.../Http/UpnpHttpClientTests.cs`** — `SampleSoap()` (L229), `InvokeAction_HappyPath…` (L266-285, asserts `ResponseXml`/`StatusCode` — both gone), `InvokeAction_Malformed500…`, `InvokeAction_Soap500WithFault…`. The timeout/cancel/transport/oversize tests still compile (they only touch `SampleSoap()` + exception types).
- **`tests/.../Fakes/StubUpnpHttpClient.cs`** — `using` only.

### Files you will CREATE

```
src/ohSpy.Core/Models/SoapArgument.cs          # (Name, Value)
src/ohSpy.Core/Models/SoapRequest.cs           # relocated + reshaped
src/ohSpy.Core/Models/SoapResponse.cs          # relocated + reshaped
src/ohSpy.Core/Soap/SoapEnvelopeBuilder.cs     # Build(SoapRequest) -> string
src/ohSpy.Core/Soap/SoapFaultParser.cs         # TryParse(byte[], out UpnpFault)
src/ohSpy.Core/Soap/UpnpFault.cs               # (ErrorCode, ErrorDescription) data carrier
src/ohSpy.Core/Soap/SoapResponseReader.cs      # ReadOutputArguments(byte[]) (or private in client)
tests/ohSpy.Core.Tests/Soap/SoapEnvelopeBuilderTests.cs
tests/ohSpy.Core.Tests/Soap/SoapFaultParserTests.cs
tests/ohSpy.Core.Tests/Soap/SoapResponseReaderTests.cs   # if split out
```

### Decision — folder & namespace (resolves a three-way discrepancy)

The records currently live in `Http/` (1.3 reality); the **architecture canonical source tree** (arch L2117-2122) and the **story AC** both place them in `Models/`. Because the reshape already breaks every consumer of these records, relocating them now is near-zero marginal cost and removes the divergence permanently. **Decision: records → `Models/` (namespace `ohSpy.Core.Models`); builder + fault parser + response reader + `UpnpFault` → new `Soap/` dir (namespace `ohSpy.Core.Soap`)** per arch L2126-2128. Do the move with the reshape, not as a separate step.

### `SoapEnvelopeBuilder` specifics

- **Use `XmlWriter`, not string concatenation.** It gives you correct XML-escaping (AC #7) and well-formed self-closing empty elements (AC #9) for free. Manual string building is how escaping bugs ship.
- **`OmitXmlDeclaration = true`.** Pitfall: an `XmlWriter` over a `StringWriter` emits `<?xml version="1.0" encoding="utf-16"?>` (StringWriter is UTF-16) — wrong charset in the declaration, and UPnP devices key off the `Content-Type: …; charset="utf-8"` header anyway. Omitting the declaration sidesteps this entirely and is valid per UDA 1.0 §3.2.1 (the declaration is optional in the body). Document this inline.
- Namespaces: `s` = `http://schemas.xmlsoap.org/soap/envelope/`, `s:encodingStyle` = `http://schemas.xmlsoap.org/soap/encoding/`, `u` = `req.ServiceType`. Write the action element with `writer.WriteStartElement("u", req.ActionName, req.ServiceType)` so the `xmlns:u` lands on the action element exactly as the spec shows.
- Element content (`WriteElementString(arg.Name, arg.Value)` or `WriteString`) auto-escapes `< > &`. Apostrophe/quote need no escaping in element text — that's spec-correct; your adversarial-input golden test should reflect what XmlWriter actually emits (`&lt; &gt; &amp;` plus literal `'` `"`), not a hand-guessed string.

### `SoapFaultParser` + `UpnpFault` specifics

- **Reuse `UpnpXmlReaderSettings.Create()`** (`ohSpy.Core.Scpd`, `internal static`). Same assembly ⇒ accessible from `ohSpy.Core.Soap`. This is the single XXE-locked settings factory (DtdProcessing.Prohibit, XmlResolver=null, 4M char cap). Do **not** reconstruct settings inline the way the soon-to-be-deleted `TryParseUPnPError` did.
- Settings have `Async = true`; that is fine for synchronous `reader.Read()` usage — no need for a separate sync settings object.
- **Success gate = a parsed non-zero `errorCode`** (mirrors the current inline logic `return errorCode != 0`). Missing `<errorDescription>` ⇒ still `true`, `ErrorDescription = ""`. Missing/zero `<errorCode>` ⇒ `false`. Malformed XML / DOCTYPE (XXE attempt) ⇒ `XmlException` ⇒ caught ⇒ `false`.
- `UpnpFault` is a **data record**, deliberately separate from `UpnpFaultException`: the parser returns data; the HTTP client decides whether to throw. Keeps the parser pure + unit-testable without exception plumbing.

### `InvokeActionAsync` — exact diagnostic behaviour after the change

| Path | Exception thrown | Diagnostic | vs today |
|---|---|---|---|
| 2xx OK | none | none | unchanged (shape of return differs) |
| 500 + parsable `<UPnPError>` | `UpnpFaultException` | **`Warning SoapFault`** (Url, ActionName, StatusCode=500, ErrorText) | **NEW emit** (today: no diagnostic) |
| 500 + unparsable | `UpnpTransportException(500)` | `Warning` **`SoapInvoke`** | was `HttpTransport` |
| non-2xx / non-500 | `UpnpTransportException(status)` | `Warning` **`SoapInvoke`** | was `HttpTransport` |
| per-op timeout | `UpnpTimeoutException` | `Warning HttpTimeout` | unchanged |
| caller cancel | `OperationCanceledException` | none | unchanged |
| `HttpRequestException` | `UpnpTransportException` | `Warning HttpTransport` | unchanged |

- **`DeviceUuid` is absent at the HTTP layer.** `SoapRequest` carries no UUID and `InvokeActionAsync(SoapRequest, ct)` has no uuid parameter — so the `SoapFault`/`SoapInvoke` diagnostics emit with `DeviceUuid = null`. This is expected: Story 3.2's popup VM emits a *second*, UUID-bearing `SoapFault` diagnostic at the catch site (epic L1409 explicitly anticipates the possible duplicate and leaves suppression to 3.2's judgment). Do **not** invent a uuid parameter on the facade method in this story.
- `DiagCategories.SoapInvoke` (`"Soap.Invoke"`) and `DiagCategories.SoapFault` (`"Soap.Fault"`) **already exist** (`DiagCategories.cs:48-53`, pre-added for this story). No constant additions; `DiagCategoriesUsageTests` is reflection-based over all constants and needs no edit.
- Note the diagnostic for the parsable-fault path must be emitted **inside the `try`** (before the throw), because that throw originates inside the `try` and would otherwise hit the `catch (HttpRequestException)` block — it won't (it's a `UpnpFaultException`), but the existing code already emits-before-throw inside the try for the 500 paths; follow that established shape (`UpnpHttpClient.cs:172-184`).

### Exceptions hierarchy (Amendment A5 / A9)

- `UpnpFaultException(Uri url, string actionName, int errorCode, string errorDescription)` already exists exactly as needed (`UpnpExceptions.cs:62-74`). Use it verbatim.
- **Opportunistic, NON-BLOCKING:** the shipped `UpnpTransportException` ctor (`UpnpExceptions.cs:40-44`) still uses the `inner ?? new InvalidOperationException(message)` synthetic-inner form that **Amendment A9** flagged for removal ("any author who touches `UpnpExceptions.cs` next can pick it up"). If you touch that file, apply the A9 fix (`: base(message, inner)` and widen the abstract base's `Exception inner` → `Exception? inner`). If you'd rather keep this story tight, leave it and note it for a later pickup — it is **not** an AC of 3.1. Don't let it expand scope.

### Testing standards

- xUnit v2 + FluentAssertions; mirror-tree layout (`tests/ohSpy.Core.Tests/Soap/` mirrors `src/ohSpy.Core/Soap/`) — Pattern 5.
- Below-the-facade `UpnpHttpClient` tests use the existing `TestHttpMessageHandler` + the `Build(...)` helper in `UpnpHttpClientTests.cs` (returns `(client, handler, diagSpy)`). Assert diagnostics via `diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.SoapFault)` etc. — see the timeout test at L300 for the pattern.
- Builder/parser/reader tests are pure unit tests (no HTTP). For adversarial-escaping (AC #7) assert the **actual XmlWriter output**, not a hand-written expectation.
- Keep `[Trait("ac", "...")]` on every test (the suite filters by it). Preserve existing `AC-3.3` traits on the fault tests.
- Gates before review: `dotnet build` 0 warnings; full suite green (current baseline 313 passed / 2 skipped — this story nets new tests, expect ~325+); chaos suite still 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green.

### Project structure notes

- **Pure `ohSpy.Core`. No App / WinUI / XAML surface in this story** → **no manual UI smoke required.** The "smoke per UI-touching story" discipline (Epic 2 retro action E) applies to **Story 3.2** (the invocation popup), not 3.1. All of 3.1 is unit-testable below the facade.
- No DI changes: `IUpnpHttpClient` is registered `AddSingleton<IUpnpHttpClient, UpnpHttpClient>()` with `Configure<HttpTimeoutOptions>` in `src/ohSpy.App/Composition/ServiceRegistration.cs:33-34`. The interface signature is unchanged, so registration is untouched.
- `CoreAppBoundaryTests` forbids `Core → App` — everything here is Core-internal, so the boundary is naturally respected.
- Forward handoff to **Story 3.2**: the popup VM builds a `SoapRequest` from `ScpdAction` + `ArgumentInputViewModel` values, resolving `ServiceDescription.ControlUrl` (a *relative* string, `ServiceDescription.cs`) against the device `LocationUrl` into the absolute `Uri ControlUrl`, and maps `Inputs.Select(i => new SoapArgument(i.Name, i.Value))`. Nothing for you to build now — just make the records ergonomic for that call.

### References

- Story 3.1 ACs: `_bmad-output/planning-artifacts/epics.md:1273-1327`
- Canonical source tree (Models/ + Soap/ placement): `…/architectures/arch-ohSpy-2026-05-31/architecture.md:2113-2128`
- Decision 3 (HTTP facade invariant, size caps, typed exceptions): `architecture.md:264-396`
- Decision 11 (`SoapInvoke` 10 s default): `architecture.md:1393-1466`
- Amendment A5 (exception hierarchy concrete shape): `architecture.md:2524-2598`
- Amendment A9 (`UpnpTransportException` synthetic-inner fix): `architecture.md:2666-2694`
- Pattern 11 / D8 diagnostics discipline: `architecture.md:1003-1076, 1928-1930`
- Current implementation under change: `src/ohSpy.Core/Http/UpnpHttpClient.cs:143-254`
- Shared XML settings to reuse: `src/ohSpy.Core/Scpd/UpnpXmlReaderSettings.cs`
- DiagCategories (SoapInvoke/SoapFault pre-added): `src/ohSpy.Core/Diagnostics/DiagCategories.cs:48-53`
- Test surface: `tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs:229-346`, `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs:66`

### Previous-story intelligence (Epic 2 patterns that carry over)

- **Sealed-record data carriers (Pattern 9)** are the house style for all the SOAP types — see `ScpdAction`/`ScpdArgument`/`ServiceDescription`.
- **`new`-constructed helpers + static builders/parsers** (not DI) for pure transforms — consistent with `SsdpParser`, `DeviceDescriptionParser`, `XmlReaderScpdParser`.
- **Emit-before-throw inside `try`, structured `DiagnosticContext`, never string-interpolate context into the message** (Pattern 11) — the 1.3 code already models this; match it.
- Epic 2 recurring lesson: *"trivially passing? prove it's wired."* Here that means the happy-path test must assert **actual structured output args came back through the reader**, not just that no exception threw. The Epic 2 device-tree bug hid behind tests that set state directly instead of through the real path — don't assert on inputs you handed in; assert on what the parse produced.

### Git intelligence

Recent commits (`8af34e8`…`a61ca08`) are Epic 2 UI + discovery work — no overlap with the SOAP layer, so no merge hazards. The last time this exact code was touched was Story 1.3 (`UpnpExceptions.cs` / `UpnpHttpClient.cs`, commit `8a6fb44` per Amendment A9). Branch is `main`; this is the first Epic 3 story.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (bmad-dev-story workflow)

### Debug Log References

- One implementation bug found+fixed during RED→GREEN: the initial `SoapResponseReader` loop combined `while (reader.Read())` with `ReadElementContentAsString()`, which double-advanced and skipped every other output arg (multi-arg + happy-path tests failed: 2 of N args returned). Fixed by driving the reader manually with `continue` after the content-read (same pitfall the deleted inline `TryParseUPnPError` warned about). Re-ran → green.

### Completion Notes List

- **RESHAPE confirmed, not greenfield.** `InvokeActionAsync` was fully wired by Story 1.3; this story swapped the pre-built-string plumbing for structured records + dedicated `Soap/` classes and corrected the diagnostic categories. The Story 1.3 NFR invariants (linked-CTS timeout, `ResponseHeadersRead`, `EnforceSizeCapOnHeaders` + `ReadWithSizeCapAsync`, quoted `SOAPAction`, `Encoding.UTF8 "text/xml"`, the three catch blocks) are byte-for-byte preserved — only the envelope source, response shape, fault-parse call, and the 500/non-2xx diagnostic categories changed.
- **Records relocated `Http/` → `Models/`** (namespace `ohSpy.Core.Models`), removing the architecture/AC divergence permanently per the Decision in Dev Notes. Old `Http/SoapRequest.cs` + `Http/SoapResponse.cs` `git rm`'d.
- **Diagnostic categories applied exactly per the Dev Notes table:** parsable-500 now emits a **new** `Warning SoapFault` (Url, ActionName, StatusCode=500, ErrorText=`"{code}: {desc}"`) before throwing `UpnpFaultException`; unparsable-500 and non-2xx/non-500 switched `HttpTransport → SoapInvoke`; timeout (`HttpTimeout`), caller-cancel (silent), and `HttpRequestException` (`HttpTransport`) catch blocks unchanged. `DeviceUuid` is absent at this layer (no uuid param invented — Story 3.2 adds the UUID-bearing emit).

#### Engineering-judgment decisions / deviations
- **`SoapEnvelopeBuilder` / `SoapFaultParser` / `SoapResponseReader` are `internal static`**, not `public`. The story AC quotes `public static` signatures, but the house style + `InternalsVisibleTo(ohSpy.Core.Tests + ohSpy.App)` makes `internal` correct (these are Core-internal transforms with no external-assembly consumer; matches `UpnpXmlReaderSettings`, `SsdpParser` et al). Tests access them via the existing `InternalsVisibleTo`. `SoapArgument`/`SoapRequest`/`SoapResponse`/`UpnpFault` records are `public` per AC (they cross the facade).
- **`SoapResponseReader` implemented as a dedicated class** (the story's recommended option), not a private helper in `UpnpHttpClient` — testable in isolation + symmetric with the builder/parser.
- **Golden assertions = pinned inline strings**, not golden `Fixtures/Soap/*.xml`. Captured verbatim from actual XmlWriter output (via a throwaway probe), so they double as a framing/namespace/escaping regression guard and match the repo's inline-XML test idiom. Confirmed XmlWriter emits `s:encodingStyle` before the `xmlns:s` decl and leaves `"`/`'` literal in element text (spec-correct) — the adversarial-input test asserts the real output, not a hand-guess.
- **A9 NOT applied (left for later pickup).** The `UpnpTransportException` synthetic-inner form (`inner ?? new InvalidOperationException(message)`) is untouched — I kept the story tight; it is explicitly non-blocking and not an AC of 3.1. Flagged below for the reviewer.

#### Follow-ups for the code reviewer
- **A9 pickup (optional, non-blocking):** `UpnpExceptions.cs:40-44` still uses the synthetic-inner shape Amendment A9 flagged. I deliberately did not touch `UpnpExceptions.cs` to avoid scope creep. Available for a future PR that edits that file.
- The only build warning is the **pre-existing** benign `WMC1506` on `MainWindow.xaml:141` (Story 2.5 FallbackTemplate) — not introduced here; no XAML was touched. The Core project itself builds 0/0.

### File List

**Created**
- `src/ohSpy.Core/Models/SoapArgument.cs`
- `src/ohSpy.Core/Models/SoapRequest.cs` (relocated + reshaped from `Http/`)
- `src/ohSpy.Core/Models/SoapResponse.cs` (relocated + reshaped from `Http/`)
- `src/ohSpy.Core/Soap/SoapEnvelopeBuilder.cs`
- `src/ohSpy.Core/Soap/UpnpFault.cs`
- `src/ohSpy.Core/Soap/SoapFaultParser.cs`
- `src/ohSpy.Core/Soap/SoapResponseReader.cs`
- `tests/ohSpy.Core.Tests/Soap/SoapEnvelopeBuilderTests.cs`
- `tests/ohSpy.Core.Tests/Soap/SoapFaultParserTests.cs`
- `tests/ohSpy.Core.Tests/Soap/SoapResponseReaderTests.cs`

**Modified**
- `src/ohSpy.Core/Http/UpnpHttpClient.cs` (re-wired `InvokeActionAsync`; deleted inline `TryParseUPnPError`; updated usings)
- `src/ohSpy.Core/Http/IUpnpHttpClient.cs` (added `using ohSpy.Core.Models;`)
- `tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs` (reshaped `SampleSoap()`; structured happy-path assertions; SoapFault/SoapInvoke diagnostic assertions; new non-2xx/non-500 test; added `using`)
- `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs` (added `using ohSpy.Core.Models;`)

**Deleted**
- `src/ohSpy.Core/Http/SoapRequest.cs`
- `src/ohSpy.Core/Http/SoapResponse.cs`

### Review Findings

- [x] [Review][Patch] **Malformed 2xx body caused untyped XmlException to escape** [`src/ohSpy.Core/Http/UpnpHttpClient.cs:197`] — Applied: added inner try/catch(XmlException) around `SoapResponseReader.ReadOutputArguments(bytes)` call; converts to `UpnpProtocolException` + `Warning SoapInvoke` diagnostic. Confirmed with new test `InvokeAction_HappyPath_MalformedResponseXml_ThrowsUpnpProtocolExceptionAndEmitsSoapInvoke`.
- [x] [Review][Patch] **New malformed-2xx-body test missing** [`tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs`] — Applied: added `InvokeAction_HappyPath_MalformedResponseXml_ThrowsUpnpProtocolExceptionAndEmitsSoapInvoke` as `[Trait("ac", "AC-3.1.4")]`. Test green.
- [x] [Review][Patch] **Trait bookkeeping: 500-path tests cover AC-3.1.5 behavior but missing that trait** [`tests/ohSpy.Core.Tests/Http/UpnpHttpClientTests.cs:237,262`] — Applied: added `[Trait("ac", "AC-3.1.5")]` alongside existing `AC-3.3` on both `InvokeAction_Soap500WithFault_ThrowsUpnpFaultException` and `InvokeAction_Malformed500_ThrowsUpnpTransportException`.
- [x] [Review][Defer] **`$"unexpected status {(int)resp.StatusCode}"` string interpolation in diagnostic message** [`src/ohSpy.Core/Http/UpnpHttpClient.cs:192`] — deferred, pre-existing from Story 1.3 baseline; not introduced by this story. Pattern 11 purists would move the status code to context only, but the StatusCode is already in `DiagnosticContext.StatusCode` so context IS structured. The message redundancy is cosmetic.
- [x] [Review][Defer] **`UpnpFault` public but only consumed internally** [`src/ohSpy.Core/Soap/UpnpFault.cs:12`] — deferred, deliberate design choice; Story 3.2 may reference it, and `public` doesn't create an API contract issue here.
- [x] [Review][Defer] **A9 synthetic-inner `UpnpTransportException` fix** [`src/ohSpy.Core/Http/UpnpExceptions.cs:40-44`] — deferred, explicitly flagged by dev as non-blocking out-of-scope for 3.1. Carry forward to any future PR touching `UpnpExceptions.cs`.

### Change Log

- 2026-06-03 — Story 3.1 implemented (claude-opus-4-8[1m]). Reshaped SoapRequest/SoapResponse + new SoapArgument record, relocated `Http/` → `Models/`. Added `Soap/` dir: SoapEnvelopeBuilder (XmlWriter, OmitXmlDeclaration), SoapFaultParser (shared XXE-locked settings) + UpnpFault data record, SoapResponseReader. Re-wired `InvokeActionAsync` onto the builder/reader/parser; corrected diagnostics (new SoapFault emit on parsable-500; HttpTransport→SoapInvoke on unparsable-500 + non-2xx). 16 new tests. Build 0 warnings (Core), full suite 329 passed / 2 skipped / 0 failed; chaos 1.
- 2026-06-03 — Code review (claude-sonnet-4-6, bmad-code-review). APPROVED WITH MINOR FIXES. 3 patches applied (malformed-2xx escape path + test + trait bookkeeping), 3 defers recorded. Build 0/0 (Core), 1 pre-existing WMC1506 (App). Suite 330 passed / 2 skipped / 0 failed; chaos=1; CoreAppBoundary + DiagCategoriesUsage green. Story → done.
