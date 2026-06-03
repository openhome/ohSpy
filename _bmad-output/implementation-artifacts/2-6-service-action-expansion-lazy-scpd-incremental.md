---
baseline_commit: 8172c8e3474596af41059e24b561afd45d430db2
---

# Story 2.6: Service & Action Expansion (Lazy SCPD, Incremental)

Status: review

## Story

As a Linn engineer,
I want to expand a device row to see its services (immediate, from the eager-fetched description) and expand a service to see its actions (lazily fetched on first expand, streamed incrementally),
so that I can navigate to any action on any device without the UI freezing on a 200-action IGD-router SCPD.

## Acceptance Criteria

**Verbatim ACs from epics.md §Story 2.6 (lines 1062–1119). AC trait IDs follow Amendment A2; this story assigns the numbers AC-2.6.1 … AC-2.6.8 to the eight `Given/When/Then` blocks below.**

**AC-2.6.1 — ServiceNodeViewModel shape**

**Given** `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`
**When** I inspect it
**Then** it wraps a `ServiceDescription` (extracted by `DeviceDescriptionParser` in Story 1.4)
**And** exposes `[ObservableProperty] Label` (typically the `<serviceType>` tail after `:service:`, e.g. `MediaRenderer:1` — pick the more readable; consistency with the prior tool UpnpSpy preferred — see Dev Notes §"Service label")
**And** `Kind => NodeKind.Service` (FR-045)
**And** `Children` is initialised in the constructor to `[ new LoadingPlaceholderViewModel() ]` (AC-A1.2 + FR-044)
**And** an `[ObservableProperty] IsExpanded` triggers `LoadActionsAsync` on first transition to `true`

**AC-2.6.2 — DeviceNodeViewModel expand → services (synchronous, no fetch)**

**Given** `DeviceNodeViewModel.IsExpanded` transitions to `true`
**When** the expansion happens (Story 2.5 wired the empty stub; this story implements the real handler)
**Then** the device's children are replaced atomically via `ReplaceWith([... new ServiceNodeViewModel(s) for s in entry.Description.Services])` (AC-A1.4 — single `INotifyCollectionChanged` notification; chevron does NOT collapse mid-expand; NFR-UI3)
**And** the service list is `entry.Description.Services` — already recursively flattened across embedded children by `DeviceDescriptionParser` (FR-011 + FR-053 — embedded children's services appear as the root's services in the tree)
**And** no HTTP fetch is triggered by the expand (FR-011 — the description was eager-fetched in Story 2.3)
**And** the replacement runs exactly once: a collapse + re-expand does NOT rebuild the service list (guards the chevron-collapse failure mode deferred from Story 2.5 — see Dev Notes §"Resolving the deferred ReplaceWith hazard")

**AC-2.6.3 — Lazy SCPD fetch + incremental action stream**

**Given** `ServiceNodeViewModel.LoadActionsAsync` runs on first expand
**When** the SCPD URL is fetched
**Then** the call uses `IUpnpHttpClient.FetchScpdAsync(scpdUrl, deviceToken)` (NFR-P2 timeout applies; `scpdUrl` is `service.ScpdUrl` resolved against the device `LocationUrl` — see Dev Notes §"SCPD URL resolution")
**And** on success, the returned `byte[]` is wrapped in a `using MemoryStream` and passed to `IScpdParser.StreamActionsAsync`
**And** the consumer loop is `await foreach (var action in parser.StreamActionsAsync(stream, deviceToken)) { _services.Ui.Post(() => /* remove placeholder on first action, then */ Children.Add(new ActionNodeViewModel(action))); }` (FR-100 incremental — actions appear in the tree as they parse)
**And** the placeholder is removed in the same `Post` that adds the first action; subsequent actions append directly to `Children` so the operator sees actions stream in one-by-one (see Dev Notes §"Incremental placeholder removal" for the chosen semantic)

**AC-2.6.4 — SCPD fetch/parse failure → inline error**

**Given** the SCPD fetch fails (timeout, transport, protocol/parse)
**When** the failure is observed
**Then** the service node's `Children` is replaced via `ReplaceWith([ new InlineErrorViewModel(message) ])` (FR-013 + AC-A1.5), marshalled through `_services.Ui.Post`
**And** a `Warning` diagnostic is emitted: `DiagCategories.ScpdFetch` for fetch failures (`UpnpTimeoutException` / `UpnpTransportException`) and `DiagCategories.ScpdParse` for `UpnpProtocolException`, each with `DeviceUuid` + `Url` populated per Pattern 11 (`ScpdParse` also carries `ErrorText`)

**AC-2.6.5 — Large-SCPD streaming performance**

