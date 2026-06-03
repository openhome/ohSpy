---
baseline_commit: 39f3f06aa4a593924c06d072f8e2abca8ea15dae
---

# Story 2.5: Main Window Shell + Device Tree (Top-Level Rows)

Status: done

## Story

As a Linn engineer,
I want a two-pane main window with discovered root devices populating the left-pane tree (sorted, with friendly name + secondary detail line + kind glyph + persistent expand chevron),
so that I can launch ohSpy and see every UPnP root device on my network within the SC-001 budget without manually triggering anything.

## Acceptance Criteria

**Verbatim ACs from epics.md §Story 2.5 (lines 991–1059). AC trait IDs follow Amendment A2.**

**AC-2.5.1 — MainWindow layout**

**Given** `src/ohSpy.App/MainWindow.xaml` + `MainWindow.xaml.cs`
**When** the window opens
**Then** the layout is a `Grid` with two columns — left tree pane and right SSDP log pane (FR-001 — log pane content fills in Story 2.7; this story renders an empty placeholder `Border` for the right pane)
**And** the code-behind is constructor-only (Pattern 13): `InitializeComponent()` + DI-injected `ShellViewModel` assignment exposed as a typed `ViewModel` property (for `x:Bind`), and DataContext

**AC-2.5.2 — ShellViewModel composes DeviceTreeViewModel**

**Given** `src/ohSpy.Core/ViewModels/ShellViewModel.cs`
**When** I inspect it
**Then** it composes `DeviceTreeViewModel` and exposes it via an `[ObservableProperty]` (FR-002)
**And** the `AdapterScope` from Story 2.2 lives inside `ShellViewModel` — constructed by `StartAsync(CancellationToken appToken)` and torn down in `DisposeAsync()` (Amendment A26 migration from App.xaml.cs)
**And** `ShellViewModel` implements `IAsyncDisposable`

**AC-2.5.3 — DeviceTreeViewModel**

**Given** `src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs`
**When** I inspect it
**Then** it exposes `IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel> Devices` (D6 + FR-054)
**And** the sort comparator is case-insensitive ordinal on `FriendlyName` (with `uuid:<uuid>` fallback) with ordinal UUID string tiebreak (FR-054)
**And** the VM subscribes to `IDeviceRegistry.DeviceLoaded` → `Devices.Add(new DeviceNodeViewModel(entry))` (FR-005 + FR-047)
**And** the VM subscribes to `IDeviceRegistry.DeviceUpdated` → updates the existing VM's properties then calls `Devices.Update(existingNode)` (label/sort-key change → `Move(old, new)` per AC-6.4 — selection/expansion preserved per FR-054)
**And** the VM subscribes to `IDeviceRegistry.DeviceRemoved` → `Devices.Remove(uuid)` (FR-008)
**And** all subscriptions marshal via `IUiDispatcher.Post`

**AC-2.5.4 — DeviceNodeViewModel**

**Given** `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`
**When** I inspect the VM
**Then** it wraps a `RegistryEntry` and exposes `[ObservableProperty]` `FriendlyName` (FR-009 — bound from `entry.Description.FriendlyName` or `uuid:<uuid>` fallback per FR-010)
**And** it exposes `NodeKind Kind => NodeKind.Device` (FR-045)
**And** it exposes a `SecondaryDetail` string formatted per FR-051: `<deviceTypeTail> · <host>:<port>` (middle-dot U+00B7 separator; tail of `<deviceType>` after `:device:`; host:port from `entry.LocationUrl`)
**And** `Children` is initialised in the constructor to `[ new LoadingPlaceholderViewModel() ]` so the WinUI `TreeView` renders the expand chevron immediately (FR-044 + AC-A1.1)
**And** `[ObservableProperty]` `IsExpanded` triggers service enumeration in Story 2.6 — wiring stub present in this story but does nothing

**AC-2.5.5 — INodeViewModel + LoadingPlaceholderViewModel + InlineErrorViewModel**

**Given** `src/ohSpy.Core/ViewModels/LoadingPlaceholderViewModel.cs` + `InlineErrorViewModel.cs` + `INodeViewModel.cs`
**When** I inspect them
**Then** they implement an `INodeViewModel` marker interface with `string Label`, `NodeKind Kind` (FR-045)
**And** `LoadingPlaceholderViewModel.Label == "Loading…"` and `Kind == NodeKind.Placeholder`
**And** `InlineErrorViewModel.Label` carries the FR-013 error text and `Kind == NodeKind.Error`
**And** neither renders a kind glyph (FR-045 — only device/service/action nodes carry glyphs)

**AC-2.5.6 — Device row visual rendering**

**Given** the XAML `DataTemplate` for `DeviceNodeViewModel`
**When** it renders
**Then** the layout is a `Grid` (two rows in one column: primary text row, secondary text row) with a leading `FontIcon` in the first row (glyph from Segoe MDL2 Assets or Segoe Fluent Icons — no external icon assets, FR-045)
**And** the friendly name appears as primary text beside the glyph
**And** the secondary detail line appears below in a `TextBlock` bound to `SecondaryDetail` with `Foreground="{StaticResource MutedForegroundBrush}"` (FR-051 + NFR-UI2)
**And** `MutedForegroundBrush` is a `SolidColorBrush` resource key in `App.xaml` App-level resources (Pattern 13)
**And** binding uses `x:Bind` with `x:DataType="vm:DeviceNodeViewModel"` (Pattern 13)

**AC-2.5.7 — SC-001 performance budget**

**Given** the SC-001 performance budget
**When** I launch ohSpy on a LAN with 10–20 announcing UPnP devices
**Then** every responsive device with a fetchable description is visible in the tree within ≤ ~7 s (5 s MX + ≤ 2 s eager fetch)
**And** zero duplicate tree entries appear for any UUID (SC-002 + FR-007)
**And** devices whose description fetch failed do NOT appear in the tree (FR-047 — `DeviceLoaded` only fires on `Loaded` state)

**AC-2.5.8 — Sort-key migration on re-announce**

**Given** a re-announce that changes a device's friendly name
**When** the change triggers `DeviceUpdated`
**Then** the row migrates to its new sorted position via `Move(old, new)` (AC-6.4 + FR-054)
**And** the row's identity (and any future expansion state) is preserved across the migration
**And** sibling subtrees are NOT redrawn (NFR-P5 + FR-054 consequence)

**AC-2.5.9 — Byebye removes row**

**Given** a `byebye` arrives during steady state
**When** the registry removes the entry
**Then** the row vanishes within ~2 s on a quiet LAN (SC-003)

**AC-2.5.10 — AdapterScope in ShellViewModel (Amendment A26)**

**Given** `App.xaml.cs` after Story 2.5
**When** I inspect it
**Then** `_adapterScope` field is REMOVED from `App` — it now lives as a private field inside `ShellViewModel`
**And** `App.OnLaunched` resolves `ShellViewModel` from DI, calls `_ = shellVm.StartAsync(_appCts.Token)` (fire-and-forget per A26 pattern)
**And** `App.ShutdownAsync` calls `await _shellVm.DisposeAsync()` instead of `await _adapterScope.DisposeAsync()`

---

## Tasks / Subtasks

### Task 1 — Add CommunityToolkit.Mvvm package reference to ohSpy.Core (AC: all VM ACs)

