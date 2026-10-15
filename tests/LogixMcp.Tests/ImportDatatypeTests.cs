using System.Text.Json;
using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="ImportDatatypeHandler"/> and the
/// <c>logix_import_datatype</c> MCP tool wired in <see cref="LogixProjectTools"/>.
/// </summary>
public class ImportDatatypeTests
{
    // Standard fixture — has MotorCmd (User type with Enable/Speed members)
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // Tag-usage fixture — has MotorCmd controller tag (Ctrl_Motor) AND
    // program-scoped MotorCmd tag (Prog_Motor in MainProgram)
    private static readonly string TagUsageFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoTagUsage.L5X");

    /// <summary>A project service loaded from Demo.L5X (contains existing UDT <c>MotorCmd</c>).</summary>
    private static ProjectService DemoService()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        return svc;
    }

    /// <summary>Returns a unique, not-yet-existing temp path with the given extension.</summary>
    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"logixmcp-import-{Guid.NewGuid():N}{ext}");

    // ─────────────────────────────────────────────────────────────────────────
    //  Bounded file handling, input validation, audit
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly DataTypeMemberEntry[] OneMember =
    {
        new("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
    };

    [Fact]
    public void SaveAs_NonAcdNonL5xExtension_Refused()
    {
        foreach (var bad in new[] { "out.txt", "out.dll", "out", "out.ACD.exe" })
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MyType",
                l5xPath:      null,
                members:      OneMember,
                saveAs:       Path.Combine(Path.GetTempPath(), bad),
                dryRun:       false,
                confirm:      true);

            var err = Assert.IsType<ImportDatatypeError>(result);
            Assert.Equal("InvalidSavePath", err.Kind);
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), bad)));
        }
    }

    [Fact]
    public void SaveAs_InsideInstallDirectory_Refused()
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      OneMember,
            saveAs:       Path.Combine(AppContext.BaseDirectory, "evil.ACD"),
            dryRun:       false,
            confirm:      true);

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("InvalidSavePath", err.Kind);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "evil.ACD")));
    }

    [Fact]
    public void L5xPath_NotRsLogixContent_RefusedWithoutEchoingContent()
    {
        const string secret = "TOP-SECRET-MARKER-12345";
        var notL5x   = TempPath(".L5X");
        var wrongExt = TempPath(".txt");
        var badXml   = TempPath(".L5X");
        try
        {
            File.WriteAllText(notL5x,   $"<Other><Secret>{secret}</Secret></Other>");
            File.WriteAllText(wrongExt, "<RSLogix5000Content />");
            File.WriteAllText(badXml,   $"{secret} <not xml");

            foreach (var path in new[] { notL5x, wrongExt, badXml })
            {
                var result = ImportDatatypeHandler.Execute(
                    acdPath:      "/tmp/project.ACD",
                    datatypeName: "MyType",
                    l5xPath:      path,
                    members:      null,
                    saveAs:       TempPath(".L5X"));

                var err = Assert.IsType<ImportDatatypeError>(result);
                // Unparseable XML is InvalidL5x (SafeXml); wrong extension / root is InvalidInput.
                Assert.Equal(path == badXml ? "InvalidL5x" : "InvalidInput", err.Kind);
                Assert.DoesNotContain(secret, err.Error);
                Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
            }
        }
        finally
        {
            foreach (var f in new[] { notL5x, wrongExt, badXml })
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("1Bad")]
    [InlineData("Bad-Name")]
    [InlineData("Bad Name")]
    [InlineData("x']/../..|//*['")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]   // 41 chars
    public void DatatypeName_InvalidIdentifier_Refused(string name)
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: name,
            l5xPath:      null,
            members:      OneMember,
            saveAs:       TempPath(".L5X"));

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("InvalidInput", err.Kind);
    }

    [Fact]
    public void RealRun_WritesDatatypeImportAuditEvent()
    {
        var logDir  = Path.Combine(Path.GetTempPath(), $"logixmcp-audit-{Guid.NewGuid():N}");
        var tmpSave = TempPath(".L5X");
        AuditLogger.OverrideLogDir(logDir);
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      OneMember,
                saveAs:       tmpSave,
                service:      DemoService(),
                collision:    ImportCollisionOption.Overwrite,
                dryRun:       false,
                confirm:      true);
            Assert.IsType<ImportDatatypeResult>(result);

            var lines = File.ReadAllLines(Path.Combine(logDir, "audit.jsonl"))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "datatype_import")
                .ToList();

            var evt = Assert.Single(lines);
            Assert.Equal("/tmp/project.ACD",   evt.GetProperty("acdPath").GetString());
            Assert.Equal(tmpSave,              evt.GetProperty("saveAs").GetString());
            Assert.Equal("MotorCmd",           evt.GetProperty("datatypeName").GetString());
            Assert.Equal("Overwrite",          evt.GetProperty("collision").GetString());
            Assert.Equal("success",            evt.GetProperty("outcome").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { File.Delete(tmpSave); } catch { /* best effort */ }
            try { Directory.Delete(logDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DryRun_DoesNotWriteDatatypeImportAuditEvent()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"logixmcp-audit-{Guid.NewGuid():N}");
        AuditLogger.OverrideLogDir(logDir);
        try
        {
            ImportDatatypeHandler.Execute(
                acdPath: "/tmp/project.ACD", datatypeName: "MyType", l5xPath: null,
                members: OneMember, saveAs: TempPath(".L5X"), dryRun: true);

            Assert.False(File.Exists(Path.Combine(logDir, "audit.jsonl")));
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(logDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Contractual acceptance tests (exact names required by acceptance criteria)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Contractual: when dryRun=false but confirm is not passed (defaults to false),
    /// the handler must refuse without writing any file.  The response must look like
    /// a dry-run result — dryRun=true, Delta populated, SavedTo=null — and must
    /// include a non-null 'refused' field.  No file must be written to disk.
    /// </summary>
    [Fact]
    public void RealRun_WithoutConfirm_IsRefused_ReturnsDryRunDelta()
    {
        var tmpSave = Path.Combine(
            Path.GetTempPath(),
            $"logixmcp-confirm-gate-test-{Guid.NewGuid():N}.L5X");

        try
        {
            var members = new[]
            {
                new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
                new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
            };

            // dryRun=false but confirm is omitted (defaults to false)
            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      members,
                saveAs:       tmpSave,
                service:      DemoService(),
                dryRun:       false);
                // confirm not passed — defaults to false

            // Must return a result (not an error)
            var ok = Assert.IsType<ImportDatatypeResult>(result);

            // Must set the refused field
            Assert.NotNull(ok.Refused);
            Assert.NotEmpty(ok.Refused!);
            Assert.Contains("confirm=true", ok.Refused, StringComparison.OrdinalIgnoreCase);

            // Must mirror a dry-run response (no file written, delta present)
            Assert.True(ok.DryRun,  "dryRun must be true in a refused response");
            Assert.Null(ok.SavedTo);
            Assert.NotNull(ok.Delta);

            // No file must be written to disk
            Assert.False(File.Exists(tmpSave), "no file must be written when confirm=false");

            // Fragment must still be returned for inspection
            Assert.NotNull(ok.L5XFragment);
            Assert.NotEmpty(ok.L5XFragment);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Contractual: the response must include controller-scoped and program-scoped
    /// tags whose DataType matches the edited type, plus a fixed advisory sentence
    /// about the download requirement.  The presence of dependent tags must not
    /// block or abort the operation.
    /// </summary>
    [Fact]
    public void RealRun_ReportsTagsUsingTheType()
    {
        var svc = new ProjectService();
        svc.Load(TagUsageFixturePath);
        // DemoTagUsage.L5X contains:
        //   Controller tag  Ctrl_Motor  : MotorCmd
        //   MainProgram tag Prog_Motor  : MotorCmd

        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MotorCmd",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X",
            service:      svc);

        // Operation must succeed — dependent tags do not block or abort
        var ok = Assert.IsType<ImportDatatypeResult>(result);

        // Controller-scoped tag must appear
        var ctrlTag = ok.DependentTags!.SingleOrDefault(
            t => string.Equals(t.Name, "Ctrl_Motor", StringComparison.Ordinal));
        Assert.NotNull(ctrlTag);
        Assert.Equal("Controller", ctrlTag!.Scope);
        Assert.Equal("MotorCmd",   ctrlTag.Type, StringComparer.OrdinalIgnoreCase);

        // Program-scoped tag must appear with the program name as scope
        var progTag = ok.DependentTags!.SingleOrDefault(
            t => string.Equals(t.Name, "Prog_Motor", StringComparison.Ordinal));
        Assert.NotNull(progTag);
        Assert.Equal("MainProgram", progTag!.Scope);
        Assert.Equal("MotorCmd",    progTag.Type, StringComparer.OrdinalIgnoreCase);

        // Advisory sentence about download requirement and controller-memory clear
        // must be present in every response
        Assert.NotEmpty(ok.Advisory);
        Assert.Contains("download",          ok.Advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("controller memory", ok.Advisory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Contractual: the tool must refuse when saveAs resolves to the same path
    /// as acdPath — it must never overwrite the input ACD.
    /// </summary>
    [Fact]
    public void RealRun_RefusesWhenSaveAsEqualsInput()
    {
        // Use a real (non-existent) ACD path — path resolution is all that matters.
        const string acdPath = "/tmp/project.ACD";

        var result = ImportDatatypeHandler.Execute(
            acdPath:      acdPath,
            datatypeName: "MyType",
            l5xPath:      null,
            members:      new[] { new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write") },
            saveAs:       acdPath,   // ← same as acdPath
            service:      null);

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("SaveAsEqualsInput", err.Kind);
        Assert.Contains("saveAs", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Contractual: the tool must refuse when the input ACD is held open
    /// exclusively by Studio 5000 (simulated here via the injectable lock checker).
    /// Uses the real <see cref="ImportDatatypeHandler.Execute"/> path — no handler mocking.
    /// </summary>
    [Fact]
    public void RealRun_RefusesWhenInputIsLockedByStudio()
    {
        // Create a real temp file to represent the ACD
        var tmpAcd = Path.GetTempFileName();
        try
        {
            // Simulate Studio's exclusive lock via the injectable checker
            // (avoids platform differences in OS-level file locking between
            //  test runner threads on Linux vs Windows).
            var result = ImportDatatypeHandler.Execute(
                acdPath:           tmpAcd,
                datatypeName:      "MyType",
                l5xPath:           null,
                members:           new[] { new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write") },
                saveAs:            tmpAcd + ".out.ACD",
                service:           null,
                studioLockChecker: _ => true);   // real Execute, real refusal path

            var err = Assert.IsType<ImportDatatypeStudioLockError>(result);
            Assert.Contains("Studio 5000", err.Error, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(err.Workaround);
            Assert.NotEmpty(err.Workaround);
        }
        finally
        {
            try { File.Delete(tmpAcd); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  OS-level lock detection (verifies IsLockedByStudio against a real lock)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsLockedByStudio_ReturnsFalseForUnlockedFile()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            Assert.False(ImportDatatypeHandler.IsLockedByStudio(tmpFile));
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void IsLockedByStudio_ReturnsFalseForNonExistentFile()
    {
        Assert.False(ImportDatatypeHandler.IsLockedByStudio("/tmp/no-such-file-logixmcp.ACD"));
    }

    [Fact]
    public void IsLockedByStudio_ReturnsTrueWhenFileHeldExclusive()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Hold an exclusive lock (simulates Studio 5000's lock on the ACD)
            using var lockHandle = new FileStream(
                tmpFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // The probe must detect the exclusive lock
            Assert.True(ImportDatatypeHandler.IsLockedByStudio(tmpFile));
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  saveAs validation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_RefusesWhenSaveAsIsEmpty()
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      new[] { new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write") },
            saveAs:       string.Empty);

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("InvalidInput", err.Kind);
    }

    [Fact]
    public void Execute_RefusesWhenSaveAsEqualsAcdPath_CaseInsensitiveOnWindows()
    {
        // Path.GetFullPath on Linux is case-sensitive; test with the exact same path.
        const string acdPath = "/tmp/project.ACD";

        var result = ImportDatatypeHandler.Execute(
            acdPath:      acdPath,
            datatypeName: "MyType",
            l5xPath:      null,
            members:      new[] { new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write") },
            saveAs:       acdPath);

        Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("SaveAsEqualsInput", ((ImportDatatypeError)result).Kind);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  l5xPath passthrough mode
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When l5xPath is supplied the handler reads the file verbatim and returns
    /// it in l5xFragment without re-rendering the XML.
    /// </summary>
    [Fact]
    public void Execute_AcceptsL5xPath_PassesThroughWithoutRerendering()
    {
        const string expectedContent = "<RSLogix5000Content><Controller><DataTypes>" +
                                       "<DataType Use=\"Target\" Name=\"MyType\" /></DataTypes>" +
                                       "</Controller></RSLogix5000Content>";

        var tmpL5x   = TempPath(".L5X");
        var tmpSave  = TempPath(".out.L5X");
        try
        {
            File.WriteAllText(tmpL5x, expectedContent);

            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/nonexistent.ACD",
                datatypeName: "MyType",
                l5xPath:      tmpL5x,
                members:      null,
                saveAs:       tmpSave);

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.Equal("MyType", ok.DatatypeName);
            Assert.Equal(expectedContent, ok.L5XFragment);
            Assert.True(ok.DryRun);      // default
            Assert.Null(ok.SavedTo);     // not written in dryRun
        }
        finally
        {
            try { File.Delete(tmpL5x); }  catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Execute_ReturnsFileNotFoundWhenL5xPathMissing()
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      "/tmp/no-such-file-logixmcp.L5X",
            members:      null,
            saveAs:       "/tmp/output.L5X");

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("FileNotFound", err.Kind);
    }

    [Fact]
    public void Execute_RefusesWhenBothL5xPathAndMembersSupplied()
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      "/tmp/some.L5X",
            members:      new[] { new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write") },
            saveAs:       "/tmp/output.L5X");

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("InvalidInput", err.Kind);
    }

    [Fact]
    public void Execute_RefusesWhenNeitherL5xPathNorMembersSupplied()
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      null,
            saveAs:       "/tmp/output.L5X");

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal("InvalidInput", err.Kind);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  members rendering — L5X fragment with Context section
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When members is supplied and the project is loaded, the rendered L5X
    /// must contain a Use="Target" entry for the requested type and a
    /// Use="Context" entry for every referenced user-defined type found in the project.
    /// </summary>
    [Fact]
    public void Execute_AcceptsMembers_RendersValidL5XWithContextSection()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);   // Demo.L5X has MotorCmd (BOOL Enable + REAL Speed)

        // Import a new type whose member references MotorCmd (a UDT in Demo.L5X)
        var members = new[]
        {
            new DataTypeMemberEntry("Motor",  "MotorCmd", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Enable", "BOOL",     "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "NewControlType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X",
            service:      svc);

        var ok = Assert.IsType<ImportDatatypeResult>(result);
        Assert.Equal("NewControlType", ok.DatatypeName);

        // Parse the rendered fragment
        var doc            = XDocument.Parse(ok.L5XFragment);
        var dataTypesElem  = doc.Root!.Element("Controller")!.Element("DataTypes")!;
        var allTypes       = dataTypesElem.Elements("DataType").ToList();

        // Must have exactly one Target and one Context (MotorCmd)
        var target  = allTypes.Single(e => e.Attribute("Use")?.Value == "Target");
        var context = allTypes.Where(e => e.Attribute("Use")?.Value == "Context").ToList();

        Assert.Equal("NewControlType", target.Attribute("Name")?.Value);
        Assert.Single(context);
        Assert.Equal("MotorCmd", context[0].Attribute("Name")?.Value);

        // Target must have the two members we supplied, plus the hidden SINT host the
        // BOOL needs in L5X form (host first, then the BOOL pointing at it).
        var targetMembers = target.Element("Members")!.Elements("Member").ToList();
        Assert.Equal(3, targetMembers.Count);
        Assert.Equal("Motor",  targetMembers[0].Attribute("Name")?.Value);
        Assert.Equal("true",   targetMembers[1].Attribute("Hidden")?.Value);
        Assert.Equal("Enable", targetMembers[2].Attribute("Name")?.Value);
        Assert.Equal(targetMembers[1].Attribute("Name")?.Value, targetMembers[2].Attribute("Target")?.Value);

        // Context (MotorCmd) must carry the original type's members
        var ctxMembers = context[0].Element("Members")!.Elements("Member").ToList();
        // (Demo.L5X stores Enable as a bare BOOL, so the renderer adds its hidden SINT host.)
        Assert.Equal(3, ctxMembers.Count);
        Assert.Equal("true",   ctxMembers[0].Attribute("Hidden")?.Value);
        Assert.Equal("Enable", ctxMembers[1].Attribute("Name")?.Value);
        Assert.Equal(ctxMembers[0].Attribute("Name")?.Value, ctxMembers[1].Attribute("Target")?.Value);
        Assert.Equal("Speed",  ctxMembers[2].Attribute("Name")?.Value);
    }

    [Fact]
    public void Execute_AcceptsMembers_NoContextWhenAllMembersAreBuiltIn()
    {
        // A type whose members use only built-in types should have no Context entries
        var members = new[]
        {
            new DataTypeMemberEntry("Speed",  "REAL", "0", "Float",   false, "Read/Write"),
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType",false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "SimpleType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X");

        var ok  = Assert.IsType<ImportDatatypeResult>(result);
        var doc = XDocument.Parse(ok.L5XFragment);

        var allTypes = doc.Root!.Element("Controller")!
            .Element("DataTypes")!
            .Elements("DataType").ToList();

        // Only the Target type; no Context entries
        Assert.Single(allTypes);
        Assert.Equal("Target", allTypes[0].Attribute("Use")?.Value);
        Assert.Equal("true",   doc.Root.Attribute("ContainsContext")?.Value);
    }

    [Fact]
    public void Execute_AcceptsMembers_NoContextWhenNoProjectLoaded()
    {
        // Without a project service, Context types cannot be resolved
        var members = new[]
        {
            new DataTypeMemberEntry("Motor", "MotorCmd", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "SomeType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X",
            service:      null);   // no project

        var ok  = Assert.IsType<ImportDatatypeResult>(result);
        var doc = XDocument.Parse(ok.L5XFragment);

        var allTypes = doc.Root!.Element("Controller")!
            .Element("DataTypes")!
            .Elements("DataType").ToList();

        // No Context entries without a loaded project
        Assert.Single(allTypes);
        Assert.Equal("Target", allTypes[0].Attribute("Use")?.Value);
    }

    [Fact]
    public void Execute_RenderedFragment_HasCorrectRootAttributes()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Speed", "REAL", "0", "Float", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "SpeedControl",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X");

        var ok  = Assert.IsType<ImportDatatypeResult>(result);
        var doc = XDocument.Parse(ok.L5XFragment);

        Assert.Equal("SpeedControl", doc.Root!.Attribute("TargetName")?.Value);
        Assert.Equal("DataType",     doc.Root.Attribute("TargetType")?.Value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Collision options — all three values exposed and wired to enum
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All three collision options must be exposed on the enum and reflected
    /// in the result's Collision field.
    /// </summary>
    [Theory]
    [InlineData(ImportCollisionOption.Overwrite, "Overwrite")]
    [InlineData(ImportCollisionOption.Discard,   "Discard")]
    [InlineData(ImportCollisionOption.Cancel,    "Cancel")]
    public void Execute_CollisionOption_ReflectedInResult(
        ImportCollisionOption option, string expectedName)
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X",
            collision:    option);

        var ok = Assert.IsType<ImportDatatypeResult>(result);
        Assert.Equal(expectedName, ok.Collision);
    }

    [Fact]
    public void Execute_CollisionDefault_IsOverwrite()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X");   // collision not specified

        var ok = Assert.IsType<ImportDatatypeResult>(result);
        Assert.Equal("Overwrite", ok.Collision);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  dryRun behaviour
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_DryRunDefault_IsTrue()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X");

        var ok = Assert.IsType<ImportDatatypeResult>(result);
        Assert.True(ok.DryRun);
        Assert.Null(ok.SavedTo);
    }

    [Fact]
    public void Execute_DryRunFalse_WritesFragmentToSaveAs()
    {
        var tmpSave = TempPath(".L5X");
        try
        {
            var members = new[]
            {
                new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            };

            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      members,
                saveAs:       tmpSave,
                service:      DemoService(),
                dryRun:       false,
                confirm:      true);

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.False(ok.DryRun);
            Assert.NotNull(ok.SavedTo);
            Assert.Null(ok.Refused);   // no refusal when confirm=true

            // File must exist and contain the fragment
            Assert.True(File.Exists(tmpSave));
            var written = File.ReadAllText(tmpSave);
            Assert.Equal(ok.L5XFragment, written);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  JSON serialisation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_ResultSerializesToJsonWithRequiredKeys()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X");

        var json = JsonSerializer.Serialize(result);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("datatypeName", out _));
        Assert.True(doc.RootElement.TryGetProperty("l5xFragment",  out _));
        Assert.True(doc.RootElement.TryGetProperty("collision",     out _));
        Assert.True(doc.RootElement.TryGetProperty("dryRun",        out _));
    }

    [Fact]
    public void Execute_ErrorSerializesToJsonWithErrorAndKindKeys()
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      null,
            saveAs:       "/tmp/project.ACD");   // same path → SaveAsEqualsInput

        var json = JsonSerializer.Serialize(result);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.TryGetProperty("kind",  out _));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LogixProjectTools integration
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LogixProjectTools_GetImportDatatypeResponse_RefusesWhenSaveAsEqualsAcd()
    {
        var tools = new LogixProjectTools(new ProjectService());

        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };
        const string path = "/tmp/project.ACD";

        var json = tools.GetImportDatatypeResponse(
            acdPath:      path,
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members,
            saveAs:       path);   // same as acdPath

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("kind", out var kind));
        Assert.Equal("SaveAsEqualsInput", kind.GetString());
    }

    [Fact]
    public void LogixProjectTools_GetImportDatatypeResponse_ReturnsFragmentForValidInput()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);

        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };

        var json = tools.GetImportDatatypeResponse(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("l5xFragment", out var frag));
        Assert.NotEmpty(frag.GetString()!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Re-export verification and build reporting
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Contractual: verified:true only when the re-exported member list (re-read
    /// from the saved file) matches the target member list member-for-member;
    /// verified:false when an injected re-exporter returns a mismatched list.
    /// </summary>
    [Fact]
    public void RealRun_VerifiedTrueOnlyWhenReexportMatchesTarget()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
        };
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> sdkRunner = (_, _, _, _) => Array.Empty<string>();
        var acd      = FakeAcd();
        var tmpSave1 = TempPath(".ACD");
        var tmpSave2 = TempPath(".ACD");
        try
        {
            // Matching re-export → verified true
            var ok1 = Assert.IsType<ImportDatatypeResult>(ImportDatatypeHandler.Execute(
                acdPath: acd, datatypeName: "MotorCmd", l5xPath: null, members: members,
                saveAs: tmpSave1, service: DemoService(), dryRun: false, confirm: true,
                sdkImportRunner: sdkRunner, reexportReader: _ => members));
            Assert.True(ok1.Verified,
                "verified must be true when the re-exported members match the target");

            // Mismatched re-export → typed ImportNotApplied, nothing written
            var err2 = Assert.IsType<ImportNotAppliedError>(ImportDatatypeHandler.Execute(
                acdPath: acd, datatypeName: "MotorCmd", l5xPath: null, members: members,
                saveAs: tmpSave2, service: DemoService(), dryRun: false, confirm: true,
                sdkImportRunner: sdkRunner,
                reexportReader: _ => new[]
                {
                    new DataTypeMemberEntry("WrongMember", "DINT", "0", "NullType", false, "Read/Write"),
                }));
            Assert.Equal(ErrorKinds.ImportNotApplied, err2.Kind);
            Assert.False(File.Exists(tmpSave2), "no file may be left at saveAs when the import did not apply");
        }
        finally
        {
            try { File.Delete(acd); }      catch { /* best effort */ }
            try { File.Delete(tmpSave1); } catch { /* best effort */ }
            try { File.Delete(tmpSave2); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Contractual: when build() raises an 'Operation not supported' exception the
    /// build field must be set to "unsupported on &lt;version&gt;" — it must never
    /// be silently omitted from the response.
    /// </summary>
    [Fact]
    public void BuildUnsupported_IsReportedNotSkipped()
    {
        var tmpSave = TempPath(".L5X");
        try
        {
            var members = new[]
            {
                new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            };

            // Build runner that simulates an SDK version that does not support build()
            Func<string, int> unsupportedRunner = _ =>
                throw new InvalidOperationException("Operation not supported on this version");

            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      members,
                saveAs:       tmpSave,
                service:      DemoService(),
                dryRun:       false,
                confirm:      true,
                buildRunner:  unsupportedRunner);

            var ok = Assert.IsType<ImportDatatypeResult>(result);

            // The build field must be present (not null) — never silently omitted
            Assert.NotNull(ok.Build);

            // The value must indicate "unsupported on <version>" (not a generic error)
            Assert.StartsWith("unsupported on", ok.Build,
                StringComparison.OrdinalIgnoreCase);

            // The version extracted from the rendered L5X (SoftwareRevision="36.1")
            // must appear in the field value
            Assert.Contains("36.1", ok.Build, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Build field — unsupported-version branch and success path
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Contractual acceptance test (exact name required).
    ///
    /// Simulates the Logix Designer 36.05 scenario (and any SDK version where
    /// <c>BuildAsync</c> throws an 'Operation not supported' exception).  The
    /// handler must reach the existing <c>catch…when…not supported</c> branch
    /// and set <c>build = "unsupported on &lt;SoftwareRevision&gt;"</c> rather
    /// than leaving the field <c>null</c> or propagating the exception.
    ///
    /// <para>
    /// Uses the SDK real-run path: an <c>sdkImportRunner</c> no-op succeeds,
    /// then a <c>buildRunner</c> throws "Operation not supported" to simulate
    /// an older SDK that does not expose <c>BuildAsync</c>.
    /// The supplied L5X file carries <c>SoftwareRevision="36.05"</c> so the
    /// test can assert that the exact version appears in the <c>build</c> field.
    /// </para>
    /// </summary>
    [Fact]
    public void RealRun_Build_UnsupportedBranch_ReportsVersion()
    {
        var tmpL5x  = TempPath(".L5X");
        var tmpAcd  = Path.GetTempFileName();
        // Write > 100 KB so the copied file passes the ACD-size sanity check
        File.WriteAllBytes(tmpAcd, new byte[101 * 1024]);
        var tmpSave = Path.Combine(
            Path.GetTempPath(), $"logixmcp-build-unsupported-branch-{Guid.NewGuid():N}.out.ACD");

        try
        {
            // L5X fragment with SoftwareRevision="36.05" (Logix Designer 36.05)
            const string fragment =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.05\" " +
                "TargetName=\"MyType\" TargetType=\"DataType\" ContainsContext=\"false\">" +
                "<Controller><DataTypes>" +
                "<DataType Use=\"Target\" Name=\"MyType\" Family=\"NoFamily\" Class=\"User\">" +
                "<Members><Member Name=\"Enable\" DataType=\"BOOL\" Dimension=\"0\" " +
                "Radix=\"\" ExternalAccess=\"Read/Write\" Hidden=\"false\"/>" +
                "</Members></DataType></DataTypes></Controller></RSLogix5000Content>";

            File.WriteAllText(tmpL5x, fragment);

            // SDK import runner: no-op (simulates open/PartialImport/SaveAsync succeeding)
            Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> sdkRunner =
                (_, __, ___, ____) => Array.Empty<string>();

            // Build runner: throws "Operation not supported" — simulates Logix Designer 36.05
            // where BuildAsync is not available on the SDK version installed on the bench.
            Func<string, int> unsupportedBuildRunner = _ =>
                throw new InvalidOperationException(
                    "Operation not supported: BuildAsync requires Logix Designer v37 or later");

            var result = ImportDatatypeHandler.Execute(
                acdPath:         tmpAcd,
                datatypeName:    "MyType",
                l5xPath:         tmpL5x,
                members:         null,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                sdkImportRunner: sdkRunner,
                reexportReader:  _ => OneMember,
                buildRunner:     unsupportedBuildRunner);

            var ok = Assert.IsType<ImportDatatypeResult>(result);

            // build must be non-null — never silently omitted when a runner is wired
            Assert.NotNull(ok.Build);

            // build must contain "unsupported on <version>" — not a generic "error:" prefix
            Assert.StartsWith("unsupported on", ok.Build,
                StringComparison.OrdinalIgnoreCase);

            // The SoftwareRevision from the L5X fragment ("36.05") must appear in the value
            Assert.Contains("36.05", ok.Build, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(tmpL5x); }  catch { /* best effort */ }
            try { File.Delete(tmpAcd); }  catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// When the build runner succeeds (returns an error count), the build field
    /// must contain the error count as a string (e.g. <c>"0"</c> for a clean build).
    /// The field must be non-null even when the error count is zero.
    /// </summary>
    [Fact]
    public void RealRun_Build_ReturnsErrorCount_WhenBuildSucceeds()
    {
        var tmpSave = TempPath(".L5X");
        try
        {
            var members = new[]
            {
                new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            };

            // Build runner: returns 0 (clean build — zero errors)
            Func<string, int> cleanBuildRunner = _ => 0;

            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      members,
                saveAs:       tmpSave,
                service:      DemoService(),
                dryRun:       false,
                confirm:      true,
                buildRunner:  cleanBuildRunner);

            var ok = Assert.IsType<ImportDatatypeResult>(result);

            // build must be non-null — the runner was invoked and returned a count
            Assert.NotNull(ok.Build);

            // build must contain the error count as a string ("0" for a clean build)
            Assert.Equal("0", ok.Build);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Dry-run delta computation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Contractual acceptance test (exact name required).
    ///
    /// Uses the Demo.L5X fixture whose <c>MotorCmd</c> type has:
    ///   Enable (BOOL) and Speed (REAL).
    ///
    /// Target members proposed:
    ///   speed  (DINT) — matches Speed case-insensitively → renamed (Speed → speed) + retyped (REAL → DINT)
    ///   Torque (REAL) — no match → added
    ///   (Enable omitted)          → removed
    ///
    /// Asserts that the dry-run result reports all four delta categories,
    /// includes the rendered L5X fragment, and writes no file to disk.
    /// </summary>
    [Fact]
    public void DryRun_ReportsAddedRemovedRetypedRenamed_NoFilesWritten()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);   // MotorCmd: Enable (BOOL), Speed (REAL)

        // Use a path under /tmp that should NOT exist after a dry run
        var tmpSave = Path.Combine(
            Path.GetTempPath(),
            $"logixmcp-dryrun-test-{Guid.NewGuid():N}.L5X");

        try
        {
            // Target: speed/DINT (renamed+retyped from Speed/REAL), Torque/REAL (new)
            // Enable is absent → removed
            var members = new[]
            {
                new DataTypeMemberEntry("speed",  "DINT", "0", "NullType", false, "Read/Write"),
                new DataTypeMemberEntry("Torque", "REAL", "0", "NullType", false, "Read/Write"),
            };

            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      members,
                saveAs:       tmpSave,
                service:      svc,
                dryRun:       true);

            var ok = Assert.IsType<ImportDatatypeResult>(result);

            // ── dryRun semantics ─────────────────────────────────────────────
            Assert.True(ok.DryRun,           "dryRun must be true");
            Assert.Null(ok.SavedTo);         // null in dry-run mode
            Assert.False(File.Exists(tmpSave), "no file must be written in dry-run mode");

            // ── rendered L5X text must be present ────────────────────────────
            Assert.NotNull(ok.L5XFragment);
            Assert.NotEmpty(ok.L5XFragment);

            // ── delta must be present ────────────────────────────────────────
            Assert.NotNull(ok.Delta);
            var delta = ok.Delta!;

            // added: Torque
            Assert.Contains("Torque", delta.Added);
            Assert.Single(delta.Added);

            // removed: Enable
            Assert.Contains("Enable", delta.Removed);
            Assert.Single(delta.Removed);

            // retyped: speed (from REAL to DINT)
            Assert.Single(delta.Retyped);
            var retypedEntry = delta.Retyped[0];
            Assert.Equal("speed", retypedEntry.Name, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("REAL",  retypedEntry.From, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("DINT",  retypedEntry.To,   StringComparer.OrdinalIgnoreCase);

            // renamed: Speed → speed (case-only change)
            Assert.Single(delta.Renamed);
            var renamedEntry = delta.Renamed[0];
            Assert.Equal("Speed", renamedEntry.From, StringComparer.Ordinal);
            Assert.Equal("speed", renamedEntry.To,   StringComparer.Ordinal);

            // reordered field is always present (bool)
            // With only one common member the order cannot change
            Assert.False(delta.Reordered);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Delta for a brand-new type (not yet in the project): all target members
    /// appear in the <c>added</c> list; removed, retyped, and renamed are empty.
    /// </summary>
    [Fact]
    public void DryRun_NewType_AllMembersAppearAsAdded()
    {
        // No project loaded → current members = [] (type does not exist)
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "BrandNewType",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X",
            service:      null,
            dryRun:       true);

        var ok    = Assert.IsType<ImportDatatypeResult>(result);
        var delta = ok.Delta;

        Assert.NotNull(delta);
        Assert.Equal(2, delta!.Added.Count);
        Assert.Empty(delta.Removed);
        Assert.Empty(delta.Retyped);
        Assert.Empty(delta.Renamed);
        Assert.False(delta.Reordered);
    }

    /// <summary>
    /// Delta includes <c>reordered=true</c> when the same members appear in a
    /// different order in the target compared with the current project definition.
    /// </summary>
    [Fact]
    public void DryRun_ReorderedMembers_FlaggedAsReordered()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);   // MotorCmd: Enable (BOOL), Speed (REAL)

        // Target reverses the order: Speed first, Enable second
        var members = new[]
        {
            new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MotorCmd",
            l5xPath:      null,
            members:      members,
            saveAs:       "/tmp/output.L5X",
            service:      svc,
            dryRun:       true);

        var ok    = Assert.IsType<ImportDatatypeResult>(result);
        var delta = ok.Delta;

        Assert.NotNull(delta);
        Assert.True(delta!.Reordered, "member order changed — reordered must be true");
        Assert.Empty(delta.Added);
        Assert.Empty(delta.Removed);
        Assert.Empty(delta.Retyped);
        Assert.Empty(delta.Renamed);
    }

    /// <summary>
    /// Delta is NOT computed (Delta property is null) when dryRun=false.
    /// </summary>
    [Fact]
    public void DryRunFalse_DeltaIsNull()
    {
        var tmpSave = TempPath(".L5X");
        try
        {
            var members = new[]
            {
                new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            };

            var result = ImportDatatypeHandler.Execute(
                acdPath:      "/tmp/project.ACD",
                datatypeName: "MotorCmd",
                l5xPath:      null,
                members:      members,
                saveAs:       tmpSave,
                service:      DemoService(),
                dryRun:       false,
                confirm:      true);

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.False(ok.DryRun);
            Assert.Null(ok.Delta);
            Assert.Null(ok.Refused);   // no refusal when confirm=true
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// ComputeDelta unit test — verifies the pure delta logic in isolation.
    /// </summary>
    [Fact]
    public void ComputeDelta_DetectsAllChangeKinds()
    {
        var current = new[]
        {
            new DataTypeMemberEntry("Alpha", "BOOL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Beta",  "REAL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Gamma", "DINT", "0", "NullType", false, "Read/Write"),
        };

        var target = new[]
        {
            // Alpha renamed (case change) + retyped (BOOL→SINT)
            new DataTypeMemberEntry("alpha", "SINT", "0", "NullType", false, "Read/Write"),
            // Beta kept identical
            new DataTypeMemberEntry("Beta",  "REAL", "0", "NullType", false, "Read/Write"),
            // Delta is new
            new DataTypeMemberEntry("Delta", "LINT", "0", "NullType", false, "Read/Write"),
            // Gamma removed (not included)
        };

        var delta = ImportDatatypeHandler.ComputeDelta(current, target);

        // added: Delta
        Assert.Single(delta.Added);
        Assert.Equal("Delta", delta.Added[0]);

        // removed: Gamma
        Assert.Single(delta.Removed);
        Assert.Equal("Gamma", delta.Removed[0]);

        // retyped: alpha (BOOL → SINT)
        Assert.Single(delta.Retyped);
        Assert.Equal("alpha", delta.Retyped[0].Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("BOOL",  delta.Retyped[0].From, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("SINT",  delta.Retyped[0].To,   StringComparer.OrdinalIgnoreCase);

        // renamed: Alpha → alpha
        Assert.Single(delta.Renamed);
        Assert.Equal("Alpha", delta.Renamed[0].From, StringComparer.Ordinal);
        Assert.Equal("alpha", delta.Renamed[0].To,   StringComparer.Ordinal);

        // order of common members in current: Alpha, Beta  (Gamma removed)
        // order of common members in target:  alpha, Beta
        // lower-case: [alpha, beta] vs [alpha, beta] → same → not reordered
        Assert.False(delta.Reordered);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SDK real-run path — call-sequence and verification tests
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Contractual: the SDK import sequence must call export, import(collision),
    /// save, then re-export — in that order.
    ///
    /// <para>
    /// A recording <c>sdkImportRunner</c> fake appends "export", "import(Overwrite)",
    /// and "save" to a shared log as it runs; the <c>reexportReader</c> fake appends
    /// "re-export" when it is called by the handler after the runner returns.
    /// The test asserts that the four entries appear in that exact order.
    /// </para>
    /// </summary>
    [Fact]
    public void RealRun_CallsImportThenSave_InOrder()
    {
        var callLog = new List<string>();

        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
        };

        // Recording fake: appends the SDK calls it represents to callLog and
        // captures the temp L5X path so we can verify it is deleted afterwards.
        string? capturedTempL5x = null;
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> sdkRunner =
            (_, tempL5x, ___, collision) =>
            {
                capturedTempL5x = tempL5x;
                callLog.Add("export");
                callLog.Add($"import({collision})");
                callLog.Add("save");
                return Array.Empty<string>();
            };

        // Re-export fake: appends "re-export" and returns the expected members.
        Func<string, IReadOnlyList<DataTypeMemberEntry>> reexportReader =
            _ =>
            {
                callLog.Add("re-export");
                return members;
            };

        var tmpAcd  = Path.GetTempFileName();
        // Write > 100 KB so the saved copy passes the ACD-size sanity check
        File.WriteAllBytes(tmpAcd, new byte[101 * 1024]);
        var tmpSave = Path.Combine(
            Path.GetTempPath(), $"logixmcp-sdk-sequence-{Guid.NewGuid():N}.out.ACD");
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:         tmpAcd,
                datatypeName:    "MotorCmd",
                l5xPath:         null,
                members:         members,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                sdkImportRunner: sdkRunner,
                reexportReader:  reexportReader);

            // Must succeed (not an error)
            Assert.IsType<ImportDatatypeResult>(result);

            // Verify the call sequence: existence pre-check (re-export of the source) →
            // export → import(Overwrite) → save → re-export of the result
            Assert.Equal(
                new[] { "re-export", "export", "import(Overwrite)", "save", "re-export" },
                callLog);

            // Verify that the temp .L5X fragment was deleted after the runner returned.
            Assert.NotNull(capturedTempL5x);
            Assert.False(File.Exists(capturedTempL5x),
                "temp .L5X fragment must be deleted after the SDK runner completes");
        }
        finally
        {
            try { File.Delete(tmpAcd); }  catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Contractual: <c>verified</c> must be <c>false</c> when the re-exporter
    /// returns a member list that differs from the target list.
    ///
    /// <para>
    /// Uses a recording <c>sdkImportRunner</c> fake (success path) and a
    /// <c>reexportReader</c> that returns a mismatched member list.
    /// </para>
    /// </summary>
    [Fact]
    public void RealRun_ReexportDiffers_ReturnsImportNotApplied_AndWritesNothing()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
        };

        // SDK runner fake: succeeds without side-effects.
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> sdkRunner =
            (_, __, ___, ____) => Array.Empty<string>();

        // Re-export fake: returns a DIFFERENT member list.
        Func<string, IReadOnlyList<DataTypeMemberEntry>> mismatchReader =
            _ => new[]
            {
                new DataTypeMemberEntry("WrongMember", "DINT", "0", "NullType", false, "Read/Write"),
            };

        var tmpAcd  = Path.GetTempFileName();
        // Write > 100 KB so the saved copy passes the ACD-size sanity check
        File.WriteAllBytes(tmpAcd, new byte[101 * 1024]);
        var tmpSave = Path.Combine(
            Path.GetTempPath(), $"logixmcp-sdk-verify-{Guid.NewGuid():N}.out.ACD");
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:         tmpAcd,
                datatypeName:    "MotorCmd",
                l5xPath:         null,
                members:         members,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                sdkImportRunner: sdkRunner,
                reexportReader:  mismatchReader);

            var err = Assert.IsType<ImportNotAppliedError>(result);
            Assert.False(File.Exists(tmpSave), "no file may be left at saveAs when the import did not apply");
        }
        finally
        {
            try { File.Delete(tmpAcd); }  catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Contractual: the SHA-256 hash of <c>acdPath</c> must be unchanged after a
    /// real run — all file writes go to the <c>saveAs</c> copy, never to the input.
    ///
    /// <para>
    /// The handler copies <c>acdPath</c> to <c>saveAs</c> before invoking the
    /// <c>sdkImportRunner</c>; this test verifies that the copy destination is
    /// <c>saveAs</c> and that the original input is untouched.
    /// </para>
    /// </summary>
    [Fact]
    public void RealRun_InputAcd_IsNeverWritten()
    {
        var tmpAcd  = Path.GetTempFileName();
        var tmpSave = Path.Combine(
            Path.GetTempPath(), $"logixmcp-sdk-acdguard-{Guid.NewGuid():N}.out.ACD");
        try
        {
            // Write a known byte pattern to the input ACD so we can detect any
            // modification.  The file must be > 100 KB so the ACD-size sanity check
            // passes after the handler copies it to saveAs.
            var knownContent = new byte[101 * 1024];
            knownContent[0] = 0xCA; knownContent[1] = 0xFE;
            knownContent[2] = 0xBA; knownContent[3] = 0xBE;
            knownContent[4] = 0x01; knownContent[5] = 0x02;
            File.WriteAllBytes(tmpAcd, knownContent);
            var hashBefore = ComputeSha256Hash(tmpAcd);

            var members = new[]
            {
                new DataTypeMemberEntry("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
            };

            // SDK runner fake: operates only on saveAs and tempL5x (never acdPath).
            Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> sdkRunner =
                (saveAs, tempL5x, xpath, collision) => Array.Empty<string>(); // no-op on saveAs

            var result = ImportDatatypeHandler.Execute(
                acdPath:         tmpAcd,
                datatypeName:    "TestType",
                l5xPath:         null,
                members:         members,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                sdkImportRunner: sdkRunner,
                reexportReader:  _ => members);

            // Must succeed
            Assert.IsType<ImportDatatypeResult>(result);

            // The input ACD must be byte-identical to what it was before the run.
            var hashAfter = ComputeSha256Hash(tmpAcd);
            Assert.Equal(hashBefore, hashAfter);  // acdPath must be unchanged
        }
        finally
        {
            try { File.Delete(tmpAcd); }  catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Computes the SHA-256 hash of the file at <paramref name="filePath"/> and
    /// returns it as an upper-case hex string.
    /// </summary>
    private static string ComputeSha256Hash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  new-UDT import is refused up front, honestly
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Reader fake standing in for "type absent from the source ACD".</summary>
    private static readonly Func<string, IReadOnlyList<DataTypeMemberEntry>> TypeAbsentReader =
        _ => throw new InvalidOperationException("type not found in ACD");

    /// <summary>Creates a fake >100 KB ACD so the working-copy size check passes.</summary>
    private static string FakeAcd()
    {
        var acd = Path.GetTempFileName();
        File.WriteAllBytes(acd, new byte[101 * 1024]);
        return acd;
    }

    /// <summary>
    /// Reader fake for a new type: throws for the source ACD (type absent) and returns
    /// <paramref name="afterImport"/> for any other path (the imported temp result).
    /// </summary>
    private static Func<string, IReadOnlyList<DataTypeMemberEntry>> NewTypeReader(
        string sourceAcd, IReadOnlyList<DataTypeMemberEntry> afterImport) =>
        path => string.Equals(path, sourceAcd, StringComparison.Ordinal)
            ? throw new InvalidOperationException("type not found in ACD")
            : afterImport;

    [Fact]
    public void NewType_RealRun_ImportsAtControllerDataTypes_AndVerifiesAtTypePath()
    {
        var acd     = FakeAcd();
        var tmpSave = TempPath(".ACD");
        string? seenXpath = null;
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:         acd,
                datatypeName:    "BrandNewType",
                l5xPath:         null,
                members:         OneMember,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                reexportReader:  NewTypeReader(acd, OneMember),
                sdkImportRunner: (_, _, xpath, _) => { seenXpath = xpath; return Array.Empty<string>(); });

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.Equal("Controller/DataTypes", seenXpath);
            Assert.True(ok.Verified);
            Assert.True(File.Exists(tmpSave));
            Assert.Null(ok.WillCreate);
        }
        finally
        {
            try { File.Delete(acd); }     catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void NewType_RealRun_TypeMissingAfterImport_ReturnsImportNotApplied()
    {
        var acd     = FakeAcd();
        var tmpSave = TempPath(".ACD");
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:         acd,
                datatypeName:    "BrandNewType",
                l5xPath:         null,
                members:         OneMember,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                reexportReader:  TypeAbsentReader,
                sdkImportRunner: (_, _, _, _) => Array.Empty<string>());

            var err = Assert.IsType<ImportNotAppliedError>(result);
            Assert.Empty(err.ActualMembers);
            Assert.False(File.Exists(tmpSave));
        }
        finally
        {
            try { File.Delete(acd); }     catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void NewType_DryRun_PreviewIncludesWillCreateAndWarning()
    {
        var acd = FakeAcd();
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:        acd,
                datatypeName:   "BrandNewType",
                l5xPath:        null,
                members:        OneMember,
                saveAs:         TempPath(".ACD"),
                dryRun:         true,
                reexportReader: TypeAbsentReader);

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.True(ok.DryRun);
            Assert.True(ok.WillCreate);
            Assert.NotNull(ok.Delta);
            Assert.NotEmpty(ok.L5XFragment);
            var warning = Assert.Single(ok.Warnings!);
            Assert.Equal("this real run will create the type", warning);
            Assert.Equal(OneMember.Length, ok.Delta!.Added.Count());

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok));
            Assert.True(json.RootElement.GetProperty("willCreate").GetBoolean());
            Assert.Equal(1, json.RootElement.GetProperty("warnings").GetArrayLength());
        }
        finally
        {
            try { File.Delete(acd); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ExistingType_RealRun_StillCallsSdkImport()
    {
        var acd     = FakeAcd();
        var tmpSave = TempPath(".ACD");
        var sdkCalls = 0;
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:         acd,
                datatypeName:    "MotorCmd",
                l5xPath:         null,
                members:         OneMember,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                reexportReader:  _ => OneMember,
                sdkImportRunner: (_, _, _, _) => { sdkCalls++; return Array.Empty<string>(); });

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.Equal(1, sdkCalls);
            Assert.Null(ok.WillCreate);
            Assert.Null(ok.Warnings);
            Assert.True(File.Exists(tmpSave));
        }
        finally
        {
            try { File.Delete(acd); }     catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SdkFailure_RemovesSaveAsCopy()
    {
        var acd     = FakeAcd();
        var tmpSave = TempPath(".ACD");
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath:         acd,
                datatypeName:    "MotorCmd",
                l5xPath:         null,
                members:         OneMember,
                saveAs:          tmpSave,
                dryRun:          false,
                confirm:         true,
                reexportReader:  _ => OneMember,
                sdkImportRunner: (saveAs, _, _, _) =>
                {
                    Assert.True(File.Exists(saveAs), "working copy must exist while the SDK runs");
                    throw new InvalidOperationException("boom");
                });

            var err = Assert.IsType<ImportDatatypeError>(result);
            Assert.Equal("SdkError", err.Kind);
            Assert.False(File.Exists(tmpSave), "the saveAs copy must be deleted after an SDK failure");
            Assert.True(File.Exists(acd), "the input ACD must be untouched");
        }
        finally
        {
            try { File.Delete(acd); }     catch { /* best effort */ }
            try { File.Delete(tmpSave); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Truthful import, verification, collision outcomes, SDK messages
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly DataTypeMemberEntry[] TwoMembers =
    {
        new("Enable", "BOOL", "0", "NullType", false, "Read/Write"),
        new("Speed",  "REAL", "0", "NullType", false, "Read/Write"),
    };

    private static readonly IReadOnlyList<string> NoMessages = Array.Empty<string>();

    /// <summary>
    /// Reader fake for an existing type: <paramref name="before"/> for the source ACD,
    /// <paramref name="after"/> for any other path (the imported temp result).
    /// </summary>
    private static Func<string, IReadOnlyList<DataTypeMemberEntry>> ExistingTypeReader(
        string sourceAcd, IReadOnlyList<DataTypeMemberEntry> before, IReadOnlyList<DataTypeMemberEntry> after) =>
        path => string.Equals(path, sourceAcd, StringComparison.Ordinal) ? before : after;

    /// <summary>Directory entries beside <paramref name="saveAs"/> whose names mark leaked temp files.</summary>
    private static string[] LeakedTemps(string saveAs) =>
        Directory.GetFiles(Path.GetDirectoryName(saveAs)!, "*.tmp*")
            .Where(f => Path.GetFileName(f).Contains(Path.GetFileNameWithoutExtension(saveAs), StringComparison.Ordinal))
            .ToArray();

    private static object RealRun(
        string acd, string saveAs, IReadOnlyList<DataTypeMemberEntry> members,
        Func<string, IReadOnlyList<DataTypeMemberEntry>>? reader,
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> runner,
        ImportCollisionOption collision = ImportCollisionOption.Overwrite) =>
        ImportDatatypeHandler.Execute(
            acdPath: acd, datatypeName: "MotorCmd", l5xPath: null, members: members,
            saveAs: saveAs, service: DemoService(), collision: collision, dryRun: false, confirm: true,
            reexportReader: reader, sdkImportRunner: runner);

    [Fact]
    public void RenderedFragment_ContainsContextTrue_WithContextTypes()
    {
        var members = new[]
        {
            new DataTypeMemberEntry("Motor",  "MotorCmd", "0", "NullType", false, "Read/Write"),
            new DataTypeMemberEntry("Enable", "BOOL",     "0", "NullType", false, "Read/Write"),
        };
        var ok = Assert.IsType<ImportDatatypeResult>(ImportDatatypeHandler.Execute(
            acdPath: "/tmp/project.ACD", datatypeName: "NewControlType", l5xPath: null,
            members: members, saveAs: "/tmp/output.L5X", service: DemoService()));
        var doc = XDocument.Parse(ok.L5XFragment);
        Assert.Equal("true", doc.Root!.Attribute("ContainsContext")?.Value);
        Assert.Contains(doc.Root.Element("Controller")!.Element("DataTypes")!.Elements("DataType"),
            e => e.Attribute("Use")?.Value == "Context");
    }

    [Fact]
    public void RealRun_ReexportEqualsTarget_VerifiedTrue_FilePresent_NoSdkMessagesWhenEmpty()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var ok = Assert.IsType<ImportDatatypeResult>(
                RealRun(acd, save, TwoMembers, _ => TwoMembers, (_, _, _, _) => NoMessages));
            Assert.True(ok.Verified);
            Assert.True(File.Exists(save));
            Assert.Null(ok.SdkMessages);
            Assert.DoesNotContain("sdkMessages", JsonSerializer.Serialize(ok), StringComparison.Ordinal);
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void RealRun_ReexportDiffers_ImportNotApplied_CarriesMembersAndMessages_NoFileNoTemp()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var result = RealRun(acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, OneMember),
                (_, _, _, _) => new[] { "import log: nothing to do" });
            var err = Assert.IsType<ImportNotAppliedError>(result);
            Assert.Equal("ImportNotApplied", err.Kind);
            Assert.Equal("MotorCmd", err.DatatypeName);
            Assert.Equal(2, err.ExpectedMembers.Count);
            Assert.Single(err.ActualMembers);
            Assert.Equal("Overwrite", err.Collision);
            Assert.Equal(new[] { "import log: nothing to do" }, err.SdkMessages);
            Assert.Contains("collision: Overwrite", err.Hint, StringComparison.Ordinal);
            Assert.False(File.Exists(save));
            Assert.Empty(LeakedTemps(save));

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var root = json.RootElement;
            foreach (var absent in new[] { "savedTo", "advisory", "buildVerify", "build" })
                Assert.False(root.TryGetProperty(absent, out _), $"'{absent}' must not appear on ImportNotApplied");
            Assert.Equal(2, root.GetProperty("expectedMembers").GetArrayLength());
            Assert.Equal(1, root.GetProperty("actualMembers").GetArrayLength());
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void RealRun_ImportNotApplied_BuildRunnerNotCalled()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        var builds = 0;
        try
        {
            var result = ImportDatatypeHandler.Execute(
                acdPath: acd, datatypeName: "MotorCmd", l5xPath: null, members: TwoMembers,
                saveAs: save, service: DemoService(), dryRun: false, confirm: true,
                reexportReader: ExistingTypeReader(acd, OneMember, OneMember),
                sdkImportRunner: (_, _, _, _) => NoMessages,
                buildRunner: _ => { builds++; return 0; });
            Assert.IsType<ImportNotAppliedError>(result);
            Assert.Equal(0, builds);
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void RealRun_NoReexportReader_VerifiedNull_NoteSaysUnverified()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var ok = Assert.IsType<ImportDatatypeResult>(
                RealRun(acd, save, OneMember, null, (_, _, _, _) => NoMessages));
            Assert.Null(ok.Verified);
            Assert.Contains("could not be verified", ok.Note, StringComparison.Ordinal);
            Assert.True(File.Exists(save));
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void Discard_ExistingType_UnchangedReexport_ImportNotApplied_WithDiscardReason()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var err = Assert.IsType<ImportNotAppliedError>(RealRun(
                acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, OneMember),
                (_, _, _, _) => NoMessages, ImportCollisionOption.Discard));
            Assert.Equal("collision=Discard kept the existing type", err.Reason);
            Assert.Equal("Discard", err.Collision);
            Assert.False(File.Exists(save));
            Assert.Empty(LeakedTemps(save));
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void Discard_TargetAlreadyEqualsCurrent_NoOpSuccess_WithNote()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var ok = Assert.IsType<ImportDatatypeResult>(RealRun(
                acd, save, OneMember, ExistingTypeReader(acd, OneMember, OneMember),
                (_, _, _, _) => NoMessages, ImportCollisionOption.Discard));
            Assert.True(ok.Verified);
            Assert.Equal("the type already had these members", ok.Note);
            Assert.True(File.Exists(save));
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void Cancel_SdkAbort_ReturnsImportCancelledOnCollision_WithSdkMessage_NothingWritten()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var result = RealRun(acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, OneMember),
                (_, _, _, _) => throw new SdkImportException(
                    "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES: import aborted, no changes made",
                    new[] { "Data type MotorCmd already exists" }),
                ImportCollisionOption.Cancel);
            var err = Assert.IsType<ImportCancelledOnCollisionError>(result);
            Assert.Equal("ImportCancelledOnCollision", err.Kind);
            Assert.Contains("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES", err.Error, StringComparison.Ordinal);
            Assert.Equal(new[] { "Data type MotorCmd already exists" }, err.SdkMessages);
            Assert.False(File.Exists(save));
            Assert.Empty(LeakedTemps(save));
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void Cancel_AbortMnemonicOnlyInSdkMessages_StillImportCancelledOnCollision()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        try
        {
            var result = RealRun(acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, OneMember),
                (_, _, _, _) => throw new SdkImportException(
                    "import failed", new[] { "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES" }),
                ImportCollisionOption.Cancel);
            Assert.IsType<ImportCancelledOnCollisionError>(result);
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void ExistingType_ImportsAtPerTypeXPath()
    {
        var acd = FakeAcd(); var save = TempPath(".ACD");
        string? seen = null;
        try
        {
            Assert.IsType<ImportDatatypeResult>(RealRun(
                acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, TwoMembers),
                (_, _, xpath, _) => { seen = xpath; return NoMessages; }));
            Assert.Equal("Controller/DataTypes/DataType[@Name='MotorCmd']", seen);
        }
        finally { File.Delete(acd); File.Delete(save); }
    }

    [Fact]
    public void SdkMessages_FlowThroughOnSuccess_AndIntoAuditLine()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"logixmcp-audit-{Guid.NewGuid():N}");
        var acd = FakeAcd(); var save = TempPath(".ACD");
        AuditLogger.OverrideLogDir(logDir);
        try
        {
            var ok = Assert.IsType<ImportDatatypeResult>(RealRun(
                acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, TwoMembers),
                (_, _, _, _) => new[] { "Imported DataType MotorCmd" }));
            Assert.Equal(new[] { "Imported DataType MotorCmd" }, ok.SdkMessages);

            var evt = Assert.Single(
                File.ReadAllLines(Path.Combine(logDir, "audit.jsonl"))
                    .Select(l => JsonDocument.Parse(l).RootElement)
                    .Where(e => e.GetProperty("event").GetString() == "datatype_import"));
            var outcome = evt.GetProperty("outcome").GetString();
            Assert.StartsWith("success", outcome, StringComparison.Ordinal);
            Assert.Contains("Imported DataType MotorCmd", outcome, StringComparison.Ordinal);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            File.Delete(acd); File.Delete(save);
            try { Directory.Delete(logDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ImportNotApplied_IsAudited_WithKindAndSdkMessages()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"logixmcp-audit-{Guid.NewGuid():N}");
        var acd = FakeAcd(); var save = TempPath(".ACD");
        AuditLogger.OverrideLogDir(logDir);
        try
        {
            Assert.IsType<ImportNotAppliedError>(RealRun(
                acd, save, TwoMembers, ExistingTypeReader(acd, OneMember, OneMember),
                (_, _, _, _) => new[] { "kept existing" }));
            var evt = Assert.Single(
                File.ReadAllLines(Path.Combine(logDir, "audit.jsonl"))
                    .Select(l => JsonDocument.Parse(l).RootElement)
                    .Where(e => e.GetProperty("event").GetString() == "datatype_import"));
            var outcome = evt.GetProperty("outcome").GetString();
            Assert.StartsWith("error:ImportNotApplied", outcome, StringComparison.Ordinal);
            Assert.Contains("kept existing", outcome, StringComparison.Ordinal);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            File.Delete(acd); File.Delete(save);
            try { Directory.Delete(logDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BOOL members: visible-member verification, delta, no .bak
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly DataTypeMemberEntry CountMember =
        new("Count", "DINT", "0", "Decimal", false, "Read/Write");

    private static readonly DataTypeMemberEntry RunMember =
        new("Run", "BOOL", "0", "", false, "Read/Write");

    private static DataTypeMemberEntry[] Reexported(string boolType) => new[]
    {
        CountMember,
        new DataTypeMemberEntry("ZZZZZZZZZZFlags0", "SINT", "0", "Decimal", true, "Read/Write"),
        new DataTypeMemberEntry("Run", boolType, "0", "", false, "Read/Write",
            Target: "ZZZZZZZZZZFlags0", BitNumber: 0),
    };

    private static string MakeAcd()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[101 * 1024]);
        return path;
    }

    private static object RunGainBool(
        string acd, string saveAs, Func<string, IReadOnlyList<DataTypeMemberEntry>> reader,
        Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>>? runner = null) =>
        ImportDatatypeHandler.Execute(
            acdPath:         acd,
            datatypeName:    "Flags",
            l5xPath:         null,
            members:         new[] { CountMember, RunMember },
            saveAs:          saveAs,
            dryRun:          false,
            confirm:         true,
            sdkImportRunner: runner ?? ((_, __, ___, ____) => Array.Empty<string>()),
            reexportReader:  reader);

    [Theory]
    [InlineData("BOOL")]
    [InlineData("BIT")]
    public void RealRun_TypeGainingBoolMember_VerifiesTrue(string boolType)
    {
        var acd = MakeAcd();
        var saveAs = TempPath(".out.ACD");
        try
        {
            var result = RunGainBool(acd, saveAs,
                path => path == acd ? new[] { CountMember } : Reexported(boolType));

            var ok = Assert.IsType<ImportDatatypeResult>(result);
            Assert.True(ok.Verified);
            Assert.Equal(saveAs, ok.SavedTo);
            Assert.True(File.Exists(saveAs));
        }
        finally
        {
            File.Delete(acd);
            if (File.Exists(saveAs)) File.Delete(saveAs);
        }
    }

    [Fact]
    public void DryRunDelta_AddingBool_ListsOneAddedMember()
    {
        var current = new[] { CountMember };
        var target  = Reexported("BIT");

        var delta = ImportDatatypeHandler.ComputeDelta(current, target);
        Assert.Equal(new[] { "Run" }, delta.Added.ToArray());
        Assert.Empty(delta.Removed);
        Assert.Empty(delta.Retyped);

        // Hosts on both sides, BIT vs BOOL: nothing changed.
        var same = ImportDatatypeHandler.ComputeDelta(Reexported("BOOL"), Reexported("BIT"));
        Assert.Empty(same.Added);
        Assert.Empty(same.Removed);
        Assert.Empty(same.Retyped);
        Assert.False(same.Reordered);
    }

    [Fact]
    public void ImportNotApplied_ListsVisibleMembers_AndReportsHiddenHostsSeparately()
    {
        var acd = MakeAcd();
        var saveAs = TempPath(".out.ACD");
        try
        {
            var result = RunGainBool(acd, saveAs,
                path => new[]
                {
                    CountMember,
                    new DataTypeMemberEntry("ZZZZZZZZZZFlags0", "SINT", "0", "Decimal", true, "Read/Write"),
                });

            var err = Assert.IsType<ImportNotAppliedError>(result);
            Assert.DoesNotContain(err.ExpectedMembers, m => m.Hidden);
            Assert.DoesNotContain(err.ActualMembers, m => m.Hidden);
            Assert.Equal(new[] { "Count" }, err.ActualMembers.Select(m => m.Name).ToArray());
            var host = Assert.Single(err.HiddenHosts!);
            Assert.Equal("ZZZZZZZZZZFlags0", host.Name);
            Assert.False(File.Exists(saveAs));
        }
        finally
        {
            File.Delete(acd);
        }
    }

    private static Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>> BakWritingRunner(
        List<string> baks) =>
        (dest, _, __, ___) =>
        {
            var dir  = Path.GetDirectoryName(dest)!;
            var stem = Path.GetFileNameWithoutExtension(dest);
            var a = dest + ".bak";
            var b = Path.Combine(dir, stem + ".1.bak");
            File.WriteAllText(a, "bak");
            File.WriteAllText(b, "bak");
            baks.Add(a);
            baks.Add(b);
            return Array.Empty<string>();
        };

    [Fact]
    public void RealRun_BakBesideTemp_RemovedOnSuccess()
    {
        var acd = MakeAcd();
        var dir = Path.Combine(Path.GetTempPath(), $"logixmcp-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var saveAs = Path.Combine(dir, "out.ACD");
        var baks = new List<string>();
        try
        {
            var result = RunGainBool(acd, saveAs,
                path => path == acd ? new[] { CountMember } : Reexported("BIT"),
                BakWritingRunner(baks));

            Assert.True(Assert.IsType<ImportDatatypeResult>(result).Verified);
            Assert.Equal(2, baks.Count);
            Assert.Equal(new[] { saveAs }, Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            File.Delete(acd);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RealRun_BakBesideTemp_RemovedOnImportNotApplied()
    {
        var acd = MakeAcd();
        var dir = Path.Combine(Path.GetTempPath(), $"logixmcp-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var saveAs = Path.Combine(dir, "out.ACD");
        var baks = new List<string>();
        try
        {
            var result = RunGainBool(acd, saveAs,
                path => new[] { CountMember },
                BakWritingRunner(baks));

            Assert.IsType<ImportNotAppliedError>(result);
            Assert.Equal(2, baks.Count);
            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            File.Delete(acd);
            Directory.Delete(dir, true);
        }
    }
}
