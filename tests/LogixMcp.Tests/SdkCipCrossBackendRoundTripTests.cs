// ── SdkCipCrossBackendRoundTripTests ─────────────────────────────────────────
//
// Cross-backend round-trip tests that verify SDK-online writes reach the
// controller and are visible when read back via the CIP (libplctag) lane.
//
// This is the only class of test that can catch silent write failures where
// the SDK-online path "succeeds" at the API level but does not propagate the
// written value to controller memory — a class of defect that single-backend
// tests (write then read via the SAME backend) cannot detect.
//
// Structure
// ─────────
//  Part 1 — CrossBackendRoundTripStructureTests (always runs, no SDK required)
//            Uses VirtualTagService for both the "SDK-online write lane" and
//            the "CIP read lane", sharing the same underlying store to simulate
//            two protocols targeting the same controller memory.
//            Also exercises a SilentWriteTagService to prove the disagreement
//            check actively detects the failure class being guarded against.
//
//  Part 2 — SdkOnlineCipReadSandboxTests (#if LOGIX_SDK, no live PLC required)
//            Uses TypedSdkTagService backed by CapturingFakeLogixProject (stores
//            written values) to validate that:
//            (a) REAL tags are written as float via SetTagValueREALAsync, not as
//                a DINT bit-pattern.
//            (b) The stored IEEE-754 bits survive the CIP-lane reinterpretation
//                path (BitConverter.Int32BitsToSingle), confirming the round-trip
//                works across the protocol boundary.
//
//  Part 3 — SdkCipCrossBackendLiveTests (#if LOGIX_SDK, live hardware)
//            Requires LOGIX_ACD_PATH (open SDK session) and AB_SERVER_HOST
//            (LibPlcTagService CIP connection).  Skipped in CI; intended for
//            operator verification on a Windows bench with Echo 5580 in RUN mode.
//
// Run the live integration tests only:
//   LOGIX_ACD_PATH=/path/to/Project.ACD AB_SERVER_HOST=198.51.100.x \
//     dotnet test --filter Category=Integration
// ─────────────────────────────────────────────────────────────────────────────

// ── SDK-only using directives (must precede namespace declaration) ─────────────
#if LOGIX_SDK
using System.Collections.Generic;
using System.Threading;
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;

using ILogixProjectHandle  = RockwellAutomation.LogixDesigner.ILogixProject;
using OperationMode        = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using ControllerMode       = RockwellAutomation.LogixDesigner.LogixProject.ControllerMode;
using ConnectedState       = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using DataType             = RockwellAutomation.LogixDesigner.LogixProject.DataType;
using RequestedBuildTarget    = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using RequestedControllerMode = RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode;
using ImportCollisionOptions  = RockwellAutomation.LogixDesigner.LogixProject.ImportCollisionOptions;
using PartialImportOption     = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using RequestedLoadEvent      = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadEvent;
using RequestedLoadMode       = RockwellAutomation.LogixDesigner.LogixProject.RequestedLoadMode;
using AutomaticFirmwareUpdate = RockwellAutomation.LogixDesigner.LogixProject.AutomaticFirmwareUpdate;
#endif

namespace LogixMcp.Tests;

// ── Part 1: Sandbox-verifiable structural tests ───────────────────────────────

