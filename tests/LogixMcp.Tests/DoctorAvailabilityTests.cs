using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_doctor</c> is registered from the core assembly in every install
/// state (adapter missing, adapter loaded) and exactly once, and without the SDK its checks name the
/// directories the loader probed.
/// </summary>
public class DoctorAvailabilityTests
{
    private static readonly System.Reflection.Assembly Core = typeof(LogixDoctorTools).Assembly;
    private static readonly System.Reflection.Assembly Adapter = typeof(SdkToolNames).Assembly;

    private static List<string> RegisteredToolNames(System.Reflection.Assembly? adapter)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<TagSessionService>();
        services.AddSingleton<ControllerModeService>();
        services.AddSingleton(Capabilities.FromEnvironment());
        var builder = services.AddMcpServer().WithToolsFromAssembly(Core);
        // Mirrors Program.cs: the adapter's tools are added only when the adapter loaded.
        if (adapter is not null) builder.WithToolsFromAssembly(adapter);

        using var sp = services.BuildServiceProvider();
        return sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToList();
    }

    /// <summary>No adapter (missing, failed to load, or a no-SDK build): the core assembly still registers logix_doctor, once.</summary>
    [Fact]
    public void NoAdapter_DoctorRegisteredOnce()
    {
        var names = RegisteredToolNames(adapter: null);

        Assert.Single(names, n => n == "logix_doctor");
    }

    /// <summary>Adapter loaded: its tools are added and logix_doctor is still registered exactly once.</summary>
    [Fact]
    public void AdapterLoaded_DoctorRegisteredOnce()
    {
        var names = RegisteredToolNames(Adapter);

        Assert.Single(names, n => n == "logix_doctor");
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain("logix_doctor", SdkToolNames.All);
    }

    /// <summary>With no adapter loaded the SDK check names every probed path, and the rest of the doctor still runs.</summary>
    [Fact]
    public async Task NoAdapter_SdkCheckNamesProbedPaths()
    {
        // Injected "no adapter" observations: the process-wide DoctorEnvironment.SdkObservations is never written.
        {
            var env = new DoctorEnvironment(new TagSessionService(), observations: DoctorEnvironment.NoObservations);
            var result = await DoctorHandler.ExecuteAsync(null, null, env, new Capabilities(false));

            var sdk = result.Checks.Single(c => c.Id == "sdkInstalled");
            Assert.NotEqual(DoctorStatus.Ok, sdk.Status);
            if (env.SdkSupportedInBuild)
            {
                Assert.Contains("Install Studio 5000 Logix Designer or set LOGIX_SDK_PATH", sdk.Fix);
                var probed = SdkAdapterLoader.CollectProbeResults();
                Assert.NotEmpty(probed);
                foreach (var entry in probed)
                    Assert.Contains(entry.Path, sdk.Observed);
            }
            else
            {
                // A no-SDK build has no SDK lane to probe for: the check says so instead of asking for an install.
                Assert.Equal(DoctorStatus.Info, sdk.Status);
                Assert.Contains("not used by this build", sdk.Observed);
            }

            Assert.Equal(DoctorHandler.CheckIds, result.Checks.Select(c => c.Id).ToArray());
            Assert.Contains(result.Checks, c => c.Id == "cipRuntime");
        }
    }

    /// <summary>The tool handler itself answers with the doctor JSON when no session or adapter exists.</summary>
    [Fact]
    public async Task DoctorTool_AnswersWithoutAdapter()
    {
        var tool = new LogixDoctorTools(new TagSessionService(), new Capabilities(false));

        var json = await tool.GetDoctorResponseAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(DoctorHandler.CheckIds.Count, doc.RootElement.GetProperty("checks").GetArrayLength());
    }
}
