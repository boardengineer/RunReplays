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
    internal static readonly Queue<string> _pending = new();

    // ── Overlay context ───────────────────────────────────────────────────────

    /// <summary>Fired on the calling thread each time a command is consumed.</summary>
    internal static event Action? ContextChanged;

    internal static readonly List<string> _recentConsumed = new(2);

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
    internal static bool _replayActive;
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

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out string? cmd) => _pending.TryPeek(out cmd);

    // Temp function as a passtrhough to consume
    public static bool ConsumeAny()
    { 
        SignalConsumed(_pending.Dequeue());
        return true;
    }
}
