## Document Summary
- **Purpose:** Specify behaviour of ohSpy (Windows UPnP inspector) at FR-level fidelity to feed architecture, story, and sprint phases; double as readable artifact for L&L audience.
- **Audience:** UPnP-fluent Linn engineers building the tool AND wider Linn engineering audience attending an L&L (non-uniformly UPnP-literate).
- **Reader type:** humans (mixed-fluency engineering audience; comprehension aids worth preserving where they bridge fluency gap).
- **Structure model:** Pyramid (Strategic/Context) with embedded Reference (FR catalogue) — the §4 features section is reference-shaped inside an otherwise pyramid document.
- **Current length:** ~7,300 words across 14 feature subsections + 11 top-level sections.

## Recommendations

### 1. CONDENSE — §0 Document Purpose, paragraph 5 (advocacy-demo paragraph)
**Rationale:** Repeats material covered in §1 Vision paragraph 3 ("It is also a deliberate, walk-through demonstration…") almost verbatim; the "artifact quality is co-equal with binary quality" framing is restated again in §5 preamble.
**Impact:** ~80 words
**Comprehension note:** None — kept in §1 where it's better placed.

### 2. CUT — §0 Document Purpose, paragraph 3 ("The PRD is structured as: Vision → …")
**Rationale:** A table-of-contents-in-prose for a document that already has a heading hierarchy and standard section numbers; readers can read the section list itself. This is the precise "preludes that restate the section heading" anti-pattern called out in your scope.
**Impact:** ~70 words
**Comprehension note:** Negligible; the headings serve as their own ToC.

### 3. CONDENSE — §1.1 Risk and Fallback
**Rationale:** Two bullets plus a closing paragraph ("Downstream workflows inherit explicit permission… The brief presumes success. It does not depend on it.") restate the brief's framing. The bullets carry the load; the closing paragraph repeats the bullets' point.
**Impact:** ~50 words
**Comprehension note:** None — bullets retained.

### 4. CUT — §2.3 Operator Narrative meta-sentence ("Per `bmad-prd` guidance, full User Journeys are downscaled…")
**Rationale:** Process meta-commentary aimed at the workflow rather than the reader. The narrative paragraph that follows stands on its own. The closing sentence "Every feature in §4 contributes to that single narrative…" is also process commentary.
**Impact:** ~50 words
**Comprehension note:** None — narrative paragraph remains.

### 5. CUT — §4 preamble sentence about FR-ID gaps ("Gaps in the FR-001..FR-055 sequence reflect deletions in the upstream spec…")
**Rationale:** Useful once for downstream architecture/story authors, but the same statement is implicit from the visible numbering and adds noise on every read.
**Impact:** ~25 words
**Comprehension note:** None of note.

### 6. CONDENSE — §4.1 FR-053 enforcement bullet (c) and §4.2 FR-043 "Mismatched-root backstop" bullet
**Rationale:** True redundancy — the mismatched-root backstop is stated in full in both FR-053(c) and FR-043's Consequences. Pick one canonical site (FR-043, where the fetch lives) and have FR-053(c) reference it: "(c) Mismatched-root backstop — see FR-043."
**Impact:** ~40 words; also removes a maintenance hazard if the rule ever changes.
**Comprehension note:** None — cross-reference preserves discoverability.

### 7. CONDENSE — §4.2 §-Description vs FR-043 Consequences
**Rationale:** §4.2 Description paragraph 1 and FR-043's Consequences block both spell out "hidden until loaded, bounded parallelism, friendly-name population." Description can shrink to: "Description XML is fetched eagerly without user interaction; devices are hidden from the tree until the fetch succeeds. Concurrent fetches are capped." Details belong in the FR.
**Impact:** ~50 words
**Comprehension note:** None — FR-043 Consequences carry the load.

### 8. CONDENSE — §4.3 Description paragraph
**Rationale:** "Each row carries a kind glyph (device/service/action)" duplicates FR-045 verbatim; "every device row carries a secondary detail line beneath the friendly name (deviceType tail + IPv4 host:port)" duplicates FR-051 verbatim. The Description block should set narrative context, not pre-state the FR contents.
**Impact:** ~40 words
**Comprehension note:** None — FRs immediately follow.

### 9. CONDENSE — §4.5 Description paragraph
**Rationale:** "Capped at 10,000 entries with FIFO eviction" = FR-016 verbatim; "auto-follows new arrivals while the operator is parked at the top" = FR-055 Consequences verbatim; "virtualised — chatty networks do not produce visible stutter" = FR-101 verbatim. Description can shrink to one sentence framing the pane.
**Impact:** ~50 words
**Comprehension note:** None.

