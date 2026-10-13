#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using Xunit;

using ControllerMode      = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState      = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType            = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode       = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget      = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode   = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions    = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption       = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent        = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode         = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate   = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;

namespace LogixMcp.Tests;

// ── CreateRoutineFake ─────────────────────────────────────────────────────────

/// <summary>One routine held by <see cref="CreateRoutineFake"/>.</summary>
/// <param name="Name">Routine name.</param>
/// <param name="Type">Routine type (<c>RLL</c>, <c>ST</c>).</param>
/// <param name="RungTypes">The L5X <c>Type</c> of each rung (<c>N</c>, <c>I</c>, ...).</param>
/// <param name="SelfClosed">When true, the routine exports as a self-closed element.</param>
internal sealed record FakeRoutine(string Name, string Type, string[] RungTypes, bool SelfClosed = false);

/// <summary>
/// <see cref="ILogixProject"/> stand-in holding one program's routines. It exports the program and
/// each routine from that state and, on a named-object import, records the call and (by default) adds
/// the routine, so a re-export shows it.
/// </summary>
internal sealed class CreateRoutineFake : ILogixProject
{
    public const string Program = "Prog";

    /// <summary>The name the fake's single program exports with (default <see cref="Program"/>).</summary>
    public string ProgramName { get; set; } = Program;

    /// <summary>When true, program/routine XPaths match <see cref="ProgramName"/> ignoring case.</summary>
    public bool CaseInsensitiveProgramExport { get; set; }

