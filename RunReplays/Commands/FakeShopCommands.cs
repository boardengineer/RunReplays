using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;

using RunReplays.Patches;
namespace RunReplays.Commands;

/// <summary>
/// Open the fake merchant event shop inventory.
/// Recorded as: "OpenFakeShop"
/// </summary>
public sealed class OpenFakeShopCommand : ReplayCommand
{
    private static readonly MethodInfo? OpenInventoryMethod =
        typeof(NFakeMerchant).GetMethod("OpenInventory",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public OpenFakeShopCommand() : base("") { }

    public override string ToString() => "OpenFakeShop";

    public override string Describe() => "open fake shop";

    public override ExecuteResult Execute()
    {
        var merchant = ReplayState.FakeMerchantInstance;
        if (merchant == null || !merchant.IsInsideTree())
            return ExecuteResult.Retry(200);

        var openMethod = OpenInventoryMethod;
        if (openMethod == null)
        {
            PlayerActionBuffer.LogToDevConsole("[OpenFakeShop] OpenInventory method not found.");
            return ExecuteResult.Ok();
        }
        
        openMethod.Invoke(merchant, null);

        return ExecuteResult.Ok();
    }

    public static OpenFakeShopCommand? TryParse(string raw)
        => raw == "OpenFakeShop" ? new OpenFakeShopCommand() : null;
}
