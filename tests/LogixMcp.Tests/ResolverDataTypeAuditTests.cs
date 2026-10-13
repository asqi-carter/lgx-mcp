using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Typed read/write results carry the resolver's dataType in the
/// tool result, the audit log and the traffic log; TypeUnknown and
/// StructureReadUnsupported refusals are audited as refusals.
/// </summary>
public class ResolverDataTypeAuditTests : IDisposable
{
    private readonly string _dir;

    public ResolverDataTypeAuditTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"resolver-dt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        AuditLogger.OverrideLogDir(_dir);
        TrafficLogger.OverrideLogDir(_dir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        TrafficLogger.OverrideLogDir(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Service that types a REAL tag and refuses everything else, as the CIP resolver does.</summary>
    private sealed class ResolverStub : ITagService
    {
        private object _value = 0.0f;
        public List<string> Writes { get; } = [];

        public Task ConnectAsync(string gateway, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>([]);

        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default) => tagName switch
        {
            "Sim_Udt.Speed" => Task.FromResult(new TagValue(tagName, _value, "REAL")),
            "Mystery" => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeUnknown, "Cannot read 'Mystery': its data type is unknown.", tagName)),
            "Sim_Udt" => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.StructureReadUnsupported, "Cannot read 'Sim_Udt' as a whole.", tagName)),
            _ => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch, "size mismatch", tagName)),
        };

        public Task WriteAsync(string tagName, object value, CancellationToken ct = default)
        {
            if (tagName != "Sim_Udt.Speed")
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TypeUnknown,
                    $"Cannot write '{tagName}': its data type is unknown, so nothing was written.", tagName));
            Writes.Add(tagName);
            _value = Convert.ToSingle(value);
            return Task.CompletedTask;
        }
    }

    private static (TagSessionService Session, ResolverStub Stub) Session()
    {
        var stub = new ResolverStub();
        var s = new TagSessionService();
        s.Attach(stub, isVirtual: false);
        return (s, stub);
    }

    private List<JsonElement> Lines(string file)
    {
        var path = Path.Combine(_dir, file);
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList()
            : [];
    }

    [Fact]
    public async Task Read_TypedResult_CarriesResolverDataType()
    {
        var (session, _) = Session();
        var result = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Sim_Udt.Speed", session));
        Assert.Equal("REAL", result.DataType);
    }

    [Fact]
    public async Task Write_TypedResult_CarriesResolverDataType_AndAuditRecordsIt()
    {
        var (session, _) = Session();
        var result = Assert.IsType<WriteTagResult>(
            await WriteTagHandler.ExecuteAsync("Sim_Udt.Speed", "42", session,
                capabilities: new Capabilities(LiveWritesEnabled: true)));
        Assert.Equal("REAL", result.DataType);

        var write = Lines("audit.jsonl").Last(e => e.GetProperty("event").GetString() == "write");
        Assert.Equal("REAL", write.GetProperty("dataType").GetString());
        Assert.True(write.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task Read_TypeUnknown_IsAuditedAsRefusal()
    {
        var (session, _) = Session();
        var result = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync("Mystery", session));
        Assert.Equal("TypeUnknown", result.Kind);

        var refusal = Assert.Single(Lines("audit.jsonl"), e => e.GetProperty("event").GetString() == "refusal");
        Assert.Equal("logix_read", refusal.GetProperty("operation").GetString());
        Assert.Equal("Mystery", refusal.GetProperty("tag").GetString());
        Assert.Equal("TypeUnknown", refusal.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Read_StructureReadUnsupported_IsAuditedAsRefusal()
    {
        var (session, _) = Session();
        await ReadTagHandler.ExecuteAsync("Sim_Udt", session);

        var refusal = Assert.Single(Lines("audit.jsonl"), e => e.GetProperty("event").GetString() == "refusal");
        Assert.Equal("StructureReadUnsupported", refusal.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Read_TypeMismatch_IsAuditedAsRefusal()
    {
        var (session, _) = Session();
        await ReadTagHandler.ExecuteAsync("Oddball", session);

        var refusal = Assert.Single(Lines("audit.jsonl"), e => e.GetProperty("event").GetString() == "refusal");
        Assert.Equal("TypeMismatch", refusal.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Write_TypeUnknown_IsAuditedAsRefusal_NothingWritten()
    {
        var (session, stub) = Session();
        var result = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Mystery", "42", session,
                capabilities: new Capabilities(LiveWritesEnabled: true)));
        Assert.Equal("TypeUnknown", result.Kind);
        Assert.Empty(stub.Writes);

        var write = Assert.Single(Lines("audit.jsonl"), e => e.GetProperty("event").GetString() == "write");
        Assert.Equal("TypeUnknown", write.GetProperty("kind").GetString());
        Assert.False(write.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task ReadAndWriteTools_TrafficLog_RecordsDataType()
    {
        var (session, _) = Session();
        var tools = new LogixTagTools(new ProjectService(), session, new ControllerModeService(),
            new Capabilities(LiveWritesEnabled: true));

        await tools.ReadTag("Sim_Udt.Speed");
        await tools.WriteTag("Sim_Udt.Speed", "42");
        await tools.ReadTag("Mystery");

        var traffic = Lines("traffic.jsonl");
        Assert.Equal(3, traffic.Count);
        Assert.Equal("REAL", traffic[0].GetProperty("data_type").GetString());
        Assert.Equal("REAL", traffic[1].GetProperty("data_type").GetString());
        Assert.False(traffic[2].TryGetProperty("data_type", out _));
        Assert.False(traffic[2].GetProperty("ok").GetBoolean());
    }
}
