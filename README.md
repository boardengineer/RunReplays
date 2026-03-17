# RunReplays

A **Slay the Spire 2** mod that automatically records your runs and lets you replay them from the main menu. Every decision — card plays, reward picks, event choices, shop purchases — is captured in the background and can be played back at any time.

## Table of Contents

- [Installation](#installation)
- **For Users**
  - [Recording](#recording)
  - [Replay](#replay)
  - [Mid-run resume](#mid-run-resume)
  - [Usage](#usage)

## Installation

Copy `RunReplays.dll` and `RunReplays.pck` into the game's mod folder:
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