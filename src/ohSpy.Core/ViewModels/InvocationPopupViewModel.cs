namespace ohSpy.Core.ViewModels;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;
using ohSpy.Core.Threading;

/// <summary>
/// Invocation popup ViewModel (Story 3.2, FR-025/026/027). Lists every input argument of an
/// <see cref="ScpdAction"/> as a free-form text input, POSTs the SOAP request on Invoke, and
/// surfaces success outputs / UPnP fault detail / transport errors. The first consumer of
/// Story 3.1's SOAP layer (<see cref="SoapRequest"/> + <see cref="IUpnpHttpClient.InvokeActionAsync"/>)
/// and the second reuse of Story 2.9's popup pattern (CTS linked to the device token + a
/// DeviceRemoved banner + Interlocked-guarded dispose).
/// <para>This is the automated-test heart of Story 3.2; the windowing/XAML layer is App-only.</para>
/// </summary>
public sealed partial class InvocationPopupViewModel : ObservableObject, IDisposable
{
    // Numeric SCPD dataTypes that support a range spinner (AC-3.3.4; epic L1445). Case-insensitive;
    // float/r4/r8/number are deliberately excluded in v1 (they fall to free-form text — PRD §7).
    private static readonly HashSet<string> NumericDataTypes =
        new(StringComparer.OrdinalIgnoreCase) { "ui1", "ui2", "ui4", "i1", "i2", "i4", "int" };

    private readonly ScpdAction _action;
    private readonly ServiceDescription _parentService;
    private readonly RegistryEntry _parentEntry;
    private readonly IUpnpHttpClient _http;
    private readonly IUiDispatcher _ui;
    private readonly IDiagnosticEmitter _diag;
    private readonly IDeviceRegistry _registry;
    private readonly IScpdParser _scpd;

    private readonly Guid _uuid;                 // snapshot for the diagnostic Identity column + banner match
    private readonly Uri? _controlUrl;           // resolved ONCE; null ⇒ malformed → short-circuit to TransportError
    private readonly CancellationTokenSource _popupCts; // D7 popup level, linked to the device token
    private int _disposed;                       // Interlocked-guarded (mirror PropertiesViewModel)

    /// <summary>Header label: the service-type tail (reusing the ":service:" logic) · the action name.</summary>
    public string Title { get; }

    /// <summary>One free-form text input per declared input arg, in SCPD-declared order (FR-026).</summary>
    public ObservableCollection<ArgumentInputViewModel> Inputs { get; } = [];

    /// <summary>Null until Invoke completes; then a Success / Fault / TransportError variant (FR-028/029/030).</summary>
    [ObservableProperty] private InvocationResultViewModel? _result;

    /// <summary>True while a call is in flight (drives "Invoking…" + disables controls; App projects to Visibility/IsEnabled).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InvokeCommand))]
    private bool _isInvoking;

    // ── Device-gone state (AC-3.2.11). No Visibility in Core (Pattern 2) — App-side projection. ──
    [ObservableProperty] private bool _isDeviceGone;
    [ObservableProperty] private string _deviceGoneText = "";

    /// <summary>True while <see cref="InitializeAsync"/> fetches+parses the SCPD state table to upgrade
    /// the ctor's text-only inputs into constrained variants (AC-3.3.1). Drives a "Loading…" hint
    /// (App projects to Visibility). Cleared — always marshalled via <c>_ui.Post</c> — when the load
    /// settles (success or fallback). False for argument-less actions (nothing to load).</summary>
    [ObservableProperty] private bool _isLoadingInputs;

