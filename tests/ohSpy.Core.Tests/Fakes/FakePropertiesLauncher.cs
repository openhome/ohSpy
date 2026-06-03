namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;
using ohSpy.Core.ViewModels;

/// <summary>
/// Capturing <see cref="IPropertiesLauncher"/>: records each entry handed to
/// <see cref="OpenProperties"/> so tests can assert DeviceNodeViewModel.OpenPropertiesCommand
/// crosses the Core/App seam with the right device (Story 2.9).
/// </summary>
internal sealed class FakePropertiesLauncher : IPropertiesLauncher
{
    public List<RegistryEntry> Opened { get; } = new();
    public void OpenProperties(RegistryEntry entry) => Opened.Add(entry);
}
