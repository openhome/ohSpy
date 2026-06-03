namespace ohSpy.App.Windowing;

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

/// <summary>
/// Establishes the Win32 owner relationship (FR-046) for every secondary window. WinUI 3's
/// <see cref="Window"/> exposes no Owner property (unlike WPF), so the four FR-046 behaviours
/// (z-order above parent, no-push-behind on focus, minimise/restore together, close-with-parent)
/// are delivered by SetWindowLongPtr(GWLP_HWNDPARENT) — centralised here so the contract is a
/// pattern, not boilerplate (Decision 10).
/// </summary>
public interface IWindowOwnershipManager
{
    /// <summary>
    /// Establish FR-046 ownership of <paramref name="child"/> by <paramref name="parent"/>.
    /// MUST be called AFTER <c>child.Activate()</c> — calling SetWindowLongPtr before Activate
    /// leaves the relationship undefined in WinUI 3 (empirically required; AC-10.1). Every popup
    /// creation site (Epics 2-5) follows window.Activate() THEN Adopt(window, shellWindow).
    /// </summary>
    void Adopt(Window child, Window parent);

    /// <summary>Child windows currently owned by <paramref name="parent"/> (testability / introspection).</summary>
    IReadOnlyList<IntPtr> GetChildrenOf(Window parent);
}

internal sealed partial class WindowOwnershipManager : IWindowOwnershipManager
{
    private const int GWLP_HWNDPARENT = -8;
    private readonly Dictionary<IntPtr, List<IntPtr>> _ownership = new();

    // Source-generated P/Invoke (.NET 7+). SetWindowLongPtrW is the wide (Unicode) entry point;
    // IntPtr is the correct pointer-sized type on both x64 and ARM64.
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // CANONICAL POPUP-OPEN PATTERN (Decision 10 / AC-10.1) — follow this VERBATIM at every popup
    // creation site (Epics 2-5):
    //
    //     var window = new XxxPopupWindow(vm);
    //     window.Activate();                       // (1) realise the HWND
    //     _windowOwnership.Adopt(window, shell);   // (2) THEN establish FR-046 ownership
    //
    // The order is LOAD-BEARING: SetWindowLongPtr(GWLP_HWNDPARENT) before Activate() leaves the
    // owner relationship undefined in WinUI 3 (the child HWND is not fully realised until Activate).
    public void Adopt(Window child, Window parent)
    {
        var childHwnd = WinRT.Interop.WindowNative.GetWindowHandle(child);
        var parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(parent);

        // FR-046: the OS owner relationship. After this the OS delivers z-order, no-push-behind,
        // minimise/restore-with-parent, and close-with-parent for free — no event handlers needed.
        SetWindowLongPtr(childHwnd, GWLP_HWNDPARENT, parentHwnd);

        if (!_ownership.TryGetValue(parentHwnd, out var children))
            _ownership[parentHwnd] = children = new();
        children.Add(childHwnd);

        // Prune tracking when the child closes (the OS has already torn down the owner link).
        child.Closed += (_, _) =>
        {
            if (_ownership.TryGetValue(parentHwnd, out var list))
                list.Remove(childHwnd);
        };
    }

    public IReadOnlyList<IntPtr> GetChildrenOf(Window parent)
    {
        var parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(parent);
        return _ownership.TryGetValue(parentHwnd, out var children)
            ? children.AsReadOnly()
            : Array.Empty<IntPtr>();
    }
}
