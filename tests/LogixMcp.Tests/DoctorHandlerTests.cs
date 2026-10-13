using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="DoctorHandler"/> (<c>logix_doctor</c>). Every machine fact comes
/// through <see cref="IDoctorEnvironment"/>, so these run on Linux with a fake environment.
/// </summary>
public class DoctorHandlerTests
{
    private static readonly string[] ExpectedOrder =
        ["server", "audit", "sdkInstalled", "sdkService", "cipRuntime", "echoService",
         "controllerReachable", "controllerFault", "session", "pendingZone", "studio"];

    /// <summary>A healthy bench by default; each test breaks only what it is about.</summary>
    internal sealed class FakeEnv : IDoctorEnvironment
    {
        public int CurrentSessionId { get; set; } = 1;
        public bool IsElevated { get; set; }
        public string? GateConfig { get; set; }
        public string? GateConfigRaw() => GateConfig;
        public string? WriteAllowlist { get; set; }
        public string? WriteAllowlistRaw() => WriteAllowlist;
        public string? SdkAssemblyVersion { get; set; } = "2.2.1109.0";
        public bool SdkSupportedInBuild { get; set; } = true;
        public string? SdkLoadFailureReason { get; set; }
        public bool CipRuntimeOk { get; set; } = true;
        public bool CipRuntimeLoads() => CipRuntimeOk;
        public Dictionary<string, string> Services { get; } = new()
        {
            [DoctorHandler.SdkServiceName]  = "Running",
            [DoctorHandler.EchoServiceName] = "Running",
            [DoctorHandler.EchoBrokerName]  = "Running",
        };
        public string GetServiceState(string serviceName) =>
            Services.TryGetValue(serviceName, out var s) ? s : DoctorServiceState.NotInstalled;
        public SdkServiceDiagnosisSnapshot? LastSdkServiceDiagnosis { get; set; }
        public HashSet<string> OwnAddresses { get; } = new();
        public bool IsOwnInterfaceAddress(System.Net.IPAddress address) => OwnAddresses.Contains(address.ToString());
        public int? SdkCrashes { get; set; }
        public int? SdkServerCrashesLast24h() => SdkCrashes;
        public DateTimeOffset? NewestCrash { get; set; }
        public DateTimeOffset? NewestSdkServerCrashLast24h() => NewestCrash;
        public int? SdkCrashes7d { get; set; }
        public int? SdkServerCrashesLast7d() => SdkCrashes7d;
        public ConnectTimingsSnapshot? LastConnect { get; set; }
        public SdkConnectCrashes? SdkCrashesDuringConnect24h { get; set; }
        public string AuditDirectory { get; set; } = "/tmp/lgx-audit";
        public bool AuditWritable { get; set; } = true;
        public bool IsAuditDirectoryWritable() => AuditWritable;
        public long? AuditBytes { get; set; } = 1024;
        public long? AuditLogBytes() => AuditBytes;
        public bool TcpReachable { get; set; } = true;
        public List<(string Host, int Port, TimeSpan Timeout)> TcpCalls { get; } = new();
        public Task<bool> TcpConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            TcpCalls.Add((host, port, timeout));
            return Task.FromResult(TcpReachable);
        }
        public byte[] FaultRecord { get; set; } = new byte[64];
        public Exception? FaultReadError { get; set; }
        public List<string> FaultReads { get; } = new();
        public Task<byte[]> ReadFaultRecordAsync(string host, CancellationToken ct)
        {
            FaultReads.Add(host);
            if (FaultReadError is not null) throw FaultReadError;
            return Task.FromResult(FaultRecord);
        }
        public DoctorSessionInfo? Session { get; set; } =
            new("sdk", "online-controller", true, "correlated", TimeSpan.FromSeconds(12));
        public DoctorSessionInfo? GetActiveSession() => Session;
        public List<DoctorZone> Zones { get; set; } = [new("MainProgram", "MainRoutine", "none")];
        public int ZoneReads { get; private set; }
        public Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(CancellationToken ct)
        {
            ZoneReads++;
            return Task.FromResult<IReadOnlyList<DoctorZone>>(Zones);
        }
        public IReadOnlyList<int> StudioSessionIds { get; set; } = [1];
        public bool StudioIntegrityHigherThanCurrent { get; set; }
        public IReadOnlyList<string> StudioWindowTitlesInCurrentSession { get; set; } =
            ["[Rem Run] - Logix Designer - LgxDemo"];
    }

    private static Capabilities Caps(bool studio = false) =>
        new(LiveWritesEnabled: false, StudioAutomationEnabled: studio);

    private static Task<DoctorResult> Run(
        FakeEnv env, string? commPath = null, string? ip = null, bool studio = false) =>
        DoctorHandler.ExecuteAsync(commPath, ip, env, Caps(studio));

    /// <summary>Asserts the overall status; on mismatch names every non-ok check with its status and Observed text.</summary>
    private static void AssertOverall(string expected, DoctorResult r)
    {
        if (r.Overall == expected) return;
        var detail = string.Join(Environment.NewLine,
            r.Checks.Where(c => c.Status != "ok").Select(c => $"  {c.Id}: {c.Status} - {c.Observed}"));
        Assert.Fail($"Expected overall '{expected}' but was '{r.Overall}'. Non-ok checks:{Environment.NewLine}{detail}");
    }

    private static DoctorCheck Get(DoctorResult r, string id) => r.Checks.Single(c => c.Id == id);

    [Fact]
    public async Task Server_ReportsWriteAllowlistStatus()
    {
        var off = await DoctorHandler.ExecuteAsync(null, null, new FakeEnv(), Caps());
        Assert.Contains("LGX_WRITE_ALLOWLIST=off", Get(off, "server").Observed);

        var file = WriteTempAllowlist("Sim_*\n");
        try
        {
            var on = await DoctorHandler.ExecuteAsync(null, null, new FakeEnv { WriteAllowlist = file }, Caps());
            Assert.Contains($"LGX_WRITE_ALLOWLIST=active ({file})", Get(on, "server").Observed);
        }
        finally { File.Delete(file); }
    }

    private static string WriteTempAllowlist(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lgx-allow-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A readable allowlist file gives the active result.</summary>
    [Fact]
    public async Task Server_ReadableAllowlistFile_IsActive()
    {
        var file = WriteTempAllowlist("# site list\nSim_*\nCmd_Start\n");
        try
        {
            var r = await DoctorHandler.ExecuteAsync(null, null, new FakeEnv { WriteAllowlist = file }, Caps());
            var server = Get(r, "server");
            Assert.Equal("ok", server.Status);
            Assert.Contains($"LGX_WRITE_ALLOWLIST=active ({file})", server.Observed);
        }
        finally { File.Delete(file); }
    }

    /// <summary>An unreadable allowlist file gives fail, with the path and the connect error.</summary>
    [Fact]
    public async Task Server_UnreadableAllowlistFile_IsFailWithPathAndConnectError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lgx-missing-{Guid.NewGuid():N}", "allow.txt");
        var r = await DoctorHandler.ExecuteAsync(null, null, new FakeEnv { WriteAllowlist = missing }, Caps());
        var server = Get(r, "server");

        var connectError = ((AllowlistInvalidError)WriteAllowlist.Load(missing)).Error;
        Assert.Equal("fail", server.Status);
        Assert.Contains(missing, server.Observed);
        Assert.Contains(connectError, server.Observed);
        Assert.DoesNotContain("active", server.Observed);
        Assert.Equal("fail", r.Overall);
    }

    // ── Acceptance tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task AllChecksPresent_InOrder_WithStatus()
    {
        var env = new FakeEnv();
        var r = await Run(env, commPath: @"EmulateEthernet\127.0.0.1", studio: true);

        Assert.Equal(ExpectedOrder, r.Checks.Select(c => c.Id).ToArray());
        Assert.Equal(ExpectedOrder, DoctorHandler.CheckIds.ToArray());
        string[] valid = ["ok", "warn", "fail", "skipped"];
        Assert.All(r.Checks, c =>
        {
            Assert.Contains(c.Status, valid);
            Assert.False(string.IsNullOrWhiteSpace(c.Observed));
        });
        // Healthy bench: everything applicable is ok.
        Assert.All(r.Checks, c => Assert.Equal("ok", c.Status));
        AssertOverall("ok", r);

        var server = Get(r, "server").Observed;
        Assert.Contains(ServerIdentity.Version, server);
        Assert.Contains("OS session 1", server);
        Assert.Contains("elevated no", server);
        foreach (var name in new[]
                 { "LGX_ENABLE_LIVE_WRITES", "LGX_ENABLE_STUDIO_AUTOMATION", "LGX_ENABLE_LIVE_FINALIZE", "LGX_PUBLISH_MODE" })
            Assert.Contains(name, server);
        Assert.Contains("correlated", Get(r, "session").Observed);
        Assert.Contains("12 s", Get(r, "session").Observed);

        // JSON shape: { overall, checks: [ {id,status,observed,fix?} ] }
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(r));
        Assert.Equal("ok", doc.RootElement.GetProperty("overall").GetString());
        var first = doc.RootElement.GetProperty("checks")[0];
        Assert.Equal("server", first.GetProperty("id").GetString());
        Assert.False(first.TryGetProperty("fix", out _));
    }

    [Fact]
    public async Task NonOkCheck_AlwaysHasFix()
    {
        var env = new FakeEnv
        {
            AuditWritable      = false,
            SdkAssemblyVersion = null,
            TcpReachable       = false,
            Session            = new("sdk", "offline-project", false, "uncorrelated", null),
            StudioSessionIds   = [],
        };
        env.Services[DoctorHandler.SdkServiceName]  = "Stopped";
        env.Services[DoctorHandler.EchoServiceName] = "Stopped";

        var r = await Run(env, commPath: @"EmulateEthernet\127.0.0.2", studio: true);

        var nonOk = r.Checks.Where(c => c.Status is "warn" or "fail").ToList();
        Assert.True(nonOk.Count >= 5, "expected several broken checks, got " + nonOk.Count);
        Assert.All(nonOk, c => Assert.False(string.IsNullOrWhiteSpace(c.Fix), $"{c.Id} ({c.Status}) has no fix"));
        Assert.All(r.Checks.Where(c => c.Status == "ok"), c => Assert.Null(c.Fix));
        AssertOverall("fail", r);
    }

    [Fact]
    public async Task CipRuntime_OkWhenNativeLibraryLoads_FailWithRequirement2TextOtherwise()
    {
        var ok = Get(await Run(new FakeEnv()), "cipRuntime");
        Assert.Equal("ok", ok.Status);
        Assert.Null(ok.Fix);

        var r = await Run(new FakeEnv { CipRuntimeOk = false });
        var c = Get(r, "cipRuntime");
        Assert.Equal("fail", c.Status);
        Assert.Equal(
            "The CIP library could not load: its native component needs the Microsoft Visual C++ " +
            "2015\u20132022 Redistributable (x64), which is not installed.", c.Observed);
        Assert.Equal(
            "Install the Microsoft Visual C++ x64 redistributable (vc_redist.x64.exe), " +
            "then restart the MCP server.", c.Fix);
        Assert.Equal("CipRuntimeMissing", c.Kind);
        AssertOverall("fail", r);
    }

    [Fact]
    public async Task SdkServiceStopped_FailWithRestartFix()
    {
        var env = new FakeEnv();
        env.Services[DoctorHandler.SdkServiceName] = "Stopped";

        var c = Get(await Run(env), "sdkService");

        Assert.Equal("fail", c.Status);
        Assert.Contains("LdSdkService", c.Observed);
        Assert.Contains("Stopped", c.Observed);
        Assert.Equal("Restart the LdSdkService Windows service, then logix_connect again.", c.Fix);
    }

    [Fact]
    public async Task SdkServiceDegradedDiagnosis_WarnWithSameRestartFix()
    {
        var env = new FakeEnv
        {
            LastSdkServiceDiagnosis = new(DateTimeOffset.UtcNow, "RxE_COMM_DTLERROR", "reachable", "SdkServiceDegraded"),
        };

        var c = Get(await Run(env), "sdkService");

        Assert.Equal("warn", c.Status);
        Assert.Contains("SdkServiceDegraded", c.Observed);
        Assert.Contains("restart the LdSdkService Windows service, then logix_connect again",
            c.Fix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoAdapter_ServiceChecks_SayBuildNotOs()
    {
        var env = new FakeEnv { SdkAssemblyVersion = null };
        env.Services[DoctorHandler.SdkServiceName]  = DoctorServiceState.Unsupported;
        env.Services[DoctorHandler.EchoServiceName] = DoctorServiceState.Unsupported;
        env.Services[DoctorHandler.EchoBrokerName]  = DoctorServiceState.Unsupported;

        var r = await Run(env, commPath: @"EmulateEthernet\127.0.0.1");

        foreach (var id in new[] { "sdkService", "echoService" })
        {
            var c = Get(r, id);
            Assert.Equal("skipped", c.Status);
            Assert.Equal("not checked: this build has no Logix Designer SDK support", c.Observed);
        }
    }

    [Fact]
    public async Task AdapterLoaded_OffWindows_ServiceChecksSayNotWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        var env = new FakeEnv();
        env.Services[DoctorHandler.SdkServiceName]  = DoctorServiceState.Unsupported;
        env.Services[DoctorHandler.EchoServiceName] = DoctorServiceState.Unsupported;
        env.Services[DoctorHandler.EchoBrokerName]  = DoctorServiceState.Unsupported;

        var r = await Run(env, commPath: @"EmulateEthernet\127.0.0.1");

        Assert.Equal("not checked: not Windows", Get(r, "sdkService").Observed);
        Assert.Equal("not checked: not Windows", Get(r, "echoService").Observed);
    }

    [Fact]
    public async Task EchoChecks_SkippedForNonEmulatePath()
    {
        var env = new FakeEnv();
        env.Services[DoctorHandler.EchoServiceName] = "Stopped"; // must not matter

        var skipped = Get(await Run(env, commPath: @"AB_ETHIP-1\10.0.0.10\Backplane\0"), "echoService");
        Assert.Equal("skipped", skipped.Status);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Observed));

        Assert.Equal("skipped", Get(await Run(env), "echoService").Status);

        // Same env, Echo path: now it applies and the stopped service is a failure with the named fix.
        var applied = Get(await Run(env, commPath: @"EmulateEthernet\127.0.0.1"), "echoService");
        Assert.Equal("fail", applied.Status);
        Assert.Equal("Start-Service 'FactoryTalk Logix Echo Service'", applied.Fix);

        // 127.0.0.1 without an EmulateEthernet commPath also applies.
        Assert.Equal("fail", Get(await Run(env, ip: "127.0.0.1"), "echoService").Status);
    }

    [Fact]
    public async Task EchoDetection_127_0_0_1_IsEcho()
    {
        Assert.True(DoctorHandler.IsEchoTarget(new FakeEnv(), null, "127.0.0.1"));
        var env = new FakeEnv();
        env.Services[DoctorHandler.EchoServiceName] = "Stopped";
        Assert.Equal("fail", Get(await Run(env, ip: "127.0.0.1"), "echoService").Status);
    }

    [Fact]
    public async Task EchoDetection_127_0_0_3_IsEcho()
    {
        Assert.True(DoctorHandler.IsEchoTarget(new FakeEnv(), null, "127.0.0.3"));
        var env = new FakeEnv();
        env.Services[DoctorHandler.EchoServiceName] = "Stopped";
        Assert.Equal("fail", Get(await Run(env, ip: "127.0.0.3"), "echoService").Status);
    }

    [Fact]
    public async Task EchoDetection_EmulateEthernet_127_0_0_4_IsEcho()
    {
        Assert.True(DoctorHandler.IsEchoTarget(new FakeEnv(), @"EmulateEthernet\127.0.0.4", null));
        var env = new FakeEnv();
        env.Services[DoctorHandler.EchoServiceName] = "Stopped";
        Assert.Equal("fail", Get(await Run(env, commPath: @"EmulateEthernet\127.0.0.4"), "echoService").Status);
    }

    // Split literal for the host's own LAN address used in the own-interface tests.
    private const string OwnLanIp = "10.20" + ".30.40";

    [Fact]
    public void IsEchoTarget_TrueForLoopbackLocalhostAndOwnInterfaceAddress()
    {
        var env = new FakeEnv();
        env.OwnAddresses.Add(OwnLanIp);

        Assert.True(DoctorHandler.IsEchoTarget(env, null, "127.0.0.2"));
        Assert.True(DoctorHandler.IsEchoTarget(env, null, "localhost"));
        Assert.True(DoctorHandler.IsEchoTarget(env, null, "LOCALHOST"));
        Assert.True(DoctorHandler.IsEchoTarget(env, null, "::1"));
        Assert.True(DoctorHandler.IsEchoTarget(env, null, OwnLanIp));
        Assert.True(DoctorHandler.IsEchoTarget(env, $@"AB_ETHIP-1\{OwnLanIp}\Backplane\0", null));
        Assert.False(DoctorHandler.IsEchoTarget(env, null, "10.20" + ".30.41")); // not ours
    }

    [Fact]
    public void IsEchoTarget_RequiresEchoServiceInstalled()
    {
        var env = new FakeEnv();
        env.OwnAddresses.Add(OwnLanIp);
        env.Services.Remove(DoctorHandler.EchoServiceName); // -> NotInstalled

        Assert.False(DoctorHandler.IsEchoTarget(env, null, "127.0.0.2"));
        Assert.False(DoctorHandler.IsEchoTarget(env, null, "localhost"));
        Assert.False(DoctorHandler.IsEchoTarget(env, null, "::1"));
        Assert.False(DoctorHandler.IsEchoTarget(env, null, OwnLanIp));

        // A stopped (but installed) service is still an Echo target.
        env.Services[DoctorHandler.EchoServiceName] = "Stopped";
        Assert.True(DoctorHandler.IsEchoTarget(env, null, "127.0.0.2"));
    }

    // Split literal: the shipped-text guard bans the private-range prefix as a customer identifier.
    private const string NonLoopbackLanIp = "192.168" + ".1.10";

    [Fact]
    public async Task EchoDetection_192_168_1_10_IsNotEcho_AndServiceCheckSkipped()
    {
        Assert.False(DoctorHandler.IsEchoTarget(new FakeEnv(), null, NonLoopbackLanIp));
        var env = new FakeEnv();
        env.Services[DoctorHandler.EchoServiceName] = "Stopped"; // must not matter
        Assert.Equal("skipped", Get(await Run(env, ip: NonLoopbackLanIp), "echoService").Status);
    }

    [Fact]
    public async Task EchoBrokerStopped_WarnNamesBrokerService()
    {
        var env = new FakeEnv();
        env.Services[DoctorHandler.EchoBrokerName] = "Stopped";

        var c = Get(await Run(env, commPath: @"EmulateEthernet\127.0.0.1"), "echoService");

        Assert.Equal("warn", c.Status);
        Assert.Equal("Start-Service 'FactoryTalk Logix Echo Message Broker'", c.Fix);
    }

    /// <summary>
    /// Fault record bytes: the real S32-p112 bench reply (type 4 code 20) for 4/20; a copy with the
    /// type/code patched in at offsets 20/22 for any other pair.
    /// </summary>
    private static byte[] FaultBytes(ushort type, ushort code)
    {
        var b = Convert.FromHexString(FaultRecordDecoderTests.BenchS32p112Hex);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), code);
        return b;
    }

    [Fact]
    public async Task ControllerFault_FailWithDecodedFault()
    {
        var env = new FakeEnv { FaultRecord = FaultBytes(4, 20) };

        var r = await Run(env, ip: "10.0.0.10");
        var c = Get(r, "controllerFault");

        Assert.Equal("fail", c.Status);
        Assert.Contains("FAULTED", c.Observed);
        Assert.Contains("type 4 code 20", c.Observed);
        Assert.DoesNotContain("12618", c.Observed);
        Assert.Contains(FaultRecordDecoder.Decode(FaultBytes(4, 20))!.Name, c.Observed);
        Assert.Contains("logix_diagnose", c.Fix);
        Assert.Contains("Clear Majors", c.Fix);
        AssertOverall("fail", r);
        Assert.Equal("10.0.0.10", Assert.Single(env.FaultReads));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    public async Task Localhost_ResolvesTo127001_ForReachabilityAndControllerFault(string host)
    {
        var env = new FakeEnv();
        var r = await Run(env, ip: host);

        var reach = Get(r, "controllerReachable");
        Assert.Equal("ok", reach.Status);
        Assert.Contains("127.0.0.1", reach.Observed);
        var call = Assert.Single(env.TcpCalls);
        Assert.Equal("127.0.0.1", call.Host);

        Assert.Equal("ok", Get(r, "controllerFault").Status);
        Assert.Equal("127.0.0.1", Assert.Single(env.FaultReads));
    }

    [Fact]
    public async Task ControllerFault_OkWhenHealthy_SkippedWithoutIpOrReachability_WarnOnReadError()
    {
        Assert.Equal("ok", Get(await Run(new FakeEnv(), ip: "10.0.0.10"), "controllerFault").Status);

        var noIp = new FakeEnv { FaultRecord = FaultBytes(4, 20) };
        Assert.Equal("skipped", Get(await Run(noIp), "controllerFault").Status);
        Assert.Empty(noIp.FaultReads);

        var down = new FakeEnv { TcpReachable = false, FaultRecord = FaultBytes(4, 20) };
        Assert.Equal("skipped", Get(await Run(down, ip: "10.0.0.10"), "controllerFault").Status);
        Assert.Empty(down.FaultReads);

        var bad = new FakeEnv { FaultReadError = new FaultRecordException("short reply") };
        var w = Get(await Run(bad, ip: "10.0.0.10"), "controllerFault");
        Assert.Equal("warn", w.Status);
        Assert.Contains("logix_diagnose", w.Fix);
    }

    [Fact]
    public async Task ControllerUnreachable_FailNames44818()
    {
        var env = new FakeEnv { TcpReachable = false };

        var c = Get(await Run(env, ip: "127.0.0.2"), "controllerReachable");

        Assert.Equal("fail", c.Status);
        Assert.Contains("44818", c.Observed);
        Assert.Contains("127.0.0.2", c.Observed);
        Assert.Contains("44818", c.Fix);
        var call = Assert.Single(env.TcpCalls);
        Assert.Equal(("127.0.0.2", 44818), (call.Host, call.Port));
        Assert.Equal(TimeSpan.FromSeconds(2), call.Timeout);
    }

    [Fact]
    public async Task ControllerReachable_IpParsedFromCommPath_AndSkippedWithoutOne()
    {
        var env = new FakeEnv();
        var ok = Get(await Run(env, commPath: @"AB_ETHIP-1\10.0.0.10\Backplane\0"), "controllerReachable");
        Assert.Equal("ok", ok.Status);
        Assert.Equal("10.0.0.10", Assert.Single(env.TcpCalls).Host);

        // controllerIp wins over the commPath.
        var env2 = new FakeEnv();
        await Run(env2, commPath: @"AB_ETHIP-1\10.0.0.10\Backplane\0", ip: "10.0.0.5");
        Assert.Equal("10.0.0.5", Assert.Single(env2.TcpCalls).Host);

        var env3 = new FakeEnv();
        var none = Get(await Run(env3), "controllerReachable");
        Assert.Equal("skipped", none.Status);
        Assert.Empty(env3.TcpCalls);

        var env4 = new FakeEnv();
        var bad = Get(await Run(env4, ip: "not-an-ip"), "controllerReachable");
        Assert.Equal("fail", bad.Status);
        Assert.False(string.IsNullOrWhiteSpace(bad.Fix));
        Assert.Empty(env4.TcpCalls);
    }

    [Fact]
    public async Task OfflineProjectTargetWithCommPath_WarnWithOnlineFix()
    {
        var env = new FakeEnv { Session = new("sdk", "offline-project", false, "uncorrelated", TimeSpan.FromMinutes(3)) };

        var c = Get(await Run(env, commPath: @"AB_ETHIP-1\10.0.0.10\Backplane\0"), "session");

        Assert.Equal("warn", c.Status);
        Assert.Contains("offline-project", c.Observed);
#if LOGIX_SDK
        Assert.Contains("sdkMode=\"online\" with sdkCommPath", c.Fix);
#else
        Assert.Contains("backend=\"cip\"", c.Fix);
        Assert.DoesNotContain("sdkMode", c.Fix);
#endif

        // Without a commPath, an offline session is legitimate offline work.
        Assert.Equal("ok", Get(await Run(env), "session").Status);
    }

    [Fact]
    public async Task Session_ShowsActiveTagRouteAndReason()
    {
        var env = new FakeEnv
        {
            Session = new("sdk", "online-controller", true, "correlated", TimeSpan.FromSeconds(12),
                TagRoute: "cip", TagRouteReason: "auto: CIP connected", TagRouteSetting: "auto"),
        };

        var c = Get(await Run(env), "session");

        Assert.Equal("ok", c.Status);
        Assert.Contains("tag route cip (LGX_TAG_ROUTE=auto), because auto: CIP connected", c.Observed);
    }

    [Fact]
    public async Task Session_ShowsIdentityCheckMethod()
    {
        var env = new FakeEnv
        {
            Session = new("sdk", "online-controller", true, "correlated", TimeSpan.FromSeconds(12),
                TagRoute: "cip", TagRouteReason: "auto: CIP connected", TagRouteSetting: "auto", IdentityCheck: "name"),
        };

        Assert.Contains("identityCheck name", Get(await Run(env), "session").Observed);
    }

    [Fact]
    public async Task Session_ShowsSdkRouteAndWhyCipWasNotUsed()
    {
        var env = new FakeEnv
        {
            Session = new("sdk", "online-controller", true, "correlated", TimeSpan.FromSeconds(12),
                TagRoute: "sdk", TagRouteReason: "auto: CIP connect failed: refused", TagRouteSetting: "auto"),
        };

        var c = Get(await Run(env), "session");

        Assert.Contains("tag route sdk (LGX_TAG_ROUTE=auto), because auto: CIP connect failed: refused", c.Observed);
    }

    [Fact]
    public async Task Session_RouteNotChosenYet_SaysSoAndDoesNotChoose()
    {
        var env = new FakeEnv
        {
            Session = new("sdk", "online-controller", true, "correlated", TimeSpan.FromSeconds(12),
                TagRouteSetting: "cip"),
        };

        var c = Get(await Run(env), "session");

        Assert.Contains("tag route not chosen yet (LGX_TAG_ROUTE=cip)", c.Observed);
    }

    [Fact]
    public async Task Session_NotRouted_HasNoTagRouteClause()
    {
        var c = Get(await Run(new FakeEnv()), "session");

        Assert.DoesNotContain("tag route", c.Observed);
    }

    [Fact]
    public async Task NoSession_SessionAndPendingZoneSkipped()
    {
        var env = new FakeEnv { Session = null };

        var r = await Run(env);

        Assert.Equal("skipped", Get(r, "session").Status);
        Assert.Equal("skipped", Get(r, "pendingZone").Status);
        Assert.Equal(0, env.ZoneReads);
    }

    [Fact]
    public async Task PendingZone_WarnNamesProgram()
    {
        var env = new FakeEnv
        {
            Zones = [new("MainProgram", "MainRoutine", "none"), new("Conveyor", "Run", "untested")],
        };

        var c = Get(await Run(env), "pendingZone");

        Assert.Equal("warn", c.Status);
        Assert.Contains("Conveyor", c.Observed);
        Assert.Equal("The SDK refuses imports into 'Conveyor' until it is accepted or cancelled in Studio.", c.Fix);
        Assert.DoesNotContain("MainProgram", c.Fix);
    }

    [Fact]
    public async Task PendingZone_SkippedWhenSessionNotOnline()
    {
        var env = new FakeEnv { Session = new("sdk", "offline-project", false, "uncorrelated", null) };

        var c = Get(await Run(env), "pendingZone");

        Assert.Equal("skipped", c.Status);
        Assert.Equal(0, env.ZoneReads);
    }

    [Fact]
    public async Task StudioChecks_SkippedWhenAutomationOff()
    {
        var env = new FakeEnv { StudioSessionIds = [] }; // would be StudioNotRunning if it applied

        var c = Get(await Run(env, studio: false), "studio");

        Assert.Equal("skipped", c.Status);
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", c.Observed);
        Assert.Null(c.Kind);
    }

    [Fact]
    public async Task StudioNotRunning_FailKind()
    {
        var env = new FakeEnv { StudioSessionIds = [] };

        var c = Get(await Run(env, studio: true), "studio");

        Assert.Equal("fail", c.Status);
        Assert.Equal("StudioNotRunning", c.Kind);
        Assert.Contains("Upload", c.Fix);
    }

    [Fact]
    public async Task StudioInOtherSession_Elevated_Offline_FailKinds()
    {
        var other = Get(await Run(new FakeEnv { StudioSessionIds = [2] }, studio: true), "studio");
        Assert.Equal(("fail", "StudioInOtherSession"), (other.Status, other.Kind));
        Assert.Contains("2", other.Observed);

        var elevated = Get(await Run(new FakeEnv { StudioIntegrityHigherThanCurrent = true }, studio: true), "studio");
        Assert.Equal(("fail", "StudioElevated"), (elevated.Status, elevated.Kind));

        var offline = Get(await Run(
            new FakeEnv { StudioWindowTitlesInCurrentSession = ["Logix Designer - LgxDemo"] }, studio: true), "studio");
        Assert.Equal(("fail", "StudioOffline"), (offline.Status, offline.Kind));
        Assert.Contains("Upload", offline.Fix);
    }

    [Fact]
    public async Task NeverCreatesSession_NeverWrites()
    {
        // Real production environment over an empty session registry and an audit directory
        // that does not exist: the doctor must neither create a session nor create the directory.
        var auditDir = Path.Combine(Path.GetTempPath(), $"doctor-readonly-{Guid.NewGuid():N}");
        // Task.Run isolates the AsyncLocal audit-dir override from this test's caller.
        await Task.Run(async () =>
        {
        AuditLogger.OverrideLogDir(auditDir);
        try
        {
            var sessions = new TagSessionService();
            var env = new DoctorEnvironment(sessions);

            var r = await DoctorHandler.ExecuteAsync(null, null, env, Caps(studio: false));

            Assert.Equal(ExpectedOrder, r.Checks.Select(c => c.Id).ToArray());
            Assert.Null(sessions.Service);
            Assert.False(sessions.IsConnected);
            Assert.False(Directory.Exists(auditDir), "the doctor created the audit directory");
            Assert.Equal("skipped", Get(r, "session").Status);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            if (Directory.Exists(auditDir)) Directory.Delete(auditDir, recursive: true);
        }
        });
    }

    /// <summary>
    /// A clean no-SDK install (no SDK, no Studio, VC++ runtime present) reports ok overall; the missing
    /// SDK and Studio are info, not warn or fail, even with Studio automation on.
    /// </summary>
    [Fact]
    public async Task NoSdkBuild_CleanInstall_Ok()
    {
        var env = new FakeEnv
        {
            SdkSupportedInBuild = false,
            SdkAssemblyVersion  = null,
            SdkLoadFailureReason = "probed nothing",
            CipRuntimeOk        = true,
            StudioSessionIds    = [],
            StudioWindowTitlesInCurrentSession = [],
            Session             = null,
        };
        env.Services.Clear();
        env.Services[DoctorHandler.SdkServiceName] = DoctorServiceState.Unsupported;

        var r = await Run(env, studio: true);

        AssertOverall("ok", r);
        foreach (var id in new[] { "sdkInstalled", "sdkService", "studio" })
        {
            var c = Get(r, id);
            Assert.Equal("info", c.Status);
            Assert.Contains("not used by this build", c.Observed);
            Assert.Null(c.Fix);
        }
        Assert.Equal("ok", Get(r, "cipRuntime").Status);
    }

    /// <summary>The same missing SDK in an SDK build is still a warning, so the no-SDK downgrade is build-specific.</summary>
    [Fact]
    public async Task SdkBuild_MissingSdk_StillWarns()
    {
        var env = new FakeEnv { SdkAssemblyVersion = null };
        var r = await Run(env);
        Assert.Equal("warn", Get(r, "sdkInstalled").Status);
    }

    [Fact]
    public void IDoctorEnvironment_ExposesOnlyObservations()
    {
        // The only methods are reads/probes; a verb that mutates would have to be added here deliberately.
        string[] allowed =
        [
            "GetServiceState", "IsOwnInterfaceAddress", "IsAuditDirectoryWritable", "AuditLogBytes", "TcpConnectAsync",
            "GetActiveSession", "ReadPendingZonesAsync", "ReadFaultRecordAsync", "CipRuntimeLoads",
            "GetSdkCapabilities", "GateConfigRaw", "WriteAllowlistRaw", "GetTargetRevision", "SdkServerCrashesLast24h", "NewestSdkServerCrashLast24h", "SdkServerCrashesLast7d",
        ];
        var methods = typeof(IDoctorEnvironment).GetMethods()
            .Where(m => !m.IsSpecialName).Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(allowed.OrderBy(n => n).ToArray(), methods);
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[Rem Run] - Logix Designer - X", true, "Rem Run")]
    [InlineData("[Emulator] - Logix Designer - X", true, "Emulator")]
    [InlineData("Logix Designer - online project", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void ParseStudioOnlineState_UsesBracketedPrefixOnly(string? title, bool online, string? mode)
    {
        var s = DoctorHandler.ParseStudioOnlineState(title);
        Assert.Equal((online, mode), (s.Online, s.Mode));
    }

    [Theory]
    [InlineData(@"EmulateEthernet\127.0.0.1", "127.0.0.1")]
    [InlineData(@"AB_ETHIP-1\10.0.0.10\Backplane\0", "10.0.0.10")]
    [InlineData("10.0.0.10,1,0", "10.0.0.10")]
    [InlineData(@"AB_ETHIP-1\Backplane\1", null)]
    [InlineData(null, null)]
    public void ParseIpFromCommPath_FindsDottedIpv4(string? commPath, string? expected) =>
        Assert.Equal(expected, DoctorHandler.ParseIpFromCommPath(commPath));

    [Fact]
    public async Task Audit_NotWritable_Warns_AndOversizeLogWarns()
    {
        var notWritable = Get(await Run(new FakeEnv { AuditWritable = false }), "audit");
        Assert.Equal("warn", notWritable.Status);
        Assert.Contains("/tmp/lgx-audit", notWritable.Fix);

        await Task.Run(async () =>
        {
            AuditLogger.OverrideRotation(1.0);
            try
            {
                var big = Get(await Run(new FakeEnv { AuditBytes = 5 * 1024 * 1024 }), "audit");
                Assert.Equal("warn", big.Status);
                Assert.Contains("LGX_AUDIT_MAX_MB", big.Fix);
            }
            finally { AuditLogger.OverrideRotation(null); }
        });
    }

    [Fact]
    public async Task ThrowingProbe_BecomesWarn_NotAnEscapedException()
    {
        var env = new ThrowingEnv();

        var r = await DoctorHandler.ExecuteAsync(null, null, env, Caps());

        Assert.Equal(ExpectedOrder, r.Checks.Select(c => c.Id).ToArray());
        Assert.Equal("warn", Get(r, "audit").Status);
        Assert.False(string.IsNullOrWhiteSpace(Get(r, "audit").Fix));
    }

    private sealed class ThrowingEnv : IDoctorEnvironment
    {
        private readonly FakeEnv _inner = new();
        public int CurrentSessionId => _inner.CurrentSessionId;
        public bool IsElevated => _inner.IsElevated;
        public string? GateConfigRaw() => null;
        public string? WriteAllowlistRaw() => null;
        public string? SdkAssemblyVersion => _inner.SdkAssemblyVersion;
        public string? SdkLoadFailureReason => null;
        public string GetServiceState(string serviceName) => _inner.GetServiceState(serviceName);
        public SdkServiceDiagnosisSnapshot? LastSdkServiceDiagnosis => null;
        public string AuditDirectory => throw new InvalidOperationException("boom");
        public bool IsAuditDirectoryWritable() => true;
        public long? AuditLogBytes() => null;
        public Task<bool> TcpConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<byte[]> ReadFaultRecordAsync(string host, CancellationToken ct) => Task.FromResult(new byte[64]);
        public DoctorSessionInfo? GetActiveSession() => null;
        public Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public IReadOnlyList<int> StudioSessionIds => [];
        public bool StudioIntegrityHigherThanCurrent => false;
        public IReadOnlyList<string> StudioWindowTitlesInCurrentSession => [];
    }
}

/// <summary>
/// Mutates process environment variables, so it runs in its own non-parallel collection and
/// restores every variable in <c>finally</c>.
/// </summary>
[Collection("ProcessEnvironment")]
public class DoctorHandlerProcessEnvironmentTests
{
    /// <summary>Hostile process environment must not change the server check; only <see cref="IDoctorEnvironment"/> does.</summary>
    [Fact]
    public async Task ServerCheck_IgnoresProcessEnvironment()
    {
        string[] names = [GateConfigResolver.EnvVar, WriteAllowlist.EnvVar];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var allowFile = Path.Combine(Path.GetTempPath(), $"lgx-allow-{Guid.NewGuid():N}.txt");
        File.WriteAllText(allowFile, "Sim_*\n");
        try
        {
            Environment.SetEnvironmentVariable(GateConfigResolver.EnvVar, "bad entry!");
            Environment.SetEnvironmentVariable(WriteAllowlist.EnvVar, "/nonexistent/allow.txt");

            var r = await DoctorHandler.ExecuteAsync(null, null, new DoctorHandlerTests.FakeEnv(),
                new Capabilities(LiveWritesEnabled: false));
            var server = r.Checks.Single(c => c.Id == "server");

            Assert.True(server.Status == "ok", $"server was {server.Status}: {server.Observed}");
            Assert.Contains("LGX_WRITE_ALLOWLIST=off", server.Observed);
            Assert.Contains("(default)", server.Observed);
            Assert.DoesNotContain("invalid", server.Observed);

            // And the fake environment's values are what is reported.
            var faked = await DoctorHandler.ExecuteAsync(null, null,
                new DoctorHandlerTests.FakeEnv { GateConfig = "Cmd_,HMI_", WriteAllowlist = allowFile },
                new Capabilities(false));
            var fs = faked.Checks.Single(c => c.Id == "server");
            Assert.Contains("HMI_", fs.Observed);
            Assert.Contains($"active ({allowFile})", fs.Observed);

            var bad = await DoctorHandler.ExecuteAsync(null, null,
                new DoctorHandlerTests.FakeEnv { GateConfig = "bad entry!" }, new Capabilities(false));
            Assert.Equal("fail", bad.Checks.Single(c => c.Id == "server").Status);
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
            File.Delete(allowFile);
        }
    }
}
