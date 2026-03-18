# ReplayCommand Hierarchy — Migration Plan

## Base Abstraction

### IReplayCommand interface
- `ReadyState RequiredState { get; }` — readiness flag(s) needed before execution
- `bool IsSelectionCommand { get; }` — consumed by card selectors, not dispatcher
- `bool BlocksDuringCombatStartup { get; }` — blocked when combat in progress but Combat readiness not set
- `string Describe()` — human-readable description for logging
- `void Execute()` — API execution logic

### ReplayCommand abstract base class
- Holds `RawText` (original command string)
- Defaults: `IsSelectionCommand => false`, `BlocksDuringCombatStartup => false`

### ReplayCommandParser
- Static `Parse(string rawCmd)` method
- Each subclass provides `static TryParse(string raw)` factory method
- Parser tries prefixes in order, returns first match

## Command Subtypes (~25 total)

### Combat (RequiredState = Combat)
- `PlayCardCommand` — params: combatCardIndex, targetId
- `EndTurnCommand` — no params

### Potions (RequiredState = None, BlocksDuringCombatStartup = true)
- `UsePotionCommand` — params: potionIndex, targetId, inCombat
- `DiscardPotionCommand` — params: slotIndex

### Rewards (RequiredState = Rewards)
- `TakeGoldRewardCommand` — params: goldAmount
- `TakeCardRewardCommand` — params: cardTitle, rewardIndex
- `SacrificeCardRewardCommand` — params: rewardIndex (optional)
- `TakeRelicRewardCommand` — params: relicTitle
- `TakePotionRewardCommand` — params: potionTitle
- `ProceedToNextActCommand` — (VoteForMapCoordAction)

### Navigation (RequiredState = Map)
- `MapMoveCommand` — params: col, row

### Events (RequiredState = Event)
- `ChooseEventOptionCommand` — params: textKey, recordedIndex

### Rest Site (RequiredState = RestSite)
- `ChooseRestSiteOptionCommand` — params: optionId

### Starting Bonus (RequiredState = StartingBonus)
- `ChooseStartingBonusCommand` — params: choiceIndex

### Shop (RequiredState = Shop)
- `OpenShopCommand` — no params
- `OpenFakeShopCommand` — no params
- `BuyCardCommand` — params: cardTitle
- `BuyRelicCommand` — params: relicTitle
- `BuyPotionCommand` — params: potionTitle
- `BuyCardRemovalCommand` — no params

### Treasure (RequiredState = Treasure)
- `TakeChestRelicCommand` — params: relicTitle
- `NetPickRelicCommand` — params: relicIndex

### Minigame (RequiredState = CrystalSphere)
- `CrystalSphereClickCommand` — params: x, y, tool

### Selection (IsSelectionCommand = true, RequiredState = None)
- `SelectCardFromScreenCommand` — params: screenIndex
- `SelectDeckCardCommand` — params: deckIndex
- `SelectHandCardsCommand` — params: cardIds[]
- `SelectSimpleCardCommand`
- `RemoveCardFromDeckCommand` — params: deckIndex
- `UpgradeCardCommand` — params: deckIndex

## File Layout

```
Replay/Commands/
    IReplayCommand.cs
    ReplayCommand.cs
    ReplayCommandParser.cs
    MapMoveCommand.cs
    PlayCardCommand.cs
    EndTurnCommand.cs
    UsePotionCommand.cs
    DiscardPotionCommand.cs
    ... (flat, one file per type)
```

## Dispatcher Changes

### Before (current)
- `GetRequiredState(string cmd)` — big string-prefix switch
- `IsSelectionCommand(string cmd)` — string-prefix checks
- `ExecuteNext()` — switch on ReadyState, delegates to `*ReplayPatch.DispatchFromEngine()`

### After
- `cmd.RequiredState` replaces `GetRequiredState()`
- `cmd.IsSelectionCommand` replaces `IsSelectionCommand()`
- `cmd.Execute()` replaces the entire switch block
- Shop sub-routing (FakeMerchantReplayPatch.IsActive) moves into shop command Execute() methods

### What stays in dispatcher
- Readiness bitmask, pausing/stepping, delay timers
- In-flight guards (CardPlayInFlight, PotionInFlight, MapMoveInFlight, ActionInFlight)
- Watchdog, SignalReady/ClearReady

## Incremental Migration Strategy

### Phase 1: Scaffold + first command
- Create IReplayCommand, ReplayCommand base, ReplayCommandParser
- Implement MapMoveCommand as first migration (simple parsing, simple execution)
- Keep Queue<string> — parse lazily on demand
- Add TryParseAndExecute path in dispatcher: if command parses to IReplayCommand,
  call Execute(), otherwise fall through to existing switch logic
- Each command's Execute() delegates to existing *ReplayPatch.DispatchFromEngine()

### Phase 2: Migrate commands one at a time
- Each command is a single commit
- Suggested order (simple to complex):
  1. MapMoveCommand
  2. ChooseRestSiteOptionCommand
  3. ChooseStartingBonusCommand
  4. TakeChestRelicCommand / NetPickRelicCommand
  5. CrystalSphereClickCommand
  6. ChooseEventOptionCommand
  7. Reward commands (TakeGold, TakeCard, etc.)
  8. Shop commands (OpenShop, Buy*, etc.)
  9. Potion commands (UsePotion, DiscardPotion)
  10. Selection commands (SelectCardFromScreen, etc.)
  11. Combat commands (PlayCard, EndTurn) — most complex, last

### Phase 3: Queue migration
- Change ReplayEngine._pending from Queue<string> to Queue<IReplayCommand>
- Parse at Load() time instead of lazily
- Old Peek*/Consume* methods become compatibility shims or get deleted

### Phase 4: Cleanup
- Delete GetRequiredState(), IsSelectionCommand(), ExecuteNext switch
- Delete ReplayRunner (Execute*/Describe methods absorbed by commands)
- Remove compatibility shims from ReplayEngine

## Risks and Considerations

1. Shop command routing is stateful (FakeMerchantReplayPatch.IsActive check).
   Shop command Execute() methods must handle this.

2. CardPlayReplayPatch is ~950 lines with complex state machine.
   PlayCardCommand.Execute() should remain a thin delegation, not absorb the state machine.

3. Selection commands are consumed by ICardSelector patches, never by the dispatcher.
   Their Execute() should throw or be a no-op.

4. Harmony patches must remain static classes. Command objects delegate to them,
   they don't replace them.
