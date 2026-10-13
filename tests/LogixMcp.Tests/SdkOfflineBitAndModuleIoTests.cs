#if LOGIX_SDK
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Bit-of-integer and module I/O reads on a <c>backend=sdk</c> offline session: the same
/// resolver and bit rules as CIP, module I/O typed from the export and valued from the file's decorated data.
/// </summary>
public class SdkOfflineBitAndModuleIoTests
{
    private const string ProjectXml = """
        <RSLogix5000Content SchemaRevision="1.0"><Controller Name="C" MajorRev="38" MinorRev="11" ProcessorType="1756-L83E">
        <DataTypes />
        <Modules>
        <Module Name="Local" CatalogNumber="1756-L83E" ParentModule="Local" ParentModPortId="1"><Ports>
        <Port Id="1" Address="0" Type="ICP" Upstream="false"><Bus Size="7"/></Port></Ports></Module>
        <Module Name="IB16D_S1" CatalogNumber="1756-IB16D" ParentModule="Local" ParentModPortId="1">
        <Ports><Port Id="1" Address="1" Type="ICP" Upstream="true"/></Ports>
        <Communications><Connections><Connection Name="Data" Type="Input"><InputTag ExternalAccess="Read/Write">
        <Data Format="Decorated"><Structure DataType="AB:1756_DI:I:0">
        <DataValueMember Name="Fault" DataType="DINT" Radix="Binary" Value="2#0000_0000_0000_0000_0000_0000_0000_0000"/>
        <DataValueMember Name="Data" DataType="DINT" Radix="Binary" Value="2#0000_0000_0000_0000_0000_0000_0000_1000"/>
        </Structure></Data></InputTag></Connection></Connections></Communications></Module>
        <Module Name="IB16D_S2" CatalogNumber="1756-IB16D" ParentModule="Local" ParentModPortId="1">
        <Ports><Port Id="1" Address="2" Type="ICP" Upstream="true"/></Ports>
        <Communications><Connections><Connection Name="Data" Type="Input"><InputTag ExternalAccess="Read/Write">
        <Data Format="Decorated"><Structure DataType="AB:1756_DI:I:0">
        <DataValueMember Name="Fault" DataType="DINT" Radix="Binary"/>
        <DataValueMember Name="Data" DataType="DINT" Radix="Binary"/>
        </Structure></Data></InputTag></Connection></Connections></Communications></Module>
        </Modules>
        <Tags>
        <Tag Name="Bench_Dint" TagType="Base" DataType="DINT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"/>
        <Tag Name="Bench_Lint" TagType="Base" DataType="LINT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write"/>
        <Tag Name="Alias_Bit" TagType="Alias" AliasFor="Bench_Dint.5" Radix="Decimal" ExternalAccess="Read/Write"/>
        <Tag Name="Alias_Chain" TagType="Alias" AliasFor="Alias_Bit" Radix="Decimal" ExternalAccess="Read/Write"/>
        <Tag Name="Alias_LintBit" TagType="Alias" AliasFor="Bench_Lint.63" Radix="Decimal" ExternalAccess="Read/Write"/>
        <Tag Name="Alias_ModIn" TagType="Alias" AliasFor="Local:1:I.Data" Radix="Decimal" ExternalAccess="Read/Write"/>
        <Tag Name="Alias_ModInBit" TagType="Alias" AliasFor="Local:1:I.Data.3" Radix="Decimal" ExternalAccess="Read/Write"/>
        </Tags>
        <Programs/></Controller></RSLogix5000Content>
        """;

    private static async Task<(TypedSdkTagService Svc, RecordingFakeLogixProject Fake)> OpenAsync()
    {
        var fake = new RecordingFakeLogixProject { DintReturnValue = 0x20, LintReturnValue = long.MinValue };
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        return (new TypedSdkTagService(session, L5X.Parse(ProjectXml)), fake);
    }

    [Fact]
    public async Task DintBitAndLintBit_OnSdkOffline_ReturnValues()
    {
        var (svc, fake) = await OpenAsync();

        var set = await svc.ReadAsync("Bench_Dint.5");
        Assert.Equal(true, set.Value);
        Assert.Equal("BOOL", set.DataType);
        Assert.Equal("Bench_Dint.5", set.TagName);
        Assert.Equal("Controller/Tags/Tag[@Name='Bench_Dint']", fake.LastReadTagPath); // the parent is read, never the bit

        Assert.Equal(false, (await svc.ReadAsync("Bench_Dint.4")).Value);
        Assert.Equal(true, (await svc.ReadAsync("Bench_Lint.63")).Value);
        Assert.Equal(false, (await svc.ReadAsync("Bench_Lint.62")).Value);
    }

    [Fact]
    public async Task ModuleIoBit_AndAliasOfAliasReads_ResolveOnSdkOffline()
    {
        var (svc, _) = await OpenAsync();

        var data = await svc.ReadAsync("Local:1:I.Data");
        Assert.Equal(8, data.Value);
        Assert.Equal("DINT", data.DataType);

        var bit3 = await svc.ReadAsync("Local:1:I.Data.3");
        Assert.Equal(true, bit3.Value);
        Assert.Equal("BOOL", bit3.DataType);
        Assert.Equal(false, (await svc.ReadAsync("Local:1:I.Data.2")).Value);

        var alias = await svc.ReadAsync("Alias_Bit");
        Assert.Equal(true, alias.Value);
        Assert.Equal("Alias_Bit", alias.TagName);
        Assert.Equal("Bench_Dint.5", alias.ResolvedFrom);

        var chain = await svc.ReadAsync("Alias_Chain");
        Assert.Equal(true, chain.Value);
        Assert.Equal("Bench_Dint.5", chain.ResolvedFrom);

        Assert.Equal(true, (await svc.ReadAsync("Alias_LintBit")).Value);

        var modIn = await svc.ReadAsync("Alias_ModIn");
        Assert.Equal(8, modIn.Value);
        Assert.Equal("Local:1:I.Data", modIn.ResolvedFrom);

        var modBit = await svc.ReadAsync("Alias_ModInBit");
        Assert.Equal(true, modBit.Value);
        Assert.Equal("BOOL", modBit.DataType);
    }

    [Fact]
    public async Task ModuleIoWithNoValueInTheFile_ReturnsValueUnavailableOffline_NamingTheType()
    {
        var (svc, _) = await OpenAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Local:2:I.Data"));
        Assert.Equal(TagServiceErrorKind.ValueUnavailableOffline, ex.Error.Kind);
        Assert.Contains("DINT", ex.Error.Message);

        var bit = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Local:2:I.Data.3"));
        Assert.Equal(TagServiceErrorKind.ValueUnavailableOffline, bit.Error.Kind);
        Assert.Contains("DINT", bit.Error.Message);
    }

    [Fact]
    public async Task OutOfRangeBit_OnSdkOffline_ReturnsBitOutOfRange_WithARange()
    {
        var (svc, fake) = await OpenAsync();

        var dint = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Bench_Dint.32"));
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, dint.Error.Kind);
        Assert.Contains("bits 0 to 31", dint.Error.Message);
        Assert.Null(fake.LastReadTagPath); // nothing was read

        var lint = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Bench_Lint.64"));
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, lint.Error.Kind);
        Assert.Contains("bits 0 to 63", lint.Error.Message);

        var module = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Local:1:I.Data.32"));
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, module.Error.Kind);
        Assert.Contains("bits 0 to 31", module.Error.Message);
    }
}
#endif