    private bool IsProgramPath(string xPath, string suffix = "")
    {
        var expected = $"Controller/Programs/Program[@Name='{ProgramName}']{suffix}";
        return string.Equals(xPath, expected,
            CaseInsensitiveProgramExport ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public List<FakeRoutine> Routines { get; } = new();
    public List<string> ExportXPaths { get; } = new();
    public List<(string XPath, string TargetName, PartialImportOption Option, string File)> Imports { get; } = new();
    public int RungImportCount { get; private set; }

    /// <summary>When false the import returns success but adds nothing.</summary>
    public bool ImportAddsRoutine { get; set; } = true;

    /// <summary>When true the import reports an importer error and throws.</summary>
    public bool ImportThrows { get; set; }

    private readonly List<IOperationEvent> _handlers = new();

    public void Dispose() { }

    private string RoutineElement(FakeRoutine r, string use)
    {
        if (r.SelfClosed)
            return $"<Routine Use=\"{use}\" Name=\"{r.Name}\" Type=\"{r.Type}\"/>";
        if (r.Type != "RLL")
            return $"<Routine Use=\"{use}\" Name=\"{r.Name}\" Type=\"{r.Type}\"><STContent><Line Number=\"0\"><![CDATA[x := 1;]]></Line></STContent></Routine>";
        var rungs = string.Concat(r.RungTypes.Select((t, i) =>
            $"<Rung Number=\"{i}\" Type=\"{t}\"><Text><![CDATA[XIC(Big_Tag)OTE(Out_{i});]]></Text></Rung>"));
        return $"<Routine Use=\"{use}\" Name=\"{r.Name}\" Type=\"RLL\"><Description><![CDATA[old text]]></Description><RLLContent>{rungs}</RLLContent></Routine>";
    }

    /// <summary>Size of the context tag's data body; a large value makes the raw export big.</summary>
    public int ContextTagPayloadChars { get; set; } = 1;

    private string BigTag =>
        "<Tag Name=\"Big_Tag\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" ExternalAccess=\"Read/Write\">" +
        "<Data Format=\"L5K\"><![CDATA[" + new string('0', ContextTagPayloadChars) + "]]></Data></Tag>";

    private string ProgramXml() =>
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.01\" TargetName=\"" + ProgramName + "\" TargetType=\"Program\" ContainsContext=\"true\" Owner=\"t\">" +
        "<Controller Use=\"Context\" Name=\"Ctl\"><DataTypes Use=\"Context\"/><Tags Use=\"Context\">" + BigTag + "</Tags>" +
        "<Programs Use=\"Context\"><Program Use=\"Target\" Name=\"" + ProgramName + "\" TestEdits=\"false\" MainRoutineName=\"Main\">" +
        "<Tags>" + BigTag + "</Tags><Routines>" +
        string.Concat(Routines.Select(r => RoutineElement(r, "Target"))) +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private string RoutineXml(FakeRoutine r) =>
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.01\" TargetName=\"" + r.Name +
        "\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\" Owner=\"t\">" +
        "<Controller Use=\"Context\" Name=\"Ctl\"><DataTypes Use=\"Context\"/><Tags Use=\"Context\">" + BigTag + "</Tags>" +
        "<Programs Use=\"Context\"><Program Use=\"Context\" Name=\"" + ProgramName + "\"><Routines Use=\"Context\">" +
        RoutineElement(r, "Target") + "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    public Task PartialExportToXmlFileAsync(string xPath, string filePath, CancellationToken ct = default)
    {
        ExportXPaths.Add(xPath);
        if (IsProgramPath(xPath) || xPath == "Controller/Programs")
        {
            File.WriteAllText(filePath, ProgramXml());
            return Task.CompletedTask;
        }
        var routine = Routines.FirstOrDefault(r =>
            IsProgramPath(xPath, $"/Routines/Routine[@Name='{r.Name}']"));
        if (routine is null)
            throw new OperationFailedException("No such object.", xPath);
        File.WriteAllText(filePath, RoutineXml(routine));
        return Task.CompletedTask;
    }

    public Task PartialImportWithTargetFromXmlFileAsync(
        string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption,
        CancellationToken ct = default)
    {
        Imports.Add((xPath, targetName, onlineImportOption, File.ReadAllText(xmlFileToImport)));
        if (ImportThrows)
        {
            foreach (var h in _handlers.ToList())
                h.Error("x.ACD", "<Exception Severity=\"Error\" Mnemonic=\"XMLSrv_E_BAD\" HexCode=\"80042cae\"><![CDATA[envelope refused]]></Exception>");
            throw new OperationFailedException("Import failed.", xPath);
        }
        if (ImportAddsRoutine)
            Routines.Add(new FakeRoutine(targetName, "RLL", Array.Empty<string>()));
        return Task.CompletedTask;
    }

    public Task PartialImportRungsFromXmlFileAsync(string xPath, uint insertPosition, uint replaceCount, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        RungImportCount++;
        return Task.CompletedTask;
    }

    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default) => Task.FromResult(ConnectedState.Online);
    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) => Task.FromResult(ControllerMode.Run);
    public void AddEventHandler(IOperationEvent handler) => _handlers.Add(handler);
    public void RemoveEventHandler(IOperationEvent handler) => _handlers.Remove(handler);
    public void ClearEventHandlers() => _handlers.Clear();

