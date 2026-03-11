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
        string title = __instance.Title.GetFormattedText();
        string textKey = __instance.TextKey;

        PlayerActionBuffer.LogToDevConsole(
            $"[EventOption] Chosen — title='{title}' textKey='{textKey}'");

        PlayerActionBuffer.RecordVerboseOnly($"[EventOption] Chosen — title='{title}' textKey='{textKey}'");
        PlayerActionBuffer.RecordMinimalOnly($"ChooseEventOption {textKey}");
    }
}
