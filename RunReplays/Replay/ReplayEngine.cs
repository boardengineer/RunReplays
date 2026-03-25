using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;

namespace RunReplays;

/// <summary>
/// Raw command queue for an active replay.
/// Exposes typed Peek helpers for each command kind; consumption and logging
/// are handled by ReplayRunner, which calls these methods and proxies results.
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
        SelectorStackDebug.Clear();
        SelectorStackDebug.Log("=== Replay Load ===");
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

        // Replay selector scopes
        FromChooseACardScreenPatch._pendingScope?.Dispose();
        FromChooseACardScreenPatch._pendingScope = null;
        FromDeckGenericPatch._pendingScope?.Dispose();
        FromDeckGenericPatch._pendingScope = null;
        FromDeckForEnchantmentPatch._pendingScope?.Dispose();
        FromDeckForEnchantmentPatch._pendingScope = null;
        FromDeckForEnchantmentWithFilterPatch._pendingScope?.Dispose();
        FromDeckForEnchantmentWithFilterPatch._pendingScope = null;
        FromDeckForEnchantmentWithCardsPatch._pendingScope?.Dispose();
        FromDeckForEnchantmentWithCardsPatch._pendingScope = null;
        FromDeckForTransformationPatch._pendingScope?.Dispose();
        FromDeckForTransformationPatch._pendingScope = null;
        FromDeckForUpgradePatch._pendingScope?.Dispose();
        FromDeckForUpgradePatch._pendingScope = null;
        Commands.HandSelectionCapture.Clear();
        FromSimpleGridPatch._pendingScope?.Dispose();
        FromSimpleGridPatch._pendingScope = null;

        // Crystal sphere
        CrystalSphereReplayPatch.PendingTool = null;

        // Buffered recording contexts
        CardChoiceScreenSyncPatch.FlushIfPending();
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

    // ── Card plays ────────────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via PlayCardAction.ToString():
    //   "PlayCardAction card: {CardModel} index: {CombatCardIndex} targetid: {TargetId}"
    // TargetId prints as empty string when null.

    private const string CardPlayPrefix       = "PlayCardAction ";

    // ── Card rewards ──────────────────────────────────────────────────────────

    private const string CardRewardPrefix = "TakeCardReward: ";
    private const string CardRewardIndexedPrefix = "TakeCardReward[";

    public static bool PeekCardReward(out string cardTitle, out int rewardIndex)
    {
        if (_pending.TryPeek(out string? cmd))
        {
            // Indexed format: TakeCardReward[N]: CardTitle
            if (cmd.StartsWith(CardRewardIndexedPrefix))
            {
                int closeBracket = cmd.IndexOf("]: ", CardRewardIndexedPrefix.Length, StringComparison.Ordinal);
                if (closeBracket > CardRewardIndexedPrefix.Length &&
                    int.TryParse(cmd.AsSpan(CardRewardIndexedPrefix.Length, closeBracket - CardRewardIndexedPrefix.Length), out rewardIndex))
                {
                    cardTitle = cmd.Substring(closeBracket + "]: ".Length);
                    return true;
                }
            }

            // Legacy format: TakeCardReward: CardTitle
            if (cmd.StartsWith(CardRewardPrefix))
            {
                cardTitle = cmd.Substring(CardRewardPrefix.Length);
                rewardIndex = -1;
                return true;
            }
        }
        cardTitle = string.Empty;
        rewardIndex = -1;
        return false;
    }

    private const string OpenFakeShopCmd   = "OpenFakeShop";

    public static bool PeekOpenFakeShop()
        => _pending.TryPeek(out string? cmd) && cmd == OpenFakeShopCmd;

    // ── Rest site option choices ──────────────────────────────────────────────
    //
    // Recorded by RestSiteRecordPatch via RestSiteSynchronizer.ChooseLocalOption:
    //   "ChooseRestSiteOption {optionId}"  (e.g. "HEAL", "SMITH")

    private const string RestSiteOptionPrefix = "ChooseRestSiteOption ";

    public static bool PeekRestSiteOption(out string optionId)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(RestSiteOptionPrefix))
        {
            optionId = cmd.Substring(RestSiteOptionPrefix.Length);
            return true;
        }
        optionId = string.Empty;
        return false;
    }

    // ── Card choice screen selections ─────────────────────────────────────────
    //
    // Recorded by CardChoiceScreenPatch via PlayerChoiceSynchronizer.SyncLocalChoice
    // when FromChooseACardScreen is active (e.g. Skill Potion, Power Potion,
    // relic-triggered card choices like Lead Paperweight):
    //   "SelectCardFromScreen {index}"
    // index is the 0-based position in the offered card list; -1 means skipped.

    private const string SelectCardFromScreenPrefix = "SelectCardFromScreen ";

    public static bool PeekSelectCardFromScreen(out int index)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(SelectCardFromScreenPrefix)
            && int.TryParse(cmd.AsSpan(SelectCardFromScreenPrefix.Length), out index))
            return true;

        index = -1;
        return false;
    }

    /// <summary>
    /// Drains any interleaved commands that precede the next SelectCardFromScreen
    /// command, bringing it to the front of the queue.  Used for relic-triggered
    /// card choices (e.g. Lead Paperweight) where auto-processed actions may sit
    /// between the relic reward and the card selection.
    /// Returns true if SelectCardFromScreen is now at the front.
    /// </summary>
    public static bool SkipToSelectCardFromScreen()
    {
        if (PeekSelectCardFromScreen(out _))
            return true;

        bool found = false;
        foreach (string cmd in _pending)
        {
            if (cmd.StartsWith(SelectCardFromScreenPrefix))
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        while (_pending.Count > 0 && !PeekSelectCardFromScreen(out _))
            SignalConsumed(_pending.Dequeue());

        return PeekSelectCardFromScreen(out _);
    }

    public static bool ConsumeSelectCardFromScreen(out int index)
    {
        if (PeekSelectCardFromScreen(out index))
        {
            SignalConsumed(_pending.Dequeue());
            return true;
        }
        return false;
    }

    // ── Crystal Sphere minigame clicks ──────────────────────────────────────
    //
    // Recorded by CrystalSphereCellClickedPatch via CrystalSphereMinigame.CellClicked:
    //   "CrystalSphereClick {x} {y} {tool}"
    // x/y are cell grid coordinates; tool is the CrystalSphereToolType int value
    // (0 = None, 1 = Small, 2 = Big).

    private const string CrystalSphereClickPrefix = "CrystalSphereClick ";

    public static bool PeekCrystalSphereClick(out int x, out int y, out int tool)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(CrystalSphereClickPrefix))
        {
            ReadOnlySpan<char> rest = cmd.AsSpan(CrystalSphereClickPrefix.Length);
            // Format: "{x} {y} {tool}"
            int sp1 = rest.IndexOf(' ');
            if (sp1 > 0)
            {
                int sp2 = rest[(sp1 + 1)..].IndexOf(' ');
                if (sp2 > 0)
                {
                    sp2 += sp1 + 1; // absolute index
                    if (int.TryParse(rest[..sp1], out x)
                        && int.TryParse(rest[(sp1 + 1)..sp2], out y)
                        && int.TryParse(rest[(sp2 + 1)..], out tool))
                        return true;
                }
            }
        }

        x = y = tool = 0;
        return false;
    }

}
