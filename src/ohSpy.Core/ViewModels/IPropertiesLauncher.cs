namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Devices;

/// <summary>
/// Core seam for opening the (App-layer) read-only Properties window for a device. Implemented
/// in ohSpy.App (PropertiesLauncher) because constructing a WinUI Window is not a Core concern.
/// Lets DeviceNodeViewModel.OpenPropertiesCommand (Core) trigger the popup across the Core/App
/// boundary (Pattern 2). Story 2.9; Epics 3-5 add sibling popup seams following the same
/// window.Activate()→Adopt() sequence (Decision 10).
/// </summary>
public interface IPropertiesLauncher
{
    /// <summary>Open the read-only Properties window for <paramref name="entry"/> (UI-thread; fire-and-forget).</summary>
    void OpenProperties(RegistryEntry entry);
}
