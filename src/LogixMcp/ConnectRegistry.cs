using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Which server process is answering: its pid and when it started.</summary>
/// <param name="Pid">Process id of this server.</param>
/// <param name="StartedAt">When this server process started (UTC).</param>
public sealed record ServerInstance(
    [property: JsonPropertyName("pid")]       int Pid,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

/// <summary>
/// One connect as recorded in the cross-instance <see cref="ConnectRegistry"/>.
/// </summary>
/// <param name="ConnectId">Id of the connect.</param>
/// <param name="OwnerPid">Pid of the server process that runs the connect.</param>
/// <param name="OwnerStartedAt">Start time of the owning server process.</param>
/// <param name="CommPath">Communication path the connect targets; <c>null</c> when none was given.</param>
/// <param name="Phase">Last phase the connect reached.</param>
/// <param name="ElapsedMs">Milliseconds the connect had run when this entry was written.</param>
/// <param name="Status">
/// <c>connecting</c> while the connect runs; <c>connected</c> or <c>failed</c> once it finished
/// (<see cref="ConnectRegistry.StatusConnecting"/>, <see cref="ConnectRegistry.StatusConnected"/>,
/// <see cref="ConnectRegistry.StatusFailed"/>).
/// </param>
/// <param name="CompletedAt">When the connect finished; <c>null</c> while it runs.</param>
/// <param name="StartedAt">
/// When the connect began (UTC); <c>null</c> in an entry written by an older server, whose elapsed time is then unknown.
/// </param>
public sealed record ConnectRegistryEntry(
    [property: JsonPropertyName("connectId")]      string ConnectId,
    [property: JsonPropertyName("ownerPid")]       int OwnerPid,
    [property: JsonPropertyName("ownerStartedAt")] DateTimeOffset OwnerStartedAt,
    [property: JsonPropertyName("commPath")]       string? CommPath,
    [property: JsonPropertyName("phase")]          string Phase,
    [property: JsonPropertyName("elapsedMs")]      long ElapsedMs,
    [property: JsonPropertyName("status")]         string Status,
    [property: JsonPropertyName("completedAt")]    DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("startedAt")]      DateTimeOffset? StartedAt = null);

/// <summary>
/// A per-user, advisory record of the connects that server instances on this machine are running or have
/// finished: one JSON file per connect id. It lets one instance tell the model that another instance owns a
/// connect, instead of reporting that no connect exists. Every operation swallows its own failures (with an
/// audit line) so the registry can never fail a connect.
/// </summary>
public sealed class ConnectRegistry
{
    /// <summary>Entries older than this are deleted on every write.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(1);

    /// <summary>Default age limit for a finished connect reported by <see cref="FindMostRecent"/>: 10 minutes.</summary>
    public static readonly TimeSpan DefaultReportMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Status of a connect that is still running.</summary>
    public const string StatusConnecting = "connecting";

    /// <summary>Status of a connect that finished successfully.</summary>
    public const string StatusConnected = "connected";

    /// <summary>Status of a connect that finished with an error.</summary>
    public const string StatusFailed = "failed";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly DateTimeOffset ThisProcessStart = ReadOwnStart();

    private readonly string _dir;
    private readonly Func<int, bool> _isAlive;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>The pid this registry writes as the owner.</summary>
    public int Pid { get; }

    /// <summary>The start time this registry writes as the owner's start.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>The directory holding the entries.</summary>
    public string Directory => _dir;

    /// <summary>Age after which an entry is deleted.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>The current time according to this registry's clock (UTC).</summary>
    public DateTimeOffset Now => _clock();

    /// <summary>Who this server instance is.</summary>
    public ServerInstance Instance => new(Pid, StartedAt);

