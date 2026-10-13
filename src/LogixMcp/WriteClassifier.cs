using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// Shared write-classification helper for Rockwell Automation Logix RLL rung neutral text.
/// Provides the canonical instruction→operand-kind mapping used by both
/// <see cref="CrossReferenceHandler"/> and <see cref="GateEngine"/> so the logic is
/// defined exactly once.
/// </summary>
public static class WriteClassifier
{
    /// <summary>
    /// Maps instruction key (upper-case) to the argument indices that represent write (output)
    /// operands.  A <c>null</c> value means every argument is a read.  Instructions absent from
    /// this table are classified as <c>"unknown"</c>.
    ///
    /// Keys are canonical (the v36+ IEC spelling, see <see cref="InstructionMnemonics"/>); a lookup
    /// canonicalizes the instruction first, so the v35-and-earlier spelling (<c>LES</c>, <c>MOV</c>,
    /// <c>SQR</c>, ...) classifies identically.
    ///
    /// Instruction semantics (Rockwell Automation Logix instruction set):
    ///
    ///   Read-only contacts (bit-test):
    ///     XIC(Tag)          – Examine If Closed     – Tag is tested, never written
    ///     XIO(Tag)          – Examine If Open        – Tag is tested, never written
    ///
    ///   Write-only output coils:
    ///     OTE(Tag)          – Output Energize        – arg 0 is driven
    ///     OTL(Tag)          – Output Latch           – arg 0 is set
    ///     OTU(Tag)          – Output Unlatch         – arg 0 is cleared
    ///
    ///   Comparison instructions (all arguments are source reads):
    ///     EQ(A, B)          – Equal                 (EQU before v36)
    ///     NE(A, B)          – Not Equal             (NEQ before v36)
    ///     GT(A, B)          – Greater Than          (GRT before v36)
    ///     GE(A, B)          – Greater Than or Equal (GEQ before v36)
    ///     LT(A, B)          – Less Than             (LES before v36)
    ///     LE(A, B)          – Less Than or Equal    (LEQ before v36)
    ///     LIMIT(Low,Test,Hi) – Limit Test           (LIM before v36)
    ///     MEQ(A,Mask,B)     – Mask Equal
    ///     CMP(expression)   – Compare expression
    ///
    ///   Data-move (last arg is destination write):
    ///     MOVE(Src, Dest)        – Move:       write index 1 (MOV before v36)
    ///     COP(Src, Dest, Len)    – Copy File:  write index 1
    ///     FLL(Src, Dest, Len)    – Fill File:  write index 1
    ///
    ///   Arithmetic (last arg is destination write, others are source reads):
    ///     ADD(A, B, Dest)        – Add:        write index 2
    ///     SUB(A, B, Dest)        – Subtract:   write index 2
    ///     MUL(A, B, Dest)        – Multiply:   write index 2
    ///     DIV(A, B, Dest)        – Divide:     write index 2
    ///     MOD(A, B, Dest)        – Modulo:     write index 2
    ///     SQRT(Src, Dest)        – Square Root: write index 1 (SQR before v36)
    ///     NEG(Src, Dest)         – Negate:     write index 1
    ///     ABS/SIN/COS/TAN/ASIN/ACOS/ATAN/LN/LOG/DEG/RAD/TO_BCD/BCD_TO/TRUNC(Src, Dest) – write index 1
    ///                            (ASN/ACS/ATN/TOD/FRD/TRN before v36)
    ///     EXPT(A, B, Dest)       – X to the power of Y: write index 2 (XPY before v36)
    ///
    ///   Compute — arg 0 is destination (write), arg 1 is expression:
    ///     CPT(Dest, Expr)        – Compute:    write index 0
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<int>?> KindMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Boolean contact (bit-test) instructions — all args read ───────
            ["XIC"] = null,
            ["XIO"] = null,

            // ── Boolean output coil instructions — arg 0 is written ──────────
            ["OTE"] = [0],
            ["OTL"] = [0],
            ["OTU"] = [0],

            // ── Comparison instructions — all args read ───────────────────────
            // Canonical (v36+) spellings; the pre-v36 EQU/NEQ/GRT/GEQ/LES/LEQ/LIM map here.
            ["EQ"] = null,
            ["NE"] = null,
            ["GT"] = null,
            ["GE"] = null,
            ["LT"] = null,
            ["LE"] = null,
            ["LIMIT"] = null,   // LIMIT(Low, Test, High) is a test; it has no destination
            ["MEQ"] = null,
            ["CMP"] = null,

            // ── Data-move instructions — arg 1 is destination (write) ─────────
            ["MOVE"] = [1],     // MOV before v36
            ["COP"] = [1],
            ["FLL"] = [1],
            ["CPS"] = [1],   // Synchronous Copy File: CPS(Src, Dest, Len)

            // ── Arithmetic — arg 2 is destination (write) ─────────────────────
            ["ADD"] = [2],
            ["SUB"] = [2],
            ["MUL"] = [2],
            ["DIV"] = [2],
            ["MOD"] = [2],

            // ── Single-source arithmetic — arg 1 is destination (write) ───────
            // Operand order per Logix 5000 General Instructions (1756-RM003),
            // Compute/Math and Move/Logical groups: OP(Source, Dest).
            ["SQRT"] = [1],     // SQR before v36
            ["NEG"] = [1],
            ["ABS"] = [1],
            ["SIN"] = [1],
            ["COS"] = [1],
            ["TAN"] = [1],
            ["ASIN"] = [1],     // ASN before v36
            ["ACOS"] = [1],     // ACS before v36
            ["ATAN"] = [1],     // ATN before v36
            ["LN"]  = [1],
            ["LOG"] = [1],
            ["DEG"] = [1],
            ["RAD"] = [1],
            ["TO_BCD"] = [1],   // TOD before v36
            ["BCD_TO"] = [1],   // FRD before v36
            ["TRUNC"] = [1],    // TRN before v36

            // EXPT(Source A, Source B, Dest) — X to the power of Y; dest is arg 2 (XPY before v36).
            ["EXPT"] = [2],

            // ── Compute — arg 0 is destination (write), arg 1 is expression ───
            ["CPT"] = [0],

            // ── Timers / counters — arg 0 is the backing structure, which the
            //    instruction writes (.ACC/.DN/.TT/.EN). Confirmed against real
            //    v36 rung text: TON(TestTimer,?,?).
            ["TON"] = [0],
            ["TOF"] = [0],
            ["RTO"] = [0],
            ["CTU"] = [0],
            ["CTD"] = [0],
            ["RES"] = [0],
            // ST spellings of timers/counters: the backing structure is arg 0.
            ["TONR"] = [0],
            ["TOFR"] = [0],
            ["RTOR"] = [0],
            ["CTUD"] = [0],

            // ── v36 renamed 16 mnemonics to IEC 61131-3 names (InstructionMnemonics). Their
            //    canonical spellings are the keys above; ClassifyKind canonicalizes first, so
            //    LES/MOV/SQR/... classify the same as LT/MOVE/SQRT/... In Structured Text,
            //    ATAN(x), SQRT(x), TRUNC(x), ASIN(x) and ACOS(x) are one-argument value-returning
            //    functions: their only argument is index 0, a read, so they write nothing.

            // ── Bit/word clear — arg 0 is destination ─────────────────────────
            ["CLR"] = [0],

            // ── String instructions with a destination ────────────────────────
            // Source for every index below: Logix 5000 General Instructions reference
            // (1756-RM003), "String instructions" (ASCII String) group. The repo's
            // lgx-instruction-reference skill does not cover these instructions.
            // CONCAT(Source A, Source B, Dest): Dest is index 2.
            ["CONCAT"] = [2],
            // DELETE(Source, Quantity, Start, Dest): Dest is index 3.
            ["DELETE"] = [3],
            // INSERT(Source A, Source B, Start, Dest): Dest is index 3.
            ["INSERT"] = [3],
            // MID(Source, Quantity, Start, Dest): Dest is index 3.
            ["MID"]    = [3],
            // FIND(Source, Search, Start, Result): Result (DINT) is index 3.
            ["FIND"]   = [3],
            // UPPER(Source, Dest): Dest is index 1.
            ["UPPER"]  = [1],
            // LOWER(Source, Dest): Dest is index 1.
            ["LOWER"]  = [1],
            // DTOS(Source, Dest): DINT to string, Dest is index 1.
            ["DTOS"]   = [1],
            // STOD(Source, Dest): string to DINT, Dest is index 1.
            ["STOD"]   = [1],
            // RTOS(Source, Dest): REAL to string, Dest is index 1.
            ["RTOS"]   = [1],
            // STOR(Source, Dest): string to REAL, Dest is index 1.
            ["STOR"]   = [1],

            // ── MSG(MessageControl) — the operand is the MESSAGE control tag, which the
            //    instruction drives (.EN/.EW/.ER/.DN/.ST and, for a read, the data it
            //    delivers). The destination the message fills is named by the control
            //    tag's <MessageParameters LocalElement="..."/> (read-type MessageType),
            //    scanned by ScanMessageDestinations.
            ["MSG"] = [0],