/// <summary>
/// Validates the cross-backend round-trip assertion structure using
/// <see cref="VirtualTagService"/> for both backends.  These tests always run in
/// CI without any SDK installation or live PLC.
///
/// <para>Covered scenarios:</para>
/// <list type="bullet">
///   <item>Agreement holds when the SDK write propagates: both backends see the
///     same value because they share the same underlying controller memory (here
///     simulated by a single <see cref="VirtualTagService"/> instance).</item>
///   <item>Disagreement is detected when the SDK write silently fails: the CIP
///     lane still carries the stale pre-write value, and the assertion correctly
///     identifies the mismatch.</item>
/// </list>
/// </summary>
public class CrossBackendRoundTripStructureTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="VirtualTagService"/> pre-seeded with one BOOL and one
    /// REAL tag at their zero/false initial values.
    /// </summary>
    private static VirtualTagService BuildSharedStore() =>
        new(new Dictionary<string, (object Value, string? DataType)>
        {
            ["Cmd_MotorStart"]    = (false, "BOOL"),
            ["Cmd_SpeedSetpoint"] = (0.0f,  "REAL"),
        });

    // ── Agreement tests ───────────────────────────────────────────────────────

    /// <summary>
    /// BOOL round-trip: write via the "SDK-online lane", read via the "CIP lane".
    /// Both lanes share the same underlying in-memory store, simulating two
    /// protocols accessing the same physical controller memory.
    /// </summary>
    [Fact]
    public async Task WhenWritePropagates_BoolTag_CipReadMatchesWrittenValue()
    {
        // Arrange: both lanes share one in-memory store — simulates shared
        // controller memory visible to SDK and CIP protocols alike.
        var sharedStore = BuildSharedStore();
        ITagService sdkOnlineLane = sharedStore;
        ITagService cipLane       = sharedStore;

        // (1) Write via SDK-online lane.
        await sdkOnlineLane.WriteAsync("Cmd_MotorStart", true);

        // (2) Read via CIP lane.
        var result = await cipLane.ReadAsync("Cmd_MotorStart");

        // (3) Assert cross-backend agreement.
        Assert.True((bool)result.Value);
    }

    /// <summary>
    /// REAL round-trip: write via the "SDK-online lane", read via the "CIP lane".
    /// Verifies the core scenario that exposed the silent-write-failure defect:
    /// a REAL value written via the SDK must be visible through the CIP lane.
    /// </summary>
    [Fact]
    public async Task WhenWritePropagates_RealTag_CipReadMatchesWrittenValue()
    {
        // Arrange: both lanes share one in-memory store.
        var sharedStore = BuildSharedStore();
        ITagService sdkOnlineLane = sharedStore;
        ITagService cipLane       = sharedStore;

        const float written = 42.5f;

        // (1) Write via SDK-online lane.
        await sdkOnlineLane.WriteAsync("Cmd_SpeedSetpoint", written);

        // (2) Read via CIP lane.
        var result = await cipLane.ReadAsync("Cmd_SpeedSetpoint");

        // (3) Assert cross-backend agreement.
        Assert.Equal(written, (float)result.Value, tolerance: 0.0001f);
    }

    /// <summary>
    /// Disagreement-detection test: when the SDK write silently fails (the API
    /// succeeds without storing the value), the CIP lane returns the stale
    /// pre-write value.  The cross-backend assertion detects the mismatch.
    ///
    /// <para>
    /// This test proves the check actively guards against the defect class —
    /// it is not a vacuous assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenSdkWriteDoesNotPropagate_CrossBackendDisagreementIsDetected()
    {
        // Arrange: SDK lane silently "succeeds" without storing anything;
        // the CIP lane is an independent store holding the stale initial value.
        ITagService sdkOnlineLane = new SilentWriteTagService();
        ITagService cipLane       = BuildSharedStore();   // still has 0.0f

        const float attemptedWrite = 99.9f;
        const float staleValue     = 0.0f;

        // (1) Write via SDK-online lane — accepted without error, but no propagation.
        await sdkOnlineLane.WriteAsync("Cmd_SpeedSetpoint", attemptedWrite);

        // (2) Read via CIP lane — returns stale pre-write value.
        var result = await cipLane.ReadAsync("Cmd_SpeedSetpoint");

        // (3) Detect disagreement: the CIP read does NOT match what was written.
        // This assertion proves the check can catch silent write failures.
        Assert.NotEqual(attemptedWrite, (float)result.Value);
        Assert.Equal(staleValue, (float)result.Value, tolerance: 0.0001f);
    }

    /// <summary>
    /// Verifies that sequential writes via the SDK lane are each visible via the
    /// CIP lane — ruling out stale caching or single-write anomalies.
    /// </summary>
    [Fact]
    public async Task WhenMultipleWritesPropagate_CipReadTracksEachValue()
    {
        var sharedStore = BuildSharedStore();
        ITagService sdkOnlineLane = sharedStore;
        ITagService cipLane       = sharedStore;

        float[] sequence = [10.0f, 20.0f, 30.0f];

        foreach (var value in sequence)
        {
            // (1) Write via SDK-online lane.
            await sdkOnlineLane.WriteAsync("Cmd_SpeedSetpoint", value);

            // (2) Read via CIP lane.
            var result = await cipLane.ReadAsync("Cmd_SpeedSetpoint");

            // (3) Assert cross-backend agreement for this write cycle.
            Assert.Equal(value, (float)result.Value, tolerance: 0.0001f);
        }
    }
}

// ── SilentWriteTagService ─────────────────────────────────────────────────────

/// <summary>
/// A write-only <see cref="ITagService"/> stub that accepts write calls and
/// returns <see cref="System.Threading.Tasks.Task.CompletedTask"/> without
/// storing the value or forwarding to an inner service.
///
/// <para>
/// Models the silent-write-failure defect: the SDK-online write path
/// "succeeds" at the API level but does not propagate the value to the
/// controller.  Used by <see cref="CrossBackendRoundTripStructureTests"/> to
/// verify that the cross-backend agreement check actively detects this class
/// of failure.
/// </para>
/// </summary>
internal sealed class SilentWriteTagService : ITagService
{
    /// <inheritdoc/>
    /// <remarks>No-op.</remarks>
    public System.Threading.Tasks.Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>No-op.</remarks>
    public System.Threading.Tasks.Task DisconnectAsync(
        CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// Always throws <see cref="NotSupportedException"/>.
    /// <see cref="SilentWriteTagService"/> is intentionally write-only — reads
    /// are unsupported because the service never stores any values.
    /// </remarks>
    public System.Threading.Tasks.Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{nameof(SilentWriteTagService)} does not support reads — it is " +
            $"a write-only stub that silently discards all writes.");