    /// <summary>Creates a registry; the arguments exist so tests can use their own directory, pid and clock.</summary>
    /// <param name="directory">Entry directory; default <c>%LOCALAPPDATA%\lgx-mcp\connects</c>.</param>
    /// <param name="pid">Owner pid; default the current process.</param>
    /// <param name="startedAt">Owner start time; default the current process's start.</param>
    /// <param name="isProcessAlive">Liveness check for a pid; default a <see cref="Process"/> lookup.</param>
    /// <param name="maxAge">Age after which an entry is deleted; default <see cref="DefaultMaxAge"/>.</param>
    /// <param name="clock">Time source; default <see cref="DateTimeOffset.UtcNow"/>.</param>
    public ConnectRegistry(
        string? directory = null, int? pid = null, DateTimeOffset? startedAt = null,
        Func<int, bool>? isProcessAlive = null, TimeSpan? maxAge = null, Func<DateTimeOffset>? clock = null)
    {
        _dir      = directory ?? DefaultDirectory();
        Pid       = pid ?? Environment.ProcessId;
        StartedAt = startedAt ?? ThisProcessStart;
        _isAlive  = isProcessAlive ?? DefaultIsAlive;
        MaxAge    = maxAge ?? DefaultMaxAge;
        _clock    = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The per-user default entry directory.</summary>
    /// <returns><c>%LOCALAPPDATA%\lgx-mcp\connects</c> (the platform's local application data folder).</returns>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lgx-mcp", "connects");

    /// <summary>Identity of the current process, for responses when no registry is in use.</summary>
    /// <returns>The pid and start time of this process.</returns>
    public static ServerInstance CurrentProcess() => new(Environment.ProcessId, ThisProcessStart);

    /// <summary>
    /// Writes (replaces) the entry for a connect, atomically, then deletes stale and dead-owner entries.
    /// Never throws: a failure is recorded as an audit line and the connect carries on.
    /// </summary>
    /// <param name="entry">The entry to store, keyed by its connect id.</param>
    /// <returns><c>true</c> when the entry was written.</returns>
    public bool Write(ConnectRegistryEntry entry)
    {
        var written = false;
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            var final = PathFor(entry.ConnectId);
            var temp  = Path.Combine(_dir, $"{SafeName(entry.ConnectId)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
                File.Move(temp, final, overwrite: true);
                written = true;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { /* best effort */ }
            }
        }
        catch (Exception ex) { Failed("write", ex); }
        Prune(entry.ConnectId);
        return written;
    }

