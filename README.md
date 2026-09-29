# RunReplays

A **Slay the Spire 2** mod that automatically records your runs and lets you replay them from the main menu. Every decision — card plays, reward picks, event choices, shop purchases — is captured in the background and can be played back at any time.

## Table of Contents

- [Installation](#installation)
- **For Users**
  - [Recording](#recording)
  - [Replay](#replay)

## Installation

Copy `RunReplays.dll`, `RunReplays.pck`, and `RunReplays.json` into the game's mod folder:
```
C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\
```

---

## For Users

### Recording
Logs are saved automatically on every game save to:
```
%APPDATA%/Godot/app_userdata/Slay the Spire 2/RunReplays/logs/{seed}/floor_{floor}/
```

See the [command reference](docs/commands.md) for details on the replay command format.

A sample Ironclad full-run replay is included at [`Resources/LFKFUEPCRA/`](RunReplays/Resources/LFKFUEPCRA/floor_49/actions.sts2replay).

### Replay
Load any recorded run from the **Run Replays** button on the main menu.  Replays will always executed the recorded commands in order for the applicable section
Replay Options:

- Replay the entire run through to a given save
- Replay the run starting at some lower floor
- Replay the single floor
- Load the game to the given point (same as continuing to that floor)
---

## Live command bridge (branch `sts-sim-live-export`)

For external bots (e.g. `C:\sts-sim\tools\real_game_bot.py`). **Inactive unless the game is started with the
environment variable `RUNREPLAYS_LIVE=1`.** All files live in `%APPDATA%\SlayTheSpire2\RunReplays\`:

| file | direction | contents |
|---|---|---|
| `live_cmd.txt` | bot -> game | append-only, one command per line: `seq<TAB>command[ # comment]` (replay syntax, see [commands](docs/commands.md)) |
| `live_ack.txt` | game -> bot | `seq<TAB>ok/error<TAB>detail` of the last executed / rejected command |
| `live_state.json` | game -> bot | `screen` (main_menu, combat, hand_select, grid_select, choose_card, rewards, card_reward, map, event, rest, shop, treasure, game_over), `ready` (no command pending, nothing in flight, state unchanged for `RUNREPLAYS_LIVE_STABLE` x 0.1 s), `available` (the concrete dispatchable commands), `run` (seed, act, floor, HP, gold, deck, relics, potions), combat `hand` with combat card ids, open card `selection` (options in `CardSelectCmd` order with their UI index), rewards, card reward, event options (+ descriptions, hover-tip models), the act map, and at a combat input point `fight` = the sts-sim fight snapshot (`FightExport.cs`) |
| `live_fights.jsonl` | game -> bot | every exported fight snapshot |
| `live_bridge.log` | | bridge log |

Commands are executed by the normal replay dispatcher: a live run is a replay fed one command at a time
(`ReplayEngine.IsActive` is true for the whole run, so the replay-side screen captures work). Every executed command is
appended to the action buffer, so the usual per-floor logs (`logs/<seed>/floor_N/actions.sts2replay` + `run.save`) are
written and the run can be watched with the Run Replays menu. Special commands: `StartRun CHARACTER SEED ASC` (main
menu only; acts rolled like `StartRunLobby`: `ActModel.GetRandomList(Rng(hash(seed), "act_selection"))`),
`Replay SEED[:floor_N]` (same as the menu's replay of that log), `Quit`.

The hard-coded `RUNREPLAYS_AUTOPLAY` target is disabled on this branch.
