using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;

namespace RunReplays;

/// <summary>
/// Recording and replay patches for the fake merchant event shop.
///
/// The fake merchant event (NFakeMerchant) has its own OpenInventory method
/// separate from the regular NMerchantRoom.  When the player clicks the
/// merchant character, OnMerchantOpened fires, which calls OpenInventory.
///
/// Recording: prefix on NFakeMerchant.OpenInventory records "OpenFakeShop".
/// Replay:    postfix on NFakeMerchant.AfterRoomIsLoaded signals shop readiness
///            so the dispatcher can execute OpenFakeShopCommand and BuyRelicCommand.
/// </summary>

// ── Record when the fake merchant shop is opened ─────────────────────────────

[HarmonyPatch(typeof(NFakeMerchant), "OpenInventory")]
public static class FakeMerchantOpenRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (ReplayEngine.IsActive)
            return;

        PlayerActionBuffer.Record("OpenFakeShop");
    }
}

// ── Auto-open the fake merchant shop during replay ───────────────────────────

[HarmonyPatch(typeof(NFakeMerchant), "AfterRoomIsLoaded")]
public static class FakeMerchantReplayPatch
{
    internal static NFakeMerchant? ActiveInstance;

    internal static bool IsActive => ActiveInstance != null;

    internal static readonly MethodInfo? OpenInventoryMethod =
        typeof(NFakeMerchant).GetMethod("OpenInventory",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    // NFakeMerchant._event is the FakeMerchant model which holds the inventory.
    private static readonly FieldInfo? EventField =
        typeof(NFakeMerchant).GetField("_event",
            BindingFlags.NonPublic | BindingFlags.Instance);

    // FakeMerchant._inventory is the MerchantInventory with relic entries.
    private static readonly FieldInfo? InventoryField =
        typeof(NFakeMerchant).Assembly
            .GetType("MegaCrit.Sts2.Core.Models.Events.FakeMerchant")
            ?.GetField("_inventory", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NFakeMerchant __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        if (!ReplayEngine.PeekOpenFakeShop())
            return;

        ActiveInstance = __instance;
        ShopOpenedReplayPatch.ActiveRoom = null;
        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Shop);
        ReplayDispatcher.DispatchNow();
    }

    internal static List<MerchantEntry>? GetEntries(NFakeMerchant merchant)
    {
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                              | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        object? eventModel = EventField?.GetValue(merchant);
        if (eventModel == null)
        {
            PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] _event field is null.");
            return null;
        }

        object? inventory = InventoryField?.GetValue(eventModel);
        if (inventory == null)
        {
            PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] _inventory field is null.");
            return null;
        }

        var all = new List<MerchantEntry>();
        foreach (var field in inventory.GetType().GetFields(bf))
        {
            object? value;
            try { value = field.GetValue(inventory); }
            catch { continue; }

            if (value is IEnumerable enumerable)
                foreach (object? item in enumerable)
                    if (item is MerchantEntry e)
                        all.Add(e);
            else if (value is MerchantEntry single)
                all.Add(single);
        }

        foreach (var prop in inventory.GetType().GetProperties(bf))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? value;
            try { value = prop.GetValue(inventory); }
            catch { continue; }

            if (value is IEnumerable enumerable)
                foreach (object? item in enumerable)
                    if (item is MerchantEntry e && !all.Contains(e))
                        all.Add(e);
            else if (value is MerchantEntry single && !all.Contains(single))
                all.Add(single);
        }

        return all.Count > 0 ? all : null;
    }
}
