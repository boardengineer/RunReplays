using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using RunReplays.Patches.Replay;

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

    public override bool IsSelectionCommand => true;

    public SelectHandCardsCommand(int[] handIndices) : base("")
    {
        HandIndices = handIndices;
    }

    public override string ToString()
        => HandIndices.Length > 0
            ? $"{Prefix}{string.Join(" ", HandIndices)}"
            : "SelectHandCards";

    public override string Describe()
    {
        string idxStr = HandIndices.Length > 0 ? string.Join(", ", HandIndices) : "(none)";
        return $"select hand cards [{idxStr}]";
    }

    public override ExecuteResult Execute()
    {
        var nHand = HandSelectionCapture.ActiveHand;
        if (nHand == null)
            return ExecuteResult.Retry(100);

        // Use the canonical Hand.Cards list (same list recording indexed into).
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        var player = LocalContext.GetMe(combatState!)
                     ?? combatState?.Players.FirstOrDefault();
        var handCards = player?.PlayerCombatState?.Hand?.Cards;
        if (handCards == null)
            return ExecuteResult.Retry(100);

        foreach (int idx in HandIndices)
        {
            if (idx < 0 || idx >= handCards.Count)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[SelectHandCards] Index {idx} out of range (count={handCards.Count}) — retrying.");
                return ExecuteResult.Retry(100);
            }

            // Get the card model, then find its UI holder.
            var cardModel = handCards[idx];
            var holder = nHand.GetCardHolder(cardModel);
            if (holder == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[SelectHandCards] No holder for card '{cardModel.Title}' at index {idx} — retrying.");
                return ExecuteResult.Retry(100);
            }

            HandSelectionCapture.PressHolder(nHand, holder);
        }

        HandSelectionCapture.ConfirmSelection(nHand);
        HandSelectionCapture.ActiveHand = null;
        return ExecuteResult.Ok();
    }

    public static SelectHandCardsCommand? TryParse(string raw)
    {
        if (raw.StartsWith(Prefix))
        {
            string rest = raw.Substring(Prefix.Length).Trim();
            if (rest.Length == 0)
                return new SelectHandCardsCommand(System.Array.Empty<int>());

            var parts = rest.Split(' ');
            var indices = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx))
                    indices.Add(idx);
                else
                    return null;
            }
            return new SelectHandCardsCommand(indices.ToArray());
        }

        if (raw == "SelectHandCards")
            return new SelectHandCardsCommand(System.Array.Empty<int>());

        return null;
    }
}

/// <summary>
/// Captures NPlayerHand instances when they enter card selection mode
/// and provides helpers to simulate card selection and confirmation.
/// </summary>
[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
public static class HandSelectionCapture
{
    private static readonly MethodInfo? OnHolderPressedMethod =
        typeof(NPlayerHand).GetMethod(
            "OnHolderPressed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? OnConfirmMethod =
        typeof(NPlayerHand).GetMethod(
            "OnSelectModeConfirmButtonPressed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly PropertyInfo? HoldersProp =
        typeof(NPlayerHand).GetProperty(
            "ActiveHolders", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NPlayerHand? ActiveHand;

    [HarmonyPostfix]
    public static void Postfix(NPlayerHand __instance)
    {
        if (!ReplayEngine.IsActive) return;
        ActiveHand = __instance;
        ReplayDispatcher.DispatchNow();
    }

    internal static IReadOnlyList<Godot.Node>? GetHolders(NPlayerHand hand)
    {
        return HoldersProp?.GetValue(hand) as IReadOnlyList<Godot.Node>;
    }

    internal static void PressHolder(NPlayerHand hand, Godot.Node holder)
    {
        OnHolderPressedMethod?.Invoke(hand, new object[] { holder });
    }

    internal static void ConfirmSelection(NPlayerHand hand)
    {
        OnConfirmMethod?.Invoke(hand, new object?[] { null });
    }

    internal static void Clear()
    {
        ActiveHand = null;
    }
}
