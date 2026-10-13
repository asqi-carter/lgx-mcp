// ── RockwellErrorHints — one table for every Rockwell SDK error code the server recognises ──
//
// Each entry maps an SDK error code or message pattern to the error kind the server reports, a
// plain-language cause and the next step for the engineer. The import, connect, comm and tag
// paths all classify SDK failures through this table, so a code is described in one place only.
//
// Core assembly: no Rockwell types, so the table is testable without the SDK. The adapter
// (SdkCommErrorClassifier, SdkConnectFactory, ProposeRungEditHandler, TypedSdkTagService) calls it.
using System.Text.RegularExpressions;

/// <summary>One row of <see cref="RockwellErrorHints.Table"/>.</summary>
/// <param name="Id">Stable name of the entry (a code, or a short name for a message pattern).</param>
/// <param name="Pattern">Regular expression matched against the SDK error text.</param>
/// <param name="Kind">The error <c>kind</c> the server reports for it; <c>null</c> when the entry only describes the code.</param>
/// <param name="Cause">What the code means, in plain language.</param>
/// <param name="NextStep">What the engineer should do next.</param>
/// <param name="CaseSensitive">Whether <paramref name="Pattern"/> is matched case-sensitively.</param>
/// <param name="WholeChain">Whether the code is looked for in the whole inner-exception chain (otherwise the outer message only).</param>
public sealed record RockwellErrorHint(
    string Id,
    string Pattern,
    string? Kind,
    string Cause,
    string NextStep,
    bool CaseSensitive = false,
    bool WholeChain = false)
{
    private Regex? _regex;

    /// <summary>The compiled <see cref="Pattern"/>.</summary>
    public Regex Regex => _regex ??= new Regex(
        Pattern,
        RegexOptions.CultureInvariant
            | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));

    /// <summary>True when <paramref name="text"/> matches this entry.</summary>
    /// <param name="text">The SDK error text (may be <c>null</c>).</param>
    public bool IsMatch(string? text) => !string.IsNullOrEmpty(text) && Regex.IsMatch(text);

    /// <summary>The cause and the next step as one sentence group for an error message.</summary>
    public string Explanation => $"{Cause} {NextStep}";
}

/// <summary>Stable ids of the <see cref="RockwellErrorHints.Table"/> entries.</summary>
public static class RockwellErrorHintIds
{
    /// <summary>The controller's project image differs from the open project.</summary>
    public const string ImagesIncompatible = "RxCL_E_IMAGES_INCOMPATIBLE";
    /// <summary>The SDK cannot upload because physical addresses do not correlate.</summary>
    public const string CannotUploadPhysAddr = "RxCL_E_CANNOT_UPLOAD_PHYS_ADDR";
    /// <summary>Any comm-layer fault, <c>RxE_COMM_*</c>.</summary>
    public const string CommLayer = "RxE_COMM_*";
    /// <summary>Logix Services died or is faulted: <c>LgxSrv_E_FATAL_ERROR</c> or <c>LgxSrv_E_SERVER_FAULTED</c>.</summary>
    public const string ServerFaulted = "LgxSrv_E_SERVER_FAULTED";
    /// <summary>The SDK's "Project is not open." message.</summary>
    public const string ProjectNotOpen = "ProjectNotOpen";
    /// <summary>The SDK service cannot be reached.</summary>
    public const string ServiceUnreachable = "LdSdkServiceUnreachable";
    /// <summary>The importer rejected the fragment and changed nothing.</summary>
    public const string ImportAbortedNoChanges = "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES";
    /// <summary>An accepted edit zone blocks further imports into the program.</summary>
    public const string PieEditsExistForProgram = "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM";
    /// <summary>Any importer mnemonic, <c>XMLSrv_E_*</c>.</summary>
    public const string XmlSrvAny = "XMLSrv_E_*";
    /// <summary>The fragment root does not match the XPath target.</summary>
    public const string IncompatibleImportTarget = "XMLSrv_E_INCOMPATIBLE_IMPORT_TARGET";
    /// <summary>The project was saved by a newer Logix Designer.</summary>
    public const string DataTooNew = "RxE_DATA_TOO_NEW";
    /// <summary>The Logix Designer version the project needs is not installed.</summary>
    public const string RequiredVersionNotInstalled = "RequiredDesignerVersionNotInstalled";
    /// <summary>Converting the project to this controller type is not supported.</summary>
    public const string MorphToThisControllerNotSupported = "RxCsE_MORPH_TO_THIS_CONTROLLER_NOT_SUPPORTED";
    /// <summary>Another client or Studio holds a higher exclusive lock on the controller.</summary>
    public const string ControllerLocked = "RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK";
    /// <summary><c>RxE_ILLEGAL_OPERATIONS</c> (<c>80042021</c>, "Sequence of operations not allowed"): the SDK refused an operation because of the state another client left the project in.</summary>
    public const string IllegalOperations = "RxE_ILLEGAL_OPERATIONS";
    /// <summary>The SDK reports an operation unsupported on this Logix Designer version.</summary>
    public const string UnsupportedOnDesignerVersion = "OperationUnsupportedOnDesignerVersion";
}

