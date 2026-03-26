using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays.Commands;

/// <summary>
/// Click a reward button on the rewards screen by index.
/// Recorded as: "ClaimReward {index} # {rewardType}: {description}"
///
/// Replaces the old TakeGoldReward, TakeRelicReward, TakePotionReward,
/// and the reward-button-click portion of TakeCardReward.
/// For card rewards, this opens the card selection screen;
/// a TakeCard command follows to select the actual card.
/// </summary>
public class ClaimRewardCommand : ReplayCommand
{
    private const string Prefix = "ClaimReward ";

    // NRewardButton base type — used for IsAssignableFrom checks.
    private static readonly Type? NRewardButtonType =
        typeof(NRewardsScreen).Assembly
            .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

    public int RewardIndex { get; }

    public ClaimRewardCommand(int rewardIndex) : base("")
    {
        RewardIndex = rewardIndex;
    }

    public override string ToString() => $"{Prefix}{RewardIndex}";

    public override string Describe()
        => Comment != null
            ? $"claim reward [{RewardIndex}] ({Comment})"
            : $"claim reward [{RewardIndex}]";

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen == null || !screen.IsInsideTree())
            return ExecuteResult.Retry(200);

        var buttons = EnumerateRewardButtons(screen).ToList();
        if (RewardIndex < 0 || RewardIndex >= buttons.Count)
        {
            PlayerActionBuffer.LogMigrationWarning(
                $"[ClaimReward] Index {RewardIndex} out of range (count={buttons.Count}) — retrying.");
            return ExecuteResult.Retry(200);
        }

        var (button, reward) = buttons[RewardIndex];
        InvokeGetReward(button);
        PlayerActionBuffer.LogDispatcher(
            $"[ClaimReward] Claimed reward [{RewardIndex}] ({reward.GetType().Name}).");
        return ExecuteResult.Ok();
    }

    public static ClaimRewardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int index))
            return new ClaimRewardCommand(index);

        return null;
    }

    // ── Shared reward-button helpers ────────────────────────────────────────

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

    /// <summary>
    /// Returns a human-readable description of a reward for comments.
    /// </summary>
    internal static string DescribeReward(object reward)
    {
        string typeName = reward.GetType().Name;

        // Try to extract a title/amount via common properties.
        var titleProp = reward.GetType().GetProperty("Title", BindingFlags.Public | BindingFlags.Instance);
        if (titleProp?.GetValue(reward) is { } titleObj)
        {
            string? title = titleObj.GetType().GetMethod("GetFormattedText")?.Invoke(titleObj, null) as string
                            ?? titleObj.ToString();
            if (!string.IsNullOrEmpty(title))
                return $"{typeName}: {title}";
        }

        var amountProp = reward.GetType().GetProperty("Amount", BindingFlags.Public | BindingFlags.Instance);
        if (amountProp?.GetValue(reward) is { } amount)
            return $"{typeName}: {amount}";

        return typeName;
    }
}
