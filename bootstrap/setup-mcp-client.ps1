<#
.SYNOPSIS
    Registers LogixMcp with an MCP client: ClaudeCode, ClaudeDesktop, or VSCode.

.DESCRIPTION
    Without -WriteConfig the script prints the command or JSON block that would
    register the server.  With -WriteConfig it writes/merges the config file and
    backs up any prior version.

    ClaudeCode:    prints or runs
                       claude mcp add <ServerName> --scope user
                           [-e KEY=VALUE ...] -- "<AppDir>\LogixMcp.exe"

    ClaudeDesktop: merges an mcpServers.<ServerName> block into
                       %APPDATA%\Claude\claude_desktop_config.json
                   or, when the Microsoft Store (MSIX) build of Claude Desktop is
                   installed (%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude
                   exists), into claude_desktop_config.json in that directory,
                   because the Store build ignores %APPDATA%\Claude.  The path
                   used is printed.

    VSCode:        merges a servers.<ServerName> block (type: stdio) into
                       %APPDATA%\Code\User\mcp.json

    Both file-writing clients preserve every existing key, create the config file
    if absent, and write a timestamped backup before overwriting.

    -WhatIf prints what would be written without touching any file.

    Before writing claude_desktop_config.json the script checks whether Claude
    Desktop is running (a process named Claude or claude, or a process of the
    Microsoft Store package).  If it is, the script refuses, because Claude
    Desktop rewrites its config on exit: "Quit Claude Desktop fully (tray -> Quit)
    and re-run".  -Force skips the check.  After every file write the config is
    read back and the script errors unless the server entry is present.

    The pure function Get-LgxClientConfig returns the config block as a hashtable
    so it can be called from tests without file I/O.

.PARAMETER Client
    Target MCP client.  ClaudeCode | ClaudeDesktop | VSCode.  Default: ClaudeCode.

.PARAMETER AppDir
    Path to the published application directory that contains LogixMcp.exe.
    Default: the parent of the script's own directory (the publish root when
    the script lives in bootstrap/).

.PARAMETER ServerName
    Name to register the server under.  Default: lgx-mcp.

.PARAMETER EnableLiveWrites
    Adds LGX_ENABLE_LIVE_WRITES=1 to the server env block.

.PARAMETER EnableStudioAutomation
    Adds LGX_ENABLE_STUDIO_AUTOMATION=1 to the server env block.
    SDK builds only: a no-SDK build (no sdk\LogixMcp.SdkAdapter.dll under
    -AppDir) rejects this switch with an error instead of writing an env
    variable that has no effect.

.PARAMETER PublishMode
    Confirm (default) | Auto.  Auto adds LGX_PUBLISH_MODE=auto to the server
    env block so the controller publishes without a confirmation prompt.
    Confirm (the default) omits the variable, preserving the confirmation gate.
    SDK builds only: a no-SDK build rejects this parameter when it is passed.

.PARAMETER CommandPrefixes
    Comma-separated tag-name prefixes that designate writable command tags
    (for example "Cmd_,Sim_,HMI_"), or "*" for every tag name.  Adds
    LGX_GATE_COMMAND_PREFIXES=<value> to the server env block.  "*" relaxes the
    naming rule only: logic-written, safety, External Access and consumed tags
    stay denied.  Omitted (or empty) means the variable is not written and the
    default Cmd_,Sim_ applies.  An entry with a character outside A-Z a-z 0-9 _
    (other than a lone "*") is rejected with an error.

.PARAMETER LiveFinalize
    Adds LGX_ENABLE_LIVE_FINALIZE=1 to the server env block.

.PARAMETER WriteConfig
    Write / merge the config file.  Without this switch the script prints only
    and makes no changes.

.PARAMETER Force
    Write the Claude Desktop config even when Claude Desktop is running.

.EXAMPLE
    # Print the Claude Code registration command (default client, default name):
    .\bootstrap\setup-mcp-client.ps1 -AppDir "C:\LogixMcp"

.EXAMPLE
    # Preview the ClaudeDesktop block without writing anything:
    .\bootstrap\setup-mcp-client.ps1 -Client ClaudeDesktop -AppDir "C:\LogixMcp-next" -ServerName lgx-mcp-next -WhatIf

.EXAMPLE
    # Write the registration into ClaudeDesktop config with live-writes enabled:
    .\bootstrap\setup-mcp-client.ps1 -Client ClaudeDesktop -AppDir "C:\LogixMcp" -EnableLiveWrites -WriteConfig

