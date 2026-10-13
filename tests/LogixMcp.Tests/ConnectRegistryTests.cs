using System.Text.Json.Nodes;

namespace LogixMcp.Tests;

/// <summary>
/// The cross-instance connect registry and the <c>ConnectOwnedByOtherInstance</c> status. Every test uses its own
/// temp directory and injected pids; the real per-user registry directory is never touched.
/// </summary>
public sealed class ConnectRegistryTests : IDisposable
{
    private const int PidA = 41001;
    private const int PidB = 41002;
    private static readonly DateTimeOffset StartA = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartB = new(2026, 10, 12, 8, 5, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lgx-registry-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ConnectRegistry Registry(int pid, DateTimeOffset start, Func<int, bool>? alive = null,
        Func<DateTimeOffset>? clock = null) =>
        new(_dir, pid, start, alive ?? (_ => true), clock: clock);

    private static ConnectRegistryEntry Entry(string id, int pid = PidA, string phase = "upload",
        string status = ConnectRegistry.StatusConnecting, DateTimeOffset? completedAt = null) =>
        new(id, pid, StartA, "1,0", phase, 1234, status, completedAt);

    private static JsonObject Status(ConnectState s, string? id = null) =>
        JsonNode.Parse(s.GetStatusJson(id))!.AsObject();

    /// <summary>An entry written to the registry reads back with every field intact.</summary>
    [Fact]
    public void RegistryRoundTrip()
    {
        var reg = Registry(PidA, StartA);
        var entry = Entry("abc123", completedAt: StartA.AddMinutes(1)) with { Status = ConnectRegistry.StatusConnected };

        Assert.True(reg.Write(entry));

        Assert.Equal(entry, reg.Read("abc123"));
        Assert.Null(reg.Read("missing"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    /// <summary>A reader running while the writer rewrites an entry never sees a torn or missing file.</summary>
    [Fact]
    public async Task AtomicWriteSurvivesConcurrentReader()
    {
        var reg = Registry(PidA, StartA);
        reg.Write(Entry("race", phase: "p0"));
        var stop = false;
        var bad = 0;
        var reads = 0;
        var reader = Task.Run(() =>
        {
            var other = Registry(PidB, StartB);
            while (!Volatile.Read(ref stop))
            {
                var e = other.Read("race");
                Interlocked.Increment(ref reads);
                if (e is null || e.ConnectId != "race") Interlocked.Increment(ref bad);
            }
        });

        // Rewrite until the reader has demonstrably overlapped with the writer (a loaded machine may start it late).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        for (var i = 0; i < 300 || (Volatile.Read(ref reads) < 200 && DateTime.UtcNow < deadline); i++)
            Assert.True(reg.Write(Entry("race", phase: "p" + i) with { ElapsedMs = i }));
        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reads > 0);
        Assert.Equal(0, bad);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    /// <summary>A write deletes entries older than an hour and entries whose owner pid is gone, and keeps the rest.</summary>
    [Fact]
    public void StaleAndDeadPidEntriesArePruned()
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var live = new HashSet<int> { PidA, PidB };
        var reg = Registry(PidA, StartA, alive: p => live.Contains(p), clock: () => now);
        var other = Registry(PidB, StartB, alive: p => live.Contains(p), clock: () => now);

        other.Write(Entry("stale", PidB));
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "stale.json"), now.UtcDateTime.AddMinutes(-61));
        other.Write(Entry("dead", 41999));
        other.Write(Entry("fresh", PidB));

        reg.Write(Entry("mine", PidA));

        Assert.Null(reg.Read("stale"));
        Assert.Null(reg.Read("dead"));
        Assert.NotNull(reg.Read("fresh"));
        Assert.NotNull(reg.Read("mine"));
    }

    private static async Task<(string Id, TaskCompletionSource Release)> StartBlocked(
        ConnectState state, string payload, string phase = ConnectPhases.Upload)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = await state.RunAsync(async progress =>
        {
            progress.Report(ConnectPhases.For(phase));
            await release.Task;
            return payload;
        }, TimeSpan.Zero, commPath: "1,0");
        // With a zero budget the call returns before the job reports its phase: wait until it has.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (state.Current?.Phase != phase && DateTime.UtcNow < deadline) await Task.Delay(5);
        return (JsonNode.Parse(response)!["connectId"]!.GetValue<string>(), release);
    }

