# Technical Fidelity Review — ohSpy PRD

**Reviewer scope:** lift fidelity vs `C:\work\UpnpSpy\specs\001-upnp-spy-discovery\spec.md`; UDA 1.0 §-citation correctness; protocol correctness of ohSpy-specific additions and NFRs; glossary accuracy.

---

## Overall verdict

The PRD is a faithful lift of the UpnpSpy spec with most FR semantics preserved verbatim or compressed without loss. However there are **several UDA 1.0 §-citations that are demonstrably wrong** (most notably `NTS: ssdp:alive` cited as §1.1.2 — UDA 1.0 places `ssdp:alive` in §1.2.2 and `ssdp:byebye` in §1.2.3; FR-006's "§1.1, §1.2" is also imprecise) and one citation in the SOAP-fault FR is mis-located. Protocol claims in the ohSpy-specific FRs are largely correct but FR-103's `<step>` validation and FR-102's "mutual exclusion" claim need tightening against what UDA 1.0 actually mandates.

---

## 1. Lift fidelity vs UpnpSpy spec

Walked every FR-001..FR-055 in both documents. Lift is overwhelmingly clean — the wording compression preserves the testable consequences in nearly every case, and the FR IDs are stable. A small number of weakenings or omissions identified below.

### Findings

- **medium** — *FR-004 lost the "join multicast group on selected adapter" clause* (PRD §4.1 FR-004 / UpnpSpy spec.md FR-004) — The UpnpSpy version says "The probe MUST be sent **and the multicast group joined** on the user-selected adapter". The PRD's FR-004 says only "issues an SSDP M-SEARCH … on the user-selected eligible adapter" — the multicast-group-join is the more critical of the two protocol acts (M-SEARCH is unicast-response-bearing but the listener has to be joined to 239.255.255.250 to receive NOTIFYs). The "tear down SSDP socket and rebuild on switch" is preserved as a Consequence, but the underlying "join the multicast group" act is left implicit. *Fix:* in FR-004 say "issues an M-SEARCH **and joins the SSDP multicast group** on the user-selected eligible adapter".

- **medium** — *FR-053(b) lost the explicit examples of "other NT" values that still log but don't register* (PRD §4.1 FR-053 / UpnpSpy spec.md FR-053) — The original enumerates: "an embedded device's UDN such as `uuid:<udn>`, an embedded device's `<deviceType>` URN, or a service type URN". The PRD reduces this to "other NT values still append to the SSDP log … but do not affect the registry". A reader without the upstream will not know what concrete NTs trigger this branch (which matters at lunch-and-learn time — someone will ask). *Fix:* keep the original parenthetical list of NT examples.

- **low** — *FR-038's "stop attempting to renew" merged into Consequences, not the FR body* (PRD §4.10 FR-038 / UpnpSpy spec.md FR-038) — Upstream the lapsed-renewal rule and the closing-while-lapsed rule are both part of the FR body. The PRD relegates them to "Consequences (testable)" — semantically equivalent and the structure is consistent with the PRD's "FR + Consequences" pattern, so this is structural, not behavioural. No fix required, but flagging in case you want strict parity.

- **low** — *FR-043 omits the citation `UDA 1.0 §2.1` from the mismatched-root backstop* (PRD §4.2 FR-043 / UpnpSpy spec.md FR-043) — Upstream says "the fetched description's root `<UDN>` (UDA 1.0 §2.1) does not match". The PRD drops the §2.1 citation. Same prose, weaker provenance. *Fix:* restore the §2.1 cite on `<UDN>` (and see UDA citation findings below — §2.1 / §2.3 for `<UDN>` is itself worth double-checking).

- **low** — *FR-013 wording is generic but consistent.* PRD says "in or near the affected node"; upstream is the same. Preserved. No issue.

- **low** — *Operator-narrative substitution.* The PRD declares (in §2.3 and the §4 preamble) that User Stories US1–US8 from the upstream spec are compressed into a single operator narrative. This is a deliberate genre shift (PRD vs spec-kit) called out explicitly, not silent drift. No fix.

- **low** — *FR-024 wording match.* Upstream: "A rescan in progress MUST NOT suspend handling of unsolicited alive or byebye advertisements." PRD identical. OK.

- **low** — *FR-051 "never empty for a visible device" is paraphrased but equivalent.* Upstream says "the detail line is never empty for a visible device"; PRD says "never empty for a visible device (FR-047)". OK.

- **low** — *Edge-case content not lifted.* The upstream spec carries a substantial "Edge Cases" enumeration; the PRD does not reproduce it. The PRD does not claim to lift edge cases (only FRs), so this is in-scope omission. No fix; downstream architecture / story phases will need to surface these edges. Worth a one-line forward-reference in the PRD if you want belt-and-braces.

---

## 2. UDA 1.0 citation correctness

UDA 1.0's structure is: §1 Discovery (SSDP), §2 Description, §3 Control (SOAP), §4 Eventing (GENA). Within §1, the canonical sub-structure is §1.1 Discovery: general; §1.2 Discovery: advertisement (alive in §1.2.2, byebye in §1.2.3, update in §1.2.4); §1.3 Discovery: search (M-SEARCH in §1.3.2, ST values incl. `upnp:rootdevice` in §1.3.3 / §1.3.2's ST table).