.EXAMPLE
    # Register with Claude Code CLI (must be on PATH):
    .\bootstrap\setup-mcp-client.ps1 -AppDir "C:\LogixMcp" -WriteConfig
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('ClaudeCode', 'ClaudeDesktop', 'VSCode')]
    [string] $Client = 'ClaudeCode',

    [Parameter(Mandatory = $false)]
    [string] $AppDir,

    [Parameter(Mandatory = $false)]
    [string] $ServerName = 'lgx-mcp',

    [Parameter(Mandatory = $false)]
    [switch] $EnableLiveWrites,

    [Parameter(Mandatory = $false)]
    [switch] $EnableStudioAutomation,

    [Parameter(Mandatory = $false)]
    [ValidateSet('Confirm', 'Auto')]
    [string] $PublishMode = 'Confirm',

    [Parameter(Mandatory = $false)]
    [string] $CommandPrefixes,

    [Parameter(Mandatory = $false)]
    [switch] $LiveFinalize,

    [Parameter(Mandatory = $false)]
    [switch] $WriteConfig,

    [Parameter(Mandatory = $false)]
    [switch] $Force,

    # Test seam: replaces the live process table for the Claude Desktop running check.
    [Parameter(Mandatory = $false, DontShow)]
    [object[]] $ProcessList
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Resolve AppDir -- default to the parent of the script's own directory.
# When this script lives in bootstrap/, the parent is the publish root that
# contains LogixMcp.exe.
# ---------------------------------------------------------------------------

if (-not $AppDir) {
    if ($PSScriptRoot) {
        $AppDir = Split-Path -Parent $PSScriptRoot
    } else {
        $AppDir = (Get-Location).Path
    }
}

$AppDir   = [System.IO.Path]::GetFullPath($AppDir)
$ExePath  = Join-Path $AppDir 'LogixMcp.exe'

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Error ("ERROR: LogixMcp.exe not found at '$ExePath'." +
                 " Pass -AppDir pointing to the directory that contains LogixMcp.exe.")
    exit 1
}

# ---------------------------------------------------------------------------
# No-SDK build detection.  A no-SDK build ships without
# sdk\LogixMcp.SdkAdapter.dll.  -EnableStudioAutomation and -PublishMode only
# affect the SDK lane, so passing either to a no-SDK build is an error rather
# than a silently ignored environment variable.
# ---------------------------------------------------------------------------

$SdkAdapterDll = Join-Path (Join-Path $AppDir 'sdk') 'LogixMcp.SdkAdapter.dll'
$IsNoSdkBuild  = -not (Test-Path -LiteralPath $SdkAdapterDll -PathType Leaf)

if ($IsNoSdkBuild) {
    $sdkOnly = @()
    if ($PSBoundParameters.ContainsKey('EnableStudioAutomation')) { $sdkOnly += '-EnableStudioAutomation' }
    if ($PSBoundParameters.ContainsKey('PublishMode'))            { $sdkOnly += '-PublishMode' }
    if ($sdkOnly.Count -gt 0) {
        Write-Error ("ERROR: $($sdkOnly -join ' and ') not supported in this build." +
                     " This is a no-SDK build (no sdk\LogixMcp.SdkAdapter.dll under '$AppDir'), so" +
                     " these options have no effect. Remove them and re-run.")
        exit 1
    }
}

# ---------------------------------------------------------------------------
# -CommandPrefixes validation.  The server refuses to connect with an invalid
# LGX_GATE_COMMAND_PREFIXES (GateConfigInvalid), so reject a bad value here
# rather than write a config that cannot connect.  Empty means "not written".
# ---------------------------------------------------------------------------

$CommandPrefixesValue = ''
if ($PSBoundParameters.ContainsKey('CommandPrefixes') -and
    -not [string]::IsNullOrWhiteSpace($CommandPrefixes)) {
    foreach ($entry in ($CommandPrefixes -split ',')) {
        $e = $entry.Trim()
        if ($e -ne '*' -and $e -notmatch '^[A-Za-z0-9_]+$') {
            Write-Error ("ERROR: -CommandPrefixes entry '$e' is invalid. Entries may use only" +
                         " letters, digits and underscore (or be a lone '*').")
            exit 1
        }
    }
    $CommandPrefixesValue = $CommandPrefixes.Trim()
}

