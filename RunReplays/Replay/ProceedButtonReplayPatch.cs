using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NProceedButton._Ready that, when a replay is active and
/// the next command is a map move, defers opening the map to the next frame.
///
/// This handles rooms where the proceed button only appears after all in-room
/// actions are complete (rest sites, treasure rooms, etc.) — the button becoming
/// ready signals that the room is done and the map should open.
///
/// For the merchant shop the proceed button is present from the start, so
/// ShopOpenedReplayPatch.ProcessNextPurchase opens the map explicitly after all
/// purchases are consumed instead.
/// </summary>
[HarmonyPatch(typeof(NProceedButton), "_Ready")]
public static class ProceedButtonReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NProceedButton __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.TryDispatch();

        if (ShopOpenedReplayPatch.IsShopReplayActive)
            return;

        if (!ReplayEngine.PeekMapNode(out _, out _))
            return;

        Callable.From(OpenMap).CallDeferred();
        ReplayDispatcher.DispatchNow();
    }

    internal static void OpenMap()
    {
        NMapScreen.Instance?.Open();
        NMapScreen.Instance?.SetTravelEnabled(true);
    }
}
