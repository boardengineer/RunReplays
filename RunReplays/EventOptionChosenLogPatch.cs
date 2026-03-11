using HarmonyLib;
using MegaCrit.Sts2.Core.Events;

namespace RunReplays;

/// <summary>
/// Harmony prefix on EventOption.Chosen that logs to the dev console whenever
/// an event option's action is executed, including its title and text key.
/// </summary>
[HarmonyPatch(typeof(EventOption), nameof(EventOption.Chosen))]
public static class EventOptionChosenLogPatch
{
    [HarmonyPrefix]
    public static void Prefix(EventOption __instance)
    {
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOption] Chosen — title='{__instance.Title.GetFormattedText()}' textKey='{__instance.TextKey}'");
    }
}
