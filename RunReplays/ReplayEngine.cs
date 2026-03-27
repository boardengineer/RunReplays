using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using RunReplays.Commands;

namespace RunReplays;

/// <summary>
/// Command queue for an active replay.
/// Commands are parsed at load time and stored as ReplayCommand objects.
/// </summary>
public static class ReplayEngine
{
    internal static readonly Queue<ReplayCommand> _pending = new();

    // ── Overlay context ───────────────────────────────────────────────────────

    /// <summary>Fired on the calling thread each time a command is consumed.</summary>
    internal static event Action? ContextChanged;

    internal static readonly List<ReplayCommand> _recentConsumed = new(2);

    /// <summary>
    /// Dequeues one command, records it in the recent-history buffer, and
    /// fires ContextChanged so the overlay can refresh.
    /// When the queue empties naturally (all commands consumed), also fires
    /// ReplayCompleted so callers can restore the action buffer.
    /// </summary>
    private static ReplayCommand SignalConsumed(ReplayCommand cmd, [CallerMemberName] string? caller = null)
    {
        if (_recentConsumed.Count >= 2)
            _recentConsumed.RemoveAt(0);
        _recentConsumed.Add(cmd);
        ContextChanged?.Invoke();
        if (_replayActive && _pending.Count == 0)
        {
            var commands = _loadedCommands;
            Callable.From(() =>
            {
                if (!_replayActive) return;
                _replayActive = false;
                ReplayDispatcher.RestoreGameSpeed();
                ReplayCompleted?.Invoke(commands);
            }).CallDeferred();
        }

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
    internal static event Action<IReadOnlyList<ReplayCommand>>? ReplayCompleted;

    internal static bool _replayActive;
    internal static List<ReplayCommand> _loadedCommands = new();

    /// <summary>
    /// Fires ReplayCompleted with the given commands. Used by StopAndRecord
    /// to restore consumed commands to the action buffer.
    /// </summary>
    internal static void FireReplayCompleted(IReadOnlyList<ReplayCommand> commands)
    {
        ReplayCompleted?.Invoke(commands);
    }

    /// <summary>
    /// Returns up to 2 recently consumed commands (prev), the current front of
    /// the queue (current), and up to 2 commands ahead of it (next).
    /// </summary>
    internal static void GetReplayContext(
        out IReadOnlyList<ReplayCommand> prev,
        out ReplayCommand? current,
        out IReadOnlyList<ReplayCommand> next)
    {
        prev = _recentConsumed;

        ReplayCommand[] arr = _pending.ToArray();
        current = arr.Length > 0 ? arr[0] : null;

        var nextList = new List<ReplayCommand>(2);
        for (int i = 1; i < Math.Min(arr.Length, 3); i++)
            nextList.Add(arr[i]);
        next = nextList;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public static bool IsActive => _pending.Count > 0 || _replayActive;

    public static string? ActiveSeed { get; set; }

    /// <summary>State suffix separator embedded in minimal log entries.</summary>
    private const string StateSeparator = " || ";

    public static void Load(IReadOnlyList<string> commands)
    {
        Utils.RngCheckpointLogger.Clear();
        Utils.RngCheckpointLogger.Log("=== Replay Load ===");
        _pending.Clear();
        _recentConsumed.Clear();

        _loadedCommands = new List<ReplayCommand>();
        foreach (string raw in commands)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (raw.StartsWith('#'))
                continue;

            int sepIdx = raw.IndexOf(StateSeparator, StringComparison.Ordinal);
            string cmdText = sepIdx >= 0 ? raw[..sepIdx] : raw;

            // Strip inline comment: "CommandText # comment"
            string? comment = null;
            int commentIdx = cmdText.IndexOf(" # ", StringComparison.Ordinal);
            if (commentIdx >= 0)
            {
                comment = cmdText[(commentIdx + 3)..];
                cmdText = cmdText[..commentIdx];
            }

            ReplayCommand? parsed = ReplayCommandParser.TryParse(cmdText);
            if (parsed == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[ReplayEngine] Skipping unparseable command: {cmdText}");
                continue;
            }

            parsed.Comment = comment;
            _loadedCommands.Add(parsed);
            _pending.Enqueue(parsed);
        }

        _replayActive = _loadedCommands.Count > 0;
        if (_replayActive)
        {
            ReplayDispatcher.ApplyGameSpeed();
            ReplayDispatcher.StartWatchdog();
        }
    }

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out ReplayCommand? cmd) => _pending.TryPeek(out cmd);

    public static bool ConsumeAny()
    {
        SignalConsumed(_pending.Dequeue());
        return true;
    }
}
