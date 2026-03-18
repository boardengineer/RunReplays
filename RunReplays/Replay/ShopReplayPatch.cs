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
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

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
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Shop);

        if (!ReplayEngine.PeekOpenShop())
            return;

        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] NMerchantRoom ready — deferring OpenInventory.");
        Callable.From(() => __instance.OpenInventory()).CallDeferred();
    }
}

// ── Resume shop loop after card-removal async flow completes ─────────────────
//
// TryBuyCardRemoval skips the immediate deferred ProcessNextPurchase call
// because the card-selection UI runs asynchronously; the loop must resume only
// after InvokePurchaseCompleted or InvokePurchaseFailed fires.

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
public static class ShopCardRemovalCompletedReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!ShopOpenedReplayPatch.CardRemovalInProgress)
            return;

        ShopOpenedReplayPatch.CardRemovalInProgress = false;

        if (!ReplayEngine.IsActive)
            return;

        NMerchantRoom? room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return;

        PlayerActionBuffer.LogToDevConsole(
            "[ShopReplayPatch] Card removal purchase completed — resuming shop loop.");
        Callable.From(() => ShopOpenedReplayPatch.ProcessNextPurchase(room)).CallDeferred();
    }
}

[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseFailed))]
public static class ShopCardRemovalFailedReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!ShopOpenedReplayPatch.CardRemovalInProgress)
            return;

        ShopOpenedReplayPatch.CardRemovalInProgress = false;

        if (!ReplayEngine.IsActive)
            return;

        NMerchantRoom? room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return;

        // Consume the pending RemoveCardFromDeck command that will never
        // be reached because the purchase failed.
        if (ReplayEngine.PeekRemoveCardFromDeck(out _))
            ReplayEngine.ConsumeRemoveCardFromDeck(out _);

        PlayerActionBuffer.LogToDevConsole(
            "[ShopReplayPatch] Card removal purchase failed — resuming shop loop.");
        Callable.From(() => ShopOpenedReplayPatch.ProcessNextPurchase(room)).CallDeferred();
    }
}

// ── Handle purchases once the shop UI is open ─────────────────────────────────

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
public static class ShopOpenedReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMerchantRoom __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Shop);

        if (!ReplayEngine.PeekOpenShop())
            return;

        ReplayRunner.ExecuteOpenShop();
        IsShopReplayActive = true;
        ActiveRoom = __instance;
    }

    /// <summary>Called by ReplayDispatcher to trigger next shop action.</summary>
    internal static void DispatchFromEngine()
    {
        if (ActiveRoom == null || !ActiveRoom.IsInsideTree())
            return;
        Callable.From(() => ProcessNextPurchase(ActiveRoom)).CallDeferred();
    }

    // Accessed by ShopCardRemovalCompletedReplayPatch after InvokePurchaseCompleted.
    internal static NMerchantRoom? ActiveRoom;
    internal static bool           CardRemovalInProgress;

    // True from when OpenShop is consumed until ProcessNextPurchase opens the map
    // or determines there is nothing more to do.  Used by ProceedButtonReplayPatch
    // to avoid a race where NProceedButton._Ready fires mid-loop and consumes the
    // MoveToMapCoordAction before ProcessNextPurchase reaches it.
    internal static bool IsShopReplayActive;

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

        if (TryDiscardPotionInShop(room))        return;
        if (TryBuyCard(room, entries))           return;
        if (TryBuyRelic(room, entries))          return;
        if (TryBuyPotion(room, entries))         return;
        if (TryBuyCardRemoval(room, entries))    return;

        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] No more shop purchase commands pending.");

        // All purchases done — if a map move is next, open the map directly.
        if (!ReplayEngine.PeekMapNode(out _, out _))
        {
            // A stray OpenShop (recorded when OpenInventory was called while the
            // inventory was already open) can appear here.  Consume it and retry
            // so the real map-move or buy commands that follow are processed.
            if (ReplayEngine.PeekOpenShop())
            {
                PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] Consuming stray OpenShop command — retrying.");
                ReplayRunner.ExecuteOpenShop();
                Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
                return;
            }

            IsShopReplayActive = false;
            return;
        }

        IsShopReplayActive = false;
        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] Map move next — opening map.");
        NMapScreen.Instance?.Open();
        NMapScreen.Instance?.SetTravelEnabled(true);
    }

    private static async Task RetryAfterDelay(NMerchantRoom room, int retriesLeft)
    {
        await Task.Delay(100);
        Callable.From(() => ProcessNextPurchase(room, retriesLeft)).CallDeferred();
    }

    private static bool TryDiscardPotionInShop(NMerchantRoom room)
    {
        if (!ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
            return false;

        PlayerActionBuffer.LogToDevConsole(
            $"[ShopReplayPatch] Potion discard slot={discardSlot} during shop — executing.");
        CardPlayReplayPatch.TryDiscardPotion();
        // Resume the shop loop after a short delay so the discard action completes.
        TaskHelper.RunSafely(ResumeShopAfterDelay(room));
        return true;
    }

    private static async Task ResumeShopAfterDelay(NMerchantRoom room)
    {
        await Task.Delay(200);
        Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
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

        // During recording, game actions (e.g. gold changes) may be interleaved
        // between BuyCardRemoval and RemoveCardFromDeck.  Skip past them so the
        // DeckRemovalReplayPatch selector finds RemoveCardFromDeck at the front.
        ReplayEngine.SkipToRemoveCardFromDeck();

        // Card removal triggers an async FromDeckForRemoval UI; ProcessNextPurchase
        // must not be deferred here — it runs after InvokePurchaseCompleted or
        // InvokePurchaseFailed fires (handled by the corresponding replay patches).
        ActiveRoom             = room;
        CardRemovalInProgress  = true;

        try
        {
            InvokePurchase(entry);
        }
        catch (Exception ex)
        {
            CardRemovalInProgress = false;
            PlayerActionBuffer.LogToDevConsole(
                $"[ShopReplayPatch] Card removal purchase threw — {ex.GetType().Name}: {ex.Message}");

            if (ReplayEngine.PeekRemoveCardFromDeck(out _))
                ReplayEngine.ConsumeRemoveCardFromDeck(out _);

            Callable.From(() => ProcessNextPurchase(room)).CallDeferred();
            return true;
        }

        PlayerActionBuffer.LogToDevConsole("[ShopReplayPatch] Triggered card removal purchase — waiting for selection.");
        return true;
    }

    // ── Invoke OnTryPurchaseWrapper via reflection ────────────────────────────

    /// <summary>
    /// Invokes OnTryPurchaseWrapper on the most-derived type of the entry,
    /// filling any extra parameters (e.g. MerchantCardRemovalEntry.cancelable)
    /// with their declared default values.
    /// </summary>
    internal static void InvokePurchase(MerchantEntry entry)
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

        foreach (PropertyInfo prop in inventory.GetType().GetProperties(bf))
        {
            if (prop.GetIndexParameters().Length > 0)
                continue;

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
