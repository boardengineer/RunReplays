using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Patches.Replay;

namespace RunReplays.Commands;

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

    internal static void ClickCard(NCardGridSelectionScreen screen, CardModel card)
    {
        OnCardClickedMethod?.Invoke(screen, new object[] { card });
    }

    internal static void ConfirmSelection(NCardGridSelectionScreen screen, IEnumerable<CardModel> cards)
    {
        var tcs = CompletionSourceField?.GetValue(screen)
            as System.Threading.Tasks.TaskCompletionSource<IEnumerable<CardModel>>;
        tcs?.TrySetResult(cards);
    }

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
