using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Source-scan tests for <c>bootstrap/cold-install-gate.ps1</c> version resolution:
/// no-SDK builds must not require Studio 5000; SDK builds still must.
/// </summary>
public class ColdGateScriptTests
{
    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No *.sln found above " + AppContext.BaseDirectory);
    }

    private static string ReadScript() =>
        File.ReadAllText(Path.Combine(FindProjectRoot(), "bootstrap", "cold-install-gate.ps1"));

    [Fact]
    public void NoSdkBuild_DoesNotRequireLogixDesignerVersion()
    {
        var script = ReadScript();

        // No-SDK fallback: empty version AND not an SDK build -> fixed 38.11.
        var m = Regex.Match(script,
            @"IsNullOrEmpty\(\$logixSoftwareRevision\)\s*-and\s*-not\s*\$isSdkBuild\)\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline);
        Assert.True(m.Success, "No-SDK fallback guard not found in gate script.");
        Assert.Contains("'38.11'", m.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("fixed (no-SDK build; Studio not required)", m.Groups["body"].Value, StringComparison.Ordinal);

        // The printed line format yields 'logix ver : 38.11  # fixed (...)'.
        Assert.Contains("logix ver : $logixSoftwareRevision  # $logixVersionSource", script, StringComparison.Ordinal);

        // The explicit error text lives in Resolve-LogixDesignerVersion; the catch
        // block around its call site exits 1 for SDK builds (no fallback there).
        int fallbackIdx = m.Index;
        int funcIdx = script.IndexOf("function Resolve-LogixDesignerVersion", StringComparison.Ordinal);
        int errorIdx = script.IndexOf("Logix Designer version could not be determined", StringComparison.Ordinal);
        Assert.True(funcIdx >= 0 && errorIdx > funcIdx, "Explicit error must remain inside Resolve-LogixDesignerVersion.");

        int callIdx = script.IndexOf("Resolve-LogixDesignerVersion -InstallRoot", StringComparison.Ordinal);
        int catchIdx = script.IndexOf("} catch {", callIdx, StringComparison.Ordinal);
        int sdkExitIdx = script.IndexOf("exit 1", catchIdx, StringComparison.Ordinal);
        Assert.True(callIdx > funcIdx && catchIdx > callIdx && sdkExitIdx > catchIdx,
            "SDK-build failure path (catch block with 'exit 1') must follow the resolver call.");
        Assert.True(sdkExitIdx < fallbackIdx,
            "SDK-build 'exit 1' in the catch block must precede the no-SDK fallback.");
    }

    [Fact]
    public void Setup_NoSdkBuild_SkipsProvision()
    {
        var script = File.ReadAllText(Path.Combine(FindProjectRoot(), "bootstrap", "setup.ps1"));

        // -NoSdk switch exists.
        Assert.Matches(@"\[switch\]\s*\$NoSdk", script);

        // Detection keys on the adapter dll; -NoSdk forces the skip path.
        Assert.Contains(@"sdk\LogixMcp.SdkAdapter.dll", script, StringComparison.Ordinal);
        Assert.Matches(@"\$skipProvision\s*=\s*\$NoSdk\s*-or\s*\(-not\s*\$sdkPresent\)", script);

        // Exact skip message.
        const string msg = "SDK lane: not present (no-SDK build); skipping provision-sdk";
        Assert.Contains(msg, script, StringComparison.Ordinal);

        // Provision invocation lives only in the non-skip branch, before the gate.
        int skipIdx = script.IndexOf("if ($skipProvision) {", StringComparison.Ordinal);
        int provIdx = script.IndexOf("& $provisionScript @provisionArgs", StringComparison.Ordinal);
        int gateIdx = script.IndexOf("& $gateScript @gateArgs", StringComparison.Ordinal);
        Assert.True(skipIdx >= 0 && provIdx > skipIdx, "provision-sdk must be inside the skip-guarded block.");
        Assert.True(gateIdx > provIdx, "Cold-install gate must still run after step 1.");
        Assert.Contains("} else {", script.Substring(skipIdx, provIdx - skipIdx), StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_NoSdkLayout_DoesNotRequireProvisionScript()
    {
        var script = File.ReadAllText(Path.Combine(FindProjectRoot(), "bootstrap", "setup.ps1"));

        // No unconditional loop over both scripts any more.
        Assert.DoesNotMatch(@"foreach\s*\(\$script\s+in\s+@\(\$provisionScript", script);

        // Gate script existence is still required up front, before the SDK lane decision.
        int gateCheckIdx = script.IndexOf("Test-Path -LiteralPath $gateScript", StringComparison.Ordinal);
        int skipDeclIdx = script.IndexOf("$skipProvision =", StringComparison.Ordinal);
        Assert.True(gateCheckIdx >= 0 && gateCheckIdx < skipDeclIdx,
            "cold-install-gate.ps1 must be checked unconditionally.");

        // provision-sdk.ps1 existence check lives only in the non-skip (else) branch.
        int checkIdx = script.IndexOf("Test-Path -LiteralPath $provisionScript", StringComparison.Ordinal);
        int skipIdx = script.IndexOf("if ($skipProvision) {", StringComparison.Ordinal);
        int elseIdx = script.IndexOf("} else {", skipIdx, StringComparison.Ordinal);
        int provIdx = script.IndexOf("& $provisionScript @provisionArgs", StringComparison.Ordinal);
        Assert.True(checkIdx > elseIdx && checkIdx < provIdx,
            "provision-sdk.ps1 must only be required on the SDK path.");
        Assert.Equal(checkIdx, script.LastIndexOf("Test-Path -LiteralPath $provisionScript", StringComparison.Ordinal));
    }

    /// <summary>
    /// A no-SDK install (even run as administrator) must not query or change any Rockwell
    /// service: the <c>sc.exe failure LdSdkService</c> call is reachable only on the SDK path,
    /// the no-SDK gate timeout message names no-SDK causes, and the README does not
    /// claim the gate calls <c>logix_connect</c>.
    /// </summary>
    [Fact]
    public void Setup_NoSdk_TouchesNoRockwellService()
    {
        var root = FindProjectRoot();
        var script = File.ReadAllText(Path.Combine(root, "bootstrap", "setup.ps1"));

        // Step 3 branch order: -SkipServiceRecovery, then the no-SDK skip, then the SDK path.
        int step3Idx = script.IndexOf("if ($SkipServiceRecovery) {", StringComparison.Ordinal);
        int noSdkIdx = script.IndexOf("} elseif ($skipProvision) {", step3Idx, StringComparison.Ordinal);
        int sdkElseIdx = script.IndexOf("} else {", noSdkIdx, StringComparison.Ordinal);
        Assert.True(step3Idx >= 0 && noSdkIdx > step3Idx, "No-SDK skip branch must guard service recovery.");
        Assert.True(sdkElseIdx > noSdkIdx);

        // The no-SDK branch itself touches no service.
        var noSdkBranch = script.Substring(noSdkIdx, sdkElseIdx - noSdkIdx);
        Assert.DoesNotContain("sc.exe", noSdkBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("$ServiceName", noSdkBranch, StringComparison.Ordinal);

        // Every sc.exe invocation (qfailure and failure) lives in the SDK-only else branch.
        var invocations = Regex.Matches(script, @"&\s*sc\.exe\b");
        Assert.NotEmpty(invocations);
        foreach (Match m in invocations)
            Assert.True(m.Index > sdkElseIdx, "sc.exe call at offset " + m.Index + " is reachable on the no-SDK path.");
        Assert.Matches(@"&\s*sc\.exe\s+failure\s+\$ServiceName", script);

        // The no-SDK gate timeout message names no-SDK causes, not FTSP/LdSdkService.
        var gate = File.ReadAllText(Path.Combine(root, "bootstrap", "cold-install-gate.ps1"));
        int sdkMsgIdx = gate.IndexOf("if ($isSdkBuild) {", gate.IndexOf("server may have stalled", StringComparison.Ordinal), StringComparison.Ordinal);
        int noSdkMsgIdx = gate.IndexOf("} else {", sdkMsgIdx, StringComparison.Ordinal);
        int msgEnd = gate.IndexOf("return $null", noSdkMsgIdx, StringComparison.Ordinal);
        Assert.True(sdkMsgIdx >= 0 && noSdkMsgIdx > sdkMsgIdx && msgEnd > noSdkMsgIdx);
        var sdkMsg = gate.Substring(sdkMsgIdx, noSdkMsgIdx - sdkMsgIdx);
        var noSdkMsg = gate.Substring(noSdkMsgIdx, msgEnd - noSdkMsgIdx);
        Assert.Contains("FTSP", sdkMsg, StringComparison.Ordinal);
        Assert.Contains("no-SDK build", noSdkMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("FTSP", noSdkMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("LdSdkService", noSdkMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("FactoryTalk", noSdkMsg, StringComparison.Ordinal);

        // The README no longer claims the gate calls logix_connect.
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        int gateIdx = readme.IndexOf("runs a cold-install gate", StringComparison.Ordinal);
        Assert.True(gateIdx >= 0);
        var para = readme.Substring(gateIdx, readme.IndexOf("\n\n", gateIdx, StringComparison.Ordinal) - gateIdx);
        Assert.Contains("logix_open_project", para, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"→\s*`logix_connect`", para);
    }

    private static readonly string[] GateScripts =
    {
        Path.Combine("bootstrap", "cold-install-gate.ps1"),
        Path.Combine("scripts", "cold-install-gate.sh"),
    };

    private static string[] MakeTargetsMentioned(string text) =>
        Regex.Matches(text, @"\bmake\s+([A-Za-z0-9_][A-Za-z0-9_.-]*)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

    /// <summary>
    /// Every <c>make &lt;target&gt;</c> named in the cold-install gate scripts must exist in the
    /// Makefile, (this test also runs in the public export, whose Makefile has no SDK-lane targets).
    /// </summary>
    [Fact]
    public void GateScripts_MakeTargets_ExistInMakefile()
    {
        var root = FindProjectRoot();
        var makefile = File.ReadAllText(Path.Combine(root, "Makefile"));


        foreach (var rel in GateScripts)
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            var targets = MakeTargetsMentioned(text);
            Assert.NotEmpty(targets);
            foreach (var t in targets)
            {
                Assert.Matches($@"(?m)^{Regex.Escape(t)}:", makefile);
            }
        }
    }

    /// <summary>
    /// The gate scripts ship in the no-SDK export, so they must not point at the SDK-lane
    /// <c>publish-win</c>/<c>publish-linux</c> targets or their <c>publish/</c> folders.
    /// </summary>
    [Fact]
    public void GateScripts_DoNotReferenceSdkPublishTargetsOrFolders()
    {
        var root = FindProjectRoot();
        foreach (var rel in GateScripts)
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            Assert.DoesNotMatch(@"publish-(win|linux)(?!-nosdk)\b", text);
            Assert.DoesNotMatch(@"(win|linux)-x64[\\/]publish(?!-nosdk)\b", text);
        }
    }
}
