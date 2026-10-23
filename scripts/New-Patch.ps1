param(
    [Parameter(Mandatory)][string]$Config,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Package,
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$cliProject = Join-Path $repoRoot 'src/LocaleStack.Cli'
function Invoke-Cli { & $DotNet run --project $cliProject -c Release -- @args; if ($LASTEXITCODE -ne 0) { throw 'Maintainer command failed.' } }
Invoke-Cli capture --config $Config --output $OutputDirectory
Invoke-Cli pack --source $OutputDirectory --output $Package
Invoke-Cli verify --package $Package
