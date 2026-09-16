param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Commit = 'local',
    [string]$RunId = 'local'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $projectRoot 'Jellyfin.Plugin.MetaTube/bin'
$hashes = @{}
foreach ($platform in @('Jellyfin', 'Emby')) {
    $configuration = if ($platform -eq 'Jellyfin') { 'Release' } else { 'Release.Emby' }
    $framework = if ($platform -eq 'Jellyfin') { 'net10.0' } else { 'net8.0' }
    $assembly = Join-Path $bin "$configuration/$framework/MetaTube.dll"
    if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -ne $Version) {
        throw "$platform assembly version differs from release version"
    }
    $archive = Join-Path $bin "$platform.MetaTube@v$Version.zip"
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -ne 'MetaTube.dll') {
            throw "$platform archive must contain only root MetaTube.dll"
        }
        $stream = $zip.Entries[0].Open()
        try { $zippedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($zippedHash -ne (Get-FileHash $assembly -Algorithm SHA256).Hash) {
            throw "$platform archive differs from built assembly"
        }
    }
    finally { $zip.Dispose() }
    $hashes[$platform] = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{
    version = $Version
    commit = $Commit
    runId = $RunId
    sdk = (dotnet --version)
    targets = @{ Jellyfin = '12.0.0/net10.0'; Emby = '4.9.1.80/net8.0' }
    sha256 = $hashes
} | ConvertTo-Json | Set-Content (Join-Path $bin 'build-evidence.json') -Encoding utf8NoBOM