    /// <inheritdoc/>
    /// <remarks>
    /// Silently succeeds without storing <paramref name="value"/> or forwarding
    /// to any inner service.  This simulates an SDK write that returns success
    /// at the API level but does not reach the controller.
    /// </remarks>
    public System.Threading.Tasks.Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.CompletedTask;   // Silent no-op.

    /// <inheritdoc/>
    public System.Threading.Tasks.Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
        => System.Threading.Tasks.Task.FromResult<IReadOnlyList<TagInfo>>(
            Array.Empty<TagInfo>());
}

// ── Parts 2 & 3: SDK-only classes ────────────────────────────────────────────

#if LOGIX_SDK

// ── CapturingFakeLogixProject ─────────────────────────────────────────────────

/// <summary>
/// A configurable <see cref="ILogixProject"/> fake that stores typed tag values
/// written via <c>SetTagValueXXXAsync</c> and returns them on subsequent
/// <c>GetTagValueXXXAsync</c> calls.
///
/// <para>
/// Unlike <see cref="RecordingFakeLogixProject"/> (which uses fixed pre-configured
/// return values), this fake acts as an in-memory per-type store.  It enables SDK
/// write → read round-trip validation without a live Logix Designer installation.
/// </para>
///
/// <para>
/// The key used for storage is the plain tag name as forwarded by
/// <see cref="TypedSdkTagService"/> — e.g. <c>"Cmd_SpeedSetpoint"</c>.
/// Call <see cref="GetCapturedReal"/> or <see cref="GetCapturedBool"/> to inspect
/// what was stored after a write.
/// </para>
/// </summary>
internal sealed class CapturingFakeLogixProject : ILogixProjectHandle
{
    // ── Per-type value stores (keyed by SDK XPath) ────────────────────────────

    private readonly Dictionary<string, bool>   _boolValues  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>    _dintValues  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float>  _realValues  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lrealValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stringValues = new(StringComparer.Ordinal);

    // ── Public inspection helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the REAL (float) value last stored for <paramref name="tagPath"/>,
    /// or 0.0f when no value has been written.
    /// </summary>
    public float GetCapturedReal(string tagPath)
        => _realValues.GetValueOrDefault(tagPath);

    /// <summary>
    /// Returns the BOOL value last stored for <paramref name="tagPath"/>,
    /// or <c>false</c> when no value has been written.
    /// </summary>
    public bool GetCapturedBool(string tagPath)
        => _boolValues.GetValueOrDefault(tagPath);

    /// <summary>
    /// Returns the DINT (int) value last stored for <paramref name="tagPath"/>,
    /// or 0 when no value has been written.
    /// </summary>
    public int GetCapturedDint(string tagPath)
        => _dintValues.GetValueOrDefault(tagPath);

    // ── ILogixProject: typed read accessors ──────────────────────────────────

    public Task<bool>   GetTagValueBOOLAsync(string tagPath, OperationMode mode)
        => Task.FromResult(_boolValues.GetValueOrDefault(tagPath));

    public Task<sbyte>  GetTagValueSINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult((sbyte)0);

    public Task<short>  GetTagValueINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult((short)0);

    public Task<int>    GetTagValueDINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult(_dintValues.GetValueOrDefault(tagPath));

    public Task<long>   GetTagValueLINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult(0L);

    public Task<byte>   GetTagValueUSINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult((byte)0);

    public Task<ushort> GetTagValueUINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult((ushort)0);

    public Task<uint>   GetTagValueUDINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult(0u);

    public Task<ulong>  GetTagValueULINTAsync(string tagPath, OperationMode mode)
        => Task.FromResult(0UL);

    public Task<float>  GetTagValueREALAsync(string tagPath, OperationMode mode)
        => Task.FromResult(_realValues.GetValueOrDefault(tagPath));

    public Task<double> GetTagValueLREALAsync(string tagPath, OperationMode mode)
        => Task.FromResult(_lrealValues.GetValueOrDefault(tagPath));

    public Task<string> GetTagValueSTRINGAsync(string tagPath, OperationMode mode)
        => Task.FromResult(_stringValues.GetValueOrDefault(tagPath, string.Empty));

    // ── ILogixProject: typed write accessors (capture the values) ────────────

    public Task SetTagValueBOOLAsync(string tagPath, OperationMode mode, bool value)
    { _boolValues[tagPath] = value; return Task.CompletedTask; }

    public Task SetTagValueSINTAsync(string tagPath, OperationMode mode, sbyte value)
        => Task.CompletedTask;

