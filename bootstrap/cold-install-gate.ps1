<#
.SYNOPSIS
    PowerShell stdio launch gate for the LogixMcp Windows publish artifact.
    Windows peer of scripts/cold-install-gate.sh.

.DESCRIPTION
    Accepts -AppDir (path to the publish tree produced by dotnet publish /
    make publish-win-nosdk), launches LogixMcp.exe over stdio, and drives it through
    the MCP protocol to verify the artifact is release-ready:

      1. MCP initialize handshake -- validates the server starts and responds.
      2. logix_ping -- requires status="ok" in the response.
      3. logix_open_project -- requires a success payload (controller field
           present) from the minimal L5X written to a temp file.  Any error
           or non-success response is a gate failure named as a
           logix_open_project failure, not a connect failure.
      4. When sdk\vendor\ has been provisioned (CSClient.dll is present):
           logix_connect backend="sdk" -- requires status="connected".
           A slow connect answers status="connecting"; the gate then polls
           logix_connect_status (printing the phase) until connected, failed,
           or -TimeoutSeconds is exceeded.
           A structured error or silent crash is never a passing outcome.

    Exit codes:
      0  All probes passed.
      1  Any probe failed (details printed to stderr).

    Failure conditions include:
      - LogixMcp.exe not found in AppDir
      - No MCP response within TimeoutSeconds
      - logix_ping did not return status="ok"
      - logix_open_project did not return a success payload (controller field
        present) -- failure is reported as a logix_open_project failure, not
        as an ambiguous SDK/connect error
      - SDK publish detected (sdk\LogixMcp.SdkAdapter.dll present) but
        sdk\vendor\ is not provisioned (CSClient.dll absent) -- run
        bootstrap\provision-sdk.ps1 first
      - logix_connect backend="sdk" reported status="failed" (or an error), or
        was still "connecting" when -TimeoutSeconds elapsed, when
        sdk\vendor\ is provisioned

    This script has no dependency on bash, make, or git.
    It is the mandatory gate before tagging a Windows release.

.PARAMETER AppDir
    Path to the published application directory produced by
    'dotnet publish' or 'make publish-win-nosdk'.  Must contain LogixMcp.exe.

.PARAMETER TimeoutSeconds
    Per-probe timeout in seconds.  Default: 120.
    Must be >= 60 -- a cold first token acquisition on a fresh install can be slow.

.PARAMETER LogixDesignerVersion
    Major version of Logix Designer to use (e.g. 28) instead of the automatic
    choice.  By default the newest installed major version is selected.  The
    gate fails with the list of installed versions if the requested major is
    not installed.

.PARAMETER LogixInstallRoot
    Studio 5000 Logix Designer install root to scan for installed versions.
    Default: C:\Program Files (x86)\Rockwell Software\Studio 5000\Logix Designer

.EXAMPLE
    # Run against the default win-x64 no-SDK publish output:
    .\bootstrap\cold-install-gate.ps1 `
        -AppDir "src\LogixMcp\bin\Release\net10.0\win-x64\publish-nosdk"

.EXAMPLE
    # Run against an already-provisioned publish tree with a longer timeout:
    .\bootstrap\cold-install-gate.ps1 `
        -AppDir "C:\LogixMcp\publish" `
        -TimeoutSeconds 180
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppDir,

    [Parameter(Mandatory = $false)]
    [int] $TimeoutSeconds = 120,

    [Parameter(Mandatory = $false)]
    [int] $LogixDesignerVersion = 0,

    [Parameter(Mandatory = $false)]
    [string] $LogixInstallRoot = 'C:\Program Files (x86)\Rockwell Software\Studio 5000\Logix Designer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Step 0 -- Self-identify: the very first line of output must name the
# resolved path of the script that is actually executing and the
# TimeoutSeconds value in effect.  This makes any captured log unambiguous
# about which copy of the script ran and which timeout was active -- e.g.
# when a stale copy is invoked from an unexpected PATH or working directory.
# ---------------------------------------------------------------------------

$ResolvedScriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
Write-Host "cold-install-gate.ps1: running from $ResolvedScriptPath, TimeoutSeconds=$TimeoutSeconds"

# ---------------------------------------------------------------------------
# Constants -- kept in sync with SdkAdapterLoader.cs and provision-sdk.ps1
# ---------------------------------------------------------------------------

# Relative path to the adapter DLL inside the publish tree (SDK builds only).
$AdapterRelPath = 'sdk\LogixMcp.SdkAdapter.dll'

# Relative path to the vendor directory populated by provision-sdk.ps1.
$VendorRelDir = 'sdk\vendor'

# The Rockwell CSClient DLL that provision-sdk.ps1 extracts into sdk\vendor\.
# Its presence indicates that the publish tree has been provisioned and the
# SDK backend can attempt to initialise its transport.
$CsClientDllName = 'RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll'

# MCP protocol version used for the initialize handshake.
$McpProtocolVersion = '2024-11-05'

# ---------------------------------------------------------------------------
# Step 1 -- Validate inputs
# ---------------------------------------------------------------------------

$AppDir = [System.IO.Path]::GetFullPath($AppDir)

if (-not (Test-Path -LiteralPath $AppDir -PathType Container)) {
    Write-Error @"
ERROR: AppDir does not exist or is not a directory.
       Checked: $AppDir

       Build the publish tree first:
           make publish-win-nosdk
       or:
           dotnet publish src/LogixMcp/LogixMcp.csproj -r win-x64 --self-contained ^
               -p:PublishSingleFile=true -p:EnableLogixSdk=false -c Release
"@
    exit 1
}

$exePath = Join-Path $AppDir 'LogixMcp.exe'

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Error @"
ERROR: LogixMcp.exe not found in AppDir.
       Expected: $exePath

       Build the publish tree first:
           make publish-win-nosdk
"@
    exit 1
}

if ($TimeoutSeconds -lt 60) {
    Write-Warning "TimeoutSeconds ($TimeoutSeconds) is below the recommended minimum of 60 s.`nCold first-token acquisition can be slow -- increase the timeout to avoid false failures."
}

