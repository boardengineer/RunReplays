namespace RunReplays.Commands;

/// <summary>
/// Parses raw command strings into <see cref="ReplayCommand"/> objects.
/// Returns null for commands that haven't been migrated yet — the dispatcher
/// falls back to the legacy string-based switch for those.
/// </summary>
public static class ReplayCommandParser
{
    /// <summary>
    /// Attempts to parse a raw command string into a typed command object.
    /// Returns null if the command type hasn't been migrated yet.
    /// </summary>
    public static ReplayCommand? TryParse(string raw)
    {
        // Commands are tried in rough frequency order for fast short-circuit.
        return MapMoveCommand.TryParse(raw);
        // Future commands will be chained here:
        // ?? PlayCardCommand.TryParse(raw)
        // ?? EndTurnCommand.TryParse(raw)
        // ?? ...
    }
}
