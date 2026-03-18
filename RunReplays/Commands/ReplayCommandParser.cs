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
        return (ReplayCommand?)MapMoveCommand.TryParse(raw)
            ?? ChooseRestSiteOptionCommand.TryParse(raw);
    }
}
