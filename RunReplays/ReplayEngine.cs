using System.Collections.Generic;

namespace RunReplays;

/// <summary>
/// Manages an active replay by maintaining a queue of commands loaded from a
/// minimal log file and exposing typed peek/consume helpers for each command kind.
///
/// Commands are consumed one-by-one as the game reaches each decision point.
/// The engine is inactive (IsActive == false) when the queue is empty.
/// </summary>
public static class ReplayEngine
{
    private static readonly Queue<string> _pending = new();

    public static bool IsActive => _pending.Count > 0;

    /// <summary>Loads commands from a minimal log and activates the engine.</summary>
    public static void Load(IReadOnlyList<string> commands)
    {
        _pending.Clear();
        foreach (string cmd in commands)
            if (!string.IsNullOrWhiteSpace(cmd))
                _pending.Enqueue(cmd);
    }

    public static void Clear() => _pending.Clear();

    // ── Card rewards ──────────────────────────────────────────────────────────

    private const string CardRewardPrefix = "TakeCardReward: ";

    public static bool PeekCardReward(out string cardTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(CardRewardPrefix))
        {
            cardTitle = cmd.Substring(CardRewardPrefix.Length);
            return true;
        }
        cardTitle = string.Empty;
        return false;
    }

    public static bool ConsumeCardReward(out string cardTitle)
    {
        if (PeekCardReward(out cardTitle))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }
}
