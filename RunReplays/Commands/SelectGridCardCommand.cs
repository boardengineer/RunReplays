using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Commands;

/// <summary>
/// Select one or more cards from a grid selection screen (NCardGridSelectionScreen).
/// Unified command for deck selection, card removal, upgrade, and simple grid picks.
///
/// Recorded as: "SelectGridCard {idx0} {idx1} ..."
/// Legacy:      "SelectDeckCard {idx...}", "RemoveCardFromDeck: {idx...}",
///              "SelectSimpleCard {idx}", "UpgradeCard {idx}"
/// </summary>
public class SelectGridCardCommand : ReplayCommand
{
    private const string Prefix = "SelectGridCard ";

    public int[] Indices { get; }

    public override bool IsSelectionCommand => true;

    public SelectGridCardCommand(int[] indices) : base("")
    {
        Indices = indices;
    }

    public override string ToString()
        => $"{Prefix}{string.Join(" ", Indices)}";

    public override string Describe()
    {
        string idxStr = Indices.Length > 0 ? string.Join(", ", Indices) : "(none)";
        return $"select grid card indices=[{idxStr}]";
    }

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = CardGridScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
            return ExecuteResult.Retry(300);

        var selected = new List<CardModel>();
        foreach (int idx in Indices)
        {
            if (idx < 0 || idx >= cards.Count)
                return ExecuteResult.Retry(300);

            CardGridScreenCapture.ClickCard(screen, cards[idx]);
            selected.Add(cards[idx]);
        }

        CardGridScreenCapture.ConfirmSelection(screen, selected);
        CardGridScreenCapture.ActiveScreen = null;
        return ExecuteResult.Ok();
    }

    public static SelectGridCardCommand? TryParse(string raw)
    {
        // New format: "SelectGridCard {idx0} {idx1} ..."
        if (raw.StartsWith(Prefix))
            return ParseIndices(raw, Prefix.Length);

        // Legacy formats
        if (raw.StartsWith("SelectDeckCard "))
            return ParseIndices(raw, "SelectDeckCard ".Length);
        if (raw.StartsWith("RemoveCardFromDeck: "))
            return ParseIndices(raw, "RemoveCardFromDeck: ".Length);
        if (raw.StartsWith("SelectSimpleCard "))
            return ParseIndices(raw, "SelectSimpleCard ".Length);
        if (raw.StartsWith("UpgradeCard "))
            return ParseIndices(raw, "UpgradeCard ".Length);

        return null;
    }

    private static SelectGridCardCommand? ParseIndices(string raw, int prefixLen)
    {
        string rest = raw.Substring(prefixLen).Trim();
        if (rest.Length == 0)
            return new SelectGridCardCommand(System.Array.Empty<int>());

        var parts = rest.Split(' ');
        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out int idx))
                indices.Add(idx);
            else
                return null;
        }
        return new SelectGridCardCommand(indices.ToArray());
    }
}
