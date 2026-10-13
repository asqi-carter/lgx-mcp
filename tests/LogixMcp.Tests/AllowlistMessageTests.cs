using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// With a partial export gated by <c>LGX_WRITE_ALLOWLIST</c>, refusals name the allowlist; they
/// never quote an empty command prefix or send the caller to the Cmd_/Sim_ naming convention.
/// </summary>
public class AllowlistMessageTests
{
    // Cmd_Bad carries a range annotation on a BOOL, which cannot be applied (UnknownWriteContext).
    private static readonly string Export = PartialExportTests.DeniesExport.Replace(
        "<Tag Name=\"Cmd_Fine\"",
        "<Tag Name=\"Cmd_Bad\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\">" +
        "<Description><![CDATA[range 0..1]]></Description><Data Format=\"L5K\"><![CDATA[0]]></Data></Tag>\n" +
        "<Tag Name=\"Cmd_Fine\"", StringComparison.Ordinal);

    [Fact]
    public async Task NoEmptyPrefixText()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lgx-allow-msg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var l5x  = Path.Combine(dir, "Partial.L5X");
            var list = Path.Combine(dir, "allow.txt");
            File.WriteAllText(l5x, Export);
            File.WriteAllText(list, "Cmd_Bad\nCmd_Shared\nCmd_Fine\nCmd_W\n");

            var project = new ProjectService();
            project.Load(l5x);
            var session = new TagSessionService();
            Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
                "198.51.100.10", "1,0", useVirtual: false, project, session,
                backend: "cip",
                capabilities: new Capabilities(LiveWritesEnabled: true, WriteAllowlistPath: list),
                cipConnectProbe: (_, _) => Task.CompletedTask));

            // Listed but refused by the file (unappliable range, range violation, program-written),
            // unlisted in the file, and unknown to both the file and the allowlist.
            foreach (var (tag, value) in new[]
                     { ("Cmd_Bad", "true"), ("Cmd_Shared", "5000"), ("Cmd_W", "true"),
                       ("Cmd_Unlisted", "true"), ("Nowhere_Tag", "true") })
            {
                var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync(
                    tag, value, session, capabilities: new Capabilities(true)));

                Assert.DoesNotContain("command prefix ''", err.Error, StringComparison.Ordinal);
                Assert.DoesNotContain("matches command prefix", err.Error, StringComparison.Ordinal);
                Assert.NotNull(err.Hint);
                Assert.DoesNotContain("Cmd_", err.Hint!, StringComparison.Ordinal);
                Assert.DoesNotContain("Sim_", err.Hint!, StringComparison.Ordinal);
                Assert.Contains(WriteAllowlist.EnvVar, err.Hint!, StringComparison.Ordinal);
            }

            var unknown = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync(
                "Nowhere_Tag", "true", session, capabilities: new Capabilities(true)));
            Assert.Contains(nameof(DenyReason.NotOnAllowlist), unknown.Error, StringComparison.Ordinal);
            Assert.Contains(list, unknown.Error, StringComparison.Ordinal);

            var report = GateReportHandler.Serialize(GateReportHandler.Execute(project, null, null, session));
            Assert.DoesNotContain("command prefix ''", report, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
