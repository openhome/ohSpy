namespace ohSpy.Core.Devices;

/// <summary>
/// Lifecycle of a device's description fetch (Decision 9). <see cref="Pending"/> and
/// <see cref="InFlight"/> are transient; <see cref="Loaded"/> and <see cref="Failed"/>
/// are terminal. Only <see cref="Loaded"/> entries appear in the tree (FR-047).
/// </summary>
public enum DescriptionFetchState
{
    /// <summary>Entry added to the registry; fetch not yet started.</summary>
    Pending,

    /// <summary>HTTP fetch issued; response not yet parsed.</summary>
    InFlight,

    /// <summary>Description fetched + parsed successfully — the only tree-visible state.</summary>
    Loaded,

    /// <summary>Fetch or parse failed terminally.</summary>
    Failed,
}
