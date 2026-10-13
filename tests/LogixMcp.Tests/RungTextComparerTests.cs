namespace LogixMcp.Tests;

/// <summary>
/// The single rung-text comparator treats an export's <c>?</c> operands as a wildcard only
/// for the table instructions (TON, TOF, RTO, CTU, CTD; operands 2 and 3) and nothing else.
/// </summary>
public class RungTextComparerTests
{
    [Fact]
    public void TonPresetAndAccumulator_MatchExportPlaceholders()
    {
        Assert.True(RungTextComparer.Matches("TON(T,3000,0)", "TON(T,?,?)"));
    }

    [Fact]
    public void TonWithDifferentTag_DoesNotMatchPlaceholders()
    {
        Assert.False(RungTextComparer.Matches("TON(T,3000,0)", "TON(U,?,?)"));
    }

    [Fact]
    public void PlaceholderOutsideTable_XicDoesNotMatch()
    {
        Assert.False(RungTextComparer.Matches("XIC(A)", "XIC(?)"));
    }

    [Fact]
    public void NestedBranchContainingTon_ComparesCorrectly()
    {
        const string expected = "XIC(A)[XIC(B)TON(T1,3000,0),XIO(C)[OTE(D),TON(T2,500,0)]]OTE(E);";
        const string exported = "XIC(A)[XIC(B)TON(T1,?,?),XIO(C)[OTE(D),TON(T2,?,?)]]OTE(E);";
        Assert.True(RungTextComparer.Matches(expected, exported));

        // A different tag inside the nested branch, or a changed non-table operand, must not match.
        Assert.False(RungTextComparer.Matches(expected,
            "XIC(A)[XIC(B)TON(T1,?,?),XIO(C)[OTE(D),TON(T3,?,?)]]OTE(E);"));
        Assert.False(RungTextComparer.Matches(expected,
            "XIC(A)[XIC(B)TON(T1,?,?),XIO(C)[OTE(X),TON(T2,?,?)]]OTE(E);"));
        // Different branch structure.
        Assert.False(RungTextComparer.Matches(expected,
            "XIC(A)XIC(B)TON(T1,?,?)XIO(C)[OTE(D),TON(T2,?,?)]OTE(E);"));
    }

    [Theory]
    [InlineData("TOF(T,100,0)", "TOF(T,?,?)")]
    [InlineData("RTO(T,100,0)", "RTO(T,?,?)")]
    [InlineData("CTU(C,10,0)", "CTU(C,?,?)")]
    [InlineData("CTD(C,10,0)", "CTD(C,?,?)")]
    public void EveryTableInstruction_MatchesPlaceholders(string expected, string exported)
    {
        Assert.True(RungTextComparer.Matches(expected, exported));
    }

    [Fact]
    public void ExportedLiteralStillHasToEqualExpected()
    {
        Assert.False(RungTextComparer.Matches("TON(T,3000,0)", "TON(T,2000,0)"));
        Assert.True(RungTextComparer.Matches("TON(T,3000,0)", "TON(T,3000,0)"));
    }

    [Fact]
    public void ExpectedPlaceholder_IsNotAWildcard()
    {
        Assert.False(RungTextComparer.Matches("TON(T,?,?)", "TON(T,3000,0)"));
    }

    [Fact]
    public void TagOperand_IsNeverAWildcard_EvenForTableInstructions()
    {
        Assert.False(RungTextComparer.Matches("TON(T,3000,0)", "TON(?,3000,0)"));
    }

    [Fact]
    public void OperandCountMismatch_DoesNotMatch()
    {
        Assert.False(RungTextComparer.Matches("TON(T,3000,0)", "TON(T,?)"));
    }

    [Fact]
    public void LineEndingsAndOuterWhitespace_AreStillNormalised()
    {
        Assert.True(RungTextComparer.Matches("  XIC(A)\r\nOTE(B);  ", "XIC(A)\nOTE(B);"));
    }

    [Fact]
    public void BranchWhitespace_IsIgnoredBetweenInstructions()
    {
        Assert.True(RungTextComparer.Matches("[XIC(a) XIO(b),XIC(c)]OTE(d);", "[XIC(a)XIO(b),XIC(c)]OTE(d);"));
    }

    [Fact]
    public void InterInstructionWhitespace_IsIgnored()
    {
        Assert.True(RungTextComparer.Matches("XIC(a) XIO(b)", "XIC(a)XIO(b)"));
    }

    [Fact]
    public void CptExpression_IsComparedExactly()
    {
        Assert.True(RungTextComparer.Matches("CPT(x,a + b)", "CPT(x,a + b)"));
        Assert.False(RungTextComparer.Matches("CPT(x,a + b)", "CPT(x,a+b)"));
        Assert.Equal("CPT(x,a + b)OTE(d);", RungTextComparer.Normalize(" CPT(x,a + b) OTE(d); "));
    }

    [Fact]
    public void TonPlaceholder_StillMatchesWithInterInstructionWhitespace()
    {
        Assert.True(RungTextComparer.Matches("TON(T,3000,0)", "TON(T,?,?)"));
        Assert.True(RungTextComparer.Matches("XIC(a) TON(T,3000,0)", "XIC(a)TON(T,?,?)"));
    }

