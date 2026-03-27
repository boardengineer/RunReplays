using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Commands;

/// <summary>
/// Proceed to the next act after the boss fight.
/// Recorded as: "NextAct"
/// Legacy:      "VoteForMapCoordAction {playerId}"
/// </summary>
public sealed class ProceedToNextActCommand : ReplayCommand
{
    private const string Cmd = "ProceedToNextAct";
    private const string LegacyCmd = "NextAct";
    private const string LegacyPrefix = "VoteForMapCoordAction ";

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
        if (raw == Cmd || raw == LegacyCmd)
            return new ProceedToNextActCommand();

        if (raw.StartsWith(LegacyPrefix))
            return new ProceedToNextActCommand();

        return null;
    }
}
