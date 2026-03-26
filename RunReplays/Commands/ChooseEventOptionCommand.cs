using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RunReplays.Commands;

/// <summary>
/// Choose an event option by index.
/// Recorded as: "ChooseEventOption {index} # {textKey}" (current format)
/// Legacy:      "ChooseEventOption {index} {textKey}" or "ChooseEventOption {textKey}"
///
/// Index -1 represents PROCEED (event finished or about to transition).
/// The text key is stored as a comment for human readability only.
/// </summary>
public class ChooseEventOptionCommand : ReplayCommand
{
    private const string Prefix = "ChooseEventOption ";
    private const int ProceedIndex = -1;

    public int RecordedIndex { get; }

    public ChooseEventOptionCommand(int recordedIndex) : base("")
    {
        RecordedIndex = recordedIndex;
    }

    public override string ToString() => $"{Prefix}{RecordedIndex}";

    public override string Describe()
        => RecordedIndex == ProceedIndex
            ? "proceed from event"
            : $"choose event option index={RecordedIndex}" +
              (Comment != null ? $" ({Comment})" : "");

    public override ExecuteResult Execute()
    {
        var sync = ReplayState.ActiveEventSynchronizer;
        if (sync == null)
            return ExecuteResult.Retry(300);

        // Event finished — consume PROCEED and advance.
        if (sync.Events.Count > 0 && sync.Events[0].IsFinished)
        {
            TaskHelper.RunSafely(NEventRoom.Proceed());
            ScheduleFollowUp();
            return ExecuteResult.Ok();
        }

        // PROCEED sentinel — consume and advance.
        if (RecordedIndex == ProceedIndex)
            return ExecuteResult.Ok();

        if (sync.Events.Count == 0)
            return ExecuteResult.Retry(300);

        var options = sync.Events[0].CurrentOptions;
        if (RecordedIndex < 0 || RecordedIndex >= options.Count)
            return ExecuteResult.Retry(300);

        sync.ChooseLocalOption(RecordedIndex);
        ScheduleFollowUp();
        return ExecuteResult.Ok();
    }

    private static void ScheduleFollowUp()
    {
        NGame.Instance!.GetTree()!.CreateTimer(0.5).Connect(
            "timeout", Callable.From(() =>
            {
                ReplayDispatcher.DispatchNow();
            }));
    }

    public static ChooseEventOptionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length);

        // Current format: "ChooseEventOption {index}"
        if (int.TryParse(rest.Trim(), out int idx))
            return new ChooseEventOptionCommand(idx);

        // Legacy format: "ChooseEventOption {index} {textKey}"
        int space = rest.IndexOf(' ');
        if (space >= 0 && int.TryParse(rest.AsSpan(0, space), out int legacyIdx))
            return new ChooseEventOptionCommand(legacyIdx) { Comment = rest.Substring(space + 1) };

        // Oldest legacy: "ChooseEventOption {textKey}" — no index, can't replay by index
        // Store textKey as comment, use -1 as fallback
        return new ChooseEventOptionCommand(ProceedIndex) { Comment = rest };
    }
}
