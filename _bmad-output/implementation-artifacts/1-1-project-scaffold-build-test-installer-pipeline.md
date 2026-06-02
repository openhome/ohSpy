---
baseline_commit: 66ffccc91396942a947f74ed7a4325181f7933e0
---

# Story 1.1: Project Scaffold & Build/Test/Installer Pipeline

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an **ohSpy developer**,
I want **a clone-and-build .NET 10 / WinUI 3 solution wired to an InnoSetup installer pipeline and a pre-commit chaos hook**,
so that **I can write subsequent stories against a stable foundation with one-step `build`, `test`, and `package` commands**.

## Acceptance Criteria

> Each AC is restated verbatim from epics.md §Story 1.1 (lines 418–462). The architecture-level AC IDs (AC-12.x, AC-13.x) cited inline trace back to architecture.md §Decision-12 and §Decision-13.

### AC-1 — Solution builds with quality gates

**Given** a fresh clone of the repository on Windows 11 with .NET 10 SDK + Visual Studio 2026 + InnoSetup 6 installed
**When** I run `dotnet build` from the repo root
**Then** the solution containing `ohSpy.App`, `ohSpy.Core`, and `ohSpy.Core.Tests` builds without warnings
**And** `TreatWarningsAsErrors=true` is enforced via `Directory.Build.props` (A4)
**And** `LangVersion=13`, `Nullable=enable`, `ImplicitUsings=enable` are configured solution-wide
**And** `Microsoft.VisualStudio.Threading.Analyzers` is referenced via `Directory.Build.props` (A4) and active in every project

### AC-2 — Test runner discovers tests

**Given** the solution is built
**When** I run `dotnet test`
**Then** the test runner discovers `ohSpy.Core.Tests` and reports 0 failures (zero or more tests, all green)

### AC-3 — Installer build target produces signed-shape artefact

**Given** I want to package the app
**When** I run `dotnet build src/ohSpy.App -t:BuildInstaller -p:Configuration=Release`
**Then** the target depends on `Publish` and produces `installer/out/ohSpy-setup-<yyyy.MM.dd.HHmm>-x64.exe` (AC-12.2)
**And** the installer script `installer/ohSpy.iss` is present and committed
**And** the publish profile bundles the .NET 10 runtime AND the Windows App Runtime via `<SelfContained>true</SelfContained>` + `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` (D12)
**And** `<WindowsPackageType>None</WindowsPackageType>` is set in `ohSpy.App.csproj` (AC-12.6)
**And** the build succeeds for the `win-x64` RID with the `win-arm64` publish profile also present in `Properties/PublishProfiles/` but not built by default

### AC-4 — Clean-machine install + bootstrap

