using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;

using RunReplays.Patches;
using RunReplays.Patches.Record;
using RunReplays.Patches.Replay;
namespace RunReplays;

/// <summary>
/// Raw command queue for an active replay.
/// Exposes typed Peek helpers for each command kind; consumption and logging
/// are handled by ReplayDispatcher, which calls these methods and proxies results.
/// </summary>
public static class ReplayEngine
{
    private static readonly Queue<string> _pending = new();

    // ── Overlay context ───────────────────────────────────────────────────────

    /// <summary>Fired on the calling thread each time a command is consumed.</summary>
    internal static event Action? ContextChanged;

    private static readonly List<string> _recentConsumed = new(2);

    /// <summary>
    /// Dequeues one command, records it in the recent-history buffer, and
    /// fires ContextChanged so the overlay can refresh.
    /// When the queue empties naturally (all commands consumed), also fires
    /// ReplayCompleted so callers can restore the action buffer.
    /// </summary>
    private static string SignalConsumed(string cmd, [CallerMemberName] string? caller = null)
    {
        // PlayerActionBuffer.LogMigrationWarning($"[Queue] {caller}: {cmd[..Math.Min(cmd.Length, 60)]} ({_pending.Count} remaining)");

        if (_recentConsumed.Count >= 2)
            _recentConsumed.RemoveAt(0);
        _recentConsumed.Add(cmd);
        ContextChanged?.Invoke();
        if (_replayActive && _pending.Count == 0)
        {
            // Defer the replay→record transition to the next Godot frame.
            // This keeps IsActive = true long enough for the last replayed action's
            // AfterActionExecuted to fire (and be suppressed), preventing it from
            // being double-recorded into the new log alongside the restored buffer.
            var commands = _loadedCommands;
            Callable.From(() =>
            {
                // If Clear() was called before this fires, _replayActive is already
                // false — bail out so ReplayCompleted is not fired spuriously.
                if (!_replayActive) return;
                _replayActive = false;
                ReplayDispatcher.RestoreGameSpeed();
                ReplayCompleted?.Invoke(commands);
            }).CallDeferred();
        }

        // Mark the previous dispatch as complete and re-trigger for the next command.
        ReplayDispatcher.NotifyConsumed();
        ReplayDispatcher.TryDispatch();

        return cmd;
    }

    // ── Replay-completed notification ─────────────────────────────────────────

    /// <summary>
    /// Fired when the last queued command is consumed (i.e. the replay finishes
    /// naturally). Carries the full list of commands that were loaded so
    /// subscribers can restore the action buffer for the record phase.
    /// Not fired when Clear() is called explicitly.
    /// </summary>
    internal static event Action<IReadOnlyList<string>>? ReplayCompleted;

    // True only while commands loaded by Load() are still pending.
    // Set false by Clear() so ReplayCompleted is not fired on explicit cancels.
    private static bool _replayActive;
    private static List<string> _loadedCommands = new();

    /// <summary>
    /// Returns up to 2 recently consumed commands (prev), the current front of
    /// the queue (current), and up to 2 commands ahead of it (next).
    /// </summary>
    internal static void GetReplayContext(
        out IReadOnlyList<string> prev,
        out string? current,
        out IReadOnlyList<string> next)
    {
        prev = _recentConsumed;

        string[] arr = _pending.ToArray();
        current = arr.Length > 0 ? arr[0] : null;

        var nextList = new List<string>(2);
        for (int i = 1; i < Math.Min(arr.Length, 3); i++)
            nextList.Add(arr[i]);
        next = nextList;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True while commands are queued OR while the last command was just consumed
    /// but its AfterActionExecuted callback may not have fired yet.
    /// The _replayActive flag is cleared one Godot frame after the queue empties
    /// so that the last replayed action is still suppressed from recording.
    /// </summary>
    public static bool IsActive => _pending.Count > 0 || _replayActive;

    /// <summary>
    /// The seed of the run being replayed or loaded from a save.
    /// Set by RunReplayMenu before starting the run.  Used by
    /// GetRandomListUnlockPatch to override the seed deterministically.
    /// Null when not using the replay menu (falls back to ForcedSeedPatch).
    /// </summary>
    public static string? ActiveSeed { get; set; }

    /// <summary>State suffix separator embedded in minimal log entries.</summary>
    private const string StateSeparator = " || ";

    public static void Load(IReadOnlyList<string> commands)
    {
        Utils.RngCheckpointLogger.Clear();
        Utils.RngCheckpointLogger.Log("=== Replay Load ===");
        _pending.Clear();
        _recentConsumed.Clear();

        _loadedCommands = new List<string>();
        foreach (string raw in commands)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Skip header comment lines (e.g. "# Character: ...", "# Seed: ...").
            if (raw.StartsWith('#'))
                continue;

            // Strip the " || state" suffix so the command text parses cleanly.
            int sepIdx = raw.IndexOf(StateSeparator, StringComparison.Ordinal);
            string cmd = sepIdx >= 0 ? raw[..sepIdx] : raw;
            _loadedCommands.Add(cmd);
            _pending.Enqueue(cmd);
        }

        _replayActive = _loadedCommands.Count > 0;
        if (_replayActive)
        {
            ReplayDispatcher.ApplyGameSpeed();
            ReplayDispatcher.StartWatchdog();
        }
    }

    public static void Clear()
    {
        _pending.Clear();
        _recentConsumed.Clear();
        _replayActive = false;
        ReplayDispatcher.RestoreGameSpeed();
        ResetAllPatchState();
    }

    /// <summary>
    /// Resets all static state across recording and replay patches so that
    /// both paths can start cleanly after a stall or cancellation.
    /// </summary>
    public static void ResetAllPatchState()
    {
        // Recording state
        BattleRewardPatch.LastCardRewardIndex = -1;
        BattleRewardPatch.IsProcessingCardReward = false;
        DeckRemovalState.PendingRemoval = false;
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingLabel = null;
        EventSelectionPatch.PendingIndex = null;
        DeckCardSelectContext.Pending = false;
        SimpleGridContext.Pending = false;
        HandCardSelectRecordPatch.SuppressNext = false;

        // Crystal sphere
        CrystalSphereReplayPatch.PendingTool = null;

        // Buffered recording contexts
        SimpleGridSyncPatch.FlushIfPending();

        // Dispatcher
        ReplayDispatcher.Reset();
    }

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out string? cmd) => _pending.TryPeek(out cmd);

    /// <summary>
    /// Returns the command at the given offset from the front (0 = front, 1 = second, etc.)
    /// without consuming anything.  Returns null if the offset is out of range.
    /// </summary>
    public static string? PeekAhead(int offset)
    {
        int i = 0;
        foreach (string cmd in _pending)
        {
            if (i == offset)
                return cmd;
            i++;
        }
        return null;
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

    // Temp function as a passtrhough to consume
    public static bool ConsumeAny()
    { 
        SignalConsumed(_pending.Dequeue());
        return true;
    }
}
