param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$workRoot = Join-Path $repoRoot ('artifacts/workflow-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$original = Join-Path $workRoot 'original with spaces'
$translated = Join-Path $workRoot 'translated with spaces'
[IO.Directory]::CreateDirectory($original) | Out-Null
[IO.Directory]::CreateDirectory($translated) | Out-Null
[IO.File]::WriteAllText((Join-Path $original 'text.dat'), 'English')
[IO.File]::WriteAllText((Join-Path $translated 'text.dat'), 'Chinese sample')
$config = @{
    gameId = 'workflow-example'; gameTitle = 'Workflow Example'; version = '1.0.0'
    steamAppId = $null; executableNames = @()
    variants = @(@{ id = 'standalone'; label = 'Standalone'; storefront = 'standalone'; steamBuildIds = @(); originalDirectory = $original; translatedDirectory = $translated; files = @('text.dat'); fingerprintFiles = @() })
}
$configPath = Join-Path $workRoot 'release.json'
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 12))
$catalogPath = Join-Path $workRoot 'catalog.json'
[IO.File]::WriteAllText($catalogPath, '{"schemaVersion":1,"repository":"tkxs/LocaleStack","games":[{"id":"workflow-example","title":"Workflow Example","status":"planned","patch":null}]}')
$cli = Join-Path $repoRoot 'src/LocaleStack.Cli'
function Invoke-Cli { & $DotNet run --project $cli -c Release -- @args; if ($LASTEXITCODE -ne 0) { throw "CLI failed: $args" } }
$packageRoot = Join-Path $workRoot 'package'
$package = Join-Path $workRoot 'example.lspatch'
Invoke-Cli capture --config $configPath --output $packageRoot
Invoke-Cli pack --source $packageRoot --output $package
Invoke-Cli verify --package $package
Invoke-Cli inspect --package $package --game-directory $original
Invoke-Cli catalog-add --catalog $catalogPath --game-id workflow-example --package $package --url 'https://github.com/tkxs/LocaleStack/releases/download/patch-workflow-example-v1.0.0/example.lspatch' --notes 'Test release'
Invoke-Cli catalog-validate --catalog $catalogPath
$catalog = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json
if ($catalog.games[0].status -ne 'available') { throw 'Catalog state did not update.' }
if ($catalog.games[0].patch.sha256 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLower()) { throw 'Published checksum mismatch.' }
if ([IO.File]::ReadAllText((Join-Path $original 'text.dat')) -ne 'English') { throw 'Capture or inspect changed input files.' }
Write-Host "PASS full maintainer workflow: $workRoot"
