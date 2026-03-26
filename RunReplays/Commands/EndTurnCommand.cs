using RunReplays.Patches;
using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// End the player's turn.
/// Recorded as: "EndPlayerTurnAction for player {playerId} round {combatRound}"
/// </summary>
public class EndTurnCommand : ReplayCommand
{
    private const string Prefix = "EndPlayerTurnAction ";

    public string Arguments { get; }

    private EndTurnCommand(string raw, string arguments) : base(raw)
    {
        Arguments = arguments;
    }

    public override string ToString() => $"{Prefix}{Arguments}";

    public override string Describe() => "end player turn";

    public override ExecuteResult Execute()
    {
        if (CardPlayReplayPatch.TryEndTurn())
        {
            return ExecuteResult.Ok();
        }
        else
        {
            return ExecuteResult.Retry(200);
        }
    }

    public static EndTurnCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new EndTurnCommand(raw, raw.Substring(Prefix.Length));
    }
}
