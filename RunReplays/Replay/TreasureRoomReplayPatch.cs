using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Three Harmony postfixes that together automate treasure chest replay:
///
/// 1. NTreasureRoom._Ready — when the next command is TakeChestRelic, stores
///    the room instance, consumes the command, and emits the chest button's
///    Released signal on the next frame to trigger OpenChest().
///
/// 2. NTreasureRoomRelicCollection.InitializeRelics — called from within
///    OpenChest() after the relic holders are populated.  The next command at
///    this point is NetPickRelicAction (auto-recorded by AfterActionExecuted).
///    Its index is consumed and PickRelicLocally(index) is called deferred,
///    enqueuing a PickRelicAction whose execution fires RelicsAwarded →
///    AnimateRelicAwards → _relicPickingTaskCompletionSource.SetResult().
///
/// 3. NMapScreen.SetTravelEnabled (second postfix alongside MapChoiceReplayPatch)
///    — OpenChest() calls SetTravelEnabled(true) then _proceedButton.Enable()
///    synchronously.  By the time the deferred proceed-button click runs (next
///    frame), the button is already enabled.  Clicking it calls
///    ProceedFromTerminalRewardsScreen() → NMapScreen.Open() so the auto
///    map-node selection takes visible effect.
/// </summary>
public static class TreasureRoomReplayPatch
{
    // Set when _Ready fires with a pending TakeChestRelic; cleared after the
    // proceed button is clicked so later SetTravelEnabled calls don't re-fire.
    internal static NTreasureRoom? ActiveRoom;

    /// <summary>Called by ReplayDispatcher to trigger treasure room actions.</summary>
    internal static void DispatchFromEngine()
    {
        if (ReplayEngine.PeekTakeChestRelic(out _))
        {
            if (ActiveRoom != null && ActiveRoom.IsInsideTree())
                Callable.From(() => ChestOpenReplayPatch.ClickChest(ActiveRoom)).CallDeferred();
            return;
        }

        if (ReplayEngine.PeekNetPickRelicAction(out int relicIndex))
        {
            ReplayRunner.ExecuteNetPickRelicAction(out _);
            var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            PlayerActionBuffer.LogDispatcher($"[Treasure] PickRelicLocally({relicIndex})");
            Callable.From(() => sync.PickRelicLocally(relicIndex)).CallDeferred();
        }
    }

    [HarmonyPatch(typeof(NTreasureRoom), "_Ready")]
    public static class ChestOpenReplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(NTreasureRoom __instance)
        {
            RngCheckpointLogger.Log("Treasure (NTreasureRoom._Ready)");

            if (!ReplayEngine.IsActive)
                return;
            ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Treasure);

            if (!ReplayEngine.PeekTakeChestRelic(out _))
                return;

            ActiveRoom = __instance;
            PlayerActionBuffer.LogToDevConsole(
                "[TreasureRoomReplayPatch] NTreasureRoom ready — stored room reference.");
            ReplayDispatcher.DispatchNow();
        }

        internal static void ClickChest(NTreasureRoom room)
        {
            if (!ReplayEngine.IsActive)
                return;

            NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
            if (chest == null)
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[TreasureRoomReplayPatch] Chest button node not found.");
                return;
            }

            // Consume the TakeChestRelic command — its purpose was to open the
            // chest; the relic pick is driven by the subsequent NetPickRelicAction.
            ReplayRunner.ExecuteTakeChestRelic(out string relicTitle);
            PlayerActionBuffer.LogToDevConsole(
                $"[TreasureRoomReplayPatch] Opening chest (expected relic '{relicTitle}').");
            chest.EmitSignal(NClickableControl.SignalName.Released, chest);
        }
    }

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.InitializeRelics))]
    public static class RelicPickReplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!ReplayEngine.IsActive)
                return;

            ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Treasure);

            if (!ReplayEngine.PeekNetPickRelicAction(out int relicIndex))
                return;

            PlayerActionBuffer.LogToDevConsole(
                $"[TreasureRoomReplayPatch] Relic pick pending (index={relicIndex}) — stored for dispatcher.");
            ReplayDispatcher.DispatchNow();
        }
    }

    /// <summary>
    /// Second postfix on NMapScreen.SetTravelEnabled (MapChoiceReplayPatch is the other).
    /// OpenChest() calls SetTravelEnabled(true) and _proceedButton.Enable() in the same
    /// synchronous continuation, so by the time the deferred callback runs the proceed
    /// button is already enabled.
    /// </summary>
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled))]
    public static class TreasureRoomProceedPatch
    {
        [HarmonyPostfix]
        public static void Postfix(bool enabled)
        {
            if (!enabled || ActiveRoom == null || !ActiveRoom.IsInsideTree())
                return;

            NTreasureRoom room = ActiveRoom;
            ActiveRoom = null;

            PlayerActionBuffer.LogToDevConsole(
                "[TreasureRoomReplayPatch] SetTravelEnabled in treasure room — deferring proceed click.");
            Callable.From(() => ClickProceed(room)).CallDeferred();
        }

        private static void ClickProceed(NTreasureRoom room)
        {
            if (!ReplayEngine.IsActive || !room.IsInsideTree())
                return;

            NProceedButton button = room.ProceedButton;
            PlayerActionBuffer.LogDispatcher(
                "[Treasure] Clicking proceed button.");
            button.EmitSignal(NClickableControl.SignalName.Released, button);
            // The proceed opens the map — signal readiness for the next map move.
            ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Map);
        }
    }
}
