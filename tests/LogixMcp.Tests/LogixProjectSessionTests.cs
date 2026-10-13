#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// Nested-type aliases so tests read as close to the documented SDK API as possible.
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

// ── FakeLogixProject ──────────────────────────────────────────────────────────

/// <summary>
/// Minimal <see cref="ILogixProject"/> stub for unit testing
/// <see cref="LogixProjectSession"/>.
///
/// <para>
/// Only <see cref="Dispose"/> and the configurable
/// <see cref="ObjectDisposedOnNextCall"/> flag are exercised by these tests;
/// all other interface members are intentionally not implemented
/// (<see cref="NotImplementedException"/>).
/// </para>
/// </summary>
internal sealed class FakeLogixProject : ILogixProject
{
    /// <summary>Number of times <see cref="Dispose"/> has been called.</summary>
    public int DisposeCallCount { get; private set; }

    /// <summary>
    /// When <c>true</c>, the next non-Dispose call throws
    /// <see cref="ObjectDisposedException"/> — simulating the SDK throwing
    /// after the channel has been disposed unexpectedly.
    /// </summary>
    public bool ObjectDisposedOnNextCall { get; set; }

    public void Dispose() => DisposeCallCount++;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ThrowIfConfigured()
    {
        if (ObjectDisposedOnNextCall)
            throw new ObjectDisposedException("FakeLogixProject");
    }

    private Task ThrowIfConfiguredAsync()
    {
        ThrowIfConfigured();
        return Task.CompletedTask;
    }

    // ── ILogixProject — all non-Dispose members ───────────────────────────────

