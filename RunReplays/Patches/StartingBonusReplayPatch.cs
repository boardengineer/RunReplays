using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patches;
using RunReplays;

/// <summary>
/// Harmony postfix on EventSynchronizer.BeginEvent for ancient (starting) events.
/// Signals StartingBonus readiness when an AncientEventModel is encountered.
/// The actual event option selection is handled by ChooseEventOptionCommand.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class StartingBonusReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, EventModel canonicalEvent)
    {
        if (canonicalEvent is not AncientEventModel)
            return;

        if (ReplayEngine.IsActive)
            ReplayDispatcher.TryDispatch();
    }
}
