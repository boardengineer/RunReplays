using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using RunReplays.Utils;

namespace RunReplays.Commands;

/// <summary>
///     Opens the shop inventory.
///     Recorded as: "OpenShop"
/// </summary>
public sealed class OpenShopCommand : ReplayCommand
{
    public OpenShopCommand() : base("")
    {
    }

    public override string ToString() => "OpenShop";

    public override string Describe() => "open shop";

    public override ExecuteResult Execute()
    {
        var room = ReplayState.ActiveMerchantRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        Callable.From(() => room.OpenInventory()).CallDeferred();
        return ExecuteResult.Ok();
    }

    public static OpenShopCommand? TryParse(string raw)
    {
        return raw == "OpenShop" ? new OpenShopCommand() : null;
    }

    /// <summary>
    ///     Invokes OnTryPurchaseWrapper on the most-derived type of the entry,
    ///     filling any extra parameters (e.g. MerchantCardRemovalEntry.cancelable)
    ///     with their declared default values.
    /// </summary>
    internal static void InvokePurchase(MerchantEntry entry)
    {
        var method = entry.GetType()
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

        var args = method.GetParameters()
            .Select(p => p.HasDefaultValue
                ? p.DefaultValue
                : p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null)
            .ToArray();

        var result = method.Invoke(entry, args);
        if (result is Task task)
            TaskHelper.RunSafely(task);
    }

    /// <summary>
    ///     Scans NMerchantRoom.Inventory (NMerchantInventory) and its fields
    ///     for every MerchantEntry reachable from the open shop.
    /// </summary>
    internal static List<MerchantEntry>? GetEntries(NMerchantRoom room)
    {
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        // NMerchantRoom.Inventory is the NMerchantInventory Godot node —
        // access it directly (the old path via room.Room.Inventory was wrong;
        // NMerchantRoom has no "Room" property).
        var inventory = room.Inventory;

        if (inventory == null)
        {
            DiagnosticLog.Write("GetEntries", "room.Inventory is null");
            return null;
        }

        List<MerchantEntry> all = new();

        // Scan fields and properties of NMerchantInventory itself.
        ScanObject(inventory, bf, all);

        if (all.Count > 0)
        {
            DiagnosticLog.Write("GetEntries",
                $"Found {all.Count} entries on NMerchantInventory: {string.Join(", ", all.Select(e => e.GetType().Name))}");
            PlayerActionBuffer.LogToDevConsole(
                $"[GetEntries] Found {all.Count}: {string.Join(", ", all.Select(e => e.GetType().Name))}");
            return all;
        }

        // NMerchantInventory may wrap an inner data model — scan one level of
        // non-Node, non-primitive fields so we reach it without false positives
        // from the entire Godot scene graph.
        foreach (var field in inventory.GetType().GetFields(bf))
        {
            object? value;
            try { value = field.GetValue(inventory); }
            catch { continue; }
            if (value == null || value is string || value is Godot.GodotObject) continue;
            if (value.GetType().IsPrimitive || value.GetType().IsEnum) continue;
            ScanObject(value, bf, all);
        }

        if (all.Count > 0)
        {
            DiagnosticLog.Write("GetEntries",
                $"Found {all.Count} entries via NMerchantInventory inner model: {string.Join(", ", all.Select(e => e.GetType().Name))}");
            return all;
        }

        DiagnosticLog.Write("GetEntries",
            $"NMerchantInventory ({inventory.GetType().Name}) yielded no MerchantEntry objects. " +
            $"Fields: [{string.Join(", ", inventory.GetType().GetFields(bf).Select(f => f.Name))}]");
        return null;
    }

