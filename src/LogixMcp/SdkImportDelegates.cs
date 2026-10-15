using System.Collections.Generic;

// ── SdkImportDelegates ────────────────────────────────────────────────────────
// SDK-free registration point for the SDK-backed operations that
// logix_import_datatype's real run needs.  The pure handler
// (ImportDatatypeHandler.Execute) takes these as injectable delegates so it can
// be unit-tested with fakes; production wiring flows call site → this registry →
// SdkImportDatatypeRunner (compiled only under #if LOGIX_SDK, registered by
// SdkConnectFactory.Initialize).  When the adapter is absent (SDK-less builds,
// unit tests) every field stays null and the tool serves dry-run deltas only.
//
// Note: the call site (LogixProjectTools.GetImportDatatypeResponse) must pass the
// handler's delegates from this registry. If it passes none, the real run would
// silently take the File.WriteAllText fragment fallback and report verified:true.

/// <summary>
/// Holds the SDK-backed implementations that <c>logix_import_datatype</c>'s real
/// run invokes.  Populated once by the SDK adapter's module initializer; left
/// null in SDK-less builds so the tool degrades to dry-run only.
/// </summary>
public static class SdkImportDelegates
{
    /// <summary>
    /// Performs the real SDK import: opens the ACD at the given save path,
    /// imports the L5X fragment at the datatype XPath with the collision option,
    /// and saves the ACD in place.  Signature: (saveAsAcd, l5xToImport, xpath,
    /// collision).  Returns the status/error messages the SDK reported during the
    /// import (empty when none); a failed import throws
    /// <see cref="SdkImportException"/> carrying the messages captured up to the failure.
    /// </summary>
    public static System.Func<string, string, string, ImportCollisionOption, IReadOnlyList<string>>? SdkImportRunner { get; set; }

    /// <summary>
    /// Re-exports the datatype from a saved ACD and returns its members.
    /// Signature: (acdPath, xpath) → members.  The XPath is supplied by the call
    /// site (which alone knows the datatype name) so this stays a plain reader.
    /// </summary>
    public static System.Func<string, string, IReadOnlyList<DataTypeMemberEntry>>? ReexportReader { get; set; }

    /// <summary>
    /// Builds a saved ACD and returns the error count; throws with a message
    /// containing "not supported" on Logix Designer versions that do not support
    /// offline build (the handler reports "unsupported on &lt;version&gt;").
    /// Signature: (acdPath) → errorCount.
    /// </summary>
    public static System.Func<string, int>? BuildRunner { get; set; }

    /// <summary>
    /// Probes whether Studio 5000 holds the ACD open exclusively.  When null the
    /// handler falls back to its own <c>IsLockedByStudio</c> probe.
    /// </summary>
    public static System.Func<string, bool>? StudioLockChecker { get; set; }
}

/// <summary>
/// Thrown by the SDK import runner when the partial import fails.  Carries the SDK's own status/error
/// messages captured during the call so the handler can report them (<c>sdkMessages</c>) and recognise
/// the SDK's abort mnemonics.
/// </summary>
public sealed class SdkImportException : System.Exception
{
    /// <summary>The SDK mnemonic raised when <c>collision: Cancel</c> meets an existing type.</summary>
    public const string AbortedNoChangesMnemonic = "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES";

    /// <summary>Status/error messages the SDK reported during the failed import (never null).</summary>
    public IReadOnlyList<string> SdkMessages { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The SDK exception message.</param>
    /// <param name="sdkMessages">Captured SDK messages; null is treated as empty.</param>
    /// <param name="inner">The original SDK exception.</param>
    public SdkImportException(string message, IReadOnlyList<string>? sdkMessages, System.Exception? inner = null)
        : base(message, inner)
    {
        SdkMessages = sdkMessages ?? System.Array.Empty<string>();
    }
}
