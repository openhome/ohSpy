# Sprint Change Proposal — SSDP expiry (inferred byebye) + network-change handling

**Date:** 2026-06-11
**Author:** Developer (correct-course), with Simonc (Project Lead)
**Trigger:** Two discovery defects found in real-world use after the Epic 6 install (the app shipped + ran on a clean box). Both require **new functional requirements** — the behaviours were never specified.
**Mode:** Batch.

---

## Section 1 — Issue Summary

**Defect 1 — SSDP `max-age` expiry / "inferred byebye" is missing.** A device pulled off the network **without** sending `ssdp:byebye` is **never removed**. `CACHE-CONTROL: max-age` is parsed and stored (`SsdpParser` → `SsdpAnnouncement.CacheControlMaxAge` → `RegistryEntry.CacheControlMaxAge` + `LastSeenUtc`) but **nothing acts on it**. The only removal paths are `DeviceRegistry.OnByebye` (graceful), the **manual** `PruneNotSeenSince` (Story 5.3 Rescan), and adapter-switch `Clear`. There is **no expiry timer/sweep**. The spec confirms the gap: **FR-008 is "Removal on graceful *leave*" — byebye only.** Standard UDA behaviour (a device promises to re-advertise within `max-age`; a control point evicts when that lease lapses) was never required.

**Defect 2 — moving the PC between networks doesn't work.** Moving office→home left **all** unreachable office-subnet devices visible for a day. There is **no** `NetworkChange.NetworkAddressChanged` listener anywhere — the app only rebinds on a **manual** `View → Network adapter` pick. So on a network move: the old devices never byebye, never expire (Defect 1), and the app never notices the adapter/IP changed → they linger indefinitely.

**Relationship:** Defect 1's expiry is the *safety net* (stale devices eventually age out after `max-age`); Defect 2's network-change detection is the *responsive* fix (rebind to the live network immediately + clear the old one). Both are wanted.

---

## Section 2 — Impact Analysis

- **Epic impact:** **Epic 2 (Discovery & Tree)** reopens for two **corrective stories** (the Story 2-10 precedent — corrective stories appended to a closed epic). No other epic changes.
- **Story impact:** two **new** stories (proposed `2-11`, `2-12`). No existing story is rewritten. They reuse shipped seams: Defect 1's eviction reuses `DeviceRegistry.RemoveCore` (cancel `DeviceCts` + `DeviceRemoved` per UDN — same cascade as byebye, so FR-037 popup banners "just work"); it is the **automatic cousin of 5.3's `PruneNotSeenSince`**. Defect 2's rebind reuses **Story 5.2's `ShellViewModel.SwitchAdapterAsync`** atomic rebind (which already clears the registry + re-discovers).
- **Artifact conflicts:**
  - **PRD:** add **FR-056** (removal on expiry) next to FR-008, and **FR-057** (network-change rebind) near FR-050. Both are new.
  - **Architecture:** amend **Decision 9 / the DiscoveryService lifecycle** for the expiry sweep (a new Amendment); add a decision/amendment for **network-change detection → auto-rebind** (reusing the 5.2 machinery). Possibly new `DiagCategories` constants (`Ssdp.Expired`, `Adapter.NetworkChanged`) — pinned-set update (the 5.1/5.3 precedent).
  - **Epics doc:** add the FR rows + the two corrective stories under Epic 2.
- **Technical impact (Core only; no UI redesign):**
  - *Expiry:* a background sweep owned by the singleton `DiscoveryService` (already holds the discovery loop + the registry). Evicts entries where `now > LastSeenUtc + lease`. Marshalled onto the UI thread (`IUiDispatcher.Post` — registry is UI-thread-owned). Fully unit-testable with a virtual clock + the existing `DeviceRegistry` test rig; no manual smoke for the Core logic, plus a live smoke that a yanked (no-byebye) device disappears after its lease.
  - *Network-change:* a `NetworkChange.NetworkAddressChanged` subscription (debounced) → re-enumerate eligible adapters → if the bound adapter is gone/changed, `SwitchAdapterAsync` to the new best (or clear). The event fires off-thread → marshal; the rebind has its own re-entrancy guard.

---

## Section 3 — Recommended Approach

**Direct Adjustment** — add two corrective stories to Epic 2 + the two FRs + the architecture amendments. No rollback, no MVP change. Rationale: both are additive discovery-correctness features built on shipped seams (`RemoveCore`, `SwitchAdapterAsync`), Core-only, low blast radius, and independently shippable. Sequence them **2-11 (expiry) first** (it's self-contained and the safety net), then **2-12 (network-change)** (it leans on 2-11 for the eventual cleanup if a rebind target isn't found, and on 5.2 for the rebind).

**Scope classification: Moderate** (backlog add + PRD/architecture amendments). Route through the standard cycle: `create-story` → `dev-story` → Sonnet `code-review` → smoke → done, per story.

---

## Section 4 — Detailed Change Proposals

### 4.1 PRD — new FR-056 (insert after FR-008)