    public InvocationPopupViewModel(
        ScpdAction action,
        ServiceDescription parentService,
        RegistryEntry parentEntry,
        IUpnpHttpClient http,
        IUiDispatcher ui,
        IDiagnosticEmitter diag,
        IDeviceRegistry registry,
        IScpdParser scpd)
    {
        _action = action;
        _parentService = parentService;
        _parentEntry = parentEntry;
        _http = http;
        _ui = ui;
        _diag = diag;
        _registry = registry;
        _scpd = scpd;

        _uuid = parentEntry.Uuid;

        // Decision: Title = "{serviceTail} · {action.Name}" — reuse the ":service:" tail logic from
        // ServiceNodeViewModel.ComputeLabel for visual consistency with the tree (open question #2).
        Title = $"{ComputeServiceTail(parentService)} · {action.Name}";

        // Reconciliation #1: ServiceDescription.ControlUrl is a possibly-RELATIVE string;
        // SoapRequest.ControlUrl is an absolute Uri. Resolve once, GUARDED — a malformed control
        // URL must not crash the popup; InvokeAsync short-circuits to a TransportErrorResult.
        _controlUrl = Uri.TryCreate(parentEntry.LocationUrl, parentService.ControlUrl, out var u) ? u : null;

        // FR-026: one input row per declared input arg, in declared order. These are the text-only
        // FALLBACK (the 3.2 behaviour); InitializeAsync upgrades them to constrained variants once the
        // SCPD state table is fetched+parsed. If init fails, these stay — defensive (AC-3.3.1 #4).
        foreach (var arg in action.Inputs)
            Inputs.Add(new ArgumentInputViewModel(arg));

        // AC-3.3.1 #1: show "Loading…" only when there is something to resolve (an argument-less
        // action has no state-table lookup to do). Set synchronously in the ctor (UI thread) — safe.
        _isLoadingInputs = action.Inputs.Count > 0;

        // D7: link the popup CTS to the PUBLIC device token (DeviceCts is internal). Device removal
        // cancels DeviceToken → cancels this → the in-flight InvokeActionAsync throws OCE (swallowed).
        _popupCts = CancellationTokenSource.CreateLinkedTokenSource(parentEntry.DeviceToken);

        // FR-037 banner (the 2.9 pattern verbatim): DeviceRemoved fires on the UI thread; a UUID
        // match flips IsDeviceGone. IDisposable unsubscribes — without it the singleton registry
        // pins every popup VM ever opened (Story 2.9's hard lesson).
        _registry.DeviceRemoved += OnDeviceRemoved;
    }

    // CanInvoke: false while a call is in flight (re-invoke guard); true otherwise — including for
    // argument-less actions (AC-3.2.2 #7 / AC-3.2.5).
    private bool CanInvoke() => !IsInvoking;

