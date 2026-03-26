using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using RunReplays.Commands;

namespace RunReplays.Patches.Replay;
using RunReplays;

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
public static class CardRewardReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCardRewardSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayState.CardRewardSelectionScreen = __instance;
        CardRewardCommand.waitingForRewardScreenOpen = false;
    }
}