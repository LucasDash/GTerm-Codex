Set-StrictMode -Version Latest

function New-GmodDedicatedPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ServerRoot,
        [string]$BindAddress = '127.0.0.1',
        [ValidateRange(1,65535)][int]$GamePort = 28115,
        [ValidateRange(1,65535)][int]$ClientPort = 28105,
        [ValidateRange(1,65535)][int]$TvPort = 28120,
        [ValidateRange(1,65535)][int]$ModulePort = 27524,
        [ValidateRange(1,128)][int]$MaxPlayers = 2,
        [string]$Map = 'gm_construct',
        [string]$GameMode = 'sandbox',
        [string]$WorkshopCollection = ''
    )
    if (-not [IO.Path]::IsPathRooted($ServerRoot)) { throw 'ServerRoot must be an absolute path.' }
    $root = [IO.Path]::GetFullPath($ServerRoot).TrimEnd('\')
    if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'ServerRoot cannot be a drive root.' }
    if ($root -match '(?i)[\\/]steamapps[\\/]common[\\/]garrysmod(?:[\\/]|$)') { throw 'Use an isolated dedicated-server root, not the Steam client installation.' }
    if ($Map -notmatch '^[A-Za-z0-9_/-]{1,128}$' -or $Map.StartsWith('/') -or $Map.EndsWith('/')) { throw 'Map must be a plain map name without an extension.' }
    if ($GameMode -notmatch '^[A-Za-z0-9_-]{1,64}$') { throw 'GameMode must be a plain gamemode folder name.' }
    if ($WorkshopCollection -and ($WorkshopCollection -notmatch '^[0-9]{1,20}$' -or $WorkshopCollection -match '^0+$')) { throw 'WorkshopCollection must be a nonzero decimal collection ID or empty.' }
    $address = $null
    if (-not [Net.IPAddress]::TryParse($BindAddress, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'BindAddress must be an IPv4 address.' }
    $octets = $address.GetAddressBytes()
    $private = [Net.IPAddress]::IsLoopback($address) -or $octets[0] -eq 10 -or ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or ($octets[0] -eq 192 -and $octets[1] -eq 168)
    if (-not $private) { throw 'BindAddress must be loopback or private LAN IPv4.' }
    $ports = @($GamePort, $ClientPort, $TvPort, $ModulePort)
    if (@($ports | Select-Object -Unique).Count -ne $ports.Count) { throw 'Game, client, TV and GTerm module ports must be distinct.' }
    # All arguments are validated and contain no whitespace or quoting
    # characters; Start-Process on Windows joins ArgumentList into one string.
    $arguments = @('-console','-game','garrysmod','-ip',$address.ToString(),'-port',"$GamePort",'+clientport',"$ClientPort",'+tv_port',"$TvPort",'+maxplayers',"$MaxPlayers",'+sv_lan','1','+sv_hibernate_think','1','+gamemode',$GameMode)
    if ($WorkshopCollection) { $arguments += @('+host_workshop_collection',$WorkshopCollection) }
    $arguments += @('+map',$Map,'-norestart','-condebug')
    [pscustomobject]@{
        ServerRoot=$root; Executable=(Join-Path $root 'srcds_console_win64.exe'); BindAddress=$address.ToString()
        GamePort=$GamePort; ClientPort=$ClientPort; TvPort=$TvPort; ModulePort=$ModulePort
        WorkshopCollection=$WorkshopCollection; Arguments=[string[]]$arguments
    }
}

function Get-GmodDedicatedInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    # An inaccessible server process could be this root; refuse to infer safety.
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name LIKE 'srcds%.exe' OR Name='gmod.exe'" -ErrorAction Stop)
    $unknown = @($processes | Where-Object { -not $_.ExecutablePath })
    if ($unknown.Count) { throw 'Cannot inspect every GMod or srcds executable path; cannot safely determine whether this root is active.' }
    $prefix = $Plan.ServerRoot + '\'
    $matches = @($processes | Where-Object { $_.ExecutablePath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) })
    $udp = @(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object { $_.LocalPort -in @($Plan.GamePort,$Plan.ClientPort,$Plan.TvPort) } | Select-Object LocalAddress,LocalPort,OwningProcess)
    $tcp = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -eq $Plan.ModulePort } | Select-Object LocalAddress,LocalPort,OwningProcess)
    [pscustomobject]@{ServerProcesses=$matches; UdpConflicts=$udp; ModulePortConflicts=$tcp}
}

function Assert-GmodDedicatedAvailable {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Inventory)
    if (@($Inventory.ServerProcesses).Count) { throw 'A dedicated server from this root is already running; no restart or second process was started.' }
    if (@($Inventory.UdpConflicts).Count) { throw 'A requested UDP game/client/TV port is already in use.' }
    if (@($Inventory.ModulePortConflicts).Count) { throw 'The requested GTerm module TCP port is already in use.' }
}

function Assert-GmodDedicatedInstallation {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    if (-not (Test-Path -LiteralPath $Plan.Executable -PathType Leaf)) { throw 'The x64 dedicated-server executable is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $Plan.ServerRoot 'garrysmod') -PathType Container)) { throw 'The garrysmod server directory is missing.' }
    $markerPath = Join-Path $Plan.ServerRoot '.gterm-codex-server-lab.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'The isolated SteamCMD server marker is missing.' }
    foreach ($path in @($Plan.ServerRoot,(Join-Path $Plan.ServerRoot 'garrysmod'),$Plan.Executable,$markerPath)) {
        if (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The server root, game directory, executable and marker cannot be reparse points.' }
    }
    $ancestor = $Plan.ServerRoot
    while ($ancestor) {
        if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The server root cannot pass through a reparse point.' }
        $next = Split-Path -Path $ancestor -Parent
        if (-not $next -or $next -eq $ancestor) { break }
        $ancestor = $next
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.schema -ne 1 -or $marker.appId -ne '4020' -or $marker.branch -ne 'x86-64' -or $marker.serverRoot -ine $Plan.ServerRoot) { throw 'The server marker does not match this x64 installation.' }
}

Export-ModuleMember -Function New-GmodDedicatedPlan,Get-GmodDedicatedInventory,Assert-GmodDedicatedAvailable,Assert-GmodDedicatedInstallation
