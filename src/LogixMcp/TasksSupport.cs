using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// MCP Tasks extension wiring. The long calls <c>logix_connect</c> (SDK backend) and
/// <c>logix_run_test</c> run as pollable tasks for clients that declare the
/// <c>io.modelcontextprotocol/tasks</c> extension; every other call, and every client without
/// task support, keeps today's synchronous behaviour with an unchanged result shape.
/// </summary>
public static class TasksSupport
{
    /// <summary>Name of the connect tool.</summary>
    public const string ConnectToolName = "logix_connect";

    /// <summary>Name of the scripted-test tool.</summary>
    public const string RunTestToolName = "logix_run_test";

    /// <summary>
    /// Decides whether a tool call may run as a task: <c>logix_run_test</c> always may, and
    /// <c>logix_connect</c> may when <c>backend</c> is <c>"sdk"</c> (the upload plus detailed L5X save
    /// takes 20-60 s). Every other call is <see cref="McpTaskExecutionMode.Synchronous"/>.
    /// </summary>
    /// <param name="toolName">Name of the tool being called.</param>
    /// <param name="arguments">Raw call arguments, or <c>null</c> when none were supplied.</param>
    /// <returns><see cref="McpTaskExecutionMode.Optional"/> when the call is task-capable.</returns>
    public static McpTaskExecutionMode SelectExecutionMode(
        string? toolName, IDictionary<string, JsonElement>? arguments)
    {
        if (string.Equals(toolName, RunTestToolName, StringComparison.Ordinal))
            return McpTaskExecutionMode.Optional;

        if (string.Equals(toolName, ConnectToolName, StringComparison.Ordinal)
            && arguments is not null
            && arguments.TryGetValue("backend", out var backend)
            && backend.ValueKind == JsonValueKind.String
            && string.Equals(backend.GetString(), "sdk", StringComparison.OrdinalIgnoreCase))
            return McpTaskExecutionMode.Optional;

        return McpTaskExecutionMode.Synchronous;
    }

    /// <summary>
    /// Whether the calling client declared the <c>io.modelcontextprotocol/tasks</c> extension. Such clients
    /// keep the task path for long calls, so the connect sync budget does not apply to them.
    /// </summary>
    /// <param name="context">The tool call's request context.</param>
    /// <returns><c>true</c> when the client's capabilities carry the Tasks extension.</returns>
    public static bool ClientDeclaresTasks(RequestContext<CallToolRequestParams>? context) =>
        context?.Server?.ClientCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) == true;

    /// <summary>
    /// Creates a progress sink for the current tool call that sends MCP progress notifications in the
    /// order they are reported (the SDK's built-in sink sends each one fire-and-forget, so two quick
    /// phases can overtake each other on the wire).
    /// </summary>
    /// <param name="context">The tool call's request context.</param>
    /// <returns>The sink, or <c>null</c> when the caller supplied no progress token.</returns>
    public static IProgress<ProgressNotificationValue>? CreateProgress(
        RequestContext<CallToolRequestParams>? context) =>
        context?.Params?.ProgressToken is { } token && context.Server is { } server
            ? new OrderedProgress(server, token)
            : null;

    /// <summary>
    /// Sends each report as a progress notification, one after another. Delivery is best effort: a
    /// failed or cancelled send is dropped, since progress must never fail the tool call.
    /// </summary>
    private sealed class OrderedProgress(McpServer server, ProgressToken token)
        : IProgress<ProgressNotificationValue>
    {
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;

        public void Report(ProgressNotificationValue value)
        {
            lock (_gate)
            {
                _tail = _tail.ContinueWith(
                    async _ =>
                    {
                        try { await server.NotifyProgressAsync(token, value).ConfigureAwait(false); }
                        catch (Exception) { /* best effort */ }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
        }
    }

    /// <summary>
    /// Adapts an MCP progress sink to connect-phase reports: each <see cref="ConnectProgress"/> becomes
    /// a <see cref="ProgressNotificationValue"/> with <c>Progress</c> = step, <c>Total</c> = number of
    /// phases and <c>Message</c> = "phase: description".
    /// </summary>
    /// <param name="sink">The progress sink bound by the MCP SDK; <c>null</c> when absent.</param>
    /// <returns>The adapter, or <c>null</c> when <paramref name="sink"/> is <c>null</c>.</returns>
    public static IProgress<ConnectProgress>? ToConnectProgress(IProgress<ProgressNotificationValue>? sink) =>
        sink is null
            ? null
            : new DelegateProgress<ConnectProgress>(p => sink.Report(new ProgressNotificationValue
            {
                Progress = p.Step,
                Total    = p.TotalSteps,
                Message  = $"{p.Phase}: {p.Message}",
            }));

    /// <summary>
    /// Like <see cref="ToConnectProgress"/>, but also repeats the current phase every few seconds while it
    /// runs (see <see cref="ConnectHeartbeat"/>). The caller disposes the result when the call returns.
    /// </summary>
    /// <param name="sink">The progress sink bound by the MCP SDK; <c>null</c> when the request had no token.</param>
    /// <param name="interval">Time between heartbeats; <see cref="ConnectHeartbeat.DefaultInterval"/> when <c>null</c>.</param>
    /// <param name="timeProvider">Clock and timer source; the system clock when <c>null</c>.</param>
    /// <returns>The heartbeat, or <c>null</c> (nothing is ever sent) when <paramref name="sink"/> is <c>null</c>.</returns>
    public static ConnectHeartbeat? CreateConnectProgress(
        IProgress<ProgressNotificationValue>? sink, TimeSpan? interval = null, TimeProvider? timeProvider = null) =>
        sink is null ? null : new ConnectHeartbeat(sink, interval, timeProvider);

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> (unlike <see cref="Progress{T}"/>, which posts to the
    /// captured context), so reports reach the client in phase order.
    /// </summary>
    private sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>
    /// Enables the Tasks extension on the MCP server with an in-memory task store and the
    /// <see cref="SelectExecutionMode"/> policy.
    /// </summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IMcpServerBuilder WithLogixTasks(this IMcpServerBuilder builder) =>
        builder.WithTasks(
            new InMemoryMcpTaskStore(),
            options => options.ExecutionModeSelector =
                context => SelectExecutionMode(
                    (context.MatchedPrimitive as McpServerTool)?.ProtocolTool.Name,
                    context.Params?.Arguments));
}
