namespace RunReplays.Commands;

/// <summary>
/// Navigate to a map node at the given column and row.
/// Recorded as: "MoveToMapCoordAction {playerId} MapCoord ({col}, {row})"
/// </summary>
public class MapMoveCommand : ReplayCommand
{
    private const string Prefix = "MoveToMapCoordAction ";
    private const string CoordMarker = "MapCoord (";

    public int Col { get; }
    public int Row { get; }

    public override ReplayDispatcher.ReadyState RequiredState => ReplayDispatcher.ReadyState.Map;

    private MapMoveCommand(string raw, int col, int row) : base(raw)
    {
        Col = col;
        Row = row;
    }

    public override string Describe() => $"navigate to map node col={Col} row={Row}";

    public override bool Execute()
    {
        MapChoiceReplayPatch.DispatchFromEngine();
        return true;
    }

    public static MapMoveCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        int markerIdx = raw.IndexOf(CoordMarker, System.StringComparison.Ordinal);
        if (markerIdx < 0)
            return null;

        ReadOnlySpan<char> coords = raw.AsSpan(markerIdx + CoordMarker.Length);
        int comma = coords.IndexOf(',');
        int close = coords.IndexOf(')');
        if (comma > 0 && close > comma
            && int.TryParse(coords[..comma].Trim(), out int col)
            && int.TryParse(coords[(comma + 1)..close].Trim(), out int row))
        {
            return new MapMoveCommand(raw, col, row);
        }

        return null;
    }
}
