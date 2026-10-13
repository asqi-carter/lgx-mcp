<#
.SYNOPSIS
    One-command install of lgx-mcp from a git clone: prerequisites, build,
    install, self-check and MCP client registration.

.DESCRIPTION
    Run from a regular PowerShell window in the root of a clone:

        git clone https://github.com/asqi-carter/lgx-mcp.git
        cd lgx-mcp
        powershell -ExecutionPolicy Bypass -File bootstrap\install.ps1

    Needs only PowerShell and winget. No make, bash or Python.

    Steps:
      1. .NET 10 SDK: installed with winget if missing.
      2. Logix Designer SDK package: found in its default install location
         (or -NupkgPath) and copied into .localfeed\ for the build. If it is
         not found, the build falls back to the no-SDK build (static
         analysis and CIP tag access; no rung edits, Test or Assemble).
      3. Build: dotnet publish, self-contained win-x64.
      4. Rockwell guard: the build output must hold no RockwellAutomation*
         file. Nothing from Rockwell is redistributed.
      5. Install: the build output is copied to -InstallDir
         (default %LOCALAPPDATA%\Programs\lgx-mcp).
      6. setup.ps1: provisions the SDK files from your own package into
         <InstallDir>\sdk\vendor\ and runs the cold-install gate.
      7. setup-mcp-client.ps1: registers lgx-mcp with -Client (default
         ClaudeDesktop; Cowork uses the Claude Desktop registration).

    Re-running is safe: it rebuilds and reinstalls in place. Quit your MCP
    client first, because it holds LogixMcp.exe open while it runs.

.PARAMETER Client
    ClaudeDesktop (default), ClaudeCode, VSCode, or None to skip registration.

.PARAMETER EnableLiveWrites
    Lets the agent write tags on a live controller (LGX_ENABLE_LIVE_WRITES=1).
    Off by default. Prefer an emulator.

.PARAMETER EnableStudioAutomation
    SDK build only. Lets the agent drive Test, Assemble, Untest and Cancel in
    the Studio 5000 window you already have open (LGX_ENABLE_STUDIO_AUTOMATION=1).
    Each call still needs confirm=true. Off by default.

.PARAMETER NoSdk
    Build and install the no-SDK build even when the Logix Designer SDK is
    installed.

.PARAMETER NupkgPath
    Path to RockwellAutomation.LogixDesigner.CSClient.*.nupkg when it is not in
    the default Logix Designer SDK location.

.PARAMETER Force
    Continue even when Claude Desktop is running (-Client ClaudeDesktop checks for it
    at the start, before the build). The registration step then also writes over a
    running Claude Desktop, which rewrites its config on exit and may discard it.

.PARAMETER InstallDir
    Where the built server is installed. Default %LOCALAPPDATA%\Programs\lgx-mcp.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File bootstrap\install.ps1 -EnableLiveWrites -EnableStudioAutomation
#>
[CmdletBinding()]
param(
    [ValidateSet('ClaudeDesktop', 'ClaudeCode', 'VSCode', 'None')]
    [string] $Client = 'ClaudeDesktop',
    [switch] $EnableLiveWrites,
    [switch] $EnableStudioAutomation,
    [switch] $NoSdk,
    [switch] $Force,
    [string] $NupkgPath,
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\lgx-mcp'),
    # Test seam: replaces the live process table for the Claude Desktop running check.
    [Parameter(DontShow)] [object[]] $ProcessList
)

$ErrorActionPreference = 'Stop'

function Step([string] $n, [string] $text) { Write-Host ""; Write-Host "[$n] $text" -ForegroundColor Cyan }
function Ok([string] $text) { Write-Host "    OK  $text" -ForegroundColor Green }
function Note([string] $text) { Write-Host "    $text" }
function Fail([string] $text) { Write-Host ""; Write-Host "FAILED: $text" -ForegroundColor Red; exit 1 }

# SDK package version lgx-mcp is built against.  Keep in sync with the
# RockwellAutomation.LogixDesigner.CSClient PackageReference in
# src\LogixMcp.SdkAdapter\LogixMcp.SdkAdapter.csproj.  Supporting several SDK
# versions is out of scope.
$RequiredSdkVersion = '2.2.1109'

function Get-NupkgVersion {
    # Returns the package version recorded in the .nuspec inside the nupkg (a zip),
    # or $null when the archive cannot be read or its nuspec carries no version.
    # The file name is deliberately NOT consulted: it can be renamed or wrong.
    param([string] $Path)
    $zip = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $entry = $zip.Entries | Where-Object { $_.FullName -notmatch '[\\/]' -and $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $xml = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }
        $v = $xml.package.metadata.version
        if ($v) { return ([string] $v).Trim() }
    } catch {
        return $null
    } finally {
        if ($zip) { $zip.Dispose() }
    }
    return $null
}

