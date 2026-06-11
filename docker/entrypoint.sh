#!/usr/bin/env bash
# Entrypoint for the headless Sledding Game server.
#
# The (paid) game files come from a prepared GameData directory — the game plus
# MelonLoader and your Mods — bind-mounted at /game. Mods, UserData and
# MelonLoader/Logs are subfolders of $GAME_DIR, so binding /game to a host path
# makes all three accessible from the host automatically.
set -euo pipefail

PUID=${PUID:-1000}
PGID=${PGID:-1000}
GAME_DIR=${GAME_DIR:-/game}
PROTON=${PROTON:-/opt/proton/current/proton}
COMPAT=${STEAM_COMPAT_DATA_PATH:-/compatdata}
STEAM_COMPAT_CLIENT_INSTALL_PATH=${STEAM_COMPAT_CLIENT_INSTALL_PATH:-/home/steam/.steam/steam}

# --- Privileged setup: prepare writable mounts, then drop to PUID:PGID -----------------
if [ "$(id -u)" = "0" ] && [ "${_DROPPED:-0}" != "1" ]; then
    mkdir -p "$COMPAT" "$STEAM_COMPAT_CLIENT_INSTALL_PATH" /home/steam

    # EOS and dbus want a stable /etc/machine-id. Persist one in the prefix volume so this
    # instance keeps the same identity across restarts (and differs from other instances).
    MID_FILE="$COMPAT/machine-id"
    [ -s "$MID_FILE" ] || tr -d '-' < /proc/sys/kernel/random/uuid > "$MID_FILE"
    cp "$MID_FILE" /etc/machine-id
    mkdir -p /var/lib/dbus && cp "$MID_FILE" /var/lib/dbus/machine-id

    # Prefix + steam dirs must be writable by the runtime user. We deliberately do NOT
    # chown the whole (multi-GB) game dir; only the folders the game writes to.
    chown -R "$PUID:$PGID" "$COMPAT" /home/steam 2>/dev/null || true
    for d in Mods UserData MelonLoader MelonLoader/Logs; do
        mkdir -p "$GAME_DIR/$d"
        chown "$PUID:$PGID" "$GAME_DIR/$d" 2>/dev/null || true
    done
    export _DROPPED=1
    exec gosu "$PUID:$PGID" "$0" "$@"
fi

export HOME=/home/steam
export STEAM_COMPAT_DATA_PATH="$COMPAT"
export STEAM_COMPAT_CLIENT_INSTALL_PATH

EXE="$GAME_DIR/Sledding Game.exe"
if [ ! -f "$EXE" ]; then
    echo "[entry] '$EXE' not found." >&2
    echo "[entry] Mount a prepared GameData (game + MelonLoader + Mods) at $GAME_DIR." >&2
    echo "[entry] See docker/README.md for how to copy it in." >&2
    exit 1
fi

mkdir -p "$GAME_DIR/Mods" "$GAME_DIR/UserData" "$GAME_DIR/MelonLoader/Logs"

# MelonLoader ships as a native version.dll proxy; tell Wine to prefer the game's own
# copy over the builtin. winhttp is overridden too in case a doorstop build is used.
export WINEDLLOVERRIDES="version=n,b;winhttp=n,b${WINEDLLOVERRIDES:+;$WINEDLLOVERRIDES}"
# Headless: no audio, no GPU renderer.
export PULSE_SERVER=none
# Keep Wine's err/warn channels (only silence noisy fixme) so crashes are visible.
export WINEDEBUG=${WINEDEBUG:-fixme-all}
# Have Proton write a full log to a host-visible folder for diagnosis.
export PROTON_LOG=${PROTON_LOG:-1}
export PROTON_LOG_DIR=${PROTON_LOG_DIR:-$GAME_DIR/MelonLoader/Logs}

cd "$GAME_DIR"
echo "[entry] Proton: $(basename "$(readlink -f "$(dirname "$PROTON")")")"
echo "[entry] Launching: Sledding Game.exe -batchmode -nographics"