- [x] **1.1** In `src/ohSpy.Core/ohSpy.Core.csproj`, add inside the `<ItemGroup>`:
  ```xml
  <!-- Story 2.5: MVVM source-gen for ViewModels (ObservableObject, ObservableProperty, RelayCommand). Version pinned in Directory.Packages.props at 8.4.0. Core is platform-independent; CommunityToolkit.Mvvm targets net standard — no WinUI dependency, NetArchTest boundary still holds. -->
  <PackageReference Include="CommunityToolkit.Mvvm" />
  ```
- [x] **1.2** Confirm `dotnet build ohSpy.Core` resolves without error after the package addition.
- [x] **1.3** Confirm `CoreAppBoundaryTests` still green — CommunityToolkit.Mvvm must not introduce a `Microsoft.UI.*`, `Microsoft.Windows.*`, or `WinRT.Interop.*` dependency.

### Task 2 — INodeViewModel + NodeKind + placeholder/error VMs (AC: #5)

- [x] **2.1** Create `src/ohSpy.Core/ViewModels/NodeKind.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  public enum NodeKind { Device, Service, Action, Placeholder, Error }
  ```
- [x] **2.2** Create `src/ohSpy.Core/ViewModels/INodeViewModel.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  public interface INodeViewModel
  {
      string Label { get; }
      NodeKind Kind { get; }
  }
  ```
- [x] **2.3** Create `src/ohSpy.Core/ViewModels/LoadingPlaceholderViewModel.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  public sealed class LoadingPlaceholderViewModel : INodeViewModel
  {
      public string Label => "Loading…"; // "Loading…" (ellipsis U+2026)
      public NodeKind Kind => NodeKind.Placeholder;
  }
  ```
- [x] **2.4** Create `src/ohSpy.Core/ViewModels/InlineErrorViewModel.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  public sealed class InlineErrorViewModel : INodeViewModel
  {
      public string Label { get; }
      public NodeKind Kind => NodeKind.Error;
      public InlineErrorViewModel(string message) => Label = message;
  }
  ```

### Task 3 — DeviceNodeViewModel (AC: #4, #5, #6)

