using L5Sharp.Core;
using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the destination-operand indexes in <see cref="WriteClassifier"/>'s
/// instruction table (operand order per Logix 5000 General Instructions, 1756-RM003).
/// </summary>
public class WriteClassifierTests
{
    /// <summary>OP(Source, Dest): the destination is argument 1.</summary>
    [Theory]
    [InlineData("SQR")]
    [InlineData("NEG")]
    [InlineData("ABS")]
    [InlineData("SIN")]
    [InlineData("COS")]
    [InlineData("TAN")]
    [InlineData("ASN")]
    [InlineData("ACS")]
    [InlineData("ATN")]
    [InlineData("LN")]
    [InlineData("LOG")]
    [InlineData("DEG")]
    [InlineData("RAD")]
    [InlineData("TOD")]
    [InlineData("FRD")]
    [InlineData("TRN")]
    public void TwoOperandMath_DestIsWrite(string instruction)
    {
        Assert.Equal("read",  WriteClassifier.ClassifyKind(instruction, 0));
        Assert.Equal("write", WriteClassifier.ClassifyKind(instruction, 1));
        Assert.Equal("write", WriteClassifier.ClassifyKind(instruction.ToLowerInvariant(), 1));
    }

    /// <summary>XPY(SourceA, SourceB, Dest): the destination is argument 2.</summary>
    [Fact]
    public void Xpy_DestIsArgumentTwo()
    {
        Assert.Equal("read",  WriteClassifier.ClassifyKind("XPY", 0));
        Assert.Equal("read",  WriteClassifier.ClassifyKind("XPY", 1));
        Assert.Equal("write", WriteClassifier.ClassifyKind("XPY", 2));
    }

    [Fact]
    public void Atn_CmdTagDestination_IsProgramWritten()
    {
        var scan = WriteClassifier.ScanFragment(
            """<Rung Number="0" Type="N"><Text>XIC(Run)ATN(Ratio,Cmd_Angle);</Text></Rung>""");

        Assert.True(scan.ProgramWritten.ContainsKey("Cmd_Angle"));
        Assert.False(scan.ProgramWritten.ContainsKey("Ratio"));
    }

    /// <summary>ST function forms return a value; their arguments are reads.</summary>
    [Fact]
    public void StFunctionForm_Atan_ArgumentIsRead()
    {
        Assert.Equal("read", WriteClassifier.ClassifyKind("ATAN", 0));

        var routine = XElement.Parse(
            """<Routine Name="R" Type="ST"><STContent><Line Number="0"><![CDATA[Result := ATAN(Cmd_Slope);]]></Line></STContent></Routine>""");
        var written = WriteClassifier.ScanStructuredText(routine).Select(w => w.Name).ToList();

        Assert.Equal(new[] { "Result" }, written);
    }

    // ── Enumerated motion operands (item 3) ──────────────────────────

    private const string MasRung =
        "[XIO(LocalEnablesOK) ,NE(ServoAxisDrive.AxisFault,0) ]MAS(ServoAxisDrive,Axis_MAS,All,Yes,StopDecel,Units per sec2,Yes,100,% of Time);";

    private const string MajRung =
        "XIC(Cmd_Jog)MAJ(JogAxis,Axis_MAJ,Forward,Yes,Cmd_JogSpeed,Units per sec,Yes,50,Units per sec2,Yes,100,% of Time,Yes,Cmd_Decel,Units per sec2,Yes,100,% of Time,Yes,Trapezoidal,5.0,% of Time);";

    private static L5X RllProject(params string[] rungs) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules /><AddOnInstructionDefinitions /><Tags /><Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                <Tags />
                <Routines><Routine Name="R" Type="RLL"><RLLContent>
                  {string.Concat(rungs.Select((r, n) => $"<Rung Number=\"{n}\" Type=\"N\"><Text><![CDATA[{r}]]></Text></Rung>"))}
                </RLLContent></Routine></Routines>
              </Program>
            </Programs><Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    /// <summary>
    /// MAS (from the bench trace) and MAJ rungs with "Units per sec2" / "% of Time" tokenise, so
    /// they never fall back to the "rung text not parseable" fail-closed path, and their axis and
    /// motion-control operands are proven writes while enumerations are not tags.
    /// </summary>
    [Fact]
    public void MotionEnumeratedOperands()
    {
        var scan = WriteClassifier.ScanRungs(RllProject(MasRung, MajRung), new[] { "*" });

        Assert.DoesNotContain(scan.UnknownContext.Values, v => v.Contains("not parseable"));
        Assert.Empty(scan.UnknownContext);

        Assert.Equal("Main/R[0] via MAS", scan.ProgramWritten["ServoAxisDrive"]);
        Assert.Equal("Main/R[0] via MAS", scan.ProgramWritten["Axis_MAS"]);
        Assert.Equal("Main/R[1] via MAJ", scan.ProgramWritten["JogAxis"]);
        Assert.Equal("Main/R[1] via MAJ", scan.ProgramWritten["Axis_MAJ"]);

        // Source operands and enumeration keywords are not writes.
        Assert.DoesNotContain("Cmd_JogSpeed", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_Decel", scan.ProgramWritten.Keys);
        Assert.DoesNotContain(scan.ProgramWritten.Keys, k => k.Contains("sec") || k.Contains("Pct") || k.Contains("Time"));

        // The fragment scanner accepts the same text.
        var fragment = WriteClassifier.ScanFragment(
            $"""<Rung Number="0" Type="N"><Text><![CDATA[{MasRung}]]></Text></Rung>""");
        Assert.True(fragment.ProgramWritten.ContainsKey("Axis_MAS"));
    }

    [Theory]
    [InlineData("MAM(Ax,Ctl,0,100,5,Units per sec,10,Units per sec2,10,Units per sec2,Disabled,Programmed,0,0,0,0,0,0,% of Time);")]
    [InlineData("MAH(Ax,Ctl);")]
    [InlineData("MCD(Ax,Ctl,Jog,Yes,5,Units per sec2,Yes,100,% of Time);")]
    [InlineData("MAG(Slave,Ctl,Master,Actual Position,1,1,Yes,Yes);")]
    public void OtherMotionInstructions_WithEnumeratedOperands_Parse(string rung)
    {
        var scan = WriteClassifier.ScanRungs(RllProject(rung), new[] { "*" });
        Assert.Empty(scan.UnknownContext);
        Assert.True(scan.ProgramWritten.ContainsKey("Ctl"));
    }

    [Fact]
    public void SanitizeEnumeratedOperands_LeavesExpressionsAndIndexesAlone()
    {
        const string cmp = "CMP(A AND B > 1);";
        Assert.Equal(cmp, WriteClassifier.SanitizeEnumeratedOperands(cmp));
        const string idx = "MOV(Arr[1,2],Dst);";
        Assert.Equal(idx, WriteClassifier.SanitizeEnumeratedOperands(idx));
        Assert.Equal("MAS(A,C,Units_per_sec2,Pct_of_Time);",
            WriteClassifier.SanitizeEnumeratedOperands("MAS(A,C,Units per sec2,% of Time);"));
    }
}
