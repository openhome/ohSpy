---
baseline_commit: 15ebca5
---
# Story 6.3: Performance Budget Verification + Clean-Machine Install Dry-Run

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer / ohSpy maintainer,
I want a single verification pass that walks every Performance Budget SC-* row and asserts it against the dev LAN, **plus the one real production change that unblocks it** — resolving the shipped `Program.cs` bootstrap ↔ `WindowsAppSDKSelfContained=true` contradiction — **plus** a clean-Windows-11 install dry-run that asserts the installer lands, the app launches, diagnostics are written to `%LOCALAPPDATA%\ohSpy\diagnostics\`, and the uninstaller behaves per spec,
so that the L&L can confidently claim "every budget in §6 of the PRD is met" and "drop the installer on a fresh machine, double-click setup.exe, run ohSpy" — and so the install path can never silently regress.

## ⚠️ Read this first — what this story is (HYBRID: verification + ONE real fix)

This is the **FINAL story** of Epic 6 and the project build-out. It has **two halves** — read both before estimating:

- **(A) Verification (6.1-report style).** Walk every `SC-*` Performance Budget against the dev LAN + a clean-Win11 install dry-run, recording **PASS / FAIL / N/A** in a verification report under `docs/verification/`. Several `SC-*` rows **reuse the Story 6.2 farm primitives** (`GiantScpd` for "Cold large-SCPD ≤ 2 s"; the ≥ 20 adv/s burst for "Sustained chatty-SSDP"). The clean-machine install dry-run + the live-LAN `SC-*` stopwatch checks are the **Project Lead's to run on real hardware / a fresh Win11 box** (exactly like 6.1's walkthrough) — the dev agent cannot run them headlessly. The dev's job for half (A) is to **scaffold the verification-report template** + wire the two farm-backed budget checks so they are reproducible, and to capture the reconciliations below so the Project Lead's walk is unambiguous.

- **(B) ONE real production fix — the load-bearing install blocker (⭐⭐ below).** Unlike 6.1 / 6.2 this story has a **genuine code/config change that is a PREREQUISITE for the install dry-run AC to pass**. It is App/csproj/Program.cs/.iss only — **no `ohSpy.Core` change**. Do NOT gold-plate beyond it; do NOT add features. **Epic 6 delivers no new FRs.** If a verification check surfaces a real *other* defect, that defect is a **separate minimal fix with its own regression test** (the 6.1 "Task-8 minimal fix" discipline), not scope-creep into this story.

**Standing gate:** the install fix is App-layer; if anything you touch reaches Core, keep `-warnaserror` 0/0 and the suite green (553/2 baseline, unchanged by Epic 6). The clean-machine dry-run is a **manual Project-Lead gate**; the story ends at `review` / verification (like 6.1), not at a green clean-box run inside this session. **Smoke-per-UI discipline** applies to the launch-renders check — but here the launch happens on the Project Lead's box, not the dev's.

---

## ⭐⭐ #1 — THE load-bearing issue: the self-contained ↔ bootstrap contradiction (decide + fix in 6.3)

**This is the story's #1 open question AND its one real fix. It must be (a) presented as both options, (b) RECOMMENDED one way, flagged as a Project-Lead decision, and (c) the dev tasks spec'd for the recommended option with the other option's delta noted.**

### The defect (verified against SHIPPED code on `main`)

`src/ohSpy.App/Program.cs` `Program.Main` **unconditionally** calls the **framework-dependent** Windows App SDK bootstrapper:

```csharp
var minVersion = new PackageVersion(major: 2, minor: 1, build: 3, revision: 0);
var ok = Bootstrap.TryInitialize(0x00020001, "", minVersion, Bootstrap.InitializeOptions.None, out var hr);
if (!ok) { /* native MessageBoxW "Windows App Runtime initialisation failed (0x{hr:X8})" */ return hr; }
```

`Bootstrap.TryInitialize` requires an **installed** Windows App Runtime ≥ 2.1.3 on the machine. But `src/ohSpy.App/ohSpy.App.csproj` (L17-19) sets:

```xml
<WindowsPackageType>None</WindowsPackageType>      <!-- Unpackaged -->
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>   <!-- bundle WAS -->
<SelfContained>true</SelfContained>                <!-- bundle .NET -->
```

whose contract is: **the runtime ships next to the exe and the bootstrapper is NOT used.** The two are **mutually exclusive**:
- A *self-contained* WinAppSDK app loads the bundled runtime **directly via the app's own `.deps.json` / runtimeconfig** — it does **not** call `Bootstrap.TryInitialize` at all.
- The *framework-dependent* bootstrap path (`Bootstrap.TryInitialize`) exists precisely to **find a centrally-installed runtime** when one is **not** bundled.

So the self-contained config is currently a **no-op as far as startup is concerned**: `Program.Main` forces the framework-dependent path. (Architecture L1641 *claims* "self-contained publish bundles the WAS; `Bootstrap.TryInitialize` finds the bundled runtime and binds to it" — **that claim is wrong as shipped**; the bootstrapper looks for an *installed* runtime, not the self-contained sibling. This story corrects that architecture claim by Amendment — see Task 5.)

### The symptom (the blocker)

On a **clean Win11 box with no Windows App Runtime ≥ 2.1.3 installed**, the InnoSetup installer lands the files, but the app dies at startup with the **native MessageBox `Windows App Runtime initialisation failed (0x80670016)`** (that dialog is `Program.Main`'s own `MessageBoxW`). Observed 2026-06-03 (deferred-work.md L58-65, commit `d417fad`): the dev box only had WinAppRuntime `2.0.1.0`; the app targets `2.1.3`; worked around per-developer via `Add-AppxPackage` of the `2.1.3` MSIX framework packages — **not a product fix**. **AC-12.4 ("Bootstrap.TryInitialize succeeds" / "app launches on a clean box") FAILS on a clean box today.** This MUST be resolved for the install dry-run AC of this story to pass.

### The two coherent options (this is OPEN-Q #1, a Project-Lead decision)

> ✅ **DECIDED 2026-06-06 (Project Lead): OPTION 1 — Truly self-contained.** Implement Option 1; do NOT implement Option 2 (its delta below stays as the recorded rejected alternative).


**Option 1 — Truly self-contained (⭐ RECOMMENDED).**
- **Remove** the `Bootstrap.TryInitialize` / `Bootstrap.Shutdown` calls (and the `using Microsoft.Windows.ApplicationModel.DynamicDependency;` + the `0x80670016` `MessageBoxW` failure path that only exists to report bootstrap failure) from `Program.cs`. A self-contained app's `Application.Start(_ => new App())` loads the bundled WinAppSDK runtime directly — no bootstrapper.
- **Keep** the csproj `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true` flags (they become *real* now, not a no-op).
- **Confirm** `dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained` lays down a bundle that runs on a box with **no** runtime install required (the publish folder must contain the WinAppSDK native runtime DLLs — e.g. `Microsoft.WindowsAppRuntime.Bootstrap.dll` is *not* needed, but `CoreMessaging`/`Microsoft.ui.xaml.dll`/the WinAppSDK framework DLLs **are** laid down beside `ohSpy.App.exe`).
- **Why recommended:** it matches the PRD/D12 goal exactly — "drop the installer on a fresh machine, double-click setup.exe, run ohSpy" with **no prerequisite installer, no Admin** (AC-12.3 + AC-12.4). It removes the runtime-version coupling (`2.1.3` minVersion) that is the source of `0x80670016`. It is the smaller, lower-risk change (delete code + keep config + re-publish), and it preserves the per-user `%LOCALAPPDATA%\Programs\ohSpy\` install with no elevation.

**Option 2 — Framework-dependent (the delta, NOT recommended).**
- **Drop** `WindowsAppSDKSelfContained` + `SelfContained` from the csproj (they are misleading as-is and would now be honestly absent).
- **Keep** `Bootstrap.TryInitialize` in `Program.cs`.
- **Make the InnoSetup installer a prerequisite-carrier:** bundle `WindowsAppRuntimeInstall-x64.exe` (≥ 2.1.3, from the `Microsoft.WindowsAppSDK` runtime redist) as an InnoSetup `[Files]` payload and run it in `[Run]` (`BeforeInstall`/prereq) so the runtime is installed on the target before first launch.
- **Cost vs Option 1:** larger installer-script change; introduces a per-machine runtime install step (the runtime installer itself may prompt / may need its own elevation depending on per-user vs per-machine mode); re-introduces the "is the runtime present and the right version" coupling that `0x80670016` came from; also produces a *smaller* app bundle but a *more complex* install. This is why Option 1 is recommended.

**Either way (mandatory regardless of choice):** add a **clean-machine install/run smoke** to the release-readiness checks (this story's verification report + `docs/DEVELOPMENT.md`) so this can never regress silently again. That smoke is half (A)'s install dry-run AC.

> **Dev directive:** implement **Option 1** (the recommended path) by default. Record the Option-2 delta in the completion notes as the rejected alternative. If the Project Lead has explicitly chosen Option 2 before dev-story runs, follow §"Option-2 task delta" instead. **Do not implement both.**

---

## ⭐ #2 — InnoSetup `installer/ohSpy.iss` reconciliation (SHIPPED state vs the ACs — gaps the dev must close)

The InnoSetup script **EXISTS** at `installer/ohSpy.iss` (Story 1.1). Reconciled against the Story-6.3 ACs + D12 (architecture L1532-1538):

| AC / D12 requirement | Shipped in `ohSpy.iss`? | Action |
|---|---|---|
| Per-user install dir `%LOCALAPPDATA%\Programs\ohSpy\` (AC-12.3, no Admin) | ✅ `DefaultDirName={localappdata}\Programs\{#AppName}` + `PrivilegesRequired=lowest` (L25, L29) | none — verify in dry-run |
| `AppId` GUID stable for upgrade-detection (silent replace, no "uninstall first") | ⚠️ **PARTIAL.** `AppId={{5E1C113B-...}` exists (L21, the `{{` is correct InnoSetup brace-escaping → literal `{5E1C…}`). BUT there is **no explicit `usesetupclassic` / `SetupAppMutex` / `CloseApplications`** and **no upgrade-detection test**. InnoSetup *does* silently upgrade same-`AppId` installs by default (replaces files in `{app}`), so the **silent-replace behaviour likely already holds** — but D12 (L1538) names "Standard InnoSetup `usesetupclassic` / `SetupAppMutex` pattern". **Action:** confirm the default same-`AppId` rerun replaces silently (no "please uninstall first" prompt) in the dry-run (it's a verification AC, not necessarily a script change). If a prompt appears, add `SetupAppMutex`/`CloseApplications`/`CloseApplicationsFilter` to suppress it. Do NOT change the `AppId` (changing it causes side-by-side installs). |
| Start Menu shortcut `Programs\ohSpy\ohSpy.lnk` | ✅ `[Icons] Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"` (L47) — `{group}` resolves under Start Menu `Programs\ohSpy\` via `DefaultGroupName={#AppName}` (L26) | none — verify the resulting `.lnk` path in dry-run |
| Desktop-shortcut checkbox **unchecked by default** | ✅ `[Tasks] Name: desktopicon; … Flags: unchecked` + `[Icons] … Tasks: desktopicon` (L48, L51) | none — verify checkbox state in dry-run |
| Uninstall removes install dir + Start Menu shortcut | ✅ default InnoSetup uninstall removes `[Files]` (the `{app}` dir) + `[Icons]` | none |
| Uninstall **PRESERVES** `%LOCALAPPDATA%\ohSpy\diagnostics\` (D12 + AC-12.5) | ✅ explicitly NOT listed in `[UninstallDelete]`; comment L56-59 documents the deliberate non-deletion | none — verify the dir survives uninstall in dry-run |
| Versioning `yyyy.MM.dd.HHmm` (D12) | ✅ MSBuild `BuildInstaller` target computes `InstallerVersion = UtcNow.ToString("yyyy.MM.dd.HHmm")` → `/DVersion=` → `AppVersion={#Version}` + `OutputBaseFilename=ohSpy-setup-{#Version}-x64` (csproj L68, .iss L23/L35) | none — confirm the produced artefact name carries the timestamp |
| SmartScreen "Run anyway" on first run | n/a in script (unsigned by design — PRD §8.1) | verify "More info → Run anyway" in dry-run |
| **The self-contained publish bundle is laid down into `[Files]`** | ✅ `[Files] Source: "{#PublishDir}\*" … recursesubdirs` (L44) — but this is **only correct if the publish is truly self-contained** → **THIS is where the ⭐⭐#1 fix interacts**: with Option 1 the publish folder gains the bundled runtime DLLs and `[Files]\*` carries them; with the current contradictory config the bundle is effectively framework-dependent and the runtime DLLs may not be present → the clean box fails | **gated on ⭐⭐#1** |

**Net `.iss` gaps:** the script is in good shape. The only *latent* gap is the upgrade-detection prompt (verify; add `SetupAppMutex`/`CloseApplications` only if the dry-run shows a prompt) — and the **critical interaction** that the `[Files]\*` payload is only a runnable bundle once ⭐⭐#1 is fixed so the publish is genuinely self-contained. **No `.iss` change is required for the recommended Option 1** beyond possibly the upgrade-mutex (verify-first).

---

## ⭐ #3 — Build / publish pipeline (Decision 12) reconciliation

- `ohSpy.App.csproj` has `<WindowsPackageType>None</WindowsPackageType>` (✅ AC-12.6 unpackaged) and the self-contained flags (the ones ⭐⭐#1 makes real for Option 1).
- The **`BuildInstaller` MSBuild target EXISTS** (csproj L61-86): `DependsOnTargets="Publish"`, computes `InstallerVersion`, asserts the publish output + the InnoSetup compiler exist, then invokes `ISCC.exe` with `/DPublishDir`, `/DOutputDir`, `/DVersion`. Output: `installer/out/ohSpy-setup-<version>-x64.exe`. **One-liner (already documented in csproj L59):** `dotnet build src\ohSpy.App -t:BuildInstaller -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true`.
- **No installer artefact is built in the repo yet** (`installer/out/` does not exist) — the Project Lead produces the L&L-ready artefact at release time. **AC-12.2** (the target runs publish → iscc) is verified when the Project Lead builds it.
- **`InstallerPublishDir`** (csproj L72) is the deterministic publish path `bin\$(Configuration)\$(TargetFramework)\$(RuntimeIdentifier)\publish\`. After the ⭐⭐#1 Option-1 fix, **re-run the publish and confirm this folder contains the WinAppSDK runtime DLLs** beside `ohSpy.App.exe` (the proof that "self-contained" is now real). This is the dev's headless-verifiable check (Task 2) — it does NOT need a clean box, just an inspection of the publish output.

---

## ⭐ #4 — SC-* verification plan (which reuse the 6.2 farm vs which are live-LAN stopwatch)

Cross-checked against PRD §6 (L669-690) and the epic 6.3 AC list. Each row → its verification method. The **report scaffold** (Task 3) carries one row per budget with a Result + Evidence column the Project Lead fills, exactly like 6.1's report.

| Budget (PRD §6) | Target | Verification method | Who / where |
|---|---|---|---|
| **SC-001** Launch → all responsive devices in tree | ≤ ~7 s (5 s MX + ≤ 2 s eager) | **Live-LAN stopwatch** — process start → last device row populated; **also re-verified end-to-end on the clean box** (install dry-run) | Project Lead, dev LAN + clean box |
| **SC-002** Dedup over 30-min session | 1 tree entry per UDN; 0 dups | **Live-LAN** — tree-snapshot vs SSDP log over 30 min (note: identity is the **UDN string**, Amendment A30 — not a parsed Guid) | Project Lead, dev LAN |
| **SC-003** `ssdp:byebye` → row removed | typically < 2 s | **Farm-assisted** — trigger byebye via the 6.2 farm (`DeviceFarm` byebye-on-demand / `FarmUpnpDevice`) OR a real device power-off; stopwatch | Project Lead (LAN) / dev (farm) |
| **SC-004** Service/action node expansion | ≤ 2 s typical (cold) | **Live-LAN stopwatch** — expand a real device's service node cold | Project Lead, dev LAN |
| **SC-005** "View XML" → default browser opens | ≤ 2 s | **Live-LAN stopwatch** — right-click → View XML | Project Lead, dev LAN |
| **SC-009** SSDP advert → row in log | ≤ 1 s | **Live-LAN stopwatch** (or farm advert injection) | Project Lead / dev (farm) |
| **SC-010** Double-click action → popup interactive | ≤ 1 s | **Live-LAN stopwatch** — double-click → input fields editable | Project Lead, dev LAN |
| **SC-011** Invoke submitted → result visible | ≤ 2 s (device < 1 s LAN latency) | **Live-LAN stopwatch** — against a real device | Project Lead, dev LAN |
| **SC-013** 1-hour continuous operation | no mem exhaustion; bounded collections; on-disk roll | **Live-LAN interactive run** — the **interactive complement to 6.2's headless soak**; this is the **full-app RSS figure** the 6.2 200 MB caveat cross-references (6.2 ⭐#4 / AC-6.2.7). Record real-app WorkingSet over the hour; confirm SSDP log ≤ 10k, ring ≤ 5k, event lists ≤ 5k, on-disk rolls | Project Lead, dev LAN (real `ohSpy.App`) |
| **Warm SCPD expand** | ≤ 100 ms | **Live-LAN stopwatch** — re-expand the SAME service node after first cold expand (eager-fetched → cache hit) | Project Lead, dev LAN |
| **Cold large-SCPD expand** | ≤ 2 s, no UI freeze (FR-100) | **FARM (6.2 `GiantScpd`)** — the 6.2 `FarmUpnpDevice` 120-action `GiantScpd` body is the reproducible source of a 100+-action SCPD; also reachable against a real IGD router (100+-action SCPD). Re-uses the 6.1.7 measure | dev (farm) + Project Lead (real IGD) |
| **Sustained chatty-SSDP** | ≥ 20 adv/s ≥ 30 s; no dropped frames; stalls < 16 ms | **FARM (6.2 ≥ 20 adv/s burst)** — the 6.2 `DeviceFarm` burst loop is the reproducible ≥ 20 adv/s source; this is the **complement to the 6.1.14 deferred burst** (deferred-work.md L13 — "needs a busier network"). The 6.2 burst fixture satisfies it reproducibly | dev (farm) + Project Lead (eye-test on real app) |

**Farm reuse note:** the 6.2 farm primitives live in `tests/ohSpy.Soak.Tests/Farm/` (`FarmUpnpDevice` — incl. `GiantScpd` 120-action body; `DeviceFarm` — advertiser loop with configurable adv/s incl. the ≥ 20 adv/s burst + byebye-on-demand). The dev does **not** rebuild these — the dev wires a **small headless reproducer** (a `[Trait("category","soak")]`-style or perf-tagged check, OR a documented `dotnet test` invocation against the existing farm) that demonstrates the GiantScpd cold-expand path and the ≥ 20 adv/s burst path are exercisable, and records the measured numbers in the report. The **eye-test "no dropped frames / no UI freeze"** part is inherently the Project Lead's on the real `ohSpy.App` (a headless harness cannot judge dropped frames — same boundary as 6.2's ⭐#1 headless-drive limit). State that split honestly in the report.

---

## ⭐ #5 — Final release-readiness checkpoint (SM-5 / SM-6 — the L&L gate)

When 6.1 + 6.2 + 6.3 are all green:
- The **verification reports are committed to `docs/`** — `docs/verification/6.1-…md` (done), the 6.2 soak reports under `docs/soak-reports/` (done), and **this story's `docs/verification/6.3-…md`** (new).
- The **latest installer artefact is tagged with the build timestamp** (`yyyy.MM.dd.HHmm` per D12) and **identified as the L&L-ready build** — the Project Lead builds `ohSpy-setup-<version>-x64.exe` via the `BuildInstaller` target (after the ⭐⭐#1 fix is in) and records its version + SHA in the 6.3 report.
- The **brief → PRD → architecture → epics → working-app narrative is walkable end-to-end** against the committed artefacts (SM-5 + SM-6 from PRD §9). The 6.3 report's conclusion explicitly asserts this walk holds with no retconning (SM-C3 honesty — if anything is wrong, fix it, don't paper over).
- The **chaos-hook discipline spot-check (AC-13.x)** over the commit history: `git log --oneline` (60 commits at baseline) + `git log --all --grep="no-verify"` confirm **zero `--no-verify` bypasses** (verified at story creation: clean). Record the spot-check result in the report.

---

## Acceptance Criteria

> ACs renumbered `6.3.x` for traceability; the source epic/PRD/architecture AC is cited inline. Where the verification is the **Project Lead's manual gate**, the dev's deliverable is the **reproducible scaffold + the captured reconciliation** that makes that gate unambiguous (the 6.1 model).

### The one real production fix (half B) — the install blocker

1. **(AC-6.3.1 — bootstrap/self-contained contradiction RESOLVED)** The ⭐⭐#1 contradiction is fixed per the **recommended Option 1** (unless the Project Lead pre-selected Option 2): `Program.cs` no longer calls the framework-dependent `Bootstrap.TryInitialize` / `Bootstrap.Shutdown`; the csproj retains `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true`; `Application.Start(_ => new App())` is the startup path. `-warnaserror` stays 0/0 (App bar the pre-existing benign WMC1506); the App still builds + the Core suite is unchanged (553/2). **OPEN-Q #1 (the option choice) is flagged in the completion notes as a Project-Lead decision.**
2. **(AC-6.3.2 — self-contained publish proven, headlessly verifiable)** `dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained` produces a publish folder that contains the bundled WinAppSDK runtime DLLs beside `ohSpy.App.exe` (proof "self-contained" is now real, not a no-op). The dev inspects + records the presence of the WinAppSDK framework runtime files in the publish output (this check needs no clean box — just the publish folder). [Source: architecture.md Decision 12 + AC-12.4; deferred-work.md L58-65]
3. **(AC-6.3.3 — clean-machine install/run smoke added to release-readiness)** A **clean-machine install/run smoke** is documented in `docs/DEVELOPMENT.md` (and referenced from the 6.3 report) as a permanent release-gate step, so the install path can never regress silently again (the deferred-work directive "add a clean-machine install/run smoke … so this can't regress").

### (A) Performance-budget verification report (live-LAN + farm-backed)

4. **(AC-6.3.4 — SC-* verification report scaffold)** A verification report `docs/verification/6.3-performance-budget-verification-<date>.md` exists with **one row per Performance Budget** (SC-001, SC-002, SC-003, SC-004, SC-005, SC-009, SC-010, SC-011, SC-013, Warm SCPD, Cold large-SCPD, Sustained chatty-SSDP) carrying a **Result (PASS/FAIL/N/A) + Evidence (measured value)** column the Project Lead fills on the dev LAN — the 6.1 report model. Each row names its verification method per ⭐#4 (live-LAN stopwatch vs 6.2-farm-backed). [Source: epics.md 6.3 AC SC-* list; PRD §6]
5. **(AC-6.3.5 — farm-backed budget reproducers wired)** The two farm-backed budgets are demonstrably reproducible **headlessly** by the dev using the **existing 6.2 farm primitives** (no rebuild): (a) **Cold large-SCPD ≤ 2 s** via `FarmUpnpDevice` `GiantScpd` (120-action); (b) **Sustained chatty-SSDP ≥ 20 adv/s ≥ 30 s** via the `DeviceFarm` burst loop. The measured cold-expand time + the achieved adv/s are recorded in the report; the **"no dropped frames / no UI freeze" eye-test is the Project Lead's** on the real app (stated as such). [Source: 6.2 ⭐#5 farm; epics.md 6.3 "GiantScpd mode" / "fake-device burst fixture"]
6. **(AC-6.3.6 — SC-013 interactive 1-hour run captured)** The report has a section for the **interactive 1-hour SC-013 run** on the real `ohSpy.App` — the **interactive complement** to 6.2's headless soak — recording **full-app resident memory** over the hour (the figure the 6.2 200 MB headless caveat cross-references), plus confirmation that the bounded collections (SSDP log ≤ 10k, ring ≤ 5k, event lists ≤ 5k) and on-disk rollover behave. [Source: PRD §6 SC-013; 6.2 ⭐#4 / AC-6.2.7 cross-ref]

### (A) Clean-Windows-11 install dry-run (Project-Lead gate, scaffolded by the dev)

7. **(AC-6.3.7 — clean-box install)** On a fresh Win11 box with NO .NET 10 / NO WindowsAppRuntime / NO Visual Studio: copying `ohSpy-setup-<version>-x64.exe` and running it shows SmartScreen "Windows protected your PC" → "More info" → "Run anyway" proceeds; the installer runs to completion **without an Administrator prompt** (AC-12.3); the install lands in `%LOCALAPPDATA%\Programs\ohSpy\` (AC-12.3); a Start Menu shortcut `Programs\ohSpy\ohSpy.lnk` exists; the desktop-shortcut checkbox is **unchecked by default**. Captured in the report's install-dry-run section. [Source: epics.md 6.3 AC; D12 L1532-1538]
8. **(AC-6.3.8 — clean-box launch + render + discovery + diagnostics)** Launching ohSpy from the Start Menu on the clean box: the app opens and the **main window renders** (AC-12.4 — startup now succeeds because the bundled runtime loads directly, **no `0x80670016`**); SSDP discovery proceeds (if an eligible adapter exists); within ~7 s the device tree populates (**SC-001 end-to-end on a clean machine**); the diagnostic file sink creates `%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-<yyyyMMdd>.log` and writes the first session's entries (**AC-8.5 end-to-end**). [Source: epics.md 6.3 AC; AC-8.5]
9. **(AC-6.3.9 — uninstall preserves diagnostics)** Uninstall via Apps & Features removes `%LOCALAPPDATA%\Programs\ohSpy\` + the Start Menu shortcut (AC-12.5), and **PRESERVES** `%LOCALAPPDATA%\ohSpy\diagnostics\` (D12 + AC-12.5). Captured in the report. [Source: epics.md 6.3 AC; .iss L56-59]
10. **(AC-6.3.10 — silent upgrade rerun)** Rerunning the installer while a prior install exists: the prior install is detected via the `AppId` GUID and **replaced silently** (no "please uninstall first" prompt); the install completes cleanly. Captured in the report; if a prompt appears, the `.iss` gets a minimal `SetupAppMutex`/`CloseApplications` fix (⭐#2). [Source: epics.md 6.3 AC; D12 L1538]

### Release-readiness checkpoint (SM-5 / SM-6 / AC-13.x)

11. **(AC-6.3.11 — chaos-hook discipline spot-check)** The commit history is spot-checked: every merged commit was pre-commit-hooked (chaos suite ran + passed); **no `--no-verify` bypasses appear without justified-in-message rationale** (D13). Result recorded in the report (verified clean at story creation: 60 commits, 0 bypasses). [Source: epics.md 6.3 AC; architecture.md Decision 13]
12. **(AC-6.3.12 — final release-readiness)** With 6.1 + 6.2 + 6.3 green: the verification reports are committed under `docs/`; the latest installer artefact is **tagged with the build timestamp** (`yyyy.MM.dd.HHmm` per D12) and identified as the **L&L-ready build** (version + SHA recorded in the 6.3 report); and the **brief → PRD → architecture → epics → working-app narrative is walkable end-to-end** against the committed artefacts (SM-5 + SM-6), with no retconning (SM-C3). The 6.3 report's conclusion asserts this. [Source: epics.md 6.3 AC; PRD §9 SM-5/SM-6]

---

## Tasks / Subtasks

- [x] **Task 0 — Confirm the install-blocker decision (OPEN-Q #1)** (AC: 1)
  - [x] Surface the ⭐⭐#1 decision to the Project Lead BEFORE coding: Option 1 (recommended, truly self-contained) vs Option 2 (framework-dependent + installer-carried runtime). Default to **Option 1** if no explicit instruction. Record the choice + rationale in the completion notes. — **DECIDED: Option 1** (Project Lead, 2026-06-06; the story's "✅ DECIDED" note). Implemented Option 1; Option-2 delta recorded as the rejected alternative in completion notes + the report + Amendment A32.

- [x] **Task 1 — Resolve the contradiction (Option 1 — recommended)** (AC: 1, 2)
  - [x] In `src/ohSpy.App/Program.cs`: removed the `Bootstrap.TryInitialize(...)` call + the `if (!ok)` `0x80670016` `MessageBoxW` failure block + the `Bootstrap.Shutdown()` in `finally` + the `using Microsoft.Windows.ApplicationModel.DynamicDependency;` + the `PackageVersion minVersion`. Kept `[STAThread] Main`, kept `Application.Start(_ => new App())` (and its `#pragma warning disable CA1806`), kept the `StartupObject`/`DISABLE_XAML_GENERATED_MAIN` wiring. `MessageBoxW`/`MB_*` became entirely unused after removing the failure path → removed them too (+ the `System.Runtime.InteropServices` using) to avoid a new analyzer warning.
  - [x] Kept `ohSpy.App.csproj` `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true` (now real). No csproj change.
  - [x] Built the App: `-warnaserror` 0/0 bar the pre-existing benign **WMC1506** (`MainWindow.xaml:162`, untouched). Core suite **unchanged (553 passed / 2 skipped)**.
  - [x] **(Option-2 task delta):** NOT implemented (Option 1 chosen). Recorded as the rejected alternative.

- [x] **Task 2 — Prove the self-contained publish (headless)** (AC: 2)
  - [x] Ran `dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained`. Inspected the publish folder (`src/ohSpy.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`, **430 files / ~215 MB**) and recorded the WinAppSDK runtime DLLs (`Microsoft.ui.xaml.dll`, `CoreMessagingXP.dll` [the self-contained `CoreMessaging` variant], `DWriteCore.dll`, `Microsoft.WindowsAppRuntime.dll`, `MRM.dll`, `dwmcorei.dll`, `Microsoft.Internal.FrameworkUdk.dll`, … 75 `Microsoft.*.dll`) **and** the .NET runtime (`coreclr.dll`, `clrjit.dll`, `System.Private.CoreLib.dll`, `hostfxr/hostpolicy`) beside `ohSpy.App.exe`. `runtimeconfig.json` carries `includedFrameworks` (the self-contained marker). Proof recorded in the report §A.
  - [x] Did NOT commit the publish output (build output). File inventory recorded in the report's "self-contained publish proof" section.

- [x] **Task 3 — Verification report scaffold** (AC: 4, 5, 6, 7, 8, 9, 10, 11, 12)
  - [x] Created `docs/verification/6.3-performance-budget-verification-2026-06-06.md` from the template — header, SC-* budget table, install-dry-run section, SC-013 interactive section, release-readiness checklist, chaos-hook spot-check, defects table, conclusion.
  - [x] Pre-filled the headless rows: chaos-hook spot-check (60 commits, 0 `--no-verify`), the self-contained publish proof (Task 2), the `.iss` reconciliation summary (⭐#2), and the two farm-backed measured numbers (Task 4). Left the live-LAN stopwatch + clean-box rows as `_PASS/FAIL/N/A_` placeholders for the Project Lead.

- [x] **Task 4 — Farm-backed budget reproducers (reuse 6.2 primitives — DO NOT rebuild)** (AC: 5)
  - [x] **Cold large-SCPD ≤ 2 s:** added `ColdLargeScpd_Expand_CompletesWithinBudget_ViaGiantScpdFarmDevice` — drives the real `ServiceNodeViewModel` lazy SCPD fetch against the farm's `GiantScpd` (120-action) device; times the cold expand. **Measured: 21 ms (budget 2000 ms); 120/120 actions; 0 stalls; 0 exceptions.** Reused the 6.2 farm; no new device.
  - [x] **Sustained chatty-SSDP ≥ 20 adv/s:** added `SustainedChattySsdp_BurstLoop_SustainsAtLeast20PerSecond_NoStallsNoExceptions` — runs the `DeviceFarm` burst at 25 adv/s, measures achieved rate from SSDP-log growth. **Measured: 21.4 adv/s over a 12.1 s smoke window (≥ 20 asserted); 0 UI-stalls > 1 s; 0 exceptions.** ≥ 30 s window via `OHSPY_SOAK_BURST_DURATION=00:00:30`. The "no dropped frames" eye-test stated as the Project Lead's.
  - [x] Both in `tests/ohSpy.Soak.Tests/PerformanceBudgetReproducerTests.cs`, `[Trait("category","soak")]` — NOT in production, NOT in `ohSpy.sln`, NOT in the chaos hook (`category=soak` excluded; verified the chaos filter matches 0 tests in the soak project).

- [x] **Task 5 — Architecture amendment (the L1641 claim correction)** (AC: 1, 12)
  - [x] Added **Amendment A32** to `architecture.md` Decision 12 recording the L1641 claim was incorrect (`Bootstrap.TryInitialize` finds an *installed* runtime, not the self-contained sibling; mutually exclusive) and that Story 6.3 resolved it by removing the bootstrap call (Option 1). Patched the L1641 sentence inline with a pointer to A32. Recorded the Option-2 rejected alternative.
  - [x] Resolved the `deferred-work.md` entry (the bootstrap ↔ self-contained contradiction) → marked **✅ RESOLVED 2026-06-06 in Story 6.3 (Option 1)** with the A32 reference (original entry retained for history).

- [x] **Task 6 — `docs/DEVELOPMENT.md`: clean-machine install/run smoke + release-readiness** (AC: 3, 12)
  - [x] Added a **clean-machine install/run smoke** section (permanent release gate): build the installer (`BuildInstaller` one-liner) → fresh Win11 box no runtimes → SmartScreen "Run anyway" → install (no Admin) → launch → main window renders (no `0x80670016`) → tree ≤ ~7 s → diagnostics written → uninstall preserves diagnostics → rerun = silent upgrade. Also added the farm-backed reproducer subsection.
  - [x] Cross-referenced the 6.3 verification report + the D12 `yyyy.MM.dd.HHmm` versioning + the L&L-ready-build identification.

- [x] **Task 7 — Manual gates (Project Lead) + smoke-per-UI** (AC: 7, 8, 9, 10, 11, 12)
  - [x] **Manual gate (Project Lead — story ends at `review`):** scaffolded in the report §B/§C/§D with PASS/FAIL/N/A placeholders for the live-LAN SC-* stopwatch walk + the clean-Win11 install dry-run + the interactive SC-013 1-hour run. **Smoke-per-UI:** the clean-box "main window renders" launch IS the UI smoke for the install fix — it happens on the Project Lead's box; the dev's headless proof is Task 2's publish inspection. (Dev cannot run these gates headlessly — 6.1 posture.)
  - [x] Defects table in the report carries the install-blocker entry (fixed, Option 1) + a note that any live-LAN/clean-box FAIL → a separate minimal fix (6.1 "Task-8" discipline).

- [x] **Task 8 — Final release-readiness sign-off** (AC: 12)
  - [x] Scaffolded the report §E release-readiness checklist: 6.1 + 6.2 + 6.3 reports under `docs/`; the L&L-ready installer-tag slots (version/SHA — Project Lead fills on building the artefact); the SM-5/SM-6/SM-C3 narrative-walk assertion in the conclusion. The Project Lead completes the conclusion + tags the build (the artefact is built at release, not committed).

---

## Dev Notes

### What this story is (framing)

The **final** story of a verification-only epic — but a **HYBRID**: half (A) is a 6.1-style verification report (mostly the Project Lead's manual walk; the dev scaffolds + reconciles + wires two farm-backed reproducers), half (B) is **the one real production fix** — removing the `Bootstrap.TryInitialize` call so the self-contained config becomes real and a clean box stops dying at `0x80670016`. Half (B) is the **prerequisite** for half (A)'s install dry-run to pass. Beyond (B) + the report + two farm reproducers + the doc/amendment updates, **write no new production code**. If a budget check finds another defect, that's a separate minimal fix with its own test — not this story.

### Shipped behaviour — verified against current `main` (reconcile, don't trust stale prose)

- **The contradiction is REAL and shipped** (⭐⭐#1): `Program.cs` L19-25 calls framework-dependent `Bootstrap.TryInitialize(2.1.3 minVersion)`; csproj L18-19 sets the self-contained flags. Mutually exclusive → self-contained is a startup no-op → clean box = `0x80670016` (deferred-work.md L58-65, commit `d417fad`).
- **The `.iss` EXISTS and is in good shape** (⭐#2): per-user dir ✅, `PrivilegesRequired=lowest` ✅, `AppId` ✅ (the `{{` brace-escape is correct), Start Menu shortcut ✅, desktop checkbox `unchecked` ✅, diagnostics-preserved-on-uninstall ✅ (deliberate non-`[UninstallDelete]`), version `yyyy.MM.dd.HHmm` via MSBuild ✅. Only latent gap: upgrade-detection prompt (verify-first; add `SetupAppMutex` only if a prompt appears).
- **The `BuildInstaller` target EXISTS** (csproj L61-86): `Publish` → `ISCC.exe` → `installer/out/ohSpy-setup-<version>-x64.exe`. No artefact built yet (`installer/out/` absent — Project Lead builds at release).
- **WinAppSDK is pinned `2.1.3`** (`Directory.Packages.props` L12). The `2.1.3` minVersion in `Program.cs` is the exact source of the version-coupling that breaks on a box with only `2.0.1.0` — Option 1 removes that coupling entirely.
- **The 6.2 farm primitives are shipped + reusable** (`tests/ohSpy.Soak.Tests/Farm/FarmUpnpDevice.cs` GiantScpd 120-action; `Farm/DeviceFarm.cs` advertiser/burst/byebye). The 6.2 soak project is NOT in `ohSpy.sln`; invoked by path; `[Trait("category","soak")]`; excluded from the chaos hook + the quick filter. Reuse it; do NOT rebuild farm devices.
- **Identity is the UDN string** (Amendment A30, commit `9303ba4`) — SC-002 dedup is per-UDN, not per-Guid.
- **6.1 deferred two ACs** to a busier network (deferred-work.md L9-14): 6.1.4 "Loading…" visibility + 6.1.14 SSDP burst ≥ 20 adv/s. The 6.2/6.3 farm burst is the reproducible complement to 6.1.14 — note in the report that the burst path is now reproducible headlessly even if the live LAN can't produce it.
- **Bounded caps (shipped, accurate)**: SSDP log 10,000 (`SsdpLogViewModel.cs`), event list 5,000 (`SubscriptionPopupViewModel.EventListCapacity`), ring 5,000 (`DiagnosticRingSink.cs`), on-disk 2 MB × 8 = 16 MB (`DiagnosticFileSink.cs`). SC-013 confirms these in the real app over an hour.

### The chaos-hook spot-check (verified at story creation)

`git log --oneline` → 60 commits; `git log --all --grep="no-verify"` → **none**. Clean. The `.githooks/pre-commit` chaos hook (`dotnet test --filter "category=chaos"`, D13 + A18) ran on every commit; no bypasses. Record this in the report (AC-6.3.11) and re-run the two commands at sign-off to confirm nothing slipped in during dev.

### Standing gates (don't regress)

- `-warnaserror` 0/0 (App bar the pre-existing benign WMC1506 at `MainWindow.xaml:162` — untouched). Core suite **553/2 UNCHANGED** (Epic 6 adds no Core). No new `DiagCategories` constant (DiagCategoriesUsageTests/ExactSet unchanged). Chaos hook untouched. The soak project stays out of `ohSpy.sln`.
- The install fix is **App/csproj/Program.cs/.iss + docs/architecture only** — no Core. If you find yourself editing Core, stop and reconsider.

### Verification report template (Task 3) — write to `docs/verification/6.3-performance-budget-verification-<date>.md`

```markdown
# Story 6.3 — Performance Budget Verification + Clean-Machine Install Dry-Run

- **Date:** <yyyy-MM-dd>
- **Verifier:** Simon Chisholm (Project Lead)
- **Build / commit:** `<sha>` (the L&L-ready build) — installer version `<yyyy.MM.dd.HHmm>`
- **Adapter / network:** _<adapter, IPv4, LAN description; 10–30 announcing devices>_
- **Clean-box:** _<fresh Win11 build no.; NO .NET 10 / NO WindowsAppRuntime / NO VS>_

> Scaffold prepared by dev (Story 6.3). Project Lead fills the Result + Evidence columns on the dev LAN +
> the clean Win11 box. PASS / FAIL / N/A per row. Any FAIL → a separate minimal fix (+ regression test).

## A. Install-blocker fix (half B) — headless proof (dev)
- Option chosen: **Option 1 (truly self-contained)** / Option 2 — <decision + rationale>
- `Program.cs`: `Bootstrap.TryInitialize`/`Shutdown` removed; `Application.Start` retained. Build -warnaserror 0/0; Core 553/2 unchanged.
- Self-contained publish proof: publish folder contains the WinAppSDK runtime DLLs beside `ohSpy.App.exe` — _<file inventory: Microsoft.ui.xaml.dll, WinAppSDK framework DLLs, CoreMessaging.dll, …>_

## B. Performance budgets (§6)
| Budget | Target | Method | Result | Evidence (measured) |
|---|---|---|---|---|
| SC-001 launch → all devices | ≤ ~7 s | live-LAN stopwatch (+ clean box) | _PASS/FAIL_ | measured: ___ s |
| SC-002 dedup (per UDN) | 1/UDN, 0 dups | live-LAN 30 min | _PASS/FAIL_ | |
| SC-003 byebye → removed | < 2 s | farm byebye / power-off | _PASS/FAIL_ | measured: ___ s |
| SC-004 node expand (cold) | ≤ 2 s | live-LAN stopwatch | _PASS/FAIL_ | measured: ___ s |
| SC-005 View XML → browser | ≤ 2 s | live-LAN stopwatch | _PASS/FAIL_ | measured: ___ s |
| SC-009 advert → log row | ≤ 1 s | live-LAN / farm | _PASS/FAIL_ | measured: ___ s |
| SC-010 dbl-click → popup interactive | ≤ 1 s | live-LAN stopwatch | _PASS/FAIL_ | measured: ___ s |
| SC-011 invoke → result | ≤ 2 s | live-LAN stopwatch | _PASS/FAIL_ | measured: ___ s |
| Warm SCPD re-expand | ≤ 100 ms | live-LAN stopwatch | _PASS/FAIL_ | measured: ___ ms |
| Cold large-SCPD (GiantScpd) | ≤ 2 s, no freeze | **6.2 farm GiantScpd** (dev) + real IGD (PL) | _PASS/FAIL_ | farm: ___ s; eye-test: ___ |
| Sustained chatty-SSDP | ≥ 20 adv/s ≥ 30 s, no drops, stalls < 16 ms | **6.2 farm burst** (dev) + eye-test (PL) | _PASS/FAIL_ | farm: ___ adv/s; stalls > 1 s: 0; eye-test: ___ |

## C. SC-013 — interactive 1-hour run (real ohSpy.App; complement to 6.2 headless soak)
- Full-app resident memory over the hour (the figure 6.2's 200 MB headless caveat cross-references): start ___ MB → end ___ MB (plateau / no leak: _yes/no_)
- SSDP log ≤ 10,000: ___   ring ≤ 5,000: ___   event lists ≤ 5,000: ___   on-disk rolled: _yes/no_

## D. Clean-Win11 install dry-run
| Step | Expected | Result | Notes |
|---|---|---|---|
| SmartScreen | "More info → Run anyway" proceeds | _PASS/FAIL_ | |
| No Admin prompt | installer completes, no UAC | _PASS/FAIL_ | AC-12.3 |
| Install dir | `%LOCALAPPDATA%\Programs\ohSpy\` | _PASS/FAIL_ | |
| Start Menu shortcut | `Programs\ohSpy\ohSpy.lnk` | _PASS/FAIL_ | |
| Desktop checkbox | unchecked by default | _PASS/FAIL_ | |
| Launch → render | main window renders, NO 0x80670016 | _PASS/FAIL_ | AC-12.4 (the fix) |
| Discovery → tree ≤ ~7 s | SC-001 on clean box | _PASS/FAIL_ | measured: ___ s |
| Diagnostics written | `%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-<yyyyMMdd>.log` | _PASS/FAIL_ | AC-8.5 |
| Uninstall | install dir + shortcut removed; diagnostics PRESERVED | _PASS/FAIL_ | AC-12.5 |
| Rerun installer | silent upgrade, no "uninstall first" | _PASS/FAIL_ | D12 |

## E. Release-readiness (SM-5 / SM-6 / AC-13.x)
- Verification reports committed under `docs/`: 6.1 ✅, 6.2 soak ✅, 6.3 (this) ✅
- L&L-ready installer tagged: version `<yyyy.MM.dd.HHmm>`, SHA `<sha>`
- Chaos-hook spot-check: `git log --oneline` = <N> commits; `--grep="no-verify"` = <none/list> → _PASS/FAIL_
- Narrative arc (brief → PRD → architecture → epics → app) walkable, no retconning: _PASS/FAIL_

## Defects found & resolutions
| # | AC | Description | Resolution (commit / deferred) |
|---|----|-------------|-------------------------------|

## Conclusion
<PASS for the L&L / list of fixes / deferrals — the 6.1 conclusion style>
```

### Project Structure Notes

- **Production change (half B):** `src/ohSpy.App/Program.cs` (remove bootstrap), `src/ohSpy.App/ohSpy.App.csproj` (keep self-contained flags — Option 1 = no change, or drop them for Option 2). App-only; no Core.
- **Docs:** new `docs/verification/6.3-performance-budget-verification-<date>.md`; updates to `docs/DEVELOPMENT.md` (clean-machine smoke) + `architecture.md` Decision 12 (amendment) + `deferred-work.md` (resolve the entry).
- **Farm reuse:** `tests/ohSpy.Soak.Tests/Farm/*` — reuse, don't rebuild. Any new headless reproducer stays in the soak project (out of `ohSpy.sln`, `[Trait soak]`, chaos-hook-excluded).
- **No installer artefact committed** — `installer/out/` is build output; the Project Lead builds + tags the L&L build.

### Open questions (for the dev / Project Lead — #1 must be resolved before Task 1)

1. **⭐⭐ THE install-blocker decision (#1):** Option 1 (truly self-contained — recommended) vs Option 2 (framework-dependent + installer-carried `WindowsAppRuntimeInstall-x64.exe`). Default Option 1. **This is a Project-Lead decision; surface it in Task 0.** The dev specs Option 1; Option-2 delta noted in Task 1.
2. **Upgrade-detection prompt:** does a same-`AppId` rerun already replace silently (InnoSetup default), or does it prompt? Verify in the dry-run (AC-6.3.10); add `SetupAppMutex`/`CloseApplications` to `.iss` only if a prompt appears. Don't change the `AppId`.
3. **8-hour vs SC-013:** 6.2's 8-hour soak is OPTIONAL (Project Lead); SC-013's interactive 1-hour run on the real app is this story's required full-app-RSS evidence (the figure 6.2's 200 MB headless caveat defers to). Confirm the 1-hour interactive run is run pre-L&L.
4. **Farm reproducer form:** a small `[Trait soak]` test in `tests/ohSpy.Soak.Tests` vs a documented `dotnet test --filter` invocation against the existing farm for the GiantScpd cold-expand + ≥ 20 adv/s burst. Lean minimal — reuse the existing 6.2 farm + harness; don't build new farm devices.

### References

- [Source: epics.md#Story 6.3: Performance Budget Verification + Clean-Machine Install Dry-Run] (epic ACs ~L2068-2126)
- [Source: prd.md#§6 Performance Budgets] SC-001..SC-013 + Warm/Cold SCPD + chatty-SSDP target (L665-690); [#§9 SM-5/SM-6/SM-C3] (L756-766); [#§8.1 distribution] InnoSetup per-user (L723); [#§11] §6 8-hr/200 MB extrapolation note (L792)
- [Source: architecture.md#Decision 12 — Build / Packaging Pipeline Shape] (L1497-1690): self-contained claim L1641 (**corrected by this story's amendment**); installer behaviour L1532-1538; AC-12.1..12.6 L1679-1684; BuildInstaller target L1550-1564; versioning L1570; csproj snippet L1659-1666; [#Amendment A7] real Bootstrap signature (L2621-2649); [#Amendment A8] csproj completeness (L2653-2667); [#Decision 13] chaos hook (L3023-3052)
- [Source: src/ohSpy.App/Program.cs L19-51] the shipped `Bootstrap.TryInitialize` call (the defect to remove — Option 1)
- [Source: src/ohSpy.App/ohSpy.App.csproj L17-19] self-contained flags; [L61-86] BuildInstaller target
- [Source: installer/ohSpy.iss] the shipped InnoSetup script (per-user, AppId, shortcuts, diagnostics-preserved)
- [Source: Directory.Packages.props L12] Microsoft.WindowsAppSDK 2.1.3 (the minVersion coupling source)
- [Source: _bmad-output/implementation-artifacts/deferred-work.md L58-65] the bootstrap ↔ self-contained contradiction entry (the load-bearing blocker) — RESOLVE in Task 5
- [Source: docs/verification/6.1-manual-ui-verification-2026-06-05.md] the verification-report MODEL to mirror (header, AC table, defects table, conclusion)
- [Source: _bmad-output/implementation-artifacts/6-2-soak-tests-…md] previous story — the FARM primitives to REUSE (GiantScpd, ≥ 20 adv/s burst) + the 200 MB headless caveat SC-013 cross-references (⭐#4); the soak-project isolation discipline
- [Source: tests/ohSpy.Soak.Tests/Farm/FarmUpnpDevice.cs + Farm/DeviceFarm.cs] GiantScpd 120-action + advertiser/burst loop (reuse)
- [Source: docs/DEVELOPMENT.md] add the clean-machine install/run smoke (release-gate)
- MEMORY: `smoke-per-ui-story` (the clean-box launch-renders is the UI smoke — on the Project Lead's box); the four WinUI render-hazard memories (watch on the clean-box render)

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Opus 4.8, 1M context) — bmad-dev-story workflow.

### Debug Log References

- Core baseline (pre-change): `dotnet test tests/ohSpy.Core.Tests` → **553 passed / 2 skipped** (unchanged — the App-only fix touches no Core).
- App build: `dotnet build src/ohSpy.App -c Debug` → **Build succeeded, 0 errors, 1 warning** (the pre-existing benign WMC1506 at `MainWindow.xaml:162`, untouched).
- Self-contained publish: `dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained` → **430 files / ~215 MB** in the publish folder; WinAppSDK native runtime + .NET runtime confirmed beside `ohSpy.App.exe`; `runtimeconfig.json` carries `includedFrameworks` (self-contained marker).
- Soak project build: `dotnet build tests/ohSpy.Soak.Tests -warnaserror` → **0 warnings / 0 errors**.
- Farm reproducers: `dotnet test tests/ohSpy.Soak.Tests --filter "FullyQualifiedName~PerformanceBudgetReproducerTests"` → both PASS. Cold large-SCPD = **21 ms** (120/120 actions, 0 stalls, 0 exceptions); chatty-SSDP = **21.4 adv/s** over 12.1 s (0 stalls, 0 exceptions).
- Full soak quick suite: `dotnet test tests/ohSpy.Soak.Tests` → **8 passed / 0 failed** (6 pre-existing + 2 new reproducers).
- Chaos hook (as pre-commit runs it): `dotnet test --filter "category=chaos"` → **1 passed** (loads only Core.Tests; soak not in `ohSpy.sln`).
- Exclusion re-verified: `dotnet test tests/ohSpy.Soak.Tests --filter "category=chaos"` → "No test matches" (the new reproducer is `category=soak`, never picked up by the chaos hook).
- Chaos-hook discipline spot-check: `git log --oneline` = **60 commits**; `git log --all --grep="no-verify"` = **none** (0 bypasses).

### Completion Notes List

- **OPEN-Q #1 (the install-blocker decision) — RESOLVED: Option 1 (truly self-contained), Project-Lead decision (2026-06-06).** Implemented Option 1; did NOT implement Option 2.
- **The one real production fix (half B):** `src/ohSpy.App/Program.cs` no longer calls the framework-dependent `Bootstrap.TryInitialize` / `Bootstrap.Shutdown`. Removed: the bootstrap calls, the `0x80670016` `MessageBoxW` failure path, the `PackageVersion minVersion`, the `Microsoft.Windows.ApplicationModel.DynamicDependency` + `System.Runtime.InteropServices` usings, and the now-unused `MessageBoxW`/`MB_OK`/`MB_ICONERROR` P/Invoke plumbing. Kept `[STAThread] Main` + `Application.Start(_ => new App())` (+ `#pragma warning disable CA1806`) + the `StartupObject`/`DISABLE_XAML_GENERATED_MAIN` wiring. The csproj self-contained flags (`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true`) are retained and become **real** (no longer a startup no-op). No Core change.
- **Self-contained publish proof (the load-bearing evidence the `0x80670016` blocker is fixed):** the publish folder is a genuinely runnable self-contained bundle — WinAppSDK native runtime (`Microsoft.ui.xaml.dll`, `CoreMessagingXP.dll`, `DWriteCore.dll`, `Microsoft.WindowsAppRuntime.dll`, `MRM.dll`, `dwmcorei.dll`, `dcompi.dll`, `Microsoft.Internal.FrameworkUdk.dll`, …) **and** the .NET runtime (`coreclr.dll`, `clrjit.dll`, `System.Private.CoreLib.dll`, `hostfxr/hostpolicy`) sit beside `ohSpy.App.exe`; `runtimeconfig.json` carries `includedFrameworks`. A clean box needs nothing pre-installed. (`Microsoft.WindowsAppRuntime.Bootstrap.dll` is still laid down as part of the package but is no longer called — `Application.Start` binds the bundled runtime directly.)
- **Rejected alternative — Option 2 (framework-dependent):** keep `Bootstrap.TryInitialize`, drop the self-contained flags, and make the InnoSetup installer carry + run `WindowsAppRuntimeInstall-x64.exe` (≥ 2.1.3) as a `[Run]` prerequisite. Rejected: larger installer-script change, a per-machine runtime install step (possible elevation), and it re-introduces the runtime-version coupling that produced `0x80670016`.
- **Two farm-backed budget reproducers** wired in `tests/ohSpy.Soak.Tests/PerformanceBudgetReproducerTests.cs` (REUSING the 6.2 farm — nothing new built): cold large-SCPD via `GiantScpd` (**21 ms**, ≤ 2 s budget) + sustained chatty-SSDP via the `DeviceFarm` burst (**21.4 adv/s**, ≥ 20 floor). Both assert 0 UI-stalls > 1 s + 0 unhandled exceptions headlessly; the "no dropped frames / no UI freeze" eye-test is the Project Lead's. The chatty-SSDP reproducer is the reproducible complement to the deferred 6.1.14.
- **Docs/architecture:** new `docs/verification/6.3-performance-budget-verification-2026-06-06.md` (the 6.1-model scaffold, headless rows pre-filled); `docs/DEVELOPMENT.md` gains the permanent clean-machine install/run smoke gate + the farm-reproducer subsection; `architecture.md` Amendment **A32** corrects the L1641 "bootstrapper finds the bundled runtime" claim; `deferred-work.md` bootstrap-contradiction entry marked **RESOLVED**.
- **`installer/ohSpy.iss`:** NO change required for Option 1 (reconciled in the report §D — the only latent gap is the upgrade-detection prompt, which is verify-first in the Project Lead's dry-run; add `SetupAppMutex`/`CloseApplications` only if a prompt appears; do not change the `AppId`).
- **Gates:** Core 553/2 unchanged; App `-warnaserror` 0/0 bar the pre-existing WMC1506; soak project `-warnaserror` 0/0; full soak quick suite 8/8; chaos hook green; soak stays out of `ohSpy.sln`; no new `DiagCategories`.
- **Manual gates pending (Project Lead — story held at `review`, the 6.1 posture):** the live-LAN SC-* stopwatch walk (§B), the interactive 1-hour SC-013 run (§C), and the clean-Win11 install dry-run (§D, including the clean-box launch that confirms `0x80670016` is gone). The dev cannot run these headlessly.
- **No installer artefact committed** (`installer/out/` + `bin/`/`publish/` are build output) — the Project Lead builds + tags the L&L-ready build via `BuildInstaller`.

### File List

**Production (half B — App only, no Core):**
- `src/ohSpy.App/Program.cs` — removed the framework-dependent `Bootstrap.TryInitialize`/`Shutdown` + `0x80670016` `MessageBoxW` path (Option 1, truly self-contained).

**Tests (soak project — out of `ohSpy.sln`, `[Trait("category","soak")]`):**
- `tests/ohSpy.Soak.Tests/PerformanceBudgetReproducerTests.cs` — NEW: the two farm-backed budget reproducers (cold large-SCPD via GiantScpd; sustained chatty-SSDP via the DeviceFarm burst).
- `tests/ohSpy.Soak.Tests/Harness/SoakHarness.cs` — added a `GiantScpdDevice` accessor for the cold-large-SCPD reproducer (test-harness only).

**Docs / artefacts:**
- `docs/verification/6.3-performance-budget-verification-2026-06-06.md` — NEW: the verification report scaffold (6.1 model).
- `docs/DEVELOPMENT.md` — added the clean-machine install/run smoke (permanent release gate) + the farm-reproducer subsection.
- `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` — Amendment A32 + the L1641 inline correction.
- `_bmad-output/implementation-artifacts/deferred-work.md` — resolved the bootstrap ↔ self-contained contradiction entry.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — 6.3 status flips ready-for-dev → in-progress → review.
- `_bmad-output/implementation-artifacts/6-3-performance-budget-verification-clean-machine-install-dry-run.md` — task checkboxes, Dev Agent Record, Change Log, Status (this file).

### Review Findings

- [x] [Review][Patch] **FIXED 2026-06-06** — both stale lines updated to the Option-1 reality (L1503 "loaded directly from the bundle by `Application.Start` — no bootstrap initialiser"; L1686/AC-12.6 "`Application.Start` loads the bundled WinAppSDK runtime directly — no bootstrap initialiser call is required"), each with an A32 pointer. Original finding: stale AC-12.6 prose in architecture.md still references the bootstrap initialiser as a requirement [`_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md:L1503 + L1686`] — Two sentences still describe the bootstrap initialiser as active after A32: (1) L1503 in Decision 12's back-applies section: "bound at app startup via the bootstrap initialiser." (2) L1686 AC-12.6: "bootstrap initialiser runs before any WinUI type is touched." Both should be updated to reflect the Option-1 reality (Application.Start loads the bundled runtime directly; no bootstrapper). The L1641 inline correction and A32 amendment are in place and authoritative, but these two stale sentences are misleading — especially AC-12.6 which reads as an active requirement.

- [x] [Review][Defer] No exception handling around `Application.Start` in `Program.cs` [`src/ohSpy.App/Program.cs:23-25`] — deferred, pre-existing (the old `try/finally` only called `Bootstrap.Shutdown` in `finally`; it did not catch exceptions from the XAML runtime). If `Application.Start` throws, the unhandled exception propagates to the OS. Not introduced by this story.

- [x] [Review][Defer] Flaky `SubscriptionClientTests.Renew412_Lapses_RenewRefused_NoRetry_NoUnsubscribe` test observed on first run (552/1/2) but passed on second run (553/0/2) [`tests/ohSpy.Core.Tests/Events/SubscriptionClientTests.cs:364`] — deferred, pre-existing timing sensitivity not caused by this story. The baseline is 553/2; this flake predates Story 6.3.

### Change Log

| Date | Change |
|---|---|
| 2026-06-06 | Story 6.3 code-review (Sonnet, fresh context) APPROVED-WITH-MINOR-FIXES — 1 P2 patch (stale L1503 + L1686 prose in architecture.md); 2 deferred (pre-existing no-exception-handling around Application.Start; pre-existing flaky SubscriptionClientTests test). NO blockers. Focus-1 startup correctness confirmed: `Application.Start` without Bootstrap IS the complete self-contained WinUI 3 startup; WinAppSDK `BootstrapCommon.targets` explicitly gates bootstrap injection on `WindowsAppSDKSelfContained != 'true'` (verified in NuGet package). Focus-2 publish proof genuine (430 files, WinAppSDK native + .NET runtime, `runtimeconfig.json includedFrameworks` marker confirmed). Focus-3 reproducers meaningful (drive real `ServiceNodeViewModel.FetchScpdAsync`; burst measures live SSDP-log growth; both CAN fail). Gates confirmed: Core 553/2 (second run, first run had pre-existing flake); App -warnaserror 0/0 bar WMC1506; soak 8/8; chaos 1/1; soak excluded from chaos hook + sln. Verification report §D clean-box rows correctly unmarked (PASS/FAIL placeholders only). Story held at review; Project Lead manual gates pending. |
| 2026-06-06 | Story 6.3 implemented via dev-story (claude-opus-4-8[1m]). Install-blocker FIX (Option 1, truly self-contained): removed the framework-dependent `Bootstrap.TryInitialize` from `Program.cs`; proved the self-contained publish (430 files; WinAppSDK + .NET runtime bundled beside the exe; `runtimeconfig.json` `includedFrameworks`). Wired two farm-backed budget reproducers (cold large-SCPD 21 ms; sustained chatty-SSDP 21.4 adv/s). Scaffolded the 6.3 verification report; added the clean-machine install/run smoke release gate to `docs/DEVELOPMENT.md`; architecture Amendment A32; resolved the deferred-work bootstrap-contradiction entry. Gates: Core 553/2 unchanged; App -warnaserror 0/0 bar WMC1506; soak 8/8; chaos green. Status ready-for-dev → in-progress → review. Live-LAN SC-* walk + 1-hr SC-013 + clean-Win11 install dry-run held as Project-Lead manual gates (6.1 posture). |
