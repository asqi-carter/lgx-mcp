#if LOGIX_SDK
using System;
using System.Threading;
using System.Threading.Tasks;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;

// L5Sharp.Core defines its own Task type — alias System.Threading.Tasks.Task
// to avoid the ambiguous-reference error.
using Task = System.Threading.Tasks.Task;

using ConnectedState = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="SdkConnectFactory.CreateAsync"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item>Offline mode — <c>GoOnlineAsync</c> is never called</item>
///   <item>Online mode, success — <c>GoOnlineAsync</c> called, state verified as <c>Online</c></item>
///   <item>Online mode, <c>GoOnlineAsync</c> throws — surfaces as <see cref="TagServiceException"/>(<see cref="TagServiceErrorKind.ConnectionFailed"/>)</item>
///   <item>Online mode, <c>ReadConnectedStateAsync</c> returns non-<c>Online</c> state — surfaces as <see cref="TagServiceException"/>(<see cref="TagServiceErrorKind.ConnectionFailed"/>)</item>
///   <item>Online mode, error path — session is disposed before the exception propagates</item>
/// </list>
/// </summary>
public class SdkConnectFactoryTests : IDisposable
{

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();
    // ── Minimal L5X project (no tags needed for factory-level tests) ──────────

    private const string MinimalL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="FactoryTestController" TargetType="Controller" ContainsContext="false">
          <Controller Name="FactoryTestController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags />
            <Programs />
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    private static L5X MinimalTypeProject() => L5X.Parse(MinimalL5Xml);

    // ── Factory / session helpers ─────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="SdkConnectFactory"/> whose session uses
    /// <paramref name="fake"/> as the underlying project handle.
    /// </summary>
    private SdkConnectFactory FactoryWithFake(FakeLogixProject fake)
    {
        return new SdkConnectFactory(
            () => new LogixProjectSession(
                (_path, _ct) => Task.FromResult<ILogixProject>(fake)),
                lease: _leaseScope.Lease);
    }

    // ── Offline mode ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_OfflineMode_ReturnsService()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline");

