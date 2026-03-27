using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Commands;

namespace RunReplays.Patches.Record;

/// <summary>
/// Records TakeCard commands when the player selects a card, sacrifices,
/// or skips on NCardRewardSelectionScreen.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen))]
public static class TakeCardRecordPatch
{
    private static readonly FieldInfo? CardRowField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_cardRow", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Fired when the player clicks a card holder to take it.
    /// Records "TakeCard {index} # {cardTitle}".
    /// Index is the 0-based position in _cardRow's children (left to right).
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

        var cardRow = CardRowField?.GetValue(__instance) as Node;
        if (cardRow == null || cardModel == null) return;

        // Card reward holders don't seem to come up in left to right order, resort them by X coordinate to get there
        // actual visual order.
        var holders = new List<(Control node, CardModel card)>();
        foreach (Node child in cardRow.GetChildren())
        {
            if (child is not Control ctrl) continue;
            var childCard = child.GetType()
                .GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(child) as CardModel;
            if (childCard != null)
                holders.Add((ctrl, childCard));
        }
        holders.Sort((a, b) => a.node.Position.X.CompareTo(b.node.Position.X));

        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i].card.Title == title)
            {
                var cmd = new TakeCardCommand(i) { Comment = title };
                PlayerActionBuffer.Record(cmd.ToLogString());
                return;
            }
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
