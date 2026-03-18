using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

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
    /// Set when a card play command is issued, cleared when it completes.
    /// When cleared, triggers immediate dispatch (bypasses delay) so the
    /// next card play or end turn doesn't wait unnecessarily.
    /// </summary>
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

    /// <summary>
    /// Called by Harmony postfixes to signal that the game is ready for a
    /// category of action.  Triggers dispatch if the next command matches.
    /// </summary>
    /// <summary>Room readiness signals that indicate a map move has completed.</summary>
    private const ReadyState RoomReadyMask =
        ReadyState.Combat | ReadyState.Event | ReadyState.RestSite |
        ReadyState.Shop | ReadyState.Treasure;

    public static void SignalReady(ReadyState state)
    {
        _ready |= state;

        // A room readiness signal means the map move completed and the
        // new room is loaded — unblock dispatch.
        if (MapMoveInFlight && (state & RoomReadyMask) != 0)
        {
            PlayerActionBuffer.LogDispatcher($"[Dispatcher] MapMove complete — room ready: {state}");
            MapMoveInFlight = false;
        }

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
        ++_dispatchGeneration;
        RestoreGameSpeed();
    }

    /// <summary>
    /// Checks if the next queued command can execute given the current readiness
    /// state, and if so, executes it.
    /// </summary>
    internal static void TryDispatch()
    {
        if (!ReplayEngine.IsActive)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: not active");
            return;
        }

        if (_paused)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: paused");
            return;
        }

        if (_stepping && !_stepRequested)
            return;

        if (!ReplayEngine.PeekNext(out string? cmd) || cmd == null)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: queue empty");
            return;
        }

        // Selection commands are consumed by ICardSelector implementations when the
        // game triggers a selection screen (FromChooseACardScreen, FromDeckGeneric, etc.).
        // The dispatcher does not touch them — the selector consumes the command,
        // NotifyConsumed fires, and the dispatcher advances to the next command.
        if (IsSelectionCommand(cmd))
        {
            PlayerActionBuffer.LogDispatcher(
                $"[Dispatcher] Waiting for selector: '{cmd[..Math.Min(cmd.Length, 50)]}'");
            return;
        }

        // Block dispatch while a potion animation is playing.
        if (PotionInFlight)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: BLOCKED — potion in flight");
            return;
        }

        // Block dispatch while a new room is loading after map movement.
        if (MapMoveInFlight)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: BLOCKED — map move in flight");
            return;
        }

        // Block dispatch while end-turn is completing (waiting for TurnEnded+TurnStarted gate).
        if (CardPlayReplayPatch.IsAwaitingEndTurnCompletion)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: BLOCKED — awaiting end-turn completion");
            return;
        }

        // Block potion use/discard during combat startup (before TurnStarted fires).
        if ((cmd.StartsWith("UsePotionAction ") || cmd.StartsWith("NetDiscardPotionGameAction "))
            && CombatManager.Instance.IsInProgress
            && (_ready & ReadyState.Combat) == 0)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] TryDispatch: BLOCKED — potion during combat startup");
            return;
        }

        // Don't re-dispatch the same command that's already in progress.
        if (_dispatchInProgress && cmd == _lastDispatchedCmd)
        {
            PlayerActionBuffer.LogDispatcher($"[Dispatcher] TryDispatch: BLOCKED — already dispatching '{cmd[..Math.Min(cmd.Length, 50)]}'");
            return;
        }

        ReadyState required = GetRequiredState(cmd);
        if (required != ReadyState.None && (_ready & required) == 0)
        {
            PlayerActionBuffer.LogDispatcher($"[Dispatcher] TryDispatch: WAITING — need {required}, have {_ready}, cmd='{cmd[..Math.Min(cmd.Length, 50)]}'");
            return;
        }

        _stepRequested = false;
        _dispatchInProgress = true;
        _lastDispatchedCmd = cmd;

        int gen = ++_dispatchGeneration;
        PlayerActionBuffer.LogDispatcher(
            $"[Dispatcher] TryDispatch: scheduling gen={gen} delay={_delayBetweenCommands}s cmd='{cmd[..Math.Min(cmd.Length, 40)]}'");
        if (_delayBetweenCommands > 0)
        {
            NGame.Instance!.GetTree()!.CreateTimer(_delayBetweenCommands).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        ExecuteNext();
                    else
                        PlayerActionBuffer.LogDispatcher(
                            $"[Dispatcher] Timer STALE gen={gen} current={_dispatchGeneration}");
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

        PlayerActionBuffer.LogDispatcher(
            $"[Dispatcher] ExecuteNext: gen={_dispatchGeneration} inProgress={_dispatchInProgress}" +
            $" potion={PotionInFlight} card={CardPlayInFlight} endTurn={CardPlayReplayPatch.IsAwaitingEndTurnCompletion}");

        if (!ReplayEngine.IsActive || _paused)
            return;

        if (PotionInFlight)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] ExecuteNext: BLOCKED — potion in flight");
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        if (CardPlayInFlight)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] ExecuteNext: BLOCKED — card play in flight");
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        if (CardPlayReplayPatch.IsAwaitingEndTurnCompletion)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] ExecuteNext: BLOCKED — awaiting end-turn completion");
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        if (MapMoveInFlight)
        {
            PlayerActionBuffer.LogDispatcher("[Dispatcher] ExecuteNext: BLOCKED — map move in flight");
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        if (_stepping && !_stepRequested)
            return;

        if (!ReplayEngine.PeekNext(out string? cmd) || cmd == null)
            return;

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
            PlayerActionBuffer.LogDispatcher("[Dispatcher] ExecuteNext: BLOCKED — potion during combat startup");
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            return;
        }

        PlayerActionBuffer.LogDispatcher(
            $"[Dispatcher] {cmd}");

        switch (required)
        {
            case ReadyState.Combat:
                CardPlayReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.Rewards:
                BattleRewardsReplayPatch.DispatchFromEngine();
                break;
            case ReadyState.Map:
                MapChoiceReplayPatch.DispatchFromEngine();
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
                // Card selections are handled inline by selectors (no dispatch needed).
                if (cmd.StartsWith("UsePotionAction ") || cmd.StartsWith("NetDiscardPotionGameAction "))
                {
                    CardPlayReplayPatch.DispatchFromEngine();
                }
                else
                {
                    PlayerActionBuffer.LogDispatcher($"[Dispatcher] ExecuteNext: no handler for ReadyState.None cmd='{cmd[..Math.Min(cmd.Length, 50)]}'");
                }
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
