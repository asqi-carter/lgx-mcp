using System.Buffers.Binary;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The <c>LGX_TAG_ROUTE</c> setting (auto | cip | sdk) and route selection for
/// <c>logix_read</c>, <c>logix_write</c>, <c>logix_poll</c>, <c>logix_run_test</c> and <c>logix_diagnose</c>.
/// </summary>
public class TagRouteTests
{
    private static readonly Capabilities LiveWritesOn = new(LiveWritesEnabled: true);

    private static readonly ControllerIdentity SameController = new("PLC_A", 0x1234ABCDu);

    private const string Emulator = "Emulate 5580 Controller";

    /// <summary>Virtual store labelled with the route it stands for; counts reads and records writes.</summary>
    private sealed class Channel : ITagService
    {
        private readonly VirtualTagService _inner;
        public Channel(string label, int speed = 5)
        {
            Label  = label;
            _inner = new VirtualTagService(new Dictionary<string, (object, string?)>
            {
                ["Cmd_Start"] = (false, "BOOL"),
                ["Motor_Run"] = (false, "BOOL"),
                ["Speed"]     = (speed, "DINT"),
                ["Probe_Idx"] = (5, "DINT"),
                ["Other_Idx"] = (3, "DINT"),
            });
        }
        public string Label { get; }
        public int Reads { get; private set; }
        public List<string> Writes { get; } = new();
        /// <summary>When set, reads and writes throw ConnectionFailed (a CIP connection lost mid-session).</summary>
        public bool ConnectionLost { get; set; }
        public int ReadAttempts { get; private set; }
        public int WriteAttempts { get; private set; }
        private void ThrowIfLost()
        {
            if (ConnectionLost)
                throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "connection reset by peer"));
        }
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default) { ReadAttempts++; ThrowIfLost(); Reads++; return _inner.ReadAsync(t, ct); }
        public async Task WriteAsync(string t, object v, CancellationToken ct = default)
        {
            WriteAttempts++;
            ThrowIfLost();
            Writes.Add(t);
            await _inner.WriteAsync(t, v, ct);
            if (t == "Cmd_Start") await _inner.WriteAsync("Motor_Run", v, ct);   // stand-in for the PLC program
        }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) => _inner.ListTagsAsync(ct);
    }

    private static Func<CancellationToken, Task<ITagService>> Connects(Channel cip) =>
        _ => Task.FromResult<ITagService>(cip);

    private static Func<CancellationToken, Task<ITagService>> CannotConnect() =>
        _ => throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "no route to host"));

    private static WriteGatePolicy Policy() => new(
        Grants:
        [
            new GrantEntry("Cmd_Start", null, null, "command prefix 'Cmd_'"),
            new GrantEntry("Speed",     null, null, "granted for test"),
        ],
        Denies:
        [
            new DenyEntry("Motor_Run", DenyReason.ProgramWritten, "Tag 'Motor_Run' is written by program logic at MainRoutine rung 1."),
        ]);

    /// <summary>A session the way an SDK connect builds it: gate over router over the SDK service.</summary>
    private static (TagSessionService Session, RoutedTagService Router) SessionOf(
        Channel sdk, TagRouteMode mode, Func<CancellationToken, Task<ITagService>>? cip, string? noCipReason = null)
    {
        var router  = new RoutedTagService(sdk, mode, cip, noCipReason);
        var session = new TagSessionService();
        session.Attach(new GatedTagService(router, Policy()), isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");
        return (session, router);
    }

    // ── The setting ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,     TagRouteMode.Auto)]
    [InlineData("",       TagRouteMode.Auto)]
    [InlineData("  ",     TagRouteMode.Auto)]
    [InlineData("auto",   TagRouteMode.Auto)]
    [InlineData("AUTO",   TagRouteMode.Auto)]
    [InlineData("cip",    TagRouteMode.Cip)]
    [InlineData(" Cip ",  TagRouteMode.Cip)]
    [InlineData("sdk",    TagRouteMode.Sdk)]
    [InlineData("SDK",    TagRouteMode.Sdk)]
    public void Setting_Parses_EachValue(string? raw, TagRouteMode expected)
    {
        Assert.True(TagRouteResolver.TryParse(raw, out var parsed));
        Assert.Equal(expected, parsed);
        using var scope = TagRouteResolver.UseValueForTesting(raw);
        Assert.Equal(expected, TagRouteResolver.Current);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("ethernet")]
    [InlineData("1")]
    public void Setting_UnknownValue_IsAnError_NotTheDefault(string raw)
    {
        Assert.False(TagRouteResolver.TryParse(raw, out _));
        using var scope = TagRouteResolver.UseValueForTesting(raw);
        var ex = Assert.Throws<TagRouteSettingInvalidException>(() => TagRouteResolver.Current);
        Assert.Contains("LGX_TAG_ROUTE", ex.Message);
        Assert.Contains(raw, ex.Message);
    }

    // ── Route selection, each setting ─────────────────────────────────────────

    [Fact]
    public async Task RouteSelection_Auto_UsesCipWhenCipConnects()
    {
        var sdk = new Channel("sdk", speed: 1);
        var cip = new Channel("cip", speed: 2);
        var (session, router) = SessionOf(sdk, TagRouteMode.Auto, Connects(cip));

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(2, read.Value);
        Assert.Equal(TagRoutes.Cip, router.ActiveRoute);
        Assert.Equal(0, sdk.Reads);
    }

    [Fact]
    public async Task RouteSelection_Auto_FallsBackToSdkWhenCipCannotConnect()
    {
        var sdk = new Channel("sdk", speed: 1);
        var (session, router) = SessionOf(sdk, TagRouteMode.Auto, CannotConnect());

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(1, read.Value);
        Assert.Equal(TagRoutes.Sdk, router.ActiveRoute);
        Assert.Contains("no route to host", router.RouteReason);
    }

    [Fact]
    public async Task RouteSelection_Auto_NoCipTarget_UsesSdkAndRecordsWhy()
    {
        var sdk = new Channel("sdk", speed: 1);
        var (session, router) = SessionOf(sdk, TagRouteMode.Auto, null, "could not derive a CIP gateway");

        Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(TagRoutes.Sdk, router.ActiveRoute);
        Assert.Contains("could not derive a CIP gateway", router.RouteReason);
    }

    [Fact]
    public async Task RouteSelection_Cip_UsesCipWhenCipConnects()
    {
        var sdk = new Channel("sdk", speed: 1);
        var cip = new Channel("cip", speed: 2);
        var (session, router) = SessionOf(sdk, TagRouteMode.Cip, Connects(cip));

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(2, read.Value);
        Assert.Equal(TagRoutes.Cip, router.ActiveRoute);
        Assert.Equal(0, sdk.Reads);
    }

    [Fact]
    public async Task RouteSelection_Cip_CannotConnect_RaisesTypedTagRouteUnavailable()
    {
        var sdk = new Channel("sdk", speed: 1);
        var router = new RoutedTagService(sdk, TagRouteMode.Cip, CannotConnect());

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => router.ReadAsync("Speed"));

        Assert.Equal(TagServiceErrorKind.TagRouteUnavailable, ex.Error.Kind);
        Assert.Contains("no route to host", ex.Error.Message);
        Assert.Equal(0, sdk.Reads);          // never falls back to the SDK in cip mode
        Assert.Null(router.ActiveRoute);
    }

    [Fact]
    public async Task RouteSelection_Cip_NoCipTarget_RaisesTypedTagRouteUnavailable()
    {
        var router = new RoutedTagService(new Channel("sdk"), TagRouteMode.Cip, null, "could not derive a CIP gateway");

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => router.WriteAsync("Cmd_Start", true));

        Assert.Equal(TagServiceErrorKind.TagRouteUnavailable, ex.Error.Kind);
        Assert.Contains("could not derive a CIP gateway", ex.Error.Message);
    }

    [Fact]
    public async Task RouteSelection_Cip_FailureIsNotCached_NextCallRetriesTheConnect()
    {
        var cip = new Channel("cip", speed: 2);
        var attempts = 0;
        var router = new RoutedTagService(new Channel("sdk"), TagRouteMode.Cip, _ =>
        {
            attempts++;
            return attempts == 1
                ? throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "down"))
                : Task.FromResult<ITagService>(cip);
        });

        await Assert.ThrowsAsync<TagServiceException>(() => router.ReadAsync("Speed"));
        var value = await router.ReadAsync("Speed");

        Assert.Equal(2, value.Value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RouteSelection_Sdk_KeepsTodaysBehaviour_NeverOpensCip()
    {
        var sdk = new Channel("sdk", speed: 1);
        var cipConnects = 0;
        var (session, router) = SessionOf(sdk, TagRouteMode.Sdk, _ =>
        {
            cipConnects++;
            return Task.FromResult<ITagService>(new Channel("cip", speed: 2));
        });

        var read  = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));
        var write = Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

        Assert.Equal(1, read.Value);
        Assert.Equal(9, write.Observed);
        Assert.Equal(["Speed"], sdk.Writes);
        Assert.Equal(0, cipConnects);
        Assert.Equal(TagRoutes.Sdk, router.ActiveRoute);
    }

    // ── Every tool honours the setting ────────────────────────────────────────

    [Theory]
    [InlineData(TagRouteMode.Auto)]
    [InlineData(TagRouteMode.Cip)]
    public async Task Write_GoesOverCip_WhenRouteIsCip_AndSdkIsUntouched(TagRouteMode mode)
    {
        var sdk = new Channel("sdk");
        var cip = new Channel("cip");
        var (session, _) = SessionOf(sdk, mode, Connects(cip));

        var result = Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

        Assert.Equal(["Speed"], cip.Writes);
        Assert.Empty(sdk.Writes);
        Assert.Equal(9, result.Observed);       // readback came over the same route
        Assert.Equal(0, sdk.Reads);
    }

    [Theory]
    [InlineData(TagRouteMode.Auto, TagRoutes.Sdk)]
    [InlineData(TagRouteMode.Sdk,  TagRoutes.Sdk)]
    public async Task Write_GoesOverSdk_WhenRouteIsSdk(TagRouteMode mode, string expected)
    {
        var sdk = new Channel("sdk");
        var (session, router) = SessionOf(sdk, mode, mode == TagRouteMode.Auto ? CannotConnect() : null);

        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

        Assert.Equal(["Speed"], sdk.Writes);
        Assert.Equal(expected, router.ActiveRoute);
    }

    [Fact]
    public async Task Write_CipMode_NoRoute_ReturnsTypedTagRouteUnavailable_NothingWritten()
    {
        var sdk = new Channel("sdk");
        var (session, _) = SessionOf(sdk, TagRouteMode.Cip, CannotConnect());

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

        Assert.Equal(ErrorKinds.TagRouteUnavailable, err.Kind);
        Assert.Empty(sdk.Writes);
    }

    [Fact]
    public async Task Read_CipMode_NoRoute_ReturnsTypedTagRouteUnavailable()
    {
        var (session, _) = SessionOf(new Channel("sdk"), TagRouteMode.Cip, CannotConnect());

        var err = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(ErrorKinds.TagRouteUnavailable, err.Kind);
    }

    [Fact]
    public async Task Poll_UsesTheSelectedRoute_AndCipModeReturnsTypedError()
    {
        var sdk = new Channel("sdk", speed: 1);
        var cip = new Channel("cip", speed: 2);
        var (viaCip, _) = SessionOf(sdk, TagRouteMode.Cip, Connects(cip));

        var poll = Assert.IsType<PollResult>(await PollTagsHandler.ExecuteAsync(["Speed"], 2, PollTagsHandler.MinIntervalMs, viaCip));

        Assert.Equal(2, poll.Cycles[0].Changes[0].Current);
        Assert.Equal(2, cip.Reads);
        Assert.Equal(0, sdk.Reads);

        var (down, _) = SessionOf(new Channel("sdk"), TagRouteMode.Cip, CannotConnect());
        var err = Assert.IsType<PollError>(await PollTagsHandler.ExecuteAsync(["Speed"], 1, PollTagsHandler.MinIntervalMs, down));
        Assert.Equal(ErrorKinds.TagRouteUnavailable, err.Kind);
    }

    // ── The route is visible in results and in the audit write record ─────────

    private static System.Text.Json.JsonElement Json(object result) =>
        System.Text.Json.JsonSerializer.SerializeToElement(result, result.GetType());

    [Theory]
    [InlineData(TagRouteMode.Auto, true,  TagRoutes.Cip)]
    [InlineData(TagRouteMode.Cip,  true,  TagRoutes.Cip)]
    [InlineData(TagRouteMode.Auto, false, TagRoutes.Sdk)]
    [InlineData(TagRouteMode.Sdk,  false, TagRoutes.Sdk)]
    public async Task Read_Write_Poll_Results_CarryTheRoute(TagRouteMode mode, bool cipUp, string expected)
    {
        var sdk = new Channel("sdk");
        var cip = new Channel("cip");
        var connector = mode == TagRouteMode.Sdk ? null : cipUp ? Connects(cip) : CannotConnect();
        var (session, router) = SessionOf(sdk, mode, connector);

        var read  = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));
        var write = Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));
        var poll  = Assert.IsType<PollResult>(await PollTagsHandler.ExecuteAsync(["Speed"], 1, PollTagsHandler.MinIntervalMs, session));

        Assert.Equal(expected, router.ActiveRoute);
        Assert.Equal(expected, read.Route);
        Assert.Equal(expected, write.Route);
        Assert.Equal(expected, poll.Route);
        Assert.Equal(expected, Json(read).GetProperty("route").GetString());
        Assert.Equal(expected, Json(write).GetProperty("route").GetString());
        Assert.Equal(expected, Json(poll).GetProperty("route").GetString());
    }

    [Fact]
    public async Task Results_OmitRoute_WhenTheSessionIsNotRouted()
    {
        var session = new TagSessionService();
        session.Attach(new Channel("plain"), isVirtual: true);

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Null(read.Route);
        Assert.False(Json(read).TryGetProperty("route", out _));
    }

    [Theory]
    [InlineData(true,  TagRoutes.Cip)]
    [InlineData(false, TagRoutes.Sdk)]
    public async Task AuditWriteRecord_CarriesTheRoute(bool cipUp, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"route-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        AuditLogger.OverrideLogDir(dir);
        try
        {
            var (session, _) = SessionOf(new Channel("sdk"), TagRouteMode.Auto, cipUp ? Connects(new Channel("cip")) : CannotConnect());

            Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

            var line = File.ReadAllLines(Path.Combine(dir, "audit.jsonl"))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .Single(e => e.GetProperty("event").GetString() == "write");
            Assert.Equal(expected, line.GetProperty("route").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Fallback on a CIP connection loss ───────────────────

    /// <summary>A session whose CIP route connects, is chosen, and then loses its connection.</summary>
    private static async Task<(TagSessionService Session, RoutedTagService Router, Channel Sdk, Channel Cip)> LostCipSession(TagRouteMode mode)
    {
        var sdk = new Channel("sdk", speed: 1);
        var cip = new Channel("cip", speed: 2);
        var (session, router) = SessionOf(sdk, mode, Connects(cip));
        Assert.Equal(TagRoutes.Cip, await router.ResolveRouteAsync());
        cip.ConnectionLost = true;
        return (session, router, sdk, cip);
    }

    [Fact]
    public async Task Fallback_Auto_ReadRetriesOnceOverSdk_AndTheResultSaysSo()
    {
        var (session, router, sdk, cip) = await LostCipSession(TagRouteMode.Auto);

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(1, read.Value);
        Assert.Equal(TagRoutes.Sdk, read.Route);
        Assert.Contains("retried once over the SDK", read.RouteNote);
        Assert.Contains("connection reset by peer", read.RouteNote);
        Assert.Equal(1, cip.ReadAttempts);
        Assert.Equal(1, sdk.Reads);
        Assert.Contains("fell back to the SDK", router.RouteReason);
        Assert.Equal("sdk", Json(read).GetProperty("route").GetString());
        Assert.True(Json(read).TryGetProperty("routeNote", out _));
    }

    [Fact]
    public async Task Fallback_Auto_RetriesExactlyOnce_AndAFailingSdkIsNotRetried()
    {
        var (session, _, sdk, cip) = await LostCipSession(TagRouteMode.Auto);
        sdk.ConnectionLost = true;

        var err = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(nameof(TagServiceErrorKind.ConnectionFailed), err.Kind);
        Assert.Equal(1, cip.ReadAttempts);
        Assert.Equal(1, sdk.ReadAttempts);
    }

    [Fact]
    public async Task Fallback_Auto_LaterCallsStayOnSdk_WithoutTryingCipAgain()
    {
        var (session, _, sdk, cip) = await LostCipSession(TagRouteMode.Auto);
        await ReadTagHandler.ExecuteAsync("Speed", session);

        var second = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(TagRoutes.Sdk, second.Route);
        Assert.Null(second.RouteNote);
        Assert.Equal(1, cip.ReadAttempts);
        Assert.Equal(2, sdk.Reads);
    }

    [Fact]
    public async Task Fallback_Cip_ReadReturnsTheTypedError_NoSdkRetry()
    {
        var (session, router, sdk, _) = await LostCipSession(TagRouteMode.Cip);

        var err = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(nameof(TagServiceErrorKind.ConnectionFailed), err.Kind);
        Assert.Equal(0, sdk.ReadAttempts);
        Assert.Equal(TagRoutes.Cip, router.ActiveRoute);
    }

    [Fact]
    public async Task Fallback_Auto_WriteRetriedOverSdk_CarriesRouteSdkAndANote()
    {
        var (session, _, sdk, cip) = await LostCipSession(TagRouteMode.Auto);

        var write = Assert.IsType<WriteTagResult>(
            await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

        Assert.Equal(TagRoutes.Sdk, write.Route);
        Assert.Contains("retried once over the SDK", write.RouteNote);
        Assert.True(write.Verified);
        Assert.Equal(1, cip.WriteAttempts);
        Assert.Equal(["Speed"], sdk.Writes);
        Assert.Equal("sdk", Json(write).GetProperty("route").GetString());
        Assert.True(Json(write).TryGetProperty("routeNote", out _));
    }

    [Fact]
    public async Task Fallback_Auto_WriteRetry_AuditRecordCarriesRouteSdkAndTheNote()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"route-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        AuditLogger.OverrideLogDir(dir);
        try
        {
            var (session, _, _, _) = await LostCipSession(TagRouteMode.Auto);

            Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

            var line = File.ReadAllLines(Path.Combine(dir, "audit.jsonl"))
                .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .Single(e => e.GetProperty("event").GetString() == "write");
            Assert.Equal("sdk", line.GetProperty("route").GetString());
            Assert.Contains("retried once over the SDK", line.GetProperty("routeNote").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Fallback_Cip_WriteReturnsTheTypedError_NothingWrittenOverSdk()
    {
        var (session, _, sdk, _) = await LostCipSession(TagRouteMode.Cip);

        var err = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Speed", "9", session, capabilities: LiveWritesOn));

        Assert.Equal(nameof(TagServiceErrorKind.ConnectionFailed), err.Kind);
        Assert.Equal(0, sdk.WriteAttempts);
    }

    [Fact]
    public async Task Fallback_Auto_PollNotesTheRetryAndReportsRouteSdk()
    {
        var (session, _, _, _) = await LostCipSession(TagRouteMode.Auto);

        var poll = Assert.IsType<PollResult>(await PollTagsHandler.ExecuteAsync(["Speed"], 1, PollTagsHandler.MinIntervalMs, session));

        var json = Json(poll);
        Assert.Equal("sdk", json.GetProperty("route").GetString());
        Assert.Contains("retried once over the SDK", json.GetProperty("routeNote").GetString());
    }

    [Fact]
    public async Task Fallback_Auto_NoNoteWhenCipIsHealthy()
    {
        var (session, _, _, cip) = await LostCipSession(TagRouteMode.Auto);
        cip.ConnectionLost = false;

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(TagRoutes.Cip, read.Route);
        Assert.Null(read.RouteNote);
    }

    private static RunTestCase StartCase() =>
        new("start", Steps:
        [
            new RunTestStep(
                Set:    [new RunTestSet("Cmd_Start", System.Text.Json.JsonDocument.Parse("true").RootElement.Clone())],
                WaitMs: 0,
                Expect: [new RunTestExpect("Motor_Run", EqualsValue: System.Text.Json.JsonDocument.Parse("true").RootElement.Clone())]),
        ]);

    private static Task<object> RunTest(TagSessionService session) =>
        RunTestHandler.ExecuteAsync(
            [StartCase()], true, session, new Capabilities(LiveWritesEnabled: true),
            (_, _) => Task.FromResult(new EmulatorIdentityInfo(Emulator, 36, 11, 1, 14, 166, 1u)),
            (_, _) => Task.CompletedTask);

    [Fact]
    public async Task RunTest_ReadsAndWritesGoOverTheSelectedRoute()
    {
        var sdk = new Channel("sdk");
        var cip = new Channel("cip");
        var (session, _) = SessionOf(sdk, TagRouteMode.Auto, Connects(cip));

        var result = Assert.IsType<RunTestResult>(await RunTest(session));

        Assert.Equal("pass", result.Overall);
        Assert.Contains("Cmd_Start", cip.Writes);
        Assert.Empty(sdk.Writes);
        Assert.Equal(0, sdk.Reads);
    }

    [Fact]
    public async Task RunTest_SdkMode_UsesSdk()
    {
        var sdk = new Channel("sdk");
        var cipConnects = 0;
        var (session, _) = SessionOf(sdk, TagRouteMode.Sdk, _ => { cipConnects++; return Task.FromResult<ITagService>(new Channel("cip")); });

        Assert.IsType<RunTestResult>(await RunTest(session));

        Assert.Contains("Cmd_Start", sdk.Writes);
        Assert.Equal(0, cipConnects);
    }

    [Fact]
    public async Task RunTest_CipMode_NoRoute_RefusedWithTypedError_NothingWritten()
    {
        var sdk = new Channel("sdk");
        var (session, _) = SessionOf(sdk, TagRouteMode.Cip, CannotConnect());

        var err = Assert.IsType<RunTestError>(await RunTest(session));

        Assert.Equal(ErrorKinds.TagRouteUnavailable, err.Kind);
        Assert.Empty(sdk.Writes);
        Assert.Equal(0, sdk.Reads);
    }

    private static byte[] FaultBytes()
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 20);
        return b;
    }

    private static async Task<object> Diagnose(TagSessionService session)
    {
        var project = new ProjectService();
        project.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoDiagnose.L5X"));
        return await DiagnoseHandler.ExecuteAsync(
            null, session, new ControllerModeService(),
            (_, _) => Task.FromResult(new EmulatorIdentityInfo(Emulator, 38, 11, 1, 14, 0x55, 1, 0x3450)),
            (_, _) => Task.FromResult(FaultBytes()), project);
    }

    [Fact]
    public async Task Diagnose_LiveTagReadsGoOverTheSelectedRoute()
    {
        var sdk = new Channel("sdk");
        var cip = new Channel("cip");
        var (session, _) = SessionOf(sdk, TagRouteMode.Auto, Connects(cip));

        var r = Assert.IsType<DiagnoseResult>(await Diagnose(session));

        Assert.NotEmpty(r.Suspects);
        Assert.True(cip.Reads > 0);
        Assert.Equal(0, sdk.Reads);
    }

    [Fact]
    public async Task Diagnose_CipMode_NoRoute_ReturnsTypedTagRouteUnavailable()
    {
        var (session, _) = SessionOf(new Channel("sdk"), TagRouteMode.Cip, CannotConnect());

        var err = Assert.IsType<DiagnoseError>(await Diagnose(session));

        Assert.Equal(ErrorKinds.TagRouteUnavailable, err.Kind);
    }

    // ── One gate for both routes ──────────────────────────────────────────────

    [Theory]
    [InlineData(TagRouteMode.Cip)]
    [InlineData(TagRouteMode.Sdk)]
    public async Task Gate_RefusesTheSameWrite_OnEitherRoute(TagRouteMode mode)
    {
        var sdk = new Channel("sdk");
        var cip = new Channel("cip");
        var (session, _) = SessionOf(sdk, mode, Connects(cip));

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session, capabilities: LiveWritesOn));

        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), err.Kind);
        Assert.Empty(sdk.Writes);
        Assert.Empty(cip.Writes);
    }

    // ── Unwrapping keeps SDK-only casts working ───────────────────────────────

    [Fact]
    public void Backend_UnwrapsGateAndRouter_ToTheSdkService()
    {
        var sdk = new Channel("sdk");
        var (session, _) = SessionOf(sdk, TagRouteMode.Auto, null);

        Assert.Same(sdk, TagRouteResolver.Backend(session.Service));
        Assert.Same(sdk, TagRouteResolver.Backend(sdk));
        Assert.Null(TagRouteResolver.Backend(null));
    }

    // ── Connect wiring ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("EmulateEthernet\\192.0.2.10",              "1,0")]
    [InlineData("192.0.2.10",                               "1,0")]
    [InlineData("192.0.2.10,1,3",                           "1,3")]
    [InlineData("AB_ETHIP-1\\192.0.2.10\\Backplane\\2",     "1,2")]
    [InlineData("AB_ETH-1\\192.0.2.10\\Backplane\\0",       "1,0")]
    public void DeriveCipPath_Table(string commPath, string expected) =>
        Assert.Equal(expected, ConnectHandler.DeriveCipPath(commPath));

    [Fact]
    public async Task BuildRoutedService_NoDerivableGateway_AutoUsesSdk_CipFails()
    {
        var sdk = new Channel("sdk", speed: 1);

        var auto = ConnectHandler.BuildRoutedService(sdk, "USB-1\\Something", null, TagRouteMode.Auto);
        Assert.Equal(1, (await auto.ReadAsync("Speed")).Value);
        Assert.Equal(TagRoutes.Sdk, auto.ActiveRoute);

        var cip = ConnectHandler.BuildRoutedService(sdk, "USB-1\\Something", null, TagRouteMode.Cip);
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => cip.ReadAsync("Speed"));
        Assert.Equal(TagServiceErrorKind.TagRouteUnavailable, ex.Error.Kind);
    }

