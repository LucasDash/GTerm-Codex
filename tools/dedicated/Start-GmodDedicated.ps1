[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Inspect','Start')][string]$Action = 'Inspect',
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

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GmodDedicated.psm1') -Force
$plan = New-GmodDedicatedPlan -ServerRoot $ServerRoot -BindAddress $BindAddress -GamePort $GamePort `
    -ClientPort $ClientPort -TvPort $TvPort -ModulePort $ModulePort -MaxPlayers $MaxPlayers `
    -Map $Map -GameMode $GameMode -WorkshopCollection $WorkshopCollection

if ($Action -eq 'Inspect') {
    $inventory = Get-GmodDedicatedInventory -Plan $plan
    [pscustomobject]@{
        ServerRoot=$plan.ServerRoot; ExecutablePresent=(Test-Path -LiteralPath $plan.Executable -PathType Leaf)
        MarkerPresent=(Test-Path -LiteralPath (Join-Path $plan.ServerRoot '.gterm-codex-server-lab.json') -PathType Leaf)
        BindAddress=$plan.BindAddress; GamePort=$plan.GamePort; ClientPort=$plan.ClientPort
        TvPort=$plan.TvPort; ModulePort=$plan.ModulePort; WorkshopCollection=$plan.WorkshopCollection
        ServerProcessIds=@($inventory.ServerProcesses | ForEach-Object { $_.ProcessId })
        UdpConflicts=@($inventory.UdpConflicts); ModulePortConflicts=@($inventory.ModulePortConflicts)
        Arguments=$plan.Arguments
    }
    return
}

# Serializes competing invocations of this launcher in the same Windows login
# session. Other programs can still race the port checks; startup is not proof
# that the server subsequently bound its sockets or became ready.
$mutex = [Threading.Mutex]::new($false, 'Local\GTermCodex-Dedicated-Launcher-v1')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another GTerm dedicated launcher is currently starting a server.' }
    Assert-GmodDedicatedInstallation -Plan $plan
    if ($plan.BindAddress -ne '127.0.0.1' -and -not (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $plan.BindAddress -ErrorAction SilentlyContinue)) {
        throw 'BindAddress is not assigned to a local network interface.'
    }
    $inventory = Get-GmodDedicatedInventory -Plan $plan
    Assert-GmodDedicatedAvailable -Inventory $inventory
    if ($plan.WorkshopCollection) {
        $addons = Join-Path $plan.ServerRoot 'garrysmod\addons'
        if ((Test-Path -LiteralPath $addons -PathType Container) -and @(Get-ChildItem -LiteralPath $addons -Force).Count) {
            Write-Warning 'A Workshop collection and local addons may contain duplicates. Verify addon identities and mounts before testing.'
        }
    }
    if (-not $PSCmdlet.ShouldProcess($plan.ServerRoot, "Start dedicated server on $($plan.BindAddress):$($plan.GamePort)")) { return }
    $previousPort = [Environment]::GetEnvironmentVariable('GTERM_MODULE_PORT','Process')
    try {
        [Environment]::SetEnvironmentVariable('GTERM_MODULE_PORT',[string]$plan.ModulePort,'Process')
        $process = Start-Process -FilePath $plan.Executable -WorkingDirectory $plan.ServerRoot `
            -ArgumentList $plan.Arguments -WindowStyle Hidden -PassThru
    } finally {
        [Environment]::SetEnvironmentVariable('GTERM_MODULE_PORT',$previousPort,'Process')
    }
    [pscustomobject]@{
        Requested=$true; ProcessId=$process.Id; ServerRoot=$plan.ServerRoot
        BindAddress=$plan.BindAddress; GamePort=$plan.GamePort; ModulePort=$plan.ModulePort
        WorkshopCollection=$plan.WorkshopCollection; Ready=$false
    }
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
