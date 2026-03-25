using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Patch;

[HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "ShowScreen")]
public static class UpgradeCardReplayPatch
{
    internal static NCardGridSelectionScreen? selectionScreen;

    [HarmonyPostfix]
    public static void Postfix(NDeckUpgradeSelectScreen __result, IReadOnlyList<CardModel> cards)
    {
        selectionScreen = __result;
    }
}