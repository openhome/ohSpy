---
title: "PRD: ohSpy"
status: final
created: 2026-05-30
updated: 2026-06-01
---

# PRD: ohSpy

## 0. Document Purpose

This PRD specifies the behaviour of **ohSpy** — a native Windows desktop UPnP inspector for Linn software engineers, intended to fill the gap left by Intel's discontinued Device Spy. It is the second artifact in a BMad workflow chain: it builds on `briefs/brief-ohSpy-2026-05-29/brief.md` and feeds the architecture document, epic/story breakdown, and sprint plan that follow.

The functional scope is **parity with the prior implementation at `C:\work\UpnpSpy`** (Claude + spec-kit) plus a small, named set of deliberate fixes for performance complaints in that prior tool, **plus two deliberate parity-plus additions agreed during PRD review** (FR-102 `<allowedValueList>` and FR-103 `<allowedValueRange>` — see §4.9 and the decision log). FR IDs are lifted from `UpnpSpy/specs/001-upnp-spy-discovery/spec.md` and **preserved verbatim** so cross-reference between the two specs is direct; ohSpy-specific additions appear as **FR-100+** to avoid collision.

Technical specifics (UI framework, UPnP transport choice, threading model, packaging) are **architecture** and live in `briefs/brief-ohSpy-2026-05-29/addendum.md` for ratification in the `bmad-create-architecture` phase that follows this PRD.

---

## 1. Vision

ohSpy is the supported successor to Intel's Device Spy: a single native Windows desktop window that shows you every UPnP device on your network, lets you walk its services and actions, invoke any action interactively, and subscribe to GENA events live — all without leaving the app. It is built for Linn software engineers and behaves like one of them: terse, dense, fast, no tutorials, no hand-holding. When a device is slow or broken, ohSpy stays responsive and tells you why through a diagnostic stream you can pull up in one menu click.

The UPnP debugging surface at Linn degrades to Wireshark plus hand-typed SOAP envelopes whenever Device Spy fails to run on a modern Windows machine. ohSpy is the first tool in that surface that is actively maintained, owned by a team that uses it daily, and built to fit cleanly into a Linn developer's existing Windows workflow.

It is also a deliberate, walk-through demonstration of spec-driven AI development with Claude Code + BMad — the brief, this PRD, the architecture document, the stories, and the sprint plan are the script for a Linn engineering lunch & learn. The product and the process are both deliverables.

The vision is **modest and grounded**: ohSpy is not a product with a roadmap. It is a tool that should keep working. If it lands, it becomes the supported internal UPnP inspector at Linn (filling the gap left by Intel Device Spy); open-source release becomes a credible option if Linn wants a public footprint in UPnP dev tools; and the L&L outcome may matter more than the tool itself — if peers adopt Claude + BMad spec-driven development on their own projects, that is the larger win.

### 1.1 Risk and Fallback

The PRD presumes ohSpy will be visibly better than UpnpSpy and that walking through the BMad process will land with the audience. It does not depend on either:

- **If the BMad result is no better than UpnpSpy** — no L&L. ohSpy is still useful as a supported internal UPnP inspector; worth doing on its own.
- **If the L&L happens and lands flat** — no harm done. The tool persists, the spec artifacts persist, the next person to try the methodology has both as reference.

## 2. Target User

### 2.1 Jobs To Be Done

- **Diagnose a UPnP device's behaviour during development.** "I changed the streamer firmware — does the new service show up, does the action work, do my evented properties fire?" — answered in one window without spinning up Wireshark.
- **Discover what's actually on this network right now.** Especially during integration: "I plugged in three things, are they all advertising? Is anything else here I didn't expect?"
- **Walk an unfamiliar device's surface.** Third-party DLNA media renderer, an IGD router, a smart-home gateway: "Show me its services, show me its actions, let me poke them."
- **Watch a service's events live to verify state changes.** Volume control, transport state, queue updates: subscribe, see the property updates as they arrive, confirm what the device is actually emitting.
- **Get a diagnostic trail when something doesn't behave.** Failed description fetch, malformed NOTIFY, SOAP fault on an action — recorded with timestamp, severity, and enough context to understand without re-running.
- **Demonstrate, for Linn peers, what spec-driven AI development looks like end-to-end.** The build, the artifacts, the methodology — visible as a single coherent walkthrough.

### 2.2 Non-Users (v1)

- **Consumers** — this is a developer tool. No setup wizard, no friendly-error-message work, no localisation, no theming.
- **Non-Linn engineers** at v1 — distribution is internal only (unsigned installer); public/OSS release is a deferred decision.
- **Users on macOS or Linux** — Windows-only by deliberate scope.
- **Users with a11y requirements** — acknowledged out-of-scope for v1; revisit if distribution widens.

### 2.3 Operator Narrative

> **A Linn developer launches ohSpy, picks the right network adapter, sees the devices on that network populate within a few seconds, expands the streamer they're working on, drills into its services and actions, invokes an action and reads the result (or the fault), opens a subscription on another service to watch events flow in, and uses the diagnostic viewer when something didn't behave as expected.**
>
> **Then they close the window. Next session, the tool launches clean — no carried state — and the same flow runs again.**

---

## 3. Glossary

Downstream artefacts (architecture, stories) must use these terms exactly. The L&L audience is not uniformly UPnP-literate, so domain terms are defined here even where the primary user knows them.

- **UPnP** — Universal Plug and Play. The device-and-control protocol stack governed by UDA 1.0 (`docs/specs/UPnP-arch-DeviceArchitecture-v1.0-20080424.pdf` in the prior-art repo). All protocol references in this PRD trace to UDA 1.0 section numbers.
- **SSDP** — Simple Service Discovery Protocol. The HTTPMU/HTTPU discovery layer (UDA 1.0 §1). Multicast group `239.255.255.250:1900` over IPv4.
- **M-SEARCH** — Active SSDP search request issued by control points (UDA 1.0 §1.2.2).
- **NOTIFY** — Unsolicited SSDP advertisement issued by devices. `NTS: ssdp:alive` (UDA 1.0 §1.1.2) on joining or refresh; `NTS: ssdp:byebye` (UDA 1.0 §1.1.3) on graceful leave.
- **GENA** — General Event Notification Architecture (UDA 1.0 §4). The eventing layer; uses `SUBSCRIBE`, `UNSUBSCRIBE`, and `NOTIFY` HTTP verbs.
- **SCPD** — Service Control Protocol Description. The XML document at a service's `<SCPDURL>` listing its actions and state variables. `<SCPDURL>` appears in the device description (UDA 1.0 §2.1); the SCPD body itself (`<actionList>`, `<serviceStateTable>`) is defined in UDA 1.0 §2.3.
- **Device** — A root UPnP device, uniquely identified by UUID in the `USN`/`UDN` headers. Embedded devices are flattened into their root (FR-053).
- **Service** — A UPnP service exposed by a device, with a SOAP `controlURL`, an event `eventSubURL`, and an SCPD.
- **Action** — A SOAP-invocable operation on a service, with declared input and output argument lists.
- **Eligible adapter** — An IPv4 network interface that is operational, non-loopback, and multicast-capable. ohSpy operates on exactly one eligible adapter at a time (FR-048).
- **Registry** — The internal collection of discovered devices keyed by UUID. Membership in the registry is **not** the same as membership in the visible device tree — failed-fetch devices remain registered but hidden (FR-047, FR-053).
- **Eager description fetch** — The asynchronous fetch of a device's description XML triggered on discovery (FR-043), not on user expansion.
- **DescriptionFetchState** — The state of a registry entry's eager description fetch (FR-043, FR-047). One of: **Pending** (entered registry, fetch not yet started), **InFlight** (HTTP request issued, response not yet processed), **Loaded** (description fetched and parsed successfully — the only state in which the device appears in the visible tree), **Failed** (fetch or parse failed terminally). Membership in the visible tree is gated on `Loaded`; registry membership is independent.
- **Callback host** — The in-process HTTP endpoint that receives GENA `NOTIFY` requests from subscribed devices. Bound to the eligible adapter's IPv4 address on an ephemeral TCP port using raw sockets (FR-049).
- **Diagnostic entry** — A structured record (timestamp, severity, category, message, context) emitted on internal errors and notable events. Sinks: rolling log file and bounded in-memory ring (FR-039–FR-042).

