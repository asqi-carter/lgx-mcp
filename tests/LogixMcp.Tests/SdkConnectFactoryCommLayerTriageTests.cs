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
/// Unit tests for the three-branch comm-layer triage logic in
/// <see cref="SdkConnectFactory.CreateFromControllerAsync"/>.
///
/// <para>
/// When the Logix Designer SDK reports an <c>RxE_COMM_*</c> comm-layer error on
/// the connect/upload path, the factory runs a TCP probe on the controller's
/// EtherNet/IP port 44818 and branches:
/// </para>
/// <list type="number">
///   <item>
///     <b>Controller answers (IP derivable + TCP probe succeeds):</b>
///     surfaces <see cref="TagServiceErrorKind.SdkServiceDegraded"/> — controller
///     is alive, fault is in <c>LdSdkService</c>'s communications layer.
///   </item>
///   <item>
///     <b>Controller silent (IP derivable + TCP probe fails):</b>
///     surfaces <see cref="TagServiceErrorKind.ConnectionFailed"/> naming
///     <c>&lt;ip&gt;:44818</c> and the raw SDK code.  No <c>LdSdkService</c>
///     mention — the PLC itself is the likely cause.
///   </item>
///   <item>
///     <b>IP not derivable from comm path:</b>
///     surfaces <see cref="TagServiceErrorKind.ConnectionFailed"/> with a
///     two-cause message — degraded-service cause listed first.
///   </item>
/// </list>
/// <para>
/// Both the upload step and the GoOnlineAsync step are covered for each branch.
/// </para>
/// </summary>
public class SdkConnectFactoryCommLayerTriageTests : IDisposable
{

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();
    // ── Minimal L5X project (no tags needed for triage tests) ─────────────────

    private const string MinimalL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TriageTestController" TargetType="Controller" ContainsContext="false">
          <Controller Name="TriageTestController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
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

    // ── RxE_COMM_* test exception ─────────────────────────────────────────────

    private static readonly InvalidOperationException CommLayerEx =
        new("RxE_COMM_DTLERROR - communications layer fault");

