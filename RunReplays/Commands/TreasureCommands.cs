using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Open the treasure chest.
/// Recorded as: "OpenChest # {relicTitle}"
///
/// Emits the chest button's Released signal to trigger OpenChest().
/// A TakeChestRelic command follows to pick the relic.
/// </summary>
public sealed class OpenChestCommand : ReplayCommand
{
    private const string Prefix = "OpenChest";

    public OpenChestCommand() : base("") { }

    public override string ToString() => Prefix;

    public override string Describe()
        => Comment != null ? $"open chest ({Comment})" : "open chest";

    public override ExecuteResult Execute()
    {
        NTreasureRoom? room = TreasureRoomReplayPatch.ActiveRoom;
        if (room == null)
        {
            PlayerActionBuffer.LogDispatcher("[OpenChest] ActiveRoom is null; retrying.");
            return ExecuteResult.Retry(200);
        }

        if (!room.IsInsideTree())
        {
            PlayerActionBuffer.LogDispatcher("[OpenChest] ActiveRoom is not in tree; retrying.");
            return ExecuteResult.Retry(200);
        }

        NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
        if (chest == null)
        {
            PlayerActionBuffer.LogToDevConsole("[OpenChest] Chest button node not found.");
            return ExecuteResult.Retry(200);
        }

        PlayerActionBuffer.LogDispatcher(
            $"[OpenChest] Emit Released; chestInside={chest.IsInsideTree()} sync={TreasureSyncDebug.Describe(RunManager.Instance.TreasureRoomRelicSynchronizer)}");
        chest.EmitSignal(NClickableControl.SignalName.Released, chest);
        return ExecuteResult.Ok();
    }

    public static OpenChestCommand? TryParse(string raw)
    {
        if (raw == Prefix)
            return new OpenChestCommand();

        return null;
    }
}

/// <summary>
/// Pick the relic from the opened treasure chest.
/// Recorded as: "TakeChestRelic"
///
/// Always picks relic at index 0 (treasure chests offer one relic).
/// </summary>
public sealed class TakeChestRelicCommand : ReplayCommand
{
    private const string Cmd = "TakeChestRelic";

    public TakeChestRelicCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "take chest relic";

    public override ExecuteResult Execute()
    {
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        PlayerActionBuffer.LogDispatcher($"[TakeChestRelic] Attempt; {TreasureSyncDebug.Describe(sync)}");

        var relics = sync.CurrentRelics;
        if (relics == null || relics.Count == 0)
        {
            PlayerActionBuffer.LogDispatcher($"[TakeChestRelic] Relics not ready; {TreasureSyncDebug.Describe(sync)}; retrying.");
            return ExecuteResult.Retry(200);
        }

        try
        {
            PlayerActionBuffer.LogDispatcher("[TakeChestRelic] PickRelicLocally(0)");
            sync.PickRelicLocally(0);
            return ExecuteResult.Ok();
        }
        catch (System.InvalidOperationException ex)
        {
            PlayerActionBuffer.LogDispatcher(
                $"[TakeChestRelic] Pick not ready ({ex.Message}); {TreasureSyncDebug.Describe(sync)}; retrying.");
            return ExecuteResult.Retry(200);
        }
    }

    public static TakeChestRelicCommand? TryParse(string raw)
    {
        if (raw == Cmd)
            return new TakeChestRelicCommand();

        return null;
    }
}

internal static class TreasureSyncDebug
{
    private static readonly FieldInfo? VotesField =
        typeof(TreasureRoomRelicSynchronizer).GetField("_votes", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? PredictedVoteField =
        typeof(TreasureRoomRelicSynchronizer).GetField("_predictedVote", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? SinglePlayerSkippedField =
        typeof(TreasureRoomRelicSynchronizer).GetField("_singlePlayerSkipped", BindingFlags.NonPublic | BindingFlags.Instance);

    internal static string Describe(TreasureRoomRelicSynchronizer sync)
    {
        int currentRelics = sync.CurrentRelics?.Count ?? -1;
        int votes = (VotesField?.GetValue(sync) as ICollection)?.Count ?? -1;
        bool singlePlayerSkipped = SinglePlayerSkippedField?.GetValue(sync) is bool b && b;
        string predictedVote = PredictedVoteField?.GetValue(sync)?.ToString() ?? "null";

        return $"syncState(relics={currentRelics}, votes={votes}, predicted={predictedVote}, skipped={singlePlayerSkipped})";
    }
}