### 10. CONDENSE — §4.10 Description paragraph
**Rationale:** "Multiple subscription popups across different services can be open simultaneously, each managing its own lifecycle" = FR-036 verbatim; "auto-renews before each device-granted timeout" = FR-038 verbatim; "closing the popup sends UNSUBSCRIBE" = FR-034 verbatim.
**Impact:** ~40 words
**Comprehension note:** None — FRs carry it.

### 11. CONDENSE — §4.9 FR-102/FR-103 parity-plus italic preamble
**Rationale:** The same one-sentence italic preamble appears at the top of both FR-102 and FR-103: "Parity-plus — beyond UpnpSpy parity and beyond the brief's named fixes; agreed during PRD review as a required v1 capability. Rationale in decision log." Lift it once to the §4.9 Description block ("FR-102 and FR-103 are parity-plus additions agreed during PRD review — see decision log.") and remove from each FR.
**Impact:** ~50 words
**Comprehension note:** None — flagged once in the section header.

### 12. MERGE — §4.13 Cross-Interaction Robustness and §4.14 Secondary Window Ownership
**Rationale:** Each holds exactly one FR (FR-037 and FR-046). Two single-FR feature sections inflate the §4 outline. Both concern secondary-window lifecycle. Merge into a single "§4.13 Secondary Window Lifecycle" containing both FRs.
**Impact:** ~15 words and one outline-level entry; preserves both FR IDs and content.
**Comprehension note:** Improves the §4 outline scannability without affecting FR fidelity.

### 13. CONDENSE — §5 preamble
**Rationale:** "Schedule yields to these three — per the brief: 'the lunch & learn happens when the bars are met, not on a fixed date.' A release that misses a quality bar is not a release at all." This repeats the §1 Vision framing and the §0 "artifact quality is co-equal" point. One sentence: "The brief defines three non-negotiable quality bars: Reliability, Performance, UI polish — schedule yields to them."
**Impact:** ~40 words
**Comprehension note:** None — load preserved in one tighter sentence.

### 14. CUT — §6 row "SC-R-30min"
**Rationale:** Restates NFR-R1 in full inside the Performance Budgets table. The other rows are quantitative budgets; this one is a prose paragraph that belongs (and already lives) in §5.1. The table is purer if it stays quantitative.
**Impact:** ~50 words (and tighter table); NFR-R1 retains the same content unchanged.
**Comprehension note:** None — content is unchanged in §5.1.

### 15. CONDENSE — §11 Assumptions Index entries that duplicate inline `[ASSUMPTION]` markers
**Rationale:** Several rows (NFR-P2 timeouts, FR-101 filtering, FR-033 event interpretation, FR-102/103 surface) are already tagged inline as `[ASSUMPTION]` at the FR site. The Assumptions Index value is in being a single page-to-confirm; trim each entry to one short line referencing the FR — drop the re-explanation.
**Impact:** ~80 words
**Comprehension note:** None — assumption flags remain at FR sites; index becomes a true index, not a re-statement.

### 16. PRESERVE — §3 Glossary, §6 Performance Budgets table, §10 Open Questions, all FR IDs/ordering
**Rationale:** Per your scope rules. Glossary is downstream contract; Budgets table rows (other than #14 above) are quantitative and load-bearing; Open Questions are deferral receipts.
**Impact:** 0 words
**Comprehension note:** Mixed-fluency audience particularly benefits from the Glossary.

### 17. PRESERVE — Feature Description blocks (after the §4.x heading, before FRs)
**Rationale:** These provide the narrative scaffolding the L&L audience needs to follow before hitting FR-level detail. Cut the redundancies inside them (recs 8/9/10) but keep the blocks themselves — they are the "warmth/orientation" the wider audience relies on.
**Impact:** 0 words (no removal); positive comprehension impact for non-UPnP-fluent readers.

## Summary
- **Total recommendations:** 17 (12 cuts/condenses, 1 merge, 0 moves, 2 preserves, 2 marker entries)
- **Estimated reduction:** ~730 words (~10% of ~7,300)
- **Meets length target:** No target specified; reduction is modest by design — the PRD's bulk is FR fidelity which is explicitly off-limits.
- **Comprehension trade-offs:** None of substance. The cuts target true redundancies (Description blocks that pre-state FR contents, repeated parity-plus preambles, restatements between §0/§1/§5 of the artifact-quality framing) and outline noise (single-FR feature sections, ToC-in-prose, process meta-commentary). FR text, FR IDs, FR ordering, §-numbering, Glossary, and Performance Budgets quantitative rows are untouched.