# ---------------------------------------------------------------------------
# Get-LgxClaudeDesktopConfigDir
# Returns the directory that holds claude_desktop_config.json.  The Microsoft
# Store (MSIX) build of Claude Desktop reads its config from
#   %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude
# and ignores %APPDATA%\Claude, so that directory wins when it exists.
# ---------------------------------------------------------------------------

function Get-LgxClaudeDesktopConfigDir {
    [CmdletBinding()]
    param()

    if ($env:LOCALAPPDATA) {
        $packages = Join-Path $env:LOCALAPPDATA 'Packages'
        if (Test-Path -LiteralPath $packages -PathType Container) {
            $stores = @(Get-ChildItem -LiteralPath $packages -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
                        Sort-Object Name)
            foreach ($pkg in $stores) {
                $dir = Join-Path (Join-Path (Join-Path $pkg.FullName 'LocalCache') 'Roaming') 'Claude'
                if (Test-Path -LiteralPath $dir -PathType Container) {
                    return [pscustomobject] @{ Dir = $dir; Kind = 'Microsoft Store (MSIX) install' }
                }
            }
        }
    }

    $appData = $env:APPDATA
    if (-not $appData) { $appData = [Environment]::GetFolderPath('ApplicationData') }
    if (-not $appData) { $appData = '%APPDATA%' }
    return [pscustomobject] @{
        Dir  = (Join-Path $appData 'Claude')
        Kind = 'standard install'
    }
}

# ---------------------------------------------------------------------------
# Get-LgxClientConfig
# Pure function -- no file I/O.  Returns a hashtable describing the config
# block for the chosen client so tests can call it without side effects.
# ---------------------------------------------------------------------------

