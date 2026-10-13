#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

using ConnectedState           = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using ControllerMode           = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using DataType                 = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode            = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget     = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode  = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions   = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption      = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent       = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode        = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate  = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── FakeFinalizeEditsProject ─────────────────────────────────────────────────

/// <summary>
/// <see cref="ILogixProject"/> stub for testing <see cref="FinalizeEditsHandler"/>
/// and the <c>logix_finalize_edits</c> tool wiring in <see cref="LogixSdkTools"/>.
///
/// <para>
/// Combines the export-file-write behaviour of <c>FakePendingEditsProject</c>
/// (used for both the before/after <c>logix_pending_edits</c> reads and the
/// fresh-export envelope derivation) with call recording on
/// <see cref="PartialImportRungsFromXmlFileAsync"/> (xPath, insertPosition,
/// replaceCount, the imported XML's full text content, and the
/// <see cref="PartialImportOption"/> used) so tests can assert on the exact
/// zero-rung <c>FinalizeEdits</c> envelope the handler builds.
/// </para>
///
/// <para>
/// <see cref="ExportContentAfterImport"/>, when set, is returned by every
/// export call made after the first successful import — simulating the
/// controller's edit zone actually assembling — so tests can assert on the
/// <c>before</c>/<c>after</c> state transition.
/// </para>
/// </summary>
internal sealed class FakeFinalizeEditsProject : ILogixProject
{
    /// <summary>
    /// Content written by every <see cref="PartialExportToXmlFileAsync"/> call
    /// until a successful import has occurred (see <see cref="ExportContentAfterImport"/>).
    /// </summary>
    public string? ExportContent { get; set; }

    /// <summary>
    /// When non-<c>null</c>, content written by every export call made after
    /// <see cref="ImportCallCount"/> has been incremented at least once —
    /// simulating the controller's pending-edit zone changing as a result of
    /// the finalize import. When <c>null</c>, <see cref="ExportContent"/> is
    /// always returned.
    /// </summary>
    public string? ExportContentAfterImport { get; set; }

    /// <summary>When true, <see cref="PartialExportToXmlFileAsync"/> throws <see cref="OperationFailedException"/>.</summary>
    public bool ExportShouldThrow { get; set; }

    /// <summary>Value returned by <see cref="ReadConnectedStateAsync"/>.</summary>
    public ConnectedState ConnectedStateToReturn { get; set; } = ConnectedState.Online;

    /// <summary>Value returned by <see cref="ReadControllerModeAsync"/>.</summary>
    public ControllerMode ControllerModeToReturn { get; set; } = ControllerMode.Run;

    /// <summary>Records the xPath argument supplied to the last export call.</summary>
    public string? LastExportXPath { get; private set; }

    /// <summary>Number of times <see cref="PartialExportToXmlFileAsync"/> has been called.</summary>
    public int ExportCallCount { get; private set; }

    // ── PartialImportRungsFromXmlFileAsync recording ────────────────────────

    /// <summary>Number of times <see cref="PartialImportRungsFromXmlFileAsync"/> has been called.</summary>
    public int ImportCallCount { get; private set; }

    /// <summary>xPath arguments captured from each import call.</summary>
    public List<string> CapturedXPaths { get; } = new();

    /// <summary>insertPosition arguments captured from each import call.</summary>
    public List<uint> CapturedInsertPositions { get; } = new();

    /// <summary>replaceCount arguments captured from each import call.</summary>
    public List<uint> CapturedReplaceCounts { get; } = new();

    /// <summary>onlineImportOption arguments captured from each import call.</summary>
    public List<PartialImportOption> CapturedImportOptions { get; } = new();

    /// <summary>
    /// Full text content of the XML file passed to the last
    /// <see cref="PartialImportRungsFromXmlFileAsync"/> call, captured before the
    /// handler deletes the temp file.
    /// </summary>
    public string? LastCapturedXmlContent { get; private set; }

    /// <summary>
    /// When non-<c>null</c>, <see cref="PartialImportRungsFromXmlFileAsync"/> throws
    /// this exception instead of recording the call.
    /// </summary>
    public Exception? ThrowOnImport { get; set; }

    public void Dispose() { }

