# SledHeadless

Run **Sledding Game** as a headless dedicated server.

SledHeadless is a MelonLoader (IL2CPP) mod. When the game is launched with `-batchmode
-nographics`, it auto-hosts a lobby, keeps that lobby alive and listed, shows the host in
everyone's player list, and stays running indefinitely without a window — so you can host
a persistent server on a spare PC or in a container.

It is the headless **plumbing**; it does not add gameplay or admin commands. Pair it with
[LobbyKit](https://github.com/ricky-davis/SGLobbyKit) on the same server for chat
commands, permissions, moderation, and anticheat (the two share their server settings).

## Features

- **Auto-host on launch** — creates a lobby from your saved server settings (name,
  capacity, public/private, password, peaceful mode, text-chat-only). Capacity supports
  the extended 64-player range.
- **Stays listed 24/7** — refreshes the EOS Connect token, drives the lobby heartbeat,
  and detects/recovers when EOS silently delists the lobby (re-hosts when empty) so the
  server doesn't become an invisible zombie after a few hours.
- **Proper host player** — the host shows up in every client's player list under a name
  you choose (`FakeClientName`, with optional rotation), backed by a valid host
  PlayerReference so joiners don't NRE or hang while loading in. Seats the host on a bench
  in peaceful mode.
- **Headless-safe** — mutes audio, and patches the IL2CPP managers/callbacks that would
  otherwise NRE with no renderer/player present.
- **Clean shutdown** — destroys the EOS lobby on exit so it doesn't leave a ghost entry,
  with a fast process/container kill on a graceful stop.
- **Multiple instances per host** — give each server its own EOS identity
  (`ServerInstanceId`) so two servers on one machine don't share a PUID and split a
  joiner's packets.
- **Docker + GE-Proton** — run the Windows IL2CPP build headlessly on Linux in a
  container (see [`docker/`](docker/README.md)).

## Requirements

- A licensed, mod-ready **Sledding Game** install (see Setup below). The game is a paid
  title and is never downloaded for you.
- **MelonLoader** installed into that game directory.
- `SledHeadless.dll` in `Mods/`. (Recommended: also `LobbyKit.dll` for gameplay/admin.)
- Networking is **EOS** (Epic) P2P/relay — no Steam client and no published ports
  required, just outbound internet.

## Setup — provide the game files

The server runs from a `GameData/` folder at the repo root: your own licensed copy of the
game plus MelonLoader and your mods. It is git-ignored and never committed.

1. **Copy your licensed game install into `GameData/`.** From the repo root, copy your
   Steam install of Sledding Game to a folder named `GameData`:

   - Windows (PowerShell):
     ```powershell
     Copy-Item -Recurse "C:\Program Files (x86)\Steam\steamapps\common\Sledding Game" GameData
     ```
   - Linux / WSL:
     ```bash
     cp -a "/mnt/c/Program Files (x86)/Steam/steamapps/common/Sledding Game" GameData
     ```

2. **Install MelonLoader** into `GameData/` (so it contains the `version.dll` proxy and a
   `MelonLoader/` folder). Run the game once normally so MelonLoader generates its folders
   and IL2CPP assemblies.

3. **Add the mod(s):** drop `SledHeadless.dll` (and optionally `LobbyKit.dll`) into
   `GameData/Mods/`.

For Docker you can copy this same `GameData/` into `docker/`, or point `GAME_DIR_HOST` at
it — see [`docker/README.md`](docker/README.md).

## Quick start

### Windows

From this repo root (with your prepared `GameData` alongside `launch-headless.bat`):

```bat
launch-headless.bat
```

It starts `GameData\Sledding Game.exe -batchmode -nographics`. Logs go to
`GameData\MelonLoader\Logs\`.

### Linux (Docker + Proton)

Containerised headless server using GE-Proton. See **[`docker/README.md`](docker/README.md)**
for the full guide; the short version:

```bash
cd docker
cp .env.example .env          # set PUID/PGID and GAME_DIR_HOST
docker compose up --build -d
docker compose logs -f
docker compose down           # graceful stop closes the EOS lobby (no ghost)
```

## Configuration

Settings live in `UserData/MelonPreferences.cfg` (edit while the server is stopped).

**`[ServerSettings]`** — shared one-to-one with LobbyKit, so both read the same defaults:

| Key | Default | Description |
| --- | --- | --- |
| `ServerName` | `""` | Lobby name. Empty = `"<PlayerName>'s Lobby"`. |
| `ServerCapacity` | `8` | Max players (supports up to 64). |
| `IsPublicLobby` | `true` | Public or private lobby. |
| `IsPasswordProtected` | `false` | Require a password to join. |
| `LobbyPassword` | `""` | Password when protection is on. |
| `IsPeacefulMode` | `false` | Peaceful (no-collision) mode. |
| `IsTextChatOnly` | `false` | Disable voice, text chat only. |

**`[SledHeadless]`** — headless-specific:

| Key | Default | Description |
| --- | --- | --- |
| `HeadlessAutoHost` | `true` | Auto-host a lobby on launch. |
| `FakeClientName` | `[]` | Name(s) the host shows as in the player list. Provide more than one to rotate. Empty = the server name (or `"Server"`). |
| `FakeClientNameRotateSeconds` | `30` | Seconds between name rotations when more than one is set (`0` disables). |
| `ServerInstanceId` | `""` | This instance's EOS identity. Blank = auto-generate and store a GUID in `UserData/SledHeadless-instance.id`. Set distinct values to run several servers on one host. |

## CI/CD (Thunderstore)

This repository includes GitHub Actions workflows for Thunderstore packaging and publishing:

- `.github/workflows/github-release.yml`
  - Manual workflow dispatch.
  - Creates a GitHub Release from a version in `CHANGELOG.md` (extracts that version's notes).
- `.github/workflows/thunderstore-build.yml`
  - Runs on push/PR/manual dispatch.
  - Builds `SledHeadless` and uploads a Thunderstore zip artifact.
- `.github/workflows/thunderstore-publish.yml`
  - Runs when a GitHub Release is published (or manually via workflow dispatch).
  - Builds and publishes with `tcli publish`.
  - All workflows accept `dryrun` on manual dispatch; `dryrun=true` echoes commands and skips execution-sensitive steps.

Required repository secrets:

- `THUNDERSTORE_TOKEN`: Thunderstore service account token (used as `TCLI_AUTH_TOKEN`).
- `SGREFROOT_TOKEN`: GitHub token with read access to `ricky-davis/SGRefRoot` (used to fetch `Il2CppAssemblies` and `net6` refs).
- `RELEASE_WORKFLOW_TOKEN`: PAT used by `github-release.yml` to create GitHub Releases so `release`-triggered workflows run.

### Local Pre-Commit Hook

This repo includes a pre-commit hook that enforces version consistency across:

- `SledHeadless/Directory.Build.props` (`<Version>`)
- `thunderstore.toml` (`versionNumber`)
- `CHANGELOG.md` (latest `## [x.y.z]` heading)

Enable repo hooks once per clone:

```bash
git config core.hooksPath .githooks
```
