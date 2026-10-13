#if LOGIX_SDK
// ── AcdProjectOpenerTests — path-isolation unit tests ────────────────────────
using RockwellAutomation.LogixDesigner;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for the path-isolation helpers on <see cref="AcdProjectOpener"/>:
/// <list type="bullet">
///   <item><see cref="AcdProjectOpener.ComputeL5xOutputPath"/></item>
///   <item><see cref="AcdProjectOpener.AssertWithinWorkingDirectory"/></item>
/// </list>
///
/// <para>
/// These tests verify that all ACD conversion output files are constrained
/// to the server's working directory (<see cref="LogixProjectSession.WorkingDirectoryBase"/>),
/// satisfying the output-isolation requirement:
/// no file may be written to the operator's source directory or any parent
/// of the input path.
/// </para>
/// </summary>
public class AcdProjectOpenerTests
{
    // ── ComputeL5xOutputPath ──────────────────────────────────────────────────

    [Fact]
    public void ComputeL5xOutputPath_IsUnderWorkingDirectoryBase()
    {
        var path         = AcdProjectOpener.ComputeL5xOutputPath();
        var resolvedPath = Path.GetFullPath(path);
        var resolvedBase = Path.GetFullPath(LogixProjectSession.WorkingDirectoryBase)
                               .TrimEnd(Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

        Assert.StartsWith(resolvedBase, resolvedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeL5xOutputPath_HasL5XExtension()
    {
        var path = AcdProjectOpener.ComputeL5xOutputPath();

        Assert.Equal(".L5X", Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeL5xOutputPath_TwoCallsReturnDistinctPaths()
    {
        var path1 = AcdProjectOpener.ComputeL5xOutputPath();
        var path2 = AcdProjectOpener.ComputeL5xOutputPath();

        Assert.NotEqual(path1, path2, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeL5xOutputPath_IsAbsolutePath()
    {
        var path = AcdProjectOpener.ComputeL5xOutputPath();

        Assert.True(Path.IsPathRooted(path),
            $"Expected an absolute (rooted) path; got '{path}'.");
    }

    // ── AssertWithinWorkingDirectory — valid paths ────────────────────────────

    [Fact]
    public void AssertWithinWorkingDirectory_PathDirectlyInsideBase_DoesNotThrow()
    {
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(basePath, "lgx-acd-abc123.L5X");

        // Must not throw.
        AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath);
    }

    [Fact]
    public void AssertWithinWorkingDirectory_PathInsideSubdirectory_DoesNotThrow()
    {
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(basePath, "subdir", "file.L5X");

        // Paths nested in subdirectories of the working directory are also valid.
        AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath);
    }

    // ── AssertWithinWorkingDirectory — path-escape rejections ────────────────

    [Fact]
    public void AssertWithinWorkingDirectory_PathOutsideBase_ThrowsInvalidOperationException()
    {
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(Path.GetTempPath(), "evil-output.L5X");

        Assert.Throws<InvalidOperationException>(() =>
            AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath));
    }

    [Fact]
    public void AssertWithinWorkingDirectory_PathTraversalAttempt_Throws()
    {
        // "../escape.L5X" resolves to the parent of workingDirBase — must be rejected.
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(basePath, "..", "escape.L5X");

        Assert.Throws<InvalidOperationException>(() =>
            AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath));
    }

    [Fact]
    public void AssertWithinWorkingDirectory_SiblingDirectoryWithSamePrefix_Throws()
    {
        // "/tmp/logix-mcp-work-extra/file" must NOT match base "/tmp/logix-mcp-work".
        // The trailing-separator guard in the implementation prevents this false match.
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(Path.GetTempPath(), "logix-mcp-work-extra", "file.L5X");

        Assert.Throws<InvalidOperationException>(() =>
            AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath));
    }

    [Fact]
    public void AssertWithinWorkingDirectory_SourceDirectoryAsOutput_Throws()
    {
        // Attempting to write the L5X back into the source (operator) directory
        // must be rejected.
        var basePath        = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "operator", "projects");
        var outputPath      = Path.Combine(sourceDirectory, "derived.L5X");

        Assert.Throws<InvalidOperationException>(() =>
            AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath));
    }

    // ── AssertWithinWorkingDirectory — error message quality ─────────────────

