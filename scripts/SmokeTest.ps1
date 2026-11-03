param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$OutputDirectory = 'artifacts/smoke'
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $AppPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dataDirectory = Join-Path $output ([Guid]::NewGuid().ToString('N'))
$report = Join-Path $dataDirectory 'startup.json'
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$arguments = @('--smoke-test', '--data-dir', ('"' + $dataDirectory + '"'), '--report', ('"' + $report + '"'))
$appProcess = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
if (-not $appProcess.WaitForExit(30000)) {
    Stop-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
    throw 'GameBooster did not complete its startup smoke test within 30 seconds.'
}
if ($appProcess.ExitCode -ne 0) { throw "GameBooster exited with code $($appProcess.ExitCode). Report: $report" }
if (-not (Test-Path -LiteralPath $report)) { throw "GameBooster did not write a startup report: $report" }
$result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
if (-not $result.success -or -not $result.windowLoaded) { throw "Startup failed: $($result.error)" }
Write-Output "Startup smoke test passed. Report: $report"
