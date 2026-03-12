using System;
using System.Linq;
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
using MegaCrit.Sts2.Core.Runs;

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
    private static CombatState?         _currentCombatState;
    private static int                  _retryCount;

    [HarmonyPostfix]
    public static void Postfix(ActionExecutor __instance)
    {
        // Unsubscribe any handler from the previous run before subscribing again.
        if (_turnStartedHandler != null)
            CombatManager.Instance.TurnStarted -= _turnStartedHandler;

        _turnStartedHandler = OnTurnStarted;
        CombatManager.Instance.TurnStarted += _turnStartedHandler;

        __instance.AfterActionExecuted += OnAfterActionExecuted;
    }

    // ── Turn-start trigger ────────────────────────────────────────────────────

    private static void OnTurnStarted(CombatState combatState)
    {
        if (!ReplayEngine.IsActive)
            return;

        _currentCombatState = combatState;
        _retryCount = 0;

        if (ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TurnStarted: next command is NetDiscardPotion slot={discardSlot}, scheduling TryDiscardPotion.");
            Callable.From(TryDiscardPotion).CallDeferred();
        }
        else if (ReplayEngine.PeekUsePotion(out uint potionIdx, out _, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TurnStarted: next command is UsePotion index={potionIdx}, scheduling TryUsePotion.");
            Callable.From(TryUsePotion).CallDeferred();
        }
        else if (ReplayEngine.PeekCardPlay(out uint idx, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TurnStarted: next command is CardPlay index={idx}, scheduling TryPlayNextCard.");
            Callable.From(TryPlayNextCard).CallDeferred();
        }
        else if (ReplayEngine.PeekEndTurn())
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TurnStarted: next command is EndTurn, scheduling TryEndTurn.");
            Callable.From(TryEndTurn).CallDeferred();
        }
        else
        {
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TurnStarted: next command is not a card play or end turn — '{next ?? "(none)"}'.");
        }
    }

    // ── Post-card-play chain trigger ──────────────────────────────────────────

    private static void OnAfterActionExecuted(GameAction action)
    {
        if (!ReplayEngine.IsActive)
            return;

        if (action is PlayCardAction playCard)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: PlayCardAction completed ({playCard}).");
            ScheduleNextCombatAction();
        }
        else if (action is DiscardPotionGameAction discardPotion)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: DiscardPotionGameAction completed ({discardPotion}).");
            ScheduleNextCombatAction();
        }
        else if (action is UsePotionAction usePotion)
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: UsePotionAction completed ({usePotion}).");
            ScheduleNextCombatAction();
        }
    }

    private static void ScheduleNextCombatAction()
    {
        if (ReplayEngine.PeekNetDiscardPotion(out int discardSlot))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] ScheduleNext: NetDiscardPotion slot={discardSlot}, scheduling TryDiscardPotion.");
            Callable.From(TryDiscardPotion).CallDeferred();
        }
        else if (ReplayEngine.PeekUsePotion(out uint potionIdx, out _, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] ScheduleNext: UsePotion index={potionIdx}, scheduling TryUsePotion.");
            Callable.From(TryUsePotion).CallDeferred();
        }
        else if (ReplayEngine.PeekCardPlay(out uint idx, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] ScheduleNext: CardPlay index={idx}, scheduling TryPlayNextCard.");
            Callable.From(TryPlayNextCard).CallDeferred();
        }
        else if (ReplayEngine.PeekEndTurn())
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] ScheduleNext: EndTurn, scheduling TryEndTurn.");
            Callable.From(TryEndTurn).CallDeferred();
        }
        else
        {
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] ScheduleNext: no more combat actions — '{next ?? "(none)"}'. Checking for active rewards screen.");
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
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: PeekCardPlay returned false, next='{next ?? "(none)"}'.");
            return;
        }

        PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryPlayNextCard: attempting card index={combatCardIndex} targetId={targetId?.ToString() ?? "none"}.");

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
            const int maxRetries = 20;
            if (_retryCount >= maxRetries)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[RunReplays] TryPlayNextCard: giving up after {maxRetries} retries for card index={combatCardIndex}.");
                _retryCount = 0;
                return;
            }

            _retryCount++;
            Callable.From(TryPlayNextCard).CallDeferred();
            return;
        }

        // Success: consume the command and log it.
        _retryCount = 0;
        ReplayRunner.ExecuteCardPlay(out _, out _);
    }

    // ── Potion-discard execution ──────────────────────────────────────────────

    internal static void TryDiscardPotion()
    {
        if (!ReplayRunner.ExecuteNetDiscardPotion(out int slotIndex))
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryDiscardPotion: ExecuteNetDiscardPotion returned false.");
            return;
        }

        Player? player;
        try
        {
            player = LocalContext.GetMe(_currentCombatState);
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryDiscardPotion: LocalContext.GetMe threw {ex.GetType().Name}: {ex.Message}");
            player = null;
        }

        // Fallback: in single-player there is exactly one player in the combat state.
        player ??= _currentCombatState?.Players.FirstOrDefault();

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

        Player? player;
        try
        {
            player = LocalContext.GetMe(_currentCombatState);
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryUsePotion: LocalContext.GetMe threw {ex.GetType().Name}: {ex.Message}");
            player = null;
        }

        player ??= _currentCombatState?.Players.FirstOrDefault();

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

    private static void TryEndTurn()
    {
        PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: attempting to end player turn.");

        if (!ReplayRunner.ExecuteEndTurn())
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: ExecuteEndTurn returned false (next command is not EndTurn).");
            return;
        }

        var player = LocalContext.GetMe(_currentCombatState);
        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] TryEndTurn: could not resolve local player.");
            return;
        }

        PlayerActionBuffer.LogToDevConsole($"[RunReplays] TryEndTurn: calling PlayerCmd.EndTurn for player '{player}'.");
        PlayerCmd.EndTurn(player, canBackOut: false);
    }
}
