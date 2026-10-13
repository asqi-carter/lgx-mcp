/// <summary>
/// Non-conditional registry of every tool name exposed by
/// <c>LogixSdkTools</c>.
///
/// <para>
/// This file is compiled in <em>both</em> SDK and non-SDK builds so that
/// <c>SkillsGuardTests.EveryToolNameInSkills_IsARegisteredTool</c> can
/// validate skill-playbook tool references without requiring the full Rockwell
/// SDK dependencies that are only available on Windows bench machines.
/// </para>
///
/// <para>
/// The list must be kept in sync with the
/// <c>[McpServerTool(Name = "...")]</c> attributes in
/// <c>LogixSdkTools.cs</c>.  A companion assertion in
/// <c>SkillsGuardTests</c> uses reflection (when the SDK assembly is
/// compiled with <c>LOGIX_SDK</c>) to confirm that every name here is
/// actually present on a decorated method, closing the round-trip gap.
/// </para>
/// </summary>
public static class SdkToolNames
{
    /// <summary>
    /// All tool names registered in <c>LogixSdkTools</c>. <c>logix_doctor</c> is not among them: it is
    /// registered from the core assembly (<c>LogixDoctorTools</c>) so it exists in every install state.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        "logix_export_rungs",
        "logix_propose_rung_edit",
        "logix_pending_edits",
        "logix_clear_staged",
        "logix_create_routine",
        "logix_finalize_edits",
        "logix_cancel_pending_edits",
        "logix_accept_pending_edits",
        "logix_test_pending_edits",
        "logix_untest_pending_edits",
        "logix_assemble_pending_edits",
        "logix_run_test",
        "logix_build",
    ];
}
