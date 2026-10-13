using System.Buffers.Binary;
using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>Tests for <see cref="DiagnoseHandler"/>; no network.</summary>
public class DiagnoseHandlerTests
{
    private static EmulatorIdentityInfo Identity(ushort status) =>
        new("Emulate 5580 Controller", 38, 11, 1, 14, 0x55, 1, status);

    private static byte[] FaultBytes()
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 20);
        return b;
    }

    private sealed class Recorder
    {
        public List<string> Hosts { get; } = [];
        public DiagnoseHandler.IdentityReader Id(ushort status) => (h, _) => { Hosts.Add(h); return Task.FromResult(Identity(status)); };
        public DiagnoseHandler.FaultRecordReader Fault(byte[] rec) => (h, _) => { Hosts.Add(h); return Task.FromResult(rec); };
    }

    /// <summary>Tag service decorator that counts every call that could mutate the controller.</summary>
    private sealed class CountingTagService : ITagService
    {
        private readonly VirtualTagService _inner = new();
        public int Writes;
        public int Connects;
        public Task ConnectAsync(string gateway, string path, CancellationToken cancellationToken = default)
        { Connects++; return _inner.ConnectAsync(gateway, path, cancellationToken); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => _inner.DisconnectAsync(cancellationToken);
        public Task<TagValue> ReadAsync(string tagName, CancellationToken cancellationToken = default) => _inner.ReadAsync(tagName, cancellationToken);
        public Task WriteAsync(string tagName, object value, CancellationToken cancellationToken = default)
        { Writes++; return _inner.WriteAsync(tagName, value, cancellationToken); }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken cancellationToken = default) => _inner.ListTagsAsync(cancellationToken);
    }

    [Fact]
    public async Task Faulted_ReturnsDecodedFault_Fix_AndSessionAddress()
    {
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");
        var rec = new Recorder();

        var result = await DiagnoseHandler.ExecuteAsync(
            null, session, new ControllerModeService(), rec.Id(0x3450), rec.Fault(FaultBytes()));

        var r = Assert.IsType<DiagnoseResult>(result);
        Assert.True(r.Faulted);
        Assert.Equal("0x3450", r.IdentityStatus);
        Assert.Equal(4, r.MajorFault!.Type);
        Assert.Equal(20, r.MajorFault.Code);
        Assert.Empty(r.Suspects);
        Assert.Equal(DiagnoseHandler.FixText, r.Fix);
        Assert.All(rec.Hosts, h => Assert.Equal("10.0.0.7", h));

        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, result.GetType())).RootElement;
        Assert.True(doc.TryGetProperty("majorFault", out _));
        Assert.True(doc.TryGetProperty("suspects", out _));
    }

    [Fact]
    public async Task ExplicitIp_OverridesSessionAddress_AndNoSessionIsNeeded()
    {
        var rec = new Recorder();
        var result = await DiagnoseHandler.ExecuteAsync(
            "10.0.0.5", new TagSessionService(), new ControllerModeService(), rec.Id(0x3060), rec.Fault(new byte[24]));

        var r = Assert.IsType<DiagnoseResult>(result);
        Assert.False(r.Faulted);
        Assert.Null(r.Fix);
        Assert.Null(r.MajorFault);
        Assert.Null(r.Mode);
        Assert.Equal("0x3060", r.IdentityStatus);
        Assert.All(rec.Hosts, h => Assert.Equal("10.0.0.5", h));
    }

    [Fact]
    public async Task NoIpAndNoSession_IsNoActiveSession()
    {
        var result = await DiagnoseHandler.ExecuteAsync(null, new TagSessionService(), new ControllerModeService());
        var e = Assert.IsType<DiagnoseError>(result);
        Assert.Equal(ErrorKinds.NoActiveSession, e.Kind);
    }

    [Fact]
    public async Task IdentityFailure_IsIdentityUnavailable()
    {
        var result = await DiagnoseHandler.ExecuteAsync(
            "10.0.0.9", new TagSessionService(), new ControllerModeService(),
            (_, _) => throw new EmulatorIdentityException("boom"), new Recorder().Fault(new byte[24]));
        Assert.Equal(ErrorKinds.IdentityUnavailable, Assert.IsType<DiagnoseError>(result).Kind);
    }

    [Fact]
    public async Task NotFaulted_NoSuspects_NoFix()
    {
        var rec = new Recorder();
        var result = await DiagnoseHandler.ExecuteAsync(
            "10.0.0.9", new TagSessionService(), new ControllerModeService(), rec.Id(0x3060), rec.Fault(new byte[24]));
        var r = Assert.IsType<DiagnoseResult>(result);
        Assert.False(r.Faulted);
        Assert.Empty(r.Suspects);
        Assert.Null(r.Fix);
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, result.GetType())).RootElement;
        Assert.False(doc.TryGetProperty("fix", out _));
    }

    [Fact]
    public void FixText_IsTheFixedClearMajorsSentence()
    {
        Assert.Equal(
            "Clear the fault in Logix Designer (Controller Properties \u2192 Major Faults \u2192 Clear Majors) after correcting " +
            "the cause; lgx-mcp cannot clear faults.",
            DiagnoseHandler.FixText);
    }

    [Fact]
    public async Task NeverWrites_NeverChangesMode()
    {
        var tags = new CountingTagService();
        var session = new TagSessionService();
        session.Attach(tags, isVirtual: true, controllerHost: "10.0.0.7");
        var modeSvc = new ControllerModeService();
        modeSvc.Reset("Run");
        var rec = new Recorder();

        await DiagnoseHandler.ExecuteAsync(null, session, modeSvc, rec.Id(0x3450), rec.Fault(FaultBytes()));
        var healthy = await DiagnoseHandler.ExecuteAsync(null, session, modeSvc, rec.Id(0x3060), rec.Fault(new byte[24]));

        Assert.Equal(0, tags.Writes);
        Assert.Equal("Run", modeSvc.ControllerMode);
        Assert.Equal("Run", modeSvc.RequestedMode);
        Assert.Equal("Run", Assert.IsType<DiagnoseResult>(healthy).Mode);
    }

    // ── Rung-citation suspects ──────────────────────────

    private static readonly string DiagnoseFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoDiagnose.L5X");

    private static byte[] FaultBytes(ushort type, ushort code)
    {
        var b = FaultBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), code);
        return b;
    }

    private static async Task<DiagnoseResult> DiagnoseWith(
        byte[] fault, IReadOnlyDictionary<string, (object Value, string? DataType)> tagValues, bool loadProject = true)
    {
        var project = new ProjectService();
        if (loadProject) project.Load(DiagnoseFixturePath);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(tagValues), isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");
        var rec = new Recorder();
        var result = await DiagnoseHandler.ExecuteAsync(
            null, session, new ControllerModeService(), rec.Id(0x3450), rec.Fault(fault), project);
        return Assert.IsType<DiagnoseResult>(result);
    }

    private static Dictionary<string, (object Value, string? DataType)> Values(
        int probeIdx, int otherIdx, int timerNegPre = 100, int timerOkPre = 100) => new()
    {
        ["Probe_Idx"] = (probeIdx, "DINT"),
        ["Other_Idx"] = (otherIdx, "DINT"),
        ["Timer_Neg.PRE"] = (timerNegPre, "DINT"),
        ["Timer_Ok.PRE"] = (timerOkPre, "DINT"),
    };

    [Fact]
    public async Task ArrayIndexSuspect_RankedByCurrentIndexVsDimension()
    {
        // Other_Arr[Other_Idx] appears first in the routine but is in range; Probe_Arr[Probe_Idx] (5 >= 2) must rank first.
        var r = await DiagnoseWith(FaultBytes(), Values(probeIdx: 5, otherIdx: 3));

        Assert.Equal(2, r.Suspects.Count);
        var first = Assert.IsType<ArrayIndexSuspect>(r.Suspects[0]);
        Assert.Equal("Probe_Arr", first.Array);
        Assert.Equal("Probe_Idx", first.Index);
        Assert.Equal(5, first.IndexValue);
        Assert.Equal(2, first.Dimension);
        Assert.True(first.OutOfRange);
        Assert.Equal(1, first.Citation.RungNumber);
        Assert.Equal("ADD", first.Citation.Instruction);
        Assert.Contains("index currently 5, array size 2", first.Summary);

        var second = Assert.IsType<ArrayIndexSuspect>(r.Suspects[1]);
        Assert.Equal("Other_Arr", second.Array);
        Assert.Equal(3, second.IndexValue);
        Assert.Equal(10, second.Dimension);
        Assert.False(second.OutOfRange);
        Assert.Null(r.SuspectsNote);
    }

    [Fact]
    public async Task BenchRecord_ArrayIndexSuspectListed()
    {
        // Real S32-p112 bench reply (type 4 code 20), decoded by the production decoder, drives the handler.
        var bench = Convert.FromHexString(FaultRecordDecoderTests.BenchS32p112Hex);
        var decoded = FaultRecordDecoder.Decode(bench);
        Assert.Equal(4, decoded!.Type);
        Assert.Equal(20, decoded.Code);

        var r = await DiagnoseWith(bench, Values(probeIdx: 5, otherIdx: 3));

        Assert.Equal(4, r.MajorFault!.Type);
        Assert.Equal(20, r.MajorFault.Code);
        var probe = Assert.Single(r.Suspects.Cast<ArrayIndexSuspect>(),
            s => s.Array == "Probe_Arr" && s.Index == "Probe_Idx" && s.Citation.Instruction == "ADD");
        Assert.Same(probe, r.Suspects[0]);
        Assert.Equal(5, probe.IndexValue);
        Assert.Equal(2, probe.Dimension);
        Assert.True(probe.OutOfRange);
        Assert.Equal("Probe_Arr[Probe_Idx]: index currently 5, array size 2", probe.Summary);
    }

    [Fact]
    public async Task ArrayIndexSuspect_NegativeIndexIsOutOfRange_AndLiteralSubscriptIsNotListed()
    {
        var r = await DiagnoseWith(FaultBytes(), Values(probeIdx: 1, otherIdx: -1));

        // Rung 2 (Probe_Arr[1]) has a literal subscript and is not a suspect.
        Assert.Equal(2, r.Suspects.Count);
        var first = Assert.IsType<ArrayIndexSuspect>(r.Suspects[0]);
        Assert.Equal("Other_Arr", first.Array);
        Assert.True(first.OutOfRange);
        Assert.DoesNotContain(r.Suspects.Cast<ArrayIndexSuspect>(), s => s.Citation.RungNumber == 2);
    }

    [Fact]
    public async Task RungTextWrappedUntrusted()
    {
        var r420 = await DiagnoseWith(FaultBytes(), Values(5, 3));
        foreach (var s in r420.Suspects.Cast<ArrayIndexSuspect>())
        {
            Assert.StartsWith("<untrusted source=\"rung-text\"", s.Citation.Rung);
            Assert.EndsWith("</untrusted>", s.Citation.Rung);
        }
        Assert.Contains("ADD(Probe_Arr[Probe_Idx],0,Probe_Out)", ((ArrayIndexSuspect)r420.Suspects[0]).Citation.Rung);

        var r434 = await DiagnoseWith(FaultBytes(4, 34), Values(0, 0, timerNegPre: -7));
        var timer = Assert.IsType<NegativePreSuspect>(Assert.Single(r434.Suspects));
        Assert.NotEmpty(timer.References);
        Assert.All(timer.References, c => Assert.StartsWith("<untrusted source=\"rung-text\"", c.Rung));
    }

    [Fact]
    public async Task Type4Code34_ListsOnlyTimersWithNegativePre()
    {
        var r = await DiagnoseWith(FaultBytes(4, 34), Values(0, 0, timerNegPre: -7, timerOkPre: 250));

        var s = Assert.IsType<NegativePreSuspect>(Assert.Single(r.Suspects));
        Assert.Equal("Timer_Neg", s.Timer);
        Assert.Equal(-7, s.Pre);
        var cite = Assert.Single(s.References);
        Assert.Equal(3, cite.RungNumber);
        Assert.Equal("TON", cite.Instruction);
    }

    [Fact]
    public async Task OtherFaultTypes_ReturnEmptySuspectsWithNote()
    {
        var r = await DiagnoseWith(FaultBytes(6, 1), Values(5, 3));

        Assert.Empty(r.Suspects);
        Assert.Contains("type 6 code 1", r.SuspectsNote);
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(r, r.GetType())).RootElement;
        Assert.Equal(0, doc.GetProperty("suspects").GetArrayLength());
        Assert.True(doc.TryGetProperty("suspectsNote", out _));
    }

    [Fact]
    public async Task Type4Code20_NoProjectLoaded_EmptySuspectsWithNote()
    {
        var r = await DiagnoseWith(FaultBytes(), Values(5, 3), loadProject: false);

        Assert.Empty(r.Suspects);
        Assert.Contains("No project is loaded", r.SuspectsNote);
    }

    [Fact]
    public async Task ArrayIndexSuspects_NoLiveReads_ListedUnrankedWithNote()
    {
        var project = new ProjectService();
        project.Load(DiagnoseFixturePath);
        var rec = new Recorder();
        var result = await DiagnoseHandler.ExecuteAsync(
            "10.0.0.5", new TagSessionService(), new ControllerModeService(), rec.Id(0x3450), rec.Fault(FaultBytes()), project);

        var r = Assert.IsType<DiagnoseResult>(result);
        Assert.Equal(2, r.Suspects.Count);
        Assert.All(r.Suspects.Cast<ArrayIndexSuspect>(), s => Assert.Null(s.IndexValue));
        Assert.Contains("could not be read", r.SuspectsNote);
    }

    private static async Task<(DiagnoseResult R, JsonElement Doc)> RunStatus(ushort status, byte[] record)
    {
        var rec = new Recorder();
        var result = await DiagnoseHandler.ExecuteAsync(
            "10.0.0.5", new TagSessionService(), new ControllerModeService(), rec.Id(status), rec.Fault(record));
        var r = Assert.IsType<DiagnoseResult>(result);
        return (r, JsonDocument.Parse(JsonSerializer.Serialize(r, r.GetType())).RootElement.Clone());
    }

    [Fact]
    public async Task Forces_NeverReportedAndNeverSaysNone()
    {
        foreach (var (status, rec) in new[] { ((ushort)0x3060, new byte[24]), ((ushort)0x3450, FaultBytes()) })
        {
            var (_, doc) = await RunStatus(status, rec);
            var forces = doc.GetProperty("forces");
            Assert.False(forces.GetProperty("reported").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(forces.GetProperty("howToCheck").GetString()));
            Assert.Equal(DiagnoseHandler.ForcesReason, forces.GetProperty("reason").GetString());
            Assert.Equal(DiagnoseHandler.ForcesHowToCheck, forces.GetProperty("howToCheck").GetString());
            Assert.False(forces.TryGetProperty("note", out _));
            Assert.DoesNotContain("no forces", doc.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DebugModeFlag_AddsNoteWithoutClaimingForces()
    {
        var (_, doc) = await RunStatus(0x3060 | 0x8000, new byte[24]);
        Assert.Equal("debugMode", doc.GetProperty("status").GetProperty("modeFlag").GetString());
        var forces = doc.GetProperty("forces");
        Assert.False(forces.GetProperty("reported").GetBoolean());
        Assert.Equal(DiagnoseHandler.DebugModeNote, forces.GetProperty("note").GetString());
        Assert.DoesNotContain("no forces", doc.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var (_, healthy) = await RunStatus(0x3060, new byte[24]);
        Assert.False(healthy.GetProperty("forces").TryGetProperty("note", out _));
    }

    [Fact]
    public async Task ExistingFields_Unchanged()
    {
        var (h, hd) = await RunStatus(0x3060, new byte[24]);
        Assert.False(h.Faulted);
        Assert.Equal("0x3060", hd.GetProperty("identityStatus").GetString());
        Assert.False(hd.TryGetProperty("majorFault", out _));
        Assert.Equal("run", hd.GetProperty("status").GetProperty("deviceStatus").GetString());
        Assert.Equal(0, hd.GetProperty("status").GetProperty("faults").GetArrayLength());

        var (f, fd) = await RunStatus(0x3450, FaultBytes());
        Assert.True(f.Faulted);
        Assert.Equal("0x3450", fd.GetProperty("identityStatus").GetString());
        Assert.Equal(4, fd.GetProperty("majorFault").GetProperty("type").GetInt32());
        Assert.Equal(20, fd.GetProperty("majorFault").GetProperty("code").GetInt32());
        Assert.Equal("faultedModes", fd.GetProperty("status").GetProperty("deviceStatus").GetString());
        Assert.Equal("recoverableMajor", fd.GetProperty("status").GetProperty("faults")[0].GetString());
    }
}
