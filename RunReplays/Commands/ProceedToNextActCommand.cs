using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Commands;

/// <summary>
/// Proceed to the next act after the boss fight.
/// Recorded as: "VoteForMapCoordAction {playerId}"
/// </summary>
public sealed class ProceedToNextActCommand : ReplayCommand
{
    private const string Prefix = "VoteForMapCoordAction ";

    public string Arguments { get; }

    private ProceedToNextActCommand(string raw, string arguments) : base(raw)
    {
        Arguments = arguments;
    }

    public override string ToString() => $"{Prefix}{Arguments}";

    public override string Describe() => "proceed to next act";

    public override ExecuteResult Execute()
    {
        RunManager.Instance.ActChangeSynchronizer.SetLocalPlayerReady();
        return ExecuteResult.Ok();
    }

    public static ProceedToNextActCommand? TryParse(string raw)
        => raw.StartsWith(Prefix) ? new ProceedToNextActCommand(raw, raw.Substring(Prefix.Length)) : null;
}
