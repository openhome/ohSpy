---
baseline_commit: 14fb30be47b520945bbf06b8d1083711840ee3c7
---

# Story 2.2: Network Adapter Enumerator + Adapter Scope + Startup Bind

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want ohSpy to enumerate every eligible IPv4 adapter at startup, default to the first one, bind the SSDP transport to it inside a cancellation scope, and degrade gracefully when there are no eligible adapters,
so that the tool runs deterministically on the developer's primary network without configuration — and never crashes on a host that happens to be offline.

## Acceptance Criteria

**Verbatim ACs derived from epics.md §Story 2.2 (lines 806–846). AC trait IDs follow Amendment A2 (`AC-2.2.<n>`).**

**AC-2.2.1 — Enumerator eligibility, ordering, and display fields (FR-048)**

**Given** `ohSpy.Core/Discovery/NetworkAdapterEnumerator.cs`
**When** I call `Enumerate()` on a typical Windows host
**Then** it returns the list of eligible IPv4 adapters — **operational** (`OperationalStatus.Up`), **non-loopback** (`NetworkInterfaceType != Loopback`), **multicast-capable** (`SupportsMulticast == true`), and possessing at least one **IPv4** (`AddressFamily.InterNetwork`) unicast address — with **stable enumeration ordering** (FR-048: order preserved from the underlying source so "first eligible" is deterministic across calls)
**And** each entry (`NetworkAdapter` record) exposes the friendly **Name**, **Description**, and the selected **IPv4** address suitable for the future `View → Network adapter` radio list (E5 / Story 5.2 consumer)
**And** when an eligible adapter carries multiple IPv4 unicast addresses, the **first** is selected as `IPv4`

**AC-2.2.2 — Testable interface seam over the BCL (FR-048 test contract)**

**Given** `ohSpy.Core/Discovery/INetworkInterfaceSource.cs` and the `AdapterCandidate` projection record
**When** I inspect them
**Then** `INetworkInterfaceSource.GetCandidates()` returns `IReadOnlyList<AdapterCandidate>` — a pure-data projection of each NIC's `Name`, `Description`, `OperationalStatus`, `InterfaceType`, `SupportsMulticast`, and `UnicastAddresses` (so eligibility filtering is unit-testable WITHOUT constructing a `System.Net.NetworkInformation.NetworkInterface`, which is sealed and not mockable)
**And** `NetworkAdapterEnumerator` depends only on `INetworkInterfaceSource`, so a stub source can simulate zero / one / many adapters in unit tests
**And** the live implementation `LiveNetworkInterfaceSource` projects from `NetworkInterface.GetAllNetworkInterfaces()` and is the only type that touches the BCL NIC API

**AC-2.2.3 — Adapter-level cancellation scope (Decision 7)**

**Given** the application starts and an `AdapterScope` is constructed
**When** I inspect it
**Then** it owns `_adapterCts = CancellationTokenSource.CreateLinkedTokenSource(appToken)` — the **adapter** level of the D7 cancellation hierarchy (`app → adapter → device → popup`)
**And** the **app**-level `_appCts` lives in `App` (Decision 7 ownership map: "App | App startup composition") and its `appToken` is passed into the `AdapterScope`

**AC-2.2.4 — Default-to-first-eligible selection (FR-048)**

**Given** the `AdapterScope` starts on a host with ≥ 1 eligible adapter
**When** selection runs
**Then** it selects the **FIRST** eligible adapter from `NetworkAdapterEnumerator.Enumerate()` (FR-048: default at launch is the first eligible adapter)
**And** `CurrentAdapterIPv4` reflects that adapter's IPv4 address after selection

**AC-2.2.5 — Startup transport bind + initial M-SEARCH (FR-004)**

**Given** an eligible adapter was selected
**When** the `AdapterScope` starts
**Then** it constructs / uses an `ISsdpTransport` bound to that adapter and awaits `StartAsync(currentAdapterIPv4, _adapterCts.Token)`
**And** it issues the initial discovery M-SEARCH via `SendMSearchAsync(TimeSpan.FromSeconds(5), _adapterCts.Token)` (FR-004 startup discovery)
**And** these calls are made on a background path so they never block the WinUI UI thread (Pattern 6)

**AC-2.2.6 — Graceful zero-adapter degradation (NFR-R5 + FR-048)**

**Given** a host with ZERO eligible adapters
**When** the app launches
**Then** the app does **NOT** crash and does **NOT** show an error dialog
**And** the main window opens with an empty device tree (the tree itself is Story 2.5; here "empty" means the app reaches its normal idle window state)
**And** a single `Warning` diagnostic is emitted: `diag.Warning(DiagCategories.AdapterSwitch, "no eligible adapters at startup")` (no `DiagnosticContext` fields required — Pattern 11 table: `Adapter.Switch.*` requires "none beyond message")
**And** the transport is **NOT** started (no socket bind is attempted) and `CurrentAdapterIPv4` is `null`
**And** the app remains interactive (menus openable, diagnostics viewable later)

**AC-2.2.7 — DI composition (Pattern 7)**

**Given** the DI composition root (`src/ohSpy.App/Composition/ServiceRegistration.cs`)
**When** the App starts
**Then** `INetworkInterfaceSource` → `LiveNetworkInterfaceSource` and `INetworkAdapterEnumerator` → `NetworkAdapterEnumerator` are registered as **singletons**
**And** `AdapterScope` is **NOT** registered as a long-lived DI singleton — it is constructed by the app-startup orchestrator (`App.OnLaunched` interim home; relocated into `ShellViewModel` by Story 2.5), because its lifetime is bounded by adapter selection, not the process (Pattern 7 + Decision 7)

**AC-2.2.8 — Future-proof AdapterScope shape for the E5 atomic switch (FR-050)**

