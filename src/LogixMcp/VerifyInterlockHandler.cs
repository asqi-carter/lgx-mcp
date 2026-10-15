using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>
/// Per-rung detail produced by <see cref="VerifyInterlockHandler.Execute"/>.
/// Describes one rung that writes the output tag, and whether the condition tag
/// is present as an XIC or XIO contact on that rung.
/// </summary>
/// <param name="Program">Name of the program that contains the routine.</param>
/// <param name="Routine">Name of the routine that contains the rung.</param>
/// <param name="RungNumber">Zero-based rung index within the routine.</param>
/// <param name="RungText">Neutral-text content of the rung (e.g. <c>XIC(Safety_OK)OTE(Motor_Run);</c>).</param>
/// <param name="ConditionPresent">
/// <c>true</c> when the condition tag appears as an XIC or XIO contact on this rung.
/// </param>
/// <param name="Polarity">
/// <c>"XIC"</c> or <c>"XIO"</c> when <paramref name="ConditionPresent"/> is <c>true</c>;
/// <c>null</c> when the condition tag is absent from this rung.
/// </param>
/// <param name="Status">
/// <c>"guarded"</c> when every path from the left rail to every write of the output passes through
/// the condition contact; <c>"bypassed"</c> when the contact is on some paths but at least one path
/// to the output skips it; <c>"absent"</c> when the rung has no usable condition contact.
/// </param>
/// <param name="BypassPaths">
/// For a <c>"bypassed"</c> rung, the branch path(s) that reach the output without the contact; <c>null</c> otherwise.
/// </param>
/// <param name="Via">
/// For a <c>"chained"</c> rung, the intermediate bit(s) the rung is gated by, from the first (nearest the output)
/// to the last (written under the condition); <c>null</c> otherwise.
/// </param>
/// <param name="State">
/// With <c>includePending: true</c>, <c>staged</c>, <c>pending</c> or <c>tested</c> when the rung is not yet accepted
/// controller logic; <c>null</c> (omitted) for an accepted rung.
/// </param>
public record VerifyInterlockRungDetail(
    [property: JsonPropertyName("program")]          string  Program,
    [property: JsonPropertyName("routine")]          string  Routine,
    [property: JsonPropertyName("rungNumber")]       int     RungNumber,
    [property: JsonPropertyName("rungText")]         string  RungText,
    [property: JsonPropertyName("conditionPresent")] bool    ConditionPresent,
    [property: JsonPropertyName("polarity")]         string? Polarity,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonPropertyName("bypassPaths"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? BypassPaths = null,
    [property: JsonPropertyName("via"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Via = null,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null);

/// <summary>
/// A rung that writes an intermediate bit without being interlocked by the condition, so it breaks a chain.
/// </summary>
/// <param name="Tag">The intermediate bit this rung writes.</param>
/// <param name="Program">Program containing the rung.</param>
/// <param name="Routine">Routine containing the rung.</param>
/// <param name="RungNumber">Zero-based rung index.</param>
/// <param name="RungText">Neutral text of the rung (wrapped as untrusted project content).</param>
/// <param name="State">
/// With <c>includePending: true</c>, <c>staged</c>, <c>pending</c> or <c>tested</c> when the rung is not yet accepted
/// controller logic; <c>null</c> (omitted) for an accepted rung.
/// </param>
public record VerifyInterlockWriter(
    [property: JsonPropertyName("tag")]        string Tag,
    [property: JsonPropertyName("program")]    string Program,
    [property: JsonPropertyName("routine")]    string Routine,
    [property: JsonPropertyName("rungNumber")] int    RungNumber,
    [property: JsonPropertyName("rungText")]   string RungText,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null);

/// <summary>
/// Strongly-typed success response returned by <see cref="VerifyInterlockHandler.Execute"/>.
/// </summary>
/// <param name="Verified">
/// <c>true</c> only when, on every rung that writes <paramref name="OutputTag"/>, every path from the
/// left rail to the output passes through <paramref name="ConditionTag"/> as an XIC or XIO contact;
/// <c>false</c> when a writing rung lacks the condition or lets a branch bypass it.
/// </param>
/// <param name="OutputTag">The output tag that was checked.</param>
/// <param name="ConditionTag">The condition tag whose presence was verified.</param>
/// <param name="Rungs">
/// Per-rung details for every rung that writes <paramref name="OutputTag"/>.
/// </param>
public record VerifyInterlockResult(
    [property: JsonPropertyName("verified")]     bool                                  Verified,
    [property: JsonPropertyName("outputTag")]    string                                OutputTag,
    [property: JsonPropertyName("conditionTag")] string                                ConditionTag,
    [property: JsonPropertyName("rungs")]        IReadOnlyList<VerifyInterlockRungDetail> Rungs)
{
    /// <summary>
    /// Rungs that could not be parsed and so were not checked (program, routine, rung, error);
    /// <c>null</c> when every rung parsed. <see cref="Verified"/> is <c>false</c> when one of them
    /// mentions the output tag, because it may write it unguarded.
    /// </summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// <c>"verified"</c>, <c>"bypassed"</c> (a branch lets the output be driven without the condition),
    /// or <c>"unverified"</c> (a rung lacks the condition or could not be parsed). Never <c>"verified"</c>
    /// when any rung is bypassed.
    /// </summary>
    [JsonPropertyName("result")]
    public string Result => Verified ? "verified"
        : Rungs.Any(r => r.Status == "bypassed") ? "bypassed"
        : "unverified";

    /// <summary>Why <see cref="Verified"/> is <c>false</c> despite every parsed rung carrying the condition; <c>null</c> otherwise.</summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>
    /// Intermediate bits the proof went through (distinct, nearest the output first); <c>null</c> when no rung
    /// needed a chain. With <see cref="Reason"/> <c>"depth-limit"</c> it lists the chain that is too deep.
    /// </summary>
    [JsonPropertyName("via"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Via { get; init; }

    /// <summary>The chain(s) followed, e.g. <c>Drive &lt;- StartBit &lt;- Cond</c>; <c>null</c> when none.</summary>
    [JsonPropertyName("chain"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Chain { get; init; }

    /// <summary>
    /// Machine-readable reason the result is not verified when it is not the plain "no condition" case:
    /// <c>"depth-limit"</c> (a proving chain exists but is deeper than the limit of 3 intermediate bits) or
    /// <c>"unguarded-writer"</c> (an intermediate bit has a writer not interlocked by the condition); else <c>null</c>.
    /// </summary>
    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>Writers of intermediate bits that are not interlocked by the condition; <c>null</c> when none were found.</summary>
    [JsonPropertyName("unguardedWriters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<VerifyInterlockWriter>? UnguardedWriters { get; init; }

    /// <summary>
    /// Pending rungs that write the output tag but were left out because <c>includePending</c> is false
    /// (program, routine, rungIndex, tag); <c>null</c> when none were left out.
    /// </summary>
    [JsonPropertyName("pendingWritersExcluded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PendingWriter>? PendingWritersExcluded { get; init; }
}

/// <summary>
/// Strongly-typed error response returned when the project is not loaded,
/// inputs are invalid, or the output tag is never written.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record VerifyInterlockError(
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>Machine-readable error kind (e.g. <c>AmbiguousTagName</c>); <c>null</c> for plain errors.</summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>The ambiguous name that was requested, for <c>AmbiguousTagName</c>; <c>null</c> otherwise.</summary>
    [JsonPropertyName("tag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; init; }

    /// <summary>Qualified names (<c>\Program.Tag</c>) the bare name matches, for <c>AmbiguousTagName</c>; <c>null</c> otherwise.</summary>
    [JsonPropertyName("matches"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Matches { get; init; }

    /// <summary>Remediation text; <c>null</c> when none.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    /// <summary>Rungs that could not be parsed (so the tag may be written there); <c>null</c> when none.</summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// Pending rungs that write the output tag but were left out because <c>includePending</c> is false
    /// (program, routine, rungIndex, tag); <c>null</c> when none were left out.
    /// </summary>
    [JsonPropertyName("pendingWritersExcluded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PendingWriter>? PendingWritersExcluded { get; init; }
}

/// <summary>
/// Pure handler for the <c>logix_verify_interlock</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// Write-classification logic is delegated to the shared <see cref="WriteClassifier"/>.
/// </summary>
public static class VerifyInterlockHandler
{
    /// <summary>
    /// Performs a purely static structural check to determine whether every rung that
    /// writes <paramref name="outputTag"/> also contains <paramref name="conditionTag"/>
    /// as an XIC or XIO contact.
    ///
    /// <para>Algorithm:</para>
    /// <list type="number">
    ///   <item><description>
    ///     Scan every RLL rung in every program and routine of the open project for rungs
    ///     that write <paramref name="outputTag"/> (detected via <see cref="WriteClassifier"/>).
    ///   </description></item>
    ///   <item><description>
    ///     Return a not-found error if <paramref name="outputTag"/> is never written.
    ///   </description></item>
    ///   <item><description>
    ///     For each writing rung, check whether <paramref name="conditionTag"/> appears
    ///     as an argument of an XIC or XIO instruction on the same rung.
    ///   </description></item>
    ///   <item><description>
    ///     Return <c>verified=true</c> only when ALL writing rungs include the condition;
    ///     include per-rung detail in every case.
    ///   </description></item>
    /// </list>
    ///
    /// Never throws; returns <see cref="VerifyInterlockError"/> on invalid input or
    /// missing project.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="outputTag">
    /// Tag whose write locations should be found (case-insensitive).
    /// </param>
    /// <param name="conditionTag">
    /// Tag to look for as an XIC or XIO contact on each writing rung (case-insensitive).
    /// </param>
    /// <returns>
    /// A <see cref="VerifyInterlockResult"/> with per-rung detail on success,
    /// or a <see cref="VerifyInterlockError"/> on invalid input, missing project,
    /// or when <paramref name="outputTag"/> is never written.
    /// </returns>
    /// <param name="session">Optional tag session whose controller image <paramref name="includePending"/> may read.</param>
    /// <param name="includePending">
    /// Read the session's controller image instead of the project file. Defaults to <c>true</c> when the session is
    /// SDK-online and holds an image, otherwise <c>false</c>.
    /// </param>
    public static object Execute(
        ProjectService service, string outputTag, string conditionTag,
        TagSessionService? session = null, bool? includePending = null)
    {
        var selection = AnalysisImage.Resolve(service, session, includePending);
        if (selection.Project is null)
        {
            var np = service.NoProjectLoadedError("logix_verify_interlock");
            return new VerifyInterlockError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }

        if (string.IsNullOrWhiteSpace(outputTag))
            return new VerifyInterlockError("outputTag must not be empty.");

        if (string.IsNullOrWhiteSpace(conditionTag))
            return new VerifyInterlockError("conditionTag must not be empty.");

        var project   = selection.Project;
        foreach (var requested in new[] { outputTag, conditionTag })
        {
            var ambiguous = AmbiguousTagNames.Find(project, requested);
            if (ambiguous is not null)
                return new VerifyInterlockError(AmbiguousTagNames.Message(requested, ambiguous))
                {
                    Kind = ErrorKinds.AmbiguousTagName, Tag = requested.Trim(), Matches = ambiguous, Hint = AmbiguousTagNames.Hint,
                };
        }
        var staleNote = AnalysisImage.StaleNote(selection, outputTag, conditionTag);
        var scope   = new InterlockScope(project);
        var outKey  = scope.ResolveRequest(outputTag);
        var condKey = scope.ResolveRequest(conditionTag);
        var rungs   = new List<VerifyInterlockRungDetail>();
        var unparsed = new List<UnparsedRungEntry>();
        // Raw text of each unparsed rung, so an unparsed rung that names outputTag is not ignored.
        var unparsedMentions = false;
        var allRungs      = new List<InterlockChain.RungInfo>();     // every parsed ladder rung, for chain following
        var outputRungs   = new List<InterlockChain.RungInfo>();     // parallel to rungs
        var unparsedTexts = new List<string>();
        var writerCandidates = new List<string>();   // program/routine/rung of each unparsed rung naming outputTag

        foreach (var program in project.Programs)
        {
            foreach (var routine in program.Routines)
            {
                // Enumerating Rungs on a non-RLL routine throws — see WriteClassifier.IsLadder.
                // A tag driven only from FBD/SFC/ST is therefore invisible to this
                // proof; verified=true means "no ladder rung writes it unguarded".
                if (!WriteClassifier.IsLadder(routine)) continue;

                foreach (var rung in routine.Rungs)
                {
                    // A rung that cannot be parsed is reported, never fatal.
                    var before       = unparsed.Count;
                    var instructions = RungParser.ParseContained(rung, program.Name, routine.Name, unparsed);
                    if (unparsed.Count > before)
                    {
                        var text = rung.Text?.ToString() ?? string.Empty;
                        unparsedTexts.Add(text);
                        if (text.Contains(outKey.Expr, StringComparison.OrdinalIgnoreCase))
                        {
                            unparsedMentions = true;
                            writerCandidates.Add(unparsed[^1].Location);
                        }
                        continue;
                    }

                    var info = new InterlockChain.RungInfo(
                        program.Name, routine.Name, rung.Number, rung.Text?.ToString() ?? string.Empty, instructions);
                    allRungs.Add(info);

                    // ── Step 1: does this rung write outputTag? ────────────────
                    bool writesOutput = false;

                    foreach (var instruction in instructions)
                    {
                        if (writesOutput) break;

                        var args = instruction.Arguments.ToList();
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (WriteClassifier.ClassifyKind(instruction.Key, i) == "write"
                                && scope.Matches(outKey, program.Name, args[i].ToString()))
                            {
                                writesOutput = true;
                                break;
                            }
                        }
                    }

                    if (!writesOutput) continue;

                    // ── Step 2: does every path to the output pass through conditionTag? ──
                    var analysis = InterlockPathAnalysis.Analyze(
                        rung.Text?.ToString() ?? string.Empty,
                        a => scope.Matches(outKey, program.Name, a),
                        a => scope.Matches(condKey, program.Name, a));

                    // Fall back to "appears as a contact" when the branch structure cannot be built;
                    // that can never prove the interlock, so it is not reported as guarded.
                    if (analysis is null)
                    {
                        analysis = new InterlockPathAnalysis.Result(
                            FlatConditionPresent(instructions, a => scope.Matches(condKey, program.Name, a), out var flatPolarity),
                            flatPolarity, Guarded: false, Array.Empty<string>());
                    }

                    var conditionPresent = analysis.ConditionPresent;
                    var polarity         = analysis.Polarity;
                    var status           = analysis.Guarded ? "guarded"
                                         : conditionPresent
                                             ? (analysis.ConditionAfterOutput && analysis.BypassPaths.Count == 0 ? "condition-after-output" : "bypassed")
                                         : "absent";
                    var bypassPaths      = status == "bypassed" && analysis.BypassPaths.Count > 0
                        ? analysis.BypassPaths.Select(UntrustedWrapper.WrapRungText).ToList()
                        : null;

                    // RungText is customer-authored ladder text from the project file; wrap it
                    // so the model can distinguish project content from server-authored text.
                    outputRungs.Add(info);
                    rungs.Add(new VerifyInterlockRungDetail(
                        Program:          program.Name,
                        Routine:          routine.Name,
                        RungNumber:       rung.Number,
                        RungText:         UntrustedWrapper.WrapRungText(rung.Text?.ToString() ?? string.Empty),
                        ConditionPresent: conditionPresent,
                        Polarity:         polarity,
                        Status:           status,
                        BypassPaths:      bypassPaths));
                }
            }
        }

        // includePending: false leaves pending rungs out; a pending writer must not make the tag look unwritten.
        var pendingWriters = AnalysisImage.PendingWriters(selection, pending => FindPendingWriters(pending, outputTag),
            session is null ? null : session.ZoneTested);
        var pendingList    = pendingWriters.Count == 0 ? null : pendingWriters;

        // ── Step 3: outputTag not written anywhere ──────────────────────────────
        if (rungs.Count == 0)
        {
            // An unparsed rung that names the tag may be its writer: say so rather than claim nobody writes it.
            var message = writerCandidates.Count > 0
                ? $"Output tag '{outputTag}' has no parsed writer rung; writer rung unparsed: {string.Join(", ", writerCandidates)}. " +
                  "It may write the tag without the condition, so the interlock cannot be checked."
                : pendingWriters.Count > 0
                    ? AnalysisImage.NotWrittenByAccepted($"Output tag '{outputTag}'", pendingWriters)
                    : $"Output tag '{outputTag}' is not written by any rung in the project.";
            if (pendingWriters.Count > 0 && writerCandidates.Count > 0)
                message += $" {AnalysisImage.DescribePending(pendingWriters)} also " +
                           $"{(pendingWriters.Select(w => (w.Program, w.Routine, w.RungIndex)).Distinct().Count() == 1 ? "writes" : "write")} it. " +
                           "Pass includePending:true to include it.";
            if (unparsed.Count > 0)
                message += $" {unparsed.Count} rung(s) could not be parsed and were not checked" +
                           (unparsedMentions ? ", and at least one of them mentions the tag" : string.Empty) +
                           "; see unparsedRungs.";
            if (staleNote is not null) message += $" Note: {staleNote}";
            return new VerifyInterlockError(message)
            { UnparsedRungs = unparsed.Count == 0 ? null : unparsed, PendingWritersExcluded = pendingList };
        }

        // ── Step 4: follow chains of intermediate bits for rungs not directly guarded ──
        // Prove with the real depth limit first; if that fails, retry with a generous limit so a chain that
        // exists but is too deep can be reported as "depth-limit" rather than a plain failure.
        var shallow = InterlockChain.Evaluate(allRungs, unparsedTexts, outputRungs, rungs, scope, outKey, condKey, InterlockChain.MaxDepth);

        // ── Step 5: verified only when ALL writing rungs are guarded (directly or through a chain) ─────
        // An unparsed rung that names the output tag may write it unguarded, so it blocks the proof.
        // "Guarded" means every path to the output passes through the contact; a bypassed rung is never verified.
        // An unguarded OTU of the output only clears it (the safe direction), so it does not block the proof
        // as long as some other rung sets the output under the condition.
        var unlatchOnly = outputRungs.Select(r => WritesOnlyByUnlatch(r, scope, outKey)).ToList();
        var finalRungs  = ApplyUnlatchExemption(shallow.Rungs, unlatchOnly);
        bool allGuarded = AllProven(finalRungs);
        bool verified   = allGuarded && !unparsedMentions;

        string? reason = null;
        string? xioBit = null;
        InterlockChain.Evaluation? deep = null;
        if (!verified && !unparsedMentions)
        {
            deep = InterlockChain.Evaluate(allRungs, unparsedTexts, outputRungs, rungs, scope, outKey, condKey, InterlockChain.ProbeDepth);
            var deepRungs = ApplyUnlatchExemption(deep.Rungs, unlatchOnly);
            if (AllProven(deepRungs) && deepRungs.Any(r => r.Status == "chained"))
                reason = "depth-limit";
            else
                deep = null;
        }
        if (reason is null && !verified && shallow.UnguardedWriters.Count > 0)
            reason = "unguarded-writer";
        if (reason is null && !verified && !unparsedMentions)
        {
            if (finalRungs.Any(r => r.Status == "condition-after-output") && !finalRungs.Any(r => r.Status == "bypassed"))
                reason = "condition-after-output";
            else
            {
                xioBit = InterlockChain.FindXioOfProvenBit(allRungs, unparsedTexts, outputRungs, finalRungs, scope, outKey, condKey);
                if (xioBit is not null) reason = "xio-of-guarded-bit";
            }
        }

        var shown = reason == "depth-limit" ? deep! : shallow;

        // Unguarded unlatch writers (of the output, and of chain bits) that were exempted from the proof.
        var unlatchWriters = new List<VerifyInterlockWriter>();
        if (verified)
        {
            for (int i = 0; i < finalRungs.Count; i++)
            {
                if (finalRungs[i].Status != "unlatch") continue;
                var r = outputRungs[i];
                unlatchWriters.Add(new VerifyInterlockWriter(outKey.Display, r.Program, r.Routine, r.Number, UntrustedWrapper.WrapRungText(r.Text)));
            }
            foreach (var w in shown.UnlatchWriters)
                if (!unlatchWriters.Any(u => u.Program == w.Program && u.Routine == w.Routine && u.RungNumber == w.RungNumber && u.Tag == w.Tag))
                    unlatchWriters.Add(w);
        }
        var listedWriters = shown.UnguardedWriters.Concat(unlatchWriters).ToList();
        var vias  = shown.Rungs.Where(r => r.Via is not null).SelectMany(r => r.Via!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string? note = null;
        if (unparsedMentions && allGuarded)
            note = $"Not verified: writer rung unparsed: {string.Join(", ", writerCandidates)}; it mentions the output tag and may write it without the condition. See unparsedRungs.";
        else if (reason == "depth-limit")
            note = $"Not verified (depth-limit): the output is interlocked only through a chain deeper than {InterlockChain.MaxDepth} intermediate bits. See chain.";
        else if (reason == "unguarded-writer")
            note = "Not verified: an intermediate bit in the chain has a writer that is not interlocked by the condition. See unguardedWriters.";
        else if (reason == "condition-after-output")
            note = "Not verified (condition-after-output): the condition contact is placed after the output instruction on the rung, so it does not gate the output.";
        else if (reason == "xio-of-guarded-bit")
            note = $"Not verified (xio-of-guarded-bit): the output is gated by XIO({xioBit}), and {xioBit} is interlocked by the condition. " +
                   "XIO passes when the bit is false, so it does not require the condition; only XIC of an interlocked bit does.";
        else if (verified && unlatchWriters.Count > 0)
            note = "unlatch writer not interlocked (unlatching is safe-direction)";
        else if (finalRungs.Any(r => r.Status == "bypassed"))
            note = "Bypassed: the condition is on some paths to the output but a branch reaches it without the condition. See bypassPaths on the rungs.";
        if (staleNote is not null) note = note is null ? staleNote : $"{note} {staleNote}";
        if (pendingWriters.Count > 0)
        {
            var pendingNote = $"Not proven for pending logic: {AnalysisImage.AlsoWrittenByPending(outputTag, pendingWriters)}";
            note = note is null ? pendingNote : $"{note} {pendingNote}";
        }

        // includePending: true reads the image with pending rungs applied: label the rungs that are not accepted logic.
        var included = CrossReferenceHandler.PendingRungStates(selection, session);
        if (included.Count > 0)
        {
            finalRungs = finalRungs.Select(r => included.TryGetValue(CrossReferenceHandler.StateKey(r.Program, r.Routine, r.RungNumber), out var st)
                ? r with { State = st } : r).ToList();
            listedWriters = listedWriters.Select(w => included.TryGetValue(CrossReferenceHandler.StateKey(w.Program, w.Routine, w.RungNumber), out var st)
                ? w with { State = st } : w).ToList();
            var includedNote = CrossReferenceHandler.IncludedPendingNote(finalRungs
                .Where(r => r.State is not null).Select(r => new PendingWriter(r.Program, r.Routine, r.RungNumber, outputTag, r.State!))
                .Concat(listedWriters.Where(w => w.State is not null).Select(w => new PendingWriter(w.Program, w.Routine, w.RungNumber, w.Tag, w.State!)))
                .GroupBy(w => (w.Program, w.Routine, w.RungIndex)).Select(g => g.First()).ToList());
            if (includedNote is not null) note = note is null ? includedNote : $"{note} {includedNote}";
        }

        return new VerifyInterlockResult(
            Verified:     verified,
            OutputTag:    outputTag,
            ConditionTag: conditionTag,
            Rungs:        finalRungs)
        {
            UnparsedRungs    = unparsed.Count == 0 ? null : unparsed,
            Note             = note,
            Via              = vias.Count == 0 ? null : vias,
            Chain            = shown.Chains.Count == 0 ? null : shown.Chains,
            Reason           = reason,
            UnguardedWriters = listedWriters.Count == 0 ? null : listedWriters,
            PendingWritersExcluded = pendingList,
        };
    }

    /// <summary>
    /// Finds the rungs of <paramref name="pending"/> (an image of pending-only rungs) that write
    /// <paramref name="outputTag"/>, using the same write test as the accepted-logic scan.
    /// </summary>
    /// <param name="pending">Image holding only pending rungs.</param>
    /// <param name="outputTag">The output tag as the caller gave it.</param>
    private static IEnumerable<PendingWriter> FindPendingWriters(L5X pending, string outputTag)
    {
        var scope  = new InterlockScope(pending);
        var outKey = scope.ResolveRequest(outputTag);
        var found  = new List<PendingWriter>();
        foreach (var program in pending.Programs)
            foreach (var routine in program.Routines)
            {
                if (!WriteClassifier.IsLadder(routine)) continue;
                foreach (var rung in routine.Rungs)
                {
                    var instructions = RungParser.ParseContained(rung, program.Name, routine.Name, new List<UnparsedRungEntry>());
                    var writes = instructions.Any(ins =>
                    {
                        var args = ins.Arguments.ToList();
                        return Enumerable.Range(0, args.Count).Any(i =>
                            WriteClassifier.ClassifyKind(ins.Key, i) == "write" && scope.Matches(outKey, program.Name, args[i].ToString()));
                    });
                    if (writes) found.Add(new PendingWriter(program.Name, routine.Name, rung.Number, outputTag.Trim()));
                }
            }
        return found;
    }

    /// <summary>True when every rung is proven; an exempted unlatch rung counts only if some other rung is proven.</summary>
    private static bool AllProven(IReadOnlyList<VerifyInterlockRungDetail> rungs) =>
        rungs.All(r => r.Status is "guarded" or "chained" or "unlatch")
        && rungs.Any(r => r.Status is "guarded" or "chained");

    /// <summary>
    /// Marks rungs that only unlatch (OTU) the output and are not interlocked as <c>"unlatch"</c>, provided at least
    /// one rung does something else to the output. When every writer is an unlatch, nothing is exempted.
    /// </summary>
    private static IReadOnlyList<VerifyInterlockRungDetail> ApplyUnlatchExemption(
        IReadOnlyList<VerifyInterlockRungDetail> rungs, IReadOnlyList<bool> unlatchOnly)
    {
        if (unlatchOnly.All(u => u)) return rungs;
        return rungs.Select((r, i) =>
            unlatchOnly[i] && r.Status is not ("guarded" or "chained")
                ? r with { Status = "unlatch", BypassPaths = null }
                : r).ToList();
    }

    /// <summary>True when the only writes the rung makes to <paramref name="key"/> are OTU.</summary>
    private static bool WritesOnlyByUnlatch(InterlockChain.RungInfo rung, InterlockScope scope, TagKey key)
    {
        var unlatch = false;
        foreach (var inst in rung.Instructions)
        {
            var args = inst.Arguments.ToList();
            for (int i = 0; i < args.Count; i++)
            {
                if (WriteClassifier.ClassifyKind(inst.Key, i) != "write" || !scope.Matches(key, rung.Program, args[i].ToString()))
                    continue;
                if (!string.Equals(inst.Key, "OTU", StringComparison.OrdinalIgnoreCase)) return false;
                unlatch = true;
            }
        }
        return unlatch;
    }

    /// <summary>Flat "contact appears anywhere on the rung" check, used only when the branch structure is unavailable.</summary>
    private static bool FlatConditionPresent(IEnumerable<Instruction> instructions, Func<string, bool> isCondition, out string? polarity)
    {
        polarity = null;
        foreach (var instruction in instructions)
        {
            if (!InterlockPathAnalysis.IsContact(instruction.Key)) continue;
            if (instruction.Arguments.Any(a => isCondition(a.ToString())))
            {
                polarity = instruction.Key.ToUpperInvariant();
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Path-aware model of one rung for <see cref="VerifyInterlockHandler"/>. A condition interlocks the
/// output only if every path from the left rail to the output passes through the contact in series;
/// a contact in just some legs of a parallel branch is reported as a bypass naming the skipping leg.
/// </summary>
internal static class InterlockPathAnalysis
{
    /// <summary>Outcome for one rung.</summary>
    /// <param name="ConditionPresent">The condition appears as an XIC/XIO contact somewhere on the rung.</param>
    /// <param name="Polarity"><c>XIC</c> or <c>XIO</c> of the first matching contact, else <c>null</c>.</param>
    /// <param name="Guarded">Every path to every write of the output passes through the contact.</param>
    /// <param name="BypassPaths">Branch paths that reach the output without the contact.</param>
    /// <param name="ConditionAfterOutput">The contact appears after the output instruction, so it cannot gate it.</param>
    internal sealed record Result(bool ConditionPresent, string? Polarity, bool Guarded, IReadOnlyList<string> BypassPaths, bool ConditionAfterOutput = false);

    /// <summary>True for the bit-test instructions XIC and XIO.</summary>
    internal static bool IsContact(string key) =>
        string.Equals(key, "XIC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "XIO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Analyses <paramref name="rungText"/>. Returns <c>null</c> when the branch structure cannot be parsed.
    /// A rung with no write of <paramref name="outputTag"/> is reported as not guarded.
    /// </summary>
    /// <param name="rungText">Rung neutral text.</param>
    /// <param name="isOutput">Whether an operand (as written on the rung) is the tag written by the output instruction.</param>
    /// <param name="isCondition">Whether an operand (as written on the rung) is the tag tested by the interlock contact.</param>
    internal static Result? Analyze(string rungText, Func<string, bool> isOutput, Func<string, bool> isCondition)
    {
        if (!RungStructure.TryParse(rungText, out var nodes, out _))
            return null;

        var state = new State(isOutput, isCondition);
        Walk(nodes, new Flow(false, new List<string>()), string.Empty, state);

        var guarded = state.Writes > 0 && state.Unguarded.Count == 0 && state.Unconstrained == 0;
        var paths   = state.Unguarded.Distinct().ToList();
        return new Result(state.ConditionPresent, state.Polarity, guarded, paths, state.ConditionAfterOutput);
    }

    private sealed class State
    {
        public State(Func<string, bool> output, Func<string, bool> condition) { Output = output; Condition = condition; }
        public Func<string, bool> Output { get; }
        public Func<string, bool> Condition { get; }
        public bool    ConditionPresent;
        public bool    ConditionAfterOutput;        // a condition contact follows an unguarded write
        public string? Polarity;
        public int     Writes;
        public int     Unconstrained;               // writes reached unguarded with no path to name
        public List<string> Unguarded { get; } = new();
    }

    /// <summary>Whether all paths so far crossed the contact, and the known paths that did not.</summary>
    private sealed record Flow(bool Guarded, List<string> Skipped);

    private static Flow Walk(IReadOnlyList<RungNode> seq, Flow entry, string context, State st)
    {
        var flow = new Flow(entry.Guarded, new List<string>(entry.Skipped));
        foreach (var node in seq)
        {
            if (node is InstructionNode inst)
            {
                if (IsContact(inst.Key) && inst.Args.Any(st.Condition))
                {
                    st.ConditionPresent = true;
                    if (st.Writes > 0 && !flow.Guarded) st.ConditionAfterOutput = true;
                    st.Polarity ??= inst.Key.ToUpperInvariant();
                    flow = new Flow(true, new List<string>());
                }
                else if (WritesOutput(inst, st.Output))
                {
                    st.Writes++;
                    if (!flow.Guarded)
                    {
                        if (flow.Skipped.Count > 0) st.Unguarded.AddRange(flow.Skipped);
                        else if (context.Length > 0) st.Unguarded.Add(context);
                        else st.Unconstrained++;
                    }
                }
            }
            else if (node is BranchNode branch)
            {
                var text = Render(branch);
                var exits = new List<Flow>();
                for (int i = 0; i < branch.Legs.Count; i++)
                {
                    var legContext = $"leg {i + 1} of {text}: {RenderLeg(branch.Legs[i])}";
                    exits.Add(Walk(branch.Legs[i], flow, legContext, st));
                }

                if (flow.Guarded) continue;     // already guarded before the branch; stays guarded

                if (exits.All(e => e.Guarded))
                {
                    flow = new Flow(true, new List<string>());
                }
                else
                {
                    var skipped = new List<string>(flow.Skipped);
                    var anyGuardedLeg = exits.Any(e => e.Guarded);
                    for (int i = 0; i < exits.Count; i++)
                    {
                        if (exits[i].Guarded) continue;
                        if (exits[i].Skipped.Count > 0) skipped.AddRange(exits[i].Skipped);
                        else if (anyGuardedLeg) skipped.Add($"leg {i + 1} of {text}: {RenderLeg(branch.Legs[i])}");
                    }
                    flow = new Flow(false, skipped);
                }
            }
        }
        return flow;
    }

    private static bool WritesOutput(InstructionNode inst, Func<string, bool> output)
    {
        for (int i = 0; i < inst.Args.Count; i++)
        {
            if (WriteClassifier.ClassifyKind(inst.Key, i) == "write"
                && output(inst.Args[i]))
                return true;
        }
        return false;
    }

    private static string RenderLeg(IReadOnlyList<RungNode> leg) =>
        leg.Count == 0 ? "(empty wire)" : string.Concat(leg.Select(Render));

    private static string Render(RungNode node) => node switch
    {
        InstructionNode i => $"{i.Key}({string.Join(",", i.Args)})",
        BranchNode b      => "[" + string.Join(",", b.Legs.Select(l => string.Concat(l.Select(Render)))) + "]",
        _                 => string.Empty,
    };
}

/// <summary>
/// Follows chains of intermediate bits for <see cref="VerifyInterlockHandler"/>. An output rung that is gated
/// (in series, by the same series/branch rule) by a bit B is interlocked if every writer of B is itself interlocked
/// by the condition, directly or through a further bit, to a depth of <see cref="MaxDepth"/> intermediate bits.
/// A bit is made true only by OTE/OTL (or another writing instruction); OTU can only clear it, so an OTU rung is
/// not counted as a writer. Only XIC contacts are followed, since only XIC(B) requires B to be true.
/// </summary>
internal static class InterlockChain
{
    /// <summary>Maximum number of intermediate bits in a verified chain.</summary>
    internal const int MaxDepth = 3;

    /// <summary>Limit used to detect (and list) a chain that exists but is deeper than <see cref="MaxDepth"/>.</summary>
    internal const int ProbeDepth = 32;

    /// <summary>One parsed ladder rung.</summary>
    internal sealed record RungInfo(string Program, string Routine, int Number, string Text, IReadOnlyList<Instruction> Instructions);

    /// <summary>Per-rung outcome plus chains and breaking writers for one depth limit.</summary>
    internal sealed record Evaluation(
        IReadOnlyList<VerifyInterlockRungDetail> Rungs,
        IReadOnlyList<string> Chains,
        IReadOnlyList<VerifyInterlockWriter> UnguardedWriters,
        IReadOnlyList<VerifyInterlockWriter> UnlatchWriters);

    private sealed class Ctx
    {
        public required IReadOnlyList<RungInfo> All { get; init; }
        public required IReadOnlyList<string> UnparsedTexts { get; init; }
        public required InterlockScope Scope { get; init; }
        public required TagKey Output { get; init; }
        public required TagKey Condition { get; init; }
        public required int Limit { get; init; }
        public List<VerifyInterlockWriter> Failing { get; } = new();
        public List<VerifyInterlockWriter> Unlatch { get; } = new();   // unguarded OTU writers of bits in a proven chain
    }

    /// <summary>
    /// Re-evaluates the rungs that write the output and are not directly guarded, trying to prove them
    /// through intermediate bits within <paramref name="limit"/>. Bits are tracked by scope, so a program tag
    /// is never confused with a same-named tag in another program or the controller.
    /// </summary>
    /// <param name="all">Every parsed ladder rung in the project.</param>
    /// <param name="unparsedTexts">Text of rungs that could not be parsed (they may write any tag they mention).</param>
    /// <param name="outputRungs">The rungs that write the output, parallel to <paramref name="details"/>.</param>
    /// <param name="details">Direct per-rung results for those rungs.</param>
    /// <param name="scope">Resolves operands to their owning scope.</param>
    /// <param name="output">Output tag.</param>
    /// <param name="condition">Condition tag.</param>
    /// <param name="limit">Maximum number of intermediate bits.</param>
    internal static Evaluation Evaluate(
        IReadOnlyList<RungInfo> all, IReadOnlyList<string> unparsedTexts,
        IReadOnlyList<RungInfo> outputRungs, IReadOnlyList<VerifyInterlockRungDetail> details,
        InterlockScope scope, TagKey output, TagKey condition, int limit)
    {
        var ctx = new Ctx
        {
            All = all, UnparsedTexts = unparsedTexts, Scope = scope,
            Output = output, Condition = condition, Limit = limit,
        };
        var result = new List<VerifyInterlockRungDetail>();
        var chains = new List<string>();

        for (int i = 0; i < details.Count; i++)
        {
            var d = details[i];
            if (d.Status == "guarded") { result.Add(d); continue; }

            var proof = ProveRung(ctx, outputRungs[i], output, new List<TagKey>(), direct: false);
            if (proof is null) { result.Add(d); continue; }

            var via = proof.SelectMany(c => c).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var c in proof)
            {
                var text = string.Join(" <- ", new[] { output.Display }.Concat(c).Append(condition.Display));
                if (!chains.Contains(text)) chains.Add(text);
            }
            result.Add(d with { Status = "chained", Via = via, BypassPaths = null });
        }
        return new Evaluation(result, chains, ctx.Failing, ctx.Unlatch);
    }

    /// <summary>
    /// Proves one rung that writes <paramref name="target"/>. Returns the chains (each the intermediate bits after
    /// <paramref name="target"/>; one empty chain for a direct guard) or <c>null</c> when it cannot be proven.
    /// </summary>
    private static List<List<string>>? ProveRung(Ctx ctx, RungInfo rung, TagKey target, List<TagKey> vias, bool direct)
    {
        if (direct)
        {
            var a = InterlockPathAnalysis.Analyze(rung.Text, Is(ctx, target, rung), Is(ctx, ctx.Condition, rung));
            if (a is { Guarded: true }) return new List<List<string>> { new() };
        }

        var before = ctx.Failing.Count;
        if (vias.Count < ctx.Limit)
        {
            foreach (var cand in GatingCandidates(rung, target, ctx, vias))
            {
                var gate = InterlockPathAnalysis.Analyze(rung.Text, Is(ctx, target, rung), Is(ctx, cand, rung));
                if (gate is not { Guarded: true }) continue;

                var next = new List<TagKey>(vias) { cand };
                var sub = ProveTag(ctx, cand, next);
                if (sub is not null)
                {
                    ctx.Failing.RemoveRange(before, ctx.Failing.Count - before);   // an earlier candidate's failures are moot
                    return sub;
                }
            }
        }

        // Name this rung when nothing deeper explained the failure (it is the breaking writer of a chain bit).
        if (vias.Count > 0 && ctx.Failing.Count == before)
            AddFailing(ctx, rung, target);
        return null;
    }

    /// <summary>
    /// Proves that every writer of <paramref name="tag"/> is interlocked. Returns chains starting with
    /// <paramref name="tag"/>, or <c>null</c> when there is no writer, a writer is not interlocked, or an
    /// unparsed rung mentions the tag.
    /// </summary>
    private static List<List<string>>? ProveTag(Ctx ctx, TagKey tag, List<TagKey> vias)
    {
        var mention = WriteClassifier.ExtractBaseName(tag.Expr) ?? tag.Expr;
        if (ctx.UnparsedTexts.Any(t => t.Contains(mention, StringComparison.OrdinalIgnoreCase)))
            return null;

        var writers = ctx.All.Where(r => SetsTag(ctx, r, tag)).ToList();
        if (writers.Count == 0) return null;

        var chains = new List<List<string>>();
        var ok = true;
        var unlatchMark = ctx.Unlatch.Count;
        foreach (var w in writers)
        {
            var sub = ProveRung(ctx, w, tag, vias, direct: true);
            if (sub is null) { ok = false; continue; }   // keep going so every breaking writer is named
            foreach (var s in sub)
            {
                var chain = new List<string> { tag.Display };
                chain.AddRange(s);
                if (!chains.Any(c => c.SequenceEqual(chain, StringComparer.OrdinalIgnoreCase))) chains.Add(chain);
            }
        }
        if (!ok) { ctx.Unlatch.RemoveRange(unlatchMark, ctx.Unlatch.Count - unlatchMark); return null; }

        // Unguarded unlatch writers do not break the chain (OTU only clears the bit) but are reported.
        foreach (var r in ctx.All)
        {
            if (SetsTag(ctx, r, tag) || !UnlatchesTag(ctx, r, tag)) continue;
            var a = InterlockPathAnalysis.Analyze(r.Text, Is(ctx, tag, r), Is(ctx, ctx.Condition, r));
            if (a is { Guarded: true }) continue;
            if (ctx.Unlatch.Any(u => u.Program == r.Program && u.Routine == r.Routine && u.RungNumber == r.Number && u.Tag == tag.Display)) continue;
            ctx.Unlatch.Add(new VerifyInterlockWriter(tag.Display, r.Program, r.Routine, r.Number, UntrustedWrapper.WrapRungText(r.Text)));
        }
        return chains;
    }

    /// <summary>
    /// Finds a bit that gates an unproven output rung through <c>XIO</c> and that is itself interlocked by the
    /// condition. XIO passes when the bit is false, so it cannot require the condition. Returns the bit, or <c>null</c>.
    /// </summary>
    /// <param name="all">Every parsed ladder rung in the project.</param>
    /// <param name="unparsedTexts">Text of rungs that could not be parsed.</param>
    /// <param name="outputRungs">The rungs that write the output, parallel to <paramref name="details"/>.</param>
    /// <param name="details">Per-rung results for those rungs.</param>
    /// <param name="scope">Resolves operands to their owning scope.</param>
    /// <param name="output">Output tag.</param>
    /// <param name="condition">Condition tag.</param>
    internal static string? FindXioOfProvenBit(
        IReadOnlyList<RungInfo> all, IReadOnlyList<string> unparsedTexts,
        IReadOnlyList<RungInfo> outputRungs, IReadOnlyList<VerifyInterlockRungDetail> details,
        InterlockScope scope, TagKey output, TagKey condition)
    {
        var ctx = new Ctx
        {
            All = all, UnparsedTexts = unparsedTexts, Scope = scope,
            Output = output, Condition = condition, Limit = MaxDepth,
        };
        for (int i = 0; i < details.Count; i++)
        {
            if (details[i].Status is "guarded" or "chained" or "unlatch") continue;
            var rung = outputRungs[i];
            foreach (var inst in rung.Instructions)
            {
                if (!string.Equals(inst.Key, "XIO", StringComparison.OrdinalIgnoreCase)) continue;
                var arg = inst.Arguments.FirstOrDefault()?.ToString();
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (scope.Matches(condition, rung.Program, arg) || scope.Matches(output, rung.Program, arg)) continue;
                var cand = scope.ResolveOperand(rung.Program, arg);
                var gate = InterlockPathAnalysis.Analyze(rung.Text, Is(ctx, output, rung), Is(ctx, cand, rung));
                if (gate is not { Guarded: true }) continue;
                var direct = all.Where(r => SetsTag(ctx, r, cand)).ToList();
                if (direct.Count > 0 && ProveTag(ctx, cand, new List<TagKey> { cand }) is not null)
                    return cand.Display;
            }
        }
        return null;
    }

    /// <summary>True when the rung writes <paramref name="tag"/> with OTU.</summary>
    private static bool UnlatchesTag(Ctx ctx, RungInfo rung, TagKey tag)
    {
        foreach (var inst in rung.Instructions)
        {
            if (!string.Equals(inst.Key, "OTU", StringComparison.OrdinalIgnoreCase)) continue;
            var args = inst.Arguments.ToList();
            for (int i = 0; i < args.Count; i++)
                if (WriteClassifier.ClassifyKind(inst.Key, i) == "write" && ctx.Scope.Matches(tag, rung.Program, args[i].ToString()))
                    return true;
        }
        return false;
    }

    /// <summary>Operand test for <paramref name="key"/> as written on a rung of <paramref name="rung"/>'s program.</summary>
    private static Func<string, bool> Is(Ctx ctx, TagKey key, RungInfo rung) =>
        operand => ctx.Scope.Matches(key, rung.Program, operand);

    /// <summary>XIC-tested bits on the rung that could gate <paramref name="target"/>, excluding cycles and the condition itself.</summary>
    private static IEnumerable<TagKey> GatingCandidates(RungInfo rung, TagKey target, Ctx ctx, List<TagKey> vias)
    {
        var seen = new HashSet<(string?, string)>();
        foreach (var inst in rung.Instructions)
        {
            if (!string.Equals(inst.Key, "XIC", StringComparison.OrdinalIgnoreCase)) continue;
            var arg = inst.Arguments.FirstOrDefault()?.ToString();
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (ctx.Scope.Matches(ctx.Condition, rung.Program, arg)
                || ctx.Scope.Matches(ctx.Output, rung.Program, arg)
                || ctx.Scope.Matches(target, rung.Program, arg)
                || vias.Any(v => ctx.Scope.Matches(v, rung.Program, arg)))
                continue;
            var cand = ctx.Scope.ResolveOperand(rung.Program, arg);
            if (seen.Add((cand.Scope?.ToUpperInvariant(), cand.Expr.ToUpperInvariant()))) yield return cand;
        }
    }

    /// <summary>True when the rung can make <paramref name="tag"/> true (a write other than OTU).</summary>
    private static bool SetsTag(Ctx ctx, RungInfo rung, TagKey tag)
    {
        foreach (var inst in rung.Instructions)
        {
            if (string.Equals(inst.Key, "OTU", StringComparison.OrdinalIgnoreCase)) continue;
            var args = inst.Arguments.ToList();
            for (int i = 0; i < args.Count; i++)
            {
                if (WriteClassifier.ClassifyKind(inst.Key, i) == "write"
                    && ctx.Scope.Matches(tag, rung.Program, args[i].ToString()))
                    return true;
            }
        }
        return false;
    }

    private static void AddFailing(Ctx ctx, RungInfo rung, TagKey tag)
    {
        if (ctx.Failing.Any(f => f.Program == rung.Program && f.Routine == rung.Routine && f.RungNumber == rung.Number && f.Tag == tag.Display))
            return;
        ctx.Failing.Add(new VerifyInterlockWriter(tag.Display, rung.Program, rung.Routine, rung.Number,
            UntrustedWrapper.WrapRungText(rung.Text)));
    }
}

/// <summary>
/// A tag as <see cref="VerifyInterlockHandler"/> tracks it: the expression within its scope plus the scope.
/// </summary>
/// <param name="Scope">
/// Owning program, <see cref="TagScopes.Controller"/> (empty) for a controller tag, or <c>null</c> when the
/// caller named the tag without a scope and no controller tag of that name exists (any scope matches).
/// </param>
/// <param name="Expr">Tag expression within the scope (members and indexes included), no program qualifier.</param>
/// <param name="Display">How the tag is shown in chains and writer lists.</param>
internal sealed record TagKey(string? Scope, string Expr, string Display);

/// <summary>
/// Scope-aware operand matching for <see cref="VerifyInterlockHandler"/>: the same tag name in two programs
/// (or in a program and the controller) is two tags, and <c>\Program.Tag</c> reaches the named program's tag.
/// </summary>
internal sealed class InterlockScope
{
    private readonly L5X _project;
    private readonly TagScopes _scopes;

    /// <summary>Creates a resolver over <paramref name="project"/>.</summary>
    /// <param name="project">The loaded project.</param>
    public InterlockScope(L5X project)
    {
        _project = project;
        _scopes = new TagScopes(project);
    }

    /// <summary>
    /// Resolves a tag named by the caller. <c>\P2.Bit</c>, <c>Program:P2.Bit</c> and <c>P2.Bit</c> (P2 a
    /// program, no controller tag called P2) are scoped to P2; a plain name is the controller tag when one
    /// exists, otherwise it matches that name in any program.
    /// </summary>
    /// <param name="text">Tag as the caller gave it.</param>
    public TagKey ResolveRequest(string text)
    {
        var trimmed = text.Trim();
        var (program, tag) = CrossReferenceHandler.ResolveTarget(_project, trimmed);
        if (program is not null) return new TagKey(program, tag, trimmed);
        var baseName = WriteClassifier.ExtractBaseName(tag) ?? tag;
        return new TagKey(_scopes.ControllerHas(baseName) ? TagScopes.Controller : null, tag, trimmed);
    }

    /// <summary>Resolves an operand written on a rung of <paramref name="program"/> to a concrete scope.</summary>
    /// <param name="program">Program containing the rung.</param>
    /// <param name="operandText">Operand as it appears in the rung.</param>
    public TagKey ResolveOperand(string program, string operandText)
    {
        var op = RungOperand.Parse(operandText);
        return new TagKey(_scopes.Resolve(program, op), op.Tag, op.IsProgramQualified ? op.Qualified : op.Tag);
    }

    /// <summary>
    /// True when <paramref name="operandText"/>, written on a rung of <paramref name="program"/>, is
    /// <paramref name="key"/> or a member / element of it.
    /// </summary>
    /// <param name="key">The tag to look for.</param>
    /// <param name="program">Program containing the rung.</param>
    /// <param name="operandText">Operand as it appears in the rung.</param>
    public bool Matches(TagKey key, string program, string operandText)
    {
        var op = RungOperand.Parse(operandText);
        if (!WriteClassifier.ArgumentMatchesTag(op.Tag, key.Expr)) return false;
        if (key.Scope is null) return true;
        // An unqualified operand inside the named program is that program's tag even if the project does not declare it.
        if (!op.IsProgramQualified && key.Scope.Length > 0)
            return string.Equals(program, key.Scope, StringComparison.OrdinalIgnoreCase);
        return string.Equals(_scopes.Resolve(program, op), key.Scope, StringComparison.OrdinalIgnoreCase);
    }
}