**Given** a large SCPD (the streaming-order test uses `tests/.../Fixtures/Scpds/linn-ds-5action.xml`; the 100-action perf target is verified as a non-gating manual smoke — see Dev Notes §"Large-SCPD fixture")
**When** the operator expands the service
**Then** the service node shows "Loading…" immediately (AC-5.1 streaming behaviour)
**And** the first action appears in the tree promptly (sub-second on a LAN)
**And** the full action list is visible within ≤ 2 s (Performance Budget "Cold large-SCPD expand")
**And** no UI-thread stall > 16 ms occurs during the parse (NFR-UI4 + AC-5.1 — guaranteed by `XmlReaderScpdParser`'s `await Task.Yield()` between actions + per-action `Post`)

**AC-2.6.6 — No re-fetch on collapse/re-expand**

**Given** a service that has already been expanded
**When** the operator collapses then re-expands it
**Then** no re-fetch is issued (the action list is retained); the chevron toggles state cleanly (NFR-UI3)

**AC-2.6.7 — ActionNodeViewModel shape (leaf)**

**Given** `src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs`
**When** I inspect it
**Then** it wraps a `ScpdAction` and exposes `[ObservableProperty] Label` (the action name)
**And** `Kind => NodeKind.Action` (FR-045)
**And** `Children` is EMPTY (FR-044 second consequence + AC-A1.3)
**And** the XAML template does NOT render an expand chevron for `ActionNodeViewModel` instances (verified via manual UI inspection — falls out of the empty `Children` binding)

**AC-2.6.8 — Cancellation mid-parse**

**Given** a service node whose device cancels mid-parse (byebye, adapter switch → `DeviceCts` cancels)
**When** the `deviceToken` cancels during the fetch or the `await foreach`
**Then** the loop throws `OperationCanceledException` (AC-5.4)
**And** the partial action list previously emitted is discarded along with the node itself (the device is being removed from `Devices`, dropping its whole subtree)
**And** NO diagnostic is emitted for the cancellation (cancellation is not a fault — `OperationCanceledException` is caught and swallowed, distinct from the `UpnpException` failure path)

---

## Tasks / Subtasks

### Task 1 — NodeServices dependency bundle (AC: #2, #3, #4)

The tree-node VMs are constructed with `new` (not from DI), but `ServiceNodeViewModel.LoadActionsAsync` needs `IUpnpHttpClient`, `IScpdParser`, `IUiDispatcher`, and `IDiagnosticEmitter`. Thread them through a single immutable bundle from DI → `ShellViewModel` → `DeviceTreeViewModel` → `DeviceNodeViewModel` → `ServiceNodeViewModel`. This avoids a per-VM 4-arg constructor explosion and keeps `Core` DI-free (the bundle is constructed once, in the composition root).

- [x] **1.1** Create `src/ohSpy.Core/ViewModels/NodeServices.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using ohSpy.Core.Diagnostics;
  using ohSpy.Core.Http;
  using ohSpy.Core.Scpd;
  using ohSpy.Core.Threading;

  /// <summary>
  /// Immutable bundle of the Core services a tree-node ViewModel needs to lazily fetch and
  /// parse an SCPD on expand (Story 2.6). Constructed once in the composition root and
  /// threaded DeviceTree → DeviceNode → ServiceNode so node VMs (created via `new`, not DI)
  /// can reach the HTTP client, parser, dispatcher, and diagnostic emitter without each
  /// taking four constructor arguments.
  /// </summary>
  public sealed record NodeServices(
      IUpnpHttpClient Http,
      IScpdParser ScpdParser,
      IUiDispatcher Ui,
      IDiagnosticEmitter Diag);
  ```
  All four members are DI-registered interfaces, so the record's primary constructor resolves cleanly when registered (Task 6).

### Task 2 — ServiceNodeViewModel (AC: #1, #3, #4, #5, #6, #8)

- [x] **2.1** Create `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`. Implements `INodeViewModel`, extends `ObservableObject`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using System.Collections.ObjectModel;
  using CommunityToolkit.Mvvm.ComponentModel;
  using ohSpy.Core.Diagnostics;
  using ohSpy.Core.Http;
  using ohSpy.Core.Models;

  public partial class ServiceNodeViewModel : ObservableObject, INodeViewModel
  {
      private readonly ServiceDescription _service;
      private readonly Uri _deviceLocation;   // device LocationUrl — base for relative SCPDURL
      private readonly Guid _deviceUuid;       // for the FR-041 Identity column on diagnostics
      private readonly CancellationToken _deviceToken; // D7 device-level cancellation (byebye/adapter switch)
      private readonly NodeServices _services;

      private int _loadStarted; // 0 = not started, 1 = started (Interlocked guard — AC-2.6.6)

      [ObservableProperty] private string _label = "";
      [ObservableProperty] private bool _isExpanded;

      public NodeKind Kind => NodeKind.Service;

      // FR-045 service glyph. See Dev Notes §"Glyphs" — verify in the Segoe MDL2 chart.
  #pragma warning disable CA1822
      public string KindGlyph => ""; // Settings / "service config"
  #pragma warning restore CA1822

      public ObservableCollection<INodeViewModel> Children { get; } = [];

      public ServiceNodeViewModel(
          ServiceDescription service, Uri deviceLocation, Guid deviceUuid,
          CancellationToken deviceToken, NodeServices services)
      {
          _service = service;
          _deviceLocation = deviceLocation;
          _deviceUuid = deviceUuid;
          _deviceToken = deviceToken;
          _services = services;
          Label = ComputeLabel(service);
          Children.Add(new LoadingPlaceholderViewModel()); // AC-A1.2: force expand chevron
      }

      // Service label: prefer the <serviceType> tail after ":service:" (e.g. "MediaRenderer:1"),
      // falling back to the verbatim serviceType, then serviceId. Mirrors the device row's
      // FR-051 ":device:" tail style for visual symmetry. See Dev Notes §"Service label".
      private static string ComputeLabel(ServiceDescription service)
      {
          const string marker = ":service:";
          var type = service.ServiceType ?? "";
          var idx = type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
          if (idx >= 0)
          {
              var tail = type[(idx + marker.Length)..];
              if (tail.Length > 0) return tail;
          }
          if (type.Length > 0) return type;
          return service.ServiceId ?? "(service)";
      }
  ```
- [x] **2.2** `IsExpanded` partial hook fires the lazy load exactly once on the first `true` transition (AC-2.6.1 + AC-2.6.6):
  ```csharp
  partial void OnIsExpandedChanged(bool value)
  {
      if (!value) return; // collapse: no-op (retain loaded actions — AC-2.6.6)
      if (Interlocked.Exchange(ref _loadStarted, 1) == 1) return; // already loading/loaded
      _ = LoadActionsAsync(); // fire-and-forget (EagerDescriptionDispatcher precedent); all
                              // exceptions handled inside; deviceToken drives teardown.
  }
  ```
- [x] **2.3** `LoadActionsAsync` — fetch, stream, marshal each action to the UI thread (AC-2.6.3, #4, #5, #8):
  ```csharp
  private async Task LoadActionsAsync()
  {
      var scpdUrl = new Uri(_deviceLocation, _service.ScpdUrl); // resolves relative OR absolute
      try
      {
          var bytes = await _services.Http.FetchScpdAsync(scpdUrl, _deviceToken).ConfigureAwait(false);
          using var stream = new MemoryStream(bytes); // caller owns stream lifetime (IScpdParser contract)

          var first = true;
          await foreach (var action in _services.ScpdParser
              .StreamActionsAsync(stream, _deviceToken).ConfigureAwait(false))
          {
              var node = new ActionNodeViewModel(action);
              if (first)
              {
                  first = false;
                  _services.Ui.Post(() => { Children.Clear(); Children.Add(node); }); // drop placeholder + first action atomically
              }
              else
              {
                  _services.Ui.Post(() => Children.Add(node)); // incremental append (FR-100)
              }
          }

          if (first) // streamed zero actions — clear the placeholder (empty service, no chevron)
              _services.Ui.Post(() => Children.Clear());
      }
      catch (OperationCanceledException)
      {
          // AC-2.6.8: device removed mid-parse. Node is being dropped; emit nothing.
      }
      catch (UpnpProtocolException ex)
      {
          EmitFailure(DiagCategories.ScpdParse, scpdUrl, ex.Message);
      }
      catch (UpnpException ex) // timeout + transport
      {
          EmitFailure(DiagCategories.ScpdFetch, scpdUrl, ex.Message);
      }
  }

  private void EmitFailure(string category, Uri url, string message)
  {
      _services.Diag.Warning(category, "SCPD load failed",
          new DiagnosticContext { DeviceUuid = _deviceUuid, Url = url.ToString(), ErrorText = message });
      _services.Ui.Post(() => ReplaceWith([new InlineErrorViewModel(message)]));
  }

  internal void ReplaceWith(IReadOnlyList<INodeViewModel> newChildren)
  {
      Children.Clear();
      foreach (var child in newChildren) Children.Add(child);
  }

  string INodeViewModel.Label => Label; // explicit impl returns the observable Label
  ```
  Notes the dev must honour:
  - `deviceToken` (not a fresh CTS) is passed to BOTH `FetchScpdAsync` and `StreamActionsAsync` so a byebye / adapter switch cancels in-flight work (Decision 7 / AC-2.6.8). Collapse does NOT cancel (AC-2.6.6).
  - `UpnpProtocolException` is a subclass of `UpnpException`, so its `catch` MUST precede the `UpnpException` catch (ordering matters).
  - The per-action `Post` + the parser's internal `await Task.Yield()` together satisfy the "no UI stall > 16 ms" budget (AC-2.6.5) — do NOT batch actions into one `Post`.

### Task 3 — ActionNodeViewModel (AC: #7)

- [x] **3.1** Create `src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs`:
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using System.Collections.ObjectModel;
  using CommunityToolkit.Mvvm.ComponentModel;
  using ohSpy.Core.Models;

  public partial class ActionNodeViewModel : ObservableObject, INodeViewModel
  {
      private readonly ScpdAction _action;

      [ObservableProperty] private string _label = "";

      public NodeKind Kind => NodeKind.Action;

      // FR-045 action glyph. See Dev Notes §"Glyphs" — verify in the Segoe MDL2 chart.
  #pragma warning disable CA1822
      public string KindGlyph => ""; // Code / "callable method"
  #pragma warning restore CA1822

      // AC-A1.3 / AC-2.6.7: actions are leaves — empty children, no chevron.
      public ObservableCollection<INodeViewModel> Children { get; } = [];

      public ActionNodeViewModel(ScpdAction action)
      {
          _action = action;
          Label = action.Name;
      }

      string INodeViewModel.Label => Label;
  }
  ```
  `Children` is exposed (empty) so the shared `TreeView.ItemContainerStyle` `{Binding Children}` setter resolves to an empty source → WinUI renders no chevron (AC-2.6.7). Keep `_action` even though only `Name` is surfaced today — Story 3.2 (invocation popup) reads its argument lists off the same VM.

### Task 4 — DeviceNodeViewModel: implement the real expand handler (AC: #2)

Edit `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs` (UPDATE — Story 2.5 created it). Current state: constructor takes `(RegistryEntry entry)`, holds an empty `OnIsExpandedChanged` stub, exposes `ReplaceWith`.

- [x] **4.1** Thread `NodeServices` into the constructor and store it + a once-guard:
  ```csharp
  private readonly NodeServices _services;
  private int _servicesBuilt; // 0 = not built, 1 = built (Interlocked guard — AC-2.6.2)

  public DeviceNodeViewModel(RegistryEntry entry, NodeServices services)
  {
      _entry = entry;
      _services = services;
      Children.Add(new LoadingPlaceholderViewModel()); // AC-A1.1: force expand chevron
      RefreshFrom(entry);
  }
  ```
- [x] **4.2** Replace the empty `OnIsExpandedChanged` stub with the real handler:
  ```csharp
  // AC-2.6.2: first expand swaps the placeholder for the (already-loaded, flattened)
  // service list — synchronous, no HTTP. Build once: re-expand must NOT rebuild (that would
  // emit a second Reset and collapse any expanded service subtrees — the Story 2.5 deferral).
  partial void OnIsExpandedChanged(bool value)
  {
      if (!value) return;
      if (Interlocked.Exchange(ref _servicesBuilt, 1) == 1) return;

      var services = _entry.Description?.Services ?? [];
      var nodes = services
          .Select(s => (INodeViewModel)new ServiceNodeViewModel(
              s, _entry.LocationUrl, _entry.Uuid, _entry.DeviceToken, _services))
          .ToList();
      ReplaceWith(nodes); // single Reset — AC-A1.4
  }
  ```
  - Runs on the UI thread (the `TreeView` toggles `IsExpanded` on the UI thread), so no `Post` is needed here — the work is synchronous.
  - `_entry.Description` is non-null whenever a `DeviceNodeViewModel` exists (it is only created on `DeviceLoaded`; AC-9.2), but the `?? []` keeps the analyzer + defensive-coding rule happy.
  - If `services` is empty, `ReplaceWith([])` clears the placeholder → the device shows no expandable children. Acceptable (a device with zero services is degenerate).
- [x] **4.3** Leave `ReplaceWith`, `RefreshFrom`, `ComputeSecondaryDetail`, `KindGlyph`, and the `INodeViewModel.Label` explicit impl unchanged. Do NOT re-introduce a Reset hazard: `RefreshFrom` (called on `DeviceUpdated`) must NOT rebuild children — FR-043 guarantees the description (and thus the service list) never changes after `Loaded`, so the once-guard in 4.2 is correct and safe.

### Task 5 — XAML: service + action templates, chevron suppression (AC: #2, #7)

- [x] **5.1** Edit `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs` — add `ServiceTemplate` + `ActionTemplate` and route by concrete type:
  ```csharp
  public DataTemplate DeviceTemplate { get; set; } = null!;
  public DataTemplate ServiceTemplate { get; set; } = null!;
  public DataTemplate ActionTemplate { get; set; } = null!;
  public DataTemplate FallbackTemplate { get; set; } = null!;

  protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
      item switch
      {
          DeviceNodeViewModel  => DeviceTemplate,
          ServiceNodeViewModel => ServiceTemplate,
          ActionNodeViewModel  => ActionTemplate,
          _                    => FallbackTemplate, // Loading / InlineError
      };
  ```
- [x] **5.2** Edit `src/ohSpy.App/MainWindow.xaml` — add the two new templates inside `<local:NodeDataTemplateSelector>` (alongside the existing `DeviceTemplate` and `FallbackTemplate`). Both are a single-row glyph + label, mirroring the device template's first row:
  ```xml
  <local:NodeDataTemplateSelector.ServiceTemplate>
      <DataTemplate x:DataType="vm:ServiceNodeViewModel">
          <StackPanel Orientation="Horizontal">
              <FontIcon
                  VerticalAlignment="Center" Margin="0,0,8,0"
                  FontFamily="Segoe MDL2 Assets" FontSize="14"
                  Glyph="{x:Bind KindGlyph}" />
              <TextBlock
                  Text="{x:Bind Label, Mode=OneWay}"
                  TextTrimming="CharacterEllipsis" />
          </StackPanel>
      </DataTemplate>
  </local:NodeDataTemplateSelector.ServiceTemplate>

  <local:NodeDataTemplateSelector.ActionTemplate>
      <DataTemplate x:DataType="vm:ActionNodeViewModel">
          <StackPanel Orientation="Horizontal">
              <FontIcon
                  VerticalAlignment="Center" Margin="0,0,8,0"
                  FontFamily="Segoe MDL2 Assets" FontSize="14"
                  Glyph="{x:Bind KindGlyph}" />
              <TextBlock
                  Text="{x:Bind Label, Mode=OneWay}"
                  TextTrimming="CharacterEllipsis" />
          </StackPanel>
      </DataTemplate>
  </local:NodeDataTemplateSelector.ActionTemplate>
  ```
  The `TreeView.ItemContainerStyle` setter (`ItemsSource="{Binding Children}"`) is UNCHANGED — it already binds every node's `Children`. `ActionNodeViewModel.Children` is empty → no chevron (AC-2.6.7); `ServiceNodeViewModel.Children` has the placeholder → chevron shown (AC-A1.2).
- [x] **5.3** Manual verification (non-AC-gating, recorded in Dev Agent Record): expand a device → services appear with the service glyph; expand a service → "Loading…" flashes, then actions stream in with the action glyph and NO expand chevron on action rows.

### Task 6 — DI: register NodeServices, thread it through the VM graph (AC: #2, #3)

- [x] **6.1** In `src/ohSpy.App/Composition/ServiceRegistration.cs`, before the `ShellViewModel` registration, add:
  ```csharp
  // Story 2.6 — NodeServices bundle: the Core services the tree-node VMs need to lazily
  // fetch + parse an SCPD on expand. All four members are already-registered singletons;
  // the bundle itself is a stateless singleton threaded into the VM graph by ShellViewModel.
  services.AddSingleton<NodeServices>();
  ```
  `NodeServices`'s record primary constructor resolves `IUpnpHttpClient`, `IScpdParser`, `IUiDispatcher`, `IDiagnosticEmitter` — all registered earlier in this method.
- [x] **6.2** Edit `src/ohSpy.Core/ViewModels/ShellViewModel.cs` — take `NodeServices` and pass it to `DeviceTreeViewModel`:
  ```csharp
  public ShellViewModel(
      INetworkAdapterEnumerator adapterEnum,
      ISsdpTransport transport,
      IDiscoveryService discovery,
      IDeviceRegistry registry,
      IUiDispatcher ui,
      IDiagnosticEmitter diag,
      NodeServices nodeServices)          // NEW
  {
      _adapterEnum = adapterEnum;
      _transport   = transport;
      _discovery   = discovery;
      _diag        = diag;
      _deviceTree  = new DeviceTreeViewModel(registry, ui, nodeServices); // NEW arg
  }
  ```
- [x] **6.3** Edit `src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs` — take `NodeServices`, store it, pass it when constructing each `DeviceNodeViewModel`:
  ```csharp
  private readonly NodeServices _nodeServices;

  public DeviceTreeViewModel(IDeviceRegistry registry, IUiDispatcher ui, NodeServices nodeServices)
  {
      _registry = registry;
      _ui = ui;
      _nodeServices = nodeServices;
      Devices = new IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>(
          vm => vm.Uuid, DeviceNodeComparer.Instance);
      registry.DeviceLoaded  += OnDeviceLoaded;
      registry.DeviceUpdated += OnDeviceUpdated;
      registry.DeviceRemoved += OnDeviceRemoved;
  }
  ```
  In `OnDeviceLoaded`, the `Devices.Add(new DeviceNodeViewModel(entry))` call becomes `Devices.Add(new DeviceNodeViewModel(entry, _nodeServices))`. The `RefreshFrom` path on the duplicate-Loaded / Updated branches is unchanged.

### Task 7 — Test fakes (AC: #3, #4, #8)

- [x] **7.1** Extend `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs` to support the SCPD path. Replace the `FetchScpdAsync` body (currently `throw new NotSupportedException()`) with a closure mirroring `DescriptionResponder`:
  ```csharp
  /// <summary>Supplies the SCPD-fetch result. Default: throws NotSupportedException so
  /// tests that don't opt in still fail loudly if the path is hit unexpectedly.</summary>
  public Func<Uri, CancellationToken, Task<byte[]>>? ScpdResponder { get; set; }

  public async Task<byte[]> FetchScpdAsync(Uri scpdUrl, CancellationToken ct)
  {
      if (ScpdResponder is null) throw new NotSupportedException();
      lock (_gate) { _requested.Add(scpdUrl); }
      return await ScpdResponder(scpdUrl, ct).ConfigureAwait(false);
  }
  ```
  Keep the existing `DescriptionResponder` / `RequestedUrls` / `PeakConcurrency` members intact (Story 2.3 tests depend on them).
- [x] **7.2** Create `tests/ohSpy.Core.Tests/Fakes/StubScpdParser.cs` — a controllable `IScpdParser` for deterministic incremental-emission tests (the real parser's timing is non-deterministic):
  ```csharp
  namespace ohSpy.Core.Tests.Fakes;

  using System.Runtime.CompilerServices;
  using ohSpy.Core.Models;
  using ohSpy.Core.Scpd;

  internal sealed class StubScpdParser : IScpdParser
  {
      // Set Actions for a happy-path stream, or Thrower to simulate a parse failure.
      public IReadOnlyList<ScpdAction> Actions { get; set; } = Array.Empty<ScpdAction>();
      public Func<Exception>? Thrower { get; set; }

      public async IAsyncEnumerable<ScpdAction> StreamActionsAsync(
          Stream xml, [EnumeratorCancellation] CancellationToken ct)
      {
          if (Thrower is not null) throw Thrower();
          foreach (var a in Actions)
          {
              ct.ThrowIfCancellationRequested();
              yield return a;
              await Task.Yield();
          }
      }

      public Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct) =>
          throw new NotSupportedException();
  }
  ```
- [x] **7.3** Add a small test helper for a `NodeServices` built from fakes (place near the VM tests, e.g. a private static factory in each test class):
  ```csharp
  private static NodeServices MakeNodeServices(
      StubUpnpHttpClient http, IScpdParser parser, IUiDispatcher ui, IDiagnosticEmitter diag) =>
      new(http, parser, ui, diag);
  ```
  Use `InlineUiDispatcher` (synchronous) and `CapturingDiagnosticEmitter` (already in `Fakes/`).

### Task 8 — Update existing Story 2.5 VM tests for the new constructors (AC: #2)

Adding `NodeServices` to `DeviceTreeViewModel` and `DeviceNodeViewModel` breaks the Story 2.5 test call sites. Update them (no behavioural change to those tests):

- [x] **8.1** `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs` — construct a `NodeServices` in the fixture (http = `new StubUpnpHttpClient()`, parser = `new StubScpdParser()`, ui = the existing `_ui`, diag = `new CapturingDiagnosticEmitter()`) and pass it to `new DeviceTreeViewModel(_registry, _ui, nodeServices)`.
- [x] **8.2** `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs` — every `new DeviceNodeViewModel(entry)` becomes `new DeviceNodeViewModel(entry, nodeServices)`. Add a shared `private static readonly NodeServices NodeServices = ...` built from inert fakes (no expand is triggered in the 2.5 tests, so the stubs are never invoked).

### Task 9 — Tests: ServiceNodeViewModel (AC: #1, #3, #4, #6, #8)

**Location:** `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs`. Trait every test `[Trait("ac", "AC-2.6.<n>")]`. Use `InlineUiDispatcher` for synchronous dispatch so `Post` runs inline and assertions are deterministic.

- [x] **9.1** `Constructor_InitializesPlaceholderChild_ACA12` — `Children.Count == 1`, first child is `LoadingPlaceholderViewModel`. `[Trait ac AC-2.6.1]`
- [x] **9.2** `Constructor_KindIsService_AC261` — `Kind == NodeKind.Service`.
- [x] **9.3** `Label_FromServiceTypeTail_AC261` — `ServiceType = "urn:schemas-upnp-org:service:MediaRenderer:1"` → `Label == "MediaRenderer:1"`. Add a fallback case: empty serviceType + `ServiceId = "urn:...:serviceId:Foo"` → `Label` falls back per `ComputeLabel`.
- [x] **9.4** `FirstExpand_HappyPath_StreamsActionsInOrder_RemovesPlaceholder_AC263` — `StubScpdParser.Actions = [a("GetMute"), a("SetMute"), a("GetVolume")]`; `StubUpnpHttpClient.ScpdResponder` returns any bytes; set `IsExpanded = true`; await quiescence; assert `Children` is exactly `[GetMute, SetMute, GetVolume]` as `ActionNodeViewModel`s in order, placeholder gone.
- [x] **9.5** `FirstExpand_RealParser_LinnDs5Action_AC263` — integration with the REAL `XmlReaderScpdParser`: `ScpdResponder` returns `File.ReadAllBytes(.../Fixtures/Scpds/linn-ds-5action.xml)`; expand; assert 5 actions in order: `GetMute, SetMute, GetVolume, SetVolume, VolumeInc`.
- [x] **9.6** `Expand_FetchThrowsTimeout_ShowsInlineError_EmitsScpdFetchWarning_AC264` — `ScpdResponder` throws `new UpnpTimeoutException(url, budget, elapsed)`; expand; assert `Children` is `[InlineErrorViewModel]`, and `CapturingDiagnosticEmitter` recorded a `Warning` with category `DiagCategories.ScpdFetch`, `DeviceUuid` + `Url` populated.
- [x] **9.7** `Expand_ParserThrowsProtocol_ShowsInlineError_EmitsScpdParseWarning_AC264` — `ScpdResponder` returns bytes; `StubScpdParser.Thrower = () => new UpnpProtocolException(url, "bad xml")`; expand; assert `Children` is `[InlineErrorViewModel]` with the message, and a `Warning` with category `DiagCategories.ScpdParse` + `ErrorText`.
- [x] **9.8** `Expand_Twice_DoesNotRefetch_AC266` — happy path; set `IsExpanded = true`, then `false`, then `true` again; assert `StubUpnpHttpClient.RequestedUrls` contains the SCPD URL exactly once and `Children` still holds the original action nodes.
- [x] **9.9** `Expand_DeviceTokenCancelled_NoError_NoDiagnostic_AC268` — pre-cancel the `deviceToken` (or use a `StubScpdParser` whose stream observes a token cancelled mid-stream); expand; assert `Children` does NOT contain an `InlineErrorViewModel` and `CapturingDiagnosticEmitter` recorded NO entries (cancellation is silent).
- [x] **9.10** `ScpdUrl_RelativeResolvedAgainstLocation_AC263` — `deviceLocation = http://host:49152/desc.xml`, `service.ScpdUrl = "/Foo/Scpd.xml"`; capture the URL passed to `ScpdResponder`; assert it equals `http://host:49152/Foo/Scpd.xml`.

### Task 10 — Tests: ActionNodeViewModel (AC: #7)

**Location:** `tests/ohSpy.Core.Tests/ViewModels/ActionNodeViewModelTests.cs`.

- [x] **10.1** `Constructor_LabelIsActionName_AC267` — `new ActionNodeViewModel(new ScpdAction("Play", [], []))` → `Label == "Play"`.
- [x] **10.2** `Kind_IsAction_AC267` — `Kind == NodeKind.Action`.
- [x] **10.3** `Children_IsEmpty_ACA13` — `Children.Count == 0` (leaf; no placeholder, so the XAML renders no chevron).

### Task 11 — Tests: DeviceNodeViewModel expand (AC: #2)

**Location:** extend `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs`.

- [x] **11.1** `Expand_ReplacesPlaceholderWithServiceNodes_AC262` — build a `RegistryEntry` whose `Description.Services` has 2 services; `new DeviceNodeViewModel(entry, nodeServices)`; set `IsExpanded = true`; assert `Children` is exactly 2 `ServiceNodeViewModel`s (placeholder gone), in description order.
- [x] **11.2** `Expand_NoHttpFetchTriggered_AC262` — same setup with a `StubUpnpHttpClient` whose `ScpdResponder` is null (would throw if hit); expand the DEVICE only (not the services); assert no exception and `StubUpnpHttpClient.RequestedUrls` is empty (building the service list is synchronous, fetch-free).
- [x] **11.3** `Expand_Twice_DoesNotRebuildServiceList_AC262` — expand; capture the `Children` `ServiceNodeViewModel` instances; collapse (`IsExpanded = false`); re-expand (`true`); assert the SAME `ServiceNodeViewModel` instances remain (once-guard held; no second Reset that would collapse expanded service subtrees — the Story 2.5 deferral).
- [x] **11.4** `Expand_EmptyServiceList_ClearsPlaceholder_AC262` — `Description.Services` empty; expand; assert `Children.Count == 0`.

Use the existing `AddLoaded`-style `RegistryEntry` builder pattern from `DeviceTreeViewModelTests` (internal ctor + `MarkInFlight`/`MarkLoaded`), passing a real `DeviceDescription` with a populated `Services` list (`ServiceDescription("urn:schemas-upnp-org:service:RenderingControl:1", "urn:...:serviceId:RenderingControl", "/RC/Scpd.xml", "/RC/ctrl", "/RC/evt")`).

### Task 12 — Final verification (AC: all)

- [x] **12.1** `dotnet build` — 0 errors / 0 warnings (`TreatWarningsAsErrors` enforced). Watch for: nullable warnings on `_entry.Description`, `CA1822` on `KindGlyph` (suppressed), analyzer flags on the fire-and-forget `_ = LoadActionsAsync()`.
- [x] **12.2** `dotnet test` — all green. Baseline 250 passing (Story 2.5). Story 2.6 adds ~24 tests; target ~274 (plus the 2.5 tests still green after the Task 8 constructor edits).
- [x] **12.3** `dotnet test --filter "category=chaos"` — still exactly **1** (chaos suite unchanged).
- [x] **12.4** `CoreAppBoundaryTests` green — the new Core VM types must not reference `Microsoft.UI.*` / `Microsoft.Windows.*` / `WinRT.Interop.*`. `NodeServices`, `ServiceNodeViewModel`, `ActionNodeViewModel` are pure Core.
- [x] **12.5** Manual smoke (non-AC-gating; record in Dev Agent Record): launch `ohSpy.App`; within ~7 s devices appear; expand a Linn DS → services list appears instantly (no network blip); expand a service → "Loading…" then actions stream in; action rows have no chevron; collapse/re-expand a service → no second fetch (watch the diagnostics file / no new HTTP). If a real 100+-action IGD router is on the LAN, confirm first action is sub-second and the UI never freezes.

---

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **FR-011 / FR-053** | Device expand → flattened service list from the eager description, no fetch | AC-2.6.2 |
| **FR-012 / FR-100** | Service expand → lazy SCPD fetch + incremental action stream | AC-2.6.3, AC-2.6.5 |
| **FR-013 / Amendment A1.5** | SCPD fetch/parse failure → inline `InlineErrorViewModel` | AC-2.6.4 |
| **FR-044 / Amendment A1** | Service node placeholder forces chevron; action node is a leaf (no chevron) | AC-2.6.1, AC-2.6.7 |
| **FR-045** | Service + action kind glyphs in the row templates | AC-2.6.1, AC-2.6.7 |
| **Decision 7** | `deviceToken` threaded into fetch + parse so byebye/adapter-switch cancels mid-stream | AC-2.6.8 |
| **NFR-P2** | `FetchScpdAsync` carries the `ScpdFetch` 10 s operation timeout (baked into the facade) | AC-2.6.3 |
| **NFR-UI3 / NFR-UI4** | Single-Reset `ReplaceWith`, per-action `Post`, parser `Task.Yield` → no flicker, no >16 ms stall | AC-2.6.2, AC-2.6.5 |
| **Pattern 9 / 13** | `ObservableObject` VMs, `x:Bind` templates, `DataTemplateSelector` per node kind | AC-2.6.1, AC-2.6.7 |

### CRITICAL DESIGN DECISIONS

**1. `entry.Description.Services`, NOT `entry.Description.AllServices`.**
The epic AC text says `entry.Description.AllServices`. That member does NOT exist. The real model is `DeviceDescription.Services` (`src/ohSpy.Core/Models/DeviceDescription.cs`), and `DeviceDescriptionParser` ALREADY flattens embedded-child services into it recursively (FR-053 — verified in `DeviceDescriptionParser.ReadEmbeddedDeviceList`). So `entry.Description.Services` is the correct, already-flattened list. Do NOT write any flattening logic in the VM — it is done at parse time.

**2. Dependency threading via `NodeServices` (not DI on the node VMs).**
Node VMs are created with `new` (the registry → tree path), so they can't take DI constructor injection. `ServiceNodeViewModel` needs four Core services to fetch+parse on expand. Bundling them in one `NodeServices` record threaded `ShellViewModel → DeviceTreeViewModel → DeviceNodeViewModel → ServiceNodeViewModel` keeps each constructor small and keeps `Core` free of a service-locator. `NodeServices` is DI-registered (`AddSingleton<NodeServices>()`) and constructed once. This changes the `DeviceTreeViewModel` and `DeviceNodeViewModel` constructor signatures — Task 8 updates the Story 2.5 tests accordingly.

**3. SCPD URL resolution.**
`ServiceDescription.ScpdUrl` is stored verbatim by the parser ("resolution is the caller's concern" — see the type's XML doc). UPnP devices usually emit a relative `SCPDURL` (e.g. `/Volkano/Scpd.xml`). Resolve it against the device's absolute `LocationUrl`: `new Uri(_deviceLocation, _service.ScpdUrl)`. `Uri(baseUri, relativeOrAbsolute)` correctly passes through an absolute `SCPDURL` unchanged, so this one expression handles both forms. (The architecture's `URLBase` element is not captured by the parser; `LocationUrl` is the correct pragmatic base and matches how the prior tool resolved.)

**4. Incremental placeholder removal (the chosen semantic — AC-2.6.3).**
The epic offers two options; only one is genuinely incremental. If actions are buffered into a side `actionsList` and swapped in via `ReplaceWith(actionsList)` only at completion, the operator sees nothing until the whole SCPD is parsed — that defeats FR-100. The chosen semantic streams into the BOUND `Children` collection: keep "Loading…" during the fetch + until the first action; on the first action, `Post(() => { Children.Clear(); Children.Add(first); })` (drops the placeholder + shows the first action in one notification); each subsequent action appends with its own `Post`. Per-action `Post` + the parser's internal `await Task.Yield()` keep each UI-thread slice tiny (NFR-UI4). Empty SCPD (zero actions) → clear the placeholder so no stale "Loading…" lingers.

**5. `deviceToken` is the cancellation scope (Decision 7).**
Pass `_entry.DeviceToken` (captured at `ServiceNodeViewModel` construction) to BOTH `FetchScpdAsync` and `StreamActionsAsync`. On byebye / adapter switch the registry cancels the device's `DeviceCts`; the in-flight `await foreach` throws `OperationCanceledException`, which is caught and SILENTLY swallowed (AC-2.6.8 — cancellation is not a fault). The whole `DeviceNodeViewModel` (with its service subtree) is removed from `Devices` by `OnDeviceRemoved`, so the partial action list is discarded with it. Do NOT create a per-node CTS or cancel on collapse — collapse must retain the loaded list (AC-2.6.6).

**6. Exception catch ordering.**
`UpnpProtocolException : UpnpException`. Catch `OperationCanceledException` first (silent), then `UpnpProtocolException` → `ScpdParse`, then the base `UpnpException` (timeout + transport) → `ScpdFetch`. Reversing the last two would mis-categorise every parse failure as a fetch failure. Do not catch bare `Exception` — let programming errors surface.

**7. Once-guards prevent the Story 2.5 deferral from biting.**
`DeviceNodeViewModel._servicesBuilt` and `ServiceNodeViewModel._loadStarted` are `Interlocked.Exchange` guards. The device's service list is built exactly once (FR-043: the description never changes after `Loaded`, so re-expand has nothing new to show — and a second `ReplaceWith` would emit a Reset that collapses any expanded service subtrees). The service's actions are fetched exactly once (AC-2.6.6). See §"Resolving the deferred ReplaceWith hazard".

**8. Glyphs (FR-045).**
Device = `` (Network — already shipped). Suggested Service = `` (Settings), Action = `` (Code). These are starting suggestions from the Segoe MDL2 Assets PUA range — verify them in Character Map / the official MDL2 glyph chart and adjust for readability before finalising. Placeholders and errors carry NO glyph (the `FallbackTemplate` has no `FontIcon`). Glyphs are fixed per kind → plain getters, NOT `[ObservableProperty]`.

**9. Action chevron suppression is structural, not a flag (AC-2.6.7).**
`ActionNodeViewModel.Children` is an empty `ObservableCollection`. The shared `TreeView.ItemContainerStyle` setter binds `ItemsSource="{Binding Children}"` for every node; an empty source makes WinUI render no expand chevron. So actions get no chevron "for free" — there is no per-template chevron toggle to set. (Confirm via manual UI inspection; it cannot be unit-tested.)

### Resolving the deferred ReplaceWith hazard

`deferred-work.md` (from the Story 2.5 review) flags: *"a second `ReplaceWith` (service-list re-fetch) would collapse expansion."* This story resolves it WITHOUT incremental child reconciliation: the device's service list is built **once** (`_servicesBuilt` guard) because FR-043 guarantees the description is immutable after `Loaded` — there is no legitimate trigger for a second device-level `ReplaceWith`. Likewise the service's actions load **once** (`_loadStarted` guard). The single-Reset-per-node rule (Amendment A1) therefore holds: the only Reset a node ever emits is the placeholder→real-children swap, which happens while the node is mid-expand (empty visible set) — exactly the case A1 declares safe. No `Move`-based child reconciliation is needed at the service/action level.

### What this story does NOT do (scope discipline)

- **Does NOT add context menus / "Fetch SCPD XML"** (right-click — Story 2.8).
- **Does NOT add the invocation popup** (double-click an action — Story 3.2); `ActionNodeViewModel` retains its `ScpdAction` for that later story but exposes only `Label` now.
- **Does NOT read the SCPD state table** (`ReadStateTableAsync` — consumed lazily by the Story 3.x invocation popup, not by tree expansion).
- **Does NOT add subscribe wiring** (`ServiceNodeViewModel.OnSubscribe` — Story 4.x).
- **Does NOT touch the SSDP log right pane** (Story 2.7).
- **Does NOT make node VMs `IDisposable`** — teardown is via `deviceToken` cancellation + parent-collection removal, not per-node disposal.

### Previous-story intelligence

**Story 2.5 (DeviceNodeViewModel / DeviceTreeViewModel / ShellViewModel):**
- `DeviceNodeViewModel` already has the `OnIsExpandedChanged` stub (empty), `ReplaceWith`, `Children` (`ObservableCollection<INodeViewModel>`), and is created in `DeviceTreeViewModel.OnDeviceLoaded`. This story fills the stub + adds the `NodeServices` constructor arg.
- `ReplaceWith` is `Clear()` + `Add` (single Reset). Reused as-is; the once-guards keep it safe.
- `LoadingPlaceholderViewModel` / `InlineErrorViewModel` / `INodeViewModel` / `NodeKind` (incl. `Service`, `Action`) already exist from Story 2.5 — do NOT recreate them.
- The XAML `NodeDataTemplateSelector` + `TreeView.ItemContainerStyle` (`{Binding Children}`) are in place; this story adds two templates + two selector slots.
- Story 2.5 review patches established: store fire-and-forget tasks where lifetime matters; `Interlocked.Exchange` start-guards; `IUiDispatcher.Post` for every cross-thread VM mutation.

**Story 2.3 (DeviceRegistry / RegistryEntry):**
- `RegistryEntry.Description` is non-null iff `State == Loaded` (AC-9.2); a `DeviceNodeViewModel` only exists post-`DeviceLoaded`, so `Description.Services` is safe to read on expand.
- `RegistryEntry.DeviceToken` is a thread-safe snapshot (valid even after the registry disposes `DeviceCts` on removal) — read it freely from the VM.
- `RegistryEntry.LocationUrl` is the absolute SSDP `LOCATION` — the base for SCPD URL resolution.

**Story 1.4 (parsers):**
- `IScpdParser.StreamActionsAsync(Stream, ct)` yields `ScpdAction`s one-by-one, `await Task.Yield()` between each (FR-100), and does NOT dispose the stream (caller owns it → `using var ms`).
- It throws `UpnpProtocolException` (wrapping `XmlException`) on malformed/oversize/XXE; `OperationCanceledException` flows through unwrapped on `ct` cancel.
- `IUpnpHttpClient.FetchScpdAsync(Uri, ct)` returns raw `byte[]` with the `ScpdFetch` 10 s timeout baked in (Amendment A10 — both Fetch methods return bytes); throws `UpnpTimeoutException` / `UpnpTransportException` / `UpnpProtocolException` (oversize).
- Fixtures available: `linn-ds-5action.xml` (5 actions: GetMute, SetMute, GetVolume, SetVolume, VolumeInc), `malformed-mid-document.xml` (parse-failure), `state-table-rich.xml`, `xxe-attempt.xml`.

### Latest tech / library notes

- **CommunityToolkit.Mvvm 8.4.0** (added in Story 2.5, pinned in `Directory.Packages.props`). `[ObservableProperty]` on `_label`/`_isExpanded` generates the public `Label`/`IsExpanded` properties + the `partial void OnIsExpandedChanged(bool)` hook. `partial class` is required for the source generator. `Label` (generated, public get/set) satisfies `INodeViewModel.Label` implicitly for Service/Action; `DeviceNodeViewModel` keeps its EXPLICIT `INodeViewModel.Label => FriendlyName` because its bindable property is `FriendlyName`.
- **`IAsyncEnumerable` + `[EnumeratorCancellation]`** — the `StubScpdParser` fake must annotate its `ct` param with `[EnumeratorCancellation]` (from `System.Runtime.CompilerServices`) exactly as the real parser does, or `ct` won't flow into the iterator.

### Code-style + pattern compliance

- **Pattern 1:** file-scoped namespaces; `_camelCase` backing fields; `Async` suffix on async methods.
- **Pattern 2 (CoreAppBoundaryTests):** `NodeServices`, `ServiceNodeViewModel`, `ActionNodeViewModel` live in `ohSpy.Core` and must NOT reference `Microsoft.UI.*` / `Microsoft.Windows.*` / `WinRT.Interop.*`. Only `CommunityToolkit.Mvvm` + BCL.
- **Pattern 6:** `ConfigureAwait(false)` on EVERY await in Core (`FetchScpdAsync`, the `await foreach`, `Task.Yield` is internal to the parser). CT parameter is `deviceToken`, passed last.
- **Pattern 7:** `NodeServices` singleton; node VMs constructed by their parent VM (per-VM factory, not DI) — only the root `ShellViewModel` + the `NodeServices` bundle are in DI.
- **Pattern 9:** `ObservableObject` base; `[ObservableProperty]`; `partial class`; cross-thread mutations via `IUiDispatcher.Post`.
- **Pattern 11 (diagnostics):** use `DiagCategories.ScpdFetch` / `DiagCategories.ScpdParse` constants (already defined) — no inline category strings. Populate the mandatory context: `DeviceUuid` + `Url` (+ `ErrorText` for `ScpdParse`). `DiagCategoriesUsageTests` + code review enforce this.
- **Pattern 13:** `x:Bind` with `x:DataType` in every new `DataTemplate`; code-behind stays constructor-only; resource keys PascalCase.
- **Pattern 14/15 + A2:** test names `Method_Scenario_Expected_AC26n`; `[Trait("ac", "AC-2.6.<n>")]` (lowercase trait name, uppercase value).

### Anti-patterns to avoid

- **Don't flatten services in the VM** — `Description.Services` is already flattened (FR-053). Just project it to `ServiceNodeViewModel`s.
- **Don't buffer actions and swap at the end** — that kills the incremental UX (FR-100). Stream into the bound `Children`.
- **Don't batch multiple actions into one `Post`** — per-action `Post` is what keeps each UI slice < 16 ms (NFR-UI4).
- **Don't create a per-node CTS or cancel on collapse** — use `deviceToken`; collapse retains the loaded list (AC-2.6.6).
- **Don't emit a diagnostic on `OperationCanceledException`** — cancellation is silent (AC-2.6.8). Only `UpnpException` subclasses get a `Warning`.
- **Don't rebuild the service list on `DeviceUpdated`/re-expand** — once-guard it; a second device-level Reset collapses expanded service subtrees (the Story 2.5 deferral).
- **Don't dispose the `MemoryStream` before the `await foreach` completes** — wrap the whole stream loop in the `using` (the parser reads lazily from it).
- **Don't put `ScpdUrl` straight into `FetchScpdAsync`** — resolve it against `LocationUrl` first (it's usually relative).

### Project Structure Notes

New Core files: `ViewModels/NodeServices.cs`, `ViewModels/ServiceNodeViewModel.cs`, `ViewModels/ActionNodeViewModel.cs`.
Edited Core files: `ViewModels/DeviceNodeViewModel.cs`, `ViewModels/DeviceTreeViewModel.cs`, `ViewModels/ShellViewModel.cs`.
Edited App files: `Composition/ServiceRegistration.cs`, `Converters/NodeDataTemplateSelector.cs`, `MainWindow.xaml`.
New test files: `ViewModels/ServiceNodeViewModelTests.cs`, `ViewModels/ActionNodeViewModelTests.cs`, `Fakes/StubScpdParser.cs`.
Edited test files: `ViewModels/DeviceNodeViewModelTests.cs`, `ViewModels/DeviceTreeViewModelTests.cs`, `Fakes/StubUpnpHttpClient.cs`.
No new project, no new package reference, no `Directory.Packages.props` change.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.6] (lines 1062–1119) — verbatim ACs.
- [Source: architecture.md#Amendment A1 — "Loading…" Placeholder VM Contract] (lines 2389–2427) — AC-A1.1..A1.5, ReplaceWith atomic-Reset rule, action-leaf rule.
- [Source: architecture.md#Decision 5 / IScpdParser] (lines 551, 596, 608–618) — streaming-consumer pattern, AC-5.1 200-action budget.
- [Source: architecture.md#ScpdFetch timeout] (line 1425) — 10 s operation timeout vs perceived budget.
- [Source: architecture.md#Agent guidelines] (lines 3121–3129) — diagnostics/async/dispatcher/cancellation/collection rules.
- [Source: src/ohSpy.Core/Models/DeviceDescription.cs] — `Services` is the flattened list (NOT `AllServices`).
- [Source: src/ohSpy.Core/Scpd/DeviceDescriptionParser.cs#ReadEmbeddedDeviceList] — FR-053 flattening proof.
- [Source: src/ohSpy.Core/Scpd/XmlReaderScpdParser.cs] — streaming + cancellation + exception contract.
- [Source: src/ohSpy.Core/Http/IUpnpHttpClient.cs + UpnpExceptions.cs] — `FetchScpdAsync` + exception hierarchy.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs] — `ScpdFetch` / `ScpdParse` constants + mandatory context.
- [Source: _bmad-output/implementation-artifacts/deferred-work.md] — Story 2.5 `ReplaceWith` Reset deferral resolved here.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (dev-story workflow)

### Debug Log References

- `dotnet build -c Debug` — 0 errors. 1 pre-existing benign `WMC1506` XAML warning at `MainWindow.xaml(120)` (the Story 2.5 `FallbackTemplate` `{x:Bind Label, Mode=OneWay}` against `INodeViewModel`, which has no `INotifyPropertyChanged`; Loading/Error labels never change). The two new Service/Action templates bind `Label` against `[ObservableProperty]` VMs and emit no warning — no NEW warnings introduced.
- `dotnet test -c Debug` — Failed: 0, Passed: 267, Skipped: 2 (baseline 250 + 17 new). The 2 skips (`AsyncDisciplineTests`, `DiagCategoriesUsageTests`) are unchanged from the Story 2.5 baseline.
- `dotnet test --filter "category=chaos"` — exactly 1 passing (chaos suite unchanged).
- `dotnet test --filter "FullyQualifiedName~CoreAppBoundary"` — 4 passing (NodeServices / ServiceNodeViewModel / ActionNodeViewModel are pure Core, no `Microsoft.UI.*`).

### Completion Notes List

- **One deviation from the literal spec snippet (CA1068):** `ServiceNodeViewModel`'s constructor takes `CancellationToken deviceToken` as the LAST parameter — `(ServiceDescription, Uri, Guid, NodeServices, CancellationToken)` — not mid-list as the Task 2.1 snippet showed. The mid-list order tripped `CA1068` under `TreatWarningsAsErrors`. CT-last matches Dev Notes §"Code-style" Pattern 6 ("CT parameter is `deviceToken`, passed last"), so this aligns with the stated convention. The single caller in `DeviceNodeViewModel.OnIsExpandedChanged` was updated to match.
- **Glyphs (FR-045):** Service = U+E713 (Segoe MDL2 "Setting"), Action = U+E943 ("Code"), per Dev Notes §8 suggestions. Written as literal PUA chars (consistent with Device U+E703 from Story 2.5) and byte-verified after writing — guarding against the Story 2.5 mojibake false-positive (the chars are correct UTF-8 in source; any garbled display is a viewer artifact). Glyph values are runtime-only strings, not asserted by tests.
- **Incremental semantic (AC-2.6.3):** actions stream into the BOUND `Children` — placeholder dropped together with the first action in a single `Post` (`Clear()` + `Add(first)`), subsequent actions appended one `Post` each (FR-100). Empty SCPD clears the placeholder so no stale "Loading…" lingers.
- **Once-guards (AC-2.6.2 / AC-2.6.6):** `DeviceNodeViewModel._servicesBuilt` and `ServiceNodeViewModel._loadStarted` are `Interlocked.Exchange` guards — the device service list builds exactly once and the SCPD fetches exactly once, resolving the Story 2.5 `ReplaceWith`-Reset deferral (a second device-level Reset would collapse expanded service subtrees). Verified by `Expand_Twice_DoesNotRebuildServiceList_AC262` (same VM instances retained) and `Expand_Twice_DoesNotRefetch_AC266` (one fetch).
- **Cancellation (AC-2.6.8):** `deviceToken` flows into both `FetchScpdAsync` and `StreamActionsAsync`; `OperationCanceledException` is caught and silently swallowed (no diagnostic) — distinct from the `UpnpProtocolException` (`ScpdParse`) / `UpnpException` (`ScpdFetch`) → `Warning` + inline-error paths. Catch ordering is OCE → protocol → base, since `UpnpProtocolException : UpnpException`.
- **Test strategy:** `StubScpdParser` yields with `await Task.Yield()` (mirrors the real parser, exercises the cross-thread `Post`). Error/cancellation paths complete synchronously inline; happy-path streaming hops to the thread pool, so those tests await quiescence via a bounded `WaitUntilAsync` poll. `FirstExpand_RealParser_LinnDs5Action_AC263` is an end-to-end check against the real `XmlReaderScpdParser` + the `linn-ds-5action.xml` fixture.
- **Task 12.5 (manual UI smoke):** NOT executed — requires a running WinUI desktop session, not available in this headless dev environment. The AC-gating behaviours it would observe (service list on device expand, incremental action stream, inline error, no re-fetch on re-expand, no diagnostic on cancel) are all covered by the unit/integration tests above. Chevron suppression for action rows (AC-2.6.7) is structural (empty `Children` → no chevron) and can only be confirmed by manual inspection; recommend running it before closing the epic.

**Code-review follow-ups resolved (2026-06-03, claude-opus-4-8[1m]):**
- ✅ Resolved review finding [LOW / F1]: `UpnpProtocolException` from an oversize HTTP body was misclassified as `ScpdParse`. `LoadActionsAsync` now uses two separate try blocks — the fetch's `catch (UpnpException)` attributes timeout/transport/oversize-body failures to `DiagCategories.ScpdFetch`, while only the parser loop's `catch (UpnpProtocolException)` maps to `DiagCategories.ScpdParse`. Added regression test `Expand_FetchThrowsProtocol_Oversize_ShowsInlineError_EmitsScpdFetchWarning_AC264` (asserts `ScpdFetch` category for a fetch-thrown protocol exception). Catch ordering (OCE-first) preserved in both blocks.
- ✅ Resolved review finding [COSMETIC / F2]: removed the dead `?? ""` null-guard on the non-nullable `ServiceDescription.ServiceType` in `ComputeLabel`.
- Post-fix verification: `dotnet build` 0 errors / 0 warnings; `dotnet test` 268 passed / 2 skipped / 0 failed (was 267; +1 F1 regression test); chaos suite unchanged at 1.

### File List

**New (Core):**
- `src/ohSpy.Core/ViewModels/NodeServices.cs`
- `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`
- `src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs`

**Modified (Core):**
- `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`
- `src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs`
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs`

**Modified (App):**
- `src/ohSpy.App/Composition/ServiceRegistration.cs`
- `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs`
- `src/ohSpy.App/MainWindow.xaml`

**New (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/StubScpdParser.cs`
- `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/ActionNodeViewModelTests.cs`

**Modified (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs`
- `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs`

### Change Log

| Date | Change |
|---|---|
| 2026-06-03 | Story 2.6 implemented: lazy SCPD fetch + incremental action stream. New `NodeServices` bundle, `ServiceNodeViewModel`, `ActionNodeViewModel`; `DeviceNodeViewModel` real expand handler (synchronous service-list build, once-guarded); DI + ShellViewModel + DeviceTreeViewModel threading; Service/Action XAML templates + selector routing. 17 new tests (250→267), chaos unchanged (1), CoreAppBoundary green. One spec deviation: `ServiceNodeViewModel` ctor takes `CancellationToken` last (CA1068 / Pattern 6). |
| 2026-06-03 | Addressed code review findings — 2 patches resolved. F1 (LOW): split fetch/parse try blocks so an oversize-body `UpnpProtocolException` logs as `ScpdFetch` not `ScpdParse`; added regression test. F2 (cosmetic): removed dead `?? ""` guard on non-nullable `ServiceType`. Build 0/0; tests 268 passed / 2 skipped; chaos 1. |

---

## Senior Developer Review (AI)

**Reviewer:** claude-sonnet-4-6 (bmad-code-review workflow, 2026-06-03)
**Baseline commit:** `8172c8e3474596af41059e24b561afd45d430db2`
**Diff scope:** All uncommitted changes in the working tree (modified tracked files + untracked new files)
**Build verified:** `dotnet build -c Debug` — 0 errors, 0 warnings. Build claim CONFIRMED.
**Test verified:** `dotnet test -c Debug` — 267 passed, 2 skipped, 0 failed. Test claim CONFIRMED.
**Chaos suite:** 1 passing (unchanged). CONFIRMED.
**CoreAppBoundaryTests:** 4 passing (NodeServices / ServiceNodeViewModel / ActionNodeViewModel are pure Core). CONFIRMED.

### Review Findings

- [x] [Review][Patch] `UpnpProtocolException` from oversize HTTP body misclassified as `ScpdParse` [`src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs:99-110`] — `FetchScpdAsync` can throw `UpnpProtocolException` for an oversize body (via `EnforceSizeCapOnHeaders` + `ReadWithSizeCapAsync` in `UpnpHttpClient`). In `LoadActionsAsync`, the single `UpnpProtocolException` catch block covers both this HTTP-layer case and the XmlReader-layer case, logging both as `DiagCategories.ScpdParse`. An oversize SCPD body is a fetch-layer failure and should be categorised as `DiagCategories.ScpdFetch`. Fix: split the try into two nested try blocks — inner wrapping only the `await foreach` to catch `UpnpProtocolException` as `ScpdParse`; outer catch handles `UpnpProtocolException` from `FetchScpdAsync` as `ScpdFetch`. Note: this is spec-compliant as written (AC-2.6.4 maps ALL `UpnpProtocolException` to `ScpdParse`), but the spec is ambiguous about the HTTP-oversize sub-case. Severity: LOW — oversize SCPDs are extremely rare in practice.

- [x] [Review][Patch] Dead null-check on non-nullable field in `ComputeLabel` [`src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs:51`] — `service.ServiceType ?? ""` guards against null, but `ServiceDescription.ServiceType` is declared non-nullable (it is a `required` record positional parameter). The guard is harmless but misleading. Fix: remove the `?? ""` and use `service.ServiceType` directly (or keep the guard and suppress the IDE warning explicitly, since callers in other stories could potentially construct `ServiceDescription` via object initialiser with a null). Severity: COSMETIC.

- [x] [Review][Defer] AC-2.6.8 cancellation test only exercises parser-path OCE, not HTTP-layer cancellation [`tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs:204-218`] — `Expand_DeviceTokenCancelled_NoError_NoDiagnostic_AC268` pre-cancels the token but `StubUpnpHttpClient.ScpdResponder` does not check `ct` before returning bytes, so `FetchScpdAsync` succeeds and cancellation is only observed in `StubScpdParser.ct.ThrowIfCancellationRequested()`. The HTTP-layer OCE path (token already cancelled before `FetchScpdAsync` sends) is untested. Behaviour is correct in both cases (OCE is caught); test gap is low-risk. Deferring to avoid complexity: a real HTTP-layer cancel test would need `ScpdResponder` to call `ct.ThrowIfCancellationRequested()` or use `Task.FromCanceled(ct)`. — deferred, pre-existing test-strategy choice.

### Review Follow-ups (AI)

**Approved with minor findings.** The implementation is architecturally sound and correctly delivers all eight acceptance criteria. Key design decisions — Interlocked once-guards, per-action `Post` incremental stream, catch ordering (OCE → UpnpProtocolException → UpnpException), SCPD URL resolution via `new Uri(deviceLocation, scpdUrl)`, and the constructor parameter-order deviation (CancellationToken last per CA1068/Pattern 6) — are all correct and well-justified.

**Spec deviation (CA1068):** ACCEPTABLE. The `ServiceNodeViewModel` constructor takes `CancellationToken` last `(ServiceDescription, Uri, Guid, NodeServices, CancellationToken)` rather than the spec snippet's mid-list position. This aligns with Pattern 6 ("CT parameter passed last"), satisfies the `TreatWarningsAsErrors` CA1068 rule, and is documented in both the dev agent record and the code comment. No behavioural difference; single call-site updated accordingly.

**Action required before closing:**
1. [SHOULD] Fix the `UpnpProtocolException` from oversize body being logged as `ScpdParse` — split the try block to separate fetch from parse exceptions (see Patch F1 above).
2. [MAY] Remove the dead `?? ""` null-guard in `ComputeLabel` (cosmetic).

**Not actionable now:**
- Task 12.5 manual UI smoke test remains unexecuted (headless environment). Recommend running before closing the epic (confirms chevron suppression for action rows and incremental streaming UX).
- No test validates `ActionNodeViewModel._action` storage (intentional — Story 3.2 scope). No change needed.
