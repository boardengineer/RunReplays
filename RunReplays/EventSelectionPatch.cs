using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays;

/// <summary>
/// Harmony prefix on EventSynchronizer.ChooseLocalOption(int index) that logs
/// the full set of visible options to the dev console for diagnostic purposes.
///
/// Recording to the action buffer is handled by EventOptionChosenLogPatch
/// (on EventOption.Chosen) which captures the stable TextKey rather than
/// the positional index, making replay more robust.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
public static class EventSelectionPatch
{
    [HarmonyPrefix]
    public static void Prefix(EventSynchronizer __instance, int index)
    {
        if (__instance.Events.Count == 0)
            return;

        EventModel eventModel = __instance.Events[0];

        var options = eventModel.CurrentOptions;
        if (index < 0 || index >= options.Count)
            return;

        string eventTitle = eventModel.Title.GetFormattedText();
        string chosenTitle = options[index].Title.GetFormattedText();
        PlayerActionBuffer.LogToDevConsole(
            $"[EventSelectionPatch] Event '{eventTitle}' — chose option {index}: '{chosenTitle}'.");
    }
}