- [x] **3.1** Create `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`. Implements `INodeViewModel`, extends `ObservableObject` (CommunityToolkit):
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using CommunityToolkit.Mvvm.ComponentModel;
  using ohSpy.Core.Devices;
  using System.Collections.ObjectModel;

  public partial class DeviceNodeViewModel : ObservableObject, INodeViewModel
  {
      private RegistryEntry _entry;

      [ObservableProperty] private string _friendlyName = "";
      [ObservableProperty] private string _secondaryDetail = "";
      [ObservableProperty] private bool _isExpanded;

      public NodeKind Kind => NodeKind.Device;

      // Glyph from Segoe MDL2 Assets — "Network" icon (U+E703).
      // No converter needed for devices in this story; Story 2.6 adds Service/Action glyphs.
      public string KindGlyph => "";

      public ObservableCollection<INodeViewModel> Children { get; } = [];

      public DeviceNodeViewModel(RegistryEntry entry)
      {
          _entry = entry;
          Children.Add(new LoadingPlaceholderViewModel()); // AC-A1.1: force expand chevron
          RefreshFrom(entry);
      }

      public Guid Uuid => _entry.Uuid;

      // Called by DeviceTreeViewModel on DeviceUpdated to push new display values.
      internal void RefreshFrom(RegistryEntry entry)
      {
          _entry = entry;
          FriendlyName = entry.Description?.FriendlyName is { Length: > 0 } name
              ? name
              : $"uuid:{entry.Uuid}";
          SecondaryDetail = ComputeSecondaryDetail(entry);
      }

      private static string ComputeSecondaryDetail(RegistryEntry entry)
      {
          var deviceType = entry.Description?.DeviceType ?? "";
          const string deviceMarker = ":device:";
          var idx = deviceType.IndexOf(deviceMarker, StringComparison.OrdinalIgnoreCase);
          var tail = idx >= 0 ? deviceType[(idx + deviceMarker.Length)..] : deviceType;
          return $"{tail} · {entry.LocationUrl.Host}:{entry.LocationUrl.Port}";
      }
  }
  ```
- [x] **3.2** `IsExpanded` property change stub: in `OnIsExpandedChanged(bool value)` (generated partial hook from `[ObservableProperty]`) — add a comment `// Story 2.6 wires service enumeration here`. Leave the body empty for now.
- [x] **3.3** `Children` is `ObservableCollection<INodeViewModel>` (not `IdentityKeyedSortedCollection` — children at this level are small, heterogeneous, and replaced atomically). Add `ReplaceWith` helper:
  ```csharp
  internal void ReplaceWith(IReadOnlyList<INodeViewModel> newChildren)
  {
      Children.Clear();
      foreach (var child in newChildren)
          Children.Add(child);
      // Clear+AddRange emits Reset; acceptable for placeholder→real-children swap (A1 atomic rule).
  }
  ```
  (Story 2.6 calls this. Shape it now so 2.6 doesn't need a new API on DeviceNodeViewModel.)
- [x] **3.4** `INodeViewModel.Label` explicit implementation returns `FriendlyName` (the interface label):
  ```csharp
  string INodeViewModel.Label => FriendlyName;
  ```

### Task 4 — DeviceTreeViewModel (AC: #3)

- [x] **4.1** Create `src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using ohSpy.Core.Collections;
  using ohSpy.Core.Devices;
  using ohSpy.Core.Threading;

  public sealed class DeviceTreeViewModel
  {
      private readonly IUiDispatcher _ui;

      public IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel> Devices { get; }

      public DeviceTreeViewModel(IDeviceRegistry registry, IUiDispatcher ui)
      {
          _ui = ui;
          Devices = new IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>(
              vm => vm.Uuid,
              DeviceNodeComparer.Instance);

          registry.DeviceLoaded  += OnDeviceLoaded;
          registry.DeviceUpdated += OnDeviceUpdated;
          registry.DeviceRemoved += OnDeviceRemoved;
      }

      private void OnDeviceLoaded(RegistryEntry entry) =>
          _ui.Post(() => Devices.Add(new DeviceNodeViewModel(entry)));

      private void OnDeviceUpdated(RegistryEntry entry)
      {
          _ui.Post(() =>
          {
              if (Devices.TryGetItem(entry.Uuid, out var vm))
              {
                  vm.RefreshFrom(entry);
                  Devices.Update(vm); // re-sort if FriendlyName changed
              }
          });
      }

      private void OnDeviceRemoved(Guid uuid) =>
          _ui.Post(() => Devices.Remove(uuid));
  }
  ```
- [x] **4.2** Create `DeviceNodeComparer` as a private nested class (or inner file) inside the same file — or as a small separate `sealed` class in the same namespace:
  ```csharp
  internal sealed class DeviceNodeComparer : IComparer<DeviceNodeViewModel>
  {
      public static readonly DeviceNodeComparer Instance = new();
      private DeviceNodeComparer() { }

      public int Compare(DeviceNodeViewModel? x, DeviceNodeViewModel? y)
      {
          if (x is null && y is null) return 0;
          if (x is null) return -1;
          if (y is null) return 1;

          // Primary: case-insensitive FriendlyName (FR-054).
          int nameCmp = string.Compare(x.FriendlyName, y.FriendlyName,
              StringComparison.OrdinalIgnoreCase);
          if (nameCmp != 0) return nameCmp;

          // Tiebreak: ordinal UUID string (stable for equal friendly names).
          return string.Compare(x.Uuid.ToString(), y.Uuid.ToString(),
              StringComparison.Ordinal);
      }
  }
  ```

### Task 5 — ShellViewModel (AC: #2, #10)

- [x] **5.1** Create `src/ohSpy.Core/ViewModels/ShellViewModel.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using CommunityToolkit.Mvvm.ComponentModel;
  using ohSpy.Core.Devices;
  using ohSpy.Core.Diagnostics;
  using ohSpy.Core.Discovery;
  using ohSpy.Core.Threading;

  public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
  {
      private readonly INetworkAdapterEnumerator _adapterEnum;
      private readonly ISsdpTransport _transport;
      private readonly DiscoveryService _discovery;
      private readonly IDiagnosticEmitter _diag;

      private AdapterScope? _adapterScope;

      [ObservableProperty]
      private DeviceTreeViewModel _deviceTree;

      public ShellViewModel(
          INetworkAdapterEnumerator adapterEnum,
          ISsdpTransport transport,
          DiscoveryService discovery,
          IDeviceRegistry registry,
          IUiDispatcher ui,
          IDiagnosticEmitter diag)
      {
          _adapterEnum = adapterEnum;
          _transport   = transport;
          _discovery   = discovery;
          _diag        = diag;
          _deviceTree  = new DeviceTreeViewModel(registry, ui);
      }

      // Called from App.OnLaunched (fire-and-forget, Amendment A26 pattern).
      // Constructs and starts the AdapterScope; starts DiscoveryService after scope is live.
      public Task StartAsync(CancellationToken appToken)
      {
          _adapterScope = new AdapterScope(_adapterEnum, _transport, _diag, appToken);
          _ = RunStartAsync(_adapterScope);
          return Task.CompletedTask;
      }

      private async Task RunStartAsync(AdapterScope scope)
      {
          try
          {
              await scope.StartAsync().ConfigureAwait(false);
              if (scope.CurrentAdapterIPv4 is not null)
              {
                  await _discovery.StartAsync(scope.AdapterToken, scope.AdapterToken)
                                  .ConfigureAwait(false);
              }
          }
          catch (Exception ex) when (ex is not OutOfMemoryException)
          {
              _diag.Warning(DiagCategories.AdapterSwitch,
                  "adapter startup failed — no SSDP traffic",
                  new DiagnosticContext { ErrorText = ex.Message });
          }
      }

      public async ValueTask DisposeAsync()
      {
          if (_adapterScope is not null)
              await _adapterScope.DisposeAsync().ConfigureAwait(false);
      }
  }
  ```
- [x] **5.2** `ShellViewModel.StartAsync` is NOT async itself — it fires `RunStartAsync` internally (equivalent to the current `StartAdapterScopeAsync` pattern in App). This matches the A26 "fire-and-forget" pattern and keeps `App.OnLaunched` synchronous.

### Task 6 — ServiceRegistration: add ShellViewModel (AC: #10)

- [x] **6.1** In `src/ohSpy.App/Composition/ServiceRegistration.cs`, after the Story 2.4 registrations, add:
  ```csharp
  // Story 2.5 — Main window shell ViewModel. Singleton: one window, one ShellViewModel.
  // ShellViewModel owns the AdapterScope lifetime (Amendment A26 migration from App.xaml.cs).
  // DeviceTreeViewModel is constructed by ShellViewModel, not registered separately.
  services.AddSingleton<ShellViewModel>();
  ```
  Add `using ohSpy.Core.ViewModels;` at the top.

### Task 7 — App.xaml.cs: migrate AdapterScope to ShellViewModel (AC: #10)

- [x] **7.1** In `src/ohSpy.App/App.xaml.cs`, REMOVE the `_adapterScope` field and add `_shellVm`:
  ```csharp
  // Remove: private AdapterScope? _adapterScope;
  private ShellViewModel? _shellVm;
  ```
- [x] **7.2** In `OnLaunched`, REMOVE the `AdapterScope` construction block and `StartAdapterScopeAsync` call + `DiscoveryService` resolve. Replace with:
  ```csharp
  _shellVm = Services.GetRequiredService<ShellViewModel>();
  _ = _shellVm.StartAsync(_appCts.Token); // fire-and-forget; exceptions handled inside ShellViewModel.RunStartAsync
  ```
  The `_ = Services.GetRequiredService<EagerDescriptionDispatcher>();` and `IUiDispatcher` pin and `DiagnosticFileSink.SetRingSink(...)` stay unchanged — keep them in order before the ShellViewModel resolve.
- [x] **7.3** In `OnLaunched`, pass `_shellVm` to `MainWindow`:
  ```csharp
  _window = new MainWindow(_shellVm);
  ```
- [x] **7.4** In `ShutdownAsync`, replace `await _adapterScope.DisposeAsync()` with:
  ```csharp
  if (_shellVm is not null)
      await _shellVm.DisposeAsync().ConfigureAwait(false);
  ```
- [x] **7.5** Remove `using ohSpy.Core.Discovery;` if it was only needed for `AdapterScope` — keep it if still needed for other types (`INetworkAdapterEnumerator`, `ISsdpTransport`, `DiscoveryService` resolves are removed; `IDiscoveryService` is still potentially referenced). Actually `App.xaml.cs` no longer resolves any Discovery types directly — remove the unused using.
- [x] **7.6** Remove `StartAdapterScopeAsync` static method entirely from `App.xaml.cs` — it moves into `ShellViewModel.RunStartAsync`.
- [x] **7.7** Add `using ohSpy.Core.ViewModels;` to `App.xaml.cs`.

### Task 8 — MainWindow.xaml + MainWindow.xaml.cs refactor (AC: #1, #6)

- [x] **8.1** Update `src/ohSpy.App/MainWindow.xaml.cs` — inject `ShellViewModel`:
  ```csharp
  using Microsoft.UI.Xaml;
  using ohSpy.Core.ViewModels;

  namespace ohSpy.App;

  public sealed partial class MainWindow : Window
  {
      // Exposed as a typed property so x:Bind in XAML can reference it at compile time.
      // Pattern 13: constructor-only code-behind; all logic in VM.
      public ShellViewModel ViewModel { get; }

      public MainWindow(ShellViewModel vm)
      {
          InitializeComponent();
          ViewModel = vm;
          ExtendsContentIntoTitleBar = true;
          SetTitleBar(AppTitleBar);
          AppWindow.SetIcon("Assets/AppIcon.ico");
      }
  }
  ```
  Remove the `RootFrame.Navigate(typeof(MainPage))` call.
- [x] **8.2** Update `src/ohSpy.App/MainWindow.xaml` — replace Frame-based layout with two-column shell.
  The new XAML structure (full replacement):
  ```xml
  <?xml version="1.0" encoding="utf-8" ?>
  <Window
      x:Class="ohSpy.App.MainWindow"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
      xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
      xmlns:vm="using:ohSpy.Core.ViewModels"
      Title="ohSpy"
      mc:Ignorable="d">
      <Window.SystemBackdrop>
          <MicaBackdrop />
      </Window.SystemBackdrop>

      <Grid>
          <Grid.RowDefinitions>
              <RowDefinition Height="Auto" />
              <RowDefinition Height="*" />
          </Grid.RowDefinitions>

          <TitleBar x:Name="AppTitleBar" Title="ohSpy">
              <TitleBar.IconSource>
                  <ImageIconSource ImageSource="Assets/AppIcon.ico" />
              </TitleBar.IconSource>
          </TitleBar>

          <!-- FR-001: Two-pane shell. Left: device tree. Right: SSDP log (Story 2.7 fills it). -->
          <Grid Grid.Row="1">
              <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="300" MinWidth="200" />
                  <ColumnDefinition Width="*" />
              </Grid.ColumnDefinitions>

              <!-- Left pane: device tree (FR-001 + FR-002) -->
              <TreeView
                  Grid.Column="0"
                  ItemsSource="{x:Bind ViewModel.DeviceTree.Devices, Mode=OneWay}"
                  SelectionMode="Single">

                  <!-- ItemContainerStyle sets Children binding for nested nodes (classic Binding for style setter — x:Bind doesn't work in Style.Setter in WinUI 3) -->
                  <TreeView.ItemContainerStyle>
                      <Style TargetType="TreeViewItem">
                          <Setter Property="ItemsSource" Value="{Binding Children}" />
                      </Style>
                  </TreeView.ItemContainerStyle>

                  <!-- DataTemplateSelector handles DeviceNodeViewModel vs placeholder/error children -->
                  <TreeView.ItemTemplateSelector>
                      <local:NodeDataTemplateSelector
                          xmlns:local="using:ohSpy.App.Converters">
                          <local:NodeDataTemplateSelector.DeviceTemplate>
                              <DataTemplate x:DataType="vm:DeviceNodeViewModel">
                                  <Grid>
                                      <Grid.ColumnDefinitions>
                                          <ColumnDefinition Width="Auto" />
                                          <ColumnDefinition Width="*" />
                                      </Grid.ColumnDefinitions>
                                      <Grid.RowDefinitions>
                                          <RowDefinition Height="Auto" />
                                          <RowDefinition Height="Auto" />
                                      </Grid.RowDefinitions>

                                      <!-- Kind glyph (FR-045): Segoe MDL2 Assets U+E703 "Network" -->
                                      <FontIcon
                                          Grid.Column="0" Grid.RowSpan="2"
                                          VerticalAlignment="Center"
                                          Margin="0,0,8,0"
                                          FontFamily="Segoe MDL2 Assets"
                                          FontSize="16"
                                          Glyph="{x:Bind KindGlyph}" />

                                      <!-- Primary text: FriendlyName (FR-009) -->
                                      <TextBlock
                                          Grid.Column="1" Grid.Row="0"
                                          Text="{x:Bind FriendlyName, Mode=OneWay}"
                                          TextTrimming="CharacterEllipsis" />

                                      <!-- Secondary detail: DeviceTypeTail · host:port (FR-051) -->
                                      <TextBlock
                                          Grid.Column="1" Grid.Row="1"
                                          Text="{x:Bind SecondaryDetail, Mode=OneWay}"
                                          FontSize="11"
                                          Foreground="{StaticResource MutedForegroundBrush}"
                                          TextTrimming="CharacterEllipsis" />
                                  </Grid>
                              </DataTemplate>
                          </local:NodeDataTemplateSelector.DeviceTemplate>

                          <local:NodeDataTemplateSelector.FallbackTemplate>
                              <DataTemplate x:DataType="vm:INodeViewModel">
                                  <TextBlock
                                      Text="{x:Bind Label, Mode=OneWay}"
                                      Foreground="{StaticResource MutedForegroundBrush}"
                                      FontStyle="Italic" />
                              </DataTemplate>
                          </local:NodeDataTemplateSelector.FallbackTemplate>
                      </local:NodeDataTemplateSelector>
                  </TreeView.ItemTemplateSelector>
              </TreeView>

              <!-- Right pane: placeholder for SSDP log (Story 2.7) -->
              <Border
                  Grid.Column="1"
                  BorderBrush="{ThemeResource DividerStrokeColorDefaultBrush}"
                  BorderThickness="1,0,0,0">
                  <TextBlock
                      Text="SSDP log — Story 2.7"
                      HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      Foreground="{StaticResource MutedForegroundBrush}" />
              </Border>
          </Grid>
      </Grid>
  </Window>
  ```
- [x] **8.3** Create `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs`:
  ```csharp
  namespace ohSpy.App.Converters;

  using Microsoft.UI.Xaml;
  using Microsoft.UI.Xaml.Controls;
  using ohSpy.Core.ViewModels;

  // Selects the correct DataTemplate for heterogeneous tree nodes (FR-045).
  // DeviceNodeViewModel gets the full glyph+name+detail template.
  // All other INodeViewModel types (Loading placeholder, InlineError) get the FallbackTemplate.
  public sealed class NodeDataTemplateSelector : DataTemplateSelector
  {
      public DataTemplate DeviceTemplate { get; set; } = null!;
      public DataTemplate FallbackTemplate { get; set; } = null!;

      protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
          item is DeviceNodeViewModel ? DeviceTemplate : FallbackTemplate;
  }
  ```
- [x] **8.4** Delete `src/ohSpy.App/MainPage.xaml` and `src/ohSpy.App/MainPage.xaml.cs` (scaffold code from the WinUI template, superseded by direct MainWindow content).

### Task 9 — App.xaml: add MutedForegroundBrush resource (AC: #6)

- [x] **9.1** In `src/ohSpy.App/App.xaml`, add `MutedForegroundBrush` to the App-level `ResourceDictionary`:
  ```xml
  <!-- Pattern 13: App-level brush resource for secondary/muted text (NFR-UI2) -->
  <SolidColorBrush x:Key="MutedForegroundBrush" Color="#FF767676" />
  ```
  `#FF767676` is a mid-grey that passes 3:1 contrast ratio against white backgrounds (NFR-UI2 guideline).

### Task 10 — Tests: DeviceNodeViewModel (AC: #4, #5, #8)

**Location:** `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs`

Build `RegistryEntry` test helpers using `RegistryEntry`'s internal constructor. Because `RegistryEntry.ctor` is `internal`, tests in `ohSpy.Core.Tests` can reach it via the existing `InternalsVisibleTo` declaration.

- [x] **10.1** `Constructor_WithLoadedEntry_SetsFriendlyName_AC254` — entry with `FriendlyName = "Linn Klimax DSM"` → `vm.FriendlyName == "Linn Klimax DSM"`.
- [x] **10.2** `Constructor_WithNullFriendlyName_FallsBackToUuid_AC254` — entry with no description → `vm.FriendlyName` starts with `"uuid:"`.
- [x] **10.3** `Constructor_InitializesChildrenWithPlaceholder_ACA11` — `vm.Children.Count == 1`, first child is `LoadingPlaceholderViewModel`.
- [x] **10.4** `Constructor_KindIsDevice_AC254` — `vm.Kind == NodeKind.Device`.
- [x] **10.5** `SecondaryDetail_FormatsDeviceTypeTailAndHostPort_AC254` — `DeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1"`, `LocationUrl = "http://192.168.1.100:49152/desc.xml"` → `vm.SecondaryDetail == "MediaRenderer:1 · 192.168.1.100:49152"`.
- [x] **10.6** `SecondaryDetail_WhenDeviceTypeHasNoDeviceMarker_UsesFullType_AC254` — `DeviceType = "upnp:rootdevice"` → tail is `"upnp:rootdevice"`.
- [x] **10.7** `RefreshFrom_UpdatesFriendlyNameAndSecondaryDetail_AC258` — initial FriendlyName "Old Name"; call `RefreshFrom(newEntry)` with "New Name" → `FriendlyName == "New Name"`, SecondaryDetail updated.
- [x] **10.8** `ReplaceWith_ReplacesChildrenCollection_ACA14` — call `vm.ReplaceWith([ new InlineErrorViewModel("err") ])` → `Children.Count == 1`, child is `InlineErrorViewModel`.
- [x] **10.9** `LoadingPlaceholderViewModel_LabelAndKind_AC255` — `new LoadingPlaceholderViewModel().Label == "Loading…"`, Kind == Placeholder.
- [x] **10.10** `InlineErrorViewModel_LabelAndKind_AC255` — `new InlineErrorViewModel("failed").Label == "failed"`, Kind == Error.

### Task 11 — Tests: DeviceTreeViewModel (AC: #3, #8, #9)

**Location:** `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs`

Uses `DeviceRegistry` (real instance) + `InlineUiDispatcher` + helpers to build `RegistryEntry` instances (via internal ctor).

- [x] **11.1** `DeviceLoaded_AddsDeviceNodeViewModelToDevices_AC253` — fire `DeviceLoaded` → `vm.Devices.Count == 1`, entry's UUID matches.
- [x] **11.2** `DeviceLoaded_MarshalledViaDispatcher_AC253` — use a recording dispatcher that captures `Post` calls; verify `Post` is called (not direct mutation).
- [x] **11.3** `DeviceRemoved_RemovesFromDevices_AC253` — add then remove → `vm.Devices.Count == 0`.
- [x] **11.4** `DeviceUpdated_UpdatesExistingNodeAndResortsIfNeeded_AC258` — add two devices "Bravo" and "Alpha"; update "Bravo" to "Aardvark"; verify "Aardvark" sorts before "Alpha" (Move notification fired).
- [x] **11.5** `DeviceUpdated_PreservesNodeIdentityOnRename_AC258` — after rename the same `DeviceNodeViewModel` instance is in the collection (not a new one).
- [x] **11.6** `Devices_SortedCaseInsensitive_AC253` — add "zebra" then "Apple" → order is Apple first (case-insensitive sort FR-054).
- [x] **11.7** `Devices_UuidTiebreakForEqualFriendlyNames_AC253` — two entries with identical FriendlyName "Linn DS"; verify stable ordering by UUID string.
- [x] **11.8** `DeviceRemoved_UnknownUuid_DoesNotThrow_AC253` — calling Remove for a UUID not in Devices is a no-op (IDeviceRegistry may fire DeviceRemoved for non-Loaded entries which DeviceTreeViewModel never received).

### Task 12 — Final verification (AC: all)

- [x] **12.1** `dotnet build` — 0 errors / 0 warnings (`TreatWarningsAsErrors` enforced).
- [x] **12.2** `dotnet test` — all tests green. Baseline 229 (Story 2.4). Story 2.5 adds ~20 tests; target ~249.
- [x] **12.3** `dotnet test --filter "category=chaos"` — still exactly **1** (chaos suite unchanged).
- [x] **12.4** `CoreAppBoundaryTests` green — `CommunityToolkit.Mvvm` must not pull `Microsoft.UI.*`, `Microsoft.Windows.*`, or `WinRT.Interop.*` into Core assembly. Verify by running the arch tests.
- [x] **12.5** Manual smoke (not AC-gating): launch `ohSpy.App`. The window opens with a Mica backdrop, two-column layout (narrow tree pane left, placeholder right). Within ~7 s devices should appear in the left tree with glyph + FriendlyName + SecondaryDetail rows. Expand chevron visible on every device. The right pane shows "SSDP log — Story 2.7" placeholder text.

### Review Findings

_Adversarial code review 2026-06-03 (claude-opus-4-8 / Blind Hunter + Edge Case Hunter + Acceptance Auditor). All 10 ACs verified satisfied — no AC violations. 5 patches applied + 3 regression tests added (247 -> 250 passing). 1 finding deferred. The "KindGlyph empty" finding proved a false positive (see dismissed note)._

- [x] [Review][Patch] OnDeviceLoaded unguarded against duplicate UUID — `Devices.Add` throws `ArgumentException` if the UUID is already present, faulting the UI-thread closure (the sibling `OnDeviceUpdated` is guarded). FIXED: `OnDeviceLoaded` now folds a duplicate Loaded into an update (mirrors `OnDeviceUpdated`). Regression test `DeviceLoaded_DuplicateUuid_TreatedAsUpdate_NoThrow_AC253`. [src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs]
- [x] [Review][Patch] ShellViewModel startup task discarded + IDiscoveryService never disposed — `_ = RunStartAsync(...)` discarded the task (stale `_runTask` comment), `DisposeAsync` did not await it (dispose could race in-flight bind), and the started `IDiscoveryService` (IAsyncDisposable) was never drained. FIXED: `_runTask` field stored; `DisposeAsync` awaits it, disposes the scope, then `await _discovery.DisposeAsync()`; comment corrected. [src/ohSpy.Core/ViewModels/ShellViewModel.cs]
- [x] [Review][Patch] ShellViewModel.StartAsync had no re-entrancy guard — a second call would orphan the first `AdapterScope` (transport + linked CTS). FIXED: `Interlocked.Exchange` started-guard added (matches `DiscoveryService.StartAsync` precedent). [src/ohSpy.Core/ViewModels/ShellViewModel.cs]
- [x] [Review][Patch] DeviceTreeViewModel subscribed to 3 registry events with no unsubscribe — registry is a long-lived singleton holding strong delegate refs. FIXED: `DeviceTreeViewModel` now implements `IDisposable` (idempotent unsubscribe); `ShellViewModel.DisposeAsync` disposes it. Test fixture made `IDisposable` to satisfy CA1001. [src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs]
- [x] [Review][Patch] ComputeSecondaryDetail mishandled degenerate device metadata — an empty type tail rendered an orphaned " <U+00B7> host:port"; an unresolvable port (Uri.Port == -1) rendered "host:-1". FIXED: empty tail drops the separator; Port < 0 drops the ":port". Regression tests `SecondaryDetail_EmptyDeviceType_OmitsSeparator_AC254`, `SecondaryDetail_UnresolvablePort_OmitsPort_AC254`. [src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs]
- [x] [Review][Defer] ReplaceWith Clear()+Add emits Reset — collapses expanded service subtrees on a second swap; only bites once Story 2.6 wires real expansion. Deferred to Story 2.6. [src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs] — deferred, surfaces in Story 2.6
- [x] [Review][Dismissed] "KindGlyph renders empty" (Blind Hunter F7 / Acceptance Auditor F3) — FALSE POSITIVE. The source already contained the correct Segoe MDL2 "Network" glyph U+E703; it renders as an invisible PUA char in the diff, which the reviewers read as "empty". Verified against the original git-diff bytes (KindGlyph => "<U+E703>"). AC-2.5.6 satisfied as-shipped; no change needed. (Likewise the FR-051 U+00B7 separator and the "Loading" U+2026 ellipsis were correctly encoded all along; an apparent "mojibake" signal during review was an artifact of Windows PowerShell 5.1 Get-Content defaulting to ANSI when reading UTF-8-no-BOM files.)

---

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **FR-001** | Two-pane `MainWindow.xaml` shell; left tree, right placeholder | AC-2.5.1 |
| **FR-002 / FR-005 / FR-008** | `DeviceTreeViewModel` subscribes to registry events; `Devices` collection drives WinUI `TreeView` | AC-2.5.3 |
| **FR-009 / FR-010** | `DeviceNodeViewModel.FriendlyName` with uuid fallback | AC-2.5.4 |
| **FR-045 / FR-051** | Kind glyph + secondary detail line in device row template | AC-2.5.6 |
| **FR-047** | `DeviceLoaded` (not DeviceAdded) — tree rows only appear when description is parsed | AC-2.5.7 |
| **FR-054 / Decision 6** | `IdentityKeyedSortedCollection` sort + `Move` notification (not Remove+Add) | AC-2.5.3, AC-2.5.8 |
| **FR-044 / Amendment A1** | `LoadingPlaceholderViewModel` child forces expand chevron | AC-2.5.4 |
| **Pattern 13** | `x:Bind` DataTemplate, code-behind constructor-only, `MutedForegroundBrush` in App.xaml | AC-2.5.6 |
| **Amendment A26** | AdapterScope construction moves from `App.xaml.cs` into `ShellViewModel` | AC-2.5.10 |

### CRITICAL DESIGN DECISIONS

**1. AdapterScope migration (Amendment A26).**
`App.xaml.cs` currently constructs `AdapterScope` and calls `StartAdapterScopeAsync`. This story moves that into `ShellViewModel.StartAsync(CancellationToken appToken)`. The _appCts token is passed from App.OnLaunched. Key invariant: `App._appCts.CancelAsync()` fires first in ShutdownAsync (cancels all linked scopes), THEN `await _shellVm.DisposeAsync()` awaits the adapter scope's teardown. This ordering (cancel-then-dispose) matches the current ShutdownAsync pattern.

`ShellViewModel.StartAsync` returns `Task.CompletedTask` immediately after fire-and-forgetting `RunStartAsync`. App does `_ = shellVm.StartAsync(...)` — the same pattern as the current `_ = StartAdapterScopeAsync(...)`. Exceptions inside `RunStartAsync` are caught and emitted as `Warning(DiagCategories.AdapterSwitch, ...)`.

**2. ShellViewModel in ohSpy.Core.**
ShellViewModel takes `INetworkAdapterEnumerator`, `ISsdpTransport`, `DiscoveryService` (concrete, needed because StartAsync calls it directly), `IDeviceRegistry`, `IUiDispatcher`, `IDiagnosticEmitter`. All are Core types — no WinUI references. NetArchTest boundary holds.

**3. `x:Bind` requires a typed property on the code-behind.**
WinUI 3's `x:Bind` resolves paths relative to the code-behind class (not DataContext). Expose `public ShellViewModel ViewModel { get; }` on `MainWindow`. Then use `{x:Bind ViewModel.DeviceTree.Devices, Mode=OneWay}`. DataContext is also set for any classic `Binding` fallbacks.

**4. `DataTemplateSelector` is required for heterogeneous tree items.**
`TreeView.ItemTemplate` applies to ALL items at ALL tree levels. Since the tree has `DeviceNodeViewModel` at the root and `LoadingPlaceholderViewModel` / `InlineErrorViewModel` as children, we need `TreeView.ItemTemplateSelector`. The `NodeDataTemplateSelector` (in `ohSpy.App/Converters/`) uses `is DeviceNodeViewModel` to route to the device template; everything else gets the fallback.

**5. `TreeView.ItemContainerStyle` + classic `Binding` for children.**
WinUI 3 `Style.Setter` does not support `x:Bind` — only classic `Binding`. Use:
```xml
<Setter Property="ItemsSource" Value="{Binding Children}" />
```
This binds the generated `TreeViewItem.ItemsSource` to `DeviceNodeViewModel.Children` (an `ObservableCollection<INodeViewModel>`). The `LoadingPlaceholderViewModel` inside Children gives the WinUI TreeView the non-empty children count it needs to render the expand chevron (FR-044).

**6. `DeviceNodeViewModel.Children` is `ObservableCollection<INodeViewModel>`, not `IdentityKeyedSortedCollection`.**
Children at the device level are small, replaced atomically (not incrementally sorted). `ObservableCollection` suffices. Story 2.6's `ReplaceWith` calls `Clear()` + `Add` range (emits `Reset`), which is acceptable here because the placeholder is the only item being replaced during expand. `IdentityKeyedSortedCollection` is for the top-level `Devices` (which is large, sorted, and identity-keyed for FR-054 `Move` semantics).

**7. `IsExpanded` partial hook for Story 2.6.**
CommunityToolkit.Mvvm 8.x generates a `partial void OnIsExpandedChanged(bool value)` hook when you use `[ObservableProperty]`. Add the partial method stub with a comment pointing to Story 2.6. The empty body compiles fine.

**8. `SecondaryDetail` is a computed value derived from description data.**
Since `DeviceNodeViewModel` is only created when `DeviceLoaded` fires (i.e., `entry.Description` is non-null), the initial `SecondaryDetail` computation is always safe. On `RefreshFrom(entry)`, re-compute it. Make `SecondaryDetail` an `[ObservableProperty]` so XAML gets change notifications when `RefreshFrom` is called.

**9. `FallbackTemplate` uses `x:DataType="vm:INodeViewModel"`.**
The `INodeViewModel` interface is in `ohSpy.Core.ViewModels`. In XAML, `xmlns:vm="using:ohSpy.Core.ViewModels"` covers both the concrete VMs and the interface. `x:Bind Label` in the fallback template works because `INodeViewModel` exposes `string Label`.

**10. MainPage.xaml deletion.**
`MainPage.xaml` and `MainPage.xaml.cs` are the WinUI project template scaffolding files. They become unused after this story replaces the Frame-navigation pattern with direct MainWindow content. Delete both. The `<Page>` class is removed from the build; there are no other references after `MainWindow.xaml.cs` removes the `RootFrame.Navigate(typeof(MainPage))` call.

### What this story does NOT do (scope discipline)

- **Does NOT implement SSDP log** (right pane placeholder only — Story 2.7).
- **Does NOT implement expand handler** (IsExpanded stub only — Story 2.6).
- **Does NOT add adapter switch** (Story 5.2) or rescan command (Story 5.3).
- **Does NOT add `WindowOwnershipManager`** (Story 2.9).
- **Does NOT implement `PropertiesWindow` or invocation popups** (Stories 2.9, 3.2).
- **Does NOT add diagnostics emission for VM-level failures.** The diagnostic path for description fetch failures already exists in Stories 2.3/2.4; ShellViewModel only wraps the existing `AdapterSwitch` warning path.

### Previous-story intelligence

**Story 2.2 (AdapterScope):**
- `AdapterScope(INetworkAdapterEnumerator, ISsdpTransport, IDiagnosticEmitter, CancellationToken appToken)` constructor.
- `scope.StartAsync()` binds to the adapter; `scope.AdapterToken` is the adapter-level CTS token.
- `scope.CurrentAdapterIPv4` is null when no eligible adapter was found (zero-adapter path).
- `scope.DisposeAsync()` cancels the adapter CTS, disposes the transport, awaits cleanup.
- **The `_adapterScope` field and `StartAdapterScopeAsync` method move from `App.xaml.cs` into `ShellViewModel` in this story.**

**Story 2.3 (DeviceRegistry):**
- `IDeviceRegistry.DeviceLoaded(RegistryEntry)` — only fires when `DescriptionFetchState == Loaded`. FR-047 guarantee: VMs never see Pending/InFlight/Failed entries. `entry.Description` is non-null when this fires.
- `IDeviceRegistry.DeviceUpdated(RegistryEntry)` — fires when display metadata changes on a Loaded entry (friendly name update via re-announce).
- `IDeviceRegistry.DeviceRemoved(Guid)` — fires for byebye / prune / mismatch.
- All three events are raised on the UI thread (via `IUiDispatcher.Post` inside `DeviceRegistry`).

**Story 2.4 (DiscoveryService):**
- `DiscoveryService.StartAsync(adapterToken, ct)` starts the datagram read loop. Must be called AFTER `scope.StartAsync()`.
- Zero-adapter path: `scope.CurrentAdapterIPv4 == null` → don't call `discovery.StartAsync`.
- `IDiscoveryService.AnnouncementReceived` event: raised for all parsed datagrams (Story 2.7 subscribes here).

**Story 1.2 (IdentityKeyedSortedCollection):**
- `Add(item)` — inserts at sorted position; throws on duplicate identity.
- `Update(item)` — re-sorts by new sort key; emits `Move(oldIdx, newIdx)` if position changed, emits NOTHING if unchanged. NEVER Remove+Add.
- `Remove(id)` — removes by identity key; returns false if absent (no throw).
- `TryGetItem(id, out item)` — safe lookup by identity.
- The collection is NOT thread-safe; all mutations via `IUiDispatcher.Post` (Pattern 9 + Decision 1).

**Story 2.3 review learnings:**
- Dispose linked `CancellationTokenSource` after use.
- VSTHRD003 suppression for fire-and-forget Task fields is established precedent (`#pragma warning disable/restore VSTHRD003`).

### Code-style + pattern compliance

- **Pattern 1:** file-scoped namespaces; `_camelCase` backing fields; `Async` suffix.
- **Pattern 2:** `ohSpy.Core/ViewModels/` types MUST NOT reference `Microsoft.UI.*`, `Microsoft.Windows.*`, or `WinRT.Interop.*`. `CommunityToolkit.Mvvm` is safe (targets .NET Standard, no WinUI dependency).
- **Pattern 6:** `ConfigureAwait(false)` on every `await` in Core (ShellViewModel.RunStartAsync, DisposeAsync). In App project, ConfigureAwait is NOT needed (UI context capture is desired for UI code — but ShellViewModel is in Core, so use it there).
- **Pattern 7:** `ShellViewModel` registered as singleton. `DeviceTreeViewModel` constructed by `ShellViewModel` (not in DI — per-ViewModel factory pattern, only the root orchestrator is in DI).
- **Pattern 9:** `ObservableObject` base from CommunityToolkit; `[ObservableProperty]` for bindable fields; `partial class` required for source-gen.
- **Pattern 13:** `x:Bind` in DataTemplates; `x:DataType` set; code-behind constructor-only; resource keys PascalCase.
- **Pattern 14/15 + A2:** test names `Method_Scenario_Expected_AC25n`; `[Trait("ac", "AC-2.5.<n>")]`.

### Anti-patterns to avoid

- **Don't call `Devices.Add/Remove/Update` off the UI thread.** All DeviceRegistry event handlers run on the UI thread, but double-check dispatch if ever called from background code.
- **Don't use `Remove(uuid) + Add(newVm)` to handle DeviceUpdated.** Use `vm.RefreshFrom(entry); Devices.Update(vm)`. The `Update` path emits a `Move` which preserves expand/selection state.
- **Don't put logic in MainWindow.xaml.cs.** Pattern 13: constructor-only. Any behavior should be in ShellViewModel or DeviceTreeViewModel.
- **Don't use `Binding` where `x:Bind` is possible.** Exception: `Style.Setter.Value` — WinUI 3 limitation requires classic Binding there.
- **Don't forget `Mode=OneWay` on `x:Bind` for collection/observable properties.** Default is `OneTime` — omitting Mode causes the tree not to update when devices appear.
- **Don't add `[ObservableProperty]` to `Kind` or `KindGlyph`.** These are fixed constants (not observable); a computed getter is sufficient.
- **Don't use `CommunityToolkit.Mvvm.ComponentModel.ObservableObject` in `ohSpy.App` types.** ViewModels are in Core; App types are code-behind only (Pattern 13).

### Forward-looking dependencies

| Story | What it consumes from 2.5 |
|---|---|
| 2.6 (Expand: Service/Action) | `DeviceNodeViewModel.IsExpanded` partial hook; `ReplaceWith(...)` method; `Children: ObservableCollection<INodeViewModel>` |
| 2.7 (SSDP log) | `IDiscoveryService.AnnouncementReceived` event (already shaped in 2.4); right pane `Border` to be replaced with `SsdpLogView` |
| 2.9 (Properties popup) | `ShellViewModel` reference to `MainWindow` for `IWindowOwnershipManager.Adopt(popup, _shellWindow)` |
| 5.2 (Adapter switch) | `ShellViewModel.SwitchAdapterAsync(adapter)` + `StartAsync`/teardown lifecycle management |

### Architecture amendments to anticipate

- **A32 (likely):** The architecture's directory listing shows `MainPage.xaml` in the project root. After Story 2.5 removes MainPage, update the architecture tree to remove it. Also document that `NodeDataTemplateSelector.cs` is in `Converters/`.
- **A33 (speculative):** `ShellViewModel.StartAsync` returns `Task.CompletedTask` synchronously after fire-and-forgetting `RunStartAsync`. If the architecture's decision diagram for "app startup sequence" shows a synchronous path, it may need a note that the AdapterScope start is now fire-and-forget from ShellViewModel.

### Project structure notes

**New files (10 source + 2 test):**
```
src/ohSpy.Core/
└── ViewModels/
    ├── INodeViewModel.cs            ← Task 2 NEW (interface)
    ├── NodeKind.cs                  ← Task 2 NEW (enum)
    ├── LoadingPlaceholderViewModel.cs  ← Task 2 NEW
    ├── InlineErrorViewModel.cs      ← Task 2 NEW
    ├── DeviceNodeViewModel.cs       ← Task 3 NEW (partial, ObservableObject)
    ├── DeviceTreeViewModel.cs       ← Task 4 NEW (sealed)
    └── ShellViewModel.cs            ← Task 5 NEW (sealed partial, IAsyncDisposable)

src/ohSpy.App/
└── Converters/
    └── NodeDataTemplateSelector.cs  ← Task 8 NEW

tests/ohSpy.Core.Tests/
└── ViewModels/
    ├── DeviceNodeViewModelTests.cs  ← Task 10 NEW
    └── DeviceTreeViewModelTests.cs  ← Task 11 NEW
```

**Modified files (7):**
- `src/ohSpy.Core/ohSpy.Core.csproj` — add CommunityToolkit.Mvvm PackageReference
- `src/ohSpy.App/App.xaml` — add MutedForegroundBrush resource
- `src/ohSpy.App/App.xaml.cs` — migrate AdapterScope to ShellViewModel; _shellVm field
- `src/ohSpy.App/Composition/ServiceRegistration.cs` — ShellViewModel singleton
- `src/ohSpy.App/MainWindow.xaml` — two-column shell layout; TreeView
- `src/ohSpy.App/MainWindow.xaml.cs` — inject ShellViewModel; ViewModel property

**Deleted files (2):**
- `src/ohSpy.App/MainPage.xaml`
- `src/ohSpy.App/MainPage.xaml.cs`

### Testing standards summary

- xUnit + FluentAssertions 7.2.0. `[Trait("ac", "AC-2.5.<n>")]` per AC-traceable test.
- **No new chaos tests** (chaos suite stays at 1). VM/tree tests are fast unit tests.
- **`InlineUiDispatcher`** for synchronous `Post` in tree VM tests.
- **`DeviceRegistry` real instance** for DeviceTreeViewModel integration-style tests (registry is lightweight, no real sockets needed).
- **XAML tests not automated** — WinUI 3 visual rendering is manual smoke test only (Task 12.5).
- **Target: ~249 tests** (229 baseline + ~20).

### References

> Authoritative paths:
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md`
>   - Pattern 13 (XAML conventions) ~line 1947
>   - Pattern 4 (MVVM naming) ~line 1772
>   - Amendment A1 (LoadingPlaceholderViewModel) ~line 2389
>   - Amendment A26 (App-lifetime disposable / AdapterScope migration) ~line 2893
>   - Directory layout ~line 2051
>   - Decision 6 (IdentityKeyedSortedCollection) ~line 680
> - Epics: `_bmad-output/planning-artifacts/epics.md` (Story 2.5 lines 991–1059)
> - Previous story: `_bmad-output/implementation-artifacts/2-4-ssdp-parser-discoveryservice-wire-transport-into-registry.md`

- [Source: epics.md#Story-2.5] — verbatim ACs (lines 991–1059).
- [Source: architecture.md §Pattern-13] — XAML conventions; `x:Bind`; code-behind constructor-only; MutedForegroundBrush.
- [Source: architecture.md §Pattern-4] — MVVM naming; ObservableObject base; [ObservableProperty]; partial class.
- [Source: architecture.md §Amendment-A1] — LoadingPlaceholderViewModel + InlineErrorViewModel contract.
- [Source: architecture.md §Amendment-A26] — AdapterScope migration from App to ShellViewModel.
- [Source: architecture.md §Decision-6] — IdentityKeyedSortedCollection; Move semantics; FR-054.
- [Source: src/ohSpy.Core/Collections/IdentityKeyedSortedCollection.cs] — `Add`, `Update`, `Remove`, `TryGetItem` API.
- [Source: src/ohSpy.Core/Devices/IDeviceRegistry.cs] — `DeviceLoaded`, `DeviceUpdated`, `DeviceRemoved` event surface.
- [Source: src/ohSpy.Core/Devices/RegistryEntry.cs] — `Uuid`, `LocationUrl`, `Description`, `DeviceToken`.
- [Source: src/ohSpy.Core/Models/DeviceDescription.cs] — `FriendlyName`, `DeviceType`, `Udn`.
- [Source: src/ohSpy.Core/Discovery/AdapterScope.cs] — constructor, `StartAsync`, `CurrentAdapterIPv4`, `AdapterToken`, `DisposeAsync`.
- [Source: src/ohSpy.Core/Discovery/DiscoveryService.cs] — `StartAsync(adapterToken, ct)`.
- [Source: src/ohSpy.App/App.xaml.cs] — existing `StartAdapterScopeAsync` pattern + `ShutdownAsync` ordering (to be replaced/updated).
- [Source: src/ohSpy.App/MainWindow.xaml] + [Source: src/ohSpy.App/MainWindow.xaml.cs] — current state; to be replaced.
- [Source: Directory.Packages.props] — `CommunityToolkit.Mvvm` Version 8.4.0 already pinned.
- [Source: tests/ohSpy.Core.Tests/Architecture/CoreAppBoundaryTests.cs] — `Microsoft.UI.*` ban on Core must still pass after CommunityToolkit.Mvvm is added.
- [Source: tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs] — synchronous Post double for VM tests.
- [Source: 2-4-…md §Dev-Agent-Record] — Story 2.4 completion notes (229 test baseline, 2 skips).
- [Source: project_ohspy memory] — native Windows desktop UPnP inspector; raw-BCL UPnP; no CI (pre-commit chaos hook).

## Dev Agent Record

### Agent Model Used

claude-sonnet-4-6

### Debug Log References

- IDiscoveryService used instead of concrete DiscoveryService in ShellViewModel constructor: DiscoveryService is internal sealed; a public ShellViewModel constructor can't take an internal parameter type. IDiscoveryService exposes the same StartAsync signature — functionally identical.
- CA1822 suppressed on KindGlyph: must be an instance property for x:Bind in WinUI DataTemplate; making it static would break the binding.
- DeviceTreeViewModelTests: initial AddLoadedDevice used RaiseDeviceLoaded only (bypassing OnAlive), so registry._entries never had the entry and OnByebye was a no-op. Fixed by calling OnAlive first so the entry lands in _entries.
- XAML compiler warning WMC1506 on MainWindow.xaml line 92 (x:Bind ViewModel.DeviceTree.Devices): benign — ViewModel property is set-once in constructor, DeviceTree is [ObservableProperty], Devices is INotifyCollectionChanged. XAML compiler can't statically verify the chain. Build succeeds, 0 errors.

### Completion Notes List

- Added CommunityToolkit.Mvvm 8.4.0 to ohSpy.Core — CoreAppBoundaryTests confirm no WinUI dependency introduced.
- Created 7 Core ViewModels: NodeKind, INodeViewModel, LoadingPlaceholderViewModel, InlineErrorViewModel, DeviceNodeViewModel, DeviceTreeViewModel, ShellViewModel.
- DeviceNodeViewModel: partial ObservableObject with [ObservableProperty] FriendlyName/SecondaryDetail/IsExpanded; LoadingPlaceholder child forces expand chevron; ReplaceWith helper shapes Story 2.6 API surface.
- DeviceTreeViewModel: subscribes to IDeviceRegistry.DeviceLoaded/Updated/Removed; marshals all mutations via IUiDispatcher.Post; uses IdentityKeyedSortedCollection + DeviceNodeComparer (case-insensitive, UUID tiebreak per FR-054).
- ShellViewModel: IAsyncDisposable; owns AdapterScope (Amendment A26 migration); StartAsync fire-and-forgets RunStartAsync; DiscoveryService started via IDiscoveryService interface.
- App.xaml.cs: removed _adapterScope field + StartAdapterScopeAsync; added _shellVm; passes ShellViewModel to MainWindow constructor.
- MainWindow: two-column shell with TreeView (Devices) + placeholder Border; NodeDataTemplateSelector routes DeviceNodeViewModel vs INodeViewModel fallback; TreeView.ItemContainerStyle uses classic Binding for Children (WinUI 3 Style.Setter limitation).
- MainPage.xaml + MainPage.xaml.cs deleted (WinUI template scaffolding, superseded).
- MutedForegroundBrush (#FF767676) added to App.xaml app-level resources.
- Tests: 18 new tests (10 DeviceNodeViewModel + 8 DeviceTreeViewModel). 229 → 247 passing, 2 skips unchanged, chaos suite 1.

### File List

src/ohSpy.Core/ohSpy.Core.csproj
src/ohSpy.Core/ViewModels/NodeKind.cs
src/ohSpy.Core/ViewModels/INodeViewModel.cs
src/ohSpy.Core/ViewModels/LoadingPlaceholderViewModel.cs
src/ohSpy.Core/ViewModels/InlineErrorViewModel.cs
src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs
src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs
src/ohSpy.Core/ViewModels/ShellViewModel.cs
src/ohSpy.App/App.xaml
src/ohSpy.App/App.xaml.cs
src/ohSpy.App/Composition/ServiceRegistration.cs
src/ohSpy.App/Converters/NodeDataTemplateSelector.cs
src/ohSpy.App/MainWindow.xaml
src/ohSpy.App/MainWindow.xaml.cs
src/ohSpy.App/MainPage.xaml (DELETED)
src/ohSpy.App/MainPage.xaml.cs (DELETED)
tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs
tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs

## Change Log

- Story 2.5 implemented (Date: 2026-06-03): Main window shell + device tree top-level rows. Added CommunityToolkit.Mvvm; created ViewModels layer (NodeKind, INodeViewModel, Loading/Error placeholders, DeviceNodeViewModel, DeviceTreeViewModel, ShellViewModel); migrated AdapterScope from App.xaml.cs to ShellViewModel (Amendment A26); replaced MainWindow Frame-navigation with two-column shell (TreeView + placeholder); deleted MainPage scaffolding; added MutedForegroundBrush; 18 new tests (229 → 247 passing).
