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

    // FR-045 service glyph. U+E713 = Segoe MDL2 Assets "Setting" glyph ("service config").
#pragma warning disable CA1822
    public string KindGlyph => "";
#pragma warning restore CA1822

    public ObservableCollection<INodeViewModel> Children { get; } = [];

    // CancellationToken is last per CA1068 / Dev Notes Pattern 6 ("deviceToken, passed last").
    public ServiceNodeViewModel(
        ServiceDescription service, Uri deviceLocation, Guid deviceUuid,
        NodeServices services, CancellationToken deviceToken)
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
        var type = service.ServiceType; // non-nullable record member — no null-guard needed
        var idx = type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tail = type[(idx + marker.Length)..];
            if (tail.Length > 0) return tail;
        }
        if (type.Length > 0) return type;
        return service.ServiceId ?? "(service)";
    }

    // AC-2.6.1 + AC-2.6.6: fire the lazy load exactly once on the first `true` transition.
    partial void OnIsExpandedChanged(bool value)
    {
        if (!value) return; // collapse: no-op (retain loaded actions — AC-2.6.6)
        if (Interlocked.Exchange(ref _loadStarted, 1) == 1) return; // already loading/loaded
        _ = LoadActionsAsync(); // fire-and-forget (EagerDescriptionDispatcher precedent); all
                                // exceptions handled inside; deviceToken drives teardown.
    }

    // AC-2.6.3 / #4 / #5 / #8: fetch the SCPD, stream actions, marshal each to the UI thread.
    // Fetch and parse have SEPARATE try blocks so failures are attributed to the right layer
    // (review F1): a fetch-layer UpnpException — timeout, transport, OR oversize-body
    // UpnpProtocolException — is ScpdFetch; only a parser-layer UpnpProtocolException
    // (malformed/XXE/oversize XML) is ScpdParse. Both layers swallow OperationCanceledException
    // (AC-2.6.8 — cancellation is not a fault).
    private async Task LoadActionsAsync()
    {
        var scpdUrl = new Uri(_deviceLocation, _service.ScpdUrl); // resolves relative OR absolute

        byte[] bytes;
        try
        {
            bytes = await _services.Http.FetchScpdAsync(scpdUrl, _deviceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // device removed mid-fetch; node being dropped, emit nothing.
        }
        catch (UpnpException ex) // timeout, transport, OR oversize-body protocol — all fetch-layer
        {
            EmitFailure(DiagCategories.ScpdFetch, scpdUrl, ex.Message);
            return;
        }

        try
        {
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
        catch (UpnpProtocolException ex) // malformed / XXE / oversize XML at the parser layer
        {
            EmitFailure(DiagCategories.ScpdParse, scpdUrl, ex.Message);
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
}
