using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays;

/// <summary>
/// Drives automatic card play during combat replays.
///
/// Two triggers are used:
///
///   1. CombatManager.TurnStarted — fires at the start of every player turn,
///      which is when the hand first becomes playable.  Subscribed once per run
///      from the ActionExecutor constructor patch (same lifecycle hook used by
///      PlayerActionBuffer).  A static field prevents handler accumulation
///      across multiple runs.
///
///   2. AfterActionExecuted for PlayCardAction — chains the next card play
///      immediately after the previous one is fully executed, covering the case
///      where multiple cards must be played in sequence during a single turn.
///
/// Actual play is delegated to CardModel.TryManualPlay(target), which performs
/// its own CanPlayTargeting check before enqueuing the PlayCardAction.
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

        if (!ReplayEngine.PeekCardPlay(out _, out _))
            return;

        Callable.From(TryPlayNextCard).CallDeferred();
    }

    // ── Post-card-play chain trigger ──────────────────────────────────────────

    private static void OnAfterActionExecuted(GameAction action)
    {
        if (action is not PlayCardAction)
            return;

        if (!ReplayEngine.PeekCardPlay(out _, out _))
            return;

        Callable.From(TryPlayNextCard).CallDeferred();
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
}
