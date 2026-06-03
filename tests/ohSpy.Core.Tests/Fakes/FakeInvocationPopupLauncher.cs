namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Capturing <see cref="IInvocationPopupLauncher"/>: records each (action, service, entry) tuple
/// handed to <see cref="Open"/> so tests can assert ActionNodeViewModel.OpenInvocationPopupCommand
/// crosses the Core/App seam with the right context (Story 3.2). Mirror of FakePropertiesLauncher.
/// </summary>
internal sealed class FakeInvocationPopupLauncher : IInvocationPopupLauncher
{
    public List<(ScpdAction Action, ServiceDescription Service, RegistryEntry Entry)> Opened { get; } = new();

    public void Open(ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry) =>
        Opened.Add((action, parentService, parentEntry));
}
