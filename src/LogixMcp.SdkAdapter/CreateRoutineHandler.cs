#if LOGIX_SDK
// ── CreateRoutineHandler — creates an empty ladder routine in a program ───────
//    through the SDK's named-object import (logix_create_routine)
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task
// when both namespaces are in scope.
using Task = System.Threading.Tasks.Task;
using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Successful outcome of <c>logix_create_routine</c>.
/// </summary>
/// <param name="Status">Always <c>"created"</c>.</param>
/// <param name="RoutineXPath">
/// SDK XPath of the new routine, ready to pass to <c>logix_propose_rung_edit</c> and
/// <c>logix_export_rungs</c>.
/// </param>
/// <param name="CreatedInController">
/// <c>true</c>: only online sessions can create a routine (an offline session is refused with
/// <c>OfflineCreateNotSupported</c>), so the routine is in the controller's project image.
/// </param>
/// <param name="Undo">
/// How to take the routine back out. The routine stays even when later edits are cancelled and the
/// SDK has no routine delete; remove it in Studio.
/// </param>
/// <param name="Program">Program the routine was created in.</param>
/// <param name="Name">Name of the new routine.</param>
/// <param name="Type">Routine type; always <c>"RLL"</c> in this version.</param>
public record CreateRoutineResult(
    [property: JsonPropertyName("status")]              string Status,
    [property: JsonPropertyName("routineXPath")]        string RoutineXPath,
    [property: JsonPropertyName("createdInController")] bool   CreatedInController,
    [property: JsonPropertyName("undo")]                string Undo,
    [property: JsonPropertyName("program")]             string Program,
    [property: JsonPropertyName("name")]                string Name,
    [property: JsonPropertyName("type")]                string Type);

