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

        if (ReplayEngine.PeekCardPlay(out _, out _))
            Callable.From(TryPlayNextCard).CallDeferred();
        else if (ReplayEngine.PeekEndTurn())
            Callable.From(TryEndTurn).CallDeferred();
    }

    // ── Post-card-play chain trigger ──────────────────────────────────────────

    private static void OnAfterActionExecuted(GameAction action)
    {
        if (action is not PlayCardAction)
            return;

        if (ReplayEngine.PeekCardPlay(out _, out _))
            Callable.From(TryPlayNextCard).CallDeferred();
        else if (ReplayEngine.PeekEndTurn())
            Callable.From(TryEndTurn).CallDeferred();
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private static void TryPlayNextCard()
    {
        if (!ReplayRunner.ExecuteCardPlay(out uint combatCardIndex, out uint? targetId))
            return;

        CardModel? card;
        try
        {
            card = NetCombatCardDb.Instance.GetCard(combatCardIndex);
        }
        catch
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] CardPlay: card index {combatCardIndex} not found in NetCombatCardDb.");
            return;
        }

        Creature? target = null;
        if (targetId.HasValue)
            target = _currentCombatState?.GetCreature(targetId);

        if (!card.TryManualPlay(target))
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] CardPlay: TryManualPlay failed for card index {combatCardIndex}.");
    }

    // ── End-turn execution ────────────────────────────────────────────────────

    private static void TryEndTurn()
    {
        if (!ReplayRunner.ExecuteEndTurn())
            return;

        var player = LocalContext.GetMe(_currentCombatState);
        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] EndTurn: could not resolve local player.");
            return;
        }

        PlayerCmd.EndTurn(player, canBackOut: false);
    }
}
