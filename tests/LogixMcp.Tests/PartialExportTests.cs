using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A routine or program export declares itself partial on the root element
/// (<c>TargetType</c>, <c>ContainsContext</c>, <c>Controller Use="Context"</c>). Such a file
/// opens for reads and analysis but never derives a write gate.
/// </summary>
public class PartialExportTests
{
    private static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    private static string RoutineExport => Fixture("real-shape", "RoutineExport.L5X");
    private static string ProgramExport => Fixture("real-shape", "ProgramExport.L5X");

    private static Task<object> ConnectCip(ProjectService project, TagSessionService session) =>
        ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, project, session,
            backend: "cip",
            capabilities: new Capabilities(LiveWritesEnabled: true),
            cipConnectProbe: (_, _) => Task.CompletedTask);

    [Fact]
    public void RoutineExport_NoEnforcedGate()
    {
        var svc = new ProjectService();
        svc.Load(RoutineExport);

        Assert.NotNull(svc.PartialExport);
        Assert.Equal("Routine", svc.PartialExport!.ExportTarget);

        // The gate report is hypothetical and carries the reason, never "enforced".
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("hypothetical", doc.RootElement.GetProperty("policy").GetString());
        Assert.Contains(PartialExportInfo.Reason, json, StringComparison.Ordinal);
        Assert.Contains("open a full controller export to derive the write gate", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramExport_CipWriteGateRequired()
    {
        var project = new ProjectService();
        project.Load(ProgramExport);
        var session = new TagSessionService();

        var connected = Assert.IsType<ConnectResult>(await ConnectCip(project, session));
        Assert.False(connected.Gated);
        Assert.IsNotType<GatedTagService>(session.Service);

        var err = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Cmd_Fill", "true", session, capabilities: new Capabilities(true)));

        Assert.Equal(ErrorKinds.WriteGateRequired, err.Kind);
        Assert.Contains(
            "the open project is a partial (routine/program) export; open a full controller export to derive the write gate",
            err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualAndL5x_NoEnforcedGate()
    {
        foreach (var backend in new[] { "virtual", "l5x" })
        {
            var project = new ProjectService();
            project.Load(ProgramExport);
            var session = new TagSessionService();

            var connected = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, project, session, backend: backend,
                capabilities: new Capabilities(LiveWritesEnabled: true)));

            Assert.False(connected.Gated, backend);
            Assert.IsNotType<GatedTagService>(session.Service);
            Assert.DoesNotContain("derived from open project", connected.Message, StringComparison.Ordinal);

            // Same refusal as CIP, even though the session is offline.
            var err = Assert.IsType<WriteTagError>(
                await WriteTagHandler.ExecuteAsync("Cmd_Fill", "true", session, capabilities: new Capabilities(true)));
            Assert.Equal(ErrorKinds.WriteGateRequired, err.Kind);
            Assert.Contains(PartialExportInfo.Reason, err.Error, StringComparison.Ordinal);

            // The gate report is hypothetical, names the reason and says nothing about custom prefixes.
            var json = GateReportHandler.Serialize(GateReportHandler.Execute(project, session: session));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("hypothetical", doc.RootElement.GetProperty("policy").GetString());
            Assert.False(doc.RootElement.GetProperty("enforcedBySession").GetBoolean());
            Assert.Contains(PartialExportInfo.Reason, json, StringComparison.Ordinal);
            Assert.DoesNotContain("custom prefix rules", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task VirtualAndL5x_PartialWithAllowlist_GrantsOnlyFromAllowlist()
    {
        foreach (var backend in new[] { "virtual", "l5x" })
        {
            var dir = Path.Combine(Path.GetTempPath(), $"lgx-partial-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var l5x = Path.Combine(dir, "Partial.L5X");
                var list = Path.Combine(dir, "allow.txt");
                File.WriteAllText(l5x, DeniesExport);
                File.WriteAllText(list, "Cmd_Fine\nCmd_W\n");
                var project = new ProjectService();
                project.Load(l5x);
                var session = new TagSessionService();

                var connected = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
                    "", "", useVirtual: false, project, session, backend: backend,
                    capabilities: new Capabilities(LiveWritesEnabled: true, WriteAllowlistPath: list)));

                Assert.True(connected.Gated, backend);
                Assert.Equal("allowlist+partial-export-denies", connected.GateSource);
                Assert.IsType<GatedTagService>(session.Service);

                var denied = await Refused(session, "Cmd_W", "true");
                Assert.Contains(nameof(DenyReason.ProgramWritten), denied.Error, StringComparison.Ordinal);
                var unlisted = await Refused(session, "Cmd_Unlisted", "true");
                Assert.Contains(nameof(DenyReason.NotOnAllowlist), unlisted.Error, StringComparison.Ordinal);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public async Task FullControllerExport_Unchanged()
    {
        var full = Fixture("Demo.L5X");
        var project = new ProjectService();
        project.Load(full);
        Assert.Null(project.PartialExport);

        // open: no partial fields at all.
        var opened = OpenProjectHandler.Execute(full, new ProjectService());
        var openJson = JsonSerializer.Serialize(opened);
        Assert.DoesNotContain("partialExport", openJson, StringComparison.Ordinal);
        Assert.DoesNotContain("exportTarget", openJson, StringComparison.Ordinal);

        // gate report: still enforced.
        Assert.IsType<WriteGatePolicy>(GateReportHandler.Execute(project));
        using var doc = JsonDocument.Parse(GateReportHandler.Serialize(GateReportHandler.Execute(project)));
        Assert.Equal("enforced", doc.RootElement.GetProperty("policy").GetString());

        // CIP connect still derives the gate.
        var session = new TagSessionService();
        var connected = Assert.IsType<ConnectResult>(await ConnectCip(project, session));
        Assert.True(connected.Gated);
        Assert.IsType<GatedTagService>(session.Service);
    }

    [Fact]
    public void OpenReportsPartialExport()
    {
        foreach (var (path, target) in new[] { (RoutineExport, "Routine"), (ProgramExport, "Program") })
        {
            var result = Assert.IsType<OpenProjectResult>(OpenProjectHandler.Execute(path, new ProjectService()));
            Assert.True(result.PartialExport);
            Assert.Equal(target, result.ExportTarget);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(doc.RootElement.GetProperty("partialExport").GetBoolean());
            Assert.Equal(target, doc.RootElement.GetProperty("exportTarget").GetString());
        }
    }

    [Theory]
    [InlineData("<RSLogix5000Content TargetType=\"Controller\" ContainsContext=\"true\"><Controller Use=\"Target\"/></RSLogix5000Content>")]
    [InlineData("<RSLogix5000Content TargetType=\"Controller\" ContainsContext=\"false\"><Controller Use=\"Context\"/></RSLogix5000Content>")]
    [InlineData("<RSLogix5000Content TargetType=\"DataType\" ContainsContext=\"false\"><Controller Use=\"Target\"/></RSLogix5000Content>")]
    public void Detect_AnyPartialMarker_IsPartial(string xml) =>
        Assert.NotNull(PartialExportInfo.Detect(System.Xml.Linq.XElement.Parse(xml)));

    // ── Partial export plus LGX_WRITE_ALLOWLIST ───────────────────────────────

    /// <summary>A routine export with a safety tag, a program-written tag, a ranged tag and two plain command tags.</summary>
    internal const string DeniesExport = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.01" TargetName="Main" TargetType="Routine" ContainsContext="true" Owner="tester" ExportDate="Mon Jan 05 08:00:00 2026" ExportOptions="NoRawData L5KData DecoratedData Context Dependencies ForceProtectedEncoding AllProjDocTrans">
        <Controller Use="Context" Name="Plant_Ctl">
        <DataTypes Use="Context" />
        <Tags Use="Context">
        <Tag Name="Safety_Trip" TagType="Base" DataType="BOOL" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"><Data Format="L5K"><![CDATA[0]]></Data><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0"/></Data></Tag>
        <Tag Name="Cmd_W" TagType="Base" DataType="BOOL" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"><Data Format="L5K"><![CDATA[0]]></Data><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0"/></Data></Tag>
        <Tag Name="Cmd_Shared" TagType="Base" DataType="DINT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"><Description><![CDATA[range 0..100]]></Description><Data Format="L5K"><![CDATA[0]]></Data><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0"/></Data></Tag>
        <Tag Name="Cmd_Fine" TagType="Base" DataType="BOOL" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"><Data Format="L5K"><![CDATA[0]]></Data><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0"/></Data></Tag>
        <Tag Name="Cmd_Unlisted" TagType="Base" DataType="BOOL" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"><Data Format="L5K"><![CDATA[0]]></Data><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0"/></Data></Tag>
        </Tags>
        <Programs Use="Context">
        <Program Use="Context" Name="Main" TestEdits="false" MainRoutineName="Main" Disabled="false" UseAsFolder="false">
        <Routines>
        <Routine Use="Target" Name="Main" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[XIC(Cmd_Fine)OTE(Cmd_W);]]></Text></Rung></RLLContent></Routine>
        </Routines>
        </Program>
        </Programs>
        </Controller>
        </RSLogix5000Content>
        """;

    private sealed class RecordingService : ITagService
    {
        public List<string> Writes { get; } = [];
        public Task ConnectAsync(string gateway, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>([]);
        public Task WriteAsync(string tagName, object value, CancellationToken ct = default)
        {
            Writes.Add(tagName);
            return Task.CompletedTask;
        }
    }

    private static async Task<(ConnectResult Result, TagSessionService Session, string Dir)> ConnectWithAllowlist(
        string export, string allowlist)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lgx-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var l5x = Path.Combine(dir, "Partial.L5X");
        var list = Path.Combine(dir, "allow.txt");
        File.WriteAllText(l5x, export);
        File.WriteAllText(list, allowlist);

        var project = new ProjectService();
        project.Load(l5x);
        Assert.NotNull(project.PartialExport);
        var session = new TagSessionService();

        var connected = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, project, session,
            backend: "cip",
            capabilities: new Capabilities(LiveWritesEnabled: true, WriteAllowlistPath: list),
            cipConnectProbe: (_, _) => Task.CompletedTask));
        return (connected, session, dir);
    }

    private static async Task<WriteTagError> Refused(TagSessionService session, string tag, string value) =>
        Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync(
            tag, value, session, capabilities: new Capabilities(true)));

    [Fact]
    public async Task Allowlist_PartialDeniesAndBoundsStillApply()
    {
        var (connected, session, dir) = await ConnectWithAllowlist(
            DeniesExport, "Safety_Trip\nCmd_W\nCmd_Shared\nCmd_Fine\n");
        try
        {
            Assert.True(connected.Gated);
            Assert.Equal("allowlist+partial-export-denies", connected.GateSource);
            Assert.IsType<GatedTagService>(session.Service);

            var safety = await Refused(session, "Safety_Trip", "true");
            Assert.Contains(nameof(DenyReason.SafetyTag), safety.Error, StringComparison.Ordinal);

            var written = await Refused(session, "Cmd_W", "true");
            Assert.Contains(nameof(DenyReason.ProgramWritten), written.Error, StringComparison.Ordinal);

            var range = await Refused(session, "Cmd_Shared", "5000");
            Assert.Contains("outside the allowed range [0..100]", range.Error, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Allowlist_UnlistedTagRefused()
    {
        var (connected, session, dir) = await ConnectWithAllowlist(DeniesExport, "Cmd_Fine\n");
        try
        {
            Assert.True(connected.Gated);
            Assert.Equal("allowlist+partial-export-denies", connected.GateSource);

            var err = await Refused(session, "Cmd_Unlisted", "true");
            Assert.Contains(nameof(DenyReason.NotOnAllowlist), err.Error, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Allowlist_ListedTagsInRangeOrAbsentFromFileAreGranted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lgx-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var l5x = Path.Combine(dir, "Partial.L5X");
            File.WriteAllText(l5x, DeniesExport);
            var project = new ProjectService();
            project.Load(l5x);
            var list = Path.Combine(dir, "allow.txt");
            File.WriteAllText(list, "Cmd_Shared\nOther_Tag\nSafety_Other\nSafety_Trip[1]\nSafety_*\n");
            var allow = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(list));

            var policy = GateEngine.DerivePolicy(project.Project!,
                GateConfig.Default with { CommandPrefixes = new[] { "" } });
            var inner = new RecordingService();
            var gated = new GatedTagService(inner, policy);
            gated.GrantFromAllowlist();
            gated.ApplyAllowlist(allow);

            await gated.WriteAsync("Cmd_Shared", 50);       // in range
            await gated.WriteAsync("Other_Tag", 1);         // absent from the partial file, listed
            Assert.Equal(["Cmd_Shared", "Other_Tag"], inner.Writes);

            await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Cmd_Shared", 101));
            await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Safety_Other", 1));  // safety prefix by name
            await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Safety_Trip[1]", 1)); // deny reaches elements
            await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Cmd_Unlisted", true)); // not listed
            Assert.Equal(2, inner.Writes.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
