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

            var connectState = context.Services?.GetService<ConnectState>();
            // Own connect visible before the tool runs: mark synchronously, before next, so an id-less
            // status poll sent right behind logix_connect never sees "no connect". The scope is released when the
            // tool completes, success or exception; RunAsync adopts the mark when the connect runs as a job.
            var rawArgs = context.Params?.Arguments is { } a
                ? new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(a)
                : null;
            using var ownConnect = string.Equals(toolName, TasksSupport.ConnectToolName, StringComparison.Ordinal)
                ? connectState?.BeginOwnConnect(
                    ConnectState.CommPathFromArguments(rawArgs), ConnectState.ArgsKeyFromArguments(rawArgs))
                : null;

            var result = await InvokeAsync(
                toolName, () => next(context, ct), logger, connectState,
                context.Services?.GetService<TagSessionService>(), rawArgs);
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
        ConnectState? connectState = null, TagSessionService? tagSession = null,
        IReadOnlyDictionary<string, JsonElement>? arguments = null)
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
            if (IsArgumentBindingFailure(ex))
            {
                // A value the SDK could not bind to the parameter's type (for example a negative insertPosition
                // for a uint) is the caller's mistake, not a server fault.
                logger?.LogWarning(ex, "Tool {Tool} received an argument it could not bind", toolName);
                return ToErrorResult(toolName, ex, arguments);
            }
            logger?.LogError(ex, "Tool {Tool} failed with an unhandled exception", toolName);
            return ToErrorResult(toolName, ex, arguments);
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

    private const string ConversionMarker = "The JSON value could not be converted to ";

    /// <summary>
    /// <c>true</c> for the exception the MCP SDK's argument binding throws when a JSON value does not fit the
    /// parameter's CLR type (a <see cref="JsonException"/>, or any exception carrying its message).
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>Whether it is an argument-binding failure.</returns>
    internal static bool IsArgumentBindingFailure(Exception ex) =>
        ex is JsonException || (ex.Message?.StartsWith(ConversionMarker, StringComparison.Ordinal) ?? false)
        || (ex.InnerException is JsonException);

    /// <summary>The CLR type name the conversion message names (<c>UInt32</c>, <c>Boolean</c>, ...), or <c>null</c>.</summary>
    internal static string? ConversionTargetType(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var i = message.IndexOf(ConversionMarker, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + ConversionMarker.Length;
        var end = start;
        while (end < message.Length && (char.IsLetterOrDigit(message[end]) || message[end] is '.' or '_')) end++;
        var full = message[start..end].TrimEnd('.');
        var dot = full.LastIndexOf('.');
        return dot >= 0 ? full[(dot + 1)..] : full;
    }

    private static readonly HashSet<string> UnsignedTypes = new(StringComparer.Ordinal) { "Byte", "UInt16", "UInt32", "UInt64" };
    private static readonly HashSet<string> SignedTypes   = new(StringComparer.Ordinal) { "SByte", "Int16", "Int32", "Int64" };

    /// <summary>
    /// The arguments whose JSON value cannot be <paramref name="targetType"/>: for an unsigned integer a negative or
    /// fractional number, for a signed integer a fractional number. Only numbers are considered, because the filter does
    /// not know which parameters are numeric; an empty list when the target is not an integer type or nothing fits.
    /// </summary>
    internal static List<string> ArgumentsThatCannotBe(string? targetType, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var names = new List<string>();
        if (targetType is null || arguments is null) return names;
        bool unsigned = UnsignedTypes.Contains(targetType), signed = SignedTypes.Contains(targetType);
        if (!unsigned && !signed) return names;
        foreach (var (name, value) in arguments)
        {
            if (value.ValueKind != JsonValueKind.Number) continue;
            bool whole = value.TryGetInt64(out var l);
            bool negative = whole ? l < 0 : value.TryGetDouble(out var d) && d < 0;
            if (!whole || (unsigned && negative)) names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string ExpectedShape(string? targetType) => targetType switch
    {
        "Byte" or "UInt16" or "UInt32" or "UInt64" => "a whole number from 0 upward",
        "SByte" or "Int16" or "Int32" or "Int64"   => "a whole number",
        "Boolean"                                   => "true or false",
        "Double" or "Single" or "Decimal"           => "a number",
        "String"                                    => "a string",
        _                                           => "the type the tool reference gives for it",
    };

    /// <summary>Builds the typed error result for an exception.</summary>
    /// <param name="toolName">Tool being called.</param>
    /// <param name="ex">The exception.</param>
    /// <param name="arguments">The call's arguments, when known; used to name the argument a binding failure is about.</param>
    /// <returns>An <c>IsError</c> result whose text is a JSON error object.</returns>
    public static CallToolResult ToErrorResult(string? toolName, Exception ex,
        IReadOnlyDictionary<string, JsonElement>? arguments = null)
    {
        var firstLine = FirstLine(ex.Message);
        object payload;
        if (IsArgumentBindingFailure(ex))
        {
            // "The JSON value could not be converted to System.UInt32" for insertPosition: -1 is InvalidArguments,
            // naming the argument when the arguments show which number cannot be that type.
            var targetType = ConversionTargetType(ex.Message) ?? ConversionTargetType(ex.InnerException?.Message);
            var culprits   = ArgumentsThatCannotBe(targetType, arguments);
            var expected   = ExpectedShape(targetType);
            var tool       = toolName ?? "tool";
            payload = culprits.Count switch
            {
                1 => new
                {
                    error = $"{tool}: argument '{culprits[0]}' has the wrong type or range: {firstLine}",
                    kind = ErrorKinds.InvalidArguments,
                    tool = toolName,
                    parameter = culprits[0],
                    hint = $"'{culprits[0]}' must be {expected}. Fix it and call {tool} again.",
                },
                > 1 => new
                {
                    error = $"{tool}: arguments {string.Join(", ", culprits.Select(c => $"'{c}'"))} have the wrong type or range: {firstLine}",
                    kind = ErrorKinds.InvalidArguments,
                    tool = toolName,
                    parameters = culprits,
                    hint = $"Each of them must be {expected}. Fix them and call {tool} again.",
                },
                _ => (object)new
                {
                    error = $"{tool}: an argument has the wrong type or range: {firstLine}",
                    kind = ErrorKinds.InvalidArguments,
                    tool = toolName,
                    hint = $"The argument must be {expected}; check the tool reference for the parameter types and call {tool} again.",
                },
            };
        }
        else if (ex is ArgumentException && TryMissingParameter(ex.Message, out var parameter))
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
