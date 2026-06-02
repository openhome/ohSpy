namespace ohSpy.Core.Diagnostics;

using ohSpy.Core.Collections;
using ohSpy.Core.Threading;

internal sealed class DiagnosticRingSink : IDiagnosticRingSink
{
    // FR-041 cap.
    private const int Capacity = 5000;

    private readonly IUiDispatcher _dispatcher;
    private readonly IDiagnosticIdentityLookup _identityLookup;

    public DiagnosticRingSink(IUiDispatcher dispatcher, IDiagnosticIdentityLookup identityLookup)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(identityLookup);
        _dispatcher = dispatcher;
        _identityLookup = identityLookup;
        Entries = new BoundedObservableCollection<DiagnosticRow>(Capacity);
    }

    public BoundedObservableCollection<DiagnosticRow> Entries { get; }

    public void Push(DiagnosticEntry entry)
    {
        // FR-041 snapshot semantics: resolve BOTH labels HERE, on the calling thread, so the
        // values reflect the registry / endpoint state AT THIS MOMENT. The resulting
        // DiagnosticRow is immutable; later registry mutations do not affect existing rows.
        var row = new DiagnosticRow(
            entry,
            ResolveIdentityLabel(entry.Context),
            ResolveEndpointLabel(entry.Context));

        // The BoundedObservableCollection is UI-thread-owned; cross-thread mutations would
        // race. Marshal the prepend through the dispatcher. Post (not PostAsync) — we don't
        // need to await; AC-8.8 requires the emitter call returns within 100 µs.
        _dispatcher.Post(() => Entries.PrependNewest(row));
    }

    // FR-041 Identity column resolution (AC-8.3):
    //   null DeviceUuid                                     → "—"
    //   registry hit with friendly name                     → friendly name
    //   registry miss OR registry hit without friendly name → "uuid:<uuid>"
    private string ResolveIdentityLabel(DiagnosticContext ctx)
    {
        if (ctx.DeviceUuid is not { } uuid)
        {
            return "—";
        }
        var name = _identityLookup.TryGetFriendlyName(uuid);
        return name ?? $"uuid:{uuid}";
    }

    // FR-041 Endpoint column resolution (AC-8.4):
    //   parsed URL → host (default port) or host:port (non-default)
    //   fallback to RemoteEndpoint
    //   final fallback "—"
    private static string ResolveEndpointLabel(DiagnosticContext ctx)
    {
        if (!string.IsNullOrEmpty(ctx.Url) && Uri.TryCreate(ctx.Url, UriKind.Absolute, out var uri))
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        if (!string.IsNullOrEmpty(ctx.RemoteEndpoint))
        {
            return ctx.RemoteEndpoint;
        }
        return "—";
    }
}