            // ── Motion instructions — MxX(Axis, MotionControl, ...). The instruction commands the
            //    axis (arg 0) and drives the MOTION_INSTRUCTION control tag (arg 1: .EN/.DN/.IP/
            //    .PC/.ER). Every later operand (speed, rates, enumerations such as
            //    "Units per sec2" / "% of Time", Yes/No flags) is a source. Only instructions whose
            //    operand order is (axis, control, inputs...) are listed; others stay unknown.
            ["MSO"] = [0, 1], ["MSF"] = [0, 1], ["MASD"] = [0, 1], ["MASR"] = [0, 1],
            ["MDO"] = [0, 1], ["MDF"] = [0, 1], ["MAS"] = [0, 1], ["MAH"] = [0, 1],
            ["MAJ"] = [0, 1], ["MAM"] = [0, 1], ["MAG"] = [0, 1], ["MCD"] = [0, 1],
            ["MRP"] = [0, 1], ["MAFR"] = [0, 1], ["MAPC"] = [0, 1], ["MATC"] = [0, 1],
            ["MDAC"] = [0, 1], ["MAAT"] = [0, 1], ["MRAT"] = [0, 1], ["MAOC"] = [0, 1],
            ["MDOC"] = [0, 1], ["MAR"] = [0, 1], ["MAW"] = [0, 1],
        };

    /// <summary>Instruction key for a subroutine call, handled positionally.</summary>
    private const string JsrKey = "JSR";

    /// <summary>
    /// Classifies one tag occurrence at argument index <paramref name="argIndex"/> of an
    /// instruction identified by <paramref name="instructionKey"/>.
    /// </summary>
    /// <param name="instructionKey">Instruction mnemonic, e.g. <c>XIC</c>, <c>MOV</c>.</param>
    /// <param name="argIndex">Zero-based index of the argument that matched the tag.</param>
    /// <returns><c>"read"</c>, <c>"write"</c>, or <c>"unknown"</c>.</returns>
    public static string ClassifyKind(string instructionKey, int argIndex) =>
        ClassifyKind(instructionKey, argIndex, null);

    /// <summary>
    /// Classifies one tag occurrence, additionally resolving Add-On Instruction
    /// calls against <paramref name="aoiWrites"/> (see <see cref="BuildAoiWriteMap"/>).
    /// An AOI call such as <c>aoi_Test(aoiTestInstance,In1,Out1)</c> is not in
    /// <see cref="KindMap"/> — without the AOI map it classifies as
    /// <c>"unknown"</c> and any tag it drives escapes the ProgramWritten deny.
    /// </summary>
    public static string ClassifyKind(
        string instructionKey,
        int argIndex,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites)
    {
        if (KindMap.TryGetValue(InstructionMnemonics.Canonical(instructionKey), out var writeIndices))
        {
            // null means all arguments are reads
            if (writeIndices is null)
                return "read";

            return writeIndices.Contains(argIndex) ? "write" : "read";
        }

        if (aoiWrites is not null &&
            aoiWrites.TryGetValue(instructionKey, out var aoiIndices))
        {
            // An encoded / unreadable AOI definition: its parameter usage is unknown.
            if (ReferenceEquals(aoiIndices, UnreadableAoi)) return "unknown";
            return aoiIndices.Contains(argIndex) ? "write" : "read";
        }

        return "unknown";
    }

    /// <summary>
    /// Sentinel value in the <see cref="BuildAoiWriteMap"/> result for an AOI whose definition is
    /// encoded or otherwise unreadable.  Its name is still a known project AOI (so it is never
    /// treated as a pure function or type conversion), but every argument classifies as unknown.
    /// </summary>
    private static readonly IReadOnlySet<int> UnreadableAoi = new HashSet<int>();

    /// <summary>
    /// Classifies one tag occurrence with full instruction context.  Required for
    /// <c>JSR</c>, whose write operands cannot be expressed as a fixed index set:
    /// the neutral text is <c>JSR(Routine, InputCount, In1..InN, Ret1..RetM)</c>,
    /// so the boundary between passed-in and returned parameters is carried in
    /// argument 1 at runtime.  Every argument at or beyond
    /// <c>2 + InputCount</c> is a return parameter and is written by the callee.
    /// </summary>
    public static string ClassifyKind(
        Instruction instruction,
        int argIndex,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites)
    {
        if (!string.Equals(instruction.Key, JsrKey, StringComparison.OrdinalIgnoreCase))
            return ClassifyKind(instruction.Key, argIndex, aoiWrites);

        return ClassifyKind(
            instruction.Key, instruction.Arguments.Select(a => a.ToString()).ToList(), argIndex, aoiWrites);
    }

    /// <summary>
    /// Classifies one operand from the instruction's mnemonic and operand texts (no L5Sharp
    /// <see cref="Instruction"/> needed), with the same <c>JSR</c> rule as
    /// <see cref="ClassifyKind(Instruction,int,IReadOnlyDictionary{string,IReadOnlySet{int}}?)"/>:
    /// every argument at or beyond <c>2 + InputCount</c> is a return parameter and is written.
    /// The origin trace classifies through this overload so it agrees with the gate by construction.
    /// </summary>
    /// <param name="instructionKey">Instruction mnemonic.</param>
    /// <param name="args">Operand texts in order.</param>
    /// <param name="argIndex">Zero-based index of the operand to classify.</param>
    /// <param name="aoiWrites">AOI write map from <see cref="BuildAoiWriteMap"/>, or <c>null</c>.</param>
    /// <returns><c>"read"</c>, <c>"write"</c>, or <c>"unknown"</c>.</returns>
    public static string ClassifyKind(
        string instructionKey,
        IReadOnlyList<string> args,
        int argIndex,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites)
    {
        if (!string.Equals(instructionKey, JsrKey, StringComparison.OrdinalIgnoreCase))
            return ClassifyKind(instructionKey, argIndex, aoiWrites);

        // args[0] = routine name, args[1] = input count. Anything shorter is
        // malformed; classify as unknown so the caller fails closed.
        if (args.Count < 2 ||
            !int.TryParse(args[1].Trim(), out var inputCount) ||
            inputCount < 0)
            return "unknown";

        if (argIndex <= 1) return "read";

        return argIndex >= 2 + inputCount ? "write" : "read";
    }

    /// <summary>
    /// <c>true</c> when <paramref name="instructionKey"/> is a known instruction that only reads its
    /// operands (a contact or a comparison), so it can gate the instructions after it on a rung.
    /// </summary>
    /// <param name="instructionKey">Instruction mnemonic.</param>
    public static bool IsReadOnlyInstruction(string instructionKey) =>
        KindMap.TryGetValue(InstructionMnemonics.Canonical(instructionKey), out var writeIndices) && writeIndices is null;

    /// <summary>
    /// Builds a map of Add-On Instruction name to the set of call-argument indices
    /// that the instruction writes.
    ///
    /// <para>
    /// Argument 0 of an AOI call is the instance backing tag, which the instruction
    /// always writes (its internal state).  Arguments 1..N map positionally to the
    /// definition's <em>required</em> parameters excluding <c>EnableIn</c>/
    /// <c>EnableOut</c>; a parameter with usage <c>Output</c> or <c>InOut</c> is
    /// written by the instruction.
    /// </para>
    /// </summary>
    /// <param name="project">Parsed L5X project supplying the AOI definitions.</param>
    /// <returns>
    /// Case-insensitive map keyed by AOI name.  Empty when the project defines no
    /// Add-On Instructions.
    /// </returns>
    public static IReadOnlyDictionary<string, IReadOnlySet<int>> BuildAoiWriteMap(L5X project) =>
        BuildAoiWriteMap(project, null);

    /// <summary>
    /// <see cref="BuildAoiWriteMap(L5X)"/> that records an AOI it cannot read as unparsed content
    /// and leaves it out of the map as <see cref="UnreadableAoi"/>, so every call to it
    /// classifies as <c>"unknown"</c>.
    /// </summary>
    /// <param name="project">Parsed L5X project supplying the AOI definitions.</param>
    /// <param name="unparsed">Receives an entry per unreadable AOI, or <c>null</c> to discard them.</param>
    internal static IReadOnlyDictionary<string, IReadOnlySet<int>> BuildAoiWriteMap(
        L5X project, List<UnparsedContent>? unparsed)
    {
        var map = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase);

        List<AddOnInstruction> aois;
        try { aois = project.AddOnInstructions.ToList(); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            unparsed?.Add(UnparsedContent.Create("project (add-on instructions)", null,
                $"AOI definitions could not be read: {FirstLine(ex)}"));
            aois = new List<AddOnInstruction>();
            // Names are recovered from the raw XML below; every call to them is unknown.
            foreach (var el in project.Content.Serialize().Descendants("AddOnInstructionDefinition"))
                if (el.Attribute("Name")?.Value is { Length: > 0 } n) map[n] = UnreadableAoi;
        }

        foreach (var aoi in aois)
        {
            try
            {
                AddAoiEntry(map, aoi);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                var name = SafeName(() => aoi.Name.ToString());
                unparsed?.Add(UnparsedContent.Create($"AddOnInstruction {name}", null,
                    $"definition could not be read: {FirstLine(ex)}"));
                if (name != "?" && name.Length > 0) map[name] = UnreadableAoi;
            }
        }

        // Encoded AOI definitions replace the element in a real export; name them from the raw
        // XML too, in case the object model does not list them.
        foreach (var encoded in project.Content.Serialize().DescendantsAndSelf("EncodedData"))
        {
            var name = encoded.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name) &&
                string.Equals(encoded.Attribute("EncodedType")?.Value, "AddOnInstructionDefinition",
                    StringComparison.OrdinalIgnoreCase))
                map[name] = UnreadableAoi;
        }

        return map;
    }

    /// <summary>Adds the write-operand positions of one AOI definition to <paramref name="map"/>.</summary>
    private static void AddAoiEntry(Dictionary<string, IReadOnlySet<int>> map, AddOnInstruction aoi)
    {
        // L5Sharp lists an <EncodedData EncodedType="AddOnInstructionDefinition"> element
        // (the real-export replacement for a protected AOI) as an AOI with no parameters,
        // which would classify the instance as a write and every other operand as a read.
        // Leave it out of the map so every call to it classifies as "unknown".
        var aoiXml = aoi.Serialize();
        if (aoiXml.Name.LocalName != "AddOnInstructionDefinition" ||
            aoiXml.DescendantsAndSelf("EncodedData").Any())
        {
            if (!string.IsNullOrEmpty(aoi.Name)) map[aoi.Name] = UnreadableAoi;
            return;
        }

        // Instance backing tag is always written.
        var writes = new HashSet<int> { 0 };

        var positional = aoi.Parameters
            .Where(p => !string.Equals(p.Name, "EnableIn",  StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(p.Name, "EnableOut", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Required == true)
            .ToList();

        for (int i = 0; i < positional.Count; i++)
        {
            var usage = positional[i].Usage;
            if (usage == TagUsage.Output || usage == TagUsage.InOut)
                writes.Add(i + 1);   // +1 for the instance argument at index 0
        }

        map[aoi.Name] = writes;
    }

    /// <summary>
    /// L5Sharp has no deserializer for a bare <c>&lt;EquipmentPhase&gt;</c> element and throws
    /// when <c>Programs</c> is enumerated. An equipment phase carries the same tags and routines
    /// as a program, so when the project contains one, return a copy in which each is renamed to
    /// <c>&lt;Program Type="EquipmentPhase"&gt;</c> and scanned like any other program. Returns
    /// <paramref name="project"/> unchanged when it has none.
    /// </summary>
    private static L5X NormalizeEquipmentPhases(L5X project)
    {
        var content = project.Content.Serialize();
        var phases = content.Descendants("EquipmentPhase").ToList();
        if (phases.Count == 0) return project;

        foreach (var phase in phases)
        {
            phase.Name = "Program";
            if (phase.Attribute("Type") is null) phase.SetAttributeValue("Type", "EquipmentPhase");
        }
        return SafeXml.ParseL5x(content.ToString());
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="routine"/> is a ladder (RLL)
    /// routine and its <c>Rungs</c> collection can be safely enumerated.
    ///
    /// <para>
    /// L5Sharp's <c>Routine.Rungs</c> does <strong>not</strong> return an empty
    /// sequence for non-RLL routines — enumerating it on an FBD routine throws
    /// <c>InvalidCastException: Unable to cast L5Sharp.Core.Sheet to
    /// L5Sharp.Core.Rung</c> (and the SFC/ST equivalents).  Every enumeration of
    /// <c>Rungs</c> must be guarded by this check.  Measured 2026-07-31 against
    /// L5Sharp's own Test.L5X, which crashed logix_gate_report,
    /// logix_cross_reference and logix_verify_interlock.
    /// </para>
    /// </summary>
    public static bool IsLadder(Routine routine) =>
        IsRealRoutineElement(routine.Serialize()) && routine.Type == RoutineType.RLL;

    /// <summary>
    /// L5Sharp surfaces every child of <c>&lt;Routines&gt;</c> as a <see cref="Routine"/>,
    /// including the <c>&lt;EncodedData&gt;</c> element that replaces a protected routine in a
    /// real export; reading <c>Routine.Type</c> on it throws. This is <c>true</c> only for a
    /// genuine <c>&lt;Routine&gt;</c> element that carries a <c>Type</c> attribute.
    /// </summary>
    private static bool IsRealRoutineElement(XElement xml) =>
        xml.Name.LocalName == "Routine" && xml.Attribute("Type") is not null;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="arg"/> refers to <paramref name="tagName"/>
    /// exactly (case-insensitive) or as a member-path prefix
    /// (e.g. argument <c>Cmd_Motor.Enable</c> matches base tag <c>Cmd_Motor</c>).
    /// </summary>
    public static bool ArgumentMatchesTag(Argument arg, string tagName)
    {
        return ArgumentMatchesTag(arg.ToString(), tagName);
    }

    /// <summary>
    /// String form of <see cref="ArgumentMatchesTag(Argument, string)"/> for operands taken from
    /// the parsed rung structure: exact match, or a member/array-element of <paramref name="tagName"/>.
    /// </summary>
    /// <param name="value">Operand text, e.g. <c>Tag.Member</c>.</param>
    /// <param name="tagName">Tag to match (case-insensitive).</param>
    public static bool ArgumentMatchesTag(string? value, string tagName)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // Exact match
        if (string.Equals(value, tagName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Member-access prefix: "TagName.Member" or "TagName[0].Member"
        return value.StartsWith(tagName + ".", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(tagName + "[", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the bare tag name from an instruction argument string, stripping
    /// any member-access suffix (<c>.Member</c>) or array-index suffix (<c>[N]</c>).
    /// Returns <c>null</c> for null/empty input.
    /// </summary>
    public static string? ExtractBaseName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        // Strip member access: "Tag.Member" → "Tag"
        int dotIdx = value.IndexOf('.');
        if (dotIdx > 0) value = value[..dotIdx];

        // Strip array index: "Tag[0]" → "Tag"
        int bracketIdx = value.IndexOf('[');
        if (bracketIdx > 0) value = value[..bracketIdx];

        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Like <see cref="ExtractBaseName"/> but understands cross-program references.
    /// <c>\Prog.Tag</c>, <c>\Program:Prog.Tag</c> and <c>Program:Prog.Tag</c> (any member or
    /// index suffix allowed) resolve to the program-scoped key <c>Program:Prog.Tag</c>, so a
    /// write through the reference is applied to that program's tag and not reduced to the
    /// base <c>Prog</c>.  Any other operand reduces exactly as <see cref="ExtractBaseName"/>.
    /// </summary>
    /// <param name="value">Raw operand text.</param>
    /// <returns>The scoped key or bare base name, or <c>null</c> for null/empty input.</returns>
    public static string? ExtractScopedName(string? value)
    {
        if (value is null) return null;
        var text = value.Trim();
        var crossProgram = text.StartsWith('\\');
        if (crossProgram) text = text[1..];
        if (text.StartsWith(ProgramScopePrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[ProgramScopePrefix.Length..];
            crossProgram = true;
        }
        if (!crossProgram) return ExtractBaseName(text);

        var dot = text.IndexOf('.');
        if (dot <= 0) return null;
        var tag = ExtractBaseName(text[(dot + 1)..]);
        return string.IsNullOrEmpty(tag) ? null : $"{ProgramScopePrefix}{text[..dot]}.{tag}";
    }

    /// <summary>
    /// The write classification of one instruction operand together with the tag it designates.
    /// </summary>
    /// <param name="Kind"><c>"read"</c>, <c>"write"</c> or <c>"unknown"</c>.</param>
    /// <param name="Program">
    /// The program that owns the tag when the operand is program-qualified (<c>\P2.Dst</c>);
    /// <c>null</c> for an unqualified operand (scoped by the program that contains the rung).
    /// </param>
    /// <param name="Tag">The base tag name (members and array indexes stripped), or <c>null</c> when none.</param>
    public sealed record OperandClassification(string Kind, string? Program, string? Tag)
    {
        /// <summary>True when the operand writes a tag that belongs to a program-qualified reference.</summary>
        public bool IsCrossProgramWrite => Program is not null && Kind == "write";
    }

    /// <summary>
    /// Classifies operand <paramref name="argIndex"/> of <paramref name="instruction"/> and resolves
    /// the tag it names, so a destination such as <c>\P2.Dst</c> in <c>OTE</c> / <c>MOVE</c> is a write to
    /// <c>P2</c>'s tag <c>Dst</c> rather than to a tag called <c>P2</c>.
    /// </summary>
    /// <param name="instruction">A parsed instruction (from <see cref="RungParser"/>).</param>
    /// <param name="argIndex">Zero-based operand index.</param>
    /// <param name="aoiWrites">AOI write map from <see cref="BuildAoiWriteMap(L5X)"/>, or <c>null</c>.</param>
    /// <returns>The kind plus the owning program (if qualified) and base tag name.</returns>
    public static OperandClassification ClassifyOperand(
        Instruction instruction,
        int argIndex,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites)
    {
        var kind = ClassifyKind(instruction, argIndex, aoiWrites);
        var args = instruction.Arguments.ToList();
        if (argIndex < 0 || argIndex >= args.Count) return new OperandClassification(kind, null, null);

        var scoped = ExtractScopedName(args[argIndex].ToString());
        if (scoped is not null && scoped.StartsWith(ProgramScopePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = scoped[ProgramScopePrefix.Length..];
            var dot  = rest.IndexOf('.');
            return new OperandClassification(kind, rest[..dot], rest[(dot + 1)..]);
        }
        return new OperandClassification(kind, null, scoped);
    }

    /// <summary>Prefix of a program-scoped tag key (<c>Program:Prog.Tag</c>).</summary>
    private const string ProgramScopePrefix = "Program:";

    /// <summary>
    /// Scans every RLL rung in every program and routine of the supplied project and
    /// collects each base tag name that appears as a write operand.
    /// </summary>
    /// <param name="project">Parsed L5X project to scan.</param>
    /// <returns>
    /// A dictionary mapping each written base tag name (case-insensitive key) to a
    /// human-readable write-location string of the form
    /// <c>Program/Routine[RungNumber] via INSTRUCTION</c> for use as provenance.
    /// When a tag is written in multiple locations, the first occurrence is recorded.
    /// </returns>
    public static Dictionary<string, string> CollectProgramWrittenTags(L5X project) =>
        ScanRungs(project).ProgramWritten;

    /// <summary>
    /// Result of a single pass over every ladder rung in a project.
    /// </summary>
    /// <param name="ProgramWritten">
    /// Base tag name → <c>Program/Routine[Rung] via INSTRUCTION</c> for tags that
    /// appear as a <em>proven</em> write operand.
    /// </param>
    /// <param name="UnknownContext">
    /// Base tag name → <c>Program/Routine[Rung] via INSTRUCTION</c> for tags that
    /// appear as an operand of an instruction the classifier does not know.  The
    /// operand may or may not be a write — that is precisely the point: the gate
    /// engine cannot prove the tag is operator-writable, so it must fail closed.
    /// </param>
    /// <param name="Unparsed">
    /// Rungs and elements the scan could not parse (each already made unknown write context for
    /// the tags that depend on it). Empty when everything parsed.
    /// </param>
    public record RungScan(
        Dictionary<string, string> ProgramWritten,
        Dictionary<string, string> UnknownContext,
        IReadOnlyList<UnparsedContent>? Unparsed = null)
    {
        /// <summary>The unparsed content; never <c>null</c>.</summary>
        public IReadOnlyList<UnparsedContent> Unparsed { get; init; } =
            Unparsed ?? Array.Empty<UnparsedContent>();
    }

    /// <summary>
    /// Scans every <c>&lt;Rung&gt;</c> element in an L5X fragment XML string and
    /// collects each base tag name that appears as a write operand, using the same
    /// classification logic as <see cref="ScanRungs"/> but operating on raw XML
    /// rather than a fully loaded <see cref="L5X"/> project.
    ///
    /// <para>
    /// The fragment does not need to be a complete L5X document — any XML string
    /// that contains <c>&lt;Rung&gt;</c> elements with a nested <c>&lt;Text&gt;</c>
    /// child is accepted.  Add-On Instruction definitions are not available in a
    /// fragment, so any AOI call in the fragment is classified as
    /// <c>"unknown"</c> (fail-closed).
    /// </para>
    ///
    /// <para>
    /// The location strings in the returned scan have the form
    /// <c>fragment[N] via INSTRUCTION</c>, where <c>N</c> is the value of the
    /// rung's <c>Number</c> attribute (or <c>?</c> when absent).
    /// </para>
    /// </summary>
    /// <param name="fragmentXml">
    /// An L5X XML string, possibly partial, containing one or more
    /// <c>&lt;Rung&gt;</c> elements with rung neutral text in <c>&lt;Text&gt;</c>
    /// child elements.
    /// </param>
    /// <returns>
    /// A <see cref="RungScan"/> with write operands discovered in the fragment.
    /// </returns>
    /// <exception cref="System.Xml.XmlException">
    /// Thrown when <paramref name="fragmentXml"/> is not well-formed XML.
    /// </exception>
    public static RungScan ScanFragment(string fragmentXml, string? routineXPath = null)
    {
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unparsed = new List<UnparsedContent>();

        // SafeXml.ParseDocument tolerates partial fragments (no SchemaRevision required,
        // no Routine Type attribute required).  No AOI map → null → AOI calls are
        // "unknown" (fail-closed, matching how GateEngine handles them).
        var doc = SafeXml.ParseDocument(fragmentXml);

        foreach (var rungEl in RoutineRungSelector.Select(doc, routineXPath))
        {
            var textEl = rungEl.Element("Text");
            if (textEl is null) continue;

            var rungNumber = rungEl.Attribute("Number")?.Value ?? "?";
            // Same tolerant parse as ScanRungs. A rung neither can parse is recorded as
            // unparsed content (never thrown): its operands cannot be classified.
            var parsed = RungParser.TryParse(textEl.Value);
            if (parsed is null)
            {
                unparsed.Add(UnparsedContent.Create($"fragment[{rungNumber}]", textEl.Value,
                    "rung text could not be tokenised; its write operands are unknown"));
                continue;
            }

            foreach (var instruction in parsed)
            {
                var args = instruction.Arguments.ToList();
                for (int i = 0; i < args.Count; i++)
                {
                    var kind = ClassifyKind(instruction, i, null);
                    if (kind == "read") continue;

                    var baseName = ExtractScopedName(args[i].ToString());
                    if (string.IsNullOrEmpty(baseName)) continue;

                    var location = $"fragment[{rungNumber}] via {instruction.Key}";
                    var target   = kind == "write" ? written : unknown;
                    if (!target.ContainsKey(baseName))
                        target[baseName] = location;
                }
            }
        }

        return new RungScan(written, unknown, unparsed);
    }

    /// <summary>
    /// Scans every ladder rung in every program and routine of the supplied project
    /// once, collecting both proven write operands and operands of unclassifiable
    /// instructions.
    ///
    /// <para>
    /// Non-RLL routines are never enumerated through <c>Routine.Rungs</c> (see
    /// <see cref="IsLadder"/>) but they are not skipped either:
    /// <list type="bullet">
    ///   <item><description><b>ST</b> — the routine's <c>&lt;Line&gt;</c> text is joined so a
    ///   statement runs to its <c>;</c>, then scanned for assignment targets (<c>name :=</c>),
    ///   destination/structure operands of instruction calls (<c>COP</c>, <c>CPS</c>, <c>FLL</c>,
    ///   <c>TONR</c>, <c>RES</c>, …) and outputs of project AOI calls; each base name is
    ///   program-written with provenance <c>Program/Routine (ST line N)</c> (plus
    ///   <c>via INSTRUCTION</c> for calls).  An unknown function or AOI call makes every
    ///   identifier argument unknown write context.</description></item>
    ///   <item><description><b>FBD / SFC</b> — not parsed.  Every identifier appearing
    ///   anywhere in the routine XML is recorded as unknown write context (fail closed), with
    ///   provenance naming the routine and its type.</description></item>
    ///   <item><description><b>Encoded or protected routines</b> (an <c>EncodedData</c> element,
    ///   or no readable <c>STContent</c>/<c>RLLContent</c>/…) — every command-prefixed tag in
    ///   the program is unknown write context, provenance naming the routine.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static RungScan ScanRungs(L5X project) => ScanRungs(project, null);

    /// <summary>
    /// <see cref="ScanRungs(L5X)"/> with explicit command-tag prefixes.  The prefixes
    /// (default <c>Cmd_</c>/<c>Sim_</c>) decide which tags an encoded or protected routine
    /// makes <c>UnknownWriteContext</c>.
    /// </summary>
    /// <param name="project">Parsed L5X project to scan.</param>
    /// <param name="commandPrefixes">Command-tag prefixes, or <c>null</c> for the gate defaults.</param>
    public static RungScan ScanRungs(L5X project, IReadOnlyList<string>? commandPrefixes)
    {
        commandPrefixes ??= GateConfigResolver.Current.CommandPrefixes;
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unparsed = new List<UnparsedContent>();

        // Every L5Sharp call below can throw on content the library cannot parse (a rung it cannot
        // tokenise, an element it cannot deserialise, or whole-project indexing). None may escape:
        // each failure is recorded as UnparsedContent and the tags that depend on it fail closed.
        try { project = NormalizeEquipmentPhases(project); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            unparsed.Add(UnparsedContent.Create("project (equipment phases)", null,
                $"could not be normalised: {FirstLine(ex)}"));
            MarkControllerCommandTagsSafe(project, commandPrefixes, unknown,
                "project (equipment phases not readable)");
        }

        var aoiWrites = BuildAoiWriteMap(project, unparsed);

        // Encoded content anywhere in the project (any depth, any EncodedType) fails closed
        // before any routine is read. Runs first so its provenance names the element.
        Guarded(unparsed, "project (encoded content)", null, () =>
            MarkEncodedElements(project, commandPrefixes, unknown),
            () => MarkControllerCommandTagsSafe(project, commandPrefixes, unknown,
                "project (encoded content scan failed)"));

        List<L5Sharp.Core.Program> programs;
        try { programs = project.Programs.ToList(); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            unparsed.Add(UnparsedContent.Create("project (programs)", null,
                $"programs could not be read: {FirstLine(ex)}"));
            MarkControllerCommandTagsSafe(project, commandPrefixes, unknown,
                "project (programs not readable)");
            programs = new List<L5Sharp.Core.Program>();
        }

        foreach (var program in programs)
        {
            var programName = SafeName(() => program.Name.ToString());
            try
            {
                foreach (var routine in program.Routines)
                {
                    ScanRoutineGuarded(project, program, programName, routine, commandPrefixes,
                        aoiWrites, written, unknown, unparsed);
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                // The routine collection itself could not be enumerated.
                var where = $"{programName} (routines)";
                unparsed.Add(UnparsedContent.Create(where, null,
                    $"routines could not be read: {FirstLine(ex)}"));
                MarkProgramCommandTagsSafe(project, program, commandPrefixes, unknown,
                    $"{where} (not readable)");
            }
        }

        Guarded(unparsed, "project (program parameters)", null, () =>
            ScanProgramParameters(project, commandPrefixes, written, unknown),
            () => MarkControllerCommandTagsSafe(project, commandPrefixes, unknown,
                "project (program parameters not readable)"));
        Guarded(unparsed, "project (MSG parameters)", null, () =>
            ScanMessageDestinations(project, written, unknown),
            () => MarkControllerCommandTagsSafe(project, commandPrefixes, unknown,
                "project (MSG parameters not readable)"));

        return new RungScan(written, unknown, unparsed);
    }

    /// <summary>
    /// Scans one routine; a failure that escapes the per-rung guards is recorded as unparsed
    /// content for the routine and fails closed for the program's command tags.
    /// </summary>
    private static void ScanRoutineGuarded(
        L5X project,
        L5Sharp.Core.Program program,
        string programName,
        Routine routine,
        IReadOnlyList<string> commandPrefixes,
        IReadOnlyDictionary<string, IReadOnlySet<int>> aoiWrites,
        Dictionary<string, string> written,
        Dictionary<string, string> unknown,
        List<UnparsedContent> unparsed)
    {
        var routineName = SafeName(() => routine.Name.ToString());
        try
        {
            // Encoded / protected content cannot be read: fail closed for every
            // command tag the routine could reach, and do not enumerate its rungs.
            if (HasUnreadableContent(routine))
            {
                MarkEncodedRoutine(project, program, routine, commandPrefixes, unknown);
                return;
            }

            if (!IsLadder(routine))
            {
                ScanNonLadderRoutine(programName, routine, aoiWrites, written, unknown);
                return;
            }

            foreach (var rung in routine.Rungs)
                ScanRungGuarded(project, program, programName, routineName, rung, commandPrefixes,
                    aoiWrites, written, unknown, unparsed);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            var where = $"{programName}/{routineName}";
            unparsed.Add(UnparsedContent.Create(where, SafeXmlText(routine),
                $"routine could not be read: {FirstLine(ex)}"));
            MarkProgramCommandTagsSafe(project, program, commandPrefixes, unknown,
                $"{where} (routine not readable)");
        }
    }

    /// <summary>
    /// Scans one rung. Text the tokenizer rejects, or any other failure reading the rung, is
    /// recorded as unparsed content and fails closed for every command tag of the program.
    /// </summary>
    private static void ScanRungGuarded(
        L5X project,
        L5Sharp.Core.Program program,
        string programName,
        string routineName,
        Rung rung,
        IReadOnlyList<string> commandPrefixes,
        IReadOnlyDictionary<string, IReadOnlySet<int>> aoiWrites,
        Dictionary<string, string> written,
        Dictionary<string, string> unknown,
        List<UnparsedContent> unparsed)
    {
        var number = SafeName(() => rung.Number.ToString());
        var where = $"{programName}/{routineName}[{number}]";
        try
        {
            var instructions = RungParser.TryParse(rung, out var parseError);
            if (instructions is null)
            {
                // Rung text this build cannot tokenise: any command tag of the program
                // might be driven there. Fail closed rather than abort the whole scan.
                unparsed.Add(UnparsedContent.Create(where, SafeName(() => rung.Text.ToString()),
                    "rung text could not be tokenised; its write operands are unknown",
                    new UnparsedRungEntry(programName, routineName, RungNumber(number),
                        parseError ?? "rung text could not be tokenised")));
                MarkProgramCommandTagsSafe(project, program, commandPrefixes, unknown,
                    $"{where} (rung text not parseable)");
                return;
            }

            // Classify into scratch maps first so a rung that fails half way records nothing partial.
            var rungWritten = new List<(string Name, string Location)>();
            var rungUnknown = new List<(string Name, string Location)>();
            foreach (var instruction in instructions)
            {
                var args = instruction.Arguments.ToList();
                for (int i = 0; i < args.Count; i++)
                {
                    var kind = ClassifyKind(instruction, i, aoiWrites);
                    if (kind == "read") continue;

                    var baseName = ExtractScopedName(args[i].ToString());
                    if (string.IsNullOrEmpty(baseName)) continue;

                    var entry = (baseName, $"{where} via {instruction.Key}");
                    (kind == "write" ? rungWritten : rungUnknown).Add(entry);
                }
            }

            // Record the first occurrence only.
            foreach (var (name, location) in rungWritten) written.TryAdd(name, location);
            foreach (var (name, location) in rungUnknown) unknown.TryAdd(name, location);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            unparsed.Add(UnparsedContent.Create(where, SafeName(() => rung.Text.ToString()),
                $"rung could not be read: {FirstLine(ex)}",
                new UnparsedRungEntry(programName, routineName, RungNumber(number), FirstLine(ex))));
            MarkProgramCommandTagsSafe(project, program, commandPrefixes, unknown,
                $"{where} (rung not readable)");
        }
    }

    /// <summary>The rung number read by <see cref="SafeName"/>, or <c>-1</c> when it could not be read.</summary>
    private static int RungNumber(string number) => int.TryParse(number, out var n) ? n : -1;

    /// <summary>Whether a failure may be absorbed as "content could not be parsed".</summary>
    private static bool IsRecoverable(Exception ex) =>
        ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    /// <summary>First line of an exception message, prefixed by the exception type name.</summary>
    private static string FirstLine(Exception ex)
    {
        var message = (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim();
        return $"{ex.GetType().Name}: {message}";
    }

    /// <summary>Evaluates <paramref name="read"/>, returning <c>"?"</c> when it throws.</summary>
    private static string SafeName(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) when (IsRecoverable(ex)) { return "?"; }
    }

    /// <summary>The serialized XML of a routine, or <c>null</c> when it cannot be produced.</summary>
    private static string? SafeXmlText(Routine routine)
    {
        try { return routine.Serialize().ToString(); }
        catch (Exception ex) when (IsRecoverable(ex)) { return null; }
    }

    /// <summary>
    /// Runs <paramref name="action"/>; when it throws, records unparsed content and runs
    /// <paramref name="failClosed"/> (which marks the tags that depend on it).
    /// </summary>
    private static void Guarded(
        List<UnparsedContent> unparsed, string location, string? text, Action action, Action failClosed)
    {
        try { action(); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            unparsed.Add(UnparsedContent.Create(location, text, $"could not be read: {FirstLine(ex)}"));
            failClosed();
        }
    }

    /// <summary><see cref="MarkControllerCommandTags"/> that never throws (best effort on already-failing content).</summary>
    private static void MarkControllerCommandTagsSafe(
        L5X project, IReadOnlyList<string> commandPrefixes, Dictionary<string, string> unknown, string provenance)
    {
        try { MarkControllerCommandTags(project, commandPrefixes, unknown, provenance); }
        catch (Exception ex) when (IsRecoverable(ex)) { }
    }

    /// <summary><see cref="MarkProgramCommandTags"/> that falls back to the controller tags when the program's own cannot be read.</summary>
    private static void MarkProgramCommandTagsSafe(
        L5X project, L5Sharp.Core.Program program, IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> unknown, string provenance)
    {
        try { MarkProgramCommandTags(project, program, commandPrefixes, unknown, provenance); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            MarkControllerCommandTagsSafe(project, commandPrefixes, unknown, provenance);
        }
    }

    /// <summary>Forwards to <see cref="RungParser.SanitizeEnumeratedOperands"/> (kept for existing callers).</summary>
    /// <param name="text">Rung neutral text.</param>
    /// <returns>The rewritten text, or <paramref name="text"/> when nothing needed rewriting.</returns>
    internal static string SanitizeEnumeratedOperands(string text) =>
        RungParser.SanitizeEnumeratedOperands(text);

    // ── MSG destinations ──────────────────────────────────────────────────────

    /// <summary>MessageParameters attributes a real Studio 5000 export carries (see <c>fixtures/real-shape/MsgRead.L5X</c> and <c>MsgCipGeneric.L5X</c>).</summary>
    private static readonly HashSet<string> MessageAttributes = new(StringComparer.Ordinal)
    {
        "MessageType", "RemoteElement", "RequestedLength", "ConnectedFlag", "ConnectionPath",
        "CommTypeCode", "LocalIndex", "LocalElement", "CacheConnections",
        "DestinationTag", "ServiceCode", "ObjectType", "TargetObject", "AttributeNumber", "LargePacketUsage",
    };

    /// <summary>Fragments that make an unrecognised MessageParameters attribute name destination-like.</summary>
    private static readonly string[] DestinationLikeFragments = ["Dest", "Dst", "Local", "Target", "Sink"];

    /// <summary>How a MSG's tag-valued attributes are used, decided by its <c>MessageType</c>.</summary>
    private enum MsgTypeKind { Read, Write, CipGeneric, Unknown }

    /// <summary>
    /// Classifies a <c>MessageType</c>. A read type (any containing "Read": CIP Data Table Read,
    /// PLC5/SLC Typed Read, PLC2 Unprotected Read, Block-Transfer Read) delivers remote data into
    /// <c>LocalElement</c>; a write type (containing "Write") only reads it; <c>CIP Generic</c>
    /// uses <c>LocalElement</c> as the request source. A missing, unrecognised or ambiguous type
    /// is <see cref="MsgTypeKind.Unknown"/>.
    /// </summary>
    private static MsgTypeKind ClassifyMessageType(string? type, out string reason)
    {
        reason = "";
        type = type?.Trim();
        if (string.IsNullOrEmpty(type))
        {
            reason = "no MessageType";
            return MsgTypeKind.Unknown;
        }
        if (type.Equals("CIP Generic", StringComparison.OrdinalIgnoreCase)) return MsgTypeKind.CipGeneric;
        var isRead  = type.Contains("Read",  StringComparison.OrdinalIgnoreCase);
        var isWrite = type.Contains("Write", StringComparison.OrdinalIgnoreCase);
        if (isRead == isWrite)
        {
            reason = $"unrecognised MessageType '{type}'";
            return MsgTypeKind.Unknown;
        }
        return isRead ? MsgTypeKind.Read : MsgTypeKind.Write;
    }

    /// <summary>True when the attribute name marks it as holding a tag (<c>*Tag</c> or <c>*Element</c>).</summary>
    private static bool IsTagValuedAttribute(string name) =>
        name.EndsWith("Tag", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Element", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides, per attribute, the role of every tag-valued attribute of one
    /// <c>&lt;MessageParameters&gt;</c> element. <c>DestinationTag</c> is program-written for every
    /// MessageType (the reply lands there). <c>LocalElement</c> is program-written for read types,
    /// a source (no mark) for write types and CIP Generic. For an unknown MessageType every
    /// tag-valued attribute (<c>DestinationTag</c>, <c>LocalElement</c>, any name ending in
    /// <c>Tag</c> or <c>Element</c>) is unknown-write-context. For a known type, an unrecognised
    /// destination-like attribute makes <c>LocalElement</c> unknown, and an unrecognised
    /// attribute ending in <c>Tag</c> or <c>Element</c> is unknown itself.
    /// </summary>
    internal static IEnumerable<(string Attribute, string Value, bool Written, string Reason)> ClassifyMsgAttributes(XElement mp)
    {
        var kind = ClassifyMessageType(mp.Attribute("MessageType")?.Value, out var typeReason);
        string? unrecognised = null;
        foreach (var attr in mp.Attributes())
        {
            var name = attr.Name.LocalName;
            if (MessageAttributes.Contains(name)) continue;
            if (DestinationLikeFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                unrecognised ??= $"unrecognised destination-like attribute '{name}'";
            }
        }

        foreach (var attr in mp.Attributes())
        {
            var name = attr.Name.LocalName;
            if (string.IsNullOrWhiteSpace(attr.Value)) continue;
            var recognised = MessageAttributes.Contains(name);
            if (!recognised && !IsTagValuedAttribute(name)) continue;

            if (kind == MsgTypeKind.Unknown)
            {
                if (name == "MessageType" || !IsTagValuedAttribute(name)) continue;
                yield return (name, attr.Value, false, typeReason);
            }
            else if (name == "DestinationTag")
            {
                yield return (name, attr.Value, true, "");
            }
            else if (name == "LocalElement")
            {
                if (unrecognised is not null) yield return (name, attr.Value, false, unrecognised);
                else if (kind == MsgTypeKind.Read) yield return (name, attr.Value, true, "");
            }
            else if (!recognised)
            {
                yield return (name, attr.Value, false, $"unrecognised tag-valued attribute '{name}'");
            }
        }
    }

    /// <summary>
    /// Reads the tag-valued attributes of every MESSAGE tag's <c>&lt;MessageParameters&gt;</c>
    /// (see <see cref="ClassifyMsgAttributes"/> for the per-attribute rule): program-written
    /// ones go to <paramref name="written"/>, unknown-context ones to <paramref name="unknown"/>.
    /// The base tag resolves in the MSG tag's scope: a program-scoped MSG whose program declares
    /// that tag marks the program-scoped key, otherwise the controller (bare) name.
    /// Cross-program references keep their <c>Program:Prog.Tag</c> key.
    /// </summary>
    /// <param name="project">Parsed L5X project.</param>
    /// <param name="written">Map to add program-written base names to (first occurrence wins).</param>
    /// <param name="unknown">Map to add unknown-write-context base names to (first occurrence wins).</param>
    private static void ScanMessageDestinations(
        L5X project, Dictionary<string, string> written, Dictionary<string, string> unknown)
    {
        void Scan(Tag tag, string scope, string? program, HashSet<string> programTags)
        {
            foreach (var mp in tag.Serialize().Descendants("MessageParameters"))
            {
                foreach (var (attribute, rawValue, isWritten, reason) in ClassifyMsgAttributes(mp))
                {
                    var value = rawValue.Trim();
                    var key = ExtractScopedName(value);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (program is not null && !key.StartsWith(ProgramScopePrefix, StringComparison.OrdinalIgnoreCase)
                        && programTags.Contains(key))
                        key = $"{ProgramScopePrefix}{program}.{key}";

                    var target = isWritten ? written : unknown;
                    if (target.ContainsKey(key)) continue;
                    var detail = isWritten
                        ? $"MSG MessageParameters {attribute} '{value.Trim()}'"
                        : $"MSG MessageParameters {attribute} '{value.Trim()}', {reason}";
                    target[key] = $"{scope}{tag.Name} ({detail})";
                }
            }
        }

        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in project.Tags) Scan(tag, "controller tag ", null, none);
        foreach (var program in project.Programs)
        {
            var names = new HashSet<string>(program.Tags.Select(t => t.Name.ToString()),
                StringComparer.OrdinalIgnoreCase);
            foreach (var tag in program.Tags) Scan(tag, $"{program.Name}/", program.Name.ToString(), names);
        }
    }

    // ── Program parameters and connections ────────────────────────────────────

    private static readonly HashSet<string> ConnectionAttributes = new(StringComparer.Ordinal)
    {
        "EndPoint1", "EndPoint2",
    };

    /// <summary>
    /// Models program parameters and parameter connections, failing closed. A real Studio 5000
    /// export declares a program parameter as a <c>&lt;Tag Usage="..."&gt;</c> inside the
    /// program's <c>&lt;Tags&gt;</c> (see <c>fixtures/real-shape/UsageParameters.L5X</c>); a tag
    /// with no <c>Usage</c> is an ordinary local tag.
    /// <list type="bullet">
    ///   <item><description><c>Output</c> / <c>InOut</c>: program-written, key
    ///   <c>Program:Prog.Tag</c>, provenance naming the program and parameter.</description></item>
    ///   <item><description><c>Input</c> / <c>Public</c>: program-written when it is an endpoint of
    ///   any controller-level <c>&lt;ParameterConnection&gt;</c> (the connection drives it). A write
    ///   through a <c>\Prog.Tag</c> reference from another program is already recorded by the
    ///   rung scan under the same key.</description></item>
    ///   <item><description>Any other <c>Usage</c> value: <c>UnknownWriteContext</c> for that tag.</description></item>
    ///   <item><description>An unrecognised element or attribute in <c>&lt;ParameterConnections&gt;</c>
    ///   (which names no single program reliably) makes every program's command-prefixed tags
    ///   unknown write context.</description></item>
    /// </list>
    /// The connection shape (<c>EndPoint1</c>/<c>EndPoint2</c>) is not proven by a real export; a
    /// mismatch only over-denies.
    /// </summary>
    private static void ScanProgramParameters(
        L5X project,
        IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> written,
        Dictionary<string, string> unknown)
    {
        var programs = project.Programs.ToList();
        // Scoped key (Program:Prog.Tag) → Usage, for tags that carry a Usage.
        var usages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var program in programs)
        {
            var tagsEl = program.Serialize().Element("Tags");
            if (tagsEl is null) continue;

            foreach (var t in tagsEl.Elements("Tag"))
            {
                var name = t.Attribute("Name")?.Value;
                var usage = t.Attribute("Usage")?.Value;
                if (string.IsNullOrEmpty(name) || usage is null) continue;

                var key = $"{ProgramScopePrefix}{program.Name}.{name}";
                usages[key] = usage;
                if (IsOutputUsage(usage))
                {
                    written.TryAdd(key, $"program '{program.Name}' parameter '{name}' (Usage={usage})");
                }
                else if (!IsInputUsage(usage))
                {
                    unknown.TryAdd(key,
                        $"program '{program.Name}' parameter '{name}' (unrecognised Usage '{usage}')");
                }
            }
        }

        var connections = project.Controller.Serialize().Element("ParameterConnections");
        if (connections is null) return;

        var connectionProblem = CheckConnectionsShape(connections);
        if (connectionProblem is not null)
        {
            foreach (var program in programs)
                MarkProgramCommandTags(project, program, commandPrefixes, unknown,
                    $"{program.Name} (ParameterConnections not recognised: {connectionProblem})");
            if (programs.Count == 0)
                foreach (var tag in project.Tags)
                    if (commandPrefixes.Any(pre => tag.Name.StartsWith(pre, StringComparison.OrdinalIgnoreCase)))
                        unknown.TryAdd(tag.Name,
                            $"controller (ParameterConnections not recognised: {connectionProblem})");
        }

        foreach (var c in connections.Elements("ParameterConnection"))
        {
            var ends = new[] { c.Attribute("EndPoint1")?.Value, c.Attribute("EndPoint2")?.Value };
            if (ends.Any(string.IsNullOrWhiteSpace)) continue;

            var keys = ends.Select(e => ExtractScopedName(e)).ToArray();
            if (keys.Any(string.IsNullOrEmpty)) continue;

            string? why = null;
            for (var i = 0; i < 2 && why is null; i++)
            {
                var key = keys[i]!;
                if (usages.TryGetValue(key, out var usage))
                {
                    if (IsOutputUsage(usage) || IsInputUsage(usage))
                        why = $"'{ends[i]!.Trim()}' is a connected {usage} parameter";
                }
                else if (key.StartsWith(ProgramScopePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    why = $"'{ends[i]!.Trim()}' is not a declared parameter, so its direction cannot be proven";
                }
            }
            if (why is null) continue;

            var provenance =
                $"ParameterConnection '{ends[0]!.Trim()}' <-> '{ends[1]!.Trim()}' ({why})";
            foreach (var key in keys)
                written.TryAdd(key!, provenance);
        }
    }

    private static bool IsOutputUsage(string usage) =>
        usage.Equals("Output", StringComparison.OrdinalIgnoreCase) ||
        usage.Equals("InOut", StringComparison.OrdinalIgnoreCase);

    private static bool IsInputUsage(string usage) =>
        usage.Equals("Input", StringComparison.OrdinalIgnoreCase) ||
        usage.Equals("Public", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a description of the first unrecognised part of a <c>ParameterConnections</c> element, or <c>null</c>.</summary>
    private static string? CheckConnectionsShape(XElement connections)
    {
        if (connections.HasAttributes)
            return $"attribute '{connections.FirstAttribute!.Name.LocalName}' on ParameterConnections";
        foreach (var child in connections.Elements())
        {
            if (child.Name.LocalName != "ParameterConnection")
                return $"element '{child.Name.LocalName}' in ParameterConnections";
            foreach (var a in child.Attributes())
                if (!ConnectionAttributes.Contains(a.Name.LocalName))
                    return $"attribute '{a.Name.LocalName}' on ParameterConnection";
            if (child.HasElements)
                return $"element '{child.Elements().First().Name.LocalName}' in ParameterConnection";
            if (child.Attribute("EndPoint1") is null || child.Attribute("EndPoint2") is null)
                return "ParameterConnection without both endpoints";
        }
        return null;
    }

    // ── Non-ladder routines ───────────────────────────────────────────────────

    /// <summary>Matches a bare identifier (tag name or member name) in XML text.</summary>
    private static readonly System.Text.RegularExpressions.Regex IdentifierPattern =
        new(@"[A-Za-z_][A-Za-z0-9_]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Matches <c>name(</c> — a call to an instruction, function or AOI in ST code.</summary>
    internal static readonly System.Text.RegularExpressions.Regex StCallPattern =
        new(@"(?<![\w.$])([A-Za-z_][A-Za-z0-9_]*)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Matches a leading (non-member) identifier that is not itself called.</summary>
    private static readonly System.Text.RegularExpressions.Regex StOperandPattern =
        new(@"(?<![\w.$])(?:\\(?:Program:)?[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*)(?![\w])(?!\s*\()",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>ST keywords and operators that may be followed by <c>(</c> without being a call.</summary>
    internal static readonly HashSet<string> StKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "ELSIF", "ELSE", "THEN", "WHILE", "DO", "REPEAT", "UNTIL", "CASE", "OF", "FOR", "TO", "BY",
        "NOT", "AND", "OR", "XOR", "RETURN", "EXIT",
    };

    /// <summary>
    /// ST functions that only return a value and never write an operand.  Anything
    /// else that is not a known instruction or project AOI is an unknown call.
    /// Deliberately excludes the string instructions (FIND, MID, UPPER, LOWER, ...), which
    /// write a destination and live in <see cref="KindMap"/>, and names whose Logix
    /// semantics are not documented here (SEL, MUX, LEN, LEFT, RIGHT): those fail closed.
    /// </summary>
    private static readonly HashSet<string> StPureFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SQRT", "MIN", "MAX", "LIMIT", "TRUNC", "ROUND", "EXP", "ASIN", "ACOS", "ATAN2",
        "ABS", "SIZEOF",
    };

    private static readonly System.Text.RegularExpressions.Regex StTypeConversionPattern =
        new(@"^[A-Za-z]+_TO_[A-Za-z]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns <c>true</c> when the routine's logic cannot be read: it carries an
    /// <c>EncodedData</c> element, or lacks the content element for its type
    /// (<c>STContent</c>, <c>RLLContent</c>, <c>FBDContent</c>, <c>SFCContent</c>).
    /// </summary>
    private static bool HasUnreadableContent(Routine routine)
    {
        var xml = routine.Serialize();
        if (xml.DescendantsAndSelf("EncodedData").Any()) return true;
        if (!IsRealRoutineElement(xml)) return true;

        var content = routine.Type == RoutineType.ST  ? "STContent"
                    : routine.Type == RoutineType.RLL ? "RLLContent"
                    : routine.Type == RoutineType.FBD ? "FBDContent"
                    : routine.Type == RoutineType.SFC ? "SFCContent"
                    : null;
        return content is not null && xml.Element(content) is null;
    }

    /// <summary>
    /// Fails closed on every <c>EncodedData</c> element in the project, at any depth and with
    /// any <c>EncodedType</c> (in a real export it replaces the <c>Routine</c> or
    /// <c>AddOnInstructionDefinition</c> element rather than nesting inside one). When the
    /// enclosing <c>Program</c> is known, every command-prefixed tag visible to it is unknown
    /// write context; when it cannot be determined, every controller-scope command-prefixed tag
    /// is. Provenance names the element's <c>Name</c> and <c>EncodedType</c>.
    /// </summary>
    private static void MarkEncodedElements(
        L5X project,
        IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> unknown)
    {
        foreach (var encoded in project.Content.Serialize().DescendantsAndSelf("EncodedData"))
        {
            var encodedType = encoded.Attribute("EncodedType")?.Value;
            var name        = encoded.Attribute("Name")?.Value;

            var programName = encoded.Ancestors("Program").FirstOrDefault()?.Attribute("Name")?.Value;
            var routineName = encoded.Ancestors("Routine").FirstOrDefault()?.Attribute("Name")?.Value;
            var aoiName     = encoded.Ancestors("AddOnInstructionDefinition").FirstOrDefault()?.Attribute("Name")?.Value;
            var label       = name ?? routineName ?? aoiName;

            var description =
                $"EncodedData Name=\"{name ?? "(none)"}\" EncodedType=\"{encodedType ?? "(none)"}\"";

            var program = programName is null
                ? null
                : project.Programs.FirstOrDefault(p =>
                    string.Equals(p.Name, programName, StringComparison.OrdinalIgnoreCase));

            if (program is not null)
            {
                var where = label is null ? programName : $"{programName}/{label}";
                MarkProgramCommandTags(project, program, commandPrefixes, unknown,
                    $"{where} (encoded or protected content, not readable: {description})");
            }
            else
            {
                var where = label is null ? "controller" : $"controller/{label}";
                MarkControllerCommandTags(project, commandPrefixes, unknown,
                    $"{where} (encoded or protected content, enclosing program unknown: {description})");
            }
        }
    }

    /// <summary>
    /// Marks every controller-scope command-prefixed tag as unknown write context.
    /// </summary>
    private static void MarkControllerCommandTags(
        L5X project,
        IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> unknown,
        string provenance)
    {
        foreach (var tag in project.Tags)
        {
            var name = tag.Name;
            if (commandPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                !unknown.ContainsKey(name))
                unknown[name] = provenance;
        }
        MarkUsageParameters(project, commandPrefixes, unknown, provenance);
    }

    /// <summary>
    /// Marks every command-prefixed tag that carries a <c>Usage</c> attribute (a program
    /// parameter) in <em>every</em> program as unknown write context, keyed
    /// <c>Program:Prog.Tag</c>. Unreadable content anywhere (an encoded routine, an unparseable
    /// rung) could drive a parameter of any program through a cross-program reference or a
    /// parameter connection, so no program's parameters are exempt. The first provenance
    /// recorded for a key wins; <paramref name="provenance"/> names the unreadable element.
    /// </summary>
    /// <param name="project">Parsed L5X project.</param>
    /// <param name="commandPrefixes">Command-tag prefixes.</param>
    /// <param name="unknown">Map to add unknown-write-context keys to.</param>
    /// <param name="provenance">Description naming the unreadable element.</param>
    private static void MarkUsageParameters(
        L5X project,
        IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> unknown,
        string provenance)
    {
        foreach (var program in project.Programs)
        {
            var tagsEl = program.Serialize().Element("Tags");
            if (tagsEl is null) continue;

            foreach (var t in tagsEl.Elements("Tag"))
            {
                var name = t.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name) || t.Attribute("Usage") is null) continue;
                var prefixed = commandPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                string? aliasedBase = null;
                if (!prefixed)
                {
                    // A parameter without a command prefix that aliases a command-prefixed base
                    // shares that base's storage, so it is unreadable-content-marked too and the
                    // gate engine passes the mark on to the base.
                    aliasedBase = CommandPrefixedAliasBase(project, tagsEl, t, commandPrefixes);
                    if (aliasedBase is null) continue;
                }

                var via = aliasedBase is null ? "" : $", alias of command-prefixed '{aliasedBase}'";
                unknown.TryAdd($"{ProgramScopePrefix}{program.Name}.{name}",
                    $"{provenance}; program '{program.Name}' parameter '{name}' (Usage={t.Attribute("Usage")!.Value}{via})");
            }
        }
    }

    /// <summary>
    /// Follows the <c>AliasFor</c> chain of a program parameter (program scope first, then
    /// controller scope) and returns the first command-prefixed tag name it reaches, or
    /// <c>null</c> when the tag is not an alias or no tag on the chain is command-prefixed.
    /// An unresolvable target counts by its literal name.
    /// </summary>
    private static string? CommandPrefixedAliasBase(
        L5X project, XElement programTags, XElement tag, IReadOnlyList<string> commandPrefixes)
    {
        bool Prefixed(string n) =>
            commandPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var current = tag;
        var currentScope = programTags;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var hop = 0; hop < 32; hop++)
        {
            if (!string.Equals(current.Attribute("TagType")?.Value, "Alias",
                    StringComparison.OrdinalIgnoreCase)) return null;
            var target = ExtractScopedName(current.Attribute("AliasFor")?.Value);
            if (string.IsNullOrEmpty(target)) return null;

            // Cross-program form: the tag name follows the program name.
            if (target.StartsWith(ProgramScopePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = target[ProgramScopePrefix.Length..];
                var tagName = rest[(rest.IndexOf('.') + 1)..];
                return Prefixed(tagName) ? tagName : null;
            }
            if (Prefixed(target)) return target;
            if (!seen.Add(target)) return null;

            var next = currentScope.Elements("Tag").FirstOrDefault(e => string.Equals(
                               e.Attribute("Name")?.Value, target, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                var controllerTags = project.Controller.Serialize().Element("Tags");
                next = controllerTags?.Elements("Tag").FirstOrDefault(e => string.Equals(
                    e.Attribute("Name")?.Value, target, StringComparison.OrdinalIgnoreCase));
                currentScope = controllerTags!;
            }
            if (next is null) return null;
            current = next;
        }
        return null;
    }

    /// <summary>
    /// Marks every command-prefixed tag visible to <paramref name="program"/> (controller tags
    /// plus the program's own) as unknown write context, naming the unreadable routine.
    /// </summary>
    private static void MarkEncodedRoutine(
        L5X project,
        L5Sharp.Core.Program program,
        Routine routine,
        IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> unknown) =>
        MarkProgramCommandTags(project, program, commandPrefixes, unknown,
            $"{program.Name}/{routine.Name} (encoded or protected routine, content not readable)");

    /// <summary>
    /// Marks every command-prefixed tag visible to <paramref name="program"/> (controller tags
    /// plus the program's own) as unknown write context with the given provenance.
    /// </summary>
    private static void MarkProgramCommandTags(
        L5X project,
        L5Sharp.Core.Program program,
        IReadOnlyList<string> commandPrefixes,
        Dictionary<string, string> unknown,
        string provenance)
    {
        foreach (var tag in project.Tags.Concat(program.Tags))
        {
            var name = tag.Name;
            if (commandPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                !unknown.ContainsKey(name))
                unknown[name] = provenance;
        }
        MarkUsageParameters(project, commandPrefixes, unknown, provenance);
    }

    /// <summary>
    /// Records the write evidence of one readable non-ladder routine into the supplied maps.
    /// ST routines yield proven writes (assignments, instruction destinations, AOI outputs) and
    /// unknown context (unknown calls); FBD and SFC routines yield unknown context.
    /// </summary>
    private static void ScanNonLadderRoutine(
        string programName,
        Routine routine,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites,
        Dictionary<string, string> written,
        Dictionary<string, string> unknown)
    {
        var xml = routine.Serialize();

        if (routine.Type == RoutineType.ST)
        {
            foreach (var f in ScanStructuredTextDetailed(xml, aoiWrites))
            {
                var target = f.Proven ? written : unknown;
                if (target.ContainsKey(f.Name)) continue;

                var where = $"{programName}/{routine.Name} (ST line {f.Line}";
                target[f.Name] = f.Via is null ? (f.Proven ? where + ")"
                                                           : where + ", assignment target is not a parseable tag path)")
                               : f.Proven      ? $"{where} via {f.Via})"
                               :                 $"{where}, call to {f.Via} with unknown write semantics)";
            }
            return;
        }

        // FBD, SFC (and any routine type this build does not know): the content is not
        // parsed, so any identifier anywhere in it might be a driven operand.
        var typeName = routine.Type == RoutineType.FBD ? "FBD"
                     : routine.Type == RoutineType.SFC ? "SFC"
                     : routine.Type.ToString();
        foreach (var name in CollectIdentifiers(xml))
        {
            if (!unknown.ContainsKey(name))
                unknown[name] =
                    $"{programName}/{routine.Name} ({typeName} routine, content not parsed)";
        }
    }

    /// <summary>
    /// Yields every identifier found in attribute values and text nodes of
    /// <paramref name="routineXml"/>.  Element and attribute names are excluded.
    /// </summary>
    private static IEnumerable<string> CollectIdentifiers(XElement routineXml)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = routineXml.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Select(a => a.Value))
            .Concat(routineXml.DescendantNodes().OfType<XText>().Select(t => t.Value));

        foreach (var value in values)
            foreach (System.Text.RegularExpressions.Match m in IdentifierPattern.Matches(value))
                if (seen.Add(m.Value))
                    yield return m.Value;
    }

    /// <summary>One ST write finding: a base tag, the line it was found on, and how.</summary>
    /// <param name="Name">Base tag name.</param>
    /// <param name="Line">The <c>Number</c> of the line where the statement or call begins.</param>
    /// <param name="Proven"><c>true</c> for a proven write, <c>false</c> for unknown write context.</param>
    /// <param name="Via">Instruction or function name, or <c>null</c> for a <c>:=</c> assignment.</param>
    /// <param name="Target">The operand as written (whitespace removed): <c>Source9.AirBlow</c>, <c>Arr[i]</c>.</param>
    /// <param name="Pos">Offset of the target (assignment) or call name (call) in the joined text.</param>
    internal readonly record struct StFinding(
        string Name, string Line, bool Proven, string? Via, string Target = "", int Pos = 0);

    /// <summary>
    /// Scans the <c>&lt;Line&gt;</c> elements of an ST routine for proven write targets
    /// (<c>name :=</c>, <c>name.Member :=</c>, <c>name[i] :=</c>, and the destination operands of
    /// instruction calls such as <c>COP</c>/<c>FLL</c>/<c>TONR</c>) and returns each distinct
    /// base name with the number of the first line that writes it.  Lines are joined first, so a
    /// statement may span several lines up to its <c>;</c>.  Block comments
    /// (<c>(* *)</c>, <c>/* */</c>), line comments (<c>//</c>) and string literals are ignored.
    /// Add-On Instruction calls are not resolved here; see <see cref="ScanRungs(L5X)"/>.
    /// </summary>
    /// <param name="routineXml">Serialized <c>&lt;Routine&gt;</c> element.</param>
    /// <returns>Base tag name and the <c>Number</c> of the line holding the write.</returns>
    public static IEnumerable<(string Name, string Line)> ScanStructuredText(XElement routineXml)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ScanStructuredTextDetailed(routineXml, null))
            if (f.Proven && seen.Add(f.Name))
                yield return (f.Name, f.Line);
    }

    /// <summary>
    /// Full ST scan: proven writes and unknown-context operands, in source order.
    /// </summary>
    private static List<StFinding> ScanStructuredTextDetailed(
        XElement routineXml,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites) =>
        ScanStructuredTextDetailed(routineXml, aoiWrites, out _);

    /// <summary>
    /// <see cref="ScanStructuredTextDetailed(XElement,IReadOnlyDictionary{string,IReadOnlySet{int}}?)"/>
    /// that also returns the joined, comment-free text the <see cref="StFinding.Pos"/> offsets index into.
    /// </summary>
    internal static List<StFinding> ScanStructuredTextDetailed(
        XElement routineXml,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites,
        out string joinedText)
    {
        // Join every line into one comment-free string so a statement can span lines;
        // lineAt maps each character back to its source line number.
        var sb     = new System.Text.StringBuilder();
        var lineAt = new List<string>();
        var inBlock = false;
        char blockClose = ')';
        var fallback = 0;
        foreach (var lineEl in routineXml.Descendants("Line"))
        {
            var number = lineEl.Attribute("Number")?.Value ?? fallback.ToString();
            fallback++;
            var code = StripStComments(lineEl.Value, ref inBlock, ref blockClose) + "\n";
            sb.Append(code);
            for (var k = 0; k < code.Length; k++) lineAt.Add(number);
        }
        var text = sb.ToString();
        joinedText = text;

        var found = new List<(int Pos, StFinding Finding)>();

        // Assignments.
        for (var i = text.IndexOf(":=", StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(":=", i + 2, StringComparison.Ordinal))
        {
            var end = AssignmentTargetEnd(text, i);
            var rawTarget = ExtractAssignmentTarget(text, i);
            var start = Math.Max(0, end - rawTarget.Length);
            var target = StripWhitespace(rawTarget);

            if (IsValidTagPath(target))
            {
                var name = ExtractScopedName(target);
                if (!string.IsNullOrEmpty(name))
                {
                    found.Add((start, new StFinding(name, lineAt[start], true, null, target, start)));
                    continue;
                }
            }

            // The left-hand side does not parse as a Logix tag path: never drop the
            // statement.  Every identifier on the left-hand side is unknown write context.
            // If the walk-back found no identifier at all, widen to the whole statement
            // prefix (back to the previous ';').
            var lhs = rawTarget;
            var lhsStart = start;
            if (!StOperandPattern.IsMatch(lhs))
            {
                var semi = text.LastIndexOf(';', Math.Max(0, i - 1));
                lhsStart = semi < 0 || semi > i ? 0 : semi + 1;
                lhs = text[lhsStart..end];
            }
            foreach (System.Text.RegularExpressions.Match op in StOperandPattern.Matches(lhs))
            {
                if (StKeywords.Contains(op.Value)) continue;
                found.Add((lhsStart + op.Index,
                    new StFinding(ExtractScopedName(op.Value) ?? op.Value, lineAt[lhsStart + op.Index], false, null,
                        op.Value, lhsStart + op.Index)));
            }
        }

        // Instruction / function / AOI calls.
        foreach (System.Text.RegularExpressions.Match m in StCallPattern.Matches(text))
        {
            var callName = m.Groups[1].Value;
            if (StKeywords.Contains(callName)) continue;

            var args = SplitCallArguments(text, m.Index + m.Length - 1);
            var line = lineAt[m.Groups[1].Index];
            for (var a = 0; a < args.Count; a++)
            {
                var kind = ClassifyStCallArgument(callName, args, a, aoiWrites);
                if (kind == "read") continue;

                var arg = args[a].Trim();
                if (kind == "write" && IsSimpleReference(arg))
                {
                    var baseName = ExtractScopedName(arg);
                    if (!string.IsNullOrEmpty(baseName))
                        found.Add((m.Index, new StFinding(baseName, line, true, callName.ToUpperInvariant(), arg, m.Index)));
                    continue;
                }

                // Unknown call, or a write operand that is not a plain reference: every
                // identifier in the argument might be driven — fail closed.
                foreach (System.Text.RegularExpressions.Match op in StOperandPattern.Matches(arg))
                    found.Add((m.Index, new StFinding(ExtractScopedName(op.Value) ?? op.Value, line, false, callName.ToUpperInvariant(),
                        op.Value, m.Index)));
            }
        }

        return found.OrderBy(f => f.Pos).Select(f => f.Finding).ToList();
    }

    /// <summary>
    /// Classifies argument <paramref name="index"/> of an ST call.  Known instructions and
    /// project AOIs resolve through <see cref="ClassifyKind(string,int,IReadOnlyDictionary{string,IReadOnlySet{int}}?)"/>;
    /// <c>JSR</c> is positional; known pure functions and type conversions are reads; anything
    /// else is <c>"unknown"</c>.
    /// </summary>
    internal static string ClassifyStCallArgument(
        string callName,
        IReadOnlyList<string> args,
        int index,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? aoiWrites)
    {
        if (string.Equals(callName, JsrKey, StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2 || !int.TryParse(args[1].Trim(), out var inputCount) || inputCount < 0)
                return "unknown";
            if (index <= 1) return "read";
            return index >= 2 + inputCount ? "write" : "read";
        }

        var kind = ClassifyKind(callName, index, aoiWrites);
        // A project AOI (readable or encoded) is never a pure function or type conversion,
        // whatever it is named: MAX or INT_TO_REAL may be the project's own instruction.
        var isProjectAoi = aoiWrites is not null && aoiWrites.ContainsKey(callName);
        if (kind == "unknown" && !isProjectAoi &&
            (StPureFunctions.Contains(callName) || StTypeConversionPattern.IsMatch(callName)))
            return "read";
        return kind;
    }

    /// <summary>
    /// Splits the argument list whose opening parenthesis is at <paramref name="open"/> on
    /// top-level commas.  An unbalanced list runs to the end of the text.
    /// </summary>
    internal static List<string> SplitCallArguments(string text, int open)
    {
        var args  = new List<string>();
        var depth = 0;
        var start = open + 1;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']')
            {
                if (--depth == 0)
                {
                    args.Add(text[start..i]);
                    return args;
                }
            }
            else if (c == ',' && depth == 1)
            {
                args.Add(text[start..i]);
                start = i + 1;
            }
        }
        args.Add(text[start..]);
        return args;
    }

    /// <summary>
    /// <c>true</c> when <paramref name="arg"/> is a plain tag reference
    /// (<c>name</c>, <c>name.member</c>, <c>name[expr]</c>) rather than an expression.
    /// </summary>
    private static bool IsSimpleReference(string arg)
    {
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in arg)
        {
            if (c == '[') { depth++; continue; }
            if (c == ']') { if (--depth < 0) return false; continue; }
            if (depth == 0) sb.Append(c);
        }
        return depth == 0 &&
               System.Text.RegularExpressions.Regex.IsMatch(sb.ToString(), @"^\\?(Program:)?[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$");
    }

    /// <summary>Removes comments and string literals from one ST line, tracking block-comment state.</summary>
    private static string StripStComments(string line, ref bool inBlock, ref char blockClose)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inBlock)
            {
                if (blockClose == ')' && c == '*' && i + 1 < line.Length && line[i + 1] == ')')
                { inBlock = false; i++; }
                else if (blockClose == '/' && c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                { inBlock = false; i++; }
                continue;
            }
            if (c == '(' && i + 1 < line.Length && line[i + 1] == '*')
            { inBlock = true; blockClose = ')'; i++; sb.Append(' '); continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            { inBlock = true; blockClose = '/'; i++; sb.Append(' '); continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;
            if (c == '\'')
            {
                // Skip the string literal; '$' escapes the next character in IEC strings.
                i++;
                while (i < line.Length && line[i] != '\'')
                    i += line[i] == '$' ? 2 : 1;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the index just past the assignment target for the <c>:=</c> at
    /// <paramref name="assignAt"/>, skipping whitespace and the <c>[</c> of the ST
    /// non-retentive assignment <c>[:=]</c>.
    /// </summary>
    private static int AssignmentTargetEnd(string code, int assignAt)
    {
        var end = assignAt;
        while (end > 0 && char.IsWhiteSpace(code[end - 1])) end--;
        if (end > 0 && code[end - 1] == '[')
        {
            end--;
            while (end > 0 && char.IsWhiteSpace(code[end - 1])) end--;
        }
        return end;
    }

    /// <summary>
    /// Walks backwards from the <c>:=</c> at <paramref name="assignAt"/> over the
    /// left-hand side (identifier, <c>.member</c> and balanced <c>[...]</c> segments).
    /// Whitespace and line breaks adjacent to <c>.</c>, <c>[</c> and <c>]</c> are part of
    /// the target (comments were already replaced by whitespace); whitespace between two
    /// other tokens ends it.  The returned text may contain whitespace.
    /// </summary>
    private static string ExtractAssignmentTarget(string code, int assignAt)
    {
        var end = AssignmentTargetEnd(code, assignAt);

        var start = end;
        while (start > 0)
        {
            var c = code[start - 1];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '\\')
            {
                start--;
                // Whitespace after consuming '.' (e.g. "Cmd_Motor .Speed") is skipped.
                if (c == '.') start = SkipWhitespaceBack(code, start);
            }
            else if (c == ']')
            {
                var depth = 0;
                var j = start - 1;
                for (; j >= 0; j--)
                {
                    if (code[j] == ']') depth++;
                    else if (code[j] == '[' && --depth == 0) break;
                }
                if (j < 0) break;
                start = j;
                // "Cmd_Arr [i]": whitespace before '[' belongs to the target when an
                // identifier or another subscript precedes it.
                var ws = SkipWhitespaceBack(code, start);
                if (ws < start && ws > 0 && (char.IsLetterOrDigit(code[ws - 1]) || code[ws - 1] == '_' || code[ws - 1] == ']'))
                    start = ws;
            }
            else if (char.IsWhiteSpace(c))
            {
                // Whitespace on the right of a '.' ("Cmd_Motor. Speed") belongs to the target.
                var ws = SkipWhitespaceBack(code, start);
                if (ws > 0 && code[ws - 1] == '.') start = ws;
                else break;
            }
            else break;
        }
        var target = code[start..end];

        // A CASE label written without a space ("1:Cmd_X := 2") is walked into the target;
        // drop any leading numeric label so the base name is the real assignment target.
        while (target.Length > 0 && char.IsDigit(target[0]) && target.IndexOf(':') is var k and >= 0)
            target = target[(k + 1)..];
        return target.TrimStart();
    }

    /// <summary>Returns the index of the first whitespace of the run that ends at <paramref name="pos"/>.</summary>
    private static int SkipWhitespaceBack(string code, int pos)
    {
        while (pos > 0 && char.IsWhiteSpace(code[pos - 1])) pos--;
        return pos;
    }

    /// <summary>Removes every whitespace character from <paramref name="text"/>.</summary>
    internal static string StripWhitespace(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static readonly System.Text.RegularExpressions.Regex TagPathSubscript =
        new(@"^[A-Za-z0-9_.,+\-*/%()\\:\[\]]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TagPathHead =
        new(@"^\\?(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TagPathMember =
        new(@"^\.(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Validates whitespace-free <paramref name="path"/> against the Logix tag-path grammar:
    /// <c>[\][Program:Prog.]name</c> followed by any number of <c>.member</c>, <c>.bit</c>
    /// and non-empty, balanced <c>[index-expression]</c> segments.
    /// </summary>
    /// <param name="path">Candidate target with whitespace removed.</param>
    /// <returns><c>true</c> when the whole string is a valid tag path.</returns>
    private static bool IsValidTagPath(string path)
    {
        var head = TagPathHead.Match(path);
        if (!head.Success) return false;
        var pos = head.Length;
        while (pos < path.Length)
        {
            if (path[pos] == '.')
            {
                var m = TagPathMember.Match(path[pos..]);
                if (!m.Success) return false;
                pos += m.Length;
            }
            else if (path[pos] == '[')
            {
                var depth = 0;
                var j = pos;
                for (; j < path.Length; j++)
                {
                    if (path[j] == '[') depth++;
                    else if (path[j] == ']' && --depth == 0) break;
                }
                if (j >= path.Length) return false;
                var inner = path[(pos + 1)..j];
                if (inner.Length == 0 || !TagPathSubscript.IsMatch(inner) || !HasBalancedBrackets(inner)) return false;
                pos = j + 1;
            }
            else return false;
        }
        return true;
    }

    private static bool HasBalancedBrackets(string text)
    {
        int square = 0, round = 0;
        foreach (var c in text)
        {
            if (c == '[') square++;
            else if (c == ']' && --square < 0) return false;
            else if (c == '(') round++;
            else if (c == ')' && --round < 0) return false;
        }
        return square == 0 && round == 0;
    }
}