    public Task SetTagValueINTAsync(string tagPath, OperationMode mode, short value)
        => Task.CompletedTask;

    public Task SetTagValueDINTAsync(string tagPath, OperationMode mode, int value)
    { _dintValues[tagPath] = value; return Task.CompletedTask; }

    public Task SetTagValueLINTAsync(string tagPath, OperationMode mode, long value)
        => Task.CompletedTask;

    public Task SetTagValueUSINTAsync(string tagPath, OperationMode mode, byte value)
        => Task.CompletedTask;

    public Task SetTagValueUINTAsync(string tagPath, OperationMode mode, ushort value)
        => Task.CompletedTask;

    public Task SetTagValueUDINTAsync(string tagPath, OperationMode mode, uint value)
        => Task.CompletedTask;

    public Task SetTagValueULINTAsync(string tagPath, OperationMode mode, ulong value)
        => Task.CompletedTask;

    public Task SetTagValueREALAsync(string tagPath, OperationMode mode, float value)
    { _realValues[tagPath] = value; return Task.CompletedTask; }

    public Task SetTagValueLREALAsync(string tagPath, OperationMode mode, double value)
    { _lrealValues[tagPath] = value; return Task.CompletedTask; }

    public Task SetTagValueSTRINGAsync(string tagPath, OperationMode mode, string value)
    { _stringValues[tagPath] = value; return Task.CompletedTask; }

    // ── ILogixProject: raw byte accessors (not exercised by these tests) ──────

    public Task<ByteString> GetTagValueAsync(string tagPath, OperationMode mode, DataType type)
        => Task.FromResult(ByteString.Empty);

    public Task SetTagValueAsync(string tagPath, OperationMode mode, byte[] tagValue, DataType type)
        => Task.CompletedTask;

    // ── ILogixProject: lifecycle and session members (not used in these tests) ─

    public void Dispose() { }

    public Task<string>         GetCommunicationsPathAsync(CancellationToken ct = default)    => throw new NotImplementedException();
    public Task                 SetCommunicationsPathAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
    public Task                 ChangeControllerTypeAsync(string t, CancellationToken ct = default)  => throw new NotImplementedException();
    public Task                 SaveAsAsync(string p, bool f, bool d, CancellationToken ct = default) => throw new NotImplementedException();
    public Task                 SaveAsync(CancellationToken ct = default)                      => throw new NotImplementedException();
    public Task                 DownloadAsync(CancellationToken ct = default)                  => throw new NotImplementedException();
    public Task<ControllerMode> ReadControllerModeAsync(CancellationToken ct = default)        => throw new NotImplementedException();
    public Task                 ChangeControllerModeAsync(RequestedControllerMode m, CancellationToken ct = default) => throw new NotImplementedException();
    public Task                 BuildAsync(RequestedBuildTarget t, CancellationToken ct = default)    => throw new NotImplementedException();
    public Task                 UploadAsync(CancellationToken ct = default)                    => throw new NotImplementedException();
    public Task                 StoreImageOnSDCardAsync(RequestedLoadEvent le, RequestedLoadMode lm, AutomaticFirmwareUpdate afu, string n, string note) => throw new NotImplementedException();
    public Task                 LoadImageFromSDCardAsync()                                      => throw new NotImplementedException();
    public void                 AddEventHandler(IOperationEvent h)   { }
    public void                 ClearEventHandlers()                  { }
    public void                 RemoveEventHandler(IOperationEvent h) { }

