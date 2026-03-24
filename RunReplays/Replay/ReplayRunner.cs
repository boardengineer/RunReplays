namespace RunReplays;

/// <summary>
/// Drives replay execution by consuming commands from ReplayEngine.
/// Most commands are now handled by typed ReplayCommand classes via the
/// dispatcher.  The remaining Execute* methods here serve legacy patches
/// that still consume commands directly.
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

    // ── Potion discard (used by CardPlayReplayPatch.TryDiscardPotion) ─────────

    public static bool ExecuteNetDiscardPotion(out int slotIndex)
    {
        if (!ReplayEngine.ConsumeNetDiscardPotion(out slotIndex))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: discard potion slot={slotIndex}");
        LogNext();
        return true;
    }

    // ── Potion use (used by CardPlayReplayPatch.TryUsePotion) ────────────────

    public static bool ExecuteUsePotion(out uint potionIndex, out uint? targetId, out bool inCombat)
    {
        if (!ReplayEngine.ConsumeUsePotion(out potionIndex, out targetId, out inCombat))
            return false;

        string target = targetId.HasValue ? $" targeting id={targetId}" : "";
        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: use potion index={potionIndex}{target} combat={inCombat}");
        LogNext();
        return true;
    }

    // ── Card rewards (used by CardChoiceScreenPatch) ─────────────────────────

    public static bool ExecuteCardReward(out string cardTitle)
    {
        if (!ReplayEngine.ConsumeCardReward(out cardTitle, out int rewardIndex))
            return false;

        string indexStr = rewardIndex >= 0 ? $" (pack {rewardIndex})" : "";
        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: take card reward '{cardTitle}'{indexStr}");
        LogNext();
        return true;
    }

    // ── Treasure chest relic (used by TreasureRoomReplayPatch) ───────────────

    public static bool ExecuteTakeChestRelic(out string relicTitle)
    {
        if (!ReplayEngine.ConsumeTakeChestRelic(out relicTitle))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: open chest (relic '{relicTitle}')");
        LogNext();
        return true;
    }

    // ── Rest site option (used by RestSiteReplayPatch) ───────────────────────

    public static bool ExecuteRestSiteOption(out string optionId)
    {
        if (!ReplayEngine.ConsumeRestSiteOption(out optionId))
            return false;

        PlayerActionBuffer.LogToDevConsole($"[ReplayRunner] Execute: choose rest site option '{optionId}'");
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
        // Try typed command description first.
        var parsed = Commands.ReplayCommandParser.TryParse(cmd);
        if (parsed != null)
            return parsed.Describe();

        if (cmd.StartsWith("SelectCardFromScreen ") && ReplayEngine.PeekSelectCardFromScreen(out int screenIdx))
            return screenIdx >= 0 ? $"select card from screen index={screenIdx}" : "skip card selection screen";

        if (cmd.StartsWith("SelectHandCards ") && ReplayEngine.PeekSelectHandCards(out uint[] hIds))
            return $"select hand cards [{(hIds.Length > 0 ? string.Join(",", hIds) : "(none)")}]";

        if (cmd.StartsWith("SelectDeckCard "))
            return "select deck card";

        if (cmd.StartsWith("SelectSimpleCard "))
            return "select simple card";

        if (cmd.StartsWith("RemoveCardFromDeck: "))
            return "remove card from deck";

        if (cmd.StartsWith("UpgradeCard ") &&
            int.TryParse(cmd.AsSpan("UpgradeCard ".Length), out int upgradeIdx))
            return $"upgrade card at deck index {upgradeIdx}";

        return $"(unknown) {cmd}";
    }
}
