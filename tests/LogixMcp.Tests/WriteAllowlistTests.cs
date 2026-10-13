namespace LogixMcp.Tests;

/// <summary>Wording of the NotOnAllowlist denial for gated vs ungated sessions.</summary>
public class WriteAllowlistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("write-allowlist-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private WriteAllowlist Allowlist()
    {
        var p = Path.Combine(_dir, "allow.txt");
        File.WriteAllText(p, "Cmd_MotorStart\n");
        return Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(p));
    }

    [Fact]
    public void UngatedDenial_SaysAllowlistIsOnlyGrant()
    {
        var msg = Allowlist().DenialMessage("Sim_Speed", gated: false);

        Assert.NotNull(msg);
        Assert.EndsWith("With no project open, the allowlist is the only write grant.", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("only narrows", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void GatedDenial_SaysAllowlistOnlyNarrows()
    {
        var msg = Allowlist().DenialMessage("Sim_Speed");

        Assert.NotNull(msg);
        Assert.EndsWith("The allowlist only narrows what the write gate grants.", msg, StringComparison.Ordinal);
    }
}