function Get-NupkgNuspecField {
    # Returns one metadata field (e.g. 'id', 'version') from the .nuspec inside the
    # nupkg, or $null when the archive or the field cannot be read.
    param([string] $Path, [string] $Field)
    $zip = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $entry = $zip.Entries | Where-Object { $_.FullName -notmatch '[\\/]' -and $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $xml = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }
        $v = $xml.package.metadata.$Field
        if ($v) { return ([string] $v).Trim() }
    } catch {
        return $null
    } finally {
        if ($zip) { $zip.Dispose() }
    }
    return $null
}

function Copy-NupkgToFeed {
    # Copies the nupkg into the feed directory under its canonical name
    # <id>.<version>.nupkg (both read from the nuspec, never from the file name),
    # then removes every other nupkg in the feed with the same package id, so a
    # renamed or older copy never piles up. Returns @{ Id; Version; Name; Path }.
    # When the nuspec carries no id or version the original file name is kept.
    param([string] $Path, [string] $Feed)
    New-Item -ItemType Directory -Force -Path $Feed | Out-Null
    $id  = Get-NupkgNuspecField -Path $Path -Field 'id'
    $ver = Get-NupkgNuspecField -Path $Path -Field 'version'
    $name = if ($id -and $ver) { "$id.$ver.nupkg" } else { [System.IO.Path]::GetFileName($Path) }
    $dest = Join-Path $Feed $name
    $same = (Test-Path -LiteralPath $dest) -and
            ([System.IO.Path]::GetFullPath($dest) -eq [System.IO.Path]::GetFullPath($Path))
    if (-not $same) { Copy-Item -LiteralPath $Path -Destination $dest -Force }
    if ($id) {
        foreach ($f in @(Get-ChildItem -LiteralPath $Feed -Filter '*.nupkg' -File -ErrorAction SilentlyContinue)) {
            if ($f.Name -eq $name) { continue }
            if ((Get-NupkgNuspecField -Path $f.FullName -Field 'id') -eq $id) {
                Remove-Item -LiteralPath $f.FullName -Force
            }
        }
    }
    return @{ Id = $id; Version = $ver; Name = $name; Path = $dest }
}

function ConvertTo-SdkVersion {
    # Parses a numeric dotted version into a [version] with all four components
    # set, so 2.2.1109 and 2.2.1109.0 are the same value. Returns $null when the
    # text is not a plain numeric version (e.g. carries a -prerelease suffix).
    param([string] $Text)
    if ($Text -notmatch '^\s*\d+(\.\d+){0,3}\s*$') { return $null }
    $p = @($Text.Trim().Split('.') | ForEach-Object { [int] $_ })
    while ($p.Count -lt 4) { $p += 0 }
    return [version]::new($p[0], $p[1], $p[2], $p[3])
}

function Compare-SdkVersion {
    # Numeric comparison: -1, 0 or 1. Returns $null when either side is not a
    # plain numeric version. Never compares as strings (2.2.999 < 2.2.1109).
    param([string] $Left, [string] $Right)
    $l = ConvertTo-SdkVersion $Left
    $r = ConvertTo-SdkVersion $Right
    if ($null -eq $l -or $null -eq $r) { return $null }
    return $l.CompareTo($r)
}

function Get-NupkgVersionError {
    # Returns $null when the package is the required version, else the failure message.
    param([string] $Path, [string] $Required)
    $found = Get-NupkgVersion -Path $Path
    if ($found -and (Compare-SdkVersion $found $Required) -eq 0) { return $null }
    $foundText = if ($found) { $found } else { 'unknown (no version in the package .nuspec)' }
    return ("Logix Designer SDK package version mismatch. Found $foundText, required $Required. " +
        "Package: $Path. " +
        "Use Logix Designer SDK $Required or the version in Directory.Build.props, then re-run.")
}

function Select-LatestNupkg {
    # From nupkg FileInfo objects, returns the one with the highest nuspec version
    # (numeric compare). Packages with no readable version sort last.
    param([object[]] $Candidates)
    $best = $null; $bestVer = $null
    foreach ($c in @($Candidates)) {
        $v = ConvertTo-SdkVersion (Get-NupkgVersion -Path $c.FullName)
        if ($null -eq $best -or ($null -ne $v -and ($null -eq $bestVer -or $v -gt $bestVer))) { $best = $c; $bestVer = $v }
    }
    return $best
}

