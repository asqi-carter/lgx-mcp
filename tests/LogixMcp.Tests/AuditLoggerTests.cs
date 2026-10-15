using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="AuditLogger"/>.
///
/// <para>
/// Each test redirects writes to a temporary directory so production
/// log paths are never touched.  The temporary directory is created in
/// <see cref="IDisposable"/> teardown via <c>TempLogDir</c>.
/// </para>
/// </summary>
[Collection("AuditLoggerDir")]
public class AuditLoggerTests : IDisposable
{
    // ── Fixture helpers ───────────────────────────────────────────────────────

    private readonly string _tempDir;

    public AuditLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"auditlogger-tests-{Guid.NewGuid():N}");
        // Pre-create the temp directory so tests that clear the file can write to it.
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        // Restore default path and clean up the temp directory.
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string AuditFilePath => Path.Combine(_tempDir, "audit.jsonl");

    /// <summary>
    /// Reads and parses every JSON line written to the temp audit file.
    /// Returns an empty list when the file does not yet exist.
    /// </summary>
    private List<JsonDocument> ReadAllLines()
    {
        if (!File.Exists(AuditFilePath))
            return [];

        return File.ReadAllLines(AuditFilePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    // ── Path resolution ───────────────────────────────────────────────────────

    [Fact]
    public void LogDirectory_DefaultPath_ContainsLogixMcpLogs()
    {
        // Restore the real default to check path resolution.
        AuditLogger.OverrideLogDir(null);

        var logDir = AuditLogger.LogDirectory;

        Assert.Contains("logix-mcp", logDir, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logs",      logDir, StringComparison.OrdinalIgnoreCase);

        // Re-apply test override.
        AuditLogger.OverrideLogDir(_tempDir);
    }

    [Fact]
    public void LogDirectory_DefaultPath_IsAbsolute()
    {
        AuditLogger.OverrideLogDir(null);

        var logDir = AuditLogger.LogDirectory;
        Assert.True(Path.IsPathRooted(logDir), $"Expected absolute path but got: {logDir}");

        AuditLogger.OverrideLogDir(_tempDir);
    }

    [Fact]
    public void OverrideLogDir_Null_RestoresDefault()
    {
        AuditLogger.OverrideLogDir("/some/custom/path");
        AuditLogger.OverrideLogDir(null);

        var logDir = AuditLogger.LogDirectory;
        Assert.Contains("logix-mcp", logDir, StringComparison.OrdinalIgnoreCase);

        // Re-apply test override.
        AuditLogger.OverrideLogDir(_tempDir);
    }

    [Fact]
    public void DefaultLogDir_IgnoresTrafficLoggerThreadOverride()
    {
        // AuditLogger's static initialiser runs once per process, so to test it
        // deterministically we load a fresh copy of the LogixMcp assembly in an
        // isolated, collectible AssemblyLoadContext and trigger its static init
        // on a thread that has a TrafficLogger [ThreadStatic] override set.
        var asmPath = typeof(AuditLogger).Assembly.Location;
        var alc = new AssemblyLoadContext($"auditlogger-fresh-{Guid.NewGuid():N}", isCollectible: true);
        var overrideDir = Path.Combine(Path.GetTempPath(), $"traffic-override-{Guid.NewGuid():N}");
        string? resolved = null;
        Exception? failure = null;
        try
        {
            var asm = alc.LoadFromAssemblyPath(asmPath);
            Assert.NotSame(typeof(AuditLogger).Assembly, asm);

            var t = new Thread(() =>
            {
                try
                {
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    var traffic = asm.GetType("LogixMcp.TrafficLogger") ?? asm.GetType("TrafficLogger")!;
                    var audit   = asm.GetType("LogixMcp.AuditLogger")   ?? asm.GetType("AuditLogger")!;

                    // Set the thread-static override, then touch AuditLogger first.
                    traffic.GetMethod("OverrideLogDir", flags)!.Invoke(null, [overrideDir]);
                    resolved = (string?)audit.GetProperty("LogDirectory", flags)!.GetValue(null);
                }
                catch (Exception ex) { failure = ex; }
            });
            t.Start();
            t.Join();
        }
        finally
        {
            alc.Unload();
        }

        Assert.Null(failure);
        Assert.NotNull(resolved);
        Assert.Contains("logix-mcp", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("traffic-override-", resolved, StringComparison.OrdinalIgnoreCase);
    }

    // ── LogWrite ──────────────────────────────────────────────────────────────

    [Fact]
    public void LogWrite_Success_WritesJsonlLine()
    {
        AuditLogger.LogWrite(
            tag:      "Cmd_MotorStart",
            written:  true,
            observed: true,
            verified: true);

        var lines = ReadAllLines();
        Assert.Single(lines);

        var root = lines[0].RootElement;
        Assert.Equal("write",          root.GetProperty("event").GetString());
        Assert.Equal("Cmd_MotorStart", root.GetProperty("tag").GetString());
        Assert.True(root.GetProperty("written").GetBoolean());
        Assert.True(root.GetProperty("observed").GetBoolean());
        Assert.True(root.GetProperty("verified").GetBoolean());

        // No error/kind on success.
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("kind",  out _));
    }

    [Fact]
    public void LogWrite_IncludesUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AuditLogger.LogWrite("Cmd_SpeedSetpoint", 42.0, 42.0, true);
        var after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var lines = ReadAllLines();
        var ts    = lines[0].RootElement.GetProperty("ts").GetString() is { } t ? DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds() : 0;

        Assert.InRange(ts, before, after);
    }

    [Fact]
    public void LogWrite_WithError_IncludesErrorAndKind()
    {
        AuditLogger.LogWrite(
            tag:      "Safety_OK",
            written:  true,
            observed: null,
            verified: null,
            error:    "Write denied for tag 'Safety_OK': SafetyTag — ...",
            kind:     "WriteDenied");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("write",       root.GetProperty("event").GetString());
        Assert.Equal("Safety_OK",   root.GetProperty("tag").GetString());
        // observed and verified are null (write failed) → omitted by WhenWritingNull.
        Assert.False(root.TryGetProperty("observed", out _));
        Assert.False(root.TryGetProperty("verified", out _));
        Assert.Equal("WriteDenied", root.GetProperty("kind").GetString());
        Assert.NotEmpty(root.GetProperty("error").GetString()!);
    }

    [Fact]
    public void LogWrite_NullOptionalFields_OmittedFromJson()
    {
        AuditLogger.LogWrite("Cmd_MotorStart", false, null, null);

        var json = File.ReadAllText(AuditFilePath);
        // "error" and "kind" should be absent (null → WhenWritingNull removes them).
        Assert.DoesNotContain("\"error\"", json);
        Assert.DoesNotContain("\"kind\"",  json);
    }

    [Fact]
    public void LogWrite_IntegerValue_SerializesAsNumber()
    {
        AuditLogger.LogWrite("Cmd_SpeedSetpoint", 100, 100, true);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("written").ValueKind);
        Assert.Equal(100,                  root.GetProperty("written").GetInt32());
    }

    // ── LogGateDecision ───────────────────────────────────────────────────────

    [Fact]
    public void LogGateDecision_Granted_WritesCorrectFields()
    {
        AuditLogger.LogGateDecision(
            tag:        "Cmd_MotorStart",
            outcome:    "granted",
            reason:     null,
            provenance: "Tag 'Cmd_MotorStart' matches command prefix 'Cmd_'.");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("gate_decision",   root.GetProperty("event").GetString());
        Assert.Equal("Cmd_MotorStart",  root.GetProperty("tag").GetString());
        Assert.Equal("granted",         root.GetProperty("outcome").GetString());
        Assert.NotEmpty(root.GetProperty("provenance").GetString()!);

        // Reason is null for grants, so it should be absent.
        Assert.False(root.TryGetProperty("reason", out _));
    }

    [Fact]
    public void LogGateDecision_Denied_WritesReasonAndProvenance()
    {
        AuditLogger.LogGateDecision(
            tag:        "Safety_OK",
            outcome:    "denied",
            reason:     "SafetyTag",
            provenance: "Tag 'Safety_OK' matches safety prefix 'Safety_'.");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("gate_decision", root.GetProperty("event").GetString());
        Assert.Equal("Safety_OK",     root.GetProperty("tag").GetString());
        Assert.Equal("denied",        root.GetProperty("outcome").GetString());
        Assert.Equal("SafetyTag",     root.GetProperty("reason").GetString());
        Assert.NotEmpty(root.GetProperty("provenance").GetString()!);
    }

    [Fact]
    public void LogGateDecision_BoundsViolation_WritesCorrectReason()
    {
        AuditLogger.LogGateDecision(
            tag:        "Cmd_SpeedSetpoint",
            outcome:    "denied",
            reason:     "BoundsViolation",
            provenance: "Write denied for tag 'Cmd_SpeedSetpoint': value 9999 is outside the allowed range [0..1000].");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("denied",         root.GetProperty("outcome").GetString());
        Assert.Equal("BoundsViolation",root.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Connect_WithAllowlist_AuditRecordsSha256()
    {
        var file = Path.Combine(_tempDir, "allow.txt");
        File.WriteAllText(file, "Motor1_Speed\n");
        var expected = Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(file)).Sha256;

        var ok = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip",
            capabilities: new Capabilities(false, WriteAllowlistPath: file),
            cipConnectProbe: (_, _) => Task.CompletedTask));

        var root = ReadAllLines().Select(d => d.RootElement)
            .Single(e => e.GetProperty("event").GetString() == "connect");
        Assert.Equal(expected, root.GetProperty("allowlist_sha256").GetString());
        Assert.Equal(ok.AllowlistSha256, expected);
    }

    [Fact]
    public void LogConnect_WithoutAllowlist_OmitsSha256()
    {
        AuditLogger.LogConnect("cip", "online-controller", null, null);
        Assert.False(ReadAllLines()[0].RootElement.TryGetProperty("allowlist_sha256", out _));
    }

    // ── LogConnect ────────────────────────────────────────────────────────────

    [Fact]
    public void LogConnect_CipBackend_WritesExpectedFields()
    {
        AuditLogger.LogConnect(
            backend:           "cip",
            target:            "online-controller",
            correlation:       null,
            downloadPerformed: null);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("connect",            root.GetProperty("event").GetString());
        Assert.Equal("cip",                root.GetProperty("backend").GetString());
        Assert.Equal("online-controller",  root.GetProperty("target").GetString());

        // correlation and download_performed are null → omitted.
        Assert.False(root.TryGetProperty("correlation",        out _));
        Assert.False(root.TryGetProperty("download_performed", out _));
    }

    [Fact]
    public void LogConnect_SdkOnline_IncludesCorrelationAndDownload()
    {
        AuditLogger.LogConnect(
            backend:           "sdk",
            target:            "online-controller",
            correlation:       "already-correlated",
            downloadPerformed: false);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("connect",             root.GetProperty("event").GetString());
        Assert.Equal("sdk",                 root.GetProperty("backend").GetString());
        Assert.Equal("already-correlated",  root.GetProperty("correlation").GetString());
        Assert.False(root.GetProperty("download_performed").GetBoolean());
    }

    [Fact]
    public void LogConnect_VirtualBackend_WritesOfflineTarget()
    {
        AuditLogger.LogConnect(
            backend:           "virtual",
            target:            "offline-project",
            correlation:       null,
            downloadPerformed: null);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("virtual",         root.GetProperty("backend").GetString());
        Assert.Equal("offline-project", root.GetProperty("target").GetString());
    }

    [Fact]
    public void LogConnect_UploadFromControllerBranch_IncludesGateSource()
    {
        // The comm-path-wins upload branch: the connect audit record must
        // carry gate_source so the uploaded-controller-project origin of the
        // write-gate policy is queryable in audit.jsonl, not just in the
        // response payload.
        AuditLogger.LogConnect(
            backend:           "sdk",
            target:            "online-controller",
            correlation:       "upload",
            downloadPerformed: false,
            gateSource:        "uploaded-controller-project");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("connect",                      root.GetProperty("event").GetString());
        Assert.Equal("uploaded-controller-project",  root.GetProperty("gate_source").GetString());
    }

    [Fact]
    public void LogConnect_GateSourceOmitted_DefaultsToNullAndIsAbsentFromJson()
    {
        // Regression guard: every non-upload branch calls LogConnect without a
        // gateSource argument, so the field must default to null and be
        // omitted from the JSON line (WhenWritingNull) rather than serialised
        // as "gate_source":null.
        AuditLogger.LogConnect(
            backend:           "cip",
            target:            "online-controller",
            correlation:       null,
            downloadPerformed: null);

        var root = ReadAllLines()[0].RootElement;
        Assert.False(root.TryGetProperty("gate_source", out _));
    }

    // ── LogDisconnect ─────────────────────────────────────────────────────────

    [Fact]
    public void LogDisconnect_WritesEventField()
    {
        AuditLogger.LogDisconnect();

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("disconnect", root.GetProperty("event").GetString());
        Assert.True(root.TryGetProperty("ts", out var ts));
        Assert.True(DateTimeOffset.Parse(ts.GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds() > 0);
    }

    // ── LogProjectClosed ──────────────────────────────────────────────────────

    [Fact]
    public void LogProjectClosed_SdkSessionDisposed_WritesPathAndTrueFlag()
    {
        AuditLogger.LogProjectClosed(@"C:\projects\Line3.ACD", sdkSessionDisposed: true);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("project_closed",           root.GetProperty("event").GetString());
        Assert.Equal(@"C:\projects\Line3.ACD",   root.GetProperty("path").GetString());
        Assert.True(root.GetProperty("sdk_session_disposed").GetBoolean());
    }

    [Fact]
    public void LogProjectClosed_NoSdkSession_WritesPathAndFalseFlag()
    {
        // Core-only build, or the active tag session (if any) was not
        // SDK-backed — the flag must still be written, as an explicit false,
        // not omitted, so audit consumers can distinguish "no SDK session" from
        // "field not populated".
        AuditLogger.LogProjectClosed("/data/projects/Line3.acd", sdkSessionDisposed: false);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("project_closed",                 root.GetProperty("event").GetString());
        Assert.Equal("/data/projects/Line3.acd",    root.GetProperty("path").GetString());
        Assert.False(root.GetProperty("sdk_session_disposed").GetBoolean());
    }

    [Fact]
    public void LogProjectClosed_IncludesUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AuditLogger.LogProjectClosed("/tmp/demo.acd", sdkSessionDisposed: false);
        var after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var ts = ReadAllLines()[0].RootElement.GetProperty("ts").GetString() is { } t ? DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds() : 0;
        Assert.InRange(ts, before, after);
    }

    // ── LogRungEdit ───────────────────────────────────────────────────────────

    [Fact]
    public void LogRungEdit_Proposed_WritesOnlineStructuralVerify()
    {
        const string xpath =
            "Controller/Programs/Program[@Name='Main']/Routines/Routine[@Name='MainRoutine']";

        AuditLogger.LogRungEdit(
            routine:         xpath,
            insertPosition:  3,
            outcome:         "proposed",
            structuralVerify: "online");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("rung_edit",  root.GetProperty("event").GetString());
        Assert.Equal(xpath,        root.GetProperty("routine").GetString());
        Assert.Equal(3,            root.GetProperty("insert_position").GetInt32());
        Assert.Equal("proposed",   root.GetProperty("outcome").GetString());
        Assert.Equal("online",     root.GetProperty("structural_verify").GetString());
    }

    [Fact]
    public void LogRungEdit_WorkstationStage_WritesStagedInAndVerified()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "proposed",
            structuralVerify: null,
            destination:      "workstation",
            stagedIn:         "session-image",
            verified:         false);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("session-image", root.GetProperty("staged_in").GetString());
        Assert.False(root.GetProperty("verified").GetBoolean());
        Assert.False(root.TryGetProperty("structural_verify", out _));
    }

    [Fact]
    public void LogRungEdit_SavedToFile_NullStructuralVerify_Omitted()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "saved-to-file",
            structuralVerify: null);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("saved-to-file", root.GetProperty("outcome").GetString());
        Assert.False(root.TryGetProperty("structural_verify", out _));
    }

    [Fact]
    public void LogRungEdit_Denied_WritesCorrectOutcome()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "denied",
            structuralVerify: null);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("denied", root.GetProperty("outcome").GetString());
    }

    [Fact]
    public void LogRungEdit_Error_WritesStructuralVerifyState()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "error",
            structuralVerify: "Connected");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("error",     root.GetProperty("outcome").GetString());
        Assert.Equal("Connected", root.GetProperty("structural_verify").GetString());
    }

    /// <summary>
    /// A rung_edit record with <c>destination="controller"</c> must include the
    /// field in the JSON output so that a controller-bound edit is distinguishable
    /// from a workstation-only edit without further context.
    /// </summary>
    [Fact]
    public void LogRungEdit_ControllerDestination_WritesDestinationField()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "proposed",
            structuralVerify: "online",
            destination:      "controller");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("rung_edit",   root.GetProperty("event").GetString());
        Assert.Equal("controller",  root.GetProperty("destination").GetString());
    }

    /// <summary>
    /// A rung_edit record with <c>destination="workstation"</c> must include the
    /// field in the JSON output so that a workstation-only edit is distinguishable
    /// from a controller-bound edit without further context.
    /// </summary>
    [Fact]
    public void LogRungEdit_WorkstationDestination_WritesDestinationField()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "proposed",
            structuralVerify: "online",
            destination:      "workstation");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("rung_edit",   root.GetProperty("event").GetString());
        Assert.Equal("workstation", root.GetProperty("destination").GetString());
    }

    /// <summary>
    /// A rung_edit record with <c>destination="file"</c> must include the
    /// field in the JSON output so that an offline file edit is distinguishable
    /// from a controller or workstation edit.
    /// </summary>
    [Fact]
    public void LogRungEdit_FileDestination_WritesDestinationField()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "saved-to-file",
            structuralVerify: null,
            destination:      "file");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("rung_edit", root.GetProperty("event").GetString());
        Assert.Equal("file",      root.GetProperty("destination").GetString());
    }

    /// <summary>
    /// When <c>destination</c> is omitted (null), the field must be absent from
    /// the JSON output — consistent with the WhenWritingNull policy applied to all
    /// audit records.
    /// </summary>
    [Fact]
    public void LogRungEdit_NullDestination_OmittedFromJson()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "error",
            structuralVerify: null);

        var root = ReadAllLines()[0].RootElement;
        Assert.False(root.TryGetProperty("destination", out _),
            "destination must be absent from JSON when null (WhenWritingNull).");
    }

    /// <summary>
    /// When <c>gateAnnotations</c> are provided, the <c>gate_annotations</c> array
    /// must be present in the JSON output and each entry must contain the tag,
    /// classification, and provenance fields.
    /// </summary>
    [Fact]
    public void LogRungEdit_WithGateAnnotations_WritesAnnotationsField()
    {
        var annotations = new List<AuditGateAnnotation>
        {
            new AuditGateAnnotation("Motor_Run",  "ProgramWritten", "fragment[5] via OTE"),
            new AuditGateAnnotation("Safety_OK",  "SafetyTag",      "fragment[3] via OTE"),
        };

        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "proposed",
            structuralVerify: "online",
            destination:      "workstation",
            gateAnnotations:  annotations);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("rung_edit", root.GetProperty("event").GetString());

        Assert.True(root.TryGetProperty("gate_annotations", out var gateAnnotations),
            "gate_annotations must be present when annotations are provided.");

        var annotationArray = gateAnnotations.EnumerateArray().ToList();
        Assert.Equal(2, annotationArray.Count);

        // First entry.
        Assert.Equal("Motor_Run",      annotationArray[0].GetProperty("tag").GetString());
        Assert.Equal("ProgramWritten", annotationArray[0].GetProperty("classification").GetString());
        Assert.Equal("fragment[5] via OTE", annotationArray[0].GetProperty("provenance").GetString());

        // Second entry.
        Assert.Equal("Safety_OK",           annotationArray[1].GetProperty("tag").GetString());
        Assert.Equal("SafetyTag",           annotationArray[1].GetProperty("classification").GetString());
        Assert.Equal("fragment[3] via OTE", annotationArray[1].GetProperty("provenance").GetString());
    }

    /// <summary>
    /// When <c>gateAnnotations</c> is <c>null</c> (not provided), the
    /// <c>gate_annotations</c> field must be absent from the JSON output —
    /// consistent with the <see cref="JsonIgnoreCondition.WhenWritingNull"/> policy
    /// applied to all audit records.
    /// </summary>
    [Fact]
    public void LogRungEdit_NullGateAnnotations_OmittedFromJson()
    {
        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/...",
            insertPosition:   0,
            outcome:          "proposed",
            structuralVerify: "online",
            destination:      "workstation");

        var root = ReadAllLines()[0].RootElement;
        Assert.False(root.TryGetProperty("gate_annotations", out _),
            "gate_annotations must be absent from JSON when null (WhenWritingNull).");
    }

    /// <summary>
    /// A <c>proposed</c> audit record with a SafetyTag annotation must be
    /// distinguishable from one without — the classification field names the risk
    /// so the record is reconstructable from the log without additional context.
    /// </summary>
    [Fact]
    public void LogRungEdit_SafetyTagAnnotation_ClassificationPreservedInAudit()
    {
        var annotations = new List<AuditGateAnnotation>
        {
            new AuditGateAnnotation("Safety_EmergencyStop", "SafetyTag",
                "fragment[1] via OTE"),
        };

        AuditLogger.LogRungEdit(
            routine:          "Controller/Programs/Program[@Name='Safety']/Routines/Routine[@Name='MainRoutine']",
            insertPosition:   2,
            outcome:          "proposed",
            structuralVerify: "online",
            destination:      "controller",
            gateAnnotations:  annotations);

        var root = ReadAllLines()[0].RootElement;
        Assert.True(root.TryGetProperty("gate_annotations", out var ga));
        var entries = ga.EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("Safety_EmergencyStop", entries[0].GetProperty("tag").GetString());
        Assert.Equal("SafetyTag",            entries[0].GetProperty("classification").GetString());
        Assert.NotEmpty(entries[0].GetProperty("provenance").GetString()!);
    }

    /// <summary>
    /// A controller-bound edit and a workstation-only edit must not produce
    /// identical audit log entries — the destination field must differ.
    /// </summary>
    [Fact]
    public void LogRungEdit_ControllerVsWorkstation_ProducesDistinctEntries()
    {
        AuditLogger.LogRungEdit("Controller/Programs/...", 0, "proposed", "online",
            destination: "controller");
        AuditLogger.LogRungEdit("Controller/Programs/...", 0, "proposed", "online",
            destination: "workstation");

        var lines = ReadAllLines();
        Assert.Equal(2, lines.Count);

        var controllerDest  = lines[0].RootElement.GetProperty("destination").GetString();
        var workstationDest = lines[1].RootElement.GetProperty("destination").GetString();

        Assert.Equal("controller",  controllerDest);
        Assert.Equal("workstation", workstationDest);
        Assert.NotEqual(controllerDest, workstationDest);
    }

    // ── LogTagsStaged ─────────────────────────────────────────────────────────

    /// <summary>
    /// A single tag name produces exactly one <c>tags_staged</c> line in the log.
    /// The line must include the event field, the proposal_id, the tag name, and
    /// a valid timestamp.
    /// </summary>
    [Fact]
    public void LogTagsStaged_SingleTag_WritesTagsStagedEvent()
    {
        const string proposalId = "abc123";
        AuditLogger.LogTagsStaged(proposalId, ["Cmd_MotorStart"]);

        var lines = ReadAllLines();
        Assert.Single(lines);

        var root = lines[0].RootElement;
        Assert.Equal("tags_staged",  root.GetProperty("event").GetString());
        Assert.Equal(proposalId,     root.GetProperty("proposal_id").GetString());
        Assert.Equal("Cmd_MotorStart", root.GetProperty("tag").GetString());
        Assert.True(root.TryGetProperty("ts", out var ts));
        Assert.True(DateTimeOffset.Parse(ts.GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds() > 0);
    }

    /// <summary>
    /// Multiple tag names each produce their own <c>tags_staged</c> line so each
    /// declaration is independently queryable.  All lines must share the same
    /// proposal_id.
    /// </summary>
    [Fact]
    public void LogTagsStaged_MultipleTags_WritesOneEventPerTag()
    {
        const string proposalId = "proposal-001";
        AuditLogger.LogTagsStaged(proposalId, ["Cmd_MotorStart", "Cmd_ValveOpen", "Sim_Feedback"]);

        var lines = ReadAllLines();
        Assert.Equal(3, lines.Count);

        var tagNames = lines.Select(l => l.RootElement.GetProperty("tag").GetString()).ToList();
        Assert.Contains("Cmd_MotorStart",  tagNames);
        Assert.Contains("Cmd_ValveOpen",   tagNames);
        Assert.Contains("Sim_Feedback",    tagNames);

        // All lines must share the same proposal_id.
        foreach (var line in lines)
            Assert.Equal(proposalId, line.RootElement.GetProperty("proposal_id").GetString());
    }

    /// <summary>
    /// An empty tag name sequence produces no audit lines — there is nothing to record.
    /// </summary>
    [Fact]
    public void LogTagsStaged_EmptySequence_WritesNoLines()
    {
        AuditLogger.LogTagsStaged("pid", []);
        Assert.False(File.Exists(AuditFilePath));
    }

    // ── LogTagsCreated ────────────────────────────────────────────────────────

    /// <summary>
    /// A single tag produces exactly one <c>tags_created</c> line containing the
    /// event, proposal_id, tag name, and granted_namespace boolean.
    /// </summary>
    [Fact]
    public void LogTagsCreated_SingleTag_WritesTagsCreatedEvent()
    {
        const string proposalId = "publish-001";
        AuditLogger.LogTagsCreated(proposalId, [("Cmd_MotorStart", true)]);

        var lines = ReadAllLines();
        Assert.Single(lines);

        var root = lines[0].RootElement;
        Assert.Equal("tags_created",   root.GetProperty("event").GetString());
        Assert.Equal(proposalId,       root.GetProperty("proposal_id").GetString());
        Assert.Equal("Cmd_MotorStart", root.GetProperty("tag").GetString());
        Assert.True(root.GetProperty("granted_namespace").GetBoolean());
        Assert.True(root.TryGetProperty("ts", out var ts));
        Assert.True(DateTimeOffset.Parse(ts.GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds() > 0);
    }

    /// <summary>
    /// <c>granted_namespace</c> is <c>false</c> for a tag not in a command
    /// namespace — the field is always present so the log is self-describing.
    /// </summary>
    [Fact]
    public void LogTagsCreated_NonGrantedNamespace_WritesGrantedNamespaceFalse()
    {
        AuditLogger.LogTagsCreated("pid", [("UserData_Counter", false)]);

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("tags_created",    root.GetProperty("event").GetString());
        Assert.Equal("UserData_Counter", root.GetProperty("tag").GetString());
        Assert.False(root.GetProperty("granted_namespace").GetBoolean());
    }

    /// <summary>
    /// Multiple tag pairs each produce their own <c>tags_created</c> line.  All
    /// lines must share the same proposal_id, and each must carry the correct
    /// granted_namespace value for that tag.
    /// </summary>
    [Fact]
    public void LogTagsCreated_MultipleTags_WritesOneEventPerTag()
    {
        const string proposalId = "publish-002";
        AuditLogger.LogTagsCreated(proposalId,
        [
            ("Cmd_MotorStart", true),
            ("Sim_Feedback",   true),
            ("UserData_Count", false),
        ]);

        var lines = ReadAllLines();
        Assert.Equal(3, lines.Count);

        // All must share the same proposal_id.
        foreach (var line in lines)
            Assert.Equal(proposalId, line.RootElement.GetProperty("proposal_id").GetString());

        // Verify event type and granted_namespace for each.
        Assert.All(lines, l =>
            Assert.Equal("tags_created", l.RootElement.GetProperty("event").GetString()));

        var grantedMap = lines.ToDictionary(
            l => l.RootElement.GetProperty("tag").GetString()!,
            l => l.RootElement.GetProperty("granted_namespace").GetBoolean());

        Assert.True(grantedMap["Cmd_MotorStart"]);
        Assert.True(grantedMap["Sim_Feedback"]);
        Assert.False(grantedMap["UserData_Count"]);
    }

    /// <summary>
    /// An empty tag sequence produces no audit lines.
    /// </summary>
    [Fact]
    public void LogTagsCreated_EmptySequence_WritesNoLines()
    {
        AuditLogger.LogTagsCreated("pid", []);
        Assert.False(File.Exists(AuditFilePath));
    }

    /// <summary>
    /// <c>tags_staged</c> and <c>tags_created</c> events with the same proposal_id
    /// are distinguishable solely from <c>audit.jsonl</c>: event field names differ,
    /// <c>granted_namespace</c> is present only on <c>tags_created</c>.
    /// </summary>
    [Fact]
    public void LogTagsStaged_ThenLogTagsCreated_ProducesDistinctReconstructableHistory()
    {
        const string proposalId = "session-reconstruct-001";
        AuditLogger.LogTagsStaged( proposalId, ["Cmd_MotorStart"]);
        AuditLogger.LogTagsCreated(proposalId, [("Cmd_MotorStart", true)]);

        var lines = ReadAllLines();
        Assert.Equal(2, lines.Count);

        var staged  = lines[0].RootElement;
        var created = lines[1].RootElement;

        Assert.Equal("tags_staged",  staged.GetProperty("event").GetString());
        Assert.Equal("tags_created", created.GetProperty("event").GetString());
        Assert.Equal(proposalId,     staged.GetProperty("proposal_id").GetString());
        Assert.Equal(proposalId,     created.GetProperty("proposal_id").GetString());

        // tags_staged must NOT carry granted_namespace.
        Assert.False(staged.TryGetProperty("granted_namespace", out _),
            "tags_staged must not include granted_namespace.");

        // tags_created must carry granted_namespace.
        Assert.True(created.TryGetProperty("granted_namespace", out _),
            "tags_created must include granted_namespace.");
    }

    // ── Multi-event ordering ──────────────────────────────────────────────────

    [Fact]
    public void MultipleEvents_AppendInOrder()
    {
        AuditLogger.LogConnect("cip", "online-controller", null, null);
        AuditLogger.LogGateDecision("Cmd_MotorStart", "granted", null, "provenance");
        AuditLogger.LogWrite("Cmd_MotorStart", true, true, true);
        AuditLogger.LogDisconnect();

        var lines = ReadAllLines();
        Assert.Equal(4, lines.Count);
        Assert.Equal("connect",       lines[0].RootElement.GetProperty("event").GetString());
        Assert.Equal("gate_decision", lines[1].RootElement.GetProperty("event").GetString());
        Assert.Equal("write",         lines[2].RootElement.GetProperty("event").GetString());
        Assert.Equal("disconnect",    lines[3].RootElement.GetProperty("event").GetString());
    }

    // ── Fault tolerance ───────────────────────────────────────────────────────

    /// <summary>
    /// All public Log* methods must swallow I/O failures so that a full disk,
    /// a locked file, or an unwritable directory never propagates to the caller.
    /// We simulate this by redirecting the log directory to a path whose parent
    /// is an existing file (so <see cref="Directory.CreateDirectory"/> will fail),
    /// then assert that none of the Log* methods throw.
    /// </summary>
    [Fact]
    public void Append_UnwritableDirectory_NeverThrows()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Point the logger at a path inside a file — CreateDirectory will fail.
            AuditLogger.OverrideLogDir(Path.Combine(tempFile, "nested", "dir"));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogWrite("Cmd_MotorStart", true, true, true)));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogGateDecision("Cmd_MotorStart", "granted", null, "provenance")));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogConnect("cip", "online-controller", null, null)));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogDisconnect()));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogRungEdit("Controller/Programs/...", 0, "error", null)));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogTagsStaged("pid", ["Cmd_MotorStart"])));

            Assert.Null(Record.Exception(() =>
                AuditLogger.LogTagsCreated("pid", [("Cmd_MotorStart", true)])));
        }
        finally
        {
            File.Delete(tempFile);
            // Restore the per-test temp-dir override so teardown is clean.
            AuditLogger.OverrideLogDir(_tempDir);
        }
    }

    // ── GatedTagService integration ───────────────────────────────────────────

    [Fact]
    public async Task GatedTagService_WriteGranted_LogsGateDecisionAndWrite()
    {
        // Build a minimal policy with one grant.
        var grants = new List<GrantEntry>
        {
            new GrantEntry("Cmd_MotorStart", null, null,
                "Tag 'Cmd_MotorStart' matches command prefix 'Cmd_'."),
        };
        var policy = new WriteGatePolicy(grants, []);
        var inner  = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });
        var gated  = new GatedTagService(inner, policy);

        await gated.WriteAsync("Cmd_MotorStart", true);

        var lines = ReadAllLines();
        Assert.Single(lines);
        var root = lines[0].RootElement;
        Assert.Equal("gate_decision", root.GetProperty("event").GetString());
        Assert.Equal("granted",       root.GetProperty("outcome").GetString());
        Assert.Equal("Cmd_MotorStart",root.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task GatedTagService_WriteDenied_LogsGateDecisionWithReason()
    {
        var denies = new List<DenyEntry>
        {
            new DenyEntry("Safety_OK", DenyReason.SafetyTag,
                "Tag 'Safety_OK' matches safety prefix 'Safety_'."),
        };
        var policy = new WriteGatePolicy([], denies);
        var inner  = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Safety_OK"] = (true, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        // Gate denial throws; we verify the audit event before the throw.
        await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Safety_OK", false));

        var lines = ReadAllLines();
        Assert.Single(lines);
        var root = lines[0].RootElement;
        Assert.Equal("gate_decision", root.GetProperty("event").GetString());
        Assert.Equal("denied",        root.GetProperty("outcome").GetString());
        Assert.Equal("SafetyTag",     root.GetProperty("reason").GetString());

        // Provenance must be present and non-empty for denial decisions.
        Assert.True(root.TryGetProperty("provenance", out var provenance),
            "Denied gate decision must include a 'provenance' field.");
        Assert.NotEmpty(provenance.GetString()!);
    }

    /// <summary>
    /// A denied write whose deny entry carries a provenance string must log
    /// that exact string in the <c>provenance</c> field of the audit record.
    /// Covers acceptance criterion: a denied write appends an audit line with
    /// reason and provenance.
    /// </summary>
    [Fact]
    public async Task GatedTagService_WriteDenied_LogsCorrectProvenance()
    {
        const string expectedProvenance = "Tag 'Motor_Run' is written by program logic at MainProgram/MainRoutine[0] via OTE.";

        var denies = new List<DenyEntry>
        {
            new DenyEntry("Motor_Run", DenyReason.ProgramWritten, expectedProvenance),
        };
        var policy = new WriteGatePolicy([], denies);
        var inner  = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Motor_Run"] = (false, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", true));

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("gate_decision",   root.GetProperty("event").GetString());
        Assert.Equal("denied",          root.GetProperty("outcome").GetString());
        Assert.Equal("ProgramWritten",  root.GetProperty("reason").GetString());
        Assert.Equal(expectedProvenance, root.GetProperty("provenance").GetString());
    }

    /// <summary>
    /// Exercises the full <see cref="WriteTagHandler.ExecuteAsync"/> path for a
    /// granted write, verifying that the audit log contains BOTH a
    /// <c>gate_decision</c> (granted) record from the gate and a subsequent
    /// <c>write</c> record from the handler — in that order.
    /// Covers acceptance criterion: a granted write appends a correct audit line.
    /// </summary>
    [Fact]
    public async Task WriteTagHandler_GrantedWrite_LogsGateDecisionThenWriteEvent()
    {
        const string provenance = "Tag 'Cmd_MotorStart' matches command prefix 'Cmd_'.";

        var grants = new List<GrantEntry>
        {
            new GrantEntry("Cmd_MotorStart", null, null, provenance),
        };
        var policy = new WriteGatePolicy(grants, []);
        var inner  = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        // Clear any disconnect event the first Attach might produce.
        session.Attach(gated, isVirtual: true);
        File.Delete(AuditFilePath);

        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_MotorStart", "true", session);

        Assert.IsType<WriteTagResult>(result);

        var lines = ReadAllLines();
        Assert.Equal(2, lines.Count);

        // Line 0: gate decision (granted).
        var gate = lines[0].RootElement;
        Assert.Equal("gate_decision",  gate.GetProperty("event").GetString());
        Assert.Equal("granted",        gate.GetProperty("outcome").GetString());
        Assert.Equal("Cmd_MotorStart", gate.GetProperty("tag").GetString());
        Assert.NotEmpty(gate.GetProperty("provenance").GetString()!);

        // Line 1: write event with tag, written, observed, verified.
        var write = lines[1].RootElement;
        Assert.Equal("write",          write.GetProperty("event").GetString());
        Assert.Equal("Cmd_MotorStart", write.GetProperty("tag").GetString());
        Assert.True(write.GetProperty("written").GetBoolean());
        Assert.True(write.GetProperty("observed").GetBoolean());
        Assert.True(write.GetProperty("verified").GetBoolean());

        // No error or kind on success.
        Assert.False(write.TryGetProperty("error", out _));
        Assert.False(write.TryGetProperty("kind",  out _));
    }

    /// <summary>
    /// Exercises the full <see cref="WriteTagHandler.ExecuteAsync"/> path for a
    /// denied write, verifying that the audit log contains a <c>gate_decision</c>
    /// (denied) record with <c>reason</c> and <c>provenance</c>, followed by a
    /// <c>write</c> record carrying <c>error</c> and <c>kind</c>.
    /// Covers acceptance criterion: a denied write appends an audit line with reason
    /// and provenance.
    /// </summary>
    [Fact]
    public async Task WriteTagHandler_DeniedWrite_LogsGateDecisionWithReasonAndProvenance()
    {
        const string provenance = "Tag 'Safety_OK' matches safety prefix 'Safety_'.";

        var denies = new List<DenyEntry>
        {
            new DenyEntry("Safety_OK", DenyReason.SafetyTag, provenance),
        };
        var policy = new WriteGatePolicy([], denies);
        var inner  = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Safety_OK"] = (true, "BOOL"),
        });
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);
        File.Delete(AuditFilePath);

        var result = await WriteTagHandler.ExecuteAsync(
            "Safety_OK", "false", session);

        // Handler converts the exception to a WriteTagError — never throws.
        Assert.IsType<WriteTagError>(result);

        var lines = ReadAllLines();
        Assert.Equal(2, lines.Count);

        // Line 0: gate decision (denied) with reason and provenance.
        var gate = lines[0].RootElement;
        Assert.Equal("gate_decision", gate.GetProperty("event").GetString());
        Assert.Equal("denied",        gate.GetProperty("outcome").GetString());
        Assert.Equal("Safety_OK",     gate.GetProperty("tag").GetString());
        Assert.Equal("SafetyTag",     gate.GetProperty("reason").GetString());
        Assert.Equal(provenance,      gate.GetProperty("provenance").GetString());

        // Line 1: write event carrying error details.
        var write = lines[1].RootElement;
        Assert.Equal("write",       write.GetProperty("event").GetString());
        Assert.Equal("Safety_OK",   write.GetProperty("tag").GetString());
        Assert.Equal("WriteDenied", write.GetProperty("kind").GetString());
        Assert.NotEmpty(write.GetProperty("error").GetString()!);
    }

    // ── TagSessionService integration ─────────────────────────────────────────

    [Fact]
    public void TagSessionService_Detach_WithActiveSession_LogsDisconnect()
    {
        var inner   = new VirtualTagService(new Dictionary<string, (object, string?)>());
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        // Clear lines written by Attach (no prior session → no disconnect logged).
        File.Delete(AuditFilePath);

        session.Detach();

        var lines = ReadAllLines();
        Assert.Single(lines);
        Assert.Equal("disconnect", lines[0].RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public void TagSessionService_Attach_ReplacesPriorSession_LogsDisconnect()
    {
        var svc1    = new VirtualTagService(new Dictionary<string, (object, string?)>());
        var svc2    = new VirtualTagService(new Dictionary<string, (object, string?)>());
        var session = new TagSessionService();

        // First attach — no prior session, so no disconnect event.
        session.Attach(svc1, isVirtual: true);
        File.WriteAllText(AuditFilePath, string.Empty);   // clear the file

        // Second attach — prior session exists, should log disconnect.
        session.Attach(svc2, isVirtual: true);

        var lines = ReadAllLines();
        Assert.Single(lines);
        Assert.Equal("disconnect", lines[0].RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public void TagSessionService_Detach_NoSession_DoesNotLogDisconnect()
    {
        var session = new TagSessionService();
        session.Detach();    // nothing attached yet

        Assert.False(File.Exists(AuditFilePath));
    }

    // ── LogSdkServiceDiagnosis ────────────────────────────────────────────────

    /// <summary>
    /// A <c>sdk_service_diagnosis</c> record with <c>probe="reachable"</c> and
    /// <c>kind="SdkServiceDegraded"</c> must include all three required fields and
    /// the correct event name.
    /// </summary>
    [Fact]
    public void LogSdkServiceDiagnosis_Reachable_WritesCorrectFields()
    {
        AuditLogger.LogSdkServiceDiagnosis(
            sdkCode: "RxE_COMM_DTLERROR - communications layer fault",
            probe:   "reachable",
            kind:    "SdkServiceDegraded");

        var lines = ReadAllLines();
        Assert.Single(lines);

        var root = lines[0].RootElement;
        Assert.Equal("sdk_service_diagnosis",             root.GetProperty("event").GetString());
        Assert.Equal("RxE_COMM_DTLERROR - communications layer fault",
                                                          root.GetProperty("sdk_code").GetString());
        Assert.Equal("reachable",                         root.GetProperty("probe").GetString());
        Assert.Equal("SdkServiceDegraded",                root.GetProperty("kind").GetString());
    }

    /// <summary>
    /// A <c>sdk_service_diagnosis</c> record with <c>probe="unreachable"</c> and
    /// <c>kind="ConnectionFailed"</c> must serialize correctly.
    /// </summary>
    [Fact]
    public void LogSdkServiceDiagnosis_Unreachable_WritesCorrectFields()
    {
        AuditLogger.LogSdkServiceDiagnosis(
            sdkCode: "RxE_COMM_DTLERROR - failed to call FactoryTalk Linx",
            probe:   "unreachable",
            kind:    "ConnectionFailed");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("sdk_service_diagnosis", root.GetProperty("event").GetString());
        Assert.Equal("unreachable",           root.GetProperty("probe").GetString());
        Assert.Equal("ConnectionFailed",      root.GetProperty("kind").GetString());
    }

    /// <summary>
    /// A <c>sdk_service_diagnosis</c> record with <c>probe="not-probed"</c> must
    /// serialize correctly.  This is emitted when the IP is not derivable from the
    /// comm path, or on the in-session path where no TCP probe is performed.
    /// </summary>
    [Fact]
    public void LogSdkServiceDiagnosis_NotProbed_WritesCorrectFields()
    {
        AuditLogger.LogSdkServiceDiagnosis(
            sdkCode: "RxE_COMM_OTHER",
            probe:   "not-probed",
            kind:    "ConnectionFailed");

        var root = ReadAllLines()[0].RootElement;
        Assert.Equal("sdk_service_diagnosis", root.GetProperty("event").GetString());
        Assert.Equal("not-probed",            root.GetProperty("probe").GetString());
        Assert.Equal("ConnectionFailed",      root.GetProperty("kind").GetString());
        Assert.Equal("RxE_COMM_OTHER",        root.GetProperty("sdk_code").GetString());
    }

    /// <summary>
    /// The <c>sdk_service_diagnosis</c> record must include a valid Unix-second
    /// timestamp in the <c>ts</c> field, consistent with all other audit events.
    /// </summary>
    [Fact]
    public void LogSdkServiceDiagnosis_IncludesUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AuditLogger.LogSdkServiceDiagnosis("RxE_COMM_DTLERROR", "reachable", "SdkServiceDegraded");
        var after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var ts = ReadAllLines()[0].RootElement.GetProperty("ts").GetString() is { } t ? DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds() : 0;
        Assert.InRange(ts, before, after);
    }

    /// <summary>
    /// Each call to <see cref="AuditLogger.LogSdkServiceDiagnosis"/> must write
    /// exactly one line — multiple diagnoses in a single operation accumulate
    /// independently.
    /// </summary>
    [Fact]
    public void LogSdkServiceDiagnosis_MultipleCallsWriteMultipleLines()
    {
        AuditLogger.LogSdkServiceDiagnosis("RxE_COMM_DTLERROR", "reachable",   "SdkServiceDegraded");
        AuditLogger.LogSdkServiceDiagnosis("RxE_COMM_OTHER",    "unreachable", "ConnectionFailed");

        var lines = ReadAllLines();
        Assert.Equal(2, lines.Count);
        Assert.Equal("reachable",   lines[0].RootElement.GetProperty("probe").GetString());
        Assert.Equal("unreachable", lines[1].RootElement.GetProperty("probe").GetString());
    }

    // ── Rotation (LGX_AUDIT_MAX_MB / LGX_AUDIT_KEEP) ─────────────────────────

    private string RotatedPath(int n) => Path.Combine(_tempDir, $"audit.{n}.jsonl");

    private static void LogMany(int count)
    {
        for (var i = 0; i < count; i++)
            AuditLogger.LogGateDecision($"Tag_{i:D4}", "granted", null, "fragment[0] padding padding padding padding");
    }

    /// <summary>
    /// With a tiny MaxMb and Keep=2, filling the log many times over rotates
    /// into audit.1/audit.2 only, never exceeds the cap, and never loses the
    /// newest events.
    /// </summary>
    [Fact]
    public void Rotates_WhenAppendWouldExceedMax_KeepsN()
    {
        const double maxMb = 2000.0 / (1024 * 1024); // ~2000 bytes
        AuditLogger.OverrideRotation(maxMb, keep: 2);
        try
        {
            LogMany(100);

            Assert.True(File.Exists(AuditFilePath));
            Assert.True(File.Exists(RotatedPath(1)));
            Assert.True(File.Exists(RotatedPath(2)));
            Assert.False(File.Exists(RotatedPath(3)), "Keep=2 must not retain a third rotated file");

            var cap = (long)(maxMb * 1024 * 1024);
            Assert.True(new FileInfo(AuditFilePath).Length <= cap);
            Assert.True(new FileInfo(RotatedPath(1)).Length <= cap);

            // Newest event is in the live file; ordering across files is newest-first.
            var live = File.ReadAllLines(AuditFilePath);
            Assert.Contains("Tag_0099", live[^1]);
            var r1 = File.ReadAllLines(RotatedPath(1));
            var r2 = File.ReadAllLines(RotatedPath(2));
            Assert.True(string.CompareOrdinal(r1[^1], r2[^1]) > 0);
            Assert.True(string.CompareOrdinal(live[0], r1[^1]) > 0);
        }
        finally { AuditLogger.OverrideRotation(null); }
    }

    /// <summary>MaxMb=0 disables rotation entirely.</summary>
    [Fact]
    public void MaxZero_NeverRotates()
    {
        AuditLogger.OverrideRotation(0, keep: 3);
        try
        {
            LogMany(200);

            Assert.False(File.Exists(RotatedPath(1)));
            Assert.Equal(200, File.ReadAllLines(AuditFilePath).Length);
        }
        finally { AuditLogger.OverrideRotation(null); }
    }

    /// <summary>
    /// When the rename fails, every event is still appended to the current file
    /// and the failure never propagates.
    /// </summary>
    [Fact]
    public void RenameFailure_KeepsAppending_NoEventLost()
    {
        AuditLogger.OverrideRotation(2000.0 / (1024 * 1024), keep: 2);
        AuditLogger.OverrideMove((_, _) => throw new IOException("simulated rename failure"));
        try
        {
            LogMany(100);

            Assert.False(File.Exists(RotatedPath(1)));
            var lines = File.ReadAllLines(AuditFilePath);
            Assert.Equal(100, lines.Length);
            for (var i = 0; i < 100; i++)
                Assert.Contains($"Tag_{i:D4}", lines[i]);
        }
        finally
        {
            AuditLogger.OverrideMove(null);
            AuditLogger.OverrideRotation(null);
        }
    }

    /// <summary>logix_ping reports the active rotation settings.</summary>
    [Fact]
    public void Ping_ReportsAuditMaxMbAndKeep()
    {
        AuditLogger.OverrideRotation(7, keep: 3);
        try
        {
            using var doc = JsonDocument.Parse(LogixTools.GetPingResponse(null));
            Assert.Equal(7, doc.RootElement.GetProperty("auditMaxMb").GetDouble());
            Assert.Equal(3, doc.RootElement.GetProperty("auditKeep").GetInt32());
        }
        finally { AuditLogger.OverrideRotation(null); }
    }
    // ── Controller context, ms timestamps, failure handling ────────

    [Fact]
    public async Task ConnectRecord_HasBackendGatewayGatedSource()
    {
        // CIP, no project, no allowlist: gated=false, gate_source=none.
        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip", cipConnectProbe: (_, _) => Task.CompletedTask);

        var none = ReadAllLines().Select(d => d.RootElement)
            .Single(e => e.GetProperty("event").GetString() == "connect");
        Assert.Equal("cip",           none.GetProperty("backend").GetString());
        Assert.Equal("198.51.100.10", none.GetProperty("gateway").GetString());
        Assert.Equal("1,0",           none.GetProperty("path").GetString());
        Assert.False(none.GetProperty("gated").GetBoolean());
        Assert.Equal("none",          none.GetProperty("gate_source").GetString());

        // CIP with an allowlist: gate_source=allowlist.
        File.Delete(AuditFilePath);
        var allow = Path.Combine(_tempDir, "allow2.txt");
        File.WriteAllText(allow, "Motor1_Speed\n");
        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip", capabilities: new Capabilities(false, WriteAllowlistPath: allow),
            cipConnectProbe: (_, _) => Task.CompletedTask);
        var al = ReadAllLines().Select(d => d.RootElement)
            .Single(e => e.GetProperty("event").GetString() == "connect");
        Assert.Equal("allowlist", al.GetProperty("gate_source").GetString());

        // CIP with an open project: gated, gate_source is the project path plus its SHA-256.
        File.Delete(AuditFilePath);
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");
        var projects = new ProjectService();
        projects.Load(fixture);
        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            projects, new TagSessionService(),
            backend: "cip", cipConnectProbe: (_, _) => Task.CompletedTask);
        var proj = ReadAllLines().Select(d => d.RootElement)
            .Single(e => e.GetProperty("event").GetString() == "connect");
        Assert.True(proj.GetProperty("gated").GetBoolean());
        Assert.Equal("project:" + fixture, proj.GetProperty("gate_source").GetString());
        var expectedSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant();
        Assert.Equal(expectedSha, proj.GetProperty("gate_source_sha256").GetString());
    }

    [Fact]
    public async Task WriteRecord_CarriesController()
    {
        var session = new TagSessionService();
        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), session,
            backend: "cip", cipConnectProbe: (_, _) => Task.CompletedTask);
        Assert.Equal("198.51.100.10/1,0", session.AuditController);

        AuditLogger.LogWrite("Cmd_MotorStart", true, true, true, controller: session.AuditController);
        var w = ReadAllLines().Select(d => d.RootElement)
            .Single(e => e.GetProperty("event").GetString() == "write");
        Assert.Equal("198.51.100.10/1,0", w.GetProperty("controller").GetString());
    }

    [Fact]
    public void TimestampsHaveMilliseconds()
    {
        AuditLogger.LogDisconnect();
        AuditLogger.LogWrite("Cmd_MotorStart", true, true, true);

        var lines = ReadAllLines();
        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            var ts = line.RootElement.GetProperty("ts").GetString();
            Assert.NotNull(ts);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", ts);
            Assert.True(DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _));
        }
    }

    /// <summary>Points the audit log at an unusable location: its parent "directory" is a file.</summary>
    private void BreakAuditDir()
    {
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        AuditLogger.OverrideLogDir(Path.Combine(blocker, "logs"));
    }

    [Fact]
    public async Task AppendFailure_StderrAndDoctorCount()
    {
        AuditLogger.OverrideFailureCounter(isolate: true);
        BreakAuditDir();

        var origErr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            AuditLogger.LogDisconnect();   // must not throw
        }
        finally { Console.SetError(origErr); }

        Assert.Equal(1, AuditLogger.WriteFailures);
        Assert.Contains("audit write failed:", captured.ToString(), StringComparison.Ordinal);

        var report = await DoctorHandler.ExecuteAsync(null, null, new DoctorHandlerTests.FakeEnv(), new Capabilities(LiveWritesEnabled: false));
        var audit = report.Checks.Single(c => c.Id == "audit");
        Assert.Contains("auditWriteFailures=1", audit.Observed, StringComparison.Ordinal);
        Assert.Equal("warn", audit.Status);
    }

    [Fact]
    public async Task FailClosed_RefusesWrite()
    {
        AuditLogger.OverrideFailureCounter(isolate: true);
        var store = new VirtualTagService(new Dictionary<string, (object, string?)>
            { ["Cmd_MotorStart"] = (false, "BOOL") });
        var session = new TagSessionService();
        session.Attach(store, isVirtual: true);
        // Virtual session with no gate: the write itself would otherwise succeed.

        // Default off: audit failure does not block the write.
        BreakAuditDir();
        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session));
        Assert.Equal(true, (await store.ReadAsync("Cmd_MotorStart")).Value);
        await store.WriteAsync("Cmd_MotorStart", false);

        // Fail closed + unwritable audit log: refused, nothing written.
        AuditLogger.OverrideFailClosed(true);
        try
        {
            var err = Assert.IsType<WriteTagError>(
                await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session));
            Assert.Equal("AuditUnavailable", err.Kind);
            Assert.Equal(false, (await store.ReadAsync("Cmd_MotorStart")).Value);

            // Fail closed with a working audit log: the write proceeds.
            AuditLogger.OverrideLogDir(_tempDir);
            Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session));
            Assert.Equal(true, (await store.ReadAsync("Cmd_MotorStart")).Value);
        }
        finally { AuditLogger.OverrideFailClosed(null); }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData(" yes ", true)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void FailClosed_EnvVar_UsesTruthyParser(string value, bool expected)
    {
        var previous = Environment.GetEnvironmentVariable(AuditLogger.FailClosedEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AuditLogger.FailClosedEnvVar, value);
            Assert.Equal(expected, AuditLogger.FailClosed);
        }
        finally { Environment.SetEnvironmentVariable(AuditLogger.FailClosedEnvVar, previous); }
    }

    // ── Studio lifecycle failure lines ──────────────────────

    [Theory]
    [InlineData("cancel",   "edits_cancelled")]
    [InlineData("accept",   "edits_accepted")]
    [InlineData("test",     "edits_test_confirmed")]
    [InlineData("untest",   "edits_untest_confirmed")]
    [InlineData("assemble", "edits_assembled")]
    public void LogLifecycleFailure_WritesOneErrorLine_NeverASuccessOutcome(string verb, string expectedEvent)
    {
        AuditLogger.LogLifecycleFailure(verb, "MainProgram", "MainRoutine", "StillPending", "gesture_sent", "pending", "pending");

        var doc = Assert.Single(ReadAllLines()).RootElement;
        Assert.Equal(expectedEvent, doc.GetProperty("event").GetString());
        Assert.Equal("error", doc.GetProperty("outcome").GetString());
        Assert.Equal("StillPending", doc.GetProperty("kind").GetString());
        Assert.Equal("gesture_sent", doc.GetProperty("reason").GetString());
        Assert.Equal("pending", doc.GetProperty("before_state").GetString());
        Assert.Equal("pending", doc.GetProperty("after_state").GetString());
    }

    [Fact]
    public void LogAcceptPendingEdits_SuccessLine_KeepsItsExactShape()
    {
        AuditLogger.LogAcceptPendingEdits("MainProgram", "MainRoutine", "accepted", "pending", "none");

        var doc = Assert.Single(ReadAllLines()).RootElement;
        Assert.Equal(
            new[] { "ts", "event", "program", "routine", "outcome", "before_state", "after_state" },
            doc.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("accepted", doc.GetProperty("outcome").GetString());
    }
}
