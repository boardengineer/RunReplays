using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Drives automatic card play and turn-ending during combat replays.
///
/// Three triggers are used:
///
///   1. CombatManager.TurnStarted — fires at the start of every player turn.
///      Plays the first card of the turn (or ends it immediately if the next
///      command is EndPlayerTurnAction with no cards to play first).
///
///   2. AfterActionExecuted for PlayCardAction — chains the next card play
///      after each card executes.  When no more card plays remain, checks
///      whether the next command is EndPlayerTurnAction and ends the turn.
///
///   3. Both paths call TryEndTurn after exhausting card plays.
///
/// Subscribed once per run from the ActionExecutor constructor patch.
/// A static field prevents handler accumulation across multiple runs.
/// </summary>
[HarmonyPatch(typeof(ActionExecutor), MethodType.Constructor, new[] { typeof(ActionQueueSet) })]
public static class CardPlayReplayPatch
{
    private static Action<CombatState>? _turnStartedHandler;
    private static Action<CombatState>? _turnEndedHandler;
    internal static CombatState?         _currentCombatState;

    /// <summary>
    /// Incremented each time a new battle starts (ActionExecutor ctor).
    /// Timer callbacks capture the generation at creation time and become
    /// no-ops if it has changed, preventing stale callbacks from a previous
    /// battle from corrupting dispatch state.
    /// </summary>
    private static int _battleGeneration;

    /// <summary>
    /// True after TryEndTurn issues PlayerCmd.EndTurn.  Blocks all dispatch
    /// until both TurnEnded and TurnStarted have fired (in any order).
    /// </summary>
    private static bool _awaitingEndTurnCompletion;

    /// <summary>Exposes _awaitingEndTurnCompletion for the overlay UI.</summary>
    internal static bool IsAwaitingEndTurnCompletion => _awaitingEndTurnCompletion;

    /// <summary>
    /// Whether TurnEnded has fired since the last TryEndTurn.
    /// Part of the two-signal gate for EndTurn completion.
    /// </summary>
    private static bool _postEndTurn_turnEndedReceived;

    /// <summary>
    /// Whether TurnStarted has fired since the last TryEndTurn.
    /// Part of the two-signal gate for EndTurn completion.
    /// </summary>
    private static bool _postEndTurn_turnStartedReceived;

    /// <summary>
    /// The CombatState from TurnStarted, saved so that when TurnEnded fires
    /// second we can still dispatch with the correct state.
    /// </summary>
    private static CombatState? _postEndTurn_savedTurnStartState;

    /// <summary>
    /// Set to true by OnTurnStarted, cleared by TryEndTurn after issuing
    /// PlayerCmd.EndTurn.  Prevents consecutive EndTurn commands from being
    /// issued without an intervening TurnStarted signal.
    /// </summary>
    private static bool _turnStartedSinceLastEndTurn;

    private static readonly FieldInfo? SelectorStackField =
        typeof(CardSelectCmd).GetField("_selectorStack", BindingFlags.NonPublic | BindingFlags.Static);

    [HarmonyPostfix]
    public static void Postfix(ActionExecutor __instance)
    {
        // Unsubscribe any handler from the previous run before subscribing again.
        if (_turnStartedHandler != null)
            CombatManager.Instance.TurnStarted -= _turnStartedHandler;
        if (_turnEndedHandler != null)
            CombatManager.Instance.TurnEnded -= _turnEndedHandler;

        _turnStartedHandler = OnTurnStarted;
        _turnEndedHandler = OnTurnEnded;
        CombatManager.Instance.TurnStarted += _turnStartedHandler;
        CombatManager.Instance.TurnEnded += _turnEndedHandler;

        __instance.AfterActionExecuted += OnAfterActionExecuted;
        ReplayState.SubscribeToExecutor(__instance);

        // Invalidate any pending timer callbacks from the previous battle.
        _battleGeneration++;

        // Reset all dispatch state so stale flags from a previous battle
        // or non-combat context don't block OnTurnStarted.
        _dispatching = false;
        _waitingForEffects = false;
        _actionFiredThisFrame = false;
        _quietFrameCount = 0;
        _awaitingEndTurnCompletion = false;

        _turnStartedSinceLastEndTurn = true;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;
        _postEndTurn_savedTurnStartState = null;

        // Clear Combat readiness so potion use is blocked until TurnStarted fires.
        ReplayState.ClearReady(ReplayState.ReadyState.Combat);

        SelectorStackDebug.Log("\n=== Battle Start (ActionExecutor ctor) ===");
        LogCardSelectState("ActionExecutor ctor (battle start)");
        TreasureRoomReplayPatch.ActiveRoom = null;
        RngCheckpointLogger.Log("CombatStart (ActionExecutor ctor)");
    }

