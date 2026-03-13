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

        // Flush any pending deck card selection that was triggered by the previous
        // event option (e.g. Wood Carvings).  This ensures SelectDeckCard appears
        // before the next ChooseEventOption in the minimal log, since there is no
        // PlayCardAction to flush it in the event context.
        CardEffectDeckSelectContext.FlushIfPending();

        int? idx = EventSelectionPatch.PendingIndex;
        EventSelectionPatch.PendingIndex = null;

        PlayerActionBuffer.RecordVerboseOnly($"[EventOption] Chosen — title='{title}' textKey='{textKey}' index={idx}");
        PlayerActionBuffer.RecordMinimalOnly(idx.HasValue
            ? $"ChooseEventOption {idx.Value} {textKey}"
            : $"ChooseEventOption {textKey}");
    }
}
