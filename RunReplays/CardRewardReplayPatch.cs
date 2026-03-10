using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NSimpleCardSelectScreen.CardsSelected() that, when a
/// replay is active and the next command is a TakeCardReward entry, defers an
/// automatic card selection to the next Godot frame.
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
/// The private _grid field (declared on the base NCardGridSelectionScreen) is
/// accessed via reflection. Card holders inside the grid expose their CardModel
/// through NCardHolder.CardModel (a public virtual property), so no reflection
/// is needed to read the card title. The selection is completed by emitting the
/// holder's "Pressed" signal, which NSimpleCardSelectScreen connects to its
/// private SelectCard(NCardHolder) handler during setup.
/// </summary>
[HarmonyPatch(typeof(NSimpleCardSelectScreen), "_Ready")]
public static class CardRewardReplayPatch
{
    // _grid is declared on NCardGridSelectionScreen (the direct base class).
    private static readonly FieldInfo? GridField =
        typeof(NSimpleCardSelectScreen).BaseType?.GetField(
            "_grid",
            BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NSimpleCardSelectScreen __instance)
    {
        if (!ReplayEngine.PeekCardReward(out string cardTitle))
            return;

        // Defer one frame: _completionSource is set (synchronous), but emitting
        // the signal now would fire SelectCard before the async continuation
        // that awaits CardsSelected() has had a chance to attach its handler.
        Callable.From(() => AutoSelectCard(__instance, cardTitle)).CallDeferred();
    }

    private static void AutoSelectCard(NSimpleCardSelectScreen screen, string expectedTitle)
    {
        // Guard: replay may have been cancelled between scheduling and execution.
        if (!ReplayEngine.IsActive)
            return;

        if (GridField?.GetValue(screen) is not Node grid)
        {
            GD.PrintErr("[RunReplays] Replay: could not access card grid via reflection.");
            return;
        }

        // Search direct children first; fall back to recursive search in case
        // the grid wraps holders inside an additional container node.
        Node? match = FindHolderByTitle(grid.GetChildren(), expectedTitle)
                   ?? FindHolderByTitle(grid.FindChildren("*", "", owned: false), expectedTitle);

        if (match == null)
        {
            GD.PrintErr($"[RunReplays] Replay: card '{expectedTitle}' not found in reward screen.");
            return;
        }

        // Consume before emitting: BattleRewardPatch.ObtainedCard fires
        // synchronously inside SelectCard and will record the action, but the
        // ReplayEngine queue should already be advanced past this entry.
        ReplayEngine.ConsumeCardReward(out _);

        // Emitting "Pressed" on the holder triggers NSimpleCardSelectScreen's
        // SelectCard(NCardHolder) handler, which completes the TaskCompletionSource
        // and causes CardsSelected() to return the chosen card.
        match.EmitSignal("Pressed", match);
        GD.Print($"[RunReplays] Replay: auto-selected card reward '{expectedTitle}'.");
    }

    /// <summary>
    /// Returns the first node in <paramref name="nodes"/> whose CardModel title
    /// matches <paramref name="expectedTitle"/>, or null if none is found.
    /// CardModel is accessed as a public virtual property on NCardHolder without
    /// reflection; we cast through object to avoid a hard compile-time dependency
    /// on the NCardHolder type from this namespace.
    /// </summary>
    private static Node? FindHolderByTitle(
        Godot.Collections.Array<Node> nodes, string expectedTitle)
    {
        foreach (Node node in nodes)
        {
            // CardModel is a public virtual property declared on NCardHolder.
            // Access it reflectively so this file has no compile-time dependency
            // on the MegaCrit.Sts2.Core.Nodes.Cards namespace.
            PropertyInfo? prop = node.GetType().GetProperty(
                "CardModel", BindingFlags.Public | BindingFlags.Instance);

            if (prop?.GetValue(node) is not CardModel card)
                continue;

            if (card.Title == expectedTitle)
                return node;
        }
        return null;
    }
}
