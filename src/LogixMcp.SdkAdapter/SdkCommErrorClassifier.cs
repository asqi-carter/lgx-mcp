#if LOGIX_SDK
// ── SdkCommErrorClassifier — operation-first comm-layer error classifier ──────

/// <summary>
/// Shared, stateless classifier for Rockwell SDK comm-layer errors
/// (<c>RxE_COMM_*</c> family).
///
/// <para>
/// This is the <em>outermost decision gate</em> used by both
/// <see cref="SdkConnectFactory"/> (connect/upload path) and
/// <see cref="TypedSdkTagService"/> (in-session tag operations).  It runs
/// before the comm-layer discriminator (TCP probe on port 44818) to
/// distinguish errors that are inherent to the file-opened project path
/// from errors that require a network reachability check.
/// </para>
///
/// <para>
/// Decision table:
/// <list type="table">
///   <listheader>
///     <term>Operation that threw</term>
///     <description>Classification</description>
///   </listheader>
///   <item>
///     <term><c>GoOnlineAsync</c> on a file-opened project
///       (<see cref="SdkConnectFactory.CreateAsync"/> path — L5X or
///       uncorrelated ACD)</term>
///     <description>
///       <see cref="TagServiceErrorKind.ProjectNotCorrelated"/> — advise the
///       comm-path/upload route; TCP probe is <b>not</b> invoked.
///     </description>
///   </item>
///   <item>
///     <term><c>UploadToNewProjectAsync</c>, <c>SaveAsAsync</c>, in-session
///       tag/rung operations</term>
///     <description>
///       Fall through to the comm-layer discriminator (TCP probe path).
///     </description>
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>Scope of <see cref="IsCommLayerError"/>:</b> matches any token beginning
/// with <c>RxE_COMM_</c> anywhere in the exception chain (outer message or inner
/// exception chain).  Does <b>not</b> match
/// <c>RxCL_E_CANNOT_UPLOAD_PHYS_ADDR</c> or <c>RxCL_E_IMAGES_INCOMPATIBLE</c>
/// — those are correlation failures with their own dedicated handlers.
/// </para>
/// </summary>
internal static class SdkCommErrorClassifier
{
    // Every code and message pattern below is defined once, in RockwellErrorHints (core assembly).
    // This class keeps its method names for the connect and tag paths and delegates the matching.

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/>'s message contains the
    /// literal text <c>"Project is not open."</c> — the SDK exception thrown by
    /// <c>SaveAsAsync</c> when the session handle is in a degraded state.
    ///
    /// <para>
    /// <b>Scope:</b> this is a dead-service indicator, not a pending-edit
    /// indicator.  Hardware-verified 2026-09-06: an upload connect succeeded
    /// with three pending (I) rungs in the controller.  <c>"Project is not
    /// open."</c> is emitted by the SDK when the gRPC service has crashed or
    /// been stopped, leaving the project handle in a degraded state.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns>
    /// <c>true</c> when the exception message contains <c>"Project is not open."</c>;
    /// <c>false</c> otherwise.
    /// </returns>
    public static bool IsPendingEditsError(Exception ex)
        => RockwellErrorHints.IsProjectNotOpen(ex);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> indicates the
    /// <c>LdSdkService</c> gRPC service is unreachable — specifically when the
    /// exception message contains one of the following SDK-emitted texts:
    /// <list type="bullet">
    ///   <item><c>"Could not reach LdSdkService"</c></item>
    ///   <item><c>"Unable to complete connection to LdSdkService"</c></item>
    /// </list>
    ///
    /// <para>
    /// These messages are emitted by the Logix Designer SDK when the gRPC client
    /// cannot connect to <c>LdSdkServer.exe</c>, typically because the service
    /// has crashed or been stopped.  They can appear on any SDK call — including
    /// in-session tag read/write operations after the service has degraded
    /// mid-session.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns>
    /// <c>true</c> when the exception message contains either service-unreachable
    /// text; <c>false</c> otherwise.
    /// </returns>
    public static bool IsServiceUnreachableError(Exception ex)
        => RockwellErrorHints.IsServiceUnreachable(ex);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> is a dead-session
    /// indicator — a message that signals the SDK session has died, either
    /// because the <c>LdSdkService</c> gRPC service crashed
    /// (<c>"Project is not open."</c>) or because it is unreachable
    /// (<c>"Could not reach LdSdkService"</c>,
    ///  <c>"Unable to complete connection to LdSdkService"</c>).
    ///
    /// <para>
    /// Used as the outer guard by both
    /// <see cref="SdkConnectFactory"/> (connect/upload path) and
    /// <see cref="TypedSdkTagService"/> (in-session tag operations) before
    /// mapping the exception to a structured <see cref="TagServiceError"/>.
    /// Both paths apply the SCM race-avoidance re-query protocol after a
    /// positive match.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns>
    /// <c>true</c> when <see cref="IsPendingEditsError"/> or
    /// <see cref="IsServiceUnreachableError"/> returns <c>true</c>;
    /// <c>false</c> otherwise.
    /// </returns>
    public static bool IsDeadSessionIndicator(Exception ex)
        => IsPendingEditsError(ex) || IsServiceUnreachableError(ex);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> or any exception in its
    /// <see cref="Exception.InnerException"/> chain contains a token that
    /// begins with <c>RxE_COMM_</c> (case-insensitive).
    ///
    /// <para>
    /// Matches the entire <c>RxE_COMM_*</c> family (e.g. <c>RxE_COMM_DTLERROR</c>,
    /// <c>RxE_COMM_OTHER</c>, …) so that future Rockwell comm-layer codes are
    /// automatically covered without code changes.
    /// </para>
    ///
    /// <para>
    /// <b>Not matched</b>: <c>RxCL_E_CANNOT_UPLOAD_PHYS_ADDR</c>,
    /// <c>RxCL_E_IMAGES_INCOMPATIBLE</c>, <c>"Project is not open."</c>,
    /// <c>"Could not reach LdSdkService"</c> — those have their own handlers.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect (never <c>null</c>).</param>
    /// <returns>
    /// <c>true</c> when any exception in the chain has a message containing
    /// <c>"RxE_COMM_"</c>; <c>false</c> otherwise.
    /// </returns>
    public static bool IsCommLayerError(Exception ex)
        => RockwellErrorHints.IsCommLayerError(ex);