#if LOGIX_SDK
    private sealed class UploadFactory : ISdkTagServiceFactory
    {
        public Channel Sdk { get; } = new("sdk", speed: 1);
        public ControllerIdentity? Identity { get; init; } = SameController;
        public Task<SdkSessionResult> CreateAsync(string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline", bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
            => Task.FromResult(new SdkSessionResult(Sdk, false, [], "upload-to-new-project", null, null, ControllerIdentity: Identity));
    }

    private static async Task<(object Result, TagSessionService Session, Channel Sdk, List<(string Gateway, string Path)> Cip)>
        ConnectOnline(string setting, bool cipUp, string commPath = "EmulateEthernet\\192.0.2.10",
                      ControllerIdentity? cipIdentity = null, ControllerIdentity? sdkIdentity = null,
                      bool identityReadFails = false)
    {
        var factory  = sdkIdentity is null ? new UploadFactory() : new UploadFactory { Identity = sdkIdentity };
        var session  = new TagSessionService();
        var attempts = new List<(string, string)>();
        var cipChannel = new Channel("cip", speed: 2);
        using var _ = TagRouteResolver.UseValueForTesting(setting);

        var result = await ConnectHandler.ExecuteAsync(
            gateway: "", path: "", useVirtual: false, projectService: new ProjectService(), sessionService: session,
            backend: "sdk", sdkFactory: factory, sdkMode: "online", sdkCommPath: commPath,
            cipIdentityReader: (_, _) => identityReadFails
                ? throw new InvalidOperationException("ListIdentity timed out")
                : Task.FromResult(cipIdentity ?? SameController),
            cipRouteConnector: (gw, p, _) =>
            {
                attempts.Add((gw, p));
                return cipUp ? Task.FromResult<ITagService>(cipChannel)
                             : throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "down"));
            });
        return (result, session, factory.Sdk, attempts);
    }

    [Fact]
    public async Task Connect_Online_DefaultSetting_ReadsOverCip_ToTheGatewayFromTheCommPath()
    {
        var (result, session, sdk, cipAttempts) = await ConnectOnline(setting: "", cipUp: true);
        Assert.IsType<ConnectResult>(result);

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(2, read.Value);
        Assert.Equal(("192.0.2.10", "1,0"), Assert.Single(cipAttempts));
        Assert.Equal(0, sdk.Reads);
    }

    [Fact]
    public async Task Connect_Online_SdkSetting_ReadsOverSdk_AndNeverOpensCip()
    {
        var (_, session, sdk, cipAttempts) = await ConnectOnline(setting: "sdk", cipUp: true);

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(1, read.Value);
        Assert.Empty(cipAttempts);
        Assert.Equal(1, sdk.Reads);
    }

    [Fact]
    public async Task Connect_Online_CipSetting_CipDown_ReadFailsWithTypedKind()
    {
        var (result, session, sdk, _) = await ConnectOnline(setting: "cip", cipUp: false);
        Assert.IsType<ConnectResult>(result);

        var err = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync("Speed", session));

        Assert.Equal(ErrorKinds.TagRouteUnavailable, err.Kind);
        Assert.Equal(0, sdk.Reads);
    }

    [Fact]
    public async Task Connect_Online_InvalidSetting_FailsTheConnect_NoSession()
    {
        var (result, session, _, _) = await ConnectOnline(setting: "fast", cipUp: true);

        var err = Assert.IsType<ConnectError>(result);
        Assert.Contains("LGX_TAG_ROUTE", err.Error);
        Assert.False(session.IsConnected);
    }
