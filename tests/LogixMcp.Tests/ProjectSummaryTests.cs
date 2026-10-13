namespace LogixMcp.Tests;

/// <summary>Tests for routine names/types in <see cref="ProjectSummaryHandler"/>.</summary>
public class ProjectSummaryTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoInterlock.L5X");

    [Fact]
    public void Summary_ListsRoutineNamesAndTypes()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = Assert.IsType<ProjectSummaryResult>(ProjectSummaryHandler.Execute(svc));
        var prog = Assert.Single(result.Programs);

        Assert.Equal(3, prog.Routines);
        Assert.Equal(
            new[] { "MainRoutine:RLL", "SafeRoutine:RLL", "AlarmRoutine:RLL" },
            prog.RoutineList.Select(r => $"{r.Name}:{r.Type}").ToArray());
    }

    [Fact]
    public void Summary_RoutineList_AlsoPresentForAcdDerivedProject()
    {
        var svc = new ProjectService();
        svc.LoadFromAcd(FixturePath, "/fake/original.ACD");
        var result = (ProjectSummaryResult)ProjectSummaryHandler.Execute(svc);

        Assert.Equal(3, result.Programs[0].RoutineList.Count);
    }
}