/// <summary>
/// The single table of Rockwell SDK error codes and message patterns, with lookups over it.
/// </summary>
public static class RockwellErrorHints
{
    /// <summary>
    /// The next step for a faulted SDK server: re-establish the session. The failed operation never
    /// changed the controller.
    /// </summary>
    public const string ServerFaultedHint =
        "Call logix_connect to re-establish the session (logix_close_project also works and disposes the faulted SDK process). " +
        "Whether the controller was changed depends on the operation that faulted. Edits staged in the dead session are lost; " +
        "after reconnecting, read logix_pending_edits to see what the controller holds.";

    /// <summary>Operation name recorded for a fault during <c>logix_clear_staged</c>.</summary>
    public const string OperationClear = "clear";
    /// <summary>Operation name recorded for a fault during <c>logix_build</c>.</summary>
    public const string OperationBuild = "build";
    /// <summary>Operation name recorded for a fault while publishing staged rungs to the controller.</summary>
    public const string OperationPublish = "publish";
    /// <summary>Operation name recorded for a fault while staging rungs in the workstation image.</summary>
    public const string OperationStage = "stage";
    /// <summary>Operation name recorded for a fault during a rung export.</summary>
    public const string OperationExport = "export";
    /// <summary>Operation name recorded for a fault while reading pending-edit state.</summary>
    public const string OperationPendingRead = "pending-edit read";

    /// <summary>
    /// The faulted-server hint for the operation that was running when Logix Services faulted: it names that
    /// operation and says what that operation could and could not have done to the controller.
    /// </summary>
    /// <param name="operation">
    /// One of the <c>Operation*</c> names, or <c>null</c>/unknown when the faulting operation was not recorded.
    /// </param>
    /// <returns>The hint text; the generic <see cref="ServerFaultedHint"/> when <paramref name="operation"/> is unknown.</returns>
    public static string ServerFaultedHintFor(string? operation)
    {
        const string Reconnect =
            "Call logix_connect to re-establish the session (logix_close_project also works and disposes the faulted SDK process). ";
        const string Reread =
            " Edits staged in the dead session are lost; after reconnecting, read logix_pending_edits to see what the controller holds.";
        return operation switch
        {
            OperationClear => Reconnect +
                "The fault happened during logix_clear_staged, while it rebuilt the session image. A clear never sends staged rungs " +
                "to the controller; the only controller write it can make is putting back an original rung comment." + Reread +
                " Then call logix_clear_staged again only if staged work remains.",
            OperationBuild => Reconnect +
                "The failed build did not change the controller." + Reread,
            OperationExport => Reconnect +
                "The fault happened during a rung export, which only reads; it did not change the controller." + Reread,
            OperationPublish => Reconnect +
                "The fault happened during a publish: the import may have reached the controller before the server died, " +
                "so the controller may hold some or all of the published rungs." + Reread,
            OperationStage => Reconnect +
                "The fault happened while staging rungs in the workstation image; nothing was sent to the controller." + Reread,
            OperationPendingRead => Reconnect +
                "The fault happened while reading pending-edit state, which only reads; it did not change the controller." + Reread,
            _ => ServerFaultedHint,
        };
    }

