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

    public int DeckIndex { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.None;
    public override bool IsSelectionCommand => true;

    private RemoveCardFromDeckCommand(string raw, int deckIndex) : base(raw)
    {
        DeckIndex = deckIndex;
    }

    public override string Describe() => $"remove card from deck index={DeckIndex}";

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = CardGridScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
            return ExecuteResult.Retry(300);

        if (DeckIndex < 0 || DeckIndex >= cards.Count)
        {
            PlayerActionBuffer.LogMigrationWarning(
                $"[RemoveCardFromDeck] Index {DeckIndex} out of range (count={cards.Count}) — retrying.");
            return ExecuteResult.Retry(300);
        }

        var selected = new List<CardModel> { cards[DeckIndex] };
        PlayerActionBuffer.LogMigrationWarning(
            $"[RemoveCardFromDeck] Selected '{cards[DeckIndex].Title}' at index {DeckIndex} for removal.");

        CardGridScreenCapture.ResolveSelection(selected);
        return ExecuteResult.Ok();
    }

    public static RemoveCardFromDeckCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int idx))
            return new RemoveCardFromDeckCommand(raw, idx);

        return null;
    }
}
