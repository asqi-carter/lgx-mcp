// ── RungReferenceChecker — cheap pre-check of tag operands and instruction names ──
//
// Runs on every stage and publish of logix_propose_rung_edit, before anything is staged or imported.
// It refuses only what Logix certainly rejects:
//   - UndefinedTag        a tag operand whose name is in no scope the rung can see (controller scope, the
//                         routine's program scope, a tag declared by the same call, a staged tag). A bare name
//                         that exists only in ANOTHER program is this too, with the hint to write
//                         \<Program>.<Tag> if the tag is Public;
//   - TagNotPublic        \Prog.Tag where the tag is not Public, Input or Output in the export (Usage missing, Local or InOut);
//   - UndefinedMember     Tag.Member where the tag's type is known (a user DataType from the export, or TIMER,
//                         COUNTER, CONTROL, STRING) and has no such member.
// Left to Logix, on purpose: a string literal where a tag is expected (MOVE('A',dA)) and a JSR to a routine in
// another program (JSR names a routine, not a tag, and its program-scope rules are not in the exports).
// An instruction name that is not in the known-instruction set is only a warning (see KnownInstructions):
// Logix itself is the final check at publish, and the set can lag a revision. What the target revision
// certainly rejects (ASCII instructions on v37+, wrong-set mnemonics) is refused by MnemonicSetChecker, which
// holds the catalogue; this checker has no revision and never refuses on an unfamiliar name.
// It must never refuse anything Logix accepts. So every question it cannot answer from the data it was given
// (an export that failed, a program it never saw, rung text it cannot parse, an operand that is not a plain
// tag/number) is let through, with a warning. Module I/O tags (Local:1:I.Data) are never checked, because
// module-defined tags are not in the tag export.
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

/// <summary>The <c>Kind</c> values the reference check reports.</summary>
public static class ReferenceCheckKinds
{
    /// <summary>A rung names a tag that exists in no visible scope.</summary>
    public const string UndefinedTag = "UndefinedTag";

    /// <summary><c>\Program.Tag</c> names a tag of that program that is not Public.</summary>
    public const string TagNotPublic = "TagNotPublic";

    /// <summary><c>Tag.Member</c> names a member the tag's (known) type does not have.</summary>
    public const string UndefinedMember = "UndefinedMember";

    /// <summary>
    /// Retained for callers that still reference it. <see cref="RungReferenceChecker.Check"/> no longer refuses
    /// for an unfamiliar instruction name (it warns); it never returns this kind.
    /// </summary>
    public const string UnknownInstruction = "UnknownInstruction";

    /// <summary>
    /// A write instruction (OTE, OTL, OTU) names another program's Output parameter as its destination, for example
    /// <c>OTE(\Line_A.P6_Out)</c>. Logix refuses it (<c>RxCMP_E_DIRECT_WRITE_OUTPUT</c>); only the owning program
    /// may write its Output parameters. Reads of the same tag are fine.
    /// </summary>
    public const string CrossProgramOutputNotWritable = "CrossProgramOutputNotWritable";

    /// <summary>
    /// A tag of a motion data type (AXIS_*, MOTION_GROUP, COORDINATE_SYSTEM) is declared while the session is
    /// online; Logix creates these only offline.
    /// </summary>
    public const string MotionTagRequiresOffline = "MotionTagRequiresOffline";

    /// <summary>
    /// A <c>\Program.Tag</c> operand names a program that does not exist in the controller (for example
    /// <c>XIC(\Line_Z.P6_Loc)</c>). Reported as a stage-time warning; refused when <c>verifyOnStage</c> is on.
    /// </summary>
    public const string UnknownProgram = "UnknownProgram";

    /// <summary>The Logix mnemonic for a direct write to another program's Output parameter.</summary>
    public const string DirectWriteOutputMnemonic = "RxCMP_E_DIRECT_WRITE_OUTPUT";