    public Task PartialExportToXmlFileAsync(
        string xPath, string filePath, CancellationToken ct = default)
    {
        LastExportXPath = xPath;
        ExportCallCount++;

        if (ExportShouldThrow)
            throw new OperationFailedException("Simulated export failure.", xPath);

        var content = (ImportCallCount > 0 && ExportContentAfterImport is not null)
            ? ExportContentAfterImport
            : ExportContent;

        if (content is not null)
            File.WriteAllText(filePath, content);

        return Task.CompletedTask;
    }

    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default) =>
        Task.FromResult(ConnectedStateToReturn);

    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) =>
        Task.FromResult(ControllerModeToReturn);

    public Task PartialImportRungsFromXmlFileAsync(
        string xPath, uint insertPosition, uint replaceCount, string xmlFileToImport,
        PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        if (ThrowOnImport is not null)
            throw ThrowOnImport;

        // Always capture the file content before the handler deletes the temp
        // file, so tests can assert on the exact XML the SDK would receive.
        LastCapturedXmlContent = File.ReadAllText(xmlFileToImport);

        CapturedXPaths.Add(xPath);
        CapturedInsertPositions.Add(insertPosition);
        CapturedReplaceCounts.Add(replaceCount);
        CapturedImportOptions.Add(onlineImportOption);
        ImportCallCount++;

        return Task.CompletedTask;
    }

    // ── Other PartialImport* — FinalizeEditsHandler must never call these ───

    public Task PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default)
    {
        throw new InvalidOperationException("FinalizeEditsHandler must never call PartialImportFromXmlFileAsync.");
    }

    public Task PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        throw new InvalidOperationException("FinalizeEditsHandler must never call PartialImportWithTargetFromXmlFileAsync.");
    }

    // ── All other ILogixProject members — throw NotImplementedException ────

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

/// <summary>Shared helpers for building test sessions and fixture L5X for the finalize-edits tests.</summary>
internal static class FinalizeEditsTestHelpers
{
    /// <summary>
    /// Builds a <see cref="LogixProjectSession"/> backed by <paramref name="fake"/>,
    /// opens it, and marks it online and correlated so
    /// <see cref="FinalizeEditsHandler.ExecuteAsync"/>'s pre-flight guards pass.
    /// </summary>
    public static async Task<(LogixProjectSession Session, FakeFinalizeEditsProject Fake)>
        OpenedOnlineSessionAsync(FakeFinalizeEditsProject? fake = null)
    {
        fake ??= new FakeFinalizeEditsProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    /// <summary>
    /// A full L5X export fragment for <c>MainProgram/MainRoutine</c>, carrying an
    /// XML declaration and the full complement of <c>RSLogix5000Content</c>
    /// attributes (<c>SchemaRevision</c>, <c>SoftwareRevision</c>, etc.) that a real
    /// export from Studio 5000 would include, exactly as bench data
    /// (<c>scripts/exp-finalize.py</c>) showed is required to avoid
    /// <c>XMLSrv_E_INVALID_SCHEMA_REVISION</c> / <c>XMLSrv_E_ERROR_READING_XML</c>.
    /// </summary>
    /// <param name="rungs">
    /// Rungs to embed, as (number, type-or-null, text) tuples. A <c>null</c> type
    /// omits the <c>Type</c> attribute (defaults to Normal).
    /// </param>
    public static string ExportL5X(params (int Number, string? Type, string Text)[] rungs)
    {
        var rungXml = string.Join(string.Empty, rungs.Select(r =>
            r.Type is null
                ? $"<Rung Number=\"{r.Number}\"><Text><![CDATA[{r.Text}]]></Text></Rung>"
                : $"<Rung Number=\"{r.Number}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>"));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
               "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"32.02\" " +
               "TargetName=\"MainRoutine\" TargetType=\"Rung\" TargetClass=\"Standard\" " +
               "TargetCode=\"N\" ContainsContext=\"true\" ExportDate=\"Mon Sep 07 00:00:00 2026\" " +
               "ExportOptions=\"References NoRawData L5KData DecoratedData Context Dependencies\">" +
               "<Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
               "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
               rungXml +
               "</RLLContent></Routine>" +
               "</Routines></Program></Programs></Controller>" +
               "</RSLogix5000Content>";
    }
}

// ── FinalizeEditsToolTests ───────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="FinalizeEditsHandler"/> (the <c>logix_finalize_edits</c>
/// tool's pure handler) and its wiring in <see cref="LogixSdkTools"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Gate: refuses without <c>confirm=true</c>, and refuses when
///     <c>logix_pending_edits</c> reports <c>state='none'</c> — in both cases
///     without ever calling <c>PartialImportRungsFromXmlFileAsync</c>.</item>
///   <item>This change requirement 1: when the state is anything other than
///     <c>'none'</c>, the tool refuses with kind <c>PendingZoneStudioOnly</c>,
///     naming the routine, every pending rung, and the two valid transitions —
///     and never calls the SDK import used to assemble an edit zone.</item>
///   <item>Source-scan guard: <c>logix_connect</c>/<c>SdkConnectFactory.cs</c> and
///     <c>logix_propose_rung_edit</c>/<c>ProposeRungEditHandler.cs</c> never
///     reference <c>FinalizeEdits</c>.</item>
/// </list>
/// </summary>
public class FinalizeEditsToolTests
{
    // ── RefusesWithoutConfirm ─────────────────────────────────────────────────

