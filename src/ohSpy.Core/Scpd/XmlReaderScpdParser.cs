namespace ohSpy.Core.Scpd;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using ohSpy.Core.Http;
using ohSpy.Core.Models;

/// <summary>
/// <see cref="XmlReader"/>-backed implementation of <see cref="IScpdParser"/>. Uses
/// <see cref="XmlReader.ReadAsync"/> for incremental parse + <see cref="Task.Yield"/>
/// between emitted actions (FR-100 / AC-5.1 / Perf Budget §6 cold-large-SCPD ≤ 2 s).
/// </summary>
internal sealed class XmlReaderScpdParser : IScpdParser
{
    // Placeholder URI used when constructing UpnpProtocolException — parsers don't know
    // the source URL (they take a Stream, not a Uri). Callers are encouraged to catch +
    // re-throw with their known Uri context (see consumer pattern in Story 2.6).
    private static readonly Uri PlaceholderUri = new Uri("about:blank");

    public async IAsyncEnumerable<ScpdAction> StreamActionsAsync(
        Stream xml, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(xml, UpnpXmlReaderSettings.Create());

        // Yield-outside-try pattern: C# forbids `yield return` inside try-with-catch.
        // Loop reads ONE action into a local under a try (catching XmlException from any
        // reader call, including ReadElementContentAsStringAsync), then yields outside.
        // OperationCanceledException flows through unwrapped — caller-driven cancel is
        // not a protocol error.
        while (true)
        {
            ScpdAction? action;
            bool eof;
            try
            {
                (action, eof) = await TryReadNextActionAsync(reader, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;  // caller cancel — propagate as-is (AC-7)
            }
            catch (XmlException ex)
            {
                throw new UpnpProtocolException(PlaceholderUri, $"SCPD XML parse failed: {ex.Message}");
            }

            if (eof) yield break;
            if (action is null) continue;   // non-<action> element skipped

            yield return action;
            await Task.Yield();   // FR-100: let UI thread breathe between actions
        }
    }

    // Advances the reader until the next <action> element, reads it, returns
    // (action, eof=false). If end-of-document is hit first, returns (null, eof=true).
    private static async Task<(ScpdAction? action, bool eof)> TryReadNextActionAsync(
        XmlReader reader, CancellationToken ct)
    {
        while (await ReadSafeAsync(reader, ct).ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "action")
            {
                var action = await ReadActionAsync(reader, ct).ConfigureAwait(false);
                return (action, false);
            }
        }
        return (null, true);
    }

    public async Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(xml, UpnpXmlReaderSettings.Create());

        var byName = new Dictionary<string, ScpdStateVariable>(StringComparer.Ordinal);
        try
        {
            while (await ReadSafeAsync(reader, ct).ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "stateVariable")
                {
                    var sv = await ReadStateVariableAsync(reader, ct).ConfigureAwait(false);
                    byName[sv.Name] = sv;   // last-wins on duplicate name (UPnP spec is silent; lenient)
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;  // caller cancel — propagate as-is
        }
        catch (XmlException ex)
        {
            throw new UpnpProtocolException(PlaceholderUri, $"SCPD XML parse failed: {ex.Message}");
        }
        return new ScpdStateTable(byName);
    }

    // ── helpers ──

