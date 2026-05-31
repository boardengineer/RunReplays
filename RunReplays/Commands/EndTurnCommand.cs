using RunReplays.Patches.Replay;
using MegaCrit.Sts2.Core.Combat;
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
        if (CombatManager.Instance == null
            || !CombatManager.Instance.IsInProgress
            || CombatManager.Instance.IsOverOrEnding)
            return ExecuteResult.Ok();

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
