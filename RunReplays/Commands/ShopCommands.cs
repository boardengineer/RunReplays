using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using RunReplays.Patch;

namespace RunReplays.Commands;

/// <summary>
///     Opens the shop inventory.
///     Recorded as: "OpenShop"
/// </summary>
public sealed class OpenShopCommand : ReplayCommand
{
    private OpenShopCommand(string raw) : base(raw)
    {
    }

    public override string Describe()
    {
        return "open shop";
    }

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
    {
        return raw == "OpenShop" ? new OpenShopCommand(raw) : null;
    }
}

/// <summary>
///     Buys a card from the shop.
///     Recorded as: "BuyCard {title}"
/// </summary>
public sealed class BuyCardCommand : ReplayCommand
{
    private const string Prefix = "BuyCard ";


    private BuyCardCommand(string raw, string cardTitle) : base(raw)
    {
        CardTitle = cardTitle;
    }

    public string CardTitle { get; }

    public override string Describe()
    {
        return $"buy card '{CardTitle}'";
    }

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
///     Buys a relic from the shop.
///     Recorded as: "BuyRelic {title}"
/// </summary>
public sealed class BuyRelicCommand : ReplayCommand
{
    private const string Prefix = "BuyRelic ";


    private BuyRelicCommand(string raw, string relicTitle) : base(raw)
    {
        RelicTitle = relicTitle;
    }

    public string RelicTitle { get; }

    public override string Describe()
    {
        return $"buy relic '{RelicTitle}'";
    }

    public override ExecuteResult Execute()
    {
        List<MerchantEntry>? entries = null;

        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room != null && room.IsInsideTree())
            entries = ShopOpenedReplayPatch.GetEntries(room);

        // Fall back to fake merchant if regular shop isn't active.
        if ((entries == null || entries.Count == 0) && FakeMerchantReplayPatch.ActiveInstance != null)
            entries = FakeMerchantReplayPatch.GetEntries(FakeMerchantReplayPatch.ActiveInstance);

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
}

/// <summary>
///     Buys card removal from the shop.
///     Recorded as: "BuyCardRemoval"
///     Card removal opens an async deck selection UI.  Execute triggers the purchase
///     and sets CardRemovalInProgress so the existing ShopCardRemovalCompleted/Failed
///     patches resume the shop loop after the selection finishes.
/// </summary>
public sealed class BuyCardRemovalCommand : ReplayCommand
{
    private BuyCardRemovalCommand(string raw) : base(raw)
    {
    }

    public override string Describe()
    {
        return "buy card removal";
    }

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
    {
        return raw == "BuyCardRemoval" ? new BuyCardRemovalCommand(raw) : null;
    }
}

/// <summary>
///     Buys a potion from the shop.
///     Recorded as: "BuyPotion {title}"
/// </summary>
public sealed class BuyPotionCommand : ReplayCommand
{
    private const string Prefix = "BuyPotion ";


    private BuyPotionCommand(string raw, string potionTitle) : base(raw)
    {
        PotionTitle = potionTitle;
    }

    public string PotionTitle { get; }

    public override string Describe()
    {
        return $"buy potion '{PotionTitle}'";
    }

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