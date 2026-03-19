using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Commands;

/// <summary>
/// A card selection command consumed inline by ICardSelector implementations
/// or Harmony patch postfixes, not by the dispatcher.
///
/// Covers: SelectCardFromScreen, SelectDeckCard, SelectHandCards,
///         SelectSimpleCard, RemoveCardFromDeck, UpgradeCard.
///
/// These commands are never dispatched — the game opens a selection screen,
/// the corresponding patch peeks the queue, consumes the command, and makes
/// the selection.  The dispatcher skips them and retries until they're consumed.
/// </summary>
public class SelectionCommand : ReplayCommand
{
    public enum SelectionKind
    {
        SelectCardFromScreen,
        SelectDeckCard,
        SelectHandCards,
        SelectSimpleCard,
        RemoveCardFromDeck,
        UpgradeCard,
    }

    public SelectionKind Kind { get; }

    /// <summary>
    /// Parsed indices from the command.  Interpretation depends on <see cref="Kind"/>:
    /// - UpgradeCard: single deck index
    /// - SelectDeckCard: one or more deck indices
    /// - SelectSimpleCard: single card index
    /// - SelectCardFromScreen: single index (-1 = skip)
    /// - SelectHandCards: combat card IDs (as ints)
    /// - RemoveCardFromDeck: single deck index
    /// </summary>
    public int[] Indices { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.None;
    public override bool IsSelectionCommand => true;

    private SelectionCommand(string raw, SelectionKind kind, int[] indices) : base(raw)
    {
        Kind = kind;
        Indices = indices;
    }

    public override string Describe()
    {
        string idxStr = Indices.Length > 0 ? string.Join(", ", Indices) : "(none)";
        return Kind switch
        {
            SelectionKind.SelectCardFromScreen => $"select card from screen index={idxStr}",
            SelectionKind.SelectDeckCard => $"select deck card indices=[{idxStr}]",
            SelectionKind.SelectHandCards => $"select hand cards ids=[{idxStr}]",
            SelectionKind.SelectSimpleCard => $"select simple card index={idxStr}",
            SelectionKind.RemoveCardFromDeck => $"remove card from deck index={idxStr}",
            SelectionKind.UpgradeCard => $"upgrade card at deck index {idxStr}",
            _ => "card selection",
        };
    }

    public override ExecuteResult Execute()
    {
        // Use _cards from the screen instance (same list the recording patch indexes
        // into), not the ShowScreen parameter which may have a different order.
        var cards = UpgradeCardReplayPatch.CardsField?.GetValue(UpgradeCardReplayPatch.selectionScreen) as IReadOnlyList<CardModel>;
        if (cards == null)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[UpgradeCardReplayPatch] Could not access _cards — aborting.");
            return ExecuteResult.Retry(300);
        }

        foreach (var deckIndex in Indices)
            if (deckIndex < 0 || deckIndex >= cards.Count)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[UpgradeCardReplayPatch] Deck index {deckIndex} out of range (count={cards.Count}) — aborting.");
                return ExecuteResult.Retry(300);
            }

        if (UpgradeCardReplayPatch.SelectedCardsField?.GetValue(UpgradeCardReplayPatch.selectionScreen) is not HashSet<CardModel> selectedCards)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[UpgradeCardReplayPatch] Could not access _selectedCards — aborting.");
            return ExecuteResult.Retry(300);
        }

        foreach (var deckIndex in Indices)
        {
            CardModel nextCard = cards[deckIndex];
            selectedCards.Add(nextCard);
        }
        UpgradeCardReplayPatch.CheckIfCompleteMethod?.Invoke(UpgradeCardReplayPatch.selectionScreen, null);

        return ExecuteResult.Ok();
    }

    public static SelectionCommand? TryParse(string raw)
    {
        if (raw.StartsWith("SelectCardFromScreen "))
            return ParseSingleInt(raw, "SelectCardFromScreen ".Length, SelectionKind.SelectCardFromScreen);
        if (raw.StartsWith("SelectDeckCard "))
            return ParseMultiInt(raw, "SelectDeckCard ".Length, SelectionKind.SelectDeckCard);
        if (raw.StartsWith("SelectHandCards "))
            return ParseMultiInt(raw, "SelectHandCards ".Length, SelectionKind.SelectHandCards);
        if (raw == "SelectHandCards")
            return new SelectionCommand(raw, SelectionKind.SelectHandCards, System.Array.Empty<int>());
        if (raw.StartsWith("SelectSimpleCard "))
            return ParseSingleInt(raw, "SelectSimpleCard ".Length, SelectionKind.SelectSimpleCard);
        if (raw.StartsWith("RemoveCardFromDeck: "))
            return ParseSingleInt(raw, "RemoveCardFromDeck: ".Length, SelectionKind.RemoveCardFromDeck);
        if (raw.StartsWith("UpgradeCard "))
            return ParseSingleInt(raw, "UpgradeCard ".Length, SelectionKind.UpgradeCard);
        return null;
    }

    private static SelectionCommand? ParseSingleInt(string raw, int prefixLen, SelectionKind kind)
    {
        if (int.TryParse(raw.AsSpan(prefixLen).Trim(), out int index))
            return new SelectionCommand(raw, kind, new[] { index });
        return null;
    }

    private static SelectionCommand? ParseMultiInt(string raw, int prefixLen, SelectionKind kind)
    {
        string rest = raw.Substring(prefixLen).Trim();
        if (rest.Length == 0)
            return new SelectionCommand(raw, kind, System.Array.Empty<int>());

        var parts = rest.Split(' ');
        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out int idx))
                indices.Add(idx);
            else
                return null;
        }
        return new SelectionCommand(raw, kind, indices.ToArray());
    }
}