---

## 4. Features

FR IDs FR-001..FR-055 are lifted verbatim from `UpnpSpy/specs/001-upnp-spy-discovery/spec.md` (filler trimmed; testable behaviour unchanged). FR-100+ are ohSpy-specific — the named fixes for prior-art complaints.

### 4.1 Discovery & Device Registry

**Description.** On launch, ohSpy issues an SSDP active-search on the user-selected eligible adapter and begins listening for unsolicited SSDP NOTIFY messages for the rest of the session. Every distinct **root** UPnP device (identified by UUID) that responds becomes a candidate registry entry; once its description XML is fetched (the **eager description fetch** path), it appears in the visible device tree. The registry is the system-of-record; the visible tree is a filtered, sorted projection of it. Devices announcing `ssdp:byebye` (or failing a rescan) are removed from both. Embedded child devices and bare services are deliberately **not** registered as top-level entries — this is enforced at three layers (search target, NOTIFY filter, and a fetch-time backstop).

**Functional Requirements**

#### FR-004: Active SSDP discovery on startup

On startup, ohSpy issues an SSDP M-SEARCH (UDA 1.0 §1.2.2) with `ST: upnp:rootdevice` (UDA 1.0 §1.2.3) on the user-selected eligible adapter (FR-048); default at launch is the first eligible adapter enumerated.

**Consequences (testable):**
- On startup, ohSpy joins the SSDP multicast group `239.255.255.250:1900` on the selected adapter before issuing the M-SEARCH, so the same socket also receives unsolicited NOTIFY messages (FR-006).
- Search target is `upnp:rootdevice`, not `ssdp:all`, so each root device responds exactly once.
- M-SEARCH is sent on exactly one IPv4 adapter at a time.
- On adapter switch (FR-050), the SSDP socket is torn down and rebuilt on the newly-selected adapter.

#### FR-005: Tree entry per responding root device

For every distinct root device that responds to the active-discovery request, ohSpy creates a registry entry; once eager description fetch succeeds (FR-043), the device appears as a top-level tree row (FR-047).

#### FR-006: Continuous unsolicited-advertisement listening

For the entire application runtime, ohSpy listens for unsolicited SSDP NOTIFY messages (UDA 1.0 §1.1) and creates registry entries for newly-announced root devices.

#### FR-007: UUID-keyed device identity

Devices are uniquely identified by the UUID in the `USN` header (defined in the NOTIFY header table, UDA 1.0 §1.1.2; matches the `<UDN>` of the device description, UDA 1.0 §2.1). Further advertisements for an already-known UUID MUST NOT create additional registry entries (or, by extension, additional tree rows).

#### FR-008: Removal on graceful leave

On receiving an SSDP NOTIFY with `NTS: ssdp:byebye` (UDA 1.0 §1.1.3) for a known device, ohSpy removes that device from the registry and (if visible) from the tree.

#### FR-056: Removal on expiry (inferred byebye)

A registered device whose latest `ssdp:alive` promised a `CACHE-CONTROL: max-age` lease MUST be removed from the registry (and tree) when that lease lapses without a refreshing `alive` — i.e. when `now > LastSeenUtc + max-age` — even though no `ssdp:byebye` was received (UDA 1.0 §1.2.2: a device re-advertises before its `max-age` expires; absence implies it has left). Removal uses the same path as FR-008 (byebye): the device leaves the registry + tree, open popups receive the FR-037 "device no longer reachable" treatment, and any in-flight description/SCPD fetch is cancelled.
- **Grace:** eviction occurs at `LastSeenUtc + max-age` plus a small fixed jitter tolerance (`~5 s`) for network/routing latency and clock skew (a device promises to re-advertise within that window; UDA recommends `< ½ max-age`, but eviction at `1× max-age` is the conservative, spec-faithful control-point bound).
- **Missing `CACHE-CONTROL`:** when an `alive` omits `max-age` (non-conformant but seen in the wild), a default lease of `1800 s` (the UDA 1.0 §1.2.2 example) applies so the device still expires rather than living forever.
- **Diagnostic:** an expiry emits a distinct `Ssdp.Expired` diagnostic (Information) carrying the device UDN, so the FR-041 Diagnostics viewer shows *why* a device left.
- The check is periodic and MUST NOT block the SSDP read loop, the GENA listener, or the UI thread; the eviction is marshalled onto the UI thread (the registry is UI-thread-owned).

#### FR-053: Root-only registration with three-layer enforcement

The registry contains **only root UPnP devices**. Embedded children and standalone services MUST NOT appear as separate registry entries. Services declared by embedded children flatten into their root device's `<serviceList>`. Enforcement:
- (a) M-SEARCH `ST` is `upnp:rootdevice` (FR-004, FR-022) — embedded children do not respond.
- (b) NOTIFY `ssdp:alive` / `ssdp:byebye` only creates or removes a registry entry when `NT` is exactly `upnp:rootdevice`; other NT values still append to the SSDP log (FR-014, FR-015) but do not affect the registry.
- (c) Mismatched-root backstop — see FR-043.

#### FR-054: Case-insensitive alphabetical tree ordering with stable secondary key

Device rows in the left-pane tree are ordered case-insensitively by friendly name (FR-009, with the FR-010 `uuid:<uuid>` fallback). When two devices share a label, ordinal UUID comparison breaks the tie. The sort applies at:
- Initial seeding from registry snapshot.
- `DeviceAdded` for devices entering already-Loaded.
- `DeviceUpdated` promotion when eager fetch lands after the first announcement.

When a re-announcement causes the label to change such that the row migrates to a new position, the underlying node identity (selection/expansion state) MUST be preserved across the migration.

**Consequences (testable):**
- Sort-induced row migration MUST be implemented as an in-place move on the same node instance, NOT as remove+insert; a label change MUST NOT cause the row's expanded subtree (services, actions) to redraw, collapse, or lose scroll/selection state.
- Tree updates triggered by FR-005 / FR-008 / FR-054 MUST NOT invalidate or redraw sibling subtrees that are unaffected by the change.

**Feature-specific NFRs:**

- Discovery and runtime listening MUST coexist: a rescan (FR-021) MUST NOT suspend unsolicited-advertisement handling (FR-024).
- Multicast blockage MUST NOT crash the app or block startup; tree is simply empty (operator narrative continues), with a Warning diagnostic.

---

### 4.2 Eager Device-Description Fetch and Tree Visibility

**Description.** Description XML is fetched eagerly, without user interaction; devices remain hidden from the tree until the fetch succeeds. Concurrent fetches are capped.

**Functional Requirements**

#### FR-043: Asynchronous, bounded eager description fetch

