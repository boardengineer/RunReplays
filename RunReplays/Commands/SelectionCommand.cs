using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Commands;

/// <summary>
/// A card selection command consumed inline by ICardSelector implementations
/// or dispatched via CardGridScreenCapture when the selection screen opens.
///
/// Covers: SelectCardFromScreen, SelectSimpleCard, UpgradeCard.
/// SelectHandCards is handled by SelectHandCardsCommand.
/// SelectDeckCard is handled by SelectDeckCardCommand.
/// RemoveCardFromDeck is handled by RemoveCardFromDeckCommand.
/// </summary>
public class SelectionCommand : ReplayCommand
{
    public enum SelectionKind
    {
        SelectCardFromScreen,
        SelectSimpleCard,
        UpgradeCard,
    }

    public SelectionKind Kind { get; }
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
            SelectionKind.SelectSimpleCard => $"select simple card index={idxStr}",
            SelectionKind.UpgradeCard => $"upgrade card at deck index {idxStr}",
            _ => "card selection",
        };
    }

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = CardGridScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
            return ExecuteResult.Retry(300);

        foreach (int idx in Indices)
        {
            if (idx < 0 || idx >= cards.Count)
                return ExecuteResult.Retry(300);
        }

        var selected = new List<CardModel>();
        foreach (int idx in Indices)
            selected.Add(cards[idx]);

        CardGridScreenCapture.ResolveSelection(selected);
        return ExecuteResult.Ok();
    }

    public static SelectionCommand? TryParse(string raw)
    {
        if (raw.StartsWith("SelectCardFromScreen "))
            return ParseSingleInt(raw, "SelectCardFromScreen ".Length, SelectionKind.SelectCardFromScreen);
        if (raw.StartsWith("SelectSimpleCard "))
            return ParseSingleInt(raw, "SelectSimpleCard ".Length, SelectionKind.SelectSimpleCard);
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
}
