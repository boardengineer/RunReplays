using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;

namespace RunReplays.Patches;
using RunReplays;

/// <summary>
/// Recording and replay patches for the fake merchant event shop.
///
/// The fake merchant event (NFakeMerchant) has its own OpenInventory method
/// separate from the regular NMerchantRoom.  When the player clicks the
/// merchant character, OnMerchantOpened fires, which calls OpenInventory.
///
/// Recording: prefix on NFakeMerchant.OpenInventory records "OpenFakeShop".
/// Replay:    postfix on NFakeMerchant.AfterRoomIsLoaded signals shop readiness
///            so the dispatcher can execute OpenFakeShopCommand and BuyRelicCommand.
/// </summary>

// ── Record when the fake merchant shop is opened ─────────────────────────────

[HarmonyPatch(typeof(NFakeMerchant), "OpenInventory")]
public static class FakeMerchantOpenRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (ReplayEngine.IsActive)
            return;

        PlayerActionBuffer.Record("OpenFakeShop");
    }
}

[HarmonyPatch(typeof(NFakeMerchant), "AfterRoomIsLoaded")]
public static class FakeMerchantReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NFakeMerchant __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayState.FakeMerchantInstance = __instance;
        ReplayState.ActiveMerchantRoom = null;
        ReplayDispatcher.DispatchNow();
    }
}
