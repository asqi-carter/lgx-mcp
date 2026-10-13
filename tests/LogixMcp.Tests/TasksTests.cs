using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.IO.Pipelines;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_connect</c> (SDK backend) and <c>logix_run_test</c> are task-capable through the
/// MCP Tasks extension, with progress at the upload / save L5X / parse / gate-derivation phases, and
/// clients that do not declare the extension get today's synchronous result.
/// Runs an in-process MCP server and client over in-memory pipes.
/// </summary>
[Collection("ProcessEnvironment")]
public class TasksTests : IDisposable
{

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();
    private const string CommPath = "EmulateEthernet\\192.0.2.10";

    private const string MinimalL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TasksController" TargetType="Controller" ContainsContext="false">
          <Controller Name="TasksController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
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

    /// <summary>
    /// Stands in for the SDK adapter: reports the upload, save-L5X and parse boundaries the way
    /// <c>SdkConnectFactory</c> does, then returns a canned session with a parsed type project so
    /// <c>ConnectHandler</c> performs (and reports) gate derivation.
    /// </summary>
    private sealed class PhasedUploadFactory : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
            => CreateFromControllerAsync(commPath, progress: null, ct);

        public async Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath, IProgress<ConnectProgress>? progress, CancellationToken ct)
        {
            progress?.Report(ConnectPhases.For(ConnectPhases.Upload));
            await Task.Delay(20, ct);
            progress?.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
            await Task.Delay(20, ct);
            progress?.Report(ConnectPhases.For(ConnectPhases.Parse));
            return new SdkSessionResult(
                Service:           new VirtualTagService(),
                DownloadPerformed: false,
                ModeTransitions:   [],
                CorrelationPath:   "upload-to-new-project",
                TypeProject:       L5X.Parse(MinimalL5Xml),
                AcdPath:           Path.Combine(Path.GetTempPath(), "tasks-test-uploaded.ACD"));
        }
    }

    /// <summary>An in-process server (tools: <see cref="LogixTagTools"/>) plus a connected client.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
        public McpClient Client { get; }
        public CancellationToken Token => _cts.Token;
        public ConcurrentQueue<ProgressNotificationParams> Progress { get; } = new();

        private IAsyncDisposable? _progressRegistration;

        private Rig(IHost host, McpClient client) { _host = host; Client = client; }

        public static async Task<Rig> StartAsync(bool clientDeclaresTasks)
        {
            var c2s = new Pipe();
            var s2c = new Pipe();

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<ProjectService>();
            builder.Services.AddSingleton<TagSessionService>();
            builder.Services.AddSingleton<ControllerModeService>();
            builder.Services.AddSingleton<ISdkTagServiceFactory>(new PhasedUploadFactory());
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream())
                .WithLogixTasks()
                .WithTools<LogixTagTools>();
            var host = builder.Build();
            await host.StartAsync();

            var options = new McpClientOptions();
            if (clientDeclaresTasks)
            {
                options.Capabilities = new ClientCapabilities
                {
                    Extensions = new Dictionary<string, object>
                    {
                        [TasksProtocol.ExtensionId] = new JsonObject(),
                    },
                };
            }

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()),
                options);
            var rig = new Rig(host, client);
            rig._progressRegistration = client.RegisterNotificationHandler(
                NotificationMethods.ProgressNotification,
                (n, _) =>
                {
                    var p = n.Params.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions);
                    if (p is not null) rig.Progress.Enqueue(p);
                    return ValueTask.CompletedTask;
                });
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            if (_progressRegistration is not null) await _progressRegistration.DisposeAsync();
            await Client.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
            _cts.Dispose();
        }
    }

    private static CallToolRequestParams ConnectCall(string progressToken) => new()
    {
        Name = "logix_connect",
        Arguments = new Dictionary<string, JsonElement>
        {
            ["backend"]     = JsonSerializer.SerializeToElement("sdk"),
            ["sdkMode"]     = JsonSerializer.SerializeToElement("online"),
            ["sdkCommPath"] = JsonSerializer.SerializeToElement(CommPath),
        },
        Meta = new JsonObject { ["progressToken"] = progressToken },
    };

