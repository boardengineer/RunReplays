using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patches;
using RunReplays;
using RunReplays.Patches.Record;

/// <summary>
/// Tracks state for shop purchase suppression.
/// Recording of reward claims is now handled by the GetRewardPrefix patch
/// in MainMenuButtonInjector (fires on NRewardButton.GetReward).
/// </summary>
[HarmonyPatch(typeof(RewardSynchronizer))]
public static class BattleRewardPatch
{
    internal static bool IsProcessingCardReward;
}
