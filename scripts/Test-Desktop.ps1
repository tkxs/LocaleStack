param([string]$Executable = 'artifacts/app/LocaleStack.exe', [string]$OutputDirectory = 'artifacts/screenshots')
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Executable).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
foreach ($page in @('library','tasks','help','settings','compact')) {
    $imagePath = Join-Path $outputPath "$page.png"
    $processArgs = @('--offline','--screenshot',"`"$imagePath`"",'--page',$page)
    if ($page -eq 'compact') { $processArgs += '--compact' }
    $preview = Start-Process -FilePath $exePath -ArgumentList $processArgs -WindowStyle Hidden -PassThru
    if (-not $preview.WaitForExit(30000)) { throw "Desktop preview timed out: $page (PID $($preview.Id))" }
    if ($preview.ExitCode -ne 0) { throw "Desktop preview failed: $page. See LocalAppData/LocaleStack/errors.log." }
    if (-not (Test-Path -LiteralPath $imagePath)) { throw "Screenshot missing: $page" }
    Write-Host "PASS desktop startup and render: $page"
}
