#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

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

// ── FakeExportProject ─────────────────────────────────────────────────────────

/// <summary>
/// <see cref="ILogixProject"/> stub for testing <see cref="ExportRungsHandler"/>
/// and <see cref="LogixSdkTools"/>.
///
/// <para>
/// Supports configurable behaviour for
/// <see cref="PartialExportToXmlFileAsync"/>:
/// <list type="bullet">
///   <item>When <see cref="ExportContent"/> is set, writes that string to the
///     <c>filePath</c> argument and completes successfully.</item>
///   <item>When <see cref="ExportShouldThrowOperationFailed"/> is <c>true</c>,
///     throws <see cref="OperationFailedException"/>.</item>
///   <item>When <see cref="ExportShouldThrowOperationNotPerformed"/> is <c>true</c>,
///     throws <see cref="OperationNotPerformedException"/>.</item>
/// </list>
/// All other members throw <see cref="NotImplementedException"/>.
/// </para>
/// </summary>
internal sealed class FakeExportProject : ILogixProject
{
    /// <summary>Content to write to the export file. Required for success.</summary>
    public string? ExportContent { get; set; }

    /// <summary>When true, <see cref="PartialExportToXmlFileAsync"/> throws <see cref="OperationFailedException"/>.</summary>
    public bool ExportShouldThrowOperationFailed { get; set; }

    /// <summary>When true, <see cref="PartialExportToXmlFileAsync"/> throws <see cref="OperationNotPerformedException"/>.</summary>
    public bool ExportShouldThrowOperationNotPerformed { get; set; }

    /// <summary>When true, <see cref="PartialExportToXmlFileAsync"/> throws an unexpected <see cref="InvalidOperationException"/>.</summary>
    public bool ExportShouldThrowUnexpectedException { get; set; }

    /// <summary>Records the xPath argument supplied to the last export call.</summary>
    public string? LastExportXPath { get; private set; }

    /// <summary>Records the filePath argument supplied to the last export call.</summary>
    public string? LastExportFilePath { get; private set; }

    /// <summary>Number of times <see cref="Dispose"/> has been called.</summary>
    public int DisposeCallCount { get; private set; }

    public void Dispose() => DisposeCallCount++;

    public Task PartialExportToXmlFileAsync(
        string xPath, string filePath, CancellationToken ct = default)
    {
        LastExportXPath    = xPath;
        LastExportFilePath = filePath;

        if (ExportShouldThrowOperationFailed)
            throw new OperationFailedException("Simulated export failure.", xPath);

        if (ExportShouldThrowOperationNotPerformed)
            throw new OperationNotPerformedException("Simulated export not-performed.", xPath);

        if (ExportShouldThrowUnexpectedException)
            throw new InvalidOperationException("Simulated unexpected error.");

        if (ExportContent is not null)
            File.WriteAllText(filePath, ExportContent);

        return Task.CompletedTask;
    }

    // ── All other ILogixProject members — throw NotImplementedException ────────

    public Task<string> GetCommunicationsPathAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task SetCommunicationsPathAsync(string projectPath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ChangeControllerTypeAsync(string processorTypeName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveAsAsync(string savePath, bool force, bool detailedL5x, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task DownloadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) => throw new NotImplementedException();
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

    public Task PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportRungsFromXmlFileAsync(string xPath, uint insertPosition, uint replaceCount, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default) => throw new NotImplementedException();

    public Task GoOnlineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task GoOfflineAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default) => throw new NotImplementedException();

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

/// <summary>Shared helpers for building test sessions and services.</summary>
internal static class ExportTestHelpers
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// Builds a <see cref="LogixProjectSession"/> backed by <paramref name="fake"/>
    /// and opens it immediately so callers get an already-open session.
    /// </summary>
    public static async Task<(LogixProjectSession Session, FakeExportProject Fake)>
        OpenedSessionAsync(FakeExportProject? fake = null)
    {
        fake ??= new FakeExportProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        return (session, fake);
    }

    /// <summary>
    /// Builds a <see cref="ProjectService"/> that reports a loaded project at
    /// <see cref="FixturePath"/>.
    /// </summary>
    public static ProjectService LoadedProjectService()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        return svc;
    }

