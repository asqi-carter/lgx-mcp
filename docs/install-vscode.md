# VS Code — quick install

lgx-mcp speaks MCP over stdio. VS Code's native MCP support (consumed by
GitHub Copilot's agent mode) can spawn it directly as a local process — no
bridge, no port.

## Prerequisites

lgx-mcp installed from a release zip, or built with `make release-nosdk`
(run in bash, WSL or Git Bash; needs `make`, `bash`, `python3`, `sha256sum` or
`shasum` and the .NET 10 SDK; see the [README](../README.md#install-10-minutes)).
Running the tests needs `make`, `tar` and `pwsh`.

### Windows: execution policy

If you downloaded the release zip, run `Unblock-File` on it before unpacking,
then run the installer with the execution policy bypassed for that one process:

```powershell
Unblock-File .\lgx-mcp-<version>-win-x64.zip
powershell -ExecutionPolicy Bypass -File bootstrap\setup.ps1 -AppDir <path>
```

`<path>` is the unpacked directory that contains `LogixMcp.exe`. Use the same
`-ExecutionPolicy Bypass` form for `bootstrap\setup-mcp-client.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client VSCode -AppDir <path> -WriteConfig
```

### CIP: Visual C++ runtime

If you will use `backend="cip"` (live tag reads and writes over EtherNet/IP),
install the Microsoft Visual C++ 2015–2022 Redistributable (x64)
(`vc_redist.x64.exe`) on the machine that runs the server. The CIP library's
native component needs it. Studio 5000 installs it, so machines with Studio
already have it; a clean Windows install does not. Without it,
`logix_connect backend="cip"` returns `CipRuntimeMissing`. `bootstrap/setup.ps1`
warns when it is missing and `logix_doctor` reports it in its `cipRuntime`
check. See [Troubleshooting](troubleshooting.md#logix_connect-cip-fails-with-dll-was-not-found-cipruntimemissing).

## VS Code native MCP (GitHub Copilot agent mode)

VS Code reads MCP server definitions from an `mcp.json` file — workspace-
level at `.vscode/mcp.json`, or a user-level equivalent reachable from the
Command Palette (**MCP: Open User Configuration**). The current shape is a
top-level `servers` map:

```json
{
  "servers": {
    "lgx-mcp": {
      "type": "stdio",
      "command": "C:\\path\\to\\published\\LogixMcp.exe",
      "args": []
    }
  }
}
```

Adjust the path to wherever `bootstrap/setup.ps1` published the binary for
your platform. This config leaves live writes **off** (static analysis and
gate derivation only).

Or run `bootstrap/setup-mcp-client.ps1 -Client VSCode -WriteConfig` to merge
this block into the user-level `mcp.json` for you (without `-WriteConfig` it
only prints the block).

### Enable live writes (separate step)

> **Warning:** this lets the agent write tags on a live controller. Gate
> denials still apply, but a granted write changes a running process. Do this
> only on a controller you are allowed to write to, and prefer an emulator.

Add an `env` block that passes `LGX_ENABLE_LIVE_WRITES=1` to the server process:

```json
{
  "servers": {
    "lgx-mcp": {
      "type": "stdio",
      "command": "C:\\path\\to\\published\\LogixMcp.exe",
      "args": [],
      "env": {
        "LGX_ENABLE_LIVE_WRITES": "1"
      }
    }
  }
}
```

The installer equivalent is `bootstrap/setup-mcp-client.ps1 -Client VSCode -EnableLiveWrites -WriteConfig`.

### SDK lane switches (SDK build only)

In an SDK build (one that contains
`sdk\LogixMcp.SdkAdapter.dll`), `setup-mcp-client.ps1` also accepts the SDK-lane
switches. Both are optional and off by default:

- `-EnableStudioAutomation` sets `LGX_ENABLE_STUDIO_AUTOMATION=1`, which allows the
  experimental Studio 5000 automation tools (test, untest, assemble, cancel and
  accept pending edits). Each call still needs `confirm=true`.
- `-PublishMode Auto` sets `LGX_PUBLISH_MODE=auto`, so staged rung edits publish
  without a confirmation step. `-PublishMode Confirm` (the default) writes nothing
  and keeps the confirmation.

```powershell
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client VSCode -AppDir <path> -EnableStudioAutomation -PublishMode Auto -WriteConfig
```

These are added to the `env` block of the `lgx-mcp` entry.

A no-SDK build (no `sdk\LogixMcp.SdkAdapter.dll` under `-AppDir`) rejects both
switches with an error, because the variables would have no effect there. See the
[README](../README.md#logix-designer-sdk-lane) and
[`docs/safety-model.md`](safety-model.md#the-sdk-lane-online-edits-and-studio-automation).

> **Key names may have moved on.** VS Code's MCP config format has changed
> shape more than once since it shipped. If `servers` doesn't work, check
> VS Code's own MCP documentation (Command Palette → **MCP: Open User
> Configuration**) for the current key names before assuming the server
> itself is broken.

Once added, open the Command Palette → **MCP: List Servers** to confirm
`lgx-mcp` connects, then enable it for a Copilot Chat agent-mode session.
Tools surface as `logix_*` — see [`docs/tool-reference.md`](tool-reference.md)
for the full list.

## Claude Code inside the VS Code terminal/extension

If you're driving lgx-mcp via the Claude Code CLI (either in VS Code's
integrated terminal or the Claude Code extension), the config is identical
to the standalone CLI setup. See [`docs/install-claude-code.md`](install-claude-code.md).

## Cursor / Cline

Both consume a similar stdio MCP shape — a `command`/`args` entry naming
the published binary. Check each client's own MCP settings UI or config
file location (Cursor: Settings → MCP; Cline: its extension settings) for
where to paste the equivalent block.

## Live controller access

As with every client, live tag writes
require `LGX_ENABLE_LIVE_WRITES=1`, set in the `env` block above or in the
environment VS Code (or the terminal it spawns the server from) runs in. See
[`docs/safety-model.md`](safety-model.md#live-writes-gate).