Whenever a new device is added to the registry as the result of SSDP discovery (FR-005, FR-006), ohSpy MUST asynchronously fetch the device's description (UDA 1.0 §2.1) from its `LOCATION` URL without waiting for user interaction.

**Consequences (testable):**
- On success: ohSpy populates the friendly name and parsed service list, then admits the device to the tree (FR-047).
- On failure: the device is **not** admitted to the tree (FR-047); a Warning `DiagnosticEntry` (FR-039) is recorded with UUID, URL, status code, and error text.
- Eager fetches run with bounded parallelism (target: 8 concurrent — see Performance Budgets).
- Subsequent advertisements for an already-known UUID MUST NOT trigger re-fetch; the description is cached for the lifetime of the registry entry.
- A fresh registry entry created after byebye or rescan-prune MUST fetch again.
- A pending or in-flight fetch MUST be cancelled if the device leaves the registry before the fetch completes.
- **Mismatched-root backstop:** if the fetched root `<UDN>` does not match the requesting UUID, ohSpy MUST NOT write the description's friendly name or service list onto the requesting device, MUST remove the requesting UUID from the registry, and MUST record an `Information` `Description.Fetch` diagnostic carrying `device.uuid`, `url`, and `declared.root.uuid`.

#### FR-047: Hide-until-loaded tree visibility

A device MUST appear in the left-pane tree if and only if `DescriptionFetchState == Loaded`. Devices in `Pending`, `InFlight`, or `Failed` states MUST NOT appear in the tree.

**Consequences (testable):**
- The user never sees a transient placeholder label for a device row.
- A device entry remains in the underlying registry while a fetch is in flight (so the dispatcher and byebye handler can address it by UUID).
- A device MAY remain in the registry after a failed fetch — registry membership is not the same as tree visibility.
- Every fetch failure MUST produce a Warning diagnostic (FR-039) so the operator can identify, via `View → Diagnostics` (FR-041), every device whose description could not be retrieved and why.

---

### 4.3 Device Tree Display

**Description.** The left pane is a tree: devices at the top, their services as children, actions as grandchildren. Visually-similar devices are made distinguishable at a glance; expand chevrons are visible immediately even while children are still being fetched.

**Functional Requirements**

#### FR-001: Two-pane layout

The main window presents two side-by-side panes: the device tree on the left, the SSDP message log on the right.

#### FR-002: Tree shape — device → service → action

The left pane presents discovered devices as the top level of the tree, their services as children of each device, and the actions of each service as grandchildren.

#### FR-009: Friendly-name labels

Each device row is labelled with the device's friendly name (from the device description). A device MUST NOT appear in the tree until its description has been successfully fetched (FR-047), so the operator never sees a transient placeholder label.

#### FR-010: Friendly-name fallback for descriptions without one

When the fetched description has no `<friendlyName>` element, ohSpy displays a fallback label that uniquely identifies the entry: `uuid:<uuid>`. Devices whose description fetch failed entirely do NOT use this fallback — they are hidden from the tree (FR-047).

#### FR-011: Service enumeration on device expansion

When the user expands a device node, ohSpy displays every service listed in the device description's `<serviceList>` (UDA 1.0 §2.1), together with the services declared by any embedded children (recursively flattened per FR-053), as child nodes. The description has already been fetched eagerly (FR-043); the expansion MUST NOT trigger a second HTTP fetch.

**Consequences (testable):**
- If the description fetch is still in flight at expansion time, the node shows the transient "Loading…" placeholder (FR-044).
- If the description fetch failed, the device is not visible in the first place (FR-047); the FR-013 inline error placeholder is the fallback if the fetch fails after the node is shown.

#### FR-013: Inline error placeholder on enumeration failure

When ohSpy cannot retrieve or parse a description needed to populate a node's children, the failure is surfaced inline (in or near the affected node) without crashing the app or affecting sibling nodes.

#### FR-044: Persistent expand chevron via "Loading…" placeholder

Every tree node whose children are populated lazily or asynchronously (device nodes and service nodes) MUST carry at least one child item — a transient "Loading…" placeholder — from the moment the node is added, so the WinUI tree control renders the expand chevron without waiting for the user's first click.

**Consequences (testable):**
- The placeholder is visually distinguishable from real children (literal text "Loading…").
- The placeholder is replaced atomically by real children when the fetch completes; on failure, replaced by the FR-013 inline error placeholder.
- Action nodes — which have no children by design — MUST NOT carry a placeholder and MUST NOT show an expand chevron.

#### FR-045: Kind glyphs in front of node labels

Every tree row displays a small glyph in front of the node label identifying its kind (device / service / action). Glyphs are drawn from a font already shipped by Windows (no external icon assets) and must be visually distinct enough that the operator can distinguish kinds without reading the label.

#### FR-051: Device row secondary detail line

Every device row in the left-pane tree MUST display, beneath the friendly name, a muted secondary line containing:
- (a) The tail of the device's `<deviceType>` URN (the segment after `:device:`, e.g. `InternetGatewayDevice`).
- (b) The IPv4 host and port extracted from the device's `LOCATION` URL.

Separated by a middle-dot. Styled with a secondary foreground brush so the friendly name remains the visual focus. Drawn from fields populated by the eager description fetch (FR-043); never empty for a visible device (FR-047).

**Feature-specific NFRs:**

- Tree updates on incremental changes (device added, removed, label updated, child node fetched) MUST NOT cause full-tree or full-subtree repaints. Identity-tracked, keyed collection updates are required (see Cross-Cutting NFRs §5).

---

### 4.4 Service & Action Enumeration (Lazy SCPD)

**Description.** Device descriptions are fetched eagerly, but each service's **SCPD** (the XML listing its actions) is fetched only when the operator expands the service node. This keeps startup cost bounded — most services in most sessions are never expanded. The SCPD parse is incremental, so a large action list does not freeze the expand interaction.

**Functional Requirements**

#### FR-012: Action enumeration on service expansion

When the user expands a service node, ohSpy retrieves the service's SCPD (`<SCPDURL>` declared in the device description, UDA 1.0 §2.1) and displays every action in its `<actionList>` (UDA 1.0 §2.3) as child nodes.

**Consequences (testable):**
- A "Loading…" placeholder is visible during the fetch (FR-044).
- On fetch or parse failure, an inline error placeholder is shown (FR-013) and a Warning diagnostic is recorded (FR-039).

#### FR-100 (ohSpy): Incremental SCPD parse — UI never blocked

The SCPD parse MUST yield to the UI thread incrementally so that very large SCPDs (100+ actions, e.g. IGD routers) do not freeze the expand interaction. Actions MAY appear in the tree as they are parsed rather than waiting for the full document.

**Consequences (testable):**
- No UI-thread freeze longer than the no-blocking budget (see NFR §5.2) when expanding a service with a 100-action SCPD.
- Service node enters "Loading…" state immediately on expand; first action visible promptly, full list within the warm/cold expansion budget (Performance Budgets §6).

---

### 4.5 SSDP Message Log

**Description.** The right pane is a live scrolling list of every SSDP `alive` and `byebye` advertisement received, newest at top. The list is virtualised — chatty networks do not produce visible stutter (one of the two named UpnpSpy fixes).

**Functional Requirements**

#### FR-003: Right pane is a scrolling SSDP log

The right pane presents content as a scrolling list. Newer entries are inserted at the top; earlier entries are reached by scrolling down.

#### FR-014: Alive log entries

