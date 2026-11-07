[CmdletBinding()]
param([string]$DotNet = 'dotnet')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('gterm-config-test-' + [Guid]::NewGuid().ToString('N'))
$checks = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:checks++
    if (-not $Condition) { throw "FAIL: $Message" }
}
function Invoke-DotNet([string[]]$Arguments) {
    & $DotNet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $project = Join-Path $testRoot 'project'
    [IO.Directory]::CreateDirectory($project) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'GTerm.csproj') -Destination $project
    Get-ChildItem -LiteralPath $repo -Filter '*.cs' -File | Copy-Item -Destination $project
    foreach ($directory in @('Extensions', 'Listeners', 'MCP')) {
        Copy-Item -LiteralPath (Join-Path $repo $directory) -Destination $project -Recurse
    }
    # A synthetic template and synthetic consent avoid copying any live config or secrets.
    $template = Join-Path $project 'Config.json'
    [IO.File]::WriteAllText($template, '{"MCP":false,"template":1}')
    $projectFile = Join-Path $project 'GTerm.csproj'
    Invoke-DotNet @('build', $projectFile, '-c', 'Release', '--verbosity', 'quiet')
    $built = Join-Path $project 'bin/Release/net10.0/Config.json'
    Assert-True ((Test-Path -LiteralPath $built) -and ([IO.File]::ReadAllText($built) -eq [IO.File]::ReadAllText($template))) 'A new build receives the default config'

    $saved = '{"MCP":true,"MCPSecret":"fixture-only","ToolPackageConsent":[{"Scope":"fixture","Package":"agent_bridge","Hash":"fixture-hash"}]}'
    [IO.File]::WriteAllText($built, $saved)
    [IO.File]::WriteAllText($template, '{"MCP":false,"template":2}')
    [IO.File]::SetLastWriteTimeUtc($template, [DateTime]::UtcNow.AddSeconds(5))
    Invoke-DotNet @('build', $projectFile, '-c', 'Release', '--verbosity', 'quiet')
    Assert-True ([IO.File]::ReadAllText($built) -eq $saved) 'Rebuild preserves the existing runtime config and consent'

    # Exercise the first rebuild after upgrading from the previous copy-always
    # project as well; its intermediate file list may still name Config.json.
    $newProject = [IO.File]::ReadAllText($projectFile)
    $oldProject = $newProject.Replace('<CopyToOutputDirectory>Never</CopyToOutputDirectory>', '<CopyToOutputDirectory>Always</CopyToOutputDirectory>').Replace('<CopyToPublishDirectory>Never</CopyToPublishDirectory>', '<CopyToPublishDirectory>Always</CopyToPublishDirectory>')
    Assert-True ($oldProject -ne $newProject) 'Legacy copy behavior fixture is configured'
    [IO.File]::WriteAllText($projectFile, $oldProject)
    Invoke-DotNet @('build', $projectFile, '-c', 'Release', '--verbosity', 'quiet')
    [IO.File]::WriteAllText($built, $saved)
    [IO.File]::WriteAllText($template, '{"MCP":false,"template":3}')
    [IO.File]::WriteAllText($projectFile, $newProject)
    Invoke-DotNet @('build', $projectFile, '-c', 'Release', '--verbosity', 'quiet')
    Assert-True ([IO.File]::ReadAllText($built) -eq $saved) 'First build after upgrading preserves an existing config'
    Invoke-DotNet @('clean', $projectFile, '-c', 'Release', '--verbosity', 'quiet')
    Assert-True ((Test-Path -LiteralPath $built) -and [IO.File]::ReadAllText($built) -eq $saved) 'Clean preserves the runtime config'

    $publish = Join-Path $testRoot 'publish'
    Invoke-DotNet @('publish', $projectFile, '-c', 'Release', '-o', $publish, '--verbosity', 'quiet')
    $published = Join-Path $publish 'Config.json'
    Assert-True ((Test-Path -LiteralPath $published) -and ([IO.File]::ReadAllText($published) -eq [IO.File]::ReadAllText($template))) 'A new publish receives the default config'
    [IO.File]::WriteAllText($published, $saved)
    [IO.File]::WriteAllText($template, '{"MCP":false,"template":4}')
    [IO.File]::SetLastWriteTimeUtc($template, [DateTime]::UtcNow.AddSeconds(10))
    Invoke-DotNet @('publish', $projectFile, '-c', 'Release', '-o', $publish, '--verbosity', 'quiet')
    Assert-True ([IO.File]::ReadAllText($published) -eq $saved) 'Republish preserves the existing runtime config and consent'
    Assert-True ([IO.File]::ReadAllText($built) -eq $saved) 'Publishing also preserves the build output config'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).Path
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^gterm-config-test-[a-f0-9]{32}$') { throw 'Refusing cleanup outside the generated test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output "PASS: $checks config preservation checks (isolated project and synthetic config)."
