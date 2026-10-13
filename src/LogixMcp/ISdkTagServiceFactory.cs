using L5Sharp.Core;

// ── SdkSessionResult — factory return type carrying service + metadata ────────

/// <summary>
/// Return value from <see cref="ISdkTagServiceFactory.CreateAsync"/> that
/// carries the opened tag service together with structured metadata about what
/// happened during session establishment.
/// </summary>
/// <param name="Service">
/// The opened SDK-backed <see cref="ITagService"/>.  The caller owns this
/// service's lifetime.
/// </param>
/// <param name="DownloadPerformed">
/// <c>true</c> when the PROGRAM→download→go_online→RUN sequence was executed
/// during establishment (i.e. <c>allowControllerDownload=true</c> was passed
/// <em>and</em> the initial go-online probe failed, so a download was
/// required); <c>false</c> in all other cases (offline mode, or online mode
/// where the initial go-online succeeded without a download).
/// </param>
/// <param name="ModeTransitions">
/// Ordered list of controller mode names that were requested during session
/// establishment, in invocation order.  Only populated when
/// <see cref="DownloadPerformed"/> is <c>true</c>; otherwise an empty array.
/// Typical value for a full download sequence: <c>["Program", "Run"]</c>.
/// </param>
/// <param name="CorrelationPath">
/// Identifies which rung of the three-rung correlation ladder was used to
/// bring the session online.  Only populated when <c>sdkMode="online"</c>;
/// <c>null</c> for offline sessions.
/// <list type="bullet">
///   <item>
///     <c>"already-correlated"</c> — <c>GoOnlineAsync</c> succeeded on the
///     first attempt without any upload or download (rung 1).  The controller
///     already held a matching project image.
///   </item>
///   <item>
///     <c>"upload"</c> — <c>UploadAsync</c> was called to pull the
///     controller's current image into the working copy, after which
///     <c>GoOnlineAsync</c> succeeded (rung 2).  Non-destructive to the
///     controller.
///   </item>
///   <item>
///     <c>"download"</c> — the PROGRAM→download→go_online→RUN sequence was
///     executed (rung 3).  Only reached when rungs 1 and 2 both failed and
///     <c>allowControllerDownload=true</c> was specified.  Destructive —
///     overwrites the controller's stored program image.
///   </item>
///   <item>
///     <c>"upload-to-new-project"</c> — the session was established via
///     <c>UploadToNewProjectAsync</c> (no pre-existing project file).  The
///     controller's current program image was uploaded to a new server-managed
///     ACD, which was then used to open the session.  Non-destructive to the
///     controller.  Only produced by
///     <see cref="ISdkTagServiceFactory.CreateFromControllerAsync"/>.
///   </item>
/// </list>
/// </param>
/// <param name="TypeProject">
/// The parsed L5X project extracted from the uploaded ACD after a
/// <c>"upload-to-new-project"</c> session.  Populated only when the session
/// was established via
/// <see cref="ISdkTagServiceFactory.CreateFromControllerAsync"/>; <c>null</c>
/// for all other sessions (which derive the type project from the caller's
/// pre-loaded project).  Used by <see cref="ConnectHandler"/> to derive the
/// write-gate policy when no project was pre-loaded.
/// </param>
/// <param name="AcdPath">
/// Absolute path to the uploaded ACD file placed in the server-managed working
/// directory.  Populated only when the session was established via
/// <see cref="ISdkTagServiceFactory.CreateFromControllerAsync"/>; <c>null</c>
/// for all other sessions.  Used by <see cref="ConnectHandler"/> to register
/// the uploaded project in <see cref="ProjectService"/> via
/// <c>RegisterFromUpload</c> so that subsequent tool calls that guard on
/// <see cref="ProjectService.IsLoaded"/> or <see cref="ProjectService.FilePath"/>
/// see a valid project without requiring a separate <c>logix_open_project</c>
/// invocation.
/// </param>
/// <param name="DesignerVersion">
/// Logix Designer version known when the session was established (from the project's
/// <c>SoftwareRevision</c>), or <c>null</c> when unknown. Reported by <c>logix_connect</c>.
/// </param>
/// <param name="Capabilities">
/// The session's SDK capability table (<see cref="SdkCapabilities"/>), or <c>null</c> when the factory built none.
/// Reported by <c>logix_connect</c> as <c>capabilities</c>.
/// </param>
/// <param name="ControllerIdentity">
/// Name and serial the SDK session reports for the controller, used to verify that a CIP
/// connection reaches the same controller. <c>null</c> when the factory has none;
/// <see cref="ConnectHandler"/> then derives it from <paramref name="TypeProject"/>.
/// </param>
public record SdkSessionResult(
    ITagService Service,
    bool        DownloadPerformed,
    string[]    ModeTransitions,
    string?     CorrelationPath = null,
    L5X?        TypeProject     = null,
    string?     AcdPath         = null,
    DesignerVersion? DesignerVersion = null,
    SdkCapabilities? Capabilities = null,
    ControllerIdentity? ControllerIdentity = null);

