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

    private SelectDeckCardCommand(string raw, int[] deckIndices) : base(raw)
    {
        DeckIndices = deckIndices;
    }

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

        foreach (int idx in DeckIndices)
        {
            if (idx < 0 || idx >= cards.Count)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[SelectDeckCard] Index {idx} out of range (count={cards.Count}) — retrying.");
                return ExecuteResult.Retry(300);
            }
        }

        var selected = new List<CardModel>();
        foreach (int idx in DeckIndices)
        {
            selected.Add(cards[idx]);
            PlayerActionBuffer.LogToDevConsole(
                $"[SelectDeckCard] Selected '{cards[idx].Title}' at index {idx}.");
        }

        CardGridScreenCapture.ResolveSelection(selected);
        return ExecuteResult.Ok();
    }

    public static SelectDeckCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length).Trim();
        if (rest.Length == 0)
            return new SelectDeckCardCommand(raw, System.Array.Empty<int>());

        var parts = rest.Split(' ');
        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out int idx))
                indices.Add(idx);
            else
                return null;
        }
        return new SelectDeckCardCommand(raw, indices.ToArray());
    }
}

/// <summary>
/// Captures NCardGridSelectionScreen instances when they enter the scene tree
/// and provides helpers to resolve their _completionSource directly.
///
/// _completionSource is a protected TaskCompletionSource on NCardGridSelectionScreen.
/// CardsSelected() just awaits it. By calling SetResult ourselves, we bypass
/// the normal UI selection flow entirely.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class CardGridScreenCapture
{
    internal static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

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
    /// Resolves the captured screen's _completionSource with the given cards,
    /// causing CardsSelected() to return immediately with our selection.
    /// </summary>
    internal static bool ResolveSelection(IEnumerable<CardModel> cards)
    {
        if (ActiveScreen == null) return false;

        var tcs = CompletionSourceField?.GetValue(ActiveScreen)
            as TaskCompletionSource<IEnumerable<CardModel>>;
        if (tcs == null) return false;

        tcs.TrySetResult(cards);
        ReplayState.EnqueueScreenCleanup(ActiveScreen);
        ActiveScreen = null;
        return true;
    }

    internal static void Clear()
    {
        ActiveScreen = null;
    }
}
