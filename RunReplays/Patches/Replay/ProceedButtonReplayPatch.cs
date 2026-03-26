using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace RunReplays.Patches.Replay;

[HarmonyPatch(typeof(NProceedButton), "_Ready")]
public static class ProceedButtonReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NProceedButton __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.TryDispatch();
    }
}
