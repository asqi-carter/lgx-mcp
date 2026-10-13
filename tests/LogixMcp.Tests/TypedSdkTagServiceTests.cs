#if LOGIX_SDK
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// L5Sharp.Core defines its own Task type (representing an AOI Task component).
// Alias System.Threading.Tasks.Task to avoid the ambiguous-reference error.
using Task = System.Threading.Tasks.Task;

using ControllerMode           = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState           = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType                 = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode            = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget     = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode  = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions   = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption      = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent       = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode        = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate  = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;

namespace LogixMcp.Tests;

// ── RecordingFakeLogixProject ─────────────────────────────────────────────────

/// <summary>
/// A configurable <see cref="ILogixProject"/> fake for
/// <see cref="TypedSdkTagServiceTests"/>.
///
/// <para>
/// Extends the simple fixed-return fake from
/// <see cref="LogixProjectSessionTests"/> by recording which typed accessor
/// was last called and the tag path supplied, so tests can assert that
/// <see cref="TypedSdkTagService"/> routes to the correct accessor.
/// </para>
/// </summary>
internal sealed class RecordingFakeLogixProject : ILogixProject
{
    // ── Recording ─────────────────────────────────────────────────────────────

    /// <summary>XPath received by the most recent <c>GetTagValueXXX</c> call.</summary>
    public string? LastReadTagPath  { get; private set; }

    /// <summary>XPath received by the most recent <c>SetTagValueXXX</c> call.</summary>
    public string? LastWriteTagPath { get; private set; }

    /// <summary>
    /// Logix type name of the most recent <c>GetTagValueXXX</c> call
    /// (e.g. <c>"BOOL"</c>, <c>"REAL"</c>).
    /// </summary>
    public string? LastReadType  { get; private set; }

    /// <summary>
    /// Logix type name of the most recent <c>SetTagValueXXX</c> call.
    /// </summary>
    public string? LastWriteType { get; private set; }

    /// <summary>
    /// <see cref="OperationMode"/> received by the most recent
    /// <c>GetTagValueXXX</c> call.  Allows tests to verify that the
    /// correct mode (Online/Offline) is forwarded from
    /// <see cref="TypedSdkTagService"/> to the SDK layer.
    /// </summary>
    public OperationMode? LastReadMode  { get; private set; }

    /// <summary>
    /// <see cref="OperationMode"/> received by the most recent
    /// <c>SetTagValueXXX</c> call.
    /// </summary>
    public OperationMode? LastWriteMode { get; private set; }

    // ── Configurable return values ────────────────────────────────────────────

    public bool   BoolReturnValue   { get; set; }
    public sbyte  SintReturnValue   { get; set; }
    public short  IntReturnValue    { get; set; }
    public int    DintReturnValue   { get; set; }
    public long   LintReturnValue   { get; set; }
    public byte   UsintReturnValue  { get; set; }
    public ushort UintReturnValue   { get; set; }
    public uint   UdintReturnValue  { get; set; }
    public ulong  UlintReturnValue  { get; set; }
    public float  RealReturnValue   { get; set; }
    public double LrealReturnValue  { get; set; }
    public string StringReturnValue { get; set; } = string.Empty;

    /// <summary>
    /// When non-<c>null</c>, every typed tag-accessor call (<c>GetTagValueXXXAsync</c>
    /// and <c>SetTagValueXXXAsync</c>) throws this exception instead of returning a
    /// value.  Used to simulate SDK comm-layer faults (e.g. <c>RxE_COMM_DTLERROR</c>)
    /// from within a live tag operation.
    /// </summary>
    public Exception? TagAccessorException { get; set; }

    public void Dispose() { }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ThrowIfAccessorException()
    {
        if (TagAccessorException is not null)
            throw TagAccessorException;
    }

    // ── Get/Set tag-value typed accessors ─────────────────────────────────────

