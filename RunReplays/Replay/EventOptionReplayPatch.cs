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

namespace RunReplays;

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
    private static EventSynchronizer? _activeSynchronizer;

    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, EventModel canonicalEvent)
    {
        bool isAncient = canonicalEvent is AncientEventModel;
        bool replayActive = ReplayEngine.IsActive;

        if (replayActive)
            ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Event);

        ReplayEngine.PeekNext(out string? nextCmd);
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] BeginEvent — event='{canonicalEvent.GetType().Name}' isAncient={isAncient} replayActive={replayActive} nextCmd='{nextCmd}'");

        _activeSynchronizer = __instance;
        ReplayDispatcher.DispatchNow();
    }

    /// <summary>Called by ReplayDispatcher to trigger event option selection.</summary>
    internal static void DispatchFromEngine()
    {
        if (_activeSynchronizer == null)
        {
            PlayerActionBuffer.LogDispatcher("[Event] DispatchFromEngine: no active synchronizer.");
            return;
        }

        var synchronizer = _activeSynchronizer;

        // Check if the event is finished — consume the PROCEED command and advance.
        if (synchronizer.Events.Count > 0 && synchronizer.Events[0].IsFinished)
        {
            // Consume the ChooseEventOption PROCEED so it doesn't loop.
            if (ReplayEngine.PeekEventOption(out _, out _))
                ReplayRunner.ExecuteEventOption(out _);

            PlayerActionBuffer.LogDispatcher("[Event] Event finished — consumed PROCEED, calling NEventRoom.Proceed().");
            TaskHelper.RunSafely(NEventRoom.Proceed());
            DispatchAfterDelay();
            return;
        }

        // If the event option is PROCEED and the next command after it is a map
        // move, consume PROCEED and let the dispatcher advance to the map move.
        if (ReplayEngine.PeekEventOption(out string peekKey, out _)
            && peekKey.Contains("PROCEED", System.StringComparison.OrdinalIgnoreCase))
        {
            // Peek the command after PROCEED.
            var pending = ReplayEngine.PeekAhead(1);
            if (pending != null && pending.StartsWith("MoveToMapCoordAction "))
            {
                ReplayRunner.ExecuteEventOption(out _);
                PlayerActionBuffer.LogDispatcher($"[Event] Consumed PROCEED (map move follows) — advancing.");
                TaskHelper.RunSafely(NEventRoom.Proceed());
                DispatchAfterDelay();
                return;
            }
        }

        // Consume and execute the event option.
        if (!ReplayEngine.PeekEventOption(out string textKey, out int recordedIndex))
        {
            // Not ready yet — retry.
            PlayerActionBuffer.LogDispatcher("[Event] No ChooseEventOption at front — retrying.");
            NGame.Instance!.GetTree()!.CreateTimer(0.3).Connect(
                "timeout", Callable.From(() => ReplayDispatcher.DispatchNow()));
            return;
        }

        var options = synchronizer.Events[0].CurrentOptions;
        int index = -1;
        if (recordedIndex >= 0 && recordedIndex < options.Count
            && options[recordedIndex].TextKey == textKey)
        {
            index = recordedIndex;
        }
        else
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].TextKey == textKey)
                {
                    index = i;
                    break;
                }
            }
        }

        if (index < 0)
        {
            PlayerActionBuffer.LogDispatcher(
                $"[Event] Option '{textKey}' not found — retrying.");
            NGame.Instance!.GetTree()!.CreateTimer(0.3).Connect(
                "timeout", Callable.From(() => ReplayDispatcher.DispatchNow()));
            return;
        }

        ReplayRunner.ExecuteEventOption(out _);
        PlayerActionBuffer.LogDispatcher($"[Event] Consumed and selecting option '{textKey}' at index {index}.");
        synchronizer.ChooseLocalOption(index);

        // After the option is chosen, wait before checking if more event options follow.
        NGame.Instance!.GetTree()!.CreateTimer(0.5).Connect(
            "timeout", Callable.From(() =>
            {
                ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Event);
                ReplayDispatcher.DispatchNow();
            }));
    }

    private static void DispatchAfterDelay()
    {
        NGame.Instance!.GetTree()!.CreateTimer(0.5).Connect(
            "timeout", Callable.From(() => ReplayDispatcher.DispatchNow()));
    }
}
