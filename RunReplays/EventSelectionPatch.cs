using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays;

/// <summary>
/// Harmony prefix on EventSynchronizer.ChooseLocalOption(int index) that records
/// random-event option selections into the action buffer.
///
/// A prefix is used so that CurrentOptions still reflects the page the player
/// saw at the moment of selection (multi-page events transition their options
/// after the choice executes, so a postfix would capture the wrong page).
///
/// AncientEventModel events (Neow and other starting-bonus ancients) are
/// skipped here — they are already handled by StartingBonusPatch.
///
/// Verbose: full annotated block showing the event name, every visible option,
///          and a [CHOSEN] marker next to the selected one.
/// Minimal: single summary line "ChooseEventOption {index}".
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
public static class EventSelectionPatch
{
    [HarmonyPrefix]
    public static void Prefix(EventSynchronizer __instance, int index)
    {
        if (__instance.Events.Count == 0)
            return;

        // Events[0] is the local player's event model (single-player always has one).
        EventModel eventModel = __instance.Events[0];

        // Skip Ancient events — StartingBonusPatch already records these.
        if (eventModel is AncientEventModel)
            return;

        var options = eventModel.CurrentOptions;
        if (index < 0 || index >= options.Count)
            return;

        string eventTitle = eventModel.Title.GetFormattedText();

        // Verbose: annotated block with all visible options.
        PlayerActionBuffer.RecordVerboseOnly($"--- Event: {eventTitle} ---");
        for (int i = 0; i < options.Count; i++)
        {
            string marker = i == index ? "[CHOSEN]" : "[      ]";
            string optTitle = options[i].Title.GetFormattedText();
            PlayerActionBuffer.RecordVerboseOnly($"{marker} {i}: {optTitle}");
        }
        PlayerActionBuffer.RecordVerboseOnly("---");

        // Minimal: compact single-line summary.
        PlayerActionBuffer.RecordMinimalOnly($"ChooseEventOption {index}");
    }
}
