using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RunReplays;

/// <summary>
/// Drives automatic shop replay.
///
/// ShopRoomReadyPatch hooks NMerchantRoom._Ready.  When a replay is active and
/// the next command is OpenShop it defers a call to OpenInventory so the shop UI
/// opens without any player interaction.
///
/// ShopOpenedReplayPatch hooks NMerchantRoom.OpenInventory.  It consumes the
/// OpenShop command and starts chaining Buy* commands.
///
/// Merchant entries are located by scanning every instance field of NMerchantRoom
/// (and one level of child-object fields) for an IEnumerable that yields
/// MerchantEntry elements.
///
/// Each purchase is triggered by calling OnTryPurchaseWrapper on the matching
/// entry via reflection so that both the base and overridden signatures
/// (e.g. MerchantCardRemovalEntry adds a cancelable parameter) are handled
/// without hard-coding exact parameter lists.  Any Task result is forwarded to
/// TaskHelper.RunSafely so the async purchase body runs on the game thread.
/// </summary>

// ── Auto-open the shop when the merchant room becomes active ──────────────────

[HarmonyPatch(typeof(NMerchantRoom), "_Ready")]
public static class ShopRoomReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMerchantRoom __instance)
    {
        if (!ReplayEngine.PeekOpenShop())
            return;

        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] NMerchantRoom ready — deferring OpenInventory.");
        Callable.From(() => __instance.OpenInventory()).CallDeferred();
    }
}

