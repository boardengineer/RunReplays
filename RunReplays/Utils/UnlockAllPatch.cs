using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
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
    // NOTE: The first parameter was renamed `string seed` → `Rng rng` when the
    // game moved seed-hashing earlier in the pipeline. The seed override used
    // to live here; that job now belongs to the StartNewSingleplayerRun prefix
    // in EncounterRngTracker (which rewrites `seed` before the RunRngSet is
    // constructed from it). This patch only forces UnlockState.all.
    [HarmonyPrefix]
    public static void Prefix(Rng rng, ref UnlockState unlockState, bool isMultiplayer)
    {
        DiagnosticLog.Write("Rng",
            $"GetRandomList prefix — rng.Seed={rng.Seed} rng.Counter={rng.Counter} " +
            $"isMultiplayer={isMultiplayer} unlockState.isAll={ReferenceEquals(unlockState, UnlockState.all)} " +
            $"activeSeed='{ReplayEngine.ActiveSeed}' forcedSeedEnabled={ForcedSeedPatch.Enabled}");

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
