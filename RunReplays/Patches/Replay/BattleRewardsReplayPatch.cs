using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays.Patches.Replay;
using RunReplays;

/// <summary>
/// Harmony postfix on NRewardsScreen._Ready that captures the active screen
/// and triggers replay dispatch.
/// </summary>
[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
public static class BattleRewardsReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRewardsScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayState.ActiveRewardsScreen = __instance;
        ReplayDispatcher.DispatchNow();
    }
}
