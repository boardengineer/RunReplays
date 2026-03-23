using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace RunReplays.Commands;

/// <summary>
/// Hand-card selection command.  When the game opens the hand selection UI
/// (via CardSelectCmd.FromHand / FromHandForDiscard), a Harmony postfix on
/// NPlayerHand.SelectCards captures the hand instance.  Execute() then maps
/// the recorded hand indices to cards and resolves the hand's
/// _selectionCompletionSource directly — bypassing the UI entirely.
/// </summary>
public sealed class SelectHandCardsCommand : ReplayCommand
{
    private const string Prefix = "SelectHandCards ";

    /// <summary>Hand-position indices of the selected cards.</summary>
    public int[] HandIndices { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.None;
    public override bool IsSelectionCommand => true;

    private SelectHandCardsCommand(string raw, int[] handIndices) : base(raw)
    {
        HandIndices = handIndices;
    }

    public override string Describe()
    {
        string idxStr = HandIndices.Length > 0 ? string.Join(", ", HandIndices) : "(none)";
        return $"select hand cards [{idxStr}]";
    }

    public override ExecuteResult Execute()
    {
        var hand = HandSelectionCapture.ActiveHand;
        if (hand == null)
            return ExecuteResult.Retry(100);

        var tcs = HandSelectionCapture.CompletionSourceField?.GetValue(hand)
            as TaskCompletionSource<IEnumerable<CardModel>>;
        if (tcs == null || tcs.Task.IsCompleted)
            return ExecuteResult.Retry(100);

        // Get the player's hand cards to map indices.
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        if (combatState == null)
            return ExecuteResult.Retry(100);

        var player = LocalContext.GetMe(combatState)
                     ?? combatState.Players.FirstOrDefault();
        var handCards = player?.PlayerCombatState?.Hand?.Cards;
        if (handCards == null)
            return ExecuteResult.Retry(100);

        var selected = new List<CardModel>();
        foreach (int idx in HandIndices)
        {
            if (idx >= 0 && idx < handCards.Count)
            {
                selected.Add(handCards[idx]);
                PlayerActionBuffer.LogMigrationWarning(
                    $"[SelectHandCards] Selected '{handCards[idx].Title}' at hand index {idx}.");
            }
            else
            {
                PlayerActionBuffer.LogMigrationWarning(
                    $"[SelectHandCards] Index {idx} out of range (hand={handCards.Count}).");
            }
        }

        tcs.TrySetResult(selected);
        HandSelectionCapture.ActiveHand = null;
        return ExecuteResult.Ok();
    }

    public static SelectHandCardsCommand? TryParse(string raw)
    {
        if (raw.StartsWith(Prefix))
        {
            string rest = raw.Substring(Prefix.Length).Trim();
            if (rest.Length == 0)
                return new SelectHandCardsCommand(raw, System.Array.Empty<int>());

            var parts = rest.Split(' ');
            var indices = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx))
                    indices.Add(idx);
                else
                    return null;
            }
            return new SelectHandCardsCommand(raw, indices.ToArray());
        }

        if (raw == "SelectHandCards")
            return new SelectHandCardsCommand(raw, System.Array.Empty<int>());

        return null;
    }
}

/// <summary>
/// Captures NPlayerHand instances when they enter card selection mode
/// and provides reflection access to resolve the selection directly.
///
/// NPlayerHand.SelectCards creates a _selectionCompletionSource that drives
/// the selection UI.  By resolving it ourselves (TrySetResult), we bypass
/// the UI entirely — matching the pattern used by CardGridScreenCapture
/// for deck card selection.
/// </summary>
[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
public static class HandSelectionCapture
{
    internal static readonly FieldInfo? CompletionSourceField =
        typeof(NPlayerHand).GetField(
            "_selectionCompletionSource", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NPlayerHand? ActiveHand;

    [HarmonyPostfix]
    public static void Postfix(NPlayerHand __instance)
    {
        if (!ReplayEngine.IsActive) return;
        ActiveHand = __instance;
        PlayerActionBuffer.LogMigrationWarning(
            $"[HandSelectionCapture] Hand captured for selection.");
        ReplayDispatcher.DispatchNow();
    }

    internal static void Clear()
    {
        ActiveHand = null;
    }
}
