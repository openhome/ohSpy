namespace ohSpy.Core.Models;

/// <summary>
/// A single UPnP service exposed by a device. URIs (<see cref="ScpdUrl"/>,
/// <see cref="ControlUrl"/>, <see cref="EventSubUrl"/>) may be relative to the device
/// description's URLBase / location URL — the parser stores them verbatim; resolution
/// is the caller's concern.
/// </summary>
public sealed record ServiceDescription(
    string ServiceType,
    string ServiceId,
    string ScpdUrl,
    string ControlUrl,
    string EventSubUrl);