    [RelayCommand(CanExecute = nameof(CanInvoke))]
    private async Task InvokeAsync()
    {
        IsInvoking = true;

        // Guard (reconciliation #1): a malformed/unresolvable control URL never makes a SOAP call.
        if (_controlUrl is null)
        {
            Result = new TransportErrorResult(
                $"Invalid control URL (could not resolve '{_parentService.ControlUrl}' against the device location).");
            IsInvoking = false;
            return;
        }

        // AC-3.3.6: off-step / out-of-range client-side gate. Re-validate every range input and
        // short-circuit BEFORE the first await (synchronous → no marshalling needed) if any is
        // invalid. The inline ValidationError renders next to the offending input (App binds it).
        var invalid = false;
        foreach (var range in Inputs.OfType<AllowedValueRangeArgumentViewModel>())
        {
            range.Validate();
            if (range.ValidationError is not null) invalid = true;
        }
        if (invalid)
        {
            IsInvoking = false;
            return; // no SOAP request fires while a range input is off-step/out-of-range
        }

        var req = new SoapRequest(
            _controlUrl,
            _parentService.ServiceType, // already a URN string
            _action.Name,
            Inputs.Select(i => new SoapArgument(i.Name, i.ResolvedValue)).ToList());

        // ⚠️ THREADING (smoke-crash regression, 2026-06-03): the continuation AFTER this await runs
        // on a thread-pool thread — WinUI 3 does not install a SynchronizationContext that
        // ConfigureAwait(true) could capture, so the await does NOT resume on the UI thread. Every
        // observable-state mutation below therefore MUST be marshalled to the UI thread via
        // _ui.Post (Decision 1 / IUiDispatcher), exactly as ServiceNodeViewModel does for its
        // streamed Children. Setting Result/IsInvoking directly off-thread makes the bound window
        // poke UIElement.Visibility from the wrong thread → COMException 0x8001010E
        // (RPC_E_WRONGTHREAD) → unhandled → process crash. (Pre-await mutations above are safe:
        // the RelayCommand body runs on the UI thread up to the first await.)
        InvocationResultViewModel result;
        try
        {
            var resp = await _http.InvokeActionAsync(req, _popupCts.Token).ConfigureAwait(false);
            result = new SuccessResult(resp.OutputArguments);
        }
        catch (OperationCanceledException)
        {
            // AC-3.2.10 / #11: popup close or device-gone cancellation. NOT a fault — no Result,
            // no diagnostic (mirror the 3.1 / ServiceNodeViewModel cancellation convention). Still
            // clear the in-flight flag, marshalled to the UI thread.
            _ui.Post(() => IsInvoking = false);
            return;
        }
        catch (UpnpTimeoutException ex)
        {
            // Pattern 11: emit BEFORE setting Result; structured context; never interpolate context
            // into the message. DiagCategories.HttpTimeout already exists (no new constant).
            // (_diag is thread-safe — emit stays off-thread; only the VM-state apply is marshalled.)
            _diag.Warning(DiagCategories.HttpTimeout, "SOAP invoke timed out",
                new DiagnosticContext
                {
                    DeviceUuid = _uuid,
                    Url = _controlUrl.ToString(),
                    ActionName = _action.Name,
                    Elapsed = ex.Elapsed,
                    Budget = ex.Budget,
                });
            result = new TransportErrorResult(BuildTransportMessage(_controlUrl, null, ex.Message));
        }
        catch (UpnpFaultException ex)
        {
            // Reconciliation #4 — INTENTIONAL DUPLICATE of the 3.1 http-layer SoapFault emit.
            // The 3.1 emit (inside UpnpHttpClient) carries DeviceUuid = null (the http layer has no
            // UUID). THIS popup-level emit carries parentEntry.Uuid — the operator-facing identity the
            // FR-041 Diagnostics viewer's Identity column needs. The two coexist by design; do NOT
            // "fix" the duplication by deleting this one — it is the useful, UUID-bearing emit.
            _diag.Warning(DiagCategories.SoapFault, "SOAP action returned a UPnP fault",
                new DiagnosticContext
                {
                    DeviceUuid = _uuid,
                    Url = _controlUrl.ToString(),
                    ActionName = _action.Name,
                    StatusCode = 500,
                    ErrorText = $"{ex.ErrorCode}: {ex.ErrorDescription}",
                });
            result = new FaultResult(500, ex.ErrorCode, ex.ErrorDescription);
        }
        catch (UpnpTransportException ex)
        {
            _diag.Warning(DiagCategories.SoapInvoke, "SOAP invoke transport failure",
                new DiagnosticContext
                {
                    DeviceUuid = _uuid,
                    Url = _controlUrl.ToString(),
                    ActionName = _action.Name,
                    StatusCode = ex.StatusCode,
                    ErrorText = ex.Message,
                });
            result = new TransportErrorResult(BuildTransportMessage(_controlUrl, ex.StatusCode, ex.Message));
        }
        catch (UpnpProtocolException ex)
        {
            // Malformed 2xx body (3.1 review patch) — a transport-class failure, no StatusCode.
            _diag.Warning(DiagCategories.SoapInvoke, "SOAP invoke protocol failure",
                new DiagnosticContext
                {
                    DeviceUuid = _uuid,
                    Url = _controlUrl.ToString(),
                    ActionName = _action.Name,
                    ErrorText = ex.Message,
                });
            result = new TransportErrorResult(BuildTransportMessage(_controlUrl, null, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NFR-R3 defensive catch: an unexpected exception from the HTTP layer (e.g. a
            // future IUpnpHttpClient impl throwing something unlisted) must not leave
            // IsInvoking=true forever and must not crash the popup. Show as a transport error
            // (no diagnostic — no typed context is available for an unknown failure). The
            // OperationCanceledException guard above already handled OCE — exclude it here.
            result = new TransportErrorResult($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }

        // Apply the terminal UI state atomically on the UI thread (the code above ran off-thread).
        _ui.Post(() =>
        {
            Result = result;
            IsInvoking = false;
        });
    }

    /// <summary>
    /// Story 3.3 async init (AC-3.3.1): fetch the parent service's SCPD, parse its state table, and
    /// REBUILD <see cref="Inputs"/> with constrained variants (list dropdown / numeric range) resolved
    /// from each argument's related state variable. Kicked off (fire-and-forget) by the App launcher
    /// AFTER the window is constructed + activated. All failures are handled inside — the launcher
    /// never observes an exception.
    /// <para>
    /// ⚠️ THREADING (the Story 3.2 smoke-crash class, <c>winui-no-synccontext-marshal-vm</c>): WinUI 3
    /// installs no SynchronizationContext, so the continuation AFTER each await resumes on a
    /// thread-pool thread — even with ConfigureAwait(true). Mutating <see cref="Inputs"/> /
    /// <see cref="IsLoadingInputs"/> there pokes bound UIElements off-thread → RPC_E_WRONGTHREAD →
    /// process crash. EVERY post-await observable mutation below is therefore marshalled via
    /// <c>_ui.Post</c>. The pure <see cref="ResolveInput"/> projection (reads the table, news up VMs)
    /// is thread-safe and runs off-thread; <c>_diag</c> is thread-safe too, so the ScpdParse emit may
    /// stay off-thread. Copy of <c>InvokeAsync</c>'s terminal-marshal shape.
    /// </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_action.Inputs.Count == 0)
            return; // nothing to resolve — IsLoadingInputs is already false from the ctor

        Uri scpdUrl;
        try
        {
            scpdUrl = new Uri(_parentEntry.LocationUrl, _parentService.ScpdUrl);
        }
        catch (UriFormatException)
        {
            // Malformed SCPD URL — keep the ctor's text inputs (AC-3.3.1 #2), clear the flag (marshalled).
            _ui.Post(() => IsLoadingInputs = false);
            return;
        }

        try
        {
            var bytes = await _http.FetchScpdAsync(scpdUrl, _popupCts.Token).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes); // caller owns the stream — the parser does not dispose it
            var table = await _scpd.ReadStateTableAsync(ms, _popupCts.Token).ConfigureAwait(false);

            // Pure projection — safe off-thread; per-arg malformed cases emit ScpdParse here (_diag thread-safe).
            var resolved = _action.Inputs.Select(a => ResolveInput(a, table, scpdUrl)).ToList();

            // ⚠️ marshal the COLLECTION rebuild + flag clear (the only observable mutation).
            _ui.Post(() =>
            {
                Inputs.Clear();
                foreach (var input in resolved)
                    Inputs.Add(input);
                IsLoadingInputs = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Popup close / device gone (AC-3.3.1 #4): swallow — no diagnostic, no rebuild. Just clear
            // the flag (marshalled). Mirrors InvokeAsync / LoadActionsAsync cancellation convention.
            _ui.Post(() => IsLoadingInputs = false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fetch or parse failed entirely (AC-3.3.1 #4): keep the ctor's text inputs, emit one
            // ScpdParse warning, clear the flag (marshalled). Broad NFR-R3 defensive catch.
            EmitScpdParse(scpdUrl, ex.Message);
            _ui.Post(() => IsLoadingInputs = false);
        }
    }

    /// <summary>
    /// Pure variant resolver (AC-3.3.2..3.3.9): given a SCPD argument + the parsed state table, returns
    /// the constrained <see cref="ArgumentInputViewModel"/> subclass, or the free-form text base when no
    /// usable constraint applies. Per-arg malformed cases emit a <c>ScpdParse</c> warning (the rest of
    /// the inputs still resolve normally). Thread-safe (reads the table, news up VMs, emits via the
    /// thread-safe <c>_diag</c>) — safe to run inside <see cref="InitializeAsync"/>'s off-thread Select.
    /// Open Question #1 resolved: a private method on the VM (the resolution is only ever needed here;
    /// the popup-VM tests cover it end-to-end through InitializeAsync).
    /// </summary>
    private ArgumentInputViewModel ResolveInput(ScpdArgument arg, ScpdStateTable table, Uri scpdUrl)
    {
        // Miss → free-form text, no diagnostic (a name mismatch is legitimate, not malformed; AC-3.3.9).
        if (!table.ByName.TryGetValue(arg.RelatedStateVariable, out var sv))
            return new ArgumentInputViewModel(arg);

        // FR-102 list variant. Wins even if a range is ALSO declared (malformed per UDA — AC-3.3.8).
        if (sv.AllowedValueList is { Count: > 0 } list)
        {
            if (sv.AllowedValueRange is not null)
                EmitScpdParse(scpdUrl, $"State variable '{sv.Name}' declares both <allowedValueList> and <allowedValueRange>; list wins (FR-102).");
            return new AllowedValueListArgumentViewModel(arg, list, sv.DefaultValue);
        }

        // Present-but-empty list → text + ScpdParse (AC-3.3.3).
        if (sv.AllowedValueList is { Count: 0 })
        {
            EmitScpdParse(scpdUrl, $"State variable '{sv.Name}' declares an empty <allowedValueList>; falling back to free-form text.");
            return new ArgumentInputViewModel(arg);
        }

        // FR-103 range variant — requires a numeric dataType AND a coherent min/max/step (AC-3.3.4/.7).
        if (sv.AllowedValueRange is { } r)
        {
            var numeric = NumericDataTypes.Contains(sv.DataType);
            var coherent = r.Minimum <= r.Maximum && r.Step is null or > 0;
            if (numeric && coherent)
                return new AllowedValueRangeArgumentViewModel(arg, r.Minimum, r.Maximum, r.Step, sv.DefaultValue);

            EmitScpdParse(scpdUrl,
                FormattableString.Invariant(
                    $"State variable '{sv.Name}' has an unusable <allowedValueRange> (dataType '{sv.DataType}', min {r.Minimum}, max {r.Maximum}, step {(r.Step is { } st ? st.ToString(CultureInfo.InvariantCulture) : "<none>")}); falling back to free-form text."));
            return new ArgumentInputViewModel(arg);
        }

        // Neither constraint → free-form text, no diagnostic (AC-3.3.9 — not malformed).
        return new ArgumentInputViewModel(arg);
    }

    // AC-3.3.1/.3/.7/.8 — structured ScpdParse warning (Pattern 11; never interpolate context into the
    // message). DiagCategories.ScpdParse already exists (no new constant). _diag is thread-safe.
    private void EmitScpdParse(Uri scpdUrl, string detail) =>
        _diag.Warning(DiagCategories.ScpdParse, "SCPD state-table input resolution failed",
            new DiagnosticContext
            {
                DeviceUuid = _uuid,
                Url = scpdUrl.ToString(),
                ServiceId = _parentService.ServiceId,
                ErrorText = detail,
            });

    private void OnDeviceRemoved(Guid uuid)
    {
        if (uuid != _uuid || IsDeviceGone) return; // ignore other devices; idempotent
        DeviceGoneText = $"Device left the network at {DateTime.Now:HH:mm:ss}";
        IsDeviceGone = true; // already-shown data stays; the banner appears (XAML binds Visibility)
    }

    /// <summary>
    /// Cancel any in-flight invocation and release the registry subscription + CTS. Called by the
    /// window's Closed handler (mirror PropertiesWindow.OnClosed → ViewModel.Dispose()). Idempotent.
    /// Cleanup ordering (AC-7.4): cancel → unsubscribe → dispose the CTS. No GENA unsubscribe here
    /// (that is Epic 4) — popup close is a pure cancel-and-dispose; nothing needs the level-above token.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _popupCts.Cancel();
        _registry.DeviceRemoved -= OnDeviceRemoved;
        _popupCts.Dispose();
    }

    // Service-type tail after ":service:" (e.g. "RenderingControl:1"), falling back to the verbatim
    // serviceType then serviceId. Same logic as ServiceNodeViewModel.ComputeLabel (consistency).
    private static string ComputeServiceTail(ServiceDescription service)
    {
        const string marker = ":service:";
        var type = service.ServiceType;
        var idx = type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tail = type[(idx + marker.Length)..];
            if (tail.Length > 0) return tail;
        }
        if (type.Length > 0) return type;
        return service.ServiceId ?? "(service)";
    }

    private static string BuildTransportMessage(Uri url, int? statusCode, string detail) =>
        statusCode is { } code
            ? $"{url} (HTTP {code}): {detail}"
            : $"{url}: {detail}";
}