For every SSDP `NTS: ssdp:alive` advertisement received (UDA 1.0 §1.1.2), ohSpy inserts a row at the top of the right-pane list showing: timestamp received, literal `ALIVE`, and the device's UUID.

#### FR-015: Byebye log entries

For every SSDP `NTS: ssdp:byebye` advertisement received (UDA 1.0 §1.1.3), ohSpy inserts a row at the top of the right-pane list showing: timestamp received, literal `BYEBYE`, and the device's UUID.

#### FR-016: SSDP log cap with FIFO eviction

The SSDP log is capped at **10,000 entries**. Once the cap is reached, the oldest entry (at the bottom of the list per FR-055) is discarded each time a new entry arrives.

#### FR-055: Newest-first ordering with smart auto-follow

The right-pane SSDP log is ordered newest-first; the most recently received advertisement occupies the top row. Ordering MUST hold during steady-state arrivals and across the FR-016 eviction boundary (eviction removes the bottom row, never the top).

**Consequences (testable):**
- The view auto-follows new arrivals **only while the operator is parked at (or near) the top** of the list.
- Once the operator scrolls away from the top to read history, the list MUST NOT yank back to the top on every new arrival.

#### FR-101 (ohSpy): Virtualised log rendering

The SSDP log MUST be rendered with item-virtualised scrolling (e.g. `ItemsRepeater` or equivalent) so that sustained advertisement rates on a chatty network do not produce visible stutter and do not provoke full-pane or full-window repaints.

**Consequences (testable):**
- The log handles the burst-rate target in Performance Budgets §6 without dropped frames visible to the eye.
- Memory used by the rendered view does not scale with the number of buffered entries — only with the number of visible rows.

**Notes:**
- `[ASSUMPTION]` *No user-side filtering of the SSDP log is in v1 (e.g. by UUID, by NT, by alive vs byebye). The addendum named this as an UpnpSpy gap but doesn't commit ohSpy to fix it. Listed as Open Question §8.*

---

### 4.6 XML Viewing

**Description.** Right-click on a device or service row to open the raw XML (description XML for a device, SCPD XML for a service) in the system default browser. Cheap, high diagnostic value — no in-app XML viewer.

**Functional Requirements**

#### FR-017: Right-click device → Fetch description XML

Right-clicking a device node MUST present a context menu with an option to fetch the device's description XML.

#### FR-018: Right-click service → Fetch service XML / Subscribe

Right-clicking a service node MUST present a context menu with a "Fetch service XML" option and a "Subscribe" option.

#### FR-019: Open device XML in default browser

Choosing the "Fetch XML" option on a device node opens the device's description XML resource in the user's default web browser.

#### FR-020: Open service XML (SCPD) in default browser

Choosing the "Fetch service XML" option on a service node opens the SCPD XML resource in the user's default web browser.

---

### 4.7 Device Properties Window

**Description.** Right-click → Properties… on a device opens a read-only window showing the full UPnP description plus SSDP metadata, organised into Identity / Manufacturer / Network / Discovery history / Embedded devices sections. Complements the at-a-glance FR-051 secondary detail line when the operator wants the full record.

**Functional Requirements**

#### FR-052: Read-only Properties window

Right-clicking a device node MUST present a `Properties…` option alongside the existing `Fetch XML` option (FR-017). Choosing it opens a read-only Properties window owned by the main window (FR-046) displaying every captured field for that device, organised as:

