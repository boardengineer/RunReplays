using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RunReplays;

/// <summary>
/// Raw command queue for an active replay.
/// Exposes typed Peek helpers for each command kind; consumption and logging
/// are handled by ReplayRunner, which calls these methods and proxies results.
/// </summary>
public static class ReplayEngine
{
    private static readonly Queue<string> _pending = new();

    // ── Overlay context ───────────────────────────────────────────────────────

    /// <summary>Fired on the calling thread each time a command is consumed.</summary>
    internal static event Action? ContextChanged;

    private static readonly List<string> _recentConsumed = new(2);

    /// <summary>
    /// Dequeues one command, records it in the recent-history buffer, and
    /// fires ContextChanged so the overlay can refresh.
    /// When the queue empties naturally (all commands consumed), also fires
    /// ReplayCompleted so callers can restore the action buffer.
    /// </summary>
    private static string SignalConsumed(string cmd)
    {
        if (_recentConsumed.Count >= 2)
            _recentConsumed.RemoveAt(0);
        _recentConsumed.Add(cmd);
        ContextChanged?.Invoke();
        if (_replayActive && _pending.Count == 0)
        {
            // Defer the replay→record transition to the next Godot frame.
            // This keeps IsActive = true long enough for the last replayed action's
            // AfterActionExecuted to fire (and be suppressed), preventing it from
            // being double-recorded into the new log alongside the restored buffer.
            var commands = _loadedCommands;
            Callable.From(() =>
            {
                // If Clear() was called before this fires, _replayActive is already
                // false — bail out so ReplayCompleted is not fired spuriously.
                if (!_replayActive) return;
                _replayActive = false;
                ReplayCompleted?.Invoke(commands);
            }).CallDeferred();
        }
        return cmd;
    }

    // ── Replay-completed notification ─────────────────────────────────────────

    /// <summary>
    /// Fired when the last queued command is consumed (i.e. the replay finishes
    /// naturally). Carries the full list of commands that were loaded so
    /// subscribers can restore the action buffer for the record phase.
    /// Not fired when Clear() is called explicitly.
    /// </summary>
    internal static event Action<IReadOnlyList<string>>? ReplayCompleted;

    // True only while commands loaded by Load() are still pending.
    // Set false by Clear() so ReplayCompleted is not fired on explicit cancels.
    private static bool _replayActive;
    private static List<string> _loadedCommands = new();

    /// <summary>
    /// Returns up to 2 recently consumed commands (prev), the current front of
    /// the queue (current), and up to 2 commands ahead of it (next).
    /// </summary>
    internal static void GetReplayContext(
        out IReadOnlyList<string> prev,
        out string? current,
        out IReadOnlyList<string> next)
    {
        prev = _recentConsumed;

        string[] arr = _pending.ToArray();
        current = arr.Length > 0 ? arr[0] : null;

        var nextList = new List<string>(2);
        for (int i = 1; i < Math.Min(arr.Length, 3); i++)
            nextList.Add(arr[i]);
        next = nextList;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True while commands are queued OR while the last command was just consumed
    /// but its AfterActionExecuted callback may not have fired yet.
    /// The _replayActive flag is cleared one Godot frame after the queue empties
    /// so that the last replayed action is still suppressed from recording.
    /// </summary>
    public static bool IsActive => _pending.Count > 0 || _replayActive;

    public static void Load(IReadOnlyList<string> commands)
    {
        _pending.Clear();
        _recentConsumed.Clear();
        _loadedCommands = commands.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        _replayActive   = _loadedCommands.Count > 0;
        foreach (string cmd in _loadedCommands)
            _pending.Enqueue(cmd);
    }

    public static void Clear()
    {
        _pending.Clear();
        _recentConsumed.Clear();
        _replayActive = false;
    }

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out string? cmd) => _pending.TryPeek(out cmd);

    // ── Starting bonus ────────────────────────────────────────────────────────

    private const string StartingBonusPrefix = "ChooseStartingBonus ";

