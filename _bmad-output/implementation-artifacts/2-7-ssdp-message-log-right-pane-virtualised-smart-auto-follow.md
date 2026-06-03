---
baseline_commit: d417fad1447f7ad03b5fff7ad55cd003ffd8360c
---

# Story 2.7: SSDP Message Log (Right Pane, Virtualised, Smart Auto-Follow)

Status: done

## Story

As a Linn engineer,
I want the right pane to be a live scrolling list of every SSDP `alive` and `byebye` advertisement, newest at the top, virtualised so a chatty network doesn't stutter, with smart auto-follow that respects manual scroll position,
so that I can monitor what's happening on the wire without the prior tool's full-pane repaints and without losing my place when I scroll back to read history.

## Acceptance Criteria

**Verbatim ACs from epics.md §Story 2.7 (lines 1123–1171). AC trait IDs follow Amendment A2; this story assigns the numbers AC-2.7.1 … AC-2.7.7 to the seven `Given/When/Then` blocks below (the two auto-follow blocks are folded into AC-2.7.5).**

**AC-2.7.1 — SsdpLogEntry shape**

**Given** `ohSpy.Core/Models/SsdpLogEntry.cs`
**When** I inspect it
**Then** it is a `public sealed record` with `DateTime TimestampUtc`, `SsdpLogKind Kind` (enum `Alive | Byebye`), `Guid Uuid` (the device UUID — see Dev Notes §"Uuid is already parsed")

**AC-2.7.2 — SsdpLogViewModel shape**

**Given** `ohSpy.Core/ViewModels/SsdpLogViewModel.cs`
**When** I inspect it
**Then** it exposes `BoundedObservableCollection<SsdpLogEntry> Entries` constructed with capacity 10,000 (FR-016 + D6)
**And** it exposes `[ObservableProperty] bool IsAtTop` reflecting whether the bound list is parked at (or near) the top
**And** it subscribes to `IDiscoveryService.AnnouncementReceived` and routes alive / byebye announcements via `IUiDispatcher.Post(() => Entries.PrependNewest(new SsdpLogEntry(...)))` (FR-014 + FR-015)
**And** announcements with NTS other than `ssdp:alive` / `ssdp:byebye` are ignored at the log VM level (per FR-014 / FR-015 grammar)

**AC-2.7.3 — FIFO eviction**

**Given** the FIFO eviction
**When** the 10,001st entry arrives at capacity
**Then** the oldest (tail) entry is discarded (FR-016)
**And** the underlying `BoundedObservableCollection.PrependNewest` emits exactly `Add(0)` + `Remove(10000)` — never `Reset` (AC-6.1 invariant carried into the log VM)
**And** eviction never removes the top row (FR-055)

**AC-2.7.4 — Virtualised right-pane visual**

**Given** `MainWindow.xaml`'s right pane
**When** the log is rendered
**Then** the visual is an `ItemsRepeater` (or equivalent virtualised control) inside a `ScrollViewer` — NOT a `ListView` with non-virtualised wrapping (FR-101 + NFR-P1)
**And** each row displays the timestamp, the literal `ALIVE` / `BYEBYE` token, and the UUID — with `x:Bind` and `x:DataType="m:SsdpLogEntry"` (Pattern 13)

**AC-2.7.5 — Smart auto-follow (FR-055)**

**Given** the smart auto-follow rule (FR-055)
**When** the operator is parked at (or near) the top of the list
**Then** new arrivals scroll into view automatically (the visual stays anchored at the top)