    /// <summary>
    /// True for a data type that Logix will not create while online: every <c>AXIS_*</c> type,
    /// <c>MOTION_GROUP</c> and <c>COORDINATE_SYSTEM</c> (<c>MOTION_INSTRUCTION</c> is an ordinary online tag).
    /// </summary>
    /// <param name="dataType">The tag's data type name; surrounding whitespace and case are ignored.</param>
    public static bool IsMotionTagType(string? dataType)
    {
        var t = dataType?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        return t.StartsWith("AXIS_", StringComparison.OrdinalIgnoreCase)
            || t.Equals("MOTION_GROUP", StringComparison.OrdinalIgnoreCase)
            || t.Equals("COORDINATE_SYSTEM", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One unresolved name.</summary>
/// <param name="RungIndex">Zero-based index of the rung in the list that was checked.</param>
/// <param name="Instruction">The instruction the name appears in.</param>
/// <param name="Name">The tag, or the instruction, as written.</param>
/// <param name="Kind">The <see cref="ReferenceCheckKinds"/> value for this finding.</param>
/// <param name="Detail">Extra context for the finding (for example, where else the bare name exists); otherwise <c>null</c>.</param>
public sealed record ReferenceFinding(
    [property: JsonPropertyName("rungIndex")]   int RungIndex,
    [property: JsonPropertyName("instruction")] string Instruction,
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("kind")]        string Kind = ReferenceCheckKinds.UndefinedTag,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

/// <summary>Outcome of <see cref="RungReferenceChecker.Check"/>.</summary>
/// <param name="Kind">One of <see cref="ReferenceCheckKinds"/>; <c>null</c> when nothing is refused.</param>
/// <param name="Findings">The unresolved names behind <paramref name="Kind"/>.</param>
/// <param name="Message">Agent-facing refusal message; <c>null</c> when nothing is refused.</param>
/// <param name="Warnings">References that could not be resolved and were let through.</param>
/// <param name="Hint">What to do about the refusal, when the kind has a specific remedy; otherwise <c>null</c>.</param>
/// <param name="UnknownPrograms">
/// References to programs that do not exist (<see cref="ReferenceCheckKinds.UnknownProgram"/>). They are let through
/// with a warning by <see cref="RungReferenceChecker.Check"/>; the caller decides whether to refuse (verifyOnStage).
/// </param>
public sealed record ReferenceCheckResult(
    string? Kind,
    IReadOnlyList<ReferenceFinding> Findings,
    string? Message,
    IReadOnlyList<string> Warnings,
    string? Hint = null,
    IReadOnlyList<ReferenceFinding>? UnknownPrograms = null)
{
    /// <summary><c>true</c> when the rungs must be refused.</summary>
    public bool Refused => Kind is not null;

    /// <summary>Nothing refused, nothing to warn about.</summary>
    public static ReferenceCheckResult Pass { get; } = new(null, [], null, []);
}

/// <summary>
/// What the checker knows about the project. A <c>null</c> set means "not known": nothing is refused on the
/// strength of it.
/// </summary>
/// <param name="TargetProgram">The program that holds the routine being edited.</param>
/// <param name="ControllerTags">Controller-scope tag names, or <c>null</c> when they could not be read.</param>
/// <param name="TargetProgramTags">
/// Tag names in the target program's scope, or <c>null</c> when the program's tags could not be read.
/// </param>
/// <param name="ProgramTags">
/// Tag names per program (key: program name, case ignored), for <c>\Program.Tag</c> references; a program
/// that is absent is unknown, not undefined. <c>null</c> when none could be read.
/// </param>
/// <param name="DeclaredTags">Tags the same call declares, and tags already staged in the session.</param>
/// <param name="AoiNames">Add-On Instruction names, or <c>null</c> when they could not be read.</param>
/// <param name="LocalProgramTags">
/// Per program (case ignored), the tags another program cannot reference: Usage missing, Local or InOut (only
/// Public, Input and Output are reachable). A program absent here, or <c>null</c> when the usage could not be read,
/// is unknown and never refused as <c>TagNotPublic</c>.
/// </param>
/// <param name="ControllerTagTypes">Controller-scope tag name to data type name; <c>null</c> when unknown.</param>
/// <param name="ProgramTagTypes">Per program, tag name to data type name; <c>null</c> when unknown.</param>
/// <param name="TypeMembers">
/// User data types: type name to (member name to member type). The predefined TIMER, COUNTER, CONTROL and STRING
/// are always known. A tag whose type is in neither is never checked for members.
/// </param>
/// <param name="OutputParameters">
/// Per program (case ignored), the tags whose Usage is Output. A write instruction aimed at one of these in a
/// program other than <paramref name="TargetProgram"/> is refused. <c>null</c>, or a program absent from it, is
/// unknown and never refused.
/// </param>
/// <param name="ProgramNames">
/// Every program name in the controller (case ignored), or <c>null</c> when the list could not be read. Only when it is
/// known is a <c>\Program.Tag</c> operand naming a program outside it reported as <c>UnknownProgram</c>.
/// </param>
public sealed record ReferenceSymbols(
    string? TargetProgram,
    IReadOnlySet<string>? ControllerTags,
    IReadOnlySet<string>? TargetProgramTags,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? ProgramTags,
    IReadOnlySet<string> DeclaredTags,
    IReadOnlySet<string>? AoiNames,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? LocalProgramTags = null,
    IReadOnlyDictionary<string, string>? ControllerTagTypes = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ProgramTagTypes = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? TypeMembers = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? OutputParameters = null,
    IReadOnlySet<string>? ProgramNames = null);

/// <summary>Checks the tag operands and instruction names of rung text against the project.</summary>
public static class RungReferenceChecker
{
    // ── Known instructions ───────────────────────────────────────────────────

    /// <summary>
    /// The known-instruction set: every Logix instruction name the checker recognises, both mnemonic sets
    /// (wrong-set use is the mnemonic check's business). This is the ONE source for "is this a Logix
    /// instruction"; nothing else in the code base keeps its own list.
    /// <para>
    /// Intended source: the Logix Designer v38 local help index (<c>help/clinset/</c>), the newest revision we
    /// target, which lists every instruction of every earlier revision that still exists. Instructions that
    /// older revisions lack, or that later revisions dropped, are NOT removed from this set: whether an
    /// instruction is unsupported on the target revision is answered per revision by the catalogue in
    /// <see cref="MnemonicSetChecker"/> (for example the ASCII instructions from v37). Keeping "known" and
    /// "supported on this revision" apart is what lets <c>AWT</c> on v37+ still be refused there while an
    /// unfamiliar name here is merely warned about.
    /// </para>
    /// <para>
    /// The set is deliberately generous and may lag a Logix release. A name missing from it is a warning
    /// ("not in the known-instruction list"), never a refusal; Logix stays the final check at import/publish.
    /// To add a revision's new instructions, add them here once.
    /// </para>
    /// <para>
    /// NOTE: the <c>help/clinset/</c> index was not present in the sandbox where this list was last extended,
    /// so completeness against it is unverified; the v38 names from task logix-mcp-r7k0.3 (ISINF, ISNAN, ATAN2,
    /// HMIBC, LV, ASO and the safety/drives names) were added from that task's list.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Bit
        "XIC", "XIO", "OTE", "OTL", "OTU", "ONS", "OSR", "OSF", "OSRI", "OSFI",
        // Timer and counter
        "TON", "TOF", "RTO", "CTU", "CTD", "RES", "TONR",
        // Compare
        "CMP", "EQU", "NEQ", "LES", "LEQ", "GRT", "GEQ", "LIM", "MEQ", "EQ", "NE", "LT", "LE", "GT", "GE", "LIMIT",
        // Compute and math
        "CPT", "ADD", "SUB", "MUL", "DIV", "MOD", "SQR", "SQRT", "NEG", "ABS", "CLR", "SIN", "COS", "TAN",
        "ASN", "ACS", "ATN", "ASIN", "ACOS", "ATAN", "LN", "LOG", "XPY", "EXPT", "TRN", "TRUNC", "DEG", "RAD",
        // Move, logical, convert
        "MOV", "MOVE", "MVM", "MVMT", "BTD", "BTDT", "SWPB", "AND", "OR", "XOR", "NOT",
        "TOD", "FRD", "TO_BCD", "BCD_TO",
        // File, array, shift, sequencer
        "COP", "CPS", "FLL", "AVE", "SRT", "STD", "SIZE", "FAL", "FSC", "FBC", "DDT", "DTR", "BSL", "BSR",
        "FFL", "FFU", "LFL", "LFU", "SQI", "SQO", "SQL",
        // Program control
        "JMP", "LBL", "JSR", "SBR", "RET", "FOR", "BRK", "NOP", "AFI", "TND", "UID", "UIE", "MCR", "EOT",
        "SFP", "SFR", "EVENT",
        // System and messaging
        "GSV", "SSV", "MSG", "IOT", "BTR", "BTW", "PXRQ",
        // ASCII
        "ABL", "ACB", "ACL", "AHL", "ARD", "ARL", "AWA", "AWT",
        // Alarm, select, process control and drives
        "ALMD", "ALMA", "ESEL", "SEL", "SSUM", "RLIM", "MUX", "SNEG", "PID", "PIDE", "POSP", "SCL", "SCRV",
        "SRTP", "LDL2", "LDLG", "DEDT", "FGEN", "HPF", "LPF", "NTCH", "RMPS", "TOT", "UPDN", "D2SD", "D3SD",
        "DFF", "HLL", "MAVE", "MAXC", "MINC", "MSTD", "INTG", "DERV", "CC", "IMC", "MMC", "SOC", "PI", "PMUL",
        "RTOR", "JKFF", "SETD", "RESD", "ENPEN",
        // String (ladder)
        "CONCAT", "DELETE", "DTOS", "FIND", "INSERT", "LOWER", "MID", "RTOS", "STOD", "STOR", "UPPER",
        // Phase manager
        "PSC", "PCMD", "PFL", "PCLF", "PRNP", "PATT", "PPD", "POVR", "PDET",
        // GuardLogix safety (beyond the DC*/CR* family)
        "THRSE", "ENPEN",
        // Added from the v38 help index: floating-point tests, two-argument arctangent, drives, safety
        "ISINF", "ISNAN", "ATAN2", "HMIBC", "LV", "ASO", "SATT", "SDET", "SASI", "SCLF", "SCMD", "SOVR",
        // Motion (the rest of the family is matched by MotionOrSafetyFamily)
        "MSO", "MSF", "MASD", "MASR", "MAS", "MAH", "MAJ", "MAM", "MAG", "MCD", "MAPC", "MATC", "MCCP", "MCCD",
        "MCS", "MCSD", "MCSR", "MCT", "MCTP", "MCLM", "MCCM", "MDO", "MDF", "MDR", "MDW", "MAR", "MAW", "MAFR",
        "MAOC", "MAAT", "MGS", "MGSD", "MGSR", "MGSP", "MRP", "MRAT", "MRHD", "MRDC", "MDAC", "MAHD",
    };

    /// <summary>
    /// Instruction families whose members are not all listed: motion (<c>MA*</c>, <c>MC*</c>, <c>MD*</c>,
    /// <c>MG*</c>, <c>MR*</c>, <c>MS*</c>) and the GuardLogix safety instructions (<c>DC*</c>, <c>CR*</c>).
    /// </summary>
    private static bool MotionOrSafetyFamily(string name) =>
        name.Length is >= 3 and <= 6 && name.All(char.IsAsciiLetter) &&
        (name.StartsWith("DC", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("CR", StringComparison.OrdinalIgnoreCase) ||
         name.Length >= 3 && char.ToUpperInvariant(name[0]) == 'M' && "ACDGRS".Contains(char.ToUpperInvariant(name[1])));

    /// <summary>
    /// Instructions whose operands are all tags or numbers, so every operand is checked. Anything else that is
    /// a Logix instruction (motion, process control, GSV/SSV class names, ASCII, safety) can take keyword
    /// operands, so its operands are not checked.
    /// </summary>
    private static readonly HashSet<string> TagOperandInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "XIC", "XIO", "OTE", "OTL", "OTU", "ONS", "OSR", "OSF", "OSRI", "OSFI",
        "TON", "TOF", "RTO", "CTU", "CTD", "RES",
        "CMP", "EQU", "NEQ", "LES", "LEQ", "GRT", "GEQ", "LIM", "MEQ", "EQ", "NE", "LT", "LE", "GT", "GE", "LIMIT",
        "CPT", "ADD", "SUB", "MUL", "DIV", "MOD", "SQR", "SQRT", "NEG", "ABS", "CLR", "SIN", "COS", "TAN",
        "ASN", "ACS", "ATN", "ASIN", "ACOS", "ATAN", "LN", "LOG", "XPY", "EXPT", "TRN", "TRUNC", "DEG", "RAD",
        "MOV", "MOVE", "MVM", "MVMT", "BTD", "BTDT", "SWPB", "AND", "OR", "XOR", "NOT",
        "TOD", "FRD", "TO_BCD", "BCD_TO",
        "COP", "CPS", "FLL", "AVE", "SRT", "STD", "SIZE", "FAL", "FSC", "FBC", "DDT", "DTR", "BSL", "BSR",
        "FFL", "FFU", "LFL", "LFU", "SQI", "SQO", "SQL",
        "JSR", "SBR", "RET", "FOR", "MSG",
    };

    /// <summary>Instructions whose operand text is an arithmetic expression.</summary>
    private static readonly HashSet<string> ExpressionInstructions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CMP", "CPT", "FAL", "FSC",
    };

    /// <summary>Operators written as words inside an expression.</summary>
    private static readonly HashSet<string> ExpressionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "XOR", "NOT", "MOD",
    };