function Get-LgxClientConfig {
    <#
    .SYNOPSIS
        Returns the MCP registration block for the specified client as a hashtable.
        No file I/O -- safe to call from tests.
    .PARAMETER ForAppDir
        Path to the directory that contains LogixMcp.exe.
    .PARAMETER ForServerName
        Name under which the server will be registered.
    .PARAMETER WithLiveWrites
        Include LGX_ENABLE_LIVE_WRITES=1 in the env block.
    .PARAMETER WithStudioAutomation
        Include LGX_ENABLE_STUDIO_AUTOMATION=1 in the env block.
    .PARAMETER WithCommandPrefixes
        Value for LGX_GATE_COMMAND_PREFIXES; empty string omits the variable.
    .PARAMETER WithLiveFinalize
        Include LGX_ENABLE_LIVE_FINALIZE=1 in the env block.
    .PARAMETER WithPublishMode
        Auto | Confirm (or empty string for Confirm).  Auto adds
        LGX_PUBLISH_MODE=auto to the env block.  Confirm omits the variable.
    .PARAMETER ForClient
        ClaudeCode | ClaudeDesktop | VSCode.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ForAppDir,

        [Parameter(Mandatory = $true)]
        [string] $ForServerName,

        [Parameter(Mandatory = $false)]
        [switch] $WithLiveWrites,

        [Parameter(Mandatory = $false)]
        [switch] $WithStudioAutomation,

        [Parameter(Mandatory = $false)]
        [ValidateSet('Confirm', 'Auto', '')]
        [string] $WithPublishMode = 'Confirm',

        [Parameter(Mandatory = $false)]
        [string] $WithCommandPrefixes = '',

        [Parameter(Mandatory = $false)]
        [switch] $WithLiveFinalize,

        [Parameter(Mandatory = $true)]
        [ValidateSet('ClaudeCode', 'ClaudeDesktop', 'VSCode')]
        [string] $ForClient
    )

    $exeArg  = Join-Path $ForAppDir 'LogixMcp.exe'
    $envHash = [ordered] @{}
    if ($WithLiveWrites)                { $envHash['LGX_ENABLE_LIVE_WRITES']       = '1'    }
    if ($WithStudioAutomation)          { $envHash['LGX_ENABLE_STUDIO_AUTOMATION'] = '1'    }
    if ($WithLiveFinalize)              { $envHash['LGX_ENABLE_LIVE_FINALIZE']     = '1'    }
    if ($WithPublishMode -eq 'Auto')    { $envHash['LGX_PUBLISH_MODE']             = 'auto' }
    if (-not [string]::IsNullOrWhiteSpace($WithCommandPrefixes)) {
        $envHash['LGX_GATE_COMMAND_PREFIXES'] = $WithCommandPrefixes.Trim()
    }

    switch ($ForClient) {

        'ClaudeCode' {
            return [ordered] @{
                Client     = 'ClaudeCode'
                ServerName = $ForServerName
                Command    = $exeArg
                Env        = $envHash
            }
        }

        'ClaudeDesktop' {
            $block = [ordered] @{
                command = $exeArg
                args    = @()
            }
            if ($envHash.Count -gt 0) { $block['env'] = $envHash }
            return [ordered] @{
                Client     = 'ClaudeDesktop'
                ServerName = $ForServerName
                Block      = $block
            }
        }

        'VSCode' {
            $block = [ordered] @{
                type    = 'stdio'
                command = $exeArg
                args    = @()
            }
            if ($envHash.Count -gt 0) { $block['env'] = $envHash }
            return [ordered] @{
                Client     = 'VSCode'
                ServerName = $ForServerName
                Block      = $block
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Claude / MCP client config I/O
#
# One read/write rule for every script that writes an MCP client config file
# (claude_desktop_config.json, VS Code mcp.json).  install.ps1 does not write
# these files itself; it runs this script.
#   * Read with [IO.File]::ReadAllText, write with [IO.File]::WriteAllText and
#     UTF8Encoding($false): UTF-8 without a BOM, identical on PS 5.1 and 7.
#     (Get-Content -Raw / Set-Content -Encoding UTF8 guess the code page on
#     5.1 and add a BOM, which corrupts non-ASCII names such as Jose.)
#   * ConvertTo-Json -Depth $script:LgxJsonDepth0 so deeply nested entries are not flattened.
#   * Before writing, the new text is parsed again and every entry other than
#     ours is deep-compared with the original.  Any difference: refuse to
#     write, leave the original file as it is.
#   * A file that is not valid JSON is never touched; the message names the
#     file and the line.
# ---------------------------------------------------------------------------

$script:LgxJsonDepth = 100

# Returns $null when $Text is strict JSON, else @{ Line; Column; Message }.
# Hand-written so the line number is available on both PS 5.1 and 7.
function Get-LgxJsonSyntaxError {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Text)

    $n     = $Text.Length
    $i     = 0
    $stack = New-Object 'System.Collections.Generic.Stack[char]'
    $expect = 'value'          # value | valueOrEnd | keyOrEnd | key | comma | done
    $numRx = New-Object System.Text.RegularExpressions.Regex('\G-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?')
    $errPos = -1
    $errMsg = ''

    while ($true) {
        while ($i -lt $n -and ($Text[$i] -eq ' ' -or $Text[$i] -eq "`t" -or $Text[$i] -eq "`r" -or $Text[$i] -eq "`n")) { $i++ }
        if ($i -ge $n) {
            if ($expect -ne 'done') { $errPos = $n; $errMsg = 'unexpected end of file' }
            break
        }
        $c = $Text[$i]

        if ($expect -eq 'done') { $errPos = $i; $errMsg = 'unexpected content after the end of the JSON value'; break }

        if ($expect -eq 'comma') {
            $top = $stack.Peek()
            if ($c -eq ',') { $i++; $expect = if ($top -eq '{') { 'key' } else { 'value' }; continue }
            if (($c -eq '}' -and $top -eq '{') -or ($c -eq ']' -and $top -eq '[')) {
                [void] $stack.Pop(); $i++
                $expect = if ($stack.Count -eq 0) { 'done' } else { 'comma' }
                continue
            }
            $errPos = $i; $errMsg = "expected ',' or a closing bracket but found '$c'"; break
        }

        if ($expect -eq 'keyOrEnd' -or $expect -eq 'key') {
            if ($expect -eq 'keyOrEnd' -and $c -eq '}') {
                [void] $stack.Pop(); $i++
                $expect = if ($stack.Count -eq 0) { 'done' } else { 'comma' }
                continue
            }
            if ($c -ne '"') { $errPos = $i; $errMsg = "expected a property name in double quotes but found '$c'"; break }
        }

        if ($expect -eq 'valueOrEnd' -and $c -eq ']') {
            [void] $stack.Pop(); $i++
            $expect = if ($stack.Count -eq 0) { 'done' } else { 'comma' }
            continue
        }

        # A string (value or property name) starts here, or a value of another kind.
        $isKey = ($expect -eq 'keyOrEnd' -or $expect -eq 'key')
        if ($c -eq '"') {
            $j = $i + 1
            $closed = $false
            while ($j -lt $n) {
                $d = $Text[$j]
                if ($d -eq '"') { $closed = $true; break }
                if ([int] $d -lt 32) { $errPos = $j; $errMsg = 'control character inside a string'; break }
                if ($d -eq '\') {
                    if ($j + 1 -ge $n) { $j++; break }
                    $e = $Text[$j + 1]
                    if ('"\/bfnrt'.IndexOf($e) -ge 0) { $j += 2; continue }
                    if ($e -eq 'u' -and $j + 5 -lt $n + 0 -and $Text.Substring($j + 2, 4) -match '^[0-9A-Fa-f]{4}$') { $j += 6; continue }
                    $errPos = $j; $errMsg = 'invalid escape sequence in a string'; break
                }
                $j++
            }
            if ($errPos -ge 0) { break }
            if (-not $closed) { $errPos = $i; $errMsg = 'unterminated string'; break }
            $i = $j + 1
            if ($isKey) {
                while ($i -lt $n -and ($Text[$i] -eq ' ' -or $Text[$i] -eq "`t" -or $Text[$i] -eq "`r" -or $Text[$i] -eq "`n")) { $i++ }
                if ($i -ge $n) { $errPos = $n; $errMsg = "unexpected end of file, expected ':'"; break }
                if ($Text[$i] -ne ':') { $errPos = $i; $errMsg = "expected ':' after the property name but found '$($Text[$i])'"; break }
                $i++
                $expect = 'value'
            } else {
                $expect = if ($stack.Count -eq 0) { 'done' } else { 'comma' }
            }
            continue
        }

        if ($c -eq '{') { $stack.Push('{'); $i++; $expect = 'keyOrEnd'; continue }
        if ($c -eq '[') { $stack.Push('['); $i++; $expect = 'valueOrEnd'; continue }

        $matched = 0
        foreach ($lit in 'true', 'false', 'null') {
            if ([string]::CompareOrdinal($Text, $i, $lit, 0, $lit.Length) -eq 0) { $matched = $lit.Length; break }
        }
        if ($matched -eq 0 -and ($c -eq '-' -or ($c -ge '0' -and $c -le '9'))) {
            $m = $numRx.Match($Text, $i)
            if ($m.Success -and $m.Length -gt 0) { $matched = $m.Length }
        }
        if ($matched -eq 0) { $errPos = $i; $errMsg = "unexpected character '$c'"; break }
        $i += $matched
        $expect = if ($stack.Count -eq 0) { 'done' } else { 'comma' }
    }

    if ($errPos -lt 0) { return $null }
    $before = $Text.Substring(0, [Math]::Min($errPos, $n))
    $line   = ($before.Length - $before.Replace("`n", '').Length) + 1
    $col    = $before.Length - $before.LastIndexOf("`n")
    return [pscustomobject] @{ Line = $line; Column = $col; Message = $errMsg }
}

# Returns $null when $A and $B are the same JSON value, else a description of
# the first difference (with its path).  Compares parsed ConvertFrom-Json trees.
function Compare-LgxJsonValue {
    [CmdletBinding()]
    param($A, $B, [string] $Path = '$')

    if ($null -eq $A -and $null -eq $B) { return $null }
    if ($null -eq $A -or $null -eq $B) { return "$Path differs (null vs value)" }

    if ($A -is [System.Management.Automation.PSCustomObject]) {
        if ($B -isnot [System.Management.Automation.PSCustomObject]) { return "$Path differs (object vs other type)" }
        $namesA = @($A.PSObject.Properties | ForEach-Object { $_.Name })
        $namesB = @($B.PSObject.Properties | ForEach-Object { $_.Name })
        $setB = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($nm in $namesB) { [void] $setB.Add($nm) }
        foreach ($nm in $namesA) {
            if (-not $setB.Contains($nm)) { return "$Path.$nm is missing" }
        }
        if ($namesA.Count -ne $namesB.Count) { return "$Path has a different set of properties" }
        foreach ($nm in $namesA) {
            $d = Compare-LgxJsonValue -A $A.PSObject.Properties[$nm].Value -B $B.PSObject.Properties[$nm].Value -Path "$Path.$nm"
            if ($d) { return $d }
        }
        return $null
    }

    if ($A -is [System.Array]) {
        if ($B -isnot [System.Array]) { return "$Path differs (array vs other type)" }
        if ($A.Count -ne $B.Count) { return "$Path has a different number of elements" }
        for ($k = 0; $k -lt $A.Count; $k++) {
            $d = Compare-LgxJsonValue -A $A[$k] -B $B[$k] -Path "$Path[$k]"
            if ($d) { return $d }
        }
        return $null
    }

    if ($A -is [string] -or $B -is [string]) {
        if ($A -is [string] -and $B -is [string] -and $A -ceq $B) { return $null }
        return "$Path differs (text)"
    }
    if ($A -is [bool] -or $B -is [bool]) {
        if ($A -is [bool] -and $B -is [bool] -and $A -eq $B) { return $null }
        return "$Path differs (boolean)"
    }
    if ($A -is [datetime] -or $B -is [datetime]) {
        if ($A -is [datetime] -and $B -is [datetime] -and $A -eq $B) { return $null }
        return "$Path differs (date)"
    }
    # Numbers: 1e5 may come back as an Int32 after a round trip; compare by value.
    if ($A -eq $B) { return $null }
    return "$Path differs (number)"
}

# Reads and parses a config file.  Returns $null for a missing or empty file.
# Throws (naming file and line, and saying the file was left untouched) when
# the text is not valid JSON, or is JSON but not an object.
function Read-LgxJsonConfig {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Trim().Length -eq 0) { return $null }
    $syntax = Get-LgxJsonSyntaxError -Text $text
    if ($syntax) {
        throw ("Config file '$Path' is not valid JSON: line $($syntax.Line), column $($syntax.Column): $($syntax.Message)." +
               " The file was left untouched. Fix or remove it and re-run.")
    }
    try {
        $doc = $text | ConvertFrom-Json
    } catch {
        throw ("Config file '$Path' could not be parsed: line 1: $($_.Exception.Message)." +
               " The file was left untouched. Fix or remove it and re-run.")
    }
    if ($doc -isnot [System.Management.Automation.PSCustomObject]) {
        throw ("Config file '$Path' is valid JSON but its top level is not an object (line 1)." +
               " The file was left untouched. Fix or remove it and re-run.")
    }
    return $doc
}

# Merges $Block under $Section.$Name in the config file at $Path.
# Returns the backup path (or $null when the file did not exist).
# Throws, writing nothing, when the file is malformed or when re-parsing the new
# text shows any entry other than ours changed.
function Set-LgxMcpServerEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Section,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] $Block
    )

    $orig = Read-LgxJsonConfig -Path $Path

    $root = [ordered] @{}
    $servers = [ordered] @{}
    if ($null -ne $orig) {
        foreach ($p in $orig.PSObject.Properties) {
            if ($p.Name -ceq $Section) {
                if ($p.Value -isnot [System.Management.Automation.PSCustomObject]) {
                    throw ("Config file '$Path': '$Section' is not an object (line 1)." +
                           " The file was left untouched. Fix or remove it and re-run.")
                }
                foreach ($s in $p.Value.PSObject.Properties) { $servers[$s.Name] = $s.Value }
            } else {
                $root[$p.Name] = $p.Value
            }
        }
    }
    $servers[$Name] = $Block
    $root[$Section] = $servers

    $json = $root | ConvertTo-Json -Depth $script:LgxJsonDepth

    # Re-parse what we are about to write and compare everything that is not ours.
    $new = $null
    $parseError = $null
    try { $new = $json | ConvertFrom-Json } catch { $parseError = $_.Exception.Message }
    $diff = $null
    if ($parseError) {
        $diff = "the new text does not parse ($parseError)"
    } elseif ($null -eq $new -or $new -isnot [System.Management.Automation.PSCustomObject] -or
              -not $new.PSObject.Properties[$Section]) {
        $diff = "the new text has no '$Section' entry"
    } else {
        $origProps = if ($null -ne $orig) { @($orig.PSObject.Properties) } else { @() }
        $newNames  = @($new.PSObject.Properties | ForEach-Object { $_.Name })
        $expected  = @($origProps | Where-Object { $_.Name -cne $Section }).Count + 1
        if ($newNames.Count -ne $expected) { $diff = 'the number of top-level entries changed' }
        foreach ($p in $origProps) {
            if ($diff) { break }
            if ($p.Name -ceq $Section) {
                $newSec = $new.PSObject.Properties[$Section].Value
                if ($newSec -isnot [System.Management.Automation.PSCustomObject]) { $diff = "'$Section' is not an object"; break }
                $expectedSrv = @($p.Value.PSObject.Properties | Where-Object { $_.Name -cne $Name }).Count + 1
                if (@($newSec.PSObject.Properties).Count -ne $expectedSrv) { $diff = "the number of entries under '$Section' changed"; break }
                foreach ($s in $p.Value.PSObject.Properties) {
                    if ($s.Name -ceq $Name) { continue }
                    if (-not $newSec.PSObject.Properties[$s.Name]) { $diff = "'$Section.$($s.Name)' is missing"; break }
                    $d = Compare-LgxJsonValue -A $s.Value -B $newSec.PSObject.Properties[$s.Name].Value -Path "$Section.$($s.Name)"
                    if ($d) { $diff = $d; break }
                }
            } else {
                if (-not $new.PSObject.Properties[$p.Name]) { $diff = "'$($p.Name)' is missing"; break }
                $d = Compare-LgxJsonValue -A $p.Value -B $new.PSObject.Properties[$p.Name].Value -Path $p.Name
                if ($d) { $diff = $d; break }
            }
        }
        if (-not $diff) {
            $newSec = $new.PSObject.Properties[$Section].Value
            if ($newSec -isnot [System.Management.Automation.PSCustomObject] -or -not $newSec.PSObject.Properties[$Name]) {
                $diff = "'$Section.$Name' is missing"
            }
        }
    }
    if ($diff) {
        throw ("Refusing to write '$Path': re-parsing the new config showed a change to an entry other than '$Name' ($diff)." +
               " The file was left untouched.")
    }

    $backup = $null
    if ($null -ne $orig -or (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $backup = "$Path.bak-$([DateTime]::Now.ToString('yyyyMMddHHmmss'))"
        Copy-Item -LiteralPath $Path -Destination $backup
    }
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $backup
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

# ---------------------------------------------------------------------------
# Assert-LgxConfigEntry
# Reads a written config file back and confirms Section.Name is present.
# Throws (terminating error) when the file is missing, is not valid JSON, or
# lacks the entry.
# ---------------------------------------------------------------------------

function Assert-LgxConfigEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Section,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Write verification failed: '$Path' does not exist after writing."
    }
    try {
        $doc = [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json
    } catch {
        throw "Write verification failed: '$Path' is not valid JSON after writing ($($_.Exception.Message))."
    }
    $sec = if ($doc -and $doc.PSObject.Properties[$Section]) { $doc.$Section } else { $null }
    if ($null -eq $sec -or -not $sec.PSObject.Properties[$Name]) {
        throw "Write verification failed: '$Name' is not present under '$Section' in '$Path' after writing."
    }
}

