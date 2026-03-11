using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RunReplays;

/// <summary>
/// Shared state for shop purchase recording.
///
/// IsPurchasing: suppresses duplicate SyncLocalObtained* recordings in
///   BattleRewardPatch while a merchant purchase is in progress.
///
/// PendingLabel: the Buy* string captured in the OnTryPurchaseWrapper prefix,
///   before ClearAfterPurchase() nulls out the entry's item reference.
///   Written by InvokePurchaseCompleted (on success) or cleared by
///   InvokePurchaseFailed (on failure).
/// </summary>
internal static class ShopPurchaseState
{
    internal static bool IsPurchasing;
    internal static string? PendingLabel;
}

/// <summary>
/// Recording patches for merchant shop interactions.
///
/// Item details (card title etc.) are read in the OnTryPurchaseWrapper PREFIX
/// because ClearAfterPurchase() nulls the entry's item reference before
/// InvokePurchaseCompleted fires.
///
/// InvokePurchaseCompleted prefix writes the captured label and clears state.
/// InvokePurchaseFailed prefix clears state so the flag never stays stuck.
///
/// MerchantCardRemovalEntry overrides OnTryPurchaseWrapper with an extra
/// parameter and is therefore given its own prefix patch.
/// </summary>

// ── Open shop ─────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
public static class ShopOpenRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        PlayerActionBuffer.Record("OpenShop");
    }
}

// ── Capture item label and set flag on purchase start ─────────────────────────

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
public static class ShopPurchaseStartPatch
{
    [HarmonyPrefix]
    public static void Prefix(MerchantEntry __instance)
    {
        ShopPurchaseState.PendingLabel = __instance switch
        {
            MerchantCardEntry card     => card.CreationResult?.Card?.Title is string t
                                             ? $"BuyCard {t}" : null,
            MerchantRelicEntry relic   => relic.Model  != null
                                             ? $"BuyRelic {relic.Model.Title.GetFormattedText()}"  : null,
            MerchantPotionEntry potion => potion.Model != null
                                             ? $"BuyPotion {potion.Model.Title.GetFormattedText()}" : null,
            _ => null
        };
        ShopPurchaseState.IsPurchasing = true;
    }
}

[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper))]
public static class ShopCardRemovalPurchaseStartPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        ShopPurchaseState.PendingLabel = "BuyCardRemoval";
        ShopPurchaseState.IsPurchasing = true;
    }
}

// ── Record on success and clear state ────────────────────────────────────────

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
public static class ShopPurchaseCompletedPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        string? label = ShopPurchaseState.PendingLabel;
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingLabel = null;

        if (label != null)
            PlayerActionBuffer.Record(label);
    }
}

// ── Clear state on failure ────────────────────────────────────────────────────

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseFailed))]
public static class ShopPurchaseFailedPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingLabel = null;
    }
}
