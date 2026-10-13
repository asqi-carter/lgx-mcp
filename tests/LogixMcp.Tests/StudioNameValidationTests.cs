#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Program and routine names are validated against <c>^[A-Za-z_][A-Za-z0-9_]{0,39}$</c> before any
/// script call, so Unicode quotes (U+2018-U+201B) never reach a PowerShell single-quoted literal.
/// Invalid names are refused with the existing argument-error shape and no runner call occurs.
/// </summary>
public class StudioNameValidationTests
{
    private sealed class FakeProbe : IEnvironmentProbe
    {
        public int CurrentSessionId { get; init; } = 2;
        public bool IsElevated { get; init; }
        public IReadOnlyList<int> LogixDesignerSessionIds { get; init; } = new[] { 2 };
        public bool StudioIntegrityHigherThanCurrent { get; init; }
    }

    private static Func<string, string, CancellationToken, Task<object>> Reader() =>
        (p, r, ct) => Task.FromResult<object>(new PendingEditsResult(
            true, "Run", null,
            new[] { new RoutinePendingEditState(p, r, 1, new[] { new PendingEditRungEntry(0, "I", "OTE(X);") }, "pending") },
            new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));

    public static IEnumerable<object[]> BadNames() => new[]
    {
        new object[] { "Main‘Program" }, new object[] { "Main’Program" },
        new object[] { "Main‚Program" }, new object[] { "Main‛Program" },
        new object[] { "Main'Program" }, new object[] { "Main Program" },
        new object[] { "1Main" }, new object[] { "Main\n" }, new object[] { "École" },
        new object[] { new string('a', 41) }, new object[] { "" },
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void InvalidNames_AreRejected(string name) => Assert.False(StudioPreLaunch.IsValidName(name));

    [Theory]
    [InlineData("MainProgram")]
    [InlineData("_x")]
    [InlineData("R01_Main")]
    public void ValidNames_AreAccepted(string name) => Assert.True(StudioPreLaunch.IsValidName(name));

    [Fact]
    public void MaxLength_Boundary()
    {
        Assert.True(StudioPreLaunch.IsValidName(new string('a', 40)));
        Assert.False(StudioPreLaunch.IsValidName(new string('a', 41)));
    }

    [Fact]
    public async Task UnicodeQuote_Refused()
    {
        // Lifecycle handler: refused with the existing argument-error shape, runner never called.
        foreach (var (program, routine) in new[]
                 { ("Main‘Program", "MainRoutine"), ("MainProgram", "Main‛Routine") })
        {
            var verbs = new List<string>();
            var err = Assert.IsType<CancelPendingEditsError>(await CancelPendingEditsHandler.ExecuteAsync(
                program, routine, true, Reader(),
                (verb, ct) => { verbs.Add(verb); return Task.FromResult(new Session1ScriptResult(true, "x", Array.Empty<string>())); },
                () => true, planLaunch: () => ("direct", null)));
            Assert.False(err.Launched);
            Assert.Contains("invalid_argument", err.Error, StringComparison.Ordinal);
            Assert.Empty(verbs);
        }

        // Script runner: refused before the process runner is invoked.
        var calls = 0;
        Task<Session1ScriptRunner.ProcessRunResult?> Runner(ProcessStartInfo psi, TimeSpan? t, CancellationToken _)
        {
            calls++;
            return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                new Session1ScriptRunner.ProcessRunResult(0, "", ""));
        }
        var script = Path.Combine(Path.GetTempPath(), $"s1-name-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, "# fake\n");
        try
        {
            var result = await Session1ScriptRunner.RunCoreAsync(
                "assemble", script, null, new FakeProbe(), Runner, () => true,
                CancellationToken.None, program: "Main’Program");
            Assert.False(result.Ok);
            Assert.False(result.Launched);
            Assert.Contains("invalid_argument", result.Reason, StringComparison.Ordinal);
            Assert.Equal(0, calls);
        }
        finally { File.Delete(script); }
    }
}
#endif
