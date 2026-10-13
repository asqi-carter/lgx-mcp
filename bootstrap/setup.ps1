<#
.SYNOPSIS
    Sets up lgx-mcp; provisions the Logix Designer SDK only for SDK builds.

.DESCRIPTION
    setup.ps1 is the only script a controls engineer needs to run after
    downloading the binary artifact.  It requires no dotnet build, no bash,
    no make, and no git.

    It orchestrates three steps in order:

      1. provision-sdk.ps1  -- Extracts the Rockwell CSClient DLL and
                              FtspAdapterLDSDK.exe from the CSClient NuGet
                              package into <AppDir>\sdk\vendor\, making the
                              SDK backend activatable at runtime.

      2. cold-install-gate.ps1 -- Launches LogixMcp.exe over stdio and drives
                                  it through the MCP protocol to confirm the
                                  artifact is release-ready:
                                    - MCP initialize handshake
                                    - logix_ping (status="ok" required)
                                    - logix_connect backend="sdk"
                                      (status="connected" required when
                                       sdk\vendor\ is provisioned)

      3. Service recovery     -- Configures Windows service failure actions for
                                 LdSdkService so the SCM auto-restarts the
                                 service after crashes (best-effort; requires
                                 Administrator elevation).  If the session is
                                 not elevated the step is skipped non-fatally
                                 and the exact sc failure command is printed for
                                 the operator to run manually.

    On success a summary is printed that names every file that was written
    into sdk\vendor\ and confirms the gate passed.

    Exit codes:
      0  Steps 1 and 2 completed successfully (step 3 is non-fatal).
      1  Step 1 or 2 failed (details printed to stderr by the sub-script).

    This script has no dependency on bash, make, or git.

.PARAMETER AppDir
    Path to the published application directory produced by
    'dotnet publish' or 'make publish-win'.  Must contain LogixMcp.exe and
    sdk\LogixMcp.SdkAdapter.dll.

.PARAMETER NupkgPath
    Optional.  Full path to the RockwellAutomation.LogixDesigner.CSClient
    .nupkg file.  When omitted, provision-sdk.ps1 searches the default
    Studio 5000 Public Documents location:

        %PUBLIC%\Documents\Studio 5000\Logix Designer SDK\dotnet\

.PARAMETER TimeoutSeconds
    Per-probe timeout in seconds forwarded to cold-install-gate.ps1.
    Default: 120.  Must be >= 60 -- cold FTSP token acquisition can take
    up to ~15 s.

.PARAMETER SkipServiceRecovery
    When set, skips the service recovery configuration step (Step 3) entirely.
    Useful when running setup in a context where LdSdkService is not installed.
    Step 3 is also skipped automatically on the no-SDK path (-NoSdk or a
    no-SDK build), so no Rockwell service is ever queried or modified.

.PARAMETER NoSdk
    Forces the no-SDK path: Step 1 (provision-sdk.ps1) is skipped even when
    sdk\LogixMcp.SdkAdapter.dll is present.  The same path is taken
    automatically when that dll is absent (a no-SDK build).  The cold-install
    gate and the remaining steps still run.

.EXAMPLE
    # Minimal usage -- defaults cover nupkg location and probe timeout:
    .\bootstrap\setup.ps1 -AppDir "src\LogixMcp\bin\Release\net10.0\win-x64\publish"

.EXAMPLE
    # Explicit nupkg path:
    .\bootstrap\setup.ps1 `
        -AppDir "C:\LogixMcp\publish" `
        -NupkgPath "C:\packages\RockwellAutomation.LogixDesigner.CSClient.2.2.1109.nupkg"

.EXAMPLE
    # Longer timeout for slow hardware:
    .\bootstrap\setup.ps1 `
        -AppDir "C:\LogixMcp\publish" `
        -TimeoutSeconds 60

