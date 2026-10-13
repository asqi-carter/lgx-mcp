using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_gate_report</c> says "enforced" only when the active session enforces this file's
/// gate, and the no-gate reason follows the files opened since the session connected.
/// </summary>
public class GateReportLabelTests
{
    private static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    private static JsonElement Report(LogixProjectTools tools)
    {
        using var doc = JsonDocument.Parse(tools.GetGateReportResponse());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task LabelsMatchSessionEnforcement()
    {
        var project = new ProjectService();
        var session = new TagSessionService();
        var tools   = new LogixProjectTools(project, session);

        // 1. Gated session on a full export: enforced by the session.
        tools.GetOpenProjectResponse(Fixture("Demo.L5X"));
        Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, project, session, backend: "l5x"));
        var enforced = Report(tools);
        Assert.Equal("enforced", enforced.GetProperty("policy").GetString());
        Assert.True(enforced.GetProperty("enforcedBySession").GetBoolean());

        // 2. Ungated session on a partial export: never "enforced"; no custom-prefix text.
        tools.GetOpenProjectResponse(Fixture("real-shape", "ProgramExport.L5X"));
        Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, project, session, backend: "virtual"));
        var partial = Report(tools);
        Assert.Equal("hypothetical", partial.GetProperty("policy").GetString());
        Assert.False(partial.GetProperty("enforcedBySession").GetBoolean());
        var disclaimer = partial.GetProperty("disclaimer").GetString()!;
        Assert.Contains(PartialExportInfo.Reason, disclaimer, StringComparison.Ordinal);
        Assert.DoesNotContain("custom prefix rules", disclaimer, StringComparison.OrdinalIgnoreCase);

        // 3. A full export opened on that ungated session: still not enforced, and the
        //    reason no longer blames a partial export (it is refreshed, not stale).
        tools.GetOpenProjectResponse(Fixture("Demo.L5X"));
        var stale = Report(tools);
        Assert.Equal("hypothetical", stale.GetProperty("policy").GetString());
        Assert.False(stale.GetProperty("enforcedBySession").GetBoolean());
        var staleDisclaimer = stale.GetProperty("disclaimer").GetString()!;
        Assert.DoesNotContain("partial", staleDisclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TagSessionService.StaleNoGateReason, staleDisclaimer, StringComparison.Ordinal);
        Assert.DoesNotContain("custom prefix rules", staleDisclaimer, StringComparison.OrdinalIgnoreCase);

        // ...and the write refusal uses the refreshed reason too, still refusing.
        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync(
            "Cmd_MotorStart", "true", session, capabilities: new Capabilities(true)));
        Assert.Equal(ErrorKinds.WriteGateRequired, err.Kind);
        Assert.DoesNotContain("partial", err.Error, StringComparison.OrdinalIgnoreCase);

        // 4. Reconnecting on the full export enforces its gate again.
        Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, project, session, backend: "l5x"));
        var again = Report(tools);
        Assert.Equal("enforced", again.GetProperty("policy").GetString());
        Assert.True(again.GetProperty("enforcedBySession").GetBoolean());
    }

    [Fact]
    public void CustomPrefixesOnPartial_StillDescribeCustomRules()
    {
        var project = new ProjectService();
        project.Load(Fixture("real-shape", "ProgramExport.L5X"));

        var json = GateReportHandler.Serialize(
            GateReportHandler.Execute(project, new[] { "Sim_" }, null));
        Assert.Contains("custom prefix rules", json, StringComparison.Ordinal);
        Assert.Contains(PartialExportInfo.Reason, json, StringComparison.Ordinal);
    }

    private static async Task<(ProjectService Project, TagSessionService Session, string Dir)> ConnectPartial(
        string? allowlist)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lgx-gate-label-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var l5x = Path.Combine(dir, "Partial.L5X");
        File.WriteAllText(l5x, PartialExportTests.DeniesExport);
        string? list = null;
        if (allowlist is not null)
        {
            list = Path.Combine(dir, "allow.txt");
            File.WriteAllText(list, allowlist);
        }

        var project = new ProjectService();
        project.Load(l5x);
        Assert.NotNull(project.PartialExport);
        var session = new TagSessionService();
        Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, project, session,
            backend: "cip",
            capabilities: new Capabilities(LiveWritesEnabled: true, WriteAllowlistPath: list),
            cipConnectProbe: (_, _) => Task.CompletedTask));
        return (project, session, dir);
    }

    [Fact]
    public async Task PartialExportWithAllowlist_ReportsAllowlistPlusDenies()
    {
        var (project, session, dir) = await ConnectPartial("Cmd_Shared\nCmd_W\nSafety_Trip\nOther_Tag\nSafety_Other\n");
        try
        {
            var json = GateReportHandler.Serialize(GateReportHandler.Execute(project, null, null, session));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("enforced", root.GetProperty("policy").GetString());
            Assert.Equal("allowlist+partial-export-denies", root.GetProperty("policySource").GetString());
            Assert.True(root.GetProperty("enforcedBySession").GetBoolean());
            var note = root.GetProperty("note").GetString()!;
            Assert.Contains("grants come only from the allowlist", note, StringComparison.Ordinal);
            Assert.Contains("denies and ranges still apply", note, StringComparison.Ordinal);

            var grants = root.GetProperty("grants").EnumerateArray().ToDictionary(
                g => g.GetProperty("tag").GetString()!, g => g);
            Assert.Equal(["Cmd_Shared", "Other_Tag"], grants.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(0, grants["Cmd_Shared"].GetProperty("tagBounds").GetProperty("min").GetDouble());
            Assert.Equal(100, grants["Cmd_Shared"].GetProperty("tagBounds").GetProperty("max").GetDouble());
            Assert.All(grants.Values, g => Assert.True(g.GetProperty("onAllowlist").GetBoolean()));

            var denies = root.GetProperty("denies").EnumerateArray().ToDictionary(
                d => d.GetProperty("tag").GetString()!, d => d.GetProperty("reason").GetString()!);
            Assert.Equal(nameof(DenyReason.ProgramWritten), denies["Cmd_W"]);
            Assert.Equal(nameof(DenyReason.SafetyTag), denies["Safety_Trip"]);
            Assert.Equal(nameof(DenyReason.SafetyTag), denies["Safety_Other"]);
            Assert.Equal(3, denies.Count);

            // File tags the allowlist does not name are not listed; no empty-prefix text.
            Assert.DoesNotContain("Cmd_Fine", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Cmd_Unlisted", json, StringComparison.Ordinal);
            Assert.DoesNotContain("NotOnAllowlist", json, StringComparison.Ordinal);
            Assert.DoesNotContain("command prefix ''", json, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PartialExportWithoutAllowlist_Hypothetical()
    {
        var (project, session, dir) = await ConnectPartial(null);
        try
        {
            var json = GateReportHandler.Serialize(GateReportHandler.Execute(project, null, null, session));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("hypothetical", root.GetProperty("policy").GetString());
            Assert.Equal("project-file", root.GetProperty("policySource").GetString());
            Assert.False(root.GetProperty("enforcedBySession").GetBoolean());
            Assert.Contains(PartialExportInfo.Reason, root.GetProperty("disclaimer").GetString()!, StringComparison.Ordinal);
            Assert.False(root.TryGetProperty("note", out _));
            Assert.DoesNotContain("allowlist+partial-export-denies", json, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
