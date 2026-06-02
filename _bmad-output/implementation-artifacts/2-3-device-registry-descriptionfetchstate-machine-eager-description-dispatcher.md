---
baseline_commit: ca16daa97f86cdf0cdc4e52b2a5442f1ccd800fb
---

# Story 2.3: Device Registry + DescriptionFetchState Machine + Eager Description Dispatcher

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want a UUID-keyed device registry whose entries progress through a strict `Pending → InFlight → Loaded/Failed` state machine driven by a bounded-parallelism eager-fetch dispatcher,
so that devices appear in the visible tree only after their description is parsed (no transient placeholders), slow devices can't choke the fetch pipeline, and the registry's event surface gives the tree exactly the rows it needs — no filtering, no race conditions.

## Acceptance Criteria

**Verbatim ACs derived from epics.md §Story 2.3 (lines 849–924). AC trait IDs follow Amendment A2; the architecture numbers these AC-9.1..AC-9.7 (Decision 9) plus AC-7.2 (Decision 7) — use `[Trait("ac", "AC-9.<n>")]` / `[Trait("ac", "AC-7.2")]` to match the architecture's own numbering.**

**AC-9.0 — `DescriptionFetchState` enum (D9)**

**Given** `ohSpy.Core/Devices/DescriptionFetchState.cs`
**When** I inspect the enum
**Then** it has exactly the four values `Pending, InFlight, Loaded, Failed` (D9)

**AC-9.1 — `RegistryEntry` shape + legal state transitions (D9)**

**Given** `ohSpy.Core/Devices/RegistryEntry.cs`
**When** I inspect the type
**Then** it carries the D9 shape: `Uuid` (Guid), `LocationUrl` (Uri), `State` (default `Pending`), `Description?` (DeviceDescription), `FailureReason?` (string), `FirstSeenUtc`, `LastSeenUtc`, `AliveCount`, `Server?`, `CacheControlMaxAge?` (TimeSpan?), `BootId?`, `ConfigId?`, an `internal CancellationTokenSource DeviceCts`, and `public CancellationToken DeviceToken => DeviceCts.Token`
**And** `MarkInFlight()`, `MarkLoaded(DeviceDescription)`, `MarkFailed(string)`, `RefreshSsdpMetadata(...)` are all `internal` (only Core + tests call them)
**And** the legal transitions `Pending→InFlight` (`MarkInFlight`), `Pending→Failed` (`MarkFailed`), `InFlight→Loaded` (`MarkLoaded`), `InFlight→Failed` (`MarkFailed`) all succeed
**And** every other transition (`Loaded→*`, `Failed→*`, `Pending→Loaded` directly, double `MarkInFlight`, etc.) throws `InvalidOperationException`
**And** `Loaded` and `Failed` are terminal for the entry's lifetime

**AC-9.2 — `Description` non-null iff `Loaded` (D9)**

**Given** a `RegistryEntry`
**When** its `State` is `Loaded`
**Then** `Description` is non-null; in every other state `Description` is null
**And** `FailureReason` is non-null iff `State == Failed`

**AC-9.3 — `IDeviceRegistry` surface + no `DeviceAdded` (D9)**

**Given** `ohSpy.Core/Devices/IDeviceRegistry.cs` + `DeviceRegistry.cs`
**When** I inspect the interface
**Then** it exposes `bool TryGetEntry(Guid, out RegistryEntry)`, `IReadOnlyCollection<RegistryEntry> Loaded` (snapshot of `State==Loaded` entries only), `int Count` (total, all states), and three events `event Action<RegistryEntry> DeviceLoaded`, `event Action<RegistryEntry> DeviceUpdated`, `event Action<Guid> DeviceRemoved`
**And** there is NO `DeviceAdded` event — `DeviceLoaded` is raised exactly when `MarkLoaded` runs, so VMs never see an entry before it is `Loaded`
**And** `DeviceUpdated` is raised when an already-`Loaded` entry's display-affecting field changes (FR-054 trigger — shaped now; no 2.3 production path triggers it, see Dev Notes)

**AC-9.x-dispatcher — `EagerDescriptionDispatcher` shape (D9 / NFR-P6 / FR-043)**

**Given** `ohSpy.Core/Devices/EagerDescriptionDispatcher.cs`
**When** I inspect the impl
**Then** it holds a `SemaphoreSlim(8, 8)` concurrency cap (NFR-P6 + FR-043 — 8 concurrent fetches)
**And** it injects `IUpnpHttpClient`, `IDeviceDescriptionParser`, `IUiDispatcher`, the concrete `DeviceRegistry` (for internal `Remove`/`RaiseDeviceLoaded` + the fetch-trigger subscription), and `IDiagnosticEmitter`
**And** `FetchAsync(RegistryEntry entry)` implements the canonical D9 flow verbatim (see AC-9.x-flow)

**AC-9.x-flow — canonical happy-path fetch (D9 / FR-005)**

**Given** the canonical fetch flow
**When** `FetchAsync(entry)` runs against a happy device
**Then** the sequence is: `await _semaphore.WaitAsync(entry.DeviceToken)` → `_dispatcher.Post(() => entry.MarkInFlight())` → `await _http.FetchDeviceDescriptionAsync(entry.LocationUrl, entry.DeviceToken)` → `_descParser.Parse(bytes)` → (UDN-match check) → `_dispatcher.Post(() => { entry.MarkLoaded(description); _registry.RaiseDeviceLoaded(entry); })`
**And** the semaphore is released in a `finally` block

**AC-9.6 — mismatched-root backstop (FR-043)**

**When** the parsed description's UDN does NOT equal `entry.Uuid` (after normalising the `uuid:` prefix — see Dev Notes; the model field is `DeviceDescription.Udn`, a string, NOT `RootUdn`)
**Then** an `Information` `DiagCategories.DescriptionFetchMismatch` diagnostic is emitted with `DeviceUuid = entry.Uuid`, `Url = entry.LocationUrl.ToString()`, `ErrorText = $"declared root: {description.Udn}"`
**And** the entry is removed via `_dispatcher.Post(() => _registry.Remove(entry.Uuid))`
**And** NO `MarkLoaded` is called

**AC-9.7 — cancellation during fetch (byebye / adapter switch)**

**When** the fetch is cancelled via `entry.DeviceToken`
**Then** `OperationCanceledException` is caught silently (guarded by `when (entry.DeviceToken.IsCancellationRequested)`) — no state transition
**And** NO diagnostic is emitted (the cancel is caller-initiated; the registry remove path handles the rest)

**AC-9.x-fail — other-exception path (FR-047)**