- **Identity:** `friendlyName`, full `deviceType` URN, `UDN`/UUID, `presentationURL` (rendered as a clickable link that opens the device's web UI in the default browser when present).
- **Manufacturer:** `manufacturer`, `manufacturerURL` (link), `modelName`, `modelNumber`, `modelDescription`, `modelURL` (link), `serialNumber`, `UPC`.
- **Network:** `LOCATION` URL (link), IP and port extracted from it, SSDP `SERVER` header, `CACHE-CONTROL` max-age in seconds.
- **Discovery history:** `FirstSeenUtc`, `LastSeenUtc`, total alive count, `BOOTID.UPNP.ORG` and `CONFIGID.UPNP.ORG` (UDA 1.1 §1.2 — present only if the device advertised them).
- **Embedded devices:** recursive list of `<deviceList>` children showing each child's `deviceType` and `friendlyName`; MAY be empty.

**Consequences (testable):**
- Fields the device did not declare are shown as a muted placeholder (e.g. `—`) so the operator can distinguish "absent" from "empty".
- The Properties window MUST remain closeable without producing errors if the device is removed from the tree while it is open (FR-037).

---

### 4.8 Rescan

**Description.** A `View → Rescan` menu item re-runs the startup discovery probe and prunes devices that didn't respond. While a rescan is running, ohSpy keeps handling unsolicited advertisements — rescan is additive to the live listener, not a replacement.

**Functional Requirements**

#### FR-021: Rescan menu command

ohSpy MUST provide a "Rescan" command under the "View" menu.

#### FR-022: Rescan uses identical M-SEARCH semantics

Choosing "Rescan" issues the same active-discovery (M-SEARCH, UDA 1.0 §1.2.2) request that startup uses, including the same `ST: upnp:rootdevice` search target (FR-004).

#### FR-023: Rescan-prune of non-responders

After the discovery wait period (MX) elapses for a rescan, ohSpy MUST remove any device in the tree that did not respond to that rescan.

#### FR-024: Rescan does not suspend live listening

A rescan in progress MUST NOT suspend handling of unsolicited alive or byebye advertisements (FR-006).

---

### 4.9 Action Invocation

**Description.** Double-clicking an action node opens a popup listing every input argument with editable fields, an "Invoke" control that POSTs the SOAP request to the service's `controlURL`, and the result display: output arguments on success, structured fault detail on UPnP fault, or transport-error detail on connection failure. Each popup is independent; multiple invocations across different actions can be in flight at once.

**FR-102 and FR-103** are deliberate parity-plus additions agreed during PRD review — see decision log.

**Functional Requirements**

#### FR-025: Open invocation popup on action double-click

Double-clicking an action node MUST open an invocation popup window for that action.

#### FR-026: Editable input arguments

The invocation popup MUST list every input argument declared by the action, provide an editable input for each, and allow the operator to set any input value before invoking.

#### FR-027: Invoke control sends SOAP request

The invocation popup MUST offer a control that sends the invocation to the device as a SOAP action request (UDA 1.0 §3.2.1) to the service's `<controlURL>`, using the values the operator has entered.

#### FR-028: Success result display

When the device returns a success response, the popup MUST display every output argument returned along with its value.

#### FR-029: UPnP fault display

When the device returns a SOAP/UPnP fault (UDA 1.0 §3.2.2, `<UPnPError><errorCode/><errorDescription/></UPnPError>`), the popup MUST display:
- HTTP status code
- UPnP error code
- UPnP fault description text returned

#### FR-030: Transport-error display

When an invocation request fails before a response can be parsed (e.g. device unreachable, timeout), the popup MUST display the error condition with available diagnostic information without crashing the application.

#### FR-031: Argument-less actions

The invocation popup MUST handle:
- Actions that declare no input arguments (invocable with empty input).
- Actions that declare no output arguments (show success without output values).

#### FR-102 (ohSpy, parity-plus): Enumerated input arguments via SCPD `<allowedValueList>`

When the SCPD declares an input argument's related state variable with an `<allowedValueList>` (UDA 1.0 §2.3), the invocation popup MUST present that argument as a constrained selector (dropdown / combo) populated with exactly the listed values, in declared order.

**Consequences (testable):**
- The operator cannot submit a value outside the `<allowedValueList>` for that argument.
- If the related state variable declares a `<defaultValue>` and that value is a member of the `<allowedValueList>`, the selector is pre-populated with the default; otherwise the first listed value is pre-populated.
- If the SCPD declares the argument with `<allowedValueList>` but the list is empty or malformed, the popup falls back to free-form text input and records a Warning diagnostic (FR-039).
- Arguments **without** `<allowedValueList>` are covered by FR-103 if the SCPD declares `<allowedValueRange>` for them; otherwise they continue to be entered as free-form text in v1.
- `<allowedValueList>` and `<allowedValueRange>` are logically mutually exclusive — they partition by the related state variable's `<dataType>` in UDA 1.0 §2.3 (string-typed variables carry `<allowedValueList>`; numeric ones carry `<allowedValueRange>`). If a malformed SCPD declares both for the same state variable, FR-102 wins and a Warning diagnostic (FR-039) is recorded.

#### FR-103 (ohSpy, parity-plus): Numeric input arguments via SCPD `<allowedValueRange>`

When the SCPD declares an input argument's related state variable with an `<allowedValueRange>` (UDA 1.0 §2.3) and a numeric `<dataType>`, the invocation popup MUST present that argument as a constrained numeric input bounded by `<minimum>` and `<maximum>`, advancing by `<step>` where declared.

**Consequences (testable):**
- The operator cannot submit a numeric value outside `[<minimum>, <maximum>]` for that argument.
- Where `<step>` is declared, the input snaps to (or validates against) `<minimum> + n × <step>` for integer `n ≥ 0` within range; submitting a value off-step is rejected client-side with an inline message.
- If the related state variable declares a `<defaultValue>` that satisfies the range (and step, where declared), the input is pre-populated with the default; otherwise the input is pre-populated with `<minimum>`.
- If the SCPD declares `<allowedValueRange>` on a non-numeric `<dataType>`, or `<minimum>` exceeds `<maximum>`, or `<step>` is zero or negative, the popup falls back to free-form text input and records a Warning diagnostic (FR-039).
- Arguments without either `<allowedValueList>` (FR-102) or `<allowedValueRange>` (FR-103) continue to be entered as free-form text in v1.

**Feature-specific NFRs:**

- The invocation popup becomes interactive (input fields editable) within the interaction budget in Performance Budgets §6.
- Invocation requests MUST be subject to the per-request HTTP timeout discipline (NFR-P2, §5.2) — a hung device does not freeze the popup or leak resources.

**Notes:**
- `[ASSUMPTION]` *Action input arguments with neither `<allowedValueList>` nor `<allowedValueRange>` are entered as free-form text in v1 (no per-`<dataType>` typed inputs — strings, booleans, dates etc. all enter as text). Listed as Open Question §10 for revisit.*

---

### 4.10 Service Subscription (GENA)

**Description.** Right-click a service → Subscribe opens a popup and initiates a GENA subscription against the service's `eventSubURL`. Incoming `NOTIFY` events stream into the popup's event list; a "Latest property values" summary at the top shows the most recent value of each evented property.

**Functional Requirements**

#### FR-032: Open subscription popup and SUBSCRIBE

Choosing the "Subscribe" option on a service's right-click menu (FR-018) MUST open a subscription popup window and initiate a UPnP eventing subscription (`SUBSCRIBE`, UDA 1.0 §4.1.1) against the service's `<eventSubURL>`.

**Consequences (testable):**
- The `CALLBACK` URL announced in SUBSCRIBE MUST point at the currently-selected adapter's IPv4 address (FR-048) on the local callback host's port (FR-049).

#### FR-033: Event list and "Latest property values" summary

While the subscription popup is open, every event notification received from the subscribed service (`NOTIFY` message, UDA 1.0 §4.2.1; `<e:propertyset>` XML template, UDA 1.0 §4.3) MUST be inserted at the **top** (index 0) of the popup's scrolling event list. Newest event always first row visible; older events scroll off the bottom.

Above the event list, the popup MUST display a fixed "Latest property values" summary:
- For each evented property name seen so far during the subscription's lifetime.
- Showing the property's most-recent value.
- Later events for the same name overwrite the row in place.
- The summary remains anchored at the top of the popup independent of the event list's scroll position.

When the popup's event-buffer cap is reached (target: 5,000 newest-first; see Performance Budgets §6), the oldest event (now at the **tail** of the list) MUST be discarded first.

#### FR-034: UNSUBSCRIBE on popup close

When the user closes a subscription popup, ohSpy MUST send an `UNSUBSCRIBE` request (UDA 1.0 §4.1.3) for that subscription.

#### FR-035: Failed-subscription handling

If a subscription cannot be established, the popup MUST inform the operator and MUST NOT attempt to send an unsubscribe for a subscription that was never created.

#### FR-036: Multiple concurrent subscription popups

ohSpy MUST allow multiple subscription popups, across different services, to be simultaneously open, each managing its own subscription lifecycle independently.

#### FR-038: Subscription auto-renewal

For as long as a subscription popup remains open, ohSpy MUST renew the subscription with the device (`SUBSCRIBE` with `SID` only, UDA 1.0 §4.1.2) before each device-granted timeout (`TIMEOUT` header on the SUBSCRIBE response, UDA 1.0 §4.1.1) expires, so event delivery is uninterrupted.

**Consequences (testable):**
- If renewal is refused or fails, the popup informs the operator that the subscription has lapsed and stops attempting to renew.
- Closing the popup in the lapsed state MUST NOT attempt to send an unsubscribe for an expired subscription.

#### FR-104 (ohSpy): Non-serial NOTIFY processing per subscription

The event-handling pipeline MUST NOT process incoming `NOTIFY` messages strictly one-by-one in a way that lets a single slow or malformed `NOTIFY` block subsequent events.

**Consequences (testable):**
- One slow-parsing or malformed `NOTIFY` on a subscription MUST NOT delay delivery of subsequent `NOTIFY` messages on the same or other subscriptions by more than the per-request timeout (NFR-P2).
- A subscription receiving a high-frequency event burst MUST NOT cause other open subscriptions to fall behind.
- Per-subscription event queues are bounded (FR-033 cap); overflow is by FIFO eviction at the tail (newest preserved), not by back-pressure on the device.

**Feature-specific NFRs:**

- The event list is rendered with item-virtualised scrolling (per NFR-P1, §5.2) — busy services do not produce visible stutter.
- Notification processing MUST tolerate one malformed `NOTIFY` without blocking subsequent events (the discipline FR-104 codifies).

**Notes:**
- `[ASSUMPTION]` *Event notifications are displayed in their received form; per-service rich interpretation (e.g. translating LinnDS `Volume` property updates into a slider) is out of scope for v1. Carried forward from UpnpSpy.*

---

### 4.11 Network Adapter Selection

**Description.** ohSpy operates on exactly one IPv4 adapter at a time, defaulting at startup to the first eligible adapter enumerated. A `View → Network adapter` menu lists every eligible adapter as a radio item; selecting a different adapter triggers an atomic rebind (stop transport + callback host → clear registry → cancel in-flight fetches → notify open popups → rebind on new adapter → re-run startup discovery).

**Functional Requirements**

#### FR-048: Single adapter at a time, radio-list switch

ohSpy MUST operate on exactly one IPv4 network adapter at a time. The set of available adapters is the eligible-IPv4 interfaces enumerated at startup (operational, non-loopback, multicast-capable, IPv4). At startup the system MUST default to the first eligible adapter. ohSpy MUST expose a `View → Network adapter` menu listing every available adapter as a radio item. Selecting a different adapter becomes the new "current adapter" and triggers the rebind sequence in FR-050.

**Consequences (testable):**
- Hosts with zero eligible adapters MUST keep running with an empty tree (no error dialog, no crash); a Warning diagnostic MUST be recorded.

#### FR-049: TcpListener callback host — no URL ACL, no Admin

The eventing callback host (FR-033) MUST bind via `System.Net.Sockets.TcpListener` to the specific IPv4 address of the currently-selected adapter (FR-048), parsing incoming `NOTIFY` requests in-process.

**Consequences (testable):**
- Implementation MUST NOT rely on `System.Net.HttpListener`.
- MUST NOT register a URL ACL via `netsh http`.
- MUST NOT require Administrator privileges or any one-shot installer step.
- Hand-parsed HTTP/1.1 surface is restricted to: request line, header block, `Content-Length`-bounded request body.
- Requests with malformed framing, oversized headers, or oversized bodies MUST be rejected with `400 Bad Request` and a Warning `DiagnosticEntry`.
- Per-request read MUST be bounded by timeout to defend against half-open / slowloris connections.

#### FR-050: Atomic adapter-switch rebind

When the user selects a different adapter (FR-048), ohSpy MUST atomically:
- (a) Stop the SSDP transport and callback host.
- (b) Clear the device registry (devices observed on the previous adapter are no longer reachable the same way and MUST be re-discovered).
- (c) Cancel every in-flight description / SCPD fetch.
- (d) Tell every open invocation or subscription popup that its device is no longer reachable (per FR-037).
- (e) Rebind the SSDP transport and callback host on the new adapter.
- (f) Re-run the startup discovery sweep (FR-004) so the tree refills on the new adapter.

**Consequences (testable):**
- The adapter switch completes within the discovery budget (Performance Budgets §6) and MUST NOT block the UI thread.

---

### 4.12 Diagnostics

**Description.** ohSpy records a structured diagnostic entry for every internal error and notable event: SSDP parse failure, description/SCPD fetch failure, action transport error, subscription failure, and similar. Two sinks: a bounded in-memory ring buffer exposed via `View → Diagnostics`, and a rolling on-disk log file under `%LOCALAPPDATA%`. Both are bounded. Logging MUST NOT block the UI thread, MUST NOT prevent app startup if the log file cannot be opened, and MUST NOT include sensitive data beyond what is already in the UPnP protocol exchange.

**Functional Requirements**

#### FR-039: Record structured diagnostic entries

ohSpy MUST record internal diagnostic events including (but not limited to):
- SSDP parse failures
- Device-description fetch or parse failures
- SCPD fetch or parse failures
- Action-invocation transport errors
- Subscription establishment failures
- Subscription renewal failures
- Unsubscribe failures

Each diagnostic entry MUST carry: timestamp, severity, and enough context (device UUID, service id, action name, URL, status code, error text as applicable) to identify what went wrong — in addition to whatever user-visible inline message was shown at the point of failure.

#### FR-040: Bounded rolling log file

ohSpy MUST write diagnostic entries to a rolling log file at a standard per-user location on disk (e.g. under `%LOCALAPPDATA%`). The file MUST be bounded via size-based rollover with a small fixed number of rotated files; it MUST NOT grow without limit across long sessions or many runs.

#### FR-041: In-memory diagnostic buffer and live viewer

ohSpy MUST keep an in-memory diagnostic buffer of bounded size (a ring buffer), exposed via a `View → Diagnostics` menu item that opens a scrollable viewer window showing the buffered entries.

**Consequences (testable):**
- The viewer MUST remain responsive while new entries arrive.
- The viewer MUST update live as new entries are recorded.
- Each row MUST surface two device-affiliation columns alongside timestamp/severity/category/message:
  - **Identity**: resolved from the entry's `device.uuid` context value, displayed as the device's current `friendlyName` if the device is in the registry and has one, otherwise as `"uuid:<uuid>"`. Entries with no `device.uuid` context render as a visually muted placeholder (e.g. `—`).
  - **Endpoint**: resolved from the entry's `url` context value (parsed as URI, displayed as `host` or `host:port` depending on whether port is the URI's default), falling back to `remote.endpoint` for diagnostics carrying only a network endpoint (e.g. `Ssdp.Parse` failures), falling back to a muted placeholder when neither is present.
