using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

using RunReplays.Patches;
using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Select one or more cards from the deck.
/// Recorded as: "SelectDeckCard {idx0} {idx1} ..."
///
/// A Harmony postfix on NCardGridSelectionScreen._Ready captures the screen
/// instance when it enters the scene tree. Execute() reads _cards from the
/// captured screen, maps recorded indices to cards, and resolves the screen's
/// _completionSource directly — causing CardsSelected() to return immediately
/// with our selection.
/// </summary>
public class SelectDeckCardCommand : ReplayCommand
{
    private const string Prefix = "SelectDeckCard ";

    public int[] DeckIndices { get; }

    public override bool IsSelectionCommand => true;

    public SelectDeckCardCommand(int[] deckIndices) : base("")
    {
        DeckIndices = deckIndices;
    }

    public override string ToString()
        => $"{Prefix}{string.Join(" ", DeckIndices)}";

    public override string Describe()
    {
        string idxStr = DeckIndices.Length > 0 ? string.Join(", ", DeckIndices) : "(none)";
        return $"select deck card indices=[{idxStr}]";
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
        foreach (int idx in DeckIndices)
        {
            if (idx < 0 || idx >= cards.Count)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[SelectDeckCard] Index {idx} out of range (count={cards.Count}) — retrying.");
                return ExecuteResult.Retry(300);
            }

            CardGridScreenCapture.ClickCard(screen, cards[idx]);
            selected.Add(cards[idx]);
            PlayerActionBuffer.LogToDevConsole(
                $"[SelectDeckCard] Clicked card '{cards[idx].Title}' at index {idx}.");
        }

        CardGridScreenCapture.ConfirmSelection(screen, selected);
        CardGridScreenCapture.ActiveScreen = null;
        return ExecuteResult.Ok();
    }

    public static SelectDeckCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length).Trim();
        if (rest.Length == 0)
            return new SelectDeckCardCommand(System.Array.Empty<int>());

        var parts = rest.Split(' ');
        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out int idx))
                indices.Add(idx);
            else
                return null;
        }
        return new SelectDeckCardCommand(indices.ToArray());
    }
}

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

    internal static NCardGridSelectionScreen? ActiveScreen;

    [HarmonyPrefix]
    public static void Prefix(NCardGridSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive) return;
        ActiveScreen = __instance;
        UpgradeCardReplayPatch.selectionScreen = __instance;
        PlayerActionBuffer.LogDispatcher(
            $"[CardGridCapture] Screen captured: {__instance.GetType().Name}");
        ReplayDispatcher.DispatchNow();
    }

    /// <summary>
    /// Invokes OnCardClicked on the screen with the given card, simulating
    /// a player clicking that card in the grid.
    /// </summary>
    internal static void ClickCard(NCardGridSelectionScreen screen, CardModel card)
    {
        OnCardClickedMethod?.Invoke(screen, new object[] { card });
    }

    /// <summary>
    /// Resolves the screen's _completionSource with the given cards,
    /// confirming the selection and causing CardsSelected() to return.
    /// </summary>
    internal static void ConfirmSelection(NCardGridSelectionScreen screen, IEnumerable<CardModel> cards)
    {
        var tcs = CompletionSourceField?.GetValue(screen)
            as System.Threading.Tasks.TaskCompletionSource<IEnumerable<CardModel>>;
        tcs?.TrySetResult(cards);
    }

    /// <summary>
    /// Finds the Nth card holder child in the screen (nodes with a CardModel property).
    /// </summary>
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
