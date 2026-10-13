#Requires -Modules Pester
# Pester 5 tests for the SDK version pre-check (install.ps1, setup.ps1) and the
# lib/net10.0 + FtspAdapterLDSDK.exe pre-check (provision-sdk.ps1).
# Run from the repo root:  Invoke-Pester tests/bootstrap

BeforeAll {
    $script:Repo      = Resolve-Path (Join-Path $PSScriptRoot '../..')
    $script:Bootstrap = Join-Path $script:Repo 'bootstrap'
    $script:Pin       = '2.2.1109'
    # The scripts address Windows paths (sdk\vendor, vcruntime140.dll), so the
    # end-to-end script runs are Windows-only.
    $script:OnWindows = ($env:OS -eq 'Windows_NT')
    $script:Shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
    $script:Wording   = 'Logix Designer SDK 2.2.1109 or the version in Directory.Build.props'

    # $NuspecVersion defaults to $Version; pass it (or '' for no nuspec) to make
    # the file name and the nuspec disagree.
    function script:New-FakeNupkg([string] $Dir, [string] $Version, [string[]] $Entries, $NuspecVersion = $null) {
        Add-Type -AssemblyName System.IO.Compression
        if ($null -eq $NuspecVersion) { $NuspecVersion = $Version }
        $path = Join-Path $Dir "RockwellAutomation.LogixDesigner.CSClient.$Version.nupkg"
        $fs = [System.IO.File]::Create($path)
        $zip = [System.IO.Compression.ZipArchive]::new($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($e in $Entries) {
            $entry = $zip.CreateEntry($e)
            $w = [System.IO.StreamWriter]::new($entry.Open()); $w.Write('x'); $w.Dispose()
        }
        if ($NuspecVersion) {
            $entry = $zip.CreateEntry('RockwellAutomation.LogixDesigner.CSClient.nuspec')
            $w = [System.IO.StreamWriter]::new($entry.Open())
            $w.Write("<?xml version=`"1.0`"?><package xmlns=`"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd`"><metadata><id>RockwellAutomation.LogixDesigner.CSClient</id><version>$NuspecVersion</version></metadata></package>")
            $w.Dispose()
        }
        $zip.Dispose(); $fs.Dispose()
        return $path
    }

    function script:New-AppDir([string] $Root) {
        $app = Join-Path $Root 'app'
        New-Item -ItemType Directory -Force -Path (Join-Path $app 'sdk') | Out-Null
        Set-Content -LiteralPath (Join-Path (Join-Path $app 'sdk') 'LogixMcp.SdkAdapter.dll') -Value 'x'
        return $app
    }

    function script:Invoke-Script([string] $Name, [string[]] $ScriptArgs) {
        $out = & $script:Shell -NoProfile -File (Join-Path $script:Bootstrap $Name) @ScriptArgs 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }
}

Describe 'version helpers' -ForEach @('install.ps1', 'setup.ps1') {
    BeforeAll {
        # Load the version helpers from the script via the AST, without running it.
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:Bootstrap $_), [ref]$null, [ref]$null)
        $funcs = @($ast.FindAll({ param($n)
            $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $n.Name -in 'Get-NupkgVersion', 'Get-NupkgVersionError', 'ConvertTo-SdkVersion', 'Compare-SdkVersion', 'Select-LatestNupkg' }, $true))
        $script:count = $funcs.Count
        foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }
        $script:d = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:d | Out-Null
    }
    AfterAll { Remove-Item -Recurse -Force $script:d -ErrorAction SilentlyContinue }

    It 'defines all helpers in <_>' {
        $script:count | Should -Be 5
    }
    It 'nupkg version comes from the nuspec, not the file name' {
        # File name says 2.2.1109; the nuspec says 2.1.0.
        $pkg = New-FakeNupkg $script:d '2.2.1109' @('lib/net10.0/a.dll') '2.1.0'
        Get-NupkgVersion $pkg | Should -Be '2.1.0'
        Get-NupkgVersionError $pkg $script:Pin | Should -Match ([regex]::Escape('Found 2.1.0'))
        # And the other way round: misleading file name, correct nuspec.
        $pkg2 = New-FakeNupkg $script:d '9.9.9' @('lib/net10.0/a.dll') '2.2.1109'
        Get-NupkgVersion $pkg2 | Should -Be '2.2.1109'
        Get-NupkgVersionError $pkg2 $script:Pin | Should -BeNullOrEmpty
    }
    It 'reports unknown when the package has no nuspec, ignoring the file name' {
        $pkg = New-FakeNupkg $script:d '2.2.1109' @('lib/net10.0/a.dll') ''
        Get-NupkgVersion $pkg | Should -BeNullOrEmpty
        Get-NupkgVersionError $pkg $script:Pin | Should -Match 'unknown'
    }
    It '2.2.999 < 2.2.1109' {
        Compare-SdkVersion '2.2.999' '2.2.1109' | Should -Be -1
        Compare-SdkVersion '2.2.1109' '2.2.999' | Should -Be 1
    }
    It '2.2.1109.0 equals 2.2.1109' {
        Compare-SdkVersion '2.2.1109.0' '2.2.1109' | Should -Be 0
    }
    It 'accepts a nuspec version of 2.2.1109.0 against the pin' {
        $pkg = New-FakeNupkg $script:d '2.2.1109' @('lib/net10.0/a.dll') '2.2.1109.0'
        Get-NupkgVersionError $pkg $script:Pin | Should -BeNullOrEmpty
    }
    It 'rejects 2.2.999 against the pin (numeric, not lexical)' {
        $pkg = New-FakeNupkg $script:d '2.2.999' @('lib/net10.0/a.dll')
        Get-NupkgVersionError $pkg $script:Pin | Should -Match ([regex]::Escape('Found 2.2.999'))
    }
    It 'names found version, required version and the wording on mismatch' {
        $pkg = New-FakeNupkg $script:d '2.1.0' @('lib/net10.0/a.dll')
        $msg = Get-NupkgVersionError $pkg $script:Pin
        $msg | Should -Match ([regex]::Escape('Found 2.1.0'))
        $msg | Should -Match ([regex]::Escape('required 2.2.1109'))
        $msg | Should -Match ([regex]::Escape($script:Wording))
    }
    It 'does not treat a prerelease as equal to the pin' {
        $pkg = New-FakeNupkg $script:d '2.2.1109' @('lib/net10.0/a.dll') '2.2.1109-beta'
        Get-NupkgVersionError $pkg $script:Pin | Should -Match ([regex]::Escape('Found 2.2.1109-beta'))
    }
    It 'selects the latest package numerically, not by name' {
        $sub = Join-Path $script:d 'latest'; New-Item -ItemType Directory -Path $sub | Out-Null
        New-FakeNupkg $sub '2.2.999' @('a') | Out-Null
        New-FakeNupkg $sub '2.2.1109' @('a') | Out-Null
        (Select-LatestNupkg (Get-ChildItem $sub -Filter *.nupkg)).Name | Should -Be 'RockwellAutomation.LogixDesigner.CSClient.2.2.1109.nupkg'
    }
}

