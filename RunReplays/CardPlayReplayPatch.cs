using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

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
        _currentCombatState = combatState;
        _retryCount = 0;

        if (ReplayEngine.PeekCardPlay(out uint idx, out _))
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
        if (action is not PlayCardAction)
            return;

        var playCard = (PlayCardAction)action;
        PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: PlayCardAction completed ({playCard}).");

        if (ReplayEngine.PeekCardPlay(out uint idx, out _))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: next command is CardPlay index={idx}, scheduling TryPlayNextCard.");
            Callable.From(TryPlayNextCard).CallDeferred();
        }
        else if (ReplayEngine.PeekEndTurn())
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] AfterActionExecuted: next command is EndTurn, scheduling TryEndTurn.");
            Callable.From(TryEndTurn).CallDeferred();
        }
        else
        {
            ReplayEngine.PeekNext(out string? next);
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] AfterActionExecuted: no more card plays or end turn — '{next ?? "(none)"}'.");
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
