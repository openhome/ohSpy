namespace ohSpy.Core.Devices;

using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Scpd;
using ohSpy.Core.Threading;

/// <summary>
/// Bounded-parallelism eager-description fetcher (Decision 9 + Decision 3). Subscribes to
/// the registry's <see cref="DeviceRegistry.EntryNeedsFetch"/> and runs the canonical
/// <see cref="FetchAsync"/> flow for each new entry, capped at
/// <see cref="MaxConcurrentFetches"/> concurrent fetches (NFR-P6 / FR-043).
/// </summary>
internal sealed class EagerDescriptionDispatcher : IDisposable
{
    private const int MaxConcurrentFetches = 8; // NFR-P6 / FR-043

    private readonly SemaphoreSlim _semaphore = new(MaxConcurrentFetches, MaxConcurrentFetches);
    private readonly IUpnpHttpClient _http;
    private readonly IDeviceDescriptionParser _descParser;
    private readonly IUiDispatcher _dispatcher;
    private readonly DeviceRegistry _registry;
    private readonly IDiagnosticEmitter _diag;

    public EagerDescriptionDispatcher(
        IUpnpHttpClient http,
        IDeviceDescriptionParser descParser,
        IUiDispatcher dispatcher,
        DeviceRegistry registry,
        IDiagnosticEmitter diag)
    {
        _http = http;
        _descParser = descParser;
        _dispatcher = dispatcher;
        _registry = registry;
        _diag = diag;
        _registry.EntryNeedsFetch += entry => _ = FetchAsync(entry); // fire-and-forget per new entry
    }

    /// <summary>
    /// Canonical eager-fetch flow (Decision 9). Acquire a concurrency permit, mark in-flight,
    /// fetch + parse the description, and either admit the row (MarkLoaded + DeviceLoaded),
    /// remove it on a UDN mismatch (AC-9.6), stay silent on cancellation (AC-9.7), or mark it
    /// Failed on any other error (FR-047 — stays in the registry, never in the tree).
    /// </summary>
    internal async Task FetchAsync(RegistryEntry entry)
    {
        // The acquire is OUTSIDE the release try/finally: a cancelled wait never acquired a
        // permit, so it must not hit Release() (that would over-release the semaphore).
        try
        {
            await _semaphore.WaitAsync(entry.DeviceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // cancelled before start — the entry is being removed (AC-9.7).
        }

        try
        {
            // Guard: if the device was removed (byebye / adapter switch) while waiting at the
            // semaphore, the token is now cancelled — skip MarkInFlight so the orphaned entry
            // stays in Pending rather than transitioning to InFlight with no further path out.
            _dispatcher.Post(() =>
            {
                if (!entry.DeviceToken.IsCancellationRequested)
                {
                    entry.MarkInFlight();
                }
            });

            var bytes = await _http.FetchDeviceDescriptionAsync(entry.LocationUrl, entry.DeviceToken)
                .ConfigureAwait(false);
            var description = _descParser.Parse(bytes);

            if (!UdnMatches(description.Udn, entry.Uuid))
            {
                // FR-043 mismatched-root backstop (AC-9.6): remove the entry, no MarkLoaded.
                _diag.Information(DiagCategories.DescriptionFetchMismatch, "root udn mismatch",
                    new DiagnosticContext
                    {
                        DeviceUuid = entry.Uuid,
                        Url = entry.LocationUrl.ToString(),
                        ErrorText = $"declared root: {description.Udn}",
                    });
                _dispatcher.Post(() => _registry.Remove(entry.Uuid));
                return;
            }

            _dispatcher.Post(() =>
            {
                entry.MarkLoaded(description);
                _registry.RaiseDeviceLoaded(entry); // admits the row to the tree (FR-005 / FR-047)
            });
        }
        catch (OperationCanceledException) when (entry.DeviceToken.IsCancellationRequested)
        {
            // AC-9.7: caller-initiated cancel (byebye / adapter switch) — silent, no transition,
            // no diagnostic. The registry's remove path handles the rest.
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
            _dispatcher.Post(() => entry.MarkFailed(ex.Message)); // FR-047: stays in registry, not in tree
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Compares a device-description UDN (a <c>uuid:&lt;guid&gt;</c> string) against the SSDP
    /// UUID. Strips the case-insensitive <c>uuid:</c> prefix and parses; any non-match or
    /// parse failure returns false (treated as a mismatch). A naive string compare against
    /// <c>uuid.ToString()</c> would false-mismatch every real device (prefix + hex casing).
    /// </summary>
    internal static bool UdnMatches(string udn, Guid uuid)
    {
        var s = udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? udn[5..] : udn;
        return Guid.TryParse(s, out var parsed) && parsed == uuid;
    }

    /// <summary>Disposes the concurrency semaphore. Invoked by the DI container at app shutdown.</summary>
    public void Dispose() => _semaphore.Dispose();
}
