[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-WorkshopId([System.Text.Json.JsonElement]$Value, [string]$Field) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        throw "$Field must contain decimal IDs as JSON strings."
    }
    $id = $Value.GetString()
    [UInt64]$number = 0
    if ($id -cnotmatch '^[1-9][0-9]{0,19}$' -or -not [UInt64]::TryParse($id, [ref]$number)) {
        throw "$Field contains an invalid Workshop ID."
    }
    return $id
}

function Read-WorkshopIds([System.Text.Json.JsonElement]$Value, [string]$Field) {
    if ($Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "$Field must be an array."
    }
    if ($Value.GetArrayLength() -gt 1024) { throw "$Field is limited to 1024 IDs." }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Value.EnumerateArray()) {
        $id = Read-WorkshopId $item $Field
        if (-not $seen.Add($id)) { throw "$Field contains duplicate Workshop ID $id." }
        $id
    }
}

function Write-NewUtf8File([string]$Path, [string]$Content) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $writer.Write($Content) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'OutputDirectory must be an absolute path.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory already exists; choose a new bundle directory.' }
$parent = [IO.Path]::GetDirectoryName($output)
if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The output parent directory must already exist.' }
$inputFile = Get-Item -LiteralPath $ManifestPath
if ($inputFile.PSIsContainer -or $inputFile.Length -gt 131072) { throw 'Manifest must be a JSON file of at most 128 KiB.' }
$inputText = [IO.File]::ReadAllText($inputFile.FullName, [Text.UTF8Encoding]::new($false, $true))
$document = [System.Text.Json.JsonDocument]::Parse($inputText)
try {
    $root = $document.RootElement
    if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Manifest root must be an object.' }
    $fields = [Collections.Generic.Dictionary[string, System.Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $root.EnumerateObject()) {
        if ($property.Name -cnotin @('schema', 'server_collection', 'server_items', 'client_content')) { throw "Unknown manifest property: $($property.Name)" }
        if ($fields.ContainsKey($property.Name)) { throw "Duplicate manifest property: $($property.Name)" }
        $fields.Add($property.Name, $property.Value)
    }
    foreach ($required in @('schema', 'server_items', 'client_content')) {
        if (-not $fields.ContainsKey($required)) { throw "Missing manifest property: $required" }
    }
    [int]$schema = 0
    if ($fields['schema'].ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $fields['schema'].TryGetInt32([ref]$schema) -or $schema -ne 1) { throw 'Manifest schema must be 1.' }
    $collection = ''
    if ($fields.ContainsKey('server_collection') -and $fields['server_collection'].ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        $collection = Read-WorkshopId $fields['server_collection'] 'server_collection'
    }
    [string[]]$serverItems = @(Read-WorkshopIds $fields['server_items'] 'server_items')
    [string[]]$clientContent = @(Read-WorkshopIds $fields['client_content'] 'client_content')
} finally { $document.Dispose() }

# Build a new review bundle only after the complete manifest validates. Existing
# server installations and previously generated bundles are never overwritten.
[IO.Directory]::CreateDirectory($output) | Out-Null
$serverRelative = $null
if ($serverItems.Count -gt 0) {
    $serverRelative = 'garrysmod/cfg/srcds_workshop_ids.txt'
    $serverPath = Join-Path $output $serverRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($serverPath)) | Out-Null
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('"my_workshop_addons"')
    $lines.Add('{')
    for ($i = 0; $i -lt $serverItems.Count; $i++) {
        $lines.Add(('    "{0}" {{ "wsid" "{1}" }}' -f ($i + 1), $serverItems[$i]))
    }
    $lines.Add('}')
    Write-NewUtf8File $serverPath (($lines -join "`n") + "`n")
}
$clientRelative = $null
if ($clientContent.Count -gt 0) {
    $clientRelative = 'garrysmod/lua/autorun/server/gterm_workshop_content.lua'
    $clientPath = Join-Path $output $clientRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($clientPath)) | Out-Null
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('-- Generated from explicitly declared client content IDs.')
    foreach ($id in $clientContent) { $lines.Add(('resource.AddWorkshop("{0}")' -f $id)) }
    Write-NewUtf8File $clientPath (($lines -join "`n") + "`n")
}
$plan = [ordered]@{
    schema = 1
    server_collection = if ($collection) { $collection } else { $null }
    server_items = $serverItems
    client_content = $clientContent
    server_config = $serverRelative
    client_autorun = $clientRelative
}
Write-NewUtf8File (Join-Path $output 'workshop-plan.json') (($plan | ConvertTo-Json -Depth 4) + "`n")
[pscustomobject]@{
    OutputDirectory = $output
    WorkshopCollection = $collection
    ServerItemCount = $serverItems.Count
    ClientContentCount = $clientContent.Count
    ServerConfig = $serverRelative
    ClientAutorun = $clientRelative
}
