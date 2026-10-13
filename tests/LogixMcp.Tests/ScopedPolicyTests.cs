using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Grants and denies are keyed by scope: controller tags by bare name, program tags as
/// <c>Program:&lt;P&gt;.&lt;Tag&gt;</c>. A program-local tag never grants or widens a
/// controller tag of the same name.
/// </summary>
public class ScopedPolicyTests : IDisposable
{
    private readonly string _tempDir;

    public ScopedPolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scopedpolicy-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string BoolTag(string name, string access = "Read/Write") =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="{access}" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static L5X Project(string controllerTags, string programTags, string rungs = "") =>
        L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <Tags>{controllerTags}</Tags>
                <Programs>
                  <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                    <Tags>{programTags}</Tags>
                    <Routines><Routine Name="R" Type="RLL"><RLLContent>{rungs}</RLLContent></Routine></Routines>
                  </Program>
                </Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);

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

    [Fact]
    public async Task ProgramTagDoesNotWidenControllerTag()
    {
        // Controller 'Cmd_X' is Read Only (denied); program-local 'Cmd_X' is writable.
        var policy = GateEngine.DerivePolicy(
            Project(BoolTag("Cmd_X", "Read Only"), BoolTag("Cmd_X")), GateConfig.Default);

        Assert.Contains(policy.Denies, d => d.ScopedName == "Cmd_X");
        Assert.DoesNotContain(policy.Grants, g => g.ScopedName == "Cmd_X");
        Assert.Contains(policy.Grants, g => g.ScopedName == "Program:Main.Cmd_X");

        var inner = new RecordingService();
        var gated = new GatedTagService(inner, policy);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Cmd_X", true));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task ControllerGrantDoesNotWidenProgramTag()
    {
        var policy = GateEngine.DerivePolicy(
            Project(BoolTag("Cmd_X"), BoolTag("Cmd_X", "Read Only")), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.ScopedName == "Cmd_X");
        Assert.Contains(policy.Denies, d => d.ScopedName == "Program:Main.Cmd_X");

        var inner = new RecordingService();
        var gated = new GatedTagService(inner, policy);

        await gated.WriteAsync("Cmd_X", true);
        await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Program:Main.Cmd_X", true));
        Assert.Equal(["Cmd_X"], inner.Writes);
    }

    [Fact]
    public async Task ProgramScopedWrite_UsesProgramGrant()
    {
        var policy = GateEngine.DerivePolicy(
            Project(BoolTag("Cmd_X", "Read Only"), BoolTag("Cmd_X")), GateConfig.Default);

        var inner = new RecordingService();
        var gated = new GatedTagService(inner, policy);

        await gated.WriteAsync("Program:Main.Cmd_X", true);

        Assert.Equal(["Program:Main.Cmd_X"], inner.Writes);
    }

    [Fact]
    public void ProgramWrittenAndSafety_StillMatchByName()
    {
        // A rung writing 'Cmd_Y' denies the controller tag and the same-named program tag.
        var policy = GateEngine.DerivePolicy(
            Project(BoolTag("Cmd_Y") + BoolTag("Safety_Z"), BoolTag("Cmd_Y") + BoolTag("Safety_Z"),
                """<Rung Number="0" Type="N"><Text><![CDATA[XIC(Cmd_In)OTE(Cmd_Y);]]></Text></Rung>"""),
            GateConfig.Default);

        foreach (var key in new[] { "Cmd_Y", "Program:Main.Cmd_Y" })
            Assert.Equal(DenyReason.ProgramWritten, Assert.Single(policy.Denies, d => d.ScopedName == key).Reason);
        foreach (var key in new[] { "Safety_Z", "Program:Main.Safety_Z" })
            Assert.Equal(DenyReason.SafetyTag, Assert.Single(policy.Denies, d => d.ScopedName == key).Reason);
    }
}
