using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays.Patch;
using RunReplays;

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
                typeof(BattleRewardPatch),
                nameof(BattleRewardPatch.GetRewardPrefix));

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
}
