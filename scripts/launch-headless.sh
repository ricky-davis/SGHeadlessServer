#!/usr/bin/env bash
# Launch the headless server from GameData and tail the latest log.
# Usage: ./scripts/launch-headless.sh [--no-tail]

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$SCRIPT_DIR/../GameData"
GAME_EXE_WIN="M:\\CodingProjects\\Modding\\SleddingGame\\HeadlessServer\\GameData\\Sledding Game.exe"
LOG_DIR="$GAME_DIR/MelonLoader/Logs"

# Kill any existing instance
taskkill.exe /IM "Sledding Game.exe" /F 2>/dev/null && echo "[launch] Killed existing instance"

# Remove the latest log so we can detect the new one cleanly
LATEST=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)
[ -n "$LATEST" ] && echo "[launch] Previous log: $(basename "$LATEST")"

powershell.exe -Command "
\$exe = '$GAME_EXE_WIN'
\$dir = [System.IO.Path]::GetDirectoryName(\$exe)
Start-Process -FilePath \$exe -ArgumentList '-batchmode','-nographics' -WorkingDirectory \$dir
" && echo "[launch] Game started."

# Wait for a new log file to appear
echo "[launch] Waiting for log..."
until NEW=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1) && [ "$NEW" != "$LATEST" ] && [ -n "$NEW" ]; do
    sleep 1
done
echo "[launch] Log: $(basename "$NEW")"

if [[ "$1" != "--no-tail" ]]; then
    echo "[launch] Tailing log (Ctrl+C to stop)..."
    tail -f "$NEW"
fi
