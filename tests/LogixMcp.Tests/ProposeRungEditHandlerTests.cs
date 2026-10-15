#if LOGIX_SDK
using System.Text.Json;
using System.Collections.Generic;
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// Nested-type aliases so tests read as close to the documented SDK API as possible.
using ControllerMode            = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState            = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType                  = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using OperationMode             = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using RequestedBuildTarget      = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode   = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions    = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption       = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent        = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode         = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate   = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;
using SdkTask                   = System.Threading.Tasks.Task;
using Task                      = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ── ProposeRungEditRecordingFake ─────────────────────────────────────────────────

/// <summary>
/// Minimal <see cref="ILogixProject"/> stub for testing
/// <see cref="ProposeRungEditHandler"/>.
///
/// <para>
/// Records calls to <see cref="PartialImportRungsFromXmlFileAsync"/> so tests
/// can assert on the arguments that were passed.  All other interface members
/// are stubs that either do nothing or return default values.
/// </para>
/// </summary>
internal sealed class ProposeRungEditRecordingFake : ILogixProject
{
    /// <summary>Number of times <see cref="Dispose"/> has been called.</summary>
    public int DisposeCallCount { get; private set; }

    // ── PartialImportRungsFromXmlFileAsync recording ──────────────────────────

    /// <summary>XPath arguments captured from each call.</summary>
    public List<string> CapturedXPaths { get; } = new();

    /// <summary>insertPosition arguments captured from each call.</summary>
    public List<uint> CapturedInsertPositions { get; } = new();

    /// <summary>replaceCount arguments captured from each call.</summary>
    public List<uint> CapturedReplaceCounts { get; } = new();

    /// <summary>xmlFileToImport arguments captured from each call.</summary>
    public List<string> CapturedXmlFiles { get; } = new();

    /// <summary>onlineImportOption arguments captured from each call.</summary>
    public List<PartialImportOption> CapturedImportOptions { get; } = new();

    /// <summary>
    /// When non-<c>null</c>, <see cref="PartialImportRungsFromXmlFileAsync"/>
    /// throws this exception instead of recording the call.
    /// </summary>
    public Exception? ThrowOnImport { get; set; }

    /// <summary>
    /// When non-<c>null</c>, <see cref="BuildAsync"/>
    /// throws this exception instead of completing successfully.
    /// </summary>
    public Exception? ThrowOnBuild { get; set; }

    /// <summary>
    /// When non-<c>null</c>, runs on every <see cref="PartialImportRungsFromXmlFileAsync"/> call before it is recorded, with the
    /// import option, insert position, replace count and the fragment text. A test models the controller/session image here
    /// (set <see cref="ExportContent"/>) and may throw to simulate an SDK refusal.
    /// </summary>
    public Action<PartialImportOption, uint, uint, string>? OnImportRungs { get; set; }

    /// <summary>Import log events replayed to registered handlers on each import call: (onErrorChannel, message).</summary>
    public List<(bool OnErrorChannel, string Message)> ImportLogEvents { get; } = new();

    /// <summary>When non-<c>null</c>, an import call returns this task, which a test never completes (a hung SDK call).</summary>
    public TaskCompletionSource? BlockImportUntil { get; set; }

    /// <summary>Number of times <see cref="BuildAsync"/> has been called.</summary>
    public int BuildCallCount { get; private set; }

    /// <summary>RequestedBuildTarget arguments captured from each <see cref="BuildAsync"/> call.</summary>
    public List<RequestedBuildTarget> CapturedBuildTargets { get; } = new();

    // ── ReadConnectedStateAsync recording ─────────────────────────────────────

    /// <summary>
    /// The value returned by <see cref="ReadConnectedStateAsync"/>.
    /// Defaults to <see cref="ConnectedState.Online"/> so that the post-import
    /// state check passes in success-path tests without extra setup.
    /// </summary>
    public ConnectedState ConnectedStateReturnValue { get; set; } =
        RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Online;

    /// <summary>Number of times <see cref="ReadConnectedStateAsync"/> has been called.</summary>
    public int ReadConnectedStateCallCount { get; private set; }

    /// <summary>
    /// When non-<c>null</c>, <see cref="ReadConnectedStateAsync"/>
    /// throws this exception instead of returning <see cref="ConnectedStateReturnValue"/>.
    /// </summary>
    public Exception? ThrowOnReadConnectedState { get; set; }

