using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays;

/// <summary>
/// Shared state for correlating FromDeckForRemoval / FromDeckForUpgrade with
/// the subsequent CardPileCmd.RemoveFromDeck call. Set by the entry patches,
/// cleared and consumed by RemoveFromDeckRecordPatch.
/// </summary>
internal static class DeckRemovalState
{
    internal static bool PendingRemoval;
}

/// <summary>
/// Sets PendingRemoval when CardSelectCmd.FromDeckForRemoval is entered so that
/// the next RemoveFromDeck call can be attributed to a card removal.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForRemoval))]
public static class FromDeckForRemovalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        DeckRemovalState.PendingRemoval = true;
        PlayerActionBuffer.LogToDevConsole("[DeckRemovalRecordPatch] FromDeckForRemoval entered — awaiting RemoveFromDeck.");
    }
}

/// <summary>
/// Records the removed card when CardPileCmd.RemoveFromDeck fires while
/// PendingRemoval is set.
/// </summary>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(CardModel), typeof(bool) })]
public static class RemoveFromDeckRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel card)
    {
        if (!DeckRemovalState.PendingRemoval)
        {
            PlayerActionBuffer.LogToDevConsole($"[DeckRemovalRecordPatch] RemoveFromDeck — card='{card.Title}' (no pending removal, not recording).");
            return;
        }

        DeckRemovalState.PendingRemoval = false;
        PlayerActionBuffer.Record($"RemoveCardFromDeck: {card.Title}");
        PlayerActionBuffer.LogToDevConsole($"[DeckRemovalRecordPatch] RemoveFromDeck — recorded removal of '{card.Title}'.");
    }
}
