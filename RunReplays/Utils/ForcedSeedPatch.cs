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
    internal static readonly string ForcedSeed = "C59SQWSQP6";
    internal static readonly bool Enabled = false;

    [HarmonyPrefix]
    public static void Prefix(ref string seed)
    {
        if (!Enabled) return;
        seed = ForcedSeed;
    }
}
