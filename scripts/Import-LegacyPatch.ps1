param(
    [Parameter(Mandatory)][string]$OriginalDirectory,
    [Parameter(Mandatory)][string]$PatchDirectory,
    [Parameter(Mandatory)][string]$OutputConfig,
    [string]$Version = '1.0.1',
    [string]$SteamBuildId = '26147432'
)
$ErrorActionPreference = 'Stop'
$originalRoot = (Resolve-Path -LiteralPath $OriginalDirectory).Path
$patchRoot = (Resolve-Path -LiteralPath $PatchDirectory).Path.TrimEnd('\')
$files = @(Get-ChildItem -LiteralPath $patchRoot -Recurse -File | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Patch files may not be links.' }
    $_.FullName.Substring($patchRoot.Length + 1).Replace('\', '/')
})
if ($files.Count -eq 0) { throw 'Chinese_Patch is empty.' }
$config = [ordered]@{
    gameId = 'a-sector-darkly-demo'
    gameTitle = 'A Sector Darkly Demo'
    version = $Version
    steamAppId = '5195950'
    executableNames = @('A Sector Darkly Demo.exe')
    variants = @([ordered]@{
        id = "steam-build$SteamBuildId"
        label = "Steam Demo - Build $SteamBuildId"
        storefront = 'steam'
        steamBuildIds = @($SteamBuildId)
        originalDirectory = $originalRoot
        translatedDirectory = $patchRoot
        files = $files
        fingerprintFiles = @()
    })
}
$outputPath = [IO.Path]::GetFullPath($OutputConfig)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
if (Test-Path -LiteralPath $outputPath) { throw 'Output config already exists.' }
[IO.File]::WriteAllText($outputPath, ($config | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote capture config for $($files.Count) files: $outputPath"
Write-Host 'Review the config, apply context-specific translation corrections, then run capture and pack.'