**Multiple §-citations in the PRD do not match this structure.** This is the highest-value class of finding for a UPnP-fluent audience.

### Findings

- **high** — *`NOTIFY ssdp:alive` cited as UDA 1.0 §1.1.2* (PRD §3 Glossary; PRD §4.5 FR-014) — Upstream UpnpSpy spec also says §1.1.2. Both are wrong. UDA 1.0 places `ssdp:alive` device-advertisement semantics in **§1.2.2** ("Advertisement: Device available — NOTIFY with ssdp:alive"). §1.1 is the general overview; the alive-NOTIFY rules live in §1.2.2. *Fix:* change every `§1.1.2` referring to `ssdp:alive` to `§1.2.2`. Occurrences: glossary `NOTIFY` entry; FR-014.

- **high** — *`NOTIFY ssdp:byebye` cited as UDA 1.0 §1.1.3* (PRD §3 Glossary; PRD §4.1 FR-008; PRD §4.5 FR-015; PRD §4.1 FR-053(b) by inheritance) — Same problem. UDA 1.0 places `ssdp:byebye` in **§1.2.3** ("Advertisement: Device unavailable — NOTIFY with ssdp:byebye"), not §1.1.3. *Fix:* change every `§1.1.3` referring to `ssdp:byebye` to `§1.2.3`. Occurrences: glossary; FR-008; FR-015.

- **high** — *FR-007 "UDA 1.0 §1.1.4, §1.3" for UUID/USN/UDN identity* (PRD §4.1 FR-007) — `§1.1.4` doesn't correspond to USN/UDN content in UDA 1.0. UDA 1.0 covers `USN` as part of the advertisement and search-response header tables (in §1.2.2, §1.2.3, §1.3.3); `UDN` is defined in **§2.1** (Device description / required device element list) where `<UDN>` is enumerated. *Fix:* replace `§1.1.4, §1.3` with `§1.2.2, §1.3.3` for the `USN` header and add a separate `§2.1` cite for `<UDN>` if you want both layers covered. The upstream spec has the same error — opportunity to fix it on lift.

- **high** — *FR-004 / FR-022 cite `ST: upnp:rootdevice` at "UDA 1.0 §1.3.3"* (PRD §4.1 FR-004; PRD §4.8 FR-022) — The M-SEARCH request format and the ST header are defined in **§1.3.2**, with §1.3.3 covering the unicast search response. `upnp:rootdevice` as a valid ST value appears in §1.3.2's table. So `M-SEARCH (§1.2.1)` is also wrong — M-SEARCH is **§1.3.2**, not §1.2.1. The pattern "M-SEARCH … (UDA 1.0 §1.2.1)" appears in FR-004, FR-022, the glossary M-SEARCH entry. *Fix:* change "M-SEARCH (UDA 1.0 §1.2.1)" to "M-SEARCH (UDA 1.0 §1.3.2)" everywhere. The `ST: upnp:rootdevice` value cite should be `§1.3.2` (or `§1.1.2` if you mean the rootdevice search-target identifier table — but conventionally §1.3.2's ST list is what's referenced).