    public static bool PeekStartingBonus(out int choiceIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(StartingBonusPrefix)
            && int.TryParse(cmd.AsSpan(StartingBonusPrefix.Length), out choiceIndex))
            return true;

        choiceIndex = -1;
        return false;
    }

    public static bool ConsumeStartingBonus(out int choiceIndex)
    {
        if (PeekStartingBonus(out choiceIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Map node choices ──────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via MoveToMapCoordAction.ToString():
    //   "MoveToMapCoordAction {playerId} MapCoord ({col}, {row})"

    private const string MapNodePrefix   = "MoveToMapCoordAction ";
    private const string MapCoordMarker  = "MapCoord (";

    public static bool PeekMapNode(out int col, out int row)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(MapNodePrefix))
        {
            int markerIdx = cmd.IndexOf(MapCoordMarker, StringComparison.Ordinal);
            if (markerIdx >= 0)
            {
                ReadOnlySpan<char> coords = cmd.AsSpan(markerIdx + MapCoordMarker.Length);
                // coords = "{col}, {row})"
                int comma = coords.IndexOf(',');
                int close = coords.IndexOf(')');
                if (comma > 0 && close > comma
                    && int.TryParse(coords[..comma].Trim(), out col)
                    && int.TryParse(coords[(comma + 1)..close].Trim(), out row))
                    return true;
            }
        }
        col = -1;
        row = -1;
        return false;
    }

    public static bool ConsumeMapNode(out int col, out int row)
    {
        if (PeekMapNode(out col, out row))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── End player turn ───────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via EndPlayerTurnAction.ToString():
    //   "EndPlayerTurnAction for player {playerId} round {combatRound}"

    private const string EndTurnPrefix = "EndPlayerTurnAction ";

    public static bool PeekEndTurn()
    {
        return _pending.TryPeek(out string? cmd) && cmd.StartsWith(EndTurnPrefix);
    }

    public static bool ConsumeEndTurn()
    {
        if (PeekEndTurn())
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Potion discard ────────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via DiscardPotionGameAction.ToString():
    //   "NetDiscardPotionGameAction for player {netId} potion slot: {slotIndex}"

    private const string NetDiscardPotionPrefix     = "NetDiscardPotionGameAction for player ";
    private const string NetDiscardPotionSlotMarker = " potion slot: ";

    public static bool PeekNetDiscardPotion(out int slotIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(NetDiscardPotionPrefix))
        {
            int markerPos = cmd.LastIndexOf(NetDiscardPotionSlotMarker);
            if (markerPos >= 0 && int.TryParse(
                    cmd.AsSpan(markerPos + NetDiscardPotionSlotMarker.Length), out slotIndex))
                return true;
        }
        slotIndex = -1;
        return false;
    }

    public static bool ConsumeNetDiscardPotion(out int slotIndex)
    {
        if (PeekNetDiscardPotion(out slotIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Potion use ────────────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via UsePotionAction.ToString():
    //   "UsePotionAction {netId} {potionName} index: {potionIndex} target: {targetId} ({creatureName}) combat: {bool}"
    // targetId is empty when null; combat is True for in-combat use.

    private const string UsePotionPrefix       = "UsePotionAction ";
    private const string UsePotionIndexMarker  = " index: ";
    private const string UsePotionTargetMarker = " target: ";
    private const string UsePotionCombatMarker = " combat: ";

    public static bool PeekUsePotion(out uint potionIndex, out uint? targetId, out bool inCombat)
    {
        potionIndex = 0;
        targetId    = null;
        inCombat    = false;

        if (!_pending.TryPeek(out string? cmd) || !cmd.StartsWith(UsePotionPrefix))
            return false;

        int combatIdx = cmd.LastIndexOf(UsePotionCombatMarker, StringComparison.Ordinal);
        if (combatIdx < 0) return false;

        int targetIdx = cmd.LastIndexOf(UsePotionTargetMarker, combatIdx, StringComparison.Ordinal);
        if (targetIdx < 0) return false;

        int indexIdx = cmd.LastIndexOf(UsePotionIndexMarker, targetIdx, StringComparison.Ordinal);
        if (indexIdx < 0) return false;

        ReadOnlySpan<char> indexSpan = cmd.AsSpan(
            indexIdx + UsePotionIndexMarker.Length,
            targetIdx - indexIdx - UsePotionIndexMarker.Length).Trim();
        if (!uint.TryParse(indexSpan, out potionIndex)) return false;

        // targetId sits between " target: " and the " (" that precedes the creature name.
        int openParenIdx = cmd.IndexOf(" (", targetIdx + UsePotionTargetMarker.Length, StringComparison.Ordinal);
        if (openParenIdx < 0) return false;

        ReadOnlySpan<char> targetSpan = cmd.AsSpan(
            targetIdx + UsePotionTargetMarker.Length,
            openParenIdx - targetIdx - UsePotionTargetMarker.Length).Trim();
        if (targetSpan.Length > 0 && uint.TryParse(targetSpan, out uint tid))
            targetId = tid;

        ReadOnlySpan<char> combatSpan = cmd.AsSpan(combatIdx + UsePotionCombatMarker.Length).Trim();
        inCombat = combatSpan.Equals("True", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    public static bool ConsumeUsePotion(out uint potionIndex, out uint? targetId, out bool inCombat)
    {
        if (PeekUsePotion(out potionIndex, out targetId, out inCombat))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Card plays ────────────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via PlayCardAction.ToString():
    //   "PlayCardAction card: {CardModel} index: {CombatCardIndex} targetid: {TargetId}"
    // TargetId prints as empty string when null.

    private const string CardPlayPrefix       = "PlayCardAction ";
    private const string CardPlayIndexMarker  = " index: ";
    private const string CardPlayTargetMarker = " targetid: ";

    public static bool PeekCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        combatCardIndex = 0;
        targetId = null;

        if (!_pending.TryPeek(out string? cmd) || !cmd.StartsWith(CardPlayPrefix))
            return false;

        // Parse from the right so card display names containing spaces are safe.
        int targetIdx = cmd.LastIndexOf(CardPlayTargetMarker, StringComparison.Ordinal);
        int indexIdx  = cmd.LastIndexOf(CardPlayIndexMarker,  StringComparison.Ordinal);

        if (targetIdx < 0 || indexIdx < 0 || indexIdx >= targetIdx)
            return false;

        ReadOnlySpan<char> indexStr = cmd.AsSpan(
            indexIdx + CardPlayIndexMarker.Length,
            targetIdx - indexIdx - CardPlayIndexMarker.Length).Trim();

        if (!uint.TryParse(indexStr, out combatCardIndex))
            return false;

        ReadOnlySpan<char> targetStr = cmd.AsSpan(targetIdx + CardPlayTargetMarker.Length).Trim();
        if (targetStr.Length > 0 && uint.TryParse(targetStr, out uint tid))
            targetId = tid;

        return true;
    }

    public static bool ConsumeCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        if (PeekCardPlay(out combatCardIndex, out targetId))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Card rewards ──────────────────────────────────────────────────────────

    private const string CardRewardPrefix = "TakeCardReward: ";

    public static bool PeekCardReward(out string cardTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(CardRewardPrefix))
        {
            cardTitle = cmd.Substring(CardRewardPrefix.Length);
            return true;
        }
        cardTitle = string.Empty;
        return false;
    }

    public static bool ConsumeCardReward(out string cardTitle)
    {
        if (PeekCardReward(out cardTitle))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Relic rewards ─────────────────────────────────────────────────────────

    private const string RelicRewardPrefix = "TakeRelicReward: ";

    public static bool PeekRelicReward(out string relicTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(RelicRewardPrefix))
        {
            relicTitle = cmd.Substring(RelicRewardPrefix.Length);
            return true;
        }
        relicTitle = string.Empty;
        return false;
    }

    public static bool ConsumeRelicReward(out string relicTitle)
    {
        if (PeekRelicReward(out relicTitle))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Potion rewards ────────────────────────────────────────────────────────

    private const string PotionRewardPrefix = "TakePotionReward: ";

    public static bool PeekPotionReward(out string potionTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(PotionRewardPrefix))
        {
            potionTitle = cmd.Substring(PotionRewardPrefix.Length);
            return true;
        }
        potionTitle = string.Empty;
        return false;
    }

    public static bool ConsumePotionReward(out string potionTitle)
    {
        if (PeekPotionReward(out potionTitle))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Event option choices ──────────────────────────────────────────────────
    //
    // Recorded by EventOptionChosenLogPatch via EventOption.Chosen():
    //   "ChooseEventOption {textKey}"

    private const string EventOptionPrefix = "ChooseEventOption ";

    public static bool PeekEventOption(out string textKey)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(EventOptionPrefix))
        {
            textKey = cmd.Substring(EventOptionPrefix.Length);
            return true;
        }
        textKey = string.Empty;
        return false;
    }

    public static bool ConsumeEventOption(out string textKey)
    {
        if (PeekEventOption(out textKey))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Searches the queue for a ChooseEventOption command, drains any interleaved
    /// commands that precede it (they are orphaned — already processed by the game
    /// during ChooseLocalOption), then consumes the event option itself.
    /// Used for the finished-event PROCEED case so orphaned commands don't linger
    /// and block subsequent queue consumers (e.g. map navigation).
    /// Returns true if the event option was found and consumed.
    /// </summary>
    public static bool SkipToEventOption(out string textKey)
    {
        // Fast path: already at front.
        if (PeekEventOption(out textKey))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }

        // Verify a ChooseEventOption exists before draining anything.
        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(EventOptionPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            textKey = string.Empty;
            return false;
        }

        // Drain interleaved commands until ChooseEventOption is at the front.
        while (_pending.Count > 0 && !PeekEventOption(out _))
            SignalConsumed(_pending.Dequeue());

        if (!PeekEventOption(out textKey))
        {
            textKey = string.Empty;
            return false;
        }

        SignalConsumed(_pending.Dequeue());
        return true;
    }

    /// <summary>
    /// Returns true if any ChooseEventOption command exists anywhere in the queue,
    /// regardless of position.
    /// </summary>
    public static bool HasPendingEventOption()
    {
        foreach (string cmd in _pending)
            if (cmd.StartsWith(EventOptionPrefix))
                return true;
        return false;
    }

    // ── Card upgrades ─────────────────────────────────────────────────────────
    //
    // Recorded by NDeckUpgradeSelectScreenLogPatch:
    //   "UpgradeCard {deckIndex}"

    private const string UpgradeCardPrefix = "UpgradeCard ";

    public static bool PeekUpgradeCard(out int deckIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(UpgradeCardPrefix)
            && int.TryParse(cmd.AsSpan(UpgradeCardPrefix.Length), out deckIndex))
            return true;

        deckIndex = -1;
        return false;
    }

    public static bool ConsumeUpgradeCard(out int deckIndex)
    {
        if (PeekUpgradeCard(out deckIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Shop commands ─────────────────────────────────────────────────────────
    //
    // Recorded by ShopRecordPatch:
    //   "OpenShop"
    //   "BuyCard {title}"
    //   "BuyRelic {title}"
    //   "BuyPotion {title}"
    //   "BuyCardRemoval"

    private const string OpenShopCmd        = "OpenShop";
    private const string BuyCardPrefix      = "BuyCard ";
    private const string BuyRelicPrefix     = "BuyRelic ";
    private const string BuyPotionPrefix    = "BuyPotion ";
    private const string BuyCardRemovalCmd  = "BuyCardRemoval";

    public static bool PeekOpenShop()
        => _pending.TryPeek(out string? cmd) && cmd == OpenShopCmd;

    public static bool ConsumeOpenShop()
    {
        if (PeekOpenShop()) { SignalConsumed(_pending.Dequeue()); return true; }
        return false;
    }

    public static bool PeekBuyCard(out string cardTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(BuyCardPrefix))
        {
            cardTitle = cmd.Substring(BuyCardPrefix.Length);
            return true;
        }
        cardTitle = string.Empty;
        return false;
    }

    public static bool ConsumeBuyCard(out string cardTitle)
    {
        if (PeekBuyCard(out cardTitle)) { SignalConsumed(_pending.Dequeue()); return true; }
        return false;
    }

    public static bool PeekBuyRelic(out string relicTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(BuyRelicPrefix))
        {
            relicTitle = cmd.Substring(BuyRelicPrefix.Length);
            return true;
        }
        relicTitle = string.Empty;
        return false;
    }

    public static bool ConsumeBuyRelic(out string relicTitle)
    {
        if (PeekBuyRelic(out relicTitle)) { SignalConsumed(_pending.Dequeue()); return true; }
        return false;
    }

    public static bool PeekBuyPotion(out string potionTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(BuyPotionPrefix))
        {
            potionTitle = cmd.Substring(BuyPotionPrefix.Length);
            return true;
        }
        potionTitle = string.Empty;
        return false;
    }

    public static bool ConsumeBuyPotion(out string potionTitle)
    {
        if (PeekBuyPotion(out potionTitle)) { SignalConsumed(_pending.Dequeue()); return true; }
        return false;
    }

    public static bool PeekBuyCardRemoval()
        => _pending.TryPeek(out string? cmd) && cmd == BuyCardRemovalCmd;

    public static bool ConsumeBuyCardRemoval()
    {
        if (PeekBuyCardRemoval()) { SignalConsumed(_pending.Dequeue()); return true; }
        return false;
    }

    // ── Rest site option choices ──────────────────────────────────────────────
    //
    // Recorded by RestSiteRecordPatch via RestSiteSynchronizer.ChooseLocalOption:
    //   "ChooseRestSiteOption {optionId}"  (e.g. "HEAL", "SMITH")

    private const string RestSiteOptionPrefix = "ChooseRestSiteOption ";

    public static bool PeekRestSiteOption(out string optionId)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(RestSiteOptionPrefix))
        {
            optionId = cmd.Substring(RestSiteOptionPrefix.Length);
            return true;
        }
        optionId = string.Empty;
        return false;
    }

    public static bool ConsumeRestSiteOption(out string optionId)
    {
        if (PeekRestSiteOption(out optionId))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Treasure chest relic ──────────────────────────────────────────────────

    private const string TakeChestRelicPrefix = "TakeChestRelic ";

    public static bool PeekTakeChestRelic(out string relicTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(TakeChestRelicPrefix))
        {
            relicTitle = cmd.Substring(TakeChestRelicPrefix.Length);
            return true;
        }
        relicTitle = string.Empty;
        return false;
    }

    public static bool ConsumeTakeChestRelic(out string relicTitle)
    {
        if (PeekTakeChestRelic(out relicTitle))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // Format: "NetPickRelicAction for player {netId} index {relicIndex}"
    private const string NetPickRelicPrefix = "NetPickRelicAction for player ";
    private const string NetPickRelicIndexMarker = " index ";

    public static bool PeekNetPickRelicAction(out int relicIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(NetPickRelicPrefix))
        {
            int markerPos = cmd.LastIndexOf(NetPickRelicIndexMarker);
            if (markerPos >= 0 && int.TryParse(
                    cmd.AsSpan(markerPos + NetPickRelicIndexMarker.Length), out relicIndex))
                return true;
        }
        relicIndex = -1;
        return false;
    }

    public static bool ConsumeNetPickRelicAction(out int relicIndex)
    {
        if (PeekNetPickRelicAction(out relicIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Gold rewards ──────────────────────────────────────────────────────────

    private const string GoldRewardPrefix = "TakeGoldReward: ";

    public static bool PeekGoldReward(out int goldAmount)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(GoldRewardPrefix)
            && int.TryParse(cmd.AsSpan(GoldRewardPrefix.Length), out goldAmount))
            return true;

        goldAmount = 0;
        return false;
    }

    public static bool ConsumeGoldReward(out int goldAmount)
    {
        if (PeekGoldReward(out goldAmount))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Proceed to next act ───────────────────────────────────────────────────
    //
    // Recorded automatically by PlayerActionBuffer via VoteToMoveToNextActAction.
    // The game's ToString() override uses the wrong literal, so the recorded string is:
    //   "VoteForMapCoordAction {playerId}"
    //
    // Replay: consume this command and call ActChangeSynchronizer.SetLocalPlayerReady().

    private const string ProceedToNextActPrefix = "VoteForMapCoordAction ";

    public static bool PeekProceedToNextAct()
        => _pending.TryPeek(out string? cmd) && cmd.StartsWith(ProceedToNextActPrefix);

    public static bool ConsumeProceedToNextAct()
    {
        if (PeekProceedToNextAct())
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Card removal from deck ────────────────────────────────────────────────
    //
    // Recorded by DeckRemovalRecordPatch via CardPileCmd.RemoveFromDeck:
    //   "RemoveCardFromDeck: {deckIndex}"
    // deckIndex is the 0-based position in the card list shown to the player.

    private const string RemoveCardFromDeckPrefix = "RemoveCardFromDeck: ";

    public static bool PeekRemoveCardFromDeck(out int deckIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(RemoveCardFromDeckPrefix)
            && int.TryParse(cmd.AsSpan(RemoveCardFromDeckPrefix.Length), out deckIndex))
            return true;

        deckIndex = -1;
        return false;
    }

    public static bool ConsumeRemoveCardFromDeck(out int deckIndex)
    {
        if (PeekRemoveCardFromDeck(out deckIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Scans the pending queue for a RemoveCardFromDeck command and consumes
    /// any interleaved game-action commands that precede it.  During shop card
    /// removal, the game may fire recordable actions (e.g. gold changes) between
    /// BuyCardRemoval and RemoveCardFromDeck; these are replayed automatically
    /// by the purchase itself and must be drained so the selector finds the
    /// removal command at the front of the queue.
    /// Returns true if RemoveCardFromDeck is now at the front.
    /// </summary>
    public static bool SkipToRemoveCardFromDeck()
    {
        // First check if it's already at the front.
        if (PeekRemoveCardFromDeck(out _))
            return true;

        // Scan ahead to verify RemoveCardFromDeck exists before consuming anything.
        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(RemoveCardFromDeckPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        // Consume interleaved commands until RemoveCardFromDeck is at the front.
        while (_pending.Count > 0 && !PeekRemoveCardFromDeck(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekRemoveCardFromDeck(out _);
    }

    // ── Deck card selections (Wood Carvings and similar events) ──────────────
    //
    // Recorded by WoodCarvingsCardSelectPatch via NCardGridSelectionScreen.CardsSelected
    // when FromDeckGeneric or FromDeckForEnchantment is active:
    //   "SelectDeckCard {deckIndex}"
    // deckIndex is the 0-based position in the card list shown to the player.

    private const string SelectDeckCardPrefix = "SelectDeckCard ";

    public static bool PeekSelectDeckCard(out int deckIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(SelectDeckCardPrefix)
            && int.TryParse(cmd.AsSpan(SelectDeckCardPrefix.Length), out deckIndex))
            return true;

        deckIndex = -1;
        return false;
    }

    public static bool ConsumeSelectDeckCard(out int deckIndex)
    {
        if (PeekSelectDeckCard(out deckIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drains any interleaved game-action commands that precede the next
    /// SelectDeckCard command, bringing it to the front of the queue.
    /// Used for combat card effects (e.g. Seeker Strike) where auto-processed
    /// actions may sit between PlayCardAction and SelectDeckCard.
    /// Returns true if SelectDeckCard is now at the front.
    /// </summary>
    public static bool SkipToSelectDeckCard()
    {
        if (PeekSelectDeckCard(out _))
            return true;

        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(SelectDeckCardPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        while (_pending.Count > 0 && !PeekSelectDeckCard(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekSelectDeckCard(out _);
    }

    // ── Simple-grid card selections (FromSimpleGrid — e.g. Seeker Strike) ───────
    //
    // Recorded by SimpleGridCardSelectPatch via PlayerChoiceSynchronizer.SyncLocalChoice
    // when FromSimpleGrid is active (e.g. Seeker Strike draw-pile selection):
    //   "SelectSimpleCard {index}"
    // index is the 0-based position in the list of cards shown to the player.

    private const string SelectSimpleCardPrefix = "SelectSimpleCard ";

    public static bool PeekSelectSimpleCard(out int index)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(SelectSimpleCardPrefix)
            && int.TryParse(cmd.AsSpan(SelectSimpleCardPrefix.Length), out index))
            return true;

        index = -1;
        return false;
    }

    public static bool ConsumeSelectSimpleCard(out int index)
    {
        if (PeekSelectSimpleCard(out index))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drains any interleaved game-action commands that precede the next
    /// SelectSimpleCard command, bringing it to the front of the queue.
    /// Used for combat card effects (e.g. Seeker Strike) where auto-processed
    /// actions may sit between PlayCardAction and SelectSimpleCard.
    /// Returns true if SelectSimpleCard is now at the front.
    /// </summary>
    public static bool SkipToSelectSimpleCard()
    {
        if (PeekSelectSimpleCard(out _))
            return true;

        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(SelectSimpleCardPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        while (_pending.Count > 0 && !PeekSelectSimpleCard(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekSelectSimpleCard(out _);
    }

    // ── Card choice screen selections ─────────────────────────────────────────
    //
    // Recorded by CardChoiceScreenPatch via PlayerChoiceSynchronizer.SyncLocalChoice
    // when FromChooseACardScreen is active (e.g. Skill Potion, Power Potion):
    //   "SelectCardFromScreen {index}"
    // index is the 0-based position in the offered card list; -1 means skipped.

    private const string SelectCardFromScreenPrefix = "SelectCardFromScreen ";

    public static bool PeekSelectCardFromScreen(out int index)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(SelectCardFromScreenPrefix)
            && int.TryParse(cmd.AsSpan(SelectCardFromScreenPrefix.Length), out index))
            return true;

        index = -1;
        return false;
    }

    public static bool ConsumeSelectCardFromScreen(out int index)
    {
        if (PeekSelectCardFromScreen(out index))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Hand card selections ───────────────────────────────────────────────────
    //
    // Recorded by HandCardSelectPatch via PlayerChoiceSynchronizer.SyncLocalChoice
    // when a CombatCard choice is made (e.g. Touch of Insanity, other hand-targeting potions):
    //   "SelectHandCards {combatId1} {combatId2} ..."
    // IDs are space-separated NetCombatCardDb combat indices. Empty when no cards chosen.

    private const string SelectHandCardsPrefix = "SelectHandCards ";

    public static bool PeekSelectHandCards(out uint[] cardIds)
    {
        if (!_pending.TryPeek(out string? cmd) || !cmd.StartsWith(SelectHandCardsPrefix))
        {
            cardIds = Array.Empty<uint>();
            return false;
        }

        string idsPart = cmd.Substring(SelectHandCardsPrefix.Length).Trim();
        if (idsPart.Length == 0)
        {
            cardIds = Array.Empty<uint>();
        }
        else
        {
            cardIds = idsPart.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => uint.TryParse(s, out uint id) ? (uint?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToArray();
        }
        return true;
    }

    public static bool ConsumeSelectHandCards(out uint[] cardIds)
    {
        if (PeekSelectHandCards(out cardIds))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Scans the pending queue for a SelectHandCards command and consumes
    /// any interleaved game-action commands that precede it.  During hand-card
    /// selection (e.g. Touch of Insanity), the game may fire recordable actions
    /// between the triggering action and SelectHandCards; these are replayed
    /// automatically and must be drained so the selector finds the command
    /// at the front of the queue.
    /// Returns true if SelectHandCards is now at the front.
    /// </summary>
    public static bool SkipToSelectHandCards()
    {
        if (PeekSelectHandCards(out _))
            return true;

        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(SelectHandCardsPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        while (_pending.Count > 0 && !PeekSelectHandCards(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekSelectHandCards(out _);
    }
}
