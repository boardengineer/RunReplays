using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Commands;

namespace RunReplays;

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
public static class CardRewardReplayPatch
{
    internal static NCardRewardSelectionScreen? selectionScreen;

    [HarmonyPostfix]
    public static void Postfix(NCardRewardSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

       selectionScreen = __instance;
       CardRewardCommand.waitingForRewardScreenOpen = false;
    }

    private static Node? FindHolderByTitle(
        Godot.Collections.Array<Node> nodes, string expectedTitle)
    {
        foreach (Node node in nodes)
        {
            PropertyInfo? prop = node.GetType().GetProperty(
                "CardModel", BindingFlags.Public | BindingFlags.Instance);

            if (prop?.GetValue(node) is not CardModel card)
                continue;

            if (card.Title == expectedTitle)
                return node;
        }
        return null;
    }

    internal static bool SelectCard(string expectedTitle)
    {
        if (selectionScreen == null)
            return false;

        Node? match = FindHolderByTitle(selectionScreen.FindChildren("*", "", owned: false), expectedTitle);

        if (match == null)
            return false;

        match.EmitSignal("Pressed", match);
        
        selectionScreen = null;
        return true;

    }
}