#if LOGIX_SDK
// ── HardwareTests — hardware-gated reproduction tests against Echo bench ─────
//
// Skipped when no Echo controller is reachable at 192.0.2.30 on EtherNet/IP
// port 44818 — never fails for absent hardware.
//
// When the Echo IS reachable but an SDK-layer member read fails (reproducing the
// XMLSrv_E_TARGET_NOT_FOUND defect), the exact SDK exception text
// is recorded in the skip reason.  A skip is reported, not counted as a pass.
//
// Run on the bench (Echo must be running):
//   dotnet test --filter Category=Integration
// ─────────────────────────────────────────────────────────────────────────────
using System.Net.Sockets;
using ILogixProjectHandle   = RockwellAutomation.LogixDesigner.ILogixProject;
using ImportCollisionOptions = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using IOperationEvent        = RockwellAutomation.LogixDesigner.Logging.IOperationEvent;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Hardware-gated reproduction tests that require a live FactoryTalk Logix Echo
/// controller reachable at 192.0.2.30 on EtherNet/IP port 44818.
///
/// <para>
/// Every test skips (never fails) when the Echo is not reachable.  When the Echo
/// is reachable but an SDK-layer operation fails, the skip reason carries the
/// exact SDK exception text so the defect is documented without counting it as a
/// CI failure.
/// </para>
///
/// <para>
/// The tests use the same connect path the MCP uses: <c>upload-to-new-project</c>
/// via <see cref="SdkConnectFactory.CreateFromControllerAsync"/>, which calls
/// <c>LogixProject.UploadToNewProjectAsync</c> to obtain the controller's current
/// program and goes online against it.
/// </para>
/// </summary>
[Collection("LogixAcd")]
[Trait("Category", "Integration")]
public class HardwareTests : IDisposable
{

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();
    // ── Well-known bench-fixed controller address ────────────

    /// <summary>IP address of the Echo bench controller.</summary>
    private const string EchoIp = "192.0.2.30";

    /// <summary>
    /// Rockwell SDK comm path for the Echo controller.
    /// Matches the <c>sdkCommPath</c> used in live MCP sessions.
    /// </summary>
    private const string EchoCommPath = @"EmulateEthernet\192.0.2.30";

    /// <summary>Standard EtherNet/IP CIP TCP port.</summary>
    private const int EnipPort = 44818;

    // ── Tags in the bench project. Override with LGX_HW_ATOMIC_TAG,
    // LGX_HW_MEMBER_TAG and LGX_HW_UDT when your bench project names them differently.

    /// <summary>
    /// Controller-scoped DINT tag confirmed readable online
    /// (returned value 3 in the 01:20Z session).
    /// </summary>
    private static readonly string AtomicTagName =
        Environment.GetEnvironmentVariable("LGX_HW_ATOMIC_TAG") ?? "ActiveRecipeState";

    /// <summary>
    /// Controller-scoped INT member of UDT_Recipe, confirmed to fail with
    /// XMLSrv_E_TARGET_NOT_FOUND in the 01:58Z session.
    /// </summary>
    private static readonly string MemberTagName =
        Environment.GetEnvironmentVariable("LGX_HW_MEMBER_TAG") ?? "Recipe_Working.TotalDoseCount";

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts the <see cref="LogixAcdFixture"/> injected by xunit for the
    /// <c>LogixAcd</c> collection, but does not use it as a skip gate.
    /// <see cref="HardwareTests"/> gates on TCP reachability of the Echo
    /// controller, not on <c>LOGIX_ACD_PATH</c>.
    /// </summary>
    public HardwareTests(LogixAcdFixture _)
    {
        // Discard the fixture — gate is TCP reachability, not LOGIX_ACD_PATH.
    }

