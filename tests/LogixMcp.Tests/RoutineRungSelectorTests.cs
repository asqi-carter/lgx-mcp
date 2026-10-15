using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// N1: pre-check, mnemonic check, census, copy check and write scan select only the
/// target routine's <c>RLLContent/Rung</c>; an Add-On Instruction's own <c>Logic</c> (In1/Out1) never counts.
/// </summary>
public class RoutineRungSelectorTests
{
    private const string RoutineXPath = "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    // A routine export: the AOI definition (rungs using its own In1/Out1 parameters) precedes the programs.
    private const string ExportWithAoi =
        "<RSLogix5000Content><Controller Name=\"C\">" +
        "<AddOnInstructionDefinitions><AddOnInstructionDefinition Name=\"MotorCtl\"><Routines>" +
        "<Routine Name=\"Logic\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(In1)OTE(Out1);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></AddOnInstructionDefinition></AddOnInstructionDefinitions>" +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"Other\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Other_Tag)OTE(Other_Out);]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Cmd_MotorStart)NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    [Fact]
    public void AoiLogicWithIn1Out1_AllowsPlainRungStage()
    {
        var texts = MnemonicSetChecker.RungTextsFromFragment(ExportWithAoi, RoutineXPath);
        Assert.Equal(["XIC(Cmd_MotorStart)NOP();"], texts);

        var symbols = new ReferenceSymbols(
            "MainProgram",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cmd_MotorStart" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MotorCtl" });
        Assert.False(RungReferenceChecker.Check(texts, symbols).Refused);

        // Sanity: scanning the AOI's own rung as a routine rung is exactly what used to refuse the stage.
        Assert.True(RungReferenceChecker.Check(["XIC(In1)OTE(Out1);"], symbols).Refused);
    }

    [Fact]
    public void Select_ReturnsOnlyTargetRoutineRungs_AndCountMatchesRoutine()
    {
        var doc = SafeXml.ParseDocument(ExportWithAoi);

        var rungs = RoutineRungSelector.Select(doc, RoutineXPath);

        var rung = Assert.Single(rungs);
        Assert.Equal("XIC(Cmd_MotorStart)NOP();", rung.Element("Text")!.Value);
        var routine = doc.Descendants("Program").SelectMany(p => p.Elements("Routines").Elements("Routine"))
            .Single(r => (string?)r.Attribute("Name") == "MainRoutine");
        Assert.Equal(routine.Element("RLLContent")!.Elements("Rung").Count(), rungs.Count);
    }

    [Fact]
    public void Select_NameMatchIgnoresCase_AndNoRoutineFallsBackToNonAoiRungs()
    {
        var doc = SafeXml.ParseDocument(ExportWithAoi);

        Assert.Single(RoutineRungSelector.Select(doc,
            "Controller/Programs/Program[@Name='mainprogram']/Routines/Routine[@Name='MAINROUTINE']"));

        // Routine not in the document: AOI logic is still excluded.
        var fallback = RoutineRungSelector.Select(doc,
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='Missing']");
        Assert.Equal(2, fallback.Count);
        Assert.DoesNotContain(fallback, r => r.Element("Text")!.Value.Contains("In1"));
    }

    [Fact]
    public void WriteScan_IgnoresAoiLogicWrites()
    {
        var scan = WriteClassifier.ScanFragment(ExportWithAoi, RoutineXPath);

        Assert.DoesNotContain(scan.ProgramWritten.Keys.Concat(scan.UnknownContext.Keys), k =>
            k.Equals("Out1", StringComparison.OrdinalIgnoreCase));
    }

#if LOGIX_SDK
    [Fact]
    public void DedupeCopyCheck_CountsOnlyTargetRoutineRungs()
    {
        var staged = new Dictionary<(string RoutineXPath, uint InsertPosition), List<string>>
        {
            [(RoutineXPath, 0u)] = ["XIC(Cmd_MotorStart)NOP();"],
        };

        var result = ProposeRungEditHandler.DedupeFragmentRungsAgainstStagedBuffer(
            ExportWithAoi, RoutineXPath, 0, staged, out var fragmentRungCount, out var dropped);

        Assert.Equal(1, fragmentRungCount);
        Assert.Equal(1, dropped);
        Assert.Contains("XIC(In1)OTE(Out1);", result); // the AOI definition itself is left untouched
        Assert.DoesNotContain("Cmd_MotorStart", result);
    }
#endif
}
