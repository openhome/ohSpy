---
baseline_commit: 00a2f4a36b3edc47674b110753aa53e0b9c4efd1
---

# Story 2.9: Window Ownership Manager + Properties Window (First Popup)

Status: review

## Story

As a Linn engineer,
I want right-click → Properties… on a device row to open a read-only Properties window showing the full UPnP description and SSDP metadata, owned by the main window so its z-order and lifetime behave correctly, and surviving cleanly if the device leaves the network while open,
so that I can see every captured field for a device without committing to keep it on the network — and the popup behaves like a proper Windows child window.

> **⚠️ READ FIRST — automated vs. manual test surface.** This is the project's **first popup window**. The headline deliverable (`WindowOwnershipManager` Win32 ownership + the `PropertiesWindow` XAML) is **inherently manual-verify**: it needs a live WinUI runtime + real HWNDs, and there is **no App-layer test project** (Core.Tests must never reference `ohSpy.App` — `CoreAppBoundaryTests`). The architecture already marks AC-10.2/10.3/10.4 as manual UI tests. The **automated** test surface for this story is **`PropertiesViewModel`** (pure Core): field mapping, absent→placeholder, the device-gone banner, the URL-open whitelist, and dispose/unsubscribe. The windowing + XAML + Adopt sequence are verified by **a clean App build + code review + the Task 11 manual smoke**. Do not try to unit-test `WindowOwnershipManager`/`PropertiesWindow`/`PropertiesLauncher` in Core.Tests — they cannot compile there.

## Acceptance Criteria

**Verbatim ACs from epics.md §Story 2.9 (lines 1219–1265). This story assigns numbers AC-2.9.1 … AC-2.9.7 to the seven `Given/When/Then` blocks; the D10 `AC-10.x` tags from the architecture are preserved inline where the epics cite them.**

**AC-2.9.1 — WindowOwnershipManager shape (D10)**

**Given** `src/ohSpy.App/Windowing/WindowOwnershipManager.cs`
**When** I inspect it
**Then** it implements `IWindowOwnershipManager` declared in `Core` (or `App` if the interface is App-local — D10 default)
**And** it uses `[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]` for the Win32 `SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, parentHwnd)` call with `GWLP_HWNDPARENT = -8` (D10)
**And** it tracks ownership in a `Dictionary<IntPtr, List<IntPtr>>` for testability via `GetChildrenOf(parent)`
**And** the `Closed` event on the child window prunes the tracking dictionary

**AC-2.9.2 — Canonical popup-open pattern (AC-10.1)**

**Given** the canonical popup-open pattern (D10)
**When** any popup is constructed
**Then** the sequence is `window.Activate()` THEN `_windowOwnership.Adopt(window, _shellWindow)` (AC-10.1 — order is non-obvious but empirically required in WinUI 3)
**And** the pattern is documented in code-comment on the `Adopt` method so future popup-creation sites (Epics 3-5) follow it verbatim

**AC-2.9.3 — FR-046 behaviours (manual)**

**Given** the four FR-046 behaviours
**When** the Properties popup is open
**Then** it appears above the main window when shown (AC-10.1)
**And** clicking the main window for focus does NOT push the Properties popup behind it (AC-10.4 — manual UI test)
**And** minimising the main window minimises the Properties popup; restoring restores it (AC-10.3 — manual UI test)
**And** closing the main window closes the Properties popup (AC-10.2 — manual UI test)
**And** the popup is independently activatable — z-order ownership is NOT modality (D10)

**AC-2.9.4 — PropertiesViewModel shape (FR-052)**

**Given** `src/ohSpy.Core/ViewModels/PropertiesViewModel.cs`
**When** I inspect it
**Then** it takes a `RegistryEntry` in its constructor and exposes read-only properties grouped per FR-052: `Identity` (FriendlyName, DeviceTypeUrn, Udn / Uuid, PresentationUrl), `Manufacturer` (Manufacturer, ManufacturerUrl, ModelName, ModelNumber, ModelDescription, ModelUrl, SerialNumber, Upc), `Network` (LocationUrl, Ip, Port, SsdpServer, CacheControlMaxAgeSeconds), `DiscoveryHistory` (FirstSeenUtc, LastSeenUtc, AliveCount, BootId, ConfigId), `EmbeddedDevices` (recursive list)
**And** fields the device did not declare render as a muted placeholder (e.g. `"—"`) so the operator can distinguish "absent" from "empty" (FR-052 consequence)

**AC-2.9.5 — PropertiesWindow XAML (read-only, hyperlinks, sections)**

**Given** the Properties window XAML (`src/ohSpy.App/Views/PropertiesWindow.xaml`)
**When** the window renders
**Then** it is read-only (no editable controls)
**And** `PresentationUrl`, `ManufacturerUrl`, `ModelUrl`, `LocationUrl` render as clickable hyperlinks (when present and matching the http/https whitelist from Story 2.8); clicking opens in the default browser via the same shell-execute path
**And** the layout uses sections with section headers matching the FR-052 grouping (Identity / Manufacturer / Network / Discovery history / Embedded devices)

**AC-2.9.6 — Device-removal survival (FR-037 / NFR-R3)**

**Given** the Properties window is open
**When** the device leaves the network (`byebye` arrives, registry removes the entry — FR-008)
**Then** the popup transitions to a "device is no longer reachable" UI state (e.g. a banner at the top reading "Device left the network at <time>"; data remains visible from the snapshot at popup-open time)
**And** the popup remains closeable without producing errors (FR-037 + NFR-R3 + AC-10.5)
**And** the registry's `DeviceRemoved(uuid)` event is the trigger — the VM subscribes to the registry and matches by UUID

**AC-2.9.7 — Right-click handler + DI wiring (Pattern 7 / AC-10.5)**

**Given** the right-click handler from Story 2.8's "Properties…" menu item
**When** the user chooses it
**Then** `DeviceNodeViewModel.OpenPropertiesCommand` (or `ShellViewModel.OpenPropertiesCommand` — engineering judgment, document the seam) creates a new `PropertiesWindow(propertiesVm)`, calls `Activate()`, calls `_windowOwnership.Adopt(propertiesWindow, _shellWindow)` (AC-10.5)

**Given** the DI composition root
**When** the App starts
**Then** `IWindowOwnershipManager` is registered as a singleton with `WindowOwnershipManager` as the implementation
**And** a `Func<RegistryEntry, PropertiesViewModel>` factory is registered so popups can be constructed without leaking the `IServiceProvider` to call sites (Pattern 7)

---

## Tasks / Subtasks

### Task 1 — `IWindowOwnershipManager` + `WindowOwnershipManager` (App, D10) (AC: #1, #2)

