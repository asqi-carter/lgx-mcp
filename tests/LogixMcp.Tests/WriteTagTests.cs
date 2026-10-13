using L5Sharp.Core;
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The tag name is trimmed once at the <see cref="WriteTagHandler.ExecuteAsync"/>
/// entry point; gate, write, read-back, audit and the response all use that name.
/// </summary>
public class WriteTagTests : IDisposable
{
    private readonly string _tempDir;

    public WriteTagTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"writetag-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private static (TagSessionService Session, VirtualTagService Inner) BuildSession()
    {
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Sim_LineSpeed"] = (0.0,  "REAL"),
            ["Safety_OK"]     = (true, "BOOL"),
        });
        var gated   = new GatedTagService(inner,
            GateEngine.DerivePolicy(L5X.Load(FixturePath), GateConfig.Default));
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);
        return (session, inner);
    }

    private List<JsonElement> WriteEvents()
    {
        var path = Path.Combine(_tempDir, "audit.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("event").GetString() == "write")
            .ToList();
    }

    [Fact]
    public async Task PaddedName_ReadBackAndAuditUseNormalisedName()
    {
        var (session, inner) = BuildSession();

        var result = await WriteTagHandler.ExecuteAsync(
            "  Sim_LineSpeed \t", "12.5", session, default,
            new Capabilities(LiveWritesEnabled: true));

        // Gate, write and read-back all resolved the trimmed name.
        var ok = Assert.IsType<WriteTagResult>(result);
        Assert.Equal("Sim_LineSpeed", ok.Tag);
        Assert.Equal(true, ok.Verified);
        Assert.Equal(12.5, (await inner.ReadAsync("Sim_LineSpeed")).Value);

        // Audit records only the normalised name.
        var events = WriteEvents();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("Sim_LineSpeed", e.GetProperty("tag").GetString()));
    }

    [Fact]
    public async Task PaddedName_GateDenial_ReportsAndAuditsNormalisedName()
    {
        var (session, _) = BuildSession();

        var result = await WriteTagHandler.ExecuteAsync(
            " Safety_OK  ", "false", session, default,
            new Capabilities(LiveWritesEnabled: true));

        var err = Assert.IsType<WriteTagError>(result);
        Assert.Equal("Safety_OK", err.Tag);
        Assert.All(WriteEvents(), e => Assert.Equal("Safety_OK", e.GetProperty("tag").GetString()));
    }
}
