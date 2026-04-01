using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
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
    private static readonly MegaCrit.Sts2.Core.Logging.Logger _logger =
        new MegaCrit.Sts2.Core.Logging.Logger("RunReplays", LogType.Generic);
    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static AbstractRoom? GetCurrentRoom()
    {
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        return state?.CurrentRoom;
    }

    private static bool LocalPlayerHasPotions()
    {
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        if (state == null) return false;
        var player = state.Players.FirstOrDefault();
        return player != null && player.Potions.Any();
    }

    private static readonly HashSet<Type> ShopCommandTypes = new()
    {
        typeof(OpenShopCommand),
        typeof(BuyCardCommand), typeof(BuyRelicCommand),
        typeof(BuyCardRemovalCommand), typeof(BuyPotionCommand),
    };

    /// <summary>
    /// Returns the concrete commands that can be dispatched right now,
    /// expanding each available command type against the current game state.
    /// </summary>
    public static List<ReplayCommand> GetAvailableCommands()
    {
        var types = GetDispatchableTypes();
        var commands = new List<ReplayCommand>();
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        var player = state?.Players.FirstOrDefault();

        foreach (var type in types)
        {
            // -- parameterless commands --
            if (type == typeof(EndTurnCommand))
                commands.Add(new EndTurnCommand());
            else if (type == typeof(OpenChestCommand))
                commands.Add(new OpenChestCommand());
            else if (type == typeof(TakeChestRelicCommand))
                commands.Add(new TakeChestRelicCommand());
            else if (type == typeof(ProceedToNextActCommand))
                commands.Add(new ProceedToNextActCommand());
            else if (type == typeof(OpenShopCommand))
                commands.Add(new OpenShopCommand());
            else if (type == typeof(BuyCardRemovalCommand))
                commands.Add(new BuyCardRemovalCommand());
            else if (type == typeof(OpenFakeShopCommand))
                commands.Add(new OpenFakeShopCommand());
            else if (type == typeof(ProceedFromRewardsCommand))
                commands.Add(new ProceedFromRewardsCommand());

            // -- combat card plays --
            else if (type == typeof(PlayCardCommand))
            {
                var combat = player?.PlayerCombatState;
                if (combat != null)
                {
                    var combatState = CombatManager.Instance?.DebugOnlyGetState();
                    var aliveEnemies = combatState?.Enemies
                        .Where(e => e.IsAlive).ToList();

                    foreach (var card in combat.Hand.Cards)
                    {
                        if (!MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb
                                .Instance.TryGetCardId(card, out uint id))
                            continue;

                        if (card.CanPlayTargeting(null))
                            commands.Add(new PlayCardCommand(id));

                        if (aliveEnemies != null)
                            foreach (var enemy in aliveEnemies)
                                if (card.CanPlayTargeting(enemy))
                                    commands.Add(new PlayCardCommand(id, enemy.CombatId));
                    }
                }
            }

            // -- potions --
            else if (type == typeof(UsePotionCommand))
            {
                if (player != null)
                {
                    var combatState = CombatManager.Instance?.DebugOnlyGetState();
                    for (int i = 0; i < player.Potions.Count(); i++)
                    {
                        if (player.GetPotionAtSlotIndex(i) == null) continue;
                        commands.Add(new UsePotionCommand((uint)i));
                        if (combatState != null)
                            foreach (var enemy in combatState.Enemies)
                                if (enemy.IsAlive)
                                    commands.Add(new UsePotionCommand((uint)i, enemy.CombatId));
                    }
                }
            }
            else if (type == typeof(DiscardPotionCommand))
            {
                if (player != null)
                    for (int i = 0; i < player.Potions.Count(); i++)
                        if (player.GetPotionAtSlotIndex(i) != null)
                            commands.Add(new DiscardPotionCommand(i));
            }

            // -- map navigation --
            else if (type == typeof(MapMoveCommand))
            {
                if (NMapScreen.Instance != null
                    && MapMoveCommand.MapPointDictionaryField?.GetValue(NMapScreen.Instance)
                        is Dictionary<MapCoord, NMapPoint> dict)
                {
                    var currentCoord = state?.CurrentMapCoord;
                    if (currentCoord.HasValue && dict.TryGetValue(currentCoord.Value, out var currentPoint))
                    {
                        foreach (var child in currentPoint.Point.Children)
                            commands.Add(new MapMoveCommand(child.coord.col));
                    }
                    else
                    {
                        // First move of act — all row 0 nodes are reachable
                        foreach (var coord in dict.Keys)
                            if (coord.row == 0)
                                commands.Add(new MapMoveCommand(coord.col));
                    }
                }
            }

            // -- rest site --
            else if (type == typeof(ChooseRestSiteOptionCommand))
            {
                var sync = ReplayState.ActiveRestSiteSynchronizer;
                if (sync != null)
                    foreach (var option in sync.GetLocalOptions())
                        commands.Add(new ChooseRestSiteOptionCommand(option.OptionId));
            }

            // -- events --
            else if (type == typeof(ChooseEventOptionCommand))
            {
                var sync = ReplayState.ActiveEventSynchronizer;
                if (sync != null && sync.Events.Count > 0)
                {
                    if (sync.Events[0].IsFinished)
                        commands.Add(new ChooseEventOptionCommand(-1));
                    else
                        for (int i = 0; i < sync.Events[0].CurrentOptions.Count; i++)
                            commands.Add(new ChooseEventOptionCommand(i));
                }
            }

            // -- rewards --
            else if (type == typeof(ClaimRewardCommand))
            {
                var screen = ReplayState.ActiveRewardsScreen;
                if (screen != null && screen.IsInsideTree())
                {
                    int i = 0;
                    foreach (var _ in ClaimRewardCommand.EnumerateRewardButtons(screen))
                        commands.Add(new ClaimRewardCommand(i++));
                }
            }
            else if (type == typeof(TakeCardCommand))
            {
                var screen = ReplayState.CardRewardSelectionScreen;
                if (screen != null)
                {
                    var cardRow = typeof(NCardRewardSelectionScreen)
                        .GetField("_cardRow", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(screen) as Godot.Node;
                    if (cardRow != null)
                    {
                        int count = 0;
                        foreach (Godot.Node child in cardRow.GetChildren())
                        {
                            var prop = child.GetType().GetProperty(
                                "CardModel", BindingFlags.Public | BindingFlags.Instance);
                            if (prop?.GetValue(child) is MegaCrit.Sts2.Core.Models.CardModel)
                                count++;
                        }
                        for (int i = 0; i < count; i++)
                            commands.Add(new TakeCardCommand(i));
                    }
                    commands.Add(TakeCardCommand.Skip());
                    commands.Add(TakeCardCommand.Sacrifice());
                }
            }

            // -- selection screens --
            else if (type == typeof(SelectGridCardCommand))
            {
                var screen = CardGridScreenCapture.ActiveScreen;
                if (screen != null)
                {
                    var cards = CardGridScreenCapture.CardsField?.GetValue(screen)
                        as IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel>;
                    if (cards != null)
                        for (int i = 0; i < cards.Count; i++)
                            commands.Add(new SelectGridCardCommand(new[] { i }));
                }
            }
            else if (type == typeof(SelectCardFromScreenCommand))
            {
                var screen = ChooseACardScreenCapture.ActiveScreen;
                if (screen != null)
                {
                    int count = 0;
                    foreach (Godot.Node node in screen.FindChildren("*", "", owned: false))
                    {
                        var prop = node.GetType().GetProperty(
                            "CardModel", BindingFlags.Public | BindingFlags.Instance);
                        if (prop?.GetValue(node) is MegaCrit.Sts2.Core.Models.CardModel)
                            count++;
                    }
                    for (int i = 0; i < count; i++)
                        commands.Add(new SelectCardFromScreenCommand(i));
                    commands.Add(new SelectCardFromScreenCommand(-1));
                }
            }
            else if (type == typeof(SelectHandCardsCommand))
            {
                var combatState = CombatManager.Instance?.DebugOnlyGetState();
                if (combatState != null)
                {
                    var p = combatState.Players.FirstOrDefault();
                    if (p != null)
                        for (int i = 0; i < p.PlayerCombatState.Hand.Cards.Count; i++)
                            commands.Add(new SelectHandCardsCommand(new[] { i }));
                }
            }
            else if (type == typeof(CrystalSphereClickCommand))
            {
                // Crystal sphere cells require deep grid reflection;
                // not enumerable without knowing dimensions.
            }

            // -- shop purchases --
            else if (type == typeof(BuyCardCommand))
            {
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                {
                    var entries = OpenShopCommand.GetEntries(room);
                    if (entries != null)
                        foreach (var e in entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry>())
                            if (e.CreationResult?.Card?.Title != null)
                                commands.Add(new BuyCardCommand(e.CreationResult.Card.Title));
                }
            }
            else if (type == typeof(BuyRelicCommand))
            {
                List<MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry>? entries = null;
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                    entries = OpenShopCommand.GetEntries(room);
                if ((entries == null || entries.Count == 0) && ReplayState.FakeMerchantInstance != null)
                    entries = BuyRelicCommand.GetFakeMerchantEntries(ReplayState.FakeMerchantInstance);
                if (entries != null)
                    foreach (var e in entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry>())
                    {
                        var title = e.Model?.Title.GetFormattedText();
                        if (title != null)
                            commands.Add(new BuyRelicCommand(title));
                    }
            }
            else if (type == typeof(BuyPotionCommand))
            {
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                {
                    var entries = OpenShopCommand.GetEntries(room);
                    if (entries != null)
                        foreach (var e in entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry>())
                        {
                            var title = e.Model?.Title.GetFormattedText();
                            if (title != null)
                                commands.Add(new BuyPotionCommand(title));
                        }
                }
            }
        }

        return commands;
    }

    private static HashSet<Type> GetDispatchableTypes()
    {
        bool blocked = ReplayState.PotionInFlight
                    || ReplayState.CardPlayInFlight
                    || CardPlayReplayPatch.IsAwaitingEndTurnCompletion
                    || MapMoveInFlight
                    || ReplayState.ActionInFlight;

        
        var types = new HashSet<Type>();
        
        if (CardGridScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(CardGridScreenCapture.ActiveScreen)
            && CardGridScreenCapture.ActiveScreen.IsInsideTree())
            types.Add(typeof(SelectGridCardCommand));
        if (ChooseACardScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(ChooseACardScreenCapture.ActiveScreen)
            && ChooseACardScreenCapture.ActiveScreen.IsInsideTree())
            types.Add(typeof(SelectCardFromScreenCommand));
        if (HandSelectionCapture.ActiveHand != null
            && GodotObject.IsInstanceValid(HandSelectionCapture.ActiveHand)
            && HandSelectionCapture.ActiveHand.IsInsideTree())
            types.Add(typeof(SelectHandCardsCommand));
        
        if (blocked)
        {
            return types;
        }

        // TODO: further refine potion commands to only include potions usable in
        // the current context (e.g. combat-only potions gated behind IsInProgress)
        if (LocalPlayerHasPotions())
        {
            types.Add(typeof(UsePotionCommand));
            types.Add(typeof(DiscardPotionCommand));
        }

        if (CardGridScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(CardGridScreenCapture.ActiveScreen)
            && CardGridScreenCapture.ActiveScreen.IsInsideTree())
            types.Add(typeof(SelectGridCardCommand));
        if (ChooseACardScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(ChooseACardScreenCapture.ActiveScreen)
            && ChooseACardScreenCapture.ActiveScreen.IsInsideTree())
            types.Add(typeof(SelectCardFromScreenCommand));
        if (HandSelectionCapture.ActiveHand != null
            && GodotObject.IsInstanceValid(HandSelectionCapture.ActiveHand)
            && HandSelectionCapture.ActiveHand.IsInsideTree())
            types.Add(typeof(SelectHandCardsCommand));

        if (CrystalSphereReplayPatch.ActiveScreen != null
            && GodotObject.IsInstanceValid(CrystalSphereReplayPatch.ActiveScreen))
            types.Add(typeof(CrystalSphereClickCommand));

        if (CombatManager.Instance.IsInProgress && CombatManager.Instance.IsPlayPhase)
        {
            types.Add(typeof(PlayCardCommand));
            types.Add(typeof(EndTurnCommand));
        }

        if (NMapScreen.Instance != null
            && GodotObject.IsInstanceValid(NMapScreen.Instance)
            && NMapScreen.Instance.IsTravelEnabled)
            types.Add(typeof(MapMoveCommand));

        var currentRoom = GetCurrentRoom();

        if (currentRoom is RestSiteRoom)
            types.Add(typeof(ChooseRestSiteOptionCommand));

        if (currentRoom is TreasureRoom)
        {
            types.Add(typeof(OpenChestCommand));
            types.Add(typeof(TakeChestRelicCommand));
        }

        if (ReplayState.ActiveRewardsScreen != null
            && GodotObject.IsInstanceValid(ReplayState.ActiveRewardsScreen)
            && ReplayState.ActiveRewardsScreen.IsInsideTree())
        {
            types.Add(typeof(ClaimRewardCommand));
            types.Add(typeof(TakeCardCommand));
            types.Add(typeof(ProceedFromRewardsCommand));

            if (currentRoom != null
                && (currentRoom.RoomType == RoomType.Boss || currentRoom.IsVictoryRoom))
                types.Add(typeof(ProceedToNextActCommand));
        }

        if (ReplayState.ActiveMerchantRoom != null)
            types.UnionWith(ShopCommandTypes);

        if (ReplayState.FakeMerchantInstance != null)
        {
            types.Add(typeof(OpenFakeShopCommand));
            types.Add(typeof(BuyRelicCommand));
        }

        if (currentRoom is EventRoom)
        {
            bool hasNonPotionCommands = types.Any(t =>
                t != typeof(UsePotionCommand) && t != typeof(DiscardPotionCommand));
            if (!hasNonPotionCommands)
                types.Add(typeof(ChooseEventOptionCommand));
        }

        return types;
    }

    private static bool _dispatchPollRunning;
    private static HashSet<Type>? _lastDispatchableTypes;

    public static void StartDispatchPoll()
    {
        if (_dispatchPollRunning) return;
        _dispatchPollRunning = true;
        EnsureEmitter();
        PlayerActionBuffer.LogMigrationWarning("connected");
        _emitter!.Connect(DispatchSignalEmitter.SignalInputRequired,
            Callable.From(() =>
            {
                PlayerActionBuffer.LogMigrationWarning("signal fire");
                var state = new GameStateSnapshot(GetDispatchableTypes());
                var json = System.Text.Json.JsonSerializer.Serialize(state,
                    new System.Text.Json.JsonSerializerOptions
                    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                var cmds = GetAvailableCommands();
                foreach (var cmd in cmds)
                    PlayerActionBuffer.LogMigrationWarning(cmd.ToString());
            }));
        SubscribeToRoomEvents();
        ScheduleDispatchPollTick();
    }

    private static void ScheduleDispatchPollTick()
    {
        if (!_dispatchPollRunning) return;
        NGame.Instance?.GetTree()?.CreateTimer(0.5).Connect(
            "timeout", Callable.From(DispatchPollTick));
    }

    public static void ClearDispatchableCache()
    {
        _lastDispatchableTypes?.Clear();
    }

    private static void LogDispatchableChanges()
    {
        var current = GetDispatchableTypes();
        if (_lastDispatchableTypes == null || !current.SetEquals(_lastDispatchableTypes))
        {
            _lastDispatchableTypes = new HashSet<Type>(current);

            if (current.Count > 0 && _emitter != null && GodotObject.IsInstanceValid(_emitter))
                _emitter.EmitSignal("InputRequired");
        }
    }

    private static void DispatchPollTick()
    {
        LogDispatchableChanges();
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
        LogDispatchableChanges();

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
        LogDispatchableChanges();

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


    private static DispatchSignalEmitter? _emitter;
    public static DispatchSignalEmitter? Emitter => _emitter;

    private static void EnsureEmitter()
    {
        if (_emitter != null && GodotObject.IsInstanceValid(_emitter)) return;
        _emitter = new DispatchSignalEmitter();
        _emitter.Name = "DispatchSignalEmitter";
        NGame.Instance?.AddChild(_emitter);
    }
}

public partial class DispatchSignalEmitter : Node
{
    public const string SignalInputRequired = "InputRequired";

    public DispatchSignalEmitter()
    {
        AddUserSignal(SignalInputRequired);
    }
}
