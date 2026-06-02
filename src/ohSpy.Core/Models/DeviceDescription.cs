namespace ohSpy.Core.Models;

/// <summary>
/// Parsed device description XML. Root device metadata plus a FLATTENED service list
/// containing services from the root device AND all recursively embedded devices
/// (FR-053 three-layer enforcement: only roots are registered; embedded children
/// flatten into the root's service list).
/// <para>
/// All optional fields are nullable. <see cref="Udn"/> (= UPnP <c>&lt;UDN&gt;</c> on the
/// root device) is the load-bearing identity field — consumers compare it against the
/// SSDP USN UUID for AC-9.6 mismatched-root backstop.
/// </para>
/// </summary>
public sealed record DeviceDescription(
    string FriendlyName,
    string DeviceType,
    string Udn,
    string? PresentationUrl,
    string Manufacturer,
    string? ManufacturerUrl,
    string ModelName,
    string? ModelNumber,
    string? ModelDescription,
    string? ModelUrl,
    string? SerialNumber,
    string? Upc,
    IReadOnlyList<ServiceDescription> Services);
