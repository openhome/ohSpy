# Sprint Change Proposal — Device identity must be the UDN string, not a parsed `Guid`

**Date:** 2026-06-04
**Raised by:** Simonc (Project Lead) — discovered during Story 5.2 manual smoke on a live Linn network
**Author:** Developer (correct-course workflow)
**Scope classification:** **Moderate** (contained but cross-cutting refactor; amends Architecture **Decision 9** + **FR-041**) → implement as a single corrective story (create-story → dev-story → code-review), with an architecture amendment.

---

## 1. Issue Summary

**Problem:** ohSpy models a device's identity as a `System.Guid` parsed from the SSDP USN's `uuid:` token. **UPnP UDNs are opaque strings** — UDA 1.0/1.1 only *recommends* (a "should") that the part after `uuid:` be an RFC 4122 UUID; control points are required to match the UDN **as a string**, not by parsing it into a numeric GUID. Real devices (Linn's included) routinely use non-RFC-4122 UDNs.

**Symptom (smoke, 2026-06-04):** on a network whose device has a non-standard UDN, starting/stopping the device shows ALIVE/BYEBYE in the SSDP log but with an **all-zero UDN**, and **no device appears in the tree**.

**Confirmed root cause (in code):**
- `SsdpParser.ExtractUuid` (`src/ohSpy.Core/Discovery/SsdpParser.cs:80-87`) does `Guid.TryParse(token) ? g : null` → a non-RFC-4122 UDN yields **`null`**.
- `SsdpLogViewModel` (`:58`) renders `ann.Uuid ?? Guid.Empty` → the **all-zero UDN** in the log.
- `DiscoveryService.RouteOnUiThread` (`:107,:115`) gates every registry mutation on **`if (ann.Uuid.HasValue && …)`** → a `null` UUID means `OnAlive` is **never called**; the device never enters the registry → **no tree row**.
- `EagerDescriptionDispatcher.UdnMatches` (`:124-127`) re-parses the description's UDN to a `Guid` the same way, so even a routed device would fail the root-match.

This is a **pre-existing defect from Stories 2.3/2.4**, not a Story 5.2 regression — 5.2's adapter switch works (the transport receives, the log populates). It would also fail on a *direct* startup on that network. The Sky devices happened to ship RFC-4122 UUIDs, which is why it only surfaced now.

**Decision (Project Lead):** the proper fix — model device identity as the **UDN string** end-to-end, and amend **Decision 9 "UUID-keyed registry" → "UDN-keyed (string identity)"**.

---

## 2. Impact Analysis

### Epic / Story impact
- **No epic re-scoping.** This is a defect correction across the Epic 2 foundation that ripples into Epics 3/4 (popups) and 5 (registry clear). No AC changes to delivered stories; their behaviour is preserved (RFC-4122 devices keep working) and extended (non-RFC-4122 devices now work).
- Tracked as **one new corrective story** (proposed key `2-10-udn-string-identity` — an Epic-2 correction, run now before 5.2 smoke resumes). Status flows like any story.

### Architecture / artifact conflicts
- **Decision 9 — "UUID-keyed registry"** → amend to **"UDN-keyed registry (string identity)"**. New amendment **A30** documenting: UPnP UDNs are opaque strings (RFC 4122 is a SHOULD); identity is the full `uuid:<body>` string compared `OrdinalIgnoreCase`; the `Guid.TryParse` model was the defect.
- **FR-041 (Diagnostics Identity column):** the `DiagnosticContext.DeviceUuid` becomes `string?`; the "FriendlyName else `uuid:<uuid>` else `—`" rule is unchanged in spirit (the fallback is now the UDN string, which already carries the `uuid:` prefix).
- **PRD:** no change (no requirement said "GUID"; FR-041 only references identity).

### Technical impact — the precise code surface (production)
Identity type changes from `Guid` → `string` (the normalised UDN) in:

| File | Change |
|---|---|
| `Discovery/SsdpParser.cs` | `ExtractUuid(usn): Guid?` → `ExtractUdn(usn): string?` — strip `uuid:`-prefixed token from USN, drop the `::<nt>` suffix, **no Guid parse**; null only when there is no `uuid:` token. |
| `Discovery/SsdpAnnouncement.cs` | `Guid? Uuid` → `string? Udn`; `IsRootDevice` unchanged. |
| `Discovery/DiscoveryService.cs` | route gate `ann.Uuid.HasValue` → `!string.IsNullOrEmpty(ann.Udn)`; pass the string to `OnAlive`/`OnByebye`. |
| `Devices/DeviceRegistry.cs` | backing `ConcurrentDictionary<Guid,…>` → `<string,…>` with `StringComparer.OrdinalIgnoreCase`; `OnAlive/OnByebye/Remove/TryGetEntry/RemoveCore/Clear` `Guid` → `string`; `DeviceRemoved` `Action<Guid>` → `Action<string>`. |
| `Devices/IDeviceRegistry.cs` | `TryGetEntry`, `DeviceRemoved` signatures. |
| `Devices/RegistryEntry.cs` | `Guid Uuid` → `string Udn` (+ ctor param). |
| `Devices/EagerDescriptionDispatcher.cs` | `UdnMatches(string descUdn, Guid)` → `(string descUdn, string registeredUdn)` ordinal-ignore-case string compare (strip `uuid:` from both); the two `DeviceUuid = entry.Uuid` emits. |
| `Diagnostics/DiagnosticContext.cs` | `Guid? DeviceUuid` → `string? DeviceUuid`. |
| `Diagnostics/DiagnosticRingSink.cs` | the identity-resolution block + the `uuid:<uuid>` fallback (now the string directly). |
| `Diagnostics/IDiagnosticIdentityLookup.cs` + `RegistryIdentityLookup.cs` + `NullIdentityLookup.cs` | `TryGetFriendlyName(Guid)` → `(string udn)`. |
| `ViewModels/DeviceNodeViewModel.cs` | `Guid Uuid` → `string Udn`. |
| `ViewModels/DeviceTreeViewModel.cs` | `OnDeviceRemoved(Guid)` → `(string)`. |
| `ViewModels/PropertiesViewModel.cs`, `InvocationPopupViewModel.cs`, `SubscriptionPopupViewModel.cs` | `Guid _uuid` → `string _udn`; `OnDeviceRemoved(Guid)` → `(string)`; the FR-037 match → `OrdinalIgnoreCase`. |
| `Models/SsdpLogEntry.cs` + `ViewModels/SsdpLogViewModel.cs:58` | `Guid Uuid` → `string Udn`/`UdnText`; the log line drops `?? Guid.Empty` → shows the real UDN (fixes the all-zero display). |
| diagnostic emit sites passing `DeviceUuid =` (`SubscriptionClient`, `BrowserLaunch`, `ServiceNodeViewModel`, `InvocationPopupViewModel`, …) | mechanical — the source (`parentEntry.Uuid` / `_deviceUuid`) becomes a string. |

**Do NOT change:** `SubscriptionClient._pending` is `ConcurrentDictionary<Guid,Subscription>` keyed by a per-subscribe **correlation id** (`Guid.NewGuid()`), not device identity — leave it.

### Test impact
Broad but mechanical: every device-identity `Guid.NewGuid()` / `Guid.Parse("…")` in `DeviceRegistryTests`, `DiscoveryServiceTests`, `SsdpParserTests`, `EagerDescriptionDispatcherTests`, `SsdpLogViewModelTests`, `DiagnosticRingSink`/identity tests, the three popup-VM test suites, `DeviceTreeViewModelTests`, `ShellViewModelTests` (Clear/cascade) + the fakes (`FakeDeviceRegistry`, `RegistryEntry`/entry helpers) becomes a string UDN. **New tests to add:** `SsdpParser` extracts a non-RFC-4122 UDN as a string (the regression); `UdnMatches` ordinal-ignore-case (GUID-cased + opaque); registry round-trips a non-GUID UDN; a non-GUID device reaches `DeviceLoaded` (end-to-end via the dispatcher); the popup FR-037 match on a string UDN.

### Risk
- **Wide blast radius** (the whole identity spine) but **mostly a type swap** + two real logic changes (`ExtractUdn` string extraction, `UdnMatches` string compare). Guarded by the full suite + the new regression tests.
- **Behaviour-preserving for RFC-4122 devices** (the Sky-network smoke stays valid). The `OrdinalIgnoreCase` comparer matches the old Guid-equality semantics for hex UDNs.
- Low architectural risk: no new dependencies, no DI-graph change, Core stays WinUI-free.

---

## 3. Recommended Approach

**Direct adjustment — one corrective story.** Implement the UDN-string-identity refactor as a single story (`2-10-udn-string-identity`, an Epic-2 correction inserted at the head of the current work), with architecture **Amendment A30** folded in. Run it through the normal **create-story → dev-story → code-review** cycle (the wide blast radius + the normalisation decisions justify the full create-story context-engineering). Then **resume the Story 5.2 smoke**, which will now detect the Linn device — closing the verification keystone.

**Sequencing:** do this BEFORE finishing 5.2's smoke (5.2 stays committed at `review`; the smoke can't pass until devices are detectable). After 2-10 lands and 5.2's smoke passes, mark 5.2 + the bundled deferred items `done` → Epic 4 closes.

