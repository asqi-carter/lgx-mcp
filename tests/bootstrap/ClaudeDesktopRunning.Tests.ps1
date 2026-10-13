#Requires -Modules Pester
# Pester tests for the Claude Desktop running check (setup-mcp-client.ps1, install.ps1)
# and the post-write read-back verification.
# Run from the repo root:  Invoke-Pester tests/bootstrap

BeforeAll {
    $script:Repo      = Resolve-Path (Join-Path $PSScriptRoot '../..')
    $script:Bootstrap = Join-Path $script:Repo 'bootstrap'
    $script:Client    = Join-Path $script:Bootstrap 'setup-mcp-client.ps1'
    $script:Arrow     = [string][char]0x2192
    $script:Refusal   = "Quit Claude Desktop fully (tray $($script:Arrow) Quit) and re-run"

    function script:New-FakeProc([string] $Name, [string] $Path = '', [int] $Id = 100) {
        [pscustomobject]@{ Name = $Name; Id = $Id; Path = $Path }
    }

    function script:Import-FromScript([string] $File, [string[]] $Names) {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:Bootstrap $File), [ref]$null, [ref]$null)
        $funcs = @($ast.FindAll({ param($n)
            $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in $Names }, $true))
        return $funcs
    }
}

Describe 'Claude Desktop running check' -ForEach @('setup-mcp-client.ps1', 'install.ps1') {
    BeforeAll {
        $funcs = Import-FromScript $_ @('Get-LgxClaudeDesktopProcess', 'Get-LgxClaudeDesktopRunningError')
        $script:count = $funcs.Count
        foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }
    }

    It 'defines both helpers in <_>' { $script:count | Should -Be 2 }

    It 'detects a process named Claude' {
        Get-LgxClaudeDesktopRunningError -ProcessList @((New-FakeProc 'svchost'), (New-FakeProc 'Claude')) |
            Should -Match ([regex]::Escape($script:Refusal))
    }
    It 'detects a process named claude' {
        Get-LgxClaudeDesktopRunningError -ProcessList @(New-FakeProc 'claude') |
            Should -Match ([regex]::Escape($script:Refusal))
    }
    It 'detects an MSIX package process' {
        $p = New-FakeProc 'Claude' 'C:\Program Files\WindowsApps\Claude_1.2.3.0_x64__abc123\app\Claude.exe'
        Get-LgxClaudeDesktopRunningError -ProcessList @($p) | Should -Match ([regex]::Escape($script:Refusal))
    }
    It 'ignores a process list containing only cowork-svc with the WindowsApps path' {
        $p = New-FakeProc 'cowork-svc' 'C:\Program Files\WindowsApps\Claude_1.2.3.0_x64__abc123\app\resources\cowork-svc.exe'
        $hits = Get-LgxClaudeDesktopProcess -ProcessList @($p); @($hits).Count | Should -Be 0
        Get-LgxClaudeDesktopRunningError -ProcessList @($p) | Should -BeNullOrEmpty
    }
    It 'ignores any non-Claude.exe helper under AnthropicClaude' {
        $p = New-FakeProc 'Update' 'C:\Users\Example\AppData\Local\AnthropicClaude\Update.exe'
        $hits = Get-LgxClaudeDesktopProcess -ProcessList @($p); @($hits).Count | Should -Be 0
    }
    It 'hits Claude.exe in WindowsApps' {
        $p = New-FakeProc 'Claude' 'C:\Program Files\WindowsApps\Claude_1.2.3.0_x64__abc123\app\Claude.exe'
        $hits = Get-LgxClaudeDesktopProcess -ProcessList @($p); @($hits).Count | Should -Be 1
    }
    It 'hits Claude.exe under AnthropicClaude' {
        $p = New-FakeProc 'claude' 'C:\Users\Example\AppData\Local\AnthropicClaude\app-0.9.0\Claude.exe'
        $hits = Get-LgxClaudeDesktopProcess -ProcessList @($p); @($hits).Count | Should -Be 1
    }
    It 'does not hit a native CLI claude.exe under .local\bin' {
        $p = New-FakeProc 'claude' 'C:\Users\Example\.local\bin\claude.exe'
        $hits = Get-LgxClaudeDesktopProcess -ProcessList @($p); @($hits).Count | Should -Be 0
    }
    It 'still hits a Claude process with a null path' {
        $p = [pscustomobject]@{ Name = 'Claude'; Id = 7; Path = $null }
        $hits = Get-LgxClaudeDesktopProcess -ProcessList @($p); @($hits).Count | Should -Be 1
    }
    It 'honours the LGX_MCP_PROCESS_LIST_JSON seam for cowork-svc' {
        $env:LGX_MCP_PROCESS_LIST_JSON = '[{"Name":"cowork-svc","Id":9,"Path":"C:\\Program Files\\WindowsApps\\Claude_1.2.3.0_x64__abc123\\app\\resources\\cowork-svc.exe"}]'
        try { $hits = Get-LgxClaudeDesktopProcess; @($hits).Count | Should -Be 0 }
        finally { Remove-Item Env:LGX_MCP_PROCESS_LIST_JSON -ErrorAction SilentlyContinue }
    }
    It 'the check ignores ~\.local\bin\claude.exe' {
        $p = New-FakeProc 'claude' 'C:\Users\Example\.local\bin\claude.exe'
        Get-LgxClaudeDesktopRunningError -ProcessList @($p) | Should -BeNullOrEmpty
    }
    It 'the check matches the AnthropicClaude path' {
        $p = New-FakeProc 'claude' 'C:\Users\Example\AppData\Local\AnthropicClaude\app-0.9.0\claude.exe'
        Get-LgxClaudeDesktopRunningError -ProcessList @($p) | Should -Match ([regex]::Escape($script:Refusal))
    }
    It 'returns null when no Claude process is listed' {
        Get-LgxClaudeDesktopRunningError -ProcessList @((New-FakeProc 'svchost'), (New-FakeProc 'ClaudeHelperTool')) |
            Should -BeNullOrEmpty
    }
    It 'returns null for an empty list' {
        Get-LgxClaudeDesktopRunningError -ProcessList @() | Should -BeNullOrEmpty
    }
    It '-Force bypasses the check' {
        Get-LgxClaudeDesktopRunningError -ProcessList @(New-FakeProc 'Claude') -Force | Should -BeNullOrEmpty
    }
}

