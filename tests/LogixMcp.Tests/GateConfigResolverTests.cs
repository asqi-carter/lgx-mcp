using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change item 2: <c>LGX_GATE_COMMAND_PREFIXES</c> is read by one resolver and every
/// enforcement and reporting site uses it. Tests set the value through the resolver's
/// per-async-flow seam so they cannot race other tests in the same process.
/// </summary>
public class GateConfigResolverTests
{
    private static GateConfig Config(string? raw) => Assert.IsType<GateConfig>(GateConfigResolver.Resolve(raw));

    // ── Resolution ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetEmptyOrWhitespace_GivesDefault(string? raw)
    {
        var c = Config(raw);
        Assert.Equal(new[] { "Cmd_", "Sim_" }, c.CommandPrefixes);
        Assert.Equal("default", c.CommandPrefixesSource);
        Assert.False(c.AllNamesDesignated);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void List_GivesThosePrefixes_Trimmed()
    {
        var c = Config(" Cmd_ , Sim_,HMI_ ");
        Assert.Equal(new[] { "Cmd_", "Sim_", "HMI_" }, c.CommandPrefixes);
        Assert.Equal("LGX_GATE_COMMAND_PREFIXES", c.CommandPrefixesSource);
        Assert.False(c.AllNamesDesignated);
        Assert.Equal(new[] { "Safety_" }, c.SafetyPrefixes);
    }

    [Fact]
    public void Wildcard_Alone_DesignatesEveryName_WithoutWarning()
    {
        var c = Config("*");
        Assert.True(c.AllNamesDesignated);
        Assert.Equal("*", c.CommandPrefixesWire);
        Assert.Equal("LGX_GATE_COMMAND_PREFIXES", c.CommandPrefixesSource);
        Assert.Empty(c.Warnings);
        Assert.Equal(new[] { "Safety_" }, c.SafetyPrefixes);
    }

    [Fact]
    public void Wildcard_MixedWithOthers_IsWildcard_WithWarning()
    {
        var c = Config("Cmd_, *");
        Assert.True(c.AllNamesDesignated);
        Assert.Equal("*", c.CommandPrefixesWire);
        var warning = Assert.Single(c.Warnings);
        Assert.Contains("LGX_GATE_COMMAND_PREFIXES", warning);
    }

    [Theory]
    [InlineData("Cmd-", "Cmd-")]
    [InlineData("Cmd_,Sim x", "Sim x")]
    [InlineData("Cmd_,,Sim_", "")]
    [InlineData("Cmd*", "Cmd*")]
    [InlineData("Cmd_.Speed", "Cmd_.Speed")]
    public void InvalidEntry_IsTypedError_NamingVariableAndEntry(string raw, string entry)
    {
        var e = Assert.IsType<GateConfigInvalidError>(GateConfigResolver.Resolve(raw));
        Assert.Equal("GateConfigInvalid", e.Kind);
        Assert.Equal("LGX_GATE_COMMAND_PREFIXES", e.Variable);
        Assert.Equal(entry, e.Entry);
        Assert.Contains("LGX_GATE_COMMAND_PREFIXES", e.Error);
        Assert.Contains($"'{entry}'", e.Error);
    }

    [Fact]
    public void Current_ThrowsForInvalidValue_AndReadsTheSeam()
    {
        using (GateConfigResolver.UseValueForTesting("Bad-Prefix"))
            Assert.Throws<GateConfigInvalidException>(() => GateConfigResolver.Current);

        using (GateConfigResolver.UseValueForTesting("hmi_"))
            Assert.Equal(new[] { "hmi_" }, GateConfigResolver.Current.CommandPrefixes);
    }

    [Fact]
    public void CaseInsensitive_PrefixGrantsDifferentlyCasedTag()
    {
        var project = Build(Tag("Hmi_Speed"));
        var policy = GateEngine.DerivePolicy(project, Config("hMI_"));
        Assert.Contains(policy.Grants, g => g.Tag == "Hmi_Speed");

        var def = GateEngine.DerivePolicy(project, Config(null));
        Assert.Equal(DenyReason.NotDesignated, Assert.Single(def.Denies, d => d.Tag == "Hmi_Speed").Reason);
    }

    [Fact]
    public void NotDesignated_Provenance_NamesTheVariable_OnlyWhenSet()
    {
        var project = Build(Tag("Plain_Tag"));

        var set = GateEngine.DerivePolicy(project, Config("Cmd_,HMI_"));
        Assert.Contains("set by LGX_GATE_COMMAND_PREFIXES",
            Assert.Single(set.Denies, d => d.Tag == "Plain_Tag").Provenance);

        var def = GateEngine.DerivePolicy(project, Config(null));
        Assert.DoesNotContain("LGX_GATE_COMMAND_PREFIXES",
            Assert.Single(def.Denies, d => d.Tag == "Plain_Tag").Provenance);

        Assert.Equal("Cmd_ or HMI_ (set by LGX_GATE_COMMAND_PREFIXES)",
            GateConfigResolver.DescribePrefixes(Config("Cmd_,HMI_")));
        Assert.Equal("Cmd_ or Sim_", GateConfigResolver.DescribePrefixes(Config(null)));
    }

    // ── Source scan ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GateConfig.Default</c> is read only by the resolver: no enforcement or reporting
    /// site may bypass <c>LGX_GATE_COMMAND_PREFIXES</c> by using it directly.
    /// </summary>
    [Fact]
    public void NoEnforcementSiteUsesDefaultDirectly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.sln").Length == 0) dir = dir.Parent;
        Assert.NotNull(dir);

        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Where(f => Path.GetFileName(f) != "GateConfigResolver.cs")
            .Where(f => File.ReadAllText(f).Contains("GateConfig.Default", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(dir.FullName, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "GateConfig.Default must only be used by GateConfigResolver; found in: " + string.Join(", ", offenders));
    }

    // ── '*' versus default on one fixture ────────────────────────────────────

    private static string Tag(string name, string access = "Read/Write", string extra = "", string tagType = "Base") =>
        $"""<Tag Name="{name}" TagType="{tagType}" ExternalAccess="{access}" Constant="false" DataType="DINT" Radix="Decimal"{extra}><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static L5X Build(string tags, string rung = "NOP();") => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules /><AddOnInstructionDefinitions />
            <Tags>{tags}</Tags>
            <Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
                <Tags />
                <Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text>{rung}</Text></Rung></RLLContent></Routine></Routines>
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private const string FixtureRung = "XIC(Hmi_Setpoint)OTE(Motor_Run);";

    private static L5X Fixture() => Build(
        Tag("Hmi_Setpoint") +
        Tag("Motor_Run") +
        $"""<Tag Name="Motor_Alias" TagType="Alias" AliasFor="Motor_Run" Radix="Decimal" ExternalAccess="Read/Write" />""" +
        Tag("Safety_Mode") +
        Tag("Hmi_ReadOnly", access: "Read Only") +
        $"""<Tag Name="Remote_Value" TagType="Consumed" ExternalAccess="Read/Write" DataType="DINT"><ConsumeInfo Producer="Other" RemoteTag="Remote_Value" RPI="20" /></Tag>""",
        FixtureRung);

    [Fact]
    public void Wildcard_GrantsOnlyTheNonPrefixedNotLogicWrittenTag_EveryOtherDenyStands()
    {
        var project = Fixture();

        var def = GateEngine.DerivePolicy(project, Config(null));
        var star = GateEngine.DerivePolicy(project, Config("*"));

        // Default: nothing here has a command prefix.
        Assert.Empty(def.Grants);
        Assert.Equal(DenyReason.NotDesignated, Assert.Single(def.Denies, d => d.Tag == "Hmi_Setpoint").Reason);

        // '*': the one tag that only failed the naming rule is granted.
        Assert.Equal(new[] { "Hmi_Setpoint" }, star.Grants.Select(g => g.Tag).ToArray());

        DenyReason Reason(string tag) => Assert.Single(star.Denies, d => d.Tag == tag).Reason;
        Assert.Equal(DenyReason.ProgramWritten, Reason("Motor_Run"));
        Assert.Equal(DenyReason.ProgramWritten, Reason("Motor_Alias"));
        Assert.Equal(DenyReason.SafetyTag, Reason("Safety_Mode"));
        Assert.Equal(DenyReason.ExternalAccessNotReadWrite, Reason("Hmi_ReadOnly"));
        Assert.Equal(DenyReason.UnknownWriteContext, Reason("Remote_Value"));

        // The enforced grant text names the wildcard rule.
        Assert.Contains("LGX_GATE_COMMAND_PREFIXES=*", star.Grants.Single().Provenance);
    }

    [Fact]
    public void Wildcard_StillDeniesLogicWrittenTagsInStructuredText()
    {
        var project = Build(Tag("Hmi_Setpoint") + Tag("St_Driven"), "NOP();")
            ;
        var st = L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes /><Modules /><AddOnInstructionDefinitions />
                <Tags>{Tag("Hmi_Setpoint")}{Tag("St_Driven")}</Tags>
                <Programs>
                  <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
                    <Tags />
                    <Routines><Routine Name="MainRoutine" Type="ST"><STContent><Line Number="0"><![CDATA[St_Driven := Hmi_Setpoint;]]></Line></STContent></Routine></Routines>
                  </Program>
                </Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);

        var star = GateEngine.DerivePolicy(st, Config("*"));

        Assert.Contains(star.Grants, g => g.Tag == "Hmi_Setpoint");
        Assert.Equal(DenyReason.ProgramWritten, Assert.Single(star.Denies, d => d.Tag == "St_Driven").Reason);
        Assert.NotNull(project);
    }

    // ── Write path ───────────────────────────────────────────────────────────

    private static GatedTagService Gated(GateConfig config, out VirtualTagService inner)
    {
        inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Hmi_Setpoint"] = (0, "DINT"),
            ["Motor_Run"]    = (0, "DINT"),
        });
        return new GatedTagService(inner, GateEngine.DerivePolicy(Fixture(), config));
    }

    [Fact]
    public async Task WritePath_Wildcard_GrantedTagPassesGate_LogicWrittenTagRefusedProgramWritten()
    {
        var gated = Gated(Config("*"), out var inner);

        await gated.WriteAsync("Hmi_Setpoint", 7);
        Assert.Equal(7, Convert.ToInt32((await inner.ReadAsync("Hmi_Setpoint")).Value));

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Motor_Run", 1));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains(nameof(DenyReason.ProgramWritten), ex.Message);
    }

    [Fact]
    public async Task WritePath_Default_RefusesTheSameTagAsNotDesignated()
    {
        var gated = Gated(Config(null), out _);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Hmi_Setpoint", 7));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains(nameof(DenyReason.NotDesignated), ex.Message);
    }

    [Fact]
    public async Task WriteTagHandler_NotDesignatedHint_NamesActivePrefixesAndVariable()
    {
        using var scope = GateConfigResolver.UseValueForTesting("Cmd_,Pnl_");
        var gated = Gated(Config("Cmd_,Pnl_"), out _);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);

        var error = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Hmi_Setpoint", "7", session));

        Assert.Contains("Cmd_ or Pnl_ (set by LGX_GATE_COMMAND_PREFIXES)", error.Hint);
    }

    // ── Connect and gate_report fields ───────────────────────────────────────

    private static readonly string DemoPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private static async Task<object> ConnectVirtual()
    {
        var projects = new ProjectService();
        projects.Load(DemoPath);
        return await ConnectHandler.ExecuteAsync("", "", useVirtual: true, projects, new TagSessionService());
    }

    [Fact]
    public async Task Connect_Default_ReportsDefaultPrefixesAndSource()
    {
        using var scope = GateConfigResolver.UseValueForTesting(null);
        var ok = Assert.IsType<ConnectResult>(await ConnectVirtual());

        var json = JsonSerializer.SerializeToElement(ok);
        Assert.Equal(new[] { "Cmd_", "Sim_" },
            json.GetProperty("commandPrefixes").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("default", json.GetProperty("commandPrefixesSource").GetString());
        Assert.False(json.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task Connect_Wildcard_ReportsStar_AndGatesWithIt()
    {
        using var scope = GateConfigResolver.UseValueForTesting("*");
        var ok = Assert.IsType<ConnectResult>(await ConnectVirtual());

        var json = JsonSerializer.SerializeToElement(ok);
        Assert.Equal("*", json.GetProperty("commandPrefixes").GetString());
        Assert.Equal("LGX_GATE_COMMAND_PREFIXES", json.GetProperty("commandPrefixesSource").GetString());
        Assert.True(ok.Gated);
    }

    [Fact]
    public async Task Connect_WildcardMixed_AddsWarningToResult()
    {
        using var scope = GateConfigResolver.UseValueForTesting("Cmd_,*");
        var ok = Assert.IsType<ConnectResult>(await ConnectVirtual());

        Assert.Equal("*", ok.CommandPrefixes);
        var warning = Assert.Single(ok.Warnings!);
        Assert.Contains("treated as '*'", warning);
        Assert.Contains(warning, ok.Message);
    }

    [Fact]
    public async Task Connect_InvalidEntry_FailsWithGateConfigInvalid()
    {
        using var scope = GateConfigResolver.UseValueForTesting("Cmd_,Bad-Entry");
        var error = Assert.IsType<GateConfigInvalidError>(await ConnectVirtual());

        Assert.Equal("GateConfigInvalid", error.Kind);
        Assert.Equal("LGX_GATE_COMMAND_PREFIXES", error.Variable);
        Assert.Equal("Bad-Entry", error.Entry);
    }

    [Fact]
    public async Task Connect_InvalidEntry_FailsOnEveryBackend_BeforeAnyConnectionWork()
    {
        using var scope = GateConfigResolver.UseValueForTesting("no spaces");
        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, new ProjectService(), new TagSessionService(),
            backend: "cip", cipConnectProbe: (_, _) => Task.CompletedTask);

        Assert.IsType<GateConfigInvalidError>(result);
    }

    [Fact]
    public void GateReport_Enforced_ReportsPrefixesAndSource()
    {
        var svc = new ProjectService();
        svc.Load(DemoPath);

        using (GateConfigResolver.UseValueForTesting(null))
        {
            using var doc = JsonDocument.Parse(GateReportHandler.Serialize(GateReportHandler.Execute(svc)));
            Assert.Equal(new[] { "Cmd_", "Sim_" },
                doc.RootElement.GetProperty("commandPrefixes").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal("default", doc.RootElement.GetProperty("commandPrefixesSource").GetString());
        }

        using (GateConfigResolver.UseValueForTesting("*"))
        {
            using var doc = JsonDocument.Parse(GateReportHandler.Serialize(GateReportHandler.Execute(svc)));
            Assert.Equal("*", doc.RootElement.GetProperty("commandPrefixes").GetString());
            Assert.Equal("LGX_GATE_COMMAND_PREFIXES", doc.RootElement.GetProperty("commandPrefixesSource").GetString());
            Assert.Equal("enforced", doc.RootElement.GetProperty("policy").GetString());
        }
    }

    [Fact]
    public void GateReport_Hypothetical_NamesTheEnforcedPrefixesInTheDisclaimer()
    {
        var svc = new ProjectService();
        svc.Load(DemoPath);

        using var scope = GateConfigResolver.UseValueForTesting("*");
        using var doc = JsonDocument.Parse(GateReportHandler.Serialize(
            GateReportHandler.Execute(svc, new[] { "Sp_" })));

        Assert.Equal("hypothetical", doc.RootElement.GetProperty("policy").GetString());
        Assert.Equal("request", doc.RootElement.GetProperty("commandPrefixesSource").GetString());
        Assert.Contains("The enforced policy uses commandPrefixes: '*' (set by LGX_GATE_COMMAND_PREFIXES)",
            doc.RootElement.GetProperty("disclaimer").GetString());
    }

    [Fact]
    public void GateReport_InvalidVariable_IsAnErrorNotAThrow()
    {
        var svc = new ProjectService();
        svc.Load(DemoPath);

        using var scope = GateConfigResolver.UseValueForTesting("Bad-Entry");
        var error = Assert.IsType<GateReportError>(GateReportHandler.Execute(svc));
        Assert.Contains("Bad-Entry", error.Error);
    }

    [Fact]
    public async Task Doctor_ServerCheck_ShowsEffectivePrefixes()
    {
        var caps = new Capabilities(LiveWritesEnabled: false);

        var set = await DoctorHandler.ExecuteAsync(null, null,
            new DoctorHandlerTests.FakeEnv { GateConfig = "Cmd_,HMI_" }, caps);
        Assert.Contains("LGX_GATE_COMMAND_PREFIXES=Cmd_,HMI_ (LGX_GATE_COMMAND_PREFIXES)",
            set.Checks.Single(c => c.Id == "server").Observed);

        var unset = await DoctorHandler.ExecuteAsync(null, null, new DoctorHandlerTests.FakeEnv(), caps);
        Assert.Contains("LGX_GATE_COMMAND_PREFIXES=Cmd_,Sim_ (default)",
            unset.Checks.Single(c => c.Id == "server").Observed);
    }
}
