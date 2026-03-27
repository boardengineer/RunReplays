using System.Linq;
using Godot;

namespace RunReplays.Commands;

/// <summary>
/// Claims a relic reward from the rewards screen.
/// Recorded as: "TakeRelicReward: {title}"
/// </summary>
public class RelicRewardCommand : ReplayCommand
{
    private const string Prefix = "TakeRelicReward: ";

    public string RelicTitle { get; }


    public RelicRewardCommand(string relicTitle) : base("")
    {
        RelicTitle = relicTitle;
    }

    public override string ToString() => $"TakeRelicReward: {RelicTitle}";

    public override string Describe() => $"claim relic reward '{RelicTitle}'";

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        var match = ClaimRewardCommand.EnumerateRewardButtons(screen)
            .FirstOrDefault(x => ClaimRewardCommand.IsRewardOfType(x.reward, "RelicReward"));
        if (match.button == null)
        {
            PlayerActionBuffer.LogDispatcher("[RelicReward] Relic reward button not found — skipping.");
            return ExecuteResult.Fail();
        }

        ClaimRewardCommand.InvokeGetReward(match.button);
        PlayerActionBuffer.LogDispatcher($"[RelicReward] Claimed relic reward '{RelicTitle}'.");

        return ExecuteResult.Ok();
    }

    public static RelicRewardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        return new RelicRewardCommand(raw.Substring(Prefix.Length));
    }
}
