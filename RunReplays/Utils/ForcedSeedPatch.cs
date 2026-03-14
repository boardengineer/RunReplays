using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Utils;

/// <summary>
/// Harmony prefix on RunState.CreateForNewRun that replaces the seed with a
/// fixed value for every new run, making results fully reproducible.
/// </summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
public static class ForcedSeedPatch
{
    internal const string ForcedSeed = "C59SQWSQP6";
    internal const bool Enabled = false;

    [HarmonyPrefix]
    public static void Prefix(ref string seed)
    {
        if (Enabled)
            seed = ForcedSeed;
    }
}
