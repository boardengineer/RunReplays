using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays;

/// <summary>
/// Tracks in-flight operation state for the replay system.  The dispatcher
/// checks this state before executing queued commands.
/// </summary>
public static class ReplayState
{
    /// <summary>
    /// Tracks whether a card play is in flight.  Set by the combat patch.
    /// Does NOT trigger immediate dispatch on clear — effects need to settle
    /// first.  <see cref="ReplayDispatcher.NotifyEffectsSettled"/> triggers dispatch after.
    /// </summary>
    /// <summary>
    /// The currently active rewards screen, set by the BattleRewardsReplayPatch postfix.
    /// </summary>
    public static NRewardsScreen? ActiveRewardsScreen { get; set; }

    /// <summary>
    /// The card reward selection screen, set by CardRewardReplayPatch postfix.
    /// </summary>
    public static NCardRewardSelectionScreen? CardRewardSelectionScreen { get; set; }

    /// <summary>
    /// The active event synchronizer, set by EventOptionReplayPatch postfix.
    /// </summary>
    public static EventSynchronizer? ActiveEventSynchronizer { get; set; }

    /// <summary>
    /// The active fake merchant instance, set by FakeMerchantReplayPatch postfix.
    /// </summary>
    public static NFakeMerchant? FakeMerchantInstance { get; set; }

    /// <summary>
    /// Tracks whether a card play is in flight.  Set by the combat patch.
    /// Does NOT trigger immediate dispatch on clear — effects need to settle
    /// first.  <see cref="ReplayDispatcher.NotifyEffectsSettled"/> triggers dispatch after.
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

    /// <summary>Force-clears the action-in-flight flag (used by the watchdog).</summary>
    internal static void ClearActionInFlight() => _actionInFlight = false;

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
    }

    private static void OnAfterAction(GameAction action)
    {
        if (!ReplayEngine.IsActive) return;
        _actionInFlight = false;
        ReplayDispatcher.TryDispatch();
    }

    /// <summary>
    /// Screens that have been resolved but should be freed at a safe lifecycle
    /// point (room exit, turn start) rather than immediately.
    /// </summary>
    private static readonly List<Node> _pendingScreenCleanup = new();

    /// <summary>Enqueue a screen node for deferred cleanup.</summary>
    internal static void EnqueueScreenCleanup(Node screen)
    {
        _pendingScreenCleanup.Add(screen);
    }

    /// <summary>
    /// Frees all screens queued for cleanup.  Called on room exit and turn start.
    /// </summary>
    internal static void DrainScreenCleanup()
    {
        foreach (var screen in _pendingScreenCleanup)
        {
            if (GodotObject.IsInstanceValid(screen))
                screen.QueueFree();
        }
        _pendingScreenCleanup.Clear();
    }

    /// <summary>Resets all replay state.  Called on replay start and clear.</summary>
    internal static void Reset()
    {
        ActiveRewardsScreen = null;
        CardRewardSelectionScreen = null;
        ActiveEventSynchronizer = null;
        FakeMerchantInstance = null;
        CardPlayInFlight = false;
        PotionInFlight = false;
        _actionInFlight = false;
        DrainScreenCleanup();
    }
}
