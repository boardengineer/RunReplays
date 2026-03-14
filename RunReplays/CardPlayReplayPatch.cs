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
    private static CombatState?         _currentCombatState;
    private static int                  _retryCount;

    /// <summary>
    /// Incremented each time a new battle starts (ActionExecutor ctor).
    /// Timer callbacks capture the generation at creation time and become
    /// no-ops if it has changed, preventing stale callbacks from a previous
    /// battle from corrupting dispatch state.
    /// </summary>
    private static int _battleGeneration;


    /// <summary>
    /// True between TryManualPlay succeeding and AfterActionExecuted firing
    /// for PlayCardAction.  Blocks all dispatch entry points so no new
    /// commands are issued while the game is resolving a card play.
    /// </summary>
    private static bool _cardPlayInFlight;

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

        // Invalidate any pending timer callbacks from the previous battle.
        _battleGeneration++;

        // Reset all dispatch state so stale flags from a previous battle
        // or non-combat context don't block OnTurnStarted.
        _dispatching = false;
        _cardPlayInFlight = false;
        _waitingForEffects = false;
        _actionFiredThisFrame = false;
        _quietFrameCount = 0;
        _awaitingEndTurnCompletion = false;
        _endTurnRetryCount = 0;
        _endTurnConsumed = false;
        _retryCount = 0;
        _turnStartRetries = 0;
        _turnStartedSinceLastEndTurn = true;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;
        _postEndTurn_savedTurnStartState = null;

        SelectorStackDebug.Log("\n=== Battle Start (ActionExecutor ctor) ===");
        LogCardSelectState("ActionExecutor ctor (battle start)");
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
        bool hasRemovalScope = DeckRemovalReplayPatch._pendingScope != null;
        bool hasHandScope = HandCardSelectReplayPatch._pendingScope != null;
        bool hasSimpleGridScope = FromSimpleGridPatch._pendingScope != null;

        PlayerActionBuffer.LogToDevConsole(
            $"[CardSelectState@{context}] selectorStack.Count={stackCount}{stackTypes}" +
            $" | DeckPending={deckPending}" +
            $" | Scopes: Generic={hasGenericScope} Enchant={hasEnchantScope} EnchantFilter={hasEnchantFilterScope}" +
            $" Transform={hasTransformScope} Upgrade={hasUpgradeScope}" +
            $" Choice={hasChoiceScope} Removal={hasRemovalScope} Hand={hasHandScope} SimpleGrid={hasSimpleGridScope}");
    }

    // ── Player resolution ──────────────────────────────────────────────────────

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
    private static bool IsCombatReady()
    {
        try
        {
            if (!CombatManager.Instance.IsInProgress)
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
    private static bool _dispatching;

    private static void OnTurnStarted(CombatState combatState)
    {
        RngCheckpointLogger.Log($"TurnStarted (round {combatState.RoundNumber})");
        PlayerActionBuffer.LogToDevConsole(
            $"[RunReplays] >>> TurnStarted round={combatState.RoundNumber}" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion} dispatching={_dispatching}" +
            $" turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}");

        if (!ReplayEngine.IsActive)
            return;

        ReplayEngine.PeekNext(out string? nextCmd);
        SelectorStackDebug.Log(
            $"OnTurnStarted: round={combatState.RoundNumber}" +
            $" dispatching={_dispatching} waitingForEffects={_waitingForEffects}" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion}" +
            $" turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}" +
            $" nextCmd='{nextCmd ?? "(none)"}'");

        _currentCombatState = combatState;
        _retryCount = 0;
        _turnStartedSinceLastEndTurn = true;

        // If we're waiting for EndTurn completion, record that TurnStarted
        // has arrived and check if both signals have now fired.
        if (_awaitingEndTurnCompletion)
        {
            _postEndTurn_turnStartedReceived = true;
            _postEndTurn_savedTurnStartState = combatState;
            PlayerActionBuffer.LogToDevConsole(
                "[RunReplays] TurnStarted: recorded for EndTurn gate (waiting for TurnEnded too).");
            TryCompleteEndTurnGate();
            return;
        }

        if (_waitingForEffects || _dispatching)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TurnStarted: dispatch chain already active, skipping.");
            return;
        }

        _turnStartRetries = 0;
        ScheduleNextFromQueue("TurnStarted");
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
        PlayerActionBuffer.LogToDevConsole(
            "[RunReplays] TurnEnded: recorded for EndTurn gate (waiting for TurnStarted too).");
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
        PlayerActionBuffer.LogToDevConsole("[RunReplays] EndTurn gate complete (TurnEnded + TurnStarted) — dispatching.");

        // Clear all end-turn state.
        _awaitingEndTurnCompletion = false;
        _dispatching = false;
        _waitingForEffects = false;
        _endTurnConsumed = false;
        _endTurnRetryCount = 0;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;

        if (_postEndTurn_savedTurnStartState != null)
            _currentCombatState = _postEndTurn_savedTurnStartState;
        _postEndTurn_savedTurnStartState = null;

        // Delay before dispatching so the game fully initializes the new turn.
        _turnStartRetries = 0;
        int gen = _battleGeneration;
        NGame.Instance!.GetTree()!.CreateTimer(2.0).Connect(
            "timeout", Callable.From(() =>
            {
                if (_battleGeneration != gen) return;
                if (_dispatching || _waitingForEffects) return;
                ScheduleNextFromQueue("EndTurnGate (post-EndTurn)");
            }));
    }

    private static int _turnStartRetries;
    private const int MaxTurnStartRetries = 100;

    private static void ScheduleNextFromQueue(string caller)
    {
        ReplayEngine.PeekNext(out string? snqNext);
        SelectorStackDebug.Log(
            $"ScheduleNextFromQueue({caller}): nextCmd='{snqNext ?? "(none)"}'" +
            $" dispatching={_dispatching} awaitingEndTurn={_awaitingEndTurnCompletion}" +
            $" turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}");

        if (_cardPlayInFlight)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] {caller}: blocked — card play in flight.");
            return;
        }

        if (_awaitingEndTurnCompletion)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] {caller}: blocked — awaiting end-turn completion.");
            return;
        }

        _dispatching = true;

        if (ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] {caller}: next command is NetDiscardPotion slot={discardSlot}, scheduling TryDiscardPotion.");
            Callable.From(TryDiscardPotion).CallDeferred();
        }
        else if (ReplayEngine.PeekUsePotion(out uint potionIdx, out _, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] {caller}: next command is UsePotion index={potionIdx}, scheduling TryUsePotion.");
            Callable.From(TryUsePotion).CallDeferred();
        }
        else if (ReplayEngine.PeekCardPlay(out uint idx, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] {caller}: next command is CardPlay index={idx}, scheduling TryPlayNextCard.");
            Callable.From(TryPlayNextCard).CallDeferred();
        }
        else if (ReplayEngine.PeekEndTurn())
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] {caller}: next command is EndTurn, scheduling TryEndTurn (0.5s delay).");
            int gen = _battleGeneration;
            NGame.Instance!.GetTree()!.CreateTimer(0.5).Connect(
                "timeout", Callable.From(() => { if (_battleGeneration == gen) TryEndTurn(); }));
        }
        else
        {
            ReplayEngine.PeekNext(out string? next);

            if (_turnStartRetries < MaxTurnStartRetries)
            {
                _turnStartRetries++;
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] {caller}: no combat action yet — '{next ?? "(none)"}', retry {_turnStartRetries}/{MaxTurnStartRetries}.");
                int gen = _battleGeneration;
                NGame.Instance!.GetTree()!.CreateTimer(0.25).Connect(
                    "timeout", Callable.From(() => { if (_battleGeneration == gen) ScheduleNextFromQueue(caller); }));
            }
            else
            {
                _dispatching = false;
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] {caller}: gave up after {MaxTurnStartRetries} retries — '{next ?? "(none)"}'.");
            }
        }
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
        if (!ReplayEngine.IsActive)
            return;

        // While waiting for the end-turn to complete, ignore all actions.
        // Only OnTurnStarted (the next player turn) clears this flag.
        if (_awaitingEndTurnCompletion)
            return;

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
            BattleRewardsReplayPatch.TryResumeRewardsProcessing();
            return;
        }

        // Only chain the next dispatch for actions initiated by our replay.
        // Without this guard, enemy PlayCardActions during the enemy turn
        // trigger DispatchNextCombatAction, which prematurely consumes the
        // next player EndTurn command and stalls the replay.
        if (!_dispatching)
            return;

        if (action is PlayCardAction playCard)
        {
            _cardPlayInFlight = false;
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: PlayCardAction completed ({playCard}).");
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
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: UsePotionAction completed ({usePotion}).");
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
        DispatchNextCombatAction();
    }

    private static void DispatchNextCombatAction()
    {
        ReplayEngine.PeekNext(out string? dncNext);
        SelectorStackDebug.Log(
            $"DispatchNextCombatAction: nextCmd='{dncNext ?? "(none)"}'" +
            $" awaitingEndTurn={_awaitingEndTurnCompletion}" +
            $" turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}");

        if (_cardPlayInFlight)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] DispatchNextCombatAction: blocked — card play in flight.");
            return;
        }

        if (_awaitingEndTurnCompletion)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] DispatchNextCombatAction: blocked — awaiting end-turn completion.");
            return;
        }

        if (ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Dispatch: NetDiscardPotion slot={discardSlot}.");
            TryDiscardPotion();
        }
        else if (ReplayEngine.PeekUsePotion(out uint potionIdx, out _, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Dispatch: UsePotion index={potionIdx}.");
            TryUsePotion();
        }
        else if (ReplayEngine.PeekCardPlay(out uint idx, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Dispatch: CardPlay index={idx}.");
            TryPlayNextCard();
        }
        else if (ReplayEngine.PeekEndTurn())
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] Dispatch: EndTurn.");
            TryEndTurn();
        }
        else
        {
            _dispatching = false;
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] Dispatch: no more combat actions — '{next ?? "(none)"}'. Checking for active rewards screen.");
            BattleRewardsReplayPatch.TryResumeRewardsProcessing();
        }
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private static void TryPlayNextCard()
    {
        // Peek without consuming so that a failed play can be retried next frame.
        if (!ReplayEngine.PeekCardPlay(out uint combatCardIndex, out uint? targetId))
        {
            ReplayEngine.PeekNext(out string? next);
            SelectorStackDebug.Log($"TryPlayNextCard: PeekCardPlay=false, next='{next ?? "(none)"}'");
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: PeekCardPlay returned false, next='{next ?? "(none)"}'.");
            return;
        }

        if (_retryCount == 0)
            SelectorStackDebug.Log($"TryPlayNextCard: index={combatCardIndex} target={targetId?.ToString() ?? "none"}");
        PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: attempting card index={combatCardIndex} targetId={targetId?.ToString() ?? "none"}.");

        // Check if combat is ready to accept commands — the hand must be
        // drawn and the player must exist before we resolve cards.
        if (!IsCombatReady())
        {
            const int maxRetries = 50;
            if (_retryCount >= maxRetries)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] TryPlayNextCard: combat not ready after {maxRetries} retries — giving up.");
                _retryCount = 0;
                return;
            }

            _retryCount++;
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryPlayNextCard: combat not ready, retry #{_retryCount}.");
            int gen = _battleGeneration;
            NGame.Instance!.GetTree()!.CreateTimer(0.25).Connect(
                "timeout", Callable.From(() => { if (_battleGeneration == gen) TryPlayNextCard(); }));
            return;
        }

        CardModel? card;
        try
        {
            card = NetCombatCardDb.Instance.GetCard(combatCardIndex);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: resolved card '{card}' from index {combatCardIndex}.");
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryPlayNextCard: GetCard({combatCardIndex}) threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Creature? target = null;
        if (targetId.HasValue)
        {
            target = _currentCombatState?.GetCreature(targetId);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: resolved target id={targetId} → {(target == null ? "null" : target.ToString())}.");
        }

        bool played = card.TryManualPlay(target);
        PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: TryManualPlay returned {played} (retry #{_retryCount}).");

        if (!played)
        {
            const int maxRetries = 50;
            if (_retryCount >= maxRetries)
            {
                ReplayEngine.PeekNext(out string? giveUpNext);
                SelectorStackDebug.Log(
                    $"TryPlayNextCard: GAVE UP after {maxRetries} retries for card '{card}' index={combatCardIndex}" +
                    $" nextCmd='{giveUpNext ?? "(none)"}'" +
                    $" dispatching={_dispatching} turnStartedSinceEndTurn={_turnStartedSinceLastEndTurn}");
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] TryPlayNextCard: giving up after {maxRetries} retries for card index={combatCardIndex}.");
                _retryCount = 0;
                return;
            }

            _retryCount++;
            int gen = _battleGeneration;
            NGame.Instance!.GetTree()!.CreateTimer(0.25).Connect(
                "timeout", Callable.From(() => { if (_battleGeneration == gen) TryPlayNextCard(); }));
            return;
        }

        // Success: consume the command and log it.
        _retryCount = 0;
        _cardPlayInFlight = true;
        ReplayRunner.ExecuteCardPlay(out _, out _);
        RunOverlay.NotifyCardPlayStarted();
        ReplayEngine.PeekNext(out string? afterPlay);
        SelectorStackDebug.Log($"TryPlayNextCard: SUCCESS played '{card}' index={combatCardIndex}, next='{afterPlay ?? "(none)"}'");
    }

    // ── Potion-discard execution ──────────────────────────────────────────────

    internal static void TryDiscardPotion()
    {
        if (!ReplayRunner.ExecuteNetDiscardPotion(out int slotIndex))
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryDiscardPotion: ExecuteNetDiscardPotion returned false.");
            return;
        }

        Player? player = ResolveLocalPlayer();

        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryDiscardPotion: could not resolve local player.");
            return;
        }

        try
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryDiscardPotion: enqueuing DiscardPotionGameAction slot={slotIndex} for player '{player}'.");
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new DiscardPotionGameAction(player, (uint)slotIndex));
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryDiscardPotion: RequestEnqueue threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Potion-use execution ──────────────────────────────────────────────────

    private static void TryUsePotion()
    {
        if (!ReplayRunner.ExecuteUsePotion(out uint potionIndex, out uint? targetId, out bool inCombat))
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryUsePotion: ExecuteUsePotion returned false.");
            return;
        }

        Player? player = ResolveLocalPlayer();

        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryUsePotion: could not resolve local player.");
            return;
        }

        Creature? target = null;
        if (targetId.HasValue)
        {
            target = _currentCombatState?.GetCreature(targetId);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryUsePotion: resolved target id={targetId} → {(target == null ? "null" : target.ToString())}.");
        }

        // For out-of-combat self-targeting, pass the player's own NetId.
        ulong? targetPlayerId = (!inCombat && target == null) ? player.NetId : (ulong?)null;

        try
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryUsePotion: enqueuing UsePotionAction index={potionIndex} for player '{player}'.");
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new UsePotionAction(player, potionIndex, targetId, targetPlayerId, inCombat));
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryUsePotion: RequestEnqueue threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── End-turn execution ────────────────────────────────────────────────────

    private static int  _endTurnRetryCount;
    private static bool _endTurnConsumed;


    private static void TryEndTurn()
    {
        if (_waitingForEffects)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: effects still in progress, deferring.");
            Callable.From(TryEndTurn).CallDeferred();
            return;
        }

        // Don't issue EndTurn unless we've received a TurnStarted since the
        // last one — prevents consecutive EndTurns without the game advancing.
        if (!_turnStartedSinceLastEndTurn)
        {
            const int maxTurnWaitRetries = 50;
            if (_endTurnRetryCount < maxTurnWaitRetries)
            {
                _endTurnRetryCount++;
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] TryEndTurn: no TurnStarted since last EndTurn — waiting ({_endTurnRetryCount}/{maxTurnWaitRetries}).");
                int gen = _battleGeneration;
                NGame.Instance!.GetTree()!.CreateTimer(0.25).Connect(
                    "timeout", Callable.From(() => { if (_battleGeneration == gen) TryEndTurn(); }));
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: gave up waiting for TurnStarted — aborting.");
                _dispatching = false;
            }
            return;
        }

        // Wait until combat is in progress and a player is available.
        if (!CombatManager.Instance.IsInProgress || ResolveLocalPlayer() == null)
        {
            const int maxReadyRetries = 50;
            if (_endTurnRetryCount < maxReadyRetries)
            {
                _endTurnRetryCount++;
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] TryEndTurn: combat not ready — waiting ({_endTurnRetryCount}/{maxReadyRetries}).");
                int gen = _battleGeneration;
                NGame.Instance!.GetTree()!.CreateTimer(0.25).Connect(
                    "timeout", Callable.From(() => { if (_battleGeneration == gen) TryEndTurn(); }));
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: combat not ready after retries — aborting.");
                _dispatching = false;
            }
            return;
        }

        ReplayEngine.PeekNext(out string? peekCmd);
        SelectorStackDebug.Log(
            $"TryEndTurn: attempting end turn (retry #{_endTurnRetryCount})" +
            $" endTurnConsumed={_endTurnConsumed} nextCmd='{peekCmd ?? "(none)"}'");

        // Only consume the command on the first attempt — retries re-issue
        // PlayerCmd.EndTurn for the same already-consumed command.
        if (!_endTurnConsumed)
        {
            if (!ReplayRunner.ExecuteEndTurn())
            {
                PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: ExecuteEndTurn returned false (next command is not EndTurn).");
                SelectorStackDebug.Log("TryEndTurn: ExecuteEndTurn=false, aborting.");
                return;
            }
            _endTurnConsumed = true;
            ReplayEngine.PeekNext(out string? afterConsume);
            SelectorStackDebug.Log($"TryEndTurn: consumed EndTurn, next is now '{afterConsume ?? "(none)"}'");
        }

        // Player was already verified in the readiness check above.
        Player player = ResolveLocalPlayer()!;

        // Block all further dispatch until both TurnEnded and TurnStarted fire.
        _awaitingEndTurnCompletion = true;
        _turnStartedSinceLastEndTurn = false;
        _postEndTurn_turnEndedReceived = false;
        _postEndTurn_turnStartedReceived = false;
        _postEndTurn_savedTurnStartState = null;

        ReplayEngine.PeekNext(out string? endTurnNext);
        SelectorStackDebug.Log(
            $"TryEndTurn: ISSUING PlayerCmd.EndTurn (endTurnConsumed={_endTurnConsumed}" +
            $" retry={_endTurnRetryCount} next='{endTurnNext ?? "(none)"}')");
        PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryEndTurn: calling PlayerCmd.EndTurn for player '{player}'.");
        PlayerCmd.EndTurn(player, canBackOut: false);
    }
}
