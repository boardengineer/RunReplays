using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Navigate to a map node at the given column.
/// Recorded as: "MoveToMapCoord {col}"
///
/// The row is derived at execution time from the player's current map position
/// (CurrentMapCoord.row + 1), since map travel always advances one row.
/// </summary>
public class MapMoveCommand : ReplayCommand
{
    private const string Prefix = "MoveToMapCoord ";

    private static readonly FieldInfo? MapPointDictionaryField =
        typeof(NMapScreen).GetField(
            "_mapPointDictionary",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty(
            "State",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    internal static NMapScreen? _activeScreen;

    public int Col { get; }

    public MapMoveCommand(int col) : base("")
    {
        Col = col;
    }

    public override string ToString() => $"{Prefix}{Col}";

    public override string Describe() => $"navigate to map node col={Col}";

    public override ExecuteResult Execute()
    {
        Callable.From(() => AutoSelectMapNode(_activeScreen!, Col)).CallDeferred();
        ReplayDispatcher.MapMoveInFlight = true;
        return ExecuteResult.Ok();
    }

    public static MapMoveCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        // Handle both "MoveToMapCoord {col}" and "MoveToMapCoord {col} {row}" (row ignored)
        var parts = raw.Substring(Prefix.Length).Trim().Split(' ');
        if (parts.Length >= 1 && int.TryParse(parts[0], out int col))
            return new MapMoveCommand(col);
        return null;
    }

    internal static void AutoSelectMapNode(NMapScreen screen, int col)
    {
        if (MapPointDictionaryField?.GetValue(screen) is not Dictionary<MapCoord, NMapPoint> dict)
        {
            PlayerActionBuffer.LogMigrationWarning("[RunReplays] MapChoice: could not access map point dictionary.");
            return;
        }

        // Derive row from the player's current position + 1.
        int row;
        var runState = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        var currentCoord = runState?.CurrentMapCoord;
        if (currentCoord.HasValue)
        {
            row = currentCoord.Value.row + 1;
        }
        else
        {
            // First move of the act — row 0.
            row = 0;
        }

        var coord = new MapCoord(col, row);
        if (!dict.TryGetValue(coord, out NMapPoint? point))
        {
            PlayerActionBuffer.LogMigrationWarning(
                $"[RunReplays] MapChoice: no node at col={col} row={row} (derived from current pos). Trying row 0.");
            // Fallback for act start where CurrentMapCoord may be stale.
            coord = new MapCoord(col, 0);
            if (!dict.TryGetValue(coord, out point))
                return;
        }

        CardPlayReplayPatch.InvalidateStaleTimers();
        screen.OnMapPointSelectedLocally(point);
    }
}
