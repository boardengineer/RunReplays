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

## ClaimReward

Click a reward button on the rewards screen by index. Handles gold, relic, potion, and card rewards. For card rewards, this opens the card selection screen — a `TakeCard` command follows.

```
ClaimReward {index}
```

- `index` — 0-based index among all reward buttons currently on the screen

The reward type and description are stored as a comment for readability.

**Example:**
```
ClaimReward 0 # GoldReward: 25
ClaimReward 1 # RelicReward: Vajra
ClaimReward 2 # PotionReward: Fire Potion
ClaimReward 1 # CardReward
```

**Legacy formats** (still parsed):
```
TakeGoldReward: {amount}
TakeRelicReward: {title}
TakePotionReward: {title}
TakeCardReward[{index}]: {cardTitle}
TakeCardReward: {cardTitle}
```

## TakeCard

Select a card from the card reward selection screen, or sacrifice (Pael's Wing). Follows a `ClaimReward` that opened the card selection screen.

```
TakeCard {index}
TakeCard sacrifice
```

- `index` — 0-based index of the card holder in the selection screen
- `sacrifice` — triggers Pael's Wing sacrifice instead of picking a card

The card title or sacrifice option is stored as a comment for readability.

**Example:**
```
TakeCard 0 # Bash
TakeCard 2 # Inflame
TakeCard sacrifice # sacrifice
```

**Legacy formats** (still parsed):
```
SacrificeCardReward[{index}]
SacrificeCardReward
```

## EndTurn

End the player's turn during combat.

```
EndTurn
```

No parameters. The original game action details are stored as a comment for readability.

**Example:**
```
EndTurn # EndPlayerTurnAction for player 1 round 3
EndTurn
```

**Legacy format** (still parsed):
```
EndPlayerTurnAction for player {playerId} round {round}
```

## DiscardPotion

Discard a potion from the player's potion belt.

```
DiscardPotion {slotIndex}
```

- `slotIndex` — 0-based index into the potion belt

**Example:**
```
DiscardPotion 0
DiscardPotion 1
```

**Legacy format** (still parsed):
```
NetDiscardPotionGameAction for player {netId} potion slot: {slotIndex}
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
