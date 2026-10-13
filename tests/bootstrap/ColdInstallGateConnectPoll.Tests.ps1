#Requires -Modules Pester
# Pester 5 tests for the logix_connect -> logix_connect_status polling in
# bootstrap/cold-install-gate.ps1 (Invoke-LogixConnectSdk).  A fake MCP server
# (a child pwsh reading NDJSON on stdin) scripts the status sequence.
# Run from the repo root:  Invoke-Pester tests/bootstrap

BeforeAll {
    $script:Repo = Resolve-Path (Join-Path $PSScriptRoot '../..')
    $script:Gate = Join-Path $script:Repo 'bootstrap/cold-install-gate.ps1'

    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:Gate, [ref]$null, [ref]$null)
    $funcs = @($ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $n.Name -in 'Send-McpMessage', 'ConvertTo-Hashtable', 'Receive-McpMessage',
                    'Invoke-LogixToolPayload', 'Invoke-LogixConnectSdk' }, $true))
    $script:FuncCount = $funcs.Count
    foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }

    $script:isSdkBuild = $true

    # The fake server: logix_connect answers $First; each logix_connect_status pops the next
    # scripted payload (the last one repeats).
    function script:Start-FakeServer([string] $First, [string[]] $Statuses) {
        $serverScript = Join-Path $TestDrive ("fake-$([guid]::NewGuid().ToString('N')).ps1")
        $statusLiteral = ($Statuses | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ','
        @"
`$first = '$($First.Replace("'", "''"))'
`$statuses = @($statusLiteral)
`$i = 0
while (`$null -ne (`$line = [Console]::In.ReadLine())) {
    `$req = `$line | ConvertFrom-Json
    if (`$req.params.name -eq 'logix_connect') { `$text = `$first }
    else {
        `$text = `$statuses[[math]::Min(`$i, `$statuses.Count - 1)]
        `$i++
    }
    `$resp = @{ jsonrpc = '2.0'; id = `$req.id; result = @{ content = @(@{ type = 'text'; text = `$text }) } }
    [Console]::Out.WriteLine((`$resp | ConvertTo-Json -Depth 10 -Compress))
    [Console]::Out.Flush()
}
"@ | Set-Content -LiteralPath $serverScript
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = if (Get-Command pwsh -ErrorAction SilentlyContinue) { (Get-Command pwsh).Source } else { (Get-Command powershell).Source }
        $psi.Arguments = "-NoProfile -File `"$serverScript`""
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.UseShellExecute = $false
        return [System.Diagnostics.Process]::Start($psi)
    }

    $script:Connecting = '{"status":"connecting","connectId":"c1","phase":"acquiring-token","elapsedMs":20000,"pollWith":"logix_connect_status"}'
    $script:Connected  = '{"status":"connected","backend":"sdk"}'
    $script:Failed     = '{"status":"failed","error":"boom","kind":"ConnectError"}'
}

Describe 'cold-install-gate connect polling' {
    AfterEach {
        if ($script:Proc -and -not $script:Proc.HasExited) { $script:Proc.Kill() }
    }

    It 'loads the helper functions from the gate' {
        $script:FuncCount | Should -Be 5
    }

    It 'gate polls through a fake server returning connecting x3 then connected and passes' {
        $script:TimeoutSeconds = 60
        $script:Proc = Start-FakeServer $script:Connecting @($script:Connecting, $script:Connecting, $script:Connected)
        $out = Invoke-LogixConnectSdk -Process $script:Proc -ReqId 4 -PollIntervalMs 50 6>&1
        $out[-1] | Should -BeTrue
        ($out | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } |
            ForEach-Object { "$_" }) -join "`n" | Should -Match 'phase=acquiring-token'
    }

    It 'reports each real phase while polling, not starting' {
        $script:TimeoutSeconds = 60
        $open = '{"status":"connecting","connectId":"c1","phase":"open-project","elapsedMs":20000,"pollWith":"logix_connect_status"}'
        $gate = '{"status":"connecting","connectId":"c1","phase":"gate-derivation","elapsedMs":25000,"pollWith":"logix_connect_status"}'
        $script:Proc = Start-FakeServer $open @($gate, $script:Connected)
        $out = Invoke-LogixConnectSdk -Process $script:Proc -ReqId 4 -PollIntervalMs 50 6>&1
        $out[-1] | Should -BeTrue
        $log = ($out | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } |
            ForEach-Object { "$_" }) -join "`n"
        $log | Should -Match 'phase=open-project'
        $log | Should -Match 'phase=gate-derivation'
        $log | Should -Not -Match 'phase=starting'
    }

    It 'passes immediately when logix_connect answers connected' {
        $script:TimeoutSeconds = 60
        $script:Proc = Start-FakeServer $script:Connected @($script:Failed)
        (Invoke-LogixConnectSdk -Process $script:Proc -ReqId 4 -PollIntervalMs 50 6>$null) | Should -BeTrue
    }

    It 'fails when the status poll reports failed' {
        $script:TimeoutSeconds = 60
        $script:Proc = Start-FakeServer $script:Connecting @($script:Connecting, $script:Failed)
        (Invoke-LogixConnectSdk -Process $script:Proc -ReqId 4 -PollIntervalMs 50 6>$null 2>$null) | Should -BeFalse
    }

    It 'fails when -TimeoutSeconds is exceeded while still connecting' {
        $script:TimeoutSeconds = 2
        $script:Proc = Start-FakeServer $script:Connecting @($script:Connecting)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        (Invoke-LogixConnectSdk -Process $script:Proc -ReqId 4 -PollIntervalMs 100 6>$null) | Should -BeFalse
        $sw.Elapsed.TotalSeconds | Should -BeGreaterThan 1.5
        $sw.Elapsed.TotalSeconds | Should -BeLessThan 10
    }
}
