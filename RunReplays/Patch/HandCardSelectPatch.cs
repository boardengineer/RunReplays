using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Patch;

/// <summary>
///     Records hand-card selections (e.g. Touch of Insanity) into the action log
///     by patching PlayerChoiceSynchronizer.SyncLocalChoice — the single point
///     where every finalised local choice is announced.
///     Only CombatCard-type choices are captured (those created by CardSelectCmd.FromHand).
///     Commands are recorded immediately when the choice is made.
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class HandCardSelectRecordPatch
{
    /// <summary>
    ///     Set by HandCardSelectForDiscardRecordPatch when it records a
    ///     FromHandForDiscard selection.  Prevents SyncLocalChoice from
    ///     recording a duplicate for the same selection.
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
            PlayerActionBuffer.LogToDevConsole(
                "[HandCardSelectRecordPatch] Suppressed — already recorded by FromHandForDiscard.");
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
        foreach (var card in cards)
            for (var i = 0; i < hand.Count; i++)
                if (hand[i] == card)
                {
                    indices.Add(i);
                    break;
                }

        if (indices.Count == 0)
            return;

        var command = $"SelectHandCards {string.Join(" ", indices)}";
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectRecordPatch] Recording: {command}");
        PlayerActionBuffer.Record(command);
    }
}

// ── Replay ────────────────────────────────────────────────────────────────────

// ── FromHandForDiscard (e.g. Tools of the Trade) ────────────────────────────

/// <summary>
///     Records hand-card selections that go through CardSelectCmd.FromHandForDiscard
///     (e.g. Tools of the Trade's start-of-turn discard).  This path does NOT go
///     through PlayerChoiceSynchronizer.SyncLocalChoice, so HandCardSelectRecordPatch
///     never captures it.  We await the Task result and record the selected card IDs.
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
            var combatState = CombatManager.Instance?.DebugOnlyGetState();
            var player = combatState?.Players.FirstOrDefault();
            var hand = player?.PlayerCombatState?.Hand?.Cards;
            if (hand != null)
                handBefore = new List<CardModel>(hand);
        }
        catch
        {
            /* ignore */
        }

        // Suppress SyncLocalChoice BEFORE the async await — SyncLocalChoice
        // fires synchronously when the player picks a card, which is before
        // RecordAsync's continuation runs.
        HandCardSelectRecordPatch.SuppressNext = true;

        TaskHelper.RunSafely(RecordAsync(__result, handBefore));
    }

    private static async Task RecordAsync(Task<IEnumerable<CardModel>> task, List<CardModel>? handBefore)
    {
        IEnumerable<CardModel> cards;
        try
        {
            cards = await task;
        }
        catch
        {
            return;
        }

        if (handBefore == null || handBefore.Count == 0)
            return;

        var indices = new List<int>();
        foreach (var card in cards)
            for (var i = 0; i < handBefore.Count; i++)
                if (handBefore[i] == card)
                {
                    indices.Add(i);
                    break;
                }

        if (indices.Count == 0)
            return;

        // Skip auto-resolved discards where there was no real choice.
        // If every card in hand was selected, the game auto-resolved
        // (e.g. Hidden Daggers with 2 cards, Survivor with 1 card).
        if (indices.Count >= handBefore.Count)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[HandCardSelectForDiscardRecord] Skipping — auto-resolved (handBefore={handBefore.Count}, selected={indices.Count}).");
            return;
        }

        var command = $"SelectHandCards {string.Join(" ", indices)}";
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectForDiscardRecord] Recording: {command}");
        PlayerActionBuffer.Record(command);
    }
}