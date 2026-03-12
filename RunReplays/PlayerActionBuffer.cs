using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace RunReplays;

/// <summary>
/// Accumulates recordable player actions for the duration of a run.
///
/// A new ActionExecutor is constructed at the start of every run, so its
/// constructor is the right place to both clear the previous run's buffer
/// and subscribe to AfterActionExecuted for the new one.
///
/// The buffer is never drained between saves — each save log is a full
/// snapshot of all actions from run-start to that save point.
/// </summary>
[HarmonyPatch(typeof(ActionExecutor), MethodType.Constructor, new[] { typeof(ActionQueueSet) })]
public static class PlayerActionBuffer
{
    /// <summary>Fired (on the calling thread) each time a line is added to the buffer.</summary>
    internal static event Action<string>? EntryRecorded;

    // Separate queues allow verbose and minimal to hold entirely different
    // content for the same event (e.g. a multi-line block vs a single summary).
    // Thread-safe: AfterActionExecuted fires from async action execution.
    private static readonly ConcurrentQueue<(string Timestamp, string Action)> _verboseEntries = new();
    private static readonly ConcurrentQueue<string> _minimalEntries = new();

    [HarmonyPostfix]
    public static void Postfix(ActionExecutor __instance)
    {
        // Clear both queues whenever a new executor is created (new run start).
        while (_verboseEntries.TryDequeue(out _)) { }
        while (_minimalEntries.TryDequeue(out _)) { }

        RunOverlay.InitForRun();

        // When the replay finishes, restore the replayed commands into the
        // buffer so that new recordings append after them rather than starting
        // from an empty log.
        ReplayEngine.ReplayCompleted -= OnReplayCompleted;
        ReplayEngine.ReplayCompleted += OnReplayCompleted;

        __instance.AfterActionExecuted += action =>
        {
            if (ReplayEngine.IsActive)
            {
                // During replay, fire pending validation now that the action has
                // resolved and the game state matches what was recorded.
                ReplayEngine.FlushPendingValidation();
                return;
            }

            if (!action.RecordableToReplay
                || action is VoteForMapCoordAction
                || action is ReadyToBeginEnemyTurnAction)
                return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string actionText = action.ToString()!;
            _minimalEntries.Enqueue(actionText);

            string? battleState = GetBattleStateSummary();
            if (battleState != null)
                _verboseEntries.Enqueue((timestamp, actionText + " | " + battleState));
            else
                _verboseEntries.Enqueue((timestamp, actionText));

            LogToDevConsole($"[{timestamp}] {actionText}");
            EntryRecorded?.Invoke(actionText);

            // Hand-card, card-choice-screen, and deck-card selections may buffer their
            // commands until the triggering action is logged.  Flush them now so they
            // follow the triggering action in the minimal log.
            if (action is UsePotionAction || action is PlayCardAction)
            {
                HandCardSelectRecordPatch.FlushIfPending();
                CardChoiceScreenSyncPatch.FlushIfPending();
                CardEffectDeckSelectContext.FlushIfPending();
                SimpleGridSyncPatch.FlushIfPending();
            }
        };
    }

    /// <summary>
    /// Records the same text into both verbose and minimal (for non-GameAction
    /// events where the two formats share content).
    /// </summary>
    public static void Record(string text)
    {
        if (ReplayEngine.IsActive)
            return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _verboseEntries.Enqueue((timestamp, text));
        _minimalEntries.Enqueue(text);
        LogToDevConsole($"[{timestamp}] {text}");
        EntryRecorded?.Invoke(text);
    }

    /// <summary>
    /// Records only into the verbose log (e.g. decorative separators or
    /// per-option lines that the minimal log replaces with a summary).
    /// </summary>
    public static void RecordVerboseOnly(string text)
    {
        if (ReplayEngine.IsActive)
            return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _verboseEntries.Enqueue((timestamp, text));
        LogToDevConsole($"[{timestamp}] {text}");
        EntryRecorded?.Invoke(text);
    }

