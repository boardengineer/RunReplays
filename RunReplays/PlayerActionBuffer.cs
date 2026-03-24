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
using MegaCrit.Sts2.Core.Models;
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

    private const string StateSeparator = " || ";

    /// <summary>
    /// Holds the battle state captured after the previous recordable action
    /// (= the state before the next action).  Set at TurnStarted for the
    /// first action of a turn, then updated after each recordable action.
    /// </summary>
    private static string? _pendingPreState;


    [HarmonyPostfix]
    public static void Postfix(ActionExecutor __instance)
    {
        // Clear both queues whenever a new executor is created (new run start).
        while (_verboseEntries.TryDequeue(out _)) { }
        while (_minimalEntries.TryDequeue(out _)) { }
        _pendingPreState = null;

        RunOverlay.InitForRun();

        // Capture initial combat state at the start of each player turn so the
        // first action of the turn has a pre-state.
        CombatManager.Instance.TurnStarted -= OnTurnStarted;
        CombatManager.Instance.TurnStarted += OnTurnStarted;

        // When the replay finishes, restore the replayed commands into the
        // buffer so that new recordings append after them rather than starting
        // from an empty log.
        ReplayEngine.ReplayCompleted -= OnReplayCompleted;
        ReplayEngine.ReplayCompleted += OnReplayCompleted;

        __instance.BeforeActionExecuted += action =>
        {
            if (ReplayEngine.IsActive)
                return;

            // Reset the card-reward flag so it doesn't leak into non-reward
            // actions (e.g. potion use triggering FromChooseACardScreen).
            BattleRewardPatch.IsProcessingCardReward = false;

            if (action is UsePotionAction)
            {
                var text = action.ToString()!;
                // Potions that open a card selection screen (Power Potion,
                // Colorless Potion, Attack Potion, Gambler's Brew, etc.)
                // fire a second UsePotionAction with an empty potion name
                // after the selection resolves.  Skip that duplicate.
                if (!text.Contains("POTION."))
                    return;
                RecordCardPlayEarly(text);
            }
        };

        __instance.AfterActionExecuted += action =>
        {
            if (ReplayEngine.IsActive)
                return;

            if (!action.RecordableToReplay
                || action is ReadyToBeginEnemyTurnAction)
                return;

            // VoteForMapCoordAction is not recorded, but it is the first action
            // after the rewards screen closes.  Flush any pending card-choice
            // commands from relic-triggered selections (e.g. Lead Paperweight)
            // that were buffered but had no subsequent action to flush them.
            if (action is VoteForMapCoordAction)
            {
                CardChoiceScreenSyncPatch.FlushIfPending();
                return;
            }

            // PlayCardAction is recorded early from EnqueueManualPlay.
            // UsePotionAction is recorded early from BeforeActionExecuted.
            // Skip both here — just update pre-state and flush nested selections.
            if (action is PlayCardAction or UsePotionAction)
            {
                _pendingPreState = GetBattleStateSummary();
                CardChoiceScreenSyncPatch.FlushIfPending();
                CardEffectDeckSelectContext.FlushIfPending();
                SimpleGridSyncPatch.FlushIfPending();
                return;
            }

            CardChoiceScreenSyncPatch.FlushIfPending();

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string actionText = action.ToString()!;

            // Grab the pre-state (captured after the previous action or at
            // turn start) and update it for the next action.
            string? preState = _pendingPreState;
            _pendingPreState = GetBattleStateSummary();

            // Append pre-action state to minimal entry for replay validation.
            string minimalEntry = preState != null
                ? actionText + StateSeparator + preState
                : actionText;

            _verboseEntries.Enqueue((timestamp, actionText));
            _minimalEntries.Enqueue(minimalEntry);
            LogToDevConsole($"[{timestamp}] {actionText}");
            EntryRecorded?.Invoke(actionText);
        };
    }

    private static void OnTurnStarted(CombatState _)
    {
        if (!ReplayEngine.IsActive)
            _pendingPreState = GetBattleStateSummary();
    }

    /// <summary>
    /// Returns a compact summary of the current battle state:
    ///   Hand: [card1, card2] Enemies: [Monster 42/44, ...]
    /// Returns null when not in combat.
    /// </summary>
    internal static string? GetBattleStateSummary()
    {
        if (!CombatManager.Instance.IsInProgress)
            return null;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
            return null;

        Player? me;
        try { me = LocalContext.GetMe(state); }
        catch { me = state.Players.FirstOrDefault(); }
        if (me == null)
            return null;

        var sb = new StringBuilder();
        sb.Append("Hand: [");
        var hand = me.PlayerCombatState?.Hand?.Cards;
        if (hand != null)
        {
            for (int i = 0; i < hand.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(hand[i].Title);
            }
        }
        sb.Append("] Enemies: [");
        bool first = true;
        foreach (var enemy in state.Enemies)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(enemy.Name).Append(' ')
              .Append(enemy.CurrentHp).Append('/').Append(enemy.MaxHp);
        }
        sb.Append(']');
        return sb.ToString();
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
    /// Removes the most recently recorded entry from both queues.
    /// Used to undo a speculatively recorded action (e.g. a shop purchase
    /// that later fails).
    /// </summary>
    public static void UndoLast()
    {
        RemoveLast(_verboseEntries);
        RemoveLast(_minimalEntries);
    }

    private static void RemoveLast<T>(ConcurrentQueue<T> queue)
    {
        int count = queue.Count;
        if (count == 0) return;
        // Re-enqueue all but the last item.
        for (int i = 0; i < count - 1; i++)
        {
            if (queue.TryDequeue(out T? item))
                queue.Enqueue(item);
        }
        // Discard the last item.
        queue.TryDequeue(out _);
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

        // Strip " || state" suffix for overlay display.
        var displayEntries = commands.Select(c =>
        {
            int sep = c.IndexOf(StateSeparator, StringComparison.Ordinal);
            return sep >= 0 ? c[..sep] : c;
        }).ToList();
        RunOverlay.RestoreRecentEntries(displayEntries);

        LogToDevConsole(
            $"[PlayerActionBuffer] Replay completed — restored {commands.Count} command(s) to buffer.");
        CardPlayReplayPatch.LogCardSelectState("ReplayCompleted");
        SelectorStackDebug.Log("=== ReplayCompleted ===");
        Utils.RngCheckpointLogger.Log("=== ReplayCompleted ===");
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

    // Reflected once; null until the field is found.
    private static readonly FieldInfo? _instanceField =
        typeof(NDevConsole).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);

    [System.Diagnostics.Conditional("RUNREPLAYS_VERBOSE")]
    internal static void LogToDevConsole(string entry)
    {
        WriteToDevConsole(entry);
    }

    [System.Diagnostics.Conditional("RUNREPLAYS_DISPATCHER")]
    internal static void LogDispatcher(string entry)
    {
        WriteToDevConsole(entry);
    }

    [System.Diagnostics.Conditional("RUNREPLAYS_MIGRATION")]
    internal static void LogMigrationWarning(string entry)
    {
        WriteToDevConsole(entry);
    }

    private static void WriteToDevConsole(string entry)
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

    /// <summary>
    /// Records a card play action before execution.
    /// Consumes the pending pre-state (AfterActionExecuted will capture
    /// a fresh one after the card's effects resolve).
    /// </summary>
    internal static void RecordCardPlayEarly(string actionText)
    {
        if (ReplayEngine.IsActive)
            return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

        string? preState = _pendingPreState;
        _pendingPreState = null;

        string minimalEntry = preState != null
            ? actionText + StateSeparator + preState
            : actionText;

        _verboseEntries.Enqueue((timestamp, actionText));
        _minimalEntries.Enqueue(minimalEntry);
        LogToDevConsole($"[{timestamp}] {actionText}");
        EntryRecorded?.Invoke(actionText);
    }
}

/// <summary>
/// Records a card play as soon as EnqueueManualPlay completes — before the
/// PlayCardAction executes.  By this point the PlayCardAction constructor has
/// registered the card in NetCombatCardDb via NetCombatCard.FromModel.
/// </summary>
[HarmonyPatch(typeof(CardModel), "EnqueueManualPlay")]
public static class CardPlayRecordPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, Creature target)
    {
        if (ReplayEngine.IsActive)
            return;

        if (!NetCombatCardDb.Instance.TryGetCardId(__instance, out uint cardId))
            return;

        string targetStr = target?.CombatId?.ToString() ?? "";
        string actionText = $"PlayCardAction card: {__instance} index: {cardId} targetid: {targetStr}";

        PlayerActionBuffer.RecordCardPlayEarly(actionText);
    }
}