.EXAMPLE
    # Skip service recovery (e.g. service not yet installed):
    .\bootstrap\setup.ps1 `
        -AppDir "C:\LogixMcp\publish" `
        -SkipServiceRecovery
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppDir,

    [Parameter(Mandatory = $false)]
    [string] $NupkgPath,

    [Parameter(Mandatory = $false)]
    [int] $TimeoutSeconds = 120,

    [Parameter(Mandatory = $false)]
    [switch] $SkipServiceRecovery,

    [Parameter(Mandatory = $false)]
    [switch] $NoSdk
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Resolve absolute path and locate sub-scripts relative to this file
# ---------------------------------------------------------------------------

$AppDir = [System.IO.Path]::GetFullPath($AppDir)

if (-not (Test-Path -LiteralPath $AppDir -PathType Container)) {
    Write-Host ''
    Write-Host "FAILED: AppDir not found: $AppDir" -ForegroundColor Red
    Write-Host '        Pass the folder that contains LogixMcp.exe (the publish output).' -ForegroundColor Red
    exit 1
}

$bootstrapDir    = $PSScriptRoot
$provisionScript = Join-Path $bootstrapDir 'provision-sdk.ps1'
$gateScript      = Join-Path $bootstrapDir 'cold-install-gate.ps1'

# cold-install-gate.ps1 is required on every path.  provision-sdk.ps1 is only
# required on the SDK path and is checked there (see Step 1).
if (-not (Test-Path -LiteralPath $gateScript -PathType Leaf)) {
    Write-Error @"
ERROR: Required bootstrap script not found.
       Expected: $gateScript

       setup.ps1 must be run from the bootstrap\ directory or with its full
       path, and cold-install-gate.ps1 must be alongside it.
"@
    exit 1
}

# ---------------------------------------------------------------------------
# Constants -- kept in sync with provision-sdk.ps1 and cold-install-gate.ps1
# ---------------------------------------------------------------------------

$VendorRelDir    = 'sdk\vendor'
$CsClientDllName = 'RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll'
$FtspAdapterName = 'FtspAdapterLDSDK.exe'
$AdapterRelPath  = 'sdk\LogixMcp.SdkAdapter.dll'

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

# SDK lane detection: a no-SDK build ships without sdk\LogixMcp.SdkAdapter.dll.
# -NoSdk forces the skip path even when the dll is present.
$adapterDll = Join-Path $AppDir $AdapterRelPath
$sdkPresent = Test-Path -LiteralPath $adapterDll -PathType Leaf
$skipProvision = $NoSdk -or (-not $sdkPresent)

# ---------------------------------------------------------------------------
# Banner
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '============================================================'
Write-Host '  LogixMcp -- Windows SDK Bootstrap'
Write-Host '============================================================'
Write-Host "  AppDir : $AppDir"
Write-Host ''

# ---------------------------------------------------------------------------
# Prerequisite check -- Microsoft Visual C++ 2015-2022 Redistributable (x64)
# The CIP backend's native library (plctag) needs vcruntime140.dll.  This is
# a warning only: the install continues because the SDK backend does not
# need it.  The runtime is deliberately NOT bundled.
# ---------------------------------------------------------------------------

$vcRuntimePath = Join-Path ([Environment]::GetFolderPath('System')) 'vcruntime140.dll'
if (-not (Test-Path -LiteralPath $vcRuntimePath -PathType Leaf)) {
    Write-Host '  WARNING: vcruntime140.dll was not found.' -ForegroundColor Yellow
    Write-Host '  The CIP library needs the Microsoft Visual C++ 2015-2022' -ForegroundColor Yellow
    Write-Host '  Redistributable (x64), which is not installed.' -ForegroundColor Yellow
    Write-Host '  Install it from Microsoft (vc_redist.x64.exe), then restart the' -ForegroundColor Yellow
    Write-Host '  MCP server. Studio 5000 installs it, so machines with Studio' -ForegroundColor Yellow
    Write-Host '  already have it. Continuing with the install.' -ForegroundColor Yellow
    Write-Host ''
}

# ---------------------------------------------------------------------------
# Step 1 -- Provision SDK vendor files
# ---------------------------------------------------------------------------

