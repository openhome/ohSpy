# ohSpy

Native Windows desktop UPnP inspector for Linn engineers — a modern replacement for Intel's discontinued *Device Spy*.

ohSpy enumerates UPnP devices on the local network, lets you browse their service descriptions, invoke actions, and subscribe to events. It is a focused diagnostic tool built for Linn streamer development; it is not a media player.

See the [architecture document](_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md) for the binding technical contract.

## Prerequisites

- **.NET 10 SDK** (LTS) — released 2025-11-11. Install via `winget install Microsoft.DotNet.SDK.10` or from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Visual Studio 2026** (Community or higher) with the *Windows application development* workload — optional for IDE work; not required for command-line builds.
- **InnoSetup 6** — for building the installer artefact. Install from <https://jrsoftware.org/isdl.php>.
- **Windows 11** (Windows 10 22H2+ should also work; only Windows 11 is supported).
- **Git for Windows** — bundles Bash, which the pre-commit chaos hook requires.

## First-time clone setup

After cloning, every contributor must run this **once** in the repo root to wire up the pre-commit chaos hook (Git deliberately does not permit committed config to redirect hooks — phishing protection — so this step cannot be automated by the repo):

```powershell
git config core.hooksPath .githooks
```

Verify with `git config --get core.hooksPath` — it should print `.githooks`.

## Build

From the repo root:

```powershell
# Restore + build the whole solution. Zero warnings is enforced (TreatWarningsAsErrors=true).
dotnet build

# Run the test suite. Story 1.1 ships zero tests; chaos tests come online in Story 1.6.
dotnet test

# Publish a self-contained x64 binary (bundles .NET 10 + Windows App Runtime).
dotnet publish src\ohSpy.App -c Release -r win-x64 --self-contained

# Build the InnoSetup installer (requires InnoSetup 6 at C:\Program Files\Inno Setup 6\).
# The output lands in installer\out\ohSpy-setup-<yyyy.MM.dd.HHmm>-x64.exe.
dotnet build src\ohSpy.App -t:BuildInstaller -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
```

## Architecture

The full system architecture, ADRs, decisions, and amendments live at:

- **[architecture.md](_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md)** — the binding technical contract (~2700 lines).
- [PRD](_bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md) — product requirements.
- [Epics](_bmad-output/planning-artifacts/epics.md) — story breakdown.

Key decisions worth knowing up-front:

- **Unpackaged WinUI 3 + InnoSetup, not MSIX** (Decision 12). Per-user install at `%LOCALAPPDATA%\Programs\ohSpy\`. Diagnostics persist across uninstall at `%LOCALAPPDATA%\ohSpy\diagnostics\`.
- **No CI** (Decision 12). Solo greenfield. The pre-commit chaos hook (Decision 13) is the regression net.
- **Central Package Management** (Amendment A3). All NuGet versions live in `Directory.Packages.props`; csproj files carry bare `<PackageReference>` entries with no `Version` attribute.
- **Async discipline enforced at build time** (Pattern 6). `Microsoft.VisualStudio.Threading.Analyzers` is wired into `Directory.Build.props` and bans `.Result` / `.Wait()` solution-wide.
- **Core ↔ App boundary** (Pattern 2). `ohSpy.Core` targets `net10.0` (no `-windows` suffix) and must not reference `Microsoft.WindowsAppSDK`. NetArchTest enforces this from Story 1.6.
