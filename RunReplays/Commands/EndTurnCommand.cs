using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// End the player's turn.
/// Recorded as: "EndTurn # for player {playerId} round {combatRound}"
/// </summary>
public class EndTurnCommand : ReplayCommand
{
    private const string Prefix = "EndTurn";

    public EndTurnCommand() : base("") { }

    public override string ToString() => Prefix;

    public override string Describe() => "end player turn";

    public override ExecuteResult Execute()
    {
        if (CardPlayReplayPatch.TryEndTurn())
            return ExecuteResult.Ok();
        return ExecuteResult.Retry(200);
    }

    public static EndTurnCommand? TryParse(string raw)
    {
        if (raw == Prefix)
            return new EndTurnCommand();

        return null;
    }
}
