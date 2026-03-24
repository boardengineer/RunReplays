using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace RunReplays;

/// <summary>
/// Recording and replay patches for the fake merchant event shop.
///
/// The fake merchant event (NFakeMerchant) has its own OpenInventory method
/// separate from the regular NMerchantRoom.  When the player clicks the
/// merchant character, OnMerchantOpened fires, which calls OpenInventory.
///
/// Recording: prefix on NFakeMerchant.OpenInventory records "OpenFakeShop".
/// Replay:    postfix on NFakeMerchant.AfterRoomIsLoaded opens the inventory
///            and starts a purchase loop that handles BuyRelic commands.
///
/// The fake merchant only sells relics (with ??? titles), so the purchase
/// loop only needs to handle BuyRelic.
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

    /// <summary>Called by the dispatcher to handle fake shop commands.</summary>
    internal static void DispatchFromEngine()
    {
        if (ActiveInstance == null)
            return;

        if (ReplayEngine.PeekOpenFakeShop())
        {
            ReplayRunner.ExecuteOpenFakeShop();
            PlayerActionBuffer.LogDispatcher("[FakeShop] Opening inventory.");

            if (OpenInventoryMethod != null)
            {
                var merchant = ActiveInstance;
                Callable.From(() =>
                {
                    OpenInventoryMethod.Invoke(merchant, null);
                    ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Shop);
                    ReplayDispatcher.DispatchNow();
                }).CallDeferred();
            }
            return;
        }

        if (ReplayEngine.PeekBuyRelic(out string relicTitle))
        {
            var entries = GetEntries(ActiveInstance);
            var entry = entries?
                .OfType<MerchantRelicEntry>()
                .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == relicTitle);

            if (entry == null)
            {
                PlayerActionBuffer.LogDispatcher($"[FakeShop] Relic '{relicTitle}' not found — skipping.");
                ReplayRunner.ExecuteBuyRelic(out _);
                ReplayDispatcher.DispatchNow();
                return;
            }

            ReplayRunner.ExecuteBuyRelic(out _);
            ShopOpenedReplayPatch.InvokePurchase(entry);
            PlayerActionBuffer.LogDispatcher($"[FakeShop] Purchased relic '{relicTitle}'.");
            ReplayDispatcher.DispatchNow();
            return;
        }

        PlayerActionBuffer.LogDispatcher("[FakeShop] No more fake shop commands — clearing and re-dispatching.");
        ActiveInstance = null;
        ReplayDispatcher.DispatchNow();
    }

    // ── Purchase loop ────────────────────────────────────────────────────────

    private const int MaxRetries = 10;

    private static void ProcessNextPurchase(NFakeMerchant merchant, int retriesLeft = MaxRetries)
    {
        if (!ReplayEngine.IsActive)
            return;

        var entries = GetEntries(merchant);
        if (entries == null || entries.Count == 0)
        {
            if (retriesLeft > 0)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[FakeMerchantReplayPatch] Entries not yet available — retrying ({retriesLeft} left).");
                NGame.Instance!.GetTree()!.CreateTimer(0.1).Connect(
                    "timeout", Callable.From(() => ProcessNextPurchase(merchant, retriesLeft - 1)));
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] Could not find entries after retries.");
            }
            return;
        }

        PlayerActionBuffer.LogToDevConsole($"[FakeMerchantReplayPatch] {entries.Count} entries available.");

        if (ReplayEngine.PeekBuyRelic(out string relicTitle))
        {
            var entry = entries
                .OfType<MerchantRelicEntry>()
                .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == relicTitle);

            if (entry == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[FakeMerchantReplayPatch] Relic '{relicTitle}' not found in entries — aborting.");
                return;
            }

            ReplayRunner.ExecuteBuyRelic(out _);
            ShopOpenedReplayPatch.InvokePurchase(entry);
            PlayerActionBuffer.LogToDevConsole($"[FakeMerchantReplayPatch] Triggered purchase of relic '{relicTitle}'.");
            Callable.From(() => ProcessNextPurchase(merchant)).CallDeferred();
            return;
        }

        PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] No more purchase commands — done.");

        // If a map move is next, open the map.
        if (ReplayEngine.PeekMapNode(out _, out _))
        {
            PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] Map move next — opening map.");
            NMapScreen.Instance?.Open();
            NMapScreen.Instance?.SetTravelEnabled(true);
        }
    }

    internal static List<MerchantEntry>? GetEntries(NFakeMerchant merchant)
    {
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                              | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        // Navigate: NFakeMerchant._event (FakeMerchant model) → _inventory (MerchantInventory)
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
