namespace LogixMcp.Tests;

/// <summary>
/// Pre-publish mnemonic check: WrongMnemonicSet in both directions, nested branches,
/// expression functions, UnsupportedOnRevision (ASCII), tag/AOI-name false positives, unknown set.
/// </summary>
public class MnemonicSetCheckerTests
{
    private static TargetRevisionInfo Target(int major) =>
        new(major, TargetRevision.MnemonicSetFor(major), TargetRevisionSource.MajorRev, null);

    private static readonly TargetRevisionInfo UnknownTarget = TargetRevision.UnknownTarget();

    private static MnemonicCheckResult Check(int major, params string[] rungs) =>
        MnemonicSetChecker.Check(rungs, Target(major));

    [Fact]
    public void LegacyNameOnIecTarget_IsRefused_WithSuggestedText()
    {
        var result = Check(38, "LES(x,500)OTE(y);");

        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(0, finding.RungIndex);
        Assert.Equal(["LES"], finding.Found);
        Assert.Equal(["LT"], finding.Expected);
        Assert.Equal(38, finding.TargetMajorRevision);
        Assert.Equal("LT(x,500)OTE(y);", finding.SuggestedText);
        Assert.Contains("suggestedText", result.Message);
    }

    [Fact]
    public void IecNameOnLegacyTarget_IsRefused_WithSuggestedText()
    {
        var result = Check(35, "XIC(a)MOVE(1,b);");

        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(["MOVE"], finding.Found);
        Assert.Equal(["MOV"], finding.Expected);
        Assert.Equal(35, finding.TargetMajorRevision);
        Assert.Equal("XIC(a)MOV(1,b);", finding.SuggestedText);
    }

    [Fact]
    public void MatchingSet_Passes()
    {
        Assert.False(Check(36, "LT(x,500)MOVE(1,y)OTE(z);").Refused);
        Assert.False(Check(35, "LES(x,500)MOV(1,y)OTE(z);").Refused);
    }

    [Fact]
    public void OnlyOffendingRungsAreReported_WithTheirIndex()
    {
        var result = Check(36, "XIC(a)OTE(b);", "GRT(x,1)OTE(c);", "XIC(d)MOV(1,e);");

        Assert.Equal([1, 2], result.Findings.Select(f => f.RungIndex));
        Assert.Equal("GT(x,1)OTE(c);", result.Findings[0].SuggestedText);
        Assert.Equal("XIC(d)MOVE(1,e);", result.Findings[1].SuggestedText);
    }

