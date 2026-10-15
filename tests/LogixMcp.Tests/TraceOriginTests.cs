using System.Text;
using System.Text.Json;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_trace_origin</c>: the backward origin trace. Projects are built inline so each test shows
/// the exact logic it traces; the property test runs the whole fixture corpus.
/// </summary>
public class TraceOriginTests : IDisposable
{
    private readonly string _tempDir;

    public TraceOriginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"traceorigin-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Inline project builders ──────────────────────────────────────────────

    private static string Tag(string name, string type = "BOOL", string extra = "") =>
        $"""<Tag Name="{name}" TagType="Base" {extra} ExternalAccess="Read/Write" Constant="false" DataType="{type}" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="{type}" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static string Alias(string name, string target) =>
        $"""<Tag Name="{name}" TagType="Alias" AliasFor="{target}" Radix="Decimal" ExternalAccess="Read/Write" />""";

    private static string Rll(string name, params string[] rungs) =>
        $"""<Routine Name="{name}" Type="RLL"><RLLContent>{string.Concat(rungs.Select((r, i) => $"""<Rung Number="{i}" Type="N"><Text><![CDATA[{r}]]></Text></Rung>"""))}</RLLContent></Routine>""";

    private static string St(string name, params string[] lines) =>
        $"""<Routine Name="{name}" Type="ST"><STContent>{string.Concat(lines.Select((l, i) => $"""<Line Number="{i}"><![CDATA[{l}]]></Line>"""))}</STContent></Routine>""";

    private static string Program(string name, string tags, params string[] routines) =>
        $"""
        <Program Name="{name}" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="Main">
          <Tags>{tags}</Tags>
          <Routines>{string.Concat(routines)}</Routines>
        </Program>
        """;

    private static L5X Project(string programs, string controllerTags = "", string dataTypes = "") =>
        L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes>{dataTypes}</DataTypes>
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags>{controllerTags}</Tags>
                <Programs>{programs}</Programs>
                <Tasks />
                <ParameterConnections />
              </Controller>
            </RSLogix5000Content>
            """);

    private static TraceGraph Trace(L5X project, string tag, int? maxDepth = null, string? focus = null)
    {
        var graph = TraceOriginHandler.BuildGraph(project, tag, maxDepth, focus, out var error);
        Assert.True(graph is not null, error?.Error);
        return graph!;
    }

    private static TraceNode Write(TraceGraph g, string program, string location) =>
        Assert.Single(g.WriteNodes, w => w.Program == program && w.Location == location);

    private static JsonElement Json(object result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement;

    private const string SrcType = """
        <DataType Name="Src" Family="NoFamily" Class="User">
          <Members>
            <Member Name="AirBlow" DataType="BOOL" Dimension="0" Radix="Decimal" Hidden="false" ExternalAccess="Read/Write" />
            <Member Name="Other" DataType="BOOL" Dimension="0" Radix="Decimal" Hidden="false" ExternalAccess="Read/Write" />
          </Members>
        </DataType>
        """;

    // ── Alias chain ──────────────────────────────────────────────────────────

    /// <summary>A plain <c>Line_A.Cmd</c> names the program tag the same way <c>logix_xref</c> accepts it.</summary>
    [Fact]
    public void TraceOrigin_AcceptsLineA_Tag_ProgramDotTagForm()
    {
        var project = Project(
            Program("Line_A", Tag("Cmd") + Tag("Start"), Rll("Main", "XIC(Start)OTE(Cmd);")));

        var plain = Trace(project, "Line_A.Cmd");
        var qualified = Trace(project, "Program:Line_A.Cmd");

        Assert.Equal(qualified.Resolved, plain.Resolved);
        Assert.Equal("Line_A.Cmd", plain.Requested);
        Assert.Single(plain.WriteNodes, w => w.Program == "Line_A" && w.Location == "rung 0");
    }

    /// <summary>A two-link alias chain in ProgA leads to a base tag that ProgB unlatches under two contacts.</summary>
    [Fact]
    public void TraceOrigin_AliasChain_ReachesUnlatchInOtherProgram_WithGatingContacts()
    {
        var project = Project(
            Program("ProgA",
                Alias("Run_Alias", "Run_Latch") + Alias("Run_Show", "Run_Alias") + Tag("Start"),
                Rll("Main", "XIC(Start)OTL(Run_Alias);")) +
            Program("ProgB",
                Tag("Fault") + Tag("Reset"),
                Rll("Main", "XIC(Fault)XIO(Reset)OTU(Run_Latch);")),
            controllerTags: Tag("Run_Latch"));

        var g = Trace(project, "Program:ProgA.Run_Show");

        var unlatch = Write(g, "ProgB", "rung 0");
        Assert.Equal("OTU", unlatch.Instruction);
        Assert.Equal("unlatch", unlatch.Value);
        Assert.Equal("implicit", unlatch.ValueKind);
        Assert.NotNull(unlatch.Conditions);
        Assert.Contains(unlatch.Conditions!, c => c.Text == "XIC(Fault)" && c.Tags.Contains("Program:ProgB.Fault"));
        Assert.Contains(unlatch.Conditions!, c => c.Text == "XIO(Reset)" && c.Tags.Contains("Program:ProgB.Reset"));

        // The same tag is latched through the alias in ProgA.
        var latch = Write(g, "ProgA", "rung 0");
        Assert.Equal("OTL", latch.Instruction);
        Assert.Equal("Program:ProgA.Run_Alias", latch.Via);

        // The root is the outer alias; the alias edges lead to the storage tag the writes hang off.
        var root = g.Nodes.Single(n => n.Root);
        Assert.Equal("Program:ProgA.Run_Show", root.Tag);
        Assert.Equal("Run_Alias", root.AliasOf);
        Assert.Equal("Run_Latch", g.Resolved);
        Assert.Contains(g.Edges, e => e.Kind == "alias");
        Assert.Contains(g.Literals, l => l.Value == "unlatch" && l.Role == "implicit");
    }

    /// <summary>The alias chain is also followed when the trace starts from the base tag itself.</summary>
    [Fact]
    public void TraceOrigin_BaseTag_FindsWritesMadeThroughAlias()
    {
        var project = Project(
            Program("ProgA", Alias("Run_Alias", "Run_Latch") + Tag("Start"), Rll("Main", "XIC(Start)OTL(Run_Alias);")),
            controllerTags: Tag("Run_Latch"));

        var g = Trace(project, "Run_Latch");

        Assert.Single(g.WriteNodes);
        Assert.Equal("Program:ProgA.Run_Alias", Write(g, "ProgA", "rung 0").Via);
    }

    // ── ST ───────────────────────────────────────────────────────────────────

    /// <summary>An ST assignment inside an IF is a write gated by the IF, with its right-hand side as the source.</summary>
    [Fact]
    public void TraceOrigin_StAssignmentInsideIf_IsWriteGatedByIf()
    {
        var project = Project(
            Program("Main",
                Tag("Start") + Tag("Fault") + Tag("Setpoint", "REAL") + Tag("Speed", "REAL"),
                St("Main",
                    "IF Start AND NOT Fault THEN",
                    "    Speed := Setpoint * 2.0;",
                    "END_IF;")));

        var g = Trace(project, "Program:Main.Speed");

        var w = Assert.Single(g.WriteNodes);
        Assert.Equal("ST", w.Kind);
        Assert.Equal(":=", w.Instruction);
        Assert.Equal("ST line 1", w.Location);
        Assert.Contains("Setpoint", w.Text);
        var gate = Assert.Single(w.Conditions!);
        Assert.Equal("if", gate.Kind);
        Assert.Contains("Start AND NOT Fault", gate.Text);
        Assert.Contains("Program:Main.Start", gate.Tags);
        Assert.Contains("Program:Main.Fault", gate.Tags);
        Assert.Contains(g.Edges, e => e.From == w.Id && e.Kind == "source" && g.Nodes.Single(n => n.Id == e.To).Tag == "Program:Main.Setpoint");
        Assert.Contains(g.ExternalInputs, e => e.Tag == "Program:Main.Setpoint");
    }

    /// <summary>A write outside any IF carries no gating condition.</summary>
    [Fact]
    public void TraceOrigin_StAssignmentOutsideIf_HasNoCondition_AndLiteralStopsTrace()
    {
        var project = Project(Program("Main", Tag("Count", "DINT"), St("Main", "Count := 5;")));

        var g = Trace(project, "Program:Main.Count");

        var w = Assert.Single(g.WriteNodes);
        Assert.Null(w.Conditions);
        Assert.Equal("5", w.Value);
        Assert.Equal("literal", w.ValueKind);
        Assert.Contains(g.Literals, l => l.Value == "5" && l.Role == "source");
        Assert.Empty(g.ExternalInputs.Where(e => e.Tag == "5"));
    }

    // ── Cycle ────────────────────────────────────────────────────────────────

    /// <summary>A := B and B := A is recorded as a cycle, and the trace ends.</summary>
    [Fact]
    public void TraceOrigin_Cycle_IsRecordedAndNeverLooped()
    {
        var project = Project(Program("Main",
            Tag("A", "DINT") + Tag("B", "DINT"),
            Rll("Main", "MOV(B,A);", "MOV(A,B);")));

        var g = Trace(project, "Program:Main.A");

        Assert.Single(g.Cycles);
        Assert.Equal(new[] { "Program:Main.A", "Program:Main.B", "Program:Main.A" }, g.Cycles[0].Path);
        Assert.Contains(g.Edges, e => e.Cycle);
        Assert.Equal(2, g.WriteNodes.Count());
        Assert.Equal(2, g.TagNodes.Count());
        Assert.Contains("cycle", TraceRenderer.Text(g));
    }

    /// <summary>A seal-in rung (the tag gates its own latch) is a one-tag cycle, not an endless trace.</summary>
    [Fact]
    public void TraceOrigin_SealIn_IsOneTagCycle()
    {
        var project = Project(Program("Main", Tag("Start") + Tag("Run"), Rll("Main", "[XIC(Start),XIC(Run)]OTE(Run);")));

        var g = Trace(project, "Program:Main.Run");

        Assert.Contains(g.Cycles, c => c.Path.SequenceEqual(new[] { "Program:Main.Run", "Program:Main.Run" }));
        Assert.Single(g.WriteNodes);
    }

    // ── JSR gap ──────────────────────────────────────────────────────────────

    /// <summary>A value returned by a JSR parameter is listed as a gap: the called routine is not followed.</summary>
    [Fact]
    public void TraceOrigin_JsrReturnParameter_IsListedAsGap()
    {
        var project = Project(Program("Main",
            Tag("In1", "DINT") + Tag("Result", "DINT"),
            Rll("Main", "JSR(Calc,1,In1,Result);"),
            Rll("Calc", "NOP();")));

        var g = Trace(project, "Program:Main.Result");

        var gap = Assert.Single(g.Gaps, x => x.Kind == "JsrParameter");
        Assert.Contains("Calc", gap.Detail);
        Assert.Contains("Main/Main rung 0", gap.Location);
        var w = Assert.Single(g.WriteNodes);
        Assert.Equal("JSR", w.Instruction);
    }

    // ── UDT member ───────────────────────────────────────────────────────────

    /// <summary>
    /// A member path is one reference: tracing Source9.AirBlow shows its own writers, not the writers of
    /// Source1.AirBlow nor of the other members of Source9.
    /// </summary>
    [Fact]
    public void TraceOrigin_UdtMember_Source9AirBlow_DoesNotPullInEveryAirBlow()
    {
        var project = Project(
            Program("Main",
                Tag("Cmd9") + Tag("Cmd1") + Tag("CmdOther"),
                Rll("Main",
                    "XIC(Cmd9)OTE(Source9.AirBlow);",
                    "XIC(Cmd1)OTE(Source1.AirBlow);",
                    "XIC(CmdOther)OTE(Source9.Other);")),
            controllerTags: Tag("Source9", "Src") + Tag("Source1", "Src"),
            dataTypes: SrcType);

        var g = Trace(project, "Source9.AirBlow");

        var w = Assert.Single(g.WriteNodes);
        Assert.Equal("rung 0", w.Location);
        Assert.DoesNotContain(g.Nodes, n => (n.Tag ?? "").Contains("Source1"));
        Assert.DoesNotContain(g.Nodes, n => (n.Tag ?? "").Contains("CmdOther"));
        Assert.Contains(g.TagNodes, n => n.Tag == "Program:Main.Cmd9");
        Assert.Equal("Source9.AirBlow", g.Resolved);
    }

    /// <summary>A write to the whole structure reaches its members.</summary>
    [Fact]
    public void TraceOrigin_UdtMember_WholeStructureWrite_IsAWriterOfTheMember()
    {
        var project = Project(
            Program("Main", Tag("Blank", "Src"), Rll("Main", "COP(Blank,Source9,1);")),
            controllerTags: Tag("Source9", "Src"),
            dataTypes: SrcType);

        var g = Trace(project, "Source9.AirBlow");

        Assert.Single(g.WriteNodes);
    }

    // ── Indirect addressing, MSG, FBD, AOI, depth, focus ─────────────────────

    /// <summary>A variable array index is a gap; the writers of every element are shown.</summary>
    [Fact]
    public void TraceOrigin_VariableIndex_IsListedAsIndirectAddressGap()
    {
        var project = Project(Program("Main",
            Tag("Arr", "DINT", "Dimensions=\"4\"") + Tag("Idx", "DINT"),
            Rll("Main", "MOV(7,Arr[Idx]);")));

        var g = Trace(project, "Program:Main.Arr[2]");

        Assert.Contains(g.Gaps, x => x.Kind == "IndirectAddress");
        Assert.Single(g.WriteNodes);
    }

    /// <summary>An instruction the classifier does not know is a gap, never silently treated as a read or a write.</summary>
    [Fact]
    public void TraceOrigin_UnclassifiedInstruction_IsListedAsGap()
    {
        var project = Project(Program("Main",
            Tag("Q") + Tag("V", "DINT"),
            Rll("Main", "MyMysteryInstr(V,Q);")));

        var g = Trace(project, "Program:Main.Q");

        Assert.Contains(g.Gaps, x => x.Kind == "UnclassifiedInstruction");
        Assert.Contains(g.ExternalInputs, e => e.Tag == "Program:Main.Q");
    }

    /// <summary>Tracing with a program filter drops other programs' writes and says so.</summary>
    [Fact]
    public void TraceOrigin_Focus_RestrictsWritesToOneProgram()
    {
        var project = Project(
            Program("ProgA", Tag("X"), Rll("Main", "XIC(X)OTL(Shared);")) +
            Program("ProgB", Tag("Y"), Rll("Main", "XIC(Y)OTU(Shared);")),
            controllerTags: Tag("Shared"));

        var all = Trace(project, "Shared");
        var focused = Trace(project, "Shared", focus: "progb");

        Assert.Equal(2, all.WriteNodes.Count());
        var w = Assert.Single(focused.WriteNodes);
        Assert.Equal("ProgB", w.Program);
        Assert.Contains(focused.Gaps, x => x.Kind == "OutsideFocus");
    }

    /// <summary>maxDepth stops the walk and lists the tags whose writers were not followed.</summary>
    [Fact]
    public void TraceOrigin_MaxDepth_StopsAndReportsUnfollowedTags()
    {
        var project = Project(Program("Main",
            Tag("A", "DINT") + Tag("B", "DINT") + Tag("C", "DINT") + Tag("D", "DINT"),
            Rll("Main", "MOV(B,A);", "MOV(C,B);", "MOV(D,C);")));

        var g = Trace(project, "Program:Main.A", maxDepth: 2);

        Assert.Equal(2, g.WriteNodes.Count());
        Assert.Contains("Program:Main.C", g.DepthLimited);
        Assert.False(g.TagNodes.Single(n => n.Tag == "Program:Main.C").Expanded);
        Assert.DoesNotContain(g.Nodes, n => n.Tag == "Program:Main.D");
    }

    /// <summary>An MSG destination is a write; the trace shows the remote element and the rung that starts the MSG.</summary>
    [Fact]
    public void TraceOrigin_MsgDestination_IsWriteWithTrigger()
    {
        var project = L5X.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "MsgRead.L5X"));
        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var denied = policy.Denies.Where(d => d.Reason == DenyReason.ProgramWritten).ToList();
        Assert.NotEmpty(denied);

        foreach (var d in denied)
        {
            var g = Trace(project, d.ScopedName ?? d.Tag);
            Assert.NotEmpty(g.WriteNodes);
        }
        var anyMsg = denied.Select(d => Trace(project, d.ScopedName ?? d.Tag)).Any(g => g.WriteNodes.Any(w => w.Kind == "MSG"));
        Assert.True(anyMsg, "expected the MSG fixture's destination to be traced to an MSG write");
    }

    // ── Output: truncation, JSON, formats, validation ────────────────────────

    private static L5X ManyWritesProject(int writers)
    {
        var tags = new StringBuilder(Tag("Target", "DINT"));
        var rungs = new List<string>();
        for (var i = 0; i < writers; i++)
        {
            tags.Append(Tag($"Src{i}", "DINT"));
            rungs.Add($"MOV(Src{i},Target);");
        }
        return Project(Program("Main", tags.ToString(), Rll("Main", rungs.ToArray())));
    }

    /// <summary>More than 200 nodes: the trace stops, says truncated, and tells the caller how to narrow it.</summary>
    [Fact]
    public void TraceOrigin_Over200Nodes_ReturnsTruncatedWithNarrowingAdvice()
    {
        var project = ManyWritesProject(150);

        var result = Json(TraceOriginHandler.Execute(project, "Program:Main.Target", format: "json"));

        Assert.True(result.GetProperty("truncated").GetBoolean());
        var advice = result.GetProperty("advice").GetString()!;
        Assert.Contains("focus", advice);
        Assert.Contains("maxDepth", advice);
        Assert.Equal(TraceGraph.MaxNodes, result.GetProperty("nodes").GetArrayLength());
    }

    /// <summary>Under the cap nothing is truncated and there is no advice.</summary>
    [Fact]
    public void TraceOrigin_UnderCap_IsNotTruncated()
    {
        var result = Json(TraceOriginHandler.Execute(ManyWritesProject(5), "Program:Main.Target", format: "json"));

        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.False(result.TryGetProperty("advice", out _));
    }

    /// <summary>A wide fan-out: <paramref name="writers"/> rungs each copy a long-named source into Target.</summary>
    private static L5X WideFanOutProject(int writers)
    {
        var tags = new StringBuilder(Tag("Target", "DINT"));
        var rungs = new List<string>();
        for (var i = 0; i < writers; i++)
        {
            tags.Append(Tag($"Conveyor_Section_Speed_Setpoint_{i}", "DINT"));
            rungs.Add($"MOV(Conveyor_Section_Speed_Setpoint_{i},Target);");
        }
        return Project(Program("Main", tags.ToString(), Rll("Main", rungs.ToArray())));
    }

    /// <summary>Over the default 12000-character budget: cut at whole lines, with the fields and hint set.</summary>
    [Fact]
    public void TraceOrigin_WideFanOut_OverDefaultBudget_CutsAtWholeLinesWithTruncationFields()
    {
        var project = WideFanOutProject(95);
        var full = TraceRenderer.Text(Trace(project, "Program:Main.Target"));
        Assert.True(full.Length > TraceOriginHandler.DefaultMaxChars, $"fixture is only {full.Length} chars");

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Target"));

        Assert.Equal(12000, r.GetProperty("maxChars").GetInt32());
        Assert.True(r.GetProperty("truncated").GetBoolean());
        var text = r.GetProperty("text").GetString()!;
        Assert.True(text.Length <= 12000, $"text is {text.Length} chars");
        Assert.True(r.GetProperty("omittedWrites").GetInt32() > 0);
        Assert.Contains("depth", r.GetProperty("truncatedAt").GetString());
        var hint = r.GetProperty("hint").GetString()!;
        Assert.Contains("focus", hint);
        Assert.Contains("smaller maxDepth", hint);

        // Whole lines only: every kept line of the tree is a line of the full rendering.
        var fullLines = full.Split('\n').ToHashSet();
        var kept = text.Split('\n');
        foreach (var line in kept.Take(kept.Length - 1)) Assert.Contains(line, fullLines);
        Assert.StartsWith("... output cut at maxChars 12000", kept[^1]);
    }

    /// <summary>The summary counts the whole trace even when the text is cut; omitted + shown writes add up.</summary>
    [Fact]
    public void TraceOrigin_WideFanOut_SummaryCoversWholeTraceWhenTruncated()
    {
        var project = WideFanOutProject(90);

        var small = Json(TraceOriginHandler.Execute(project, "Program:Main.Target", maxChars: 2000));
        var big = Json(TraceOriginHandler.Execute(project, "Program:Main.Target", maxChars: TraceOriginHandler.MaxCharsLimit));

        Assert.True(small.GetProperty("truncated").GetBoolean());
        Assert.False(big.GetProperty("truncated").GetBoolean());
        Assert.False(big.TryGetProperty("omittedWrites", out _));
        Assert.False(big.TryGetProperty("truncatedAt", out _));
        Assert.Equal(90, small.GetProperty("summary").GetProperty("writes").GetInt32());
        Assert.Equal(big.GetProperty("summary").GetRawText(), small.GetProperty("summary").GetRawText());

        var shown = System.Text.RegularExpressions.Regex.Matches(small.GetProperty("text").GetString()!, "written by ").Count;
        Assert.Equal(90, shown + small.GetProperty("omittedWrites").GetInt32());
    }

    /// <summary>The cut applies to the text only: format json still returns every node.</summary>
    [Fact]
    public void TraceOrigin_WideFanOut_JsonFormatIsNotCutByMaxChars()
    {
        var r = Json(TraceOriginHandler.Execute(WideFanOutProject(90), "Program:Main.Target", format: "json", maxChars: 500));

        Assert.False(r.TryGetProperty("text", out _));
        Assert.False(r.GetProperty("truncated").GetBoolean());
        Assert.Equal(181, r.GetProperty("nodes").GetArrayLength());
    }

    /// <summary>maxChars outside 500-1000000 is an InvalidValue error.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(499)]
    [InlineData(1_000_001)]
    public void TraceOrigin_MaxCharsOutOfRange_IsInvalidValue(int maxChars)
    {
        var r = Json(TraceOriginHandler.Execute(WideFanOutProject(2), "Program:Main.Target", maxChars: maxChars));

        Assert.Equal("InvalidValue", r.GetProperty("kind").GetString());
        Assert.Contains("maxChars", r.GetProperty("error").GetString());
    }

    /// <summary>maxChars is in the tool's input schema with default 12000.</summary>
    [Fact]
    public void TraceOrigin_Tool_HasMaxCharsParameterDefaultingTo12000()
    {
        var p = typeof(LogixProjectTools).GetMethod("TraceOrigin")!.GetParameters().Single(x => x.Name == "maxChars");

        Assert.Equal(typeof(int), p.ParameterType);
        Assert.Equal(12000, p.DefaultValue);
    }

    // ── S: controller status operands ────────────────────────────────────────

    /// <summary>Every S: status operand read by the traced rung is classified controllerStatus, not an external input.</summary>
    [Theory]
    [InlineData("S:FS")]
    [InlineData("S:N")]
    [InlineData("S:Z")]
    [InlineData("S:V")]
    [InlineData("S:C")]
    [InlineData("S:MINOR")]
    public void TraceOrigin_StatusOperand_IsControllerStatus_NotExternalInput(string operand)
    {
        var project = Project(Program("Main", Tag("Out"), Rll("Main", $"XIC({operand})OTE(Out);")));

        var g = Trace(project, "Program:Main.Out");

        var s = Assert.Single(g.ControllerStatus);
        Assert.Equal(operand, s.Tag);
        Assert.Equal("controllerStatus", s.Kind);
        Assert.Equal("controller status flag (set by the controller)", s.Detail);
        Assert.DoesNotContain(g.ExternalInputs, e => e.Tag.StartsWith("S:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The JSON and text output carry the label and keep S: operands out of externalInputs.</summary>
    [Fact]
    public void TraceOrigin_StatusOperand_JsonAndText_UseLabelAndOmitFromExternalInputs()
    {
        var project = Project(Program("Main", Tag("Out") + Tag("Go"), Rll("Main", "XIC(S:FS)XIC(Go)OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out", format: "all"));

        var status = r.GetProperty("controllerStatus");
        Assert.Equal("controllerStatus", Assert.Single(status.EnumerateArray()).GetProperty("kind").GetString());
        Assert.DoesNotContain(r.GetProperty("externalInputs").EnumerateArray(),
            e => e.GetProperty("tag").GetString()!.StartsWith("S:", StringComparison.Ordinal));
        Assert.Contains("controller status flag (set by the controller)", r.GetProperty("text").GetString());
    }

    /// <summary>The JSON carries every collection the contract names.</summary>
    [Fact]
    public void TraceOrigin_Json_ContainsNodesEdgesExternalInputsLiteralsGaps()
    {
        var project = Project(Program("Main",
            Tag("Go") + Tag("Out") + Tag("Sp", "DINT") + Tag("Val", "DINT"),
            Rll("Main", "XIC(Go)MOV(Sp,Val);", "XIC(Go)OTE(Out);", "JSR(Calc,1,Val,Out);"),
            Rll("Calc", "NOP();")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out", format: "json"));

        foreach (var name in new[] { "nodes", "edges", "externalInputs", "literals", "gaps" })
            Assert.Equal(JsonValueKind.Array, r.GetProperty(name).ValueKind);
        Assert.True(r.GetProperty("nodes").GetArrayLength() > 0);
        Assert.True(r.GetProperty("edges").GetArrayLength() > 0);
        Assert.True(r.GetProperty("externalInputs").GetArrayLength() > 0);
        Assert.True(r.GetProperty("literals").GetArrayLength() > 0);
        Assert.True(r.GetProperty("gaps").GetArrayLength() > 0);
    }

    /// <summary>Every edge of the JSON graph points at nodes that exist.</summary>
    [Fact]
    public void TraceOrigin_Json_EdgesReferenceExistingNodes()
    {
        var g = Trace(ManyWritesProject(40), "Program:Main.Target");
        var ids = g.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(g.Edges, e => { Assert.Contains(e.From, ids); Assert.Contains(e.To, ids); });
        Assert.Equal(ids.Count, g.Nodes.Count);
    }

    /// <summary>The text tree names the writing rung, the instruction and the gate.</summary>
    [Fact]
    public void TraceOrigin_TextFormat_ShowsWriterAndGate()
    {
        var project = Project(Program("Main", Tag("Go") + Tag("Out"), Rll("Main", "XIC(Go)OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out"));

        var text = r.GetProperty("text").GetString()!;
        Assert.Contains("Main/Main rung 0", text);
        Assert.Contains("OTE(Out)", text);
        Assert.Contains("when XIC(Go)", text);
        Assert.False(r.TryGetProperty("nodes", out _));
    }

    /// <summary>The Mermaid output is a flowchart with one box per writing rung.</summary>
    [Fact]
    public void TraceOrigin_MermaidFormat_HasOneBoxPerWritingRung()
    {
        var project = Project(Program("Main",
            Tag("Go") + Tag("A") + Tag("B") + Tag("Out"),
            Rll("Main", "XIC(Go)OTE(A);", "XIC(A)OTE(B);", "XIC(A)XIC(B)OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out", format: "mermaid"));

        var mermaid = r.GetProperty("mermaid").GetString()!;
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Equal(3, mermaid.Split('\n').Count(l => l.TrimStart().StartsWith('R') && l.Contains("[\"")));
        Assert.Contains("-.->", mermaid);
        Assert.False(r.TryGetProperty("text", out _));
    }

    /// <summary>format=all carries the text, the Mermaid source and the JSON graph.</summary>
    [Fact]
    public void TraceOrigin_AllFormat_CarriesTextMermaidAndGraph()
    {
        var project = Project(Program("Main", Tag("Go") + Tag("Out"), Rll("Main", "XIC(Go)OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out", format: "all"));

        Assert.True(r.TryGetProperty("text", out _));
        Assert.True(r.TryGetProperty("mermaid", out _));
        Assert.True(r.TryGetProperty("nodes", out _));
    }

    /// <summary>Bad input is refused with a structured error and the InvalidValue kind.</summary>
    [Theory]
    [InlineData("", null, null, "tag is required")]
    [InlineData("Program:Main.Out", 0, null, "maxDepth")]
    [InlineData("Program:Main.Nope", null, null, "not declared")]
    public void TraceOrigin_BadInput_ReturnsInvalidValueError(string tag, int? maxDepth, string? focus, string expected)
    {
        var project = Project(Program("Main", Tag("Out"), Rll("Main", "OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, tag, maxDepth, focus));

        Assert.Contains(expected, r.GetProperty("error").GetString());
        Assert.Equal(ErrorKinds.InvalidValue, r.GetProperty("kind").GetString());
    }

    /// <summary>A focus that names no program is a typed ProgramNotFound that lists the programs.</summary>
    [Fact]
    public void TraceOrigin_FocusNamingNoProgram_ReturnsProgramNotFound()
    {
        var project = Project(Program("Main", Tag("Out"), Rll("Main", "OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out", null, "NoSuchProgram"));

        Assert.Equal(ErrorKinds.ProgramNotFound, r.GetProperty("kind").GetString());
        Assert.Contains("focus", r.GetProperty("error").GetString());
        Assert.Contains("Main", r.GetProperty("hint").GetString());
    }

    /// <summary>An unknown format is refused.</summary>
    [Fact]
    public void TraceOrigin_UnknownFormat_ReturnsInvalidValueError()
    {
        var project = Project(Program("Main", Tag("Out"), Rll("Main", "OTE(Out);")));

        var r = Json(TraceOriginHandler.Execute(project, "Program:Main.Out", format: "svg"));

        Assert.Equal(ErrorKinds.InvalidValue, r.GetProperty("kind").GetString());
    }

    /// <summary>Without a project the tool says to open one.</summary>
    [Fact]
    public void TraceOrigin_NoProjectLoaded_SaysToOpenOne()
    {
        var r = Json(TraceOriginHandler.Execute(new ProjectService(), "X"));

        Assert.Contains("logix_open_project", r.GetProperty("error").GetString());
    }

    /// <summary>A bare name that is a program tag in exactly one program resolves to it.</summary>
    [Fact]
    public void TraceOrigin_BareProgramTagName_ResolvesWhenUnique()
    {
        var project = Project(Program("Main", Tag("Out"), Rll("Main", "OTE(Out);")));

        var g = Trace(project, "Out");

        Assert.Equal("Program:Main.Out", g.Resolved);
        Assert.Single(g.WriteNodes);
    }

    /// <summary>A tag nothing writes is an external input, and a constant is reported as one.</summary>
    [Fact]
    public void TraceOrigin_UnwrittenTags_AreExternalInputs()
    {
        var project = Project(Program("Main",
            Tag("Sw") + Tag("Cfg", "DINT", "").Replace("Constant=\"false\"", "Constant=\"true\"") + Tag("Out", "DINT"),
            Rll("Main", "XIC(Sw)MOV(Cfg,Out);")));

        var g = Trace(project, "Program:Main.Out");

        Assert.Contains(g.ExternalInputs, e => e.Tag == "Program:Main.Sw" && e.Kind == "unwritten");
        Assert.Contains(g.ExternalInputs, e => e.Tag == "Program:Main.Cfg" && e.Kind == "constant");
    }

    // ── Property: the gate and the trace agree ───────────────────────────────

    private static IEnumerable<string> CorpusFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures");
        return Directory.EnumerateFiles(root, "*.L5X", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every tag the write gate marks ProgramWritten has at least one write node in its trace, across the
    /// whole fixture corpus. The gate and the trace both classify writes with WriteClassifier.
    /// </summary>
    [Fact]
    public void TraceOrigin_EveryProgramWrittenTag_HasAWriteNode_AcrossCorpus()
    {
        var checkedTags = 0;
        var failures = new List<string>();
        foreach (var file in CorpusFiles())
        {
            L5X project;
            try { project = L5X.Load(file); }
            catch (Exception) { continue; }

            var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
            foreach (var d in policy.Denies.Where(d => d.Reason == DenyReason.ProgramWritten))
            {
                var tag = d.ScopedName ?? d.Tag;
                var graph = TraceOriginHandler.BuildGraph(project, tag, null, null, out var error);
                checkedTags++;
                if (graph is null) failures.Add($"{Path.GetFileName(file)}: {tag}: {error?.Error}");
                else if (!graph.WriteNodes.Any()) failures.Add($"{Path.GetFileName(file)}: {tag}: no write node");
            }
        }

        Assert.True(checkedTags > 0, "the corpus has no ProgramWritten tags to check");
        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedTags} tags:\n" + string.Join("\n", failures));
    }

    /// <summary>Tracing every declared tag of the corpus never throws, never exceeds the cap and never loops.</summary>
    [Fact]
    public void TraceOrigin_EveryCorpusTag_TracesWithoutThrowing()
    {
        foreach (var file in CorpusFiles())
        {
            L5X project;
            try { project = L5X.Load(file); }
            catch (Exception) { continue; }

            var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
            foreach (var d in policy.Denies.Concat<DenyEntry>(Array.Empty<DenyEntry>()).Take(40))
            {
                var graph = TraceOriginHandler.BuildGraph(project, d.ScopedName ?? d.Tag, null, null, out _);
                if (graph is null) continue;
                Assert.True(graph.Nodes.Count <= TraceGraph.MaxNodes, $"{Path.GetFileName(file)}: {d.Tag}");
                _ = TraceRenderer.Text(graph);
                _ = TraceRenderer.Mermaid(graph);
            }
        }
    }

    // ── Pending rungs ───────────────────────────

    /// <summary>One pending writer is "also writes"; several are "also write".</summary>
    [Theory]
    [InlineData(1, "also writes")]
    [InlineData(2, "also write ")]
    public void ExcludedPendingWriters_NoteAgreesWithTheNumberOfRungs(int pendingRungs, string expected)
    {
        var rungs = new List<(string, string)> { ("XIC(A)OTE(B);", "N") };
        for (var i = 0; i < pendingRungs; i++) rungs.Add(("XIC(A)OTE(B);", "I"));
        var (svc, session) = PendingImageFixture.Online(["A", "B"], rungs.ToArray());

        var response = Assert.IsType<TraceOriginResponse>(
            TraceOriginHandler.Execute(svc, "B", session: session, includePending: false));

        Assert.Contains(expected, response.Note);
        Assert.Equal(pendingRungs, response.PendingWriters!.Count);
    }

    /// <summary>With includePending the write nodes of staged, pending and tested rungs say so, and the note names them.</summary>
    [Fact]
    public void IncludePending_LabelsPendingWriteNodes_AndNoteNamesThem()
    {
        var (svc, session) = PendingImageFixture.Online(["A", "B"],
            ("XIC(A)OTE(B);", "N"), ("XIC(A)OTE(B);", "e"), ("XIC(A)OTE(B);", "I"));

        var response = Assert.IsType<TraceOriginResponse>(
            TraceOriginHandler.Execute(svc, "B", format: "json", session: session, includePending: true));

        Assert.NotNull(response.PendingIncluded);
        Assert.Contains(response.PendingIncluded!, w => w.RungIndex == 1 && w.State == "staged");
        Assert.Contains(response.PendingIncluded!, w => w.RungIndex == 2 && w.State == "pending");
        Assert.DoesNotContain(response.PendingIncluded!, w => w.RungIndex == 0);
        Assert.Contains("1 staged rung", response.Note);
        Assert.Contains("1 pending rung", response.Note);
        Assert.Contains(response.Nodes!, n => n.Note is not null && n.Note.Contains("staged", StringComparison.Ordinal));
        Assert.Contains(response.Nodes!, n => n.Note is not null && n.Note.Contains("pending", StringComparison.Ordinal));

        session.SetZoneTested("MainProgram", true);
        var tested = Assert.IsType<TraceOriginResponse>(
            TraceOriginHandler.Execute(svc, "B", format: "json", session: session, includePending: true));
        Assert.Contains(tested.PendingIncluded!, w => w.RungIndex == 2 && w.State == "tested");
        Assert.Contains("1 tested rung", tested.Note);
    }

    [Fact]
    public void IncludePending_WithNoPendingRungs_HasNoIncludedLabels()
    {
        var (svc, session) = PendingImageFixture.Online(["A", "B"], ("XIC(A)OTE(B);", "N"));

        var response = Assert.IsType<TraceOriginResponse>(
            TraceOriginHandler.Execute(svc, "B", session: session, includePending: true));

        Assert.Null(response.PendingIncluded);
    }
}
