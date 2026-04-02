using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using RunReplays.Patches.Replay;

namespace RunReplays.Commands;

/// <summary>
/// Presses the proceed button to return to the map.
/// Works on rewards screens, shop rooms, treasure rooms, and rest sites.
/// Recorded as: "ProceedToMap"
/// </summary>
public sealed class ProceedToMapCommand : ReplayCommand
{
    private const string Cmd = "ProceedToMap";

    private static readonly MethodInfo? RewardsOnProceedMethod =
        typeof(NRewardsScreen).GetMethod("OnProceedButtonPressed",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    private static readonly MethodInfo? TreasureOnProceedMethod =
        typeof(NTreasureRoom).GetMethod("OnProceedButtonPressed",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    private static readonly MethodInfo? ShopHideScreenMethod =
        typeof(NMerchantRoom).GetMethod("HideScreen",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    private static readonly MethodInfo? RestSiteOnProceedMethod =
        typeof(NRestSiteRoom).GetMethod("OnProceedButtonReleased",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public ProceedToMapCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "proceed to map";

    public override ExecuteResult Execute()
    {
        // Try rewards screen
        var rewardsScreen = ReplayState.ActiveRewardsScreen;
        if (rewardsScreen != null && rewardsScreen.IsInsideTree())
        {
            if (TryPressProceedButton(rewardsScreen, RewardsOnProceedMethod))
                return ExecuteResult.Ok();
        }

        // Try treasure room
        var treasureRoom = TreasureRoomReplayPatch.ActiveRoom;
        if (treasureRoom != null && GodotObject.IsInstanceValid(treasureRoom) && treasureRoom.IsInsideTree())
        {
            if (TryPressProceedButton(treasureRoom, TreasureOnProceedMethod))
                return ExecuteResult.Ok();
        }

        // Try shop room
        var shopRoom = ReplayState.ActiveMerchantRoom;
        if (shopRoom != null && GodotObject.IsInstanceValid(shopRoom) && shopRoom.IsInsideTree())
        {
            if (TryPressProceedButton(shopRoom, ShopHideScreenMethod))
                return ExecuteResult.Ok();
        }

        // Try rest site
        var restSite = NRestSiteRoom.Instance;
        if (restSite != null && GodotObject.IsInstanceValid(restSite) && restSite.IsInsideTree())
        {
            if (TryPressProceedButton(restSite, RestSiteOnProceedMethod))
                return ExecuteResult.Ok();
        }

        return ExecuteResult.Retry(200);
    }

    /// <summary>
    /// Returns true if any context has an enabled proceed button right now.
    /// </summary>
    public static bool IsAvailable()
    {
        var rewardsScreen = ReplayState.ActiveRewardsScreen;
        if (rewardsScreen != null && rewardsScreen.IsInsideTree()
            && HasEnabledProceedButton(rewardsScreen))
            return true;

        var treasureRoom = TreasureRoomReplayPatch.ActiveRoom;
        if (treasureRoom != null && GodotObject.IsInstanceValid(treasureRoom) && treasureRoom.IsInsideTree()
            && HasEnabledProceedButton(treasureRoom))
            return true;

        var shopRoom = ReplayState.ActiveMerchantRoom;
        if (shopRoom != null && GodotObject.IsInstanceValid(shopRoom) && shopRoom.IsInsideTree()
            && HasEnabledProceedButton(shopRoom))
            return true;

        var restSite = NRestSiteRoom.Instance;
        if (restSite != null && GodotObject.IsInstanceValid(restSite) && restSite.IsInsideTree()
            && HasEnabledProceedButton(restSite))
            return true;

        return false;
    }

    private static bool HasEnabledProceedButton(Node room)
    {
        var proceedBtn = Traverse.Create(room).Field("_proceedButton").GetValue<NProceedButton>();
        return proceedBtn != null && proceedBtn.IsEnabled;
    }

    private static bool TryPressProceedButton(Node room, MethodInfo? handler)
    {
        if (!HasEnabledProceedButton(room))
            return false;

        handler?.Invoke(room, new object?[] { null });
        return true;
    }

    public static ProceedToMapCommand? TryParse(string raw)
        => raw == Cmd ? new ProceedToMapCommand() : null;
}
