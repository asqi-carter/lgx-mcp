using System.Text.Json;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Tests that <see cref="WriteClassifier"/> no longer ignores non-ladder routines:
/// ST assignments are proven program writes, and any reference to a tag inside an
/// FBD or SFC routine (which is not parsed) fails closed as unknown write context.
/// </summary>
public class NonLadderScanTests : IDisposable
{
    private readonly string _tempDir;

    public NonLadderScanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nonladder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string Tag(string name) =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>""";

    private const string StRoutine = """
        <Routine Name="StRoutine" Type="ST">
          <STContent>
            <Line Number="0"><![CDATA[// Cmd_InComment := 1;]]></Line>
            <Line Number="1"><![CDATA[(* Cmd_InBlock := 1;]]></Line>
            <Line Number="2"><![CDATA[   still comment Cmd_InBlock2 := 1; *)]]></Line>
            <Line Number="3"><![CDATA[IF Cmd_Read THEN]]></Line>
            <Line Number="4"><![CDATA[  Cmd_Written := 1;]]></Line>
            <Line Number="5"><![CDATA[  Cmd_Struct.Field[2] := Cmd_Read; Sim_Indexed[Idx[1]] := 5;]]></Line>
            <Line Number="6"><![CDATA[END_IF;]]></Line>
          </STContent>
        </Routine>
        """;

    private const string FbdRoutine = """
        <Routine Name="FbdRoutine" Type="FBD">
          <FBDContent SheetSize="Letter - 8.5 x 11 in" SheetOrientation="Landscape">
            <Sheet Number="1">
              <IRef ID="0" X="100" Y="100" Operand="Cmd_FbdRef" HideDesc="false" />
              <ORef ID="1" X="300" Y="100" Operand="Sim_FbdOut" HideDesc="false" />
              <Wire FromID="0" ToID="1" />
            </Sheet>
          </FBDContent>
        </Routine>
        """;

    private const string SfcRoutine = """
        <Routine Name="SfcRoutine" Type="SFC">
          <SFCContent SheetSize="Letter - 8.5 x 11 in" SheetOrientation="Landscape" StepName="Step" TransitionName="Tran" ActionName="Act" StopName="Stop">
            <Step ID="0" X="100" Y="100" Operand="Step_000" HideDesc="false" InitialStep="true" PresetUsesExpr="false" LimitHighUsesExpr="false" LimitLowUsesExpr="false" ShowActions="true">
              <Action ID="1" Operand="Action_000" Qualifier="N" IsBoolean="false"><Body><NText><![CDATA[Cmd_SfcRef := 1;]]></NText></Body></Action>
            </Step>
          </SFCContent>
        </Routine>
        """;

    private static L5X BuildProject(params string[] routines) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules /><AddOnInstructionDefinitions />
            <Tags>
              {Tag("Cmd_Read")}{Tag("Cmd_Written")}{Tag("Cmd_Struct")}{Tag("Sim_Indexed")}
              {Tag("Cmd_InComment")}{Tag("Cmd_InBlock")}{Tag("Cmd_InBlock2")}{Tag("Cmd_Clean")}
              {Tag("Cmd_FbdRef")}{Tag("Sim_FbdOut")}{Tag("Cmd_SfcRef")}
            </Tags>
            <Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="StRoutine">
                <Tags />
                <Routines>{string.Concat(routines)}</Routines>
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    [Fact]
    public void StAssignment_IsProgramWritten()
    {
        var project = BuildProject(StRoutine);

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Equal("Main/StRoutine (ST line 4)", scan.ProgramWritten["Cmd_Written"]);
        Assert.Equal("Main/StRoutine (ST line 5)", scan.ProgramWritten["Cmd_Struct"]);
        Assert.Equal("Main/StRoutine (ST line 5)", scan.ProgramWritten["Sim_Indexed"]);

        // Reads and commented-out assignments are not writes.
        Assert.DoesNotContain("Cmd_Read", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_InComment", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_InBlock", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_InBlock2", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var deny = policy.Denies.Single(d => d.Tag == "Cmd_Written");
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
        Assert.Contains("Main/StRoutine (ST line 4)", deny.Provenance);
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Read");
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_InComment");
    }

    [Fact]
    public void NonRetentiveAssignment_IsProgramWritten()
    {
        const string routine = """
            <Routine Name="StRoutine" Type="ST">
              <STContent>
                <Line Number="0"><![CDATA[Cmd_Clean [:=] 0;]]></Line>
                <Line Number="1"><![CDATA[Cmd_Written[:=]1; Cmd_Struct.Field[2] [ := ] 3;]]></Line>
                <Line Number="2"><![CDATA[Sim_Indexed[Idx[1]] [:=] 5;]]></Line>
                <Line Number="3"><![CDATA[Cmd_FbdRef := 1;]]></Line>
              </STContent>
            </Routine>
            """;
        var project = BuildProject(routine);

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Equal("Main/StRoutine (ST line 0)", scan.ProgramWritten["Cmd_Clean"]);
        Assert.Equal("Main/StRoutine (ST line 1)", scan.ProgramWritten["Cmd_Written"]);
        Assert.Equal("Main/StRoutine (ST line 1)", scan.ProgramWritten["Cmd_Struct"]);
        Assert.Equal("Main/StRoutine (ST line 2)", scan.ProgramWritten["Sim_Indexed"]);
        // Plain ':=' still works alongside '[:=]'.
        Assert.Equal("Main/StRoutine (ST line 3)", scan.ProgramWritten["Cmd_FbdRef"]);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_Clean").Reason);
    }

    [Fact]
    public void FbdOrSfcReference_UnknownWriteContext()
    {
        var project = BuildProject(FbdRoutine, SfcRoutine);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        var fbdRef = policy.Denies.Single(d => d.Tag == "Cmd_FbdRef");
        Assert.Equal(DenyReason.UnknownWriteContext, fbdRef.Reason);
        Assert.Contains("Main/FbdRoutine", fbdRef.Provenance);
        Assert.Contains("FBD", fbdRef.Provenance);

        var fbdOut = policy.Denies.Single(d => d.Tag == "Sim_FbdOut");
        Assert.Equal(DenyReason.UnknownWriteContext, fbdOut.Reason);

        var sfcRef = policy.Denies.Single(d => d.Tag == "Cmd_SfcRef");
        Assert.Equal(DenyReason.UnknownWriteContext, sfcRef.Reason);
        Assert.Contains("Main/SfcRoutine", sfcRef.Provenance);
        Assert.Contains("SFC", sfcRef.Provenance);

        // A command tag never mentioned in a non-ladder routine is still granted.
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Clean");
    }

    [Fact]
    public async System.Threading.Tasks.Task WriteToStWrittenTag_IsRefusedAndLoggedWithReasonAndProvenance()
    {
        var policy = GateEngine.DerivePolicy(BuildProject(StRoutine, FbdRoutine), GateConfig.Default);
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_Written"] = (false, "BOOL"),
            ["Cmd_FbdRef"]  = (false, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Cmd_Written", true));
        await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Cmd_FbdRef", true));

        var events = File.ReadAllLines(Path.Combine(_tempDir, "audit.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("event").GetString() == "gate_decision")
            .ToList();

        var st = events.Single(e => e.GetProperty("tag").GetString() == "Cmd_Written");
        Assert.Equal("denied", st.GetProperty("outcome").GetString());
        Assert.Equal("ProgramWritten", st.GetProperty("reason").GetString());
        Assert.Contains("(ST line 4)", st.GetProperty("provenance").GetString());

        var fbd = events.Single(e => e.GetProperty("tag").GetString() == "Cmd_FbdRef");
        Assert.Equal("UnknownWriteContext", fbd.GetProperty("reason").GetString());
        Assert.Contains("FBD", fbd.GetProperty("provenance").GetString());
    }

    // ── Fail-closed ST scan (statement joining, instruction destinations, AOIs, encoded) ──

    private const string AoiDefinition = """
        <AddOnInstructionDefinition Name="Aoi_Drive" Revision="1.0" ExecutePrescan="false" ExecutePostscan="false" ExecuteEnableInFalse="false" CreatedDate="2026-01-01T00:00:00.000Z" CreatedBy="t" EditedDate="2026-01-01T00:00:00.000Z" EditedBy="t" SoftwareRevision="v36.00">
          <Parameters>
            <Parameter Name="EnableIn" TagType="Base" DataType="BOOL" Usage="Input" Radix="Decimal" Required="false" Visible="false" ExternalAccess="Read Only" />
            <Parameter Name="EnableOut" TagType="Base" DataType="BOOL" Usage="Output" Radix="Decimal" Required="false" Visible="false" ExternalAccess="Read Only" />
            <Parameter Name="Run" TagType="Base" DataType="BOOL" Usage="Input" Radix="Decimal" Required="true" Visible="true" ExternalAccess="Read/Write" />
            <Parameter Name="Active" TagType="Base" DataType="BOOL" Usage="Output" Radix="Decimal" Required="true" Visible="true" ExternalAccess="Read Only" />
          </Parameters>
          <LocalTags />
          <Routines>
            <Routine Name="Logic" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[XIC(Run)OTE(Active);]]></Text></Rung></RLLContent></Routine>
          </Routines>
        </AddOnInstructionDefinition>
        """;

    /// <summary>One-program project with the given tags, optional AOI definition and routines.</summary>
    private static L5X BuildCustom(string[] tagNames, string aoi, params string[] routines) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules />
            <AddOnInstructionDefinitions>{aoi}</AddOnInstructionDefinitions>
            <Tags>{string.Concat(tagNames.Select(Tag))}</Tags>
            <Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                <Tags />
                <Routines>{string.Concat(routines)}</Routines>
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private static string StLines(params string[] lines) =>
        "<Routine Name=\"R\" Type=\"ST\"><STContent>" +
        string.Concat(lines.Select((l, i) => $"<Line Number=\"{i}\"><![CDATA[{l}]]></Line>")) +
        "</STContent></Routine>";

    [Fact]
    public void MultiLineAssignment_IsProgramWritten()
    {
        var project = BuildCustom(
            ["Cmd_Split", "Cmd_Tail", "Cmd_Cond", "Cmd_Read", "Cmd_Clean"], "",
            StLines(
                "Cmd_Split",                 // 0: target and := on different lines
                "   := Cmd_Read",            // 1
                "      AND TRUE;",           // 2
                "Cmd_Tail :=",               // 3
                "   1;",                     // 4
                "IF Cmd_Read THEN Cmd_Cond", // 5
                "  := 0; END_IF;"));         // 6

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Equal("Main/R (ST line 0)", scan.ProgramWritten["Cmd_Split"]);
        Assert.Equal("Main/R (ST line 3)", scan.ProgramWritten["Cmd_Tail"]);
        Assert.Equal("Main/R (ST line 5)", scan.ProgramWritten["Cmd_Cond"]);
        Assert.DoesNotContain("Cmd_Read", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_Split").Reason);
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Clean");
    }

    [Fact]
    public void StInstructionCall_DestIsProgramWritten()
    {
        var project = BuildCustom(
            ["Cmd_Src", "Cmd_CopDest", "Cmd_CpsDest", "Sim_Fill", "Cmd_Timer", "Cmd_Counter",
             "Cmd_UpDown", "Cmd_MovDest", "Cmd_Multi", "Cmd_Pure", "Cmd_Clean"], "",
            StLines(
                "COP(Cmd_Src, Cmd_CopDest, 1);",
                "CPS(Cmd_Src, Cmd_CpsDest, 1);",
                "FLL(0, Sim_Fill, 4);",
                "TONR(Cmd_Timer);",
                "RES(Cmd_Counter);",
                "CTUD(Cmd_UpDown);",
                "MOV(Cmd_Src, Cmd_MovDest);",
                "COP(Cmd_Src,",
                "    Cmd_Multi,",
                "    1);",
                "Out := SQRT(Cmd_Pure);"));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Equal("Main/R (ST line 0 via COP)", scan.ProgramWritten["Cmd_CopDest"]);
        Assert.Contains("via CPS", scan.ProgramWritten["Cmd_CpsDest"]);
        Assert.Contains("via FLL", scan.ProgramWritten["Sim_Fill"]);
        Assert.Contains("via TONR", scan.ProgramWritten["Cmd_Timer"]);
        Assert.Contains("via RES", scan.ProgramWritten["Cmd_Counter"]);
        Assert.Contains("via CTUD", scan.ProgramWritten["Cmd_UpDown"]);
        Assert.Contains("via MOV", scan.ProgramWritten["Cmd_MovDest"]);
        Assert.Contains("(ST line 7 via COP)", scan.ProgramWritten["Cmd_Multi"]);

        // Sources and pure-function arguments are reads.
        Assert.DoesNotContain("Cmd_Src", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_Pure", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_Pure", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_CopDest").Reason);
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Src");
    }

    [Fact]
    public void EncodedRoutine_UnknownWriteContext()
    {
        // Real shape: <EncodedData> replaces the <Routine> element (fixtures/real-shape/EncodedRoutine.L5X).
        const string encoded = """<EncodedData EncodedType="Routine" Name="Locked" EncryptionConfig="1" />""";
        var project = BuildCustom(
            ["Cmd_A", "Sim_B", "Plain_C"], "",
            StLines("Plain_C := 1;"), encoded);

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains("Main/Locked", scan.UnknownContext["Cmd_A"]);
        Assert.Contains("Main/Locked", scan.UnknownContext["Sim_B"]);
        Assert.DoesNotContain("Plain_C", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var deny = policy.Denies.Single(d => d.Tag == "Cmd_A");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains("Main/Locked", deny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Sim_B");
    }

    [Fact]
    public void RoutineWithoutContentElement_UnknownWriteContext()
    {
        var project = BuildCustom(
            ["Cmd_A"], "",
            """<Routine Name="Empty" Type="ST"></Routine>""", StLines("X := 1;"));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains("Main/Empty", scan.UnknownContext["Cmd_A"]);
    }

    [Fact]
    public void AoiCall_Argument_ProgramWrittenOrUnknown()
    {
        var project = BuildCustom(
            ["Cmd_AoiIn", "Cmd_AoiOut", "Cmd_Mystery", "Cmd_Clean"], AoiDefinition,
            StLines(
                "Aoi_Drive(Inst, Cmd_AoiIn, Cmd_AoiOut);",
                "Mystery_Fn(Cmd_Mystery);"));

        var scan = WriteClassifier.ScanRungs(project);

        // Project AOI: the output parameter is a proven write, the input is a read.
        Assert.Contains("via AOI_DRIVE", scan.ProgramWritten["Cmd_AoiOut"]);
        Assert.DoesNotContain("Cmd_AoiIn", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_AoiIn", scan.UnknownContext.Keys);

        // Unknown function: its argument is unknown write context, never a grant.
        Assert.Contains("MYSTERY_FN", scan.UnknownContext["Cmd_Mystery"]);
        Assert.DoesNotContain("Cmd_Mystery", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_AoiOut").Reason);
        Assert.Equal(DenyReason.UnknownWriteContext, policy.Denies.Single(d => d.Tag == "Cmd_Mystery").Reason);
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_AoiIn");
    }

    /// <summary>
    /// String instructions write a destination; operand indexes per Logix 5000 General
    /// Instructions reference (String instructions).
    /// </summary>
    [Theory]
    [InlineData("FIND",   "FIND(Cmd_Src, Cmd_Pat, 1, Cmd_Dest);")]
    [InlineData("MID",    "MID(Cmd_Src, 2, 1, Cmd_Dest);")]
    [InlineData("UPPER",  "UPPER(Cmd_Src, Cmd_Dest);")]
    [InlineData("LOWER",  "LOWER(Cmd_Src, Cmd_Dest);")]
    [InlineData("CONCAT", "CONCAT(Cmd_Src, Cmd_Pat, Cmd_Dest);")]
    public void StStringInstruction_DestIsProgramWritten(string instruction, string call)
    {
        var project = BuildCustom(["Cmd_Src", "Cmd_Pat", "Cmd_Dest"], "", StLines(call));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains($"via {instruction}", scan.ProgramWritten["Cmd_Dest"]);
        Assert.DoesNotContain("Cmd_Src", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_Src", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_Dest").Reason);
    }

    /// <summary>
    /// Functions that are neither documented value-returning ST functions nor in the operand
    /// table are unknown: their arguments deny as UnknownWriteContext.
    /// </summary>
    [Theory]
    [InlineData("LEN")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("SEL")]
    [InlineData("MUX")]
    [InlineData("Mystery_Fn")]
    public void UnlistedStFunction_ArgumentsUnknown(string function)
    {
        Assert.Equal("unknown", WriteClassifier.ClassifyKind(function, 0));

        var project = BuildCustom(["Cmd_Arg"], "", StLines($"{function}(Cmd_Arg);"));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains(function.ToUpperInvariant(), scan.UnknownContext["Cmd_Arg"]);
        Assert.DoesNotContain("Cmd_Arg", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.UnknownWriteContext, policy.Denies.Single(d => d.Tag == "Cmd_Arg").Reason);
    }

    /// <summary>
    /// Whitespace, comments and line breaks around '.', '[' and ']' in an ST target do not
    /// hide the write ('|' in the data separates source lines).
    /// </summary>
    [Theory]
    [InlineData("Cmd_Arr",   "Cmd_Arr [i] := 1;")]
    [InlineData("Cmd_Arr",   "Cmd_Arr [ i ] := 1;")]
    [InlineData("Cmd_Motor", "Cmd_Motor .Speed := 1;")]
    [InlineData("Cmd_Motor", "Cmd_Motor. Speed := 1;")]
    [InlineData("Cmd_Motor", "Cmd_Motor (* c *) .Speed := 1;")]
    [InlineData("Cmd_Motor", "Cmd_Motor /* c */.Speed := 1;")]
    [InlineData("Cmd_Motor", "Cmd_Motor // c|.Speed := 1;")]
    [InlineData("Cmd_Motor", "Cmd_Motor|.Speed := 1;")]
    [InlineData("Cmd_Arr",   "Cmd_Arr|[i] := 1;")]
    [InlineData("Cmd_Arr",   "Cmd_Arr (* c *) [i] [:=] 1;")]
    [InlineData("Cmd_Arr",   "Cmd_Arr [i] .Speed [ 2 ] := 1;")]
    public void WhitespaceInTarget_IsProgramWritten(string tag, string source)
    {
        var lines = ("IF Cmd_Read THEN|" + source + "|END_IF;").Split('|');
        var project = BuildCustom([tag, "Cmd_Read", "i"], "", StLines(lines));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains(tag, scan.ProgramWritten.Keys);
        Assert.DoesNotContain(tag, scan.UnknownContext.Keys);
        Assert.DoesNotContain("Cmd_Read", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_Read", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == tag).Reason);
    }

    /// <summary>
    /// A target that is not a valid Logix tag path is never dropped: every identifier on the
    /// left-hand side becomes unknown write context.
    /// </summary>
    [Theory]
    [InlineData("Cmd_Arr[] := 1;")]
    [InlineData("Cmd_Arr[Cmd_Idx+#] := 1;")]
    [InlineData("Cmd_Arr[Cmd_Idx]. := 1;")]
    [InlineData("Cmd_Arr.[Cmd_Idx] := 1;")]
    public void UnparseableTarget_UnknownWriteContext(string source)
    {
        var project = BuildCustom(["Cmd_Arr", "Cmd_Idx"], "", StLines(source));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains("assignment target", scan.UnknownContext["Cmd_Arr"]);
        Assert.DoesNotContain("Cmd_Arr", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.UnknownWriteContext, policy.Denies.Single(d => d.Tag == "Cmd_Arr").Reason);
    }
}