    // Wrap reader.ReadAsync so XmlException becomes UpnpProtocolException with consistent
    // shape. The caller's CT cancellation flows through OperationCanceledException unwrapped.
    // NOTE: ReadElementContentAsStringAsync can ALSO throw XmlException — those are caught
    // by the outer try/catch in StreamActionsAsync / ReadStateTableAsync, NOT here.
    private static async Task<bool> ReadSafeAsync(XmlReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await reader.ReadAsync().ConfigureAwait(false);
        }
        catch (XmlException ex)
        {
            throw new UpnpProtocolException(PlaceholderUri, $"SCPD XML parse failed: {ex.Message}");
        }
    }

    private static async Task<ScpdAction> ReadActionAsync(XmlReader reader, CancellationToken ct)
    {
        // Reader is positioned on <action>. Children: <name>, <argumentList><argument>*</argumentList>.
        // CONTROL-FLOW: ReadElementContentAsStringAsync advances the reader past the matching
        // end-element. We track this with `advanced` so the outer loop's Read doesn't skip
        // the next sibling.
        string? name = null;
        var inputs = new List<ScpdArgument>();
        var outputs = new List<ScpdArgument>();

        // Empty <action/> self-closing — nothing to read.
        if (reader.IsEmptyElement)
        {
            throw new UpnpProtocolException(PlaceholderUri, "SCPD action missing <name>");
        }

        // Read children until matching </action>.
        var depth = reader.Depth;
        var advanced = false;
        while (advanced || await ReadSafeAsync(reader, ct).ConfigureAwait(false))
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName)
            {
                case "name":
                    name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    advanced = true;
                    break;
                case "argument":
                    var arg = await ReadArgumentAsync(reader, ct).ConfigureAwait(false);
                    (arg.Direction == ScpdDirection.In ? inputs : outputs).Add(arg);
                    break;
                // argumentList is the parent of <argument>; we skip the wrapper (default).
            }
        }
        if (name is null)
            throw new UpnpProtocolException(PlaceholderUri, "SCPD action missing <name>");
        return new ScpdAction(name, inputs, outputs);
    }

    private static async Task<ScpdArgument> ReadArgumentAsync(XmlReader reader, CancellationToken ct)
    {
        string? name = null;
        string? related = null;
        ScpdDirection? direction = null;

        if (reader.IsEmptyElement)
        {
            throw new UpnpProtocolException(PlaceholderUri, "SCPD argument missing name / direction / relatedStateVariable");
        }

        var depth = reader.Depth;
        var advanced = false;
        while (advanced || await ReadSafeAsync(reader, ct).ConfigureAwait(false))
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.LocalName)
            {
                case "name":
                    name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    advanced = true;
                    break;
                case "relatedStateVariable":
                    related = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    advanced = true;
                    break;
                case "direction":
                    var dir = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    direction = string.Equals(dir, "in", StringComparison.OrdinalIgnoreCase)
                        ? ScpdDirection.In
                        : ScpdDirection.Out;
                    advanced = true;
                    break;
            }
        }
        if (name is null || related is null || direction is null)
            throw new UpnpProtocolException(PlaceholderUri, "SCPD argument missing name / direction / relatedStateVariable");
        return new ScpdArgument(name, related, direction.Value);
    }

    private static async Task<ScpdStateVariable> ReadStateVariableAsync(XmlReader reader, CancellationToken ct)
    {
        string? name = null;
        string? dataType = null;
        string? defaultValue = null;
        List<string>? allowedList = null;
        ScpdAllowedValueRange? allowedRange = null;

        if (reader.IsEmptyElement)
        {
            throw new UpnpProtocolException(PlaceholderUri, "SCPD stateVariable missing name / dataType");
        }

        var depth = reader.Depth;
        var advanced = false;
        while (advanced || await ReadSafeAsync(reader, ct).ConfigureAwait(false))
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.LocalName)
            {
                case "name":
                    name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    advanced = true;
                    break;
                case "dataType":
                    dataType = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    advanced = true;
                    break;
                case "defaultValue":
                    defaultValue = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    advanced = true;
                    break;
                case "allowedValueList":
                    allowedList = await ReadAllowedValueListAsync(reader, ct).ConfigureAwait(false);
                    break;
                case "allowedValueRange":
                    allowedRange = await ReadAllowedValueRangeAsync(reader, ct).ConfigureAwait(false);
                    break;
            }
        }
        if (name is null || dataType is null)
            throw new UpnpProtocolException(PlaceholderUri, "SCPD stateVariable missing name / dataType");
        return new ScpdStateVariable(name, dataType, defaultValue, allowedList, allowedRange);
    }

    private static async Task<List<string>> ReadAllowedValueListAsync(XmlReader reader, CancellationToken ct)
    {
        var values = new List<string>();

        if (reader.IsEmptyElement) return values;

        var depth = reader.Depth;
        var advanced = false;
        while (advanced || await ReadSafeAsync(reader, ct).ConfigureAwait(false))
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "allowedValue")
            {
                values.Add(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false));
                advanced = true;
            }
        }
        return values;
    }

    private static async Task<ScpdAllowedValueRange> ReadAllowedValueRangeAsync(XmlReader reader, CancellationToken ct)
    {
        double? min = null, max = null, step = null;

        if (reader.IsEmptyElement)
        {
            throw new UpnpProtocolException(PlaceholderUri, "SCPD allowedValueRange missing minimum / maximum");
        }

        var depth = reader.Depth;
        var advanced = false;
        while (advanced || await ReadSafeAsync(reader, ct).ConfigureAwait(false))
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            // Capture LocalName BEFORE ReadElementContentAsStringAsync — that call advances
            // the reader past the end-element, after which LocalName is stale / wrong.
            var localName = reader.LocalName;
            var text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            advanced = true;   // ReadElementContentAsString advances past </element>
            switch (localName)
            {
                case "minimum":
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minV))
                        min = minV;
                    break;
                case "maximum":
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxV))
                        max = maxV;
                    break;
                case "step":
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var stepV))
                        step = stepV;
                    break;
            }
        }
        if (min is null || max is null)
            throw new UpnpProtocolException(PlaceholderUri, "SCPD allowedValueRange missing minimum / maximum");
        return new ScpdAllowedValueRange(min.Value, max.Value, step);   // AC-5.5: step is null when omitted
    }
}
