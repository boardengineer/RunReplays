using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Commands;

/// <summary>
/// Proceed to the next act after the boss fight.
/// Recorded as: "ProceedToNextAct"
/// </summary>
public sealed class ProceedToNextActCommand : ReplayCommand
{
    private const string Cmd = "ProceedToNextAct";

    public ProceedToNextActCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "proceed to next act";

    public override ExecuteResult Execute()
    {
        RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
        return ExecuteResult.Ok();
    }

    public static ProceedToNextActCommand? TryParse(string raw)
    {
        if (raw == Cmd)
            return new ProceedToNextActCommand();

        return null;
    }
}
