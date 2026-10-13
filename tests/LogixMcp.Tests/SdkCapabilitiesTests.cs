using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>The SDK capability table, its refusal kind, and the lgx://sdk/capabilities resource.</summary>
public class SdkCapabilitiesTests
{
    private static SdkCapability Entry(SdkCapabilities t, string name) => t.Report().Get(name)!;

    // ── Version rules ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildCheck_VersionRules_Below37Unavailable_37Plus_Available()
    {
        // 37 and later: available, by version rule.
        var v37 = Entry(SdkCapabilities.ForSession(new DesignerVersion(37, 0, DesignerVersionSource.ProjectRevision), "2.2"),
            SdkCapabilityNames.BuildCheck);
        Assert.Equal(SdkCapabilityStatus.Available, v37.Status);
        Assert.Equal(SdkCapabilitySource.VersionRule, v37.Source);

        var v38 = Entry(SdkCapabilities.ForSession(new DesignerVersion(38, 1, DesignerVersionSource.SdkError), "2.2"),
            SdkCapabilityNames.BuildCheck);
        Assert.Equal(SdkCapabilityStatus.Available, v38.Status);

        // Authoritative version below 37: unavailable, by version rule.
        var v36 = Entry(SdkCapabilities.ForSession(new DesignerVersion(36, 5, DesignerVersionSource.SdkError), "2.2"),
            SdkCapabilityNames.BuildCheck);
        Assert.Equal(SdkCapabilityStatus.Unavailable, v36.Status);
        Assert.Equal(SdkCapabilitySource.VersionRule, v36.Source);

        // A project revision below 37 proves nothing about the installed Designer: unknown.
        var rev36 = Entry(SdkCapabilities.ForSession(new DesignerVersion(36, 0, DesignerVersionSource.ProjectRevision), "2.2"),
            SdkCapabilityNames.BuildCheck);
        Assert.Equal(SdkCapabilityStatus.Unknown, rev36.Status);

        // No version at all: unknown.
        Assert.Equal(SdkCapabilityStatus.Unknown,
            Entry(SdkCapabilities.ForSession((DesignerVersion?)null, "2.2"), SdkCapabilityNames.BuildCheck).Status);
    }

    [Fact]
    public void BuildCheck_Detail_UnknownVersion_SaysTheVersionIsNotKnownYet()
    {
        const string expected = "Needs Logix Designer 37 or later. The installed version is not known yet; the first logix_build reports it.";
        Assert.Equal(expected,
            Entry(SdkCapabilities.ForSession((DesignerVersion?)null, "2.2"), SdkCapabilityNames.BuildCheck).Detail);
        // A project revision proves nothing about the installed Designer, so it reads the same.
        Assert.Equal(expected,
            Entry(SdkCapabilities.ForSession(new DesignerVersion(36, 0, DesignerVersionSource.ProjectRevision), "2.2"),
                SdkCapabilityNames.BuildCheck).Detail);
    }

    [Fact]
    public void BuildCheck_Detail_KnownVersion_SaysWhichVersionThisIs()
    {
        Assert.Equal("Needs Logix Designer 37 or later; this is 38.1.",
            Entry(SdkCapabilities.ForSession(new DesignerVersion(38, 1, DesignerVersionSource.SdkError), "2.2"),
                SdkCapabilityNames.BuildCheck).Detail);
        Assert.Equal("Needs Logix Designer 37 or later; this is 36.5.",
            Entry(SdkCapabilities.ForSession(new DesignerVersion(36, 5, DesignerVersionSource.SdkError), "2.2"),
                SdkCapabilityNames.BuildCheck).Detail);
    }

    [Fact]
    public void VersionEntries_ReportDesignerAndPackageVersion()
    {
        var t = SdkCapabilities.ForSession(new DesignerVersion(36, 5, DesignerVersionSource.SdkError), "2.2.1109.0");
        Assert.Equal("36.5", Entry(t, SdkCapabilityNames.DesignerVersion).Value);
        Assert.Equal("2.2.1109.0", Entry(t, SdkCapabilityNames.SdkPackageVersion).Value);
        Assert.Equal(SdkCapabilityNames.All, t.Report().Entries.Select(e => e.Name).ToArray());
    }

