@echo off
setlocal

set GAME_EXE=M:\CodingProjects\Modding\SleddingGame\HeadlessServer\GameData\Sledding Game.exe
set GAME_DIR=M:\CodingProjects\Modding\SleddingGame\HeadlessServer\GameData

powershell.exe -NoProfile -Command ^
  "$psi = New-Object System.Diagnostics.ProcessStartInfo;" ^
  "$psi.FileName = '%GAME_EXE%';" ^
  "$psi.Arguments = '-batchmode -nographics';" ^
  "$psi.WorkingDirectory = '%GAME_DIR%';" ^
  "$psi.UseShellExecute = $false;" ^
  "[System.Diagnostics.Process]::Start($psi) | Out-Null;" ^
  "Write-Host 'Headless server launched.'"

endlocal
