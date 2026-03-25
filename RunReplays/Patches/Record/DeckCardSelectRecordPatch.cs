using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Patches.Record;
using RunReplays.Commands;

[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class DeckCardSelectRecordPatch
{
    internal static bool Pending;

    private static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance, Task<IEnumerable<CardModel>> __result)
    {
        if (__instance is NDeckUpgradeSelectScreen)
            return;

        if (!Pending)
            return;

        Pending = false;

        var deckList =
            CardsField?.GetValue(__instance) as IReadOnlyList<CardModel>;

        TaskHelper.RunSafely(RecordAsync(__result, deckList));
    }

    private static async Task RecordAsync(
        Task<IEnumerable<CardModel>> task,
        IReadOnlyList<CardModel>? deckList)
    {
        var selected = await task;
        var cardList = selected.ToList();

        var titles = string.Join(", ", cardList.Select(c => $"'{c.Title}'"));
        PlayerActionBuffer.RecordVerboseOnly($"[DeckCardSelect] Selected: [{titles}]");

        // Collect all selected indices into a single command so that
        // multi-card selections (e.g. Morphic Grove) are recorded atomically.
        var indices = new List<int>(cardList.Count);
        foreach (var card in cardList)
        {
            var index = deckList == null ? -1 : deckList.ToList().IndexOf(card);
            indices.Add(index);
        }

        // When a deck removal is pending (Empty Cage, Cook, etc.), record as
        // a single combined RemoveCardFromDeck command.
        string command;
        if (DeckRemovalState.PendingRemoval)
        {
            DeckRemovalState.PendingRemoval = false;
            command = new RemoveCardFromDeckCommand(indices.ToArray()).ToString();
            PlayerActionBuffer.Record(command);
        }
        else
        {
            command = new SelectDeckCardCommand(indices.ToArray()).ToString();
            PlayerActionBuffer.RecordMinimalOnly(command);
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[DeckCardSelectPatch] Recorded {command} ({titles}).");
    }
}