using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Guard tests that enforce two invariants which must survive the insertion of
/// <c>logix_open_project</c> into the cold-install gate probe sequence:
///
/// <para>
/// (a) <b>Unprovisioned-rejection</b>: when the publish tree is detected as an
///     SDK build but the vendor directory (<c>sdk\vendor\</c>) has not been
///     provisioned (i.e. <c>CSClient.dll</c> is absent), the PowerShell gate
///     must exit non-zero AND name <c>CSClient.dll</c> in its output.  This
///     early-exit happens <em>before</em> the probe sequence starts and must
///     not be displaced by the new <c>logix_open_project</c> step.
/// </para>
///
/// <para>
/// (b) <b>logix_ping gate</b>: the probe sequence must call <c>logix_ping</c>
///     and require <c>status="ok"</c> before proceeding to
///     <c>logix_open_project</c> or <c>logix_connect</c>.  A ping failure
///     must cause the gate to exit non-zero without calling the later steps.
/// </para>
///
/// <para>
/// Script-structure tests read <c>bootstrap/cold-install-gate.ps1</c> from the
/// project root (located by walking up from <see cref="AppContext.BaseDirectory"/>
/// until a <c>.sln</c> file is found) and assert ordering and content properties.
/// C#-handler tests exercise <see cref="PingHandler"/> directly.
/// </para>
/// </summary>
public class ColdInstallGateInvariantTests
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
        FindProjectRoot(), "bootstrap", "cold-install-gate.ps1");

    private static string ReadScript() => File.ReadAllText(ScriptPath);

    // ── Timeout parameter invariants ──────────────────────────────────────────

    /// <summary>
    /// The <c>-TimeoutSeconds</c> parameter must default to 120 seconds so that
    /// a cold FactoryTalk Security (FTSP) token acquisition -- which can take
    /// up to a minute or more on a fresh install -- has enough headroom to
    /// complete without producing a false gate failure.
    /// </summary>
    [Fact]
    public void ColdInstallGate_TimeoutSecondsDefault_Is120()
    {
        var script = ReadScript();

        Assert.Contains(
            "[int] $TimeoutSeconds = 120",
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe-timeout failure message must name <c>FactoryTalk Security</c>
    /// so that operators seeing a timeout on a fresh install understand the
    /// likely root cause (slow first-time FTSP token acquisition) instead of
    /// assuming the MCP server itself is defective.
    /// </summary>
    [Fact]
    public void ColdInstallGate_TimeoutMessage_NamesFactoryTalkSecurity()
    {
        var script = ReadScript();

        Assert.Contains(
            "FactoryTalk Security",
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The doc-comment examples must honour the 60 s minimum and not mention FTSP.
    /// </summary>
    [Fact]
    public void ColdInstallGate_Examples_HonourMinimumTimeout_AndNameNoFtsp()
    {
        var script = ReadScript();
        int end = script.IndexOf("#>", StringComparison.Ordinal);
        var header = script.Substring(0, end);
        Assert.DoesNotContain("FTSP", header, StringComparison.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(header, @"-TimeoutSeconds\s+(\d+)"))
            Assert.True(int.Parse(m.Groups[1].Value) >= 60, "Example timeout below 60 s: " + m.Value);
    }

    // ── Self-identifying first line invariants ────────────────────────────────

    /// <summary>
    /// The gate script's very first line of output must print the script's own
    /// resolved path (<c>$PSCommandPath</c> or equivalent) together with the
    /// <c>TimeoutSeconds</c> value currently in effect, so that any captured
    /// log is unambiguous about which copy of the script executed and which
    /// timeout was active.
    /// </summary>
    [Fact]
    public void ColdInstallGate_PrintsSelfIdentifyingFirstLine_WithResolvedPathAndTimeout()
    {
        var script = ReadScript();

        Assert.Contains(
            "cold-install-gate.ps1: running from $ResolvedScriptPath, TimeoutSeconds=$TimeoutSeconds",
            script,
            StringComparison.Ordinal);

        // The resolved path must be derived from $PSCommandPath (the script's
        // own path), not a hardcoded or unrelated value.
        Assert.Contains(
            "$PSCommandPath",
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The self-identifying line must be emitted before any other gate output
    /// (e.g. the "==> cold-install gate" banner) so it is unambiguously the
    /// first line of the log.
    /// </summary>
    [Fact]
    public void ColdInstallGate_SelfIdentifyingLine_PrecedesOtherOutput()
    {
        var script = ReadScript();

        int selfIdIdx = script.IndexOf(
            "cold-install-gate.ps1: running from", StringComparison.Ordinal);
        Assert.True(selfIdIdx >= 0,
            "Self-identifying first-line output not found in gate script.");

        int bannerIdx = script.IndexOf(
            "==> cold-install gate (PowerShell)", StringComparison.Ordinal);
        Assert.True(bannerIdx >= 0,
            "'==> cold-install gate (PowerShell)' banner not found in gate script.");

        Assert.True(
            selfIdIdx < bannerIdx,
            $"Self-identifying line (at char {selfIdIdx}) must precede the " +
            $"'==> cold-install gate (PowerShell)' banner (at char {bannerIdx}) " +
            "so it is the first line of gate output.");
    }

    // ── (a) Unprovisioned-rejection invariants ────────────────────────────────

    /// <summary>
    /// The PowerShell gate script must exist at the expected path so that all
    /// subsequent structural assertions have a meaningful target.
    /// </summary>
    [Fact]
    public void ColdInstallGatePsScript_Exists()
    {
        Assert.True(File.Exists(ScriptPath),
            $"Expected cold-install-gate.ps1 at '{ScriptPath}'.");
    }

    /// <summary>
    /// The unprovisioned-rejection block must reference the exact CSClient DLL
    /// filename so users know which file they need to provide.
    /// This mirrors the C# constant <see cref="SdkAdapterLoader.CsClientDllName"/>.
    /// </summary>
    [Fact]
    public void ColdInstallGate_UnprovisionedRejection_NamesCSClientDll()
    {
        var script = ReadScript();

        Assert.Contains(
            SdkAdapterLoader.CsClientDllName,
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The unprovisioned-rejection block must cause the script to exit with a
    /// non-zero exit code.  A gate that logs the problem but continues is not
    /// a gate.
    /// </summary>
    [Fact]
    public void ColdInstallGate_UnprovisionedRejection_ExitsNonZero()
    {
        var script = ReadScript();

        // The unprovisioned check must contain a hard 'exit 1' (PowerShell
        // sets the process exit code; Write-Error alone is not sufficient).
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unprovisioned-rejection block must appear <em>before</em> the probe
    /// sequence so that a gate run against an unprovisioned tree never reaches
    /// <c>logix_ping</c>, <c>logix_open_project</c>, or <c>logix_connect</c>.
    /// Insertion of <c>logix_open_project</c> must not push the CSClient check
    /// past the start of the probe.
    /// </summary>
    [Fact]
    public void ColdInstallGate_UnprovisionedRejection_PrecedesProbeSequence()
    {
        var script = ReadScript();

        // First occurrence of the CSClient filename (in the rejection block).
        int csClientIdx = script.IndexOf(SdkAdapterLoader.CsClientDllName,
            StringComparison.Ordinal);
        Assert.True(csClientIdx >= 0,
            $"'{SdkAdapterLoader.CsClientDllName}' not found in the gate script.");

        // First invocation of the logix_ping helper — marks the start of the probe.
        int pingCallIdx = script.IndexOf("Invoke-LogixPing", StringComparison.Ordinal);
        Assert.True(pingCallIdx >= 0,
            "'Invoke-LogixPing' not found in the gate script.");

        Assert.True(
            csClientIdx < pingCallIdx,
            $"Unprovisioned-rejection (CSClient.dll check at char {csClientIdx}) " +
            $"must appear before the probe sequence (Invoke-LogixPing at char {pingCallIdx}). " +
            "Insertion of logix_open_project must not have displaced this early-exit path.");
    }

    // ── (b) logix_ping gate invariants ───────────────────────────────────────

    /// <summary>
    /// The gate script must contain a logix_ping call that checks the
    /// <c>status</c> field for the value <c>'ok'</c>.  Changing the expected
    /// value (e.g. to accept any non-null status) would break the contract
    /// that the gate enforces server liveness.
    /// </summary>
    [Fact]
    public void ColdInstallGate_LogixPing_GatesOnStatusOk()
    {
        var script = ReadScript();

        // The Invoke-LogixPing function must reference 'ok' as the required
        // status string.  Both single-quoted and double-quoted forms are accepted.
        bool hasOkCheck =
            script.Contains("'ok'",  StringComparison.Ordinal) ||
            script.Contains("\"ok\"", StringComparison.Ordinal);

        Assert.True(hasOkCheck,
            "Gate script Invoke-LogixPing must check for status value 'ok'.");
    }

    /// <summary>
    /// <c>logix_ping</c> must be called before <c>logix_open_project</c> in the
    /// probe sequence so that a ping failure exits non-zero without ever
    /// reaching the open-project step.
    /// </summary>
    [Fact]
    public void ColdInstallGate_LogixPing_PrecedesLogixOpenProject_InProbeSequence()
    {
        var script = ReadScript();

        // Last call site of each helper (the probe sequence at the bottom of
        // the script, not the function definition earlier in the file).
        int pingCallIdx =
            script.LastIndexOf("Invoke-LogixPing", StringComparison.Ordinal);
        int openProjIdx =
            script.LastIndexOf("Invoke-LogixOpenProject", StringComparison.Ordinal);

        Assert.True(pingCallIdx >= 0,
            "'Invoke-LogixPing' (call site) not found in the gate script.");
        Assert.True(openProjIdx >= 0,
            "'Invoke-LogixOpenProject' (call site) not found in the gate script.");

        Assert.True(
            pingCallIdx < openProjIdx,
            $"'Invoke-LogixPing' (at char {pingCallIdx}) must precede " +
            $"'Invoke-LogixOpenProject' (at char {openProjIdx}) so that a " +
            "ping failure exits non-zero before logix_open_project is attempted.");
    }

    /// <summary>
    /// The probe sequence must use conditional short-circuit logic
    /// (<c>elseif</c>) so that <c>logix_open_project</c> is never called when
    /// <c>logix_ping</c> fails.
    /// </summary>
    [Fact]
    public void ColdInstallGate_ProbeSequence_UsesElseifChain()
    {
        var script = ReadScript();

        // The probe body must contain 'elseif' to implement short-circuit:
        // ping failure must prevent open-project from being called.
        Assert.Contains("elseif", script, StringComparison.OrdinalIgnoreCase);
    }

    // ── (c) Version-detection invariants (gate-probe behaviours after earlier tasks) ───

    /// <summary>
    /// The version-detection block must appear in the script AFTER the
    /// unprovisioned-rejection block so that an SDK publish tree without a
    /// provisioned vendor directory still exits non-zero (naming
    /// <c>CSClient.dll</c>) before the gate attempts to resolve the installed
    /// Logix Designer version.
    /// </summary>
    [Fact]
    public void ColdInstallGate_VersionDetection_AppearsAfterUnprovisionedRejection()
    {
        var script = ReadScript();

        // Locate the unprovisioned-rejection guard (the if-block that
        // checks both isSdkBuild and the absence of isProvisioned).
        int unprovisionedIdx = script.IndexOf(
            "$isSdkBuild -and -not $isProvisioned", StringComparison.Ordinal);
        Assert.True(unprovisionedIdx >= 0,
            "'$isSdkBuild -and -not $isProvisioned' guard not found in gate " +
            "script.  The unprovisioned-rejection check must be present.");

        // Locate the start of the version-detection block (null initialisation
        // signals that no version has been resolved yet; an empty default would
        // hide a missing installation instead of failing explicitly).
        int versionInitIdx = script.IndexOf(
            "$logixSoftwareRevision = $null", StringComparison.Ordinal);
        Assert.True(versionInitIdx >= 0,
            "'$logixSoftwareRevision = $null' not found in gate script.  " +
            "The version-detection block must initialise the variable to null " +
            "so missing installations are detected explicitly.");

        Assert.True(
            unprovisionedIdx < versionInitIdx,
            $"Unprovisioned-rejection guard (at char {unprovisionedIdx}) must " +
            $"precede the version-detection block (at char {versionInitIdx}).  " +
            "Moving version detection before the unprovisioned check would " +
            "cause a machine without Logix Designer to hit the version error " +
            "instead of the expected CSClient.dll error on an unprovisioned tree.");
    }

    /// <summary>
    /// When neither detection source (install-dir subdirectory nor
    /// <c>LogixDesigner.exe</c> FileVersion) resolves the Logix Designer
    /// version, the gate must exit non-zero immediately with a clear
    /// diagnostic — not continue and fail with a confusing SDK activation error.
    /// </summary>
    [Fact]
    public void ColdInstallGate_VersionDetection_ExitsNonZeroWhenVersionNotFound()
    {
        var script = ReadScript();

        // Version resolution is done by Resolve-LogixDesignerVersion, which throws
        // 'LogixDesignerNotInstalled' when nothing is found.
        Assert.Contains("LogixDesignerNotInstalled", script, StringComparison.Ordinal);

        // The catch block around the resolver call must 'exit 1' so the process
        // stops with a clear error when Logix Designer is not installed (SDK builds).
        int callIdx = script.IndexOf(
            "Resolve-LogixDesignerVersion -InstallRoot", StringComparison.Ordinal);
        Assert.True(callIdx >= 0, "Resolver call site not found.");
        int catchIdx = script.IndexOf("} catch {", callIdx, StringComparison.Ordinal);
        Assert.True(catchIdx > callIdx, "catch block must follow the resolver call.");
        int exitIdx = script.IndexOf("exit 1", catchIdx, StringComparison.Ordinal);

        Assert.True(
            exitIdx > catchIdx,
            $"'exit 1' must follow the resolver's catch block (at char {catchIdx}) " +
            "so the gate stops with a clear error when Logix Designer is not installed.");
    }

    /// <summary>
    /// The minimal L5X here-string written to the temp directory must
    /// reference <c>$logixSoftwareRevision</c> (resolved at runtime) so the
    /// probe L5X carries the correct revision for the installed Logix Designer,
    /// not a hardcoded constant that mismatches later releases.
    /// </summary>
    [Fact]
    public void ColdInstallGate_L5XTemplate_UsesRuntimeSoftwareRevision()
    {
        var script = ReadScript();

        // Locate the double-quoted here-string that builds the minimal L5X.
        int templateStart = script.IndexOf("$minimalL5X = @\"", StringComparison.Ordinal);
        Assert.True(templateStart >= 0,
            "'$minimalL5X = @\"' (double-quoted here-string) not found in " +
            "gate script.  The L5X template must use a double-quoted here-string " +
            "so that variable references are interpolated.");

        int templateEnd = script.IndexOf("\"@", templateStart + 1, StringComparison.Ordinal);
        Assert.True(templateEnd > templateStart,
            "Closing '\"@' for the L5X here-string not found after the opening " +
            "'$minimalL5X = @\"'.");

        string template = script.Substring(templateStart, templateEnd - templateStart);

        // Template must embed the runtime variable, not a hardcoded version.
        Assert.Contains(
            "$logixSoftwareRevision", template, StringComparison.Ordinal);

        // No hardcoded SoftwareRevision="36.1" may remain inside the template —
        // that would defeat runtime version detection.
        Assert.DoesNotContain(
            "SoftwareRevision=\"36.1\"", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// The temp L5X file written for the <c>logix_open_project</c> probe must
    /// be deleted inside a <c>finally</c> block so cleanup always happens,
    /// whether the probe succeeds, fails with an exit-code, or throws.
    /// </summary>
    [Fact]
    public void ColdInstallGate_TempL5X_DeletedInFinallyBlock()
    {
        var script = ReadScript();

        // The try/catch/finally that wraps the MCP probe sequence.
        int finallyIdx = script.IndexOf("} finally {", StringComparison.Ordinal);
        Assert.True(finallyIdx >= 0,
            "'} finally {' not found in gate script.  The probe sequence must " +
            "be wrapped in a try/finally so cleanup always runs.");

        // Remove-Item for the temp file must appear after 'finally {' (inside it).
        int removeIdx = script.IndexOf("Remove-Item", finallyIdx, StringComparison.Ordinal);
        Assert.True(
            removeIdx > finallyIdx,
            $"'Remove-Item' for temp L5X cleanup not found after '}} finally {{' " +
            $"(at char {finallyIdx}).  Temp L5X must be removed inside the finally block.");

        // Confirm the Remove-Item line references the temp-file variable.
        int tempVarIdx = script.IndexOf("$tempL5X", removeIdx, StringComparison.Ordinal);
        Assert.True(
            tempVarIdx > removeIdx,
            $"'$tempL5X' not found after 'Remove-Item' (at char {removeIdx}).  " +
            "The cleanup statement must reference the temp L5X variable so the " +
            "correct file is deleted.");
    }

    // ── C# handler: PingHandler.Execute() always returns status='ok' ─────────

    /// <summary>
    /// <see cref="PingHandler.Execute"/> must always return a response whose
    /// <c>Status</c> field is exactly <c>"ok"</c>.  The gate script requires
    /// this value; any other value causes the probe to fail and the gate to
    /// exit non-zero.
    /// </summary>
    [Fact]
    public void PingHandler_Execute_StatusIsAlwaysOk()
    {
        PingResponse response = PingHandler.Execute();

        Assert.Equal("ok", response.Status);
    }

    /// <summary>
    /// The JSON-serialized form of <see cref="PingHandler.Execute()"/> must
    /// carry <c>status="ok"</c> so that the gate script's JSON parsing logic
    /// (which reads the <c>content[0].text</c> field from the MCP tool response)
    /// finds the expected value.
    /// </summary>
    [Fact]
    public void PingHandler_Execute_SerializedJson_StatusIsOk()
    {
        PingResponse response = PingHandler.Execute();
        string json = JsonSerializer.Serialize(response);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// <see cref="PingHandler.Execute()"/> must always return <c>"ok"</c> on
    /// repeated calls — the status must be unconditional, never dependent on
    /// server state or call count.
    /// </summary>
    [Fact]
    public void PingHandler_Execute_StatusIsOk_OnRepeatedCalls()
    {
        for (int i = 0; i < 5; i++)
        {
            PingResponse response = PingHandler.Execute();
            Assert.Equal("ok", response.Status);
        }
    }
}
