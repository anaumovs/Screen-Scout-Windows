@echo off
setlocal
set "SCOUT_APP_DIR=%~dp0"
rem Use an environment variable so folder names are never interpreted as PowerShell code.
powershell.exe -NoLogo -NoProfile -Command "$ErrorActionPreference='Stop'; try { $folder=[IO.Path]::GetFullPath($env:SCOUT_APP_DIR); $app=Join-Path $folder 'ScreenScoutTest.exe'; if (!(Test-Path -LiteralPath $app -PathType Leaf)) { throw 'Extract the entire Screen Scout Test ZIP, then run this file beside ScreenScoutTest.exe.' }; $desktop=[Environment]::GetFolderPath('DesktopDirectory'); if ([string]::IsNullOrWhiteSpace($desktop) -or !(Test-Path -LiteralPath $desktop -PathType Container)) { throw 'Windows could not find your desktop folder.' }; $shell=New-Object -ComObject WScript.Shell; $index=1; $name='Screen Scout Test'; while ($true) { $link=Join-Path $desktop ($name+'.lnk'); $shortcut=$shell.CreateShortcut($link); if (!(Test-Path -LiteralPath $link) -or [string]::Equals($shortcut.TargetPath,$app,[StringComparison]::OrdinalIgnoreCase)) { break }; $index++; $name='Screen Scout Test ('+$index+')' }; $shortcut.TargetPath=$app; $shortcut.WorkingDirectory=$folder; $shortcut.IconLocation=$app+',0'; $shortcut.Description='Open Screen Scout Test for Windows'; $shortcut.WindowStyle=1; $shortcut.Save(); Write-Host ('Desktop shortcut ready: '+$link); exit 0 } catch { Write-Host ('Could not create the shortcut: '+$_.Exception.Message); exit 1 }"
if errorlevel 1 (
    echo.
    echo You can also right-click ScreenScoutTest.exe and choose Send to - Desktop.
    pause
    exit /b 1
)
echo.
echo Open Screen Scout Test using its new desktop icon.
echo Close the app with X. Press F8 to stop clicking while leaving the app open.
echo Keep this extracted folder in place. If you move it, run this setup again.
pause
exit /b 0
