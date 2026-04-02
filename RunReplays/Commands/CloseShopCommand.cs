using System.Reflection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace RunReplays.Commands;

/// <summary>
/// Closes the shop inventory screen.
/// Recorded as: "CloseShop"
/// </summary>
public sealed class CloseShopCommand : ReplayCommand
{
    private const string Cmd = "CloseShop";

    private static readonly MethodInfo? CloseMethod =
        typeof(NMerchantInventory).GetMethod("Close",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public CloseShopCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "close shop";

    public override ExecuteResult Execute()
    {
        var room = ReplayState.ActiveMerchantRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        var inventory = room.Inventory;
        if (inventory == null || !inventory.IsOpen)
            return ExecuteResult.Retry(200);

        CloseMethod?.Invoke(inventory, null);
        return ExecuteResult.Ok();
    }

    public static CloseShopCommand? TryParse(string raw)
        => raw == Cmd ? new CloseShopCommand() : null;
}
