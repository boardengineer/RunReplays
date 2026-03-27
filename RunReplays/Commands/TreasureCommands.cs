using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Open the treasure chest.
/// Recorded as: "OpenChest # {relicTitle}"
/// Legacy:      "TakeChestRelic {relicTitle}"
///
/// Emits the chest button's Released signal to trigger OpenChest().
/// A TakeChestRelic command follows to pick the relic.
/// </summary>
public sealed class OpenChestCommand : ReplayCommand
{
    private const string Prefix = "OpenChest";
    private const string LegacyPrefix = "TakeChestRelic ";

    public OpenChestCommand() : base("") { }

    public override string ToString() => Prefix;

    public override string Describe()
        => Comment != null ? $"open chest ({Comment})" : "open chest";

    public override ExecuteResult Execute()
    {
        NTreasureRoom? room = TreasureRoomReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
        if (chest == null)
        {
            PlayerActionBuffer.LogToDevConsole("[OpenChest] Chest button node not found.");
            return ExecuteResult.Retry(200);
        }

        chest.EmitSignal(NClickableControl.SignalName.Released, chest);
        return ExecuteResult.Ok();
    }

    public static OpenChestCommand? TryParse(string raw)
    {
        // New format: "OpenChest"
        if (raw == Prefix)
            return new OpenChestCommand();

        // Legacy: "TakeChestRelic {relicTitle}"
        if (raw.StartsWith(LegacyPrefix))
            return new OpenChestCommand { Comment = raw.Substring(LegacyPrefix.Length) };

        return null;
    }
}

/// <summary>
/// Pick the relic from the opened treasure chest.
/// Recorded as: "TakeChestRelic"
/// Legacy:      "NetPickRelicAction for player {netId} index {relicIndex}"
///
/// Always picks relic at index 0 (treasure chests offer one relic).
/// </summary>
public sealed class TakeChestRelicCommand : ReplayCommand
{
    private const string Cmd = "TakeChestRelic";
    private const string LegacyPrefix = "NetPickRelicAction for player ";
    private const string LegacyIndexMarker = " index ";

    public TakeChestRelicCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "take chest relic";

    public override ExecuteResult Execute()
    {
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        PlayerActionBuffer.LogDispatcher("[TakeChestRelic] PickRelicLocally(0)");
        Callable.From(() => sync.PickRelicLocally(0)).CallDeferred();
        return ExecuteResult.Ok();
    }

    public static TakeChestRelicCommand? TryParse(string raw)
    {
        // New format: "TakeChestRelic"
        if (raw == Cmd)
            return new TakeChestRelicCommand();

        // Legacy: "NetPickRelicAction for player {id} index {idx}"
        if (raw.StartsWith(LegacyPrefix))
        {
            int markerPos = raw.LastIndexOf(LegacyIndexMarker);
            if (markerPos >= 0)
                return new TakeChestRelicCommand();
        }

        return null;
    }
}
