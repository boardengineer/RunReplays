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
/// Commands are recorded immediately when the choice is made.
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class HandCardSelectRecordPatch
{
    /// <summary>
    /// Set by HandCardSelectForDiscardRecordPatch when it records a
    /// FromHandForDiscard selection.  Prevents SyncLocalChoice from
    /// recording a duplicate for the same selection.
    /// </summary>
    internal static bool SuppressNext;

    [HarmonyPostfix]
    public static void Postfix(Player player, uint choiceId, PlayerChoiceResult result)
    {
        PlayerActionBuffer.LogToDevConsole(
            $"[HandCardSelectRecordPatch] SyncLocalChoice fired — " +
            $"isReplay={ReplayEngine.IsActive} choiceType={result.ChoiceType} " +
            $"suppressNext={SuppressNext} choiceId={choiceId} player={player}");

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

        var hand = player.PlayerCombatState?.Hand?.Cards;
        if (hand == null)
            return;

        var indices = new List<int>();
        foreach (CardModel card in cards)
        {
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i] == card)
                {
                    indices.Add(i);
                    break;
                }
            }
        }

        if (indices.Count == 0)
            return;

        string command = $"SelectHandCards {string.Join(" ", indices)}";
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectRecordPatch] Recording: {command}");
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
        PlayerActionBuffer.LogDispatcher($"[Selection] FromHand.Prefix: IsActive={ReplayEngine.IsActive}");
        if (!ReplayEngine.IsActive)
            return;

        ReplayEngine.PeekNext(out string? nextCmd);
        bool safeSkip = ReplayEngine.SafeSkipToSelectHandCards();
        PlayerActionBuffer.LogDispatcher($"[Selection] FromHand.Prefix: SafeSkip={safeSkip} nextCmd='{nextCmd}'");

        if (!safeSkip)
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

        // Capture the hand NOW (before the player selects) so RecordAsync can
        // find indices and distinguish a genuine full-hand selection (e.g. Gambler's Brew)
        // from an auto-resolved no-choice scenario (e.g. Survivor last card).
        List<CardModel>? handBefore = null;
        try
        {
            var combatState = MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.DebugOnlyGetState();
            var player = combatState?.Players.FirstOrDefault();
            var hand = player?.PlayerCombatState?.Hand?.Cards;
            if (hand != null)
                handBefore = new List<CardModel>(hand);
        }
        catch { /* ignore */ }

        // Suppress SyncLocalChoice BEFORE the async await — SyncLocalChoice
        // fires synchronously when the player picks a card, which is before
        // RecordAsync's continuation runs.
        HandCardSelectRecordPatch.SuppressNext = true;

        MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(RecordAsync(__result, handBefore));
    }

    private static async Task RecordAsync(Task<IEnumerable<CardModel>> task, List<CardModel>? handBefore)
    {
        IEnumerable<CardModel> cards;
        try { cards = await task; }
        catch { return; }

        if (handBefore == null || handBefore.Count == 0)
            return;

        var indices = new List<int>();
        foreach (CardModel card in cards)
        {
            for (int i = 0; i < handBefore.Count; i++)
            {
                if (handBefore[i] == card)
                {
                    indices.Add(i);
                    break;
                }
            }
        }

        if (indices.Count == 0)
            return;

        // Skip truly auto-resolved discards where there was no real choice.
        // When a card like Survivor is the last in hand, the discard has
        // only 0-1 options and the game auto-resolves without player input.
        // Recording it would leave a stale command in the replay queue.
        // With 0-1 cards there is never a real choice; with 2+ cards the
        // player made a genuine selection (e.g. Gambler's Brew full hand).
        if (handBefore.Count <= 1)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[HandCardSelectForDiscardRecord] Skipping — auto-resolved (handBefore={handBefore.Count}, selected={indices.Count}).");
            return;
        }

        string command = $"SelectHandCards {string.Join(" ", indices)}";
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectForDiscardRecord] Recording: {command}");
        PlayerActionBuffer.Record(command);
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