    // ── Factory helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a factory whose upload step fails with <paramref name="commLayerEx"/>.
    /// The <paramref name="controllerReachable"/> delegate controls the TCP probe result.
    /// </summary>
    private SdkConnectFactory FactoryWithUploadCommLayerFault(
        Exception commLayerEx,
        Func<string, CancellationToken, System.Threading.Tasks.Task<bool>> controllerReachable)
        => new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromException<ILogixProject>(commLayerEx),
            saveAsFactory:            (_, _, _) => Task.CompletedTask,
            l5xLoader:                _          => MinimalTypeProject(),
            controllerReachable:      controllerReachable,
            lease: _leaseScope.Lease);

    /// <summary>
    /// Builds a factory whose GoOnlineAsync step (after a successful upload) fails
    /// with <paramref name="commLayerEx"/>.
    /// The <paramref name="controllerReachable"/> delegate controls the TCP probe result.
    /// </summary>
    private SdkConnectFactory FactoryWithGoOnlineCommLayerFault(
        Exception commLayerEx,
        Func<string, CancellationToken, System.Threading.Tasks.Task<bool>> controllerReachable)
    {
        // The uploaded handle throws commLayerEx on GoOnlineAsync.
        var uploadedHandle = new FakeLogixProject
        {
            GoOnlineException    = commLayerEx,
            ConnectedStateResult = ConnectedState.Online,
        };
        return new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromResult<ILogixProject>(uploadedHandle),
            saveAsFactory:            (_, _, _) => Task.CompletedTask,
            l5xLoader:                _          => MinimalTypeProject(),
            controllerReachable:      controllerReachable,
            lease: _leaseScope.Lease);
    }

    // ── Probe delegates ───────────────────────────────────────────────────────

    private static Func<string, CancellationToken, System.Threading.Tasks.Task<bool>>
        ProbeAlwaysReachable()
        => (_, _) => System.Threading.Tasks.Task.FromResult(true);

    private static Func<string, CancellationToken, System.Threading.Tasks.Task<bool>>
        ProbeAlwaysUnreachable()
        => (_, _) => System.Threading.Tasks.Task.FromResult(false);

    // ═════════════════════════════════════════════════════════════════════════
    // Upload-step fault — branch (a): controller answers → SdkServiceDegraded
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UploadFault_ControllerReachable_ThrowsSdkServiceDegraded()
    {
        // Upload throws RxE_COMM_DTLERROR; TCP probe returns reachable.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    [Fact]
    public async Task UploadFault_ControllerReachable_MessageNamesControllerIp()
    {
        // Branch (a): message must name the controller IP.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("198.51.100.100", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadFault_ControllerReachable_MessageMentionsPort44818()
    {
        // Branch (a): message must confirm the controller answers on port 44818.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("44818", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadFault_ControllerReachable_MessageIncludesRawSdkCode()
    {
        // Branch (a): message must include the raw SDK code verbatim.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadFault_ControllerReachable_MessageMentionsLdSdkService()
    {
        // Branch (a): message must mention LdSdkService as the faulted component.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadFault_ControllerReachable_InnerExceptionIsOriginalSdkException()
    {
        // Branch (a): the original SDK exception must be preserved as InnerException.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Same(CommLayerEx, ex.InnerException);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Upload-step fault — branch (b): controller silent → ConnectionFailed
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UploadFault_ControllerUnreachable_ThrowsConnectionFailed()
    {
        // Upload throws RxE_COMM_DTLERROR; TCP probe returns unreachable.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task UploadFault_ControllerUnreachable_MessageNamesIpAndPort()
    {
        // Branch (b): message must name the controller IP and port 44818 together.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("198.51.100.100:44818", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadFault_ControllerUnreachable_MessageIncludesRawSdkCode()
    {
        // Branch (b): raw SDK code must appear in the message.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadFault_ControllerUnreachable_MessageDoesNotMentionLdSdkService()
    {
        // Branch (b): must NOT mention the service — the PLC is the likely cause.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.DoesNotContain("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Upload-step fault — branch (c): IP not derivable → two-cause message
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UploadFault_IpNotDerivable_ThrowsConnectionFailed()
    {
        // commPath has no recognisable IP — triage cannot probe the controller.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task UploadFault_IpNotDerivable_MessageHasDegradedServiceCauseFirst()
    {
        // Branch (c): the degraded-service hypothesis must appear before the
        // unreachable-controller hypothesis in the message text.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        // Locate both causes in the message.
        var msg = ex.Error.Message;
        var degradedIdx    = msg.IndexOf("faulted", StringComparison.OrdinalIgnoreCase);
        var unreachableIdx = msg.IndexOf("not reachable", StringComparison.OrdinalIgnoreCase);

        Assert.True(degradedIdx >= 0,
            "Message should mention service comm layer fault.");
        Assert.True(unreachableIdx >= 0,
            "Message should mention controller unreachable as second cause.");
        Assert.True(degradedIdx < unreachableIdx,
            "Degraded-service cause must appear before unreachable-controller cause.");
    }

    [Fact]
    public async Task UploadFault_IpNotDerivable_MessageIncludesRawSdkCode()
    {
        // Branch (c): raw SDK code must still appear in the two-cause message.
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // GoOnline-step fault — branch (a): controller answers → SdkServiceDegraded
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoOnlineFault_ControllerReachable_ThrowsSdkServiceDegraded()
    {
        // Upload succeeds; GoOnlineAsync throws RxE_COMM_DTLERROR;
        // TCP probe returns reachable.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Equal(TagServiceErrorKind.SdkServiceDegraded, ex.Error.Kind);
    }

    [Fact]
    public async Task GoOnlineFault_ControllerReachable_MessageNamesControllerIp()
    {
        // GoOnline branch (a): message must name the controller IP.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("198.51.100.200", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoOnlineFault_ControllerReachable_MessageIncludesRawSdkCode()
    {
        // GoOnline branch (a): raw SDK code must be quoted verbatim.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("RxE_COMM_DTLERROR", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // GoOnline-step fault — branch (b): controller silent → ConnectionFailed
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoOnlineFault_ControllerUnreachable_ThrowsConnectionFailed()
    {
        // GoOnlineAsync throws RxE_COMM_DTLERROR; TCP probe returns unreachable.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task GoOnlineFault_ControllerUnreachable_MessageNamesIpAndPort()
    {
        // GoOnline branch (b): message must name the controller IP and port together.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Contains("198.51.100.200:44818", ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoOnlineFault_ControllerUnreachable_MessageDoesNotMentionLdSdkService()
    {
        // GoOnline branch (b): no mention of LdSdkService — PLC is the likely cause.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.DoesNotContain("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // GoOnline-step fault — branch (c): IP not derivable → two-cause message
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoOnlineFault_IpNotDerivable_ThrowsConnectionFailed()
    {
        // commPath has no recognisable IP — triage falls through to two-cause message.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task GoOnlineFault_IpNotDerivable_MessageHasDegradedServiceCauseFirst()
    {
        // GoOnline branch (c): degraded-service cause must appear first.
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        var msg = ex.Error.Message;
        var degradedIdx    = msg.IndexOf("faulted", StringComparison.OrdinalIgnoreCase);
        var unreachableIdx = msg.IndexOf("not reachable", StringComparison.OrdinalIgnoreCase);

        Assert.True(degradedIdx >= 0,
            "Message should mention service comm layer fault.");
        Assert.True(unreachableIdx >= 0,
            "Message should mention controller unreachable as second cause.");
        Assert.True(degradedIdx < unreachableIdx,
            "Degraded-service cause must appear before unreachable-controller cause.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Probe delegate is invoked with the correct IP extracted from commPath
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UploadFault_ProbeIsCalledWithExtractedIp()
    {
        // Verify that the TCP probe is invoked with the IP extracted from commPath.
        string? probedIp = null;
        Func<string, CancellationToken, System.Threading.Tasks.Task<bool>> capturingProbe =
            (ip, _) => { probedIp = ip; return System.Threading.Tasks.Task.FromResult(true); };

        var factory  = FactoryWithUploadCommLayerFault(CommLayerEx, capturingProbe);
        const string commPath = "AB_ETHIP-1\\10.0.0.50";

        try { await factory.CreateFromControllerAsync(commPath); }
        catch (TagServiceException) { }

        Assert.Equal("10.0.0.50", probedIp);
    }

    [Fact]
    public async Task UploadFault_IpNotDerivable_ProbeIsNotCalled()
    {
        // When IP cannot be derived, the probe must not be invoked.
        bool probeCalled = false;
        Func<string, CancellationToken, System.Threading.Tasks.Task<bool>> trackingProbe =
            (_, _) => { probeCalled = true; return System.Threading.Tasks.Task.FromResult(true); };

        var factory  = FactoryWithUploadCommLayerFault(CommLayerEx, trackingProbe);
        const string commPath = "UnknownProtocol\\NoIpHere";

        try { await factory.CreateFromControllerAsync(commPath); }
        catch (TagServiceException) { }

        Assert.False(probeCalled, "TCP probe must not be called when IP cannot be derived.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Human-only remediation text enforcement
    //
    // Branch (a) [SdkServiceDegraded] must include a plain-language instruction
    // telling the operator to restart LdSdkService.  Branch (c) [two-cause
    // ConnectionFailed] must include plain-language remediation for both causes.
    // Neither branch may contain a service-control command (Restart-Service,
    // sc start, net start, etc.) — the NoServiceRestartCodePathTests gate covers
    // the source-code scan; these tests verify the runtime message text.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Branch (a), upload fault: the SdkServiceDegraded message must contain a
    /// plain-language instruction to restart LdSdkService, not a service-control
    /// command such as <c>Restart-Service</c> or <c>sc restart</c>.
    /// </summary>
    [Fact]
    public async Task UploadFault_ControllerReachable_MessageContainsRestartInstruction()
    {
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.100,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        // Message must tell the operator to restart in plain language.
        Assert.Contains("restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);

        // Message must NOT contain a service-control command.
        Assert.DoesNotContain("Restart-Service",  ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sc restart",        ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("net start",         ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Service",     ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Branch (a), GoOnline fault: the SdkServiceDegraded message must contain a
    /// plain-language instruction to restart LdSdkService.
    /// </summary>
    [Fact]
    public async Task GoOnlineFault_ControllerReachable_MessageContainsRestartInstruction()
    {
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "198.51.100.200,1,0";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        // Message must tell the operator to restart in plain language.
        Assert.Contains("restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);

        // Message must NOT contain a service-control command.
        Assert.DoesNotContain("Restart-Service",  ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sc restart",        ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("net start",         ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Service",     ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Branch (c), upload fault: the two-cause ConnectionFailed message must
    /// include plain-language remediation for the degraded-service cause —
    /// i.e., it must tell the operator to restart LdSdkService.
    /// </summary>
    [Fact]
    public async Task UploadFault_IpNotDerivable_MessageContainsRemediationForDegradedService()
    {
        var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        // Cause (1) mentions LdSdkService fault and instructs the operator to restart.
        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart",      ex.Error.Message, StringComparison.OrdinalIgnoreCase);

        // Cause (2) tells the operator to verify the network — no service-control command.
        Assert.Contains("verify",             ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restart-Service",  ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sc restart",        ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Branch (c), GoOnline fault: the two-cause ConnectionFailed message must
    /// include plain-language remediation for the degraded-service cause.
    /// </summary>
    [Fact]
    public async Task GoOnlineFault_IpNotDerivable_MessageContainsRemediationForDegradedService()
    {
        var factory = FactoryWithGoOnlineCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
        const string commPath = "UnknownProtocol\\NoIpHere";

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => factory.CreateFromControllerAsync(commPath));

        // Cause (1) mentions LdSdkService fault and instructs the operator to restart.
        Assert.Contains("LdSdkService", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart",      ex.Error.Message, StringComparison.OrdinalIgnoreCase);

        // Cause (2) tells the operator to verify the network — no service-control command.
        Assert.Contains("verify",             ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Restart-Service",  ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sc restart",        ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Audit — sdk_service_diagnosis record
    //
    // Each comm-layer classification must write exactly one sdk_service_diagnosis
    // line to audit.jsonl with the raw SDK code, the probe result, and the kind.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Branch (a), upload fault: the audit log must contain one
    /// <c>sdk_service_diagnosis</c> line with <c>probe="reachable"</c> and
    /// <c>kind="SdkServiceDegraded"</c>.
    /// </summary>
    [Fact]
    public async Task UploadFault_ControllerReachable_WritesAuditDiagnosisRecord()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-triage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
            const string commPath = "198.51.100.100,1,0";

            try { await factory.CreateFromControllerAsync(commPath); }
            catch (TagServiceException) { }

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "audit.jsonl must be written");

            var lines = File.ReadAllLines(auditPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .ToList();

            var diagLines = lines
                .Where(r => r.GetProperty("event").GetString() == "sdk_service_diagnosis")
                .ToList();

            Assert.Single(diagLines);
            Assert.Equal("reachable",        diagLines[0].GetProperty("probe").GetString());
            Assert.Equal("SdkServiceDegraded", diagLines[0].GetProperty("kind").GetString());
            Assert.Contains("RxE_COMM_DTLERROR",
                diagLines[0].GetProperty("sdk_code").GetString() ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Branch (b), upload fault: the audit log must contain one
    /// <c>sdk_service_diagnosis</c> line with <c>probe="unreachable"</c> and
    /// <c>kind="ConnectionFailed"</c>.
    /// </summary>
    [Fact]
    public async Task UploadFault_ControllerUnreachable_WritesAuditDiagnosisRecord()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-triage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysUnreachable());
            const string commPath = "198.51.100.100,1,0";

            try { await factory.CreateFromControllerAsync(commPath); }
            catch (TagServiceException) { }

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "audit.jsonl must be written");

            var lines = File.ReadAllLines(auditPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .ToList();

            var diagLines = lines
                .Where(r => r.GetProperty("event").GetString() == "sdk_service_diagnosis")
                .ToList();

            Assert.Single(diagLines);
            Assert.Equal("unreachable",     diagLines[0].GetProperty("probe").GetString());
            Assert.Equal("ConnectionFailed", diagLines[0].GetProperty("kind").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Branch (c), upload fault: when IP cannot be derived, the audit log must
    /// contain one <c>sdk_service_diagnosis</c> line with
    /// <c>probe="not-probed"</c> and <c>kind="ConnectionFailed"</c>.
    /// </summary>
    [Fact]
    public async Task UploadFault_IpNotDerivable_WritesAuditDiagnosisRecordNotProbed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-triage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var factory = FactoryWithUploadCommLayerFault(CommLayerEx, ProbeAlwaysReachable());
            const string commPath = "UnknownProtocol\\NoIpHere";

            try { await factory.CreateFromControllerAsync(commPath); }
            catch (TagServiceException) { }

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath), "audit.jsonl must be written");

            var lines = File.ReadAllLines(auditPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .ToList();

            var diagLines = lines
                .Where(r => r.GetProperty("event").GetString() == "sdk_service_diagnosis")
                .ToList();

            Assert.Single(diagLines);
            Assert.Equal("not-probed",       diagLines[0].GetProperty("probe").GetString());
            Assert.Equal("ConnectionFailed",  diagLines[0].GetProperty("kind").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
#endif
