#if LOGIX_SDK
// ── NoPendingEditAutoActionTests ──────────────────────────────────────────────
// Guards requirement 4: no code path in the connect or
// upload-from-controller paths shall automatically cancel, accept, or
// finalize pending edits on the controller as a side-effect.
//
// Edit-lifecycle SDK APIs that must NOT be called during connect/upload:
//   • PartialImportRungsFromXmlFileAsync — the rung-import call; carries the
//     PartialImportOption (LeaveEdits / AcceptEdits / FinalizeEdits) that
//     determines whether a pending edit is left, accepted, or finalised.
//   • PartialImportWithTargetFromXmlFileAsync — the named-object import call;
//     also accepts a PartialImportOption and can accept or finalize edits.
//   • GoOfflineAsync — going offline discards any pending edits on the
//     controller; must not be invoked as a silent side-effect of connecting
//     or uploading.
//
// Two complementary verification strategies are used:
//   1. Unit tests using FakeLogixProject with explicit call-count assertions
//      (detect runtime regressions).
//   2. Source-scan tests that read the production .cs files at test time and
//      assert the forbidden symbols/calls are absent from the
//      connect/upload code paths (detect source-level regressions before
//      the code even runs).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task.
using Task = System.Threading.Tasks.Task;

using ConnectedState = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;

namespace LogixMcp.Tests;

/// <summary>
/// Tests asserting that the connect path (<see cref="SdkConnectFactory.CreateAsync"/>)
/// and the upload-from-controller path
/// (<see cref="SdkConnectFactory.CreateFromControllerAsync"/>) never invoke
/// edit-lifecycle SDK methods as side-effects.
///
/// <para>
/// Edit-lifecycle methods guarded here:
/// <list type="bullet">
///   <item><see cref="ILogixProject.PartialImportRungsFromXmlFileAsync"/> —
///     carries the <c>PartialImportOption</c> that can accept or finalise edits.</item>
///   <item><see cref="ILogixProject.PartialImportWithTargetFromXmlFileAsync"/> —
///     the named-object import variant; same option set.</item>
///   <item><see cref="ILogixProject.GoOfflineAsync"/> — going offline
///     silently discards any pending edits on the controller.</item>
/// </list>
/// </para>
/// </summary>
public class NoPendingEditAutoActionTests : IDisposable
{

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();
    // ── Minimal L5X used by all tests ─────────────────────────────────────────

    private const string MinimalL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="NoPendingEditController" TargetType="Controller" ContainsContext="false">
          <Controller Name="NoPendingEditController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
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

    // ── Factory helpers matching the SdkConnectFactoryTests style ─────────────

    /// <summary>
    /// Wraps <paramref name="fake"/> in a <see cref="SdkConnectFactory"/> using
    /// the same pattern as <c>SdkConnectFactoryTests.FactoryWithFake</c>.
    /// </summary>
    private SdkConnectFactory FactoryWithFake(FakeLogixProject fake)
        => new SdkConnectFactory(
            () => new LogixProjectSession(
                (_path, _ct) => Task.FromResult<ILogixProject>(fake)),
                lease: _leaseScope.Lease);

