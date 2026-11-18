$ErrorActionPreference = "Stop"
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/ScoutChecks/ScoutChecks.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Behavioral checks failed." }
    dotnet build ScreenScout.Windows.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
} finally { Pop-Location }
