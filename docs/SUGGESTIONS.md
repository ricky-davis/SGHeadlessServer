# LobbyKit Suggestions

## Status Legend

- Approved: worth designing and implementing.
- Maybe: plausible, but needs a sharper scope or stronger reason.
- Probably Not Supported: likely blocked by EOS or game APIs, but not fully proven.
- Already Implemented: already covered by the mod.
- Base Game: already covered well enough by the game itself.
- Rejected: intentionally out of scope.

## Approved

- [Approved] Search result count and active filter summary near refresh.
  Area: Lobby Browser QoL.

- [Approved] Host announcement or broadcast message button.
  Area: Host / Server-Side Tools.


## Maybe

- [Maybe] Recently joined lobbies list.
  Area: Lobby Browser QoL.

- [Maybe] Host-only quick actions panel.
  Area: Host / Server-Side Tools. (What would this be?)

## Probably Not Supported

- [Probably Not Supported] Change max players while lobby is active, if EOS/game lobby updates support it.
  Area: Host / Server-Side Tools.

- [Probably Not Supported] Change lobby name while active.
  Area: Host / Server-Side Tools.

## Already Implemented

- [Already Implemented] Enhanced player list with join time.

- [Already Implemented] In-game LobbyKit settings panel.
  Area: Config / Preferences.

## Base Game (Rejected)

- [Base Game] Host capacity warning for very high max player counts.
  Area: Create Lobby QoL.

- [Base Game] Kick by player name from the in-game player list.
  Area: Host / Server-Side Tools.

- [Base Game] Ban or session-block list for the current lobby.
  Area: Host / Server-Side Tools.

- [Base Game] Temp mute or voice mute controls if exposed by the game.
  Area: Host / Server-Side Tools.

- [Base Game] Show host status and whether the local player is current host.
  Area: Lobby Health / Diagnostics.

## Rejected

### Lobby Browser QoL

- [Rejected] Saved lobby filter profiles, such as Public Any, Modded Only, High Population.
- [Rejected] Sort modes for current players, max players, lobby name, region, and modded status.
- [Rejected] Hide empty lobbies toggle.
- [Rejected] Hide full lobbies toggle.
- [Rejected] Favorite lobby names or hosts, with highlighted rows.
- [Rejected] Lobby row badges for password, modded, peaceful, region, language, and voice chat.
- [Rejected] Auto-refresh toggle with interval control.
- [Rejected] Join best lobby button using preferred filters and highest population.

### Create Lobby QoL

- [Rejected] Create lobby presets for casual, public modded, private friends, high-capacity, and peaceful lobbies.
- [Rejected] Remember last lobby name template.
- [Rejected] Lobby name variables, such as `{player}`, `{region}`, `{mode}`, and `{capacity}`.
- [Rejected] One-click copy of the last created lobby settings.
- [Rejected] Better validation before create, including password required, name too long, and incompatible toggles.
- [Rejected] Auto-generate password button.

### Host / Server-Side Tools

- [Rejected] Force lobby privacy toggle while hosted.
- [Rejected] Toggle join-in-progress on or off.
- [Rejected] Lock lobby without recreating it.

### Player Management

- [Rejected] Copy player ID or product user ID.
- [Rejected] Local notes per player.
- [Rejected] Mark trusted players.
- [Rejected] Auto-kick blocked players on join.
- [Rejected] Alert when blocked or favorited players join.
- [Rejected] Detect duplicate or suspicious names.

### Lobby Health / Diagnostics

- [Rejected] Show EOS lobby ID with a copy button.
- [Rejected] Show connection and RTC status.
- [Rejected] Show lobby attributes in a debug modal.
- [Rejected] Why can't I join diagnostics for password, full lobby, version mismatch, modded mismatch, and similar cases.
- [Rejected] Optional verbose lobby search diagnostics toggle.
- [Rejected] Export current lobby or search debug info to a text file.

### Gameplay Session QoL

- [Rejected] Host migration warning or status, if exposed by the game.
- [Rejected] Ready-check overlay for lobby players.
- [Rejected] Auto-start countdown helper.
- [Rejected] Minimum players gate before starting.
- [Rejected] Force sync button if there is an exposed flow.
- [Rejected] Quick preset chat messages for host instructions.

### Config / Preferences

- [Rejected] Import and export mod settings.
- [Rejected] Reset lobby browser settings button.
- [Rejected] Per-feature enable toggles.
- [Rejected] Separate Client QoL and Host Tools categories.
- [Rejected] Optional hotkeys for refresh, join selected, open create lobby, and host panel.
