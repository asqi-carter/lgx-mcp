#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using Task = System.Threading.Tasks.Task;

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

using System.Text.Json;

namespace LogixMcp.Tests;

// ── FakePendingEditsProject ─────────────────────────────────────────────────────

/// <summary>
/// <see cref="ILogixProject"/> stub for testing <see cref="PendingEditsHandler"/>
/// and the <c>logix_pending_edits</c> tool wiring in <see cref="LogixSdkTools"/>.
///
/// <para>
/// Adds configurable <see cref="ReadConnectedStateAsync"/> /
/// <see cref="ReadControllerModeAsync"/> behaviour on top of the same
/// export-file-write pattern used by <c>FakeExportProject</c>, plus a shared
/// <see cref="ImportCallCount"/> counter incremented by all three
/// <c>PartialImport*</c> methods so tests can assert the reader never calls any
/// of them (<c>ReadIsSideEffectFree_NoImportCall</c>).
/// </para>
/// </summary>
internal sealed class FakePendingEditsProject : ILogixProject
{
    /// <summary>Content to write to the export file. Required for a successful export.</summary>
    public string? ExportContent { get; set; }

    /// <summary>
    /// Optional per-XPath export content; takes precedence over <see cref="ExportContent"/>
    /// for an exact XPath match.
    /// </summary>
    public Dictionary<string, string> ExportContentByXPath { get; } = new();

    /// <summary>Every xPath passed to <see cref="PartialExportToXmlFileAsync"/>, in call order.</summary>
    public List<string> ExportXPaths { get; } = new();

    /// <summary>When true, <see cref="PartialExportToXmlFileAsync"/> throws <see cref="OperationFailedException"/>.</summary>
    public bool ExportShouldThrow { get; set; }

    /// <summary>Value returned by <see cref="ReadConnectedStateAsync"/>.</summary>
    public ConnectedState ConnectedStateToReturn { get; set; } = ConnectedState.Online;

    /// <summary>When true, <see cref="ReadConnectedStateAsync"/> throws <see cref="OperationFailedException"/>.</summary>
    public bool ThrowOnReadConnectedState { get; set; }

    /// <summary>Value returned by <see cref="ReadControllerModeAsync"/>.</summary>
    public ControllerMode ControllerModeToReturn { get; set; } = ControllerMode.Run;

    /// <summary>When true, <see cref="ReadControllerModeAsync"/> throws <see cref="OperationFailedException"/>.</summary>
    public bool ThrowOnReadControllerMode { get; set; }

    /// <summary>Records the xPath argument supplied to the last export call.</summary>
    public string? LastExportXPath { get; private set; }

    /// <summary>
    /// Number of times any <c>PartialImport*</c> method has been called. Must stay
    /// zero across every <see cref="PendingEditsHandler"/> operation — the reader
    /// is required to be strictly read-only.
    /// </summary>
    public int ImportCallCount { get; private set; }

    public void Dispose() { }

    public Task PartialExportToXmlFileAsync(
        string xPath, string filePath, CancellationToken ct = default)
    {
        LastExportXPath = xPath;
        ExportXPaths.Add(xPath);

        if (ExportShouldThrow)
            throw new OperationFailedException("Simulated export failure.", xPath);

        if (ExportContentByXPath.TryGetValue(xPath, out var perPath))
            File.WriteAllText(filePath, perPath);
        else if (ExportContent is not null)
            File.WriteAllText(filePath, ExportContent);

        return Task.CompletedTask;
    }

    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default)
    {
        if (ThrowOnReadConnectedState)
            throw new OperationFailedException("Simulated connected-state read failure.", "ReadConnectedStateAsync");
        return Task.FromResult(ConnectedStateToReturn);
    }

    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default)
    {
        if (ThrowOnReadControllerMode)
            throw new OperationFailedException("Simulated controller-mode read failure.", "ReadControllerModeAsync");
        return Task.FromResult(ControllerModeToReturn);
    }

    // ── PartialImport* — must never be called by PendingEditsHandler ───────────

    public Task PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default)
    {
        ImportCallCount++;
        throw new InvalidOperationException("PendingEditsHandler must never call PartialImportFromXmlFileAsync.");
    }

    public Task PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        ImportCallCount++;
        throw new InvalidOperationException("PendingEditsHandler must never call PartialImportWithTargetFromXmlFileAsync.");
    }

    public Task PartialImportRungsFromXmlFileAsync(string xPath, uint insertPosition, uint replaceCount, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        ImportCallCount++;
        throw new InvalidOperationException("PendingEditsHandler must never call PartialImportRungsFromXmlFileAsync.");
    }

    // ── All other ILogixProject members — throw NotImplementedException ────────

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
    public void AddEventHandler(IOperationEvent handler) { }
    public void ClearEventHandlers() { }
    public void RemoveEventHandler(IOperationEvent handler) { }

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

// ── Helpers ───────────────────────────────────────────────────────────────────

/// <summary>Shared helpers for building test sessions for the pending-edits tests.</summary>
internal static class PendingEditsTestHelpers
{
    /// <summary>XPath of <c>MainProgram/MainRoutine</c>, as keyed in the staged rung buffer.</summary>
    public const string MainRoutineXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    /// <summary>XPath of <c>MainProgram/OtherRoutine</c>, as keyed in the staged rung buffer.</summary>
    public const string OtherRoutineXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='OtherRoutine']";

    /// <summary>
    /// Builds a <see cref="LogixProjectSession"/> backed by <paramref name="fake"/>
    /// and opens it immediately so callers get an already-open session.
    /// </summary>
    public static async Task<(LogixProjectSession Session, FakePendingEditsProject Fake)>
        OpenedSessionAsync(FakePendingEditsProject? fake = null)
    {
        fake ??= new FakePendingEditsProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        return (session, fake);
    }
}

// ── PendingEditsHandlerTests ────────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="PendingEditsHandler"/> (the <c>logix_pending_edits</c>
/// tool's pure handler).
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Rung Type classification: N/I/D/R/IR/e/er/dN/rR/rI/rN, unknown codes,
///     and the missing-attribute default.</item>
///   <item>Full-export parsing: NNIII → 3 pending rungs, all-N → state "none".</item>
///   <item>Strict read-only guarantee: no <c>PartialImport*</c> call is ever made.</item>
///   <item><c>connected</c>/<c>controllerMode</c> sourced from the two read-only
///     SDK calls, including the <c>Faulted</c> mode.</item>
///   <item>XPath scoping (whole controller / one program / one routine) and
///     input validation (routine without program, single-quote injection guard).</item>
/// </list>
/// </summary>
public class PendingEditsHandlerTests
{
    private const string MainProgramXPath =
        "Controller/Programs/Program[@Name='MainProgram']";

    private const string MainRoutineXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private static string RungsL5X(params (int Number, string? Type)[] rungs)
    {
        var rungXml = string.Join(string.Empty, rungs.Select(r =>
            r.Type is null
                ? $"<Rung Number=\"{r.Number}\"><Text><![CDATA[NOP();]]></Text></Rung>"
                : $"<Rung Number=\"{r.Number}\" Type=\"{r.Type}\"><Text><![CDATA[NOP();]]></Text></Rung>"));

        return "<RSLogix5000Content><Controller><Programs>" +
               "<Program Name=\"MainProgram\"><Routines>" +
               "<Routine Name=\"MainRoutine\"><RLLContent>" +
               rungXml +
               "</RLLContent></Routine>" +
               "</Routines></Program>" +
               "</Programs></Controller></RSLogix5000Content>";
    }

