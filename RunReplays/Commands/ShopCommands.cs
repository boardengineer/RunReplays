using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;

using RunReplays.Patches;
namespace RunReplays.Commands;

/// <summary>
/// Opens the shop inventory.
/// Recorded as: "OpenShop"
/// </summary>
public sealed class OpenShopCommand : ReplayCommand
{

    private OpenShopCommand(string raw) : base(raw) { }

    public override string Describe() => "open shop";

    public override ExecuteResult Execute()
    {
        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        ShopOpenedReplayPatch.IsShopReplayActive = true;
        Callable.From(() => room.OpenInventory()).CallDeferred();
        return ExecuteResult.Ok();
    }

    public static OpenShopCommand? TryParse(string raw)
        => raw == "OpenShop" ? new OpenShopCommand(raw) : null;
}

/// <summary>
/// Buys a card from the shop.
/// Recorded as: "BuyCard {title}"
/// </summary>
public sealed class BuyCardCommand : ReplayCommand
{
    private const string Prefix = "BuyCard ";

    public string CardTitle { get; }


    private BuyCardCommand(string raw, string cardTitle) : base(raw)
    {
        CardTitle = cardTitle;
    }

    public override string Describe() => $"buy card '{CardTitle}'";

    public override ExecuteResult Execute()
    {
        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var entries = ShopOpenedReplayPatch.GetEntries(room);
        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        var entry = entries.OfType<MerchantCardEntry>()
            .FirstOrDefault(e => e.CreationResult?.Card?.Title == CardTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogMigrationWarning($"[BuyCard] Card '{CardTitle}' not found — skipping.");
            return ExecuteResult.Ok();
        }

        ShopOpenedReplayPatch.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new BuyCardCommand(raw, raw.Substring(Prefix.Length));
    }
}

/// <summary>
/// Buys a relic from the shop.
/// Recorded as: "BuyRelic {title}"
/// </summary>
public sealed class BuyRelicCommand : ReplayCommand
{
    private const string Prefix = "BuyRelic ";

    public string RelicTitle { get; }


    private BuyRelicCommand(string raw, string relicTitle) : base(raw)
    {
        RelicTitle = relicTitle;
    }

    public override string Describe() => $"buy relic '{RelicTitle}'";

    public override ExecuteResult Execute()
    {
        List<MerchantEntry>? entries = null;

        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room != null && room.IsInsideTree())
            entries = ShopOpenedReplayPatch.GetEntries(room);

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
        
        ShopOpenedReplayPatch.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyRelicCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new BuyRelicCommand(raw, raw.Substring(Prefix.Length));
    }

    // NFakeMerchant._event is the FakeMerchant model which holds the inventory.
    private static readonly FieldInfo? EventField =
        typeof(NFakeMerchant).GetField("_event",
            BindingFlags.NonPublic | BindingFlags.Instance);

    // FakeMerchant._inventory is the MerchantInventory with relic entries.
    private static readonly FieldInfo? InventoryField =
        typeof(NFakeMerchant).Assembly
            .GetType("MegaCrit.Sts2.Core.Models.Events.FakeMerchant")
            ?.GetField("_inventory", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static List<MerchantEntry>? GetFakeMerchantEntries(NFakeMerchant merchant)
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

/// <summary>
/// Buys card removal from the shop.
/// Recorded as: "BuyCardRemoval"
///
/// Card removal opens an async deck selection UI.  Execute triggers the purchase
/// and sets CardRemovalInProgress so the existing ShopCardRemovalCompleted/Failed
/// patches resume the shop loop after the selection finishes.
/// </summary>
public sealed class BuyCardRemovalCommand : ReplayCommand
{

    private BuyCardRemovalCommand(string raw) : base(raw) { }

    public override string Describe() => "buy card removal";

    public override ExecuteResult Execute()
    {
        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var entries = ShopOpenedReplayPatch.GetEntries(room);
        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        var entry = entries.OfType<MerchantCardRemovalEntry>().FirstOrDefault();
        if (entry == null)
        {
            PlayerActionBuffer.LogMigrationWarning("[BuyCardRemoval] Card removal entry not found — skipping.");
            return ExecuteResult.Ok();
        }

        // ReplayEngine.SkipToRemoveCardFromDeck();
        ShopOpenedReplayPatch.CardRemovalInProgress = true;
        ShopOpenedReplayPatch.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyCardRemovalCommand? TryParse(string raw)
        => raw == "BuyCardRemoval" ? new BuyCardRemovalCommand(raw) : null;
}

/// <summary>
/// Buys a potion from the shop.
/// Recorded as: "BuyPotion {title}"
/// </summary>
public sealed class BuyPotionCommand : ReplayCommand
{
    private const string Prefix = "BuyPotion ";

    public string PotionTitle { get; }


    private BuyPotionCommand(string raw, string potionTitle) : base(raw)
    {
        PotionTitle = potionTitle;
    }

    public override string Describe() => $"buy potion '{PotionTitle}'";

    public override ExecuteResult Execute()
    {
        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var entries = ShopOpenedReplayPatch.GetEntries(room);
        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        var entry = entries.OfType<MerchantPotionEntry>()
            .FirstOrDefault(e => e.Model?.Title.GetFormattedText() == PotionTitle);

        if (entry == null)
        {
            PlayerActionBuffer.LogMigrationWarning($"[BuyPotion] Potion '{PotionTitle}' not found — skipping.");
            return ExecuteResult.Ok();
        }

        ShopOpenedReplayPatch.InvokePurchase(entry);
        return ExecuteResult.Ok();
    }

    public static BuyPotionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new BuyPotionCommand(raw, raw.Substring(Prefix.Length));
    }
}
