using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

using RunReplays.Patches;
namespace RunReplays.Commands;

/// <summary>
/// Sacrifices a card reward (Pael's Wing) instead of picking a card.
/// Recorded as: "SacrificeCardReward[{index}]" (indexed) or "SacrificeCardReward" (legacy).
///
/// Execution flow:
///   1. Wait for the rewards screen to be active.
///   2. Find the CardReward button at the recorded index and invoke GetReward()
///      to open NCardRewardSelectionScreen.
///   3. Wait for the selection screen to appear (captured by CardRewardReplayPatch).
///   4. Find the sacrifice alternative in the screen's _extraOptions, invoke
///      OnSelect() then OnAlternateRewardSelected() to dismiss the screen.
/// </summary>
public sealed class SacrificeCardRewardCommand : ReplayCommand
{
    private const string Cmd = "SacrificeCardReward";
    private const string IndexedPrefix = "SacrificeCardReward[";

    private static readonly FieldInfo? ExtraOptionsField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? OnAlternateRewardSelectedMethod =
        typeof(NCardRewardSelectionScreen).GetMethod(
            "OnAlternateRewardSelected",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public int RewardIndex { get; }

    /// <summary>True once we've opened the card reward screen and are waiting for the selection screen.</summary>
    private static bool _screenOpened;


    private SacrificeCardRewardCommand(string raw, int rewardIndex) : base(raw)
    {
        RewardIndex = rewardIndex;
    }

    public override string Describe()
    {
        string indexStr = RewardIndex >= 0 ? $" (pack {RewardIndex})" : "";
        return $"sacrifice card reward{indexStr}";
    }

    public override ExecuteResult Execute()
    {
        // Step 3+4: selection screen is open — perform the sacrifice.
        var screen = CardRewardReplayPatch.selectionScreen;
        if (screen != null)
        {
            var extras = ExtraOptionsField?.GetValue(screen)
                as IReadOnlyList<CardRewardAlternative>;

            if (extras == null || extras.Count == 0)
            {
                PlayerActionBuffer.LogMigrationWarning(
                    "[SacrificeCardReward] No extra options on selection screen.");
                return ExecuteResult.Retry(200);
            }

            CardRewardAlternative? sacrifice = null;
            foreach (var alt in extras)
            {
                if (alt.OptionId.Contains("sacrifice", System.StringComparison.OrdinalIgnoreCase)
                    || alt.OptionId.Contains("pael", System.StringComparison.OrdinalIgnoreCase))
                {
                    sacrifice = alt;
                    break;
                }
            }
            sacrifice ??= extras[0];

            TaskHelper.RunSafely(sacrifice.OnSelect());
            
            OnAlternateRewardSelectedMethod?.Invoke(screen, new object[] { sacrifice.AfterSelected });

            CardRewardReplayPatch.selectionScreen = null;
            _screenOpened = false;
            ReplayDispatcher.DispatchNow();
            return ExecuteResult.Ok();
        }

        // Step 2: waiting for the selection screen to appear after opening the reward.
        if (_screenOpened)
            return ExecuteResult.Retry(200);

        // Step 1: find and click the CardReward button to open the selection screen.
        var rewardScreen = ReplayState.ActiveRewardsScreen;
        if (rewardScreen == null || !rewardScreen.IsInsideTree())
            return ExecuteResult.Retry(200);

        Node? targetButton = null;
        int cardRewardCount = 0;
        foreach (var (button, reward) in CardRewardCommand.EnumerateRewardButtons(rewardScreen))
        {
            if (CardRewardCommand.IsRewardOfType(reward, "CardReward"))
            {
                if (RewardIndex >= 0)
                {
                    if (cardRewardCount == RewardIndex)
                        targetButton = button;
                }
                else
                {
                    targetButton ??= button;
                }
                cardRewardCount++;
            }
        }

        if (targetButton == null)
        {
            return ExecuteResult.Retry(200);
        }

        CardRewardCommand.InvokeGetReward(targetButton);
        _screenOpened = true;
        return ExecuteResult.Retry(200);
    }

    public static SacrificeCardRewardCommand? TryParse(string raw)
    {
        if (raw.StartsWith(IndexedPrefix))
        {
            int closeBracket = raw.IndexOf(']', IndexedPrefix.Length);
            if (closeBracket > IndexedPrefix.Length
                && int.TryParse(
                    raw.AsSpan(IndexedPrefix.Length, closeBracket - IndexedPrefix.Length),
                    out int idx))
            {
                return new SacrificeCardRewardCommand(raw, idx);
            }
        }

        if (raw == Cmd)
            return new SacrificeCardRewardCommand(raw, -1);

        return null;
    }
}
