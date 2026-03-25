using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Commands;

/// <summary>
///     Proceed to the next act after the boss fight.
///     Recorded as: "VoteForMapCoordAction {playerId}"
/// </summary>
public sealed class ProceedToNextActCommand : ReplayCommand
{
    private const string Prefix = "VoteForMapCoordAction ";


    private ProceedToNextActCommand(string raw) : base(raw)
    {
    }

    public override string Describe()
    {
        return "proceed to next act";
    }

    public override ExecuteResult Execute()
    {
        RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
        return ExecuteResult.Ok();
    }

    public static ProceedToNextActCommand? TryParse(string raw)
    {
        return raw.StartsWith(Prefix) ? new ProceedToNextActCommand(raw) : null;
    }
}