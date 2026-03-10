using System.Collections.Generic;

namespace RunReplays;

/// <summary>
/// Raw command queue for an active replay.
/// Exposes typed Peek helpers for each command kind; consumption and logging
/// are handled by ReplayRunner, which calls these methods and proxies results.
/// </summary>
public static class ReplayEngine
{
    private static readonly Queue<string> _pending = new();

    public static bool IsActive => _pending.Count > 0;

    public static void Load(IReadOnlyList<string> commands)
    {
        _pending.Clear();
        foreach (string cmd in commands)
            if (!string.IsNullOrWhiteSpace(cmd))
                _pending.Enqueue(cmd);
    }

    public static void Clear() => _pending.Clear();

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out string? cmd) => _pending.TryPeek(out cmd);

    // ── Starting bonus ────────────────────────────────────────────────────────

    private const string StartingBonusPrefix = "ChooseStartingBonus ";

    public static bool PeekStartingBonus(out int choiceIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(StartingBonusPrefix)
            && int.TryParse(cmd.AsSpan(StartingBonusPrefix.Length), out choiceIndex))
            return true;

        choiceIndex = -1;
        return false;
    }

    public static bool ConsumeStartingBonus(out int choiceIndex)
    {
        if (PeekStartingBonus(out choiceIndex))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Map node choices ──────────────────────────────────────────────────────

    private const string MapNodePrefix = "ChooseMapNode ";

    public static bool PeekMapNode(out int col, out int row)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(MapNodePrefix))
        {
            ReadOnlySpan<char> rest = cmd.AsSpan(MapNodePrefix.Length);
            int space = rest.IndexOf(' ');
            if (space > 0
                && int.TryParse(rest[..space], out col)
                && int.TryParse(rest[(space + 1)..], out row))
                return true;
        }
        col = -1;
        row = -1;
        return false;
    }

    public static bool ConsumeMapNode(out int col, out int row)
    {
        if (PeekMapNode(out col, out row))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

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