**When** the operator scrolls away from the top to read history
**Then** new arrivals do NOT yank the view back to the top (FR-055 — the operator's scroll context is preserved)
**And** the `IsAtTop` flag transitions to `false` when the operator's scroll offset exceeds a small threshold (e.g. one row from the top)
**And** the `IsAtTop` flag transitions to `true` when the operator scrolls back to the top

**AC-2.7.6 — Sustained chatty-SSDP performance (manual / non-gating)**

**Given** the sustained chatty-SSDP test target
**When** the test fixture injects ≥ 20 advertisements/sec for ≥ 30 seconds (test baseline §6)
**Then** the log renders every entry without dropped frames visible to the eye (NFR-UI4)
**And** main-thread stalls remain < 16 ms (NFR-P5 + NFR-UI4)
**And** memory used by the rendered view scales with VISIBLE row count, not with the 10,000 buffered entries (FR-101 consequence)

**AC-2.7.7 — Adapter-switch clear (forward-compatible)**

**Given** an adapter switch (forward-compatible — full FR-050 lands in E5)
**When** the AdapterScope is replaced
**Then** the log VM's `Entries.Clear()` is called (single `Reset` notification — AC-6.6)
**And** the log starts fresh on the new adapter — no carry-over (PRD §7 Non-Goal: no settings persistence; same principle applies to runtime state)

> **Scope note on AC-2.7.7:** Story 2.7 delivers and unit-tests a public `SsdpLogViewModel.Clear()` method. There is NO adapter-switch event to subscribe to yet — FR-050 atomic rebind (and the call site that invokes `Clear()`) lands in **Story 5.2**. Do not invent an adapter-switch trigger here. See Dev Notes §"Adapter-switch clear is forward-compat only".

---

## Tasks / Subtasks

### Task 1 — SsdpLogKind enum + SsdpLogEntry record (AC: #1)

- [x] **1.1** Create `src/ohSpy.Core/Models/SsdpLogKind.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  /// <summary>Which SSDP NOTIFY verb a log row represents (FR-014 / FR-015).</summary>
  public enum SsdpLogKind
  {
      Alive,
      Byebye,
  }
  ```
- [x] **1.2** Create `src/ohSpy.Core/Models/SsdpLogEntry.cs`:
  ```csharp
  namespace ohSpy.Core.Models;

  using System.Globalization;

  /// <summary>
  /// One row in the SSDP log right pane (FR-003 / FR-014 / FR-015). Immutable snapshot —
  /// stamped at receipt and never mutated, so the bound row template uses OneTime x:Bind
  /// (no INotifyPropertyChanged needed). Newest-first; capped at 10,000 via
  /// BoundedObservableCollection (FR-016).
  /// </summary>
  public sealed record SsdpLogEntry(
      DateTime TimestampUtc,
      SsdpLogKind Kind,
      Guid Uuid)
  {
      /// <summary>Uppercase literal token for the row (AC-2.7.4): "ALIVE" / "BYEBYE".</summary>
      public string KindToken => Kind == SsdpLogKind.Alive ? "ALIVE" : "BYEBYE";

      /// <summary>Local-time HH:mm:ss.fff for the operator (the wire stamp is UTC).</summary>
      public string TimestampDisplay =>
          TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
  }
  ```
  These are the ONLY new model types. The two computed members exist so the XAML binds plain properties (`{x:Bind KindToken}`, `{x:Bind TimestampDisplay}`) instead of fragile `x:Bind` function/format-string syntax — see Dev Notes §"Bind computed display members, not inline ToString". `SsdpAnnouncement` already lives in `ohSpy.Core.Discovery` (NOT `Models`) and already exposes a parsed `Guid? Uuid` — do not re-parse the USN string (Dev Notes §"Uuid is already parsed").

### Task 2 — SsdpLogViewModel (AC: #2, #3, #7)

- [x] **2.1** Create `src/ohSpy.Core/ViewModels/SsdpLogViewModel.cs`. `partial` (source generator) `ObservableObject`, `IDisposable` (it subscribes to a discovery event and must unsubscribe — mirrors `DeviceTreeViewModel`):
  ```csharp
  namespace ohSpy.Core.ViewModels;

  using CommunityToolkit.Mvvm.ComponentModel;
  using ohSpy.Core.Collections;
  using ohSpy.Core.Discovery;
  using ohSpy.Core.Models;
  using ohSpy.Core.Threading;

  public sealed partial class SsdpLogViewModel : ObservableObject, IDisposable
  {
      // FR-016 + D6 cap.
      private const int Capacity = 10_000;

      private readonly IDiscoveryService _discovery;
      private readonly IUiDispatcher _ui;
      private int _disposed;

      // True when the bound list is parked at (or near) the top — drives smart auto-follow
      // (FR-055). Set by the view's ScrollViewer.ViewChanged handler; read by the view's
      // new-arrival handler to decide whether to keep the visual anchored at the top.
      [ObservableProperty]
      private bool _isAtTop = true; // starts at top (empty list)

      public BoundedObservableCollection<SsdpLogEntry> Entries { get; } =
          new(Capacity);

      public SsdpLogViewModel(IDiscoveryService discovery, IUiDispatcher ui)
      {
          _discovery = discovery;
          _ui = ui;
          _discovery.AnnouncementReceived += OnAnnouncementReceived;
      }

      // AnnouncementReceived already fires on the UI thread (DiscoveryService routes via
      // IUiDispatcher.Post), but we marshal the prepend through IUiDispatcher.Post anyway —
      // per the AC and the DiagnosticRingSink precedent. It is a cheap same-thread re-queue
      // that keeps the VM decoupled from the event's threading guarantee. DispatcherQueue is
      // FIFO, so arrival order is preserved. Do NOT add a second marshal.
      private void OnAnnouncementReceived(SsdpAnnouncement ann)
      {
          var kind = ClassifyOrNull(ann.NTS);
          if (kind is null) return; // FR-014/FR-015 grammar: only ssdp:alive / ssdp:byebye

          var entry = new SsdpLogEntry(DateTime.UtcNow, kind.Value, ann.Uuid ?? Guid.Empty);
          _ui.Post(() => Entries.PrependNewest(entry));
      }

      // Log routing is NTS-only and narrower than the registry routing in
      // DiscoveryService.RouteOnUiThread: absent NTS (M-SEARCH responses) is NOT logged.
      private static SsdpLogKind? ClassifyOrNull(string? nts) => nts switch
      {
          not null when nts.Equals("ssdp:alive", StringComparison.OrdinalIgnoreCase)
              => SsdpLogKind.Alive,
          not null when nts.Equals("ssdp:byebye", StringComparison.OrdinalIgnoreCase)
              => SsdpLogKind.Byebye,
          _ => null,
      };

      /// <summary>
      /// Drop all log rows (single Reset — AC-6.6). Forward-compat for the FR-050 adapter
      /// switch, which Story 5.2 wires to call this. UI-thread-owned; callers on the UI thread.
      /// </summary>
      public void Clear() => Entries.Clear();

      public void Dispose()
      {
          if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
          _discovery.AnnouncementReceived -= OnAnnouncementReceived;
      }
  }
  ```
  Notes the dev must honour:
  - **Stamp `DateTime.UtcNow` at receipt.** The `AnnouncementReceived` event carries only `SsdpAnnouncement` (no arrival timestamp); `SsdpDatagram.ArrivalUtc` is NOT propagated through it. The event fires on the UI thread shortly after arrival, so UtcNow-at-receipt is accurate enough. There is no clock abstraction in Core — `DiagnosticEmitter` stamps `DateTime.UtcNow` directly; follow that precedent (Dev Notes §"Timestamp source").
  - **`ann.Uuid ?? Guid.Empty`** — the parser already extracted the UUID from the USN into `SsdpAnnouncement.Uuid`. Do NOT re-parse `USN`. `Guid.Empty` is the defensive fallback for the (rare) announcement whose USN had no parseable UUID.
  - The `Capacity` constant is `10_000`. `PrependNewest` + the FIFO eviction (`Add(0)` + `Remove(10000)`, no `Reset`) are already implemented and tested in `BoundedObservableCollection` (Story 1.2) — AC-2.7.3 is satisfied by USING the primitive, not by re-implementing eviction.

### Task 3 — Wire SsdpLogViewModel into ShellViewModel (AC: #2, #7)

Edit `src/ohSpy.Core/ViewModels/ShellViewModel.cs` (UPDATE — Story 2.5/2.6 own it). `ShellViewModel` already receives `IDiscoveryService discovery` and `IUiDispatcher ui` in its constructor (today `ui` is passed only to `DeviceTreeViewModel`).

- [x] **3.1** Add an observable property for the log VM, alongside the existing `_deviceTree`:
  ```csharp
  [ObservableProperty]
  private SsdpLogViewModel _ssdpLog;
  ```
- [x] **3.2** Construct it in the constructor (no new constructor parameters — reuse `discovery` + `ui`):
  ```csharp
  _deviceTree = new DeviceTreeViewModel(registry, ui, nodeServices);
  _ssdpLog    = new SsdpLogViewModel(discovery, ui); // subscribes to AnnouncementReceived
  ```
- [x] **3.3** Dispose it in `DisposeAsync`, alongside `DeviceTree.Dispose()`:
  ```csharp
  DeviceTree.Dispose();
  SsdpLog.Dispose(); // unsubscribe from AnnouncementReceived
  ```
  Order relative to `_discovery.DisposeAsync()` does not matter functionally (unsubscribe is idempotent and the read loop is already drained), but unsubscribe before/after is both safe. Keep it next to `DeviceTree.Dispose()` for readability.

### Task 4 — MainWindow.xaml right pane: virtualised log (AC: #4)

Edit `src/ohSpy.App/MainWindow.xaml` (UPDATE). Replace the right-pane placeholder `Border`/`TextBlock` (currently lines ~129–139, `"SSDP log — Story 2.7"`) with the real virtualised log.

- [x] **4.1** Add the models namespace to the `<Window>` root element (next to the existing `xmlns:vm`):
  ```xml
  xmlns:m="using:ohSpy.Core.Models"
  ```
- [x] **4.2** Replace the placeholder right-pane `Border` with an `ItemsRepeater` inside a `ScrollViewer` (FR-101 virtualisation). Keep the left dividing border:
  ```xml
  <!-- Right pane: live SSDP log (FR-003 / FR-014 / FR-015 / FR-101) -->
  <Border
      Grid.Column="1"
      BorderBrush="{ThemeResource DividerStrokeColorDefaultBrush}"
      BorderThickness="1,0,0,0">
      <ScrollViewer
          x:Name="LogScrollViewer"
          VerticalScrollBarVisibility="Auto"
          VerticalScrollMode="Enabled">
          <ItemsRepeater ItemsSource="{x:Bind ViewModel.SsdpLog.Entries, Mode=OneWay}">
              <ItemsRepeater.Layout>
                  <StackLayout Orientation="Vertical" />
              </ItemsRepeater.Layout>
              <ItemsRepeater.ItemTemplate>
                  <DataTemplate x:DataType="m:SsdpLogEntry">
                      <Grid Padding="8,2" ColumnSpacing="12">
                          <Grid.ColumnDefinitions>
                              <ColumnDefinition Width="Auto" />
                              <ColumnDefinition Width="Auto" />
                              <ColumnDefinition Width="*" />
                          </Grid.ColumnDefinitions>
                          <!-- Timestamp (local-time HH:mm:ss.fff) -->
                          <TextBlock
                              Grid.Column="0"
                              Text="{x:Bind TimestampDisplay}"
                              FontFamily="Consolas"
                              Foreground="{StaticResource MutedForegroundBrush}" />
                          <!-- ALIVE / BYEBYE literal token -->
                          <TextBlock
                              Grid.Column="1"
                              Text="{x:Bind KindToken}"
                              FontFamily="Consolas" />
                          <!-- Device UUID -->
                          <TextBlock
                              Grid.Column="2"
                              Text="{x:Bind Uuid}"
                              FontFamily="Consolas"
                              TextTrimming="CharacterEllipsis" />
                      </Grid>
                  </DataTemplate>
              </ItemsRepeater.ItemTemplate>
          </ItemsRepeater>
      </ScrollViewer>
  </Border>
  ```
  - `KindToken` / `TimestampDisplay` are the computed display members on `SsdpLogEntry` (Task 1.2). Bind those plain properties — NOT `x:Bind Kind` (renders `Alive`/`Byebye`, not the uppercase token the AC wants) and NOT an inline `x:Bind TimestampUtc.ToString('…')` (function/format-string binding is fragile in WinUI). See Dev Notes §"Bind computed display members, not inline ToString".
  - All bindings are `OneTime` (the default for `x:Bind` on a property of an immutable record) — correct, because `SsdpLogEntry` never mutates. This avoids the `WMC1506` warning seen on the 2.5 `FallbackTemplate`.
  - Do NOT wrap the `ItemsRepeater` in anything that disables virtualisation (no `ItemsControl`, no `VerticalScrollMode="Disabled"` on an outer scroller). The `ScrollViewer` + `ItemsRepeater` + `StackLayout` combination is the virtualised path (FR-101 / NFR-P1).

### Task 5 — Smart auto-follow wiring (AC: #5)

Auto-follow is **pure view mechanics** (scroll-offset math against a `ScrollViewer`) and cannot live in Core or be unit-tested without a WinUI runtime. The testable state — `IsAtTop` — lives in the VM (Task 2). The view owns the scroll handlers. This is a **documented, deliberate exception to Pattern 13** ("constructor-only code-behind"): the handlers contain NO business logic, only scroll-position bookkeeping. Confine them to a clearly-commented region.

Edit `src/ohSpy.App/MainWindow.xaml.cs` (UPDATE).

- [x] **5.1** Wire the two scroll behaviours in the constructor, AFTER `InitializeComponent()` (named elements exist by then):
  ```csharp
  // ── Smart auto-follow (FR-055) — view mechanics only, no business logic (Pattern 13
  //    documented exception). The testable state (IsAtTop) lives in SsdpLogViewModel. ──
  LogScrollViewer.ViewChanged += OnLogViewChanged;
  ViewModel.SsdpLog.Entries.CollectionChanged += OnLogEntriesChanged;
  ```
- [x] **5.2** Add the handlers (and an "approximately one row" threshold constant):
  ```csharp
  // One log row is a single Consolas line + 2px padding top/bottom. ~24px is a safe
  // "near the top" threshold (AC-2.7.5 — within one row of the top counts as at-top).
  private const double AtTopThresholdPx = 24.0;

  private void OnLogViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
  {
      // Update IsAtTop from the operator's scroll position. The VM property drives whether
      // the next arrival re-anchors to the top (FR-055).
      ViewModel.SsdpLog.IsAtTop = LogScrollViewer.VerticalOffset <= AtTopThresholdPx;
  }

  private void OnLogEntriesChanged(object? sender,
      System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
  {
      // Only react to a newest-row prepend (Add at index 0). Remove(tail eviction) and
      // Reset(Clear) need no scroll adjustment.
      if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
          return;

      if (ViewModel.SsdpLog.IsAtTop)
      {
          // Parked at the top: keep the newest row in view (anchor to offset 0).
          LogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
      }
      else
      {
          // Scrolled away reading history: a top prepend pushes existing content down by one
          // row, so add ~one row to the offset to keep the SAME item under the viewport — do
          // NOT yank to the top (FR-055). Rows are uniform single lines, so a fixed delta is
          // accurate enough. (Variable-height rows are out of scope for this log.)
          LogScrollViewer.ChangeView(null, LogScrollViewer.VerticalOffset + AtTopThresholdPx,
              null, disableAnimation: true);
      }
  }
  ```
  - Required `using`s: `Microsoft.UI.Xaml.Controls` (`ScrollViewerViewChangedEventArgs`) — already implied by the WinUI namespace; add explicitly if the analyzer complains.
  - `disableAnimation: true` keeps the re-anchor instantaneous (no smooth-scroll lag under burst).
  - The `CollectionChanged` subscription is on a collection owned by a VM that `ShellViewModel` disposes; the window itself lives for the app's lifetime, so no explicit unsubscribe is required here (the window and VM die together at shutdown). Document this; do not add `IDisposable` to the `Window`.

### Task 6 — Tests: SsdpLogEntry / SsdpLogKind (AC: #1)

**Location:** `tests/ohSpy.Core.Tests/Models/SsdpLogEntryTests.cs` (new). Create the `Models` test folder if absent.

- [x] **6.1** `Record_HoldsTimestampKindUuid_AC271` — construct `new SsdpLogEntry(ts, SsdpLogKind.Alive, uuid)`; assert all three round-trip; assert value equality (records) for two entries with identical fields.
- [x] **6.2** `KindToken_MapsAliveAndByebye_AC271` — `Alive` → `"ALIVE"`, `Byebye` → `"BYEBYE"`.
- [x] **6.3** `TimestampDisplay_FormatsLocalHmsMillis_AC271` — a known UTC `DateTime` → `TimestampDisplay` equals its `ToLocalTime().ToString("HH:mm:ss.fff", InvariantCulture)` (asserts the format + invariant culture; the local-time conversion makes this machine-TZ-relative, so compare against the same expression, not a hard-coded string).

### Task 7 — Tests: SsdpLogViewModel (AC: #2, #3, #7)

**Location:** `tests/ohSpy.Core.Tests/ViewModels/SsdpLogViewModelTests.cs` (new). Trait every test `[Trait("ac", "AC-2.7.<n>")]`. Use `InlineUiDispatcher` so `Post` runs inline and assertions are deterministic.

You need a controllable `IDiscoveryService` fake — none exists yet. Create `tests/ohSpy.Core.Tests/Fakes/StubDiscoveryService.cs`:
```csharp
namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Discovery;

internal sealed class StubDiscoveryService : IDiscoveryService
{
    public event Action<SsdpAnnouncement>? AnnouncementReceived;

    /// <summary>Test helper: raise the event as the real service would (already on UI thread).</summary>
    public void Raise(SsdpAnnouncement ann) => AnnouncementReceived?.Invoke(ann);

    public Task StartAsync(CancellationToken adapterToken, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```
Build `SsdpAnnouncement`s with the positional record ctor (it lives in `ohSpy.Core.Discovery`); only `NTS` and `Uuid` matter for the log — pass `null` for the rest.

- [x] **7.1** `Alive_PrependsEntry_AC272` — raise an announcement with `NTS = "ssdp:alive"`, `Uuid = g`; assert `Entries.Count == 1`, `Entries[0].Kind == Alive`, `Entries[0].Uuid == g`.
- [x] **7.2** `Byebye_PrependsEntry_AC272` — `NTS = "ssdp:byebye"`; assert `Entries[0].Kind == Byebye`.
- [x] **7.3** `Newest_IsAtIndexZero_AC272` — raise alive(g1) then alive(g2); assert `Entries[0].Uuid == g2`, `Entries[1].Uuid == g1` (newest-first).
- [x] **7.4** `NtsCaseInsensitive_AC272` — `NTS = "SSDP:ALIVE"` still classifies as `Alive`.
- [x] **7.5** `OtherNts_Ignored_AC272` — raise announcements with `NTS = null` (M-SEARCH response), `NTS = "ssdp:update"`, and `NTS = "ssdp:discover"`; assert `Entries.Count == 0` (FR-014/FR-015 grammar — only alive/byebye logged).
- [x] **7.6** `NullUuid_FallsBackToEmpty_AC272` — `NTS = "ssdp:alive"`, `Uuid = null`; assert `Entries[0].Uuid == Guid.Empty` (no throw, no re-parse).
- [x] **7.7** `Capacity_Is10000_AC272` — assert `Entries.Capacity == 10_000`.
- [x] **7.8** `Eviction_AtCapacity_DropsTail_NoReset_AC273` — capacity-boundary test. To avoid raising 10,001 events, construct a small-capacity scenario OR raise to capacity+1 and assert: `Entries.Count == 10_000` after the 10,001st, the oldest (first-raised) UUID is gone, and `Entries[0]` is the newest. Subscribe a `NotifyCollectionChangedEventArgs` recorder to `Entries.CollectionChanged` and assert NO `Reset` was emitted across the burst (only `Add`/`Remove`). *(The exact `Add(0)`+`Remove(10000)` index contract is already proven by `BoundedObservableCollectionTests` (Story 1.2) — this test confirms the VM routes through `PrependNewest`, not that the primitive is re-correct.)*
- [x] **7.9** `Clear_EmptiesEntries_EmitsSingleReset_AC277` — raise a few alives; subscribe a recorder; call `vm.Clear()`; assert `Entries.Count == 0` and exactly one `Reset` notification.
- [x] **7.10** `Dispose_Unsubscribes_NoPrependAfterDispose_AC272` — raise one alive (asserts subscription works); `vm.Dispose()`; raise another alive; assert `Entries.Count` is unchanged (handler detached). Confirms the `IDisposable` unsubscribe (mirrors the 2.5 `DeviceTreeViewModel` review patch).
- [x] **7.11** `IsAtTop_DefaultsTrue_AC275` — fresh VM (empty list) → `IsAtTop == true` (the at-top default so the first arrivals auto-follow).

> **Note on AC-2.7.5/AC-2.7.6:** the *scroll* behaviours (auto-follow re-anchor, no-yank offset compensation, `IsAtTop` flips from real scroll offset) and the chatty-burst perf bars are **view-layer / manual-verify** (Task 9) — they cannot be unit-tested headlessly. The VM unit tests cover `IsAtTop`'s shape and default; the wiring that drives it from `ScrollViewer.VerticalOffset` is exercised only in the manual smoke.

### Task 8 — DI / composition sanity (AC: #2)

- [x] **8.1** No new DI registration is required. `SsdpLogViewModel` is constructed by `ShellViewModel` (like `DeviceTreeViewModel`), NOT registered in the container. `IDiscoveryService` + `IUiDispatcher` are already registered (ServiceRegistration.cs lines 25, 95). Confirm the app still resolves `ShellViewModel` and launches (build + the existing composition test, if any).
- [x] **8.2** Confirm `CoreAppBoundaryTests` still green — `SsdpLogEntry`, `SsdpLogKind`, `SsdpLogViewModel` are pure Core (only `CommunityToolkit.Mvvm` + BCL + Core types); they must NOT reference `Microsoft.UI.*` / `Microsoft.Windows.*` / `WinRT.Interop.*`.

### Task 9 — Final verification (AC: all)

- [x] **9.1** `dotnet build` — 0 errors / 0 warnings (`TreatWarningsAsErrors`). Watch for: `WMC1506` on the new `DataTemplate` (avoid by keeping all log-row bindings `OneTime` — `SsdpLogEntry` is immutable, so no `Mode=OneWay`); nullable warnings on `ann.Uuid`; analyzer flags on the code-behind handlers.
- [x] **9.2** `dotnet test` — all green. Baseline 268 passing (Story 2.6). Story 2.7 adds ~14 tests; target ~282, 2 skips unchanged.
- [x] **9.3** `dotnet test --filter "category=chaos"` — still exactly **1** (chaos suite unchanged).
- [x] **9.4** `dotnet test --filter "FullyQualifiedName~CoreAppBoundary"` — green (new Core types are WinUI-free).
- [x] **9.5** **Manual smoke (non-AC-gating; record in Dev Agent Record — covers AC-2.7.4/5/6 view behaviours):** launch `ohSpy.App` on a network with live UPnP devices. Confirm: (a) the right pane shows `ALIVE`/`BYEBYE` rows streaming in, newest at top, with timestamp + token + UUID; (b) parked at the top, new arrivals keep the newest row visible (auto-follow); (c) scroll down to read history → new arrivals do NOT yank you back to the top, and your reading position holds; (d) scroll back to the top → auto-follow re-engages; (e) under a chatty network (or the `FakeUpnpDevice` burst fixture ≥ 20 adv/s for ≥ 30 s) there is no visible stutter and the UI stays responsive (FR-101 / NFR-UI4); (f) memory does not balloon as the log fills toward 10,000 (virtualisation — visible-row-bound).

---

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **FR-003 / FR-014 / FR-015** | Right-pane log of every NOTIFY `ssdp:alive` / `ssdp:byebye`, newest at top | AC-2.7.1, AC-2.7.2 |
| **FR-016 / D6 / AC-6.1** | `BoundedObservableCollection<SsdpLogEntry>(10_000)`; FIFO tail eviction; `Add(0)`+`Remove(10000)`, never `Reset` | AC-2.7.3 |
| **FR-055** | Smart auto-follow: anchor at top when `IsAtTop`; preserve scroll context when scrolled away | AC-2.7.5 |
| **FR-101 / NFR-P1 / NFR-P5** | `ItemsRepeater` + `ScrollViewer` virtualisation; memory scales with visible rows; no full-pane repaint | AC-2.7.4, AC-2.7.6 |
| **AC-6.6 / FR-050** | `SsdpLogViewModel.Clear()` → single `Reset` (adapter-switch call site lands in Story 5.2) | AC-2.7.7 |
| **Pattern 9 / 13** | `ObservableObject` VM; `x:Bind` `x:DataType` template; cross-thread mutation via `IUiDispatcher.Post` | AC-2.7.2, AC-2.7.4 |
| **Decision 6 precedent** | `DiagnosticRingSink` already does `BoundedObservableCollection` + `Post(PrependNewest)` — same shape | AC-2.7.2 |

### CRITICAL DESIGN DECISIONS

**1. `SsdpAnnouncement.Uuid` is already parsed — do NOT re-parse the USN.** *(§"Uuid is already parsed")*
The AC text says the entry's `Uuid` is "extracted from USN". That extraction already happened: `SsdpParser` populates `SsdpAnnouncement.Uuid` (`Guid?`) from the USN at parse time (`src/ohSpy.Core/Discovery/SsdpAnnouncement.cs`). The log VM consumes `ann.Uuid` directly — `ann.Uuid ?? Guid.Empty`. Writing any USN-string parsing in the VM duplicates parser logic and is wrong. (This mirrors the Story 2.6 `AllServices`→`Services` correction: the AC describes the *intent*, the model already provides the *result*.)

**2. NTS-only routing — narrower than the registry's routing.**
`DiscoveryService.RouteOnUiThread` computes `effectiveNt = ann.NT ?? ann.ST` and treats *absent* NTS (M-SEARCH responses) as `ssdp:alive` for the **registry**. The **log** does NOT do this. Per FR-014/FR-015 the log captures only NOTIFY `ssdp:alive` / `ssdp:byebye`, keyed strictly on `NTS`. So `NTS == null` (M-SEARCH responses), `ssdp:update`, `ssdp:discover`, etc. are all ignored by the log (AC-2.7.2 "NTS other than alive/byebye are ignored"). Do NOT reuse `effectiveNt` / the registry predicate here. The log's `ClassifyOrNull` is NTS-only.

**3. `AnnouncementReceived` already fires on the UI thread — keep the `Post` anyway.**
`DiscoveryService.ReadLoopAsync` does `ui.Post(() => RouteOnUiThread(...))`, and `RouteOnUiThread` raises `AnnouncementReceived?.Invoke(ann)` as its last line. So the log VM's handler runs **on the UI thread**. The AC nonetheless specifies `IUiDispatcher.Post(() => Entries.PrependNewest(...))`. Keep it: it is a cheap same-thread re-queue (DispatcherQueue is FIFO → arrival order preserved), it matches the `DiagnosticRingSink` precedent (which genuinely marshals from a background thread), and it decouples the VM from the event's threading guarantee. It is NOT a cross-thread hazard either way. Do not add a *second* marshal, and do not "optimise it away" — a reviewer should see the precedent-consistent shape.

**4. Timestamp source: `DateTime.UtcNow` at receipt.** *(§"Timestamp source")*
The `AnnouncementReceived` event carries only `SsdpAnnouncement`. `SsdpDatagram.ArrivalUtc` (the real wire-arrival stamp) is NOT propagated through it — and propagating it would mean changing the event signature (`Action<SsdpAnnouncement>` → a tuple/2-arg), touching `IDiscoveryService`, `DiscoveryService`, and every existing discovery test. Out of scope. Stamp `DateTime.UtcNow` in the VM at receipt; the event fires on the UI thread shortly after arrival, so the skew is sub-millisecond. There is **no clock abstraction** in Core (`DiagnosticEmitter` uses `DateTime.UtcNow` directly) — follow that precedent. Consequence: `SsdpLogViewModelTests` should not assert an exact timestamp; assert `Kind`/`Uuid` and (if desired) that `TimestampUtc` is recent / non-default.

**5. The bounded-collection contract is already satisfied — use it, don't re-implement.**
`BoundedObservableCollection<T>.PrependNewest` (Story 1.2, `src/ohSpy.Core/Collections/BoundedObservableCollection.cs`) already: prepends at index 0; at capacity emits `Add(0)` then `Remove(Capacity)` and NEVER `Reset`; and `Clear()` is the ONLY `Reset` source. The 1.2 test suite proves the exact index contract (AC-6.1). AC-2.7.3 for this story is about the VM **routing every alive/byebye through `PrependNewest`** at capacity 10,000 — not about re-proving the primitive. Don't write eviction logic in the VM.

**6. `IDisposable` for unsubscribe (mirror `DeviceTreeViewModel`).**
`SsdpLogViewModel` subscribes to `_discovery.AnnouncementReceived` in its constructor. It MUST implement `IDisposable` and unsubscribe (`-=`) in `Dispose()`, guarded by `Interlocked.Exchange` — identical to the `DeviceTreeViewModel` pattern (which was a Story 2.5 review patch: "unsubscribe on dispose"). `ShellViewModel.DisposeAsync` calls `SsdpLog.Dispose()` next to `DeviceTree.Dispose()`. Without this, the singleton discovery service holds a strong reference to the VM.

**7. Auto-follow is view mechanics — a documented Pattern 13 exception.** *(§"Auto-follow division of labour")*
Pattern 13 says "constructor-only code-behind; all logic in the VM". Auto-follow is the rare legitimate exception: scroll-offset arithmetic against a live `ScrollViewer` is inherently view-layer and untestable in Core. The division:
- **VM (`SsdpLogViewModel`, testable):** owns `IsAtTop` (`[ObservableProperty]`, defaults `true`). The view writes it from scroll position; nothing else reads it in Core.
- **View (`MainWindow.xaml.cs`, manual-verify):** `ScrollViewer.ViewChanged` → `IsAtTop = VerticalOffset <= threshold`. `Entries.CollectionChanged` (Add only) → if `IsAtTop`, `ChangeView(null, 0, …)`; else `ChangeView(null, VerticalOffset + rowHeight, …)` to hold the reading position (FR-055 "don't yank").
Keep the handlers in a clearly-commented region, no business logic. This is the architecture's Gap-2 mechanism verbatim ("IsAtTop flag observed from scroll position; auto-scroll only when IsAtTop").

**8. Why a top-prepend needs active scroll compensation (the FR-055 subtlety).**
`ItemsRepeater` + `StackLayout` lays out index 0 at y=0. `PrependNewest` makes the new row index 0 (y=0) and shifts every existing row down by one row-height in content coordinates. If you do nothing: at the top (offset ≈ 0) the new row shows naturally — but the offset can drift, so we re-anchor to 0 to be safe. When scrolled away (offset > 0) the row you were reading is now one row lower in content space, so the *same numeric offset* shows a *different* row — i.e. the content appears to scroll under you. To preserve the reading position you must add one row-height to the offset per prepend. Rows are uniform single Consolas lines, so a fixed `AtTopThresholdPx` (~24px) delta is accurate. Variable-height rows would need per-row measurement — explicitly out of scope for this log.

**9. `x:Bind` is `OneTime` here — and that's correct.**
`SsdpLogEntry` is an immutable record; its fields never change after construction. So the row template binds `OneTime` (the `x:Bind` default for a one-shot value) — NOT `Mode=OneWay`. This is both correct (nothing to update) and avoids the `WMC1506` "no `INotifyPropertyChanged`" warning that the Story 2.5 `FallbackTemplate` triggered with `Mode=OneWay`. Do not add `Mode=OneWay` to the log-row bindings.

**10. Bind computed display members, not inline `ToString` / converters.** *(§"Bind computed display members, not inline ToString")*
WinUI `x:Bind` function binding with a literal format argument (`{x:Bind TimestampUtc.ToString('HH:mm:ss')}`) is fragile — quoting and overload resolution bite, and a headless dev can't iterate on XAML-compile errors quickly. And `{x:Bind Kind}` renders the enum name (`Alive`), not the uppercase `ALIVE` token the AC requires. Both are solved by two one-line computed members on the record — `KindToken` (`"ALIVE"`/`"BYEBYE"`) and `TimestampDisplay` (`ToLocalTime().ToString("HH:mm:ss.fff", InvariantCulture)`) — bound as plain `OneTime` properties. No `IValueConverter` (more boilerplate, App-layer). Yes, this puts display formatting on a Core record; that's an accepted pragmatic trade for binding reliability (the values are pure projections of the record's own fields). `InvariantCulture` keeps the timestamp format stable across machines; `ToLocalTime` is deliberate — the operator reads wall-clock, the wire stamp is UTC.

### Adapter-switch clear is forward-compat only (AC-2.7.7)

Story 2.7 ships a public `SsdpLogViewModel.Clear()` and unit-tests it (AC-6.6 single `Reset`). It does NOT wire a trigger, because **there is no adapter-switch event in the system yet** — FR-050 atomic rebind, the `AdapterScope` replacement sequence, and the call site that invokes `Clear()` (and `DeviceRegistry.Clear()`, per Story 5.2 AC line 1860) all land in **Story 5.2**. Resist inventing an adapter-switch hook here. The method's existence + test is the entire deliverable for this AC in this story.

### What this story does NOT do (scope discipline)

- **Does NOT add right-click context menus on log rows** — not in scope for any story; the log is read-only.
- **Does NOT add a "clear log" button / menu** — `Clear()` exists for the Story 5.2 adapter switch only.
- **Does NOT filter the log** (by UUID, by kind) — no filtering FR in scope.
- **Does NOT persist the log** — PRD §7 Non-Goal (no settings/state persistence).
- **Does NOT log M-SEARCH responses** (absent NTS) — FR-014/FR-015 are NOTIFY-only (Design Decision 2).
- **Does NOT change the `AnnouncementReceived` event signature** — the timestamp is stamped VM-side (Design Decision 4).
- **Does NOT touch the device tree / left pane** (Stories 2.5/2.6) or the registry routing in `DiscoveryService`.
- **Does NOT add a VM-side prepend-coalescing layer** — the architecture lists it as a *conditional* follow-up ("IF degraded under burst, add coalescing"). Only add it if the manual burst smoke (Task 9.5e) actually shows >16 ms stalls; otherwise it's premature. Note any degradation in the Dev Agent Record.

### Previous-story intelligence

**Story 2.6 (NodeServices / ServiceNodeViewModel / ActionNodeViewModel):**
- Established: `[ObservableProperty]` requires `partial class`; the source generator turns `_isAtTop` → `IsAtTop` + `OnIsAtTopChanged` hook (not needed here). `ConfigureAwait(false)` on every Core await (no awaits in this VM — it's event-driven + synchronous `Post`). CT-last convention (no CT params here).
- `Interlocked.Exchange` once-guards are the project's idiom for "do this exactly once" (used here for `_disposed`).
- Code-review caught: misclassified exception categories, dead null-guards. None apply here (no exception handling — the log VM never fetches; it only routes already-parsed announcements).

**Story 2.5 (ShellViewModel / DeviceTreeViewModel / MainWindow):**
- `ShellViewModel` is the owner/composition point for pane VMs (`[ObservableProperty] DeviceTreeViewModel _deviceTree`); add `_ssdpLog` the same way. `MainWindow.xaml.cs` exposes `public ShellViewModel ViewModel { get; }` so `x:Bind ViewModel.SsdpLog.Entries` compiles.
- **Review patch precedent (directly relevant):** `DeviceTreeViewModel` was made `IDisposable` to unsubscribe from registry events on dispose, and `ShellViewModel.DisposeAsync` calls it. `SsdpLogViewModel` must follow this exactly (Design Decision 6).
- The `MutedForegroundBrush` / `DividerStrokeColorDefaultBrush` resources already exist (used by the device template + the placeholder border) — reuse them in the log row.
- The `NodeDataTemplateSelector` / `TreeView` are the LEFT pane only — the log is a separate `ItemsRepeater`, no selector needed (homogeneous `SsdpLogEntry` rows).

**Story 1.2 (collection primitives):**
- `BoundedObservableCollection<T>(capacity)` — newest-first ring buffer; `PrependNewest` O(1), `Add(0)`+`Remove(Capacity)` at cap, no `Reset`; `Clear()` = the only `Reset`. UI-thread-owned, not thread-safe → marshal via `IUiDispatcher` (we do). Fully tested in `BoundedObservableCollectionTests`.

**Story 1.5 (DiagnosticRingSink) — the closest precedent:**
- `DiagnosticRingSink` already implements the exact target shape: holds a `BoundedObservableCollection<DiagnosticRow>`, and on each push does `_dispatcher.Post(() => Entries.PrependNewest(row))`. `SsdpLogViewModel` is the same pattern, packaged as a VM that subscribes to an event instead of being called by the emitter. Read it for the canonical form.

### Latest tech / library notes

- **CommunityToolkit.Mvvm 8.4.0** (pinned in `Directory.Packages.props`, added Story 2.5). `[ObservableProperty] private bool _isAtTop;` generates the public `IsAtTop`. No new package, no `Directory.Packages.props` change for this story.
- **`ItemsRepeater`** ships in WindowsAppSDK (`Microsoft.UI.Xaml.Controls.ItemsRepeater`) — already referenced; no new using/package. It does NOT bring its own scrolling — it MUST sit inside a `ScrollViewer` (done in Task 4). `StackLayout` (not `StackPanel`) is the `ItemsRepeater` layout type.
- **`x:Bind` on a record property/method** defaults to `OneTime` — correct for the immutable `SsdpLogEntry` (Design Decision 9).

### Code-style + pattern compliance

- **Pattern 1:** file-scoped namespaces; `_camelCase` backing fields; PascalCase public members.
- **Pattern 2 (CoreAppBoundaryTests):** `SsdpLogEntry`, `SsdpLogKind`, `SsdpLogViewModel` live in `ohSpy.Core` and must NOT reference `Microsoft.UI.*` / `Microsoft.Windows.*` / `WinRT.Interop.*`. Only `CommunityToolkit.Mvvm` + BCL + Core types.
- **Pattern 7:** `SsdpLogViewModel` is constructed by `ShellViewModel` (per-VM, not DI) — only the root `ShellViewModel` is in the container.
- **Pattern 9:** `ObservableObject` base; `[ObservableProperty]`; `partial class`; cross-thread (here: same-thread defensive) mutation via `IUiDispatcher.Post`.
- **Pattern 13:** `x:Bind` + `x:DataType` in the new `DataTemplate`; code-behind is constructor-only EXCEPT the documented auto-follow scroll handlers (Design Decision 7) — comment them as the explicit exception; resource keys PascalCase.
- **Pattern 14/15 + A2:** test names `Method_Scenario_Expected_AC27n`; `[Trait("ac", "AC-2.7.<n>")]` (lowercase trait name, uppercase value).

### Anti-patterns to avoid

- **Don't re-parse the USN** — use `ann.Uuid ?? Guid.Empty` (Decision 1).
- **Don't log M-SEARCH responses / absent-NTS announcements** — NTS-only grammar (Decision 2). Don't copy `effectiveNt = NT ?? ST` from the registry path.
- **Don't remove the `IUiDispatcher.Post`** thinking it's redundant — keep it (Decision 3). And don't add a *second* marshal.
- **Don't change the `AnnouncementReceived` signature** to carry a timestamp — stamp `UtcNow` VM-side (Decision 4).
- **Don't re-implement FIFO eviction** in the VM — `PrependNewest` already does it (Decision 5).
- **Don't forget `IDisposable`/unsubscribe** — the singleton discovery service would otherwise pin the VM (Decision 6).
- **Don't put auto-follow logic in the VM** beyond the `IsAtTop` flag — scroll math is view-only (Decision 7). And don't bury business logic in the scroll handlers.
- **Don't use `Mode=OneWay`** on the immutable log-row bindings — `OneTime` (Decision 9), avoids `WMC1506`.
- **Don't wrap `ItemsRepeater` in a non-virtualising container** or disable the scroller's vertical scroll mode — that breaks FR-101.
- **Don't add a prepend-coalescing layer pre-emptively** — only if the burst smoke shows >16 ms stalls (scope-discipline note).

### Project Structure Notes

New Core files: `Models/SsdpLogKind.cs`, `Models/SsdpLogEntry.cs`, `ViewModels/SsdpLogViewModel.cs`.
Edited Core files: `ViewModels/ShellViewModel.cs`.
Edited App files: `MainWindow.xaml`, `MainWindow.xaml.cs`.
New test files: `Models/SsdpLogEntryTests.cs`, `ViewModels/SsdpLogViewModelTests.cs`, `Fakes/StubDiscoveryService.cs`.
No edited test files (ShellViewModel has no direct unit tests; the constructor change is source-compatible — it adds no new ctor params). No new project, no new package reference, no `Directory.Packages.props` change, no DI registration change.

Matches the architecture's planned tree: `Models/.../SsdpLogEntry.cs` (arch line 2121) and `ViewModels/SsdpLogViewModel.cs` (arch line 2135), bound in `MainWindow.xaml` right pane (arch line 2186).

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.7] (lines 1123–1171) — verbatim ACs.
- [Source: epics.md#Functional Requirements] (FR-003 line 60, FR-014 line 61, FR-015 line 62, FR-016 line 63, FR-055 line 64, FR-101 line 65) — log requirements.
- [Source: epics.md#NonFunctional Requirements] (NFR-P1 line 137, NFR-P5 line 141, NFR-UI4 line 149; chatty-SSDP target line 166) — virtualisation + perf bars.
- [Source: architecture.md#Decision 6 — Identity-Tracked Observable Collection Primitives] (lines 628–730) — `BoundedObservableCollection` contract, `SsdpLogViewModel.Entries` binding row (line 698), AC-6.1 (line 713), AC-6.6 (line 718), burst-coalescing follow-up (line 730).
- [Source: architecture.md#Integration Points — SSDP datagram flow] (lines 2227–2238) — `... → SsdpLogViewModel.Entries.PrependNewest(SsdpLogEntry) via IUiDispatcher`.
- [Source: architecture.md#Gap-2 FR-055 smart auto-follow] (line 3061) — `IsAtTop` observed from scroll position; auto-scroll only when `IsAtTop`.
- [Source: src/ohSpy.Core/Discovery/SsdpAnnouncement.cs] — `Uuid` (`Guid?`) already parsed from USN; `NTS` field for classification.
- [Source: src/ohSpy.Core/Discovery/DiscoveryService.cs] — `AnnouncementReceived` raised on the UI thread; registry routing uses `effectiveNt`, the log must NOT.
- [Source: src/ohSpy.Core/Discovery/IDiscoveryService.cs] — `event Action<SsdpAnnouncement> AnnouncementReceived`.
- [Source: src/ohSpy.Core/Collections/BoundedObservableCollection.cs] — `PrependNewest` / `Clear` notification contract (no `Reset` on incremental; `Reset` only on `Clear`).
- [Source: src/ohSpy.Core/Diagnostics/DiagnosticRingSink.cs] — canonical `BoundedObservableCollection` + `IUiDispatcher.Post(PrependNewest)` precedent.
- [Source: src/ohSpy.Core/ViewModels/DeviceTreeViewModel.cs] — `IDisposable` unsubscribe pattern to mirror.
- [Source: src/ohSpy.Core/ViewModels/ShellViewModel.cs] — pane-VM ownership + disposal wiring point.
- [Source: src/ohSpy.App/MainWindow.xaml] — right-pane placeholder to replace; `xmlns:vm`, resource keys.
- [Source: _bmad-output/implementation-artifacts/2-6-...md + deferred-work.md] — prior-story patterns; no open deferral applies to the log.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (dev-story workflow)

### Debug Log References

- `dotnet test tests/ohSpy.Core.Tests` — Failed: 0, Passed: **283**, Skipped: 2 (baseline 268 + 15 new). The 2 skips (`AsyncDisciplineTests`, `DiagCategoriesUsageTests`) are unchanged from the Story 2.6 baseline.
- `dotnet build src/ohSpy.App -c Debug -p:RuntimeIdentifier=win-x64` — **0 errors**. 1 warning: `WMC1506` at `MainWindow.xaml(121,37)` — this is the **pre-existing** Story 2.5 `FallbackTemplate` `{x:Bind Label, Mode=OneWay}` warning, shifted from line 120 → 121 by the one added `xmlns:m` line. **No new warnings**: the `ItemsRepeater` `ItemsSource` `Mode=OneWay` binding compiled clean, and all log-row bindings are `OneTime` (immutable record).
- `dotnet test --filter "category=chaos"` — exactly **1** passing (chaos suite unchanged).
- `dotnet test --filter "FullyQualifiedName~CoreAppBoundary"` — **4** passing (`SsdpLogEntry`, `SsdpLogKind`, `SsdpLogViewModel` are pure Core; no `Microsoft.UI.*` references).

### Completion Notes List

- **Uuid is already parsed (AC-2.7.1/2.7.2):** the VM consumes `ann.Uuid ?? Guid.Empty` — no USN re-parsing. Confirmed `SsdpAnnouncement.Uuid` (`Guid?`) is populated by `SsdpParser`.
- **NTS-only routing (AC-2.7.2):** `ClassifyOrNull` keys strictly on `NTS` (`ssdp:alive`/`ssdp:byebye`, case-insensitive); absent-NTS (M-SEARCH responses), `ssdp:update`, `ssdp:discover` are all ignored — deliberately narrower than `DiscoveryService.RouteOnUiThread`'s `effectiveNt = NT ?? ST` registry routing. Verified by `OtherNts_Ignored_AC272`.
- **`IUiDispatcher.Post` kept despite the event already being on the UI thread (AC-2.7.2):** documented in-code as a cheap FIFO same-thread re-queue matching the `DiagnosticRingSink` precedent; decouples the VM from the event's threading guarantee. Not a cross-thread hazard either way.
- **Timestamp stamped `DateTime.UtcNow` VM-side (AC-2.7.1):** the `AnnouncementReceived` event carries no arrival time and there is no clock seam in Core (`DiagnosticEmitter` precedent). Event signature left unchanged (no broader refactor).
- **FIFO eviction reuses the primitive (AC-2.7.3):** routing through `BoundedObservableCollection.PrependNewest` — `Eviction_AtCapacity_DropsTail_NoReset_AC273` raises 10,001 alives and asserts `Count == 10_000`, newest at top, oldest gone, and **no `Reset`** across the burst. The exact `Add(0)`+`Remove(10000)` index contract is already proven by `BoundedObservableCollectionTests` (Story 1.2).
- **`IDisposable` unsubscribe (AC-2.7.2):** mirrors the Story 2.5 `DeviceTreeViewModel` review patch; `Interlocked`-guarded; `ShellViewModel.DisposeAsync` calls `SsdpLog.Dispose()`. Verified by `Dispose_Unsubscribes_NoPrependAfterDispose_AC272`.
- **Auto-follow (AC-2.7.5):** `IsAtTop` (`[ObservableProperty]`, defaults `true`) lives in the VM (tested for default + shape); the scroll mechanics (`ScrollViewer.ViewChanged` → `IsAtTop`; `Entries.CollectionChanged` Add → `ChangeView` to anchor-at-top or offset-compensate) live in `MainWindow.xaml.cs` as the documented Pattern 13 exception. The runtime scroll behaviour is covered by the manual smoke (Task 9.5), not unit tests.
- **Display members on the record:** added `KindToken` (`"ALIVE"`/`"BYEBYE"`), `TimestampDisplay` (local-time `HH:mm:ss.fff`, InvariantCulture), and `UuidText` (`Uuid.ToString()`) so the XAML binds plain `OneTime` string properties — avoids fragile `x:Bind` `ToString()` syntax and any Guid→`Text` conversion ambiguity under `TreatWarningsAsErrors`. (`UuidText` is an additive robustness member beyond the Task 1.2 snippet; tested in `Record_HoldsTimestampKindUuid_AC271`.)
- **AC-2.7.7 forward-compat:** `SsdpLogViewModel.Clear()` ships + is unit-tested (`Clear_EmptiesEntries_EmitsSingleReset_AC277`); NO adapter-switch trigger wired — that call site lands in Story 5.2 (FR-050). No adapter-switch event exists yet to subscribe to.
- **No DI change, no new ctor params:** `SsdpLogViewModel` is constructed by `ShellViewModel` reusing the already-injected `discovery` + `ui` — so no `ServiceRegistration` edit and no existing-test edits were needed.
- **Task 9.5 (manual UI smoke) — NOT executed:** requires a running WinUI desktop session, unavailable in this headless dev environment (same constraint as Story 2.6's Task 12.5). The AC-gating *data* behaviours (routing, eviction, ignore-non-alive/byebye, Clear, dispose, capacity, IsAtTop default) are all covered by the 15 unit tests. The *view* behaviours it would confirm — virtualised render of `ALIVE`/`BYEBYE` rows newest-first (AC-2.7.4), auto-follow anchor at top / no-yank when scrolled away / re-engage at top (AC-2.7.5), and no-stutter under a ≥20 adv/s burst with visible-row-bound memory (AC-2.7.6) — can ONLY be confirmed with a live UI. **Recommend running before closing Epic 2.** Note: the auto-follow offset-compensation assumes uniform row heights; if the burst smoke shows >16 ms stalls, the architecture's conditional VM-side prepend-coalescing follow-up (Decision 6) would be the remedy.

### File List

**New (Core):**
- `src/ohSpy.Core/Models/SsdpLogKind.cs`
- `src/ohSpy.Core/Models/SsdpLogEntry.cs`
- `src/ohSpy.Core/ViewModels/SsdpLogViewModel.cs`

**Modified (Core):**
- `src/ohSpy.Core/ViewModels/ShellViewModel.cs`

**Modified (App):**
- `src/ohSpy.App/MainWindow.xaml`
- `src/ohSpy.App/MainWindow.xaml.cs`

**New (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/StubDiscoveryService.cs`
- `tests/ohSpy.Core.Tests/Models/SsdpLogEntryTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/SsdpLogViewModelTests.cs`

### Change Log

| Date | Change |
|---|---|
| 2026-06-03 | Story 2.7 context created via bmad-create-story (claude-opus-4-8[1m]); backlog → ready-for-dev. |
| 2026-06-03 | Story 2.7 implemented (dev-story, claude-opus-4-8[1m]): `SsdpLogKind` + `SsdpLogEntry` record (with `KindToken`/`TimestampDisplay`/`UuidText` display members); `SsdpLogViewModel` (NTS-only alive/byebye routing → `BoundedObservableCollection(10_000)` via `IUiDispatcher.Post`, `IsAtTop`, `IDisposable`, `Clear()`); wired into `ShellViewModel` (no new ctor args / no DI change); `MainWindow.xaml` right pane = `ItemsRepeater`+`ScrollViewer` (FR-101) with `OneTime` row template; `MainWindow.xaml.cs` smart auto-follow scroll handlers (FR-055, documented Pattern 13 exception). 15 new tests (268→283), chaos unchanged (1), CoreAppBoundary 4 green. App build 0 errors, 1 pre-existing benign WMC1506 (no new warnings). Task 9.5 manual UI smoke not executed (headless). |
| 2026-06-03 | Story 2.7 review → done by code-review workflow (claude-sonnet-4-6, bmad-code-review). APPROVED. 0 patches, 1 deferred (OnLogEntriesChanged NewStartingIndex defensiveness). Build verified 0 errors/0 warnings (dev's "1 WMC1506" claim is a local-incremental-build artifact; clean dotnet build is warning-free). Tests 283 passed/2 skipped confirmed. Chaos=1, CoreAppBoundary=4 green. All AC-2.7.1..2.7.7 satisfied. Task 9.5 manual UI smoke remains pending before Epic 2 close. |

## Senior Developer Review (AI)

**Reviewer:** claude-sonnet-4-6 (bmad-code-review workflow, 2026-06-03)
**Baseline commit:** `d417fad1447f7ad03b5fff7ad55cd003ffd8360c`
**Diff scope:** All uncommitted changes in the working tree (modified tracked + untracked new files)
**Build verified:** `dotnet build src/ohSpy.App/ohSpy.App.csproj -c Debug -p:RuntimeIdentifier=win-x64 --nologo` — **0 errors, 0 warnings**. Dev claim of "1 pre-existing WMC1506" is a local-incremental-build artifact; clean build from repo root is warning-free. Build claim CORRECTED: 0 warnings (better than claimed).
**Test verified:** `dotnet test tests/ohSpy.Core.Tests` — **283 passed, 2 skipped, 0 failed**. Test claim CONFIRMED (268 baseline + 15 new).
**Chaos suite:** 1 passing (unchanged). CONFIRMED.
**CoreAppBoundaryTests:** 4 passing (`SsdpLogEntry`, `SsdpLogKind`, `SsdpLogViewModel` are pure Core — no `Microsoft.UI.*` references). CONFIRMED.

### Review Findings

- [x] [Review][Defer] `OnLogEntriesChanged` does not check `e.NewStartingIndex == 0` [`src/ohSpy.App/MainWindow.xaml.cs:47`] — The handler guards on `e.Action != Add` but does not assert `e.NewStartingIndex == 0`. `BoundedObservableCollection.PrependNewest` always emits `Add(index=0)`, so this is safe today. If the collection ever gains an append operation (e.g., an `AppendOldest` for import), any `Add` at index > 0 would incorrectly trigger the scroll-to-top or offset-compensation logic. `BoundedObservableCollection` is sealed and `PrependNewest` is its only Add operation, so no real risk this story; deferring as a defensive concern. — deferred, pre-existing design property of `BoundedObservableCollection`.

### Review Follow-ups (AI)

**Approved.** The implementation is architecturally sound, fully satisfies all seven ACs, and has no defects.

**Key design decisions verified as correct:**
- NTS-only classification in `ClassifyOrNull` is correctly narrower than `DiscoveryService.RouteOnUiThread`'s `effectiveNt = NT ?? ST`. M-SEARCH responses (absent NTS) are correctly excluded from the log per FR-014/FR-015.
- `IUiDispatcher.Post` kept despite same-thread event firing — correctly matches the `DiagnosticRingSink` precedent; cheap FIFO re-queue, decouples VM from event threading guarantee. No double-marshal issue: the outer `Post` (in `ReadLoopAsync`) schedules `RouteOnUiThread`, which synchronously raises `AnnouncementReceived`, which triggers the VM's inner `_ui.Post(() => Entries.PrependNewest(entry))`. Both Posts are on the same DispatcherQueue; FIFO ordering is preserved.
- `BoundedObservableCollection.PrependNewest` (Add(0)+Remove(10000), never Reset) is used correctly — eviction test confirms routing goes through the primitive, not re-implemented.
- `Interlocked.Exchange` dispose guard is idempotent and mirrors `DeviceTreeViewModel` exactly.
- `ShellViewModel.DisposeAsync` order: `_discovery.DisposeAsync()` drains the read loop (loop already cancelled by `adapterToken`), then `SsdpLog.Dispose()` unsubscribes. Any remaining queued `ui.Post` callbacks that fire between drain and unsubscribe would add harmless entries; callbacks that fire after unsubscribe are no-ops (handler detached). No race defect.
- `UuidText` computed member (`Uuid.ToString()`) is a correct additive robustness improvement over direct `{x:Bind Uuid}` (avoids Guid→TextBlock implicit conversion under `TreatWarningsAsErrors`). Deviates from Task 4.2 snippet but is fully documented and correctly implemented.
- Auto-follow `AtTopThresholdPx` dual use (threshold + row-height compensation) is intentional and correctly specified. At the threshold boundary (offset=24), re-anchor to 0 is a cosmetic 24px snap — acceptable per story scope.
- `IsAtTop` generated setter is written from the view (`ViewModel.SsdpLog.IsAtTop = ...`). The `[ObservableProperty]` setter raises `PropertyChanged("IsAtTop")` but no consumer in Core observes it, so no spurious re-entrancy.

**Not actionable now:**
- Task 9.5 manual UI smoke test (AC-2.7.4/5/6 view behaviours) remains unexecuted (headless environment). Recommend running before closing Epic 2 — this covers: virtualised row render, auto-follow anchor/no-yank/re-engage, and chatty-burst perf (≥20 adv/s, ≥30 s, no visible stutter, visible-row-bound memory). If the burst smoke shows >16 ms stalls, the architecture's conditional VM-side prepend-coalescing layer is the remedy (architecture Decision 6, line 730).
- Dev Agent Record claims "1 pre-existing benign WMC1506" warning: this is a local incremental-build artifact. Clean `dotnet build` from repo root is warning-free. The FallbackTemplate `{x:Bind Label, Mode=OneWay}` on `INodeViewModel` may show WMC1506 in VS incremental builds only. No action required — the CI gate is clean.
