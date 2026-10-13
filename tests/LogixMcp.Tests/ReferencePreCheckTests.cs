#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change requirement 5 (task logix-mcp-64f5.5): the cheap tag and instruction pre-check on every stage and
/// publish (<c>UndefinedTag</c>, <c>UnknownInstruction</c>, never a false refusal), and
/// <c>buildVerify: "passed-excluding-staged"</c> when the staged rungs were not verified.
/// </summary>
public class ReferencePreCheckTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string TagsXml =
        "<RSLogix5000Content><Controller Name=\"C\"><Tags>" +
        "<Tag Name=\"Run\" TagType=\"Base\" DataType=\"BOOL\"/>" +
        "<Tag Name=\"Lamp\" TagType=\"Base\" DataType=\"BOOL\"/>" +
        "<Tag Name=\"Idx\" TagType=\"Base\" DataType=\"DINT\"/>" +
        "<Tag Name=\"Arr\" TagType=\"Base\" DataType=\"DINT\" Dimensions=\"10\"/>" +
        "<Tag Name=\"RunAlias\" TagType=\"Alias\" AliasFor=\"Run\"/>" +
        "<Tag Name=\"Tmr\" TagType=\"Base\" DataType=\"TIMER\"/>" +
        "</Tags></Controller></RSLogix5000Content>";

    private const string ProgramsXml =
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Tags><Tag Name=\"LocalFlag\" DataType=\"BOOL\"/></Tags>" +
        "<Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"/></Routines></Program>" +
        "<Program Name=\"P2\"><Tags><Tag Name=\"Tag\" DataType=\"BOOL\" Usage=\"Public\"/></Tags>" +
        "<Routines><Routine Name=\"R\" Type=\"RLL\"/></Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    private const string AoiXml =
        "<RSLogix5000Content><Controller><AddOnInstructionDefinitions>" +
        "<AddOnInstructionDefinition Name=\"MyAoi\"/>" +
        "</AddOnInstructionDefinitions></Controller></RSLogix5000Content>";

    private static string Fragment(string text) =>
        ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", text);

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        fake.ExportContentByXPath["Controller/Tags"] = TagsXml;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] = ProgramsXml;
        fake.ExportContentByXPath["Controller/AddOnInstructionDefinitions"] = AoiXml;
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, fake);
    }

    private static async Task<object> StageAsync(LogixProjectSession session, string rung, string? target = null) =>
        await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(rung), session, target: target);

    // ── contractual criteria ──────────────────────────────────────────────

    [Fact]
    public async Task UndefinedTag_IsRefusedBeforeStaging()
    {
        var (session, fake) = await OpenAsync();

        var result = await StageAsync(session, "XIC(NoSuchTag)OTE(Lamp);");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("UndefinedTag", error.Kind);
        Assert.Contains("NoSuchTag", error.Error);
        Assert.Empty(fake.CapturedImportOptions);          // nothing imported
        Assert.Empty(session.StagedRungBuffer);            // nothing staged
    }

    /// <summary>logix-mcp-qpqy.12: a publish blocked by pending edits also reports the tag and instruction pre-check.</summary>
    [Fact]
    public async Task Publish_BlockedByPendingEdits_ReportsBlockAndPreCheckTogether()
    {
        var (session, fake) = await OpenAsync();
        fake.ProgramExportCleanBeforeImport = false;
        fake.ExportContent =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"rN\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(NoSuchTag)OTE(Lamp);"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsBlockImport", error.Kind);
        Assert.Contains("No import was attempted", error.Error, StringComparison.Ordinal);
        Assert.Contains("pre-check also failed", error.Error, StringComparison.Ordinal);
        Assert.Contains("NoSuchTag", error.Error, StringComparison.Ordinal);
        var preCheck = Assert.IsType<PublishPreCheckReport>(error.PreCheck);
        Assert.Equal("UndefinedTag", preCheck.Kind);
        Assert.Contains(preCheck.RejectReasons!, r => r.Text.Contains("NoSuchTag", StringComparison.Ordinal));
        Assert.Empty(fake.CapturedImportOptions);

        // A clean pre-check leaves the plain pending-edits refusal.
        var (session2, fake2) = await OpenAsync();
        fake2.ProgramExportCleanBeforeImport = false;
        fake2.ExportContent = fake.ExportContent;
        var clean = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(Run)OTE(Lamp);"), session2, target: "controller"));
        Assert.Equal("PendingEditsBlockImport", clean.Kind);
        Assert.Null(clean.PreCheck);
    }

    [Fact]
    public async Task UnknownInstruction_IsNotRefused_ItWarns()
    {
        var (session, _) = await OpenAsync();

        var result = await StageAsync(session, "XYZ(a);");

        // Not refused for the name (Logix is the final check); any other error would be about something else.
        if (result is ProposeRungEditError error) Assert.NotEqual("UnknownInstruction", error.Kind);
    }

    [Theory]
    [InlineData("XIC(Local:1:I.Data.0)OTE(Lamp);")]                  // module I/O tag
    [InlineData("XIC(\\P2.Tag)OTE(Lamp);")]                          // another program's tag
    [InlineData("XIC(Arr[Idx].3)OTE(Lamp);")]                        // array element + bit
    [InlineData("MyAoi(Tmr,Run,Lamp);")]                             // an AOI call
    [InlineData("XIC(RunAlias)OTE(Lamp);")]                          // an alias
    [InlineData("XIC(LocalFlag)OTE(Lamp);")]                         // program-scope tag
    [InlineData("TON(Tmr,?,?)[XIC(Run),XIO(Lamp)]MOVE(0005,Arr[2]);")]  // literals, placeholder, branch
    [InlineData("CPT(Idx,(Arr[0]+16#FF)*2 MOD 7);")]                 // expression
    [InlineData("GSV(Task,MainTask,Rate,Idx);")]                     // class/instance/attribute names
    [InlineData("MAM(Axis1,Ctl,0,100,Units per sec);")]              // motion with keyword operands
    [InlineData("FIND(Str1,Str2,Idx,Pos);")]                         // string instruction
    [InlineData("MID(Str1,Idx,Len,Str2);")]                          // string instruction (not in the M[ACDGRS] family)
    [InlineData("CONCAT(Str1,Str2,Str3);")]
    [InlineData("DELETE(Str1,Idx,Len,Str2);")]
    [InlineData("INSERT(Str1,Str2,Idx,Str3);")]
    [InlineData("UPPER(Str1,Str2);")]
    [InlineData("LOWER(Str1,Str2);")]
    [InlineData("DTOS(Idx,Str1);")]
    [InlineData("RTOS(Idx,Str1);")]
    [InlineData("STOD(Str1,Idx);")]
    [InlineData("STOR(Str1,Idx);")]
    [InlineData("PDET(Phase1);")]                                    // phase instruction
    public async Task NoFalseRefusal_ForValidReferences(string rung)
    {
        var (session, fake) = await OpenAsync();

        var result = await StageAsync(session, rung);

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", ok.Status);
        Assert.Single(fake.CapturedImportOptions);
    }

    [Fact]
    public async Task NoFalseRefusal_ForTagCreatedInTheSameCall()
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session,
            rungs: new[] { new RungInput("XIC(Run)OTE(BrandNewTag);") },
            tags: new[] { new TagInput("BrandNewTag", "BOOL") });

        Assert.IsType<ProposeRungEditResult>(result);
        Assert.Single(fake.CapturedImportOptions);
    }

    [Fact]
    public async Task NoFalseRefusal_ForTagStagedEarlierInTheSession()
    {
        var (session, _) = await OpenAsync();
        session.StagedTagBuffer["EarlierTag"] = new System.Xml.Linq.XElement(
            "Tag", new System.Xml.Linq.XAttribute("Name", "EarlierTag"));

        Assert.IsType<ProposeRungEditResult>(await StageAsync(session, "XIC(EarlierTag)OTE(Lamp);"));
    }

    [Fact]
    public async Task UnresolvableReferences_AreLetThroughWithAWarning_NeverRefused()
    {
        var (session, fake) = await OpenAsync();
        fake.ExportContentByXPath.Remove("Controller/Tags");
        fake.ExportContentByXPath["Controller/Tags"] = "<RSLogix5000Content><Controller/></RSLogix5000Content>";
        fake.ExportContentByXPath["Controller/AddOnInstructionDefinitions"] = "<not xml";

        var result = await StageAsync(session, "XIC(Unreadable)OTE(Lamp);MyOtherAoi(x);");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(ok.Warnings);
        Assert.Contains(ok.Warnings!, w => w.Contains("Unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UndefinedTag_IsAlsoRefusedOnPublish()
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(NoSuchTag)OTE(Lamp);"), session, target: "controller");

        Assert.Equal("UndefinedTag", Assert.IsType<ProposeRungEditError>(result).Kind);
        Assert.Empty(fake.CapturedImportOptions);
    }

    [Fact]
    public async Task BuildVerify_ReportsPassedExcludingStaged_WithANote_WhenStagedRungsAreUnverified()
    {
        var (session, _) = await OpenAsync();

        var ok = Assert.IsType<ProposeRungEditResult>(await StageAsync(session, "XIC(Run)OTE(Lamp);"));

        Assert.Equal("passed-excluding-staged", ok.BuildVerify);
        Assert.NotNull(ok.Note);
        Assert.Contains("passed-excluding-staged", ok.Note!);
    }

    // ── checker unit tests ───────────────────────────────────────────────

    private static ReferenceSymbols Symbols(params string[] controller) => new(
        "MainProgram",
        new HashSet<string>(controller, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["P2"] = new HashSet<string>(["Tag"], StringComparer.OrdinalIgnoreCase),
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(["MyAoi"], StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData("XIC(a)OTE(b);", null)]
    [InlineData("xic(A)ote(B);", null)]
    [InlineData("XIC(Missing)OTE(b);", "UndefinedTag")]
    [InlineData("XIC(\\P2.Tag)OTE(b);", null)]
    [InlineData("XIC(\\P2.Nope)OTE(b);", "UndefinedTag")]
    [InlineData("XIC(\\Unknown.Tag)OTE(b);", null)]                  // unknown program: unresolved, not refused
    [InlineData("XIC(Rack1:2:I.Data[3].1)OTE(b);", null)]
    [InlineData("XIC(arr[idx].3)OTE(b);", null)]
    [InlineData("XIC(arr[Missing].3)OTE(b);", "UndefinedTag")]       // the index is a tag reference too
    [InlineData("MOVE(1.5e-3,a)MOVE(-5,a)MOVE(2#0101,a)MOVE('text',a);", null)]
    [InlineData("CMP(a > 5 AND NOT b)CPT(a,SQRT(a)+ABS(b));", null)]
    [InlineData("JMP(Skip)LBL(Skip)JSR(Sub,0);", null)]              // label and routine names are not tags
    [InlineData("MyAoi(a,b,c);", null)]
    [InlineData("MyAoi(a,Missing,c);", "UndefinedTag")]
    [InlineData("XYZ(a);", null)]                                    // unfamiliar name: warning, not refusal
    [InlineData("XIC(a)[XIC(b),QQQ(c)]OTE(a);", null)]
    [InlineData("MSO(Axis1,Ctl)MAXYZ(Axis1,Ctl)DCS(a,b);", null)]    // motion and safety families
    public void Check_ClassifiesReferences(string rung, string? expectedKind)
    {
        var result = RungReferenceChecker.Check([rung], Symbols("a", "b", "c", "Arr", "Idx"));

        Assert.Equal(expectedKind, result.Kind);
    }

    [Fact]
    public void Check_WithUnreadableControllerTags_NeverRefusesATag()
    {
        var symbols = Symbols("a") with { ControllerTags = null };

        var result = RungReferenceChecker.Check(["XIC(Missing)OTE(a);"], symbols);

        Assert.False(result.Refused);
        Assert.Contains(result.Warnings, w => w.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_WithUnreadableAoiList_NeverRefusesAnInstruction()
    {
        var symbols = Symbols("a") with { AoiNames = null };

        var result = RungReferenceChecker.Check(["SomethingCustom(a);"], symbols);

        Assert.False(result.Refused);
        Assert.Contains(result.Warnings, w => w.Contains("SomethingCustom", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_UnparseableRung_IsLetThroughWithAWarning()
    {
        var result = RungReferenceChecker.Check(["XIC(a"], Symbols("a"));

        Assert.False(result.Refused);
        Assert.Single(result.Warnings);
    }
}
#endif
