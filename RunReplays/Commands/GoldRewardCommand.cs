using Godot;

using RunReplays.Patch;
namespace RunReplays.Commands;

/// <summary>
/// Claims a gold reward from the rewards screen.
/// Recorded as: "TakeGoldReward: {amount}"
/// </summary>
public class GoldRewardCommand : ReplayCommand
{
    private const string Prefix = "TakeGoldReward: ";

    public int GoldAmount { get; }


    private GoldRewardCommand(string raw, int goldAmount) : base(raw)
    {
        GoldAmount = goldAmount;
    }

    public override string Describe() => $"claim gold reward ({GoldAmount})";

    public override ExecuteResult Execute()
    {
        var screen = BattleRewardsReplayPatch._activeScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        Node? goldButton = BattleRewardsReplayPatch.FindRewardButton(screen, "GoldReward");
        if (goldButton == null)
        {
            PlayerActionBuffer.LogDispatcher("[GoldReward] Gold reward button not found — skipping.");
            return ExecuteResult.Fail();
        }

        BattleRewardsReplayPatch.InvokeGetReward(goldButton);
        PlayerActionBuffer.LogDispatcher($"[GoldReward] Claimed gold reward ({GoldAmount}).");

        return ExecuteResult.Ok();
    }

    public static GoldRewardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length), out int amount))
            return new GoldRewardCommand(raw, amount);

        return null;
    }
}
