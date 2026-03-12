using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays;

/// <summary>
/// Shared state for correlating FromDeckForRemoval / FromDeckForUpgrade with
/// the subsequent CardPileCmd.RemoveFromDeck call. Set by the entry patches,
/// cleared and consumed by RemoveFromDeckRecordPatch.
/// </summary>
internal static class DeckRemovalState
{
    internal static bool PendingRemoval;
    /// <summary>The ordered card list shown to the player; captured from NCardGridSelectionScreen.</summary>
    internal static IReadOnlyList<CardModel>? PendingOptions;
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
        DeckRemovalState.PendingOptions = null;
        PlayerActionBuffer.LogToDevConsole("[DeckRemovalRecordPatch] FromDeckForRemoval entered — awaiting RemoveFromDeck.");
    }
}

/// <summary>
/// Captures the card list shown in NCardGridSelectionScreen when a deck removal
/// is pending, so RemoveFromDeckRecordPatch can record a stable deck index.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class DeckRemovalCardSelectPatch
{
    private static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance)
    {
        if (!DeckRemovalState.PendingRemoval)
            return;

        DeckRemovalState.PendingOptions =
            CardsField?.GetValue(__instance) as IReadOnlyList<CardModel>;
        PlayerActionBuffer.LogToDevConsole(
            $"[DeckRemovalRecordPatch] Captured {DeckRemovalState.PendingOptions?.Count ?? 0} option(s) from selection screen.");
    }
}

/// <summary>
/// Records the removed card when CardPileCmd.RemoveFromDeck fires while
/// PendingRemoval is set. Uses the captured options list to record the
/// 0-based index rather than the card title.
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

        // Don't clear PendingRemoval or PendingOptions here — multi-card removal
        // (e.g. Precarious Shears) calls RemoveFromDeck once per card, and all
        // calls must be recorded.  State is reset when FromDeckForRemoval is
        // entered again for the next removal flow.

        var options = DeckRemovalState.PendingOptions;

        int index = -1;
        if (options != null)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (ReferenceEquals(options[i], card) || options[i] == card)
                {
                    index = i;
                    break;
                }
            }
        }

        PlayerActionBuffer.Record($"RemoveCardFromDeck: {index}");
        PlayerActionBuffer.LogToDevConsole($"[DeckRemovalRecordPatch] RemoveFromDeck — recorded removal of '{card.Title}' at index {index}.");
    }
}
