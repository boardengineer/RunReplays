using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using RunReplays.Commands;

namespace RunReplays.Patches;

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled))]
public static class MapChoiceReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMapScreen __instance, bool enabled)
    {
        if (!enabled || !__instance.IsTravelEnabled)
            return;

        MapMoveCommand._activeScreen = __instance;
        ReplayDispatcher.TryDispatch();
    }
}