    // ── All other ILogixProject members — not used by logix_create_routine ──────
    public Task<string> GetCommunicationsPathAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task SetCommunicationsPathAsync(string projectPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ChangeControllerTypeAsync(string processorTypeName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveAsAsync(string savePath, bool force, bool detailedL5x, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task DownloadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task ChangeControllerModeAsync(RequestedControllerMode mode, CancellationToken ct = default) => throw new NotImplementedException();
    public Task BuildAsync(RequestedBuildTarget target, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UploadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task StoreImageOnSDCardAsync(RequestedLoadEvent loadEvent, RequestedLoadMode loadMode, AutomaticFirmwareUpdate afuSetting, string imageName, string imageNote) => throw new NotImplementedException();
    public Task LoadImageFromSDCardAsync() => throw new NotImplementedException();

    public Task<ByteString> GetTagValueAsync(string tagPath, OperationMode mode, DataType type) => throw new NotImplementedException();
    public Task<bool>   GetTagValueBOOLAsync(string tagPath, OperationMode mode)  => throw new NotImplementedException();
    public Task<sbyte>  GetTagValueSINTAsync(string tagPath, OperationMode mode)  => throw new NotImplementedException();
    public Task<short>  GetTagValueINTAsync(string tagPath, OperationMode mode)   => throw new NotImplementedException();
    public Task<int>    GetTagValueDINTAsync(string tagPath, OperationMode mode)  => throw new NotImplementedException();
    public Task<long>   GetTagValueLINTAsync(string tagPath, OperationMode mode)  => throw new NotImplementedException();
    public Task<byte>   GetTagValueUSINTAsync(string tagPath, OperationMode mode) => throw new NotImplementedException();
    public Task<ushort> GetTagValueUINTAsync(string tagPath, OperationMode mode)  => throw new NotImplementedException();
    public Task<uint>   GetTagValueUDINTAsync(string tagPath, OperationMode mode) => throw new NotImplementedException();
    public Task<ulong>  GetTagValueULINTAsync(string tagPath, OperationMode mode) => throw new NotImplementedException();
    public Task<string> GetTagValueSTRINGAsync(string tagPath, OperationMode mode) => throw new NotImplementedException();
    public Task<float>  GetTagValueREALAsync(string tagPath, OperationMode mode)  => throw new NotImplementedException();
    public Task<double> GetTagValueLREALAsync(string tagPath, OperationMode mode) => throw new NotImplementedException();

    public Task SetTagValueAsync(string tagPath, OperationMode mode, byte[] tagValue, DataType type) => throw new NotImplementedException();
    public Task SetTagValueBOOLAsync(string tagPath, OperationMode mode, bool tagValue)    => throw new NotImplementedException();
    public Task SetTagValueSINTAsync(string tagPath, OperationMode mode, sbyte tagValue)   => throw new NotImplementedException();
    public Task SetTagValueINTAsync(string tagPath, OperationMode mode, short tagValue)    => throw new NotImplementedException();
    public Task SetTagValueDINTAsync(string tagPath, OperationMode mode, int tagValue)     => throw new NotImplementedException();
    public Task SetTagValueLINTAsync(string tagPath, OperationMode mode, long tagValue)    => throw new NotImplementedException();
    public Task SetTagValueUSINTAsync(string tagPath, OperationMode mode, byte tagValue)   => throw new NotImplementedException();
    public Task SetTagValueUINTAsync(string tagPath, OperationMode mode, ushort tagValue)  => throw new NotImplementedException();
    public Task SetTagValueUDINTAsync(string tagPath, OperationMode mode, uint tagValue)   => throw new NotImplementedException();
    public Task SetTagValueULINTAsync(string tagPath, OperationMode mode, ulong tagValue)  => throw new NotImplementedException();
    public Task SetTagValueSTRINGAsync(string tagPath, OperationMode mode, string tagValue) => throw new NotImplementedException();
    public Task SetTagValueREALAsync(string tagPath, OperationMode mode, float tagValue)   => throw new NotImplementedException();
    public Task SetTagValueLREALAsync(string tagPath, OperationMode mode, double tagValue) => throw new NotImplementedException();

    public Task PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default) => throw new NotImplementedException();

    public Task GoOnlineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task GoOfflineAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public Task<string> GetSafetyNetworkNumberAsync(string moduleName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> IsSafetyLockedAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task SafetyLockAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SafetyUnlockAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SetSafetyLockPasswordAsync(string newPassword, string oldPassword, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SetSafetyUnlockPasswordAsync(string newPassword, string oldPassword, CancellationToken ct = default) => throw new NotImplementedException();
    public Task GenerateSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> GetSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public Task ProtectWithLicenseAsync(string xPath, uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ProtectWithLicenseAsync(IEnumerable<string> xPaths, uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ProtectAllWithLicenseAsync(uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ProtectAllWithPasswordAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnprotectAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task ProtectWithPasswordAsync(string xPath, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ProtectWithPasswordAsync(IEnumerable<string> xPaths, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnprotectAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnprotectAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public Task LockExecutableAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task LockExecutableAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> IsExecutableLockedAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnlockExecutableAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnlockExecutableAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public Task LockAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task UnlockAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> IsExecutableProtectedAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string[]> GetAllExecutablesAsync(CancellationToken ct = default) => throw new NotImplementedException();
}

// ── CreateRoutineHandlerTests ─────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="CreateRoutineHandler"/> (the <c>logix_create_routine</c> handler), run
/// against <see cref="CreateRoutineFake"/>. No SDK, controller or Studio is involved.
/// </summary>
[Collection("AuditLoggerDir")]
public sealed class CreateRoutineHandlerTests : IDisposable
{
    private const string ProgramXPath = "Controller/Programs/Program[@Name='Prog']";
    private readonly string _tempDir;

    /// <summary>Redirects the audit log to a temp directory.</summary>
    public CreateRoutineHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"createroutine-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    /// <summary>Restores the audit log directory and removes the temp directory.</summary>
    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<LogixProjectSession> OpenAsync(CreateRoutineFake fake, bool online = true)
    {
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline = online;
        session.IsCorrelated = online;
        return session;
    }

    private static CreateRoutineFake FakeWithMain(params string[] rungTypes)
    {
        var fake = new CreateRoutineFake();
        fake.Routines.Add(new FakeRoutine("Main", "RLL", rungTypes));
        return fake;
    }

    private static Task<object> RunAsync(
        LogixProjectSession session, string? name = "New_Routine", string? type = "RLL",
        string? description = null, bool confirmed = true, bool liveWrites = true,
        WriteAllowlist? allowlist = null, string program = "Prog") =>
        CreateRoutineHandler.ExecuteAsync(program, name, type, description, confirmed, session, liveWrites, allowlist);

    private string ReadAudit() => File.ReadAllText(Path.Combine(_tempDir, "audit.jsonl"));

    // ── success ───────────────────────────────────────────────────────────────

    /// <summary>A valid call imports once with LeaveEdits and returns xpath, flag and undo note.</summary>
    [Fact]
    public async Task CreateRoutine_Success_ImportsWithLeaveEditsAndReturnsResult()
    {
        var fake = FakeWithMain("N", "N");
        var session = await OpenAsync(fake);

        var result = await RunAsync(session, description: "Hand-made");

        var ok = Assert.IsType<CreateRoutineResult>(result);
        Assert.Equal("created", ok.Status);
        Assert.Equal(ProgramXPath + "/Routines/Routine[@Name='New_Routine']", ok.RoutineXPath);
        Assert.True(ok.CreatedInController);
        Assert.Contains("no routine delete", ok.Undo);
        Assert.Contains("Studio", ok.Undo);
        Assert.Contains("even if later edits are cancelled", ok.Undo);

        var import = Assert.Single(fake.Imports);
        Assert.Equal(ProgramXPath + "/Routines", import.XPath);
        Assert.Equal("New_Routine", import.TargetName);
        Assert.Equal(PartialImportOption.LeaveEdits, import.Option);
        Assert.Equal(0, fake.RungImportCount);

        var envelope = XDocument.Parse(import.File);
        var routine = Assert.Single(envelope.Descendants("Routine"));
        Assert.Equal("New_Routine", (string?)routine.Attribute("Name"));
        Assert.Equal("Target", (string?)routine.Attribute("Use"));
        Assert.Empty(routine.Element("RLLContent")!.Elements());
        Assert.Equal("Hand-made", routine.Element("Description")!.Value);
        Assert.Contains(fake.Routines, r => r.Name == "New_Routine");
    }

    /// <summary>The import envelope stays under 30 kB even when the context holds a huge tag.</summary>
    [Fact]
    public async Task CreateRoutine_HugeContext_EnvelopeStaysUnder30kB()
    {
        var fake = FakeWithMain("N");
        fake.ContextTagPayloadChars = 200_000;
        var session = await OpenAsync(fake);

        var result = await RunAsync(session);

        Assert.IsType<CreateRoutineResult>(result);
        var import = Assert.Single(fake.Imports);
        Assert.True(Encoding.UTF8.GetByteCount(import.File) < CreateRoutineHandler.MaxEnvelopeBytes);
    }

    /// <summary>The routine with the fewest rungs is the one exported for the envelope.</summary>
    [Fact]
    public async Task CreateRoutine_UsesSmallestRllRoutineAsEnvelopeSource()
    {
        var fake = FakeWithMain("N", "N", "N");
        fake.Routines.Add(new FakeRoutine("Tiny", "RLL", new[] { "N" }));
        var session = await OpenAsync(fake);

        await RunAsync(session);

        Assert.Contains(ProgramXPath + "/Routines/Routine[@Name='Tiny']", fake.ExportXPaths);
        Assert.DoesNotContain(ProgramXPath + "/Routines/Routine[@Name='Main']", fake.ExportXPaths);
    }

    // ── envelope variants ─────────────────────────────────────────────────────

    /// <summary>A self-closed routine export is opened into a full element with an empty RLLContent.</summary>
    [Fact]
    public async Task CreateRoutine_EnvelopeFromSelfClosedRoutine_HasOpenEmptyRllContent()
    {
        var fake = new CreateRoutineFake();
        fake.Routines.Add(new FakeRoutine("Main", "RLL", Array.Empty<string>(), SelfClosed: true));
        var session = await OpenAsync(fake);

        var result = await RunAsync(session);

        var shown = result is CreateRoutineError e ? $"{e.Kind}: {e.Error}" : "ok";
        Assert.True(result is CreateRoutineResult, shown);
        var import = Assert.Single(fake.Imports);
        var routine = Assert.Single(XDocument.Parse(import.File).Descendants("Routine"));
        var rll = routine.Element("RLLContent");
        Assert.NotNull(rll);
        Assert.Empty(rll!.Elements());
        Assert.Equal("New_Routine", (string?)routine.Attribute("Name"));
    }

    /// <summary>A program with no RLL routine gets its envelope from the program export.</summary>
    [Fact]
    public async Task CreateRoutine_ProgramWithoutRllRoutine_EnvelopeFromProgramExport()
    {
        var fake = new CreateRoutineFake();
        fake.Routines.Add(new FakeRoutine("StMain", "ST", Array.Empty<string>()));
        var session = await OpenAsync(fake);

        var result = await RunAsync(session);

        Assert.IsType<CreateRoutineResult>(result);
        var import = Assert.Single(fake.Imports);
        Assert.Equal(PartialImportOption.LeaveEdits, import.Option);
        var routines = XDocument.Parse(import.File).Descendants("Routine").ToList();
        var only = Assert.Single(routines);
        Assert.Equal("New_Routine", (string?)only.Attribute("Name"));
        Assert.Equal("RLL", (string?)only.Attribute("Type"));
        Assert.Empty(only.Element("RLLContent")!.Elements());
        Assert.DoesNotContain("StMain", import.File);
        Assert.Equal(new[] { ProgramXPath }, fake.ExportXPaths.Distinct().ToArray());
    }

    // ── refusals ──────────────────────────────────────────────────────────────

    /// <summary>A program with pending edits is refused before any import.</summary>
    [Theory]
    [InlineData("I")]
    [InlineData("e")]
    [InlineData("rN")]
    public async Task CreateRoutine_PendingEdits_RefusesWithPendingEditsBlockImport(string rungType)
    {
        var fake = FakeWithMain("N", rungType);
        var session = await OpenAsync(fake);

        var result = await RunAsync(session);

        var err = Assert.IsType<CreateRoutineError>(result);
        Assert.Equal("PendingEditsBlockImport", err.Kind);
        Assert.False(err.ImportAttempted);
        Assert.Contains("Main", err.Routines!);
        Assert.Empty(fake.Imports);
        Assert.DoesNotContain(fake.Routines, r => r.Name == "New_Routine");
    }

    /// <summary>A name that already exists in the program is refused, case-insensitively.</summary>
    [Theory]
    [InlineData("Main")]
    [InlineData("MAIN")]
    public async Task CreateRoutine_NameCollision_RefusesWithRoutineAlreadyExists(string name)
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var result = await RunAsync(session, name: name);

        var err = Assert.IsType<CreateRoutineError>(result);
        Assert.Equal("RoutineAlreadyExists", err.Kind);
        Assert.False(err.ImportAttempted);
        Assert.Empty(fake.Imports);
    }

    /// <summary>Names that break the identifier rules are refused before any SDK call.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1Start")]
    [InlineData("Has Space")]
    [InlineData("Double__Underscore")]
    [InlineData("Quote'd")]
    [InlineData("Slash/Name")]
    public async Task CreateRoutine_InvalidName_RefusesWithInvalidRoutineName(string? name)
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var result = await RunAsync(session, name: name);

        var err = Assert.IsType<CreateRoutineError>(result);
        Assert.Equal("InvalidRoutineName", err.Kind);
        Assert.Empty(fake.Imports);
        Assert.Empty(fake.ExportXPaths);
    }

    /// <summary>Reserved words are refused with InvalidRoutineName, and the message names the word.</summary>
    [Theory]
    [InlineData("AND")]
    [InlineData("IF")]
    [InlineData("END_IF")]
    [InlineData("and")]
    public async Task CreateRoutine_ReservedWord_RefusesWithInvalidRoutineName(string name)
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var result = await RunAsync(session, name: name);

        var err = Assert.IsType<CreateRoutineError>(result);
        Assert.Equal("InvalidRoutineName", err.Kind);
        Assert.Contains(name, err.Error);
        Assert.Contains("reserved", err.Error);
        Assert.Empty(fake.Imports);
        Assert.Empty(fake.ExportXPaths);
    }

    /// <summary>Names Logix accepts, though they look like keywords, are not refused.</summary>
    [Theory]
    [InlineData("MOVE")]
    [InlineData("_Lead")]
    public async Task CreateRoutine_NonReservedLookalike_IsAccepted(string name)
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var result = await RunAsync(session, name: name);

        Assert.IsType<CreateRoutineResult>(result);
    }

    /// <summary>A 40-character name is accepted; a 41-character name is refused.</summary>
    [Fact]
    public async Task CreateRoutine_NameLength_LimitIs40Characters()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var tooLong = await RunAsync(session, name: new string('A', 41));
        Assert.Equal("InvalidRoutineName", Assert.IsType<CreateRoutineError>(tooLong).Kind);

        var exact = await RunAsync(session, name: new string('A', 40));
        Assert.IsType<CreateRoutineResult>(exact);
    }

    /// <summary>Only RLL is supported in this version.</summary>
    [Theory]
    [InlineData("ST")]
    [InlineData("FBD")]
    public async Task CreateRoutine_UnsupportedType_Refused(string type)
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session, type: type));

        Assert.Equal("UnsupportedRoutineType", err.Kind);
        Assert.Empty(fake.Imports);
    }

    /// <summary>confirmedByUser=false is refused before any SDK call.</summary>
    [Fact]
    public async Task CreateRoutine_NotConfirmed_RefusesWithConfirmationRequired()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session, confirmed: false));

