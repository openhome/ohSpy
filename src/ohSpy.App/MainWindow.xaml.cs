using Microsoft.UI.Xaml;
using ohSpy.Core.ViewModels;

namespace ohSpy.App;

// Pattern 13: constructor-only code-behind; all logic in VM.
public sealed partial class MainWindow : Window
{
    // Exposed as a typed property so x:Bind in XAML can reference it at compile time.
    public ShellViewModel ViewModel { get; }

    public MainWindow(ShellViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
    }
}
