using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Patches.Record;
using RunReplays;
using RunReplays.Commands;

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

        var cmd = new SelectHandCardsCommand(indices.ToArray());
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectRecordPatch] Recording: {cmd}");
        PlayerActionBuffer.Record(cmd.ToString());
    }

}

// ── Replay ────────────────────────────────────────────────────────────────────

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
    public static void Postfix(
        Task<IEnumerable<CardModel>> __result,
        MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source)
    {
        if (ReplayEngine.IsActive)
            return;

        if (__result == null)
            return;

        PlayerActionBuffer.LogToDevConsole(
            $"[HandCardSelectForDiscardRecord] FromHandForDiscard called — source='{source}' ({source?.GetType().Name ?? "null"})");

        // Capture the hand NOW (before the player selects) so RecordAsync can
        // find indices against the same list the player saw.
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

        // Replicate FromHand's own auto-resolve condition: with no eligible
        // cards, or eligible count within MinSelect and no manual confirmation,
        // the game returns immediately — no UI opens and SyncLocalChoice never
        // fires, so nothing must be recorded (or suppressed).  Selecting zero
        // or ALL cards through the real UI (e.g. Gambler's Brew) is a genuine
        // choice and must be recorded — selection size is not a valid signal.
        int eligibleCount = handBefore?.Count(c => filter == null || filter(c)) ?? 0;
        bool autoResolves = eligibleCount == 0
            || (!prefs.RequireManualConfirmation && eligibleCount <= prefs.MinSelect);
        if (autoResolves)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[HandCardSelectForDiscardRecord] Skipping — auto-resolves " +
                $"(eligible={eligibleCount}, minSelect={prefs.MinSelect}).");
            return;
        }

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

        var selected = cards.ToList();
        var indices = new List<int>();
        foreach (CardModel card in selected)
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

        if (indices.Count < selected.Count)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[HandCardSelectForDiscardRecord] Only resolved {indices.Count}/{selected.Count} " +
                "selected cards to hand indices — selection NOT recorded.");
            Utils.DiagnosticLog.Write("HandCardSelect",
                $"FromHandForDiscard: resolved {indices.Count}/{selected.Count} indices — not recorded.");
            return;
        }

        // An empty selection ("discard nothing") is a real choice on min=0
        // screens — record it so replay confirms the empty selection instead
        // of waiting on the UI forever.
        var cmd = new SelectHandCardsCommand(indices.ToArray());
        PlayerActionBuffer.LogToDevConsole($"[HandCardSelectForDiscardRecord] Recording: {cmd}");
        PlayerActionBuffer.Record(cmd.ToString()!);
    }

}