**When** any other exception is raised (HTTP error, parse failure, etc.)
**Then** a `Warning` `DiagCategories.DescriptionFetch` diagnostic is emitted with `DeviceUuid`, `Url`, `ErrorText = ex.Message`
**And** `_dispatcher.Post(() => entry.MarkFailed(ex.Message))` runs (FR-047: failed entries STAY in the registry but do NOT appear in the tree — they're not in `Loaded`, and `DeviceLoaded` never fires for them)

**AC-7.2 — device-level CTS linkage + per-device byebye (D7)**

**Given** the device-level CTS hierarchy
**When** the registry creates an entry
**Then** `entry.DeviceCts = CancellationTokenSource.CreateLinkedTokenSource(adapterToken)` (D7 device level — the adapterToken is passed into the create/alive call; see Dev Notes for the singleton-registry / per-adapter-token wiring)
**And** removing an entry (byebye) cancels its `DeviceCts` before the entry is dropped, cancelling in-flight fetches for THAT device only — other devices unaffected (AC-7.2)

**AC-9.4 — subsequent alive for a known UUID (FR-007 / FR-043 cache invariant)**

**Given** subsequent alive for an already-known UUID
**When** the registry observes it (call surface is `DiscoveryService` in Story 2.4; the registry method is shaped now)
**Then** the registry routes it through `entry.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId)` (FR-007)
**And** `RefreshSsdpMetadata` does NOT call any `Mark*` and triggers NO re-fetch (FR-043 cache invariant)
**And** `LastSeenUtc` is updated and `AliveCount` is incremented

**AC-9.5 — re-discovery after byebye (new instance)**

**Given** a known UUID that receives byebye then alive
**When** the second alive arrives
**Then** the registry creates a NEW `RegistryEntry` instance (different reference) — no reset/carry-over of the old entry
**And** the new entry starts at `Pending` with a fresh `DeviceCts`
**And** a fresh fetch is scheduled (via the dispatcher subscription — see Dev Notes)

**AC-9.x-identity — registry-backed identity lookup (Decision 8 / FR-041)**

**Given** the DI composition
**When** the App starts
**Then** `IDiagnosticIdentityLookup` resolves to a registry-backed `RegistryIdentityLookup` (replacing `NullIdentityLookup`) whose `TryGetFriendlyName(uuid)` returns `entry.Description?.FriendlyName` for a registry hit, else `null`
**And** `RegistryIdentityLookup.TryGetFriendlyName` is safe to call from ANY thread (it is invoked by `DiagnosticRingSink.Push` on the emitting thread, which may be a background fetch thread — see the ConcurrentDictionary note in Dev Notes)

**AC-9.x-tests — test suite (Pattern 14/15 + Amendment A2)**

**Given** the test suite
**When** I run the state-machine tests
**Then** the full AC-9.1..AC-9.7 transition matrix is exercised, each test carrying `[Trait("ac", "AC-9.<n>")]`
**And** AC-7.2 is exercised with a 5-device scenario where one byebye cancels only the targeted device's in-flight fetch
**And** the dispatcher happy-path / mismatch / cancel / fail flows are each covered with a stub `IUpnpHttpClient` + `InlineUiDispatcher`

## Tasks / Subtasks

### Task 1 — `DescriptionFetchState` enum (AC: #9.0)

- [x] **1.1** Create `src/ohSpy.Core/Devices/DescriptionFetchState.cs` (new `Devices/` folder):
  ```csharp
  namespace ohSpy.Core.Devices;

  /// <summary>Lifecycle of a device's description fetch (Decision 9). Pending and
  /// InFlight are transient; Loaded and Failed are terminal. Only <see cref="Loaded"/>
  /// entries appear in the tree (FR-047).</summary>
  public enum DescriptionFetchState
  {
      Pending,    // entry added; fetch not yet started
      InFlight,   // HTTP fetch issued; response not yet parsed
      Loaded,     // fetched + parsed OK — the only tree-visible state
      Failed,     // fetch or parse failed terminally
  }
  ```

### Task 2 — `RegistryEntry` + state machine (AC: #9.1, #9.2, #7.2)

- [x] **2.1** Create `src/ohSpy.Core/Devices/RegistryEntry.cs` — `public sealed class` (mutable entity, Pattern 9). Fields per D9; the `DeviceCts` is **linked to the adapter token** (ctor takes `adapterToken` — this supersedes D9's inline `new()`, per AC-7.2 / architecture line 905):
  ```csharp
  public sealed class RegistryEntry
  {
      public Guid Uuid { get; }
      public Uri LocationUrl { get; private set; }
      public DescriptionFetchState State { get; private set; } = DescriptionFetchState.Pending;
      public DeviceDescription? Description { get; private set; }   // non-null iff Loaded (AC-9.2)
      public string? FailureReason { get; private set; }           // non-null iff Failed
      public DateTime FirstSeenUtc { get; }
      public DateTime LastSeenUtc { get; private set; }
      public int AliveCount { get; private set; }
      public string? Server { get; private set; }
      public TimeSpan? CacheControlMaxAge { get; private set; }
      public string? BootId { get; private set; }
      public string? ConfigId { get; private set; }

      internal CancellationTokenSource DeviceCts { get; }          // D7 device level
      public CancellationToken DeviceToken => DeviceCts.Token;

      internal RegistryEntry(Guid uuid, Uri locationUrl, DateTime nowUtc, CancellationToken adapterToken)
      {
          Uuid = uuid;
          LocationUrl = locationUrl;
          FirstSeenUtc = nowUtc;
          LastSeenUtc = nowUtc;
          AliveCount = 1;
          DeviceCts = CancellationTokenSource.CreateLinkedTokenSource(adapterToken); // AC-7.2
      }
  }
  ```
- [x] **2.2** Implement the transition methods as `internal` with a single guard helper that throws `InvalidOperationException` on an illegal source state (AC-9.1):
  ```csharp
  internal void MarkInFlight()
  {
      Require(DescriptionFetchState.Pending);
      State = DescriptionFetchState.InFlight;
  }

  internal void MarkLoaded(DeviceDescription description)
  {
      Require(DescriptionFetchState.InFlight);
      Description = description;        // AC-9.2: set together with the state
      State = DescriptionFetchState.Loaded;
  }

  internal void MarkFailed(string reason)
  {
      RequireAny(DescriptionFetchState.Pending, DescriptionFetchState.InFlight);
      FailureReason = reason;
      State = DescriptionFetchState.Failed;
  }

  private void Require(DescriptionFetchState expected)
  {
      if (State != expected)
          throw new InvalidOperationException($"Illegal transition from {State}; expected {expected}.");
  }

  private void RequireAny(params DescriptionFetchState[] allowed)
  {
      if (Array.IndexOf(allowed, State) < 0)
          throw new InvalidOperationException($"Illegal transition from {State}.");
  }
  ```
- [x] **2.3** `RefreshSsdpMetadata` updates metadata + bumps liveness with **NO** state transition (AC-9.4):
  ```csharp
  internal void RefreshSsdpMetadata(DateTime nowUtc, string? server, TimeSpan? maxAge,
                                    string? bootId, string? configId)
  {
      LastSeenUtc = nowUtc;
      AliveCount++;
      Server = server;
      CacheControlMaxAge = maxAge;
      BootId = bootId;
      ConfigId = configId;
      // Deliberately NO Mark* call and NO re-fetch (FR-043 cache invariant).
  }
  ```
- [x] **2.4** **Do NOT** add `volatile`/locks to `RegistryEntry` fields. `Description` (a reference) is written on the UI thread and may be read on a background thread via the identity lookup; reference reads/writes are atomic and a slightly-stale `null` read just yields the `uuid:<uuid>` fallback (acceptable per the `IDiagnosticIdentityLookup` contract). The thread-safety that matters lives in the **registry's collection** (Task 3), not the entry.
- [x] **2.5** XML-doc each member; cite D9 / AC tags inline. The transition methods being `internal` is load-bearing — only Core (the dispatcher) and tests may drive the machine.

### Task 3 — `IDeviceRegistry` + `DeviceRegistry` (AC: #9.3, #9.4, #9.5, #7.2)

- [x] **3.1** Create `src/ohSpy.Core/Devices/IDeviceRegistry.cs`:
  ```csharp
  public interface IDeviceRegistry
  {
      bool TryGetEntry(Guid uuid, out RegistryEntry entry);
      IReadOnlyCollection<RegistryEntry> Loaded { get; }   // State==Loaded snapshot only
      int Count { get; }                                   // all states
      event Action<RegistryEntry> DeviceLoaded;
      event Action<RegistryEntry> DeviceUpdated;
      event Action<Guid> DeviceRemoved;
  }
  ```
- [x] **3.2** Create `src/ohSpy.Core/Devices/DeviceRegistry.cs` — `internal sealed`. **Backing store is `ConcurrentDictionary<Guid, RegistryEntry>`** — NOT a plain `Dictionary`. Rationale (CRITICAL — see Dev Notes "Threading reality"): `TryGetEntry` is read off the UI thread by `RegistryIdentityLookup` → `DiagnosticRingSink.Push` (which resolves identity on the *emitting* thread, often a background fetch thread). A plain `Dictionary` read concurrent with a UI-thread write is a data race. Mutations still happen on the UI thread (per D9); the ConcurrentDictionary only protects the dict structure against the cross-thread *read*.
  ```csharp
  internal sealed class DeviceRegistry(IUiDispatcher ui) : IDeviceRegistry
  {
      private readonly ConcurrentDictionary<Guid, RegistryEntry> _entries = new();

      public event Action<RegistryEntry>? DeviceLoaded;
      public event Action<RegistryEntry>? DeviceUpdated;
      public event Action<Guid>? DeviceRemoved;

      // Internal coordinator signal — the dispatcher subscribes (breaks the DI cycle;
      // see Dev Notes). NOT on IDeviceRegistry (external surface stays clean — no DeviceAdded).
      internal event Action<RegistryEntry>? EntryNeedsFetch;

      public bool TryGetEntry(Guid uuid, out RegistryEntry entry) => _entries.TryGetValue(uuid, out entry!);
      public int Count => _entries.Count;
      public IReadOnlyCollection<RegistryEntry> Loaded =>
          _entries.Values.Where(e => e.State == DescriptionFetchState.Loaded).ToArray();
      // ... OnAlive / OnByebye / Remove / RaiseDeviceLoaded / RaiseDeviceUpdated below
  }
  ```
- [x] **3.3** **DeviceRegistry MUST NOT depend on `IDiagnosticEmitter`.** All diagnostics in this story are emitted by the dispatcher. Adding `IDiagnosticEmitter` to the registry would form a DI cycle: `DiagnosticEmitter → DiagnosticRingSink → IDiagnosticIdentityLookup → DeviceRegistry → IDiagnosticEmitter`. Keep the registry's only dependency `IUiDispatcher` (for `AssertOnUiThread` on the mutators).
- [x] **3.4** `OnAlive` — the alive handler (called on the UI thread by `DiscoveryService` in 2.4 and by tests). New UUID ⇒ create entry + raise `EntryNeedsFetch`; known UUID ⇒ `RefreshSsdpMetadata`, no fetch (AC-9.4):
  ```csharp
  internal void OnAlive(Guid uuid, Uri location, DateTime nowUtc, string? server,
                        TimeSpan? maxAge, string? bootId, string? configId, CancellationToken adapterToken)
  {
      ui.AssertOnUiThread();
      if (_entries.TryGetValue(uuid, out var existing))
      {
          existing.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId); // AC-9.4
          return;
      }
      var entry = new RegistryEntry(uuid, location, nowUtc, adapterToken);
      entry.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId); // seed Server/maxAge/etc. (AliveCount→2? see 3.4a)
      _entries[uuid] = entry;
      EntryNeedsFetch?.Invoke(entry); // dispatcher schedules FetchAsync
  }
  ```
- [x] **3.4a** **Decide AliveCount-on-create semantics.** The ctor sets `AliveCount = 1` and seeds `Server`/`maxAge`/etc. from the FIRST alive — do NOT also call `RefreshSsdpMetadata` on a freshly-created entry (that would double-count to 2 and is redundant). Instead, set the SSDP metadata in the ctor (pass it in) OR seed it inline. Prefer: extend the ctor to accept `server/maxAge/bootId/configId` and set them there, leaving `AliveCount = 1`. Remove the stray `RefreshSsdpMetadata` call in the new-entry branch. (The skeleton above intentionally shows the wrong-looking double path so you catch it — fix it.)
- [x] **3.5** `OnByebye` — cancel the device CTS, remove, raise `DeviceRemoved` (AC-7.2 / FR-008):
  ```csharp
  internal void OnByebye(Guid uuid)
  {
      ui.AssertOnUiThread();
      if (_entries.TryRemove(uuid, out var entry))
      {
          entry.DeviceCts.Cancel();      // AC-7.2: cancels THIS device's in-flight fetch only
          DeviceRemoved?.Invoke(uuid);
      }
  }
  ```
- [x] **3.6** `Remove(Guid)` — the dispatcher's mismatch path (AC-9.6). Same effect as byebye (cancel + remove + raise). Make it idempotent (TryRemove already is). Factor `OnByebye` and `Remove` onto a shared private `RemoveCore(uuid)`.
- [x] **3.7** `RaiseDeviceLoaded(RegistryEntry)` and `RaiseDeviceUpdated(RegistryEntry)` — `internal` methods the dispatcher (and future re-fetch paths) call on the UI thread:
  ```csharp
  internal void RaiseDeviceLoaded(RegistryEntry entry) { ui.AssertOnUiThread(); DeviceLoaded?.Invoke(entry); }
  internal void RaiseDeviceUpdated(RegistryEntry entry) { ui.AssertOnUiThread(); DeviceUpdated?.Invoke(entry); }
  ```
  **Note:** `DeviceUpdated` has NO production trigger in Story 2.3 (no path mutates a `Loaded` entry's friendly name — re-fetch on CONFIGID change is a later story). It is shaped for FR-054 / forward use. Cover `RaiseDeviceUpdated` with a direct unit test so the wiring is proven, and note the absent production trigger in Completion Notes.

### Task 4 — `EagerDescriptionDispatcher` (AC: #9.x-dispatcher, #9.x-flow, #9.6, #9.7, #9.x-fail)

- [x] **4.1** Create `src/ohSpy.Core/Devices/EagerDescriptionDispatcher.cs` — `internal sealed`. Ctor injects the **concrete** `DeviceRegistry` (needs internal `Remove`/`RaiseDeviceLoaded` + `EntryNeedsFetch`), plus `IUpnpHttpClient`, `IDeviceDescriptionParser`, `IUiDispatcher`, `IDiagnosticEmitter`. Subscribe to `EntryNeedsFetch` in the ctor so new entries auto-schedule (this is what makes "a fresh fetch is scheduled" true — AC-9.5):
  ```csharp
  internal sealed class EagerDescriptionDispatcher
  {
      private const int MaxConcurrentFetches = 8;   // NFR-P6 / FR-043
      private readonly SemaphoreSlim _semaphore = new(MaxConcurrentFetches, MaxConcurrentFetches);
      private readonly IUpnpHttpClient _http;
      private readonly IDeviceDescriptionParser _descParser;
      private readonly IUiDispatcher _dispatcher;
      private readonly DeviceRegistry _registry;
      private readonly IDiagnosticEmitter _diag;

      public EagerDescriptionDispatcher(IUpnpHttpClient http, IDeviceDescriptionParser descParser,
          IUiDispatcher dispatcher, DeviceRegistry registry, IDiagnosticEmitter diag)
      {
          _http = http; _descParser = descParser; _dispatcher = dispatcher;
          _registry = registry; _diag = diag;
          _registry.EntryNeedsFetch += entry => _ = FetchAsync(entry); // fire-and-forget per entry
      }
  }
  ```
- [x] **4.2** Implement `FetchAsync` VERBATIM per the D9 canonical flow (AC-9.x-flow / 9.6 / 9.7 / 9.x-fail). Note the UDN normalisation (the model field is `Udn`, a `uuid:<guid>` string — NOT `RootUdn`):
  ```csharp
  internal async Task FetchAsync(RegistryEntry entry)
  {
      try
      {
          await _semaphore.WaitAsync(entry.DeviceToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
          return; // cancelled before we even started — entry is being removed (AC-9.7)
      }

      try
      {
          _dispatcher.Post(() => entry.MarkInFlight());
          var bytes = await _http.FetchDeviceDescriptionAsync(entry.LocationUrl, entry.DeviceToken)
                                  .ConfigureAwait(false);
          var description = _descParser.Parse(bytes);

          if (!UdnMatches(description.Udn, entry.Uuid))
          {
              _diag.Information(DiagCategories.DescriptionFetchMismatch, "root udn mismatch",
                  new DiagnosticContext
                  {
                      DeviceUuid = entry.Uuid,
                      Url = entry.LocationUrl.ToString(),
                      ErrorText = $"declared root: {description.Udn}",
                  });
              _dispatcher.Post(() => _registry.Remove(entry.Uuid)); // AC-9.6 — no MarkLoaded
              return;
          }

          _dispatcher.Post(() =>
          {
              entry.MarkLoaded(description);
              _registry.RaiseDeviceLoaded(entry); // admits the row to the tree (FR-005/FR-047)
          });
      }
      catch (OperationCanceledException) when (entry.DeviceToken.IsCancellationRequested)
      {
          // AC-9.7: caller-initiated cancel — silent, no transition, no diagnostic.
      }
      catch (Exception ex)
      {
          _diag.Warning(DiagCategories.DescriptionFetch, "description fetch failed",
              new DiagnosticContext
              {
                  DeviceUuid = entry.Uuid,
                  Url = entry.LocationUrl.ToString(),
                  ErrorText = ex.Message,
              });
          _dispatcher.Post(() => entry.MarkFailed(ex.Message)); // FR-047 — stays in registry, not in tree
      }
      finally
      {
          _semaphore.Release();
      }
  }
  ```
- [x] **4.3** `UdnMatches(string udn, Guid uuid)` — normalise and compare. UPnP UDN is `uuid:<guid>`; strip the (case-insensitive) `uuid:` prefix, `Guid.TryParse` the remainder, compare to `uuid`. Any parse failure ⇒ mismatch (returns false):
  ```csharp
  internal static bool UdnMatches(string udn, Guid uuid)
  {
      var s = udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? udn[5..] : udn;
      return Guid.TryParse(s, out var parsed) && parsed == uuid;
  }
  ```
  **Why this matters:** the architecture's D9 pseudo-code writes `description.RootUdn != entry.Uuid` as if `RootUdn` were a `Guid`. The real `DeviceDescription.Udn` is a `string` carrying the `uuid:` prefix. A naive `description.Udn != entry.Uuid.ToString()` comparison would FALSE-mismatch every real device (prefix + casing). Expose `UdnMatches` as `internal static` so it is unit-tested directly.
- [x] **4.4** **Semaphore wait is OUTSIDE the main try** so a wait-cancellation does not hit the `finally` `_semaphore.Release()` (you must not release a permit you never acquired). The skeleton above splits the two `try` blocks deliberately — keep them split. The post-`MarkInFlight` work is in the second `try`/`finally` that owns the release.
- [x] **4.5** `ConfigureAwait(false)` on every `await` (Core, Pattern 6). The `_dispatcher.Post(...)` calls are fire-and-forget UI marshals — do not await them.

### Task 5 — `RegistryIdentityLookup` (AC: #9.x-identity)

- [x] **5.1** Create `src/ohSpy.Core/Diagnostics/RegistryIdentityLookup.cs` — `internal sealed`, implements `IDiagnosticIdentityLookup`, depends on `IDeviceRegistry`:
  ```csharp
  internal sealed class RegistryIdentityLookup(IDeviceRegistry registry) : IDiagnosticIdentityLookup
  {
      public string? TryGetFriendlyName(Guid deviceUuid) =>
          registry.TryGetEntry(deviceUuid, out var entry) ? entry.Description?.FriendlyName : null;
  }
  ```
- [x] **5.2** **Thread-safety:** `TryGetFriendlyName` is invoked by `DiagnosticRingSink.Push` *on the emitting thread* (see `DiagnosticRingSink.cs:27` — "resolve BOTH labels HERE, on the calling thread"). Emitting threads include background fetch tasks. `registry.TryGetEntry` over the `ConcurrentDictionary` (Task 3.2) is therefore lock-free-safe; `entry.Description?.FriendlyName` is an atomic reference read (Task 2.4). No locking needed here — but the registry's ConcurrentDictionary is non-negotiable for this to be safe.

### Task 6 — DI registration (AC: #9.x-dispatcher, #9.x-identity)

- [x] **6.1** In `ServiceRegistration.cs`, **replace** the `NullIdentityLookup` line with the registry-backed lookup, and register the registry (double-registration so both the interface and the concrete resolve to the SAME singleton — the established `DiagnosticFileSink` pattern) + the dispatcher:
  ```csharp
  // Story 2.3 — Device registry (Decision 9). Concrete + interface forward to one singleton
  // so EagerDescriptionDispatcher can reach the internal Remove/RaiseDeviceLoaded/EntryNeedsFetch.
  services.AddSingleton<DeviceRegistry>();
  services.AddSingleton<IDeviceRegistry>(sp => sp.GetRequiredService<DeviceRegistry>());

  // Eager description dispatcher (Decision 9 + Decision 3). Subscribes to the registry's
  // EntryNeedsFetch in its ctor — must be constructed at startup to wire the subscription (Task 7).
  services.AddSingleton<EagerDescriptionDispatcher>();
  ```
  And change the existing identity-lookup registration from:
  ```csharp
  services.AddSingleton<IDiagnosticIdentityLookup, NullIdentityLookup>();
  ```
  to:
  ```csharp
  // Story 2.3: registry-backed identity resolution replaces the NullIdentityLookup placeholder.
  services.AddSingleton<IDiagnosticIdentityLookup, RegistryIdentityLookup>();
  ```
- [x] **6.2** Add `using ohSpy.Core.Devices;` to `ServiceRegistration.cs`.
- [x] **6.3** **Do NOT delete `NullIdentityLookup.cs`** — leave the type (it may be a useful test double / fallback). Just stop registering it. (If the build flags it as unused with no references, that's fine — it's `internal` and referenced by nothing; CA1812 "uninstantiated internal class" could fire. If so, either keep a `[assembly: ...]`-style suppression already present, or delete it and note the removal in the File List. Check the build; prefer keeping it unless CA1812 fails the build.)

### Task 7 — App startup wiring: pin the dispatcher (AC: #9.x-dispatcher)

- [x] **7.1** In `App.OnLaunched`, force-construct the dispatcher so its `EntryNeedsFetch` subscription is live for the app lifetime (mirrors the existing `IUiDispatcher` pin). Add after the diagnostic-sink wiring, before the adapter-scope construction:
  ```csharp
  // Story 2.3: construct the eager-description dispatcher so it subscribes to the registry's
  // fetch-trigger before any SSDP alive is processed (DiscoveryService wiring lands in 2.4).
  _ = Services.GetRequiredService<EagerDescriptionDispatcher>();
  ```
- [x] **7.2** Add `using ohSpy.Core.Devices;` to `App.xaml.cs`. This pin ALSO validates the full DI graph resolves with no cycle at startup (the diamond `Emitter→RingSink→IdentityLookup→Registry` + `Dispatcher→{Emitter,Registry}` — proves Task 3.3's "registry has no emitter dep" held).
- [x] **7.3** No other App change. The dispatcher does nothing visible until Story 2.4 feeds `registry.OnAlive(...)` — but pinning now keeps the subscription alive and fails fast if the graph regresses.

### Task 8 — Tests: `RegistryEntry` state machine (AC: #9.0, #9.1, #9.2, #7.2)

**Location:** `tests/ohSpy.Core.Tests/Devices/RegistryEntryTests.cs` (mirror-tree, new `Devices/` test folder). `[Trait("ac", "AC-9.<n>")]` per test.

- [x] **8.1** `State_Enum_HasFourValues_AC90` — `DescriptionFetchState` has exactly `Pending, InFlight, Loaded, Failed`.
- [x] **8.2** Legal transitions (one `[Fact]` or a `[Theory]`): `Pending→InFlight`, `InFlight→Loaded`, `Pending→Failed`, `InFlight→Failed` succeed and land on the expected state (`AC-9.1`).
- [x] **8.3** Illegal transitions throw `InvalidOperationException` (`AC-9.1`): `MarkLoaded` from `Pending`; `MarkInFlight` twice; `MarkInFlight`/`MarkLoaded`/`MarkFailed` from `Loaded`; same from `Failed`. A `[Theory]` over (start-state, action) pairs reads cleanest.
- [x] **8.4** `Description_NonNull_IffLoaded_AC92` — null in Pending/InFlight/Failed; non-null after `MarkLoaded`. `FailureReason` non-null iff Failed.
- [x] **8.5** `RefreshSsdpMetadata_DoesNotTransition_BumpsLiveness_AC94` — state unchanged; `LastSeenUtc`/`AliveCount`/`Server`/`CacheControlMaxAge`/`BootId`/`ConfigId` updated. Construct via the registry or a test helper since the ctor is `internal` (InternalsVisibleTo grants test access).
- [x] **8.6** `DeviceCts_LinkedToAdapterToken_AC72` — construct an entry with an adapter CTS token; cancel the adapter CTS; assert `entry.DeviceToken.IsCancellationRequested` is true.

### Task 9 — Tests: `DeviceRegistry` (AC: #9.3, #9.4, #9.5, #7.2)

**Location:** `tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs`. Use `InlineUiDispatcher` (Post runs inline → events fire synchronously).

- [x] **9.1** `OnAlive_NewUuid_AddsPendingEntry_RaisesEntryNeedsFetch_AC93` — new UUID ⇒ `Count==1`, entry is `Pending`, the internal `EntryNeedsFetch` fired once with the entry. (Subscribe to the internal event in-test — InternalsVisibleTo grants access.)
- [x] **9.2** `OnAlive_KnownUuid_RefreshesNoFetch_AC94` — second alive for the same UUID ⇒ no new entry (`Count==1`), `AliveCount` incremented, `EntryNeedsFetch` NOT fired again, no `Mark*` change.
- [x] **9.3** `Loaded_ContainsOnlyLoadedEntries_AC93` — add two entries, mark one `Loaded` (via the dispatcher path or directly through internal methods); `Loaded` returns exactly the loaded one; `Count` is 2.
- [x] **9.4** `DeviceLoaded_RaisedOnRaiseDeviceLoaded_NotOnAdd_AC93` — subscribing to `DeviceLoaded` yields nothing on `OnAlive`; fires exactly when `RaiseDeviceLoaded` runs. Confirms "no DeviceAdded; VM never sees pre-Loaded entries."
- [x] **9.5** `OnByebye_CancelsCtsRemovesRaisesRemoved_AC72` — `OnByebye` cancels the entry's `DeviceToken`, drops it (`Count==0`), raises `DeviceRemoved(uuid)` once. Byebye for an unknown UUID is a no-op (no throw, no event).
- [x] **9.6** `Rediscovery_AfterByebye_CreatesNewInstance_AC95` — alive → capture entry ref → byebye → alive again ⇒ a DIFFERENT `RegistryEntry` reference, `Pending`, fresh (non-cancelled) `DeviceToken`, and `EntryNeedsFetch` fired again.
- [x] **9.7** `RaiseDeviceUpdated_FiresEvent` — direct call raises `DeviceUpdated` (wiring proof; note no production trigger in 2.3).

### Task 10 — Tests: `EagerDescriptionDispatcher` + `UdnMatches` (AC: #9.x-flow, #9.6, #9.7, #9.x-fail, #7.2)

**Location:** `tests/ohSpy.Core.Tests/Devices/EagerDescriptionDispatcherTests.cs`. Add a fake `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs` (records the requested URL + token; returns caller-supplied bytes OR throws a caller-supplied exception OR honours cancellation). Use the **real** `DeviceDescriptionParser` with canned device-description XML (reuse a Story 1.4 fixture under `tests/.../Fixtures/`) OR a stub parser returning a canned `DeviceDescription` — prefer the stub parser for unit isolation, and add ONE happy test through the real parser for realism.

- [x] **10.1** `UdnMatches` unit table (`AC-9.6`): `"uuid:<g>"` vs same `g` ⇒ true; case-variant prefix/guid ⇒ true; different guid ⇒ false; missing `uuid:` but valid guid ⇒ true; garbage ⇒ false.
- [x] **10.2** `FetchAsync_Happy_MarksLoaded_RaisesDeviceLoaded_AC9flow` — stub HTTP returns matching-UDN bytes; with `InlineUiDispatcher`, after `await FetchAsync(entry)`: `entry.State==Loaded`, `entry.Description` set, `DeviceLoaded` raised once, semaphore released (assert by running a second fetch, or expose nothing and just verify no deadlock).
- [x] **10.3** `FetchAsync_Mismatch_RemovesEntry_EmitsInformation_NoMarkLoaded_AC96` — stub returns a description whose `Udn` is a different uuid ⇒ `Information`/`DescriptionFetchMismatch` emitted with `DeviceUuid`+`Url`+`ErrorText`; entry removed (`registry.Count==0` / `DeviceRemoved` raised); state never `Loaded`.
- [x] **10.4** `FetchAsync_Cancelled_NoTransition_NoDiagnostic_AC97` — cancel `entry.DeviceToken` before/at fetch; stub HTTP throws `OperationCanceledException` on the cancelled token ⇒ no `Mark*` past `InFlight` (or none at all if cancelled at the wait), zero diagnostics emitted.
- [x] **10.5** `FetchAsync_HttpThrows_MarksFailed_EmitsWarning_AC9fail` — stub HTTP throws `UpnpTransportException` (or any non-cancel exception) ⇒ `Warning`/`DescriptionFetch` with `DeviceUuid`+`Url`+`ErrorText`; `entry.State==Failed`; entry STILL in registry (`Count==1`); NOT in `Loaded`.
- [x] **10.6** `FetchAsync_ParseThrows_MarksFailed_AC9fail` — stub HTTP returns malformed bytes, real parser throws `UpnpProtocolException` ⇒ same `Warning`/`DescriptionFetch` + `Failed` path (parse failures route through the general catch per the AC).
- [x] **10.7** **AC-7.2 per-device byebye drill** `Byebye_CancelsOnlyTargetDeviceFetch_AC72` — 5 entries each with an HTTP stub that blocks until its token cancels (or a `TaskCompletionSource` gate); start all 5 fetches; `OnByebye` device 3 ⇒ device 3's fetch observes cancellation (silent, no Warning, no `Loaded`); the other 4 complete to `Loaded`. This is the headline Decision-7 drill.
- [x] **10.8** *(Optional, NFR-P6)* `FetchAsync_Concurrency_NeverExceeds8` — schedule 12 fetches against a gated HTTP stub that records peak concurrent in-flight count; assert peak ≤ 8. Use a counter + `Interlocked` in the stub. Keep the gate releasable so the test finishes fast.

### Task 11 — Tests: `RegistryIdentityLookup` (AC: #9.x-identity)

**Location:** `tests/ohSpy.Core.Tests/Diagnostics/RegistryIdentityLookupTests.cs`.

- [x] **11.1** `TryGetFriendlyName_LoadedEntry_ReturnsFriendlyName` — registry with a `Loaded` entry ⇒ returns `Description.FriendlyName`.
- [x] **11.2** `TryGetFriendlyName_UnknownOrPending_ReturnsNull` — unknown UUID ⇒ null; known-but-`Pending` (no Description) ⇒ null (so the ring sink falls back to `uuid:<uuid>`).

### Task 12 — Final verification (AC: all)

- [x] **12.1** **Compile the skeletons first (epic-1 retro action A).** Expect analyzer nits: `CA2007`/`ConfigureAwait` in Core; `CA1068` (CancellationToken-last) on any new method; `VSTHRD110`/`CA2012` on the fire-and-forget `_ = FetchAsync(...)` in the `EntryNeedsFetch` handler (the `_ =` discard is the sanctioned form — verify it satisfies the analyzers; if not, an explicit `async`-lambda wrapper that catches is acceptable). `CA1812` if `NullIdentityLookup` becomes unreferenced (Task 6.3). Fix at source.
- [x] **12.2** `dotnet build` 0 warnings / 0 errors under `TreatWarningsAsErrors=true`. NetArchTest `CoreAppBoundaryTests` still green — all new `Devices/` types are BCL + `ohSpy.Core.*` only (no WinUI / `ohSpy.App`).
- [x] **12.3** `dotnet test` green. Story 2.2 left **163 passing + 2 skipped (165)**. Story 2.3 adds ~30 tests; target ~195.
- [x] **12.4** `dotnet test --filter "category=chaos"` still exactly **1** (no chaos tests added — SSDP malformed-frame chaos is Story 2.4's parser layer, per Story 2.1 dev notes).
- [x] **12.5** **DI-graph smoke (the diamond):** the App pin in Task 7 means a build+run resolves `EagerDescriptionDispatcher` → `DeviceRegistry` + `IDiagnosticEmitter` → `DiagnosticRingSink` → `RegistryIdentityLookup` → `DeviceRegistry` with no cycle. If `BuildServiceProvider` throws a circular-dependency error at startup, Task 3.3 was violated (registry took an emitter dep). Optionally add a `ServiceRegistrationTests` assertion that `provider.GetRequiredService<EagerDescriptionDispatcher>()` resolves — but the App-tree DI isn't unit-tested today, so a manual run is acceptable.

## Dev Notes

### Architectural pillars this story implements

| Decision / pattern | What this story delivers | AC tag |
|---|---|---|
| **Decision 9 — Device registry + state machine** | `DescriptionFetchState`, `RegistryEntry` (Pending→InFlight→Loaded/Failed), `IDeviceRegistry`/`DeviceRegistry` (TryGetEntry/Loaded/Count + DeviceLoaded/Updated/Removed; no DeviceAdded), `EagerDescriptionDispatcher` canonical FetchAsync | AC-9.0–9.7 |
| **Decision 7 — Cancellation hierarchy (device level)** | `entry.DeviceCts = linked(adapterToken)`; byebye cancels only that device | AC-7.2 |
| **Decision 8 — Diagnostics identity** | `RegistryIdentityLookup` replaces `NullIdentityLookup`; UUID→friendly-name at diagnostic-arrival | AC-9.x-identity |
| **Decision 1 / Pattern 7 — UI-thread discipline** | All `Mark*`/`RefreshSsdpMetadata`/raise-event run on the UI thread (dispatcher `Post`); registry mutators `AssertOnUiThread` | AC-9.x |
| **NFR-P6 / FR-043 — bounded eager fetch** | `SemaphoreSlim(8)`; cache invariant (alive ⇒ refresh, no re-fetch); mismatched-root backstop | AC-9.x-dispatcher, 9.4, 9.6 |
| **FR-047 — failed devices hidden** | `Failed` entries stay in registry, never in `Loaded`, `DeviceLoaded` never fires | AC-9.x-fail |
| **Pattern 11 — DiagnosticContext** | `Description.Fetch` / `Description.Fetch.MismatchedRoot` carry `DeviceUuid` + `Url` (+ `ErrorText`) | AC-9.6, 9.x-fail |

### THE FIVE THINGS MOST LIKELY TO BITE YOU (read before coding)

1. **`DeviceDescription.Udn` is a `string` (`"uuid:<guid>"`), NOT a `Guid RootUdn`.** The architecture's D9 pseudo-code shows `description.RootUdn != entry.Uuid` — that field does not exist. The real model (`src/ohSpy.Core/Models/DeviceDescription.cs:17`) exposes `string Udn`. Compare via `UdnMatches` (Task 4.3): strip the case-insensitive `uuid:` prefix, `Guid.TryParse`, compare to `entry.Uuid`. A naive string compare false-mismatches **every real device** (prefix + hex casing) → every device gets removed → empty tree. This is the single highest-risk defect in the story.

2. **The registry backing store MUST be `ConcurrentDictionary`, not `Dictionary`.** The architecture says registry mutations are UI-thread-only "no locks" (line 1228) — true for *writes*. But `RegistryIdentityLookup.TryGetFriendlyName` is called by `DiagnosticRingSink.Push` **on the emitting thread** (`DiagnosticRingSink.cs:27` — "resolve … HERE, on the calling thread"), and diagnostics are emitted from background fetch tasks. So `TryGetEntry` reads the dict off-thread, concurrent with UI-thread writes. A plain `Dictionary` read during a write is a torn-read / corruption race. Use `ConcurrentDictionary` (writes still happen on the UI thread; the concurrent dict only guards the cross-thread read). See "Threading reality" below.

3. **`DeviceRegistry` must NOT depend on `IDiagnosticEmitter`.** Cycle: `DiagnosticEmitter → DiagnosticRingSink → IDiagnosticIdentityLookup → (RegistryIdentityLookup →) DeviceRegistry`. If the registry also took `IDiagnosticEmitter`, `BuildServiceProvider()` throws a circular-dependency error at startup. The registry's only dependency is `IUiDispatcher`. ALL diagnostics in this story are the **dispatcher's** job.

4. **Break the registry↔dispatcher cycle with the internal `EntryNeedsFetch` event.** The dispatcher needs the registry (for `Remove`/`RaiseDeviceLoaded`). If the registry also called the dispatcher to schedule fetches, you'd have a DI cycle. Instead the registry raises an `internal event Action<RegistryEntry> EntryNeedsFetch` when a new `Pending` entry is created; the dispatcher subscribes in its ctor. One-directional: dispatcher → registry. The event is `internal` (not on `IDeviceRegistry`) so the external surface stays clean (no `DeviceAdded`, AC-9.3).

5. **Semaphore acquire goes OUTSIDE the try/finally that releases it.** If `WaitAsync` is cancelled you never acquired a permit — releasing in a `finally` would over-release (corrupt the count). Two `try` blocks: one around the `WaitAsync` (catch cancel → return), one (with the `finally { Release(); }`) around everything after a successful acquire.

### Threading reality (the non-obvious part of Decision 9)

D9 says "no fields require volatile or locks" — that statement is about `RegistryEntry`'s fields under UI-thread mutation. It does NOT cover the registry's collection, which is read cross-thread by the identity lookup. The full picture:

- **Writes** to the dict (`OnAlive`/`OnByebye`/`Remove`) and **all `Mark*`** happen on the UI thread (dispatcher `Post`). `AssertOnUiThread()` in the mutators enforces it.
- **Reads** via `TryGetEntry` happen on the UI thread (VM) AND on arbitrary emitting threads (identity lookup). → `ConcurrentDictionary` (Task 3.2).
- **`entry.Description`** is written on the UI thread (`MarkLoaded`) and read off-thread (identity lookup). Reference read/write is atomic; a stale `null` read just yields the `uuid:<uuid>` fallback. No lock needed (Task 2.4).
- **`entry.DeviceToken`** is read off-thread by the dispatcher — `CancellationToken` is thread-safe by design.

### What this story does NOT do (scope discipline)

- **Does NOT parse SSDP or wire the transport.** No `SsdpAnnouncement`, no `DiscoveryService`. That's **Story 2.4**. Story 2.3 shapes `registry.OnAlive(...)` / `OnByebye(...)` (called by 2.4) and tests them directly. The `adapterToken` is passed as a parameter to `OnAlive` (the registry is an app-lifetime singleton; the token is per-adapter — 2.4's `DiscoveryService`, constructed within the adapter scope, supplies it; on adapter switch the registry is cleared per D7 step 6 and re-seeded with the new token).
- **Does NOT build any ViewModel or tree.** `IdentityKeyedSortedCollection` (Story 1.2) is the **VM's** tool (Story 2.5), NOT the registry's backing store. The registry is a flat keyed container; the VM wraps `Loaded` + the three events into the sorted tree in 2.5.
- **Does NOT trigger `DeviceUpdated` from any production path.** No 2.3 flow mutates a `Loaded` entry's friendly name (re-fetch on CONFIGID change is a later story). The event + `RaiseDeviceUpdated` are shaped for FR-054; cover the raise with a direct unit test (Task 9.7).
- **Does NOT add SCPD fetching.** `EagerDescriptionDispatcher` fetches the DEVICE DESCRIPTION only. Lazy per-service SCPD fetch is Story 2.6.
- **Does NOT add new packages** (`SemaphoreSlim`/`ConcurrentDictionary` are BCL) or new `DiagCategories` (`Description.Fetch`/`Description.Fetch.MismatchedRoot`/`Description.Parse` pre-exist).
- **Does NOT emit `Description.Parse`.** Per the AC, parse failures route through the general catch → `Description.Fetch`. `Description.Parse` remains a pre-added constant with no 2.3 emit site (it may be used by a future explicit parse-error path).

### Previous-story / existing-code intelligence — reuse, don't reinvent

- **`IUpnpHttpClient.FetchDeviceDescriptionAsync(Uri, CancellationToken) → Task<byte[]>`** (`src/ohSpy.Core/Http/IUpnpHttpClient.cs`). Returns RAW BYTES (Amendment A10) — the dispatcher parses. Throws `UpnpTransportException`/`UpnpTimeoutException` (Story 1.3) on failure → your general catch.
- **`IDeviceDescriptionParser.Parse(byte[]) → DeviceDescription`** (`src/ohSpy.Core/Scpd/IDeviceDescriptionParser.cs`). Synchronous. Throws `UpnpProtocolException` on malformed/XXE/oversize → your general catch. `DeviceDescription` (`src/ohSpy.Core/Models/DeviceDescription.cs`): `record` with `FriendlyName, DeviceType, Udn (string!), …, IReadOnlyList<ServiceDescription> Services`.
- **`IUiDispatcher`** (`src/ohSpy.Core/Threading/`): `Post(Action)`, `AssertOnUiThread()`. Test double `InlineUiDispatcher` runs `Post` inline (synchronous) — so in tests the `Mark*`/raise happen immediately after `await FetchAsync`.
- **`CapturingDiagnosticEmitter`** (`tests/.../Fakes/`): `Entries` list of `(Severity, Category, Message, Context)`. Use for the mismatch/fail diagnostic assertions.
- **`IDiagnosticIdentityLookup.TryGetFriendlyName(Guid) → string?`** + `NullIdentityLookup` (`src/ohSpy.Core/Diagnostics/`). You replace the registration, not the interface.
- **`DiagnosticRingSink.cs`** — READ IT (it's the consumer of your identity lookup). Confirms identity is resolved on the emitting thread (line 27) — the reason the registry needs `ConcurrentDictionary`.
- **`AdapterScope.AdapterToken`** (`src/ohSpy.Core/Discovery/AdapterScope.cs:38`) — the adapter-level token the device CTS links to. Not directly consumed in 2.3 (the token is passed into `OnAlive`), but this is the D7 parent.
- **Story 2.2 patterns:** `volatile` for cross-thread flags, the `[SuppressMessage]`-with-justification convention (A26), `internal sealed` + primary ctors, per-test fakes. Follow them.

### Epic-1 retro carry-forwards

- **Compile every skeleton first (action A).** Stories 2.1/2.2 each caught 4–5 analyzer errors in as-written skeletons. The `EntryNeedsFetch += entry => _ = FetchAsync(entry)` line and the two-try semaphore split are the most likely to draw analyzer attention here.
- **Trivially passing is a red flag (action B).** If the AC-7.2 per-device drill (Task 10.7) passes instantly without any fetch actually blocking, your gating is wrong — verify a fetch is genuinely in-flight before the byebye.
- **FluentAssertions 7.2.0 (MIT)**; xUnit; `InlineUiDispatcher`/`CapturingDiagnosticEmitter` are the canonical fakes.

### Code-style + pattern compliance

- **Pattern 1/9:** `DescriptionFetchState` `public enum`; `RegistryEntry` `public sealed class` (mutable entity); `DeviceRegistry`/`EagerDescriptionDispatcher`/`RegistryIdentityLookup` `internal sealed`; `IDeviceRegistry` public.
- **Pattern 2:** all new code in `ohSpy.Core/Devices` + `ohSpy.Core/Diagnostics`; App touch is the Task 6/7 registration + pin. NetArchTest-backstopped.
- **Pattern 6:** `ConfigureAwait(false)` on every `await`; `CancellationToken` last param; fire-and-forget only via `_ =` with the entry-needs-fetch handler.
- **Pattern 7:** singletons; concrete `DeviceRegistry` double-registered behind `IDeviceRegistry` (DiagnosticFileSink precedent).
- **Pattern 11:** `Description.Fetch.*` ⇒ `DeviceUuid` + `Url` mandatory (+ `ErrorText`). Pass them.
- **Pattern 12:** terse ASCII messages, sentence case, no trailing punctuation — `"root udn mismatch"`, `"description fetch failed"`.
- **Pattern 14/15 + A2:** `Method_Scenario_Expected_AC9x`; `[Trait("ac", "AC-9.<n>")]` / `[Trait("ac","AC-7.2")]`.

### Anti-patterns to avoid

- **Don't compare `description.Udn` to `entry.Uuid.ToString()` directly.** Prefix + casing ⇒ universal false-mismatch. Use `UdnMatches`.
- **Don't use a plain `Dictionary` in the registry.** Cross-thread read race (see Threading reality). `ConcurrentDictionary`.
- **Don't give `DeviceRegistry` an `IDiagnosticEmitter` dependency.** DI cycle.
- **Don't add a `DeviceAdded` event** to `IDeviceRegistry`. VMs must not see pre-`Loaded` entries (AC-9.3). Use the `internal EntryNeedsFetch` for the dispatcher only.
- **Don't release the semaphore in a `finally` that also covers the `WaitAsync`.** Over-release. Split the try blocks.
- **Don't call `Mark*` or raise events off the UI thread.** Always `_dispatcher.Post(...)`. The registry mutators `AssertOnUiThread()`.
- **Don't re-fetch on a subsequent alive.** `RefreshSsdpMetadata` only (FR-043 cache invariant). Re-fetch happens ONLY on byebye-then-alive (new entry, AC-9.5).
- **Don't emit any diagnostic on cancellation** (AC-9.7). The `when (entry.DeviceToken.IsCancellationRequested)` filter keeps the cancel path silent.
- **Don't let `Loaded`/`Failed` entries transition again.** They're terminal (AC-9.1). The `Require`/`RequireAny` guards enforce it.
- **Don't double-count `AliveCount` when creating an entry** (Task 3.4a) — seed metadata in the ctor; don't also call `RefreshSsdpMetadata` on the new entry.

### Forward-looking dependencies — what later stories need from us

| Story | Consumes from 2.3 |
|---|---|
| 2.4 (`SsdpParser` + `DiscoveryService`) | Calls `registry.OnAlive(uuid, location, nowUtc, server, maxAge, bootId, configId, adapterToken)` / `registry.OnByebye(uuid)` from the parsed datagram stream; the dispatcher auto-fetches via `EntryNeedsFetch`. |
| 2.5 (Shell + Device Tree) | Subscribes to `IDeviceRegistry.DeviceLoaded`/`DeviceUpdated`/`DeviceRemoved`; wraps `Loaded` into `IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>`. Reads `entry.Description.FriendlyName`, `LocationUrl`, `DeviceType`. |
| 2.6 (Service/Action expansion) | Reads `entry.Description.Services` (already flattened, FR-053) for lazy SCPD fetch; `entry.DeviceToken` parents the SCPD fetch CTS. |
| 5.2 (Adapter switch) | Clears the registry (D7 step 6 — raises `DeviceRemoved` per UUID) and re-seeds via the new adapter's `DiscoveryService`/`adapterToken`. |

### Architecture amendments to anticipate

Stories with amendments so far: 1.1→A6/7/8, 1.3→A9/10/11, 1.5→A14, 1.6→A16/18, 2.1→A22, 2.2→A23/A26. Candidates to flag in Completion Notes if encountered:

- **A27 (likely):** D9's `RegistryEntry.DeviceCts { get; } = new()` inline initialiser is wrong — it must be `CreateLinkedTokenSource(adapterToken)` per AC-7.2 / line 905. Recommend patching D9's code block to take the adapter token in the ctor. (You're implementing the correct form; the amendment just fixes the architecture's stale snippet.)
- **A28 (likely):** D9's "no locks" prose + the FetchAsync `description.RootUdn` field are both inaccurate against the real code (registry needs `ConcurrentDictionary` for the identity-lookup read path; the model field is `Udn:string` not `RootUdn:Guid`). Recommend amending D9 to (a) note the ConcurrentDictionary requirement with the `DiagnosticRingSink.Push`-on-emitting-thread rationale, and (b) replace `RootUdn` with the `Udn` normalisation (`UdnMatches`).
- **A29 (speculative):** if the `EntryNeedsFetch` internal-event coordination proves awkward when 2.4 wires `DiscoveryService`, document the chosen registry↔dispatcher↔discovery coordination shape.

### Project Structure Notes

**New (8 source + ~5 test):**

```
src/ohSpy.Core/
├── Devices/                                  ← NEW folder
│   ├── DescriptionFetchState.cs              ← Task 1
│   ├── RegistryEntry.cs                      ← Task 2
│   ├── IDeviceRegistry.cs                    ← Task 3.1
│   ├── DeviceRegistry.cs                     ← Task 3.2–3.7
│   └── EagerDescriptionDispatcher.cs         ← Task 4
└── Diagnostics/
    └── RegistryIdentityLookup.cs             ← Task 5

tests/ohSpy.Core.Tests/
├── Devices/                                  ← NEW folder
│   ├── RegistryEntryTests.cs                 ← Task 8
│   ├── DeviceRegistryTests.cs                ← Task 9
│   └── EagerDescriptionDispatcherTests.cs    ← Task 10
├── Diagnostics/
│   └── RegistryIdentityLookupTests.cs        ← Task 11
└── Fakes/
    └── StubUpnpHttpClient.cs                 ← Task 10
```

**Modified (2):**

- `src/ohSpy.App/Composition/ServiceRegistration.cs` — registry double-registration + dispatcher + swap `NullIdentityLookup`→`RegistryIdentityLookup` + `using ohSpy.Core.Devices;`.
- `src/ohSpy.App/App.xaml.cs` — pin `EagerDescriptionDispatcher` at startup + `using ohSpy.Core.Devices;`.

**Does NOT modify:** `DiagCategories.cs` (constants pre-exist), `Directory.Packages.props` (BCL only), `ohSpy.Core.csproj` (InternalsVisibleTo already grants Tests + App), `IUpnpHttpClient`/`IDeviceDescriptionParser`/`DeviceDescription` (consumed as-is).

### Testing standards summary

- xUnit + FluentAssertions 7.2.0. `[Trait("ac", "AC-9.<n>")]` / `[Trait("ac","AC-7.2")]` per AC-traceable test (A2).
- **`InlineUiDispatcher`** (synchronous `Post`) for all registry/dispatcher tests — makes the posted `Mark*`/raise observable right after `await`.
- **`CapturingDiagnosticEmitter`** for the mismatch/fail diagnostic assertions.
- New **`StubUpnpHttpClient`** fake (Task 10). Stub `IDeviceDescriptionParser` for isolation + one real-parser happy test.
- **No chaos tests** (chaos suite stays at 1). No `[Trait("category","integration")]` needed — these are fast pure-logic tests.
- **`dotnet test` target ~195** (165 baseline + ~30). **`category=chaos` target: 1.**

### References

> Authoritative paths:
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (Decision 9 lines ~1082–1261; Decision 7 device level lines 740–878; Decision 8 identity lines ~971–987; Pattern 11 lines ~1906–1926)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (Story 2.3 lines 849–924)
> - Previous story: `_bmad-output/implementation-artifacts/2-2-network-adapter-enumerator-adapter-scope-startup-bind.md`

- [Source: epics.md#Story-2.3] — verbatim ACs (lines 849–924).
- [Source: architecture.md#Decision-9] — RegistryEntry shape, state machine (AC-9.1), Description-iff-Loaded (AC-9.2), IDeviceRegistry surface (AC-9.3), FetchAsync canonical flow, mismatch (AC-9.6), cancel (AC-9.7), RefreshSsdpMetadata (AC-9.4), re-discovery (AC-9.5).
- [Source: architecture.md#Decision-7] — device-level CTS `linked(adapterToken)`; AC-7.2 per-device byebye (lines 740–787).
- [Source: architecture.md#Decision-8] — identity resolution at diagnostic arrival (registry.TryGetEntry → friendly name).
- [Source: architecture.md#Pattern-11] — `Description.Fetch.*` mandatory context `DeviceUuid` + `Url`.
- [Source: architecture.md#Amendment-A10] — `FetchDeviceDescriptionAsync` returns `Task<byte[]>`; parser is the caller's concern.
- [Source: src/ohSpy.Core/Http/IUpnpHttpClient.cs] — `FetchDeviceDescriptionAsync(Uri, CancellationToken) → Task<byte[]>`.
- [Source: src/ohSpy.Core/Scpd/IDeviceDescriptionParser.cs] — `Parse(byte[]) → DeviceDescription`, throws `UpnpProtocolException`.
- [Source: src/ohSpy.Core/Models/DeviceDescription.cs] — `Udn` (string) is the identity field; `FriendlyName`, `Services` (flattened, FR-053).
- [Source: src/ohSpy.Core/Diagnostics/DiagnosticRingSink.cs:27,51] — identity resolved on the EMITTING thread → registry needs `ConcurrentDictionary`.
- [Source: src/ohSpy.Core/Diagnostics/IDiagnosticIdentityLookup.cs + NullIdentityLookup.cs] — the lookup contract you replace.
- [Source: src/ohSpy.Core/Diagnostics/DiagCategories.cs:33,36,39] — `Description.Fetch` / `.MismatchedRoot` / `.Parse` constants pre-exist.
- [Source: src/ohSpy.Core/Threading/IUiDispatcher.cs] — `Post` / `AssertOnUiThread`.
- [Source: src/ohSpy.App/Composition/ServiceRegistration.cs] — current registrations; `NullIdentityLookup` line to swap.
- [Source: src/ohSpy.Core/Discovery/AdapterScope.cs:38] — `AdapterToken` (device-CTS parent).
- [Source: tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs + CapturingDiagnosticEmitter.cs] — canonical test doubles.
- [Source: 2-2-…md] — `volatile`/`[SuppressMessage]`/internal-sealed/per-test-fake conventions; A23/A26.
- [Source: project_ohspy memory] — native Windows desktop UPnP inspector; raw-BCL UPnP; no CI (pre-commit chaos hook is the net).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Claude Opus 4.8, 1M context) — bmad-dev-story workflow.

### Debug Log References

**Spec-skeleton compile check (epic-1 retro action A) — issues caught at build, none logic bugs:**

1. **CA1001** — `EagerDescriptionDispatcher` owns the `SemaphoreSlim` (IDisposable) but wasn't
   disposable. It's a DI singleton, so implemented `IDisposable` disposing `_semaphore` (MS.DI
   disposes IDisposable singletons at provider teardown) rather than suppressing.
2. **IDE0005** — the skeleton's `using System.Net;` in `RegistryEntry.cs` was unused; removed.
3. **CS0854** ×2 (tests) — FluentAssertions `.Match<>(e => …)` builds an expression tree, which
   cannot contain a call that omits an optional argument (`Loc()` has `i = 0`). Hoisted the URL
   into a local before the lambda.

**One pre-existing test destabilised by the new test classes (diagnosed, fixed at root):**

- `DiagnosticEmitterTests.Verbose_BelowMinSeverity_AllocatesZeroDiagnosticEntries` (Story 1.5)
  failed once in the full run, then **passed in isolation (17 ms)**. Root cause: it measured
  **process-wide** allocations via `GC.GetTotalAllocatedBytes(precise: true)`, but xUnit runs
  test classes in parallel — concurrent allocations from other classes leak into the window. The
  ~36 new `Devices` tests increased that pressure and surfaced the latent flaw. Fixed by switching
  to **thread-local** `GC.GetAllocatedBytesForCurrentThread()`, which isolates the measured loop
  from other threads. Production code untouched; the test's zero-allocation intent is preserved
  and now robust under parallelism. Re-ran the full suite twice — stable at 199 passing.

### Completion Notes List

**All ACs satisfied; 36 new tests (165 → 199 passing, +2 pre-existing skips = 201 total).
Build 0 warnings / 0 errors under `TreatWarningsAsErrors`; chaos suite unchanged at 1; DI graph
resolves with no cycle (App pin validates it).**

- **The five flagged traps were all handled as specified:**
  1. `UdnMatches` normalises the `uuid:` prefix + casing (`DeviceDescription.Udn` is a `string`,
     not `Guid RootUdn`) — unit-tested via a `[Theory]` (prefix/casing/garbage). A naive compare
     would have removed every real device.
  2. `DeviceRegistry` backs onto a **`ConcurrentDictionary`** — `RegistryIdentityLookup` reads
     `TryGetEntry` off the UI thread (verified: `DiagnosticRingSink.Push` resolves identity on the
     emitting thread, line 27/51).
  3. `DeviceRegistry` depends only on `IUiDispatcher` (NOT `IDiagnosticEmitter`) — the App-startup
     pin proves `BuildServiceProvider` resolves the `Emitter→RingSink→IdentityLookup→Registry`
     diamond with no cycle.
  4. Registry↔dispatcher cycle broken via the `internal EntryNeedsFetch` event (dispatcher
     subscribes in its ctor; registry never references the dispatcher).
  5. Semaphore acquire sits OUTSIDE the release `try/finally` (a cancelled wait never acquired a
     permit, so it must not `Release()`).
- **`AliveCount` design (Task 3.4a resolved):** the ctor leaves `AliveCount = 0`; `OnAlive` ALWAYS
  calls `RefreshSsdpMetadata` (new and existing), so metadata seeding has a single path and the
  count is 1 after the first alive — no double-count.
- **`DeviceUpdated` has no production trigger in 2.3** (no path mutates a Loaded entry's friendly
  name; CONFIGID-change re-fetch is a later story). `RaiseDeviceUpdated` is covered by a direct
  wiring test.
- **AC-7.2 per-device byebye drill** proves isolation directly: 5 devices reach InFlight (blocked
  HTTP), byebye device #3 → it's removed and its token cancelled, while the other 4 tokens are
  asserted **not** cancelled and stay InFlight; zero Warnings (cancellation is silent).
- **NFR-P6 cap** verified deterministically: 12 sequential `OnAlive` calls drive 8 fetches to the
  (blocking) HTTP stub and leave 4 waiting at the semaphore → `PeakConcurrency == 8`.
- **`Description.Parse` is not emitted** — per the AC, parse failures route through the dispatcher's
  general catch → `Description.Fetch`. The pre-added `Description.Parse` constant has no 2.3 emit
  site (left in place).
- **`NullIdentityLookup` kept** (Task 6.3) — no longer registered, referenced by no live code;
  build did NOT flag CA1812, so it remains as a potential test double / fallback.
- **Test-parser note (minor deviation from Task 10):** dispatcher tests use the
  `StubDeviceDescriptionParser` throughout (the parse-failure path uses a throwing stub). The real
  `DeviceDescriptionParser` is already covered by Story 1.4's tests; a stub throwing the same
  exception class exercises the identical dispatcher catch path, so a separate real-parser happy
  test was not added.

**Amendment candidates raised for architect review:**

- **A27 (confirmed):** D9's `RegistryEntry.DeviceCts { get; } = new()` inline initialiser is wrong —
  it must be `CreateLinkedTokenSource(adapterToken)` (AC-7.2 / line 905). Implemented the linked
  form (ctor takes the adapter token); recommend patching D9's code snippet.
- **A28 (confirmed):** D9 is inaccurate against the real code in two ways — (a) the registry needs a
  `ConcurrentDictionary`, not a lock-free plain Dictionary, because identity resolution reads
  cross-thread (`DiagnosticRingSink.Push` on the emitting thread); (b) the mismatch check uses
  `DeviceDescription.Udn` (a `uuid:<guid>` string) not a `Guid RootUdn`. Recommend amending D9's
  prose + FetchAsync snippet (`UdnMatches` normalisation).
- **A29 (new — test robustness):** allocation-sensitive tests must use
  `GC.GetAllocatedBytesForCurrentThread()`, never the process-wide `GC.GetTotalAllocatedBytes`,
  because xUnit parallelism makes the process-wide counter non-deterministic. Recommend a one-line
  testing-standards note so future zero-allocation assertions don't reintroduce the flake.

### File List

**New (6 source + 6 test):**

- `src/ohSpy.Core/Devices/DescriptionFetchState.cs`
- `src/ohSpy.Core/Devices/RegistryEntry.cs`
- `src/ohSpy.Core/Devices/IDeviceRegistry.cs`
- `src/ohSpy.Core/Devices/DeviceRegistry.cs`
- `src/ohSpy.Core/Devices/EagerDescriptionDispatcher.cs`
- `src/ohSpy.Core/Diagnostics/RegistryIdentityLookup.cs`
- `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs`
- `tests/ohSpy.Core.Tests/Fakes/StubDeviceDescriptionParser.cs`
- `tests/ohSpy.Core.Tests/Devices/RegistryEntryTests.cs`
- `tests/ohSpy.Core.Tests/Devices/DeviceRegistryTests.cs`
- `tests/ohSpy.Core.Tests/Devices/EagerDescriptionDispatcherTests.cs`
- `tests/ohSpy.Core.Tests/Diagnostics/RegistryIdentityLookupTests.cs`

**Modified (3):**

- `src/ohSpy.App/Composition/ServiceRegistration.cs` — `DeviceRegistry` (concrete + interface
  forward) + `EagerDescriptionDispatcher` registrations; swapped `NullIdentityLookup` →
  `RegistryIdentityLookup`; `using ohSpy.Core.Devices;`.
- `src/ohSpy.App/App.xaml.cs` — pin `EagerDescriptionDispatcher` at startup (wires the
  `EntryNeedsFetch` subscription + validates the DI graph); `using ohSpy.Core.Devices;`.
- `tests/ohSpy.Core.Tests/Diagnostics/DiagnosticEmitterTests.cs` — allocation test switched to
  thread-local `GC.GetAllocatedBytesForCurrentThread()` (parallelism robustness; A29).

## Senior Developer Review (AI)

**Review Date:** 2026-06-02
**Reviewer Model:** Claude Sonnet 4.6 (bmad-code-review, 7-angle parallel)
**Outcome:** Changes Requested → APPROVED-WITH-MINOR-FIXES

### Action Items

- [x] [Review][Patch] `DeviceCts` never disposed — leaked callback registration on adapter CTS [`src/ohSpy.Core/Devices/DeviceRegistry.cs:RemoveCore`] — fixed: `entry.DeviceCts.Dispose()` added after `Cancel()` in `RemoveCore`; `RegistryEntry.DeviceToken` snapshotted at construction so it remains readable post-dispose
- [x] [Review][Patch] Orphaned `MarkInFlight` post after byebye cancels the entry mid-fetch [`src/ohSpy.Core/Devices/EagerDescriptionDispatcher.cs:61`] — fixed: guard `if (!entry.DeviceToken.IsCancellationRequested)` in the Post lambda prevents the Pending→InFlight transition on an already-removed entry
- [x] [Review][Defer] `Loaded` property allocates ToArray on every read — deferred; snapshot semantics are intentional and cost is negligible at realistic device count

## Change Log

| Date | Change |
|---|---|
| 2026-06-02 | Implemented Story 2.3: `DescriptionFetchState` enum, `RegistryEntry` (Pending→InFlight→Loaded/Failed state machine, device-CTS linked to adapter token), `IDeviceRegistry`/`DeviceRegistry` (ConcurrentDictionary backing, no DeviceAdded, internal EntryNeedsFetch to break the dispatcher cycle), `EagerDescriptionDispatcher` (SemaphoreSlim(8) canonical fetch + UdnMatches normalisation), `RegistryIdentityLookup` replacing NullIdentityLookup; DI registrations + App-startup dispatcher pin. Also fixed a pre-existing flaky allocation test (process-wide → thread-local GC measurement) surfaced by the new test load. 36 new tests (165→199 passing, +2 skips). Build 0 warnings; chaos suite unchanged at 1; DI graph cycle-free. Amendments A27/A28 (D9 stale snippets) + A29 (allocation-test robustness) raised. Status → review. |
| 2026-06-02 | Applied code-review patches (Sonnet 4.6, APPROVED-WITH-MINOR-FIXES): `DeviceCts.Dispose()` in `RemoveCore` + `DeviceToken` snapshotted at construction (prevents `ObjectDisposedException` post-byebye); `MarkInFlight` Post guarded by token-cancelled check (prevents orphaned InFlight state on removed entry). 199 passing + 2 skipped. Status → done. |
