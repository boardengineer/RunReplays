using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays;

[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
public static class BattleRewardsReplayPatch
{
    // NRewardButton base type — used for IsAssignableFrom checks.
    private static readonly Type? NRewardButtonType =
        typeof(NRewardsScreen).Assembly
            .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

    internal static NRewardsScreen? _activeScreen;

    [HarmonyPostfix]
    public static void Postfix(NRewardsScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Rewards);
        _activeScreen = __instance;
        ReplayDispatcher.DispatchNow();
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
