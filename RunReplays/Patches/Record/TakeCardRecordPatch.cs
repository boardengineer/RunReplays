using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Commands;

namespace RunReplays.Patches.Record;
using RunReplays;

/// <summary>
/// Records TakeCard commands when the player selects a card, sacrifices,
/// or skips on NCardRewardSelectionScreen.
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

        var cardModel = cardHolder.GetType()
            .GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(cardHolder) as CardModel;

        string? title = cardModel?.Title;

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
    /// Fired when the player selects an alternate reward option.
    /// Sacrifice (DismissScreenAndRemoveReward) records "TakeCard sacrifice".
    /// Skip (DismissScreenAndKeepReward) records "TakeCard skip".
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("OnAlternateRewardSelected")]
    public static void OnAlternatePrefix(PostAlternateCardRewardAction afterSelected)
    {
        if (ReplayEngine.IsActive) return;

        if (afterSelected == PostAlternateCardRewardAction.DismissScreenAndRemoveReward)
        {
            var cmd = TakeCardCommand.Sacrifice();
            cmd.Comment = "sacrifice";
            PlayerActionBuffer.Record(cmd.ToLogString());
        }
        else if (afterSelected == PostAlternateCardRewardAction.DismissScreenAndKeepReward)
        {
            var cmd = TakeCardCommand.Skip();
            cmd.Comment = "skip";
            PlayerActionBuffer.Record(cmd.ToLogString());
        }
    }
}
