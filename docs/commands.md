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

## PlayCard

Play a card from the hand during combat.

```
PlayCard {combatCardIndex}
PlayCard {combatCardIndex} {targetId}
```

- `combatCardIndex` — per-combat unique card instance ID assigned by `NetCombatCardDb`, not a hand position. Each card receives a stable uint ID when it enters combat; the ID persists for the entire fight.
- `targetId` — creature combat ID of the target (omitted for untargeted cards)

The card name is stored as a comment for readability.

**Example:**
```
PlayCard 55419292 # CARD.DEFEND_IRONCLAD
PlayCard 24294472 1 # CARD.BASH
PlayCard 10155926 2 # CARD.STRIKE_IRONCLAD
```

**Legacy format** (still parsed):
```
PlayCardAction card: {cardDescription} index: {combatCardIndex} targetid: {targetId}
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
