param(
    [string]$Version = '0.2.0',
    [string]$DotNet = 'dotnet',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
    function Invoke-DotNet { & $DotNet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" } }
    Invoke-DotNet build LocaleStack.sln -c Release "-p:Version=$Version"
    if (-not $SkipTests) { Invoke-DotNet run --project tests/LocaleStack.Tests -c Release --no-build }
    Invoke-DotNet run --project src/LocaleStack.Cli -c Release --no-build -- catalog-validate --catalog catalog/catalog.json
    Invoke-DotNet publish src/LocaleStack.App -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' "-p:Version=$Version" -o artifacts/app
    Invoke-DotNet publish src/LocaleStack.Cli -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' "-p:Version=$Version" -o artifacts/cli
    Invoke-DotNet publish src/LocaleStack.App -c Release -r win-x64 --self-contained true '-p:MaintainerEdition=true' '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' "-p:Version=$Version" -o artifacts/maintainer
    Copy-Item -LiteralPath artifacts/cli/LocaleStack.Cli.exe -Destination artifacts/maintainer/LocaleStack.Cli.exe -Force
    Compress-Archive -Path artifacts/app/* -DestinationPath artifacts/LocaleStack-Portable-win-x64.zip -Force
    Compress-Archive -Path artifacts/maintainer/* -DestinationPath artifacts/LocaleStack-Maintainer-win-x64.zip -Force
    Write-Host 'Portable app and maintainer CLI are ready in artifacts/.'
}
finally { Pop-Location }
