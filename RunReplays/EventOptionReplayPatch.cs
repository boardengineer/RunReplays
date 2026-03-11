using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

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
    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, EventModel canonicalEvent)
    {
        bool isAncient = canonicalEvent is AncientEventModel;
        bool replayActive = ReplayEngine.IsActive;
        ReplayEngine.PeekNext(out string? nextCmd);
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] BeginEvent — event='{canonicalEvent.GetType().Name}' isAncient={isAncient} replayActive={replayActive} nextCmd='{nextCmd}'");

        if (!ReplayEngine.PeekEventOption(out string peekedKey))
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[EventOptionReplayPatch] No ChooseEventOption pending — skipping auto-select.");
            return;
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] Deferring AutoSelect for textKey='{peekedKey}'.");
        Callable.From(() => AutoSelect(__instance)).CallDeferred();
    }

    private static void AutoSelect(EventSynchronizer synchronizer)
    {
        int eventCount = synchronizer.Events.Count;
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — Events.Count={eventCount}");

        if (eventCount == 0)
            return;

        EventModel eventModel = synchronizer.Events[0];
        bool isFinished = eventModel.IsFinished;
        var options = eventModel.CurrentOptions;
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — event='{eventModel.Title.GetFormattedText()}' isFinished={isFinished} options={options.Count}");

        if (isFinished)
            return;

        if (!ReplayEngine.PeekEventOption(out string textKey))
        {
            PlayerActionBuffer.LogToDevConsole(
                "[EventOptionReplayPatch] AutoSelect — no ChooseEventOption in queue.");
            return;
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — looking for textKey='{textKey}' among {options.Count} options: [{string.Join(", ", options.Select(o => o.TextKey))}]");

        int index = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].TextKey == textKey)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[EventOptionReplayPatch] No option with textKey='{textKey}' found — aborting.");
            return;
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — selecting index={index} (textKey='{textKey}').");
        ReplayRunner.ExecuteEventOption(out _);
        synchronizer.ChooseLocalOption(index);

        Callable.From(() => ContinueIfNeeded(synchronizer)).CallDeferred();
    }

    private static void ContinueIfNeeded(EventSynchronizer synchronizer)
    {
        ReplayEngine.PeekNext(out string? nextCmd);
        int eventCount = synchronizer.Events.Count;
        bool finished = eventCount > 0 && synchronizer.Events[0].IsFinished;
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] ContinueIfNeeded — Events.Count={eventCount} finished={finished} nextCmd='{nextCmd}'");

        if (!ReplayEngine.PeekEventOption(out _))
            return;

        if (eventCount == 0)
            return;

        if (finished)
            return;

        AutoSelect(synchronizer);
    }
}
