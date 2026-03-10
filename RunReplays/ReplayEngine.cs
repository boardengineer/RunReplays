using System;
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
    //
    // Recorded by PlayerActionBuffer via MoveToMapCoordAction.ToString():
    //   "MoveToMapCoordAction {playerId} MapCoord ({col}, {row})"

    private const string MapNodePrefix   = "MoveToMapCoordAction ";
    private const string MapCoordMarker  = "MapCoord (";

    public static bool PeekMapNode(out int col, out int row)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(MapNodePrefix))
        {
            int markerIdx = cmd.IndexOf(MapCoordMarker, StringComparison.Ordinal);
            if (markerIdx >= 0)
            {
                ReadOnlySpan<char> coords = cmd.AsSpan(markerIdx + MapCoordMarker.Length);
                // coords = "{col}, {row})"
                int comma = coords.IndexOf(',');
                int close = coords.IndexOf(')');
                if (comma > 0 && close > comma
                    && int.TryParse(coords[..comma].Trim(), out col)
                    && int.TryParse(coords[(comma + 1)..close].Trim(), out row))
                    return true;
            }
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

    // ── Card plays ────────────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via PlayCardAction.ToString():
    //   "PlayCardAction card: {CardModel} index: {CombatCardIndex} targetid: {TargetId}"
    // TargetId prints as empty string when null.

    private const string CardPlayPrefix       = "PlayCardAction ";
    private const string CardPlayIndexMarker  = " index: ";
    private const string CardPlayTargetMarker = " targetid: ";

    public static bool PeekCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        combatCardIndex = 0;
        targetId = null;

        if (!_pending.TryPeek(out string? cmd) || !cmd.StartsWith(CardPlayPrefix))
            return false;

        // Parse from the right so card display names containing spaces are safe.
        int targetIdx = cmd.LastIndexOf(CardPlayTargetMarker, StringComparison.Ordinal);
        int indexIdx  = cmd.LastIndexOf(CardPlayIndexMarker,  StringComparison.Ordinal);

        if (targetIdx < 0 || indexIdx < 0 || indexIdx >= targetIdx)
            return false;

        ReadOnlySpan<char> indexStr = cmd.AsSpan(
            indexIdx + CardPlayIndexMarker.Length,
            targetIdx - indexIdx - CardPlayIndexMarker.Length).Trim();

        if (!uint.TryParse(indexStr, out combatCardIndex))
            return false;

        ReadOnlySpan<char> targetStr = cmd.AsSpan(targetIdx + CardPlayTargetMarker.Length).Trim();
        if (targetStr.Length > 0 && uint.TryParse(targetStr, out uint tid))
            targetId = tid;

        return true;
    }

    public static bool ConsumeCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        if (PeekCardPlay(out combatCardIndex, out targetId))
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
