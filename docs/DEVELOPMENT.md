# ohSpy — Development & Test Gates

This document records the project's local test/build gates and the pre-release soak gate.

## Everyday test commands

- **Default unit + integration suite (fast, run constantly):**

  ```
  dotnet test tests/ohSpy.Core.Tests
  ```

  This NEVER runs the soak tests — `ohSpy.Soak.Tests` is a separate project that is
  deliberately NOT part of `ohSpy.sln`'s default set (see below).

- **Quick filter (excludes the slow categories explicitly):**

  ```
  dotnet test --filter "category!=chaos&category!=soak"
  ```

- **Chaos category (NFR-P2 drills) — also run by the pre-commit hook:**

  ```
  dotnet test --filter "category=chaos"
  ```

  The committed hook lives at `.githooks/pre-commit`.

> ⚠️ A bare `dotnet test` over the solution runs every suite in `ohSpy.sln`. The soak project is
> intentionally **excluded from `ohSpy.sln`** so even a bare/solution-wide run can never trigger a
> multi-hour soak. The `[Trait("category","soak")]` on every soak test is belt-and-braces — both the
> chaos hook (`category=chaos`) and the quick filter (`category!=chaos&category!=soak`) exclude it.

## Soak gate (Story 6.2 — NFR-R1 / Scale Ceiling)

The soak tests live in `tests/ohSpy.Soak.Tests` and are invoked **BY PATH ONLY**. They are a
**HEADLESS** soak: they drive the real `ohSpy.Core` ViewModel + service stack against an in-process
`FakeUpnpDevice` farm. They never reference WinUI (`CoreAppBoundaryTests` forbid it). There are two:

| Test | What it verifies | Real-run env var |
|---|---|---|
| `ThirtyMinuteNoCrashSoakTests` (`…~ThirtyMinute`) | 30-min no-crash session (SC-R-30min / NFR-R1): 0 crashes, 0 UI-thread stalls > 1 s, 0 unclosable popups, diagnostics responsive | `OHSPY_SOAK_30MIN_DURATION` |
| `EightHourScaleCeilingSoakTests` (`…~EightHour`) | 8-hour scale ceiling (Scale ceiling / SC-013): memory bounded & < 200 MB HEADLESS, bounded collections at their shipped caps, on-disk log rollover | `OHSPY_SOAK_8HR_DURATION` |

### Time-parameterisation

Both durations come from environment variables, defaulting to a **~10-second structural smoke** when
unset (so the same script proves the harness wires up, pumps, asserts, and writes a report in seconds
— no multi-hour wait during dev). `OHSPY_SOAK_DURATION` is a global override applied to both if the
specific var is unset. The value is a `TimeSpan` (e.g. `00:30:00`, `08:00:00`).

### Commands

- **Quick structural validation (~10 s default — proves the harness, NOT the gate):**

  ```
  dotnet test tests/ohSpy.Soak.Tests
  ```

- **Real 30-minute gate run:**

  PowerShell:
  ```
  $env:OHSPY_SOAK_30MIN_DURATION = "00:30:00"; dotnet test tests/ohSpy.Soak.Tests --filter "category=soak&FullyQualifiedName~ThirtyMinute"
  ```
  bash:
  ```
  OHSPY_SOAK_30MIN_DURATION=00:30:00 dotnet test tests/ohSpy.Soak.Tests --filter "category=soak&FullyQualifiedName~ThirtyMinute"
  ```

- **8-hour scale-ceiling run (OPTIONAL — see below):**

  PowerShell:
  ```
  $env:OHSPY_SOAK_8HR_DURATION = "08:00:00"; dotnet test tests/ohSpy.Soak.Tests --filter "category=soak&FullyQualifiedName~EightHour"
  ```
  bash:
  ```
  OHSPY_SOAK_8HR_DURATION=08:00:00 dotnet test tests/ohSpy.Soak.Tests --filter "category=soak&FullyQualifiedName~EightHour"
  ```

Each completed run writes a Markdown report under `docs/soak-reports/<yyyy-MM-dd-HHmm>-<duration>.md`
(environment, farm composition, memory-sample table, exception count, max dispatch latency, on-disk-log
rollover result, bounded-cap snapshot, anomalies). Commit at least the real 30-minute report as the
release-gate artefact.

### The 8-hour full run is OPTIONAL (Project Lead decision)

The 8-hour full scale-ceiling run is **NOT a required release gate** (per the Project Lead). The
required evidence is:

1. the real **30-minute** soak run (gate artefact), plus
2. the **structural quick-validation** (the ~10 s default run of both soak tests), plus
3. Story **6.3's interactive 1-hour SC-013** on the dev LAN (the full-app resident-memory figure).

The 8-hour test stays **present and runnable** at `OHSPY_SOAK_8HR_DURATION=08:00:00`; it is simply not
gate-mandatory. Run it later on real-ish timing if/when desired.

### Memory-ceiling caveat (HEADLESS)

The soak process is the test host + the Kestrel farm — **NOT** the full WinUI `ohSpy.App` process
(WindowsAppRuntime / XAML / composition add resident overhead the headless process never pays). So the
8-hour `< 200 MB` assertion, measured headlessly, verifies that the Core collections + pipeline do **not
leak** and that growth is **bounded** — it does not by itself prove the full app stays under 200 MB. The
full-app RSS figure is verified by **Story 6.3's SC-013**. The reports state this explicitly.

### Flake discipline

**A soak flake is a REAL DEFECT — it is investigated and fixed, NEVER retried-until-green.** NFR-R1 and
the Scale Ceiling are not statistical claims; a single failure is a defect to fix (with its own
regression test). If a soak run surfaces a real defect, that defect is a separate, minimal fix with its
own regression test — the soak harness is not gold-plated to mask it.
