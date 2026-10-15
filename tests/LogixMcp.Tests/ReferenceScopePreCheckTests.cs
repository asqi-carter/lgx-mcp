namespace LogixMcp.Tests;

/// <summary>
/// The reference pre-check also catches a bare tag that lives in another program
/// (<c>UndefinedTag</c> with a hint), a non-Public <c>\Prog.Tag</c> (<c>TagNotPublic</c>) and a missing member of a
/// known type (<c>UndefinedMember</c>). Pure checker tests: they need no SDK.
/// </summary>
public class ReferenceScopePreCheckTests
{
    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static ReferenceSymbols Symbols() => new(
        "Main",
        Set("T1", "Run"),
        Set("MainLocal"),
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Main"] = Set("MainLocal"),
            ["Prog"] = Set("Local", "Shared", "OtherTimer"),
        },
        Set(),
        Set(),
        LocalProgramTags: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Prog"] = Set("Local"),
        },
        ControllerTagTypes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["T1"] = "TIMER", ["Run"] = "BOOL",
        },
        ProgramTagTypes: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Prog"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OtherTimer"] = "TIMER", ["Local"] = "BOOL" },
        },
        TypeMembers: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pump"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Speed"] = "REAL", ["Tmr"] = "TIMER",
            },
        });

    [Fact]
    public void BareOtherProgramTag_IsUndefinedTag_WithUseProgramDotTagHint()
    {
        var result = RungReferenceChecker.Check(["XIC(Shared)OTE(Run);"], Symbols());

        Assert.Equal(ReferenceCheckKinds.UndefinedTag, result.Kind);
        Assert.Equal("use \\<Program>.<Tag> if it is Public", result.Hint);
        Assert.Contains("'Prog'", result.Message);
    }

    [Fact]
    public void BareTagInNoProgram_IsUndefinedTag_WithoutTheOtherProgramHint()
    {
        var result = RungReferenceChecker.Check(["XIC(Nowhere)OTE(Run);"], Symbols());

        Assert.Equal(ReferenceCheckKinds.UndefinedTag, result.Kind);
        Assert.Null(result.Hint);
    }

    [Fact]
    public void NonPublicTagOfAnotherProgram_IsTagNotPublic()
    {
        var result = RungReferenceChecker.Check(["XIC(\\Prog.Local)OTE(Run);"], Symbols());

        Assert.Equal(ReferenceCheckKinds.TagNotPublic, result.Kind);
        Assert.Contains("\\Prog.Local", result.Message);
    }

    [Fact]
    public void PublicTagOfAnotherProgram_IsAccepted()
    {
        Assert.False(RungReferenceChecker.Check(["XIC(\\Prog.Shared)OTE(Run);"], Symbols()).Refused);
    }

    [Fact]
    public void TagWithUnknownUsage_IsNeverRefusedAsNotPublic()
    {
        var symbols = Symbols() with { LocalProgramTags = null };

        Assert.False(RungReferenceChecker.Check(["XIC(\\Prog.Local)OTE(Run);"], symbols).Refused);
    }

    [Fact]
    public void MissingMemberOfKnownType_IsUndefinedMember()
    {
        var result = RungReferenceChecker.Check(["XIC(T1.NoMember)OTE(Run);"], Symbols());

        Assert.Equal(ReferenceCheckKinds.UndefinedMember, result.Kind);
        Assert.Contains("T1.NoMember", result.Message);
    }

    [Theory]
    [InlineData("XIC(T1.DN)OTE(Run);", false)]
    [InlineData("MOV(T1.ACC,Run);", false)]
    [InlineData("XIC(\\Prog.OtherTimer.TT)OTE(Run);", false)]
    [InlineData("XIC(\\Prog.OtherTimer.Bogus)OTE(Run);", true)]
    [InlineData("XIC(Run.0)OTE(Run);", false)]                  // bit select on a non-struct
    public void MemberCheck_RefusesOnlyWhatTheTypeCertainlyLacks(string rung, bool refused)
    {
        Assert.Equal(refused, RungReferenceChecker.Check([rung], Symbols()).Refused);
    }

    [Fact]
    public void MemberCheck_WalksUserTypes_AndSkipsTagsWhoseTypeIsUnknown()
    {
        var symbols = Symbols() with
        {
            ControllerTags = Set("T1", "Run", "P1", "Mystery"),
            ControllerTagTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["P1"] = "Pump", ["T1"] = "TIMER", ["Run"] = "BOOL", ["Mystery"] = "SomeModuleType",
            },
        };

        Assert.False(RungReferenceChecker.Check(["MOV(P1.Speed,Run);XIC(P1.Tmr.DN)OTE(Run);"], symbols).Refused);
        Assert.Equal(ReferenceCheckKinds.UndefinedMember,
            RungReferenceChecker.Check(["XIC(P1.Tmr.Nope)OTE(Run);"], symbols).Kind);
        Assert.Equal(ReferenceCheckKinds.UndefinedMember,
            RungReferenceChecker.Check(["MOV(P1.Velocity,Run);"], symbols).Kind);
        Assert.False(RungReferenceChecker.Check(["MOV(Mystery.Anything,Run);"], symbols).Refused);
    }

    [Fact]
    public void ExportReaders_ReadUsageTypesAndDataTypeMembers()
    {
        const string programs =
            "<Controller><Programs><Program Name=\"Prog\"><Tags>" +
            "<Tag Name=\"Local\" DataType=\"BOOL\" Usage=\"Local\"/><Tag Name=\"Pub\" DataType=\"TIMER\" Usage=\"Public\"/>" +
            "<Tag Name=\"Old\" DataType=\"DINT\"/></Tags><Routines/></Program></Programs></Controller>";
        const string types =
            "<Controller><DataTypes><DataType Name=\"Pump\"><Members>" +
            "<Member Name=\"Speed\" DataType=\"REAL\"/><Member Name=\"Run\" DataType=\"BIT\"/></Members></DataType></DataTypes></Controller>";

        var local = RungReferenceChecker.LocalProgramTagsFromExport(programs)!;
        Assert.Contains("Local", local["prog"]);
        Assert.DoesNotContain("Pub", local["Prog"]);
        Assert.Contains("Old", local["Prog"]);                  // a real export leaves Usage out of ordinary local tags
        Assert.Equal("TIMER", RungReferenceChecker.ProgramTagTypesFromExport(programs)!["Prog"]["pub"]);
        Assert.Equal("REAL", RungReferenceChecker.DataTypeMembersFromExport(types)!["pump"]["speed"]);
    }

    // ── N7: every category in one list ─────────────────

    [Fact]
    public void RungWithSeveralErrorCategories_ReportsAllOfThemInOneFindingList()
    {
        // One rung: an undefined tag, a non-Public tag of another program and a missing member.
        var result = RungReferenceChecker.Check(
            ["XIC(Nowhere)XIC(\\Prog.Local)XIC(T1.NoMember)OTE(Run);"], Symbols());

        Assert.True(result.Refused);
        Assert.Equal(ReferenceCheckKinds.UndefinedTag, result.Kind);
        Assert.Equal(
            [ReferenceCheckKinds.UndefinedTag, ReferenceCheckKinds.TagNotPublic, ReferenceCheckKinds.UndefinedMember],
            result.Findings.Select(f => f.Kind).ToArray());
        foreach (var f in result.Findings)
            Assert.Contains(f.Kind, RungReferenceChecker.DescribeFinding(f));
        Assert.Contains("Nowhere", result.Message);
        Assert.Contains("\\Prog.Local", result.Message);
        Assert.Contains("T1.NoMember", result.Message);
    }

#if LOGIX_SDK
    [Fact]
    public void RungWithSeveralErrorCategories_ReportsAllOfThemInRejectReasons()
    {
        var result = RungReferenceChecker.Check(
            ["XIC(Nowhere)XIC(\\Prog.Local)XIC(T1.NoMember)OTE(Run);"], Symbols());

        var reasons = ProposeRungEditHandler.PreCheckRejectReasons(result);

        Assert.Equal(3, reasons.Count);
        Assert.Equal(
            [ReferenceCheckKinds.UndefinedTag, ReferenceCheckKinds.TagNotPublic, ReferenceCheckKinds.UndefinedMember],
            reasons.Select(r => r.Kind).ToArray());
        Assert.All(reasons, r => Assert.Equal(0, r.RungIndex));
    }
#endif
}