    /// <summary>For an instruction in <see cref="TagOperandInstructions"/>, the leading operands that are names, not tags.</summary>
    private static int LeadingNonTagOperands(string instruction) => instruction.ToUpperInvariant() switch
    {
        "JSR" or "FOR" => 1,   // routine name
        _ => 0,
    };

    // ── Symbol tables from exports ───────────────────────────────────────────

    /// <summary>Reads controller-scope tag names from an export of <c>Controller/Tags</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>The names, or <c>null</c> when the export has no controller <c>Tags</c> element.</returns>
    public static HashSet<string>? ControllerTagsFromExport(string exportXml)
    {
        try
        {
            var containers = SafeXml.ParseDocument(exportXml).Descendants("Controller").Elements("Tags").ToList();
            if (containers.Count == 0) return null;
            return NamesOf(containers.Elements("Tag"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Reads each program's tag names from an export of <c>Controller/Programs</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>
    /// Tag names per program (case ignored); only programs whose export includes their routines are listed, because
    /// only then does a missing <c>Tags</c> element mean "no tags". <c>null</c> when the export cannot be read.
    /// </returns>
    public static Dictionary<string, IReadOnlySet<string>>? ProgramTagsFromExport(string exportXml)
    {
        try
        {
            var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in SafeXml.ParseDocument(exportXml).Descendants("Program"))
            {
                var name = (string?)program.Attribute("Name");
                if (string.IsNullOrEmpty(name) || program.Element("Routines") is null) continue;
                map[name] = NamesOf(program.Elements("Tags").Elements("Tag"));
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Reads every program name from an export of <c>Controller/Programs</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>The names (case ignored; possibly none), or <c>null</c> when the export cannot be read.</returns>
    public static HashSet<string>? ProgramNamesFromExport(string exportXml)
    {
        try
        {
            return NamesOf(SafeXml.ParseDocument(exportXml).Descendants("Program"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// Reads, per program, the tags another program cannot reference. A program tag is reachable from another program
    /// only when its Usage is Public, Input or Output; a missing Usage (real exports omit it for ordinary local tags),
    /// Local or InOut is not.
    /// </summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>Non-reachable tag names per program (case ignored), or <c>null</c> when the export cannot be read.</returns>
    public static Dictionary<string, IReadOnlySet<string>>? LocalProgramTagsFromExport(string exportXml)
    {
        try
        {
            var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in SafeXml.ParseDocument(exportXml).Descendants("Program"))
            {
                var name = (string?)program.Attribute("Name");
                if (string.IsNullOrEmpty(name)) continue;
                map[name] = NamesOf(program.Elements("Tags").Elements("Tag")
                    .Where(t => (string?)t.Attribute("Usage") is not ("Public" or "Input" or "Output")));
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Reads, per program, the tags whose Usage is Output (G29: another program may read but not write them).</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>Output parameter names per program (case ignored), or <c>null</c> when the export cannot be read.</returns>
    public static Dictionary<string, IReadOnlySet<string>>? OutputParametersFromExport(string exportXml)
    {
        try
        {
            var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in SafeXml.ParseDocument(exportXml).Descendants("Program"))
            {
                var name = (string?)program.Attribute("Name");
                if (string.IsNullOrEmpty(name)) continue;
                map[name] = NamesOf(program.Elements("Tags").Elements("Tag")
                    .Where(t => string.Equals((string?)t.Attribute("Usage"), "Output", StringComparison.Ordinal)));
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Reads controller-scope tag data types (name to type) from an export of <c>Controller/Tags</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>The types of the tags that declare one (aliases do not), or <c>null</c> when unreadable.</returns>
    public static Dictionary<string, string>? ControllerTagTypesFromExport(string exportXml)
    {
        try
        {
            var containers = SafeXml.ParseDocument(exportXml).Descendants("Controller").Elements("Tags").ToList();
            return containers.Count == 0 ? null : TypesOf(containers.Elements("Tag"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Reads each program's tag data types from an export of <c>Controller/Programs</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>Tag name to type per program (case ignored), or <c>null</c> when unreadable.</returns>
    public static Dictionary<string, IReadOnlyDictionary<string, string>>? ProgramTagTypesFromExport(string exportXml)
    {
        try
        {
            var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var program in SafeXml.ParseDocument(exportXml).Descendants("Program"))
            {
                var name = (string?)program.Attribute("Name");
                if (string.IsNullOrEmpty(name) || program.Element("Routines") is null) continue;
                map[name] = TypesOf(program.Elements("Tags").Elements("Tag"));
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Reads user data type members (type to member name to member type) from an export of <c>Controller/DataTypes</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>The user types, or <c>null</c> when the export cannot be read.</returns>
    public static Dictionary<string, IReadOnlyDictionary<string, string>>? DataTypeMembersFromExport(string exportXml)
    {
        try
        {
            var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dt in SafeXml.ParseDocument(exportXml).Descendants("DataType"))
            {
                var name = (string?)dt.Attribute("Name");
                var members = dt.Element("Members");
                if (string.IsNullOrEmpty(name) || members is null) continue;
                map[name] = TypesOf(members.Elements("Member"));
            }
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static readonly HashSet<string> TypesWithKnownMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "BIT",
        "TIMER", "COUNTER", "CONTROL", "STRING",
    };

    /// <summary>
    /// Whether any tag has a type whose members the checker does not already know, i.e. whether exporting the
    /// project's data types could change the answer.
    /// </summary>
    /// <param name="controllerTagTypes">Controller tag types, or <c>null</c>.</param>
    /// <param name="programTagTypes">Program tag types, or <c>null</c>.</param>
    /// <returns><c>true</c> when at least one tag type is not atomic or predefined.</returns>
    public static bool NeedsDataTypes(
        IReadOnlyDictionary<string, string>? controllerTagTypes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? programTagTypes) =>
        (controllerTagTypes?.Values ?? []).Concat(programTagTypes?.Values.SelectMany(d => d.Values) ?? [])
            .Any(t => !TypesWithKnownMembers.Contains(t));

    private static Dictionary<string, string> TypesOf(IEnumerable<XElement> elements)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in elements)
            if ((string?)e.Attribute("Name") is { Length: > 0 } n && (string?)e.Attribute("DataType") is { Length: > 0 } t)
                map[n] = t;
        return map;
    }

    /// <summary>Reads Add-On Instruction names from an export of <c>Controller/AddOnInstructionDefinitions</c>.</summary>
    /// <param name="exportXml">The export text.</param>
    /// <returns>The names (possibly none), or <c>null</c> when the export cannot be read.</returns>
    public static HashSet<string>? AoiNamesFromExport(string exportXml)
    {
        try
        {
            return NamesOf(SafeXml.ParseDocument(exportXml).Descendants("AddOnInstructionDefinition"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Names of the <c>&lt;Tag Name=...&gt;</c> declarations in a fragment (any scope).</summary>
    /// <param name="fragmentXml">The import fragment.</param>
    /// <returns>The names; empty when the fragment does not parse.</returns>
    public static HashSet<string> DeclaredTagsInFragment(string? fragmentXml)
    {
        if (string.IsNullOrWhiteSpace(fragmentXml)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return NamesOf(SafeXml.ParseDocument(fragmentXml).Descendants("Tag"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static HashSet<string> NamesOf(IEnumerable<XElement> elements)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in elements)
            if ((string?)e.Attribute("Name") is { Length: > 0 } n) set.Add(n);
        return set;
    }

    // ── Check ────────────────────────────────────────────────────────────────

    /// <summary>Checks rung texts.</summary>
    /// <param name="rungTexts">The rung texts, in order.</param>
    /// <param name="symbols">What is known about the project.</param>
    /// <returns>The outcome; <see cref="ReferenceCheckResult.Refused"/> when anything must be refused.</returns>
    public static ReferenceCheckResult Check(IReadOnlyList<string> rungTexts, ReferenceSymbols symbols)
    {
        ArgumentNullException.ThrowIfNull(rungTexts);
        ArgumentNullException.ThrowIfNull(symbols);
        if (rungTexts.Count == 0) return ReferenceCheckResult.Pass;

        // AOI names are case-insensitive whatever comparer the caller's set was built with.
        var aoiNames = symbols.AoiNames is null
            ? null
            : new HashSet<string>(symbols.AoiNames, StringComparer.OrdinalIgnoreCase);
        var unlistedInstructions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var undefinedTags = new List<ReferenceFinding>();
        var notPublic = new List<ReferenceFinding>();
        var undefinedMembers = new List<ReferenceFinding>();
        var crossOutputs = new List<ReferenceFinding>();
        var unknownPrograms = new List<ReferenceFinding>();
        var refusals = new List<(ReferenceFinding Finding, string Detail)>();
        var warnings = new List<string>();
        int unparsed = 0;
        var unverifiableTags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unverifiableInstructions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rungTexts.Count; i++)
        {
            if (!TryParseInstructions(rungTexts[i] ?? string.Empty, out var instructions))
            {
                unparsed++;
                continue;
            }

            foreach (var (name, args) in instructions)
            {
                bool isAoi = aoiNames?.Contains(name) == true;
                bool known = KnownInstructions.Contains(name) || MotionOrSafetyFamily(name);
                if (!known && !isAoi)
                {
                    // Not refused: the list may lag a Logix release, and Logix is the final check at publish.
                    if (symbols.AoiNames is null) unverifiableInstructions.Add(name);
                    else unlistedInstructions.Add(name);
                }

                // Instructions with plain tag/number operands, AOI calls and unknown instructions have their
                // operands checked: a misspelt instruction must not hide an undefined tag (FOO(NoSuchTag_B)).
                bool checkOperands = isAoi || !known || TagOperandInstructions.Contains(name);
                if (!checkOperands) continue;

                bool expression = ExpressionInstructions.Contains(name);
                int skip = isAoi ? 0 : LeadingNonTagOperands(name);
                var operands = SplitOperands(args);
                for (int k = skip; k < operands.Count; k++)
                {
                    foreach (var reference in ReferencesIn(operands[k], expression))
                    {
                        var outcome = Resolve(reference, symbols);
                        switch (outcome.Status)
                        {
                            case Resolution.Ok:
                                if (k == skip && IsCoilWrite(name) && IsOtherProgramOutput(reference, symbols))
                                    Add(crossOutputs, new ReferenceFinding(i, name, reference.Display,
                                        ReferenceCheckKinds.CrossProgramOutputNotWritable));
                                break;
                            case Resolution.Unknown: unverifiableTags.Add(reference.Display); break;
                            case Resolution.UnknownProgram:
                                Add(unknownPrograms, new ReferenceFinding(i, name, reference.Display,
                                    ReferenceCheckKinds.UnknownProgram, reference.Program));
                                break;
                            case Resolution.Undefined:
                                Add(undefinedTags, new ReferenceFinding(i, name, reference.Display, Detail: outcome.Detail));
                                if (outcome.Detail is not null) refusals.Add((new ReferenceFinding(i, name, reference.Display), outcome.Detail));
                                break;
                            case Resolution.NotPublic:
                                Add(notPublic, new ReferenceFinding(i, name, reference.Display, ReferenceCheckKinds.TagNotPublic));
                                break;
                            case Resolution.UndefinedMember:
                                Add(undefinedMembers, new ReferenceFinding(i, name, reference.Display, ReferenceCheckKinds.UndefinedMember));
                                break;
                        }
                    }
                }
            }
        }

        if (unparsed > 0)
            warnings.Add($"{unparsed} rung(s) could not be parsed for the tag and instruction pre-check; they were let through unchecked.");
        if (unverifiableInstructions.Count > 0)
            warnings.Add("Instruction(s) not checked because the project's Add-On Instruction list could not be read: " +
                         string.Join(", ", unverifiableInstructions) + ".");
        if (unlistedInstructions.Count > 0)
            warnings.Add("Instruction(s) not in the known-instruction list and not Add-On Instructions in this " +
                         "project: " + string.Join(", ", unlistedInstructions) + ". They were let through; " +
                         "Logix checks them at import/publish.");
        foreach (var f in unknownPrograms)
            warnings.Add(DescribeUnknownProgram(f));
        if (unverifiableTags.Count > 0)
            warnings.Add("Tag reference(s) not checked because they could not be resolved from the export: " +
                         string.Join(", ", unverifiableTags.Take(10)) + (unverifiableTags.Count > 10 ? ", ..." : "") + ".");

        if (undefinedTags.Count + notPublic.Count + undefinedMembers.Count + crossOutputs.Count > 0)
        {
            var findings = undefinedTags.Concat(notPublic).Concat(undefinedMembers).Concat(crossOutputs).ToList();
            var parts = new List<string>();
            string? hint = null;
            string kind = ReferenceCheckKinds.UndefinedTag;

            if (undefinedTags.Count > 0)
            {
                var names = string.Join(", ", undefinedTags.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase));
                var part = $"{names}: no such tag in the controller scope or in the routine's program scope, and this call " +
                           "declares no tag by that name " +
                           $"(rung {string.Join(", ", undefinedTags.Select(f => f.RungIndex).Distinct())}).";
                var elsewhere = refusals.Select(r => r.Detail).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (elsewhere.Count > 0)
                {
                    part += " " + string.Join(" ", elsewhere);
                    hint = OtherProgramHint;
                }
                parts.Add(part);
            }
            if (notPublic.Count > 0)
            {
                if (undefinedTags.Count == 0) kind = ReferenceCheckKinds.TagNotPublic;
                parts.Add(string.Join(", ", notPublic.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase)) +
                          ": the tag is not Public in its program, so another program cannot reference it " +
                          $"(rung {string.Join(", ", notPublic.Select(f => f.RungIndex).Distinct())}).");
                hint ??= "Make the tag Public in its program (or use a Public tag or a program parameter), or reference a controller-scope tag.";
            }
            if (undefinedMembers.Count > 0)
            {
                if (undefinedTags.Count == 0 && notPublic.Count == 0) kind = ReferenceCheckKinds.UndefinedMember;
                parts.Add(string.Join(", ", undefinedMembers.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase)) +
                          ": the tag's data type has no such member " +
                          $"(rung {string.Join(", ", undefinedMembers.Select(f => f.RungIndex).Distinct())}).");
                hint ??= "Check the member name against the tag's data type (a UDT's members, or PRE/ACC/EN/TT/DN for a TIMER).";
            }
            if (crossOutputs.Count > 0)
            {
                if (undefinedTags.Count + notPublic.Count + undefinedMembers.Count == 0)
                    kind = ReferenceCheckKinds.CrossProgramOutputNotWritable;
                parts.Add(string.Join("; ", crossOutputs.Select(f =>
                              $"rung {f.RungIndex}: {f.Instruction}({f.Name}) writes another program's Output parameter")) +
                          $" ({ReferenceCheckKinds.DirectWriteOutputMnemonic}). Only the program that owns an Output " +
                          "parameter can write it; other programs may read it.");
                hint ??= CrossProgramOutputHint;
            }

            return new ReferenceCheckResult(kind, findings,
                string.Join(" ", parts) + " Nothing was imported or staged.", warnings, hint, unknownPrograms);
        }

        return new ReferenceCheckResult(null, [], null, warnings, UnknownPrograms: unknownPrograms);
    }

    /// <summary>
    /// One-line description of an <see cref="ReferenceCheckKinds.UnknownProgram"/> finding, naming the rung, the
    /// operand and the program that does not exist.
    /// </summary>
    /// <param name="finding">An entry of <see cref="ReferenceCheckResult.UnknownPrograms"/>.</param>
    /// <returns>The text used for the stage-time warning and for the refusal reason.</returns>
    public static string DescribeUnknownProgram(ReferenceFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return $"UnknownProgram: rung {finding.RungIndex}: {finding.Instruction}({finding.Name}) names program " +
               $"'{finding.Detail}', which does not exist in the controller.";
    }

    /// <summary>
    /// One-line, agent-facing description of a single finding, used as the <c>text</c> of its entry in the
    /// <c>rejectReasons</c> list so that every category the pre-check found is reported together.
    /// </summary>
    /// <param name="finding">A finding from <see cref="ReferenceCheckResult.Findings"/>.</param>
    /// <returns>The description, naming the category, the reference, the instruction and the rung.</returns>
    public static string DescribeFinding(ReferenceFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        string what = finding.Kind switch
        {
            ReferenceCheckKinds.TagNotPublic =>
                "the tag is not Public in its program, so another program cannot reference it",
            ReferenceCheckKinds.UndefinedMember => "the tag's data type has no such member",
            ReferenceCheckKinds.UnknownProgram => "the program does not exist in the controller",
            ReferenceCheckKinds.CrossProgramOutputNotWritable =>
                "it is another program's Output parameter and only that program can write it (" +
                ReferenceCheckKinds.DirectWriteOutputMnemonic + "); reads are allowed",
            _ => "no such tag in the controller scope or in the routine's program scope, and this call declares no tag by that name",
        };
        var text = $"{finding.Kind}: {finding.Name} in {finding.Instruction} (rung {finding.RungIndex}): {what}.";
        return finding.Detail is null ? text : text + " " + finding.Detail;
    }

    /// <summary>The remedy offered for a write to another program's Output parameter.</summary>
    internal const string CrossProgramOutputHint =
        "Write the value inside the owning program, or use a Public or Input parameter of that program as the " +
        "destination, or write a tag of this program and read the Output with XIC/XIO or an input operand.";

    /// <summary>True for the coil instructions whose first operand is written.</summary>
    private static bool IsCoilWrite(string instruction) =>
        instruction.Equals("OTE", StringComparison.OrdinalIgnoreCase)
        || instruction.Equals("OTL", StringComparison.OrdinalIgnoreCase)
        || instruction.Equals("OTU", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="r"/> is <c>\Other.Tag</c> and that tag is an Output parameter of Other.</summary>
    private static bool IsOtherProgramOutput(TagReference r, ReferenceSymbols s) =>
        r.Program is not null
        && !string.Equals(r.Program, s.TargetProgram, StringComparison.OrdinalIgnoreCase)
        && s.OutputParameters is not null
        && s.OutputParameters.TryGetValue(r.Program, out var outputs)
        && outputs.Contains(r.Root);

    /// <summary>The remedy offered when a bare tag name exists only in another program.</summary>
    internal const string OtherProgramHint = "use \\<Program>.<Tag> if it is Public";

    private static void Add(List<ReferenceFinding> list, ReferenceFinding f)
    {
        if (!list.Any(x => x.RungIndex == f.RungIndex && string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)))
            list.Add(f);
    }

    /// <summary>A tag reference: optional program qualifier, root tag name and the member path after it.</summary>
    private readonly record struct TagReference(string? Program, string Root, string Display, bool Bare, IReadOnlyList<string> Members);

    private enum Resolution { Ok, Undefined, NotPublic, UndefinedMember, Unknown, UnknownProgram }

    private readonly record struct Resolved(Resolution Status, string? Detail = null);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoTypes =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>Resolves one reference: exists, certainly undefined / not Public / no such member, or cannot tell.</summary>
    private static Resolved Resolve(TagReference r, ReferenceSymbols s)
    {
        if (s.DeclaredTags.Contains(r.Root)) return new(Resolution.Ok);

        if (r.Program is null)
        {
            IReadOnlyDictionary<string, string>? targetTypes = null;
            s.ProgramTagTypes?.TryGetValue(s.TargetProgram ?? string.Empty, out targetTypes);
            if (s.TargetProgramTags?.Contains(r.Root) == true) return CheckMembers(r, targetTypes, s);
            if (s.ControllerTags?.Contains(r.Root) == true) return CheckMembers(r, s.ControllerTagTypes, s);
            if (s.ControllerTags is null || s.TargetProgramTags is null) return new(Resolution.Unknown);

            // Undefined here. If another program has a tag by that name, say so: that is the usual mistake.
            var owners = s.ProgramTags is null ? [] : s.ProgramTags
                .Where(kv => kv.Value.Contains(r.Root) &&
                             !string.Equals(kv.Key, s.TargetProgram, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            return owners.Count == 0
                ? new(Resolution.Undefined)
                : new(Resolution.Undefined,
                      $"'{r.Root}' exists only in program {string.Join(", ", owners.Select(o => $"'{o}'"))}: " +
                      OtherProgramHint + $" (for example \\{owners[0]}.{r.Root}).");
        }

        if (s.ProgramTags is null || !s.ProgramTags.TryGetValue(r.Program, out var tags))
            return s.ProgramNames is not null && !s.ProgramNames.Contains(r.Program)
                ? new(Resolution.UnknownProgram)
                : new(Resolution.Unknown);
        if (!tags.Contains(r.Root)) return new(Resolution.Undefined);
        bool sameProgram = string.Equals(r.Program, s.TargetProgram, StringComparison.OrdinalIgnoreCase);
        if (!sameProgram && s.LocalProgramTags is not null &&
            s.LocalProgramTags.TryGetValue(r.Program, out var local) && local.Contains(r.Root))
            return new(Resolution.NotPublic);
        IReadOnlyDictionary<string, string>? types = null;
        s.ProgramTagTypes?.TryGetValue(r.Program, out types);
        return CheckMembers(r, types, s);
    }

    /// <summary>
    /// Walks <c>Tag.Member.Member</c> through the known types. Stops, accepting, at the first type it has no
    /// member table for, at a bit select, or at a tag whose type is not known.
    /// </summary>
    private static Resolved CheckMembers(TagReference r, IReadOnlyDictionary<string, string>? tagTypes, ReferenceSymbols s)
    {
        if (r.Members.Count == 0 || tagTypes is null || !tagTypes.TryGetValue(r.Root, out var type))
            return new(Resolution.Ok);

        foreach (var member in r.Members)
        {
            if (member.Length == 0 || char.IsAsciiDigit(member[0])) break;        // bit select
            IReadOnlyDictionary<string, string>? members = null;
            if (s.TypeMembers is not null) s.TypeMembers.TryGetValue(type, out members);
            members ??= DeclaredMemberTypes.PredefinedMembers.GetValueOrDefault(type);
            if (members is null) break;                                           // type not known: let Logix decide
            if (!members.TryGetValue(member, out var next)) return new(Resolution.UndefinedMember);
            type = next;
        }
        return new(Resolution.Ok);
    }

    // ── Rung text parsing ────────────────────────────────────────────────────

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Splits rung text into (instruction, raw argument text). <c>false</c> on anything unexpected.</summary>
    private static bool TryParseInstructions(string text, out List<(string Name, string Args)> instructions)
    {
        instructions = [];
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c is '[' or ']' or ',' or ';') { i++; continue; }
            if (!IsWordStart(c)) return false;

            int start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            string name = text[start..i];
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '(') return false;

            int depth = 0, argStart = i + 1;
            for (; i < text.Length; i++)
            {
                char d = text[i];
                if (d is '\'' or '"')
                {
                    int close = text.IndexOf(d, i + 1);
                    if (close < 0) return false;
                    i = close;
                }
                else if (d == '(') depth++;
                else if (d == ')' && --depth == 0) break;
            }
            if (depth != 0) return false;
            instructions.Add((name, text[argStart..i]));
            i++;
        }
        return true;
    }

    /// <summary>Splits argument text at top-level commas (not inside parentheses, brackets or quotes).</summary>
    private static List<string> SplitOperands(string args)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c is '\'' or '"')
            {
                int close = args.IndexOf(c, i + 1);
                int end = close < 0 ? args.Length - 1 : close;
                sb.Append(args, i, end - i + 1);
                i = end;
                continue;
            }
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            if (c == ',' && depth == 0) { result.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// Tag references in one operand. An operand that reads like words (an enumeration such as
    /// <c>Units per sec</c>) yields none, as does anything that is not a plain path or expression.
    /// </summary>
    private static IEnumerable<TagReference> ReferencesIn(string operand, bool expression)
    {
        var text = operand.Trim();
        if (text.Length == 0) return [];
        if (!expression && LooksLikeWords(text)) return [];
        var found = new List<TagReference>();
        ScanExpression(text, expression, found);
        return found;
    }

    private static bool LooksLikeWords(string text)
    {
        bool inQuote = false;
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            char c = text[i];
            if (inQuote) { if (c == quote) inQuote = false; continue; }
            if (c is '\'' or '"') { inQuote = true; quote = c; continue; }
            if (c is '[' or '(') depth++;
            else if (c is ']' or ')') depth--;
            if (depth == 0 && char.IsWhiteSpace(c) && i > 0 && IsWordChar(text[i - 1]))
            {
                int j = i;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (j < text.Length && IsWordStart(text[j])) return true;
            }
        }
        return false;
    }

    /// <summary>Walks an operand or expression and collects the tag references in it.</summary>
    private static void ScanExpression(string s, bool expression, List<TagReference> found)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            if (c is '\'' or '"')
            {
                int close = s.IndexOf(c, i + 1);
                i = close < 0 ? s.Length : close + 1;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                // A number: 5, 0005, 1.5, 1.5e-3, 16#FF, 2#0101_0101.
                int start = i;
                while (i < s.Length && (IsWordChar(s[i]) || s[i] is '#' or '.')) i++;
                var token = s[start..i];
                if (i < s.Length && s[i] is '+' or '-' && !token.Contains('#') &&
                    token.EndsWith("e", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
                {
                    i++;
                    while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                }
                continue;
            }

            if (IsWordStart(c) || c == '\\')
            {
                int start = i;
                if (!TryReadPath(s, ref i, expression, found, out var reference))
                {
                    // Not a path we understand: skip the character and keep going.
                    i = Math.Max(i, start + 1);
                    continue;
                }
                if (reference is { } r)
                {
                    int next = i;
                    while (next < s.Length && char.IsWhiteSpace(s[next])) next++;
                    bool isCall = next < s.Length && s[next] == '(' && r.Bare;
                    bool isWord = expression && r.Bare && ExpressionWords.Contains(r.Root);
                    if (!isCall && !isWord) found.Add(r);
                }
                continue;
            }

            i++;
        }
    }

    /// <summary>
    /// Reads <c>[\Program.]Root(.member | [index] | .bit)*</c> at <paramref name="i"/>. A root containing a colon is a
    /// module I/O tag and is not returned. Index expressions inside brackets are scanned for their own references.
    /// </summary>
    private static bool TryReadPath(string s, ref int i, bool expression, List<TagReference> found, out TagReference? reference)
    {
        reference = null;
        int start = i;
        string? program = null;

        if (s[i] == '\\')
        {
            i++;
            int p = i;
            while (i < s.Length && IsWordChar(s[i])) i++;
            if (i == p || i >= s.Length || s[i] != '.') return false;
            program = s[p..i];
            i++;
        }

        int rootStart = i;
        if (i >= s.Length || !IsWordStart(s[i])) return false;
        bool module = false;
        while (i < s.Length && (IsWordChar(s[i]) || s[i] == ':')) { if (s[i] == ':') module = true; i++; }
        string root = s[rootStart..i];
        int rootEnd = i;

        // Members, bits and indexes.
        var memberPath = new List<string>();
        while (i < s.Length)
        {
            if (s[i] == '.' && i + 1 < s.Length && IsWordChar(s[i + 1]))
            {
                i++;
                int memberStart = i;
                while (i < s.Length && (IsWordChar(s[i]) || s[i] == ':')) i++;
                memberPath.Add(s[memberStart..i]);
            }
            else if (s[i] == '[')
            {
                int depth = 0, j = i;
                for (; j < s.Length; j++)
                {
                    if (s[j] == '[') depth++;
                    else if (s[j] == ']' && --depth == 0) break;
                }
                if (j >= s.Length) { i = s.Length; break; }
                ScanExpression(s[(i + 1)..j], expression: true, found);
                i = j + 1;
            }
            else break;
        }

        if (!module)
            reference = new TagReference(program, root, s[start..i], Bare: program is null && i == rootEnd, memberPath);
        return true;
    }
}