    internal static void LogCardSelectState(string context)
    {
        var stack = SelectorStackField?.GetValue(null) as ICollection;
        int stackCount = stack?.Count ?? -1;
        string stackTypes = "";
        if (stack != null && stackCount > 0)
        {
            var types = new System.Collections.Generic.List<string>();
            foreach (object? item in stack)
                types.Add(item?.GetType().Name ?? "null");
            stackTypes = $" [{string.Join(", ", types)}]";
        }

        bool deckPending = DeckCardSelectContext.Pending;
        bool hasGenericScope = FromDeckGenericPatch._pendingScope != null;
        bool hasEnchantScope = FromDeckForEnchantmentPatch._pendingScope != null;
        bool hasEnchantFilterScope = FromDeckForEnchantmentWithFilterPatch._pendingScope != null;
        bool hasTransformScope = FromDeckForTransformationPatch._pendingScope != null;
        bool hasUpgradeScope = FromDeckForUpgradePatch._pendingScope != null;
        bool hasChoiceScope = FromChooseACardScreenPatch._pendingScope != null;
        bool hasSimpleGridScope = FromSimpleGridPatch._pendingScope != null;

        PlayerActionBuffer.LogToDevConsole(
            $"[CardSelectState@{context}] selectorStack.Count={stackCount}{stackTypes}" +
            $" | DeckPending={deckPending}" +
            $" | Scopes: Generic={hasGenericScope} Enchant={hasEnchantScope} EnchantFilter={hasEnchantFilterScope}" +
            $" Transform={hasTransformScope} Upgrade={hasUpgradeScope}" +
            $" Choice={hasChoiceScope} SimpleGrid={hasSimpleGridScope}");
    }

    // ── Player resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Bumps _battleGeneration so all pending timer callbacks from the
    /// previous room's battle become no-ops.  Does not touch any other
    /// dispatch state.
    /// </summary>
    internal static void InvalidateStaleTimers()
    {
        PlayerActionBuffer.LogToDevConsole($"[RunReplays] InvalidateStaleTimers: gen {_battleGeneration} → {_battleGeneration + 1}");
        _battleGeneration++;
        // Clear flags that block OnTurnStarted from starting a new dispatch.
        _dispatching = false;
        _waitingForEffects = false;
        _awaitingEndTurnCompletion = false;
    }