# ---------------------------------------------------------------------------
# Get-LgxClaudeDesktopProcess
# Returns the processes that mean Claude Desktop is running: any process whose
# image is Claude.exe under %LOCALAPPDATA%\AnthropicClaude\ or in the Microsoft Store
# (MSIX) Claude package (...\WindowsApps\<publisher>Claude_...), or a process
# named Claude/claude whose path cannot be read.  The Claude Code CLI
# (~\.local\bin\claude.exe) is deliberately ignored.  Claude
# Desktop keeps claude_desktop_config.json open in memory and rewrites it on
# exit, so a write made while it runs is lost.
# -ProcessList makes the process list injectable (objects with Name, Id, Path);
# when omitted the live process table is used (or LGX_MCP_PROCESS_LIST_JSON).
# ---------------------------------------------------------------------------

function Get-LgxClaudeDesktopProcess {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $false)]
        [object[]] $ProcessList
    )

    # Test seam for `pwsh -File` runs, where -ProcessList cannot be passed:
    # LGX_MCP_PROCESS_LIST_JSON holds a JSON array of {Name, Id, Path} objects.
    if (-not $PSBoundParameters.ContainsKey('ProcessList') -and
        $null -ne $env:LGX_MCP_PROCESS_LIST_JSON) {
        $ProcessList = @($env:LGX_MCP_PROCESS_LIST_JSON | ConvertFrom-Json)
    } elseif (-not $PSBoundParameters.ContainsKey('ProcessList')) {
        $ProcessList = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
            $p = $null
            try { $p = $_.Path } catch { $p = $null }
            [pscustomobject] @{ Name = $_.ProcessName; Id = $_.Id; Path = $p }
        })
    }

    $hits = @()
    foreach ($proc in @($ProcessList)) {
        if ($null -eq $proc) { continue }
        $name = if ($proc.PSObject.Properties['Name']) { [string] $proc.Name } else { '' }
        $path = if ($proc.PSObject.Properties['Path']) { [string] $proc.Path } else { '' }
        $name = $name -replace '\.exe$', ''
        # Match by executable path so the native Claude Code CLI (claude.exe, e.g.
        # ~\.local\bin\claude.exe) is not mistaken for Claude Desktop.  A process
        # named Claude whose path is unreadable is still counted (fail safe).
        # Only the Claude.exe image itself counts: helpers shipped inside the
        # package (e.g. cowork-svc.exe, a long-lived service) are not Desktop.
        $isClaudeExe   = $path -match '[\\/]Claude\.exe$'
        $isDesktopPath = $isClaudeExe -and
                         ($path -match '[\\/]AnthropicClaude[\\/]' -or
                          $path -match '[\\/]WindowsApps[\\/][^\\/]*Claude_')
        $isClaudeName  = $name -ceq 'Claude' -or $name -ceq 'claude'
        if ($isDesktopPath -or ($isClaudeName -and [string]::IsNullOrWhiteSpace($path))) {
            $hits += $proc
        }
    }
    return , $hits
}

# ---------------------------------------------------------------------------
# Get-LgxClaudeDesktopRunningError
# Returns $null when Claude Desktop is not running (or -Force is given), else
# the refusal message.
# ---------------------------------------------------------------------------

function Get-LgxClaudeDesktopRunningError {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [object[]] $ProcessList,

        [Parameter(Mandatory = $false)]
        [switch] $Force
    )

    if ($Force) { return $null }
    $hits = if ($PSBoundParameters.ContainsKey('ProcessList')) {
        Get-LgxClaudeDesktopProcess -ProcessList $ProcessList
    } else {
        Get-LgxClaudeDesktopProcess
    }
    if (@($hits).Count -eq 0) { return $null }
    $ids = (@($hits) | ForEach-Object { if ($_.PSObject.Properties['Id']) { $_.Id } }) -join ', '
    $arrow = [string] [char] 0x2192
    return ("Claude Desktop is running (PID $ids). Quit Claude Desktop fully (tray $arrow Quit) and re-run." +
            " Claude Desktop rewrites its config on exit and would discard the change." +
            " Pass -Force to write anyway.")
}

