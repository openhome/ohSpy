# Story 6.1: Manual UI Verification — FR-044 / FR-046 / FR-054 Behaviours

Status: done
<!-- 2026-06-05: Manual UI verification walkthrough run on the live Linn network (Project Lead). Report:
     docs/verification/6.1-manual-ui-verification-2026-06-05.md — overall PASS for the L&L demo. All observable
     ACs PASS; A31 z-order/minimise supersession confirmed (free z-order + close-with-parent). Two polish
     defects found + fixed under Task 8 + re-verified: D1 phantom chevron on recycled action rows
     (AssignContainerSources clears leaf ItemsSource); D2 Invocation popup dropped behind on double-click
     post-A31 (InvocationPopupLauncher Low-priority deferred re-Activate). 6.1.9 N/A (friendlyName change is a
     byebye→alive cycle, not in-place; Move invariant unit-tested). 6.1.4 + 6.1.14 DEFERRED to a busier-network
     session (logged in deferred-work.md). ready-for-dev → done. -->;

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want a documented human walkthrough confirming every FR-044 / FR-046 / FR-054 / FR-045 / FR-051 / FR-055 behaviour that automated tests can't fully enforce — chevron-no-collapse-during-load, popup open-on-top / free-z-order / close-with-parent (per Amendment A31), row-migration identity preservation, kind-glyph rendering, secondary-detail muted styling, SSDP smart auto-follow — observed visually against a real LAN,
so that the L&L demo doesn't get derailed by a polish defect that integration tests passed but the eye catches.

## ⚠️ Read this first — what this story is (and is NOT)

**This is a VERIFICATION story. It writes essentially NO production code.**

- The deliverable is a **verification report** (Markdown — `docs/verification/6.1-manual-ui-verification-<yyyy-MM-dd>.md`) citing each AC **PASS / FAIL / N/A**, with the test devices used and screenshots/video for any non-obvious behaviour.
- The "Tasks" below are a **structured manual-verification checklist + the report template**, NOT code tasks.
- This is the **whole** smoke-per-ui-story discipline applied as a dedicated story.
- **If the walkthrough finds a polish defect, that becomes a small fix** committed under this story (with a regression test in Core if the defect is reproducible headlessly, or a note if it is App-render-only). The story itself remains verification — do not gold-plate, do not refactor, do not add features.
- Epic 6 delivers **no new FRs**. Source: epics.md §"Epic 6: Polish, Soak & Release Readiness" + epic-5-retro Epic-6-preparation note.

**This story REQUIRES the real app run against a live LAN.** It cannot be completed headlessly. The verifier is the Project Lead (Simon) on real Linn/OpenHome hardware — the same posture every UI-touching story in this project has held (smoke-per-ui-story is a first-class gate; see MEMORY `smoke-per-ui-story`).

## ⭐ CRITICAL — Amendment A31 supersedes the epic's z-order / minimise ACs

The epic prose for 6.1 (epics.md ~1988–2004) still describes the **original** FR-046 / Decision-10 popup behaviour (Win32 owner link: always-above, no-push-behind, minimise-together). **That owner link was REMOVED by Amendment A31** (committed `5668759`, 2026-06-04, after the Story 5.2 keystone smoke — the Project Lead found the pinned z-order confusing). The ACs below have been **rewritten to the A31 reality**. Verifying the stale epic ACs would "FAIL" correct, deliberate, shipped behaviour.

Source of truth: `architecture.md` §"Amendment A31 — Popups float in free z-order; no Win32 owner link (FR-046 / Decision 10 revision)" and `WindowOwnershipManager.cs` (verified shipped — no `SetWindowLongPtr`, explicit `parent.Closed` handler).

