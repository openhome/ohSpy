# Addendum — ohSpy

Technical depth captured while drafting the brief. Feeds the PRD and architecture phases. Does not belong in the brief itself.

---

## Prior Art Digest: C:\work\UpnpSpy (Claude + spec-kit)

The user has a working prior implementation. ohSpy is the BMad-driven re-run.

### Tech stack

- **UI**: WinUI 3 (Microsoft.WindowsAppSDK 1.5) with XAML
- **MVVM**: CommunityToolkit.Mvvm (source-gen)
- **Language/Runtime**: C# 11 on .NET 7 (`net7.0-windows` app, `net7.0` core)
- **UPnP**: No third-party library — raw BCL (`System.Net.Sockets`, `System.Net.Http`)
- **DI/Config**: Microsoft.Extensions.{DependencyInjection, Configuration, Logging, Options}
- **Tests**: xUnit + Moq + FluentAssertions
- **Packaging**: MSIX with Windows App Runtime 1.5 (self-contained)
- **Build**: MSBuild / `dotnet` CLI; x64 + ARM64 publish profiles

### Architecture shape

Two-project split: `App` (XAML + composition) and `Core` (models, VM, services).

**Core (async throughout, no UI thread blocking):**
- SSDP transport: two bound sockets per NIC (multicast listener on 0.0.0.0:1900 + ephemeral search socket); datagrams flow into a bounded channel (capacity 4096).
- DiscoveryService: background pump; issues M-SEARCH on startup with MX=3s; filters to `upnp:rootdevice`.
- DeviceRegistry: UUID-keyed, fires DeviceAdded/Updated/Removed via UI dispatcher.
- EagerDescriptionDispatcher: SemaphoreSlim(8) gate; fetches description XML eagerly so the tree shows friendlyName from the start. Device hidden until `FetchState == Loaded` (FR-047).
- ControlClient: SOAP POST + response parse (success or SOAP fault).
- SubscriptionClient + TcpListenerEventCallbackHost: SUBSCRIBE/UNSUBSCRIBE via HTTP verbs; event callback endpoint bound to the selected NIC's IPv4 address + ephemeral TCP port. Avoids HttpListener ACL grief. Hand-rolled `HttpRequestReader` parses NOTIFY. Per-subscription `Channel<EventNotification>` (bounded, capacity 1024). Auto-renew before expiry.

**UI (all mutations via `IDispatcher.Post()`):**
- ShellViewModel: orchestrates tree + log; owns rescan/adapter-switch.
- DeviceTreeViewModel: filtered mirror of registry (Loaded only); sorted-insert keyed on (FriendlyName, Uuid).
- DeviceNodeViewModel: lazy children with "Loading…" placeholder; atomic replacement on SCPD fetch completion.
- SubscriptionPopupViewModel: `BoundedObservableCollection<EventNotification>` (5K newest-first).
- SsdpLogViewModel: `BoundedObservableCollection<SsdpLogEntry>` (10K), Insert(0) newest-first, tail eviction.

### Carry forward

What clearly works in UpnpSpy and should land in ohSpy unchanged:

- Async/await throughout; no `.Result` / `.Wait()`; `ConfigureAwait(false)` used consistently.
- Registry/VM separation (decoupled discovery state from UI state).
- Bounded concurrency on eager fetch (no HTTP saturation during discovery bursts).
- TcpListener eventing without HttpListener ACL hassle (huge ergonomic win).
- Sorted, keyed tree insertion ((FriendlyName, Uuid)).
- Rolling JSON-lines diagnostic file + in-memory ring.

### Root causes of the two complaints

**"Slow responses":**
1. No HTTP timeout override on description/SCPD fetches — hung devices stall the SemaphoreSlim queue at the HttpClient default (~100s).
2. SCPD parsing happens lazily on UI expansion — large SCPDs (e.g., an IGD with 100+ actions) briefly freeze the expand interaction.
3. Event pump processes NOTIFY one-by-one; a bad NOTIFY can block the parser.

**"Unnecessary full-screen repaints":**
1. SSDP log uses `ObservableCollection.Insert(0, entry)` on every datagram — O(N) per insert, no virtualization. Chatty networks produce visible stutter.
2. WinUI `TreeView` has weak item-level tracking — `InsertSorted()` (remove + insert) on label refresh may trigger a subtree redraw.
3. No `ItemsRepeater` / virtual scroll anywhere.
4. Device removal on byebye may cause expanded subtree to redraw.

### Open gaps

Known gaps in UpnpSpy that ohSpy needs to decide on:

- No HTTP timeout override per request.
- No virtual scroll on the SSDP log.
- No lazy/streaming SCPD parse.
- No tooltip on tree rows (Tier 2 deferred).
- No automatic device-name refresh on re-announce.
- No SSDP message filtering in the log view.
- No cross-session state (window layout, last selection).

### Design objectives for ohSpy

Specifically to fix the named complaints:

1. **`ItemsRepeater` / virtualized scrolling** for the SSDP log and any other high-cardinality lists.
2. **Per-request HTTP timeout discipline** with user-visible cancellation of hung requests.
3. **Streaming/incremental SCPD parse** so large action lists don't freeze the expand.
4. **Keyed collection updates** with explicit identity tracking — no rebuild-on-change patterns.
5. **"No UI thread blocking" as an enforced invariant** — verified in tests if possible.

### Reference artifacts in UpnpSpy

Spec artifacts worth lifting / referencing in the ohSpy PRD:

- `specs/001-upnp-spy-discovery/spec.md` — 369 lines, fully fleshed. FR-047 hide-until-loaded, FR-044 chevron affordances, FR-051 row disambiguation, FR-049/048 single-adapter eventing, FR-004 root-device filter. These are battle-tested requirements; the ohSpy PRD can lift them.
- `plan.md` — performance budgets (5s startup discovery, 2s cold SCPD expansion, 100ms warm expansion when eager-fetched).
