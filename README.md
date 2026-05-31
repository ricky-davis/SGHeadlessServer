# LobbyKit

Host and lobby quality-of-life tools for **Sledding Game**.

## Host Requirement

LobbyKit is primarily a host/server-side mod. Most functionality (especially chat commands) is driven by the lobby host having LobbyKit installed. Clients generally do not need to install LobbyKit to join and use host-provided features.

## Features

- Host lobbies with up to 64 players.
- Add a custom lobby name and saved create-lobby defaults.
- Save public/private, password, peaceful mode, and text-chat-only lobby preferences.
- Improve lobby search with saved filters, inclusive max-player filtering, region preference, modded/locked lobby toggles, and current-player sorting.
- Show live session timers beside player names in the player list.
- Send join and leave messages with configurable size.
- Send an optional message of the day to players who join your hosted lobby.
- Use chat commands for settings and teleport workflows.

## Chat Commands

| Command | Host only | Description |
| --- | --- | --- |
| `!!` | No | Repeat last command |
| `!settings` | Yes | Opens the in-game LobbyKit settings menu. |
| `!motd [message]` | Set: yes | Shows the message of the day. Hosts can pass a message to update it. |
| `!bc <on\|off>` | Yes | Enables or disables guest bang commands. |
| `!tp <name>` | No | Teleport to a player by name. |
| `!tpme <name>` | No | Ask another player to teleport to you. |
| `!tpa` | No | Accept a pending teleport request. |
| `!tpf <name>` | Yes | Force a player to teleport to you. |

## Installation

### Gale / r2modman

Install with Gale or r2modman. MelonLoader is declared as a dependency.

### Manual

1. Install MelonLoader for Sledding Game.
2. Launch the game once so MelonLoader generates its folders and IL2CPP assemblies.
3. Copy `LobbyKit.dll` into the game's `Mods/` folder.
4. Start the game through MelonLoader.

For hosted-lobby features, install LobbyKit on the host machine; clients typically do not need a local install.

## Configuration

Most options are available in-game through `!settings` while hosting. The mod also stores preferences through MelonLoader under the `LobbyKit` category.

## CI/CD (Thunderstore)

This repository includes GitHub Actions workflows for Thunderstore packaging and publishing:

- `.github/workflows/github-release.yml`
  - Manual workflow dispatch.
  - Creates a GitHub Release from a version in `CHANGELOG.md` (extracts that version's notes).
- `.github/workflows/thunderstore-build.yml`
  - Runs on push/PR/manual dispatch.
  - Builds `LobbyKit` and uploads a Thunderstore zip artifact.
- `.github/workflows/thunderstore-publish.yml`
  - Runs when a GitHub Release is published (or manually via workflow dispatch).
  - Builds and publishes with `tcli publish`.
  - All workflows accept `dryrun` on manual dispatch; `dryrun=true` echoes commands and skips execution-sensitive steps.

Required repository secret:

- `THUNDERSTORE_TOKEN`: Thunderstore service account token (used as `TCLI_AUTH_TOKEN`).
- `SGREFROOT_TOKEN`: GitHub token with read access to `ricky-davis/SGRefRoot` (used to fetch `Il2CppAssemblies` and `net6` refs).
- `RELEASE_WORKFLOW_TOKEN`: PAT used by `github-release.yml` to create GitHub Releases so `release`-triggered workflows run.

### Local Pre-Commit Hook

This repo includes a pre-commit hook that enforces version consistency across:

- `LobbyKit/Directory.Build.props` (`<Version>`)
- `thunderstore.toml` (`versionNumber`)
- `CHANGELOG.md` (latest `## [x.y.z]` heading)

Enable repo hooks once per clone:

```bash
git config core.hooksPath .githooks
```
