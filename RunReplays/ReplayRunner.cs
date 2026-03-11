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

    // ── End player turn ───────────────────────────────────────────────────────

    public static bool ExecuteEndTurn()
    {
        if (!ReplayEngine.ConsumeEndTurn())
            return false;

        PlayerActionBuffer.LogToDevConsole("[ReplayRunner] Execute: end player turn");
        LogNext();
        return true;
    }

    // ── Card plays ────────────────────────────────────────────────────────────

    public static bool ExecuteCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        if (!ReplayEngine.ConsumeCardPlay(out combatCardIndex, out targetId))
            return false;

        string target = targetId.HasValue ? $" targeting id={targetId}" : "";
        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: play card index={combatCardIndex}{target}");
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

    // ── Potion rewards ────────────────────────────────────────────────────────

    public static bool ExecutePotionReward(out string potionTitle)
    {
        if (!ReplayEngine.ConsumePotionReward(out potionTitle))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: take potion reward '{potionTitle}'");
        LogNext();
        return true;
    }

    // ── Event option choices ──────────────────────────────────────────────────

    public static bool ExecuteEventOption(out string textKey)
    {
        if (!ReplayEngine.ConsumeEventOption(out textKey))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: choose event option '{textKey}'");
        LogNext();
        return true;
    }

    // ── Card upgrades ─────────────────────────────────────────────────────────

    public static bool ExecuteUpgradeCard(out int deckIndex)
    {
        if (!ReplayEngine.ConsumeUpgradeCard(out deckIndex))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: upgrade card at deck index {deckIndex}");
        LogNext();
        return true;
    }

    // ── Shop commands ─────────────────────────────────────────────────────────

    public static bool ExecuteOpenShop()
    {
        if (!ReplayEngine.ConsumeOpenShop())
            return false;

        PlayerActionBuffer.LogToDevConsole("[ReplayRunner] Execute: open shop");
        LogNext();
        return true;
    }

    public static bool ExecuteBuyCard(out string cardTitle)
    {
        if (!ReplayEngine.ConsumeBuyCard(out cardTitle))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: buy card '{cardTitle}'");
        LogNext();
        return true;
    }

    public static bool ExecuteBuyRelic(out string relicTitle)
    {
        if (!ReplayEngine.ConsumeBuyRelic(out relicTitle))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: buy relic '{relicTitle}'");
        LogNext();
        return true;
    }

    public static bool ExecuteBuyPotion(out string potionTitle)
    {
        if (!ReplayEngine.ConsumeBuyPotion(out potionTitle))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: buy potion '{potionTitle}'");
        LogNext();
        return true;
    }

    public static bool ExecuteBuyCardRemoval()
    {
        if (!ReplayEngine.ConsumeBuyCardRemoval())
            return false;

        PlayerActionBuffer.LogToDevConsole("[ReplayRunner] Execute: buy card removal");
        LogNext();
        return true;
    }

    // ── Gold rewards ──────────────────────────────────────────────────────────

    public static bool ExecuteGoldReward(out int goldAmount)
    {
        if (!ReplayEngine.ConsumeGoldReward(out goldAmount))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: take gold reward {goldAmount}");
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

        if (cmd.StartsWith("EndPlayerTurnAction "))
            return "end player turn";

        if (cmd.StartsWith("PlayCardAction "))
        {
            int targetIdx = cmd.LastIndexOf(" targetid: ", StringComparison.Ordinal);
            int indexIdx  = cmd.LastIndexOf(" index: ",    StringComparison.Ordinal);
            if (indexIdx >= 0 && targetIdx > indexIdx)
            {
                var indexSpan  = cmd.AsSpan(indexIdx + " index: ".Length, targetIdx - indexIdx - " index: ".Length).Trim();
                var targetSpan = cmd.AsSpan(targetIdx + " targetid: ".Length).Trim();
                if (uint.TryParse(indexSpan, out uint idx))
                {
                    string targetStr = targetSpan.Length > 0 ? $" targeting id={targetSpan}" : "";
                    return $"play card index={idx}{targetStr}";
                }
            }
        }

        if (cmd.StartsWith("UpgradeCard ") &&
            int.TryParse(cmd.AsSpan("UpgradeCard ".Length), out int upgradeIdx))
            return $"upgrade card at deck index {upgradeIdx}";

        if (cmd.StartsWith("TakeCardReward: "))
            return $"take card reward '{cmd["TakeCardReward: ".Length..]}'";

        if (cmd.StartsWith("TakeRelicReward: "))
            return $"take relic reward '{cmd["TakeRelicReward: ".Length..]}'";

        if (cmd.StartsWith("TakePotionReward: "))
            return $"take potion reward '{cmd["TakePotionReward: ".Length..]}'";

        if (cmd.StartsWith("TakeGoldReward: "))
            return $"take gold reward {cmd["TakeGoldReward: ".Length..]}";

        if (cmd == "OpenShop")
            return "open shop";

        if (cmd.StartsWith("BuyCard "))
            return $"buy card '{cmd["BuyCard ".Length..]}'";

        if (cmd.StartsWith("BuyRelic "))
            return $"buy relic '{cmd["BuyRelic ".Length..]}'";

        if (cmd.StartsWith("BuyPotion "))
            return $"buy potion '{cmd["BuyPotion ".Length..]}'";

        if (cmd == "BuyCardRemoval")
            return "buy card removal";

        if (cmd.StartsWith("ChooseEventOption "))
            return $"choose event option '{cmd["ChooseEventOption ".Length..]}'";


        return $"(unknown) {cmd}";
    }
}