    // ── Observed update ───────────────────────────────────────────────────────

    [Fact]
    public void UnsupportedVersionError_AtRuntime_UpdatesTheCapabilityEntry()
    {
        // Only a project revision is known, so the build check starts unknown.
        DesignerVersion? version = new DesignerVersion(36, 0, DesignerVersionSource.ProjectRevision);
        var table = SdkCapabilities.ForSession(() => version, "2.2");
        Assert.Equal(SdkCapabilityStatus.Unknown, Entry(table, SdkCapabilityNames.BuildCheck).Status);
        Assert.Null(table.Require(SdkCapabilityNames.BuildCheck));

        // The SDK's own unsupported-version error arrives at runtime.
        var learned = DesignerVersionInfo.TryParseUnsupportedVersion("Operation not supported on Logix Designer version 36.5.");
        Assert.NotNull(learned);
        version = learned;
        table.ObserveUnsupportedVersion(SdkCapabilityNames.BuildCheck, learned!);

        var after = Entry(table, SdkCapabilityNames.BuildCheck);
        Assert.Equal(SdkCapabilityStatus.Unavailable, after.Status);
        Assert.Equal(SdkCapabilitySource.Observed, after.Source);
        Assert.Equal("36.5", Entry(table, SdkCapabilityNames.DesignerVersion).Value);
        Assert.NotNull(table.Require(SdkCapabilityNames.BuildCheck));
    }

    [Fact]
    public void Observe_Overrides_TheRuleBasedEntry_ForAnyCapability()
    {
        var table = SdkCapabilities.ForSession(new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision), "2.2");
        Assert.Equal(SdkCapabilityStatus.Unknown, Entry(table, SdkCapabilityNames.RungDelete).Status);

        table.Observe(SdkCapabilityNames.RungDelete, SdkCapabilityStatus.Unavailable, "The SDK refused it.");

        var e = Entry(table, SdkCapabilityNames.RungDelete);
        Assert.Equal(SdkCapabilityStatus.Unavailable, e.Status);
        Assert.Equal(SdkCapabilitySource.Observed, e.Source);
    }

#if LOGIX_SDK
    [Fact]
    public async Task Session_RecordingTheSdkVersionError_UpdatesItsCapabilityTable()
    {
        var session = new LogixProjectSession((_, _) => throw new InvalidOperationException("not opened"));
        session.DesignerVersion = DesignerVersionInfo.FromProjectRevision("36.00");
        Assert.Equal(SdkCapabilityStatus.Unknown, session.Capabilities.Report().Get(SdkCapabilityNames.BuildCheck)!.Status);

        session.RecordDesignerVersionFromSdk(
            DesignerVersionInfo.TryParseUnsupportedVersion("Operation not supported on Logix Designer version 36.5.")!);

        var e = session.Capabilities.Report().Get(SdkCapabilityNames.BuildCheck)!;
        Assert.Equal(SdkCapabilityStatus.Unavailable, e.Status);
        Assert.Equal(SdkCapabilitySource.Observed, e.Source);
        await Task.CompletedTask;
    }