    /// <summary>
    /// Resolves the local player, trying combat state first and falling back
    /// to RunManager's RewardSynchronizer for out-of-combat contexts (shop,
    /// events, map).
    /// </summary>
    internal static Player? ResolveLocalPlayer()
    {
        Player? player = null;

        // 1. Current combat state (set by OnTurnStarted, may persist after combat ends).
        try { player = LocalContext.GetMe(_currentCombatState); }
        catch { /* ignore */ }

        player ??= _currentCombatState?.Players.FirstOrDefault();

        // 2. Fresh combat state from CombatManager.
        if (player == null)
        {
            try
            {
                var combatState = CombatManager.Instance?.DebugOnlyGetState();
                if (combatState != null)
                {
                    try { player = LocalContext.GetMe(combatState); }
                    catch { /* ignore */ }
                    player ??= combatState.Players.FirstOrDefault();
                }
            }
            catch { /* ignore */ }
        }

        // 3. Out-of-combat fallback: RewardSynchronizer.LocalPlayer via reflection.
        if (player == null)
        {
            try
            {
                var rewardSync = RunManager.Instance?.RewardSynchronizer;
                if (rewardSync != null)
                {
                    var prop = rewardSync.GetType().GetProperty("LocalPlayer",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    player = prop?.GetValue(rewardSync) as Player;
                }
            }
            catch { /* ignore */ }
        }

        return player;
    }

    /// <summary>
    /// Returns true when combat is in progress, a local player exists, and
    /// the player's hand has been drawn (i.e. cards are available).
    /// </summary>
    internal static bool IsCombatReady()
    {
        try
        {
            if (!CombatManager.Instance.IsInProgress)
                return false;

            // Cards can't be played while the game is drawing cards.
            if (!CombatManager.Instance.IsPlayPhase)
                return false;

            var state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null)
                return false;

            Player? player;
            try { player = LocalContext.GetMe(state); }
            catch { player = state.Players.FirstOrDefault(); }

            if (player == null)
                return false;

            var hand = player.PlayerCombatState?.Hand?.Cards;
            return hand != null && hand.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Turn-start trigger ────────────────────────────────────────────────────

    /// <summary>
    /// True while we are actively dispatching commands for the current turn.
    /// Prevents OnTurnStarted from starting a second dispatch chain when the
    /// previous turn's chain (quiet-frame wait → DispatchNextCombatAction)
    /// already handles the transition.
    /// </summary>
    internal static bool _dispatching;

    private static void OnTurnStarted(CombatState combatState)
    {
        RngCheckpointLogger.Log($"TurnStarted (round {combatState.RoundNumber})");
        PlayerActionBuffer.LogToDevConsole(
            $"[RunReplays] >>> TurnStarted round={combatState.RoundNumber}" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion} dispatching={_dispatching}" +
            $" turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}");

        if (!ReplayEngine.IsActive)
            return;

        ReplayState.DrainScreenCleanup();
        ReplayState.SignalReady(ReplayState.ReadyState.Combat);

        ReplayEngine.PeekNext(out string? nextCmd);
        SelectorStackDebug.Log(
            $"OnTurnStarted: round={combatState.RoundNumber}" +
            $" dispatching={_dispatching} waitingForEffects={_waitingForEffects}" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion}" +
            $" turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}" +
            $" nextCmd='{nextCmd ?? "(none)"}'");

        _currentCombatState = combatState;
        _turnStartedSinceLastEndTurn = true;

        // If we're waiting for EndTurn completion, record that TurnStarted
        // has arrived and check if both signals have now fired.
        if (_awaitingEndTurnCompletion)
        {
            _postEndTurn_turnStartedReceived = true;
            _postEndTurn_savedTurnStartState = combatState;
            TryCompleteEndTurnGate();
            return;
        }

        if (_waitingForEffects || _dispatching)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TurnStarted: dispatch chain already active, skipping.");
            return;
        }

