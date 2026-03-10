using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays;

/// <summary>
/// Harmony postfix on EventSynchronizer.BeginEvent that, when a replay is active
/// and the next command is a ChooseStartingBonus entry, defers an automatic option
/// selection to the next Godot frame.
///
/// BeginEvent is patched (rather than a UI hook) because it fires on the
/// EventSynchronizer that owns ChooseLocalOption, giving us a direct reference
/// to the object we need to call.  A single CallDeferred is sufficient here
/// because BeginEvent is synchronous and the event options are populated before
/// it returns, so the deferred callback runs after the UI is ready.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class StartingBonusReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, EventModel canonicalEvent)
    {
        if (canonicalEvent is not AncientEventModel)
            return;

        if (!ReplayEngine.PeekStartingBonus(out int choiceIndex))
            return;

        Callable.From(() => AutoSelect(__instance, choiceIndex)).CallDeferred();
    }

    private static void AutoSelect(EventSynchronizer synchronizer, int choiceIndex)
    {
        if (!ReplayRunner.ExecuteStartingBonus(out _))
            return;

        synchronizer.ChooseLocalOption(choiceIndex);
    }
}
