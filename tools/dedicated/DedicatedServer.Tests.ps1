$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$modulePath = Join-Path $PSScriptRoot 'GmodDedicated.psm1'
$launcher = Join-Path $PSScriptRoot 'Start-GmodDedicated.ps1'
Import-Module $modulePath -Force
$checks = 0
function Assert-True([bool]$condition, [string]$message) {
    $script:checks++
    if (-not $condition) { throw "FAIL: $message" }
}
function Assert-Throws([scriptblock]$operation, [string]$message) {
    $caught = $false
    try { & $operation | Out-Null } catch { $caught = $true }
    Assert-True $caught $message
}
$plan = New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -WorkshopCollection '123456789'
Assert-True ($plan.Arguments -contains '+host_workshop_collection') 'Workshop collection flag is present'
Assert-True ($plan.Arguments -contains '123456789') 'Collection ID is passed as a separate token'
Assert-True ($plan.Arguments -contains '+sv_lan' -and $plan.BindAddress -eq '127.0.0.1') 'Default bind and LAN scope are local'
Assert-True (@($plan.Arguments | Where-Object { $_ -match '[\s";]' }).Count -eq 0) 'No argument can break Windows argument joining'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -Map 'gm_construct;quit' } 'Command injection in map is rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -GameMode 'sandbox +quit' } 'Command injection in gamemode is rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -WorkshopCollection '0' } 'Zero collection rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -WorkshopCollection '1 +quit' } 'Command injection in collection rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -BindAddress '0.0.0.0' } 'Wildcard bind rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -BindAddress '8.8.8.8' } 'Public address rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\gterm-private-test\server' -ModulePort 28115 } 'Conflicting ports rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\Steam\steamapps\common\GarrysMod' } 'Steam client root rejected'
Assert-Throws { New-GmodDedicatedPlan -ServerRoot 'C:\' } 'Drive root rejected'

# All process, socket and start operations are replaced by in-process fakes.
$global:GTermTestProcesses = @()
$global:GTermTestUdp = @()
$global:GTermTestTcp = @()
$global:GTermTestStarts = 0
$global:GTermTestSeenPort = $null
function global:Get-CimInstance { param($ClassName,$Filter) $global:GTermTestProcessFilter = $Filter; return $global:GTermTestProcesses }
function global:Get-NetUDPEndpoint { return $global:GTermTestUdp }
function global:Get-NetTCPConnection { param($State) return $global:GTermTestTcp }
function global:Start-Process {
    param($FilePath,$WorkingDirectory,$ArgumentList,$WindowStyle,[switch]$PassThru)
    $global:GTermTestStarts++
    $global:GTermTestSeenPort = [Environment]::GetEnvironmentVariable('GTERM_MODULE_PORT','Process')
    [pscustomobject]@{Id=12345}
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('gterm-dedicated-test-' + [Guid]::NewGuid().ToString('N'))
$priorPort = [Environment]::GetEnvironmentVariable('GTERM_MODULE_PORT','Process')
try {
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'garrysmod') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $testRoot 'srcds_console_win64.exe'),'fake, never executed')
    $marker = @{schema=1;appId='4020';branch='x86-64';serverRoot=$testRoot}
    $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot '.gterm-codex-server-lab.json') -Encoding utf8
    $inspection = & $launcher -Action Inspect -ServerRoot $testRoot
    Assert-True ($inspection.ExecutablePresent -and $inspection.MarkerPresent -and $inspection.ServerProcessIds.Count -eq 0) 'Inspect reads state without launching'
    Assert-True ($global:GTermTestProcessFilter -match 'gmod.exe' -and $global:GTermTestProcessFilter -match 'srcds') 'Process inventory queries both dedicated executable families'
    & $launcher -Action Start -ServerRoot $testRoot -WhatIf | Out-Null
    Assert-True ($global:GTermTestStarts -eq 0) 'WhatIf does not launch'
    $global:GTermTestProcesses = @([pscustomobject]@{ExecutablePath=(Join-Path $testRoot 'srcds_console_win64.exe');ProcessId=991})
    Assert-Throws { & $launcher -Action Start -ServerRoot $testRoot } 'Existing server is not restarted'
    $global:GTermTestProcesses = @()
    $global:GTermTestUdp = @([pscustomobject]@{LocalAddress='0.0.0.0';LocalPort=28115;OwningProcess=992})
    Assert-Throws { & $launcher -Action Start -ServerRoot $testRoot } 'Occupied UDP port rejected'
    $global:GTermTestUdp = @()
    $global:GTermTestTcp = @([pscustomobject]@{LocalAddress='127.0.0.1';LocalPort=27524;OwningProcess=993})
    Assert-Throws { & $launcher -Action Start -ServerRoot $testRoot } 'Occupied module TCP port rejected'
    $global:GTermTestTcp = @()
    $global:GTermTestProcesses = @([pscustomobject]@{ExecutablePath=$null;ProcessId=994})
    Assert-Throws { & $launcher -Action Start -ServerRoot $testRoot } 'Unknown server process path fails closed'
    $global:GTermTestProcesses = @()
    [Environment]::SetEnvironmentVariable('GTERM_MODULE_PORT','19999','Process')
    $started = & $launcher -Action Start -ServerRoot $testRoot -WorkshopCollection '123456789'
    Assert-True ($global:GTermTestStarts -eq 1 -and $started.ProcessId -eq 12345 -and -not $started.Ready) 'Start uses the fake and does not claim readiness'
    Assert-True ($global:GTermTestSeenPort -eq '27524') 'Server receives selected module port'
    Assert-True ([Environment]::GetEnvironmentVariable('GTERM_MODULE_PORT','Process') -eq '19999') 'Parent environment restored'
    $gameDir = Join-Path $testRoot 'garrysmod'
    $linkedTarget = Join-Path $testRoot 'fixture-linked-game'
    New-Item -ItemType Directory -Path $linkedTarget | Out-Null
    Remove-Item -LiteralPath $gameDir
    New-Item -ItemType Junction -Path $gameDir -Target $linkedTarget | Out-Null
    Assert-Throws { & $launcher -Action Start -ServerRoot $testRoot } 'Junction game directory rejected'
    Remove-Item -LiteralPath $gameDir
    New-Item -ItemType Directory -Path $gameDir | Out-Null
    $marker.branch='public'
    $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot '.gterm-codex-server-lab.json') -Encoding utf8
    Assert-Throws { & $launcher -Action Start -ServerRoot $testRoot } 'Mismatched marker rejected'
    Assert-True ($global:GTermTestStarts -eq 1) 'No rejected case launches'
} finally {
    [Environment]::SetEnvironmentVariable('GTERM_MODULE_PORT',$priorPort,'Process')
    foreach ($name in @('Get-CimInstance','Get-NetUDPEndpoint','Get-NetTCPConnection','Start-Process')) { Remove-Item -LiteralPath ("Function:global:" + $name) -ErrorAction SilentlyContinue }
    foreach ($name in @('GTermTestProcesses','GTermTestUdp','GTermTestTcp','GTermTestStarts','GTermTestSeenPort','GTermTestProcessFilter')) { Remove-Variable -Name $name -Scope Global -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).Path
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^gterm-dedicated-test-[a-f0-9]{32}$') { throw 'Refusing test cleanup outside its generated temporary directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output "PASS: $checks dedicated-server launcher checks (process and sockets mocked)."