if ($skipProvision) {
    if ($NoSdk -and $sdkPresent) {
        Write-Host 'SDK lane: skipped (-NoSdk); skipping provision-sdk'
    } else {
        Write-Host 'SDK lane: not present (no-SDK build); skipping provision-sdk'
    }
    Write-Host ''
} else {
    if (-not (Test-Path -LiteralPath $provisionScript -PathType Leaf)) {
        Write-Error @"
ERROR: Required bootstrap script not found.
       Expected: $provisionScript

       provision-sdk.ps1 must be alongside setup.ps1 when installing the SDK
       lane.  Use -NoSdk to skip the SDK lane.
"@
        exit 1
    }

    # Version pre-check: fail before provisioning when the package is not the
    # version this build was made against.
    $versionNupkg = $null
    if ($NupkgPath) {
        if (Test-Path -LiteralPath $NupkgPath -PathType Leaf) { $versionNupkg = $NupkgPath }
    } else {
        $defaultNupkgDir = "$env:SystemDrive\Users\Public\Documents\Studio 5000\Logix Designer SDK\dotnet"
        $versionCandidate = Select-LatestNupkg -Candidates @(Get-ChildItem -LiteralPath $defaultNupkgDir -Filter 'RockwellAutomation.LogixDesigner.CSClient.*.nupkg' -File -ErrorAction SilentlyContinue)
        if ($versionCandidate) { $versionNupkg = $versionCandidate.FullName }
    }
    if ($versionNupkg) {
        $versionError = Get-NupkgVersionError -Path $versionNupkg -Required $RequiredSdkVersion
        if ($versionError) {
            Write-Host ''
            Write-Host "FAILED: $versionError" -ForegroundColor Red
            Write-Host '==> setup FAILED before step 1 (SDK version check)' -ForegroundColor Red
            exit 1
        }
    }

    Write-Host '------------------------------------------------------------'
    Write-Host '  Step 1 of 3: provision-sdk.ps1'
    Write-Host '------------------------------------------------------------'

    $provisionArgs = @{ AppDir = $AppDir }
    if ($NupkgPath) {
        $provisionArgs['NupkgPath'] = $NupkgPath
    }

    & $provisionScript @provisionArgs
    $provisionOk = $?

    if (-not $provisionOk) {
        Write-Host ''
        Write-Host '==> setup FAILED at step 1 (provision-sdk)' -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Step 2 -- Cold-install gate
# ---------------------------------------------------------------------------

Write-Host '------------------------------------------------------------'
Write-Host '  Step 2 of 3: cold-install-gate.ps1'
Write-Host '------------------------------------------------------------'

$gateArgs = @{ AppDir = $AppDir; TimeoutSeconds = $TimeoutSeconds }

& $gateScript @gateArgs
$gateOk = $?

if (-not $gateOk) {
    Write-Host ''
    Write-Host '==> setup FAILED at step 2 (cold-install gate)' -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# Step 3 -- Service recovery configuration (best-effort; requires elevation)
# ---------------------------------------------------------------------------

Write-Host '------------------------------------------------------------'
Write-Host '  Step 3 of 3: service recovery configuration'
Write-Host '------------------------------------------------------------'

$ServiceName        = 'LdSdkService'
$ScFailureCmd       = "sc failure $ServiceName reset= 300 actions= restart/5000/restart/10000/restart/30000"
$recoveryApplied    = $false
$recoveryPreserved  = $false

if ($SkipServiceRecovery) {
    Write-Host "  Skipped (SkipServiceRecovery switch was set)."
} elseif ($skipProvision) {
    # No-SDK path: no Rockwell service is configured or queried (no service-control call).
    Write-Host "  Skipped (no-SDK install; no Rockwell service is touched)."
} else {
    # Detect whether the current process is running with administrator privileges.
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isAdmin   = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Host ''
        Write-Host '  WARNING: Service recovery was NOT configured.' -ForegroundColor Yellow
        Write-Host '  The current session is not running as Administrator.' -ForegroundColor Yellow
        Write-Host '  To configure crash recovery for LdSdkService, run the' -ForegroundColor Yellow
        Write-Host '  following command from an elevated prompt:' -ForegroundColor Yellow
        Write-Host ''
        Write-Host "    $ScFailureCmd" -ForegroundColor Cyan
        Write-Host ''
    } else {
        # Query existing failure configuration before applying new settings.
        # An administrator may have set site-specific values; do not overwrite them.
        Write-Host "  Checking existing failure configuration for $ServiceName ..."
        $qfailureOutput = & sc.exe qfailure $ServiceName 2>&1
        $qfailureExit   = $LASTEXITCODE

        # Determine whether failure actions are already present.
        # sc qfailure reports "FAILURE_ACTIONS  : NONE" when no actions are set;
        # any other value (RESTART, RUN_PROCESS, REBOOT) means actions exist.
        $hasExistingActions = $false
        if ($qfailureExit -eq 0) {
            $qfailureText = ($qfailureOutput | Out-String)
            if ($qfailureText -match 'FAILURE_ACTIONS' -and
                $qfailureText -notmatch 'FAILURE_ACTIONS\s*:\s*NONE') {
                $hasExistingActions = $true
            }
        }

        if ($hasExistingActions) {
            Write-Host "  Existing failure actions detected; leaving site-specific configuration untouched."
            $recoveryPreserved = $true
        } else {
            Write-Host "  Applying failure actions to $ServiceName ..."

            # Use sc.exe explicitly to avoid conflict with the Set-Content alias 'sc'.
            $scOutput = & sc.exe failure $ServiceName reset= 300 actions= restart/5000/restart/10000/restart/30000 2>&1
            $scExit   = $LASTEXITCODE

            if ($scExit -ne 0) {
                Write-Host ''
                Write-Host "  WARNING: sc failure returned exit code $scExit." -ForegroundColor Yellow
                Write-Host '  Service recovery was NOT applied. Output:' -ForegroundColor Yellow
                Write-Host "    $scOutput" -ForegroundColor Yellow
                Write-Host '  You can apply it manually from an elevated prompt:' -ForegroundColor Yellow
                Write-Host ''
                Write-Host "    $ScFailureCmd" -ForegroundColor Cyan
                Write-Host ''
            } else {
                Write-Host "  Recovery actions applied: restart in 5 s / 10 s / 30 s (reset window: 300 s)."
                $recoveryApplied = $true
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

$vendorDir       = Join-Path $AppDir $VendorRelDir
$csClientDest    = Join-Path $vendorDir $CsClientDllName
$ftspAdapterDest = Join-Path $vendorDir $FtspAdapterName

if ($SkipServiceRecovery) {
    $recoveryStatus = 'skipped (-SkipServiceRecovery)'
} elseif ($skipProvision) {
    $recoveryStatus = 'skipped (no-SDK install; no Rockwell service touched)'
} elseif ($recoveryPreserved) {
    $recoveryStatus = 'preserved (site-specific configuration detected)'
} elseif ($recoveryApplied) {
    $recoveryStatus = 'applied'
} else {
    $recoveryStatus = 'not applied (run as Administrator to configure)'
}

Write-Host ''
Write-Host '============================================================'
Write-Host '  Setup complete'
Write-Host '============================================================'
Write-Host ''
if ($skipProvision) {
    Write-Host '  SDK lane         : not provisioned (no-SDK path); no files installed into sdk\vendor\'
} else {
    Write-Host '  Files installed into sdk\vendor\:'
    Write-Host "    $csClientDest"
    Write-Host "    $ftspAdapterDest"
}
Write-Host ''
Write-Host '  Gate result      : PASSED'
Write-Host "  Recovery actions : $recoveryStatus"
Write-Host "  Artifact dir     : $AppDir"
Write-Host ''
Write-Host '  LogixMcp is ready.  Register it with your MCP client:'
Write-Host "    .\bootstrap\setup-mcp-client.ps1 -Client ClaudeCode"
Write-Host ''
