using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays.Patch;
using RunReplays;
using RunReplays.Commands;

/// <summary>
/// Manually patches NRewardButton.GetReward() to track which CardReward
/// button (by 0-based index) was clicked during recording.
///
/// Applied manually (not via [HarmonyPatch]) because NRewardButton is
/// resolved at runtime — Godot 4 generates subclasses, and the concrete
/// type name varies.
/// </summary>
public static class CardRewardButtonPatcher
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            var harmony = new Harmony("RunReplays.CardRewardButton");

            var nRewardButtonType = typeof(NRewardsScreen).Assembly
                .GetType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");

            if (nRewardButtonType == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[CardRewardButtonPatcher] NRewardButton type not found — skipping patch.");
                return;
            }

            var getReward = AccessTools.Method(nRewardButtonType, "GetReward");
            if (getReward == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[CardRewardButtonPatcher] GetReward method not found — skipping patch.");
                return;
            }

            var prefix = new HarmonyMethod(
                typeof(CardRewardButtonPatcher),
                nameof(GetRewardPrefix));

            harmony.Patch(getReward, prefix: prefix);
            PlayerActionBuffer.LogToDevConsole(
                "[CardRewardButtonPatcher] Patched NRewardButton.GetReward OK.");
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[CardRewardButtonPatcher] Manual patching FAILED: {ex}");
        }
    }

    /// <summary>
    /// Harmony prefix for NRewardButton.GetReward().
    /// When a CardReward-type button is clicked (during recording, not
    /// replay), computes its 0-based index among all CardReward buttons
    /// on the parent NRewardsScreen and stores it in
    /// <see cref="BattleRewardPatch.LastCardRewardIndex"/>.
    /// </summary>
    public static void GetRewardPrefix(object __instance)
    {
        // During replay the index comes from the log, not from button clicks.
        if (ReplayEngine.IsActive) return;

        // Check whether the reward on this button is a regular CardReward.
        var rewardProp = __instance.GetType()
            .GetProperty("Reward", BindingFlags.Public | BindingFlags.Instance);
        var reward = rewardProp?.GetValue(__instance);
        if (reward == null || !CardRewardCommand.IsRewardOfType(reward, "CardReward"))
        {
            BattleRewardPatch.LastCardRewardIndex = -1;
            return;
        }

        BattleRewardPatch.IsProcessingCardReward = true;

        // Walk up the tree to find the NRewardsScreen ancestor.
        Node node = (Node)__instance;
        Node? current = node.GetParent();
        NRewardsScreen? screen = null;
        while (current != null)
        {
            if (current is NRewardsScreen s) { screen = s; break; }
            current = current.GetParent();
        }

        if (screen == null)
        {
            BattleRewardPatch.LastCardRewardIndex = -1;
            return;
        }

        // Find this button's index among all CardReward buttons.
        int index = 0;
        foreach (var (button, r) in CardRewardCommand.EnumerateRewardButtons(screen))
        {
            if (!CardRewardCommand.IsRewardOfType(r, "CardReward"))
                continue;
            if (ReferenceEquals(button, node))
            {
                BattleRewardPatch.LastCardRewardIndex = index;
                return;
            }
            index++;
        }
        BattleRewardPatch.LastCardRewardIndex = -1;
    }
}
