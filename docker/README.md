# Headless Sledding Game server — Docker + Proton

Runs the SledHeadless/LobbyKit dedicated server on Linux inside a container, using
**GE-Proton** (Valve's Steam Play / Proton, GloriousEggroll build) to run the Windows
IL2CPP game with `-batchmode -nographics`. This is the Linux equivalent of
`launch-headless.bat`.

## What you need on the host

- Docker + the Compose plugin (`docker compose`).
- A **prepared `GameData` directory** — the game *plus* MelonLoader and your `Mods/`,
  exactly like the one this repo runs on Windows. It's a paid title, so the container
  never downloads it; you copy in your own licensed, mod-ready install.

## Provide the game files (copy GameData)

Copy your prepared `GameData` into this `docker/` folder so the stack is self-contained.
From the repo root (`HeadlessServer/`):

```bash
cp -a GameData docker/GameData
```

That folder already includes MelonLoader (the `version.dll` proxy + `MelonLoader/`) and
your `Mods/`, so nothing else needs installing. If you'd rather not duplicate ~2 GB,
point `GAME_DIR_HOST` in `.env` at an existing install instead of copying — e.g.
`GAME_DIR_HOST=../GameData` to use the repo's copy directly, or an absolute path.

> The container only ever **reads** the game binaries and **writes** to `Mods/`,
> `UserData/`, and `MelonLoader/Logs/`. It is never re-downloaded or patched.

## Quick start

```bash
cd docker
cp .env.example .env
# edit .env: set PUID/PGID to `id -u` / `id -g`, and GAME_DIR_HOST if not ./GameData.
docker compose up --build -d
docker compose logs -f          # watch MelonLoader / LobbyKit output
```

The first launch is slow — Proton builds its Wine prefix in the `proton-prefix` volume.
Subsequent starts reuse it (and keep the EOS device identity stable).

Stop gracefully (lets the server close its EOS lobby so it doesn't leave a ghost):

```bash
docker compose down            # honours the 45s stop grace period
```

## Host-accessible folders

`Mods/`, `UserData/`, and `MelonLoader/Logs/` are subdirectories of the mounted game
directory, so they appear on the host under `GAME_DIR_HOST` automatically:

| Inside container | On host |
| --- | --- |
| `/game/Mods` | `${GAME_DIR_HOST}/Mods` |
| `/game/UserData` | `${GAME_DIR_HOST}/UserData` |
| `/game/MelonLoader/Logs` | `${GAME_DIR_HOST}/MelonLoader/Logs` |

Drop an updated `LobbyKit.dll` into `Mods/` on the host and restart the container to
pick it up. Edit `UserData/LobbyKit-permissions.json` / `MelonPreferences.cfg` while the
container is stopped.

## How it works

- **`Dockerfile`** — Debian + i386 libs and a pinned GE-Proton at `/opt/proton/current`.
  Override the build with `PROTON_VERSION`.
- **`entrypoint.sh`** — prepares writable dirs as root, drops to `PUID:PGID`, sets
  `WINEDLLOVERRIDES=version=n,b` (so Wine loads MelonLoader's proxy), then
  `proton run "Sledding Game.exe" -batchmode -nographics`.
- **`proton-prefix` volume** — the persistent Wine prefix (`STEAM_COMPAT_DATA_PATH`). The
  game's EOS identity lives here; keeping it stable matters for the lobby keep-alive.

## Notes & caveats

- **Networking** uses EOS (Epic), not Steam, so the container just needs outbound
  internet (default bridge network is fine). No ports to publish — it's an EOS P2P/relay
  lobby, not a listen server.
- **No Steam client** runs in the container. The game uses the `steam_appid.txt=480`
  spoof, so it should start without a logged-in Steam client; if a future game update
  hard-requires Steamworks, that assumption may need revisiting.
- **Multiple instances on one host:** give each its own `proton-prefix` volume *and* its
  own EOS instance id (see project memory on multi-instance EOS identity), or they'll
  share a PUID and split a joiner's packets.
- **GPU:** none required — `-nographics` skips the renderer. The image installs only
  software/Vulkan loader libs; if Proton complains about a missing library on your host,
  add it to the `apt-get install` list in the `Dockerfile`.
- **First-run cost & size:** the image pulls GE-Proton (~400 MB) and builds a prefix on
  first boot; allow a minute before the server is live.
