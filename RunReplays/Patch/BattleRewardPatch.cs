using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patch;

[HarmonyPatch(typeof(RewardSynchronizer))]
public static class BattleRewardPatch
{
    internal static int LastCardRewardIndex = -1;
    internal static bool IsProcessingCardReward;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalObtainedCard))]
    public static void ObtainedCard(CardModel card)
    {
        if (ShopPurchaseState.IsPurchasing) return;
        CardChoiceScreenSyncPatch.FlushIfPending();

        var idx = LastCardRewardIndex;
        LastCardRewardIndex = -1;
        IsProcessingCardReward = false;

        if (idx >= 0)
            PlayerActionBuffer.Record($"TakeCardReward[{idx}]: {card.Title}");
        else
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

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalPaelsWingSacrifice))]
    public static void PaelsWingSacrifice(PaelsWing paelsWing)
    {
        CardChoiceScreenSyncPatch.FlushIfPending();

        var idx = LastCardRewardIndex;
        LastCardRewardIndex = -1;
        IsProcessingCardReward = false;

        if (idx >= 0)
            PlayerActionBuffer.Record($"SacrificeCardReward[{idx}]");
        else
            PlayerActionBuffer.Record("SacrificeCardReward");
    }
}