namespace ohSpy.Core.Discovery;

using System.Text;
using ohSpy.Core.Diagnostics;

internal sealed class SsdpParser(IDiagnosticEmitter diag)
{
    /// <summary>
    /// Parse a raw SSDP datagram payload. Returns null + emits Warning on malformed.
    /// Both NOTIFY (request-form) and M-SEARCH response (response-form) are accepted.
    /// </summary>
    internal SsdpAnnouncement? Parse(byte[] payload, string remoteEndpoint)
    {
        if (payload.Length == 0)
        {
            EmitParseWarning(remoteEndpoint);
            return null;
        }

        var text = Encoding.UTF8.GetString(payload);
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);

        if (lines.Length == 0)
        {
            EmitParseWarning(remoteEndpoint);
            return null;
        }

        var firstLine = lines[0].Trim();
        bool isNotify = firstLine.StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase);
        bool isMSearchResponse = firstLine.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase);

        if (!isNotify && !isMSearchResponse)
        {
            EmitParseWarning(remoteEndpoint);
            return null;
        }

        string? nt = null, nts = null, st = null, usn = null, server = null, bootId = null, configId = null;
        Uri? location = null;
        TimeSpan? cacheControlMaxAge = null;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break; // blank line = end of headers

            var colonPos = line.IndexOf(':', StringComparison.Ordinal);
            if (colonPos < 0) continue;

            var key = line[..colonPos].Trim();
            var value = line[(colonPos + 1)..].Trim();

            if (key.Equals("NT", StringComparison.OrdinalIgnoreCase))
                nt = value;
            else if (key.Equals("NTS", StringComparison.OrdinalIgnoreCase))
                nts = value;
            else if (key.Equals("ST", StringComparison.OrdinalIgnoreCase))
                st = value;
            else if (key.Equals("USN", StringComparison.OrdinalIgnoreCase))
                usn = value;
            else if (key.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                _ = Uri.TryCreate(value, UriKind.Absolute, out location);
            else if (key.Equals("CACHE-CONTROL", StringComparison.OrdinalIgnoreCase))
                cacheControlMaxAge = ParseMaxAge(value);
            else if (key.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                server = value;
            else if (key.Equals("BOOTID.UPNP.ORG", StringComparison.OrdinalIgnoreCase))
                bootId = value;
            else if (key.Equals("CONFIGID.UPNP.ORG", StringComparison.OrdinalIgnoreCase))
                configId = value;
            // Unknown headers silently ignored (lenient, D4 vendor-noise philosophy)
        }

        var udn = ExtractUdn(usn);

        return new SsdpAnnouncement(nt, nts, st, usn, udn, location, cacheControlMaxAge, server, bootId, configId);
    }

    /// <summary>
    /// Extracts the device UDN (the opaque <c>uuid:&lt;body&gt;</c> token) from an SSDP USN.
    /// UPnP UDNs are opaque strings — RFC 4122 is only a SHOULD — so we do NOT parse to a
    /// <see cref="Guid"/> (Amendment A30). Returns the full <c>uuid:&lt;body&gt;</c> with the
    /// <c>::&lt;nt&gt;</c> suffix stripped and the <c>uuid:</c> prefix + body casing preserved
    /// (matching <c>DeviceDescription.Udn</c>); returns null only when there is no <c>uuid:</c> token.
    /// </summary>
    internal static string? ExtractUdn(string? usn)
    {
        if (usn is null) return null;
        if (!usn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase)) return null;

        // USN forms: "uuid:<body>" or "uuid:<body>::<nt>". Strip the "::<nt>" suffix; keep "uuid:".
        var sepPos = usn.IndexOf("::", StringComparison.Ordinal);
        return sepPos >= 0 ? usn[..sepPos] : usn;
    }

    private static TimeSpan? ParseMaxAge(string value)
    {
        // e.g. "max-age=1800" or "max-age = 1800"
        var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && parts[0].Equals("max-age", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return null;
    }

    private void EmitParseWarning(string remoteEndpoint)
    {
        diag.Warning(DiagCategories.SsdpParse, "ssdp parse failed",
            new DiagnosticContext { RemoteEndpoint = remoteEndpoint });
    }
}
