using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Drives automatic shop replay.
///
/// ShopRoomReadyPatch hooks NMerchantRoom._Ready.  When a replay is active it
/// signals shop readiness so the dispatcher can execute shop commands.
///
/// ShopOpenedReplayPatch hooks NMerchantRoom.OpenInventory.  It signals shop
/// readiness after the inventory opens.
///
/// All purchase commands (BuyCard, BuyRelic, BuyPotion, BuyCardRemoval) are
/// handled by typed ReplayCommand classes in Commands/ShopCommands.cs.
///
/// Merchant entries are located by scanning NMerchantRoom → Room → Inventory
/// for MerchantEntry objects via reflection.
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
        RngCheckpointLogger.Log("Shop (NMerchantRoom._Ready)");

        if (!ReplayEngine.IsActive)
            return;

        PlayerActionBuffer.LogDispatcher("[Shop] NMerchantRoom._Ready fired.");
        ShopOpenedReplayPatch.ActiveRoom = __instance;
        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Shop);
        ReplayDispatcher.DispatchNow();
    }
}

// ── Resume dispatch after card-removal async flow completes ───────────────────

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

        PlayerActionBuffer.LogToDevConsole(
            "[ShopReplayPatch] Card removal purchase completed — resuming shop loop.");
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

        PlayerActionBuffer.LogToDevConsole(
            "[ShopReplayPatch] Card removal purchase failed — resuming shop loop.");
    }
}

// ── Signal shop readiness when inventory opens ────────────────────────────────

[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
public static class ShopOpenedReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMerchantRoom __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ActiveRoom = __instance;
        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Shop);
        ReplayDispatcher.DispatchNow();
    }

    internal static NMerchantRoom? ActiveRoom;
    internal static bool           CardRemovalInProgress;

    // Used by ProceedButtonReplayPatch to avoid a race where NProceedButton._Ready
    // fires and consumes the MoveToMapCoordAction before shop commands finish.
    internal static bool IsShopReplayActive;

    // ── Invoke OnTryPurchaseWrapper via reflection ────────────────────────────

    /// <summary>
    /// Invokes OnTryPurchaseWrapper on the most-derived type of the entry,
    /// filling any extra parameters (e.g. MerchantCardRemovalEntry.cancelable)
    /// with their declared default values.
    /// </summary>
    internal static void InvokePurchase(MerchantEntry entry)
    {
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
    /// Navigates NMerchantRoom → Room → Inventory and aggregates every
    /// MerchantEntry found across all fields of the inventory object.
    /// </summary>
    internal static List<MerchantEntry>? GetEntries(NMerchantRoom room)
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
}