Describe 'setup-mcp-client.ps1 -WriteConfig' {
    BeforeEach {
        $script:tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        $script:app = Join-Path $script:tmp 'app'
        New-Item -ItemType Directory -Force -Path $script:app | Out-Null
        Set-Content -LiteralPath (Join-Path $script:app 'LogixMcp.exe') -Value 'x'
        $script:oldApp = $env:APPDATA; $script:oldLocal = $env:LOCALAPPDATA
        $env:APPDATA = Join-Path $script:tmp 'roaming'
        $env:LOCALAPPDATA = Join-Path $script:tmp 'local'
        $script:cfg = Join-Path (Join-Path $env:APPDATA 'Claude') 'claude_desktop_config.json'
    }
    AfterEach {
        $env:APPDATA = $script:oldApp; $env:LOCALAPPDATA = $script:oldLocal
        Remove-Item -Recurse -Force $script:tmp -ErrorAction SilentlyContinue
    }

    It 'refuses to write when a Claude process is running' {
        { & $script:Client -Client ClaudeDesktop -AppDir $script:app -WriteConfig `
            -ProcessList @((New-FakeProc 'Claude')) 6>$null } | Should -Throw "*$($script:Refusal)*"
        Test-Path -LiteralPath $script:cfg | Should -BeFalse
    }
    It 'refuses to write when a lowercase claude process is running' {
        { & $script:Client -Client ClaudeDesktop -AppDir $script:app -WriteConfig `
            -ProcessList @((New-FakeProc 'claude')) 6>$null } | Should -Throw "*$($script:Refusal)*"
    }
    It 'refuses to write when an MSIX package process is running' {
        $p = New-FakeProc 'Claude' 'C:\Program Files\WindowsApps\Claude_1.2.3.0_x64__abc123\app\Claude.exe'
        { & $script:Client -Client ClaudeDesktop -AppDir $script:app -WriteConfig `
            -ProcessList @($p) 6>$null } | Should -Throw "*$($script:Refusal)*"
    }
    It '-Force bypasses the refusal and verifies the written entry' {
        & $script:Client -Client ClaudeDesktop -AppDir $script:app -WriteConfig -Force `
            -ProcessList @((New-FakeProc 'Claude')) 6>$null
        $doc = Get-Content -LiteralPath $script:cfg -Raw | ConvertFrom-Json
        $doc.mcpServers.PSObject.Properties['lgx-mcp'] | Should -Not -BeNullOrEmpty
    }
    It 'writes when no Claude process is running' {
        & $script:Client -Client ClaudeDesktop -AppDir $script:app -WriteConfig -ProcessList @() 6>$null
        (Get-Content -LiteralPath $script:cfg -Raw | ConvertFrom-Json).mcpServers.PSObject.Properties['lgx-mcp'] |
            Should -Not -BeNullOrEmpty
    }
}

Describe 'Assert-LgxConfigEntry (write verification)' {
    BeforeAll {
        foreach ($f in (Import-FromScript 'setup-mcp-client.ps1' @('Assert-LgxConfigEntry'))) {
            . ([scriptblock]::Create($f.Extent.Text))
        }
        $script:file = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.json')
    }
    AfterAll { Remove-Item -Force $script:file -ErrorAction SilentlyContinue }

    It 'passes when the entry is present' {
        Set-Content -LiteralPath $script:file -Value '{"mcpServers":{"lgx-mcp":{"command":"x"}}}'
        { Assert-LgxConfigEntry -Path $script:file -Section mcpServers -Name 'lgx-mcp' } | Should -Not -Throw
    }
    It 'errors when the entry is absent' {
        Set-Content -LiteralPath $script:file -Value '{"mcpServers":{"other":{}}}'
        { Assert-LgxConfigEntry -Path $script:file -Section mcpServers -Name 'lgx-mcp' } | Should -Throw '*not present*'
    }
    It 'errors when the file is missing' {
        { Assert-LgxConfigEntry -Path "$($script:file).nope" -Section mcpServers -Name 'lgx-mcp' } | Should -Throw '*does not exist*'
    }
}

Describe 'install.ps1 ordering' {
    It 'runs the Desktop-running check before the build' {
        $text = Get-Content -Raw (Join-Path $script:Bootstrap 'install.ps1')
        $check = $text.IndexOf('Get-LgxClaudeDesktopRunningError -')
        $build = $text.IndexOf("Step 3 'Build")
        $check | Should -BeGreaterThan 0
        $build | Should -BeGreaterThan 0
        $check | Should -BeLessThan $build
        $text.IndexOf('Get-LgxClaudeDesktopRunningError -ProcessList') | Should -BeLessThan $text.IndexOf('Step 1 ')
    }
}

Describe 'install.ps1 refuses before building (real -File run)' {
    It 'exits non-zero with the quit message and builds nothing when Claude is running' {
        $env:LGX_MCP_PROCESS_LIST_JSON = '[{"Name":"Claude","Id":4242,"Path":null}]'
        try {
            $out = & pwsh -NoProfile -File (Join-Path $script:Bootstrap 'install.ps1') `
                -Client ClaudeDesktop -InstallDir (Join-Path ([IO.Path]::GetTempPath()) 'lgx-never-created') 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $out | Should -Match ([regex]::Escape($script:Refusal))
            $out | Should -Not -Match 'Step 1'
        } finally {
            Remove-Item Env:LGX_MCP_PROCESS_LIST_JSON -ErrorAction SilentlyContinue
        }
    }
}