        Assert.NotNull(result.Service);
    }

    [Fact]
    public async Task CreateAsync_OfflineMode_DoesNotCallGoOnlineAsync()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline");

        Assert.Equal(0, fake.GoOnlineCallCount);
    }

    [Fact]
    public async Task CreateAsync_OfflineMode_DoesNotCallReadConnectedStateAsync()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline");

        Assert.Equal(0, fake.ReadConnectedStateCallCount);
    }

    // ── Online mode — success path ────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_OnlineMode_ReturnsService()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.NotNull(result.Service);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_CallsGoOnlineAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal(1, fake.GoOnlineCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_CallsReadConnectedStateAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal(1, fake.ReadConnectedStateCallCount);
    }

    [Theory]
    [InlineData("online")]
    [InlineData("ONLINE")]
    [InlineData("Online")]
    public async Task CreateAsync_OnlineModeVariousCase_CallsGoOnlineAsync(string sdkMode)
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: sdkMode);

        Assert.Equal(1, fake.GoOnlineCallCount);
    }

    // ── Online mode — GoOnlineAsync failure ───────────────────────────────────

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineAsyncThrows_ThrowsConnectionFailed()
    {
        var fake    = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("controller not reachable")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineAsyncThrows_ErrorMessageContainsCause()
    {
        var fake    = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("controller not reachable")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains("GoOnlineAsync", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("controller not reachable", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineAsyncThrows_SessionIsDisposed()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("controller not reachable")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── Online mode — ReadConnectedStateAsync returns non-Online ─────────────

    [Theory]
    [InlineData(ConnectedState.Unknown)]
    [InlineData(ConnectedState.Offline)]
    [InlineData(ConnectedState.Connected)]
    public async Task CreateAsync_OnlineMode_NonOnlineState_ThrowsConnectionFailed(
        ConnectedState reportedState)
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = reportedState };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Theory]
    [InlineData(ConnectedState.Unknown)]
    [InlineData(ConnectedState.Offline)]
    [InlineData(ConnectedState.Connected)]
    public async Task CreateAsync_OnlineMode_NonOnlineState_ErrorMessageNamesActualState(
        ConnectedState reportedState)
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = reportedState };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains(reportedState.ToString(), ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ConnectedState.Unknown)]
    [InlineData(ConnectedState.Offline)]
    [InlineData(ConnectedState.Connected)]
    public async Task CreateAsync_OnlineMode_NonOnlineState_SessionIsDisposed(
        ConnectedState reportedState)
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = reportedState };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── Online mode — IMAGES_INCOMPATIBLE (mismatched project) ───────────────

    [Fact]
    public async Task CreateAsync_OnlineMode_ImagesIncompatible_ThrowsImagesIncompatibleKind()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ImagesIncompatible, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_ImagesIncompatible_ErrorMessageMentionsMismatch()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains("different project", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_ImagesIncompatible_ErrorMessageIncludesDownloadRemediation()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        // Remediation step 1: download the open project to the controller.
        Assert.Contains("download", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_ImagesIncompatible_ErrorMessageIncludesOpenControllerProjectRemediation()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        // Remediation step 2: open the project already in the controller.
        Assert.Contains("controller", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_ImagesIncompatible_SessionIsDisposed()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_NonImagesIncompatibleException_ThrowsConnectionFailed()
    {
        // An unrelated GoOnlineAsync failure should still surface as ConnectionFailed.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("network timeout")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── Online mode — CANNOT_UPLOAD_PHYS_ADDR (address correlation failure) ──

    [Fact]
    public async Task CreateAsync_OnlineMode_CannotUploadPhysAddr_ThrowsImagesIncompatibleKind()
    {
        // CANNOT_UPLOAD_PHYS_ADDR is treated as ImagesIncompatible because both
        // require a download to proceed to ONLINE.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated with open project.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ImagesIncompatible, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_CannotUploadPhysAddr_ErrorMessageMentionsDownload()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated with open project.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains("download", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_CannotUploadPhysAddr_SessionIsDisposed()
    {
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated with open project.")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_CannotUploadPhysAddr_NoDownloadTriggered()
    {
        // No download must occur when allowControllerDownload is false (default),
        // even when GoOnlineAsync throws CANNOT_UPLOAD_PHYS_ADDR.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated with open project.")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(0, fake.DownloadCallCount);
    }

    // ── Online mode — operation-first classifier (RxE_COMM_DTLERROR on file path) ──
    //
    // When GoOnlineAsync throws an RxE_COMM_* comm-layer error on the file-opened
    // project path (CreateAsync), SdkCommErrorClassifier intercepts it before the
    // comm-layer discriminator runs and surfaces ProjectNotCorrelated immediately.
    // The TCP probe is not invoked; rung 2 (upload) is not attempted.

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommDtlerror_ThrowsProjectNotCorrelated()
    {
        // GoOnlineAsync throws RxE_COMM_DTLERROR on the file-based path →
        // must surface as ProjectNotCorrelated, not ConnectionFailed or SdkCommLayerFaulted.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxE_COMM_DTLERROR - comm layer faulted")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("C:\\Projects\\MyController.ACD",
                MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ProjectNotCorrelated, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommDtlerror_ErrorMessageNamesProjectFile()
    {
        // The error message must name the file that was opened so the operator can
        // identify the misconfigured path.
        const string projectPath = "C:\\Projects\\MyController.ACD";
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync(projectPath, MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains(projectPath, ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommDtlerror_ErrorMessageNamesCommPathRoute()
    {
        // The error must name the comm-path/upload route as the correct alternative,
        // so the operator knows what to do next.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains("comm-path", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommDtlerror_SessionIsDisposed()
    {
        // The session must be disposed when ProjectNotCorrelated is thrown to avoid
        // leaking the gRPC channel.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommDtlerror_UploadNotAttempted()
    {
        // The classifier fires before rung 2 — UploadAsync must NOT be called.
        // This also confirms the TCP probe is not invoked (CreateAsync has no TCP probe path).
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(0, fake.UploadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommDtlerror_InnerExceptionIsOriginalSdkException()
    {
        // The original SDK exception must be preserved as InnerException for diagnostics.
        var sdkEx  = new InvalidOperationException("RxE_COMM_DTLERROR - details");
        var fake   = new FakeLogixProject { GoOnlineException = sdkEx };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Same(sdkEx, ex.InnerException);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRxeCommOther_ThrowsProjectNotCorrelated()
    {
        // The entire RxE_COMM_* family (not just DTLERROR) must resolve to
        // ProjectNotCorrelated on the file-based path — verifies the prefix
        // matcher covers all family members.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_OTHER error code")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ProjectNotCorrelated, ex.Error.Kind);
    }

    // ── allowControllerDownload=true — go_online succeeds on first try ────────
    //
    // When GoOnlineAsync succeeds immediately (controller already holds a
    // matching image), the download sequence is skipped entirely.

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_ReturnsService()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.NotNull(result.Service);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFirstSucceeds_SkipsDownload()
    {
        // GoOnline succeeds on the first attempt — download must be skipped.
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFirstSucceeds_NoModeChanges()
    {
        // GoOnline succeeds on the first attempt — no PROGRAM/RUN mode transitions.
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Empty(fake.ChangeControllerModeCalls);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFirstSucceeds_GoOnlineCalledOnce()
    {
        // GoOnline succeeds on the first attempt — no second GoOnline call needed.
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(1, fake.GoOnlineCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_CallsGoOnlineAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(1, fake.GoOnlineCallCount);
    }

    // ── allowControllerDownload=true — cancellation during go_online probe ──────
    //
    // When an OperationCanceledException is thrown from GoOnlineAsync during the
    // Step-0 probe, it must propagate immediately rather than being swallowed by
    // the bare catch {} that exists to handle SDK-level incompatibility errors.
    // Swallowing it would cause the factory to fall through to
    // ChangeControllerModeAsync(Program) — sending an unintended PROGRAM mode
    // command to the controller before the cancellation is detected, which
    // violates the non-destructive requirement.

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_CancellationDuringProbe_ThrowsOperationCancelled()
    {
        // Configure GoOnlineAsync to throw OperationCanceledException on the
        // first call (index 0), simulating a cancellation arriving during the
        // Step-0 probe.  The second call (fallback path) is never reached.
        var fake = new FakeLogixProject
        {
            GoOnlineException            = new OperationCanceledException(
                "cancelled during GoOnline probe"),
            GoOnlineExceptionOnCallIndex = 0,
            ConnectedStateResult         = ConnectedState.Online,
        };
        var factory = FactoryWithFake(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true));
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_CancellationDuringProbe_NoModeChanges()
    {
        // When the probe throws OperationCanceledException, the factory must NOT
        // call ChangeControllerModeAsync(Program) — the PLC scan must not be
        // stopped as a side-effect of a cancelled connection probe.
        var fake = new FakeLogixProject
        {
            GoOnlineException            = new OperationCanceledException(
                "cancelled during GoOnline probe"),
            GoOnlineExceptionOnCallIndex = 0,
            ConnectedStateResult         = ConnectedState.Online,
        };
        var factory = FactoryWithFake(fake);

        try
        {
            await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true);
        }
        catch (OperationCanceledException) { }

        Assert.Empty(fake.ChangeControllerModeCalls);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_CancellationDuringProbe_SessionIsDisposed()
    {
        // The session must be disposed when the probe throws
        // OperationCanceledException to avoid leaking the gRPC channel.
        var fake = new FakeLogixProject
        {
            GoOnlineException            = new OperationCanceledException(
                "cancelled during GoOnline probe"),
            GoOnlineExceptionOnCallIndex = 0,
            ConnectedStateResult         = ConnectedState.Online,
        };
        var factory = FactoryWithFake(fake);

        try
        {
            await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true);
        }
        catch (OperationCanceledException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── Rung 2 — upload→GoOnline succeeds (normal uncorrelated case) ─────────
    //
    // When rung 1 GoOnline fails but UploadAsync succeeds and the subsequent
    // GoOnline also succeeds, the session is established via rung 2 without any
    // controller-side modification.  This path is available regardless of
    // allowControllerDownload because upload is non-destructive to the controller.

    /// <summary>
    /// Returns a <see cref="FakeLogixProject"/> configured so that
    /// <see cref="FakeLogixProject.GoOnlineAsync"/> throws only on the first call
    /// (rung 1 probe) but succeeds on the second call (rung 2 after upload).
    /// </summary>
    private static FakeLogixProject FakeRequiringRung2() =>
        new()
        {
            ConnectedStateResult         = ConnectedState.Online,
            GoOnlineException            = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project."),
            GoOnlineExceptionOnCallIndex = 0,   // only rung-1 call (index 0) fails
        };

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_ReturnsService()
    {
        // Rung 1 GoOnline fails; rung 2 (upload→GoOnline) succeeds → service returned.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.NotNull(result.Service);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_UploadCalled()
    {
        // Upload must be called once during rung 2.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal(1, fake.UploadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_GoOnlineCalledTwice()
    {
        // GoOnline is called twice: once in rung 1 (fails) and once in rung 2
        // (after upload, succeeds).
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal(2, fake.GoOnlineCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_NoDownload()
    {
        // Rung 2 is non-destructive — DownloadAsync must not be called.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_NoModeTransitions()
    {
        // Rung 2 does not alter the controller mode.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Empty(fake.ChangeControllerModeCalls);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_DownloadPerformed_IsFalse()
    {
        // DownloadPerformed must be false since no download was executed.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.False(result.DownloadPerformed);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_ModeTransitions_IsEmpty()
    {
        // No controller mode transitions occur during rung 2.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Empty(result.ModeTransitions);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_Rung2_SkipsRung3()
    {
        // Even when allowControllerDownload=true, if rung 2 succeeds then rung 3
        // (the download) must be skipped entirely.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(0, fake.DownloadCallCount);
        Assert.Empty(fake.ChangeControllerModeCalls);
    }

    // ── Rung 2 — upload called on working copy, not source file ──────────────
    //
    // UploadAsync overwrites the working copy (server-managed private copy) that
    // was already opened by LogixProjectSession.  The engineer's source file is
    // never affected because the session always operates against the private copy.
    // This is verified at the session level (LogixProjectSession private-copy
    // semantics); at the factory level we verify that UploadAsync is called
    // exactly once on the session's ILogixProject handle.

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_UploadCalledExactlyOnce()
    {
        // Only one upload should occur during a single rung-2 attempt.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal(1, fake.UploadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_SourceAcdUnchangedAfterUpload()
    {
        // ── Private-copy integrity — upload rung must not touch the engineer's source file ──
        //
        // UploadAsync is intentionally destructive to the working copy — the real
        // SDK overwrites it with the controller's current program image.  This test
        // verifies that the private-copy mechanism (LogixProjectSession's copier)
        // ensures UploadAsync only touches the server-managed copy, never the path
        // the engineer originally supplied.
        //
        // Setup:
        //   • A real temp file represents the engineer's source ACD.
        //   • A real file copy is placed in a temp working directory.
        //   • The UploadAction on the fake project writes different bytes to the
        //     working copy, simulating the SDK overwriting it with controller image.
        //   • After a rung-2 online session the source file's SHA-256 must match
        //     the hash recorded before the session began.

        var srcDir = Path.Combine(Path.GetTempPath(),
            $"logix-mcp-test-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var sourcePath = Path.Combine(srcDir, "Engineer.ACD");

        var workDir = Path.Combine(Path.GetTempPath(),
            $"logix-mcp-test-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var workCopyPath = Path.Combine(workDir, "Engineer.ACD");

        try
        {
            // Write distinct content to the source file and snapshot its hash.
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(
                "Original ACD content — engineer source file");
            File.WriteAllBytes(sourcePath, originalBytes);
            var beforeHash = System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(sourcePath));

            // Bytes that the simulated upload writes to the working copy (must differ
            // from originalBytes to prove we are testing the right thing).
            var controllerImageBytes = System.Text.Encoding.UTF8.GetBytes(
                "Controller image — overwrites working copy, not source");
            Assert.False(originalBytes.SequenceEqual(controllerImageBytes),
                "pre-condition: source and controller bytes must differ");

            // Rung-2 scenario: GoOnline fails on the first call (index 0), upload
            // succeeds (and overwrites the working copy), then GoOnline succeeds.
            var fake = new FakeLogixProject
            {
                ConnectedStateResult         = ConnectedState.Online,
                GoOnlineException            = new InvalidOperationException(
                    "RxCL_E_IMAGES_INCOMPATIBLE"),
                GoOnlineExceptionOnCallIndex = 0,
                UploadAction                 = () =>
                    File.WriteAllBytes(workCopyPath, controllerImageBytes),
            };

            var factory = new SdkConnectFactory(
                () => new LogixProjectSession(
                    (path, ct) => Task.FromResult<ILogixProject>(fake),
                    src =>
                    {
                        // Real copier: physically copy source to working location.
                        File.Copy(src, workCopyPath, overwrite: true);
                        return workCopyPath;
                    }),
                    lease: _leaseScope.Lease);

            _ = await factory.CreateAsync(sourcePath, MinimalTypeProject(),
                sdkMode: "online");

            // ── Assertions ────────────────────────────────────────────────────
            // 1. The upload actually ran (working copy content changed).
            var workCopyAfter = File.Exists(workCopyPath) ? File.ReadAllBytes(workCopyPath) : null;
            Assert.NotNull(workCopyAfter);
            Assert.True(controllerImageBytes.SequenceEqual(workCopyAfter),
                "working copy must contain controller image bytes after upload");

            // 2. The source file is byte-for-byte identical to what it was before.
            var afterHash = System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(sourcePath));
            Assert.Equal(beforeHash, afterHash);
        }
        finally
        {
            if (Directory.Exists(srcDir))  Directory.Delete(srcDir,  recursive: true);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ── allowControllerDownload=true — go_online fails, fallback to download ──
    //
    // When both the initial GoOnlineAsync (rung 1) and the upload→GoOnlineAsync
    // (rung 2) fail, the factory falls back to the rung 3
    // PROGRAM→download→go_online→RUN sequence.

    /// <summary>
    /// Returns a <see cref="FakeLogixProject"/> configured so that
    /// <see cref="FakeLogixProject.GoOnlineAsync"/> throws on the first two calls
    /// (rung 1 and rung 2 of the correlation ladder) but succeeds on the third
    /// call (after a download corrects the controller image in rung 3).
    /// </summary>
    private static FakeLogixProject FakeWithFirstTwoGoOnlineFailures() =>
        new()
        {
            ConnectedStateResult = ConnectedState.Online,
            GoOnlineException    = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project."),
            GoOnlineFailCount    = 2,   // fails on calls 0 (rung 1) and 1 (rung 2)
        };

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFallback_ReturnsService()
    {
        // When GoOnline fails on first try, the download sequence runs and the
        // factory still returns a valid service after the second GoOnline succeeds.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.NotNull(result.Service);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFallback_GoOnlineCalledThreeTimes()
    {
        // GoOnline is attempted three times across the three-rung ladder:
        // call 0 = rung 1 probe (fails), call 1 = rung 2 after upload (fails),
        // call 2 = rung 3 after download (succeeds).
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(3, fake.GoOnlineCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFallback_DownloadCalled()
    {
        // After the initial GoOnline fails, DownloadAsync must be called.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(1, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_ChangesModeToProgram()
    {
        // After the initial GoOnline fails, the download sequence puts the
        // controller into PROGRAM mode before downloading.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Contains(
            RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode.Program,
            fake.ChangeControllerModeCalls);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_CallsDownloadAsync()
    {
        // After the initial GoOnline fails, DownloadAsync is called.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(1, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_ChangesModeToRunAfterOnline()
    {
        // After download→GoOnline, the controller is returned to RUN mode.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Contains(
            RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode.Run,
            fake.ChangeControllerModeCalls);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_ModeSequenceIsProgramThenRun()
    {
        // Verifies the sequence in the fallback path: PROGRAM first, then RUN last.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(2, fake.ChangeControllerModeCalls.Count);
        Assert.Equal(
            RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode.Program,
            fake.ChangeControllerModeCalls[0]);
        Assert.Equal(
            RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode.Run,
            fake.ChangeControllerModeCalls[1]);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_DownloadBeforeThirdGoOnline()
    {
        // Verifies the three-rung ladder: GoOnline(fail, rung1) → Upload →
        // GoOnline(fail, rung2) → Program → Download → GoOnline(success, rung3)
        // → ReadState → Run.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        // After the full ladder: 1 download, 3 GoOnline calls.
        Assert.Equal(1, fake.DownloadCallCount);
        Assert.Equal(3, fake.GoOnlineCallCount);
    }

    // ── allowControllerDownload=false — no extra calls ────────────────────────

    [Fact]
    public async Task CreateAsync_OnlineMode_NoDownload_DoesNotCallChangeControllerModeAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: false);

        Assert.Empty(fake.ChangeControllerModeCalls);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_NoDownload_DoesNotCallDownloadAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: false);

        Assert.Equal(0, fake.DownloadCallCount);
    }

    [Fact]
    public async Task CreateAsync_OfflineMode_AllowDownloadIgnored_NoChangeControllerModeCalls()
    {
        // allowControllerDownload is ignored in offline mode.
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline", allowControllerDownload: true);

        Assert.Empty(fake.ChangeControllerModeCalls);
        Assert.Equal(0, fake.DownloadCallCount);
    }

    // ── allowControllerDownload=true — ChangeControllerModeAsync(Program) fails
    //
    // These tests require both rung 1 (GoOnline) and rung 2 (upload→GoOnline) to
    // fail so the factory enters rung 3 where ChangeControllerModeAsync(Program)
    // is called.  GoOnlineFailCount=2 causes GoOnline to fail on calls 0 (rung 1)
    // and 1 (rung 2), and succeed on call 2 (rung 3 post-download) — but
    // ChangeControllerModeAsync(Program) throws before the download can run.

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_ProgramModeThrows_ThrowsConnectionFailed()
    {
        // Rungs 1 and 2 both fail → factory enters rung 3 (download path).
        // ChangeControllerModeAsync(Program) then throws → ConnectionFailed.
        var fake = new FakeLogixProject
        {
            ConnectedStateResult          = ConnectedState.Online,
            GoOnlineException             = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount             = 2,   // fails on rung-1 (call 0) and rung-2 (call 1)
            ChangeControllerModeException = new InvalidOperationException("cannot set Program mode"),
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_ProgramModeThrows_SessionIsDisposed()
    {
        var fake = new FakeLogixProject
        {
            ConnectedStateResult          = ConnectedState.Online,
            GoOnlineException             = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount             = 2,
            ChangeControllerModeException = new InvalidOperationException("cannot set Program mode"),
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── allowControllerDownload=true — DownloadAsync fails ────────────────────
    //
    // Rungs 1 and 2 both fail (GoOnlineFailCount=2) → factory enters rung 3.
    // ChangeControllerModeAsync(Program) succeeds; DownloadAsync then throws.

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_DownloadThrows_ThrowsConnectionFailed()
    {
        var fake = new FakeLogixProject
        {
            ConnectedStateResult  = ConnectedState.Online,
            GoOnlineException     = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount     = 2,   // fails on rung-1 (call 0) and rung-2 (call 1)
            DownloadException     = new InvalidOperationException("download rejected"),
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_DownloadThrows_ErrorMessageContainsCause()
    {
        var fake = new FakeLogixProject
        {
            ConnectedStateResult  = ConnectedState.Online,
            GoOnlineException     = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount     = 2,
            DownloadException     = new InvalidOperationException("download rejected"),
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true));

        Assert.Contains("DownloadAsync", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("download rejected", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_DownloadThrows_SessionIsDisposed()
    {
        var fake = new FakeLogixProject
        {
            ConnectedStateResult  = ConnectedState.Online,
            GoOnlineException     = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount     = 2,
            DownloadException     = new InvalidOperationException("download rejected"),
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── allowControllerDownload=true — ChangeControllerModeAsync(Run) fails ───
    //
    // Rungs 1 and 2 both fail (GoOnlineFailCount=2) → rung 3 download sequence
    // runs → ChangeControllerModeAsync(Program) succeeds (index 0) →
    // ChangeControllerModeAsync(Run) fails (index 1).

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_RunModeThrows_ThrowsConnectionFailed()
    {
        // ChangeControllerModeAsync throws only on the second call (Run mode, index 1).
        // GoOnlineFailCount=2 forces entry into rung 3 (download path).
        var fake = new FakeLogixProject
        {
            ConnectedStateResult                     = ConnectedState.Online,
            GoOnlineException                        = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount                        = 2,   // fails on rung-1 and rung-2
            ChangeControllerModeException            = new InvalidOperationException("cannot set Run mode"),
            ChangeControllerModeExceptionOnCallIndex = 1,   // 0=Program succeeds, 1=Run fails
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_RunModeThrows_ErrorMessageMentionsRunMode()
    {
        var fake = new FakeLogixProject
        {
            ConnectedStateResult                     = ConnectedState.Online,
            GoOnlineException                        = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount                        = 2,
            ChangeControllerModeException            = new InvalidOperationException("cannot set Run mode"),
            ChangeControllerModeExceptionOnCallIndex = 1,
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: true));

        Assert.Contains("ChangeControllerModeAsync(Run)", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_RunModeThrows_SessionIsDisposed()
    {
        var fake = new FakeLogixProject
        {
            ConnectedStateResult                     = ConnectedState.Online,
            GoOnlineException                        = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            GoOnlineFailCount                        = 2,
            ChangeControllerModeException            = new InvalidOperationException("cannot set Run mode"),
            ChangeControllerModeExceptionOnCallIndex = 1,
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── SdkSessionResult metadata — DownloadPerformed ────────────────────────

    [Fact]
    public async Task CreateAsync_OfflineMode_DownloadPerformed_IsFalse()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline");

        Assert.False(result.DownloadPerformed);
    }

    [Fact]
    public async Task CreateAsync_OfflineMode_ModeTransitions_IsEmpty()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline");

        Assert.Empty(result.ModeTransitions);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_NoDownload_DownloadPerformed_IsFalse()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: false);

        Assert.False(result.DownloadPerformed);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_NoDownload_ModeTransitions_IsEmpty()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: false);

        Assert.Empty(result.ModeTransitions);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFirstSucceeds_DownloadPerformed_IsFalse()
    {
        // GoOnline succeeds on probe — download is skipped; DownloadPerformed must be false.
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.False(result.DownloadPerformed);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFirstSucceeds_ModeTransitions_IsEmpty()
    {
        // GoOnline succeeds on probe — no PROGRAM/RUN mode transitions performed.
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Empty(result.ModeTransitions);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFallback_DownloadPerformed_IsTrue()
    {
        // GoOnline fails on probe → download sequence runs → DownloadPerformed must be true.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.True(result.DownloadPerformed);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_AllowDownload_GoOnlineFallback_ModeTransitions_IsProgramThenRun()
    {
        // GoOnline fails on probe → download sequence: PROGRAM then RUN.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(new[] { "Program", "Run" }, result.ModeTransitions);
    }

    // ── SdkSessionResult metadata — CorrelationPath ──────────────────────────

    [Fact]
    public async Task CreateAsync_OfflineMode_CorrelationPath_IsNull()
    {
        // Offline sessions have no correlation concept — CorrelationPath must be null.
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "offline");

        Assert.Null(result.CorrelationPath);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung1Succeeds_CorrelationPath_IsAlreadyCorrelated()
    {
        // GoOnlineAsync succeeds on the first call (rung 1) — the controller already
        // holds a matching project image.  CorrelationPath must be "already-correlated".
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal("already-correlated", result.CorrelationPath);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2Succeeds_CorrelationPath_IsUpload()
    {
        // Rung 1 GoOnline fails; rung 2 (upload → GoOnline) succeeds.
        // CorrelationPath must be "upload" to indicate the non-destructive upload path.
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online");

        Assert.Equal("upload", result.CorrelationPath);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung3Succeeds_CorrelationPath_IsDownload()
    {
        // Rungs 1 and 2 both fail → rung 3 (download) is executed.
        // CorrelationPath must be "download" to signal the destructive download path.
        var fake    = FakeWithFirstTwoGoOnlineFailures();
        var factory = FactoryWithFake(fake);

        var result = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal("download", result.CorrelationPath);
    }

    // ── CreateFromControllerAsync — no-project-file entry point ──────────────
    //
    // These tests use the full-control constructor to inject fakes for all three
    // production delegates:
    //   • controllerUploadFactory  → replaces LogixProject.UploadToNewProjectAsync
    //   • saveAsFactory            → replaces ILogixProject.SaveAsAsync
    //   • l5xLoader                → replaces L5X.Load
    //
    // The internal LogixProjectSession is created by CreateFromControllerAsync
    // using the uploaded handle as its project factory, so GoOnlineAsync and
    // ReadConnectedStateAsync route through the FakeLogixProject instance returned
    // by controllerUploadFactory.

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the minimal L5X string used as a canned parse result for
    /// <c>l5xLoader</c> in <see cref="CreateFromControllerAsync"/> tests.
    /// </summary>
    private static L5X CannedL5X() => MinimalTypeProject();

    /// <summary>
    /// Builds a <see cref="SdkConnectFactory"/> for testing
    /// <see cref="SdkConnectFactory.CreateFromControllerAsync"/> by injecting
    /// a fake <c>controllerUploadFactory</c> that returns
    /// <paramref name="uploadedHandle"/>, a no-op <c>saveAsFactory</c>, and a
    /// <c>l5xLoader</c> that returns a canned L5X.
    /// </summary>
    private SdkConnectFactory FactoryForUpload(ILogixProject uploadedHandle)
        => new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploadedHandle),
            saveAsFactory:            (_, _, _) => Task.CompletedTask,
            l5xLoader:                _          => CannedL5X(),
            lease: _leaseScope.Lease);

    /// <summary>
    /// Builds a factory that throws <paramref name="ex"/> from
    /// <c>controllerUploadFactory</c> to simulate an upload failure.
    /// </summary>
    private SdkConnectFactory FactoryWithUploadFailure(Exception ex)
        => new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromException<ILogixProject>(ex),
            saveAsFactory:            (_, _, _) => Task.CompletedTask,
            l5xLoader:                _          => CannedL5X(),
            lease: _leaseScope.Lease);

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_NullCommPath_ThrowsArgumentException()
    {
        var factory = FactoryForUpload(new FakeLogixProject { ConnectedStateResult = ConnectedState.Online });

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => factory.CreateFromControllerAsync(null!));
    }

    [Fact]
    public async Task CreateFromControllerAsync_WhiteSpaceCommPath_ThrowsArgumentException()
    {
        var factory = FactoryForUpload(new FakeLogixProject { ConnectedStateResult = ConnectedState.Online });

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => factory.CreateFromControllerAsync("   "));
    }

    // ── Upload failure ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_UploadFails_ThrowsConnectionFailed()
    {
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("Controller at '10.0.0.1' not found."));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UploadFails_ErrorMessageContainsCause()
    {
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("Controller at '10.0.0.1' not found."));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("UploadToNewProjectAsync", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10.0.0.1", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Upload failure — RxE_COMM_DTLERROR from UploadToNewProjectAsync ──────────
    //
    // When UploadToNewProjectAsync throws an RxE_COMM_* error on a parseable comm
    // path, CreateFromControllerAsync runs the TCP probe on port 44818.  Because the
    // default probe returns true the triage selects SdkServiceDegraded — NOT
    // ProjectNotCorrelated (that kind is reserved for the file-based CreateAsync path)
    // and not SdkCommLayerFaulted (which is never emitted from this code path).

    [Fact]
    public async Task CreateFromControllerAsync_UploadRxeCommDtlerror_ThrowsSdkServiceDegraded()
    {
        // UploadToNewProjectAsync throwing RxE_COMM_DTLERROR on a parseable comm path
        // (here "10.0.0.1") runs the TCP probe. The default probe returns true
        // (controller reachable), so the triage selects SdkServiceDegraded — NOT
        // ProjectNotCorrelated (which is reserved for the file-based CreateAsync path).
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UploadRxeCommDtlerror_NotProjectNotCorrelated()
    {
        // Explicitly verify the kind is NOT ProjectNotCorrelated — that kind is reserved
        // for the file-opened GoOnline path (CreateAsync), not the upload-from-controller path.
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.NotEqual(TagServiceErrorKind.ProjectNotCorrelated, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UploadRxeCommDtlerror_ErrorMessageMentionsCommPath()
    {
        // The error message must name the comm path so the operator can identify
        // which controller was being contacted when the comm layer faulted.
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("10.0.0.1", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SaveAs failure ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_SaveAsFails_ThrowsConnectionFailed()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploaded),
            saveAsFactory:            (_, _, _) =>
                Task.FromException(new InvalidOperationException("disk full")),
            l5xLoader:                _ => CannedL5X(),
            lease: _leaseScope.Lease);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAsFails_ErrorMessageContainsCause()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploaded),
            saveAsFactory:            (_, _, _) =>
                Task.FromException(new InvalidOperationException("disk full")),
            l5xLoader:                _ => CannedL5X(),
            lease: _leaseScope.Lease);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("L5X", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SaveAs failure — dead-session indicator (Project is not open.) ─────────
    //
    // "Project is not open." is a dead-service indicator, not a pending-edit
    // indicator (HARDWARE-VERIFIED.md 2026-09-06 "NOT REPRODUCED": upload connect
    // succeeded with three pending rungs in the controller).  It must be mapped to
    // ConnectionFailed via the SCM re-query path — not PendingEditsPresent.

    /// <summary>
    /// Builds a <see cref="SdkConnectFactory"/> whose <c>saveAsFactory</c>
    /// throws an exception with the given <paramref name="message"/>, simulating
    /// the SDK dead-session condition ("Project is not open.").  The delay
    /// is injected as a no-op so re-query loops complete instantly in tests.
    /// </summary>
    private SdkConnectFactory FactoryWithSaveAsFailure(string message)
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        return new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploaded),
            saveAsFactory:            (_, _, _) =>
                Task.FromException(new InvalidOperationException(message)),
            l5xLoader:                _ => CannedL5X(),
            delay:                    (_, _) => Task.CompletedTask,
            lease: _leaseScope.Lease);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_ProjectNotOpen_ThrowsConnectionFailed()
    {
        // "Project is not open." is a dead-service indicator — it must be mapped to
        // ConnectionFailed (not PendingEditsPresent) after the SCM re-query.
        var factory = FactoryWithSaveAsFailure("Project is not open.");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_ProjectNotOpen_ErrorMessageMentionsService()
    {
        // The message must name LdSdkService as the suspect — not controller
        // pending edits, which did not reproduce on hardware.
        var factory = FactoryWithSaveAsFailure("Project is not open.");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("LdSdkService", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_ProjectNotOpen_ErrorMessageQuotesRawSdkText()
    {
        // INVERTED 2026-08-04. This previously asserted the raw SDK text was HIDDEN.
        // Hiding it is what made this condition undiagnosable on the bench: the
        // wrapper asserted "the controller has uncommitted edits" while Logix
        // Designer showed "No Edits", and the evidence that would have exposed the
        // wrong guess — that "Project is not open." is a generic message with more
        // than one cause — had been thrown away. Quote the SDK verbatim so a reader
        // can tell our interpretation from the SDK's actual words.
        var factory = FactoryWithSaveAsFailure("Project is not open.");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("Project is not open.", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_ProjectNotOpen_ErrorMessageMentionsCommPath()
    {
        // The error message should identify the controller comm path so the user
        // knows which controller has the pending-edit condition.
        var factory = FactoryWithSaveAsFailure("Project is not open.");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("10.0.0.1", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_ProjectNotOpen_ErrorMessageMentionsRemediation()
    {
        // The message must name the service as the suspect and advise restarting it.
        // It must NOT present a two-cause hypothesis — pending edits did not reproduce.
        var factory = FactoryWithSaveAsFailure("Project is not open.");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        // Must name the service.
        Assert.Contains("LdSdkService", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        // Must advise restarting the service.
        Assert.Contains("restart", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        // Must NOT assert pending edits as a cause.
        Assert.DoesNotContain("uncommitted edits", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("two common causes", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_ProjectNotOpen_InnerExceptionIsOriginal()
    {
        // The original SDK exception must be preserved as the inner exception
        // so diagnostic tooling (logs, telemetry) can inspect the raw cause.
        var factory = FactoryWithSaveAsFailure("Project is not open.");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.NotNull(ex.InnerException);
        Assert.Contains("Project is not open.", ex.InnerException!.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_SaveAs_GenericFailure_StillThrowsConnectionFailed()
    {
        // A generic SaveAs failure (e.g. "disk full") must still map to ConnectionFailed,
        // not PendingEditsPresent — only the "Project is not open." text triggers
        // the pending-edit path.
        var factory = FactoryWithSaveAsFailure("disk full");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── GoOnline failure ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_GoOnlineFails_ThrowsConnectionFailed()
    {
        var uploaded = new FakeLogixProject
        {
            ConnectedStateResult = ConnectedState.Online,
            GoOnlineException    = new InvalidOperationException("connection refused"),
        };
        var factory = FactoryForUpload(uploaded);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_GoOnlineFails_ErrorMessageContainsCause()
    {
        var uploaded = new FakeLogixProject
        {
            ConnectedStateResult = ConnectedState.Online,
            GoOnlineException    = new InvalidOperationException("connection refused"),
        };
        var factory = FactoryForUpload(uploaded);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("GoOnlineAsync", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── ReadConnectedState returns non-Online ─────────────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_NonOnlineState_ThrowsConnectionFailed()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Offline };
        var factory  = FactoryForUpload(uploaded);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── Success path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_Success_ReturnsService()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.NotNull(result.Service);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_CorrelationPath_IsUploadToNewProject()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.Equal("upload-to-new-project", result.CorrelationPath);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_TypeProject_IsNotNull()
    {
        // TypeProject must be populated so ConnectHandler can derive the gate policy.
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.NotNull(result.TypeProject);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_DownloadPerformed_IsFalse()
    {
        // Upload-to-new-project is non-destructive; no download is performed.
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.False(result.DownloadPerformed);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_ModeTransitions_IsEmpty()
    {
        // Upload-to-new-project does not alter the controller mode.
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.Empty(result.ModeTransitions);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_GoOnlineCalledOnce()
    {
        // Since the ACD is uploaded from the controller, GoOnline should succeed on
        // the first attempt (no ladder rungs needed).
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        _ = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.Equal(1, uploaded.GoOnlineCallCount);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_AcdPath_IsNotNull()
    {
        // AcdPath must be populated so ConnectHandler can register the uploaded
        // project in ProjectService via RegisterFromUpload.
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.NotNull(result.AcdPath);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Success_AcdPath_EndsWithAcd()
    {
        // The ACD path should reference the uploaded.ACD file in the working directory.
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        var result = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.EndsWith(".ACD", result.AcdPath, StringComparison.OrdinalIgnoreCase);
    }

    // ── Service-state reporting on dead-session indicators ────────────────────
    //
    // an earlier change + an earlier change: when either "Project is not open." or
    // "Could not reach LdSdkService" is detected, run the SCM re-query protocol
    // (three queries: immediate, +2 s, +5 s) and report the observed fact:
    //   - service not running (any query) → ConnectionFailed "service not running"
    //   - service running after all queries → ConnectionFailed "service faulted"
    //     (pending-edit cause retired — did not reproduce on hardware 2026-09-06)

    /// <summary>
    /// Builds a <see cref="SdkConnectFactory"/> with a custom saveAs failure
    /// message and an injectable service-state checker.
    /// </summary>
    private SdkConnectFactory FactoryWithSaveAsFailureAndServiceState(
        string saveAsMessage, bool serviceRunning,
        Func<bool>? processRunning = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        return new SdkConnectFactory(
            sessionFactory:            () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:   (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploaded),
            saveAsFactory:             (_, _, _) =>
                Task.FromException(new InvalidOperationException(saveAsMessage)),
            l5xLoader:                 _ => CannedL5X(),
            ldSdkServiceRunning:       () => serviceRunning,
            ldSdkServerProcessRunning: processRunning,
            delay:                     delay ?? ((_, _) => Task.CompletedTask),
            lease: _leaseScope.Lease);
    }

    /// <summary>
    /// Builds a <see cref="SdkConnectFactory"/> with a custom upload failure
    /// message and an injectable service-state checker.
    /// </summary>
    private SdkConnectFactory FactoryWithUploadFailureAndServiceState(
        string uploadMessage, bool serviceRunning,
        Func<bool>? processRunning = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new SdkConnectFactory(
            sessionFactory:            () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:   (_, _, _) =>
                System.Threading.Tasks.Task.FromException<ILogixProject>(
                    new InvalidOperationException(uploadMessage)),
            saveAsFactory:             (_, _, _) => Task.CompletedTask,
            l5xLoader:                 _          => CannedL5X(),
            ldSdkServiceRunning:       () => serviceRunning,
            ldSdkServerProcessRunning: processRunning,
            delay:                     delay ?? ((_, _) => Task.CompletedTask),
            lease: _leaseScope.Lease);

    // ── "Project is not open." + service NOT running ──────────────────────────

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceNotRunning_ThrowsConnectionFailed()
    {
        // When the service is observed not-running, the dead-session condition must
        // be surfaced as ConnectionFailed (not PendingEditsPresent), because
        // the controller-side pending-edit cause is implausible when the service is down.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceNotRunning_ErrorNamesService()
    {
        // The error message must identify LdSdkService as the observed cause.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceNotRunning_ErrorStatesNotRunning()
    {
        // The exact phrase "is not running" must appear so the operator sees the
        // observed state, not a list of possibilities.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceNotRunning_ErrorDoesNotAssertPendingEdits()
    {
        // When the service is down, the message must NOT assert controller pending
        // edits — that diagnosis is only appropriate when the service is running.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.DoesNotContain("uncommitted edits", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending edits", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceNotRunning_ErrorAdvisesRetry()
    {
        // The message must advise the operator to wait and retry — recovery
        // (not the server) will restart the service.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("retry", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── "Project is not open." + service RUNNING ──────────────────────────────
    // When all three SCM re-queries show the service running, the result is still
    // ConnectionFailed — the pending-edit cause has been retired (requirement 3a).

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceRunning_ThrowsConnectionFailed()
    {
        // Even when all three re-queries show the service running, the kind must be
        // ConnectionFailed — the two-cause pending-edit form is retired.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ServiceRunning_MessageNamesService()
    {
        // Service running after re-queries → message must name the service as suspect,
        // not assert controller pending edits.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("LdSdkService", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uncommitted edits", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProjectNotOpen_ScmRace_ThirdQueryRevealsServiceStopped_ThrowsConnectionFailed()
    {
        // SCM-race test (requirement 3b criterion 1c):
        // The first two queries return RUNNING (SCM lag) and the third returns STOPPED.
        // The result must be the definitive "service is not running" form, not the
        // (retired) two-cause form.
        int callCount = 0;
        bool[] sequence = [true, true, false];
        var factory = new SdkConnectFactory(
            sessionFactory:          () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory: (_, _, _) =>
                System.Threading.Tasks.Task.FromResult(
                    (ILogixProject)new FakeLogixProject { ConnectedStateResult = ConnectedState.Online }),
            saveAsFactory:           (_, _, _) =>
                Task.FromException(new InvalidOperationException("Project is not open.")),
            l5xLoader:               _ => CannedL5X(),
            ldSdkServiceRunning:     () => sequence[Math.Min(callCount++, sequence.Length - 1)],
            delay:                   (_, _) => Task.CompletedTask,
            lease: _leaseScope.Lease);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── "Could not reach LdSdkService" + service NOT running (upload step) ───

    [Fact]
    public async Task CreateFromControllerAsync_ServiceUnreachable_UploadStep_ServiceNotRunning_ThrowsConnectionFailed()
    {
        // "Could not reach LdSdkService" from the upload step must trigger the
        // same service-state diagnostic as "Project is not open." from SaveAs.
        var factory = FactoryWithUploadFailureAndServiceState(
            "Could not reach LdSdkService", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceUnreachable_UploadStep_ServiceNotRunning_ErrorNamesService()
    {
        // LdSdkService must be named in the error when the service is confirmed down.
        var factory = FactoryWithUploadFailureAndServiceState(
            "Could not reach LdSdkService", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceUnreachable_UploadStep_ServiceNotRunning_ErrorStatesNotRunning()
    {
        // The "not running" state must be stated explicitly.
        var factory = FactoryWithUploadFailureAndServiceState(
            "Could not reach LdSdkService", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── "Could not reach LdSdkService" + service RUNNING (upload step) ───────

    [Fact]
    public async Task CreateFromControllerAsync_ServiceUnreachable_UploadStep_ServiceRunning_ThrowsConnectionFailed()
    {
        // When "Could not reach LdSdkService" fires from the upload step and the
        // service appears running after re-queries, the kind must be ConnectionFailed
        // — the two-cause pending-edit form is retired (requirement 3a).
        var factory = FactoryWithUploadFailureAndServiceState(
            "Could not reach LdSdkService", serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── "Could not reach LdSdkService" + service NOT running (saveAs step) ───

    [Fact]
    public async Task CreateFromControllerAsync_ServiceUnreachable_SaveAsStep_ServiceNotRunning_ThrowsConnectionFailed()
    {
        // "Could not reach LdSdkService" from the saveAs step (gRPC died between
        // upload and SaveAs) must also trigger the service-state diagnostic.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Could not reach LdSdkService", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceUnreachable_SaveAsStep_ServiceNotRunning_ErrorStatesNotRunning()
    {
        // The observed "not running" state must be reported definitively.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Could not reach LdSdkService", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SCM race-avoidance re-query logic ─────────────────────────────────────
    //
    // When the first service-state query reads RUNNING, the factory must re-query
    // after 2 s and then after a further 5 s before choosing the two-cause form.
    // Only if all three queries agree RUNNING is the two-cause form chosen.

    /// <summary>
    /// Builds a factory that returns a sequence of service-running states across
    /// successive combined (SCM+process) queries.  The delay is always a no-op so
    /// tests run instantly.  Each query consumes the next entry in
    /// <paramref name="serviceStates"/>; once exhausted the last value is repeated.
    /// </summary>
    private SdkConnectFactory FactoryWithSaveAsFailureAndServiceStateSequence(
        string saveAsMessage, bool[] serviceStates)
    {
        int callIndex = 0;
        bool ServiceRunning()
        {
            var idx   = Math.Min(callIndex, serviceStates.Length - 1);
            callIndex++;
            return serviceStates[idx];
        }

        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        return new SdkConnectFactory(
            sessionFactory:            () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:   (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploaded),
            saveAsFactory:             (_, _, _) =>
                Task.FromException(new InvalidOperationException(saveAsMessage)),
            l5xLoader:                 _ => CannedL5X(),
            ldSdkServiceRunning:       ServiceRunning,
            delay:                     (_, _) => Task.CompletedTask,
            lease: _leaseScope.Lease);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Requery_Q1Running_Q2NotRunning_ThrowsConnectionFailed()
    {
        // Q1=running, Q2=not-running: must emit the definitive "service is down" error
        // rather than the two-cause pending-edit form.
        var factory = FactoryWithSaveAsFailureAndServiceStateSequence(
            "Project is not open.", new[] { true, false });

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Requery_Q1Running_Q2NotRunning_ErrorStatesNotRunning()
    {
        // Q1=running, Q2=not-running: error message must state "not running".
        var factory = FactoryWithSaveAsFailureAndServiceStateSequence(
            "Project is not open.", new[] { true, false });

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Requery_Q1Q2Running_Q3NotRunning_ThrowsConnectionFailed()
    {
        // Q1=running, Q2=running, Q3=not-running: third query must still detect
        // the service as down and emit ConnectionFailed.
        var factory = FactoryWithSaveAsFailureAndServiceStateSequence(
            "Project is not open.", new[] { true, true, false });

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Requery_AllThreeRunning_ThrowsConnectionFailed()
    {
        // All three queries return running → service-faulted form (ConnectionFailed).
        // The pending-edit cause has been retired (requirement 3a).
        var factory = FactoryWithSaveAsFailureAndServiceStateSequence(
            "Project is not open.", new[] { true, true, true });

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_Requery_AllThreeRunning_MessageNamesService()
    {
        // All three queries return running → message names the service as the suspect.
        var factory = FactoryWithSaveAsFailureAndServiceStateSequence(
            "Project is not open.", new[] { true, true, true });

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("two common causes", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Process presence check ────────────────────────────────────────────────
    //
    // The absence of an LdSdkServer.exe process must be treated as "not running"
    // independently of the SCM state.

    [Fact]
    public async Task CreateFromControllerAsync_ProcessAbsent_ServiceRunning_ThrowsConnectionFailed()
    {
        // SCM says RUNNING but process is absent → definitive "service is down" error.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: true,
            processRunning: () => false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProcessAbsent_ServiceRunning_ErrorStatesNotRunning()
    {
        // SCM says RUNNING but process is absent → "not running" in error message.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: true,
            processRunning: () => false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("not running", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProcessAbsent_ServiceRunning_ErrorDoesNotAssertPendingEdits()
    {
        // SCM says RUNNING but process is absent → must NOT assert pending edits.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: true,
            processRunning: () => false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.DoesNotContain("uncommitted edits", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending edits", ex.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ProcessPresent_ServiceRunning_ThrowsConnectionFailed()
    {
        // SCM says RUNNING and process is present → ConnectionFailed (service-faulted form).
        // The pending-edit cause has been retired (requirement 3a).
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Project is not open.", serviceRunning: true,
            processRunning: () => true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── "Unable to complete connection to LdSdkService" dead-session indicator ──
    //
    // This SDK error text must be treated as equivalent to
    // "Could not reach LdSdkService" — both are dead-session indicators.

    [Fact]
    public async Task CreateFromControllerAsync_UnableToCompleteConnection_ServiceNotRunning_ThrowsConnectionFailed()
    {
        // "Unable to complete connection to LdSdkService" with service not running →
        // ConnectionFailed (same behaviour as "Could not reach LdSdkService").
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Unable to complete connection to LdSdkService", serviceRunning: false);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UnableToCompleteConnection_ServiceRunning_ThrowsConnectionFailed()
    {
        // "Unable to complete connection to LdSdkService" with service running →
        // ConnectionFailed (service-faulted form); pending-edit cause retired.
        var factory = FactoryWithSaveAsFailureAndServiceState(
            "Unable to complete connection to LdSdkService", serviceRunning: true);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── RxE_COMM_* comm-layer error — CreateAsync (file-based path, rung 1) ────
    //
    // When GoOnlineAsync on the file-based path (CreateAsync) throws an RxE_COMM_*
    // error, the factory must surface ProjectNotCorrelated immediately.  The TCP
    // probe is NOT invoked because the error is inherent to the file-based path,
    // not to controller reachability. an earlier change.

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsRxeCommDtlerror_ThrowsProjectNotCorrelated()
    {
        // RxE_COMM_DTLERROR on rung-1 GoOnline of the file-based path must surface
        // as ProjectNotCorrelated — not SdkCommLayerFaulted or ConnectionFailed.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxE_COMM_DTLERROR - comm layer faulted")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("proj.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ProjectNotCorrelated, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsRxeCommOther_ThrowsProjectNotCorrelated()
    {
        // Any RxE_COMM_ prefix variant must surface as ProjectNotCorrelated — the
        // family match is prefix-based (not limited to DTLERROR).
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "RxE_COMM_OTHER - alternative comm-layer fault code")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("proj.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Equal(TagServiceErrorKind.ProjectNotCorrelated, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsRxeCommDtlerror_ErrorMessageNamesProjectFile()
    {
        // The ProjectNotCorrelated error message must name the file path so the
        // operator knows which file was open when the comm-layer error occurred.
        const string projPath = "C:\\Projects\\MyController.ACD";
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync(projPath, MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains(projPath, ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsRxeCommDtlerror_ErrorMessageNamesCommPathRoute()
    {
        // The error must direct the caller to the comm-path/upload route as the
        // correct alternative (no pre-opened project → controller uploads its image).
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("proj.ACD", MinimalTypeProject(), sdkMode: "online"));

        Assert.Contains("comm-path", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsRxeCommDtlerror_SessionIsDisposed()
    {
        // The session must be disposed when ProjectNotCorrelated is thrown from rung 1
        // to avoid leaking the SDK gRPC channel.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("proj.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsRxeCommDtlerror_DoesNotProceedToRung2()
    {
        // When rung-1 GoOnline throws RxE_COMM_*, the factory exits immediately
        // (ProjectNotCorrelated) — UploadAsync must never be called.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("RxE_COMM_DTLERROR")
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("proj.ACD", MinimalTypeProject(), sdkMode: "online"); }
        catch (TagServiceException) { }

        Assert.Equal(0, fake.UploadCallCount);
    }

    // ── CANNOT_UPLOAD_PHYS_ADDR exclusion — CreateAsync (ordering guard) ───────
    //
    // RxCL_E_CANNOT_UPLOAD_PHYS_ADDR is a correlation failure, not an RxE_COMM_*
    // comm-layer error.  IsIncompatibilityError runs before IsCommLayerError in
    // the catch chain so CANNOT_UPLOAD_PHYS_ADDR maps to ImagesIncompatible,
    // never to ProjectNotCorrelated or SdkCommLayerFaulted.

    [Fact]
    public async Task CreateAsync_OnlineMode_GoOnlineRung1ThrowsCannotUploadPhysAddr_NotProjectNotCorrelated()
    {
        // CANNOT_UPLOAD_PHYS_ADDR must NOT be classified as ProjectNotCorrelated —
        // it is a correlation failure that maps to ImagesIncompatible.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException(
                "CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated.")
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("proj.ACD", MinimalTypeProject(), sdkMode: "online"));

        // Must be ImagesIncompatible (via IsIncompatibilityError), not ProjectNotCorrelated.
        Assert.NotEqual(TagServiceErrorKind.ProjectNotCorrelated, ex.Error.Kind);
        Assert.Equal(TagServiceErrorKind.ImagesIncompatible, ex.Error.Kind);
    }

    // ── RxE_COMM_* from upload step → SdkCommLayerFaulted (CreateAsync rung 2) ─
    //
    // When rung-1 GoOnline fails for a non-RxE_COMM reason and the rung-2
    // UploadAsync throws RxE_COMM_*, lastSdkFail carries the comm-layer error.
    // With allowControllerDownload=false the factory surfaces SdkCommLayerFaulted.

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2UploadThrowsRxeCommDtlerror_ThrowsSdkCommLayerFaulted()
    {
        // Rung 1: GoOnline fails with IMAGES_INCOMPATIBLE (non-RxE_COMM).
        // Rung 2: UploadAsync throws RxE_COMM_DTLERROR → lastSdkFail = RxE_COMM_*.
        // allowControllerDownload=false → SdkCommLayerFaulted (not ConnectionFailed).
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            UploadException   = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"),
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("proj.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: false));

        Assert.Equal(TagServiceErrorKind.SdkCommLayerFaulted, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2UploadThrowsRxeCommDtlerror_ErrorMessageQuotesSdkText()
    {
        // The SdkCommLayerFaulted message must include the raw RxE_COMM_* SDK code.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            UploadException   = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"),
        };
        var factory = FactoryWithFake(fake);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateAsync("proj.ACD", MinimalTypeProject(),
                sdkMode: "online", allowControllerDownload: false));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2UploadThrowsRxeCommDtlerror_SessionIsDisposed()
    {
        // Session must be disposed when SdkCommLayerFaulted is thrown.
        var fake = new FakeLogixProject
        {
            GoOnlineException = new InvalidOperationException("IMAGES_INCOMPATIBLE"),
            UploadException   = new InvalidOperationException("RxE_COMM_DTLERROR"),
        };
        var factory = FactoryWithFake(fake);

        try { await factory.CreateAsync("proj.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: false); }
        catch (TagServiceException) { }

        Assert.Equal(1, fake.DisposeCallCount);
    }

    // ── RxE_COMM_* comm-layer error — CreateFromControllerAsync ─────────────
    //
    // CreateFromControllerAsync (the no-project-file upload path) runs a TCP probe
    // on port 44818 when either the upload step or GoOnlineAsync throws an
    // RxE_COMM_* error.  The default probe returns true (controller reachable),
    // so the triage yields SdkServiceDegraded in these tests.

    [Fact]
    public async Task CreateFromControllerAsync_UploadThrowsRxeCommDtlerror_ThrowsSdkServiceDegraded()
    {
        // UploadToNewProjectAsync throws RxE_COMM_DTLERROR; default probe says
        // controller is reachable → SdkServiceDegraded.
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UploadThrowsRxeCommOther_ThrowsSdkServiceDegraded()
    {
        // Any RxE_COMM_ variant from the upload step must surface as SdkServiceDegraded
        // when the default probe confirms the controller is reachable.
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_OTHER - other comm-layer error code"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UploadThrowsRxeCommDtlerror_ErrorMessageQuotesSdkText()
    {
        // The SdkCommLayerFaulted message must include the raw RxE_COMM_* SDK code.
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_UploadThrowsRxeCommDtlerror_ErrorMessageNamesCommPath()
    {
        // The SdkCommLayerFaulted message must name the controller comm path.
        var factory = FactoryWithUploadFailure(
            new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("10.0.0.1", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_GoOnlineThrowsRxeCommDtlerror_ThrowsSdkServiceDegraded()
    {
        // Upload succeeds; GoOnlineAsync throws RxE_COMM_DTLERROR; default probe
        // says controller is reachable → SdkServiceDegraded.
        var uploaded = new FakeLogixProject
        {
            ConnectedStateResult = ConnectedState.Online,
            GoOnlineException    = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"),
        };
        var factory = FactoryForUpload(uploaded);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_GoOnlineThrowsRxeCommDtlerror_ErrorMessageQuotesSdkText()
    {
        // The SdkCommLayerFaulted message from a GoOnline failure must quote the
        // raw RxE_COMM_* SDK code.
        var uploaded = new FakeLogixProject
        {
            ConnectedStateResult = ConnectedState.Online,
            GoOnlineException    = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted"),
        };
        var factory = FactoryForUpload(uploaded);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── RxE_COMM_* inner-exception chain — CreateFromControllerAsync ──────────
    //
    // The SDK sometimes wraps the Rockwell error in a higher-level exception type.
    // IsCommLayerError must walk the full inner-exception chain.

    [Fact]
    public async Task CreateFromControllerAsync_UploadThrowsWrappedRxeCommDtlerror_ThrowsSdkServiceDegraded()
    {
        // RxE_COMM_DTLERROR nested in an inner exception (SDK wrapper pattern)
        // must still be classified by the triage logic; with the default probe
        // confirming the controller is reachable the result is SdkServiceDegraded.
        var inner   = new InvalidOperationException("RxE_COMM_DTLERROR - wrapped comm-layer fault");
        var wrapped = new Exception("SDK operation failed", inner);

        var factory = FactoryWithUploadFailure(wrapped);

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("10.0.0.1"));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    // ── TryParseControllerIp — comm-path parser ───────────────────────────────
    //
    // The parser must extract the controller IP from each of the four recognised
    // comm-path forms and return null for anything else.

    [Theory]
    [InlineData("192.0.2.10",                                  "192.0.2.10")]   // bare IP
    [InlineData("EmulateEthernet\\192.0.2.10",                 "192.0.2.10")]   // emulated controller
    [InlineData("AB_ETHIP-1\\192.0.2.10",                      "192.0.2.10")]   // real controller via AB EtherNet/IP driver
    [InlineData("EmulateEthernet\\192.0.2.10\\Backplane\\0",   "192.0.2.10")]   // emulated + backplane route
    [InlineData("192.0.2.10,1,0",                              "192.0.2.10")]   // bare IP + CIP comma route
    [InlineData("AB_ETHIP-1\\192.0.2.10,1,0",                  "192.0.2.10")]   // AB_ETHIP + CIP comma route
    [InlineData("emulateethernet\\10.0.0.1",                      "10.0.0.1")]        // case-insensitive prefix
    [InlineData("ab_ethip-1\\10.0.0.1",                           "10.0.0.1")]        // case-insensitive prefix
    [InlineData("UNKNOWNPREFIX\\192.0.2.10",                   null)]              // unrecognised prefix → null
    [InlineData("not-an-ip-address",                              null)]              // invalid bare string → null
    [InlineData("",                                               null)]              // empty string → null
    public void TryParseControllerIp_RecognisedAndUnrecognisedPaths_ReturnExpectedIp(
        string commPath, string? expected)
    {
        var result = SdkConnectFactory.TryParseControllerIp(commPath);
        Assert.Equal(expected, result);
    }

    // ── Injectable _controllerReachable probe ────────────────────────────────
    //
    // The TCP reachability probe must be injectable so unit tests never touch
    // the network.  When service is running but the injected probe returns false,
    // the factory must surface a definitive "controller unreachable" error.

    /// <summary>
    /// Builds a <see cref="SdkConnectFactory"/> with a saveAs failure, configurable
    /// service/process state, and an injectable TCP reachability probe.
    /// </summary>
    private SdkConnectFactory FactoryWithSaveAsFailureServiceStateAndProbe(
        string saveAsMessage,
        bool   serviceRunning,
        Func<string, CancellationToken,
            System.Threading.Tasks.Task<bool>>? controllerReachable = null,
        Func<bool>? processRunning = null)
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        return new SdkConnectFactory(
            sessionFactory:            () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:   (_, _, _) =>
                System.Threading.Tasks.Task.FromResult((ILogixProject)uploaded),
            saveAsFactory:             (_, _, _) =>
                Task.FromException(new InvalidOperationException(saveAsMessage)),
            l5xLoader:                 _ => CannedL5X(),
            ldSdkServiceRunning:       () => serviceRunning,
            controllerReachable:       controllerReachable,
            ldSdkServerProcessRunning: processRunning,
            delay:                     (_, _) => Task.CompletedTask,
            lease: _leaseScope.Lease);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceRunning_ControllerUnreachable_ThrowsConnectionFailed()
    {
        // Service appears running across all three re-queries, but the injected TCP
        // probe returns false — controller cannot be reached on port 44818.
        // Must surface ConnectionFailed naming the controller as the root cause.
        var factory = FactoryWithSaveAsFailureServiceStateAndProbe(
            "Project is not open.", serviceRunning: true,
            controllerReachable: (_, _) => System.Threading.Tasks.Task.FromResult(false));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("EmulateEthernet\\192.0.2.10"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceRunning_ControllerUnreachable_ErrorMentionsPort44818()
    {
        // The error message must name port 44818 so the operator knows which port
        // was probed (EtherNet/IP standard port).
        var factory = FactoryWithSaveAsFailureServiceStateAndProbe(
            "Project is not open.", serviceRunning: true,
            controllerReachable: (_, _) => System.Threading.Tasks.Task.FromResult(false));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("EmulateEthernet\\192.0.2.10"));

        Assert.Contains("44818", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceRunning_ControllerUnreachable_ErrorNamesControllerIp()
    {
        // The error message must include the IP extracted from the comm path so the
        // operator can identify which controller is unreachable.
        var factory = FactoryWithSaveAsFailureServiceStateAndProbe(
            "Project is not open.", serviceRunning: true,
            controllerReachable: (_, _) => System.Threading.Tasks.Task.FromResult(false));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("EmulateEthernet\\192.0.2.10"));

        Assert.Contains("192.0.2.10", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceRunning_ControllerReachable_InjectedProbeIsCalled()
    {
        // The injected probe must be invoked when service is running, proving that
        // unit tests can control reachability without making real network calls.
        bool probeCalled = false;
        var factory = FactoryWithSaveAsFailureServiceStateAndProbe(
            "Project is not open.", serviceRunning: true,
            controllerReachable: (_, _) =>
            {
                probeCalled = true;
                return System.Threading.Tasks.Task.FromResult(true);
            });

        _ = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("AB_ETHIP-1\\10.0.0.1"));

        Assert.True(probeCalled,
            "Expected the injected _controllerReachable delegate to be called, but it was not.");
    }

    [Fact]
    public async Task CreateFromControllerAsync_ServiceRunning_ControllerUnreachable_AbEthipCommPath_ThrowsConnectionFailed()
    {
        // AB_ETHIP-1 comm-path form must also work with the probe: IP is extracted
        // correctly and, when the probe returns false, ConnectionFailed is surfaced.
        var factory = FactoryWithSaveAsFailureServiceStateAndProbe(
            "Project is not open.", serviceRunning: true,
            controllerReachable: (_, _) => System.Threading.Tasks.Task.FromResult(false));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync("AB_ETHIP-1\\10.0.0.2"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
        Assert.Contains("10.0.0.2", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