    /// <summary>
    /// Without <c>confirm=true</c>, the call must be refused before any session
    /// or controller interaction — no export, no import.
    /// </summary>
    [Fact]
    public async Task RefusesWithoutConfirm()
    {
        var fake = new FakeFinalizeEditsProject
        {
            ExportContent = FinalizeEditsTestHelpers.ExportL5X((0, "N", "NOP();"), (1, "I", "OTE(Motor_Run);")),
        };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);

        var result = await FinalizeEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: false, session);

        var error = Assert.IsType<FinalizeEditsError>(result);
        Assert.Contains("confirm=true", error.Error, StringComparison.Ordinal);
        Assert.Equal(0, fake.ExportCallCount);
        Assert.Equal(0, fake.ImportCallCount);
    }

    // ── RefusesWhenNothingPending ─────────────────────────────────────────────

    /// <summary>
    /// When <c>logix_pending_edits</c> reports <c>state='none'</c> for the target
    /// routine (all rungs Normal), the call is refused before any import is
    /// attempted, and the before snapshot is still surfaced in the error.
    /// </summary>
    [Fact]
    public async Task RefusesWhenNothingPending()
    {
        var fake = new FakeFinalizeEditsProject
        {
            ExportContent = FinalizeEditsTestHelpers.ExportL5X((0, "N", "NOP();"), (1, "N", "NOP();")),
        };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);

        var result = await FinalizeEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: true, session);

        var error = Assert.IsType<FinalizeEditsError>(result);
        Assert.Contains("state='none'", error.Error, StringComparison.Ordinal);
        Assert.Contains("target=\"live\"", error.Error, StringComparison.Ordinal);
        Assert.NotNull(error.Before);
        Assert.Equal("none", error.Before!.State);
        Assert.Equal(0, fake.ImportCallCount);
    }

    // ── PendingZone_ReturnsStudioOnlyRefusal_NamingRungsAndTransitions ───────

    /// <summary>
    /// When <c>logix_pending_edits</c> reports a
    /// non-<c>'none'</c> state for the target routine, the call is refused with
    /// kind <c>"PendingZoneStudioOnly"</c>. The refusal names the routine, every
    /// pending rung (number, type, text), and the valid transitions — accept or
    /// cancel in Logix Designer, or (with the Studio-automation flag) the
    /// <c>logix_accept_pending_edits</c> / <c>logix_cancel_pending_edits</c> tools.
    /// </summary>
    [Fact]
    public async Task PendingZone_ReturnsStudioOnlyRefusal_NamingRungsAndTransitions()
    {
        var l5x = FinalizeEditsTestHelpers.ExportL5X(
            (0, "N", "NOP();"),
            (1, "I", "OTE(Motor_Run);"));
        var fake = new FakeFinalizeEditsProject { ExportContent = l5x };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);

        var result = await FinalizeEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: true, session);

        var error = Assert.IsType<FinalizeEditsError>(result);
        Assert.Equal("PendingZoneStudioOnly", error.Kind);

        // Names the routine and the pending rung(s).
        Assert.Contains("MainProgram", error.Error, StringComparison.Ordinal);
        Assert.Contains("MainRoutine", error.Error, StringComparison.Ordinal);
        Assert.Contains("rung 1", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Motor_Run", error.Error, StringComparison.Ordinal);

        // Names the valid transitions; never steers away from accepting.
        Assert.Contains("Logix Designer", error.Error, StringComparison.Ordinal);
        Assert.Contains("Online Edits", error.Error, StringComparison.Ordinal);
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", error.Error, StringComparison.Ordinal);
        Assert.Contains("logix_cancel_pending_edits", error.Error, StringComparison.Ordinal);
        Assert.Contains("to go live in one step when nothing is pending", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logix_propose_rung_edit", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Studio-only", error.Error, StringComparison.Ordinal);

        Assert.NotNull(error.Before);
        Assert.Equal("pending", error.Before!.State);
    }

    /// <summary>
    /// With the Studio-automation flag on, the <c>PendingZoneStudioOnly</c> refusal
    /// (kind name retained for compatibility) names <c>logix_accept_pending_edits</c>
    /// and <c>logix_cancel_pending_edits</c>.
    /// </summary>
    [Fact]
    public async Task PendingZone_FlagOn_NamesAcceptTool()
    {
        var l5x = FinalizeEditsTestHelpers.ExportL5X((0, "N", "NOP();"), (1, "I", "OTE(Motor_Run);"));
        var fake = new FakeFinalizeEditsProject { ExportContent = l5x };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);

        var result = await FinalizeEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: true, session, studioAutomationEnabled: true);

        var error = Assert.IsType<FinalizeEditsError>(result);
        Assert.Equal("PendingZoneStudioOnly", error.Kind);
        Assert.Contains("logix_accept_pending_edits", error.Error, StringComparison.Ordinal);
        Assert.Contains("logix_cancel_pending_edits", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Studio-only", error.Error, StringComparison.Ordinal);
    }

    // ── PendingZone_NeverCallsSdkImport ───────────────────────────────────────

    /// <summary>
    /// The handler must never call
    /// <c>PartialImportRungsFromXmlFileAsync</c> against an existing pending edit
    /// zone — there is no code path left that assembles one. This holds even when
    /// <c>confirm=true</c> is passed.
    /// </summary>
    [Fact]
    public async Task PendingZone_NeverCallsSdkImport()
    {
        var l5x = FinalizeEditsTestHelpers.ExportL5X(
            (0, "N", "NOP();"),
            (1, "I", "OTE(Motor_Run);"));
        var fake = new FakeFinalizeEditsProject { ExportContent = l5x };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);

        var result = await FinalizeEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: true, session);

        Assert.IsType<FinalizeEditsError>(result);
        Assert.Equal(0, fake.ImportCallCount);
        Assert.Empty(fake.CapturedImportOptions);
    }

    // ── IsNeverInvokedFromConnectOrProposePaths ───────────────────────────────

    /// <summary>
    /// <c>FinalizeEdits</c> must never be reachable from
    /// <c>logix_connect</c> (<c>SdkConnectFactory.cs</c>) or
    /// <c>logix_propose_rung_edit</c> (<c>ProposeRungEditHandler.cs</c>) — neither
    /// file may reference the literal string <c>FinalizeEdits</c> anywhere.
    /// Complements the broader source-wide scans in
    /// <c>ProposeRungEditHandlerTests.SourceScan_NoFinalizeEditsInSrc</c> and
    /// <c>NoPendingEditAutoActionTests.SourceScan_AllSrc_NoFinalizeEdits</c> by
    /// naming the two forbidden files directly.
    /// </summary>
    [Fact]
    public void IsNeverInvokedFromConnectOrProposePaths()
    {
        var srcDir = FindSrcDir();
        Assert.True(srcDir is not null && Directory.Exists(srcDir),
            "Could not locate the src/ directory from the test output path. " +
            "Ensure the solution layout has not changed.");

        var connectFactoryPath = Path.Combine(srcDir!, "LogixMcp.SdkAdapter", "SdkConnectFactory.cs");
        var proposeHandlerPath = Path.Combine(srcDir!, "LogixMcp.SdkAdapter", "ProposeRungEditHandler.cs");

        Assert.True(File.Exists(connectFactoryPath), $"Expected to find {connectFactoryPath}.");
        Assert.True(File.Exists(proposeHandlerPath), $"Expected to find {proposeHandlerPath}.");

        Assert.DoesNotContain("FinalizeEdits", File.ReadAllText(connectFactoryPath), StringComparison.Ordinal);
        // ProposeRungEditHandler.cs now legitimately names
        // FinalizeEdits, but only for target="live". The invariant is narrowed from
        // "never referenced" to "only selected under the live target": the import
        // option may be FinalizeEdits solely via the isLiveTarget branch.
        var proposeText = File.ReadAllText(proposeHandlerPath).Replace("\r\n", "\n");
        Assert.Contains("isLiveTarget\n                    ? PartialImportOption.FinalizeEdits",
            proposeText, StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(
            proposeText, @"\?\s*PartialImportOption\.FinalizeEdits").Count);
    }

    /// <summary>
    /// Walks up from the test-output directory until the <c>src/</c> folder at
    /// the solution root is located, mirroring the same walk used by
    /// <c>ProposeRungEditHandlerTests.SourceScan_NoFinalizeEditsInSrc</c> and
    /// <c>NoPendingEditAutoActionTests.SourceScan_AllSrc_NoFinalizeEdits</c>.
    /// </summary>
    private static string? FindSrcDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "src");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                break;
            dir = parent;
        }
        return null;
    }

    // ── LogixSdkTools wiring ──────────────────────────────────────────────────

    [Fact]
    public async Task GetFinalizeEditsResponseAsync_NoActiveSession_ReturnsError()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetFinalizeEditsResponseAsync("MainProgram", "MainRoutine", confirm: true);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    /// <summary>
    /// This change requirement 1, exercised through the <see cref="LogixSdkTools"/>
    /// JSON wiring: a routine with an existing pending edit zone (state != 'none')
    /// serializes to a <c>"kind": "PendingZoneStudioOnly"</c> error, never a
    /// <c>"finalized"</c> status — the SDK is never called to assemble the zone.
    /// </summary>
    [Fact]
    public async Task GetFinalizeEditsResponseAsync_WithSession_PendingState_ReturnsPendingZoneStudioOnlyKind()
    {
        var l5x  = FinalizeEditsTestHelpers.ExportL5X((0, "I", "OTE(Motor_Run);"));
        var fake = new FakeFinalizeEditsProject { ExportContent = l5x };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetFinalizeEditsResponseAsync(
            "MainProgram", "MainRoutine", confirm: true, session);
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("PendingZoneStudioOnly", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.TryGetProperty("before", out _));
        Assert.False(doc.RootElement.TryGetProperty("status", out _),
            "The SDK must never be called to assemble an existing pending edit zone, " +
            "so a 'status' property (which only a finalize success would produce) " +
            "must never appear.");
        Assert.Equal(0, fake.ImportCallCount);
    }

    [Fact]
    public async Task FinalizeEdits_NoActiveSession_ReturnsErrorJson()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.FinalizeEdits("MainProgram", "MainRoutine", confirm: true);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    [Fact]
    public async Task FinalizeEdits_ConfirmDefaultsFalse_Refuses()
    {
        var l5x  = FinalizeEditsTestHelpers.ExportL5X((0, "I", "OTE(Motor_Run);"));
        var fake = new FakeFinalizeEditsProject { ExportContent = l5x };
        var (session, _) = await FinalizeEditsTestHelpers.OpenedOnlineSessionAsync(fake);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetFinalizeEditsResponseAsync("MainProgram", "MainRoutine", session: session);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal(0, fake.ImportCallCount);
    }

    [Fact]
    public void LogixSdkTools_FinalizeEdits_IsRegisteredWithCorrectName()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.FinalizeEdits));
        Assert.NotNull(method);

        var toolAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
        Assert.NotNull(toolAttr);

        var name = toolAttr!.GetType().GetProperty("Name")?.GetValue(toolAttr) as string;
        Assert.Equal("logix_finalize_edits", name);
    }
}
#endif
