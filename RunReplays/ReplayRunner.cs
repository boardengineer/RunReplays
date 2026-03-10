namespace RunReplays;

/// <summary>
/// Drives replay execution by consuming commands from ReplayEngine one at a time.
/// Every Execute* call logs a human-readable description of the command being run
/// and then prints what comes next.  Actual game-API calls will be added here as
/// each decision point is implemented; for now the runner is purely diagnostic.
/// </summary>
public static class ReplayRunner
{
    /// <summary>
    /// Loads commands into ReplayEngine and prints the first pending action.
    /// </summary>
    public static void Load(IReadOnlyList<string> commands)
    {
        ReplayEngine.Load(commands);
        LogNext("Loaded replay");
    }

    // ── Starting bonus ────────────────────────────────────────────────────────

    public static bool ExecuteStartingBonus(out int choiceIndex)
    {
        if (!ReplayEngine.ConsumeStartingBonus(out choiceIndex))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: choose starting bonus option {choiceIndex}");
        LogNext();
        return true;
    }

    // ── Map node choices ──────────────────────────────────────────────────────

    public static bool ExecuteMapNode(out int col, out int row)
    {
        if (!ReplayEngine.ConsumeMapNode(out col, out row))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: navigate to map node col={col} row={row}");
        LogNext();
        return true;
    }

    // ── Card rewards ──────────────────────────────────────────────────────────

    public static bool ExecuteCardReward(out string cardTitle)
    {
        if (!ReplayEngine.ConsumeCardReward(out cardTitle))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: take card reward '{cardTitle}'");
        LogNext();
        return true;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private static void LogNext(string? context = null)
    {
        string prefix = context != null ? $"[ReplayRunner] {context} — next" : "[ReplayRunner] Next";

        if (ReplayEngine.PeekNext(out string? cmd) && cmd != null)
            PlayerActionBuffer.LogToDevConsole($"{prefix}: {Describe(cmd)}");
        else
            PlayerActionBuffer.LogToDevConsole($"{prefix}: (no more commands)");
    }

    private static string Describe(string cmd)
    {
        if (cmd.StartsWith("ChooseStartingBonus ") &&
            int.TryParse(cmd.AsSpan("ChooseStartingBonus ".Length), out int bonusIdx))
            return $"choose starting bonus option {bonusIdx}";

        if (cmd.StartsWith("MoveToMapCoordAction "))
        {
            int markerIdx = cmd.IndexOf("MapCoord (", StringComparison.Ordinal);
            if (markerIdx >= 0)
            {
                var coords = cmd.AsSpan(markerIdx + "MapCoord (".Length);
                int comma = coords.IndexOf(',');
                int close = coords.IndexOf(')');
                if (comma > 0 && close > comma &&
                    int.TryParse(coords[..comma].Trim(), out int col) &&
                    int.TryParse(coords[(comma + 1)..close].Trim(), out int row))
                    return $"navigate to map node col={col} row={row}";
            }
        }

        if (cmd.StartsWith("TakeCardReward: "))
            return $"take card reward '{cmd["TakeCardReward: ".Length..]}'";

        if (cmd.StartsWith("TakeRelicReward: "))
            return $"take relic reward '{cmd["TakeRelicReward: ".Length..]}'";

        if (cmd.StartsWith("TakePotionReward: "))
            return $"take potion reward '{cmd["TakePotionReward: ".Length..]}'";

        if (cmd.StartsWith("TakeGoldReward: "))
            return $"take gold reward {cmd["TakeGoldReward: ".Length..]}";

        if (cmd.StartsWith("ChooseEventOption ") &&
            int.TryParse(cmd.AsSpan("ChooseEventOption ".Length), out int optIdx))
            return $"choose event option {optIdx}";

        return $"(unknown) {cmd}";
    }
}
