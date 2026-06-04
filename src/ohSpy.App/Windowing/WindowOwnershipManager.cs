namespace ohSpy.App.Windowing;

using Microsoft.UI.Xaml;

/// <summary>
/// Tracks the shell→popup relationship for every secondary window and gives popups a free,
/// non-pinned z-order (FR-046, amended 2026-06-04). WinUI 3's <see cref="Window"/> exposes no
/// Owner property (unlike WPF). We deliberately do NOT establish the Win32 owner link
/// (SetWindowLongPtr GWLP_HWNDPARENT): that link forces an owned window to stay ALWAYS above its
/// owner, so clicking the shell could never bring it in front of an open popup. Instead a popup
/// simply opens on top (its <c>Activate()</c> makes it foreground) and then participates in normal
/// z-order — click the shell and it comes forward over the popup. The one ownership behaviour worth
/// keeping, close-with-parent, is wired here explicitly so closing the shell tears down its popups
/// rather than leaving orphaned windows alive.
/// </summary>
public interface IWindowOwnershipManager
{
    /// <summary>
    /// Track <paramref name="child"/> as a popup of <paramref name="parent"/> and wire
    /// close-with-parent. Call AFTER <c>child.Activate()</c> (the child HWND must be realised, and
    /// activating it is what puts the popup on top on open). Every popup creation site (Epics 2-5)
    /// follows window.Activate() THEN Adopt(window, shellWindow).
    /// </summary>
    void Adopt(Window child, Window parent);

    /// <summary>Child windows currently tracked under <paramref name="parent"/> (testability / introspection).</summary>
    IReadOnlyList<IntPtr> GetChildrenOf(Window parent);
}

internal sealed class WindowOwnershipManager : IWindowOwnershipManager
{
    private readonly Dictionary<IntPtr, List<IntPtr>> _ownership = new();

    // CANONICAL POPUP-OPEN PATTERN (Decision 10 / AC-10.1) — follow this VERBATIM at every popup
    // creation site (Epics 2-5):
    //
    //     var window = new XxxPopupWindow(vm);
    //     window.Activate();                       // (1) realise the HWND + put the popup on top
    //     _windowOwnership.Adopt(window, shell);   // (2) THEN track it + wire close-with-parent
    //
    // Activate() (not an owner link) is what makes the popup foreground on open; after that the
    // popup floats freely and the shell can be clicked back in front of it.
    public void Adopt(Window child, Window parent)
    {
        var childHwnd = WinRT.Interop.WindowNative.GetWindowHandle(child);
        var parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(parent);

        if (!_ownership.TryGetValue(parentHwnd, out var children))
            _ownership[parentHwnd] = children = new();
        children.Add(childHwnd);

        // Close-with-parent: when the shell closes, close its popups (no orphaned windows kept alive).
        // One handler per child so each closes exactly itself; unhooked if the child closes first.
        void OnParentClosed(object sender, WindowEventArgs args) => child.Close();
        parent.Closed += OnParentClosed;

        // Prune tracking when the child closes, and stop trying to close an already-gone child.
        child.Closed += (_, _) =>
        {
            parent.Closed -= OnParentClosed;
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
