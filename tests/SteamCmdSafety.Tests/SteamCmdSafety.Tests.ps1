[CmdletBinding()]
param([Parameter(Mandatory)][string]$ModulePath,[string]$UpdaterPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Import-Module -Name $ModulePath -Force -PassThru
$passed=0
function Check([bool]$Condition,[string]$Message) {
    if(-not $Condition){throw "FAILED: $Message"}
    $script:passed++
}
function ExpectFailure([scriptblock]$Action,[string]$Fragment,[string]$Message) {
    $failure=$null
    try { & $Action } catch { $failure=$_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Fragment)) { throw "FAILED: $Message; unexpected error: $failure" }
    Check $true $Message
}
& $module { Assert-SafeProcessRecords -Records @() }
Check $true 'Empty process enumeration is safe'
& $module { Assert-SafeProcessRecords -Records @([pscustomobject]@{Name='gmod.exe';CommandLine='gmod.exe -game garrysmod'}) }
Check $true 'Ordinary client is accepted'
ExpectFailure { & $module { Assert-SafeProcessRecords -Records @([pscustomobject]@{Name='srcds_console_win64.exe';CommandLine='anything'}) } } 'dedicated-server process is running' 'Source server blocks update'
ExpectFailure { & $module { Assert-SafeProcessRecords -Records @([pscustomobject]@{Name='gmod.exe';CommandLine='gmod.exe "-dedicated" -game garrysmod'}) } } 'dedicated-server process is running' 'Quoted GMod dedicated flag blocks update'
ExpectFailure { & $module { Assert-SafeProcessRecords -Records @([pscustomobject]@{Name='gmod.exe';CommandLine=$null}) } } 'Cannot classify' 'Unknown GMod command line fails closed'
& $module { function script:Get-CimInstance { throw 'synthetic WMI failure' } }
try { ExpectFailure { & $module { Assert-NoDedicatedProcesses } } 'Cannot inspect dedicated-server processes' 'WMI failure fails closed' }
finally { & $module { Remove-Item Function:\script:Get-CimInstance } }

$base=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture=Join-Path $base ('gterm-update-guard-'+[guid]::NewGuid().ToString('N'))
$root=Join-Path $fixture 'server'
$other=Join-Path $fixture 'other'
$junction=Join-Path $root 'junction'
try {
    New-Item -ItemType Directory -Path $root,$other | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'file.vpk'),'fixture')
    & $module { param($p) Assert-NoLinkedFiles -ServerRoot $p } $root
    Check $true 'Independent file is accepted'
    New-Item -ItemType HardLink -Path (Join-Path $other 'linked.vpk') -Target (Join-Path $root 'file.vpk') | Out-Null
    ExpectFailure { & $module { param($p) Assert-NoLinkedFiles -ServerRoot $p } $root } 'refusing update' 'Real hardlink is rejected'
    Remove-Item -LiteralPath (Join-Path $other 'linked.vpk')
    & $module { param($p) Assert-NoLinkedFiles -ServerRoot $p } $root
    Check $true 'Removing the test link restores independent status'
    New-Item -ItemType Junction -Path $junction -Target $other | Out-Null
    ExpectFailure { & $module { param($p) Assert-NoLinkedFiles -ServerRoot $p } $root } 'refusing update' 'Real junction is rejected'
    ExpectFailure { & $module { param($p) Assert-NoLinkedFiles -ServerRoot $p } $junction } 'refusing update' 'Junction as root is rejected'
    Remove-Item -LiteralPath $junction
    & $module { function script:Initialize-FileLinkInspector { throw 'synthetic link-inspection failure' } }
    try { ExpectFailure { & $module { param($p) Assert-NoLinkedFiles -ServerRoot $p } $root } 'refusing update' 'Link-inspection failure fails closed' }
    finally { & $module { Remove-Item Function:\script:Initialize-FileLinkInspector } }
    if ($UpdaterPath) {
        $fakeSteam=Join-Path $other 'steamcmd.exe'
        $newRoot=Join-Path $fixture 'new-server'
        [IO.File]::WriteAllText($fakeSteam,'this test file cannot execute')
        $certificate=[pscustomobject]@{}
        $certificate | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($kind,$issuer) 'Valve Corp.' }
        function global:Get-AuthenticodeSignature { [pscustomobject]@{Status='Valid';SignerCertificate=$certificate} }
        function global:Get-CimInstance { [pscustomobject]@{Name='srcds.exe';CommandLine='fake'} }
        try {
            $fixtureParams=@{SteamCmdPath=$fakeSteam;ServerRoot=$newRoot;Branch='x86-64'}
            $inspected=& $UpdaterPath -Action Inspect @fixtureParams
            Check (-not $inspected.directoryExists) 'Inspect is read-only'
            & $UpdaterPath -Action InstallOrUpdate @fixtureParams -WhatIf
            Check (-not (Test-Path -LiteralPath $newRoot)) 'WhatIf creates no directory'
            ExpectFailure { & $UpdaterPath -Action InstallOrUpdate @fixtureParams } 'dedicated-server process is running' 'Updater invokes process guard before changes'
            Check (-not (Test-Path -LiteralPath $newRoot)) 'Blocked update creates no marker or directory'
        } finally {
            Remove-Item Function:\global:Get-AuthenticodeSignature -ErrorAction SilentlyContinue
            Remove-Item Function:\global:Get-CimInstance -ErrorAction SilentlyContinue
        }
    }
} finally {
    if(Test-Path -LiteralPath $junction){Remove-Item -LiteralPath $junction -Force}
    if(Test-Path -LiteralPath $fixture){
        $resolved=(Resolve-Path -LiteralPath $fixture).Path
        $prefix=Join-Path $base 'gterm-update-guard-'
        if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $resolved -ne $fixture){throw 'Unexpected fixture cleanup path.'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output "PASS: $passed focused updater safety checks"
