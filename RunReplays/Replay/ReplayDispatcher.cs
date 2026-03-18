using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using RunReplays.Commands;

namespace RunReplays;

/// <summary>
/// Central controller for replay command execution.  Sits between game events
/// (which signal readiness) and command execution (which invokes game APIs).
///
/// Game event postfixes call <see cref="SignalReady"/> to indicate that the
/// game is ready to accept a particular category of action.  The dispatcher
/// checks the next command in the queue and, if it matches a ready category,
/// executes it after an optional delay.
///
/// This gives the mod control over pacing, pausing, and stepping through
/// replay commands independently of game event timing.
/// </summary>
public static class ReplayDispatcher
{
    /// <summary>Categories of game readiness, set by Harmony postfixes.</summary>
    [Flags]
    public enum ReadyState
    {
        None            = 0,
        Combat          = 1 << 0,  // TurnStarted — card plays, potions, end turn
        Rewards         = 1 << 1,  // NRewardsScreen._Ready
        Map             = 1 << 2,  // NMapScreen.SetTravelEnabled(true)
        Event           = 1 << 3,  // EventSynchronizer.BeginEvent
        RestSite        = 1 << 4,  // RestSiteSynchronizer.BeginRestSite
        Shop            = 1 << 5,  // NMerchantRoom._Ready / OpenInventory
        Treasure        = 1 << 6,  // NTreasureRoom._Ready / InitializeRelics
        CrystalSphere   = 1 << 7,  // NCrystalSphereScreen.AfterOverlayOpened
        StartingBonus   = 1 << 8,  // BeginEvent for starting event
        CardSelection   = 1 << 9,  // FromChooseACardScreen, FromDeck*, etc.
        Proceed         = 1 << 10, // NProceedButton._Ready
    }

    private static ReadyState _ready;
    private static bool _paused;
    private static float _delayBetweenCommands = 2.0f;
    private static bool _stepping;
    private static bool _stepRequested;

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

    /// <summary>Current readiness state (bitmask of what the game is ready for).</summary>
    public static ReadyState Ready => _ready;

