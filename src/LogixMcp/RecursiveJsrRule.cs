/// <summary>
/// Flags cycles in a program's JSR call graph (A→B→A, or A→A).
/// Rationale: unbounded recursion overflows the controller's stack and faults.
/// </summary>
public sealed class RecursiveJsrRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "RecursiveJsr";

    /// <inheritdoc />
    public string Rationale =>
        "A JSR cycle (A→B→A, or A→A) recurses without bound, overflowing the controller's stack and faulting.";

    private sealed record Edge(string Target, int Rung, string Text);

    /// <summary>
    /// Builds the JSR call graph per program (JSR cannot cross programs; targets that are not
    /// ladder routines in the program are ignored) and reports each cycle found by depth-first
    /// search once, as a warning citing the JSR rung that closes the cycle (the back edge) and
    /// listing the whole cycle in the message.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>One warning per cycle.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var group in context.LadderRoutines.GroupBy(r => r.Program, StringComparer.OrdinalIgnoreCase))
        {
            var routines = group.ToList();
            var program  = routines[0].Program;
            var names    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in routines) names.TryAdd(r.Routine, r.Routine);

            var graph = new Dictionary<string, List<Edge>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in routines)
            {
                var edges = new List<Edge>();
                foreach (var rung in r.Rungs)
                    foreach (var target in LintJsr.Targets(rung))
                        if (names.TryGetValue(target, out var canonical))
                            edges.Add(new Edge(canonical, rung.Number, rung.Text));
                graph[r.Routine] = edges;
            }

            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = on stack, 2 = done
            var stack = new List<string>();
            var found = new List<LintFinding>();

            void Visit(string node)
            {
                state[node] = 1;
                stack.Add(node);
                foreach (var edge in graph[node])
                {
                    state.TryGetValue(edge.Target, out var s);
                    if (s == 2) continue;
                    if (s == 1)
                    {
                        var start = stack.FindIndex(x => string.Equals(x, edge.Target, StringComparison.OrdinalIgnoreCase));
                        var path  = stack.Skip(start).Append(edge.Target);
                        var cycle = string.Join(" → ", path);
                        found.Add(new LintFinding(
                            Id, LintHandler.SeverityName(LintSeverity.Warning),
                            program, node, edge.Rung, edge.Text,
                            $"JSR({edge.Target}) closes a recursive call cycle in program '{program}': {cycle}. See lgx-instruction-reference, section \"Program control: JSR, RET, SBR, JMP, LBL, AFI, NOP\".",
                            "Break the cycle: remove the JSR that closes it, or replace the recursion with a bounded loop."));
                        continue;
                    }
                    Visit(edge.Target);
                }
                stack.RemoveAt(stack.Count - 1);
                state[node] = 2;
            }

            foreach (var r in routines)
                if (!state.ContainsKey(r.Routine)) Visit(r.Routine);

            foreach (var f in found) yield return f;
        }
    }
}