#endif

    // ── Comm-path parser table ─────────────────────────────

    [Theory]
    [InlineData("EmulateEthernet\\192.0.2.220",                  "192.0.2.220", "1,0")]
    [InlineData("emulateethernet\\192.0.2.220",                  "192.0.2.220", "1,0")]
    [InlineData("EmulateEthernet\\192.0.2.220\\Backplane\\0",    "192.0.2.220", "1,0")]
    [InlineData("EmulateEthernet\\192.0.2.220\\Backplane\\3",    "192.0.2.220", "1,3")]
    [InlineData("AB_ETH-1\\192.0.2.10\\Backplane\\2",            "192.0.2.10",    "1,2")]
    [InlineData("AB_ETH-1\\192.0.2.10\\Backplane\\0",            "192.0.2.10",    "1,0")]
    [InlineData("AB_ETH-1\\192.0.2.10",                           "192.0.2.10",    "1,0")]
    [InlineData("ab_eth-2\\192.0.2.10\\backplane\\5",            "192.0.2.10",    "1,5")]
    [InlineData("AB_ETHIP-1\\192.0.2.10\\Backplane\\1",          "192.0.2.10",    "1,1")]
    [InlineData("AB_ETHIP-1\\192.0.2.10,1,4",                     "192.0.2.10",    "1,4")]
    [InlineData("192.0.2.10",                                     "192.0.2.10",    "1,0")]
    [InlineData("  192.0.2.10,1,3  ",                             "192.0.2.10",    "1,3")]
    public void CommPathParser_Table_DerivesGatewayAndPath(string commPath, string gateway, string path)
    {
        Assert.True(CipCommPath.TryParse(commPath, out var target, out var failure), failure);
        Assert.Equal(new CipTarget(gateway, path), target);
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("USB-1\\Something")]
    [InlineData("AB_DF1-1\\1")]
    [InlineData("AB_ETH-1\\192.0.2.10\\Backplane\\1\\192.0.2.11\\Backplane\\0")] // multi-hop
    [InlineData("AB_ETH-1\\192.0.2.10\\Backplane\\x")]
    [InlineData("EmulateEthernet\\999.0.2.10")]
    [InlineData("EmulateEthernet\\plc-host")]
    [InlineData("UNKNOWN\\192.0.2.10")]
    public void CommPathParser_Table_RejectsUnsupportedForms_WithAReason(string commPath)
    {
        Assert.False(CipCommPath.TryParse(commPath, out var target, out var failure));
        Assert.Null(target);
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Theory]
    [InlineData("16#1234_ABCD", 0x1234ABCDu)]
    [InlineData("0x1234ABCD",   0x1234ABCDu)]
    [InlineData("1234abcd",     0x1234ABCDu)]
    [InlineData("",             null)]
    [InlineData("zz",           null)]
    public void ControllerSerial_Parses(string raw, uint? expected) =>
        Assert.Equal(expected, ControllerIdentity.ParseSerial(raw));

    [Theory]
    [InlineData(0x1234ABCDu, "PLC_A",  true)]
    [InlineData(0x1234ABCDu, "plc_a",  true)]
    [InlineData(0x1234ABCDu, null,    true)]   // name reported by one side only: serial decides
    [InlineData(0x1234ABCEu, "PLC_A",  false)]  // serial differs
    [InlineData(0x1234ABCDu, "Other", false)]  // name differs
    [InlineData(null,        "PLC_A",  false)]  // no serial on the CIP side: unverified
    public void Verifier_ComparesSerialAndName(uint? cipSerial, string? cipName, bool verified)
    {
        Assert.Equal(verified, CipRouteVerifier.TryVerify(new ControllerIdentity(cipName, cipSerial), SameController, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    private static bool Verify(ControllerIdentity cip, ControllerIdentity sdk, out string reason, out string? method) =>
        CipRouteVerifier.TryVerify(cip, sdk, out reason, out method);

    [Fact]
    public void SerialMatch()
    {
        // Names known on one side only: the serial decides.
        Assert.True(Verify(new ControllerIdentity(null, 0x1234ABCDu), SameController, out _, out var method));
        Assert.Equal("serial", method);
    }

    [Fact]
    public void SerialMismatch()
    {
        Assert.False(Verify(new ControllerIdentity("PLC_A", 0x1u), SameController, out var reason, out var method));
        Assert.Null(method);
        Assert.Contains("serial mismatch", reason);
    }

    [Fact]
    public void ZeroSerialWithNameMatch()
    {
        var sdk = new ControllerIdentity("PLC_LN02_Template", 0u);
        // Case-insensitive, and the CIP serial is irrelevant when the project has none.
        Assert.True(Verify(new ControllerIdentity("plc_ln02_template", 0x1A2B3C4Du), sdk, out _, out var method));
        Assert.Equal("name", method);
        Assert.True(Verify(new ControllerIdentity("PLC_LN02_TEMPLATE", 0x1A2B3C4Du), new ControllerIdentity("PLC_LN02_Template", null), out _, out method));
        Assert.Equal("name", method);
    }

    [Fact]
    public void ZeroSerialWithNameMismatch()
    {
        Assert.False(Verify(new ControllerIdentity("Other", 0x1A2B3C4Du), new ControllerIdentity("PLC_A", 0u), out var reason, out var method));
        Assert.Null(method);
        Assert.Contains("name mismatch", reason);
    }

    [Fact]
    public void ZeroSerialWithNoName()
    {
        Assert.False(Verify(new ControllerIdentity(null, 0x1A2B3C4Du), new ControllerIdentity("PLC_A", 0u), out var reason, out var method));
        Assert.Null(method);
        Assert.Contains("name could not be read", reason);
    }

    [Fact]
    public void SerialMatchWithNameMismatch()
    {
        Assert.False(Verify(new ControllerIdentity("Other", 0x1234ABCDu), SameController, out var reason, out var method));
        Assert.Null(method);
        Assert.Contains("name mismatch", reason);
    }

    [Fact]
    public void SerialMatchWithNameMatch_ReportsSerialPlusName()
    {
        Assert.True(Verify(new ControllerIdentity("plc_a", 0x1234ABCDu), SameController, out _, out var method));
        Assert.Equal("serial+name", method);
    }

    [Fact]
    public async Task IdentityCheck_IsInRouteReason_AndOnTheRouter()
    {
        var (router, _) = Build("EmulateEthernet\\192.0.2.10",
            cip: new ControllerIdentity("PLC_A", 0x1A2B3C4Du), sdkId: new ControllerIdentity("plc_a", 0u));

        Assert.Equal(TagRoutes.Cip, await router.ResolveRouteAsync());
        Assert.Equal("name", router.IdentityCheck);
        Assert.Contains("identityCheck: name", router.RouteReason);
    }

    [Fact]
    public void Verifier_NoSdkIdentity_IsUnverified() =>
        Assert.False(CipRouteVerifier.TryVerify(SameController, null, out _));

    private static (RoutedTagService Router, List<string> Opened) Build(
        string commPath, ControllerIdentity cip, ControllerIdentity? sdkId, TagRouteMode mode = TagRouteMode.Auto)
    {
        var opened = new List<string>();
        var router = ConnectHandler.BuildRoutedService(
            new Channel("sdk", speed: 1), commPath, null, mode,
            (gw, p, _) => { opened.Add($"{gw}|{p}"); return Task.FromResult<ITagService>(new Channel("cip", speed: 2)); },
            sdkId, (_, _) => Task.FromResult(cip));
        return (router, opened);
    }

    [Fact]
    public async Task IdentityMismatch_UsesSdkRoute_WithRecordedReason()
    {
        var (router, opened) = Build("EmulateEthernet\\192.0.2.10",
            cip: new ControllerIdentity("PLC_A", 0x99999999u), sdkId: SameController);

        Assert.Equal(TagRoutes.Sdk, await router.ResolveRouteAsync());
        Assert.Equal(1, (await router.ReadAsync("Speed")).Value);   // SDK channel answered
        Assert.Empty(opened);                                       // the CIP session was never opened
        Assert.Contains("mismatch", router.RouteReason);
        Assert.Contains("serial", router.RouteReason);
    }

    [Fact]
    public async Task IdentityMismatch_CipMode_RaisesTypedTagRouteUnavailable()
    {
        var (router, opened) = Build("EmulateEthernet\\192.0.2.10",
            cip: new ControllerIdentity("Other", 0x1234ABCDu), sdkId: SameController, TagRouteMode.Cip);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => router.ReadAsync("Speed"));
        Assert.Equal(TagServiceErrorKind.TagRouteUnavailable, ex.Error.Kind);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task IdentityMatch_UsesCipRoute_AndRecordsTheEvidence()
    {
        var (router, opened) = Build("AB_ETH-1\\192.0.2.10\\Backplane\\2", SameController, SameController);

        Assert.Equal(TagRoutes.Cip, await router.ResolveRouteAsync());
        Assert.Equal("192.0.2.10|1,2", Assert.Single(opened));
        Assert.Contains("identity verified", router.RouteReason);
    }

    [Fact]
    public async Task UnknownSdkIdentity_UsesSdkRoute_NeverRoutesToAnUnverifiedController()
    {
        var (router, opened) = Build("EmulateEthernet\\192.0.2.10", SameController, sdkId: null);

        Assert.Equal(TagRoutes.Sdk, await router.ResolveRouteAsync());
        Assert.Empty(opened);
        Assert.Contains("no controller identity", router.RouteReason);
    }

    [Fact]
    public async Task ParseFailure_FallsBackToSdkRoute_WithRecordedReason()
    {
        var (router, opened) = Build("USB-1\\Something", SameController, SameController);

        Assert.Equal(TagRoutes.Sdk, await router.ResolveRouteAsync());
        Assert.Empty(opened);
        Assert.Contains("USB-1\\Something", router.RouteReason);
        Assert.StartsWith("auto:", router.RouteReason);
    }

#if LOGIX_SDK
    [Fact]
    public async Task Connect_Result_ReportsTagRouteAndReason_Cip()
    {
        var (result, _, _, _) = await ConnectOnline(setting: "", cipUp: true);

        var connect = Assert.IsType<ConnectResult>(result);
        Assert.Equal(TagRoutes.Cip, connect.TagRoute);
        Assert.Contains("identity verified", connect.TagRouteReason);
        var json = System.Text.Json.JsonSerializer.Serialize(connect);
        Assert.Contains("\"tagRoute\":\"cip\"", json);
        Assert.Contains("\"tagRouteReason\"", json);
    }

    [Fact]
    public async Task Connect_Result_IdentityMismatch_ReportsSdkAndReason()
    {
        var (result, session, sdk, cipAttempts) = await ConnectOnline(
            setting: "", cipUp: true, cipIdentity: new ControllerIdentity("PLC_A", 0x1u));

        var connect = Assert.IsType<ConnectResult>(result);
        Assert.Equal(TagRoutes.Sdk, connect.TagRoute);
        Assert.Contains("serial mismatch", connect.TagRouteReason);
        Assert.Empty(cipAttempts);
        Assert.Equal(1, Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Speed", session)).Value);
        Assert.Equal(1, sdk.Reads);
    }

    [Fact]
    public async Task Connect_Result_ParseFailure_ReportsSdkAndReason()
    {
        var (result, _, _, cipAttempts) = await ConnectOnline(setting: "", cipUp: true, commPath: "USB-1\\Something");

        var connect = Assert.IsType<ConnectResult>(result);
        Assert.Equal(TagRoutes.Sdk, connect.TagRoute);
        Assert.Contains("could not derive a CIP gateway", connect.TagRouteReason);
        Assert.Empty(cipAttempts);
    }

    [Fact]
    public async Task Connect_Result_IdentityUnreadable_ReportsSdkAndReason()
    {
        var (result, _, _, cipAttempts) = await ConnectOnline(setting: "", cipUp: true, identityReadFails: true);

        var connect = Assert.IsType<ConnectResult>(result);
        Assert.Equal(TagRoutes.Sdk, connect.TagRoute);
        Assert.Contains("unverified", connect.TagRouteReason);
        Assert.Empty(cipAttempts);
    }

    [Fact]
    public async Task Connect_Result_SdkSetting_ReportsSdkWithTheSetting()
    {
        var (result, _, _, _) = await ConnectOnline(setting: "sdk", cipUp: true);

        var connect = Assert.IsType<ConnectResult>(result);
        Assert.Equal(TagRoutes.Sdk, connect.TagRoute);
        Assert.Contains("LGX_TAG_ROUTE=sdk", connect.TagRouteReason);
    }

    [Fact]
    public async Task Connect_Result_CipSetting_CipDown_OmitsRouteAndExplains()
    {
        var (result, _, _, _) = await ConnectOnline(setting: "cip", cipUp: false);

        var connect = Assert.IsType<ConnectResult>(result);
        Assert.Null(connect.TagRoute);
        Assert.Contains("CIP route is unavailable", connect.TagRouteReason);
    }
#endif
}
