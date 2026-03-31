using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using RunReplays.Commands;

using RunReplays.Patches;
using RunReplays.Patches.Record;
using RunReplays.Patches.Replay;
namespace RunReplays;

/// <summary>
/// Central controller for replay command execution.  Sits between game events
/// and command execution (which invokes game APIs).
///
/// This gives the mod control over pacing and pausing replay commands
/// independently of game event timing.
/// </summary>
public static class ReplayDispatcher
{
    private static readonly HashSet<Type> ShopCommandTypes = new()
    {
        typeof(OpenShopCommand),
        typeof(BuyCardCommand), typeof(BuyRelicCommand),
        typeof(BuyCardRemovalCommand), typeof(BuyPotionCommand),
    };

    private static HashSet<Type> GetDispatchableTypes()
    {
        bool blocked = ReplayState.PotionInFlight
                    || ReplayState.CardPlayInFlight
                    || CardPlayReplayPatch.IsAwaitingEndTurnCompletion
                    || MapMoveInFlight
                    || ReplayState.ActionInFlight;

        if (blocked)
        {
            return new HashSet<Type>
            {
                typeof(SelectGridCardCommand),
                typeof(SelectCardFromScreenCommand),
                typeof(SelectHandCardsCommand),
            };
        }

        var types = new HashSet<Type>
        {
            typeof(PlayCardCommand), typeof(EndTurnCommand), typeof(MapMoveCommand),
            typeof(ChooseRestSiteOptionCommand), typeof(ChooseEventOptionCommand),
            typeof(ClaimRewardCommand), typeof(TakeCardCommand),
            typeof(SelectGridCardCommand), typeof(SelectHandCardsCommand),
            typeof(UsePotionCommand), typeof(DiscardPotionCommand),
            typeof(ProceedToNextActCommand), typeof(OpenChestCommand),
            typeof(TakeChestRelicCommand), typeof(CrystalSphereClickCommand),
            typeof(SelectCardFromScreenCommand),
        };

        if (ReplayState.ActiveMerchantRoom != null)
            types.UnionWith(ShopCommandTypes);

        if (ReplayState.FakeMerchantInstance != null)
        {
            types.Add(typeof(OpenFakeShopCommand));
            types.Add(typeof(BuyRelicCommand));
        }

        return types;
    }

    private static bool _dispatchPollRunning;
    private static HashSet<Type>? _lastDispatchableTypes;

    public static void StartDispatchPoll()
    {
        if (_dispatchPollRunning) return;
        _dispatchPollRunning = true;
        SubscribeToRoomEvents();
        ScheduleDispatchPollTick();
    }

    private static void ScheduleDispatchPollTick()
    {
        if (!_dispatchPollRunning) return;
        NGame.Instance?.GetTree()?.CreateTimer(0.5).Connect(
            "timeout", Callable.From(DispatchPollTick));
    }

    private static void DispatchPollTick()
    {
        PlayerActionBuffer.LogMigrationWarning(
            $"[Dispatcher] Flags: ActionInFlight={ReplayState.ActionInFlight}" +
            $" CardPlayInFlight={ReplayState.CardPlayInFlight}" +
            $" PotionInFlight={ReplayState.PotionInFlight}" +
            $" MapMoveInFlight={MapMoveInFlight}" +
            $" AwaitingEndTurn={CardPlayReplayPatch.IsAwaitingEndTurnCompletion}");

        var current = GetDispatchableTypes();
        if (_lastDispatchableTypes == null || !current.SetEquals(_lastDispatchableTypes))
        {
            _lastDispatchableTypes = new HashSet<Type>(current);
            var names = current.Select(t => t.Name).OrderBy(n => n);
            PlayerActionBuffer.LogMigrationWarning(
                $"[Dispatcher] Dispatchable commands changed: {string.Join(", ", names)}");
        }

        ScheduleDispatchPollTick();
    }

    public static void Load(IReadOnlyList<string> commands)
    {
        ReplayEngine.Load(commands);
    }

