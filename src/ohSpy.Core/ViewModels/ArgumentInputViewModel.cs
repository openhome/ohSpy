namespace ohSpy.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Models;

/// <summary>
/// One input-argument row in the invocation popup (Story 3.2, FR-026). Wraps an
/// <see cref="ScpdArgument"/> and exposes a free-form text <see cref="Value"/> the operator
/// edits, plus a <see cref="ResolvedValue"/> funnel the SOAP layer reads.
/// <para>
/// This is the <b>polymorphic base</b> Story 3.3 extends: <c>AllowedValueListArgumentViewModel</c>
/// (dropdown over an <c>&lt;allowedValueList&gt;</c>) and <c>AllowedValueRangeArgumentViewModel</c>
/// (numeric step/min/max) subclass it. For that reason this type is NOT sealed, and
/// <see cref="ResolvedValue"/> is the single virtual seam every variant funnels into — a 3.3
/// subclass overrides it to project its constrained selection back to the wire string without
/// the popup VM caring which variant it is. This story is <b>text-only</b>; no
/// <c>&lt;dataType&gt;</c> / state-table parsing happens here (that is Story 3.3).
/// </para>
/// </summary>
public partial class ArgumentInputViewModel : ObservableObject
{
    /// <summary>Argument name as declared in the service's SCPD.</summary>
    public string Name { get; }

    /// <summary>Free-form text value the operator types. Default empty string (FR-026 / FR-031).</summary>
    [ObservableProperty] private string _value = "";

    public ArgumentInputViewModel(ScpdArgument argument)
    {
        Name = argument.Name;
    }

    /// <summary>
    /// The resolved wire value the SOAP envelope receives. The text-only base just returns the
    /// raw <see cref="Value"/>; Story 3.3 subclasses override this to project a constrained
    /// selection (list item / clamped number) to its string form. An untouched input resolves to
    /// <c>""</c> — the 3.1 envelope builder emits a self-closing <c>&lt;argName /&gt;</c> for that,
    /// which is the correct "empty-string argument" wire form (no special-casing needed).
    /// </summary>
    public virtual string ResolvedValue => Value;
}