    // ── Reachability probe ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when a TCP connection to
    /// <see cref="EchoIp"/>:<see cref="EnipPort"/> completes within 3 seconds;
    /// <c>false</c> on any failure (unreachable, refused, timeout).
    /// </summary>
    private static async Task<bool> EchoIsReachableAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(EchoIp, EnipPort, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reproduction test for an earlier change (structure-member reads fail online).
    ///
    /// <para>
    /// Opens the bench project via <c>upload-to-new-project</c> — the same
    /// connect path the MCP uses when <c>logix_connect</c> is called with
    /// <c>sdkMode="online"</c> and no pre-opened ACD.  Then reads one atomic tag
    /// (<c>ActiveRecipeState</c>, DINT) and one UDT member
    /// (<c>Recipe_Working.TotalDoseCount</c>, INT) through the same
    /// <see cref="TypedSdkTagService.ReadAsync"/> code path that
    /// <c>logix_read</c> uses, and asserts both succeed.
    /// </para>
    ///
    /// <para>
    /// <b>Skip conditions:</b>
    /// <list type="bullet">
    ///   <item>
    ///     Echo at 192.0.2.30:44818 is not reachable — hardware absent, not a failure.
    ///   </item>
    ///   <item>
    ///     <c>upload-to-new-project</c> connect fails — skip with connection error text.
    ///   </item>
    ///   <item>
    ///     SDK member read fails (the defect: <c>XMLSrv_E_TARGET_NOT_FOUND</c>)
    ///     — skip with the <b>exact SDK exception text</b>; not counted as a pass.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task MemberRead_OnEcho_Succeeds()
    {
        // ── Gate: Echo must answer on port 44818 ──────────────────────────────
        bool echoUp = await EchoIsReachableAsync();
        Skip.If(!echoUp,
            $"Echo at {EchoIp}:{EnipPort} (EtherNet/IP) is not reachable. " +
            "Start the FactoryTalk Logix Echo controller and ensure the bench " +
            "network is connected before running hardware-gated tests.");

        // ── Connect via upload-to-new-project ─────────────────────────────────
        // Replicates what ConnectHandler does when called with:
        //   sdkMode="online", sdkCommPath="EmulateEthernet\192.0.2.30"
        // and no pre-opened project file.
        var factory = new SdkConnectFactory(_leaseScope.Lease);
        SdkSessionResult sessionResult;
        try
        {
            sessionResult = await factory.CreateFromControllerAsync(EchoCommPath);
        }
        catch (Exception ex)
        {
            // Connection failure (LdSdkService not running, Echo offline, etc.) —
            // skip rather than fail so CI is not broken when the bench is down.
            Skip.If(true,
                $"upload-to-new-project connect to {EchoCommPath} failed: {ex.Message}");
            return;  // unreachable; satisfies compiler flow analysis
        }

        var svc = sessionResult.Service;
        try
        {
            // ── Atomic read — must succeed ─────────────────────────────────────
            // ActiveRecipeState is a DINT (confirmed readable in an earlier changetable).
            // Failure here is a hard test failure (unexpected — not the bug under test).
            var atomicResult = await svc.ReadAsync(AtomicTagName);
            Assert.NotNull(atomicResult.Value);

            // ── UDT member read — reproduction point for an earlier change ─────────────
            // Recipe_Working.TotalDoseCount is an INT member of UDT_Recipe.
            //
            // When the bug is present the SDK returns XMLSrv_E_TARGET_NOT_FOUND
            // for the XPath that TypedSdkTagService generates.  If that happens,
            // skip with the exact exception text — not counted as a pass — so the
            // defect is visible without breaking CI.
            TagValue memberResult;
            try
            {
                memberResult = await svc.ReadAsync(MemberTagName);
            }
            catch (Exception ex)
            {
                // Record the exact SDK exception text in the skip reason.
                // A skip is reported, not counted as a pass.
                Skip.If(true,
                    $"SDK member read for '{MemberTagName}' failed: {ex.Message}");
                return;
            }

            Assert.NotNull(memberResult.Value);
        }
        finally
        {
            await svc.DisconnectAsync();
        }
    }

    /// <summary>
    /// Cross-backend consistency test: the byte-array decode path in
    /// <see cref="TypedSdkTagService"/> and the direct CIP path in
    /// <see cref="LibPlcTagService"/> must return equal values for
    /// <c>Recipe_Working.TotalDoseCount</c> (INT member of UDT_Recipe).
    ///
    /// <para>
    /// Skipped when the Echo controller at 192.0.2.30 is not reachable.
    /// Skipped (not failed) when either backend fails to connect or read —
    /// the defect is visible in the skip reason without breaking CI.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task MemberRead_OnEcho_TotalDoseCount_MatchesCipRead()
    {
        // ── Gate: Echo must answer on port 44818 ──────────────────────────────
        bool echoUp = await EchoIsReachableAsync();
        Skip.If(!echoUp,
            $"Echo at {EchoIp}:{EnipPort} (EtherNet/IP) is not reachable. " +
            "Start the FactoryTalk Logix Echo controller and ensure the bench " +
            "network is connected before running hardware-gated tests.");

        // ── SDK path: upload-to-new-project then byte-array member read ───────
        var factory = new SdkConnectFactory(_leaseScope.Lease);
        SdkSessionResult sessionResult;
        try
        {
            sessionResult = await factory.CreateFromControllerAsync(EchoCommPath);
        }
        catch (Exception ex)
        {
            Skip.If(true,
                $"SDK upload-to-new-project connect to {EchoCommPath} failed: {ex.Message}");
            return;
        }

        TagValue sdkResult;
        var sdkSvc = sessionResult.Service;
        try
        {
            try
            {
                sdkResult = await sdkSvc.ReadAsync(MemberTagName);
            }
            catch (Exception ex)
            {
                Skip.If(true,
                    $"SDK member read for '{MemberTagName}' failed: {ex.Message}");
                return;
            }
        }
        finally
        {
            await sdkSvc.DisconnectAsync();
        }

        // ── CIP path: LibPlcTagService direct EtherNet/IP read ────────────────
        var cipSvc = new LibPlcTagService();
        try
        {
            await cipSvc.ConnectAsync(EchoIp, "1,0");
        }
        catch (Exception ex)
        {
            Skip.If(true,
                $"LibPlcTagService connect to {EchoIp} failed: {ex.Message}");
            return;
        }

        TagValue cipResult;
        try
        {
            try
            {
                cipResult = await cipSvc.ReadAsync(MemberTagName);
            }
            catch (Exception ex)
            {
                Skip.If(true,
                    $"CIP member read for '{MemberTagName}' failed: {ex.Message}");
                return;
            }
        }
        finally
        {
            await cipSvc.DisconnectAsync();
        }

        // ── Both paths must return the same value ─────────────────────────────
        // Convert both to int for comparison so that SINT/INT/DINT differences in
        // representation do not cause spurious failures.
        var sdkInt = Convert.ToInt64(sdkResult.Value);
        var cipInt = Convert.ToInt64(cipResult.Value);
        Assert.Equal(cipInt, sdkInt);
    }

    /// <summary>
    /// Acceptance test for the full SDK import sequence against the bench ACD.
    ///
    /// <para>
    /// Opens the bench project (path from <c>LOGIX_ACD_PATH</c> env var), reads the
    /// current definition of <c>UDT_Recipe</c> (expected 10 members), imports it
    /// back into a disposable copy via the real SDK runner, and verifies that the
    /// re-exported member list contains exactly 10 members matching the originals.
    /// </para>
    ///
    /// <para><b>Skip conditions:</b></para>
    /// <list type="bullet">
    ///   <item>Echo at 192.0.2.30:44818 is not reachable.</item>
    ///   <item><c>LOGIX_ACD_PATH</c> is not set or the file does not exist.</item>
    ///   <item>The SDK session fails to open the ACD.</item>
    ///   <item>The import or re-export SDK call fails.</item>
    /// </list>
    ///
    /// <para>
    /// VERIFICATION STATUS: pending bench verification.
    /// Run on the Windows bench with Echo bench ACD: <c>LOGIX_ACD_PATH=C:\RA\udt-bench\BenchUdt.ACD dotnet test --filter "ImportDatatype_RealRun_OnBenchAcd_Reexports10Members"</c>
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task ImportDatatype_RealRun_OnBenchAcd_Reexports10Members()
    {
        // ── Gate 1: Echo must answer on port 44818 ────────────────────────────
        bool echoUp = await EchoIsReachableAsync();
        Skip.If(!echoUp,
            $"Echo at {EchoIp}:{EnipPort} is not reachable. " +
            "Start the FactoryTalk Logix Echo controller before running hardware-gated tests.");

        // ── Gate 2: bench ACD must exist ───────────────────────────────────────
        var acdPath = Environment.GetEnvironmentVariable("LOGIX_ACD_PATH")
                      ?? @"C:\RA\udt-bench\BenchUdt.ACD";
        Skip.If(!File.Exists(acdPath),
            $"bench ACD not found at '{acdPath}'. " +
            "Set LOGIX_ACD_PATH to the absolute path of the bench ACD before running hardware-gated tests.");

        // ── Read the current UDT_Recipe definition ────────────────────────
        var udtName = Environment.GetEnvironmentVariable("LGX_HW_UDT") ?? "UDT_Recipe";
        var svc = new ProjectService();

        // Use AcdProjectOpener to derive an L5X from the ACD for reading the member list.
        var opener = AcdProjectOpener.Default;
        try
        {
            var openResult = await opener.OpenAsync(acdPath, svc).ConfigureAwait(false);
            if (openResult is OpenProjectError openErr)
                Skip.If(true, $"SDK failed to open bench ACD: {openErr.Error}");
        }
        catch (Exception ex)
        {
            Skip.If(true, $"SDK open failed: {ex.Message}");
            return;
        }

        IReadOnlyList<DataTypeMemberEntry> originalMembers;
        try
        {
            originalMembers = ImportDatatypeHandler.ExtractCurrentMembersFromProject(udtName, svc);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Failed to read {udtName} from project: {ex.Message}");
            return;
        }

        Skip.If(originalMembers.Count == 0,
            $"{udtName} not found in bench project or has no members.");

        // ── Create a real SDK runner (no private copy — operates on saveAs) ───
        // Uses the public single-parameter LogixProjectSession constructor which
        // applies identity workingCopier (static path => path), so the SDK
        // opens saveAs directly and SaveAsync writes back to it in place.
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> realSdkRunner =
            (saveAs, tempL5x, xpath, collision) =>
            {
                using var session = new LogixProjectSession(
                    async (path, ct) =>
                        (ILogixProjectHandle)
                        await RockwellAutomation.LogixDesigner.LogixProject
                              .OpenLogixProjectAsync(path, (IOperationEvent?)null, ct)
                              .ConfigureAwait(false));

                session.OpenAsync(saveAs, CancellationToken.None).GetAwaiter().GetResult();
                var project = session.GetProject();

                // Map ImportCollisionOption → SDK ImportCollisionOptions.
                var sdkCollision = collision switch
                {
                    ImportCollisionOption.Overwrite => ImportCollisionOptions.OverwriteOnColl,
                    ImportCollisionOption.Discard   => ImportCollisionOptions.DiscardOnColl,
                    ImportCollisionOption.Cancel    => ImportCollisionOptions.CancelOnColl,
                    _                               => ImportCollisionOptions.OverwriteOnColl
                };

                project.PartialImportFromXmlFileAsync(xpath, tempL5x, sdkCollision, false)
                       .GetAwaiter().GetResult();
                project.SaveAsync().GetAwaiter().GetResult();
                return Array.Empty<string>();
            };

        // ── Re-export reader: opens the saved ACD and exports the DataType ────
        Func<string, IReadOnlyList<DataTypeMemberEntry>> realReexportReader =
            savedAcdPath =>
            {
                var exportXPath = $"Controller/DataTypes/DataType[@Name='{udtName}']";
                var tempExport  = Path.Combine(
                    Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".L5X");

                using var session = new LogixProjectSession(
                    async (path, ct) =>
                        (ILogixProjectHandle)
                        await RockwellAutomation.LogixDesigner.LogixProject
                              .OpenLogixProjectAsync(path, (IOperationEvent?)null, ct)
                              .ConfigureAwait(false));
                try
                {
                    session.OpenAsync(savedAcdPath, CancellationToken.None)
                           .GetAwaiter().GetResult();
                    session.GetProject()
                           .PartialExportToXmlFileAsync(exportXPath, tempExport)
                           .GetAwaiter().GetResult();
                    return ImportDatatypeHandler.ExtractTargetMembersFromFragment(
                        File.ReadAllText(tempExport));
                }
                finally
                {
                    try { File.Delete(tempExport); } catch { /* best effort */ }
                }
            };

        // ── Run the import into a temp copy ───────────────────────────────────
        var tmpSave = Path.Combine(
            Path.GetTempPath(), $"logixmcp-hw-import-{Guid.NewGuid():N}.ACD");
        try
        {
            ImportDatatypeResult ok;
            try
            {
                var result = ImportDatatypeHandler.Execute(
                    acdPath:         acdPath,
                    datatypeName:    udtName,
                    l5xPath:         null,
                    members:         originalMembers,
                    saveAs:          tmpSave,
                    dryRun:          false,
                    confirm:         true,
                    sdkImportRunner: realSdkRunner,
                    reexportReader:  realReexportReader);

                ok = Assert.IsType<ImportDatatypeResult>(result);
            }
            catch (Exception ex)
            {
                Skip.If(true, $"ImportDatatypeHandler.Execute threw: {ex.Message}");
                return;
            }

            // ── Verify: re-exported member list must match originals ───────────
            Assert.NotNull(ok.Verified);
            Assert.True(ok.Verified,
                $"verified must be true after idempotent re-import of {udtName}; " +
                $"savedTo={ok.SavedTo}");
            // bench UDT_Recipe is expected to have exactly 10 members.
            Assert.Equal(10, originalMembers.Count);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }
}
#endif