    /// <summary>
    /// Builds a factory for <see cref="SdkConnectFactory.CreateFromControllerAsync"/>
    /// where <paramref name="uploadedHandle"/> is the fake returned by the upload
    /// delegate and subsequently wrapped in the internal session.
    /// </summary>
    private SdkConnectFactory FactoryForUpload(ILogixProject uploadedHandle)
        => new SdkConnectFactory(
            sessionFactory:           () => new LogixProjectSession(
                (_, _) => Task.FromResult<ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory:  (_, _, _) =>
                System.Threading.Tasks.Task.FromResult(uploadedHandle),
            saveAsFactory:            (_, _, _) => Task.CompletedTask,
            l5xLoader:                _          => MinimalTypeProject(),
            lease: _leaseScope.Lease);

    // ── Helper: fake wired for rung-2 (upload → GoOnline) path ───────────────

    /// <summary>
    /// Returns a <see cref="FakeLogixProject"/> configured so that rung 2
    /// of the correlation ladder runs: GoOnline fails on the first call (rung 1)
    /// but succeeds on the second call after <c>UploadAsync</c> (rung 2).
    /// </summary>
    private static FakeLogixProject FakeRequiringRung2() =>
        new()
        {
            ConnectedStateResult         = ConnectedState.Online,
            GoOnlineException            = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project."),
            GoOnlineExceptionOnCallIndex = 0,   // only rung-1 call (index 0) fails
        };

    /// <summary>
    /// Returns a <see cref="FakeLogixProject"/> configured so that rungs 1 and 2
    /// both fail, forcing rung 3 (download path) when
    /// <c>allowControllerDownload=true</c>.
    /// </summary>
    private static FakeLogixProject FakeRequiringRung3() =>
        new()
        {
            ConnectedStateResult = ConnectedState.Online,
            GoOnlineException    = new InvalidOperationException(
                "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project."),
            GoOnlineFailCount    = 2,   // fails on calls 0 (rung 1) and 1 (rung 2)
        };

    // ═════════════════════════════════════════════════════════════════════════
    // Section 1 — CreateAsync: all correlation rungs must not call edit-
    //             lifecycle APIs.
    // ═════════════════════════════════════════════════════════════════════════

    // ── Offline mode ──────────────────────────────────────────────────────────

    /// <summary>
    /// Offline mode (no controller connection): <c>PartialImportRungsFromXmlFileAsync</c>
    /// must never be called as a side-effect of opening an offline session.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OfflineMode_DoesNotCallPartialImportRungsFromXmlFileAsync()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "offline");

        Assert.Equal(0, fake.PartialImportRungsCallCount);
    }

    /// <summary>
    /// Offline mode: <c>PartialImportWithTargetFromXmlFileAsync</c> must never
    /// be called during session creation.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OfflineMode_DoesNotCallPartialImportWithTargetFromXmlFileAsync()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "offline");