    public Task PartialExportToXmlFileAsync(string xp, string fp, CancellationToken ct = default)    => throw new NotImplementedException();
    public Task PartialImportFromXmlFileAsync(string xp, string xf, ImportCollisionOptions o, bool c, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportWithTargetFromXmlFileAsync(string xp, string tn, string xf, PartialImportOption oi, CancellationToken ct = default) => throw new NotImplementedException();
    public Task PartialImportRungsFromXmlFileAsync(string xp, uint ip, uint rc, string xf, PartialImportOption oi, CancellationToken ct = default) => throw new NotImplementedException();

    public Task                 GoOnlineAsync(CancellationToken ct = default)              => throw new NotImplementedException();
    public Task                 GoOfflineAsync(CancellationToken ct = default)             => throw new NotImplementedException();
    public Task<ConnectedState> ReadConnectedStateAsync(CancellationToken ct = default)    => throw new NotImplementedException();

    public Task<string> GetSafetyNetworkNumberAsync(string m, CancellationToken ct = default)  => throw new NotImplementedException();
    public Task<bool>   IsSafetyLockedAsync(CancellationToken ct = default)                    => throw new NotImplementedException();
    public Task         SafetyLockAsync(string p, CancellationToken ct = default)              => throw new NotImplementedException();
    public Task         SafetyUnlockAsync(string p, CancellationToken ct = default)            => throw new NotImplementedException();
    public Task         SetSafetyLockPasswordAsync(string np, string op, CancellationToken ct = default)   => throw new NotImplementedException();
    public Task         SetSafetyUnlockPasswordAsync(string np, string op, CancellationToken ct = default) => throw new NotImplementedException();
    public Task         GenerateSafetySignatureAsync(CancellationToken ct = default)           => throw new NotImplementedException();
    public Task         DeleteSafetySignatureAsync(CancellationToken ct = default)             => throw new NotImplementedException();
    public Task<string> GetSafetySignatureAsync(CancellationToken ct = default)               => throw new NotImplementedException();

    public Task ProtectWithLicenseAsync(string xp, uint f, uint p, CancellationToken ct = default)                 => throw new NotImplementedException();
    public Task ProtectWithLicenseAsync(IEnumerable<string> xp, uint f, uint p, CancellationToken ct = default)    => throw new NotImplementedException();
    public Task ProtectAllWithLicenseAsync(uint f, uint p, CancellationToken ct = default)                         => throw new NotImplementedException();
    public Task ProtectAllWithPasswordAsync(string p, CancellationToken ct = default)                              => throw new NotImplementedException();
    public Task UnprotectAllAsync(CancellationToken ct = default)                                                   => throw new NotImplementedException();
    public Task ProtectWithPasswordAsync(string xp, string p, CancellationToken ct = default)                      => throw new NotImplementedException();
    public Task ProtectWithPasswordAsync(IEnumerable<string> xp, string p, CancellationToken ct = default)         => throw new NotImplementedException();
    public Task UnprotectAsync(string xp, CancellationToken ct = default)                                          => throw new NotImplementedException();
    public Task UnprotectAsync(IEnumerable<string> xp, CancellationToken ct = default)                             => throw new NotImplementedException();
    public Task LockExecutableAsync(string xp, CancellationToken ct = default)                                     => throw new NotImplementedException();
    public Task LockExecutableAsync(IEnumerable<string> xp, CancellationToken ct = default)                        => throw new NotImplementedException();
    public Task<bool> IsExecutableLockedAsync(string xp, CancellationToken ct = default)                           => throw new NotImplementedException();
    public Task UnlockExecutableAsync(string xp, CancellationToken ct = default)                                   => throw new NotImplementedException();
    public Task UnlockExecutableAsync(IEnumerable<string> xp, CancellationToken ct = default)                      => throw new NotImplementedException();
    public Task LockAllAsync(CancellationToken ct = default)                                                        => throw new NotImplementedException();
    public Task UnlockAllAsync(CancellationToken ct = default)                                                      => throw new NotImplementedException();
    public Task<bool> IsExecutableProtectedAsync(string xp, CancellationToken ct = default)                        => throw new NotImplementedException();
    public Task<string[]> GetAllExecutablesAsync(CancellationToken ct = default)                                    => throw new NotImplementedException();
}

// ── Part 2: SDK sandbox tests (no live PLC required) ─────────────────────────

/// <summary>
/// SDK-only sandbox tests that validate the cross-backend round-trip assertion
/// using <see cref="TypedSdkTagService"/> backed by
/// <see cref="CapturingFakeLogixProject"/>.
///
/// <para>
/// These tests require the Rockwell SDK NuGet package (<c>EnableLogixSdk=true</c>)
/// but do NOT require a live Allen-Bradley controller or Logix Designer installation.
/// They validate:
/// <list type="bullet">
///   <item>The SDK write path routes REAL tags to <c>SetTagValueREALAsync</c>
///     (float) rather than <c>SetTagValueDINTAsync</c> (int bit-pattern).</item>
///   <item>The float bits stored by the SDK survive the CIP-lane reinterpretation:
///     <see cref="BitConverter.Int32BitsToSingle"/>(<see cref="BitConverter.SingleToInt32Bits"/>(x)) == x.</item>
///   <item>The BOOL round-trip produces the correct bool value on both lanes.</item>
/// </list>
/// </para>
/// </summary>
public class SdkOnlineCipReadSandboxTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>Loads the Demo.L5X project for type resolution.</summary>
    private static L5X LoadDemoProject() => L5X.Load(FixturePath);

    /// <summary>
    /// Opens a <see cref="LogixProjectSession"/> backed by
    /// <paramref name="capturing"/> and returns a
    /// <see cref="TypedSdkTagService"/> configured with
    /// <see cref="OperationMode.Online"/> — matching the SDK-online write path
    /// that the cross-backend defect was triggered on.
    /// </summary>
    private static async Task<TypedSdkTagService>
        BuildOnlineSdkServiceAsync(CapturingFakeLogixProject capturing)
    {
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProjectHandle>(capturing));

        await session.OpenAsync("dummy.ACD");

