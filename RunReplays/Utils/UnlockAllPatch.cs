using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
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

/// <summary>
/// Forces GetRandomList to use UnlockState.all so that the act pool/order
/// is deterministic regardless of player progression.
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GetRandomList))]
public static class GetRandomListUnlockPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref string seed, ref UnlockState unlockState)
    {
        PlayerActionBuffer.LogToDevConsole(
            $"[GetRandomListUnlockPatch] seed='{seed}' activeSeed='{ReplayEngine.ActiveSeed}' " +
            $"forcedSeedEnabled={ForcedSeedPatch.Enabled} forcedSeed='{ForcedSeedPatch.ForcedSeed}'");

        // Prefer the seed from the replay/save being loaded via the menu.
        // Fall back to the hardcoded forced seed if no active seed is set.
        if (ReplayEngine.ActiveSeed != null)
            seed = ReplayEngine.ActiveSeed;
        else if (ForcedSeedPatch.Enabled)
            seed = ForcedSeedPatch.ForcedSeed;
        unlockState = UnlockState.all;
    }
}

/// <summary>
/// Forces HasSeenEncounter to always return true so that discovery order
/// never reorders encounters — making the encounter list fully seed-deterministic.
/// </summary>
[HarmonyPatch(typeof(UnlockState), nameof(UnlockState.HasSeenEncounter))]
public static class HasSeenEncounterPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref bool __result)
    {
        __result = true;
    }
}
