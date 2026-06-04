---
baseline_commit: 0c11c8b
---

# Story 3.3: Constrained Inputs — `<allowedValueList>` Dropdown + `<allowedValueRange>` Numeric

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want input arguments whose related state variable declares `<allowedValueList>` to render as a dropdown of exactly those values, and arguments declaring `<allowedValueRange>` on a numeric `<dataType>` to render as a bounded numeric input honouring `<step>`,
So that I can drive constrained actions (e.g. `SetMute true/false`, `SetVolume 0..100 step 1`) without typing the literal value and without submitting an invalid value the device will reject.

---

## ⚠️ READ THIS FIRST — reconcile the epic against shipped reality

The epic for 3.3 was written before Stories 1.4, 3.1 and 3.2 shipped. **Four places diverge from the code you will actually build against.** Internalise them before writing anything.

| # | Epic says | Shipped reality | Your job |
|---|---|---|---|
| 1 | "the VM calls `IScpdParser.ReadStateTableAsync(scpdStreamFromCachedBytes, popupToken)`" (epic L1423) | `IScpdParser.ReadStateTableAsync` **already exists and is fully implemented** (Story 1.4, AC-5.5) — it parses `<allowedValueList>` (empty → empty list), `<allowedValueRange>` (null `<step>` when omitted), `<defaultValue>`, `<dataType>` into `ScpdStateTable.ByName`. **Do NOT build a parser.** Just consume it. | Inject `IScpdParser` into the popup VM; call `ReadStateTableAsync(new MemoryStream(bytes), _popupCts.Token)`. Models are done. |
| 2 | "`scpdStreamFromCachedBytes`… cache the state table on the parent `ServiceDescription` / `ServiceNodeViewModel` so subsequent invocations don't re-parse" (epic L1423, L1425) | The SCPD bytes are **discarded**: `ServiceNodeViewModel.LoadActionsAsync` (L83-133) fetches them via `_http.FetchScpdAsync`, streams actions, and never retains the `byte[]`. `ServiceDescription` is an **immutable record** (can't cache on it). The launcher seam `IInvocationPopupLauncher.Open(action, parentService, parentEntry)` does **not** pass the `ServiceNodeViewModel`, so per-node caching is not reachable from the popup VM without re-plumbing the seam. | **Re-fetch** the SCPD in the popup VM's async init via `_http.FetchScpdAsync(new Uri(parentEntry.LocationUrl, parentService.ScpdUrl), _popupCts.Token)`, exactly the `LoadActionsAsync` idiom. Per-popup re-fetch is **spec-blessed** (arch L597: "Re-parsing on each expansion is acceptable at SCPD sizes (~50 ms)… the `byte[]` is already cheap to retain"). Do NOT re-plumb the launcher to thread the node — flagged as Open Question #3, deferred. |
| 3 | (epic is silent) | **`Inputs` is built SYNCHRONOUSLY in the 3.2 ctor** (`foreach (var arg in action.Inputs) Inputs.Add(new ArgumentInputViewModel(arg))`). A ctor cannot `await` the state-table fetch. | Introduce an **async init path** (`InitializeAsync`) the launcher kicks off **after** construction. The ctor populates `Inputs` with **text-only** placeholders (the 3.2 behaviour — a safe fallback) and sets `IsLoadingInputs = true`; `InitializeAsync` fetches+parses the table, then **rebuilds** `Inputs` with the resolved variants and clears the loading flag. |
| 4 | (epic is silent on threading) | **The Story 3.2 smoke CRASH** (`winui-no-synccontext-marshal-vm`, 2026-06-03): WinUI 3 installs **no SynchronizationContext**, so the continuation after `await FetchScpdAsync(...)` / `await ReadStateTableAsync(...)` resumes on a **thread-pool thread**. Mutating `Inputs` / observable state there pokes `UIElement.Visibility` off-thread → `RPC_E_WRONGTHREAD` → process crash. | **Every** post-await mutation of `Inputs` / `IsLoadingInputs` / `InputsLoadFailed` MUST be marshalled via `_ui.Post(...)` (Decision 1 / `IUiDispatcher`), exactly as `InvokeAsync` already does. Use `ConfigureAwait(false)` on the awaits. **Guard the new tests with `DeferredUiDispatcher`** (not `InlineUiDispatcher`, which masks missing marshalling). See Dev Notes §"THE BIG ONE". |

**This story layers constrained inputs onto Story 3.2's popup.** It touches the Core VM heart (the new `ArgumentInputViewModel` subclasses + the popup VM's async state-table load + variant resolution) and the App XAML (heterogeneous input templates). It reuses the 3.2 `_ui.Post` marshalling discipline, the 3.1 SOAP layer, and the 1.4 parser — **do not reinvent any of them.**

---

## Acceptance Criteria

> ACs are the epic's + PRD FR-102/FR-103, reconciled to shipped reality (see table above). File locations pinned in Dev Notes.

