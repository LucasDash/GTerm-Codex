[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Inspect', 'InstallOrUpdate')]
    [string]$Action,

    [Parameter(Mandatory)]
    [string]$SteamCmdPath,

    [Parameter(Mandatory)]
    [string]$ServerRoot,

    [ValidateSet('public', 'x86-64')]
    [string]$Branch = 'x86-64'
)

$ErrorActionPreference = 'Stop'
$appId = '4020'
$schema = 1
$markerName = '.gterm-codex-server-lab.json'
$steamExe = [IO.Path]::GetFullPath($SteamCmdPath)
$serverDir = [IO.Path]::GetFullPath($ServerRoot).TrimEnd('\')
$driveRoot = [IO.Path]::GetPathRoot($serverDir).TrimEnd('\')
if ($serverDir -eq $driveRoot) { throw 'The server root cannot be a drive root.' }
if ($serverDir -match '(?i)[\\/]steamapps[\\/]common[\\/]garrysmod(?:[\\/]|$)') {
    throw 'The server lab cannot be placed inside the Steam client game installation.'
}
if ($steamExe.StartsWith($serverDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SteamCMD and the dedicated server must use separate directories.'
}
if ([IO.Path]::GetFileName($steamExe) -ine 'steamcmd.exe' -or -not (Test-Path -LiteralPath $steamExe -PathType Leaf)) {
    throw 'Provide the path to an existing steamcmd.exe.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $steamExe
if ($signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Valve Corp.') {
    throw 'SteamCMD must have a valid Valve Corp. Authenticode signature.'
}

# Avoid writing through junctions or symbolic links to an unexpected location.
$ancestor = $serverDir
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Server path has a reparse point: $ancestor"
        }
    }
    $next = Split-Path -Path $ancestor -Parent
    if (-not $next -or $next -eq $ancestor) { break }
    $ancestor = $next
}

$markerPath = Join-Path $serverDir $markerName
$existing = Test-Path -LiteralPath $serverDir -PathType Container
$marker = $null
if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
    if (((Get-Item -LiteralPath $markerPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The server marker cannot be a reparse point.'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.schema -ne $schema -or $marker.appId -ne $appId -or
        $marker.branch -ne $Branch -or $marker.serverRoot -ine $serverDir) {
        throw 'Server marker does not match this path, app or branch.'
    }
}
$children = if ($existing) { @(Get-ChildItem -LiteralPath $serverDir -Force) } else { @() }
if (-not $marker -and $children.Count -gt 0) {
    throw 'Refusing a nonempty server directory without a matching lab marker.'
}

$summary = [ordered]@{
    appId = $appId
    branch = $Branch
    steamCmdPath = $steamExe
    serverRoot = $serverDir
    directoryExists = $existing
    marked = $null -ne $marker
    serverFilesPresent = Test-Path -LiteralPath (Join-Path $serverDir 'garrysmod') -PathType Container
}
if ($Action -eq 'Inspect') {
    [pscustomobject]$summary
    return
}

if (-not $PSCmdlet.ShouldProcess($serverDir, "Install or validate Garry's Mod dedicated server ($Branch)")) { return }
if (-not $existing) { New-Item -ItemType Directory -Path $serverDir | Out-Null }
if (-not $marker) {
    [ordered]@{schema = $schema; appId = $appId; branch = $Branch; serverRoot = $serverDir} |
        ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding utf8
}

# Anonymous authentication requires no credentials in arguments or logs.
$steamArgs = @('+force_install_dir', $serverDir, '+login', 'anonymous',
    '+app_update', $appId, '-beta', $Branch, 'validate', '+quit')
$lines = @()
Push-Location -LiteralPath (Split-Path -Path $steamExe -Parent)
try {
    & $steamExe @steamArgs 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $lines += $line
        Write-Host $line
    }
    $exitCode = $LASTEXITCODE
} finally { Pop-Location }
if ($exitCode -ne 0) { throw "SteamCMD exited with code $exitCode." }
if (-not ($lines -match "Success! App '4020' fully installed")) {
    throw 'SteamCMD did not report successful app installation; inspect its output before retrying.'
}
if (-not (Test-Path -LiteralPath (Join-Path $serverDir 'garrysmod') -PathType Container)) {
    throw 'SteamCMD reported success but the garrysmod directory is missing.'
}
Write-Host 'Dedicated-server installation validated by SteamCMD.'