# ---- 0. Claude Desktop must not be running ----------------------------------
# Checked before anything is built: Claude Desktop holds LogixMcp.exe open and
# rewrites claude_desktop_config.json on exit, which would undo the registration.
if ($Client -eq 'ClaudeDesktop') {
    $desktopError = if ($PSBoundParameters.ContainsKey('ProcessList')) {
        Get-LgxClaudeDesktopRunningError -ProcessList $ProcessList -Force:$Force
    } else {
        Get-LgxClaudeDesktopRunningError -Force:$Force
    }
    if ($desktopError) { Fail $desktopError }
}

$Repo = Split-Path -Parent $PSScriptRoot
$Csproj = Join-Path $Repo 'src\LogixMcp\LogixMcp.csproj'
if (-not (Test-Path -LiteralPath $Csproj)) {
    Fail "install.ps1 builds from a git clone and $Csproj is not there. To install a release zip instead, run bootstrap\setup.ps1 -AppDir <unpacked folder>."
}
Set-Location -LiteralPath $Repo
[System.IO.Directory]::SetCurrentDirectory($Repo)

# ---- 1. .NET 10 SDK ---------------------------------------------------------
Step 1 '.NET 10 SDK'
function Find-Dotnet {
    $cands = @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $cands = @($cmd.Source) + $cands }
    foreach ($c in $cands) {
        if ($c -and (Test-Path -LiteralPath $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks | Where-Object { $_ -match '^10\.' }) { return $c }
        }
    }
    return $null
}
$Dotnet = Find-Dotnet
if (-not $Dotnet) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail '.NET 10 SDK not found and winget is not available. Install the .NET 10 SDK from https://dotnet.microsoft.com/download and re-run.'
    }
    Note 'not found; installing with winget (Windows may ask for permission)'
    winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements
    $Dotnet = Find-Dotnet
    if (-not $Dotnet) { Fail '.NET 10 SDK still not found after winget install. Open a new PowerShell window and re-run.' }
}
Ok ("$Dotnet (" + ((& $Dotnet --list-sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1) -replace '\s.*$', '') + ')')

# ---- 2. Logix Designer SDK package -----------------------------------------
Step 2 'Logix Designer SDK package'
$Sdk = -not $NoSdk
$Nupkg = $null
if ($Sdk) {
    if ($NupkgPath) {
        if (-not (Test-Path -LiteralPath $NupkgPath)) { Fail "-NupkgPath '$NupkgPath' does not exist." }
        $Nupkg = Get-Item -LiteralPath $NupkgPath
    } else {
        $default = Join-Path $env:PUBLIC 'Documents\Studio 5000\Logix Designer SDK\dotnet'
        $Nupkg = Get-ChildItem -LiteralPath $default -Filter 'RockwellAutomation.LogixDesigner.CSClient.*.nupkg' -ErrorAction SilentlyContinue
        $Nupkg = Select-LatestNupkg -Candidates @($Nupkg)
    }
    if ($Nupkg) {
        # Version pre-check: must fail before anything is copied or restored.
        $versionError = Get-NupkgVersionError -Path $Nupkg.FullName -Required $RequiredSdkVersion
        if ($versionError) { Fail $versionError }
        $feed = Join-Path $Repo '.localfeed'
        $copied = Copy-NupkgToFeed -Path $Nupkg.FullName -Feed $feed
        $label = if ($copied.Id -and $copied.Version) { "$($copied.Id) $($copied.Version)" } else { $copied.Name }
        Ok "$label (copied into .localfeed\; it is gitignored and never shipped)"
    } else {
        $Sdk = $false
        Note 'Logix Designer SDK not found. Installing the no-SDK build: static analysis and'
        Note 'CIP tag access work; rung edits, Test and Assemble need the SDK. Install the'
        Note 'Logix Designer SDK (or pass -NupkgPath) and re-run to get them.'
    }
} else {
    Note '-NoSdk: building the no-SDK build'
}
if (-not $Sdk -and $EnableStudioAutomation) { Fail '-EnableStudioAutomation needs the SDK build.' }

