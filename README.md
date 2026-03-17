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

See the [replay file format](docs/sts2replay-format.md) for details on the `.sts2replay` log format.

### Replay
Load any recorded run from the **Run Replays** button on the main menu.  Replays will always executed the recorded commands in order for the applicable section
Replay Options:

- Replay the entire run through to a given save
- Replay the run starting at some lower floor
- Replay the single floor
- Load the game to the given point (same as continuing to that floor)