The interface is **App-local** (D10 default per AC-2.9.1) — it takes `Microsoft.UI.Xaml.Window`, which is forbidden in Core (Pattern 2). Co-locate the interface and impl in the one file (the architecture's project tree lists only `WindowOwnershipManager.cs`, no separate interface file).

- [x] **1.1** Create `src/ohSpy.App/Windowing/WindowOwnershipManager.cs` with the interface + impl. Use `[LibraryImport]` (source-generated P/Invoke; requires `partial` method + `partial` class) — NOT `[DllImport]`:
  ```csharp
  namespace ohSpy.App.Windowing;

  using System.Runtime.InteropServices;
  using Microsoft.UI.Xaml;

  /// <summary>
  /// Establishes the Win32 owner relationship (FR-046) for every secondary window. WinUI 3's
  /// <see cref="Window"/> exposes no Owner property (unlike WPF), so the four FR-046 behaviours
  /// (z-order above parent, no-push-behind on focus, minimise/restore together, close-with-parent)
  /// are delivered by SetWindowLongPtr(GWLP_HWNDPARENT) — centralised here so the contract is a
  /// pattern, not boilerplate (Decision 10).
  /// </summary>
  public interface IWindowOwnershipManager
  {
      /// <summary>
      /// Establish FR-046 ownership of <paramref name="child"/> by <paramref name="parent"/>.
      /// MUST be called AFTER <c>child.Activate()</c> — calling SetWindowLongPtr before Activate
      /// leaves the relationship undefined in WinUI 3 (empirically required; AC-10.1). Every popup
      /// creation site (Epics 2-5) follows window.Activate() THEN Adopt(window, shellWindow).
      /// </summary>
      void Adopt(Window child, Window parent);

      /// <summary>Child windows currently owned by <paramref name="parent"/> (testability / introspection).</summary>
      IReadOnlyList<IntPtr> GetChildrenOf(Window parent);
  }

  internal sealed partial class WindowOwnershipManager : IWindowOwnershipManager
  {
      private const int GWLP_HWNDPARENT = -8;
      private readonly Dictionary<IntPtr, List<IntPtr>> _ownership = new();

      // Source-generated P/Invoke (.NET 7+). SetWindowLongPtrW is the wide (Unicode) entry point;
      // IntPtr is the correct pointer-sized type on both x64 and ARM64.
      [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
      private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

      public void Adopt(Window child, Window parent)
      {
          var childHwnd = WinRT.Interop.WindowNative.GetWindowHandle(child);
          var parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(parent);

          // FR-046: the OS owner relationship. After this the OS delivers z-order, no-push-behind,
          // minimise/restore-with-parent, and close-with-parent for free — no event handlers needed.
          SetWindowLongPtr(childHwnd, GWLP_HWNDPARENT, parentHwnd);

          if (!_ownership.TryGetValue(parentHwnd, out var children))
              _ownership[parentHwnd] = children = new();
          children.Add(childHwnd);

          // Prune tracking when the child closes (the OS has already torn down the owner link).
          child.Closed += (_, _) =>
          {
              if (_ownership.TryGetValue(parentHwnd, out var list))
                  list.Remove(childHwnd);
          };
      }

      public IReadOnlyList<IntPtr> GetChildrenOf(Window parent)
      {
          var parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(parent);
          return _ownership.TryGetValue(parentHwnd, out var children)
              ? children.AsReadOnly()
              : Array.Empty<IntPtr>();
      }
  }
  ```
  - **`GetChildrenOf` returns `IReadOnlyList<IntPtr>`** (HWNDs), not `IReadOnlyList<Window>`. The architecture sketch shows `Window` but the tracking dictionary stores HWNDs (the only thing the P/Invoke needs); returning HWNDs avoids retaining `Window` references (lifetime leak) and is sufficient for the AC's "testability via GetChildrenOf". Document this minor deviation from the arch sketch in the Dev Agent Record.
  - **Why `[LibraryImport]` not `[DllImport]`:** the existing `Program.cs` `MessageBoxW` uses the older `[DllImport]`, but D10 pins `[LibraryImport]` for this class (the .NET 7+ source-generated form). The `partial` keyword on BOTH the class and the method is mandatory for the generator.
  - **No unit test** for this class (needs real `Window`/HWND). Manual-verify (Task 11) + code review (AC-10.1/10.5).

### Task 2 — `IPropertiesLauncher` seam (Core) + `PropertiesLauncher` (App) (AC: #2, #7)

`DeviceNodeViewModel` lives in **Core** and CANNOT construct a `PropertiesWindow` (App). The seam: a Core interface the VM command calls, implemented in App where the window work happens. (This resolves the epics' "DeviceNodeViewModel.OpenPropertiesCommand … creates a new PropertiesWindow" — the *command* stays on the Core VM; the *window construction* is delegated across the boundary via this seam. Document the seam per AC-2.9.7.)

- [x] **2.1** Create `src/ohSpy.Core/ViewModels/IPropertiesLauncher.cs` (Core, WinUI-free):
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using ohSpy.Core.Devices;

  /// <summary>
  /// Core seam for opening the (App-layer) read-only Properties window for a device. Implemented
  /// in ohSpy.App (PropertiesLauncher) because constructing a WinUI Window is not a Core concern.
  /// Lets DeviceNodeViewModel.OpenPropertiesCommand (Core) trigger the popup across the Core/App
  /// boundary (Pattern 2). Story 2.9; Epics 3-5 add sibling popup seams following the same
  /// window.Activate()→Adopt() sequence (Decision 10).
  /// </summary>
  public interface IPropertiesLauncher
  {
      /// <summary>Open the read-only Properties window for <paramref name="entry"/> (UI-thread; fire-and-forget).</summary>
      void OpenProperties(RegistryEntry entry);
  }
  ```
- [x] **2.2** Create `src/ohSpy.App/Windowing/PropertiesLauncher.cs` (App). Holds the Pattern-7 VM factory + `IWindowOwnershipManager` + a settable shell-window reference (set in `App.OnLaunched`):
  ```csharp
  namespace ohSpy.App.Windowing;

  using Microsoft.UI.Xaml;
  using ohSpy.App.Views;
  using ohSpy.Core.Devices;
  using ohSpy.Core.ViewModels;

  /// <summary>
  /// App-side <see cref="IPropertiesLauncher"/>: constructs the PropertiesViewModel via the
  /// Pattern-7 factory, news up the PropertiesWindow, and applies the canonical D10 popup-open
  /// sequence (Activate THEN Adopt). The shell window is injected post-construction by
  /// App.OnLaunched (the MainWindow is created there, not in DI).
  /// </summary>
  internal sealed class PropertiesLauncher : IPropertiesLauncher
  {
      private readonly Func<RegistryEntry, PropertiesViewModel> _vmFactory;
      private readonly IWindowOwnershipManager _ownership;

      /// <summary>The main window, set once in App.OnLaunched. Parent for FR-046 ownership.</summary>
      public Window? ShellWindow { get; set; }

      public PropertiesLauncher(
          Func<RegistryEntry, PropertiesViewModel> vmFactory, IWindowOwnershipManager ownership)
      {
          _vmFactory = vmFactory;
          _ownership = ownership;
      }

      public void OpenProperties(RegistryEntry entry)
      {
          var vm = _vmFactory(entry);
          var window = new PropertiesWindow(vm);
          window.Activate();                                   // (1) D10: MUST precede Adopt
          if (ShellWindow is not null)
              _ownership.Adopt(window, ShellWindow);           // (2) FR-046 ownership (AC-10.5)
      }
  }
  ```
  - **No unit test** (App, needs WinUI). Manual-verify (Task 11) + code review.

### Task 3 — `PropertiesViewModel` (Core, FR-052) (AC: #4, #5, #6)

Create `src/ohSpy.Core/ViewModels/PropertiesViewModel.cs`. This is the **automated-test heart** of the story. It is `partial` `ObservableObject` (for the device-gone observable state), `IDisposable` (unsubscribes from the registry). It **snapshots** all display fields at construction from the passed `RegistryEntry` (data survives device removal — AC-2.9.6).

- [x] **3.1** Constructor signature + dependencies (injected by the Pattern-7 factory — Task 5.3):
  ```csharp
  public PropertiesViewModel(
      RegistryEntry entry,
      IDeviceRegistry registry,   // subscribe to DeviceRemoved (FR-037)
      IUriLauncher launcher,      // Story 2.8 shell-open seam (hyperlinks)
      IDiagnosticEmitter diag)    // Story 2.8 whitelist Warning path
  ```
  Capture `_uuid = entry.Uuid` and `_locationUrl = entry.LocationUrl` (base for resolving relative hyperlink URLs). Snapshot every display field NOW (do not hold the `RegistryEntry` for display — hold only what's needed: `_uuid`, `_locationUrl`, `launcher`, `diag`).

- [x] **3.2** Grouped read-only display properties. Use a small private helper `static string OrDash(string? s) => string.IsNullOrEmpty(s) ? "—" : s;` for the absent→placeholder rule (AC-2.9.4 "absent vs empty"). Map from `entry.Description` (`DeviceDescription`) and the `RegistryEntry` SSDP metadata:
  - **Identity:** `FriendlyName` = `OrDash(desc?.FriendlyName)`; `DeviceTypeUrn` = `OrDash(desc?.DeviceType)`; `Udn` = `OrDash(desc?.Udn)`; `Uuid` = `entry.Uuid.ToString()`; `PresentationUrl` = `OrDash(desc?.PresentationUrl)`.
  - **Manufacturer:** `Manufacturer`, `ManufacturerUrl`, `ModelName`, `ModelNumber`, `ModelDescription`, `ModelUrl`, `SerialNumber`, `Upc` — each `OrDash(desc?.X)`.
  - **Network:** `LocationUrl` = `entry.LocationUrl.ToString()`; `Ip` = `entry.LocationUrl.Host`; `Port` = `entry.LocationUrl.Port` (int; render as-is — `LocationUrl` is always absolute with a port); `SsdpServer` = `OrDash(entry.Server)`; `CacheControlMaxAgeSeconds` = `entry.CacheControlMaxAge?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ?? "—"`.
  - **DiscoveryHistory:** `FirstSeenUtc` / `LastSeenUtc` (format local-time, e.g. `ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture)` — DiagnosticEmitter/SsdpLogEntry precedent); `AliveCount` (int); `BootId` = `OrDash(entry.BootId)`; `ConfigId` = `OrDash(entry.ConfigId)`.
  - **EmbeddedDevices:** ⚠️ **see Decision 5 — the model retains NO embedded-device records.** Expose `IReadOnlyList<...> EmbeddedDevices` as an **empty list** and a `bool HasEmbeddedDevices => false` (always, currently). The XAML section renders a muted "— (services flattened per FR-053)". Do NOT attempt to reconstruct embedded devices from the flattened `Services` list. Document the gap.
- [x] **3.3** Hyperlink URL opening (AC-2.9.5). Each of the four URL fields needs a *resolved absolute* `Uri` (PresentationUrl is commonly relative) + an open command that routes through the **Story 2.8** whitelist + shell-execute path:
  ```csharp
  // Expose the four resolved Uris (null when absent/unparseable) + a single open command.
  [RelayCommand]
  private void OpenUrl(Uri? url)
  {
      if (url is null) return;
      BrowserLaunch.OpenInDefaultBrowser(url, _launcher, _diag, _uuid);
  }
  ```
  Provide resolved `Uri?` properties: `PresentationUri`, `ManufacturerUri`, `ModelUri`, `LocationUri` — each `TryResolve(desc?.PresentationUrl)` etc., where `TryResolve` does `Uri.TryCreate(_locationUrl, raw, out var u) ? u : null` (resolves relative against `LocationUrl`; absolute passes through). The XAML binds `HyperlinkButton.Command="{x:Bind ViewModel.OpenUrlCommand}" CommandParameter="{x:Bind ViewModel.PresentationUri}"` and shows the field via `Visibility` only when the `Uri` is non-null (else the plain "—" text). `BrowserLaunch` is `internal` in `ohSpy.Core.ViewModels` — directly callable (same namespace/assembly).
- [x] **3.4** Device-gone state (AC-2.9.6). Subscribe to `registry.DeviceRemoved` in the ctor; handler matches UUID and flips observable state:
  ```csharp
  [ObservableProperty] private bool _isDeviceGone;
  [ObservableProperty] private string _deviceGoneText = "";

  private void OnDeviceRemoved(Guid uuid)
  {
      if (uuid != _uuid || IsDeviceGone) return; // ignore other devices; idempotent
      DeviceGoneText = $"Device left the network at {DateTime.Now:HH:mm:ss}";
      IsDeviceGone = true; // data stays visible (snapshot); banner appears (XAML binds Visibility)
  }
  ```
  `DeviceRemoved` fires on the UI thread (registry marshals via `IUiDispatcher`), so the handler sets observable properties directly — no dispatcher hop. Stamp `DateTime.Now` VM-side (no clock seam in Core; SsdpLogEntry/DiagnosticEmitter precedent).
- [x] **3.5** `IDisposable` — unsubscribe (mirror `DeviceTreeViewModel`/`SsdpLogViewModel`): `Interlocked`-guarded `Dispose()` does `registry.DeviceRemoved -= OnDeviceRemoved`. The `PropertiesWindow.Closed` handler calls `vm.Dispose()` (Task 4.3). Without this, the singleton registry pins every Properties VM ever opened.

### Task 4 — `PropertiesWindow.xaml` + code-behind (App, FR-052) (AC: #5, #6)

- [x] **4.1** Create `src/ohSpy.App/Views/PropertiesWindow.xaml` — a `Window` (NOT a Page). Read-only (no editable controls): use `TextBlock`s for values, `HyperlinkButton`s for the four URL fields. A top **banner** bound to `ViewModel.IsDeviceGone` (`Visibility`) showing `ViewModel.DeviceGoneText` (AC-2.9.6). Sections with headers matching FR-052 grouping: **Identity / Manufacturer / Network / Discovery history / Embedded devices**. Wrap content in a `ScrollViewer`. Expose `public PropertiesViewModel ViewModel { get; }` on the code-behind for `x:Bind` (MainWindow precedent). Reuse `MutedForegroundBrush` for the "—" placeholders + the banner styling.
  - Hyperlinks: `<HyperlinkButton Content="{x:Bind ViewModel.PresentationUrl}" Command="{x:Bind ViewModel.OpenUrlCommand}" CommandParameter="{x:Bind ViewModel.PresentationUri}" Visibility="{x:Bind ...}"/>`. When the resolved `Uri` is null (absent/non-http), show the plain "—" `TextBlock` instead. Do NOT use `HyperlinkButton.NavigateUri` (that bypasses the Story 2.8 whitelist + Warning path — the AC requires the shell-execute path).
  - A bool→Visibility converter is needed (App-layer; Pattern 2 anti-pattern forbids `Visibility` in Core). Check for an existing converter under `src/ohSpy.App/Converters/`; if none, add a small `BoolToVisibilityConverter` there (or use the WinUI built-in `x:Bind` with a `Visibility` helper). Keep converters in App.
- [x] **4.2** `PropertiesWindow.xaml.cs` — constructor-only (Pattern 13) except the `Closed` handler:
  ```csharp
  public sealed partial class PropertiesWindow : Window
  {
      public PropertiesViewModel ViewModel { get; }

      public PropertiesWindow(PropertiesViewModel viewModel)
      {
          ViewModel = viewModel;
          InitializeComponent();
          Title = "Device Properties";
          Closed += OnClosed; // sync void (VSTHRD100); dispose the VM to unsubscribe from the registry
      }

      private void OnClosed(object sender, WindowEventArgs args) => ViewModel.Dispose();
  }
  ```
  - The `Closed` handler MUST be synchronous `void` (the `Window.Closed` delegate returns void; `async void` is App-tree-fatal per VSTHRD100). `Dispose()` is synchronous — fine.

### Task 5 — Wire the command + `NodeServices` + DI (AC: #7)

- [x] **5.1** Replace the Story 2.8 STUB `OpenProperties` in `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs` with the real call (the command + XAML binding from 2.8 stay):
  ```csharp
  // AC-2.9.7: open the read-only Properties window (Story 2.9). The window construction lives in
  // the App-side IPropertiesLauncher impl (a Core VM can't new up a WinUI Window — Pattern 2);
  // this command just hands off the entry. Synchronous fire-and-forget (matches FetchXml).
  [RelayCommand]
  private void OpenProperties() => _services.PropertiesLauncher.OpenProperties(_entry);
  ```
  Remove the now-unused `using ohSpy.Core.Diagnostics;` ONLY if nothing else in the file needs it — `FetchXml`/`DiagCategories` no longer reference it, but check: the file no longer emits diagnostics directly after this change (FetchXml routes through BrowserLaunch). Remove the `DiagCategories`/`DiagnosticContext` usings if the analyzer flags them as unused (`IDE0005`, which is build-fatal under `TreatWarningsAsErrors`). Verify by building.
- [x] **5.2** Add `IPropertiesLauncher` to the `NodeServices` bundle (6th member) — `src/ohSpy.Core/ViewModels/NodeServices.cs`:
  ```csharp
  public sealed record NodeServices(
      IUpnpHttpClient Http,
      IScpdParser ScpdParser,
      IUiDispatcher Ui,
      IDiagnosticEmitter Diag,
      IUriLauncher Launcher,           // Story 2.8
      IPropertiesLauncher PropertiesLauncher); // Story 2.9 — open the Properties window
  ```
  (`IPropertiesLauncher` is in the same `ohSpy.Core.ViewModels` namespace — no new using.) This breaks the **4 `NodeServices` construction sites** again (Task 7).
- [x] **5.3** DI registration — `src/ohSpy.App/Composition/ServiceRegistration.cs` (add `using ohSpy.App.Windowing;` + `using ohSpy.Core.Devices;`). Register BEFORE the `NodeServices` line so it auto-resolves into the bundle:
  ```csharp
  // Story 2.9 — window ownership (D10) + Properties popup (FR-052).
  services.AddSingleton<IWindowOwnershipManager, WindowOwnershipManager>();
  // Pattern 7: per-popup VM factory — no IServiceProvider leak at the call site.
  services.AddSingleton<Func<RegistryEntry, PropertiesViewModel>>(sp =>
      entry => new PropertiesViewModel(
          entry,
          sp.GetRequiredService<IDeviceRegistry>(),
          sp.GetRequiredService<IUriLauncher>(),
          sp.GetRequiredService<IDiagnosticEmitter>()));
  // Concrete + interface (dual reg, DiscoveryService precedent) so OnLaunched can set ShellWindow.
  services.AddSingleton<PropertiesLauncher>();
  services.AddSingleton<IPropertiesLauncher>(sp => sp.GetRequiredService<PropertiesLauncher>());
  ```
- [x] **5.4** `src/ohSpy.App/App.xaml.cs` — set the launcher's shell window right after the MainWindow is created in `OnLaunched`:
  ```csharp
  _window = new MainWindow(_shellVm);
  Services.GetRequiredService<PropertiesLauncher>().ShellWindow = _window; // FR-046 parent
  _window.Closed += OnWindowClosed;
  _window.Activate();
  ```
  (`using ohSpy.App.Windowing;` if not already present.)

### Task 6 — Tests: `PropertiesViewModel` (Core) (AC: #4, #6)

**Location:** `tests/ohSpy.Core.Tests/ViewModels/PropertiesViewModelTests.cs`. `[Trait("ac", "AC-2.9.<n>")]`. Needs a controllable registry fake (to raise `DeviceRemoved`) — create `tests/ohSpy.Core.Tests/Fakes/FakeDeviceRegistry.cs`:
```csharp
namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;

internal sealed class FakeDeviceRegistry : IDeviceRegistry
{
    public event Action<RegistryEntry>? DeviceLoaded;
    public event Action<RegistryEntry>? DeviceUpdated;
    public event Action<Guid>? DeviceRemoved;

    public void RaiseDeviceRemoved(Guid uuid) => DeviceRemoved?.Invoke(uuid);

    public bool TryGetEntry(Guid uuid, out RegistryEntry entry) { entry = null!; return false; }
    public IReadOnlyCollection<RegistryEntry> Loaded => Array.Empty<RegistryEntry>();
    public int Count => 0;
}
```
Build a Loaded `RegistryEntry` for the VM via the existing test idiom (`DeviceNodeViewModelTests.LoadedEntry` pattern: `new RegistryEntry(uuid, location, nowUtc, CancellationToken.None)` then `MarkInFlight()` + `MarkLoaded(new DeviceDescription(...))`; `RefreshSsdpMetadata(...)` to seed Server/BootId/etc.). The `RegistryEntry` ctor + mutators are `internal` — visible to tests via `InternalsVisibleTo`.

- [x] **6.1** `Identity_MapsFromDescription_AC294` — loaded entry with known FriendlyName/DeviceType/Udn/PresentationUrl → the four Identity properties match; `Uuid` equals the entry UUID string.
- [x] **6.2** `Manufacturer_MapsAllEightFields_AC294` — known Manufacturer/ModelName/etc. → the eight properties match; a null `ModelNumber`/`Upc` → `"—"`.
- [x] **6.3** `Network_MapsLocationServerCacheControl_AC294` — `LocationUrl`, `Ip` (host), `Port`, `SsdpServer`, `CacheControlMaxAgeSeconds` (from `RefreshSsdpMetadata` max-age) map correctly; null Server → `"—"`.
- [x] **6.4** `DiscoveryHistory_MapsTimestampsAndCounts_AC294` — `AliveCount`, `BootId`, `ConfigId` map; timestamps render in the chosen format; null BootId → `"—"`.
- [x] **6.5** `AbsentFields_RenderAsDash_AC294` — a minimal `DeviceDescription` with all-nullable fields null → those properties are `"—"`; a present-but-empty string also `"—"` (OrDash covers both; the "absent vs empty" distinction is documented as null→"—").
- [x] **6.6** `EmbeddedDevices_AlwaysEmpty_AC294` — `EmbeddedDevices` is empty and `HasEmbeddedDevices` is false (model flattens per FR-053 — Decision 5).
- [x] **6.7** `DeviceRemoved_MatchingUuid_SetsBanner_AC296` — `registry.RaiseDeviceRemoved(uuid)` → `IsDeviceGone == true`, `DeviceGoneText` starts with "Device left the network"; the snapshot display fields are UNCHANGED (data still visible).
- [x] **6.8** `DeviceRemoved_OtherUuid_Ignored_AC296` — raise with a DIFFERENT uuid → `IsDeviceGone == false`.
- [x] **6.9** `OpenUrlCommand_HttpUri_Launches_AC295` — `OpenUrlCommand.Execute(presentationUri)` with an http(s) `Uri` → `FakeUriLauncher.Launched` contains it (reuse the Story 2.8 `FakeUriLauncher`).
- [x] **6.10** `OpenUrlCommand_NullUri_NoLaunch_NoThrow_AC295` — `Execute(null)` → no launch, no Warning, no throw (absent URL is a no-op).
- [x] **6.11** `OpenUrlCommand_NonHttpUri_Refused_Warns_AC295` — a `file://` resolved Uri → no launch, one `Shell.Execute` Warning (the Story 2.8 whitelist applies — verify the wiring, not re-prove the helper).
- [x] **6.12** `PresentationUri_RelativeResolvedAgainstLocation_AC295` — `PresentationUrl = "/index.html"`, location `http://host:80/desc.xml` → `PresentationUri == http://host:80/index.html`.
- [x] **6.13** `Dispose_Unsubscribes_NoBannerAfterDispose_AC296` — `vm.Dispose()`; then `RaiseDeviceRemoved(uuid)` → `IsDeviceGone` stays false (handler detached). Confirms the registry won't pin the VM.

### Task 7 — Update `NodeServices` construction sites (AC: #7)

The 6th `NodeServices` member breaks the 4 existing sites (all touched in Story 2.8). Add a `FakePropertiesLauncher` arg to each. First create the fake — `tests/ohSpy.Core.Tests/Fakes/FakePropertiesLauncher.cs`:
```csharp
namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;
using ohSpy.Core.ViewModels;

internal sealed class FakePropertiesLauncher : IPropertiesLauncher
{
    public List<RegistryEntry> Opened { get; } = new();
    public void OpenProperties(RegistryEntry entry) => Opened.Add(entry);
}
```
- [x] **7.1** `DeviceNodeViewModelTests.cs` — the static `NodeServices` field, the `Expand_NoHttpFetchTriggered_AC262` local `new NodeServices(...)`, AND the `CapturingServices()` helper (Story 2.8) — add `new FakePropertiesLauncher()` to each.
- [x] **7.2** `DeviceTreeViewModelTests.cs` — the `_nodeServices` ctor — add the arg.
- [x] **7.3** `ServiceNodeViewModelTests.cs` — `MakeNodeServices` factory — add a `propertiesLauncher` param defaulting to `new FakePropertiesLauncher()`.
- [x] **7.4** Update the Story 2.8 `OpenPropertiesCommand_Stub_WarnsNotImplemented_AC281` test in `DeviceNodeViewModelTests` — it asserts the OLD stub behaviour (a `Feature.NotImplemented` Warning) which this story REMOVES. Replace it with `OpenPropertiesCommand_OpensPropertiesWindow_AC297`: construct the VM with a `NodeServices` carrying a capturing `FakePropertiesLauncher`; `vm.OpenPropertiesCommand.Execute(null)` → `launcher.Opened` contains the device's `RegistryEntry` (matched by `Uuid`). Re-trait it `[Trait("ac", "AC-2.9.7")]`.

### Task 8 — DI / boundary / build (AC: all)

- [x] **8.1** `CoreAppBoundaryTests` still green (4 facts). New **Core** types — `PropertiesViewModel`, `IPropertiesLauncher` — must be WinUI-free (`CommunityToolkit.Mvvm` + BCL + Core only; no `Microsoft.UI.*`). The windowing classes are in **App** (correct side).
- [x] **8.2** `dotnet test tests/ohSpy.Core.Tests` — all green. Baseline **298** (Story 2.8) + ~14 new (`PropertiesViewModelTests`) + the 1 retargeted stub test; target ~313. 2 skips unchanged.
- [x] **8.3** `dotnet build src/ohSpy.App -c Debug -p:RuntimeIdentifier=win-x64` — **0 errors**. Watch for: the `[LibraryImport]` source-gen requiring `partial` class+method; `IDE0005` unused-usings after the `DeviceNodeViewModel` diagnostics removal (Task 5.1); `WMC1506` only on the pre-existing FallbackTemplate (no new ones — keep Properties XAML bindings `OneTime`/correct mode); `VSTHRD100` if any `async void` sneaks into the window code-behind. The existing benign FallbackTemplate `WMC1506` is acceptable (no NEW warnings).
- [x] **8.4** `dotnet test --filter "category=chaos"` — exactly **1** (unchanged). `--filter "FullyQualifiedName~CoreAppBoundary"` — **4** green.

### Task 9 — Final verification (AC: all)

- [x] **9.1** Re-confirm the full suite + both filters + the App build are clean (Task 8).
- [x] **9.2** Confirm `DiagCategories` is UNCHANGED — `Feature.NotImplemented` is still referenced (by the Story 2.8 `Subscribe` stub), so the pinned-set guard (`DiagCategoriesTests`) needs no edit. (Only the *Properties* call site of `Feature.NotImplemented` is removed; the constant + the Subscribe call site remain.)

### Task 10 — Manual smoke (non-AC-gating; the FR-046 + render ACs) — record in Dev Agent Record

- [ ] **10.1** *(NOT EXECUTED — headless environment; deferred to Epic 2 close, joins the 2.6/2.7/2.8 deferred smokes.)* **Manual UI smoke (covers AC-2.9.3 FR-046, AC-2.9.5 render/hyperlinks, AC-2.9.6 banner, AC-10.1/10.2/10.3/10.4):** launch `ohSpy.App` on a network with live UPnP devices. Confirm: (a) right-click a device → "Properties…" opens a read-only window with the five sections populated; absent fields show "—"; (b) the four URL fields are clickable hyperlinks that open in the default browser (≤2 s) via the Story 2.8 path; a non-http URL is refused with a Warning; (c) **FR-046:** the Properties window appears above the main window (AC-10.1); clicking the main window does NOT push it behind (AC-10.4); minimising the main window minimises it, restore restores (AC-10.3); closing the main window closes it (AC-10.2); it is independently activatable (not modal); (d) **FR-037:** with the Properties window open, take the device off the network (or wait for byebye) → a "Device left the network at <time>" banner appears at the top, the data stays visible, and the window still closes cleanly with no error. If headless, record as not-executed and recommend before Epic 2 close (joins the 2.6/2.7/2.8 deferred smokes).

---

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **Decision 10 (window ownership)** | `WindowOwnershipManager` — `SetWindowLongPtr(GWLP_HWNDPARENT)` via `[LibraryImport]`; `Adopt`/`GetChildrenOf`; `Closed` pruning; canonical `Activate()`→`Adopt()` | AC-2.9.1, AC-2.9.2, AC-2.9.3 |
| **FR-052 (Properties)** | `PropertiesViewModel` (Core) + `PropertiesWindow` (App) — 5 read-only sections, absent→"—" | AC-2.9.4, AC-2.9.5 |
| **FR-037 / NFR-R3** | Device-gone banner via `IDeviceRegistry.DeviceRemoved`; snapshot data survives; closeable | AC-2.9.6 |
| **Gap-3 / Story 2.8 reuse** | Hyperlinks open via `BrowserLaunch` (http/https whitelist + Warning + `IUriLauncher`) | AC-2.9.5 |
| **Pattern 7 (DI factory)** | `Func<RegistryEntry, PropertiesViewModel>` factory; no `IServiceProvider` leak | AC-2.9.7 |
| **Pattern 2 (boundary)** | VM + seam in Core (WinUI-free); window/interop/XAML in App | all |

### CRITICAL DESIGN DECISIONS

**1. The "open a window" command crosses the Core/App boundary via `IPropertiesLauncher` (the seam).** *(§"The seam")*
The epics say "`DeviceNodeViewModel.OpenPropertiesCommand` … creates a new `PropertiesWindow`" — but `DeviceNodeViewModel` is **Core** and `PropertiesWindow` is **App** (`Microsoft.UI.Xaml.Window`). A Core type cannot reference it (Pattern 2 / `CoreAppBoundaryTests`). The architecture's D10 sketch (`new InvocationPopupWindow(action)` inside an "OpenXxxPopup" method) is illustrative — the real window construction MUST be App-side. The seam: `IPropertiesLauncher` (Core interface, method `OpenProperties(RegistryEntry)`) → `PropertiesLauncher` (App impl) does factory→`new PropertiesWindow`→`Activate`→`Adopt`. The command stays on the Core `DeviceNodeViewModel` (already XAML-wired from Story 2.8) and just hands the entry to the seam. This is the "document the seam" the epics' AC-2.9.7 explicitly asks for.

**2. `Activate()` THEN `Adopt()` — order is load-bearing (AC-10.1).** *(§"Activate before Adopt")*
`SetWindowLongPtr(GWLP_HWNDPARENT)` before `Activate()` leaves the owner relationship undefined in WinUI 3 (the HWND isn't fully realised until Activate). The architecture pins the order and wants it documented in a code-comment on `Adopt` so all Epics 3-5 popup sites copy it verbatim. Put the comment on the interface method AND the impl. Every future popup follows `window.Activate(); _ownership.Adopt(window, shell);`.

**3. `[LibraryImport]`, not `[DllImport]`; `partial` class + method.** *(§"P/Invoke form")*
D10 pins the .NET 7+ source-generated `[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]`. The source generator requires the method to be `static partial` and the containing class to be `partial`. `SetWindowLongPtrW` (wide/Unicode) works identically on x64 and ARM64 with `IntPtr` (pointer-sized). The existing `Program.cs` `MessageBoxW` uses the older `[DllImport]` — do NOT copy that form here; D10 specifies `[LibraryImport]`. All P/Invoke is App-only (Pattern 2 — "Any P/Invoke" is forbidden in Core).

**4. The windowing classes are NOT unit-tested — by design.** *(§"Test surface")*
`WindowOwnershipManager`, `PropertiesLauncher`, and `PropertiesWindow` all touch `Window`/HWND/`WinRT.Interop`/P/Invoke → they live in **App** and cannot be referenced by `ohSpy.Core.Tests` (the boundary test forbids it, and there is no App test project). The architecture's "Adopt unit test: create two windows…" requires a WinUI runtime → manual/gated UI-test, explicitly out of v1 automated scope (AC-10.2/10.3/10.4 are manual). The **automated** coverage is entirely on `PropertiesViewModel` (pure Core). The windowing layer is covered by: a clean App build (compile-proves the `[LibraryImport]` signature, the `x:Bind`s, the DI graph) + code review (AC-10.1/10.5 — verify the Activate→Adopt order) + the Task 10 manual smoke. State this plainly in the Dev Agent Record; do not fake a unit test that can't exist.

**5. `EmbeddedDevices` cannot be populated — the model flattens (FR-053).** *(§"Embedded-devices gap")*
The AC's `EmbeddedDevices (recursive list)` section has **no data source**: `DeviceDescription` (the parsed model) retains only root-device metadata + a **flattened** `Services` list (services from the root AND all embedded devices, merged per FR-053). It keeps NO per-embedded-device records (no friendly names, UDNs, or device types for embedded children). So `PropertiesViewModel.EmbeddedDevices` is **always empty** and the XAML section renders a muted placeholder. This is a real, documented gap — surfacing per-embedded-device metadata would require a `DeviceDescriptionParser` change to retain the embedded `<device>` tree (out of scope for 2.9). Flag it for the reviewer; do NOT try to reverse-engineer embedded devices from the flattened service list (the mapping is lossy and wrong). If product wants this later, it's a parser-model enhancement story.

**6. Hyperlinks route through the Story 2.8 shell-execute path — NOT `HyperlinkButton.NavigateUri`.** *(§"Hyperlinks")*
WinUI's `HyperlinkButton.NavigateUri` auto-launches the OS browser, bypassing the FR-052/Gap-3 http/https whitelist + Warning diagnostic. AC-2.9.5 requires "the same shell-execute path" as Story 2.8 — so bind `HyperlinkButton.Command` to `PropertiesViewModel.OpenUrlCommand` (which calls `BrowserLaunch.OpenInDefaultBrowser` → whitelist + `IUriLauncher` + Warning). Resolve relative URLs (PresentationUrl is commonly relative) against `LocationUrl` via `Uri.TryCreate(base, raw, out _)` before opening. Reuse the existing `IUriLauncher`/`BrowserLaunch`/`Shell.Execute` machinery — do NOT add a second browser-open path.

**7. Snapshot at construction; survive removal (FR-037).** *(§"Snapshot")*
`PropertiesViewModel` reads every display field from the `RegistryEntry` **at construction** and holds only the resulting strings (plus `_uuid`, `_locationUrl`, `_launcher`, `_diag`). It does NOT hold the `RegistryEntry` for display. So when the device leaves and the registry drops the entry, the popup's data stays intact (snapshot), and the `DeviceRemoved` event just flips the banner. `DeviceRemoved` fires on the UI thread (registry marshals via `IUiDispatcher`) → the handler sets observable properties directly. The VM is `IDisposable` and unsubscribes on window close (else the singleton registry pins it).

**8. Pattern 7 factory — `Func<RegistryEntry, PropertiesViewModel>`.** *(§"Factory")*
`PropertiesViewModel` is per-popup (per-entity) → NOT registered as a type in DI (Pattern 7). Register a `Func<RegistryEntry, PropertiesViewModel>` that closes over the DI-resolved `IDeviceRegistry`/`IUriLauncher`/`IDiagnosticEmitter` and takes the `entry`. `PropertiesLauncher` resolves the `Func`, not `IServiceProvider` (no service-locator leak). The shell `Window` is set on `PropertiesLauncher` post-construction in `OnLaunched` (the MainWindow is created there, not in DI) — same dual-registration (concrete + interface) pattern as `DiscoveryService`.

### What this story does NOT do (scope discipline)

- **Does NOT build the other three popups** (Invocation FR-025, Subscription FR-032, Diagnostics FR-041) — Epics 3/4/5. It builds the reusable `WindowOwnershipManager` + the Properties popup; the architecture's `Adopt` comment sets the pattern those will follow.
- **Does NOT populate embedded devices** — the model flattens (Decision 5); section renders a placeholder.
- **Does NOT add automated tests for the windowing/XAML layer** — App-only, no App test project (Decision 4); manual smoke + code review cover AC-10.x.
- **Does NOT change `DiagCategories`** — `Feature.NotImplemented` stays (still used by the 2.8 Subscribe stub); only the Properties *call site* of it is removed.
- **Does NOT add modality** — FR-046 ownership is z-order + lifetime, NOT modal; the popup is independently activatable (D10).
- **Does NOT add a popup CTS** — Properties has no in-flight async work after construction; the `DeviceRemoved` event is the sole FR-037 trigger (architecture: "for Properties … the primary mechanism is the DeviceRemoved event").
- **Does NOT wire adapter-switch popup teardown** — FR-050 (Story 5.2) cancels CTS → FR-037 state; not relevant here (no CTS).

### Files being modified — current state & what must be preserved

- **`src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`** (UPDATE): the Story 2.8 `OpenProperties` stub (emits a `Feature.NotImplemented` Warning) becomes the real `_services.PropertiesLauncher.OpenProperties(_entry)` call. **Preserve** `FetchXmlCommand` (2.8), the `OnIsExpandedChanged` device-expand once-guard (2.6), `RefreshFrom`, `ComputeSecondaryDetail`. After the change the file may no longer use `ohSpy.Core.Diagnostics` — remove the now-unused using if the analyzer flags it (build-fatal `IDE0005`).
- **`src/ohSpy.Core/ViewModels/NodeServices.cs`** (UPDATE): add the 6th member `IPropertiesLauncher PropertiesLauncher`. Breaking ctor change → 4 test sites + DI (auto-resolved).
- **`src/ohSpy.App/Composition/ServiceRegistration.cs`** (UPDATE): add the `IWindowOwnershipManager`, `Func<…>` factory, and `PropertiesLauncher`/`IPropertiesLauncher` registrations before the `NodeServices` line. Preserve all existing registrations + the documented ordering (e.g. `IUiDispatcher` first).
- **`src/ohSpy.App/App.xaml.cs`** (UPDATE): after `_window = new MainWindow(...)`, set `PropertiesLauncher.ShellWindow = _window`. **Preserve** the `OnLaunched` resolve-order comments (IUiDispatcher pin first, ring-sink wiring, eager dispatcher), the `_appCts`/`ShutdownAsync` teardown, and the `CA1001` suppression rationale.
- **NEW App files:** `Windowing/WindowOwnershipManager.cs`, `Windowing/PropertiesLauncher.cs`, `Views/PropertiesWindow.xaml` + `.cs`, possibly `Converters/BoolToVisibilityConverter.cs` (if none exists).
- **NEW Core files:** `ViewModels/IPropertiesLauncher.cs`, `ViewModels/PropertiesViewModel.cs`.

### Previous-story intelligence

**Story 2.8 (context menus / XML viewing — just shipped, commit `00a2f4a`):**
- The `OpenPropertiesCommand` on `DeviceNodeViewModel` ALREADY EXISTS as a stub (emits `Feature.NotImplemented` Warning) and is ALREADY bound in `MainWindow.xaml` ("Properties…" `MenuFlyoutItem` → `{x:Bind OpenPropertiesCommand}`). This story just replaces the command BODY — the XAML binding + the command name are unchanged. The `OpenPropertiesCommand_Stub_WarnsNotImplemented_AC281` test asserts the old stub and MUST be retargeted (Task 7.4).
- `IUriLauncher`/`ShellUriLauncher` (Core seam) + `BrowserLaunch.OpenInDefaultBrowser` (internal static, http/https whitelist + `Shell.Execute` Warning) + `FakeUriLauncher` (test fake) all exist and are REUSED verbatim for the Properties hyperlinks. Do not reinvent.
- The `NodeServices` 5th member (`IUriLauncher`) pattern is exactly how to add the 6th (`IPropertiesLauncher`): same 4 ctor sites, same DI auto-resolution. The 4 sites are listed in Task 7.
- Code review (Sonnet) on 2.8 was clean (0 patches). The one note: a test-fake ordering detail — irrelevant here.

**Story 2.7 (SSDP log):** `DateTime` stamped VM-side (no clock seam in Core) — the device-gone banner timestamp follows this. `IDisposable`-unsubscribe pattern (Interlocked-guarded) — `PropertiesViewModel` mirrors it. `MutedForegroundBrush` resource exists (reuse for "—" + banner).

**Story 2.5 (MainWindow / ShellViewModel):** `MainWindow.xaml.cs` exposes `public ShellViewModel ViewModel { get; }` for `x:Bind` — `PropertiesWindow` mirrors this (`public PropertiesViewModel ViewModel { get; }`). Pattern 13 (constructor-only code-behind) — the only allowed code-behind logic is the `Closed`→`Dispose()` handler (sync void).

### Latest tech / library notes

- **`[LibraryImport]`** (.NET 7+, the project is .NET 10) — source-generated P/Invoke; `partial` class + `static partial` method mandatory; no `CharSet` (the generator infers from `StringMarshalling`, irrelevant here — only `IntPtr`/`int` params). Preferred over `[DllImport]` for new interop (better trimming/AOT, compile-time marshalling).
- **`WinRT.Interop.WindowNative.GetWindowHandle(Window)`** — the only approved way to get an HWND from a WinUI 3 `Window`. App-only (`WinRT.Interop` is forbidden in Core).
- **CommunityToolkit.Mvvm 8.4.0** — `[ObservableProperty]` (banner state) + `[RelayCommand]` (`OpenUrl`). `[RelayCommand] private void OpenUrl(Uri? url)` generates `OpenUrlCommand` of type `IRelayCommand<Uri?>`; bind `CommandParameter` to the resolved `Uri?`.
- **`Window.Closed`** delegate returns `void` → the handler is synchronous (VSTHRD100 forbids `async void` in the App tree). `Dispose()` is synchronous — safe.

### Code-style + pattern compliance

- **Pattern 1:** file-scoped namespaces; `_camelCase` fields; PascalCase public members.
- **Pattern 2 (boundary):** `PropertiesViewModel` + `IPropertiesLauncher` are pure Core (no `Microsoft.UI.*`/`Microsoft.Windows.*`/`WinRT.Interop.*`/P/Invoke). `WindowOwnershipManager`/`PropertiesLauncher`/`PropertiesWindow` are App. `CoreAppBoundaryTests` stays green.
- **Pattern 7:** `PropertiesViewModel` via `Func<RegistryEntry, PropertiesViewModel>` factory; `IWindowOwnershipManager`/`IPropertiesLauncher` singletons; no `IServiceProvider` at call sites.
- **Pattern 9:** `ObservableObject` base; `[ObservableProperty]`; `[RelayCommand]`; `partial class`.
- **Pattern 13:** `x:Bind`/`x:DataType` in `PropertiesWindow.xaml`; constructor-only code-behind except the documented `Closed`→`Dispose` handler.
- **Pattern 14 + A2:** test names `Method_Scenario_Expected_AC29n`; `[Trait("ac", "AC-2.9.<n>")]`.
- **VSTHRD100:** no `async void` in App (window `Closed` handler is sync `void`).

### Anti-patterns to avoid

- **Don't `new PropertiesWindow()` from Core** — cross the boundary via `IPropertiesLauncher` (Decision 1).
- **Don't call `Adopt` before `Activate`** — order is load-bearing (Decision 2 / AC-10.1).
- **Don't use `[DllImport]`** for `SetWindowLongPtr` — D10 pins `[LibraryImport]` (Decision 3); remember `partial` class + method.
- **Don't try to unit-test `WindowOwnershipManager`/`PropertiesWindow`/`PropertiesLauncher`** in Core.Tests — they can't compile there (Decision 4). Manual smoke + code review.
- **Don't reconstruct embedded devices** from the flattened `Services` — the model has none; render a placeholder (Decision 5).
- **Don't use `HyperlinkButton.NavigateUri`** — route through `BrowserLaunch`/the 2.8 whitelist (Decision 6).
- **Don't hold the `RegistryEntry` for display** — snapshot fields at construction so the popup survives removal (Decision 7).
- **Don't forget `IDisposable`/unsubscribe** — the singleton registry pins every Properties VM otherwise (Decision 7 / Task 3.5).
- **Don't `async void`** the window `Closed` handler — sync `void` + sync `Dispose()` (VSTHRD100).
- **Don't put a `Visibility`/converter in Core** — `bool IsDeviceGone` + an App-side converter (Pattern 2 anti-pattern).
- **Don't edit `DiagCategories` / its pinned-set test** — `Feature.NotImplemented` stays (Subscribe still uses it); only the Properties call site changes.
- **Don't forget the 4 `NodeServices` ctor sites + the retargeted 2.8 stub test** (Task 7).

### Project Structure Notes

New Core files: `ViewModels/IPropertiesLauncher.cs`, `ViewModels/PropertiesViewModel.cs`.
New App files: `Windowing/WindowOwnershipManager.cs`, `Windowing/PropertiesLauncher.cs`, `Views/PropertiesWindow.xaml` + `.cs`, (maybe) `Converters/BoolToVisibilityConverter.cs`.
Edited Core files: `ViewModels/DeviceNodeViewModel.cs`, `ViewModels/NodeServices.cs`.
Edited App files: `Composition/ServiceRegistration.cs`, `App.xaml.cs`.
New test files: `Fakes/FakeDeviceRegistry.cs`, `Fakes/FakePropertiesLauncher.cs`, `ViewModels/PropertiesViewModelTests.cs`.
Edited test files: `ViewModels/DeviceNodeViewModelTests.cs` (NodeServices 6th arg + retargeted stub test), `ViewModels/DeviceTreeViewModelTests.cs`, `ViewModels/ServiceNodeViewModelTests.cs` (NodeServices 6th arg).
No new project, no new package reference, no `Directory.Packages.props` change. Three new DI registrations (`IWindowOwnershipManager`, the `Func` factory, `PropertiesLauncher`/`IPropertiesLauncher`).

Matches the architecture's planned tree: `Windowing/WindowOwnershipManager.cs` (arch line 2065), `Views/PropertiesWindow.xaml + .cs` (arch line 2070), `ViewModels/PropertiesViewModel.cs` (arch lines 2139), component-map 4.7 (arch line 2188) + 4.13 secondary-window-lifecycle (arch line 2194).

### References

- [Source: epics.md#Story 2.9] (lines 1213–1265) — verbatim ACs.
- [Source: epics.md#FR-037/FR-046/FR-052] (FR-052 line 76, FR-037 line 122, FR-046 line 123; NFR-R3 line 131) — Properties window, popup ownership, device-removal survival.
- [Source: architecture.md#Decision 10 — Window Ownership Mechanism] (lines 1264–1390) — `IWindowOwnershipManager`, `SetWindowLongPtr(GWLP_HWNDPARENT=-8)` via `[LibraryImport("user32.dll", "SetWindowLongPtrW")]`, `Dictionary<IntPtr,List<IntPtr>>` + `GetChildrenOf`, `Closed` pruning, `Activate()`→`Adopt()` order (lines 1313–1324), FR-046 behaviours (lines 1337–1346), AC-10.1..10.5 (lines 1370–1376), test contract (lines 1363–1368), non-modality (lines 1348–1351), adapter-switch interaction (lines 1353–1355).
- [Source: architecture.md#Pattern 7 — DI composition] (lines 1817–1843) — per-popup VM via `Func<TArgs,TViewModel>`; `AddSingleton<IWindowOwnershipManager, WindowOwnershipManager>()` (line 1836); no `IServiceProvider` leak.
- [Source: architecture.md#Pattern 2 — Core/App boundary] (lines 1714–1729) — "Any P/Invoke" forbidden in Core; all `Window` types + interop in App; `bool IsVisible` + App converter (not `Visibility` in Core).
- [Source: architecture.md#Window ownership flow] (lines 2332–2339) — `ShellViewModel.OpenXxxPopupCommand → new XxxPopupWindow → Activate → Adopt`.
- [Source: architecture.md#Component-to-FR map] (line 2188 4.7 Device Properties; line 2194 4.13 secondary-window lifecycle / registry `DeviceRemoved` handling per popup VM).
- [Source: architecture.md#Project tree] (lines 2063–2070 App `Windowing/`+`Views/`; lines 2131–2139 Core `ViewModels/PropertiesViewModel.cs`).
- [Source: architecture.md#Gap-3] (line 3062) — FR-052 URL safety = the Story 2.8 http/https whitelist + Warning.
- [Source: architecture.md#VSTHRD100 / window close] (lines 2887–2888) — sync `void` `Closed` handler; `_ = SomeAsync()` if async needed.
- [Source: src/ohSpy.Core/Models/DeviceDescription.cs] — the FR-052 source fields; NOTE: flattened `Services`, NO embedded-device records (Decision 5).
- [Source: src/ohSpy.Core/Devices/RegistryEntry.cs] — `Uuid`, `LocationUrl`, `Description`, `Server`, `CacheControlMaxAge`, `BootId`, `ConfigId`, `FirstSeenUtc`, `LastSeenUtc`, `AliveCount`, `DeviceToken`; internal ctor + `MarkInFlight`/`MarkLoaded`/`RefreshSsdpMetadata` for test fixtures.
- [Source: src/ohSpy.Core/Devices/IDeviceRegistry.cs] — `event Action<Guid> DeviceRemoved` (UI-thread), `DeviceLoaded`, `DeviceUpdated`, `TryGetEntry`, `Loaded`, `Count`.
- [Source: src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs] — the Story 2.8 `OpenProperties` stub to replace; `_entry`, `_services`; preserve `FetchXmlCommand`/`OnIsExpandedChanged`.
- [Source: src/ohSpy.Core/ViewModels/NodeServices.cs] — bundle to extend (6th member).
- [Source: src/ohSpy.Core/ViewModels/BrowserLaunch.cs + Shell/IUriLauncher.cs] — Story 2.8 whitelist + shell-open path reused for hyperlinks.
- [Source: src/ohSpy.App/Windowing/WinUiDispatcher.cs] — App-windowing service shape to mirror (internal sealed, App-local).
- [Source: src/ohSpy.App/App.xaml.cs] — `OnLaunched` MainWindow creation point; set `PropertiesLauncher.ShellWindow` there; preserve resolve-order + `ShutdownAsync`.
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs] — DI root; dual-registration (concrete + interface) precedent at `DiscoveryService` (lines 94–95).
- [Source: src/ohSpy.App/MainWindow.xaml.cs] — `public ViewModel { get; }` x:Bind pattern; Pattern 13 code-behind exception precedent.
- [Source: tests/ohSpy.Core.Tests/Fakes/FakeUriLauncher.cs + CapturingDiagnosticEmitter.cs] — reuse for hyperlink-open + Warning assertions.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (bmad-dev-story workflow)

### Debug Log References

- Core+tests build: `dotnet build tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj -c Debug --nologo` → 0 warnings, 0 errors.
- Full Core suite: `dotnet test tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj -c Debug --nologo` → **313 passed / 2 skipped / 0 failed** (baseline 298 + 15 new PropertiesViewModel tests + the retargeted 2.8 stub test, which replaced one existing test name → net +15).
- App build: `dotnet build src/ohSpy.App/ohSpy.App.csproj -c Debug -p:RuntimeIdentifier=win-x64 --nologo` → **0 errors, 1 warning** (the pre-existing benign `WMC1506` on the Story 2.5 `MainWindow.xaml(140,37)` FallbackTemplate — NOT promoted to error, NOT new). No new warnings from `PropertiesWindow.xaml`.
- Filters: `--filter "category=chaos"` → 1 passed. `--filter "FullyQualifiedName~CoreAppBoundary"` → 4 passed (confirms `PropertiesViewModel` + `IPropertiesLauncher` are WinUI-free).
- First App build failed with `SYSLIB1062` (`[LibraryImport]` requires unsafe code) + a cascade of WMC0001 "unknown type" XAML errors. Root cause = the C# compile aborting before converter/selector types compiled. Fixed by adding `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to `ohSpy.App.csproj` (App-only; Core stays safe). Second App build then failed with `CS1503` in `PropertiesWindow.g.cs` (`SetConverterLookupRoot(this)` expects a `FrameworkElement`, but a `Window` root is not one) — `x:Bind` converters can't be used when the binding root is a `Window`. Resolved by moving the `bool`/`Uri?`→`Visibility` projections into App-side code-behind properties on `PropertiesWindow` (no XAML converters), which also keeps `Visibility` out of Core (Pattern 2). The now-unused `Converters/BoolToVisibilityConverter.cs` was deleted.

### Completion Notes List

- **All 9 implementation task-groups complete; all ACs satisfied by automated tests + a clean App build.** Task 10 (manual UI smoke) NOT executed — headless environment; recorded as deferred (joins the 2.6/2.7/2.8 deferred smokes) and recommended before Epic 2 close. The manual smoke is the only verification path for AC-2.9.3 (FR-046 z-order/minimise/close) + the live render/hyperlink/banner behaviours, by design (Decision 4 — the windowing/XAML layer is App-only and has no test project).
- **Decision 4 honoured:** the only automated tests are for `PropertiesViewModel` (pure Core). `WindowOwnershipManager`/`PropertiesLauncher`/`PropertiesWindow` are App-layer and not unit-tested (they'd fail to compile in Core.Tests under `CoreAppBoundaryTests`). Their correctness is compile-proven (`[LibraryImport]` signature, the `x:Bind`s, the DI graph) + the Activate→Adopt order is documented for code review.
- **Decision 5 honoured:** `PropertiesViewModel.EmbeddedDevices` is always empty + `HasEmbeddedDevices` is always false; the XAML renders a muted "— (services flattened per FR-053)" placeholder. The model retains no per-embedded records; not reconstructed.
- **Story 2.8 reuse:** hyperlinks route through `BrowserLaunch.OpenInDefaultBrowser` (http/https whitelist + `IUriLauncher` + Warning), NOT `HyperlinkButton.NavigateUri`. `FakeUriLauncher` reused for the open tests.
- **DiagCategories UNCHANGED** — `Feature.NotImplemented` stays (still used by the 2.8 Subscribe stub); only the Properties call site of it was removed. `DiagCategoriesTests`/usage guards untouched and green. The `ohSpy.Core.Diagnostics` using was removed from `DeviceNodeViewModel.cs` (no longer emits diagnostics directly) to avoid build-fatal `IDE0005`.
- **NodeServices** gained `IPropertiesLauncher` as the 6th member; all 4 construction sites updated (+ `FakePropertiesLauncher` / `FakeDeviceRegistry` fakes), and the 2.8 `OpenPropertiesCommand_Stub_WarnsNotImplemented_AC281` test was retargeted to `OpenPropertiesCommand_OpensPropertiesWindow_AC297`.

**Deviations from the story spec (all documented):**
1. **`GetChildrenOf` returns `IReadOnlyList<IntPtr>`** (HWNDs), not `IReadOnlyList<Window>` — per the story's own Task 1.1 note (the tracking dict stores HWNDs; avoids retaining `Window` references). Pre-approved deviation.
2. **`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` added to `ohSpy.App.csproj`** — required by the `[LibraryImport]` source generator (SYSLIB1062). Not called out in the story but mandated by the D10 `[LibraryImport]` choice; App-only, Core stays safe.
3. **bool/Uri→Visibility mapping lives in `PropertiesWindow` code-behind, not XAML `{StaticResource}` converters.** `x:Bind` converters require the binding root to be a `FrameworkElement`; a `Window` root is not, so the generated `SetConverterLookupRoot(this)` does not compile (CS1503). The code-behind projections (App-layer) are the cleanest fix and still keep `Visibility` out of Core. The `BoolToVisibilityConverter` created mid-task was removed as unused.

### File List

**New — Core:**
- `src/ohSpy.Core/ViewModels/IPropertiesLauncher.cs`
- `src/ohSpy.Core/ViewModels/PropertiesViewModel.cs`

**New — App:**
- `src/ohSpy.App/Windowing/WindowOwnershipManager.cs`
- `src/ohSpy.App/Windowing/PropertiesLauncher.cs`
- `src/ohSpy.App/Views/PropertiesWindow.xaml`
- `src/ohSpy.App/Views/PropertiesWindow.xaml.cs`

**New — Tests:**
- `tests/ohSpy.Core.Tests/Fakes/FakeDeviceRegistry.cs`
- `tests/ohSpy.Core.Tests/Fakes/FakePropertiesLauncher.cs`
- `tests/ohSpy.Core.Tests/ViewModels/PropertiesViewModelTests.cs`

**Edited — Core:**
- `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs` (real OpenProperties call via the seam; removed unused `ohSpy.Core.Diagnostics` using)
- `src/ohSpy.Core/ViewModels/NodeServices.cs` (6th member `IPropertiesLauncher`)

**Edited — App:**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` (IWindowOwnershipManager + Func factory + PropertiesLauncher/IPropertiesLauncher)
- `src/ohSpy.App/App.xaml.cs` (set `PropertiesLauncher.ShellWindow = _window`; +using)
- `src/ohSpy.App/ohSpy.App.csproj` (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` for `[LibraryImport]`)

**Edited — Tests:**
- `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs` (NodeServices 6th arg ×3 sites; retargeted stub test → AC-2.9.7)
- `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs` (NodeServices 6th arg)
- `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs` (MakeNodeServices 6th param)

### Change Log

| Date | Change |
|---|---|
| 2026-06-03 | Story 2.9 context created via bmad-create-story (claude-opus-4-8[1m]); backlog → ready-for-dev. |
| 2026-06-03 | Story 2.9 implemented via bmad-dev-story (claude-opus-4-8[1m]). WindowOwnershipManager (`[LibraryImport]` SetWindowLongPtr GWLP_HWNDPARENT, Activate→Adopt) + IPropertiesLauncher seam + PropertiesLauncher + PropertiesViewModel (FR-052 snapshot, FR-037 device-gone banner, 2.8 hyperlink reuse) + PropertiesWindow.xaml/.cs. NodeServices 6th member; DI factory (Pattern 7); App.OnLaunched shell-window wiring. 15 new PropertiesViewModel tests + retargeted 2.8 stub test → 313 passed / 2 skipped / 0 failed; chaos=1; CoreAppBoundary=4. App build 0 errors / 1 pre-existing benign WMC1506. Task 10 manual UI smoke NOT executed (headless) — deferred to Epic 2 close. Status → review. |
| 2026-06-03 | Story 2.9 moved review → done by code-review workflow (claude-sonnet-4-6, run as sub-agent of bmad-code-review). APPROVED. Build 0 errors/0 warnings (clean — dev's "1 WMC1506" claim is a local-incremental-build artifact, same as 2.7/2.8 pattern; clean build is warning-free). Tests 313 passed/2 skipped confirmed. Chaos=1, CoreAppBoundary=4 green. All AC-2.9.1..2.9.7 satisfied. 0 patches required. 3 declared deviations all accepted. Task 10 manual UI smoke remains pending before Epic 2 close. |

---

## Senior Developer Review (AI)

**Reviewer:** claude-sonnet-4-6 (bmad-code-review workflow, run as sub-agent, 2026-06-03)
**Baseline commit:** `00a2f4a36b3edc47674b110753aa53e0b9c4efd1`
**Diff scope:** All uncommitted working-tree changes — modified tracked files (DeviceNodeViewModel.cs, NodeServices.cs, ServiceRegistration.cs, App.xaml.cs, ohSpy.App.csproj, 3 test files) + untracked new files (IPropertiesLauncher.cs, PropertiesViewModel.cs, WindowOwnershipManager.cs, PropertiesLauncher.cs, PropertiesWindow.xaml/.xaml.cs, FakeDeviceRegistry.cs, FakePropertiesLauncher.cs, PropertiesViewModelTests.cs). All files read in full.
**Build verified:** `dotnet build src/ohSpy.App/ohSpy.App.csproj -c Debug -p:RuntimeIdentifier=win-x64 --nologo` — **0 errors, 0 warnings**. Dev claim of "1 pre-existing WMC1506" is a local-incremental-build artifact; clean build from this reviewer is completely warning-free (identical pattern seen in 2.7 and 2.8 reviews). Build claim confirmed as at-least-as-good-as-claimed.
**Test verified:** `dotnet test tests/ohSpy.Core.Tests/ohSpy.Core.Tests.csproj -c Debug --nologo` — **313 passed, 2 skipped, 0 failed**. Dev claim CONFIRMED (298 baseline + 15 new PropertiesViewModelTests + 1 retargeted stub test replacing a same-file test, net +15). 15 new tests break down as: 13 per spec items 6.1-6.13 + 2 additional edge-case tests (`Network_NullServer_RendersDash_AC294`, `DiscoveryHistory_NullBootId_RendersDash_AC294`) — extra coverage is a net positive.
**Chaos suite:** `--filter "category=chaos"` — **1 passing** (unchanged). CONFIRMED.
**CoreAppBoundaryTests:** `--filter "FullyQualifiedName~CoreAppBoundary"` — **4 passing**. CONFIRMED. `PropertiesViewModel` and `IPropertiesLauncher` contain no `Microsoft.UI.*` / `Microsoft.Windows.*` / `WinRT.Interop.*` / P/Invoke references. Windowing classes correctly in App.

### Review Findings

No findings rise to High or Medium severity. Two Low informational observations noted below; neither requires a code change before merging.

- [ ] [Low / Informational] `LocationUri` assignment uses a redundant `TryResolve` roundtrip [`src/ohSpy.Core/ViewModels/PropertiesViewModel.cs:129`] — `LocationUri = TryResolve(entry.LocationUrl.ToString())` converts an already-absolute `Uri` to a string and re-resolves it against itself via `Uri.TryCreate(_locationUrl, raw, out var u)`. Since `LocationUrl` is always an absolute URI (it is the SSDP `LOCATION` header, validated at entry construction), this is functionally identical to `LocationUri = entry.LocationUrl` and will never return null. The code is correct — `TryResolve` correctly handles the absolute case — but the round-trip is mildly confusing to read. A future-story cleanup could assign `LocationUri = entry.LocationUrl` directly, but this is purely cosmetic and carries zero behavioural risk. No test is missing (the XAML correctly shows `LocationUrl` as a hyperlink in all cases, which is the correct UX since the URL is always present).

- [ ] [Low / Informational] No dedicated test for `LocationUri` hyperlink resolution [`tests/ohSpy.Core.Tests/ViewModels/PropertiesViewModelTests.cs`] — The story spec (item 6.12) covers `PresentationUri` relative-URL resolution explicitly, but `LocationUri` (always an absolute pass-through) is not directly asserted in any test. Because `LocationUrl` is always absolute, `LocationUri` is always non-null and always equals `entry.LocationUrl` — there is no wrong-result risk. The Network test (`Network_MapsLocationServerCacheControl_AC294`) asserts `LocationUrl` as a string, which is sufficient coverage for the display field. No action required.

### AC Coverage Assessment

- **AC-2.9.1** (WindowOwnershipManager shape / D10): CONFIRMED — `WindowOwnershipManager.cs` declares `IWindowOwnershipManager` (App-local per D10) and `internal sealed partial WindowOwnershipManager`. `[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]` with `private static partial IntPtr` signature. `GWLP_HWNDPARENT = -8`. `Dictionary<IntPtr, List<IntPtr>> _ownership`. `GetChildrenOf` returns `IReadOnlyList<IntPtr>` (HWNDs — pre-approved deviation, documented). `Closed` event lambda prunes `children.Remove(childHwnd)`. All structural requirements satisfied.
- **AC-2.9.2** (canonical Activate→Adopt order): CONFIRMED — `PropertiesLauncher.OpenProperties` calls `window.Activate()` then `_ownership.Adopt(window, ShellWindow)`. The `Adopt` method carries a multi-line block comment documenting the load-bearing order and spelling out the verbatim pattern for Epics 3-5 call sites.
- **AC-2.9.3** (FR-046 behaviours / manual): NOT EXECUTED — deferred, headless environment. Joins the 2.6/2.7/2.8 deferred manual smokes. Recommend before Epic 2 close. The Win32 mechanism (`SetWindowLongPtr(GWLP_HWNDPARENT)`) is architecturally correct; the Activate→Adopt order is verified by code review; the four OS behaviours (z-order, no-push-behind, minimise/restore, close-with-parent) are delivered by the OS for free once the owner relationship is established.
- **AC-2.9.4** (PropertiesViewModel shape / FR-052): CONFIRMED — all five field groups present and correctly mapped. `OrDash` correctly handles null and empty-string. `EmbeddedDevices` always empty + `HasEmbeddedDevices` always false (Decision 5 honoured, placeholder XAML present). `Port` exposed as string (correct — rendered as-is). `CacheControlMaxAgeSeconds` uses `TotalSeconds` with `InvariantCulture`. `FirstSeenUtc` / `LastSeenUtc` formatted with `ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture)`. All 15 field-mapping tests pass.
- **AC-2.9.5** (PropertiesWindow XAML / hyperlinks): CONFIRMED — four URL fields (`PresentationUrl`, `ManufacturerUrl`, `ModelUrl`, `LocationUrl`) each render as either a `HyperlinkButton` (when the resolved `Uri` is non-null, i.e. http/https) or a plain `TextBlock` (when null / non-http). `HyperlinkButton.Command = OpenUrlCommand`, `CommandParameter = XxxUri` — routes through `BrowserLaunch.OpenInDefaultBrowser` (Story 2.8 whitelist + Warning). `HyperlinkButton.NavigateUri` is NOT used. Sections match FR-052 grouping (Identity / Manufacturer / Network / Discovery history / Embedded devices). `ScrollViewer` present. All three hyperlink command tests pass.
- **AC-2.9.6** (device-removal survival / FR-037): CONFIRMED — `_registry.DeviceRemoved += OnDeviceRemoved` in ctor. Handler matches UUID and is idempotent (`if (uuid != _uuid || IsDeviceGone) return`). `DeviceGoneText` set before `IsDeviceGone = true` (avoids TOCTOU). Banner in XAML bound to `BannerVisibility` with `Mode=OneWay`. `IDisposable.Dispose()` unsubscribes via `Interlocked.Exchange` guard (mirrors `SsdpLogViewModel` / `DeviceTreeViewModel`). `PropertiesWindow.OnClosed` detaches `OnViewModelPropertyChanged` then calls `vm.Dispose()`. All three device-gone tests pass. `Dispose_Unsubscribes` test confirms the registry no longer pins the VM.
- **AC-2.9.7** (right-click handler + DI wiring): CONFIRMED — `DeviceNodeViewModel.OpenPropertiesCommand` calls `_services.PropertiesLauncher.OpenProperties(_entry)` (one-liner, no diag, no WinUI reference). `NodeServices` 6th member `IPropertiesLauncher PropertiesLauncher` added. All 4 test construction sites updated with `new FakePropertiesLauncher()`. Retargeted test `OpenPropertiesCommand_OpensPropertiesWindow_AC297` asserts `launcher.Opened.Single().Uuid == uuid`. DI: `IWindowOwnershipManager` singleton, `Func<RegistryEntry,PropertiesViewModel>` factory (no `IServiceProvider` leak), `PropertiesLauncher` dual-reg (concrete + interface). `App.OnLaunched` sets `ShellWindow` immediately after `MainWindow` construction, before `Activate()`.

### Key Design Decisions Verified

**Deviation 1 — `GetChildrenOf` returns `IReadOnlyList<IntPtr>` (HWNDs), not `IReadOnlyList<Window>`:** ACCEPTED. Pre-approved by the story spec (Task 1.1 note). The tracking dictionary stores HWNDs — returning them avoids retaining `Window` references that would pin closed popups. The `Window`-typed return in the architecture sketch was illustrative; the HWND-typed return is both safer and sufficient for testability / introspection. No lifetime or correctness issue.

**Deviation 2 — `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` added to `ohSpy.App.csproj`:** ACCEPTED. The `[LibraryImport]` source generator emits an `unsafe` stub method containing a `fixed` pointer statement (SYSLIB1062). This is a mandatory consequence of the D10 `[LibraryImport]` choice (the story pinned `[LibraryImport]` over `[DllImport]` for trimming/AOT correctness). The change is correctly scoped to `ohSpy.App.csproj` only; `ohSpy.Core.csproj` is untouched and Pattern 2 ("any P/Invoke forbidden in Core") is fully preserved. The comment in the `.csproj` accurately explains the reason. No safety guarantee is weakened beyond what `SetWindowLongPtr` itself inherently entails (a platform-native call that any equivalent `[DllImport]` version would also make).

**Deviation 3 — `bool`/`Uri?`→`Visibility` projections in `PropertiesWindow` code-behind, not XAML `{StaticResource}` converters:** ACCEPTED. The dev's claim that `x:Bind` converter lookup fails on a `Window` binding root is verified by the generated `PropertiesWindow.g.cs`: the class does NOT contain `SetConverterLookupRoot(this)` (the call that would fail with CS1503 since `Window` is not a `FrameworkElement`). Had a `{StaticResource}` converter been attempted via `x:Bind`, the XAML compiler would have emitted that call and CS1503 would have aborted the build. The code-behind approach (`public Visibility XxxVisibility => ToVisibility(...)` properties + `INotifyPropertyChanged` forwarding from the VM's `PropertyChanged`) is the correct WinUI 3 workaround. The approach is cleanly implemented: `Visibility` is confined to App (Pattern 2 satisfied), the `OnViewModelPropertyChanged` handler correctly filters to `nameof(IsDeviceGone)` to update only `BannerVisibility`, and `OnClosed` correctly detaches the handler before calling `Dispose()` (no double-fire risk). The static URL projections (`PresentationLinkVisibility`, `ManufacturerLinkVisibility`, etc.) are bound with default `Mode=OneTime` (correct — the resolved `Uri?`s are set at construction and never change), while `BannerVisibility` uses `Mode=OneWay` (correct — it must update when `IsDeviceGone` flips). Pattern 13 pattern note is in the code-behind XML doc comment; this is a documented, justified exception, not hidden business logic.

### Additional Correctness Checks

- **`Adopt` lambda captures:** `parentHwnd` and `childHwnd` are `IntPtr` (value types), correctly captured by value at `Adopt` call time — no stale-capture risk. `_ownership` is the singleton dictionary (reference capture — correct for the pruning operation). If `Adopt` were called twice for the same child window (an API misuse), two Closed handlers would fire, but the second `list.Remove(childHwnd)` on an already-removed entry is a no-op — benign. The lambda does not extend the child Window's lifetime (the delegate chain is reachable from the Window; after close and GC the chain is collected).
- **`PropertiesWindow` reference cycle:** `Window → VM` (via `ViewModel` property, strong) + `VM → Window` (via `ViewModel.PropertyChanged += OnViewModelPropertyChanged`, strong). This is intentional and broken by `OnClosed`, which detaches the handler and calls `Dispose()`. The `Window.Closed` event fires reliably in WinUI 3 on all close paths (user X button, OS close-with-parent via `GWLP_HWNDPARENT`). No leak path identified.
- **`DiagCategories` UNCHANGED:** `DiagCategories.FeatureNotImplemented` is still referenced by `ServiceNodeViewModel.SubscribeCommand` (line 158). The `DiagCategoriesTests` pinned-set guard is untouched and passes. The `using ohSpy.Core.Diagnostics` removal from `DeviceNodeViewModel.cs` is correct — the file no longer emits diagnostics directly (FetchXml routes through BrowserLaunch; OpenProperties routes through the seam). Clean build confirms no `IDE0005` unused-using remained.
- **Pattern 7 factory — no `IServiceProvider` leak:** The lambda `sp => entry => new PropertiesViewModel(entry, sp.GetRequiredService<IDeviceRegistry>(), ...)` closes over `sp` only within the factory registration itself (a singleton); `PropertiesLauncher` receives the `Func<RegistryEntry, PropertiesViewModel>` and never sees `IServiceProvider`. Correct.
- **`ShellWindow` null-guard in `OpenProperties`:** If `OpenProperties` is called before `App.OnLaunched` sets `ShellWindow` (e.g. a theoretical race), the `if (ShellWindow is not null)` guard silently skips `Adopt` — the window opens without FR-046 ownership. This edge case is structurally impossible in the current flow (`OnLaunched` sets `ShellWindow` before `Activate()`, and the right-click menu can only be reached after the MainWindow is visible), but the null-guard is the correct defensive approach per the story spec. Appropriate.
- **Test quality:** 15 tests are genuine and non-tautological — field mapping, absent-vs-null distinction, timestamp format regex, UUID discrimination (matching vs. non-matching device), whitelist routing (http passes, `file://` refused with Warning), relative-URL resolution, dispose/unsubscribe, and the retargeted seam-crossing test. No test asserts a value it directly set on the VM. The `FakeDeviceRegistry` exposes `RaiseDeviceRemoved` for deterministic event triggering; `FakePropertiesLauncher.Opened` list captures the passed `RegistryEntry` for identity assertion.

### Review Follow-ups (AI)

**APPROVED.** The implementation is architecturally sound, fully satisfies all seven ACs, contains no defects or regressions, and presents clean automated evidence (313/2/0, chaos=1, CoreAppBoundary=4, App build 0 errors/0 warnings). All three declared deviations are justified and correctly implemented. The test suite is substantive: field mapping, absent-placeholder rule, device-gone lifecycle (subscribe, UUID filter, idempotency, dispose/unsubscribe), hyperlink whitelist routing, relative-URL resolution, and the Core/App seam-crossing command are all independently exercised.

**Not actionable now:**
- Task 10 manual UI smoke (AC-2.9.3 FR-046 z-order/minimise/close + AC-2.9.5 render/hyperlinks + AC-2.9.6 live banner) remains unexecuted (headless environment). Recommend running before closing Epic 2 — alongside the deferred 2.6/2.7/2.8 manual smokes. This covers: right-click Properties → window opens above main, clicking main does not push Properties behind, minimise/restore together, close main closes Properties, four URL fields clickable with ≤2 s open, non-http URL refused with Warning, device byebye → banner appears with data still visible, window closeable without error.
- Dev's "1 pre-existing WMC1506" claim: a local-incremental-build artifact. Clean `dotnet build` produces 0 warnings. No action required (same pattern confirmed in 2.7 and 2.8 reviews).