    /// <summary>
    /// Records only into the minimal log (e.g. a compact summary line that
    /// replaces a multi-line verbose block).
    /// </summary>
    public static void RecordMinimalOnly(string text)
    {
        if (ReplayEngine.IsActive)
            return;

        _minimalEntries.Enqueue(text);
    }

    /// <summary>
    /// Called when ReplayEngine exhausts its command queue.  Restores the
    /// replayed commands into both buffer queues so that the next save log
    /// contains all actions (replayed + new) rather than only the new ones.
    /// The overlay's recent-entry display is also refreshed.
    /// </summary>
    private static void OnReplayCompleted(IReadOnlyList<string> commands)
    {
        var verboseEntries = commands.Select(c => ("REPLAY", c)).ToList();
        Restore(verboseEntries, commands);
        RunOverlay.RestoreRecentEntries(commands);
        LogToDevConsole(
            $"[PlayerActionBuffer] Replay completed — restored {commands.Count} command(s) to buffer.");
    }

    /// <summary>
    /// Verbose snapshot: each entry prefixed with its timestamp.
    /// </summary>
    public static IReadOnlyList<string> Snapshot()
    {
        return new List<string>(_verboseEntries.Select(e => $"[{e.Timestamp}] {e.Action}"));
    }

    /// <summary>
    /// Minimal snapshot: action text only, no timestamps.
    /// </summary>
    public static IReadOnlyList<string> SnapshotMinimal()
    {
        return new List<string>(_minimalEntries);
    }

    /// <summary>
    /// Restores both queues from previously-saved log files (used when a run is continued).
    /// Called after the buffer has already been cleared by the ActionExecutor constructor patch.
    /// </summary>
    public static void Restore(
        IReadOnlyList<(string Timestamp, string Action)> verboseEntries,
        IReadOnlyList<string> minimalEntries)
    {
        foreach (var entry in verboseEntries)
            _verboseEntries.Enqueue(entry);
        foreach (var entry in minimalEntries)
            _minimalEntries.Enqueue(entry);
    }

    /// <summary>
    /// Returns a compact summary of the current battle state (hand contents and
    /// enemy HP), or null when not in combat.
    /// </summary>
    internal static string? GetBattleStateSummary()
    {
        try
        {
            if (!CombatManager.Instance.IsInProgress)
                return null;

            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null)
                return null;

            var sb = new StringBuilder();

            // Hand contents
            Player? me = LocalContext.GetMe(state);
            var hand = me?.PlayerCombatState?.Hand?.Cards;
            if (hand != null && hand.Count > 0)
            {
                sb.Append("Hand: [");
                for (int i = 0; i < hand.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(hand[i].Title);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append("Hand: []");
            }

            // Enemy HP
            IReadOnlyList<Creature> enemies = state.Enemies;
            if (enemies.Count > 0)
            {
                sb.Append(" Enemies: [");
                for (int i = 0; i < enemies.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Creature e = enemies[i];
                    sb.Append(e.Name);
                    sb.Append(' ');
                    sb.Append(e.CurrentHp);
                    sb.Append('/');
                    sb.Append(e.MaxHp);
                }
                sb.Append(']');
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    // Reflected once; null until the field is found.
    private static readonly FieldInfo? _instanceField =
        typeof(NDevConsole).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);

    internal static void LogToDevConsole(string entry)
    {
        // Check the backing field directly to avoid the InvalidOperationException
        // that NDevConsole.Instance throws when the console hasn't been created yet.
        if (_instanceField?.GetValue(null) is not NDevConsole console)
            return;

        // AfterActionExecuted fires from async context — defer the UI write to
        // the Godot main thread to avoid cross-thread node access.
        var outputBuffer = console.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
        outputBuffer.CallDeferred(RichTextLabel.MethodName.AppendText, entry + "\n");
    }
}
