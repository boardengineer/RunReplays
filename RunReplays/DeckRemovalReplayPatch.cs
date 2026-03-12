using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;

namespace RunReplays;

/// <summary>
/// Replays RemoveCardFromDeck commands by pushing a ReplayRemoveCardSelector onto
/// the CardSelectCmd selector stack before FromDeckForRemoval runs, so the game
/// picks the recorded card instead of showing the removal UI.
///
/// Card selection uses the stored 0-based deck index. If the index is out of
/// range the first available card is used as a fallback.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForRemoval))]
public static class DeckRemovalReplayPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (!ReplayEngine.IsActive)
            return;

        if (!ReplayEngine.PeekRemoveCardFromDeck(out _))
            return;

        _pendingScope = CardSelectCmd.PushSelector(new ReplayRemoveCardSelector());
        PlayerActionBuffer.LogToDevConsole(
            "[DeckRemovalReplayPatch] Pushed ReplayRemoveCardSelector for FromDeckForRemoval.");
    }
}

/// <summary>
/// ICardSelector that consumes the pending RemoveCardFromDeck command and
/// returns the card at the recorded 0-based deck index from the options list.
/// </summary>
internal sealed class ReplayRemoveCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var scope = DeckRemovalReplayPatch._pendingScope;
        DeckRemovalReplayPatch._pendingScope = null;
        scope?.Dispose();

        var optionList = options.ToList();

        if (!ReplayEngine.ConsumeRemoveCardFromDeck(out int deckIndex))
        {
            PlayerActionBuffer.LogToDevConsole(
                "[ReplayRemoveCardSelector] No RemoveCardFromDeck command — returning first card.");
            return Task.FromResult<IEnumerable<CardModel>>(
                optionList.Take(Math.Max(1, minSelect)).ToList());
        }

        if (deckIndex >= 0 && deckIndex < optionList.Count)
        {
            var match = optionList[deckIndex];
            PlayerActionBuffer.LogToDevConsole(
                $"[ReplayRemoveCardSelector] Selected '{match.Title}' at index {deckIndex} for removal.");
            return Task.FromResult<IEnumerable<CardModel>>(new[] { match });
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[ReplayRemoveCardSelector] Index {deckIndex} out of range (count={optionList.Count}) — falling back to first available.");
        return Task.FromResult<IEnumerable<CardModel>>(
            optionList.Take(Math.Max(1, minSelect)).ToList());
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => null;
}