**Given** the installer artifact exists
**When** I run it on a clean Windows 11 machine with no .NET 10 or WindowsAppRuntime pre-installed (AC-12.4)
**Then** the installer installs to `%LOCALAPPDATA%\Programs\ohSpy\` per-user with no Administrator prompt (AC-12.3)
**And** the app launches and shows an empty WinUI 3 window after the user clicks past the SmartScreen warning
**And** `Bootstrap.TryInitialize` runs in `Program.cs` before any WinUI type is touched (AC-12.6)
**And** the bootstrap failure path is wired (native message box + exit) for the case where runtime binding fails

### AC-5 — Uninstall preserves diagnostics

**Given** I uninstall via Apps & Features
**When** the uninstaller runs
**Then** the install dir and Start Menu shortcut are removed (AC-12.5)
**And** `%LOCALAPPDATA%\ohSpy\diagnostics\` is preserved (no diagnostic content yet, but the directory survives if present)

### AC-6 — Pre-commit chaos hook scaffold in place

**Given** the repository is cloned fresh
**When** I run the Story 1 init steps that configure the chaos hook
**Then** `git config core.hooksPath .githooks` has been set as part of the documented init flow (AC-13.2)
**And** `.githooks/pre-commit` exists, is executable, and contains the chaos-test shell command (AC-13.1)
**And** committing a change runs the pre-commit hook (currently passing trivially because no chaos tests exist yet — full chaos-test integration lands in Story 1.6)

### AC-7 — Root configuration files complete and correct

**Given** I look at root-level configuration
**When** I inspect the repo
**Then** `Directory.Packages.props` (A3) is present at the repo root with `ManagePackageVersionsCentrally=true` and pins for every dependency the architecture names
**And** `global.json` pins the .NET SDK to 10.0.x
**And** `.editorconfig` carries the `dotnet new editorconfig` defaults
**And** `.gitignore` covers `bin/`, `obj/`, `installer/out/`, and any other standard .NET ignores

## Tasks / Subtasks

> Tasks are ordered to produce a green `dotnet build` as early as possible, then layer the installer + chaos-hook scaffolds. AC mappings are explicit. **Do not deviate from the architecture's pinned versions / paths / patterns** — they are the contract.

### Task 1 — Run the initialization command sequence (AC: #1, #2, #7)

- [x] **1.0** Verify the WinUI template is installed before running the init sequence:
  ```powershell
  dotnet new list winui
  ```
  If `dotnet new list winui` shows no matches OR `dotnet new winui ...` errors with "No templates found", install the template package first:
  ```powershell
  dotnet new install Microsoft.WindowsAppSDK.ProjectTemplates
  ```
  After install, retry `dotnet new list winui` — you should see a `winui` template (short name may also surface as `winui3` depending on package version). If only `winui3` is listed, substitute `winui3` for `winui` in Task 1.1 below — but otherwise keep the sequence verbatim.
- [x] **1.1** From the repo root (`C:\work\ohSpy`), run the exact `dotnet new` sequence below — in order, no substitutions [Source: architecture.md §Initialization Command, lines 101–111]:
  ```powershell
  dotnet new winui     -n ohSpy.App        -o src\ohSpy.App
  dotnet new classlib  -n ohSpy.Core       -o src\ohSpy.Core       --framework net10.0
  dotnet new xunit     -n ohSpy.Core.Tests -o tests\ohSpy.Core.Tests --framework net10.0
  dotnet new sln       -n ohSpy
  dotnet sln add src\ohSpy.App\ohSpy.App.csproj src\ohSpy.Core\ohSpy.Core.csproj tests\ohSpy.Core.Tests\ohSpy.Core.Tests.csproj
  dotnet add src\ohSpy.App\ohSpy.App.csproj reference src\ohSpy.Core\ohSpy.Core.csproj
  dotnet add tests\ohSpy.Core.Tests\ohSpy.Core.Tests.csproj reference src\ohSpy.Core\ohSpy.Core.csproj
  ```
- [x] **1.2** Verify the three projects target the correct TFMs:
  - `ohSpy.App.csproj` → `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>` (default from `dotnet new winui`; do not change the Windows SDK version)
  - `ohSpy.Core.csproj` → `<TargetFramework>net10.0</TargetFramework>` (no `-windows` suffix — Core must be testable without WinUI)
  - `ohSpy.Core.Tests.csproj` → `<TargetFramework>net10.0</TargetFramework>`
- [x] **1.3** Add `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>` to `ohSpy.App.csproj` (needed for publish-profile RID resolution).

### Task 2 — Author root configuration files (AC: #1, #7)

- [x] **2.1** Write `global.json` pinning the .NET SDK to channel 10.0.x:
  ```json
  {
    "sdk": {
      "version": "10.0.100",
      "rollForward": "latestFeature",
      "allowPrerelease": false
    }
  }
  ```
  > The exact `version` should match the .NET 10 SDK Simon has installed; use `dotnet --list-sdks` to confirm and set the closest 10.0.x patch. `rollForward: latestFeature` permits feature-band roll-forward (10.0.100 → 10.0.2xx) but pins the major.minor.
- [x] **2.2** Write `.editorconfig` at the repo root using `dotnet new editorconfig` defaults [Source: architecture.md §lines 1965–1967, A4]. Add ONE override appended at the bottom:
  ```ini
  # VSTHRD100 (async void without try/catch) is exempt in test fixtures — Moq + xUnit patterns require it.
  [tests/**/*.cs]
  dotnet_diagnostic.VSTHRD100.severity = none
  ```
  > Do not author the rest by hand; run `dotnet new editorconfig` and accept the generated file, then append the test-tree exemption.
- [x] **2.3** Write `.gitignore` covering [Source: architecture.md §File Organization Patterns]:
  ```gitignore
  # Build outputs
  bin/
  obj/
  
  # Publish + installer artefacts
  publish/
  installer/out/
  
  # IDE
  .vs/
  *.user
  
  # Test results
  TestResults/
  [Cc]overage*/
  
  # BMad scratch
  _bmad-output/*.tmp
  
  # OS
  Thumbs.db
  .DS_Store
  ```
  > A standard `dotnet new gitignore` followed by appending `installer/out/` and `_bmad-output/*.tmp` is acceptable.
- [x] **2.4** Write `Directory.Packages.props` at the repo root [Source: architecture.md §A3, lines 2407–2428]:
  ```xml
  <Project>
    <PropertyGroup>
      <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
      <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
    </PropertyGroup>
    <ItemGroup>
      <PackageVersion Include="CommunityToolkit.Mvvm"                          Version="8.4.0" />
      <PackageVersion Include="Microsoft.Extensions.DependencyInjection"       Version="10.0.0" />
      <PackageVersion Include="Microsoft.Extensions.Logging"                   Version="10.0.0" />
      <PackageVersion Include="Microsoft.Extensions.Options"                   Version="10.0.0" />
      <PackageVersion Include="Microsoft.VisualStudio.Threading.Analyzers"     Version="17.11.20" />
      <PackageVersion Include="Microsoft.WindowsAppSDK"                        Version="2.1.3" />
      <PackageVersion Include="Microsoft.NET.Test.Sdk"                         Version="17.12.0" />
      <PackageVersion Include="xunit"                                          Version="2.9.2" />
      <PackageVersion Include="xunit.runner.visualstudio"                      Version="2.8.2" />
      <PackageVersion Include="Moq"                                            Version="4.20.72" />
      <PackageVersion Include="FluentAssertions"                               Version="8.0.0" />
      <PackageVersion Include="NetArchTest.Rules"                              Version="1.3.2" />
    </ItemGroup>
  </Project>
  ```
  > Resolve the `.x` patch versions in A3 against `nuget.org` at init time. The patch numbers shown above are conservative defaults — bump only to the latest STABLE patch for each line; do not move major/minor away from architecture pins. `Microsoft.WindowsAppSDK` is **2.1.3 exactly** per Decision 12.
  > **Version-skew correction vs A3:** The architecture's A3 table lists `xunit.runner.visualstudio` at `3.0.x`, but `3.0.x` targets **xUnit v3**, not v2. With `xunit` pinned to `2.9.2` per A3, the matching runner is `2.8.x`. This story pins `2.8.2`. Record the discrepancy in Completion Notes so a future architecture amendment can correct A3.
  > **Why `Microsoft.NET.Test.Sdk` is pinned here:** `CentralPackageTransitivePinningEnabled=true` requires every transitive `PackageReference` (including those `dotnet new xunit` adds implicitly) to resolve to a `PackageVersion` entry. Omitting the SDK pin causes `dotnet restore` to fail NU1010 on the test project. `17.12.0` is the latest stable at architecture time; bump within the 17.x line as needed.
- [x] **2.5** Write `Directory.Build.props` at the repo root [Source: architecture.md §A4, lines 2434–2451]:
  ```xml
  <Project>
    <PropertyGroup>
      <LangVersion>13</LangVersion>
      <Nullable>enable</Nullable>
      <ImplicitUsings>enable</ImplicitUsings>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
      <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
      <AnalysisLevel>latest</AnalysisLevel>
      <AnalysisMode>recommended</AnalysisMode>
    </PropertyGroup>

    <!-- Pattern 6 async discipline: .Result / .Wait() / .GetAwaiter().GetResult() lint -->
    <ItemGroup>
      <PackageReference Include="Microsoft.VisualStudio.Threading.Analyzers" PrivateAssets="all" />
    </ItemGroup>
  </Project>
  ```
- [x] **2.6** Write the Core-project-local override at `src/ohSpy.Core/Directory.Build.props` [Source: architecture.md §A4, lines 2460–2468]:
  ```xml
  <Project>
    <Import Project="..\..\Directory.Build.props" />
    <!-- Boundary: Microsoft.WindowsAppSDK and Microsoft.UI.* must not be referenced from Core.
         Static enforcement: NetArchTest in tests/.../Architecture/CoreAppBoundaryTests.cs (Story 1.6).
         Build-time enforcement: this csproj does NOT add Microsoft.WindowsAppSDK in its PackageReferences. -->
  </Project>
  ```
- [x] **2.7** Strip the `Version` attribute from EVERY `<PackageReference>` in the three csproj files (Central Package Management requires versions to live exclusively in `Directory.Packages.props`).

### Task 3 — Convert App project to unpackaged WinUI 3 (AC: #3, #4)

- [x] **3.1** In `src/ohSpy.App/ohSpy.App.csproj`, replace the default packaged-WinUI properties with the unpackaged set [Source: architecture.md §Decision-12, lines 1598–1609]:
  ```xml
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>                     <!-- Unpackaged -->
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>     <!-- bundle WAS -->
    <SelfContained>true</SelfContained>                               <!-- bundle .NET -->
    <PublishSingleFile>false</PublishSingleFile>                      <!-- installer wraps -->
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <RootNamespace>ohSpy.App</RootNamespace>
    <AssemblyName>ohSpy.App</AssemblyName>
    <!-- Pin our Program.Main as the entry point; disable the XAML-compiler-generated Main from
         App.xaml so we don't get CS0017 "multiple entry points". Without this pair, the WinUI
         template's XAML generator emits its own Main and conflicts with Program.cs. -->
    <StartupObject>ohSpy.App.Program</StartupObject>
    <DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>
  </PropertyGroup>
  ```
- [x] **3.2** Remove any `<AppxPackage>`, `<PackageCertificateKeyFile>`, or MSIX-related properties the WinUI template added. Remove `Package.appxmanifest` if present (unpackaged apps use `app.manifest` only).
- [x] **3.3** Create / retain `src/ohSpy.App/app.manifest` (the Win32 assembly manifest; `dotnet new winui` produces a baseline — keep it). Ensure `<dpiAware>` and `<longPathAware>` entries are present per the WinUI template default.

### Task 4 — Author the bootstrap initializer in Program.cs (AC: #4)

- [x] **4.1** Create `src/ohSpy.App/Program.cs` with the exact bootstrap pattern below [Source: architecture.md §Decision-12, lines 1564–1594]:
  ```csharp
  using System;
  using System.Runtime.InteropServices;
  using Microsoft.Windows.ApplicationModel.DynamicDependency;
  using Windows.ApplicationModel;

  namespace ohSpy.App;

  internal static class Program
  {
      [STAThread]
      private static int Main(string[] args)
      {
          // Bind to the Windows App Runtime self-contained-published alongside this exe.
          // MUST run before any Microsoft.UI.Xaml type is touched.
          var bootstrapResult = Bootstrap.TryInitialize(
              majorMinorVersion: 0x00020001,            // WindowsAppSDK 2.1.x
              versionTag: "",
              minVersion: new PackageVersion(2, 1, 3, 0),
              out _);

          if (bootstrapResult < 0)
          {
              // Bootstrap failed — runtime missing or mismatched.
              // No WinUI available yet; no diagnostic sink yet. Native message box + exit is terminal.
              _ = MessageBoxW(
                  IntPtr.Zero,
                  $"Windows App Runtime initialisation failed (0x{bootstrapResult:X8}).\n\n" +
                  "Reinstall ohSpy. If the problem persists, contact the ohSpy maintainers.",
                  "ohSpy",
                  MB_OK | MB_ICONERROR);
              return bootstrapResult;
          }

          try
          {
              Microsoft.UI.Xaml.Application.Start(_ => new App());
          }
          finally
          {
              Bootstrap.Shutdown();
          }
          return 0;
      }

      private const uint MB_OK = 0x0u;
      private const uint MB_ICONERROR = 0x10u;

      [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
      private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
  }
  ```
  > **Critical invariant:** `Bootstrap.TryInitialize` MUST be called before `Application.Start`. Touching any `Microsoft.UI.*` type before this point causes a TypeLoadException because the WindowsAppSDK runtime isn't bound yet.
- [x] **4.2** In `App.xaml` ensure no `x:Class` initialization touches WinUI before `Application.Start` runs (the default `dotnet new winui` template is correct; verify after porting).
- [x] **4.3** If `dotnet new winui` produced an `App.xaml.cs` with `[STAThread] public static void Main()`, **delete that Main and use only the one in Program.cs**. Two `Main`s causes CS0017 (multiple entry points). The `<StartupObject>` + `DISABLE_XAML_GENERATED_MAIN` pair from Task 3.1 already pins the entry point to `Program.Main`, but a literal `public static void Main` body in `App.xaml.cs` would still conflict at compile time — delete it.

### Task 5 — Author publish profiles (AC: #3)

- [x] **5.1** Create `src/ohSpy.App/Properties/PublishProfiles/win-x64.pubxml`. **Do NOT set `<PublishDir>`** — let MSBuild compute the default so the `BuildInstaller` target's `$(InstallerPublishDir)` and a plain `dotnet publish` agree on path:
  ```xml
  <Project>
    <PropertyGroup>
      <Configuration>Release</Configuration>
      <Platform>Any CPU</Platform>
      <PublishProtocol>FileSystem</PublishProtocol>
      <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
      <RuntimeIdentifier>win-x64</RuntimeIdentifier>
      <SelfContained>true</SelfContained>
      <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
      <PublishSingleFile>false</PublishSingleFile>
      <PublishReadyToRun>false</PublishReadyToRun>
    </PropertyGroup>
  </Project>
  ```
- [x] **5.2** Create `src/ohSpy.App/Properties/PublishProfiles/win-arm64.pubxml` — identical to the above except `<RuntimeIdentifier>win-arm64</RuntimeIdentifier>`. Not built by default; present for manual ARM64 publish.

### Task 6 — Author the `BuildInstaller` MSBuild target (AC: #3)

- [x] **6.1** Append the `BuildInstaller` target to `src/ohSpy.App/ohSpy.App.csproj` [Source: architecture.md §Decision-12, lines 1532–1553, with adapter changes to make `$(PublishDir)` deterministic]:
  ```xml
  <Target Name="BuildInstaller"
          DependsOnTargets="Publish"
          Condition="'$(RuntimeIdentifier)' == 'win-x64' Or '$(BuildInstaller)' == 'true'">

    <PropertyGroup>
      <InnoSetupCompiler Condition="'$(InnoSetupCompiler)' == ''">$(ProgramFiles)\Inno Setup 6\ISCC.exe</InnoSetupCompiler>
      <InstallerOutputDir>$(MSBuildThisFileDirectory)..\..\installer\out</InstallerOutputDir>
      <InstallerVersion>$([System.DateTime]::UtcNow.ToString("yyyy.MM.dd.HHmm"))</InstallerVersion>
      <!-- Deterministic publish path: matches the default Publish target output regardless of whether
           a publish profile was used. Avoids the case where the pubxml's PublishDir and MSBuild's
           default disagree, resulting in an empty installer. -->
      <InstallerPublishDir>$(MSBuildThisFileDirectory)bin\$(Configuration)\$(TargetFramework)\$(RuntimeIdentifier)\publish\</InstallerPublishDir>
    </PropertyGroup>

    <Error Condition="!Exists('$(InnoSetupCompiler)')"
           Text="Inno Setup compiler not found at '$(InnoSetupCompiler)'. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or override InnoSetupCompiler." />

    <Error Condition="!Exists('$(InstallerPublishDir)ohSpy.App.exe')"
           Text="Publish output not found at '$(InstallerPublishDir)'. The Publish target should have produced this. Confirm RuntimeIdentifier=win-x64 is set on the build command line." />

    <MakeDir Directories="$(InstallerOutputDir)" />

    <Exec Command="&quot;$(InnoSetupCompiler)&quot; /Q /DPublishDir=&quot;$(InstallerPublishDir.TrimEnd('\'))&quot; /DOutputDir=&quot;$(InstallerOutputDir)&quot; /DVersion=$(InstallerVersion) &quot;$(MSBuildThisFileDirectory)..\..\installer\ohSpy.iss&quot;" />

    <Message Text="Installer built: $(InstallerOutputDir)\ohSpy-setup-$(InstallerVersion)-x64.exe" Importance="high" />
  </Target>
  ```
  > **Documented one-liner** (this is the verified-good invocation; document verbatim in README.md):
  > ```powershell
  > dotnet build src\ohSpy.App -t:BuildInstaller -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
  > ```
  > The `DependsOnTargets="Publish"` clause auto-runs `dotnet publish` first. The two `<Error>` guards above fail fast with diagnostics rather than silently producing an empty installer if the publish step's output landed somewhere unexpected.
- [x] **6.2** Remove the hard-coded `<PublishDir>` from the pubxml files written in Task 5 (delete the `<PublishDir>...</PublishDir>` line from both `win-x64.pubxml` and `win-arm64.pubxml`) — let MSBuild compute the default path so the BuildInstaller target and a plain `dotnet publish` write to the same place. The pubxml retains only `<RuntimeIdentifier>`, `<SelfContained>`, `<WindowsAppSDKSelfContained>`, and the other config knobs.

### Task 7 — Author the InnoSetup script (AC: #3, #4, #5)

- [x] **7.1** Create `installer/ohSpy.iss` with this minimal-but-correct content. The MSBuild target passes `PublishDir`, `OutputDir`, and `Version` as `/D` preprocessor symbols:
  ```pascal
  ; InnoSetup 6 script for ohSpy — unsigned, per-user, unpackaged WinUI 3.
  ; Invoked by the BuildInstaller MSBuild target; see src/ohSpy.App/ohSpy.App.csproj.

  #ifndef PublishDir
    #error "PublishDir must be provided via /DPublishDir=... (the dotnet publish output folder)."
  #endif
  #ifndef OutputDir
    #error "OutputDir must be provided via /DOutputDir=... (where to write the setup.exe)."
  #endif
  #ifndef Version
    #define Version "0.0.0.0"
  #endif

  #define AppName     "ohSpy"
  #define AppPublisher "Linn"
  #define AppExeName  "ohSpy.App.exe"

  [Setup]
  ; Generate ONCE via PowerShell `[guid]::NewGuid()`, paste here, record in Completion Notes,
  ; then NEVER change. Changing the AppId across builds causes side-by-side install instead
  ; of upgrade. The placeholder below is deliberately invalid InnoSetup syntax — the script
  ; will not compile until you replace it.
  AppId=REPLACE-WITH-FRESH-GUID-FROM-NewGuid
  AppName={#AppName}
  AppVersion={#Version}
  AppPublisher={#AppPublisher}
  DefaultDirName={localappdata}\Programs\{#AppName}
  DefaultGroupName={#AppName}
  DisableDirPage=yes
  DisableProgramGroupPage=yes
  PrivilegesRequired=lowest
  PrivilegesRequiredOverridesAllowed=dialog
  ; OutputDir resolves to an absolute path passed by MSBuild via /DOutputDir=...
  ; Do not treat as relative — InnoSetup would otherwise interpret relative paths
  ; relative to this .iss file's location, not the MSBuild invocation CWD.
  OutputDir={#OutputDir}
  OutputBaseFilename=ohSpy-setup-{#Version}-x64
  Compression=lzma2
  SolidCompression=yes
  ArchitecturesAllowed=x64compatible
  ArchitecturesInstallIn64BitMode=x64compatible
  UninstallDisplayIcon={app}\{#AppExeName}
  WizardStyle=modern

  [Files]
  Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

  [Icons]
  Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
  Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

  [Tasks]
  Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

  [Run]
  Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

  ; Uninstall preserves %LOCALAPPDATA%\ohSpy\diagnostics\ — diagnostic logs persist across uninstall (AC-12.5).
  ; Default uninstall removes [Files] entries (app dir) and [Icons] only. The diagnostics directory
  ; under {localappdata}\ohSpy\diagnostics is NOT listed here, so it is never touched. Explicitly do
  ; NOT add an [UninstallDelete] entry for it.
  ```
  > Source: structure derived from architecture.md §Decision-12 lines 1520–1530. The `AppId` GUID above is a stable per-app identifier — generate a fresh GUID at init time and keep it constant across all future builds (changing it makes upgrades install side-by-side instead of replacing). Use `[guid]::NewGuid()` in PowerShell.

### Task 8 — Author the pre-commit chaos hook scaffold (AC: #6)

- [x] **8.1** Create the `.githooks/` directory at the repo root.
- [x] **8.2** Write `.githooks/pre-commit` (no extension, LF line endings, executable bit set) [Source: architecture.md §Decision-13, lines 2552–2561]:
  ```bash
  #!/usr/bin/env bash
  # Runs the chaos test category to catch NFR-P2 regressions.
  # Wall-clock budget: ~5s. Fail the commit if any chaos test fails.
  # Full chaos-test integration lands in Story 1.6. Until then, the filter matches zero tests
  # and exits 0 trivially.
  set -e
  echo "Running chaos tests..."
  dotnet test --filter "Trait=category&Value=chaos" --nologo --verbosity quiet
  ```
- [x] **8.3** Mark the hook executable in the index. Windows' `core.fileMode` defaults to `false`, so the working-tree filesystem bit is ignored — the bit must live in Git's index, which is what `--chmod=+x` records. Two-step sequence:
  ```powershell
  git add .githooks/pre-commit
  git update-index --chmod=+x .githooks/pre-commit
  ```
  Verify with `git ls-files -s .githooks/pre-commit` — the file mode column must show `100755`, not `100644`. Re-run the `update-index --chmod=+x` step if the file is ever rewritten (rewrite resets the bit).
- [x] **8.4** Document — in `README.md` — the one-time setup step every cloner must run [Source: architecture.md §Decision-13, line 2549]:
  ```powershell
  git config core.hooksPath .githooks
  ```
  > This cannot be set automatically by the repo (Git deliberately does not allow committed config to redirect hooks for security — phishing protection). Each cloner must run it locally. The README must call this out explicitly.
- [x] **8.5** **Windows-without-Git-Bash fallback:** Git for Windows ships with Bash, so the shebang `#!/usr/bin/env bash` is satisfied on Simon's machine and any standard Windows dev box. If a future contributor lacks Git Bash, document the PowerShell shim option (`.githooks/pre-commit.ps1` + a stub `.githooks/pre-commit` that calls it) — but DO NOT ship the shim for v1; the bash version is canonical [Source: architecture.md §Decision-13 lines 2563–2564].

### Task 9 — Wire up test project references (AC: #2)

- [x] **9.1** Add to `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` (no `Version` attributes — versions come from `Directory.Packages.props`):
  ```xml
  <ItemGroup>
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Moq" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NetArchTest.Rules" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
  </ItemGroup>
  ```
  > `Microsoft.NET.Test.Sdk` is brought in by `dotnet new xunit` but its version may need to be pinned in `Directory.Packages.props` if Central Package Management complains. Add `<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.x" />` to `Directory.Packages.props` if so.
- [x] **9.2** Confirm the project reference to Core exists (the last `dotnet add reference` line in Task 1.1 already added it; verify via `dotnet list tests/ohSpy.Core.Tests reference`).

### Task 10 — Verify the build pipeline end-to-end (AC: #1, #2, #3, #7)

- [x] **10.1** From the repo root run `dotnet restore` — must succeed with no warnings about missing central package versions.
- [x] **10.2** Run `dotnet build` — must succeed with **zero warnings** (TreatWarningsAsErrors will fail the build otherwise). If a warning surfaces, EITHER fix the underlying issue OR document a justified suppression with a code comment — do not blanket-disable.
- [x] **10.3** Run `dotnet test` — must report 0 failed (0 tests is acceptable since none exist yet; the runner discovery must succeed).
- [x] **10.4** Run `dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained` — must produce a publish folder containing `ohSpy.App.exe`, the .NET 10 runtime files, and the Windows App Runtime binaries.
- [x] **10.5** Run the documented one-liner from Task 6.1: `dotnet build src\ohSpy.App -t:BuildInstaller -c Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true` — must produce `installer/out/ohSpy-setup-<timestamp>-x64.exe`. If InnoSetup 6 is not installed, the target must fail with the explicit `<Error>` message authored in Task 6.1.
- [x] **10.6** **Analyzer-coverage smoke test (verifies AC-1's "active in every project" clause).** Temporarily introduce a `.Result` blocking call in each of the three projects in turn and confirm the build fails with VSTHRD002 in each case:
  1. In `src/ohSpy.Core/`, add a throwaway file `__AnalyzerSmokeTest.cs` containing:
     ```csharp
     namespace ohSpy.Core;
     internal static class __AnalyzerSmokeTest
     {
         public static void Trip() => System.Threading.Tasks.Task.Delay(1).Wait();
     }
     ```
     Run `dotnet build src/ohSpy.Core`. Expected: VSTHRD002 (or VSTHRD110) build error. Delete the file.
  2. Repeat in `src/ohSpy.App/` (file `__AnalyzerSmokeTest.cs`, namespace `ohSpy.App`). Expected: same error. Delete the file.
  3. Repeat in `tests/ohSpy.Core.Tests/` (file `__AnalyzerSmokeTest.cs`, namespace `ohSpy.Core.Tests`). Expected: same error (note: VSTHRD100 is exempted in `tests/**` per the editorconfig override from Task 2.2, but VSTHRD002 / 003 / 110 remain active). Delete the file.
  4. Record in Completion Notes: "Analyzer smoke test confirmed VSTHRD lint active in Core / App / Tests."

### Task 11 — Clean-machine install validation (AC: #4, #5)

> Manual / one-shot. Acceptance evidence: a short screenshot or a note in Completion Notes confirming the path was walked end-to-end.

- [ ] **11.1** Copy the produced `ohSpy-setup-<timestamp>-x64.exe` to a clean Windows 11 VM (or a colleague's machine) with **no .NET 10 SDK/runtime and no WindowsAppRuntime pre-installed**.
- [ ] **11.2** Double-click to run. Confirm SmartScreen warning appears; click "More info" → "Run anyway".
- [ ] **11.3** Confirm **no Administrator prompt** appears (PrivilegesRequired=lowest).
- [ ] **11.4** Confirm installation path is `%LOCALAPPDATA%\Programs\ohSpy\` (AC-12.3).
- [ ] **11.5** Confirm the app launches and shows an empty WinUI 3 window (the default template MainWindow.xaml — no UPnP behaviour yet) (AC-12.4).
- [ ] **11.6** Create a non-empty file at `%LOCALAPPDATA%\ohSpy\diagnostics\sentinel.txt` (the directory doesn't exist by default — create it manually for this verification).
- [ ] **11.7** Uninstall via Apps & Features. Confirm `%LOCALAPPDATA%\Programs\ohSpy\` is gone AND `%LOCALAPPDATA%\ohSpy\diagnostics\sentinel.txt` STILL EXISTS (AC-12.5).
- [ ] **11.8** Bootstrap-failure smoke test: rename the `Microsoft.WindowsAppRuntime.Bootstrap.dll` (or temporarily move a critical WAS binary) in the install dir, then launch the app — confirm the native `MessageBoxW` appears with the bootstrap-failed hex code and the app exits cleanly (no .NET crash dialog). Restore the binary afterwards.

### Task 12 — README + init documentation (AC: #6, AC #7)

- [x] **12.1** Write `README.md` at the repo root containing:
  - One-paragraph project description (lift from the brief / architecture preamble).
  - "Prerequisites" section: .NET 10 SDK, Visual Studio 2026, InnoSetup 6, Windows 11.
  - "Build" section: the `dotnet build`, `dotnet test`, `dotnet publish`, and `dotnet build -t:BuildInstaller` one-liners.
  - **"First-time clone setup"** section explicitly calling out:
    ```powershell
    git config core.hooksPath .githooks
    ```
  - "Architecture" section pointing at `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` as the contract.
- [x] **12.2** Confirm the README's first-time-clone block is the documented init flow referenced by AC-13.2.

### Task 13 — Final verification + sanity sweep (AC: #1–#7)

- [x] **13.1** Re-run `dotnet build` from a fresh terminal. Confirm zero warnings, zero errors.
- [x] **13.2** Confirm `git status` shows no unstaged changes you didn't intend to commit. The committed tree should contain: `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, `.gitignore`, `ohSpy.sln`, `README.md`, `src/ohSpy.App/**`, `src/ohSpy.Core/Directory.Build.props`, `src/ohSpy.Core/**`, `tests/ohSpy.Core.Tests/**`, `installer/ohSpy.iss`, `.githooks/pre-commit`. NOT committed: `bin/`, `obj/`, `installer/out/`, `_bmad-output/*.tmp`.
- [x] **13.3** Make a trivial commit (e.g. add a sentence to README) AFTER running `git config core.hooksPath .githooks` locally. Confirm the pre-commit hook executes (you'll see "Running chaos tests..." in the output) and the commit succeeds with the trivially-passing filter.
- [x] **13.4** Confirm `git ls-files .githooks/pre-commit` shows the file is tracked with executable bit (`100755`).

## Dev Notes

### Architectural pillars this story implements

This story does **not** implement any UPnP behaviour. It establishes the discipline foundations that every subsequent epic depends on:

| Architecture decision / amendment | What this story delivers | AC tag |
|---|---|---|
| **Decision 12** — InnoSetup + unpackaged WinUI 3 + no CI | Installer pipeline, `BuildInstaller` target, bootstrap initializer, publish profiles | AC-12.1..AC-12.6 |
| **Decision 13** — Pre-commit chaos hook | `.githooks/pre-commit` scaffold + documented `core.hooksPath` step | AC-13.1, AC-13.2 |
| **Amendment A3** — Central Package Management | `Directory.Packages.props` with all version pins | (no numbered AC; mechanism is requirement) |
| **Amendment A4** — `Directory.Build.props` | Root + Core-local props, analyzer wiring, code-quality settings | (no numbered AC; mechanism is requirement) |
| **Pattern 2** — Core ↔ App boundary | `ohSpy.Core` targets `net10.0` (no `-windows`); does NOT reference WindowsAppSDK. NetArchTest enforcement comes in Story 1.6 | (referenced) |
| **Pattern 6** — Async discipline | `Microsoft.VisualStudio.Threading.Analyzers` wired in `Directory.Build.props`; bans `.Result` / `.Wait()` at build time | (referenced) |

> AC-13.3 and AC-13.4 require working chaos tests + the `IUpnpHttpClient` facade, which land in **Story 1.6** (chaos test) and **Story 1.3** (HTTP facade) respectively. **This story's chaos-hook coverage is structural only** — it ensures the hook exists, runs, and trivially passes; the regression-net teeth come online in Story 1.6.

### Critical version pins (do NOT deviate)

[Source: architecture.md §Decision-12, §A3]

- **.NET 10 LTS** — released 2025-11-11, EOL 2028-11-14. Pin via `global.json`.
- **Windows App SDK 2.1.3 Stable** — pinned exactly in `Directory.Packages.props`. NOT 2.0.x, NOT 2.2.x preview. The bootstrap initializer's `minVersion: new PackageVersion(2, 1, 3, 0)` and `majorMinorVersion: 0x00020001` parameters MUST match this pin.
- **CommunityToolkit.Mvvm 8.4.x** — source-generated MVVM. Carried from prior art.
- **Microsoft.Extensions.{DependencyInjection,Logging,Options} 10.0.x** — aligned with .NET 10.
- **Microsoft.VisualStudio.Threading.Analyzers 17.x** — Pattern 6 enforcement (VSTHRD002 / 003 / 100).

### Why no CI in v1

[Source: architecture.md §Decision-12, lines 1487–1499]

Solo greenfield. The first user of every build is the author. Per-commit CI buys nothing beyond what local `dotnet test` already provides. The L&L narrative doesn't depend on a green-badge — the artefact trail (brief / PRD / architecture / stories) carries the methodology story. The pre-commit chaos hook (Decision 13) replaces CI's regression-net role at single-digit-line cost. **Do not add a `.github/workflows/` directory or any CI configuration.** If a future contributor needs CI, it's a 50-line drop-in.

### Why InnoSetup over MSIX

[Source: architecture.md §Decision-12, lines 1511–1529; project memory `[[project-ohspy]]`]

The PRD originally specified MSIX. Architecture's Decision 12 reversed this because:

1. **MSIX sandbox virtualises filesystem** — `%LOCALAPPDATA%\ohSpy\diagnostics\` (FR-040) would be hidden behind the MSIX virtualization layer, obscuring the diagnostic log path that operators need to inspect.
2. **Unsigned MSIX requires user-side "developer mode" or "sideload apps" toggle** — bad audience UX for the Linn-developer demographic.
3. **InnoSetup's SmartScreen warning is known friction** — Linn engineers already know how to click past it.

Per-user install path `%LOCALAPPDATA%\Programs\ohSpy\`; no Administrator required.

### Bootstrap pattern non-obvious details

[Source: architecture.md §Decision-12, lines 1564–1594]

The `Bootstrap.TryInitialize` call binds the unpackaged app to its self-contained-published Windows App Runtime. **Critical invariants:**

1. Bootstrap MUST run before ANY `Microsoft.UI.*` type is touched. The `dotnet new winui` template sometimes generates an `App.xaml.cs` with its own `[STAThread] Main()` — delete that Main and rely solely on `Program.cs`.
2. The `<StartupObject>` MSBuild property in the csproj should resolve to `ohSpy.App.Program` (the entry point in `Program.cs`). If the WinUI template auto-generates an entry point, override or remove it.
3. The bootstrap-failure path uses native `MessageBoxW` (P/Invoke) because no WinUI types are available pre-bootstrap. This is the ONLY P/Invoke that lives in `App` for this story.
4. `Bootstrap.Shutdown()` runs in the `finally` block; it's idempotent if `Application.Start` already shut down WAS internally.

### Cross-story dependencies (forward-looking)

| Story | Why it needs Story 1.1 done first |
|---|---|
| 1.2 | `IUiDispatcher` interface lives in `ohSpy.Core/Threading/`; impl in `ohSpy.App/Windowing/`. Folder structure must exist. |
| 1.3 | `IUpnpHttpClient` + `UpnpExceptions` + `HttpTimeoutOptions` live in `ohSpy.Core/Http/`. Folder structure must exist. |
| 1.5 | `DiagnosticFileSink` lives in `ohSpy.App/Diagnostics/` (needs `%LOCALAPPDATA%` access). The diagnostics directory survival across uninstall (AC-5) is a Story 1.1 commitment that protects Story 1.5's log files. |
| 1.6 | The chaos-hook scaffold authored here will gain its first real test in 1.6. AC-13.3 / AC-13.4 fully activate only after 1.3 + 1.6 ship. |
| All Epic 2+ | The `Directory.Packages.props` pins + `Directory.Build.props` analyzer wiring govern every csproj going forward. Changes to either ripple across the whole repo. |

### Things this story explicitly does NOT do

- Implement `IUiDispatcher`, `BoundedObservableCollection`, `IdentityKeyedSortedCollection`, `IUpnpHttpClient`, any XML parser, any diagnostic emitter — those are Stories 1.2–1.5.
- Implement any test fixtures (`FakeUpnpDevice`, `TestHttpMessageHandler`, `InlineUiDispatcher`) — those are Stories 1.3 / 1.6.
- Implement NetArchTest rules pinning the Core ↔ App boundary — that's Story 1.6.
- Implement any SSDP / device-tree / SCPD code — those are Epic 2.
- Sign the installer. v1 ships unsigned per Decision 12.
- Provide ARM64 binaries by default. The `win-arm64.pubxml` exists for manual publishes only.

### Project Structure Notes

The full target directory layout is in architecture.md §Step 6 (lines 1989–2126). Story 1.1 creates only the **shells** — empty directories + the project files that hold them. Subsequent stories will populate the contents.

**Minimum directories this story must create** (some via `dotnet new`, some manually):

```
ohSpy/
├── .githooks/                          ← Task 8
│   └── pre-commit
├── installer/                          ← Task 7
│   └── ohSpy.iss
├── src/
│   ├── ohSpy.App/                      ← Task 1 (dotnet new winui)
│   │   ├── Properties/PublishProfiles/ ← Task 5
│   │   │   ├── win-x64.pubxml
│   │   │   └── win-arm64.pubxml
│   │   ├── Program.cs                  ← Task 4
│   │   ├── App.xaml + App.xaml.cs      ← Task 1 (from winui template)
│   │   ├── MainWindow.xaml + .cs       ← Task 1 (from winui template)
│   │   ├── app.manifest                ← Task 3.3
│   │   └── ohSpy.App.csproj            ← Tasks 1, 3, 6
│   └── ohSpy.Core/                     ← Task 1 (dotnet new classlib)
│       ├── Directory.Build.props       ← Task 2.6
│       └── ohSpy.Core.csproj
├── tests/
│   └── ohSpy.Core.Tests/               ← Task 1 (dotnet new xunit)
│       └── ohSpy.Core.Tests.csproj
├── .editorconfig                       ← Task 2.2
├── .gitignore                          ← Task 2.3
├── Directory.Build.props               ← Task 2.5
├── Directory.Packages.props            ← Task 2.4
├── global.json                         ← Task 2.1
├── ohSpy.sln                           ← Task 1 (dotnet new sln)
└── README.md                           ← Task 12
```

**Directory naming variances vs. architecture §Step 6:** none. Match the architecture's tree exactly.

**Detected conflicts:** none. The `dotnet new winui` template emits a `Package.appxmanifest` and may set `<WindowsPackageType>` differently — Tasks 3.1 + 3.2 explicitly reconcile.

### ARM64 caveat

The InnoSetup script sets `ArchitecturesAllowed=x64compatible` (Task 7.1), which permits install on ARM64 Windows running x64 via emulation. For v1 this is the intended behaviour — x64-only binaries, ARM64 hosts welcome via emulation. **Untested edge case:** the WindowsAppSDK bootstrap on ARM64-via-emulation may fail in subtle ways the x64-native path doesn't exhibit. If an ARM64 Windows box is available during Task 11 verification, smoke-test the install + bootstrap; otherwise record "ARM64-via-emulation install permitted-but-untested" in Completion Notes.

### Anti-patterns to avoid

- **Don't `dotnet new winuiapp` (singular) — use `dotnet new winui`.** The template names differ.
- **Don't put package versions in csproj `<PackageReference>` elements.** Central Package Management (A3) requires versions in `Directory.Packages.props` only. Mixing the two emits NU1008 warnings, which become errors under `TreatWarningsAsErrors=true`.
- **Don't add `Microsoft.WindowsAppSDK` to `ohSpy.Core.csproj`.** Pattern 2 boundary. If you find yourself wanting to, you're conflating concerns — the WAS-dependent code belongs in `App`.
- **Don't replace the architecture's pinned versions with "latest stable" without checking.** The pins are deliberate (e.g. WAS 2.1.3 exactly). Patch-level bumps within the pinned major.minor are fine.
- **Don't add a `.github/workflows/` directory.** Decision 12 explicitly rules CI out for v1.
- **Don't auto-set `core.hooksPath` from a postinstall script.** Git forbids committed config redirecting hooks; document the manual step in README and accept it as a one-time cloner action.
- **Don't sign the installer.** v1 is intentionally unsigned per Decision 12.
- **Don't add `app.config`, `appsettings.json`, or other configuration files** beyond what `dotnet new` produces. Pattern 7 (DI composition root) handles configuration in code, not on disk.

### Testing standards summary

[Source: architecture.md §Step 6 Pattern 6, §A4]

This story produces a test project but adds **zero tests**. AC-2's "0 failures (zero or more tests, all green)" is intentional. Tests come online in subsequent stories:

- **Story 1.2** — `BoundedObservableCollection` + `IdentityKeyedSortedCollection` unit tests (AC-6.1 .. AC-6.6).
- **Story 1.3** — `UpnpHttpClient` unit tests via `TestHttpMessageHandler` (AC-3.1 .. AC-3.6, AC-11.1 .. AC-11.4).
- **Story 1.4** — SCPD streaming + XXE defence tests (AC-5.1 .. AC-5.5).
- **Story 1.5** — diagnostic emitter / ring sink / file sink tests (AC-8.x).
- **Story 1.6** — `FakeUpnpDevice` fixture + first chaos test + NetArchTest rules.

Once Story 1.6 lands, tests carrying `[Trait("ac", "AC-N.M")]` (Amendment A2) become the dominant pattern. Story 1.1 itself has no AC-traceable test code; its acceptance is satisfied by file-existence and command-behaviour evidence.

### References

> Authoritative paths (for grep / cross-reference):
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (~2700 lines)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (lines 412–462 for Story 1.1, 408–410 + 350–354 for Epic 1)
> - PRD: `_bmad-output/planning-artifacts/prds/prd-ohSpy-2026-05-30/prd.md`

- [Source: epics.md#Story-1.1] — verbatim ACs (lines 412–462).
- [Source: epics.md#Epic-1] — epic-level FR/NFR coverage map (lines 408–410, 350–354).
- [Source: architecture.md#Initialization-Command] — verbatim `dotnet new` sequence (lines 101–111).
- [Source: architecture.md#Decision-12] — InnoSetup + unpackaged WinUI 3 + no-CI decision, `BuildInstaller` target, bootstrap initializer (lines 1485–1642).
- [Source: architecture.md#Decision-13] — pre-commit chaos hook (lines 2542–2582).
- [Source: architecture.md#Amendment-A3] — Central Package Management (lines 2403–2430).
- [Source: architecture.md#Amendment-A4] — `Directory.Build.props` + analyzer (lines 2432–2468).
- [Source: architecture.md#Pattern-2] — Core ↔ App boundary (lines 1662–1677).
- [Source: architecture.md#Pattern-6] — async discipline (lines 1754–1763).
- [Source: architecture.md#Project-Structure] — full directory tree (lines 1989–2126).
- [Source: project_ohspy memory] — quality bars (reliability / performance / UI polish, all non-negotiable), L&L deliverable status, prior-art `UpnpSpy` reference at `C:\work\UpnpSpy`.

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context) — model id `claude-opus-4-7[1m]`. Executed via bmad-dev-story workflow on 2026-06-01.

### Debug Log References

- Baseline commit: `66ffccc91396942a947f74ed7a4325181f7933e0`.
- Story 1.1 commits:
  - `5173108` — Story 1.1: project scaffold + build/test/installer pipeline
  - `615ef1d` — test: verify pre-commit chaos hook fires
  - `8887259` — fix: mark .githooks/pre-commit executable (100755) in git index
- Final `git ls-files -s .githooks/pre-commit` shows `100755 a2a88d5ac1468ae0039d7b2a4cec24b331737fc0 0 .githooks/pre-commit` — executable bit correct in index.
- Final `dotnet build` output: `Build succeeded. 0 Warning(s) 0 Error(s) Time Elapsed 00:00:07.56`.
- Final `dotnet test` output: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 2 ms - ohSpy.Core.Tests.dll (net10.0)`.

### Completion Notes List

**Versions & GUIDs**

- **Resolved `Directory.Packages.props` versions** — kept the patch numbers from the story spec as-is. NuGet successfully restored against all of them:
  - `CommunityToolkit.Mvvm 8.4.0`, `Microsoft.Extensions.{DI,Logging,Options} 10.0.0`,
    `Microsoft.VisualStudio.Threading.Analyzers 17.11.20`, `Microsoft.WindowsAppSDK 2.1.3`,
    `Microsoft.NET.Test.Sdk 17.12.0`, `xunit 2.9.2`, `xunit.runner.visualstudio 2.8.2`,
    `Moq 4.20.72`, `FluentAssertions 8.0.0`, `NetArchTest.Rules 1.3.2`.
- **InnoSetup `AppId` GUID generated 2026-06-01:** `5E1C113B-911A-445D-9B33-7DF605FFFDE8`.
  Set in `installer/ohSpy.iss` as `AppId={{5E1C113B-911A-445D-9B33-7DF605FFFDE8}` (InnoSetup brace-escaped form). **This GUID must remain stable forever; changing it across builds causes side-by-side installs instead of upgrades.**

**Build / test / publish evidence**

- **`dotnet build` zero-warning evidence** — final build output above. Three projects (`ohSpy.Core`, `ohSpy.Core.Tests`, `ohSpy.App`) all build cleanly. `TreatWarningsAsErrors=true` is active (verified by the analyzer-coverage smoke test below).
- **`dotnet test` discovery evidence** — `A total of 1 test files matched the specified pattern. Passed! - Failed: 0, Passed: 1`. The 1 test is the dotnet-new-xunit template's `UnitTest1.Test1` placeholder (a trivial empty test). Discovery works; the spec allows "0 or more, all green".
- **`dotnet publish src/ohSpy.App -c Release -r win-x64 --self-contained`** succeeded. Output at `src/ohSpy.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/` is 213 MB and contains `ohSpy.App.exe`, `Microsoft.WindowsAppRuntime.Bootstrap.dll`, the .NET 10 runtime, and the Windows App Runtime binaries.
- **`dotnet build -t:BuildInstaller` artefact** — **NOT produced**, because InnoSetup 6 is not installed on this machine. The MSBuild target's `<Error Condition="!Exists('$(InnoSetupCompiler)')">` fired with the expected message: `error : Inno Setup compiler not found at 'C:\Program Files\Inno Setup 6\ISCC.exe'. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or override InnoSetupCompiler.` — this is acceptance evidence per Task 10.5 ("If InnoSetup 6 is not installed, the target must fail with the explicit `<Error>` message"). To produce the real installer, install InnoSetup 6 (`winget install JRSoftware.InnoSetup` — requires UAC) and re-run the documented one-liner.

**Hook + analyzer evidence**

- **Hook-runs-on-commit evidence (Task 13.3)** — verified twice. The second Story 1.1 commit (`615ef1d test: verify pre-commit chaos hook fires`) shows the hook output:
  ```
  Running chaos tests...
  Test run for C:\work\ohSpy\tests\ohSpy.Core.Tests\bin\Debug\net10.0\ohSpy.Core.Tests.dll (.NETCoreApp,Version=v10.0)
  A total of 1 test files matched the specified pattern.
  [main 615ef1d] test: verify pre-commit chaos hook fires
  ```
  The chaos filter `--filter "Trait=category&Value=chaos"` matched zero tests (correct — chaos tests land in Story 1.6) and exited 0, so the commit succeeded.
- **Analyzer-coverage smoke test (Task 10.6)** — VSTHRD002 fired in all three projects when a throwaway `Task.Delay(1).Wait()` was introduced:
  - `ohSpy.Core/__AnalyzerSmokeTest.cs` → `error VSTHRD002: Synchronously waiting on tasks or awaiters may cause deadlocks.`
  - `ohSpy.App/__AnalyzerSmokeTest.cs` → same error.
  - `tests/ohSpy.Core.Tests/__AnalyzerSmokeTest.cs` → same error (VSTHRD100 is exempted under `tests/**` per the editorconfig override, but VSTHRD002 remains active — confirmed).
  - All three smoke-test files were deleted; final `dotnet build` returns zero warnings/errors.

**Deferred items**

- **Task 11 (clean-machine install validation) deferred to user** — requires a clean Windows 11 VM with no .NET 10 / WindowsAppRuntime pre-installed. The build host is Simon's main dev machine which already has both, so the install evidence can't be gathered here. The installer artefact will be produced once InnoSetup 6 is installed and the BuildInstaller target runs; the documented one-liner is in README.md. Task 11.1–11.8 flow is captured verbatim in the story file for the user to execute when ready.
- **ARM64-via-emulation install** — permitted by the InnoSetup script (`ArchitecturesAllowed=x64compatible`) but untested. The `win-arm64.pubxml` exists for manual ARM64-native publishes; not exercised in Story 1.1.

**Architecture amendments uncovered during implementation** *(all three applied to `architecture.md` on 2026-06-01 as Amendments A6, A7, A8 — see Change Log entry below)*

- **A3 version-skew correction** ✅ **APPLIED as [Amendment A6](../planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md#amendment-a6--a3-package-pin-corrections-story-11-implementation-reality).** A3's `xunit.runner.visualstudio` pin was wrong (`3.0.x` targets xUnit v3, but `xunit` is pinned to `2.9.x` = v2). Corrected in the architecture to `2.8.x`. Also added the missing `Microsoft.NET.Test.Sdk` pin (required under `CentralPackageTransitivePinningEnabled=true`).
- **Bootstrap.TryInitialize API surface mismatch** ✅ **APPLIED as [Amendment A7](../planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md#amendment-a7--bootstraptryinitialize-real-api-signature-decision-12-refinement).** The 4-arg int-returning form shown in D12 does not exist in WindowsAppSDK 2.x. Replaced D12's snippet with the canonical 5-arg bool-returning form (matches the actually-shipped `Program.cs`).
- **PlatformTarget=AnyCPU + csproj completeness** ✅ **APPLIED as [Amendment A8](../planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md#amendment-a8--csproj-snippet-completeness-decision-12-refinement).** D12's csproj snippet missed `<PlatformTarget>AnyCPU</PlatformTarget>` (NETSDK1032 unblock), `<UseWinUI>true</UseWinUI>`, and the `<StartupObject>` + `DISABLE_XAML_GENERATED_MAIN` pair (CS0017 prevention). All four now in D12's snippet.
- **CA1806 false positive in WinUI 3 startup** — *no architecture amendment needed.* The canonical Microsoft-documented WinUI 3 startup pattern is `Application.Start(_ => new App())`, which CA1806 flags as "creates a new instance never used". The `new App()` is consumed by WinUI internals not visible to Roslyn. Suppressed locally in Program.cs with `#pragma warning disable CA1806` around the single line, with a comment explaining why. Known Roslyn limitation.

**Environment prerequisites resolved during dev**

- **.NET 10 SDK was NOT installed on the dev host at story start.** Only .NET 7.0.400 and 8.0.421 were present. The dev installed .NET 10.0.300 SDK via `Microsoft.DotNet.SDK.10` (downloaded `dotnet-sdk-10-win-x64.exe` from `https://aka.ms/dotnet/10.0/dotnet-sdk-win-x64.exe`, ran with `/install /quiet /norestart`, user approved UAC). This unblocked all of Tasks 10.1–10.6. **Recommend updating the project README's Prerequisites section to clarify the install command** — already done (`winget install Microsoft.DotNet.SDK.10`).
- **InnoSetup 6 was NOT installed**, and remains uninstalled — UAC for `winget install JRSoftware.InnoSetup` was cancelled. The `<Error>` path is the acceptance evidence per Task 10.5.

**Files I touched that the spec didn't list explicitly**

- `src/ohSpy.App/.gitignore` (template-generated; edited to un-ignore `*.pubxml` so the win-x64/arm64 publish profiles can be committed).
- `src/ohSpy.App/MainPage.xaml` + `MainPage.xaml.cs` (template-generated; namespace renamed `ohSpy_App` → `ohSpy.App` along with the other XAML files).
- `tests/ohSpy.Core.Tests/UnitTest1.cs` (template-generated; kept as the placeholder test so `dotnet test` has something to discover).

### File List

**Created / authored from spec:**

- `global.json` — .NET 10 SDK pin
- `Directory.Build.props` — solution-wide build properties + VS Threading Analyzers wiring
- `Directory.Packages.props` — Central Package Management pins (A3)
- `.editorconfig` — `dotnet new editorconfig` defaults + test-tree VSTHRD100 exemption
- `.gitignore` — root .NET ignores
- `README.md` — project description, prerequisites, build commands, first-time clone setup
- `.githooks/pre-commit` — chaos-test pre-commit hook scaffold (mode 100755)
- `installer/ohSpy.iss` — InnoSetup 6 script (unsigned, per-user, unpackaged)
- `src/ohSpy.App/Program.cs` — bootstrap initializer (calls `Bootstrap.TryInitialize` before any WinUI type)
- `src/ohSpy.App/Properties/PublishProfiles/win-x64.pubxml` — x64 self-contained profile
- `src/ohSpy.App/Properties/PublishProfiles/win-arm64.pubxml` — ARM64 self-contained profile (manual)
- `src/ohSpy.Core/Directory.Build.props` — Core-local props (imports root, documents WAS boundary)

**Modified from `dotnet new` templates:**

- `ohSpy.sln` — solution file (default; references the three projects)
- `src/ohSpy.App/ohSpy.App.csproj` — converted to unpackaged WinUI 3 per Decision 12 (TFM net10.0-windows10.0.19041.0, `WindowsPackageType=None`, `SelfContained=true`, `WindowsAppSDKSelfContained=true`, `StartupObject=ohSpy.App.Program`, `DefineConstants=DISABLE_XAML_GENERATED_MAIN`, `PlatformTarget=AnyCPU`, `RuntimeIdentifiers=win-x64;win-arm64`, BuildInstaller target appended). MSIX-related properties removed.
- `src/ohSpy.App/App.xaml` + `App.xaml.cs` — namespace renamed `ohSpy_App` → `ohSpy.App`. No `Main()` (XAML compiler generates it; we suppress via `DISABLE_XAML_GENERATED_MAIN`).
- `src/ohSpy.App/MainWindow.xaml` + `MainWindow.xaml.cs` — namespace renamed.
- `src/ohSpy.App/MainPage.xaml` + `MainPage.xaml.cs` — namespace renamed.
- `src/ohSpy.App/app.manifest` — added `<longPathAware>true</longPathAware>` (PerMonitorV2 dpiAwareness was already present).
- `src/ohSpy.App/.gitignore` — un-ignored `*.pubxml` (commit publish profiles).
- `src/ohSpy.Core/ohSpy.Core.csproj` — TFM changed to `net10.0`, removed `ImplicitUsings`/`Nullable` (now inherited from root props).
- `tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj` — TFM changed to `net10.0`, replaced template package references with the central-managed set (`xunit`, `xunit.runner.visualstudio`, `Moq`, `FluentAssertions`, `NetArchTest.Rules`, `Microsoft.NET.Test.Sdk`), stripped all `Version` attributes.

**Removed from template:**

- `src/ohSpy.App/Package.appxmanifest` — MSIX manifest (unpackaged apps use `app.manifest` only).
- `src/ohSpy.App/Properties/PublishProfiles/win-x86.pubxml` — not needed.
- `src/ohSpy.Core/Class1.cs` — empty template placeholder.

**Untracked (intentionally not committed):**

- `src/ohSpy.App/Properties/launchSettings.json` — Visual Studio debug profile; reproducible per-developer.
- `.claude/`, `_bmad/`, `_bmad-output/` — BMad tooling and planning artefacts (already untracked at story start; left for Simon to commit selectively).

## Change Log

- **2026-06-01 — Implementation complete.** Story 1.1 scaffold landed across three commits (`5173108`, `615ef1d`, `8887259`). Dev sanity gates green: `dotnet build` 0/0, `dotnet test` 0 failures, analyzer smoke test confirms VSTHRD lint live in all three projects, pre-commit hook verified to fire on commit with mode 100755. Installer artefact deferred (InnoSetup 6 not installed; `<Error>` path verified per Task 10.5). Task 11 (VM verification) deferred per launch-brief instructions.
- **2026-06-01 — Code review APPROVED.** Sonnet code-review agent (fresh context, different LLM) confirmed all 7 ACs pass, no critical or major findings, 3 minor polish items, 3 architecture amendments confirmed. Story status `review` → `done`; sprint-status.yaml updated.
- **2026-06-01 — Architecture amendments applied.** The three recommended amendments uncovered during implementation have been merged into `architecture.md` as Amendments A6 (A3 package-pin corrections), A7 (Bootstrap.TryInitialize real API signature), and A8 (csproj snippet completeness). D12 + A3 snippets in the architecture now match the canonical Story 1.1 implementation; Stories 1.2–1.6 will inherit corrected guidance.
