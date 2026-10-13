#if LOGIX_SDK
using System;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="SdkCommErrorClassifier"/>.
///
/// <para>Coverage areas:</para>
/// <list type="bullet">
///   <item><see cref="SdkCommErrorClassifier.IsCommLayerError"/> — outer exception match</item>
///   <item><see cref="SdkCommErrorClassifier.IsCommLayerError"/> — inner exception chain match</item>
///   <item><see cref="SdkCommErrorClassifier.IsCommLayerError"/> — case-insensitive match</item>
///   <item><see cref="SdkCommErrorClassifier.IsCommLayerError"/> — non-matching codes are rejected</item>
///   <item><see cref="SdkCommErrorClassifier.BuildProjectNotCorrelatedError"/> — kind and message</item>
/// </list>
/// </summary>
public class SdkCommErrorClassifierTests
{
    // ── IsCommLayerError — positive cases ────────────────────────────────────

    [Fact]
    public void IsCommLayerError_OuterExceptionContainsRxeComm_ReturnsTrue()
    {
        var ex = new InvalidOperationException("RxE_COMM_DTLERROR - comm layer faulted");
        Assert.True(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    [Fact]
    public void IsCommLayerError_OuterExceptionContainsRxeCommOther_ReturnsTrue()
    {
        var ex = new InvalidOperationException("Error: RxE_COMM_OTHER");
        Assert.True(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    [Fact]
    public void IsCommLayerError_InnerExceptionContainsRxeComm_ReturnsTrue()
    {
        // The inner exception carries the comm-layer code; the outer just wraps it.
        var inner = new InvalidOperationException("RxE_COMM_DTLERROR");
        var outer = new Exception("SDK operation failed", inner);

        Assert.True(SdkCommErrorClassifier.IsCommLayerError(outer));
    }

    [Fact]
    public void IsCommLayerError_DeeplyNestedInnerException_ReturnsTrue()
    {
        // Verify the full chain is walked, not just one level deep.
        var root  = new InvalidOperationException("RxE_COMM_DTLERROR");
        var mid   = new Exception("wrapped once", root);
        var outer = new Exception("wrapped twice", mid);

        Assert.True(SdkCommErrorClassifier.IsCommLayerError(outer));
    }

    [Fact]
    public void IsCommLayerError_IsCaseInsensitive()
    {
        // Matching must be case-insensitive per the spec.
        var ex = new InvalidOperationException("rxe_comm_dtlerror");
        Assert.True(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    // ── IsCommLayerError — negative cases ────────────────────────────────────

    [Fact]
    public void IsCommLayerError_RxClECannotUploadPhysAddr_ReturnsFalse()
    {
        // RxCL_E_CANNOT_UPLOAD_PHYS_ADDR is a correlation failure — must NOT
        // be classified as a comm-layer error.
        var ex = new InvalidOperationException(
            "RxCL_E_CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated.");
        Assert.False(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    [Fact]
    public void IsCommLayerError_RxClEImagesIncompatible_ReturnsFalse()
    {
        // RxCL_E_IMAGES_INCOMPATIBLE has its own dedicated handler — must NOT
        // be classified as a comm-layer error.
        var ex = new InvalidOperationException(
            "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.");
        Assert.False(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    [Fact]
    public void IsCommLayerError_UnrelatedMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException("controller not reachable");
        Assert.False(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    [Fact]
    public void IsCommLayerError_EmptyMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException(string.Empty);
        Assert.False(SdkCommErrorClassifier.IsCommLayerError(ex));
    }

    [Fact]
    public void IsCommLayerError_NeitherOuterNorInnerMatches_ReturnsFalse()
    {
        var inner = new InvalidOperationException("some inner error");
        var outer = new Exception("some outer error", inner);

        Assert.False(SdkCommErrorClassifier.IsCommLayerError(outer));
    }

    // ── BuildProjectNotCorrelatedError ────────────────────────────────────────

    [Fact]
    public void BuildProjectNotCorrelatedError_ReturnsExceptionWithProjectNotCorrelatedKind()
    {
        var sdkEx = new InvalidOperationException("RxE_COMM_DTLERROR");
        var result = SdkCommErrorClassifier.BuildProjectNotCorrelatedError(
            sdkEx, "C:\\Projects\\MyController.ACD");

        Assert.Equal(TagServiceErrorKind.ProjectNotCorrelated, result.Error.Kind);
    }

    [Fact]
    public void BuildProjectNotCorrelatedError_MessageNamesProjectFile()
    {
        const string path  = "C:\\Projects\\MyController.ACD";
        var sdkEx  = new InvalidOperationException("RxE_COMM_DTLERROR");
        var result = SdkCommErrorClassifier.BuildProjectNotCorrelatedError(sdkEx, path);

        Assert.Contains(path, result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildProjectNotCorrelatedError_MessageNamesCommPathRoute()
    {
        var sdkEx  = new InvalidOperationException("RxE_COMM_DTLERROR");
        var result = SdkCommErrorClassifier.BuildProjectNotCorrelatedError(
            sdkEx, "C:\\MyController.ACD");

        // The error must direct the caller to the comm-path/upload route.
        Assert.Contains("comm-path", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildProjectNotCorrelatedError_MessageNamesLogixConnect()
    {
        var sdkEx  = new InvalidOperationException("RxE_COMM_DTLERROR");
        var result = SdkCommErrorClassifier.BuildProjectNotCorrelatedError(
            sdkEx, "C:\\MyController.ACD");

        Assert.Contains("logix_connect", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildProjectNotCorrelatedError_InnerExceptionIsOriginalSdkException()
    {
        var sdkEx  = new InvalidOperationException("RxE_COMM_DTLERROR");
        var result = SdkCommErrorClassifier.BuildProjectNotCorrelatedError(
            sdkEx, "C:\\MyController.ACD");

        Assert.Same(sdkEx, result.InnerException);
    }

    [Fact]
    public void BuildProjectNotCorrelatedError_MessageQuotesSdkCode()
    {
        var sdkEx  = new InvalidOperationException("RxE_COMM_DTLERROR details here");
        var result = SdkCommErrorClassifier.BuildProjectNotCorrelatedError(
            sdkEx, "C:\\MyController.ACD");

        // The SDK's verbatim message must be quoted so the operator can see it.
        Assert.Contains("RxE_COMM_DTLERROR", result.Error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
#endif
