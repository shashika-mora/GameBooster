param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$OutputDirectory = 'artifacts/smoke',
    [ValidateSet('System', 'Light', 'Dark')][string]$Theme = 'System'
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $AppPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dataDirectory = Join-Path $output ([Guid]::NewGuid().ToString('N'))
$report = Join-Path $dataDirectory 'startup.json'
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$arguments = @('--smoke-test', '--data-dir', ('"' + $dataDirectory + '"'), '--report', ('"' + $report + '"'), '--theme', $Theme)
$appProcess = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
if (-not $appProcess.WaitForExit(30000)) {
    Stop-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
    throw 'GameBooster did not complete its startup smoke test within 30 seconds.'
}
if ($appProcess.ExitCode -ne 0) { throw "GameBooster exited with code $($appProcess.ExitCode). Report: $report" }
if (-not (Test-Path -LiteralPath $report)) { throw "GameBooster did not write a startup report: $report" }
$result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
if (-not $result.success -or -not $result.windowLoaded) { throw "Startup failed: $($result.error)" }
if ($result.availablePowerPlans -lt 1) { throw 'Installed power plans were not enumerated.' }
$expectedTheme = if ($Theme -eq 'System') { 'Default' } else { $Theme }
if ($result.appearance.savedTheme -ne $Theme -or $result.appearance.requestedTheme -ne $expectedTheme) { throw 'The appearance preference was not applied.' }
if ($Theme -ne 'System' -and $result.appearance.actualTheme -ne $Theme) { throw 'The rendered theme does not match the requested theme.' }
if (-not $result.appearance.background -or -not $result.appearance.text -or $result.appearance.background -eq $result.appearance.text) { throw 'Theme colors were not resolved correctly.' }
Write-Output "Startup and $Theme theme smoke test passed. Report: $report"