#if LOGIX_SDK
    /// <summary>
    /// A client that declares the Tasks extension gets a task for an SDK-backend
    /// <c>logix_connect</c>, receives a progress notification at each of the upload, save L5X, parse and
    /// gate-derivation phases in order, and polls to the same result a synchronous call returns.
    /// </summary>
    [Fact]
    public async Task ConnectIsTaskCapable_ProgressAtPhases()
    {
        await using var rig = await Rig.StartAsync(clientDeclaresTasks: true);

        var created = await rig.Client.CallToolAsTaskAsync(ConnectCall("connect-1"), rig.Token);

        Assert.True(created.IsTask, "an SDK-backend logix_connect must run as a task for a task-capable client");
        var taskId = created.TaskCreated!.TaskId;

        GetTaskResult state;
        do
        {
            await Task.Delay(25, rig.Token);
            state = await rig.Client.GetTaskAsync(taskId, rig.Token);
        }
        while (state.Status == McpTaskStatus.Working);

        var completed = Assert.IsType<CompletedTaskResult>(state);

        // Progress notifications may trail the final status by a moment; wait for all four.
        for (var i = 0; i < 100 && rig.Progress.Count < ConnectPhases.Total; i++)
            await Task.Delay(20, rig.Token);

        // The server sends in order; the client SDK dispatches notification handlers concurrently, so
        // order by the monotonic progress value, as any MCP client must.
        var ordered = rig.Progress.Select(p => p.Progress).OrderBy(p => p.Progress).ToList();
        var phases  = ordered.Select(p => p.Message).ToList();
        Assert.Equal(ConnectPhases.Total, phases.Count);
        Assert.StartsWith(ConnectPhases.Upload + ":", phases[0]);
        Assert.StartsWith(ConnectPhases.SaveL5x + ":", phases[1]);
        Assert.StartsWith(ConnectPhases.Parse + ":", phases[2]);
        Assert.StartsWith(ConnectPhases.GateDerivation + ":", phases[3]);
        Assert.Equal([1f, 2f, 3f, 4f], ordered.Select(p => p.Progress).ToArray());
        Assert.All(ordered, p => Assert.Equal(4f, p.Total));

        var toolResult = completed.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!;
        Assert.NotEqual(true, toolResult.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(toolResult.Content)).Text;
        using var json = JsonDocument.Parse(text);
        Assert.Equal("connected", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("sdk", json.RootElement.GetProperty("backend").GetString());
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// The connect sync budget does not apply to a client that declares the Tasks extension: even with a
    /// zero budget it still gets a task (not a 'connecting' payload) that completes with the full result.
    /// </summary>
    [Fact]
    public async Task TaskClient_KeepsTaskPath_EvenWithZeroSyncBudget()
    {
        var saved = Environment.GetEnvironmentVariable(ConnectState.BudgetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectState.BudgetEnvVar, "0");
            await using var rig = await Rig.StartAsync(clientDeclaresTasks: true);

            var created = await rig.Client.CallToolAsTaskAsync(ConnectCall("connect-4"), rig.Token);
            Assert.True(created.IsTask);

            GetTaskResult state;
            do
            {
                await Task.Delay(25, rig.Token);
                state = await rig.Client.GetTaskAsync(created.TaskCreated!.TaskId, rig.Token);
            }
            while (state.Status == McpTaskStatus.Working);

            var completed  = Assert.IsType<CompletedTaskResult>(state);
            var toolResult = completed.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!;
            var text = Assert.IsType<TextContentBlock>(Assert.Single(toolResult.Content)).Text;
            using var json = JsonDocument.Parse(text);
            Assert.Equal("connected", json.RootElement.GetProperty("status").GetString());
        }
        finally { Environment.SetEnvironmentVariable(ConnectState.BudgetEnvVar, saved); }
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// A client that does not declare the Tasks extension gets today's synchronous result for the same
    /// call: a plain tool result (no task), with the same result shape.
    /// </summary>
    [Fact]
    public async Task NonTaskClient_SynchronousResult()
    {
        await using var rig = await Rig.StartAsync(clientDeclaresTasks: false);

        var result = await rig.Client.CallToolAsync(ConnectCall("connect-2"), rig.Token);

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var json = JsonDocument.Parse(text);
        Assert.Equal("connected", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("sdk", json.RootElement.GetProperty("backend").GetString());
        Assert.Equal("online-controller", json.RootElement.GetProperty("target").GetString());
    }
#endif

    private static IDictionary<string, JsonElement> Args(string backend) =>
        new Dictionary<string, JsonElement> { ["backend"] = JsonSerializer.SerializeToElement(backend) };

    /// <summary>Only the SDK backend of <c>logix_connect</c>, and <c>logix_run_test</c>, are task-capable.</summary>
    [Fact]
    public void SelectExecutionMode_OnlyLongCallsAreTaskCapable()
    {
        Assert.Equal(McpTaskExecutionMode.Optional, TasksSupport.SelectExecutionMode("logix_connect", Args("sdk")));
        Assert.Equal(McpTaskExecutionMode.Optional, TasksSupport.SelectExecutionMode("logix_connect", Args("SDK")));
        Assert.Equal(McpTaskExecutionMode.Optional, TasksSupport.SelectExecutionMode("logix_run_test", null));

        Assert.Equal(McpTaskExecutionMode.Synchronous, TasksSupport.SelectExecutionMode("logix_connect", Args("cip")));
        Assert.Equal(McpTaskExecutionMode.Synchronous, TasksSupport.SelectExecutionMode("logix_connect", Args("l5x")));
        Assert.Equal(McpTaskExecutionMode.Synchronous, TasksSupport.SelectExecutionMode("logix_connect", null));
        Assert.Equal(McpTaskExecutionMode.Synchronous, TasksSupport.SelectExecutionMode("logix_read", Args("sdk")));
        Assert.Equal(McpTaskExecutionMode.Synchronous, TasksSupport.SelectExecutionMode(null, null));
    }

    /// <summary>A task-capable client still gets a synchronous result for a non-SDK connect.</summary>
    [Fact]
    public async Task TaskClient_NonSdkConnect_StaysSynchronous()
    {
        await using var rig = await Rig.StartAsync(clientDeclaresTasks: true);
        var call = ConnectCall("connect-3");
        call.Arguments = new Dictionary<string, JsonElement>
        {
            ["backend"] = JsonSerializer.SerializeToElement("l5x"),
        };

        var created = await rig.Client.CallToolAsTaskAsync(call, rig.Token);

        Assert.False(created.IsTask);
        Assert.NotNull(created.Result);
    }

    /// <summary>The connect-phase constants are ordered 1..N and map to distinct progress reports.</summary>
    [Fact]
    public void ConnectPhases_AreOrderedAndComplete()
    {
        var reports = ConnectPhases.All.Select(ConnectPhases.For).ToList();
        Assert.Equal(ConnectPhases.Total, reports.Count);
        Assert.Equal(Enumerable.Range(1, ConnectPhases.Total), reports.Select(r => r.Step));
        Assert.All(reports, r => Assert.Equal(ConnectPhases.Total, r.TotalSteps));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectPhases.For("nope"));
    }

#if LOGIX_SDK
    /// <summary>
    /// <c>ConnectHandler</c> reports the gate-derivation phase, and with no progress receiver the same
    /// call returns the same result (no behaviour change).
    /// </summary>
    [Fact]
    public async Task ConnectHandler_ReportsGateDerivation_AndIsUnchangedWithoutProgress()
    {
        var reports = new List<ConnectProgress>();
        var sink    = new ListProgress(reports);

        async Task<object> Run(IProgress<ConnectProgress>? progress) =>
            await ConnectHandler.ExecuteAsync(
                "", "", false, new ProjectService(), new TagSessionService(), "sdk",
                sdkFactory: new PhasedUploadFactory(), sdkMode: "online", sdkCommPath: CommPath,
                progress: progress);

        var reported = Assert.IsType<ConnectResult>(await Run(sink));
        var silent   = Assert.IsType<ConnectResult>(await Run(null));

        Assert.Equal(
            [ConnectPhases.Upload, ConnectPhases.SaveL5x, ConnectPhases.Parse, ConnectPhases.GateDerivation],
            reports.Select(r => r.Phase));
        // phaseTimings are measured durations and differ run to run; everything else must match.
        Assert.Equal(silent with { PhaseTimings = null }, reported with { PhaseTimings = null });
    }
#endif

    private sealed class ListProgress(List<ConnectProgress> list) : IProgress<ConnectProgress>
    {
        public void Report(ConnectProgress value) => list.Add(value);
    }

#if LOGIX_SDK
    /// <summary>
    /// The real <c>SdkConnectFactory</c> reports the upload, save-L5X and parse boundaries in order,
    /// each before the work it names.
    /// </summary>
    [Fact]
    public async Task SdkConnectFactory_ReportsUploadSaveParse_BeforeEachStep()
    {
        var events = new List<string>();
        var phases = new List<ConnectProgress>();
        var factory = new SdkConnectFactory(
            sessionFactory: () => new LogixProjectSession(
                (_, _) => Task.FromResult<RockwellAutomation.LogixDesigner.ILogixProject>(new FakeLogixProject())),
            controllerUploadFactory: (_, _, _) =>
            {
                events.Add("work:upload");
                return Task.FromResult<RockwellAutomation.LogixDesigner.ILogixProject>(new FakeLogixProject());
            },
            saveAsFactory: (_, _, _) => { events.Add("work:save"); return Task.CompletedTask; },
            l5xLoader:     _ => { events.Add("work:parse"); return L5X.Parse(MinimalL5Xml); },
            lease: _leaseScope.Lease);

        var progress = new CallbackProgress(p => { phases.Add(p); events.Add("report:" + p.Phase); });
        var result = await factory.CreateFromControllerAsync(CommPath, progress, CancellationToken.None);

        Assert.NotNull(result.TypeProject);
        Assert.Equal(
            ["report:upload", "work:upload", "report:save-l5x", "work:save", "report:parse", "work:parse"],
            events);
        Assert.Equal([1, 2, 3], phases.Select(p => p.Step));
    }

    private sealed class CallbackProgress(Action<ConnectProgress> cb) : IProgress<ConnectProgress>
    {
        public void Report(ConnectProgress value) => cb(value);
    }
#endif
}