Describe 'install.ps1 .localfeed copy' {
    BeforeAll {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:Bootstrap 'install.ps1'), [ref]$null, [ref]$null)
        $funcs = @($ast.FindAll({ param($n)
            $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $n.Name -in 'Get-NupkgNuspecField', 'Copy-NupkgToFeed' }, $true))
        foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }
        $script:d = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:d | Out-Null
    }
    AfterAll { Remove-Item -Recurse -Force $script:d -ErrorAction SilentlyContinue }

    It 'names the nuspec package id and version, not the file name' {
        $src = Join-Path $script:d 'src1'; New-Item -ItemType Directory -Path $src | Out-Null
        $pkg = New-FakeNupkg $src '9.9.9' @('lib/net10.0/a.dll') '2.2.1109'
        $r = Copy-NupkgToFeed -Path $pkg -Feed (Join-Path $script:d 'feed1')
        $r.Id | Should -Be 'RockwellAutomation.LogixDesigner.CSClient'
        $r.Version | Should -Be '2.2.1109'
        $r.Name | Should -Be 'RockwellAutomation.LogixDesigner.CSClient.2.2.1109.nupkg'
    }
    It 'leaves no renamed or older copies of the package in .localfeed' {
        $feed = Join-Path $script:d 'feed2'
        $src = Join-Path $script:d 'src2'; New-Item -ItemType Directory -Path $src | Out-Null
        $pkg = New-FakeNupkg $src '2.2.1109' @('lib/net10.0/a.dll')
        # Earlier runs left a renamed copy and an older version in the feed.
        New-Item -ItemType Directory -Path $feed | Out-Null
        Copy-Item $pkg (Join-Path $feed 'CSClient (renamed copy).nupkg')
        $old = New-FakeNupkg $feed '2.1.0' @('lib/net10.0/a.dll')
        Set-Content -LiteralPath (Join-Path $feed 'Other.Package.1.0.0.nupkg') -Value 'not a zip'
        $r = Copy-NupkgToFeed -Path $pkg -Feed $feed
        $names = @(Get-ChildItem $feed -Filter *.nupkg | ForEach-Object Name | Sort-Object)
        $names | Should -Contain $r.Name
        $names | Should -Contain 'Other.Package.1.0.0.nupkg'
        @($names | Where-Object { $_ -like 'CSClient*' -or $_ -like '*CSClient.2.1.0*' }).Count | Should -Be 0
        @($names | Where-Object { $_ -like 'RockwellAutomation.LogixDesigner.CSClient*' }).Count | Should -Be 1
    }
    It 'running twice keeps exactly one copy' {
        $feed = Join-Path $script:d 'feed3'
        $src = Join-Path $script:d 'src3'; New-Item -ItemType Directory -Path $src | Out-Null
        $pkg = New-FakeNupkg $src '2.2.1109' @('lib/net10.0/a.dll')
        Copy-NupkgToFeed -Path $pkg -Feed $feed | Out-Null
        Copy-NupkgToFeed -Path (Join-Path $feed 'RockwellAutomation.LogixDesigner.CSClient.2.2.1109.nupkg') -Feed $feed | Out-Null
        @(Get-ChildItem $feed -Filter *.nupkg).Count | Should -Be 1
    }
}

