using System.Reflection;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="LibPlcTagService"/> covering construction and
/// configuration-validation behaviour only.  Live EtherNet/IP connections to a
/// real controller are explicitly out of scope; all tests run without network
/// access.
/// </summary>
public class LibPlcTagServiceTests
{
    // ── LibPlcTagNativeInit (single-file compatibility bootstrap) ─────────────

    /// <summary>
    /// Verifies that <see cref="LibPlcTagNativeInit.EnsureInitialized"/> completes
    /// without throwing, in both single-file and regular-deployment scenarios.
    /// Idempotency: multiple calls must not throw even if a DllImportResolver is
    /// already registered for the NativeImport assembly.
    /// </summary>
    [Fact]
    public void LibPlcTagNativeInit_EnsureInitialized_DoesNotThrow()
    {
        // Calling twice verifies idempotency (the internal _done flag prevents
        // duplicate DllImportResolver registration, which would throw).
        LibPlcTagNativeInit.EnsureInitialized();
        LibPlcTagNativeInit.EnsureInitialized();
    }

    /// <summary>
    /// Verifies that after <see cref="LibPlcTagNativeInit.EnsureInitialized"/>, the
    /// private field <c>libplctag.NativeImport.plctag.libraryAlreadyInitialized</c>
    /// is <see langword="true"/>.  This confirms that libplctag's internal extraction
    /// loop is suppressed and will never call <c>Assembly.CodeBase</c> (which throws
    /// <see cref="NotSupportedException"/> in single-file deployments).
    /// </summary>
    [Fact]
    public void LibPlcTagNativeInit_EnsureInitialized_SetsLibraryAlreadyInitialized()
    {
        LibPlcTagNativeInit.EnsureInitialized();

        // Reach into libplctag.NativeImport via reflection to confirm the flag.
        var nativeImportAsm = AppDomain.CurrentDomain.GetAssemblies()
            .Single(a => a.GetName().Name == "libplctag.NativeImport");
        var plctagType = nativeImportAsm.GetType("libplctag.NativeImport.plctag")!;
        var field = plctagType.GetField(
            "libraryAlreadyInitialized",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)field.GetValue(null)!);
    }

    /// <summary>
    /// Verifies that after <see cref="LibPlcTagNativeInit.EnsureInitialized"/>, the
    /// extracted native library file exists in the expected temp directory.
    /// </summary>
    [Fact]
    public void LibPlcTagNativeInit_EnsureInitialized_NativeLibraryFileExists()
    {
        LibPlcTagNativeInit.EnsureInitialized();

        var extractDir = System.IO.Path.GetDirectoryName(
            LibPlcTagNativeInit.ExtractedLibraryPath)!;

        // At least one plctag native library file must be present.
        Assert.True(System.IO.Directory.Exists(extractDir),
            $"Expected temp extraction directory to exist: {extractDir}");

        var files = System.IO.Directory.GetFiles(extractDir);
        Assert.NotEmpty(files);
    }


    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void LibPlcTagService_ImplementsITagService()
    {
        var svc = new LibPlcTagService();

        Assert.IsAssignableFrom<ITagService>(svc);
    }

    [Fact]
    public void LibPlcTagService_CanBeInstantiatedWithNoArguments()
    {
        // Must not throw.
        var svc = new LibPlcTagService();

        Assert.NotNull(svc);
    }

    // ── ConnectAsync validation ───────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_WithValidGateway_DoesNotThrow()
    {
        var svc = new LibPlcTagService();

        // Should complete without throwing even though no real PLC exists.
        await svc.ConnectAsync("198.51.100.10", "1,0");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConnectAsync_WithNullOrWhitespaceGateway_ThrowsConnectionFailed(
        string? gateway)
    {
        var svc = new LibPlcTagService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ConnectAsync(gateway!, "1,0"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ConnectAsync_EmptyPath_DoesNotThrow()
    {
        // An empty path is allowed; libplctag accepts it for single-slot chassis.
        var svc = new LibPlcTagService();

        await svc.ConnectAsync("10.0.0.1", string.Empty);
    }

    // ── DisconnectAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        var svc = new LibPlcTagService();

        // Must be safe to call without a prior ConnectAsync.
        await svc.DisconnectAsync();
    }

    [Fact]
    public async Task DisconnectAsync_AfterConnect_DoesNotThrow()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");

        await svc.DisconnectAsync();
    }

    // ── Pre-connect guard: ReadAsync ──────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_BeforeConnectAsync_ThrowsConnectionFailed()
    {
        var svc = new LibPlcTagService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("SomeTag"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_AfterDisconnect_ThrowsConnectionFailed()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");
        await svc.DisconnectAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("SomeTag"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── Pre-connect guard: WriteAsync ─────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_BeforeConnectAsync_ThrowsConnectionFailed()
    {
        var svc = new LibPlcTagService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("SomeTag", 42));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_AfterDisconnect_ThrowsConnectionFailed()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");
        await svc.DisconnectAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("SomeTag", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── Pre-connect guard: ListTagsAsync ──────────────────────────────────────

    [Fact]
    public async Task ListTagsAsync_BeforeConnectAsync_ThrowsConnectionFailed()
    {
        var svc = new LibPlcTagService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ListTagsAsync());

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── TypeMismatch: unsupported write value ─────────────────────────────────

    [Fact]
    public async Task WriteAsync_WithUnsupportedValueType_ThrowsTypeMismatch()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");

        // A plain string is not a supported PLC value type.
        // The TypeMismatch exception should be raised before any network I/O.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("SomeTag", "not-a-plc-value"));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Equal("SomeTag", ex.Error.TagName);
    }

    // ── ProtocolError mapping ─────────────────────────────────────────────────

    /// <summary>
    /// The libplctag statuses BadParam/BadConfig/BadData are wire/protocol failures, not
    /// value-conversion failures; they must map to ProtocolError and the
    /// message must carry the raw status name and the tag.
    /// </summary>
    [Theory]
    [InlineData(libplctag.Status.ErrorBadParam,  "ErrorBadParam")]
    [InlineData(libplctag.Status.ErrorBadConfig, "ErrorBadConfig")]
    [InlineData(libplctag.Status.ErrorBadData,   "ErrorBadData")]
    public void BadParam_MapsToProtocolError_NotTypeMismatch(
        libplctag.Status status, string statusName)
    {
        var inner = new libplctag.LibPlcTagException(status);

        var ex = LibPlcTagService.MapException(inner, status, "Motor1.Speed");

        Assert.Equal(TagServiceErrorKind.ProtocolError, ex.Error.Kind);
        Assert.NotEqual(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Contains(statusName, ex.Error.Message);
        Assert.Contains("Motor1.Speed", ex.Error.Message);
        Assert.Equal("Motor1.Speed", ex.Error.TagName);
    }

    /// <summary>
    /// The server's own pre-wire value conversion failures stay TypeMismatch.
    /// </summary>
    [Fact]
    public async Task ValueConversionFailure_StillTypeMismatch()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("SomeTag", "not-a-plc-value"));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    // ── TagServiceException carries structured error ───────────────────────────

    [Fact]
    public async Task TagServiceException_ErrorProperty_IsPopulated()
    {
        var svc = new LibPlcTagService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("AnyTag"));

        Assert.NotNull(ex.Error);
        Assert.NotEmpty(ex.Message);
    }

    // ── ConnectAsync + re-connect ─────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_CalledTwice_UpdatesGateway()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");

        // Second call should not throw and should update the stored gateway.
        await svc.ConnectAsync("10.0.0.5", "1,2");
    }

    // ── Type-driven reads (DecodeValue) ───────────────────────────────────────

    /// <summary>Little-endian byte-array backed buffer standing in for a libplctag tag.</summary>
    private sealed class BytesBuffer(params byte[] bytes) : LibPlcTagService.ITagBuffer
    {
        public int GetSize() => bytes.Length;
        public bool GetBit(int o) => (bytes[o / 8] & (1 << (o % 8))) != 0;
        public sbyte GetInt8(int o) => (sbyte)bytes[o];
        public byte GetUInt8(int o) => bytes[o];
        public short GetInt16(int o) => BitConverter.ToInt16(bytes, o);
        public ushort GetUInt16(int o) => BitConverter.ToUInt16(bytes, o);
        public int GetInt32(int o) => BitConverter.ToInt32(bytes, o);
        public uint GetUInt32(int o) => BitConverter.ToUInt32(bytes, o);
        public long GetInt64(int o) => BitConverter.ToInt64(bytes, o);
        public ulong GetUInt64(int o) => BitConverter.ToUInt64(bytes, o);
        public float GetFloat32(int o) => BitConverter.ToSingle(bytes, o);
        public double GetFloat64(int o) => BitConverter.ToDouble(bytes, o);
    }

    private static CipTypeResolution Atomic(string type) =>
        new(CipTypeKind.Atomic, type, CipTypeSource.Project, Array.Empty<string>());

    [Fact]
    public void WholeModuleRead_CannotBeTyped_GivesTypeUnknownWithModuleMessage_NotWriteDenied()
    {
        // The controller refuses a read of a whole module tag; the type resolver says why it cannot be typed.
        var refused = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.WriteDenied, "libplctag ErrorNotAllowed", "Local:1:I"));
        var unresolved = CipTypeResolution.Unresolved(
            "Module 'Local:1' (tag 'Local:1:I'): module I/O structure not in this export, so 'Local:1:I' cannot be typed.");

        var ex = LibPlcTagService.RemapReadDenial(refused, "Local:1:I", unresolved);

        Assert.Equal(TagServiceErrorKind.TypeUnknown, ex.Error.Kind);
        Assert.NotEqual(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains("module I/O structure not in this export", ex.Error.Message);
        Assert.Equal("Local:1:I", ex.Error.TagName);
    }

    [Fact]
    public void WholeModuleRead_TypedButRefused_IsProtocolError_NotWriteDenied()
    {
        var refused = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.WriteDenied, "libplctag ErrorNotAllowed", "Speed"));

        var ex = LibPlcTagService.RemapReadDenial(refused, "Speed", Atomic("REAL"));

        Assert.Equal(TagServiceErrorKind.ProtocolError, ex.Error.Kind);
    }

    [Fact]
    public void Read_Real_DecodesFloat_0x3FC00000_Is1point5()
    {
        var v = LibPlcTagService.DecodeValue("Speed", Atomic("REAL"),
            new BytesBuffer(0x00, 0x00, 0xC0, 0x3F));

        Assert.Equal("REAL", v.DataType);
        Assert.Equal(1.5f, Assert.IsType<float>(v.Value));
    }

    [Fact]
    public void Read_Sint_IsInteger_NotBool()
    {
        var v = LibPlcTagService.DecodeValue("Small", Atomic("SINT"), new BytesBuffer(0xFE));

        Assert.Equal("SINT", v.DataType);
        Assert.Equal((sbyte)-2, Assert.IsType<sbyte>(v.Value));
    }

    [Fact]
    public void Read_Bool_UsesBit0()
    {
        var v = LibPlcTagService.DecodeValue("Flag", Atomic("BOOL"), new BytesBuffer(0x01));

        Assert.True(Assert.IsType<bool>(v.Value));
    }

    [Fact]
    public void Read_SizeMismatch_TypeMismatch_NoValue()
    {
        // DINT resolved, but the controller returned 2 bytes.
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Count", Atomic("DINT"), new BytesBuffer(0x01, 0x00)));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Contains("4 byte", ex.Error.Message);
        Assert.Contains("2 byte", ex.Error.Message);
    }

    [Fact]
    public void Read_Unresolved_TypeUnknown()
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Mystery",
                CipTypeResolution.Unresolved("not declared"), new BytesBuffer(0, 0, 0, 0)));

        Assert.Equal(TagServiceErrorKind.TypeUnknown, ex.Error.Kind);
        Assert.Contains("open the project (logix_open_project) so member and structure types are known",
            ex.Error.Message);
    }

    [Fact]
    public void Read_Structure_StructureReadUnsupported_ListsMembers()
    {
        var res = new CipTypeResolution(CipTypeKind.Structure, "Probe_UDT", CipTypeSource.Project,
            new[] { "Count", "Speed", "Inner.Value" });

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeValue("Probe", res, new BytesBuffer(new byte[12])));

        Assert.Equal(TagServiceErrorKind.StructureReadUnsupported, ex.Error.Kind);
        Assert.Contains("Count", ex.Error.Message);
        Assert.Contains("Speed", ex.Error.Message);
        Assert.Contains("Inner.Value", ex.Error.Message);
        Assert.Contains("read members individually", ex.Error.Message);
    }

    [Fact]
    public async Task ReadAsync_NoResolver_NotConnected_StillConnectionFailed()
    {
        var svc = new LibPlcTagService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("X"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── Type-driven writes (EncodeValue) ──────────────────────────────────────

    /// <summary>Recording write buffer: captures every setter call so tests can assert on what was stored.</summary>
    private sealed class RecordingWriteBuffer(int size) : LibPlcTagService.ITagWriteBuffer
    {
        public List<(string Setter, object Value)> Writes { get; } = new();
        public int GetSize() => size;
        public void SetBit(int o, bool v) => Writes.Add((nameof(SetBit), v));
        public void SetInt8(int o, sbyte v) => Writes.Add((nameof(SetInt8), v));
        public void SetUInt8(int o, byte v) => Writes.Add((nameof(SetUInt8), v));
        public void SetInt16(int o, short v) => Writes.Add((nameof(SetInt16), v));
        public void SetUInt16(int o, ushort v) => Writes.Add((nameof(SetUInt16), v));
        public void SetInt32(int o, int v) => Writes.Add((nameof(SetInt32), v));
        public void SetUInt32(int o, uint v) => Writes.Add((nameof(SetUInt32), v));
        public void SetInt64(int o, long v) => Writes.Add((nameof(SetInt64), v));
        public void SetUInt64(int o, ulong v) => Writes.Add((nameof(SetUInt64), v));
        public void SetFloat32(int o, float v) => Writes.Add((nameof(SetFloat32), v));
        public void SetFloat64(int o, double v) => Writes.Add((nameof(SetFloat64), v));
    }

    [Fact]
    public void Write_IntegerJsonToReal_StoresFloat()
    {
        var buf = new RecordingWriteBuffer(4);

        LibPlcTagService.EncodeValue("Speed", Atomic("REAL"), 5, buf);

        var w = Assert.Single(buf.Writes);
        Assert.Equal(nameof(RecordingWriteBuffer.SetFloat32), w.Setter);
        Assert.Equal(5.0f, Assert.IsType<float>(w.Value));
    }

    [Fact]
    public void Write_FractionToDint_TypeMismatch_NothingWritten()
    {
        var buf = new RecordingWriteBuffer(4);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Count", Atomic("DINT"), 1.5, buf));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Contains("DINT accepts whole numbers", ex.Error.Message);
        Assert.Empty(buf.Writes);
    }

    [Fact]
    public void Write_OutOfRangeToSint_TypeMismatch()
    {
        var buf = new RecordingWriteBuffer(1);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Small", Atomic("SINT"), 300, buf));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Contains("-128", ex.Error.Message);
        Assert.Contains("127", ex.Error.Message);
        Assert.Empty(buf.Writes);
    }

    [Fact]
    public void Write_Unresolved_Refused_NothingWritten()
    {
        var buf = new RecordingWriteBuffer(4);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Mystery",
                CipTypeResolution.Unresolved("not declared"), 1, buf));

        Assert.Equal(TagServiceErrorKind.TypeUnknown, ex.Error.Kind);
        Assert.Empty(buf.Writes);
    }

    [Fact]
    public void Write_Structure_Refused_NothingWritten()
    {
        var buf = new RecordingWriteBuffer(12);
        var res = new CipTypeResolution(CipTypeKind.Structure, "Probe_UDT", CipTypeSource.Project,
            new[] { "Count", "Speed" });

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Probe", res, 1, buf));

        Assert.Equal(TagServiceErrorKind.StructureReadUnsupported, ex.Error.Kind);
        Assert.Empty(buf.Writes);
    }

    [Fact]
    public void Write_BoolRequiresJsonBoolean_AndIntToBoolRefused()
    {
        var buf = new RecordingWriteBuffer(1);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Flag", Atomic("BOOL"), 1, buf));
        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Empty(buf.Writes);

        LibPlcTagService.EncodeValue("Flag", Atomic("BOOL"), true, buf);
        Assert.Equal((nameof(RecordingWriteBuffer.SetBit), (object)true), Assert.Single(buf.Writes));
    }

    [Theory]
    [InlineData("SINT", 127, "SetInt8")]
    [InlineData("USINT", 255, "SetUInt8")]
    [InlineData("INT", -32768, "SetInt16")]
    [InlineData("UINT", 65535, "SetUInt16")]
    [InlineData("DINT", 42, "SetInt32")]
    [InlineData("UDINT", 4000000000L, "SetUInt32")]
    [InlineData("LINT", 42, "SetInt64")]
    [InlineData("ULINT", 42, "SetUInt64")]
    public void Write_Integer_UsesSetterOfResolvedType(string type, long value, string setter)
    {
        var size = type switch { "SINT" or "USINT" => 1, "INT" or "UINT" => 2, "DINT" or "UDINT" => 4, _ => 8 };
        var buf = new RecordingWriteBuffer(size);

        // A whole-valued double (JSON 42.0) is accepted for integer types.
        LibPlcTagService.EncodeValue("T", Atomic(type), (double)value, buf);

        Assert.Equal(setter, Assert.Single(buf.Writes).Setter);
    }

    // ── String input path: WriteTagHandler.ParseValue -> EncodeValue ──────────

    [Fact]
    public void WriteString_LargeIntegerToLint_StoredExactly()
    {
        var buf = new RecordingWriteBuffer(8);

        LibPlcTagService.EncodeValue("Big", Atomic("LINT"),
            WriteTagHandler.ParseValue("9007199254740993"), buf);

        Assert.Equal((nameof(RecordingWriteBuffer.SetInt64), (object)9007199254740993L), Assert.Single(buf.Writes));
    }

    [Fact]
    public void WriteString_MaxUlongToUlint_StoredExactly()
    {
        var buf = new RecordingWriteBuffer(8);

        LibPlcTagService.EncodeValue("Big", Atomic("ULINT"),
            WriteTagHandler.ParseValue("18446744073709551615"), buf);

        Assert.Equal((nameof(RecordingWriteBuffer.SetUInt64), (object)ulong.MaxValue), Assert.Single(buf.Writes));
    }

    [Fact]
    public void WriteString_MaxLongToLint_StoredExactly()
    {
        var buf = new RecordingWriteBuffer(8);

        LibPlcTagService.EncodeValue("Big", Atomic("LINT"),
            WriteTagHandler.ParseValue("9223372036854775807"), buf);

        Assert.Equal((nameof(RecordingWriteBuffer.SetInt64), (object)long.MaxValue), Assert.Single(buf.Writes));
    }

    [Fact]
    public void WriteString_UlongRangeToLint_TypeMismatch_NothingWritten()
    {
        var buf = new RecordingWriteBuffer(8);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Big", Atomic("LINT"),
                WriteTagHandler.ParseValue("18446744073709551615"), buf));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Empty(buf.Writes);
    }

    [Theory]
    [InlineData("REAL", 4, "SetFloat32")]
    [InlineData("LREAL", 8, "SetFloat64")]
    public void WriteString_IntegerLookingToReal_StoresFloat(string type, int size, string setter)
    {
        var buf = new RecordingWriteBuffer(size);

        LibPlcTagService.EncodeValue("Speed", Atomic(type), WriteTagHandler.ParseValue("5"), buf);

        Assert.Equal(setter, Assert.Single(buf.Writes).Setter);
    }

    [Fact]
    public void WriteString_FractionToDint_TypeMismatch_NothingWritten()
    {
        var buf = new RecordingWriteBuffer(4);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Count", Atomic("DINT"), WriteTagHandler.ParseValue("1.5"), buf));

        Assert.Contains("DINT accepts whole numbers", ex.Error.Message);
        Assert.Empty(buf.Writes);
    }

    [Fact]
    public void Write_BufferSizeDisagreesWithType_TypeMismatch_NothingWritten()
    {
        var buf = new RecordingWriteBuffer(2);

        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.EncodeValue("Count", Atomic("DINT"), 1, buf));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Empty(buf.Writes);
    }

    [Fact]
    public async Task WriteAsync_Unresolved_Refused_BeforeAnyNetworkIo()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync("198.51.100.1", "1,0");

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.WriteAsync("X", 1));

        Assert.Equal(TagServiceErrorKind.TypeUnknown, ex.Error.Kind);
    }

    // ── Source scan: no size-based type inference ────────────────────────────

    /// <summary>
    /// <c>GetSize()</c> may only be used as the size cross-check: the interface
    /// declarations, the thin libplctag adapters, and the
    /// <c>var size = buffer.GetSize();</c> sites that are immediately compared
    /// with the resolved type's expected size.  No type is inferred from it.
    /// </summary>
    [Fact]
    public void SourceScan_GetSizeOnlyInSizeCheck()
    {
        var dir = AppContext.BaseDirectory;
        string? file = null;
        while (dir is not null && file is null)
        {
            var candidate = Path.Combine(dir, "src", "LogixMcp", "LibPlcTagService.cs");
            if (File.Exists(candidate)) file = candidate;
            dir = Path.GetDirectoryName(dir);
        }
        Assert.True(file is not null, "Could not locate src/LogixMcp/LibPlcTagService.cs.");

        var lines = File.ReadAllLines(file!);
        var checkSites = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("//") || !line.Contains("GetSize", StringComparison.Ordinal)) continue;

            if (line == "int GetSize();" || line == "public int GetSize() => tag.GetSize();")
                continue; // interface declaration / adapter

            Assert.True(line == "var size = buffer.GetSize();",
                $"GetSize() used outside the size cross-check at line {i + 1}: {line}");
            Assert.Contains("size != expected", lines[i + 1], StringComparison.Ordinal);
            checkSites++;
        }
        Assert.Equal(3, checkSites); // DecodeValue, DecodeBit and ApplyValue

        var text = string.Join('\n', lines);
        Assert.DoesNotContain("ExtractTagValue", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidateAgainstTagSize", text, StringComparison.Ordinal);
        Assert.DoesNotContain("switch (size", text, StringComparison.Ordinal);
    }

    // ── Missing VC++ runtime (native load failure) ────────────────────────────

    [Fact]
    public void DllNotFound_CipRuntimeMissing_NoControllerAdvice()
    {
        var ex = LibPlcTagService.MapNativeLoadFailure(new DllNotFoundException("plctag"));

        Assert.Equal(TagServiceErrorKind.CipRuntimeMissing, ex.Error.Kind);
        Assert.Equal(
            "The CIP library could not load: its native component needs the Microsoft Visual C++ " +
            "2015\u20132022 Redistributable (x64), which is not installed.",
            ex.Error.Message);
        Assert.Equal(
            "Install the Microsoft Visual C++ x64 redistributable (vc_redist.x64.exe), " +
            "then restart the MCP server.",
            ex.Error.Hint);
        Assert.IsType<DllNotFoundException>(ex.InnerException);

        foreach (var text in new[] { ex.Error.Message, ex.Error.Hint! })
        {
            Assert.DoesNotContain("powered and reachable", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("controller", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ConnectProbe_DllNotFound_ReturnsCipRuntimeMissing_NoControllerAdvice()
    {
        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false,
            new ProjectService(), new TagSessionService(),
            backend: "cip",
            cipConnectProbe: (_, _) => throw new DllNotFoundException("plctag"));

        var err = Assert.IsType<ConnectCipProbeError>(result);
        Assert.Equal("CipRuntimeMissing", err.Kind);
        Assert.Contains("Visual C++", err.Error, StringComparison.Ordinal);
        Assert.Contains("vc_redist.x64.exe", err.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("powered and reachable", err.Error + err.Hint, StringComparison.OrdinalIgnoreCase);
    }
}