    /// <summary>L5X with a configurable <c>TestEdits</c> attribute on the program element.</summary>
    private static string ProgramL5X(string? testEdits, params (int Number, string? Type)[] rungs)
    {
        var l5x = RungsL5X(rungs);
        return testEdits is null
            ? l5x
            : l5x.Replace("<Program Name=\"MainProgram\">",
                          $"<Program Name=\"MainProgram\" TestEdits=\"{testEdits}\">");
    }

    // ── Self-closed RLL routines ──────────────────────────────────────

    [Fact]
    public void ParseRoutines_SelfClosedRllRoutine_ZeroRungsStateNone()
    {
        const string l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Type=\"RLL\" Name=\"X\"/>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";

        var (routines, _) = PendingEditsHandler.ParseRoutines(l5x);

        var routine = Assert.Single(routines);
        Assert.Equal("MainProgram", routine.Program);
        Assert.Equal("X", routine.Routine);
        Assert.Equal(0, routine.Rungs);
        Assert.Empty(routine.PendingRungs);
        Assert.Equal("none", routine.State);
    }

    [Fact]
    public void ParseRoutines_EmptyRllRoutineWithDescription_ZeroRungsStateNone()
    {
        const string l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Type=\"RLL\" Name=\"BeaconRoutine\"><Description><![CDATA[beacon]]></Description></Routine>" +
            "<Routine Type=\"RLL\" Name=\"EmptyContent\"><Description>d</Description><RLLContent/></Routine>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";

        var (routines, _) = PendingEditsHandler.ParseRoutines(l5x);

        Assert.Equal(2, routines.Count);
        Assert.All(routines, r =>
        {
            Assert.Equal(0, r.Rungs);
            Assert.Empty(r.PendingRungs);
            Assert.Equal("none", r.State);
        });
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRllRoutineWithDescription_ReturnsResultNotError()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent =
                "<RSLogix5000Content><Controller><Programs>" +
                "<Program Name=\"MainProgram\"><Routines>" +
                "<Routine Type=\"RLL\" Name=\"BeaconRoutine\"><Description>x</Description></Routine>" +
                "</Routines></Program></Programs></Controller></RSLogix5000Content>",
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync("MainProgram", "BeaconRoutine", session);

        var ok = Assert.IsType<PendingEditsResult>(result);
        Assert.Equal("none", Assert.Single(ok.Routines).State);
    }

    [Fact]
    public void ParseRoutines_SelfClosedNonRllRoutine_IsSkipped()
    {
        const string l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Type=\"ST\" Name=\"S\"/>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";

        var (routines, _) = PendingEditsHandler.ParseRoutines(l5x);

        Assert.Empty(routines);
    }

