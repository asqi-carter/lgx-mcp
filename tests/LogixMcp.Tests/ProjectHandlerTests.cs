using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="OpenProjectHandler"/>, <see cref="ProjectSummaryHandler"/>,
/// <see cref="FindTagsHandler"/>, <see cref="ProjectService"/>, and
/// <see cref="LogixProjectTools"/>.
/// </summary>
[Collection("StaticHandlerHooks")]
public class ProjectHandlerTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // ------------------------------------------------------------------ //
    //  ProjectService                                                      //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ProjectService_InitialState_IsNotLoaded()
    {
        var svc = new ProjectService();
        Assert.False(svc.IsLoaded);
        Assert.Null(svc.Project);
    }

    [Fact]
    public void ProjectService_Load_SetsIsLoadedTrue()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        Assert.True(svc.IsLoaded);
        Assert.NotNull(svc.Project);
    }

    [Fact]
    public void ProjectService_Load_ThrowsFileNotFoundForMissingPath()
    {
        var svc = new ProjectService();
        Assert.Throws<FileNotFoundException>(() => svc.Load("/nonexistent/path/file.L5X"));
    }

    [Fact]
    public void ProjectService_Clear_UnloadsProject()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        svc.Clear();
        Assert.False(svc.IsLoaded);
        Assert.Null(svc.Project);
    }

    [Fact]
    public void ProjectService_LoadFromAcd_SetsProjectFromL5X()
    {
        // LoadFromAcd loads the L5X file into Project…
        var svc = new ProjectService();
        svc.LoadFromAcd(FixturePath, "/fake/original.ACD");
        Assert.True(svc.IsLoaded);
        Assert.NotNull(svc.Project);
        Assert.Equal("DemoController", svc.Project!.Controller.Name);
    }

    [Fact]
    public void ProjectService_LoadFromAcd_SetsFilePathToAcdPath()
    {
        // …but FilePath points to the original ACD path (for SDK correlation).
        var svc = new ProjectService();
        svc.LoadFromAcd(FixturePath, "/fake/original.ACD");
        Assert.Equal("/fake/original.ACD", svc.FilePath);
    }

    [Fact]
    public void ProjectService_LoadFromAcd_ThrowsFileNotFoundForMissingL5X()
    {
        var svc = new ProjectService();
        Assert.Throws<FileNotFoundException>(() =>
            svc.LoadFromAcd("/nonexistent/path/file.L5X", "/fake/original.ACD"));
    }

    [Fact]
    public void ProjectService_RegisterFromUpload_SetsIsLoadedTrue()
    {
        var svc     = new ProjectService();
        var project = L5Sharp.Core.L5X.Load(FixturePath);
        svc.RegisterFromUpload(project, "/tmp/uploaded.ACD");
        Assert.True(svc.IsLoaded);
    }

    [Fact]
    public void ProjectService_RegisterFromUpload_SetsProject()
    {
        var svc     = new ProjectService();
        var project = L5Sharp.Core.L5X.Load(FixturePath);
        svc.RegisterFromUpload(project, "/tmp/uploaded.ACD");
        Assert.Same(project, svc.Project);
    }

    [Fact]
    public void ProjectService_RegisterFromUpload_SetsFilePath()
    {
        var svc     = new ProjectService();
        var project = L5Sharp.Core.L5X.Load(FixturePath);
        svc.RegisterFromUpload(project, "/tmp/uploaded.ACD");
        Assert.Equal("/tmp/uploaded.ACD", svc.FilePath);
    }

    [Fact]
    public void ProjectService_RegisterFromUpload_IsNoOpWhenAlreadyLoaded()
    {
        // Ensure an explicit logix_open_project is never silently overwritten.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var existingProject = svc.Project;
        var existingPath    = svc.FilePath;

        var otherProject = L5Sharp.Core.L5X.Load(FixturePath);
        svc.RegisterFromUpload(otherProject, "/tmp/other.ACD");

        Assert.Same(existingProject, svc.Project);
        Assert.Equal(existingPath,   svc.FilePath);
    }

    // ------------------------------------------------------------------ //
    //  OpenProjectHandler                                                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void OpenProjectHandler_Execute_ReturnsResultForValidPath()
    {
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute(FixturePath, svc);

        Assert.IsType<OpenProjectResult>(result);
        var r = (OpenProjectResult)result;
        Assert.Equal("DemoController", r.Controller);
        Assert.Equal("1.0",            r.SchemaRevision);
        Assert.Equal(1,                r.Programs);
        Assert.Equal(7,                r.Tags);
        Assert.Equal(1,                r.Routines);
    }

    [Fact]
    public void OpenProjectHandler_Execute_ReturnsErrorForMissingFile()
    {
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute("/does/not/exist.L5X", svc);

        Assert.IsType<OpenProjectError>(result);
        var e = (OpenProjectError)result;
        Assert.Contains("not found", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenProjectHandler_Execute_ReturnsErrorForEmptyPath()
    {
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute("", svc);

        Assert.IsType<OpenProjectError>(result);
    }

    [Fact]
    public void OpenProjectHandler_Execute_LoadsProjectIntoService()
    {
        var svc = new ProjectService();
        OpenProjectHandler.Execute(FixturePath, svc);
        Assert.True(svc.IsLoaded);
    }

    [Fact]
    public void OpenProjectHandler_Execute_SerializesToValidJson()
    {
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute(FixturePath, svc);
        var json   = JsonSerializer.Serialize(result);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("DemoController", doc.RootElement.GetProperty("controller").GetString());
        Assert.Equal("1.0",            doc.RootElement.GetProperty("schemaRevision").GetString());
        Assert.Equal(1,                doc.RootElement.GetProperty("programs").GetInt32());
        Assert.Equal(7,                doc.RootElement.GetProperty("tags").GetInt32());
        Assert.Equal(1,                doc.RootElement.GetProperty("routines").GetInt32());
    }

    [Fact]
    public void OpenProjectHandler_Execute_ErrorSerializesToJsonWithErrorKey()
    {
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute("/missing.L5X", svc);
        var json   = JsonSerializer.Serialize(result);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ------------------------------------------------------------------ //
    //  OpenProjectHandler.ExecuteAsync — ACD + async L5X path             //
    // ------------------------------------------------------------------ //

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_ReturnsErrorForEmptyPath()
    {
        var svc    = new ProjectService();
        var result = await OpenProjectHandler.ExecuteAsync("", svc);

        Assert.IsType<OpenProjectError>(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_ReturnsResultForValidL5XPath()
    {
        // The async path delegates to the synchronous Execute for .L5X files.
        var svc    = new ProjectService();
        var result = await OpenProjectHandler.ExecuteAsync(FixturePath, svc);

        Assert.IsType<OpenProjectResult>(result);
        var r = (OpenProjectResult)result;
        Assert.Equal("DemoController", r.Controller);
        Assert.Equal(7,                r.Tags);
    }

#if LOGIX_SDK
    private const string AcdUnavailableMarker = "unavailable";
#else
    private const string AcdUnavailableMarker = "not supported in this build";
#endif

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_ReturnsConversionUnavailableErrorForAcdWhenConverterNotRegistered()
    {
        // Without a registered AcdConverter, opening an ACD should return a
        // dedicated OpenProjectConversionUnavailableError (not a generic error)
        // that (a) states conversion is unavailable, (b) names the workaround.
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        try
        {
            OpenProjectHandler.AcdConverter = null;
            var result = await OpenProjectHandler.ExecuteAsync("/some/project.ACD", svc);

            Assert.IsType<OpenProjectConversionUnavailableError>(result);
            var e = (OpenProjectConversionUnavailableError)result;

#if LOGIX_SDK
            // (a) States that conversion / SDK is unavailable.
            Assert.Contains("unavailable", e.Error, StringComparison.OrdinalIgnoreCase);

            // (b) Names the File → Save As → L5X workaround.
            Assert.Contains("Save As", e.Workaround, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("L5X",     e.Workaround, StringComparison.OrdinalIgnoreCase);
#else
            // No-SDK build: says only that ACD is unsupported here and to export to L5X.
            Assert.Equal(".ACD files are not supported in this build.", e.Error);
            Assert.Equal("Export the project to L5X and pass the .L5X path to logix_open_project.", e.Workaround);
#endif
        }
        finally
        {
            // Restore whatever was registered (may be null in CI without SDK).
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_ConversionUnavailableError_IsNotGenericOpenProjectError()
    {
        // The error returned for ACD without SDK must be the dedicated structured type,
        // never the generic OpenProjectError — callers must be able to distinguish them.
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        try
        {
            OpenProjectHandler.AcdConverter = null;
            var result = await OpenProjectHandler.ExecuteAsync("/some/project.ACD", svc);

            Assert.IsNotType<OpenProjectError>(result);
            Assert.IsType<OpenProjectConversionUnavailableError>(result);
        }
        finally
        {
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_ConversionUnavailableError_SerializesToJsonWithBothFields()
    {
        // The structured error must serialize with both "error" and "workaround" keys
        // so callers can read both fields without parsing the error string.
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        try
        {
            OpenProjectHandler.AcdConverter = null;
            var result = await OpenProjectHandler.ExecuteAsync("/some/project.ACD", svc);

            var json = System.Text.Json.JsonSerializer.Serialize(result);
            var doc  = System.Text.Json.JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("error",      out _),
                "JSON must contain 'error' key");
            Assert.True(doc.RootElement.TryGetProperty("workaround", out _),
                "JSON must contain 'workaround' key");
        }
        finally
        {
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_AcdExtensionIsCaseInsensitive()
    {
        // Both .ACD and .acd should trigger the ACD path and return the structured
        // conversion-unavailable error (not a generic error, not a silent ungated path).
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        try
        {
            OpenProjectHandler.AcdConverter = null;

            // .ACD (upper-case) → ACD path → structured conversion-unavailable error
            var resultUpper = await OpenProjectHandler.ExecuteAsync("/project.ACD", svc);
            Assert.IsType<OpenProjectConversionUnavailableError>(resultUpper);
            Assert.Contains(AcdUnavailableMarker,
                ((OpenProjectConversionUnavailableError)resultUpper).Error,
                StringComparison.OrdinalIgnoreCase);

            // .acd (lower-case) → ACD path → same structured error
            var resultLower = await OpenProjectHandler.ExecuteAsync("/project.acd", svc);
            Assert.IsType<OpenProjectConversionUnavailableError>(resultLower);
            Assert.Contains(AcdUnavailableMarker,
                ((OpenProjectConversionUnavailableError)resultLower).Error,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_CallsAcdConverterForAcdPath()
    {
        // When AcdConverter is registered, it must be invoked for .ACD paths.
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        bool called   = false;
        try
        {
            OpenProjectHandler.AcdConverter = (path, _, _) =>
            {
                called = true;
                return System.Threading.Tasks.Task.FromResult<object>(
                    new OpenProjectError("stub — no real SDK"));
            };

            await OpenProjectHandler.ExecuteAsync("/some/project.ACD", svc);
            Assert.True(called, "AcdConverter delegate was not invoked for .ACD path.");
        }
        finally
        {
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_DoesNotCallAcdConverterForL5XPath()
    {
        // .L5X paths must not invoke AcdConverter — they use the existing sync path.
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        bool called   = false;
        try
        {
            OpenProjectHandler.AcdConverter = (_, _, _) =>
            {
                called = true;
                return System.Threading.Tasks.Task.FromResult<object>(
                    new OpenProjectError("should not be called"));
            };

            var result = await OpenProjectHandler.ExecuteAsync(FixturePath, svc);
            Assert.False(called, "AcdConverter was unexpectedly invoked for an .L5X path.");
            Assert.IsType<OpenProjectResult>(result);
        }
        finally
        {
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    // ------------------------------------------------------------------ //
    //  OpenProjectResult — ACD gate derivation provenance fields           //
    // ------------------------------------------------------------------ //

    [Fact]
    public void OpenProjectResult_WithGateSource_SerializesGateSourceAndDerivedL5xFields()
    {
        // When GateSource is "acd-derived" and DerivedL5x is set, both fields must
        // appear in the JSON so callers can distinguish server-generated exports from
        // operator-provided ones.
        var result = new OpenProjectResult(
            Controller:     "TestController",
            SchemaRevision: "1.0",
            Programs:       1,
            Tags:           5,
            Routines:       2,
            GateSource:     "acd-derived",
            DerivedL5x:     "/tmp/lgx-acd-abc123.L5X");

        var json = JsonSerializer.Serialize(result);
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("acd-derived",                    doc.RootElement.GetProperty("gateSource").GetString());
        Assert.Equal("/tmp/lgx-acd-abc123.L5X", doc.RootElement.GetProperty("derivedL5x").GetString());
    }

    [Fact]
    public void OpenProjectResult_WithoutGateSource_OmitsGateSourceAndDerivedL5xFromJson()
    {
        // L5X input path must NOT emit gateSource or derivedL5x — the operator's
        // export is the authoritative source and needs no derivation notice.
        var result = new OpenProjectResult(
            Controller:     "TestController",
            SchemaRevision: "1.0",
            Programs:       1,
            Tags:           5,
            Routines:       2);

        var json = JsonSerializer.Serialize(result);
        var doc  = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("gateSource", out _),
            "gateSource must not appear in JSON for operator-provided L5X input.");
        Assert.False(doc.RootElement.TryGetProperty("derivedL5x", out _),
            "derivedL5x must not appear in JSON for operator-provided L5X input.");
    }

    [Fact]
    public void OpenProjectHandler_Execute_L5XPath_ResultHasNullGateSourceAndDerivedL5x()
    {
        // The synchronous L5X Execute path must not populate gate derivation fields.
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute(FixturePath, svc);

        Assert.IsType<OpenProjectResult>(result);
        var r = (OpenProjectResult)result;
        Assert.Null(r.GateSource);
        Assert.Null(r.DerivedL5x);
    }

    [Fact]
    public void OpenProjectHandler_Execute_L5XPath_JsonOmitsGateSourceFields()
    {
        // JSON from the L5X path must not include gateSource or derivedL5x keys.
        var svc    = new ProjectService();
        var result = OpenProjectHandler.Execute(FixturePath, svc);
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("gateSource", out _),
            "gateSource must not appear in JSON for direct L5X input.");
        Assert.False(doc.RootElement.TryGetProperty("derivedL5x", out _),
            "derivedL5x must not appear in JSON for direct L5X input.");
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenProjectHandler_ExecuteAsync_AcdConverter_ResultPreservesGateSourceFields()
    {
        // When AcdConverter returns an OpenProjectResult with GateSource="acd-derived",
        // ExecuteAsync must pass it through unmodified so the caller sees the provenance.
        var svc       = new ProjectService();
        var savedConv = OpenProjectHandler.AcdConverter;
        try
        {
            var stubResult = new OpenProjectResult(
                Controller:     "StubController",
                SchemaRevision: "1.0",
                Programs:       2,
                Tags:           10,
                Routines:       4,
                GateSource:     "acd-derived",
                DerivedL5x:     "/tmp/lgx-acd-stub.L5X");

            OpenProjectHandler.AcdConverter = (_, _, _) =>
                System.Threading.Tasks.Task.FromResult<object>(stubResult);

            var result = await OpenProjectHandler.ExecuteAsync("/some/project.ACD", svc);

            Assert.IsType<OpenProjectResult>(result);
            var r = (OpenProjectResult)result;
            Assert.Equal("acd-derived",                    r.GateSource);
            Assert.Equal("/tmp/lgx-acd-stub.L5X",   r.DerivedL5x);
        }
        finally
        {
            OpenProjectHandler.AcdConverter = savedConv;
        }
    }

    // ------------------------------------------------------------------ //
    //  ProjectSummaryHandler                                               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ProjectSummaryHandler_Execute_ReturnsErrorWhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = ProjectSummaryHandler.Execute(svc);

        Assert.IsType<ProjectSummaryError>(result);
    }

    [Fact]
    public void ProjectSummaryHandler_Execute_ReturnsResultAfterLoad()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = ProjectSummaryHandler.Execute(svc);

        Assert.IsType<ProjectSummaryResult>(result);
        var r = (ProjectSummaryResult)result;
        Assert.Equal("DemoController", r.Controller);
        Assert.Equal("1.0",            r.SchemaRevision);
        Assert.Equal(7,                r.ControllerTags);
        Assert.Single(r.Programs);
    }

    [Fact]
    public void ProjectSummaryHandler_Execute_ProgramSummaryContainsCorrectCounts()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result   = (ProjectSummaryResult)ProjectSummaryHandler.Execute(svc);
        var mainProg = result.Programs[0];

        Assert.Equal("MainProgram", mainProg.Name);
        Assert.Equal(0,             mainProg.Tags);
        Assert.Equal(1,             mainProg.Routines);
    }

    [Fact]
    public void ProjectSummaryHandler_Execute_SerializesToValidJson()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = JsonSerializer.Serialize(ProjectSummaryHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("DemoController", doc.RootElement.GetProperty("controller").GetString());
        Assert.Equal(7,                doc.RootElement.GetProperty("controllerTags").GetInt32());
        var programs = doc.RootElement.GetProperty("programs");
        Assert.Equal(JsonValueKind.Array, programs.ValueKind);
        Assert.Equal(1, programs.GetArrayLength());
    }

    /// <summary>
    /// logix_project_summary reads only the opened/uploaded project
    /// file, never the live controller and never a pending edit. The response must
    /// declare this with source: "project-file".
    /// </summary>
    [Fact]
    public void ProjectSummaryHandler_Execute_SerializesWithSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (ProjectSummaryResult)ProjectSummaryHandler.Execute(svc);
        Assert.Equal("project-file", result.Source);

        var json = JsonSerializer.Serialize(ProjectSummaryHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);
        Assert.Equal("project-file", doc.RootElement.GetProperty("source").GetString());
    }

    // ------------------------------------------------------------------ //
    //  FindTagsHandler — GlobMatch                                         //
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("Cmd_*",          "Cmd_MotorStart",   true)]
    [InlineData("Cmd_*",          "Sim_LineSpeed",     false)]
    [InlineData("*",              "Anything",          true)]
    [InlineData("Cmd_Motor?tart", "Cmd_MotorStart",   true)]
    [InlineData("Cmd_Motor?tart", "Cmd_MotorXtart",   true)]
    [InlineData("Cmd_Motor?tart", "Cmd_MotorStart2",  false)]
    [InlineData("Safety_OK",      "Safety_OK",         true)]
    [InlineData("safety_ok",      "Safety_OK",         true)]   // case-insensitive
    [InlineData("Cmd_*",          "CMD_MOTORSTART",    true)]   // case-insensitive
    public void GlobMatch_MatchesExpected(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, FindTagsHandler.GlobMatch(pattern, value));
    }

    // ------------------------------------------------------------------ //
    //  FindTagsHandler — Execute                                           //
    // ------------------------------------------------------------------ //

    [Fact]
    public void FindTagsHandler_Execute_ReturnsErrorWhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = FindTagsHandler.Execute(svc);

        Assert.IsType<FindTagsError>(result);
    }

    [Fact]
    public void FindTagsHandler_Execute_ReturnsAllControllerTagsWithNoFilter()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc);

        // Demo.L5X has 7 controller-scoped tags and 0 program-scoped tags
        Assert.Equal(7, result.Tags.Count);
    }

    [Fact]
    public void FindTagsHandler_Execute_AllTagsHaveScopeController()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc);

        Assert.All(result.Tags, t => Assert.Equal("Controller", t.Scope));
    }

    [Fact]
    public void FindTagsHandler_Execute_FiltersByGlobNamePattern()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, namePattern: "Cmd_*");

        // Demo has: Cmd_MotorStart, Cmd_SpeedSetpoint, Cmd_Motor → 3 tags
        Assert.Equal(3, result.Tags.Count);
        Assert.All(result.Tags, t => Assert.StartsWith("Cmd_", t.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindTagsHandler_Execute_FiltersByDataType()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, dataType: "BOOL");

        // Demo BOOL tags: Cmd_MotorStart, Safety_OK, Motor_Run, SomeRandomTag → 4 tags
        Assert.Equal(4, result.Tags.Count);
        Assert.All(result.Tags, t => Assert.Equal("BOOL", t.Type));
    }

    [Fact]
    public void FindTagsHandler_Execute_FiltersByBothPatternAndDataType()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        // Cmd_* AND BOOL → only Cmd_MotorStart
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, namePattern: "Cmd_*", dataType: "BOOL");

        Assert.Single(result.Tags);
        Assert.Equal("Cmd_MotorStart", result.Tags[0].Name);
        Assert.Equal("BOOL",           result.Tags[0].Type);
        Assert.Equal("Controller",     result.Tags[0].Scope);
    }

    [Fact]
    public void FindTagsHandler_Execute_TagEntryHasNameTypeScope()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, namePattern: "Motor_Run");

        Assert.Single(result.Tags);
        var tag = result.Tags[0];
        Assert.Equal("Motor_Run",  tag.Name);
        Assert.Equal("BOOL",       tag.Type);
        Assert.Equal("Controller", tag.Scope);
        // Description may be empty string for this tag
        Assert.NotNull(tag.Description);
    }

    [Fact]
    public void FindTags_LimitAndCursor_PagesWithTruncatedAndTotal()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        // Demo.L5X has 7 tags: three pages of 3, 3, 1.
        var first = (FindTagsResult)FindTagsHandler.Execute(svc, limit: 3);
        Assert.Equal(3, first.Tags.Count);
        Assert.Equal(7, first.Total);
        Assert.True(first.Truncated);
        Assert.Equal("3", first.NextCursor);

        var second = (FindTagsResult)FindTagsHandler.Execute(svc, limit: 3, cursor: first.NextCursor);
        Assert.Equal(3, second.Tags.Count);
        Assert.True(second.Truncated);

        var last = (FindTagsResult)FindTagsHandler.Execute(svc, limit: 3, cursor: second.NextCursor);
        Assert.Single(last.Tags);
        Assert.Equal(7, last.Total);
        Assert.False(last.Truncated);
        Assert.Null(last.NextCursor);

        var all = first.Tags.Concat(second.Tags).Concat(last.Tags).Select(t => t.Name).ToList();
        Assert.Equal(7, all.Distinct().Count());

        // Default limit (200) returns everything, not truncated.
        var whole = (FindTagsResult)FindTagsHandler.Execute(svc);
        Assert.Equal(7, whole.Tags.Count);
        Assert.Equal(7, whole.Total);
        Assert.False(whole.Truncated);

        Assert.IsType<FindTagsError>(FindTagsHandler.Execute(svc, cursor: "abc"));
    }

    [Fact]
    public void FindTagsHandler_Execute_ReturnsEmptyListWhenNoMatch()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, namePattern: "NoSuchTag_*");

        Assert.Empty(result.Tags);
    }

    /// <summary>
    /// Tag descriptions are customer-authored project text.
    /// FindTagsHandler must wrap them with <c>source="tag-description"</c> so the
    /// model can distinguish project content from server-authored text.
    /// Cmd_SpeedSetpoint has description <c>range 0..1000</c> in Demo.L5X.
    /// </summary>
    [Fact]
    public void FindTagsHandler_Execute_TagDescriptionIsWrappedWithTagDescriptionSource()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, namePattern: "Cmd_SpeedSetpoint");

        Assert.Single(result.Tags);
        Assert.Equal(
            """<untrusted source="tag-description">range 0..1000</untrusted>""",
            result.Tags[0].Description);
    }

    /// <summary>
    /// Even an empty tag description is wrapped so the boundary is always present.
    /// Cmd_MotorStart has no description element in Demo.L5X.
    /// </summary>
    [Fact]
    public void FindTagsHandler_Execute_EmptyDescriptionIsWrappedWithEmptyBody()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc, namePattern: "Cmd_MotorStart");

        Assert.Single(result.Tags);
        Assert.Equal(
            """<untrusted source="tag-description"></untrusted>""",
            result.Tags[0].Description);
    }

    [Fact]
    public void FindTagsHandler_Execute_SerializesToValidJson()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = JsonSerializer.Serialize(FindTagsHandler.Execute(svc, namePattern: "Cmd_*"));
        var doc  = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("tags").ValueKind);
        Assert.Equal(3, doc.RootElement.GetProperty("tags").GetArrayLength());
    }

    /// <summary>
    /// logix_find_tags reads only the opened/uploaded project file,
    /// never the live controller and never a pending edit. The response must
    /// declare this with source: "project-file".
    /// </summary>
    [Fact]
    public void FindTagsHandler_Execute_SerializesWithSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (FindTagsResult)FindTagsHandler.Execute(svc);
        Assert.Equal("project-file", result.Source);

        var json = JsonSerializer.Serialize(FindTagsHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);
        Assert.Equal("project-file", doc.RootElement.GetProperty("source").GetString());
    }

    // ------------------------------------------------------------------ //
    //  LogixProjectTools (integration-style delegation tests)              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void LogixProjectTools_OpenProject_ReturnsValidJson()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetOpenProjectResponse(FixturePath);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("DemoController", doc.RootElement.GetProperty("controller").GetString());
    }

    [Fact]
    public void LogixProjectTools_ProjectSummary_ReturnsErrorJsonBeforeOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetProjectSummaryResponse();

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_FindTags_ReturnsAllTagsAfterOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);
        var json = tools.GetFindTagsResponse(null, null);

        var doc = JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("tags").GetArrayLength());
    }

    // ------------------------------------------------------------------ //
    //  GetRoutineHandler                                                   //
    // ------------------------------------------------------------------ //

    [Fact]
    public void GetRoutineHandler_Execute_ReturnsErrorWhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

        Assert.IsType<GetRoutineError>(result);
        var e = (GetRoutineError)result;
        Assert.Contains("logix_open_project", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRoutineHandler_Execute_ReturnsErrorWhenProgramNotFound()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GetRoutineHandler.Execute(svc, "NoSuchProgram", "MainRoutine");

        Assert.IsType<GetRoutineError>(result);
        var e = (GetRoutineError)result;
        Assert.Contains("NoSuchProgram", e.Error);
        Assert.Contains("not found", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRoutineHandler_Execute_ErrorForMissingProgramListsAvailablePrograms()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineError)GetRoutineHandler.Execute(svc, "Bogus", "MainRoutine");

        Assert.Contains("MainProgram", result.Error);
    }

    [Fact]
    public void GetRoutineHandler_Execute_ReturnsErrorWhenRoutineNotFound()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GetRoutineHandler.Execute(svc, "MainProgram", "NoSuchRoutine");

        Assert.IsType<GetRoutineError>(result);
        var e = (GetRoutineError)result;
        Assert.Contains("NoSuchRoutine", e.Error);
        Assert.Contains("not found", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRoutineHandler_Execute_ErrorForMissingRoutineListsAvailableRoutines()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineError)GetRoutineHandler.Execute(svc, "MainProgram", "Bogus");

        Assert.Contains("MainRoutine", result.Error);
    }

    [Fact]
    public void GetRoutineHandler_Execute_ReturnsResultWithCorrectProgramAndRoutineNames()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

        Assert.IsType<GetRoutineResult>(result);
        var r = (GetRoutineResult)result;
        Assert.Equal("MainProgram",  r.Program);
        Assert.Equal("MainRoutine",  r.Routine);
    }

    [Fact]
    public void GetRoutineHandler_Execute_ReturnsCorrectRungCount()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineResult)GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

        // Demo.L5X MainRoutine has two rungs (0 and 1)
        Assert.Equal(2, result.Rungs.Count);
    }

    [Fact]
    public void GetRoutineHandler_Execute_FirstRungHasCorrectNumberAndText()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineResult)GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

        var rung0 = result.Rungs[0];
        Assert.Equal(0, rung0.Number);
        Assert.Equal("XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run);", rung0.Text);
    }

    [Fact]
    public void GetRoutineHandler_Execute_FirstRungHasCorrectComment()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineResult)GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

        var rung0 = result.Rungs[0];
        // Comment is wrapped with the rung-comment provenance boundary.
        Assert.Equal(
            """<untrusted source="rung-comment">Interlock: Motor_Run coil energized when Cmd_MotorStart AND Safety_OK</untrusted>""",
            rung0.Comment);
    }

    [Fact]
    public void GetRoutineHandler_Execute_SecondRungHasCorrectNumberAndText()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineResult)GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

        var rung1 = result.Rungs[1];
        Assert.Equal(1,        rung1.Number);
        Assert.Equal("NOP();", rung1.Text);
        // Comment is wrapped with the rung-comment provenance boundary.
        Assert.Equal(
            """<untrusted source="rung-comment">Trivial rung</untrusted>""",
            rung1.Comment);
    }

    [Fact]
    public void GetRoutineHandler_Execute_EmptyRungComment_IsNotAnEmptyUntrustedWrapper()
    {
        var xml = File.ReadAllText(FixturePath)
            .Replace("<Comment><![CDATA[Trivial rung]]></Comment>", "<Comment><![CDATA[]]></Comment>");
        Assert.DoesNotContain("Trivial rung", xml);
        var path = Path.Combine(Path.GetTempPath(), $"empty-comment-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, xml);
        try
        {
            var svc = new ProjectService();
            svc.Load(path);
            var result = (GetRoutineResult)GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");

            Assert.Equal(string.Empty, result.Rungs[1].Comment);
            Assert.DoesNotContain("<untrusted source=\"rung-comment\"></untrusted>", result.Rungs[1].Comment);
            // A real comment is still wrapped.
            Assert.StartsWith("<untrusted source=\"rung-comment\">", result.Rungs[0].Comment);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void GetRoutineHandler_Execute_IsCaseInsensitiveForProgramName()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GetRoutineHandler.Execute(svc, "mainprogram", "mainroutine");

        Assert.IsType<GetRoutineResult>(result);
    }

    [Fact]
    public void GetRoutineHandler_Execute_SerializesToValidJson()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = JsonSerializer.Serialize(GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine"));
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("MainProgram",  doc.RootElement.GetProperty("program").GetString());
        Assert.Equal("MainRoutine",  doc.RootElement.GetProperty("routine").GetString());
        var rungs = doc.RootElement.GetProperty("rungs");
        Assert.Equal(JsonValueKind.Array, rungs.ValueKind);
        Assert.Equal(2, rungs.GetArrayLength());
    }

    [Fact]
    public void GetRoutineHandler_Execute_RungJsonHasExpectedKeys()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json  = JsonSerializer.Serialize(GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine"));
        var doc   = JsonDocument.Parse(json);
        var rung0 = doc.RootElement.GetProperty("rungs")[0];

        Assert.True(rung0.TryGetProperty("number",  out _));
        Assert.True(rung0.TryGetProperty("text",    out _));
        Assert.True(rung0.TryGetProperty("comment", out _));
    }

    /// <summary>
    /// logix_get_routine reads only the opened/uploaded project
    /// file, never the live controller and never a pending (unsaved) rung edit.
    /// The response must declare this with source: "project-file"; agents that
    /// need the live routine content including pending edits must use
    /// logix_export_rungs instead.
    /// </summary>
    [Fact]
    public void GetRoutineHandler_Execute_SerializesWithSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (GetRoutineResult)GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");
        Assert.Equal("project-file", result.Source);

        var json = JsonSerializer.Serialize(GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine"));
        var doc  = JsonDocument.Parse(json);
        Assert.Equal("project-file", doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void GetRoutineHandler_Execute_ErrorSerializesToJsonWithErrorKey()
    {
        var svc    = new ProjectService();
        var result = GetRoutineHandler.Execute(svc, "MainProgram", "MainRoutine");
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_GetRoutine_ReturnsErrorJsonBeforeOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetGetRoutineResponse("MainProgram", "MainRoutine");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_GetRoutine_ReturnsRungsAfterOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);
        var json = tools.GetGetRoutineResponse("MainProgram", "MainRoutine");

        var doc   = JsonDocument.Parse(json);
        var rungs = doc.RootElement.GetProperty("rungs");
        Assert.Equal(2, rungs.GetArrayLength());
    }

    // ------------------------------------------------------------------ //
    //  DiffProjectsHandler                                                 //
    // ------------------------------------------------------------------ //

    private static readonly string FixturePathB =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoB.L5X");

    [Fact]
    public void DiffProjectsHandler_Execute_ReturnsErrorForEmptyPathA()
    {
        var result = DiffProjectsHandler.Execute("", FixturePathB);

        Assert.IsType<DiffProjectsError>(result);
        var e = (DiffProjectsError)result;
        Assert.Contains("pathA", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_ReturnsErrorForEmptyPathB()
    {
        var result = DiffProjectsHandler.Execute(FixturePath, "");

        Assert.IsType<DiffProjectsError>(result);
        var e = (DiffProjectsError)result;
        Assert.Contains("pathB", e.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_ReturnsErrorForMissingFileA()
    {
        var result = DiffProjectsHandler.Execute("/does/not/exist.L5X", FixturePathB);

        Assert.IsType<DiffProjectsError>(result);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_ReturnsErrorForMissingFileB()
    {
        var result = DiffProjectsHandler.Execute(FixturePath, "/does/not/exist.L5X");

        Assert.IsType<DiffProjectsError>(result);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_ReturnsResultForValidPaths()
    {
        var result = DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        Assert.IsType<DiffProjectsResult>(result);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_IdenticalFileProducesNoDifferences()
    {
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePath);

        Assert.Empty(result.Tags);
        Assert.Empty(result.DataTypes);
        Assert.Empty(result.Routines);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DetectsRemovedTag()
    {
        // Cmd_MotorStart is in Demo.L5X (A) but not in DemoB.L5X (B)
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var removed = result.Tags.Where(t => t.Change == "removed").ToList();
        Assert.Contains(removed, e => e.Name == "Cmd_MotorStart");
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DetectsAddedTag()
    {
        // Sim_Pressure is in DemoB.L5X (B) but not in Demo.L5X (A)
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var added = result.Tags.Where(t => t.Change == "added").ToList();
        Assert.Contains(added, e => e.Name == "Sim_Pressure");
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DetectsChangedTag()
    {
        // Cmd_SpeedSetpoint changes DataType REAL → INT between A and B
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var changed = result.Tags.Where(t => t.Change == "changed").ToList();
        Assert.Contains(changed, e => e.Name == "Cmd_SpeedSetpoint");
    }

    [Fact]
    public void DiffProjectsHandler_Execute_UnchangedTagsAreNotReported()
    {
        // Safety_OK, Motor_Run, Sim_LineSpeed, Cmd_Motor are identical in both files
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var tagNames = result.Tags.Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("Safety_OK",     tagNames);
        Assert.DoesNotContain("Motor_Run",     tagNames);
        Assert.DoesNotContain("Sim_LineSpeed", tagNames);
        Assert.DoesNotContain("Cmd_Motor",     tagNames);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DetectsChangedDataType()
    {
        // MotorCmd gains a Direction member in DemoB.L5X
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var changed = result.DataTypes.Where(dt => dt.Change == "changed").ToList();
        Assert.Contains(changed, e => e.Name == "MotorCmd");
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DetectsAddedDataType()
    {
        // SpeedCmd is new in DemoB.L5X
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var added = result.DataTypes.Where(dt => dt.Change == "added").ToList();
        Assert.Contains(added, e => e.Name == "SpeedCmd");
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DetectsChangedRoutine()
    {
        // MainProgram/MainRoutine has different rung 0 text in DemoB.L5X
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        var changed = result.Routines.Where(r => r.Change == "changed").ToList();
        Assert.Contains(changed, e => e.Name == "MainProgram/MainRoutine");
    }

    [Fact]
    public void DiffProjectsHandler_Execute_TagDiffContainsExactlyThreeEntries()
    {
        // Tags: 1 removed (Cmd_MotorStart), 1 removed (SomeRandomTag), 1 added (Sim_Pressure),
        //       1 changed (Cmd_SpeedSetpoint) → 4 entries total
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        Assert.Equal(4, result.Tags.Count);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DataTypeDiffContainsExactlyTwoEntries()
    {
        // DataTypes: 1 changed (MotorCmd), 1 added (SpeedCmd)
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        Assert.Equal(2, result.DataTypes.Count);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_RoutineDiffContainsExactlyOneEntry()
    {
        // Routines: 1 changed (MainProgram/MainRoutine)
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        Assert.Single(result.Routines);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DoesNotModifyProjectServiceSingleton()
    {
        // The service should be unaffected whether loaded or not
        var svc = new ProjectService();
        svc.Load(FixturePath);

        DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        // Service still holds Demo.L5X
        Assert.True(svc.IsLoaded);
        Assert.Equal("DemoController", svc.Project!.Controller.Name);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_DoesNotLoadServiceWhenServiceIsEmpty()
    {
        var svc = new ProjectService();

        DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        // Service remains unloaded
        Assert.False(svc.IsLoaded);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_SerializesToValidJson()
    {
        var result = DiffProjectsHandler.Execute(FixturePath, FixturePathB);
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("dataTypes").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("routines").ValueKind);
    }

    [Fact]
    public void DiffProjectsHandler_Execute_EachEntryHasNameAndChangeProperties()
    {
        var result = (DiffProjectsResult)DiffProjectsHandler.Execute(FixturePath, FixturePathB);

        foreach (var entry in result.Tags.Concat(result.DataTypes).Concat(result.Routines))
        {
            Assert.NotEmpty(entry.Name);
            Assert.Contains(entry.Change, new[] { "added", "removed", "changed" });
        }
    }

    [Fact]
    public void DiffProjectsHandler_Execute_ErrorSerializesToJsonWithErrorKey()
    {
        var result = DiffProjectsHandler.Execute("", FixturePathB);
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_DiffProjects_ReturnsValidJson()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetDiffProjectsResponse(FixturePath, FixturePathB);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("tags", out _));
        Assert.True(doc.RootElement.TryGetProperty("dataTypes", out _));
        Assert.True(doc.RootElement.TryGetProperty("routines", out _));
    }

    [Fact]
    public void LogixProjectTools_DiffProjects_ReturnsErrorJsonForMissingFile()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetDiffProjectsResponse("/missing.L5X", FixturePathB);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ------------------------------------------------------------------ //
    //  CloseProjectHandler                                                //
    // ------------------------------------------------------------------ //

    [Fact]
    public void CloseProjectHandler_Execute_NoProjectOpen_ReturnsNoProjectError()
    {
        var svc     = new ProjectService();
        var session = new TagSessionService();

        var result = CloseProjectHandler.Execute(svc, session);

        Assert.IsType<CloseProjectNoProjectError>(result);
        var err = (CloseProjectNoProjectError)result;
        Assert.Contains("No project", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloseProjectHandler_Execute_NoActiveSdkSession_ClosesAndNamesPath()
    {
        // No SdkSessionInspector registered at all (core-only build/test default):
        // the guard must trivially pass and the project must be closed.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = null;
            CloseProjectHandler.SdkSessionDisposer  = null;

            var result = CloseProjectHandler.Execute(svc, session);

            Assert.IsType<CloseProjectResult>(result);
            var ok = (CloseProjectResult)result;
            Assert.Equal(FixturePath, ok.ClosedPath);
            Assert.False(ok.SdkSessionDisposed);
            Assert.False(svc.IsLoaded);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_InspectorReportsNoSession_DisposerIsNotInvoked()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = _ => null; // no active SDK session
            var disposerInvoked = false;
            CloseProjectHandler.SdkSessionDisposer = _ => disposerInvoked = true;

            var result = CloseProjectHandler.Execute(svc, session);

            Assert.IsType<CloseProjectResult>(result);
            Assert.False(((CloseProjectResult)result).SdkSessionDisposed);
            Assert.False(disposerInvoked);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_ActiveSessionNoPendingWork_DisposesSessionAndClosesProject()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector =
                _ => new SdkSessionCloseInfo(StagedTagCount: 0, HasStagedRungEdit: false);
            var disposerInvoked = false;
            CloseProjectHandler.SdkSessionDisposer = _ => disposerInvoked = true;

            var result = CloseProjectHandler.Execute(svc, session);

            Assert.IsType<CloseProjectResult>(result);
            var ok = (CloseProjectResult)result;
            Assert.True(ok.SdkSessionDisposed);
            Assert.True(disposerInvoked);
            Assert.False(svc.IsLoaded);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_Success_WritesProjectClosedAuditLine()
    {
        // The project_closed audit line must carry the closed
        // path and the sdk_session_disposed flag. Redirects AuditLogger to an
        // isolated temp directory for the duration of this test only, so
        // production/default audit locations are never touched and no state
        // leaks to other tests.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        var tempDir = Path.Combine(Path.GetTempPath(), $"close-project-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CloseProjectHandler.SdkSessionInspector =
                _ => new SdkSessionCloseInfo(StagedTagCount: 0, HasStagedRungEdit: false);
            CloseProjectHandler.SdkSessionDisposer = _ => { };
            AuditLogger.OverrideLogDir(tempDir);

            var result = CloseProjectHandler.Execute(svc, session);
            Assert.IsType<CloseProjectResult>(result);

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath));
            var line = File.ReadAllLines(auditPath).Last(l => !string.IsNullOrWhiteSpace(l));
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            Assert.Equal("project_closed", root.GetProperty("event").GetString());
            Assert.Equal(FixturePath,      root.GetProperty("path").GetString());
            Assert.True(root.GetProperty("sdk_session_disposed").GetBoolean());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_PendingStagedTags_RefusesAndNamesPendingWork()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector =
                _ => new SdkSessionCloseInfo(StagedTagCount: 3, HasStagedRungEdit: false);
            var disposerInvoked = false;
            CloseProjectHandler.SdkSessionDisposer = _ => disposerInvoked = true;

            var result = CloseProjectHandler.Execute(svc, session);

            Assert.IsType<CloseProjectPendingBatchError>(result);
            var err = (CloseProjectPendingBatchError)result;
            Assert.Contains("3", err.Pending);
            Assert.Contains("staged tag", err.Pending, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rung edit", err.Pending, StringComparison.OrdinalIgnoreCase);
            Assert.False(disposerInvoked);

            // The project must remain loaded after a refusal.
            Assert.True(svc.IsLoaded);
            Assert.Equal(FixturePath, svc.FilePath);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_PendingRungEdit_RefusesAndNamesPendingWork()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector =
                _ => new SdkSessionCloseInfo(StagedTagCount: 0, HasStagedRungEdit: true);
            var disposerInvoked = false;
            CloseProjectHandler.SdkSessionDisposer = _ => disposerInvoked = true;

            var result = CloseProjectHandler.Execute(svc, session);

            Assert.IsType<CloseProjectPendingBatchError>(result);
            var err = (CloseProjectPendingBatchError)result;
            Assert.Contains("rung edit", err.Pending, StringComparison.OrdinalIgnoreCase);
            Assert.False(disposerInvoked);
            Assert.True(svc.IsLoaded);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_PendingBatchError_HintNeverSuggestsAutomaticDiscard()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector =
                _ => new SdkSessionCloseInfo(StagedTagCount: 1, HasStagedRungEdit: true);
            CloseProjectHandler.SdkSessionDisposer = _ => { };

            var result = CloseProjectHandler.Execute(svc, session);

            Assert.IsType<CloseProjectPendingBatchError>(result);
            var err = (CloseProjectPendingBatchError)result;
            Assert.DoesNotContain("automatically discard", err.Hint, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("publish", err.Hint, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void CloseProjectHandler_Execute_ResultSerializesToJsonWithClosedPathKey()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = null;
            CloseProjectHandler.SdkSessionDisposer  = null;

            var result = CloseProjectHandler.Execute(svc, session);
            var json   = JsonSerializer.Serialize(result);
            var doc    = JsonDocument.Parse(json);

            Assert.Equal(FixturePath, doc.RootElement.GetProperty("closedPath").GetString());
            Assert.False(doc.RootElement.GetProperty("sdkSessionDisposed").GetBoolean());
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    // ------------------------------------------------------------------ //
    //  LogixProjectTools.CloseProject (logix_close_project MCP tool)      //
    // ------------------------------------------------------------------ //

    [Fact]
    public void LogixProjectTools_CloseProject_ReturnsErrorJsonWhenNoProjectOpen()
    {
        var svc     = new ProjectService();
        var session = new TagSessionService();
        var tools   = new LogixProjectTools(svc, session);

        var json = tools.CloseProject();
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_CloseProject_ReturnsClosedPathJsonOnSuccess()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var session = new TagSessionService();
        var tools   = new LogixProjectTools(svc, session);

        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            CloseProjectHandler.SdkSessionInspector = null;
            CloseProjectHandler.SdkSessionDisposer  = null;

            var json = tools.CloseProject();
            var doc  = JsonDocument.Parse(json);

            Assert.Equal(FixturePath, doc.RootElement.GetProperty("closedPath").GetString());
            Assert.False(svc.IsLoaded);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public void LogixProjectTools_CloseProject_SingleArgConstructor_StillWorksForNoProjectCase()
    {
        // The single-argument constructor (used by ~15 other call sites) must keep
        // working now that a second, tag-session-aware overload exists.
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);

        var json = tools.CloseProject();
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