**Given** the future adapter-switch use case (Story 5.2)
**When** I look at the `AdapterScope` surface
**Then** it exposes `IPAddress? CurrentAdapterIPv4` (null when no adapter selected), a `CancellationToken AdapterToken` (= `_adapterCts.Token`), and `IAsyncDisposable.DisposeAsync()`
**And** `DisposeAsync()` cancels `_adapterCts` (`await CancelAsync()`), tears down the transport (`await _transport.DisposeAsync()`), and **completes within the FR-050 2 s budget** — emitting `Warning(DiagCategories.AdapterSwitchTimeout, …)` if teardown exceeds the budget
**And** `DisposeAsync()` is idempotent (a second call is a no-op, does not throw)
**And** this story only **scaffolds** the shape; the full FR-050 atomic-switch *sequence* (Decision 7 lines 818–829) lands in Story 5.2 — do NOT implement registry-clear / callback-host teardown here (those types don't exist yet)

**AC-2.2.9 — Test suite (Pattern 14/15 + Amendment A2)**

**Given** the test suite
**When** I run the adapter tests
**Then** unit tests drive `NetworkAdapterEnumerator` via a **stubbed `INetworkInterfaceSource`** simulating zero / one / many adapters, plus filtering cases (down, loopback, non-multicast, IPv6-only) and stable ordering
**And** `AdapterScope` is unit-tested via a **fake `ISsdpTransport`** capturing `StartAsync` / `SendMSearchAsync` / `DisposeAsync` calls (zero-adapter ⇒ no start + one Warning; one-adapter ⇒ start with correct IP + M-SEARCH; dispose ⇒ token cancelled + transport disposed + idempotent)
**And** an **integration test** asserts that on the dev machine at least one eligible adapter is enumerated, carrying `[Trait("category", "integration")]` so the chaos-hook filter (`category=chaos`) does NOT pick it up
**And** every AC-traceable test carries `[Trait("ac", "AC-2.2.<n>")]` (Amendment A2)

## Tasks / Subtasks

### Task 1 — Models: `NetworkAdapter` + `AdapterCandidate` (AC: #1, #2)

- [x] **1.1** Create `src/ohSpy.Core/Models/NetworkAdapter.cs` — the public, display-facing eligible-adapter record (consumed by the future `View → Network adapter` radio list in Story 5.2):
  ```csharp
  namespace ohSpy.Core.Models;

  using System.Net;

  /// <summary>
  /// An eligible IPv4 network adapter (FR-048). Display-facing: the friendly
  /// <see cref="Name"/> + <see cref="IPv4"/> populate the future View → Network
  /// adapter radio list (Story 5.2). <see cref="IPv4"/> is the address the SSDP
  /// transport binds to.
  /// </summary>
  public sealed record NetworkAdapter(string Name, string Description, IPAddress IPv4);
  ```
- [x] **1.2** Create `src/ohSpy.Core/Models/AdapterCandidate.cs` — the raw NIC projection that decouples eligibility filtering from the unmockable BCL `NetworkInterface`:
  ```csharp
  namespace ohSpy.Core.Models;

  using System.Net;
  using System.Net.NetworkInformation;

  /// <summary>
  /// Pure-data projection of one OS network interface (Decision: testability seam
  /// for FR-048). <see cref="NetworkInterface"/> is sealed and not constructible in
  /// tests; this record carries exactly the fields the eligibility filter needs so
  /// <c>NetworkAdapterEnumerator</c> is unit-testable via a stubbed source.
  /// </summary>
  public sealed record AdapterCandidate(
      string Name,
      string Description,
      OperationalStatus OperationalStatus,
      NetworkInterfaceType InterfaceType,
      bool SupportsMulticast,
      IReadOnlyList<IPAddress> UnicastAddresses);
  ```
- [x] **1.3** File-scoped namespaces, one type per file (Pattern 1). Both records are `public sealed record` (Pattern 9 — immutable data carriers).

### Task 2 — Interface seam: `INetworkInterfaceSource` + `LiveNetworkInterfaceSource` (AC: #2)

- [x] **2.1** Create `src/ohSpy.Core/Discovery/INetworkInterfaceSource.cs`:
  ```csharp
  namespace ohSpy.Core.Discovery;

  using ohSpy.Core.Models;

  /// <summary>
  /// Abstraction over <c>NetworkInterface.GetAllNetworkInterfaces()</c> so adapter
  /// eligibility filtering is unit-testable (FR-048 test contract). The live impl is
  /// the ONLY type that touches the BCL NIC API; tests inject a stub returning
  /// synthetic <see cref="AdapterCandidate"/>s.
  /// </summary>
  public interface INetworkInterfaceSource
  {
      IReadOnlyList<AdapterCandidate> GetCandidates();
  }
  ```
- [x] **2.2** Create `src/ohSpy.Core/Discovery/LiveNetworkInterfaceSource.cs` — `internal sealed` (Pattern 7; registered behind the interface). Project each NIC; preserve source ordering for FR-048 stability:
  ```csharp
  internal sealed class LiveNetworkInterfaceSource : INetworkInterfaceSource
  {
      public IReadOnlyList<AdapterCandidate> GetCandidates()
      {
          var result = new List<AdapterCandidate>();
          foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
          {
              var ipv4 = nic.GetIPProperties().UnicastAddresses
                  .Select(u => u.Address)
                  .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                  .ToArray();

              result.Add(new AdapterCandidate(
                  nic.Name,
                  nic.Description,
                  nic.OperationalStatus,
                  nic.NetworkInterfaceType,
                  nic.SupportsMulticast,
                  ipv4));
          }
          return result;
      }
  }
  ```
- [x] **2.2a** **Order matters for FR-048:** `GetAllNetworkInterfaces()` order is preserved into the result list and never re-sorted downstream — "first eligible" must be deterministic. Do not `OrderBy` the candidates.
- [x] **2.3** Required usings: `System.Linq`, `System.Net`, `System.Net.NetworkInformation`, `System.Net.Sockets` (for `AddressFamily`), `ohSpy.Core.Models`. No new package — `System.Net.NetworkInformation` is in the `net10.0` shared framework.

### Task 3 — Enumerator: `INetworkAdapterEnumerator` + `NetworkAdapterEnumerator` (AC: #1, #2)

- [x] **3.1** Create `src/ohSpy.Core/Discovery/INetworkAdapterEnumerator.cs`:
  ```csharp
  namespace ohSpy.Core.Discovery;

  using ohSpy.Core.Models;

  /// <summary>
  /// Enumerates eligible IPv4 adapters (FR-048): operational, non-loopback,
  /// multicast-capable, with at least one IPv4 unicast address. Stable ordering —
  /// the first entry is the launch default. Consumed by <c>AdapterScope</c> (startup
  /// bind) and the View → Network adapter menu (Story 5.2).
  /// </summary>
  public interface INetworkAdapterEnumerator
  {
      IReadOnlyList<NetworkAdapter> Enumerate();
  }
  ```
- [x] **3.2** Create `src/ohSpy.Core/Discovery/NetworkAdapterEnumerator.cs` — `internal sealed`, primary ctor over the source (Pattern 8):
  ```csharp
  internal sealed class NetworkAdapterEnumerator(INetworkInterfaceSource source)
      : INetworkAdapterEnumerator
  {
      public IReadOnlyList<NetworkAdapter> Enumerate()
      {
          var result = new List<NetworkAdapter>();
          foreach (var c in source.GetCandidates())
          {
              if (c.OperationalStatus != OperationalStatus.Up) continue;
              if (c.InterfaceType == NetworkInterfaceType.Loopback) continue;
              if (!c.SupportsMulticast) continue;

              var ipv4 = c.UnicastAddresses
                  .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
              if (ipv4 is null) continue; // no IPv4 ⇒ ineligible

              result.Add(new NetworkAdapter(c.Name, c.Description, ipv4));
          }
          return result;
      }
  }
  ```
- [x] **3.3** **Eligibility predicate is the heart of this story** — each filter clause maps to an AC-2.2.1 condition. Keep them as explicit, separately-testable `continue` guards (one stub-source unit test per rejection reason).
- [x] **3.4** No diagnostics emitted from the enumerator — it is a pure query. (The zero-adapter Warning is emitted by `AdapterScope`, not here — AC-2.2.6.)

### Task 4 — `AdapterScope` (AC: #3, #4, #5, #6, #8)

- [x] **4.1** Create `src/ohSpy.Core/Discovery/AdapterScope.cs` — `internal sealed` (no DI registration; constructed by the app-startup orchestrator — AC-2.2.7). Implements `IAsyncDisposable`. Traditional ctor (non-trivial init: builds the linked CTS) per Pattern 8:
  ```csharp
  /// <summary>
  /// The adapter level of the Decision 7 cancellation hierarchy. Owns one
  /// <c>ISsdpTransport</c>, selects the launch-default adapter (FR-048), binds the
  /// transport, and issues the startup M-SEARCH (FR-004). Lifetime is bounded by
  /// adapter selection — constructed by the app-startup orchestrator (App.OnLaunched
  /// now; ShellViewModel in Story 2.5), NOT a DI singleton.
  /// <para>
  /// This story scaffolds the FR-050 atomic-switch SHAPE only (CurrentAdapterIPv4 /
  /// AdapterToken / budgeted DisposeAsync). The full switch sequence is Story 5.2.
  /// </para>
  /// </summary>
  internal sealed class AdapterScope : IAsyncDisposable
  {
      private static readonly TimeSpan SwitchBudget = TimeSpan.FromSeconds(2); // FR-050
      private static readonly TimeSpan InitialMx = TimeSpan.FromSeconds(5);    // FR-004

      private readonly INetworkAdapterEnumerator _enumerator;
      private readonly ISsdpTransport _transport;
      private readonly IDiagnosticEmitter _diag;
      private readonly CancellationTokenSource _adapterCts;
      private bool _transportStarted;
      private int _disposed;

      public IPAddress? CurrentAdapterIPv4 { get; private set; }
      public CancellationToken AdapterToken => _adapterCts.Token;

      public AdapterScope(
          INetworkAdapterEnumerator enumerator,
          ISsdpTransport transport,
          IDiagnosticEmitter diag,
          CancellationToken appToken)
      {
          _enumerator = enumerator;
          _transport = transport;
          _diag = diag;
          // Decision 7: adapter level is linked to the app level.
          _adapterCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
      }
  }
  ```
- [x] **4.2** `StartAsync()` — selection + bind + M-SEARCH; never throws on the zero-adapter path (NFR-R5):
  ```csharp
  public async Task StartAsync()
  {
      var adapters = _enumerator.Enumerate();
      if (adapters.Count == 0)
      {
          // NFR-R5 + FR-048: zero-adapter host still runs. No crash, no dialog.
          _diag.Warning(DiagCategories.AdapterSwitch, "no eligible adapters at startup");
          return;
      }

      var selected = adapters[0]; // FR-048: launch default = first eligible
      CurrentAdapterIPv4 = selected.IPv4;

      await _transport.StartAsync(selected.IPv4, _adapterCts.Token).ConfigureAwait(false);
      _transportStarted = true;
      await _transport.SendMSearchAsync(InitialMx, _adapterCts.Token).ConfigureAwait(false);
  }
  ```
- [x] **4.3** `DisposeAsync()` — idempotent, FR-050-budgeted teardown (scaffold only — AC-2.2.8):
  ```csharp
  public async ValueTask DisposeAsync()
  {
      if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

      // 1. Signal cascade (Decision 7 step 1).
      try { await _adapterCts.CancelAsync().ConfigureAwait(false); }
      catch (ObjectDisposedException) { }

      // 2. Tear down the transport within the FR-050 2 s budget (Decision 7 step 2).
      if (_transportStarted)
      {
          using var budget = new CancellationTokenSource(SwitchBudget);
          var teardown = _transport.DisposeAsync().AsTask();
          var finished = await Task.WhenAny(
              teardown, Task.Delay(Timeout.Infinite, budget.Token)).ConfigureAwait(false);
          if (finished != teardown)
          {
              _diag.Warning(DiagCategories.AdapterSwitchTimeout,
                  "adapter teardown exceeded budget");
          }
          else
          {
              try { await teardown.ConfigureAwait(false); } catch { /* tolerated during teardown */ }
          }
      }

      _adapterCts.Dispose();
  }
  ```
- [x] **4.3a** **Bare `catch` justification (Pattern 10):** the inner `await teardown` swallow is a teardown-race tolerance — the same documented precedent as `SsdpTransport.DisposeAsync` and `DiagnosticFileSink`. Narrow it if the dev agent finds a typed catch fits.
- [x] **4.3b** **Do NOT** dispose the transport if it was never started (zero-adapter path) — `_transportStarted` guards it. (The transport's own `DisposeAsync` is idempotent per AC-2.1.7, but skipping the call keeps intent clear.)
- [x] **4.4** Required usings: `System.Net`, `System.Threading`, `System.Threading.Tasks`, `ohSpy.Core.Diagnostics`, `ohSpy.Core.Models`.

### Task 5 — DI registration (AC: #7)

- [x] **5.1** In `src/ohSpy.App/Composition/ServiceRegistration.cs`, add after the Story 2.1 SSDP-transport registration:
  ```csharp
  // Story 2.2 — Network adapter enumeration (FR-048). Singletons: stateless query
  // services. AdapterScope is NOT registered here — it is constructed by the
  // app-startup orchestrator (App.OnLaunched; relocated to ShellViewModel in 2.5)
  // because its lifetime is bounded by adapter selection (Pattern 7 + Decision 7).
  services.AddSingleton<INetworkInterfaceSource, LiveNetworkInterfaceSource>();
  services.AddSingleton<INetworkAdapterEnumerator, NetworkAdapterEnumerator>();
  ```
- [x] **5.2** The `using ohSpy.Core.Discovery;` directive already exists (Story 2.1). No new using needed; both types live in `Discovery`.
- [x] **5.3** **Do NOT** register `AdapterScope`. **Do NOT** change the existing `ISsdpTransport` singleton registration (see Task 6 note on the single-scope-no-switch model).

### Task 6 — App-startup wiring in `App.OnLaunched` (AC: #3, #5, #6)

- [x] **6.1** Add an app-level `CancellationTokenSource` field to `App` (Decision 7: the `_appCts` permanent home is `App`):
  ```csharp
  private readonly CancellationTokenSource _appCts = new();
  private AdapterScope? _adapterScope;
  ```
- [x] **6.2** In `OnLaunched`, AFTER the existing ring-sink wiring and BEFORE/with window activation, construct the scope and kick startup on a background path (OnLaunched is `void` — must not block the UI thread; Pattern 6):
  ```csharp
  // Story 2.2: construct the adapter scope (Decision 7 adapter level) and bind the
  // SSDP transport to the launch-default adapter (FR-048 + FR-004). Interim home —
  // Story 2.5 relocates this into ShellViewModel. Fire-and-forget: StartAsync is
  // self-contained and never throws on the zero-adapter path (NFR-R5).
  _adapterScope = new AdapterScope(
      Services.GetRequiredService<INetworkAdapterEnumerator>(),
      Services.GetRequiredService<ISsdpTransport>(),
      Services.GetRequiredService<IDiagnosticEmitter>(),
      _appCts.Token);
  _ = _adapterScope.StartAsync();

  _window = new MainWindow();
  _window.Closed += OnWindowClosed;
  _window.Activate();
  ```
- [x] **6.3** Add an async window-closed handler for clean shutdown (cancels the app token, disposes the scope — exercises the AC-2.2.8 teardown path at process exit):
  ```csharp
  private async void OnWindowClosed(object sender, WindowEventArgs args)
  {
      if (_adapterScope is not null)
          await _adapterScope.DisposeAsync();
      _appCts.Cancel();
      _appCts.Dispose();
  }
  ```
- [x] **6.4** **`async void` is acceptable here** — it is a WinUI event handler, the one sanctioned `async void` case (Pattern 6). Keep the body trivially exception-safe (`DisposeAsync` already swallows teardown races).
- [x] **6.5** Add usings to `App.xaml.cs`: `ohSpy.Core.Discovery` (for `AdapterScope`, `INetworkAdapterEnumerator`, `ISsdpTransport`), and confirm `Microsoft.UI.Xaml` is present for `WindowEventArgs`. `IDiagnosticEmitter` is in `ohSpy.Core.Diagnostics` (already imported).
- [x] **6.6** **Fire-and-forget caveat:** `_ = _adapterScope.StartAsync();` discards the Task deliberately. Because `StartAsync` swallows the zero-adapter case and the transport's own receive loops swallow socket faults (AC-2.1.8), no unobserved exception should escape. If the dev agent prefers, wrap in a local `async` helper with a `try/catch` that emits `Warning(DiagCategories.AdapterSwitch, …)` on unexpected failure — but do NOT let startup throw.

### Task 7 — Tests: enumerator (AC: #1, #2, #9)

**Location:** `tests/ohSpy.Core.Tests/Discovery/NetworkAdapterEnumeratorTests.cs` (mirror-tree, Pattern 5). Carry `[Trait("ac", "AC-2.2.<n>")]`; unit tests need NO `category` trait (they're fast and pure).

- [x] **7.1** Add a stub source `tests/ohSpy.Core.Tests/Fakes/StubNetworkInterfaceSource.cs` returning a caller-supplied `IReadOnlyList<AdapterCandidate>` (mirrors `CapturingDiagnosticEmitter` style — `internal sealed`). Provide a small builder/helper to make `AdapterCandidate`s terse.
- [x] **7.2** `Enumerate_NoCandidates_ReturnsEmpty_AC221` — zero candidates ⇒ empty list.
- [x] **7.3** `Enumerate_OneEligible_ReturnsIt_AC221` — single Up/non-loopback/multicast/IPv4 candidate ⇒ one `NetworkAdapter` with matching Name/Description/IPv4.
- [x] **7.4** Filtering cases (one `[Fact]` each, all `AC-2.2.1`): down (`OperationalStatus.Down`) filtered; loopback (`NetworkInterfaceType.Loopback`) filtered; non-multicast (`SupportsMulticast == false`) filtered; IPv6-only (only `AddressFamily.InterNetworkV6` addresses) filtered. Consider a `[Theory]` with `[InlineData]` for the rejection matrix if it reads cleaner (Pattern 14).
- [x] **7.5** `Enumerate_PreservesSourceOrder_AC221` — three eligible candidates in a known order ⇒ result order matches the source (FR-048 stable-ordering / "first eligible is deterministic").
- [x] **7.6** `Enumerate_MultipleIPv4_PicksFirst_AC221` — candidate with two IPv4 unicast addresses ⇒ the first is chosen.
- [x] **7.7** **Integration:** `Enumerate_DevMachine_HasAtLeastOneEligible_AC229` — `new NetworkAdapterEnumerator(new LiveNetworkInterfaceSource()).Enumerate()` returns ≥ 1 on the dev box. Carry `[Trait("category", "integration")]` (Pattern 14) — NOT `chaos`. **Epic-1 retro action B ("trivially passing is a red flag"):** if this returns 0 on a machine that obviously has a live adapter, diagnose (all-down? virtual-only? multicast flag off on a VPN NIC?) before accepting.

### Task 8 — Tests: AdapterScope (AC: #3, #4, #5, #6, #8, #9)

**Location:** `tests/ohSpy.Core.Tests/Discovery/AdapterScopeTests.cs`. Use `CapturingDiagnosticEmitter` for emission assertions.

- [x] **8.1** Add a fake transport `tests/ohSpy.Core.Tests/Fakes/FakeSsdpTransport.cs` (`internal sealed : ISsdpTransport`) that records `StartAsync(ip, ct)` args, `SendMSearchAsync(mx, ct)` args, and `DisposeAsync` call count; exposes a no-op `IncomingDatagrams` (`Channel.CreateBounded<SsdpDatagram>(1).Reader`). Optionally support an injectable teardown delay to test the FR-050 budget path (8.7).
- [x] **8.2** Add a tiny in-test enumerator stub (or reuse `NetworkAdapterEnumerator` over `StubNetworkInterfaceSource`) so the scope can be driven with zero / one / many adapters.
- [x] **8.3** `StartAsync_OneAdapter_StartsTransportWithSelectedIp_AC224_AC225` — one eligible adapter ⇒ `FakeSsdpTransport.StartAsync` received that exact IPv4; `SendMSearchAsync` received `5 s`; `CurrentAdapterIPv4` equals the adapter IP.
- [x] **8.4** `StartAsync_ZeroAdapters_EmitsWarningDoesNotStart_AC226` — zero adapters ⇒ transport `StartAsync` never called; exactly one `Warning` with category `Adapter.Switch` and message `"no eligible adapters at startup"`; `CurrentAdapterIPv4` is `null`; no throw.
- [x] **8.5** `AdapterToken_LinkedToAppToken_AC223` — cancel the supplied `appToken` ⇒ `scope.AdapterToken.IsCancellationRequested` becomes true (proves the D7 linkage).
- [x] **8.6** `DisposeAsync_CancelsTokenAndDisposesTransport_AC228` — after `StartAsync` with one adapter, `DisposeAsync` ⇒ `AdapterToken` cancelled AND `FakeSsdpTransport` disposed once.
- [x] **8.7** `DisposeAsync_TransportNeverStarted_DoesNotDisposeTransport_AC228` — zero-adapter scope ⇒ `DisposeAsync` does not call transport `DisposeAsync`; completes without throw.
- [x] **8.8** `DisposeAsync_CalledTwice_Idempotent_AC228` — second `DisposeAsync` is a no-op, no throw, transport disposed at most once.
- [x] **8.9** *(Optional, if 8.1 supports a delay)* `DisposeAsync_TeardownExceedsBudget_EmitsTimeoutWarning_AC228` — fake transport teardown delayed > 2 s ⇒ `Warning(Adapter.Switch.Timeout, …)` emitted and `DisposeAsync` returns near the budget, not after the full delay. Keep the delay modest (e.g. budget shortened via the test seam if you expose `SwitchBudget`, OR accept a ~2 s test and tag `[Trait("category", "integration")]`). Prefer a seam over a real 2 s wait.

### Task 9 — Final verification (AC: all)

- [x] **9.1** `dotnet build` succeeds with `0 Warning(s), 0 Error(s)` under `TreatWarningsAsErrors=true`. **Compile the skeletons first (epic-1 retro action A)** — expect to add explicit usings and possibly fix `CA`/`VSTHRD` analyzer nits (e.g. `VSTHRD003` on awaiting the fire-and-forget Task, `CA2007`/`ConfigureAwait` in Core). Fix, don't suppress, unless a documented `.editorconfig` exemption (A11) already covers it.
- [x] **9.2** `dotnet test` green. Story 2.1 left **140 passing + 2 skipped (142 total)**. Story 2.2 adds ~14–16 tests; target ~156. NetArchTest `CoreAppBoundaryTests` still passes — all new Core types reference only BCL + `ohSpy.Core.Diagnostics`/`ohSpy.Core.Models`/`ohSpy.Core.Discovery` (no WinUI / WindowsAppSDK / `ohSpy.App`).
- [x] **9.3** `dotnet test --filter "category=chaos"` still runs exactly **1** test (unchanged — Story 2.2 adds NO chaos tests).
- [x] **9.4** **Manual smoke (recommended, not AC-gating):** launch `ohSpy.App` on Simon's LAN. Expect: window opens; no crash; the transport binds the first eligible adapter and the M-SEARCH egresses (verify via Wireshark on `239.255.255.250:1900`, or just confirm no exception + the bind succeeds). On a deliberately disconnected NIC set (disable all adapters), confirm the window still opens and the `"no eligible adapters at startup"` Warning appears in the file-sink log. **Datagrams will accumulate unconsumed** in the transport's `DropOldest(4096)` channel — that is expected; the consumer (`DiscoveryService`) arrives in Story 2.4. Do NOT commit any smoke-runner code.

## Dev Notes

### Architectural pillars this story implements

| Architecture decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **FR-048 — Adapter enumeration** | `NetworkAdapterEnumerator` over a testable `INetworkInterfaceSource` seam; eligibility = Up + non-loopback + multicast + IPv4; stable ordering; default = first | AC-2.2.1, 2, 4 |
| **Decision 7 — Cancellation hierarchy** | `_appCts` in `App` (app level) → `AdapterScope._adapterCts = linked(appToken)` (adapter level); `AdapterToken` exposed; budgeted teardown | AC-2.2.3, 8 |
| **FR-004 — Startup discovery** | `AdapterScope` issues the initial `SendMSearchAsync(5 s)` after binding the transport | AC-2.2.5 |
| **FR-050 — Atomic adapter switch (shape only)** | `CurrentAdapterIPv4` / `AdapterToken` / 2 s-budgeted `DisposeAsync` scaffold so Story 5.2 plugs the full sequence in | AC-2.2.8 |
| **NFR-R5 — Zero-adapter host still runs** | No crash, no dialog, single `Warning`, transport not started, app interactive | AC-2.2.6 |
| **Pattern 7 — DI lifetime** | Enumerator + source registered singleton; `AdapterScope` constructed by orchestrator, NOT a DI singleton | AC-2.2.7 |
| **Pattern 11 / D8 — DiagCategories usage** | Reuses pre-existing `Adapter.Switch` / `Adapter.Switch.Timeout` constants; no context fields required | AC-2.2.6, 8 |
| **Amendment A2 — AC trait shape** | Every test carries `[Trait("ac", "AC-2.2.<n>")]` | AC-2.2.9 |

### What this story does NOT do (scope discipline)

- **Does NOT implement the FR-050 atomic-switch *sequence*.** Only the *shape* (`CurrentAdapterIPv4`, `AdapterToken`, budgeted `DisposeAsync`). The full Decision 7 sequence (steps 3–6: callback-host teardown, per-device CTS cancel, registry clear) lands in **Story 5.2** — and references types (`EventCallbackHost`, `RegistryEntry`, `DeviceRegistry`) that **do not exist yet**. Do not invent them.
- **Does NOT implement live adapter-change detection.** Story 2.1's dev-notes line 530 loosely said "surfaced via `NetworkChange.NetworkAddressChanged` in Story 2.2" — that was an over-reach. The epic's Story 2.2 ACs contain **no** hot-plug requirement, and the architecture does not mention `NetworkChange`. Hot-plug/auto-reselect is a potential **E5 enhancement**, explicitly **out of scope** here. Do NOT subscribe to `NetworkChange.*`.
- **Does NOT consume `IncomingDatagrams`.** The transport is started and M-SEARCH'd, but nothing reads the channel until `DiscoveryService` (Story 2.4). Datagrams accumulate in `DropOldest(4096)` and are discarded — expected and harmless.
- **Does NOT create `ShellViewModel`.** The AC permits "ShellViewModel **or equivalent app-startup orchestrator**." ShellViewModel (with CommunityToolkit.Mvvm `[ObservableProperty]`, `DeviceTreeViewModel`, etc.) is **Story 2.5** and pulls in dependencies not yet present. Story 2.2 uses `App.OnLaunched` as the interim orchestrator; **Story 2.5 relocates** the `AdapterScope` construction into `ShellViewModel` (epics.md line 1007). The `_appCts`-in-`App` home is permanent (Decision 7), only the scope *construction site* moves.
- **Does NOT add new `DiagCategories` constants.** `Adapter.Switch` (`"Adapter.Switch"`) and `Adapter.Switch.Timeout` (`"Adapter.Switch.Timeout"`) already exist in `src/ohSpy.Core/Diagnostics/DiagCategories.cs` (pre-added for Story 5.2). Reuse them.
- **Does NOT add new packages.** `System.Net.NetworkInformation` (NICs) and `System.Net.Sockets` (`AddressFamily`) are in the `net10.0` shared framework. No `Directory.Packages.props` change.
- **Does NOT add `InternalsVisibleTo`.** `ohSpy.Core.csproj` already grants `ohSpy.Core.Tests` + `ohSpy.App` (Story 1.3). `internal sealed` types are visible to both.
- **Does NOT change the Story 2.1 `ISsdpTransport` singleton registration** (see "The single-scope / no-switch model" below).

### The single-scope / no-switch model (important design note)

The architecture says a fresh transport is **constructed per adapter** on switch (architecture line 249; Decision 7 step 8). That implies a *factory*, not a shared singleton. But **Story 2.2 has exactly one `AdapterScope`** (startup) and performs **no switch** — the switch is Story 5.2. So:

- For 2.2, `AdapterScope` is **constructor-injected with the DI-singleton `ISsdpTransport`** (resolved in `App.OnLaunched`). One scope, one transport, started once, disposed once at app exit. Correct and minimal.
- **Forward tension (flag, do not solve here):** when Story 5.2 implements the real switch, a disposed singleton transport cannot be reused. At that point `ISsdpTransport` must become a per-scope **`Func<ISsdpTransport>` factory** (transient construction). Note also that Story 2.4's `DiscoveryService` must read the **same** transport instance the scope started — so the factory wiring in 5.2 will need the scope to *own and expose* its transport/reader to `DiscoveryService`. **Raise this as amendment candidate A23** (see below) rather than pre-building the factory now (YAGNI — over-engineering risks rework if 2.3/2.4 reshape the ownership).

### Previous-story intelligence — what to reuse and what to copy from

**Story 2.1 (`SsdpTransport`) — your direct upstream:**
- `ISsdpTransport.StartAsync(IPAddress, CancellationToken)` + `SendMSearchAsync(TimeSpan, CancellationToken)` + `IncomingDatagrams` + `IAsyncDisposable` — the exact surface `AdapterScope` drives. `StartAsync` guards against double-start (throws `InvalidOperationException`); call it once per scope.
- `SsdpTransport.DisposeAsync` is **idempotent** (AC-2.1.7) and tolerates being called when never started (null sockets) — but `AdapterScope` still guards with `_transportStarted` for clarity.
- **A22 (just applied):** SSDP integration tests must deliver via multicast, not unicast, on Windows. **Not directly relevant** to 2.2 (no socket-receive tests here — `AdapterScope` tests use a `FakeSsdpTransport`), but know it exists if you add any real-socket test.
- The `internal sealed class X(IDiagnosticEmitter diag)` primary-ctor pattern and `await using var` per-test instantiation are the established idioms — follow them.

**Story 1.5 (`Diagnostics`):**
- `IDiagnosticEmitter.Warning(category, message, context = default)` — call with `default` context for `Adapter.Switch` (Pattern 11: no fields required).
- `CapturingDiagnosticEmitter` at `tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs` — canonical test emitter. Its `Entries` list records `(Severity, Category, Message, Context)`. Don't create a new one.

**Story 1.6 (`FakeUpnpDevice`, NetArchTest):**
- **Per-test fixture, not `IClassFixture`** — construct fakes inline per test.
- **NetArchTest `CoreAppBoundaryTests` is LIVE** — it fails the build if any new Core file imports `Microsoft.UI.*`, `Microsoft.Windows.*`, `WinRT.Interop.*`, or `ohSpy.App.*`. All Story 2.2 Core types stay BCL-only. (The `App.xaml.cs` wiring is the only App-side touch, and it lives in App — allowed.)
- **Chaos-hook filter (A18):** never tag a fast test `[Trait("category", "chaos")]`. The dev-machine integration test (7.7) is `integration`, not `chaos`.

**Story 1.3 (`UpnpHttpClient`):**
- `InternalsVisibleTo("ohSpy.Core.Tests")` + `InternalsVisibleTo("ohSpy.App")` already on `ohSpy.Core.csproj`. No edit needed.

### Epic 1 retro carry-forwards (`epic-1-retro-2026-06-02.md`)

- **"Compile the spec-skeleton first (action A)."** Story 2.1 caught **5 analyzer errors** in as-written skeletons before any test ran. Expect the same class here — compile Tasks 2/3/4/6 skeletons and fix analyzer nits (the fire-and-forget `_ = StartAsync()` will likely draw `VSTHRD110`/`CA2012`-style scrutiny; the `Task.WhenAny` budget pattern may draw `VSTHRD003`). Fix at source.
- **"Trivially passing is a red flag (action B)."** The dev-machine enumerator integration test (7.7) returning 0 eligible adapters on a clearly-networked box is NOT a pass — diagnose first.
- **FluentAssertions is 7.2.0 (MIT).** Use freely.

### Code-style + pattern compliance (citable rulebook)

- **Pattern 1 (naming):** file-scoped namespace, one type per file, `_camelCase` private fields, `Async` suffix, `I`-prefix interfaces.
- **Pattern 2 (Core ↔ App):** all new types except the `App.xaml.cs`/`ServiceRegistration.cs` edits live in `ohSpy.Core/Discovery` + `ohSpy.Core/Models`. Backstopped by `CoreAppBoundaryTests`.
- **Pattern 6 (async):** `ConfigureAwait(false)` on every Core `await`; `CancellationToken` threaded; no `.Result`/`.Wait()`; `async void` ONLY on the WinUI `Window.Closed` handler.
- **Pattern 7 (DI):** singletons for the two query services; `AdapterScope` constructed by parent (orchestrator), not DI.
- **Pattern 8 (constructors):** primary ctor for the enumerator/source (straight DI); traditional ctor for `AdapterScope` (builds the linked CTS).
- **Pattern 9 (records vs classes):** `NetworkAdapter` + `AdapterCandidate` are `public sealed record`; `NetworkAdapterEnumerator` / `LiveNetworkInterfaceSource` / `AdapterScope` are `internal sealed class`.
- **Pattern 10 (exceptions):** narrowest catch; the one teardown-race bare catch in `DisposeAsync` is a documented precedent.
- **Pattern 11 (`DiagnosticContext`):** `Adapter.Switch.*` requires no context fields — pass `default`.
- **Pattern 12 (message grammar):** sentence case, terse, ASCII, no trailing punctuation — `"no eligible adapters at startup"`, `"adapter teardown exceeded budget"`.
- **Pattern 14 + 15 (test naming + traceability):** `MethodUnderTest_Scenario_Expected_AC22n`; `[Trait("ac", "AC-2.2.<n>")]` always; `[Trait("category", "integration")]` on the dev-machine test only.

### Anti-patterns to avoid

- **Don't try to mock `NetworkInterface`.** It's sealed with no public ctor — that's the entire reason for the `INetworkInterfaceSource` / `AdapterCandidate` seam. Filter on the projection, not on live NICs.
- **Don't `OrderBy` / re-sort the candidates.** FR-048 "first eligible" requires the OS enumeration order to flow through untouched.
- **Don't register `AdapterScope` in DI.** Its lifetime is adapter-scoped, not process-scoped (AC-2.2.7 + Pattern 7). DI-singleton-izing it would break the Story 5.2 switch.
- **Don't let `StartAsync` throw on zero adapters.** NFR-R5 — the whole point is graceful degradation. Emit the Warning and return.
- **Don't block the UI thread.** `OnLaunched` is `void`; the scope startup is fire-and-forget. No `.Result`/`.Wait()`/`.GetAwaiter().GetResult()`.
- **Don't subscribe to `NetworkChange.NetworkAddressChanged`.** Out of scope (see scope-discipline note).
- **Don't build the `Func<ISsdpTransport>` factory now.** One scope, no switch — YAGNI. Flag A23 instead.
- **Don't dispose the DI-singleton transport twice.** `AdapterScope.DisposeAsync` disposes it once at app exit; nothing else should. (It's idempotent anyway, but keep ownership clean.)
- **Don't add a kind glyph / tree row / menu here.** Display of adapters (`View → Network adapter`) is Story 5.2; the tree is Story 2.5. This story produces the *data + binding scaffold* only.
- **Don't emit a diagnostic per enumerated adapter.** The enumerator is silent; only the zero-adapter Warning is emitted (by the scope).

### Forward-looking dependencies — what later stories need from us

| Story | What it consumes from 2.2 |
|---|---|
| 2.4 (`DiscoveryService`) | Indirect: the transport `AdapterScope` started — its `IncomingDatagrams` reader. 2.4 must read the **same** instance the scope started (informs the A23 factory decision). |
| 2.5 (Main Window Shell + `ShellViewModel`) | Relocates `AdapterScope` construction into `ShellViewModel` (epics.md line 1007); consumes `CurrentAdapterIPv4` for any status display. |
| 5.2 (Atomic adapter switch UI) | `NetworkAdapterEnumerator.Enumerate()` populates the `View → Network adapter` `RadioMenuFlyoutItem` list (epics.md line 1842); `AdapterScope.DisposeAsync` is step 2 of the FR-050 sequence; the full switch needs the A23 transport factory. |

### Architecture amendments to anticipate

Stories with amendments so far: 1.1→A6/A7/A8, 1.3→A9/A10/A11, 1.5→A14, 1.6→A16/A18, 2.1→A22. **Candidates to flag in Completion Notes if encountered:**

- **A23 (likely, not speculative):** `ISsdpTransport` must become a per-scope `Func<ISsdpTransport>` factory (transient) when Story 5.2 implements the FR-050 switch — a disposed singleton cannot be rebound to a new adapter, and `DiscoveryService` (2.4) must share the scope-owned instance. Story 2.2 keeps the 2.1 singleton (single scope, no switch). Recommend the architect record the factory migration as a Story 5.2 prerequisite (and reconcile with 2.4's `DiscoveryService` ownership).
- **A24 (speculative):** If the "first IPv4 unicast address" selection proves wrong for multi-homed adapters (e.g. an APIPA `169.254.*` address sorts first ahead of a routable one), document a preference rule (skip link-local `169.254/16` when a routable IPv4 exists) and amend FR-048's eligibility prose. Only raise if a real dev-machine adapter exhibits this.
- **A25 (speculative):** If `OnLaunched`'s fire-and-forget `StartAsync` proves to swallow a diagnostically-useful failure (something other than the zero-adapter case), document the local `try/catch` + `Warning` wrapper as the recommended pattern and pin it.

These are *candidates*, not promises. Clean implementation ⇒ no amendment.

### Project Structure Notes

**Files this story creates (7 source + ~4 test):**

```
src/ohSpy.Core/
├── Models/
│   ├── NetworkAdapter.cs                        ← Task 1.1 NEW (public record)
│   └── AdapterCandidate.cs                      ← Task 1.2 NEW (public record)
└── Discovery/
    ├── INetworkInterfaceSource.cs               ← Task 2.1 NEW (public iface)
    ├── LiveNetworkInterfaceSource.cs            ← Task 2.2 NEW (internal sealed)
    ├── INetworkAdapterEnumerator.cs             ← Task 3.1 NEW (public iface)
    ├── NetworkAdapterEnumerator.cs              ← Task 3.2 NEW (internal sealed)
    └── AdapterScope.cs                          ← Task 4   NEW (internal sealed)

tests/ohSpy.Core.Tests/
├── Discovery/
│   ├── NetworkAdapterEnumeratorTests.cs         ← Task 7 NEW
│   └── AdapterScopeTests.cs                     ← Task 8 NEW
└── Fakes/
    ├── StubNetworkInterfaceSource.cs            ← Task 7.1 NEW
    └── FakeSsdpTransport.cs                     ← Task 8.1 NEW
```

**Files this story modifies (2):**

- `src/ohSpy.App/Composition/ServiceRegistration.cs` — two `AddSingleton` lines (Task 5).
- `src/ohSpy.App/App.xaml.cs` — `_appCts` field, `_adapterScope` field, `OnLaunched` wiring, `OnWindowClosed` handler, usings (Task 6).

**Files this story does NOT modify:**

- `src/ohSpy.Core/Discovery/SsdpTransport.cs` / `ISsdpTransport.cs` — consumed as-is.
- `src/ohSpy.Core/Diagnostics/DiagCategories.cs` — `Adapter.Switch` + `Adapter.Switch.Timeout` already exist.
- `Directory.Packages.props` / `Directory.Build.props` — no new pins.
- `src/ohSpy.Core/ohSpy.Core.csproj` — `InternalsVisibleTo` already granted.
- `MainWindow.xaml` / `MainPage.xaml` and any ViewModel — Story 2.5 territory.

### Testing standards summary

- xUnit + FluentAssertions 7.2.0 (MIT).
- **Each AC-traceable test carries `[Trait("ac", "AC-2.2.<n>")]`** (Amendment A2).
- **Only the dev-machine enumerator test is `[Trait("category", "integration")]`** — everything else is fast/pure unit. Chaos suite stays at **1**.
- **Per-test fakes**, no `IClassFixture`. `CapturingDiagnosticEmitter` for emission assertions; new `StubNetworkInterfaceSource` + `FakeSsdpTransport` for the seams.
- Test names follow Pattern 14: `MethodUnderTest_Scenario_Expected_AC22n`.
- **`dotnet test` total target ~156** (142 baseline + ~14). **`category=chaos` target: 1** (unchanged).

### References

> Authoritative paths (for grep / cross-reference):
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (~3016 lines, post A6–A22)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (Story 2.2 lines 806–846)
> - Previous story: `_bmad-output/implementation-artifacts/2-1-ssdp-transport-multicast-search-sockets-with-bounded-channel.md`
> - Epic 1 retrospective: `_bmad-output/implementation-artifacts/epic-1-retro-2026-06-02.md`

- [Source: epics.md#Story-2.2] — verbatim ACs (lines 806–846).
- [Source: epics.md#Story-2.5] — `AdapterScope` relocates into `ShellViewModel` (line 1007); FR-054 tree.
- [Source: epics.md#Story-5.2] — `NetworkAdapterEnumerator.Enumerate()` populates the adapter radio menu (line 1842).
- [Source: architecture.md#Decision-7] — cancellation hierarchy; `_appCts` (App) → `_adapterCts = linked(appToken)` (lines 734–878); CTS tree (lines 748–773); adapter-switch atomic sequence + 2 s budget (lines 818–831).
- [Source: architecture.md#Decision-2] — adapter-specific bind aligns with FR-048; transport reconstructed per adapter on switch (line 249, 258).
- [Source: architecture.md#Pattern-7] — DI composition root + singleton default + per-entity-constructed-by-parent (lines 1819–1843).
- [Source: architecture.md#Pattern-2] — Core ↔ App boundary (NetArchTest-backstopped).
- [Source: architecture.md#Pattern-6] — async discipline; `async void` only on event handlers.
- [Source: architecture.md#Pattern-11] — `DiagnosticContext` per-category fields; `Adapter.Switch.*` = none beyond message.
- [Source: architecture.md#Amendment-A2] — AC trait shape.
- [Source: architecture.md#Amendment-A18] — chaos-hook filter `category=chaos` (don't tag fast tests chaos).
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs:18-71] — existing registrations + ordering/style; Story 2.1 SSDP line.
- [Source: src/ohSpy.App/App.xaml.cs:37-73] — DI build in ctor; `OnLaunched` UI-thread pin + post-construction wiring (the wiring template for Task 6).
- [Source: src/ohSpy.Core/Discovery/ISsdpTransport.cs] — transport surface `AdapterScope` drives.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs] — `Adapter.Switch` + `Adapter.Switch.Timeout` constants (pre-added).
- [Source: src/ohSpy.Core/Diagnostics/IDiagnosticEmitter.cs] — `Warning(category, message, context = default)`.
- [Source: src/ohSpy.Core/Diagnostics/DiagnosticContext.cs] — context fields (all pass `default` for adapter category).
- [Source: src/ohSpy.Core/ohSpy.Core.csproj] — `InternalsVisibleTo` Tests + App (no edit needed).
- [Source: tests/ohSpy.Core.Tests/Fakes/CapturingDiagnosticEmitter.cs] — canonical test emitter.
- [Source: tests/ohSpy.Core.Tests/Discovery/SsdpTransportTests.cs] — Discovery test layout + trait conventions.
- [Source: 2-1-…md#Anti-Patterns] — line 530: the `NetworkChange` reference now corrected to out-of-scope here.
- [Source: 2-1-…md#Forward-looking-dependencies] — what 2.2 consumes from 2.1 (line 541).
- [Source: epic-1-retro-2026-06-02.md] — action A (compile-the-skeleton) + action B (trivially-passing red flag).
- [Source: project_ohspy memory] — native Windows desktop UPnP inspector; raw-BCL UPnP; no CI (pre-commit chaos hook is the regression net).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Claude Opus 4.8, 1M context) — bmad-dev-story workflow.

### Debug Log References

**Spec-skeleton compile check (epic-1 retro action A) — caught 4 analyzer errors in the
as-written skeletons before/at first test build, none logic bugs (exactly the failure class
the retro flagged). All under `TreatWarningsAsErrors=true`:**

1. **CA1068** — the `AdapterScope` test-seam ctor put `TimeSpan switchBudget` AFTER
   `CancellationToken appToken`. `CancellationToken` must be last. Reordered to
   `(…, TimeSpan switchBudget, CancellationToken appToken)`; named args at the call site
   were already order-independent.
2. **CA1859** — `AdapterScopeTests.EnumeratorWith` returned the `INetworkAdapterEnumerator`
   interface; analyzer prefers the concrete `NetworkAdapterEnumerator` for perf. Changed to
   the concrete return type (still assignable to the `Scope()` interface parameter).
3. **VSTHRD100** — the `Window.Closed` handler was written `async void` (the story's
   "sanctioned WinUI event-handler" intent). VSTHRD100 is exempt only in `tests/**`
   (`.editorconfig` A11), NOT App production. Refactored to a **sync** `void OnWindowClosed`
   that fire-and-forgets `_ = ShutdownAsync()` (mirrors the existing `_ = StartAsync()`
   pattern) — removes `async void` while keeping awaited teardown.
4. **VSTHRD103** — `_appCts.Cancel()` synchronously blocks inside the async `ShutdownAsync`.
   Switched to `await _appCts.CancelAsync()` (same fix Story 2.1 applied in its teardown).

**One design decision surfaced by the analyzer (CA1001):** `App` now owns two app-lifetime
disposables (`_appCts`, `_adapterScope`) per Decision 7, so CA1001 wants `App : IDisposable`.
WinUI's `Application` base has no `IDisposable` contract the framework invokes, and
`_adapterScope` is `IAsyncDisposable` (a synchronous `Dispose` would violate Pattern 6's
no-blocking-on-async rule). Resolved with a narrowly-justified type-level
`[SuppressMessage(CA1001)]` + comment; deterministic teardown is in `OnWindowClosed →
ShutdownAsync`. Flagged as **A26 candidate** below.

**One implementation refinement during REFACTOR:**

- The story skeleton (Task 4.3) used `Task.WhenAny(teardown, Task.Delay(Infinite, budget.Token))`
  for the FR-050 budget. That leaves a dangling `Task.Delay` when teardown wins, risking an
  unobserved cancellation. Replaced with `teardown.WaitAsync(_switchBudget)` (net6+) — caps the
  wait cleanly with no dangling timer; `TimeoutException` drives the `Adapter.Switch.Timeout`
  Warning. The transport's own swallowing `DisposeAsync` continues harmlessly if the budget fires.
- `LiveNetworkInterfaceSource` projects **all** unicast addresses (IPv4 + IPv6) rather than
  pre-filtering IPv4 (as the skeleton showed), so `NetworkAdapterEnumerator` stays the single
  eligibility authority — the IPv4 selection / "IPv6-only filtered" path is then exercised in
  production, not just in stub tests.

### Completion Notes List

**All 9 ACs satisfied; 19 new tests (144 → 163 passing, +2 pre-existing skips = 165 total).
Build 0 warnings / 0 errors under `TreatWarningsAsErrors=true`; chaos suite unchanged at 1.**

- **Testability seam works as designed (AC-2.2.2).** `NetworkInterface` is sealed/unmockable;
  the `INetworkInterfaceSource` → `AdapterCandidate` projection lets all eligibility filtering
  (down / loopback / non-multicast / IPv6-only / multi-IPv4-pick-first / order-preservation)
  be unit-tested with a `StubNetworkInterfaceSource`. The live BCL call sits only in
  `LiveNetworkInterfaceSource`, covered by the single dev-machine integration test.
- **Dev-machine integration test passed (AC-2.2.9, epic-1 retro action B).** `LiveNetworkInterfaceSource`
  + `NetworkAdapterEnumerator` enumerated ≥ 1 eligible adapter on this box — not a trivial 0.
  Tagged `[Trait("category","integration")]`, NOT `chaos`.
- **Zero-adapter degradation (AC-2.2.6).** `AdapterScope.StartAsync` emits exactly one
  `Warning(Adapter.Switch, "no eligible adapters at startup")`, never starts the transport,
  leaves `CurrentAdapterIPv4` null, and never throws — the host still runs (NFR-R5).
- **D7 token linkage verified (AC-2.2.3).** Cancelling the supplied `appToken` flips
  `scope.AdapterToken.IsCancellationRequested`, proving `_adapterCts =
  CreateLinkedTokenSource(appToken)`. The permanent `_appCts` home is `App` (Decision 7).
- **FR-050 budget shape (AC-2.2.8).** `DisposeAsync` cancels the scope, tears the transport down
  within a 2 s budget (test-seam ctor injects a short budget so the timeout-Warning path is
  tested in ~50 ms, no real 2 s wait), is idempotent, and skips transport disposal when never
  started. The **full** atomic-switch sequence (callback host, registry clear) remains Story 5.2.
- **Scope discipline held.** No `ShellViewModel` created (interim orchestrator is `App.OnLaunched`;
  Story 2.5 relocates the scope). No `NetworkChange.*` subscription. No new packages, no new
  `DiagCategories` (`Adapter.Switch` / `Adapter.Switch.Timeout` pre-existed), no
  `InternalsVisibleTo` edits. `IncomingDatagrams` is intentionally left unconsumed until
  `DiscoveryService` (Story 2.4).
- **A23 confirmed (the deferred transport factory).** The 2.1 `ISsdpTransport` singleton is kept
  as-is. When Story 5.2 implements the real switch, the transport must become a per-scope
  `Func<ISsdpTransport>` (a disposed singleton can't be rebound), and `DiscoveryService` (2.4)
  must share the scope-owned instance. No change made here — single scope, no switch.

**Amendment candidates raised for architect review:**

- **A26 (new, confirmed — not speculative):** *App-level disposable ownership pattern.* `App`
  owning `_appCts` (+ async-disposable `_adapterScope`) trips CA1001, but WinUI `Application`
  exposes no `IDisposable` contract and the scope is `IAsyncDisposable` (sync `Dispose` would
  violate Pattern 6). The accepted pattern is a justified `[SuppressMessage(CA1001)]` with
  teardown in the `Window.Closed` handler (sync void → fire-and-forget `ShutdownAsync`).
  Recommend recording this as the canonical App-lifetime-disposable convention so Stories 2.5 /
  5.2 (which add more app-lifetime state to the orchestrator) follow it consistently.
- **A23 (restated from story Dev Notes, now a hard prerequisite for 5.2):** `ISsdpTransport` must
  migrate from DI singleton to a per-`AdapterScope` `Func<ISsdpTransport>` factory when the
  FR-050 switch is implemented, reconciled with Story 2.4's `DiscoveryService` transport ownership.
- **A24 (speculative — not observed):** multi-homed "first IPv4" selection could pick an APIPA
  `169.254/16` address ahead of a routable one. Did not occur on the dev box; no link-local
  skip rule added. Revisit only if a real adapter exhibits it.

### File List

**New (9 source + 4 test):**

- `src/ohSpy.Core/Models/NetworkAdapter.cs`
- `src/ohSpy.Core/Models/AdapterCandidate.cs`
- `src/ohSpy.Core/Discovery/INetworkInterfaceSource.cs`
- `src/ohSpy.Core/Discovery/LiveNetworkInterfaceSource.cs`
- `src/ohSpy.Core/Discovery/INetworkAdapterEnumerator.cs`
- `src/ohSpy.Core/Discovery/NetworkAdapterEnumerator.cs`
- `src/ohSpy.Core/Discovery/AdapterScope.cs`
- `tests/ohSpy.Core.Tests/Fakes/StubNetworkInterfaceSource.cs`
- `tests/ohSpy.Core.Tests/Fakes/FakeSsdpTransport.cs`
- `tests/ohSpy.Core.Tests/Discovery/NetworkAdapterEnumeratorTests.cs`
- `tests/ohSpy.Core.Tests/Discovery/AdapterScopeTests.cs`

**Modified (3):**

- `src/ohSpy.App/Composition/ServiceRegistration.cs` — added `INetworkInterfaceSource` +
  `INetworkAdapterEnumerator` singleton registrations (Story 2.2 block).
- `src/ohSpy.App/App.xaml.cs` — `_appCts` + `_adapterScope` fields, `OnLaunched` adapter-scope
  construction + fire-and-forget `StartAdapterScopeAsync`, `OnWindowClosed` → `ShutdownAsync`
  teardown (D7 ordering fix), `using ohSpy.Core.Discovery`, type-level `[SuppressMessage(CA1001)]`.
- `tests/ohSpy.Core.Tests/Fakes/FakeSsdpTransport.cs` — added `TeardownCts` to cancel lingering delay post-assertion.
- `tests/ohSpy.Core.Tests/Discovery/AdapterScopeTests.cs` — cancel `TeardownCts` after budget-exceeded assertion.
- `_bmad-output/implementation-artifacts/2-2-network-adapter-enumerator-adapter-scope-startup-bind.md`
  — task checkboxes, Dev Agent Record, review section, Status (ready-for-dev → done).

## Senior Developer Review (AI)

**Review Date:** 2026-06-02
**Reviewer Model:** Claude Sonnet 4.6 (bmad-code-review, 7-angle parallel)
**Outcome:** Changes Requested → APPROVED-WITH-MINOR-FIXES

### Action Items

- [x] [Review][Patch] `StartAsync` exceptions from transport bind silently dropped as unobserved task [`src/ohSpy.App/App.xaml.cs` — fire-and-forget site] — fixed via `StartAdapterScopeAsync` wrapper with `catch (Exception) when (not OOM)` + `Warning(AdapterSwitch, …, ErrorText)`
- [x] [Review][Patch] Wrong D7 cancellation ordering in `ShutdownAsync` — `_appCts` cancelled after scope disposed [`src/ohSpy.App/App.xaml.cs:ShutdownAsync`] — fixed: `await _appCts.CancelAsync()` now precedes `await _adapterScope.DisposeAsync()`
- [x] [Review][Patch] `_transportStarted` lacks `volatile` — memory visibility relies on implicit CTS barrier [`src/ohSpy.Core/Discovery/AdapterScope.cs:31`] — fixed: `private volatile bool _transportStarted`
- [x] [Review][Patch] `CurrentAdapterIPv4` set before `_transport.StartAsync` completes — non-null implies live transport invariant broken [`src/ohSpy.Core/Discovery/AdapterScope.cs:84`] — fixed: assignment moved to after `await _transport.StartAsync(…)` succeeds
- [x] [Review][Patch] Timeout test leaks a 450 ms `Task.Delay` background continuation after test returns [`tests/ohSpy.Core.Tests/Discovery/AdapterScopeTests.cs:150`] — fixed: `FakeSsdpTransport.TeardownCts` added; test cancels it after assertion
- [x] [Review][Defer] Pre-cancelled `appToken` causes half-initialised transport (sockets bound, loops never run) [`src/ohSpy.Core/Discovery/AdapterScope.cs:65`] — deferred: no realistic caller path today; note in Completion Notes

## Change Log

| Date | Change |
|---|---|
| 2026-06-02 | Implemented Story 2.2: `NetworkAdapter` + `AdapterCandidate` models, `INetworkInterfaceSource`/`LiveNetworkInterfaceSource` testability seam, `INetworkAdapterEnumerator`/`NetworkAdapterEnumerator` (FR-048 eligibility filter), `AdapterScope` (D7 adapter-level CTS, FR-048 first-eligible select, FR-004 startup M-SEARCH, NFR-R5 zero-adapter degrade, FR-050-budgeted idempotent `DisposeAsync`), DI registrations, App.OnLaunched startup bind + clean shutdown. 19 new tests (144→163 passing, +2 skips). Build 0 warnings; chaos suite unchanged at 1. Amendment **A26** raised (App-level disposable ownership); **A23** restated as a 5.2 prerequisite. Status → review. |
| 2026-06-02 | Applied code-review patches (Sonnet 4.6, APPROVED-WITH-MINOR-FIXES): `StartAdapterScopeAsync` wrapper catches transport bind exceptions; D7 shutdown ordering fixed (`_appCts` cancelled before scope disposal); `volatile bool _transportStarted`; `CurrentAdapterIPv4` moved post-bind; `FakeSsdpTransport.TeardownCts` added to cancel lingering delay in timeout test. Build 0 warnings; 163 passing + 2 skipped; chaos suite unchanged at 1. Status → done. |
