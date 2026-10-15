namespace LogixMcp.Tests;

/// <summary>
/// A real Logix export leaves out <c>Usage</c> for ordinary local program
/// tags, so a tag is reachable from another program only when its Usage is Public, Input or Output. The XML here
/// has the shape of the real export (synth0161-r4.L5X): <c>XIC(\Line_A.Pump_01_Cmd)</c> in Line_C, where
/// <c>Pump_01_Cmd</c> has no Usage attribute.
/// </summary>
public class RealExportNonPublicTests
{
    private static string ProgramXml(string tagUsageAttribute) => $$"""
        <Program Name="Line_A" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
          <Tags>
            <Tag Name="Pump_01_Cmd" TagType="Base" DataType="BOOL" Radix="Decimal" Constant="false"{{tagUsageAttribute}} ExternalAccess="Read/Write">
              <Data Format="L5K"><![CDATA[0]]></Data>
              <Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0"/></Data>
            </Tag>
          </Tags>
          <Routines>
            <Routine Name="MainRoutine" Type="RLL"><RLLContent>
              <Rung Number="0" Type="N"><Text><![CDATA[XIC(Pump_01_Cmd)OTE(Pump_01_Cmd);]]></Text></Rung>
            </RLLContent></Routine>
          </Routines>
        </Program>
        """;

