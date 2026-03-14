using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace RunReplays;

/// <summary>
/// Records hand-card selections (e.g. Touch of Insanity) into the action log
/// by patching PlayerChoiceSynchronizer.SyncLocalChoice — the single point
/// where every finalised local choice is announced.
///
/// Only CombatCard-type choices are captured (those created by CardSelectCmd.FromHand).
///
/// Recording is deferred: the command is buffered here and flushed by
/// PlayerActionBuffer immediately after the parent UsePotionAction is written,
/// so the replay log always reads: UsePotionAction → SelectHandCards.
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class HandCardSelectRecordPatch
{
    private static string? _pending;

    /// <summary>
    /// Set by HandCardSelectForDiscardRecordPatch when it records a
    /// FromHandForDiscard selection.  Prevents SyncLocalChoice from
    /// buffering a duplicate for the same selection.
    /// </summary>
    internal static bool SuppressNext;

    /// <summary>Sets the pending command from an external recorder.</summary>
    internal static void SetPending(string command) => _pending = command;

    [HarmonyPostfix]
    public static void Postfix(Player player, uint choiceId, PlayerChoiceResult result)
    {
        if (ReplayEngine.IsActive)
            return;

        if (result.ChoiceType != PlayerChoiceType.CombatCard)
            return;

        if (SuppressNext)
        {
            SuppressNext = false;
            PlayerActionBuffer.LogToDevConsole("[HandCardSelectRecordPatch] Suppressed — already recorded by FromHandForDiscard.");
            return;
        }

        IEnumerable<CardModel> cards;
        try
        {
            cards = result.AsCombatCards();
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[HandCardSelectRecordPatch] AsCombatCards threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var ids = new List<uint>();
        foreach (CardModel card in cards)
        {
            if (NetCombatCardDb.Instance.TryGetCardId(card, out uint id))
                ids.Add(id);
        }

        if (ids.Count == 0)
            return;

        _pending = $"SelectHandCards {string.Join(" ", ids)}";
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectRecordPatch] Buffered: {_pending}");
    }

    /// <summary>
    /// Called by PlayerActionBuffer after a UsePotionAction is recorded.
    /// Flushes the pending SelectHandCards command so it follows the potion in the log.
    /// </summary>
    internal static void FlushIfPending()
    {
        if (_pending == null)
            return;

        string command = _pending;
        _pending = null;
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectRecordPatch] Flushing: {command}");
        PlayerActionBuffer.Record(command);
    }
}

// ── Replay ────────────────────────────────────────────────────────────────────

/// <summary>
/// Pushes a ReplayHandCardSelector onto the CardSelectCmd selector stack before
/// CardSelectCmd.FromHand runs, so the game uses replay data instead of showing
/// the hand-selection UI.
///
/// Any stale scope from a previous call that was never consumed is disposed at
/// the start of each prefix invocation.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
public static class HandCardSelectReplayPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        // Dispose any stale scope from a previous FromHand call that returned early.
        _pendingScope?.Dispose();
        _pendingScope = null;

        SelectorStackDebug.Log("FromHand.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (!ReplayEngine.IsActive)
            return;

        // Only push a selector if SelectHandCards is at or near the front of
        // the queue.  SkipToSelectHandCards drains everything before the command
        // and is dangerous when no SelectHandCards was recorded (e.g. Brand as
        // the last card in hand — empty hand auto-resolves without a choice).
        // In that case a distant SelectHandCards from a later turn would cause
        // the skip to consume card plays and end turns.
        if (!ReplayEngine.SafeSkipToSelectHandCards())
            return;

        var selector = new ReplayHandCardSelector();
        _pendingScope = CardSelectCmd.PushSelector(selector);
        SelectorStackDebug.Log("PUSH FromHand");
        PlayerActionBuffer.LogToDevConsole("[HandCardSelectReplayPatch] Pushed ReplayHandCardSelector for FromHand.");
    }
}

// ── FromHandForDiscard (e.g. Tools of the Trade) ────────────────────────────

/// <summary>
/// Records hand-card selections that go through CardSelectCmd.FromHandForDiscard
/// (e.g. Tools of the Trade's start-of-turn discard).  This path does NOT go
/// through PlayerChoiceSynchronizer.SyncLocalChoice, so HandCardSelectRecordPatch
/// never captures it.  We await the Task result and record the selected card IDs.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard))]
public static class HandCardSelectForDiscardRecordPatch
{
    [HarmonyPostfix]
    public static void Postfix(Task<IEnumerable<CardModel>> __result, AbstractModel source)
    {
        if (ReplayEngine.IsActive)
            return;

        if (__result == null)
            return;

        PlayerActionBuffer.LogToDevConsole(
            $"[HandCardSelectForDiscardRecord] FromHandForDiscard called — source='{source}' ({source?.GetType().Name ?? "null"})");

        // Suppress SyncLocalChoice BEFORE the async await — SyncLocalChoice
        // fires synchronously when the player picks a card, which is before
        // RecordAsync's continuation runs.
        HandCardSelectRecordPatch.SuppressNext = true;

        // Card-triggered discards (e.g. Survivor) should buffer so the
        // SelectHandCards appears after the PlayCardAction in the log.
        // Power/turn-start discards (e.g. Tools of the Trade) should record
        // immediately so they appear before the next card play.
        bool shouldBuffer = source is CardModel;
        MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(RecordAsync(__result, shouldBuffer));
    }

