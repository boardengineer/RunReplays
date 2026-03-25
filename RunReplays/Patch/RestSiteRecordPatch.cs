using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patch;

/// <summary>
///     Harmony prefix on RestSiteSynchronizer.ChooseLocalOption that records the
///     chosen rest site option into the action buffer.
///     ChooseLocalOption(int index) is called whenever the local player selects a
///     rest site option.  GetLocalOptions() returns the current option list so we
///     can resolve the index to a RestSiteOption and capture its OptionId — a
///     stable uppercase string identifier (e.g. "HEAL", "SMITH") that is used both
///     here for recording and by RestSiteReplayPatch for replay execution.
///     The prefix fires synchronously before the async body, so GetLocalOptions()
///     still holds the full option list at recording time — the selected option is
///     only removed after OnSelect() completes inside ChooseOption.
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
public static class RestSiteRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(RestSiteSynchronizer __instance, int index)
    {
        var options = __instance.GetLocalOptions();

        if (index < 0 || index >= options.Count)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RestSiteRecordPatch] ChooseLocalOption index {index} out of range (count={options.Count}).");
            return;
        }

        var optionId = options[index].OptionId;
        PlayerActionBuffer.LogToDevConsole(
            $"[RestSiteRecordPatch] ChooseLocalOption index={index} optionId='{optionId}'.");
        PlayerActionBuffer.Record($"ChooseRestSiteOption {optionId}");
    }
}