# RunReplays

A **Slay the Spire 2** mod that automatically records your runs and lets you replay them from the main menu. Every decision — card plays, reward picks, event choices, shop purchases — is captured in the background and can be played back at any time.

## Installation

Copy `RunReplays.dll` and `RunReplays.pck` into the game's mod folder:
```
C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\
```

---

## For Users

### Recording
Every meaningful player action is captured into a minimal, deterministic command log:

- **Combat** -- card plays, potion uses/discards, end turn
- **Map navigation** -- node selection, act transitions
- **Rewards** -- gold, cards, relics, potions, chest relics
- **Events** -- option choices, card selections (Spirit Grafter, Wood Carvings, etc.)
- **Rest sites** -- heal, smith, and multi-option (Miniature Tent)
- **Shop** -- purchases, card removal
- **Card selections** -- hand picks, deck picks, grid picks, upgrade picks
- **Starting bonus** -- Neow/ancient event choice

Logs are saved automatically on every game save to:
```
%APPDATA%/Godot/app_userdata/Slay the Spire 2/RunReplays/logs/{seed}/floor_{floor}/
```

See the [replay file format](docs/sts2replay-format.md) for details on the `.sts2replay` log format.

### Replay
Load any recorded run from the **Run Replays** button on the main menu. The mod:

- Restores the game save or starts a fresh seeded run
- Automatically executes every recorded action in order
- Waits for animations and sub-effects to settle between actions
- Retries timing-sensitive operations (card plays, UI transitions)
- Displays a live overlay showing the current command context

### Mid-run resume
Replay from any intermediate floor using the corresponding save file. New actions recorded after replay completes are appended to the existing log.

### Usage

1. **Play normally** -- actions are recorded in the background with a small overlay in the top-right corner.
2. **Replay a run** -- from the main menu, click **Run Replays**, pick a seed and floor, and the run replays automatically.
3. **Continue after replay** -- once all recorded commands are exhausted, normal play resumes and new actions append to the log.

---

## For Developers

### Build from source

**Prerequisites:**
- Slay the Spire 2 (Steam, Windows)
- .NET 9.0 SDK

```bash
dotnet build RunReplays.sln -c Release
```

The post-build step copies the DLL to the game's mod directory.

### Architecture

```
PlayerActionBuffer          Records actions into the command log
    |
RunSaveLogger               Persists logs to disk on every game save
    |
ReplayEngine                Command queue with typed Peek/Consume parsers
    |
ReplayRunner                Consumes commands with diagnostic logging
    |
+-- CardPlayReplayPatch     Combat: card plays, potions, end turn
+-- BattleRewardsReplayPatch  Post-combat rewards
+-- MapChoiceReplayPatch    Map node selection
+-- EventOptionReplayPatch  Event choices
+-- RestSiteReplayPatch     Rest site options
+-- ShopReplayPatch         Shop interactions
+-- (20+ more patches)      One per decision point
    |
RunOverlay                  Live HUD showing recent/current commands
RunReplayMenu               Main menu UI for browsing and loading replays
```

Each game decision point has its own Harmony patch that:
- **Records** the player's choice as a compact command string
- **Replays** by consuming the command and invoking the game API directly

### Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [Lib.Harmony](https://github.com/pardeike/Harmony) | 2.4.2 | Runtime method patching |
| [GodotSharp](https://www.nuget.org/packages/GodotSharp) | 4.7.0-dev.2 | Godot engine bindings |
| sts2.dll | -- | Slay the Spire 2 game assembly (local reference) |

---

## License

This project is a modding tool for Slay the Spire 2 by Mega Crit Games. It is not affiliated with or endorsed by Mega Crit.
