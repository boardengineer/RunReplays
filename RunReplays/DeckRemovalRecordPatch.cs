using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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
    /// <summary>Set by SwipePower.Steal to suppress recording of Thieving Hopper's card theft.</summary>
    internal static bool SuppressRecording;
}

/// <summary>
/// Suppresses RemoveFromDeck recording for Thieving Hopper's card theft.
/// SwipePower.Steal calls CardPileCmd.RemoveFromDeck directly (not via
/// FromDeckForRemoval), but a stale PendingRemoval flag would cause it to
/// be recorded.  The theft is RNG-driven and replays deterministically, so
/// no action-log entry is needed.
/// </summary>
[HarmonyPatch(typeof(SwipePower), nameof(SwipePower.Steal))]
public static class SwipePowerStealRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        DeckRemovalState.SuppressRecording = true;
        PlayerActionBuffer.LogToDevConsole("[DeckRemovalRecordPatch] SwipePower.Steal — suppressing RemoveFromDeck recording.");
    }
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
/// Records the removed card when CardPileCmd.RemoveFromDeck(CardModel, bool)
/// fires while PendingRemoval is set. Sets a flag so the list overload (which
/// the single-card overload delegates to) does not double-record.
/// </summary>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(CardModel), typeof(bool) })]
public static class RemoveFromDeckRecordPatch
{
    internal static bool HandledBySingleCard;

    [HarmonyPrefix]
    public static void Prefix(CardModel card)
    {
        HandledBySingleCard = true;
        DeckRemovalRecordHelper.RecordCard(card);
    }
}

/// <summary>
/// Records removed cards when CardPileCmd.RemoveFromDeck(IReadOnlyList, bool)
/// fires while PendingRemoval is set. Skips recording when the call originates
/// from the single-card overload (which already recorded via its own patch).
/// Called directly by events like Spirit Grafter.
/// </summary>
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromDeck),
    new[] { typeof(IReadOnlyList<CardModel>), typeof(bool) })]
public static class RemoveFromDeckListRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(IReadOnlyList<CardModel> cards)
    {
        if (RemoveFromDeckRecordPatch.HandledBySingleCard)
        {
            RemoveFromDeckRecordPatch.HandledBySingleCard = false;
            return;
        }

        foreach (var card in cards)
            DeckRemovalRecordHelper.RecordCard(card);
    }
}

/// <summary>
/// Shared recording logic for both RemoveFromDeck overloads.
/// </summary>
internal static class DeckRemovalRecordHelper
{
    internal static void RecordCard(CardModel card)
    {
        if (DeckRemovalState.SuppressRecording)
        {
            DeckRemovalState.SuppressRecording = false;
            PlayerActionBuffer.LogToDevConsole($"[DeckRemovalRecordPatch] RemoveFromDeck — card='{card.Title}' (suppressed, not recording).");
            return;
        }

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
