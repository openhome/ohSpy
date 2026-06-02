---
title: "Product Brief: ohSpy"
status: ready-for-review
created: 2026-05-29
updated: 2026-05-30
---

# Product Brief: ohSpy

## Executive Summary

ohSpy is a native Windows desktop app for inspecting UPnP devices on a network — discovery, service and action browsing, action invocation, and GENA event subscription. It aims to fill the gap left by Intel's Device Spy (discontinued, ~2003 vintage) — once the most widely used UPnP inspector on Windows, no longer viable on the platform.

ohSpy serves a second purpose: a Linn-internal advocacy demonstration of spec-driven AI development with Claude Code + BMad. It is built end-to-end through BMad's workflow — brief, PRD, architecture, stories, dev loop — and walked through at a Linn engineering lunch & learn. A prior Claude + spec-kit implementation of the same problem (`C:\work\UpnpSpy`) informs the FR set and the known-issue targets.

## The Problem

Two problems are real.

**Tooling gap.** Linn software engineers regularly debug UPnP devices — streamers, control points, third-party media renderers — and Intel Device Spy was the long-standing tool for it. It is unsupported on modern Windows. There is no first-class equivalent that is actively maintained.

**Methodology curiosity, unmet.** Linn engineers are interested in AI-assisted development but lack a concrete internal example — one built in front of them by a peer using a structured methodology. A working tool that other engineers will actually use — built openly through Claude + BMad, spec artifacts visible — is a more credible demonstration than a slide deck or a toy example.

## The Solution

A native Windows desktop application that:

- Discovers UPnP devices on a chosen network adapter (SSDP M-SEARCH + NOTIFY).
- Displays each device's services and the actions defined by their SCPD documents.
- Invokes actions interactively, with SOAP fault inspection on failure.
- Subscribes to services for GENA event notifications, with automatic renewal.

Feature scope is **parity with UpnpSpy** — same capabilities, no scope creep. Its existing spec is high-value input to the ohSpy PRD; the technical detail lives in the addendum.

The build proceeds through BMad phases: this brief → PRD → architecture → epics & stories → sprint plan → story-by-story implementation with code review.

## What Makes This Different

Honest answer — there is no technical moat.

- **Versus Intel Device Spy:** actively maintained, runs on modern Windows, owned by a team that uses it daily.
- **Versus ad-hoc alternatives** (Wireshark + SOAP envelopes, one-off scripts): integrated workflow in a single tool.

## Who This Serves

**Primary users — Linn software engineers** diagnosing UPnP devices during development, integration, and debugging. UPnP-literate, technical, impatient with bad tools. They want rapid discovery, dense readable display of device internals, the ability to poke an action and see what comes back, and live event streams. No tutorials, no hand-holding.

**Secondary audience — the wider Linn engineering org**, encountering ohSpy through the lunch & learn. Their question is whether Claude + BMad spec-driven development is worth their time on their own projects.

## Success Criteria

**Product success:**

- All four core capabilities (discover, browse, invoke, subscribe) work reliably against the device set in scope (see Scope below).
- The two UpnpSpy performance issues are demonstrably absent: SSDP log handles chatty networks without visible stutter; description and SCPD fetches enforce timeouts and do not hang on slow devices; tree updates produce no full-screen repaints (all verified by eye in a chatty SSDP environment).
- UI meets the Performance budget below on contemporary Windows hardware.

**Eval / advocacy success:**

- The lunch & learn lands: attendees can follow the narrative arc (problem → brief → PRD → architecture → stories → working app), ask substantive questions about the process, and leave curious enough to try BMad on their own work (within a ~30-45 minute slot).
- The spec artifacts (brief, PRD, architecture, stories) are coherent and readable enough to be walked through live; they are the demonstration.

## Scope

**In:**

- Feature parity with UpnpSpy (full FR list lifted as PRD input).
- Deliberate fixes for the two known performance issues from the prior implementation (specifics in addendum).
- Native Windows desktop, single-platform.
- Full BMad spec artifact trail: brief, PRD, architecture document, epics + stories, sprint plan.
- **Diagnostics** — rolling log file plus in-memory buffer (carry forward from UpnpSpy).
- **Distribution** — unsigned installer, internal only.

**Out:**

- Features not present in UpnpSpy. No parity-plus.
- Cross-platform support. Windows only.
- Public / open-source distribution at v1 — decision deferred, internal only first.
- Persona work, visual design system, branding — this is a developer tool, not a UX-led consumer product (BMM lane, not WDS).
- Any moat-building or technical differentiation beyond "it works and is supported."
- **Settings persistence** — no cross-session state (last adapter, window layout, last selection). The tool launches clean every time.
- **Accessibility / a11y compliance** — out of v1, acknowledged. The audience is a small set of Linn engineers; this can be added later if the tool sees wider distribution.

**Devices in scope:**

- Linn DS streamers and OpenHome devices — primary, most common debugging target.
- Arbitrary third-party UPnP gear typically encountered on a developer's network: DLNA media servers and renderers, IGD routers, smart-home gateways, printers, anything that announces itself via SSDP.
- Real-world misbehavior in scope: slow responders, devices that go away mid-subscription, partial NOTIFY messages, larger-than-typical SCPDs.
- Out: deliberately adversarial / fuzz-style malformed UPnP. Ordinary brokenness yes; pathological no.

**Quality constraints (binding, non-negotiable):**

- **Reliability** — no crashes during a typical 30-minute debugging session on a developer's network; slow-responding or misbehaving devices do not hang the UI; the app recovers cleanly when devices disappear mid-interaction.
- **Performance** — initial discovery returns first devices within ~5 s of app start on a typical network; service-node SCPD expansion completes within ~100 ms when the description was eager-fetched, within ~2 s for cold large SCPDs (100+ actions); SSDP log stays smooth at sustained chatty traffic; no UI-thread blocking, ever (budgets inherited from UpnpSpy `plan.md`).
- **UI polish** — modern WinUI conventions throughout; virtualized scrolling on any high-cardinality list; considered visual hierarchy; no full-screen repaints on incremental updates.

Schedule yields to these three. The lunch & learn happens when the bars are met, not on a fixed date.

## Process Artifacts as First-Class Deliverables

The brief, PRD, architecture document, epics, stories, and sprint plan are not byproducts — they are the demonstration substrate. Quality and clarity in these documents matters as much as quality in the binary. The build is the talk; the artifacts are the script.

## If It Doesn't Land

The advocacy framing presumes ohSpy will be visibly better than UpnpSpy and that walking through the BMad process will land with the audience. Honest fallback:

- **If the BMad result is no better than UpnpSpy** — no talk. ohSpy is still useful as a supported internal UPnP inspector; worth doing on its own.
- **If the talk happens and lands flat** — no harm done. The tool persists, the spec artifacts persist, the next person to try the methodology has both as reference.

The brief presumes success. It does not depend on it.

## Vision

Modest and grounded. If ohSpy lands:

- It becomes the supported internal UPnP inspector at Linn, filling the gap left by Intel Device Spy.
- Open-source release becomes a credible option if Linn wants a public footprint in UPnP dev tools.
- The L&L outcome may matter more than the tool itself: if peers adopt Claude + BMad spec-driven development on their own projects, that is the larger win.

ohSpy is not a product with a roadmap. It is a tool that should keep working.