    /// <summary>
    /// Builds a <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.ProjectNotCorrelated"/> for the
    /// "GoOnlineAsync on a file-opened project" trap.
    ///
    /// <para>
    /// Called when <c>GoOnlineAsync</c> throws an <c>RxE_COMM_*</c> error on
    /// the file-based connect path (<see cref="SdkConnectFactory.CreateAsync"/>).
    /// A file opened via <c>logix_open_project</c> cannot prove it matches the
    /// controller's running program image; the file-based online path is not the
    /// correct route.
    /// </para>
    ///
    /// <para>
    /// The message:
    /// <list type="bullet">
    ///   <item>Names the file that was opened.</item>
    ///   <item>Quotes the raw SDK code verbatim so it can be searched.</item>
    ///   <item>Names the comm-path/upload route as the correct alternative.</item>
    /// </list>
    /// The TCP probe is <b>not</b> invoked: the error is inherent to the
    /// file-based path, not to controller reachability.
    /// </para>
    /// </summary>
    /// <param name="ex">
    /// The SDK exception that was thrown.  Preserved as
    /// <see cref="Exception.InnerException"/> of the returned exception.
    /// </param>
    /// <param name="projectFilePath">
    /// The file path that was opened (ACD or L5X), included in the error
    /// message for operator diagnostics.
    /// </param>
    /// <returns>
    /// A new <see cref="TagServiceException"/> with kind
    /// <see cref="TagServiceErrorKind.ProjectNotCorrelated"/>.
    /// </returns>
    public static TagServiceException BuildProjectNotCorrelatedError(
        Exception ex, string projectFilePath)
        => new(new TagServiceError(
            TagServiceErrorKind.ProjectNotCorrelated,
            $"Cannot go online from the file-opened project '{projectFilePath}'. " +
            $"The SDK reported: \"{ex.Message}\" " +
            $"Opening a project file is for analysis and gate derivation — a file " +
            $"cannot prove it matches the controller's running program image. " +
            $"Use the comm-path/upload route instead: call logix_connect with " +
            $"sdkMode='online' and a gateway or sdkCommPath without a pre-opened " +
            $"project — the controller's current project will be uploaded " +
            $"automatically and used to establish the correlated online session."),
            ex);
}
#endif
