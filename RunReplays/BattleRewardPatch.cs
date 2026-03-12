using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays;

/// <summary>
/// Harmony prefixes on RewardSynchronizer.SyncLocalObtained*() that record
/// battle reward selections into the action buffer.
///
/// The SyncLocal methods are called immediately after each reward is confirmed
/// by the player, so they fire:
///   - Cards:   only when the player picks a card from the selection screen
///              (not when the screen first opens — satisfying the requirement).
///   - Relics:  when the relic button is clicked.
///   - Potions: when the potion button is clicked.
///   - Gold:    when the gold button is clicked.
/// </summary>
[HarmonyPatch(typeof(RewardSynchronizer))]
public static class BattleRewardPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalObtainedCard))]
    public static void ObtainedCard(CardModel card)
    {
        if (ShopPurchaseState.IsPurchasing) return;
        CardChoiceScreenSyncPatch.FlushIfPending();
        PlayerActionBuffer.Record($"TakeCardReward: {card.Title}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalObtainedRelic))]
    public static void ObtainedRelic(RelicModel relic)
    {
        if (ShopPurchaseState.IsPurchasing) return;
        CardChoiceScreenSyncPatch.FlushIfPending();
        PlayerActionBuffer.Record($"TakeRelicReward: {relic.Title.GetFormattedText()}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalObtainedPotion))]
    public static void ObtainedPotion(PotionModel potion)
    {
        if (ShopPurchaseState.IsPurchasing) return;
        CardChoiceScreenSyncPatch.FlushIfPending();
        PlayerActionBuffer.Record($"TakePotionReward: {potion.Title.GetFormattedText()}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalObtainedGold))]
    public static void ObtainedGold(int goldAmount)
    {
        if (ShopPurchaseState.IsPurchasing) return;
        CardChoiceScreenSyncPatch.FlushIfPending();
        PlayerActionBuffer.Record($"TakeGoldReward: {goldAmount}");
    }
}
