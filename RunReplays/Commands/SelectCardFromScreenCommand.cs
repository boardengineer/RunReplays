using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Commands;

/// <summary>
/// Select a card from a choose-a-card screen (Power Potion, Attack Potion, etc.).
/// Recorded as: "SelectCardFromScreen {index}"
/// Index -1 means skip (no card selected).
/// </summary>
public sealed class SelectCardFromScreenCommand : ReplayCommand
{
    private const string Prefix = "SelectCardFromScreen ";

    public int Index { get; }

    public override bool IsSelectionCommand => true;

    public SelectCardFromScreenCommand(int index) : base("")
    {
        Index = index;
    }

    public override string ToString()
        => $"{Prefix}{Index}";

    public override string Describe()
        => Index >= 0 ? $"select card from screen index={Index}" : "skip card selection";

    public override ExecuteResult Execute()
    {
        var screen = ChooseACardScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);
        
        if (Index < 0)
        {
            ChooseACardScreenCapture.EmitSkip(screen);
            ChooseACardScreenCapture.ActiveScreen = null;
            return ExecuteResult.Ok();
        }
        
        var holder = CardGridScreenCapture.FindCardHolderByIndex(screen, Index);
        if (holder == null)
            return ExecuteResult.Retry(300);

        CardModel? cardModel = holder.GetType()
            .GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(holder) as CardModel;
        

        PlayerActionBuffer.LogDispatcher(
            $"[SelectCardFromScreen] Selecting card '{cardModel?.Title}' at index {Index}.");

        ChooseACardScreenCapture.SelectHolder(screen, holder);
        if (cardModel != null)
            ChooseACardScreenCapture.ConfirmSelection(screen, new[] { cardModel });
        ChooseACardScreenCapture.ActiveScreen = null;
        return ExecuteResult.Ok();
    }

    public static SelectCardFromScreenCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int index))
            return new SelectCardFromScreenCommand(index);

        return null;
    }
}

/// <summary>
/// Captures NChooseACardSelectionScreen instances when CardsSelected is called.
/// </summary>
[HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.CardsSelected))]
public static class ChooseACardScreenCapture
{
    private static readonly MethodInfo? SelectHolderMethod =
        typeof(NChooseACardSelectionScreen).GetMethod(
            "SelectHolder", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? SkipButtonField =
        typeof(NChooseACardSelectionScreen).GetField(
            "_skipButton", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? CompletionSourceField =
        typeof(NChooseACardSelectionScreen).GetField(
            "_completionSource", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NChooseACardSelectionScreen? ActiveScreen;

    [HarmonyPrefix]
    public static void Prefix(NChooseACardSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive) return;
        ActiveScreen = __instance;
        Callable.From(ReplayDispatcher.TryDispatch).CallDeferred();
    }

    internal static void SelectHolder(NChooseACardSelectionScreen screen, Node holder)
    {
        SelectHolderMethod?.Invoke(screen, new object[] { holder });
    }

    internal static void ConfirmSelection(NChooseACardSelectionScreen screen, IEnumerable<CardModel> cards)
    {
        var tcs = CompletionSourceField?.GetValue(screen)
            as TaskCompletionSource<IEnumerable<CardModel>>;
        tcs?.TrySetResult(cards);
    }

    internal static void EmitSkip(NChooseACardSelectionScreen screen)
    {
        var tcs = CompletionSourceField?.GetValue(screen)
            as TaskCompletionSource<IEnumerable<CardModel>>;
        tcs?.TrySetResult(Array.Empty<CardModel>());
    }

    internal static void Clear()
    {
        ActiveScreen = null;
    }
}
