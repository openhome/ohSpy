# PRD Quality Review — ohSpy

## Overall verdict

This PRD is unusually strong for its stakes — a parity-plus internal dev tool that doubles as a lunch & learn substrate. The FR set is concrete, testable, and traceable to UDA 1.0 section numbers; the three quality bars are translated into bounded NFRs with named enforcement mechanisms; scope honesty is exemplary. The principal risks are minor: one Vision sentence ("behaves like one of them") that drifts toward atmosphere, a handful of mechanical ID/cross-reference imperfections (FR-053 cites a non-existent FR-022 in (a); the "FR-XXX-not-present" gap-sentinel reads as broken when it is meant as a guidance note), and a Glossary that omits a few terms (`DescriptionFetchState`, `BoundedObservableCollection`, `LinnDS`) used downstream. None block green-light to architecture.

## Decision-readiness — strong

The PRD makes the load-bearing trade-offs visible. FR-053's three-layer enforcement names *why* each layer is needed (search-target, NOTIFY filter, fetch-time backstop) rather than picking one and hiding the rest. FR-043's "mismatched-root backstop" walks through five separate consequences honestly — including cancellation on registry exit and re-fetch after byebye — instead of paving them over with "system handles edge cases." The §8.2 Out-of-Scope list is doing real work (settings persistence, hover tooltip, rich event interpretation, enumerated dropdowns, IPv6, multi-NIC merging, a11y, live-device smoke tests) and each entry names *why* it's deferred or who could revisit it. Counter-metrics §SM-C1–C3 explicitly name the temptations to resist ("Do not optimise for feature count", "Do not optimise for benchmark microscores at the cost of perceived smoothness", "Do not optimise for talk polish at the cost of artifact honesty") — these are real anti-goals, not hedges.

Open Questions §10 are genuinely open: per-request HTTP timeout values (§10.1) defer to the architecture phase with a concrete proposed default; SSDP log filtering (§10.2) honestly says "does not commit"; typed-input expansion (§10.3) narrows the residual gap precisely after FR-102/103 absorbed the enumerated and ranged cases.

No findings.

## Substance over theater — strong

The PRD avoids the four classic theater modes. **Personas:** explicitly skipped ("BMM lane, not WDS lane" — §8.2 brief carry-forward), with the operator narrative §2.3 doing only the work it actually needs to do. **Differentiation:** §0 names "the supported successor to Intel's Device Spy" as the entire moat — no manufactured novelty section. **NFRs:** every NFR cites either a numeric bound or a forbidden mechanism (NFR-P3 forbids `.Result` / `.Wait()` by name; NFR-P6 names the SemaphoreSlim cap; NFR-P5 forbids rebuild-on-change patterns). **Vision §1:** mostly load-bearing — "terse, dense, fast, no tutorials, no hand-holding" maps to NFR-UI1/UI4 and to the §2.2 non-users list.

The one piece that drifts: §1's "behaves like one of them" anthropomorphism is stylistic furniture. Minor — it sets the L&L tone — but it does not earn its keep against the rubric.

### Findings
- **low** Vision sentence flirts with atmosphere (§1, "behaves like one of them: terse, dense, fast, no tutorials, no hand-holding") — the four adjectives that follow are real (mapped to NFR-UI1, NFR-UI4, §2.2), but the framing reads as voice rather than commitment. *Fix:* keep it; the L&L substrate purpose justifies a slightly more written tone. No change needed unless tightening for the architecture phase.

## Strategic coherence — strong

The thesis is stated cleanly and the features serve it. §0 names two coupled goals: (a) replace Device Spy with something Linn engineers will daily-use, (b) be the substrate for a spec-driven AI development demonstration. Every §4 feature serves (a); the §0 + §1 + Glossary + Document Purpose framing serves (b). MVP-shape per the rubric is "problem-solving + experience" hybrid — and §8.1 names exactly that scope ("All four core capabilities — Discover, Browse, Invoke, Subscribe — at parity with UpnpSpy" + the three quality bars + diagnostic logging + single-adapter operation + the Properties window).

Feature prioritization follows from the thesis: the two named UpnpSpy complaints get their own FRs (FR-100, FR-101) and their own success metric (SM-2). Counter-metrics name the failure modes that would betray the thesis. §SM-5 (the L&L lands) validates the second goal; §SM-6 (artifact coherence) validates the methodology claim.

The only soft spot: §SM-2's "Verified by eye in a chatty SSDP environment" — coherent with the §6 budget table's "Sustained chatty-SSDP target: ≥ 20 advertisements/sec for ≥ 30 seconds without visible dropped frames" but the "by eye" language reads as informal for a primary metric. The §6 budget makes it measurable; SM-2 should point at the §6 entry rather than describe its own informal version.

### Findings
- **low** SM-2 reads as informal "by eye" when §6 provides a measurable form (§9.SM-2, "Verified by eye in a chatty SSDP environment"). *Fix:* one sentence: "Operationalised via the §6 chatty-SSDP target (≥ 20 advertisements/sec for ≥ 30 s, no visible stutter, no full-pane repaints)."