    [Fact]
    public void SeveralNamesInOneRung_AreOneEntry_AndAllReplaced()
    {
        var result = Check(36, "LES(a,1)[MOV(1,b),SQR(c,d)]EQU(a,2)OTE(e);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(["LES", "MOV", "SQR", "EQU"], finding.Found);
        Assert.Equal(["LT", "MOVE", "SQRT", "EQ"], finding.Expected);
        Assert.Equal("LT(a,1)[MOVE(1,b),SQRT(c,d)]EQ(a,2)OTE(e);", finding.SuggestedText);
    }

    [Fact]
    public void NestedBranch_IsScanned()
    {
        var iec = Check(36, "XIC(a)[[XIC(b),LES(x,5)],[XIC(c)[GRT(y,1),XIC(d)]]]OTE(z);");
        Assert.Equal("XIC(a)[[XIC(b),LT(x,5)],[XIC(c)[GT(y,1),XIC(d)]]]OTE(z);",
            Assert.Single(iec.Findings).SuggestedText);

        var legacy = Check(35, "[XIC(a),[XIC(b),GT(x,5)]]OTE(z);");
        Assert.Equal("[XIC(a),[XIC(b),GRT(x,5)]]OTE(z);", Assert.Single(legacy.Findings).SuggestedText);
    }

    [Fact]
    public void ExpressionFunction_InCpt_IsRefused_BothDirections()
    {
        var iec = Check(36, "CPT(d,SQR(a)+TRN(b)*2);");
        var iecFinding = Assert.Single(iec.Findings);
        Assert.Equal(["SQR", "TRN"], iecFinding.Found);
        Assert.Equal("CPT(d,SQRT(a)+TRUNC(b)*2);", iecFinding.SuggestedText);

        var legacy = Check(35, "CMP(ATAN(a)>0.5);");
        Assert.Equal("CMP(ATN(a)>0.5);", Assert.Single(legacy.Findings).SuggestedText);
    }

    [Fact]
    public void ExpressionFunction_InFalAndFsc_IsChecked()
    {
        Assert.True(Check(36, "FAL(c,5,all,d,SQR(a));").Refused);
        Assert.True(Check(36, "FSC(c,5,all,ASN(a)>1);").Refused);
    }

    [Fact]
    public void FunctionNameOutsideAnExpressionInstruction_IsNotAFunctionCall()
    {
        // Operands of MOVE are not expressions; a nested "call" shape there is not scanned.
        Assert.False(Check(36, "MOVE(SQR(a),b);").Refused);
    }

    // 2026-10-08 SDK probe (as reported in task logix-mcp-vazn.6; no probe log checked in): CMP(dA=>dB) and CMP(dA=<dB) are accepted on v38.
    [Fact]
    public void ArrowOperators_AreAcceptedOnRev38_InExpressions()
    {
        Assert.False(Check(38, "CMP(a=>b)OTE(c);").Refused);
        Assert.False(Check(38, "CMP(a=<b);").Refused);
        Assert.False(Check(38, "CPT(d,(b=<3)+1);").Refused);
    }

    [Fact]
    public void ArrowOperators_PassOnEveryRevision()
    {
        foreach (var major in new[] { 35, 36, 37, 38 })
            Assert.False(Check(major, "CMP(a=>5);", "CMP(a=<b);").Refused, $"v{major}");
    }

    [Fact]
    public void ModernComparisonOperators_Pass()
    {
        Assert.False(Check(38, "CMP(a>=5);", "CPT(d,(b<=3));").Refused);
    }

    [Fact]
    public void AsciiInstruction_IsRefusedOnRev37_AndUp()
    {
        var result = Check(37, "XIC(a)AWT(chan,src,ctl,1,0);");

        Assert.Equal(MnemonicCheckKinds.UnsupportedOnRevision, result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(["AWT"], finding.Found);
        Assert.Equal(37, finding.TargetMajorRevision);
        Assert.Null(finding.SuggestedText);

        foreach (var name in new[] { "AWA", "ARD", "ARL", "ABL", "ACB", "ACL", "AHL" })
            Assert.True(Check(38, $"{name}(a,b);").Refused, name);
    }

    [Fact]
    public void AsciiInstruction_PassesOnRev36AndEarlier()
    {
        Assert.False(Check(36, "AWT(chan,src,ctl,1,0);").Refused);
        Assert.False(Check(35, "ARD(chan,dest,ctl,1,0);").Refused);
    }

    [Theory]
    [InlineData(36, "XIC(MOV_Count)OTE(LES_Timer);")]
    [InlineData(36, "XIC(MOV.Done)MOVE(LES_Timer.PRE,MOV_Count);")]
    [InlineData(35, "XIC(MOVE_Count)OTE(LT_Timer);")]
    [InlineData(36, "MyAoi(LES_Instance,MOV)OTE(SQR);")]
    [InlineData(36, "XIC(\\Prog:Main.MOV)OTE(Arr[LES_Idx]);")]
    [InlineData(36, "CPT(d,SQRT(MOV_Count)+LES_Timer.ACC);")]
    [InlineData(38, "CMP(MOV_Count>LES_Timer);")]
    public void TagAndAoiNamesContainingAMnemonic_DoNotTrigger(int major, string rung)
    {
        Assert.False(Check(major, rung).Refused);
    }

    [Fact]
    public void CmpWithLesFunction_ReturnsWrongMnemonicSet_SuggestingOperatorForm()
    {
        var result = Check(35, "CMP(LES(a,b))OTE(c);");

        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(["LES"], finding.Found);
        Assert.Equal(["<"], finding.Expected);
        Assert.Equal("CMP(a<b)OTE(c);", finding.SuggestedText);

        // Refused on an IEC target too, with the IEC spelling.
        var iec = Check(38, "CMP(LT(a,b));");
        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, iec.Kind);
        Assert.Equal("CMP(a<b);", Assert.Single(iec.Findings).SuggestedText);
    }

    [Theory]
    [InlineData("EQU(a,b)", "a=b")]
    [InlineData("NEQ(a,b)", "a<>b")]
    [InlineData("GRT(a,b)", "a>b")]
    [InlineData("GEQ(a,b)", "a>=b")]
    [InlineData("LEQ(a,b)", "a<=b")]
    [InlineData("GT(a, b)", "a>b")]
    public void ComparisonMnemonicsAsFunctions_AreRewrittenToOperators(string call, string op)
    {
        var finding = Assert.Single(Check(36, $"CMP({call});").Findings);
        Assert.Equal($"CMP({op});", finding.SuggestedText);
    }

    [Fact]
    public void ComparisonMnemonicInsideCpt_IsRefused()
    {
        var result = Check(36, "CPT(d,LES(a,b));");

        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(["LES"], finding.Found);
        Assert.Equal("CPT(d,a<b);", finding.SuggestedText);

        var embedded = Assert.Single(Check(36, "CPT(d,1+GRT(a,b));").Findings);
        Assert.Equal("CPT(d,1+(a>b));", embedded.SuggestedText);
    }

    [Fact]
    public void ComparisonMnemonicAsFunction_CombinesWithRenamedFunctions()
    {
        var finding = Assert.Single(Check(36, "CMP(LES(SQR(a),b));").Findings);
        Assert.Equal(["LES", "SQR"], finding.Found);
        Assert.Equal("CMP(SQRT(a)<b);", finding.SuggestedText);
    }

    [Fact]
    public void ComparisonMnemonicWithWrongArgumentCount_IsRefusedWithoutSuggestion()
    {
        var finding = Assert.Single(Check(36, "CMP(LES(a));").Findings);
        Assert.Null(finding.SuggestedText);
    }

    private static void AssertCompoundWithheld(string rung)
    {
        var result = Check(36, rung);
        var finding = Assert.Single(result.Findings);
        Assert.Null(finding.SuggestedText);
        Assert.Contains("RxCMP_E_OPERAND_INVALIDEXPR", finding.Reason);
        Assert.Contains("RxCMP_E_OPERAND_INVALIDEXPR", result.Message);
        Assert.Contains("No suggestedText", result.Message);
    }

    [Fact]
    public void CmpWithAndOfComparisons_HasNoSuggestion_AndStatesReason()
    {
        AssertCompoundWithheld("CMP(GRT(a,b) AND LES(b,100));");
        AssertCompoundWithheld("CMP((GRT(a,b)) AND (LES(b,100)))OTE(c);");
    }

    [Fact]
    public void CmpWithNotOfComparison_HasNoSuggestion()
    {
        AssertCompoundWithheld("CMP(NOT LES(a,b));");
    }

    [Fact]
    public void CmpWithComparisonComparedToValue_HasNoSuggestion()
    {
        AssertCompoundWithheld("CMP(LES(a,b)=1);");
    }

    [Fact]
    public void CompoundCmp_DoesNotSuppressSuggestionForOtherRungs()
    {
        var result = Check(36, "CMP(GRT(a,b) AND LES(b,100));", "CMP(LES(a,b));");
        Assert.Null(result.Findings[0].SuggestedText);
        Assert.Equal("CMP(a<b);", result.Findings[1].SuggestedText);
        Assert.Null(result.Findings[1].Reason);
    }

    [Fact]
    public void TagNamedLikeComparisonMnemonic_IsNotFlagged()
    {
        Assert.False(Check(36, "CMP(LES_Timer>5)OTE(GRT_Flag);").Refused);
        Assert.False(Check(38, "CPT(d,EQU_Count+LES_Timer.ACC);").Refused);
        Assert.False(Check(36, "CMP(a<b)OTE(c);").Refused);
    }

    [Fact]
    public void BareMnemonicUsedAsTagName_DoesNotTrigger()
    {
        // A tag literally named like a mnemonic, as an operand (not followed by '(').
        Assert.False(Check(36, "XIC(LES)OTE(MOV);").Refused);
        Assert.False(Check(35, "XIC(LT)OTE(MOVE);").Refused);
    }

    [Fact]
    public void AoiSpelledLikeAMnemonic_IsNotFlagged()
    {
        var result = MnemonicSetChecker.Check(
            ["LES(a,b);"], Target(36), () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LES" });

        Assert.False(result.Refused);
    }

    [Fact]
    public void AoiSpelledLikeAMnemonic_IsNotFlagged_WhateverTheCaseOrSetComparer()
    {
        var result = MnemonicSetChecker.Check(
            ["les(a,b);"], Target(36), () => new HashSet<string>(StringComparer.Ordinal) { "LES" });

        Assert.False(result.Refused);
    }

    [Fact]
    public void LowercaseLegacyMnemonic_IsStillRefusedOnIecTarget()
    {
        Assert.True(Check(36, "les(a,b);").Refused);
    }

    [Fact]
    public void UnknownSet_PassesWithAWarning()
    {
        var result = MnemonicSetChecker.Check(["LES(x,500)OTE(y);", "AWT(a,b);"], UnknownTarget);

        Assert.False(result.Refused);
        Assert.Empty(result.Findings);
        Assert.NotNull(result.Warning);
        Assert.Contains("unknown", result.Warning);
    }

    [Fact]
    public void NoTarget_IsTreatedAsUnknown()
    {
        var result = MnemonicSetChecker.Check(["LES(x,500)OTE(y);"], null);

        Assert.False(result.Refused);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void NoRungs_NeitherRefusesNorWarns()
    {
        var result = MnemonicSetChecker.Check([], UnknownTarget);

        Assert.False(result.Refused);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void WrongSetAndUnsupportedInOneRung_ReturnsBothFindings()
    {
        var result = Check(38, "LES(a,1)AWT(b,c);");

        Assert.Equal(MnemonicCheckKinds.WrongMnemonicSet, result.Kind);
        Assert.Equal(2, result.Findings.Count);
        var wrong = Assert.Single(result.Findings, f => f.Kind == MnemonicCheckKinds.WrongMnemonicSet);
        var unsupported = Assert.Single(result.Findings, f => f.Kind == MnemonicCheckKinds.UnsupportedOnRevision);
        Assert.Equal(0, wrong.RungIndex);
        Assert.Equal(["LES"], wrong.Found);
        Assert.Equal(["LT"], wrong.Expected);
        Assert.Equal(0, unsupported.RungIndex);
        Assert.Equal(["AWT"], unsupported.Found);
    }

    [Fact]
    public void WrongSetAndUnsupportedInOneRung_WithholdsSuggestedText_AndMessageSaysWhy()
    {
        var result = Check(38, "LES(a,1)AWT(b,c);");

        Assert.All(result.Findings, f => Assert.Null(f.SuggestedText));
        Assert.Contains("No suggestedText", result.Message);
        Assert.Contains("unsupported", result.Message);
        Assert.Contains("AWT", result.Message);
    }

    [Fact]
    public void SuggestedText_IsKept_ForRungsWithoutUnsupportedInstructions()
    {
        var result = Check(38, "LES(a,1)AWT(b,c);", "LES(x,2)OTE(y);");

        var clean = Assert.Single(result.Findings, f => f.RungIndex == 1);
        Assert.Equal("LT(x,2)OTE(y);", clean.SuggestedText);
        Assert.Equal(3, result.Findings.Count);
    }

    [Fact]
    public void QuotedText_IsNotScanned()
    {
        Assert.False(Check(36, "MOVE('LES(x)',b);").Refused);
    }

    [Fact]
    public void RungTextsFromFragment_ReadsRungText_AndToleratesGarbage()
    {
        const string fragment =
            "<RSLogix5000Content><Controller><Programs><Program><Routines><Routine><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[LES(a,1)OTE(b);]]></Text></Rung>" +
            "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[XIC(c)OTE(d);]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

        Assert.Equal(["LES(a,1)OTE(b);", "XIC(c)OTE(d);"], MnemonicSetChecker.RungTextsFromFragment(fragment));
        Assert.Empty(MnemonicSetChecker.RungTextsFromFragment("not xml"));
        Assert.Empty(MnemonicSetChecker.RungTextsFromFragment(null));
    }

    [Fact]
    public void Findings_SerializeWithTheContractFieldNames()
    {
        var result = Check(36, "LES(x,500)OTE(y);");
        using var doc = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(result.Findings));
        var entry = doc.RootElement[0];

        Assert.Equal(0, entry.GetProperty("rungIndex").GetInt32());
        Assert.Equal("LES", entry.GetProperty("found")[0].GetString());
        Assert.Equal("LT", entry.GetProperty("expected")[0].GetString());
        Assert.Equal(36, entry.GetProperty("targetMajorRevision").GetInt32());
        Assert.Equal("LT(x,500)OTE(y);", entry.GetProperty("suggestedText").GetString());
    }
}