| FR-046 / Decision-10 AC | Original epic claim | After A31 (what to verify) | Status |
|---|---|---|---|
| **AC-10.1** open-on-top | popup appears above main when shown | **STILL TRUE** — via `child.Activate()` (foreground on open), NOT an owner link | ✅ Retained |
| **AC-10.4** no-push-behind | clicking main does NOT push popup behind | **SUPERSEDED / REMOVED** — popups float in NORMAL z-order; clicking the main window **DOES** bring it forward over the popup (deliberate change). Verify the NEW behaviour. | ❌ Removed |
| **AC-10.3** minimise/restore together | all popups minimise + restore with the shell | **SUPERSEDED / REMOVED** — popups are independent windows; they do **NOT** minimise/restore with the shell. Verify they DON'T (mark N/A vs the old claim). | ❌ Removed |
| **AC-10.2** close-with-parent | closing main closes all popups | **STILL TRUE** — now via an explicit per-child `parent.Closed` handler (not the OS owner link). Verify it. | ✅ Retained |
| **AC-10.5** `Activate()`-then-`Adopt` at every site | (implicit) | **STILL TRUE** — canonical open pattern unchanged at all four popup sites. | ✅ Retained |

The verification report MUST cite the supersession explicitly so a future reader does not file the A31 behaviour as a defect.

## Acceptance Criteria

> Each AC is verified by direct visual observation against the dev LAN and recorded PASS / FAIL / N/A in the report. ACs are renumbered for traceability; the source FR/AC is cited inline.

### Device tree rendering (FR-045 / FR-051 / NFR-UI2 / NFR-UI3)

1. **(AC-6.1.1 — row composition, FR-045 + FR-051 + NFR-UI2)** With 10–20 announcing UPnP devices, every device row shows: a **kind glyph** (leading, `Segoe MDL2 Assets` `Glyph` per `DeviceNodeViewModel.KindGlyph`) + **friendly name** (primary, default weight) + a **secondary detail line** beneath it in the muted brush (`MutedForegroundBrush` = `#FF767676`), containing `deviceType-tail · host:port` (middle-dot separated, `SecondaryDetail`). The friendly name remains the visual focus.

2. **(AC-6.1.2 — no flicker on re-announce, NFR-UI3 + NFR-P5)** When re-announces refresh device metadata, labels do **not** disappear / reappear; no transient empty state. (Bindings are `Mode=OneWay` x:Bind; the row updates in place.)

### Chevron + lazy load (FR-044 / FR-100 / NFR-UI3 / AC-A1.x)

3. **(AC-6.1.3 — persistent chevron, FR-044 + AC-A1.1)** A device row shows its **expand chevron immediately** when the row first appears — before any user click — because the node carries a `LoadingPlaceholderViewModel` child from creation (`DeviceNodeViewModel` ctor adds it; AC-A1.1).

4. **(AC-6.1.4 — Loading placeholder, FR-044)** Expanding a device whose services have not yet loaded shows the literal **"Loading…"** placeholder for ~0–2 s while service entries populate; the placeholder is visually distinguishable from real children.

5. **(AC-6.1.5 — chevron does NOT flicker during load, NFR-UI3 + AC-A1.4)** The chevron does **not** disappear and re-appear during the device load; children replace atomically (`ReplaceWith`, single Reset — AC-A1.4). **Regression watch (commit `4d380f8`):** the persistent-chevron "Loading…" placeholder bug — chevron previously never rendered children. **Regression watch (commit `a55ed74`):** a 25-service OpenHome device previously stuck on "Loading…" because the imperatively-set `TreeViewItem.ItemsSource` snapshotted the placeholder-only collection — fixed by `RebindChildren` (null-then-reassign after the lazy build). **Verify against a LARGE real device (≥ 20 services), not just a trivial one** (Epic-4 Action L).

6. **(AC-6.1.6 — incremental SCPD stream, FR-012 + FR-100)** Expanding a service whose SCPD has not been fetched: actions appear one-by-one (or in small batches) for large SCPDs — the UI does **not** freeze, and the chevron does **not** collapse during the stream.