- **medium** — *FR-006 "UDA 1.0 §1.1, §1.2" is vague.* (PRD §4.1 FR-006) — Listening for unsolicited NOTIFY messages should cite **§1.2** (Advertisement) specifically — or §1.2.2 + §1.2.3 if you want to be precise about alive vs byebye. The "§1.1" half is too general to cite. *Fix:* drop "§1.1" and keep "§1.2" (or expand to §1.2.2, §1.2.3).

- **medium** — *FR-029 cites SOAP/UPnP fault at "UDA 1.0 §3.1.3" but the canonical location is §3.2* (PRD §4.9 FR-029) — UDA 1.0 §3.1 is "Control: Action invocation"; §3.2 is "Control: Action response / faults" (or, depending on edition, §3.2 / §3.2.2 cover the UPnP-specific fault envelope `<UPnPError><errorCode/><errorDescription/></UPnPError>`). `§3.1.3` is not where the fault envelope lives in UDA 1.0 as typically referenced. *Fix:* verify against the PDF and change to `§3.2` (and probably `§3.2.2` for the `<UPnPError>` element structure). The upstream spec has the same mis-cite — fix on lift.

- **medium** — *FR-041 mentions FR-052's "BOOTID.UPNP.ORG and CONFIGID.UPNP.ORG (UDA 1.1 §1.2 — present only if the device advertised them)"* (PRD §4.7 FR-052) — `BOOTID.UPNP.ORG` and `CONFIGID.UPNP.ORG` are indeed UDA 1.1 (not 1.0) — that's correctly flagged. Worth saying that in 1.1 they live in §1.2 (advertisement) and §1.3 (search-response) — the bare "§1.2" cite is OK but slightly imprecise. Low priority.

- **low** — *FR-011 "UDA 1.0 §2.1" for `<serviceList>` and FR-012 "UDA 1.0 §2.2, §2.4" for `<actionList>` / SCPD* — §2.1 is Device description (root), §2.2 is Service description (SCPD), §2.4 is Description: non-standard vocabularies. `<serviceList>` lives in §2.1 (correct in FR-011). `<actionList>` lives in §2.2 (correct in FR-012). `<SCPDURL>` is in §2.1 (device description), not §2.2 — the `SCPDURL` URL itself is declared inside the device description's `<service>` element. So FR-012's "(<SCPDURL>, UDA 1.0 §2.2, §2.4)" arguably needs `§2.1` for SCPDURL and `§2.2` for the SCPD content it points at. Minor. *Fix:* split — "fetches the document referenced by `<SCPDURL>` (declared at §2.1) which conforms to §2.2".

- **low** — *FR-032 "SUBSCRIBE UDA 1.0 §4.1.1", FR-034 "UNSUBSCRIBE §4.1.4", FR-038 "renewal §4.1.3 / TIMEOUT §4.1.2"* — UDA 1.0 §4.1 is the eventing model overview, with sub-sections describing SUBSCRIBE (§4.1.2), renewal (§4.1.3), UNSUBSCRIBE (§4.1.4), and the SUBSCRIBE / response message details. `§4.1.1` for SUBSCRIBE itself is on the edge — depending on the edition it can be the model/overview rather than the request. UDA 1.0 has SUBSCRIBE under §4.1.2 in some printings. **Worth double-checking against the PDF** — Simon will know on sight which printing is canonical at Linn. The §4.3 cite for `<e:propertyset>` (FR-033) is correct.

- **low** — *FR-033 NOTIFY for events cited as §4.3* — Correct (UDA 1.0 §4.3 is Eventing: NOTIFY / `<propertyset>`).

---

## 3. Protocol correctness of ohSpy-specific additions

### Findings

