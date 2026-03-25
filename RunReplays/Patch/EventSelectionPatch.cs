using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patch;

/// <summary>
///     Harmony prefix on EventSynchronizer.ChooseLocalOption(int index) that records
///     the chosen option to the action buffer.
///     Format: "ChooseEventOption {index} {textKey}"
///     The index ensures correct replay even when multiple options share a TextKey
///     (e.g. "The Future of Potions?" event).
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
public static class EventSelectionPatch
{
    /// <summary>
    ///     The 0-based index of the option being chosen, captured by the Prefix
    ///     and consumed by EventOptionChosenLogPatch.
    /// </summary>
    internal static int? PendingIndex;

    [HarmonyPrefix]
    public static void Prefix(EventSynchronizer __instance, int index)
    {
        if (__instance.Events.Count == 0)
            return;

        var eventModel = __instance.Events[0];

        var options = eventModel.CurrentOptions;
        if (index < 0 || index >= options.Count)
            return;

        var textKey = options[index].TextKey;
        var title = options[index].Title.GetFormattedText();
        var eventTitle = eventModel.Title.GetFormattedText();
        PlayerActionBuffer.LogToDevConsole(
            $"[EventSelectionPatch] Event '{eventTitle}' — chose option {index}: '{title}' (textKey='{textKey}').");

        // Store the index for EventOptionChosenLogPatch to include in its recording.
        PendingIndex = index;
    }
}