using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="RungParser"/>, the single rung-parse helper, against
/// <c>fixtures/DemoCrossProgram.L5X</c> (two programs, IEC mnemonics, SoftwareRevision 36.x).
/// </summary>
public class RungParserTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossProgram.L5X");

    private static List<Rung> P1Rungs()
    {
        var project = L5X.Load(FixturePath);
        return project.Programs.Single(p => p.Name == "P1").Routines.Single().Rungs.ToList();
    }

    /// <summary>The fixture has two programs and is labelled 36.x.</summary>
    [Fact]
    public void Fixture_HasTwoPrograms_AndIsLabelled36x()
    {
        var project = L5X.Load(FixturePath);
        Assert.Equal(["P1", "P2"], project.Programs.Select(p => p.Name.ToString()).ToArray());
        var xml = File.ReadAllText(FixturePath);
        Assert.Contains("SoftwareRevision=\"36.", xml);
    }

    /// <summary><c>MOVE(\P2.Src,Dst)</c> parses without an <see cref="ArgumentException"/>.</summary>
    [Fact]
    public void TryParse_MoveWithCrossProgramSource_Parses()
    {
        var parsed = RungParser.TryParse(@"MOVE(\P2.Src,Dst);");

        var ins = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Instruction>>(parsed));
        Assert.Equal("MOVE", ins.Key.ToString());
        Assert.Equal(2, ins.Arguments.Count());
    }

    /// <summary><c>XIC(\P2.Bit)OTE(Local)</c> parses without an <see cref="ArgumentException"/>.</summary>
    [Fact]
    public void TryParse_ContactWithCrossProgramBit_Parses()
    {
        var parsed = RungParser.TryParse(@"XIC(\P2.Bit)OTE(Local);");

        Assert.NotNull(parsed);
        Assert.Equal(["XIC", "OTE"], parsed!.Select(i => i.Key.ToString()).ToArray());
    }

    /// <summary>Both fixture rung forms parse through the Rung overload, never throwing.</summary>
    [Fact]
    public void Parse_FixtureRungs_DoNotThrow()
    {
        var rungs = P1Rungs();
        var ex = Record.Exception(() =>
        {
            RungParser.Parse(rungs[0]);
            RungParser.Parse(rungs[1]);
            RungParser.Parse(rungs[2]);
        });
        Assert.Null(ex);
    }

    /// <summary>The tag operand keeps its program qualifier and exposes program and tag separately.</summary>
    [Fact]
    public void Operands_KeepProgramQualifier_AndExposeProgramAndTagSeparately()
    {
        var move = Assert.Single(RungParser.TryParse(@"MOVE(\P2.Src,Dst);")!);
        var operands = RungParser.Operands(move);

        Assert.Equal("P2", operands[0].Program);
        Assert.Equal("Src", operands[0].Tag);
        Assert.True(operands[0].IsProgramQualified);
        Assert.Equal(@"\P2.Src", operands[0].Qualified);
        Assert.Null(operands[1].Program);
        Assert.Equal("Dst", operands[1].Tag);
    }

    /// <summary>The fixture's XIC rung reports <c>P2</c> / <c>Bit</c>.</summary>
    [Fact]
    public void Operands_OfFixtureContactRung_ArePrograP2AndTagBit()
    {
        var xic = RungParser.Parse(P1Rungs()[1]).First();
        var operand = Assert.Single(RungParser.Operands(xic));

        Assert.Equal(("P2", "Bit"), (operand.Program, operand.Tag));
    }

    /// <summary>Member and index suffixes stay on the tag; the qualifier forms are equivalent.</summary>
    [Theory]
    [InlineData(@"\P2.Motor.Speed", "P2", "Motor.Speed")]
    [InlineData(@"\P2.Arr[3]", "P2", "Arr[3]")]
    [InlineData(@"\Program:P2.Bit", "P2", "Bit")]
    [InlineData("Program:P2.Bit", "P2", "Bit")]
    [InlineData("Plain.Member", null, "Plain.Member")]
    [InlineData(@"\P2", null, @"\P2")]
    public void RungOperand_Parse_SplitsProgramFromTag(string text, string? program, string tag)
    {
        var operand = RungOperand.Parse(text);
        Assert.Equal((program, tag), (operand.Program, operand.Tag));
    }

    /// <summary>The deliberately unparseable fixture rung yields null / throws, never a partial result.</summary>
    [Fact]
    public void UnparseableFixtureRung_IsReportedNotParsed()
    {
        var rung = P1Rungs()[3];

        Assert.Null(RungParser.TryParse(rung));
        Assert.Throws<ArgumentException>(() => RungParser.Parse(rung));
    }

    /// <summary>Enumerated motion operands still parse through the helper.</summary>
    [Fact]
    public void TryParse_EnumeratedMotionOperands_Parse()
    {
        Assert.NotNull(RungParser.TryParse("MAS(A,C,Units per sec2,% of Time);"));
    }

    /// <summary>Tokenize masks the backslash without moving any token.</summary>
    [Fact]
    public void Tokenize_CrossProgramReference_KeepsTokenIndexes()
    {
        var text = @"XIC(\P2.Bit)OTE(Local);";
        var tokens = RungParser.Tokenize(text);

        Assert.Equal(TokenType.EOF, tokens[^1].Type);
        Assert.Contains(tokens, t => t.Type == TokenType.Identifier && t.Index == text.IndexOf("OTE", StringComparison.Ordinal));
    }

    /// <summary>No source file outside the helper parses rungs directly.</summary>
    [Fact]
    public void NoCodeOutsideTheHelper_ParsesRungsDirectly()
    {
        var root = FixtureMnemonicGuardTests.RepoRoot();
        Assert.NotNull(root);
        var banned = new System.Text.RegularExpressions.Regex(
            @"new\s+Rung\s*\(|\.Instructions\s*\(\)|new\s+NeutralText\s*\(|\.Tokenize\s*\(\)");

        var offenders = Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && Path.GetFileName(f) != "RungParser.cs")
            .Where(f => banned.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string StripComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", "");
}
