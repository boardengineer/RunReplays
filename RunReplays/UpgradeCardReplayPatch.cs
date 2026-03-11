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
        Callable.From(() => AutoSelect(__result, cards, deckIndex)).CallDeferred();
    }

    private static void AutoSelect(NDeckUpgradeSelectScreen screen, IReadOnlyList<CardModel> cards, int deckIndex)
    {
        if (!ReplayRunner.ExecuteUpgradeCard(out _))
            return;

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

        CardModel card = cards[deckIndex];
        selectedCards.Clear();
        selectedCards.Add(card);

        CheckIfCompleteMethod?.Invoke(screen, null);

        PlayerActionBuffer.LogToDevConsole(
            $"[UpgradeCardReplayPatch] Auto-selected '{card.Title}' at deck index {deckIndex}.");
    }
}