- **medium** — *FR-102 "mutual exclusion" claim is not in UDA 1.0 as written* (PRD §4.9 FR-102 last bullet) — The PRD says "`<allowedValueList>` and `<allowedValueRange>` are mutually exclusive per UDA 1.0 §2.3". UDA 1.0 §2.3 (Service description / state variable XML) defines the two as alternatives intended for different `<dataType>` families (lists for string types, ranges for numeric types) but does **not** spell out an explicit MUST-NOT-co-occur rule. The mutual-exclusion is implied by the type system (a single state variable has a single dataType) but isn't a literal §2.3 prohibition. *Fix:* soften to "`<allowedValueList>` applies to string-typed state variables and `<allowedValueRange>` to numeric-typed state variables (UDA 1.0 §2.3); per §2.3's `<dataType>` partitioning the two cannot legitimately co-occur on the same state variable. If a malformed SCPD declares both, FR-102 wins …" — same defensive behaviour, accurate provenance.

- **medium** — *FR-103 `<step>` semantics are stricter than UDA 1.0* (PRD §4.9 FR-103, second consequence) — UDA 1.0 §2.3 defines `<step>` as the granularity hint for the variable's permitted values. The PRD's "submitting a value off-step is rejected client-side with an inline message" is a stronger constraint than UDA 1.0 mandates (devices typically tolerate off-step values, returning a UPnP error 600-series only sometimes). This is fine as ohSpy UX but worth a note that this is a **client-side helpfulness**, not a protocol-conformance check. *Fix:* phrase as "ohSpy MAY reject off-step values client-side as a usability aid; this is stricter than UDA 1.0 §2.3, which describes `<step>` as a granularity hint." Or change "rejected" to "warned about, with submit still possible" if you want pure tool-doesn't-second-guess behaviour. Worth a deliberate call here.

- **medium** — *FR-103 `<defaultValue>` precedence rule* (PRD §4.9 FR-103, third consequence) — UDA 1.0 §2.3 says `<defaultValue>` is the value to use "when the device is reset" and should satisfy declared constraints. The PRD's "if `<defaultValue>` satisfies range (and step), use it; otherwise use `<minimum>`" is a sensible UX choice but is **ohSpy-defined fallback behaviour**, not a UDA mandate. The existing wording is OK but the "where declared" qualifier should also acknowledge that step-snapping a non-conforming default is also an option (some implementations do this). Low priority — defensive default is fine.

- **low** — *FR-100 incremental SCPD parse* — Pure UI/perf claim, no protocol implication. OK.

- **low** — *FR-101 virtualised SSDP log rendering* — Pure rendering claim. OK.

- **high** — *NFR-P2 default timeouts* (PRD §5.2 NFR-P2 + assumption) — "5 s for description fetches and SUBSCRIBE/UNSUBSCRIBE, 10 s for SOAP invocations" — these are inherited from UpnpSpy `plan.md` and are defensible defaults, but **UDA 1.0 §1.3.2 specifies that an M-SEARCH MX value MUST be between 1 and 5 seconds inclusive**, and the **description-fetch budget should be at least MX + a small response window** to avoid racing the device's response. A 5 s description-fetch timeout might be tight for slow embedded devices on the first request after wake. Worth flagging that 5 s is on the aggressive end for description fetch on real-world IGD routers — a common UPnP-test failure mode. *Fix:* consider 10 s description, 5 s SUBSCRIBE/UNSUBSCRIBE, 30 s SOAP (UDA 1.0 doesn't mandate but 30 s aligns with HTTP/1.1 norms for slow devices). Or call out in the assumption that "5 s description" is known-aggressive and to be measured. The exact numbers are deferred to architecture, so this is a flag, not a blocker.

- **low** — *FR-049 TcpListener / no-URL-ACL claim* — Protocol-correct. `HttpListener` on Windows routes through `HTTP.SYS`, which requires URL ACL grants for non-admin users; `TcpListener` uses raw sockets and bypasses `HTTP.SYS` entirely, so no ACL is needed. The hand-parsed HTTP/1.1 surface restriction (request line, headers, `Content-Length`-bounded body) is the right minimal slice — GENA NOTIFY uses POST with `Content-Length` (chunked transfer-encoding is not used by conformant UPnP devices, though a defensive parser may want to log if it sees one). *Possible addition:* mention `TRANSFER-ENCODING: chunked` is out of scope and should be 400'd alongside other malformed framing. Low priority.

