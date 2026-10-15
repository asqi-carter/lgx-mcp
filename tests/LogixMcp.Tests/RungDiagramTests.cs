using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <c>logix_get_rung_diagram</c> (<see cref="RungDiagramHandler"/>, <see cref="RungDiagram"/>,
/// <see cref="RungDiagramSvg"/>). The golden text lives in <c>fixtures/rung-diagram/*.txt</c>; every
/// golden file was read and checked by eye when it was written, never snapshotted blindly.
/// </summary>
public class RungDiagramTests
{
    private const string Program = "Line_A";

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string RungDiagramFixture => Path.Combine(FixtureDir, "real-shape", "RungDiagram.L5X");

    private static XElement LoadRoot(string path) => XDocument.Load(path).Root!;

    private static RungDiagramResult Draw(string routine, string rung, string format = "text")
    {
        var result = RungDiagramHandler.Execute(
            LoadRoot(RungDiagramFixture), RungDiagramHandler.SourceProjectFile, Program, routine, rung, format);
        return Assert.IsType<RungDiagramResult>(result);
    }

    private static RungDiagramError Fail(string routine, string rung, string? format = "text", string program = Program)
    {
        var result = RungDiagramHandler.Execute(
            LoadRoot(RungDiagramFixture), RungDiagramHandler.SourceProjectFile, program, routine, rung, format);
        return Assert.IsType<RungDiagramError>(result);
    }

    /// <summary>Minimal one-rung L5X, for shapes the fixture does not carry.</summary>
    private static XElement OneRung(string neutralText, string? comment = null, string type = "N")
    {
        var rung = new XElement("Rung", new XAttribute("Number", 0), new XAttribute("Type", type));
        if (comment is not null) rung.Add(new XElement("Comment", new XCData(comment)));
        rung.Add(new XElement("Text", new XCData(neutralText)));
        return new XElement("RSLogix5000Content",
            new XElement("Controller", new XAttribute("Name", "C"),
                new XElement("Tags"),
                new XElement("Programs",
                    new XElement("Program", new XAttribute("Name", "P"),
                        new XElement("Tags"),
                        new XElement("Routines",
                            new XElement("Routine", new XAttribute("Name", "R"), new XAttribute("Type", "RLL"),
                                new XElement("RLLContent", rung)))))));
    }

    private static RungDiagramResult DrawText(string neutralText, string? comment = null, string format = "text") =>
        Assert.IsType<RungDiagramResult>(
            RungDiagramHandler.Execute(OneRung(neutralText, comment), "test", "P", "R", "0", format));

    // ── Golden text ──────────────────────────────────────────────────────────

    /// <summary>One golden file per drawn shape: (file, routine, rung spec).</summary>
    public static IEnumerable<object[]> GoldenCases() => new[]
    {
        new object[] { "contacts",            "Main",  "0"   },
        new object[] { "coils",               "Main",  "1-2" },
        new object[] { "branch",              "Main",  "3"   },
        new object[] { "nested-branch",       "Main",  "4"   },
        new object[] { "timer",               "Main",  "5"   },
        new object[] { "cpt-mov-cop",         "Main",  "6"   },
        new object[] { "aoi-call",            "Main",  "7"   },
        new object[] { "one-shot",            "Main",  "8"   },
        new object[] { "long-wrap",           "Main",  "9"   },
        new object[] { "unknown-instruction", "Main",  "10"  },
        new object[] { "compare",             "Main",  "11"  },
        new object[] { "jsr",                 "Main",  "12"  },
        new object[] { "pending-insert",      "Edits", "1"   },
        new object[] { "pending-replace",     "Edits", "2"   },
        new object[] { "pending-delete",      "Edits", "4"   },
        new object[] { "pending-tested",      "Edits", "5-6" },
    };

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void GoldenText_MatchesFixture(string name, string routine, string rung)
    {
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "rung-diagram", name + ".txt"))
            .Replace("\r\n", "\n").TrimEnd('\n');

        var actual = Draw(routine, rung).Text;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GoldenText_FromProjectService_MatchesTheFileDrawing()
    {
        // The file path goes through L5Sharp's serializer; descriptions, comments and the AOI must survive it.
        var service = new ProjectService();
        service.Load(RungDiagramFixture);

        foreach (var (routine, rung) in new[] { ("Main", "0"), ("Main", "6"), ("Main", "7"), ("Edits", "2") })
        {
            var viaService = Assert.IsType<RungDiagramResult>(
                RungDiagramHandler.Execute(service, Program, routine, rung, "text"));

            Assert.Equal(Draw(routine, rung).Text, viaService.Text);
            Assert.Equal(RungDiagramHandler.SourceProjectFile, viaService.Source);
        }
    }

    [Fact]
    public void Contacts_DrawXicXioAndOteSymbols_BetweenPowerRails()
    {
        var text = Draw("Main", "0").Text!;

        Assert.Contains("┤ ├", text);
        Assert.Contains("┤/├", text);
        Assert.Contains("( )", text);
        Assert.Contains("╟", text);
        Assert.Contains("╢", text);
    }

    [Fact]
    public void Coils_DrawOtlAndOtuSymbols()
    {
        var text = Draw("Main", "1-2").Text!;

        Assert.Contains("(L)", text);
        Assert.Contains("(U)", text);
    }

    [Fact]
    public void OneShot_DrawsBracketedSymbol()
    {
        Assert.Contains("[ONS]", Draw("Main", "8").Text!);
    }

    [Fact]
    public void Branch_StacksLegsWithJunctions()
    {
        var text = Draw("Main", "3").Text!;

        Assert.Contains("┬", text);
        Assert.Contains("└", text);
        Assert.Contains("┘", text);
        Assert.Contains("Status_Word.0", text);
    }

    [Fact]
    public void NestedBranch_DrawsInnerLegsInsideTheOuterLeg()
    {
        var text = Draw("Main", "4").Text!;

        Assert.Equal(2, Regex.Matches(text, "└").Count); // outer and inner branch bottoms
        Assert.Contains("Guard_Closed", text);
        Assert.Contains("Status_Word.1", text);
    }

    [Fact]
    public void Blocks_DrawOperandsWithTheirLabels()
    {
        var timer = Draw("Main", "5").Text!;
        Assert.Contains("TON", timer);
        Assert.Contains("Preset 5000", timer);

        var blocks = Draw("Main", "6").Text!;
        Assert.Contains("MOV", blocks);
        Assert.Contains("CPT", blocks);
        Assert.Contains("COP", blocks);
        Assert.Contains("Expression", blocks);
        Assert.Contains("Length 10", blocks);

        var aoi = Draw("Main", "7").Text!;
        Assert.Contains("Valve_Ctl (AOI)", aoi);
        Assert.Contains("OpenCmd", aoi);
        Assert.Contains("OpenFb", aoi);
    }

    [Fact]
    public void Descriptions_AppearAboveTheInstruction_IncludingBitDescriptions()
    {
        var lines = Draw("Main", "3").Text!.Split('\n');
        var wire = Array.FindIndex(lines, l => l.StartsWith('╟'));

        Assert.True(wire > 0);
        var above = string.Join("\n", lines.Take(wire));
        Assert.Contains("Operator start", above);
        Assert.Contains("pushbutton", above);
        // Bit description from <Comments><Comment Operand=".0">, shown under the tag's own description.
        var all = string.Join("\n", lines);
        Assert.Contains(".0: Line", all);
        Assert.Contains("Line status", all);
    }

    [Fact]
    public void UdtMemberDescription_IsShown()
    {
        var root = OneRung("XIC(Seq.Step)OTE(Out);");
        var controller = root.Element("Controller")!;
        controller.Element("Tags")!.Add(XElement.Parse(
            "<Tag Name=\"Seq\" DataType=\"Seq_Type\"><Description>Sequence data</Description></Tag>"));
        controller.AddFirst(XElement.Parse(
            "<DataTypes><DataType Name=\"Seq_Type\"><Members>" +
            "<Member Name=\"Step\" DataType=\"DINT\"><Description>Current step number</Description></Member>" +
            "</Members></DataType></DataTypes>"));

        var result = Assert.IsType<RungDiagramResult>(
            RungDiagramHandler.Execute(root, "test", "P", "R", "0", "text"));

        Assert.Contains(".Step: Current", result.Text);
    }

    [Fact]
    public void RungComment_AppearsAboveTheRung()
    {
        var lines = Draw("Main", "0").Text!.Split('\n');

        Assert.StartsWith("Rung 0", lines[0]);
        Assert.StartsWith("// Start the fill valve", lines[1]);
        Assert.StartsWith("║", lines[2]);
    }

    [Fact]
    public void RungComment_AndDescriptions_AreFlaggedAsUntrustedContent()
    {
        var result = Draw("Main", "0");

        Assert.Contains("rung-comment", result.UntrustedContent!);
        Assert.Contains("tag-description", result.UntrustedContent!);
        Assert.False(string.IsNullOrEmpty(result.Notice));
    }

    [Fact]
    public void RungWithoutAuthoredText_CarriesNoUntrustedNotice()
    {
        var result = DrawText("XIC(A)OTE(B);");

        Assert.Null(result.UntrustedContent);
        Assert.Null(result.Notice);
    }

    [Fact]
    public void RungComment_WithTabsAndRaggedSpacing_IsCollapsed()
    {
        var result = DrawText("XIC(A)OTE(B);", comment: "line\tone   spaced\r\nline two");

        Assert.DoesNotContain('\t', result.Text!);
        Assert.Contains("// line one spaced", result.Text);
        Assert.Contains("// line two", result.Text);
    }

    // ── Width and wrapping ───────────────────────────────────────────────────

    [Fact]
    public void EveryFixtureRung_StaysWithin120Characters()
    {
        foreach (var routine in new[] { "Main", "Edits", "Fill_Seq" })
        {
            var text = Draw(routine, "0-9").Text!;

            Assert.All(text.Split('\n'), line => Assert.True(line.Length <= RungDiagram.MaxWidth,
                $"{routine}: {line.Length} characters: {line}"));
        }
    }

    [Fact]
    public void LongRung_WrapsOntoContinuationRows_MarkedWithEllipsis()
    {
        var result = Draw("Main", "9");
        var lines = result.Text!.Split('\n');

        Assert.True(result.Rungs[0].Wrapped);
        Assert.Contains(lines, l => l.StartsWith('╟') && l.EndsWith('…'));
        Assert.Contains(lines, l => l.StartsWith('…') && l.EndsWith('╢'));
        Assert.All(lines, l => Assert.True(l.Length <= RungDiagram.MaxWidth, l));
    }

    [Fact]
    public void LongRung_LosesNoInstruction()
    {
        var text = Draw("Main", "9").Text!;

        Assert.Contains("Local_Flag", text);
        for (var i = 1; i <= 12; i++) Assert.Contains($"Permissive_{i:00}", text);
        Assert.Contains("Run_Out", text);
        Assert.Equal(13, Regex.Matches(text, "┤ ├").Count);
    }

    [Fact]
    public void ShortRung_IsNotMarkedWrapped()
    {
        var result = Draw("Main", "0");

        Assert.False(result.Rungs[0].Wrapped);
        Assert.DoesNotContain('…', result.Text!);
    }

    [Fact]
    public void WideBranch_WrapsInsideTheLeg_WithinTheLimit()
    {
        var legs = string.Join(",", Enumerable.Range(0, 3).Select(l =>
            string.Concat(Enumerable.Range(0, 14).Select(i => $"XIC(Leg{l}_Contact_{i:00})"))));

        var result = DrawText($"[{legs}]OTE(Out);");

        Assert.All(result.Text!.Split('\n'), l => Assert.True(l.Length <= RungDiagram.MaxWidth, l));
        Assert.Contains('…', result.Text);
        for (var l = 0; l < 3; l++)
            for (var i = 0; i < 14; i++)
                Assert.Contains($"Leg{l}_Contact_{i:00}", result.Text);
    }

    [Fact]
    public void VeryLongOperand_IsWrappedNeverTruncated()
    {
        var expression = string.Join("+", Enumerable.Range(0, 40).Select(i => $"Value_{i:00}"));

        var result = DrawText($"XIC(A)CPT(Total,{expression});");

        Assert.All(result.Text!.Split('\n'), l => Assert.True(l.Length <= RungDiagram.MaxWidth, l));
        var squashed = Regex.Replace(result.Text!, @"[\s║│┌┐└┘─┤├╟╢…]", "");
        for (var i = 0; i < 40; i++) Assert.Contains($"Value_{i:00}", squashed);
    }

    [Fact]
    public void VeryLongDescription_IsWrappedWithinTheLimit()
    {
        var root = OneRung("XIC(Big)OTE(Out);");
        root.Element("Controller")!.Element("Tags")!.Add(XElement.Parse(
            $"<Tag Name=\"Big\" DataType=\"BOOL\"><Description>{string.Join(' ', Enumerable.Repeat("pressure", 80))}</Description></Tag>"));

        var result = Assert.IsType<RungDiagramResult>(
            RungDiagramHandler.Execute(root, "test", "P", "R", "0", "text"));

        Assert.All(result.Text!.Split('\n'), l => Assert.True(l.Length <= RungDiagram.MaxWidth, l));
        Assert.Equal(80, Regex.Matches(result.Text!, "pressure").Count);
    }

    // ── Unknown and unparseable ──────────────────────────────────────────────

    [Fact]
    public void UnknownInstruction_IsDrawnAsABlockWithItsArguments()
    {
        var text = Draw("Main", "10").Text!;

        Assert.Contains("NewVendorBlock", text);
        Assert.Contains("Arg 1", text);
        Assert.Contains("Arg 2 42", text);
        Assert.Contains("┌", text);
    }

    [Theory]
    [InlineData("XIC(A")]
    [InlineData("XIC(A)[XIC(B),XIC(C)OTE(D);")]
    [InlineData("]]]XIC(A);")]
    [InlineData("%%% not a rung")]
    public void UnparseableRungText_IsShownRawWithANote_NotDropped(string neutral)
    {
        var result = DrawText(neutral);

        Assert.Equal("raw", result.Rungs[0].Drawn);
        Assert.Contains(neutral.Replace(" ", ""), result.Text!.Replace(" ", "").Replace("\n", ""));
        Assert.Contains("[", result.Text);
    }

    [Fact]
    public void EmptyRung_DrawsJustThePowerRails()
    {
        var result = DrawText(";");

        Assert.Equal("ladder", result.Rungs[0].Drawn);
        Assert.Contains("╟", result.Text);
        Assert.Contains("╢", result.Text);
    }

    [Fact]
    public void DeeplyNestedBranches_DoNotThrow_AndStayWithinTheLimit()
    {
        var neutral = "XIC(Leaf)";
        for (var i = 0; i < 30; i++) neutral = $"[{neutral},XIC(Alt{i})]";

        var result = DrawText(neutral + "OTE(Out);");

        Assert.All(result.Text!.Split('\n'), l => Assert.True(l.Length <= RungDiagram.MaxWidth, l));
        Assert.Contains("Leaf", result.Text);
    }

    [Fact]
    public void CrossProgramReference_IsDrawn()
    {
        var result = DrawText("XIC(\\Other.Shared_Tag)OTE(B);");

        Assert.Equal("ladder", result.Rungs[0].Drawn);
        Assert.Contains("Shared_Tag", result.Text);
    }

    // ── Pending edits ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("I", "pending insert")]
    [InlineData("D", "pending delete")]
    [InlineData("R", "pending replace: old rung")]
    [InlineData("IR", "pending replace: new rung")]
    [InlineData("dN", "pending delete, tested")]
        [InlineData("rI", "accepted insert, awaiting assembly")]
    [InlineData("rR", "accepted replace, awaiting assembly")]
    [InlineData("Zq", "pending edit, type Zq")]
    public void PendingEditTypes_AreLabelled(string code, string label)
    {
        var result = Assert.IsType<RungDiagramResult>(RungDiagramHandler.Execute(
            OneRung("XIC(A)OTE(B);", type: code), "test", "P", "R", "0", "text"));

        Assert.Equal(label, result.Rungs[0].Edit);
        Assert.Contains($"[{label}]", result.Text);
        Assert.Equal(code, result.Rungs[0].Type);
    }

    [Fact]
    public void NormalRung_HasNoEditLabel()
    {
        var result = Draw("Edits", "0");

        Assert.Null(result.Rungs[0].Edit);
        Assert.DoesNotContain("pending", result.Text!);
    }

    [Fact]
    public void PendingInsert_IsLabelledUntested()
    {
        var text = Draw("Edits", "1").Text!;

        Assert.Contains("[pending insert]", text);
    }

    [Fact]
    public void PendingDelete_IsLabelledAndStillDrawn()
    {
        var text = Draw("Edits", "4").Text!;

        Assert.Contains("[pending delete]", text);
        Assert.Contains("Drain_Valve", text);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    public void PendingReplace_DrawsBothTheOldAndTheNewRung_WhicheverHalfIsAsked(string rung)
    {
        var result = Draw("Edits", rung);

        Assert.Equal(2, result.Rungs.Count);
        Assert.Contains("[pending replace: old rung]", result.Text);
        Assert.Contains("[pending replace: new rung]", result.Text);
        Assert.Contains("// Old reset rung.", result.Text);
        Assert.Contains("// New reset rung.", result.Text);
    }

    [Fact]
    public void TestedReplace_DrawsTheOldRungToo_AndLabelsTheNewOne()
    {
        var root = XElement.Parse(
            "<RSLogix5000Content><Controller><Programs><Program Name=\"P\"><Routines><Routine Name=\"R\" Type=\"RLL\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"R\"><Text>XIC(A)OTE(B);</Text></Rung>" +
            "<Rung Number=\"1\" Type=\"er\"><Text>XIC(A)XIC(C)OTE(B);</Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");

        var result = Assert.IsType<RungDiagramResult>(RungDiagramHandler.Execute(root, "test", "P", "R", "1", "text"));

        Assert.Equal(2, result.Rungs.Count);
        Assert.Equal("staged (not sent to the controller)", result.Rungs[1].Edit);
    }

    [Fact]
    public void RungDiagram_LabelsErRungAsStagedReplacementWorkstation()
    {
        var result = Assert.IsType<RungDiagramResult>(RungDiagramHandler.Execute(
            OneRung("XIC(A)OTE(B);", type: "er"), "test", "P", "R", "0", "text"));

        Assert.Equal("staged (not sent to the controller)", result.Rungs[0].Edit);
        Assert.Contains("[staged (not sent to the controller)]", result.Text);
        Assert.DoesNotContain("pending replace, tested", result.Text!);
    }

    [Fact]
    public void PendingTestedInsertAndDelete_AreLabelledTested()
    {
        var text = Draw("Edits", "5-6").Text!;

        Assert.Contains("[staged (not sent to the controller)]", text);
        Assert.Contains("[pending delete, tested]", text);
    }

    [Fact]
    public void StagedBufferRungs_AreLabelledStagedLocally()
    {
        var result = Assert.IsType<RungDiagramResult>(Draw("Edits", "5"));

        Assert.Equal("staged (not sent to the controller)", result.Rungs[0].Edit);
        Assert.Contains("[staged (not sent to the controller)]", result.Text);
        Assert.DoesNotContain("pending insert, tested", result.Text);
    }

    // ── Not ladder ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Calc", "ST")]
    [InlineData("Blocks", "FBD")]
    [InlineData("Steps", "SFC")]
    public void NonLadderRoutines_ReturnNotLadderWithTheRoutineType(string routine, string type)
    {
        var error = Fail(routine, "0");

        Assert.Equal("NotLadder", error.Kind);
        Assert.Equal(type, error.RoutineType);
        Assert.Contains(type, error.Error);
    }

    [Fact]
    public void NotLadder_Json_CarriesKindAndRoutineType()
    {
        var service = new ProjectService();
        service.Load(RungDiagramFixture);
        var tools = new LogixProjectTools(service);

        using var doc = JsonDocument.Parse(tools.GetGetRungDiagramResponse(Program, "Calc", "0"));

        Assert.Equal("NotLadder", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("ST", doc.RootElement.GetProperty("routineType").GetString());
    }

    [Fact]
    public void EncodedRoutine_IsRefused()
    {
        var root = LoadRoot(Path.Combine(FixtureDir, "real-shape", "EncodedRoutine.L5X"));

        var error = Assert.IsType<RungDiagramError>(
            RungDiagramHandler.Execute(root, "test", "Line_A", "Locked_Logic", "0", "text"));

        Assert.Contains("encoded", error.Error);
    }

    // ── Inputs ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3", 3, 3)]
    [InlineData("3-7", 3, 7)]
    [InlineData("3..7", 3, 7)]
    [InlineData(" 0 - 9 ", 0, 9)]
    public void RungSpec_AcceptsANumberOrARange(string spec, int first, int last)
    {
        Assert.True(RungDiagramHandler.TryParseRange(spec, out var a, out var b, out var error));
        Assert.Equal((first, last), (a, b));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1,2")]
    [InlineData("7-3")]
    [InlineData("0-10")]
    public void RungSpec_RejectsJunkBackwardsAndMoreThanTenRungs(string spec)
    {
        Assert.False(RungDiagramHandler.TryParseRange(spec, out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void RangeOfEleven_ReturnsInvalidValueError()
    {
        var error = Fail("Main", "0-10");

        Assert.Equal("InvalidValue", error.Kind);
        Assert.Contains("at most 10", error.Error);
    }

    [Fact]
    public void RangeOfTen_IsDrawn()
    {
        var result = Draw("Main", "0-9");

        Assert.Equal(10, result.Rungs.Count);
        Assert.Equal(Enumerable.Range(0, 10), result.Rungs.Select(r => r.Number));
    }

    [Fact]
    public void UnknownFormat_ReturnsInvalidValueError()
    {
        var error = Fail("Main", "0", "png");

        Assert.Equal("InvalidValue", error.Kind);
        Assert.Contains("png", error.Error);
    }

    [Theory]
    [InlineData("text", true, false)]
    [InlineData("svg", false, true)]
    [InlineData("both", true, true)]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("TEXT", true, false)]
    public void Format_SelectsTheOutputs(string? format, bool hasText, bool hasSvg)
    {
        var result = Assert.IsType<RungDiagramResult>(RungDiagramHandler.Execute(
            LoadRoot(RungDiagramFixture), "test", Program, "Main", "0", format));

        Assert.Equal(hasText, result.Text is not null);
        Assert.Equal(hasSvg, result.Svg is not null);
    }

    [Fact]
    public void UnknownProgram_ListsTheAvailableOnes()
    {
        var error = Fail("Main", "0", program: "Nope");

        Assert.Contains("'Nope' not found", error.Error);
        Assert.Contains(Program, error.Error);
    }

    [Fact]
    public void UnknownRoutine_ListsTheAvailableOnes()
    {
        var error = Fail("Nope", "0");

        Assert.Contains("'Nope' not found", error.Error);
        Assert.Contains("Main", error.Error);
    }

    [Fact]
    public void UnknownRung_SaysWhichRungsExist()
    {
        var error = Fail("Main", "99");

        Assert.Contains("Rung 99 not found", error.Error);
        Assert.Contains("0 to 12", error.Error);
    }

    [Fact]
    public void UnknownRung_HintNamesPendingEditsThenSessionImageGetRoutine()
    {
        var error = Fail("Main", "99");

        Assert.NotNull(error.Hint);
        var pending = error.Hint!.IndexOf("logix_pending_edits", StringComparison.Ordinal);
        var getRoutine = error.Hint.IndexOf("logix_get_routine", StringComparison.Ordinal);
        Assert.True(pending >= 0 && getRoutine > pending);
        Assert.Contains("source:\"session-image\"", error.Hint);
    }

    [Fact]
    public void ProgramAndRoutineNames_AreCaseInsensitive()
    {
        var result = Assert.IsType<RungDiagramResult>(RungDiagramHandler.Execute(
            LoadRoot(RungDiagramFixture), "test", "line_a", "MAIN", "0", "text"));

        Assert.Equal(Program, result.Program);
        Assert.Equal("Main", result.Routine);
    }

    [Fact]
    public void NoProjectLoaded_ReturnsAnError()
    {
        var result = RungDiagramHandler.Execute(new ProjectService(), Program, "Main", "0", "text");

        Assert.Contains("No project loaded", Assert.IsType<RungDiagramError>(result).Error);
    }

    // ── SVG ──────────────────────────────────────────────────────────────────

    private static readonly string[] SvgFixtureRungs = { "0", "3", "4", "5", "6", "7", "9" };

    [Theory]
    [InlineData("Main", "0")]
    [InlineData("Main", "4")]
    [InlineData("Main", "6")]
    [InlineData("Main", "9")]
    [InlineData("Edits", "2")]
    public void Svg_IsWellFormedXml_WithAnSvgRoot(string routine, string rung)
    {
        var svg = Draw(routine, rung, "svg").Svg!;

        var doc = XDocument.Parse(svg);

        Assert.Equal("svg", doc.Root!.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", doc.Root.Name.NamespaceName);
        Assert.NotNull(doc.Root.Attribute("viewBox"));
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "title");
    }

    [Fact]
    public void Svg_UsesNoLiteralColours_OnlyCurrentColorAndNone()
    {
        foreach (var rung in SvgFixtureRungs)
        {
            var svg = Draw("Main", rung, "svg").Svg!;
            var doc = XDocument.Parse(svg);

            var paints = doc.Descendants().SelectMany(e => e.Attributes())
                .Where(a => a.Name.LocalName is "fill" or "stroke" or "color" or "stop-color" or "flood-color")
                .Select(a => a.Value)
                .ToList();

            Assert.Contains("currentColor", paints);
            Assert.All(paints, p => Assert.True(p is "currentColor" or "none", $"rung {rung}: literal paint '{p}'"));
            Assert.DoesNotContain(doc.Descendants().SelectMany(e => e.Attributes()), a => a.Name.LocalName == "style");
            Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|rgb\(|rgba\(|hsl\(", svg);
        }
    }

    [Fact]
    public void Svg_CarriesTheSameText_AsTheTextDrawing()
    {
        var both = Draw("Main", "0", "both");
        var doc = XDocument.Parse(both.Svg!);

        var svgText = string.Concat(doc.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value));

        foreach (var word in new[] { "Cmd_Start", "Cmd_Stop", "Guard_Closed", "Fill_Valve", "pushbutton" })
        {
            Assert.Contains(word, both.Text);
            Assert.Contains(word, svgText);
        }
    }

    [Fact]
    public void Svg_EscapesMarkupInProjectText()
    {
        var result = DrawText("XIC(A)OTE(B);", comment: "a <b>&</b> \"quoted\" ]]> end", format: "svg");

        var doc = XDocument.Parse(result.Svg!);

        Assert.DoesNotContain("<b>", result.Svg);
        Assert.Contains("a <b>&</b>", string.Concat(doc.Descendants().Select(e => e.Value)));
    }

    [Fact]
    public void Svg_ForAWrappedRung_StaysWellFormed()
    {
        var doc = XDocument.Parse(Draw("Main", "9", "svg").Svg!);

        Assert.Contains("…", string.Concat(doc.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value))
                              + string.Concat(doc.Descendants().Select(e => e.Value)));
    }

    // ── Real-shape export fixtures ───────────────────────────────────────────

    [Fact]
    public void RealShapeFixture_CarriesTheHeaderAndNoCustomerNames()
    {
        var raw = File.ReadAllText(RungDiagramFixture);

        Assert.Contains("Structure copied from a real Studio 5000 export; all names invented.", raw);
        Assert.Contains("TargetName=\"Line_A\"", raw); // invented program name, not a site name
    }

    [Theory]
    [InlineData("ProgramExport.L5X", "Main")]
    [InlineData("RoutineExport.L5X", "Fill_Seq")]
    public void RealShapeExports_DrawFromProgramAndRoutineExports(string file, string routine)
    {
        var root = LoadRoot(Path.Combine(FixtureDir, "real-shape", file));

        var result = Assert.IsType<RungDiagramResult>(RungDiagramHandler.Execute(
            root, "test", Program, routine, "0", "both"));

        Assert.Contains("┤ ├", result.Text);
        Assert.Contains("Fill_Valve", result.Text);
        Assert.Contains("<svg", result.Svg);
    }

    [Fact]
    public void RealShapeFixture_DrawsEveryLadderRungInEveryRoutine()
    {
        var root = LoadRoot(RungDiagramFixture);
        var ladder = root.Descendants("Routine").Where(r => (string?)r.Attribute("Type") == "RLL");

        foreach (var routine in ladder)
        {
            var name = (string)routine.Attribute("Name")!;
            var numbers = routine.Descendants("Rung").Select(r => (int)r.Attribute("Number")!).ToList();
            for (var first = numbers.Min(); first <= numbers.Max(); first += 10)
            {
                var result = Draw(name, $"{first}-{Math.Min(first + 9, numbers.Max())}");

                Assert.All(result.Rungs, r => Assert.Equal("ladder", r.Drawn));
                Assert.All(result.Text!.Split('\n'), l => Assert.True(l.Length <= RungDiagram.MaxWidth, l));
            }
        }
    }

    // ── Parser reuse ─────────────────────────────────────────────────────────

    [Fact]
    public void Parser_ReusesTheL5SharpNeutralTextTokenizer()
    {
        var source = File.ReadAllText(FindRepoFile("src", "LogixMcp", "RungDiagram.cs"));

        // The tokenizer is reached only through the shared helper, which owns the NeutralText call.
        Assert.Contains("RungParser.Tokenize(", source);
        Assert.DoesNotContain("new NeutralText(", source);
        var helper = File.ReadAllText(FindRepoFile("src", "LogixMcp", "RungParser.cs"));
        Assert.Contains("NeutralText", helper);
        Assert.Contains(".Tokenize()", helper);
    }

    [Fact]
    public void RungStructure_GroupsInstructionsAndNestedBranches()
    {
        Assert.True(RungStructure.TryParse("XIC(A)[XIC(B),[XIC(C)XIO(D),OTL(E)]]OTE(F);", out var nodes, out var error), error);

        Assert.Equal(3, nodes.Count);
        var branch = Assert.IsType<BranchNode>(nodes[1]);
        Assert.Equal(2, branch.Legs.Count);
        var inner = Assert.IsType<BranchNode>(Assert.Single(branch.Legs[1]));
        Assert.Equal(2, inner.Legs.Count);
        Assert.Equal(2, inner.Legs[0].Count);
    }

    // ── SDK-session path ─────────────────────────────────────────────────────

    private sealed class FakeRoutineSource : IRoutineL5xSource
    {
        private readonly Func<string> _l5x;
        public FakeRoutineSource(Func<string> l5x) => _l5x = l5x;
        public Task<string> ExportRoutineL5xAsync(string program, string routine, CancellationToken ct = default) =>
            Task.FromResult(_l5x());
    }

    [Fact]
    public async Task SdkSession_DrawsTheExport_WithPendingEdits_AndSaysSo()
    {
        var source = new FakeRoutineSource(() => File.ReadAllText(RungDiagramFixture));

        var result = Assert.IsType<RungDiagramResult>(
            await RungDiagramHandler.ExecuteAsync(source, Program, "Edits", "2", "text"));

        Assert.Equal(RungDiagramHandler.SourceSdkSession, result.Source);
        Assert.Contains("[pending replace: old rung]", result.Text);
        Assert.Contains("[pending replace: new rung]", result.Text);
    }

    [Fact]
    public async Task SdkSession_ExportFailure_ReturnsAnErrorObject()
    {
        var source = new FakeRoutineSource(() => throw new InvalidOperationException("Export failed: nope"));

        var error = Assert.IsType<RungDiagramError>(
            await RungDiagramHandler.ExecuteAsync(source, Program, "Main", "0", "text"));

        Assert.Contains("nope", error.Error);
    }

    [Fact]
    public async Task SdkSession_UnreadableExport_ReturnsAnErrorObject()
    {
        var source = new FakeRoutineSource(() => "not xml at all");

        var error = Assert.IsType<RungDiagramError>(
            await RungDiagramHandler.ExecuteAsync(source, Program, "Main", "0", "text"));

        Assert.Contains("not readable L5X", error.Error);
    }

    [Fact]
    public async Task SdkSession_BadRung_IsRejectedBeforeTheExport()
    {
        var exported = false;
        var source = new FakeRoutineSource(() => { exported = true; return ""; });

        var error = Assert.IsType<RungDiagramError>(
            await RungDiagramHandler.ExecuteAsync(source, Program, "Main", "0-20", "text"));

        Assert.Equal("InvalidValue", error.Kind);
        Assert.False(exported);
    }

    // ── MCP tool ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tool_IsRegistered_WithTheDocumentedParameters()
    {
        var method = typeof(LogixProjectTools).GetMethod(nameof(LogixProjectTools.GetRungDiagram))!;

        Assert.Equal("logix_get_rung_diagram", method.GetCustomAttribute<McpServerToolAttribute>()!.Name);
        Assert.Equal(new[] { "program", "routine", "rung", "format", "ct" },
            method.GetParameters().Select(p => p.Name).ToArray());
        Assert.Equal("text", method.GetParameters().Single(p => p.Name == "format").DefaultValue);
    }

    [Fact]
    public void Tool_Description_SaysFileSourceNotLiveController_AndMentionsNotLadder()
    {
        var method = typeof(LogixProjectTools).GetMethod(nameof(LogixProjectTools.GetRungDiagram))!;
        var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;

        Assert.Contains("project file", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never the live controller", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NotLadder", description);
        Assert.Contains("120", description);
    }

    [Fact]
    public async Task Tool_WithoutASession_DrawsFromTheProjectFile()
    {
        var service = new ProjectService();
        service.Load(RungDiagramFixture);
        var tools = new LogixProjectTools(service);

        using var doc = JsonDocument.Parse(await tools.GetRungDiagram(Program, "Main", "0", "both"));
        var root = doc.RootElement;

        Assert.Equal("project-file", root.GetProperty("source").GetString());
        Assert.Equal("Main", root.GetProperty("routine").GetString());
        Assert.Contains("┤ ├", root.GetProperty("text").GetString());
        Assert.StartsWith("<svg", root.GetProperty("svg").GetString());
        Assert.Equal("rung-comment", root.GetProperty("untrustedContent")[0].GetString());
        Assert.True(root.TryGetProperty("notice", out _));
    }

    [Fact]
    public async Task Tool_WithAnSdkBackedSession_DrawsTheSessionExport()
    {
        var service = new ProjectService();
        var sessions = new TagSessionService();
        sessions.Attach(new SessionStubTagService(File.ReadAllText(RungDiagramFixture)), isVirtual: false);
        var tools = new LogixProjectTools(service, sessions);

        using var doc = JsonDocument.Parse(await tools.GetRungDiagram(Program, "Edits", "1"));

        Assert.Equal("sdk-session", doc.RootElement.GetProperty("source").GetString());
        Assert.Contains("[pending insert]", doc.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("true")]
    public async Task GetRungDiagram_IntegerRung_IsAcceptedOrInvalidArguments_NeverInternalError(string rungJson)
    {
        var service = new ProjectService();
        service.Load(RungDiagramFixture);

        var c2s = new System.IO.Pipelines.Pipe();
        var s2c = new System.IO.Pipelines.Pipe();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<TagSessionService>();
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())
            .WithToolErrorFilter()
            .WithTools<LogixProjectTools>();
        using var host = builder.Build();
        await host.StartAsync();
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()));

        var args = new Dictionary<string, object?>
        {
            ["program"] = Program,
            ["routine"] = "Main",
            ["rung"]    = JsonDocument.Parse(rungJson).RootElement.Clone(),
        };
        var response = await client.CallToolAsync("logix_get_rung_diagram", args);
        var text = ((TextContentBlock)response.Content[0]).Text;

        Assert.DoesNotContain("InternalError", text);
        Assert.DoesNotContain("An error occurred invoking", text);
        if (rungJson is "0" or "\"0\"")
        {
            Assert.NotEqual(true, response.IsError);
            Assert.Contains("┤ ├", JsonDocument.Parse(text).RootElement.GetProperty("text").GetString());
        }
        else if (rungJson == "null")
        {
            Assert.Equal("InvalidArguments", JsonDocument.Parse(text).RootElement.GetProperty("kind").GetString());
        }
        else
        {
            Assert.True(response.IsError);
        }
    }

    [Fact]
    public void RungArgumentText_NumberOrString_BecomesRungText()
    {
        Assert.Equal("11", LogixProjectTools.RungArgumentText(JsonDocument.Parse("11").RootElement));
        Assert.Equal("3-7", LogixProjectTools.RungArgumentText("3-7"));
        Assert.Equal("4", LogixProjectTools.RungArgumentText(4));
        Assert.Null(LogixProjectTools.RungArgumentText(null));
        Assert.Null(LogixProjectTools.RungArgumentText(JsonDocument.Parse("null").RootElement));
        Assert.Null(LogixProjectTools.RungArgumentText("  "));
    }

    [Fact]
    public async Task Tool_WithNoProject_ReturnsAnErrorObject()
    {
        var tools = new LogixProjectTools(new ProjectService());

        using var doc = JsonDocument.Parse(await tools.GetRungDiagram(Program, "Main", "0"));

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln"))) dir = dir.Parent;
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}

/// <summary>An <see cref="ITagService"/> that can also export a routine, standing in for the SDK backend.</summary>
internal sealed class SessionStubTagService : ITagService, IRoutineL5xSource
{
    private readonly string _l5x;

    public SessionStubTagService(string l5x) => _l5x = l5x;

    public Task ConnectAsync(string gateway, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TagValue> ReadAsync(string tagName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TagValue(tagName, 0, "DINT"));

    public Task WriteAsync(string tagName, object value, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TagInfo>>(new List<TagInfo>());

    public Task<string> ExportRoutineL5xAsync(string program, string routine, CancellationToken ct = default) =>
        Task.FromResult(_l5x);
}
