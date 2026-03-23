using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;

namespace RunReplays.Commands;

/// <summary>
/// Opens the shop inventory.
/// Recorded as: "OpenShop"
/// </summary>
public sealed class OpenShopCommand : ReplayCommand
{
    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Shop;

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
/// Buys a relic from the shop.
/// Recorded as: "BuyRelic {title}"
/// </summary>
public sealed class BuyRelicCommand : ReplayCommand
{
    private const string Prefix = "BuyRelic ";

    public string RelicTitle { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Shop;

    private BuyRelicCommand(string raw, string relicTitle) : base(raw)
    {
        RelicTitle = relicTitle;
    }

    public override string Describe() => $"buy relic '{RelicTitle}'";

    public override ExecuteResult Execute()
    {
        var room = ShopOpenedReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var entries = ShopOpenedReplayPatch.GetEntries(room);
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
/// Buys card removal from the shop.
/// Recorded as: "BuyCardRemoval"
///
/// Card removal opens an async deck selection UI.  Execute triggers the purchase
/// and sets CardRemovalInProgress so the existing ShopCardRemovalCompleted/Failed
/// patches resume the shop loop after the selection finishes.
/// </summary>
public sealed class BuyCardRemovalCommand : ReplayCommand
{
    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Shop;

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

        ReplayEngine.SkipToRemoveCardFromDeck();
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

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Shop;

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