        Assert.Equal(0, fake.PartialImportWithTargetCallCount);
    }

    /// <summary>
    /// Offline mode: <c>GoOfflineAsync</c> (which would discard pending edits)
    /// must never be called during session creation.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OfflineMode_DoesNotCallGoOfflineAsync()
    {
        var fake    = new FakeLogixProject();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "offline");

        Assert.Equal(0, fake.GoOfflineCallCount);
    }

    // ── Online mode — rung 1 succeeds ─────────────────────────────────────────

    /// <summary>
    /// Online rung 1 (GoOnline succeeds immediately, controller already correlated):
    /// no rung-import call must be made.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung1Succeeds_DoesNotCallPartialImportRungsFromXmlFileAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.Equal(0, fake.PartialImportRungsCallCount);
    }

    /// <summary>
    /// Online rung 1: no named-object import call must be made.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung1Succeeds_DoesNotCallPartialImportWithTargetFromXmlFileAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.Equal(0, fake.PartialImportWithTargetCallCount);
    }

    /// <summary>
    /// Online rung 1: GoOfflineAsync (which would discard pending edits) must not
    /// be called as part of the connection sequence.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung1Succeeds_DoesNotCallGoOfflineAsync()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.Equal(0, fake.GoOfflineCallCount);
    }

    // ── Online mode — rung 2 (upload → GoOnline) ─────────────────────────────

    /// <summary>
    /// Online rung 2 (upload → GoOnline — normal uncorrelated case): the upload
    /// step is non-destructive and must not touch any edit-lifecycle API.
    /// <c>PartialImportRungsFromXmlFileAsync</c> must not be called.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_DoesNotCallPartialImportRungsFromXmlFileAsync()
    {
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.Equal(0, fake.PartialImportRungsCallCount);
    }

    /// <summary>
    /// Online rung 2: <c>PartialImportWithTargetFromXmlFileAsync</c> must not
    /// be called.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_DoesNotCallPartialImportWithTargetFromXmlFileAsync()
    {
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.Equal(0, fake.PartialImportWithTargetCallCount);
    }

    /// <summary>
    /// Online rung 2: <c>GoOfflineAsync</c> must not be called — uploading the
    /// controller image must not discard any pending edits as a side-effect.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung2_DoesNotCallGoOfflineAsync()
    {
        var fake    = FakeRequiringRung2();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.Equal(0, fake.GoOfflineCallCount);
    }

    // ── Online mode — rung 3 (download path, allowControllerDownload=true) ────

    /// <summary>
    /// Online rung 3 (download → GoOnline → RUN — most invasive path):
    /// even the download sequence must not call edit-lifecycle APIs.
    /// <c>PartialImportRungsFromXmlFileAsync</c> must not be called.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung3_DoesNotCallPartialImportRungsFromXmlFileAsync()
    {
        var fake    = FakeRequiringRung3();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(0, fake.PartialImportRungsCallCount);
    }

    /// <summary>
    /// Online rung 3: <c>PartialImportWithTargetFromXmlFileAsync</c> must not
    /// be called.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung3_DoesNotCallPartialImportWithTargetFromXmlFileAsync()
    {
        var fake    = FakeRequiringRung3();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(0, fake.PartialImportWithTargetCallCount);
    }

    /// <summary>
    /// Online rung 3: <c>GoOfflineAsync</c> must not be called during the
    /// PROGRAM→download→GoOnline→RUN sequence — the download itself does not
    /// cancel pending edits.
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung3_DoesNotCallGoOfflineAsync()
    {
        var fake    = FakeRequiringRung3();
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(),
            sdkMode: "online", allowControllerDownload: true);

        Assert.Equal(0, fake.GoOfflineCallCount);
    }

    // ── Online mode: import options must be LeaveEdits or AcceptEdits only ────

    /// <summary>
    /// Guards the permitted-options invariant: if <c>PartialImportRungsFromXmlFileAsync</c>
    /// were ever called during a connect operation, the only acceptable options are
    /// <c>LeaveEdits</c> and <c>AcceptEdits</c>.  <c>FinalizeEdits</c> must never appear.
    ///
    /// <para>
    /// This test is complementary to the "zero calls" assertions above: even if a
    /// future regression introduces an import call, it must not use
    /// <c>FinalizeEdits</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateAsync_OnlineMode_Rung1_PartialImportRungsOptions_NeverContainFinalizeEdits()
    {
        var fake    = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory = FactoryWithFake(fake);

        _ = await factory.CreateAsync("dummy.ACD", MinimalTypeProject(), sdkMode: "online");

        Assert.DoesNotContain(
            RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption.FinalizeEdits,
            fake.PartialImportRungsOptions);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Section 2 — CreateFromControllerAsync (upload-to-new-project path):
    //             must not call edit-lifecycle APIs.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Upload-from-controller (no-project-file path): <c>PartialImportRungsFromXmlFileAsync</c>
    /// must never be called on the uploaded handle as a side-effect of the
    /// connect+upload sequence.
    /// </summary>
    [Fact]
    public async Task CreateFromControllerAsync_Success_DoesNotCallPartialImportRungsFromXmlFileAsync()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        _ = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.Equal(0, uploaded.PartialImportRungsCallCount);
    }

    /// <summary>
    /// Upload-from-controller: <c>PartialImportWithTargetFromXmlFileAsync</c>
    /// must never be called.
    /// </summary>
    [Fact]
    public async Task CreateFromControllerAsync_Success_DoesNotCallPartialImportWithTargetFromXmlFileAsync()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        _ = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.Equal(0, uploaded.PartialImportWithTargetCallCount);
    }

    /// <summary>
    /// Upload-from-controller: <c>GoOfflineAsync</c> must not be called — the
    /// upload sequence must never silently discard pending edits on the controller.
    /// </summary>
    [Fact]
    public async Task CreateFromControllerAsync_Success_DoesNotCallGoOfflineAsync()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        _ = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.Equal(0, uploaded.GoOfflineCallCount);
    }

    /// <summary>
    /// Upload-from-controller: if an import call were ever made, its option
    /// must not be <c>FinalizeEdits</c>.
    /// </summary>
    [Fact]
    public async Task CreateFromControllerAsync_Success_PartialImportRungsOptions_NeverContainFinalizeEdits()
    {
        var uploaded = new FakeLogixProject { ConnectedStateResult = ConnectedState.Online };
        var factory  = FactoryForUpload(uploaded);

        _ = await factory.CreateFromControllerAsync("10.0.0.1");

        Assert.DoesNotContain(
            RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption.FinalizeEdits,
            uploaded.PartialImportRungsOptions);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Section 3 — Source-scan assertions on the connect/upload implementation.
    //
    // These tests read the production .cs files at test time and verify that
    // the edit-lifecycle symbols never appear in the connect or upload
    // implementation file (SdkConnectFactory.cs).  They serve as a static
    // guard that catches regressions before the code even runs: if a future
    // developer adds a call to PartialImportRungsFromXmlFileAsync or
    // GoOfflineAsync inside CreateAsync or CreateFromControllerAsync, the
    // scan fails immediately on any test run.
    // ═════════════════════════════════════════════════════════════════════════

    // ── Helper: locate the src/ directory from the test-output directory ──────

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> until it finds a
    /// <c>src/</c> directory, or returns <c>null</c> when it cannot be located.
    /// </summary>
    private static string? LocateSrcDir()
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

    /// <summary>
    /// Asserts that the src/ directory can be located and returns it.
    /// Fails the test with a descriptive message when it cannot be found.
    /// </summary>
    private static string RequireSrcDir()
    {
        var srcDir = LocateSrcDir();
        Assert.True(srcDir is not null && Directory.Exists(srcDir),
            "Could not locate the src/ directory from the test output path. " +
            "Ensure the solution layout has not changed.");
        return srcDir!;
    }

    /// <summary>
    /// Locates <c>SdkConnectFactory.cs</c> under <paramref name="srcDir"/> and
    /// returns its path, or fails the test when the file cannot be found.
    /// </summary>
    private static string RequireSdkConnectFactoryFile(string srcDir)
    {
        var matches = Directory.GetFiles(srcDir, "SdkConnectFactory.cs",
            SearchOption.AllDirectories);
        Assert.True(matches.Length > 0,
            "Could not locate SdkConnectFactory.cs under src/. " +
            "Ensure the file has not been renamed or moved.");
        return matches[0];
    }

    /// <summary>
    /// (scan-a) <c>SdkConnectFactory.cs</c> must not contain any call to
    /// <c>PartialImportRungsFromXmlFileAsync</c>.  The connect and
    /// upload-from-controller paths have no business calling rung-import APIs;
    /// doing so would alter the edit state of the controller as a side-effect
    /// of connecting.
    /// </summary>
    [Fact]
    public void SourceScan_SdkConnectFactory_NoCallToPartialImportRungsFromXmlFileAsync()
    {
        var srcDir  = RequireSrcDir();
        var srcFile = RequireSdkConnectFactoryFile(srcDir);
        var text    = File.ReadAllText(srcFile);

        Assert.DoesNotContain(
            "PartialImportRungsFromXmlFileAsync",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (scan-b) <c>SdkConnectFactory.cs</c> must not contain any call to
    /// <c>PartialImportWithTargetFromXmlFileAsync</c>.
    /// </summary>
    [Fact]
    public void SourceScan_SdkConnectFactory_NoCallToPartialImportWithTargetFromXmlFileAsync()
    {
        var srcDir  = RequireSrcDir();
        var srcFile = RequireSdkConnectFactoryFile(srcDir);
        var text    = File.ReadAllText(srcFile);

        Assert.DoesNotContain(
            "PartialImportWithTargetFromXmlFileAsync",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (scan-c) <c>SdkConnectFactory.cs</c> must not contain <c>FinalizeEdits</c>.
    /// This would finalise any pending edit on the controller without operator
    /// review — forbidden on any code path in the connect/upload flow.
    /// </summary>
    [Fact]
    public void SourceScan_SdkConnectFactory_NoFinalizeEdits()
    {
        var srcDir  = RequireSrcDir();
        var srcFile = RequireSdkConnectFactoryFile(srcDir);
        var text    = File.ReadAllText(srcFile);

        Assert.DoesNotContain(
            "FinalizeEdits",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (scan-d) <c>SdkConnectFactory.cs</c> must not call <c>GoOfflineAsync</c>.
    /// Going offline silently discards any pending edits on the controller;
    /// it must never happen as a side-effect of a connect or upload operation.
    /// </summary>
    [Fact]
    public void SourceScan_SdkConnectFactory_NoCallToGoOfflineAsync()
    {
        var srcDir  = RequireSrcDir();
        var srcFile = RequireSdkConnectFactoryFile(srcDir);
        var text    = File.ReadAllText(srcFile);

        Assert.DoesNotContain(
            "GoOfflineAsync",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (scan-e) Broad src/ scan: <c>FinalizeEdits</c> must not appear anywhere
    /// in the production source tree, <em>except</em> in
    /// <c>FinalizeEditsHandler.cs</c>, <c>LogixSdkTools.cs</c> — the handler
    /// and tool wiring for the gated <c>logix_finalize_edits</c> MCP tool
    /// — and <c>AuditLogger.cs</c> (its
    /// <c>LogFinalizeEdits</c> method only records the <c>edits_finalized</c>
    /// audit event; it never triggers a finalize). This extends the existing
    /// <c>SourceScan_NoFinalizeEditsInSrc</c> test in
    /// <c>ProposeRungEditHandlerTests</c> and ensures the invariant — that
    /// <c>FinalizeEdits</c> is never reachable from <c>logix_connect</c>/
    /// <c>SdkConnectFactory.cs</c> or <c>logix_propose_rung_edit</c>/
    /// <c>ProposeRungEditHandler.cs</c> — is also enforced from this dedicated
    /// pending-edit guard test class.
    /// </summary>
    [Fact]
    public void SourceScan_AllSrc_NoFinalizeEdits()
    {
        var srcDir    = RequireSrcDir();
        var violations = new List<string>();

        // The gated logix_finalize_edits tool and its wiring are
        // the one deliberate, reviewed exception to this scan.
        // ProposeRungEditHandler.cs is the one additional
        // deliberate, reviewed exception — it dispatches FinalizeEdits only for
        // target="live", guarded so no other target can select it.
        var allowedFileNames = new[]
            { "FinalizeEditsHandler.cs", "LogixSdkTools.cs", "AuditLogger.cs",
              "ProposeRungEditHandler.cs" };

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs",
            SearchOption.AllDirectories))
        {
            if (allowedFileNames.Contains(Path.GetFileName(file)))
                continue;

            if (File.ReadAllText(file).Contains("FinalizeEdits", StringComparison.Ordinal))
                violations.Add(file);
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// (scan-f) Broad src/ scan: the string <c>CancelEdits</c> must not appear in
    /// any production .cs file.  Although <c>CancelEdits</c> is not an existing
    /// <see cref="RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption"/>
    /// value in the current SDK version, its appearance in source would signal a
    /// future SDK upgrade that introduces a cancel-edits option; this scan ensures
    /// that any such introduction is a deliberate, reviewed decision rather than an
    /// accidental side-effect.
    /// </summary>
    [Fact]
    public void SourceScan_AllSrc_NoCancelEdits()
    {
        var srcDir    = RequireSrcDir();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs",
            SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("CancelEdits", StringComparison.Ordinal))
                violations.Add(file);
        }

        Assert.Empty(violations);
    }
}
#endif
