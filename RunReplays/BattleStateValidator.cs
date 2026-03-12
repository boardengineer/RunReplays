namespace RunReplays;

/// <summary>
/// Subscribes to ReplayEngine.CommandConsumedWithState and compares the
/// expected battle state (from the verbose log) against the actual current
/// battle state, logging mismatches to the dev console.
/// Initialized once; re-subscribes are safe because the handler is static.
/// </summary>
internal static class BattleStateValidator
{
    private static bool _subscribed;

    internal static void EnsureSubscribed()
    {
        if (_subscribed)
            return;

        _subscribed = true;
        ReplayEngine.CommandConsumedWithState += OnCommandConsumed;
    }

    private static void OnCommandConsumed(string command, string? expectedState)
    {
        string? actualState = PlayerActionBuffer.GetBattleStateSummary();

        // Neither expected nor actual — nothing to compare (out of combat).
        if (expectedState == null && actualState == null)
            return;

        // Both present — compare.
        if (expectedState != null && actualState != null)
        {
            if (expectedState == actualState)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[BattleStateValidator] MATCH: {actualState}");
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[BattleStateValidator] MISMATCH after: {command}");
                PlayerActionBuffer.LogToDevConsole(
                    $"[BattleStateValidator]   Expected: {expectedState}");
                PlayerActionBuffer.LogToDevConsole(
                    $"[BattleStateValidator]   Actual:   {actualState}");
            }
            return;
        }

        // One is null and the other isn't — log for diagnostics.
        if (expectedState != null)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[BattleStateValidator] Expected state but not in combat: {expectedState}");
        }
        else
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[BattleStateValidator] In combat but no expected state recorded. Actual: {actualState}");
        }
    }
}