    public Task<string> GetCommunicationsPathAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task SetCommunicationsPathAsync(string projectPath, CancellationToken ct = default) => ThrowIfConfiguredAsync();
    public Task ChangeControllerTypeAsync(string processorTypeName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveAsAsync(string savePath, bool force, bool detailedL5x, CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveAsync(CancellationToken ct = default) => throw new NotImplementedException();
    /// <summary>Number of times <see cref="DownloadAsync"/> has been called.</summary>
    public int DownloadCallCount { get; private set; }

    /// <summary>
    /// When non-<c>null</c>, <see cref="DownloadAsync"/> throws this exception
    /// instead of succeeding.
    /// </summary>
    public Exception? DownloadException { get; set; }

    public Task DownloadAsync(CancellationToken ct = default)
    {
        DownloadCallCount++;
        if (DownloadException is not null)
            throw DownloadException;
        return Task.CompletedTask;
    }

    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default) => throw new NotImplementedException();

    /// <summary>
    /// Ordered list of <see cref="RequestedControllerMode"/> values passed to each
    /// <see cref="ChangeControllerModeAsync"/> call, in invocation order.
    /// </summary>
    public List<RequestedControllerMode> ChangeControllerModeCalls { get; } = new();

    /// <summary>
    /// When non-<c>null</c>, <see cref="ChangeControllerModeAsync"/> throws this
    /// exception instead of succeeding.
    /// </summary>
    public Exception? ChangeControllerModeException { get; set; }

    /// <summary>
    /// When set to a non-negative value, <see cref="ChangeControllerModeAsync"/>
    /// throws <see cref="ChangeControllerModeException"/> only on that specific
    /// (zero-based) call index.  A value of <c>-1</c> (default) means throw on
    /// every call when <see cref="ChangeControllerModeException"/> is non-null.
    /// </summary>
    public int ChangeControllerModeExceptionOnCallIndex { get; set; } = -1;

    public Task ChangeControllerModeAsync(RequestedControllerMode mode, CancellationToken ct = default)
    {
        var callIndex = ChangeControllerModeCalls.Count;
        ChangeControllerModeCalls.Add(mode);
        if (ChangeControllerModeException is not null)
        {
            bool shouldThrow = ChangeControllerModeExceptionOnCallIndex < 0
                || callIndex == ChangeControllerModeExceptionOnCallIndex;
            if (shouldThrow)
                throw ChangeControllerModeException;
        }
        return Task.CompletedTask;
    }
    public Task BuildAsync(RequestedBuildTarget target, CancellationToken ct = default) => throw new NotImplementedException();
    /// <summary>Number of times <see cref="UploadAsync"/> has been called.</summary>
    public int UploadCallCount { get; private set; }

    /// <summary>
    /// When non-<c>null</c>, <see cref="UploadAsync"/> throws this exception
    /// instead of succeeding.
    /// </summary>
    public Exception? UploadException { get; set; }

    /// <summary>
    /// When non-<c>null</c>, invoked synchronously at the start of
    /// <see cref="UploadAsync"/> (before any exception is thrown).
    /// Use this to simulate the real SDK's side-effect of overwriting the
    /// working copy file with controller contents.
    /// </summary>
    public Action? UploadAction { get; set; }

    public Task UploadAsync(CancellationToken ct = default)
    {
        UploadCallCount++;
        UploadAction?.Invoke();
        if (UploadException is not null)
            throw UploadException;
        return Task.CompletedTask;
    }
    public Task StoreImageOnSDCardAsync(RequestedLoadEvent loadEvent, RequestedLoadMode loadMode, AutomaticFirmwareUpdate afuSetting, string imageName, string imageNote) => throw new NotImplementedException();
    public Task LoadImageFromSDCardAsync() => throw new NotImplementedException();
    public void AddEventHandler(IOperationEvent handler) { }
    public void ClearEventHandlers() { }
    public void RemoveEventHandler(IOperationEvent handler) { }

    public Task<ByteString> GetTagValueAsync(string tagPath, OperationMode mode, DataType type)
    {
        ThrowIfConfigured();
        return Task.FromResult(ByteString.Empty);
    }

    public Task<bool>   GetTagValueBOOLAsync(string tagPath, OperationMode mode)  { ThrowIfConfigured(); return Task.FromResult(false); }
    public Task<sbyte>  GetTagValueSINTAsync(string tagPath, OperationMode mode)  { ThrowIfConfigured(); return Task.FromResult((sbyte)0); }
    public Task<short>  GetTagValueINTAsync(string tagPath, OperationMode mode)   { ThrowIfConfigured(); return Task.FromResult((short)0); }
    public Task<int>    GetTagValueDINTAsync(string tagPath, OperationMode mode)  { ThrowIfConfigured(); return Task.FromResult(0); }
    public Task<long>   GetTagValueLINTAsync(string tagPath, OperationMode mode)  { ThrowIfConfigured(); return Task.FromResult(0L); }
    public Task<byte>   GetTagValueUSINTAsync(string tagPath, OperationMode mode) { ThrowIfConfigured(); return Task.FromResult((byte)0); }
    public Task<ushort> GetTagValueUINTAsync(string tagPath, OperationMode mode)  { ThrowIfConfigured(); return Task.FromResult((ushort)0); }
    public Task<uint>   GetTagValueUDINTAsync(string tagPath, OperationMode mode) { ThrowIfConfigured(); return Task.FromResult(0u); }
    public Task<ulong>  GetTagValueULINTAsync(string tagPath, OperationMode mode) { ThrowIfConfigured(); return Task.FromResult(0ul); }
    public Task<string> GetTagValueSTRINGAsync(string tagPath, OperationMode mode){ ThrowIfConfigured(); return Task.FromResult(string.Empty); }
    public Task<float>  GetTagValueREALAsync(string tagPath, OperationMode mode)  { ThrowIfConfigured(); return Task.FromResult(0f); }
    public Task<double> GetTagValueLREALAsync(string tagPath, OperationMode mode) { ThrowIfConfigured(); return Task.FromResult(0.0); }

    public Task SetTagValueAsync(string tagPath, OperationMode mode, byte[] tagValue, DataType type) { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueBOOLAsync(string tagPath, OperationMode mode, bool tagValue)    { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueSINTAsync(string tagPath, OperationMode mode, sbyte tagValue)   { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueINTAsync(string tagPath, OperationMode mode, short tagValue)    { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueDINTAsync(string tagPath, OperationMode mode, int tagValue)     { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueLINTAsync(string tagPath, OperationMode mode, long tagValue)    { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueUSINTAsync(string tagPath, OperationMode mode, byte tagValue)   { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueUINTAsync(string tagPath, OperationMode mode, ushort tagValue)  { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueUDINTAsync(string tagPath, OperationMode mode, uint tagValue)   { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueULINTAsync(string tagPath, OperationMode mode, ulong tagValue)  { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueSTRINGAsync(string tagPath, OperationMode mode, string tagValue){ ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueREALAsync(string tagPath, OperationMode mode, float tagValue)   { ThrowIfConfigured(); return Task.CompletedTask; }
    public Task SetTagValueLREALAsync(string tagPath, OperationMode mode, double tagValue) { ThrowIfConfigured(); return Task.CompletedTask; }

    public Task PartialExportToXmlFileAsync(string xPath, string filePath, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportFromXmlFileAsync(string xPath, string xmlFileToImport, ImportCollisionOptions collisionOption, bool continueOnErrors, CancellationToken ct = default) => throw new NotImplementedException();

    /// <summary>Number of times <see cref="PartialImportWithTargetFromXmlFileAsync"/> has been called.</summary>
    public int PartialImportWithTargetCallCount { get; private set; }

    public Task PartialImportWithTargetFromXmlFileAsync(string xPath, string targetName, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        PartialImportWithTargetCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>Number of times <see cref="PartialImportRungsFromXmlFileAsync"/> has been called.</summary>
    public int PartialImportRungsCallCount { get; private set; }

    /// <summary>Ordered list of <see cref="PartialImportOption"/> values passed to each
    /// <see cref="PartialImportRungsFromXmlFileAsync"/> call.</summary>
    public List<PartialImportOption> PartialImportRungsOptions { get; } = new();

    public Task PartialImportRungsFromXmlFileAsync(string xPath, uint insertPosition, uint replaceCount, string xmlFileToImport, PartialImportOption onlineImportOption, CancellationToken ct = default)
    {
        PartialImportRungsCallCount++;
        PartialImportRungsOptions.Add(onlineImportOption);
        return Task.CompletedTask;
    }

    /// <summary>
    /// When non-<c>null</c>, <see cref="GoOnlineAsync"/> throws this exception
    /// instead of succeeding.
    /// </summary>
    public Exception? GoOnlineException { get; set; }

    /// <summary>Number of times <see cref="GoOnlineAsync"/> has been called.</summary>
    public int GoOnlineCallCount { get; private set; }

    /// <summary>
    /// When set to a non-negative value, <see cref="GoOnlineAsync"/>
    /// throws <see cref="GoOnlineException"/> only on that specific
    /// (zero-based) call index.  A value of <c>-1</c> (default) means throw on
    /// every call when <see cref="GoOnlineException"/> is non-null.
    /// Ignored when <see cref="GoOnlineFailCount"/> is set to a non-negative value.
    /// </summary>
    public int GoOnlineExceptionOnCallIndex { get; set; } = -1;

    /// <summary>
    /// When set to a non-negative value, <see cref="GoOnlineAsync"/>
    /// throws <see cref="GoOnlineException"/> on the first
    /// <see cref="GoOnlineFailCount"/> calls (zero-based indices 0 to
    /// <c>GoOnlineFailCount - 1</c>) and succeeds on all subsequent calls.
    /// A value of <c>-1</c> (default) disables this mode and defers to
    /// <see cref="GoOnlineExceptionOnCallIndex"/> for exception control.
    /// </summary>
    public int GoOnlineFailCount { get; set; } = -1;

    /// <summary>
    /// The <see cref="ConnectedState"/> returned by
    /// <see cref="ReadConnectedStateAsync"/>.  Defaults to
    /// <see cref="ConnectedState.Online"/>.
    /// </summary>
    public ConnectedState ConnectedStateResult { get; set; } = ConnectedState.Online;

    /// <summary>Number of times <see cref="ReadConnectedStateAsync"/> has been called.</summary>
    public int ReadConnectedStateCallCount { get; private set; }

    public Task GoOnlineAsync(CancellationToken ct = default)
    {
        var callIndex = GoOnlineCallCount;
        GoOnlineCallCount++;
        if (GoOnlineException is not null)
        {
            bool shouldThrow;
            if (GoOnlineFailCount >= 0)
                // Fail on the first GoOnlineFailCount calls; succeed afterwards.
                shouldThrow = callIndex < GoOnlineFailCount;
            else
                // Fall back to per-index control (-1 = always throw).
                shouldThrow = GoOnlineExceptionOnCallIndex < 0
                    || callIndex == GoOnlineExceptionOnCallIndex;
            if (shouldThrow)
                throw GoOnlineException;
        }
        return Task.CompletedTask;
    }

    /// <summary>Number of times <see cref="GoOfflineAsync"/> has been called.</summary>
    public int GoOfflineCallCount { get; private set; }

    public Task GoOfflineAsync(CancellationToken ct = default)
    {
        GoOfflineCallCount++;
        return Task.CompletedTask;
    }

    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default)
    {
        ReadConnectedStateCallCount++;
        return Task.FromResult(ConnectedStateResult);
    }

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

// ── LogixProjectSessionTests ──────────────────────────────────────────────────

/// <summary>
/// Unit tests for <see cref="LogixProjectSession"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Construction — initial <c>NotOpened</c> state</item>
///   <item><see cref="LogixProjectSession.IsOpen"/> / <see cref="LogixProjectSession.IsClosed"/> state flags</item>
///   <item><see cref="LogixProjectSession.GetProject"/> — structured errors for each non-Open state</item>
///   <item><see cref="LogixProjectSession.OpenAsync"/> — happy path, already-open guard, and already-closed guard</item>
///   <item><see cref="LogixProjectSession.Dispose"/> — calls <c>Dispose</c> on the inner handle, safe to call twice</item>
///   <item><see cref="LogixProjectSession.CloseAsync"/> — equivalent to <c>Dispose</c></item>
///   <item><see cref="LogixProjectSession.ExecuteAsync{T}"/> — state guard, result forwarding, <see cref="ObjectDisposedException"/> conversion</item>
///   <item><see cref="LogixProjectSession.ExecuteAsync(Func{ILogixProject, Task}, CancellationToken)"/> — non-generic overload</item>
/// </list>
/// </summary>
public class LogixProjectSessionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="LogixProjectSession"/> pre-wired with
    /// <paramref name="fake"/> so <see cref="LogixProjectSession.OpenAsync"/>
    /// returns the fake without touching the real SDK.
    /// </summary>
    private static LogixProjectSession SessionWithFake(FakeLogixProject fake) =>
        new((_path, _ct) => Task.FromResult<ILogixProject>(fake));

    /// <summary>
    /// Opens a new session backed by <paramref name="fake"/> and returns
    /// both so tests can assert on both.
    /// </summary>
    private static async Task<(LogixProjectSession Session, FakeLogixProject Fake)>
        OpenedSessionAsync()
    {
        var fake = new FakeLogixProject();
        var session = SessionWithFake(fake);
        await session.OpenAsync("dummy.ACD");
        return (session, fake);
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NewSession_IsNotOpen()
    {
        var session = new LogixProjectSession(
            (_p, _ct) => Task.FromResult<ILogixProject>(new FakeLogixProject()));

        Assert.False(session.IsOpen);
    }

    [Fact]
    public void Constructor_NewSession_IsNotClosed()
    {
        var session = new LogixProjectSession(
            (_p, _ct) => Task.FromResult<ILogixProject>(new FakeLogixProject()));

        Assert.False(session.IsClosed);
    }

    // ── GetProject — NotOpened state ─────────────────────────────────────────

    [Fact]
    public void GetProject_WhenNotOpened_ThrowsTagServiceException()
    {
        var session = SessionWithFake(new FakeLogixProject());

        var ex = Assert.Throws<TagServiceException>(() => session.GetProject());

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public void GetProject_WhenNotOpened_ErrorMessageMentionsOpenAsync()
    {
        var session = SessionWithFake(new FakeLogixProject());

        var ex = Assert.Throws<TagServiceException>(() => session.GetProject());

        // The message must be actionable — tell the caller what to do.
        Assert.Contains("ConnectAsync", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── GetProject — Closed state ─────────────────────────────────────────────

    [Fact]
    public async Task GetProject_WhenClosed_ThrowsTagServiceException()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        var ex = Assert.Throws<TagServiceException>(() => session.GetProject());

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task GetProject_WhenClosed_ErrorMessageMentionsReconnect()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        var ex = Assert.Throws<TagServiceException>(() => session.GetProject());

        Assert.Contains("closed", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetProject — Open state ───────────────────────────────────────────────

    [Fact]
    public async Task GetProject_WhenOpen_ReturnsILogixProject()
    {
        var (session, fake) = await OpenedSessionAsync();

        var project = session.GetProject();

        Assert.Same(fake, project);
    }

    // ── OpenAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_ValidPath_TransitionsToOpenState()
    {
        var session = SessionWithFake(new FakeLogixProject());

        await session.OpenAsync("dummy.ACD");

        Assert.True(session.IsOpen);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task OpenAsync_WhenAlreadyOpen_ThrowsInvalidOperationException()
    {
        var (session, _) = await OpenedSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.OpenAsync("dummy.ACD"));
    }

    [Fact]
    public async Task OpenAsync_WhenClosed_ThrowsTagServiceException_ConnectionFailed()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.OpenAsync("dummy.ACD"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task OpenAsync_WhenClosed_ErrorMessageMentionsNewSession()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.OpenAsync("dummy.ACD"));

        // Caller must be told to create a new session — not retry the same one.
        Assert.Contains("new", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_NullPath_ThrowsArgumentNullException()
    {
        var session = SessionWithFake(new FakeLogixProject());

        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException
        // for null (a subtype of ArgumentException).
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.OpenAsync(null!));
    }

    [Fact]
    public async Task OpenAsync_WhitespacePath_ThrowsArgumentException()
    {
        var session = SessionWithFake(new FakeLogixProject());

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.OpenAsync("   "));
    }

    [Fact]
    public async Task OpenAsync_FactoryThrows_SurfacesAsConnectionFailed()
    {
        var session = new LogixProjectSession(
            (_p, _ct) => Task.FromException<ILogixProject>(
                new Exception("gRPC channel failed")));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.OpenAsync("dummy.ACD"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
        Assert.Contains("gRPC channel failed", ex.Error.Message);
    }

    [Fact]
    public async Task OpenAsync_FactoryThrows_SessionRemainsInNotOpenedState()
    {
        var session = new LogixProjectSession(
            (_p, _ct) => Task.FromException<ILogixProject>(new Exception("fail")));

        try { await session.OpenAsync("dummy.ACD"); } catch { }

        Assert.False(session.IsOpen);
        Assert.False(session.IsClosed);
    }

    // ── CloseAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CloseAsync_WhenOpen_TransitionsToClosedState()
    {
        var (session, _) = await OpenedSessionAsync();

        await session.CloseAsync();

        Assert.False(session.IsOpen);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task CloseAsync_CallsDisposeOnInnerProject()
    {
        var (session, fake) = await OpenedSessionAsync();

        await session.CloseAsync();

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_WhenOpen_TransitionsToClosedState()
    {
        var (session, _) = await OpenedSessionAsync();

        session.Dispose();

        Assert.True(session.IsClosed);
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task Dispose_CallsDisposeOnInnerProject()
    {
        var (session, fake) = await OpenedSessionAsync();

        session.Dispose();

        Assert.Equal(1, fake.DisposeCallCount);
    }

    [Fact]
    public async Task Dispose_CalledTwice_DisposeOnInnerProjectCalledOnce()
    {
        var (session, fake) = await OpenedSessionAsync();

        session.Dispose();
        session.Dispose();   // Second call must be a no-op.

        Assert.Equal(1, fake.DisposeCallCount);
    }

    [Fact]
    public void Dispose_WhenNotOpened_DoesNotThrow()
    {
        var session = SessionWithFake(new FakeLogixProject());

        // Must not throw — safe to dispose before opening.
        session.Dispose();

        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task Dispose_WhenAlreadyClosed_DoesNotThrow()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        // Second dispose must be idempotent — no exception.
        session.Dispose();
    }

    // ── IsOpen / IsClosed state flags ─────────────────────────────────────────

    [Fact]
    public void IsOpen_WhenNotOpened_IsFalse()
    {
        var session = SessionWithFake(new FakeLogixProject());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task IsOpen_AfterOpenAsync_IsTrue()
    {
        var (session, _) = await OpenedSessionAsync();
        Assert.True(session.IsOpen);
    }

    [Fact]
    public async Task IsOpen_AfterDispose_IsFalse()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();
        Assert.False(session.IsOpen);
    }

    [Fact]
    public void IsClosed_WhenNotOpened_IsFalse()
    {
        var session = SessionWithFake(new FakeLogixProject());
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task IsClosed_AfterOpenAsync_IsFalse()
    {
        var (session, _) = await OpenedSessionAsync();
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task IsClosed_AfterDispose_IsTrue()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();
        Assert.True(session.IsClosed);
    }

    // ── ExecuteAsync<T> ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsyncT_WhenOpen_ReturnsOperationResult()
    {
        var (session, _) = await OpenedSessionAsync();

        var result = await session.ExecuteAsync(
            p => p.GetTagValueBOOLAsync("DummyTag",
                OperationMode.Offline));

        Assert.False(result);   // FakeLogixProject returns false
    }

    [Fact]
    public async Task ExecuteAsyncT_WhenNotOpened_ThrowsConnectionFailed()
    {
        var session = SessionWithFake(new FakeLogixProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p => p.GetTagValueBOOLAsync("Tag",
                OperationMode.Offline)));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ExecuteAsyncT_WhenClosed_ThrowsConnectionFailed()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p => p.GetTagValueDINTAsync("Tag",
                OperationMode.Offline)));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ExecuteAsyncT_ObjectDisposedException_IsConvertedToConnectionFailed()
    {
        var (session, fake) = await OpenedSessionAsync();
        fake.ObjectDisposedOnNextCall = true;

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p => p.GetTagValueBOOLAsync("Tag",
                OperationMode.Offline)));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ExecuteAsyncT_ObjectDisposedException_InnerExceptionIsPreserved()
    {
        var (session, fake) = await OpenedSessionAsync();
        fake.ObjectDisposedOnNextCall = true;

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p => p.GetTagValueBOOLAsync("Tag",
                OperationMode.Offline)));

        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    [Fact]
    public async Task ExecuteAsyncT_NullDelegate_ThrowsArgumentNullException()
    {
        var (session, _) = await OpenedSessionAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.ExecuteAsync<bool>(null!));
    }

    // ── ExecuteAsync (non-generic) ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenOpen_CompletesSuccessfully()
    {
        var (session, _) = await OpenedSessionAsync();

        // Must not throw.
        await session.ExecuteAsync(p =>
            p.SetCommunicationsPathAsync("192.0.2.1"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotOpened_ThrowsConnectionFailed()
    {
        var session = SessionWithFake(new FakeLogixProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p =>
                p.SetCommunicationsPathAsync("192.0.2.1")));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClosed_ThrowsConnectionFailed()
    {
        var (session, _) = await OpenedSessionAsync();
        session.Dispose();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p =>
                p.SetCommunicationsPathAsync("192.0.2.1")));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_ObjectDisposedException_IsConvertedToConnectionFailed()
    {
        var (session, fake) = await OpenedSessionAsync();
        fake.ObjectDisposedOnNextCall = true;

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p =>
                p.SetCommunicationsPathAsync("192.0.2.1")));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_ObjectDisposedException_InnerExceptionIsPreserved()
    {
        var (session, fake) = await OpenedSessionAsync();
        fake.ObjectDisposedOnNextCall = true;

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.ExecuteAsync(p =>
                p.SetCommunicationsPathAsync("192.0.2.1")));

        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    [Fact]
    public async Task ExecuteAsync_NullDelegate_ThrowsArgumentNullException()
    {
        var (session, _) = await OpenedSessionAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.ExecuteAsync((Func<ILogixProject, Task>)null!));
    }

    // ── Private-copy semantics ────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="LogixProjectSession"/> wired with
    /// <paramref name="fake"/> and a no-op copier, together with a recorder
    /// that captures every path passed to the factory.
    /// </summary>
    private static (LogixProjectSession Session, List<string> FactoryPaths)
        SessionWithFakeAndPathRecorder(FakeLogixProject fake)
    {
        var paths = new List<string>();
        var session = new LogixProjectSession(
            (path, _ct) =>
            {
                paths.Add(path);
                return Task.FromResult<ILogixProject>(fake);
            },
            static path => path);   // identity copier — no real file I/O
        return (session, paths);
    }

    [Fact]
    public async Task OpenAsync_WithCustomCopier_FactoryReceivesCopiedPath()
    {
        // Arrange: copier maps any path to a fixed sentinel.
        const string copiedPath = "/tmp/logix-mcp-work/aabbcc112233/Project.ACD";
        string? pathPassedToFactory = null;
        var fake = new FakeLogixProject();
        var session = new LogixProjectSession(
            (path, _ct) =>
            {
                pathPassedToFactory = path;
                return Task.FromResult<ILogixProject>(fake);
            },
            _ => copiedPath);

        // Act
        await session.OpenAsync("original.ACD");

        // Assert: the factory received the copy path, not the original.
        Assert.Equal(copiedPath, pathPassedToFactory);
    }

    [Fact]
    public async Task OpenAsync_WithCustomCopier_CopierReceivesOriginalPath()
    {
        const string originalPath = "original.ACD";
        string? pathPassedToCopier = null;
        var fake = new FakeLogixProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake),
            path =>
            {
                pathPassedToCopier = path;
                return "/tmp/copy.ACD";
            });

        await session.OpenAsync(originalPath);

        Assert.Equal(originalPath, pathPassedToCopier);
    }

    [Fact]
    public async Task OpenAsync_WithCustomCopier_OriginalPathNeverReachesFactory()
    {
        // Ensure the original path does NOT appear in what the factory sees.
        const string originalPath = "sensitive_original.ACD";
        const string copiedPath   = "/tmp/logix-mcp-work/slot/copy.ACD";
        string? pathPassedToFactory = null;
        var fake = new FakeLogixProject();
        var session = new LogixProjectSession(
            (path, _ct) =>
            {
                pathPassedToFactory = path;
                return Task.FromResult<ILogixProject>(fake);
            },
            _ => copiedPath);

        await session.OpenAsync(originalPath);

        Assert.NotEqual(originalPath, pathPassedToFactory);
    }

    [Fact]
    public async Task OpenAsync_CopierThrows_SurfacesAsConnectionFailed()
    {
        var fake = new FakeLogixProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake),
            _ => throw new IOException("disk full"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.OpenAsync("original.ACD"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task OpenAsync_CopierThrows_ErrorMessageMentionsCopyFailure()
    {
        var fake = new FakeLogixProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake),
            _ => throw new IOException("disk full"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => session.OpenAsync("original.ACD"));

        Assert.Contains("working copy", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_CopierThrows_SessionRemainsInNotOpenedState()
    {
        var fake = new FakeLogixProject();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake),
            _ => throw new IOException("disk full"));

        try { await session.OpenAsync("original.ACD"); } catch { }

        Assert.False(session.IsOpen);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task Dispose_AfterOpen_DeletesWorkingCopy()
    {
        // Create a real temp file so we can verify Dispose deletes it.
        var tempFile = Path.GetTempFileName();
        try
        {
            var fake = new FakeLogixProject();
            var session = new LogixProjectSession(
                (_path, _ct) => Task.FromResult<ILogixProject>(fake),
                _ => tempFile);   // copier returns the existing temp file

            await session.OpenAsync("original.ACD");
            Assert.True(File.Exists(tempFile), "pre-condition: file must exist");

            session.Dispose();

            Assert.False(File.Exists(tempFile), "working copy must be deleted on Dispose");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Dispose_AfterOpen_DeletesEmptySlotDirectory()
    {
        // Create a dedicated temp directory with a single file inside.
        var slotDir  = Path.Combine(Path.GetTempPath(),
            $"logix-mcp-test-{Guid.NewGuid():N}");
        var tempFile = Path.Combine(slotDir, "Project.ACD");
        Directory.CreateDirectory(slotDir);
        File.WriteAllText(tempFile, "dummy");

        try
        {
            var fake = new FakeLogixProject();
            var session = new LogixProjectSession(
                (_path, _ct) => Task.FromResult<ILogixProject>(fake),
                _ => tempFile);

            await session.OpenAsync("original.ACD");
            session.Dispose();

            Assert.False(Directory.Exists(slotDir),
                "empty slot directory must be pruned on Dispose");
        }
        finally
        {
            if (Directory.Exists(slotDir))
                Directory.Delete(slotDir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_AfterOpen_PreservesNonEmptySlotDirectory()
    {
        // If the slot directory still holds other files, it must NOT be deleted.
        var slotDir   = Path.Combine(Path.GetTempPath(),
            $"logix-mcp-test-{Guid.NewGuid():N}");
        var tempFile  = Path.Combine(slotDir, "Project.ACD");
        var otherFile = Path.Combine(slotDir, "other.tmp");
        Directory.CreateDirectory(slotDir);
        File.WriteAllText(tempFile, "dummy");
        File.WriteAllText(otherFile, "other");

        try
        {
            var fake = new FakeLogixProject();
            var session = new LogixProjectSession(
                (_path, _ct) => Task.FromResult<ILogixProject>(fake),
                _ => tempFile);

            await session.OpenAsync("original.ACD");
            session.Dispose();

            Assert.True(Directory.Exists(slotDir),
                "non-empty slot directory must be preserved");
        }
        finally
        {
            if (Directory.Exists(slotDir))
                Directory.Delete(slotDir, recursive: true);
        }
    }

    // ── ComputeWorkingCopyPath ────────────────────────────────────────────────

    [Fact]
    public void ComputeWorkingCopyPath_IsDeterministic()
    {
        var path1 = LogixProjectSession.ComputeWorkingCopyPath("/projects/MyProject.ACD");
        var path2 = LogixProjectSession.ComputeWorkingCopyPath("/projects/MyProject.ACD");
        Assert.Equal(path1, path2);
    }

    [Fact]
    public void ComputeWorkingCopyPath_DifferentSourcePaths_DifferentSlots()
    {
        var path1 = LogixProjectSession.ComputeWorkingCopyPath("/projects/ProjectA.ACD");
        var path2 = LogixProjectSession.ComputeWorkingCopyPath("/projects/ProjectB.ACD");

        // The slot component (directory name) must differ.
        var slot1 = Path.GetFileName(Path.GetDirectoryName(path1))!;
        var slot2 = Path.GetFileName(Path.GetDirectoryName(path2))!;
        Assert.NotEqual(slot1, slot2);
    }

    [Fact]
    public void ComputeWorkingCopyPath_PreservesOriginalFilename()
    {
        var result = LogixProjectSession.ComputeWorkingCopyPath("/projects/MyProject.ACD");
        Assert.Equal("MyProject.ACD", Path.GetFileName(result));
    }

    [Fact]
    public void ComputeWorkingCopyPath_ResultIsUnderWorkingDirectoryBase()
    {
        var result = LogixProjectSession.ComputeWorkingCopyPath("/projects/MyProject.ACD");
        Assert.StartsWith(LogixProjectSession.WorkingDirectoryBase, result,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeWorkingCopyPath_SlotIsHexString()
    {
        var result = LogixProjectSession.ComputeWorkingCopyPath("/projects/MyProject.ACD");
        var slot   = Path.GetFileName(Path.GetDirectoryName(result))!;

        Assert.True(slot.All(c => "0123456789abcdef".Contains(c)),
            $"Slot '{slot}' must be lowercase hexadecimal.");
        Assert.Equal(12, slot.Length);
    }
}
#endif
