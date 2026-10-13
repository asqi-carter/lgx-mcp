using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_controller_mode</c> on a CIP session reads the same ListIdentity status word
/// <c>logix_diagnose</c> decodes, so the two agree on mode and keyswitch (no network).
/// </summary>
public class CipControllerModeTests
{
    private static EmulatorIdentityInfo Identity(ushort status) =>
        new("Fake 5580 Controller", 38, 11, 1, 14, 0x55, 1, status);

    private static TagSessionService CipSession()
    {
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");
        return session;
    }

    private static DiagnoseHandler.IdentityReader Reader(ushort status) =>
        (_, _) => Task.FromResult(Identity(status));

    private static Task<byte[]> NoFault(string h, CancellationToken ct) => Task.FromResult(new byte[24]);

    // Status words: nibble (bits 7..4) = device state, bits 13..12 = keyswitch.
    // 0x3060 run/remote, 0x2060 run/program key, 0x1060 run/run key, 0x3070 program/remote,
    // 0x3450 faultedModes/remote, 0x3000 reserved device state.
    [Theory]
    [InlineData((ushort)0x3060, "Remote Run",     "remote")]
    [InlineData((ushort)0x1060, "Run",            "run")]
    [InlineData((ushort)0x2070, "Program",        "program")]
    [InlineData((ushort)0x3070, "Remote Program", "remote")]
    [InlineData((ushort)0x3450, "Faulted",        "remote")]
    [InlineData((ushort)0x3000, "Unknown",        "remote")]
    public async Task ControllerMode_OnCipSession_MatchesDiagnose(ushort status, string expectedMode, string expectedKeyswitch)
    {
        var session = CipSession();
        var modes   = new ControllerModeService();
        modes.Reset("Unknown");

        var mode = Assert.IsType<ControllerModeResult>(await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, modes, identityReader: Reader(status)));
        var diag = Assert.IsType<DiagnoseResult>(await DiagnoseHandler.ExecuteAsync(
            null, session, modes, Reader(status), (h, c) => NoFault(h, c)));

        Assert.Equal(expectedMode, mode.ControllerMode);
        Assert.Equal(expectedKeyswitch, mode.Keyswitch);
        Assert.Equal(expectedKeyswitch, diag.Status.Keyswitch);

        // Diagnose omits mode exactly when controller_mode reports Unknown; otherwise they agree.
        Assert.Equal(mode.ControllerMode == "Unknown" ? null : mode.ControllerMode, diag.Mode);
        Assert.Equal(mode.ControllerMode == "Unknown", mode.Note is not null);
    }

    [Fact]
    public async Task ControllerMode_OnCipSession_UnreadableIdentity_IsUnknownWithNote()
    {
        var session = CipSession();
        DiagnoseHandler.IdentityReader failing = (_, _) => throw new EmulatorIdentityException("unreachable");

        var mode = Assert.IsType<ControllerModeResult>(await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, new ControllerModeService(), identityReader: failing));

        Assert.Equal("Unknown", mode.ControllerMode);
        Assert.Contains("ListIdentity", mode.Note);
    }

    [Fact]
    public async Task ControllerMode_OnCipSession_JsonCarriesKeyswitch()
    {
        var result = await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, CipSession(), new ControllerModeService(), identityReader: Reader(0x3060));
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, result.GetType())).RootElement;

        Assert.Equal("Remote Run", doc.GetProperty("controllerMode").GetString());
        Assert.Equal("remote", doc.GetProperty("keyswitch").GetString());
    }
}
