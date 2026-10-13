# Claude Code — setup

**Audience.** A controls engineer bringing up Claude Code as the agent
frontend for lgx-mcp, running the published server as a local stdio process.

## Which registration answers

The registration on this page is used **only by the standalone `claude` CLI**.
Claude Desktop's Code tab and Cowork use the Claude Desktop registration
instead (see [install-claude-desktop.md](install-claude-desktop.md)), so
registering here does nothing for them. **Env flags such as
`LGX_ENABLE_LIVE_WRITES` must be set on the registration the client actually
uses**: `-e` on `claude mcp add` for the CLI, the `env` block in
`claude_desktop_config.json` for Desktop's Code tab and Cowork.

## Prerequisites

- lgx-mcp installed from a release zip, or built with `make release-nosdk`
  (run in bash, WSL or Git Bash; needs `make`, `bash`, `python3`, `sha256sum`
  or `shasum` and the .NET 10 SDK), then set up with `bootstrap/setup.ps1` (see the
  [README](../README.md#install-10-minutes)) — the cold-install gate should
  have passed. Running the tests needs `make`, `tar` and `pwsh`.
- Claude Code installed (`irm https://claude.ai/install.ps1 | iex` on
  Windows, or the platform-appropriate installer).
- **CIP backend (`backend="cip"`) only:** the Microsoft Visual C++ 2015–2022
  Redistributable (x64) (`vc_redist.x64.exe`). The CIP library's native
  component needs `vcruntime140.dll`. Machines with Studio 5000 already have
  it; a clean Windows install does not. `bootstrap/setup.ps1` warns if it is
  missing, and `logix_doctor` reports it in its `cipRuntime` check. Without it,
  `logix_connect backend="cip"` returns `CipRuntimeMissing`. The runtime is not
  bundled; install it from Microsoft.

## Windows: execution policy

If you downloaded the release zip, run `Unblock-File` on it before unpacking,
then run the installer with the execution policy bypassed for that one process:

```powershell
Unblock-File .\lgx-mcp-<version>-win-x64.zip
powershell -ExecutionPolicy Bypass -File bootstrap\setup.ps1 -AppDir <path>
```

`<path>` is the unpacked directory that contains `LogixMcp.exe`. Use the same
`-ExecutionPolicy Bypass` form for `bootstrap\setup-mcp-client.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client ClaudeCode -AppDir <path> -WriteConfig
```

## Register the server

lgx-mcp speaks MCP over stdio. From a shell where `claude` is on `PATH`:

```powershell
claude mcp add lgx-mcp --scope user -- <path-to-published>\LogixMcp.exe
```

`--scope user` registers the server for every project rather than just the
current directory. This registration leaves live writes **off**: static
analysis and gate derivation work, and live tag writes are refused (see
[Live controller access](#live-controller-access)).

On Linux/macOS, substitute the published binary path for your platform.

Or let `bootstrap/setup-mcp-client.ps1 -Client ClaudeCode -WriteConfig`
run this registration for you; without `-WriteConfig` it prints the command.

### Enable live writes (separate step)

> **Warning:** this lets the agent write tags on a live controller. Gate
> denials still apply, but a granted write changes a running process. Do this
> only on a controller you are allowed to write to, and prefer an emulator.

`claude mcp add` will not overwrite an existing registration, so remove the
one from the previous step first (this errors harmlessly if none exists), then
add it again with the env var:

```powershell
claude mcp remove lgx-mcp --scope user
claude mcp add lgx-mcp --scope user -e LGX_ENABLE_LIVE_WRITES=1 -- <path-to-published>\LogixMcp.exe
```

The installer equivalent is `bootstrap/setup-mcp-client.ps1 -Client ClaudeCode -EnableLiveWrites -WriteConfig`.

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
powershell -ExecutionPolicy Bypass -File bootstrap\setup-mcp-client.ps1 -Client ClaudeCode -AppDir <path> -EnableStudioAutomation -PublishMode Auto -WriteConfig
```

These become `-e LGX_ENABLE_STUDIO_AUTOMATION=1` and `-e LGX_PUBLISH_MODE=auto` on the `claude mcp add` command.

A no-SDK build (no `sdk\LogixMcp.SdkAdapter.dll` under `-AppDir`) rejects both
switches with an error, because the variables would have no effect there. See the
[README](../README.md#logix-designer-sdk-lane) and
[`docs/safety-model.md`](safety-model.md#the-sdk-lane-online-edits-and-studio-automation).

## Verify

```powershell
claude mcp list
```

Confirm `lgx-mcp` shows as connected, then ask Claude to run `logix_ping` —
it should return the server's identity and version with no further setup.

## First real call

Ask Claude to open a project:

> "Use lgx-mcp to open `<path-to-your-project>.L5X`, then show me the
> write-gate report."

This exercises the static-analysis path end to end with no controller
required and no opt-in environment variables needed.

## Live controller access

Live tag writes that can reach a controller
are refused until `LGX_ENABLE_LIVE_WRITES=1` is set in the environment the
server process runs in. Set it before starting Claude Code (or restart the
registered server after setting it) once you intend to point the server at a
real or emulated controller. See
[`docs/safety-model.md`](safety-model.md#live-writes-gate).

## Cross-references

- [`docs/tool-reference.md`](tool-reference.md) — every tool's parameters.
- [`docs/troubleshooting.md`](troubleshooting.md) — failure-mode index.
- [`docs/install-vscode.md`](install-vscode.md) — the same registration for
  VS Code / Copilot agent mode.