# ---------------------------------------------------------------------------
# Step 2 -- Detect SDK build and vendor provisioning state
# ---------------------------------------------------------------------------

$adapterDll  = Join-Path $AppDir $AdapterRelPath
$isSdkBuild  = Test-Path -LiteralPath $adapterDll -PathType Leaf

$vendorDir      = Join-Path $AppDir $VendorRelDir
$csClientDll    = Join-Path $vendorDir $CsClientDllName
$isProvisioned  = $isSdkBuild -and (Test-Path -LiteralPath $csClientDll -PathType Leaf)

Write-Host '==> cold-install gate (PowerShell)'
Write-Host "    exe      : $exePath"
Write-Host "    sdk build: $isSdkBuild"
Write-Host "    vendor   : $(if ($isSdkBuild) { if ($isProvisioned) { 'provisioned' } else { 'NOT provisioned' } } else { 'n/a' })"
Write-Host "    timeout  : ${TimeoutSeconds}s"

# An SDK publish without a provisioned vendor directory is always a gate failure.
# The logix_connect backend="sdk" probe requires CSClient.dll to be present so
# it can exercise the full SDK activation path.  Without it the backend can never
# return status="connected", which is the required outcome.
if ($isSdkBuild -and -not $isProvisioned) {
    Write-Error @"
ERROR: SDK publish detected but sdk\vendor\ is not provisioned.
       Missing: $CsClientDllName
       Expected at: $csClientDll

       Run provision-sdk.ps1 first to extract the Rockwell binaries:
           .\bootstrap\provision-sdk.ps1 -AppDir "$AppDir"

       The cold-install gate requires status="connected" from
       logix_connect backend="sdk", which is only possible when
       $CsClientDllName is present in sdk\vendor\.
"@
    exit 1
}

# ---------------------------------------------------------------------------
# Step 3 -- MCP stdio probe helpers
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    Start LogixMcp.exe with redirected stdin/stdout/stderr.
    Returns the System.Diagnostics.Process handle.
#>
function Start-McpProcess {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $exePath
    $psi.WorkingDirectory       = $AppDir
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    # Strip LOGIX_SDK_PATH so the gate is a cold probe (no Rockwell SDK influence).
    # Snapshot the env dict first so the enumeration is stable.
    $envSnapshot = [System.Environment]::GetEnvironmentVariables()
    foreach ($kvp in $envSnapshot.GetEnumerator()) {
        if ($kvp.Key -ne 'LOGIX_SDK_PATH') {
            $psi.Environment[$kvp.Key] = $kvp.Value
        }
    }

    $p = [System.Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $p.Start() | Out-Null
    return $p
}

<#
.SYNOPSIS
    Send a single JSON-RPC message as an NDJSON line to the process stdin.
#>
function Send-McpMessage {
    param(
        [System.Diagnostics.Process] $Process,
        [hashtable] $Message
    )

    $json = $Message | ConvertTo-Json -Depth 10 -Compress
    $Process.StandardInput.WriteLine($json)
    $Process.StandardInput.Flush()
}

<#
.SYNOPSIS
    Recursively convert a PSCustomObject (returned by ConvertFrom-Json in
    Windows PowerShell 5.1) into a nested hashtable so that callers can use
    .ContainsKey() and ['key'] indexing identically to the output produced by
    ConvertFrom-Json -AsHashtable in PowerShell 6+.
    Arrays are preserved as arrays with each element converted in turn.
#>
function ConvertTo-Hashtable {
    param(
        [Parameter(ValueFromPipeline = $true)]
        $InputObject
    )
    process {
        if ($null -eq $InputObject)                                         { return $null }
        if ($InputObject -is [System.Collections.IDictionary])              { return $InputObject }
        if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
            $ht = @{}
            foreach ($prop in $InputObject.PSObject.Properties) {
                $ht[$prop.Name] = ConvertTo-Hashtable $prop.Value
            }
            return $ht
        }
        if ($InputObject -is [array]) {
            # Build into an explicit typed array so PowerShell cannot collapse a
            # single-element result to a scalar.  Return with the unary comma
            # operator so the caller receives an array even when Count -eq 1.
            [object[]] $out = foreach ($item in $InputObject) {
                ConvertTo-Hashtable $item
            }
            return ,$out
        }
        return $InputObject
    }
}

<#
.SYNOPSIS
    Read one NDJSON line from the process stdout within TimeoutSeconds.
    Returns the parsed hashtable on success, or $null on timeout / process exit.
