using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RunReplays.Commands;

/// <summary>
/// Choose an event option by text key and optional recorded index.
/// Recorded as: "ChooseEventOption {index} {textKey}" (new format)
///          or: "ChooseEventOption {textKey}" (legacy format)
/// </summary>
public class ChooseEventOptionCommand : ReplayCommand
{
    private const string Prefix = "ChooseEventOption ";

    public string TextKey { get; }
    public int RecordedIndex { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Event;

    private ChooseEventOptionCommand(string raw, string textKey, int recordedIndex) : base(raw)
    {
        TextKey = textKey;
        RecordedIndex = recordedIndex;
    }

    public override string Describe() => $"choose event option '{TextKey}'";

    public override ExecuteResult Execute()
    {
        PlayerActionBuffer.LogDispatcher($"Should be executing event command?");
        
        var sync = EventOptionReplayPatch._activeSynchronizer;
        if (sync == null)
            return ExecuteResult.Retry(300);

        // Event finished — consume PROCEED and advance.
        if (sync.Events.Count > 0 && sync.Events[0].IsFinished)
        {
            TaskHelper.RunSafely(NEventRoom.Proceed());
            ScheduleFollowUp();
            return ExecuteResult.Ok();
        }

        // PROCEED before a map move — consume and advance.
        if (TextKey.Contains("PROCEED", System.StringComparison.OrdinalIgnoreCase))
        {
            var pending = ReplayEngine.PeekAhead(1);
            if (pending != null && pending.StartsWith("MoveToMapCoordAction "))
            {
                TaskHelper.RunSafely(NEventRoom.Proceed());
                ScheduleFollowUp();
                return ExecuteResult.Ok();
            }
        }

        // Find the matching option.
        if (sync.Events.Count == 0)
            return ExecuteResult.Retry(300);

        var options = sync.Events[0].CurrentOptions;
        int index = -1;

        // Try recorded index first.
        if (RecordedIndex >= 0 && RecordedIndex < options.Count
            && options[RecordedIndex].TextKey == TextKey)
        {
            index = RecordedIndex;
        }
        else
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].TextKey == TextKey)
                {
                    index = i;
                    break;
                }
            }
        }

        if (index < 0)
            return ExecuteResult.Retry(300);

        sync.ChooseLocalOption(index);

        // After the option is chosen, wait before checking if more event options follow.
        ScheduleFollowUp();
        return ExecuteResult.Ok();
    }

    private static void ScheduleFollowUp()
    {
        NGame.Instance!.GetTree()!.CreateTimer(0.5).Connect(
            "timeout", Callable.From(() =>
            {
                ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Event);
                ReplayDispatcher.DispatchNow();
            }));
    }

    public static ChooseEventOptionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length);

        // New format: "ChooseEventOption {index} {textKey}"
        int space = rest.IndexOf(' ');
        if (space >= 0 && int.TryParse(rest.AsSpan(0, space), out int idx))
            return new ChooseEventOptionCommand(raw, rest.Substring(space + 1), idx);

        // Legacy format: "ChooseEventOption {textKey}"
        return new ChooseEventOptionCommand(raw, rest, -1);
    }
}