> #### FR-056: Removal on expiry (inferred byebye)
>
> A registered device whose latest `ssdp:alive` promised a `CACHE-CONTROL: max-age` lease MUST be removed from the registry (and tree) when that lease lapses without a refreshing `alive` — i.e. when `now > LastSeenUtc + max-age` — even though no `ssdp:byebye` was received (UDA 1.0 §1.2.2: a device re-advertises before its `max-age` expires; absence implies it has left). Removal uses the same path as FR-008 (byebye): the device leaves the registry + tree, open popups receive the FR-037 "device no longer reachable" treatment, and any in-flight description/SCPD fetch is cancelled.
> - **Grace:** eviction occurs at `LastSeenUtc + max-age` (a device promises to re-advertise within that window; UDA recommends `< ½ max-age`), with a small tolerance for network jitter (design decision in the story).
> - **Missing `CACHE-CONTROL`:** when an `alive` omits `max-age` (non-conformant but seen in the wild), a sensible **default lease** applies so the device still expires rather than living forever (default value is a story decision).
> - The check is periodic and MUST NOT block the SSDP read loop or the UI thread.

### 4.2 PRD — new FR-057 (insert near FR-050)

> #### FR-057: Rebind on host network change
>
> When the host's network changes while ohSpy is running — the bound adapter's IPv4 address changes, or the adapter is removed/disabled (e.g. moving the PC between networks) — ohSpy MUST detect the change and rebind: re-enumerate eligible adapters and, if the currently-bound adapter is no longer eligible, atomically rebind to the best available adapter (FR-050 sequence) or, if none is eligible, tear down to the zero-adapter state. Devices from the now-unreachable network are cleared as part of the rebind. The detection MUST debounce the burst of OS notifications a transition produces, and MUST NOT require an operator to manually re-pick the adapter.

### 4.3 New Story 2-11 — SSDP max-age expiry (FR-056)

- **Scope:** a periodic expiry sweep in `DiscoveryService` that evicts registry entries past `LastSeenUtc + lease` via `DeviceRegistry`'s `RemoveCore` cascade (new `IDeviceRegistry` method, e.g. `ExpireOlderThan(nowUtc)` or reuse/generalise `PruneNotSeenSince` with a per-entry lease), marshalled via `IUiDispatcher.Post`. Virtual-clock + a settable sweep interval (test seam; the `SubscriptionClient._delay` / `_rescanDelay` precedent).
- **Key decisions to settle in create-story:** sweep interval; grace tolerance at `1× max-age`; default lease when `CACHE-CONTROL` absent; a diagnostic on expiry (new `DiagCategories.SsdpExpired`? — pinned-set update, the 5.1/5.3 precedent).
- **Tests:** unit (device with lease L is evicted after L with no alive; a refreshing alive resets the lease; byebye still wins immediately; default-lease path for no-max-age; eviction raises `DeviceRemoved` + cancels `DeviceCts`; sweep marshalled — `DeferredUiDispatcher` guard). Manual smoke: yank a device (no byebye) → it disappears after its lease.

### 4.4 New Story 2-12 — network-change detection + auto-rebind (FR-057)

- **Scope:** subscribe to `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` (App-side or a Core seam); debounce; on quiescence re-enumerate via the existing `NetworkAdapterEnumerator`; if the bound adapter changed/disappeared, drive `ShellViewModel.SwitchAdapterAsync(best)` (reuse 5.2) or tear down to zero-adapter. Marshal the off-thread event; rely on the existing switch re-entrancy guard.
- **Key decisions to settle in create-story:** auto-target selection (best eligible vs clear-and-prompt); debounce window; interaction with a manual switch in flight; whether detection lives in Core (testable seam over an `INetworkChangeNotifier`) or App.
- **Tests:** unit over an injectable network-change notifier + adapter enumerator (bound-adapter-gone → rebind to best; no-eligible → zero-adapter; debounce coalesces a burst; manual-switch-in-flight is respected). Manual smoke: move the PC between networks → old devices clear, new network discovered, no manual re-pick.

### 4.5 Architecture amendments (pointers; authored during dev-story)

- **Amendment (Decision 9 / DiscoveryService lifecycle):** the registry gains an **expiry sweep** — a periodic, UI-thread-marshalled eviction of entries past their `CACHE-CONTROL` lease, reusing `RemoveCore`. Documents the sweep owner (DiscoveryService singleton), the lease/grace rule, the default-lease policy, and the test seam.
- **Amendment (network-change):** a debounced `NetworkAddressChanged` notifier drives `SwitchAdapterAsync` on a host network change (auto-rebind), complementing the manual `View → Network adapter` (5.2). Notes the off-thread marshalling + the shared re-entrancy guard.

---

## Section 5 — Implementation Handoff

- **Scope:** **Moderate** — backlog add (2 corrective stories under Epic 2) + PRD (FR-056, FR-057) + architecture (2 amendments) + epics-doc rows.
- **Recipients:** Developer (via `create-story` → `dev-story` → Sonnet `code-review` → smoke → done), one story at a time, **2-11 before 2-12**.
- **Success criteria:** (2-11) a no-byebye device is gone after its `max-age` lease; refresh resets it; byebye still immediate; sweep never blocks the UI; covered by unit + a live smoke. (2-12) moving networks auto-rebinds to the live network and clears the stale one, no manual re-pick, debounced; covered by unit + a live smoke.
- **PRD/epics/architecture edits** land as part of each story's create-story (the 2-10 precedent), not as a separate up-front doc pass.
