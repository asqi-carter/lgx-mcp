using System.Text.RegularExpressions;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// A MSG's tag-valued attributes are
/// classified per attribute by <c>MessageType</c> (<c>DestinationTag</c> is always a reply
/// destination; <c>LocalElement</c> is a destination only for read types).
/// Built from the real-shape fixtures under <c>fixtures/real-shape/</c>.
/// </summary>
public class MsgRealShapeTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", name));

    private static L5X LoadWithType(string file, string messageType) =>
        L5X.Parse(Regex.Replace(Fixture(file), "MessageType=\"[^\"]*\"", $"MessageType=\"{messageType}\""));

    [Fact]
    public void DataTableRead_LocalElementProgramWritten()
    {
        var scan = WriteClassifier.ScanRungs(L5X.Parse(Fixture("MsgRead.L5X")));

        Assert.Contains("Program:Line_A.Dst_Values", scan.ProgramWritten.Keys);
        Assert.Contains("LocalElement", scan.ProgramWritten["Program:Line_A.Dst_Values"]);
        Assert.DoesNotContain("Dst_Values", scan.UnknownContext.Keys);
    }

    [Fact]
    public void DataTableWrite_LocalElementNotMarked()
    {
        var scan = WriteClassifier.ScanRungs(L5X.Parse(Fixture("MsgWrite.L5X")));

        Assert.DoesNotContain("Dst_Values", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Dst_Values", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Dst_Values", scan.UnknownContext.Keys);
        Assert.DoesNotContain("Program:Line_A.Dst_Values", scan.UnknownContext.Keys);
    }

    [Theory]
    [InlineData("Frobnicate")]
    [InlineData("")]
    public void UnknownMessageType_UnknownWriteContext(string messageType)
    {
        var scan = WriteClassifier.ScanRungs(LoadWithType("MsgRead.L5X", messageType));

        Assert.Contains("Program:Line_A.Dst_Values", scan.UnknownContext.Keys);
        Assert.DoesNotContain("Program:Line_A.Dst_Values", scan.ProgramWritten.Keys);
    }

    [Fact]
    public void CipGeneric_ReadFixtureLocalElementIsSourceNotMarked()
    {
        var scan = WriteClassifier.ScanRungs(LoadWithType("MsgRead.L5X", "CIP Generic"));

        Assert.DoesNotContain("Program:Line_A.Dst_Values", scan.UnknownContext.Keys);
        Assert.DoesNotContain("Program:Line_A.Dst_Values", scan.ProgramWritten.Keys);
    }

    [Fact]
    public void CipGeneric_DestinationTagProgramWritten()
    {
        var scan = WriteClassifier.ScanRungs(L5X.Parse(Fixture("MsgCipGeneric.L5X")));

        Assert.Contains("Program:Line_A.Reply_Buf", scan.ProgramWritten.Keys);
        Assert.Contains("DestinationTag", scan.ProgramWritten["Program:Line_A.Reply_Buf"]);
        Assert.DoesNotContain("Program:Line_A.Reply_Buf", scan.UnknownContext.Keys);
    }

    [Fact]
    public void CipGeneric_LocalElementIsSourceNotMarked()
    {
        var scan = WriteClassifier.ScanRungs(L5X.Parse(Fixture("MsgCipGeneric.L5X")));

        Assert.DoesNotContain("Program:Line_A.Req_Src", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Req_Src", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Req_Src", scan.UnknownContext.Keys);
        Assert.DoesNotContain("Req_Src", scan.UnknownContext.Keys);
    }

    [Fact]
    public void CipGeneric_BothAttributes()
    {
        var scan = WriteClassifier.ScanRungs(L5X.Parse(Fixture("MsgCipGeneric.L5X")));

        Assert.Contains("Program:Line_A.Both_Reply", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Both_Src", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Both_Src", scan.UnknownContext.Keys);
    }

    [Theory]
    [InlineData("Frobnicate")]
    [InlineData("")]
    public void UnknownType_AllTagAttributesUnknownWriteContext(string messageType)
    {
        var xml = Regex.Replace(Fixture("MsgCipGeneric.L5X"), "MessageType=\"[^\"]*\"", $"MessageType=\"{messageType}\"")
            .Replace("LocalIndex=\"0\" LocalElement=\"Both_Src\"",
                     "LocalIndex=\"0\" ExtraTag=\"Extra_Tag\" LocalElement=\"Both_Src\"");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        foreach (var name in new[] { "Reply_Buf", "Req_Src", "Both_Src", "Both_Reply" })
        {
            Assert.Contains($"Program:Line_A.{name}", scan.UnknownContext.Keys);
            Assert.DoesNotContain($"Program:Line_A.{name}", scan.ProgramWritten.Keys);
        }
        // Not declared in the program, so it resolves to the controller (bare) name.
        Assert.Contains("Extra_Tag", scan.UnknownContext.Keys);
    }

    [Fact]
    public void UnknownTagValuedAttributeOnKnownType_UnknownWriteContext()
    {
        var xml = Fixture("MsgWrite.L5X").Replace("CacheConnections=", "SinkElement=\"Dst_Values[1]\" CacheConnections=");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Dst_Values", scan.UnknownContext.Keys);
    }

    [Fact]
    public void DestinationTagOnDataTableRead_ProgramWritten()
    {
        var xml = Fixture("MsgRead.L5X").Replace("CacheConnections=", "DestinationTag=\"Cmd_Go\" CacheConnections=");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Cmd_Go", scan.ProgramWritten.Keys);
    }

    [Fact]
    public void UnrecognisedDestinationLikeAttribute_UnknownWriteContext()
    {
        var xml = Fixture("MsgWrite.L5X").Replace("CacheConnections=", "DestinationLink=\"3\" CacheConnections=");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Dst_Values", scan.UnknownContext.Keys);
    }

    [Theory]
    [InlineData("PLC5 Typed Read")]
    [InlineData("SLC Typed Read")]
    [InlineData("PLC2 Unprotected Read")]
    [InlineData("Block-Transfer Read")]
    public void OtherReadTypes_LocalElementProgramWritten(string messageType)
    {
        var scan = WriteClassifier.ScanRungs(LoadWithType("MsgRead.L5X", messageType));

        Assert.Contains("Program:Line_A.Dst_Values", scan.ProgramWritten.Keys);
    }

    [Fact]
    public void MsgWrittenCmdTag_IsDeniedByGate()
    {
        var xml = Fixture("MsgRead.L5X")
            .Replace("Dst_Values", "Cmd_Dst").Replace("Cmd_Go", "Go_Probe");
        var project = L5X.Parse(xml);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Dst");
        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Dst");
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
        Assert.Contains("LocalElement", deny.Provenance);
    }

    [Fact]
    public void PaddedDestination_Marked()
    {
        var xml = Fixture("MsgCipGeneric.L5X").Replace("DestinationTag=\"Reply_Buf\"", "DestinationTag=\"  Reply_Buf \"");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Reply_Buf", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Reply_Buf", scan.UnknownContext.Keys);
    }

    [Fact]
    public void PaddedLocalElementOnRead_Marked()
    {
        var xml = Regex.Replace(Fixture("MsgRead.L5X"), "LocalElement=\"Dst_Values\"", "LocalElement=\" Dst_Values\t\"");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Dst_Values", scan.ProgramWritten.Keys);
    }
}
