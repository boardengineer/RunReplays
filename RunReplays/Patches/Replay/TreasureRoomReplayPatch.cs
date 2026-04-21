using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace RunReplays.Patches.Replay;


public static class TreasureRoomReplayPatch
{
    internal static NTreasureRoom? ActiveRoom;

    [HarmonyPatch(typeof(NTreasureRoom), "_Ready")]
    public static class ChestOpenReplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(NTreasureRoom __instance)
        {
            if (!ReplayEngine.IsActive)
                return;

            ActiveRoom = __instance;
            ReplayDispatcher.TryDispatch();
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

            ReplayDispatcher.TryDispatch();
        }
    }
}