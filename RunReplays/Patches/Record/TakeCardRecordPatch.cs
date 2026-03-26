using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Commands;

namespace RunReplays.Patches.Record;
using RunReplays;

/// <summary>
/// Records TakeCard commands when the player selects a card or sacrifices
/// on NCardRewardSelectionScreen.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen))]
public static class TakeCardRecordPatch
{
    /// <summary>
    /// Fired when the player clicks a card holder to take it.
    /// Records "TakeCard {index} # {cardTitle}".
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("SelectCard")]
    public static void SelectCardPrefix(NCardRewardSelectionScreen __instance, object cardHolder)
    {
        if (ReplayEngine.IsActive) return;

        // Find the card holder's index among siblings with CardModel property.
        var cardModel = cardHolder.GetType()
            .GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(cardHolder) as CardModel;

        string? title = cardModel?.Title;

        // Enumerate card holders to find the index.
        int index = 0;
        foreach (Node node in __instance.FindChildren("*", "", owned: false))
        {
            var prop = node.GetType().GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(node) is not CardModel) continue;

            if (ReferenceEquals(node, cardHolder))
            {
                var cmd = new TakeCardCommand(index) { Comment = title };
                PlayerActionBuffer.Record(cmd.ToLogString());
                return;
            }
            index++;
        }
    }

    /// <summary>
    /// Fired when the player selects an alternate reward (e.g. Pael's Wing sacrifice).
    /// Records "TakeCard sacrifice # {optionId}".
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("OnAlternateRewardSelected")]
    public static void OnAlternatePrefix(NCardRewardSelectionScreen __instance)
    {
        if (ReplayEngine.IsActive) return;

        var cmd = TakeCardCommand.Sacrifice();
        cmd.Comment = "sacrifice";
        PlayerActionBuffer.Record(cmd.ToLogString());
    }
}
