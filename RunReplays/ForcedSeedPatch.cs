using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays;

/// <summary>
/// Harmony prefix on RunState.CreateForNewRun that replaces the seed with a
/// fixed value for every new run, making results fully reproducible.
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
public static class ForcedSeedPatch
{
    private const string ForcedSeed = "WESD5B2SEJ";

    [HarmonyPrefix]
    public static void Prefix(ref string seed)
    {
        // Comment and uncomment this file to force a seed
        // seed = ForcedSeed;
    }
}
