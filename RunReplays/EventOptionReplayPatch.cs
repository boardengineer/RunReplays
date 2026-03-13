using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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

    private const int MaxRetries = 10;

    private static void AutoSelect(EventSynchronizer synchronizer, int retriesLeft = MaxRetries)
    {
        int eventCount = synchronizer.Events.Count;
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — Events.Count={eventCount} retriesLeft={retriesLeft}");

        if (eventCount == 0)
            return;

        EventModel eventModel = synchronizer.Events[0];
        bool isFinished = eventModel.IsFinished;
        var options = eventModel.CurrentOptions;
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — event='{eventModel.Title.GetFormattedText()}' isFinished={isFinished} options={options.Count}");

        // When the event is finished, NEventRoom.SetOptions creates a UI-level PROCEED
        // button that calls EventOption.Chosen() directly, bypassing ChooseLocalOption.
        // CurrentOptions is always empty at this point so a normal lookup would always
        // fail.  Use SkipToEventOption to drain any orphaned interleaved commands
        // (auto-processed during ChooseLocalOption) and consume the ChooseEventOption,
        // then call NEventRoom.Proceed() directly.  We call Proceed() regardless of
        // whether a ChooseEventOption was found — some events may not log one.
        if (isFinished)
        {
            ReplayEngine.SkipToEventOption(out string finishedKey);
            PlayerActionBuffer.LogToDevConsole(finishedKey.Length > 0
                ? $"[EventOptionReplayPatch] Event finished — consumed '{finishedKey}' and calling NEventRoom.Proceed()."
                : "[EventOptionReplayPatch] Event finished — no ChooseEventOption in queue; calling NEventRoom.Proceed() directly.");
            TaskHelper.RunSafely(NEventRoom.Proceed());
            return;
        }

        if (!ReplayEngine.PeekEventOption(out string textKey, out int recordedIndex))
        {
            PlayerActionBuffer.LogToDevConsole(
                "[EventOptionReplayPatch] AutoSelect — no ChooseEventOption at front of queue.");
            return;
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — looking for textKey='{textKey}' recordedIndex={recordedIndex} among {options.Count} options: [{string.Join(", ", options.Select(o => o.TextKey))}]");

        // Prefer the recorded index (new format) when it's valid.
        int index = -1;
        if (recordedIndex >= 0 && recordedIndex < options.Count)
        {
            index = recordedIndex;
        }
        else
        {
            // Fallback: match by textKey (legacy format or out-of-range index).
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
            string available = options.Count > 0
                ? string.Join(", ", options.Select(o => $"'{o.TextKey}'"))
                : "(none)";

            if (retriesLeft > 0)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[EventOptionReplayPatch] Option '{textKey}' not yet available (have: [{available}]) — retrying in 100 ms ({retriesLeft} left).");
                TaskHelper.RunSafely(RetryAfterDelay(synchronizer, retriesLeft - 1));
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[EventOptionReplayPatch] Option '{textKey}' not found after {MaxRetries} retries (have: [{available}]) — aborting.");
            }
            return;
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] AutoSelect — selecting index={index} (textKey='{textKey}').");
        ReplayRunner.ExecuteEventOption(out _);
        synchronizer.ChooseLocalOption(index);

        Callable.From(() => ContinueIfNeeded(synchronizer)).CallDeferred();
    }

    private static async Task RetryAfterDelay(EventSynchronizer synchronizer, int retriesLeft)
    {
        await Task.Delay(100);
        Callable.From(() => AutoSelect(synchronizer, retriesLeft)).CallDeferred();
    }

    private static void ContinueIfNeeded(EventSynchronizer synchronizer, int retriesLeft = MaxRetries)
    {
        ReplayEngine.PeekNext(out string? nextCmd);
        int eventCount = synchronizer.Events.Count;
        bool finished = eventCount > 0 && synchronizer.Events[0].IsFinished;
        PlayerActionBuffer.LogToDevConsole(
            $"[EventOptionReplayPatch] ContinueIfNeeded — Events.Count={eventCount} finished={finished} nextCmd='{nextCmd}'");

        if (eventCount == 0)
            return;

        // If finished, AutoSelect handles PROCEED (drains orphaned commands + Proceed()).
        if (finished)
        {
            AutoSelect(synchronizer);
            return;
        }

        if (ReplayEngine.PeekEventOption(out _))
        {
            AutoSelect(synchronizer);
            return;
        }

        // No event option at front of queue.  If one exists deeper (behind orphaned
        // interleaved commands), retry — either the event will finish asynchronously
        // (making finished=true next time) or another patch will consume the blocker.
        if (retriesLeft > 0 && ReplayEngine.HasPendingEventOption())
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[EventOptionReplayPatch] ContinueIfNeeded — pending event option behind interleaved commands; retrying ({retriesLeft} left).");
            TaskHelper.RunSafely(RetryContinueAfterDelay(synchronizer, retriesLeft - 1));
        }
    }

    private static async Task RetryContinueAfterDelay(EventSynchronizer synchronizer, int retriesLeft)
    {
        await Task.Delay(100);
        Callable.From(() => ContinueIfNeeded(synchronizer, retriesLeft)).CallDeferred();
    }
}