- Resolution is **best-effort and snapshot-at-arrival**: identity is resolved from the registry once when the entry enters the viewer's collection; devices that have since left the registry (byebye / rescan-prune) fall back to `"uuid:<uuid>"`. This preserves the diagnostic as a stable historical record while still giving the operator a recognisable name for devices still in the tree.

#### FR-042: Diagnostic logging discipline

Diagnostic logging:
- MUST NOT block the UI thread.
- MUST NOT prevent the application from starting if the log file cannot be opened (in that case the in-memory buffer and viewer continue to function and a single user-visible warning is shown).
- MUST NOT include sensitive data beyond what is already implicit in the UPnP protocol exchange.

---

### 4.13 Secondary Window Lifecycle

**Description.** ohSpy opens several secondary windows (action invocation popups, subscription popups, the Diagnostics viewer, the Properties window). They are visually owned by the main window (z-order, minimise/restore, close) and remain robust when the underlying device leaves the network while open.

**Functional Requirements**

#### FR-037: Open popups survive device disappearance

When a device leaves the network (byebye, FR-008) or is otherwise removed from the tree (rescan-prune, FR-023; adapter switch, FR-050), any open invocation or subscription popup for that device (or for one of its services) MUST inform the operator that the device is no longer reachable and MUST remain closeable without producing errors. The Properties window (FR-052) is bound by the same contract.

#### FR-046: Main-window-owned popups

Every secondary window ohSpy opens — action **invocation** popup (FR-025), service **subscription** popup (FR-032), **Diagnostics** viewer (FR-041), device **Properties** window (FR-052) — MUST be visually **owned** by the main window:
- Appears above the main window the moment it is shown.
- Remains z-ordered above the main window as long as both are visible (routine focus shifts back to the main window MUST NOT push the popup behind it).
- Minimises and restores together with the main window.
- Closes automatically when the main window closes.

Each popup is otherwise independently activatable — ownership is a z-order and lifetime contract, not modality.

---

## 5. Cross-Cutting NFRs

The brief defines three non-negotiable quality bars — **Reliability**, **Performance**, **UI polish**. Schedule yields to them: the lunch & learn happens when the bars are met, not on a fixed date.

### 5.1 Reliability

**NFR-R1.** No crashes during a typical 30-minute debugging session on a developer's network with normal real-world device misbehaviour (slow responders, devices that disappear mid-interaction, partial NOTIFY messages, larger-than-typical SCPDs).

**NFR-R2.** Slow-responding or misbehaving devices MUST NOT hang the UI. Bounded eager-fetch parallelism (FR-043), per-request HTTP timeout (NFR-P2 below), and incremental SCPD parse (FR-100) are the enforcement mechanisms.

**NFR-R3.** Open popups MUST recover cleanly when their device disappears mid-interaction (FR-037, restated as a cross-cutting expectation).

**NFR-R4.** Diagnostic logging failure (e.g. log file path unwritable) MUST NOT prevent the app from running (FR-042).

**NFR-R5.** Hosts with zero eligible network adapters MUST keep running (FR-048) — empty tree, Warning diagnostic, app remains interactive.

