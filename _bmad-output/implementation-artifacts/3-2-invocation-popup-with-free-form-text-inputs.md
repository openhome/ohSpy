---
baseline_commit: dfa5b81
---

# Story 3.2: Invocation Popup with Free-Form Text Inputs

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Linn engineer,
I want double-clicking an action node to open an invocation popup that lists every input argument as a free-form text input, lets me press Invoke to POST the SOAP request, and displays success outputs / UPnP fault details / transport errors,
So that I can drive any device action with arbitrary arguments and see exactly what the device returned — without leaving ohSpy.

---

## ⚠️ READ THIS FIRST — reconcile the epic against shipped reality

The epic for 3.2 was written before Stories 3.1 and 2.9 shipped. **Five places diverge from the code you will actually build against.** Internalise these before writing anything — they change the design, not just the wording.

| # | Epic says | Shipped reality | Your job |
|---|---|---|---|
| 1 | `ControlUrl = parentService.ControlUrl` (epic L1370) | `ServiceDescription.ControlUrl` is a **`string`**, often **relative** (`/ctrl`). `SoapRequest.ControlUrl` is an absolute **`Uri`**. | **Resolve** `new Uri(parentEntry.LocationUrl, parentService.ControlUrl)` in the VM. |
| 2 | `ShellViewModel.OpenInvocationPopupCommand(action)` (epic L1359) | The shipped `ActionNodeViewModel` is a **bare leaf** holding only `ScpdAction` — no back-ref to its service, entry, token, or `NodeServices`. Shell can't get from a bare action to its context. Arch table (L1330) says `ActionNodeViewModel.OnDoubleClick`. | **Enrich `ActionNodeViewModel`** with parent context (threaded `ServiceNode → ActionNode`, same as `ServiceNode` gets its context today) + an `OpenInvocationPopupCommand` that crosses the Core/App boundary via a **new `IInvocationPopupLauncher` seam** (mirror of `IPropertiesLauncher`). |
| 3 | "device disappears → `parentEntry.DeviceCts` cancels" (epic L1397-1402) | `RegistryEntry.DeviceCts` is **`internal`**; only `RegistryEntry.DeviceToken` (public) is reachable from a popup VM. And the banner is delivered the 2.9 way: `IDeviceRegistry.DeviceRemoved` event on UUID match. | Link `_popupCts` to **`parentEntry.DeviceToken`** (not `DeviceCts`). Subscribe to **`IDeviceRegistry.DeviceRemoved`** for the banner — **both** mechanisms (D7 "two mechanisms, one outcome", arch L833-837). |
| 4 | "may be a duplicate of the emit Story 3.1 added… engineering judgment whether to suppress" (epic L1409) | 3.1 already emits `Warning SoapFault` **inside `UpnpHttpClient`** with `DeviceUuid = null` (no UUID at the http layer). | **Decision baked in:** KEEP the popup-level `SoapFault` emit — it carries `parentEntry.Uuid` (the operator-facing identity for the FR-041 Diagnostics column). Do **not** suppress it. The http-layer emit is uuid-less; the popup emit is the useful one. Document inline. |
| 5 | ctor takes injected `IUpnpHttpClient, IUiDispatcher, IDiagnosticEmitter` (epic L1346) | Correct — but the epic omits `IDeviceRegistry` (needed for #3). `IScpdParser` is **NOT** needed in 3.2 (state-table parse is Story 3.3). | Inject `IUpnpHttpClient, IUiDispatcher, IDiagnosticEmitter, IDeviceRegistry`. `ArgumentInputViewModel` is **text-only** here — the polymorphic base seam Story 3.3 extends. |

**This story is the first consumer of Story 3.1's SOAP layer** (`SoapRequest`/`SoapResponse`/`SoapArgument` records + `InvokeActionAsync`) and **the second reuse of Story 2.9's popup pattern** (`WindowOwnershipManager` + `Activate()→Adopt()` + the `IXxxLauncher` Core-seam/App-impl precedent). Lean on both — do not reinvent.

---

## Acceptance Criteria

> ACs are the epic's, reconciled to shipped reality (see table above). File locations pinned in Dev Notes.

**AC-3.2.1 — `InvocationPopupWindow` (App; FR-025)**
1. `src/ohSpy.App/Views/InvocationPopupWindow.xaml` + `.xaml.cs` exist. Layout: action name / title header, a panel of input-argument controls (one row per input arg), an **Invoke** button, a result area that toggles between "no result yet" / output args / fault detail / transport error, and a status indicator ("Invoking…" / idle).
2. Code-behind is constructor-only (Pattern 13) — the only logic is the `Window.Closed` handler (cancels + disposes the VM) plus the App-side `bool/enum → Visibility` projections the VM can't carry (Pattern 2 forbids `Visibility` in Core; mirror `PropertiesWindow.xaml.cs`).

**AC-3.2.2 — `InvocationPopupViewModel` (Core; FR-026/027)**
3. `src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs` is a `public sealed partial class : ObservableObject, IDisposable`. Constructor takes `ScpdAction action`, `ServiceDescription parentService`, `RegistryEntry parentEntry`, plus injected `IUpnpHttpClient`, `IUiDispatcher`, `IDiagnosticEmitter`, `IDeviceRegistry`.
4. Exposes `string Title` — engineering judgment on shape; **decision: `$"{ComputeServiceTail(parentService)} · {action.Name}"`** reusing the `:service:` tail logic already in `ServiceNodeViewModel.ComputeLabel` (consistency); document the choice inline.
5. Exposes `ObservableCollection<ArgumentInputViewModel> Inputs` populated from `action.Inputs` in declared order — one input control per declared input arg (FR-026).
6. Exposes `[ObservableProperty] InvocationResultViewModel? Result` (null until Invoke completes).
7. Exposes `[RelayCommand(CanExecute = nameof(CanInvoke))] InvokeAsync` (FR-027). `CanInvoke` returns `false` while a call is in flight (re-invoke guard) and `true` otherwise — including for argument-less actions.
8. Exposes a `[ObservableProperty] bool _isInvoking` (drives the "Invoking…" status + disables controls; App projects to `Visibility`/`IsEnabled`).

**AC-3.2.3 — `ArgumentInputViewModel` (Core; FR-026 + Story 3.3 seam)**
9. `src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs` wraps an `ScpdArgument` and exposes `string Name` and `[ObservableProperty] string Value` (default `""` — free-form text).
10. The type is shaped as the **polymorphic base** Story 3.3 extends (`AllowedValueListArgumentViewModel` / `AllowedValueRangeArgumentViewModel`): it is **not `sealed`**, `Value` is the single resolved-string-value seam all variants funnel into, and any virtual/overridable members 3.3 needs (e.g. a `virtual string ResolvedValue => Value;`) are introduced now. Base is text-only; **no** state-table parsing in this story.

**AC-3.2.4 — Double-click trigger + popup open (FR-025, FR-046 reuse, D10, SC-010)**
11. `ActionNodeViewModel` is enriched with the parent context needed to build the popup (the parent `ServiceDescription`, the device `RegistryEntry`, `NodeServices`, and the `deviceToken`), threaded `ServiceNodeViewModel → ActionNodeViewModel` exactly as `ServiceNodeViewModel` receives its own context today. It exposes `[RelayCommand] OpenInvocationPopup` (sync, fire-and-forget; mirrors `DeviceNodeViewModel.OpenPropertiesCommand`).
12. `OpenInvocationPopupCommand` calls `_services.InvocationPopupLauncher.Open(action, parentService, parentEntry)` (a **new `IInvocationPopupLauncher` Core seam**, App-implemented).
13. The App impl constructs the `InvocationPopupViewModel` via a Pattern-7 factory, `new InvocationPopupWindow(vm)`, calls `Activate()`, **then** `_ownership.Adopt(window, ShellWindow)` — the canonical D10 sequence from Story 2.9 (AC-10.5).
14. The App double-tap handler routes a double-click on an `ActionNodeViewModel` row to `OpenInvocationPopupCommand` (the shipped `MainWindow.OnTreeDoubleTapped` currently handles only Device/Service expand — add the action branch).
15. The popup is interactive (input fields editable) within ≤ 1 s of the double-click (SC-010) — manual smoke.

**AC-3.2.5 — Argument-less action (FR-031)**
16. For an action with no inputs, `Inputs` is empty, the input panel shows a neutral **"No input arguments"** hint, and the Invoke button is enabled.

**AC-3.2.6 — Invoke builds the SoapRequest correctly (FR-027)**
17. `InvokeAsync` constructs `new SoapRequest(controlUrl, parentService.ServiceType, action.Name, inputArgs)` where:
    - `controlUrl = new Uri(parentEntry.LocationUrl, parentService.ControlUrl)` — **resolves the relative/absolute control URL** (reconciliation #1). If resolution throws/fails, transition to a TransportError result (do not crash).
    - `inputArgs = Inputs.Select(i => new SoapArgument(i.Name, i.ResolvedValue)).ToList()`.
18. The call passes `_popupCts.Token`, where `_popupCts = CreateLinkedTokenSource(parentEntry.DeviceToken)` (D7 popup level, linked to the **device token** — reconciliation #3).
19. While in flight: `IsInvoking = true`, `CanInvoke` false, status shows "Invoking…", controls disabled (NFR-UI3 — feedback without flicker). On completion (any outcome): `IsInvoking = false`.

**AC-3.2.7 — Success result (FR-028, SC-011)**
20. On a returned `SoapResponse`, `Result` is set to a **Success** variant carrying the output args as `(Name, Value)` pairs (from `response.OutputArguments`).
21. The result area renders one row per output arg; an argument-less response shows a neutral **"Success (no output)"** message (FR-031 second consequence).
22. Result visible ≤ 2 s of pressing Invoke for a < 1 s-LAN device (SC-011) — manual smoke.

**AC-3.2.8 — UPnP fault result (FR-029)**
23. On `UpnpFaultException`, `Result` is set to a **Fault** variant carrying `StatusCode` (500), `ErrorCode`, `ErrorDescription`.
24. The result area visually distinguishes the fault from success (warning brush / icon) — App-side.

**AC-3.2.9 — Transport-error result (FR-030, NFR-R3)**
25. On `UpnpTransportException`, `UpnpTimeoutException`, `UpnpProtocolException`, or any other non-fault transport failure, `Result` is set to a **TransportError** variant carrying a human-readable message (Url + StatusCode-if-known + exception message).
26. The result area visually distinguishes the transport error from a UPnP fault. The popup does NOT crash (NFR-R3).

**AC-3.2.10 — Popup close mid-invocation (D7, AC-7.3/7.4)**
27. On `Window.Closed`, the code-behind calls a VM `Cancel()`/`Dispose()` that calls `_popupCts.Cancel()` → the in-flight SOAP request observes cancellation and throws `OperationCanceledException`.
28. `_popupCts` is disposed (in a `finally` or in `Dispose()`), and the registry subscription is removed — **no leaked CTS, no leaked handler** (AC-7.4). `Dispose()` is idempotent (Interlocked guard, mirror `PropertiesViewModel`).
29. `OperationCanceledException` from a popup-close cancel is swallowed in `InvokeAsync` (it is not a fault — no `Result`, no diagnostic). The popup closes cleanly.

**AC-3.2.11 — Device disappears mid-invocation (FR-037, NFR-R3)**
30. When the device is removed (`byebye` / rescan-prune), `parentEntry.DeviceToken` cancels → `_popupCts` cancels (linked) → the in-flight invocation throws `OperationCanceledException` (swallowed, as AC-3.2.10).
31. Independently, `IDeviceRegistry.DeviceRemoved` fires on the UI thread; on a UUID match the VM flips an `IsDeviceGone` banner (the 2.9 `PropertiesViewModel` pattern verbatim). Display data already shown stays; the banner appears.
32. The popup remains closeable without errors.

**AC-3.2.12 — Diagnostic discipline (Pattern 11)**
33. On `UpnpTimeoutException` caught in `InvokeAsync`: emit `Warning DiagCategories.HttpTimeout` with `DeviceUuid = parentEntry.Uuid`, `Url = controlUrl.ToString()`, `ActionName = action.Name`, `Elapsed`, `Budget` (the exception carries `Budget`/`Elapsed`).
34. On `UpnpFaultException` caught: emit `Warning DiagCategories.SoapFault` with `DeviceUuid = parentEntry.Uuid`, `Url`, `ActionName`, `StatusCode = 500`, `ErrorText = $"{ErrorCode}: {ErrorDescription}"`. **This is the UUID-bearing emit** (reconciliation #4 — keep it; the 3.1 http-layer emit is uuid-less). Document the intentional duplication inline.
35. On `UpnpTransportException` / `UpnpProtocolException` caught: emit `Warning DiagCategories.SoapInvoke` with `DeviceUuid`, `Url`, `ActionName`, `StatusCode` (when known), `ErrorText`.
36. `OperationCanceledException` (popup close / device gone) → **no** diagnostic (cancellation is not a fault — mirror the 3.1 / `ServiceNodeViewModel` convention).

**AC-3.2.13 — Tests + manual smoke**
37. Core unit tests cover `InvocationPopupViewModel` (Title; Inputs population incl. argument-less; SoapRequest construction incl. **relative-ControlUrl resolution**; Success/Fault/TransportError result mapping; each diagnostic emit incl. DeviceUuid; in-flight `CanInvoke` guard; cancel-on-dispose swallows OCE + no diagnostic; CTS disposed + unsubscribed; FR-037 `DeviceRemoved` banner on UUID match; dispose idempotency), `ArgumentInputViewModel` (Name/Value default/`ResolvedValue`), and `InvocationResultViewModel` variants. Every test carries `[Trait("ac", "AC-3.2.x")]`.
38. The App popup window, the `IInvocationPopupLauncher` impl, and the `Activate→Adopt` wiring are App-only and **cannot** be unit-tested (no App test project; `CoreAppBoundaryTests` forbids `Core.Tests → App`). AC-3.2.4 (FR-046 z-order/minimise/close), AC-3.2.7 SC-011, AC-3.2.9 no-crash, and the popup render are **manual UI smoke** (Task 9) — REQUIRED before review/done, NOT deferred (Epic 2 retro action E).
39. Gates: `dotnet build` 0 warnings (Core 0/0; App may carry the one pre-existing benign `WMC1506` on `MainWindow.xaml:141`); full suite green (baseline 330 passed / 2 skipped — expect ~348-352); chaos suite still 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green.

---

## Tasks / Subtasks

- [x] **Task 1 — Core result + argument view-models** (AC-3.2.3, AC-3.2.7/8/9)
  - [x] Create `src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs` — `public partial class` (NOT sealed; 3.3 subclasses it), wraps `ScpdArgument`, `string Name { get; }`, `[ObservableProperty] string _value = "";`, and a `public virtual string ResolvedValue => Value;` seam (3.3 overrides for list/range variants). Document the 3.3 extension intent in the class XML doc.
  - [x] Create `src/ohSpy.Core/ViewModels/InvocationResultViewModel.cs` — model the three outcomes. **Decision: one sealed record hierarchy or a single record with a `kind` discriminator — pick the cleaner shape; recommended: an abstract `InvocationResultViewModel` base + three sealed subclasses `SuccessResult(IReadOnlyList<SoapArgument> Outputs)`, `FaultResult(int StatusCode, int ErrorCode, string ErrorDescription)`, `TransportErrorResult(string Message)`.** The App XAML uses a `DataTemplateSelector` or per-type `Visibility` projection (mirror `NodeDataTemplateSelector`). Reuse `SoapArgument` for the output `(name,value)` pairs — do NOT invent a new pair type.
- [x] **Task 2 — `IInvocationPopupLauncher` Core seam** (AC-3.2.4)
  - [x] Create `src/ohSpy.Core/ViewModels/IInvocationPopupLauncher.cs` — `void Open(ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry);` Model the doc-comment on `IPropertiesLauncher` (Pattern 2 boundary rationale; canonical Activate→Adopt note).
- [x] **Task 3 — `InvocationPopupViewModel`** (AC-3.2.2, .5, .6, .7, .8, .9, .10, .11, .12)
  - [x] Create `src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs` — `public sealed partial class : ObservableObject, IDisposable`. Ctor signature per AC-3.2.2 #3. Snapshot `_uuid = parentEntry.Uuid`, resolve `_controlUrl` once (guard the `new Uri(...)` — on failure, store a flag so `InvokeAsync` short-circuits to a TransportErrorResult), build `Inputs` from `action.Inputs`.
  - [x] `_popupCts = CancellationTokenSource.CreateLinkedTokenSource(parentEntry.DeviceToken);` (D7 — link to the **DeviceToken**, the public token; `DeviceCts` is internal). Subscribe `_registry.DeviceRemoved += OnDeviceRemoved` (UI-thread; mirror `PropertiesViewModel`).
  - [x] `InvokeAsync`: set `IsInvoking = true` + notify `CanInvoke`; build `SoapRequest`; `try { var resp = await _http.InvokeActionAsync(req, _popupCts.Token); Result = new SuccessResult(resp.OutputArguments); }` with catch arms per AC-3.2.8/9/12 and an `catch (OperationCanceledException) { /* swallow — close/device-gone; no Result, no diag */ }`; `finally { IsInvoking = false; }`. **Diagnostics: emit-before-setting-Result, structured `DiagnosticContext`, never interpolate context into the message** (Pattern 11; match `ServiceNodeViewModel.EmitFailure`).
  - [x] `[ObservableProperty] bool _isDeviceGone; [ObservableProperty] string _deviceGoneText = "";` + `OnDeviceRemoved(Guid uuid)` (UUID-match → set banner; idempotent — copy `PropertiesViewModel.OnDeviceRemoved`).
  - [x] `Dispose()` (Interlocked-guarded): `_popupCts.Cancel(); _registry.DeviceRemoved -= OnDeviceRemoved; _popupCts.Dispose();`. Also expose a thin `Cancel()` the window's `Closed` handler can call (or have the handler call `Dispose()` directly — match how `PropertiesWindow.OnClosed` calls `ViewModel.Dispose()`).
- [x] **Task 4 — Enrich `ActionNodeViewModel` + thread context** (AC-3.2.4)
  - [x] Add to `ActionNodeViewModel` ctor: `ServiceDescription parentService, RegistryEntry parentEntry, NodeServices services` (keep `ScpdAction action` first). Store them. Add `[RelayCommand] void OpenInvocationPopup() => _services.InvocationPopupLauncher.Open(_action, _parentService, _parentEntry);` (sync fire-and-forget; mirror `DeviceNodeViewModel.OpenProperties`). Keep `Children` empty (still a leaf) + the `KindGlyph`.
  - [x] In `ServiceNodeViewModel.LoadActionsAsync`, change `new ActionNodeViewModel(action)` → `new ActionNodeViewModel(action, _service, _parentEntry, _services)`. **`ServiceNodeViewModel` does not currently hold the `RegistryEntry`** — it holds `_deviceLocation`, `_deviceUuid`, `_deviceToken`. Thread the `RegistryEntry` down `DeviceNode → ServiceNode` (DeviceNode has `_entry`): add a `RegistryEntry parentEntry` param to the `ServiceNodeViewModel` ctor and pass `_entry` from `DeviceNodeViewModel.OnIsExpandedChanged`'s `new ServiceNodeViewModel(...)`. (The popup VM needs `LocationUrl` + `Uuid` + `DeviceToken` — all on `RegistryEntry` — so passing the entry is cleanest and avoids widening `ActionNodeViewModel` to 6 scalars.)
- [x] **Task 5 — `NodeServices` 7th member + DI** (AC-3.2.4)
  - [x] Add `IInvocationPopupLauncher InvocationPopupLauncher` to the `NodeServices` record (after `PropertiesLauncher`).
  - [x] In `ServiceRegistration.cs`: register `IWindowOwnershipManager` is already there; add the Pattern-7 VM factory `Func<(ScpdAction, ServiceDescription, RegistryEntry), InvocationPopupViewModel>` (or a small factory delegate type — pick the cleaner; a named delegate reads better than a 3-tuple Func) resolving `IUpnpHttpClient`, `IUiDispatcher`, `IDiagnosticEmitter`, `IDeviceRegistry`; register `InvocationPopupLauncher` concrete + `IInvocationPopupLauncher` (dual-reg, like `PropertiesLauncher`). Register it **before** the `NodeServices` line so it auto-resolves into the bundle.
  - [x] In `App.OnLaunched`, set `Services.GetRequiredService<InvocationPopupLauncher>().ShellWindow = _window;` (mirror the `PropertiesLauncher.ShellWindow` line).
- [x] **Task 6 — App: `InvocationPopupLauncher` + `InvocationPopupWindow`** (AC-3.2.1, .4)
  - [x] Create `src/ohSpy.App/Windowing/InvocationPopupLauncher.cs` — `internal sealed class : IInvocationPopupLauncher` with `Window? ShellWindow { get; set; }`, the VM factory, and `IWindowOwnershipManager`. `Open(...)` = factory → `new InvocationPopupWindow(vm)` → `window.Activate()` → `if (ShellWindow is not null) _ownership.Adopt(window, ShellWindow);` (copy `PropertiesLauncher.cs` verbatim in shape).
  - [x] Create `src/ohSpy.App/Views/InvocationPopupWindow.xaml` + `.xaml.cs` — header (Title), input panel (`ItemsControl`/`ItemsRepeater` over `Inputs` with a `TextBox` bound `Value` two-way + a "No input arguments" hint when empty), Invoke button (`Command="{x:Bind ViewModel.InvokeCommand}"`), status text (bound `IsInvoking`), result area (DataTemplate-selected over `Result`: success rows / fault detail / transport message), and the device-gone banner (copy the `PropertiesWindow.xaml` banner Border + `BannerVisibility`). Code-behind: constructor-only + `Closed` handler that disposes the VM (mirror `PropertiesWindow.xaml.cs`) + the `Visibility`/`IsEnabled` projections.
- [x] **Task 7 — App: double-tap routing** (AC-3.2.4 #14)
  - [x] In `MainWindow.xaml.cs.OnTreeDoubleTapped`, add a branch: when `item is ActionNodeViewModel act`, call `act.OpenInvocationPopupCommand.Execute(null)` (do not toggle expansion — actions are leaves). Keep the existing Device/Service expand branch for non-leaf nodes. Document as a Pattern-13 view-mechanics exception (like the existing handler).
- [x] **Task 8 — Update existing tests + fakes** (AC-3.2.13)
  - [x] Create `tests/ohSpy.Core.Tests/Fakes/FakeInvocationPopupLauncher.cs` — records `(action, service, entry)` tuples (mirror `FakePropertiesLauncher`).
  - [x] Update **every** `NodeServices` construction site to pass the 7th arg. There are **4 test sites + 1 DI reg** (some use target-typed `new(...)`, so grep both `new NodeServices(` AND the `NodeServices` helper methods): `DeviceNodeViewModelTests` static field (L21-23), `DeviceNodeViewModelTests.Expand_NoHttpFetchTriggered` (L221-222), `DeviceNodeViewModelTests.CapturingServices()` (L272-273), `DeviceTreeViewModelTests` (L26), `ServiceNodeViewModelTests.MakeNodeServices` (L37-42, target-typed `new(...)`), and `ServiceRegistration.cs` (L118).
  - [x] Update `ActionNodeViewModelTests` (3 tests) for the new ctor — its `Action(...)` helper now needs a `ServiceDescription` + `RegistryEntry` + `NodeServices`; add small builders (mirror `DeviceNodeViewModelTests.Svc`/`LoadedEntry`).
  - [x] Update `ServiceNodeViewModelTests` for the new `ServiceNodeViewModel` ctor param (`RegistryEntry parentEntry`): the `NewVm` helper (L44-47, target-typed `new(...)`) directly constructs `ServiceNodeViewModel` and needs the entry threaded in. Add a `RegistryEntry` builder (mirror `DeviceNodeViewModelTests.LoadedEntry`). Note `ServiceNodeViewModel` is NOT constructed via `new ServiceNodeViewModel(` literally anywhere (target-typed `new` + the production call in `DeviceNodeViewModel.cs:60`) — update both.
- [x] **Task 9 — Core tests for the new VMs** (AC-3.2.13 #37)
  - [x] `tests/ohSpy.Core.Tests/ViewModels/InvocationPopupViewModelTests.cs` — use `StubUpnpHttpClient` (extend it: its `InvokeActionAsync` currently `throw new NotSupportedException()` — add an `InvokeResponder` closure like `ScpdResponder`/`DescriptionResponder` so a test can return a `SoapResponse`, throw `UpnpFaultException`, throw `UpnpTimeoutException`, or block on the token), `InlineUiDispatcher`, `CapturingDiagnosticEmitter`, `FakeDeviceRegistry`. Cover all AC-3.2.13 #37 cases. **Assert on what the parse produced / the request that went out, not on inputs you handed in** (Epic 2 lesson — capture the `SoapRequest` in the stub and assert `ControlUrl` is the RESOLVED absolute Uri, args map 1:1).
  - [x] `tests/ohSpy.Core.Tests/ViewModels/ArgumentInputViewModelTests.cs` — Name, default `Value=""`, `ResolvedValue == Value`.
  - [x] (Optional) `InvocationResultViewModelTests` if the variants carry logic; otherwise covered via the popup VM tests.
- [~] **Task 10 — Manual UI smoke (RUN 2026-06-03 on live Sky network — core paths PASS; 3 steps deferred)** (AC-3.2.13 #38; Epic 2 retro action E + project memory)
  - [x] Open ≤ 1 s (SC-010): double-click an action row → popup opens interactive within ~1 s. **PASS.**
  - [x] Argument-less success ≤ 2 s (SC-011): `WANIPConnection:1 GetConnectionTypeInfo` → output rows render fast. **PASS.** (Surfaced + fixed a cosmetic clip: the arg-name column hard-clipped long names like `NewPossibleConnectionTypes` at 160 px → widened to 200 px + `TextWrapping="Wrap"` in `InvocationPopupWindow.xaml`.)
  - [x] Typed inputs reach the device: `GetGenericPortMappingEntry` with `NewPortMappingIndex` → device processed the supplied index. **PASS.**
  - [x] UPnP fault + caution styling (FR-029): `GetGenericPortMappingEntry` with out-of-range index → HTTP 500 `713 SpecifiedArrayIndexInvalid`, fault view renders with caution border. **PASS.**
  - [x] FR-046 ownership (AC-3.2.4): popup above main, stays above on main-focus, minimises/restores + closes with main. **PASS.**
  - [ ] ~~Transport-error styling (AC-3.2.9)~~ — **DEFERRED** (single-device LAN can't cleanly force a transport failure without pruning the device). VM logic unit-tested (`Invoke_Transport_…`/`Invoke_Protocol_…`); only the App-side critical-border Visibility projection is unverified — same code-behind projection mechanism the PASSED fault (step 4) + ownership (step 8) exercised.
  - [ ] ~~Device-gone banner (AC-3.2.11)~~ — **DEFERRED.** VM logic unit-tested (`DeviceRemoved_UuidMatch_FlipsBanner`); only the App-side banner Visibility projection is unverified (same mechanism).
  - [ ] ~~Close mid-invoke (AC-3.2.10)~~ — **DEFERRED** (IGD calls return <1 s; not reliably catchable in-flight). VM logic unit-tested (`Dispose_MidInvoke_CancelsSwallowsOce_…`).

  > **SMOKE OUTCOME (2026-06-03, live Sky network, Opus operator-driven):** The core interactive paths PASS — open speed, success rendering, typed-inputs-reach-device, the UPnP-fault path + caution styling, and FR-046 window ownership. **A real crash was caught and fixed first** (argument-less invoke → `RPC_E_WRONGTHREAD`; WinUI-3-no-SynchronizationContext → post-await continuation off the UI thread; fixed by marshalling the terminal state via `_ui.Post` — see memory `winui-no-synccontext-marshal-vm`; regression test added). A cosmetic name-clip was also fixed. **Steps 5/6/7 (transport-error styling, device-gone banner, close-mid-invoke) are DEFERRED to a richer network** — each has passing **Core VM unit tests**; only their App-side Visibility projection is unverified, and that projection is the identical code-behind mechanism the PASSED fault + ownership steps validated. This is an explicit, honest partial (not a silent defer): risk is low and bounded to two XAML Visibility projections. Recommend proceeding to code-review; revisit 5/6/7 when a multi-device / Linn-DS network (or the Story 5.2 adapter switch) is available.

- [x] **Task 11 — Gates** (AC-3.2.13 #39)
  - [x] `dotnet build` 0 warnings (Core 0/0); full suite green; chaos 1; `CoreAppBoundaryTests` + `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green. Note the App `WMC1506` is pre-existing.

---

## Dev Notes

### Files you will CREATE

```
src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs          # text-only base; 3.3 subclasses it
src/ohSpy.Core/ViewModels/InvocationResultViewModel.cs       # Success / Fault / TransportError variants
src/ohSpy.Core/ViewModels/IInvocationPopupLauncher.cs        # Core seam (mirror IPropertiesLauncher)
src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs        # the automated-test heart
src/ohSpy.App/Windowing/InvocationPopupLauncher.cs           # App impl (mirror PropertiesLauncher)
src/ohSpy.App/Views/InvocationPopupWindow.xaml + .xaml.cs    # FR-025 popup window
tests/ohSpy.Core.Tests/Fakes/FakeInvocationPopupLauncher.cs  # mirror FakePropertiesLauncher
tests/ohSpy.Core.Tests/ViewModels/InvocationPopupViewModelTests.cs
tests/ohSpy.Core.Tests/ViewModels/ArgumentInputViewModelTests.cs
```

### Files you will MODIFY (read each before editing)

- **`src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs`** *(currently a bare leaf: only `_action`, `Label`, empty `Children`, `KindGlyph`)*.
  - *What changes:* ctor gains `ServiceDescription parentService, RegistryEntry parentEntry, NodeServices services`; add `[RelayCommand] OpenInvocationPopup`. Keep `Kind == Action`, empty `Children`, `KindGlyph`.
  - *Preserve:* the leaf shape (no placeholder child → no chevron, AC-2.6.7); the `INodeViewModel.Label` explicit impl.
- **`src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs`** *(holds `_service`, `_deviceLocation`, `_deviceUuid`, `_deviceToken`, `_services`; builds `ActionNodeViewModel`s in `LoadActionsAsync` L105)*.
  - *What changes:* ctor gains `RegistryEntry parentEntry` (store `_parentEntry`); `new ActionNodeViewModel(action, _service, _parentEntry, _services)` at L105.
  - *Preserve:* the separate fetch/parse try blocks + diagnostic attribution (review F1); the incremental-append streaming (`first` placeholder swap); `OperationCanceledException` swallow; the `Subscribe`/`FetchServiceXml` commands; CT-last ctor convention (CA1068).
- **`src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs`** *(holds `_entry`; builds `ServiceNodeViewModel`s in `OnIsExpandedChanged` L60)*.
  - *What changes:* pass `_entry` to the `new ServiceNodeViewModel(...)` call (the new `parentEntry` param). One-line change.
  - *Preserve:* the `_servicesBuilt` Interlocked once-guard (single Reset — AC-A1.4 / Story 2.5 deferral fix); `OpenPropertiesCommand`, `FetchXmlCommand`, `RefreshFrom`.
- **`src/ohSpy.Core/ViewModels/NodeServices.cs`** — add `IInvocationPopupLauncher InvocationPopupLauncher` as the 7th member.
- **`src/ohSpy.App/Composition/ServiceRegistration.cs`** *(L101-118: the 2.9 launcher block + the `NodeServices` line)*.
  - *What changes:* add the invocation-popup VM factory + `InvocationPopupLauncher` dual-reg **before** the `NodeServices` registration (L118) so the bundle auto-resolves it.
  - *Preserve:* registration ordering rationale (IPropertiesLauncher before NodeServices — same constraint applies to the new launcher).
- **`src/ohSpy.App/App.xaml.cs`** *(OnLaunched L90-93 sets `PropertiesLauncher.ShellWindow = _window`)*.
  - *What changes:* add the symmetric `InvocationPopupLauncher.ShellWindow = _window;` line.
- **`src/ohSpy.App/MainWindow.xaml.cs`** *(`OnTreeDoubleTapped` L120-133)*.
  - *What changes:* add the `ActionNodeViewModel` → `OpenInvocationPopupCommand.Execute(null)` branch. **Find the action row's VM the same way the existing handler does — `(e.OriginalSource as FrameworkElement)?.DataContext`.** Do NOT toggle expansion for an action (leaf).
  - *Preserve:* the device/service expand branch + the null-container ItemsSource safety net (WinUI TreeView null-DataContext quirk — see memory note `winui-treeview-datacontext-null`).
- **Tests:** `DeviceNodeViewModelTests`, `ActionNodeViewModelTests`, `ServiceNodeViewModelTests`, `StubUpnpHttpClient` (add `InvokeResponder`). Grep `new NodeServices(` and `new ActionNodeViewModel(` and `new ServiceNodeViewModel(` to find every construction site.

### The launcher seam (reconciliation #2 — the load-bearing design decision)

The epic's `ShellViewModel.OpenInvocationPopupCommand(action)` cannot work: a bare `ScpdAction` has no link to its service/device. The arch table (L1330) is right — the trigger lives on `ActionNodeViewModel`. So:

- **Enrich `ActionNodeViewModel`** with parent context (threaded `ServiceNode → ActionNode`, exactly the pattern by which `ServiceNode` already receives `_deviceLocation`/`_deviceUuid`/`_deviceToken`/`_services` from `DeviceNode`). Pass the **`RegistryEntry`** down rather than 4 scalars — it carries `LocationUrl`, `Uuid`, `DeviceToken` in one object, which is exactly what the popup VM needs.
- **`OpenInvocationPopupCommand` (Core) crosses the boundary via `IInvocationPopupLauncher`** — a Core interface, App impl. This is the **2.9 `IPropertiesLauncher` precedent verbatim** (`src/ohSpy.Core/ViewModels/IPropertiesLauncher.cs` + `src/ohSpy.App/Windowing/PropertiesLauncher.cs`): a Core VM cannot `new` a WinUI `Window` (Pattern 2 / `CoreAppBoundaryTests`), so the "open a window" verb is a seam. Copy the shape: Pattern-7 VM factory (no `IServiceProvider` leak), `Window? ShellWindow { get; set; }` set in `App.OnLaunched`, `Activate()` THEN `Adopt(window, ShellWindow)`.

### `SoapRequest` construction (reconciliation #1 — the bug the epic hides)

`ServiceDescription.ControlUrl` is a **`string`** and frequently **relative** (the 2.6 service builder stores it verbatim; resolution is the caller's job — see the `ServiceDescription` XML doc and how `ServiceNodeViewModel.LoadActionsAsync` resolves `ScpdUrl` via `new Uri(_deviceLocation, _service.ScpdUrl)`). `SoapRequest.ControlUrl` is an absolute **`Uri`** (Story 3.1 reshaped it that way precisely so the popup resolves once). So:

```csharp
// Resolve once at construction (guard — a malformed ControlUrl must not crash the popup):
_controlUrl = Uri.TryCreate(parentEntry.LocationUrl, parentService.ControlUrl, out var u) ? u : null;
// ... in InvokeAsync, if _controlUrl is null → TransportErrorResult("invalid control URL: …"), no SOAP call.

var req = new SoapRequest(
    _controlUrl,
    parentService.ServiceType,                                  // already a URN string
    action.Name,
    Inputs.Select(i => new SoapArgument(i.Name, i.ResolvedValue)).ToList());
var resp = await _http.InvokeActionAsync(req, _popupCts.Token);
```

The shipped records (verified): `SoapRequest(Uri ControlUrl, string ServiceType, string ActionName, IReadOnlyList<SoapArgument> InputArguments)`, `SoapResponse(string ActionName, IReadOnlyList<SoapArgument> OutputArguments)`, `SoapArgument(string Name, string Value)` — all in `namespace ohSpy.Core.Models`. `InvokeActionAsync(SoapRequest request, CancellationToken ct)` — the CT is the popup token.

### CTS + device-token wiring (reconciliation #3 — D7)

`RegistryEntry.DeviceCts` is **`internal`** (only Core's dispatcher + tests drive it). The popup VM links to the **public `RegistryEntry.DeviceToken`** (snapshotted at entry construction; safe to read after the registry disposes `DeviceCts` on removal):

```csharp
_popupCts = CancellationTokenSource.CreateLinkedTokenSource(parentEntry.DeviceToken);  // D7 popup level
```

D7 (arch L734-873, esp. L833-837) is explicit that FR-037 needs **both** mechanisms:
- **In-flight abort:** device removal cancels `DeviceToken` → cancels the linked `_popupCts` → the in-flight `InvokeActionAsync` throws `OperationCanceledException` (swallowed — not a fault).
- **UI notification:** `IDeviceRegistry.DeviceRemoved(uuid)` fires on the UI thread → UUID match → `IsDeviceGone` banner. This is the `PropertiesViewModel.OnDeviceRemoved` pattern verbatim — copy it (including the idempotent guard and the `IDisposable` unsubscribe; without `Dispose()` the singleton registry pins every popup VM ever opened — Story 2.9's hard lesson).

Cleanup ordering (AC-7.4): `Dispose()` cancels `_popupCts`, unsubscribes `DeviceRemoved`, then disposes `_popupCts`. Interlocked-guard it (mirror `PropertiesViewModel` / `SsdpLogViewModel` / `DeviceTreeViewModel`). **Note:** this story has **no UNSUBSCRIBE** (that's GENA / Epic 4) — so the D7 "cleanup uses the level-above token" invariant (arch L790-816) does NOT apply here. There is no post-cancel work that needs the adapter token; popup close is a pure cancel-and-dispose. Don't over-engineer it.

### Diagnostic discipline (reconciliation #4 — the deliberate duplicate)

Story 3.1's `UpnpHttpClient` already emits `Warning SoapFault` / `Warning SoapInvoke` on the fault / transport paths — but with **`DeviceUuid = null`** (the http layer has no UUID; `SoapRequest` carries none). The epic (L1409) anticipated this and left the call. **Decision baked in: the popup VM emits its OWN `SoapFault`/`SoapInvoke`/`HttpTimeout` Warning carrying `DeviceUuid = parentEntry.Uuid`.** This is the operator-facing emit (the FR-041 Diagnostics viewer's Identity column needs the UUID). The two emits coexist; the http-layer one is uuid-less plumbing telemetry, the popup one is the user-identity-bearing one. Document this inline in `InvokeAsync` so a future reader doesn't "fix" the duplication by deleting the useful one.

Pattern 11 mechanics (match `ServiceNodeViewModel.EmitFailure` / the 3.1 client): emit **before** throwing/setting Result, structured `DiagnosticContext { DeviceUuid, Url, ActionName, StatusCode, ErrorText, Elapsed, Budget }`, **never** string-interpolate context into the message. `DiagCategories.HttpTimeout` / `SoapFault` / `SoapInvoke` all already exist (`DiagCategories.cs:13,50,53`) — **no new constant**, so `DiagCategoriesUsageTests` (reflection-based) needs no edit. `OperationCanceledException` → no diagnostic.

`UpnpTimeoutException` carries `.Budget` and `.Elapsed`; `UpnpTransportException` carries `.StatusCode` (nullable) + `.Url`; `UpnpFaultException` carries `.ErrorCode`, `.ErrorDescription`, `.Url`, `.ActionName` (`src/ohSpy.Core/Http/UpnpExceptions.cs`). `UpnpProtocolException` (thrown by the 3.1 review patch on a malformed 2xx body) carries `.Url` — handle it on the transport-error arm.

### `InvocationResultViewModel` shape

Recommended: `public abstract record InvocationResultViewModel;` + `public sealed record SuccessResult(IReadOnlyList<SoapArgument> Outputs) : InvocationResultViewModel;` + `FaultResult(int StatusCode, int ErrorCode, string ErrorDescription)` + `TransportErrorResult(string Message)`. Reuse `SoapArgument` for output pairs (do not invent a new pair type — the response already returns `IReadOnlyList<SoapArgument>`). App renders via a `DataTemplateSelector` keyed on the runtime type (mirror `src/ohSpy.App/Converters/NodeDataTemplateSelector.cs`) OR per-type `Visibility` projections in the window code-behind (mirror the `PropertiesWindow.xaml.cs` hyperlink-vs-text pattern). Keep `Visibility` OUT of Core (Pattern 2).

### XAML — reuse the `PropertiesWindow` chrome

`src/ohSpy.App/Views/PropertiesWindow.xaml` is the template: `MicaBackdrop`, the caution-coloured device-gone `Border` banner bound to `BannerVisibility` (copy it 1:1), `ScrollViewer` body, `x:Bind` to a typed `ViewModel` property on the code-behind (a `Window` binding root is not a `FrameworkElement`, so `Visibility` converters must be code-behind properties, not XAML converters — this is why `PropertiesWindow` projects them in `.xaml.cs`; do the same). Title: set `Title = "Invoke: " + vm.Title` or similar in the ctor.

### Test boundary (Core vs App)

- **Automated (Core.Tests):** `InvocationPopupViewModel`, `ArgumentInputViewModel`, `InvocationResultViewModel`. These are the whole testable surface. Use `StubUpnpHttpClient` (extend with `InvokeResponder`), `InlineUiDispatcher`, `CapturingDiagnosticEmitter`, `FakeDeviceRegistry`, `FakeInvocationPopupLauncher`.
- **App-only, NOT unit-testable** (`CoreAppBoundaryTests` forbids `Core.Tests → App`; there is no App test project): `InvocationPopupWindow`, `InvocationPopupLauncher`, the `Activate→Adopt` wiring, the double-tap routing. These are **manual UI smoke** (Task 10). This is the exact boundary Story 2.9 documented — do not try to test the window.
- **Epic 2 lesson (memory `smoke-per-ui-story` + retro action E):** the device-tree expand bug hid behind green VM tests that set `IsExpanded` directly instead of through the real UI path, across 4 reviews + 313 tests, because the manual smoke was deferred to epic close. This story ships a real WinUI window → **Task 10 manual smoke is a first-class gate, run before review/done, NOT batched to Epic 3 close.** When asserting in Core tests, assert on what the SOAP layer produced / the request captured by the stub — not on values you handed in.

### Previous-story intelligence (carries over)

- **Sealed-record data carriers (Pattern 9)** for result variants; **`new`-constructed VMs threaded via `NodeServices`** (not DI) for tree nodes; **Pattern-7 `Func<>` factory + dual-reg launcher** for the popup (2.9 precedent).
- **`IDisposable` + Interlocked dispose guard + registry unsubscribe** is mandatory for any VM that subscribes to the singleton `IDeviceRegistry` (2.9 / 2.5 / 2.7 pattern — skipping it pins the VM forever).
- **Adding a member to `NodeServices` breaks every construction site** (happened in 2.6/2.8/2.9 — here it's 4 test sites + 1 DI reg). Some sites use target-typed `new(...)`, so a plain `grep "new NodeServices("` MISSES them — grep the helper methods (`MakeNodeServices`, the static `NodeServices` fields) too. Compiler errors will catch any you miss.
- **Sync `[RelayCommand] void`** for fire-and-forget UI verbs (`OpenInvocationPopup`, like `OpenProperties`/`FetchXml`); **`async Task [RelayCommand]`** only for `InvokeAsync` (it awaits HTTP).
- **WinUI TreeView leaves container `DataContext` null** (memory `winui-treeview-datacontext-null`, commit 4d380f8) — the double-tap handler already works around it by reading `e.OriginalSource.DataContext`; follow that, don't bind via the container.

### Git intelligence

`dfa5b81` (Story 3.1, just done) shipped the SOAP records + `Soap/` classes + re-wired `InvokeActionAsync` — pure Core, no UI. No merge hazard with this story (3.2 consumes that surface, doesn't touch it). The 2.9 commit `1595ff2` shipped the windowing seam this story reuses. Branch `main`; this is the second Epic 3 story.

### References

- Story 3.2 ACs (epic): `_bmad-output/planning-artifacts/epics.md:1331-1409`
- Story 3.1 (handoff — SOAP records + InvokeActionAsync, the consumed surface): `_bmad-output/implementation-artifacts/3-1-soap-envelope-builder-fault-parser-and-invokeactionasync-wire-up.md`
- Shipped SOAP records: `src/ohSpy.Core/Models/SoapRequest.cs`, `SoapResponse.cs`, `SoapArgument.cs`; fault carrier `src/ohSpy.Core/Soap/UpnpFault.cs`; facade `src/ohSpy.Core/Http/IUpnpHttpClient.cs:36-40`
- `ServiceDescription` (ControlUrl is a relative-capable string): `src/ohSpy.Core/Models/ServiceDescription.cs`
- 2.9 launcher seam to mirror: `src/ohSpy.Core/ViewModels/IPropertiesLauncher.cs`, `src/ohSpy.App/Windowing/PropertiesLauncher.cs`
- 2.9 VM banner/dispose pattern to copy: `src/ohSpy.Core/ViewModels/PropertiesViewModel.cs` (esp. `OnDeviceRemoved`, `Dispose`, `TryResolve`)
- 2.9 window chrome to reuse: `src/ohSpy.App/Views/PropertiesWindow.xaml` + `.xaml.cs`
- Window ownership (Activate→Adopt, AC-10.5): `src/ohSpy.App/Windowing/WindowOwnershipManager.cs`; Decision 10: `architecture.md:1264-1376` (popup table L1328-1335)
- Tree seams: `src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs`, `ServiceNodeViewModel.cs` (L78-128 `LoadActionsAsync`), `DeviceNodeViewModel.cs` (L53-64 expand), `NodeServices.cs`
- App double-tap handler: `src/ohSpy.App/MainWindow.xaml.cs:120-133`
- DI + ShellWindow injection: `src/ohSpy.App/Composition/ServiceRegistration.cs:101-123`, `src/ohSpy.App/App.xaml.cs:90-93`
- Decision 7 (popup CTS linked to device token; "two mechanisms, one outcome"; cleanup-level-above): `architecture.md:734-873`
- `RegistryEntry` (DeviceToken public, DeviceCts internal): `src/ohSpy.Core/Devices/RegistryEntry.cs:55-80`
- Exceptions (Budget/Elapsed/StatusCode/ErrorCode fields): `src/ohSpy.Core/Http/UpnpExceptions.cs`
- DiagCategories (HttpTimeout/SoapFault/SoapInvoke already exist — no new constant): `src/ohSpy.Core/Diagnostics/DiagCategories.cs:13,50,53`
- Pattern 11 diagnostics + `EmitFailure` precedent: `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs:130-135`; `architecture.md:1003-1076`
- SC-010 (popup interactive ≤ 1 s) / SC-011 (result ≤ 2 s): `architecture.md:1456`
- Canonical source tree (InvocationPopupWindow + VMs placement): `architecture.md:2065-2067, 2136`
- Test fakes to mirror/extend: `tests/ohSpy.Core.Tests/Fakes/FakePropertiesLauncher.cs`, `StubUpnpHttpClient.cs`, `FakeDeviceRegistry.cs`, `CapturingDiagnosticEmitter.cs`, `InlineUiDispatcher.cs`
- Memory: `smoke-per-ui-story` (manual smoke per UI story before review/done), `winui-treeview-datacontext-null` (commit 4d380f8)

### Project structure notes

- New Core VMs land in `src/ohSpy.Core/ViewModels/` (matches arch L2136). New App types in `src/ohSpy.App/Views/` (window) + `src/ohSpy.App/Windowing/` (launcher) — matches arch L2065-2067 and the 2.9 layout. No structural variance from the canonical tree.
- `CoreAppBoundaryTests` is respected: the Core VMs are WinUI-free (no `Visibility`, no `Window`); the boundary-crossing "open a window" verb is the `IInvocationPopupLauncher` seam (interface in Core, impl in App).

### Open questions for the implementer (flagged, non-blocking)

1. **`InvocationResultViewModel` rendering:** `DataTemplateSelector` (richer, matches `NodeDataTemplateSelector`) vs per-type `Visibility` projections in code-behind (simpler, matches `PropertiesWindow`). Either satisfies the ACs — pick the one that reads cleaner for three variants; document the choice.
2. **`Title` shape:** the AC leaves it to judgment. Baked-in recommendation: `"{serviceTail} · {action.Name}"` reusing the `:service:` tail logic. If you prefer the epic's literal `$"{parentService.ServiceId} · {action.Name}"`, that's fine too — document whichever.
3. **VM factory signature:** a 3-arg named delegate (`InvocationPopupViewModelFactory`) reads better than `Func<(ScpdAction, ServiceDescription, RegistryEntry), InvocationPopupViewModel>`. Use whichever the DI block stays clean with.
4. **Argument-less `ResolvedValue`:** confirm the base `ResolvedValue => Value` returns `""` for an untouched input (it does) — a device expecting an empty-string arg should receive `<argName></argName>` (the 3.1 builder handles that). No special-casing needed.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8[1m] (Claude Opus 4.8, 1M context) — BMAD dev-story workflow.

### Debug Log References

- Core build: 0 warnings / 0 errors.
- App build (clean `-t:Rebuild`): 0 errors; ONE warning — the pre-existing benign `WMC1506` on `MainWindow.xaml:141` (Story 2.5 FallbackTemplate). No new warnings introduced. (An incremental App build with the app still running emitted MSB3026/MSB3027 file-lock errors from a stale `ohSpy.App` process holding `ohSpy.Core.dll`; resolved by stopping that process — a stale-process artifact, not a compile error.)
- Full Core suite: **355 passed / 2 skipped / 0 failed** (baseline was 330/2 → +25 new tests). Chaos suite: **1**. `CoreAppBoundaryTests` 4 green; `AsyncDisciplineTests` + `DiagCategoriesUsageTests` green (no new DiagCategories constant added — reused HttpTimeout/SoapFault/SoapInvoke, so the reflection-based usage test needed no edit).

### Completion Notes List

**What shipped (automated surface, all gates green):**

- **`ArgumentInputViewModel`** (Core) — non-sealed text-only base; `string Name`, `[ObservableProperty] string Value = ""`, `virtual string ResolvedValue => Value`. The clean polymorphic seam Story 3.3 extends (list/range subclasses override `ResolvedValue`).
- **`InvocationResultViewModel`** (Core) — abstract record + 3 sealed variants `SuccessResult(IReadOnlyList<SoapArgument>)`, `FaultResult(int,int,string)`, `TransportErrorResult(string)`. Reuses `SoapArgument` for output pairs (no new pair type).
- **`IInvocationPopupLauncher`** (Core seam) + **`InvocationPopupLauncher`** (App impl) — verbatim mirror of `IPropertiesLauncher`/`PropertiesLauncher`: named `InvocationPopupViewModelFactory` delegate (Pattern 7), `Window? ShellWindow` set in `App.OnLaunched`, `Activate()` THEN `Adopt(window, ShellWindow)`.
- **`InvocationPopupViewModel`** (Core, sealed, `IDisposable`) — the test heart. Resolves `_controlUrl` once via guarded `Uri.TryCreate(LocationUrl, ControlUrl)`; `_popupCts` linked to the **public** `RegistryEntry.DeviceToken`; subscribes `IDeviceRegistry.DeviceRemoved` for the `IsDeviceGone` banner; `InvokeAsync` builds `SoapRequest`, maps catch-arms to result variants + emits UUID-bearing Pattern-11 diagnostics; `OperationCanceledException` swallowed (no Result, no diag); Interlocked-guarded `Dispose` cancels → unsubscribes → disposes the CTS.
- **`ActionNodeViewModel`** enriched with `(ScpdAction, ServiceDescription parentService, RegistryEntry parentEntry, NodeServices services)` + `[RelayCommand] OpenInvocationPopup`. `RegistryEntry` threaded `DeviceNode → ServiceNode → ActionNode` (the entry carries LocationUrl+Uuid+DeviceToken in one object). `ServiceNodeViewModel` ctor gained `RegistryEntry parentEntry` (deviceToken stays last per CA1068).
- **`NodeServices`** 7th member `IInvocationPopupLauncher`. DI: factory + dual-reg launcher registered before the `NodeServices` line; `App.OnLaunched` sets the new launcher's `ShellWindow`.
- **`InvocationPopupWindow.xaml/.cs`** (App) — header/title, `ItemsControl` over `Inputs` (two-way `TextBox`) with "No input arguments" hint, Invoke button + ProgressRing/"Invoking…", result area (per-type Visibility projections: no-result / success rows / "Success (no output)" / fault Border (caution) / transport Border (critical)), device-gone banner. Code-behind is constructor-only + `Closed → ViewModel.Dispose()` + `bool/result-type → Visibility` projections (mirror `PropertiesWindow`).
- **`MainWindow.OnTreeDoubleTapped`** — added the `ActionNodeViewModel` branch routing to `OpenInvocationPopupCommand.Execute(null)` (leaf — no expansion toggle), using the same null-DataContext-safe `e.OriginalSource.DataContext` lookup.
- **Tests:** `FakeInvocationPopupLauncher`; `StubUpnpHttpClient` extended with `InvokeResponder` + captured `InvokedRequests`; updated all 4 `NodeServices` ctor sites + the DI reg + `ServiceNodeViewModel`/`ActionNodeViewModel` ctor sites; new `InvocationPopupViewModelTests` (19) + `ArgumentInputViewModelTests` (6). Popup-VM tests assert on the **captured SoapRequest** (resolved absolute ControlUrl, args 1:1) and the produced Result/diagnostic — not on inputs handed in (Epic 2 lesson).

**Four flagged open questions — resolutions:**

1. **Result rendering →** per-type `Visibility` projections in `InvocationPopupWindow.xaml.cs` (matches `PropertiesWindow`; simplest for 3 variants; keeps `Visibility` out of Core). Not a `DataTemplateSelector`.
2. **`Title` shape →** `"{serviceTail} · {action.Name}"` reusing the `:service:` tail logic from `ServiceNodeViewModel.ComputeLabel` (the baked-in recommendation; consistent with the tree). Asserted in `Title_IsServiceTailDotAction_AC322`.
3. **VM factory signature →** a named `InvocationPopupViewModelFactory` delegate (reads cleaner in the DI block than a 3-tuple `Func`).
4. **Argument-less `ResolvedValue` →** confirmed `ResolvedValue => Value` returns `""` for an untouched input; no special-casing (the 3.1 builder emits a self-closing `<argName />`). Asserted in `ResolvedValue_DefaultsToValue_Empty_AC323`.

**Deviations / judgment calls beyond the four questions:**

- **`ServiceNodeViewModel` ctor param order:** placed `RegistryEntry parentEntry` BEFORE `services`, keeping `CancellationToken deviceToken` last (CA1068 / the existing convention). Story said "add a `RegistryEntry parentEntry` param" without pinning position; this preserves CT-last.
- **Duplicate `SoapFault` emit KEPT** (reconciliation #4) — the popup-level emit carries `parentEntry.Uuid`; documented inline in `InvokeAsync` with a "do not delete" note. Test `Invoke_UpnpFault_…WithUuid` asserts `DeviceUuid == parentEntry.Uuid`.
- **`UpnpProtocolException`** (malformed 2xx body, 3.1 review patch) handled on the transport-error arm → `SoapInvoke` diagnostic + `TransportErrorResult` (story listed it among "any other non-fault transport failure").
- **No `Cancel()` method** — the window's `Closed` handler calls `Dispose()` directly (matches `PropertiesWindow.OnClosed`); the story explicitly allowed either.
- **Post-await UI marshalling (smoke-crash fix, 2026-06-03):** the first real invoke crashed the app with `COMException 0x8001010E` (RPC_E_WRONGTHREAD). Root cause: WinUI 3 installs **no SynchronizationContext**, so `await InvokeActionAsync(...)` resumed the continuation on a thread-pool thread; setting `Result`/`IsInvoking` there raised `PropertyChanged` off-thread → the bound window poked `UIElement.Visibility` off-thread → crash. Fix: `ConfigureAwait(false)` + apply the terminal state (`Result` + `IsInvoking = false`) and the OCE-path `IsInvoking = false` via **`_ui.Post(...)`** (Decision 1 / `IUiDispatcher`), mirroring `ServiceNodeViewModel`. The original `InlineUiDispatcher`-based tests passed because inline `Post` masks missing marshalling (Epic 2 "prove it's wired" lesson); added `DeferredUiDispatcher` + `Invoke_Success_MarshalsTerminalStateThroughDispatcher_NotDirectly_AC327` as the regression guard. Recorded as durable knowledge: `winui-no-synccontext-marshal-vm`. Core suite now **356 passed / 2 skipped**; App build still 0 errors / 1 pre-existing `WMC1506`.

**⚠️ Manual smoke steps that remain (Task 10 — NOT executed, headless; story is at `review` with this gate OPEN):**

The human must run these on a live UPnP network (a Linn DS / Sky Hub / any device with invokable actions) before the story moves to `done`:

1. **Open ≤ 1 s (SC-010):** expand device → service → action; **double-click an action row** → the invocation popup opens and is interactive (input fields editable) within ~1 s.
2. **Argument-less success ≤ 2 s (SC-011):** invoke a `GetVolume`-style action → result area shows output rows (or "Success (no output)") within ~2 s on a sub-1 s LAN device.
3. **Action-with-inputs success:** invoke a `SetVolume`-style action with typed args → success rows render; confirm the args you typed went out (device responds correctly).
4. **UPnP fault styling (FR-029):** invoke with a deliberately-bad argument → a **UPnP fault** renders with the caution (warning) Border styling, showing HTTP 500 + error code/description.
5. **Transport error (FR-030, NFR-R3):** point at a dead URL / kill the device mid-invoke → a **transport error** renders with the critical Border styling (visually distinct from the fault); the popup does NOT crash.
6. **Device-gone banner (FR-037):** remove the device (byebye / power off) while the popup is open → the device-gone banner appears; already-shown data stays; the popup stays closeable.
7. **Close mid-invoke (D7):** start a slow invoke, close the popup before it completes → no exception surfaced (the in-flight SOAP call observes cancellation and is swallowed).
8. **FR-046 ownership:** popup sits above main; stays above when main is focused; minimises/restores with main; closes when main closes.

Record device(s) used + outcomes here when run. Until then the smoke gate is OPEN.

### File List

**Created (Core):**
- `src/ohSpy.Core/ViewModels/ArgumentInputViewModel.cs`
- `src/ohSpy.Core/ViewModels/InvocationResultViewModel.cs`
- `src/ohSpy.Core/ViewModels/IInvocationPopupLauncher.cs`
- `src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs`

**Created (App):**
- `src/ohSpy.App/Windowing/InvocationPopupLauncher.cs` (incl. the `InvocationPopupViewModelFactory` delegate)
- `src/ohSpy.App/Views/InvocationPopupWindow.xaml`
- `src/ohSpy.App/Views/InvocationPopupWindow.xaml.cs`

**Created (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/FakeInvocationPopupLauncher.cs`
- `tests/ohSpy.Core.Tests/ViewModels/InvocationPopupViewModelTests.cs`
- `tests/ohSpy.Core.Tests/ViewModels/ArgumentInputViewModelTests.cs`

**Modified (Core):**
- `src/ohSpy.Core/ViewModels/ActionNodeViewModel.cs` (enriched ctor + `OpenInvocationPopup` command)
- `src/ohSpy.Core/ViewModels/ServiceNodeViewModel.cs` (ctor gains `RegistryEntry parentEntry`; builds enriched ActionNodes)
- `src/ohSpy.Core/ViewModels/DeviceNodeViewModel.cs` (passes `_entry` to the ServiceNode ctor)
- `src/ohSpy.Core/ViewModels/NodeServices.cs` (7th member)

**Modified (App):**
- `src/ohSpy.App/Composition/ServiceRegistration.cs` (factory + dual-reg launcher, before NodeServices)
- `src/ohSpy.App/App.xaml.cs` (sets `InvocationPopupLauncher.ShellWindow`)
- `src/ohSpy.App/MainWindow.xaml.cs` (double-tap → action branch)

**Modified (Tests):**
- `tests/ohSpy.Core.Tests/Fakes/StubUpnpHttpClient.cs` (`InvokeResponder` + `InvokedRequests`)
- `tests/ohSpy.Core.Tests/ViewModels/ActionNodeViewModelTests.cs` (new ctor builders + AC-3.2.4 command test)
- `tests/ohSpy.Core.Tests/ViewModels/ServiceNodeViewModelTests.cs` (RegistryEntry threaded into `NewVm`; `MakeNodeServices` 7th arg)
- `tests/ohSpy.Core.Tests/ViewModels/DeviceNodeViewModelTests.cs` (3 NodeServices ctor sites)
- `tests/ohSpy.Core.Tests/ViewModels/DeviceTreeViewModelTests.cs` (NodeServices ctor site)

### Review Findings

- [x] [Review][Patch] No catch-all — unexpected exceptions leave `IsInvoking=true` permanently (NFR-R3) [`src/ohSpy.Core/ViewModels/InvocationPopupViewModel.cs:188`] — **Applied.** Added `catch (Exception ex) when (ex is not OperationCanceledException)` after `UpnpProtocolException` arm; maps to `TransportErrorResult`; no diagnostic (no typed context available). The real `UpnpHttpClient` is exhaustive but future IUpnpHttpClient impls could throw unlisted exceptions.
- [x] [Review][Patch] TextBox inputs not disabled while invoking (AC-3.2.6 #19 / NFR-UI3) [`src/ohSpy.App/Views/InvocationPopupWindow.xaml:73`, `.xaml.cs`] — **Applied.** Added `IsInputEnabled => !ViewModel.IsInvoking` code-behind property + `Raise(nameof(IsInputEnabled))` in `OnViewModelPropertyChanged` + `IsEnabled="{x:Bind IsInputEnabled, Mode=OneWay}"` on the `ItemsControl`. Disabled at ItemsControl level so WinUI `IsEnabled` inheritance propagates to TextBox children. Pattern 2 preserved (no IsEnabled in Core).

### Change Log

- 2026-06-03 — Story 3.2 implemented (dev-story workflow, claude-opus-4-8[1m]). Invocation popup with free-form text inputs: first consumer of the 3.1 SOAP layer + second reuse of the 2.9 popup pattern. +25 Core tests (330→355 passed / 2 skipped). Core 0/0; App clean rebuild 1 pre-existing WMC1506. Chaos=1; CoreAppBoundary/AsyncDiscipline/DiagCategoriesUsage green. Moved in-progress → review. **Task 10 manual UI smoke NOT executed (headless) — smoke gate OPEN; must run on a live device before `done`.**
