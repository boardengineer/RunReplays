using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using RunReplays.Utils;

namespace RunReplays.Patches;
using RunReplays;

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

        // Log the event's own Rng counter (used for card generation in events
        // like Slippery Bridge) alongside the textKey.
        string eventRngInfo = "";
        try
        {
            var eventModel = typeof(EventOption)
                .GetField("_eventModel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(__instance);
            if (eventModel == null)
            {
                // Try constructor-stored field or property
                var prop = typeof(EventOption).GetProperty("EventModel",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                eventModel = prop?.GetValue(__instance);
            }
            if (eventModel is MegaCrit.Sts2.Core.Models.EventModel em && em.Rng != null)
                eventRngInfo = $" eventRng.Counter={em.Rng.Counter}";
        }
        catch { /* ignore */ }

        string desc = "";
        try { desc = __instance.Description?.GetFormattedText() ?? ""; }
        catch { /* ignore */ }

        int? idx = EventSelectionPatch.PendingIndex;
        EventSelectionPatch.PendingIndex = null;

        PlayerActionBuffer.RecordVerboseOnly($"[EventOption] Chosen — title='{title}' textKey='{textKey}' index={idx}");
        PlayerActionBuffer.RecordMinimalOnly(idx.HasValue
            ? $"ChooseEventOption {idx.Value} {textKey}"
            : $"ChooseEventOption {textKey}");
    }
}