#>
function Receive-McpMessage {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $Label = '',
        [int] $TimeoutMs = 0
    )

    $timeoutMs = if ($TimeoutMs -gt 0) { $TimeoutMs } else { $TimeoutSeconds * 1000 }

    # ReadLineAsync() returns a Task<string>.  We Wait() with the probe timeout
    # so the calling thread is not blocked indefinitely if the server stalls.
    # If the task faults (e.g. the pipe is broken), Wait() rethrows the exception
    # as an AggregateException -- catch it and treat it as a failure.
    $readTask = $Process.StandardOutput.ReadLineAsync()

    $completed = $false
    try {
        $completed = $readTask.Wait($timeoutMs)
    } catch [System.AggregateException] {
        Write-Host "FAIL${Label}: stdout read error: $($_.Exception.InnerException.Message)" -ForegroundColor Red
        return $null
    }

    if (-not $completed) {
        # Timed out -- report whether the process is still alive.
        if ($Process.HasExited) {
            Write-Host "FAIL${Label}: server exited unexpectedly (rc=$($Process.ExitCode))" -ForegroundColor Red
        } else {
            Write-Host "FAIL${Label}: no response within ${TimeoutSeconds}s -- server may have stalled" -ForegroundColor Red
            if ($isSdkBuild) {
                Write-Host "" -ForegroundColor Red
                Write-Host "      Likely root cause: on a fresh FactoryTalk Security installation the" -ForegroundColor Red
                Write-Host "      first FactoryTalk Security (FTSP) token acquisition can take one to" -ForegroundColor Red
                Write-Host "      two minutes.  This one-time cost is the most common cause of a" -ForegroundColor Red
                Write-Host "      cold-install timeout and does not by itself indicate a defect." -ForegroundColor Red
                Write-Host "" -ForegroundColor Red
                Write-Host "      First remediation step: re-run the gate with a higher -TimeoutSeconds," -ForegroundColor Red
                Write-Host "      e.g.:" -ForegroundColor Red
                Write-Host "          .\bootstrap\cold-install-gate.ps1 -AppDir `"$AppDir`" -TimeoutSeconds 180" -ForegroundColor Red
                Write-Host "" -ForegroundColor Red
                Write-Host "      If the probe still times out a second time at the ${TimeoutSeconds}s" -ForegroundColor Red
                Write-Host "      default, treat that as the trigger to investigate LdSdkService" -ForegroundColor Red
                Write-Host "      directly rather than retrying again:" -ForegroundColor Red
                Write-Host "          sc query LdSdkService" -ForegroundColor Red
                Write-Host "      and check the Windows Application event log for LdSdkService errors." -ForegroundColor Red
            } else {
                Write-Host "" -ForegroundColor Red
                Write-Host "      This is a no-SDK build: no Rockwell software is involved.  Likely causes:" -ForegroundColor Red
                Write-Host "      a slow or heavily loaded machine, antivirus scanning the first launch" -ForegroundColor Red
                Write-Host "      of LogixMcp.exe, a blocked or quarantined executable, or a missing" -ForegroundColor Red
                Write-Host "      runtime dependency that stops the server before it answers." -ForegroundColor Red
                Write-Host "" -ForegroundColor Red
                Write-Host "      First remediation step: re-run the gate with a higher -TimeoutSeconds," -ForegroundColor Red
                Write-Host "      e.g.:" -ForegroundColor Red
                Write-Host "          .\bootstrap\cold-install-gate.ps1 -AppDir `"$AppDir`" -TimeoutSeconds 180" -ForegroundColor Red
                Write-Host "" -ForegroundColor Red
                Write-Host "      If it still times out, run LogixMcp.exe directly from a terminal and" -ForegroundColor Red
                Write-Host "      check its stderr output and the antivirus quarantine log." -ForegroundColor Red
            }
        }
        return $null
    }

    $line = $readTask.Result

    if ($null -eq $line) {
        # EOF -- server closed stdout (likely crashed).
        Write-Host "FAIL${Label}: server closed stdout unexpectedly (EOF)" -ForegroundColor Red
        if ($Process.HasExited) {
            Write-Host "      server exit code: $($Process.ExitCode)" -ForegroundColor Red
        }
        return $null
    }

    try {
        return $line | ConvertFrom-Json | ConvertTo-Hashtable
    } catch {
        Write-Host "FAIL${Label}: invalid JSON from server: $_" -ForegroundColor Red
        return $null
    }
}

<#
.SYNOPSIS
    Stop the server process gracefully, escalating to kill on timeout.
#>
function Stop-McpProcess {
    param([System.Diagnostics.Process] $Process)

    if (-not $Process.HasExited) {
        try {
            $Process.Kill()
            $Process.WaitForExit(3000) | Out-Null
        } catch {
            # Ignore errors during cleanup.
        }
    }
    $Process.Dispose()
}

# ---------------------------------------------------------------------------
# Step 4 -- MCP handshake helpers
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    Send the MCP initialize request and validate the response.
    Returns $true on success, $false on failure.
