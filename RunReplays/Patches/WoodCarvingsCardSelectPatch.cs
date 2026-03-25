using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;

namespace RunReplays.Patches;
using RunReplays;
using RunReplays.Patches.Record;

internal static class DeckCardSelectContext
{
    internal static bool Pending;
}



/// <summary>
/// Intercepts NCardGridSelectionScreen.CardsSelected when DeckCardSelectContext.Pending
/// is set. Skips NDeckUpgradeSelectScreen instances (handled by NDeckUpgradeSelectScreenLogPatch).
/// Awaits the result task and records the deck index of each selected card.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class DeckCardSelectRecordPatch
{
    private static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance, Task<IEnumerable<CardModel>> __result)
    {
        if (__instance is NDeckUpgradeSelectScreen)
            return;

        if (!DeckCardSelectContext.Pending)
            return;

        DeckCardSelectContext.Pending = false;

        IReadOnlyList<CardModel>? deckList =
            CardsField?.GetValue(__instance) as IReadOnlyList<CardModel>;

        TaskHelper.RunSafely(RecordAsync(__result, deckList));
    }

    private static async Task RecordAsync(
        Task<IEnumerable<CardModel>> task,
        IReadOnlyList<CardModel>? deckList)
    {
        IEnumerable<CardModel> selected = await task;
        List<CardModel> cardList = selected.ToList();

        string titles = string.Join(", ", cardList.Select(c => $"'{c.Title}'"));
        PlayerActionBuffer.RecordVerboseOnly($"[DeckCardSelect] Selected: [{titles}]");

        // Collect all selected indices into a single command so that
        // multi-card selections (e.g. Morphic Grove) are recorded atomically.
        var indices = new List<int>(cardList.Count);
        foreach (CardModel card in cardList)
        {
            int index = deckList == null ? -1 : deckList.ToList().IndexOf(card);
            indices.Add(index);
        }

        // When a deck removal is pending (Empty Cage, Cook, etc.), record as
        // a single combined RemoveCardFromDeck command.
        string command;
        if (DeckRemovalState.PendingRemoval)
        {
            DeckRemovalState.PendingRemoval = false;
            command = $"RemoveCardFromDeck: {string.Join(" ", indices)}";
            PlayerActionBuffer.Record(command);
        }
        else
        {
            command = $"SelectDeckCard {string.Join(" ", indices)}";
            PlayerActionBuffer.RecordMinimalOnly(command);
        }
        PlayerActionBuffer.LogToDevConsole(
            $"[DeckCardSelectPatch] Recorded {command} ({titles}).");
    }
}