#Requires -Modules Pester
# Pester 5 tests for newest-Designer selection in bootstrap/cold-install-gate.ps1
# (Resolve-LogixDesignerVersion).  Uses fake install trees under $TestDrive.
# Run from the repo root:  Invoke-Pester tests/bootstrap

BeforeAll {
    $script:Repo = Resolve-Path (Join-Path $PSScriptRoot '../..')
    $script:Gate = Join-Path $script:Repo 'bootstrap/cold-install-gate.ps1'

    # Load the resolver functions from the gate via the AST, without running it.
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:Gate, [ref]$null, [ref]$null)
    $funcs = @($ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $n.Name -in 'Get-LogixExeVersion', 'Resolve-LogixDesignerVersion' }, $true))
    $script:FuncCount = $funcs.Count
    foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }

    function script:New-Dir([string] $Path) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
    function script:New-EnuTree([string] $Root, [int[]] $Majors) {
        foreach ($m in $Majors) {
            New-Dir (Join-Path $Root "ENU/v$m/Bin")
            Set-Content -LiteralPath (Join-Path $Root "ENU/v$m/Bin/LogixDesigner.exe") -Value 'x'
        }
    }
}

Describe 'cold-install-gate newest-Designer selection' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Dir $script:Root
        # The fake exes are not real PE files: derive the FileVersion from the
        # ENU\vNN folder name (vNN -> NN.0) unless a test mocks it itself.
        Mock Get-LogixExeVersion {
            if ($Path -match '[\\/]v(\d+)[\\/]') { [pscustomobject]@{ Major = [int]$Matches[1]; Minor = 0 } }
            else { [pscustomobject]@{ Major = 0; Minor = 0 } }
        }
    }

    It 'defines the resolver and exposes -LogixDesignerVersion' {
        $script:FuncCount | Should -Be 2
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:Gate, [ref]$null, [ref]$null)
        $ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'LogixDesignerVersion'
    }

    It 'a side-by-side v24/v28/v36 tree in the ENU\vNN\Bin layout selects 36' {
        New-EnuTree $script:Root @(28, 36, 24)
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null
        $r.Major | Should -Be 36
        $r.Revision | Should -Be '36.0'
        $r.Candidates.Count | Should -Be 3
    }

    It 'the legacy 36.00 layout selects 36' {
        foreach ($d in '34.00', '36.00', '32.00') { New-Dir (Join-Path $script:Root $d) }
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null
        $r.Major | Should -Be 36
        $r.Minor | Should -Be 0
        $r.Revision | Should -Be '36.0'
    }

    It '-LogixDesignerVersion overrides the automatic choice' {
        New-EnuTree $script:Root @(24, 28, 36)
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root -Override 28 6>$null
        $r.Major | Should -Be 28
        $r.Revision | Should -Be '28.0'
    }

    It 'an override that is not installed fails naming what is installed' {
        New-EnuTree $script:Root @(24, 36)
        { Resolve-LogixDesignerVersion -InstallRoot $script:Root -Override 30 6>$null } |
            Should -Throw '*-LogixDesignerVersion 30*Installed: 36.0, 24.0*'
    }

    It 'no install produces a clear error' {
        $missing = Join-Path $script:Root 'does-not-exist'
        { Resolve-LogixDesignerVersion -InstallRoot $missing 6>$null } |
            Should -Throw '*Logix Designer version could not be determined*Directory not found*'
        # An existing but empty root is also a clear error.
        { Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null } |
            Should -Throw '*Logix Designer version could not be determined*No legacy*'
    }

    It 'falls back to the newest LogixDesigner.exe by FileVersion (source 2)' {
        foreach ($d in 'a', 'b', 'c') {
            New-Dir (Join-Path $script:Root "odd/$d")
            Set-Content -LiteralPath (Join-Path $script:Root "odd/$d/LogixDesigner.exe") -Value 'x'
        }
        Mock Get-LogixExeVersion {
            switch -Wildcard ($Path) {
                '*/a/*' { [pscustomobject]@{ Major = 32; Minor = 11 } }
                '*/b/*' { [pscustomobject]@{ Major = 36; Minor = 2 } }
                '*/c/*' { [pscustomobject]@{ Major = 36; Minor = 11 } }
            }
        }
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null
        $r.Revision | Should -Be '36.11'
        $r.Source | Should -BeLike '*source 2*'
        $r.Candidates.Count | Should -Be 3
    }

    It 'logs every candidate and the chosen one' {
        New-EnuTree $script:Root @(24, 28, 36)
        $log = (Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>&1 | Out-String)
        foreach ($m in 24, 28, 36) { $log | Should -Match "logix candidate: $m\.0" }
        $log | Should -Match 'logix chosen\s*: 36\.0'
    }

    It 'an ENU\v39 without an exe is skipped and the reason is logged' {
        New-EnuTree $script:Root @(36)
        New-Dir (Join-Path $script:Root 'ENU/v39/Bin')   # no LogixDesigner.exe
        $log = (Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>&1 | Out-String)
        $log | Should -Match 'logix candidate skipped: \[ENU\\v39\].*LogixDesigner\.exe not found'
        $log | Should -Match 'logix chosen\s*: 36\.0'
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null
        $r.Candidates.Count | Should -Be 1
    }

    It 'an ENU\vNN whose exe has an unreadable FileVersion is skipped and logged' {
        New-EnuTree $script:Root @(36, 38)
        Mock Get-LogixExeVersion {
            if ($Path -match '[\\/]v38[\\/]') { throw 'bad version resource' }
            [pscustomobject]@{ Major = 36; Minor = 11 }
        }
        $log = (Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>&1 | Out-String)
        $log | Should -Match 'logix candidate skipped: \[ENU\\v38\].*unreadable'
        $log | Should -Match 'logix chosen\s*: 36\.11'
    }

    It 'flags a vNN folder whose exe FileVersion major is not NN' {
        New-EnuTree $script:Root @(36, 38)
        Mock Get-LogixExeVersion {
            if ($Path -match '[\\/]v38[\\/]') { [pscustomobject]@{ Major = 24; Minor = 1 } }
            else { [pscustomobject]@{ Major = 36; Minor = 11 } }
        }
        $log = (Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>&1 | Out-String)
        $log | Should -Match 'logix candidate FLAGGED: \[ENU\\v38\].*folder says v38 but FileVersion major is 24'
        $log | Should -Not -Match 'FLAGGED: \[ENU\\v36\]'
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null
        ($r.Candidates | Where-Object Label -eq 'ENU\v38').FolderMismatch | Should -BeTrue
        ($r.Candidates | Where-Object Label -eq 'ENU\v36').FolderMismatch | Should -BeFalse
    }

    It 'selects among valid ENU candidates by FileVersion, not folder name' {
        New-EnuTree $script:Root @(36, 38)
        Mock Get-LogixExeVersion {
            if ($Path -match '[\\/]v38[\\/]') { [pscustomobject]@{ Major = 24; Minor = 1 } }
            else { [pscustomobject]@{ Major = 36; Minor = 11 } }
        }
        $r = Resolve-LogixDesignerVersion -InstallRoot $script:Root 6>$null
        $r.Revision | Should -Be '36.11'
        $r.Source | Should -BeLike 'ENU\v36*'
    }
}
