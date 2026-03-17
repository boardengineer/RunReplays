using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NDeckUpgradeSelectScreen.ShowScreen that, when a replay
/// is active and the next command is an UpgradeCard entry, defers an automatic
/// card selection to the next Godot frame.
///
/// NOTE: During replay the SMITH rest-site option goes through CardSelectCmd
/// (handled by ReplayDeckCardSelector) rather than ShowScreen, so this patch
/// is a secondary path that covers non-SMITH upgrade screens (if any).
///
/// ShowScreen is the right hook because it sets _cards and _prefs on the
/// instance before returning, and _Ready (which wires up the UI nodes needed
/// by CheckIfSelectionComplete) is called synchronously by NOverlayStack.Push
/// inside ShowScreen, so the screen is fully initialised by the time our
/// postfix runs.
///
/// AutoSelect adds the target card to _selectedCards and calls
/// CheckIfSelectionComplete(), which is exactly what the confirm button does.
/// </summary>
[HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "ShowScreen")]
public static class UpgradeCardReplayPatch
{
    // Read _cards from the base NCardGridSelectionScreen — this is the same
    // field the recording patch uses to compute the index, so it reflects any
    // sorting/reordering that ShowScreen applies.
    private static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? SelectedCardsField =
        typeof(NDeckUpgradeSelectScreen).GetField(
            "_selectedCards", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? CheckIfCompleteMethod =
        typeof(NDeckUpgradeSelectScreen).GetMethod(
            "CheckIfSelectionComplete", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NDeckUpgradeSelectScreen __result, IReadOnlyList<CardModel> cards)
    {
        if (!ReplayEngine.PeekUpgradeCard(out int deckIndex))
            return;

        PlayerActionBuffer.LogToDevConsole(
            $"[UpgradeCardReplayPatch] ShowScreen — deferring auto-select for deck index {deckIndex}.");
        Callable.From(() => AutoSelect(__result, deckIndex)).CallDeferred();
    }

    private static void AutoSelect(NDeckUpgradeSelectScreen screen, int deckIndex)
    {
        if (!ReplayRunner.ExecuteUpgradeCard(out _))
            return;

        // Use _cards from the screen instance (same list the recording patch indexes
        // into), not the ShowScreen parameter which may have a different order.
        var cards = CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[UpgradeCardReplayPatch] Could not access _cards — aborting.");
            return;
        }

        if (deckIndex < 0 || deckIndex >= cards.Count)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[UpgradeCardReplayPatch] Deck index {deckIndex} out of range (count={cards.Count}) — aborting.");
            return;
        }

        if (SelectedCardsField?.GetValue(screen) is not HashSet<CardModel> selectedCards)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[UpgradeCardReplayPatch] Could not access _selectedCards — aborting.");
            return;
        }

        // Add the first card.
        CardModel card = cards[deckIndex];
        selectedCards.Clear();
        selectedCards.Add(card);
        PlayerActionBuffer.LogToDevConsole(
            $"[UpgradeCardReplayPatch] Auto-selected '{card.Title}' at deck index {deckIndex}.");

        // Some relics (e.g. Yummy Cookie) allow upgrading multiple cards on a
        // single screen.  Keep consuming UpgradeCard commands until there are
        // no more pending or we run out of cards.
        while (ReplayEngine.PeekUpgradeCard(out int nextIndex))
        {
            if (nextIndex < 0 || nextIndex >= cards.Count)
                break;

            ReplayRunner.ExecuteUpgradeCard(out _);
            CardModel nextCard = cards[nextIndex];
            selectedCards.Add(nextCard);
            PlayerActionBuffer.LogToDevConsole(
                $"[UpgradeCardReplayPatch] Auto-selected additional '{nextCard.Title}' at deck index {nextIndex}.");
        }

        CheckIfCompleteMethod?.Invoke(screen, null);
    }
}
