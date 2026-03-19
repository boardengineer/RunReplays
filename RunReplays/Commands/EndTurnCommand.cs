namespace RunReplays.Commands;

/// <summary>
/// End the player's turn.
/// Recorded as: "EndPlayerTurnAction for player {playerId} round {combatRound}"
/// </summary>
public class EndTurnCommand : ReplayCommand
{
    private const string Prefix = "EndPlayerTurnAction ";

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Combat;

    private EndTurnCommand(string raw) : base(raw) { }

    public override string Describe() => "end player turn";

    public override ExecuteResult Execute()
    {
        CardPlayReplayPatch.DispatchFromEngine();
        return ExecuteResult.Ok();
    }

    public static EndTurnCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new EndTurnCommand(raw);
    }
}