# ---- 3. Build --------------------------------------------------------------
Step 3 'Build (dotnet publish, win-x64, self-contained)'
$Out = Join-Path $Repo ('src\LogixMcp\bin\Release\net10.0\win-x64\' + $(if ($Sdk) { 'publish' } else { 'publish-nosdk' }))
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
$props = @('-r', 'win-x64', '-c', 'Release', '-p:PublishSingleFile=true', '--nologo')
if ($Sdk) {
    & $Dotnet restore $Csproj -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableLogixSdk=true -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet restore failed (see above).' }
    & $Dotnet publish $Csproj @props --self-contained -p:EnableLogixSdk=true
} else {
    & $Dotnet publish $Csproj @props --self-contained -p:EnableLogixSdk=false -p:RestoreSources=https://api.nuget.org/v3/index.json -o $Out
}
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed (see above).' }
if (-not (Test-Path -LiteralPath (Join-Path $Out 'LogixMcp.exe'))) { Fail "LogixMcp.exe not found in $Out after the build." }
foreach ($d in 'bootstrap', 'bench') { New-Item -ItemType Directory -Force -Path (Join-Path $Out $d) | Out-Null }
Get-ChildItem -LiteralPath (Join-Path $Repo 'bootstrap') -Filter '*.ps1' |
    Where-Object { $Sdk -or $_.Name -ne 'provision-sdk.ps1' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Out 'bootstrap') -Force }
Get-ChildItem -LiteralPath (Join-Path $Repo 'bench') -Filter '*.ps1' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Out 'bench') -Force }
Ok $Out

# ---- 4. Rockwell guard -----------------------------------------------------
Step 4 'Rockwell guard'
$leak = @(Get-ChildItem -LiteralPath $Out -Recurse -File -Filter 'RockwellAutomation*' -ErrorAction SilentlyContinue)
if ($leak.Count -gt 0) { Fail ("Rockwell files found in the build output; not installing: " + (($leak | ForEach-Object { $_.FullName }) -join ', ')) }
Ok 'no RockwellAutomation* file in the build output'

# ---- 5. Install ------------------------------------------------------------
Step 5 "Install to $InstallDir"
$running = @(Get-Process LogixMcp -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    Fail "lgx-mcp is running from $InstallDir (PID $(($running | ForEach-Object { $_.Id }) -join ', ')). Fully quit your MCP client (for Claude Desktop: right-click its tray icon, Quit) and re-run."
}
if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $Out '*') -Destination $InstallDir -Recurse -Force
Ok (Join-Path $InstallDir 'LogixMcp.exe')

# ---- 6. setup.ps1: SDK provisioning and cold-install gate ------------------
Step 6 'setup.ps1 (SDK provisioning and self-check)'
$setupArgs = @{ AppDir = $InstallDir }
if (-not $Sdk) { $setupArgs['NoSdk'] = $true }
if ($Sdk -and $Nupkg) { $setupArgs['NupkgPath'] = $Nupkg.FullName }
& (Join-Path $InstallDir 'bootstrap\setup.ps1') @setupArgs
if ($LASTEXITCODE -ne 0) { Fail 'setup.ps1 reported a failure (see above).' }
Ok 'self-check passed'

# ---- 7. MCP client registration --------------------------------------------
Step 7 "Register with $Client"
if ($Client -eq 'None') {
    Note 'skipped (-Client None). Register later with bootstrap\setup-mcp-client.ps1.'
} else {
    $clientArgs = @{ Client = $Client; AppDir = $InstallDir; WriteConfig = $true }
    if ($Force) { $clientArgs['Force'] = $true }
    if ($EnableLiveWrites) { $clientArgs['EnableLiveWrites'] = $true }
    if ($EnableStudioAutomation) { $clientArgs['EnableStudioAutomation'] = $true }
    & (Join-Path $InstallDir 'bootstrap\setup-mcp-client.ps1') @clientArgs
    if ($LASTEXITCODE -ne 0) { Fail 'setup-mcp-client.ps1 reported a failure (see above).' }
    Ok "registered as lgx-mcp"
}

Write-Host ""
Write-Host "lgx-mcp is installed ($(if ($Sdk) { 'SDK build' } else { 'no-SDK build' }))." -ForegroundColor Green
Write-Host "  Live writes:        $(if ($EnableLiveWrites) { 'ON' } else { 'off (re-run with -EnableLiveWrites)' })"
if ($Sdk) { Write-Host "  Studio automation:  $(if ($EnableStudioAutomation) { 'ON' } else { 'off (re-run with -EnableStudioAutomation)' })" }
if ($Client -eq 'ClaudeDesktop') {
    Write-Host ""
    Write-Host "Next:"
    Write-Host "  1. Start Claude Desktop (it had to be quit before this install). If you started it since, quit it fully from the tray icon, then start it."
    Write-Host "     The Code tab and Cowork use this Claude Desktop registration; the Claude Code registration is used only by the standalone 'claude' CLI."
    Write-Host "  2. Settings > Developer: lgx-mcp shows 'running'. Ask Claude to run logix_doctor."
    Write-Host "  3. Cowork: start a new task; the lgx-mcp tools are there. Use 'Always allow' per tool."
}
exit 0
