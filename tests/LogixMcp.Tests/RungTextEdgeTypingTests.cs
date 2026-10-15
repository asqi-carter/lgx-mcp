using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Rung-text edge cases are refused by kind. Operand counts and unknown instructions are reported by
/// the stage-time reference check; a scanner syntax error, <c>X;;</c> and the position arguments are handled by the
/// SDK adapter (the tests for those are under <c>LOGIX_SDK</c>).
/// </summary>
public class RungTextEdgeTypingTests
{
    private static ReferenceSymbols Symbols(IReadOnlySet<string>? aois, params string[] tags) =>
        new("MainProgram",
            new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            aois);

    private static readonly IReadOnlySet<string> NoAois = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void OperandCountMismatch_XicWithTwoOperands_IsRefused_NamingTheRung()
    {
        var result = RungReferenceChecker.Check(["XIC(A,B)OTE(C);"], Symbols(NoAois, "A", "B", "C"));

        Assert.True(result.Refused);
        Assert.Equal("OperandCountMismatch", result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(0, finding.RungIndex);
        Assert.Equal("XIC(A,B)", finding.Name);
        Assert.Contains("1 operand", result.Message);
        Assert.Contains("2 given", result.Message);
        Assert.Contains("Nothing was imported or staged", result.Message);
    }

    [Theory]
    [InlineData("XIC();")]
    [InlineData("TON(T1,3000);")]
    [InlineData("MOV(A);")]
    [InlineData("ADD(A,B);")]
    [InlineData("EQU(A,B,C);")]
    [InlineData("JSR();")]
    [InlineData("OTE(A,B);")]
    public void OperandCountMismatch_WrongCounts_AreRefused(string rung)
    {
        var result = RungReferenceChecker.Check([rung], Symbols(NoAois, "A", "B", "C", "T1"));

        Assert.Equal("OperandCountMismatch", result.Kind);
    }

    [Theory]
    [InlineData("XIC(A)OTE(B);")]
    [InlineData("TON(T1,3000,0);")]
    [InlineData("MOV(A,B);")]
    [InlineData("ADD(A,B,C);")]
    [InlineData("EQU(A,B)OTE(C);")]
    [InlineData("JSR(Sub1,0);")]
    [InlineData("JSR(Sub1);")]
    [InlineData("RET();")]
    [InlineData("CPT(C,A+B);")]
    [InlineData("CLR(A);")]
    public void OperandCount_CorrectCounts_AreNotRefused(string rung)
    {
        var result = RungReferenceChecker.Check([rung], Symbols(NoAois, "A", "B", "C", "T1"));

        Assert.False(result.Refused);
    }

    [Fact]
    public void OperandCount_AnAddOnInstructionThatSharesAName_IsNotChecked()
    {
        var aois = new HashSet<string>(["MOV"], StringComparer.OrdinalIgnoreCase);

        var result = RungReferenceChecker.Check(["MOV(A,B,C);"], Symbols(aois, "A", "B", "C"));

        Assert.NotEqual("OperandCountMismatch", result.Kind);
    }

    [Fact]
    public void UnknownInstruction_Foo_IsAWarningFinding_NotARefusal()
    {
        var result = RungReferenceChecker.Check(["FOO(x);"], Symbols(NoAois, "x"));

        Assert.False(result.Refused);
        var finding = Assert.Single(result.WarningFindings!);
        Assert.Equal("UnknownInstruction", finding.Kind);
        Assert.Equal("FOO", finding.Name);
        Assert.Contains(result.Warnings, w => w.StartsWith("UnknownInstruction:", StringComparison.Ordinal) && w.Contains("FOO"));
    }

    [Fact]
    public void OperandCountMismatch_AlongsideAnUndefinedTag_ReportsBoth_UnderTheTagKind()
    {
        var result = RungReferenceChecker.Check(["XIC(A,B)XIC(Missing);"], Symbols(NoAois, "A", "B"));

        Assert.Equal("UndefinedTag", result.Kind);
        Assert.Contains(result.Findings, f => f.Kind == "OperandCountMismatch");
        Assert.Contains(result.Findings, f => f.Kind == "UndefinedTag");
    }

    [Fact]
    public void ErrorKinds_AreDeclared()
    {
        Assert.Equal("RungSyntaxError", ErrorKinds.RungSyntaxError);
        Assert.Equal("IndexOutOfRange", ErrorKinds.IndexOutOfRange);
        Assert.Equal("MemberNotFound", ErrorKinds.MemberNotFound);
        Assert.Equal("StructureWriteUnsupported", ErrorKinds.StructureWriteUnsupported);
    }

#if LOGIX_SDK
    // ── Scanner syntax error ──────────────────────────────────────────────────

    private const string ScannerLog =
        "<Exception Severity=\"Error\" Mnemonic=\"XMLSrv_E_RUNG_SYNTAX\" HexCode=\"80042001\">" +
        "<![CDATA[Syntax error found while scanning import file at line 1, column 8 near 'OTE']]></Exception>";

    [Fact]
    public void RungSyntaxError_FromTheImportLog_CarriesLineColumnAndOffendingText()
    {
        var err = ProposeRungEditHandler.ClassifyRungSyntaxError(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES", [ScannerLog], []);

        Assert.NotNull(err);
        Assert.Equal("RungSyntaxError", err!.Kind);
        Assert.Contains("line 1", err.Error);
        Assert.Contains("column 8", err.Error);
        Assert.Contains("'OTE'", err.Error);
        Assert.Contains("Nothing was staged", err.Error);
    }

    [Fact]
    public void RungSyntaxError_NamedOnlyByTheExceptionText_IsStillClassified()
    {
        var err = ProposeRungEditHandler.ClassifyRungSyntaxError(
            "Syntax error found while scanning import file (line 4, column 2)", [], []);

        Assert.Equal("RungSyntaxError", err!.Kind);
        Assert.Contains("line 4, column 2", err.Error);
    }

    [Fact]
    public void RungSyntaxError_WhenTheLogGivesNoPosition_SaysSo()
    {
        var err = ProposeRungEditHandler.ClassifyRungSyntaxError(
            "failed", ["Syntax error found while scanning import file"], []);

        Assert.Equal("RungSyntaxError", err!.Kind);
        Assert.Contains("no position", err.Error);
    }

    [Fact]
    public void RungSyntaxError_NotRaised_ForOtherImportFailures()
    {
        Assert.Null(ProposeRungEditHandler.ClassifyRungSyntaxError(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES",
            ["<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_AUDIT_INSTRNOTDEFINED\"><![CDATA[Unknown instruction FOO]]></Exception>"],
            []));
    }

    // ── X;; ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DoubleTerminator_IsStagedAsOne_WithANote()
    {
        var err = ProposeRungEditHandler.ValidateTypedInput(
            null, [new RungInput("XIC(A)OTE(B);;", null)], null, out var rungs, out var notes);

        Assert.Null(err);
        Assert.Equal("XIC(A)OTE(B);", Assert.Single(rungs).Text);
        var note = Assert.Single(notes);
        Assert.Contains("rungs[0]", note);
        Assert.Contains("empty rung", note);
    }

    [Fact]
    public void DoubleTerminator_WithWhitespaceBetween_IsStagedAsOne()
    {
        ProposeRungEditHandler.ValidateTypedInput(
            null, [new RungInput("XIC(A)OTE(B); ;", null)], null, out var rungs, out var notes);

        Assert.Equal("XIC(A)OTE(B);", Assert.Single(rungs).Text);
        Assert.Single(notes);
    }

    [Fact]
    public void SingleTerminator_AndMissingTerminator_HaveNoNote()
    {
        ProposeRungEditHandler.ValidateTypedInput(
            null, [new RungInput("XIC(A)OTE(B);", null), new RungInput("XIC(C)OTE(D)", null)], null,
            out var rungs, out var notes);

        Assert.Equal(["XIC(A)OTE(B);", "XIC(C)OTE(D);"], rungs.Select(r => r.Text));
        Assert.Empty(notes);
    }

    [Fact]
    public void OnlySemicolons_IsInvalidRungText()
    {
        var err = ProposeRungEditHandler.ValidateTypedInput(
            null, [new RungInput(";;", null)], null, out _, out _);

        Assert.Equal("InvalidRungText", err!.Kind);
    }

    [Fact]
    public void SemicolonInsideAStringLiteral_IsLeftAlone()
    {
        ProposeRungEditHandler.ValidateTypedInput(
            null, [new RungInput("COP('a;;b',X,1);", null)], null, out var rungs, out var notes);

        Assert.Equal("COP('a;;b',X,1);", Assert.Single(rungs).Text);
        Assert.Empty(notes);
    }

    [Fact]
    public void StagedDoubleTerminator_MatchesTheSingleTerminatorTheCopyCarries_NoStagedEditLost()
    {
        var expected = OfflineBuildCheck.CollapseRepeatedTerminator("XIC(A)OTE(B);;");
        var present  = new HashSet<string>([ProposeRungEditHandler.NormalizeRungText("XIC(A)OTE(B);")], StringComparer.Ordinal);

        Assert.True(RungTextComparer.ContainsMatch(present, expected));
        Assert.Equal("XIC(A)OTE(B);", expected);
    }

    // ── insertPosition / replaceCount binding ─────────────────────────────────

    [Fact]
    public void BindPosition_StringValue_IsInvalidArguments_NamingTheParameter()
    {
        var json = LogixSdkTools.BindPosition("insertPosition", JsonSerializer.SerializeToElement("1"), out _);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("InvalidArguments", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("insertPosition", doc.RootElement.GetProperty("parameter").GetString());
        Assert.Contains("insertPosition", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void BindPosition_Null_IsInvalidArguments_NamingTheParameter()
    {
        var json = LogixSdkTools.BindPosition("replaceCount", null, out _);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("InvalidArguments", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("replaceCount", doc.RootElement.GetProperty("parameter").GetString());
    }

    [Fact]
    public void BindPosition_JsonNull_AndNegativeAndFraction_AreRefused()
    {
        Assert.NotNull(LogixSdkTools.BindPosition("insertPosition", JsonSerializer.SerializeToElement<object?>(null), out _));
        Assert.NotNull(LogixSdkTools.BindPosition("insertPosition", JsonSerializer.SerializeToElement(-1), out _));
        Assert.NotNull(LogixSdkTools.BindPosition("insertPosition", JsonSerializer.SerializeToElement(1.5), out _));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    public void BindPosition_Numbers_AreAccepted(uint n)
    {
        Assert.Null(LogixSdkTools.BindPosition("insertPosition", JsonSerializer.SerializeToElement(n), out var value));
        Assert.Equal(n, value);
        Assert.Null(LogixSdkTools.BindPosition("replaceCount", (int)n, out var boxed));
        Assert.Equal(n, boxed);
    }
#endif
}