// ── Handle purchases once the shop UI is open ─────────────────────────────────

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
public static class ShopOpenedReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMerchantRoom __instance)
    {
        if (!ReplayEngine.PeekOpenShop())
            return;

        ReplayRunner.ExecuteOpenShop();
        Callable.From(() => ShopOpenedReplayPatch.ProcessNextPurchase(__instance)).CallDeferred();
    }

    // ── Purchase loop ─────────────────────────────────────────────────────────

    private const int MaxRetries = 10;

    internal static void ProcessNextPurchase(NMerchantRoom room, int retriesLeft = MaxRetries)
    {
        if (!ReplayEngine.IsActive)
            return;

        List<MerchantEntry>? entries = GetEntries(room);
        if (entries == null || entries.Count == 0)
        {
            if (retriesLeft > 0)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[ShopReplayPatch] Merchant entries not yet available — retrying in 100 ms ({retriesLeft} left).");
                TaskHelper.RunSafely(RetryAfterDelay(room, retriesLeft - 1));
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] Could not find merchant entries after retries — aborting.");
            }
            return;
        }

        PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] ProcessNextPurchase — {entries.Count} entries available.");

        if (TryBuyCard(room, entries))        return;
        if (TryBuyRelic(room, entries))       return;
        if (TryBuyPotion(room, entries))      return;
        if (TryBuyCardRemoval(room, entries)) return;

        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] No more shop purchase commands pending.");
    }

    private static async Task RetryAfterDelay(NMerchantRoom room, int retriesLeft)
    {
        await Task.Delay(100);
        Callable.From(() => ProcessNextPurchase(room, retriesLeft)).CallDeferred();
    }

    private static bool TryBuyCard(NMerchantRoom room, List<MerchantEntry> entries)
    {
        if (!ReplayEngine.PeekBuyCard(out string cardTitle))
            return false;

        MerchantCardEntry? entry = entries
            .OfType<MerchantCardEntry>()
            .FirstOrDefault(e => e.CreationResult?.Card?.Title == cardTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] Card '{cardTitle}' not found in merchant entries — aborting.");
            return false;
        }

        ReplayRunner.ExecuteBuyCard(out _);
        InvokePurchase(entry);
        PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] Triggered purchase of card '{cardTitle}'.");
        Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
        return true;
    }

    private static bool TryBuyRelic(NMerchantRoom room, List<MerchantEntry> entries)
    {
        if (!ReplayEngine.PeekBuyRelic(out string relicTitle))
            return false;

        MerchantRelicEntry? entry = entries
            .OfType<MerchantRelicEntry>()
            .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == relicTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] Relic '{relicTitle}' not found in merchant entries — aborting.");
            return false;
        }

        ReplayRunner.ExecuteBuyRelic(out _);
        InvokePurchase(entry);
        PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] Triggered purchase of relic '{relicTitle}'.");
        Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
        return true;
    }

    private static bool TryBuyPotion(NMerchantRoom room, List<MerchantEntry> entries)
    {
        if (!ReplayEngine.PeekBuyPotion(out string potionTitle))
            return false;

        MerchantPotionEntry? entry = entries
            .OfType<MerchantPotionEntry>()
            .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == potionTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] Potion '{potionTitle}' not found in merchant entries — aborting.");
            return false;
        }

        ReplayRunner.ExecuteBuyPotion(out _);
        InvokePurchase(entry);
        PlayerActionBuffer.LogToDevConsole($"[ShopReplayPatch] Triggered purchase of potion '{potionTitle}'.");
        Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
        return true;
    }

    private static bool TryBuyCardRemoval(NMerchantRoom room, List<MerchantEntry> entries)
    {
        if (!ReplayEngine.PeekBuyCardRemoval())
            return false;

        MerchantCardRemovalEntry? entry = entries.OfType<MerchantCardRemovalEntry>().FirstOrDefault();

        if (entry == null)
        {
            PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] Card removal entry not found — aborting.");
            return false;
        }

        ReplayRunner.ExecuteBuyCardRemoval();
        InvokePurchase(entry);
        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] Triggered card removal purchase.");
        Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
        return true;
    }

    // ── Invoke OnTryPurchaseWrapper via reflection ────────────────────────────

    /// <summary>
    /// Invokes OnTryPurchaseWrapper on the most-derived type of the entry,
    /// filling any extra parameters (e.g. MerchantCardRemovalEntry.cancelable)
    /// with their declared default values.
    /// </summary>
    private static void InvokePurchase(MerchantEntry entry)
    {
        // Prefer an override declared on the concrete type; fall back to inherited.
        MethodInfo? method = entry.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(MerchantEntry.OnTryPurchaseWrapper)
                              && m.DeclaringType == entry.GetType())
            ?? entry.GetType().GetMethod(
                nameof(MerchantEntry.OnTryPurchaseWrapper),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (method == null)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[ShopReplayPatch] OnTryPurchaseWrapper not found on {entry.GetType().Name}.");
            return;
        }

        object?[] args = method.GetParameters()
            .Select(p => p.HasDefaultValue
                ? p.DefaultValue
                : p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null)
            .ToArray();

        object? result = method.Invoke(entry, args);
        if (result is Task task)
            TaskHelper.RunSafely(task);
    }

    // ── Entry discovery via reflection ────────────────────────────────────────

    /// <summary>
    /// Scans fields of NMerchantRoom (and one level of child-object fields) for
    /// the first IEnumerable that yields at least one MerchantEntry.
    /// </summary>
    /// <summary>
    /// Navigates NMerchantRoom → Room → Inventory and aggregates every
    /// MerchantEntry found across all fields of the inventory object.
    /// The inventory holds separate sub-collections per item type
    /// (cards, relics, potions, card-removal), so we collect them all and
    /// let the TryBuy* helpers filter by concrete type.
    /// </summary>
    private static List<MerchantEntry>? GetEntries(NMerchantRoom room)
    {
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                              | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        object? roomModel = room.GetType()
            .GetProperty("Room", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(room);

        if (roomModel == null)
        {
            PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] NMerchantRoom.Room is null.");
            return null;
        }

        object? inventory = roomModel.GetType()
            .GetProperty("Inventory", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(roomModel);

        if (inventory == null)
        {
            PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] MerchantRoom.Inventory is null.");
            return null;
        }

        List<MerchantEntry> all = new();
        foreach (FieldInfo field in inventory.GetType().GetFields(bf))
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

        if (all.Count > 0)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[ShopReplayPatch] Found {all.Count} entries in Room.Inventory " +
                $"({string.Join(", ", all.Select(e => e.GetType().Name))}).");
            return all;
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[ShopReplayPatch] Inventory ({inventory.GetType().Name}) yielded no MerchantEntry objects.");
        return null;
    }

    private static bool TryExtractEntries(object? value, out List<MerchantEntry>? entries)
    {
        entries = null;
        if (value is not IEnumerable enumerable)
            return false;

        List<MerchantEntry> result = new();
        foreach (object? item in enumerable)
            if (item is MerchantEntry e)
                result.Add(e);

        if (result.Count > 0)
        {
            entries = result;
            return true;
        }
        return false;
    }
}