## Done-ness clarity — strong

This is the dimension where the PRD outperforms expectations. Almost every FR carries explicit **Consequences (testable)** bullets that name what "done" looks like at the engineer's bench. Sample audit:

- **FR-004:** three consequences naming search target, single-adapter constraint, teardown-and-rebuild on switch.
- **FR-043:** seven consequences covering success, failure, parallelism, no-refetch, fresh-fetch-after-byebye, cancellation, and mismatched-root backstop — including the diagnostic fields to record.
- **FR-100:** named numeric ceiling ("no UI-thread freeze longer than the no-blocking budget", "100-action SCPD"), and a behavioural promise ("first action visible promptly").
- **FR-102, FR-103:** four+ consequences each, including the malformed-input fallback and mutual-exclusion tiebreak — exactly the corners stories will need.
- **FR-051:** specifies the exact separator (middle-dot), the brush class (secondary foreground), and the field provenance (eager-fetch).

The Performance Budgets §6 table is doing exactly the work the rubric asks for — bounds, not adjectives. "≤ ~7 s total (5 s MX + ≤ 2 s eager fetch)" decomposes the budget into its components; the "Scale ceiling" row gives an 8-hour figure ("< 200 MB resident") rather than "low memory usage".

Soft spots are local:
- **FR-009, FR-011, FR-019, FR-020** are short and lean on the surrounding context (FR-047, FR-013) rather than carrying their own consequences. Reasonable, but a downstream reader pulling FR-009 in isolation gets "Each device row is labelled with the device's friendly name" and one MUST-NOT-appear sentence — they have to chase FR-047 to learn the loading semantics.
- **NFR-UI4** ("no dropped frames visible to the eye") is the kind of adjectival NFR the rubric flags — it's softened by NFR-P5 and the §6 test baseline, but standing alone it doesn't quite cash out.

### Findings
- **low** Some "primary" FRs lean on neighbours for testability (FR-009, FR-011, FR-019, FR-020) — readable in context, less so pulled alone. *Fix:* leave as-is unless downstream story creation finds itself dereferencing repeatedly; the cross-references resolve.
- **low** NFR-UI4 is adjectival ("no dropped frames visible to the eye") (§5.3). *Fix:* point at the §6 test baseline's "main-thread stalls > 16 ms" line as the operational measure.

## Scope honesty — strong

Non-Goals §7 are explicit and load-bearing — they pre-empt the most likely "what about X?" pushbacks (cross-platform, IPv6, multi-NIC merging, persona work, typed inputs, adversarial fuzzing, a11y, public distribution). Each line says *why* it's out.

`[ASSUMPTION]` tags appear inline (§4.5 SSDP filtering, §4.9 free-form text default, §4.10 raw event display, §5.2 NFR-P2 timeout defaults) and are mirrored in the §11 Assumptions Index — roundtrip clean except for one item: the §11 entry "§5.3 NFR-UI1" appears in the Index but is not flagged inline at the NFR-UI1 location as `[ASSUMPTION]`. Minor.

Open Questions §10 has seven entries — appropriately calibrated for a parity-plus PRD where most of the discovery work was done in the brief/UpnpSpy and the residual unknowns are concentrated (timeout values, deferred features, distribution decision).

No `[NOTE FOR PM]` callouts appear inline. The PRD's compressed-UJ format and the brief's pre-decided scope mean there are few PM-level tensions to call out; the §SM-C1–C3 counter-metrics arguably substitute. Acceptable.

### Findings
- **low** §11 Assumptions Index entry for NFR-UI1 ("'Modern WinUI conventions' anchors to WinUI 3 design guidelines") is not paralleled by an inline `[ASSUMPTION]` tag in §5.3 (§11 ↔ §5.3 NFR-UI1). *Fix:* add `[ASSUMPTION]` inline at NFR-UI1 or drop the Index entry — Index/inline roundtrip should be exact.

## Downstream usability — adequate

The PRD will feed architecture, stories, and (per §0) a live walkthrough. It is mostly extract-friendly:

- **Glossary** is present and well-curated (12 terms with UDA 1.0 section anchors).
- **FR IDs** are unique, cross-references resolve.
- **Sections read alone** for the most part — each §4.x has its own Description paragraph and the FRs cite each other via stable IDs.

Soft spots:

