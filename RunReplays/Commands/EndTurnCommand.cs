using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// End the player's turn.
/// Recorded as: "EndTurn # for player {playerId} round {combatRound}"
/// Legacy:      "EndPlayerTurnAction for player {playerId} round {combatRound}"
/// </summary>
public class EndTurnCommand : ReplayCommand
{
    private const string Prefix = "EndTurn";
    private const string LegacyPrefix = "EndPlayerTurnAction ";

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
        // New format: "EndTurn"
        if (raw == Prefix)
            return new EndTurnCommand();

        // Legacy format: "EndPlayerTurnAction for player {id} round {n}"
        if (raw.StartsWith(LegacyPrefix))
            return new EndTurnCommand { Comment = raw.Substring(LegacyPrefix.Length) };

        return null;
    }
}