# Proton does not forward the Windows app's console to our stdout, so mirror MelonLoader's
# log file to stdout — this is what makes `docker compose logs -f` show live server output.
# tail -F follows by name and survives the game truncating/recreating the file each launch.
LOG="$GAME_DIR/MelonLoader/Latest.log"
tail -n 0 -F "$LOG" 2>/dev/null &
TAIL_PID=$!

# Unity under Wine/Proton needs an X display even with -nographics. Start Xvfb OURSELVES (not via xvfb-run)
# and OUTSIDE the game's process group, so the graceful-shutdown signal we send to the game does not also
# tear the display down from under it mid-shutdown.
if [ "${USE_XVFB:-1}" = "1" ] && [ -z "${DISPLAY:-}" ] && command -v Xvfb >/dev/null 2>&1; then
    rm -f /tmp/.X99-lock 2>/dev/null || true
    Xvfb :99 -screen 0 1280x720x24 -nolisten tcp >/dev/null 2>&1 &
    XVFB_PID=$!
    export DISPLAY=:99
    sleep 1   # let the display come up before the game initialises
fi

cleanup() { kill "${TAIL_PID:-0}" "${XVFB_PID:-0}" 2>/dev/null || true; }
trap cleanup EXIT

# GRACEFUL SHUTDOWN. `docker stop` / `docker compose down` sends SIGTERM to us (PID 1). SledHeadless only
# destroys the EOS lobby on a Windows console-control event (SetConsoleCtrlHandler) — otherwise the lobby
# lingers as a ghost. Wine maps a Unix SIGINT to CTRL_C_EVENT for the console app, so on SIGTERM we forward
# SIGINT to the game's process group and WAIT for it to shut down cleanly. We launch under `setsid` so the
# whole Proton/Wine tree shares one process group we can signal as a unit. compose's stop_grace_period is the
# hard backstop if the game ever hangs.
setsid python3 "$PROTON" run "$EXE" -batchmode -nographics &
GAME_PID=$!
GAME_PGID=$(ps -o pgid= -p "$GAME_PID" 2>/dev/null | tr -d ' '); [ -n "$GAME_PGID" ] || GAME_PGID=$GAME_PID

graceful_shutdown() {
    echo "[entry] Stop requested — delivering CTRL_C to the game so SledHeadless destroys the EOS lobby..."
    # Primary: SIGINT the game's process group (Wine maps it to CTRL_C_EVENT for the console app).
    kill -INT "-$GAME_PGID" 2>/dev/null || kill -INT "$GAME_PID" 2>/dev/null || true
    # Fallback (in case Wine put the game in a different group): SIGINT the Wine game process DIRECTLY, but
    # never the python3 Proton launcher — signalling that could kill the game before its handler runs.
    for p in $(pgrep -f 'Sledding Game.exe' 2>/dev/null); do
        case "$(ps -o comm= -p "$p" 2>/dev/null | tr -d ' ')" in
            python3|python) ;;                                  # skip the Proton launcher
            *) kill -INT "$p" 2>/dev/null || true ;;
        esac
    done
}
trap graceful_shutdown SIGTERM SIGINT

set +e
# Re-enter wait if a trapped signal interrupts it, so the game finishes its graceful shutdown and we then
# reap its real exit code (a signal-interrupted wait returns >128 while the child is still alive).
code=0
while :; do
    wait "$GAME_PID"; code=$?
    if [ "$code" -gt 128 ] && kill -0 "$GAME_PID" 2>/dev/null; then continue; fi
    break
done
set -e

echo "[entry] Game process exited with code $code"

# On failure, surface the Proton log too (Wine-level errors don't reach Latest.log).
if [ "$code" != "0" ]; then
    PLOG=$(ls -t "$PROTON_LOG_DIR"/steam-*.log "$HOME"/steam-*.log 2>/dev/null | head -1)
    if [ -n "$PLOG" ]; then
        echo "[entry] ===== tail of Proton log ($(basename "$PLOG")) ====="
        tail -n 40 "$PLOG" || true
        echo "[entry] ============================================="
    fi
    sleep 5   # slow the restart loop so logs stay readable
fi
exit $code