**AC-3.3.1 — Async state-table load + Loading placeholder (epic L1421-1426; FR-044 family)**
1. `InvocationPopupViewModel` gains an async init path (e.g. `public async Task InitializeAsync()`), invoked by the App launcher **after** `new InvocationPopupWindow(vm)`/`Activate()` (fire-and-forget; see AC-3.3.10). The ctor still synchronously fills `Inputs` with text-only `ArgumentInputViewModel`s (the 3.2 fallback) and sets `[ObservableProperty] bool _isLoadingInputs = true` (drives a "Loading…" hint on the input panel; App projects to `Visibility`).
2. `InitializeAsync` resolves the SCPD URL `new Uri(parentEntry.LocationUrl, parentService.ScpdUrl)` (guarded — on `UriFormatException`/null, skip the load, keep text inputs, clear `IsLoadingInputs`), fetches bytes via `_http.FetchScpdAsync(scpdUrl, _popupCts.Token)`, and parses the table via `_scpd.ReadStateTableAsync(new MemoryStream(bytes), _popupCts.Token)` (caller owns the stream — `using var ms`; the parser does not dispose it).
3. On success, `InitializeAsync` **rebuilds** `Inputs`: for each `action.Inputs` arg, resolve its variant (AC-3.3.3/3.3.5/3.3.7) and add it; then set `IsLoadingInputs = false`. **All `Inputs` mutation + flag clear is marshalled via `_ui.Post`** (reconciliation #4).
4. If the SCPD fetch **or** state-table parse fails entirely (`UpnpException` / `UpnpProtocolException` / any non-cancellation exception), **every** input falls back to free-form text (the ctor's text-only `Inputs` stay), a `Warning DiagCategories.ScpdParse` diagnostic is emitted with `DeviceUuid = parentEntry.Uuid`, `Url = scpdUrl`, `ServiceId = parentService.ServiceId`, and `ErrorText`, and `IsLoadingInputs` is cleared (marshalled). `OperationCanceledException` (popup close / device gone) is **swallowed** — no diagnostic, no rebuild (mirror `InvokeAsync` / `LoadActionsAsync`).

**AC-3.3.2 — `AllowedValueListArgumentViewModel` variant (FR-102; epic L1428-1433)**
5. New `src/ohSpy.Core/ViewModels/AllowedValueListArgumentViewModel.cs` — a **sealed subclass of `ArgumentInputViewModel`** (decision: sealed subclass over a polymorphic discriminator property — see Dev Notes §"Variant shape"). Resolved when the arg's `RelatedStateVariable` looks up a state variable whose `AllowedValueList` is non-null **and non-empty**.
6. Exposes `IReadOnlyList<string> AllowedValues` populated in declared order, and `[ObservableProperty] string _selectedValue`. Overrides `public override string ResolvedValue => SelectedValue` (the single seam the popup VM's `SoapArgument` projection reads — `InvokeAsync` is unchanged).
7. **Default pre-population (epic L1435-1438):** if the state variable declares a `<defaultValue>` that **is a member** of `AllowedValues`, `SelectedValue` = that default; otherwise `SelectedValue` = the **first** listed value.

**AC-3.3.3 — Malformed-list fallback (FR-102; epic L1440-1443)**
8. If `<allowedValueList>` is present but **empty** (the parser returns an empty list) or its lookup is otherwise unusable, the arg stays a **free-form text** `ArgumentInputViewModel` (base), and a `Warning DiagCategories.ScpdParse` diagnostic is emitted (`DeviceUuid`, `Url`, `ServiceId`, `ErrorText` describing the malformed list). (A per-arg fallback — the rest of the inputs still resolve normally.)

**AC-3.3.4 — `AllowedValueRangeArgumentViewModel` variant (FR-103; epic L1445-1451)**
9. New `src/ohSpy.Core/ViewModels/AllowedValueRangeArgumentViewModel.cs` — a sealed subclass of `ArgumentInputViewModel`. Resolved when the arg's related state variable has a non-null `AllowedValueRange` **and** a numeric `<dataType>` (`ui1`, `ui2`, `ui4`, `i1`, `i2`, `i4`, `int` — case-insensitive set).
10. Exposes `double Minimum`, `double Maximum`, `double? Step` (from `ScpdAllowedValueRange.Minimum/Maximum/Step`) and `[ObservableProperty] double _numericValue`.
11. Overrides `public override string ResolvedValue => NumericValue.ToString(CultureInfo.InvariantCulture)` (FR-103 — culture-invariant per UPnP spec; **never** the current-culture `ToString()` — a comma decimal separator would corrupt the wire value).

**AC-3.3.5 — Range default pre-population (FR-103; epic L1453-1456)**
12. If `<defaultValue>` parses (invariant) to a number that satisfies the range (`Minimum ≤ d ≤ Maximum`, and on-step where `Step` is declared & > 0), `NumericValue` = that default; otherwise `NumericValue` = `Minimum`.

**AC-3.3.6 — Off-step client-side validation (FR-103; epic L1458-1461)**
13. When `<step>` is declared and > 0, submitting an off-step value (not `Minimum + n·Step` for integer `n ≥ 0`, within a small float epsilon) is **rejected client-side before the SOAP request fires**: either `InvokeAsync.CanExecute` returns false while any range input is off-step/out-of-range, **or** the popup surfaces an inline error and refuses to send. **Decision: an inline per-input validation message + `InvokeAsync` short-circuit** (see Dev Notes §"Off-step validation") — the input exposes `[ObservableProperty] string? _validationError` (null = valid) and the popup VM checks all inputs before building the `SoapRequest`. The message names the constraint, e.g. `"Value must be a multiple of {Step} from {Minimum}"`.

**AC-3.3.7 — Malformed-range fallback (FR-103; epic L1463-1466)**
14. If `<allowedValueRange>` is declared on a **non-numeric** `<dataType>`, **or** `Minimum > Maximum`, **or** `Step` is **≤ 0** (declared and zero/negative), the arg stays **free-form text** (base), and a `Warning DiagCategories.ScpdParse` diagnostic is emitted.

**AC-3.3.8 — List + range both declared (FR-102 last; epic L1468-1471)**
15. If a state variable declares **both** `<allowedValueList>` and `<allowedValueRange>` (malformed per UDA 1.0 §2.3), **FR-102 wins** (resolve the list variant; ignore the range), and a `Warning DiagCategories.ScpdParse` diagnostic is emitted.

**AC-3.3.9 — Neither constraint → free-form text (epic L1473-1475; PRD §7 Non-Goal)**
16. An arg whose related state variable has **neither** `<allowedValueList>` nor `<allowedValueRange>` (or whose `RelatedStateVariable` is not found in the table) stays a **free-form text** `ArgumentInputViewModel` (the 3.2 base). **No** `<dataType>`-driven typed inputs — `boolean`/`dateTime`/`uri` without a list/range stay text (PRD §7 Non-Goal, L707). No diagnostic for the "not found / plain" case (it is not malformed).

**AC-3.3.10 — Heterogeneous XAML rendering (App; FR-102/FR-103 view)**
17. `InvocationPopupWindow.xaml`'s input `ItemsControl` renders a **different control per variant**: a `ComboBox` (bound `ItemsSource={AllowedValues}`, `SelectedItem={SelectedValue, TwoWay}`) for the list variant, a `NumberBox` (`Minimum`, `Maximum`, `SmallChange={Step ?? 1}`, `Value={NumericValue, TwoWay}`) for the range variant, and the existing `TextBox` for the base. **Decision: a `DataTemplateSelector` keyed on the VM runtime subtype** (mirror `NodeDataTemplateSelector`) — see Dev Notes §"XAML variant rendering". A "Loading…" hint shows while `IsLoadingInputs` is true (App-projected `Visibility`). The launcher calls `InitializeAsync` after `Activate()`.
18. Code-behind stays Pattern-13 (constructor-only + `Closed` dispose + `bool/type → Visibility` projections). No business logic in the view.

**AC-3.3.11 — Resolved values flow uniformly through Invoke (epic L1477-1480)**
19. On Invoke, `Inputs.Select(i => new SoapArgument(i.Name, i.ResolvedValue))` is **unchanged** from 3.2 — the polymorphic `ResolvedValue` projects list-selection / invariant-numeric / free-text uniformly into `SoapArgument.Value`. The whole 3.2 invocation flow (Success / Fault / TransportError, diagnostics, cancel, device-gone banner) operates **identically**.

**AC-3.3.12 — Tests + manual smoke (epic L1482-1485)**
20. Core unit tests cover: `AllowedValueListArgumentViewModel` (AllowedValues order, SelectedValue default-in-list, default-not-in-list → first, `ResolvedValue == SelectedValue`); `AllowedValueRangeArgumentViewModel` (Min/Max/Step, default-in-range, default-out-of-range → Min, default-off-step → Min, `ResolvedValue` invariant formatting, off-step validation error set/cleared); and the popup VM's **`InitializeAsync` variant resolution + fallbacks** (list, range, list-empty→text+ScpdParse, range-on-non-numeric→text+ScpdParse, min>max→text+ScpdParse, step≤0→text+ScpdParse, both-declared→list-wins+ScpdParse, neither→text-no-diag, fetch-fails→all-text+ScpdParse, parse-throws→all-text+ScpdParse, OCE→swallowed-no-diag, **state-table-load marshalled through the dispatcher** via `DeferredUiDispatcher`). Every test carries `[Trait("ac", "AC-3.3.x")]` and embeds `FR-102`/`FR-103` in the test name where it maps.
21. The XAML templates + `DataTemplateSelector` are App-only and **cannot** be unit-tested (`CoreAppBoundaryTests` forbids `Core.Tests → App`; no App test project) → **manual UI smoke** (Task 9), REQUIRED before review/done, NOT deferred (Epic 2 retro action E + memory `smoke-per-ui-story`).
22. Gates: `dotnet build` 0 warnings (Core 0/0; App may carry the one pre-existing benign `WMC1506` on `MainWindow.xaml:141`); full suite green (baseline 356 passed / 2 skipped — expect ~+18-24); chaos suite still 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green (no new `DiagCategories` constant — `ScpdParse` already exists, so the reflection-based usage test needs no edit).

---

## Tasks / Subtasks

- [x] **Task 1 — Constrained `ArgumentInputViewModel` subclasses** (AC-3.3.2, .4, .6)
  - [x] Create `src/ohSpy.Core/ViewModels/AllowedValueListArgumentViewModel.cs` — `public sealed partial class : ArgumentInputViewModel`. Ctor `(ScpdArgument arg, IReadOnlyList<string> allowedValues, string? defaultValue)`. Set `AllowedValues` (declared order). Compute `SelectedValue` = (defaultValue ∈ allowedValues) ? defaultValue : allowedValues[0]. `[ObservableProperty] string _selectedValue`. `public override string ResolvedValue => SelectedValue;`. (Caller guarantees `allowedValues` is non-empty — empty falls back to base.)
  - [x] Create `src/ohSpy.Core/ViewModels/AllowedValueRangeArgumentViewModel.cs` — `public sealed partial class : ArgumentInputViewModel`. Ctor `(ScpdArgument arg, double min, double max, double? step, string? defaultValue)`. Store `Minimum/Maximum/Step`. Compute `NumericValue` per AC-3.3.5 (parse default invariant; satisfies range+step → default, else `Minimum`). `[ObservableProperty] double _numericValue`. `public override string ResolvedValue => NumericValue.ToString(CultureInfo.InvariantCulture);` (FR-103). Add `[ObservableProperty] string? _validationError;` + a `partial void OnNumericValueChanged(double v)` (or a public `Validate()`) that sets `ValidationError` when off-step/out-of-range (AC-3.3.6), null when valid. On-step test: `Step is > 0` ⇒ `n = Math.Round((v - Minimum) / Step.Value)`, valid iff `Math.Abs(Minimum + n*Step.Value - v) <= epsilon` AND `v` in `[Minimum, Maximum]`.
  - [x] **Verify the base seam compiles cleanly:** `ArgumentInputViewModel` is already `public partial class` (NOT sealed) with `public virtual string ResolvedValue => Value;` and a `protected`-accessible ctor `(ScpdArgument argument)` that sets `Name`. The subclasses call `: base(arg)`. No base change needed unless the ctor must surface `Name` — it already does (`public string Name { get; }`).

- [x] **Task 2 — Numeric-dataType + variant-resolution helper (Core)** (AC-3.3.2..3.3.9)
  - [x] Add a small pure resolver to `InvocationPopupViewModel` (or a private static helper, or a new internal `ArgumentInputFactory` — engineering judgment; a private static method on the VM is simplest and keeps the diagnostic emit local). Signature concept: given `(ScpdArgument arg, ScpdStateTable table)` → `ArgumentInputViewModel`, plus an out/side-channel for "emit a ScpdParse warning for this arg" so the VM emits with full context. Logic, in order:
    1. `table.ByName.TryGetValue(arg.RelatedStateVariable, out var sv)` — miss → base text (AC-3.3.9, no diag).
    2. `sv.AllowedValueList is { Count: > 0 }` → **list variant** (FR-102 wins even if range also present — AC-3.3.8; if range also present, emit ScpdParse warning). Pass `sv.DefaultValue`.
    3. `sv.AllowedValueList is { Count: 0 }` (present-but-empty) → base text + ScpdParse warning (AC-3.3.3).
    4. `sv.AllowedValueRange is { } r` → require numeric `sv.DataType` AND `r.Minimum <= r.Maximum` AND (`r.Step is null or > 0`); all true → **range variant** (AC-3.3.4); any false → base text + ScpdParse warning (AC-3.3.7).
    5. otherwise → base text (AC-3.3.9, no diag).
  - [x] Numeric-dataType set: `static readonly HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ui1","ui2","ui4","i1","i2","i4","int" }`. (Matches epic L1445. `float`/`r4`/`r8`/`number` are NOT in v1's set — they'd fall to text; keep the set exactly as the epic lists unless you find a fixture that needs more, then document.)

- [x] **Task 3 — Async state-table load in `InvocationPopupViewModel`** (AC-3.3.1)
  - [x] Inject `IScpdParser scpd` into the ctor (8th param, after `IDeviceRegistry registry`). Store `_scpd`. **Update the DI factory + every test construction site** (Task 6/7).
  - [x] Add `[ObservableProperty] bool _isLoadingInputs;` (default false; set `true` in the ctor only if `action.Inputs` is non-empty — an argument-less action has nothing to load, so don't show "Loading…"). Keep the ctor's synchronous text-only `Inputs` population (the fallback).
  - [x] Add `public async Task InitializeAsync()`:
    - if `action.Inputs.Count == 0` → return immediately (nothing to resolve).
    - resolve `scpdUrl = new Uri(parentEntry.LocationUrl, parentService.ScpdUrl)` guarded (try/catch `UriFormatException` → marshal `IsLoadingInputs = false`, return — keep text inputs).
    - `try { var bytes = await _http.FetchScpdAsync(scpdUrl, _popupCts.Token).ConfigureAwait(false); using var ms = new MemoryStream(bytes); var table = await _scpd.ReadStateTableAsync(ms, _popupCts.Token).ConfigureAwait(false); var resolved = action.Inputs.Select(a => ResolveInput(a, table, scpdUrl)).ToList(); _ui.Post(() => { Inputs.Clear(); foreach (var i in resolved) Inputs.Add(i); IsLoadingInputs = false; }); }`
    - `catch (OperationCanceledException) { _ui.Post(() => IsLoadingInputs = false); }` — swallow (no diag).
    - `catch (Exception ex) when (ex is UpnpException or UpnpProtocolException) { EmitScpdParse(scpdUrl, ex.Message); _ui.Post(() => IsLoadingInputs = false); }` — keep the ctor's text inputs (AC-3.3.1 #4). (A broad `catch (Exception ex) when (ex is not OperationCanceledException)` is acceptable too — NFR-R3 defensive; document.)
  - [x] ⚠️ **THREADING (reconciliation #4):** the awaits run the continuation OFF the UI thread (WinUI-3-no-SynchronizationContext). EVERY mutation of `Inputs` / `IsLoadingInputs` MUST go through `_ui.Post`. The `ResolveInput` projection (building VMs, reading the table) is pure/thread-safe and may run off-thread; only the **collection + observable mutation** marshals. `_diag` is thread-safe — the ScpdParse emit may stay off-thread. **Copy the marshalling shape from `InvokeAsync`'s terminal `_ui.Post`.**
  - [x] `EmitScpdParse(Uri url, string msg)` private helper: `_diag.Warning(DiagCategories.ScpdParse, "SCPD state-table load failed", new DiagnosticContext { DeviceUuid = _uuid, Url = url.ToString(), ServiceId = _parentService.ServiceId, ErrorText = msg });` (Pattern 11 — emit structured context, no interpolation into the message). Also call this from `ResolveInput` for the per-arg malformed cases (AC-3.3.3/.7/.8) — those run inside the off-thread projection, which is fine (`_diag` thread-safe).

- [x] **Task 4 — Off-step validation gate on Invoke** (AC-3.3.6)
  - [x] In `InvocationPopupViewModel`, before building the `SoapRequest` in `InvokeAsync` (after the `_controlUrl is null` guard), run `Validate()` on each range input (or re-check `ValidationError`); if any input has a non-null `ValidationError`, short-circuit: set `IsInvoking = false` and **do not** call the SOAP layer. The inline message renders next to the offending input (App binds `ValidationError`). (Keep this synchronous + before the first await so no marshalling is needed for this branch.)
  - [x] Confirm the empty-string / argument-less path is unaffected (no range inputs ⇒ no validation errors ⇒ invoke proceeds).

- [x] **Task 5 — App: heterogeneous input rendering** (AC-3.3.10, .11)
  - [x] Create `src/ohSpy.App/Converters/ArgumentInputTemplateSelector.cs` — `DataTemplateSelector` with `Text`, `List`, `Range` `DataTemplate` properties; `SelectTemplateCore(item)` switches on the runtime type (`AllowedValueListArgumentViewModel` → List, `AllowedValueRangeArgumentViewModel` → Range, else Text). Mirror `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs`.
  - [x] In `InvocationPopupWindow.xaml`: add the selector to `<Grid.Resources>` with three `<DataTemplate>`s (Text = the existing `TextBox`; List = `ComboBox ItemsSource={x:Bind AllowedValues} SelectedItem={x:Bind SelectedValue, Mode=TwoWay}`; Range = `NumberBox Minimum/Maximum/SmallChange/Value={x:Bind NumericValue, Mode=TwoWay}` + an inline error `TextBlock` bound to `ValidationError`). Set the `ItemsControl`'s `ItemTemplateSelector="{StaticResource …}"` and **remove** the single inline `ItemTemplate`. Keep the arg-name column + `IsEnabled={IsInputEnabled}`.
  - [x] Add a "Loading…" hint `TextBlock` (Visibility bound to a new code-behind `LoadingInputsVisibility` projecting `ViewModel.IsLoadingInputs`); raise it in `OnViewModelPropertyChanged` on `IsLoadingInputs`. (Mirror the existing `InvokingVisibility` projection pattern in `InvocationPopupWindow.xaml.cs`.)
  - [x] In `InvocationPopupLauncher.Open`: after `window.Activate()` (and `Adopt`), kick off `_ = vm.InitializeAsync();` (fire-and-forget; all exceptions handled inside `InitializeAsync`; the popup token cancels it on close). Document as the async-init seam.

- [x] **Task 6 — DI + factory wiring** (AC-3.3.1)
  - [x] In `src/ohSpy.App/Composition/ServiceRegistration.cs` (the `InvocationPopupViewModelFactory` registration, L118-126): add `sp.GetRequiredService<IScpdParser>()` as the 8th ctor arg (it is already registered, L66). Update the `InvocationPopupViewModelFactory` delegate signature? **No** — the delegate's *parameters* (`action, parentService, parentEntry`) are unchanged; only the closure's resolved services grow. No launcher/seam change.

- [x] **Task 7 — Update test fakes + existing tests** (AC-3.3.12)
  - [x] Extend `tests/ohSpy.Core.Tests/Fakes/StubScpdParser.cs`: replace `ReadStateTableAsync`'s `throw new NotSupportedException()` with a controllable `StateTable` property (default empty `ScpdStateTable(new Dictionary<string,ScpdStateVariable>())`) + an optional `StateTableThrower` `Func<Exception>?` (to simulate parse failure). Return the canned table.
  - [x] Update **every** `InvocationPopupViewModel` construction site to pass the new `IScpdParser` arg: the DI factory (Task 6) + `InvocationPopupViewModelTests` setup. Grep `new InvocationPopupViewModel(`.
  - [x] `StubUpnpHttpClient` already has `ScpdResponder` (Story 2.6) — reuse it for the SCPD-bytes fetch. Tests set `ScpdResponder = (_, _) => Task.FromResult(<fixture bytes>)`.

- [x] **Task 8 — Core tests for the new VMs + async load** (AC-3.3.12 #20)
  - [x] `tests/ohSpy.Core.Tests/ViewModels/AllowedValueListArgumentViewModelTests.cs` — AllowedValues order; SelectedValue default-in-list; default-not-in-list → first; default-null → first; `ResolvedValue == SelectedValue` after a set. `[Trait("ac","AC-3.3.2")]` / `FR-102` in names.
  - [x] `tests/ohSpy.Core.Tests/ViewModels/AllowedValueRangeArgumentViewModelTests.cs` — Min/Max/Step stored; default-in-range; default-out-of-range → Min; default-off-step → Min; `ResolvedValue` formats invariant (assert a value like `12` → `"12"`, and that a culture with comma-decimal still emits `.` — set `CultureInfo.CurrentCulture` to `de-DE` in the test if feasible, else assert the invariant call directly); off-step set → `ValidationError != null`; on-step set → `ValidationError == null`. `[Trait("ac","AC-3.3.4")]`/`FR-103`.
  - [x] Extend `tests/ohSpy.Core.Tests/ViewModels/InvocationPopupViewModelTests.cs` (the 3.2 file) with an `InitializeAsync` block: drive a `StubScpdParser.StateTable` carrying the `state-table-rich.xml` shapes (Mute boolean list-less, Volume ui4 range step1, Balance i4 range no-step, Mode string list). Assert after `InitializeAsync()`: the right variant per arg; the fallbacks (empty list, min>max, step≤0, range-on-string, both-declared, neither); the fetch-fails / parse-throws → all-text + one `ScpdParse` diag; OCE → swallowed (no diag, inputs unchanged). **Marshalling test (regression guard):** with `DeferredUiDispatcher`, after `await InitializeAsync()` returns, `Inputs` is still the ctor's text-only set and `IsLoadingInputs` is still true **until** `Drain()` — proving the rebuild went through `_ui.Post` (mirror `Invoke_Success_MarshalsTerminalStateThroughDispatcher_NotDirectly_AC327`). `[Trait("ac","AC-3.3.1")]`.
  - [x] Build the test state-table inputs from hand-built `ScpdStateTable` dictionaries (deterministic) for the unit cases; optionally also parse `state-table-rich.xml` through the real parser in one integration-style assertion. Reuse `action.Inputs` with `RelatedStateVariable` names matching the table keys.

- [~] **Task 9 — Manual UI smoke (RUN 2026-06-04 on live Sky network — dropdown PASS; 4 steps deferred for lack of suitable actions)** (AC-3.3.12 #21)
  - [x] **Dropdown (FR-102):** an action with an enumerated `<allowedValueList>` arg rendered as a **ComboBox** of exactly those values, pre-selected; selecting a value and invoking sent the selected value to the device. **PASS.** (Confirms `ArgumentInputTemplateSelector` → list template → `SelectedValue` → `ResolvedValue` → `SoapArgument` end-to-end.)
  - [ ] ~~**Numeric (FR-103)** NumberBox bounded min/max + step~~ — **DEFERRED.** No `<allowedValueRange>` numeric action available on the current Sky network; needs a Linn DS (`SetVolume 0..100 step 1`) reachable via adapter switch (Story 5.2). VM logic unit-tested (`AllowedValueRangeArgumentViewModel` Minimum/Maximum/Step + invariant `ResolvedValue`); unverified part = the App-side `NumberBox` template binding only.
  - [ ] ~~**Off-step rejection (FR-103)** inline error + Invoke refused~~ — **DEFERRED** (needs a range arg, as above). VM logic unit-tested (the synchronous off-step pre-flight gate + `ValidationError`); unverified part = the inline-error `TextBlock` rendering only.
  - [ ] ~~**Fallback-to-text**~~ — **DEFERRED** (no convenient neither-list-nor-range action surfaced on this network). VM logic unit-tested (`ResolveInput` → base `ArgumentInputViewModel`); the TextBox path is the unchanged 3.2 behaviour already smoke-verified in Story 3.2.
  - [ ] ~~**Loading state** "Loading…" → controls~~ — **DEFERRED** (not deliberately observed). VM logic unit-tested (`IsLoadingInputs` true→false marshalled via `_ui.Post`); unverified part = the App-side `LoadingInputsVisibility` projection only.

  > **SMOKE OUTCOME (2026-06-04, live Sky network):** **Dropdown (FR-102) PASS** — the headline new path (selector → ComboBox → SelectedValue → SoapArgument) works end-to-end on a real device. **Steps 2–5 (numeric NumberBox, off-step rejection, fallback-to-text, loading state) DEFERRED** — the Sky IGD exposes no `<allowedValueRange>` numeric action (nor a convenient plain-text-fallback action to exercise here), and the Linn DS devices that have them are on another network reachable only once the Story 5.2 adapter switch lands. Each deferred step's **Core VM logic is unit-tested**; what's unverified is only the App-side template/projection (NumberBox binding, inline-error TextBlock, Loading visibility) — and the fallback TextBox is unchanged 3.2 behaviour already smoke-passed. Honest partial, NOT a silent defer. Recommend revisiting steps 2/3/5 when a Linn-DS network / Story 5.2 adapter switch is available; proceeding to code-review with that caveat on `done`.

- [x] **Task 9 Review Findings** — 0 patch findings, 0 decision-needed findings, 2 deferred.

  ### Review Findings

  - [x] [Review][Defer] `NoInputsVisibility` binding missing `Mode=OneWay` [`InvocationPopupWindow.xaml:136`] — deferred, pre-existing 3.2 pattern not introduced by 3.3
  - [x] [Review][Defer] Manual smoke steps 2-5 deferred (NumberBox, off-step rejection, fallback-to-text, loading state) — requires Linn DS network; FR-102 dropdown smoke PASS; Core VM logic unit-tested; App-side templates verified by inspection

- [x] **Task 10 — Gates** (AC-3.3.12 #22)
  - [x] `dotnet build` 0 warnings (Core 0/0); full suite green; chaos 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green. App `WMC1506` is pre-existing.

---

## Dev Notes

### Files you will CREATE

```
src/ohSpy.Core/ViewModels/AllowedValueListArgumentViewModel.cs   # FR-102 dropdown variant (sealed : ArgumentInputViewModel)
src/ohSpy.Core/ViewModels/AllowedValueRangeArgumentViewModel.cs  # FR-103 numeric variant (sealed : ArgumentInputViewModel)
src/ohSpy.App/Converters/ArgumentInputTemplateSelector.cs        # DataTemplateSelector (mirror NodeDataTemplateSelector)
tests/ohSpy.Core.Tests/ViewModels/AllowedValueListArgumentViewModelTests.cs
tests/ohSpy.Core.Tests/ViewModels/AllowedValueRangeArgumentViewModelTests.cs
```

### Files you will MODIFY (read each before editing)

- **`src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs`** *(3.2 — sealed partial, IDisposable; ctor injects http/ui/diag/registry; builds `Inputs` synchronously in the ctor; `InvokeAsync` already marshals its terminal state via `_ui.Post`)*.
  - *What changes:* inject `IScpdParser scpd` (8th ctor param). Add `[ObservableProperty] bool _isLoadingInputs`. Add `public async Task InitializeAsync()` (fetch SCPD bytes → `ReadStateTableAsync` → rebuild `Inputs` with resolved variants, all marshalled via `_ui.Post`; fetch/parse failure → keep text inputs + `ScpdParse` warning; OCE swallowed). Add the private `ResolveInput(arg, table, url)` variant resolver + `EmitScpdParse(url, msg)`. Add the off-step validation gate at the top of `InvokeAsync` (Task 4).
  - *Preserve:* the ENTIRE 3.2 `InvokeAsync` flow incl. its `_ui.Post` terminal marshalling (reconciliation #4 — do NOT regress the smoke-crash fix), the `_controlUrl` guard, the `_popupCts`/`DeviceToken` link, the `DeviceRemoved` banner + Interlocked `Dispose`, the `Inputs.Select(i => new SoapArgument(i.Name, i.ResolvedValue))` projection (it now picks up the overrides for free — do NOT special-case variants here), the duplicate-`SoapFault`-emit-with-UUID, the `ComputeServiceTail` Title.
- **`src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs`** *(3.2 base — `public partial class` NOT sealed; `string Name`; `[ObservableProperty] string _value=""`; `public virtual string ResolvedValue => Value`; ctor `(ScpdArgument argument)`)*.
  - *What changes:* **likely none.** It was deliberately shaped in 3.2 as the polymorphic base. Confirm the ctor is reachable by subclasses (it's `public`; fine). If you want `Value` settable from a subclass it already is (`[ObservableProperty]` generates a public setter). Only touch it if a subclass needs a `protected` member it lacks — it shouldn't.
  - *Preserve:* `ResolvedValue` virtual seam; non-sealed; the XML-doc that names the 3.3 subclasses.
- **`src/ohSpy.App/Views/InvocationPopupWindow.xaml`** *(3.2 — `ItemsControl` over `Inputs` with a single `<DataTemplate x:DataType="vm:ArgumentInputViewModel">` = a `TextBox`)*.
  - *What changes:* replace the single `ItemTemplate` with an `ItemTemplateSelector` + three templates (Text/List/Range). Add the "Loading…" hint. Add the `xmlns:conv` for the selector + the selector as a keyed resource.
  - *Preserve:* the arg-name column (200px + Wrap — a smoke fix from 3.2), `IsEnabled={IsInputEnabled}`, the device-gone banner, the result area (Success/Fault/Transport borders), the Invoke button + ProgressRing.
- **`src/ohSpy.App/Views/InvocationPopupWindow.xaml.cs`** *(3.2 — code-behind Visibility/bool projections; `OnViewModelPropertyChanged` raises them; `Closed → ViewModel.Dispose()`)*.
  - *What changes:* add `LoadingInputsVisibility => ToVisibility(ViewModel.IsLoadingInputs)` + raise it on the `IsLoadingInputs` case in `OnViewModelPropertyChanged`.
  - *Preserve:* the existing projections (Invoking/Banner/Result/Success/Fault/Transport/IsInputEnabled), the Pattern-13 constructor-only shape, the `Closed` dispose.
- **`src/ohSpy.App/Windowing/InvocationPopupLauncher.cs`** *(3.2 — `Open(action, parentService, parentEntry)` = factory → window → Activate → Adopt)*.
  - *What changes:* after `Activate()`/`Adopt(...)`, add `_ = vm.InitializeAsync();` (fire-and-forget async init). Keep a local `var vm = _vmFactory(...)` reference (already there).
  - *Preserve:* the Activate-THEN-Adopt order (D10), the `ShellWindow` null guard.
- **`src/ohSpy.App/Composition/ServiceRegistration.cs`** *(L118-126 — the `InvocationPopupViewModelFactory` closure)*.
  - *What changes:* add `sp.GetRequiredService<IScpdParser>()` as the 8th ctor arg. `IScpdParser` is already registered (L66).
  - *Preserve:* the dual-reg launcher + ShellWindow injection; registration order before `NodeServices`.
- **Tests:** `StubScpdParser` (make `ReadStateTableAsync` controllable), `InvocationPopupViewModelTests` (new 8th ctor arg + the `InitializeAsync` block). Grep `new InvocationPopupViewModel(`.

### THE BIG ONE — async Inputs load + UI-thread marshalling (reconciliation #3 + #4)

**The crash you must not re-introduce.** Story 3.2's first live smoke crashed the app: `COMException 0x8001010E (RPC_E_WRONGTHREAD)`. WinUI 3 installs **no `SynchronizationContext`**, so `await someHttpCall` resumes its continuation on a **thread-pool thread**, NOT the UI thread — even with `ConfigureAwait(true)`. Setting an `[ObservableProperty]` (or mutating a bound `ObservableCollection`) there raises `PropertyChanged`/`CollectionChanged` off-thread → the bound `Window` pokes `UIElement.Visibility` off-thread → unhandled COM exception → process exit. The fix (durable memory `winui-no-synccontext-marshal-vm`) is: **marshal every post-await observable mutation through `_ui.Post(...)`** (Decision 1 / `IUiDispatcher`).

Story 3.3 adds a **second** async path (`InitializeAsync` — fetch SCPD + parse state table + rebuild `Inputs`). It has the **exact same hazard**. Mandatory shape:

```csharp
public async Task InitializeAsync()
{
    if (_action.Inputs.Count == 0) return;          // nothing to resolve
    Uri scpdUrl;
    try { scpdUrl = new Uri(_parentEntry.LocationUrl, _parentService.ScpdUrl); }
    catch (UriFormatException) { _ui.Post(() => IsLoadingInputs = false); return; }

    try
    {
        var bytes = await _http.FetchScpdAsync(scpdUrl, _popupCts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream(bytes);     // caller owns stream — parser doesn't dispose
        var table = await _scpd.ReadStateTableAsync(ms, _popupCts.Token).ConfigureAwait(false);
        var resolved = _action.Inputs.Select(a => ResolveInput(a, table, scpdUrl)).ToList(); // pure; off-thread OK
        _ui.Post(() =>                              // ⚠️ marshal the COLLECTION + flag mutation
        {
            Inputs.Clear();
            foreach (var i in resolved) Inputs.Add(i);
            IsLoadingInputs = false;
        });
    }
    catch (OperationCanceledException) { _ui.Post(() => IsLoadingInputs = false); } // swallow, no diag
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        EmitScpdParse(scpdUrl, ex.Message);          // _diag is thread-safe — emit off-thread OK
        _ui.Post(() => IsLoadingInputs = false);     // keep the ctor's text inputs (defensive fallback)
    }
}
```

- **Why a separate `InitializeAsync` (not the ctor):** a constructor cannot `await`. The 3.2 ctor builds text-only `Inputs` synchronously — that stays as the **fallback** and the initial render. `InitializeAsync` (kicked off by the launcher after `Activate()`) upgrades them. If init fails, the text inputs are already correct (defensive — AC-3.3.1 #4).
- **`ResolveInput` is pure** (reads the table, news up VMs) → safe off-thread. Only the `Inputs` collection mutation + `IsLoadingInputs` set marshal. `_diag.Warning(...)` is thread-safe (the 3.2 client + `ServiceNodeViewModel` already emit off-thread) — leave the ScpdParse emit off-thread.
- **Tests MUST use `DeferredUiDispatcher`** (`tests/.../Fakes/DeferredUiDispatcher.cs`) for the marshalling regression test — `InlineUiDispatcher` runs `Post` inline and **masks** missing marshalling (the exact reason 3.2's crash slipped past CI). Assert: after `await InitializeAsync()`, `Inputs` is unchanged + `IsLoadingInputs == true` UNTIL `dispatcher.Drain()`. (This proves the rebuild went through `Post`.)
- **Cancellation:** `_popupCts.Token` flows into both awaits. Closing the popup mid-load cancels → `OperationCanceledException` → swallowed. `_popupCts` is disposed in `Dispose()` (3.2) — `InitializeAsync` must tolerate the token being cancelled (it does; OCE caught).

### Where the SCPD bytes come from (reconciliation #2 — the discarded-bytes trap)

`ServiceNodeViewModel.LoadActionsAsync` (L83-133) fetches the SCPD `byte[]` via `_services.Http.FetchScpdAsync(new Uri(_deviceLocation, _service.ScpdUrl), _deviceToken)`, streams actions into the tree, and **throws the bytes away**. `ServiceDescription` is an immutable `record` (no place to cache). The `IInvocationPopupLauncher.Open(action, parentService, parentEntry)` seam does NOT carry the `ServiceNodeViewModel`, so the popup VM **cannot** reach a per-node cache without re-plumbing the seam (and the entry/service it does carry are immutable).

**Decision: the popup VM re-fetches the SCPD in `InitializeAsync`** via `_http.FetchScpdAsync(new Uri(parentEntry.LocationUrl, parentService.ScpdUrl), _popupCts.Token)` — the verbatim `LoadActionsAsync` URL-resolution idiom. This is **spec-blessed**: arch L597 says "Re-parsing on each expansion (vs caching) is acceptable at SCPD sizes (~50 ms even for large documents)… the `byte[]` is already cheap to retain. If expand-collapse-expand performance suffers, cache `ScpdStateTable` per service." A popup open is rarer than a tree expand, so a per-open re-fetch is well within budget and avoids re-plumbing the seam. **Caching the table on `ServiceNodeViewModel` is Open Question #3 (deferred) — do NOT do it in this story** unless the smoke reveals a latency problem.

### `IScpdParser.ReadStateTableAsync` is DONE — do not build a parser (reconciliation #1)

`src/ohSpy.Core/Scpd/XmlReaderScpdParser.cs` (L73-99 + helpers L209-322) already implements `ReadStateTableAsync` to spec (Story 1.4, AC-5.5). It returns `ScpdStateTable(IReadOnlyDictionary<string, ScpdStateVariable> ByName)` with O(1) name lookup. Per-variable, it already:
- parses `<allowedValueList>` → `IReadOnlyList<string>?` in declared order; **empty list returns an empty list** (not null) — that's your AC-3.3.3 "present-but-empty" signal (`{ Count: 0 }`).
- parses `<allowedValueRange>` → `ScpdAllowedValueRange(double Minimum, double Maximum, double? Step)`; **`Step` is null when `<step>` omitted** (AC-5.5); `<minimum>`/`<maximum>` parsed invariant; **throws `UpnpProtocolException` if min/max missing** (caught by your fetch/parse try → all-text + ScpdParse).
- parses `<dataType>` (required) + `<defaultValue>` (optional `string?`).

Reuses the XXE-locked `UpnpXmlReaderSettings` (DtdProcessing.Prohibit, XmlResolver null, 4M-char cap). The model records (`ScpdStateTable`, `ScpdStateVariable`, `ScpdAllowedValueRange`, `ScpdArgument` with `RelatedStateVariable`, `ScpdDirection`) are all final — no model work. **Your job is consumer-side resolution only.**

The arg → state-variable link is `ScpdArgument.RelatedStateVariable` → `table.ByName[relatedName]`. Note `ByName` uses `StringComparer.Ordinal` (case-sensitive) — UPnP state-variable names are case-sensitive, so this is correct; a name mismatch is a legitimate "not found → text" (AC-3.3.9).

### Variant shape — sealed subclass (decision; epic L1430 left it open)

The epic offers "sealed subclass of `ArgumentInputViewModel` OR a polymorphic property — pick the cleaner shape." **Decision: sealed subclasses** (`AllowedValueListArgumentViewModel`, `AllowedValueRangeArgumentViewModel`). Rationale:
- The 3.2 base was **deliberately shaped for this** — `public partial class` (not sealed), `public virtual string ResolvedValue => Value`. Subclassing is the path of least resistance and the documented intent (read the `ArgumentInputViewModel` XML doc — it names both subclasses).
- The App `DataTemplateSelector` switches on **runtime type** — distinct subclasses give it a clean `is` check (a single class with a `kind` enum would force the selector to read a property, uglier).
- Each variant carries variant-specific observable state (`SelectedValue` vs `NumericValue` + `ValidationError`) — separate classes keep those from leaking into the base.

`ResolvedValue` is the only seam the popup VM reads — the override projects each variant's selection to the wire string, so `InvokeAsync`'s `Inputs.Select(i => new SoapArgument(i.Name, i.ResolvedValue))` is **unchanged** (AC-3.3.11).

### Off-step validation (decision; epic L1458-1461 / FR-103 offers two options)

FR-103 allows either "`CanExecute` returns false" OR "inline error + refuse to send." **Decision: inline per-input `ValidationError` + a synchronous pre-flight check in `InvokeAsync`** (before the first await, so no marshalling). Rationale: a `CanExecute` that depends on N child VMs' validity needs `NotifyCanExecuteChangedFor` wiring across every range input's `NumericValue` change — fragile and verbose. A pre-flight check (`if (Inputs.OfType<AllowedValueRangeArgumentViewModel>().Any(r => r.ValidationError is not null)) { IsInvoking = false; return; }`) is simpler, keeps the error visible per-input, and matches the "inline message" half of the AC. The `NumberBox` also enforces Min/Max at the control level (App), so off-range is double-guarded; off-step is the VM's job (NumberBox has no native step-validation, only `SmallChange` for the spinner).

On-step test (epsilon for float): `Step is > 0` ⇒ `n = Math.Round((value - Minimum) / Step.Value)`; valid iff `n >= 0` AND `Math.Abs(Minimum + n * Step.Value - value) <= 1e-9 * Math.Max(1, Math.Abs(value))` AND `Minimum <= value <= Maximum`. Keep the epsilon generous — these are `double`s parsed from XML.

### XAML variant rendering (decision; reconciliation point with 3.2)

Story 3.2 chose **code-behind `Visibility` projections** for the *result* area (Success/Fault/Transport) — three mutually-exclusive panels toggled by the `Result` runtime type. That works for a fixed small set of singleton panels. **For the heterogeneous `Inputs` LIST, use a `DataTemplateSelector`** (mirror `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs`, which already does exactly this for the device/service/action tree rows). Rationale: an `ItemsControl` renders one template **per item**, and the variant differs per row — a selector is the idiomatic WinUI mechanism; a per-row Visibility hack (three controls per row, two collapsed) would be wasteful and ugly. This is a **deliberate divergence from 3.2's result-area choice**, justified by "list of heterogeneous items" vs "one-of-three singleton panels." Document the selector with that rationale. Keep `Visibility` out of Core (the selector lives in App; the VMs carry no `Visibility`).

`NumberBox` is the WinUI 3 native numeric control (`Minimum`, `Maximum`, `SmallChange`, `Value`, `SpinButtonPlacementMode="Inline"`). `ComboBox` for the list (`ItemsSource`/`SelectedItem`). Both are App-only, smoke-covered.

### Diagnostics

`DiagCategories.ScpdParse` (= `"Scpd.Parse"`) **already exists** (`src/ohSpy.Core/Diagnostics/DiagCategories.cs:46`) and is already used by `ServiceNodeViewModel.EmitFailure` — so **no new constant**, and `DiagCategoriesUsageTests` (reflection-based pinned-set) needs no edit. Emit Pattern-11 style (match `ServiceNodeViewModel.EmitFailure` / `InvokeAsync`): structured `DiagnosticContext { DeviceUuid = _uuid, Url, ServiceId = _parentService.ServiceId, ErrorText }`, **never** interpolate context into the message string. The "neither constraint / related var not found" case (AC-3.3.9) is **not** an error → **no** diagnostic.

### Test boundary (Core vs App)

- **Automated (Core.Tests):** `AllowedValueListArgumentViewModel`, `AllowedValueRangeArgumentViewModel`, and `InvocationPopupViewModel.InitializeAsync` (variant resolution + every fallback + the **marshalling regression** via `DeferredUiDispatcher`). This is the whole testable surface. Fakes: `StubUpnpHttpClient` (`ScpdResponder` for the SCPD bytes), `StubScpdParser` (make `ReadStateTableAsync` controllable — currently throws `NotSupportedException`), `InlineUiDispatcher` (most tests), `DeferredUiDispatcher` (the marshalling guard), `CapturingDiagnosticEmitter`, `FakeDeviceRegistry`.
- **App-only, NOT unit-testable** (`CoreAppBoundaryTests` forbids `Core.Tests → App`): `ArgumentInputTemplateSelector`, the ComboBox/NumberBox templates, the "Loading…" hint, the launcher's `InitializeAsync` kick-off. These are **manual UI smoke** (Task 9).
- **Epic 2 lesson (memory `smoke-per-ui-story` + retro action E):** this story ships real WinUI input controls (ComboBox/NumberBox) → **Task 9 manual smoke is a first-class gate, run before review/done, NOT batched to Epic 3 close.** When asserting in Core tests, assert on **what the resolution produced** (the variant type, `AllowedValues`, `Minimum/Step`, `ResolvedValue`) and the **request that goes out** — not on inputs you handed in (the Epic 2 "prove it's wired" lesson).

### Previous-story intelligence (carries over)

- **THE 3.2 SMOKE CRASH (`winui-no-synccontext-marshal-vm`)** — see §"THE BIG ONE". This story's `InitializeAsync` is the next async popup-VM path; the same `_ui.Post` discipline + `DeferredUiDispatcher` guard apply. The memory note explicitly flags 3.3/4.3/5.x as future repeat-risk.
- **Sealed-record / sealed-subclass data carriers** for the result + input variants; **`new`-constructed VMs threaded via `NodeServices`** for tree nodes; **Pattern-7 factory + dual-reg launcher** for the popup (unchanged here — only the factory closure grows an `IScpdParser`).
- **`IDisposable` + Interlocked dispose + registry unsubscribe** — the 3.2 VM already has this; do not regress it. `InitializeAsync` must tolerate `_popupCts` cancel/dispose on close (OCE swallowed).
- **`ConfigureAwait(false)` on awaits + `_ui.Post` on the continuation's observable mutations** — the 3.2 `InvokeAsync` is the exact template; copy its shape into `InitializeAsync`.
- **Argument-name column** in `InvocationPopupWindow.xaml` is 200px + `TextWrapping="Wrap"` (a 3.2 smoke fix) — preserve it across the template-selector rework.

### Git intelligence

`0c11c8b` (Story 3.2, done) shipped the popup VM + window + launcher + the `ArgumentInputViewModel` text-only base this story extends, **including** the `_ui.Post` smoke-crash fix and the `DeferredUiDispatcher` fake. `dfa5b81` (Story 3.1) shipped the SOAP layer (unchanged here). `IScpdParser.ReadStateTableAsync` shipped back in Epic 1 (`1-4-...`, commit in Epic 1 close). Branch `main`; this is the **last** Epic 3 story — after it, `epic-3-retrospective` is available. No merge hazard: 3.3 extends 3.2's seams, doesn't reshape them.

### References

- Story 3.3 ACs (epic): `_bmad-output/planning-artifacts/epics.md:1413-1485`
- PRD FR-102 (allowedValueList): `prds/prd-ohSpy-2026-05-30/prd.md:418-427`; FR-103 (allowedValueRange): `:429-438`; §7 Non-Goal (no dataType-driven typed inputs): `:707`, `:774`
- Story 3.2 (handoff — the popup this extends; the SMOKE CRASH lesson): `_bmad-output/implementation-artifacts/3-2-invocation-popup-with-free-form-text-inputs.md` (esp. Completion Notes §"Post-await UI marshalling" L347, and the marshalling regression test)
- Story 3.1 (the SOAP layer Invoke consumes, unchanged): `_bmad-output/implementation-artifacts/3-1-soap-envelope-builder-fault-parser-and-invokeactionasync-wire-up.md`
- The 3.2 popup VM to modify (InvokeAsync `_ui.Post` template; ctor `Inputs` build): `src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs` (ctor L53-92, InvokeAsync L98-217, terminal marshal L211-216)
- The text-only base to subclass: `src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs`
- The parser (ReadStateTableAsync — DONE): `src/ohSpy.Core/Scpd/IScpdParser.cs:41`, `XmlReaderScpdParser.cs:73-99,209-322`
- Models (final): `src/ohSpy.Core/Models/ScpdStateTable.cs`, `ScpdStateVariable.cs`, `ScpdAllowedValueRange.cs`, `ScpdArgument.cs`, `ScpdDirection.cs`
- SCPD bytes idiom to mirror (fetch + URL resolve; bytes discarded): `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs:83-133`
- DI factory to grow: `src/ohSpy.App/Composition/ServiceRegistration.cs:118-129` (add `IScpdParser`, already registered L66)
- Launcher to add `InitializeAsync` kick-off: `src/ohSpy.App/Windowing/InvocationPopupLauncher.cs:38-45`
- App window XAML (input ItemsControl + projections to extend): `src/ohSpy.App/Views/InvocationPopupWindow.xaml:73-86`, `.xaml.cs:37-91`
- DataTemplateSelector to mirror: `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs`
- DiagCategories.ScpdParse (already exists — no new constant): `src/ohSpy.Core/Diagnostics/DiagCategories.cs:46`
- Decision 5 (SCPD parsing; ReadStateTableAsync rationale; re-parse-is-cheap): `architecture.md:511-620` (esp. L597, L612 AC-5.5)
- Decision 1 / IUiDispatcher (the marshalling seam): `architecture.md` (D1); impl `src/ohSpy.Core/Threading/IUiDispatcher.cs`
- Fakes to extend/use: `tests/ohSpy.Core.Tests/Fakes/StubScpdParser.cs` (make ReadStateTableAsync controllable), `StubUpnpHttpClient.cs` (ScpdResponder), `DeferredUiDispatcher.cs` (marshalling guard), `InlineUiDispatcher.cs`, `CapturingDiagnosticEmitter.cs`, `FakeDeviceRegistry.cs`
- Existing state-table fixture + parser tests to mirror: `tests/ohSpy.Core.Tests/Fixtures/Scpds/state-table-rich.xml`, `tests/ohSpy.Core.Tests/Scpd/XmlReaderScpdParserTests.cs:179-301`
- Memory: `winui-no-synccontext-marshal-vm` (the 3.2 crash — applies here), `smoke-per-ui-story` (manual smoke per UI story before review/done)

### Project structure notes

- New Core VMs land in `src/ohSpy.Core/ViewModels/` (matches arch L2136 — `InvocationPopupViewModel.cs / ArgumentInputViewModel.cs # FR-025, FR-026, FR-102, FR-103`). The selector lands in `src/ohSpy.App/Converters/` next to `NodeDataTemplateSelector`. No structural variance.
- `CoreAppBoundaryTests` respected: the new VMs are WinUI-free (no `Visibility`, no `NumberBox`); the heterogeneous rendering is the App `DataTemplateSelector`. `CultureInfo.InvariantCulture` (FR-103) is BCL, Core-safe.

### Open questions for the implementer (flagged, non-blocking)

1. **`ResolveInput` location:** a private static method on `InvocationPopupViewModel` (simplest; keeps the ScpdParse emit local via a captured `_diag`) vs a new internal `ArgumentInputFactory` (more testable in isolation but adds a type + needs the emitter injected). Recommendation: **private method on the VM** — the variant resolution is only ever needed by the popup, and the popup-VM tests cover it end-to-end through `InitializeAsync`. Document whichever.
2. **Off-step UX:** inline `ValidationError` + Invoke short-circuit (baked-in decision) vs `CanExecute=false`. Either satisfies AC-3.3.6; the inline path is recommended (simpler, per-input message). If `CanExecute` is preferred, wire `NotifyCanExecuteChangedFor(nameof(InvokeCommand))` from each range input's `NumericValue` change up to the VM — more plumbing.
3. **Per-service state-table caching (DEFERRED):** the epic suggested caching the table on `ServiceNodeViewModel`. This story re-fetches per popup-open (spec-blessed, arch L597). Caching would require threading the `ServiceNodeViewModel` (or a cache) through the `IInvocationPopupLauncher` seam — out of scope here. Revisit only if smoke shows popup-open latency exceeds the §6 interaction budget on a large SCPD.
4. **Numeric dataType set:** the epic lists `ui1,ui2,ui4,i1,i2,i4,int`. `float`/`r4`/`r8`/`number`/`fixed.14.4` are NOT included → they fall to free-form text (still bounded by the operator's typing). Keep the set as the epic lists it; widen only if a real device fixture needs it, and document. (PRD §7 keeps v1 deliberately narrow.)

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.8 (1M context) — `claude-opus-4-8[1m]` — via the `bmad-dev-story` workflow.

### Debug Log References

- Build (Core): `dotnet build src/ohSpy.Core` → **0 warnings / 0 errors**.
- Build (solution): `dotnet build ohSpy.sln` → **1 warning / 0 errors** — the single pre-existing benign `WMC1506` on `MainWindow.xaml:141` (no new warnings introduced).
- Tests (full): `dotnet test ohSpy.sln` → **396 passed / 2 skipped / 0 failed** (baseline was 356/2 → **+40 Core tests**, within/above the ~+18-24 estimate). Chaos suite (`UpnpHttpClientChaosTests`) unchanged. `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green (7 passed / 2 skipped in that filtered slice).
- Two transient self-inflicted failures fixed mid-run: (a) CA1305 on a culture-sensitive `double.ToString()` inside a *diagnostic message* interpolation → switched to `FormattableString.Invariant` + `CultureInfo.InvariantCulture`; (b) CA1861 (constant-array-argument, error-level here) on inline `new[] {…}` test args → added `params` `L(...)` helpers; (c) a test typo passing a `because` string as a 5th `Equal(...)` element.

### Completion Notes List

**What shipped (automated portion complete):**
- New sealed Core VMs `AllowedValueListArgumentViewModel` (FR-102; `AllowedValues` + `SelectedValue`, `ResolvedValue => SelectedValue`, default-in-list-else-first) and `AllowedValueRangeArgumentViewModel` (FR-103; `Minimum`/`Maximum`/`Step`, `NumericValue`, `ValidationError`, `ResolvedValue` via `CultureInfo.InvariantCulture`, on-step/in-range validation with a float epsilon). Added a non-observable `StepOrOne => Step ?? 1` helper for the NumberBox `SmallChange` bind (a `double?` won't coerce).
- `InvocationPopupViewModel` gained the 8th ctor arg `IScpdParser`, `[ObservableProperty] bool _isLoadingInputs` (true only when the action has inputs), `InitializeAsync()` (fetch SCPD bytes via `_http.FetchScpdAsync(new Uri(LocationUrl, ScpdUrl), token)` → `_scpd.ReadStateTableAsync(ms, token)` → rebuild `Inputs` with resolved variants), the private pure `ResolveInput(arg, table, url)` resolver, and `EmitScpdParse(url, msg)` (Pattern-11 structured `ScpdParse` warning — no new `DiagCategories` constant). Added the off-step pre-flight gate at the top of `InvokeAsync` (synchronous, before the first await).
- App: `ArgumentInputTemplateSelector` (runtime-type `DataTemplateSelector`, mirrors `NodeDataTemplateSelector`); the popup XAML now renders ComboBox / NumberBox / TextBox per variant via `ItemTemplateSelector`, plus a "Loading…" hint (`ProgressRing` + text) bound to a new `LoadingInputsVisibility` code-behind projection; launcher kicks off `_ = vm.InitializeAsync()` after Activate/Adopt; DI factory closure resolves `IScpdParser`.

**⚠️ UI-thread marshalling (the #1 hazard — the Story 3.2 smoke-crash class):** every post-`await` mutation in `InitializeAsync` (the `Inputs.Clear()`+repopulate and the `IsLoadingInputs = false` clear) is marshalled through `_ui.Post(...)`, with `ConfigureAwait(false)` on both awaits, copying `InvokeAsync`'s terminal-marshal shape. The pure `ResolveInput` projection and the thread-safe `_diag` emit stay off-thread. This is **guarded by a `DeferredUiDispatcher` regression test** (`InitializeAsync_MarshalsRebuildThroughDispatcher_NotDirectly`): after `await InitializeAsync()` returns, `Inputs` is still the ctor's single text-only input and `IsLoadingInputs` is still `true` until `Drain()` — proving the rebuild went through `Post`, exactly as the 3.2 precedent. Pre-await mutations (the ctor's text inputs, the off-step gate) stay direct.

**Resolutions to the 4 open questions:**
1. **`ResolveInput` location** → a **private method on `InvocationPopupViewModel`** (not a separate `ArgumentInputFactory`). The resolution is only ever needed by the popup, keeps the `_diag` emit local, and is covered end-to-end through `InitializeAsync` tests.
2. **Off-step UX** → **inline `ValidationError` + a synchronous Invoke short-circuit** (not `CanExecute=false`). Simpler, per-input message, no `NotifyCanExecuteChangedFor` plumbing across N child VMs; runs before the first await so no marshalling is needed.
3. **Per-service state-table caching** → **DEFERRED** (not done). The popup re-fetches the SCPD per open via the `LoadActionsAsync` URL idiom (spec-blessed, arch L597). Caching would require re-plumbing the `IInvocationPopupLauncher` seam — out of scope.
4. **Numeric dataType set** → kept **exactly** as the epic lists: `{ ui1, ui2, ui4, i1, i2, i4, int }` (case-insensitive). `float`/`r4`/`r8`/`number` fall to free-form text (PRD §7 narrow-v1). Not widened.

**Minor deviation worth a reviewer glance:** I initially added an App `StringToVisibilityConverter` for the inline error's collapse, but a converter's `{StaticResource}` lookup root must be a `FrameworkElement` and the popup's `x:Bind` root is a `Window` → the XAML compiler emitted a `SetConverterLookupRoot(this)` against the Window and failed to compile (CS1503). This is the same Window-root constraint the 3.2 code-behind comment already documents. Resolution: dropped the converter (and its file) and bound the error `TextBlock.Text` directly to `ValidationError` — a null/empty string renders an empty, zero-height TextBlock (no extra row when valid). Functionally equivalent, no Core/Window-root coupling.

**Broad catch:** `InitializeAsync` uses `catch (Exception ex) when (ex is not OperationCanceledException)` (the NFR-R3 defensive form the story sanctions) rather than enumerating `UpnpException or UpnpProtocolException` — any non-cancellation failure keeps the ctor's text inputs + one `ScpdParse` warning. OCE is caught first and swallowed (no diagnostic).

**⚠️ MANUAL UI SMOKE (Task 9) IS OPEN — a blocker to `done`.** It could NOT be run here (headless agent, no UPnP device, no display). Story moved to `review` (mirrors the 3.2 precedent) so code-review can proceed, but the smoke gate remains OPEN. Concrete steps the human must run on a real device before `done`:
1. **Dropdown (FR-102):** double-click an action whose input arg's related state var has an `<allowedValueList>` (e.g. `RenderingControl SetMute`, or a Linn `Ds/Preamp` enumerated action) → arg renders as a **ComboBox** pre-selected to the default; pick another value; Invoke → selected value reaches the device.
2. **Numeric (FR-103):** double-click `RenderingControl SetVolume` (or any numeric `<allowedValueRange>` arg, e.g. `0..100 step 1`) → renders as a **NumberBox** bounded to min/max, spinner stepping by `<step>`, pre-filled with default-or-min; set a value; Invoke → device receives the **invariant-formatted** number (`.` decimal).
3. **Off-step rejection (FR-103):** type an off-step value into the NumberBox → inline error appears and Invoke is refused (no SOAP request fires); correct it → Invoke proceeds.
4. **Fallback-to-text:** double-click an action whose input arg has neither list nor range (plain string / `A_ARG_TYPE_*`) → renders as the **TextBox** (3.2 behaviour, no regression).
5. **Loading state:** confirm the input panel briefly shows "Loading…" then resolves to the variant controls (may flash on a fast LAN — acceptable).
Record device(s) + outcomes here when run.

**Follow-ups for the code reviewer:**
- Confirm the broad `catch` in `InitializeAsync` is acceptable (vs the narrower typed list the story's pseudo-code shows) — I chose the sanctioned NFR-R3 defensive form.
- Confirm the inline-error TextBlock (no converter, empty-string-collapses) is an acceptable substitute for the originally-specced `Converter`-driven Visibility, given the Window-root converter-lookup constraint.
- The real-parser integration test (`InitializeAsync_RealParser_OverRichFixture_ResolvesAllVariants`) drives `XmlReaderScpdParser` over `state-table-rich.xml` end-to-end through the VM — verifies the consumer wiring against actual parser output, not just hand-built tables.

### File List

**Created (Core):**
- `src/ohSpy.Core/ViewModels/AllowedValueListArgumentViewModel.cs`
- `src/ohSpy.Core/ViewModels/AllowedValueRangeArgumentViewModel.cs`

**Created (App):**
- `src/ohSpy.App/Converters/ArgumentInputTemplateSelector.cs`

**Created (tests):**
- `tests/ohSpy.Core.Tests/ViewModels/AllowedValueListArgumentViewModelTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/AllowedValueRangeArgumentViewModelTests.cs`

**Modified (Core):**
- `src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs` (8th ctor arg `IScpdParser`; `IsLoadingInputs`; `InitializeAsync`; `ResolveInput`; `EmitScpdParse`; off-step Invoke gate; `NumericDataTypes` set)

**Modified (App):**
- `src/ohSpy.App/Views/InvocationPopupWindow.xaml` (selector + 3 templates, "Loading…" hint, `xmlns:conv`)
- `src/ohSpy.App/Views/InvocationPopupWindow.xaml.cs` (`LoadingInputsVisibility` projection + raise)
- `src/ohSpy.App/Windowing/InvocationPopupLauncher.cs` (fire-and-forget `InitializeAsync` kick-off)
- `src/ohSpy.App/Composition/ServiceRegistration.cs` (factory closure resolves `IScpdParser`)

**Modified (tests):**
- `tests/ohSpy.Core.Tests/Fakes/StubScpdParser.cs` (controllable `StateTable` + `StateTableThrower`; `ReadStateTableAsync` returns the canned table)
- `tests/ohSpy.Core.Tests/ViewModels/InvocationPopupViewModelTests.cs` (8th ctor arg at all sites; `InitializeAsync` variant/fallback/marshalling block; off-step Invoke gate tests; real-parser integration test)

**Unchanged but verified:** `ArgumentInputViewModel.cs` (the base seam — non-sealed, `virtual ResolvedValue` — needed no edit), `App.xaml` (reverted the transient converter resource).

---

## Change Log

| Date | Change |
|------|--------|
| 2026-06-03 | Story 3.3 implemented (automated portion). Constrained inputs layered onto the 3.2 popup: `AllowedValueListArgumentViewModel` (FR-102 dropdown) + `AllowedValueRangeArgumentViewModel` (FR-103 numeric); `InvocationPopupViewModel.InitializeAsync` async state-table load with `_ui.Post` UI-thread marshalling (DeferredUiDispatcher-guarded) + per-arg variant resolution + fallbacks; off-step Invoke gate; App `DataTemplateSelector` (ComboBox/NumberBox/TextBox) + "Loading…" hint; DI factory grows `IScpdParser`. Core build 0/0, full suite 396 passed / 2 skipped (+40). Status → review. **Manual UI smoke (Task 9) remains OPEN — a blocker to `done`.** |
| 2026-06-04 | Code review passed (APPROVED-WITH-MINOR-CAVEAT). 0 patch findings, 0 decision-needed findings. 2 deferred: pre-existing `NoInputsVisibility` `Mode=OneWay` omission (3.2 carry-over); smoke steps 2-5 (NumberBox/off-step/fallback/loading) deferred pending Linn DS network. UI-thread marshalling verified complete + `DeferredUiDispatcher` regression test confirmed genuine. All 3.2 flows intact. Status → done. |