    /// <summary>
    /// Deletes entries older than <see cref="MaxAge"/> and entries whose owner process is gone. Never throws.
    /// </summary>
    /// <param name="keepConnectId">An entry that is never deleted (the one just written); may be <c>null</c>.</param>
    public void Prune(string? keepConnectId = null)
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return;
            var now = _clock().UtcDateTime;
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var isTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (!isTemp && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (keepConnectId is not null && string.Equals(file, PathFor(keepConnectId), StringComparison.Ordinal))
                        continue;

                    var stale = now - File.GetLastWriteTimeUtc(file) > MaxAge;
                    if (!stale && !isTemp)
                    {
                        var entry = ReadFile(file);
                        stale = entry is not null && entry.OwnerPid != Pid && !_isAlive(entry.OwnerPid);
                    }
                    if (stale) File.Delete(file);
                }
                catch (Exception) { /* another instance may have deleted or be replacing it */ }
            }
        }
        catch (Exception ex) { Failed("prune", ex); }
    }

    /// <summary>Reads the entry of one connect.</summary>
    /// <param name="connectId">Id of the connect.</param>
    /// <returns>The entry, or <c>null</c> when there is none, it is unreadable, or the registry fails.</returns>
    public ConnectRegistryEntry? Read(string connectId)
    {
        try { return ReadFile(PathFor(connectId)); }
        catch (Exception ex) { Failed("read", ex); return null; }
    }

    /// <summary>
    /// The most recently written entry that is still in progress, is owned by another instance, and whose owner is alive.
    /// </summary>
    /// <returns>The entry, or <c>null</c> when there is none or the registry fails.</returns>
    public ConnectRegistryEntry? FindMostRecentInProgress()
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return null;
            return System.IO.Directory.EnumerateFiles(_dir, "*.json")
                .Select(f => (Entry: SafeReadFile(f), Written: SafeWriteTime(f)))
                .Where(x => x.Entry is { Status: StatusConnecting, CompletedAt: null } e
                            && e.OwnerPid != Pid && IsOwnerAlive(e))
                .OrderByDescending(x => x.Written)
                .Select(x => x.Entry)
                .FirstOrDefault();
        }
        catch (Exception ex) { Failed("find", ex); return null; }
    }

    /// <summary>
    /// The most recent entry of a connect run by another instance, in progress or (with <paramref name="includeFinished"/>)
    /// finished, that is not older than <paramref name="maxAge"/>. An in-progress entry counts only while its owner is alive;
    /// a finished entry counts only while its owner still runs: one left by an exited instance is history, not a connect
    /// the caller can act on.
    /// </summary>
    /// <param name="includeFinished">When <c>false</c> only in-progress connects are considered.</param>
    /// <param name="maxAge">Age limit (file write time, and completion time for a finished entry); default <see cref="DefaultReportMaxAge"/>.</param>
    /// <returns>The entry, or <c>null</c> when there is none or the registry fails.</returns>
    public ConnectRegistryEntry? FindMostRecent(bool includeFinished, TimeSpan? maxAge = null)
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return null;
            var limit = maxAge ?? DefaultReportMaxAge;
            var now = _clock();
            return System.IO.Directory.EnumerateFiles(_dir, "*.json")
                .Select(f => (Entry: SafeReadFile(f), Written: SafeWriteTime(f)))
                .Where(x => x.Entry is { } e && e.OwnerPid != Pid
                            && now.UtcDateTime - x.Written <= limit
                            && (e.CompletedAt is not { } done || now - done <= limit)
                            && (IsFinished(e) ? includeFinished && IsOwnerAlive(e) : IsOwnerAlive(e)))
                .OrderByDescending(x => x.Written)
                .Select(x => x.Entry)
                .FirstOrDefault();
        }
        catch (Exception ex) { Failed("find", ex); return null; }
    }

    /// <summary>Whether <paramref name="e"/> records a connect that has finished.</summary>
    private static bool IsFinished(ConnectRegistryEntry e) =>
        e.CompletedAt is not null || e.Status != StatusConnecting;

    /// <summary>Whether the process that owns <paramref name="entry"/> is still running.</summary>
    /// <param name="entry">The entry to check.</param>
    /// <returns><c>true</c> when the owner pid is alive.</returns>
    public bool IsOwnerAlive(ConnectRegistryEntry entry)
    {
        try { return _isAlive(entry.OwnerPid); }
        catch (Exception) { return false; }
    }

    private string PathFor(string connectId) => Path.Combine(_dir, SafeName(connectId) + ".json");

    private static string SafeName(string connectId)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(connectId.Select(c => Array.IndexOf(bad, c) >= 0 || c == '.' ? '_' : c).ToArray());
    }

    private static ConnectRegistryEntry? ReadFile(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ConnectRegistryEntry>(fs, Json);
        }
        catch (JsonException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    private static ConnectRegistryEntry? SafeReadFile(string file)
    {
        try { return ReadFile(file); } catch (Exception) { return null; }
    }

    private static DateTime SafeWriteTime(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); } catch (Exception) { return DateTime.MinValue; }
    }

    private static bool DefaultIsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static DateTimeOffset ReadOwnStart()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception) { return DateTimeOffset.UtcNow; }
    }

    private void Failed(string operation, Exception ex)
    {
        try { AuditLogger.LogConnectRegistryFailure(operation, ex.GetType().Name + ": " + ex.Message); }
        catch (Exception) { /* the registry is advisory; never fail the connect */ }
    }
}
