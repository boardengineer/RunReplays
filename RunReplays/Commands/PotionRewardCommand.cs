using System.Linq;
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


    public PotionRewardCommand(string potionTitle) : base("")
    {
        PotionTitle = potionTitle;
    }

    public override string ToString() => $"TakePotionReward: {PotionTitle}";

    public override string Describe() => $"claim potion reward '{PotionTitle}'";

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        var match = ClaimRewardCommand.EnumerateRewardButtons(screen)
            .FirstOrDefault(x => ClaimRewardCommand.IsRewardOfType(x.reward, "PotionReward"));
        if (match.button == null)
        {
            PlayerActionBuffer.LogDispatcher("[PotionReward] Potion reward button not found — skipping.");
            return ExecuteResult.Fail();
        }

        ClaimRewardCommand.InvokeGetReward(match.button);
        PlayerActionBuffer.LogDispatcher($"[PotionReward] Claimed potion reward '{PotionTitle}'.");

        return ExecuteResult.Ok();
    }

    public static PotionRewardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        return new PotionRewardCommand(raw.Substring(Prefix.Length));
    }
}