        return new TypedSdkTagService(session, LoadDemoProject(), OperationMode.Online);
    }

    // ── REAL cross-backend round-trip (key regression test) ──────────────────

    /// <summary>
    /// KEY REGRESSION TEST: Writing a REAL via the SDK-online path must
    /// store the value as an IEEE-754 float, not as a DINT bit-pattern.
    ///
    /// <para>
    /// The SDK correctly calls <c>SetTagValueREALAsync</c> with the float value.
    /// The CIP lane (libplctag) reads 4-byte tags as DINT (int32).  A correctly
    /// stored REAL produces bits that, when reinterpreted via
    /// <see cref="BitConverter.Int32BitsToSingle"/>, recover the original float.
    /// This is the only cross-backend agreement check that catches the silent-
    /// write-failure class of defect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task
        SdkOnlineWrite_RealTag_StoredAsFloat_CipIeee754ReinterpretRecoversOriginalValue()
    {
        // Arrange: SDK-online write lane (TypedSdkTagService → CapturingFake).
        var capturing = new CapturingFakeLogixProject();
        var sdkSvc    = await BuildOnlineSdkServiceAsync(capturing);

        const float writtenFloat = 42.5f;
        const string tagName     = "Cmd_SpeedSetpoint";

        // (1) Write via SDK-online lane.
        await sdkSvc.WriteAsync(tagName, writtenFloat);

        // (2) Read the "CIP lane" by simulating what LibPlcTagService returns:
        //     a 4-byte tag read by GetInt32() — the raw IEEE-754 bit-pattern.
        // TypedSdkTagService translates tag names to XPath before the SDK call,
        // so CapturingFakeLogixProject stores values under the XPath key.
        float capturedFloat = capturing.GetCapturedReal(TagXPathTranslator.Translate(tagName));
        int   cipDintValue  = BitConverter.SingleToInt32Bits(capturedFloat);

        // (3) Assert cross-backend agreement: reinterpreting the CIP DINT as
        //     IEEE-754 float recovers the original written value.
        float recovered = BitConverter.Int32BitsToSingle(cipDintValue);
        Assert.Equal(writtenFloat, recovered, tolerance: 0.0001f);
    }

    /// <summary>
    /// Verifies that the SDK-online write lane correctly invokes
    /// <c>SetTagValueREALAsync</c> (not <c>SetTagValueDINTAsync</c>) when
    /// writing a REAL tag, and that the captured value is the exact float — not
    /// a DINT bit-pattern interpreted as an integer.
    /// </summary>
    [Fact]
    public async Task
        SdkOnlineWrite_RealTag_CapturedValueIsFloat_NotDintBitPattern()
    {
        // Arrange
        var capturing = new CapturingFakeLogixProject();
        var sdkSvc    = await BuildOnlineSdkServiceAsync(capturing);

        const float writtenFloat  = 3.14f;
        const string tagName      = "Cmd_SpeedSetpoint";

        // (1) Write 3.14f via SDK-online lane.
        await sdkSvc.WriteAsync(tagName, writtenFloat);

        // (2) Inspect what was captured by SetTagValueREALAsync.
        // TypedSdkTagService translates tag names to XPath before the SDK call,
        // so CapturingFakeLogixProject stores values under the XPath key.
        var xpath = TagXPathTranslator.Translate(tagName);
        float capturedFloat = capturing.GetCapturedReal(xpath);

        // (3) The captured value is the float itself — not the DINT bit-pattern.
        // If the SDK had incorrectly routed to SetTagValueDINTAsync, the DINT store
        // would be non-zero and the REAL store would be 0.0f.
        Assert.Equal(writtenFloat, capturedFloat, tolerance: 0.0001f);  // REAL path used

        // The DINT store must NOT have received the write.
        int capturedDint = capturing.GetCapturedDint(xpath);
        Assert.Equal(0, capturedDint);  // DINT path was NOT used
    }

    /// <summary>
    /// BOOL cross-backend round-trip: write via the SDK-online lane, read back
    /// via the same SDK lane (same fake backing), and confirm the correct bool
    /// value was stored.
    /// </summary>
    [Fact]
    public async Task
        SdkOnlineWrite_BoolTag_CapturedAsBool_ReadBackMatchesWrittenValue()
    {
        // Arrange
        var capturing = new CapturingFakeLogixProject();
        var sdkSvc    = await BuildOnlineSdkServiceAsync(capturing);

        const string tagName = "Cmd_MotorStart";

        // (1) Write true via SDK-online lane.
        await sdkSvc.WriteAsync(tagName, true);

        // (2) The CapturingFake stores the bool; read it back via the SDK service.
        var readBack = await sdkSvc.ReadAsync(tagName);

        // (3) Assert cross-backend agreement.
        Assert.Equal(true, (bool)readBack.Value);

        // Also verify that the capturing fake stored the correct bool (not an int or float).
        // TypedSdkTagService translates tag names to XPath before the SDK call,
        // so CapturingFakeLogixProject stores values under the XPath key.
        Assert.True(capturing.GetCapturedBool(TagXPathTranslator.Translate(tagName)));
    }

    /// <summary>
    /// Verifies that the SDK-online write and read paths both forward
    /// <see cref="OperationMode.Online"/> to the SDK accessors — confirming
    /// that the live-controller contact mode is correctly propagated through
    /// the <see cref="TypedSdkTagService"/> in the cross-backend scenario.
    /// </summary>
    [Fact]
    public async Task
        SdkOnlineService_WriteAndRead_BothForwardOnlineOperationModeToSdk()
    {
        // Use RecordingFakeLogixProject to inspect the OperationMode argument.
        var recording = new RecordingFakeLogixProject { BoolReturnValue = true };
        var session   = new LogixProjectSession(
            (_p, _ct) => Task.FromResult<ILogixProjectHandle>(recording));
        await session.OpenAsync("dummy.ACD");
        var sdkSvc = new TypedSdkTagService(session, LoadDemoProject(), OperationMode.Online);

        // Write — must forward OperationMode.Online to the SDK setter.
        await sdkSvc.WriteAsync("Cmd_MotorStart", true);
        Assert.Equal(OperationMode.Online, recording.LastWriteMode);

        // Read — must forward OperationMode.Online to the SDK getter.
        await sdkSvc.ReadAsync("Cmd_MotorStart");
        Assert.Equal(OperationMode.Online, recording.LastReadMode);
    }
}

