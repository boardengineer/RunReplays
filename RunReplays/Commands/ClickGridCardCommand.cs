using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Commands;

/// <summary>
/// Clicks a single card on the grid selection screen without confirming.
/// Emulates a UI click to toggle the card's selected state.
/// Recorded as: "ClickGridCard {index}"
/// </summary>
public sealed class ClickGridCardCommand : ReplayCommand
{
    private const string Prefix = "ClickGridCard ";

    public int Index { get; }

    public ClickGridCardCommand(int index) : base("")
    {
        Index = index;
    }

    public override string ToString() => $"{Prefix}{Index}";

    public override string Describe() => $"click grid card [{Index}]";

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = CardGridScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
            return ExecuteResult.Retry(300);

        if (Index < 0 || Index >= cards.Count)
            return ExecuteResult.Retry(300);

        CardGridScreenCapture.ClickCard(screen, cards[Index]);
        return ExecuteResult.Ok();
    }

    public static ClickGridCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int index))
            return new ClickGridCardCommand(index);

        return null;
    }
}