    /// <summary>When true, the dispatcher stops executing commands until unpaused.</summary>
    public static bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value)
                TryDispatch();
        }
    }

    /// <summary>Delay in seconds between command executions. 0 = immediate.</summary>
    public static float DelayBetweenCommands
    {
        get => _delayBetweenCommands;
        set => _delayBetweenCommands = Math.Max(0, value);
    }

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
    /// When true, the dispatcher executes one command per <see cref="Step"/> call
    /// instead of automatically chaining.
    /// </summary>
    public static bool Stepping
    {
        get => _stepping;
        set
        {
            _stepping = value;
            if (!value)
                TryDispatch();
        }
    }

    /// <summary>
    /// Tracks whether a card play is in flight.  Set by the combat patch.
    /// Does NOT trigger immediate dispatch on clear — effects need to settle
    /// first.  <see cref="NotifyEffectsSettled"/> triggers dispatch after.
    /// </summary>
    public static bool CardPlayInFlight { get; set; }

    /// <summary>
    /// Tracks whether a potion use is in flight.  Set when EnqueueManualUse
    /// is called, cleared when AfterActionExecuted fires for UsePotionAction.
    /// Blocks dispatch while the potion animation is playing.
    /// </summary>
    public static bool PotionInFlight { get; set; }

    /// <summary>
    /// True while a game action is executing (between BeforeActionExecuted
    /// and AfterActionExecuted).  Blocks all non-selection command dispatch.
    /// </summary>
    private static bool _actionInFlight;
    public static bool ActionInFlight => _actionInFlight;

    /// <summary>
    /// Subscribes to BeforeActionExecuted / AfterActionExecuted on the given
    /// executor so that <see cref="ActionInFlight"/> tracks action execution.
    /// Called from the ActionExecutor constructor patch.
    /// </summary>
    public static void SubscribeToExecutor(ActionExecutor executor)
    {
        executor.BeforeActionExecuted += OnBeforeAction;
        executor.AfterActionExecuted += OnAfterAction;
    }

    private static void OnBeforeAction(GameAction action)
    {
        if (!ReplayEngine.IsActive) return;
        _actionInFlight = true;
        PlayerActionBuffer.LogDispatcher($"[ActionFlight] BEGIN: {action.GetType().Name}");
    }

    private static void OnAfterAction(GameAction action)
    {
        if (!ReplayEngine.IsActive) return;
        _actionInFlight = false;
        PlayerActionBuffer.LogDispatcher($"[ActionFlight] END:   {action.GetType().Name}");
        TryDispatch();
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

    /// <summary>Execute the next command when in stepping mode.</summary>
    public static void Step()
    {
        _stepRequested = true;
        TryDispatch();
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

    /// <summary>Room readiness signals that indicate a map move has completed.</summary>
    private const ReadyState RoomReadyMask =
        ReadyState.Combat | ReadyState.Event | ReadyState.RestSite |
        ReadyState.Shop | ReadyState.Treasure;

    /// <summary>
    /// Called by Harmony postfixes to signal that the game is ready for a
    /// category of action.  Triggers dispatch if the next command matches.
    /// </summary>
    public static void SignalReady(ReadyState state)
    {
        _ready |= state;

        // A room readiness signal means the map move completed and the
        // new room is loaded — unblock dispatch.
        if (MapMoveInFlight && (state & RoomReadyMask) != 0)
            MapMoveInFlight = false;

        TryDispatch();
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

    /// <summary>Clears a readiness flag (e.g. when a screen closes).</summary>
    public static void ClearReady(ReadyState state)
    {
        _ready &= ~state;
    }

    /// <summary>Resets all dispatcher state.  Called on replay start and clear.</summary>
    public static void Reset()
    {
        _ready = ReadyState.None;
        _paused = false;
        _stepping = false;
        _stepRequested = false;
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;
        CardPlayInFlight = false;
        PotionInFlight = false;
        MapMoveInFlight = false;
        _actionInFlight = false;
        _lastDispatchTick = System.Environment.TickCount64;
        ++_dispatchGeneration;
        RestoreGameSpeed();
        StartWatchdog();
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

        PlayerActionBuffer.LogDispatcher(
            $"[Watchdog] watchdog ({elapsed}ms idle) — ticking");
        
        if (elapsed >= 5000
            && ReplayEngine.PeekNext(out string? cmd) && cmd != null
            && cmd.StartsWith("MoveToMapCoordAction "))
        {
            PlayerActionBuffer.LogDispatcher(
                $"[Watchdog] Stall detected ({elapsed}ms idle) — forcing map move dispatch.");
            // Force map readiness and clear blockers so dispatch can proceed.
            _actionInFlight = false;
            MapMoveInFlight = false;
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            _ready |= ReadyState.Map;
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

        if (_stepping && !_stepRequested)
            return;

        if (!ReplayEngine.PeekNext(out string? cmd) || cmd == null)
            return;

        // Block dispatch while in-flight operations are pending.
        if (PotionInFlight || MapMoveInFlight || CardPlayReplayPatch.IsAwaitingEndTurnCompletion)
            return;

        // Block while a game action is executing (BeforeAction → AfterAction).
        if (ActionInFlight && !IsSelectionCommand(cmd))
            return;

        // Selection commands are consumed by ICardSelector implementations when the
        // game triggers a selection screen (FromChooseACardScreen, FromDeckGeneric, etc.).
        // The dispatcher does not touch them — the selector consumes the command,
        // NotifyConsumed fires, and the dispatcher advances to the next command.
        if (IsSelectionCommand(cmd))
            return;

        // Block potion use/discard during combat startup (before TurnStarted fires).
        if ((cmd.StartsWith("UsePotionAction ") || cmd.StartsWith("NetDiscardPotionGameAction "))
            && CombatManager.Instance.IsInProgress
            && (_ready & ReadyState.Combat) == 0)
            return;

        // Don't re-dispatch the same command that's already in progress.
        if (_dispatchInProgress && cmd == _lastDispatchedCmd)
            return;

        ReadyState required = GetRequiredState(cmd);
        if (required != ReadyState.None && (_ready & required) == 0)
            return;

        _stepRequested = false;
        _dispatchInProgress = true;
        _lastDispatchedCmd = cmd;

        int gen = ++_dispatchGeneration;
        if (_delayBetweenCommands > 0)
        {
            NGame.Instance!.GetTree()!.CreateTimer(_delayBetweenCommands).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        ExecuteNext();
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

    private static void ExecuteNext()
    {
        // Re-apply speed in case the game reset Engine.TimeScale during a transition.
        if (ReplayEngine.IsActive && Engine.TimeScale != _gameSpeed)
            Engine.TimeScale = _gameSpeed;

        if (!ReplayEngine.IsActive || _paused)
            return;

        if (PotionInFlight || CardPlayInFlight || CardPlayReplayPatch.IsAwaitingEndTurnCompletion || MapMoveInFlight)
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        if (_stepping && !_stepRequested)
            return;

        if (!ReplayEngine.PeekNext(out string? cmd) || cmd == null)
            return;

        // Block while a game action is executing, unless it's a selection command.
        if (ActionInFlight && !IsSelectionCommand(cmd))
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        ReadyState required = GetRequiredState(cmd);
        if (required != ReadyState.None && (_ready & required) == 0)
            return;

        // Potion use/discard returns ReadyState.None (usable in any context),
        // but during combat startup (before TurnStarted fires) the combat
        // state isn't ready.  Block until Combat readiness is set.
        if (required == ReadyState.None
            && (cmd.StartsWith("UsePotionAction ") || cmd.StartsWith("NetDiscardPotionGameAction "))
            && CombatManager.Instance.IsInProgress
            && (_ready & ReadyState.Combat) == 0)
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        _lastDispatchTick = System.Environment.TickCount64;
        PlayerActionBuffer.LogDispatcher($"Attempting to dispatch {cmd}");

        // Try the new command object system first.
        ReplayCommand? parsed = ReplayCommandParser.TryParse(cmd);
        if (parsed != null)
        {
            PlayerActionBuffer.LogDispatcher($"[PARSED] executing through typed command path: {cmd}");
            if (parsed.Execute())
            {
                ReplayEngine.ConsumeAny();
                return;
            }
        }

        // Legacy string-based dispatch for commands not yet migrated.
        switch (required)
        {
            case ReadyState.Combat:
                CardPlayReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.Rewards:
                BattleRewardsReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.Event:
                EventOptionReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.RestSite:
                RestSiteReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.StartingBonus:
                StartingBonusReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.Shop:
                if (cmd == "OpenFakeShop")
                    FakeMerchantReplayPatch.DispatchFromEngine();
                else if (cmd == "OpenShop")
                    ShopOpenedReplayPatch.DispatchFromEngine();
                else if (FakeMerchantReplayPatch.IsActive)
                    FakeMerchantReplayPatch.DispatchFromEngine();
                else
                    ShopOpenedReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.Treasure:
                TreasureRoomReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.CrystalSphere:
                CrystalSphereReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.None:
                // Potion use/discard can happen in any context.
                if (cmd.StartsWith("UsePotionAction ") || cmd.StartsWith("NetDiscardPotionGameAction "))
                    CardPlayReplayPatch.DispatchFromEngine();
                break;
        }
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
    private static ReadyState GetRequiredState(string cmd)
    {
        // Combat
        if (cmd.StartsWith("PlayCardAction ")) return ReadyState.Combat;
        if (cmd.StartsWith("EndPlayerTurnAction ")) return ReadyState.Combat;

        // Potions can be used/discarded in any context (combat, rewards, events, map).
        if (cmd.StartsWith("UsePotionAction ")) return ReadyState.None;
        if (cmd.StartsWith("NetDiscardPotionGameAction ")) return ReadyState.None;

        // Rewards
        if (cmd.StartsWith("TakeGoldReward: ")) return ReadyState.Rewards;
        if (cmd.StartsWith("TakeCardReward")) return ReadyState.Rewards;
        if (cmd == "SacrificeCardReward" || cmd.StartsWith("SacrificeCardReward[")) return ReadyState.Rewards;
        if (cmd.StartsWith("TakeRelicReward: ")) return ReadyState.Rewards;
        if (cmd.StartsWith("TakePotionReward: ")) return ReadyState.Rewards;

        // Navigation
        if (cmd.StartsWith("MoveToMapCoordAction ")) return ReadyState.Map;
        if (cmd.StartsWith("ChooseEventOption ")) return ReadyState.Event;
        if (cmd.StartsWith("ChooseRestSiteOption ")) return ReadyState.RestSite;
        if (cmd.StartsWith("ChooseStartingBonus ")) return ReadyState.StartingBonus;
        if (cmd.StartsWith("VoteForMapCoordAction ")) return ReadyState.Rewards;

        // Shop
        if (cmd == "OpenShop" || cmd == "OpenFakeShop") return ReadyState.Shop;
        if (cmd.StartsWith("BuyCard ") || cmd.StartsWith("BuyRelic ")
            || cmd.StartsWith("BuyPotion ") || cmd == "BuyCardRemoval") return ReadyState.Shop;

        // Treasure
        if (cmd.StartsWith("TakeChestRelic ")) return ReadyState.Treasure;
        if (cmd.StartsWith("NetPickRelicAction ")) return ReadyState.Treasure;

        // Minigames
        if (cmd.StartsWith("CrystalSphereClick ")) return ReadyState.CrystalSphere;

        // Card selections don't need readiness — they're consumed inline by selectors.
        if (cmd.StartsWith("SelectCardFromScreen ")) return ReadyState.None;
        if (cmd.StartsWith("SelectDeckCard ")) return ReadyState.None;
        if (cmd.StartsWith("SelectHandCards")) return ReadyState.None;
        if (cmd.StartsWith("SelectSimpleCard ")) return ReadyState.None;
        if (cmd.StartsWith("RemoveCardFromDeck: ")) return ReadyState.None;
        if (cmd.StartsWith("UpgradeCard ")) return ReadyState.None;

        return ReadyState.None;
    }
}