    private static void ScanObject(object obj, BindingFlags bf, List<MerchantEntry> all)
    {
        foreach (var field in obj.GetType().GetFields(bf))
        {
            object? value;
            try { value = field.GetValue(obj); }
            catch { continue; }
            CollectEntry(value, all);
        }

        foreach (var prop in obj.GetType().GetProperties(bf))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? value;
            try { value = prop.GetValue(obj); }
            catch { continue; }
            CollectEntry(value, all);
        }
    }

    private static void CollectEntry(object? value, List<MerchantEntry> all)
    {
        if (value == null) return;
        if (value is string) return;

        if (value is MerchantEntry single)
        {
            if (!all.Contains(single))
                all.Add(single);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (item is MerchantEntry e && !all.Contains(e))
                    all.Add(e);
        }
    }
}

/// <summary>
///     Buys a card from the shop.
///     Recorded as: "BuyCard {title}"
/// </summary>
public sealed class BuyCardCommand : ReplayCommand
{
    private const string Prefix = "BuyCard ";


    public BuyCardCommand(string cardTitle) : base("")
    {
        CardTitle = cardTitle;
    }

    public string CardTitle { get; }

    public override string ToString() => $"BuyCard {CardTitle}";

    public override string Describe() => $"buy card '{CardTitle}'";

    public override ExecuteResult Execute()
    {
        var room = ReplayState.ActiveMerchantRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var entries = OpenShopCommand.GetEntries(room);
        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        var entry = entries.OfType<MerchantCardEntry>()
            .FirstOrDefault(e => e.CreationResult?.Card?.Title == CardTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogMigrationWarning($"[BuyCard] Card '{CardTitle}' not found — skipping.");
            return ExecuteResult.Ok();
        }

        OpenShopCommand.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new BuyCardCommand(raw.Substring(Prefix.Length));
    }
}

/// <summary>
///     Buys a relic from the shop.
///     Recorded as: "BuyRelic {title}"
/// </summary>
public sealed class BuyRelicCommand : ReplayCommand
{
    private const string Prefix = "BuyRelic ";

    // NFakeMerchant._event is the FakeMerchant model which holds the inventory.
    private static readonly FieldInfo? EventField =
        typeof(NFakeMerchant).GetField("_event",
            BindingFlags.NonPublic | BindingFlags.Instance);

    // FakeMerchant._inventory is the MerchantInventory with relic entries.
    private static readonly FieldInfo? InventoryField =
        typeof(NFakeMerchant).Assembly
            .GetType("MegaCrit.Sts2.Core.Models.Events.FakeMerchant")
            ?.GetField("_inventory", BindingFlags.NonPublic | BindingFlags.Instance);


    public BuyRelicCommand(string relicTitle) : base("")
    {
        RelicTitle = relicTitle;
    }

    public string RelicTitle { get; }

    public override string ToString() => $"BuyRelic {RelicTitle}";

    public override string Describe() => $"buy relic '{RelicTitle}'";

    public override ExecuteResult Execute()
    {
        List<MerchantEntry>? entries = null;

        var room = ReplayState.ActiveMerchantRoom;
        if (room != null && room.IsInsideTree())
            entries = OpenShopCommand.GetEntries(room);

        // Fall back to fake merchant if regular shop isn't active.
        if ((entries == null || entries.Count == 0) && ReplayState.FakeMerchantInstance != null)
            entries = GetFakeMerchantEntries(ReplayState.FakeMerchantInstance);

        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        var entry = entries.OfType<MerchantRelicEntry>()
            .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == RelicTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogMigrationWarning($"[BuyRelic] Relic '{RelicTitle}' not found — skipping.");
            return ExecuteResult.Ok();
        }

