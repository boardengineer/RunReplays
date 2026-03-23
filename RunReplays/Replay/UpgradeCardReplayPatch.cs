using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays;

[HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "ShowScreen")]
public static class UpgradeCardReplayPatch
{
    internal static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static readonly FieldInfo? SelectedCardsField =
        typeof(NDeckUpgradeSelectScreen).GetField(
            "_selectedCards", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static readonly MethodInfo? CheckIfCompleteMethod =
        typeof(NDeckUpgradeSelectScreen).GetMethod(
            "CheckIfSelectionComplete", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NDeckUpgradeSelectScreen? selectionScreen;
    
    [HarmonyPostfix]
    public static void Postfix(NDeckUpgradeSelectScreen __result, IReadOnlyList<CardModel> cards)
    {
        selectionScreen = __result;
    }
}