/// <summary>
/// Structured failure of <c>logix_create_routine</c>.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
/// <param name="Kind">
/// Machine-readable category, for example <c>PendingEditsBlockImport</c>, <c>RoutineAlreadyExists</c>,
/// <c>InvalidRoutineName</c>, <c>UnsupportedRoutineType</c>, <c>ConfirmationRequired</c>,
/// <c>NotOnAllowlist</c>, <c>OfflineCreateNotSupported</c>, <c>CreateNotConfirmed</c> or <c>ImportFailed</c>.
/// </param>
/// <param name="Hint">Actionable next step; omitted when none.</param>
/// <param name="ImportAttempted">
/// <c>true</c> when the SDK import was called. <c>false</c> on every refusal made before it.
/// </param>
/// <param name="Routines">Routine or pending-edit detail relevant to the failure; omitted when none.</param>
/// <param name="ImportLog">The importer's log entries on <c>ImportFailed</c>; omitted otherwise.</param>
/// <param name="Programs">The project's program names (exported casing) on <c>ProgramNotFound</c>; omitted otherwise.</param>
public record CreateRoutineError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind")]  string Kind,
    [property: JsonPropertyName("hint"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Hint = null,
    [property: JsonPropertyName("importAttempted")] bool ImportAttempted = false,
    [property: JsonPropertyName("routines"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? Routines = null,
    [property: JsonPropertyName("importLog"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<ImportLogEntry>? ImportLog = null,
    [property: JsonPropertyName("programs"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? Programs = null);

// ── CreateRoutineHandler ──────────────────────────────────────────────────────

/// <summary>
/// Creates an empty ladder (<c>RLL</c>) routine in a program of the open SDK session, so that
/// <c>logix_propose_rung_edit</c> has somewhere to stage rungs.
///
/// <para>
/// The routine is made with <c>PartialImportWithTargetFromXmlFileAsync</c> (the named-object import,
/// which renames its target at import time) into <c>&lt;program&gt;/Routines</c>, always with
/// <see cref="PartialImportOption.LeaveEdits"/>: that option never finalizes other pending edits.
/// The import envelope is an export of an existing RLL routine of the same program, renamed and
/// emptied. When the program has no RLL routine it is built from the program export.
/// </para>
/// </summary>
public static class CreateRoutineHandler
{
    /// <summary>Longest routine name Logix accepts.</summary>
    internal const int MaxNameLength = 40;

    /// <summary>Longest description accepted.</summary>
    internal const int MaxDescriptionLength = 500;

    /// <summary>Largest import envelope this tool will write, in bytes.</summary>
    internal const int MaxEnvelopeBytes = 30 * 1024;

    /// <summary>Routine types this version can create.</summary>
    internal const string RllType = "RLL";

    /// <summary>
    /// Undo note returned on success. The SDK has no routine delete and an edit cancel does not remove
    /// a routine created by an import.
    /// </summary>
    internal const string UndoNote =
        "The routine stays even if later edits are cancelled (logix_cancel_pending_edits does not " +
        "remove it), and the Logix Designer SDK has no routine delete. To remove it, delete the " +
        "routine in Studio 5000 (Logical Organizer, right-click the routine, Delete).";

    /// <summary><c>kind</c> of the refusal returned for an offline session.</summary>
    internal const string OfflineCreateNotSupportedKind = "OfflineCreateNotSupported";

    /// <summary>Hint returned with <see cref="OfflineCreateNotSupportedKind"/>.</summary>
    internal const string OfflineCreateNotSupportedHint =
        "Connect with an online SDK session (logix_connect to a controller) and call again, or create the " +
        "routine in Studio 5000 and save the project; then use logix_propose_rung_edit to stage rungs into it. " +
        "Rungs can still be written into an existing routine of an L5X file with " +
        "logix_propose_rung_edit offline=true.";

    // Logix names: a letter or underscore, then letters, digits and underscores; no two underscores in
    // a row and no trailing underscore; at most 40 characters.
    private static readonly Regex NameRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,39}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Structured-text keywords and operators that Logix refuses as routine names (compared
    /// case-insensitively). Instruction mnemonics such as MOVE are not reserved and are accepted.
    /// </summary>
    internal static readonly IReadOnlySet<string> ReservedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "XOR", "NOT", "MOD",
        "IF", "THEN", "ELSIF", "ELSE", "END_IF",
        "CASE", "OF", "END_CASE",
        "FOR", "TO", "BY", "DO", "END_FOR",
        "WHILE", "END_WHILE",
        "REPEAT", "UNTIL", "END_REPEAT",
        "EXIT", "RETURN",
    };

    /// <summary>
    /// Checks a routine name against the Logix (IEC 61131-3 identifier) rules.
    /// </summary>
    /// <param name="name">Candidate routine name.</param>
    /// <returns>A description of the first rule broken, or <c>null</c> when the name is valid.</returns>
    internal static string? ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "name must not be empty.";
        if (name.Length > MaxNameLength)
            return $"name is {name.Length} characters; the limit is {MaxNameLength}.";
        if (!NameRegex.IsMatch(name))
            return "name must start with a letter or underscore and contain only ASCII letters, digits and underscores.";
        if (name.Contains("__", StringComparison.Ordinal))
            return "name must not contain two underscores in a row.";
        if (name.EndsWith('_'))
            return "name must not end with an underscore.";
        if (ReservedWords.Contains(name))
            return $"'{name}' is a reserved word and cannot be used as a routine name.";
        return null;
    }

    /// <summary>
    /// The import-option guard: <see cref="PartialImportOption.LeaveEdits"/> is the only option this
    /// call may use. Anything else could finalize other pending edits.
    /// </summary>
    /// <param name="option">The option about to be passed to the SDK.</param>
    /// <returns><paramref name="option"/>, when it is <c>LeaveEdits</c>.</returns>
    /// <exception cref="InvalidOperationException">For any other option.</exception>
    internal static PartialImportOption RequireLeaveEdits(PartialImportOption option) =>
        option == PartialImportOption.LeaveEdits
            ? option
            : throw new InvalidOperationException(
                $"Internal error: import option '{option}' is not permitted for logix_create_routine. " +
                "Only LeaveEdits is allowed, so that creating a routine never finalizes other pending edits.");

    private static string ProgramXPath(string program) => $"Controller/Programs/Program[@Name='{program}']";

    private static string RoutineXPath(string program, string routine) =>
        $"{ProgramXPath(program)}/Routines/Routine[@Name='{routine}']";

    /// <summary>
    /// Creates an empty RLL routine.
    /// </summary>
    /// <param name="program">Program that will own the routine.</param>
    /// <param name="name">New routine name (Logix identifier rules, at most 40 characters).</param>
    /// <param name="type">Routine type; only <c>"RLL"</c> is supported.</param>
    /// <param name="description">Optional routine description.</param>
    /// <param name="confirmedByUser">Must be <c>true</c>; set only after the user approved this exact call.</param>
    /// <param name="session">Open SDK session. This method does not open or close it.</param>
    /// <param name="liveWritesEnabled">Whether <c>LGX_ENABLE_LIVE_WRITES</c> is set.</param>
    /// <param name="allowlist">The session's write allowlist, or <c>null</c> when none is set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CreateRoutineResult"/> on success, or a <see cref="CreateRoutineError"/> /
    /// <see cref="CapabilityDisabledError"/>. Never throws, except <see cref="OperationCanceledException"/>.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string?             program,
        string?             name,
        string?             type,
        string?             description,
        bool                confirmedByUser,
        LogixProjectSession session,
        bool                liveWritesEnabled,
        WriteAllowlist?     allowlist = null,
        CancellationToken   ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Dead-session short-circuit: a faulted SDK server is never called again.
        if (session.IsFaulted)
            return Faulted(session.FaultedError());

        // ── Gates — all before any SDK call ───────────────────────────────────
        if (!liveWritesEnabled)
        {
            AuditLogger.LogCapabilityRefusal("logix_create_routine", Capabilities.EnvVar);
            return new CapabilityDisabledError(
                Error:
                    "logix_create_routine refused: LGX_ENABLE_LIVE_WRITES is not set. Creating a routine " +
                    "changes the project the session holds (the controller's project when online). " +
                    "No import was attempted.",
                Hint:
                    $"Set the {Capabilities.EnvVar} environment variable to 1 to enable it. " +
                    $"Client command: {Capabilities.ClientCommandHint}");
        }

        if (!confirmedByUser)
            return new CreateRoutineError(
                "logix_create_routine refused: confirmedByUser is not true. Creating a routine adds a " +
                "permanent object that the SDK cannot delete again. No import was attempted.",
                "ConfirmationRequired",
                Hint:
                    "Tell the user the program and routine name, wait for their explicit approval in the " +
                    "conversation, and only then call again with confirmedByUser=true. The server cannot " +
                    "verify that approval; never set it on your own judgement or to retry this refusal.");

        // Option (b): an offline SDK session has no path that saves a created routine
        // back to the project file, so the routine would exist only in a throwaway in-memory image. Refuse
        // before any import rather than report a "created" routine that is lost when the session closes.
        if (!session.IsOnline)
            return new CreateRoutineError(
                "logix_create_routine is not supported on an offline session: the routine would exist only " +
                "in the session's in-memory project image and is never saved to the project file, so it " +
                "would be lost when the session closes. No import was attempted and nothing was saved.",
                OfflineCreateNotSupportedKind,
                Hint: OfflineCreateNotSupportedHint);

        if (string.IsNullOrWhiteSpace(program))
            return new CreateRoutineError("program must not be empty.", "InvalidProgram");
        if (program.Contains('\'') || program.Contains('"'))
            return new CreateRoutineError(
                "program must not contain a quote character; it cannot be safely embedded in the SDK XPath.",
                "InvalidProgram");

        if (allowlist is not null && !allowlist.Permits(program))
            return new CreateRoutineError(
                $"logix_create_routine refused: program '{program}' is not listed in the write allowlist " +
                $"file '{allowlist.Path}' ({WriteAllowlist.EnvVar}). No import was attempted.",
                "NotOnAllowlist",
                Hint: $"Ask the site to list the program name '{program}' in the allowlist file.");

        if (ValidateName(name) is { } nameProblem)
            return new CreateRoutineError(
                $"Invalid routine name '{name}': {nameProblem}",
                "InvalidRoutineName",
                Hint: "Use up to 40 letters, digits and single underscores, starting with a letter or underscore.");
        var routineName = name!;

        if (!string.Equals(type ?? RllType, RllType, StringComparison.OrdinalIgnoreCase))
            return new CreateRoutineError(
                $"Routine type '{type}' is not supported. Only 'RLL' (ladder) can be created.",
                "UnsupportedRoutineType",
                Hint: "Pass type='RLL', or omit it.");

        if (description is not null && description.Length > MaxDescriptionLength)
            return new CreateRoutineError(
                $"description is {description.Length} characters; the limit is {MaxDescriptionLength}.",
                "InvalidDescription");

        if (session.IsOnline && !session.IsCorrelated)
            return new CreateRoutineError(
                "Session is not correlated. Creating a routine on an online session requires the " +
                "controller and project images to be aligned.",
                "SessionNotCorrelated",
                Hint: "Call logix_connect again so the session is correlated, then retry.");

        // ── Read the program (one export serves the collision, envelope and pending checks) ──
        var programXPath = ProgramXPath(program);
        var programExport = await ExportRungsHandler.ExecuteAsync(programXPath, session, ct).ConfigureAwait(false);
        if (programExport is ExportRungsError exportError)
        {
            if (exportError.Kind == ErrorKinds.SdkServerFaulted)
                return Faulted(exportError);

            // The SDK text for a missing program is raw and unhelpful. Resolve the name against the
            // project's program list (the same read logix_propose_rung_edit uses, so the case-insensitive
            // match and exported casing are not re-implemented here).
            var (programs, listError) = await ProposeRungEditHandler.ReadProgramRoutinesAsync(session, ct)
                .ConfigureAwait(false);
            if (programs is not null)
            {
                var names = programs.Select(p => p.Program).ToList();
                var canonical = names.FirstOrDefault(n =>
                    string.Equals(n, program, StringComparison.OrdinalIgnoreCase));
                if (canonical is null)
                    return new CreateRoutineError(
                        $"Program '{program}' does not exist in this project. Programs in this project: " +
                        $"{(names.Count == 0 ? "(none)" : string.Join(", ", names))}. No import was attempted.",
                        "ProgramNotFound",
                        Hint: "Use one of the listed program names (case is ignored).",
                        Programs: names);

                if (!string.Equals(canonical, program, StringComparison.Ordinal))
                {
                    // Same program, different casing: use the exported casing from here on, so the
                    // returned routineXPath matches what the project exports.
                    program = canonical;
                    programXPath = ProgramXPath(program);
                    programExport = await ExportRungsHandler.ExecuteAsync(programXPath, session, ct)
                        .ConfigureAwait(false);
                    if (programExport is ExportRungsError retryError)
                        return retryError.Kind == ErrorKinds.SdkServerFaulted
                            ? Faulted(retryError)
                            : new CreateRoutineError(
                                $"Could not read program '{program}': {retryError.Error}",
                                "ProgramNotReadable",
                                Hint: "Check the program name with logix_project_summary.");
                }
            }

            if (programExport is ExportRungsError exportFailure)
                return new CreateRoutineError(
                    $"Could not read program '{program}': {exportFailure.Error}",
                    "ProgramNotReadable",
                    Hint: "Check the program name with logix_project_summary.");
        }

        XElement? programElement;
        try
        {
            programElement = FindProgram(
                SafeXml.ParseDocument(((ExportRungsResult)programExport).L5X), program);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new CreateRoutineError(
                $"The export of program '{program}' could not be parsed: {ex.Message}",
                "ProgramNotReadable");
        }

        if (programElement is null)
            return new CreateRoutineError(
                $"Program '{program}' was not found in the project.",
                "ProgramNotFound",
                Hint: "Check the program name with logix_project_summary.");

        // Always use the program element's own Name from here on (the SDK may match the requested name
        // case-insensitively on the first export, so this is not only needed when that export fails).
        if ((string?)programElement.Attribute("Name") is { Length: > 0 } elementName)
        {
            program = elementName;
            programXPath = ProgramXPath(program);
        }

        var existing = RoutineNames(programElement);
        if (existing.Contains(routineName, StringComparer.OrdinalIgnoreCase))
            return new CreateRoutineError(
                $"Program '{program}' already has a routine named '{routineName}' (routine names are " +
                "not case sensitive). No import was attempted.",
                "RoutineAlreadyExists",
                Hint: "Pick another name, or propose rungs into the existing routine.",
                Routines: existing);

        // ── Pending edits block every import into the program ────────────────
        var blocked = await CheckPendingEditsAsync(program, programElement, session, ct).ConfigureAwait(false);
        if (blocked is not null)
            return blocked;

        // ── Envelope ──────────────────────────────────────────────────────────
        string envelope;
        var template = FindRllRoutineName(programElement);
        if (template is not null)
        {
            var routineExport = await ExportRungsHandler.ExecuteAsync(
                RoutineXPath(program, template), session, ct).ConfigureAwait(false);
            if (routineExport is ExportRungsError templateError)
                return templateError.Kind == ErrorKinds.SdkServerFaulted
                    ? Faulted(templateError)
                    : new CreateRoutineError(
                        $"Could not export routine '{template}' to build the import envelope: {templateError.Error}",
                        "EnvelopeUnavailable");
            try
            {
                envelope = BuildEnvelopeFromRoutineExport(
                    ((ExportRungsResult)routineExport).L5X, routineName, description);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
            {
                return new CreateRoutineError(
                    $"The export of routine '{template}' could not be turned into an import envelope: {ex.Message}",
                    "EnvelopeUnavailable");
            }
        }
        else
        {
            try
            {
                envelope = BuildEnvelopeFromProgramExport(
                    ((ExportRungsResult)programExport).L5X, program, routineName, description);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
            {
                return new CreateRoutineError(
                    $"The export of program '{program}' could not be turned into an import envelope: {ex.Message}",
                    "EnvelopeUnavailable");
            }
        }

        if (Encoding.UTF8.GetByteCount(envelope) > MaxEnvelopeBytes)
            return new CreateRoutineError(
                $"The import envelope is larger than {MaxEnvelopeBytes / 1024} kB. No import was attempted.",
                "EnvelopeTooLarge");

        // ── Import (LeaveEdits only) ──────────────────────────────────────────
        var importOption = RequireLeaveEdits(PartialImportOption.LeaveEdits);
        var routinesXPath = programXPath + "/Routines";
        var tempFile = Path.GetTempFileName();
        var capture = new ImportEventCapture();
        try
        {
            await File.WriteAllTextAsync(tempFile, envelope, ct).ConfigureAwait(false);
            try
            {
                await session.ExecuteAsync(
                    async project =>
                    {
                        project.AddEventHandler(capture);
                        try
                        {
                            await project.PartialImportWithTargetFromXmlFileAsync(
                                routinesXPath, routineName, tempFile, importOption, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            project.RemoveEventHandler(capture);
                        }
                    },
                    ct).ConfigureAwait(false);
            }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.SdkServerFaulted)
            {
                return Faulted(ex.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = ex is TagServiceException tse ? tse.Error.Message : ex.Message;
                return new CreateRoutineError(
                    $"The SDK import of routine '{routineName}' into '{program}' failed: {message}",
                    // PRD N9: the SDK's own mnemonics get their kinds on this path too.
                    RockwellErrorHints.ImportFailureKind(message, capture.Errors.Concat(capture.StatusMessages))
                        ?? "ImportFailed",
                    Hint: "Check the importLog, then logix_export_rungs on the program to see what state it is in.",
                    ImportAttempted: true,
                    ImportLog: ImportLogParser.Parse(capture.Errors, capture.StatusMessages));
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                try { File.Delete(tempFile); } catch { /* best effort */ }
        }

        // ── Verify with a fresh program export ────────────────────────────────
        var created = session.IsOnline;
        var reread = await ExportRungsHandler.ExecuteAsync(programXPath, session, ct).ConfigureAwait(false);
        var confirmed = false;
        string? confirmDetail = null;
        if (reread is ExportRungsResult rereadOk)
        {
            try
            {
                var again = FindProgram(SafeXml.ParseDocument(rereadOk.L5X), program);
                confirmed = again is not null && RoutineNames(again, rllOnly: true)
                    .Contains(routineName, StringComparer.Ordinal);
                if (!confirmed)
                    confirmDetail = "the program's routines afterwards are: " +
                        (again is null ? "(program missing)" : string.Join(", ", RoutineNames(again)));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
            {
                confirmDetail = $"the re-export could not be parsed: {ex.Message}";
            }
        }
        else
        {
            confirmDetail = $"the re-export failed: {((ExportRungsError)reread).Error}";
        }

        AuditLogger.LogRoutineCreated(program, routineName, confirmed ? "created" : "not_confirmed",
            createdInController: created, confirmedByUser: confirmedByUser);

        if (!confirmed)
            return new CreateRoutineError(
                $"CreateNotConfirmed: the import of '{routineName}' returned without error, but a fresh " +
                $"export of program '{program}' does not show an RLL routine of that name ({confirmDetail}).",
                "CreateNotConfirmed",
                Hint:
                    "Do not assume the routine exists. Export the program with logix_export_rungs and check " +
                    "before retrying; if it did get created it can only be removed in Studio.",
                ImportAttempted: true);

        return new CreateRoutineResult(
            "created", RoutineXPath(program, routineName), created, UndoNote, program, routineName, RllType);
    }

    // ── Pending-edit check ────────────────────────────────────────────────────

    /// <summary>
    /// Refuses with <c>PendingEditsBlockImport</c> when any routine of the program holds edits, read
    /// the way <c>logix_pending_edits</c> reads them. The SDK refuses every import into a program
    /// that has a pending edit zone, and an import must never take such a zone with it.
    /// A program with no rung has nothing to read and passes.
    /// </summary>
    private static async Task<CreateRoutineError?> CheckPendingEditsAsync(
        string program, XElement programElement, LogixProjectSession session, CancellationToken ct)
    {
        // No rung anywhere (no RLL routine, or only empty/self-closed ones): nothing can be pending,
        // and logix_pending_edits has nothing to classify (it reports an error for such a program).
        var hasRungs = programElement.Descendants("Routine")
            .Where(r => string.Equals((string?)r.Attribute("Type"), RllType, StringComparison.OrdinalIgnoreCase))
            .Any(r => r.Descendants("Rung").Any());
        if (!hasRungs)
            return null;

        var read = await PendingEditsHandler.ExecuteAsync(program, null, session, ct).ConfigureAwait(false);
        if (read is PendingEditsError { Kind: ErrorKinds.SdkServerFaulted } faultedRead)
            return new CreateRoutineError(faultedRead.Error, ErrorKinds.SdkServerFaulted, faultedRead.Hint);
        if (read is PendingEditsError readError)
            return new CreateRoutineError(
                $"Could not read the pending edits of program '{program}' ({readError.Error}), so it cannot " +
                "be shown that an import is safe. No import was attempted.",
                "PendingEditsUnreadable",
                Hint: "Call logix_pending_edits to see the failure, fix it, then retry.");

        var routines = ((PendingEditsResult)read).Routines;
        var held = routines.Where(r => r.State != "none").ToList();
        if (held.Count == 0)
            return null;

        return new CreateRoutineError(
            $"PendingEditsBlockImport: program '{program}' has edits that are not yet assembled " +
            $"({string.Join(", ", held.Select(r => $"{r.Routine}: {r.State}"))}). The SDK refuses imports " +
            "into a program that has them, and an import must not finalize them. No import was attempted.",
            "PendingEditsBlockImport",
            Hint:
                "Resolve them first: logix_assemble_pending_edits or logix_cancel_pending_edits " +
                "(needs LGX_ENABLE_STUDIO_AUTOMATION=1), or accept or cancel them in Logix Designer " +
                "(Logic -> Online Edits). Then create the routine before staging rungs.",
            Routines: held.Select(r => r.Routine).ToList());
    }

    // ── Export reading ────────────────────────────────────────────────────────

    private static XElement? FindProgram(XDocument doc, string program) =>
        doc.Descendants("Program").FirstOrDefault(p =>
            string.Equals((string?)p.Attribute("Name"), program, StringComparison.OrdinalIgnoreCase));

    private static List<string> RoutineNames(XElement programElement, bool rllOnly = false)
    {
        var names = programElement.Descendants("Routine")
            .Where(r => !rllOnly || string.Equals((string?)r.Attribute("Type"), RllType, StringComparison.OrdinalIgnoreCase))
            .Select(r => (string?)r.Attribute("Name"));
        // A locked (encoded) routine is an EncodedData element; its name still collides.
        if (!rllOnly)
            names = names.Concat(programElement.Descendants("EncodedData")
                .Where(e => (string?)e.Attribute("EncodedType") == "Routine")
                .Select(e => (string?)e.Attribute("Name")));
        return names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
    }

    /// <summary>
    /// Name of the RLL routine to take the envelope from: the one with the fewest rungs, so the
    /// export stays small. <c>null</c> when the program has no RLL routine.
    /// </summary>
    private static string? FindRllRoutineName(XElement programElement) =>
        programElement.Descendants("Routine")
            .Where(r => string.Equals((string?)r.Attribute("Type"), RllType, StringComparison.OrdinalIgnoreCase))
            .Where(r => !string.IsNullOrEmpty((string?)r.Attribute("Name")))
            .OrderBy(r => r.Descendants("Rung").Count())
            .Select(r => (string?)r.Attribute("Name"))
            .FirstOrDefault();

    // ── Envelope builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the import envelope from an export of one RLL routine: renames the routine, opens a
    /// self-closed export into a full element, and leaves it with an empty <c>&lt;RLLContent/&gt;</c>.
    /// Tag, data-type and other context bodies are emptied; the routine has no rungs to resolve.
    /// </summary>
    /// <param name="routineExportL5x">L5X text of a routine export.</param>
    /// <param name="name">The new routine's name.</param>
    /// <param name="description">Optional routine description.</param>
    /// <returns>The envelope text.</returns>
    /// <exception cref="InvalidOperationException">The export holds no RLL routine.</exception>
    internal static string BuildEnvelopeFromRoutineExport(string routineExportL5x, string name, string? description)
    {
        var doc = SafeXml.ParseDocument(routineExportL5x);
        var routine = doc.Descendants("Routine")
                .FirstOrDefault(r => string.Equals((string?)r.Attribute("Use"), "Target", StringComparison.OrdinalIgnoreCase))
            ?? doc.Descendants("Routine").FirstOrDefault()
            ?? throw new InvalidOperationException("the export contains no Routine element.");

        // A self-closed routine has no RLLContent yet: open it, then empty it.
        var rll = ProposeRungEditHandler.EnsureRllContent(routine)
            ?? throw new InvalidOperationException("the export's routine is not an RLL routine.");
        rll.RemoveNodes();

        foreach (var a in routine.Attributes().Where(a => a.Name.LocalName is not ("Use" or "Name" or "Type")).ToList())
            a.Remove();
        routine.SetAttributeValue("Use", "Target");
        routine.SetAttributeValue("Name", name);
        routine.SetAttributeValue("Type", RllType);
        routine.Elements().Where(e => e != rll).Remove();
        if (!string.IsNullOrWhiteSpace(description))
            rll.AddBeforeSelf(new XElement("Description", new XCData(description)));

        StripContextBodies(doc, routine);
        if (doc.Root is { } root)
        {
            root.SetAttributeValue("TargetName", name);
            root.SetAttributeValue("TargetType", "Routine");
            root.SetAttributeValue("TargetSubType", RllType);
        }
        return ProposeRungEditHandler.SerializeWithDeclaration(doc);
    }

    /// <summary>
    /// Builds the import envelope from a program export, for a program that has no RLL routine to
    /// copy: the program becomes a context-only wrapper around one new empty RLL routine.
    /// </summary>
    /// <param name="programExportL5x">L5X text of a program export.</param>
    /// <param name="program">The program's name.</param>
    /// <param name="name">The new routine's name.</param>
    /// <param name="description">Optional routine description.</param>
    /// <returns>The envelope text.</returns>
    /// <exception cref="InvalidOperationException">The export holds no such program.</exception>
    internal static string BuildEnvelopeFromProgramExport(
        string programExportL5x, string program, string name, string? description)
    {
        var doc = SafeXml.ParseDocument(programExportL5x);
        var programElement = FindProgram(doc, program)
            ?? throw new InvalidOperationException($"the export contains no program '{program}'.");

        var routine = new XElement("Routine",
            new XAttribute("Use", "Target"),
            new XAttribute("Name", name),
            new XAttribute("Type", RllType));
        if (!string.IsNullOrWhiteSpace(description))
            routine.Add(new XElement("Description", new XCData(description)));
        routine.Add(new XElement("RLLContent"));

        foreach (var a in programElement.Attributes().Where(a => a.Name.LocalName is not "Name").ToList())
            a.Remove();
        programElement.SetAttributeValue("Use", "Context");
        programElement.RemoveNodes();
        programElement.Add(new XElement("Routines", new XAttribute("Use", "Context"), routine));

        StripContextBodies(doc, routine);
        if (doc.Root is { } root)
        {
            root.SetAttributeValue("TargetName", name);
            root.SetAttributeValue("TargetType", "Routine");
            root.SetAttributeValue("TargetSubType", RllType);
            root.SetAttributeValue("ContainsContext", "true");
        }
        return ProposeRungEditHandler.SerializeWithDeclaration(doc);
    }

    /// <summary>
    /// Reduces everything around the target routine to name-only context: the controller keeps its
    /// <c>Programs</c> and every other child is emptied; the program keeps only its routines.
    /// </summary>
    private static void StripContextBodies(XDocument doc, XElement routine)
    {
        var controller = doc.Descendants("Controller").FirstOrDefault();
        if (controller is null)
            return;

        controller.SetAttributeValue("Use", "Context");
        foreach (var attr in controller.Attributes()
                     .Where(a => a.Name.LocalName is not ("Use" or "Name")).ToList())
            attr.Remove();

        foreach (var child in controller.Elements().Where(e => e.Name.LocalName != "Programs").ToList())
        {
            child.RemoveNodes();
            child.SetAttributeValue("Use", "Context");
        }

        foreach (var programElement in controller.Descendants("Program").ToList())
        {
            var routinesElement = routine.Parent;
            foreach (var child in programElement.Elements().Where(e => e != routinesElement).ToList())
                child.Remove();
        }
        if (routine.Parent is { } routines)
        {
            routines.SetAttributeValue("Use", "Context");
            routines.Elements().Where(e => e != routine).Remove();
        }
    }

    private static CreateRoutineError Faulted(TagServiceError e) =>
        new(e.Message, ErrorKinds.SdkServerFaulted, e.Hint);

    private static CreateRoutineError Faulted(ExportRungsError e) =>
        new(e.Error, ErrorKinds.SdkServerFaulted, e.Hint);

    // ── Import event capture ──────────────────────────────────────────────────

    /// <summary>
    /// Collects the importer's status and error callbacks during the one import call, so a failure can
    /// quote the SDK's own log.
    /// </summary>
    private sealed class ImportEventCapture : IOperationEvent
    {
        private readonly List<string> _errors = new();
        private readonly List<string> _status = new();

        public IReadOnlyList<string> StatusMessages => _status;
        public IReadOnlyList<string> Errors => _errors;

        public void LogProgress(bool logProgressNew) { }
        public void Progress(string projectFile, int value) { }
        public void LogStatus(bool logStatusNew) { }
        public void Status(string projectFile, string msg) => _status.Add(msg);
        public void LogError(bool logErrorNew) { }
        public void Error(string projectFile, string msg) => _errors.Add(msg);
    }
}
#endif