    [Fact]
    public async Task ExecuteAsync_ProgramWithOnlySelfClosedRllRoutine_ReturnsResultNotError()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent =
                "<RSLogix5000Content><Controller><Programs>" +
                "<Program Name=\"MainProgram\"><Routines>" +
                "<Routine Type=\"RLL\" Name=\"X\"/>" +
                "</Routines></Program></Programs></Controller></RSLogix5000Content>",
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);

        var ok = Assert.IsType<PendingEditsResult>(result);
        Assert.Equal("none", Assert.Single(ok.Routines).State);
    }

    // ── TestEdits / zoneState ───────────────────────────────────────────────────

    [Fact]
    public async Task ProgramExport_TestEditsTrue_ZoneStateTested()
    {
        var fake = new FakePendingEditsProject { ExportContent = ProgramL5X("true", (0, "N"), (1, "I")) };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);

        var rtn = Assert.Single(result.Routines);
        Assert.True(rtn.ProgramTestedEdits);
        Assert.Equal("tested", rtn.ZoneState);
        Assert.Equal("pending", rtn.State); // existing field unchanged
        var prog = Assert.Single(result.Programs!);
        Assert.Equal("MainProgram", prog.Program);
        Assert.True(prog.TestedEdits);
        Assert.Null(prog.Note);
    }

    [Fact]
    public async Task ProgramExport_TestEditsFalse_WithIRung_ZoneStateUntested()
    {
        var fake = new FakePendingEditsProject { ExportContent = ProgramL5X("false", (0, "N"), (1, "I")) };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);

        var rtn = Assert.Single(result.Routines);
        Assert.False(rtn.ProgramTestedEdits);
        Assert.Equal("untested", rtn.ZoneState);
        Assert.Equal("pending", rtn.State);
        Assert.False(Assert.Single(result.Programs!).TestedEdits);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task NoPendingRungs_ZoneStateNone(string testEdits)
    {
        var fake = new FakePendingEditsProject { ExportContent = ProgramL5X(testEdits, (0, "N"), (1, "N")) };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);

        var rtn = Assert.Single(result.Routines);
        Assert.Equal("none", rtn.ZoneState);
        Assert.Equal("none", rtn.State);
    }

    [Fact]
    public async Task RoutineScope_AlsoExportsProgram_ReportsProgramTestedEdits()
    {
        // The routine export lacks TestEdits; only the program export carries it.
        var fake = new FakePendingEditsProject();
        fake.ExportContentByXPath[MainRoutineXPath] = ProgramL5X(null, (0, "N"), (1, "I"));
        fake.ExportContentByXPath[MainProgramXPath] = ProgramL5X("true", (0, "N"), (1, "I"));
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal(new[] { MainRoutineXPath, MainProgramXPath }, fake.ExportXPaths);
        var rtn = Assert.Single(result.Routines);
        Assert.True(rtn.ProgramTestedEdits);
        Assert.Equal("tested", rtn.ZoneState);
        Assert.True(Assert.Single(result.Programs!).TestedEdits);
        Assert.Equal(0, fake.ImportCallCount);
    }

    [Fact]
    public async Task ProgramScope_DoesNotIssueExtraExport()
    {
        var fake = new FakePendingEditsProject { ExportContent = ProgramL5X("false", (0, "I")) };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);

        Assert.Equal(new[] { MainProgramXPath }, fake.ExportXPaths);
    }

    [Fact]
    public async Task TestEditsAttributeAbsent_ReportsNull_NoGuess()
    {
        var fake = new FakePendingEditsProject { ExportContent = ProgramL5X(null, (0, "N"), (1, "I")) };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);

        var rtn = Assert.Single(result.Routines);
        Assert.Null(rtn.ProgramTestedEdits);
        Assert.Equal("unknown", rtn.ZoneState); // not "tested"/"untested"
        Assert.Equal("pending", rtn.State);
        var prog = Assert.Single(result.Programs!);
        Assert.Null(prog.TestedEdits);
        Assert.False(string.IsNullOrEmpty(prog.Note));

        // null must be serialised explicitly, not omitted.
        var json = System.Text.Json.JsonSerializer.Serialize(rtn);
        Assert.Contains("\"programTestedEdits\":null", json);
    }

    [Fact]
    public async Task RoutineScope_ProgramExportLacksAttribute_ReportsNullWithNote()
    {
        var fake = new FakePendingEditsProject();
        fake.ExportContentByXPath[MainRoutineXPath] = ProgramL5X(null, (0, "I"));
        // The program export itself lacks the attribute → null + note.
        fake.ExportContentByXPath[MainProgramXPath] = ProgramL5X(null, (0, "I"));
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Null(rtn.ProgramTestedEdits);
        Assert.Equal("unknown", rtn.ZoneState);
        Assert.NotNull(Assert.Single(result.Programs!).Note);
    }

    // ── ClassifyRungType — the code table ───────────────────────────────────────

    [Theory]
    [InlineData("N",  PendingEditsHandler.RungEditZone.None)]
    [InlineData("I",  PendingEditsHandler.RungEditZone.Pending)]
    [InlineData("D",  PendingEditsHandler.RungEditZone.Pending)]
    [InlineData("R",  PendingEditsHandler.RungEditZone.Pending)]
    [InlineData("IR", PendingEditsHandler.RungEditZone.Pending)]
    [InlineData("e",  PendingEditsHandler.RungEditZone.Tested)]
    [InlineData("er", PendingEditsHandler.RungEditZone.Tested)]
    [InlineData("dN", PendingEditsHandler.RungEditZone.Tested)]
    [InlineData("rR", PendingEditsHandler.RungEditZone.Accepted)]
    [InlineData("rI", PendingEditsHandler.RungEditZone.Accepted)]
    [InlineData("rN", PendingEditsHandler.RungEditZone.Accepted)]
    public void ClassifyRungType_KnownCode_ReturnsExpectedZone(string code, PendingEditsHandler.RungEditZone expected)
    {
        Assert.Equal(expected, PendingEditsHandler.ClassifyRungType(code));
    }

    [Fact]
    public void ClassifyRungType_MissingAttribute_DefaultsToNone()
    {
        Assert.Equal(PendingEditsHandler.RungEditZone.None, PendingEditsHandler.ClassifyRungType(null));
    }

    [Fact]
    public void ClassifyRungType_EmptyString_DefaultsToNone()
    {
        Assert.Equal(PendingEditsHandler.RungEditZone.None, PendingEditsHandler.ClassifyRungType(string.Empty));
    }

    [Fact]
    public void ClassifyRungType_UnknownCode_IsConservativelyPending()
    {
        // Any code outside the documented table must never be silently dropped as "None".
        Assert.Equal(PendingEditsHandler.RungEditZone.Pending, PendingEditsHandler.ClassifyRungType("ZZ"));
    }

    // ── BuildXPath ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildXPath_NoProgramOrRoutine_ReturnsControllerPrograms()
    {
        Assert.Equal("Controller/Programs", PendingEditsHandler.BuildXPath(null, null));
    }

    [Fact]
    public void BuildXPath_ProgramOnly_ReturnsProgramXPath()
    {
        Assert.Equal(MainProgramXPath, PendingEditsHandler.BuildXPath("MainProgram", null));
    }

    [Fact]
    public void BuildXPath_ProgramAndRoutine_ReturnsRoutineXPath()
    {
        Assert.Equal(MainRoutineXPath, PendingEditsHandler.BuildXPath("MainProgram", "MainRoutine"));
    }

    // ── Export_NNIII_IsReportedAsThreePendingRungs ──────────────────────────────

    [Fact]
    public async Task Export_NNIII_IsReportedAsThreePendingRungs()
    {
        var l5x = RungsL5X((0, "N"), (1, "N"), (2, "I"), (3, "I"), (4, "I"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Equal(5, rtn.Rungs);
        Assert.Equal(3, rtn.PendingRungs.Count);
        Assert.All(rtn.PendingRungs, r => Assert.Equal("I", r.Type));
        Assert.Equal(new[] { 2, 3, 4 }, rtn.PendingRungs.Select(r => r.Number));
        Assert.Equal("pending", rtn.State);
    }

    // ── Report_IncludesStagedRungCountAndTagNames ───────────────────────────────

    /// <summary>
    /// The response's <c>staged</c> field must reflect the current session's
    /// local staging buffer — <see cref="LogixProjectSession.StagedRungCount"/>
    /// and the tag names held in <see cref="LogixProjectSession.StagedTagBuffer"/>
    /// — independently of whatever the controller export shows. This lets an
    /// agent inspect what the next <c>target="controller"</c> publish will send
    /// before committing.
    /// </summary>
    [Fact]
    public async Task Report_IncludesStagedRungCountAndTagNames()
    {
        var l5x = RungsL5X((0, "N"), (1, "N"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        // Simulate the effect of prior target="workstation" propose-rung-edit
        // calls: rungs accumulated in the session's in-memory image, and tag
        // declarations stripped from the fragments into the staging buffer.
        session.StagedRungCount = 3;
        session.StagedTagBuffer["Motor_Run"] = new XElement("Tag",
            new XAttribute("Name", "Motor_Run"));
        session.StagedTagBuffer["Conveyor_Speed"] = new XElement("Tag",
            new XAttribute("Name", "Conveyor_Speed"));

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal(3, result.Staged.RungCount);
        Assert.Equal(
            new[] { "Conveyor_Speed", "Motor_Run" },
            result.Staged.TagNames);
    }

    // ── Report_NothingStaged_ReportsZeroCountAndEmptyTagNames ───────────────────

    [Fact]
    public async Task Report_NothingStaged_ReportsZeroCountAndEmptyTagNames()
    {
        var l5x = RungsL5X((0, "N"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal(0, result.Staged.RungCount);
        Assert.Empty(result.Staged.TagNames);
    }

    // ── Export_AllNormal_IsStateNone ────────────────────────────────────────────

    [Fact]
    public async Task Export_AllNormal_IsStateNone()
    {
        var l5x = RungsL5X((0, "N"), (1, "N"), (2, "N"), (3, "N"), (4, "N"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Equal(5, rtn.Rungs);
        Assert.Empty(rtn.PendingRungs);
        Assert.Equal("none", rtn.State);
    }

    [Fact]
    public async Task Export_AllRungsMissingTypeAttribute_IsStateNone()
    {
        var l5x = RungsL5X((0, null), (1, null), (2, null));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal("none", Assert.Single(result.Routines).State);
    }

    // ── ReadIsSideEffectFree_NoImportCall ───────────────────────────────────────

    [Fact]
    public async Task ReadIsSideEffectFree_NoImportCall()
    {
        var l5x = RungsL5X((0, "N"), (1, "e"), (2, "rR"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);

        Assert.IsType<PendingEditsResult>(result);
        Assert.Equal(0, fake.ImportCallCount);
    }

    // ── Zone mixing — worst zone wins ───────────────────────────────────────────

    [Fact]
    public async Task Export_TestedZoneBeatsPending_StateIsTested()
    {
        var l5x = RungsL5X((0, "I"), (1, "e"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal("tested", Assert.Single(result.Routines).State);
    }

    [Fact]
    public async Task Export_AcceptedZoneBeatsTested_StateIsAccepted()
    {
        var l5x = RungsL5X((0, "e"), (1, "rN"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal("accepted", Assert.Single(result.Routines).State);
    }

    // ── connected / controllerMode ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SessionOnline_ConnectedIsTrue()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent = RungsL5X((0, "N")),
            ConnectedStateToReturn = ConnectedState.Online,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.True(result.Connected);
    }

    [Fact]
    public async Task ExecuteAsync_SessionOffline_ConnectedIsFalse()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent = RungsL5X((0, "N")),
            ConnectedStateToReturn = ConnectedState.Offline,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.False(result.Connected);
    }

    [Fact]
    public async Task ExecuteAsync_ControllerFaulted_ModeSurfacedAsFaulted()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent = RungsL5X((0, "N")),
            ControllerModeToReturn = ControllerMode.Faulted,
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        Assert.Equal("Faulted", result.ControllerMode);
    }

    [Fact]
    public async Task ExecuteAsync_ReadConnectedStateThrows_ReturnsError()
    {
        var fake = new FakePendingEditsProject { ThrowOnReadConnectedState = true };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);

        Assert.IsType<PendingEditsError>(result);
    }

    [Fact]
    public async Task ExecuteAsync_ReadControllerModeThrows_ReturnsError()
    {
        var fake = new FakePendingEditsProject { ThrowOnReadControllerMode = true };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", session);

        Assert.IsType<PendingEditsError>(result);
    }

    // ── Whole-controller scope (no program/routine) ─────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoProgramOrRoutine_ExportsControllerPrograms()
    {
        var l5x = RungsL5X((0, "N"));
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync(null, null, session);

        Assert.IsType<PendingEditsResult>(result);
        Assert.Equal("Controller/Programs", fake.LastExportXPath);
    }

    // ── Input validation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RoutineWithoutProgram_ReturnsError()
    {
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync();

        var result = await PendingEditsHandler.ExecuteAsync(null, "MainRoutine", session);

        Assert.IsType<PendingEditsError>(result);
    }

    [Fact]
    public async Task ExecuteAsync_ProgramWithSingleQuote_ReturnsError()
    {
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync();

        var result = await PendingEditsHandler.ExecuteAsync("Main'Program", null, session);

        Assert.IsType<PendingEditsError>(result);
    }

    // ── No RLL routines found ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoRoutinesInExport_ReturnsError()
    {
        var fake = new FakePendingEditsProject
        {
            ExportContent = "<RSLogix5000Content><Controller><Programs /></Controller></RSLogix5000Content>",
        };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = await PendingEditsHandler.ExecuteAsync(null, null, session);

        Assert.IsType<PendingEditsError>(result);
    }
}

// ── LogixSdkTools pending-edits wiring tests ────────────────────────────────────

/// <summary>
/// Unit tests for the <c>logix_pending_edits</c> tool wiring in
/// <see cref="LogixSdkTools"/> (<see cref="LogixSdkTools.GetPendingEditsResponseAsync"/>
/// and <see cref="LogixSdkTools.PendingEdits"/>).
/// </summary>
public class PendingEditsLogixSdkToolsTests
{
    private static string SimpleL5X(string type) =>
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"{type}\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "</Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    [Fact]
    public async Task GetPendingEditsResponseAsync_NoActiveSession_ReturnsError()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine");
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    [Fact]
    public async Task GetPendingEditsResponseAsync_WithSession_ReturnsRoutinesArray()
    {
        var fake = new FakePendingEditsProject { ExportContent = SimpleL5X("N") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine", session);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("routines", out var routines),
            $"Expected 'routines' property in: {json}");
        Assert.Equal(1, routines.GetArrayLength());
    }

    [Fact]
    public async Task GetPendingEditsResponseAsync_ReportsTargetRevisionAndMnemonicSet()
    {
        var fake = new FakePendingEditsProject { ExportContent = SimpleL5X("N") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var sessions = new TagSessionService();
        sessions.SetTargetRevision(new TargetRevisionInfo(36, "iec", TargetRevisionSource.MajorRev, null));
        var tools = new LogixSdkTools(new ProjectService(), sessions);

        var doc = JsonDocument.Parse(await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine", session));

        Assert.Equal(36, doc.RootElement.GetProperty("targetMajorRevision").GetInt32());
        Assert.Equal("iec", doc.RootElement.GetProperty("mnemonicSet").GetString());
    }

    [Fact]
    public async Task GetPendingEditsResponseAsync_ReportsFingerprintAgeOnceAProbeRan()
    {
        var fake = new FakePendingEditsProject { ExportContent = SimpleL5X("N") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var sessions = new TagSessionService();
        sessions.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true);
        sessions.SetControllerImage(L5X.Parse(SimpleL5X("N")), DateTimeOffset.UtcNow);
        var tools = new LogixSdkTools(new ProjectService(), sessions);

        var before = JsonDocument.Parse(await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine", session));
        Assert.False(before.RootElement.TryGetProperty("fingerprintAgeMs", out _));

        sessions.ControllerFingerprintSource = ct => PendingEditsHandler.ReadFingerprintAsync(session, ct)!;
        await sessions.CheckControllerFingerprintAsync(force: true);
        var after = JsonDocument.Parse(await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine", session));

        Assert.True(after.RootElement.GetProperty("fingerprintAgeMs").GetInt64() >= 0);
        Assert.Null(sessions.ControllerChange);   // the export matches the image
    }

    [Fact]
    public async Task ReadFingerprintAsync_ReportsRoutinesAndRungCountsOfEveryProgram()
    {
        var fake = new FakePendingEditsProject { ExportContent = SimpleL5X("N") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var fingerprint = await PendingEditsHandler.ReadFingerprintAsync(session);

        var program = Assert.Single(fingerprint!.Programs);
        Assert.Equal("MainProgram", program.Program);
        Assert.Equal(1, program.Routines["MainRoutine"]);
        Assert.Equal("Controller/Programs", fake.LastExportXPath);
    }

    [Fact]
    public async Task GetPendingEditsResponseAsync_UnknownRevision_ReportsUnknownWithWarning()
    {
        var fake = new FakePendingEditsProject { ExportContent = SimpleL5X("N") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var doc = JsonDocument.Parse(await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine", session));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("targetMajorRevision").ValueKind);
        Assert.Equal("unknown", doc.RootElement.GetProperty("mnemonicSet").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task GetPendingEditsResponseAsync_WithStagedBuffer_ReturnsStagedRungCountAndTagNames()
    {
        var fake = new FakePendingEditsProject { ExportContent = SimpleL5X("N") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        session.StagedRungCount = 2;
        session.StagedTagBuffer["Estop_Latch"] = new XElement("Tag",
            new XAttribute("Name", "Estop_Latch"));
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetPendingEditsResponseAsync("MainProgram", "MainRoutine", session);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("staged", out var staged),
            $"Expected 'staged' property in: {json}");
        Assert.Equal(2, staged.GetProperty("rungCount").GetInt32());
        Assert.Equal("Estop_Latch", Assert.Single(staged.GetProperty("tagNames").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task PendingEdits_NoActiveSession_ReturnsErrorJson()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.PendingEdits("MainProgram", "MainRoutine");
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    [Fact]
    public void LogixSdkTools_PendingEdits_IsRegisteredWithCorrectName()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.PendingEdits));
        Assert.NotNull(method);

        var toolAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
        Assert.NotNull(toolAttr);

        var name = toolAttr!.GetType().GetProperty("Name")?.GetValue(toolAttr) as string;
        Assert.Equal("logix_pending_edits", name);
    }
}

// ── PendingEditsToolTests ─────────────────────────────────────────────────────

/// <summary>
/// Tests for the session-staged rung separation in <c>logix_pending_edits</c>.
///
/// <para>
/// Verifies that rungs present in the current session's
/// <see cref="LogixProjectSession.StagedRungBuffer"/> (staged via
/// <c>target="workstation"</c>) are classified as <c>staged</c> and excluded
/// from the controller-pending <c>pendingRungs</c> array and <c>state</c> field.
/// The <c>FinalizeEditsHandler</c> publish-refusal path must not cite a
/// <c>PendingZoneStudioOnly</c> kind when the zone is composed entirely of the
/// calling session's own staged rungs — after filtering, the state is <c>"none"</c>,
/// and the refusal's <c>Kind</c> must be <c>null</c>.
/// </para>
/// </summary>
public class PendingEditsToolTests
{
    /// <summary>
    /// The unscoped behaviour must be described as reading every program's ladder
    /// routines, not as a "whole controller" export.
    /// </summary>
    [Fact]
    public void ToolDescription_Unscoped_SaysEveryProgram_NotWholeController()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.PendingEdits));
        Assert.NotNull(method);

        var descAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        Assert.NotNull(descAttr);

        var description = descAttr!.GetType().GetProperty("Description")?.GetValue(descAttr) as string;
        Assert.NotNull(description);
        Assert.Contains("Omitting program reads every program's ladder routines", description);
        Assert.DoesNotContain("whole controller", description, StringComparison.OrdinalIgnoreCase);
    }

    private static string SingleRungL5X(int number, string type, string text) =>
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        $"<Rung Number=\"{number}\" Type=\"{type}\"><Text><![CDATA[{text}]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "</Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    // ── SessionStagedRung_IsReportedUnderStaged_NotAsControllerPending ─────────

    /// <summary>
    /// A rung present in the session's <see cref="LogixProjectSession.StagedRungBuffer"/>
    /// must appear under <c>staged.rungCount</c> and must NOT appear in
    /// <c>pendingRungs</c> — the routine's <c>state</c> must be <c>"none"</c>.
    ///
    /// <para>
    /// Scenario: a <c>target="workstation"</c> stage introduced a rung whose
    /// normalised text is recorded in <see cref="LogixProjectSession.StagedRungBuffer"/>.
    /// The controller export shows that same rung as <c>Type="e"</c> (the
    /// <c>LeaveEdits</c>-produced tested-zone marker). After filtering out the
    /// session-owned rung, the controller-side view is rung-free:
    /// <c>state="none"</c> and <c>pendingRungs</c> is empty.
    /// <c>staged.rungCount</c> reflects the session-local counter.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SessionStagedRung_IsReportedUnderStaged_NotAsControllerPending()
    {
        const string rungText = "OTE(Motor_Run);";
        var l5x = SingleRungL5X(0, "e", rungText);
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        // Simulate a prior workstation-target stage: the normalised rung text is
        // recorded in StagedRungBuffer and the rung counter is incremented.
        var normalizedText = ProposeRungEditHandler.NormalizeRungText(rungText);
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new List<string> { normalizedText };
        session.StagedRungCount = 1;

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        // Session-staged rung must be excluded from the controller-pending view.
        Assert.Empty(rtn.PendingRungs);
        Assert.Equal("none", rtn.State);
        // The staged counter must still reflect the session-local staging.
        Assert.Equal(1, result.Staged.RungCount);
    }

    // ── PublishRefusal_DoesNotCiteZoneMadeOnlyOfOwnStagedRungs ────────────────

    /// <summary>
    /// When every non-normal rung in the controller export matches the session's
    /// own <see cref="LogixProjectSession.StagedRungBuffer"/>, the filtered
    /// <c>state</c> is <c>"none"</c>. <see cref="FinalizeEditsHandler"/> must
    /// then refuse with the <c>"nothing pending to finalize"</c> message whose
    /// <c>Kind</c> is <c>null</c> — not with <c>kind="PendingZoneStudioOnly"</c>,
    /// which is reserved for a genuine controller-side pending zone that this
    /// session did not introduce.
    /// </summary>
    [Fact]
    public async Task PublishRefusal_DoesNotCiteZoneMadeOnlyOfOwnStagedRungs()
    {
        const string rungText = "OTE(Conveyor_Enable);";
        var l5x = SingleRungL5X(0, "e", rungText);
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        // Simulate prior workstation-target stage.
        var normalizedText = ProposeRungEditHandler.NormalizeRungText(rungText);
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new List<string> { normalizedText };
        session.StagedRungCount = 1;

        // FinalizeEditsHandler requires the session to be online and correlated.
        session.IsOnline     = true;
        session.IsCorrelated = true;

        var result = await FinalizeEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: true, session);

        var error = Assert.IsType<FinalizeEditsError>(result);
        // Filtered state is "none" — Kind must be null, not "PendingZoneStudioOnly".
        Assert.Null(error.Kind);
        Assert.Contains("state='none'", error.Error, StringComparison.Ordinal);
    }

    // ── ReplaceStage_OriginalsReportedUnderStagedReplacedRungs_StateNone ────────

    /// <summary>
    /// When a workstation-target replace stage has been performed, the original
    /// rung being replaced appears in the controller export with an <c>r*</c> or
    /// <c>d*</c> type.  That rung must appear under <c>staged.replacedRungs</c>
    /// and must NOT appear in <c>pendingRungs</c> — the routine's <c>state</c>
    /// must be <c>"none"</c> because there are no genuine controller-side pending
    /// edits.
    ///
    /// <para>
    /// Scenario: a <c>target="workstation"</c> stage introduced a new rung
    /// (tracked in <see cref="LogixProjectSession.StagedRungBuffer"/>) and
    /// simultaneously displaced the original rung at position 0.  The original's
    /// normalised text is recorded in
    /// <see cref="LogixProjectSession.StagedReplacedRungTexts"/>.  The controller
    /// export shows the displaced original as <c>Type="R"</c> (the
    /// LeaveEdits-produced replace-pending marker) and the new rung as <c>Type="e"</c>.
    /// After both filtering passes the controller-side view is empty:
    /// <c>state="none"</c>, <c>pendingRungs</c> is empty, and
    /// <c>staged.replacedRungs</c> carries the original.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReplaceStage_OriginalsReportedUnderStagedReplacedRungs_StateNone()
    {
        const string originalText = "OTE(Motor_Run);";
        const string newText      = "OTE(Motor_Start);";

        // Export shows two rungs: the original (type "R" — displaced original)
        // and the new replacement (type "e" — session-staged insert).
        var l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            $"<Rung Number=\"0\" Type=\"R\"><Text><![CDATA[{originalText}]]></Text></Rung>" +
            $"<Rung Number=\"1\" Type=\"e\"><Text><![CDATA[{newText}]]></Text></Rung>" +
            "</RLLContent></Routine>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";

        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        // Simulate a prior workstation-target replace stage:
        // — the new rung's normalised text is in StagedRungBuffer
        // — the original rung's normalised text is in StagedReplacedRungTexts
        var normalizedNew      = ProposeRungEditHandler.NormalizeRungText(newText);
        var normalizedOriginal = ProposeRungEditHandler.NormalizeRungText(originalText);
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new List<string> { normalizedNew };
        session.StagedRungCount = 1;
        session.StagedReplacedRungTexts.Add(normalizedOriginal);

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        // No controller-side pending rungs after filtering both the staged new rung
        // and the session-originated replaced original.
        Assert.Empty(rtn.PendingRungs);
        Assert.Equal("none", rtn.State);

        // The original rung must appear under staged.replacedRungs.
        var replaced = Assert.Single(result.Staged.ReplacedRungs!);
        Assert.Equal(0, replaced.Number);
        Assert.Equal("R", replaced.Type);
        Assert.Equal(originalText, replaced.Text);

        // staged.rungCount must still reflect the session-local staging.
        Assert.Equal(1, result.Staged.RungCount);
    }

    // ── StagedTagNames_ExcludePreExistingContextTags ──────────────────────────

    /// <summary>
    /// <c>staged.tagNames</c> must contain only the names of tag declarations
    /// that are absent from the pre-stage controller tag census (i.e., only tags
    /// that a publish would actually create on the controller).  Context
    /// declarations for tags that already existed before staging began must be
    /// silently excluded.
    ///
    /// <para>
    /// Scenario: the staging buffer holds two tag names — one genuinely new
    /// (<c>"NewTag"</c>) and one that was already present on the controller
    /// before staging (<c>"ExistingTag"</c>).  The pre-stage census
    /// (<see cref="LogixProjectSession.PreStageTagCensus"/>) contains
    /// <c>"ExistingTag"</c>.  After filtering, <c>staged.tagNames</c> must
    /// contain only <c>"NewTag"</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StagedTagNames_ExcludePreExistingContextTags()
    {
        var l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";

        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        // Staging buffer: one new tag, one pre-existing context declaration.
        session.StagedTagBuffer["NewTag"] =
            new XElement("Tag", new XAttribute("Name", "NewTag"),
                new XAttribute("DataType", "BOOL"));
        session.StagedTagBuffer["ExistingTag"] =
            new XElement("Tag", new XAttribute("Name", "ExistingTag"),
                new XAttribute("DataType", "DINT"));

        // Pre-stage census: the controller already knew about ExistingTag.
        session.PreStageTagCensus.Add("ExistingTag");

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        // Only the genuinely new tag should appear in staged.tagNames.
        Assert.Equal(new[] { "NewTag" }, result.Staged.TagNames);
    }

    // ── ExecuteAsync_Unscoped_TwoPrograms_ReportsEveryRoutine ─────────────────

    /// <summary>
    /// An unscoped call (<c>program</c> and <c>routine</c> both null) against a
    /// Controller/Programs export holding two programs — each with one RLL
    /// routine, one of which carries a pending (<c>I</c>) rung — must report
    /// both routines, and only the one with the pending rung is <c>"pending"</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Unscoped_TwoPrograms_ReportsEveryRoutine()
    {
        var l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine>" +
            "</Routines></Program>" +
            "<Program Name=\"SecondProgram\"><Routines>" +
            "<Routine Name=\"SecondRoutine\" Type=\"RLL\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "<Rung Number=\"1\" Type=\"I\"><Text><![CDATA[XIC(Start)OTE(Run);]]></Text></Rung>" +
            "</RLLContent></Routine>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";

        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        var result = Assert.IsType<PendingEditsResult>(
            await PendingEditsHandler.ExecuteAsync(null, null, session));

        Assert.Equal(2, result.Routines.Count);

        var main = Assert.Single(result.Routines,
            r => r.Program == "MainProgram" && r.Routine == "MainRoutine");
        Assert.Equal("none", main.State);
        Assert.Empty(main.PendingRungs);

        var second = Assert.Single(result.Routines,
            r => r.Program == "SecondProgram" && r.Routine == "SecondRoutine");
        Assert.Equal("pending", second.State);
        var pending = Assert.Single(second.PendingRungs);
        Assert.Equal(1, pending.Number);
    }
}
// ── PendingEditsTests ─────────────────────────────────────────────────────────

/// <summary>
/// Controller-vs-staged rung split in <c>logix_pending_edits</c> (item 7).
/// </summary>
public class PendingEditsTests
{
    // ── StagedInsertNotCountedAsControllerRung ─────────────────────────────────

    /// <summary>
    /// The server-managed ACD holds a workstation-staged rung as a normal rung, so a
    /// 4-rung controller routine with one staged insert exports 5 <c>N</c> rungs. The
    /// routine must report <c>rungs</c> (= <c>controllerRungs</c>) 4, <c>stagedRungs</c> 1
    /// and state <c>none</c> (item 7).
    /// </summary>
    [Fact]
    public async Task StagedInsertNotCountedAsControllerRung()
    {
        var l5x =
            "<RSLogix5000Content><Controller><Programs>" +
            "<Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(A)OTE(B);]]></Text></Rung>" +
            "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[XIC(C)OTE(D);]]></Text></Rung>" +
            "<Rung Number=\"2\" Type=\"N\"><Text><![CDATA[OTE(Motor_Start);]]></Text></Rung>" +
            "<Rung Number=\"3\" Type=\"N\"><Text><![CDATA[XIC(E)OTE(F);]]></Text></Rung>" +
            "<Rung Number=\"4\" Type=\"N\"><Text><![CDATA[XIC(G)OTE(H);]]></Text></Rung>" +
            "</RLLContent></Routine>" +
            "</Routines></Program>" +
            "</Programs></Controller></RSLogix5000Content>";
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 2u)] = new List<string>
            { ProposeRungEditHandler.NormalizeRungText("OTE(Motor_Start);") };
        session.StagedRungCount = 1;

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Equal(4, rtn.Rungs);
        Assert.Equal(4, rtn.ControllerRungs);
        Assert.Equal(1, rtn.StagedRungs);
        Assert.Equal("none", rtn.State);
        Assert.Empty(rtn.PendingRungs);
        Assert.Equal(1, result.Staged.RungCount);

        // Both counts are on the wire; rungs stays equal to controllerRungs.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(rtn));
        Assert.Equal(4, doc.RootElement.GetProperty("rungs").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("controllerRungs").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("stagedRungs").GetInt32());
    }

    /// <summary>
    /// A controller rung with the same text as a staged rung is not over-subtracted: the
    /// staged census is consumed once per staged rung.
    /// </summary>
    [Fact]
    public async Task StagedCensus_DoesNotSubtractMoreThanStaged()
    {
        var l5x = RungsL5XSameText("NOP();", 3);
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new List<string> { ProposeRungEditHandler.NormalizeRungText("NOP();") };
        session.StagedRungCount = 1;

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Equal(2, rtn.Rungs);
        Assert.Equal(1, rtn.StagedRungs);
    }

    // ── Per-routine staged rung buffer (two routines at the same insert position) ──

    private static string TwoRoutineL5X(string textA, string textB) =>
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{textA}]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "<Routine Name=\"OtherRoutine\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{textB}]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "</Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    private static string FragmentWithRungs(params string[] texts) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        string.Concat(texts.Select((t, i) =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>Two routines staged at position 0 keep separate rung lists in the buffer.</summary>
    [Fact]
    public async Task StagedRungBuffer_TwoRoutinesSamePosition_KeepSeparateRungLists()
    {
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(new FakePendingEditsProject());
        var a = ProposeRungEditHandler.NormalizeRungText("OTE(A);");
        var b = ProposeRungEditHandler.NormalizeRungText("OTE(B);");
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new List<string> { a };
        session.StagedRungBuffer[(PendingEditsTestHelpers.OtherRoutineXPath, 0u)] = new List<string> { b };

        Assert.Equal(2, session.StagedRungBuffer.Count);
        Assert.Equal(new[] { a }, session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)]);
        Assert.Equal(new[] { b }, session.StagedRungBuffer[(PendingEditsTestHelpers.OtherRoutineXPath, 0u)]);
        Assert.Equal(2, session.StagedRoutineXPaths.Count);
        Assert.Contains(PendingEditsTestHelpers.MainRoutineXPath, session.StagedRoutineXPaths);
        Assert.Contains(PendingEditsTestHelpers.OtherRoutineXPath, session.StagedRoutineXPaths);
    }

    /// <summary>The dedupe drops a rung staged in the same routine but not one staged in another routine.</summary>
    [Fact]
    public void Dedupe_TwoRoutinesSamePosition_OnlyDropsRungsStagedInSameRoutine()
    {
        var buffer = new Dictionary<(string RoutineXPath, uint InsertPosition), List<string>>
        {
            [(PendingEditsTestHelpers.MainRoutineXPath, 0u)]  = new() { ProposeRungEditHandler.NormalizeRungText("OTE(A);") },
            [(PendingEditsTestHelpers.OtherRoutineXPath, 0u)] = new() { ProposeRungEditHandler.NormalizeRungText("OTE(B);") },
        };
        var fragment = FragmentWithRungs("OTE(A);", "OTE(B);");

        var result = ProposeRungEditHandler.DedupeFragmentRungsAgainstStagedBuffer(
            fragment, PendingEditsTestHelpers.MainRoutineXPath, 0, buffer, out var total, out var dropped);

        Assert.Equal(2, total);
        Assert.Equal(1, dropped);
        Assert.DoesNotContain("OTE(A);", result, StringComparison.Ordinal);
        Assert.Contains("OTE(B);", result, StringComparison.Ordinal);
    }

    /// <summary>pending_edits reports per-routine stagedRungs and the correct total staged.rungCount.</summary>
    [Fact]
    public async Task PendingEdits_TwoRoutinesSamePosition_ReportsPerRoutineStagedRungsAndTotal()
    {
        // Identical text in both routines: each routine may only consume its own staged rung.
        var fake = new FakePendingEditsProject { ExportContent = TwoRoutineL5X("NOP();", "NOP();") };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var nop = ProposeRungEditHandler.NormalizeRungText("NOP();");
        // Only MainRoutine staged a NOP at position 0.
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new List<string> { nop };
        session.StagedRungCount = 1;

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);
        var main  = Assert.Single(result.Routines, r => r.Routine == "MainRoutine");
        var other = Assert.Single(result.Routines, r => r.Routine == "OtherRoutine");
        Assert.Equal(1, main.StagedRungs);
        Assert.Equal(0, other.StagedRungs);
        Assert.Equal(1, other.Rungs);
        Assert.Equal(1, result.Staged.RungCount);

        // Now both routines staged at position 0.
        session.StagedRungBuffer[(PendingEditsTestHelpers.OtherRoutineXPath, 0u)] = new List<string> { nop };
        session.StagedRungCount = 2;
        result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync("MainProgram", null, session);
        Assert.All(result.Routines, r => Assert.Equal(1, r.StagedRungs));
        Assert.Equal(2, result.Staged.RungCount);
    }

    /// <summary>Disposing the session clears every routine's staged rungs and the derived routine list.</summary>
    [Fact]
    public async Task Dispose_ClearsPerRoutineStagedBuffer()
    {
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(new FakePendingEditsProject());
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)]  = new List<string> { "a" };
        session.StagedRungBuffer[(PendingEditsTestHelpers.OtherRoutineXPath, 0u)] = new List<string> { "b" };
        Assert.Equal(2, session.StagedRoutineXPaths.Count);

        session.Dispose();

        Assert.Empty(session.StagedRungBuffer);
        Assert.Empty(session.StagedRoutineXPaths);
    }

    private static string RungsL5XSameText(string text, int count) =>
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        string.Concat(Enumerable.Range(0, count).Select(i =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{text}]]></Text></Rung>")) +
        "</RLLContent></Routine>" +
        "</Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";
    // ── The shared comparator at the staged-buffer / census sites ────

    private static string SingleRungL5X(int number, string type, string text) =>
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        $"<Rung Number=\"{number}\" Type=\"{type}\"><Text><![CDATA[{text}]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "</Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    /// <summary>A staged <c>TON(T,3000,0)</c> is recognised as the export's <c>TON(T,?,?)</c> (staged, not pending).</summary>
    [Fact]
    public async Task SessionStagedTon_MatchesExportPlaceholders_NotReportedAsControllerPending()
    {
        var l5x = SingleRungL5X(0, "I", "XIC(Run)TON(T,?,?);");
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] =
            new List<string> { ProposeRungEditHandler.NormalizeRungText("XIC(Run)TON(T,3000,0);") };
        session.StagedRungCount = 1;

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Empty(rtn.PendingRungs);
        Assert.Equal("none", rtn.State);
    }

    /// <summary>A staged TON with a different tag is not absorbed by an exported <c>TON(T,?,?)</c>.</summary>
    [Fact]
    public async Task SessionStagedTon_DifferentTag_StaysControllerPending()
    {
        var l5x = SingleRungL5X(0, "I", "XIC(Run)TON(T,?,?);");
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath, 0u)] =
            new List<string> { ProposeRungEditHandler.NormalizeRungText("XIC(Run)TON(U,3000,0);") };
        session.StagedRungCount = 1;

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);

        var rtn = Assert.Single(result.Routines);
        Assert.Single(rtn.PendingRungs);
    }

    /// <summary>The "zone matches what was staged" re-check clears a mismatch recorded for a timer rung.</summary>
    [Fact]
    public async Task ResolveMatchingPublishMismatches_TimerRungWithPlaceholders_Resolves()
    {
        var l5x = SingleRungL5X(0, "I", "XIC(Run)TON(T,?,?);");
        var fake = new FakePendingEditsProject { ExportContent = l5x };
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        session.RecordPublishMismatch(PendingEditsTestHelpers.MainRoutineXPath,
            new List<string> { "XIC(Run)TON(T,3000,0);" });

        var result = (PendingEditsResult)await PendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", session);
        PendingEditsHandler.ResolveMatchingPublishMismatches(session, result.Routines);

        Assert.Empty(session.UnresolvedPublishMismatches);
    }

    /// <summary>The staged-buffer dedupe drops a fragment rung already staged as a <c>?</c> text only for table instructions.</summary>
    [Fact]
    public void Dedupe_StagedTextWithPlaceholder_DropsOnlyTableInstructionRungs()
    {
        var buffer = new Dictionary<(string RoutineXPath, uint InsertPosition), List<string>>
        {
            [(PendingEditsTestHelpers.MainRoutineXPath, 0u)] = new()
            {
                "TON(T,?,?);",
                "XIC(?);",
            },
        };
        var fragment = FragmentWithRungs("TON(T,3000,0);", "XIC(A);");

        var result = ProposeRungEditHandler.DedupeFragmentRungsAgainstStagedBuffer(
            fragment, PendingEditsTestHelpers.MainRoutineXPath, 0, buffer, out _, out var dropped);

        Assert.Equal(1, dropped);
        Assert.DoesNotContain("TON(T,3000,0);", result, StringComparison.Ordinal);
        Assert.Contains("XIC(A);", result, StringComparison.Ordinal);
    }

}

// ── Reconcile on observation ──────────────

/// <summary>
/// <c>logix_pending_edits</c> notices a pending zone on the controller that the session image does not hold (a program
/// this session did not publish to, or a zone changed in Logix Designer), marks the image stale with the existing reason
/// and appends one <c>image_reconciled</c> audit line; the next successful refresh clears the mark.
/// </summary>
[Collection("StaticHandlerHooks")]
public class PendingEditsReconcileTests
{
    private static readonly string[] Published =
        ["XIC(A)XIC(C)OTE(B);", "XIC(D)OTE(E);", "XIC(E)OTE(C);"];   // replace(0,2) + one insert

    private const string Original3 = "XIC(A)OTE(D);";
    private const string Original1 = "XIC(A)OTE(B);";
    private const string Original2 = "XIC(C)OTE(B);";

    private static readonly (string Type, string Text)[] ZoneRungs =
        [("IR", Published[0]), ("IR", Published[1]), ("I", Published[2]), ("R", Original1), ("R", Original2), ("N", Original3)];

    private const string Reason = "pending edits of MainProgram were resolved outside lgx-mcp";

    private static string Export(params (string Type, string Text)[] rungs) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"Main\" Type=\"RLL\"><RLLContent>" +
        string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static (string, string)[] Plain(params string[] texts) => texts.Select(t => ("N", t)).ToArray();

    /// <summary>A controller whose program export is a fixed image.</summary>
    private sealed class FixedExport(L5X export) : IControllerImageSource
    {
        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default) => Task.FromResult(export.ToString());
        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) => Task.FromResult(export.ToString());
    }

    private sealed record Setup(
        LogixSdkTools Tools, TagSessionService Session, ProjectService Service, LogixProjectSession Sdk,
        FakePendingEditsProject Fake);

    /// <summary>
    /// A connected session whose image is <paramref name="image"/> (the plain connect-time image by default) and whose
    /// controller export, as <c>logix_pending_edits</c> reads it, is <paramref name="export"/>.
    /// </summary>
    private static async Task<Setup> SetupAsync(string export, L5X? image = null)
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect(image);
        var fake = new FakePendingEditsProject { ExportContent = export };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var tools = new LogixSdkTools(service, session);
        return new Setup(tools, session, service, sdk, fake);
    }

    private static async Task<JsonElement> ObserveAsync(Setup s) =>
        JsonDocument.Parse(await s.Tools.GetPendingEditsResponseAsync("MainProgram", "Main", s.Sdk)).RootElement.Clone();

    private static async Task<(JsonElement Response, List<JsonElement> Audit)> ObserveWithAuditAsync(Setup s)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgx-reconcile-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AuditLogger.OverrideLogDir(dir);
        try
        {
            var response = await ObserveAsync(s);
            var file = Path.Combine(dir, "audit.jsonl");
            var audit = File.Exists(file)
                ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                    .Where(e => e.GetProperty("event").GetString() == "image_reconciled").ToList()
                : [];
            return (response, audit);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ZoneInAProgramTheImageDoesNotHold_MarksTheImageStaleWithTheReason_AndAuditsIt()
    {
        var s = await SetupAsync(Export(ZoneRungs));   // the image is the plain connect-time image

        var (response, audit) = await ObserveWithAuditAsync(s);

        Assert.Equal("stale", response.GetProperty("reconciled").GetString());
        var stale = Assert.IsType<SessionImageStaleness>(SessionImageState.StalenessOf(s.Session));
        Assert.Equal(Reason, stale.Reason);
        Assert.True(s.Session.IsImageStale);   // the program is queued for refresh
        var result = Assert.IsType<GetRoutineResult>(GetRoutineHandler.Execute(s.Service, "MainProgram", "Main", s.Session));
        Assert.True(result.ImageStale);
        Assert.Equal(Reason, result.StaleReason);

        var line = Assert.Single(audit);
        Assert.Equal("MainProgram", line.GetProperty("program").GetString());
        Assert.Equal("Main", line.GetProperty("routine").GetString());
        Assert.Equal("stale", line.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task TheNextSuccessfulRefresh_ClearsTheReconcileMark_AndTheFollowingObservationReconcilesNothing()
    {
        var zone = SessionImageAfterAcceptTests.ImageOfTyped(ZoneRungs.Select(r => (r.Text, r.Type)).ToArray());
        var s = await SetupAsync(Export(ZoneRungs));
        s.Session.ImageSourceOverride = new FixedExport(zone);
        Assert.Equal("stale", (await ObserveAsync(s)).GetProperty("reconciled").GetString());

        Assert.True(await s.Session.RefreshControllerImageAsync());

        Assert.Null(SessionImageState.StalenessOf(s.Session));
        Assert.False(s.Session.IsImageStale);
        var (second, audit) = await ObserveWithAuditAsync(s);
        Assert.False(second.TryGetProperty("reconciled", out _));
        Assert.Empty(audit);
    }

    [Fact]
    public async Task ZoneTheImageAlreadyHolds_ReconcilesNothing_AndAddsNoReconciledField()
    {
        var zone = SessionImageAfterAcceptTests.ImageOfTyped(ZoneRungs.Select(r => (r.Text, r.Type)).ToArray());
        var s = await SetupAsync(Export(ZoneRungs), zone);

        var (response, audit) = await ObserveWithAuditAsync(s);

        Assert.False(response.TryGetProperty("reconciled", out _));
        Assert.Empty(audit);
        Assert.Null(SessionImageState.StalenessOf(s.Session));
    }

    [Fact]
    public async Task NoZoneOnTheController_ReconcilesNothing()
    {
        var s = await SetupAsync(Export(Plain(Original1, Original2, Original3)));

        var root = await ObserveAsync(s);

        Assert.False(root.TryGetProperty("reconciled", out _));
        Assert.Null(SessionImageState.StalenessOf(s.Session));
    }
}

#endif
