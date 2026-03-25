using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays.Commands;

/// <summary>
/// Open the treasure chest.
/// Recorded as: "TakeChestRelic {relicTitle}"
///
/// Emits the chest button's Released signal to trigger OpenChest().
/// The actual relic pick is driven by the subsequent NetPickRelicAction command.
/// </summary>
public sealed class TakeChestRelicCommand : ReplayCommand
{
    private const string Prefix = "TakeChestRelic ";

    public string RelicTitle { get; }

    public override ReplayState.ReadyState RequiredState => ReplayState.ReadyState.Treasure;

    private TakeChestRelicCommand(string raw, string relicTitle) : base(raw)
    {
        RelicTitle = relicTitle;
    }

    public override string Describe() => $"open chest (relic '{RelicTitle}')";

    public override ExecuteResult Execute()
    {
        NTreasureRoom? room = TreasureRoomReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
        if (chest == null)
        {
            PlayerActionBuffer.LogToDevConsole("[TakeChestRelic] Chest button node not found.");
            return ExecuteResult.Retry(200);
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[TakeChestRelic] Opening chest (expected relic '{RelicTitle}').");
        chest.EmitSignal(NClickableControl.SignalName.Released, chest);
        return ExecuteResult.Ok();
    }

    public static TakeChestRelicCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;
        return new TakeChestRelicCommand(raw, raw.Substring(Prefix.Length));
    }
}

/// <summary>
/// Pick a relic from the treasure room after the chest is opened.
/// Recorded as: "NetPickRelicAction for player {netId} index {relicIndex}"
/// </summary>
public sealed class NetPickRelicCommand : ReplayCommand
{
    private const string Prefix = "NetPickRelicAction for player ";
    private const string IndexMarker = " index ";

    public int RelicIndex { get; }

    public override ReplayState.ReadyState RequiredState => ReplayState.ReadyState.Treasure;

    private NetPickRelicCommand(string raw, int relicIndex) : base(raw)
    {
        RelicIndex = relicIndex;
    }

    public override string Describe() => $"pick relic index={RelicIndex}";

    public override ExecuteResult Execute()
    {
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        PlayerActionBuffer.LogDispatcher($"[NetPickRelic] PickRelicLocally({RelicIndex})");
        Callable.From(() => sync.PickRelicLocally(RelicIndex)).CallDeferred();
        return ExecuteResult.Ok();
    }

    public static NetPickRelicCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        int markerPos = raw.LastIndexOf(IndexMarker);
        if (markerPos < 0) return null;

        if (!int.TryParse(raw.AsSpan(markerPos + IndexMarker.Length), out int relicIndex))
            return null;

        return new NetPickRelicCommand(raw, relicIndex);
    }
}
