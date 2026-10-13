# Claude Desktop — setup

**Audience.** A controls engineer running everything on one Windows box —
the published lgx-mcp server and Claude Desktop.

lgx-mcp speaks MCP over **stdio**, not HTTP — there is no local port to
configure and no bearer token, because there is no network listener at all.
Claude Desktop spawns the published binary directly as a child process.

## Which registration answers

Claude Desktop's **Code tab** and **Cowork** both use the registration in
Claude Desktop's config file (this page). The separate Claude Code registration
(`claude mcp add`, see [install-claude-code.md](install-claude-code.md)) is used
only by the standalone `claude` CLI. **Env flags such as
`LGX_ENABLE_LIVE_WRITES` must be set on the registration the client actually
uses**: for the Code tab and Cowork, that is the `env` block in
`claude_desktop_config.json`, not the Claude Code registration.

**Quit Claude Desktop before installing, then start it.** Desktop holds
`LogixMcp.exe` open and rewrites its config on exit, which would discard the
registration.

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
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client ClaudeDesktop -AppDir <path> -WriteConfig
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

A CIP read of a tag that does not exist returns `status: "not_found"`. That
result carries no `sdkError` label; `sdkError` is set only by the SDK lane.

## Config file location

| Build | Path |
|---|---|
| Standalone `.exe` build | `%APPDATA%\Claude\claude_desktop_config.json` |
| Windows Store (MSIX) build | `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json` — writing to `%APPDATA%` is silently ignored by this build |

## Config shape

```json
{
  "mcpServers": {
    "lgx-mcp": {
      "command": "C:\\path\\to\\published\\LogixMcp.exe",
      "args": []
    }
  }
}
```

Adjust the path to wherever `bootstrap/setup.ps1` published the binary. This
config leaves live writes **off** (static analysis and gate derivation only).
Or run `bootstrap/setup-mcp-client.ps1 -Client ClaudeDesktop -WriteConfig` to
merge this block into the config file for you (it backs up the prior file;
without `-WriteConfig` it only prints the block).

### Enable live writes (separate step)

> **Warning:** this lets the agent write tags on a live controller. Gate
> denials still apply, but a granted write changes a running process. Do this
> only on a controller you are allowed to write to, and prefer an emulator.

Add an `env` block that passes `LGX_ENABLE_LIVE_WRITES=1` to the server process:

```json
{
  "mcpServers": {
    "lgx-mcp": {
      "command": "C:\\path\\to\\published\\LogixMcp.exe",
      "args": [],
      "env": {
        "LGX_ENABLE_LIVE_WRITES": "1"
      }
    }
  }
}
```

The installer equivalent is `bootstrap/setup-mcp-client.ps1 -Client ClaudeDesktop -EnableLiveWrites -WriteConfig`.
Quit Claude Desktop before editing the config (fully quit from the system
tray, not just close the window), then start it again afterwards. Desktop
rewrites the config on exit, so an edit made while it is running can be
overwritten; restarting after the edit is too late.

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
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client ClaudeDesktop -AppDir <path> -EnableStudioAutomation -PublishMode Auto -WriteConfig
```

These are added to the `env` block of the `lgx-mcp` entry.

A no-SDK build (no `sdk\LogixMcp.SdkAdapter.dll` under `-AppDir`) rejects both
switches with an error, because the variables would have no effect there. See the
[README](../README.md#logix-designer-sdk-lane) and
[`docs/safety-model.md`](safety-model.md#the-sdk-lane-online-edits-and-studio-automation).

## Verify

Under Settings → Developer → Local MCP servers, confirm `lgx-mcp` shows
`running`. Ask Claude to run `logix_ping`.

## Cowork

A server registered in Claude Desktop's config surfaces into Cowork
automatically as a bridged local device — no separate Cowork-side
registration is needed. As with any local MCP server bridged into Cowork's
VM sandbox, "Skip all approvals" does not apply to it (that control only
covers tools running inside the VM); use per-tool "Always allow" instead.

**After any server rebuild or restart, fully restart Claude Desktop**, then
start a new Cowork conversation — a stdio child process that exits does not
automatically get reconnected by a live session.

## Live controller access

As with every client, live tag writes
require `LGX_ENABLE_LIVE_WRITES=1` in the server process's environment. Set
it in the `env` block shown in [Enable live writes](#enable-live-writes-separate-step) (or as a user-level environment variable
before starting Claude Desktop); otherwise static analysis and gate derivation work with no additional configuration.
See [`docs/safety-model.md`](safety-model.md#live-writes-gate).

## Cross-references

- [`docs/tool-reference.md`](tool-reference.md)
- [`docs/troubleshooting.md`](troubleshooting.md)
