---
baseline_commit: 2f0d4e52c2205050767c7a220dbcf3821c59ada0
---

# Story 2.8: Right-Click Context Menus — XML Viewing in Default Browser

Status: done

## Story

As a Linn engineer,
I want to right-click a device row to fetch its description XML in my default browser, and right-click a service row to fetch its SCPD XML (or open a Subscribe menu item — handler lands in Epic 4),
so that I can read the raw protocol payloads directly without leaving my Windows workflow.

## Acceptance Criteria

**Verbatim ACs from epics.md §Story 2.8 (lines 1181–1209). This story assigns the numbers AC-2.8.1 … AC-2.8.6 to the six `Given/When/Then` blocks below.**

**AC-2.8.1 — Device context menu surface (FR-017 + FR-052 wiring)**

**Given** `DeviceNodeViewModel`
**When** I right-click the device row
**Then** a context menu opens with a "Fetch description XML" item AND a "Properties…" item (FR-017 + FR-052 wiring — Properties window itself is delivered in Story 2.9)
**And** the menu uses XAML `MenuFlyout` bound via `x:Bind` to `[RelayCommand]` methods on the VM

**AC-2.8.2 — Device "Fetch description XML" opens LocationUrl (FR-019 + SC-005)**

**Given** the "Fetch description XML" item is chosen
**When** `DeviceNodeViewModel.FetchXmlCommand` runs
**Then** the device's `LocationUrl` is opened in the user's default web browser via `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })` (FR-019)
**And** the operation completes within ≤ 2 s typical (SC-005)
**And** if the launch fails (e.g. no default browser), a `Warning` diagnostic is emitted with `Url` context — no app crash

**AC-2.8.3 — URL whitelist (Architecture validation Gap-3)**

**Given** the URL safety check (Architecture validation Gap-3)
**When** `FetchXmlCommand` is invoked
**Then** only `http://` and `https://` schemes are accepted (whitelist)
**And** any other scheme causes a `Warning` diagnostic to be emitted and the launch is skipped (defensive; UPnP `LOCATION` URLs are HTTP per UDA 1.0)

**AC-2.8.4 — Service context menu surface (FR-018 + FR-020)**

**Given** `ServiceNodeViewModel`
**When** I right-click the service row
**Then** a context menu opens with two items: "Fetch service XML" AND "Subscribe" (FR-018)
**And** "Fetch service XML" opens the service's `SCPDURL` in the default browser via the same shell-execute path as the device case (FR-020)
**And** the URL whitelist applies the same way

**AC-2.8.5 — Subscribe stub (forthcoming, Epic 4)**

**Given** the "Subscribe" menu item
**When** it is chosen
**And** the "Subscribe" item is wired to a `SubscribeCommand` on `ServiceNodeViewModel` — but the command's implementation is a stub that emits a `Warning` `"subscribe not yet implemented"` diagnostic; full implementation lands in Epic 4 (Story 4.1)
**And** the stub clearly indicates to the operator that subscription is forthcoming (e.g. a transient flyout "Subscribe — coming in Epic 4") — OR the menu item is hidden behind a feature flag — engineering judgment, document the choice in the impl

**AC-2.8.6 — Shell-execute is fire-and-forget on the UI thread**