    [Fact]
    public void AssertWithinWorkingDirectory_Error_MentionsWorkingDirectory()
    {
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(Path.GetTempPath(), "evil.L5X");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath));

        Assert.Contains("working directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertWithinWorkingDirectory_Error_ContainsResolvedViolatingPath()
    {
        var basePath   = Path.Combine(Path.GetTempPath(), "logix-mcp-work");
        var outputPath = Path.Combine(Path.GetTempPath(), "evil.L5X");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcdProjectOpener.AssertWithinWorkingDirectory(outputPath, basePath));

        // The error message must include the resolved output path so the caller
        // can diagnose which path triggered the violation.
        var resolvedOutput = Path.GetFullPath(outputPath);
        Assert.Contains(resolvedOutput, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── IsFileSharingViolation ────────────────────────────────────────────────

    [Fact]
    public void IsFileSharingViolation_Null_ReturnsFalse()
    {
        Assert.False(AcdProjectOpener.IsFileSharingViolation(null));
    }

    [Fact]
    public void IsFileSharingViolation_NonIoException_ReturnsFalse()
    {
        Assert.False(AcdProjectOpener.IsFileSharingViolation(new Exception("generic error")));
    }

    [Fact]
    public void IsFileSharingViolation_GenericIoException_ReturnsFalse()
    {
        // An IOException whose message does not match locking text and whose HResult
        // does not match ERROR_SHARING_VIOLATION must not be detected as a lock.
        var ex = new IOException("disk full");
        Assert.False(AcdProjectOpener.IsFileSharingViolation(ex));
    }

    [Fact]
    public void IsFileSharingViolation_IoExceptionWithSharingViolationHResult_ReturnsTrue()
    {
        // Win32 ERROR_SHARING_VIOLATION = 32 (0x20); HRESULT = 0x80070020.
        // IOException(string, int) sets HResult to the supplied value.
        var ex = new IOException("access denied", unchecked((int)0x80070020));
        Assert.True(AcdProjectOpener.IsFileSharingViolation(ex));
    }

    [Fact]
    public void IsFileSharingViolation_IoExceptionWithUsedByAnotherProcessMessage_ReturnsTrue()
    {
        // Message-text fallback — covers environments where HResult is not set by the OS.
        var ex = new IOException("The process cannot access the file because it is being " +
                                 "used by another process.");
        Assert.True(AcdProjectOpener.IsFileSharingViolation(ex));
    }

    [Fact]
    public void IsFileSharingViolation_IoExceptionWithSharingViolationMessage_ReturnsTrue()
    {
        var ex = new IOException("sharing violation encountered");
        Assert.True(AcdProjectOpener.IsFileSharingViolation(ex));
    }

    [Fact]
    public void IsFileSharingViolation_MessageCheckIsCaseInsensitive()
    {
        // "Sharing Violation" (mixed case) must still match.
        var ex = new IOException("Sharing Violation error");
        Assert.True(AcdProjectOpener.IsFileSharingViolation(ex));
    }

    [Fact]
    public void IsFileSharingViolation_IoExceptionInInnerChain_ReturnsTrue()
    {
        // The locking IOException may be wrapped inside another exception
        // (e.g. a TagServiceException wrapping the original IOException).
        var lockEx    = new IOException("The process cannot access the file because it is " +
                                        "being used by another process.");
        var outerEx   = new Exception("outer wrapper", lockEx);
        Assert.True(AcdProjectOpener.IsFileSharingViolation(outerEx));
    }

    [Fact]
    public void IsFileSharingViolation_NonLockIoExceptionInInnerChain_ReturnsFalse()
    {
        // A generic IOException nested inside another exception must NOT be
        // detected as a lock violation.
        var innerEx = new IOException("file not found");
        var outerEx = new Exception("outer wrapper", innerEx);
        Assert.False(AcdProjectOpener.IsFileSharingViolation(outerEx));
    }

    // ── OpenAsync — Studio lock detection ─────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="AcdProjectOpener"/> whose session always fails the
    /// working-copy step with the given <paramref name="copyException"/>.
    ///
    /// <para>
    /// The session factory creates a <see cref="LogixProjectSession"/> that uses
    /// <paramref name="copyException"/> as the copier throw, so
    /// <see cref="LogixProjectSession.OpenAsync"/> surfaces a
    /// <see cref="TagServiceException"/> whose <c>InnerException</c> is the
    /// supplied exception — exactly replicating the production failure path.
    /// </para>
    /// </summary>
    private static AcdProjectOpener OpenerWithFailingCopier(Exception copyException) =>
        new(
            sessionFactory: () => new LogixProjectSession(
                projectFactory: (_path, _ct) =>
                    Task.FromResult<ILogixProject>(new FakeLogixProject()),
                workingCopier:  _ => throw copyException),
            l5xLoader: _ => throw new NotSupportedException("L5X loading not expected"));

    [Fact]
    public async Task OpenAsync_CopyFailsDueToStudioLock_ReturnsStudioLockError()
    {
        // Arrange: create a real temp file so File.Exists passes, then open with a
        // copier that throws a sharing-violation IOException.
        var tempFile = Path.GetTempFileName();
        try
        {
            var lockEx  = new IOException("The process cannot access the file because " +
                                          "it is being used by another process.",
                                          unchecked((int)0x80070020));
            var opener  = OpenerWithFailingCopier(lockEx);
            var service = new ProjectService();

            var result = await opener.OpenAsync(tempFile, service);

            Assert.IsType<OpenProjectStudioLockError>(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAsync_CopyFailsDueToStudioLock_ErrorMentionsStudio()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var lockEx  = new IOException("sharing violation", unchecked((int)0x80070020));
            var opener  = OpenerWithFailingCopier(lockEx);
            var service = new ProjectService();

            var result = (OpenProjectStudioLockError)await opener.OpenAsync(tempFile, service);

            Assert.Contains("Studio", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAsync_CopyFailsDueToStudioLock_WorkaroundMentionsL5X()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var lockEx  = new IOException("used by another process");
            var opener  = OpenerWithFailingCopier(lockEx);
            var service = new ProjectService();

            var result = (OpenProjectStudioLockError)await opener.OpenAsync(tempFile, service);

            Assert.Contains("L5X", result.Workaround, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAsync_CopyFailsDueToStudioLock_WorkaroundMentionsSaveAs()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var lockEx  = new IOException("sharing violation", unchecked((int)0x80070020));
            var opener  = OpenerWithFailingCopier(lockEx);
            var service = new ProjectService();

            var result = (OpenProjectStudioLockError)await opener.OpenAsync(tempFile, service);

            Assert.Contains("Save As", result.Workaround, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAsync_CopyFailsWithGenericIoError_ReturnsOpenProjectError()
    {
        // A non-lock IOException (e.g. disk full) must NOT produce a StudioLockError.
        var tempFile = Path.GetTempFileName();
        try
        {
            var opener  = OpenerWithFailingCopier(new IOException("No space left on device"));
            var service = new ProjectService();

            var result = await opener.OpenAsync(tempFile, service);

            Assert.IsType<OpenProjectError>(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAsync_StudioLockError_IsNotGenericOpenProjectError()
    {
        // The Studio lock error must be the dedicated type, not a generic OpenProjectError.
        var tempFile = Path.GetTempFileName();
        try
        {
            var lockEx  = new IOException("used by another process");
            var opener  = OpenerWithFailingCopier(lockEx);
            var service = new ProjectService();

            var result = await opener.OpenAsync(tempFile, service);

            Assert.IsNotType<OpenProjectError>(result);
            Assert.IsType<OpenProjectStudioLockError>(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAsync_StudioLockError_SerializesToJsonWithBothFields()
    {
        // The structured error must serialize with both "error" and "workaround" keys
        // so callers can read both fields without parsing the error string.
        var tempFile = Path.GetTempFileName();
        try
        {
            var lockEx  = new IOException("sharing violation", unchecked((int)0x80070020));
            var opener  = OpenerWithFailingCopier(lockEx);
            var service = new ProjectService();

            var result = await opener.OpenAsync(tempFile, service);
            var json   = System.Text.Json.JsonSerializer.Serialize(result);
            var doc    = System.Text.Json.JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("error",      out _),
                "JSON must contain 'error' key");
            Assert.True(doc.RootElement.TryGetProperty("workaround", out _),
                "JSON must contain 'workaround' key");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
#endif
