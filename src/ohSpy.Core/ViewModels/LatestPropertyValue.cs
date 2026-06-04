namespace ohSpy.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// One row in the <see cref="SubscriptionPopupViewModel.LatestPropertyValues"/> "Latest property
/// values" summary (Story 4.3, AC-4.3.1; Dev Notes §4). <see cref="Name"/> is immutable (the
/// evented property name); <see cref="Value"/> is overwrite-in-place (last-write-wins) so the bound
/// row text updates without reshuffling the panel. All mutations are marshalled via
/// <c>IUiDispatcher.Post</c> by the VM (§0) — this row is not thread-safe.
/// </summary>
public sealed partial class LatestPropertyValue : ObservableObject
{
    /// <summary>The evented property name (stable; the row is keyed on it, append-on-first-seen).</summary>
    public string Name { get; }

    /// <summary>The most-recent value for <see cref="Name"/> (overwrite-in-place on each NOTIFY).</summary>
    [ObservableProperty] private string _value;

    public LatestPropertyValue(string name, string value)
    {
        Name = name;
        _value = value;
    }
}
