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
; AppId — stable forever after. Generated 2026-06-01 by Story 1.1.
; Changing the AppId across builds causes side-by-side install instead of upgrade.
AppId={{5E1C113B-911A-445D-9B33-7DF605FFFDE8}
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