7. **(AC-6.1.7 — cold large-SCPD ≤ 2 s, Performance Budget)** The cold large-SCPD expand completes within **≤ 2 s** on the test baseline (stopwatch or video against a 100+-action SCPD, e.g. an IGD router's `WANIPConnection` or a large OpenHome service). Record the measured time.

8. **(AC-6.1.8 — actions are leaves, FR-044 second consequence + AC-A1.3)** An action node renders **NO** expand chevron (actions carry no placeholder by design).

### Row migration / identity (FR-054 / AC-6.4 / NFR-P5)

9. **(AC-6.1.9 — in-place migration preserves identity, FR-054 + AC-6.4)** When a re-announce changes a device's friendly name (firmware update, or simulate), the row migrates to its new sorted position **in place** — **selection, expansion, and scroll position are preserved**. Mechanism: `IdentityKeyedSortedCollection` keyed on the **UDN string** (`vm => vm.Udn`, `OrdinalIgnoreCase` — Amendment A30) emits a single `Move(oldIndex, newIndex)`, **never Remove+Add** (`IdentityKeyedSortedCollection.cs` L116). Sibling subtrees are **NOT** redrawn — no flash (NFR-P5 visible to the eye).

### Popups — A31 z-order / lifetime (FR-046, as amended by A31)

10. **(AC-6.1.10 — all four popups open-on-top, AC-10.1 RETAINED)** Open all four popup types in sequence and confirm each appears **above** the main window when shown:
    - **Properties** — right-click a device → "Properties…" (`OpenPropertiesCommand`).
    - **Invocation** — **double-click an action row** (`OnTreeDoubleTapped` → `OpenInvocationPopupCommand`; only fires for `ActionNodeViewModel`).
    - **Subscription** — right-click a service → "Subscribe" (`SubscribeCommand`).
    - **Diagnostics** — **View** menu → "Diagnostics" (`OpenDiagnosticsCommand`).
    Each opens via `window.Activate()` then `WindowOwnershipManager.Adopt(window, shell)`.

11. **(AC-6.1.11 — free z-order, AC-10.4 SUPERSEDED by A31)** With a popup open, click the main window for focus → the **main window comes forward over the popup** (popups float in normal z-order). Re-click the popup → it comes forward. **This is the A31 NEW behaviour. The old "does NOT push the popup behind it" claim is REMOVED — do not assert it.**

12. **(AC-6.1.12 — independent minimise, AC-10.3 SUPERSEDED by A31)** With popups open, minimise the main window → popups do **NOT** minimise with it (they are independent windows). Restoring the main window does **NOT** restore them in lockstep. **Mark the old "minimise/restore together" AC as N/A (superseded). Verify the independent behaviour holds and is clean (no orphan/ghost windows).**

13. **(AC-6.1.13 — close-with-parent, AC-10.2 RETAINED)** With all four popups open, **close the main window** → every popup closes automatically (explicit per-child `parent.Closed` handler). There is **no exception, no error dialog, no leftover window in the taskbar**.

### SSDP log (FR-055 / NFR-UI4)

14. **(AC-6.1.14 — burst with no dropped frames, NFR-UI4 + FR-101)** Under a chatty network burst (≥ 20 adv/s for ≥ 30 s), the virtualised SSDP log (`ItemsRepeater`) renders with no visible dropped frames.

15. **(AC-6.1.15 — smart auto-follow, FR-055)** While bursting: scrolling away from the top does **NOT** yank back to top; scrolling back to the top **re-engages** auto-follow. (View mechanics live in `MainWindow.xaml.cs` "Smart auto-follow" handlers — Pattern-13 documented exception.)

### WinUI conformance + report (NFR-UI1)

16. **(AC-6.1.16 — WinUI 3 conformance, NFR-UI1)** Reviewed side-by-side with WinUI 3 design guidelines: typographic hierarchy, spacing, and colour broadly conform ("considered", not pixel-perfect). Any deliberate deviation (e.g. dense layout for a developer audience, 11 px secondary line) is documented as a conscious choice, not an oversight.

17. **(AC-6.1.17 — verification report exists)** On close, a verification report exists (Markdown under `docs/verification/`, or as this story's completion note) capturing: which devices were used; which behaviours were observed; screenshots/video for any non-obvious behaviour; any defects found and their resolutions. **The report explicitly cites each AC above with PASS / FAIL / N/A**, and explicitly records the **A31 supersession** of AC-10.3 / AC-10.4.

## Tasks / Subtasks

- [ ] **Task 0 — Build + launch against the live LAN** (AC: all)
  - [ ] Build a fresh `ohSpy.App` (`dotnet build` / run from VS); confirm 0 new warnings (pre-existing `WMC1506` benign).
  - [ ] Connect to the dev LAN with **10–20 announcing UPnP devices**, including the **Linn/OpenHome** kit (large device, ≥ 20 services) and at least one **IGD router** (100+-action SCPD). Use the **View → Network adapter** menu to bind to the Linn-DS network if multi-homed (supersedes the old Action-I override).
  - [ ] Record device inventory (names, types, host:port) for the report.

- [ ] **Task 1 — Device tree rendering checklist** (AC: 1, 2)
  - [ ] Verify glyph + primary + muted secondary line on every device row; screenshot one representative row.
  - [ ] Trigger / wait for a re-announce; confirm no label flicker (NFR-UI3).

- [ ] **Task 2 — Chevron + lazy-load checklist** (AC: 3, 4, 5, 6, 7, 8)
  - [ ] Confirm chevron present on a freshly-appeared device row **before** clicking (AC-6.1.3).
  - [ ] Expand a **large** device (≥ 20 services); watch for "Loading…" → atomic replace; **chevron must not flicker** (regression watch `4d380f8` / `a55ed74`).
  - [ ] Expand a **100+-action** service; observe incremental streaming, no freeze, no chevron collapse; **stopwatch/video the cold expand and record ≤ 2 s** (AC-6.1.7).
  - [ ] Confirm an action row shows **no** chevron (AC-6.1.8).

- [ ] **Task 3 — Row migration / identity checklist** (AC: 9)
  - [ ] Select + expand a device, scroll so it's mid-list. Cause its friendly name to change (firmware re-announce, or restart the device emitter with a new name). Confirm the row **moves in place** with selection/expansion/scroll preserved and **no sibling-subtree flash**. Video recommended (non-obvious behaviour).

- [ ] **Task 4 — Popup z-order / lifetime checklist (A31)** (AC: 10, 11, 12, 13)
  - [ ] Open all four popups in sequence (Properties / Invocation / Subscription / Diagnostics) per the exact routes in AC-6.1.10; confirm each opens **on top**.
  - [ ] **A31 free z-order:** click the main window → it comes forward over the popup; re-click popup → popup forward (AC-6.1.11). **Record as the A31 NEW behaviour.**
  - [ ] **A31 independent minimise:** minimise the main window → popups stay (do NOT minimise); restore → they don't restore in lockstep (AC-6.1.12). **Mark old AC-10.3 N/A (superseded).**
  - [ ] **Close-with-parent:** close the main window → all popups close, no exception, no taskbar orphan (AC-6.1.13).

- [ ] **Task 5 — SSDP log checklist** (AC: 14, 15)
  - [ ] Drive a ≥ 20 adv/s burst for ≥ 30 s (chatty network or emitter farm); confirm no dropped frames (AC-6.1.14).
  - [ ] Scroll away from top during burst → no yank-back; scroll back to top → auto-follow re-engages (AC-6.1.15).

- [ ] **Task 6 — WinUI conformance review** (AC: 16)
  - [ ] Side-by-side against WinUI 3 design guidelines; note any deliberate deviations.

- [ ] **Task 7 — Write the verification report** (AC: 17)
  - [ ] Create `docs/verification/6.1-manual-ui-verification-<yyyy-MM-dd>.md` (create the `docs/verification/` folder; none exists yet) using the template in Dev Notes.
  - [ ] Cite every AC PASS / FAIL / N/A; record the **A31 supersession** of AC-10.3 / AC-10.4 explicitly.
  - [ ] Attach screenshots/video for non-obvious behaviours (row migration, A31 free z-order, chevron-no-flicker).

- [ ] **Task 8 — (Only if a defect is found) small fix** (AC: the failing AC)
  - [ ] If the walkthrough finds a polish defect, fix it **minimally** under this story. If reproducible headlessly, add a Core regression test (and keep `Core -warnaserror` 0/0, run the full suite). If App-render-only (the common case — see the four WinUI memories), document the fix + manual re-verification; there is no App test project.
  - [ ] Re-run the affected checklist item; update the report PASS.
  - [ ] Do **not** expand scope beyond the defect.

## Dev Notes

### What this story is (framing)

A verification-only story (Epic 6 delivers no new FRs). The "implementation" is: run the real app on a live LAN, walk the checklist, write the report. The only code that may be written is a **minimal fix** for a defect the eye catches (Task 8). Treat the report as the primary artefact.

### Shipped behaviour — verified against current `main` (reconcile, don't trust stale epic prose)

These are confirmed in the shipped code so the verifier knows what *correct* looks like and doesn't log shipped behaviour as a defect:

- **Four popup routes** (all confirmed in `MainWindow.xaml` + code-behind):
  - Properties → right-click device → "Properties…" → `OpenPropertiesCommand` (`MainWindow.xaml` L91-92).
  - Subscription → right-click service → "Subscribe" → `SubscribeCommand` (`MainWindow.xaml` L130-131).
  - Diagnostics → **View** menu → "Diagnostics" → `OpenDiagnosticsCommand` (`MainWindow.xaml` L40).
  - Invocation → **double-click an action row** → `OpenInvocationPopupCommand` (`MainWindow.xaml.cs` `OnTreeDoubleTapped` L142-153; fires only for `ActionNodeViewModel`).
- **A31 popup z-order/lifetime** — `WindowOwnershipManager.cs` is shipped WITHOUT the Win32 owner link: no `SetWindowLongPtr`/`GWLP_HWNDPARENT`; popups open-on-top via `child.Activate()`; close-with-parent is an explicit per-child `parent.Closed` handler; popups float in free z-order and do not minimise/restore with the shell. **This is the #1 reconciliation — see the A31 table above.**
- **Services sort alphabetically** by URN domain then service name (`ServiceNodeViewModel.cs` L60-66, `OrdinalIgnoreCase`); **actions sort alphabetically** by action name (`ServiceNodeViewModel.cs` `InsertSorted` L148-157). Commit `d8bcfef` (2026-06-04). The tree is **NOT** in XML order — do not log "why aren't these in document order" as a defect.
- **Chevron / Loading placeholder** — `DeviceNodeViewModel` ctor adds `LoadingPlaceholderViewModel` (AC-A1.1, forces the chevron); `ReplaceWith` does a single atomic Reset (AC-A1.4). Two real bugs were fixed here and are **regression watches**: `4d380f8` (chevron never rendered children — expand was a no-op) and `a55ed74` (large-device "Loading…" stuck — `TreeViewItem.ItemsSource` snapshot; fixed by `RebindChildren` null-then-reassign in `MainWindow.xaml.cs` L94-132).
- **Row migration / identity (FR-054)** — `DeviceTreeViewModel.Devices` is an `IdentityKeyedSortedCollection<string, DeviceNodeViewModel>` keyed on `vm => vm.Udn` (the **UDN string**, Amendment A30, `OrdinalIgnoreCase`). A key-changing update emits a single `Move(oldIndex, newIndex)` — **never Remove+Add** (`IdentityKeyedSortedCollection.cs` L116, comment: "the FR-054 invariant (selection/expansion preservation) depends on this").
- **Secondary-detail styling (FR-051 / NFR-UI2)** — `MutedForegroundBrush` = `#FF767676` (`App.xaml` L14), 11 px (`MainWindow.xaml` L115-116).
- **SSDP log (FR-055 / FR-101)** — virtualised `ItemsRepeater` (`MainWindow.xaml` L193); smart auto-follow handlers in `MainWindow.xaml.cs` (Pattern-13 documented exception).
- **Diagnostics viewer is the 4th popup** (Story 5.1, shipped) — virtualised live ring, 6 columns, severity colour, runtime severity gate via `IDiagnosticLevelGate`.

### WinUI render hazards to specifically look for (the polish-defect classes this story exists to catch)

These are the "integration tests passed but the eye catches" defect classes — the four durable memories. The verifier should be primed to spot them; a recurrence is a Task-8 fix:

1. **Null TreeView container `DataContext` + `ItemsSource` snapshot** (MEMORY `winui-treeview-datacontext-null`; commits `4d380f8`, `a55ed74`) — chevron missing, or "Loading…" never replaced on a large device. **Smoke a ≥ 20-service device, not a trivial one.**
2. **Classic `{Binding}` to a struct DataContext** (MEMORY `winui-no-struct-databinding`; commit `63e2378`) — access-violation/crash rendering, e.g. the subscription popup's `KeyValuePair` properties (fixed via typed `EventProperty`/`PropertyRows` + x:Bind). Watch the Subscription popup's property rows render.
3. **Off-thread VM mutation after `await`** (MEMORY `winui-no-synccontext-marshal-vm`; Story 3.2) — `RPC_E_WRONGTHREAD` crash on live NOTIFY / async continuations. Watch the Subscription + Diagnostics popups under live event traffic.
4. **`x:Bind`+`{StaticResource}` converter won't compile under a `Window` root; `ItemsRepeater` leaves realized `DataContext` null** (epic-5-retro headline; Story 5.1 two P1s) — manifests as dead converters/filters (e.g. severity colour not applied, every row default foreground). Watch the **Diagnostics** viewer's severity colours and the SSDP log rows render correctly.

### Test devices required

- 10–20 announcing devices on the dev LAN.
- At least one **Linn/OpenHome** device (large — ≥ 20 services — exercises the chevron/snapshot class and the cold-expand budget).
- At least one **IGD router** (100+-action SCPD — exercises FR-100 incremental stream + the ≤ 2 s budget).
- An **event-emitting** service (Linn Ds/Product, Volume, Playlist) for the Subscription popup live-NOTIFY hazard checks.
- A way to **change a device's friendly name at runtime** (firmware re-announce or a re-named emitter) for the FR-054 migration check, and a way to **burst SSDP** ≥ 20 adv/s for the log checks (chatty network or a `FakeUpnpDevice` farm).

### Verification report template (Task 7)

Create `docs/verification/6.1-manual-ui-verification-<yyyy-MM-dd>.md`:

```markdown
# Story 6.1 — Manual UI Verification Report

- **Date:** <yyyy-MM-dd>
- **Verifier:** <name>
- **Build / commit:** <git sha>
- **Adapter / network:** <adapter name, IPv4, LAN description>

## Test devices
| Friendly name | deviceType tail | host:port | Role in verification |
|---|---|---|---|
| ...           | ...             | ...       | large OpenHome / IGD / event-emitter / ... |

## AC results
| AC | Behaviour | Result | Evidence / notes |
|----|-----------|--------|------------------|
| 6.1.1  | Row composition (glyph+name+muted secondary)         | PASS/FAIL/N/A | screenshot |
| 6.1.2  | No flicker on re-announce                            | PASS/FAIL/N/A | |
| 6.1.3  | Persistent chevron before click                      | PASS/FAIL/N/A | |
| 6.1.4  | "Loading…" placeholder                               | PASS/FAIL/N/A | |
| 6.1.5  | Chevron no flicker during load (large device)        | PASS/FAIL/N/A | regression watch 4d380f8/a55ed74 |
| 6.1.6  | Incremental SCPD stream, no freeze                   | PASS/FAIL/N/A | |
| 6.1.7  | Cold large-SCPD expand ≤ 2 s                         | PASS/FAIL/N/A | measured: __ s |
| 6.1.8  | Action node = leaf (no chevron)                      | PASS/FAIL/N/A | |
| 6.1.9  | In-place row migration preserves identity            | PASS/FAIL/N/A | video |
| 6.1.10 | All four popups open-on-top (AC-10.1)                | PASS/FAIL/N/A | |
| 6.1.11 | A31 free z-order — main clicks forward (was AC-10.4) | PASS/FAIL/N/A | A31 NEW behaviour |
| 6.1.12 | A31 independent minimise (old AC-10.3)               | PASS/N/A      | **SUPERSEDED by A31** |
| 6.1.13 | Close-with-parent (AC-10.2)                          | PASS/FAIL/N/A | |
| 6.1.14 | SSDP burst — no dropped frames                       | PASS/FAIL/N/A | |
| 6.1.15 | Smart auto-follow respected                          | PASS/FAIL/N/A | |
| 6.1.16 | WinUI 3 conformance ("considered")                   | PASS/FAIL/N/A | deviations noted |

## Amendment A31 note
AC-10.3 (minimise/restore-together) and AC-10.4 (no-push-behind) from the original
FR-046 / Decision-10 spec are **SUPERSEDED by Amendment A31** (popups float in free
z-order; no Win32 owner link). They are recorded N/A / re-specified above — the app
exhibits the deliberate post-A31 behaviour, not the original.

## Defects found & resolutions
| # | AC | Description | Resolution (commit / deferred) |
|---|----|-------------|-------------------------------|

## Conclusion
<overall PASS for the L&L demo, or list of must-fix items>
```

### Project Structure Notes

- No production source files are expected to change unless Task 8 fires. If a fix is needed, it lands in `src/ohSpy.App/` (App-render defect — the common case, no App test project) or `src/ohSpy.Core/` (with a regression test) — follow the established Pattern-13 (code-behind only for view mechanics) and the marshalling discipline (`IUiDispatcher.Post`, `DeferredUiDispatcher`-guarded test) if any Core VM path is touched.
- The report folder `docs/verification/` does **not** exist yet — create it. (No `docs/` tree exists in the repo today; this is the first.)
- Gates if any code changes: `Core -warnaserror` 0/0; full suite green (baseline 553 passed / 2 skipped at Epic-5 close); App build 0/0 bar the pre-existing benign `WMC1506`; pre-commit chaos hook green.

### References

- [Source: epics.md#Story 6.1: Manual UI Verification — FR-044 / FR-046 / FR-054 Behaviours] (epic ACs — **z-order/minimise ACs are STALE, see A31**)
- [Source: architecture.md#Amendment A31 — Popups float in free z-order; no Win32 owner link (FR-046 / Decision 10 revision)] (the authoritative popup behaviour; supersedes Decision 10 AC-10.1/10.3/10.4)
- [Source: architecture.md#Amendment A30 — Device identity is the UDN string] (FR-054 identity = UDN string, OrdinalIgnoreCase)
- [Source: prd.md#FR-044] persistent chevron / "Loading…" placeholder; [#FR-045] kind glyphs; [#FR-051] secondary detail line; [#FR-054] case-insensitive sort + stable identity; [#FR-055] newest-first + smart auto-follow; [#FR-100] incremental SCPD; [#NFR-UI1..UI4]
- [Source: src/ohSpy.App/Windowing/WindowOwnershipManager.cs] (A31 shipped — no owner link, explicit `parent.Closed`)
- [Source: src/ohSpy.App/MainWindow.xaml + MainWindow.xaml.cs] (popup routes, chevron/RebindChildren, smart auto-follow)
- [Source: src/ohSpy.Core/Collections/IdentityKeyedSortedCollection.cs] (Move-not-Remove+Add FR-054 invariant)
- [Source: src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs] (alphabetical service+action sort, commit `d8bcfef`)
- [Source: epic-5-retro-2026-06-05.md#Epic 6 preparation] (verification-only; new Window-root/ItemsRepeater gotcha belongs on this checklist)
- MEMORY: `smoke-per-ui-story`, `winui-treeview-datacontext-null`, `winui-no-struct-databinding`, `winui-no-synccontext-marshal-vm`

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
