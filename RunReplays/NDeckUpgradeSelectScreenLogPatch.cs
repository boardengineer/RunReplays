using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NCardGridSelectionScreen.CardsSelected that logs to the
/// dev console when NDeckUpgradeSelectScreen resolves a card selection,
/// including the titles of all selected cards.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class NDeckUpgradeSelectScreenLogPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance, Task<IEnumerable<CardModel>> __result)
    {
        if (__instance is not NDeckUpgradeSelectScreen)
            return;

        TaskHelper.RunSafely(LogAsync(__result));
    }

    private static async Task LogAsync(Task<IEnumerable<CardModel>> task)
    {
        IEnumerable<CardModel> cards = await task;
        string titles = string.Join(", ", cards.Select(c => $"'{c.Title}'"));
        PlayerActionBuffer.LogToDevConsole(
            $"[NDeckUpgradeSelectScreen] CardsSelected resolved — cards=[{titles}]");
    }
}
