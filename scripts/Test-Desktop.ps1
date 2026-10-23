param([string]$Executable = 'artifacts/app/LocaleStack.exe', [string]$OutputDirectory = 'artifacts/screenshots', [string]$MaintainerExecutable = '')
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Executable).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
foreach ($page in @('library','tasks','help','settings','compact','maintainer','maintainer-compact','maintainer-workflow')) {
    $imagePath = Join-Path $outputPath "$page.png"
    $processArgs = @('--offline','--screenshot',"`"$imagePath`"",'--page',$page)
    if ($page -eq 'compact') { $processArgs += '--compact' }
    if ($page.StartsWith('maintainer')) { $processArgs += '--maintainer' }
    if ($page -eq 'maintainer-compact') { $processArgs += '--compact' }
    if ($page -eq 'maintainer-workflow') { $processArgs += '--smoke-maintainer' }
    $preview = Start-Process -FilePath $exePath -ArgumentList $processArgs -WindowStyle Hidden -PassThru
    if (-not $preview.WaitForExit(30000)) { throw "Desktop preview timed out: $page (PID $($preview.Id))" }
    if ($preview.ExitCode -ne 0) { throw "Desktop preview failed: $page. See LocalAppData/LocaleStack/errors.log." }
    if (-not (Test-Path -LiteralPath $imagePath)) { throw "Screenshot missing: $page" }
    Write-Host "PASS desktop startup and render: $page"
}
if ($MaintainerExecutable) {
    $maintainerPath = (Resolve-Path -LiteralPath $MaintainerExecutable).Path
    $imagePath = Join-Path $outputPath 'maintainer-standalone.png'
    $preview = Start-Process -FilePath $maintainerPath -ArgumentList @('--offline','--screenshot',"`"$imagePath`"",'--smoke-maintainer') -WindowStyle Hidden -PassThru
    if (-not $preview.WaitForExit(30000) -or $preview.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $imagePath)) { throw 'Standalone maintainer GUI smoke test failed.' }
    Write-Host 'PASS standalone maintainer entry and graphical workflow'
}
