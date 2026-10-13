using System.Text.Json;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Cross-program (<c>\Program.Tag</c>) references in <c>logix_cross_reference</c>, <c>logix_find_tags</c>
/// and <see cref="WriteClassifier"/>, against <c>fixtures/DemoCrossProgram.L5X</c>:
/// P1 rung 0 <c>MOVE(\P2.Src,Dst)</c>, rung 1 <c>XIC(\P2.Bit)OTE(Local)</c>; P2 rung 1 <c>MOVE(Src,\P1.Dst)</c>.
/// </summary>
public class CrossProgramReferenceTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossProgram.L5X");

    private static ProjectService Load()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        return svc;
    }

    private static JsonElement Json(object result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result, result.GetType())).RootElement;

    [Theory]
    [InlineData("P2.Src")]
    [InlineData(@"\P2.Src")]
    [InlineData("Program:P2.Src")]
    public void CrossReference_ProgramQualifiedTag_IncludesTheOtherProgramsRung_MarkedCrossProgram(string name)
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), name));

        var cross = Assert.Single(result.References, r => r.CrossProgram);
        Assert.Equal("P1", cross.Program);
        Assert.Equal("Main", cross.Routine);
        Assert.Equal(0, cross.RungNumber);
        Assert.Equal("MOVE", cross.Instruction);
        Assert.Equal("read", cross.Kind);
        Assert.Equal("P2.Src", cross.Target);

        // P2's own, unqualified use of Src (the source of its MOVE on rung 1) is a plain reference.
        var local = Assert.Single(result.References, r => !r.CrossProgram);
        Assert.Equal("P2", local.Program);
        Assert.Equal(1, local.RungNumber);
    }

    private static ProjectService LoadTargets()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoLintCrossProgram.L5X"));
        return svc;
    }

    [Fact]
    public void Lint_MissingProgramTag_ReportsCrossProgramTargetMissing_WithProgramAndTag()
    {
        var result = Assert.IsType<LintResult>(LintHandler.Execute(LoadTargets()));
        var missing = result.Findings.Where(f => f.Rule == "CrossProgramTargetMissing").OrderBy(f => f.Rung).ToList();

        Assert.Equal(2, missing.Count);
        Assert.Equal(0, missing[0].Rung);
        Assert.Contains("program: Missing", missing[0].Message);
        Assert.Contains("tag: Tag", missing[0].Message);
        Assert.Equal(1, missing[1].Rung);
        Assert.Contains("program: P2", missing[1].Message);
        Assert.Contains("tag: Nope", missing[1].Message);
        Assert.All(missing, f => Assert.Equal("error", f.Severity));
    }

    [Fact]
    public void Lint_NonPublicTag_ReportsCrossProgramTargetNotPublic()
    {
        var result = Assert.IsType<LintResult>(LintHandler.Execute(LoadTargets()));

        var f = Assert.Single(result.Findings, x => x.Rule == "CrossProgramTargetNotPublic");
        Assert.Equal("P1", f.Program);
        Assert.Equal(2, f.Rung);
        Assert.Contains("program: P2", f.Message);
        Assert.Contains("tag: Hidden", f.Message);
        // A resolvable, Public reference (rung 3) produces neither finding.
        Assert.DoesNotContain(result.Findings, x => x.Rung == 3 && x.Rule.StartsWith("CrossProgramTarget"));
    }

    [Fact]
    public void CrossReference_MissingTarget_IsMarkedTargetMissing()
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(LoadTargets(), @"\Missing.Tag"));

        var r = Assert.Single(result.References);
        Assert.True(r.TargetMissing);
        Assert.Equal("P1", r.Program);
        Assert.Equal(0, r.RungNumber);
        Assert.Equal("Missing.Tag", r.Target);

        var json = Json(result).GetProperty("references")[0];
        Assert.True(json.GetProperty("targetMissing").GetBoolean());
    }

    [Fact]
    public void CrossReference_ExistingTarget_IsNotMarkedTargetMissing()
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(LoadTargets(), @"\P2.Hidden"));

        var r = Assert.Single(result.References);
        Assert.False(r.TargetMissing);
        Assert.False(Json(result).GetProperty("references")[0].TryGetProperty("targetMissing", out _));
    }

    [Fact]
    public void CrossReference_UnqualifiedName_AlsoFindsTheCrossProgramRung()
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), "Bit"));

        var cross = Assert.Single(result.References, r => r.CrossProgram);
        Assert.Equal("P1", cross.Program);
        Assert.Equal(1, cross.RungNumber);
    }

    [Fact]
    public void CrossReference_CrossProgramWrite_IsKindWrite()
    {
        // P2 rung 1 writes P1's Dst through \P1.Dst.
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), @"\P1.Dst"));

        var cross = Assert.Single(result.References, r => r.CrossProgram);
        Assert.Equal("P2", cross.Program);
        Assert.Equal(1, cross.RungNumber);
        Assert.Equal("write", cross.Kind);
    }

    [Fact]
    public void CrossReference_SerializesCrossProgramTrue_AndOmitsItOtherwise()
    {
        var json = Json(CrossReferenceHandler.Execute(Load(), "P2.Src"));
        var refs = json.GetProperty("references").EnumerateArray().ToList();

        Assert.True(refs.Single(r => r.GetProperty("program").GetString() == "P1")
            .GetProperty("crossProgram").GetBoolean());
        Assert.False(refs.Single(r => r.GetProperty("program").GetString() == "P2")
            .TryGetProperty("crossProgram", out _));
    }

    [Fact]
    public void FindTags_ProgramQualifiedPattern_ReportsTheOtherProgramsRung_MarkedCrossProgram()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load(), "P2.Src"));

        var tag = Assert.Single(result.Tags);
        Assert.Equal("Src", tag.Name);
        Assert.Equal("P2", tag.Scope);

        var cross = Assert.Single(result.CrossProgramReferences!);
        Assert.True(cross.CrossProgram);
        Assert.Equal("P1", cross.Program);
        Assert.Equal(0, cross.RungNumber);
        Assert.Equal("P2.Src", cross.Target);

        var json = Json(result);
        Assert.True(json.GetProperty("crossProgramReferences")[0].GetProperty("crossProgram").GetBoolean());
    }

    [Fact]
    public void FindTags_PlainPattern_ReportsCrossProgramReferencesToMatchedProgramTags()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load(), "Bit"));

        var cross = Assert.Single(result.CrossProgramReferences!);
        Assert.Equal("P1", cross.Program);
        Assert.Equal(1, cross.RungNumber);
    }

    [Fact]
    public void FindTags_WithoutPattern_DoesNotSearchRungs()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load()));

        Assert.Null(result.CrossProgramReferences);
        Assert.False(Json(result).TryGetProperty("crossProgramReferences", out _));
    }

    [Fact]
    public void WriteClassifier_CrossProgramDestination_IsAWriteToTheOtherProgramsTag()
    {
        var project = L5X.Load(FixturePath);
        var rung = project.Programs.Single(p => p.Name == "P2").Routines.Single().Rungs.Single(r => r.Number == 1);
        var move = Assert.Single(RungParser.TryParse(rung)!);

        var src = WriteClassifier.ClassifyOperand(move, 0, null);
        Assert.Equal("read", src.Kind);
        Assert.Null(src.Program);

        var dst = WriteClassifier.ClassifyOperand(move, 1, null);
        Assert.Equal("write", dst.Kind);
        Assert.Equal("P1", dst.Program);
        Assert.Equal("Dst", dst.Tag);
        Assert.True(dst.IsCrossProgramWrite);
    }

    [Fact]
    public void WriteClassifier_ScanRungs_RecordsCrossProgramWriteAgainstTheOtherProgramsTag()
    {
        var written = WriteClassifier.CollectProgramWrittenTags(L5X.Load(FixturePath));

        Assert.True(written.ContainsKey("Program:P1.Dst"));
        Assert.Contains("P2/Main[1]", written["Program:P1.Dst"]);
        // Not misread as a write to a tag named "P1".
        Assert.False(written.ContainsKey("P1"));
    }

    [Theory]
    [InlineData(@"OTE(\P2.Dst);", "OTE")]
    [InlineData(@"MOVE(Src,\P2.Dst);", "MOVE")]
    public void WriteClassifier_ScanFragment_CrossProgramDestination_IsWriteToThatProgramsTag(string rung, string via)
    {
        var xml = $"<Rungs><Rung Number=\"0\"><Text><![CDATA[{rung}]]></Text></Rung></Rungs>";

        var scan = WriteClassifier.ScanFragment(xml);

        Assert.Contains($"via {via}", scan.ProgramWritten["Program:P2.Dst"]);
        Assert.False(scan.ProgramWritten.ContainsKey("P2"));
    }
}