        Assert.Equal("ConfirmationRequired", err.Kind);
        Assert.Empty(fake.Imports);
        Assert.Empty(fake.ExportXPaths);
    }

    /// <summary>Without LGX_ENABLE_LIVE_WRITES the call is refused and the refusal is audited.</summary>
    [Fact]
    public async Task CreateRoutine_LiveWritesDisabled_RefusesWithCapabilityDisabled()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var result = await RunAsync(session, liveWrites: false);

        Assert.IsType<CapabilityDisabledError>(result);
        Assert.Empty(fake.Imports);
        Assert.Empty(fake.ExportXPaths);
        Assert.Contains("logix_create_routine", ReadAudit());
    }

    /// <summary>A program missing from the write allowlist is refused.</summary>
    [Fact]
    public async Task CreateRoutine_ProgramNotOnAllowlist_Refuses()
    {
        var path = Path.Combine(_tempDir, "allow.txt");
        File.WriteAllText(path, "Other_Program\n");
        var allowlist = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(path));
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session, allowlist: allowlist));

        Assert.Equal("NotOnAllowlist", err.Kind);
        Assert.Empty(fake.Imports);
    }

    /// <summary>A program on the write allowlist is allowed.</summary>
    [Fact]
    public async Task CreateRoutine_ProgramOnAllowlist_Succeeds()
    {
        var path = Path.Combine(_tempDir, "allow.txt");
        File.WriteAllText(path, "Prog\n");
        var allowlist = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(path));
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        Assert.IsType<CreateRoutineResult>(await RunAsync(session, allowlist: allowlist));
    }

    /// <summary>An unknown program is reported and nothing is imported.</summary>
    [Fact]
    public async Task CreateRoutine_UnknownProgram_Refused()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session, program: "Nope"));

        Assert.Equal("ProgramNotFound", err.Kind);
        Assert.Empty(fake.Imports);
    }

    /// <summary>A missing program is ProgramNotFound listing the programs, never ProgramNotReadable.</summary>
    [Fact]
    public async Task CreateRoutine_MissingProgram_ReturnsProgramNotFoundWithProgramList()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session, program: "Nope"));

        Assert.Equal("ProgramNotFound", err.Kind);
        Assert.NotEqual("ProgramNotReadable", err.Kind);
        Assert.Equal(new[] { "Prog" }, err.Programs);
        Assert.Contains("Prog", err.Error);
        Assert.Contains("Nope", err.Error);
        Assert.DoesNotContain("No such object", err.Error);
        Assert.False(err.ImportAttempted);
        Assert.Empty(fake.Imports);
    }

    /// <summary>A program named in other casing resolves to the exported casing in the returned routineXPath.</summary>
    [Fact]
    public async Task CreateRoutine_ProgramInOtherCasing_ReturnsRoutineXPathInExportedCasing()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        // The fake's program export is case-sensitive like the SDK, so "prog" fails the first export.
        var result = Assert.IsType<CreateRoutineResult>(await RunAsync(session, program: "prog"));

        Assert.Equal("Prog", result.Program);
        Assert.Equal("Controller/Programs/Program[@Name='Prog']/Routines/Routine[@Name='New_Routine']", result.RoutineXPath);
    }

    /// <summary>
    /// "mainprogram" is canonicalised to Program[@Name='MainProgram'] even when the first export succeeds
    /// (the SDK matched the name case-insensitively), so the import and returned XPath use the element's Name.
    /// </summary>
    [Fact]
    public async Task CreateRoutine_MainprogramLowercase_CanonicalisedToMainProgramXPath()
    {
        var fake = FakeWithMain("N");
        fake.ProgramName = "MainProgram";
        fake.CaseInsensitiveProgramExport = true;
        var session = await OpenAsync(fake);

        var result = Assert.IsType<CreateRoutineResult>(await RunAsync(session, program: "mainprogram"));

        Assert.Equal("MainProgram", result.Program);
        Assert.Equal("Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='New_Routine']", result.RoutineXPath);
        var import = Assert.Single(fake.Imports);
        Assert.Equal("Controller/Programs/Program[@Name='MainProgram']/Routines", import.XPath);
        Assert.DoesNotContain(fake.ExportXPaths, x => x.Contains("[@Name='mainprogram']"
            ) && x.Contains("/Routines/"));
    }

    // ── offline ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Option (b): an offline session cannot save a created routine, so the call is refused before any
    /// import, and nothing in the response claims a save or a creation.
    /// </summary>
    [Fact]
    public async Task CreateRoutine_Offline_RefusesWithOfflineCreateNotSupported()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake, online: false);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session));

        Assert.Equal("OfflineCreateNotSupported", err.Kind);
        Assert.False(err.ImportAttempted);
        Assert.False(string.IsNullOrWhiteSpace(err.Hint));
        Assert.Contains("online", err.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was saved", err.Error);
        Assert.Empty(fake.Imports);
        Assert.Equal(0, fake.RungImportCount);
        Assert.False(File.Exists(Path.Combine(_tempDir, "audit.jsonl")) &&
                     ReadAudit().Contains("routine_created"));
    }

    /// <summary>The offline refusal still sits behind the live-writes gate.</summary>
    [Fact]
    public async Task CreateRoutine_OfflineWithoutLiveWrites_RefusesWithCapabilityDisabled()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake, online: false);

        Assert.IsType<CapabilityDisabledError>(await RunAsync(session, liveWrites: false));
        Assert.Empty(fake.Imports);
    }

    // ── verification ──────────────────────────────────────────────────────────

    /// <summary>An import that returns success but leaves no routine is CreateNotConfirmed.</summary>
    [Fact]
    public async Task CreateRoutine_ImportDidNotCreate_ReturnsCreateNotConfirmed()
    {
        var fake = FakeWithMain("N");
        fake.ImportAddsRoutine = false;
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session));

        Assert.Equal("CreateNotConfirmed", err.Kind);
        Assert.True(err.ImportAttempted);
        Assert.Single(fake.Imports);
        Assert.Contains("not_confirmed", ReadAudit());
    }

    /// <summary>The verification is a second, fresh export of the program after the import.</summary>
    [Fact]
    public async Task CreateRoutine_Success_ReExportsProgramAfterImport()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        await RunAsync(session);

        var programExports = fake.ExportXPaths.Count(x => x == ProgramXPath);
        Assert.True(programExports >= 2, $"expected a program re-export; saw {programExports}.");
    }

    /// <summary>A failing import is reported as ImportFailed with the importer's log.</summary>
    [Fact]
    public async Task CreateRoutine_ImportThrows_ReturnsImportFailedWithLog()
    {
        var fake = FakeWithMain("N");
        fake.ImportThrows = true;
        var session = await OpenAsync(fake);

        var err = Assert.IsType<CreateRoutineError>(await RunAsync(session));

        Assert.Equal("ImportFailed", err.Kind);
        Assert.True(err.ImportAttempted);
        Assert.NotNull(err.ImportLog);
        Assert.NotEmpty(err.ImportLog!);
    }

    // ── import-mode guard ─────────────────────────────────────────────────────

    /// <summary>LeaveEdits passes the guard.</summary>
    [Fact]
    public void RequireLeaveEdits_LeaveEdits_Passes() =>
        Assert.Equal(PartialImportOption.LeaveEdits,
            CreateRoutineHandler.RequireLeaveEdits(PartialImportOption.LeaveEdits));

    /// <summary>Every other import mode is rejected by the guard.</summary>
    [Fact]
    public void RequireLeaveEdits_AnyOtherOption_Throws()
    {
        var others = Enum.GetValues<PartialImportOption>().Where(o => o != PartialImportOption.LeaveEdits).ToList();
        Assert.NotEmpty(others);
        foreach (var option in others)
            Assert.Throws<InvalidOperationException>(() => CreateRoutineHandler.RequireLeaveEdits(option));
    }

    // ── audit ─────────────────────────────────────────────────────────────────

    /// <summary>A successful create writes a routine_created audit event.</summary>
    [Fact]
    public async Task CreateRoutine_Success_EmitsRoutineCreatedAuditEvent()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        await RunAsync(session);

        var line = ReadAudit().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(l => l.Contains("routine_created"));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("Prog", doc.RootElement.GetProperty("program").GetString());
        Assert.Equal("New_Routine", doc.RootElement.GetProperty("routine").GetString());
        Assert.Equal("created", doc.RootElement.GetProperty("outcome").GetString());
        Assert.True(doc.RootElement.GetProperty("confirmed_by_user").GetBoolean());
    }

    /// <summary>A refusal before the import writes no routine_created event.</summary>
    [Fact]
    public async Task CreateRoutine_Refusal_EmitsNoRoutineCreatedEvent()
    {
        var fake = FakeWithMain("N");
        var session = await OpenAsync(fake);

        await RunAsync(session, name: "Main");

        var auditPath = Path.Combine(_tempDir, "audit.jsonl");
        Assert.False(File.Exists(auditPath) && File.ReadAllText(auditPath).Contains("routine_created"));
    }
}

#endif