    public Task<bool>   GetTagValueBOOLAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "BOOL";   LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(BoolReturnValue); }

    public Task<sbyte>  GetTagValueSINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "SINT";   LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(SintReturnValue); }

    public Task<short>  GetTagValueINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "INT";    LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(IntReturnValue); }

    public Task<int>    GetTagValueDINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "DINT";   LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(DintReturnValue); }

    public Task<long>   GetTagValueLINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "LINT";   LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(LintReturnValue); }

    public Task<byte>   GetTagValueUSINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "USINT";  LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(UsintReturnValue); }

    public Task<ushort> GetTagValueUINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "UINT";   LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(UintReturnValue); }

    public Task<uint>   GetTagValueUDINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "UDINT";  LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(UdintReturnValue); }

    public Task<ulong>  GetTagValueULINTAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "ULINT";  LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(UlintReturnValue); }

    public Task<float>  GetTagValueREALAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "REAL";   LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(RealReturnValue); }

    public Task<double> GetTagValueLREALAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "LREAL";  LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(LrealReturnValue); }

    public Task<string> GetTagValueSTRINGAsync(string tagPath, OperationMode mode)
    { LastReadTagPath = tagPath; LastReadType = "STRING"; LastReadMode = mode; ThrowIfAccessorException(); return Task.FromResult(StringReturnValue); }

    public Task SetTagValueBOOLAsync(string tagPath, OperationMode mode, bool tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "BOOL";   LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueSINTAsync(string tagPath, OperationMode mode, sbyte tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "SINT";   LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueINTAsync(string tagPath, OperationMode mode, short tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "INT";    LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueDINTAsync(string tagPath, OperationMode mode, int tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "DINT";   LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueLINTAsync(string tagPath, OperationMode mode, long tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "LINT";   LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueUSINTAsync(string tagPath, OperationMode mode, byte tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "USINT";  LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueUINTAsync(string tagPath, OperationMode mode, ushort tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "UINT";   LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueUDINTAsync(string tagPath, OperationMode mode, uint tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "UDINT";  LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueULINTAsync(string tagPath, OperationMode mode, ulong tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "ULINT";  LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueREALAsync(string tagPath, OperationMode mode, float tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "REAL";   LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueLREALAsync(string tagPath, OperationMode mode, double tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "LREAL";  LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    public Task SetTagValueSTRINGAsync(string tagPath, OperationMode mode, string tagValue)
    { LastWriteTagPath = tagPath; LastWriteType = "STRING"; LastWriteMode = mode; ThrowIfAccessorException(); return Task.CompletedTask; }

    // ── ByteArray read support ────────────────────────────────────────────────

    /// <summary>
    /// Byte array to return from <see cref="GetTagValueAsync"/> (the raw-byte
    /// read path used by <see cref="TypedSdkTagService"/> for UDT member reads).
    /// </summary>
    public ByteString ByteArrayReturnValue { get; set; } = ByteString.Empty;

    /// <summary>XPath received by the most recent <c>GetTagValueAsync</c> call.</summary>
    public string? LastByteArrayReadTagPath { get; private set; }

    public Task<ByteString> GetTagValueAsync(string tagPath, OperationMode mode, DataType type)
    {
        LastByteArrayReadTagPath = tagPath;
        return Task.FromResult(ByteArrayReturnValue);
    }

    public Task SetTagValueAsync(string tagPath, OperationMode mode, byte[] tagValue, DataType type)
        => Task.CompletedTask;

    public Task<string>         GetCommunicationsPathAsync(CancellationToken ct = default)    => throw new NotImplementedException();
    public Task                 SetCommunicationsPathAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
    public Task                 ChangeControllerTypeAsync(string t, CancellationToken ct = default)  => throw new NotImplementedException();
    public Task                 SaveAsAsync(string p, bool f, bool d, CancellationToken ct = default) => throw new NotImplementedException();
    public Task                 SaveAsync(CancellationToken ct = default)                      => throw new NotImplementedException();
    public Task                 DownloadAsync(CancellationToken ct = default)                  => throw new NotImplementedException();
    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default)        => throw new NotImplementedException();
    public Task                 ChangeControllerModeAsync(RequestedControllerMode m, CancellationToken ct = default) => throw new NotImplementedException();
    public Task                 BuildAsync(RequestedBuildTarget t, CancellationToken ct = default)    => throw new NotImplementedException();
    public Task                 UploadAsync(CancellationToken ct = default)                    => throw new NotImplementedException();
    public Task                 StoreImageOnSDCardAsync(RequestedLoadEvent le, RequestedLoadMode lm, AutomaticFirmwareUpdate afu, string n, string note) => throw new NotImplementedException();
    public Task                 LoadImageFromSDCardAsync()                                     => throw new NotImplementedException();
    public void                 AddEventHandler(IOperationEvent h)    { }
    public void                 ClearEventHandlers()                   { }
    public void                 RemoveEventHandler(IOperationEvent h)  { }

    /// <summary>XPaths passed to <c>PartialExportToXmlFileAsync</c>, in call order.</summary>
    public List<string> PartialExportXPaths { get; } = new();

    /// <summary>
    /// When set, <c>PartialExportToXmlFileAsync</c> writes the returned L5X to the
    /// target file (given the requested XPath).  When <c>null</c> the export throws
    /// <see cref="NotImplementedException"/> as before.
    /// </summary>
    public Func<string, string>? PartialExportContent { get; set; }

    public async Task PartialExportToXmlFileAsync(string xp, string fp, CancellationToken ct = default)
    {
        PartialExportXPaths.Add(xp);
        if (PartialExportContent is null) throw new NotImplementedException();
        await System.IO.File.WriteAllTextAsync(fp, PartialExportContent(xp), ct);
    }
    public Task PartialImportFromXmlFileAsync(string xp, string xf, ImportCollisionOptions o, bool c, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportWithTargetFromXmlFileAsync(string xp, string tn, string xf, PartialImportOption oi, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportRungsFromXmlFileAsync(string xp, uint ip, uint rc, string xf, PartialImportOption oi, CancellationToken ct = default) => throw new NotImplementedException();

    public Task                   GoOnlineAsync(CancellationToken ct = default)                => throw new NotImplementedException();
    public Task                   GoOfflineAsync(CancellationToken ct = default)               => throw new NotImplementedException();
    public Task<ConnectedState>   ReadConnectedStateAsync(CancellationToken ct = default)      => throw new NotImplementedException();

    public Task<string> GetSafetyNetworkNumberAsync(string m, CancellationToken ct = default)  => throw new NotImplementedException();
    public Task<bool>   IsSafetyLockedAsync(CancellationToken ct = default)                    => throw new NotImplementedException();
    public Task         SafetyLockAsync(string p, CancellationToken ct = default)              => throw new NotImplementedException();
    public Task         SafetyUnlockAsync(string p, CancellationToken ct = default)            => throw new NotImplementedException();
    public Task         SetSafetyLockPasswordAsync(string np, string op, CancellationToken ct = default)   => throw new NotImplementedException();
    public Task         SetSafetyUnlockPasswordAsync(string np, string op, CancellationToken ct = default) => throw new NotImplementedException();
    public Task         GenerateSafetySignatureAsync(CancellationToken ct = default)           => throw new NotImplementedException();
    public Task         DeleteSafetySignatureAsync(CancellationToken ct = default)             => throw new NotImplementedException();
    public Task<string> GetSafetySignatureAsync(CancellationToken ct = default)               => throw new NotImplementedException();

    public Task ProtectWithLicenseAsync(string xp, uint f, uint p, CancellationToken ct = default)                 => throw new NotImplementedException();
    public Task ProtectWithLicenseAsync(IEnumerable<string> xp, uint f, uint p, CancellationToken ct = default)    => throw new NotImplementedException();
    public Task ProtectAllWithLicenseAsync(uint f, uint p, CancellationToken ct = default)                         => throw new NotImplementedException();
    public Task ProtectAllWithPasswordAsync(string p, CancellationToken ct = default)                               => throw new NotImplementedException();
    public Task UnprotectAllAsync(CancellationToken ct = default)                                                   => throw new NotImplementedException();
    public Task ProtectWithPasswordAsync(string xp, string p, CancellationToken ct = default)                      => throw new NotImplementedException();
    public Task ProtectWithPasswordAsync(IEnumerable<string> xp, string p, CancellationToken ct = default)         => throw new NotImplementedException();
    public Task UnprotectAsync(string xp, CancellationToken ct = default)                                          => throw new NotImplementedException();
    public Task UnprotectAsync(IEnumerable<string> xp, CancellationToken ct = default)                             => throw new NotImplementedException();
    public Task LockExecutableAsync(string xp, CancellationToken ct = default)                                     => throw new NotImplementedException();
    public Task LockExecutableAsync(IEnumerable<string> xp, CancellationToken ct = default)                        => throw new NotImplementedException();
    public Task<bool> IsExecutableLockedAsync(string xp, CancellationToken ct = default)                           => throw new NotImplementedException();
    public Task UnlockExecutableAsync(string xp, CancellationToken ct = default)                                   => throw new NotImplementedException();
    public Task UnlockExecutableAsync(IEnumerable<string> xp, CancellationToken ct = default)                      => throw new NotImplementedException();
    public Task LockAllAsync(CancellationToken ct = default)                                                        => throw new NotImplementedException();
    public Task UnlockAllAsync(CancellationToken ct = default)                                                      => throw new NotImplementedException();
    public Task<bool> IsExecutableProtectedAsync(string xp, CancellationToken ct = default)                        => throw new NotImplementedException();
    public Task<string[]> GetAllExecutablesAsync(CancellationToken ct = default)                                    => throw new NotImplementedException();
}

// ── TypedSdkTagServiceTests ───────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="TypedSdkTagService"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Construction — null guards, valid construction</item>
///   <item><c>ITagService</c> assignability</item>
///   <item><see cref="TypedSdkTagService.ConnectAsync"/> — open-session validation</item>
///   <item><see cref="TypedSdkTagService.DisconnectAsync"/> — no-op</item>
///   <item>Type resolution — L5X project consulted for declared type</item>
///   <item>Typed accessor routing — correct
///     <c>GetTagValueXXXAsync</c> / <c>SetTagValueXXXAsync</c> called per type</item>
///   <item>REAL round-trip as <see cref="float"/> — the key defect fixed vs
///     <see cref="LibPlcTagService"/> (which returns a DINT bit-pattern)</item>
///   <item>XPath translation — tag name correctly translated before SDK call</item>
///   <item>Type-mismatch rejection — no silent coercion; exact type required</item>
///   <item>Closed-session error — structured error when session disposed</item>
///   <item><see cref="TypedSdkTagService.ListTagsAsync"/> — tags from L5X, session guard</item>
/// </list>
/// </summary>
public class TypedSdkTagServiceTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>Path to the shared Demo.L5X fixture (BOOL + REAL tags).</summary>
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// A minimal L5X project with one tag of every supported atomic type.
    /// Parsed by <see cref="BuildAllTypesProject"/> for typed-routing tests.
    /// </summary>
    private const string AllTypesL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TypeTestController" TargetType="Controller" ContainsContext="false">
          <Controller Name="TypeTestController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="BoolTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="BOOL"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="SintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="SINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="SINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="IntTag"    TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="INT"   Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="INT"   Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="DintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="DINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="LintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="LINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="LINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UsintTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="USINT" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="USINT" Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="UINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="UINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UdintTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="UDINT" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="UDINT" Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UlintTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="ULINT" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="ULINT" Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="RealTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="REAL"  Radix="Float">
                <Data Format="Decorated"><DataValue DataType="REAL"  Radix="Float"   Value="0.0"/></Data>
              </Tag>
              <Tag Name="LrealTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="LREAL" Radix="Float">
                <Data Format="Decorated"><DataValue DataType="LREAL" Radix="Float"   Value="0.0"/></Data>
              </Tag>
            </Tags>
            <Programs />
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// A minimal L5X project with a <c>RecipeTagsType</c> UDT and a single
    /// controller-scoped tag <c>SomeRecipeTag</c> of that type.
    ///
    /// <para>
    /// Layout (Studio 5000 rules):
    /// <code>
    /// offset 0, size 4 — RecipeNameLen  (DINT)
    /// offset 4, size 2 — TotalDoseCount (INT)
    /// offset 6, size 10 — SetupValues[5] (INT[5])
    /// total 16 bytes
    /// </code>
    /// </para>
    /// </summary>
    private const string UdtMemberL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="UdtMemberController" TargetType="Controller" ContainsContext="false">
          <Controller Name="UdtMemberController" MajorRev="36" MinorRev="1"
                      ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="RecipeTagsType" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="RecipeNameLen"     DataType="DINT" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="TotalDoseCount" DataType="INT"  Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="SetupValues"        DataType="INT"  Dimension="5"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="SomeRecipeTag" TagType="Base" ExternalAccess="Read/Write"
                   Constant="false" DataType="RecipeTagsType">
                <Data Format="Decorated">
                  <Structure DataType="RecipeTagsType" />
                </Data>
              </Tag>
            </Tags>
            <Programs />
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>Loads the Demo.L5X fixture.</summary>
    private static L5X LoadDemoProject()
        => L5X.Load(FixturePath);

    /// <summary>
    /// Parses an in-memory L5X containing one tag per supported atomic type.
    /// </summary>
    private static L5X BuildAllTypesProject()
        => L5X.Parse(AllTypesL5Xml);

    /// <summary>
    /// Creates a <see cref="LogixProjectSession"/> backed by
    /// <paramref name="fake"/>.
    /// </summary>
    private static LogixProjectSession SessionWith(RecordingFakeLogixProject fake)
        => new((_path, _ct) => Task.FromResult<ILogixProject>(fake));

    /// <summary>
    /// Opens a session backed by <paramref name="fake"/> and returns a
    /// connected <see cref="TypedSdkTagService"/> using Demo.L5X for type
    /// resolution.
    /// </summary>
    private static async Task<(TypedSdkTagService Svc, RecordingFakeLogixProject Fake)>
        OpenedServiceWithDemoAsync()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());
        return (svc, fake);
    }

    /// <summary>
    /// Opens a session backed by <paramref name="fake"/> and returns a
    /// connected <see cref="TypedSdkTagService"/> using the all-types L5X for
    /// type resolution.
    /// </summary>
    private static async Task<(TypedSdkTagService Svc, RecordingFakeLogixProject Fake)>
        OpenedServiceWithAllTypesAsync()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, BuildAllTypesProject());
        return (svc, fake);
    }

    /// <summary>
    /// Opens a session backed by <paramref name="fake"/> and returns a
    /// connected <see cref="TypedSdkTagService"/> using the UDT-member L5X for
    /// type resolution (<c>RecipeTagsType</c> UDT with <c>SomeRecipeTag</c> tag).
    /// </summary>
    private static async Task<(TypedSdkTagService Svc, RecordingFakeLogixProject Fake)>
        OpenedServiceWithUdtMemberProjectAsync(RecordingFakeLogixProject? fake = null)
    {
        fake  ??= new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, L5X.Parse(UdtMemberL5Xml));
        return (svc, fake);
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSession_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TypedSdkTagService(null!, LoadDemoProject()));
    }

    [Fact]
    public void Constructor_NullTypeProject_ThrowsArgumentNullException()
    {
        var session = new LogixProjectSession((_p, _ct) =>
            Task.FromResult<ILogixProject>(new RecordingFakeLogixProject()));

        Assert.Throws<ArgumentNullException>(
            () => new TypedSdkTagService(session, null!));
    }

    [Fact]
    public async Task Constructor_ValidArgs_DoesNotThrow()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");

        var svc = new TypedSdkTagService(session, LoadDemoProject());

        Assert.NotNull(svc);
    }

    // ── Session property ──────────────────────────────────────────────────────

    [Fact]
    public async Task Session_ReturnsUnderlyingLogixProjectSession()
    {
        // The Session property should surface the exact LogixProjectSession that
        // was supplied to the constructor — the route from TypedSdkTagService back
        // to its session without any new global.
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");

        var svc = new TypedSdkTagService(session, LoadDemoProject());

        Assert.Same(session, svc.Session);
    }

    // ── Interface assignability ───────────────────────────────────────────────

    [Fact]
    public async Task TypedSdkTagService_ImplementsITagService()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");

        ITagService svc = new TypedSdkTagService(session, LoadDemoProject());

        Assert.NotNull(svc);
    }

    // ── ConnectAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_WhenSessionOpen_DoesNotThrow()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        await svc.ConnectAsync("unused-gateway", "1,0");
    }

    [Fact]
    public async Task ConnectAsync_WhenSessionNotOpened_ThrowsConnectionFailed()
    {
        var session = SessionWith(new RecordingFakeLogixProject());
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            async () => await svc.ConnectAsync("gw", "1,0"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ConnectAsync_WhenSessionClosed_ThrowsConnectionFailed()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());
        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            async () => await svc.ConnectAsync("gw", "1,0"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── DisconnectAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DisconnectAsync_IsAlwaysNoOp()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        // Must not throw regardless of session state.
        await svc.DisconnectAsync();
    }

    [Fact]
    public async Task DisconnectAsync_AfterSessionClosed_DoesNotThrow()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());
        session.Dispose();

        await svc.DisconnectAsync();   // Must be a no-op, not throw.
    }

    // ── ReadAsync — typed accessor routing ────────────────────────────────────

    /// <summary>
    /// KEY ACCEPTANCE TEST: A REAL tag must return a <see cref="float"/> value
    /// via <c>GetTagValueREALAsync</c> — not an <see cref="int"/> bit-pattern
    /// via <c>GetTagValueDINTAsync</c> as <see cref="LibPlcTagService"/> does.
    /// </summary>
    [Fact]
    public async Task ReadAsync_RealTag_CallsGetTagValueREALAsync_ReturnFloat()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();
        fake.RealReturnValue = 3.14f;

        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");

        Assert.Equal("REAL", fake.LastReadType);          // Correct accessor called
        Assert.IsType<float>(result.Value);               // Float returned, not int
        Assert.Equal(3.14f, (float)result.Value, tolerance: 0.0001f);  // Value preserved
        Assert.Equal("REAL", result.DataType);
    }

    [Fact]
    public async Task ReadAsync_BoolTag_CallsGetTagValueBOOLAsync_ReturnBool()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();
        fake.BoolReturnValue = true;

        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal("BOOL", fake.LastReadType);
        Assert.IsType<bool>(result.Value);
        Assert.True((bool)result.Value);
        Assert.Equal("BOOL", result.DataType);
    }

    [Theory]
    [InlineData("BoolTag",  "BOOL")]
    [InlineData("SintTag",  "SINT")]
    [InlineData("IntTag",   "INT")]
    [InlineData("DintTag",  "DINT")]
    [InlineData("LintTag",  "LINT")]
    [InlineData("UsintTag", "USINT")]
    [InlineData("UintTag",  "UINT")]
    [InlineData("UdintTag", "UDINT")]
    [InlineData("UlintTag", "ULINT")]
    [InlineData("RealTag",  "REAL")]
    [InlineData("LrealTag", "LREAL")]
    public async Task ReadAsync_RoutesToCorrectTypedAccessor(
        string tagName, string expectedType)
    {
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();

        var result = await svc.ReadAsync(tagName);

        Assert.Equal(expectedType, fake.LastReadType);
        Assert.Equal(expectedType, result.DataType);
    }

    // ── ReadAsync — tag path forwarding ──────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="TypedSdkTagService.ReadAsync"/> translates the
    /// caller-supplied tag name to the SDK XPath form before forwarding it to
    /// the typed accessor (regression: 09c88af removed this translation).
    /// </summary>
    [Fact]
    public async Task ReadAsync_AtomicTag_PassesTranslatedXPathToSdk()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();

        await svc.ReadAsync("Cmd_MotorStart");

        // TagXPathTranslator.Translate("Cmd_MotorStart") =
        //   "Controller/Tags/Tag[@Name='Cmd_MotorStart']"
        Assert.Equal("Controller/Tags/Tag[@Name='Cmd_MotorStart']", fake.LastReadTagPath);
    }

    [Fact]
    public async Task ReadAsync_PassesTagNameDirectlyToSdk()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();

        await svc.ReadAsync("Cmd_MotorStart");

        // Tag name is translated to XPath form before being forwarded to the SDK.
        Assert.Equal("Controller/Tags/Tag[@Name='Cmd_MotorStart']", fake.LastReadTagPath);
    }

    // ── Type miss → refresh from SDK partial export ──

    private static string TagExportL5X(string tagName, string dataType = "DINT") => $"""
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="{tagName}" TargetType="Tag" ContainsContext="true">
          <Controller Use="Context" Name="TypeTestController">
            <Tags Use="Context">
              <Tag Name="{tagName}" TagType="Base" DataType="{dataType}" Radix="Decimal"
                   Constant="false" ExternalAccess="Read/Write">
                <Data Format="L5K"><![CDATA[0]]></Data>
                <Data Format="Decorated"><DataValue DataType="{dataType}" Radix="Decimal" Value="0" /></Data>
              </Tag>
            </Tags>
          </Controller>
        </RSLogix5000Content>
        """;

    private const string EmptyTagExportL5X = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TypeTestController" TargetType="Controller" ContainsContext="true">
          <Controller Use="Context" Name="TypeTestController">
            <Tags Use="Context" />
          </Controller>
        </RSLogix5000Content>
        """;

    [Fact]
    public async Task TypeMiss_RefreshesFromSdkExport_ThenResolves()
    {
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();
        fake.DintReturnValue = 77;
        fake.PartialExportContent = xp => TagExportL5X("CreatedAfterLoad", "DINT");

        var result = await svc.ReadAsync("CreatedAfterLoad");

        Assert.Equal("DINT", result.DataType);
        Assert.Equal(77, result.Value);
        Assert.Equal("Controller/Tags/Tag[@Name='CreatedAfterLoad']", fake.LastReadTagPath);
        Assert.Equal(new[] { "Controller/Tags/Tag[@Name='CreatedAfterLoad']" }, fake.PartialExportXPaths);

        // Now known: a second read resolves from the lookup with no further export.
        await svc.ReadAsync("CreatedAfterLoad");
        Assert.Single(fake.PartialExportXPaths);
    }

    [Fact]
    public async Task TypeMiss_ExportAlsoMissing_IsTagNotFound()
    {
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();
        fake.PartialExportContent = _ => EmptyTagExportL5X;

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("StillNotThere"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Equal("StillNotThere", ex.Error.TagName);
        Assert.Single(fake.PartialExportXPaths);
    }

    [Fact]
    public async Task TypeMiss_RefreshesOncePerMiss_NoPolling()
    {
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();
        fake.PartialExportContent = _ => EmptyTagExportL5X;

        await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Ghost"));
        Assert.Single(fake.PartialExportXPaths);   // one export for the one miss, no retry loop

        await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Ghost"));
        Assert.Equal(2, fake.PartialExportXPaths.Count);   // a new miss gets exactly one new export
    }

    [Fact]
    public async Task TypeMiss_ExportFails_IsTagNotFound()
    {
        // Default fake export throws NotImplementedException: still a clean TagNotFound.
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Ghost"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Single(fake.PartialExportXPaths);
    }

    [Fact]
    public async Task TypeMiss_Write_DoesNotRefresh()
    {
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();
        fake.PartialExportContent = _ => TagExportL5X("CreatedAfterLoad");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("CreatedAfterLoad", 1));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Empty(fake.PartialExportXPaths);
    }

    // ── ReadAsync — tag not found ─────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_UnknownTag_ThrowsTagNotFound()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("NonExistent_Tag"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Equal("NonExistent_Tag", ex.Error.TagName);
    }

    // ── ReadAsync — closed session ────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_WhenSessionClosed_ThrowsConnectionFailed()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── ReadAsync — UDT member path via byte-array decode ────────────────────

    /// <summary>
    /// KEY ACCEPTANCE TEST: Reading a UDT member path (e.g.
    /// <c>"SomeRecipeTag.TotalDoseCount"</c>) must route through the
    /// byte-array decode path rather than a typed SDK accessor.
    ///
    /// <para>
    /// <see cref="TypedSdkTagService"/> calls <c>GetTagValueAsync</c> with
    /// <c>DataType.BYTE_ARRAY</c> for the parent tag (<c>SomeRecipeTag</c>),
    /// uses <see cref="UdtLayout"/> to compute the member's byte offset, and
    /// decodes the INT value from the raw bytes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReadAsync_MemberPath_ReadsParentByteArray_DecodesInt()
    {
        // ── Arrange ───────────────────────────────────────────────────────────
        // RecipeTagsType layout (Studio 5000 rules, same as UdtLayoutTests):
        //   offset 0, size 4 — RecipeNameLen (DINT)
        //   offset 4, size 2 — TotalDoseCount (INT)
        //   offset 6, size 10 — SetupValues[5] (INT array)
        // Total: 16 bytes
        const short expectedValue = 42;

        var rawBytes = new byte[16];
        // RecipeNameLen = 0 (bytes 0-3, already zero-initialised)
        // TotalDoseCount = 42 at offset 4 (little-endian INT)
        BitConverter.GetBytes(expectedValue).CopyTo(rawBytes, 4);
        // SetupValues[5] = 0 (bytes 6-15, already zero-initialised)

        var fake = new RecordingFakeLogixProject
        {
            ByteArrayReturnValue = ByteString.CopyFrom(rawBytes),
        };
        var (svc, _) = await OpenedServiceWithUdtMemberProjectAsync(fake);

        // ── Act ───────────────────────────────────────────────────────────────
        var result = await svc.ReadAsync("SomeRecipeTag.TotalDoseCount");

        // ── Assert ────────────────────────────────────────────────────────────
        // Parent tag XPath was used for the byte-array read (not the member XPath).
        Assert.NotNull(fake.LastByteArrayReadTagPath);
        Assert.Contains("SomeRecipeTag", fake.LastByteArrayReadTagPath,
            StringComparison.OrdinalIgnoreCase);

        // Value decoded as INT (short), not as DINT (int) or any other type.
        Assert.Equal("INT", result.DataType);
        Assert.IsType<short>(result.Value);
        Assert.Equal(expectedValue, (short)result.Value);
    }

    // ── WriteAsync — typed accessor routing ───────────────────────────────────

    /// <summary>
    /// KEY ACCEPTANCE TEST: Writing a <see cref="float"/> to a REAL tag must
    /// call <c>SetTagValueREALAsync</c> — not <c>SetTagValueDINTAsync</c> with
    /// a bit-pattern.
    /// </summary>
    [Fact]
    public async Task WriteAsync_RealTagWithFloat_CallsSetTagValueREALAsync()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();

        await svc.WriteAsync("Cmd_SpeedSetpoint", 42.5f);

        Assert.Equal("REAL", fake.LastWriteType);
        // Tag name is translated to XPath form before being forwarded to the SDK.
        Assert.Equal("Controller/Tags/Tag[@Name='Cmd_SpeedSetpoint']", fake.LastWriteTagPath);
    }

    [Fact]
    public async Task WriteAsync_BoolTagWithBool_CallsSetTagValueBOOLAsync()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();

        await svc.WriteAsync("Cmd_MotorStart", true);

        Assert.Equal("BOOL", fake.LastWriteType);
    }

    [Theory]
    [InlineData("BoolTag",  true)]
    [InlineData("SintTag",  (sbyte)(-1))]
    [InlineData("IntTag",   (short)100)]
    [InlineData("DintTag",  42)]
    [InlineData("LintTag",  100L)]
    [InlineData("UsintTag", (byte)255)]
    [InlineData("UintTag",  (ushort)65535)]
    [InlineData("UdintTag", (uint)4294967295u)]
    [InlineData("UlintTag", (ulong)ulong.MaxValue)]
    [InlineData("RealTag",  3.14f)]
    [InlineData("LrealTag", 3.14)]
    public async Task WriteAsync_CorrectTypeForDeclared_RoutesToMatchedAccessor(
        string tagName, object value)
    {
        var (svc, fake) = await OpenedServiceWithAllTypesAsync();

        await svc.WriteAsync(tagName, value);

        // LastWriteType records which typed accessor was called.
        Assert.NotNull(fake.LastWriteType);
    }

    // ── WriteAsync — tag path forwarding ─────────────────────────────────────

    [Fact]
    public async Task WriteAsync_PassesTagNameDirectlyToSdk()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();

        await svc.WriteAsync("Cmd_MotorStart", true);

        // Tag name is translated to XPath form before being forwarded to the SDK.
        Assert.Equal("Controller/Tags/Tag[@Name='Cmd_MotorStart']", fake.LastWriteTagPath);
    }

    // ── WriteAsync — type mismatch (no silent coercion) ───────────────────────

    /// <summary>
    /// KEY TEST: Writing <see cref="int"/> to a REAL tag must raise
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> — not silently coerce the
    /// int to a float (which would produce a wrong bit-pattern).
    /// </summary>
    [Fact]
    public async Task WriteAsync_IntToRealTag_ThrowsTypeMismatch()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        // int is the right type for DINT, not REAL.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_SpeedSetpoint", 42));   // int, not float

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Equal("Cmd_SpeedSetpoint", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_DoubleToRealTag_ThrowsTypeMismatch()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        // double is LREAL, not REAL.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_SpeedSetpoint", 42.0));  // double, not float

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_FloatToBoolTag_ThrowsTypeMismatch()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", 1.0f));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_StringToRealTag_ThrowsTypeMismatch()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_SpeedSetpoint", "100.0"));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    [Theory]
    [InlineData("BoolTag",  42)]        // int for BOOL
    [InlineData("SintTag",  42)]        // int for SINT
    [InlineData("IntTag",   42)]        // int for INT (should be short)
    [InlineData("DintTag",  3.14f)]     // float for DINT
    [InlineData("LintTag",  3.14f)]     // float for LINT
    [InlineData("UsintTag", 42)]        // int for USINT
    [InlineData("RealTag",  42)]        // int for REAL
    [InlineData("LrealTag", 3.14f)]     // float for LREAL (should be double)
    public async Task WriteAsync_WrongTypeForDeclared_ThrowsTypeMismatch(
        string tagName, object value)
    {
        var (svc, _) = await OpenedServiceWithAllTypesAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync(tagName, value));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Equal(tagName, ex.Error.TagName);
    }

    // ── WriteAsync — TypeMismatch raised before tag lookup ────────────────────

    [Fact]
    public async Task WriteAsync_UnknownTag_ThrowsTagNotFound()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("NoSuch_Tag", true));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Equal("NoSuch_Tag", ex.Error.TagName);
    }

    // ── WriteAsync — member-path write rejection ──────────────────────────────

    /// <summary>
    /// KEY ACCEPTANCE TEST: Writing to a UDT member path (e.g.
    /// <c>"SomeTag.Member"</c>) via the SDK backend must be rejected immediately
    /// with <see cref="TagServiceErrorKind.MemberWriteUnsupported"/> — no
    /// read-modify-write of the parent byte array is attempted.
    /// </summary>
    [Fact]
    public async Task WriteAsync_MemberPath_ReturnsNotSupported()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("SomeUDT.Member", (int)42));

        Assert.Equal(TagServiceErrorKind.MemberWriteUnsupported, ex.Error.Kind);
        Assert.Equal(
            "SDK backend cannot address a UDT member; use backend=cip for member writes",
            ex.Error.Message);
    }

    // ── WriteAsync — closed session ───────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_WhenSessionClosed_ThrowsConnectionFailed()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── ListTagsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTagsAsync_ReturnsTagsFromL5XProject()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var tags = await svc.ListTagsAsync();

        Assert.NotNull(tags);
        var names = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Cmd_MotorStart",    names);
        Assert.Contains("Cmd_SpeedSetpoint", names);
        Assert.Contains("Safety_OK",         names);
    }

    [Fact]
    public async Task ListTagsAsync_IncludesDataType()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var tags = await svc.ListTagsAsync();

        Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t.DataType)));
    }

    [Fact]
    public async Task ListTagsAsync_ReturnsIReadOnlyList()
    {
        var (svc, _) = await OpenedServiceWithDemoAsync();

        var tags = await svc.ListTagsAsync();

        Assert.IsAssignableFrom<IReadOnlyList<TagInfo>>(tags);
    }

    [Fact]
    public async Task ListTagsAsync_WhenSessionClosed_ThrowsConnectionFailed()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            async () => await svc.ListTagsAsync());

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ListTagsAsync_WhenSessionNotOpened_ThrowsConnectionFailed()
    {
        var session = SessionWith(new RecordingFakeLogixProject());
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            async () => await svc.ListTagsAsync());

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── REAL round-trip scenario ──────────────────────────────────────────────

    /// <summary>
    /// End-to-end scenario: write a REAL value as float, read it back as float.
    /// Neither leg should go through a DINT bit-pattern — this is the defect
    /// that <see cref="LibPlcTagService"/> has.
    /// </summary>
    [Fact]
    public async Task RealRoundTrip_WritesThenReadAsFloat()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();
        fake.RealReturnValue = 42.5f;

        // Write 42.5f — must call SetTagValueREALAsync
        await svc.WriteAsync("Cmd_SpeedSetpoint", 42.5f);
        Assert.Equal("REAL", fake.LastWriteType);

        // Read back — must call GetTagValueREALAsync and return float
        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");
        Assert.Equal("REAL", fake.LastReadType);
        Assert.IsType<float>(result.Value);
        Assert.Equal(42.5f, (float)result.Value, tolerance: 0.0001f);
    }

    // ── TagServiceException carries structured error ───────────────────────────

    [Fact]
    public async Task TagServiceException_ErrorProperty_IsPopulated()
    {
        var session = SessionWith(new RecordingFakeLogixProject());
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        // Session is not open — ConnectAsync should throw ConnectionFailed.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            async () => await svc.ConnectAsync("gw", "1,0"));

        Assert.NotNull(ex.Error);
        Assert.NotEmpty(ex.Message);
    }

    // ── OperationMode forwarding ──────────────────────────────────────────────

    /// <summary>
    /// KEY TEST: When <see cref="TypedSdkTagService"/> is constructed with
    /// <see cref="OperationMode.Offline"/>, every <c>GetTagValueXXXAsync</c>
    /// call must pass <see cref="OperationMode.Offline"/> to the SDK.
    /// </summary>
    [Fact]
    public async Task ReadAsync_OfflineMode_PassesOfflineModeToSdk()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject(), OperationMode.Offline);

        await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(OperationMode.Offline, fake.LastReadMode);
    }

    /// <summary>
    /// KEY TEST: When <see cref="TypedSdkTagService"/> is constructed with
    /// <see cref="OperationMode.Online"/>, every <c>GetTagValueXXXAsync</c>
    /// call must pass <see cref="OperationMode.Online"/> to the SDK — this is
    /// the core of the online-controller operation mode feature.
    /// </summary>
    [Fact]
    public async Task ReadAsync_OnlineMode_PassesOnlineModeToSdk()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject(), OperationMode.Online);

        await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(OperationMode.Online, fake.LastReadMode);
    }

    /// <summary>
    /// When <see cref="TypedSdkTagService"/> is constructed with
    /// <see cref="OperationMode.Online"/>, every <c>SetTagValueXXXAsync</c>
    /// call must pass <see cref="OperationMode.Online"/> to the SDK.
    /// </summary>
    [Fact]
    public async Task WriteAsync_OnlineMode_PassesOnlineModeToSdk()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject(), OperationMode.Online);

        await svc.WriteAsync("Cmd_MotorStart", true);

        Assert.Equal(OperationMode.Online, fake.LastWriteMode);
    }

    /// <summary>
    /// When <see cref="TypedSdkTagService"/> is constructed with
    /// <see cref="OperationMode.Offline"/>, every <c>SetTagValueXXXAsync</c>
    /// call must pass <see cref="OperationMode.Offline"/> to the SDK.
    /// </summary>
    [Fact]
    public async Task WriteAsync_OfflineMode_PassesOfflineModeToSdk()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject(), OperationMode.Offline);

        await svc.WriteAsync("Cmd_MotorStart", true);

        Assert.Equal(OperationMode.Offline, fake.LastWriteMode);
    }

    /// <summary>
    /// Default constructor (<c>operationMode</c> omitted) must use
    /// <see cref="OperationMode.Offline"/> — the safe default.
    /// </summary>
    [Fact]
    public async Task ReadAsync_DefaultMode_PassesOfflineModeToSdk()
    {
        var (svc, fake) = await OpenedServiceWithDemoAsync();
        // OpenedServiceWithDemoAsync uses the default constructor (no explicit mode).

        await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(OperationMode.Offline, fake.LastReadMode);
    }

    // ── RxE_COMM_* comm-layer error — in-session tag operations ──────────────
    //
    // When the SDK tag accessor throws an RxE_COMM_* error during a live tag
    // read or write, TypedSdkTagService must surface SdkCommLayerFaulted rather
    // than Unknown.  This is distinct from a dead PLC: if the PLC is genuinely
    // unreachable a TCP probe on port 44818 also fails; if port 44818 is reachable
    // the PLC is alive and only the SDK comm layer needs recovery.

    [Fact]
    public async Task ReadAsync_TagAccessorThrowsRxeCommDtlerror_ThrowsSdkCommLayerFaulted()
    {
        // GetTagValueBOOLAsync throws RxE_COMM_DTLERROR — must surface as
        // SdkCommLayerFaulted, not Unknown or ConnectionFailed.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        // Cmd_MotorStart is a BOOL tag in Demo.L5X — routes to GetTagValueBOOLAsync.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_TagAccessorThrowsRxeCommDtlerror_ErrorMessageQuotesSdkText()
    {
        // The SdkCommLayerFaulted message must include the raw RxE_COMM_* SDK code.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_TagAccessorThrowsRxeCommOther_ThrowsSdkCommLayerFaulted()
    {
        // Any RxE_COMM_ variant from a tag accessor must surface as SdkCommLayerFaulted.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_OTHER - another comm-layer code")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_TagAccessorThrowsRxeCommInInnerException_ThrowsSdkCommLayerFaulted()
    {
        // RxE_COMM_DTLERROR nested in an inner exception must still be recognized —
        // IsCommLayerError walks the full inner-exception chain.
        var inner = new InvalidOperationException("RxE_COMM_DTLERROR - wrapped comm-layer fault");
        var fake  = new RecordingFakeLogixProject
        {
            TagAccessorException = new Exception("SDK operation failed", inner)
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_TagAccessorThrowsRxeCommDtlerror_ThrowsSdkCommLayerFaulted()
    {
        // SetTagValueBOOLAsync throws RxE_COMM_DTLERROR — must surface as
        // SdkCommLayerFaulted, not Unknown or ConnectionFailed.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        // Cmd_MotorStart is a BOOL tag in Demo.L5X — routes to SetTagValueBOOLAsync.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_TagAccessorThrowsRxeCommDtlerror_ErrorMessageQuotesSdkText()
    {
        // The SdkCommLayerFaulted message from a write failure must include the SDK code.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_TagAccessorThrowsRxeCommOther_ThrowsSdkCommLayerFaulted()
    {
        // Any RxE_COMM_ variant during a write must surface as SdkCommLayerFaulted.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_OTHER - another comm-layer code")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_RealTag_TagAccessorThrowsRxeCommDtlerror_ThrowsSdkCommLayerFaulted()
    {
        // RxE_COMM_DTLERROR from a REAL tag accessor (GetTagValueREALAsync) must
        // also surface as SdkCommLayerFaulted — not just BOOL tags.
        var fake    = new RecordingFakeLogixProject
        {
            TagAccessorException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, LoadDemoProject());

        // Cmd_SpeedSetpoint is a REAL tag in Demo.L5X — routes to GetTagValueREALAsync.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_SpeedSetpoint"));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    // ── In-session dead-session detection ───────────
    //
    // When a live session degrades mid-session (LdSdkServer.exe killed under an
    // active session), the SDK throws OperationNotPerformedException with a
    // message like "Unable to complete connection to LdSdkService".
    //
    // That error must surface as ConnectionFailed (naming the service), NOT as
    // TagNotFound — the kind the generic OperationNotPerformedException handler
    // would produce.
    //
    // The same SCM race-avoidance re-query protocol used by SdkConnectFactory
    // applies here: check service state up to three times (0s, 2s, 5s) before
    // choosing the definitive "service down" vs "service faulted" message.

    /// <summary>
    /// Builds a helper for in-session dead-session tests: opens a session
    /// backed by a fake that throws <paramref name="exception"/> on every
    /// tag-accessor call, then wraps it in a TypedSdkTagService with
    /// injectable service-state checkers and a no-op delay.
    /// </summary>
    private static async Task<TypedSdkTagService> ServiceWithTagAccessorExceptionAndServiceState(
        Exception exception,
        bool serviceRunning,
        Func<bool>? processRunning = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var fake    = new RecordingFakeLogixProject { TagAccessorException = exception };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        return new TypedSdkTagService(
            session,
            LoadDemoProject(),
            operationMode:             OperationMode.Offline,
            ldSdkServiceRunning:       () => serviceRunning,
            ldSdkServerProcessRunning: processRunning ?? (() => true),
            delay:                     delay ?? ((_, _) => Task.CompletedTask));
    }

    // ── "Unable to complete connection to LdSdkService" + service STOPPED ────

    [Fact]
    public async Task ReadAsync_UnableToCompleteConnection_ServiceStopped_ThrowsConnectionFailed()
    {
        // OperationNotPerformedException("Unable to complete connection to LdSdkService")
        // with service stopped → ConnectionFailed (NOT TagNotFound).
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_UnableToCompleteConnection_ServiceStopped_ErrorNamesService()
    {
        // The error message must name LdSdkService as the confirmed-down service.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_UnableToCompleteConnection_ServiceStopped_ErrorStatesNotRunning()
    {
        // The error message must state the service is "not running".
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_UnableToCompleteConnection_ServiceStopped_ErrorIsNotTagNotFound()
    {
        // Must NOT be TagNotFound — that is the wrong diagnosis for a dead service.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.NotEqual(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    // ── "Unable to complete connection to LdSdkService" + service RUNNING ────

    [Fact]
    public async Task ReadAsync_UnableToCompleteConnection_ServiceRunning_ThrowsConnectionFailed()
    {
        // When the service appears running but the session still died, surface
        // ConnectionFailed with the service-faulted form.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_UnableToCompleteConnection_ServiceRunning_ErrorNamesService()
    {
        // Service-faulted form must still name LdSdkService.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── "Could not reach LdSdkService" dead-session indicator ────────────────

    [Fact]
    public async Task ReadAsync_CouldNotReachLdSdkService_ServiceStopped_ThrowsConnectionFailed()
    {
        // "Could not reach LdSdkService" from a tag accessor with service stopped
        // → ConnectionFailed, not TagNotFound.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Could not reach LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Process absence treated as "not running" independently of SCM ─────────

    [Fact]
    public async Task ReadAsync_ProcessAbsent_ScmRunning_ThrowsConnectionFailed_NotRunning()
    {
        // SCM says RUNNING but LdSdkServer.exe process is absent →
        // treat as "not running" (process presence check covers the SCM race).
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: true,
            processRunning: () => false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SCM race-avoidance re-query — in-session path ────────────────────────

    [Fact]
    public async Task ReadAsync_ScmRequery_Q1Running_Q2NotRunning_ThrowsConnectionFailed_NotRunning()
    {
        // Q1=running, Q2=not-running: re-query must detect the down state and
        // emit "not running", not the service-faulted form.
        int callCount = 0;
        bool[] sequence = [true, false];
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: true,  // ignored — overridden by ldSdkServiceRunning below
            delay: (_, _) => Task.CompletedTask);

        // Rebuild with explicit sequence (ServiceWithTagAccessorExceptionAndServiceState
        // sets serviceRunning to a constant; for sequence tests, build directly).
        var fake2    = new RecordingFakeLogixProject
        {
            TagAccessorException = new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath")
        };
        var session2 = SessionWith(fake2);
        await session2.OpenAsync("dummy.ACD");
        var svc2 = new TypedSdkTagService(
            session2,
            LoadDemoProject(),
            operationMode:             OperationMode.Offline,
            ldSdkServiceRunning:       () => sequence[Math.Min(callCount++, sequence.Length - 1)],
            ldSdkServerProcessRunning: () => true,
            delay:                     (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc2.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Regular OperationNotPerformedException (no dead-session) → TagNotFound ──

    [Fact]
    public async Task ReadAsync_RegularOperationNotPerformed_StillTagNotFound()
    {
        // A regular OperationNotPerformedException (no dead-session indicator message)
        // must still map to TagNotFound — the dead-session guard must not intercept it.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException("Tag path does not exist", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    // ── Write path: same dead-session detection ───────────────────────────────

    [Fact]
    public async Task WriteAsync_UnableToCompleteConnection_ServiceStopped_ThrowsConnectionFailed()
    {
        // WriteAsync must also detect dead-session indicators — not just ReadAsync.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_UnableToCompleteConnection_ServiceStopped_ErrorStatesNotRunning()
    {
        // Write path: error message must state "not running".
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_UnableToCompleteConnection_ServiceStopped_IsNotTagNotFound()
    {
        // Write path: must NOT be TagNotFound — dead service, not a missing tag.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationNotPerformedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.NotEqual(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    // ── OperationFailedException — shared classifier ──
    //
    // OperationFailedException and OperationNotPerformedException are sibling types
    // (both inherit from ProjectException — not from each other).  The shared
    // classifier must intercept OperationFailedException carrying either a dead-
    // session indicator ('Unable to complete connection to LdSdkService',
    // 'Could not reach LdSdkService') or an RxE_COMM_* comm-layer code and route
    // it to BuildDeadSessionExceptionForInSessionAsync — the same discriminator
    // as the connect path.
    //
    // OperationFailedException WITHOUT either marker continues to map to Unknown.

    // ── OperationFailedException + RxE_COMM_* — service STOPPED ─────────────────

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithRxeComm_ServiceStopped_ThrowsConnectionFailed()
    {
        // OperationFailedException("RxE_COMM_DTLERROR") with service stopped →
        // ConnectionFailed (via dead-session discriminator), NOT Unknown or SdkCommLayerFaulted.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithRxeComm_ServiceStopped_ErrorStatesNotRunning()
    {
        // Error message must state "not running" — confirms service-state discriminator ran.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithRxeComm_ServiceStopped_IsNotUnknown()
    {
        // Must NOT be Unknown — the classifier ran and chose ConnectionFailed.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.NotEqual(TagServiceErrorKind.Unknown, ex.Error.Kind);
    }

    // ── OperationFailedException + RxE_COMM_* — service RUNNING ─────────────────

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithRxeComm_ServiceRunning_ThrowsConnectionFailed()
    {
        // Service running but OperationFailedException("RxE_COMM_DTLERROR") →
        // ConnectionFailed with service-faulted form.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithRxeComm_ServiceRunning_ErrorNamesService()
    {
        // Service-faulted form: error must name LdSdkService.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── OperationFailedException + dead-session indicator — service STOPPED ───────

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithDeadSession_ServiceStopped_ThrowsConnectionFailed()
    {
        // OperationFailedException with 'Unable to complete connection to LdSdkService'
        // and service stopped → ConnectionFailed (not Unknown).
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithDeadSession_ServiceStopped_ErrorStatesNotRunning()
    {
        // Error must state "not running".
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_OperationFailedException_WithDeadSession_ServiceStopped_IsNotUnknown()
    {
        // Must NOT be Unknown.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException(
                "Unable to complete connection to LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.NotEqual(TagServiceErrorKind.Unknown, ex.Error.Kind);
    }

    // ── OperationFailedException + plain error (no classifier match) → Unknown ───

    [Fact]
    public async Task ReadAsync_OperationFailedException_PlainError_ThrowsUnknown()
    {
        // A plain OperationFailedException (no dead-session indicator, no RxE_COMM_*)
        // must still map to Unknown — the classifier must not over-intercept.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("Some unrelated SDK failure", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.Unknown, ex.Error.Kind);
    }

    // ── Write path: same OperationFailedException detection ──────────────────────

    [Fact]
    public async Task WriteAsync_OperationFailedException_WithRxeComm_ServiceStopped_ThrowsConnectionFailed()
    {
        // WriteAsync: OperationFailedException("RxE_COMM_DTLERROR") with service stopped →
        // ConnectionFailed (same as ReadAsync — both paths apply the classifier).
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_OperationFailedException_WithRxeComm_ServiceStopped_ErrorStatesNotRunning()
    {
        // Write path: error must state "not running".
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("RxE_COMM_DTLERROR - comm layer fault", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_OperationFailedException_WithDeadSession_ServiceStopped_ThrowsConnectionFailed()
    {
        // Write path: OperationFailedException with dead-session indicator →
        // ConnectionFailed (not Unknown).
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException(
                "Could not reach LdSdkService", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_OperationFailedException_PlainError_ThrowsUnknown()
    {
        // Write path: plain OperationFailedException (no classifier match) → Unknown.
        var svc = await ServiceWithTagAccessorExceptionAndServiceState(
            new OperationFailedException("Some unrelated SDK failure", "SomePath"),
            serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.Unknown, ex.Error.Kind);
    }

    // ── Type resolution without the L5Sharp project index ──────────

    /// <summary>
    /// Bench-shaped project: an AOI rung (MAS with "Units per sec2" / "% of Time") that
    /// L5Sharp's whole-project index cannot tokenise, plus a controller REAL tag, a UDT tag
    /// and a program-scoped tag.
    /// </summary>
    private const string UnparsableAoiRungL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="Drive" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Speed" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Count" DataType="DINT" Dimension="0" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions>
              <AddOnInstructionDefinition Name="MoveAxis" Class="Standard" Revision="1.0" Vendor="" ExecutePrescan="false" ExecutePostscan="false" ExecuteEnableInFalse="false" CreatedDate="2024-01-01T00:00:00.000Z" CreatedBy="t" EditedDate="2024-01-01T00:00:00.000Z" EditedBy="t" SoftwareRevision="v36.00">
                <Parameters>
                  <Parameter Name="EnableIn" TagType="Base" DataType="BOOL" Usage="Input" Radix="Decimal" Required="false" Visible="false" ExternalAccess="Read Only" />
                  <Parameter Name="EnableOut" TagType="Base" DataType="BOOL" Usage="Output" Radix="Decimal" Required="false" Visible="false" ExternalAccess="Read Only" />
                  <Parameter Name="Speed" TagType="Base" DataType="REAL" Usage="Input" Radix="Float" Required="true" Visible="true" ExternalAccess="Read/Write" />
                </Parameters>
                <LocalTags />
                <Routines>
                  <Routine Name="Logic" Type="RLL">
                    <RLLContent>
                      <Rung Number="0" Type="N"><Text><![CDATA[MAS(Axis1,MasMotion,0,Units per sec2,100,Units per sec2,0,0,% of Time,0,0,% of Time,0,0);]]></Text></Rung>
                    </RLLContent>
                  </Routine>
                </Routines>
              </AddOnInstructionDefinition>
            </AddOnInstructionDefinitions>
            <Tags>
              <Tag Name="Cmd_Speed" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="REAL" Radix="Float">
                <Data Format="L5K"><![CDATA[0.0]]></Data>
              </Tag>
              <Tag Name="Cmd_Drive" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="Drive">
                <Data Format="L5K"><![CDATA[[0.0,0]]]></Data>
              </Tag>
              <Tag Name="Cmd_Timer" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="TIMER" />
              <Tag Name="Cmd_Counter" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="COUNTER" />
              <Tag Name="Cmd_Axis" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="MoveAxis" />
            </Tags>
            <Programs>
              <Program Name="Main" TestEdits="false" MainRoutineName="MainRoutine" Disabled="false" UseAsFolder="false">
                <Tags>
                  <Tag Name="Prog_Speed" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="REAL" Radix="Float">
                    <Data Format="L5K"><![CDATA[0.0]]></Data>
                  </Tag>
                  <Tag Name="Prog_Timer" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="TIMER" />
                </Tags>
                <Routines />
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// A project whose AOI rung the L5Sharp index cannot tokenise still resolves declared
    /// types for a controller scalar, a UDT member and a program-scoped tag.
    /// </summary>
    [Fact]
    public async Task ReadTypeResolution_ProjectWithUnparsableAoiRung()
    {
        var fake = new RecordingFakeLogixProject { RealReturnValue = 1.5f };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, L5X.Parse(UnparsableAoiRungL5Xml));

        // Scalar controller tag.
        var scalar = await svc.ReadAsync("Cmd_Speed");
        Assert.Equal("REAL", scalar.DataType);
        Assert.Equal(1.5f, scalar.Value);

        // Program-scoped tag.
        var prog = await svc.ReadAsync("Program:Main.Prog_Speed");
        Assert.Equal("REAL", prog.DataType);
        Assert.Equal(1.5f, prog.Value);

        // UDT member (Count: DINT at offset 4 of Drive).
        var bytes = new byte[8];
        BitConverter.GetBytes(7).CopyTo(bytes, 4);
        fake.ByteArrayReturnValue = ByteString.CopyFrom(bytes);
        var member = await svc.ReadAsync("Cmd_Drive.Count");
        Assert.Equal("DINT", member.DataType);
        Assert.Equal(7, member.Value);

        // Unknown tag still reports TagNotFound rather than an index failure.
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("NoSuchTag"));
        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    /// <summary>
    /// Member-path type resolution on the unparsable-rung project covers predefined types
    /// (TIMER, COUNTER), AOI parameters, UDT members, bit selects and program-scoped tags,
    /// all without the L5Sharp index.
    /// </summary>
    [Theory]
    [InlineData("Cmd_Timer.ACC", "DINT")]
    [InlineData("Cmd_Timer.DN", "BOOL")]
    [InlineData("Cmd_Counter.PRE", "DINT")]
    [InlineData("Cmd_Axis.Speed", "REAL")]
    [InlineData("Cmd_Axis.EnableOut", "BOOL")]
    [InlineData("Cmd_Drive.Speed", "REAL")]
    [InlineData("Cmd_Drive.Count.3", "BOOL")]
    [InlineData("Program:Main.Prog_Timer.ACC", "DINT")]
    public async Task MemberTypeResolution_PredefinedAoiAndUdtMembers(string tag, string expected)
    {
        var session = SessionWith(new RecordingFakeLogixProject());
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, L5X.Parse(UnparsableAoiRungL5Xml));

        var resolve = typeof(TypedSdkTagService).GetMethod(
            "ResolveDeclaredType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(expected, (string)resolve.Invoke(svc, new object[] { tag })!);
    }

    /// <summary>An unknown member on a predefined or AOI type reports TagNotFound.</summary>
    [Theory]
    [InlineData("Cmd_Timer.Nope")]
    [InlineData("Cmd_Axis.Nope")]
    public async Task MemberTypeResolution_UnknownMember_IsTagNotFound(string tag)
    {
        var session = SessionWith(new RecordingFakeLogixProject());
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, L5X.Parse(UnparsableAoiRungL5Xml));

        var resolve = typeof(TypedSdkTagService).GetMethod(
            "ResolveDeclaredType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var ex = Assert.IsType<TagServiceException>(
            Assert.Throws<System.Reflection.TargetInvocationException>(
                () => resolve.Invoke(svc, new object[] { tag })).InnerException);
        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }
}
#endif