// ── ISdkTagServiceFactory — seam for runtime SDK connection creation ──────────

/// <summary>
/// Factory seam that creates an SDK-backed <see cref="ITagService"/> from a
/// Logix Designer project file and its in-memory L5X representation.
///
/// <para>
/// Defined in the core assembly so <see cref="ConnectHandler"/> can reference
/// it without taking a compile-time dependency on
/// <c>RockwellAutomation.*</c>.  The concrete implementation
/// (<c>SdkConnectFactory</c>) lives in <c>LogixMcp.SdkAdapter</c> and is
/// registered via
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/>
/// when that assembly is loaded.
/// </para>
/// </summary>
public interface ISdkTagServiceFactory
{
    /// <summary>
    /// Creates and opens a new SDK-backed <see cref="ITagService"/> against
    /// the project at <paramref name="projectFilePath"/>.
    /// </summary>
    /// <param name="projectFilePath">
    /// Absolute path to the .ACD or .L5X project file.  The implementation
    /// opens a gRPC session to the Logix Designer SDK against this file.
    /// </param>
    /// <param name="typeProject">
    /// The in-memory L5X project used for tag-type resolution.  The returned
    /// service uses this to resolve declared types before issuing SDK calls.
    /// </param>
    /// <param name="sdkMode">
    /// The SDK operation mode: <c>"online"</c> for reads/writes against a
    /// live controller via the SDK gRPC channel; <c>"offline"</c> (default)
    /// for reads/writes against the project file only.  Case-insensitive.
    /// </param>
    /// <param name="allowControllerDownload">
    /// Last-resort opt-in for rung 3 of the correlation ladder.  Only required when
    /// the controller is empty or holds a completely unrelated program and both
    /// non-destructive rungs have failed:
    /// <list type="bullet">
    ///   <item>Rung 1 (GoOnline directly) — always attempted first; no controller impact.</item>
    ///   <item>Rung 2 (Upload→GoOnline) — always attempted when rung 1 fails; no controller impact.</item>
    ///   <item>Rung 3 (PROGRAM→download→go_online→RUN) — only executed when
    ///     <paramref name="allowControllerDownload"/> is <c>true</c> and rungs 1 and 2
    ///     have both been exhausted.</item>
    /// </list>
    /// <para>
    /// When <c>true</c> and rungs 1 and 2 have both failed: transitions the
    /// controller to PROGRAM mode (stopping program scan), downloads the open project
    /// to the controller (overwriting the currently stored program image), calls
    /// <c>GoOnlineAsync</c>, and finally transitions the controller back to RUN mode.
    /// </para>
    /// <para>
    /// <b>WARNING:</b> This sequence permanently stops the running controller and
    /// overwrites the program stored in it.  This parameter must never be set
    /// preemptively — only after the caller has received a
    /// <see cref="TagServiceErrorKind.ImagesIncompatible"/> error indicating that
    /// rungs 1 and 2 have been exhausted.
    /// </para>
    /// Ignored when <paramref name="sdkMode"/> is <c>"offline"</c>.
    /// Defaults to <c>false</c>.
    /// </param>
    /// <param name="ct">Cancellation token for the open operation.</param>
    /// <returns>
    /// A <see cref="SdkSessionResult"/> containing the opened
    /// <see cref="ITagService"/> and structured metadata about what occurred
    /// during session establishment (whether a download was performed, and the
    /// ordered list of controller mode transitions that were requested).
    /// The caller owns the service's lifetime; calling
    /// <see cref="ITagService.DisconnectAsync"/> or disposing the underlying
    /// session is the caller's responsibility.
    /// </returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// the SDK session cannot be opened.
    /// </exception>
    Task<SdkSessionResult> CreateAsync(
        string projectFilePath,
        L5X typeProject,
        string sdkMode = "offline",
        bool allowControllerDownload = false,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an online SDK-backed <see cref="ITagService"/> without a
    /// pre-existing project file by uploading the controller's current program
    /// to a new server-managed ACD and establishing an online session against it.
    ///
    /// <para>
    /// This is the entry point for the no-project-file path in
    /// <see cref="ConnectHandler"/>: when the caller provides a
    /// <paramref name="commPath"/> but no project has been pre-loaded via
    /// <c>logix_open_project</c>, this method uploads the controller's image
    /// (non-destructive) to a fresh working-directory ACD, exports it to L5X
    /// for type resolution, and returns an online session against it.
    /// </para>
    ///
    /// <para>
    /// The resulting <see cref="SdkSessionResult"/> carries
    /// <see cref="SdkSessionResult.CorrelationPath"/> set to
    /// <c>"upload-to-new-project"</c> and
    /// <see cref="SdkSessionResult.TypeProject"/> populated with the parsed L5X
    /// for gate-policy derivation by the caller.
    /// </para>
    /// </summary>
    /// <param name="commPath">
    /// Rockwell SDK communications path to the controller.  Typical formats:
    /// <list type="bullet">
    ///   <item><c>"198.51.100.10"</c> — IP-direct to controller</item>
    ///   <item><c>"198.51.100.10,1,0"</c> — IP + backplane slot</item>
    /// </list>
    /// Must not be <c>null</c> or whitespace.
    /// </param>
    /// <param name="ct">Cancellation token for the upload and session-open operation.</param>
    /// <returns>
    /// A <see cref="SdkSessionResult"/> with:
    /// <list type="bullet">
    ///   <item><see cref="SdkSessionResult.CorrelationPath"/> = <c>"upload-to-new-project"</c></item>
    ///   <item><see cref="SdkSessionResult.TypeProject"/> = parsed L5X for gate-policy derivation</item>
    ///   <item><see cref="SdkSessionResult.DownloadPerformed"/> = <c>false</c></item>
    ///   <item><see cref="SdkSessionResult.ModeTransitions"/> = empty array</item>
    /// </list>
    /// </returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when the
    /// controller cannot be reached, the upload fails, or L5X extraction fails.
    /// </exception>
    Task<SdkSessionResult> CreateFromControllerAsync(
        string commPath,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="CreateFromControllerAsync(string, CancellationToken)"/>, additionally
    /// reporting the <see cref="ConnectPhases.Upload"/>, <see cref="ConnectPhases.SaveL5x"/> and
    /// <see cref="ConnectPhases.Parse"/> phase boundaries to <paramref name="progress"/> as each
    /// phase starts.  The default implementation reports nothing and delegates, so factories that
    /// predate progress reporting keep working unchanged.
    /// </summary>
    /// <param name="commPath">Rockwell SDK communications path to the controller.</param>
    /// <param name="progress">Receiver for phase reports; <c>null</c> reports nothing.</param>
    /// <param name="ct">Cancellation token for the upload and session-open operation.</param>
    /// <returns>See <see cref="CreateFromControllerAsync(string, CancellationToken)"/>.</returns>
    Task<SdkSessionResult> CreateFromControllerAsync(
        string commPath,
        IProgress<ConnectProgress>? progress,
        CancellationToken ct) =>
        CreateFromControllerAsync(commPath, ct);
}
