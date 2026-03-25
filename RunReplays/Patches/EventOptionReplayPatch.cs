using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using RunReplays.Utils;

namespace RunReplays.Patches;
using RunReplays;

/// <summary>
/// Harmony postfix on EventSynchronizer.BeginEvent that drives automatic event
/// option selection during replay for non-ancient events.
///
/// Ancient (Neow) events are handled separately by StartingBonusReplayPatch.
///
/// Multi-page events are handled by re-checking after each ChooseLocalOption call:
/// if the next queued command is another ChooseEventOption, AutoSelect runs again
/// on the next Godot frame, by which time the synchronous part of the previous
/// EventOption.Chosen() (including any SetEventState page transition) will have run.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
public static class EventOptionReplayPatch
{
    internal static EventSynchronizer? _activeSynchronizer;

    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, EventModel canonicalEvent)
    {
        bool isAncient = canonicalEvent is AncientEventModel;
        bool replayActive = ReplayEngine.IsActive;

        if (replayActive)
            ReplayDispatcher.TryDispatch();

        RngCheckpointLogger.Log($"Event (BeginEvent '{canonicalEvent.GetType().Name}')");

        ReplayEngine.PeekNext(out string? nextCmd);
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] BeginEvent — event='{canonicalEvent.GetType().Name}' isAncient={isAncient} replayActive={replayActive} nextCmd='{nextCmd}'");

        _activeSynchronizer = __instance;
        ReplayDispatcher.DispatchNow();
    }

}
