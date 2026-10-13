#Requires -Modules Pester
# Pester tests for the Claude/MCP client config writer in setup-mcp-client.ps1
# (BOM-less UTF-8, ConvertTo-Json depth 100, deep-compare of other entries,
# malformed config left untouched).  install.ps1 runs setup-mcp-client.ps1, so
# it follows the same rule.
# Run from the repo root:  Invoke-Pester tests/bootstrap

BeforeAll {
    $script:Repo      = Resolve-Path (Join-Path $PSScriptRoot '../..')
    $script:Bootstrap = Join-Path $script:Repo 'bootstrap'
    $script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $script:Names     = @('Get-LgxJsonSyntaxError', 'Compare-LgxJsonValue', 'Read-LgxJsonConfig', 'Set-LgxMcpServerEntry')

    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $script:Bootstrap 'setup-mcp-client.ps1'), [ref]$null, [ref]$null)
    $funcs = @($ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in $script:Names }, $true))
    $script:FuncCount = $funcs.Count
    foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }
    $script:LgxJsonDepth = 100

    function script:New-Dir {
        $d = Join-Path ([IO.Path]::GetTempPath()) ("lgxcfg-" + [guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $d
        $d
    }
    function script:New-Block { [ordered]@{ command = 'C:\lgx\LogixMcp.exe'; args = @(); env = @{ LGX_X = '1' } } }
}

Describe 'Claude config writer' {
    It 'defines all helpers' { $script:FuncCount | Should -Be 4 }

    It 'config round-trip with José, nesting depth 15 and a BOM-less file keeps other entries identical' {
        $jose = 'Jos' + [char]0x00E9
        $deep = '"leaf":"' + $jose + '"'
        for ($k = 0; $k -lt 15; $k++) { $deep = '"n' + $k + '":{' + $deep + '}' }
        $deep = '{' + $deep + '}'
        $text = '{"theme":"dark","owner":"' + $jose + '","mcpServers":{"other":{"command":"x","args":["a","b"],"nested":' + $deep + '},"empty":{}},"list":[1,2.5,true,null,[]]}'
        $dir = New-Dir
        try {
            $path = Join-Path $dir 'claude_desktop_config.json'
            [IO.File]::WriteAllText($path, $text, $script:Utf8NoBom)

            $bak = Set-LgxMcpServerEntry -Path $path -Section 'mcpServers' -Name 'lgx-mcp' -Block (New-Block)

            $bytes = [IO.File]::ReadAllBytes($path)
            ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should -BeFalse
            $out = [IO.File]::ReadAllText($path)
            $out | Should -Match ([regex]::Escape($jose))

            $a = $text | ConvertFrom-Json
            $b = $out | ConvertFrom-Json
            Compare-LgxJsonValue -A $a.theme -B $b.theme | Should -BeNullOrEmpty
            Compare-LgxJsonValue -A $a.owner -B $b.owner | Should -BeNullOrEmpty
            Compare-LgxJsonValue -A $a.list -B $b.list | Should -BeNullOrEmpty
            Compare-LgxJsonValue -A $a.mcpServers.other -B $b.mcpServers.other | Should -BeNullOrEmpty
            Compare-LgxJsonValue -A $a.mcpServers.empty -B $b.mcpServers.empty | Should -BeNullOrEmpty
            $b.mcpServers.'lgx-mcp'.command | Should -Be 'C:\lgx\LogixMcp.exe'
            $b.mcpServers.other.nested.n14.n13.n12.n11.n10.n9.n8.n7.n6.n5.n4.n3.n2.n1.n0.leaf | Should -Be $jose
            Test-Path -LiteralPath $bak | Should -BeTrue
        } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'rewrites a BOM-ed file without a BOM' {
        $dir = New-Dir
        try {
            $path = Join-Path $dir 'c.json'
            [IO.File]::WriteAllText($path, '{"a":1}', (New-Object System.Text.UTF8Encoding($true)))
            $null = Set-LgxMcpServerEntry -Path $path -Section 'mcpServers' -Name 'lgx-mcp' -Block (New-Block)
            $bytes = [IO.File]::ReadAllBytes($path)
            $bytes[0] | Should -Be ([byte][char]'{')
        } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'creates a missing file and replaces only our entry' {
        $dir = New-Dir
        try {
            $path = Join-Path $dir 'new.json'
            $bak = Set-LgxMcpServerEntry -Path $path -Section 'servers' -Name 'lgx-mcp' -Block (New-Block)
            $bak | Should -BeNullOrEmpty
            ([IO.File]::ReadAllText($path) | ConvertFrom-Json).servers.'lgx-mcp'.command | Should -Be 'C:\lgx\LogixMcp.exe'
            $b2 = [ordered]@{ command = 'new' }
            $null = Set-LgxMcpServerEntry -Path $path -Section 'servers' -Name 'lgx-mcp' -Block $b2
            ([IO.File]::ReadAllText($path) | ConvertFrom-Json).servers.'lgx-mcp'.command | Should -Be 'new'
        } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'malformed config is left untouched and the message names the file and line' {
        $dir = New-Dir
        try {
            $path = Join-Path $dir 'claude_desktop_config.json'
            $bad = "{`n  `"mcpServers`": {`n    `"other`": { `"command`": `"x`" },`n  }`n}`n"
            [IO.File]::WriteAllText($path, $bad, $script:Utf8NoBom)
            $before = [IO.File]::ReadAllBytes($path)

            $msg = $null
            try { $null = Set-LgxMcpServerEntry -Path $path -Section 'mcpServers' -Name 'lgx-mcp' -Block (New-Block) }
            catch { $msg = $_.Exception.Message }

            $msg | Should -Not -BeNullOrEmpty
            $msg | Should -Match ([regex]::Escape($path))
            $msg | Should -Match 'line 4'
            $msg | Should -Match 'left untouched'
            [IO.File]::ReadAllBytes($path) | Should -Be $before
            @(Get-ChildItem -LiteralPath $dir).Count | Should -Be 1
        } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'reports truncated and non-object configs without touching them' -ForEach @(
        @{ Text = '{"a": 1';  Pattern = 'line 1' }
        @{ Text = '[1,2]';    Pattern = 'not an object' }
        @{ Text = "{`n`"a`": tru }"; Pattern = 'line 2' }
    ) {
        $dir = New-Dir
        try {
            $path = Join-Path $dir 'c.json'
            [IO.File]::WriteAllText($path, $Text, $script:Utf8NoBom)
            { Set-LgxMcpServerEntry -Path $path -Section 'mcpServers' -Name 'lgx-mcp' -Block (New-Block) } |
                Should -Throw "*$Pattern*"
            [IO.File]::ReadAllText($path) | Should -Be $Text
        } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'refuses to write and leaves the original file when the deep-compare finds a difference' {
        $dir = New-Dir
        try {
            $path = Join-Path $dir 'c.json'
            $text = '{"theme":"dark","mcpServers":{"other":{"command":"x","args":["a"]}}}'
            [IO.File]::WriteAllText($path, $text, $script:Utf8NoBom)
            # Simulate a serializer that mangles another entry.
            Mock ConvertTo-Json { '{"theme":"dark","mcpServers":{"other":{"command":"x","args":[]},"lgx-mcp":{"command":"y"}}}' }
            $msg = $null
            try { $null = Set-LgxMcpServerEntry -Path $path -Section 'mcpServers' -Name 'lgx-mcp' -Block (New-Block) }
            catch { $msg = $_.Exception.Message }
            $msg | Should -Match 'Refusing to write'
            $msg | Should -Match 'mcpServers\.other'
            [IO.File]::ReadAllText($path) | Should -Be $text
            @(Get-ChildItem -LiteralPath $dir).Count | Should -Be 1
        } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It 'Compare-LgxJsonValue treats 1e5 and 100000 as equal and detects changes' {
        $a = '{"n":1e5,"s":"x","l":[1,2]}' | ConvertFrom-Json
        $b = '{"n":100000,"s":"x","l":[1,2]}' | ConvertFrom-Json
        Compare-LgxJsonValue -A $a -B $b | Should -BeNullOrEmpty
        $c = '{"n":100000,"s":"X","l":[1,2]}' | ConvertFrom-Json
        Compare-LgxJsonValue -A $a -B $c | Should -Match 'text'
    }
}

Describe 'Claude config writers share one rule' -ForEach @('setup-mcp-client.ps1', 'install.ps1', 'setup.ps1', 'provision-sdk.ps1') {
    It 'does not use Get-Content/Set-Content/ConvertTo-Json -Depth 10 on a client config in <_>' {
        $text = Get-Content -LiteralPath (Join-Path $script:Bootstrap $_) -Raw
        $text | Should -Not -Match 'ConvertTo-Json\s+-Depth\s+10\b'
        $text | Should -Not -Match '(Get|Set)-Content[^\r\n]*(configPath|claude_desktop_config)'
    }
}
