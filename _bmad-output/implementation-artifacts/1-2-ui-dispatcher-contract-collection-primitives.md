---
baseline_commit: a55c30d42182f5626e53fe426718691ee08c2828
---

# Story 1.2: UI Dispatcher Contract & Collection Primitives

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an **ohSpy developer**,
I want **the `IUiDispatcher` thread-marshalling contract plus the two identity-tracked observable collection primitives that virtualised lists will bind to**,
so that **subsequent stories can write thread-safe, identity-stable, redraw-free collection updates with one consistent pattern instead of re-deriving the rules each time**.

## Acceptance Criteria

> Each AC is restated verbatim from epics.md §Story 1.2 (lines 466–518). The architecture-level AC IDs (D1, AC-6.1..AC-6.6) cited inline trace back to architecture.md §Decision-1 and §Decision-6.

### AC-1 — `IUiDispatcher` contract surface (D1)

**Given** the `IUiDispatcher` interface in `ohSpy.Core/Threading/IUiDispatcher.cs`
**When** I look at its surface
**Then** it exposes `Post(Action)`, `PostAsync<T>(Func<T> readback)`, `IsOnUiThread`, and `AssertOnUiThread()` (D1)
**And** `AssertOnUiThread()` throws `InvalidOperationException` in Release as well as Debug — this is a coding-error invariant, not a debug aid (D1)

### AC-2 — `WinUiDispatcher` impl

**Given** `ohSpy.App/Windowing/WinUiDispatcher.cs`
**When** I read the impl
**Then** it wraps `Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()` captured during App startup on the UI thread
**And** `Post` forwards to `_queue.TryEnqueue`
**And** `PostAsync` returns a `TaskCompletionSource`-backed `Task<T>` posted via `TryEnqueue`
**And** `IsOnUiThread` reads `_queue.HasThreadAccess`

### AC-3 — `InlineUiDispatcher` test fake

**Given** `tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs`
**When** unit tests use it
**Then** `Post(Action a)` executes `a()` synchronously
**And** `PostAsync` runs the readback inline
**And** `IsOnUiThread` returns `true`
**And** `AssertOnUiThread()` no-ops

### AC-4 — `BoundedObservableCollection<T>` semantics (D6)

**Given** `ohSpy.Core/Collections/BoundedObservableCollection<T>.cs`
**When** I call `PrependNewest(item)` at capacity
**Then** the collection emits exactly two `INotifyCollectionChanged` notifications — `Add(index=0)` and `Remove(index=Count)` — and NEVER `Reset` (AC-6.1)
**And** 100,000 sequential `PrependNewest` calls on a 10,000-capacity collection complete in O(N) total wall time with zero `Reset` notifications (AC-6.2)
**And** the backing store is a ring buffer (`T[]` of capacity) so `PrependNewest` is O(1) — no list shift, no array copy
**And** `Clear()` emits a single `Reset` notification (AC-6.6)
**And** indexed access `this[0]` returns the newest item; `this[Count-1]` returns the oldest

### AC-5 — `IdentityKeyedSortedCollection<TIdentity, TItem>` semantics (D6)

**Given** `ohSpy.Core/Collections/IdentityKeyedSortedCollection<TIdentity, TItem>.cs`
**When** I call `Update(item)` with the sort key unchanged
**Then** no `INotifyCollectionChanged` notification is emitted (AC-6.3)

**Given** the same collection
**When** I call `Update(item)` with the sort key changed
**Then** exactly one `Move(old, new)` notification is emitted (AC-6.4) — never `Remove`+`Add`
**And** the underlying item instance is preserved across the migration so any UI selection/expansion state bound to that node survives (AC-6.5 verified via integration test if WinUI test infrastructure exists, otherwise via collection-level identity assertion)
**And** the backing store is `List<TItem>` + `Dictionary<TIdentity, int>` for O(1) identity-lookup

### AC-6 — Off-thread mutation discipline

**Given** both primitives are used cross-thread
**When** any mutation is attempted off the UI thread
**Then** the call surfaces the dispatcher-violation contract appropriately (these collections are UI-thread-owned; cross-thread mutations are expected to marshal through `IUiDispatcher`)

### AC-7 — DI composition root (Pattern 7)

**Given** the DI composition root
**When** the App starts
**Then** `IUiDispatcher` is registered as a singleton via `ServiceRegistration.RegisterServices` (Pattern 7) with `WinUiDispatcher` as the implementation

## Tasks / Subtasks

> Tasks are ordered to land Core types first (zero WinUI dependencies, fully unit-testable), then the App-side impl, then DI wiring. AC mappings explicit. Architecture's pinned versions / paths / patterns are the contract — do not deviate.

### Task 1 — Author `IUiDispatcher` interface in Core (AC: #1)

- [x] **1.1** Create folder `src/ohSpy.Core/Threading/` (does not yet exist after Story 1.1).
- [x] **1.2** Create `src/ohSpy.Core/Threading/IUiDispatcher.cs` with EXACTLY this surface [Source: architecture.md §Decision-1, lines 165–204]:
  ```csharp
  namespace ohSpy.Core.Threading;

  /// <summary>
  /// Thread-marshalling contract for UI-thread access from Core. Core never touches
  /// <c>Microsoft.UI.Dispatching.DispatcherQueue</c> directly; every cross-thread
  /// VM mutation goes through this interface. NFR-P3 binding invariant.
  /// </summary>
  public interface IUiDispatcher
  {
      /// <summary>Fire-and-forget marshal to UI thread.</summary>
      void Post(Action action);

      /// <summary>Round-trip: invoke <paramref name="readback"/> on the UI thread and await its result.</summary>
      Task<T> PostAsync<T>(Func<T> readback);

      /// <summary>Cheap query; safe to call from any thread.</summary>
      bool IsOnUiThread { get; }

      /// <summary>
      /// Throws <see cref="InvalidOperationException"/> if not on the UI thread.
      /// Coding-error invariant — throws in Release as well as Debug.
      /// </summary>
      void AssertOnUiThread();
  }
  ```
- [x] **1.3** No partial class, no extension methods, no helper sub-interfaces. The four members above ARE the contract.

### Task 2 — Author `BoundedObservableCollection<T>` in Core (AC: #4)

- [x] **2.1** Create folder `src/ohSpy.Core/Collections/`.
- [x] **2.2** Create `src/ohSpy.Core/Collections/BoundedObservableCollection.cs` implementing this contract [Source: architecture.md §Decision-6]:
  ```csharp
  namespace ohSpy.Core.Collections;

  using System.Collections;
  using System.Collections.Specialized;

  /// <summary>
  /// Newest-first bounded observable collection backed by a ring buffer.
  /// <para>
  /// <see cref="PrependNewest(T)"/> inserts at logical index 0; once at capacity, the
  /// oldest item (logical index <c>Count</c>) is evicted. Emits exactly:
  /// </para>
  /// <list type="bullet">
  ///   <item>Below capacity: one <c>Add(index=0)</c>.</item>
  ///   <item>At capacity: <c>Add(index=0)</c> followed by <c>Remove(index=Count)</c>
  ///         (where <c>Count</c> is the post-Add value, i.e. <c>Capacity</c>). NEVER <c>Reset</c>.</item>
  /// </list>
  /// <para>
  /// <see cref="Clear"/> is the ONLY operation that emits <c>Reset</c>.
  /// </para>
  /// <para>UI-thread-owned. Not thread-safe. Callers must marshal mutations via <c>IUiDispatcher</c>.</para>
  /// </summary>
  public sealed class BoundedObservableCollection<T> : IReadOnlyList<T>, INotifyCollectionChanged
  {
      public BoundedObservableCollection(int capacity);  // capacity > 0
      public int Capacity { get; }
      public int Count { get; }
      public T this[int index] { get; }                  // index 0 = newest, Count-1 = oldest

      public void PrependNewest(T item);
      public void Clear();

      public IEnumerator<T> GetEnumerator();             // newest-first iteration
      IEnumerator IEnumerable.GetEnumerator();

      public event NotifyCollectionChangedEventHandler? CollectionChanged;
  }
  ```