#endif

    // ── Refusal kind ──────────────────────────────────────────────────────────

    [Fact]
    public void UnavailableCapability_Refuses_WithKindCapabilityVersionAndWhatStillWorks()
    {
        var table = SdkCapabilities.ForSession(new DesignerVersion(36, 5, DesignerVersionSource.SdkError), "2.2");

        var refusal = table.Require(SdkCapabilityNames.BuildCheck);

        Assert.NotNull(refusal);
        Assert.Equal("SdkCapabilityUnavailable", refusal!.Kind);
        Assert.Equal(ErrorKinds.SdkCapabilityUnavailable, refusal.Kind);
        Assert.Equal(SdkCapabilityNames.BuildCheck, refusal.Capability);
        Assert.Equal("36.5", refusal.Version);
        Assert.Contains("36.5", refusal.Error);
        Assert.Contains("Verify", refusal.WhatStillWorks);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(refusal));
        var root = doc.RootElement;
        Assert.Equal("SdkCapabilityUnavailable", root.GetProperty("kind").GetString());
        Assert.Equal("buildCheck", root.GetProperty("capability").GetString());
        Assert.Equal("36.5", root.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("whatStillWorks").GetString()));
    }

    [Fact]
    public void AvailableOrUnknownCapability_DoesNotRefuse()
    {
        Assert.Null(SdkCapabilities.ForSession(new DesignerVersion(38, 0, DesignerVersionSource.SdkError), "2.2")
            .Require(SdkCapabilityNames.BuildCheck));
        Assert.Null(SdkCapabilities.ForSession((DesignerVersion?)null, "2.2").Require(SdkCapabilityNames.BuildCheck));
    }

    // ── Resource shape ────────────────────────────────────────────────────────

    [Fact]
    public void CapabilitiesResource_ReturnsJson_WithNoSession()
    {
        var json = SdkCapabilitiesResource.Read(new TagSessionService());   // no session attached

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("session").GetBoolean());
        var entries = root.GetProperty("entries");
        Assert.Equal(SdkCapabilityNames.All,
            entries.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray());
        string[] statuses = ["available", "unavailable", "unknown"];
        foreach (var e in entries.EnumerateArray())
        {
            Assert.Contains(e.GetProperty("status").GetString(), statuses);
            Assert.False(string.IsNullOrEmpty(e.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrEmpty(e.GetProperty("detail").GetString()));
        }
        Assert.Equal("lgx://sdk/capabilities", SdkCapabilitiesView.ResourceUri);
    }

    [Fact]
    public void CapabilitiesResource_IsRegisteredAsAnMcpResource()
    {
        var method = typeof(SdkCapabilitiesResource).GetMethod(nameof(SdkCapabilitiesResource.Read))!;
        var attr = method.GetCustomAttributes(false)
            .Single(a => a.GetType().Name == "McpServerResourceAttribute");
        Assert.Equal("lgx://sdk/capabilities", attr.GetType().GetProperty("UriTemplate")!.GetValue(attr));
        Assert.Equal("application/json", attr.GetType().GetProperty("MimeType")!.GetValue(attr));
    }

    [Fact]
    public void NoSessionReport_WithoutSdk_MarksSdkEntriesUnavailableByInstall()
    {
        var r = SdkCapabilities.NoSession(sdkLoaded: false, sdkPackageVersion: null);
        Assert.False(r.SdkLoaded);
        Assert.All(r.Entries, e =>
        {
            Assert.Equal(SdkCapabilityStatus.Unavailable, e.Status);
            Assert.Equal(SdkCapabilitySource.Install, e.Source);
        });
    }

    // ── logix_doctor and logix_connect ────────────────────────────────────────

    [Fact]
    public async Task Doctor_Output_CarriesCapabilities()
    {
        var env = new DoctorHandlerTests.FakeEnv();
        var result = await DoctorHandler.ExecuteAsync(null, null, env, new Capabilities(false, StudioAutomationEnabled: false));

        Assert.NotNull(result.Capabilities);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var caps = doc.RootElement.GetProperty("capabilities");
        Assert.Equal(SdkCapabilityNames.All.Count, caps.GetProperty("entries").GetArrayLength());
    }

#if LOGIX_SDK
    private sealed class VersionedFactory(DesignerVersion version) : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline", bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default) =>
            Task.FromResult(new SdkSessionResult(
                Service: new VirtualTagService(), DownloadPerformed: false, ModeTransitions: [],
                CorrelationPath: "upload-to-new-project", DesignerVersion: version));
    }

    [Fact]
    public async Task Connect_Sdk_Output_CarriesCapabilities()
    {
        var result = await ConnectHandler.ExecuteAsync(
            gateway: "", path: "", useVirtual: false, projectService: new ProjectService(),
            sessionService: new TagSessionService(), backend: "sdk",
            sdkFactory: new VersionedFactory(new DesignerVersion(36, 5, DesignerVersionSource.SdkError)),
            sdkMode: "online", sdkCommPath: "EmulateEthernet\\192.0.2.10");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.NotNull(ok.Capabilities);
        Assert.Equal(SdkCapabilityStatus.Unavailable, ok.Capabilities!.Get(SdkCapabilityNames.BuildCheck)!.Status);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok));
        Assert.True(doc.RootElement.TryGetProperty("capabilities", out _));
    }
#endif
}
