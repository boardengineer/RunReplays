# Replay Command Format Reference

Lines starting with `#` are header comments. Commands support inline comments delimited by ` # `:

```
ChooseEventOption 0 # ANCIENT_GOLDEN_COMPASS_OPTION
```

## ChooseEventOption

Select an event option by index.

```
ChooseEventOption {index}
```

- `index` — 0-based index into the event's current options list
- `-1` — PROCEED (event finished, advance to map)

The text key of the chosen option is stored as a comment for readability.

**Example:**
```
ChooseEventOption 0 # ANCIENT_GOLDEN_COMPASS_OPTION
ChooseEventOption 1 # EVENT_SLIPPERY_BRIDGE_OPTION_2
ChooseEventOption -1 # PROCEED
```

**Legacy formats** (still parsed):
```
ChooseEventOption {index} {textKey}
ChooseEventOption {textKey}
```

## MoveToMapCoord

Navigate to a map node at the given column. The row is derived at execution time from the player's current position (current row + 1).

```
MoveToMapCoord {col}
```

- `col` — 0-based column index on the map

**Example:**
```
MoveToMapCoord 2
MoveToMapCoord 0
```

**Legacy format** (still parsed):
```
MoveToMapCoordAction {playerId} MapCoord ({col}, {row})
```