    // ── ILogixProject ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        DisposeCallCount++;
        // Closing the opened copy ends the window in which this fake stands in for the saved copy.
        _savedAs = false;
    }

    /// <summary>When <c>true</c>, <see cref="BuildAsync"/> clears <see cref="ExportContent"/> so every later export throws.</summary>
    public bool ClearExportContentAfterBuild { get; set; }

    /// <summary>
    /// When non-<c>null</c>, invoked with the full text content of the XML file
    /// that was passed to <see cref="PartialImportRungsFromXmlFileAsync"/>.
    /// The callback fires before the file is deleted, allowing tests to assert
    /// on the exact XML the SDK would receive.
    /// </summary>
    public Action<string>? XmlFileContentCaptor { get; set; }

    /// <summary>Content captured by the last <see cref="XmlFileContentCaptor"/> invocation, or <c>null</c>.</summary>
    public string? LastCapturedXmlContent { get; private set; }

    /// <summary>
    /// A controller publish reads the program's edit state (a program-scoped export) before it
    /// imports. While <c>true</c> (default) and before any import call, that program-scoped export is served with
    /// every rung normalised to <c>N</c>, i.e. "nothing pending yet", so tests that describe the state AFTER the
    /// publish through <c>ExportContent</c> still reach the import. Set <c>false</c> to make the program-scoped export
    /// show <c>ExportContent</c> as is (a program that already holds edits).
    /// </summary>
    public bool ProgramExportCleanBeforeImport { get; set; } = true;

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
        if (onlineImportOption != PartialImportOption.LeaveEdits) _importCalled = true;
        // Import log events replayed to registered handlers, as the SDK streams them during an import.
        foreach (var (onError, message) in ImportLogEvents)
            foreach (var h in Handlers.ToArray())
            {
                if (onError) h.Error(xmlFileToImport, message); else h.Status(xmlFileToImport, message);
            }

        if (BlockImportUntil is not null)
            return BlockImportUntil.Task;

        if (ThrowOnImport is not null)
            throw ThrowOnImport;

        OnImportRungs?.Invoke(onlineImportOption, insertPosition, replaceCount, File.ReadAllText(xmlFileToImport));

        // Always capture the file content before the handler deletes the temp
        // file, so tests can assert on the exact XML the SDK would receive.
        LastCapturedXmlContent = File.ReadAllText(xmlFileToImport);
        XmlFileContentCaptor?.Invoke(LastCapturedXmlContent);

        CapturedXPaths.Add(xPath);
        CapturedInsertPositions.Add(insertPosition);
        CapturedReplaceCounts.Add(replaceCount);
        CapturedXmlFiles.Add(xmlFileToImport);
        CapturedImportOptions.Add(onlineImportOption);

        if (onlineImportOption == PartialImportOption.LeaveEdits)
            LeaveEditsRungTexts.AddRange(System.Xml.Linq.XDocument.Parse(LastCapturedXmlContent!)
                .Descendants("Rung")
                .Select(r => (r.Element("Text")?.Value ?? string.Empty).Replace("\r\n", "\n").Trim()));

        if (ReflectAcceptedImportsInExport &&
            onlineImportOption == PartialImportOption.AcceptEdits &&
            ExportContent is not null)
            ReflectImportedRungsAsPending(LastCapturedXmlContent!);

        if (onlineImportOption == PartialImportOption.FinalizeEdits && ExportContent is not null)
        {
            if (PostFinalizeExportContent is not null)
                ExportContent = PostFinalizeExportContent;
            else if (ReflectFinalizedImportsInExport)
                ReflectImportedRungsAsFinalized(LastCapturedXmlContent!, insertPosition, replaceCount);
        }

        return SdkTask.CompletedTask;
    }

    /// <summary>
    /// When <c>true</c> (default), a <c>FinalizeEdits</c> import makes the fake's later
    /// exports show the imported rungs as normal (<c>Type="N"</c>) rungs at the insert
    /// position, replacing <c>replaceCount</c> rungs — as a real controller does. Set to
    /// <c>false</c> to simulate a controller whose state does not reflect the import.
    /// </summary>
    public bool ReflectFinalizedImportsInExport { get; set; } = true;

    /// <summary>
    /// When non-<c>null</c>, a <c>FinalizeEdits</c> import replaces <see cref="ExportContent"/>
    /// with this document (simulating an arbitrary after-state, e.g. a leftover pending rung).
    /// </summary>
    public string? PostFinalizeExportContent { get; set; }

    private void ReflectImportedRungsAsFinalized(string importedXml, uint insertPosition, uint replaceCount)
    {
        var imported = System.Xml.Linq.XDocument.Parse(importedXml).Descendants("Rung").ToList();
        var export   = System.Xml.Linq.XDocument.Parse(ExportContent!);
        var content  = export.Descendants("RLLContent").FirstOrDefault();
        if (content is null)
            return;

        var rungs = content.Elements("Rung").ToList();
        rungs.RemoveRange((int)Math.Min(insertPosition, (uint)rungs.Count),
            (int)Math.Min(replaceCount, (uint)Math.Max(0, rungs.Count - (int)insertPosition)));
        rungs.InsertRange((int)Math.Min(insertPosition, (uint)rungs.Count),
            imported.Select(r => new System.Xml.Linq.XElement(r)));

        content.RemoveNodes();
        for (int i = 0; i < rungs.Count; i++)
        {
            rungs[i].SetAttributeValue("Number", i);
            rungs[i].SetAttributeValue("Type", "N");
            content.Add(rungs[i]);
        }
        ExportContent = export.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// When <c>true</c> (default), an <c>AcceptEdits</c> import makes the fake's
    /// subsequent exports show the imported rungs as pending <c>Type="I"</c> rungs
    /// (as a real controller does), so the post-publish census
    /// verifies direct controller publishes. Set to <c>false</c> to simulate a
    /// controller whose pending state does not reflect the import.
    /// </summary>
    public bool ReflectAcceptedImportsInExport { get; set; } = true;

    private void ReflectImportedRungsAsPending(string importedXml)
    {
        var imported = System.Xml.Linq.XDocument.Parse(importedXml)
            .Descendants("Rung").ToList();
        if (imported.Count == 0)
            return;

        var export  = System.Xml.Linq.XDocument.Parse(ExportContent!);
        var content = export.Descendants("RLLContent").FirstOrDefault();
        if (content is null)
            return;

        static string Norm(string? t) => (t ?? string.Empty).Replace("\r\n", "\n").Trim();
        var already = content.Elements("Rung")
            .Where(r => ((string?)r.Attribute("Type") ?? "N") is "I" or "IR")
            .Select(r => Norm(r.Element("Text")?.Value))
            .ToHashSet();

        foreach (var rung in imported)
        {
            var text = Norm(rung.Element("Text")?.Value);
            if (already.Contains(text))
                continue;
            content.Add(new System.Xml.Linq.XElement("Rung",
                new System.Xml.Linq.XAttribute("Number", content.Elements("Rung").Count()),
                new System.Xml.Linq.XAttribute("Type", "I"),
                new System.Xml.Linq.XElement("Text", new System.Xml.Linq.XCData(text))));
        }
        ExportContent = export.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    // ── All other ILogixProject members (stubs) ───────────────────────────────

    public Task<string> GetCommunicationsPathAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask SetCommunicationsPathAsync(string projectPath, CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask ChangeControllerTypeAsync(string processorTypeName, CancellationToken ct = default) => throw new NotImplementedException();
    // ── SaveAsAsync recording (the offline build check saves a copy of the session project) ──

    /// <summary>Paths passed to each <see cref="SaveAsAsync"/> call.</summary>
    public List<string> SaveAsPaths { get; } = new();

    /// <summary>When non-<c>null</c>, <see cref="SaveAsAsync"/> throws this (e.g. "cannot save with edits pending").</summary>
    public Exception? ThrowOnSaveAs { get; set; }

    /// <summary>
    /// When <c>true</c>, the saved image does NOT carry the staged (<c>LeaveEdits</c>) rungs, simulating a
    /// save-as that dropped the pending edit.
    /// </summary>
    public bool SaveAsOmitsStagedEdit { get; set; }

    private bool _savedAs;

    /// <summary>Normalised rung texts imported with <see cref="PartialImportOption.LeaveEdits"/>.</summary>
    public List<string> LeaveEditsRungTexts { get; } = new();

    /// <summary>
    /// The export document with the <c>LeaveEdits</c>-imported rungs added as pending (<c>Type="I"</c>) rungs when
    /// they are not already present: what a project image that carries the staged workstation edit exports.
    /// </summary>
    public string? ExportWithStagedEdit()
    {
        if (ExportContent is null || LeaveEditsRungTexts.Count == 0) return ExportContent;
        var export  = System.Xml.Linq.XDocument.Parse(ExportContent);
        var content = export.Descendants("RLLContent").FirstOrDefault();
        if (content is null) return ExportContent;
        static string Norm(string? t) => (t ?? string.Empty).Replace("\r\n", "\n").Trim();
        var present = content.Elements("Rung").Select(r => Norm(r.Element("Text")?.Value)).ToHashSet();
        foreach (var text in LeaveEditsRungTexts.Where(t => !present.Contains(t)))
            content.Add(new System.Xml.Linq.XElement("Rung",
                new System.Xml.Linq.XAttribute("Number", content.Elements("Rung").Count()),
                new System.Xml.Linq.XAttribute("Type", "I"),
                new System.Xml.Linq.XElement("Text", new System.Xml.Linq.XCData(text))));
        return export.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    public SdkTask SaveAsAsync(string savePath, bool force, bool detailedL5x, CancellationToken ct = default)
    {
        SaveAsPaths.Add(savePath);
        if (ThrowOnSaveAs is not null)
            throw ThrowOnSaveAs;
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        // The "project file": the image the copy is opened from. Tests that open a separate copy handle read it.
        File.WriteAllText(savePath, SaveAsOmitsStagedEdit ? (ExportContent ?? string.Empty) : (ExportWithStagedEdit() ?? string.Empty));
        _savedAs = true;
        return SdkTask.CompletedTask;
    }
    public SdkTask SaveAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public SdkTask DownloadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    /// <summary>Value returned by <see cref="ReadControllerModeAsync"/> (needed by the target="live" pending-edits gate read).</summary>
    public ControllerMode ControllerModeToReturn { get; set; } = ControllerMode.Run;
    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) => SdkTask.FromResult(ControllerModeToReturn);
    public SdkTask ChangeControllerModeAsync(RequestedControllerMode mode, CancellationToken ct = default) => throw new NotImplementedException();
    /// <summary>Build log events replayed to registered handlers on each <see cref="BuildAsync"/> call: (onErrorChannel, message).</summary>
    public List<(bool OnErrorChannel, string Message)> BuildLogEvents { get; } = new();

    /// <summary>Event handlers currently registered via <see cref="AddEventHandler"/>.</summary>
    public List<IOperationEvent> Handlers { get; } = new();

    public SdkTask BuildAsync(RequestedBuildTarget target, CancellationToken ct = default)
    {
        BuildCallCount++;
        CapturedBuildTargets.Add(target);
        if (ClearExportContentAfterBuild) ExportContent = null;
        foreach (var (onError, message) in BuildLogEvents)
            foreach (var h in Handlers.ToArray())
            {
                if (onError) h.Error("dummy.ACD", message); else h.Status("dummy.ACD", message);
            }
        if (ThrowOnBuild is not null)
            throw ThrowOnBuild;
        return SdkTask.CompletedTask;
    }
    /// <summary>Number of times <see cref="UploadAsync"/> has been called.</summary>
    public int UploadCallCount { get; private set; }

    /// <summary>When non-<c>null</c>, <see cref="UploadAsync"/> throws this instead of completing.</summary>
    public Exception? ThrowOnUpload { get; set; }

    /// <summary>When non-<c>null</c>, runs on a successful <see cref="UploadAsync"/> (a test rebuilds the fake's image here).</summary>
    public Action? OnUpload { get; set; }

    /// <summary>Order in which <see cref="GoOfflineAsync"/>, <see cref="UploadAsync"/> and <see cref="GoOnlineAsync"/> were called.</summary>
    public List<string> RestoreCallOrder { get; } = new();

    /// <summary>
    /// Upload as the real SDK does it: it pulls the controller image into the project. The fake has no controller, so a
    /// test describes the controller image by setting <see cref="OnUpload"/>; without one the call only counts.
    /// </summary>
    public SdkTask UploadAsync(CancellationToken ct = default)
    {
        UploadCallCount++;
        RestoreCallOrder.Add("upload");
        if (ThrowOnUpload is not null) throw ThrowOnUpload;
        OnUpload?.Invoke();
        return SdkTask.CompletedTask;
    }
    public SdkTask StoreImageOnSDCardAsync(RequestedLoadEvent loadEvent, RequestedLoadMode loadMode, AutomaticFirmwareUpdate afuSetting, string imageName, string imageNote) => throw new NotImplementedException();
    public SdkTask LoadImageFromSDCardAsync() => throw new NotImplementedException();
    public void AddEventHandler(IOperationEvent handler) => Handlers.Add(handler);
    public void ClearEventHandlers() => Handlers.Clear();
    public void RemoveEventHandler(IOperationEvent handler) => Handlers.Remove(handler);
    public Task<ByteString> GetTagValueAsync(string tagPath, OperationMode mode, DataType type) => throw new NotImplementedException();
    public Task<bool>   GetTagValueBOOLAsync(string tagPath, OperationMode mode)  => SdkTask.FromResult(false);
    public Task<sbyte>  GetTagValueSINTAsync(string tagPath, OperationMode mode)  => SdkTask.FromResult((sbyte)0);
    public Task<short>  GetTagValueINTAsync(string tagPath, OperationMode mode)   => SdkTask.FromResult((short)0);
    public Task<int>    GetTagValueDINTAsync(string tagPath, OperationMode mode)  => SdkTask.FromResult(0);
    public Task<long>   GetTagValueLINTAsync(string tagPath, OperationMode mode)  => SdkTask.FromResult(0L);
    public Task<byte>   GetTagValueUSINTAsync(string tagPath, OperationMode mode) => SdkTask.FromResult((byte)0);
    public Task<ushort> GetTagValueUINTAsync(string tagPath, OperationMode mode)  => SdkTask.FromResult((ushort)0);
    public Task<uint>   GetTagValueUDINTAsync(string tagPath, OperationMode mode) => SdkTask.FromResult(0u);
    public Task<ulong>  GetTagValueULINTAsync(string tagPath, OperationMode mode) => SdkTask.FromResult(0ul);
    public Task<string> GetTagValueSTRINGAsync(string tagPath, OperationMode mode) => SdkTask.FromResult(string.Empty);
    public Task<float>  GetTagValueREALAsync(string tagPath, OperationMode mode)  => SdkTask.FromResult(0f);
    public Task<double> GetTagValueLREALAsync(string tagPath, OperationMode mode) => SdkTask.FromResult(0.0);
    public SdkTask SetTagValueAsync(string tagPath, OperationMode mode, byte[] tagValue, DataType type) => throw new NotImplementedException();
    public SdkTask SetTagValueBOOLAsync(string tagPath, OperationMode mode, bool tagValue)    => SdkTask.CompletedTask;
    public SdkTask SetTagValueSINTAsync(string tagPath, OperationMode mode, sbyte tagValue)   => SdkTask.CompletedTask;
    public SdkTask SetTagValueINTAsync(string tagPath, OperationMode mode, short tagValue)    => SdkTask.CompletedTask;
    public SdkTask SetTagValueDINTAsync(string tagPath, OperationMode mode, int tagValue)     => SdkTask.CompletedTask;
    public SdkTask SetTagValueLINTAsync(string tagPath, OperationMode mode, long tagValue)    => SdkTask.CompletedTask;
    public SdkTask SetTagValueUSINTAsync(string tagPath, OperationMode mode, byte tagValue)   => SdkTask.CompletedTask;
    public SdkTask SetTagValueUINTAsync(string tagPath, OperationMode mode, ushort tagValue)  => SdkTask.CompletedTask;
    public SdkTask SetTagValueUDINTAsync(string tagPath, OperationMode mode, uint tagValue)   => SdkTask.CompletedTask;
    public SdkTask SetTagValueULINTAsync(string tagPath, OperationMode mode, ulong tagValue)  => SdkTask.CompletedTask;
    public SdkTask SetTagValueSTRINGAsync(string tagPath, OperationMode mode, string tagValue) => SdkTask.CompletedTask;
    public SdkTask SetTagValueREALAsync(string tagPath, OperationMode mode, float tagValue)   => SdkTask.CompletedTask;
    public SdkTask SetTagValueLREALAsync(string tagPath, OperationMode mode, double tagValue) => SdkTask.CompletedTask;

    // ── PartialExportToXmlFileAsync recording ─────────────────────────────────

    /// <summary>
    /// Content written to the export file by <see cref="PartialExportToXmlFileAsync"/>.
    /// Defaults to a realistic L5X export skeleton for MainProgram/MainRoutine with
    /// <c>SchemaRevision</c>, <c>SoftwareRevision</c>, and other SDK envelope attributes.
    /// Set to a custom string to override; set to <c>null</c> to restore the
    /// <see cref="NotImplementedException"/> throw behaviour for tests that do not
    /// exercise the export path.
    /// </summary>
    public string? ExportContent { get; set; } = DefaultExportContent;

    /// <summary>
    /// Default export content: a realistic L5X skeleton for MainProgram/MainRoutine
    /// carrying all SDK-required envelope attributes and eight NOP rungs (positions 0..8 are in range). Used so that
    /// existing tests exercising the empty-fragment controller publish path continue
    /// to work without per-test export setup.
    /// </summary>
    private static readonly string DefaultExportContent =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\">" +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\">" +
        "<RLLContent>" +
        string.Concat(Enumerable.Range(0, 8).Select(i =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>")) +
        "</RLLContent>" +
        "</Routine></Routines></Program></Programs>" +
        "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>XPath the handler exports to resolve program and routine names.</summary>
    public const string ProgramsXPath = "Controller/Programs";

    /// <summary>
    /// Builds a <c>Controller/Programs</c> export listing <c>MainProgram</c> with the given routines,
    /// for tests that need name resolution to succeed while the generic export stays unconfigured.
    /// </summary>
    public static string ProgramsExport(params string[] routines) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        string.Concat(routines.Select(r => $"<Routine Name=\"{r}\" Type=\"RLL\"/>")) +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>Per-routine export overrides, keyed by the exact export XPath; falls back to <see cref="ExportContent"/>.</summary>
    public Dictionary<string, string> ExportContentByXPath { get; } = new(StringComparer.Ordinal);

    /// <summary>Every XPath passed to <see cref="PartialExportToXmlFileAsync"/>, in call order.</summary>
    public List<string> ExportXPaths { get; } = new();

    /// <summary>XPath argument captured from the last <see cref="PartialExportToXmlFileAsync"/> call.</summary>
    public string? LastExportXPath { get; private set; }

    /// <summary>Number of times <see cref="PartialExportToXmlFileAsync"/> was called.</summary>
    public int ExportCallCount { get; private set; }

    /// <summary>When non-<c>null</c>, runs at the start of every <see cref="PartialExportToXmlFileAsync"/> call, with the XPath (a test models export time here).</summary>
    public Action<string>? OnExport { get; set; }

    public SdkTask PartialExportToXmlFileAsync(string xPath, string filePath, CancellationToken ct = default)
    {
        LastExportXPath = xPath;
        OnExport?.Invoke(xPath);
        ExportCallCount++;
        ExportXPaths.Add(xPath);
        if (!_savedAs && ExportContentByXPath.TryGetValue(xPath, out var perRoutineExport))
            File.WriteAllText(filePath, perRoutineExport);
        else if (ExportContent is not null)
            // After a save-as, this fake also stands in for the opened copy, which carries the staged edit.
            File.WriteAllText(filePath, _savedAs && !SaveAsOmitsStagedEdit ? ExportWithStagedEdit()! : MaskProgramExportBeforeImport(xPath, ExportContent));
        else
            throw new NotImplementedException($"{nameof(PartialExportToXmlFileAsync)} is not configured — set {nameof(ExportContent)} before calling.");
        return SdkTask.CompletedTask;
    }

    public SdkTask PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default) => throw new NotImplementedException();

    // ── PartialImportWithTargetFromXmlFileAsync recording ─────────────────────

    /// <summary>XPath arguments captured from each PartialImportWithTargetFromXmlFileAsync call.</summary>
    public List<string> CapturedWithTargetXPaths { get; } = new();

    /// <summary>targetName arguments captured from each PartialImportWithTargetFromXmlFileAsync call.</summary>
    public List<string> CapturedWithTargetNames { get; } = new();

    /// <summary>xmlFileToImport arguments captured from each PartialImportWithTargetFromXmlFileAsync call.</summary>
    public List<string> CapturedWithTargetXmlFiles { get; } = new();

    /// <summary>onlineImportOption arguments captured from each PartialImportWithTargetFromXmlFileAsync call.</summary>
    public List<PartialImportOption> CapturedWithTargetImportOptions { get; } = new();

    /// <summary>
    /// When non-<c>null</c>, <see cref="PartialImportWithTargetFromXmlFileAsync"/>
    /// throws this exception instead of recording the call.
    /// </summary>
    public Exception? ThrowOnImportWithTarget { get; set; }

    public SdkTask PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        if (ThrowOnImportWithTarget is not null)
            throw ThrowOnImportWithTarget;

        CapturedWithTargetXPaths.Add(xPath);
        CapturedWithTargetNames.Add(targetName);
        CapturedWithTargetXmlFiles.Add(xmlFileToImport);
        CapturedWithTargetImportOptions.Add(onlineImportOption);

        return SdkTask.CompletedTask;
    }
    /// <summary>Number of times <see cref="GoOnlineAsync"/> has been called.</summary>
    public int GoOnlineCallCount { get; private set; }

    /// <summary>When non-<c>null</c>, <see cref="GoOnlineAsync"/> throws this instead of completing.</summary>
    public Exception? ThrowOnGoOnline { get; set; }

    /// <summary>Go online. Counts the call; <see cref="ThrowOnGoOnline"/> makes it fail.</summary>
    public SdkTask GoOnlineAsync(CancellationToken ct = default)
    {
        GoOnlineCallCount++;
        RestoreCallOrder.Add("online");
        if (ThrowOnGoOnline is not null) throw ThrowOnGoOnline;
        return SdkTask.CompletedTask;
    }
    /// <summary>Number of times <see cref="GoOfflineAsync"/> has been called.</summary>
    public int GoOfflineCallCount { get; private set; }

    /// <summary>When non-<c>null</c>, <see cref="GoOfflineAsync"/> throws this instead of completing.</summary>
    public Exception? ThrowOnGoOffline { get; set; }

    public SdkTask GoOfflineAsync(CancellationToken ct = default)
    {
        GoOfflineCallCount++;
        RestoreCallOrder.Add("offline");
        if (ThrowOnGoOffline is not null)
            throw ThrowOnGoOffline;
        return SdkTask.CompletedTask;
    }
    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default)
    {
        ReadConnectedStateCallCount++;
        if (ThrowOnReadConnectedState is not null)
            throw ThrowOnReadConnectedState;
        return SdkTask.FromResult(ConnectedStateReturnValue);
    }
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

// ── ProposeRungEditHandlerTests ───────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="ProposeRungEditHandler"/> and
/// <see cref="LogixSdkTools.GetProposeRungEditResponseAsync"/>.
/// </summary>
public class ProposeRungEditHandlerTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private const string ValidXPath    = "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";
    // Reference the shared production constant so that tests and product surface
    // can never drift to different L5X envelope shapes.
    private const string ValidFragment = ProposeRungEditHandler.MinimalNopFragment;

    // ── Helper: build an open session backed by a recording fake ─────────────

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        // Mark as online and correlated so the handler's pre-flight guard passes.
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    // ── External Access forced to Read Only on caller-fragment new tags ───────

    /// <summary>
    /// A caller fragment declaring a new HMIBC tag Read Only is imported Read/Write (Logix
    /// rejects Read Only for HMIBC) and reported with the reason; other types stay Read Only.
    /// </summary>
    [Fact]
    public async Task CallerFragment_NewHmibcTag_ForcedReadWrite_ReportedWithReason()
    {
        const string fragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
            "<Tags Use=\"Context\">" +
            "<Tag Name=\"HmiBc\" DataType=\"HMIBC\" ExternalAccess=\"Read Only\"/>" +
            "<Tag Name=\"NewRw\" DataType=\"BOOL\" ExternalAccess=\"Read/Write\"/>" +
            "</Tags><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
            "<Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, fragment, session, target: "controller");

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(2, proposed.ExternalAccessForced!.Count);
        Assert.StartsWith("HmiBc: Read/Write (", proposed.ExternalAccessForced[0]);
        Assert.Equal("NewRw", proposed.ExternalAccessForced[1]);

        var sent = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        string? Access(string name) => (string?)sent.Descendants("Tag")
            .Single(t => (string?)t.Attribute("Name") == name).Attribute("ExternalAccess");
        Assert.Equal("Read/Write", Access("HmiBc"));
        Assert.Equal("Read Only", Access("NewRw"));
    }

    /// <summary>
    /// G28 (a): after an <c>externalAccessForced</c> Read/Write tag (HMIBC), the publish message does not call the
    /// created tags External Access Read Only, and the tag's <c>writableViaLogixWrite</c> is true; a Read Only tag
    /// of the same publish stays false.
    /// </summary>
    [Fact]
    public async Task Publish_AfterExternalAccessForcedReadWrite_OmitsReadOnlyWording_AndTagIsWritable()
    {
        const string fragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
            "<Tags Use=\"Context\">" +
            "<Tag Name=\"HmiBc\" DataType=\"HMIBC\" ExternalAccess=\"Read Only\"/>" +
            "</Tags><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
            "<Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, fragment, session, target: "controller");

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Single(proposed.ExternalAccessForced!);
        Assert.DoesNotContain("Created tags are External Access Read Only", proposed.Message, StringComparison.Ordinal);
        Assert.Contains("HmiBc", proposed.Message, StringComparison.Ordinal);
        Assert.Contains("External Access Read/Write", proposed.Message, StringComparison.Ordinal);
        var entry = Assert.Single(proposed.TagsCreated!, e => e.TagName == "HmiBc");
        Assert.True(entry.WritableViaLogixWrite);
    }

    /// <summary>The disclosure still says Read Only for Read Only tags, and splits a mixed publish by tag.</summary>
    [Fact]
    public void TagDisclosure_ReadOnlyTagsKeepTheReadOnlyWording_MixedPublishSplitsByTag()
    {
        var readOnly = ProposeRungEditHandler.BuildTagDisclosure(
            new[] { new PublishedTagEntry("Lamp", false) }, null);
        Assert.Contains("Created tags are External Access Read Only", readOnly, StringComparison.Ordinal);

        var mixed = ProposeRungEditHandler.BuildTagDisclosure(
            new[] { new PublishedTagEntry("Lamp", false), new PublishedTagEntry("HmiBc", true) },
            new[] { "HmiBc: Read/Write (HMIBC tags cannot be imported External Access Read Only)" });
        Assert.DoesNotContain("Created tags are External Access Read Only", mixed, StringComparison.Ordinal);
        Assert.Contains("HmiBc was created External Access Read/Write", mixed, StringComparison.Ordinal);
        Assert.Contains("Lamp is External Access Read Only", mixed, StringComparison.Ordinal);
    }

    /// <summary>
    /// G28 (b): a direct controller publish of the call's own rungs (nothing staged) is not worded
    /// "N staged rungs sent".
    /// </summary>
    [Fact]
    public async Task DirectPublish_OfOwnRungs_IsNotWorded_StagedRungsSent()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "controller");

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.DoesNotContain("staged rung", proposed.Message, StringComparison.Ordinal);
        Assert.Contains("1 rung sent", proposed.Message, StringComparison.Ordinal);

        Assert.Equal("2 rungs sent, replacing no rungs",
            ProposeRungEditHandler.DescribePublishContents(2, 0, 0, ownRungs: 2));
        Assert.Equal("1 staged rung sent, replacing no rungs",
            ProposeRungEditHandler.DescribePublishContents(1, 0, 0));
        Assert.Equal("3 rungs (1 staged, 2 from this call) sent, replacing no rungs",
            ProposeRungEditHandler.DescribePublishContents(3, 0, 0, ownRungs: 2));
    }

    /// <summary>
    /// A caller l5xFragment declaring new tags as Read/Write (or with no ExternalAccess)
    /// is imported with those tags Read Only and the forced names are reported; a tag in
    /// the census and a UDT member-level access are left alone.
    /// </summary>
    [Fact]
    public async Task CallerFragment_NewTagReadWrite_ForcedReadOnly_Reported()
    {
        const string fragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
            "<DataTypes><DataType Name=\"MyUdt\"><Members>" +
            "<Member Name=\"M\" DataType=\"DINT\" ExternalAccess=\"Read/Write\"/>" +
            "</Members></DataType></DataTypes>" +
            "<Tags Use=\"Context\">" +
            "<Tag Name=\"ExistingTag\" DataType=\"BOOL\" ExternalAccess=\"Read/Write\"/>" +
            "<Tag Name=\"NewRw\" DataType=\"BOOL\" ExternalAccess=\"Read/Write\"/>" +
            "<Tag Name=\"NewNone\" DataType=\"BOOL\"/>" +
            "<Tag Name=\"NewRo\" DataType=\"BOOL\" ExternalAccess=\"Read Only\"/>" +
            "</Tags><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
            "<Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, fragment, session,
            target: "controller",
            preExistingTagNames: new[] { "ExistingTag" });

        var proposed = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(new[] { "NewRw", "NewNone" }, proposed.ExternalAccessForced);

        var sent = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        string? Access(string name) => (string?)sent.Descendants("Tag")
            .Single(t => (string?)t.Attribute("Name") == name).Attribute("ExternalAccess");
        Assert.Equal("Read Only", Access("NewRw"));
        Assert.Equal("Read Only", Access("NewNone"));
        Assert.Equal("Read Only", Access("NewRo"));
        Assert.Equal("Read/Write", Access("ExistingTag"));
        Assert.Equal("Read/Write", (string?)sent.Descendants("Member").Single().Attribute("ExternalAccess"));
    }

    /// <summary>
    /// A caller l5xFragment re-declaring a census tag with a different ExternalAccess is
    /// refused (kind ExternalAccessChangeRefused, naming the tag and both values) on the
    /// workstation, controller and live targets, and no import is attempted.
    /// </summary>
    [Theory]
    [InlineData("workstation")]
    [InlineData("controller")]
    [InlineData("live")]
    public async Task CallerFragment_ExistingTagExternalAccessChange_Refused_NoImport(string target)
    {
        const string fragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
            "<Tags Use=\"Context\">" +
            "<Tag Name=\"ExistingTag\" DataType=\"BOOL\" ExternalAccess=\"Read/Write\"/>" +
            "<Tag Name=\"NewRo\" DataType=\"BOOL\" ExternalAccess=\"Read Only\"/>" +
            "</Tags><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
            "<Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, fragment, session,
            target: target,
            preExistingTagNames: new[] { "ExistingTag" },
            preExistingTagAccess: new Dictionary<string, string> { ["ExistingTag"] = "Read Only" });

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ExternalAccessChangeRefused", error.Kind);
        Assert.Contains("ExistingTag", error.Error);
        Assert.Contains("Read/Write", error.Error);
        Assert.Contains("Read Only", error.Error);
        Assert.Contains("External Access is the engineer's write grant; change it in Logix Designer", error.Error);
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
    }

    // ── Online and correlated session guard ───────────────────────────────────

    /// <summary>
    /// (guard-a) Returns ProposeRungEditError when the session is not online,
    ///           naming the failed condition in the error message.
    ///           No SDK call should be made.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionNotOnline_ReturnsError_NamingFailedCondition()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.IsOnline = false;   // simulate not-online session

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("online", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correlated session", err.Error, StringComparison.OrdinalIgnoreCase);
        // Guard fires before any import logic — no SDK call made.
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// G25: staging on an SDK offline session returns the typed kind <c>SessionNotOnline</c>, and no SDK call is made.
    /// </summary>
    [Fact]
    public async Task Stage_OnSdkOfflineSession_ReturnsSessionNotOnline()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.IsOnline = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "workstation");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("SessionNotOnline", err.Kind);
        Assert.Equal(ErrorKinds.SessionNotOnline, err.Kind);
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// (guard-b) Returns ProposeRungEditError when the session is not correlated,
    ///           naming the failed condition in the error message.
    ///           No SDK call should be made.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionNotCorrelated_ReturnsError_NamingFailedCondition()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        session.IsCorrelated = false;   // simulate uncorrelated session

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("correlated", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correlated session", err.Error, StringComparison.OrdinalIgnoreCase);
        // Guard fires before any import logic — no SDK call made.
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// (guard-c) The not-online guard fires before input validation: even when
    ///           routineXPath is empty the error reports the session condition,
    ///           not the invalid argument.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionNotOnline_GuardFiresBeforeInputValidation()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.IsOnline = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            "", 0, 0, ValidFragment, session);  // empty XPath

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        // Error message must describe the session condition, not the empty XPath.
        Assert.Contains("online", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("routineXPath", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (guard-d) The not-correlated guard fires before input validation: even when
    ///           routineXPath is empty the error reports the session condition.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionNotCorrelated_GuardFiresBeforeInputValidation()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.IsCorrelated = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            "", 0, 0, ValidFragment, session);  // empty XPath

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("correlated", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("routineXPath", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (guard-e) The guard error never has status 'proposed' — the result is always
    ///           a ProposeRungEditError, never a ProposeRungEditResult.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionNotOnline_NeverReturnsProposedStatus()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.IsOnline = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        // Must never be a success result — the guard ensures status 'proposed' is
        // never returned when the session is not online or not correlated.
        Assert.IsNotType<ProposeRungEditResult>(result);
    }

    /// <summary>
    /// (guard-f) The guard error never has status 'proposed' — not-correlated case.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionNotCorrelated_NeverReturnsProposedStatus()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.IsCorrelated = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsNotType<ProposeRungEditResult>(result);
    }

    // ── Input validation — empty/blank routineXPath ───────────────────────────

    /// <summary>
    /// (a) Returns ProposeRungEditError when routineXPath is empty.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EmptyRoutineXPath_ReturnsError()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            "", 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("routineXPath", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (b) Returns ProposeRungEditError when routineXPath is whitespace-only.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhitespaceRoutineXPath_ReturnsError()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            "   ", 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
    }

    // ── Input validation — empty/blank l5xFragment ────────────────────────────

    /// <summary>
    /// (c) Returns ProposeRungEditError when l5xFragment is empty.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EmptyL5xFragment_ReturnsError()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("l5xFragment", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (d) Returns ProposeRungEditError when l5xFragment is whitespace-only.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhitespaceL5xFragment_ReturnsError()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "   ", session);

        Assert.IsType<ProposeRungEditError>(result);
    }

    // ── Success path: result type and field values ────────────────────────────

    /// <summary>
    /// (e) Returns ProposeRungEditResult on a successful SDK call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ValidInputs_ReturnsProposeRungEditResult()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 1, ValidFragment, session);

        Assert.IsType<ProposeRungEditResult>(result);
    }

    /// <summary>
    /// (f) Returned result has Status="proposed".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccess_StatusIsProposed()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.Equal("proposed", result.Status);
    }

    /// <summary>
    /// (g) Returned result echoes back the routineXPath, insertPosition, and replaceCount.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccess_ResultEchoesInputParameters()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 3, 2, ValidFragment, session);

        Assert.Equal(ValidXPath, result.RoutineXPath);
        Assert.Equal(3u,         result.InsertPosition);
        Assert.Equal(2u,         result.ReplaceCount);
    }

    // ── SDK call arguments ────────────────────────────────────────────────────

    /// <summary>
    /// (h) Calls PartialImportRungsFromXmlFileAsync with the correct routineXPath.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CallsPartialImportRungs_WithCorrectXPath()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.Single(fake.CapturedXPaths);
        Assert.Equal(ValidXPath, fake.CapturedXPaths[0]);
    }

    /// <summary>
    /// (i) Calls PartialImportRungsFromXmlFileAsync with the correct insertPosition.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CallsPartialImportRungs_WithCorrectInsertPosition()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 5, 0, ValidFragment, session);

        Assert.Equal(5u, fake.CapturedInsertPositions[0]);
    }

    /// <summary>
    /// (j) Calls PartialImportRungsFromXmlFileAsync with the correct replaceCount.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CallsPartialImportRungs_WithCorrectReplaceCount()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 3, ValidFragment, session);

        Assert.Equal(3u, fake.CapturedReplaceCounts[0]);
    }

    /// <summary>
    /// (k) The workstation path (target=null) calls PartialImportRungsFromXmlFileAsync
    ///     with LeaveEdits — the edit is held pending for operator review.
    ///     The controller path uses AcceptEdits (see target-d).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_PassesLeaveEdits_ToPartialImport()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.Single(fake.CapturedImportOptions);
        Assert.Equal(
            RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption.LeaveEdits,
            fake.CapturedImportOptions[0]);
    }

    /// <summary>
    /// (l) The temp file path passed to the SDK exists on disk when the SDK call is
    ///     executing (verified by capturing the path and checking it from the fake).
    ///     After execution the file must be deleted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TempFileExistsDuringSdkCall_AndIsDeletedAfterwards()
    {
        string? capturedTempFile = null;
        bool    existedDuringCall = false;

        var spy = new SpyFakeLogixProject(tempPath =>
        {
            capturedTempFile   = tempPath;
            existedDuringCall  = File.Exists(tempPath);
        });

        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(spy));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.NotNull(capturedTempFile);
        Assert.True(existedDuringCall,  "Temp file must exist while the SDK call is in progress.");
        Assert.False(File.Exists(capturedTempFile), "Temp file must be deleted after the call.");
    }

    /// <summary>
    /// (m) Temp file is deleted even when the SDK call throws an exception.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TempFileDeletedAfterSdkFailure()
    {
        string? capturedTempFile = null;

        var spy = new SpyFakeLogixProject(tempPath =>
        {
            capturedTempFile = tempPath;
            throw new InvalidOperationException("SDK exploded");
        });

        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(spy));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        Assert.NotNull(capturedTempFile);
        Assert.False(File.Exists(capturedTempFile), "Temp file must be deleted after SDK failure.");
    }

    // ── SDK failure: structured error returned ────────────────────────────────

    /// <summary>
    /// (n) Returns ProposeRungEditError when the SDK throws TagServiceException.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SdkThrowsTagServiceException_ReturnsError()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport  = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.Unknown, "simulated SDK failure"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("SDK error", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (o) Returns ProposeRungEditError when the SDK throws a generic exception.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SdkThrowsGenericException_ReturnsError()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport  = new InvalidOperationException("unexpected sdk error");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("unexpected", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Post-import ReadConnectedStateAsync verification ──────────────────────

    /// <summary>
    /// (p1) ReadConnectedStateAsync is called exactly once after a successful import.
    ///      A successful import return code alone is insufficient evidence; the
    ///      handler must also verify the connected state.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccessfulImport_CallsReadConnectedStateOnce()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.Equal(1, fake.ReadConnectedStateCallCount);
    }

    /// <summary>
    /// (p2) an earlier change: Connected is equivalent to Online for the post-import state check, so it is no longer
    ///      reported as "not Online". The result is never the untyped "may not have been applied".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReadConnectedStateReturnsConnected_IsTreatedAsOnline()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ConnectedStateReturnValue =
            RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Connected;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        if (result is ProposeRungEditError err)
        {
            Assert.Equal("PostImportCheckInconclusive", err.Kind);
            Assert.DoesNotContain("may not have been applied", err.Error);
        }
    }

    /// <summary>
    /// (p3) When ReadConnectedStateAsync returns Offline, the error names Offline
    ///      so the caller knows the session lost its connection.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReadConnectedStateReturnsOffline_ErrorNamesOfflineState()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ConnectedStateReturnValue =
            RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Offline;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("Offline", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (p4) When ReadConnectedStateAsync throws TagServiceException, returns
    ///      ProposeRungEditError (not a success result).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReadConnectedStateThrowsTagServiceException_ReturnsError()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnReadConnectedState = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.Unknown, "state read failed"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("state check failed", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (p5) When ReadConnectedStateAsync throws a generic exception, returns
    ///      ProposeRungEditError.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReadConnectedStateThrowsGenericException_ReturnsError()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnReadConnectedState = new InvalidOperationException("state read exploded");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("ReadConnectedStateAsync failed", err.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (p6) When the import fails, ReadConnectedStateAsync is NOT called —
    ///      the state check must not fire if the import itself was rejected.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ImportFails_ReadConnectedStateNotCalled()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport = new InvalidOperationException("import exploded");

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.Equal(0, fake.ReadConnectedStateCallCount);
    }

    /// <summary>
    /// (p6b) When ReadConnectedStateAsync returns Online, the returned result
    ///       has Status="proposed" — confirming the state is the gate for success.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReadConnectedStateReturnsOnline_StatusIsProposed()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        // Default is already Online; set explicitly to make test intent clear.
        fake.ConnectedStateReturnValue =
            RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Online;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", ((ProposeRungEditResult)result).Status);
    }

    // ── Pending-edit message content ──────────────────────────────────────────

    /// <summary>
    /// (p7) The success message explicitly states the edit is staged locally and not
    ///      sent to the controller, so a model cannot mistakenly report the change as active.
    /// the message also names tag declarations to reflect that both
    ///      rungs and tags are held locally (neither reaches the controller at stage time).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccess_MessageStatesEditIsPendingNotLive()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        Assert.Contains("STAGED",         result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT SENT",       result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tag declarations", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (p8) The success message states neither rungs nor tag declarations have reached
    ///      the controller, preventing a model from implying the controller programme
    ///      has changed., the message covers both rungs and tags.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccess_MessageStatesEditNotAcceptedOrApplied()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        // Must state explicitly that the edit has not reached the controller.
        Assert.Contains("NOT",        result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("controller", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (p9) The success message includes the routineXPath so the operator knows
    ///      exactly which routine holds the pending edit.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccess_MessageIncludesRoutineXPath()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 4, 2, ValidFragment, session);

        Assert.Contains(ValidXPath, result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (p10) The success message includes the insertPosition and replaceCount so
    ///       the caller can confirm the rung range that is pending.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnSuccess_MessageIncludesInsertPositionAndReplaceCount()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 5, 3, ValidFragment, session);

        Assert.Contains("5", result.Message, StringComparison.Ordinal);
        Assert.Contains("3", result.Message, StringComparison.Ordinal);
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    /// <summary>
    /// (p) Success result serializes to JSON with expected keys.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SuccessResult_SerializesToJsonWithExpectedKeys()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, ValidFragment, session);
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("status",         out _));
        Assert.True(doc.RootElement.TryGetProperty("routineXPath",   out _));
        Assert.True(doc.RootElement.TryGetProperty("insertPosition", out _));
        Assert.True(doc.RootElement.TryGetProperty("replaceCount",   out _));
        Assert.True(doc.RootElement.TryGetProperty("message",        out _));
    }

    /// <summary>
    /// (q) Error result serializes to JSON with an "error" key.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ErrorResult_SerializesToJsonWithErrorKey()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            "", 0, 0, ValidFragment, session);
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ── LogixSdkTools.GetProposeRungEditResponseAsync (MCP layer) ─────────────

    /// <summary>
    /// (r) LogixSdkTools.GetProposeRungEditResponseAsync returns error JSON when
    ///     no project is loaded.
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_ReturnsErrorJsonWhenNoProjectLoaded()
    {
        var svc   = new ProjectService();   // no project loaded
        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    /// <summary>
    /// (s) LogixSdkTools.GetProposeRungEditResponseAsync returns JSON with "proposed"
    ///     status when called with a valid injected session and a loaded project.
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_ReturnsProposedJsonOnSuccess()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

        var (session, _) = await BuildOpenSessionAsync();
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("proposed", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// (t) LogixSdkTools.GetProposeRungEditResponseAsync returns error JSON when
    ///     routineXPath is empty.
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_ReturnsErrorJsonForEmptyXPath()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());
        var (session, _) = await BuildOpenSessionAsync();

        var json = await tools.GetProposeRungEditResponseAsync(
            "", 0, 0, ValidFragment, session);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ── Write-gate scan tests (gate is now advisory) ───────────────────────────

    // Helpers: derive the Demo.L5X policy once per test via GateEngine.
    private static WriteGatePolicy LoadDemoPolicy() =>
        GateEngine.DerivePolicy(L5X.Load(FixturePath), GateConfig.Default);

    // Fragment that writes Safety_OK (SafetyTag in Demo.L5X policy — scan identifies it,
    // gate is advisory so no denial is returned).
    private const string FragmentWritesSafetyOk =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Cmd_MotorStart)OTE(Safety_OK);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    // Fragment that writes Motor_Run (ProgramWritten in Demo.L5X policy — scan identifies it,
    // gate is advisory so no denial is returned).
    private const string FragmentWritesMotorRun =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[OTE(Motor_Run);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    // Fragment that writes Cmd_MotorStart only (granted by Demo.L5X policy).
    private const string FragmentWritesCmdGranted =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(SomeSensor)OTE(Cmd_MotorStart);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>
    /// (u) When a policy is supplied and the fragment writes a safety-tagged tag
    ///     (Safety_OK), the scan runs but no denial is returned — the SDK is
    ///     called and the result is a success.  The gate is now advisory only.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_SafetyTaggedWrite_ScanRunsNoDenial_SdkCalled()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // Scan runs but no denial — result is a success.
        Assert.IsType<ProposeRungEditResult>(result);
        // SDK import was called (not blocked).
        Assert.Single(fake.CapturedXPaths);
    }

    /// <summary>
    /// (v) When a policy is supplied and the fragment writes a safety-tagged tag
    ///     (Safety_OK), the result is a ProposeRungEditResult with status="proposed",
    ///     confirming the gate no longer causes an early return.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_SafetyTaggedWrite_ReturnsProposedStatus()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // Gate is advisory: result is a success, not a denial.
        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);
    }

    /// <summary>
    /// (w) When the fragment writes a program-written tag (Motor_Run) and a
    ///     policy is supplied, the scan runs but no denial is returned — the
    ///     SDK is called and the result is a success.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_ProgramWrittenTagWrite_ScanRunsNoDenial_SdkCalled()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesMotorRun, session, policy);

        // Scan runs but no denial.
        Assert.IsType<ProposeRungEditResult>(result);
        // SDK was called.
        Assert.Single(fake.CapturedXPaths);
    }

    /// <summary>
    /// (x) When the fragment writes only a granted tag (Cmd_MotorStart),
    ///     ExecuteAsync succeeds and calls the SDK.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_GrantedTag_Succeeds()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesCmdGranted, session, policy);

        Assert.IsType<ProposeRungEditResult>(result);
        // SDK import was called.
        Assert.Single(fake.CapturedXPaths);
    }

    /// <summary>
    /// (y) When no policy is supplied (null), no gate check is performed —
    ///     even a fragment that writes Safety_OK reaches the SDK.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullPolicy_NoGateCheck_SdkIsCalled()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // No policy supplied → gate check skipped.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy: null);

        // Without a policy the SDK is called regardless of what the rung writes.
        Assert.Single(fake.CapturedXPaths);
    }

    /// <summary>
    /// (z) When a policy is supplied and the fragment writes a denied tag, the scan
    ///     runs but no denial is returned — the SDK import IS called (gate is advisory).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_DeniedTag_SdkIsStillCalled()
    {
        bool sdkCallMade = false;
        var spy = new SpyFakeLogixProject(_ => sdkCallMade = true);
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(spy));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;

        var policy = LoadDemoPolicy();
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // Gate is advisory: result is success, SDK was called.
        Assert.IsType<ProposeRungEditResult>(result);
        Assert.True(sdkCallMade,
            "The SDK import callback must fire even when the written tag was previously denied.");
    }

    /// <summary>
    /// (aa) LogixSdkTools.GetProposeRungEditResponseAsync no longer denies fragments
    ///      that write safety-tagged tags — the scan runs but the gate is advisory.
    ///      A fragment that writes Safety_OK now returns success JSON (status="proposed").
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_WithPolicy_SafetyTagWrite_ReturnsSuccessJson()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());
        var (session, _) = await BuildOpenSessionAsync();

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session);

        var doc = JsonDocument.Parse(json);
        // Gate is advisory: result is proposed, not an error.
        Assert.True(doc.RootElement.TryGetProperty("status", out var statusProp));
        Assert.Equal("proposed", statusProp.GetString());
    }

    // ── Gate check on controller dispatch path ────────────────────────────────

    /// <summary>
    /// (ab) When target="controller" is specified together with a policy, the scan
    ///      runs but no denial is returned — the gate is advisory.  The SDK import
    ///      IS called for the controller path just as for the workstation path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetController_WithPolicy_DeniedTag_SdkIsStillCalled()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        // FragmentWritesSafetyOk writes Safety_OK — previously denied, now advisory.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy,
            target: "controller");

        // Gate is advisory: result is a success.
        Assert.IsType<ProposeRungEditResult>(result);

        // The rung API must have been called (no denial).
        Assert.Single(fake.CapturedXPaths);
        // The WithTarget overload must never be used for rungs.
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// (ac) When target="controller" is specified together with a policy that grants
    ///      the written tag, the gate passes and the controller SDK overload is called.
    ///      This proves the gate does not incorrectly block the controller path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetController_WithPolicy_GrantedTag_CallsControllerSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        // FragmentWritesCmdGranted writes Cmd_MotorStart — granted by the demo policy.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesCmdGranted, session, policy,
            target: "controller");

        // Gate passes: result is a success.
        Assert.IsType<ProposeRungEditResult>(result);

        // The rung API is called exactly once, for either destination.
        Assert.Single(fake.CapturedXPaths);
        // The WithTarget overload (named-object import) must never be used for rungs.
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// A successful target="controller" result carries a zoneNote naming the program, and
    /// the workstation path does not.
    /// </summary>
    [Fact]
    public async Task ControllerTarget_Success_CarriesZoneNote()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "controller");

        var program = ProposeRungEditHandler.ProgramNamePattern.Match(ValidXPath).Groups[1].Value;
        Assert.Equal(
            $"this pending zone blocks further imports into '{program}' until it is accepted " +
            "or cancelled in Studio (or with the Studio lifecycle tools when " +
            "LGX_ENABLE_STUDIO_AUTOMATION=1); a live change could instead be undone with one " +
            "SDK call (undo)",
            result.ZoneNote);
        Assert.Contains("\"zoneNote\"", System.Text.Json.JsonSerializer.Serialize(result));

        var (wsSession, _) = await BuildOpenSessionAsync();
        var ws = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, wsSession);
        Assert.Null(ws.ZoneNote);
    }

    // ── Gate annotation tests ─────────────────────────────────────────────────

    /// <summary>
    /// (ann-a) When a policy is provided and the fragment writes a safety-tagged tag
    ///         (Safety_OK), the result carries a gateAnnotations entry with
    ///         classification="SafetyTag".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_SafetyTaggedWrite_ResultCarriesGateAnnotation()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        // GateAnnotations must be non-null: fragment writes Safety_OK.
        Assert.NotNull(result.GateAnnotations);
        Assert.NotEmpty(result.GateAnnotations!);

        // Find the annotation for Safety_OK.
        var entry = result.GateAnnotations!.FirstOrDefault(a =>
            string.Equals(a.Tag, "Safety_OK", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.Equal("SafetyTag", entry!.Classification);
        // Provenance must identify the fragment location and instruction.
        Assert.Contains("via OTE", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (ann-b) When a policy is provided and the fragment writes a program-written tag
    ///         (Motor_Run), the result carries a gateAnnotations entry with
    ///         classification="ProgramWritten".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_ProgramWrittenTagWrite_ResultCarriesGateAnnotation()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesMotorRun, session, policy);

        Assert.NotNull(result.GateAnnotations);
        Assert.NotEmpty(result.GateAnnotations!);

        var entry = result.GateAnnotations!.FirstOrDefault(a =>
            string.Equals(a.Tag, "Motor_Run", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.Equal("ProgramWritten", entry!.Classification);
        Assert.Contains("via OTE", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (ann-c) When a policy is provided and the fragment writes a granted tag
    ///         (Cmd_MotorStart), the result carries a gateAnnotations entry with
    ///         classification="granted".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_GrantedTagWrite_ResultCarriesGrantedAnnotation()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesCmdGranted, session, policy);

        Assert.NotNull(result.GateAnnotations);
        Assert.NotEmpty(result.GateAnnotations!);

        var entry = result.GateAnnotations!.FirstOrDefault(a =>
            string.Equals(a.Tag, "Cmd_MotorStart", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.Equal("granted", entry!.Classification);
        Assert.Contains("via OTE", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (ann-d) When no policy is provided, the result has GateAnnotations == null —
    ///         gate classification requires a policy.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullPolicy_GateAnnotationsIsNull()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy: null);

        // No policy → no annotations.
        Assert.Null(result.GateAnnotations);
    }

    /// <summary>
    /// (ann-e) When a policy is provided but the fragment has no write operands
    ///         (only reads), the result has GateAnnotations == null.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_NoWriteOperands_GateAnnotationsIsNull()
    {
        // Fragment that only reads — no write operands.
        const string ReadOnlyFragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Cmd_MotorStart)NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ReadOnlyFragment, session, policy);

        // No proven write operands → annotations are null.
        Assert.Null(result.GateAnnotations);
    }

    /// <summary>
    /// (ann-f) gateAnnotations serializes to JSON correctly: the field appears
    ///         in the JSON output with tag, classification, and provenance keys.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_GateAnnotations_SerializesToJson()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);
        var json = JsonSerializer.Serialize(result);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("gateAnnotations", out var ann),
            $"Expected gateAnnotations in JSON. Got: {json}");
        Assert.Equal(JsonValueKind.Array, ann.ValueKind);

        var first = ann.EnumerateArray().First();
        Assert.True(first.TryGetProperty("tag",            out _));
        Assert.True(first.TryGetProperty("classification", out _));
        Assert.True(first.TryGetProperty("provenance",     out _));
    }

    /// <summary>
    /// (ann-g) Offline path: when a policy is provided and the fragment writes a
    ///         safety-tagged tag, ExecuteOfflineAsync carries gateAnnotations in the
    ///         result with classification="SafetyTag".
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_WithPolicy_SafetyTaggedWrite_ResultCarriesGateAnnotation()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var policy = LoadDemoPolicy();
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentWritesSafetyOk, tempFile, policy);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.NotNull(result.GateAnnotations);
            Assert.NotEmpty(result.GateAnnotations!);

            var entry = result.GateAnnotations!.FirstOrDefault(a =>
                string.Equals(a.Tag, "Safety_OK", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry);
            Assert.Equal("SafetyTag", entry!.Classification);
            Assert.Contains("via OTE", entry.Provenance, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (ann-h) Offline path: when no policy is provided, GateAnnotations is null.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_NullPolicy_GateAnnotationsIsNull()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentWritesSafetyOk, tempFile, policy: null);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.Null(result.GateAnnotations);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Offline file-save mode (ExecuteOfflineAsync) ───────────────────────────

    // Shared helper: create a temp copy of Demo.L5X for offline tests.
    // Tests must not mutate the fixture directly; each test gets its own copy.
    private static string CreateTempL5XCopy()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"OfflineTest_{Guid.NewGuid():N}.L5X");
        File.Copy(FixturePath, tempPath, overwrite: true);
        return tempPath;
    }

    /// <summary>
    /// (off-a) ExecuteOfflineAsync returns ProposeRungEditOfflineResult (not an error)
    ///         for valid inputs and a real L5X file.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_ValidInputs_ReturnsOfflineResult()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            Assert.IsType<ProposeRungEditOfflineResult>(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-b) The returned result has status="saved-to-file" (never "proposed").
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_OnSuccess_StatusIsSavedToFile()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.Equal("saved-to-file", result.Status);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-c) The returned result includes the project file path.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_OnSuccess_ResultIncludesFilePath()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.False(string.IsNullOrWhiteSpace(result.FilePath));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-d) The response message explicitly states the file was modified and
    ///         no controller was contacted.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_OnSuccess_MessageStatesFileModifiedNoController()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            // Must state the file was modified on disk.
            Assert.Contains("FILE MODIFIED", result.Message, StringComparison.OrdinalIgnoreCase);
            // Must explicitly state no controller was contacted.
            Assert.Contains("NO CONTROLLER CONTACTED", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-e) The response message must NOT contain the word "proposed" —
    ///         callers must not be misled into thinking this created a pending
    ///         controller edit.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_OnSuccess_MessageDoesNotContainProposed()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.DoesNotContain("proposed", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-f) The file is actually modified on disk: the saved file contains
    ///         the rung text from the fragment.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_OnSuccess_FileSavedToDisk()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var rawBefore = File.ReadAllText(tempFile);

            await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            var rawAfter = File.ReadAllText(tempFile);

            // The file should have changed (NOP rung added).
            Assert.NotEqual(rawBefore, rawAfter);
            // The NOP rung text should be present in the saved file.
            Assert.Contains("NOP();", rawAfter, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-g) Echoes back routineXPath, insertPosition, and replaceCount.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_OnSuccess_ResultEchoesInputParameters()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 3, 2, ValidFragment, tempFile);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.Equal(ValidXPath, result.RoutineXPath);
            Assert.Equal(3u,         result.InsertPosition);
            Assert.Equal(2u,         result.ReplaceCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-h) Returns ProposeRungEditError when routineXPath is empty.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_EmptyRoutineXPath_ReturnsError()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                "", 0, 0, ValidFragment, tempFile);

            Assert.IsType<ProposeRungEditError>(result);
            Assert.Contains("routineXPath", ((ProposeRungEditError)result).Error,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-i) Returns ProposeRungEditError when l5xFragment is empty.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_EmptyL5xFragment_ReturnsError()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, "", tempFile);

            Assert.IsType<ProposeRungEditError>(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-j) Returns ProposeRungEditError when the project file does not exist.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_FileNotFound_ReturnsError()
    {
        var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
            ValidXPath, 0, 0, ValidFragment, "/nonexistent/path/Project.L5X");

        Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("not found", ((ProposeRungEditError)result).Error,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (off-k) Returns ProposeRungEditError when the routineXPath does not match
    ///         the expected XPath pattern.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_UnparsableXPath_ReturnsError()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                "not/a/valid/xpath/at/all", 0, 0, ValidFragment, tempFile);

            Assert.IsType<ProposeRungEditError>(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-l) In offline mode, the scan runs but no denial is returned — the gate
    ///         is advisory.  A fragment that writes a safety-tagged tag (Safety_OK)
    ///         now succeeds: the file IS modified on disk.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_WithPolicy_DeniedTag_ScanRunsNoDenial_FileIsModified()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var rawBefore = File.ReadAllText(tempFile);
            var policy    = LoadDemoPolicy();

            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentWritesSafetyOk, tempFile, policy);

            // Gate is advisory: result is a success.
            Assert.IsType<ProposeRungEditOfflineResult>(result);

            // File MUST have been modified (gate did not block the write).
            var rawAfter = File.ReadAllText(tempFile);
            Assert.NotEqual(rawBefore, rawAfter);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-m) The offline result serializes to JSON with the expected keys.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_SuccessResult_SerializesToJsonWithExpectedKeys()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var raw    = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);
            var json   = System.Text.Json.JsonSerializer.Serialize(raw);
            var doc    = System.Text.Json.JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("status",         out _));
            Assert.True(doc.RootElement.TryGetProperty("filePath",       out _));
            Assert.True(doc.RootElement.TryGetProperty("routineXPath",   out _));
            Assert.True(doc.RootElement.TryGetProperty("insertPosition", out _));
            Assert.True(doc.RootElement.TryGetProperty("replaceCount",   out _));
            Assert.True(doc.RootElement.TryGetProperty("message",        out _));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── MCP-layer offline mode (GetProposeRungEditResponseAsync with offline=true) ─

    /// <summary>
    /// (off-n) GetProposeRungEditResponseAsync with offline=true returns JSON with
    ///         status="saved-to-file" and does not require a session.
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_OfflineTrue_ReturnsSavedToFileJson()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var svc = new ProjectService();
            svc.Load(tempFile);
            var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

            var json = await tools.GetProposeRungEditResponseAsync(
                ValidXPath, 0, 0, ValidFragment, offline: true);

            var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal("saved-to-file",
                doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-o) GetProposeRungEditResponseAsync with offline=true returns JSON with
    ///         a filePath field pointing at the project file.
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_OfflineTrue_JsonContainsFilePath()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var svc = new ProjectService();
            svc.Load(tempFile);
            var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

            var json = await tools.GetProposeRungEditResponseAsync(
                ValidXPath, 0, 0, ValidFragment, offline: true);

            var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("filePath", out var fp));
            Assert.False(string.IsNullOrWhiteSpace(fp.GetString()));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (off-p) GetProposeRungEditResponseAsync with offline=false (default) when
    ///         the session is not online still returns an error — offline mode is
    ///         never activated by fallback.
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_OfflineFalse_NotOnlineSession_ReturnsError_NotSavedToFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

        // Build a session that is open but NOT online/correlated.
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        // IsOnline and IsCorrelated remain false — the handler's guard should fire.

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session: session, offline: false);

        var doc = System.Text.Json.JsonDocument.Parse(json);
        // Must return an error (not status=saved-to-file).
        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            "Expected an error response when session is not online and offline=false.");
        // Must NOT silently return a saved-to-file result.
        Assert.False(doc.RootElement.TryGetProperty("filePath", out _),
            "offline=false must not produce a filePath — offline mode must not activate by fallback.");

        session.Dispose();
    }

    /// <summary>
    /// (off-q) GetProposeRungEditResponseAsync with offline=true and no project loaded
    ///         returns a structured error with a specific, actionable message that
    ///         names <c>logix_open_project</c> as the required step.
    ///
    /// <para>
    /// This test covers the case where the caller requested offline mode but
    /// <see cref="ProjectService"/> has no file loaded (e.g. the operator skipped
    /// <c>logix_open_project</c>).  The error message must tell the operator
    /// exactly what to do next rather than returning a generic failure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SdkTools_ProposeRungEdit_OfflineTrue_NoProjectLoaded_ReturnsSpecificActionableError()
    {
        // No project loaded — ProjectService is empty.
        var svc   = new ProjectService();
        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, offline: true);

        var doc = System.Text.Json.JsonDocument.Parse(json);

        // Must return a structured error (not a success result).
        Assert.True(doc.RootElement.TryGetProperty("error", out var errProp),
            $"Expected 'error' property when offline=true and no project is loaded. Got: {json}");

        var errorText = errProp.GetString() ?? string.Empty;

        // Error must name the missing capability (project file) and the corrective action.
        Assert.Contains("logix_open_project", errorText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Source-scan: FinalizeEdits must not appear in src/ ───────────────────
    // AcceptEdits is intentionally used on the controller path (see target-d).

    /// <summary>
    /// (e) Source-scan assertion: no .cs file under src/ contains the string
    ///     "FinalizeEdits", <em>except</em> <c>FinalizeEditsHandler.cs</c> (the
    ///     handler implementing the gated <c>logix_finalize_edits</c> MCP tool,
    ///     an earlier change), <c>LogixSdkTools.cs</c> (which wires that
    ///     handler to the one and only <c>logix_finalize_edits</c> tool entry
    ///     point), and <c>AuditLogger.cs</c> (whose <c>LogFinalizeEdits</c>
    ///     method name and doc comments describe the <c>edits_finalized</c> audit
    ///     event; it only records that a finalize happened, it never triggers
    ///     one).  AcceptEdits is permitted everywhere (it is used on the
    ///     controller path); FinalizeEdits must never be used on any other code
    ///     path — in particular not from <c>logix_connect</c>/
    ///     <c>SdkConnectFactory.cs</c> or <c>logix_propose_rung_edit</c>/
    ///     <c>ProposeRungEditHandler.cs</c> — because it finalises pending edits
    ///     without operator review unless explicitly requested and confirmed via
    ///     the gated tool.
    /// </summary>
    [Fact]
    public void SourceScan_NoFinalizeEditsInSrc()
    {
        // Walk up from the test-output directory until we locate the src/ folder
        // at the solution root.
        var dir = AppContext.BaseDirectory;
        string? srcDir = null;

        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "src");
            if (Directory.Exists(candidate))
            {
                srcDir = candidate;
                break;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                break;
            dir = parent;
        }

        Assert.True(srcDir is not null && Directory.Exists(srcDir),
            "Could not locate the src/ directory from the test output path. " +
            "Ensure the solution layout has not changed.");

        // The gated logix_finalize_edits tool and its wiring are
        // the one deliberate, reviewed exception to this scan.
        // ProposeRungEditHandler.cs is the one additional
        // deliberate, reviewed exception — it dispatches FinalizeEdits only for
        // target="live", guarded so no other target can select it.
        var allowedFileNames = new[]
            { "FinalizeEditsHandler.cs", "LogixSdkTools.cs", "AuditLogger.cs",
              "ProposeRungEditHandler.cs" };

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir!, "*.cs", SearchOption.AllDirectories))
        {
            if (allowedFileNames.Contains(Path.GetFileName(file)))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("FinalizeEdits", StringComparison.Ordinal))
            {
                violations.Add(file);
            }
        }

        Assert.Empty(violations);
    }

    // ── ProposeRungEdit MCP entry point — live session forwarding ─────────────

    /// <summary>
    /// Helper: builds a <see cref="TagSessionService"/> that holds an active
    /// <see cref="TypedSdkTagService"/> backed by a
    /// <see cref="ProposeRungEditRecordingFake"/>, with the session marked as
    /// online and correlated so the handler's pre-flight guard passes.
    /// </summary>
    private static async Task<(TagSessionService TagSession, LogixProjectSession Session)>
        ActiveSdkSessionForProposeAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;

        var l5x      = L5X.Load(FixturePath);
        var typedSvc = new TypedSdkTagService(session, l5x);
        var tagSession = new TagSessionService();
        tagSession.Attach(typedSvc, isVirtual: false);

        return (tagSession, session);
    }

    /// <summary>
    /// (mcp-a) ProposeRungEdit with offline=false and no active session returns
    ///         error JSON with an "error" key — not a success result.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_NoActiveSession_OfflineFalse_ReturnsErrorJson()
    {
        var svc   = new ProjectService();
        svc.Load(FixturePath);
        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait()); // no session attached

        var json = await tools.ProposeRungEdit(ValidXPath, 0, 0, ValidFragment, offline: false);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            "Expected 'error' property when no active session is present.");
    }

    /// <summary>
    /// (mcp-b) The error returned when no session is active instructs the caller
    ///         to call logix_connect first — a distinct message that does not come
    ///         from the handler's own 'session is not online' guard.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_NoActiveSession_OfflineFalse_ErrorMentionsLogixConnect()
    {
        var svc   = new ProjectService();
        svc.Load(FixturePath);
        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

        var json = await tools.ProposeRungEdit(ValidXPath, 0, 0, ValidFragment, offline: false);

        var doc = JsonDocument.Parse(json);
        var errMsg = doc.RootElement.GetProperty("error").GetString() ?? "";
        Assert.Contains("logix_connect", errMsg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (mcp-c) The no-session error is distinct from the handler's 'session is
    ///         not online' guard: the message mentions "no active" (not just
    ///         "not online") so callers understand they need to call logix_connect
    ///         rather than simply ensuring the session is online.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_NoActiveSession_OfflineFalse_ErrorMentionsNoActiveSession()
    {
        var svc   = new ProjectService();
        svc.Load(FixturePath);
        var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

        var json = await tools.ProposeRungEdit(ValidXPath, 0, 0, ValidFragment, offline: false);

        var doc = JsonDocument.Parse(json);
        var errMsg = doc.RootElement.GetProperty("error").GetString() ?? "";
        // Must say "no active" to distinguish from the handler's "not online" guard.
        Assert.Contains("No active", errMsg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (mcp-d) ProposeRungEdit with offline=true does NOT return the
    ///         "no active session" error — the offline path does not require an
    ///         SDK session and must not be gated on one.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_NoActiveSession_OfflineTrue_DoesNotReturnSessionError()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var svc = new ProjectService();
            svc.Load(tempFile);
            var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait()); // no session

            var json = await tools.ProposeRungEdit(ValidXPath, 0, 0, ValidFragment, offline: true);

            var doc = JsonDocument.Parse(json);
            // Must not return the "no active session" error — offline mode is exempt.
            if (doc.RootElement.TryGetProperty("error", out var errProp))
            {
                Assert.DoesNotContain("logix_connect", errProp.GetString() ?? "",
                    StringComparison.OrdinalIgnoreCase);
            }
            // Offline success path produces "saved-to-file" status.
            Assert.True(
                doc.RootElement.TryGetProperty("status", out _) ||
                doc.RootElement.TryGetProperty("error", out _),
                "Expected either a status or error response from offline mode.");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// After an offline save, logix_get_routine (read from the in-memory model) shows the new rung
    /// without the project being re-opened.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_OfflineSave_GetRoutineShowsNewRung()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var svc = new ProjectService();
            svc.Load(tempFile);
            var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

            var before = Assert.IsType<GetRoutineResult>(
                GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine"));

            var json = await tools.ProposeRungEdit(
                ValidXPath, 0, 0, null, offline: true,
                rungs: new[] { new RungInput("XIC(OfflineRefreshA)OTE(OfflineRefreshB);") });
            Assert.Equal("saved-to-file", JsonDocument.Parse(json).RootElement.GetProperty("status").GetString());

            var after = Assert.IsType<GetRoutineResult>(
                GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine"));
            Assert.Equal(before.Rungs.Count + 1, after.Rungs.Count);
            Assert.Contains("OfflineRefreshA", after.Rungs[0].Text);
            Assert.Equal(0, after.Rungs[0].Number);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// After an offline save, logix_find_tags (read from the in-memory model) shows the change: the new
    /// rung's <c>\P2.Src</c> reference appears as a cross-program reference without re-opening the project.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_OfflineSave_FindTagsShowsChange()
    {
        var crossFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossProgram.L5X");
        var tempFile = Path.Combine(Path.GetTempPath(), $"OfflineTest_{Guid.NewGuid():N}.L5X");
        File.Copy(crossFixture, tempFile, overwrite: true);
        try
        {
            var svc = new ProjectService();
            svc.Load(tempFile);
            var tools = new LogixSdkTools(svc, new TagSessionService(), confirmWait: new StudioConfirmWait());

            var before = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(svc, "P2.Src"));
            var beforeRefs = before.CrossProgramReferences?.Count ?? 0;

            var json = await tools.ProposeRungEdit(
                "Controller/Programs/Program[@Name='P1']/Routines/Routine[@Name='Main']",
                0, 0, null, offline: true,
                rungs: new[] { new RungInput(@"MOVE(\P2.Src,Dst);") });
            Assert.Equal("saved-to-file", JsonDocument.Parse(json).RootElement.GetProperty("status").GetString());

            var after = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(svc, "P2.Src"));
            Assert.Equal(beforeRefs + 1, after.CrossProgramReferences?.Count ?? 0);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (mcp-e) ProposeRungEdit with an active session forwards the call to the
    ///         handler and returns a JSON result with "proposed" status.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_WithActiveSession_ForwardsToHandlerAndReturnsProposed()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var (tagSession, _) = await ActiveSdkSessionForProposeAsync();
        var tools = new LogixSdkTools(svc, tagSession, confirmWait: new StudioConfirmWait());

        var json = await tools.ProposeRungEdit(ValidXPath, 0, 0, ValidFragment, offline: false);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("status", out var statusProp),
            $"Expected 'status' property — got: {json}");
        Assert.Equal("proposed", statusProp.GetString());
    }

    /// <summary>
    /// (mcp-f) ProposeRungEdit with an active session does NOT return the
    ///         "no active session" error — the live session is forwarded correctly.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_WithActiveSession_DoesNotReturnNoSessionError()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var (tagSession, _) = await ActiveSdkSessionForProposeAsync();
        var tools = new LogixSdkTools(svc, tagSession, confirmWait: new StudioConfirmWait());

        var json = await tools.ProposeRungEdit(ValidXPath, 0, 0, ValidFragment, offline: false);

        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var errProp))
        {
            Assert.DoesNotContain("logix_connect", errProp.GetString() ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── hint field — build failure ────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="ProposeRungEditError.Hint"/> is non-empty when
    /// <c>BuildAsync</c> throws a structural compile error after a successful rung
    /// import.  The hint must guide the caller to fix the rung logic and reconnect
    /// before re-proposing — the handler rolls back (goes offline) before returning,
    /// so nothing is left pending on the controller.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BuildFails_ReturnsErrorWithNonEmptyHint()
    {
        // Arrange: an open, online, correlated session backed by the recording fake.
        // ThrowOnBuild causes BuildAsync to throw after a successful import, exercising
        // the build-failure rollback path in ProposeRungEditHandler.ExecuteAsync.
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnBuild = new InvalidOperationException(
            "undefined tag reference XIC_Motor — rung 0");

        // Act: the build failure fires; GoOfflineAsync may also throw (NotImplementedException
        // from the stub) — the rollback-failure note is appended to the error message,
        // but the Hint is always set by the build-failure handler regardless.
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        // Assert: ProposeRungEditError with a non-empty hint.
        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.NotNull(error.Hint);
        Assert.NotEmpty(error.Hint);
    }

    // ── Fragment envelope validation (pre-SDK) ────────────────────────────────

    // Malformed fragment: root element is not RSLogix5000Content.
    private const string FragmentWrongRoot =
        "<SomeOtherElement>" +
          "<RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
          "</RLLContent>" +
        "</SomeOtherElement>";

    // Malformed fragment: root is RSLogix5000Content but no RLLContent inside.
    private const string FragmentMissingRllContent =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // Malformed fragment: bare RLLContent as root (no RSLogix5000Content wrapper).
    private const string FragmentBareRllContent =
        "<RLLContent>" +
          "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent>";

    /// <summary>
    /// (env-a) When the fragment root element is not RSLogix5000Content,
    ///         ExecuteAsync returns ProposeRungEditError that names
    ///         RSLogix5000Content as the missing element — no SDK call is made.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FragmentWrongRoot_ReturnsErrorNamingRSLogix5000Content()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWrongRoot, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("RSLogix5000Content", err.Error, StringComparison.OrdinalIgnoreCase);
        // No SDK call — envelope error fires before any temp file or SDK interaction.
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// (env-b) When the fragment has the RSLogix5000Content root but no RLLContent,
    ///         ExecuteAsync returns ProposeRungEditError that names RLLContent —
    ///         no SDK call is made.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FragmentMissingRllContent_ReturnsErrorNamingRLLContent()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentMissingRllContent, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("RLLContent", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// (env-c) A bare &lt;RLLContent&gt; root (no RSLogix5000Content wrapper) is
    ///         rejected with an error naming RSLogix5000Content as the missing root.
    ///         This matches the SDK's "allows only to import RLLContent" scenario
    ///         where the caller sent the inner element as the root.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FragmentBareRllContentRoot_ReturnsErrorNamingRSLogix5000Content()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentBareRllContent, session);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        Assert.Contains("RSLogix5000Content", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// (env-d) The ValidFragment constant (MinimalNopFragment) passes the envelope
    ///         check and reaches the SDK — confirming the reference shape is accepted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ValidFragment_PassesEnvelopeCheck_SdkIsCalled()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session);

        // Valid fragment succeeds — result is a ProposeRungEditResult (not an error).
        Assert.IsType<ProposeRungEditResult>(result);
        // SDK was called exactly once.
        Assert.Single(fake.CapturedXPaths);
    }

    /// <summary>
    /// (env-e) Envelope error fires before the gate check: even when a policy is
    ///         supplied, a fragment with a missing RSLogix5000Content root still
    ///         returns an envelope error (not a gate error), and no SDK call is made.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EnvelopeError_FiresBeforeGateCheck_NoSdkCall()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();  // policy would normally gate Safety_OK

        // Fragment has the wrong root — this should hit the envelope check,
        // not the gate check (the gate check only runs when the envelope is valid).
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWrongRoot, session, policy);

        Assert.IsType<ProposeRungEditError>(result);
        var err = (ProposeRungEditError)result;
        // Error must name RSLogix5000Content, not a tag name from the gate policy.
        Assert.Contains("RSLogix5000Content", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.CapturedXPaths);
    }

    /// <summary>
    /// (env-f) The error message includes the expected nesting path so the caller
    ///         knows the full correct envelope shape, not just which element is missing.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EnvelopeError_MessageIncludesExpectedNestingPath()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentMissingRllContent, session);

        var err = Assert.IsType<ProposeRungEditError>(result);
        // The error must reference RLLContent (the missing element) and show
        // RSLogix5000Content in the expected nesting path.
        Assert.Contains("RLLContent",          err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RSLogix5000Content",  err.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── target parameter tests ─────────────────────────────────────────────────

    /// <summary>
    /// (target-a) When target is null (default), the workstation path is taken:
    ///            PartialImportRungsFromXmlFileAsync is called (not the WithTarget overload).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetNull_RoutesToPartialImportRungsFromXmlFileAsync()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: null);

        // Workstation path: PartialImportRungsFromXmlFileAsync called once.
        Assert.Single(fake.CapturedXPaths);
        // Controller path: PartialImportWithTargetFromXmlFileAsync NOT called.
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// (target-b) When target="workstation" (explicit), the workstation path is taken:
    ///            PartialImportRungsFromXmlFileAsync is called (same as null).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetWorkstation_RoutesToPartialImportRungsFromXmlFileAsync()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "workstation");

        Assert.Single(fake.CapturedXPaths);
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// (target-c) When target="controller", the controller path is taken:
    ///            PartialImportRungsFromXmlFileAsync is called with AcceptEdits.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetController_RoutesToPartialImportRungsFromXmlFileAsync()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        // Both destinations use PartialImportRungsFromXmlFileAsync — it is the rung
        // API. Only the PartialImportOption differs. The WithTarget overload is for
        // importing *named objects*, takes no insertPosition/replaceCount, and its
        // second parameter is the SDK's targetName; passing our "controller" literal
        // into it crashed Logix 5000 Services on the bench 2026-08-04.
        Assert.Single(fake.CapturedXPaths);
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// (target-d) target="controller" passes AcceptEdits to PartialImportRungsFromXmlFileAsync
    ///            so the edit is immediately accepted and visible on the physical controller.
    ///            FinalizeEdits is never used on this target (only target="live").
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetController_PassesAcceptEditsToSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        Assert.Single(fake.CapturedImportOptions);
        Assert.Equal(PartialImportOption.AcceptEdits, fake.CapturedImportOptions[0]);
        // FinalizeEdits must never appear on target="controller" — it is permitted only
        // for target="live"; the controller path stays AcceptEdits-only.
        Assert.DoesNotContain(PartialImportOption.FinalizeEdits, fake.CapturedImportOptions);
    }

    /// <summary>
    /// (target-e) target="controller" passes the correct XPath to PartialImportRungsFromXmlFileAsync.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetController_PassesCorrectXPathToSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        Assert.Single(fake.CapturedXPaths);
        Assert.Equal(ValidXPath, fake.CapturedXPaths[0]);
    }

    /// <summary>
    /// (target-f) An invalid target value returns ProposeRungEditError before any SDK call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InvalidTarget_ReturnsErrorBeforeSdkCall()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "plc");

        Assert.IsType<ProposeRungEditError>(result);
        // No SDK call made on an invalid target.
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// (target-g) The error message for an invalid target names the invalid value
    ///            and the accepted values ('workstation', 'controller').
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InvalidTarget_ErrorNamesInvalidValueAndAcceptedValues()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "remote");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("remote",        err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workstation",   err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("controller",    err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (target-h) With target="controller", the success result message states the edit
    ///            is visible to any Logix Designer session online with the controller.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetController_SuccessMessageMentionsControllerVisibility()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        // Message must state the controller-visibility semantics.
        Assert.Contains("controller", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visible",    result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (target-i) With target=null (default), the success result message states the edit
    ///            cannot be seen by any other session (workstation semantics).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetNull_SuccessMessageMentionsWorkstationVisibility()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: null);

        // Message must state the workstation-only semantics: both rungs and tag
        // declarations are held locally (an earlier change — staging is invisible for both).
        Assert.Contains("tag declarations",
            result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("staging buffer",
            result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (target-j) target parameter is case-insensitive: "CONTROLLER" routes to the
    ///            controller SDK path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TargetControllerCaseInsensitive_RoutesToRungsApi()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "CONTROLLER");

        Assert.Single(fake.CapturedXPaths);
        Assert.Equal(PartialImportOption.AcceptEdits, fake.CapturedImportOptions[0]);
        Assert.Empty(fake.CapturedWithTargetXPaths);
    }

    /// <summary>
    /// (target-k) The logix_propose_rung_edit MCP tool exposes a 'target' parameter
    ///            with a non-empty description that mentions both 'workstation' and 'controller'
    ///            and clearly documents visibility semantics.
    /// </summary>
    [Fact]
    public void LogixProposeRungEdit_TargetParameter_IsExposedWithDescriptionMentioningBothValues()
    {
        var method = typeof(LogixSdkTools)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.GetCustomAttributes(inherit: true)
                 .Any(a => a.GetType().Name == "McpServerToolAttribute") &&
                m.GetCustomAttributes(inherit: true)
                 .Any(a =>
                 {
                     var name = a.GetType().GetProperty("Name")?.GetValue(a) as string;
                     return string.Equals(name, "logix_propose_rung_edit",
                         StringComparison.OrdinalIgnoreCase);
                 }));

        Assert.NotNull(method);

        var targetParam = method!.GetParameters()
            .FirstOrDefault(p => string.Equals(
                p.Name, "target", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(targetParam);

        var desc = targetParam!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), true)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description ?? "";

        // Description must mention both accepted values.
        Assert.Contains("workstation", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("controller",  desc, StringComparison.OrdinalIgnoreCase);

        // Description must state the workstation visibility semantics (cannot be seen by any other session).
        Assert.True(
            desc.Contains("cannot be seen by any other session", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("in-memory", StringComparison.OrdinalIgnoreCase),
            $"target parameter description must state workstation visibility semantics. Got: \"{desc}\"");

        // Description must state the controller visibility semantics (visible to any session).
        Assert.True(
            desc.Contains("visible to any", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("any Logix Designer session", StringComparison.OrdinalIgnoreCase),
            $"target parameter description must state controller visibility semantics. Got: \"{desc}\"");
    }

    /// <summary>
    /// (target-l) GetProposeRungEditResponseAsync passes target="controller" to the handler:
    ///            the SDK PartialImportWithTargetFromXmlFileAsync is called.
    /// </summary>
    [Fact]
    public async Task SdkTools_GetProposeRungEditResponseAsync_TargetController_RoutesToControllerSdk()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;

        var tools = new LogixSdkTools(svc, new TagSessionService(), new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ValidFragment, session: session, target: "controller");

        // The rung API was called with AcceptEdits; the named-object overload never is.
        Assert.Single(fake.CapturedXPaths);
        Assert.Equal(PartialImportOption.AcceptEdits, fake.CapturedImportOptions[0]);
        Assert.Empty(fake.CapturedWithTargetXPaths);

        // Response is a success with "proposed" status.
        var doc = JsonDocument.Parse(json);
        Assert.Equal("proposed", doc.RootElement.GetProperty("status").GetString());
    }

    // ── Audit record destination — end-to-end (acceptance criterion 6) ─────────
    //
    // These tests verify that ProposeRungEditHandler calls AuditLogger.LogRungEdit
    // with the correct destination field for each dispatch path.  The AuditLoggerTests
    // class covers the logger's own serialisation; these tests cover the wiring between
    // the handler and the logger so the audit trail accurately reflects which path was taken.

    /// <summary>
    /// (audit-a) The workstation path (target=null) records a rung_edit audit entry
    /// with destination="workstation" on success, so the audit trail distinguishes
    /// a workstation-only edit from a controller-bound edit.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_Success_AuditRecordHasDestinationWorkstation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = await BuildOpenSessionAsync();

            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, ValidFragment, session, policy: null, target: null);

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "Audit file must exist after a successful call.");

            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllLines(auditPath)
                    .Select(l => System.Text.Json.JsonDocument.Parse(l))
                    .Select(d => d.RootElement)
                    .First(e => e.TryGetProperty("event", out var ev) &&
                                ev.GetString() == "rung_edit")
                    .GetRawText());

            Assert.True(doc.RootElement.TryGetProperty("destination", out var dest),
                "rung_edit entry must carry a 'destination' field.");
            Assert.Equal("workstation", dest.GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// (audit-b) The controller path (target="controller") records a rung_edit audit
    /// entry with destination="controller" on success, distinguishable from the
    /// workstation entry.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_Success_AuditRecordHasDestinationController()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-ctrl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = await BuildOpenSessionAsync();

            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "Audit file must exist after a successful call.");

            using var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllLines(auditPath)
                    .Select(l => System.Text.Json.JsonDocument.Parse(l))
                    .Select(d => d.RootElement)
                    .First(e => e.TryGetProperty("event", out var ev) &&
                                ev.GetString() == "rung_edit")
                    .GetRawText());

            Assert.True(doc.RootElement.TryGetProperty("destination", out var dest),
                "rung_edit entry must carry a 'destination' field.");
            Assert.Equal("controller", dest.GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// (audit-c) When a policy is supplied and the fragment writes a previously-denied
    /// tag, the gate is advisory and the operation succeeds — the workstation path
    /// records a rung_edit audit entry with outcome="proposed" and destination="workstation".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_WithPolicy_DeniedTag_AuditRecordsProposedWorkstation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-ws-deny-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = await BuildOpenSessionAsync();
            var policy = LoadDemoPolicy();

            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy, target: null);

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "Audit file must exist after a successful call.");

            var rungEditJson = File.ReadAllLines(auditPath)
                .Select(l => System.Text.Json.JsonDocument.Parse(l))
                .Select(d => d.RootElement)
                .First(e => e.TryGetProperty("event", out var ev) &&
                            ev.GetString() == "rung_edit" &&
                            e.GetProperty("outcome").GetString() != "sending")
                .GetRawText();

            using var doc = System.Text.Json.JsonDocument.Parse(rungEditJson);
            // Gate is advisory: outcome is "proposed", not "denied".
            Assert.Equal("proposed", doc.RootElement.GetProperty("outcome").GetString());
            Assert.True(doc.RootElement.TryGetProperty("destination", out var dest),
                "rung_edit entry must carry a 'destination' field.");
            Assert.Equal("workstation", dest.GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// (audit-d) When a policy is supplied and the fragment writes a previously-denied
    /// tag, the gate is advisory and the operation succeeds — the controller path
    /// records a rung_edit audit entry with outcome="proposed" and destination="controller".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_WithPolicy_DeniedTag_AuditRecordsProposedController()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-ctrl-deny-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = await BuildOpenSessionAsync();
            var policy = LoadDemoPolicy();

            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy, target: "controller");

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "Audit file must exist after a successful call.");

            var rungEditJson = File.ReadAllLines(auditPath)
                .Select(l => System.Text.Json.JsonDocument.Parse(l))
                .Select(d => d.RootElement)
                .First(e => e.TryGetProperty("event", out var ev) &&
                            ev.GetString() == "rung_edit" &&
                            e.GetProperty("outcome").GetString() != "sending")
                .GetRawText();

            using var doc = System.Text.Json.JsonDocument.Parse(rungEditJson);
            // Gate is advisory: outcome is "proposed", not "denied".
            Assert.Equal("proposed", doc.RootElement.GetProperty("outcome").GetString());
            Assert.True(doc.RootElement.TryGetProperty("destination", out var dest),
                "rung_edit entry must carry a 'destination' field.");
            Assert.Equal("controller", dest.GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Response text — controller path, PRD requirement 3 (acceptance criterion 7) ──
    //
    // This change specifies the exact semantics the message must convey:
    // Controller path: "the rungs were sent to the controller as accepted-but-not-assembled
    //   edits and are visible to any Logix Designer session online with it."
    // Workstation path: "the rungs are in this server's in-memory image only and no other
    //   session can see them."

    /// <summary>
    /// (resp-a) The controller path success message includes the phrase
    ///          "EDIT SENT TO CONTROLLER", making it immediately clear the edit
    ///          reached the controller rather than staying in server memory.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_SuccessMessage_ContainsEditSentToController()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        Assert.Contains("EDIT SENT TO CONTROLLER", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (resp-b) The controller path success message states "ACCEPTED BUT NOT YET
    ///          ASSEMBLED", matching the AcceptEdits SDK semantic: the edit is
    ///          accepted on the controller but not yet assembled (not running).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_SuccessMessage_StatesAcceptedButNotYetAssembled()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        // PRD req 3: the rungs were sent as accepted-but-not-assembled edits.
        Assert.Contains("ACCEPTED", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT YET ASSEMBLED", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (resp-c) The controller path success message uses the phrase
    ///          "accepted-but-not-assembled", matching PRD requirement 3 verbatim.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_SuccessMessage_UsesAcceptedButNotAssembledPhrase()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        // PRD requirement 3 wording: "accepted-but-not-assembled edits"
        Assert.Contains("accepted-but-not-assembled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (resp-d) The controller path success message does NOT use the workstation-path
    ///          phrase "PENDING EDIT" — each path has distinct terminology matching
    ///          its distinct semantics (AcceptEdits vs LeaveEdits).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_SuccessMessage_DoesNotUsePendingEditPhrase()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: "controller");

        // "PENDING EDIT" is the workstation phrasing; the controller path must not use it.
        Assert.DoesNotContain("PENDING EDIT", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (resp-e) The workstation path success message states the rungs are in the
    ///          "in-memory project image only", matching PRD requirement 3 for the
    ///          local path and making clear no controller was contacted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_SuccessMessage_StatesInMemoryImageOnly()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, policy: null, target: null);

        // PRD requirement 3: "the rungs are in this server's in-memory image only"
        Assert.Contains("in-memory", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate scan — identical on both paths (advisory only) ──────────────────
    //
    // The scan is positioned before the SDK dispatch branch and runs identically
    // regardless of target.  Both paths now produce a success result; neither is
    // denied.  Tests (ab) and (ac) verify that the SDK is called on both paths.

    /// <summary>
    /// (gate-ident) With the gate now advisory, both dispatch targets (workstation
    ///              and controller) succeed when a previously-denied tag is written.
    ///              The result status is "proposed" on both paths.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_DeniedTag_BothPathsSucceed()
    {
        var (sessionA, _) = await BuildOpenSessionAsync();
        var (sessionB, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var wsResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, sessionA, policy, target: null);

        var ctrlResult = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, sessionB, policy, target: "controller");

        // Gate is advisory: both paths succeed.
        var wsSuccess   = Assert.IsType<ProposeRungEditResult>(wsResult);
        var ctrlSuccess = Assert.IsType<ProposeRungEditResult>(ctrlResult);
        Assert.Equal("proposed", wsSuccess.Status);
        Assert.Equal("proposed", ctrlSuccess.Status);
    }

    // ── hint + docs_anchor fields — envelope validation errors ───────────────

    /// <summary>
    /// (env-hint-a) When the fragment root element is not RSLogix5000Content,
    ///              the returned <see cref="ProposeRungEditError"/> must carry a
    ///              non-empty <c>Hint</c> that directs the caller toward the
    ///              export→edit→import round-trip, and a non-empty <c>DocsAnchor</c>
    ///              referencing the docs section for rung-edit errors.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FragmentWrongRoot_ErrorCarriesNonEmptyHintAndDocsAnchor()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWrongRoot, session);

        var err = Assert.IsType<ProposeRungEditError>(result);

        // Hint must be present and direct the caller toward the export→edit→import workflow.
        Assert.NotNull(err.Hint);
        Assert.NotEmpty(err.Hint);
        Assert.Contains("export", err.Hint, StringComparison.OrdinalIgnoreCase);

        // DocsAnchor must be present and non-empty.
        Assert.NotNull(err.DocsAnchor);
        Assert.NotEmpty(err.DocsAnchor);
    }

    /// <summary>
    /// (env-hint-b) When the fragment has the RSLogix5000Content root but no
    ///              RLLContent, the returned <see cref="ProposeRungEditError"/>
    ///              must carry a non-empty <c>Hint</c> that directs the caller
    ///              toward the export→edit→import round-trip, and a non-empty
    ///              <c>DocsAnchor</c> referencing the docs section for rung-edit
    ///              errors.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FragmentMissingRllContent_ErrorCarriesNonEmptyHintAndDocsAnchor()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentMissingRllContent, session);

        var err = Assert.IsType<ProposeRungEditError>(result);

        // Hint must be present and direct the caller toward the export→edit→import workflow.
        Assert.NotNull(err.Hint);
        Assert.NotEmpty(err.Hint);
        Assert.Contains("export", err.Hint, StringComparison.OrdinalIgnoreCase);

        // DocsAnchor must be present and non-empty.
        Assert.NotNull(err.DocsAnchor);
        Assert.NotEmpty(err.DocsAnchor);
    }

    // ── Offline path: fragment envelope validation ────────────────────────────

    /// <summary>
    /// (env-off-a) ExecuteOfflineAsync rejects a fragment with the wrong root element,
    ///             returning ProposeRungEditError that names RSLogix5000Content.
    ///             The project file must not be modified.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_FragmentWrongRoot_ReturnsErrorNamingRSLogix5000Content_FileUnmodified()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var rawBefore = File.ReadAllText(tempFile);

            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentWrongRoot, tempFile);

            Assert.IsType<ProposeRungEditError>(result);
            var err = (ProposeRungEditError)result;
            Assert.Contains("RSLogix5000Content", err.Error, StringComparison.OrdinalIgnoreCase);

            // File must not have been modified.
            Assert.Equal(rawBefore, File.ReadAllText(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (env-off-b) ExecuteOfflineAsync rejects a fragment missing RLLContent,
    ///             returning ProposeRungEditError that names RLLContent.
    ///             The project file must not be modified.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_FragmentMissingRllContent_ReturnsErrorNamingRLLContent_FileUnmodified()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var rawBefore = File.ReadAllText(tempFile);

            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentMissingRllContent, tempFile);

            Assert.IsType<ProposeRungEditError>(result);
            var err = (ProposeRungEditError)result;
            Assert.Contains("RLLContent", err.Error, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(rawBefore, File.ReadAllText(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (env-off-c) The ValidFragment (MinimalNopFragment) passes offline envelope
    ///             validation and the file is modified — confirming the reference
    ///             shape is accepted in the offline path too.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_ValidFragment_PassesEnvelopeCheck_FileModified()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var result = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, ValidFragment, tempFile);

            Assert.IsType<ProposeRungEditOfflineResult>(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── MinimalNopFragment description example (rot-proof guard) ─────────────

    /// <summary>
    /// Verifies that <see cref="ProposeRungEditHandler.MinimalNopFragment"/>
    /// — the authoritative example embedded verbatim in the <c>l5xFragment</c>
    /// parameter description on <c>logix_propose_rung_edit</c> — is valid XML
    /// that contains every required nesting level.
    ///
    /// <para>
    /// This test is the rot-proof guard for the tool description: because the
    /// description concatenates <c>ProposeRungEditHandler.MinimalNopFragment</c>
    /// directly, any change to that constant that breaks the required envelope
    /// shape will fail here immediately, before the change can silently
    /// corrupt the copy-pasteable example seen by MCP clients.
    /// </para>
    ///
    /// Required nesting:
    /// RSLogix5000Content &gt; Controller &gt; Programs &gt; Program &gt;
    /// Routines &gt; Routine &gt; RLLContent &gt; Rung &gt; Text &gt; CDATA.
    /// </summary>
    [Fact]
    public void MinimalNopFragment_IsValidXml_WithRequiredNesting()
    {
        // Must parse without throwing.
        var doc = System.Xml.Linq.XDocument.Parse(ProposeRungEditHandler.MinimalNopFragment);

        // RSLogix5000Content must be the root element.
        Assert.Equal("RSLogix5000Content", doc.Root!.Name.LocalName);

        // Required nesting: Controller
        var controller = doc.Descendants("Controller").FirstOrDefault();
        Assert.NotNull(controller);

        // Required nesting: Programs > Program
        var program = doc.Descendants("Program").FirstOrDefault();
        Assert.NotNull(program);

        // Required nesting: Routines > Routine
        var routine = doc.Descendants("Routine").FirstOrDefault();
        Assert.NotNull(routine);

        // Required nesting: RLLContent
        var rllContent = doc.Descendants("RLLContent").FirstOrDefault();
        Assert.NotNull(rllContent);

        // Required nesting: Rung
        var rung = rllContent!.Elements("Rung").FirstOrDefault();
        Assert.NotNull(rung);

        // Required nesting: Text with CDATA content
        var text = rung!.Element("Text");
        Assert.NotNull(text);
        Assert.False(
            string.IsNullOrWhiteSpace(text!.Value),
            "MinimalNopFragment Rung/Text must contain non-empty CDATA content.");
    }

    /// <summary>
    /// Verifies that the <c>l5xFragment</c> parameter description on the
    /// <c>logix_propose_rung_edit</c> MCP tool names every required nesting
    /// level, carries the typed rungs/tags example, and does not embed the bare
    /// <see cref="ProposeRungEditHandler.MinimalNopFragment"/> envelope.
    ///
    /// <para>
    /// Guards against description drift: any edit that removes the nesting
    /// specification or the embedded example will fail this test immediately.
    /// </para>
    /// </summary>
    [Fact]
    public void LogixProposeRungEdit_L5xFragmentDescription_NamesRequiredNestingAndTypedExample()
    {
        // Locate the l5xFragment parameter on LogixSdkTools.ProposeRungEdit via
        // reflection so this test is not fragile to parameter position changes.
        var method = typeof(LogixSdkTools)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.GetCustomAttributes(inherit: true)
                 .Any(a => a.GetType().Name == "McpServerToolAttribute") &&
                m.GetCustomAttributes(inherit: true)
                 .Any(a =>
                 {
                     var name = a.GetType().GetProperty("Name")?.GetValue(a) as string;
                     return string.Equals(name, "logix_propose_rung_edit",
                         StringComparison.OrdinalIgnoreCase);
                 }));

        Assert.NotNull(method);

        var fragmentParam = method!.GetParameters()
            .FirstOrDefault(p => string.Equals(
                p.Name, "l5xFragment", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(fragmentParam);

        var desc = fragmentParam!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), true)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description ?? "";

        // Must name every required nesting level.
        Assert.Contains("RSLogix5000Content", desc, StringComparison.Ordinal);
        Assert.Contains("RLLContent",         desc, StringComparison.Ordinal);
        Assert.Contains("Rung",               desc, StringComparison.Ordinal);
        Assert.Contains("CDATA",              desc, StringComparison.Ordinal);

        // Must carry the typed example and the advanced-path wording, and must NOT embed
        // the bare MinimalNopFragment envelope.
        Assert.Contains("rungs=[{\"text\":\"XIC(Start)OTE(Lamp);\"", desc, StringComparison.Ordinal);
        Assert.Contains("tags=[{\"name\":\"Lamp\",\"dataType\":\"BOOL\"}]", desc, StringComparison.Ordinal);
        Assert.Contains("ADVANCED", desc, StringComparison.Ordinal);
        Assert.Contains("logix_export_rungs", desc, StringComparison.Ordinal);
        Assert.Contains("rebuilt by the server", desc, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ProposeRungEditHandler.MinimalNopFragment,
            desc,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<RSLogix5000Content", desc, StringComparison.Ordinal);
    }

    // ── ProposalSafetyFlags tests ─────────────────────────────────────────────

    // Fragment writes a brand-new tag that does not exist in the project at all.
    private const string FragmentWritesUnknownTag =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[OTE(Brand_New_Tag_XYZ);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>
    /// (sf-a) When the fragment writes a safety-tagged tag (Safety_OK), the result
    ///        carries safetyFlags.writesToSafetyTags listing that tag.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_SafetyTagWrite_SafetyFlagsWritesToSafetyTagsSet()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);

        Assert.NotNull(result.SafetyFlags);
        Assert.NotNull(result.SafetyFlags!.WritesToSafetyTags);
        Assert.Contains("Safety_OK", result.SafetyFlags.WritesToSafetyTags!,
            StringComparer.OrdinalIgnoreCase);

        // The other two flags must not fire for a pure safety-tag write.
        Assert.Null(result.SafetyFlags.InvalidatesGrantBasis);
        Assert.Null(result.SafetyFlags.WritesUnknownTags);
    }

    /// <summary>
    /// (sf-b) When the fragment writes a currently-granted tag (Cmd_MotorStart),
    ///        the result carries safetyFlags.invalidatesGrantBasis listing that tag
    ///        (advisory: accepting the edit makes the tag program-written).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_GrantedTagWrite_SafetyFlagsInvalidatesGrantBasisSet()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        // FragmentWritesCmdGranted writes Cmd_MotorStart, which is granted by the demo policy.
        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesCmdGranted, session, policy);

        Assert.NotNull(result.SafetyFlags);
        Assert.NotNull(result.SafetyFlags!.InvalidatesGrantBasis);
        Assert.Contains("Cmd_MotorStart", result.SafetyFlags.InvalidatesGrantBasis!,
            StringComparer.OrdinalIgnoreCase);

        // The other two flags must not fire for a pure granted-tag write.
        Assert.Null(result.SafetyFlags.WritesToSafetyTags);
        Assert.Null(result.SafetyFlags.WritesUnknownTags);
    }

    /// <summary>
    /// (sf-c) When the fragment writes a tag that does not exist in the project
    ///        (absent from the policy entirely), the result carries
    ///        safetyFlags.writesUnknownTags listing that tag.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_UnknownTagWrite_SafetyFlagsWritesUnknownTagsSet()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesUnknownTag, session, policy);

        Assert.NotNull(result.SafetyFlags);
        Assert.NotNull(result.SafetyFlags!.WritesUnknownTags);
        Assert.Contains("Brand_New_Tag_XYZ", result.SafetyFlags.WritesUnknownTags!,
            StringComparer.OrdinalIgnoreCase);

        // The other two flags must not fire for an unknown-tag write.
        Assert.Null(result.SafetyFlags.WritesToSafetyTags);
        Assert.Null(result.SafetyFlags.InvalidatesGrantBasis);
    }

    /// <summary>
    /// (sf-d) When no policy is provided, safetyFlags is null — the flags require
    ///        a policy to classify the write destinations.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullPolicy_SafetyFlagsIsNull()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy: null);

        Assert.Null(result.SafetyFlags);
    }

    /// <summary>
    /// (sf-e) When a policy is provided but the fragment contains no write operands,
    ///        safetyFlags is null.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_NoWriteOperands_SafetyFlagsIsNull()
    {
        const string ReadOnlyFragment =
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Cmd_MotorStart)NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ReadOnlyFragment, session, policy);

        Assert.Null(result.SafetyFlags);
    }

    /// <summary>
    /// (sf-f) When a policy is provided and the fragment writes a ProgramWritten tag
    ///        (Motor_Run), safetyFlags is null — denied-for-other-reasons is not a
    ///        safety flag.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_ProgramWrittenTagWrite_SafetyFlagsIsNull()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        // Motor_Run is denied as ProgramWritten — not a safety flag.
        var result = (ProposeRungEditResult)await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesMotorRun, session, policy);

        Assert.Null(result.SafetyFlags);
    }

    /// <summary>
    /// (sf-g) safetyFlags serializes to JSON as a distinct top-level field with the
    ///        three sub-fields; null sub-fields are omitted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPolicy_SafetyTagWrite_SafetyFlagsSerializesToJson()
    {
        var (session, _) = await BuildOpenSessionAsync();
        var policy = LoadDemoPolicy();

        var raw  = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWritesSafetyOk, session, policy);
        var json = JsonSerializer.Serialize(raw);
        var doc  = JsonDocument.Parse(json);

        // Must appear as a distinct top-level field — not buried in provenance.
        Assert.True(doc.RootElement.TryGetProperty("safetyFlags", out var flags),
            $"Expected safetyFlags in JSON. Got: {json}");
        Assert.Equal(JsonValueKind.Object, flags.ValueKind);

        // writesToSafetyTags must be present (Safety_OK).
        Assert.True(flags.TryGetProperty("writesToSafetyTags", out var wst),
            "Expected writesToSafetyTags sub-field.");
        Assert.Equal(JsonValueKind.Array, wst.ValueKind);

        // invalidatesGrantBasis and writesUnknownTags must NOT be present (null → omitted).
        Assert.False(flags.TryGetProperty("invalidatesGrantBasis", out _),
            "invalidatesGrantBasis must be absent (null) when no granted tags are written.");
        Assert.False(flags.TryGetProperty("writesUnknownTags", out _),
            "writesUnknownTags must be absent (null) when no unknown tags are written.");
    }

    /// <summary>
    /// (sf-h) Offline path: when a policy is provided and the fragment writes a
    ///        safety-tagged tag, ExecuteOfflineAsync carries safetyFlags.writesToSafetyTags.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_WithPolicy_SafetyTagWrite_SafetyFlagsSet()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var policy = LoadDemoPolicy();
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentWritesSafetyOk, tempFile, policy);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.NotNull(result.SafetyFlags);
            Assert.NotNull(result.SafetyFlags!.WritesToSafetyTags);
            Assert.Contains("Safety_OK", result.SafetyFlags.WritesToSafetyTags!,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// (sf-i) Offline path: when a policy is provided and the fragment writes the
    ///        only granted tag (Cmd_MotorStart), safetyFlags.invalidatesGrantBasis is set.
    /// </summary>
    [Fact]
    public async Task ExecuteOfflineAsync_WithPolicy_GrantedTagWrite_SafetyFlagsInvalidatesGrantBasisSet()
    {
        var tempFile = CreateTempL5XCopy();
        try
        {
            var policy = LoadDemoPolicy();
            var raw = await ProposeRungEditHandler.ExecuteOfflineAsync(
                ValidXPath, 0, 0, FragmentWritesCmdGranted, tempFile, policy);

            var result = Assert.IsType<ProposeRungEditOfflineResult>(raw);
            Assert.NotNull(result.SafetyFlags);
            Assert.NotNull(result.SafetyFlags!.InvalidatesGrantBasis);
            Assert.Contains("Cmd_MotorStart", result.SafetyFlags.InvalidatesGrantBasis!,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── XML declaration must survive serialisation (Bench 2, 2026-09-06) ─────
    // XDocument.ToString() drops <?xml ...?>; the Logix SDK importer then
    // rejects the fragment with XMLSrv_E_ERROR_READING_XML.  Measured with
    // scripts/exp-xmlprobe.py against the old binary: export unchanged imports,
    // export minus the declaration is rejected, export minus every <Tags>
    // container imports.  These tests pin the fix in SerializeWithDeclaration.

    [Fact]
    public void StripTagsFromFragment_PreservesXmlDeclaration()
    {
        const string fragment =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Tags><Tag Name=\"T1\" DataType=\"BOOL\" /></Tags>" +
            "<Programs><Program Name=\"P\"><Routines><Routine Name=\"R\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

        var extracted = ProposeRungEditHandler.StripTagsFromFragment(fragment, out var stripped);

        Assert.Single(extracted);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>", stripped);
        Assert.DoesNotContain("<Tags", stripped);
        Assert.Contains("<![CDATA[NOP();]]>", stripped);
    }

    [Fact]
    public void StripTagsFromFragment_AddsDeclarationWhenInputHasNone()
    {
        var extracted = ProposeRungEditHandler.StripTagsFromFragment(FragmentWithControllerTags, out var stripped);

        Assert.Single(extracted);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>", stripped);
    }

    [Fact]
    public void SerializeWithDeclaration_RoundTripsExistingDeclarationVerbatim()
    {
        var doc = System.Xml.Linq.XDocument.Parse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><A><B/></A>");

        var text = ProposeRungEditHandler.SerializeWithDeclaration(doc);

        Assert.Equal("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<A><B /></A>", text);
    }

    // ── StripTagsFromFragment unit tests ─────────────────────────────────────

    // Fragment with a controller-scoped <Tags> container and one <Tag> child.
    private const string FragmentWithControllerTags =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags>" +
              "<Tag Name=\"CtrlTag1\" DataType=\"BOOL\" />" +
            "</Tags>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // Fragment with a program-scoped <Tags> container and one <Tag> child.
    private const string FragmentWithProgramTags =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Tags>" +
                  "<Tag Name=\"ProgTag1\" DataType=\"DINT\" />" +
                "</Tags>" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // Fragment with both controller-scoped and program-scoped <Tags>.
    private const string FragmentWithBothTagScopes =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags>" +
              "<Tag Name=\"CtrlTag1\" DataType=\"BOOL\" />" +
              "<Tag Name=\"CtrlTag2\" DataType=\"DINT\" />" +
            "</Tags>" +
            "<Programs>" +
              "<Program Name=\"MainProgram\">" +
                "<Tags>" +
                  "<Tag Name=\"ProgTag1\" DataType=\"REAL\" />" +
                "</Tags>" +
                "<Routines>" +
                  "<Routine Name=\"MainRoutine\">" +
                    "<RLLContent>" +
                      "<Rung Number=\"0\" Type=\"N\">" +
                        "<Text><![CDATA[NOP();]]></Text>" +
                      "</Rung>" +
                    "</RLLContent>" +
                  "</Routine>" +
                "</Routines>" +
              "</Program>" +
            "</Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// (strip-a) StripTagsFromFragment with a controller-scoped &lt;Tags&gt; element
    ///           returns the Tag children and removes the container from the XML.
    /// </summary>
    [Fact]
    public void StripTagsFromFragment_ControllerScopedTags_ExtractsTagAndRemovesContainer()
    {
        var tags = ProposeRungEditHandler.StripTagsFromFragment(
            FragmentWithControllerTags, out var stripped);

        // One <Tag> extracted.
        Assert.Single(tags);
        Assert.Equal("CtrlTag1", (string?)tags[0].Attribute("Name"));

        // Stripped XML must not contain <Tags>.
        var strippedDoc = System.Xml.Linq.XDocument.Parse(stripped);
        Assert.Empty(strippedDoc.Descendants("Tags"));
    }

    /// <summary>
    /// (strip-b) StripTagsFromFragment with a program-scoped &lt;Tags&gt; element
    ///           returns the Tag children and removes the container from the XML.
    /// </summary>
    [Fact]
    public void StripTagsFromFragment_ProgramScopedTags_ExtractsTagAndRemovesContainer()
    {
        var tags = ProposeRungEditHandler.StripTagsFromFragment(
            FragmentWithProgramTags, out var stripped);

        // One <Tag> extracted.
        Assert.Single(tags);
        Assert.Equal("ProgTag1", (string?)tags[0].Attribute("Name"));

        // Stripped XML must not contain <Tags>.
        var strippedDoc = System.Xml.Linq.XDocument.Parse(stripped);
        Assert.Empty(strippedDoc.Descendants("Tags"));
    }

    /// <summary>
    /// (strip-c) StripTagsFromFragment with both controller-scoped and
    ///           program-scoped &lt;Tags&gt; extracts all Tag children from both
    ///           containers and removes both containers.
    /// </summary>
    [Fact]
    public void StripTagsFromFragment_BothScopes_ExtractsAllTagsAndRemovesBothContainers()
    {
        var tags = ProposeRungEditHandler.StripTagsFromFragment(
            FragmentWithBothTagScopes, out var stripped);

        // Three <Tag> elements extracted (2 controller + 1 program).
        Assert.Equal(3, tags.Count);

        var tagNames = tags.Select(t => (string?)t.Attribute("Name")).ToHashSet();
        Assert.Contains("CtrlTag1", tagNames);
        Assert.Contains("CtrlTag2", tagNames);
        Assert.Contains("ProgTag1", tagNames);

        // Stripped XML must contain no <Tags> containers.
        var strippedDoc = System.Xml.Linq.XDocument.Parse(stripped);
        Assert.Empty(strippedDoc.Descendants("Tags"));
    }

    /// <summary>
    /// (strip-d) StripTagsFromFragment on a fragment with no &lt;Tags&gt; elements
    ///           returns an empty list and the stripped XML still contains RLLContent.
    /// </summary>
    [Fact]
    public void StripTagsFromFragment_NoTagsElements_ReturnsEmptyList_AndRllContentPreserved()
    {
        var tags = ProposeRungEditHandler.StripTagsFromFragment(
            ValidFragment, out var stripped);

        // No tags extracted.
        Assert.Empty(tags);

        // RLLContent must still be present in the stripped XML.
        var strippedDoc = System.Xml.Linq.XDocument.Parse(stripped);
        Assert.NotEmpty(strippedDoc.Descendants("RLLContent"));
    }

    // ── ExecuteAsync workstation path: Tags stripping and session buffering ────

    /// <summary>
    /// (strip-ws-a) On the workstation path, a controller-scoped &lt;Tags&gt;
    ///              element is removed from the fragment written to the temp file.
    ///              The SDK receives only the rungs-only fragment.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_ControllerTags_StrippedFromFragmentSentToSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        // The fragment the SDK received must not contain <Tags>.
        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        Assert.Empty(sdkDoc.Descendants("Tags"));
    }

    /// <summary>
    /// (strip-ws-b) On the workstation path, a program-scoped &lt;Tags&gt;
    ///              element is removed from the fragment written to the temp file.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_ProgramTags_StrippedFromFragmentSentToSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithProgramTags, session, target: "workstation");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        Assert.Empty(sdkDoc.Descendants("Tags"));
    }

    /// <summary>
    /// (strip-ws-c) On the workstation path, RLLContent is preserved after
    ///              &lt;Tags&gt; stripping — the SDK still receives a valid rung fragment.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_TagsStripped_RllContentPreserved()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        Assert.NotEmpty(sdkDoc.Descendants("RLLContent"));
    }

    /// <summary>
    /// (strip-ws-d) On the workstation path, extracted tag declarations are stored
    ///              in session.StagedTagBuffer keyed by tag name.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_ControllerTags_BufferedInSession()
    {
        var (session, _) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        Assert.True(session.StagedTagBuffer.ContainsKey("CtrlTag1"),
            "StagedTagBuffer must contain 'CtrlTag1' after stripping controller-scoped Tags.");
    }

    /// <summary>
    /// (strip-ws-e) On the workstation path, program-scoped tag declarations are
    ///              stored in session.StagedTagBuffer.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_ProgramTags_BufferedInSession()
    {
        var (session, _) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithProgramTags, session, target: "workstation");

        Assert.True(session.StagedTagBuffer.ContainsKey("ProgTag1"),
            "StagedTagBuffer must contain 'ProgTag1' after stripping program-scoped Tags.");
    }

    /// <summary>
    /// (strip-ws-f) On the workstation path with both scopes, all Tag children from
    ///              both containers are buffered in the session.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_BothTagScopes_AllBufferedInSession()
    {
        var (session, _) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithBothTagScopes, session, target: "workstation");

        Assert.True(session.StagedTagBuffer.ContainsKey("CtrlTag1"));
        Assert.True(session.StagedTagBuffer.ContainsKey("CtrlTag2"));
        Assert.True(session.StagedTagBuffer.ContainsKey("ProgTag1"));
    }

    /// <summary>
    /// (strip-ws-g) On the workstation path, when the fragment contains no &lt;Tags&gt;
    ///              elements, the session buffer remains empty and the operation succeeds.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_NoTags_BufferRemainsEmpty_OperationSucceeds()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "workstation");

        // No tags stripped — buffer must be empty.
        Assert.Empty(session.StagedTagBuffer);
        // Operation must succeed regardless.
        Assert.IsType<ProposeRungEditResult>(result);
    }

    /// <summary>
    /// (strip-ws-h) The buffered XElement for a tag preserves the tag's DataType attribute,
    ///              verifying that full element fidelity is maintained in the buffer.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_BufferedTag_PreservesAttributes()
    {
        var (session, _) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        Assert.True(session.StagedTagBuffer.TryGetValue("CtrlTag1", out var buffered));
        // The DataType attribute must be preserved.
        Assert.Equal("BOOL", (string?)buffered!.Attribute("DataType"));
    }

    // ── ExecuteAsync controller path: Tags are NOT stripped ───────────────────

    /// <summary>
    /// (strip-ctrl-a) On the controller path, &lt;Tags&gt; elements are NOT stripped —
    ///                the full fragment (including Tags) is sent to the SDK.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_TagsNotStripped_FullFragmentSentToSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "controller");

        // The fragment the SDK received on the controller path must contain <Tags>.
        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        Assert.NotEmpty(sdkDoc.Descendants("Tags"));
    }

    /// <summary>
    /// (strip-ctrl-b) On the controller path, the session buffer is NOT populated —
    ///                tags in a controller-bound fragment are part of the intended change.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_SessionBufferUnaffected()
    {
        var (session, _) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "controller");

        // Controller path must not populate the staging buffer.
        Assert.Empty(session.StagedTagBuffer);
    }

    // ── stagedTags field in ProposeRungEditResult ──────────────────────────────

    /// <summary>
    /// (stag-a) On the workstation path, when the fragment contains controller-scoped
    ///          tag declarations, the returned ProposeRungEditResult.StagedTags is a
    ///          non-null list containing those tag names.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_WithTags_ResultContainsStagedTagNames()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.StagedTags);
        Assert.Contains("CtrlTag1", result.StagedTags!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (stag-b) On the workstation path, when the fragment contains no tag declarations,
    ///          ProposeRungEditResult.StagedTags is null (omitted from JSON).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_NoTags_ResultStagedTagsIsNull()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "workstation");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.Null(result.StagedTags);
    }

    /// <summary>
    /// (stag-c) On the controller path, ProposeRungEditResult.StagedTags is null —
    ///          the buffer is not consulted or populated on the controller path.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_ResultStagedTagsIsNull()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.Null(result.StagedTags);
    }

    /// <summary>
    /// (stag-d) When a second workstation staging call is made after a first, the
    ///          returned StagedTags reflects the full cumulative buffer (both tag names)
    ///          sorted alphabetically.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_TwoCalls_StagedTagsReflectsFullBuffer()
    {
        var (session, _) = await BuildOpenSessionAsync();

        // First call: buffers CtrlTag1.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        // Second call: buffers ProgTag1.
        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, FragmentWithProgramTags, session, target: "workstation");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.StagedTags);
        Assert.Contains("CtrlTag1", result.StagedTags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ProgTag1", result.StagedTags!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (stag-e) When the fragment has both controller-scoped and program-scoped tags,
    ///          StagedTags includes all buffered tag names in alphabetical order.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WorkstationPath_BothTagScopes_StagedTagsAlphabetical()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithBothTagScopes, session, target: "workstation");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.StagedTags);
        // All three tag names must be present.
        Assert.Contains("CtrlTag1", result.StagedTags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CtrlTag2", result.StagedTags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ProgTag1", result.StagedTags!, StringComparer.OrdinalIgnoreCase);
        // Must be sorted alphabetically.
        var sorted = result.StagedTags!.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(sorted, result.StagedTags!.ToList());
    }

    // ── StagedTagBuffer lifecycle ─────────────────────────────────────────────

    /// <summary>
    /// (buf-a) StagedTagBuffer is empty on a freshly opened session.
    /// </summary>
    [Fact]
    public async Task StagedTagBuffer_NewSession_IsEmpty()
    {
        var (session, _) = await BuildOpenSessionAsync();

        Assert.Empty(session.StagedTagBuffer);
    }

    /// <summary>
    /// (buf-b) StagedTagBuffer is cleared when the session is disposed.
    /// </summary>
    [Fact]
    public async Task StagedTagBuffer_AfterDispose_IsCleared()
    {
        var (session, _) = await BuildOpenSessionAsync();

        // Populate the buffer via a workstation staging call.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        Assert.NotEmpty(session.StagedTagBuffer);

        session.Dispose();

        Assert.Empty(session.StagedTagBuffer);
    }

    /// <summary>
    /// (buf-c) A later workstation stage for the same tag name overwrites the
    ///         earlier buffer entry (last-write-wins by tag name).
    /// </summary>
    [Fact]
    public async Task StagedTagBuffer_DuplicateTagName_LastWriteWins()
    {
        const string FragmentFirstDINT =
            "<RSLogix5000Content SchemaRevision=\"1.0\">" +
              "<Controller>" +
                "<Tags>" +
                  "<Tag Name=\"CtrlTag1\" DataType=\"DINT\" />" +
                "</Tags>" +
                "<Programs><Program Name=\"MainProgram\"><Routines>" +
                "<Routine Name=\"MainRoutine\"><RLLContent>" +
                "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
                "</RLLContent></Routine></Routines></Program></Programs>" +
              "</Controller>" +
            "</RSLogix5000Content>";

        const string FragmentSecondREAL =
            "<RSLogix5000Content SchemaRevision=\"1.0\">" +
              "<Controller>" +
                "<Tags>" +
                  "<Tag Name=\"CtrlTag1\" DataType=\"REAL\" />" +
                "</Tags>" +
                "<Programs><Program Name=\"MainProgram\"><Routines>" +
                "<Routine Name=\"MainRoutine\"><RLLContent>" +
                "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
                "</RLLContent></Routine></Routines></Program></Programs>" +
              "</Controller>" +
            "</RSLogix5000Content>";

        var (session, _) = await BuildOpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentFirstDINT, session, target: "workstation");
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, FragmentSecondREAL, session, target: "workstation");

        // Last write (REAL) must win.
        Assert.True(session.StagedTagBuffer.TryGetValue("CtrlTag1", out var buffered));
        Assert.Equal("REAL", (string?)buffered!.Attribute("DataType"));
    }

    // ── Controller publish path: buffered tag merge and disclosure ────────────

    // Fragment with a controller-scoped tag whose name falls in the Cmd_ namespace.
    private const string FragmentWithCmdTag =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags>" +
              "<Tag Name=\"Cmd_MotorStart\" DataType=\"BOOL\" />" +
            "</Tags>" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    // Fragment with a controller-scoped tag whose name does NOT fall in a granted namespace.
    private const string FragmentWithNonGrantedTag =
        "<RSLogix5000Content SchemaRevision=\"1.0\">" +
          "<Controller>" +
            "<Tags>" +
              "<Tag Name=\"SomePlainTag\" DataType=\"DINT\" />" +
            "</Tags>" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs>" +
          "</Controller>" +
        "</RSLogix5000Content>";

    /// <summary>
    /// (pub-a) an earlier change: a buffered tag from prior workstation staging
    ///         IS merged into a publish fragment that has no &lt;Tags&gt; of its own —
    ///         a new &lt;Tags Use="Context"&gt; container is created to carry it, since
    ///         the controller path must actually commit the accumulated batch (rungs
    ///         and buffered tags) rather than silently drop the tags.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_BufferedTagsAlone_MergedIntoFragmentSentToSdk()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage a tag via the workstation path — it lands in the buffer.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        // Publish with a fragment that has no <Tags>.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "controller");

        // The SDK must receive the buffered tag, merged into a new Use="Context"
        // container since the publish fragment carried no <Tags> of its own.
        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        var tagNames = sdkDoc.Descendants("Tag")
            .Select(t => (string?)t.Attribute("Name"))
            .Where(n => n is not null)
            .ToList();
        Assert.Contains("CtrlTag1", tagNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (pub-b) an earlier change: with a buffered tag AND a different fragment
    ///         tag, the SDK receives both — the fragment's own tag untouched, and the
    ///         buffered tag merged into a newly created &lt;Tags Use="Context"&gt;
    ///         container (this fragment's only &lt;Tags&gt; is program-scoped, not a
    ///         controller-scoped context container, so one is created for the merge).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_BufferedAndFragmentTags_SdkReceivesBothTags()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage CtrlTag1 via workstation.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        // Publish with a fragment containing ProgTag1 (different tag).
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithProgramTags, session, target: "controller");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        var tagNames = sdkDoc.Descendants("Tag")
            .Select(t => (string?)t.Attribute("Name"))
            .Where(n => n is not null)
            .ToList();
        Assert.Contains("ProgTag1", tagNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CtrlTag1", tagNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (pub-c) When both the buffer and the fragment declare a tag with the same name,
    ///         the fragment's version wins (fragment overlay takes precedence over buffer).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_SameTagInBufferAndFragment_FragmentWins()
    {
        const string FragmentBufferedDINT =
            "<RSLogix5000Content SchemaRevision=\"1.0\">" +
              "<Controller>" +
                "<Tags><Tag Name=\"CtrlTag1\" DataType=\"DINT\" /></Tags>" +
                "<Programs><Program Name=\"MainProgram\"><Routines>" +
                "<Routine Name=\"MainRoutine\"><RLLContent>" +
                "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
                "</RLLContent></Routine></Routines></Program></Programs>" +
              "</Controller>" +
            "</RSLogix5000Content>";

        const string FragmentPublishBOOL =
            "<RSLogix5000Content SchemaRevision=\"1.0\">" +
              "<Controller>" +
                "<Tags><Tag Name=\"CtrlTag1\" DataType=\"BOOL\" /></Tags>" +
                "<Programs><Program Name=\"MainProgram\"><Routines>" +
                "<Routine Name=\"MainRoutine\"><RLLContent>" +
                "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
                "</RLLContent></Routine></Routines></Program></Programs>" +
              "</Controller>" +
            "</RSLogix5000Content>";

        var (session, fake) = await BuildOpenSessionAsync();

        // Buffer CtrlTag1 as DINT via workstation.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentBufferedDINT, session, target: "workstation");

        // Publish CtrlTag1 as BOOL (fragment wins on collision).
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentPublishBOOL, session, target: "controller");

        Assert.NotNull(fake.LastCapturedXmlContent);
        var sdkDoc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);
        var ctrlTag = sdkDoc.Descendants("Tag")
            .FirstOrDefault(t => string.Equals(
                (string?)t.Attribute("Name"), "CtrlTag1", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ctrlTag);
        Assert.Equal("BOOL", (string?)ctrlTag!.Attribute("DataType"));
    }

    /// <summary>
    /// (pub-d) On the controller path with no buffered tags and no tags in the fragment,
    ///         the result's TagsCreated field is null (omitted from JSON).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_NoTags_TagsCreatedIsNull()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.Null(result.TagsCreated);
    }

    /// <summary>
    /// (pub-e) On the controller path, when a tag is published, the result's TagsCreated
    ///         field lists that tag name.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_WithFragmentTag_TagsCreatedListed()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.TagsCreated);
        Assert.Contains(result.TagsCreated!, e =>
            string.Equals(e.TagName, "CtrlTag1", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// (pub-f) Even when a published tag name matches a command prefix (Cmd_ or Sim_),
    ///         its PublishedTagEntry.WritableViaLogixWrite is false — the
    ///         CreatedByThisSession guard blocks logix_write for the entire
    ///         session lifetime; the tag becomes writable only after logix_connect.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_CmdPrefixTag_WritableViaLogixWriteFalse()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithCmdTag, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.TagsCreated);
        var entry = result.TagsCreated!.FirstOrDefault(e =>
            string.Equals(e.TagName, "Cmd_MotorStart", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.False(entry!.WritableViaLogixWrite,
            "Cmd_MotorStart is session-created and blocked by CreatedByThisSession — " +
            "WritableViaLogixWrite must be false for all session-created tags.");
    }

    /// <summary>
    /// (pub-g) When a published tag name does NOT match any command prefix,
    ///         its PublishedTagEntry.WritableViaLogixWrite is false.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_NonGrantedTag_WritableViaLogixWriteFalse()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithNonGrantedTag, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.TagsCreated);
        var entry = result.TagsCreated!.FirstOrDefault(e =>
            string.Equals(e.TagName, "SomePlainTag", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.False(entry!.WritableViaLogixWrite,
            "SomePlainTag does not match any command prefix and must have WritableViaLogixWrite=false.");
    }

    /// <summary>
    /// (pub-h) On the controller path, the StagedTagBuffer is cleared after a
    ///         successful publish (buffered tags must not be re-sent on a future call).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_AfterPublish_BufferIsCleared()
    {
        var (session, _) = await BuildOpenSessionAsync();

        // Stage a tag via workstation.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        Assert.NotEmpty(session.StagedTagBuffer);

        // Publish to controller — buffer must be cleared on success.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "controller");

        Assert.Empty(session.StagedTagBuffer);
    }

    /// <summary>
    /// (pub-i) The success message for the controller path includes the required tag
    ///         disclosure text when tags are created: lists their names, states that
    ///         creation is immediate and NOT a pending edit.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_WithTags_MessageContainsTagDisclosure()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.Contains("TAGS CREATED", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CtrlTag1", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT a pending edit", result.Message, StringComparison.Ordinal);
        Assert.Contains("cancelling rung edits", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (pub-j) When a published tag is in a command namespace (Cmd_ or Sim_),
    ///         the success message still names logix_write — as denying the tag
    ///         (External Access Read Only), not as newly writable.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_GrantedTag_MessageMentionsLogixWrite()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithCmdTag, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.Contains("logix_write", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A publish that creates tags discloses that they are External Access
    /// Read Only, and no longer flags Cmd_*/Sim_* names as newly writable.
    /// </summary>
    [Fact]
    public async Task CreatedTags_DisclosureSaysReadOnly_NotNewlyWritable()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithCmdTag, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.Contains(
            "created tags are External Access Read Only; they are readable and not writable " +
            "through lgx-mcp or any CIP client until an engineer sets them to Read/Write in " +
            "Logix Designer",
            result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will be writable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from a new session", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (pub-k) On the controller path with no tags, the message does NOT contain any
    ///         tag-creation disclosure text — a publish with no tags says nothing about tags.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_NoTags_MessageHasNoTagDisclosure()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ValidFragment, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.DoesNotContain("TAGS CREATED", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT a pending edit", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("External Access Read Only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (pub-l) an earlier change: TagsCreated reflects both the fragment's own tags
    ///         and any buffer-flushed tags merged in by MergeBufferedTagsIntoFragment —
    ///         the union of both sets appears in tagsCreated after a controller publish.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControllerPath_MergedTags_TagsCreatedContainsOnlyFragmentTag()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        // Stage CtrlTag1 via workstation (strips it into StagedTagBuffer).
        // FragmentWithControllerTags carries a NOP rung, so StagedRungBuffer gets
        // "NOP;" at insertPosition 0 — the post-publish census check
        // will fire on the controller publish and must find that rung as a pending
        // I-type entry.
        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithControllerTags, session, target: "workstation");

        // Reflect the staged NOP rung as a pending I-type rung in the export so the
        // post-publish census check passes and the handler returns ProposeRungEditResult
        // rather than ProposeRungEditMismatch.
        const string NopPendingExport =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
            "ContainsContext=\"true\">" +
            "<Controller Name=\"DemoController\">" +
            "<Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\" Type=\"RLL\">" +
            "<RLLContent>" +
            "<Rung Number=\"0\" Type=\"I\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent>" +
            "</Routine></Routines></Program></Programs>" +
            "</Controller>" +
            "</RSLogix5000Content>";
        fake.ExportContent = NopPendingExport;

        // Publish with ProgTag1 (different tag).
        var raw = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithProgramTags, session, target: "controller");

        var result = Assert.IsType<ProposeRungEditResult>(raw);
        Assert.NotNull(result.TagsCreated);
        var tagNames = result.TagsCreated!.Select(e => e.TagName).ToList();
        // Buffer-flushed tag must now appear in tagsCreated (union fix).
        Assert.Contains("CtrlTag1", tagNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ProgTag1", tagNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proposing into a routine that does not exist in the program returns a typed
    /// <c>RoutineNotFound</c> error listing the program's routines, with the "create it with
    /// logix_create_routine" hint, before any envelope export or import is attempted.
    /// </summary>
    [Fact]
    public async Task ProposeIntoNonexistentRoutine_ReturnsRoutineNotFound()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        // The SDK cannot export a routine that does not exist (the fake throws when no generic
        // export is configured); the program export still lists the routines that do exist.
        fake.ExportContent = null;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "Helper");
        const string missingXPath =
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='NoSuchRoutine']";

        var result = await ProposeRungEditHandler.ExecuteAsync(
            missingXPath, 0, 0, null, session,
            target: "controller",
            rungs: new[] { new RungInput("NOP();") });

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("RoutineNotFound", error.Kind);
        Assert.Contains("MainRoutine", error.Error);
        Assert.Contains("Helper", error.Error);
        Assert.Contains("NoSuchRoutine", error.Error);
        Assert.Equal(
            "Create the routine with logix_create_routine first (type RLL), then propose into it.", error.Hint);
        Assert.Contains("logix_create_routine", ProposeRungEditHandler.RoutineNotFoundHint);
        Assert.Empty(fake.CapturedXPaths);
    }
}

// ── SpyFakeLogixProject ───────────────────────────────────────────────────────

/// <summary>
/// A variant of the recording fake that invokes a callback when
/// <see cref="PartialImportRungsFromXmlFileAsync"/> is called, passing
/// the <c>xmlFileToImport</c> path to the callback.
/// Used to assert on temp-file lifecycle (exists during call, deleted after).
/// </summary>
internal sealed class SpyFakeLogixProject : ILogixProject
{
    private readonly Action<string> _onImport;

    public SpyFakeLogixProject(Action<string> onImport) =>
        _onImport = onImport;

    public void Dispose() { }

    public System.Threading.Tasks.Task PartialImportRungsFromXmlFileAsync(
        string xPath,
        uint insertPosition,
        uint replaceCount,
        string xmlFileToImport,
        RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption onlineImportOption,
        CancellationToken ct = default)
    {
        _onImport(xmlFileToImport);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    // ── Remaining stubs ───────────────────────────────────────────────────────
    public System.Threading.Tasks.Task<string> GetCommunicationsPathAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SetCommunicationsPathAsync(string projectPath, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ChangeControllerTypeAsync(string processorTypeName, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SaveAsAsync(string savePath, bool force, bool detailedL5x, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SaveAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task DownloadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<RockwellAutomation.LogixDesigner.LogixProject.ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ChangeControllerModeAsync(RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode mode, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task BuildAsync(RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget target, CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task UploadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task StoreImageOnSDCardAsync(RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent loadEvent, RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode loadMode, RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate afuSetting, string imageName, string imageNote) => throw new NotImplementedException();
    public System.Threading.Tasks.Task LoadImageFromSDCardAsync() => throw new NotImplementedException();
    public void AddEventHandler(IOperationEvent handler) { }
    public void ClearEventHandlers() { }
    public void RemoveEventHandler(IOperationEvent handler) { }
    public System.Threading.Tasks.Task<ByteString> GetTagValueAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, RockwellAutomation.LogixDesigner.LogixProject.DataType type) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<bool>   GetTagValueBOOLAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(false);
    public System.Threading.Tasks.Task<sbyte>  GetTagValueSINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult((sbyte)0);
    public System.Threading.Tasks.Task<short>  GetTagValueINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult((short)0);
    public System.Threading.Tasks.Task<int>    GetTagValueDINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(0);
    public System.Threading.Tasks.Task<long>   GetTagValueLINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(0L);
    public System.Threading.Tasks.Task<byte>   GetTagValueUSINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult((byte)0);
    public System.Threading.Tasks.Task<ushort> GetTagValueUINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult((ushort)0);
    public System.Threading.Tasks.Task<uint>   GetTagValueUDINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(0u);
    public System.Threading.Tasks.Task<ulong>  GetTagValueULINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(0ul);
    public System.Threading.Tasks.Task<string> GetTagValueSTRINGAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(string.Empty);
    public System.Threading.Tasks.Task<float>  GetTagValueREALAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(0f);
    public System.Threading.Tasks.Task<double> GetTagValueLREALAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode) => System.Threading.Tasks.Task.FromResult(0.0);
    public System.Threading.Tasks.Task SetTagValueAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, byte[] tagValue, RockwellAutomation.LogixDesigner.LogixProject.DataType type) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SetTagValueBOOLAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, bool tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueSINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, sbyte tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, short tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueDINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, int tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueLINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, long tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueUSINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, byte tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueUINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, ushort tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueUDINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, uint tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueULINTAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, ulong tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueSTRINGAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, string tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueREALAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, float tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task SetTagValueLREALAsync(string tagPath, RockwellAutomation.LogixDesigner.LogixProject.OperationMode mode, double tagValue) => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task PartialExportToXmlFileAsync(string xPath, string filePath, CancellationToken ct = default)
    {
        // Name resolution exports Controller/Programs; anything else stays unsupported.
        if (xPath != ProposeRungEditRecordingFake.ProgramsXPath)
            throw new NotImplementedException();
        File.WriteAllText(filePath, ProposeRungEditRecordingFake.ProgramsExport("MainRoutine"));
        return System.Threading.Tasks.Task.CompletedTask;
    }
    public System.Threading.Tasks.Task PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption onlineImportOption, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task GoOnlineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task GoOfflineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<RockwellAutomation.LogixDesigner.LogixProject.ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Online);
    public System.Threading.Tasks.Task<string> GetSafetyNetworkNumberAsync(string moduleName, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<bool> IsSafetyLockedAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SafetyLockAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SafetyUnlockAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SetSafetyLockPasswordAsync(string newPassword, string oldPassword, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task SetSafetyUnlockPasswordAsync(string newPassword, string oldPassword, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task GenerateSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task DeleteSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<string> GetSafetySignatureAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ProtectWithLicenseAsync(string xPath, uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ProtectWithLicenseAsync(IEnumerable<string> xPaths, uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ProtectAllWithLicenseAsync(uint licenseFirmCode, uint licenseProductCode, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ProtectAllWithPasswordAsync(string password, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task UnprotectAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ProtectWithPasswordAsync(string xPath, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task ProtectWithPasswordAsync(IEnumerable<string> xPaths, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task UnprotectAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task UnprotectAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task LockExecutableAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task LockExecutableAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<bool> IsExecutableLockedAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task UnlockExecutableAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task UnlockExecutableAsync(IEnumerable<string> xPaths, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task LockAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task UnlockAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<bool> IsExecutableProtectedAsync(string xPath, CancellationToken ct = default) => throw new NotImplementedException();
    public System.Threading.Tasks.Task<string[]> GetAllExecutablesAsync(CancellationToken ct = default) => throw new NotImplementedException();
}
#endif
