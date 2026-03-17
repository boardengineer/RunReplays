using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace RunReplays;

/// <summary>
/// Harmony prefixes on RewardSynchronizer.SyncLocalObtained*() that record
/// battle reward selections into the action buffer.
///
/// The SyncLocal methods are called immediately after each reward is confirmed
/// by the player, so they fire:
///   - Cards:      only when the player picks a card from the selection screen
///                 (not when the screen first opens — satisfying the requirement).
///   - Relics:     when the relic button is clicked.
///   - Potions:    when the potion button is clicked.
///   - Gold:       when the gold button is clicked.
///   - Sacrifice:  when the Pael's Wing sacrifice alternative is chosen.
///
/// When multiple card reward packs are present on the rewards screen, a manual
/// Harmony prefix on NRewardButton.GetReward() tracks which CardReward button
/// (by 0-based index among all CardReward buttons) was clicked.  This index is
/// included in the recorded command so the replay can click the correct button.
/// </summary>
[HarmonyPatch(typeof(RewardSynchronizer))]
public static class BattleRewardPatch
{
    /// <summary>
    /// Index of the most recently clicked CardReward button among all
    /// CardReward buttons on the rewards screen.  -1 when the reward is
    /// not a regular CardReward (e.g. SpecialCardReward).
    /// Set by <see cref="GetRewardPrefix"/> and consumed by
    /// <see cref="ObtainedCard"/>.
    /// </summary>
    internal static int LastCardRewardIndex = -1;

    /// <summary>
    /// True while a CardReward button's GetReward() is in progress (i.e.
    /// the player clicked a CardReward and the card selection is open).
    /// Used to suppress <c>SelectCardFromScreen</c> recording — the
    /// <c>TakeCardReward</c> command already captures the selection.
    /// Set by <see cref="GetRewardPrefix"/>, cleared by <see cref="ObtainedCard"/>.
    /// </summary>
    internal static bool IsProcessingCardReward;

    /// <summary>
    /// Manual Harmony prefix for NRewardButton.GetReward().
    /// Registered in <see cref="MainMenuButtonInjector"/> alongside the
    /// Crystal Sphere manual patches.
    ///
    /// When a CardReward-type button is clicked (during recording, not
    /// replay), this computes its 0-based index among all CardReward
    /// buttons on the parent NRewardsScreen and stores it in
    /// <see cref="LastCardRewardIndex"/>.
    /// </summary>
    public static void GetRewardPrefix(object __instance)
    {
        // During replay the index comes from the log, not from button clicks.
        if (ReplayEngine.IsActive) return;

        // Check whether the reward on this button is a regular CardReward.
        var rewardProp = __instance.GetType()
            .GetProperty("Reward", BindingFlags.Public | BindingFlags.Instance);
        var reward = rewardProp?.GetValue(__instance);
        if (reward == null || !BattleRewardsReplayPatch.IsRewardOfType(reward, "CardReward"))
        {
            LastCardRewardIndex = -1;
            // Don't clear IsProcessingCardReward — non-card rewards don't
            // affect the card reward flow.
            return;
        }

        IsProcessingCardReward = true;

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
            LastCardRewardIndex = -1;
            return;
        }

        // Find this button's index among all CardReward buttons.
        int index = 0;
        foreach (var (button, r) in BattleRewardsReplayPatch.EnumerateRewardButtons(screen))
        {
            if (!BattleRewardsReplayPatch.IsRewardOfType(r, "CardReward"))
                continue;
            if (ReferenceEquals(button, node))
            {
                LastCardRewardIndex = index;
                return;
            }
            index++;
        }
        LastCardRewardIndex = -1;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RewardSynchronizer.SyncLocalObtainedCard))]
    public static void ObtainedCard(CardModel card)
    {
        if (ShopPurchaseState.IsPurchasing) return;
        CardChoiceScreenSyncPatch.FlushIfPending();

        int idx = LastCardRewardIndex;
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
        PlayerActionBuffer.Record("SacrificeCardReward");
    }
}