    /// <summary>A second instance sees the first instance's running connect as ConnectOwnedByOtherInstance.</summary>
    [Fact]
    public async Task SecondInstanceSeesInProgressConnectAsOwnedByOtherInstance()
    {
        var first = new ConnectState(Registry(PidA, StartA));
        var second = new ConnectState(Registry(PidB, StartB));
        var (id, release) = await StartBlocked(first, """{"status":"connected"}""");

        var byLatest = Status(second);
        var byId = Status(second, id);

        foreach (var s in new[] { byLatest, byId })
        {
            Assert.Equal(ErrorKinds.ConnectOwnedByOtherInstance, s["kind"]!.GetValue<string>());
            Assert.Equal(PidA, s["ownerPid"]!.GetValue<int>());
            Assert.Equal(ConnectPhases.Upload, s["phase"]!.GetValue<string>());
            Assert.Equal("connecting", s["status"]!.GetValue<string>());
            Assert.Equal(id, s["connectId"]!.GetValue<string>());
            Assert.True(s["elapsedMs"]!.GetValue<long>() >= 0);
            Assert.Contains("logix_connect", s["hint"]!.GetValue<string>());
            Assert.Equal(PidB, s["serverInstance"]!["pid"]!.GetValue<int>());
        }

        // The owner itself still reports its own connect.
        Assert.Equal("connecting", Status(first)["status"]!.GetValue<string>());
        Assert.Equal(PidA, Status(first)["serverInstance"]!["pid"]!.GetValue<int>());

        release.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Once the owner finished, the foreign status says so and carries the outcome status.</summary>
    [Fact]
    public async Task CompletedForeignConnectReportsOutcomeStatus()
    {
        var first = new ConnectState(Registry(PidA, StartA));
        var second = new ConnectState(Registry(PidB, StartB));
        var (id, release) = await StartBlocked(first, """{"status":"connected","gateway":"192.0.2.10"}""");
        release.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var s = Status(second, id);

        Assert.Equal(ErrorKinds.ConnectOwnedByOtherInstance, s["kind"]!.GetValue<string>());
        Assert.Equal("connected", s["status"]!.GetValue<string>());
        Assert.True(s["finished"]!.GetValue<bool>());
        Assert.Equal(PidA, s["ownerPid"]!.GetValue<int>());
        Assert.NotNull(s["completedAt"]);
        // A finished connect is not an "in-progress" one: without an id the second instance has nothing to report.
        Assert.Equal(ErrorKinds.NoConnectInProgress, Status(second)["kind"]!.GetValue<string>());
    }

    /// <summary>A failed foreign connect reports status failed.</summary>
    [Fact]
    public async Task FailedForeignConnectReportsFailedStatus()
    {
        var first = new ConnectState(Registry(PidA, StartA));
        var second = new ConnectState(Registry(PidB, StartB));
        var (id, release) = await StartBlocked(first, """{"error":"boom","kind":"ConnectFailed"}""");
        release.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("failed", Status(second, id)["status"]!.GetValue<string>());
    }

    /// <summary>With nothing in the registry (or only a dead owner's entry) the answer stays NoConnectInProgress.</summary>
    [Fact]
    public void NoRegistryEntryGivesNoConnectInProgress()
    {
        var dead = false;
        var second = new ConnectState(Registry(PidB, StartB, alive: p => p == PidB || !dead));

        Assert.Equal(ErrorKinds.NoConnectInProgress, Status(second)["kind"]!.GetValue<string>());
        Assert.Equal(ErrorKinds.NoConnectInProgress, Status(second, "unknown-id")["kind"]!.GetValue<string>());
        Assert.Equal(PidB, Status(second)["serverInstance"]!["pid"]!.GetValue<int>());

        Registry(PidA, StartA).Write(Entry("orphan"));
        dead = true;
        Assert.Equal(ErrorKinds.NoConnectInProgress, Status(second)["kind"]!.GetValue<string>());
        Assert.Equal(ErrorKinds.NoConnectInProgress, Status(second, "orphan")["kind"]!.GetValue<string>());
    }

    /// <summary>A registry that cannot be written (its directory path is a file) never fails the connect.</summary>
    [Fact]
    public async Task RegistryIoFailureDoesNotFailConnect()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "lgx-registry-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var state = new ConnectState(new ConnectRegistry(Path.Combine(blocker, "connects"), PidA, StartA, _ => true));

            var result = await state.RunAsync(
                _ => Task.FromResult("""{"status":"connected"}"""), TimeSpan.FromSeconds(10), commPath: "1,0");
            Assert.Equal("connected", JsonNode.Parse(result)!["status"]!.GetValue<string>());

            var (_, release) = await StartBlocked(state, """{"status":"connected"}""");
            release.SetResult();
            await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("connected", Status(state)["status"]!.GetValue<string>());

            var reg = new ConnectRegistry(Path.Combine(blocker, "connects"), PidA, StartA, _ => true);
            Assert.False(reg.Write(Entry("x")));
            Assert.Null(reg.Read("x"));
            Assert.Null(reg.FindMostRecentInProgress());
        }
        finally { File.Delete(blocker); }
    }
}
