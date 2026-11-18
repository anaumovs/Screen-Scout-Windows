param([string]$AppPath = "")
$ErrorActionPreference = "Stop"
if (!$AppPath) {
    $beside = Join-Path $PSScriptRoot "ScreenScoutTest.exe"
    if (Test-Path $beside) { $AppPath = $beside }
    else { $AppPath = Join-Path (Split-Path $PSScriptRoot -Parent) "dist/win-x64/ScreenScoutTest.exe" }
}
$AppPath = (Resolve-Path $AppPath).Path
if (Get-Process -Name ScreenScout,ScreenScoutTest -ErrorAction SilentlyContinue) { throw "Quit Screen Scout first." }
$folder = Join-Path $env:LOCALAPPDATA "ScreenScoutTest/verification"
New-Item -ItemType Directory -Force $folder | Out-Null
$report = Join-Path $folder ("external-" + [guid]::NewGuid().ToString("N") + ".json")
Write-Host "Leave the harmless Click Test Pad focused. F8 stops the entire test."
$arguments = '--verify-external --report "' + $report + '"'
$process = Start-Process -FilePath $AppPath -ArgumentList $arguments -PassThru
if (!$process.WaitForExit(240000)) {
    $process.CloseMainWindow() | Out-Null
    throw "Verification timed out. Stop Screen Scout with F8; inspect the test pad and report folder."
}
if (!(Test-Path $report)) { throw "The current run did not produce its report." }
$result = Get-Content $report -Raw | ConvertFrom-Json
$failed = @($result.Checks | Where-Object { !$_.Passed })
Write-Host "$($result.Checks.Count - $failed.Count)/$($result.Checks.Count) checks passed. Report: $report"
if ($process.ExitCode -ne 0 -or !$result.Completed -or !$result.Stopped -or !$result.SettingsRestored -or $result.Checks.Count -eq 0 -or $failed.Count -ne 0) {
    $failed | Format-Table
    throw "External verification failed."
}