Describe 'setup.ps1 AppDir check' {
    It 'a nonexistent -AppDir gives AppDir not found, not the no-SDK message' {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        $r = Invoke-Script 'setup.ps1' @('-AppDir', $missing)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match 'AppDir not found'
        $r.Output | Should -Not -Match 'no-SDK build'
    }
}

Describe 'pin consistency' {
    It 'matches the PackageReference in the SdkAdapter csproj' {
        $csproj = Get-Content -Raw (Join-Path $script:Repo 'src/LogixMcp.SdkAdapter/LogixMcp.SdkAdapter.csproj')
        $csproj | Should -Match ('RockwellAutomation\.LogixDesigner\.CSClient"\s+Version="' + [regex]::Escape($script:Pin) + '"')
    }
    It 'is the same constant in install.ps1, setup.ps1 and provision-sdk.ps1' {
        foreach ($n in 'install.ps1', 'setup.ps1', 'provision-sdk.ps1') {
            Get-Content -Raw (Join-Path $script:Bootstrap $n) | Should -Match "\`$RequiredSdkVersion = '2\.2\.1109'"
        }
    }
}

Describe 'setup.ps1 version pre-check' -Skip:($env:OS -ne 'Windows_NT') {
    BeforeEach { $script:tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $script:tmp | Out-Null }
    AfterEach { Remove-Item -Recurse -Force $script:tmp -ErrorAction SilentlyContinue }

    It 'fails before provisioning when the nupkg version differs' {
        $app = New-AppDir $script:tmp
        $pkg = New-FakeNupkg $script:tmp '2.1.0' @('lib/net10.0/a.dll')
        $r = Invoke-Script 'setup.ps1' @('-AppDir', $app, '-NupkgPath', $pkg)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match ([regex]::Escape('Found 2.1.0'))
        $r.Output | Should -Match ([regex]::Escape('required 2.2.1109'))
        $r.Output | Should -Match ([regex]::Escape($script:Wording))
        Test-Path (Join-Path (Join-Path $app 'sdk') 'vendor') | Should -BeFalse
    }
}

Describe 'provision-sdk.ps1 package contents pre-check' -Skip:($env:OS -ne 'Windows_NT') {
    BeforeEach { $script:tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $script:tmp | Out-Null }
    AfterEach { Remove-Item -Recurse -Force $script:tmp -ErrorAction SilentlyContinue }

    It 'names lib/net10.0 when it is missing' {
        $app = New-AppDir $script:tmp
        $pkg = New-FakeNupkg $script:tmp '2.2.1109' @('lib/net8.0/a.dll', 'FtspAdapter/FtspAdapterLDSDK.exe')
        $r = Invoke-Script 'provision-sdk.ps1' @('-AppDir', $app, '-NupkgPath', $pkg)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match ([regex]::Escape('Missing: lib/net10.0'))
        $r.Output | Should -Not -Match 'Missing: FtspAdapterLDSDK.exe'
    }
    It 'names FtspAdapterLDSDK.exe when it is missing' {
        $app = New-AppDir $script:tmp
        $pkg = New-FakeNupkg $script:tmp '2.2.1109' @('lib/net10.0/RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll')
        $r = Invoke-Script 'provision-sdk.ps1' @('-AppDir', $app, '-NupkgPath', $pkg)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match ([regex]::Escape('Missing: FtspAdapterLDSDK.exe'))
        $r.Output | Should -Not -Match ([regex]::Escape('Missing: lib/net10.0'))
    }
    It 'names both when both are missing' {
        $app = New-AppDir $script:tmp
        $pkg = New-FakeNupkg $script:tmp '2.2.1109' @('readme.txt')
        $r = Invoke-Script 'provision-sdk.ps1' @('-AppDir', $app, '-NupkgPath', $pkg)
        $r.ExitCode | Should -Not -Be 0
        $r.Output | Should -Match ([regex]::Escape('Missing: lib/net10.0'))
        $r.Output | Should -Match ([regex]::Escape('Missing: FtspAdapterLDSDK.exe'))
    }
}
