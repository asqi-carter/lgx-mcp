# lgx-mcp

Thanks for your interest in contributing. This guide covers how to build and
test lgx-mcp, how to run it safely against FactoryTalk Logix Echo or a
controller, how to report a verification, and the conventions the code follows.

For what the server does, see the [README](README.md). For how writes are
gated, see the [safety model](docs/safety-model.md).

## Prerequisites

- **.NET 10 SDK**
- **`make`**
- **`tar`**
- **`python3`** (for the release packaging script)
- **`pwsh`** (PowerShell 7). `make test` uses it to parse-check the
  PowerShell scripts under `bootstrap/`.

No Rockwell software is needed to build or to run the tests.

## Build and test

```
make build     # build the solution
make test      # lint the PowerShell scripts, build, and run the tests
```

`make test` runs without a controller, an emulator or any Rockwell software.
Tests that need an Echo instance or the `ab_server` simulator are gated by
environment variables and are skipped by default. A skipped test has not
passed, so when you report test results give the number of passed, failed and
skipped tests, not only the passes.

To build the release zips from source, run this in bash (Linux, macOS, WSL or
Git Bash on Windows), not in PowerShell. It needs `make`, `bash`, `python3`
(which builds the zips with its `zipfile` module, so no `zip` command is
needed), `tar`, `sha256sum` (or `shasum`, which macOS ships) and the .NET 10 SDK:

```bash
make release-nosdk
```

This writes `dist/lgx-mcp-<version>-win-x64.zip`,
`dist/lgx-mcp-<version>-linux-x64.zip` and `dist/SHA256SUMS`.

## Running against Echo or a controller

lgx-mcp is safe to try without hardware, and each step below adds more risk
than the one before. Start at the top.

1. **No controller.** Open an `.L5X` file with `logix_open_project`, then use
   `logix_gate_report`, `logix_verify_interlock` and the other static analysis
   tools. Use `logix_connect backend="virtual"` to read and write tags against
   an in-memory model of the project.
2. **Echo.** FactoryTalk Logix Echo is a software emulator. Point
   `logix_connect backend="cip"` at it and use `logix_doctor` to check that
   it is reachable. Echo is the preferred target for development and
   testing. Rockwell documents that emulated controllers can behave
   differently from physical ones, so a result on Echo says nothing certain
   about a physical controller.
3. **A physical controller.** Do this only on a controller you are
   responsible for, and only when it is not running a process that can hurt
   people or equipment. Connect read-only first.

Live writes and controller mode requests are **off by default**. They need
`LGX_ENABLE_LIVE_WRITES=1` in the environment of the server process. Leave it
unset unless the step you are testing needs a write. Every write is checked
against the gate derived from the open project, and the gate has no override.
Before a live write, run `logix_gate_report` and confirm the tag is granted.

Back up the controller project and keep your `.L5X` file under version
control before you test anything that writes. The environment variables are
listed in the [README](README.md#environment-variables).

lgx-mcp is not a safety system. Do not use it, or anything you build with it,
to satisfy a safety function. See [SECURITY.md](SECURITY.md).

## Reporting a verification

A verification report says that you ran a capability and what happened. Include:

- the version or commit you ran, and the command used to build or install it
- the target: Echo (with its version) or a physical controller (with its model
  and firmware revision), and whether the project was an `.L5X` file you
  exported yourself
- the steps you ran, the exact tool calls or commands, and the output or log of
  each. State which steps you did **not** run.
- what you expected and what you saw. Quote the actual output. A truncated log,
  or an exit code with no output, can hide the real result.
- whether your project file was left byte-for-byte unchanged

Only report a capability as verified if you ran it yourself. If you did not,
say that it is pending verification and give the command someone with access
to the hardware should run. A result on Echo is a result on Echo. Say so, and
do not describe it as verified on a physical controller. The current list is
in [what's verified](docs/verified-on.md).

## Code conventions

- **Handlers are pure functions.** The logic layer has no protocol attributes.
  Anything a tool does must be reachable from a plain call in a test.
- **The gate has no in-band override.** No tool that enforces policy may accept
  a parameter that weakens it. `commandPrefixes` and `safetyPrefixes` exist on
  `logix_gate_report` only. Enforcement always uses `GateConfig.Default`. A
  test guards this, so do not add such a parameter to an enforcing path.
- **Errors are structured.** Return a typed error object with a
  machine-readable `kind`, and never let an unhandled exception reach the
  caller. An `"An error occurred invoking X"` message means an exception
  escaped, and that is a bug. The error kinds are listed in [errors](docs/errors.md).
- **Descriptions state gate behavior** and never promise semantics the
  transport lacks. CIP has no push, so `logix_poll` is described as
  poll-and-diff, never as a subscription.
- **Static analysis is labelled as such.** `logix_verify_interlock` shows that
  a contact exists in rung text. It cannot account for runtime execution, and
  it says so. Any future fault-detection rule carries the same caveat.
- **PowerShell scripts are ASCII only.** `make lint-ps` rejects any non-ASCII
  byte, because Windows PowerShell 5.1 reads them with a different default
  encoding from PowerShell 7.

## Before opening a pull request

- Run `make test` and report the passed, failed and skipped counts.
- If the change touches the CIP lane or `bootstrap/`, run it against Echo or a
  controller as described above, and say which steps you ran and which you did
  not.
- If you conclude that a capability is absent, say how you enumerated it.
  Look at the full output, not a truncated listing.
- Add a test for new behavior, and update the docs when you change what a tool
  does, takes or returns.
