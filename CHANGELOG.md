# Changelog

All notable changes to this project will be documented in this file.

## [1.0.0] - 2026-06-12

### Added

- Headless dedicated server mode: launching with `-batchmode -nographics` auto-hosts a
  lobby and keeps the process running without a window.
- Auto-host from saved server settings (name, capacity up to 64, public/private,
  password, peaceful mode, text-chat-only), shared one-to-one with LobbyKit via the
  `ServerSettings` preferences category.
- Lobby keep-alive: EOS Connect token refresh (DeviceId re-login) and lobby heartbeat so
  the server stays joinable and listed beyond the ~1h token lifetime.
- Self-eviction recovery: detects when EOS silently delists the owned lobby and re-hosts
  once the server is empty, instead of becoming an invisible zombie. Includes lobby
  eviction diagnostics.
- Proper host player: the host appears in every client's player list under a configurable
  `FakeClientName` (with optional rotation through multiple names), backed by a valid host
  PlayerReference so joining clients don't NRE or hang while loading.
- Seats the host player on a bench in peaceful mode.
- Per-instance EOS identity (`ServerInstanceId`) so multiple servers can run on one
  machine without sharing a PUID and splitting joiners' packets.
- Headless safety patches: audio muting and stubs/guards for IL2CPP managers and
  callbacks that NRE with no renderer or local player.
- Clean shutdown that destroys the EOS lobby on exit (no ghost lobby), with a fast
  process/container kill on a graceful stop, plus a ghost-lobby sweep.
- Docker + GE-Proton support for running the Windows IL2CPP build headlessly on Linux.
- Windows `launch-headless.bat` and Linux `scripts/launch-headless.sh` launch helpers.
- Client-side spawn diagnostics when the mod is loaded on a non-headless client.