    /// <summary>The retryable hint reported with <c>ControllerLocked</c>.</summary>
    public const string ControllerLockedHint =
        "Another client is importing; retry in a few seconds.";

    /// <summary>Seconds a client should wait before it publishes again after a <c>ControllerBusy</c> publish race.</summary>
    public const int PublishRaceRetryAfterSeconds = 10;

    /// <summary>The retryable hint reported with <c>ControllerBusy</c> after an <c>RxE_ILLEGAL_OPERATIONS</c> publish race.</summary>
    public const string IllegalOperationsBusyHint =
        "Another client was editing this program at the same moment, so the SDK refused the import as out of sequence " +
        "(RxE_ILLEGAL_OPERATIONS, 80042021). Nothing changed on the controller. Retry after retryAfterSeconds; if it " +
        "fails again, check logix_pending_edits and whether another client is editing the program.";

    /// <summary>True when any exception in the chain of <paramref name="ex"/> names <c>RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK</c>.</summary>
    /// <param name="ex">The exception to inspect.</param>
    public static bool IsControllerLocked(Exception ex) =>
        IsMatch(ex, RockwellErrorHintIds.ControllerLocked);

    /// <summary>True when any exception in the chain of <paramref name="ex"/> names <c>LgxSrv_E_FATAL_ERROR</c> or <c>LgxSrv_E_SERVER_FAULTED</c>.</summary>
    /// <param name="ex">The exception to inspect.</param>
    public static bool IsServerFaulted(Exception ex) =>
        IsMatch(ex, RockwellErrorHintIds.ServerFaulted);

    /// <summary>
    /// Error <c>kind</c> reported for <c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c> on every import path:
    /// the program holds edits that are not yet assembled, so the SDK refuses the import.
    /// </summary>
    public const string PendingEditsBlockImportKind = ErrorKinds.PendingEditsBlockImport;

    /// <summary>
    /// Maps a failed import's raw SDK text to an error kind. <c>XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM</c>
    /// (in the exception text or any captured log entry) is <see cref="PendingEditsBlockImportKind"/>.
    /// <c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c> with no other cause is <c>ImportRejectedByImporter</c>.
    /// Anything else returns <c>null</c>.
    /// </summary>
    /// <param name="sdkDetail">The exception text of the failed import.</param>
    /// <param name="capturedEntries">Captured ImportLog / Status text of the same attempt (may be <c>null</c>).</param>
    /// <returns>The kind, or <c>null</c> when the text names neither mnemonic.</returns>
    public static string? ImportFailureKind(string? sdkDetail, IEnumerable<string>? capturedEntries = null)
    {
        var all = new List<string?> { sdkDetail };
        if (capturedEntries is not null) all.AddRange(capturedEntries);

        if (all.Any(t => IsMatch(t, RockwellErrorHintIds.ControllerLocked)))
            return ErrorKinds.ControllerLocked;
        if (all.Any(t => IsMatch(t, RockwellErrorHintIds.PieEditsExistForProgram)))
            return PendingEditsBlockImportKind;
        if (all.Any(t => IsMatch(t, RockwellErrorHintIds.ImportAbortedNoChanges)))
            return nameof(TagServiceErrorKind.ImportRejectedByImporter);
        return null;
    }

    /// <summary>
    /// The hint on <c>ImportRejectedByImporter</c> when no reject reason is recognised: the importer found an
    /// undefined reference, the usual cause of an <c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c> with no other detail.
    /// </summary>
    public const string ImportRejectedUndefinedReferenceHint =
        "the importer rejected a reference in the fragment (undefined tag or routine); nothing changed on the " +
        "controller; check every tag against logix_find_tags.";