    private static async Task RecordAsync(Task<IEnumerable<CardModel>> task, bool shouldBuffer)
    {
        IEnumerable<CardModel> cards;
        try { cards = await task; }
        catch { return; }

        var ids = new List<uint>();
        foreach (CardModel card in cards)
        {
            if (NetCombatCardDb.Instance.TryGetCardId(card, out uint id))
                ids.Add(id);
        }

        if (ids.Count == 0)
            return;

        // Check if the discard was auto-resolved (no real choice).
        // When a card like Survivor is the last in hand, the discard has
        // only 0-1 options and the game auto-resolves without player input.
        // Recording it would leave a stale command in the replay queue.
        try
        {
            var combatState = MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.DebugOnlyGetState();
            if (combatState != null)
            {
                var player = combatState.Players.FirstOrDefault();
                var hand = player?.PlayerCombatState?.Hand?.Cards;
                if (hand != null && hand.Count <= ids.Count)
                {
                    PlayerActionBuffer.LogToDevConsole(
                        $"[HandCardSelectForDiscardRecord] Skipping — auto-resolved (hand={hand.Count}, discarded={ids.Count}).");
                    return;
                }
            }
        }
        catch { /* ignore — record anyway if we can't check */ }

        string command = $"SelectHandCards {string.Join(" ", ids)}";
        if (shouldBuffer)
        {
            // Card-triggered: buffer so it's flushed after the PlayCardAction.
            PlayerActionBuffer.LogToDevConsole($"[HandCardSelectForDiscardRecord] Buffered: {command}");
            HandCardSelectRecordPatch.SetPending(command);
        }
        else
        {
            // Power/turn-start triggered: record immediately.
            PlayerActionBuffer.LogToDevConsole($"[HandCardSelectForDiscardRecord] Recording: {command}");
            PlayerActionBuffer.Record(command);
        }
    }

}

/// <summary>
/// Pushes a ReplayHandCardSelector before CardSelectCmd.FromHandForDiscard
/// during replay, mirroring HandCardSelectReplayPatch for FromHand.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForDiscard))]
public static class HandCardSelectForDiscardReplayPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        HandCardSelectReplayPatch._pendingScope?.Dispose();
        HandCardSelectReplayPatch._pendingScope = null;

        SelectorStackDebug.Log("FromHandForDiscard.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (!ReplayEngine.IsActive)
            return;

        if (!ReplayEngine.SafeSkipToSelectHandCards())
            return;

        var selector = new ReplayHandCardSelector();
        HandCardSelectReplayPatch._pendingScope = CardSelectCmd.PushSelector(selector);
        SelectorStackDebug.Log("PUSH FromHandForDiscard");
        PlayerActionBuffer.LogToDevConsole("[HandCardSelectForDiscardReplayPatch] Pushed ReplayHandCardSelector for FromHandForDiscard.");
    }
}

// ── Selector ──────────────────────────────────────────────────────────────────

/// <summary>
/// ICardSelector implementation used during replays.  Consumes the pending
/// SelectHandCards command and returns the matching CardModel instances from
/// the options provided by the game.
/// </summary>
internal sealed class ReplayHandCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        // Consume and clear our own scope so it is not stale-disposed later.
        var scope = HandCardSelectReplayPatch._pendingScope;
        HandCardSelectReplayPatch._pendingScope = null;
        scope?.Dispose();

        if (!ReplayEngine.ConsumeSelectHandCards(out uint[] ids))
        {
            PlayerActionBuffer.LogToDevConsole(
                "[ReplayHandCardSelector] ConsumeSelectHandCards returned false; returning empty.");
            return Task.FromResult(Enumerable.Empty<CardModel>());
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[ReplayHandCardSelector] Matching {ids.Length} id(s) against options.");

        var optionsList = options.ToList();
        var matched = new List<CardModel>();
        foreach (uint id in ids)
        {
            if (NetCombatCardDb.Instance.TryGetCard(id, out CardModel? card)
                && card != null
                && optionsList.Contains(card))
            {
                matched.Add(card);
            }
        }

        if (matched.Count == 0 && minSelect > 0)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[ReplayHandCardSelector] No matches found; falling back to first available options.");
            matched.AddRange(optionsList.Take(minSelect));
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[ReplayHandCardSelector] Returning {matched.Count} card(s).");
        return Task.FromResult<IEnumerable<CardModel>>(matched);
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => null;
}