- **low** — *FR-049 "slowloris" framing* — Correct terminology; per-request read timeout is the right defense. OK.

- **low** — *FR-048 multicast-capable / non-loopback / IPv4 eligibility* — Correct; this matches the SSDP IPv4-only scope per UDA 1.0 (which defines IPv4 multicast `239.255.255.250:1900`; IPv6 was added in UDA 1.1).

- **low** — *NFR-P3 "no `.Result` / `.Wait()`"* — Implementation guidance; not a protocol claim. OK.

- **low** — *NFR-P5 / NFR-P6* — UI/perf; no protocol claim. OK.

---

## 4. Glossary correctness

The glossary is mostly clean, with the citation errors flagged in §2 above being the main issue.

### Findings

- **high** — *Glossary `NOTIFY` definition cites §1.1.2 / §1.1.3* — Same error as flagged under §2. *Fix:* `ssdp:alive` → §1.2.2; `ssdp:byebye` → §1.2.3.

- **medium** — *Glossary `M-SEARCH` cites §1.2.1* — Same error as flagged under §2. *Fix:* §1.3.2.

- **medium** — *Glossary `SCPD` — "XML document at a service's `<SCPDURL>`"* — Correct in substance. `<SCPDURL>` is declared in the **device description's** `<service>` element (UDA 1.0 §2.1), not in the SCPD itself; the SCPD content/schema is §2.2 / §2.3. The current cite "UDA 1.0 §2.2, §2.4" is OK if §2.4 means the schema reference; §2.4 in UDA 1.0 is usually "Description: Augmentations" / non-standard vocabularies, which is not strictly what an SCPD is. *Fix:* cite §2.2 (SCPD content) and §2.3 (state variable XML) — drop §2.4.

- **low** — *Glossary `SSDP` cites "UDA 1.0 §1"* — Acceptable as a chapter-level reference; arguably could be more precise (§1 overview + §1.2 advertisement + §1.3 search) but a single chapter cite is fine here.

- **low** — *Glossary `GENA` cites "UDA 1.0 §4"* — Same comment as SSDP. Chapter-level cite is fine.

- **low** — *Glossary `Device` — "Embedded devices are flattened into their root (FR-053)"* — Accurate paraphrase. OK.

- **low** — *Glossary `Service` — "with a SOAP `controlURL`, an event `eventSubURL`, and an SCPD"* — Accurate. `<controlURL>`, `<eventSubURL>`, `<SCPDURL>` are all per-service elements declared inside the device description's `<service>` block (UDA 1.0 §2.1). Could add a §2.1 cite. Low priority.

- **low** — *Glossary `Action`, `Eligible adapter`, `Registry`, `Eager description fetch`, `Callback host`, `Diagnostic entry`* — All accurate paraphrases of the FR semantics. OK.

- **low** — *Glossary `UPnP` cites "UDA 1.0 (`docs/specs/UPnP-arch-DeviceArchitecture-v1.0-20080424.pdf` in the prior-art repo)"* — The filename suggests the 2008-04-24 issue of UDA 1.0, which is the standard reference. Accurate. OK.

---

## Summary of high-severity items

1. `ssdp:alive` cited as §1.1.2 (correct: §1.2.2) — glossary, FR-014.
2. `ssdp:byebye` cited as §1.1.3 (correct: §1.2.3) — glossary, FR-008, FR-015.
3. M-SEARCH cited as §1.2.1 (correct: §1.3.2) — glossary, FR-004, FR-022.
4. FR-007 USN/UUID cited as §1.1.4 (correct: §1.2.2, §1.3.3 for USN; §2.1 for UDN).
5. NFR-P2 5 s description-fetch timeout is aggressive for slow real-world devices; worth flagging in the assumption.

These are the items most likely to be challenged at a UPnP-fluent lunch & learn.