    /// <summary>
    /// Builds the <c>ImportRejectedByImporter</c> hint from the reject reasons the importer gave (G30): the first
    /// reason with a recognised hint, else the first with a mnemonic or readable text, quoted with the rung when
    /// known. Only when no reason says anything does it fall back to <see cref="ImportRejectedUndefinedReferenceHint"/>.
    /// The importer's own generic "aborted, no changes" line is a symptom, not a cause, and is skipped.
    /// </summary>
    /// <param name="reasons">Each reason's mnemonic, text, rung and known hint, in order; may be <c>null</c>.</param>
    /// <returns>The hint; never empty.</returns>
    public static string ImportRejectedHint(
        IEnumerable<(string? Mnemonic, string? Text, int? RungIndex, string? Hint)>? reasons)
    {
        var causes = (reasons ?? [])
            .Where(r => !IsMatch(r.Mnemonic, RockwellErrorHintIds.ImportAbortedNoChanges)
                        && (!string.IsNullOrWhiteSpace(r.Mnemonic) || ImportLogParser.IsMeaningful(r.Text)))
            .ToList();
        if (causes.Count == 0)
            return ImportRejectedUndefinedReferenceHint;

        var chosen = causes.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Hint) && ImportLogParser.IsMeaningful(r.Text));
        if (chosen == default) chosen = causes.FirstOrDefault(r => ImportLogParser.IsMeaningful(r.Text));
        if (chosen == default) chosen = causes[0];

        var sb = new System.Text.StringBuilder("the importer rejected the fragment");
        if (chosen.RungIndex is { } rung) sb.Append($" at rung {rung}");
        sb.Append(": ");
        if (!string.IsNullOrWhiteSpace(chosen.Mnemonic)) sb.Append(chosen.Mnemonic!.Trim());
        if (ImportLogParser.IsMeaningful(chosen.Text))
            sb.Append(sb[^1] == ' ' ? "" : " - ").Append(chosen.Text!.Trim().TrimEnd('.')).Append('.');
        else
            sb.Append('.');
        if (!string.IsNullOrWhiteSpace(chosen.Hint)) sb.Append(' ').Append(chosen.Hint!.Trim());
        sb.Append(" Nothing changed on the controller.");
        return sb.ToString();
    }

    /// <summary>
    /// Every entry. Order matters only for <see cref="Find(string?)"/>, which returns the first
    /// match: specific codes come before the <c>XMLSrv_E_*</c> catch-all, which is last.
    /// </summary>
    public static readonly IReadOnlyList<RockwellErrorHint> Table =
    [
        // ── Existing mappings (kinds unchanged) ────────────────────────────
        new(RockwellErrorHintIds.ImagesIncompatible,
            "IMAGES_INCOMPATIBLE",
            nameof(TagServiceErrorKind.ImagesIncompatible),
            "The controller holds a different project than the one open in the SDK.",
            "Download the open project to the controller and reconnect, or open the project that is stored in the controller."),
        new(RockwellErrorHintIds.CannotUploadPhysAddr,
            "CANNOT_UPLOAD_PHYS_ADDR",
            nameof(TagServiceErrorKind.ImagesIncompatible),
            "The SDK cannot match the controller's physical address information to the open project.",
            "Download the open project to the controller and reconnect, or open the project that is stored in the controller."),
        new(RockwellErrorHintIds.ServerFaulted,
            "LgxSrv_E_FATAL_ERROR|LgxSrv_E_SERVER_FAULTED",
            nameof(TagServiceErrorKind.SdkServerFaulted),
            "The Logix Designer SDK's server process (Logix Services) hit a fatal error and is faulted, so this session can no longer call the SDK.",
            ServerFaultedHint,
            CaseSensitive: true,
            WholeChain: true),
        new(RockwellErrorHintIds.CommLayer,
            "RxE_COMM_",
            nameof(TagServiceErrorKind.SdkCommLayerFaulted),
            "The Logix Designer SDK's communications layer reported a fault; the SDK service is running but could not talk to the controller.",
            "Check the controller is reachable on EtherNet/IP port 44818 and the comm path is right, then reconnect.",
            WholeChain: true),
        new(RockwellErrorHintIds.ProjectNotOpen,
            @"Project is not open\.",
            nameof(TagServiceErrorKind.SdkServiceDegraded),
            "The SDK reports the project is not open, which means its service crashed or was stopped and the session handle is no longer valid.",
            "Reconnect to start a new SDK session."),
        new(RockwellErrorHintIds.ServiceUnreachable,
            "Could not reach LdSdkService|Unable to complete connection to LdSdkService",
            nameof(TagServiceErrorKind.SdkServiceDegraded),
            "The Logix Designer SDK service (LdSdkServer.exe) cannot be reached; it has probably crashed or been stopped.",
            "Check the service is running on this machine, restart it if not, then reconnect."),
        new(RockwellErrorHintIds.ImportAbortedNoChanges,
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES",
            nameof(TagServiceErrorKind.ImportRejectedByImporter),
            "The importer found an undefined tag or routine in the fragment and cancelled the import; nothing changed on the controller.",
            "Check every tag and routine the fragment references, for example with logix_find_tags, and send it again."),
        new(RockwellErrorHintIds.PieEditsExistForProgram,
            "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM",
            ErrorKinds.PendingEditsBlockImport,
            "The program already holds accepted logic edits, and the SDK allows one import per accept or cancel cycle.",
            "Accept or cancel the pending edits in Logix Designer, then import again."),

        // ── Entries added with the table ───────────────────────────────────
        new(RockwellErrorHintIds.IncompatibleImportTarget,
            "XMLSrv_E_INCOMPATIBLE_IMPORT_TARGET",
            nameof(TagServiceErrorKind.ImportRejectedByImporter),
            "The root element of the fragment does not match the element the XPath target points to; for example a Rung fragment sent to a Routine target.",
            "Make the fragment's root element the type the target expects, or change the target XPath to match the fragment."),
        new(RockwellErrorHintIds.DataTooNew,
            "RxE_DATA_TOO_NEW",
            ErrorKinds.SdkCapabilityUnavailable,
            "The project was saved by a newer version of Logix Designer than the one the SDK is using, so it cannot read it.",
            "Install the Logix Designer version that saved the project, or open an export of the project made for the older version."),
        new(RockwellErrorHintIds.RequiredVersionNotInstalled,
            @"Required Logix Designer version \d+(\.\d+)? is not installed",
            ErrorKinds.SdkCapabilityUnavailable,
            "The project needs a Logix Designer version that is not installed on this machine.",
            "Install the version named in the message, or open a copy of the project saved for an installed version."),
        new(RockwellErrorHintIds.MorphToThisControllerNotSupported,
            "RxCsE_MORPH_TO_THIS_CONTROLLER_NOT_SUPPORTED",
            ErrorKinds.SdkCapabilityUnavailable,
            "The SDK cannot convert the project to the target controller type.",
            "Change the controller type in Logix Designer instead, or choose a controller type the project can be converted to."),
        new(RockwellErrorHintIds.ControllerLocked,
            "RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK",
            ErrorKinds.ControllerLocked,
            "Another client, or Studio 5000, holds an exclusive lock on the controller (for example while it is importing), so the SDK cannot take the lock this operation needs.",
            ControllerLockedHint,
            CaseSensitive: true,
            WholeChain: true),
        new(RockwellErrorHintIds.IllegalOperations,
            @"RxE_ILLEGAL_OPERATIONS|\b80042021\b",
            ErrorKinds.ControllerBusy,
            "The SDK refused the operation as out of sequence, which is what a second client editing the same program at the same moment causes.",
            IllegalOperationsBusyHint),
        new(RockwellErrorHintIds.UnsupportedOnDesignerVersion,
            DesignerVersionInfo.UnsupportedVersionPattern,
            ErrorKinds.SdkCapabilityUnavailable,
            "The SDK does not support this operation on the Logix Designer version installed here.",
            "Read lgx://sdk/capabilities for what this session can do; the operation needs a newer Logix Designer, or a different route."),

        // ── Catch-all: keep last ───────────────────────────────────────────
        new(RockwellErrorHintIds.XmlSrvAny,
            "XMLSrv_E_",
            null,
            "The Logix Designer importer rejected the request.",
            "Read the mnemonic and message in the SDK detail for the specific reason.",
            CaseSensitive: true),
    ];

    /// <summary>The entry with the given id, or <c>null</c>.</summary>
    /// <param name="id">One of <see cref="RockwellErrorHintIds"/>.</param>
    public static RockwellErrorHint? Get(string id) =>
        Table.FirstOrDefault(h => h.Id == id);

    /// <summary>The first entry whose pattern matches <paramref name="text"/>, or <c>null</c>.</summary>
    /// <param name="text">The SDK error text.</param>
    public static RockwellErrorHint? Find(string? text) =>
        string.IsNullOrEmpty(text) ? null : Table.FirstOrDefault(h => h.IsMatch(text));

    /// <summary>
    /// The first entry matching <paramref name="ex"/>: whole-chain entries look through every inner
    /// exception, the others look at the outer message only.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    public static RockwellErrorHint? Find(Exception ex) =>
        Table.FirstOrDefault(h => Matches(ex, h));

    /// <summary>True when <paramref name="text"/> matches the entry named <paramref name="id"/>.</summary>
    /// <param name="text">The SDK error text.</param>
    /// <param name="id">One of <see cref="RockwellErrorHintIds"/>.</param>
    public static bool IsMatch(string? text, string id) => Get(id)?.IsMatch(text) ?? false;

    /// <summary>True when <paramref name="ex"/> matches the entry named <paramref name="id"/>.</summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <param name="id">One of <see cref="RockwellErrorHintIds"/>.</param>
    public static bool IsMatch(Exception ex, string id) =>
        Get(id) is { } h && Matches(ex, h);

    /// <summary>
    /// True when the outer message of <paramref name="ex"/> names a project/controller image mismatch
    /// (<c>IMAGES_INCOMPATIBLE</c> or <c>CANNOT_UPLOAD_PHYS_ADDR</c>), which needs a download to resolve.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    public static bool IsIncompatibility(Exception ex) =>
        IsMatch(ex, RockwellErrorHintIds.ImagesIncompatible)
        || IsMatch(ex, RockwellErrorHintIds.CannotUploadPhysAddr);

    /// <summary>True when any exception in the chain of <paramref name="ex"/> names an <c>RxE_COMM_*</c> code.</summary>
    /// <param name="ex">The exception to inspect.</param>
    public static bool IsCommLayerError(Exception ex) =>
        IsMatch(ex, RockwellErrorHintIds.CommLayer);

    /// <summary>True when <paramref name="ex"/> says the project is not open (a dead SDK service).</summary>
    /// <param name="ex">The exception to inspect.</param>
    public static bool IsProjectNotOpen(Exception ex) =>
        IsMatch(ex, RockwellErrorHintIds.ProjectNotOpen);

    /// <summary>True when <paramref name="ex"/> says the SDK service cannot be reached.</summary>
    /// <param name="ex">The exception to inspect.</param>
    public static bool IsServiceUnreachable(Exception ex) =>
        IsMatch(ex, RockwellErrorHintIds.ServiceUnreachable);

    /// <summary>True when <paramref name="text"/> names any <c>XMLSrv_E_*</c> importer mnemonic.</summary>
    /// <param name="text">The SDK error text.</param>
    public static bool IsXmlSrvFailure(string? text) =>
        IsMatch(text, RockwellErrorHintIds.XmlSrvAny);

    /// <summary>
    /// The cause and next step for <paramref name="text"/> when it matches one of the entries that
    /// add guidance beyond what the call sites already say (the entries added with the table);
    /// otherwise an empty string. A leading space is included so it can be appended to a message.
    /// </summary>
    /// <param name="text">The SDK error text.</param>
    public static string AdditionalGuidance(string? text)
    {
        foreach (var id in GuidanceIds)
            if (Get(id) is { } h && h.IsMatch(text))
                return " " + h.Explanation;
        return string.Empty;
    }

    private static readonly string[] GuidanceIds =
    [
        RockwellErrorHintIds.IncompatibleImportTarget,
        RockwellErrorHintIds.DataTooNew,
        RockwellErrorHintIds.RequiredVersionNotInstalled,
        RockwellErrorHintIds.MorphToThisControllerNotSupported,
        RockwellErrorHintIds.UnsupportedOnDesignerVersion,
    ];

    private static bool Matches(Exception ex, RockwellErrorHint hint)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (hint.IsMatch(current.Message)) return true;
            if (!hint.WholeChain) return false;
        }
        return false;
    }
}
