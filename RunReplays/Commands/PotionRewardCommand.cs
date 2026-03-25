using Godot;

namespace RunReplays.Commands;

/// <summary>
/// Claims a potion reward from the rewards screen.
/// Recorded as: "TakePotionReward: {title}"
/// </summary>
public class PotionRewardCommand : ReplayCommand
{
    private const string Prefix = "TakePotionReward: ";

    public string PotionTitle { get; }

    public override ReplayState.ReadyState RequiredState => ReplayState.ReadyState.Rewards;

    private PotionRewardCommand(string raw, string potionTitle) : base(raw)
    {
        PotionTitle = potionTitle;
    }

    public override string Describe() => $"claim potion reward '{PotionTitle}'";

    public override ExecuteResult Execute()
    {
        var screen = BattleRewardsReplayPatch._activeScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        Node? potionButton = BattleRewardsReplayPatch.FindRewardButton(screen, "PotionReward");
        if (potionButton == null)
        {
            PlayerActionBuffer.LogDispatcher("[PotionReward] Potion reward button not found — skipping.");
            return ExecuteResult.Fail();
        }

        BattleRewardsReplayPatch.InvokeGetReward(potionButton);
        PlayerActionBuffer.LogDispatcher($"[PotionReward] Claimed potion reward '{PotionTitle}'.");

        return ExecuteResult.Ok();
    }

    public static PotionRewardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        return new PotionRewardCommand(raw, raw.Substring(Prefix.Length));
    }
}
