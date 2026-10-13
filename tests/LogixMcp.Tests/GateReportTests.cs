using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Tests that <c>logix_gate_report</c> reflects the policy enforced by the active
/// session's <see cref="GatedTagService"/>.
/// </summary>
public class GateReportTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private const string UploadPath = "/tmp/uploaded-from-controller.acd";

    /// <summary>A policy that deliberately differs from the Demo.L5X file policy.</summary>
    private static WriteGatePolicy ControllerPolicy() =>
        new(
            new[] { new GrantEntry("Ctrl_Only_Setpoint", null, null, "uploaded controller project") },
            new[] { new DenyEntry("Cmd_MotorStart", DenyReason.NotDesignated, "uploaded controller project") })
        {
            Origin = new GatePolicyOrigin(
                GatePolicyOrigin.ControllerUpload, UploadPath, EnforcedBySession: true),
        };

    [Fact]
    public void AfterSdkReconnect_ReportsSessionPolicy()
    {
        // Repro: file open, then a session whose gate was derived from a differing
        // controller upload. The report must show the session's policy, not the file's.
        var project = new ProjectService();
        var session = new TagSessionService();
        var tools   = new LogixProjectTools(project, session);
        tools.GetOpenProjectResponse(FixturePath);

        session.Attach(
            new GatedTagService(new VirtualTagService(), ControllerPolicy()),
            isVirtual: false, isLiveController: true);

        var root = JsonDocument.Parse(tools.GetGateReportResponse()).RootElement;

        Assert.Equal("enforced", root.GetProperty("policy").GetString());
        Assert.Equal("controller-upload", root.GetProperty("policySource").GetString());
        Assert.Equal(UploadPath, root.GetProperty("sourcePath").GetString());
        Assert.True(root.GetProperty("enforcedBySession").GetBoolean());

        var grants = root.GetProperty("grants").EnumerateArray()
            .Select(g => g.GetProperty("tag").GetString()).ToList();
        Assert.Equal(new[] { "Ctrl_Only_Setpoint" }, grants);
        Assert.DoesNotContain("Cmd_MotorStart", grants);
    }

    [Fact]
    public void Execute_WithSession_ReturnsTheSamePolicyInstanceTheGateEnforces()
    {
        var project = new ProjectService();
        project.Load(FixturePath);
        var session = new TagSessionService();
        var policy  = ControllerPolicy();
        session.Attach(new GatedTagService(new VirtualTagService(), policy), false, true);

        var result = GateReportHandler.Execute(project, session: session);

        Assert.Same(policy, result);
    }

    [Fact]
    public void NoSession_ReportsFilePolicyNotEnforced()
    {
        var project = new ProjectService();
        var tools   = new LogixProjectTools(project, new TagSessionService());
        tools.GetOpenProjectResponse(FixturePath);

        var root = JsonDocument.Parse(tools.GetGateReportResponse()).RootElement;

        Assert.Equal("project-file", root.GetProperty("policySource").GetString());
        Assert.Equal(project.FilePath, root.GetProperty("sourcePath").GetString());
        Assert.False(root.GetProperty("enforcedBySession").GetBoolean());
        Assert.Contains(root.GetProperty("grants").EnumerateArray(),
            g => g.GetProperty("tag").GetString() == "Cmd_MotorStart");
    }

    [Fact]
    public void UngatedSession_ReportsFilePolicyNotEnforced()
    {
        var project = new ProjectService();
        project.Load(FixturePath);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true);

        var root = JsonDocument.Parse(GateReportHandler.Serialize(
            GateReportHandler.Execute(project, session: session))).RootElement;

        Assert.False(root.GetProperty("enforcedBySession").GetBoolean());
    }

    [Fact]
    public void HypotheticalRun_IsNeverMarkedEnforcedBySession()
    {
        var project = new ProjectService();
        project.Load(FixturePath);
        var session = new TagSessionService();
        session.Attach(new GatedTagService(new VirtualTagService(), ControllerPolicy()), false, true);

        var root = JsonDocument.Parse(GateReportHandler.Serialize(
            GateReportHandler.Execute(project, new[] { "Sim_" }, null, session))).RootElement;

        Assert.Equal("hypothetical", root.GetProperty("policy").GetString());
        Assert.False(root.GetProperty("enforcedBySession").GetBoolean());
    }

    [Fact]
    public void SessionDerivedFromAcd_ReportsAcdDerived()
    {
        var origin = GatePolicyOrigin.ForSessionFromFile("/x/plant.ACD");
        Assert.Equal("acd-derived", origin.Source);
        Assert.True(origin.EnforcedBySession);
        Assert.Equal("project-file", GatePolicyOrigin.ForSessionFromFile("/x/plant.L5X").Source);
    }

    [Fact]
    public void ShowsAllowlistAndOnAllowlist()
    {
        var dir  = Directory.CreateTempSubdirectory("gate-report-allowlist").FullName;
        try
        {
            var file = Path.Combine(dir, "allow.txt");
            File.WriteAllText(file, "Ctrl_Only_Setpoint\n");
            var allowlist = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(file));

            var project = new ProjectService();
            var session = new TagSessionService();
            var tools   = new LogixProjectTools(project, session);
            tools.GetOpenProjectResponse(FixturePath);
            session.Attach(
                new GatedTagService(new VirtualTagService(), ControllerPolicy()),
                isVirtual: false, isLiveController: true);

            // No allowlist: no allowlist object and no per-tag flag.
            var before = JsonDocument.Parse(tools.GetGateReportResponse()).RootElement;
            Assert.False(before.TryGetProperty("allowlist", out _));
            Assert.False(before.GetProperty("grants")[0].TryGetProperty("onAllowlist", out _));

            session.SetWriteAllowlist(allowlist);
            var root = JsonDocument.Parse(tools.GetGateReportResponse()).RootElement;

            var al = root.GetProperty("allowlist");
            Assert.Equal(file, al.GetProperty("path").GetString());
            Assert.Equal(1, al.GetProperty("entries").GetInt32());
            Assert.Equal(allowlist.Sha256, al.GetProperty("sha256").GetString());

            Assert.True(root.GetProperty("grants")[0].GetProperty("onAllowlist").GetBoolean());
            Assert.False(root.GetProperty("denies")[0].GetProperty("onAllowlist").GetBoolean());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static WriteGatePolicy TwoGrantPolicy() =>
        new(
            new[]
            {
                new GrantEntry("Cmd_MotorStart", null, null, "command prefix 'Cmd_'"),
                new GrantEntry("Sim_Speed",      null, null, "command prefix 'Sim_'"),
            },
            new[] { new DenyEntry("Out_Prog", DenyReason.ProgramWritten, "written by program logic") });

    private static VirtualTagService TwoGrantStore() => new(new Dictionary<string, (object, string?)>
    {
        ["Cmd_MotorStart"] = (false, "BOOL"),
        ["Sim_Speed"]      = (0, "DINT"),
        ["Out_Prog"]       = (false, "BOOL"),
    });

    [Fact]
    public void AllowlistExcludedGrant_ListedAsDenyNotOnAllowlist()
    {
        var dir = Directory.CreateTempSubdirectory("gate-report-excluded").FullName;
        try
        {
            var file = Path.Combine(dir, "allow.txt");
            File.WriteAllText(file, "Sim_Speed\n");
            var allowlist = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(file));

            var policy = TwoGrantPolicy() with { Allowlist = allowlist };
            var root   = JsonDocument.Parse(policy.ToJson()).RootElement;

            // Cmd_MotorStart is granted by the gate but excluded by the allowlist.
            Assert.DoesNotContain(root.GetProperty("grants").EnumerateArray(),
                g => g.GetProperty("tag").GetString() == "Cmd_MotorStart");
            var deny = Assert.Single(root.GetProperty("denies").EnumerateArray(),
                d => d.GetProperty("tag").GetString() == "Cmd_MotorStart");
            Assert.Equal("NotOnAllowlist", deny.GetProperty("reason").GetString());
            Assert.Equal($"granted by command prefix 'Cmd_', removed by allowlist {file}",
                deny.GetProperty("provenance").GetString());
            Assert.False(deny.GetProperty("onAllowlist").GetBoolean());

            var counts = root.GetProperty("denyCounts");
            Assert.Equal(1, counts.GetProperty("NotOnAllowlist").GetInt32());
            Assert.Equal(1, counts.GetProperty("ProgramWritten").GetInt32());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task GrantsMatchWritePath_WithAllowlist()
    {
        var dir = Directory.CreateTempSubdirectory("gate-report-writepath").FullName;
        try
        {
            var file = Path.Combine(dir, "allow.txt");
            File.WriteAllText(file, "Sim_Speed\n");
            var allowlist = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(file));

            var policy  = TwoGrantPolicy();
            var session = new TagSessionService();
            session.Attach(new GatedTagService(TwoGrantStore(), policy), isVirtual: true);
            session.SetWriteAllowlist(allowlist);

            var root = JsonDocument.Parse(
                (policy with { Allowlist = allowlist }).ToJson()).RootElement;

            // Every reported grant is accepted by a write.
            foreach (var g in root.GetProperty("grants").EnumerateArray())
            {
                var tag = g.GetProperty("tag").GetString()!;
                var value = tag == "Sim_Speed" ? "5" : "true";
                Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync(tag, value, session));
            }

            // Every NotOnAllowlist deny is refused by a write with that reason.
            foreach (var d in root.GetProperty("denies").EnumerateArray()
                         .Where(d => d.GetProperty("reason").GetString() == "NotOnAllowlist"))
            {
                var err = Assert.IsType<WriteTagError>(
                    await WriteTagHandler.ExecuteAsync(d.GetProperty("tag").GetString()!, "true", session));
                Assert.Contains("NotOnAllowlist", err.Error, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
