namespace ohSpy.App.Windowing;

using Microsoft.UI.Xaml;
using ohSpy.App.Views;
using ohSpy.Core.Devices;
using ohSpy.Core.ViewModels;

/// <summary>
/// App-side <see cref="IPropertiesLauncher"/>: constructs the PropertiesViewModel via the
/// Pattern-7 factory, news up the PropertiesWindow, and applies the canonical D10 popup-open
/// sequence (Activate THEN Adopt). The shell window is injected post-construction by
/// App.OnLaunched (the MainWindow is created there, not in DI).
/// </summary>
internal sealed class PropertiesLauncher : IPropertiesLauncher
{
    private readonly Func<RegistryEntry, PropertiesViewModel> _vmFactory;
    private readonly IWindowOwnershipManager _ownership;

    /// <summary>The main window, set once in App.OnLaunched. Parent for FR-046 ownership.</summary>
    public Window? ShellWindow { get; set; }

    public PropertiesLauncher(
        Func<RegistryEntry, PropertiesViewModel> vmFactory, IWindowOwnershipManager ownership)
    {
        _vmFactory = vmFactory;
        _ownership = ownership;
    }

    public void OpenProperties(RegistryEntry entry)
    {
        var vm = _vmFactory(entry);
        var window = new PropertiesWindow(vm);
        window.Activate();                                   // (1) D10: MUST precede Adopt
        if (ShellWindow is not null)
            _ownership.Adopt(window, ShellWindow);           // (2) FR-046 ownership (AC-10.5)
    }
}
