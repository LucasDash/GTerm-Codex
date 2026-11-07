$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$generator = Join-Path $PSScriptRoot 'New-GmodWorkshopBundle.ps1'
$checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:checks++
    if (-not $Condition) { throw "FAIL: $Message" }
}
function Assert-Rejected([string]$Json, [string]$Message) {
    $inputPath = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.json')
    $outputPath = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($inputPath, $Json)
    $caught = $false
    try { & $generator -ManifestPath $inputPath -OutputDirectory $outputPath | Out-Null } catch { $caught = $true }
    Assert-True ($caught -and -not (Test-Path -LiteralPath $outputPath)) $Message
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('gterm-workshop-test-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $inputPath = Join-Path $testRoot 'manifest.json'
    $outputPath = Join-Path $testRoot 'bundle'
    [IO.File]::WriteAllText($inputPath, '{"schema":1,"server_collection":"9001","server_items":["1001","1002"],"client_content":["1002","2001"]}')
    $result = & $generator -ManifestPath $inputPath -OutputDirectory $outputPath
    $serverPath = Join-Path $outputPath 'garrysmod/cfg/srcds_workshop_ids.txt'
    $clientPath = Join-Path $outputPath 'garrysmod/lua/autorun/server/gterm_workshop_content.lua'
    $server = [IO.File]::ReadAllText($serverPath)
    $client = [IO.File]::ReadAllText($clientPath)
    $plan = Get-Content -LiteralPath (Join-Path $outputPath 'workshop-plan.json') -Raw | ConvertFrom-Json
    Assert-True ($result.WorkshopCollection -eq '9001' -and $plan.server_collection -eq '9001') 'Collection is reported for the launcher'
    Assert-True ($server.Contains('"wsid" "1001"') -and $server.Contains('"wsid" "1002"') -and -not $server.Contains('2001') -and -not $server.Contains('9001')) 'Only explicit server items enter the native configuration'
    Assert-True ($client.Contains('resource.AddWorkshop("1002")') -and $client.Contains('resource.AddWorkshop("2001")') -and -not $client.Contains('1001') -and -not $client.Contains('9001')) 'Only explicit client content enters the Lua declaration'
    Assert-True (@($plan.server_items).Count -eq 2 -and @($plan.client_content).Count -eq 2) 'Plan preserves the two ID lists'
    $before = [IO.File]::ReadAllText($serverPath)
    $caught = $false
    try { & $generator -ManifestPath $inputPath -OutputDirectory $outputPath | Out-Null } catch { $caught = $true }
    Assert-True ($caught -and [IO.File]::ReadAllText($serverPath) -eq $before) 'Existing output is not overwritten'
    $emptyPath = Join-Path $testRoot 'empty.json'
    $emptyOutput = Join-Path $testRoot 'empty-bundle'
    [IO.File]::WriteAllText($emptyPath, '{"schema":1,"server_collection":null,"server_items":[],"client_content":[]}')
    & $generator -ManifestPath $emptyPath -OutputDirectory $emptyOutput | Out-Null
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $emptyOutput 'garrysmod'))) 'Empty lists generate no server or client files'
    Assert-Rejected '{"schema":1,"server_items":[1001],"client_content":[]}' 'Numeric JSON IDs are rejected to preserve precision'
    Assert-Rejected '{"schema":1,"server_items":["0"],"client_content":[]}' 'Zero ID is rejected'
    Assert-Rejected '{"schema":1,"server_items":["18446744073709551616"],"client_content":[]}' 'ID outside UInt64 is rejected'
    Assert-Rejected '{"schema":1,"server_items":["1001","1001"],"client_content":[]}' 'Duplicate IDs in a list are rejected'
    Assert-Rejected '{"schema":1,"server_items":[],"client_content":["1\");RunConsoleCommand(\"quit\")--"]}' 'Lua injection in IDs is rejected'
    Assert-Rejected '{"schema":1,"server_collection":"1 +quit","server_items":[],"client_content":[]}' 'Collection command injection is rejected'
    Assert-Rejected '{"schema":1,"server_items":[],"client_content":[],"extra":true}' 'Unknown properties are rejected'
    Assert-Rejected '{"schema":1,"schema":1,"server_items":[],"client_content":[]}' 'Duplicate properties are rejected'
    Assert-Rejected '{"schema":1,"server_items":[]}' 'Missing list is rejected'
    Assert-Rejected '{"schema":2,"server_items":[],"client_content":[]}' 'Unknown schema is rejected'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).Path
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^gterm-workshop-test-[a-f0-9]{32}$') { throw 'Refusing cleanup outside the generated test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output "PASS: $checks Workshop bundle checks (no Workshop download or server changes)."