**Rationale for not doing the minimal Guid-hash fallback:** the Project Lead chose correctness — the UDN string is the spec-accurate identity, keeps the real UDN visible in diagnostics/log/identity, and removes the synthetic-GUID concept entirely.

---

## 4. Detailed Change Proposals

### Architecture — Amendment A30 (new)
> **A30 — Device identity is the UDN string, not a parsed `Guid` (Decision 9 correction).** UPnP UDNs are opaque strings (`uuid:` + an identifier; UDA recommends but does not require RFC 4122). Devices in the wild — including Linn — use non-RFC-4122 UDNs. The original Decision 9 keyed the registry on a `Guid` parsed via `Guid.TryParse`, which silently drops every non-RFC-4122 device (parse → null → `DiscoveryService` skips the announcement). **Correction:** identity is the full normalised UDN **string** (`uuid:<body>`, the `::<nt>` suffix stripped), compared `OrdinalIgnoreCase`. The registry is **UDN-keyed**; `DiagnosticContext.DeviceUuid`, `RegistryEntry`, `IDeviceRegistry.DeviceRemoved`, the identity lookup, and the popup FR-037 banners all carry the string. `Guid.TryParse` on a UDN is forbidden. Surfaced by the Story 5.2 smoke (2026-06-04); fixed in Story 2-10.

### Decision 9 text
> OLD: "**UUID-keyed** device registry … keyed by the device `Guid`."
> NEW: "**UDN-keyed** device registry (string identity, `OrdinalIgnoreCase`) — see Amendment A30. UPnP UDNs are opaque strings; the registry never parses them to `Guid`."

### Code — see the table in §2 for the exhaustive per-file change set (old `Guid` → new `string` UDN).

---

## 5. Implementation Handoff

- **Scope:** Moderate → one corrective story `2-10-udn-string-identity` + architecture A30.
- **Recipients:** create-story (context-engineer the refactor + the test plan + A30) → dev-story (implement; the full suite + new regression tests are the gate; no manual smoke — it's Core) → code-review (Sonnet, fresh context; adversarial on the normalisation + that no RFC-4122 behaviour regressed).
- **Success criteria:** (1) a non-RFC-4122 UDN device is parsed, registered, fetched, and rendered in the tree; (2) the SSDP log shows the real UDN, not all-zero; (3) FR-037 banners + diagnostics Identity column carry the UDN string; (4) full suite green + new regression tests; (5) Decision 9 amended (A30). Then **re-run the 5.2 smoke** on the Linn network.
