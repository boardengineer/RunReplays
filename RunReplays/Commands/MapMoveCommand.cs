using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using RunReplays.Patch;

namespace RunReplays.Commands;

/// <summary>
///     Navigate to a map node at the given column and row.
///     Recorded as: "MoveToMapCoordAction {playerId} MapCoord ({col}, {row})"
/// </summary>
public class MapMoveCommand : ReplayCommand
{
    private const string Prefix = "MoveToMapCoordAction ";
    private const string CoordMarker = "MapCoord (";

    internal static NMapScreen? _activeScreen;


    private MapMoveCommand(string raw, int col, int row) : base(raw)
    {
        Col = col;
        Row = row;
    }

    public int Col { get; }
    public int Row { get; }

    public override string Describe()
    {
        return $"navigate to map node col={Col} row={Row}";
    }

    public override ExecuteResult Execute()
    {
        Callable.From(() => MapChoiceReplayPatch.AutoSelectMapNode(_activeScreen!, Col, Row)).CallDeferred();
        ReplayDispatcher.MapMoveInFlight = true;
        return ExecuteResult.Ok();
    }

    public static MapMoveCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        var markerIdx = raw.IndexOf(CoordMarker, StringComparison.Ordinal);
        if (markerIdx < 0)
            return null;

        var coords = raw.AsSpan(markerIdx + CoordMarker.Length);
        var comma = coords.IndexOf(',');
        var close = coords.IndexOf(')');
        if (comma > 0 && close > comma
                      && int.TryParse(coords[..comma].Trim(), out var col)
                      && int.TryParse(coords[(comma + 1)..close].Trim(), out var row))
            return new MapMoveCommand(raw, col, row);

        return null;
    }
}