**Out of Scope for v1:** deliberately adversarial / fuzz-style malformed UPnP traffic. Ordinary brokenness is in scope; pathological inputs are not.

### 5.2 Performance

**NFR-P1. Virtualised rendering on all high-cardinality lists.** The SSDP log (FR-101) and any other high-cardinality list (subscription event list, diagnostic viewer) MUST be rendered with item-virtualised scrolling. Visible memory and per-frame cost MUST scale with the number of visible rows, not the number of buffered entries.

**NFR-P2. Per-request HTTP timeout discipline.** Every outbound HTTP request (description fetch, SCPD fetch, SOAP invocation, SUBSCRIBE, UNSUBSCRIBE) MUST have a bounded per-request timeout. A hung device MUST NOT stall a fetch queue or freeze a popup. Defaults are inherited from UpnpSpy `plan.md`; specific values are an architecture decision.
- `[ASSUMPTION]` *Default per-request timeout target: 5 s for description fetches and SUBSCRIBE/UNSUBSCRIBE, 10 s for SOAP invocations. Confirm in `bmad-create-architecture`.*

**NFR-P3. No UI-thread blocking.** All network I/O MUST be async. `.Result` / `.Wait()` are forbidden. No blocking calls on discovery, rescan, fetch, invocation, subscription, adapter switch, or diagnostic logging paths. This is the binding invariant; verified by static analysis or integration tests where feasible.

**NFR-P4. Incremental large-SCPD parse.** Per FR-100 — restated as a cross-cutting expectation that the same incremental-parse discipline applies anywhere XML parsing of unbounded size happens.

**NFR-P5. Keyed, identity-tracked collection updates.** Tree and list updates MUST be incremental and identity-tracked (no rebuild-on-change patterns); a single child fetch completing MUST NOT cause a subtree redraw, a single SSDP arrival MUST NOT cause a full-pane repaint.

**NFR-P6. Bounded fan-out on discovery bursts.** Eager description fetch concurrency is capped (FR-043 — target 8). A discovery burst across 50 devices does not produce 50 concurrent HTTP requests.

### 5.3 UI Polish

**NFR-UI1.** Modern WinUI conventions throughout — typographic hierarchy, spacing, colour. Where conventions conflict, the WinUI 3 design guidelines win.

**NFR-UI2.** Considered visual hierarchy on the tree row: friendly name primary, FR-051 detail line muted-secondary, glyph leading. No `[ASSUMPTION]` placeholder visuals (per FR-009 + FR-047).

**NFR-UI3.** No flicker on incremental updates: no transient empty states (FR-044), no chevron disappear/reappear, no subtree redraw on label refresh (NFR-P5).

**NFR-UI4.** Smooth interaction in steady state on contemporary Windows hardware: no dropped frames visible to the eye during SSDP log burst, large-SCPD expand, or rapid event arrival.

---

## 6. Performance Budgets

Inherited verbatim from UpnpSpy `plan.md` (SC-001 through SC-018) and the brief.

| ID | Scenario | Budget |
|---|---|---|
| **SC-001** | Startup → every responsive device with fetchable description visible in tree | ≤ ~7 s total (5 s MX + ≤ 2 s eager fetch) on a typical LAN |
| **SC-002** | Device deduplication across a 30-minute session | Exactly one tree entry per UUID; zero duplicates |
| **SC-003** | `ssdp:byebye` → device disappears from tree | Typically < 2 s on a quiet LAN |
| **SC-004** | Service/action node expansion → children visible | ≤ 2 s for descriptions of typical size on a LAN |
| **SC-005** | Choose "View XML" → default browser opens to XML | ≤ 2 s |
| **SC-009** | SSDP advertisement received → row visible in log | ≤ 1 s |
| **SC-010** | Double-click action → invocation popup interactive | ≤ 1 s |
| **SC-011** | Action invocation submitted → result visible (device answers within < 1 s LAN latency) | ≤ 2 s |
| **SC-013** | 1-hour continuous operation on a typical LAN | No memory exhaustion; SSDP log and diagnostic buffer remain bounded; on-disk log rolls over |
| **SC-R-30min** | 30-min debugging session on a developer's LAN (brief Reliability bar, NFR-R1) | 0 crashes; 0 UI hangs > 1 s; 0 unclosable popups after device disappearance |
| **Scale ceiling** | 8-hour session, 20 devices, 5 open subscription popups, saturated SSDP log | < 200 MB resident |
| **Fetch concurrency** | Eager-fetch SemaphoreSlim | ≤ 8 concurrent description fetches |
| **SSDP log cap** | FR-016 in-memory cap | 10,000 entries (FIFO) |
| **Diagnostic ring cap** | FR-041 in-memory cap | ~5,000 entries (FIFO) |
| **Diagnostic on-disk cap** | FR-040 rolling files | ≤ 8 files × ≤ 2 MB = ≤ 16 MB total |
| **Subscription event cap** | FR-033 in-popup cap | ~5,000 events per popup (FIFO) |
| **Warm SCPD expand** | Service node expansion when description eager-fetched | ≤ 100 ms |
| **Cold large-SCPD expand** | Service expansion for 100+-action SCPD on first request | ≤ 2 s, no UI freeze (FR-100) |

**Test environment baseline.** "Typical LAN" — the developer's office or home network with 10–30 announcing UPnP devices. Hardware baseline: current generation Windows laptop on Wi-Fi or wired gigabit. Sustained chatty-SSDP target for stutter tests: ≥ 20 advertisements/sec for ≥ 30 seconds without visible dropped frames or main-thread stalls > 16 ms.

---

## 7. Non-Goals (Explicit)

The brief is the source of truth on scope. Repeated here for downstream readers:

- **No features beyond UpnpSpy parity + the named fixes.** No "while we're in there" additions.
- **No cross-platform support.** Windows only.
- **No public / open-source distribution at v1.** Internal Linn only via unsigned installer. Public release is a deferred decision.
- **No persona work, visual design system, or branding.** Developer tool; BMM lane, not WDS lane.
- **No technical moat-building or differentiation beyond "it works and is supported."**
- **No settings persistence.** No cross-session state — no last-adapter, no window-layout, no last-selection. The tool launches clean every time.
- **No accessibility / a11y compliance at v1.** Acknowledged. Revisit if distribution widens.
- **No IPv6.** IPv4 only; SSDP multicast group `239.255.255.250:1900`.
- **No multi-NIC merging.** One eligible adapter at a time (FR-048); a device reachable on multiple adapters appears only on the currently-selected adapter.
- **No `<dataType>`-driven typed inputs** (beyond the `<allowedValueList>` / `<allowedValueRange>` constraints covered by FR-102 / FR-103). Arguments declared as `boolean`, `dateTime`, `uri` etc. without an explicit list or range constraint remain free-form text in v1 — no type-specific picker, no client-side parse validation.
- **No per-service rich event interpretation.** Events are shown in their received `<e:propertyset>` form (Open Question §8).
- **No deliberately adversarial / fuzz-style malformed UPnP traffic in scope for Reliability.** Ordinary brokenness in, pathological brokenness out.

---

## 8. MVP Scope

### 8.1 In Scope

