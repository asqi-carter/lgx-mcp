using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for LGX_WRITE_ALLOWLIST loading and validation at connect.
/// </summary>
public class AllowlistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("allowlist-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string content)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    private static Task<object> Connect(string? allowlistPath) =>
        ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip",
            capabilities: new Capabilities(false, WriteAllowlistPath: allowlistPath),
            cipConnectProbe: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task InvalidLine_ConnectFails_AllowlistInvalid_WithLine()
    {
        var path = WriteFile("# header\nMotor1_Speed\n\nbad name!\n");

        var err = Assert.IsType<AllowlistInvalidError>(await Connect(path));

        Assert.Equal("AllowlistInvalid", err.Kind);
        Assert.Equal(4, err.Line);
        Assert.Contains("line 4", err.Error, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(err.Hint));
    }

    [Fact]
    public async Task InnerWildcard_And_BareStar_AreInvalid()
    {
        var inner = Assert.IsType<AllowlistInvalidError>(await Connect(WriteFile("Ok_Tag\nMo*tor\n")));
        Assert.Equal(2, inner.Line);
        var bare = Assert.IsType<AllowlistInvalidError>(await Connect(WriteFile("*\n")));
        Assert.Equal(1, bare.Line);
    }

    [Fact]
    public async Task UnreadableFile_ConnectFails()
    {
        var err = Assert.IsType<AllowlistInvalidError>(
            await Connect(Path.Combine(_dir, "does-not-exist.txt")));

        Assert.Equal("AllowlistInvalid", err.Kind);
        Assert.Null(err.Line);
        Assert.Contains("does-not-exist.txt", err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unset_BehaviourUnchanged()
    {
        var ok = Assert.IsType<ConnectResult>(await Connect(null));

        Assert.Equal("connected", ok.Status);
        Assert.Null(ok.AllowlistSha256);
        Assert.Null(ok.AllowlistEntries);
    }

    [Fact]
    public async Task ValidFile_ConnectSucceeds_ReportsShaAndCount()
    {
        var path = WriteFile("Motor1_Speed  # trailing comment\r\nRecipe_*\n# only comment\n");

        var ok = Assert.IsType<ConnectResult>(await Connect(path));

        Assert.Equal(2, ok.AllowlistEntries);
        Assert.Equal(64, ok.AllowlistSha256!.Length);
    }

    [Fact]
    public void PrefixWildcard_Matches()
    {
        var al = Assert.IsType<WriteAllowlist>(
            WriteAllowlist.Load(WriteFile("Recipe_*\nMotor1_Speed\n")));

        Assert.True(al.Matches("Recipe_Batch"));
        Assert.True(al.Matches("recipe_"));
        Assert.True(al.Matches("Motor1_Speed"));
        Assert.False(al.Matches("Motor1_SpeedX"));
        Assert.False(al.Matches("Other"));
        Assert.Equal(2, al.EntryCount);
    }

    // ── Allowlist deny rule in the write gate ───────────────

    private static WriteGatePolicy GatePolicy() => new(
        Grants:
        [
            new GrantEntry("Cmd_MotorStart", null, null, "command prefix 'Cmd_'"),
            new GrantEntry("Sim_Speed",      null, null, "command prefix 'Sim_'"),
        ],
        Denies:
        [
            new DenyEntry("Out_Prog",   DenyReason.ProgramWritten, "Tag 'Out_Prog' is written by program logic."),
            new DenyEntry("Safety_Ok",  DenyReason.SafetyTag, "Tag 'Safety_Ok' matches safety prefix."),
            new DenyEntry("Cfg_RO",     DenyReason.ExternalAccessNotReadWrite, "Tag 'Cfg_RO' has External Access 'Read Only'.", "Read Only"),
        ]);

    private static VirtualTagService Store() => new(new Dictionary<string, (object, string?)>
    {
        ["Cmd_MotorStart"] = (false, "BOOL"),
        ["Sim_Speed"]      = (0, "DINT"),
        ["Out_Prog"]       = (false, "BOOL"),
        ["Safety_Ok"]      = (false, "BOOL"),
        ["Cfg_RO"]         = (false, "BOOL"),
        ["Cmd_Created"]    = (false, "BOOL"),
    });

    private TagSessionService GatedSession(string allowlistContent)
    {
        var session = new TagSessionService();
        session.Attach(new GatedTagService(Store(), GatePolicy()), isVirtual: true);
        session.SetWriteAllowlist(Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(WriteFile(allowlistContent))));
        return session;
    }

    [Fact]
    public async Task NotListed_DeniedNotOnAllowlist()
    {
        var session = GatedSession("Cmd_MotorStart\n");

        // Listed and gate-granted: succeeds.
        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session));

        // Gate-granted but not listed: denied NotOnAllowlist, message names the file.
        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Sim_Speed", "5", session));
        Assert.Equal(TagServiceErrorKind.WriteDenied.ToString(), err.Kind);
        Assert.Contains(nameof(DenyReason.NotOnAllowlist), err.Error, StringComparison.Ordinal);
        Assert.Contains(session.WriteAllowlist!.Path, err.Error, StringComparison.Ordinal);
        Assert.Equal(0, Assert.IsType<TagValue>(await session.Service!.ReadAsync("Sim_Speed")).Value);
    }

    [Theory]
    [InlineData(DenyReason.ProgramWritten,              "Out_Prog")]
    [InlineData(DenyReason.SafetyTag,                   "Safety_Ok")]
    [InlineData(DenyReason.ExternalAccessNotReadWrite,  "Cfg_RO")]
    [InlineData(DenyReason.CreatedByThisSession,        "Cmd_Created")]
    public async Task Listed_StillDeniedWhenGateDenies(DenyReason reason, string tag)
    {
        var session = GatedSession($"{tag}\n");
        if (reason == DenyReason.CreatedByThisSession)
            session.RecordCreatedTags([tag], "prop1");

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync(tag, "true", session));

        Assert.Equal(TagServiceErrorKind.WriteDenied.ToString(), err.Kind);
        Assert.Contains(reason.ToString(), err.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(DenyReason.NotOnAllowlist), err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ungated_Session_AllowlistStillNarrows()
    {
        var session = new TagSessionService();
        session.Attach(Store(), isVirtual: true);
        session.SetWriteAllowlist(Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(WriteFile("Cmd_MotorStart\n"))));

        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session));
        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Sim_Speed", "5", session));
        Assert.Contains(nameof(DenyReason.NotOnAllowlist), err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAllowlist_GateBehaviourUnchanged()
    {
        var session = new TagSessionService();
        session.Attach(new GatedTagService(Store(), GatePolicy()), isVirtual: true);

        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Sim_Speed", "5", session));
    }
}
