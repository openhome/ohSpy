namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Capturing <see cref="ISubscriptionPopupLauncher"/>: records each (service, entry) pair handed to
/// <see cref="Open"/> so tests can assert ServiceNodeViewModel.SubscribeCommand crosses the Core/App
/// seam with the right context (Story 4.3). Mirror of <see cref="FakeInvocationPopupLauncher"/>.
/// </summary>
internal sealed class FakeSubscriptionPopupLauncher : ISubscriptionPopupLauncher
{
    public List<(ServiceDescription Service, RegistryEntry Entry)> Opened { get; } = new();

    public void Open(ServiceDescription service, RegistryEntry parentEntry) =>
        Opened.Add((service, parentEntry));
}
