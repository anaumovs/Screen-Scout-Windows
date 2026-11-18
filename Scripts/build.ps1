param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/ScoutChecks/ScoutChecks.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Behavioral checks failed.' }
    $output = Join-Path (Get-Location) "dist/$Runtime"
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    foreach ($project in @('ScreenScout', 'ClickTestPad')) {
        dotnet publish "src/$project/$project.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $output
        if ($LASTEXITCODE -ne 0) { throw "$project publish failed." }
    }
    Copy-Item -LiteralPath README.md, RELEASE_NOTES.md -Destination $output
    Copy-Item -LiteralPath 'Scripts/Add Desktop Shortcut.cmd', 'Scripts/verify-external.ps1' -Destination $output
    Copy-Item -LiteralPath docs, notices -Destination $output -Recurse -Force
    $zip = Join-Path (Get-Location) 'dist/Screen-Scout-Windows-x64.zip'
    Compress-Archive -Path "$output/*" -DestinationPath $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $([IO.Path]::GetFileName($zip))" -Encoding ascii
    Write-Host "Package: $zip"
    Write-Host "SHA256: $hash"
} finally { Pop-Location }
