using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Relics;

namespace RunReplays;

/// <summary>
/// Harmony prefix on Pomander.AfterObtained that logs to the dev console
/// when the Pomander relic is obtained, before the card-upgrade selection opens.
/// </summary>
[HarmonyPatch(typeof(Pomander), "AfterObtained")]
public static class PomanderObtainedLogPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        PlayerActionBuffer.LogToDevConsole("[RunReplays] Pomander obtained — card upgrade selection will open.");
    }
}
