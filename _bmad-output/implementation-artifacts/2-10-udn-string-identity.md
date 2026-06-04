---
baseline_commit: 5db8b2c7ba38020f1c757cf628c54b3844f9f25d
---

# Story 2.10: UDN string identity (Decision 9 correction — Amendment A30)

Status: done

<!-- Corrective story (correct-course). Requirements source: sprint-change-proposal-2026-06-04.md (NOT epics.md). -->

## Story

As a **ohSpy operator on a real UPnP network (including Linn devices)**,
I want **device identity to be the opaque UDN string the device advertises, not a parsed `System.Guid`**,
so that **devices with non-RFC-4122 UDNs are discovered, registered, fetched, and shown in the tree — instead of being silently dropped and logged with an all-zero UDN**.

## Problem Statement

ohSpy models a device's identity as a `System.Guid` parsed from the SSDP USN's `uuid:` token. **UPnP UDNs are opaque strings** — UDA 1.0/1.1 only *recommends* (a SHOULD) that the part after `uuid:` be an RFC 4122 UUID; control points are required to match the UDN **as a string**. Real devices (Linn's included) routinely use non-RFC-4122 UDNs.

Confirmed root cause (verified in shipped code, 2026-06-04):
- `SsdpParser.ExtractUuid` (`SsdpParser.cs:80-87`) does `Guid.TryParse(token) ? g : null` → a non-RFC-4122 UDN yields **`null`**.
- `DiscoveryService.RouteOnUiThread` (`:107`, `:115`) gates EVERY registry mutation on `if (ann.Uuid.HasValue && …)` → a `null` UUID means `OnAlive`/`OnByebye` is **never called** → the device never enters the registry → **no tree row**.
- `SsdpLogViewModel` (`:58`) renders `ann.Uuid ?? Guid.Empty` → the **all-zero UDN** in the log.
- `EagerDescriptionDispatcher.UdnMatches` (`:124-127`) re-parses the description's `<UDN>` to a `Guid` the same way → even a routed non-RFC-4122 device would fail the root match.

This is a **pre-existing defect from Stories 2.3/2.4**, not a Story 5.2 regression (5.2's adapter switch works; the log populates). The Sky devices ship RFC-4122 UUIDs, which is why it only surfaced now.

**The fix (Project Lead decision):** model device identity as the **UDN string** end-to-end (the full normalised `uuid:<body>`, `::<nt>` suffix stripped, compared `OrdinalIgnoreCase`), and amend Architecture **Decision 9 "UUID-keyed" → "UDN-keyed (string identity)"** via new **Amendment A30**. `Guid.TryParse` on a UDN is forbidden.

## Acceptance Criteria

1. **Identity is a UDN string.** Device identity is modelled as a normalised UDN `string` (the full `uuid:<body>`, with any `::<nt>` suffix stripped) everywhere it was a `Guid`: `SsdpAnnouncement.Udn`, `RegistryEntry.Udn`, the `DeviceRegistry` key, `IDeviceRegistry.TryGetEntry`/`DeviceRemoved`, `DiagnosticContext.DeviceUuid`, `IDiagnosticIdentityLookup.TryGetFriendlyName`, the device-tree node identity, and the three popup VMs' identity field. No `Guid.TryParse` is applied to a UDN anywhere in the discovery / identity path.
2. **Non-RFC-4122 UDN is parsed.** `SsdpParser.ExtractUdn(usn)` returns a non-RFC-4122 UDN verbatim as the normalised string (no parse, no null), and returns `null` ONLY when the USN has no `uuid:` token.
3. **Non-RFC-4122 UDN is registered + fetched + rendered.** A root-device alive with a non-RFC-4122 UDN routes into `DeviceRegistry.OnAlive` (the gate is now `!string.IsNullOrEmpty(ann.Udn)`), the dispatcher's `UdnMatches` accepts the device-description `<UDN>`, and the entry reaches `DeviceLoaded` → a tree row appears (the exact path that was broken).
4. **SSDP log shows the real UDN.** The SSDP log row renders the actual UDN string, NOT all-zero — the `?? Guid.Empty` is gone.
5. **FR-037 + diagnostics carry the string.** The popup device-gone banners (Properties / Invocation / Subscription) flip on a string-UDN `DeviceRemoved` (matched `OrdinalIgnoreCase`); the FR-041 diagnostics Identity column resolves from the string UDN (friendly name if known, else the UDN string itself — which already carries `uuid:`).
6. **RFC-4122 behaviour is preserved.** Every existing GUID-based test, converted to string UDNs, still passes — an RFC-4122 device discovers/registers/de-dups/renders exactly as before. `OrdinalIgnoreCase` comparison preserves the old `Guid`-equality semantics for hex UDNs (case-insensitive).
7. **`SubscriptionClient._pending` is untouched.** It stays `ConcurrentDictionary<Guid, Subscription>` keyed by the per-subscribe correlation id (`PendingId = Guid.NewGuid()`), which is NOT device identity.
8. **Architecture A30 written.** Amendment A30 is added; Decision 9's "UUID-keyed" prose is reworded to "UDN-keyed (string identity, `OrdinalIgnoreCase`)"; the §4.1 component bullet is updated.
9. **Suite green.** The full Core test suite passes (`-warnaserror` 0/0), including the new regression tests below; `CoreAppBoundaryTests` still forbids `Core → App`.

## Tasks / Subtasks

- [x] **Task 0 — Pin the normalisation + the full site list (do FIRST).** (AC: #1, #2, #5)
  - [x] Confirm the two normalisation decisions (see Dev Notes §"Normalisation decisions") and write them as a one-paragraph note in the Dev Agent Record before touching code: (a) the stored identity is the **full `uuid:<body>`** string (NOT body-only), matching the device-description `<UDN>` element; (b) all comparison is `OrdinalIgnoreCase`.
  - [x] Re-read the per-file change map in Dev Notes §"Exhaustive change map"; this is the authoritative list of sites. Do not blanket find-and-replace `Guid`.
- [x] **Task 1 — Parser: `ExtractUuid` → `ExtractUdn` (string).** (AC: #2)
  - [x] In `SsdpParser.cs` replace `internal static Guid? ExtractUuid(string? usn)` with `internal static string? ExtractUdn(string? usn)`: if `usn` is null → null; if it has no `uuid:` token → null; otherwise return the `uuid:<body>` substring with the `::<nt>` suffix stripped (keep the `uuid:` prefix; preserve original casing of the body). NO `Guid.TryParse`.
  - [x] Update the `Parse` call site (`var udn = ExtractUdn(usn);`) and the `SsdpAnnouncement` construction.
- [x] **Task 2 — Announcement: `Guid? Uuid` → `string? Udn`.** (AC: #1)
  - [x] In `SsdpAnnouncement.cs` rename the record member `Guid? Uuid` → `string? Udn`. `IsRootDevice` is unchanged.
- [x] **Task 3 — DiscoveryService: route gate + pass-through.** (AC: #3)
  - [x] `:107` byebye gate: `ann.Uuid.HasValue` → `!string.IsNullOrEmpty(ann.Udn)`; `registry.OnByebye(ann.Uuid.Value)` → `registry.OnByebye(ann.Udn!)`.
  - [x] `:115` alive gate: `ann.Uuid.HasValue && ann.Location is not null` → `!string.IsNullOrEmpty(ann.Udn) && ann.Location is not null`; `registry.OnAlive(ann.Uuid.Value, …)` → `registry.OnAlive(ann.Udn!, …)`.
- [x] **Task 4 — Registry + entry: string key.** (AC: #1, #6)
  - [x] `RegistryEntry.cs`: `Guid Uuid` → `string Udn`; ctor first param `Guid uuid` → `string udn`. Update the XML-doc summary ("UUID-keyed" → "UDN-keyed").
  - [x] `DeviceRegistry.cs`: backing `ConcurrentDictionary<Guid, RegistryEntry>` → `ConcurrentDictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)`; the `_entries` field initialiser must pass the comparer. `OnAlive`/`OnByebye`/`Remove`/`RemoveCore`/`TryGetEntry` param `Guid uuid` → `string udn`; `DeviceRemoved` is `Action<string>`; `Clear()`'s `_entries.Keys.ToArray()` is now `string[]`. Reword the class XML-doc ("UUID-keyed" → "UDN-keyed").
  - [x] `IDeviceRegistry.cs`: `TryGetEntry(Guid …)` → `(string …)`; `event Action<Guid> DeviceRemoved` → `Action<string>`. Reword the interface + `Clear()` XML-doc ("per UUID" → "per UDN").
- [x] **Task 5 — Dispatcher: `UdnMatches` string compare + emits.** (AC: #1, #3)
  - [x] `EagerDescriptionDispatcher.cs`: `UdnMatches(string udn, Guid uuid)` → `UdnMatches(string descUdn, string registeredUdn)`: strip a leading `uuid:` (OrdinalIgnoreCase) from BOTH sides, then `string.Equals(…, StringComparison.OrdinalIgnoreCase)`. NO `Guid.TryParse`. Update the XML-doc.
  - [x] Update the call `UdnMatches(description.Udn, entry.Uuid)` → `UdnMatches(description.Udn, entry.Udn)`, and the two `DeviceUuid = entry.Uuid` emits → `DeviceUuid = entry.Udn`.
- [x] **Task 6 — Diagnostics: context + sink + lookups.** (AC: #1, #5)
  - [x] `DiagnosticContext.cs`: `Guid? DeviceUuid` → `string? DeviceUuid`. (Keep the property NAME `DeviceUuid` to minimise churn at the ~10 emit sites; only the type changes.)
  - [x] `DiagnosticRingSink.cs` `ResolveIdentityLabel`: the local is now a `string` UDN. The fallback `return name ?? $"uuid:{uuid}";` becomes `return name ?? uuid;` (the UDN string already carries `uuid:`). Update the method-comment ("→ uuid:<uuid>" → "→ the UDN string"). Keep the `null DeviceUuid → "—"` branch.
  - [x] `IDiagnosticIdentityLookup.cs`: `TryGetFriendlyName(Guid deviceUuid)` → `(string udn)`. `RegistryIdentityLookup.cs` + `NullIdentityLookup.cs`: same signature; the registry impl now passes the string straight to `TryGetEntry`.
- [x] **Task 7 — Device tree VMs.** (AC: #1, #6)
  - [x] `DeviceNodeViewModel.cs`: `public Guid Uuid => _entry.Uuid;` → `public string Udn => _entry.Udn;`; the two `$"uuid:{entry.Uuid}"` / `$"uuid:{_entry.Uuid}"` friendly-name fallbacks → `entry.Udn` / `_entry.Udn` (the UDN already has `uuid:`); the `ServiceNodeViewModel` ctor arg `_entry.Uuid` → `_entry.Udn`; the `FetchXml` / `OpenProperties` paths that pass `_entry.Uuid` to `BrowserLaunch` → `_entry.Udn`.
  - [x] `DeviceTreeViewModel.cs`: `IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>` → `<string, DeviceNodeViewModel>` (the collection type is generic — no change to the collection class itself); the `vm => vm.Uuid` selector → `vm => vm.Udn`; `OnDeviceRemoved(Guid uuid)` → `(string udn)`; `Devices.TryGetItem(entry.Uuid, …)` / `Devices.Remove(uuid)` → `.Udn` / `udn`. **`DeviceNodeComparer.Compare`** tiebreak `x.Uuid.ToString()` → `x.Udn` (already a string; drop `.ToString()`), comparison stays `StringComparison.Ordinal`.
  - [x] `ServiceNodeViewModel.cs`: ctor `Guid deviceUuid` → `string deviceUdn`; field `_deviceUuid` → `_deviceUdn`; the `EmitFailure` + `FetchServiceXml` `DeviceUuid = _deviceUuid` / `BrowserLaunch(… _deviceUuid)` → `_deviceUdn`.
  - [x] `BrowserLaunch.cs`: `OpenInDefaultBrowser(Uri, IUriLauncher, IDiagnosticEmitter, Guid deviceUuid)` → `(… string deviceUdn)`; the two `DeviceUuid = deviceUuid` → `deviceUdn`.
  - [x] `ActionNodeViewModel.cs`: no identity field of its own — verify it only forwards `_parentEntry` (no change expected; confirm).
- [x] **Task 8 — Popup VMs (FR-037 match).** (AC: #1, #5)
  - [x] `PropertiesViewModel.cs`: `Guid _uuid` → `string _udn`; `_uuid = entry.Uuid` → `_udn = entry.Udn`; `Uuid = entry.Uuid.ToString()` (the displayed `Uuid` string property) → `Uuid = entry.Udn` (it is already a string — keep the public property name); `OnDeviceRemoved(Guid uuid)` → `(string udn)` with `if (uuid != _uuid …)` → `if (!string.Equals(udn, _udn, StringComparison.OrdinalIgnoreCase) …)`; the `BrowserLaunch(… _uuid)` → `_udn`.
  - [x] `InvocationPopupViewModel.cs`: `Guid _uuid` → `string _udn`; `_uuid = parentEntry.Uuid` → `_udn = parentEntry.Udn`; the five `DeviceUuid = _uuid` emits → `_udn`; `OnDeviceRemoved(Guid uuid)` → `(string udn)` with the match → `string.Equals(udn, _udn, StringComparison.OrdinalIgnoreCase)`.
  - [x] `SubscriptionPopupViewModel.cs`: `Guid _uuid` → `string _udn`; `_uuid = parentEntry.Uuid` → `_udn = parentEntry.Udn`; `OnDeviceRemoved(Guid uuid)` → `(string udn)` with the `OrdinalIgnoreCase` match.
- [x] **Task 9 — SubscriptionClient: identity emits only (DO NOT TOUCH `_pending`).** (AC: #1, #7)
  - [x] `SubscriptionClient.cs`: the `Subscription` inner class field `Guid _deviceUuid` → `string _deviceUdn`; ctor param + the call `new Subscription(this, eventSubUrl, parentEntry.Uuid, …)` → `parentEntry.Udn`; every `DeviceUuid = parentEntry.Uuid` / `DeviceUuid = _deviceUuid` emit → the string.
  - [x] **DO NOT** change `_pending` (`ConcurrentDictionary<Guid, Subscription>`), `PendingId` (`Guid.NewGuid()`), or `DrainPendingBuffer`/`BufferPending`. These key on a correlation id, NOT device identity (AC #7).
- [x] **Task 10 — SSDP log: real UDN.** (AC: #4)
  - [x] `SsdpLogEntry.cs`: record member `Guid Uuid` → `string Udn`; `UuidText => Uuid.ToString()` → `UdnText => Udn` (or keep a passthrough). Update the XML-doc ("UUID as a string" → "UDN string").
  - [x] `SsdpLogViewModel.cs:58`: `new SsdpLogEntry(DateTime.UtcNow, kind.Value, ann.Uuid ?? Guid.Empty)` → `new SsdpLogEntry(DateTime.UtcNow, kind.Value, ann.Udn ?? "")` (drop the all-zero fallback; an absent UDN renders empty). Update the inline comment.
  - [x] **App XAML (confirmed site):** `MainWindow.xaml:213` binds the SSDP log row `Text="{x:Bind UuidText}"` → update to `{x:Bind UdnText}` (the only App x:Bind on the renamed member). `PropertiesWindow.xaml:90` binds `ViewModel.Uuid` — NO change (the public `Uuid` display property name is retained; only its backing source becomes the UDN string).
- [x] **Task 11 — Build clean.** (AC: #9) Build Core + App + Tests; fix every consumer the compiler flags (the type change ripples mechanically). `-warnaserror` must be 0/0.
- [x] **Task 12 — Convert existing tests to string UDNs.** (AC: #6) See Dev Notes §"Test plan — conversions". Convert every device-identity `Guid.NewGuid()` / `Guid.Parse("…")` to a string UDN; convert the fakes. **Leave** `SubscriptionClientTests` / `EventCallbackHostTests` PendingId/correlation `Guid`s alone.
- [x] **Task 13 — Add the new regression tests.** (AC: #2, #3, #5, #6) See Dev Notes §"Test plan — new regression tests" (a)–(f).
- [x] **Task 14 — Architecture Amendment A30.** (AC: #8) Append A30 after A29; reword Decision 9 + the §4.1 component bullet. See Dev Notes §"Architecture amendment A30".
- [x] **Task 15 — Run the full suite + record results** (AC: #9). Capture pass/skip counts and the new-test deltas in the Dev Agent Record. NO manual UI smoke (pure Core — see §"Verification posture").

## Dev Notes

### Normalisation decisions (the real design content — pin these first)

1. **Stored identity = the FULL `uuid:<body>` string (NOT body-only).**
   - The USN is `uuid:<body>::<nt>` (NOTIFY/M-SEARCH) or just `uuid:<body>`. `ExtractUdn` returns `uuid:<body>` — it strips the `::<nt>` suffix but **keeps the `uuid:` prefix**.
   - Rationale: the device-description `<UDN>` element (`DeviceDescription.Udn`, an existing `string` carrying `"uuid:<body>"` per Amendment A10/A28) already has the `uuid:` prefix. Storing the full `uuid:<body>` makes `UdnMatches` a near-direct compare (strip `uuid:` from both sides defensively, then `OrdinalIgnoreCase`). It also means the diagnostics Identity-column fallback and the SSDP-log row render the real, prefixed UDN with no re-prefixing.
   - Preserve the original casing of the body in the stored string (do not lowercase) — comparison is case-insensitive, but display should show what the device sent.

2. **Comparison = `OrdinalIgnoreCase` everywhere.**
   - Registry dict: `new ConcurrentDictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)`.
   - `UdnMatches`: strip `uuid:` from both sides, `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
   - Popup FR-037 match: `string.Equals(udn, _udn, StringComparison.OrdinalIgnoreCase)`.
   - Rationale: matches the OLD `Guid`-equality semantics for hex UDNs (a `Guid` round-trips case-insensitively), and is spec-aligned (UDA matches UDNs case-insensitively for the hex form). AC #6 (RFC-4122 preservation) depends on this.

3. **Diagnostics Identity column (FR-041).** `DiagnosticContext.DeviceUuid` becomes `string?`. The fallback when there is no friendly name is the **UDN string itself** (which already carries `uuid:`). So `DiagnosticRingSink.ResolveIdentityLabel` changes `name ?? $"uuid:{uuid}"` → `name ?? uuid`. The three-way rule (null → "—"; hit-with-name → name; else → UDN) is unchanged in spirit (FR-041 / AC-8.3).

### Exhaustive change map (verified against shipped code 2026-06-04 — current → new)

Production (`src/ohSpy.Core/`):

| File | Current | New |
|---|---|---|
| `Discovery/SsdpParser.cs:80` | `internal static Guid? ExtractUuid(string? usn)` → `Guid.TryParse(s, out var g) ? g : null` | `internal static string? ExtractUdn(string? usn)` → return `uuid:<body>` (suffix stripped), null if no `uuid:` token; NO parse |
| `Discovery/SsdpParser.cs:75,77` | `var uuid = ExtractUuid(usn);` + ctor arg | `var udn = ExtractUdn(usn);` + ctor arg |
| `Discovery/SsdpAnnouncement.cs:13` | `Guid? Uuid` | `string? Udn` |
| `Discovery/DiscoveryService.cs:107,110` | `ann.Uuid.HasValue` / `OnByebye(ann.Uuid.Value)` | `!string.IsNullOrEmpty(ann.Udn)` / `OnByebye(ann.Udn!)` |
| `Discovery/DiscoveryService.cs:115,118` | `ann.Uuid.HasValue && …` / `OnAlive(ann.Uuid.Value, …)` | `!string.IsNullOrEmpty(ann.Udn) && …` / `OnAlive(ann.Udn!, …)` |
| `Devices/RegistryEntry.cs:20,72,74` | `Guid Uuid` / ctor `Guid uuid` / `Uuid = uuid` | `string Udn` / ctor `string udn` / `Udn = udn` |
| `Devices/DeviceRegistry.cs:26` | `ConcurrentDictionary<Guid, RegistryEntry> _entries = new()` | `… <string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)` |
| `Devices/DeviceRegistry.cs:30` | `event Action<Guid>? DeviceRemoved` | `Action<string>?` |
| `Devices/DeviceRegistry.cs:40,52,63-66,70,77,103-122` | `TryGetEntry(Guid)`, `OnAlive(Guid uuid,…)`, `new RegistryEntry(uuid,…)`, `OnByebye(Guid)`, `Remove(Guid)`, `Clear` keys, `RemoveCore(Guid)` | `string udn` throughout; `DeviceRemoved?.Invoke(udn)` |
| `Devices/IDeviceRegistry.cs:12,27` | `TryGetEntry(Guid …)`, `Action<Guid> DeviceRemoved` | `string …`, `Action<string>` |
| `Devices/EagerDescriptionDispatcher.cs:76,79-86,103-110,124-128` | `UdnMatches(description.Udn, entry.Uuid)`; helper `(string udn, Guid uuid)` w/ `Guid.TryParse`; two `DeviceUuid = entry.Uuid`; `Remove(entry.Uuid)` | `UdnMatches(description.Udn, entry.Udn)`; helper `(string descUdn, string registeredUdn)` strip-both-`uuid:`+`OrdinalIgnoreCase`; `DeviceUuid = entry.Udn`; `Remove(entry.Udn)` |
| `Diagnostics/DiagnosticContext.cs:9` | `Guid? DeviceUuid` | `string? DeviceUuid` |
| `Diagnostics/DiagnosticRingSink.cs:45-53` | `ctx.DeviceUuid is not { } uuid` / `name ?? $"uuid:{uuid}"` | local is `string` UDN; `name ?? uuid` |
| `Diagnostics/IDiagnosticIdentityLookup.cs:16` | `TryGetFriendlyName(Guid deviceUuid)` | `(string udn)` |
| `Diagnostics/RegistryIdentityLookup.cs:19` | `TryGetFriendlyName(Guid deviceUuid)` | `(string udn)` |
| `Diagnostics/NullIdentityLookup.cs:10` | `TryGetFriendlyName(Guid deviceUuid)` | `(string udn)` |
| `ViewModels/DeviceNodeViewModel.cs:36,44-47,61,80,86` | `Guid Uuid => _entry.Uuid`; `$"uuid:{entry.Uuid}"`; `new ServiceNodeViewModel(… _entry.Uuid …)`; `BrowserLaunch(… _entry.Uuid)` | `string Udn => _entry.Udn`; `entry.Udn`; `_entry.Udn`; `_entry.Udn` |
| `ViewModels/DeviceTreeViewModel.cs:14,21-23,37,52,60-61,95` | `IdentityKeyedSortedCollection<Guid,…>`; `vm => vm.Uuid`; `entry.Uuid`; `OnDeviceRemoved(Guid)`; `x.Uuid.ToString()` | `<string,…>`; `vm => vm.Udn`; `entry.Udn`; `(string udn)`; `x.Udn` |
| `ViewModels/ServiceNodeViewModel.cs:15,38,44,138,156` | `Guid _deviceUuid`; ctor `Guid deviceUuid`; `DeviceUuid = _deviceUuid`; `BrowserLaunch(… _deviceUuid)` | `string _deviceUdn`; ctor `string deviceUdn`; `_deviceUdn` |
| `ViewModels/BrowserLaunch.cs:21,28,44` | `OpenInDefaultBrowser(…, Guid deviceUuid)`; two `DeviceUuid = deviceUuid` | `(…, string deviceUdn)`; `deviceUdn` |
| `ViewModels/PropertiesViewModel.cs:22,85,97,134-151` | `Guid _uuid`; `_uuid = entry.Uuid`; `Uuid = entry.Uuid.ToString()`; `OnDeviceRemoved(Guid)` + `uuid != _uuid`; `BrowserLaunch(… _uuid)` | `string _udn`; `_udn = entry.Udn`; `Uuid = entry.Udn`; `(string udn)` + `OrdinalIgnoreCase`; `_udn` |
| `ViewModels/InvocationPopupViewModel.cs:41,89,(5×)DeviceUuid=_uuid,387-389` | `Guid _uuid`; `_uuid = parentEntry.Uuid`; `DeviceUuid = _uuid`; `OnDeviceRemoved(Guid)` | `string _udn`; `_udn = parentEntry.Udn`; `_udn`; `(string udn)` + `OrdinalIgnoreCase` |
| `ViewModels/SubscriptionPopupViewModel.cs:45,88,240-242` | `Guid _uuid`; `_uuid = parentEntry.Uuid`; `OnDeviceRemoved(Guid)` + `uuid != _uuid` | `string _udn`; `_udn = parentEntry.Udn`; `(string udn)` + `OrdinalIgnoreCase` |
| `Events/SubscriptionClient.cs:170,207,241,267,(emits)` | `new Subscription(… parentEntry.Uuid …)`; `Guid _deviceUuid`; `DeviceUuid = parentEntry.Uuid` / `_deviceUuid` | `parentEntry.Udn`; `string _deviceUdn`; the string |
| `Models/SsdpLogEntry.cs:13,19-20` | `Guid Uuid`; `UuidText => Uuid.ToString()` | `string Udn`; `UdnText => Udn` |
| `ViewModels/SsdpLogViewModel.cs:58` | `ann.Uuid ?? Guid.Empty` | `ann.Udn ?? ""` |

App (`src/ohSpy.App/`):
- `MainWindow.xaml:213` — SSDP log row `Text="{x:Bind UuidText}"` → `{x:Bind UdnText}` (CONFIRMED — the only App x:Bind on a renamed member).
- `Views/PropertiesWindow.xaml:90` binds `ViewModel.Uuid` — NO change (the public `Uuid` display property name is retained; only its source becomes the UDN string). CONFIRMED.

**Sites the proposal did NOT explicitly list but that the code requires (found during verification):**
- `DeviceTreeViewModel`'s `IdentityKeyedSortedCollection<Guid,…>` generic arg AND the `vm => vm.Uuid` selector AND **`DeviceNodeComparer.Compare`'s `x.Uuid.ToString()` tiebreak** (`DeviceTreeViewModel.cs:95`). The proposal's table named `DeviceTreeViewModel.OnDeviceRemoved` only.
- `IdentityKeyedSortedCollection<TIdentity,TItem>` itself is generic over `TIdentity` — **no change to the collection class**; only the type argument at the use site changes. Its tests are identity-agnostic (use `int`/`string` keys already) — confirm no change needed.
- `RegistryIdentityLookup.TryGetEntry` call now passes the string straight through — no normalisation needed there (the registry already keys `OrdinalIgnoreCase`).

### DO-NOT-TOUCH trap (AC #7)

`SubscriptionClient._pending` is `ConcurrentDictionary<Guid, Subscription>` keyed by `PendingId = Guid.NewGuid()` — a **per-subscribe correlation id** for the NOTIFY-before-SID race, NOT device identity. Leave `_pending`, `PendingId`, `BufferPending`, `DrainPendingBuffer` exactly as-is. Do NOT blanket-replace `Guid` in this file — only the `Subscription._deviceUuid` field + the `DeviceUuid =` emits change. Likewise `SubscriptionClientTests`/`EventCallbackHostTests` correlation `Guid`s stay `Guid`.

### Test plan — conversions (every device-identity Guid → string UDN)

Convert the device-identity `Guid.NewGuid()` / `Guid.Parse("…")` to a string UDN (e.g. `"uuid:f7dc20e5-1234-5678-abcd-ef0123456789"` for the GUID-cased case; `"uuid:4c494e4e-aaaa-...-linn"`-style or a clearly-non-hex `"uuid:linn-ds-akurate-0001"` for the regression case) in:

- `Discovery/SsdpParserTests.cs` — `TestUuid` → a UDN string; the `ExtractUuid_HandlesAllForms_AC241` theory becomes `ExtractUdn_…` returning the full `uuid:<body>` string (NOT a Guid); add the non-RFC-4122 inline case (see new tests). Assertions `ann.Uuid.Should().Be(TestUuid)` → `ann.Udn.Should().Be("uuid:…")`.
- `Discovery/DiscoveryServiceTests.cs` — `RootUuid`/`AnotherUuid` `Guid.Parse(…)` → UDN strings; any `new SsdpAnnouncement(… Uuid: …)` → `Udn:`; registry-mutation assertions key on the string.
- `Devices/DeviceRegistryTests.cs` — entry construction + `TryGetEntry`/`OnAlive`/`OnByebye`/`Remove`/`Clear`/`DeviceRemoved` assertions to strings.
- `Devices/RegistryEntryTests.cs` — `new RegistryEntry(Guid.NewGuid(), …)` → `new RegistryEntry("uuid:…", …)`; `entry.Uuid` assertions → `entry.Udn`.
- `Devices/EagerDescriptionDispatcherTests.cs` — entry construction; the mismatch/match cases (the description `<UDN>` vs registered UDN); `DeviceUuid` assertions.
- `ViewModels/DeviceTreeViewModelTests.cs` — entry construction; `Devices.TryGetItem`/`Remove` keys; `OnDeviceRemoved` raised with a string.
- `ViewModels/DeviceNodeViewModelTests.cs` — `vm.Uuid` → `vm.Udn`; the `uuid:` fallback friendly-name assertion.
- `ViewModels/ServiceNodeViewModelTests.cs` — ctor `deviceUuid` arg → string; `DeviceUuid` emit assertions.
- `ViewModels/ActionNodeViewModelTests.cs` — verify (likely entry-construction only).
- `ViewModels/PropertiesViewModelTests.cs` — entry; `RaiseDeviceRemoved(Guid)` → `(string)`; the matching + non-matching UDN banner cases; the displayed `Uuid` property.
- `ViewModels/InvocationPopupViewModelTests.cs` — entry; `DeviceUuid` emit assertions; the FR-037 `RaiseDeviceRemoved` match.
- `ViewModels/SubscriptionPopupViewModelTests.cs` — entry; FR-037 match.
- `ViewModels/ShellViewModelTests.cs` + `ViewModels/AdapterSwitchPopupCascadeTests.cs` — the Clear / cascade paths (`DeviceRemoved` per UDN); entry construction.
- `Models/SsdpLogEntryTests.cs` — `Uuid`/`UuidText` → `Udn`/`UdnText` (a string ctor arg).
- `ViewModels/SsdpLogViewModelTests.cs` — the announcement → log-entry projection; assert the real UDN, NOT `Guid.Empty`.
- `Diagnostics/DiagnosticRingSinkTests.cs` — `DeviceUuid = uuid` → a string UDN; **`IdentityLabel_RegistryMiss_ResolvesToUuidColonForm`'s assertion `$"uuid:{uuid}"` → the UDN string itself** (e.g. `"uuid:…"`); the `StaticIdentityLookup.TryGetFriendlyName(Guid)` test doubles → `(string)`.
- `Diagnostics/RegistryIdentityLookupTests.cs` — `TryGetFriendlyName(Guid)` → `(string)`; entry/registry keying.
- Fakes: `Fakes/FakeDeviceRegistry.cs` — `DeviceRemoved` `Action<Guid>` → `Action<string>`; `RaiseDeviceRemoved(Guid)` → `(string)`; `TryGetEntry(Guid)` → `(string)`. Any `RegistryEntry`-construction test helper (grep for `new RegistryEntry(` across tests) → string first arg.

**LEAVE alone:** `Events/SubscriptionClientTests.cs`, `Events/EventCallbackHostTests.cs` correlation/PendingId `Guid`s; `Collections/IdentityKeyedSortedCollectionTests.cs` (identity-agnostic — verify).

### Test plan — new regression tests (a)–(f)

- **(a) `SsdpParser.ExtractUdn` returns a non-RFC-4122 UDN verbatim.** Add inline cases to the (renamed) `ExtractUdn` theory: `("uuid:linn-ds-akurate-0001::upnp:rootdevice", "uuid:linn-ds-akurate-0001")` and `("uuid:4c494e4e-NOT-hex", "uuid:4c494e4e-NOT-hex")` → returns the full `uuid:<body>` string (NOT null, NOT a Guid). Keep the `(null, null)` and "no uuid: token" → null cases. This is THE regression.
- **(b) `UdnMatches` ordinal-ignore-case, both forms.** A GUID-cased UDN matches across case (`"uuid:F7DC…"` vs `"uuid:f7dc…"` → true) AND an opaque UDN matches itself / mismatches a different one (`"uuid:linn-ds-0001"` vs `"uuid:linn-ds-0001"` → true; vs `"uuid:linn-ds-0002"` → false). Include a prefix-asymmetry case (`"linn-ds-0001"` vs `"uuid:linn-ds-0001"` → true, since both sides strip `uuid:`).
- **(c) Registry round-trips + de-dups a non-GUID UDN.** `OnAlive("uuid:linn-ds-0001", …)` twice → one entry, `AliveCount == 2`, `TryGetEntry("uuid:linn-ds-0001", …)` true, `TryGetEntry("UUID:LINN-DS-0001", …)` also true (OrdinalIgnoreCase); `OnByebye` raises `DeviceRemoved("uuid:linn-ds-0001")`.
- **(d) End-to-end: a non-GUID device reaches `DeviceLoaded` via the dispatcher.** Drive `OnAlive` with a non-RFC-4122 UDN → fake HTTP returns a device-description whose `<UDN>` is the same non-RFC-4122 UDN → `UdnMatches` accepts → `DeviceLoaded` fires (the path that was broken). Mirror the existing dispatcher happy-path test with a non-GUID UDN.
- **(e) `DiscoveryService` routes a non-GUID-UDN alive into `OnAlive`.** A root-device `ssdp:alive` whose USN is `uuid:linn-ds-0001::upnp:rootdevice` → `registry.OnAlive` is invoked with `"uuid:linn-ds-0001"` (the old `HasValue` gate would have dropped it). Add the byebye twin.
- **(f) Popup FR-037 banner flips on a string-UDN `DeviceRemoved`.** For each popup VM (at least Properties + one of Invocation/Subscription): construct with a non-GUID UDN entry, raise `DeviceRemoved("uuid:LINN-DS-0001")` (different casing) → `IsDeviceGone`/`Status==DeviceGone` flips (OrdinalIgnoreCase match); raise `DeviceRemoved("uuid:other")` → no flip.

Use realistic non-GUID UDNs. Trait the new tests to the relevant AC where the suite uses `[Trait("ac", …)]` (e.g. `AC-2.4.1`, `AC-9.x`).

### Architecture amendment A30 (Task 14 — the story writes this)

Append to `…/architectures/arch-ohSpy-2026-05-31/architecture.md` after Amendment A29 (before `### Decision 13`):

```
### Amendment A30 — Device identity is the UDN string, not a parsed `Guid` (Decision 9 correction)

**Source:** Sprint Change Proposal 2026-06-04 (correct-course); surfaced by the Story 5.2 manual smoke on a live Linn network. Fixed in Story 2.10.

UPnP UDNs are opaque strings (`uuid:` + an identifier; UDA recommends but does not *require* RFC 4122). Devices in the wild — including Linn — use non-RFC-4122 UDNs. The original Decision 9 keyed the registry on a `Guid` parsed via `Guid.TryParse`, which silently drops every non-RFC-4122 device: `SsdpParser.ExtractUuid` parses → null → `DiscoveryService`'s `Uuid.HasValue` gate skips the announcement → no registry entry → no tree row; the SSDP log renders the all-zero `Guid.Empty`; `EagerDescriptionDispatcher.UdnMatches` re-parses the same way.

**Correction:** identity is the full normalised UDN **string** (`uuid:<body>`, the `::<nt>` suffix stripped, the `uuid:` prefix retained), compared `OrdinalIgnoreCase`. The registry is **UDN-keyed** (`ConcurrentDictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase)`). `DiagnosticContext.DeviceUuid` (now `string?`), `RegistryEntry.Udn`, `IDeviceRegistry.DeviceRemoved` (`Action<string>`), `IDiagnosticIdentityLookup.TryGetFriendlyName(string)`, the device-tree node identity, and the popup FR-037 banners all carry the string. The FR-041 Identity-column fallback is the UDN string itself (already prefixed `uuid:`). `Guid.TryParse` on a UDN is forbidden. `SubscriptionClient._pending`/`PendingId` stay `Guid` — they key a per-subscribe correlation id, not device identity. The `OrdinalIgnoreCase` comparer preserves the prior `Guid`-equality semantics for RFC-4122 (hex) UDNs, so existing devices are unaffected.

**Supersedes** Amendment A28's `UdnMatches(string udn, Guid uuid)` signature: the helper is now `UdnMatches(string descUdn, string registeredUdn)` (strip `uuid:` from both, `OrdinalIgnoreCase`) — no Guid parse.

**Applied to:** `SsdpParser`, `SsdpAnnouncement`, `DiscoveryService`, `DeviceRegistry`/`IDeviceRegistry`/`RegistryEntry`, `EagerDescriptionDispatcher`, `DiagnosticContext`/`DiagnosticRingSink`/`IDiagnosticIdentityLookup`/`RegistryIdentityLookup`/`NullIdentityLookup`, `DeviceNodeViewModel`/`DeviceTreeViewModel`/`ServiceNodeViewModel`/`BrowserLaunch`, `PropertiesViewModel`/`InvocationPopupViewModel`/`SubscriptionPopupViewModel`, `SsdpLogEntry`/`SsdpLogViewModel`, `SubscriptionClient` (identity emits only). Decision 9 + §4.1 component bullet reworded "UUID-keyed" → "UDN-keyed (string identity, OrdinalIgnoreCase)".
```

Then reword:
- `architecture.md:26` (§4.1 component bullet): "UUID-keyed registry" → "UDN-keyed registry (string identity, `OrdinalIgnoreCase`)".
- Decision 9 prose / sketch (`:1082`+, `:1099`): the `Guid Uuid` in the `RegistryEntry` sketch → `string Udn`; add the one-line "UDN-keyed (string identity, `OrdinalIgnoreCase`) — see Amendment A30. UPnP UDNs are opaque strings; the registry never parses them to `Guid`." Leave A28's body but add the supersession marker noting A30 changed the `UdnMatches` signature to string/string.

### Verification posture (AC #9)

**PURE CORE — NO manual UI smoke** (like Stories 3.1 / 4.1 / 4.2). The full Core suite + the new regression tests (a)–(f) are the gate. Behaviour MUST be preserved for RFC-4122 devices: the existing GUID-based tests, converted to string UDNs, must still pass. `CoreAppBoundaryTests` must still pass (no new `Core → App` edge; nothing here adds a WinUI dependency).

**After this lands, the Story 5.2 manual smoke resumes** — it will now detect the Linn device. That smoke belongs to Story 5.2 (held at `review`), NOT this story. Do not attempt it here.

Baseline at the time of writing: ~503–504 passed / 2 skipped (Story 5.2 run). Expect a small net increase from the new regression tests; the conversions are 1:1 (no test count change beyond the new ones).

### Project Structure Notes

- Core/App split holds: every changed production file is in `src/ohSpy.Core/`; the only App touch is a possible XAML x:Bind rename (Task 10). No DI-graph change, no new dependency.
- Naming convention: the codebase already uses "UDN" for the device-description side (`DeviceDescription.Udn`, `UdnMatches`) and "Uuid" for the SSDP/identity side. This story unifies on **UDN/`Udn`** for the identity (matching A30); keep the public `PropertiesViewModel.Uuid` *display* property name (it is what the Properties window binds — only its backing source changes) unless the XAML is updated in lockstep.

### References

- [Source: _bmad-output/planning-artifacts/sprint-change-proposal-2026-06-04.md] — full issue summary, §2 per-file table, §4 A30 text + Decision 9 reword, success criteria.
- [Source: architecture.md#Decision 9 — `DescriptionFetchState` Machine] (`:1082`) — the registry/entry contract being amended.
- [Source: architecture.md#Amendment A28] (`:2927`) — the existing `UdnMatches(string, Guid)` helper this story supersedes.
- [Source: architecture.md#Amendment A27] (`:2899`) — `RegistryEntry.DeviceCts` linked-token + dispose-on-removal (unchanged; the `RemoveCore` cascade is preserved).
- [Source: architecture.md §4.1 component bullet] (`:26`) — "UUID-keyed registry" prose reworded by A30.
- Verified shipped code: `SsdpParser.cs`, `SsdpAnnouncement.cs`, `DiscoveryService.cs`, `DeviceRegistry.cs`, `IDeviceRegistry.cs`, `RegistryEntry.cs`, `EagerDescriptionDispatcher.cs`, `DiagnosticContext.cs`, `DiagnosticRingSink.cs`, `IDiagnosticIdentityLookup.cs`, `RegistryIdentityLookup.cs`, `NullIdentityLookup.cs`, `DeviceNodeViewModel.cs`, `DeviceTreeViewModel.cs`, `ServiceNodeViewModel.cs`, `ActionNodeViewModel.cs`, `BrowserLaunch.cs`, `PropertiesViewModel.cs`, `InvocationPopupViewModel.cs`, `SubscriptionPopupViewModel.cs`, `SubscriptionClient.cs`, `SsdpLogEntry.cs`, `SsdpLogViewModel.cs`, `IdentityKeyedSortedCollection.cs`, `FakeDeviceRegistry.cs`, `SsdpParserTests.cs`, `DiagnosticRingSinkTests.cs` (all read 2026-06-04).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m]

### Debug Log References

### Completion Notes List

- Ultimate context engine analysis completed — comprehensive developer guide created. Every site in the Sprint Change Proposal §2 table verified against shipped code; three sites the proposal did not name explicitly are documented in §"Exhaustive change map" (the `IdentityKeyedSortedCollection<Guid,…>` type arg + `vm => vm.Uuid` selector + `DeviceNodeComparer` tiebreak in `DeviceTreeViewModel`).

**Task 0 — pinned normalisation (confirmed before coding):** (a) The stored identity is the **FULL `uuid:<body>`** string (NOT body-only) — `ExtractUdn` keeps the `uuid:` prefix and strips only the `::<nt>` suffix, preserving body casing. This matches `DeviceDescription.Udn` so `UdnMatches` is a near-direct compare and the SSDP-log / FR-041 fallback render the prefixed UDN with no re-prefixing. (b) All comparison is `OrdinalIgnoreCase` — the registry dict comparer, `UdnMatches` (strip `uuid:` both sides), and the three popup FR-037 matches — which preserves the prior `Guid`-equality semantics for hex UDNs (AC #6).

**Implementation summary (Amendment A30):**
- Identity is now the UDN `string` end-to-end. `SsdpParser.ExtractUuid → ExtractUdn` returns `uuid:<body>` verbatim (NO `Guid.TryParse`), null only when there is no `uuid:` token. `SsdpAnnouncement.Uuid → Udn` (`string?`). `DiscoveryService` route gates changed to `!string.IsNullOrEmpty(ann.Udn)`.
- `RegistryEntry.Uuid → Udn` (`string`); `DeviceRegistry` backing dict is `ConcurrentDictionary<string,RegistryEntry>(StringComparer.OrdinalIgnoreCase)`; `IDeviceRegistry.DeviceRemoved` is `Action<string>`; `TryGetEntry(string)`. `EagerDescriptionDispatcher.UdnMatches(string descUdn, string registeredUdn)` strips `uuid:` from both then `OrdinalIgnoreCase`.
- Diagnostics: `DiagnosticContext.DeviceUuid` is `string?` (property name kept to minimise emit-site churn); `DiagnosticRingSink` fallback is `name ?? udn`; `IDiagnosticIdentityLookup.TryGetFriendlyName(string)` (+ Registry/Null impls).
- VMs: `DeviceNodeViewModel.Uuid → Udn`; **all three flagged `DeviceTreeViewModel` sites changed** — the `IdentityKeyedSortedCollection<string,…>` type arg, the `vm => vm.Udn` selector, AND `DeviceNodeComparer.Compare`'s tiebreak (`x.Udn`, dropped `.ToString()`). `ServiceNodeViewModel`/`BrowserLaunch` ctor args → `string deviceUdn`. The three popup VMs `_uuid → _udn`; FR-037 match is `string.Equals(udn, _udn, OrdinalIgnoreCase)`.
- `SsdpLogEntry.Uuid/UuidText → Udn/UdnText`; `SsdpLogViewModel:58` drops `?? Guid.Empty` → `ann.Udn ?? ""`.
- `SubscriptionClient`: only the `Subscription._deviceUuid → _deviceUdn` field + the `DeviceUuid =` emits changed. **`_pending` (`ConcurrentDictionary<Guid,Subscription>`), `PendingId = Guid.NewGuid()`, `BufferPending`/`DrainPendingBuffer` LEFT AS `Guid`** (correlation id, NOT identity — AC #7 verified).

**Open-question resolutions:**
- `SsdpLogEntry.UuidText → UdnText` (the story's recommendation) → required the one App XAML touch: `MainWindow.xaml:213` `{x:Bind UuidText}` → `{x:Bind UdnText}`.
- `PropertiesViewModel.Uuid` **display property name retained** (only its backing source becomes `entry.Udn`) → `PropertiesWindow.xaml:90` `{x:Bind ViewModel.Uuid}` UNTOUCHED.

**New regression tests (a)–(f), all green:**
- (a) `SsdpParserTests.ExtractUdn_HandlesAllForms_AC241` — non-RFC-4122 UDN returned verbatim (`uuid:linn-ds-akurate-0001`, `uuid:4c494e4e-NOT-hex`); null only when no `uuid:` token.
- (b) `EagerDescriptionDispatcherTests.UdnMatches_OrdinalIgnoreCase_PrefixStripped_AC96` — GUID-cased cross-case, opaque self/mismatch, `uuid:`-prefix asymmetry.
- (c) `DeviceRegistryTests.Registry_RoundTripsAndDeDups_NonGuidUdn_OrdinalIgnoreCase` — de-dup + `OrdinalIgnoreCase` lookup + byebye DeviceRemoved.
- (d) `EagerDescriptionDispatcherTests.Fetch_NonGuidUdn_ReachesDeviceLoaded_ViaDispatcher` — **the broken path**: a non-GUID device now reaches `DeviceLoaded`.
- (e) `DiscoveryServiceTests.StartAsync_Alive_NonGuidUdn_RoutesIntoOnAlive` (+ byebye twin) — the old `HasValue` gate would have dropped it.
- (f) `PropertiesViewModelTests.DeviceRemoved_DifferentCasedNonGuidUdn_FlipsBanner_OrdinalIgnoreCase` + `SubscriptionPopupViewModelTests.DeviceRemoved_DifferentCasedUdn_FlipsToDeviceGone_OrdinalIgnoreCase`.

**Build/test results observed:**
- `dotnet build src/ohSpy.Core -warnaserror`: **0 Warning(s) / 0 Error(s)**.
- `dotnet build src/ohSpy.App -t:Rebuild`: **1 Warning (pre-existing WMC1506 at MainWindow.xaml:156, unrelated) / 0 Error(s)**; the `UdnText` x:Bind compiles.
- `dotnet test`: **515 passed / 2 skipped / 0 failed** (baseline 504/2 → +11 from the new regression cases + added inline theory rows; conversions stayed 1:1). The 2 skips are the intentional `[Fact(Skip=…)]` `AsyncDisciplineTests` + `DiagCategoriesUsageTests` (baseline skips). `CoreAppBoundaryTests` + Chaos green (chaos = 1). No new `DiagCategories` constant.
- `SubscriptionClientTests`/`EventCallbackHostTests` correlation Guids left alone; `IdentityKeyedSortedCollectionTests` untouched (identity-agnostic).

**Architecture:** Amendment A30 appended after A29; Decision 9 prose + `RegistryEntry` sketch reworded (`Guid Uuid → string Udn`); §4.1 component bullet "UUID-keyed" → "UDN-keyed (string identity, OrdinalIgnoreCase)"; A28 marked PARTIALLY SUPERSEDED (the `UdnMatches(string, Guid)` signature).

**Follow-up for reviewer:** No manual UI smoke performed here (pure Core defect correction, per the story's verification posture). The Story 5.2 manual smoke resumes after this lands and should now detect the Linn device — that smoke belongs to 5.2, not this story.

### Review Findings

- [x] [Review][Patch] Stale comment says `parentEntry.Uuid` / "UUID-bearing emit" [`src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs:202–204`] — **Applied** by reviewer: updated to `parentEntry.Udn` / "UDN-bearing emit". Build verified 0/0 after patch.

**Verdict: APPROVED-WITH-MINOR-FIXES** (1 patch applied, 6 findings dismissed as noise). Build: Core -warnaserror 0/0; App 0 errors (WMC1506 pre-existing); 515 passed / 2 skipped across 2 independent runs. No residual device-identity `Guid` found anywhere in `src/` (grep confirmed). `SubscriptionClient._pending` correctly left as `ConcurrentDictionary<Guid, Subscription>`. `ExtractUdn` verified across all USN forms. `UdnMatches` OrdinalIgnoreCase + de-dup confirmed. No double `uuid:` prefix in diagnostics. No weakened test conversions.

### File List

Production (`src/ohSpy.Core/`):
- `Discovery/SsdpParser.cs`
- `Discovery/SsdpAnnouncement.cs`
- `Discovery/DiscoveryService.cs`
- `Devices/RegistryEntry.cs`
- `Devices/DeviceRegistry.cs`
- `Devices/IDeviceRegistry.cs`
- `Devices/EagerDescriptionDispatcher.cs`
- `Diagnostics/DiagnosticContext.cs`
- `Diagnostics/DiagnosticRingSink.cs`
- `Diagnostics/IDiagnosticIdentityLookup.cs`
- `Diagnostics/RegistryIdentityLookup.cs`
- `Diagnostics/NullIdentityLookup.cs`
- `ViewModels/DeviceNodeViewModel.cs`
- `ViewModels/DeviceTreeViewModel.cs`
- `ViewModels/ServiceNodeViewModel.cs`
- `ViewModels/BrowserLaunch.cs`
- `ViewModels/PropertiesViewModel.cs`
- `ViewModels/InvocationPopupViewModel.cs`
- `ViewModels/SubscriptionPopupViewModel.cs`
- `Events/SubscriptionClient.cs` (identity emits only — `_pending`/`PendingId` left as `Guid`)
- `Models/SsdpLogEntry.cs`
- `ViewModels/SsdpLogViewModel.cs`

App (`src/ohSpy.App/`):
- `MainWindow.xaml` (line 213: `{x:Bind UuidText}` → `{x:Bind UdnText}`)

Architecture:
- `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (Amendment A30 added; Decision 9 + §4.1 reworded; A28 supersession marker)

Tests (`tests/ohSpy.Core.Tests/`):
- `Fakes/FakeDeviceRegistry.cs`
- `Fakes/SsdpDatagramBuilder.cs`
- `Discovery/SsdpParserTests.cs` (+ regression (a))
- `Discovery/SsdpAnnouncementTests.cs`
- `Discovery/DiscoveryServiceTests.cs` (+ regression (e))
- `Devices/RegistryEntryTests.cs`
- `Devices/DeviceRegistryTests.cs` (+ regression (c))
- `Devices/EagerDescriptionDispatcherTests.cs` (+ regressions (b), (d))
- `Diagnostics/DiagnosticRingSinkTests.cs`
- `Diagnostics/RegistryIdentityLookupTests.cs`
- `Events/SubscriptionClientTests.cs` (device-identity entry only — correlation Guids untouched)
- `Models/SsdpLogEntryTests.cs`
- `ViewModels/DeviceTreeViewModelTests.cs`
- `ViewModels/DeviceNodeViewModelTests.cs`
- `ViewModels/ServiceNodeViewModelTests.cs`
- `ViewModels/ActionNodeViewModelTests.cs`
- `ViewModels/BrowserLaunchTests.cs`
- `ViewModels/PropertiesViewModelTests.cs` (+ regression (f))
- `ViewModels/InvocationPopupViewModelTests.cs`
- `ViewModels/SubscriptionPopupViewModelTests.cs` (+ regression (f))
- `ViewModels/ShellViewModelTests.cs`
- `ViewModels/AdapterSwitchPopupCascadeTests.cs`
- `ViewModels/SsdpLogViewModelTests.cs`
