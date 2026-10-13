using L5Sharp.Core;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// <see cref="InstructionMnemonics"/> is the one catalogue of the 16 mnemonics Logix
/// Designer v36 renamed, and every analyzer gives the same result for the pre-v36 and the v36+ spelling of a rung.
/// </summary>
public class InstructionMnemonicsTests : IDisposable
{
    private readonly string _tempDir;

    public InstructionMnemonicsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mnemonics-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Every (legacy, iec) pair, for theories.</summary>
    public static TheoryData<string, string> AllPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var p in InstructionMnemonics.InstructionPairs) data.Add(p.Legacy, p.Iec);
        return data;
    }

    // ── Catalogue ────────────────────────────────────────────────────────────

    [Fact]
    public void Catalogue_HasSixteenInstructionPairsAndSevenExpressionFunctionPairs()
    {
        Assert.Equal(16, InstructionMnemonics.InstructionPairs.Count);
        Assert.Equal(16, InstructionMnemonics.InstructionPairs.Select(p => p.Legacy).Distinct().Count());
        Assert.Equal(16, InstructionMnemonics.InstructionPairs.Select(p => p.Iec).Distinct().Count());

        Assert.Equal(7, InstructionMnemonics.ExpressionFunctionPairs.Count);
        Assert.Equal(
            new[] { "SQR/SQRT", "TRN/TRUNC", "ACS/ACOS", "ASN/ASIN", "ATN/ATAN", "FRD/BCD_TO", "TOD/TO_BCD" }.Order(),
            InstructionMnemonics.ExpressionFunctionPairs.Select(p => $"{p.Legacy}/{p.Iec}").Order());
        Assert.All(InstructionMnemonics.ExpressionFunctionPairs,
            p => Assert.Contains(p, InstructionMnemonics.InstructionPairs));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Canonical_MapsBothSpellingsToOneKey(string legacy, string iec)
    {
        Assert.Equal(InstructionMnemonics.Canonical(legacy), InstructionMnemonics.Canonical(iec));
        Assert.Equal(InstructionMnemonics.Canonical(legacy.ToLowerInvariant()), InstructionMnemonics.Canonical(iec));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void For_ReturnsTheSpellingOfEachSet(string legacy, string iec)
    {
        var key = InstructionMnemonics.Canonical(legacy);
        Assert.Equal(legacy, InstructionMnemonics.For(MnemonicSet.Legacy, key));
        Assert.Equal(iec, InstructionMnemonics.For(MnemonicSet.Iec, key));
        Assert.Equal(legacy, InstructionMnemonics.For(MnemonicSet.Legacy, iec));
        Assert.Equal(iec, InstructionMnemonics.For(MnemonicSet.Iec, legacy));
        Assert.Equal(MnemonicSet.Legacy, InstructionMnemonics.SetOf(legacy));
        Assert.Equal(MnemonicSet.Iec, InstructionMnemonics.SetOf(iec));
    }

    [Theory]
    [InlineData("XIC")]
    [InlineData("OTE")]
    [InlineData("ADD")]
    [InlineData("TON")]
    [InlineData("MyAoi")]
    public void UnrenamedMnemonics_AreTheirOwnKeyInBothSets(string name)
    {
        Assert.Equal(name.ToUpperInvariant(), InstructionMnemonics.Canonical(name));
        Assert.Equal(name.ToUpperInvariant(), InstructionMnemonics.For(MnemonicSet.Legacy, name));
        Assert.Equal(name.ToUpperInvariant(), InstructionMnemonics.For(MnemonicSet.Iec, name));
        Assert.False(InstructionMnemonics.IsRenamed(name));
        Assert.Null(InstructionMnemonics.SetOf(name));
    }

    // ── WriteClassifier ──────────────────────────────────────────────────────

    /// <summary>OP(Source, Dest) pairs: the destination is operand 1 in both spellings.</summary>
    [Theory]
    [InlineData("SQR", "SQRT")]
    [InlineData("TRN", "TRUNC")]
    [InlineData("ASN", "ASIN")]
    [InlineData("ACS", "ACOS")]
    [InlineData("ATN", "ATAN")]
    [InlineData("TOD", "TO_BCD")]
    [InlineData("FRD", "BCD_TO")]
    [InlineData("MOV", "MOVE")]
    public void WriteClassifier_TwoOperandPairs_DestinationIsOperandOne(string legacy, string iec)
    {
        foreach (var name in new[] { legacy, iec })
        {
            Assert.Equal("read", WriteClassifier.ClassifyKind(name, 0));
            Assert.Equal("write", WriteClassifier.ClassifyKind(name, 1));
        }
    }

    [Fact]
    public void WriteClassifier_Expt_DestinationIsOperandTwo()
    {
        foreach (var name in new[] { "XPY", "EXPT" })
        {
            Assert.Equal("read", WriteClassifier.ClassifyKind(name, 0));
            Assert.Equal("read", WriteClassifier.ClassifyKind(name, 1));
            Assert.Equal("write", WriteClassifier.ClassifyKind(name, 2));
        }
    }

    /// <summary>LIMIT(Low,Test,High) and the comparisons write nothing, in either spelling.</summary>
    [Theory]
    [InlineData("LIM", "LIMIT", 3)]
    [InlineData("EQU", "EQ", 2)]
    [InlineData("NEQ", "NE", 2)]
    [InlineData("GRT", "GT", 2)]
    [InlineData("GEQ", "GE", 2)]
    [InlineData("LES", "LT", 2)]
    [InlineData("LEQ", "LE", 2)]
    public void WriteClassifier_Comparisons_AreReadOnlyInBothSpellings(string legacy, string iec, int argCount)
    {
        foreach (var name in new[] { legacy, iec })
        {
            Assert.True(WriteClassifier.IsReadOnlyInstruction(name));
            for (var i = 0; i < argCount; i++)
                Assert.Equal("read", WriteClassifier.ClassifyKind(name, i));
        }
    }

    [Theory]
    [InlineData("LIMIT(Lo,Val,Hi)", "")]
    [InlineData("SQRT(Src,Cmd_Out)", "Cmd_Out")]
    [InlineData("TRUNC(Src,Cmd_Out)", "Cmd_Out")]
    [InlineData("EXPT(A,B,Cmd_Out)", "Cmd_Out")]
    [InlineData("TO_BCD(Src,Cmd_Out)", "Cmd_Out")]
    [InlineData("BCD_TO(Src,Cmd_Out)", "Cmd_Out")]
    [InlineData("ASIN(Src,Cmd_Out)", "Cmd_Out")]
    [InlineData("ACOS(Src,Cmd_Out)", "Cmd_Out")]
    [InlineData("ATAN(Src,Cmd_Out)", "Cmd_Out")]
    public void WriteClassifier_NewSpellings_ClassifyDestinationsInRungText(string instruction, string written)
    {
        var scan = WriteClassifier.ScanFragment(
            $"""<Rung Number="0" Type="N"><Text>XIC(Run){instruction};</Text></Rung>""");

        Assert.Equal(written.Length == 0 ? [] : new[] { written }, scan.ProgramWritten.Keys.ToArray());
        Assert.Empty(scan.UnknownContext);
    }

    /// <summary>The same rung in both spellings has the identical write scan.</summary>
    [Theory]
    [MemberData(nameof(AllPairs))]
    public void WriteClassifier_SameRungInBothSpellings_GivesIdenticalScan(string legacy, string iec)
    {
        // Three operands cover the widest signature (LIM/XPY/ADD-style); extra operands are all reads or the dest.
        var oldScan = WriteClassifier.ScanFragment(
            $"""<Rung Number="0" Type="N"><Text>XIC(Run){legacy}(A,B,Cmd_C);</Text></Rung>""");
        var newScan = WriteClassifier.ScanFragment(
            $"""<Rung Number="0" Type="N"><Text>XIC(Run){iec}(A,B,Cmd_C);</Text></Rung>""");

        Assert.Equal(oldScan.ProgramWritten.Keys.Order(), newScan.ProgramWritten.Keys.Order());
        Assert.Equal(oldScan.UnknownContext.Keys.Order(), newScan.UnknownContext.Keys.Order());
    }

    /// <summary>CPT(x,SQRT(y)): x is the destination, y is only read, with the same result as SQR(y).</summary>
    [Theory]
    [InlineData("SQRT")]
    [InlineData("SQR")]
    public void WriteClassifier_CptWithSqrtExpression_WritesOnlyTheDestination(string function)
    {
        var scan = WriteClassifier.ScanFragment(
            $"""<Rung Number="0" Type="N"><Text>XIC(Run)CPT(x,{function}(y));</Text></Rung>""");

        Assert.Equal(new[] { "x" }, scan.ProgramWritten.Keys.ToArray());
        Assert.False(scan.ProgramWritten.ContainsKey("y"));
        Assert.Empty(scan.UnknownContext);
    }

    // ── ArrayIndexUnchecked ──────────────────────────────────────────────────

    private ProjectService LoadRungs(params string[] rungs)
    {
        var rungXml = string.Concat(rungs.Select((r, i) =>
            new XElement("Rung", new XAttribute("Number", i), new XAttribute("Type", "N"),
                new XElement("Text", new XCData(r))).ToString()));
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, $"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes /><Modules /><AddOnInstructionDefinitions />
                <Tags>
                  <Tag Name="Idx" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>
                  <Tag Name="Out" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>
                  <Tag Name="Arr" TagType="Base" DataType="DINT" Dimensions="10" Radix="Decimal" ExternalAccess="Read/Write" Constant="false"><Data Format="Decorated"><Array DataType="DINT" Dimensions="10" Radix="Decimal">{string.Concat(Enumerable.Range(0, 10).Select(i => $"<Element Index=\"[{i}]\" Value=\"0\" />"))}</Array></Data></Tag>
                </Tags>
                <Programs>
                  <Program Name="P" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                    <Tags />
                    <Routines><Routine Name="R" Type="RLL"><RLLContent>{rungXml}</RLLContent></Routine></Routines>
                  </Program>
                </Programs>
                <Tasks /><ParameterConnections />
              </Controller>
            </RSLogix5000Content>
            """);
        var svc = new ProjectService();
        svc.Load(path);
        return svc;
    }

    private List<LintFinding> ArrayFindings(params string[] rungs)
    {
        var result = Assert.IsType<LintResult>(
            LintHandler.Execute(LoadRungs(rungs), rules: [new ArrayIndexUncheckedRule()]));
        return result.Findings.Where(f => f.Rule == "ArrayIndexUnchecked").ToList();
    }

    /// <summary>An LT(i,size) guard on an earlier rung means no finding, exactly as LES(i,size) does.</summary>
    [Theory]
    [InlineData("LT(Idx,10)")]
    [InlineData("LES(Idx,10)")]
    [InlineData("LIMIT(0,Idx,9)")]
    [InlineData("LIM(0,Idx,9)")]
    [InlineData("GE(Idx,0)")]
    [InlineData("GEQ(Idx,0)")]
    [InlineData("GT(Idx,-1)")]
    [InlineData("GRT(Idx,-1)")]
    public void ArrayIndexUnchecked_GuardInEitherSpelling_NoFinding(string guard)
    {
        Assert.Empty(ArrayFindings($"XIC(Run){guard}OTE(Ok);", "MOVE(Arr[Idx],Out);"));
        Assert.Empty(ArrayFindings($"XIC(Run){guard}OTE(Ok);", "MOV(Arr[Idx],Out);"));
        Assert.Empty(ArrayFindings($"XIC(Run){guard}MOVE(Arr[Idx],Out);"));
    }

    /// <summary>Without a guard the finding is the same for the old and the new spelling of the move.</summary>
    [Fact]
    public void ArrayIndexUnchecked_NoGuard_SameFindingForOldAndNewSpelling()
    {
        var oldFindings = ArrayFindings("MOV(Arr[Idx],Out);");
        var newFindings = ArrayFindings("MOVE(Arr[Idx],Out);");

        var o = Assert.Single(oldFindings);
        var n = Assert.Single(newFindings);
        Assert.Equal(o.Rung, n.Rung);
        Assert.Equal(o.Severity, n.Severity);
        Assert.Equal(o.Message.Replace("MOV ", "MOVE "), n.Message);
        Assert.Contains("Arr[Idx]", n.Message);
    }

    /// <summary>A non-comparison (EQ) is not a range guard in either spelling.</summary>
    [Theory]
    [InlineData("EQ(Idx,10)")]
    [InlineData("EQU(Idx,10)")]
    public void ArrayIndexUnchecked_EqualityIsNotARangeGuard(string compare) =>
        Assert.Single(ArrayFindings($"XIC(Run){compare}OTE(Ok);", "MOVE(Arr[Idx],Out);"));

    // ── RungDiagram ──────────────────────────────────────────────────────────

    private static XElement OneRung(string neutralText) =>
        new("RSLogix5000Content",
            new XElement("Controller", new XAttribute("Name", "C"),
                new XElement("Tags"),
                new XElement("Programs",
                    new XElement("Program", new XAttribute("Name", "P"),
                        new XElement("Tags"),
                        new XElement("Routines",
                            new XElement("Routine", new XAttribute("Name", "R"), new XAttribute("Type", "RLL"),
                                new XElement("RLLContent",
                                    new XElement("Rung", new XAttribute("Number", 0), new XAttribute("Type", "N"),
                                        new XElement("Text", new XCData(neutralText))))))))));

    private static string Diagram(string neutralText)
    {
        var result = Assert.IsType<RungDiagramResult>(
            RungDiagramHandler.Execute(OneRung(neutralText), "test", "P", "R", "0", "text"));
        return result.Text!;
    }

    /// <summary>Drops box-drawing and whitespace and masks the mnemonic, leaving the labels and operands.</summary>
    private static string Shape(string diagram, string mnemonic) =>
        Regex.Replace(Regex.Replace(diagram, @"[\s║│┌┐└┘─┤├╟╢═╔╗╚╝╠╣╤╧╪╫╬┬┴┼]", ""),
            Regex.Escape(mnemonic), "#");

    [Theory]
    [InlineData("MOV", "MOVE", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("SQR", "SQRT", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("LES", "LT", "{0}(Src,5)OTE(Dst);", "Source A,Source B")]
    [InlineData("GRT", "GT", "{0}(Src,5)OTE(Dst);", "Source A,Source B")]
    [InlineData("GEQ", "GE", "{0}(Src,5)OTE(Dst);", "Source A,Source B")]
    [InlineData("LEQ", "LE", "{0}(Src,5)OTE(Dst);", "Source A,Source B")]
    [InlineData("EQU", "EQ", "{0}(Src,5)OTE(Dst);", "Source A,Source B")]
    [InlineData("NEQ", "NE", "{0}(Src,5)OTE(Dst);", "Source A,Source B")]
    [InlineData("LIM", "LIMIT", "{0}(0,Src,9)OTE(Dst);", "Low Limit,Test,High Limit")]
    [InlineData("XPY", "EXPT", "XIC(Run){0}(A,B,Dst);", "Source A,Source B,Dest")]
    [InlineData("TRN", "TRUNC", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("TOD", "TO_BCD", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("FRD", "BCD_TO", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("ASN", "ASIN", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("ACS", "ACOS", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    [InlineData("ATN", "ATAN", "XIC(Run){0}(Src,Dst);", "Source,Dest")]
    public void RungDiagram_OldAndNewSpelling_DrawTheSameWithOperandLabels(
        string legacy, string iec, string template, string labels)
    {
        var oldDiagram = Diagram(string.Format(template, legacy));
        var newDiagram = Diagram(string.Format(template, iec));

        Assert.Equal(Shape(oldDiagram, legacy), Shape(newDiagram, iec));
        foreach (var label in labels.Split(','))
            Assert.Contains(label, newDiagram);
        Assert.Contains(iec, newDiagram);
    }

    /// <summary>CPT(x,SQRT(y)) draws like CPT(x,SQR(y)): destination and expression labels, expression intact.</summary>
    [Fact]
    public void RungDiagram_CptWithSqrtExpression_DrawsLikeSqr()
    {
        var newDiagram = Diagram("XIC(Run)CPT(x,SQRT(y));");
        var oldDiagram = Diagram("XIC(Run)CPT(x,SQR(y));");

        Assert.Contains("Dest", newDiagram);
        Assert.Contains("Expression", newDiagram);
        Assert.Equal(Shape(oldDiagram, "SQR"), Shape(newDiagram, "SQRT"));
    }

    // ── TraceOrigin ──────────────────────────────────────────────────────────

    private static L5X TraceProject(params string[] rungs) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules /><AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="Dest" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>
              <Tag Name="Level" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>
              <Tag Name="Src" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>
            </Tags>
            <Programs>
              <Program Name="P" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                <Tags />
                <Routines><Routine Name="R" Type="RLL"><RLLContent>{string.Concat(rungs.Select((r, i) => $"""<Rung Number="{i}" Type="N"><Text><![CDATA[{r}]]></Text></Rung>"""))}</RLLContent></Routine></Routines>
              </Program>
            </Programs>
            <Tasks /><ParameterConnections />
          </Controller>
        </RSLogix5000Content>
        """);

    private static TraceNode TraceWrite(string tag, string rung)
    {
        var graph = TraceOriginHandler.BuildGraph(TraceProject(rung), tag, null, null, out var error);
        Assert.True(graph is not null, error?.Error);
        return Assert.Single(graph!.WriteNodes);
    }

    /// <summary>MOV(5,Dest) and MOVE(5,Dest) are both literal writes, under a LES/LT gate.</summary>
    [Fact]
    public void TraceOrigin_MoveLiteralUnderComparison_SameResultForOldAndNewSpelling()
    {
        var oldNode = TraceWrite("Dest", "GRT(Level,5)MOV(5,Dest);");
        var newNode = TraceWrite("Dest", "GT(Level,5)MOVE(5,Dest);");

        Assert.Equal("5", oldNode.Value);
        Assert.Equal(oldNode.Value, newNode.Value);
        Assert.Equal("literal", newNode.ValueKind);
        Assert.Equal(oldNode.ValueKind, newNode.ValueKind);
        Assert.Equal(oldNode.Location, newNode.Location);
        Assert.Equal(oldNode.Kind, newNode.Kind);
        // GT(...) / GRT(...) is a gating comparison either way.
        var oldGate = Assert.Single(oldNode.Conditions!);
        var newGate = Assert.Single(newNode.Conditions!);
        Assert.Equal("GRT(Level,5)", oldGate.Text);
        Assert.Equal("GT(Level,5)", newGate.Text);
        Assert.Equal(oldGate.Tags, newGate.Tags);
        Assert.Equal("MOV", oldNode.Instruction);
        Assert.Equal("MOVE", newNode.Instruction);
    }

    /// <summary>SQR/SQRT write their operand 1 and are not reported as unclassified, in either spelling.</summary>
    [Theory]
    [InlineData("SQR")]
    [InlineData("SQRT")]
    [InlineData("TRN")]
    [InlineData("TRUNC")]
    [InlineData("TOD")]
    [InlineData("TO_BCD")]
    public void TraceOrigin_SingleSourceMath_WritesDestinationAndIsClassified(string instruction)
    {
        var graph = TraceOriginHandler.BuildGraph(
            TraceProject($"XIC(Level){instruction}(Src,Dest);"), "Dest", null, null, out var error);

        Assert.True(graph is not null, error?.Error);
        var node = Assert.Single(graph!.WriteNodes);
        Assert.Equal(instruction, node.Instruction);
        Assert.DoesNotContain(graph.Gaps, g => g.Kind == "UnclassifiedInstruction");
    }
}
