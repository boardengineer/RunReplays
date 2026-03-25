using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;

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
    // NRewardButton base type — used for IsAssignableFrom checks.
    private static readonly Type? NRewardButtonType =
        typeof(NRewardsScreen).Assembly
            .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

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
        var screen = ReplayState.ActiveRewardsScreen;
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

        foreach (var (button, reward) in CardRewardCommand.EnumerateRewardButtons(screen))
        {
            // Direct path: SpecialCardReward (e.g. stolen cards from Thieving Hopper)
            // adds the card immediately without opening a selection screen.
            if (CardRewardCommand.IsRewardOfType(reward, "SpecialCardReward"))
            {
                string? rewardTitle = CardRewardCommand.GetRewardCardTitle(reward);
                if (rewardTitle != null && rewardTitle == CardTitle)
                {
                    PlayerActionBuffer.LogDispatcher($"[CardReward] Claiming SpecialCardReward '{CardTitle}'.");
                    CardRewardCommand.InvokeGetReward(button);
                    waitingForRewardScreenOpen = false;
                    CardRewardReplayPatch.selectionScreen = null;
                    return ExecuteResult.Ok();
                }
            }

            if (CardRewardCommand.IsRewardOfType(reward, "CardReward"))
            {
                if (RewardIndex >= 0)
                {
                    if (cardRewardCount == RewardIndex)
                    {
                        CardRewardCommand.InvokeGetReward(button);
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
            CardRewardCommand.InvokeGetReward(screenButton);
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

    internal static void InvokeGetReward(Node button)
    {
        MethodInfo? method = button.GetType()
            .GetMethod("GetReward", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (method == null)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] Replay: GetReward not found on {button.GetType().Name}.");
            return;
        }

        object? result = method.Invoke(button, null);
        if (result is Task task)
            TaskHelper.RunSafely(task);
    }

    /// <summary>
    /// Yields every (button, reward) pair on the rewards screen.
    /// </summary>
    internal static IEnumerable<(Node button, object reward)> EnumerateRewardButtons(Node root)
    {
        if (NRewardButtonType == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Replay: NRewardButton type not resolved.");
            yield break;
        }

        foreach (Node node in root.FindChildren("*", "", owned: false))
        {
            if (!NRewardButtonType.IsAssignableFrom(node.GetType()))
                continue;

            PropertyInfo? rewardProp = node.GetType()
                .GetProperty("Reward", BindingFlags.Public | BindingFlags.Instance);

            object? reward = rewardProp?.GetValue(node);
            if (reward != null)
                yield return (node, reward);
        }
    }

    internal static Node? FindRewardButton(Node root, string rewardBaseTypeName)
    {
        foreach (var (button, reward) in EnumerateRewardButtons(root))
        {
            if (IsRewardOfType(reward, rewardBaseTypeName))
                return button;
        }
        return null;
    }

    /// <summary>
    /// Tries to extract a card title from a reward that carries a single card
    /// (e.g. SpecialCardReward).  Returns null when the reward type doesn't
    /// expose a card or reflection fails — callers treat null as "accept any".
    /// </summary>
    internal static string? GetRewardCardTitle(object reward)
    {
        // Look for a private _card field (SpecialCardReward) or public Card property.
        var field = reward.GetType().GetField("_card", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(reward) is CardModel card)
            return card.Title;

        var prop = reward.GetType().GetProperty("Card", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(reward) is CardModel card2)
            return card2.Title;

        return null;
    }

    internal static bool IsRewardOfType(object? reward, string baseTypeName)
    {
        Type? t = reward?.GetType();
        while (t != null)
        {
            if (t.Name == baseTypeName)
                return true;
            t = t.BaseType;
        }
        return false;
    }
}
