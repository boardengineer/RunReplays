using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace RunReplays;

// Adding [ModInitializer] disables the game's auto-PatchAll, so we call
// harmony.PatchAll() explicitly here to pick up all [HarmonyPatch] classes.
[ModInitializer(nameof(Initialize))]
public static class ModEntryPoint
{
    private const string ModId = "RunReplays";

    public static void Initialize()
    {
        ModConfigRegistry.Register(ModId, new RunReplaysConfig());
        new Harmony(ModId).PatchAll();
    }
}