        OpenShopCommand.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyRelicCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new BuyRelicCommand(raw.Substring(Prefix.Length));
    }

    internal static List<MerchantEntry>? GetFakeMerchantEntries(NFakeMerchant merchant)
    {
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        var eventModel = EventField?.GetValue(merchant);
        if (eventModel == null)
        {
            PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] _event field is null.");
            return null;
        }

        var inventory = InventoryField?.GetValue(eventModel);
        if (inventory == null)
        {
            PlayerActionBuffer.LogToDevConsole("[FakeMerchantReplayPatch] _inventory field is null.");
            return null;
        }

        var all = new List<MerchantEntry>();
        foreach (var field in inventory.GetType().GetFields(bf))
        {
            object? value;
            try
            {
                value = field.GetValue(inventory);
            }
            catch
            {
                continue;
            }

            if (value is IEnumerable enumerable)
                foreach (var item in enumerable)
                    if (item is MerchantEntry e)
                        all.Add(e);
                    else if (value is MerchantEntry single)
                        all.Add(single);
        }

        foreach (var prop in inventory.GetType().GetProperties(bf))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? value;
            try
            {
                value = prop.GetValue(inventory);
            }
            catch
            {
                continue;
            }

            if (value is IEnumerable enumerable)
                foreach (var item in enumerable)
                    if (item is MerchantEntry e && !all.Contains(e))
                        all.Add(e);
                    else if (value is MerchantEntry single && !all.Contains(single))
                        all.Add(single);
        }

        return all.Count > 0 ? all : null;
    }
}

/// <summary>
///     Buys card removal from the shop.
///     Recorded as: "BuyCardRemoval"
///     Card removal opens an async deck selection UI (NCardGridSelectionScreen).
///     Execute blocks until that screen is captured by CardGridScreenCapture.Prefix
///     so the following SelectGridCard command is immediately dispatchable.
/// </summary>
public sealed class BuyCardRemovalCommand : ReplayCommand
{
    private bool _purchaseTriggered;

    public BuyCardRemovalCommand() : base("")
    {
    }

    public override string ToString() => "BuyCardRemoval";

    public override string Describe() => "buy card removal";

    public override ExecuteResult Execute()
    {
        var room = ReplayState.ActiveMerchantRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        if (!_purchaseTriggered)
        {
            var entries = OpenShopCommand.GetEntries(room);
            if (entries == null || entries.Count == 0)
                return ExecuteResult.Retry(200);

            var entry = entries.OfType<MerchantCardRemovalEntry>().FirstOrDefault();
            if (entry == null)
            {
                PlayerActionBuffer.LogMigrationWarning("[BuyCardRemoval] Card removal entry not found — skipping.");
                return ExecuteResult.Ok();
            }

            OpenShopCommand.InvokePurchase(entry);
            _purchaseTriggered = true;
            PlayerActionBuffer.LogDispatcher("[BuyCardRemoval] Purchase triggered — waiting for deck selection screen.");
        }

        // Wait for NCardGridSelectionScreen.CardsSelected to fire.
        // CardGridScreenCapture.Prefix sets ActiveScreen then calls DispatchNow(),
        // which re-invokes Execute() directly — bypassing the retry delay.
        if (CardGridScreenCapture.ActiveScreen == null)
            return ExecuteResult.Retry(200);

        return ExecuteResult.Ok();
    }

    public static BuyCardRemovalCommand? TryParse(string raw)
    {
        return raw == "BuyCardRemoval" ? new BuyCardRemovalCommand() : null;
    }
}

/// <summary>
///     Buys a potion from the shop.
///     Recorded as: "BuyPotion {title}"
/// </summary>
public sealed class BuyPotionCommand : ReplayCommand
{
    private const string Prefix = "BuyPotion ";


    public BuyPotionCommand(string potionTitle) : base("")
    {
        PotionTitle = potionTitle;
    }

    public string PotionTitle { get; }

    public override string ToString() => $"BuyPotion {PotionTitle}";

    public override string Describe() => $"buy potion '{PotionTitle}'";

    public override ExecuteResult Execute()
    {
        var room = ReplayState.ActiveMerchantRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var entries = OpenShopCommand.GetEntries(room);
        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        var entry = entries.OfType<MerchantPotionEntry>()
            .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == PotionTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogMigrationWarning($"[BuyPotion] Potion '{PotionTitle}' not found — skipping.");
            return ExecuteResult.Ok();
        }

        OpenShopCommand.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyPotionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new BuyPotionCommand(raw.Substring(Prefix.Length));
    }
}