#>
function Invoke-McpInitialize {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $Label = '',
        [datetime] $StartTime = (Get-Date)
    )

    Send-McpMessage -Process $Process -Message @{
        jsonrpc = '2.0'
        id      = 1
        method  = 'initialize'
        params  = @{
            protocolVersion = $McpProtocolVersion
            capabilities    = @{}
            clientInfo      = @{ name = 'cold-gate-ps'; version = '0.0.1' }
        }
    }

    $resp = Receive-McpMessage -Process $Process -Label $Label
    if ($null -eq $resp) { return $false }

    if ($resp.ContainsKey('error')) {
        Write-Host "FAIL${Label}: initialize returned error: $($resp['error'] | ConvertTo-Json -Compress)" -ForegroundColor Red
        return $false
    }

    if (-not $resp.ContainsKey('result')) {
        Write-Host "FAIL${Label}: initialize response missing 'result': $($resp | ConvertTo-Json -Compress)" -ForegroundColor Red
        return $false
    }

    $result     = $resp['result']
    $serverInfo = if ($result -is [hashtable] -and $result.ContainsKey('serverInfo')) { $result['serverInfo'] } else { @{} }
    $srvName    = if ($serverInfo -is [hashtable] -and $serverInfo.ContainsKey('name'))    { $serverInfo['name']    } else { '?' }
    $srvVersion = if ($serverInfo -is [hashtable] -and $serverInfo.ContainsKey('version')) { $serverInfo['version'] } else { '?' }

    $elapsedSec = [math]::Round(((Get-Date) - $StartTime).TotalSeconds, 2)
    Write-Host "     initialize OK  ${Label}-- server: $srvName v$srvVersion (elapsed: ${elapsedSec}s)"
    return $true
}

<#
.SYNOPSIS
    Send the notifications/initialized frame (no response expected).
#>
function Send-McpInitializedNotification {
    param([System.Diagnostics.Process] $Process)

    Send-McpMessage -Process $Process -Message @{
        jsonrpc = '2.0'
        method  = 'notifications/initialized'
        params  = @{}
    }
}

<#
.SYNOPSIS
    Call logix_ping and require status="ok".
    Returns $true on success, $false on failure.
#>
function Invoke-LogixPing {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $Label = '',
        [datetime] $StartTime = (Get-Date)
    )

    Send-McpMessage -Process $Process -Message @{
        jsonrpc = '2.0'
        id      = 2
        method  = 'tools/call'
        params  = @{ name = 'logix_ping'; arguments = @{} }
    }

    $resp = Receive-McpMessage -Process $Process -Label $Label
    if ($null -eq $resp) { return $false }

    if ($resp.ContainsKey('error')) {
        Write-Host "FAIL${Label}: logix_ping returned JSON-RPC error: $($resp['error'] | ConvertTo-Json -Compress)" -ForegroundColor Red
        return $false
    }

    # Result content is a list of blocks; the first block's 'text' is the
    # JSON-serialised PingResponse.
    # Resolve the intermediate key into a named variable first so that PS 5.1
    # does not raise "Cannot index into a null array" on a chained lookup when
    # the key is absent.  ($null['content'] is fatal in Windows PowerShell 5.1.)
    $result  = $resp['result']
    if ($null -eq $result -or $result -isnot [hashtable]) {
        Write-Host "FAIL${Label}: logix_ping response missing valid 'result'" -ForegroundColor Red
        return $false
    }
    $content = $result['content']
    $text    = if ($content -and $content.Count -gt 0) {
        $item = $content[0]
        if ($item -is [hashtable] -and $item.ContainsKey('text')) { $item['text'] } else { '' }
    } else { '' }

    try {
        $ping = $text | ConvertFrom-Json | ConvertTo-Hashtable
    } catch {
        $ping = $null
    }

    if ($null -eq $ping -or $ping -isnot [hashtable]) {
        Write-Host "FAIL${Label}: logix_ping payload is not a valid JSON object: $text" -ForegroundColor Red
        return $false
    }

    if ($ping['status'] -ne 'ok') {
        Write-Host "FAIL${Label}: logix_ping payload unexpected: $text" -ForegroundColor Red
        return $false
    }

    $elapsedSec = [math]::Round(((Get-Date) - $StartTime).TotalSeconds, 2)
    Write-Host "     logix_ping OK  ${Label}-- $text (elapsed: ${elapsedSec}s)"
    return $true
}

<#
.SYNOPSIS
    Call one MCP tool and return its JSON payload as a hashtable.
    Returns $null (after printing a FAIL line) on a dead server, read timeout,
    JSON-RPC error, or a result that is not a JSON object.
#>
function Invoke-LogixToolPayload {
    param(
        [System.Diagnostics.Process] $Process,
        [int]    $ReqId,
        [string] $ToolName,
        [hashtable] $Arguments = @{},
        [string] $Label = '',
        [int]    $ReadTimeoutMs = 0
    )

    if ($Process.HasExited) {
        Write-Host "FAIL${Label}: server is dead before $ToolName call (rc=$($Process.ExitCode))" -ForegroundColor Red
        return $null
    }

    Send-McpMessage -Process $Process -Message @{
        jsonrpc = '2.0'
        id      = $ReqId
        method  = 'tools/call'
        params  = @{ name = $ToolName; arguments = $Arguments }
    }

    $resp = Receive-McpMessage -Process $Process -Label $Label -TimeoutMs $ReadTimeoutMs
    if ($null -eq $resp) { return $null }

    if ($Process.HasExited) {
        Write-Host "FAIL${Label}: server died while handling $ToolName (rc=$($Process.ExitCode))" -ForegroundColor Red
        return $null
    }

    if ($resp.ContainsKey('error')) {
        Write-Host "FAIL${Label}: $ToolName returned JSON-RPC error: $($resp['error'] | ConvertTo-Json -Compress)" -ForegroundColor Red
        return $null
    }

    # Resolve the intermediate key into a named variable first so that PS 5.1
    # does not raise "Cannot index into a null array" on a chained lookup when
    # the key is absent.  ($null['content'] is fatal in Windows PowerShell 5.1.)
    $result = $resp['result']
    if ($null -eq $result -or $result -isnot [hashtable]) {
        Write-Host "FAIL${Label}: $ToolName response missing valid 'result'" -ForegroundColor Red
        return $null
    }
    $content = $result['content']
    $text    = if ($content -and $content.Count -gt 0) {
        $item = $content[0]
        if ($item -is [hashtable] -and $item.ContainsKey('text')) { $item['text'] } else { '' }
    } else { '' }

    try {
        $payload = $text | ConvertFrom-Json | ConvertTo-Hashtable
    } catch {
        Write-Host "FAIL${Label}: $ToolName result is not valid JSON: $text" -ForegroundColor Red
        return $null
    }

    if ($null -eq $payload -or $payload -isnot [hashtable]) {
        Write-Host "FAIL${Label}: $ToolName result is not a valid JSON object: $text" -ForegroundColor Red
        return $null
    }

    $payload['__text'] = $text
    return $payload
}