    private static string ExportXml(string tagUsageAttribute, string lineCRung = "XIC(\\Line_A.Pump_01_Cmd)OTE(Run);") => $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="Synth" TargetType="Controller" ContainsContext="false">
          <Controller Name="Synth" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes/><Modules/><AddOnInstructionDefinitions/>
            <Tags/>
            <Programs>
              {{ProgramXml(tagUsageAttribute)}}
              <Program Name="Line_C" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
                <Tags>
                  <Tag Name="Run" TagType="Base" DataType="BOOL" Radix="Decimal" Constant="false" ExternalAccess="Read/Write">
                    <Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0"/></Data>
                  </Tag>
                </Tags>
                <Routines>
                  <Routine Name="MainRoutine" Type="RLL"><RLLContent>
                    <Rung Number="0" Type="N"><Text><![CDATA[{{lineCRung}}]]></Text></Rung>
                  </RLLContent></Routine>
                </Routines>
              </Program>
            </Programs>
            <Tasks><Task Name="MainTask" Type="CONTINUOUS" Priority="10" Watchdog="500" DisableUpdateOutputs="false" InhibitTask="false">
              <ScheduledPrograms><ScheduledProgram Name="Line_A"/><ScheduledProgram Name="Line_C"/></ScheduledPrograms>
            </Task></Tasks>
          </Controller>
        </RSLogix5000Content>
        """;

    private static LintResult Lint(string exportXml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"logix-mcp-nonpublic-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, exportXml);
        try
        {
            var svc = new ProjectService();
            svc.Load(path);
            return Assert.IsType<LintResult>(LintHandler.Execute(svc));
        }
        finally { File.Delete(path); }
    }

    private static ReferenceCheckResult PreCheck(string exportXml, string rung)
    {
        var programTags = RungReferenceChecker.ProgramTagsFromExport(exportXml);
        var symbols = new ReferenceSymbols(
            "Line_C",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["Run"], StringComparer.OrdinalIgnoreCase),
            programTags,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            RungReferenceChecker.LocalProgramTagsFromExport(exportXml));
        return RungReferenceChecker.Check([rung], symbols);
    }

    [Fact]
    public void RealExportProgramTagWithNoUsage_IsCrossProgramTargetNotPublic_InLint()
    {
        var result = Lint(ExportXml(tagUsageAttribute: ""));

        var f = Assert.Single(result.Findings, x => x.Rule == "CrossProgramTargetNotPublic");
        Assert.Equal("Line_C", f.Program);
        Assert.Contains("program: Line_A", f.Message);
        Assert.Contains("tag: Pump_01_Cmd", f.Message);
    }

    [Fact]
    public void RealExportProgramTagWithNoUsage_IsTagNotPublic_InPreCheck()
    {
        var result = PreCheck(ExportXml(""), "XIC(\\Line_A.Pump_01_Cmd)OTE(Run);");

        Assert.Equal(ReferenceCheckKinds.TagNotPublic, result.Kind);
        Assert.Contains("\\Line_A.Pump_01_Cmd", result.Message);
    }

    [Theory]
    [InlineData(" Usage=\"Public\"")]
    [InlineData(" Usage=\"Input\"")]
    [InlineData(" Usage=\"Output\"")]
    public void UsagePublicInputOutput_AreAllowed_InLintAndPreCheck(string usage)
    {
        var xml = ExportXml(usage);

        Assert.DoesNotContain(Lint(xml).Findings, x => x.Rule.StartsWith("CrossProgramTarget"));
        Assert.False(PreCheck(xml, "XIC(\\Line_A.Pump_01_Cmd)OTE(Run);").Refused);
    }

    [Theory]
    [InlineData(" Usage=\"Local\"")]
    [InlineData(" Usage=\"InOut\"")]
    public void UsageLocalAndInOut_AreNotReachable_InLintAndPreCheck(string usage)
    {
        var xml = ExportXml(usage);

        Assert.Contains(Lint(xml).Findings, x => x.Rule == "CrossProgramTargetNotPublic");
        Assert.Equal(ReferenceCheckKinds.TagNotPublic, PreCheck(xml, "XIC(\\Line_A.Pump_01_Cmd)OTE(Run);").Kind);
    }

    [Fact]
    public void UnresolvableProgram_IsAWarning_NotARefusal()
    {
        var result = PreCheck(ExportXml(""), "XIC(\\Line_Z.Pump_01_Cmd)OTE(Run);");

        Assert.False(result.Refused);
        Assert.Contains(result.Warnings, w => w.Contains("Line_Z.Pump_01_Cmd"));
    }

    [Fact]
    public void UsageUnknownBecauseTheExportCannotBeRead_IsAWarningOrPass_NotATagNotPublicRefusal()
    {
        var xml = ExportXml("");
        var symbols = new ReferenceSymbols(
            "Line_C", new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["Run"], StringComparer.OrdinalIgnoreCase),
            RungReferenceChecker.ProgramTagsFromExport(xml),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            LocalProgramTags: null);

        var result = RungReferenceChecker.Check(["XIC(\\Line_A.Pump_01_Cmd)OTE(Run);"], symbols);

        Assert.NotEqual(ReferenceCheckKinds.TagNotPublic, result.Kind);
    }

    [Fact]
    public void Lint_UnresolvableProgramOrTag_IsMissing_NotNotPublic()
    {
        var result = Lint(ExportXml("", "XIC(\\Line_Z.Pump_01_Cmd)OTE(Run);"));

        Assert.Contains(result.Findings, x => x.Rule == "CrossProgramTargetMissing");
        Assert.DoesNotContain(result.Findings, x => x.Rule == "CrossProgramTargetNotPublic");
    }

    // ── G29: lint refuses a write to another program's Output parameter ──

    private const string OutputUsage = " Usage=\"Output\"";

    [Theory]
    [InlineData("OTE")]
    [InlineData("OTL")]
    [InlineData("OTU")]
    public void Lint_WriteToAnotherProgramsOutput_IsCrossProgramOutputNotWritable(string instruction)
    {
        var result = Lint(ExportXml(OutputUsage, $"XIC(Run){instruction}(\\Line_A.Pump_01_Cmd);"));

        var f = Assert.Single(result.Findings, x => x.Rule == "CrossProgramOutputNotWritable");
        Assert.Equal("error", f.Severity);
        Assert.Equal("Line_C", f.Program);
        Assert.Equal(0, f.Rung);
        Assert.Contains("Rung 0", f.Message);
        Assert.Contains(@"\Line_A.Pump_01_Cmd", f.Message);
        Assert.Contains("RxCMP_E_DIRECT_WRITE_OUTPUT", f.Message);
        Assert.DoesNotContain(result.Findings, x => x.Rule == "CrossProgramTargetNotPublic");
    }

    [Fact]
    public void Lint_ReadOfAnotherProgramsOutput_IsAllowed()
    {
        var result = Lint(ExportXml(OutputUsage, "XIC(\\Line_A.Pump_01_Cmd)OTE(Run);"));

        Assert.DoesNotContain(result.Findings, x => x.Rule == "CrossProgramOutputNotWritable");
    }

    [Fact]
    public void Lint_OwnProgramWriteOfItsOutput_IsAllowed()
    {
        // Line_A's own MainRoutine already does OTE(Pump_01_Cmd) on its Output tag.
        var result = Lint(ExportXml(OutputUsage));

        Assert.DoesNotContain(result.Findings, x => x.Rule == "CrossProgramOutputNotWritable");
    }

    [Fact]
    public void Lint_WriteToAnotherProgramsPublicTag_IsNotCrossProgramOutputNotWritable()
    {
        var result = Lint(ExportXml(" Usage=\"Public\"", "OTE(\\Line_A.Pump_01_Cmd);"));

        Assert.DoesNotContain(result.Findings, x => x.Rule == "CrossProgramOutputNotWritable");
    }
}