- [x] **2.3** **Backing store MUST be a ring buffer.** `T[] _buffer = new T[capacity]`, plus `int _head` (logical-zero offset) and `int _count`. `PrependNewest` is O(1):
  1. If `_count < capacity`: decrement `_head` (wrap modulo capacity), write item, `_count++`. Emit `Add(item=newItem, index=0)`. Done.
  2. If `_count == capacity` (ring wrap):
     - **Capture the evicted item BEFORE overwriting:** `var evictedItem = _buffer[(_head + capacity - 1) % capacity];` (the current tail).
     - Decrement `_head` (wrap), overwrite the slot at the new `_head` with the new item, `_count` STAYS at `capacity` (the source's reported `Count` never exceeds capacity).
     - **Emit `Add(item=newItem, index=0)` FIRST**, then emit `Remove(item=evictedItem, index=capacity)`. The two notifications go out in that exact order. NEVER `Reset`.

  > **Why `Remove(index=capacity)` is correct even though `Count == capacity`** (the index appears past-the-end of the source's reported size): per `INotifyCollectionChanged` convention, notification indices describe the *consumer's mirror state* after applying the events in order, not the source's transient state. After applying `Add(index=0)`, a consumer's mirror has `capacity + 1` items; `Remove(index=capacity)` then removes the last item from that mirror, bringing it back to `capacity`. The source's `Count` property reports the END state throughout — querying `coll.Count` between the two events would return `capacity`, NOT `capacity + 1`. This is standard NCC behaviour (same as `ObservableCollection<T>`); WinUI consumers do not re-query `Count` between events in the same operation. **Do NOT try to "fix" the apparent inconsistency by transiently inflating `_count`** — that would require a `capacity + 1`-slot buffer, defeating the ring-buffer point.
- [x] **2.4** Indexed access `this[i]` maps logical index → ring-buffer offset: `_buffer[(_head + i) % capacity]`. Bounds-check `i ∈ [0, _count)`.
- [x] **2.5** `Clear()`: zero the buffer (or just reset `_count` and `_head`), emit a single `NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)`. **This is the only allowed Reset.**
- [x] **2.6** Enumeration yields newest-first: `for (int i = 0; i < _count; i++) yield return this[i];`.
- [x] **2.7** Throw `ArgumentOutOfRangeException` on `capacity <= 0` (constructor) and on out-of-range indexer.

### Task 3 — Author `IdentityKeyedSortedCollection<TIdentity, TItem>` in Core (AC: #5)

- [x] **3.1** Create `src/ohSpy.Core/Collections/IdentityKeyedSortedCollection.cs` implementing this contract [Source: architecture.md §Decision-6]:
  ```csharp
  namespace ohSpy.Core.Collections;

  using System.Collections;
  using System.Collections.Specialized;

  /// <summary>
  /// Sorted observable collection keyed by stable identity. Designed to bind to WinUI
  /// <c>TreeView</c> with FR-054 stable-identity semantics: <see cref="Update(TItem)"/>
  /// with a changed sort key emits a single <c>Move(oldIndex, newIndex)</c> notification,
  /// which the framework reacts to by preserving the migrated row's selection /
  /// expansion / scroll state. <c>Remove+Add</c> as two separate operations would
  /// collapse expanded children and lose visual state — DO NOT do that.
  /// </summary>
  /// <typeparam name="TIdentity">Stable identity key (e.g. <see cref="Guid"/> UUID).</typeparam>
  /// <typeparam name="TItem">Item type; comparator determines sort order.</typeparam>
  public sealed class IdentityKeyedSortedCollection<TIdentity, TItem>
      : IReadOnlyList<TItem>, INotifyCollectionChanged
      where TIdentity : notnull
  {
      public IdentityKeyedSortedCollection(
          Func<TItem, TIdentity> identitySelector,
          IComparer<TItem> sortComparer);

      public int Count { get; }
      public TItem this[int index] { get; }
      public bool TryGetItem(TIdentity id, out TItem item);

      public void Add(TItem item);                       // Emits Add(insertIndex).
      public bool Remove(TIdentity id);                  // Emits Remove(oldIndex); returns false if id absent.

      /// <summary>
      /// If <paramref name="updatedItem"/> sorts to the same position as the existing
      /// item for the same identity: emits NOTHING.
      /// If it sorts to a different position: emits exactly one <c>Move(oldIndex, newIndex)</c>.
      /// NEVER emits <c>Remove+Add</c> on sort-key change.
      /// </summary>
      public void Update(TItem updatedItem);

      public void Clear();                               // Single Reset notification.

      public IEnumerator<TItem> GetEnumerator();         // sort order
      IEnumerator IEnumerable.GetEnumerator();

      public event NotifyCollectionChangedEventHandler? CollectionChanged;
  }
  ```
- [x] **3.2** **Backing store: `List<TItem> _items` + `Dictionary<TIdentity, int> _indexById`.** Use binary search on `_items` (via `IComparer<TItem>`) to find insertion / migration positions.
- [x] **3.3** **`Add(item)`:**
  1. Compute identity = `_identitySelector(item)`. Throw `ArgumentException` if `_indexById.ContainsKey(identity)`.
  2. Binary-search `_items` for insertion index `i`.
  3. `_items.Insert(i, item)`. Update `_indexById[identity] = i`, then shift all entries with index ≥ i+1 up by one in `_indexById`.
  4. Emit `Add(item, index=i)`.
- [x] **3.4** **`Remove(id)`:**
  1. `_indexById.TryGetValue(id, out int oldIndex)` — return `false` if absent.
  2. `var item = _items[oldIndex]; _items.RemoveAt(oldIndex); _indexById.Remove(id);`. Shift all entries with index > oldIndex down by one in `_indexById`.
  3. Emit `Remove(item, index=oldIndex)`. Return `true`.
- [x] **3.5** **`Update(updatedItem)` — the FR-054 critical path. Use the algorithm below verbatim; do NOT invent a "compare neighbours" optimisation (boundary / duplicate-key bugs).**

  **Algorithm:**
  1. `var identity = _identitySelector(updatedItem);`. Throw `KeyNotFoundException` if `!_indexById.ContainsKey(identity)`.
  2. `var oldIndex = _indexById[identity];`.
  3. Remove the old slot from the list AND the index: `_items.RemoveAt(oldIndex); _indexById.Remove(identity);`.
  4. Binary-search `_items` (now without the old item) for the insertion position of `updatedItem` using `_sortComparer`. Call this `newIndex`.
  5. `_items.Insert(newIndex, updatedItem);` and rebuild `_indexById` entries in the affected range `[min(oldIndex, newIndex), max(oldIndex, newIndex)]` by walking that slice of `_items` and reassigning `_indexById[_identitySelector(_items[i])] = i;`. Add the new identity back: `_indexById[identity] = newIndex;`.
  6. **If `newIndex == oldIndex`: emit NOTHING.** (AC-6.3) The sort key did not change the position.
  7. **If `newIndex != oldIndex`: emit ONE notification** `new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, changedItem: updatedItem, index: newIndex, oldIndex: oldIndex)`. (AC-6.4)

  **The temporary `RemoveAt`/`Insert` is implementation detail.** Only ONE `CollectionChanged` event is emitted (or none, if `newIndex == oldIndex`). This is simpler than neighbour-compare and immune to:
  - Boundary bugs (no `_items[oldIndex - 1]` when `oldIndex == 0`).
  - Duplicate-sort-key bugs (when comparator returns 0 for distinct identities — e.g. two devices with the same friendlyName, tiebroken by UUID per architecture line 697).
  - Single-element collection edge case.

  **Worked example** (sortComparer = case-insensitive string compare on `Name`):

  ```
  Pre-state:   _items = [Alpha@0, Bravo@1, Charlie@2]
               _indexById = { alphaId: 0, bravoId: 1, charlieId: 2 }

  Call:        Update(Item(bravoId, "Zulu"))

  Step 1-2:    identity=bravoId, oldIndex=1
  Step 3:      _items = [Alpha@0, Charlie@1]
               _indexById = { alphaId: 0, charlieId: 1 }  (bravoId removed)
  Step 4:      BinarySearch(Item(_, "Zulu")) in [Alpha, Charlie] → newIndex=2
  Step 5:      _items = [Alpha@0, Charlie@1, Zulu@2]
               _indexById = { alphaId: 0, charlieId: 1, bravoId: 2 }
  Step 6/7:    oldIndex=1, newIndex=2 → DIFFERENT
               Emit: Move(changedItem=Zulu, index=2, oldIndex=1)
  ```

  Note that the emitted `Move` carries `updatedItem` (the new instance). AC-6.5's "underlying item instance is preserved" means *identity* preservation (same `TIdentity`), NOT object-reference preservation of the item itself. Callers who want reference preservation pass the SAME object reference they already had (mutated in-place) — the typical pattern is `coll.Update(myExistingDevice)` where the device VM has already been mutated.
- [x] **3.6** **`Clear()`:** `_items.Clear(); _indexById.Clear();`. Emit single `Reset` notification.
- [x] **3.7** Edge cases:
  - Empty collection: `Update` throws; `Remove(id)` returns `false`.
  - `Add` with duplicate identity: throw `ArgumentException` (caller should use `Update` if they intend to replace).
  - Binary search must handle the "remove-then-search" subtlety in `Update` correctly — do not include the about-to-be-removed slot in the search range.

### Task 4 — Author `WinUiDispatcher` impl in App (AC: #2)

- [x] **4.1** Create folder `src/ohSpy.App/Windowing/` (does not yet exist after Story 1.1).
- [x] **4.2** Create `src/ohSpy.App/Windowing/WinUiDispatcher.cs` [Source: architecture.md §Decision-1, lines 165–204]:
  ```csharp
  namespace ohSpy.App.Windowing;

  using Microsoft.UI.Dispatching;
  using ohSpy.Core.Threading;

  /// <summary>
  /// WinUI 3 implementation of <see cref="IUiDispatcher"/>. Captures
  /// <see cref="DispatcherQueue.GetForCurrentThread"/> at construction time —
  /// MUST be constructed on the UI thread, otherwise <c>GetForCurrentThread()</c>
  /// returns null and the dispatcher is unusable.
  /// </summary>
  internal sealed class WinUiDispatcher : IUiDispatcher
  {
      private readonly DispatcherQueue _queue;

      public WinUiDispatcher()
      {
          _queue = DispatcherQueue.GetForCurrentThread()
              ?? throw new InvalidOperationException(
                  "WinUiDispatcher must be constructed on the UI thread. " +
                  "DispatcherQueue.GetForCurrentThread() returned null.");
      }

      public bool IsOnUiThread => _queue.HasThreadAccess;

      public void Post(Action action)
      {
          ArgumentNullException.ThrowIfNull(action);
          var posted = _queue.TryEnqueue(() => action());
          if (!posted)
          {
              throw new InvalidOperationException(
                  "WinUiDispatcher.Post: TryEnqueue returned false. " +
                  "The DispatcherQueue has been shut down.");
          }
      }

      public Task<T> PostAsync<T>(Func<T> readback)
      {
          ArgumentNullException.ThrowIfNull(readback);
          // RunContinuationsAsynchronously is REQUIRED: without it, awaiters of this Task
          // can have their continuations inlined on the UI thread inside SetResult/SetException,
          // which (a) starves the UI message pump and (b) can deadlock if the awaiter is itself
          // running on the UI thread. Do not remove this flag.
          var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
          var posted = _queue.TryEnqueue(() =>
          {
              try { tcs.SetResult(readback()); }
              catch (Exception ex) { tcs.SetException(ex); }
          });
          if (!posted)
          {
              tcs.SetException(new InvalidOperationException(
                  "WinUiDispatcher.PostAsync: TryEnqueue returned false. " +
                  "The DispatcherQueue has been shut down."));
          }
          return tcs.Task;
      }

      public void AssertOnUiThread()
      {
          if (!IsOnUiThread)
          {
              throw new InvalidOperationException(
                  "Operation must run on the UI thread. " +
                  "Marshal via IUiDispatcher.Post / PostAsync.");
          }
      }
  }
  ```
- [x] **4.3** `internal sealed` — outside the App project nothing references this directly; consumers depend on `IUiDispatcher`. `sealed` because subclassing serves no purpose.
- [x] **4.4** **Do NOT add `ConfigureAwait(false)` in WinUiDispatcher** — Pattern 6 omits `ConfigureAwait` in App (UI consumer). The `TaskCompletionSource.Task` here doesn't `await` anything internally, so this is moot; the rule applies to consumers awaiting `PostAsync`.

### Task 5 — Author `InlineUiDispatcher` test fake (AC: #3)

- [x] **5.1** Create folder `tests/ohSpy.Core.Tests/Fakes/` (does not yet exist after Story 1.1).
- [x] **5.2** Create `tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs` [Source: architecture.md §Decision-1]:
  ```csharp
  namespace ohSpy.Core.Tests.Fakes;

  using ohSpy.Core.Threading;

  /// <summary>
  /// Synchronous test double for <see cref="IUiDispatcher"/>. Every operation runs
  /// inline on the calling thread; <see cref="IsOnUiThread"/> always returns true;
  /// <see cref="AssertOnUiThread"/> is a no-op. Use in unit tests that exercise
  /// dispatcher-using code without needing a real WinUI dispatcher.
  /// </summary>
  internal sealed class InlineUiDispatcher : IUiDispatcher
  {
      public bool IsOnUiThread => true;
      public void Post(Action action) => action();
      public Task<T> PostAsync<T>(Func<T> readback) => Task.FromResult(readback());
      public void AssertOnUiThread() { /* no-op for tests */ }
  }
  ```
- [x] **5.3** No tests on `InlineUiDispatcher` itself — it's a test double; testing it is self-referential. It earns its keep when used by other tests (Stories 1.3 onwards).

### Task 6 — Author DI composition root (AC: #7)

- [x] **6.1** Create folder `src/ohSpy.App/Composition/`.
- [x] **6.2** Create `src/ohSpy.App/Composition/ServiceRegistration.cs` [Source: architecture.md §Pattern-7, lines 1811–1837]:
  ```csharp
  namespace ohSpy.App.Composition;

  using Microsoft.Extensions.DependencyInjection;
  using ohSpy.App.Windowing;
  using ohSpy.Core.Threading;

  /// <summary>
  /// Single composition root for the App. Future stories add their service registrations
  /// here. Pattern 7 — singleton default, no per-request scopes.
  /// </summary>
  internal static class ServiceRegistration
  {
      public static IServiceCollection RegisterServices(this IServiceCollection services)
      {
          // Story 1.2 — IUiDispatcher (Decision 1). Must be resolved on the UI thread
          // for its first instantiation so WinUiDispatcher captures DispatcherQueue
          // correctly. See App.OnLaunched for the resolve-and-pin call.
          services.AddSingleton<IUiDispatcher, WinUiDispatcher>();

          return services;
      }
  }
  ```
- [x] **6.3** Add `Microsoft.Extensions.DependencyInjection` `<PackageReference>` to `src/ohSpy.App/ohSpy.App.csproj` (no `Version=` attribute — comes from `Directory.Packages.props` via A3). Verify:
  ```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  ```
  > **Do NOT add a `<PackageVersion>` entry to `Directory.Packages.props`** — `Microsoft.Extensions.DependencyInjection` is already pinned there at version `10.0.0` (verified). Adding a duplicate causes a build error under `CentralPackageTransitivePinningEnabled=true`. The per-project `<PackageReference>` above is the ONLY change needed.

### Task 7 — Wire DI into App startup (AC: #2, #7)

- [x] **7.1** **READ** `src/ohSpy.App/App.xaml.cs` first to see its current state (Story 1.1 left it with the WinUI-template-generated `App` class containing an `App()` constructor, a `protected override void OnLaunched(LaunchActivatedEventArgs)`, and a `private Window? _window;` field). Then **modify in place** — do NOT replace the whole file:

  **Edits required:**

  1. **Add usings** at the top of the file (preserve existing usings; the WinUI template adds several like `Microsoft.UI.Xaml.Controls` and `Windows.ApplicationModel` — leave them, they're harmless even if unused):
     ```csharp
     using Microsoft.Extensions.DependencyInjection;
     using ohSpy.App.Composition;
     using ohSpy.Core.Threading;
     ```

  2. **Add a static property** inside the `App` class, before the constructor:
     ```csharp
     /// <summary>App-wide service provider. Built once during construction.</summary>
     public static IServiceProvider Services { get; private set; } = null!;
     ```

  3. **In the existing `App()` constructor**, AFTER `this.InitializeComponent();`, append:
     ```csharp
     // Compose DI graph. WinUiDispatcher's ctor is deferred until first
     // GetRequiredService<IUiDispatcher>() call in OnLaunched (UI thread).
     Services = new ServiceCollection()
         .RegisterServices()
         .BuildServiceProvider();
     ```

  4. **In the existing `OnLaunched` override**, INSERT this line BEFORE `_window = new MainWindow();` (or whatever the current first line of the body is — the WinUI template's exact form varies slightly across templates):
     ```csharp
     // Force IUiDispatcher construction on the UI thread so WinUiDispatcher
     // captures DispatcherQueue.GetForCurrentThread() correctly. Singleton —
     // subsequent resolves return this same instance.
     _ = Services.GetRequiredService<IUiDispatcher>();
     ```

  5. **Leave the existing `private Window? _window;` field untouched** — do NOT add a duplicate. If you accidentally add a second declaration, the build will fail CS0102 "duplicate definition of '_window'".

  6. **Keep `protected override void OnLaunched(LaunchActivatedEventArgs args)` exactly as it is** — do NOT change the access modifier, parameter type, or override signature. The template's full signature may use the fully-qualified type `Microsoft.UI.Xaml.LaunchActivatedEventArgs args` — either form is fine.

- [x] **7.2** Verify the App still launches (`dotnet build` + `dotnet run --project src/ohSpy.App` if possible, or F5 in VS) showing the empty WinUI window from Story 1.1. If launch fails with an `InvalidOperationException` mentioning "DispatcherQueue.GetForCurrentThread() returned null", the resolve call escaped the UI thread — re-check ordering of `_ = Services.GetRequiredService<IUiDispatcher>();` versus any background work in `OnLaunched`.

- [x] **7.3** Do NOT add a `Microsoft.Extensions.Hosting` generic host. The DI graph is hand-rolled via `ServiceCollection`+`BuildServiceProvider`. The generic host adds lifecycle complexity Story 1.2 doesn't need.

### Task 8 — Unit tests for `BoundedObservableCollection<T>` (AC: #4)

- [x] **8.1** Create folder `tests/ohSpy.Core.Tests/Collections/`.
- [x] **8.2** Create `tests/ohSpy.Core.Tests/Collections/BoundedObservableCollectionTests.cs`. Use xUnit + FluentAssertions. Trait every test that maps to an AC ID with `[Trait("ac", "AC-6.x")]` (Amendment A2 pattern).
- [x] **8.3** Required tests (minimum):
  1. **Constructor**: `new BoundedObservableCollection<int>(0)` throws `ArgumentOutOfRangeException`; same for negative. `new BoundedObservableCollection<int>(10)` succeeds with `Capacity == 10`, `Count == 0`.
  2. **PrependNewest below capacity** `[Trait("ac", "AC-6.1")]`: prepend 3 items into a cap-10 collection; assert 3 `Add(0)` notifications captured in order, no Remove, no Reset, `Count == 3`, `this[0]` is the latest item, `this[2]` is the first.
  3. **PrependNewest at capacity** `[Trait("ac", "AC-6.1")]`: fill cap-3 collection (3 prepends), then prepend a 4th. Assert: 4 total notifications captured (`Add(0)` ×4), plus 1 `Remove(3)` after the 4th Add (so 5 total). Final state: `Count == 3`, `this[0]` = item 4, `this[2]` = item 2. NEVER `Reset`.
     > Verify notification order: for the 4th prepend the events MUST go out as `Add(index=0, item=item4)` THEN `Remove(index=3, item=item1)`. Capture both args; assert their `Action`, `NewStartingIndex`/`OldStartingIndex`, and the items they carry.
  4. **PrependNewest perf** `[Trait("ac", "AC-6.2")]`: 100,000 sequential `PrependNewest(i)` on a cap-10,000 collection. Capture all notifications into a counter that increments per notification (don't store the args — keep it cheap). Assert wall-clock < 1 s (generous; real budget is microseconds per call). Assert `Count == 10,000`. Assert zero `Reset` notifications.
  5. **Clear** `[Trait("ac", "AC-6.6")]`: prepend a handful of items, clear. Assert exactly ONE `Reset` notification. Post-clear `Count == 0`. Subsequent prepend on an empty cleared collection works normally.
  6. **Indexer bounds**: `this[-1]` and `this[Count]` throw `ArgumentOutOfRangeException` (or `IndexOutOfRangeException` — pick one and stick with it).
  7. **Enumeration order**: `coll.ToList()` returns newest-first.

### Task 9 — Unit tests for `IdentityKeyedSortedCollection` (AC: #5)

- [x] **9.1** Create `tests/ohSpy.Core.Tests/Collections/IdentityKeyedSortedCollectionTests.cs`.
- [x] **9.2** Use a test record like `record Item(Guid Id, string Name);` with `identitySelector = x => x.Id` and `sortComparer = Comparer<Item>.Create((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))`.
- [x] **9.3** Required tests (minimum):
  1. **Add** preserves sort order; emits `Add(index)` at the correct insertion position.
  2. **Add** with duplicate identity throws `ArgumentException`.
  3. **Remove(id)** absent identity returns `false`, emits nothing.
  4. **Remove(id)** present identity returns `true`, emits `Remove(oldIndex)`.
  5. **Update with unchanged sort key** `[Trait("ac", "AC-6.3")]`: Add `Item(id, "Bravo")`, then `Update(Item(id, "Bravo"))` (sort-key equivalent). Assert: ZERO notifications emitted after the initial Add. The item at `TryGetItem(id, out var x)` returns the updated instance.
  6. **Update with changed sort key** `[Trait("ac", "AC-6.4")]`: Add three items "Alpha", "Bravo", "Charlie". `Update(Item(bravoId, "Zulu"))`. Assert: exactly ONE notification, `Action == Move`, `OldStartingIndex == 1`, `NewStartingIndex == 2`. NEVER `Remove` then `Add`. Post-state: `["Alpha", "Charlie", "Zulu"]`.
     **Additional assertions on the Move event args** (per BCL NCC contract):
     - `e.NewItems` is non-null, `Count == 1`, and `e.NewItems[0]` references the UPDATED item `Item(bravoId, "Zulu")` (not the old `Item(bravoId, "Bravo")`).
     - `e.OldItems` is non-null, `Count == 1`, and `e.OldItems[0]` references the SAME updated item (`Move` semantics: same item moved, not replaced).
  7. **Identity preserved across Move** `[Trait("ac", "AC-6.5")]`: After the Move from test 6, `TryGetItem(bravoId, out var x)` returns the updated item with the same identity. `x.Name == "Zulu"` confirms the registry maps to the new item, not a stale reference. Iterate the collection and confirm no item appears twice (no orphan entries in `_indexById`).
  8. **Clear** `[Trait("ac", "AC-6.6")]`: Add 3 items, `Clear()`. Exactly ONE `Reset` notification. `Count == 0`.
  9. **Indexer bounds**: same convention as the bounded collection.
  10. **Update on absent identity** throws `KeyNotFoundException`.

- [x] **9.4** **Thread-confinement smoke test for AC-6.** Add a test class `tests/ohSpy.Core.Tests/Threading/UiDispatcherThreadConfinementTests.cs` (create the `Threading/` test folder) that demonstrates the documented pattern. Per architecture line ~701: "thread-confinement (any mutation off-thread throws when used with the dispatcher's `AssertOnUiThread` in the off-thread direction — tested via deliberately off-thread call)."

  Required tests:
  1. **InlineUiDispatcher always reports on-UI-thread** `[Trait("ac", "AC-3")]`: `var d = new InlineUiDispatcher(); d.IsOnUiThread.Should().BeTrue(); d.AssertOnUiThread();` — no exception. (Counterpart to `WinUiDispatcher`'s real behaviour.)
  2. **InlineUiDispatcher.Post executes synchronously** `[Trait("ac", "AC-3")]`: `bool ran = false; d.Post(() => ran = true); ran.Should().BeTrue();` — no async deferral.
  3. **InlineUiDispatcher.PostAsync returns a completed task with the readback's result** `[Trait("ac", "AC-3")]`: `var t = d.PostAsync(() => 42); t.IsCompletedSuccessfully.Should().BeTrue(); t.Result.Should().Be(42);`.
  4. **Documented off-thread enforcement pattern** `[Trait("ac", "AC-6")]`: Construct a custom `IUiDispatcher` test double whose `IsOnUiThread` returns `false` (an `OffThreadDispatcher` test fake — inline class inside the test file is fine). Call `AssertOnUiThread()` on it; assert it throws `InvalidOperationException`. This is the contract the collections rely on: callers MUST invoke `AssertOnUiThread()` at their mutation site, and a dispatcher that knows it's off-thread MUST throw. The collections themselves do not enforce — the test demonstrates the discipline.
     ```csharp
     private sealed class OffThreadDispatcher : IUiDispatcher
     {
         public bool IsOnUiThread => false;
         public void Post(Action action) => throw new NotImplementedException();
         public Task<T> PostAsync<T>(Func<T> readback) => throw new NotImplementedException();
         public void AssertOnUiThread()
         {
             if (!IsOnUiThread)
                 throw new InvalidOperationException(
                     "Operation must run on the UI thread. Marshal via IUiDispatcher.Post / PostAsync.");
         }
     }
     ```

### Task 10 — Delete the placeholder `UnitTest1` (or leave it — your call)

- [x] **10.1** Story 1.1 left `tests/ohSpy.Core.Tests/UnitTest1.cs` (template-generated placeholder) so `dotnet test` had something to discover. Story 1.2 now has 15+ real tests; the placeholder is no longer needed. **Delete `UnitTest1.cs`** — its presence is noise.
- [x] **10.2** Verify `dotnet test` still passes (the real tests cover discovery; deletion of the placeholder is fine).

### Task 11 — Final verification (AC: all)

- [x] **11.1** Run `dotnet build` from the repo root. Must succeed with ZERO warnings (TreatWarningsAsErrors=true).
- [x] **11.2** Run `dotnet test`. All 15+ new tests must pass. `dotnet test` summary line should look like: `Passed!  - Failed: 0, Passed: N, Skipped: 0, Total: N`.
- [x] **11.3** Run `dotnet test --filter "category=chaos"` (the pre-commit hook's filter). Must still match zero tests (chaos tests don't land until Story 1.6). Exit code 0.
- [x] **11.4** Manual smoke: run the App (F5 in Visual Studio, or `dotnet run --project src/ohSpy.App -p:Configuration=Debug`). The empty WinUI window from Story 1.1 must still appear. If you get an `InvalidOperationException` about `DispatcherQueue.GetForCurrentThread() returned null`, you've resolved `IUiDispatcher` off the UI thread — see Task 7.2.
- [ ] **11.5** Make a trivial commit (e.g., a README tweak). The pre-commit hook fires, runs the chaos filter against the new test count, exits 0 trivially. Commit succeeds.

## Dev Notes

### Architectural pillars this story implements

| Architecture decision | What this story delivers | AC tag |
|---|---|---|
| **Decision 1** — `IUiDispatcher` | Interface in Core, `WinUiDispatcher` impl in App, `InlineUiDispatcher` test fake in Tests | AC-1, AC-2, AC-3 |
| **Decision 6** — Identity-tracked observable collection primitives | `BoundedObservableCollection<T>` (ring buffer, never Reset on prepend) + `IdentityKeyedSortedCollection<TIdentity, TItem>` (Move not Remove+Add) | AC-4, AC-5 (AC-6.1..AC-6.6) |
| **Pattern 7** — DI composition root | `ServiceRegistration.RegisterServices` extension + `App.Services` static singleton + UI-thread-pinned `IUiDispatcher` resolve in `OnLaunched` | AC-7 |
| **Pattern 2** — Core ↔ App boundary | `IUiDispatcher` lives in Core (WinUI-free); `WinUiDispatcher` lives in App | (cross-cutting; enforced by NetArchTest in Story 1.6) |
| **Pattern 6** — async discipline | `PostAsync<T>` uses `TaskCompletionSource` with `RunContinuationsAsynchronously`; Core consumers will use `ConfigureAwait(false)` | (referenced) |

### What this story explicitly does NOT do

- **Does NOT implement `LoadingPlaceholderViewModel` or `InlineErrorViewModel`** — Amendment A1's placeholder/error VMs are Epic 2's concern (Story 2.5: Main Window Shell + Device Tree). The architecture extract may suggest those live alongside the collections; for Epic 1 they're explicitly out of scope.
- **Does NOT add NetArchTest rules** pinning the Core ↔ App boundary — that's Story 1.6 (`FakeUpnpDevice + Chaos Test + NetArchTest Rules`). Story 1.2's enforcement is build-time only: `ohSpy.Core.csproj` does not reference `Microsoft.WindowsAppSDK`.
- **Does NOT implement `IWindowOwnershipManager`** — that's Story 2.9. The `src/ohSpy.App/Windowing/` folder is created here but only `WinUiDispatcher.cs` lands in 1.2.
- **Does NOT register `HttpTimeoutOptions`, `IUpnpHttpClient`, `IDiagnosticEmitter`, etc.** in `ServiceRegistration` — those are added by Stories 1.3 / 1.5 as those services come online. Story 1.2's `ServiceRegistration` is intentionally minimal.
- **Does NOT add the `Microsoft.Extensions.Hosting` generic host** — hand-rolled `ServiceCollection`+`BuildServiceProvider` is sufficient and avoids lifecycle complexity Story 1.2 doesn't need.
- **Does NOT implement runtime thread-confinement enforcement on the collections themselves.** Per architecture: "Neither collection is thread-safe. Both are UI-thread-owned. Cross-thread mutations must marshal through `IUiDispatcher.Post` or `PostAsync`." The discipline is *convention*, enforced via `IUiDispatcher.AssertOnUiThread()` calls at the caller's mutation site. The collections do not throw on off-thread access. AC-6's "surfaces the dispatcher-violation contract appropriately" means: the pattern exists (callers can opt into `AssertOnUiThread()`), not that the collections self-police.

### Cross-story dependencies (forward-looking)

| Story | Why it depends on 1.2 |
|---|---|
| 1.3 | `IUpnpHttpClient` consumers will (eventually, in Epic 2) marshal results to VMs via `IUiDispatcher`. Story 1.3 itself doesn't take a hard dep, but the DI registration pattern from 1.2 is the template. |
| 1.5 | `DiagnosticRingSink` owns a `BoundedObservableCollection<DiagnosticRow>(5000)` (FR-041 cap). Marshals diagnostic entries via `IUiDispatcher.Post`. |
| 1.6 | NetArchTest rules pin the Core ↔ App boundary; Story 1.2's correct placement of `IUiDispatcher` in Core vs `WinUiDispatcher` in App is what the rules enforce. |
| 2.3 | `DeviceRegistry` marshals registry mutations via `IUiDispatcher`. |
| 2.5 | `DeviceTreeViewModel.Devices` is `IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>` — FR-054 stable-identity. The `Move` notification IS the FR-054 mechanism. |
| 2.7 | `SsdpLogViewModel.Entries` is `BoundedObservableCollection<SsdpLogEntry>(10_000)` — FR-016, FR-101 virtualised log. |
| 4.3 | `SubscriptionPopupViewModel.Events` is `BoundedObservableCollection<EventNotification>(5_000)` — FR-033 newest-first cap. |
| 5.1 | `DiagnosticsViewModel.Entries` is the same `BoundedObservableCollection<DiagnosticEntry>(5_000)` instance owned by the ring sink (Story 1.5) — AC-8.2 "same instance, no copy". |

**The `Move`-not-`Remove+Add` discipline (AC-6.4) is load-bearing for FR-054 (device tree stable identity).** Any deviation breaks the operator's selection / expansion state when a device's `friendlyName` changes mid-session.

### Story 1.1 learnings worth carrying forward

[Source: `1-1-project-scaffold-build-test-installer-pipeline.md`§Completion Notes + Change Log; recent commits `5173108` / `615ef1d` / `8887259` / `14a177d` / `a55c30d`]

- **All A3 version pins resolved cleanly.** No NuGet drama. `Microsoft.Extensions.DependencyInjection 10.0.0` will pull cleanly from the existing central-management.
- **VSTHRD analyzer is live in all three projects.** Smoke-tested in Story 1.1's Task 10.6. Any `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` in `WinUiDispatcher`, `BoundedObservableCollection`, `IdentityKeyedSortedCollection`, `InlineUiDispatcher`, `ServiceRegistration`, or the new tests will fail the build. **Do not use them.**
- **VSTHRD100 (async void) is exempted in `tests/**`** via `.editorconfig`. Test fixtures may use `async void` patterns if needed (Moq + xUnit). The collection tests for this story probably won't need async-void.
- **CA1806 false positive on `Application.Start(_ => new App())`** is known and suppressed locally in `Program.cs`. If you write any other "construct but don't bind" patterns in `WinUiDispatcher` or `ServiceRegistration`, suppress with a comment explaining why — don't disable CA1806 globally.
- **Namespace convention:** WinUI templates emit `ohSpy_App` (underscore). Story 1.1 renamed every XAML codebehind to `ohSpy.App` (dot). Keep `ohSpy.App.*` namespaces for all new App-side files in Story 1.2.
- **A6/A7/A8 architecture amendments are in place** (commit `14a177d`, applied 2026-06-01). The architecture's D12 csproj snippet now reflects shipped reality (`PlatformTarget=AnyCPU`, `UseWinUI=true`, `StartupObject`, `DISABLE_XAML_GENERATED_MAIN`). The D12 `Program.cs` snippet matches the actual 5-arg bool-returning `Bootstrap.TryInitialize` API. A3 pins `xunit.runner.visualstudio` to `2.8.x` and includes `Microsoft.NET.Test.Sdk`. Story 1.2 inherits all this — no need to revisit.
- **Pre-commit chaos hook fires on every commit** (`Running chaos tests...` line in commit output). Filter matches zero tests today. After Story 1.2 lands, the filter still matches zero tests (Story 1.2 doesn't add `[Trait("category", "chaos")]` tests — those land in Story 1.6).
- **Story 1.1 left `UnitTest1.cs` as a placeholder.** Story 1.2 deletes it (Task 10) once real tests exist.

### Project Structure Notes

**Minimum directories this story must create:**

```
src/ohSpy.Core/
├── Threading/                            ← NEW in 1.2
│   └── IUiDispatcher.cs                  ← Task 1
└── Collections/                          ← NEW in 1.2
    ├── BoundedObservableCollection.cs    ← Task 2
    └── IdentityKeyedSortedCollection.cs  ← Task 3

src/ohSpy.App/
├── Composition/                          ← NEW in 1.2
│   └── ServiceRegistration.cs            ← Task 6
└── Windowing/                            ← NEW in 1.2
    └── WinUiDispatcher.cs                ← Task 4

tests/ohSpy.Core.Tests/
├── Collections/                          ← NEW in 1.2
│   ├── BoundedObservableCollectionTests.cs       ← Task 8
│   └── IdentityKeyedSortedCollectionTests.cs     ← Task 9
└── Fakes/                                ← NEW in 1.2
    └── InlineUiDispatcher.cs             ← Task 5
```

**Files modified:**
- `src/ohSpy.App/ohSpy.App.csproj` — add `Microsoft.Extensions.DependencyInjection` PackageReference (Task 6.3).
- `src/ohSpy.App/App.xaml.cs` — add DI build + UI-thread resolve (Task 7).
- Delete `tests/ohSpy.Core.Tests/UnitTest1.cs` (Task 10).

**Alignment with architecture §Project Structure:** exact match. The architecture's `src/ohSpy.App/Windowing/` lists both `WinUiDispatcher.cs` (Story 1.2) and `WindowOwnershipManager.cs` (Story 2.9); only the former lands here.

### Anti-patterns to avoid

- **Don't put `Microsoft.UI.*` types in Core.** `IUiDispatcher` is `void Post(Action)`, not `void Post(DispatcherQueueHandler)`. The interface is WinUI-naive.
- **Don't add a `Microsoft.WindowsAppSDK` PackageReference to `ohSpy.Core.csproj`.** Pattern 2 boundary.
- **Don't construct `WinUiDispatcher` off the UI thread.** Its constructor calls `DispatcherQueue.GetForCurrentThread()`, which returns `null` from background threads. The story's `App.xaml.cs` resolves `IUiDispatcher` inside `OnLaunched` for exactly this reason. If you push the resolve into a `Task.Run` or background continuation, the dispatcher will fail to construct.
- **Don't use `Remove` + `Add` in `IdentityKeyedSortedCollection.Update` on sort-key change.** Use `Move`. The architecture is explicit. Tests will catch this if you violate AC-6.4, but the code review will also catch it as a Pattern violation.
- **Don't emit `Reset` from `PrependNewest`.** Even at capacity. The architecture says NEVER. Tests will catch this via AC-6.1 / AC-6.2.
- **Don't make the collections `ICollection<T>` or `IList<T>`.** `IReadOnlyList<T>` only. Mutation is via the explicit named methods (`PrependNewest`, `Add`, `Remove`, `Update`, `Clear`) — exposing `Add(T)` via `ICollection<T>` would invite misuse.
- **Don't add `[Obsolete]` shims, deprecation comments, or "removed for X" notes.** Per CLAUDE.md: no backwards-compatibility hacks. These types are new today.
- **Don't add property-changed notifications** to the collections. They emit `CollectionChanged` only. Item-level property-change (for friendlyName updates etc.) is the responsibility of the item type, not the collection.
- **Don't introduce a logger / diagnostic dependency** in `WinUiDispatcher`. Story 1.5 brings the diagnostic emitter online; until then, the `TryEnqueue == false` path throws (it should never happen in practice — the dispatcher is captured at startup and lives for the app's lifetime).
- **Don't use `Microsoft.Extensions.Hosting`.** Hand-rolled `ServiceCollection` + `BuildServiceProvider` is the project's pattern; the generic host adds lifecycle ceremony Story 1.2 doesn't need (no `IHostedService`, no `Stop`/`Start`).

### Testing standards summary

- xUnit + FluentAssertions are pinned via `Directory.Packages.props` (Story 1.1). No new packages needed.
- Every test with an architecture-level AC ID carries `[Trait("ac", "AC-N.M")]` (Amendment A2). Example: `[Fact, Trait("ac", "AC-6.1")]`.
- Tests run via `dotnet test`; chaos-filter tests (Story 1.6) are tagged differently — Story 1.2 adds NO `[Trait("category", "chaos")]` tests.
- The `BoundedObservableCollection` perf test (AC-6.2) uses `System.Diagnostics.Stopwatch` with a generous wall-clock bound (1 s for 100K ops on a 10K-capacity ring) — generous because xUnit runners on CI hosts can be slow; the architectural guarantee is *order of magnitude*, not microsecond precision.
- Notification-capture pattern (used in both AC-6.1 and AC-6.4): subscribe to `CollectionChanged`, append `args` to a `List<NotifyCollectionChangedEventArgs>`, then assert the list's length + each item's `Action`, indices, and items.
  ```csharp
  var notifications = new List<NotifyCollectionChangedEventArgs>();
  collection.CollectionChanged += (_, e) => notifications.Add(e);
  // ... mutate ...
  notifications.Should().HaveCount(2);
  notifications[0].Action.Should().Be(NotifyCollectionChangedAction.Add);
  notifications[0].NewStartingIndex.Should().Be(0);
  notifications[1].Action.Should().Be(NotifyCollectionChangedAction.Remove);
  notifications[1].OldStartingIndex.Should().Be(3);
  ```
- **No mocking of the collections themselves.** They're concrete types with deterministic behaviour; mocking would add zero signal.

### References

> Authoritative paths (for grep / cross-reference):
> - Architecture: `_bmad-output/planning-artifacts/architectures/arch-ohSpy-2026-05-31/architecture.md` (~2800 lines post-A8)
> - Epics: `_bmad-output/planning-artifacts/epics.md` (lines 466–518 for Story 1.2, 408–410 + 350–354 for Epic 1)
> - Story 1.1 completion record: `_bmad-output/implementation-artifacts/1-1-project-scaffold-build-test-installer-pipeline.md`

- [Source: epics.md#Story-1.2] — verbatim ACs (lines 466–518).
- [Source: epics.md#Epic-1] — epic-level FR/NFR coverage map (lines 350–354, 408–410).
- [Source: architecture.md#Decision-1] — `IUiDispatcher` contract + rationale (lines 165–204).
- [Source: architecture.md#Decision-6] — `BoundedObservableCollection<T>` + `IdentityKeyedSortedCollection<TIdentity, TItem>` (lines 622–725).
- [Source: architecture.md#Pattern-7] — DI composition root + lifetime (lines 1811–1837).
- [Source: architecture.md#Pattern-2] — Core ↔ App boundary (lines 1708–1723).
- [Source: architecture.md#Pattern-6] — async discipline (lines 1800–1809).
- [Source: architecture.md#Project-Structure] — full target directory tree (lines 2033–2172).
- [Source: project_ohspy memory] — `Move(old, new)` is the FR-054 mechanism (selection/expansion survival); placeholder atomic-replacement is collection contract, not VM cosmetic.

## Dev Agent Record

### Agent Model Used

claude-opus-4-7[1m] (Anthropic Claude Opus 4.7, 1M context) — via bmad-dev-story skill.

### Debug Log References

- `dotnet build` (full repo, root): clean — 0 warnings, 0 errors, ~7.6 s wall time.
- `dotnet test`: `Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 134 ms - ohSpy.Core.Tests.dll (net10.0)`.
- `dotnet test --filter "category=chaos"`: `No test matches the given testcase filter 'category=chaos'`, exit code 0 (pre-commit hook safe).
- Manual smoke: `dotnet run --project src/ohSpy.App --launch-profile "ohSpy.App (Unpackaged)" -c Debug --no-build` — process launched, `Get-Process ohSpy.App` reported `MainWindowTitle = "ohSpy"` (the empty WinUI window from Story 1.1). No exceptions. Cleanly terminated via `Stop-Process`.

### Completion Notes List

- **Build status:** Clean `dotnet build` from repo root produces zero warnings (TreatWarningsAsErrors=true). All three projects (`ohSpy.Core`, `ohSpy.App`, `ohSpy.Core.Tests`) build green.
- **Test count:** Went from 1 (Story 1.1's `UnitTest1.Test1` placeholder, now deleted) to 25 real tests across three test classes:
  - `BoundedObservableCollectionTests` — 9 tests (constructor validation, AC-6.1 below/at-capacity notifications, AC-6.2 perf, AC-6.6 Clear-emits-Reset, indexer bounds, enumeration order, ring-wrap correctness).
  - `IdentityKeyedSortedCollectionTests` — 11 tests (Add sort-position, Add duplicate-rejection, Remove present/absent, AC-6.3 unchanged-key emits-nothing, AC-6.4 changed-key emits-Move with full `NewItems`/`OldItems` assertions, AC-6.5 identity preserved, AC-6.6 Clear, indexer, Update-absent-throws, head/tail Move boundary).
  - `UiDispatcherThreadConfinementTests` — 5 tests (InlineUiDispatcher's `IsOnUiThread`/Post/PostAsync behaviour, AC-6 off-thread `AssertOnUiThread()` contract via in-test-file `OffThreadDispatcher`).
  Final summary line: `Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25, Duration: 134 ms`.
- **Manual smoke (Task 11.4):** App launched via `dotnet run ... --launch-profile "ohSpy.App (Unpackaged)"`. WinUI window appeared (`MainWindowTitle="ohSpy"` confirmed via `Get-Process`). No `InvalidOperationException` from `WinUiDispatcher` — DI graph built in the `App()` constructor, `IUiDispatcher` first resolved inside `OnLaunched` (UI thread), so `DispatcherQueue.GetForCurrentThread()` returned a valid queue. **NB:** the default launch profile in `Properties/launchSettings.json` is `ohSpy.App (Package)` (MSIX), which fails because the project is configured unpackaged — explicit `--launch-profile "ohSpy.App (Unpackaged)"` is required when launching via `dotnet run`. Visual Studio F5 already routes through the right profile; this only bit the headless CLI smoke test.
- **`IdentityKeyedSortedCollection.Update` algorithm note:** Implemented exactly per Task 3.5's spec — remove-binary-search-insert. Used the `min(oldIndex, newIndex) .. max(oldIndex, newIndex)` slice for the `_indexById` rebuild rather than a full rebuild; this is O(|shift|) and lets the unchanged-key path land in O(log N) (binary search) + O(1) (no shift). The "compare neighbours" optimisation was deliberately NOT attempted, per the spec's warning about boundary / duplicate-sort-key bugs. The worked example in Task 3.5 (`Bravo@1` → `Zulu` ⇒ `Move(1→2)`) is covered verbatim by `Update_ChangedSortKey_EmitsExactlyOneMove`.
- **Pre-commit hook unchanged:** Chaos filter matches zero tests (Story 1.2 added no `[Trait("category", "chaos")]` tests, per spec). The hook will still pass trivially on the first commit after this story lands. Task 11.5 (trivial commit smoke) is intentionally left for the operator / code-review step rather than performed by the dev-story workflow (per Story 1.1 precedent — dev-story does not auto-commit).
- **Deviations from the spec:**
  1. **`.editorconfig` updated** — added `CA1707` (no underscores in member names) and `CA1806` (constructor result discarded) to the existing `[tests/**/*.cs]` block alongside `VSTHRD100`. Reason: `Method_Scenario_Expected` is the standard xUnit naming convention, and `Action act = () => new T(badArg); act.Should().Throw<...>();` is the canonical FluentAssertions pattern for asserting constructor throws. Both rules are project-wide `recommended` analyzers via `AnalysisMode=recommended`; suppressing them in test files only (mirroring VSTHRD100) is the conventional fix and keeps production-code enforcement intact. No architecture amendment is warranted — this is a test-fixture-only concession identical in spirit to the existing VSTHRD100 exemption.
  2. **`InlineUiDispatcher_PostAsyncReturnsCompletedTaskWithReadbackResult` is async** — the spec's literal `t.Result.Should().Be(42)` would trip the `Microsoft.VisualStudio.Threading.Analyzers` VSTHRD002 / VSTHRD110 (synchronous wait on a Task). Rewrote as `int value = await t; value.Should().Be(42);` — semantically equivalent, analyzer-safe, no behaviour change.
  3. **`BoundedObservableCollection.Clear()` emits `Reset` even when already empty** — spec says "emit single Reset notification" without an "if non-empty" qualifier; chose uniform behaviour over a special-case branch. Tests do not exercise the empty-Clear case so this is an unverified design choice; if a future story finds this surprising, it's trivially flippable.

### File List

**Created:**
- `src/ohSpy.Core/Threading/IUiDispatcher.cs`
- `src/ohSpy.Core/Collections/BoundedObservableCollection.cs`
- `src/ohSpy.Core/Collections/IdentityKeyedSortedCollection.cs`
- `src/ohSpy.App/Windowing/WinUiDispatcher.cs`
- `src/ohSpy.App/Composition/ServiceRegistration.cs`
- `tests/ohSpy.Core.Tests/Fakes/InlineUiDispatcher.cs`
- `tests/ohSpy.Core.Tests/Collections/BoundedObservableCollectionTests.cs`
- `tests/ohSpy.Core.Tests/Collections/IdentityKeyedSortedCollectionTests.cs`
- `tests/ohSpy.Core.Tests/Threading/UiDispatcherThreadConfinementTests.cs`

**Modified:**
- `src/ohSpy.App/ohSpy.App.csproj` (added `Microsoft.Extensions.DependencyInjection` PackageReference)
- `src/ohSpy.App/App.xaml.cs` (added DI build + UI-thread `IUiDispatcher` resolve)
- `.editorconfig` (added `CA1707` and `CA1806` to the existing `[tests/**/*.cs]` suppression block)

**Deleted:**
- `tests/ohSpy.Core.Tests/UnitTest1.cs` (template placeholder no longer needed)

## Change Log

- **2026-06-02** — Implementation by dev-story workflow (claude-opus-4-7[1m]). `IUiDispatcher` contract + `BoundedObservableCollection<T>` + `IdentityKeyedSortedCollection<TIdentity, TItem>` + `WinUiDispatcher` impl + `ServiceRegistration` DI root + `App.xaml.cs` wiring + `InlineUiDispatcher` test fake + 25 unit tests. Build clean, all tests pass, App still launches. Status: ready-for-dev → in-progress → review.
- **2026-06-02** — Code review by Sonnet (fresh context, independent of implementing Opus session). Verdict: APPROVED. All 7 ACs verified. Ring-buffer eviction, reindex slice, `RunContinuationsAsynchronously`, Pattern 2 boundary, singleton lifetime, and DI thread-ordering all confirmed correct. Three documented deviations accepted. Two open non-blocking follow-ups noted. Status: review → done.