# ---------------------------------------------------------------------------
# Build config block for the chosen client
# ---------------------------------------------------------------------------

$cfg = Get-LgxClientConfig `
    -ForAppDir            $AppDir `
    -ForServerName        $ServerName `
    -WithLiveWrites:      $EnableLiveWrites `
    -WithStudioAutomation:$EnableStudioAutomation `
    -WithPublishMode      $PublishMode `
    -WithCommandPrefixes  $CommandPrefixesValue `
    -WithLiveFinalize:    $LiveFinalize `
    -ForClient            $Client

if (-not $IsNoSdkBuild) { Write-Host "Publish mode: $PublishMode" }

# ---------------------------------------------------------------------------
# Env notice -- printed when neither opt-in switch was given
# ---------------------------------------------------------------------------

if (-not $EnableLiveWrites -and ($IsNoSdkBuild -or -not $EnableStudioAutomation)) {
    Write-Host ('NOTE: Live controller writes are off.' +
                ' Add -EnableLiveWrites to set LGX_ENABLE_LIVE_WRITES=1.')
}

# ---------------------------------------------------------------------------
# Per-client action
# ---------------------------------------------------------------------------

switch ($Client) {

    # -----------------------------------------------------------------------
    # ClaudeCode
    # -----------------------------------------------------------------------
    'ClaudeCode' {
        # Build arg list and display string
        $envArgPairs = @()
        $envDisplay  = ''
        foreach ($key in $cfg.Env.Keys) {
            $envArgPairs += '-e'
            $envArgPairs += "$key=$($cfg.Env[$key])"
            $envDisplay  += " -e $key=$($cfg.Env[$key])"
        }

        $removeCmd  = "claude mcp remove $ServerName --scope user"
        $displayCmd = "$removeCmd`n" +
                      "claude mcp add $ServerName --scope user$envDisplay -- `"$ExePath`""

        if ($WriteConfig) {
            if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
                Write-Error ("ERROR: 'claude' CLI is not on PATH." +
                             " Install Claude CLI and retry, or run the following command manually:" +
                             "`n  $displayCmd")
                exit 1
            }
            if ($PSCmdlet.ShouldProcess($ServerName, 'claude mcp add')) {
                $claudeArgs = @('mcp', 'add', $ServerName, '--scope', 'user') +
                              $envArgPairs +
                              @('--', $ExePath)
                # 'claude mcp add' refuses to overwrite an existing registration,
                # so a re-run that enables live writes must remove it first.
                # Remove fails when the server is not registered yet; that is fine.
                # The miss writes to stderr, which PowerShell would show as a red error (and, under
                # $ErrorActionPreference='Stop', turn into a terminating one), so capture both streams
                # with the preference relaxed and report only the note below.
                $prevErrorAction = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try {
                    $null = & claude mcp remove $ServerName --scope user 2>&1
                } finally {
                    $ErrorActionPreference = $prevErrorAction
                }
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Note: '$removeCmd' exited $LASTEXITCODE (server not registered yet?); continuing."
                }
                & claude @claudeArgs
                if ($LASTEXITCODE -ne 0) {
                    Write-Error "ERROR: 'claude mcp add $ServerName' failed with exit code $LASTEXITCODE."
                    exit $LASTEXITCODE
                }
            }
        } else {
            Write-Host $displayCmd
        }
    }

    # -----------------------------------------------------------------------
    # ClaudeDesktop
    # -----------------------------------------------------------------------
    'ClaudeDesktop' {
        $preview = [ordered] @{
            mcpServers = [ordered] @{ $ServerName = $cfg.Block }
        } | ConvertTo-Json -Depth $script:LgxJsonDepth

        Write-Host "ClaudeDesktop -- server block for '$ServerName':"
        Write-Host $preview

        $desktopDir = Get-LgxClaudeDesktopConfigDir
        Write-Host "Config path used: $(Join-Path $desktopDir.Dir 'claude_desktop_config.json') ($($desktopDir.Kind))"

        if ($WriteConfig) {
            $configPath = Join-Path $desktopDir.Dir 'claude_desktop_config.json'

            if ($PSCmdlet.ShouldProcess($configPath, 'merge mcpServers block')) {

                $runningError = if ($PSBoundParameters.ContainsKey('ProcessList')) {
                    Get-LgxClaudeDesktopRunningError -ProcessList $ProcessList -Force:$Force
                } else {
                    Get-LgxClaudeDesktopRunningError -Force:$Force
                }
                if ($runningError) {
                    Write-Error "ERROR: $runningError"
                    exit 1
                }

                $configDir = Split-Path -Parent $configPath
                if (-not (Test-Path -LiteralPath $configDir -PathType Container)) {
                    $null = New-Item -ItemType Directory -Path $configDir -Force
                }

                try {
                    $bakPath = Set-LgxMcpServerEntry -Path $configPath -Section 'mcpServers' -Name $ServerName -Block $cfg.Block
                } catch {
                    Write-Error "ERROR: $($_.Exception.Message)"
                    exit 1
                }
                if ($bakPath) { Write-Host "Backup: $bakPath" }
                Assert-LgxConfigEntry -Path $configPath -Section 'mcpServers' -Name $ServerName
                Write-Host "Written: $configPath (verified '$ServerName' is present)"
            }
        }
    }

    # -----------------------------------------------------------------------
    # VSCode
    # -----------------------------------------------------------------------
    'VSCode' {
        $preview = [ordered] @{
            servers = [ordered] @{ $ServerName = $cfg.Block }
        } | ConvertTo-Json -Depth $script:LgxJsonDepth

        Write-Host "VSCode -- server block for '$ServerName':"
        Write-Host $preview

        if ($WriteConfig) {
            $configPath = Join-Path (Join-Path $env:APPDATA 'Code') (Join-Path 'User' 'mcp.json')

            if ($PSCmdlet.ShouldProcess($configPath, 'merge servers block')) {

                $configDir = Split-Path -Parent $configPath
                if (-not (Test-Path -LiteralPath $configDir -PathType Container)) {
                    $null = New-Item -ItemType Directory -Path $configDir -Force
                }

                try {
                    $bakPath = Set-LgxMcpServerEntry -Path $configPath -Section 'servers' -Name $ServerName -Block $cfg.Block
                } catch {
                    Write-Error "ERROR: $($_.Exception.Message)"
                    exit 1
                }
                if ($bakPath) { Write-Host "Backup: $bakPath" }
                Assert-LgxConfigEntry -Path $configPath -Section 'servers' -Name $ServerName
                Write-Host "Written: $configPath (verified '$ServerName' is present)"
            }
        }
    }
}
