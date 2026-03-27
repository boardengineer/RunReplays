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

## OpenChest

Open a treasure chest. Always followed by a `TakeChestRelic` command.

```
OpenChest
```

The expected relic title is stored as a comment for readability.

**Example:**
```
OpenChest # Venerable Tea Set
OpenChest # Runic Pyramid
```

**Legacy format** (still parsed):
```
TakeChestRelic {relicTitle}
```

## TakeChestRelic

Pick the relic from an opened treasure chest. Always follows an `OpenChest` command.

```
TakeChestRelic
```

No parameters — treasure chests offer one relic, always at index 0. The relic title is stored as a comment for readability.

**Example:**
```
TakeChestRelic # Venerable Tea Set
```

**Legacy format** (still parsed):
```
NetPickRelicAction for player {netId} index {relicIndex}
```

## ProceedToNextAct

Advance to the next act after the boss fight.

```
ProceedToNextAct
```

No parameters.

**Legacy formats** (still parsed):
```
NextAct
VoteForMapCoordAction {playerId}
```

## ChooseRestSiteOption

Choose a rest site option (e.g. heal, upgrade a card).

```
ChooseRestSiteOption {optionId}
```

- `optionId` — the option identifier (e.g. `HEAL`, `SMITH`)

**Example:**
```
ChooseRestSiteOption HEAL
ChooseRestSiteOption SMITH
```

## UsePotion

Use a potion from the player's potion belt.

```
UsePotion {slotIndex}
UsePotion {slotIndex} {targetId}
```

- `slotIndex` — 0-based index into the potion belt
- `targetId` — creature combat ID of the target (omitted for self/untargeted potions)

The potion name is stored as a comment for readability.

**Example:**
```
UsePotion 0 # POTION.CLARITY
UsePotion 0 1 # POTION.FIRE_POTION
UsePotion 1 # POTION.STRENGTH_POTION
```

**Legacy format** (still parsed):
```
UsePotionAction {netId} {potionName} ({instanceId}) index: {slotIndex} target: {targetId} ({creatureName}) combat: {inCombat}
```

## SelectGridCard

Select one or more cards from a grid selection screen (deck selection, card removal, upgrade, simple grid picks).

```
SelectGridCard {idx0} {idx1} ...
```

- `idx0`, `idx1`, ... — 0-based indices into the screen's `_cards` list

**Example:**
```
SelectGridCard 5
SelectGridCard 3 7
```

**Legacy formats** (still parsed):
```
SelectDeckCard {idx...}
RemoveCardFromDeck: {idx...}
SelectSimpleCard {idx}
UpgradeCard {idx}
```

## SelectHandCards

Select one or more cards from the player's hand (e.g. for discard effects like Touch of Insanity).

```
SelectHandCards {idx0} {idx1} ...
SelectHandCards
```

- `idx0`, `idx1`, ... — 0-based hand position indices
- Empty (no indices) — no cards selected

**Example:**
```
SelectHandCards 0 2
SelectHandCards 1
SelectHandCards
```

## SelectCardFromScreen

Select a card from a choose-a-card screen (e.g. Power Potion, Attack Potion).

```
SelectCardFromScreen {index}
```

- `index` — 0-based index of the card in the selection screen
- `-1` — skip (no card selected)

**Example:**
```
SelectCardFromScreen 0
SelectCardFromScreen 2
SelectCardFromScreen -1
```

## OpenShop

Open the merchant shop inventory.

```
OpenShop
```

No parameters.

## OpenFakeShop

Open the fake merchant event shop inventory.

```
OpenFakeShop
```

No parameters.

## BuyCard

Buy a card from the shop.

```
BuyCard {title}
```

- `title` — the card's title

**Example:**
```
BuyCard Shrug It Off
BuyCard Inflame
```

## BuyRelic

Buy a relic from the shop.

```
BuyRelic {title}
```

- `title` — the relic's title

**Example:**
```
BuyRelic Vajra
BuyRelic Brimstone
```

## BuyPotion

Buy a potion from the shop.

```
BuyPotion {title}
```

- `title` — the potion's title

**Example:**
```
BuyPotion Fire Potion
BuyPotion Strength Potion
```

## BuyCardRemoval

Buy card removal from the shop. Opens the deck selection screen — a `SelectGridCard` command follows.

```
BuyCardRemoval
```

No parameters.
