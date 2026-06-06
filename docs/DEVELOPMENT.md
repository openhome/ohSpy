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

## Farm-backed performance reproducers (Story 6.3 — PRD §6)

Two PRD §6 budgets need a "busier network" than a dev LAN reliably supplies. They are reproduced
**headlessly** by REUSING the Story 6.2 farm primitives (nothing new is built in the farm), in
`tests/ohSpy.Soak.Tests/PerformanceBudgetReproducerTests.cs` (`[Trait("category","soak")]`, excluded from
`ohSpy.sln` + the chaos hook + the quick filter):

| Reproducer | Budget | Farm primitive | What it asserts headlessly |
|---|---|---|---|
| `ColdLargeScpd_Expand_…ViaGiantScpdFarmDevice` | Cold large-SCPD ≤ 2 s, no freeze (FR-100) | `FarmUpnpDevice` `GiantScpd` (120-action) | drives the real `ServiceNodeViewModel` lazy SCPD fetch; cold expand ≤ 2 s; all 120 actions streamed; 0 UI-stalls > 1 s; 0 exceptions |
| `SustainedChattySsdp_BurstLoop_…NoStallsNoExceptions` | Sustained chatty-SSDP ≥ 20 adv/s ≥ 30 s | `DeviceFarm` burst loop | achieved ≥ 20 adv/s (from live SSDP-log growth); 0 UI-stalls > 1 s; 0 exceptions |

```
dotnet test tests/ohSpy.Soak.Tests --filter "FullyQualifiedName~PerformanceBudgetReproducerTests"
```

The sustained-burst window is time-parameterised: `OHSPY_SOAK_BURST_DURATION` (default ~12 s smoke; set
`00:00:30` for the full ≥ 30 s budget). The **"no dropped frames / no UI freeze" eye-test stays the Project
Lead's** on the real `ohSpy.App` — a headless harness cannot judge frame drops (same boundary as 6.2).

## Clean-machine install/run smoke (PERMANENT release gate — Story 6.3)

⚠️ **This is a mandatory pre-release gate step** so the install path can never regress silently again
(the install-blocker the `0x80670016` bootstrap-vs-self-contained contradiction caused — fixed in Story
6.3, Option 1 truly self-contained; architecture Amendment A32). Walk it on a **fresh Windows 11 box with
NO .NET 10, NO WindowsAppRuntime, NO Visual Studio** before every L&L / release build:

1. **Build the installer** (the documented `BuildInstaller` one-liner — runs publish → InnoSetup):

   PowerShell:
   ```
   dotnet build src\ohSpy.App -t:BuildInstaller -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
   ```
   → produces `installer/out/ohSpy-setup-<yyyy.MM.dd.HHmm>-x64.exe` (versioned with the D12 build timestamp).

2. **Copy** `ohSpy-setup-<version>-x64.exe` to the fresh Win11 box and **double-click it**.
3. **SmartScreen** "Windows protected your PC" → **More info → Run anyway** (unsigned by design, PRD §8.1).
4. **Install** runs to completion **without an Administrator/UAC prompt** (per-user install,
   `PrivilegesRequired=lowest`); lands in `%LOCALAPPDATA%\Programs\ohSpy\`; Start Menu shortcut
   `Programs\ohSpy\ohSpy.lnk` exists; the desktop-shortcut checkbox is **unchecked by default**.
5. **Launch** ohSpy from the Start Menu → the **main window renders** with **NO** native
   `Windows App Runtime initialisation failed (0x80670016)` dialog (this is the install-blocker fix — the
   bundled WinAppSDK + .NET runtime load directly; **the UI smoke for the install fix**).
6. **Discovery** populates the device tree within ~7 s (SC-001 on a clean machine, if an eligible adapter).
7. **Diagnostics** are written to `%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-<yyyyMMdd>.log` (AC-8.5).
8. **Uninstall** via Apps & Features removes `%LOCALAPPDATA%\Programs\ohSpy\` + the Start Menu shortcut, and
   **PRESERVES** `%LOCALAPPDATA%\ohSpy\diagnostics\` (AC-12.5).
9. **Rerun the installer** over a prior install → it is detected via the `AppId` GUID and **replaced
   silently** (no "please uninstall first" prompt). If a prompt appears, add a minimal
   `SetupAppMutex`/`CloseApplications` to `installer/ohSpy.iss` (verify-first; do **not** change the `AppId`).

Record the result in `docs/verification/6.3-performance-budget-verification-<date>.md` (the §D install
dry-run table) and tag the L&L-ready build (version `yyyy.MM.dd.HHmm` per D12 + its SHA) in that report.