    /// <summary>
    /// Builds a <see cref="LogixSdkTools"/> pre-wired with a session backed by
    /// <paramref name="fake"/> (already open), bypassing the real SDK factory.
    /// </summary>
    public static async Task<(LogixSdkTools Tools, FakeExportProject Fake)>
        ToolsWithFakeAsync(FakeExportProject? fake = null)
    {
        fake ??= new FakeExportProject { ExportContent = "<RSLogix5000Content />" };
        var (session, _) = await OpenedSessionAsync(fake);
        var tools = new LogixSdkTools(LoadedProjectService(), new TagSessionService());
        return (tools, fake);
    }
}

// ── ExportRungsHandlerTests ───────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="ExportRungsHandler"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Blank xPath returns <see cref="ExportRungsError"/>.</item>
///   <item>Success: temp file written, content returned, temp file cleaned up.</item>
///   <item>xPath forwarded verbatim to the SDK call.</item>
///   <item><see cref="OperationFailedException"/> surfaces as structured error.</item>
///   <item><see cref="OperationNotPerformedException"/> surfaces as structured error.</item>
///   <item>Temp file is always deleted — even on export failure.</item>
///   <item>Session in NotOpened state returns <see cref="ExportRungsError"/>.</item>
/// </list>
/// </summary>
public class ExportRungsHandlerTests
{
    private const string SampleXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string SampleL5X =
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" /></Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    // ── Blank xPath ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BlankXPath_ReturnsError()
    {
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync();

        var result = await ExportRungsHandler.ExecuteAsync("   ", session);

        var error = Assert.IsType<ExportRungsError>(result);
        Assert.Contains("blank", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyXPath_ReturnsError()
    {
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync();

        var result = await ExportRungsHandler.ExecuteAsync(string.Empty, session);

        Assert.IsType<ExportRungsError>(result);
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsExportRungsResult()
    {
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.IsType<ExportRungsResult>(result);
    }

    [Fact]
    public async Task ExecuteAsync_Success_StatusIsOk()
    {
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = (ExportRungsResult)await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsCorrectL5XContent()
    {
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = (ExportRungsResult)await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Equal(SampleL5X, result.L5X);
    }

    [Fact]
    public async Task ExecuteAsync_Success_XPathPreservedInResult()
    {
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = (ExportRungsResult)await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Equal(SampleXPath, result.XPath);
    }

    [Fact]
    public async Task ExecuteAsync_Success_XPathForwardedToSdk()
    {
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Equal(SampleXPath, fake.LastExportXPath);
    }

    // ── Temp-file cleanup ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Success_TempFileDeletedAfterRead()
    {
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        // The temp file path was captured by FakeExportProject.LastExportFilePath.
        var capturedFilePath = fake.LastExportFilePath;

        Assert.NotNull(capturedFilePath);
        Assert.False(File.Exists(capturedFilePath),
            $"Temp file '{capturedFilePath}' should have been deleted after a successful export.");
    }

    [Fact]
    public async Task ExecuteAsync_ExportFails_TempFileStillDeleted()
    {
        var fake = new FakeExportProject { ExportShouldThrowOperationFailed = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        // The fake throws before writing, but GetTempFileName() pre-creates the file.
        // The handler should still delete it.
        var exportedPath = fake.LastExportFilePath;
        Assert.NotNull(exportedPath);
        Assert.False(File.Exists(exportedPath),
            $"Temp file '{exportedPath}' should have been deleted even after an export failure.");
    }

    // ── OperationFailedException ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OperationFailed_ReturnsError()
    {
        var fake = new FakeExportProject { ExportShouldThrowOperationFailed = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.IsType<ExportRungsError>(result);
    }

    [Fact]
    public async Task ExecuteAsync_OperationFailed_ErrorMentionsFailed()
    {
        var fake = new FakeExportProject { ExportShouldThrowOperationFailed = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var error = (ExportRungsError)await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Contains("failed", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── OperationNotPerformedException ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OperationNotPerformed_ReturnsError()
    {
        var fake = new FakeExportProject { ExportShouldThrowOperationNotPerformed = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.IsType<ExportRungsError>(result);
    }

    [Fact]
    public async Task ExecuteAsync_OperationNotPerformed_ErrorMentionsXPath()
    {
        var fake = new FakeExportProject { ExportShouldThrowOperationNotPerformed = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var error = (ExportRungsError)await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Contains(SampleXPath, error.Error);
    }

    // ── Unexpected exception ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnexpectedException_ReturnsError()
    {
        var fake = new FakeExportProject { ExportShouldThrowUnexpectedException = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var result = await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.IsType<ExportRungsError>(result);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedException_ErrorMentionsXPath()
    {
        var fake = new FakeExportProject { ExportShouldThrowUnexpectedException = true };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var error = (ExportRungsError)await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.Contains(SampleXPath, error.Error);
    }

    // ── Session not open ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SessionNotOpen_ReturnsError()
    {
        // Construct a session but do NOT open it.
        var session = new LogixProjectSession(
            (_p, _ct) => Task.FromResult<ILogixProject>(new FakeExportProject()));

        var result = await ExportRungsHandler.ExecuteAsync(SampleXPath, session);

        Assert.IsType<ExportRungsError>(result);
    }
}

// ── LogixSdkToolsTests ────────────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="LogixSdkTools"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>No project loaded → structured error.</item>
///   <item>Project loaded, session open → success result is JSON-serialized.</item>
///   <item>Tool is registered with the correct MCP tool name.</item>
/// </list>
/// </summary>
public class LogixSdkToolsTests
{
    private const string SampleXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string SampleL5X = "<RSLogix5000Content />";

    // ── No project loaded (upload-path session) ───────────────────────────────

    /// <summary>
    /// <see cref="LogixSdkTools.GetExportRungsResponseAsync"/> must succeed when
    /// an SDK session is provided directly — even when no project file has been
    /// loaded via <c>logix_open_project</c>.
    ///
    /// <para>
    /// This is the upload-path scenario: the session was established by uploading
    /// from a controller (not by opening a local file), so <see cref="ProjectService"/>
    /// is empty.  Export only needs the live SDK session, not the local file.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetExportRungsResponseAsync_NoProjectLoaded_WithSession_ReturnsSuccess()
    {
        var svc   = new ProjectService();   // Not loaded — upload-path scenario.
        var tools = new LogixSdkTools(svc, new TagSessionService());

        // Create an open session to pass in directly (avoids real SDK open).
        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var json = await tools.GetExportRungsResponseAsync(SampleXPath, session);
        var doc  = JsonDocument.Parse(json);

        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusProp),
            $"Expected 'status' property (not an error) in: {json}");
        Assert.Equal("ok", statusProp.GetString());
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetExportRungsResponseAsync_ProjectLoaded_ReturnsJsonResult()
    {
        var svc   = ExportTestHelpers.LoadedProjectService();
        var tools = new LogixSdkTools(svc, new TagSessionService());

        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var json = await tools.GetExportRungsResponseAsync(SampleXPath, session);
        var doc  = JsonDocument.Parse(json);

        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusProp),
            $"Expected 'status' property in: {json}");
        Assert.Equal("ok", statusProp.GetString());
    }

    [Fact]
    public async Task GetExportRungsResponseAsync_ProjectLoaded_L5XInResult()
    {
        var svc   = ExportTestHelpers.LoadedProjectService();
        var tools = new LogixSdkTools(svc, new TagSessionService());

        var fake = new FakeExportProject { ExportContent = SampleL5X };
        var (session, _) = await ExportTestHelpers.OpenedSessionAsync(fake);

        var json = await tools.GetExportRungsResponseAsync(SampleXPath, session);
        var doc  = JsonDocument.Parse(json);

        Assert.True(
            doc.RootElement.TryGetProperty("l5x", out var l5xProp),
            $"Expected 'l5x' property in: {json}");
        Assert.Equal(SampleL5X, l5xProp.GetString());
    }

    // ── Tool registration ─────────────────────────────────────────────────────

    [Fact]
    public void LogixSdkTools_ExportRungs_IsRegisteredWithCorrectName()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ExportRungs));
        Assert.NotNull(method);

        var toolAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
        Assert.NotNull(toolAttr);

        var name = toolAttr!.GetType().GetProperty("Name")?.GetValue(toolAttr) as string;
        Assert.Equal("logix_export_rungs", name);
    }

    // ── TryGetActiveSession ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="LogixProjectSession"/> + <see cref="TypedSdkTagService"/>
    /// pair backed by a <see cref="FakeExportProject"/>, attaches it to a
    /// <see cref="TagSessionService"/> wrapped in a <see cref="GatedTagService"/>
    /// (mirroring the production SDK path), and returns the pieces.
    /// </summary>
    private static async Task<(
        TagSessionService TagSession,
        LogixProjectSession Session,
        TypedSdkTagService TypedSvc)>
        ActiveSdkSessionAsync()
    {
        var fake       = new FakeExportProject { ExportContent = SampleL5X };
        var session    = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");

        var svcPath  = Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");
        var l5x      = L5Sharp.Core.L5X.Load(svcPath);
        var typedSvc = new TypedSdkTagService(session, l5x);

        // Wrap in GatedTagService as ConnectHandler does in the SDK path.
        var policy  = GateEngine.DerivePolicy(l5x, GateConfig.Default);
        var gated   = new GatedTagService(typedSvc, policy);

        var tagSession = new TagSessionService();
        tagSession.Attach(gated, isVirtual: false);

        return (tagSession, session, typedSvc);
    }

    [Fact]
    public async Task TryGetActiveSession_WithActiveSdkSession_ReturnsSession()
    {
        var (tagSession, expectedSession, _) = await ActiveSdkSessionAsync();
        var tools = new LogixSdkTools(ExportTestHelpers.LoadedProjectService(), tagSession);

        var result = tools.TryGetActiveSession();

        Assert.Same(expectedSession, result);
    }

    [Fact]
    public void TryGetActiveSession_WithNoActiveSession_ReturnsNull()
    {
        var tagSession = new TagSessionService();   // no service attached
        var tools      = new LogixSdkTools(ExportTestHelpers.LoadedProjectService(), tagSession);

        var result = tools.TryGetActiveSession();

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetActiveSession_WithNonSdkSession_ReturnsNull()
    {
        // Attach a VirtualTagService (non-SDK path) — should return null.
        var tagSession = new TagSessionService();
        var inner      = new VirtualTagService(
            new Dictionary<string, (object, string?)>());
        tagSession.Attach(inner, isVirtual: true);

        var tools = new LogixSdkTools(ExportTestHelpers.LoadedProjectService(), tagSession);

        var result = tools.TryGetActiveSession();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetExportRungsResponseAsync_WithActiveSession_ReusesSession()
    {
        // Arrange: attach an active SDK session and inject a different fake for
        // the test-path session.  The export should route through the active
        // session (which writes SampleL5X) rather than opening a new one.
        var (tagSession, activeSession, _) = await ActiveSdkSessionAsync();
        // The active session's FakeExportProject has ExportContent = SampleL5X.

        var tools = new LogixSdkTools(ExportTestHelpers.LoadedProjectService(), tagSession);

        // Pass the already-open active session directly so ExportRungsHandler can use it.
        var json = await tools.GetExportRungsResponseAsync(SampleXPath, activeSession);
        var doc  = JsonDocument.Parse(json);

        // Should return success (not an error) because the session is already open.
        Assert.True(doc.RootElement.TryGetProperty("status", out var status),
            $"Expected 'status' property in: {json}");
        Assert.Equal("ok", status.GetString());
    }

    // ── No active session → structured error ──────────────────────────────────

    /// <summary>
    /// When no session is injected and no SDK session is registered in
    /// <see cref="TagSessionService"/>, <see cref="LogixSdkTools.GetExportRungsResponseAsync"/>
    /// must return a structured error object (not throw and not return success).
    /// </summary>
    [Fact]
    public async Task GetExportRungsResponseAsync_NoActiveSession_ReturnsError()
    {
        // No session injected, no active session in TagSessionService.
        var tools = new LogixSdkTools(
            ExportTestHelpers.LoadedProjectService(),
            new TagSessionService());

        var json = await tools.GetExportRungsResponseAsync(SampleXPath);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    /// <summary>
    /// When an SDK session is registered in <see cref="TagSessionService"/> and
    /// no session is injected explicitly, <see cref="LogixSdkTools.GetExportRungsResponseAsync"/>
    /// must detect the active session via <see cref="LogixSdkTools.TryGetActiveSession"/>
    /// and route the export through it — not through the explicit-session branch.
    /// </summary>
    [Fact]
    public async Task GetExportRungsResponseAsync_DetectsActiveSessionViaTagService_WhenNoSessionInjected()
    {
        // Attach an active SDK session to TagSessionService — do NOT pass it explicitly.
        var (tagSession, _, _) = await ActiveSdkSessionAsync();
        // ActiveSdkSessionAsync wires FakeExportProject with ExportContent = SampleL5X.

        var tools = new LogixSdkTools(ExportTestHelpers.LoadedProjectService(), tagSession);

        // No session parameter — method must detect via TryGetActiveSession().
        var json = await tools.GetExportRungsResponseAsync(SampleXPath);
        var doc  = JsonDocument.Parse(json);

        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusProp),
            $"Expected 'status' property in: {json}");
        Assert.Equal("ok", statusProp.GetString());
    }

    // ── ExportRungs MCP entry point ───────────────────────────────────────────

    /// <summary>
    /// The <see cref="LogixSdkTools.ExportRungs"/> MCP endpoint must return a
    /// structured error when no active SDK session is registered.
    /// </summary>
    [Fact]
    public async Task ExportRungs_NoActiveSession_ReturnsErrorJson()
    {
        var tools = new LogixSdkTools(
            ExportTestHelpers.LoadedProjectService(),
            new TagSessionService());

        var json = await tools.ExportRungs(SampleXPath);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    /// <summary>
    /// The <see cref="LogixSdkTools.ExportRungs"/> MCP endpoint must use the
    /// active session from <see cref="TagSessionService"/> (via
    /// <see cref="LogixSdkTools.TryGetActiveSession"/>) when no session is
    /// explicitly injected, and must return success.
    /// </summary>
    [Fact]
    public async Task ExportRungs_UsesActiveSessionFromTagService_ReturnsSuccess()
    {
        var (tagSession, _, _) = await ActiveSdkSessionAsync();
        var tools = new LogixSdkTools(ExportTestHelpers.LoadedProjectService(), tagSession);

        var json = await tools.ExportRungs(SampleXPath);
        var doc  = JsonDocument.Parse(json);

        Assert.True(
            doc.RootElement.TryGetProperty("status", out var statusProp),
            $"Expected 'status' property in: {json}");
        Assert.Equal("ok", statusProp.GetString());
    }
}

// ── ExportRungsTests ──────────────────────────────────────────────────────────

/// <summary>Tests for the bare-Controller XPath hint in <see cref="ExportRungsHandler"/>.</summary>
public class ExportRungsTests
{
    [Fact]
    public async Task BareControllerXPath_ErrorHintNamesControllerPrograms()
    {
        var (session, fake) = await ExportTestHelpers.OpenedSessionAsync(
            new FakeExportProject { ExportContent = "<RSLogix5000Content />" });

        var result = await ExportRungsHandler.ExecuteAsync("Controller", session);

        var error = Assert.IsType<ExportRungsError>(result);
        Assert.NotNull(error.Hint);
        Assert.Contains("Controller/Programs", error.Hint);
        Assert.DoesNotContain("XPath passed as argument is not valid", error.Error);
        Assert.Null(fake.LastExportXPath); // SDK not called with the invalid scope
    }

    [Theory]
    [InlineData("Controller/Programs")]
    [InlineData("controller")]
    [InlineData("Controller/Tags")]
    public async Task OtherXPaths_StillForwardedToSdk(string xPath)
    {
        var (session, fake) = await ExportTestHelpers.OpenedSessionAsync(
            new FakeExportProject { ExportContent = "<RSLogix5000Content />" });

        var result = await ExportRungsHandler.ExecuteAsync(xPath, session);

        Assert.IsType<ExportRungsResult>(result);
        Assert.Equal(xPath, fake.LastExportXPath);
    }
}
#endif
