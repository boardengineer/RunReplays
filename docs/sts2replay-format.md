# `.sts2replay` File Format

## Overview

An `.sts2replay` file is a plain-text log of every player decision in a Slay the Spire 2 run. Each line is either a **header comment** or a **command**, optionally followed by a battle-state snapshot.

Files are saved to:
```
%APPDATA%/Godot/app_userdata/Slay the Spire 2/RunReplays/logs/{seed}/floor_{floor}/actions.sts2replay
```

## Header

The first lines starting with `#` are metadata. They are ignored during replay.

```
# Character: IRONCLAD
# Seed: ABC123
# Ascension: 5
# Game: 0.2.1
# Mod: 1.0.0
```

| Field | Description |
|-------|-------------|
| Character | Character ID (e.g. `IRONCLAD`, `SILENT`, `REGENT`) |
| Seed | Run seed string |
| Ascension | Ascension level (0-20) |
| Game | Game version at time of recording |
| Mod | RunReplays mod version |

## Commands

Each non-header line is a command. Some combat commands include an optional state suffix separated by ` || `:

```
CommandText
CommandText || Hand: [card1, card2] Enemies: [Monster 42/44]
```

The state suffix is stripped during replay and used only for debugging.

---

## Command Reference

### Combat

| Command | Format | Example |
|---------|--------|---------|
| Play card | `PlayCardAction card: {desc} index: {N} targetid: {T}` | `PlayCardAction card: CARD.BASH (12345) index: 2 targetid: 1` |
| End turn | `EndPlayerTurnAction for player {P} round {R}` | `EndPlayerTurnAction for player 1 round 3` |
| Use potion | `UsePotionAction {id} {name} index: {N} target: {T} ({creature}) combat: {bool}` | `UsePotionAction 1 Fire Potion index: 0 target: 1 (Jaw Worm) combat: True` |
| Discard potion | `NetDiscardPotionGameAction for player {P} ... potion slot: {N}` | |

### Rewards

| Command | Format | Example |
|---------|--------|---------|
| Take gold | `TakeGoldReward: {amount}` | `TakeGoldReward: 35` |
| Take card (indexed) | `TakeCardReward[{pack}]: {title}` | `TakeCardReward[0]: Bash` |
| Take card (legacy) | `TakeCardReward: {title}` | `TakeCardReward: Bash` |
| Sacrifice (indexed) | `SacrificeCardReward[{pack}]` | `SacrificeCardReward[0]` |
| Sacrifice (legacy) | `SacrificeCardReward` | `SacrificeCardReward` |
| Take relic | `TakeRelicReward: {title}` | `TakeRelicReward: Vajra` |
| Take potion | `TakePotionReward: {title}` | `TakePotionReward: Fire Potion` |
| Chest relic | `TakeChestRelic {title}` | `TakeChestRelic Runic Pyramid` |
| Pick chest relic | `NetPickRelicAction for player {P} ... {index}` | |

### Card Selections

| Command | Format | Example |
|---------|--------|---------|
| Choose from screen | `SelectCardFromScreen {index}` | `SelectCardFromScreen 2` |
| Choose from deck | `SelectDeckCard {idx0} {idx1} ...` | `SelectDeckCard 0 5` |
| Choose from hand | `SelectHandCards {id0} {id1} ...` | `SelectHandCards 3 7` |
| Choose from grid | `SelectSimpleCard {index}` | `SelectSimpleCard 1` |
| Remove from deck | `RemoveCardFromDeck: {deckIndex}` | `RemoveCardFromDeck: 3` |
| Upgrade card | `UpgradeCard {deckIndex}` | `UpgradeCard 14` |

### Navigation

| Command | Format | Example |
|---------|--------|---------|
| Map move | `MoveToMapCoordAction {P} MapCoord ({col}, {row})` | `MoveToMapCoordAction 1 MapCoord (2, 3)` |
| Next act | `VoteForMapCoordAction ...` | |
| Event option | `ChooseEventOption {textKey}` | `ChooseEventOption SAPPHIRE_SEED.pages.INITIAL.options.PLANT` |
| Rest site | `ChooseRestSiteOption {optionId}` | `ChooseRestSiteOption SMITH` |
| Starting bonus | `ChooseStartingBonus {index}` | `ChooseStartingBonus 0` |

### Shop

| Command | Format | Example |
|---------|--------|---------|
| Open shop | `OpenShop` | |
| Open fake shop | `OpenFakeShop` | |
| Buy card | `BuyCard {title}` | `BuyCard Inflame` |
| Buy relic | `BuyRelic {title}` | `BuyRelic Vajra` |
| Buy potion | `BuyPotion {title}` | `BuyPotion Fire Potion` |
| Buy card removal | `BuyCardRemoval` | |

### Minigames

| Command | Format | Example |
|---------|--------|---------|
| Crystal Sphere click | `CrystalSphereClick {x} {y} {tool}` | `CrystalSphereClick 3 2 1` |

---

## Notes

- **Indexed card rewards**: The `[N]` index identifies which card reward pack on the rewards screen was selected (0-based). Legacy logs without an index assume the first pack.
- **Multi-card selections**: `SelectDeckCard` supports multiple space-separated indices for events that select more than one card (e.g. Morphic Grove).
- **Skip**: `SelectCardFromScreen -1` means the player skipped the selection.
- **Empty hand**: `SelectHandCards` with no IDs means the hand was empty.
- **State suffix**: The ` || Hand: [...] Enemies: [...]` suffix is only present on combat actions and is not used during replay.
