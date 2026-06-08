@echo off
setlocal

set "GAME_DIR=%CD%\GameData"
set "GAME_EXE=%GAME_DIR%\Sledding Game.exe"

powershell.exe -NoProfile -Command ^
  "$psi = New-Object System.Diagnostics.ProcessStartInfo;" ^
  "$psi.FileName = '%GAME_EXE%';" ^
  "$psi.Arguments = '-batchmode -nographics';" ^
  "$psi.WorkingDirectory = '%GAME_DIR%';" ^
  "$psi.UseShellExecute = $false;" ^
  "[System.Diagnostics.Process]::Start($psi) | Out-Null;" ^
  "Write-Host 'Headless server launched.'"

endlocal