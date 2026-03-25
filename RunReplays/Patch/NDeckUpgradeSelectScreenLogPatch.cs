using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Patch;

/// <summary>
///     Harmony postfix on NCardGridSelectionScreen.CardsSelected that logs to the
///     dev console when NDeckUpgradeSelectScreen resolves a card selection,
///     and records each selected card's deck index into the action buffer.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class NDeckUpgradeSelectScreenLogPatch
{
    private static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance, Task<IEnumerable<CardModel>> __result)
    {
        if (__instance is not NDeckUpgradeSelectScreen)
            return;

        var deckList =
            CardsField?.GetValue(__instance) as IReadOnlyList<CardModel>;

        TaskHelper.RunSafely(LogAsync(__result, deckList));
    }

    private static async Task LogAsync(Task<IEnumerable<CardModel>> task, IReadOnlyList<CardModel>? deckList)
    {
        var cards = await task;
        var cardList = cards.ToList();
        var titles = string.Join(", ", cardList.Select(c => $"'{c.Title}'"));

        PlayerActionBuffer.LogToDevConsole(
            $"[NDeckUpgradeSelectScreen] CardsSelected resolved — cards=[{titles}]");

        PlayerActionBuffer.RecordVerboseOnly($"[NDeckUpgradeSelectScreen] Upgraded cards: [{titles}]");
        foreach (var card in cardList)
        {
            var index = deckList == null ? -1 : deckList.ToList().IndexOf(card);
            PlayerActionBuffer.RecordMinimalOnly($"UpgradeCard {index}");
        }
    }
}