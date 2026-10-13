using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="RunTestHandler"/> (<c>logix_run_test</c>): the emulator gate,
/// the capability and confirmation gates, and the case/step/wait limits. Every refusal must leave
/// the controller untouched, so each test uses a recording tag service.
/// </summary>
public class RunTestHandlerTests
{
    private const string Emulator = "Emulate 5580 Controller";

    /// <summary>Virtual store that records every write and read.</summary>
    private sealed class RecordingService : ITagService
    {
        private readonly VirtualTagService _inner = new(new Dictionary<string, (object, string?)>
        {
            ["Cmd_Start"] = (false, "BOOL"),
            ["Motor_Run"] = (false, "BOOL"),
            ["Speed"]     = (5, "DINT"),
            ["Safety_OK"] = (false, "BOOL"),
            ["Sensor_In"] = (false, "BOOL"),
            ["Cfg_Made"]  = (false, "BOOL"),
        });
        public List<string> Writes { get; } = new();
        /// <summary>When it returns true for a write, the write is silently dropped (simulates a restore that does not stick).</summary>
        public Func<string, object, bool>? SwallowWrite { get; set; }
        /// <summary>Tags whose reads throw (simulates an unreadable tag).</summary>
        public HashSet<string> FailReads { get; } = new();
        public int Reads { get; private set; }
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default) { Reads++; return FailReads.Contains(t) ? throw new InvalidOperationException("unreadable") : _inner.ReadAsync(t, ct); }
        public async Task WriteAsync(string t, object v, CancellationToken ct = default)
        {
            Writes.Add(t);
            if (SwallowWrite?.Invoke(t, v) == true) return;
            await _inner.WriteAsync(t, v, ct);
            // Stand-in for the PLC program: Motor_Run follows Cmd_Start.
            if (t == "Cmd_Start") await _inner.WriteAsync("Motor_Run", v, ct);
        }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) => _inner.ListTagsAsync(ct);
    }

    private static (TagSessionService Session, RecordingService Svc) Session(string? host = "127.0.0.1")
    {
        var svc = new RecordingService();
        var session = new TagSessionService();
        // The session service is gated like a real SDK session; the recording service stands in for
        // the TypedSdkTagService beneath the gate.
        session.Attach(new GatedTagService(svc, Policy()), isVirtual: false, isLiveController: true, controllerHost: host);
        return (session, svc);
    }

    private static WriteGatePolicy Policy() => new(
        Grants:
        [
            new GrantEntry("Cmd_Start", null, null, "command prefix 'Cmd_'"),
            new GrantEntry("Speed",     null, null, "granted for test"),
        ],
        Denies:
        [
            new DenyEntry("Motor_Run", DenyReason.ProgramWritten, "Tag 'Motor_Run' is written by program logic at MainRoutine rung 1."),
            new DenyEntry("Safety_OK", DenyReason.SafetyTag, "Tag 'Safety_OK' matches safety prefix 'Safety_'."),
            new DenyEntry("Sensor_In", DenyReason.NotDesignated, "Tag 'Sensor_In' does not match any command prefix."),
            new DenyEntry("Cfg_Made",  DenyReason.ExternalAccessNotReadWrite, "Tag 'Cfg_Made' has External Access 'Read Only'.", "Read Only"),
        ]);

    private static RunTestHandler.IdentityReader Identity(string product) =>
        (_, _) => Task.FromResult(new EmulatorIdentityInfo(product, 36, 11, 1, 14, 166, 1u));

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static RunTestCase StartCase(string name = "start", int? waitMs = 0) =>
        new(name, Steps:
        [
            new RunTestStep(
                Set:    [new RunTestSet("Cmd_Start", J("true"))],
                WaitMs: waitMs,
                Expect: [new RunTestExpect("Motor_Run", EqualsValue: J("true"))]),
        ]);

    private static Task<object> Run(
        TagSessionService session, IReadOnlyList<RunTestCase>? cases, bool confirmed = true,
        bool liveWrites = true, RunTestHandler.IdentityReader? identity = null,
        List<int>? waits = null) =>
        RunTestHandler.ExecuteAsync(
            cases, confirmed, session, new Capabilities(LiveWritesEnabled: liveWrites),
            identity ?? Identity(Emulator),
            (ms, _) => { waits?.Add(ms); return Task.CompletedTask; });

    // ── Acceptance tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task Allowlist_RestrictsTestWrites()
    {
        var (session, svc) = Session();
        var path = Path.Combine(Path.GetTempPath(), $"allowlist-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "Cmd_Start\n");
        try
        {
            session.SetWriteAllowlist(Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(path)));

            // Listed tag: runs.
            Assert.IsType<RunTestResult>(await Run(session, [StartCase()]));

            // Unlisted tag (otherwise settable by a test): refused NotOnAllowlist, nothing written.
            svc.Writes.Clear();
            var other = new RunTestCase("speed", Steps:
            [
                new RunTestStep(Set: [new RunTestSet("Speed", J("7"))], WaitMs: 0, Expect: []),
            ]);
            var err = Assert.IsType<RunTestError>(await Run(session, [other]));
            Assert.Equal(ErrorKinds.TestWriteRefused, err.Kind);
            var refusal = Assert.Single(err.Refused!);
            Assert.Equal(nameof(DenyReason.NotOnAllowlist), refusal.Reason);
            Assert.Contains(path, refusal.Provenance, StringComparison.Ordinal);
            Assert.Empty(svc.Writes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NonEmulatorProductName_EmulatorOnly_NothingWritten()
    {
        var (session, svc) = Session();
        var r = await Run(session, [StartCase()], identity: Identity("1756-L83E/B"));

        var err = Assert.IsType<RunTestError>(r);
        Assert.Equal("EmulatorOnly", err.Kind);
        Assert.Equal(ErrorKinds.EmulatorOnly, err.Kind);
        Assert.Equal("1756-L83E/B", err.ProductName);
        Assert.Contains("1756-L83E/B", err.Error);
        Assert.Empty(svc.Writes);
        Assert.Equal(0, svc.Reads);
    }

    [Fact]
    public async Task NonEmulatorProductName_RefusedEvenWithoutLiveWritesOrConfirmation()
    {
        // The emulator fact is checked before the flags, so the refusal names the product.
        var (session, svc) = Session();
        var r = await Run(session, [StartCase()], confirmed: false, liveWrites: false,
            identity: Identity("1756-L83E/B"));
        Assert.Equal("EmulatorOnly", Assert.IsType<RunTestError>(r).Kind);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task IdentityUnavailable_Refused_NothingWritten()
    {
        var (session, svc) = Session();
        var r = await Run(session, [StartCase()],
            identity: (_, _) => throw new EmulatorIdentityException("connection refused"));

        var err = Assert.IsType<RunTestError>(r);
        Assert.Equal("IdentityUnavailable", err.Kind);
        Assert.Empty(svc.Writes);

        // No controller address on the session also cannot establish identity.
        var (noHost, svc2) = Session(host: null);
        var r2 = await Run(noHost, [StartCase()]);
        Assert.Equal("IdentityUnavailable", Assert.IsType<RunTestError>(r2).Kind);
        Assert.Empty(svc2.Writes);
    }

    [Fact]
    public async Task RequiresLiveWritesAndConfirmation()
    {
        var (session, svc) = Session();

        var noLive = await Run(session, [StartCase()], liveWrites: false);
        Assert.Equal("CapabilityDisabled", Assert.IsType<RunTestError>(noLive).Kind);

        var noConfirm = await Run(session, [StartCase()], confirmed: false);
        Assert.Equal("ConfirmationRequired", Assert.IsType<RunTestError>(noConfirm).Kind);

        Assert.Empty(svc.Writes);

        // Both satisfied on an emulator: the run proceeds.
        var ok = Assert.IsType<RunTestResult>(await Run(session, [StartCase()]));
        Assert.Equal("pass", ok.Overall);
        Assert.Equal(Emulator, ok.ProductName);
        Assert.Contains("Cmd_Start", svc.Writes);
    }

    [Fact]
    public async Task NoSession_NoActiveSession()
    {
        var r = await Run(new TagSessionService(), [StartCase()]);
        Assert.Equal(ErrorKinds.NoActiveSession, Assert.IsType<RunTestError>(r).Kind);
    }

    [Fact]
    public async Task LimitsClampedAndEnforced()
    {
        var (session, svc) = Session();

        // 21 cases: refused, nothing written.
        var tooManyCases = Enumerable.Range(0, RunTestHandler.MaxCases + 1)
            .Select(i => StartCase($"c{i}")).ToList();
        var r1 = Assert.IsType<RunTestError>(await Run(session, tooManyCases));
        Assert.Equal("LimitExceeded", r1.Kind);

        // 51 steps in one case: refused, nothing written.
        var tooManySteps = new RunTestCase("big",
            Steps: Enumerable.Range(0, RunTestHandler.MaxStepsPerCase + 1)
                .Select(_ => new RunTestStep(Set: [new RunTestSet("Cmd_Start", J("true"))])).ToList());
        var r2 = Assert.IsType<RunTestError>(await Run(session, [tooManySteps]));
        Assert.Equal("LimitExceeded", r2.Kind);
        Assert.Empty(svc.Writes);

        // Exactly the limits are accepted.
        var atLimit = Enumerable.Range(0, RunTestHandler.MaxCases)
            .Select(i => new RunTestCase($"c{i}",
                Steps: [new RunTestStep(WaitMs: 0, Expect: [new RunTestExpect("Speed", Min: 0)])])).ToList();
        Assert.IsType<RunTestResult>(await Run(session, atLimit));
        var fiftySteps = new RunTestCase("fifty",
            Steps: Enumerable.Range(0, RunTestHandler.MaxStepsPerCase)
                .Select(_ => new RunTestStep(WaitMs: 0)).ToList());
        Assert.IsType<RunTestResult>(await Run(session, [fiftySteps]));

        // waitMs: default 200, clamped to [0, 10000].
        Assert.Equal(200, RunTestHandler.ClampWaitMs(null));
        Assert.Equal(0, RunTestHandler.ClampWaitMs(-5));
        Assert.Equal(10_000, RunTestHandler.ClampWaitMs(60_000));
        Assert.Equal(1234, RunTestHandler.ClampWaitMs(1234));

        var waits = new List<int>();
        var wcase = new RunTestCase("waits", Steps:
        [
            new RunTestStep(),                  // default
            new RunTestStep(WaitMs: -1),        // clamped up
            new RunTestStep(WaitMs: 99_999),    // clamped down
        ]);
        var res = Assert.IsType<RunTestResult>(await Run(session, [wcase], waits: waits));
        Assert.Equal([200, 0, 10_000], waits);
        Assert.Equal([200, 0, 10_000], res.Cases[0].Steps.Select(s => s.WaitMs).ToArray());
    }

    // ── Test write rules ─────────────────────────────────────────

    private static RunTestCase SetCase(string tag, string json = "true") =>
        new("c", Steps: [new RunTestStep(Set: [new RunTestSet(tag, J(json))], WaitMs: 0)]);

    [Fact]
    public async Task ProgramWrittenTag_Refused()
    {
        var (session, svc) = Session();
        // The refused tag is in the second case: nothing from the first may be written either.
        var r = await Run(session, [StartCase(), SetCase("Motor_Run")]);

        var err = Assert.IsType<RunTestError>(r);
        Assert.Equal(ErrorKinds.TestWriteRefused, err.Kind);
        var refusal = Assert.Single(err.Refused!);
        Assert.Equal("Motor_Run", refusal.Tag);
        Assert.Equal("ProgramWritten", refusal.Reason);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task ReadOnlyCreatedTag_Refused()
    {
        // External Access Read Only (what lgx-mcp gives tags it creates), via the policy.
        var (session, svc) = Session();
        var r = Assert.IsType<RunTestError>(await Run(session, [SetCase("Cfg_Made")]));
        Assert.Equal(ErrorKinds.TestWriteRefused, r.Kind);
        Assert.Equal("ExternalAccessNotReadWrite", Assert.Single(r.Refused!).Reason);

        // Created by this session: refused whatever the policy says about the tag.
        session.RecordCreatedTags(["Sensor_In"], "prop-1");
        var r2 = Assert.IsType<RunTestError>(await Run(session, [SetCase("Sensor_In")]));
        Assert.Equal("CreatedByThisSession", Assert.Single(r2.Refused!).Reason);

        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task InputAndSafetyPrefixedTags_AllowedOnEmulator_Disclosed()
    {
        var (session, svc) = Session();
        var testCase = new RunTestCase("interlock",
            Setup: [new RunTestSet("Safety_OK", J("true"))],
            Steps:
            [
                new RunTestStep(
                    Set:    [new RunTestSet("Sensor_In", J("true")), new RunTestSet("Cmd_Start", J("true"))],
                    WaitMs: 0,
                    Expect: [new RunTestExpect("Motor_Run", EqualsValue: J("true"))]),
            ]);

        var res = Assert.IsType<RunTestResult>(await Run(session, [testCase]));

        Assert.Equal("pass", res.Overall);
        // Every write reached the service beneath the gate (the typed SDK service in a real session).
        // Test writes first, then the snapshot restore of each distinct tag.
        Assert.Equal(["Safety_OK", "Sensor_In", "Cmd_Start"], svc.Writes.Take(3).ToArray());
        Assert.Equal(["Safety_OK", "Sensor_In", "Cmd_Start"],
            res.Cases[0].Restored!.Select(r => r.Tag).OrderBy(t => t, StringComparer.Ordinal)
                .OrderBy(t => Array.IndexOf(new[] { "Safety_OK", "Sensor_In", "Cmd_Start" }, t)).ToArray());
        Assert.All(res.Cases[0].Restored!, r => Assert.True(r.Confirmed));

        // Disclosure: Safety_OK and Sensor_In would be denied by logix_write; Cmd_Start would not.
        var disclosed = res.Cases[0].OrdinaryWriteDenied!;
        Assert.Equal(["Safety_OK", "Sensor_In"], disclosed.Select(d => d.Tag).ToArray());
        Assert.Equal(["SafetyTag", "NotDesignated"], disclosed.Select(d => d.Reason).ToArray());

        // And ordinary logix_write does deny them through the gate.
        var gatedDenied = await WriteTagHandler.ExecuteAsync("Safety_OK", "true", session,
            capabilities: new Capabilities(LiveWritesEnabled: true));
        Assert.Equal("WriteDenied", Assert.IsType<WriteTagError>(gatedDenied).Kind);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res, res.GetType()));
        Assert.Equal(2, doc.RootElement.GetProperty("cases")[0].GetProperty("ordinaryWriteDenied").GetArrayLength());
    }

    [Fact]
    public async Task UngatedSession_Refused_NothingWritten()
    {
        var svc = new RecordingService();
        var session = new TagSessionService();
        session.Attach(svc, isVirtual: false, isLiveController: true, controllerHost: "127.0.0.1");
        var r = Assert.IsType<RunTestError>(await Run(session, [StartCase()]));
        Assert.Equal(ErrorKinds.TestWriteRefused, r.Kind);
        Assert.Empty(svc.Writes);
    }

    // ── Behaviour of the basic runner ─────────────────────────────────────────

    [Fact]
    public async Task EmptyOrStepless_InvalidTest()
    {
        var (session, svc) = Session();
        Assert.Equal("InvalidTest", Assert.IsType<RunTestError>(await Run(session, [])).Kind);
        Assert.Equal("InvalidTest",
            Assert.IsType<RunTestError>(await Run(session, [new RunTestCase("none")])).Kind);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task FailedExpectation_FailsCaseAndOverall_AndResultSerializes()
    {
        var (session, _) = Session();
        var bad = new RunTestCase("bad", Steps:
        [
            new RunTestStep(WaitMs: 0, Expect:
            [
                new RunTestExpect("Speed", EqualsValue: J("6")),
                new RunTestExpect("Speed", Min: 1, Max: 9),
            ]),
        ]);
        var res = Assert.IsType<RunTestResult>(await Run(session, [StartCase(), bad]));

        Assert.Equal("fail", res.Overall);
        Assert.Equal(["pass", "fail"], res.Cases.Select(c => c.Result).ToArray());
        var checks = res.Cases[1].Steps[0].Expect;
        Assert.Equal(["fail", "pass"], checks.Select(c => c.Verdict).ToArray());
        Assert.Equal(5, checks[0].Observed);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res, res.GetType()));
        Assert.Equal("fail", doc.RootElement.GetProperty("overall").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("cases").GetArrayLength());
    }

    private sealed class CaseProgress(List<(int Completed, int Total, string Message)> list)
        : IProgress<(int Completed, int Total, string Message)>
    {
        public void Report((int Completed, int Total, string Message) value) => list.Add(value);
    }

    /// <summary>Progress is reported per case, after the gates, without changing the result.</summary>
    [Fact]
    public async Task Progress_ReportedPerCase_AndResultUnchanged()
    {
        var reports = new List<(int Completed, int Total, string Message)>();
        var (session, _) = Session();
        var res = Assert.IsType<RunTestResult>(await RunTestHandler.ExecuteAsync(
            [StartCase("a"), StartCase("b")], true, session, new Capabilities(LiveWritesEnabled: true),
            Identity(Emulator), (_, _) => Task.CompletedTask, progress: new CaseProgress(reports)));

        Assert.Equal("pass", res.Overall);
        Assert.Equal([0, 1, 2], reports.Select(r => r.Completed));
        Assert.All(reports, r => Assert.Equal(2, r.Total));
        Assert.Contains("a", reports[0].Message);
        Assert.Contains("b", reports[1].Message);
    }

    /// <summary>A refused run reports no progress: nothing started.</summary>
    [Fact]
    public async Task Progress_NotReported_WhenRefused()
    {
        var reports = new List<(int Completed, int Total, string Message)>();
        var (session, _) = Session();
        Assert.IsType<RunTestError>(await RunTestHandler.ExecuteAsync(
            [StartCase()], false, session, new Capabilities(LiveWritesEnabled: true),
            Identity(Emulator), (_, _) => Task.CompletedTask, progress: new CaseProgress(reports)));
        Assert.Empty(reports);
    }

    // ── Verdicts, durations and the test_run audit event ────

    [Fact]
    public async Task ExpectEqualsMinMax_Verdicts()
    {
        var (session, _) = Session();
        // Speed holds 5 and Motor_Run follows Cmd_Start.
        var passing = new RunTestCase("passing",
            Steps:
            [
                new RunTestStep(
                    Set:    [new RunTestSet("Cmd_Start", J("true"))],
                    WaitMs: 0,
                    Expect:
                    [
                        new RunTestExpect("Motor_Run", EqualsValue: J("true")),
                        new RunTestExpect("Speed", Min: 5, Max: 5),     // bounds are inclusive
                        new RunTestExpect("Speed", Min: 1),
                        new RunTestExpect("Speed", Max: 10),
                    ]),
            ]);
        var failing = new RunTestCase("failing",
            Steps:
            [
                new RunTestStep(WaitMs: 0, Expect:
                [
                    new RunTestExpect("Speed", EqualsValue: J("6")),    // wrong value
                    new RunTestExpect("Speed", Min: 6),                 // below min
                    new RunTestExpect("Speed", Max: 4),                 // above max
                    new RunTestExpect("Motor_Run", Min: 0),             // non-numeric: cannot satisfy a bound
                    new RunTestExpect("Speed"),                         // no expectation given
                ]),
                new RunTestStep(WaitMs: 0, Expect: [new RunTestExpect("Speed", EqualsValue: J("5"))]),
            ]);

        var res = Assert.IsType<RunTestResult>(await Run(session, [passing, failing]));

        Assert.Equal("fail", res.Overall);
        Assert.Equal(["pass", "fail"], res.Cases.Select(c => c.Result).ToArray());

        var p = res.Cases[0].Steps[0];
        Assert.Equal("pass", p.Verdict);
        Assert.All(p.Expect, e => Assert.Equal("pass", e.Verdict));
        Assert.Equal(true, p.Expect[0].Observed);
        Assert.Equal("equals true", p.Expect[0].Expected);
        Assert.Equal("min 5, max 5", p.Expect[1].Expected);
        var set = Assert.Single(p.Set);
        Assert.Equal("Cmd_Start", set.GetType().GetProperty("tag")!.GetValue(set));

        var f = res.Cases[1].Steps;
        Assert.Equal(["fail", "pass"], f.Select(s => s.Verdict).ToArray());   // a failed step does not end the case
        Assert.Equal(["fail", "fail", "fail", "fail", "fail"], f[0].Expect.Select(e => e.Verdict).ToArray());
        Assert.All(f[0].Expect, e => Assert.NotNull(e.Observed));
        Assert.Equal(5, f[0].Expect[0].Observed);

        // Durations per case and in total; the total spans the cases.
        Assert.All(res.Cases, c => Assert.True(c.DurationMs >= 0));
        Assert.True(res.DurationMs >= 0);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res, res.GetType()));
        var root = doc.RootElement;
        Assert.Equal("fail", root.GetProperty("overall").GetString());
        Assert.True(root.TryGetProperty("durationMs", out _));
        var case0 = root.GetProperty("cases")[0];
        Assert.Equal("pass", case0.GetProperty("result").GetString());
        Assert.True(case0.TryGetProperty("durationMs", out _));
        var step0 = case0.GetProperty("steps")[0];
        Assert.Equal("Cmd_Start", step0.GetProperty("set")[0].GetProperty("tag").GetString());
        var expect0 = step0.GetProperty("expect")[0];
        Assert.Equal("Motor_Run", expect0.GetProperty("tag").GetString());
        Assert.True(expect0.GetProperty("observed").GetBoolean());
        Assert.Equal("pass", expect0.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Run_WritesExactlyOneTestRunAuditEvent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-test-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, _) = Session();
            var bad = new RunTestCase("bad", Steps:
                [new RunTestStep(WaitMs: 0, Expect: [new RunTestExpect("Speed", EqualsValue: J("6"))])]);
            var res = Assert.IsType<RunTestResult>(await Run(session, [StartCase("good"), bad]));
            Assert.Equal("fail", res.Overall);

            var events = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.TryGetProperty("event", out var ev) && ev.GetString() == "test_run")
                .ToList();

            var evt = Assert.Single(events);
            Assert.Equal("fail", evt.GetProperty("overall").GetString());
            var cases = evt.GetProperty("cases");
            Assert.Equal(["good", "bad"], cases.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray());
            Assert.Equal(["pass", "fail"], cases.EnumerateArray().Select(c => c.GetProperty("verdict").GetString()).ToArray());
            Assert.True(evt.TryGetProperty("ts", out _));
            Assert.True(evt.TryGetProperty("duration_ms", out _));

            // A refused call runs nothing and so emits no test_run event.
            var refused = await Run(session, [StartCase()], confirmed: false);
            Assert.IsType<RunTestError>(refused);
            var after = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Count(l => l.Contains("\"event\":\"test_run\""));
            Assert.Equal(1, after);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Snapshot and restore ────────────────────────────────

    [Fact]
    public async Task RestoresEveryWrittenTag_EvenOnFailure()
    {
        var (session, svc) = Session();
        // Fails an expectation, so the case fails.
        var failing = new RunTestCase("failing",
            Setup: [new RunTestSet("Speed", J("9"))],
            Steps:
            [
                new RunTestStep(
                    Set:    [new RunTestSet("Cmd_Start", J("true"))],
                    WaitMs: 0,
                    Expect: [new RunTestExpect("Speed", EqualsValue: J("123"))]),
            ]);
        // Throws mid-run, after its writes.
        var throwing = new RunTestCase("throwing",
            Setup: [new RunTestSet("Speed", J("42"))],
            Steps: [new RunTestStep(Set: [new RunTestSet("Cmd_Start", J("true"))], WaitMs: 1)]);

        var res = Assert.IsType<RunTestResult>(await RunTestHandler.ExecuteAsync(
            [failing, throwing], true, session, new Capabilities(LiveWritesEnabled: true), Identity(Emulator),
            (ms, _) => ms == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask));

        Assert.Equal("fail", res.Overall);
        Assert.All(res.Cases, c => Assert.Equal("fail", c.Result));
        Assert.Contains("boom", res.Cases[1].Error);

        foreach (var c in res.Cases)
        {
            Assert.NotNull(c.Restored);
            Assert.Equal(["Speed", "Cmd_Start"], c.Restored!.Select(r => r.Tag).ToArray());
            Assert.All(c.Restored!, r => Assert.True(r.Confirmed));
        }
        Assert.Equal(5, res.Cases[0].Restored![0].Value);
        Assert.Equal(false, res.Cases[0].Restored![1].Value);

        // The controller holds the original values again.
        Assert.Equal(5, (await svc.ReadAsync("Speed")).Value);
        Assert.Equal(false, (await svc.ReadAsync("Cmd_Start")).Value);
        Assert.Equal(res.Cases.SelectMany(c => c.Restored!).Count(), res.Restored!.Count);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(res, res.GetType()));
        var first = doc.RootElement.GetProperty("cases")[0].GetProperty("restored")[0];
        Assert.Equal("Speed", first.GetProperty("tag").GetString());
        Assert.Equal(5, first.GetProperty("value").GetInt32());
        Assert.True(first.GetProperty("confirmed").GetBoolean());
    }

    [Fact]
    public async Task UnconfirmedRestore_IsRestoreFailed()
    {
        var (session, svc) = Session();
        // The restore write of Speed's original value (5) does not stick.
        svc.SwallowWrite = (t, v) => t == "Speed" && Equals(v, 5);
        var testCase = new RunTestCase("speed",
            Setup: [new RunTestSet("Speed", J("9"))],
            Steps: [new RunTestStep(Set: [new RunTestSet("Cmd_Start", J("true"))], WaitMs: 0,
                Expect: [new RunTestExpect("Speed", EqualsValue: J("9"))])]);

        var res = Assert.IsType<RunTestResult>(await Run(session, [testCase]));

        Assert.Equal("pass", res.Cases[0].Result);       // the case itself passed
        Assert.Equal("restore_failed", res.Overall);     // but the restore did not confirm
        Assert.Equal(["Speed"], res.RestoreFailedTags!.ToArray());
        var speed = res.Restored!.Single(r => r.Tag == "Speed");
        Assert.False(speed.Confirmed);
        Assert.Equal(5, speed.Value);
        Assert.True(res.Restored!.Single(r => r.Tag == "Cmd_Start").Confirmed);
    }

    [Fact]
    public async Task SnapshotReadFailure_FailsCase_WritesNothing()
    {
        var (session, svc) = Session();
        svc.FailReads.Add("Cmd_Start");
        var bad = new RunTestCase("bad", Steps:
            [new RunTestStep(Set: [new RunTestSet("Cmd_Start", J("1"))], WaitMs: 0)]);
        var res = Assert.IsType<RunTestResult>(await Run(session, [bad]));
        Assert.Equal("fail", res.Overall);
        Assert.Contains("snapshot", res.Cases[0].Error);
        Assert.Empty(svc.Writes);
    }
}
