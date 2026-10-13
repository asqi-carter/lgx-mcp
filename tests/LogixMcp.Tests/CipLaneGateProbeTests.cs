namespace LogixMcp.Tests;

/// <summary>
/// Structural tests that verify the cold-install gate shell script
/// (<c>scripts/cold-install-gate.sh</c>) contains a well-formed CIP lane probe.
///
/// <para>
/// The CIP lane probe must exercise the CIP backend without a live
/// controller by:
/// <list type="number">
///   <item>
///     Calling <c>logix_connect backend='cip'</c> with an address guaranteed
///     to refuse (127.0.0.1) and asserting <c>status='connected'</c> —
///     <see cref="LibPlcTagService.ConnectAsync"/> stores the gateway/path
///     without network I/O, so this always succeeds when the handler is reachable.
///   </item>
///   <item>
///     Calling <c>logix_read</c> on a placeholder tag to trigger
///     <c>LibPlcTagNativeInit.EnsureInitialized()</c> (the single-file CIP
///     bootstrap) and an actual connection attempt.  The attempt will fail
///     because no PLC is at 127.0.0.1, but the failure must be a structured
///     <c>ReadTagError</c> tool response — never a JSON-RPC protocol error or
///     dead process.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// A JSON-RPC protocol error from <c>logix_read</c> would indicate that the
/// CIP stack threw an unhandled exception that bypassed the handler's catch-all
/// — the class of defect where every CIP operation throws
/// <see cref="NotSupportedException"/> from <c>Assembly.CodeBase</c> inside the
/// single-file binary.
/// </para>
///
/// <para>
/// These tests read the shell script source and assert ordering and content
/// properties, mirroring the approach used in
/// <see cref="ColdInstallGateInvariantTests"/> for the PowerShell gate script.
/// </para>
/// </summary>
public class CipLaneGateProbeTests
{
    // ── Script location ───────────────────────────────────────────────────────

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the project root: no *.sln file found while " +
            $"walking up from '{AppContext.BaseDirectory}'.");
    }

    private static readonly string ScriptPath = Path.Combine(
        FindProjectRoot(), "scripts", "cold-install-gate.sh");

    private static string ReadScript() => File.ReadAllText(ScriptPath);

    // ── Existence ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The shell gate script must exist at the expected path so that all
    /// subsequent structural assertions have a meaningful target.
    /// </summary>
    [Fact]
    public void ColdInstallGateSh_Exists()
    {
        Assert.True(File.Exists(ScriptPath),
            $"Expected cold-install-gate.sh at '{ScriptPath}'.");
    }

    // ── CIP probe function present ────────────────────────────────────────────

    /// <summary>
    /// The shell script must define a <c>do_logix_cip_probe</c> Python helper
    /// that implements the CIP lane probe.  Without this function the gate
    /// cannot exercise the CIP backend at all.
    /// </summary>
    [Fact]
    public void CipProbe_FunctionIsDefined()
    {
        var script = ReadScript();

        Assert.Contains(
            "def do_logix_cip_probe(",
            script,
            StringComparison.Ordinal);
    }

    // ── Step 1: logix_connect with backend='cip' ──────────────────────────────

    /// <summary>
    /// The CIP probe must call <c>logix_connect</c> with <c>backend='cip'</c>
    /// (or <c>"cip"</c>) inside <c>do_logix_cip_probe</c> so the gate verifies
    /// the connect handler is reachable and returns <c>status='connected'</c>.
    /// </summary>
    [Fact]
    public void CipProbe_CallsLogixConnect_WithCipBackend()
    {
        var script = ReadScript();

        // The function definition must appear before the call sites.
        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        // Locate 'logix_connect' within the function body.
        int connectIdx = script.IndexOf("logix_connect", funcIdx, StringComparison.Ordinal);
        Assert.True(connectIdx > funcIdx,
            "'logix_connect' not found inside do_logix_cip_probe in the gate script.");

        // The backend argument must be 'cip'.
        int cipIdx = script.IndexOf("\"cip\"", connectIdx, StringComparison.Ordinal);
        int cipIdx2 = script.IndexOf("'cip'", connectIdx, StringComparison.Ordinal);
        bool hasCipBackend = (cipIdx > connectIdx) || (cipIdx2 > connectIdx);

        Assert.True(hasCipBackend,
            "do_logix_cip_probe must pass backend='cip' (or \"cip\") to logix_connect.");
    }

    /// <summary>
    /// The CIP probe must assert that <c>logix_connect backend='cip'</c> returns
    /// <c>status='connected'</c>.  Without this check the probe would pass even
    /// if the connect handler returned a completely wrong payload.
    /// </summary>
    [Fact]
    public void CipProbe_AssertsConnectStatus_IsConnected()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        // The assertion can use either single or double quotes around "connected".
        bool hasConnectedCheck =
            script.IndexOf("\"connected\"", funcIdx, StringComparison.Ordinal) > funcIdx ||
            script.IndexOf("'connected'",  funcIdx, StringComparison.Ordinal) > funcIdx;

        Assert.True(hasConnectedCheck,
            "do_logix_cip_probe must check for status='connected' in the "
            + "logix_connect response.");
    }

    // ── Step 2: logix_read to trigger CIP stack init ──────────────────────────

    /// <summary>
    /// The CIP probe must call <c>logix_read</c> after a successful
    /// <c>logix_connect</c>.  The read attempt triggers
    /// <c>LibPlcTagNativeInit.EnsureInitialized()</c> inside the server process,
    /// which is the call that used to throw in a single-file binary.
    /// Without this step the gate would not detect CIP stack initialisation failures.
    /// </summary>
    [Fact]
    public void CipProbe_CallsLogixRead_AfterConnect()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        int connectIdx = script.IndexOf("logix_connect", funcIdx, StringComparison.Ordinal);
        Assert.True(connectIdx > funcIdx,
            "'logix_connect' not found inside do_logix_cip_probe.");

        int readIdx = script.IndexOf("logix_read", connectIdx, StringComparison.Ordinal);
        Assert.True(readIdx > connectIdx,
            "'logix_read' must appear after 'logix_connect' inside do_logix_cip_probe "
            + "so that the CIP stack is exercised after the session is established.");
    }

    // ── JSON-RPC error guard ──────────────────────────────────────────────────

    /// <summary>
    /// The CIP probe must check <c>resp.get("error")</c> on the <c>logix_read</c>
    /// response and return <c>False</c> (gate failure) when a JSON-RPC protocol
    /// error is received.  A protocol-level error indicates the CIP stack threw
    /// an unhandled exception that bypassed the handler's catch-all — the exact
    /// defect.
    /// </summary>
    [Fact]
    public void CipProbe_FailsGate_OnJsonRpcProtocolError()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        // The function must inspect resp.get("error") at least once after the
        // logix_read call.
        int readIdx = script.IndexOf("logix_read", funcIdx, StringComparison.Ordinal);
        Assert.True(readIdx > funcIdx,
            "'logix_read' not found inside do_logix_cip_probe.");

        int errorCheckIdx = script.IndexOf("resp.get(\"error\")", readIdx, StringComparison.Ordinal);
        int errorCheckIdx2 = script.IndexOf("resp.get('error')", readIdx, StringComparison.Ordinal);
        bool hasErrorCheck = (errorCheckIdx > readIdx) || (errorCheckIdx2 > readIdx);

        Assert.True(hasErrorCheck,
            "do_logix_cip_probe must check resp.get(\"error\") after the logix_read "
            + "call so that JSON-RPC protocol errors (CIP stack init failures) are "
            + "detected and cause a gate failure.");
    }

    /// <summary>
    /// When the <c>logix_read</c> response carries a JSON-RPC protocol error,
    /// the CIP probe must return <c>False</c> — not continue and silently pass.
    /// Verifies that the error guard is followed by <c>return False</c>.
    /// </summary>
    [Fact]
    public void CipProbe_ReturnsFalse_AfterJsonRpcErrorFromRead()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        int readIdx = script.IndexOf("logix_read", funcIdx, StringComparison.Ordinal);
        Assert.True(readIdx > funcIdx, "'logix_read' not found inside do_logix_cip_probe.");

        // After the logix_read call there must be at least one 'return False'.
        int returnFalseIdx = script.IndexOf("return False", readIdx, StringComparison.Ordinal);
        Assert.True(returnFalseIdx > readIdx,
            "'return False' must appear after the logix_read call inside "
            + "do_logix_cip_probe so that a JSON-RPC error causes the gate to fail.");
    }

    // ── Process-alive checks ──────────────────────────────────────────────────

    /// <summary>
    /// The CIP probe must check <c>proc.poll()</c> to verify the server process
    /// is still alive both before the probe starts and after each tool call.
    /// A dead process is never a passing outcome — it would indicate the CIP
    /// stack caused a fatal crash.
    /// </summary>
    [Fact]
    public void CipProbe_ChecksProcessAlive_WithPollCall()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        // proc.poll() must appear at least once inside the function body.
        int pollIdx = script.IndexOf("proc.poll()", funcIdx, StringComparison.Ordinal);
        Assert.True(pollIdx > funcIdx,
            "'proc.poll()' must appear inside do_logix_cip_probe to check that "
            + "the server process is still alive after CIP operations.");
    }

    // ── Probe is called for all builds ────────────────────────────────────────

    /// <summary>
    /// <c>do_logix_cip_probe</c> must be called in the Scenario A probe
    /// sequence so that CIP initialisation is verified in every gate run,
    /// regardless of whether it is an SDK build or a plain build.
    /// </summary>
    [Fact]
    public void CipProbe_IsCalledInScenarioA()
    {
        var script = ReadScript();

        // The function definition comes first.
        int funcDefIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcDefIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        // A call site (not the definition) must exist after the definition.
        int callIdx = script.IndexOf("do_logix_cip_probe(", funcDefIdx + 1, StringComparison.Ordinal);
        Assert.True(callIdx > funcDefIdx,
            "'do_logix_cip_probe(' call site not found after the function definition. "
            + "The probe must be invoked in the Scenario A probe sequence.");
    }

    /// <summary>
    /// <c>do_logix_cip_probe</c> must be called for non-SDK builds (the
    /// <c>else</c> branch when no adapter DLL is present) as well as for SDK
    /// builds.  The CIP backend is the primary non-SDK backend and must always
    /// be verified.
    /// </summary>
    [Fact]
    public void CipProbe_IsCalledForNonSdkBuilds()
    {
        var script = ReadScript();

        int funcDefIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcDefIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        // The call must appear after the 'else:' branch that handles non-SDK builds.
        // We look for 'do_logix_cip_probe(' after 'else:' in the Scenario A section.
        int elseIdx = script.IndexOf("else:", funcDefIdx, StringComparison.Ordinal);
        Assert.True(elseIdx > funcDefIdx,
            "'else:' branch not found after do_logix_cip_probe definition — the "
            + "non-SDK probe path is missing.");

        int callInElseIdx = script.IndexOf("do_logix_cip_probe(", elseIdx, StringComparison.Ordinal);
        Assert.True(callInElseIdx > elseIdx,
            "'do_logix_cip_probe(' not found inside the 'else:' branch — the probe "
            + "must also run for non-SDK builds.");
    }

    // ── Structured response assertion ─────────────────────────────────────────

    /// <summary>
    /// The CIP probe must verify that the <c>logix_read</c> response is a
    /// structured payload (carries an <c>"error"</c> or <c>"value"</c> field)
    /// and not an unrecognised blob.  This ensures any future regression where
    /// the handler stops returning structured results is caught by the gate.
    /// </summary>
    [Fact]
    public void CipProbe_AssertsThatReadResponseIsStructured()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        int readIdx = script.IndexOf("logix_read", funcIdx, StringComparison.Ordinal);
        Assert.True(readIdx > funcIdx, "'logix_read' not found inside do_logix_cip_probe.");

        // The probe must check for either "error" (ReadTagError) or "value"
        // (ReadTagResult) in the response payload.
        bool checksErrorField =
            script.IndexOf("\"error\"", readIdx, StringComparison.Ordinal) > readIdx ||
            script.IndexOf("'error'",  readIdx, StringComparison.Ordinal) > readIdx;
        bool checksValueField =
            script.IndexOf("\"value\"", readIdx, StringComparison.Ordinal) > readIdx ||
            script.IndexOf("'value'",  readIdx, StringComparison.Ordinal) > readIdx;

        Assert.True(checksErrorField || checksValueField,
            "do_logix_cip_probe must check the logix_read response for an 'error' "
            + "or 'value' field to confirm the response is structured (not an "
            + "unrecognised blob).");
    }

    // ── Skip without an endpoint ──────────────────────────────────────────────

    /// <summary>
    /// With no CIP endpoint configured (<c>LOGIX_CIP_GATEWAY</c> unset) the probe
    /// must print a SKIP message and return before sending any request, so it
    /// cannot time out against a loopback address nothing listens on.
    /// </summary>
    [Fact]
    public void CipProbe_SkipsCleanlyWhenNoEndpointConfigured()
    {
        var script = ReadScript();

        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0, "'def do_logix_cip_probe(' not found in gate script.");

        int envIdx = script.IndexOf("LOGIX_CIP_GATEWAY\", \"\")", funcIdx, StringComparison.Ordinal);
        Assert.True(envIdx > funcIdx, "do_logix_cip_probe must read LOGIX_CIP_GATEWAY.");

        int skipIdx = script.IndexOf("SKIP", envIdx, StringComparison.Ordinal);
        int firstSendIdx = script.IndexOf("send(proc", envIdx, StringComparison.Ordinal);
        Assert.True(skipIdx > envIdx && skipIdx < firstSendIdx,
            "The SKIP message must be emitted before the first request is sent.");

        Assert.DoesNotContain("\"gateway\": \"127.0.0.1\", \"path\": \"1,0\", \"backend\": \"cip\"", script);
    }

    // ── No live controller needed ───────────────────────────────────

    /// <summary>
    /// Step 1 passes on a structured <c>ConnectionFailed</c> result that carries no
    /// controller, so the gate does not need a live controller.
    /// </summary>
    [Fact]
    public void CipProbe_Step1_AcceptsStructuredConnectionFailed()
    {
        var script = ReadScript();
        int funcIdx = script.IndexOf("def do_logix_cip_probe(", StringComparison.Ordinal);
        int okIdx = script.IndexOf("\"ConnectionFailed\" and \"controller\" not in payload", funcIdx, StringComparison.Ordinal);
        int readIdx = script.IndexOf("\"name\": \"logix_read\"", funcIdx, StringComparison.Ordinal);
        Assert.True(okIdx > funcIdx && okIdx < readIdx,
            "Step 1 must accept kind='ConnectionFailed' with no controller before logix_read is sent.");
    }

    /// <summary>
    /// The gate decides the SDK build the way Directory.Build.props does: no
    /// <c>.localfeed</c> scan and no "same glob" claim; the timeout text names 25 s only.
    /// </summary>
    [Fact]
    public void GateScript_NoLocalfeedDetection_AndConsistentTimeout()
    {
        var script = ReadScript();
        Assert.Contains("PROBE_TIMEOUT_SECS=25", script, StringComparison.Ordinal);
        Assert.DoesNotContain("_LOCAL_FEED", script, StringComparison.Ordinal);
        Assert.DoesNotContain("same glob", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-detect from .localfeed", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("20 seconds", script, StringComparison.Ordinal);
        Assert.DoesNotContain("FTSP", script, StringComparison.Ordinal);
    }
}