<#
.SYNOPSIS
    Call logix_connect backend="sdk" and require it to reach status="connected".
    A slow first connect answers status="connecting" (never a failure on its
    own); the gate then polls logix_connect_status, printing the phase, until
    the status is "connected" (pass) or "failed" (fail), or until
    -TimeoutSeconds has elapsed since $StartTime (fail).
    Returns $true only on status="connected".
#>
function Invoke-LogixConnectSdk {
    param(
        [System.Diagnostics.Process] $Process,
        [int]    $ReqId,
        [string] $Label = '',
        [datetime] $StartTime = (Get-Date),
        [int]    $PollIntervalMs = 1000
    )

    $deadline = $StartTime.AddSeconds($TimeoutSeconds)

    $payload = Invoke-LogixToolPayload -Process $Process -ReqId $ReqId -ToolName 'logix_connect' `
        -Arguments @{ gateway = '127.0.0.1'; path = '1,0'; backend = 'sdk' } -Label $Label
    if ($null -eq $payload) { return $false }

    $polls = 0
    while ($true) {
        $status = if ($payload.ContainsKey('status')) { [string] $payload['status'] } else { '' }

        if ($status -eq 'connected') {
            $elapsedSec = [math]::Round(((Get-Date) - $StartTime).TotalSeconds, 2)
            Write-Host "     logix_connect backend='sdk' OK  ${Label}-- status=connected backend=$($payload['backend']) (elapsed: ${elapsedSec}s)"
            return $true
        }

        if ($status -ne 'connecting') {
            # 'failed', a structured error (ConnectSdkNotAvailableError, ConnectError), or anything
            # unexpected: with sdk\vendor\ provisioned this means CSClient.dll is present but the
            # backend did not activate -- a defect in the provisioned artifact.
            Write-Host "FAIL${Label}: logix_connect backend='sdk' did not reach status='connected' (status='$status')." -ForegroundColor Red
            Write-Host "      Payload: $($payload['__text'])" -ForegroundColor Red
            Write-Host "" -ForegroundColor Red
            Write-Host "      When sdk\vendor\ is provisioned the gate requires the SDK backend to" -ForegroundColor Red
            Write-Host "      activate successfully.  A failed status or structured error means CSClient.dll is" -ForegroundColor Red
            Write-Host "      present but the backend failed to initialise -- check the publish artifact and" -ForegroundColor Red
            Write-Host "      that all Rockwell prerequisites are installed on this machine." -ForegroundColor Red
            return $false
        }

        # status = connecting: report the phase and keep polling until the deadline.
        $phase      = if ($payload.ContainsKey('phase')) { $payload['phase'] } else { '?' }
        $elapsedSec = [math]::Round(((Get-Date) - $StartTime).TotalSeconds, 2)
        Write-Host "     logix_connect backend='sdk' connecting  ${Label}-- phase=$phase (elapsed: ${elapsedSec}s of ${TimeoutSeconds}s)"

        $remainingMs = [int][math]::Floor(($deadline - (Get-Date)).TotalMilliseconds)
        if ($remainingMs -le 0) {
            Write-Host "FAIL${Label}: logix_connect backend='sdk' still connecting (phase=$phase) after ${TimeoutSeconds}s." -ForegroundColor Red
            Write-Host "      Re-run the gate with a higher -TimeoutSeconds (a first FTSP token on a never-activated install can be slow)." -ForegroundColor Red
            return $false
        }

        Start-Sleep -Milliseconds ([math]::Min($PollIntervalMs, $remainingMs))

        $remainingMs = [int][math]::Floor(($deadline - (Get-Date)).TotalMilliseconds)
        if ($remainingMs -le 0) { $remainingMs = 1 }
        $polls++
        $payload = Invoke-LogixToolPayload -Process $Process -ReqId ($ReqId + 1000 + $polls) -ToolName 'logix_connect_status' `
            -Label $Label -ReadTimeoutMs $remainingMs
        if ($null -eq $payload) { return $false }
    }
}

# ---------------------------------------------------------------------------
# Step 5 -- logix_open_project helper
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    Call logix_open_project with the temp L5X path and require a success payload
    (the 'controller' field must be present in the response).
    Returns $true on success, $false on failure.
    On failure a targeted error message naming logix_open_project is emitted so
    the failure is never misread as a logix_connect or SDK-backend failure.
