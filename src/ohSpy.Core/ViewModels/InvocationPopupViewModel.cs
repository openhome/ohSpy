namespace ohSpy.Core.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
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
    private readonly ScpdAction _action;
    private readonly ServiceDescription _parentService;
    private readonly IUpnpHttpClient _http;
    private readonly IUiDispatcher _ui;
    private readonly IDiagnosticEmitter _diag;
    private readonly IDeviceRegistry _registry;

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

    public InvocationPopupViewModel(
        ScpdAction action,
        ServiceDescription parentService,
        RegistryEntry parentEntry,
        IUpnpHttpClient http,
        IUiDispatcher ui,
        IDiagnosticEmitter diag,
        IDeviceRegistry registry)
    {
        _action = action;
        _parentService = parentService;
        _http = http;
        _ui = ui;
        _diag = diag;
        _registry = registry;

        _uuid = parentEntry.Uuid;

        // Decision: Title = "{serviceTail} · {action.Name}" — reuse the ":service:" tail logic from
        // ServiceNodeViewModel.ComputeLabel for visual consistency with the tree (open question #2).
        Title = $"{ComputeServiceTail(parentService)} · {action.Name}";

        // Reconciliation #1: ServiceDescription.ControlUrl is a possibly-RELATIVE string;
        // SoapRequest.ControlUrl is an absolute Uri. Resolve once, GUARDED — a malformed control
        // URL must not crash the popup; InvokeAsync short-circuits to a TransportErrorResult.
        _controlUrl = Uri.TryCreate(parentEntry.LocationUrl, parentService.ControlUrl, out var u) ? u : null;

        // FR-026: one input row per declared input arg, in declared order.
        foreach (var arg in action.Inputs)
            Inputs.Add(new ArgumentInputViewModel(arg));

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
