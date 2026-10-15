#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

using ControllerMode          = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState          = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType                = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode           = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget    = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions  = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption     = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent      = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode       = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;
using SdkTask                 = System.Threading.Tasks.Task;
using Task                    = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── ImportFailureClassificationFake ──────────────────────────────────────────

/// <summary>
/// <see cref="ILogixProject"/> stub for testing an earlier change — the
/// controller-target import failure classification implemented by
/// <c>ProposeRungEditHandler.ClassifyControllerImportFailureAsync</c>.
///
/// <para>
/// Unlike <see cref="ProposeRungEditRecordingFake"/> (import-only) or
/// <see cref="FakePendingEditsProject"/> (export-only, forbids import), this
/// fake supports both: a configurable <see cref="PartialImportRungsFromXmlFileAsync"/>
/// failure that optionally drives the registered <see cref="IOperationEvent.Error"/>
/// hook (simulating the SDK's real <c>ImportLog</c> stream) before throwing, and a
/// configurable <see cref="PartialExportToXmlFileAsync"/> so the classifier's
/// tier-1 <c>logix_pending_edits</c> read has something to parse.
/// </para>
/// </summary>
internal sealed class ImportFailureClassificationFake : ILogixProject
{
    /// <summary>When non-<c>null</c>, thrown by <see cref="PartialImportRungsFromXmlFileAsync"/>.</summary>
    public Exception? ThrowOnImport { get; set; }

    /// <summary>
    /// When non-<c>null</c>, <see cref="ThrowOnImport"/> is thrown only on the first this-many import calls;
    /// later calls succeed (a lock that clears). <c>null</c> = throw on every call.
    /// </summary>
    public int? ThrowOnImportFirstCalls { get; set; }

    /// <summary>
    /// When non-<c>null</c>, invoked with (projectFile, message) for every registered
    /// <see cref="IOperationEvent"/> just before <see cref="ThrowOnImport"/> is thrown —
    /// simulating the SDK's real <c>ImportLog</c> event stream.
    /// </summary>
    public IReadOnlyList<string>? ImportLogMessagesToRaise { get; set; }

    /// <summary>Number of times <see cref="PartialImportRungsFromXmlFileAsync"/> was called.</summary>
    public int ImportCallCount { get; private set; }

    /// <summary>L5X content written by <see cref="PartialExportToXmlFileAsync"/>. Required for the tier-1 read to succeed.</summary>
    public string? ExportContent { get; set; }

    /// <summary>Number of times <see cref="PartialExportToXmlFileAsync"/> was called (the tier-1 pending-edits read).</summary>
    public int ExportCallCount { get; private set; }

    /// <summary>Value returned by <see cref="ReadConnectedStateAsync"/>.</summary>
    public ConnectedState ConnectedStateToReturn { get; set; } = ConnectedState.Online;

    /// <summary>Value returned by <see cref="ReadControllerModeAsync"/>.</summary>
    public ControllerMode ControllerModeToReturn { get; set; } = ControllerMode.Run;

    private readonly List<IOperationEvent> _handlers = new();

    public void Dispose() { }

    public void AddEventHandler(IOperationEvent handler) => _handlers.Add(handler);
    public void RemoveEventHandler(IOperationEvent handler) => _handlers.Remove(handler);
    public void ClearEventHandlers() => _handlers.Clear();

    /// <summary>
    /// A controller publish reads the program's edit state (a program-scoped export) before it
    /// imports. While <c>true</c> (default) and before any import call, that program-scoped export is served with
    /// every rung normalised to <c>N</c>, i.e. "nothing pending yet", so tests that describe the state AFTER the
    /// publish through <c>ExportContent</c> still reach the import. Set <c>false</c> to make the program-scoped export
    /// show <c>ExportContent</c> as is (a program that already holds edits).
    /// </summary>
    public bool ProgramExportCleanBeforeImport { get; set; } = true;

    /// <summary>When non-<c>null</c>, every export after the first import call throws it (the post-failure edit-state read fails).</summary>
    public Exception? ThrowOnExportAfterImport { get; set; }

    private bool _importCalled;