    [Fact]
    public void CommaInsideArraySubscript_DoesNotSplitOperand()
    {
        Assert.True(RungTextComparer.Matches("TON(Arr[1,2],3000,0)", "TON(Arr[1,2],?,?)"));
        Assert.False(RungTextComparer.Matches("TON(Arr[1,2],3000,0)", "TON(Arr[1,3],?,?)"));
    }

    [Fact]
    public void FindKey_PrefersExactAndSkipsExhaustedBudget()
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TON(T,3000,0);"] = 0,
            ["TON(T,5000,0);"] = 1,
        };
        Assert.Equal("TON(T,5000,0);", RungTextComparer.FindKey(budget, "TON(T,?,?);"));
        Assert.Null(RungTextComparer.FindKey(budget, "TON(U,?,?);"));
    }

    [Fact]
    public void ContainsMatch_FindsPlaceholderRung()
    {
        var exported = new[] { "XIC(A)OTE(B);", "XIC(P)TON(T,?,?);" };
        Assert.True(RungTextComparer.ContainsMatch(exported, "XIC(P)TON(T,3000,0);"));
        Assert.False(RungTextComparer.ContainsMatch(exported, "XIC(P)TON(U,3000,0);"));
    }

    [Fact]
    public void XIC_cmd_motorstart_matches_XIC_Cmd_MotorStart()
    {
        Assert.True(RungTextComparer.Matches("XIC(cmd_motorstart)", "XIC(Cmd_MotorStart)"));
    }

    [Fact]
    public void MOVE_0005_dA_does_not_match_MOVE_5_dA()
    {
        Assert.False(RungTextComparer.Matches("MOVE(0005,dA)", "MOVE(5,dA)"));
        Assert.True(RungTextComparer.Matches("MOVE(0005,dA)", "MOVE(0005,DA)"));
    }

    [Fact]
    public void XIC_lowercase_mnemonic_vs_uppercase_matches_because_logix_normalises_case()
    {
        Assert.True(RungTextComparer.Matches("xic(A)", "XIC(A)"));
    }

    [Fact]
    public void MemberPathsAndJsrRoutineNames_AreCaseInsensitive_StringLiteralsAreNot()
    {
        Assert.True(RungTextComparer.Matches("XIC(Mtr.Run)JSR(Sub_One,0);", "XIC(mtr.RUN)JSR(sub_one,0);"));
        Assert.False(RungTextComparer.Matches("MOV('Abc',S)", "MOV('abc',S)"));
        Assert.True(RungTextComparer.Matches("TON(t1,3000,0)", "TON(T1,?,?)"));
    }

    // ── N2: instruction and AOI names are case-insensitive (Logix stores benchaoi as BenchAoi) ──

    [Fact]
    public void LowercaseAoiCall_IsCountedAsLanded_AndCensusMatches()
    {
        const string staged = "XIC(Run)benchaoi(Run,Lamp)OTE(Out);";
        const string exported = "XIC(Run)BenchAoi(Run,Lamp)OTE(Out);";

        Assert.True(RungTextComparer.Matches(staged, exported));
        Assert.True(RungTextComparer.ContainsMatch([exported], staged));
        Assert.True(RungTextComparer.AnyExpectedMatches([staged], exported));

        // The per-routine census consumes the staged text against the exported rung.
        var budget = new Dictionary<string, int> { [RungTextComparer.Normalize(staged)] = 1 };
        Assert.Equal(RungTextComparer.Normalize(staged),
            RungTextComparer.FindKey(budget, RungTextComparer.Normalize(exported)));
    }

    [Fact]
    public void Census_StillFlagsARealInstructionDifference()
    {
        Assert.False(RungTextComparer.Matches("XIC(Run)benchaoi(Run,Lamp);", "XIC(Run)OtherAoi(Run,Lamp);"));
        Assert.False(RungTextComparer.Matches("XIC(Run)OTE(Out);", "XIO(Run)OTE(Out);"));
        Assert.False(RungTextComparer.ContainsMatch(["XIO(Run)OTE(Out);"], "xic(Run)ote(Out);"));

        var budget = new Dictionary<string, int> { ["xic(Run)ote(Out);"] = 1 };
        Assert.Null(RungTextComparer.FindKey(budget, "XIO(Run)OTE(Out);"));
    }

    [Fact]
    public void LowercaseTimerMnemonic_StillGetsPlaceholderTolerance()
    {
        Assert.True(RungTextComparer.Matches("ton(T,3000,0)", "TON(T,?,?)"));
        Assert.True(RungTextComparer.PlaceholderOperands.ContainsKey("ton"));
    }

    [Fact]
    public void ReferencePreCheck_LowercaseAoiCall_IsKnown_AndMissingAoiStillWarns()
    {
        var aois = new HashSet<string>(StringComparer.Ordinal) { "BenchAoi" };
        var symbols = new ReferenceSymbols("P", new HashSet<string> { "Run" }, null, null,
            new HashSet<string>(), aois);

        var ok = RungReferenceChecker.Check(["benchaoi(Run);"], symbols);
        Assert.False(ok.Refused);
        Assert.Empty(ok.Warnings);

        var unknown = RungReferenceChecker.Check(["otheraoi(Run);"], symbols);
        Assert.False(unknown.Refused);
        Assert.NotEmpty(unknown.Warnings);
    }
}
