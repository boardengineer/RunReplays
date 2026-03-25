using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Commands;

/// <summary>
/// Select a card from a choose-a-card screen (Power Potion, Attack Potion, etc.).
/// Recorded as: "SelectCardFromScreen {index}"
/// Index -1 means skip (no card selected).
///
/// Uses ChooseACardScreenCapture to resolve the selection, mirroring
/// the CardGridScreenCapture pattern used by SelectDeckCardCommand.
/// </summary>
public sealed class SelectCardFromScreenCommand : ReplayCommand
{
    private const string Prefix = "SelectCardFromScreen ";

    public int Index { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.None;
    public override bool IsSelectionCommand => true;

    private SelectCardFromScreenCommand(string raw, int index) : base(raw)
    {
        Index = index;
    }

    public override string Describe()
        => Index >= 0 ? $"select card from screen index={Index}" : "skip card selection";

    public override ExecuteResult Execute()
    {
        var screen = ChooseACardScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = ChooseACardScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null)
            return ExecuteResult.Retry(300);

        if (Index < 0)
        {
            ChooseACardScreenCapture.ResolveSelection(Array.Empty<CardModel>());
            return ExecuteResult.Ok();
        }

        if (Index >= cards.Count)
            return ExecuteResult.Retry(300);

        ChooseACardScreenCapture.ResolveSelection(new[] { cards[Index] });
        return ExecuteResult.Ok();
    }

    public static SelectCardFromScreenCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int index))
            return new SelectCardFromScreenCommand(raw, index);

        return null;
    }
}

/// <summary>
/// Captures NChooseACardSelectionScreen instances when CardsSelected is called
/// and provides helpers to resolve their _completionSource directly.
/// Mirrors CardGridScreenCapture for NCardGridSelectionScreen.
/// </summary>
[HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.CardsSelected))]
public static class ChooseACardScreenCapture
{
    internal static readonly FieldInfo? CardsField =
        typeof(NChooseACardSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? CompletionSourceField =
        typeof(NChooseACardSelectionScreen).GetField(
            "_completionSource", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NChooseACardSelectionScreen? ActiveScreen;

    [HarmonyPrefix]
    public static void Prefix(NChooseACardSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive) return;
        ActiveScreen = __instance;
        PlayerActionBuffer.LogDispatcher(
            $"[ChooseACardCapture] Screen captured: {__instance.GetType().Name}");
        ReplayDispatcher.DispatchNow();
    }

    internal static bool ResolveSelection(IEnumerable<CardModel> cards)
    {
        if (ActiveScreen == null) return false;

        var tcs = CompletionSourceField?.GetValue(ActiveScreen)
            as TaskCompletionSource<IEnumerable<CardModel>>;
        if (tcs == null) return false;

        tcs.TrySetResult(cards);
        ActiveScreen = null;
        return true;
    }

    internal static void Clear()
    {
        ActiveScreen = null;
    }
}
