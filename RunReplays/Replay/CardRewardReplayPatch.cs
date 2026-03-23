using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Commands;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NCardRewardSelectionScreen._Ready that, when a replay is
/// active and the next command is a TakeCardReward or SacrificeCardReward entry,
/// defers an automatic card selection (or sacrifice) to the next Godot frame.
///
/// _Ready() is the hook because:
///   - It is a plain non-async method that Harmony can patch reliably.
///     Patching async methods (e.g. CardsSelected) is known to cause Harmony
///     to throw during PatchAll, which would abort all patch registration.
///   - _Ready() fires when the screen enters the tree, and the game calls
///     CardsSelected() synchronously in the same frame right after Push().
///     CardsSelected() creates _completionSource before its first await, so
///     by the time our deferred callback runs (next frame) _completionSource
///     is already in place and SelectCard() can complete the Task safely.
///
/// After the card is selected, BattleRewardsReplayPatch.OnCardRewardHandled()
/// is called so that any subsequent reward buttons on NRewardsScreen
/// (e.g. gold that follows a card reward) can be processed.
///
/// Card holders are found by searching the screen's full descendant tree for
/// nodes that expose a CardModel property with the expected title. This avoids
/// any dependency on private field names (_grid etc.) that differ between
/// NCardRewardSelectionScreen and other selection screen types.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
public static class CardRewardReplayPatch
{
    private static readonly FieldInfo? ExtraOptionsField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NCardRewardSelectionScreen? selectionScreen;

    [HarmonyPostfix]
    public static void Postfix(NCardRewardSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

       selectionScreen = __instance;
        CardRewardCommand.waitingForRewardScreenOpen = false;
        
        if (ReplayEngine.PeekSacrificeCardReward())
        {
            Callable.From(() => AutoSacrifice(__instance)).CallDeferred();
            return;
        }
    }

    private static void AutoSacrifice(NCardRewardSelectionScreen screen)
    {
        if (!ReplayEngine.IsActive)
            return;

        // Read _extraOptions to find the sacrifice alternative.
        var extras = ExtraOptionsField?.GetValue(screen)
            as System.Collections.Generic.IReadOnlyList<CardRewardAlternative>;

        if (extras == null || extras.Count == 0)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[RunReplays] Replay: SacrificeCardReward — no extra options on screen.");
            return;
        }

        // Find the sacrifice alternative (PaelsWing.sacrificeAlternativeKey).
        // Fall back to the first alternative if only one exists.
        CardRewardAlternative? sacrifice = null;
        foreach (var alt in extras)
        {
            if (alt.OptionId.Contains("sacrifice", System.StringComparison.OrdinalIgnoreCase)
                || alt.OptionId.Contains("pael", System.StringComparison.OrdinalIgnoreCase))
            {
                sacrifice = alt;
                break;
            }
        }
        sacrifice ??= extras[0];

        ReplayRunner.ExecuteSacrificeCardReward();

        // The sacrifice logic lives in sacrifice.OnSelect (a Func<Task> that
        // calls OnSacrificeSynchronized → SyncLocalPaelsWingSacrifice + OnSacrifice).
        // OnAlternateRewardSelected only handles the post-action (dismiss screen)
        // and does NOT invoke OnSelect.  We must call OnSelect ourselves, then
        // invoke OnAlternateRewardSelected to dismiss the screen.
        var onSelectTask = sacrifice.OnSelect();
        MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(onSelectTask);
        PlayerActionBuffer.LogToDevConsole(
            $"[RunReplays] Replay: invoked sacrifice OnSelect (OptionId='{sacrifice.OptionId}').");

        var method = typeof(NCardRewardSelectionScreen).GetMethod(
            "OnAlternateRewardSelected",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (method != null)
        {
            method.Invoke(screen, new object[] { sacrifice.AfterSelected });
            PlayerActionBuffer.LogToDevConsole(
                "[RunReplays] Replay: invoked OnAlternateRewardSelected to dismiss screen.");
        }

        selectionScreen = null;
        BattleRewardsReplayPatch.OnCardRewardHandled();
    }

    /// <summary>
    /// Returns the first node in <paramref name="nodes"/> whose CardModel title
    /// matches <paramref name="expectedTitle"/>, or null if none is found.
    /// </summary>
    private static Node? FindHolderByTitle(
        Godot.Collections.Array<Node> nodes, string expectedTitle)
    {
        foreach (Node node in nodes)
        {
            PropertyInfo? prop = node.GetType().GetProperty(
                "CardModel", BindingFlags.Public | BindingFlags.Instance);

            if (prop?.GetValue(node) is not CardModel card)
                continue;

            if (card.Title == expectedTitle)
                return node;
        }
        return null;
    }

    internal static bool SelectCard(string expectedTitle)
    {
        // Guard: replay may have been cancelled between scheduling and execution.
        if (!ReplayEngine.IsActive)
            return false;

        if (selectionScreen == null)
            return false;

        // Search the full descendant tree for a card holder whose CardModel
        // title matches. No need to scope through a grid field — FindChildren
        // is recursive and handles any nesting depth.
        Node? match = FindHolderByTitle(selectionScreen.FindChildren("*", "", owned: false), expectedTitle);

        if (match == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: card '{expectedTitle}' not found in reward screen.");
            return false;
        }

        match.EmitSignal("Pressed", match);
        PlayerActionBuffer.LogToDevConsole($"[RunReplays] Replay: auto-selected card reward '{expectedTitle}'.");

        selectionScreen = null;
        return true;

    }
}