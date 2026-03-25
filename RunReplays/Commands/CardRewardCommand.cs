using Godot;

using RunReplays.Patch;
namespace RunReplays.Commands;

/// <summary>
/// Takes a card reward from the rewards screen.
/// Recorded as: "TakeCardReward[{index}]: {title}" (indexed) or "TakeCardReward: {title}" (legacy)
///
/// Two execution paths:
///   - Direct: a non-CardReward button (e.g. SpecialCardReward) that adds the card
///     immediately without opening a selection screen.  Consumed here, returns Ok().
///   - Screen: the normal CardReward button opens NCardRewardSelectionScreen.
///     The command stays in the queue for CardRewardReplayPatch to peek and consume
///     after the player's card is auto-selected.  Returns Retry() as a safety net;
///     the retry is typically pre-empted by OnCardRewardHandled → DispatchNow().
/// </summary>
public class CardRewardCommand : ReplayCommand
{
    private const string Prefix = "TakeCardReward: ";
    private const string IndexedPrefix = "TakeCardReward[";
    internal static bool waitingForRewardScreenOpen = false;

    public string CardTitle { get; }
    public int RewardIndex { get; }


    private CardRewardCommand(string raw, string cardTitle, int rewardIndex) : base(raw)
    {
        CardTitle = cardTitle;
        RewardIndex = rewardIndex;
    }

    public override string Describe()
    {
        string indexStr = RewardIndex >= 0 ? $" (pack {RewardIndex})" : "";
        return $"take card reward '{CardTitle}'{indexStr}";
    }

    public override ExecuteResult Execute()
    {
        var screen = BattleRewardsReplayPatch._activeScreen;
        if (screen == null || !screen.IsInsideTree())
        {
            return ExecuteResult.Retry(200);
        }

        // Scan reward buttons for a direct card reward (e.g. SpecialCardReward)
        // that matches by title and can be claimed without a selection screen.
        Node? screenButton = null;
        int cardRewardCount = 0;

        if (CardRewardReplayPatch.selectionScreen != null)
        {
            if (CardRewardReplayPatch.SelectCard(CardTitle))
            {
                waitingForRewardScreenOpen = false;
                CardRewardReplayPatch.selectionScreen = null;
                return ExecuteResult.Ok();
            }
        }

        if (waitingForRewardScreenOpen)
        {
            return ExecuteResult.Retry(200);
        }

        foreach (var (button, reward) in BattleRewardsReplayPatch.EnumerateRewardButtons(screen))
        {
            // Direct path: SpecialCardReward (e.g. stolen cards from Thieving Hopper)
            // adds the card immediately without opening a selection screen.
            if (BattleRewardsReplayPatch.IsRewardOfType(reward, "SpecialCardReward"))
            {
                string? rewardTitle = BattleRewardsReplayPatch.GetRewardCardTitle(reward);
                if (rewardTitle != null && rewardTitle == CardTitle)
                {
                    PlayerActionBuffer.LogDispatcher($"[CardReward] Claiming SpecialCardReward '{CardTitle}'.");
                    BattleRewardsReplayPatch.InvokeGetReward(button);
                    waitingForRewardScreenOpen = false;
                    CardRewardReplayPatch.selectionScreen = null;
                    return ExecuteResult.Ok();
                }
            }

            if (BattleRewardsReplayPatch.IsRewardOfType(reward, "CardReward"))
            {
                if (RewardIndex >= 0)
                {
                    if (cardRewardCount == RewardIndex)
                    {
                        BattleRewardsReplayPatch.InvokeGetReward(button);
                        waitingForRewardScreenOpen = true;
                        return ExecuteResult.Retry(200);
                    }
                }
                else
                {
                    screenButton ??= button;
                }
                cardRewardCount++;
            }
        }

        // Screen path: open the card selection screen. CardRewardReplayPatch
        // will peek the command, auto-select the card, and consume it.
        if (screenButton != null)
        {
            BattleRewardsReplayPatch.InvokeGetReward(screenButton);
            PlayerActionBuffer.LogDispatcher($"[CardReward] Triggered card reward button for '{CardTitle}'.");
            return ExecuteResult.Retry(1000);
        }

        PlayerActionBuffer.LogDispatcher("[CardReward] No card reward button found.");
        return ExecuteResult.Fail();
    }

    public static CardRewardCommand? TryParse(string raw)
    {
        // Indexed format: "TakeCardReward[N]: CardTitle"
        if (raw.StartsWith(IndexedPrefix))
        {
            int closeBracket = raw.IndexOf("]: ", System.StringComparison.Ordinal);
            if (closeBracket >= 0
                && int.TryParse(
                    raw.AsSpan(IndexedPrefix.Length, closeBracket - IndexedPrefix.Length),
                    out int idx))
            {
                string title = raw.Substring(closeBracket + "]: ".Length);
                return new CardRewardCommand(raw, title, idx);
            }
        }

        // Legacy format: "TakeCardReward: CardTitle"
        if (raw.StartsWith(Prefix))
            return new CardRewardCommand(raw, raw.Substring(Prefix.Length), -1);

        return null;
    }
}
