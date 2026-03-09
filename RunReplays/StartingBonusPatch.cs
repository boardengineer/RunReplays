using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace RunReplays;

/// <summary>
/// Harmony postfix on AncientEventModel.Done() that records the full set of
/// starting-bonus options — and which one was chosen — into the action buffer.
///
/// Done() is called after the player makes their choice, so WasChosen is
/// already set on each option. GeneratedOptions (the options actually shown,
/// not every possible option) is read via reflection since it is private.
/// </summary>
[HarmonyPatch(typeof(AncientEventModel), "Done")]
public static class StartingBonusPatch
{
    private static readonly PropertyInfo? GeneratedOptionsProperty =
        typeof(AncientEventModel).GetProperty(
            "GeneratedOptions",
            BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(AncientEventModel __instance)
    {
        if (__instance is not Neow)
            return;

        if (GeneratedOptionsProperty?.GetValue(__instance) is not List<EventOption> options)
            return;

        int chosenIndex = options.FindIndex(o => o.WasChosen);

        // Verbose: full annotated block visible in the dev console and verbose log.
        PlayerActionBuffer.RecordVerboseOnly("--- Starting Bonus Options ---");
        for (int i = 0; i < options.Count; i++)
        {
            string marker = options[i].WasChosen ? "[CHOSEN]" : "[      ]";
            string title  = options[i].Title.GetFormattedText();
            PlayerActionBuffer.RecordVerboseOnly($"{marker} {i}: {title}");
        }
        PlayerActionBuffer.RecordVerboseOnly("------------------------------");

        // Minimal: single summary line with the chosen index.
        PlayerActionBuffer.RecordMinimalOnly($"ChooseStartingBonus {chosenIndex}");
    }
}
