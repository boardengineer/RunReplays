using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Patches.Replay;

namespace RunReplays.Commands;

/// <summary>
/// Captures NCardGridSelectionScreen instances when they enter the scene tree
/// and provides helpers to simulate card clicks.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class CardGridScreenCapture
{
    internal static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? OnCardClickedMethod =
        typeof(NCardGridSelectionScreen).GetMethod(
            "OnCardClicked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? CompletionSourceField =
        typeof(NCardGridSelectionScreen).GetField(
            "_completionSource", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? PileField =
        typeof(NCombatPileCardSelectScreen).GetField(
            "_pile", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? FilterField =
        typeof(NCombatPileCardSelectScreen).GetField(
            "_filter", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NCardGridSelectionScreen? ActiveScreen;

    /// <summary>
    ///     Returns the selectable cards of a grid screen in a stable order shared
    ///     by record and replay.  Most screens keep the list in _cards, but
    ///     NCombatPileCardSelectScreen (Seeker Strike etc.) leaves _cards as an
    ///     empty array and drives its grid from the live pile — rebuild that list
    ///     the same way the screen does (pile order, filtered).
    /// </summary>
    internal static IReadOnlyList<CardModel>? GetSelectableCards(NCardGridSelectionScreen screen)
    {
        var cards = CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards is { Count: > 0 })
            return cards;

        if (screen is NCombatPileCardSelectScreen)
        {
            if (PileField?.GetValue(screen) is not CardPile pile)
                return null;
            var filter = FilterField?.GetValue(screen) as Func<CardModel, bool>;
            var pileCards = filter == null
                ? pile.Cards.ToList()
                : pile.Cards.Where(filter).ToList();
            return pileCards.Count > 0 ? pileCards : null;
        }

        return cards;
    }

    [HarmonyPrefix]
    public static void Prefix(NCardGridSelectionScreen __instance)
    {
        ActiveScreen = __instance;
        if (!ReplayEngine.IsActive) return;
        UpgradeCardReplayPatch.selectionScreen = __instance;
        PlayerActionBuffer.LogDispatcher(
            $"[CardGridCapture] Screen captured: {__instance.GetType().Name}");
        ReplayDispatcher.DispatchNow();
    }

    internal static void ClickCard(NCardGridSelectionScreen screen, CardModel card)
    {
        OnCardClickedMethod?.Invoke(screen, new object[] { card });
    }

    internal static void ConfirmSelection(NCardGridSelectionScreen screen, IEnumerable<CardModel> cards)
    {
        var tcs = CompletionSourceField?.GetValue(screen)
            as System.Threading.Tasks.TaskCompletionSource<IEnumerable<CardModel>>;
        tcs?.TrySetResult(cards);
    }

    internal static Godot.Node? FindCardHolderByIndex(Godot.Node screen, int index)
    {
        int count = 0;
        foreach (Godot.Node node in screen.FindChildren("*", "", owned: false))
        {
            var prop = node.GetType().GetProperty(
                "CardModel", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(node) is not CardModel) continue;
            if (count == index) return node;
            count++;
        }
        return null;
    }

    internal static void Clear()
    {
        ActiveScreen = null;
    }
}