- All four core capabilities — Discover, Browse (services/actions), Invoke, Subscribe — at parity with UpnpSpy.
- All FRs in §4 (FR-001..FR-055 lifted from UpnpSpy + FR-100, FR-101, FR-104 named fixes + FR-102, FR-103 parity-plus additions agreed during PRD review).
- The three quality bars (Reliability, Performance, UI polish — §5) on the budgets in §6.
- Diagnostic logging (FR-039–FR-042) with both sinks (in-memory ring + rolling file).
- Single-adapter operation with `View → Network adapter` switch (FR-048–FR-050).
- Device Properties window (FR-052) and secondary-detail row (FR-051).
- Distribution as an **unsigned InnoSetup installer** (single `setup.exe`) for internal Linn use. Per-user install under `%LOCALAPPDATA%\Programs\ohSpy\`; no Administrator required; SmartScreen warning bypassed by the user on first run. (Revised 2026-06-01 by `bmad-create-architecture` Decision 12 — supersedes the prior MSIX choice. MSIX rejected for: sandbox-virtualised filesystem obscures FR-040 diagnostic log path; unsigned MSIX install requires user-side "developer mode" or sideload-apps toggle. InnoSetup is the established free Windows installer authoring tool and aligns with the audience's existing expectations.)
- Full BMad spec-artifact trail: brief (done) → this PRD → architecture document → epics & stories → sprint plan → story-by-story implementation with code review.

### 8.2 Out of Scope for MVP

- Settings persistence (no last-adapter, no window-layout, no last-selection). *Reason: scope discipline; users launch clean each time.*
- SSDP log filtering / search (`[NOTE FOR PM]` named in addendum as a UpnpSpy gap; ohSpy does not commit to fix in v1. Revisit if quality-bar work surfaces capacity.).
- Hover tooltip on tree rows (Tier 2 disambiguation deferred upstream; FR-051 + FR-052 cover it together).
- Automatic device-name refresh on re-announce *beyond* the FR-054 sort/migration behaviour. Label change updates the row in-place; no separate "rebadge" affordance.
- Rich per-service event interpretation (deferred; events shown in received form).
- Enumerated-value dropdowns in action invocation (deferred; free-form text).
- IPv6, multi-NIC merging.
- macOS, Linux.
- Public / open-source distribution.
- Accessibility (a11y) compliance.
- Live-device smoke test project (UpnpSpy `plan.md` flagged this as a possible future opt-in; out of v1).

---

## 9. Success Metrics

### Primary

**SM-1. The four core capabilities work on the device set in scope.** Discover, browse, invoke, subscribe all succeed against (a) Linn DS streamers / OpenHome devices, (b) the typical third-party UPnP gear on a developer network (DLNA media servers and renderers, IGD routers, smart-home gateways), with normal real-world misbehaviour tolerated (slow responders, mid-interaction byebye, partial NOTIFY, large SCPDs). *Validates: all of §4.*

**SM-2. The two UpnpSpy complaints are demonstrably absent.** (a) SSDP log handles a chatty network at the burst-rate target (§6) with no visible stutter and no full-screen repaints (FR-101, NFR-P1, NFR-P5). (b) Description and SCPD fetches enforce timeouts and do not hang the app on slow devices (NFR-P2). Verified by eye in a chatty SSDP environment. *Validates: FR-100, FR-101, NFR-P1–P5.*

**SM-3. Performance budgets met on contemporary Windows hardware.** All §6 budgets pass on the test baseline. *Validates: §6 entire.*

### Secondary

**SM-4. Reliability over a typical session.** No crashes during a 30-minute debugging session on a developer's network with normal device misbehaviour. *Validates: NFR-R1–R5.*

**SM-5. The lunch & learn lands.** Within a ~30–45-minute slot, attendees can follow the narrative arc (problem → brief → PRD → architecture → stories → working app), ask substantive questions about the process, and leave curious enough to try BMad on their own work. *Validates: the artifacts-as-deliverables framing in the brief — readability of brief.md, prd.md, architecture document, stories.*

**SM-6. Artifact coherence.** The brief, PRD, architecture, stories, and sprint plan can be walked through live without retconning, hand-waving, or major contradiction between layers. *Validates: BMad workflow discipline.*

### Counter-metrics (do not optimise)

**SM-C1. Do not optimise for feature count.** ohSpy's scope is parity-with-UpnpSpy + named fixes. Adding features to look productive *counterbalances* SM-1; resist scope creep.

**SM-C2. Do not optimise for benchmark microscores at the cost of perceived smoothness.** "Faster on a graph" that fails the eye-test on SC-009 / large-SCPD expand is a regression. Counterbalances SM-3.

**SM-C3. Do not optimise for talk polish at the cost of artifact honesty.** If a section of brief / PRD / architecture is wrong or incomplete, fix it; do not paper it over for the walkthrough. Counterbalances SM-5/SM-6.

---

## 10. Open Questions

1. **Per-request HTTP timeout values.** NFR-P2 specifies *that* there is a per-request timeout discipline; exact defaults (5 s description, 10 s SOAP) are an `[ASSUMPTION]` — confirm in `bmad-create-architecture`.
2. **SSDP log filtering / search.** UpnpSpy lacks this; ohSpy does not commit to add it in v1 (Non-Goal). Revisit if quality-bar work surfaces capacity, or if real-world use reveals the lack hurts.
3. **`<dataType>`-driven typed inputs in action invocation.** Beyond `<allowedValueList>` (FR-102) and `<allowedValueRange>` (FR-103), declared SCPD `<dataType>`s (e.g. `boolean`, `dateTime`, `uri`, `bin.base64`) are not surfaced as type-specific pickers or client-side parsers in v1. Revisit post-v1 if specific types recur in real use.
4. **Rich per-service event interpretation.** Currently raw `<e:propertyset>` display. Revisit if and when Linn-specific service inspection demands warrant it.
5. **Live-device automated smoke tests.** UpnpSpy flagged a possible opt-in `DeviceTests` project. ohSpy defers; if reliability/perf testing demands it, fold into the architecture phase.
6. **Public / open-source release decision.** Brief defers. Revisit after L&L outcome and internal adoption signal.
7. **Reverse-DNS / mDNS supplementary discovery.** Out of scope. Listed only because some devices announce on mDNS but are weak SSDP responders; not a v1 concern.

---

## 11. Assumptions Index

Items below want explicit confirmation:

- **§4.5 FR-101** — no user-side SSDP-log filtering in v1.
- **§4.9 FR-102 / FR-103** — `<allowedValueList>` and `<allowedValueRange>` surfaced as constrained inputs; arguments with neither remain free-form text.
- **§4.10 FR-033** — events shown in received form; no rich per-service interpretation.
- **§5.2 NFR-P2** — default per-request timeouts (5 s description / SUBSCRIBE / UNSUBSCRIBE, 10 s SOAP); architecture phase confirms exact values.
- **§5.3 NFR-UI1** — "modern WinUI conventions" anchors to WinUI 3 design guidelines (framework choice ratified in architecture).
- **§6 test baseline** — "typical LAN" = developer network with 10–30 announcing devices; sustained chatty-SSDP target ≥ 20 adv/s for ≥ 30 s.
- **§6 Scale Ceiling row** — 8-hour / 200 MB ceiling is extrapolated beyond the brief's explicit 30-min / 1-hour bars; revisit if architecture shows it's not deliverable.
- **§4.1 / §5.1** — device set baseline and "ordinary brokenness only" — carried from brief.
- **§0 / §8** — distribution: unsigned InnoSetup installer (`setup.exe`), per-user install under `%LOCALAPPDATA%\Programs\ohSpy\`, internal Linn only. (Revised 2026-06-01 — superseded prior MSIX choice; see §8.1.)
