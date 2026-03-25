using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Commands;

/// <summary>
/// Remove a card from the deck (shop card removal, events, etc.).
/// Recorded as: "RemoveCardFromDeck: {deckIndex}"
///
/// Uses the same CardGridScreenCapture as SelectDeckCardCommand — waits for the
/// NCardGridSelectionScreen to appear, maps the recorded index to a card in
/// the screen's _cards list, and resolves _completionSource directly.
/// </summary>
public sealed class RemoveCardFromDeckCommand : ReplayCommand
{
    private const string Prefix = "RemoveCardFromDeck: ";

    public int[] DeckIndices { get; }

    public override bool IsSelectionCommand => true;

    public RemoveCardFromDeckCommand(int[] deckIndices) : base("")
    {
        DeckIndices = deckIndices;
    }

    public override string ToString()
        => $"{Prefix}{string.Join(" ", DeckIndices)}";

    public override string Describe()
    {
        string idxStr = DeckIndices.Length > 0 ? string.Join(", ", DeckIndices) : "(none)";
        return $"remove card from deck indices=[{idxStr}]";
    }

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = CardGridScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
            return ExecuteResult.Retry(300);

        foreach (int idx in DeckIndices)
        {
            if (idx < 0 || idx >= cards.Count)
            {
                PlayerActionBuffer.LogMigrationWarning(
                    $"[RemoveCardFromDeck] Index {idx} out of range (count={cards.Count}) — retrying.");
                return ExecuteResult.Retry(300);
            }
        }

        var selected = new List<CardModel>();
        foreach (int idx in DeckIndices)
        {
            selected.Add(cards[idx]);
        }

        CardGridScreenCapture.ResolveSelection(selected);
        return ExecuteResult.Ok();
    }

    public static RemoveCardFromDeckCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length).Trim();
        if (rest.Length == 0)
            return null;

        var parts = rest.Split(' ');
        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out int idx))
                indices.Add(idx);
            else
                return null;
        }
        return new RemoveCardFromDeckCommand(indices.ToArray());
    }
}
