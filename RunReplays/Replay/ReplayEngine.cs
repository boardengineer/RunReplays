using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private static string SignalConsumed(string cmd, [CallerMemberName] string? caller = null)
    {
        PlayerActionBuffer.LogDispatcher($"[Queue] {caller}: {cmd[..Math.Min(cmd.Length, 60)]} ({_pending.Count} remaining)");

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
                ReplayDispatcher.RestoreGameSpeed();
                ReplayCompleted?.Invoke(commands);
            }).CallDeferred();
        }

        // Mark the previous dispatch as complete and re-trigger for the next command.
        ReplayDispatcher.NotifyConsumed();
        ReplayDispatcher.TryDispatch();

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

    /// <summary>
    /// The seed of the run being replayed or loaded from a save.
    /// Set by RunReplayMenu before starting the run.  Used by
    /// GetRandomListUnlockPatch to override the seed deterministically.
    /// Null when not using the replay menu (falls back to ForcedSeedPatch).
    /// </summary>
    public static string? ActiveSeed { get; set; }

    /// <summary>State suffix separator embedded in minimal log entries.</summary>
    private const string StateSeparator = " || ";

    public static void Load(IReadOnlyList<string> commands)
    {
        SelectorStackDebug.Clear();
        SelectorStackDebug.Log("=== Replay Load ===");
        Utils.RngCheckpointLogger.Clear();
        Utils.RngCheckpointLogger.Log("=== Replay Load ===");
        _pending.Clear();
        _recentConsumed.Clear();

        _loadedCommands = new List<string>();
        foreach (string raw in commands)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Skip header comment lines (e.g. "# Character: ...", "# Seed: ...").
            if (raw.StartsWith('#'))
                continue;

            // Strip the " || state" suffix so the command text parses cleanly.
            int sepIdx = raw.IndexOf(StateSeparator, StringComparison.Ordinal);
            string cmd = sepIdx >= 0 ? raw[..sepIdx] : raw;
            _loadedCommands.Add(cmd);
            _pending.Enqueue(cmd);
        }

        _replayActive = _loadedCommands.Count > 0;
        if (_replayActive)
        {
            ReplayDispatcher.ApplyGameSpeed();
            ReplayDispatcher.StartWatchdog();
        }
    }

    public static void Clear()
    {
        _pending.Clear();
        _recentConsumed.Clear();
        _replayActive = false;
        ReplayDispatcher.RestoreGameSpeed();
        ResetAllPatchState();
    }

    /// <summary>
    /// Resets all static state across recording and replay patches so that
    /// both paths can start cleanly after a stall or cancellation.
    /// </summary>
    public static void ResetAllPatchState()
    {
        // Recording state
        BattleRewardPatch.LastCardRewardIndex = -1;
        BattleRewardPatch.IsProcessingCardReward = false;
        DeckRemovalState.PendingRemoval = false;
        DeckRemovalState.PendingOptions = null;
        DeckRemovalState.SuppressRecording = false;
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingLabel = null;
        EventSelectionPatch.PendingIndex = null;
        DeckCardSelectContext.Pending = false;
        SimpleGridContext.Pending = false;
        HandCardSelectRecordPatch.SuppressNext = false;

        // Replay selector scopes
        FromChooseACardScreenPatch._pendingScope?.Dispose();
        FromChooseACardScreenPatch._pendingScope = null;
        FromDeckGenericPatch._pendingScope?.Dispose();
        FromDeckGenericPatch._pendingScope = null;
        FromDeckForEnchantmentPatch._pendingScope?.Dispose();
        FromDeckForEnchantmentPatch._pendingScope = null;
        FromDeckForEnchantmentWithFilterPatch._pendingScope?.Dispose();
        FromDeckForEnchantmentWithFilterPatch._pendingScope = null;
        FromDeckForEnchantmentWithCardsPatch._pendingScope?.Dispose();
        FromDeckForEnchantmentWithCardsPatch._pendingScope = null;
        FromDeckForTransformationPatch._pendingScope?.Dispose();
        FromDeckForTransformationPatch._pendingScope = null;
        FromDeckForUpgradePatch._pendingScope?.Dispose();
        FromDeckForUpgradePatch._pendingScope = null;
        HandCardSelectReplayPatch._pendingScope?.Dispose();
        HandCardSelectReplayPatch._pendingScope = null;
        FromSimpleGridPatch._pendingScope?.Dispose();
        FromSimpleGridPatch._pendingScope = null;

        // Crystal sphere
        CrystalSphereReplayPatch.PendingTool = null;

        // Buffered recording contexts
        CardChoiceScreenSyncPatch.FlushIfPending();
        CardEffectDeckSelectContext.FlushIfPending();
        SimpleGridSyncPatch.FlushIfPending();

        // Dispatcher
        ReplayDispatcher.Reset();
    }

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out string? cmd) => _pending.TryPeek(out cmd);

    /// <summary>
    /// Returns the command at the given offset from the front (0 = front, 1 = second, etc.)
    /// without consuming anything.  Returns null if the offset is out of range.
    /// </summary>
    public static string? PeekAhead(int offset)
    {
        int i = 0;
        foreach (string cmd in _pending)
        {
            if (i == offset)
                return cmd;
            i++;
        }
        return null;
    }

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
            if (markerPos >= 0)
            {
                // Extract just the slot number — stop at the next space
                // (the format may include " in combat: {bool}" after the slot).
                var afterMarker = cmd.AsSpan(markerPos + NetDiscardPotionSlotMarker.Length);
                int spaceIdx = afterMarker.IndexOf(' ');
                var slotSpan = spaceIdx >= 0 ? afterMarker[..spaceIdx] : afterMarker;
                if (int.TryParse(slotSpan, out slotIndex))
                    return true;
            }
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
    private const string CardRewardIndexedPrefix = "TakeCardReward[";

    public static bool PeekCardReward(out string cardTitle, out int rewardIndex)
    {
        if (_pending.TryPeek(out string? cmd))
        {
            // Indexed format: TakeCardReward[N]: CardTitle
            if (cmd.StartsWith(CardRewardIndexedPrefix))
            {
                int closeBracket = cmd.IndexOf("]: ", CardRewardIndexedPrefix.Length, StringComparison.Ordinal);
                if (closeBracket > CardRewardIndexedPrefix.Length &&
                    int.TryParse(cmd.AsSpan(CardRewardIndexedPrefix.Length, closeBracket - CardRewardIndexedPrefix.Length), out rewardIndex))
                {
                    cardTitle = cmd.Substring(closeBracket + "]: ".Length);
                    return true;
                }
            }

            // Legacy format: TakeCardReward: CardTitle
            if (cmd.StartsWith(CardRewardPrefix))
            {
                cardTitle = cmd.Substring(CardRewardPrefix.Length);
                rewardIndex = -1;
                return true;
            }
        }
        cardTitle = string.Empty;
        rewardIndex = -1;
        return false;
    }

    public static bool ConsumeCardReward(out string cardTitle, out int rewardIndex)
    {
        if (PeekCardReward(out cardTitle, out rewardIndex))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Sacrifice card reward (Pael's Wing) ─────────────────────────────────

    private const string SacrificeCardRewardCmd = "SacrificeCardReward";
    private const string SacrificeCardRewardIndexedPrefix = "SacrificeCardReward[";

    public static bool PeekSacrificeCardReward() =>
        PeekSacrificeCardReward(out _);

    public static bool PeekSacrificeCardReward(out int rewardIndex)
    {
        if (_pending.TryPeek(out string? cmd))
        {
            // Indexed format: SacrificeCardReward[N]
            if (cmd.StartsWith(SacrificeCardRewardIndexedPrefix))
            {
                int closeBracket = cmd.IndexOf(']', SacrificeCardRewardIndexedPrefix.Length);
                if (closeBracket > SacrificeCardRewardIndexedPrefix.Length &&
                    int.TryParse(cmd.AsSpan(SacrificeCardRewardIndexedPrefix.Length, closeBracket - SacrificeCardRewardIndexedPrefix.Length), out rewardIndex))
                    return true;
            }

            // Legacy format: SacrificeCardReward (no index)
            if (cmd == SacrificeCardRewardCmd)
            {
                rewardIndex = -1;
                return true;
            }
        }
        rewardIndex = -1;
        return false;
    }

    public static bool ConsumeSacrificeCardReward(out int rewardIndex)
    {
        if (PeekSacrificeCardReward(out rewardIndex))
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
        return PeekEventOption(out textKey, out _);
    }

    public static bool PeekEventOption(out string textKey, out int optionIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(EventOptionPrefix))
        {
            string rest = cmd.Substring(EventOptionPrefix.Length);

            // New format: "ChooseEventOption {index} {textKey}"
            int space = rest.IndexOf(' ');
            if (space >= 0 && int.TryParse(rest.AsSpan(0, space), out int idx))
            {
                optionIndex = idx;
                textKey = rest.Substring(space + 1);
            }
            else
            {
                // Legacy format: "ChooseEventOption {textKey}"
                optionIndex = -1;
                textKey = rest;
            }
            return true;
        }
        textKey = string.Empty;
        optionIndex = -1;
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
    private const string OpenFakeShopCmd   = "OpenFakeShop";
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

    public static bool PeekOpenFakeShop()
        => _pending.TryPeek(out string? cmd) && cmd == OpenFakeShopCmd;

    public static bool ConsumeOpenFakeShop()
    {
        if (PeekOpenFakeShop()) { SignalConsumed(_pending.Dequeue()); return true; }
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
    //   "SelectDeckCard {idx0} {idx1} ..."
    // Each index is the 0-based position in the card list shown to the player.
    // Single-card selections produce "SelectDeckCard {idx}"; multi-card selections
    // (e.g. Morphic Grove) produce "SelectDeckCard {idx0} {idx1}".

    private const string SelectDeckCardPrefix = "SelectDeckCard ";

    public static bool PeekSelectDeckCard(out int[] deckIndices)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(SelectDeckCardPrefix))
        {
            var parts = cmd.Substring(SelectDeckCardPrefix.Length).Split(' ');
            var indices = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx))
                    indices.Add(idx);
                else
                {
                    deckIndices = Array.Empty<int>();
                    return false;
                }
            }
            deckIndices = indices.ToArray();
            return deckIndices.Length > 0;
        }

        deckIndices = Array.Empty<int>();
        return false;
    }

    public static bool ConsumeSelectDeckCard(out int[] deckIndices)
    {
        if (PeekSelectDeckCard(out deckIndices))
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
    // when FromChooseACardScreen is active (e.g. Skill Potion, Power Potion,
    // relic-triggered card choices like Lead Paperweight):
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

    /// <summary>
    /// Drains any interleaved commands that precede the next SelectCardFromScreen
    /// command, bringing it to the front of the queue.  Used for relic-triggered
    /// card choices (e.g. Lead Paperweight) where auto-processed actions may sit
    /// between the relic reward and the card selection.
    /// Returns true if SelectCardFromScreen is now at the front.
    /// </summary>
    public static bool SkipToSelectCardFromScreen()
    {
        if (PeekSelectCardFromScreen(out _))
            return true;

        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(SelectCardFromScreenPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        while (_pending.Count > 0 && !PeekSelectCardFromScreen(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekSelectCardFromScreen(out _);
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

    /// <summary>
    /// Like SkipToSelectHandCards but refuses to drain past combat-significant
    /// commands (card plays, end turns, potions, map moves, events, etc.).
    /// Returns false when no SelectHandCards is reachable without crossing a
    /// significant command — this is the expected outcome when the hand was
    /// empty and no SelectHandCards was recorded (e.g. Brand as last card).
    /// </summary>
    public static bool SafeSkipToSelectHandCards()
    {
        if (PeekSelectHandCards(out _))
            return true;

        // Scan forward but stop at any command that represents a distinct
        // player action — those must not be drained.
        bool foundBeforeBarrier = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(SelectHandCardsPrefix))
            {
                foundBeforeBarrier = true;
                break;
            }

            if (IsCombatSignificant(cmd))
                break;
        }

        if (!foundBeforeBarrier)
            return false;

        while (_pending.Count > 0 && !PeekSelectHandCards(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekSelectHandCards(out _);
    }

    private static bool IsCombatSignificant(string cmd)
    {
        return cmd.StartsWith(CardPlayPrefix)
            || cmd.StartsWith(EndTurnPrefix)
            || cmd.StartsWith(UsePotionPrefix)
            || cmd.StartsWith(NetDiscardPotionPrefix)
            || cmd.StartsWith(MapNodePrefix)
            || cmd.StartsWith(EventOptionPrefix)
            || cmd.StartsWith(RestSiteOptionPrefix)
            || cmd.StartsWith(ProceedToNextActPrefix);
    }

    // ── Crystal Sphere minigame clicks ──────────────────────────────────────
    //
    // Recorded by CrystalSphereCellClickedPatch via CrystalSphereMinigame.CellClicked:
    //   "CrystalSphereClick {x} {y} {tool}"
    // x/y are cell grid coordinates; tool is the CrystalSphereToolType int value
    // (0 = None, 1 = Small, 2 = Big).

    private const string CrystalSphereClickPrefix = "CrystalSphereClick ";

    public static bool PeekCrystalSphereClick(out int x, out int y, out int tool)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(CrystalSphereClickPrefix))
        {
            ReadOnlySpan<char> rest = cmd.AsSpan(CrystalSphereClickPrefix.Length);
            // Format: "{x} {y} {tool}"
            int sp1 = rest.IndexOf(' ');
            if (sp1 > 0)
            {
                int sp2 = rest[(sp1 + 1)..].IndexOf(' ');
                if (sp2 > 0)
                {
                    sp2 += sp1 + 1; // absolute index
                    if (int.TryParse(rest[..sp1], out x)
                        && int.TryParse(rest[(sp1 + 1)..sp2], out y)
                        && int.TryParse(rest[(sp2 + 1)..], out tool))
                        return true;
                }
            }
        }

        x = y = tool = 0;
        return false;
    }

    public static bool ConsumeCrystalSphereClick(out int x, out int y, out int tool)
    {
        if (PeekCrystalSphereClick(out x, out y, out tool))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }
}
