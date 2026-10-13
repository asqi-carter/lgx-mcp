using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// One shared filter around every MCP tool entry point. An unexpected exception becomes a typed,
/// structured error (<c>kind=InternalError</c>, or <c>InvalidArguments</c> for a missing required
/// argument) instead of the framework's generic "An error occurred invoking" text.
/// Any result whose JSON carries a top-level <c>error</c> is flagged <c>IsError = true</c> here, centrally.
/// Cancellation is never converted: <see cref="OperationCanceledException"/> propagates.
/// </summary>
public static class ToolErrorFilter
{
    private const string MissingMarker = "a value for the required parameter '";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Registers the filter on the MCP server builder.</summary>
    /// <param name="builder">The server builder.</param>
    /// <returns>The same builder.</returns>
    public static IMcpServerBuilder WithToolErrorFilter(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(f => f.AddCallToolFilter(next => async (context, ct) =>
        {
            var toolName = context.Params?.Name;
            var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("LogixMcp.ToolErrorFilter");
            // Unknown argument names are rejected before the tool does any work.
            if (context.MatchedPrimitive is McpServerTool tool)
            {
                var rejected = ToolArgumentFilter.Check(
                    toolName, context.Params?.Arguments?.Keys,
                    ToolArgumentFilter.ValidNames(tool.ProtocolTool.InputSchema));
                if (rejected is not null) return rejected;
            }

            var result = await InvokeAsync(
                toolName, () => next(context, ct), logger, context.Services?.GetService<ConnectState>(),
                context.Services?.GetService<TagSessionService>());
            return result;
        }));

    /// <summary>
    /// Runs <paramref name="invoke"/> and converts an unexpected exception (or the framework's generic
    /// invocation-failure result) into a typed error result.
    /// </summary>
    /// <param name="toolName">Tool being called, for the error payload.</param>
    /// <param name="invoke">The wrapped tool invocation.</param>
    /// <param name="logger">Optional logger; the full exception is logged to the server log.</param>
    /// <param name="connectState">
    /// Optional connect state; while a connect is in flight, a <c>NoActiveSession</c> error result is replaced
    /// by <c>ConnectInProgress</c> (with phase and elapsedMs).
    /// </param>
    /// <param name="tagSession">
    /// Optional tag session; a "Project is not open." failure on a live session marks it lost and is returned as
    /// a typed <c>SessionLost</c> (see <see cref="SessionLostSupport"/>).
    /// </param>
    /// <returns>The tool result, or a typed error result.</returns>
    public static async ValueTask<CallToolResult> InvokeAsync(
        string? toolName, Func<ValueTask<CallToolResult>> invoke, ILogger? logger = null,
        ConnectState? connectState = null, TagSessionService? tagSession = null)
    {
        CallToolResult result;
        try
        {
            result = await InvokeWatchedAsync(toolName, invoke, tagSession, connectState, logger);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (connectState?.RewriteProjectNotOpen(toolName, ex) is { } inProgress)
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = inProgress }] };
            if (tagSession is not null && SessionLostSupport.TryFromException(tagSession, toolName, ex) is { } lost)
            {
                logger?.LogWarning(ex, "Tool {Tool} found the session's project closed; session marked lost", toolName);
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = lost }] };
            }
            logger?.LogError(ex, "Tool {Tool} failed with an unhandled exception", toolName);
            return ToErrorResult(toolName, ex);
        }

        if (connectState is not null && result.Content is not null)
            for (var i = 0; i < result.Content.Count; i++)
                if (result.Content[i] is TextContentBlock { Text: { } text }
                    && connectState.RewriteNoActiveSession(text, toolName) is { } replacement)
                    result.Content[i] = new TextContentBlock { Text = replacement };

        if (tagSession is not null && result.Content is not null)
            for (var i = 0; i < result.Content.Count; i++)
                if (result.Content[i] is TextContentBlock { Text: { } text }
                    && SessionLostSupport.TryRewrite(tagSession, toolName, text) is { } lostText)
                    result.Content[i] = new TextContentBlock { Text = lostText };

        return MarkTopLevelError(result);
    }

    /// <summary>
    /// Runs <paramref name="invoke"/> while watching the SDK service (G37). The pid recorded at connect is checked
    /// before the call and then every <see cref="TagSessionService.RestartPollInterval"/> while it runs, so a call
    /// stuck waiting on a dead service (publish, stage, export, pending edits alike) fails with a typed SessionLost
    /// <c>sdk-service-restarted</c> within the bound, instead of waiting out the SDK's own timeout.
    /// </summary>
    private static async ValueTask<CallToolResult> InvokeWatchedAsync(
        string? toolName, Func<ValueTask<CallToolResult>> invoke, TagSessionService? tagSession,
        ConnectState? connectState, ILogger? logger)
    {
        // This process's own connect or reconnect restarts the SDK server on purpose; that is ConnectInProgress, not a loss.
        if (tagSession is null || SessionLostSupport.IsExempt(toolName) || !tagSession.WatchesSdkService
            || connectState?.Current is not null)
            return await invoke();

        if (SessionLostSupport.CheckServiceRestart(tagSession, toolName) is { } before)
            return LostResult(before);

        var call = invoke().AsTask();
        var interval = tagSession.RestartPollInterval > TimeSpan.Zero ? tagSession.RestartPollInterval : SessionLostSupport.DefaultRestartPollInterval;
        while (true)
        {
            if (await Task.WhenAny(call, Task.Delay(interval)) == call)
                return await call;
            if (connectState?.Current is null
                && SessionLostSupport.CheckServiceRestart(tagSession, toolName) is { } during)
            {
                logger?.LogWarning("Tool {Tool} abandoned: the LdSdkServer pid changed since connect (the SDK server restarted)", toolName);
                _ = call.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return LostResult(during);
            }
        }
    }

    private static CallToolResult LostResult(string payload) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = payload }] };

    /// <summary>
    /// Sets <c>IsError = true</c> on a result whose JSON text carries a top-level, non-empty
    /// <c>error</c> property. This is the single place that decides the protocol-level error flag, so
    /// handlers never set it themselves. Results without a top-level <c>error</c> (for example
    /// <c>logix_build</c> with <c>status: "failed"</c> and a build error list) are left untouched, as
    /// are results already flagged and non-JSON or non-object text. Only <c>error</c> at the top level
    /// counts; nested <c>error</c> fields (per-case/step results, lint counts) are ignored.
    /// </summary>
    /// <param name="result">The tool result.</param>
    /// <returns>The same result instance, with <c>IsError</c> set when it carries a top-level error.</returns>
    public static CallToolResult MarkTopLevelError(CallToolResult result)
    {
        if (result.IsError == true || result.Content is null) return result;
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock { Text: { } text } && HasTopLevelError(text))
            {
                result.IsError = true;
                break;
            }
        }

        return result;
    }

    private static bool HasTopLevelError(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "error", StringComparison.OrdinalIgnoreCase)) continue;
                return prop.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
                    JsonValueKind.String => !string.IsNullOrEmpty(prop.Value.GetString()),
                    _ => true,
                };
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Builds the typed error result for an exception.</summary>
    /// <param name="toolName">Tool being called.</param>
    /// <param name="ex">The exception.</param>
    /// <returns>An <c>IsError</c> result whose text is a JSON error object.</returns>
    public static CallToolResult ToErrorResult(string? toolName, Exception ex)
    {
        var firstLine = FirstLine(ex.Message);
        object payload;
        if (ex is ArgumentException && TryMissingParameter(ex.Message, out var parameter))
        {
            payload = new
            {
                error = $"Missing required argument '{parameter}' for {toolName ?? "tool"}.",
                kind = ErrorKinds.InvalidArguments,
                tool = toolName,
                parameter,
                hint = $"Pass a value for '{parameter}' and call {toolName ?? "the tool"} again.",
            };
        }
        else
        {
            payload = new
            {
                error = $"{toolName ?? "tool"} failed unexpectedly: {ex.GetType().Name}: {firstLine}",
                kind = ErrorKinds.InternalError,
                tool = toolName,
                exceptionType = ex.GetType().Name,
                message = firstLine,
                hint = "Unexpected server error; check the server log (stderr) for the stack trace.",
            };
        }

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }],
        };
    }

    private static bool TryMissingParameter(string message, out string parameter)
    {
        parameter = "";
        var i = message.IndexOf(MissingMarker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var start = i + MissingMarker.Length;
        var end = message.IndexOf('\'', start);
        if (end <= start) return false;
        parameter = message[start..end];
        return true;
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var nl = message.IndexOfAny(['\r', '\n']);
        return nl < 0 ? message : message[..nl];
    }
}