        ReplayDispatcher.DispatchNow();
    }

    private static void OnTurnEnded(CombatState combatState)
    {
        PlayerActionBuffer.LogToDevConsole(
            $"[RunReplays] >>> TurnEnded round={combatState.RoundNumber}" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion}");

        if (!ReplayEngine.IsActive)
            return;

        SelectorStackDebug.Log(
            $"OnTurnEnded: round={combatState.RoundNumber}" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion}");

        if (!_awaitingEndTurnCompletion)
            return;

        _postEndTurn_turnEndedReceived = true;

        // If combat is ending (enemy killed on the last turn), TurnStarted
        // will never fire.  Force-complete the gate so the dispatcher can
        // advance to the rewards screen.
        if (CombatManager.Instance.IsOverOrEnding)
        {
            ClearEndTurnGate();
            return;
        }

        TryCompleteEndTurnGate();
    }

    /// <summary>
    /// Called by both OnTurnEnded and OnTurnStarted.  When both signals have
    /// fired (in any order) after TryEndTurn, clears all end-turn state and
    /// dispatches the next turn's commands with a 0.5s delay.
    /// </summary>
    private static void TryCompleteEndTurnGate()
    {
        if (!_postEndTurn_turnEndedReceived || !_postEndTurn_turnStartedReceived)
            return;

        SelectorStackDebug.Log("TryCompleteEndTurnGate: both signals received — dispatching.");
        // Clear all end-turn state.
        _awaitingEndTurnCompletion = false;
        _dispatching = false;
        _waitingForEffects = false;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;

        if (_postEndTurn_savedTurnStartState != null)
            _currentCombatState = _postEndTurn_savedTurnStartState;
        _postEndTurn_savedTurnStartState = null;

        // Route back through the dispatcher for the next turn.
        ReplayState.SignalReady(ReplayState.ReadyState.Combat);
    }

    // ── Post-action chain trigger (waits for sub-effects to settle) ─────────

    /// <summary>
    /// True while we're waiting for all sub-effects of a card/potion action
    /// to finish before dispatching the next replay command.  While waiting,
    /// every AfterActionExecuted resets a quiet-frame flag.  Once a full
    /// frame passes with no AfterActionExecuted, effects have settled.
    /// </summary>
    private static bool _waitingForEffects;
    private static bool _actionFiredThisFrame;
    private static int  _quietFrameCount;

    /// <summary>
    /// Number of consecutive quiet frames (no AfterActionExecuted) required
    /// before dispatching the next command.  Extra frames allow enemy
    /// animations (damage numbers, status effects, deaths) to finish.
    /// </summary>
    private const int QuietFramesRequired = 3;

    
    
    private static void OnAfterActionExecuted(GameAction action)
    {
        PlayerActionBuffer.LogDispatcher("After action executed?");
        if (!ReplayEngine.IsActive)
            return;
        PlayerActionBuffer.LogDispatcher("Schedule next from queue 4");
        // While waiting for the end-turn to complete, ignore all actions.
        // Only OnTurnStarted (the next player turn) clears this flag.
        if (_awaitingEndTurnCompletion)
        {
            if (action is PlayCardAction)
            return;
        }

        if (_waitingForEffects)
        {
            // A sub-effect just finished — mark this frame as active so the
            // quiet-frame counter resets.  This must fire for ALL actions
            // (including enemy sub-effects) while we're waiting.
            _actionFiredThisFrame = true;
            return;
        }

        // Potion discards initiated from the rewards screen (not combat dispatch)
        // need to resume reward processing once they complete.
        if (!_dispatching && action is DiscardPotionGameAction)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] AfterActionExecuted: DiscardPotionGameAction completed outside combat — resuming rewards.");
            ReplayDispatcher.DispatchNow();
            return;
        }

        // Potion use completed outside combat — clear the in-flight flag
        // and let the dispatcher advance.
        if (!_dispatching && action is UsePotionAction outOfCombatPotion)
        {
            ReplayState.PotionInFlight = false;
            ReplayDispatcher.DispatchNow();
            return;
        }

        // Only chain the next dispatch for actions initiated by our replay.
        // Without this guard, enemy PlayCardActions during the enemy turn
        // trigger DispatchNextCombatAction, which prematurely consumes the
        // next player EndTurn command and stalls the replay.
        if (!_dispatching)
        {
            return;
        }

        if (action is PlayCardAction playCard)
        {
            ReplayState.CardPlayInFlight = false;
            RunOverlay.NotifyCardPlayFinished();
            WaitForEffectsThenDispatch();
        }
        else if (action is DiscardPotionGameAction discardPotion)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: DiscardPotionGameAction completed ({discardPotion}).");
            WaitForEffectsThenDispatch();
        }
        else if (action is UsePotionAction usePotion)
        {
            ReplayState.PotionInFlight = false;
            WaitForEffectsThenDispatch();
        }
    }

    private static void WaitForEffectsThenDispatch()
    {
        _waitingForEffects = true;
        _actionFiredThisFrame = false;
        _quietFrameCount = 0;
        Callable.From(CheckEffectsSettled).CallDeferred();
    }

    private static void CheckEffectsSettled()
    {
        // If _waitingForEffects was cleared externally (e.g. by a new
        // TurnStarted), this polling chain is stale — stop polling.
        if (!_waitingForEffects)
            return;

        if (!ReplayEngine.IsActive)
        {
            _waitingForEffects = false;
            return;
        }

        if (_actionFiredThisFrame)
        {
            // At least one sub-effect fired this frame — reset quiet counter.
            _actionFiredThisFrame = false;
            _quietFrameCount = 0;
            Callable.From(CheckEffectsSettled).CallDeferred();
            return;
        }

        _quietFrameCount++;

        if (_quietFrameCount < QuietFramesRequired)
        {
            // Keep waiting for more quiet frames so enemy animations can finish.
            Callable.From(CheckEffectsSettled).CallDeferred();
            return;
        }

        // Enough consecutive quiet frames — effects and animations settled.
        _waitingForEffects = false;

        // If combat ended (e.g. a triggered power killed the last enemy),
        // clear all combat state so post-combat flows aren't blocked.
        if (CombatManager.Instance.IsOverOrEnding)
        {
            ClearEndTurnGate();
            return;
        }

        DispatchNextCombatAction();
    }

    private static void DispatchNextCombatAction()
    {
        // Route back through the dispatcher — effects have settled, dispatch immediately.
        _dispatching = false;
        ReplayDispatcher.NotifyEffectsSettled();
    }
    
    internal static bool TryEndTurn()
    {
        if (_waitingForEffects)
        {
            return false; 
        }

        // Don't issue EndTurn unless we've received a TurnStarted since the
        // last one — prevents consecutive EndTurns without the game advancing.
        if (!_turnStartedSinceLastEndTurn)
        {
            return false;
        }

        // Wait until combat is in progress and a player is available.
        if (!CombatManager.Instance.IsInProgress || ResolveLocalPlayer() == null)
        {
            return false;
        }

        Player player = ResolveLocalPlayer()!;

        // Block all further dispatch until both TurnEnded and TurnStarted fire.
        _awaitingEndTurnCompletion = true;
        _turnStartedSinceLastEndTurn = false;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;
        _postEndTurn_savedTurnStartState = null;

        PlayerCmd.EndTurn(player, canBackOut: false);

        // If combat ended (enemy killed during the turn, before end-turn),
        // TurnEnded/TurnStarted may never fire.  Check immediately and
        // also set a timer fallback.
        if (CombatManager.Instance.IsOverOrEnding)
        {
            ClearEndTurnGate();
            return true;
        }

        // Timer fallback: if the gate hasn't completed after 5 seconds,
        // force-clear it (handles edge cases where signals are lost).
        int gateGen = _battleGeneration;
        NGame.Instance!.GetTree()!.CreateTimer(5.0).Connect(
            "timeout", Callable.From(() =>
            {
                if (_battleGeneration != gateGen) return;
                if (!_awaitingEndTurnCompletion) return;
                ClearEndTurnGate();
            }));
        return true;
    }

    private static void ClearEndTurnGate()
    {
        _awaitingEndTurnCompletion = false;
        _dispatching = false;
        _waitingForEffects = false;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;
        _postEndTurn_savedTurnStartState = null;
        ReplayState.SignalReady(ReplayState.ReadyState.Combat);
        ReplayState.SignalReady(ReplayState.ReadyState.Rewards);
    }
}
