using System;
using System.Collections.Generic;
using Godot;

namespace RunReplays;

/// <summary>
/// Tracks game readiness state for the replay system.  Harmony postfixes call
/// <see cref="SignalReady"/> to indicate that the game is ready to accept a
/// particular category of action.  The dispatcher checks this state before
/// executing queued commands.
/// </summary>
public static class ReplayState
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

    /// <summary>Current readiness state (bitmask of what the game is ready for).</summary>
    public static ReadyState Ready => _ready;

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
        if (ReplayDispatcher.MapMoveInFlight && (state & RoomReadyMask) != 0)
            ReplayDispatcher.MapMoveInFlight = false;

        ReplayDispatcher.TryDispatch();
    }

    /// <summary>Clears a readiness flag (e.g. when a screen closes).</summary>
    public static void ClearReady(ReadyState state)
    {
        _ready &= ~state;
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
        _ready = ReadyState.None;
        DrainScreenCleanup();
    }
}