#>
function Invoke-LogixOpenProject {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $L5XPath,
        [int]    $ReqId,
        [string] $Label = '',
        [datetime] $StartTime = (Get-Date)
    )

    Send-McpMessage -Process $Process -Message @{
        jsonrpc = '2.0'
        id      = $ReqId
        method  = 'tools/call'
        params  = @{
            name      = 'logix_open_project'
            arguments = @{ path = $L5XPath }
        }
    }

    $resp = Receive-McpMessage -Process $Process -Label $Label
    if ($null -eq $resp) { return $false }

    if ($resp.ContainsKey('error')) {
        Write-Host "FAIL${Label}: logix_open_project returned JSON-RPC error: $($resp['error'] | ConvertTo-Json -Compress)" -ForegroundColor Red
        return $false
    }

    $result  = $resp['result']
    if ($null -eq $result -or $result -isnot [hashtable]) {
        Write-Host "FAIL${Label}: logix_open_project response missing valid 'result'" -ForegroundColor Red
        return $false
    }
    $content = $result['content']
    $text    = if ($content -and $content.Count -gt 0) {
        $item = $content[0]
        if ($item -is [hashtable] -and $item.ContainsKey('text')) { $item['text'] } else { '' }
    } else { '' }

    try {
        $payload = $text | ConvertFrom-Json | ConvertTo-Hashtable
    } catch {
        Write-Host "FAIL${Label}: logix_open_project result is not valid JSON: $text" -ForegroundColor Red
        return $false
    }

    if ($null -eq $payload -or $payload -isnot [hashtable]) {
        Write-Host "FAIL${Label}: logix_open_project result is not a valid JSON object: $text" -ForegroundColor Red
        return $false
    }

    if ($payload.ContainsKey('error')) {
        Write-Host "FAIL${Label}: logix_open_project returned tool error: $($payload['error'])" -ForegroundColor Red
        return $false
    }

    # Require the 'controller' field that is present in every OpenProjectResult
    # success payload.  Any response that lacks this field does not indicate
    # success even if it also lacks an 'error' key -- report it explicitly as a
    # logix_open_project failure so it is never misread as a connect-layer error.
    if (-not $payload.ContainsKey('controller')) {
        Write-Host "FAIL${Label}: logix_open_project did not return a success payload -- 'controller' field absent; got: $text" -ForegroundColor Red
        return $false
    }

    $elapsedSec = [math]::Round(((Get-Date) - $StartTime).TotalSeconds, 2)
    Write-Host "     logix_open_project OK  ${Label}-- $text (elapsed: ${elapsedSec}s)"
    return $true
}

# ---------------------------------------------------------------------------
# Logix Designer version resolution
# ---------------------------------------------------------------------------

function Get-LogixExeVersion {
    <#
    .SYNOPSIS
        Returns the FileMajorPart/FileMinorPart of an executable (seam for tests).
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory = $true)] [string] $Path)

    $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    return [pscustomobject]@{ Major = [int]$fvi.FileMajorPart; Minor = [int]$fvi.FileMinorPart }
}