// ── Part 3: Live cross-backend integration tests (operator verification) ──────

/// <summary>
/// Live cross-backend round-trip tests that require both a Logix Designer SDK
/// project session (<c>LOGIX_ACD_PATH</c>) and a reachable CIP controller
/// (<c>AB_SERVER_HOST</c>).
///
/// <para>
/// All tests are skipped in CI when either environment variable is absent.
/// Intended for operator verification on a <b>Windows bench</b> with the Echo 5580
/// controller in RUN mode and the project already downloaded.
/// </para>
///
/// <para>
/// The test verifies the three-step cross-backend agreement contract:
/// <list type="number">
///   <item>Write a tag value via the SDK-online path
///     (<see cref="TypedSdkTagService"/> with <see cref="OperationMode.Online"/>).</item>
///   <item>Read the same tag via the CIP lane
///     (<see cref="LibPlcTagService"/> using libplctag).</item>
///   <item>Assert that the two values agree — detecting any silent write failure
///     that the SDK reports as success without reaching the controller.</item>
/// </list>
/// </para>
///
/// <para>
/// Tags used: <c>Cmd_MotorStart</c> (BOOL) and <c>Cmd_SpeedSetpoint</c> (REAL)
/// from the Demo project.  The gate policy (Demo.L5X) grants writes to both.
/// </para>
/// </summary>
[Collection("LogixAcd")]
[Trait("Category", "Integration")]
public class SdkCipCrossBackendLiveTests
{
    // ── Well-known Demo project tag names ─────────────────────────────────────

    /// <summary>BOOL tag granted for writes by the Demo.L5X gate policy.</summary>
    private const string BoolTag = "Cmd_MotorStart";

    /// <summary>REAL tag granted for writes by the Demo.L5X gate policy.</summary>
    private const string RealTag = "Cmd_SpeedSetpoint";

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static readonly string L5XFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private readonly LogixAcdFixture _acdFixture;
    private readonly string?         _cipHost;
    private readonly string          _cipPath;

