using L5Sharp.Core;
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// NaN and ±Infinity are refused in every tag service with <c>InvalidValue</c>, whether or
/// not the tag has bounds, and each refusal is logged via <see cref="AuditLogger.LogGateDecision"/>.
/// </summary>
public class NonFiniteWriteTests : IDisposable
{
    private readonly string _tempDir;

    public NonFiniteWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nonfinite-tests-{Guid.NewGuid():N}");
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

    private static VirtualTagService BuildVirtual() =>
        new(new Dictionary<string, (object, string?)>
        {
            ["Cmd_SpeedSetpoint"] = (5.0, "REAL"),   // bounded 0..1000 in Demo.L5X
            ["Sim_LineSpeed"]     = (5.0, "REAL"),   // granted, no bounds
        });

    private static GatedTagService BuildGated(VirtualTagService inner) =>
        new(inner, GateEngine.DerivePolicy(L5X.Load(FixturePath), GateConfig.Default));

    private static TagSessionService BuildSession(ITagService service)
    {
        var session = new TagSessionService();
        session.Attach(service, isVirtual: true);
        return session;
    }

    private List<JsonElement> GateDecisions()
    {
        var path = Path.Combine(_tempDir, "audit.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("event").GetString() == "gate_decision")
            .ToList();
    }

    private void AssertRefusalLogged(string tag)
    {
        Assert.Contains(GateDecisions(), e =>
            e.GetProperty("tag").GetString() == tag &&
            e.GetProperty("outcome").GetString() == "denied" &&
            e.GetProperty("reason").GetString() == WriteTypeRule.NonFiniteReason &&
            !string.IsNullOrEmpty(e.GetProperty("provenance").GetString()));
    }

    [Theory]
    [InlineData("Cmd_SpeedSetpoint")] // with bounds
    [InlineData("Sim_LineSpeed")]     // without bounds
    public async Task NaN_RefusedWithAndWithoutBounds(string tag)
    {
        foreach (var raw in new[] { "NaN", "-NaN", "nan" })
        {
            var inner = BuildVirtual();
            var result = await WriteTagHandler.ExecuteAsync(
                tag, raw, BuildSession(BuildGated(inner)), default,
                new Capabilities(LiveWritesEnabled: true));

            var err = Assert.IsType<WriteTagError>(result);
            Assert.Equal(ErrorKinds.InvalidValue, err.Kind);
            Assert.Equal(5.0, (await inner.ReadAsync(tag)).Value); // nothing written
        }

        // A numeric NaN straight into the gated service (no handler parse).
        var inner2 = BuildVirtual();
        var gated = BuildGated(inner2);
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(tag, double.NaN));
        Assert.Equal(TagServiceErrorKind.InvalidValue, ex.Error.Kind);
        var exF = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(tag, float.NaN));
        Assert.Equal(TagServiceErrorKind.InvalidValue, exF.Error.Kind);
        Assert.Equal(5.0, (await inner2.ReadAsync(tag)).Value);

        AssertRefusalLogged(tag);
    }

    [Fact]
    public async Task Infinity_RefusedOnRealAndLreal()
    {
        // Handler-level spellings.
        foreach (var raw in new[] { "Infinity", "-Infinity", "∞", "-∞", "inf", "1e999" })
        {
            var result = await WriteTagHandler.ExecuteAsync(
                "Sim_LineSpeed", raw, BuildSession(BuildGated(BuildVirtual())), default,
                new Capabilities(LiveWritesEnabled: true));
            Assert.Equal(ErrorKinds.InvalidValue, Assert.IsType<WriteTagError>(result).Kind);
        }

        // CIP coercion, REAL and LREAL, both infinities.
        foreach (var type in new[] { "REAL", "LREAL" })
        {
            var res = new CipTypeResolution(CipTypeKind.Atomic, type, CipTypeSource.Project, Array.Empty<string>());
            foreach (var v in new object[] { double.PositiveInfinity, double.NegativeInfinity, float.PositiveInfinity })
            {
                var ex = Assert.Throws<TagServiceException>(() => LibPlcTagService.CoerceForWrite("T", res, v));
                Assert.Equal(TagServiceErrorKind.InvalidValue, ex.Error.Kind);
            }
        }

        // Virtual and L5X services.
        var virt = BuildVirtual();
        var vex = await Assert.ThrowsAsync<TagServiceException>(() => virt.WriteAsync("Sim_LineSpeed", double.PositiveInfinity));
        Assert.Equal(TagServiceErrorKind.InvalidValue, vex.Error.Kind);

        var l5x = new L5XOfflineTagService(L5X.Load(FixturePath));
        await l5x.ConnectAsync("", "");
        var lex = await Assert.ThrowsAsync<TagServiceException>(() => l5x.WriteAsync("Sim_LineSpeed", double.NegativeInfinity));
        Assert.Equal(TagServiceErrorKind.InvalidValue, lex.Error.Kind);

        AssertRefusalLogged("Sim_LineSpeed");
    }

    [Fact]
    public void CipCoerce_RejectsNaN()
    {
        foreach (var type in new[] { "REAL", "LREAL", "DINT" })
        {
            var res = new CipTypeResolution(CipTypeKind.Atomic, type, CipTypeSource.Project, Array.Empty<string>());
            foreach (var v in new object[] { double.NaN, float.NaN })
            {
                var ex = Assert.Throws<TagServiceException>(() => LibPlcTagService.CoerceForWrite("Cmd_Speed", res, v));
                Assert.Equal(TagServiceErrorKind.InvalidValue, ex.Error.Kind);
            }
        }

        // Finite values still coerce.
        var real = new CipTypeResolution(CipTypeKind.Atomic, "REAL", CipTypeSource.Project, Array.Empty<string>());
        Assert.Equal(1.5f, LibPlcTagService.CoerceForWrite("Cmd_Speed", real, 1.5));
        AssertRefusalLogged("Cmd_Speed");
    }
}
