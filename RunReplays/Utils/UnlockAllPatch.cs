using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace RunReplays.Utils;

/// <summary>
/// Patches SaveManager.GenerateUnlockStateFromProgress to always return
/// UnlockState.all, unlocking all characters, cards, relics, and potions.
/// </summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.GenerateUnlockStateFromProgress))]
public static class UnlockAllPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref UnlockState __result)
    {
        __result = UnlockState.all;
    }
}
