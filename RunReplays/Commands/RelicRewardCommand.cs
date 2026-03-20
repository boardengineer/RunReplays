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

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Rewards;

    private RelicRewardCommand(string raw, string relicTitle) : base(raw)
    {
        RelicTitle = relicTitle;
    }

    public override string Describe() => $"claim relic reward '{RelicTitle}'";

    public override ExecuteResult Execute()
    {
        var screen = BattleRewardsReplayPatch._activeScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        Node? relicButton = BattleRewardsReplayPatch.FindRewardButton(screen, "RelicReward");
        if (relicButton == null)
        {
            PlayerActionBuffer.LogDispatcher("[RelicReward] Relic reward button not found — skipping.");
            return ExecuteResult.Fail();
        }

        BattleRewardsReplayPatch.InvokeGetReward(relicButton);
        PlayerActionBuffer.LogDispatcher($"[RelicReward] Claimed relic reward '{RelicTitle}'.");

        return ExecuteResult.Ok();
    }

    public static RelicRewardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        return new RelicRewardCommand(raw, raw.Substring(Prefix.Length));
    }
}
