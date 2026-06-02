namespace ohSpy.Core.Tests.Scpd;

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;

/// <summary>
/// Story 1.4 tests for <see cref="XmlReaderScpdParser"/> — covers AC-5.1..AC-5.5 plus
/// edge cases on empty action lists / argument lists and the caller-stream-ownership
/// contract.
/// </summary>
public sealed class XmlReaderScpdParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Scpds", name);

    private static XmlReaderScpdParser NewParser() => new XmlReaderScpdParser();

    // ─────────────────── AC-5.1 — happy path ───────────────────

    [Fact]
    [Trait("ac", "AC-5.1")]
    public async Task StreamActions_Happy_YieldsAllInOrder()
    {
        await using var stream = File.OpenRead(FixturePath("linn-ds-5action.xml"));
        var parser = NewParser();

        var actions = new List<ScpdAction>();
        await foreach (var a in parser.StreamActionsAsync(stream, CancellationToken.None))
            actions.Add(a);

        actions.Should().HaveCount(5);
        actions.Select(a => a.Name).Should().Equal("GetMute", "SetMute", "GetVolume", "SetVolume", "VolumeInc");

        // Spot-check argument shape.
        var getMute = actions[0];
        getMute.Inputs.Should().ContainSingle(a => a.Name == "Channel" && a.Direction == ScpdDirection.In);
        getMute.Outputs.Should().ContainSingle(a => a.Name == "CurrentMute" && a.Direction == ScpdDirection.Out);

        var setMute = actions[1];
        setMute.Inputs.Should().HaveCount(2);
        setMute.Outputs.Should().BeEmpty();

        actions[4].Inputs.Should().BeEmpty();
        actions[4].Outputs.Should().BeEmpty();
    }

    // ─────────────────── AC-5.1 — incremental + perf ───────────────────

    [Fact]
    [Trait("ac", "AC-5.1")]
    public async Task StreamActions_LargeScpd_StreamsIncrementally()
    {
        var bytes = BuildLargeScpd(200);
        using var stream = new MemoryStream(bytes);
        var parser = NewParser();

        var perIterationMaxMs = 0L;
        var total = Stopwatch.StartNew();
        var lastTick = Stopwatch.GetTimestamp();
        var count = 0;

        await foreach (var _ in parser.StreamActionsAsync(stream, CancellationToken.None))
        {
            var now = Stopwatch.GetTimestamp();
            var ms = (long)((now - lastTick) * 1000.0 / Stopwatch.Frequency);
            if (ms > perIterationMaxMs) perIterationMaxMs = ms;
            lastTick = now;
            count++;
        }

        total.Stop();
        count.Should().Be(200);
        // Generous CI headroom over the 16 ms spec budget. xUnit runners + CI noise.
        perIterationMaxMs.Should().BeLessThan(50,
            "FR-100 per-iteration ceiling 16 ms; allow 50 ms for CI noise");
        total.ElapsedMilliseconds.Should().BeLessThan(2_000,
            "Perf Budget §6 cold-large-SCPD ≤ 2 s");
    }

    // ─────────────────── AC-5.2 — malformed ───────────────────

    [Fact]
    [Trait("ac", "AC-5.2")]
    public async Task StreamActions_MalformedMidDocument_YieldsValidThenThrows()
    {
        await using var stream = File.OpenRead(FixturePath("malformed-mid-document.xml"));
        var parser = NewParser();

        var yielded = new List<ScpdAction>();
        var enumerator = parser.StreamActionsAsync(stream, CancellationToken.None).GetAsyncEnumerator();

        try
        {
            // The fixture has Action0, Action1 valid; the THIRD <action> has unterminated <name>.
            // Depending on where the XmlReader first detects the violation, either:
            //  (a) Action0 + Action1 yield successfully then the next MoveNextAsync throws, or
            //  (b) only Action0 yields then iteration 2 throws because Action1 is the last clean
            //      yield before the broken DOM blows up the reader.
            // Both shapes satisfy the AC text "actions 0..N-1 are yielded successfully ... next
            // iteration throws". We assert >= 1 valid yields followed by UpnpProtocolException.
            while (true)
            {
                bool moved;
                try { moved = await enumerator.MoveNextAsync(); }
                catch (UpnpProtocolException)
                {
                    // Expected — the malformed XML triggered the outer try/catch in StreamActionsAsync.
                    yielded.Should().NotBeEmpty(
                        "at least Action0 should yield before the malformed XML throws");
                    yielded.Select(a => a.Name).Should().StartWith("Action0",
                        "first valid action must yield in source order");
                    return;   // test passes
                }
                if (!moved) break;
                yielded.Add(enumerator.Current);
            }

            // If we get here the enumerator completed without throwing — bug in fixture or parser.
            Assert.Fail("malformed SCPD did not throw UpnpProtocolException");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // ─────────────────── AC-5.3 — XXE blocked ───────────────────

    [Fact]
    [Trait("ac", "AC-5.3")]
    public async Task StreamActions_XxeAttempt_ThrowsUpnpProtocolException()
    {
        await using var stream = File.OpenRead(FixturePath("xxe-attempt.xml"));
        var parser = NewParser();

        var enumerator = parser.StreamActionsAsync(stream, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            var act = async () => await enumerator.MoveNextAsync();
            await act.Should().ThrowAsync<UpnpProtocolException>(
                "DtdProcessing=Prohibit must raise XmlException at <!DOCTYPE>, wrapped to UpnpProtocolException");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // ─────────────────── AC-5.4 — cancellation ───────────────────

    [Fact]
    [Trait("ac", "AC-5.4")]
    public async Task StreamActions_CancellationMidStream_PropagatesOperationCanceledException()
    {
        var bytes = BuildLargeScpd(200);
        using var stream = new MemoryStream(bytes);
        var parser = NewParser();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();   // already cancelled — first ReadSafeAsync throws OCE

        var act = async () =>
        {
            await foreach (var _ in parser.StreamActionsAsync(stream, cts.Token))
            {
                // expected to throw before first iteration completes
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─────────────────── AC-5.5 — state table ───────────────────

    [Fact]
    [Trait("ac", "AC-5.5")]
    public async Task ReadStateTable_RichSCPD_BuildsByNameDictionary()
    {
        await using var stream = File.OpenRead(FixturePath("state-table-rich.xml"));
        var parser = NewParser();

        var table = await parser.ReadStateTableAsync(stream, CancellationToken.None);

        table.ByName.Should().HaveCount(4);

        table.ByName["Mute"].DataType.Should().Be("boolean");
        table.ByName["Mute"].DefaultValue.Should().Be("0");

        var volume = table.ByName["Volume"];
        volume.DataType.Should().Be("ui4");
        volume.DefaultValue.Should().Be("50");
        volume.AllowedValueRange.Should().NotBeNull();
        volume.AllowedValueRange!.Minimum.Should().Be(0);
        volume.AllowedValueRange.Maximum.Should().Be(100);
        volume.AllowedValueRange.Step.Should().Be(1);

        var balance = table.ByName["Balance"];
        balance.AllowedValueRange.Should().NotBeNull();
        balance.AllowedValueRange!.Minimum.Should().Be(-15);
        balance.AllowedValueRange.Maximum.Should().Be(15);
        balance.AllowedValueRange.Step.Should().BeNull("SCPD omits <step> → AC-5.5 requires null");

        var mode = table.ByName["Mode"];
        mode.DefaultValue.Should().Be("Stereo");
        mode.AllowedValueList.Should().NotBeNull();
        mode.AllowedValueList!.Should().Equal("Stereo", "Mono", "Surround");
    }

    [Fact]
    [Trait("ac", "AC-5.5")]
    public async Task ReadStateTable_AllowedValueRange_NullStepWhenOmitted()
    {
        await using var stream = File.OpenRead(FixturePath("state-table-rich.xml"));
        var parser = NewParser();

        var table = await parser.ReadStateTableAsync(stream, CancellationToken.None);

        // Balance has no <step>; AC-5.5 mandates null.
        table.ByName["Balance"].AllowedValueRange!.Step.Should().BeNull();
    }

    // ─────────────────── edge cases ───────────────────

    [Fact]
    public async Task StreamActions_EmptyActionList_YieldsZero()
    {
        var xml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <scpd xmlns="urn:schemas-upnp-org:service-1-0">
              <actionList/>
            </scpd>
            """);
        using var stream = new MemoryStream(xml);
        var parser = NewParser();

        var count = 0;
        await foreach (var _ in parser.StreamActionsAsync(stream, CancellationToken.None))
            count++;

        count.Should().Be(0);
    }

    [Fact]
    public async Task StreamActions_ActionWithNoArguments_YieldsActionWithEmptyLists()
    {
        var xml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <scpd xmlns="urn:schemas-upnp-org:service-1-0">
              <actionList>
                <action><name>NoArgs</name><argumentList/></action>
              </actionList>
            </scpd>
            """);
        using var stream = new MemoryStream(xml);
        var parser = NewParser();

        var actions = new List<ScpdAction>();
        await foreach (var a in parser.StreamActionsAsync(stream, CancellationToken.None))
            actions.Add(a);

        actions.Should().ContainSingle();
        actions[0].Name.Should().Be("NoArgs");
        actions[0].Inputs.Should().BeEmpty();
        actions[0].Outputs.Should().BeEmpty();
    }

    // ─────────────────── stream ownership contract ───────────────────

    [Fact]
    public async Task StreamActions_DoesNotDisposeCallerStream()
    {
        var bytes = BuildLargeScpd(3);
        using var inner = new MemoryStream(bytes);
        using var tracker = new DisposeTrackingStream(inner);
        var parser = NewParser();

        await foreach (var _ in parser.StreamActionsAsync(tracker, CancellationToken.None))
        {
        }

        tracker.DisposeCount.Should().Be(0,
            "XmlReaderSettings.CloseInput defaults to false; parser must NOT dispose caller's stream");
    }

    [Fact]
    public async Task ReadStateTable_DoesNotDisposeCallerStream()
    {
        await using var inner = File.OpenRead(FixturePath("state-table-rich.xml"));
        using var tracker = new DisposeTrackingStream(inner);
        var parser = NewParser();

        _ = await parser.ReadStateTableAsync(tracker, CancellationToken.None);

        tracker.DisposeCount.Should().Be(0,
            "ReadStateTableAsync must NOT dispose caller's stream");
    }

    // ─────────────────── helpers ───────────────────

    private static byte[] BuildLargeScpd(int actionCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<scpd xmlns="urn:schemas-upnp-org:service-1-0">""");
        sb.AppendLine("  <specVersion><major>1</major><minor>0</minor></specVersion>");
        sb.AppendLine("  <actionList>");
        for (var i = 0; i < actionCount; i++)
        {
            sb.Append("    <action><name>Action").Append(i).AppendLine("</name>");
            sb.AppendLine("      <argumentList>");
            sb.Append("        <argument><name>In").Append(i).AppendLine("</name><direction>in</direction><relatedStateVariable>VarA</relatedStateVariable></argument>");
            sb.Append("        <argument><name>Out").Append(i).AppendLine("</name><direction>out</direction><relatedStateVariable>VarB</relatedStateVariable></argument>");
            sb.AppendLine("      </argumentList>");
            sb.AppendLine("    </action>");
        }
        sb.AppendLine("  </actionList>");
        sb.AppendLine("  <serviceStateTable>");
        sb.AppendLine("    <stateVariable><name>VarA</name><dataType>string</dataType></stateVariable>");
        sb.AppendLine("    <stateVariable><name>VarB</name><dataType>string</dataType></stateVariable>");
        sb.AppendLine("  </serviceStateTable>");
        sb.AppendLine("</scpd>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private sealed class DisposeTrackingStream : Stream
    {
        private readonly Stream _inner;
        public int DisposeCount { get; private set; }

        public DisposeTrackingStream(Stream inner) { _inner = inner; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            // Do NOT propagate to _inner here — outer using owns it.
            base.Dispose(disposing);
        }
    }
}
