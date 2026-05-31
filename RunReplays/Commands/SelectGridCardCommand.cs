using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using RunReplays.Patches.Replay;

namespace RunReplays.Commands;

/// <summary>
/// Select one or more cards from a grid selection screen (NCardGridSelectionScreen).
///
/// Recorded as: "SelectGridCard {idx0} {idx1} ..."
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
        var screen = CardGridScreenCapture.ActiveScreen
            ?? NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen;
        if (screen == null)
        {
            GD.Print("[RunReplays] [SelectGridCard] Retry: no active grid screen.");
            PlayerActionBuffer.LogDispatcher("[SelectGridCard] Retry: no active grid screen.");
            return ExecuteResult.Retry(300);
        }

        var cards = GetSelectableCards(screen);
        if (cards == null)
        {
            GD.Print($"[RunReplays] [SelectGridCard] Retry: could not read _cards from {screen.GetType().Name}.");
            PlayerActionBuffer.LogDispatcher(
                $"[SelectGridCard] Retry: could not read _cards from {screen.GetType().Name}.");
            return ExecuteResult.Retry(300);
        }

        var selected = new List<CardModel>();
        if (Indices.All(idx => idx < 0))
        {
            if (!TryInferNegativeSelection(cards, out int inferredIndex))
                return ExecuteResult.Retry(300);

            CardGridScreenCapture.ClickCard(screen, cards[inferredIndex]);
            selected.Add(cards[inferredIndex]);
            CardGridScreenCapture.ConfirmSelection(screen, selected);
            CardGridScreenCapture.ActiveScreen = null;
            return ExecuteResult.Ok();
        }

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

    private static bool TryInferNegativeSelection(
        IReadOnlyList<CardModel> cards,
        out int inferredIndex)
    {
        inferredIndex = -1;

        RunReplays.ReplayEngine.GetReplayContext(
            out _,
            out _,
            out IReadOnlyList<ReplayCommand> next);
        string? nextHand = next
            .Select(command => ExtractHandSnapshot(command.StateSuffix))
            .FirstOrDefault(hand => !string.IsNullOrWhiteSpace(hand));
        if (string.IsNullOrWhiteSpace(nextHand))
        {
            GD.Print("[RunReplays] [SelectGridCard] Could not infer -1 selection: no next hand snapshot.");
            PlayerActionBuffer.LogDispatcher("[SelectGridCard] Could not infer -1 selection: no next hand snapshot.");
            return false;
        }

        GD.Print(
            $"[RunReplays] [SelectGridCard] Inferring -1 selection from next hand [{nextHand}] and cards [{string.Join(", ", cards.Select(card => card.Title))}].");
        PlayerActionBuffer.LogDispatcher(
            $"[SelectGridCard] Inferring -1 selection from next hand [{nextHand}] and cards [{string.Join(", ", cards.Select(card => card.Title))}].");

        var currentHandTitles = CardPlayReplayPatch.ResolveLocalPlayer()
            ?.PlayerCombatState
            ?.Hand
            ?.Cards
            .Select(card => card.Title)
            .ToList() ?? new List<string>();

        for (int i = 0; i < cards.Count; i++)
        {
            string title = cards[i].Title;
            string normalizedTitle = NormalizeCardTitle(title);
            if (CountTitle(nextHand, title) > currentHandTitles.Count(handTitle =>
                    NormalizeCardTitle(handTitle) == normalizedTitle))
            {
                GD.Print($"[RunReplays] [SelectGridCard] Inferred -1 selection by hand delta as index {i} ({title}).");
                inferredIndex = i;
                return true;
            }
        }

        for (int i = 0; i < cards.Count; i++)
        {
            string title = cards[i].Title;
            string normalizedTitle = NormalizeCardTitle(title);
            string normalizedHand = NormalizeCardTitle(nextHand);
            if (nextHand.Contains(title, System.StringComparison.OrdinalIgnoreCase)
                || normalizedHand.Contains(normalizedTitle, System.StringComparison.OrdinalIgnoreCase))
            {
                GD.Print($"[RunReplays] [SelectGridCard] Inferred -1 selection as index {i} ({title}).");
                PlayerActionBuffer.LogDispatcher(
                    $"[SelectGridCard] Inferred -1 selection as index {i} ({title}).");
                inferredIndex = i;
                return true;
            }
        }

        GD.Print("[RunReplays] [SelectGridCard] Could not infer -1 selection: no card title matched.");
        PlayerActionBuffer.LogDispatcher("[SelectGridCard] Could not infer -1 selection: no card title matched.");
        return false;
    }

    private static string NormalizeCardTitle(string value)
        => new(value
            .Where(ch => char.IsLetterOrDigit(ch))
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static int CountTitle(string handSnapshot, string title)
    {
        string normalizedTitle = NormalizeCardTitle(title);
        return handSnapshot
            .Split(',')
            .Select(part => NormalizeCardTitle(part.Trim()))
            .Count(part => part == normalizedTitle);
    }

    private static string? ExtractHandSnapshot(string? stateSuffix)
    {
        if (string.IsNullOrWhiteSpace(stateSuffix))
            return null;

        const string handPrefix = "Hand: [";
        int start = stateSuffix.IndexOf(handPrefix, System.StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += handPrefix.Length;
        int end = stateSuffix.IndexOf(']', start);
        if (end < 0)
            return null;

        return stateSuffix[start..end];
    }

    private static IReadOnlyList<CardModel>? GetSelectableCards(NCardGridSelectionScreen screen)
    {
        if (CardGridScreenCapture.CardsField?.GetValue(screen) is IReadOnlyList<CardModel> cards
            && cards.Count > 0)
            return cards;

        var holderCards = new List<CardModel>();
        foreach (Node node in screen.FindChildren("*", "", owned: false))
        {
            var prop = node.GetType().GetProperty(
                "CardModel",
                System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
            if (prop?.GetValue(node) is CardModel card)
                holderCards.Add(card);
        }

        return holderCards;
    }

    public static SelectGridCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        return ParseIndices(raw, Prefix.Length);
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
