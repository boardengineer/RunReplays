using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;

using RunReplays.Patch;
namespace RunReplays.Commands;

/// <summary>
/// Open the fake merchant event shop inventory.
/// Recorded as: "OpenFakeShop"
/// </summary>
public sealed class OpenFakeShopCommand : ReplayCommand
{

    private OpenFakeShopCommand(string raw) : base(raw) { }

    public override string Describe() => "open fake shop";

    public override ExecuteResult Execute()
    {
        var merchant = FakeMerchantReplayPatch.ActiveInstance;
        if (merchant == null || !merchant.IsInsideTree())
            return ExecuteResult.Retry(200);

        var openMethod = FakeMerchantReplayPatch.OpenInventoryMethod;
        if (openMethod == null)
        {
            PlayerActionBuffer.LogToDevConsole("[OpenFakeShop] OpenInventory method not found.");
            return ExecuteResult.Ok();
        }

        // Check if entries are already available before opening.
        var entries = FakeMerchantReplayPatch.GetEntries(merchant);
        if (entries == null || entries.Count == 0)
            return ExecuteResult.Retry(200);

        PlayerActionBuffer.LogDispatcher("[FakeShop] Opening inventory.");
        openMethod.Invoke(merchant, null);

        return ExecuteResult.Ok();
    }

    public static OpenFakeShopCommand? TryParse(string raw)
        => raw == "OpenFakeShop" ? new OpenFakeShopCommand(raw) : null;
}