- **Glossary drift:** Several domain nouns used in FRs are not in the Glossary — `DescriptionFetchState` / `Loaded` / `Pending` / `InFlight` / `Failed` (FR-047, FR-043), `BoundedObservableCollection` (only in addendum.md; ohSpy PRD avoids it — good), `LinnDS` (§4.10 notes), `BOOTID.UPNP.ORG` / `CONFIGID.UPNP.ORG` (FR-052). These are partly intentional (state names are implementation detail; UPnP-literate readers don't need `BOOTID` re-defined), but a downstream story author pulling FR-047 in isolation needs to know what the `DescriptionFetchState` enum's values mean.
- **FR-053 cross-reference fault:** §FR-053 (a) cites "FR-004, FR-022" — FR-022 is the rescan FR ("Rescan uses identical M-SEARCH semantics"), so the citation works only by transitivity. The intent reads cleanly but the citation chain is loose.
- **Gap-sentinel "FR-XXX-not-present"** at §4 intro reads like a broken placeholder on a casual scan ("Gaps in the FR-XXX number sequence (notably FR-XXX-not-present) reflect deletions…"). A standalone reader at the L&L will not parse the meta-comment. Reword.
- **UJs are deliberately downscaled** per §2.3 and the decision log — correct per Shape fit; no finding.
- **Cross-references** mostly use ID citations rather than "see above" — good.

### Findings
- **medium** Gap-sentinel sentence reads as broken text (§4 intro, "Gaps in the FR-XXX number sequence (notably FR-XXX-not-present) reflect deletions in the upstream spec during its own iteration"). A live L&L audience or new reader will read `FR-XXX-not-present` as a TODO placeholder rather than a legitimate sentinel. *Fix:* "Gaps in the FR-NNN number sequence (e.g. there is no FR-068 between FR-067 and FR-069, were one to look) reflect deletions in the upstream spec and are preserved as-is" — or simply name a concrete missing ID.
- **medium** `DescriptionFetchState` enum values (`Pending`, `InFlight`, `Loaded`, `Failed`) appear in FR-043 and FR-047 without Glossary entries. Stories will refer to these by name. *Fix:* add one Glossary entry: "**DescriptionFetchState** — per-device fetch lifecycle: Pending → InFlight → (Loaded | Failed). Tree visibility gated on Loaded (FR-047)."
- **low** FR-053 (a) cites FR-004 and FR-022, but FR-022 is downstream of FR-004 and cites it transitively (§4.1 FR-053(a)). *Fix:* drop FR-022 from the citation — FR-004 carries the constraint and FR-022 inherits it.

## Shape fit — strong

The PRD knows what shape it is. §0 names it explicitly ("internal Linn-only dev tool … this PRD is also part of a Linn-internal advocacy demonstration"); §2.3 explicitly downscales UJs per `bmad-prd` guidance for single-operator tools; the decision log records this as a conscious call. Capability-spec shape is what the rubric prescribes for this product type, and the PRD delivers it: FR-heavy, scenarios-compressed, operational metrics rather than user-facing ones.

The L&L-substrate requirement adds a "readable standalone" obligation that the PRD honours via the Glossary, the per-section Description paragraphs, and the Operator Narrative §2.3. The Glossary anchors UDA 1.0 section numbers so a Linn audience that isn't uniformly UPnP-literate has the reference inline.

The brownfield dimension is partially in play (FR-001..FR-055 lifted from UpnpSpy), and the PRD handles this explicitly: §0 names the lift, §4 intro names the ID-preservation policy, FR-100/101/102/103 distinguish new work, and the decision log §2026-05-30 records the source-of-truth choice.

No findings.

## Mechanical notes

- **Glossary drift:** `DescriptionFetchState` enum values missing (see Downstream finding). `Loaded` is used as a state name in §8.1 ("`DescriptionFetchState == Loaded`") and in the §4.3 description. `friendlyName` vs `<friendlyName>` vs "friendly name" appear in three forms — consistent with their respective referents (camelCase for the XML element when discussing the protocol, prose form when discussing the rendered label) but worth checking once before story creation.
- **ID continuity:** FR-001..FR-055 lifted from UpnpSpy. Missing IDs in the visible FR list (no FR-046 cited — actually FR-046 IS present, in §4.14; no FR-037 issues — present in §4.13). Cross-references resolve (spot-checked FR-004, FR-005, FR-006, FR-008, FR-013, FR-017, FR-018, FR-021, FR-033, FR-037, FR-039, FR-041, FR-043, FR-044, FR-046, FR-047, FR-048, FR-049, FR-050, FR-051, FR-052, FR-053, FR-055, FR-100, FR-101, FR-102, FR-103). No duplicates.
- **Assumptions Index roundtrip:** 7 inline `[ASSUMPTION]` tags vs 8 Index entries — the NFR-UI1 Index entry has no inline counterpart (see Scope finding). Otherwise clean.
- **UJ protagonist naming:** N/A by design (compressed operator narrative replaces UJs per §2.3 and decision log).
- **Required sections present:** Vision, Target User, Glossary, Features, NFRs, Performance Budgets, Non-Goals, MVP Scope, Success Metrics, Open Questions, Assumptions Index — all present and load-bearing.
- **Counter-metrics named:** yes (§SM-C1–C3) — uncommon and valuable.
- **Diagnostic field consistency:** FR-039 lists "device UUID, service id, action name, URL, status code, error text"; FR-041's row columns use `device.uuid`, `url`, `remote.endpoint`. Compatible but stylistically split between prose-fields and dotted-key naming — worth one pass at story creation time.