    private string MaskProgramExportBeforeImport(string xPath, string content)
    {
        if (!ProgramExportCleanBeforeImport || _importCalled ||
            !System.Text.RegularExpressions.Regex.IsMatch(xPath, @"^Controller/Programs/Program\[@Name='[^']*'\]$"))
            return content;
        var doc = System.Xml.Linq.XDocument.Parse(content);
        foreach (var rung in doc.Descendants("Rung"))
            rung.SetAttributeValue("Type", "N");
        return doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    public SdkTask PartialImportRungsFromXmlFileAsync(
        string xPath,
        uint insertPosition,
        uint replaceCount,
        string xmlFileToImport,
        PartialImportOption onlineImportOption,
        CancellationToken ct = default)
    {
        ImportCallCount++;
        _importCalled = true;

        if (ImportLogMessagesToRaise is not null)
        {
            foreach (var handler in _handlers)
                foreach (var msg in ImportLogMessagesToRaise)
                    handler.Error(xmlFileToImport, msg);
        }

        if (ThrowOnImport is not null && (ThrowOnImportFirstCalls is null || ImportCallCount <= ThrowOnImportFirstCalls))
            throw ThrowOnImport;

        return SdkTask.CompletedTask;
    }

    public Task PartialExportToXmlFileAsync(string xPath, string filePath, CancellationToken ct = default)
    {
        ExportCallCount++;
        if (_importCalled && ThrowOnExportAfterImport is not null)
            throw ThrowOnExportAfterImport;
        if (ExportContent is not null)
            File.WriteAllText(filePath, MaskProgramExportBeforeImport(xPath, ExportContent));
        return SdkTask.CompletedTask;
    }

    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default) =>
        SdkTask.FromResult(ConnectedStateToReturn);

    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) =>
        SdkTask.FromResult(ControllerModeToReturn);

    // ── All other ILogixProject members (stubs) ───────────────────────────────

    public Task<string> GetCommunicationsPathAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SetCommunicationsPathAsync(string projectPath, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ChangeControllerTypeAsync(string processorTypeName, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SaveAsAsync(string savePath, bool force, bool detailedL5x, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SaveAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask DownloadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ChangeControllerModeAsync(RequestedControllerMode mode, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask BuildAsync(RequestedBuildTarget target, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UploadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask StoreImageOnSDCardAsync(RequestedLoadEvent loadEvent, RequestedLoadMode loadMode, AutomaticFirmwareUpdate afuSetting, string imageName, string imageNote) => throw new NotImplementedException();
    public SdkTask LoadImageFromSDCardAsync() => throw new NotImplementedException();

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

    public SdkTask SetTagValueAsync(string tagPath, OperationMode mode, byte[] tagValue, DataType type) => throw new NotImplementedException();
    public SdkTask SetTagValueBOOLAsync(string tagPath, OperationMode mode, bool tagValue)    => throw new NotImplementedException();
    public SdkTask SetTagValueSINTAsync(string tagPath, OperationMode mode, sbyte tagValue)   => throw new NotImplementedException();
    public SdkTask SetTagValueINTAsync(string tagPath, OperationMode mode, short tagValue)    => throw new NotImplementedException();
    public SdkTask SetTagValueDINTAsync(string tagPath, OperationMode mode, int tagValue)     => throw new NotImplementedException();
    public SdkTask SetTagValueLINTAsync(string tagPath, OperationMode mode, long tagValue)    => throw new NotImplementedException();
    public SdkTask SetTagValueUSINTAsync(string tagPath, OperationMode mode, byte tagValue)   => throw new NotImplementedException();
    public SdkTask SetTagValueUINTAsync(string tagPath, OperationMode mode, ushort tagValue)  => throw new NotImplementedException();
    public SdkTask SetTagValueUDINTAsync(string tagPath, OperationMode mode, uint tagValue)   => throw new NotImplementedException();
    public SdkTask SetTagValueULINTAsync(string tagPath, OperationMode mode, ulong tagValue)  => throw new NotImplementedException();
    public SdkTask SetTagValueSTRINGAsync(string tagPath, OperationMode mode, string tagValue) => throw new NotImplementedException();
    public SdkTask SetTagValueREALAsync(string tagPath, OperationMode mode, float tagValue)   => throw new NotImplementedException();
    public SdkTask SetTagValueLREALAsync(string tagPath, OperationMode mode, double tagValue) => throw new NotImplementedException();

    public SdkTask PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default) => throw new NotImplementedException();

    public SdkTask GoOnlineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask GoOfflineAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public Task<string> GetSafetyNetworkNumberAsync(string moduleName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> IsSafetyLockedAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SafetyLockAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SafetyUnlockAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SetSafetyLockPasswordAsync(string newPassword, string oldPassword, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SetSafetyUnlockPasswordAsync(string newPassword, string oldPassword, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask GenerateSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask DeleteSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> GetSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public SdkTask ProtectWithLicenseAsync(string xPath, uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ProtectWithLicenseAsync(IEnumerable<string> xPaths, uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ProtectAllWithLicenseAsync(uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ProtectAllWithPasswordAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UnprotectAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ProtectWithPasswordAsync(string xPath, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ProtectWithPasswordAsync(IEnumerable<string> xPaths, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UnprotectAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UnprotectAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask LockExecutableAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask LockExecutableAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> IsExecutableLockedAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UnlockExecutableAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UnlockExecutableAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask LockAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask UnlockAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> IsExecutableProtectedAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string[]> GetAllExecutablesAsync(CancellationToken ct = default) => throw new NotImplementedException();
}

// ── ImportFailureClassificationTests ─────────────────────────────────────────

/// <summary>
/// Unit tests — the controller-target
/// (<c>target="controller"</c>) import failure classification rule implemented
/// by <c>ProposeRungEditHandler.ClassifyControllerImportFailureAsync</c>:
/// name the edit state and the import cause, never infer one from the other.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>
///   An <c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c> importer rejection is always
///   classified <c>ImportRejectedByImporter</c> — never a pending-edit issue —
///   and the tier-1 <c>logix_pending_edits</c> read is never run for it.
///   </item>
///   <item>
///   Any other controller-target import failure runs the tier-1 read; a
///   non-<c>"none"</c> result is classified <c>PendingEditsPresent</c>, naming
///   the routine, its pending rungs, the "one publish per accept/cancel cycle"
///   constraint, and both valid transitions (accept/cancel in Logix Designer,
///   or <c>logix_cancel_pending_edits</c>),
///   preferring neither.
///   </item>
///   <item>
///   The SDK's real <c>ImportLog</c> (captured via the <see cref="IOperationEvent.Error"/>
///   hook) is quoted — mnemonic and text — in the classified error.
///   </item>
/// </list>
/// </summary>
public class ImportFailureClassificationTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string ValidFragment = ProposeRungEditHandler.MinimalNopFragment;

    private static string RungsL5X(params (int Number, string? Type)[] rungs)
    {
        var rungXml = string.Join(string.Empty, Array.ConvertAll(rungs, r =>
            r.Type is null
                ? $"<Rung Number=\"{r.Number}\"><Text><![CDATA[NOP();]]></Text></Rung>"
                : $"<Rung Number=\"{r.Number}\" Type=\"{r.Type}\"><Text><![CDATA[NOP();]]></Text></Rung>"));

        return "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs>" +
               "<Program Name=\"MainProgram\"><Routines>" +
               "<Routine Name=\"MainRoutine\"><RLLContent>" +
               rungXml +
               "</RLLContent></Routine>" +
               "</Routines></Program>" +
               "</Programs></Controller></RSLogix5000Content>";
    }

    private static async Task<(LogixProjectSession Session, ImportFailureClassificationFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ImportFailureClassificationFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    // ── AbortedNoChanges_IsImportRejectedByImporter_NotPendingEdits ────────────

    /// <summary>
    /// When the SDK importer rejects a reference in the fragment
    /// (<c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c>), the failure must be
    /// classified <c>ImportRejectedByImporter</c> — never a pending-edit
    /// issue — regardless of whatever edit-zone state the controller happens
    /// to be carrying. The tier-1 <c>logix_pending_edits</c> read must never
    /// even run for this case, and the SDK's real ImportLog mnemonic and text
    /// must be quoted verbatim in the error.
    /// </summary>
    [Fact]
    public async Task AbortedNoChanges_IsImportRejectedByImporter_NotPendingEdits()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        const string importLogEntry =
            "<Exception Severity='Error' Mnemonic='XMLSrv_E_IMPORT_ABORTED_NO_CHANGES' " +
            "Line='3' Column='5'>The Import was cancelled due to errors. " +
            "No changes were made.</Exception>";
        fake.ImportLogMessagesToRaise = new[] { importLogEntry };
        fake.ThrowOnImport = new OperationFailedException(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors. " +
            "No changes were made.",
            ValidXPath);
        // Even though the controller happens to have a pending edit, the importer
        // rejection must win the classification — never labeled a pending-edit issue.
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ImportRejectedByImporter", err.Kind);
        Assert.Contains("nothing changed on the controller", err.Hint,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logix_find_tags", err.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PendingEditsPresent", err.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("finalize", err.Error, StringComparison.OrdinalIgnoreCase);

        // The tier-1 pending-edits read must never run for an importer rejection.
        // One export is the pre-publish pending baseline, read before the import; any more would be the classifier.
        // +1 for the Controller/Programs export that resolves the routine names before any import.
        // +3 for the tag/instruction pre-check exports (Controller/Tags, Controller/Programs, AOI definitions).
        // +1 for the program-scoped pending-edit pre-check read before the import.
        // +1 for the rung-range check's rung-count export, read before the dedup and the import.
        Assert.Equal(7, fake.ExportCallCount);

        // The SDK's real ImportLog is quoted verbatim: mnemonic and full text.
        Assert.Contains("mnemonic \"XMLSrv_E_IMPORT_ABORTED_NO_CHANGES\"", err.Error,
            StringComparison.Ordinal);
        Assert.Contains(importLogEntry, err.Error, StringComparison.Ordinal);
    }

    // ── FailureWithPendingState_IsPendingEditsPresent_NamingBothTransitions ────

    /// <summary>
    /// When a controller-target import fails for a reason other than an
    /// importer rejection, and a tier-1 <c>logix_pending_edits</c> read
    /// confirms the target routine holds a non-<c>"none"</c> edit-zone state,
    /// the failure must be classified <c>PendingEditsPresent</c>, naming the
    /// routine, its pending rungs, and both valid transitions — accept/cancel
    /// in Logix Designer, or with the Studio-automation flag
    /// <c>logix_accept_pending_edits</c> / <c>logix_cancel_pending_edits</c>
    /// (supersedes the withdrawn requirement 2
    /// wording that named <c>logix_finalize_edits</c> as a transition
    /// requirement 1 makes finalize an honest refusal, never a transition out of
    /// an existing zone).
    /// </summary>
    [Fact]
    public async Task FailureWithPendingState_IsPendingEditsPresent_NamingBothTransitions()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport = new OperationFailedException(
            "RxCL_E_SOME_OTHER_FAILURE - the import could not be completed.", ValidXPath);
        // Tier-1 read over the failed import's own scope reports a pending rung.
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"), (2, "I"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsPresent", err.Kind);

        // Names the routine and its pending rungs.
        Assert.Contains("MainProgram/MainRoutine", err.Error, StringComparison.Ordinal);
        Assert.Contains("state=pending", err.Error, StringComparison.Ordinal);
        Assert.Contains("1:I", err.Error, StringComparison.Ordinal);
        Assert.Contains("2:I", err.Error, StringComparison.Ordinal);

        // Names the valid transitions and never steers away from accepting.
        Assert.NotNull(err.Hint);
        Assert.Contains("logix_accept_pending_edits", err.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("Studio-only", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("logix_cancel_pending_edits", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("Online Edits", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("Logix Designer", err.Hint!, StringComparison.Ordinal);

        // The tier-1 read actually ran (it is what produced the verdict).
        // The pre-publish pending baseline (read before the import) plus the classifier's read.
        // The routine-scoped baseline read now also exports the parent program once (to read
        // TestEdits, which the routine export lacks), so the total is 3 rather than 2.
        // +1 for the Controller/Programs export that resolves the routine names before any import.
        // +3 for the tag/instruction pre-check exports (Controller/Tags, Controller/Programs, AOI definitions).
        // +1 for the program-scoped pending-edit pre-check read before the import.
        // +1 for the rung-range check's rung-count export, read before the dedup and the import.
        Assert.Equal(9, fake.ExportCallCount);
    }

    // ── EditsExistForProgram_IsPendingEditsPresent_WithCycleHint ───────────────

    /// <summary>
    /// A <c>target="controller"</c> publish whose import
    /// fails with the SDK's real one-publish-per-cycle mnemonic
    /// (<c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c>, bench-confirmed
    /// 2026-09-07) must be classified <c>PendingEditsPresent</c> — never surfaced
    /// as the raw SDK error string — and the response must name: the routine, the
    /// pending rungs (count, type, text), the phrase "one publish per
    /// accept/cancel cycle", both valid transitions (accept/cancel in Logix
    /// Designer, or <c>logix_cancel_pending_edits</c>),
    /// and a hint that <c>target="workstation"</c> can still be used to stage
    /// further edits while this zone is pending.
    /// </summary>
    [Fact]
    public async Task EditsExistForProgram_IsPendingEditsPresent_WithCycleHint()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport = new OperationFailedException(
            "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM - The program contains " +
            "accepted logic edits which must be accepted or cancelled before Import is " +
            "permitted online.", ValidXPath);
        // Tier-1 read over the failed import's own scope reports a pending rung with text.
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        // Classified as a structured PendingEditsPresent verdict, not a raw SDK string.
        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsBlockImport", err.Kind);

        // Names the routine and the pending rung details: count, type, text.
        Assert.Contains("MainProgram/MainRoutine", err.Error, StringComparison.Ordinal);
        Assert.Contains("pendingRungCount=1", err.Error, StringComparison.Ordinal);
        Assert.Contains("1:I:NOP();", err.Error, StringComparison.Ordinal);

        // States the one-publish-per-cycle constraint in plain language.
        Assert.Contains("one publish per accept/cancel cycle", err.Error, StringComparison.Ordinal);

        // Names both valid transitions and prefers neither.
        Assert.NotNull(err.Hint);
        Assert.Contains("Logix Designer", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("logix_accept_pending_edits", err.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("Studio-only", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("logix_cancel_pending_edits", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("Online Edits", err.Hint!, StringComparison.Ordinal);

        // Hints that further edits can still be staged with target="workstation".
        Assert.Contains("target='workstation'", err.Hint!, StringComparison.Ordinal);

        // The tier-1 read actually ran (it is what produced the verdict).
        // The pre-publish pending baseline (read before the import) plus the classifier's read.
        // The routine-scoped baseline read now also exports the parent program once (to read
        // TestEdits, which the routine export lacks), so the total is 3 rather than 2.
        // +1 for the Controller/Programs export that resolves the routine names before any import.
        // +3 for the tag/instruction pre-check exports (Controller/Tags, Controller/Programs, AOI definitions).
        // +1 for the program-scoped pending-edit pre-check read before the import.
        // +1 for the rung-range check's rung-count export, read before the dedup and the import.
        Assert.Equal(9, fake.ExportCallCount);
    }

    // ── PendingEditsBlockImport pre-check ───────────────────

    /// <summary>
    /// A controller publish into a program that already holds accepted edits is refused with
    /// <c>PendingEditsBlockImport</c> before the SDK is asked to import. The error names the routine and
    /// the assemble/finalize/cancel next steps, and the raw SDK mnemonic never surfaces.
    /// </summary>
    [Fact]
    public async Task Publish_IntoProgramWithAcceptedEdits_ReturnsPendingEditsBlockImport()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ProgramExportCleanBeforeImport = false;
        fake.ExportContent = RungsL5X((0, "N"), (1, "rN"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsBlockImport", err.Kind);
        Assert.Contains("MainRoutine", err.Error, StringComparison.Ordinal);
        Assert.Contains("No import was attempted", err.Error, StringComparison.Ordinal);
        Assert.NotNull(err.Hint);
        Assert.Contains("logix_assemble_pending_edits", err.Hint!, StringComparison.Ordinal);
        Assert.Contains("finalize", err.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logix_cancel_pending_edits", err.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM", err.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM", err.Hint!, StringComparison.Ordinal);
    }

    /// <summary>The pre-check runs before any import: no SDK import call is made when accepted edits exist.</summary>
    [Fact]
    public async Task Publish_IntoProgramWithAcceptedEdits_MakesNoImportCall()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ProgramExportCleanBeforeImport = false;
        fake.ExportContent = RungsL5X((0, "N"), (1, "rN"));
        // Would surface the raw SDK mnemonic if the import were reached.
        fake.ThrowOnImport = new OperationFailedException(
            "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM - The program contains accepted logic edits.",
            ValidXPath);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        Assert.Equal(0, fake.ImportCallCount);
        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsBlockImport", err.Kind);
    }

    // ── Supporting coverage: rename and no-heuristic guarantees ────────────────

    /// <summary>
    /// The withdrawn <c>ControllerHasPendingEdits</c> kind must have no
    /// remaining references anywhere the classifier can emit a Kind: the only
    /// pending-edit Kind string ever produced is <c>PendingEditsPresent</c>.
    /// </summary>
    [Fact]
    public async Task PendingEditsVerdict_NeverEmitsWithdrawnKindName()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport = new OperationFailedException("generic failure", ValidXPath);
        fake.ExportContent = RungsL5X((0, "rN"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("PendingEditsPresent", err.Kind);
        Assert.DoesNotContain("ControllerHasPendingEdits", err.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("ControllerHasPendingEdits", err.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A generic controller-target import failure where the tier-1 read reports
    /// every routine as <c>"none"</c> must NOT be classified <c>PendingEditsPresent</c>
    /// — the verdict is evidence-based only, never inferred from a
    /// "Project is not open." (or any other) message heuristic on the SDK detail.
    /// </summary>
    [Fact]
    public async Task FailureWithNoState_ProjectNotOpenText_IsNotClassifiedAsPendingEdits()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        // "Project is not open." was the withdrawn heuristic string
        // it must carry no special meaning to the classifier any more.
        fake.ThrowOnImport = new OperationFailedException("Project is not open.", ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"), (1, "N"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Null(err.Kind);
    }

    /// <summary>
    /// A session-lifecycle failure (the SDK's underlying handle was disposed
    /// mid-call, surfaced by <see cref="LogixProjectSession.ExecuteAsync"/> as
    /// <see cref="TagServiceException"/> with <see cref="TagServiceErrorKind.ConnectionFailed"/>)
    /// carries no evidence about edit state or import cause — it must never be
    /// routed through <c>ClassifyControllerImportFailureAsync</c>. The tier-1
    /// <c>logix_pending_edits</c> read must not run, and the failure must not be
    /// classified <c>ImportRejectedByImporter</c> or <c>PendingEditsPresent</c>.
    /// </summary>
    [Fact]
    public async Task ConnectionFailedDuringImport_IsNotRoutedThroughClassifier()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        // Simulates the SDK's underlying handle being disposed mid-call; the session
        // wrapper converts this to TagServiceException(ConnectionFailed).
        fake.ThrowOnImport = new ObjectDisposedException("LogixProject");
        // Even though the controller happens to have a pending edit, a dead-session
        // failure must not be (mis)classified as a pending-edit issue.
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Null(err.Kind);
        Assert.DoesNotContain("ImportRejectedByImporter", err.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingEditsPresent", err.Error, StringComparison.Ordinal);

        // The tier-1 pending-edits read must never run for a dead-session failure.
        // One export is the pre-publish pending baseline (read before the import); any more would be the classifier.
        // +1 for the Controller/Programs export that resolves the routine names before any import.
        // +3 for the tag/instruction pre-check exports (Controller/Tags, Controller/Programs, AOI definitions).
        // +1 for the program-scoped pending-edit pre-check read before the import.
        // +1 for the rung-range check's rung-count export, read before the dedup and the import.
        Assert.Equal(7, fake.ExportCallCount);
    }

    // ── ControllerLocked and the bounded retry ──────────────

    private const string LockMnemonic = "RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK";

    private static OperationFailedException LockedException() =>
        new($"{LockMnemonic} - another client holds a higher exclusive lock.", ValidXPath);

    /// <summary>Waits recorded instead of slept, so the retry tests take no real time.</summary>
    private static List<TimeSpan> RecordWaits(LogixProjectSession session)
    {
        var waits = new List<TimeSpan>();
        session.ControllerLockRetryWait = (d, _) => { waits.Add(d); return System.Threading.Tasks.Task.CompletedTask; };
        return waits;
    }

    /// <summary>
    /// A lock that never clears returns kind <c>ControllerLocked</c> with the retryable hint, reports
    /// <c>retries</c>, and makes at most 3 tries spread over about 10 s.
    /// </summary>
    [Fact]
    public async Task PersistentLockMnemonic_ReturnsControllerLocked_WithRetryableHint_AndAtMostThreeTries()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var waits = RecordWaits(session);
        fake.ThrowOnImport = LockedException();
        fake.ExportContent = RungsL5X((0, "N"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ControllerLocked", err.Kind);
        Assert.Equal(ErrorKinds.ControllerLocked, err.Kind);
        Assert.Contains("retry in a few seconds", err.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client is importing", err.Hint!, StringComparison.Ordinal);
        Assert.Equal(2, err.Retries);

        // At most 3 tries over about 10 s: two waits of 5 s between the three tries.
        Assert.Equal(3, fake.ImportCallCount);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits.Sum(w => w.TotalSeconds), 9.0, 11.0);
    }

    /// <summary>The default wait between lock retries is 5 s, so three tries span about 10 s.</summary>
    [Fact]
    public async Task ControllerLockRetry_DefaultDelay_SpansAboutTenSeconds()
    {
        var (session, _) = await BuildOpenSessionAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), session.ControllerLockRetryDelay);
        Assert.Equal(3, ControllerLockRetry.MaxTries);
        Assert.InRange(session.ControllerLockRetryDelay.TotalSeconds * (ControllerLockRetry.MaxTries - 1), 9.0, 11.0);
    }

    /// <summary>The lock mnemonic only in the captured ImportLog still maps to ControllerLocked and is retried.</summary>
    [Fact]
    public async Task LockMnemonicInImportLog_IsRetriedAndMapped()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        RecordWaits(session);
        fake.ImportLogMessagesToRaise = new[] { $"<Exception Severity='Error' Mnemonic='{LockMnemonic}'>locked</Exception>" };
        fake.ThrowOnImport = new OperationFailedException("The import failed.", ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ControllerLocked", err.Kind);
        Assert.Equal(3, fake.ImportCallCount);
    }

    /// <summary>Other mnemonics are not retried: one try, and no <c>retries</c> on the error.</summary>
    [Theory]
    [InlineData("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors.")]
    [InlineData("XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM - The program contains accepted logic edits.")]
    [InlineData("RxCL_E_SOME_OTHER_FAILURE - the import could not be completed.")]
    public async Task OtherMnemonics_AreNotRetried(string message)
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var waits = RecordWaits(session);
        fake.ThrowOnImport = new OperationFailedException(message, ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.NotEqual("ControllerLocked", err.Kind);
        Assert.Null(err.Retries);
        Assert.Equal(1, fake.ImportCallCount);
        Assert.Empty(waits);
    }

    /// <summary>A lock on the first try, then a later try that succeeds, returns success with retries reported.</summary>
    [Fact]
    public async Task LockThenSuccess_OnLaterTry_ReturnsSuccessWithRetriesReported()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var waits = RecordWaits(session);
        fake.ThrowOnImport = LockedException();
        fake.ThrowOnImportFirstCalls = 1;
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", ok.Status);
        Assert.Equal(1, ok.Retries);
        Assert.Equal(2, fake.ImportCallCount);
        Assert.Single(waits);
    }

    /// <summary>A success on the first try reports no <c>retries</c>.</summary>
    [Fact]
    public async Task SuccessOnFirstTry_ReportsNoRetries()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        RecordWaits(session);
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Null(ok.Retries);
        Assert.Equal(1, fake.ImportCallCount);
    }
}
#endif