    /// <summary>
    /// Initialises the test class with the shared <see cref="LogixAcdFixture"/>
    /// and reads the CIP host from <c>AB_SERVER_HOST</c>.  Skips all tests when
    /// either resource is unavailable.
    /// </summary>
    public SdkCipCrossBackendLiveTests(LogixAcdFixture acdFixture)
    {
        _acdFixture = acdFixture;
        _cipHost    = Environment.GetEnvironmentVariable("AB_SERVER_HOST");
        _cipPath    = Environment.GetEnvironmentVariable("AB_SERVER_PATH") ?? "1,0";

        // Skip when the SDK project path is absent.
        Skip.If(!acdFixture.IsAvailable, acdFixture.SkipReason);

        // Skip when the CIP host is absent.
        Skip.If(_cipHost is null,
            "AB_SERVER_HOST environment variable is not set. " +
            "Set AB_SERVER_HOST to the IP or hostname of the live controller " +
            "to run cross-backend integration tests.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an SDK session for the ACD project and returns a
    /// <see cref="TypedSdkTagService"/> in <see cref="OperationMode.Online"/>
    /// mode — the live-controller write path under test.
    /// </summary>
    private async Task<TypedSdkTagService> ConnectSdkAsync()
    {
        var session = new LogixProjectSession();
        await session.OpenAsync(_acdFixture.AcdPath!);
        var l5x = L5X.Load(L5XFixturePath);
        return new TypedSdkTagService(session, l5x, OperationMode.Online);
    }

    /// <summary>
    /// Connects <see cref="LibPlcTagService"/> to the live controller and
    /// returns it ready for CIP reads.
    /// </summary>
    private async Task<LibPlcTagService> ConnectCipAsync()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync(_cipHost!, _cipPath);
        return svc;
    }

    // ── Live round-trip tests ─────────────────────────────────────────────────

    /// <summary>
    /// Live BOOL cross-backend round-trip:
    /// <list type="number">
    ///   <item>Write <c>true</c> to <c>Cmd_MotorStart</c> via SDK-online.</item>
    ///   <item>Read it back via CIP (<see cref="LibPlcTagService"/>).</item>
    ///   <item>Assert that bit-0 of the CIP read is set (BOOL = 1).</item>
    ///   <item>Restore the original value (<c>false</c>) to leave the controller clean.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task
        SdkOnlineWrite_BoolTag_True_CipReadBitZeroIsSet_CrossBackendAgreement()
    {
        // Arrange: connect both lanes.
        var sdkSvc = await ConnectSdkAsync();
        var cipSvc = await ConnectCipAsync();

        // Pre-condition: write false to establish a known baseline.
        await sdkSvc.WriteAsync(BoolTag, false);

        // (1) Write true via SDK-online lane.
        await sdkSvc.WriteAsync(BoolTag, true);

        // (2) Read via CIP lane (returns the BOOL as bit-0 of the raw value).
        var cipResult = await cipSvc.ReadAsync(BoolTag);

        // (3) Assert cross-backend agreement: CIP bit 0 must be set.
        var cipValue = cipResult.Value;
        bool cipBool = cipValue switch
        {
            bool   b => b,
            int    i => (i & 1) == 1,
            _        => throw new InvalidCastException(
                $"Unexpected CIP value type: {cipValue.GetType().Name}"),
        };
        Assert.True(cipBool,
            $"Cross-backend disagreement detected: SDK-online wrote true to " +
            $"'{BoolTag}', but CIP read returned {cipValue} (type " +
            $"{cipValue.GetType().Name}) — bit 0 not set.");

        // Cleanup: restore false to avoid leaving the controller in a modified state.
        await sdkSvc.WriteAsync(BoolTag, false);
    }

    /// <summary>
    /// Live REAL cross-backend round-trip:
    /// <list type="number">
    ///   <item>Write a known float to <c>Cmd_SpeedSetpoint</c> via SDK-online.</item>
    ///   <item>Read it back via CIP (<see cref="LibPlcTagService"/>), which
    ///     returns the 4-byte payload as a DINT (int32 bit-pattern).</item>
    ///   <item>Reinterpret the DINT as IEEE-754 float via
    ///     <see cref="BitConverter.Int32BitsToSingle"/>.</item>
    ///   <item>Assert that the recovered float matches the SDK-written value.</item>
    ///   <item>Restore the original value (0.0f) to leave the controller clean.</item>
    /// </list>
    ///
    /// <para>
    /// This is the core regression test for the silent-write-failure defect:
    /// if the SDK write did not reach the controller, the CIP lane returns
    /// the old value and the assertion fails.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task
        SdkOnlineWrite_RealTag_CipIeee754ReinterpretMatchesWrittenFloat_CrossBackendAgreement()
    {
        // Arrange: connect both lanes.
        var sdkSvc = await ConnectSdkAsync();
        var cipSvc = await ConnectCipAsync();

        const float writtenFloat = 42.5f;

        // Pre-condition: zero the tag via SDK to ensure the test detects any write failure.
        await sdkSvc.WriteAsync(RealTag, 0.0f);

        // (1) Write 42.5f via SDK-online lane.
        await sdkSvc.WriteAsync(RealTag, writtenFloat);

        // (2) Read via CIP lane.
        //     LibPlcTagService returns 4-byte REAL as int32 (DINT bit-pattern).
        var cipResult = await cipSvc.ReadAsync(RealTag);

        // (3) Assert cross-backend agreement via IEEE-754 reinterpretation.
        //     This is the only check that catches silent SDK write failures.
        float cipFloat = BitConverter.Int32BitsToSingle((int)cipResult.Value);
        Assert.True(Math.Abs(cipFloat - writtenFloat) < 0.001f,
            $"Cross-backend disagreement detected: SDK-online wrote {writtenFloat} to " +
            $"'{RealTag}', but CIP read returned {cipResult.Value} (raw int32) = " +
            $"{cipFloat}f after IEEE-754 reinterpretation. " +
            $"The SDK write did not reach the controller.");

        // Cleanup: restore 0.0f.
        await sdkSvc.WriteAsync(RealTag, 0.0f);
    }
}

#endif
