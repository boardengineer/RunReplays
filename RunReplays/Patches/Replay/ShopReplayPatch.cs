using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using RunReplays.Utils;

namespace RunReplays.Patches.Replay;
using RunReplays;

[HarmonyPatch(typeof(NMerchantRoom))]
public static class ShopReplayPatch
{
    [HarmonyPostfix]
    [HarmonyPatch("_Ready")]
    public static void ReadyPostfix(NMerchantRoom __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayState.ActiveMerchantRoom = __instance;
        ReplayDispatcher.TryDispatch();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NMerchantRoom.OpenInventory))]
    public static void OpenInventoryPostfix(NMerchantRoom __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.TryDispatch();
    }
}