**Given** any context-menu-driven shell-execute call
**When** it runs
**Then** it executes on the UI thread and returns within the SC-005 budget; the brief shell-execute kick-off is non-blocking enough not to require `IUiDispatcher.PostAsync`-style readback (it's a fire-and-forget)

---

## Tasks / Subtasks

### Task 1 — Browser-launch seam: `IUriLauncher` + `ShellUriLauncher` (AC: #2, #3)

The architecture maps shell-open to `System.Diagnostics.Process.Start` (arch line 2187). A **direct** `Process.Start` inside the VM is untestable (it would spawn a real browser in every unit run) — and AC-2.8.2/2.8.3 explicitly require testable whitelist + warn-on-failure behaviour. Wrap the one `Process.Start` call behind a one-method seam, exactly as the project already does for every other unmockable platform call (`INetworkInterfaceSource`, `IUiDispatcher`, `IDiagnosticEmitter`). The seam is pure BCL (`System.Diagnostics`), so it lives in **Core** and passes `CoreAppBoundaryTests`.

- [x] **1.1** Create `src/ohSpy.Core/Shell/IUriLauncher.cs`:
  ```csharp
  namespace ohSpy.Core.Shell;

  /// <summary>
  /// One-method seam over the OS "open this URI in its default handler" shell call
  /// (FR-019 / FR-020). The single production impl (<see cref="ShellUriLauncher"/>) calls
  /// <c>Process.Start(UseShellExecute = true)</c>; tests inject a fake so the whitelist +
  /// warn-on-failure logic (Gap-3) is verifiable without spawning a browser.
  /// </summary>
  public interface IUriLauncher
  {
      /// <summary>Hand the URI to the OS shell. Throws on any launch failure (no default
      /// browser, blocked scheme handler, etc.) — the caller is responsible for catching
      /// and emitting the FR-019 Warning diagnostic.</summary>
      void Launch(Uri url);
  }
  ```
- [x] **1.2** Create `src/ohSpy.Core/Shell/ShellUriLauncher.cs`:
  ```csharp
  namespace ohSpy.Core.Shell;

  using System.Diagnostics;

  /// <summary>
  /// Production <see cref="IUriLauncher"/> — opens the URI in the OS default handler via the
  /// shell (arch line 2187). `UseShellExecute = true` is REQUIRED: it routes through the shell
  /// so `http(s)://` URLs open in the registered default browser (without it, .NET tries to
  /// exec the URL as a file path and throws). Pure BCL → lives in Core (Pattern 2 / boundary).
  /// Not unit-tested directly (it would launch a real browser); covered by the seam contract
  /// and the manual smoke (Task 9).
  /// </summary>
  public sealed class ShellUriLauncher : IUriLauncher
  {
      public void Launch(Uri url) =>
          Process.Start(new ProcessStartInfo
          {
              FileName = url.ToString(),
              UseShellExecute = true,
          });
  }
  ```
  - `Process.Start(ProcessStartInfo)` returns a `Process?`; discarding it is fine (fire-and-forget — AC-2.8.6). If the analyzer flags the unused return (`IDE0058`), assign to `_`.

### Task 2 — Shared launch helper: `BrowserLaunch.OpenInDefaultBrowser` (AC: #2, #3)

The device and service "Fetch XML" commands run **identical** logic on different URLs: whitelist-check → launch → warn-on-failure. Factor it into ONE internal static helper so the two VMs don't duplicate it (anti-pattern: copy-paste the whitelist into both VMs). Tested directly + once (Task 7).

- [x] **2.1** Add the diagnostic category. Edit `src/ohSpy.Core/Diagnostics/DiagCategories.cs` — add under a new "XML viewing (Story 2.8)" section (Pattern 11: constant + call sites in one PR):
  ```csharp
  // ─── XML viewing / shell-open (Story 2.8) ──────────────────────
  /// <summary>Mandatory context: Url; DeviceUuid when known. Emitted when a context-menu
  /// shell-open is refused (non-http(s) scheme) or fails (no default browser, etc.).</summary>
  public const string ShellExecute = "Shell.Execute";

  /// <summary>Mandatory context: (none beyond message). Temporary — emitted by the
  /// Story 2.8 Subscribe stub (removed in Story 4.1) and the Properties stub (replaced in
  /// Story 2.9). A placeholder for menu items whose real handler lands in a later epic.</summary>
  public const string FeatureNotImplemented = "Feature.NotImplemented";
  ```
- [x] **2.2** Create `src/ohSpy.Core/ViewModels/BrowserLaunch.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using ohSpy.Core.Diagnostics;
  using ohSpy.Core.Shell;

  /// <summary>
  /// Shared shell-open path for the Story 2.8 context-menu "Fetch XML" commands (FR-019 /
  /// FR-020). Enforces the Gap-3 scheme whitelist, delegates the actual launch to the
  /// injected <see cref="IUriLauncher"/>, and emits a single Warning diagnostic on either
  /// a refused scheme or a launch failure — never throws, never crashes the app (AC-2.8.2 /
  /// AC-2.8.3). UI-thread, synchronous, fire-and-forget (AC-2.8.6).
  /// </summary>
  internal static class BrowserLaunch
  {
      /// <summary>
      /// Open <paramref name="url"/> in the default browser if (and only if) its scheme is
      /// http/https. Returns true if the launch was attempted, false if it was refused or
      /// failed (both paths having emitted a Warning).
      /// </summary>
      public static bool OpenInDefaultBrowser(
          Uri url, IUriLauncher launcher, IDiagnosticEmitter diag, Guid deviceUuid)
      {
          // Gap-3 whitelist: UPnP LOCATION / SCPDURL are http(s) per UDA 1.0. Anything else
          // (file:, javascript:, custom schemes) is refused defensively — never shell-opened.
          if (!IsHttpOrHttps(url))
          {
              diag.Warning(DiagCategories.ShellExecute, "Refused to open non-http(s) URL",
                  new DiagnosticContext { DeviceUuid = deviceUuid, Url = url.ToString() });
              return false;
          }

          try
          {
              launcher.Launch(url);
              return true;
          }
#pragma warning disable CA1031 // FR-019: ANY launch failure (Win32Exception "no default
          catch (Exception ex) // browser", blocked handler, etc.) must warn-not-crash.
#pragma warning restore CA1031
          {
              diag.Warning(DiagCategories.ShellExecute, "Failed to open URL in default browser",
                  new DiagnosticContext
                  {
                      DeviceUuid = deviceUuid, Url = url.ToString(), ErrorText = ex.Message,
                  });
              return false;
          }
      }

      private static bool IsHttpOrHttps(Uri url) =>
          url.IsAbsoluteUri &&
          (url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
           url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
  }
  ```
  - `url.IsAbsoluteUri` guard: `Uri.Scheme` throws on a relative `Uri`. Device `LocationUrl` and the resolved SCPD `Uri` are always absolute, but the guard makes the helper total (a relative `Uri` → refused, not a thrown exception).
  - `Uri.UriSchemeHttp` / `Uri.UriSchemeHttps` are the BCL canonical scheme constants (`"http"` / `"https"`) — prefer them over string literals.

### Task 3 — Add `IUriLauncher` to the `NodeServices` bundle (AC: #2, #4)

The node VMs already receive every Core service they need via the `NodeServices` record (Story 2.6). Add the launcher there rather than threading a new constructor parameter through `DeviceTreeViewModel → DeviceNodeViewModel → ServiceNodeViewModel`.

- [x] **3.1** Edit `src/ohSpy.Core/ViewModels/NodeServices.cs` — add the 5th member:
  ```csharp
  using ohSpy.Core.Shell; // add to the existing usings

  public sealed record NodeServices(
      IUpnpHttpClient Http,
      IScpdParser ScpdParser,
      IUiDispatcher Ui,
      IDiagnosticEmitter Diag,
      IUriLauncher Launcher); // Story 2.8 — context-menu shell-open seam
  ```
  This is a **breaking constructor change** to `NodeServices`. Every construction site must add the 5th argument (Task 8 covers the 4 test sites; the DI site is Task 3.2).

### Task 4 — `DeviceNodeViewModel`: FetchXml + OpenProperties commands (AC: #1, #2, #3)

Edit `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs` (UPDATE — Story 2.5/2.6 own it). It already holds `_entry` (with `LocationUrl` + `Uuid`) and `_services` (now carrying `Launcher`). Add `using CommunityToolkit.Mvvm.Input;` for `[RelayCommand]`.

- [x] **4.1** Add the **FetchXml** command (FR-019). Synchronous `void` — `Process.Start(UseShellExecute=true)` is fire-and-forget (AC-2.8.6); no async readback (see Dev Notes §"Commands are synchronous"):
  ```csharp
  // AC-2.8.2/2.8.3: open the device description (LocationUrl) in the default browser.
  // Whitelist + warn-on-failure live in the shared BrowserLaunch helper.
  [RelayCommand]
  private void FetchXml() =>
      BrowserLaunch.OpenInDefaultBrowser(
          _entry.LocationUrl, _services.Launcher, _services.Diag, _entry.Uuid);
  ```
- [x] **4.2** Add the **OpenProperties** STUB command (AC-2.8.1 — menu item must exist now; the real window lands in Story 2.9). `DeviceNodeViewModel` is **Core** and cannot open an App `Window`; the stub only emits a diagnostic. Story 2.9 replaces this body (epics lines 1258–1260) — and may relocate the command to `ShellViewModel`/App if it needs window/factory access (document the seam tension, Dev Notes §"Properties stub"):
  ```csharp
  // STUB — AC-2.8.1 surfaces the "Properties…" item; the read-only Properties window is
  // delivered in Story 2.9, which replaces this body (and may relocate the command to the
  // App layer, since opening a Window is not a Core concern). Until then: warn, do not crash.
  [RelayCommand]
  private void OpenProperties() =>
      _services.Diag.Warning(DiagCategories.FeatureNotImplemented,
          "Properties window not yet implemented (Story 2.9)",
          new DiagnosticContext { DeviceUuid = _entry.Uuid });
  ```
  - Generated command properties: `FetchXmlCommand` and `OpenPropertiesCommand` (both `IRelayCommand`). The XAML (Task 6) binds these.

### Task 5 — `ServiceNodeViewModel`: FetchServiceXml + Subscribe commands (AC: #4, #5)

Edit `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs` (UPDATE — Story 2.6 owns it). It already holds `_service` (with `ScpdUrl`), `_deviceLocation`, `_deviceUuid`, and `_services`. Add `using CommunityToolkit.Mvvm.Input;`.

- [x] **5.1** Add the **FetchServiceXml** command (FR-020). Resolve the SCPD URL exactly as `LoadActionsAsync` does (`new Uri(_deviceLocation, _service.ScpdUrl)` — handles relative OR absolute SCPDURL), then route through the same helper:
  ```csharp
  // AC-2.8.4: open the SCPD (resolved against the device LocationUrl, like LoadActionsAsync)
  // in the default browser via the same shell-execute path + whitelist as the device case.
  [RelayCommand]
  private void FetchServiceXml() =>
      BrowserLaunch.OpenInDefaultBrowser(
          new Uri(_deviceLocation, _service.ScpdUrl),
          _services.Launcher, _services.Diag, _deviceUuid);
  ```
  - **Naming:** arch line 2187 uses the shorthand `ServiceNodeViewModel.FetchXmlCommand`. We disambiguate as **`FetchServiceXmlCommand`** to match the "Fetch service XML" menu label and read clearly next to the device's `FetchXmlCommand` (the two live on distinct types, so either is unambiguous — clarity preferred). Document this minor deviation in the Dev Agent Record.
- [x] **5.2** Add the **Subscribe** STUB command (AC-2.8.5). Emits the AC-mandated Warning. Operator "forthcoming" affordance is the **menu-item label** "Subscribe (coming in Epic 4)" (Task 6) — the simplest, testable choice over a transient flyout / feature flag (Dev Notes §"Subscribe stub"). Story 4.1 relabels to "Subscribe" + real handler (epics lines 1719–1722):
  ```csharp
  // STUB — AC-2.8.5. The real GENA subscribe handler lands in Epic 4 (Story 4.1). The menu
  // item is visible+enabled and labelled "Subscribe (coming in Epic 4)"; choosing it emits a
  // Warning so the action is observable in diagnostics. Story 4.1 removes this stub + relabel.
  [RelayCommand]
  private void Subscribe() =>
      _services.Diag.Warning(DiagCategories.FeatureNotImplemented,
          "subscribe not yet implemented",
          new DiagnosticContext { DeviceUuid = _deviceUuid, ServiceId = _service.ServiceId });
  ```
  - Generated command properties: `FetchServiceXmlCommand` and `SubscribeCommand`.

### Task 6 — XAML context menus (`MenuFlyout` via `ContextFlyout`) (AC: #1, #4, #5)

Edit `src/ohSpy.App/MainWindow.xaml` (UPDATE). Attach a `MenuFlyout` to each node template's root element via `ContextFlyout` — right-clicking the row content opens it (the standard WinUI 3 TreeView context-menu mechanism; the actual right-click UX is manual-verified in Task 9). `MenuFlyoutItem.Command` binds via `x:Bind` against the template's `x:DataType` VM (AC mandates `x:Bind` to `[RelayCommand]` methods — Pattern 13; arch anti-pattern forbids `Click=` code-behind handlers).

- [x] **6.1** Device template (lines ~53–87) — add a `ContextFlyout` to the root `Grid` (after `</Grid.RowDefinitions>` / alongside the existing children, before `</Grid>`):
  ```xml
  <Grid.ContextFlyout>
      <MenuFlyout>
          <MenuFlyoutItem Text="Fetch description XML"
                          Command="{x:Bind FetchXmlCommand}" />
          <MenuFlyoutItem Text="Properties…"
                          Command="{x:Bind OpenPropertiesCommand}" />
      </MenuFlyout>
  </Grid.ContextFlyout>
  ```
- [x] **6.2** Service template (lines ~91–101) — the root is a `StackPanel`; add a `ContextFlyout` to it:
  ```xml
  <StackPanel.ContextFlyout>
      <MenuFlyout>
          <MenuFlyoutItem Text="Fetch service XML"
                          Command="{x:Bind FetchServiceXmlCommand}" />
          <MenuFlyoutItem Text="Subscribe (coming in Epic 4)"
                          Command="{x:Bind SubscribeCommand}" />
      </MenuFlyout>
  </StackPanel.ContextFlyout>
  ```
  - `x:Bind` to a generated command property defaults to `OneTime` — correct (the command instance never changes). Do NOT add `Mode=OneWay` (avoids the `WMC1506` warning under `TreatWarningsAsErrors`).
  - The Action template and the Fallback template get **no** context menu (no AC; the log/leaf rows are not right-clickable here).
  - `…` in the "Properties…" text is the literal horizontal-ellipsis `U+2026` (matches the epics AC wording and the `LoadingPlaceholderViewModel` "Loading…" precedent). Keep the file UTF-8.

### Task 7 — Tests: `BrowserLaunch` helper + `FakeUriLauncher` (AC: #2, #3)

- [x] **7.1** Create `tests/ohSpy.Core.Tests/Fakes/FakeUriLauncher.cs`:
  ```csharp
  namespace ohSpy.Core.Tests.Fakes;

  using ohSpy.Core.Shell;

  /// <summary>Records every <see cref="Launch"/> call so tests can assert the URL that was
  /// shell-opened; set <see cref="ThrowOnLaunch"/> to simulate "no default browser" (FR-019).</summary>
  internal sealed class FakeUriLauncher : IUriLauncher
  {
      public List<Uri> Launched { get; } = new();
      public Exception? ThrowOnLaunch { get; set; }

      public void Launch(Uri url)
      {
          Launched.Add(url);
          if (ThrowOnLaunch is not null) throw ThrowOnLaunch;
      }
  }
  ```
- [x] **7.2** Create `tests/ohSpy.Core.Tests/ViewModels/BrowserLaunchTests.cs`. `[Trait("ac", "AC-2.8.<n>")]`. `BrowserLaunch` is `internal` — `ohSpy.Core` already exposes internals to the test assembly via `InternalsVisibleTo` (used by `SsdpParser` tests etc.; confirm it's present, add if missing). Cases:
  - `Http_LaunchesUrl_AC282` — `http://host/desc.xml` → `launcher.Launched` contains it; returns `true`; no Warning.
  - `Https_LaunchesUrl_AC282` — `https://host/desc.xml` → launched; `true`.
  - `NonHttpScheme_Refused_NoLaunch_Warns_AC283` — `[Theory]` over `file:///c:/x`, `ftp://h/x`, `javascript:alert(1)`, `mailto:a@b` → `launcher.Launched` empty; returns `false`; exactly one `Warning` with category `Shell.Execute` and `Url` context populated.
  - `LaunchThrows_Warns_NoCrash_AC282` — `FakeUriLauncher.ThrowOnLaunch = new InvalidOperationException("no browser")` → `OpenInDefaultBrowser` does NOT throw; returns `false`; one `Warning` (category `Shell.Execute`) with `ErrorText == "no browser"` and `Url` context.
  - `DeviceUuid_FlowsToDiagnosticContext_AC282` — refused-scheme path with a known `Guid` → the captured `Warning`'s `Context.DeviceUuid` equals it.

### Task 8 — Update `NodeServices` construction sites (AC: #2, #4)

The 5th `NodeServices` member breaks 4 existing construction sites. Add `new FakeUriLauncher()` (or a shared inert instance) to each:

- [x] **8.1** `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs:20` (static `NodeServices` field) and `:~220` (the `Expand_NoHttpFetchTriggered_AC262` local `new NodeServices(...)`) — add the launcher arg.
- [x] **8.2** `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs:26` — add the launcher arg.
- [x] **8.3** `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs:39` (`MakeNodeServices` factory) — add a `launcher` parameter defaulting to `new FakeUriLauncher()`, pass it to the `NodeServices` ctor; let the new command tests (Task 9) pass a capturing instance.
- [x] **8.4** DI: edit `src/ohSpy.App/Composition/ServiceRegistration.cs` — register the launcher singleton just before the `NodeServices` registration (line ~100). `services.AddSingleton<NodeServices>()` then auto-resolves the new `IUriLauncher` ctor param:
  ```csharp
  // Story 2.8 — OS shell-open seam for the context-menu "Fetch XML" commands.
  services.AddSingleton<IUriLauncher, ShellUriLauncher>();
  ```
  Add `using ohSpy.Core.Shell;` to the file's usings.

### Task 9 — Tests: VM commands (AC: #1, #2, #3, #4, #5)

- [x] **9.1** `DeviceNodeViewModelTests` additions (`[Trait("ac", ...)]`). Construct the VM with a `NodeServices` carrying a capturing `FakeUriLauncher` + `CapturingDiagnosticEmitter`:
  - `FetchXmlCommand_OpensLocationUrl_AC282` — loaded entry with `LocationUrl = http://192.168.1.100:49152/desc.xml`; `vm.FetchXmlCommand.Execute(null)` → `launcher.Launched` single entry == that URL; no Warning.
  - `FetchXmlCommand_NonHttpLocation_Refused_Warns_AC283` — entry with a `file://` location → `launcher.Launched` empty; one `Warning` (`Shell.Execute`).  *(Construct via the existing `PendingEntry`/`LoadedEntry` helpers with a `file://` `Uri`.)*
  - `FetchXmlCommand_LaunchFailure_Warns_NoCrash_AC282` — `FakeUriLauncher.ThrowOnLaunch` set → `Execute` does not throw; one `Warning` carrying the device `Uuid`.
  - `OpenPropertiesCommand_Stub_WarnsNotImplemented_ACA281` — `vm.OpenPropertiesCommand.Execute(null)` → no throw; one `Warning` category `Feature.NotImplemented`; `launcher.Launched` empty.
- [x] **9.2** `ServiceNodeViewModelTests` additions:
  - `FetchServiceXmlCommand_ResolvesRelativeScpdUrl_Launches_AC284` — `Service(scpdUrl: "/RC/Scpd.xml")`, device location `http://192.168.1.100:49152/desc.xml` → `launcher.Launched` single entry == `http://192.168.1.100:49152/RC/Scpd.xml`.
  - `FetchServiceXmlCommand_AbsoluteScpdUrl_PassesThrough_AC284` — `scpdUrl: "http://10.0.0.5/scpd.xml"` → launched == that absolute URL (`new Uri(base, absolute)` returns the absolute).
  - `FetchServiceXmlCommand_NonHttpResolved_Refused_Warns_AC284` — engineering choice: a device location of `file:///x/desc.xml` + relative scpd resolves to `file://` → refused + Warning. (Or skip if awkward; the whitelist itself is fully covered by `BrowserLaunchTests`.)
  - `SubscribeCommand_Stub_WarnsNotImplemented_AC285` — `vm.SubscribeCommand.Execute(null)` → no throw; one `Warning` category `Feature.NotImplemented`, message `"subscribe not yet implemented"`; `launcher.Launched` empty.
  - These command tests trigger NO SCPD fetch (they never set `IsExpanded`), so the HTTP/parser stubs stay inert — pass the capturing `FakeUriLauncher` via the updated `MakeNodeServices`.

### Task 10 — DI / boundary / final verification (AC: all)

- [x] **10.1** `CoreAppBoundaryTests` still green (4 facts). `IUriLauncher`, `ShellUriLauncher`, `BrowserLaunch`, the two new commands, and the `NodeServices` change are pure Core (`System.Diagnostics` + `CommunityToolkit.Mvvm` + BCL) — no `Microsoft.UI.*` / `Microsoft.Windows.*` / `WinRT.Interop.*`.
- [x] **10.2** Confirm `InternalsVisibleTo("ohSpy.Core.Tests")` is present on `ohSpy.Core` (needed for `BrowserLaunch` internal-helper tests). If absent, add it to the csproj / an `AssemblyInfo`-equivalent — but it is almost certainly already there (the `SsdpParser`/internal-type tests rely on it). Verify before adding a duplicate.
- [x] **10.3** `dotnet build src/ohSpy.App` — **0 errors / 0 warnings** (`TreatWarningsAsErrors`). Watch for: `WMC1506` on the new `MenuFlyoutItem` bindings (avoid by keeping them `OneTime` — no `Mode=OneWay`); `CA1031` on the broad catch (suppressed with the documented pragma in Task 2.2); `IDE0058` on the discarded `Process.Start` return (assign to `_` if flagged).
- [x] **10.4** `dotnet test tests/ohSpy.Core.Tests` — all green. Baseline **283** passing (Story 2.7) + the new tests (~13); 2 skips unchanged.
- [x] **10.5** `dotnet test --filter "category=chaos"` — still exactly **1** (chaos suite unchanged).
- [x] **10.6** `dotnet test --filter "FullyQualifiedName~CoreAppBoundary"` — **4** green.
- [x] **10.7** **Manual smoke (non-AC-gating; record in Dev Agent Record — covers AC-2.8.1/2.8.4 right-click UX + AC-2.8.2/2.8.6 ≤2 s open):** launch `ohSpy.App` on a network with live UPnP devices. Confirm: (a) right-click a device row → menu shows "Fetch description XML" + "Properties…"; choosing "Fetch description XML" opens the device `LocationUrl` in the default browser within ~2 s; choosing "Properties…" does nothing visible but logs the `Feature.NotImplemented` Warning. (b) Expand a device, right-click a service row → menu shows "Fetch service XML" + "Subscribe (coming in Epic 4)"; "Fetch service XML" opens the SCPD URL in the browser; "Subscribe (coming in Epic 4)" logs the `Feature.NotImplemented` Warning. (c) The UI never blocks/crashes on any of these (fire-and-forget). If a headless dev environment prevents a real UI run, record this Task as not-executed (as Stories 2.6/2.7 did) and recommend it before Epic 2 close.

---

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **FR-017 / FR-052 (wiring)** | Device context menu: "Fetch description XML" + "Properties…" (window in 2.9) | AC-2.8.1 |
| **FR-019 / SC-005** | Device `LocationUrl` → default browser via shell-execute, ≤ 2 s, warn-on-failure | AC-2.8.2 |
| **Gap-3 (FR-052 URL safety)** | `http`/`https` whitelist; other schemes warn + skip | AC-2.8.3 |
| **FR-018 / FR-020** | Service context menu: "Fetch service XML" (SCPDURL → browser) + "Subscribe" | AC-2.8.4 |
| **Subscribe stub (Epic 4 / Story 4.1)** | `SubscribeCommand` stub Warning + "coming in Epic 4" label | AC-2.8.5 |
| **Pattern 9 / 13** | `[RelayCommand]` on the VM; `MenuFlyout` `x:Bind` to commands; no code-behind handlers | AC-2.8.1, AC-2.8.4 |
| **Seam philosophy** | `IUriLauncher` wraps the one `Process.Start` so the whitelist/warn logic is testable | AC-2.8.2, AC-2.8.3 |

### CRITICAL DESIGN DECISIONS

**1. Wrap `Process.Start` behind `IUriLauncher` — do NOT call it inline in the VM.** *(§"Launcher seam")*
The architecture (line 2187) maps shell-open to `System.Diagnostics.Process.Start`. Taken literally that would put `Process.Start(...)` directly in `FetchXmlCommand`. But AC-2.8.2 and AC-2.8.3 require **testable** behaviour — "only http/https accepted; other scheme → Warning + skip; launch failure → Warning, no crash". A direct `Process.Start` spawns a real browser in every unit run and cannot assert "launch was skipped" or "launch failed → warned". So we wrap the single `Process.Start(UseShellExecute=true)` call behind a one-method `IUriLauncher` seam — exactly the pattern the project already uses for every other unmockable platform call (`INetworkInterfaceSource` for `NetworkInterface`, `IUiDispatcher` for the dispatcher, `IDiagnosticEmitter`). The seam IS the `Process.Start` from line 2187; it just lives behind an interface so the whitelist + diagnostics are verifiable. `ShellUriLauncher` is pure BCL (`System.Diagnostics`), so it stays in **Core** and passes `CoreAppBoundaryTests`.

**2. `UseShellExecute = true` is mandatory — and is what makes the default browser open.** *(§"UseShellExecute")*
`Process.Start("http://…")` with the **default** `UseShellExecute = false` (the .NET Core default) tries to execute the URL as a file path and throws (`Win32Exception`). Setting `UseShellExecute = true` routes through the OS shell, which resolves `http(s)://` to the registered default browser. This is the documented .NET way to "open a URL in the default browser". Do NOT omit it; do NOT try `Process.Start(url)` (string overload).

**3. One shared `BrowserLaunch` helper — don't duplicate the whitelist in both VMs.** *(§"Shared helper")*
`DeviceNodeViewModel.FetchXml` and `ServiceNodeViewModel.FetchServiceXml` differ ONLY in the URL they pass (device `LocationUrl` vs resolved SCPD `Uri`). The whitelist + launch + warn logic is identical. Factor it into `internal static BrowserLaunch.OpenInDefaultBrowser(url, launcher, diag, deviceUuid)` and call it from both. Copy-pasting the whitelist into both VMs is the anti-pattern this prevents (and would double the Gap-3 test surface).

**4. Commands are synchronous `void`, not `async Task`.** *(§"Commands are synchronous")*
AC-2.8.6 is explicit: the shell-execute kick-off is "fire-and-forget… non-blocking enough not to require `IUiDispatcher.PostAsync`-style readback". `Process.Start(UseShellExecute=true)` hands off to the shell and returns immediately (it does NOT wait for the browser). So the `[RelayCommand]` methods are plain `void` → the generator produces `IRelayCommand` (synchronous), not `IAsyncRelayCommand`. The architecture's generic example (`async Task FetchXmlAsync()`, line 1789) is illustrative MVVM shape, NOT a directive to make this path async — AC-2.8.6 governs. Synchronous avoids needless `Task`/async-void machinery and matches the fire-and-forget contract. Document this in the Dev Agent Record (it deviates from the arch snippet's `async` shape).

**5. URL resolution for the service is `new Uri(_deviceLocation, _service.ScpdUrl)` — reuse the Story 2.6 idiom.** *(§"SCPD URL resolution")*
`ServiceNodeViewModel.LoadActionsAsync` (line 79) already resolves the SCPDURL with `new Uri(_deviceLocation, _service.ScpdUrl)`, which correctly handles a **relative** SCPDURL (the common case — UPnP SCPDURLs are usually relative to the device `LOCATION`) AND an absolute one (passthrough). `FetchServiceXml` MUST use the same resolution so the browser opens the same URL the tree fetched. Do NOT pass `_service.ScpdUrl` (a `string`) raw — it's often relative and would not be a valid absolute browser target.

**6. Two diagnostic categories, with intent.** *(§"Diagnostic categories")*
- `Shell.Execute` (`DiagCategories.ShellExecute`) — the **permanent** error path: refused scheme (Gap-3) and launch failure (FR-019). Mandatory context: `Url`; `DeviceUuid` when known.
- `Feature.NotImplemented` (`DiagCategories.FeatureNotImplemented`) — **temporary** stub marker for the Subscribe (removed in Story 4.1) and Properties (replaced in Story 2.9) menu items. A later story removes both call sites; the constant can be removed when its last call site goes (or kept if other stubs adopt it).
Pattern 11: add the constants + their call sites in this one story; no inline category string literals at call sites.

**7. The "Properties…" command is a Core-side STUB; Story 2.9 owns the real behaviour.** *(§"Properties stub")*
AC-2.8.1 requires the "Properties…" item to *exist* now; the read-only Properties **window** is delivered in Story 2.9 (epics lines 1213–1265). `DeviceNodeViewModel` lives in **Core** and cannot construct an App `Window` — so the 2.8 stub only emits a `Feature.NotImplemented` Warning (no crash). Story 2.9's AC (lines 1258–1260) explicitly picks up "the right-click handler from Story 2.8's 'Properties…' menu item" and notes the command may live on `DeviceNodeViewModel` *or* `ShellViewModel` ("engineering judgment, document the seam"). Flag for the 2.9 author: opening a window from a Core VM is not possible directly — 2.9 will either relocate the command to the App layer / `ShellViewModel`, or inject a `Func<RegistryEntry, PropertiesWindow>`-style factory (Pattern 7). For 2.8, binding the menu item to `OpenPropertiesCommand` on `DeviceNodeViewModel` (the template's `x:DataType`) is the simplest wiring and avoids `ElementName` gymnastics.

**8. The Subscribe affordance is a static menu-item label, not a transient flyout.** *(§"Subscribe stub")*
AC-2.8.5 offers engineering judgment: a transient "Subscribe — coming in Epic 4" flyout, OR a feature-flag hide, OR (our choice) a clear forthcoming indication. We label the menu item **"Subscribe (coming in Epic 4)"** and have the stub command emit the AC-mandated `"subscribe not yet implemented"` Warning. Rationale: a transient teaching-tip/flyout fired from a Core VM is a UI concern with no clean Core seam; a static label conveys "forthcoming" with zero machinery, is trivially correct, and is removed by Story 4.1 (which epics line 1722 anticipates: "the 'coming in Epic 4' flyout / hidden state from Story 2.8 is removed"). Keeping the item visible+enabled (rather than feature-flag-hidden) means the operator can see the capability is planned. Document this choice in the Dev Agent Record.

**9. `MenuFlyout` via `ContextFlyout` on the template root; `x:Bind` to the generated command.** *(§"Context menu mechanism")*
The WinUI 3 TreeView context-menu mechanism is `ContextFlyout` on the DataTemplate's root element — right-clicking the row content opens the flyout. `MenuFlyoutItem.Command="{x:Bind FetchXmlCommand}"` binds against the template's `x:DataType` VM (the generated `IRelayCommand` property). This satisfies AC-2.8.1's "`MenuFlyout` bound via `x:Bind` to `[RelayCommand]` methods". No `Click=` code-behind handlers (Pattern 13 / arch anti-pattern line 1966). The actual right-click→menu UX is manual-verified (Task 9.7) since it needs a live WinUI runtime; the command *logic* is fully unit-tested via `Execute(null)` (Tasks 7, 9).

### What this story does NOT do (scope discipline)

- **Does NOT implement the Properties window** — Story 2.9. 2.8 only surfaces the menu item + a stub Warning command.
- **Does NOT implement GENA subscribe** — Epic 4 / Story 4.1. 2.8 ships the menu item + stub Warning.
- **Does NOT add context menus to action rows or log rows** — no AC; only device + service rows are right-clickable.
- **Does NOT fetch/parse/transform the XML in-app** — it hands the URL to the OS default browser (FR-019/FR-020). No in-app XML viewer.
- **Does NOT add a CanExecute guard** on the fetch commands — `LocationUrl`/SCPDURL always exist on a node that rendered; the whitelist refusal handles the degenerate scheme case at execute time.
- **Does NOT change `NodeServices` consumers' behaviour** — only adds the launcher member; the existing Http/ScpdParser/Ui/Diag flow is untouched.
- **Does NOT make the fetch commands async** — fire-and-forget sync (Decision 4).

### Files being modified — current state & what must be preserved

- **`src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`** (UPDATE): `partial class : ObservableObject, INodeViewModel`. Holds `_entry` (`RegistryEntry` — `.LocationUrl` is `Uri`, `.Uuid` is `Guid`) and `_services` (`NodeServices`). Has `OnIsExpandedChanged` (Story 2.6 device-expand once-guard — **must not be disturbed**). Add `using CommunityToolkit.Mvvm.Input;` + the two new commands. The new commands do NOT touch `Children`/expansion — no risk to the 2.6 expand machinery.
- **`src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`** (UPDATE): `partial class : ObservableObject, INodeViewModel`. Holds `_service` (`.ScpdUrl` is `string`, `.ServiceId` is `string?`), `_deviceLocation` (`Uri`), `_deviceUuid` (`Guid`), `_services`. Has `OnIsExpandedChanged` → `LoadActionsAsync` (Story 2.6 lazy-SCPD once-guard — **must not be disturbed**). Add the two new commands; reuse the `new Uri(_deviceLocation, _service.ScpdUrl)` resolution from `LoadActionsAsync`.
- **`src/ohSpy.Core/ViewModels/NodeServices.cs`** (UPDATE): immutable `record` bundle. Adding a 5th member is a breaking ctor change — touches the DI registration (auto-resolved) + 4 test construction sites (Task 8).
- **`src/ohSpy.Core/Diagnostics/DiagCategories.cs`** (UPDATE): append two constants in a new section; preserve all existing constants + comments.
- **`src/ohSpy.App/MainWindow.xaml`** (UPDATE): device template (root `Grid`) + service template (root `StackPanel`) get a `ContextFlyout`. **Preserve** the existing left-pane `TreeView` selector wiring, the right-pane SSDP log (Story 2.7), and all bindings. Add nothing to the Action/Fallback templates.
- **`src/ohSpy.App/Composition/ServiceRegistration.cs`** (UPDATE): register `IUriLauncher → ShellUriLauncher` singleton before the `NodeServices` line; add `using ohSpy.Core.Shell;`. Preserve all existing registrations + the documented ordering.

### Previous-story intelligence

**Story 2.7 (SSDP log):** established the in-repo story-file shape this file follows; `[Trait("ac", "AC-2.x.n")]` lowercase trait / uppercase value; `CapturingDiagnosticEmitter` is the canonical fake for asserting `Warning` emission (records `(Severity, Category, Message, Context)` — assert `Severity == "Warning"`, `Category`, and `Context.Url`/`Context.DeviceUuid`). Headless dev means the **view-layer** ACs (right-click UX) are manual-verify (Task 9.7), exactly as 2.7's auto-follow was — the **logic** ACs are fully unit-tested.

**Story 2.6 (Service/Action expansion):** `ServiceNodeViewModel.LoadActionsAsync` resolves `new Uri(_deviceLocation, _service.ScpdUrl)` (line 79) — reuse verbatim for `FetchServiceXml` (Decision 5). `[ObservableProperty]`/`[RelayCommand]` require `partial class` (both VMs already are). CT-last convention (no CT params on the new sync commands). Code-review on 2.6 caught **misclassified exception categories** and **dead null-guards** — relevant here: classify the two diagnostic categories precisely (Decision 6), and don't add a null-guard on the non-nullable `ServiceType`/`LocationUrl`.

**Story 2.5 (ShellViewModel / DeviceTreeViewModel / MainWindow):** `MainWindow.xaml.cs` exposes `public ShellViewModel ViewModel { get; }` for `x:Bind`. The device/service `DataTemplate`s already exist with `x:DataType` set — `ContextFlyout` + `MenuFlyoutItem` `x:Bind` slot straight in. `NodeDataTemplateSelector` routes the heterogeneous tree; the menus attach inside the per-type templates (device/service only), so no selector change.

**Story 1.5 / Story 2.6 (NodeServices):** the bundle is `AddSingleton<NodeServices>()` with all members already-registered singletons — DI auto-resolves the ctor. Adding `IUriLauncher` (registered Task 8.4) keeps that auto-resolution working with zero `NodeServices`-registration change beyond the new launcher line.

### Latest tech / library notes

- **CommunityToolkit.Mvvm 8.4.0** (pinned in `Directory.Packages.props`, Story 2.5). `[RelayCommand]` on `private void FetchXml()` generates `public IRelayCommand FetchXmlCommand { get; }`; on `private void Subscribe()` → `SubscribeCommand`; etc. Requires `using CommunityToolkit.Mvvm.Input;`. No new package, no `Directory.Packages.props` change.
- **`Process.Start(ProcessStartInfo { UseShellExecute = true })`** is the supported, cross-platform-documented .NET way to open a URL in the default browser. `System.Diagnostics.Process` is BCL — allowed in Core (`CoreAppBoundaryTests` bans only `Microsoft.UI.*`/`Microsoft.Windows.*`/`WinRT.Interop.*`).
- **`MenuFlyout` / `MenuFlyoutItem` / `ContextFlyout`** ship in WindowsAppSDK (`Microsoft.UI.Xaml.Controls`) — already referenced; XAML-only, no new using/package.

### Code-style + pattern compliance

- **Pattern 1:** file-scoped namespaces; `_camelCase` fields; PascalCase public members.
- **Pattern 2 (CoreAppBoundaryTests):** `IUriLauncher`, `ShellUriLauncher`, `BrowserLaunch`, the new commands, the `NodeServices` change — all pure Core (`System.Diagnostics` + `CommunityToolkit.Mvvm` + BCL). No `Microsoft.UI.*`/`Microsoft.Windows.*`/`WinRT.Interop.*`.
- **Pattern 7:** node VMs are `new`-constructed by their parent; only `IUriLauncher` (singleton) joins DI. `BrowserLaunch` is a stateless static helper (no DI).
- **Pattern 9:** `ObservableObject` base; `[RelayCommand]`; `partial class`.
- **Pattern 11:** new `DiagCategories` constants added with their call sites in this story; no inline category literals.
- **Pattern 13:** `MenuFlyout` `x:Bind` to `[RelayCommand]`; no `Click=` code-behind handlers; bindings `OneTime` (avoid `WMC1506`).
- **Pattern 14 + A2:** test names `Method_Scenario_Expected_AC28n`; `[Trait("ac", "AC-2.8.<n>")]`.

### Anti-patterns to avoid

- **Don't call `Process.Start` inline in the VM** — wrap it behind `IUriLauncher` so the whitelist/warn logic is testable (Decision 1).
- **Don't omit `UseShellExecute = true`** — without it `Process.Start` throws on an `http://` URL (Decision 2).
- **Don't copy-paste the whitelist into both VMs** — share `BrowserLaunch.OpenInDefaultBrowser` (Decision 3).
- **Don't make the fetch commands `async Task`** — fire-and-forget sync `void` (Decision 4 / AC-2.8.6).
- **Don't pass `_service.ScpdUrl` (string) raw** — resolve `new Uri(_deviceLocation, _service.ScpdUrl)` (Decision 5).
- **Don't catch only specific exceptions on the launch** — FR-019 demands warn-on-ANY-failure; catch broad with the documented `CA1031` pragma (Task 2.2).
- **Don't try to open the Properties window from Core in 2.8** — it's a stub Warning; 2.9 owns the window (Decision 7).
- **Don't use a transient flyout / feature flag for Subscribe** — the static "coming in Epic 4" label is the chosen affordance (Decision 8).
- **Don't add `Mode=OneWay` to the `MenuFlyoutItem` command bindings** — `OneTime` (the command never changes; avoids `WMC1506`).
- **Don't add context menus to Action or Fallback templates** — device + service rows only.
- **Don't forget the 4 `NodeServices` test construction sites** — the 5th member breaks them all (Task 8).

### Project Structure Notes

New Core files: `Shell/IUriLauncher.cs`, `Shell/ShellUriLauncher.cs`, `ViewModels/BrowserLaunch.cs`.
Edited Core files: `ViewModels/DeviceNodeViewModel.cs`, `ViewModels/ServiceNodeViewModel.cs`, `ViewModels/NodeServices.cs`, `Diagnostics/DiagCategories.cs`.
Edited App files: `MainWindow.xaml`, `Composition/ServiceRegistration.cs`.
New test files: `Fakes/FakeUriLauncher.cs`, `ViewModels/BrowserLaunchTests.cs`.
Edited test files: `ViewModels/DeviceNodeViewModelTests.cs`, `ViewModels/DeviceTreeViewModelTests.cs`, `ViewModels/ServiceNodeViewModelTests.cs` (NodeServices 5th-arg + new command tests).
No new project, no new package reference, no `Directory.Packages.props` change. One new DI registration (`IUriLauncher`).

Matches the architecture's planned mapping: `ViewModels/DeviceNodeViewModel.FetchXmlCommand`, `ViewModels/ServiceNodeViewModel.FetchXmlCommand` (named `FetchServiceXmlCommand` here — Decision/Task 5.1), OS shell-open via `System.Diagnostics.Process.Start` (arch line 2187), bound in `MainWindow.xaml` (arch line 2184).

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.8] (lines 1175–1209) — verbatim ACs.
- [Source: epics.md#Story 2.9] (lines 1213–1265) — the Properties window that 2.8's "Properties…" stub hands off to; line 1258–1260 picks up "Story 2.8's 'Properties…' menu item".
- [Source: epics.md#Story 4.3] (lines 1719–1722) — Story 4.1/4.3 removes the 2.8 Subscribe stub + "coming in Epic 4" affordance.
- [Source: epics.md#XML Viewing] (FR-017 line 69, FR-018 line 70, FR-019 line 71, FR-020 line 72; FR-052 line 76) — context-menu + browser-open requirements.
- [Source: epics.md#Success Criteria] (SC-005 line 157 / 2081) — "View XML → default browser opens ≤ 2 s".
- [Source: architecture.md#Component-to-FR map] (line 2187) — "4.6 XML viewing: `DeviceNodeViewModel.FetchXmlCommand`, `ServiceNodeViewModel.FetchXmlCommand`, OS shell-open via `System.Diagnostics.Process.Start`".
- [Source: architecture.md#Validation gaps] (Gap-3, line 3062) — `http://`/`https://` whitelist + Warning for other schemes is the story-level safety AC.
- [Source: architecture.md#MVVM patterns] (lines 1772–1793, 1955–1966) — `[RelayCommand]`, `x:Bind` command binding, `Click=` anti-pattern.
- [Source: src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs] — `_entry.LocationUrl` (`Uri`), `_entry.Uuid`, `_services`; preserve `OnIsExpandedChanged`.
- [Source: src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs] — `new Uri(_deviceLocation, _service.ScpdUrl)` resolution (line 79); `_deviceUuid`, `_service.ServiceId`; preserve `LoadActionsAsync`.
- [Source: src/ohSpy.Core/ViewModels/NodeServices.cs] — bundle to extend with `IUriLauncher`.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs + DiagnosticContext.cs] — category constant convention (Pattern 11); `Url`/`DeviceUuid`/`ErrorText`/`ServiceId` context fields.
- [Source: src/ohSpy.App/MainWindow.xaml] (lines 53–116) — device/service `DataTemplate`s to add `ContextFlyout` to.
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs] (lines 97–105) — `NodeServices` registration; add the `IUriLauncher` line before it.
- [Source: tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs] — `Warning` assertion fake.
- [Source: tests/ohSpy.Core.Tests/Architecture/CoreAppBoundaryTests.cs] — Core must stay WinUI-free (new types comply).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (dev-story workflow)

### Debug Log References

- `dotnet build tests/ohSpy.Core.Tests` — **0 errors / 0 warnings**.
- `dotnet test tests/ohSpy.Core.Tests` — Failed: 0, Passed: **298**, Skipped: 2 (baseline 283 + 15 new). The 2 skips (`AsyncDisciplineTests`, `DiagCategoriesUsageTests`) are unchanged from the Story 2.7 baseline.
- `dotnet build src/ohSpy.App -c Debug -p:RuntimeIdentifier=win-x64` — **0 errors**. 1 warning: `WMC1506` at `MainWindow.xaml(140,37)` — this is the **pre-existing** Story 2.5 `FallbackTemplate` `{x:Bind Label, Mode=OneWay}` warning, shifted from line 121 → 140 by the two `ContextFlyout` blocks added above it. **No new warnings**: the new `MenuFlyoutItem` command bindings are all `OneTime` (no `Mode=OneWay`) and compiled clean. (Per Story 2.7 review, a clean `dotnet build` from repo root is warning-free — `WMC1506` is a local incremental-build artifact on the `FallbackTemplate`; not promoted to an error by `TreatWarningsAsErrors`, which does not govern XAML-compiler WMC warnings.)
- `dotnet test --filter "category=chaos"` — exactly **1** passing (chaos suite unchanged).
- `dotnet test --filter "FullyQualifiedName~CoreAppBoundary"` — **4** passing (`IUriLauncher`, `ShellUriLauncher`, `BrowserLaunch`, the new commands, the `NodeServices` change are all pure Core — no `Microsoft.UI.*` references).
- One regression surfaced + fixed during verification: `DiagCategoriesTests.DiagCategories_ExactSetMatchesArchitecturePinnedList` (the architecturally-pinned category-set guard) failed when the two new constants were added. Added `"ShellExecute"` + `"FeatureNotImplemented"` to the test's `expectedNames` list — a deliberate sync, exactly as that test's comment mandates.

### Completion Notes List

- **Launcher seam (Decision 1):** wrapped the single `Process.Start(UseShellExecute=true)` behind `IUriLauncher`/`ShellUriLauncher` (Core, `System.Diagnostics`-only) so the Gap-3 whitelist + warn-on-failure are unit-testable via `FakeUriLauncher` without spawning a real browser. `ShellUriLauncher` is not unit-tested directly (it would launch a browser) — covered by the seam contract + the manual smoke (Task 10.7).
- **`UseShellExecute = true` (Decision 2):** assigned `Process.Start(...)`'s return to `_` (discard) — fire-and-forget, no `IDE0058` flag.
- **Shared `BrowserLaunch.OpenInDefaultBrowser` (Decision 3):** both `FetchXmlCommand` (device) and `FetchServiceXmlCommand` (service) route through the one internal static helper — whitelist (`http`/`https` via `Uri.UriSchemeHttp`/`Https`, case-insensitive, `IsAbsoluteUri`-guarded) → `launcher.Launch` → broad `catch (Exception)` with documented `CA1031` pragma (FR-019 warn-not-crash). Returns `bool` for testability.
- **Sync `void` commands (Decision 4):** `[RelayCommand] private void FetchXml()` / `FetchServiceXml()` / `OpenProperties()` / `Subscribe()` generate synchronous `IRelayCommand`s — fire-and-forget per AC-2.8.6; no async readback.
- **Service URL resolution (Decision 5):** `FetchServiceXml` resolves `new Uri(_deviceLocation, _service.ScpdUrl)`, reusing the Story 2.6 `LoadActionsAsync` idiom (relative SCPDURL → absolute; absolute passes through). Verified by `FetchServiceXmlCommand_ResolvesRelativeScpdUrl_Launches_AC284` + `_AbsoluteScpdUrl_PassesThrough_AC284`.
- **Naming:** the service command is `FetchServiceXmlCommand` (not the arch's shorthand `FetchXmlCommand`) — matches the "Fetch service XML" menu label and reads clearly beside the device's `FetchXmlCommand`. Minor, documented deviation from arch line 2187.
- **Two DiagCategories (Decision 6):** `Shell.Execute` (permanent — refused scheme + launch failure) and `Feature.NotImplemented` (temporary — Subscribe/Properties stubs). Added with their call sites (Pattern 11); added to the pinned-set guard test.
- **Properties stub (Decision 7):** `OpenPropertiesCommand` emits a `Feature.NotImplemented` Warning (Core VM can't open a `Window`); Story 2.9 replaces the body and may relocate the command to the App layer. Menu item is present + wired per AC-2.8.1.
- **Subscribe stub (Decision 8):** `SubscribeCommand` emits the AC-mandated `"subscribe not yet implemented"` Warning; the operator "forthcoming" affordance is the static menu-item label **"Subscribe (coming in Epic 4)"** (chosen over a transient flyout / feature flag — no Core seam needed; Story 4.1 relabels + removes the stub).
- **`NodeServices` 5th member:** added `IUriLauncher Launcher`; updated all 4 test construction sites (+ new `FakeUriLauncher`) and the DI root (`AddSingleton<IUriLauncher, ShellUriLauncher>()` before the `NodeServices` registration — auto-resolved into the bundle).
- **XAML (Decision 9):** `ContextFlyout` → `MenuFlyout` on the device template root `Grid` and service template root `StackPanel`; `MenuFlyoutItem.Command` `x:Bind` (`OneTime`) to the generated commands. No `Click=` code-behind (Pattern 13). The "Properties…" ellipsis uses the `&#x2026;` XAML entity.
- **Task 10.7 (manual UI smoke) — NOT executed:** requires a running WinUI desktop session, unavailable in this headless dev environment (same constraint as Stories 2.6/2.7). The AC-gating *command logic* (whitelist, warn-on-refusal, warn-on-failure, the two stubs, URL resolution) is fully covered by the 15 new unit tests via `Execute(null)`. The *view* behaviours it would confirm — the actual right-click → menu UX (AC-2.8.1/2.8.4) and the ≤ 2 s browser open (AC-2.8.2/2.8.6) — can ONLY be confirmed with a live UI. **Recommend running before closing Epic 2** (alongside the deferred 2.6/2.7 manual smokes).

### File List

**New (Core):**
- `src/ohSpy.Core/Shell/IUriLauncher.cs`
- `src/ohSpy.Core/Shell/ShellUriLauncher.cs`
- `src/ohSpy.Core/ViewModels/BrowserLaunch.cs`

**Modified (Core):**
- `src/ohSpy.Core/ViewModels/NodeServices.cs`
- `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`
- `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs`

**Modified (App):**
- `src/ohSpy.App/MainWindow.xaml`
- `src/ohSpy.App/Composition/ServiceRegistration.cs`

**New (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/FakeUriLauncher.cs`
- `tests/ohSpy.Core.Tests/ViewModels/BrowserLaunchTests.cs`

**Modified (Tests):**
- `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs`
- `tests/ohSpy.Core.Tests/Diagnostics/DiagCategoriesTests.cs`

### Change Log

| Date | Change |
|---|---|
| 2026-06-03 | Story 2.8 context created via bmad-create-story (claude-opus-4-8[1m]); backlog → ready-for-dev. |
| 2026-06-03 | Story 2.8 implemented (dev-story, claude-opus-4-8[1m]): `IUriLauncher`/`ShellUriLauncher` shell-open seam (Core); shared `BrowserLaunch.OpenInDefaultBrowser` helper (http/https whitelist + warn-on-refusal/failure, Gap-3); `DeviceNodeViewModel.FetchXmlCommand` (LocationUrl, FR-019) + `OpenPropertiesCommand` stub (Story 2.9 handoff); `ServiceNodeViewModel.FetchServiceXmlCommand` (resolved SCPDURL, FR-020) + `SubscribeCommand` stub ("coming in Epic 4" label, Story 4.1 handoff); two new `DiagCategories` (`Shell.Execute`, `Feature.NotImplemented`); `IUriLauncher` added to `NodeServices` (+ DI registration + 4 test ctor sites); `MainWindow.xaml` device/service `ContextFlyout`+`MenuFlyout` `x:Bind` commands. 15 new tests (283→298), 2 skips unchanged; chaos=1, CoreAppBoundary=4 green. App build 0 errors (1 pre-existing benign WMC1506 on the 2.5 FallbackTemplate, no new warnings). Task 10.7 manual UI smoke not executed (headless). Status → review. |
| 2026-06-03 | Story 2.8 review → done by code-review workflow (claude-sonnet-4-6, bmad-code-review). APPROVED. 0 patches required. Build 0 errors/0 warnings (clean, confirmed). Tests 298 passed/2 skipped confirmed. Chaos=1, CoreAppBoundary=4 green. All AC-2.8.1..2.8.6 satisfied. Task 10.7 manual UI smoke remains pending before Epic 2 close (deferred, headless env). 1 low-severity observation noted (FakeUriLauncher design note — informational only, no production impact). |

## Senior Developer Review (AI)

**Reviewer:** claude-sonnet-4-6 (bmad-code-review workflow, 2026-06-03)
**Baseline commit:** `2f0d4e52c2205050767c7a220dbcf3821c59ada0`
**Diff scope:** All uncommitted working-tree changes (modified tracked files + untracked new files listed in the Dev Agent Record File List)
**Build verified:** `dotnet build src/ohSpy.App/ohSpy.App.csproj -c Debug -p:RuntimeIdentifier=win-x64 --nologo` — **0 errors, 0 warnings**. Dev claim of "1 pre-existing WMC1506" is a local-incremental-build artifact; clean build from repo root is warning-free (identical pattern to Story 2.7 review). Build claim confirmed as at-least-as-good-as-claimed.
**Test verified:** `dotnet test tests/ohSpy.Core.Tests` — **298 passed, 2 skipped, 0 failed**. Dev claim CONFIRMED (283 baseline + 15 new). Dev breakdowns confirmed: `DeviceNodeViewModelTests` 20 passing (16 baseline + 4 new), `ServiceNodeViewModelTests` 14 passing (11 baseline + 3 new), `BrowserLaunchTests` 8 passing (5 theory cases + 3 facts, all new).
**Chaos suite:** 1 passing (unchanged). CONFIRMED.
**CoreAppBoundaryTests:** 4 passing (new `IUriLauncher`, `ShellUriLauncher`, `BrowserLaunch` are pure Core — no `Microsoft.UI.*` references). CONFIRMED.

### Review Findings

No findings rise to High or Medium severity. One Low observation noted below.

- [ ] [Low / Informational] `FakeUriLauncher.Launch` records the URL before throwing [`tests/ohSpy.Core.Tests/Fakes/FakeUriLauncher.cs:16-17`] — The fake appends to `Launched` and then throws if `ThrowOnLaunch` is set, meaning `launcher.Launched.Count == 1` even on a simulated launch failure. The `LaunchThrows_Warns_NoCrash_AC282` test in `BrowserLaunchTests` does not assert `launcher.Launched` contents on this path (checking only the Warning diagnostic), so the test is valid. This is a minor design note: a reader might expect `Launched` to be empty on throw (since "the launch failed"). The current ordering more accurately models the production sequence — `ShellUriLauncher` creates the `Process` before discovering the failure. No production correctness issue; no change required. Informational only.

### AC Coverage Assessment

- **AC-2.8.1** (device context menu surface): Confirmed — `Grid.ContextFlyout` with `FetchXmlCommand` + `OpenPropertiesCommand` bound via `x:Bind` in the `DataTemplate x:DataType="vm:DeviceNodeViewModel"` template. XAML compiles clean (0 warnings). `OpenPropertiesCommand_Stub_WarnsNotImplemented_AC281` test passes.
- **AC-2.8.2** (device Fetch XML opens LocationUrl): Confirmed — `FetchXmlCommand` routes through `BrowserLaunch.OpenInDefaultBrowser`; `FetchXmlCommand_OpensLocationUrl_AC282`, `FetchXmlCommand_LaunchFailure_Warns_NoCrash_AC282`, `Http_LaunchesUrl_AC282`, `Https_LaunchesUrl_AC282`, `LaunchThrows_Warns_NoCrash_AC282` tests all pass.
- **AC-2.8.3** (URL whitelist): Confirmed — `IsHttpOrHttps` uses `Uri.IsAbsoluteUri` guard + `Uri.UriSchemeHttp`/`UriSchemeHttps` constants case-insensitively. `NonHttpScheme_Refused_NoLaunch_Warns_AC283` theory over `file:`, `ftp:`, `javascript:`, `mailto:` all pass. `FetchXmlCommand_NonHttpLocation_Refused_Warns_AC283` passes.
- **AC-2.8.4** (service context menu + SCPD URL): Confirmed — `StackPanel.ContextFlyout` with `FetchServiceXmlCommand` + `SubscribeCommand`; `FetchServiceXml` resolves `new Uri(_deviceLocation, _service.ScpdUrl)` matching `LoadActionsAsync` idiom exactly. `FetchServiceXmlCommand_ResolvesRelativeScpdUrl_Launches_AC284` and `_AbsoluteScpdUrl_PassesThrough_AC284` both pass.
- **AC-2.8.5** (Subscribe stub): Confirmed — `SubscribeCommand` emits `FeatureNotImplemented` Warning with message `"subscribe not yet implemented"`; menu label is `"Subscribe (coming in Epic 4)"`. `SubscribeCommand_Stub_WarnsNotImplemented_AC285` passes.
- **AC-2.8.6** (fire-and-forget sync): Confirmed — all four commands are `[RelayCommand] private void` (not `async Task`), generating synchronous `IRelayCommand` instances. No `Task`/async machinery involved.

### Key Design Decisions Verified as Correct

- **`IUriLauncher` seam (Decision 1):** correctly wraps the single `Process.Start(UseShellExecute=true)` in `ShellUriLauncher` (Core, `System.Diagnostics`-only). `CoreAppBoundaryTests` confirms no WinUI leakage. The seam pattern is consistent with `INetworkInterfaceSource`, `IUiDispatcher`, `IDiagnosticEmitter` — the right pattern for the project.
- **`IsHttpOrHttps` guard (AC-2.8.3):** `url.IsAbsoluteUri` check before `url.Scheme` access correctly prevents the `InvalidOperationException` that `Uri.Scheme` throws on a relative `Uri`. All construction sites (`FetchXml`, `FetchServiceXml`) pass absolute URIs (`_entry.LocationUrl` is always absolute; `new Uri(absolute, relative)` always yields absolute), but the guard makes `BrowserLaunch.OpenInDefaultBrowser` a total function — correct defensive programming.
- **Broad `catch (Exception)` with `CA1031` pragma (AC-2.8.2/FR-019):** justified. `Process.Start(UseShellExecute=true)` can throw `Win32Exception`, `ObjectDisposedException`, `InvalidOperationException`, or `PlatformNotSupportedException` depending on OS state. Narrowing to one type would silently swallow the others. The pragma scope is minimal (just the `catch` line). Identical pattern to project's prior catch-all paths.
- **`FetchServiceXml` URL resolution (Decision 5):** `new Uri(_deviceLocation, _service.ScpdUrl)` at line 150 of `ServiceNodeViewModel.cs` is verbatim identical to line 80 (`LoadActionsAsync`). The browser will open the exact same URL the tree fetched. Verified by `_ResolvesRelativeScpdUrl` and `_AbsoluteScpdUrl` tests.
- **`NodeServices` 5th member + 4 test construction sites:** all updated correctly. `DeviceNodeViewModelTests` static field (line 21–23), local inline instance (line 222), `DeviceTreeViewModelTests` constructor (line 26–28), `ServiceNodeViewModelTests.MakeNodeServices` factory (line 39–41) — all confirmed present and correct.
- **DI registration order (Task 8.4):** `AddSingleton<IUriLauncher, ShellUriLauncher>()` at line 99 in `ServiceRegistration.cs`, placed before `AddSingleton<NodeServices>()` at line 104. Correct — the container resolves `NodeServices` lazily via ctor injection; the `IUriLauncher` registration must precede the first `NodeServices` resolve. Order confirmed correct.
- **XAML `x:Bind` binding modes (Decision 9/Pattern 13):** all four `MenuFlyoutItem.Command` bindings omit `Mode=` (defaulting to `OneTime`). No `Mode=OneWay` is present on any new binding. `WMC1506` is not raised for any new binding. The `"Properties&#x2026;"` ellipsis correctly uses the XAML entity for U+2026 (horizontal ellipsis), consistent with `LoadingPlaceholderViewModel`'s `"Loading…"`. No Action or Fallback template received a context menu. Verified by reading the XAML and the clean build.
- **`DiagCategoriesTests` pinned-set guard updated (dev's note):** `"ShellExecute"` and `"FeatureNotImplemented"` added to `expectedNames` in `DiagCategories_ExactSetMatchesArchitecturePinnedList`. The test runs and passes. The update is correct — it is a deliberate architecture-sync, exactly as the test's comment mandates.
- **`ShellUriLauncher.Launch` return value (Decision 2):** `_ = Process.Start(...)` discards the `Process?` return — correct fire-and-forget pattern, no `IDE0058` suppression needed because the result is explicitly assigned to discard.
- **Story 2.9 handoff (Decision 7):** `OpenPropertiesCommand` stub on the Core `DeviceNodeViewModel` emits a `Feature.NotImplemented` Warning. The dev notes clearly document the seam tension (Core VM cannot open a Window) and the Story 2.9 resolution options (relocate to App layer / inject factory). The 2.9 author is properly warned in both the Dev Notes and the Completion Notes.

### Review Follow-ups (AI)

**Approved.** The implementation is architecturally sound, fully satisfies all six ACs, contains no defects, and presents no regressions. The test suite is substantive and non-tautological: whitelist refusal, launch failure, URL resolution (relative + absolute), stub behaviour, and the DeviceUuid context flow are all independently exercised.

**Not actionable now:**
- Task 10.7 manual UI smoke test (AC-2.8.1/2.8.4 right-click UX + AC-2.8.2/2.8.6 ≤2 s open) remains unexecuted (headless environment). Recommend running before closing Epic 2 — alongside the deferred 2.6/2.7 manual smokes. This covers: actual right-click → menu surface on device and service rows, browser open within ≤2 s, `Feature.NotImplemented` Warning visible in diagnostics for Properties and Subscribe.
- Dev Agent Record claim of "1 pre-existing WMC1506": this is a local-incremental-build artifact, same pattern as Story 2.7. Clean `dotnet build` produces 0 warnings. No action required.
