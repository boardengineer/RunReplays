using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using RunReplays.Utils;

namespace RunReplays.Patches.Replay;
using RunReplays;

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
public static class RestSiteReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(RestSiteSynchronizer __instance)
    {
        RngCheckpointLogger.Log("RestSite (BeginRestSite)");

        if (!ReplayEngine.IsActive)
            return;

        ReplayState.ActiveRestSiteSynchronizer = __instance;
        ReplayDispatcher.DispatchNow();
    }
}
