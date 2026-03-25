using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using RunReplays.Commands;

namespace RunReplays;

/// <summary>
/// Central controller for replay command execution.  Sits between game events
/// (which signal readiness) and command execution (which invokes game APIs).
///
/// Game event postfixes call <see cref="ReplayState.SignalReady"/> to indicate that the
/// game is ready to accept a particular category of action.  The dispatcher
/// checks the next command in the queue and, if it matches a ready category,
/// executes it after an optional delay.
///
/// This gives the mod control over pacing and pausing replay commands
/// independently of game event timing.
/// </summary>
public static class ReplayDispatcher
{
    public static void Load(IReadOnlyList<string> commands)
    {
        ReplayEngine.Load(commands);
    }

    private static bool _paused;
    private static float _delayBetweenCommands = 2.0f;
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
    private static string? _lastDispatchedCmd;

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
        RestoreGameSpeed();
        StartWatchdog();
        SubscribeToRoomEntered();
    }

    private static bool _subscribedToRoomEntered;

    private static void SubscribeToRoomEntered()
    {
        if (_subscribedToRoomEntered) return;
        _subscribedToRoomEntered = true;
        RunManager.Instance.RoomExited += OnRoomExited;
    }

    private static void OnRoomExited()
    {
        if (!ReplayEngine.IsActive) return;

        ReplayState.DrainScreenCleanup();
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
            && ReplayEngine.PeekNext(out string? cmd) && cmd != null
            && cmd.StartsWith("MoveToMapCoordAction "))
        {
            // Force map readiness and clear blockers so dispatch can proceed.
            ReplayState.ClearActionInFlight();
            MapMoveInFlight = false;
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            ReplayState.SignalReady(ReplayState.ReadyState.Map);
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

        if (!ReplayEngine.PeekNext(out string? cmd) || cmd == null)
            return;

        // Block dispatch while in-flight operations are pending.
        if (ReplayState.PotionInFlight || MapMoveInFlight || CardPlayReplayPatch.IsAwaitingEndTurnCompletion)
            return;

        // Block while a game action is executing (BeforeAction → AfterAction).
        if (ReplayState.ActionInFlight && !IsSelectionCommand(cmd))
            return;

        // Selection commands are consumed by ICardSelector implementations when the
        // game triggers a selection screen (FromChooseACardScreen, FromDeckGeneric, etc.).
        // The dispatcher does not touch them — the selector consumes the command,
        // NotifyConsumed fires, and the dispatcher advances to the next command.
        // Re-check periodically so dispatch resumes promptly after consumption.
        if (IsSelectionCommand(cmd) && !cmd.StartsWith("SelectHandCards"))
        {
            int selGen = _dispatchGeneration;
            NGame.Instance?.GetTree()?.CreateTimer(0.3f).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == selGen) {
                        ExecuteNext();
                    }
                }));
            return;
        }

        // Block potion use/discard during combat startup (before TurnStarted fires).
        if ((cmd.StartsWith("UsePotionAction "))
            && CombatManager.Instance.IsInProgress
            && (ReplayState.Ready & ReplayState.ReadyState.Combat) == 0)
            return;

        // Don't re-dispatch the same command that's already in progress.
        if (_dispatchInProgress && cmd == _lastDispatchedCmd)
            return;

        ReplayState.ReadyState required = GetRequiredState(cmd);
        if (required != ReplayState.ReadyState.None && (ReplayState.Ready & required) == 0)
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

        if (!ReplayEngine.IsActive || _paused)
            return;

        if (!ReplayEngine.PeekNext(out string? cmd) || cmd == null)
            return;
        
        if ((ReplayState.PotionInFlight || ReplayState.CardPlayInFlight || CardPlayReplayPatch.IsAwaitingEndTurnCompletion || MapMoveInFlight) && !IsSelectionCommand(cmd))
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            scheduleDispatchOnDelay();
            return;
        }

        // Block while a game action is executing, unless it's a selection command.
        if (ReplayState.ActionInFlight && !IsSelectionCommand(cmd))
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        ReplayState.ReadyState required = GetRequiredState(cmd);
        if (required != ReplayState.ReadyState.None && (ReplayState.Ready & required) == 0)
            return;

        // Potion use/discard returns ReadyState.None (usable in any context),
        // but during combat startup (before TurnStarted fires) the combat
        // state isn't ready.  Block until Combat readiness is set.
        if (required == ReplayState.ReadyState.None
            && (cmd.StartsWith("UsePotionAction ") || cmd.StartsWith("NetDiscardPotionGameAction "))
            && CombatManager.Instance.IsInProgress
            && (ReplayState.Ready & ReplayState.ReadyState.Combat) == 0)
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        _lastDispatchTick = System.Environment.TickCount64;

        ReplayCommand? parsed = ReplayCommandParser.TryParse(cmd);
        if (parsed != null)
        {
            var result = parsed.Execute();
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
        }

        PlayerActionBuffer.LogMigrationWarning($"[Dispatcher] Unrecognised command: {cmd}");
    }

    /// <summary>
    /// Returns true for card selection commands that are consumed inline by
    /// ICardSelector implementations, not by the dispatcher.
    /// </summary>
    private static bool IsSelectionCommand(string cmd)
    {
        return cmd.StartsWith("SelectCardFromScreen ")
            || cmd.StartsWith("SelectDeckCard ")
            || cmd.StartsWith("SelectHandCards")
            || cmd.StartsWith("SelectSimpleCard ")
            || cmd.StartsWith("RemoveCardFromDeck: ")
            || cmd.StartsWith("UpgradeCard ");
    }

    /// <summary>
    /// Maps a raw command string to the readiness category it requires.
    /// </summary>
    private static ReplayState.ReadyState GetRequiredState(string cmd)
    {
        // Combat
        if (cmd.StartsWith("PlayCardAction ")) return ReplayState.ReadyState.Combat;
        if (cmd.StartsWith("EndPlayerTurnAction ")) return ReplayState.ReadyState.Combat;

        // Potions can be used/discarded in any context (combat, rewards, events, map).
        if (cmd.StartsWith("UsePotionAction ")) return ReplayState.ReadyState.None;
        if (cmd.StartsWith("NetDiscardPotionGameAction ")) return ReplayState.ReadyState.None;

        // Rewards
        if (cmd.StartsWith("TakeGoldReward: ")) return ReplayState.ReadyState.Rewards;
        if (cmd.StartsWith("TakeCardReward")) return ReplayState.ReadyState.Rewards;
        if (cmd == "SacrificeCardReward" || cmd.StartsWith("SacrificeCardReward[")) return ReplayState.ReadyState.Rewards;
        if (cmd.StartsWith("TakeRelicReward: ")) return ReplayState.ReadyState.Rewards;
        if (cmd.StartsWith("TakePotionReward: ")) return ReplayState.ReadyState.Rewards;

        // Navigation
        if (cmd.StartsWith("MoveToMapCoordAction ")) return ReplayState.ReadyState.Map;
        if (cmd.StartsWith("ChooseEventOption ")) return ReplayState.ReadyState.Event;
        if (cmd.StartsWith("ChooseRestSiteOption ")) return ReplayState.ReadyState.RestSite;
        if (cmd.StartsWith("VoteForMapCoordAction ")) return ReplayState.ReadyState.Rewards;

        // Shop
        if (cmd == "OpenShop" || cmd == "OpenFakeShop") return ReplayState.ReadyState.Shop;
        if (cmd.StartsWith("BuyCard ") || cmd.StartsWith("BuyRelic ")
            || cmd.StartsWith("BuyPotion ") || cmd == "BuyCardRemoval") return ReplayState.ReadyState.Shop;

        // Treasure
        if (cmd.StartsWith("TakeChestRelic ")) return ReplayState.ReadyState.Treasure;
        if (cmd.StartsWith("NetPickRelicAction ")) return ReplayState.ReadyState.Treasure;

        // Minigames
        if (cmd.StartsWith("CrystalSphereClick ")) return ReplayState.ReadyState.CrystalSphere;

        // Card selections don't need readiness — they're consumed inline by selectors.
        if (cmd.StartsWith("SelectCardFromScreen ")) return ReplayState.ReadyState.None;
        if (cmd.StartsWith("SelectDeckCard ")) return ReplayState.ReadyState.None;
        if (cmd.StartsWith("SelectHandCards")) return ReplayState.ReadyState.None;
        if (cmd.StartsWith("SelectSimpleCard ")) return ReplayState.ReadyState.None;
        if (cmd.StartsWith("RemoveCardFromDeck: ")) return ReplayState.ReadyState.None;
        if (cmd.StartsWith("UpgradeCard ")) return ReplayState.ReadyState.None;

        return ReplayState.ReadyState.None;
    }
}