function Resolve-LogixDesignerVersion {
    <#
    .SYNOPSIS
        Picks the Logix Designer version the gate stamps into the probe L5X.
    .DESCRIPTION
        Source (1): legacy "NN.NN" folders (major parsed from the folder name)
        and "ENU\vNN" folders under InstallRoot.  An ENU\vNN folder counts only
        if it holds Bin\LogixDesigner.exe with a readable FileVersion; otherwise
        it is skipped and the reason is logged.  Source (2), used only
        when source (1) finds nothing: every LogixDesigner.exe under InstallRoot,
        read via Get-LogixExeVersion.  Candidates are sorted by major then minor
        descending and the first is chosen, unless -Override names a major.
        Every candidate and the chosen one are written with Write-Host.
        Throws a terminating error with FullyQualifiedErrorId
        'LogixDesignerNotInstalled' (nothing found) or
        'LogixDesignerVersionNotFound' (override not installed).
    .OUTPUTS
        pscustomobject with Revision ("36.0"), Major, Minor, Source, Candidates.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)] [string] $InstallRoot,
        [Parameter(Mandatory = $false)] [int] $Override = 0
    )

    $candidates = @()
    $rootExists = Test-Path -LiteralPath $InstallRoot -PathType Container

    if ($rootExists) {
        $topDirs = @(Get-ChildItem -LiteralPath $InstallRoot -Directory -ErrorAction SilentlyContinue)

        # Source (1a): legacy "36.00" folders.
        foreach ($d in ($topDirs | Where-Object { $_.Name -match '^(\d+)\.(\d+)' })) {
            $null = $d.Name -match '^(\d+)\.(\d+)'
            $candidates += [pscustomobject]@{
                Major = [int]$Matches[1]; Minor = [int]$Matches[2]
                Label = $d.Name; Path = $d.FullName; Source = 'install-dir subdirectory (source 1)'
            }
        }
        # Source (1b): side-by-side "ENU\v36" folders.  Each candidate must hold
        # Bin\LogixDesigner.exe with a readable FileVersion (Major > 0); otherwise
        # it is skipped and the reason logged.  The version is the exe's
        # FileVersion, not the folder name.
        foreach ($enu in ($topDirs | Where-Object { $_.Name -ieq 'ENU' })) {
            foreach ($d in @(Get-ChildItem -LiteralPath $enu.FullName -Directory -ErrorAction SilentlyContinue |
                             Where-Object { $_.Name -match '^v(\d+)$' })) {
                $label  = "ENU\$($d.Name)"
                $exePath = Join-Path (Join-Path $d.FullName 'Bin') 'LogixDesigner.exe'
                if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
                    Write-Host "    logix candidate skipped: [$label]  $($d.FullName)  -- Bin\LogixDesigner.exe not found"
                    continue
                }
                $v = $null
                try { $v = Get-LogixExeVersion -Path $exePath }
                catch {
                    Write-Host "    logix candidate skipped: [$label]  $exePath  -- FileVersion unreadable ($($_.Exception.Message))"
                    continue
                }
                if ($null -eq $v -or [int]$v.Major -le 0) {
                    Write-Host "    logix candidate skipped: [$label]  $exePath  -- FileVersion unreadable (FileMajorPart is 0)"
                    continue
                }
                # The folder name is a claim; the exe's FileVersion is the fact.  Flag a
                # vNN folder whose exe is a different major (the FileVersion still wins).
                $folderMajor = [int]($d.Name -replace '^v', '')
                $mismatch = ([int]$v.Major -ne $folderMajor)
                if ($mismatch) {
                    Write-Host "    logix candidate FLAGGED: [$label]  $exePath  -- folder says v$folderMajor but FileVersion major is $($v.Major); using FileVersion $($v.Major).$($v.Minor)" -ForegroundColor Yellow
                }
                $candidates += [pscustomobject]@{
                    Major = [int]$v.Major; Minor = [int]$v.Minor
                    Label = $label; Path = $exePath; Source = 'FileVersion of ENU\vNN\Bin\LogixDesigner.exe (source 1)'
                    FolderMismatch = $mismatch
                }
            }
        }

        # Source (2): FileVersion of every LogixDesigner.exe.
        if ($candidates.Count -eq 0) {
            foreach ($exe in @(Get-ChildItem -LiteralPath $InstallRoot -Filter 'LogixDesigner.exe' `
                                   -Recurse -File -ErrorAction SilentlyContinue)) {
                $v = Get-LogixExeVersion -Path $exe.FullName
                if ($v.Major -gt 0) {
                    $candidates += [pscustomobject]@{
                        Major = [int]$v.Major; Minor = [int]$v.Minor
                        Label = $exe.Name; Path = $exe.FullName; Source = 'FileVersion of LogixDesigner.exe (source 2)'
                    }
                }
            }
        }
    }

    $candidates = @($candidates | Sort-Object -Property @{ Expression = { $_.Major }; Descending = $true },
                                                         @{ Expression = { $_.Minor }; Descending = $true })

    Write-Host "    logix candidates under '$InstallRoot': $($candidates.Count)"
    foreach ($c in $candidates) {
        Write-Host "    logix candidate: $($c.Major).$($c.Minor)  [$($c.Label)]  $($c.Path)  <- $($c.Source)"
    }

    if ($candidates.Count -eq 0) {
        $msg = @"
ERROR: Logix Designer version could not be determined at runtime.

Sources tried (none succeeded):
  (1) Version subdirectories under:
          $InstallRoot
      $(if ($rootExists) { 'No legacy "NN.NN" folder and no "ENU\vNN" folder found.' } else { 'Directory not found.' })
  (2) FileVersion of LogixDesigner.exe under:
          $InstallRoot
      Executable not found or its FileMajorPart was 0.

Ensure Rockwell Studio 5000 Logix Designer is installed on this machine
(or pass -LogixInstallRoot).  The cold-install gate cannot continue without
a detected version.
"@
        $ex = [System.InvalidOperationException]::new($msg)
        $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
            $ex, 'LogixDesignerNotInstalled', [System.Management.Automation.ErrorCategory]::ObjectNotFound, $InstallRoot))
    }

    if ($Override -gt 0) {
        $chosen = $candidates | Where-Object { $_.Major -eq $Override } | Select-Object -First 1
        if ($null -eq $chosen) {
            $have = ($candidates | ForEach-Object { "$($_.Major).$($_.Minor)" }) -join ', '
            $ex = [System.InvalidOperationException]::new(
                "ERROR: -LogixDesignerVersion $Override requested, but that version is not installed under '$InstallRoot'. Installed: $have")
            $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                $ex, 'LogixDesignerVersionNotFound', [System.Management.Automation.ErrorCategory]::ObjectNotFound, $Override))
        }
        $how = "override -LogixDesignerVersion $Override"
    } else {
        $chosen = $candidates[0]
        $how = 'newest'
    }

    $revision = "$($chosen.Major).$($chosen.Minor)"
    Write-Host "    logix chosen   : $revision  [$($chosen.Label)]  ($how)"

    return [pscustomobject]@{
        Revision   = $revision
        Major      = $chosen.Major
        Minor      = $chosen.Minor
        Source     = "$($chosen.Label) -- $($chosen.Source); $how"
        Candidates = $candidates
    }
}

# ---------------------------------------------------------------------------
# Step 6 (prep) -- Resolve installed Logix Designer version
# ---------------------------------------------------------------------------

# Resolve the installed Logix Designer version so the probe L5X carries the
# correct SoftwareRevision, MajorRev, and MinorRev attributes.
#
# Preferred source order:
#   (1) Highest-versioned subdirectory under the Studio 5000 install root:
#       legacy "36.00" folders and the side-by-side "ENU\v36" folders (valid
#       only with Bin\LogixDesigner.exe and a readable FileVersion, which
#       decides the version; invalid ones are skipped and logged).
#   (2) FileVersion of every LogixDesigner.exe found inside the install tree,
#       newest (FileMajorPart, then FileMinorPart) wins -- fallback when the
#       subdirectory layout is atypical.
#
# Every candidate and the chosen one are logged.  -LogixDesignerVersion <major>
# overrides the automatic (newest) choice.
#
# SDK builds have no built-in default.  If neither source yields a version the gate
# fails immediately with a diagnostic naming the sources that were tried.

$logixSoftwareRevision = $null
$logixVersionSource    = $null

try {
    $logixSelected = Resolve-LogixDesignerVersion -InstallRoot $LogixInstallRoot -Override $LogixDesignerVersion
    $logixSoftwareRevision = $logixSelected.Revision
    $logixVersionSource    = $logixSelected.Source
} catch {
    if ($_.FullyQualifiedErrorId -like 'LogixDesignerNotInstalled*' -and -not $isSdkBuild) {
        # Fall through to the fixed no-SDK revision below.
        Write-Host "    logix ver : no Logix Designer install found under '$LogixInstallRoot' (not required for no-SDK builds)"
    } else {
        # SDK build with no/unsuitable install, or an unsatisfiable override:
        # a clear diagnostic, never a confusing SDK error about a version that
        # is not installed.
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }
}

# No-SDK builds never touch Studio 5000 (the stub backend does not load the
# Logix Designer SDK), so Studio is not required.  Stamp the probe L5X with the
# fixed, documented revision 38.11.  SDK builds fail above when nothing is found.
if ([string]::IsNullOrEmpty($logixSoftwareRevision) -and -not $isSdkBuild) {
    $logixSoftwareRevision = '38.11'
    $logixVersionSource    = 'fixed (no-SDK build; Studio not required)'
}

$logixVersionParts = $logixSoftwareRevision -split '\.', 2
$logixMajorRev     = $logixVersionParts[0]
$logixMinorRev     = if ($logixVersionParts.Count -ge 2) { $logixVersionParts[1] } else { '0' }

Write-Host "    logix ver : $logixSoftwareRevision  # $logixVersionSource"

# ---------------------------------------------------------------------------
# Step 6 -- Run the probe
# ---------------------------------------------------------------------------

# Minimal valid L5X document written to temp so the gate is self-contained.
# No dependency on any path outside -AppDir or the temp directory.
# Double-quoted here-string so $logixSoftwareRevision, $logixMajorRev, and
# $logixMinorRev are interpolated at runtime.
$minimalL5X = @"
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="$logixSoftwareRevision" TargetName="GateProbe" TargetType="Controller" ContainsContext="false">
  <Controller Name="GateProbe" MajorRev="$logixMajorRev" MinorRev="$logixMinorRev" ProcessorType="1756-L84E">
    <DataTypes />
    <Modules />
    <AddOnInstructionDefinitions />
    <Tags />
    <Programs />
    <Tasks />
    <ParameterConnections />
    <Trends />
    <QuickWatchLists />
  </Controller>
</RSLogix5000Content>
"@

$tempL5X = Join-Path $env:TEMP "logix-gate-probe-$PID.L5X"

Write-Host ''
Write-Host "==> probing server (logix_ping; logix_open_project$(if ($isProvisioned) { '; logix_connect backend=sdk' })) ..."
Write-Host "    temp L5X : $tempL5X"

[System.IO.File]::WriteAllText($tempL5X, $minimalL5X, [System.Text.Encoding]::UTF8)

$exitCode = 0
$proc     = Start-McpProcess

try {
    $stepStart = Get-Date
    if (-not (Invoke-McpInitialize -Process $proc -StartTime $stepStart)) {
        $exitCode = 1
    } else {
        Send-McpInitializedNotification -Process $proc

        $stepStart = Get-Date
        if (-not (Invoke-LogixPing -Process $proc -StartTime $stepStart)) {
            $exitCode = 1
        } else {
            $stepStart = Get-Date
            if (-not (Invoke-LogixOpenProject -Process $proc -L5XPath $tempL5X -ReqId 3 -StartTime $stepStart)) {
                $exitCode = 1
            } elseif ($isProvisioned) {
                # SDK build with vendor provisioned: require status="connected".
                $stepStart = Get-Date
                if (-not (Invoke-LogixConnectSdk -Process $proc -ReqId 4 -StartTime $stepStart)) {
                    $exitCode = 1
                }
            }
        }
    }
} catch {
    Write-Host "FAIL: probe exception: $_" -ForegroundColor Red
    $exitCode = 1
} finally {
    Stop-McpProcess -Process $proc
    if (Test-Path -LiteralPath $tempL5X) {
        Remove-Item -LiteralPath $tempL5X -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Step 7 -- Report result
# ---------------------------------------------------------------------------

if ($exitCode -eq 0) {
    Write-Host ''
    Write-Host '==> cold-install gate PASSED'
} else {
    Write-Host ''
    Write-Host '==> cold-install gate FAILED' -ForegroundColor Red
}

exit $exitCode
