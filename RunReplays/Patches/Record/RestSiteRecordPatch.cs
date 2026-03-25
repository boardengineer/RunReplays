using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patches.Record;
using RunReplays.Commands;

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
public static class RestSiteRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(RestSiteSynchronizer __instance, int index)
    {
        IReadOnlyList<RestSiteOption> options = __instance.GetLocalOptions();

        if (index < 0 || index >= options.Count)
        {
            return;
        }

        string optionId = options[index].OptionId;
        PlayerActionBuffer.Record(new ChooseRestSiteOptionCommand(optionId).ToString());
    }
}