    private static bool _paused;
    private static bool _stepping;
    public static bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) TryDispatch();
        }
    }

    /// <summary>
    /// Executes a single command while paused, then re-pauses.
    /// </summary>
    public static void Step()
    {
        if (!_paused) Paused = true;
        _stepping = true;
        DispatchNow();
    }
    private static float _delayBetweenCommands = 1.0f;
    /// <summary>
    /// True while a dispatched command is executing (between ExecuteNext and
    /// the command being consumed from the queue).  Prevents re-dispatch of
    /// the same command.
    /// </summary>
    private static bool _dispatchInProgress;

    /// <summary>
    /// The command string that was last dispatched.  Used to detect when the
    /// queue front has changed (i.e. the previous command was consumed).
    /// </summary>
    private static ReplayCommand? _lastDispatchedCmd;

    /// <summary>
    /// Incremented each time a dispatch is scheduled.  Timer callbacks compare
    /// against this to detect if they've been superseded by DispatchNow.
    /// </summary>
    private static int _dispatchGeneration;

    /// <summary>Timestamp of the last successful command dispatch (System.Environment.TickCount64).</summary>
    private static long _lastDispatchTick;

    /// <summary>Whether the stall watchdog timer is running.</summary>
    private static bool _watchdogRunning;

    /// <summary>
    /// Game speed multiplier during replay. 1.0 = normal, 2.0 = double speed, etc.
    /// Applied via Engine.TimeScale when replay is active.
    /// Set to 1.0 to restore normal speed.
    /// </summary>
    private static float _gameSpeed = 2.0f;
    public static float GameSpeed
    {
        get => _gameSpeed;
        set
        {
            _gameSpeed = Math.Max(0.1f, value);
            if (ReplayEngine.IsActive)
                Engine.TimeScale = _gameSpeed;
        }
    }

    /// <summary>
    /// Applies the game speed when replay starts.
    /// Called from ReplayEngine.Load or when replay becomes active.
    /// </summary>
    public static void ApplyGameSpeed()
    {
        if (ReplayEngine.IsActive)
            Engine.TimeScale = _gameSpeed;
    }

    /// <summary>
    /// Restores normal game speed. Called when replay ends or is cancelled.
    /// </summary>
    public static void RestoreGameSpeed()
    {
        Engine.TimeScale = 1.0;
    }

    /// <summary>
    /// Tracks whether a map move is in progress.  Set when a MoveToMapCoordAction
    /// is dispatched, cleared when a room readiness signal arrives (Combat, Event,
    /// Shop, RestSite, Treasure).  Blocks dispatch while the new room loads.
    /// </summary>
    public static bool MapMoveInFlight { get; set; }

    /// <summary>
    /// Called when combat effects have settled after a card play, potion use,
    /// etc.  Bypasses the delay timer for immediate dispatch.
    /// </summary>
    public static void NotifyEffectsSettled()
    {
        DispatchNow();
    }

    /// <summary>
    /// Bypasses the delay timer and dispatches the next command immediately.
    /// Called by patches when they know the game is ready and waiting would
    /// cause a visible stall (e.g. after effects settle, after a screen opens).
    /// </summary>
    public static void DispatchNow()
    {
        ++_dispatchGeneration; // Invalidate any pending timer callback.
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;
        Callable.From(ExecuteNext).CallDeferred();
    }

    /// <summary>
    /// Called by ReplayEngine.SignalConsumed when a command is dequeued.
    /// Clears the in-progress guard and re-triggers dispatch for the next command.
    /// </summary>
    internal static void NotifyConsumed()
    {
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;

        // Don't re-trigger immediately — respect the delay between commands.
        int gen = ++_dispatchGeneration;
        if (_delayBetweenCommands > 0)
        {
            NGame.Instance?.GetTree()?.CreateTimer(_delayBetweenCommands).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        TryDispatch();
                }));
        }
        else
        {
            Callable.From(() =>
            {
                if (_dispatchGeneration == gen)
                    TryDispatch();
            }).CallDeferred();
        }
    }

    /// <summary>
    /// Stops the replay mid-flight and transitions to recording mode.
    /// Restores already-consumed commands to the action buffer so the
    /// next save log contains the full history.
    /// </summary>
    public static void StopAndRecord()
    {
        // Calculate consumed commands: _loadedCommands minus what's still in _pending.
        var loaded = ReplayEngine._loadedCommands;
        int remaining = ReplayEngine._pending.Count;
        int consumed = loaded.Count - remaining;

        if (consumed > 0)
        {
            var consumedCommands = loaded.GetRange(0, consumed);
            // Fire ReplayCompleted with only the consumed portion so the buffer
            // gets the commands that were actually replayed.
            ReplayEngine.FireReplayCompleted(consumedCommands);
        }

        Clear();
    }

    /// <summary>Clears the replay queue and resets all patch state.</summary>
    public static void Clear()
    {
        ReplayEngine._pending.Clear();
        ReplayEngine._recentConsumed.Clear();
        ReplayEngine._replayActive = false;
        RestoreGameSpeed();
        ResetAllPatchState();
    }

    /// <summary>
    /// Resets all static state across recording and replay patches so that
    /// both paths can start cleanly after a stall or cancellation.
    /// </summary>
    public static void ResetAllPatchState()
    {
        // Recording state
        BattleRewardPatch.IsProcessingCardReward = false;
        DeckRemovalState.PendingRemoval = false;
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingLabel = null;
        EventSelectionPatch.PendingIndex = null;
        SimpleGridContext.Pending = false;
        HandCardSelectRecordPatch.SuppressNext = false;

        // Crystal sphere
        CrystalSphereReplayPatch.PendingTool = null;

        // Dispatcher
        Reset();
    }

    /// <summary>Resets all dispatcher state.  Called on replay start and clear.</summary>
    public static void Reset()
    {
        ReplayState.Reset();
        _paused = false;
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;
        MapMoveInFlight = false;
        _lastDispatchTick = System.Environment.TickCount64;
        ++_dispatchGeneration;
        _lastDispatchableTypes = null;
        RestoreGameSpeed();
        StartWatchdog();
        SubscribeToRoomEvents();
    }

    private static bool _subscribedToRoomEvents;

    private static void SubscribeToRoomEvents()
    {
        if (_subscribedToRoomEvents) return;
        _subscribedToRoomEvents = true;
        RunManager.Instance.RoomEntered += OnRoomEntered;
        RunManager.Instance.RoomExited += OnRoomExited;
    }

    private static void OnRoomEntered()
    {
        if (!ReplayEngine.IsActive) return;

        MapMoveInFlight = false;
        TryDispatch();
    }

    private static void OnRoomExited()
    {
        ReplayState.ActiveMerchantRoom = null;

        if (!ReplayEngine.IsActive) return;

        ReplayState.DrainScreenCleanup();
        ReplayState.ActiveEventSynchronizer = null;
        TreasureRoomReplayPatch.ActiveRoom = null;
    }

    /// <summary>
    /// Starts a recurring 2-second watchdog that checks for stalled map moves.
    /// If no command has been dispatched for 5+ seconds and the next command
    /// is a map move, forces readiness and dispatches it.
    /// </summary>
    public static void StartWatchdog()
    {
        if (_watchdogRunning) return;
        _watchdogRunning = true;
        ScheduleWatchdogTick();
    }

    private static void ScheduleWatchdogTick()
    {
        if (!_watchdogRunning) return;
        NGame.Instance?.GetTree()?.CreateTimer(1.0).Connect(
            "timeout", Callable.From(WatchdogTick));
    }

    private static void WatchdogTick()
    {
        if (!ReplayEngine.IsActive)
        {
            _watchdogRunning = false;
            return;
        }
        
        long elapsed = System.Environment.TickCount64 - _lastDispatchTick;

        if (elapsed >= 5000
            && ReplayEngine.PeekNext(out ReplayCommand? cmd) && cmd != null
            && cmd is MapMoveCommand)
        {
            // Force-clear blockers so dispatch can proceed.
            ReplayState.ClearActionInFlight();
            MapMoveInFlight = false;
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            NMapScreen.Instance?.Open();
            NMapScreen.Instance?.SetTravelEnabled(true);
            DispatchNow();
        }

        ScheduleWatchdogTick();
    }

    /// <summary>
    /// Checks if the next queued command can execute given the current readiness
    /// state, and if so, executes it.
    /// </summary>
    internal static void TryDispatch()
    {
        if (!ReplayEngine.IsActive || _paused)
            return;

        if (!ReplayEngine.PeekNext(out ReplayCommand? cmd) || cmd == null)
            return;

        if (!GetDispatchableTypes().Contains(cmd.GetType()))
            return;

        // Don't re-dispatch the same command that's already in progress.
        if (_dispatchInProgress && cmd == _lastDispatchedCmd)
            return;

        _dispatchInProgress = true;
        _lastDispatchedCmd = cmd;

        int gen = ++_dispatchGeneration;
        if (_delayBetweenCommands > 0)
        {
            NGame.Instance!.GetTree()!.CreateTimer(_delayBetweenCommands).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                    {
                        ExecuteNext();
                    }
                }));
        }
        else
        {
            Callable.From(() =>
            {
                if (_dispatchGeneration == gen)
                    ExecuteNext();
            }).CallDeferred();
        }
    }

    private static void scheduleDispatchOnDelay()
    {
        int gen = ++_dispatchGeneration;
        NGame.Instance?.GetTree()?.CreateTimer(0.3f).Connect(
            "timeout", Callable.From(() =>
            {
                if (_dispatchGeneration == gen)
                    TryDispatch();
            }));
    }

    private static void ExecuteNext()
    {
        // Re-apply speed in case the game reset Engine.TimeScale during a transition.
        if (ReplayEngine.IsActive && Engine.TimeScale != _gameSpeed)
            Engine.TimeScale = _gameSpeed;

        if (!ReplayEngine.IsActive)
            return;

        if (_paused && !_stepping)
            return;

        _stepping = false;

        if (!ReplayEngine.PeekNext(out ReplayCommand? cmd) || cmd == null)
            return;

        if (!GetDispatchableTypes().Contains(cmd.GetType()))
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            scheduleDispatchOnDelay();
            return;
        }

        _lastDispatchTick = System.Environment.TickCount64;

        var result = cmd.Execute();
        if (result.Success)
        {
            ReplayEngine.ConsumeAny();
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            int gen = ++_dispatchGeneration;
            NGame.Instance?.GetTree()?.CreateTimer(0.5f).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        TryDispatch();
                }));
            return;
        }
        if (result.RetryDelayMs > 0)
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            int gen = ++_dispatchGeneration;
            NGame.Instance?.GetTree()?.CreateTimer(result.RetryDelayMs / 1000f).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        TryDispatch();
                }));
            return;
        }

        PlayerActionBuffer.LogMigrationWarning($"[Dispatcher] Unrecognised command: {cmd}");
    }

}
